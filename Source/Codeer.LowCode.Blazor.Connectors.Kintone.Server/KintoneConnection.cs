using Codeer.LowCode.Blazor.SystemSettings;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// kintone への接続 (REST API)。HttpClient とアプリの定義を持つので、アプリの中で使い回す
    /// (<see cref="FromDataSources"/> は同じ設定に同じ接続を返す)。
    /// </summary>
    public sealed class KintoneConnection
    {
        static readonly ConcurrentDictionary<string, KintoneConnection> _shared = new();

        /// <summary>レコードの読み出し 1 回の上限 (kintone の limit の上限)。</summary>
        internal const int PageSize = 500;
        /// <summary>offset の上限 (kintone の制限)。</summary>
        internal const int MaxOffset = 10000;

        readonly HttpClient _http;
        readonly HttpMessageHandler? _handler;
        readonly SemaphoreSlim _definitionLock = new(1, 1);
        readonly Dictionary<string, KintoneTableDefinition> _tables = new(StringComparer.Ordinal);

        public KintoneConnectionSettings Settings { get; }

        //テストで待ち時間を無くすための差し替え口
        internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

        public KintoneConnection(KintoneConnectionSettings settings)
            : this(settings, null)
        {
        }

        /// <param name="handler">HTTP の送信を差し替えるとき (テスト)。null なら既定。</param>
        internal KintoneConnection(KintoneConnectionSettings settings, HttpMessageHandler? handler)
        {
            settings.Validate();
            Settings = settings;
            _handler = handler;
            //リダイレクトには従わない (書き込みが GET に化ける・トークンが別のホストへ送られるため)。Cookie は持たない (要求ごとに認証する)。接続は数分で張り直す
            _http = handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                : new HttpClient(handler, false);
            _http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        }

        /// <summary>
        /// データソースの中から kintone 用 (ConnectionString が Provider=Kintone) のものを接続にする。同じ設定には同じ接続を返す。
        /// </summary>
        public static List<KintoneConnection> FromDataSources(IEnumerable<DataSource> dataSources)
        {
            var list = new List<KintoneConnection>();
            foreach (var dataSource in dataSources)
            {
                var settings = KintoneConnectionSettings.FromDataSource(dataSource);
                if (settings == null) continue;
                list.Add(_shared.GetOrAdd(settings.GetIdentityKey(), _ => new KintoneConnection(settings)));
            }
            return list;
        }

        // ---------------------------------------------------------------- 表の定義

        KintoneAppSettings GetApp(string table)
            => Settings.Apps.FirstOrDefault(e => e.Name == table)
               ?? throw new KintoneException($"kintone data source '{Settings.Name}': table '{table}' is not in Apps of the connection string.", KintoneErrorKind.NotFound);

        /// <summary>設定のアプリ全部の定義 (設定の順)。</summary>
        internal async Task<List<KintoneTableDefinition>> GetTableDefinitionsAsync()
        {
            var list = new List<KintoneTableDefinition>();
            foreach (var app in Settings.Apps) list.Add(await GetTableDefinitionAsync(app.Name));
            return list;
        }

        /// <summary>
        /// アプリの定義。フォームの設定はアプリごとに 1 回だけ読んで接続が生きている間持つ (kintone のフォームを変えたらアプリを再起動する)。
        /// </summary>
        internal async Task<KintoneTableDefinition> GetTableDefinitionAsync(string table)
        {
            var app = GetApp(table);
            lock (_tables)
            {
                if (_tables.TryGetValue(table, out var cached)) return cached;
            }
            await _definitionLock.WaitAsync();
            try
            {
                lock (_tables)
                {
                    if (_tables.TryGetValue(table, out var loaded)) return loaded;
                }
                var definition = await ReadFormAsync(app);
                lock (_tables) _tables[table] = definition;
                return definition;
            }
            finally
            {
                _definitionLock.Release();
            }
        }

        /// <summary>持っている定義を捨てて、フォームの設定を読み直す。</summary>
        internal async Task<List<KintoneTableDefinition>> ReloadTableDefinitionsAsync()
        {
            await _definitionLock.WaitAsync();
            try
            {
                var list = new List<KintoneTableDefinition>();
                foreach (var app in Settings.Apps) list.Add(await ReadFormAsync(app));
                lock (_tables)
                {
                    _tables.Clear();
                    foreach (var definition in list) _tables[definition.Name] = definition;
                }
                return list;
            }
            finally
            {
                _definitionLock.Release();
            }
        }

        //運用環境のフォームの設定 (GET app/form/fields.json?app=)
        async Task<KintoneTableDefinition> ReadFormAsync(KintoneAppSettings app)
        {
            var url = Settings.ApiUrl + "/app/form/fields.json?app=" + app.AppId.ToString(CultureInfo.InvariantCulture);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), true);
            using var json = ParseJson(await response.Content.ReadAsStringAsync(), response.StatusCode);
            return KintoneTableDefinition.ParseFields(app.Name, json.RootElement);
        }

        // ---------------------------------------------------------------- 利用者のアカウントの確認 (ログイン)

        /// <summary>
        /// 利用者の kintone のログイン名とパスワードで kintone に入れるかを確かめる (アプリのログインに使う)。失敗しても例外を投げずに結果で返す。
        /// 利用者のパスワード認証で「アプリの一覧を 1 件」読む要求を 1 回送る (やり直さない)。
        /// パスワードはどこにも保存しない (結果・メッセージ・例外に入らない)。連携用の認証・接続 (HttpClient・定義) には触らない。
        /// 2 要素認証・SAML 認証だけの利用者はパスワード認証を使えない (kintone の仕様) ので、この確認は通らない。
        /// </summary>
        /// <param name="loginName">kintone のログイン名。空・: を含むものは送らずに InvalidCredentials。</param>
        /// <param name="password">パスワード。空なら送らずに InvalidCredentials。</param>
        public async Task<KintoneAccountResult> VerifyAccountAsync(string loginName, string password)
        {
            if (string.IsNullOrEmpty(loginName) || loginName.Contains(':') || string.IsNullOrEmpty(password))
                return KintoneAccountResult.Invalid("The login name or password is empty or invalid.");
            var account = new AccountCredentials(loginName, password);
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Settings.ApiUrl + "/apps.json?limit=1"), true, account);
                //2xx でも kintone の応答 (apps の配列) でなければ通さない (途中の機器のログイン画面などを成功にしない)
                using var json = ParseJson(await response.Content.ReadAsStringAsync(), response.StatusCode);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                    return KintoneAccountResult.Fail("Could not check the account with kintone: the response is not the list of apps.");
                return KintoneAccountResult.Success(loginName);
            }
            catch (KintoneException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
            {
                return KintoneAccountResult.Invalid("kintone rejected the login name or password.");
            }
            catch (Exception e)
            {
                //例外のメッセージ (KintoneException 以外は型名だけ。ホスト名などを出さない)
                return KintoneAccountResult.Fail("Could not check the account with kintone: " + (e is KintoneException ? e.Message : e.GetType().Name));
            }
        }

        // ---------------------------------------------------------------- 読み出し

        internal class RecordPage
        {
            /// <summary>レコード (records の要素。フィールドコード → {type, value})。</summary>
            public List<JsonElement> Records { get; } = new();
            /// <summary>条件に合うレコードの数 (totalCount)。要求していなければ null。</summary>
            public long? TotalCount { get; set; }
        }

        /// <summary>
        /// レコードを 1 回読む (records.json)。query は条件・並び・limit・offset を書いた kintone のクエリ。fields が空なら全部のフィールド。
        /// 長い条件でも送れるよう、POST + X-HTTP-Method-Override: GET で本文に書く。
        /// </summary>
        internal async Task<RecordPage> ReadRecordsAsync(string table, IReadOnlyCollection<string> fields, string query, bool totalCount)
        {
            var body = new JsonObject { ["app"] = GetApp(table).AppId, ["query"] = query };
            if (fields.Count != 0) body["fields"] = new JsonArray(fields.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
            if (totalCount) body["totalCount"] = true;
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());

            using var response = await SendAsync(() =>
            {
                var request = CreateJsonRequest(HttpMethod.Post, Settings.ApiUrl + "/records.json", bytes);
                request.Headers.TryAddWithoutValidation("X-HTTP-Method-Override", "GET");
                return request;
            }, true);
            using var json = ParseJson(await response.Content.ReadAsStringAsync(), response.StatusCode);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                throw new KintoneException("kintone: the response has no 'records'.", KintoneErrorKind.Unknown, response.StatusCode, string.Empty);
            var page = new RecordPage();
            foreach (var record in records.EnumerateArray()) if (record.ValueKind == JsonValueKind.Object) page.Records.Add(record.Clone());
            if (totalCount)
            {
                //件数は文字で返る。読めない・無い件数を 0 にしない
                if (!root.TryGetProperty("totalCount", out var count) || !TryGetLong(count, out var n) || n < 0)
                    throw new KintoneException("kintone: the count of records (totalCount) was not returned.", KintoneErrorKind.Unknown, response.StatusCode, string.Empty);
                page.TotalCount = n;
            }
            return page;
        }

        static JsonDocument ParseJson(string body, HttpStatusCode status)
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new KintoneException("kintone: the response is not valid JSON.", KintoneErrorKind.Unknown, status, string.Empty, e);
            }
        }

        static bool TryGetLong(JsonElement element, out long value)
        {
            if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out value);
            value = 0;
            return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        // ---------------------------------------------------------------- 書き込み

        /// <summary>作成の応答 (レコード ID と版)。</summary>
        internal readonly record struct WriteResult(string Id, string Revision);

        /// <summary>レコードを作る (POST record.json)。再送しない。</summary>
        internal async Task<WriteResult> CreateAsync(string table, JsonObject record)
        {
            var body = new JsonObject { ["app"] = GetApp(table).AppId, ["record"] = record };
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            using var response = await SendAsync(() => CreateJsonRequest(HttpMethod.Post, Settings.ApiUrl + "/record.json", bytes), false);
            var text = await response.Content.ReadAsStringAsync();
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id) && TryGetLong(id, out var n))
                {
                    var revision = json.RootElement.TryGetProperty("revision", out var r) && TryGetLong(r, out var rn) ? rn.ToString(CultureInfo.InvariantCulture) : string.Empty;
                    return new WriteResult(n.ToString(CultureInfo.InvariantCulture), revision);
                }
            }
            catch (JsonException)
            {
            }
            throw new KintoneException($"kintone created a record in '{table}' but did not return its id. Check the record in kintone before retrying.",
                KintoneErrorKind.WriteResultUnknown, response.StatusCode, string.Empty);
        }

        /// <summary>
        /// レコードを更新する (PUT record.json)。対象は id か、重複禁止のフィールドの値 (updateKey)。revision を渡すと kintone がその版のときだけ更新する。
        /// 対象が無い・版が違う (ほかの人が先に更新した) なら false。
        /// </summary>
        internal async Task<bool> UpdateAsync(string table, string? id, (string Field, JsonNode? Value)? updateKey, string? revision, JsonObject record)
        {
            var body = new JsonObject { ["app"] = GetApp(table).AppId };
            if (updateKey != null) body["updateKey"] = new JsonObject { ["field"] = updateKey.Value.Field, ["value"] = updateKey.Value.Value };
            else body["id"] = id;
            if (!string.IsNullOrEmpty(revision)) body["revision"] = revision;
            body["record"] = record;
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            try
            {
                using var response = await SendAsync(() => CreateJsonRequest(HttpMethod.Put, Settings.ApiUrl + "/record.json", bytes), false);
                return true;
            }
            catch (KintoneException e) when (IsMissingOrConflict(e))
            {
                return false;
            }
        }

        /// <summary>レコードを削除する (DELETE records.json)。revision を渡すと kintone がその版のときだけ削除する。対象が無い・版が違うなら false。</summary>
        internal async Task<bool> DeleteAsync(string table, string id, string? revision)
        {
            var url = Settings.ApiUrl + "/records.json?app=" + GetApp(table).AppId.ToString(CultureInfo.InvariantCulture) + "&ids%5B0%5D=" + Uri.EscapeDataString(id);
            if (!string.IsNullOrEmpty(revision)) url += "&revisions%5B0%5D=" + Uri.EscapeDataString(revision);
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), false);
                return true;
            }
            catch (KintoneException e) when (IsMissingOrConflict(e))
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 添付ファイル

        /// <summary>
        /// ファイルを kintone の一時保管領域にアップロードする (POST file.json・multipart の file)。戻り値はアップロードの fileKey (レコードの値に入れて添付する)。
        /// 添付されなかったファイルは kintone が 3 日で消すので、通信の失敗はやり直す (同じファイルが一時保管領域に 2 つ残っても害は無い)。
        /// </summary>
        internal async Task<string> UploadFileAsync(string name, string contentType, byte[] content)
        {
            using var response = await SendAsync(() =>
            {
                var file = new ByteArrayContent(content);
                file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue(KintoneAttachments.DefaultContentType);
                //ファイル名は UTF-8 のまま filename="..." に書く (kintone の資料: 日本語のファイル名は UTF-8 で送る)
                file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{EscapeQuoted(name)}\"");
                //MultipartFormDataContent はファイル名を =?utf-8?...?= の形に変えることがあるので、multipart/form-data の MultipartContent で組む
                var form = new MultipartContent("form-data") { HeaderEncodingSelector = (_, _) => Encoding.UTF8 };
                form.Add(file);
                return new HttpRequestMessage(HttpMethod.Post, Settings.ApiUrl + "/file.json") { Content = form };
            }, true);
            using var json = ParseJson(await response.Content.ReadAsStringAsync(), response.StatusCode);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("fileKey", out var key) && key.ValueKind == JsonValueKind.String &&
                KintoneAttachments.IsFileKey(key.GetString()))
                return key.GetString()!;
            throw new KintoneException("kintone: the response of the file upload has no 'fileKey'.", KintoneErrorKind.Unknown, response.StatusCode, string.Empty);
        }

        //"..." の中に書く文字 (" と \ を \ で、改行などの制御文字は _ にする)
        static string EscapeQuoted(string text)
            => string.Concat(text.Select(c => c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when char.IsControl(c) => "_",
                _ => c.ToString(),
            }));

        /// <summary>
        /// 添付ファイルの中身を読む (GET file.json?fileKey=)。fileKey はレコードの値で kintone が返したもの (クライアントの文字をそのまま渡さない)。
        /// 呼ぶ前にレコードとフィールドの添付であることを確かめておく (<see cref="KintoneDbAccessor.GetAttachmentAsync"/>)。
        /// </summary>
        internal async Task<byte[]> DownloadFileAsync(string fileKey)
        {
            if (!KintoneAttachments.IsFileKey(fileKey)) throw new KintoneException("The fileKey of the attachment is invalid.", KintoneErrorKind.InvalidRequest);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Settings.ApiUrl + "/file.json?fileKey=" + Uri.EscapeDataString(fileKey)), true);
            return await response.Content.ReadAsByteArrayAsync();
        }

        //対象のレコードが無い (GAIA_RE01)・版が違う (GAIA_CO02)。アプリが無い・権限が無いなどの 404 / 409 は含めない
        static bool IsMissingOrConflict(KintoneException e) => e.ErrorCode is "GAIA_RE01" or "GAIA_CO02";

        static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, byte[] json)
        {
            var request = new HttpRequestMessage(method, url);
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return request;
        }

        // ---------------------------------------------------------------- 送信

        const int MaxAttempts = 4;

        /// <summary>
        /// 認証を付けて送る。読み出し (isRead) は一時的な失敗 (429・502・503・504・通信の失敗) を 3 回までやり直す。
        /// 書き込みはやり直さず、送ったあと応答を受け取れなかった・5xx・200 以外の 2xx が返ったら「結果不明」にする (同じ作成をもう一度送らない)。
        /// リダイレクト (3xx) には従わずエラーにする (転送先では処理されていない)。
        /// </summary>
        /// <param name="account">連携用の認証でなく、利用者のログイン名とパスワードで送るとき (ログインの確認)。null なら接続の認証。</param>
        async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool isRead, AccountCredentials? account = null)
        {
            //利用者のアカウントの要求は別の HttpClient で送る。やり直さない (1 回のログインで 1 回だけ送る)
            var http = account == null ? _http : GetAccountHttp();
            var retry = isRead && account == null;
            for (var attempt = 1; ; attempt++)
            {
                HttpResponseMessage response;
                using (var request = createRequest())
                {
                    AddAuthentication(request, account);
                    try
                    {
                        response = await http.SendAsync(request);
                    }
                    catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                    {
                        if (!isRead)
                            throw new KintoneException("The request was sent to kintone but no response was received. Whether it was processed is unknown. Check the record in kintone before retrying.", KintoneErrorKind.WriteResultUnknown, e);
                        if (retry && attempt < MaxAttempts)
                        {
                            await Delay(GetBackoff(attempt, null));
                            continue;
                        }
                        //例外のメッセージにはホスト名が入ることがあるので、詳細は InnerException だけに残す
                        throw new KintoneException("Could not reach kintone (network error or timeout).", KintoneErrorKind.ServiceUnavailable, e);
                    }
                }

                var status = (int)response.StatusCode;
                if (300 <= status && status < 400)
                {
                    response.Dispose();
                    throw new KintoneException($"kintone: the server returned a redirect (HTTP {status}). Redirects are not followed and the request was not processed. Check that Host is the URL of kintone (https://<subdomain>.cybozu.com).",
                        KintoneErrorKind.InvalidRequest, response.StatusCode, string.Empty);
                }

                if (response.IsSuccessStatusCode)
                {
                    if (isRead || response.StatusCode == HttpStatusCode.OK) return response;
                    response.Dispose();
                    throw new KintoneException($"kintone returned an unexpected status (HTTP {status}) for the write. Whether it was processed is unknown. Check the record in kintone before retrying.",
                        KintoneErrorKind.WriteResultUnknown, response.StatusCode, string.Empty);
                }

                using (response)
                {
                    var isTemporary = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                    if (retry && isTemporary && attempt < MaxAttempts)
                    {
                        await Delay(GetBackoff(attempt, response.Headers.RetryAfter?.Delta));
                        continue;
                    }
                    var exception = await CreateExceptionAsync(response);
                    //kintone のエラーコードが付いた応答は「処理されなかった」と分かる (kintone は業務のエラーを 520 で返すことがある)。コードの無い 5xx だけ結果不明
                    if (!isRead && 500 <= (int)response.StatusCode && string.IsNullOrEmpty(exception.ErrorCode))
                        throw new KintoneException(exception.Message + " Whether the write was processed is unknown. Check the record in kintone before retrying.",
                            KintoneErrorKind.WriteResultUnknown, response.StatusCode, exception.ErrorCode);
                    throw exception;
                }
            }
        }

        //API トークン (X-Cybozu-API-Token。複数はカンマ区切り) か、パスワード認証 (X-Cybozu-Authorization = base64(ログイン名:パスワード))
        void AddAuthentication(HttpRequestMessage request, AccountCredentials? account)
        {
            if (account != null)
                request.Headers.TryAddWithoutValidation("X-Cybozu-Authorization", ToPasswordHeader(account.Value.Name, account.Value.Password));
            else if (Settings.UsesPassword)
                request.Headers.TryAddWithoutValidation("X-Cybozu-Authorization", ToPasswordHeader(Settings.User, Settings.Password));
            else
                request.Headers.TryAddWithoutValidation("X-Cybozu-API-Token", string.Join(",", Settings.ApiTokens));
        }

        static string ToPasswordHeader(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

        /// <summary>利用者のアカウント (ログインの確認で 1 回だけ使う。どこにも保存しない)。</summary>
        internal readonly record struct AccountCredentials(string Name, string Password)
        {
            //パスワードを文字にしない (ログ・例外に出さない)
            public override string ToString() => Name;
        }

        HttpClient? _accountHttp;

        //利用者のアカウントで送る HttpClient (Cookie を持たない・リダイレクトに従わない)。連携用の _http とは別
        HttpClient GetAccountHttp()
        {
            if (_accountHttp != null) return _accountHttp;
            var http = _handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                : new HttpClient(_handler, false);
            http.Timeout = TimeSpan.FromSeconds(Settings.TimeoutSeconds);
            return Interlocked.CompareExchange(ref _accountHttp, http, null) ?? http;
        }

        static TimeSpan GetBackoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter != null && TimeSpan.Zero < retryAfter && retryAfter < TimeSpan.FromSeconds(60)) return retryAfter.Value;
            return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        }

        //応答の code / message / errors (項目ごとの検証エラー) を入れる。要求の URL・認証の情報は入れない
        static async Task<KintoneException> CreateExceptionAsync(HttpResponseMessage response)
        {
            var body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); } catch { }

            string message = string.Empty, code = string.Empty, details = string.Empty;
            try
            {
                using var json = JsonDocument.Parse(body);
                //ゲートウェイなどが返す、オブジェクトでない本文は読まない (ステータスだけで分類する)
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = json.RootElement;
                    if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString() ?? string.Empty;
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? string.Empty;
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object) details = ReadFieldErrors(errors);
                }
            }
            catch (JsonException) { }

            var status = (int)response.StatusCode;
            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => KintoneErrorKind.Authentication,
                HttpStatusCode.NotFound => KintoneErrorKind.NotFound,
                HttpStatusCode.TooManyRequests => KintoneErrorKind.ServiceUnavailable,
                _ when 500 <= status => string.IsNullOrEmpty(code) ? KintoneErrorKind.ServiceUnavailable : KintoneErrorKind.InvalidRequest,
                _ when 400 <= status => KintoneErrorKind.InvalidRequest,
                _ => KintoneErrorKind.Unknown,
            };
            //kintone はレコードが無い (GAIA_RE01) などを 404 で、権限なし (GAIA_NO01 / CB_NO02) を 403 で返す
            if (code is "GAIA_RE01" or "GAIA_AP01") kind = KintoneErrorKind.NotFound;
            if (string.IsNullOrEmpty(message)) message = $"HTTP {status}";
            var text = "kintone: " + message + (details.Length == 0 ? string.Empty : " " + details) + (string.IsNullOrEmpty(code) ? string.Empty : $" (error {code})");
            return new KintoneException(text, kind, response.StatusCode, code);
        }

        //errors: { "record.Code.value": { "messages": ["必須です。"] } } → [Code: 必須です。] (先頭の数件だけ)
        static string ReadFieldErrors(JsonElement errors)
        {
            var parts = new List<string>();
            foreach (var error in errors.EnumerateObject())
            {
                if (5 <= parts.Count) break;
                if (error.Value.ValueKind != JsonValueKind.Object || !error.Value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
                var text = string.Join(" ", messages.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
                var name = error.Name;
                if (name.StartsWith("record.", StringComparison.Ordinal)) name = name["record.".Length..];
                if (name.EndsWith(".value", StringComparison.Ordinal)) name = name[..^".value".Length];
                parts.Add($"[{name}: {text}]");
            }
            return string.Join(" ", parts);
        }
    }
}
