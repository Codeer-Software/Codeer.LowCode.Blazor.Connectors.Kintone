using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kintone.Mock
{
    /// <summary>
    /// kintone REST API のモック (状態を持つ本体)。アプリ・フォームの定義・レコード・利用者・API トークンをメモリに持つ。
    /// HTTP に結び付けるのは <see cref="MockKintoneEndpoints.MapMockKintone"/>。テストごとに作り直せる。
    /// </summary>
    public sealed partial class MockKintone
    {
        /// <summary>kintone の API のパスの先頭 (/k/v1/... と /k/guest/{spaceId}/v1/...)。</summary>
        public const string BasePath = "/k";

        /// <summary>API トークンで操作したときの作成者・更新者。</summary>
        public const string AdministratorCode = "Administrator";

        /// <summary>1 つの要求に付けられる API トークンの数。</summary>
        public const int MaxApiTokens = 9;

        readonly object _lock = new();
        readonly List<MockApp> _apps = new();
        readonly List<MockRecordedRequest> _requests = new();
        readonly List<Fault> _faults = new();
        long _nextRowId = 1001;
        long _nextFileId = 1;

        MockKintone() { }

        /// <summary>アプリ (ID の順)。定義を直接書き換えてもよい (書き換えたら <see cref="MockApp.FormRevision"/> も上げること)。</summary>
        public IReadOnlyList<MockApp> Apps
        {
            get
            {
                lock (_lock) return _apps.ToList();
            }
        }

        /// <summary>パスワード認証で使える利用者。足す・消すしてよい。</summary>
        public List<MockUser> Users { get; } = new();

        /// <summary>モックの時計 (作成日時・更新日時に使う)。既定は今の時刻。<see cref="SetTime"/> / <see cref="AdvanceTime"/> でも変えられる。</summary>
        public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>時計を止めて、いつも time を返すようにする。</summary>
        public void SetTime(DateTimeOffset time) => Clock = () => time;

        /// <summary>時計を span だけ進める (止めた時計でも、動いている時計でも)。</summary>
        public void AdvanceTime(TimeSpan span)
        {
            var clock = Clock;
            Clock = () => clock() + span;
        }

        DateTimeOffset Now => Clock().ToUniversalTime();

        public MockApp GetApp(long appId)
        {
            lock (_lock) return FindApp(appId) ?? throw new ArgumentException($"The app {appId} does not exist.", nameof(appId));
        }

        MockApp? FindApp(long appId) => _apps.FirstOrDefault(e => e.Id == appId);

        // ---------------------------------------------------------------- 作る

        static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        internal static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>mock-data.json の形の文字から作る。誤りがあれば InvalidDataException。</summary>
        public static MockKintone FromJson(string json)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json, documentOptions: DocumentOptions);
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"The mock data is not valid JSON: {e.Message}", e);
            }
            if (root is not JsonObject data) throw new InvalidDataException("The mock data must be a JSON object.");
            var mock = new MockKintone();
            mock.Load(data);
            return mock;
        }

        /// <summary>mock-data.json の形のファイルから作る。</summary>
        public static MockKintone FromFile(string path) => FromJson(File.ReadAllText(path));

        /// <summary>mock-data.json の形のオブジェクト (匿名型など) から作る。</summary>
        public static MockKintone FromObject(object data) => FromJson(JsonSerializer.Serialize(data, WriteOptions));

        void Load(JsonObject data)
        {
            foreach (var (key, _) in data)
            {
                if (key is not ("users" or "apps")) throw new InvalidDataException($"Unknown property '{key}' in the mock data.");
            }
            if (data["users"] is JsonArray users)
            {
                var i = 0;
                foreach (var node in users) Users.Add(ParseUser(node, $"users[{i++}]"));
            }
            else if (data["users"] != null) throw new InvalidDataException("'users' must be an array.");
            if (Users.Select(e => e.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Users.Count) throw new InvalidDataException("The user codes in 'users' must be unique.");

            if (data["apps"] is not JsonArray apps) throw new InvalidDataException("'apps' (an array) is required.");
            var records = new Dictionary<long, JsonArray>();
            for (var i = 0; i < apps.Count; i++)
            {
                var path = $"apps[{i}]";
                if (apps[i] is not JsonObject appJson) throw new InvalidDataException($"{path} must be an object.");
                var app = ParseApp(appJson, path);
                if (FindApp(app.Id) != null) throw new InvalidDataException($"{path}: the app id {app.Id} is duplicated.");
                _apps.Add(app);
                if (appJson["records"] is JsonArray rows) records[app.Id] = rows;
                else if (appJson["records"] != null) throw new InvalidDataException($"{path}.records must be an array.");
            }
            _apps.Sort((a, b) => a.Id.CompareTo(b.Id));
            var tokens = _apps.SelectMany(e => e.Tokens).Select(e => e.Token).ToList();
            if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Count) throw new InvalidDataException("The API tokens must be unique.");
            foreach (var app in _apps) ValidateApp(app);

            //ルックアップの参照元のアプリから先に入れる
            foreach (var app in LookupOrder())
            {
                if (!records.TryGetValue(app.Id, out var rows)) continue;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is not JsonObject row) throw new InvalidDataException($"apps (id {app.Id}).records[{i}] must be an object.");
                    try
                    {
                        AddRecordCore(app, row, null);
                    }
                    catch (Exception e) when (e is MockKintoneException or InvalidOperationException or FormatException)
                    {
                        throw new InvalidDataException($"apps (id {app.Id}).records[{i}]: {Describe(e)}", e);
                    }
                }
            }
        }

        List<MockApp> LookupOrder()
        {
            var result = new List<MockApp>();
            var visiting = new HashSet<long>();
            void Visit(MockApp app)
            {
                if (result.Contains(app)) return;
                if (!visiting.Add(app.Id)) throw new InvalidDataException($"The lookups of the app {app.Id} are circular.");
                foreach (var field in app.Fields.Where(e => e.Lookup != null))
                {
                    var source = FindApp(field.Lookup!.RelatedApp);
                    if (source != null && source != app) Visit(source);
                }
                result.Add(app);
            }
            foreach (var app in _apps) Visit(app);
            return result;
        }

        static string Describe(Exception e) => e is MockKintoneException k && k.Errors != null
            ? k.Message + " " + string.Join(" ", k.Errors.Select(x => $"{x.Key}: {string.Join(" / ", x.Value)}"))
            : e.Message;

        static MockUser ParseUser(JsonNode? node, string path)
        {
            if (node is not JsonObject o) throw new InvalidDataException($"{path} must be an object.");
            var user = new MockUser
            {
                Code = RequiredString(o, "code", path),
                Password = RequiredString(o, "password", path),
                Name = o["name"]?.GetValue<string>() ?? RequiredString(o, "code", path),
            };
            if (string.Equals(user.Code, AdministratorCode, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{path}: the user code '{AdministratorCode}' is reserved.");
            if (o["permissions"] is JsonObject permissions)
            {
                user.Permissions = new Dictionary<long, MockPermission>();
                foreach (var (key, value) in permissions)
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)) throw new InvalidDataException($"{path}.permissions: '{key}' is not an app id.");
                    user.Permissions[appId] = ParsePermission(value, $"{path}.permissions.{key}");
                }
            }
            foreach (var (key, _) in o)
            {
                if (key is not ("code" or "password" or "name" or "permissions")) throw new InvalidDataException($"{path}: unknown property '{key}'.");
            }
            return user;
        }

        static MockPermission ParsePermission(JsonNode? node, string path)
        {
            if (node is not JsonArray array) throw new InvalidDataException($"{path} must be an array of \"view\" / \"add\" / \"edit\" / \"delete\".");
            var permission = MockPermission.None;
            foreach (var item in array)
            {
                permission |= item?.GetValue<string>() switch
                {
                    "view" => MockPermission.View,
                    "add" => MockPermission.Add,
                    "edit" => MockPermission.Edit,
                    "delete" => MockPermission.Delete,
                    var other => throw new InvalidDataException($"{path}: unknown permission '{other}'."),
                };
            }
            return permission;
        }

        static string RequiredString(JsonObject o, string name, string path)
        {
            var value = o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrEmpty(value)) throw new InvalidDataException($"{path}.{name} (a string) is required.");
            return value;
        }

        static long RequiredLong(JsonObject o, string name, string path)
        {
            if (o[name] is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l) && l > 0) return l;
                if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out l) && l > 0) return l;
            }
            throw new InvalidDataException($"{path}.{name} (a positive integer) is required.");
        }

        static MockApp ParseApp(JsonObject o, string path)
        {
            foreach (var (key, _) in o)
            {
                if (key is not ("id" or "name" or "code" or "guestSpaceId" or "statuses" or "tokens" or "fields" or "records"))
                    throw new InvalidDataException($"{path}: unknown property '{key}'.");
            }
            var app = new MockApp
            {
                Id = RequiredLong(o, "id", path),
                Name = RequiredString(o, "name", path),
                Code = o["code"]?.GetValue<string>() ?? string.Empty,
                GuestSpaceId = o["guestSpaceId"] == null ? null : RequiredLong(o, "guestSpaceId", path),
            };
            if (app.Code.Length > 0 && !Regex.IsMatch(app.Code, "^[A-Za-z][A-Za-z0-9_]*$")) throw new InvalidDataException($"{path}.code must start with a letter and contain only letters, digits and '_'.");
            if (o["statuses"] is JsonArray statuses)
            {
                foreach (var s in statuses) app.Statuses.Add(s?.GetValue<string>() ?? throw new InvalidDataException($"{path}.statuses must be strings."));
            }
            if (o["tokens"] is JsonArray tokens)
            {
                for (var i = 0; i < tokens.Count; i++)
                {
                    var tokenPath = $"{path}.tokens[{i}]";
                    if (tokens[i] is not JsonObject t) throw new InvalidDataException($"{tokenPath} must be an object.");
                    app.Tokens.Add(new MockApiToken
                    {
                        Token = RequiredString(t, "token", tokenPath),
                        Permission = ParsePermission(t["permissions"], tokenPath + ".permissions"),
                    });
                }
            }
            if (o["fields"] is not JsonObject fields) throw new InvalidDataException($"{path}.fields (an object) is required.");
            app.Fields.AddRange(ParseFields(fields, false, path + ".fields"));
            AddSystemFields(app);
            return app;
        }

        static void AddSystemFields(MockApp app)
        {
            foreach (var (type, code) in MockFieldTypes.SystemTypes)
            {
                if (app.Fields.Any(e => e.Type == type)) continue;
                app.Fields.Add(new MockField { Code = code, Type = type, Label = code });
            }
        }

        static List<MockField> ParseFields(JsonObject fields, bool inTable, string path)
        {
            var result = new List<MockField>();
            foreach (var (code, node) in fields)
            {
                var fieldPath = $"{path}.{code}";
                var field = new MockField { Code = code, Label = code };
                if (node is JsonValue v && v.TryGetValue<string>(out var typeOnly)) field.Type = typeOnly;
                else if (node is JsonObject o)
                {
                    foreach (var (key, _) in o)
                    {
                        if (key is not ("type" or "label" or "required" or "unique" or "options" or "defaultValue" or "defaultNowValue" or "expression" or "format" or "protocol" or "lookup" or "fields"))
                            throw new InvalidDataException($"{fieldPath}: unknown property '{key}'.");
                    }
                    field.Type = RequiredString(o, "type", fieldPath);
                    field.Label = o["label"]?.GetValue<string>() ?? code;
                    field.Required = o["required"]?.GetValue<bool>() ?? false;
                    field.Unique = o["unique"]?.GetValue<bool>() ?? false;
                    if (o["options"] is JsonArray options)
                    {
                        foreach (var option in options) field.Options.Add(option?.GetValue<string>() ?? throw new InvalidDataException($"{fieldPath}.options must be strings."));
                    }
                    field.DefaultValue = o["defaultValue"]?.DeepClone();
                    field.DefaultNowValue = o["defaultNowValue"]?.GetValue<bool>() ?? false;
                    field.Expression = o["expression"]?.GetValue<string>();
                    field.Format = o["format"]?.GetValue<string>() ?? "NUMBER";
                    field.Protocol = o["protocol"]?.GetValue<string>() ?? "WEB";
                    if (o["lookup"] is JsonObject lookup)
                    {
                        field.Lookup = new MockLookup
                        {
                            RelatedApp = RequiredLong(lookup, "app", fieldPath + ".lookup"),
                            RelatedKeyField = RequiredString(lookup, "relatedKeyField", fieldPath + ".lookup"),
                        };
                        if (lookup["fieldMappings"] is JsonObject mappings)
                        {
                            foreach (var (target, source) in mappings)
                                field.Lookup.FieldMappings.Add(new(target, source?.GetValue<string>() ?? throw new InvalidDataException($"{fieldPath}.lookup.fieldMappings must map a field code to a field code.")));
                        }
                    }
                    if (o["fields"] is JsonObject inner)
                    {
                        if (inTable) throw new InvalidDataException($"{fieldPath}: a table cannot contain a table.");
                        field.Fields.AddRange(ParseFields(inner, true, fieldPath + ".fields"));
                    }
                }
                else throw new InvalidDataException($"{fieldPath} must be a type name or an object.");
                ValidateFieldShape(field, inTable, fieldPath);
                result.Add(field);
            }
            return result;
        }

        static void ValidateFieldShape(MockField field, bool inTable, string path)
        {
            if (field.Code.Length == 0 || field.Code.StartsWith('$') || field.Code.Any(c => char.IsWhiteSpace(c) || "()=!<>,\"".Contains(c)))
                throw new InvalidDataException($"{path}: the field code is not usable.");
            var isSystem = MockFieldTypes.IsSystem(field.Type);
            if (!isSystem && !MockFieldTypes.UserTypes.Contains(field.Type)) throw new InvalidDataException($"{path}: unknown type '{field.Type}'.");
            if (isSystem && inTable) throw new InvalidDataException($"{path}: a system field cannot be in a table.");
            if (field.Type == MockFieldTypes.Subtable && inTable) throw new InvalidDataException($"{path}: a table cannot contain a table.");
            if (field.Type == MockFieldTypes.Subtable && field.Fields.Count == 0) throw new InvalidDataException($"{path}: a table needs 'fields'.");
            if (field.Type != MockFieldTypes.Subtable && field.Fields.Count > 0) throw new InvalidDataException($"{path}: only a table can have 'fields'.");
            if (MockFieldTypes.HasOptions(field.Type) && field.Options.Count == 0) throw new InvalidDataException($"{path}: 'options' is required for {field.Type}.");
            if (!MockFieldTypes.HasOptions(field.Type) && field.Options.Count > 0) throw new InvalidDataException($"{path}: {field.Type} cannot have 'options'.");
            if (field.Type == MockFieldTypes.Calc && string.IsNullOrEmpty(field.Expression)) throw new InvalidDataException($"{path}: a CALC field needs 'expression'.");
            if (field.Expression != null && field.Type is not (MockFieldTypes.Calc or MockFieldTypes.SingleLineText)) throw new InvalidDataException($"{path}: only CALC and SINGLE_LINE_TEXT can have 'expression'.");
            if (field.Type == MockFieldTypes.Calc && field.Format != "NUMBER") throw new InvalidDataException($"{path}: the mock calculates only the format NUMBER.");
            if (field.Unique && field.Type is not (MockFieldTypes.SingleLineText or MockFieldTypes.Number or MockFieldTypes.Link or MockFieldTypes.Date or MockFieldTypes.DateTime))
                throw new InvalidDataException($"{path}: {field.Type} cannot be unique.");
            if (field.Unique && inTable) throw new InvalidDataException($"{path}: a field in a table cannot be unique.");
            if (field.Lookup != null)
            {
                if (inTable) throw new InvalidDataException($"{path}: the mock does not support lookups in a table.");
                if (field.Type is not (MockFieldTypes.SingleLineText or MockFieldTypes.Number)) throw new InvalidDataException($"{path}: a lookup must be SINGLE_LINE_TEXT or NUMBER.");
                if (field.Expression != null) throw new InvalidDataException($"{path}: a lookup cannot have 'expression'.");
            }
            if (field.Type == MockFieldTypes.RadioButton)
            {
                field.Required = true;
                if (field.DefaultValue == null) field.DefaultValue = field.Options[0];
            }
            if (field.DefaultValue != null)
            {
                var ok = field.Type switch
                {
                    MockFieldTypes.CheckBox or MockFieldTypes.MultiSelect => field.DefaultValue is JsonArray a && a.All(e => e is JsonValue x && x.TryGetValue<string>(out var s) && field.Options.Contains(s)),
                    MockFieldTypes.UserSelect => field.DefaultValue is JsonArray,
                    MockFieldTypes.RadioButton or MockFieldTypes.DropDown => field.DefaultValue is JsonValue x && x.TryGetValue<string>(out var s) && (s.Length == 0 || field.Options.Contains(s)),
                    MockFieldTypes.Calc or MockFieldTypes.File or MockFieldTypes.Subtable => false,
                    _ when isSystem => false,
                    _ => field.DefaultValue is JsonValue,
                };
                if (!ok) throw new InvalidDataException($"{path}: 'defaultValue' does not fit {field.Type}.");
            }
        }

        void ValidateApp(MockApp app)
        {
            var path = $"apps (id {app.Id})";
            var all = app.AllFields().ToList();
            var duplicate = all.GroupBy(e => e.Code).FirstOrDefault(e => e.Count() > 1);
            if (duplicate != null) throw new InvalidDataException($"{path}: the field code '{duplicate.Key}' is duplicated.");
            foreach (var (type, _) in MockFieldTypes.SystemTypes)
            {
                if (app.Fields.Count(e => e.Type == type) > 1) throw new InvalidDataException($"{path}: only one {type} field is allowed.");
            }
            foreach (var field in app.Fields)
            {
                if (field.Lookup is { } lookup)
                {
                    var source = FindApp(lookup.RelatedApp) ?? throw new InvalidDataException($"{path}.{field.Code}: the lookup app {lookup.RelatedApp} does not exist.");
                    var key = source.FindField(lookup.RelatedKeyField);
                    if (key == null || key.Value.Table != null || key.Value.Field.Type is not (MockFieldTypes.SingleLineText or MockFieldTypes.Number or MockFieldTypes.RecordNumber or MockFieldTypes.Calc or MockFieldTypes.Link))
                        throw new InvalidDataException($"{path}.{field.Code}: relatedKeyField '{lookup.RelatedKeyField}' must be a text, number or record number field of the app {source.Id}.");
                    foreach (var (target, from) in lookup.FieldMappings)
                    {
                        var t = app.FindField(target);
                        if (t == null || t.Value.Table != null || t.Value.Field.IsSystem || t.Value.Field.Type is MockFieldTypes.Calc or MockFieldTypes.Subtable or MockFieldTypes.File || t.Value.Field == field)
                            throw new InvalidDataException($"{path}.{field.Code}: the lookup copy target '{target}' must be a writable field outside tables.");
                        var s = source.FindField(from);
                        if (s == null || s.Value.Table != null || s.Value.Field.Type == MockFieldTypes.Subtable)
                            throw new InvalidDataException($"{path}.{field.Code}: the lookup copy source '{from}' does not exist in the app {source.Id}.");
                    }
                }
            }
            foreach (var (field, table) in all.Select(e => (e, app.FindField(e.Code)!.Value.Table)))
            {
                if (string.IsNullOrEmpty(field.Expression)) continue;
                MockExpression expression;
                try
                {
                    expression = MockExpression.Parse(field.Expression);
                }
                catch (FormatException e)
                {
                    throw new InvalidDataException($"{path}.{field.Code}: {e.Message}", e);
                }
                foreach (var code in expression.FieldReferences)
                {
                    var target = app.FindField(code);
                    var ok = target != null && target.Value.Field.Type != MockFieldTypes.Subtable && (target.Value.Table == null || target.Value.Table == table);
                    if (!ok) throw new InvalidDataException($"{path}.{field.Code}: the expression refers to '{code}', which is not a field outside tables or in the same table.");
                }
                foreach (var code in expression.SumReferences)
                {
                    var target = app.FindField(code);
                    if (target == null || target.Value.Table == null || table != null)
                        throw new InvalidDataException($"{path}.{field.Code}: SUM({code}) must be used outside tables with a field in a table.");
                }
            }
        }

        // ---------------------------------------------------------------- テスト用の口: レコード・フォーム

        /// <summary>レコードを 1 件 (records.json の 1 件と同じ形)。無ければ null。</summary>
        public JsonObject? GetRecord(long appId, long recordId)
        {
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                var record = app.Records.FirstOrDefault(e => e.Id == recordId);
                return record == null ? null : RecordToJson(app, record, null);
            }
        }

        /// <summary>レコードを全部 (レコード ID の昇順・records.json の 1 件と同じ形)。</summary>
        public IReadOnlyList<JsonObject> GetRecords(long appId)
        {
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                return app.Records.OrderBy(e => e.Id).Select(e => RecordToJson(app, e, null)).ToList();
            }
        }

        /// <summary>
        /// レコードを足す (POST record.json と同じ検証・既定値・ルックアップ・計算)。権限は見ない。
        /// record は {"コード": {"value": 値}} か {"コード": 値} の形 (mock-data.json の records と同じ。"$id" などの指定も可)。
        /// 作成者は user (省略すると Administrator)。戻り値はレコード ID。足せなければ InvalidOperationException。
        /// </summary>
        public long AddRecord(long appId, object record, string? user = null)
        {
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                try
                {
                    return AddRecordCore(app, ToRecordObject(record), user).Id;
                }
                catch (MockKintoneException e)
                {
                    throw new InvalidOperationException(Describe(e), e);
                }
            }
        }

        /// <summary>
        /// レコードを書き換える (PUT record.json と同じ扱い: 送ったフィールドだけ・テーブルは送った行で置き換え・版を 1 上げる)。権限と版は見ない。
        /// 別の利用者が先に更新した状況を作るのに使う。更新者は user (省略すると Administrator)。戻り値は新しい版。
        /// </summary>
        public long UpdateRecord(long appId, long recordId, object record, string? user = null)
        {
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                var existing = app.Records.FirstOrDefault(e => e.Id == recordId) ?? throw new InvalidOperationException($"The record {recordId} does not exist in the app {appId}.");
                try
                {
                    var updated = BuildRecord(app, existing, ToRecordObject(record), ActorOf(user), null, lenient: true);
                    updated.Revision = existing.Revision + 1;
                    app.Records[app.Records.IndexOf(existing)] = updated;
                    return updated.Revision;
                }
                catch (MockKintoneException e)
                {
                    throw new InvalidOperationException(Describe(e), e);
                }
            }
        }

        /// <summary>レコードを消す。無ければ false。</summary>
        public bool DeleteRecord(long appId, long recordId)
        {
            lock (_lock) return GetAppLocked(appId).Records.RemoveAll(e => e.Id == recordId) > 0;
        }

        /// <summary>
        /// フォームの定義 (システムでないフィールド) を差し替える。fields は mock-data.json の "fields" と同じ形。
        /// 無くなったコードの値はレコードから消え、増えたフィールドは既存のレコードでは未入力になる。フォームの版 (revision) が 1 上がる。
        /// </summary>
        public void SetFields(long appId, object fields)
        {
            var node = fields as JsonObject ?? (fields is string s ? JsonNode.Parse(s, documentOptions: DocumentOptions) : JsonSerializer.SerializeToNode(fields, WriteOptions)) as JsonObject
                ?? throw new ArgumentException("fields must be a JSON object.", nameof(fields));
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                var parsed = ParseFields(node, false, "fields");
                var system = app.Fields.Where(e => e.IsSystem && !parsed.Any(p => p.Type == e.Type)).ToList();
                var old = app.Fields.ToList();
                app.Fields.Clear();
                app.Fields.AddRange(parsed);
                app.Fields.AddRange(system);
                try
                {
                    ValidateApp(app);
                }
                catch
                {
                    app.Fields.Clear();
                    app.Fields.AddRange(old);
                    throw;
                }
                foreach (var record in app.Records) AlignValues(app, record);
                app.FormRevision++;
            }
        }

        /// <summary>フィールドコードを変える (kintone でコードを変えたときと同じく、値・ルックアップ・計算式の参照も付いて行く)。フォームの版が 1 上がる。</summary>
        public void RenameField(long appId, string oldCode, string newCode)
        {
            lock (_lock)
            {
                var app = GetAppLocked(appId);
                var found = app.FindField(oldCode) ?? throw new ArgumentException($"The field '{oldCode}' does not exist.", nameof(oldCode));
                if (app.FindField(newCode) != null) throw new ArgumentException($"The field '{newCode}' already exists.", nameof(newCode));
                var field = found.Field;
                field.Code = newCode;
                ValidateFieldShape(field, found.Table != null, "fields." + newCode);
                var pattern = new Regex($"(?<![\\p{{L}}\\p{{N}}_・＄￥$]){Regex.Escape(oldCode)}(?![\\p{{L}}\\p{{N}}_・＄￥$])");
                foreach (var f in app.AllFields())
                {
                    if (f.Expression != null) f.Expression = pattern.Replace(f.Expression, newCode);
                    if (f.Lookup != null)
                    {
                        for (var i = 0; i < f.Lookup.FieldMappings.Count; i++)
                        {
                            var (target, source) = f.Lookup.FieldMappings[i];
                            if (target == oldCode) f.Lookup.FieldMappings[i] = new(newCode, source);
                        }
                    }
                }
                foreach (var other in _apps)
                {
                    foreach (var f in other.Fields.Where(e => e.Lookup?.RelatedApp == app.Id))
                    {
                        if (f.Lookup!.RelatedKeyField == oldCode) f.Lookup.RelatedKeyField = newCode;
                        for (var i = 0; i < f.Lookup.FieldMappings.Count; i++)
                        {
                            var (target, source) = f.Lookup.FieldMappings[i];
                            if (source == oldCode) f.Lookup.FieldMappings[i] = new(target, newCode);
                        }
                    }
                }
                foreach (var record in app.Records)
                {
                    if (found.Table == null)
                    {
                        if (record.Values.Remove(oldCode, out var value)) record.Values[newCode] = value;
                    }
                    else if (record.Values.TryGetValue(found.Table.Code, out var rows) && rows is List<StoredRow> list)
                    {
                        foreach (var row in list)
                        {
                            if (row.Values.Remove(oldCode, out var value)) row.Values[newCode] = value;
                        }
                    }
                }
                app.FormRevision++;
            }
        }

        MockApp GetAppLocked(long appId) => FindApp(appId) ?? throw new ArgumentException($"The app {appId} does not exist.", nameof(appId));

        static JsonObject ToRecordObject(object record) => record switch
        {
            JsonObject o => (JsonObject)o.DeepClone(),
            string s => JsonNode.Parse(s, documentOptions: DocumentOptions) as JsonObject ?? throw new ArgumentException("record must be a JSON object.", nameof(record)),
            _ => JsonSerializer.SerializeToNode(record, WriteOptions) as JsonObject ?? throw new ArgumentException("record must be an object.", nameof(record)),
        };

        //フォームの定義に合わせて、レコードの値を足す・消す
        void AlignValues(MockApp app, StoredRecord record)
        {
            foreach (var key in record.Values.Keys.ToList())
            {
                var field = app.Fields.FirstOrDefault(e => e.Code == key);
                if (field == null && !SystemValueKeys.Contains(key)) record.Values.Remove(key);
            }
            foreach (var field in app.Fields.Where(e => !e.IsSystem))
            {
                if (!record.Values.ContainsKey(field.Code)) record.Values[field.Code] = field.Type == MockFieldTypes.Subtable ? new List<StoredRow>() : EmptyValue(field.Type);
                if (field.Type == MockFieldTypes.Subtable && record.Values[field.Code] is List<StoredRow> rows)
                {
                    foreach (var row in rows)
                    {
                        foreach (var key in row.Values.Keys.ToList())
                        {
                            if (!field.Fields.Any(e => e.Code == key)) row.Values.Remove(key);
                        }
                        foreach (var inner in field.Fields)
                        {
                            if (!row.Values.ContainsKey(inner.Code)) row.Values[inner.Code] = EmptyValue(inner.Type);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- テスト用の口: 要求の記録

        /// <summary>受けた要求 (古い順。認証に失敗した要求・障害を仕込んだ要求も含む)。</summary>
        public IReadOnlyList<MockRecordedRequest> Requests
        {
            get
            {
                lock (_lock) return _requests.ToList();
            }
        }

        /// <summary>要求の記録を消す。</summary>
        public void ClearRequests()
        {
            lock (_lock) _requests.Clear();
        }

        MockRecordedRequest Record(MockRequest request)
        {
            var (api, space) = ParsePath(request.Path);
            var effective = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) && string.Equals(request.MethodOverride?.Trim(), "GET", StringComparison.OrdinalIgnoreCase)
                ? "GET" : request.Method.ToUpperInvariant();
            var recorded = new MockRecordedRequest
            {
                Method = request.Method.ToUpperInvariant(),
                EffectiveMethod = effective,
                MethodOverride = request.MethodOverride,
                Path = request.Path,
                PathAndQuery = request.PathAndQuery,
                Api = api,
                GuestSpaceId = space,
                Query = request.Query,
                Body = string.IsNullOrEmpty(request.Body) ? null : request.Body,
                ApiTokens = SplitTokens(request.ApiToken),
                LoginName = DecodeLogin(request.CybozuAuthorization)?.Login,
            };
            lock (_lock) _requests.Add(recorded);
            return recorded;
        }

        static IReadOnlyList<string> SplitTokens(string? header)
            => header == null ? Array.Empty<string>() : header.Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

        static (string Login, string Password)? DecodeLogin(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            try
            {
                var text = Encoding.UTF8.GetString(System.Convert.FromBase64String(header.Trim()));
                var colon = text.IndexOf(':');
                return colon < 0 ? null : (text[..colon], text[(colon + 1)..]);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>/k/v1/{api} → (api, null)、/k/guest/{space}/v1/{api} → (api, space)。kintone の API の形でなければ (null, null)。</summary>
        static (string? Api, long? Space) ParsePath(string path)
        {
            if (!path.StartsWith(BasePath + "/", StringComparison.Ordinal)) return (null, null);
            var rest = path[(BasePath.Length + 1)..];
            if (rest.StartsWith("v1/", StringComparison.Ordinal)) return (rest[3..], null);
            var parts = rest.Split('/', 4);
            if (parts.Length == 4 && parts[0] == "guest" && parts[2] == "v1" && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var space) && space > 0)
                return (parts[3], space);
            return (null, null);
        }

        // ---------------------------------------------------------------- テスト用の口: 障害

        enum FaultTiming { Delay, Before, AfterWrite }

        sealed class Fault
        {
            public FaultTiming Timing { get; init; }
            public Func<MockRecordedRequest, bool>? When { get; init; }
            public int Remaining { get; set; }
            public int Status { get; init; }
            public string? Body { get; init; }
            public bool Disconnect { get; init; }
            public TimeSpan Delay { get; init; }
        }

        void AddFault(Fault fault)
        {
            if (fault.Remaining < 1) throw new ArgumentOutOfRangeException("count", "count must be 1 or more.");
            lock (_lock) _faults.Add(fault);
        }

        Fault? TakeFault(FaultTiming timing, MockRecordedRequest request)
        {
            lock (_lock)
            {
                var fault = _faults.FirstOrDefault(e => e.Timing == timing && e.Remaining > 0 && (e.When == null || e.When(request)));
                if (fault == null) return null;
                if (--fault.Remaining == 0) _faults.Remove(fault);
                return fault;
            }
        }

        /// <summary>
        /// 次の count 回の要求 (when を指定すれば、それに合う要求だけ) に、何も実行せずに status を返す。
        /// body を省略すると kintone の形のエラー ({"code":"MOCK_FAULT",...})。
        /// </summary>
        public void FailNext(int status, string? body = null, Func<MockRecordedRequest, bool>? when = null, int count = 1)
            => AddFault(new Fault { Timing = FaultTiming.Before, Status = status, Body = body, When = when, Remaining = count });

        /// <summary>次の count 回の要求で、何も実行せずに接続を切る。</summary>
        public void DisconnectNext(Func<MockRecordedRequest, bool>? when = null, int count = 1)
            => AddFault(new Fault { Timing = FaultTiming.Before, Disconnect = true, When = when, Remaining = count });

        /// <summary>次の count 回の要求を、delay だけ待ってから処理する (タイムアウトの確かめ用)。</summary>
        public void DelayNext(TimeSpan delay, Func<MockRecordedRequest, bool>? when = null, int count = 1)
            => AddFault(new Fault { Timing = FaultTiming.Delay, Delay = delay, When = when, Remaining = count });

        /// <summary>
        /// 次の count 回の書き込み (POST record.json / PUT record.json / DELETE records.json のうち成功したもの) を、実行したうえで status を返す。
        /// body を省略すると kintone の形でない応答 (text/html。途中の機器が返す形) にする。
        /// </summary>
        public void FailAfterWrite(int status = 500, string? body = null, Func<MockRecordedRequest, bool>? when = null, int count = 1)
            => AddFault(new Fault { Timing = FaultTiming.AfterWrite, Status = status, Body = body, When = when, Remaining = count });

        /// <summary>次の count 回の書き込み (成功したもの) を、実行したうえで接続を切る (応答だけ失わせる)。</summary>
        public void DisconnectAfterWrite(Func<MockRecordedRequest, bool>? when = null, int count = 1)
            => AddFault(new Fault { Timing = FaultTiming.AfterWrite, Disconnect = true, When = when, Remaining = count });

        /// <summary>仕込んだ障害を全部取り消す。</summary>
        public void ClearFaults()
        {
            lock (_lock) _faults.Clear();
        }

        static MockResponse FaultResponse(Fault fault)
        {
            if (fault.Disconnect) return new MockResponse { Abort = true };
            if (fault.Body != null) return new MockResponse { Status = fault.Status, Body = fault.Body };
            //書き込んだあとの失敗は kintone の形にしない (kintone のエラーコードの付いた応答は「処理されなかった」の意味になる。
            //書き込みが済んだのに応答だけ失われる状況 = 途中の機器が返した応答の形にする)
            if (fault.Timing == FaultTiming.AfterWrite)
                return new MockResponse { Status = fault.Status, Body = $"<html><body>Mock fault (HTTP {fault.Status.ToString(CultureInfo.InvariantCulture)})</body></html>", ContentType = "text/html" };
            return Error(fault.Status, "MOCK_FAULT", $"モックが注入した失敗です (HTTP {fault.Status.ToString(CultureInfo.InvariantCulture)})。", null);
        }

        // ---------------------------------------------------------------- 応答

        static MockResponse Json(int status, JsonNode body) => new() { Status = status, Body = body.ToJsonString(WriteOptions) };

        static MockResponse Error(int status, string code, string message, IReadOnlyDictionary<string, List<string>>? errors)
        {
            var body = new JsonObject
            {
                ["code"] = code,
                ["id"] = Guid.NewGuid().ToString("N")[..20],
                ["message"] = message,
            };
            if (errors != null)
            {
                var e = new JsonObject();
                foreach (var (key, messages) in errors) e[key] = new JsonObject { ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m!).ToArray()) };
                body["errors"] = e;
            }
            return Json(status, body);
        }

        // ---------------------------------------------------------------- 要求の処理

        /// <summary>要求を 1 つ処理する (記録 → 遅延 → 実行しない障害 → 認証 → 処理 → 書いたあとの障害)。</summary>
        public async Task<MockResponse> HandleAsync(MockRequest request, CancellationToken cancellationToken = default)
        {
            var recorded = Record(request);
            var delay = TakeFault(FaultTiming.Delay, recorded);
            if (delay != null) await Task.Delay(delay.Delay, cancellationToken);
            var before = TakeFault(FaultTiming.Before, recorded);
            if (before != null) return FaultResponse(before);

            MockResponse response;
            bool wrote;
            lock (_lock)
            {
                try
                {
                    (response, wrote) = Process(request, recorded);
                }
                catch (MockKintoneException e)
                {
                    (response, wrote) = (Error(e.Status, e.Code, e.Message, e.Errors), false);
                }
            }
            if (wrote)
            {
                var after = TakeFault(FaultTiming.AfterWrite, recorded);
                if (after != null) return FaultResponse(after);
            }
            return response;
        }

        static MockKintoneException NotFoundApi(MockRecordedRequest request)
            => new(404, "MOCK_NOT_SUPPORTED", $"モックはこの API に応えません: {request.EffectiveMethod} {request.Path}");

        (MockResponse Response, bool Wrote) Process(MockRequest request, MockRecordedRequest recorded)
        {
            if (recorded.Api == null) throw NotFoundApi(recorded);
            var caller = Authenticate(request, recorded);
            var space = recorded.GuestSpaceId;
            switch (recorded.Api, recorded.EffectiveMethod)
            {
                case ("app/form/fields.json", "GET"):
                    return (GetFormFields(ReadParameters(request, recorded), space, caller), false);
                case ("apps.json", "GET"):
                    return (GetApps(ReadParameters(request, recorded), space, caller), false);
                case ("records.json", "GET"):
                    return (GetRecordsApi(ReadParameters(request, recorded), space, caller), false);
                case ("records.json", "DELETE"):
                    return (DeleteRecordsApi(ReadParameters(request, recorded), space, caller), true);
                case ("record.json", "POST"):
                    return (AddRecordApi(ReadParameters(request, recorded), space, caller), true);
                case ("record.json", "PUT"):
                    return (UpdateRecordApi(ReadParameters(request, recorded), space, caller), true);
                case ("file.json", "POST"):
                    return (UploadFileApi(request, recorded), false);
                case ("file.json", "GET"):
                    return (DownloadFileApi(request, caller), false);
                default:
                    throw NotFoundApi(recorded);
            }
        }

        /// <summary>要求の主体 (パスワード認証の利用者か、API トークンの組)。</summary>
        sealed class Caller
        {
            public MockUser? User { get; init; }
            public List<(MockApiToken Token, MockApp App)> Tokens { get; } = new();
            public (string Code, string Name) Actor => User == null ? (AdministratorCode, AdministratorCode) : (User.Code, User.Name);
        }

        Caller Authenticate(MockRequest request, MockRecordedRequest recorded)
        {
            //パスワード認証が API トークンより先
            if (!string.IsNullOrWhiteSpace(request.CybozuAuthorization))
            {
                var login = DecodeLogin(request.CybozuAuthorization);
                var user = login == null ? null : Users.FirstOrDefault(e => string.Equals(e.Code, login.Value.Login, StringComparison.OrdinalIgnoreCase) && e.Password == login.Value.Password);
                if (user == null) throw new MockKintoneException(401, "CB_WA01", "ユーザーのパスワード認証に失敗しました。「X-Cybozu-Authorization」ヘッダーの値が正しくありません。");
                return new Caller { User = user };
            }
            if (recorded.ApiTokens.Count > 0)
            {
                if (recorded.ApiTokens.Count > MaxApiTokens) throw new MockKintoneException(400, "CB_IL02", $"APIトークンは {MaxApiTokens} 個まで指定できます。");
                var caller = new Caller();
                foreach (var value in recorded.ApiTokens)
                {
                    var found = _apps.SelectMany(a => a.Tokens.Select(t => (Token: t, App: a))).FirstOrDefault(e => e.Token.Token == value);
                    if (found.Token == null) throw new MockKintoneException(520, "GAIA_IA02", "指定したAPIトークンが正しくありません。");
                    if (caller.Tokens.Any(e => e.App == found.App)) throw new MockKintoneException(400, "GAIA_DA03", "同じアプリのAPIトークンを複数指定することはできません。");
                    caller.Tokens.Add(found);
                }
                return caller;
            }
            throw new MockKintoneException(401, "CB_AU01", "ログインしてください。");
        }

        /// <summary>アプリに permission のどれかの権限があるか (無ければ 403)。</summary>
        static void RequirePermission(Caller caller, MockApp app, MockPermission permission)
        {
            if (caller.User != null)
            {
                if ((caller.User.GetPermission(app.Id) & permission) == 0) throw new MockKintoneException(403, "CB_NO02", "権限がありません。");
                return;
            }
            var token = caller.Tokens.FirstOrDefault(e => e.App == app).Token;
            if (token == null || (token.Permission & permission) == 0) throw new MockKintoneException(403, "GAIA_NO01", "このAPIトークンでは、指定したAPIを実行できません。");
        }

        static bool CanView(Caller? caller, MockApp app)
        {
            if (caller == null) return true;
            if (caller.User != null) return (caller.User.GetPermission(app.Id) & MockPermission.View) != 0;
            var token = caller.Tokens.FirstOrDefault(e => e.App == app).Token;
            return token != null && (token.Permission & MockPermission.View) != 0;
        }

        /// <summary>パラメーターのアプリ。無い・スペースが違えば kintone の形のエラー。</summary>
        MockApp ResolveApp(long appId, long? space)
        {
            var app = FindApp(appId) ?? throw new MockKintoneException(404, "GAIA_AP01", $"指定したアプリ（id: {appId}）が見つかりません。削除されている可能性があります。");
            if (app.GuestSpaceId != space)
            {
                if (app.GuestSpaceId != null && space == null)
                    throw new MockKintoneException(400, "GAIA_IL23", "ゲストスペース内のアプリを操作する場合は、リクエストの送信先を「/k/guest/（ゲストスペースのID）/v1/...」にする必要があります。");
                throw new MockKintoneException(404, "GAIA_AP01", $"指定したアプリ（id: {appId}）が見つかりません。削除されている可能性があります。");
            }
            return app;
        }

        // ---------------------------------------------------------------- 日時

        static readonly string[] DateTimeFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        };

        /// <summary>ISO 8601 の日時 (Z か +09:00 / +0900 の時差が要る) を読む。</summary>
        public static bool TryParseDateTime(string text, out DateTimeOffset value)
        {
            var normalized = Regex.Replace(text, @"([+-]\d{2})(\d{2})$", "$1:$2");
            if (DateTimeOffset.TryParseExact(normalized, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            {
                value = value.ToUniversalTime();
                return true;
            }
            return false;
        }

        /// <summary>kintone の日時の値の形 (UTC・分まで・秒は 00)。</summary>
        public static string FormatDateTime(DateTimeOffset value)
        {
            var utc = value.ToUniversalTime();
            utc = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
