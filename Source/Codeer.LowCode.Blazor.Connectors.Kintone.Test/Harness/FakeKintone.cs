using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness
{
    /// <summary>記録した 1 つの要求。</summary>
    public class RecordedRequest
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public string Url { get; init; } = string.Empty;
        /// <summary>デコードしたパス (/k/v1/records.json など)。</summary>
        public string Path { get; init; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; init; } = string.Empty;

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public JsonNode? Json => string.IsNullOrEmpty(Body) ? null : JsonNode.Parse(Body);

        /// <summary>API のパスの末尾 (records.json / record.json / app/form/fields.json / apps.json)。/k/v1/ (ゲストスペースは /k/guest/{id}/v1/) の後ろ。</summary>
        public string Api
        {
            get
            {
                var match = System.Text.RegularExpressions.Regex.Match(Path, "^/k/(guest/[0-9]+/)?v1/(.*)$");
                return match.Success ? match.Groups[2].Value : Path;
            }
        }

        public bool IsForm => Api == "app/form/fields.json";
        /// <summary>レコードの読み出し (POST records.json + X-HTTP-Method-Override: GET)。</summary>
        public bool IsRead => Api == "records.json" && Method == HttpMethod.Post && Header("X-HTTP-Method-Override") == "GET";
        public bool IsCreate => Api == "record.json" && Method == HttpMethod.Post;
        public bool IsUpdate => Api == "record.json" && Method == HttpMethod.Put;
        public bool IsDelete => Api == "records.json" && Method == HttpMethod.Delete;
        public bool IsWrite => IsCreate || IsUpdate || IsDelete;

        /// <summary>アプリ ID (本文の app か、URL の app)。</summary>
        public long? App
        {
            get
            {
                if (Json is JsonObject body && body["app"] is JsonValue app) return app.GetValue<long>();
                return long.TryParse(QueryParam("app"), out var id) ? id : null;
            }
        }

        /// <summary>読み出しの kintone のクエリ (本文の query)。</summary>
        public string? Query => IsRead ? Json?["query"]?.GetValue<string>() : null;

        /// <summary>読み出しの fields (本文の fields)。付けていなければ null。</summary>
        public List<string>? Fields => IsRead && Json?["fields"] is JsonArray fields ? fields.Select(e => e!.GetValue<string>()).ToList() : null;

        /// <summary>件数を求めたか (本文の totalCount)。</summary>
        public bool WantsTotalCount => IsRead && Json?["totalCount"]?.GetValue<bool>() == true;

        /// <summary>URL のクエリの値 (デコード済み)。無ければ null。</summary>
        public string? QueryParam(string name)
        {
            var query = new Uri(Url).Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (Uri.UnescapeDataString(kv[0]) == name) return Uri.UnescapeDataString(kv.Length == 2 ? kv[1] : string.Empty);
            }
            return null;
        }
    }

    /// <summary>
    /// kintone の REST API の偽物 (HttpMessageHandler)。要求を記録し、決めた応答を返す。
    /// レコードの読み出しは条件で絞らない (query は記録するだけ)。効かせるのは末尾の limit / offset と、「$id &gt; N」(ID の順の読み進め) だけ。
    /// fields を付けた読み出しは、そのフィールドだけを返す。
    /// </summary>
    public class FakeKintone : HttpMessageHandler
    {
        public const string Host = "https://example.cybozu.com";
        public const string ApiPath = "/k/v1";
        public const string ApiUrl = Host + ApiPath;

        readonly object _lock = new();
        readonly List<(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond, int Remaining)> _overrides = new();
        long _created = 100;

        public List<RecordedRequest> Requests { get; } = new();

        /// <summary>フォームの設定の要求を除いた要求。</summary>
        public List<RecordedRequest> DataRequests => Snapshot().Where(e => !e.IsForm).ToList();
        public List<RecordedRequest> WriteRequests => Snapshot().Where(e => e.IsWrite).ToList();
        public List<RecordedRequest> ReadRequests => Snapshot().Where(e => e.IsRead).ToList();

        List<RecordedRequest> Snapshot()
        {
            lock (_lock) return Requests.ToList();
        }

        public void ClearRequests()
        {
            lock (_lock) Requests.Clear();
        }

        /// <summary>アプリ ID → フォームの設定 (app/form/fields.json の properties)。</summary>
        public Dictionary<long, JsonObject> Forms { get; } = new();

        /// <summary>アプリ ID → レコード (records の要素の形。フィールドコード → {type, value})。</summary>
        public Dictionary<long, List<JsonObject>> Records { get; } = new();

        /// <summary>条件に合う要求に、times 回だけ別の応答を返す。</summary>
        public void Override(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> respond, int times = 1)
        {
            lock (_lock) _overrides.Add((match, respond, times));
        }

        /// <summary>条件に合う要求で、times 回だけ例外を投げる (通信の失敗)。</summary>
        public void Throw(Func<RecordedRequest, bool> match, Func<Exception> exception, int times = 1)
            => Override(match, _ => throw exception(), times);

        public static HttpResponseMessage Status(HttpStatusCode code, string? body = null)
        {
            var response = new HttpResponseMessage(code);
            if (body != null) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }

        public static HttpResponseMessage Ok(JsonNode body) => Status(HttpStatusCode.OK, body.ToJsonString());

        /// <summary>kintone のエラーの応答 ({code, id, message[, errors]})。</summary>
        public static HttpResponseMessage Error(HttpStatusCode status, string code, string message, JsonObject? errors = null)
        {
            var body = new JsonObject { ["code"] = code, ["id"] = "fake-error-id", ["message"] = message };
            if (errors != null) body["errors"] = errors;
            return Status(status, body.ToJsonString());
        }

        /// <summary>レコードの読み出しの応答。</summary>
        public static HttpResponseMessage RecordsResponse(IEnumerable<JsonObject> records, long? totalCount = null)
        {
            var array = new JsonArray();
            foreach (var record in records) array.Add(record.DeepClone());
            var body = new JsonObject { ["records"] = array, ["totalCount"] = totalCount?.ToString(CultureInfo.InvariantCulture) };
            return Ok(body);
        }

        /// <summary>レコード 1 件 ($id と $revision と、フィールドコード → (種類, 値))。</summary>
        public static JsonObject Record(long id, long revision, params (string Code, string Type, JsonNode? Value)[] fields)
        {
            var record = new JsonObject
            {
                ["$id"] = new JsonObject { ["type"] = "__ID__", ["value"] = id.ToString(CultureInfo.InvariantCulture) },
                ["$revision"] = new JsonObject { ["type"] = "__REVISION__", ["value"] = revision.ToString(CultureInfo.InvariantCulture) },
            };
            foreach (var (code, type, value) in fields) record[code] = new JsonObject { ["type"] = type, ["value"] = value };
            return record;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Url = request.RequestUri!.AbsoluteUri,
                Path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath),
                Body = body,
            };
            foreach (var h in request.Headers) recorded.Headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null) foreach (var h in request.Content.Headers) recorded.Headers[h.Key] = string.Join(",", h.Value);

            Func<RecordedRequest, HttpResponseMessage>? respond = null;
            lock (_lock)
            {
                Requests.Add(recorded);
                for (var i = 0; i < _overrides.Count; i++)
                {
                    var o = _overrides[i];
                    if (!o.Match(recorded)) continue;
                    respond = o.Respond;
                    if (o.Remaining <= 1) _overrides.RemoveAt(i);
                    else _overrides[i] = (o.Match, o.Respond, o.Remaining - 1);
                    break;
                }
            }
            if (respond != null) return respond(recorded);
            return Default(recorded);
        }

        static readonly Regex LimitPattern = new(@"\blimit (\d+)( offset (\d+))?$", RegexOptions.CultureInvariant);
        static readonly Regex SeekPattern = new(@"\$id > (\d+)", RegexOptions.CultureInvariant);

        static long IdOf(JsonObject record) => long.Parse(record["$id"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        HttpResponseMessage Default(RecordedRequest request)
        {
            if (request.IsForm)
            {
                var app = request.App ?? 0;
                if (!Forms.TryGetValue(app, out var properties)) return Error(HttpStatusCode.NotFound, "GAIA_AP01", "The app does not exist.");
                return Ok(new JsonObject { ["properties"] = properties.DeepClone(), ["revision"] = "1" });
            }
            if (request.Api == "apps.json" && request.Method == HttpMethod.Get)
                return Ok(new JsonObject { ["apps"] = new JsonArray() });
            if (request.IsRead)
            {
                var app = request.App ?? 0;
                IEnumerable<JsonObject> rows = Records.TryGetValue(app, out var list) ? list : new List<JsonObject>();
                var query = request.Query ?? string.Empty;
                var seek = SeekPattern.Match(query);
                if (seek.Success)
                {
                    var after = long.Parse(seek.Groups[1].Value, CultureInfo.InvariantCulture);
                    rows = rows.Where(e => IdOf(e) > after);
                }
                if (query.Contains("order by $id asc", StringComparison.Ordinal)) rows = rows.OrderBy(IdOf);
                var all = rows.ToList();
                var limit = LimitPattern.Match(query);
                var take = limit.Success ? int.Parse(limit.Groups[1].Value, CultureInfo.InvariantCulture) : 100;
                var skip = limit.Success && limit.Groups[3].Success ? int.Parse(limit.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                var fields = request.Fields;
                var page = all.Skip(skip).Take(take).Select(e =>
                {
                    if (fields == null || fields.Count == 0) return e;
                    var projected = new JsonObject();
                    foreach (var field in fields) if (e.ContainsKey(field)) projected[field] = e[field]!.DeepClone();
                    return projected;
                });
                return RecordsResponse(page, request.WantsTotalCount ? all.Count : null);
            }
            if (request.IsCreate)
            {
                var id = Interlocked.Increment(ref _created);
                return Ok(new JsonObject { ["id"] = id.ToString(CultureInfo.InvariantCulture), ["revision"] = "1" });
            }
            if (request.IsUpdate) return Ok(new JsonObject { ["revision"] = "2" });
            if (request.IsDelete) return Ok(new JsonObject());
            return Error(HttpStatusCode.NotFound, "CB_NF01", "The API does not exist.");
        }
    }
}
