using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Kintone.Mock.MockFieldTypes;

namespace Kintone.Mock
{
    public sealed partial class MockKintone
    {
        // ---------------------------------------------------------------- パラメーター

        /// <summary>要求のパラメーター (JSON の本文か、クエリ文字列)。</summary>
        sealed class Parameters
        {
            readonly JsonObject? _body;
            readonly IReadOnlyList<KeyValuePair<string, string>> _query;

            public Parameters(JsonObject? body, IReadOnlyList<KeyValuePair<string, string>> query)
            {
                _body = body;
                _query = query;
            }

            public JsonNode? Node(string name) => _body?[name];

            public bool Has(string name) => _body != null
                ? _body.ContainsKey(name) && _body[name] != null
                : _query.Any(e => e.Key == name || e.Key.StartsWith(name + "[", StringComparison.Ordinal));

            public string? GetString(string name)
            {
                if (_body != null) return _body[name] is JsonValue v ? Text(v) : null;
                var found = _query.LastOrDefault(e => e.Key == name);
                return found.Key == null ? null : found.Value;
            }

            public long? GetLong(string name, bool required, bool allowMinusOne = false)
            {
                var node = _body?[name];
                if (_body != null && node is not (null or JsonValue)) throw MockKintoneException.Validation(name, "数字でなければなりません。");
                var text = GetString(name);
                if (string.IsNullOrEmpty(text))
                {
                    if (required) throw MockKintoneException.Validation(name, "必須です。");
                    return null;
                }
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || (value < 1 && !(allowMinusOne && value == -1)))
                    throw MockKintoneException.Validation(name, "数字でなければなりません。");
                return value;
            }

            public bool GetBool(string name)
            {
                if (_body != null)
                {
                    return _body[name] switch
                    {
                        null => false,
                        JsonValue v when v.TryGetValue<bool>(out var b) => b,
                        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var b) => b,
                        _ => throw MockKintoneException.Validation(name, "true か false を指定してください。"),
                    };
                }
                var text = GetString(name);
                if (string.IsNullOrEmpty(text)) return false;
                return bool.TryParse(text, out var parsed) ? parsed : throw MockKintoneException.Validation(name, "true か false を指定してください。");
            }

            /// <summary>配列 (本文なら JSON の配列、クエリなら name[0], name[1], ...)。無ければ null。</summary>
            public List<string>? GetArray(string name)
            {
                if (_body != null)
                {
                    var node = _body[name];
                    if (node == null) return null;
                    if (node is not JsonArray array) throw MockKintoneException.Validation(name, "配列で指定してください。");
                    var list = new List<string>();
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is not JsonValue v) throw MockKintoneException.Validation($"{name}[{i}]", "不正な値です。");
                        list.Add(Text(v));
                    }
                    return list;
                }
                var items = new SortedDictionary<int, string>();
                var pattern = new Regex("^" + Regex.Escape(name) + @"\[(\d+)\]$");
                foreach (var (key, value) in _query)
                {
                    if (key == name) throw MockKintoneException.Validation(name, $"{name}[0] のように添字を付けて指定してください。");
                    var match = pattern.Match(key);
                    if (!match.Success) continue;
                    var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (index > 99) throw MockKintoneException.Validation(key, "添字は 0 から 99 までです。");
                    items[index] = value;
                }
                return items.Count == 0 ? null : items.Values.ToList();
            }

            static string Text(JsonValue v) => v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        }

        static Parameters ReadParameters(MockRequest request, MockRecordedRequest recorded)
        {
            //素の GET はクエリ文字列だけ。POST + Override: GET・POST・PUT・DELETE (本文があるとき) は JSON の本文
            if (recorded.Method == "GET") return new Parameters(null, request.Query);
            if (string.IsNullOrEmpty(request.Body))
            {
                return recorded.Method == "DELETE" ? new Parameters(null, request.Query) : new Parameters(new JsonObject(), request.Query);
            }
            if (request.ContentType == null || !request.ContentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
                throw new MockKintoneException(400, "CB_IL02", "不正なリクエストです。本文が JSON の要求には Content-Type: application/json が要ります。");
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(request.Body);
            }
            catch (JsonException)
            {
                throw new MockKintoneException(400, "CB_IJ01", "不正なJSON文字列です。");
            }
            if (node is not JsonObject body) throw new MockKintoneException(400, "CB_IJ01", "不正なJSON文字列です。");
            return new Parameters(body, request.Query);
        }

        // ---------------------------------------------------------------- フォーム・アプリ

        MockResponse GetFormFields(Parameters parameters, long? space, Caller caller)
        {
            var app = ResolveApp(parameters.GetLong("app", true)!.Value, space);
            RequirePermission(caller, app, MockPermission.View | MockPermission.Add);
            var properties = new JsonObject();
            foreach (var field in app.Fields) properties[field.Code] = FieldToJson(app, field);
            return Json(200, new JsonObject
            {
                ["properties"] = properties,
                ["revision"] = app.FormRevision.ToString(CultureInfo.InvariantCulture),
            });
        }

        JsonObject FieldToJson(MockApp app, MockField field)
        {
            var o = new JsonObject
            {
                ["type"] = field.Type,
                ["code"] = field.Code,
                ["label"] = field.Label,
            };
            switch (field.Type)
            {
                case Status or StatusAssignee:
                    o["enabled"] = app.ProcessManagementEnabled;
                    return o;
                case Category:
                    o["enabled"] = false;
                    return o;
                case RecordNumber or Creator or CreatedTime or Modifier or UpdatedTime:
                    o["noLabel"] = false;
                    return o;
            }
            o["noLabel"] = false;
            if (field.Type == Subtable)
            {
                var inner = new JsonObject();
                foreach (var f in field.Fields) inner[f.Code] = FieldToJson(app, f);
                o["fields"] = inner;
                return o;
            }
            o["required"] = field.Required;
            if (field.Type is SingleLineText or Number or Link or Date or MockFieldTypes.DateTime) o["unique"] = field.Unique;
            switch (field.Type)
            {
                case SingleLineText:
                    o["minLength"] = "";
                    o["maxLength"] = "";
                    o["expression"] = field.Expression ?? "";
                    o["hideExpression"] = false;
                    o["defaultValue"] = DefaultText(field);
                    break;
                case Number:
                    o["minValue"] = "";
                    o["maxValue"] = "";
                    o["digit"] = false;
                    o["displayScale"] = "";
                    o["unit"] = "";
                    o["unitPosition"] = "BEFORE";
                    o["defaultValue"] = DefaultText(field);
                    break;
                case Calc:
                    o["expression"] = field.Expression ?? "";
                    o["format"] = field.Format;
                    o["displayScale"] = "";
                    o["hideExpression"] = false;
                    o["unit"] = "";
                    o["unitPosition"] = "BEFORE";
                    break;
                case MultiLineText or RichText:
                    o["defaultValue"] = DefaultText(field);
                    break;
                case Link:
                    o["protocol"] = field.Protocol;
                    o["minLength"] = "";
                    o["maxLength"] = "";
                    o["defaultValue"] = DefaultText(field);
                    break;
                case RadioButton or DropDown or CheckBox or MultiSelect:
                    var options = new JsonObject();
                    for (var i = 0; i < field.Options.Count; i++)
                        options[field.Options[i]] = new JsonObject { ["label"] = field.Options[i], ["index"] = i.ToString(CultureInfo.InvariantCulture) };
                    o["options"] = options;
                    o["defaultValue"] = field.Type is CheckBox or MultiSelect
                        ? field.DefaultValue?.DeepClone() ?? new JsonArray()
                        : DefaultText(field);
                    if (field.Type is RadioButton or CheckBox) o["align"] = "HORIZONTAL";
                    break;
                case Date or Time or MockFieldTypes.DateTime:
                    o["defaultValue"] = DefaultText(field);
                    o["defaultNowValue"] = field.DefaultNowValue;
                    break;
                case UserSelect:
                    o["entities"] = new JsonArray();
                    o["defaultValue"] = new JsonArray((field.DefaultValue as JsonArray ?? new JsonArray())
                        .Select(e => (JsonNode)new JsonObject { ["code"] = e is JsonObject x ? x["code"]?.GetValue<string>() : e?.GetValue<string>(), ["type"] = "USER" }).ToArray());
                    break;
                case MockFieldTypes.File:
                    o["thumbnailSize"] = "150";
                    break;
            }
            if (field.Lookup is { } lookup)
            {
                var source = FindApp(lookup.RelatedApp);
                o["lookup"] = new JsonObject
                {
                    ["relatedApp"] = new JsonObject { ["app"] = lookup.RelatedApp.ToString(CultureInfo.InvariantCulture), ["code"] = source?.Code ?? "" },
                    ["relatedKeyField"] = lookup.RelatedKeyField,
                    ["fieldMappings"] = new JsonArray(lookup.FieldMappings.Select(e => (JsonNode)new JsonObject { ["field"] = e.Key, ["relatedField"] = e.Value }).ToArray()),
                    ["lookupPickerFields"] = new JsonArray(),
                    ["filterCond"] = "",
                    ["sort"] = "",
                };
            }
            return o;
        }

        static string DefaultText(MockField field) => field.DefaultValue is JsonValue v && v.TryGetValue<string>(out var s) ? s : field.DefaultValue?.ToJsonString() ?? "";

        MockResponse GetApps(Parameters parameters, long? space, Caller caller)
        {
            var user = caller.User ?? throw new MockKintoneException(403, "GAIA_NO01", "このAPIトークンでは、指定したAPIを実行できません。");
            var limit = parameters.GetLong("limit", false) ?? 100;
            if (limit > 100) throw MockKintoneException.Validation("limit", "100 以下である必要があります。");
            var offsetText = parameters.GetString("offset");
            var offset = 0L;
            if (!string.IsNullOrEmpty(offsetText) && (!long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
                throw MockKintoneException.Validation("offset", "数字でなければなりません。");
            var apps = _apps.Where(e => e.GuestSpaceId == space && (user.GetPermission(e.Id) & MockPermission.View) != 0)
                .Skip((int)Math.Min(offset, int.MaxValue)).Take((int)limit)
                .Select(e => (JsonNode)new JsonObject
                {
                    ["appId"] = e.Id.ToString(CultureInfo.InvariantCulture),
                    ["code"] = e.Code,
                    ["name"] = e.Name,
                    ["description"] = "",
                    ["spaceId"] = e.GuestSpaceId?.ToString(CultureInfo.InvariantCulture),
                    ["threadId"] = e.GuestSpaceId?.ToString(CultureInfo.InvariantCulture),
                    ["createdAt"] = "2026-01-01T00:00:00.000Z",
                    ["creator"] = UserNode((AdministratorCode, AdministratorCode)),
                    ["modifiedAt"] = "2026-01-01T00:00:00.000Z",
                    ["modifier"] = UserNode((AdministratorCode, AdministratorCode)),
                }).ToArray();
            return Json(200, new JsonObject { ["apps"] = new JsonArray(apps) });
        }

        // ---------------------------------------------------------------- 読む

        MockResponse GetRecordsApi(Parameters parameters, long? space, Caller caller)
        {
            var app = ResolveApp(parameters.GetLong("app", true)!.Value, space);
            RequirePermission(caller, app, MockPermission.View);
            var fields = parameters.GetArray("fields");
            if (fields != null)
            {
                for (var i = 0; i < fields.Count; i++)
                {
                    var code = fields[i];
                    if (code is "$id" or "$revision") continue;
                    var found = app.FindField(code);
                    var visible = found != null && found.Value.Table == null && found.Value.Field.Type != Category
                        && (found.Value.Field.Type is not (Status or StatusAssignee) || app.ProcessManagementEnabled);
                    if (!visible) throw MockKintoneException.Validation($"fields[{i}]", found?.Table != null
                        ? $"テーブルの中のフィールド（{code}）は指定できません。テーブルのフィールドコードを指定してください。"
                        : $"指定したフィールド（{code}）が見つかりません。");
                }
            }
            var query = MockQuery.Parse(parameters.GetString("query"), app);
            var totalCount = parameters.GetBool("totalCount");
            var matched = query.FilterAndSort(app.Records);
            var records = matched.Skip(query.Offset).Take(query.Limit).Select(e => (JsonNode)RecordToJson(app, e, fields)).ToArray();
            return Json(200, new JsonObject
            {
                ["records"] = new JsonArray(records),
                ["totalCount"] = totalCount ? matched.Count.ToString(CultureInfo.InvariantCulture) : null,
            });
        }

        JsonObject RecordToJson(MockApp app, StoredRecord record, IReadOnlyCollection<string>? fields)
        {
            var o = new JsonObject();
            foreach (var field in app.Fields)
            {
                if (field.Type == Category) continue;
                if (field.Type is Status or StatusAssignee && !app.ProcessManagementEnabled) continue;
                if (fields != null && !fields.Contains(field.Code)) continue;
                JsonNode? value;
                if (field.Type == Subtable)
                {
                    var rows = record.Values.TryGetValue(field.Code, out var stored) && stored is List<StoredRow> list ? list : new List<StoredRow>();
                    value = new JsonArray(rows.Select(r =>
                    {
                        var inner = new JsonObject();
                        foreach (var f in field.Fields)
                            inner[f.Code] = new JsonObject { ["type"] = f.Type, ["value"] = (r.Values.TryGetValue(f.Code, out var v) ? v : EmptyValue(f.Type))?.DeepClone() };
                        return (JsonNode)new JsonObject { ["id"] = r.Id.ToString(CultureInfo.InvariantCulture), ["value"] = inner };
                    }).ToArray());
                }
                else value = StoredValue(app, field, record)?.DeepClone();
                o[field.Code] = new JsonObject { ["type"] = field.Type, ["value"] = value };
            }
            if (fields == null || fields.Contains("$revision")) o["$revision"] = new JsonObject { ["type"] = Revision, ["value"] = record.Revision.ToString(CultureInfo.InvariantCulture) };
            if (fields == null || fields.Contains("$id")) o["$id"] = new JsonObject { ["type"] = Id, ["value"] = record.Id.ToString(CultureInfo.InvariantCulture) };
            return o;
        }

        /// <summary>レコードの値を持つキー (システムのフィールドはコードでなく種類で持つ。コードを変えても値が付いて行くように)。</summary>
        internal static string ValueKey(MockField field) => field.IsSystem ? "@" + field.Type : field.Code;

        static readonly HashSet<string> SystemValueKeys = MockFieldTypes.SystemTypes.Select(e => "@" + e.Type).ToHashSet();

        static string FormatRecordNumber(MockApp app, long id)
            => app.Code.Length > 0 ? $"{app.Code}-{id.ToString(CultureInfo.InvariantCulture)}" : id.ToString(CultureInfo.InvariantCulture);

        static JsonNode? StoredValue(MockApp app, MockField field, StoredRecord record)
        {
            if (field.Type == RecordNumber) return FormatRecordNumber(app, record.Id);
            return record.Values.TryGetValue(ValueKey(field), out var value) && value is JsonNode node ? node : EmptyValue(field.Type);
        }

        /// <summary>未入力の値 (種類ごとの、実物が返す形)。</summary>
        internal static JsonNode? EmptyValue(string type) => type switch
        {
            Date or Time or DropDown or RadioButton => null,
            CheckBox or MultiSelect or UserSelect or MockFieldTypes.File or StatusAssignee or Category => new JsonArray(),
            Creator or Modifier => null,
            _ => "",
        };

        internal static string ScalarText(JsonNode? node) => node switch
        {
            null => string.Empty,
            JsonObject o => o["code"]?.GetValue<string>() ?? string.Empty,
            JsonArray a => string.Join(", ", a.Select(ScalarText)),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => string.Empty,
        };

        static bool IsEmptyValue(JsonNode? node) => node switch
        {
            null => true,
            JsonArray a => a.Count == 0,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0,
            _ => false,
        };

        static JsonObject UserNode((string Code, string Name) user) => new() { ["code"] = user.Code, ["name"] = user.Name };

        // ---------------------------------------------------------------- 消す

        MockResponse DeleteRecordsApi(Parameters parameters, long? space, Caller caller)
        {
            var app = ResolveApp(parameters.GetLong("app", true)!.Value, space);
            RequirePermission(caller, app, MockPermission.Delete);
            var ids = parameters.GetArray("ids");
            if (ids == null || ids.Count == 0) throw MockKintoneException.Validation("ids", "必須です。");
            if (ids.Count > 100) throw MockKintoneException.Validation("ids", "100 件まで指定できます。");
            var revisions = parameters.GetArray("revisions");
            if (revisions != null && revisions.Count != ids.Count) throw MockKintoneException.Validation("revisions", "ids と同じ数を指定してください。");
            var targets = new List<StoredRecord>();
            for (var i = 0; i < ids.Count; i++)
            {
                if (!long.TryParse(ids[i], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw MockKintoneException.Validation($"ids[{i}]", "数字でなければなりません。");
                long? revision = null;
                if (revisions != null)
                {
                    if (!long.TryParse(revisions[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) || (r < 1 && r != -1))
                        throw MockKintoneException.Validation($"revisions[{i}]", "数字でなければなりません。");
                    revision = r;
                }
                var record = app.Records.FirstOrDefault(e => e.Id == id) ?? throw RecordNotFound(id);
                if (revision is { } expected && expected != -1 && expected != record.Revision) throw RevisionConflict();
                targets.Add(record);
            }
            foreach (var record in targets) app.Records.Remove(record);
            return Json(200, new JsonObject());
        }

        static MockKintoneException RecordNotFound(long id) => new(404, "GAIA_RE01", $"指定したレコード（id: {id.ToString(CultureInfo.InvariantCulture)}）が見つかりません。");

        static MockKintoneException RevisionConflict() => new(409, "GAIA_CO02", "指定したrevisionは最新ではありません。ほかのユーザーがレコードを更新した可能性があります。");

        // ---------------------------------------------------------------- 足す・更新する

        MockResponse AddRecordApi(Parameters parameters, long? space, Caller caller)
        {
            var app = ResolveApp(parameters.GetLong("app", true)!.Value, space);
            RequirePermission(caller, app, MockPermission.Add);
            var record = RecordParameter(parameters);
            var built = BuildRecord(app, null, record, caller.Actor, caller, lenient: false);
            built.Id = app.NextRecordId++;
            app.Records.Add(built);
            return Json(200, new JsonObject
            {
                ["id"] = built.Id.ToString(CultureInfo.InvariantCulture),
                ["revision"] = built.Revision.ToString(CultureInfo.InvariantCulture),
            });
        }

        static JsonObject RecordParameter(Parameters parameters) => parameters.Node("record") switch
        {
            null => new JsonObject(),
            JsonObject o => o,
            _ => throw MockKintoneException.Validation("record", "不正な値です。"),
        };

        MockResponse UpdateRecordApi(Parameters parameters, long? space, Caller caller)
        {
            var app = ResolveApp(parameters.GetLong("app", true)!.Value, space);
            RequirePermission(caller, app, MockPermission.Edit);
            var hasId = parameters.Has("id");
            var hasKey = parameters.Has("updateKey");
            if (hasId && hasKey) throw MockKintoneException.Validation("id", "id と updateKey は同時に指定できません。");
            if (!hasId && !hasKey) throw MockKintoneException.Validation("id", "必須です。");
            StoredRecord existing;
            if (hasId)
            {
                var id = parameters.GetLong("id", true)!.Value;
                existing = app.Records.FirstOrDefault(e => e.Id == id) ?? throw RecordNotFound(id);
            }
            else existing = FindByUpdateKey(app, parameters.Node("updateKey"));
            var revision = parameters.GetLong("revision", false, allowMinusOne: true);
            if (revision is { } expected && expected != -1 && expected != existing.Revision) throw RevisionConflict();
            var updated = BuildRecord(app, existing, RecordParameter(parameters), caller.Actor, caller, lenient: false);
            updated.Revision = existing.Revision + 1;
            app.Records[app.Records.IndexOf(existing)] = updated;
            return Json(200, new JsonObject { ["revision"] = updated.Revision.ToString(CultureInfo.InvariantCulture) });
        }

        static StoredRecord FindByUpdateKey(MockApp app, JsonNode? node)
        {
            if (node is not JsonObject key) throw MockKintoneException.Validation("updateKey", "不正な値です。");
            var code = key["field"] is JsonValue f && f.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrEmpty(code)) throw MockKintoneException.Validation("updateKey.field", "必須です。");
            var field = app.Fields.FirstOrDefault(e => e.Code == code);
            if (field == null || !field.Unique || field.Type is not (SingleLineText or Number))
                throw MockKintoneException.Validation("updateKey.field", "重複禁止が設定された文字列（1行）か数値のフィールドを指定してください。");
            var value = key["value"] is JsonValue v ? ScalarText(v) : null;
            if (string.IsNullOrEmpty(value)) throw MockKintoneException.Validation("updateKey.value", "必須です。");
            return app.Records.FirstOrDefault(r => SameValue(field.Type, ScalarText(StoredValue(app, field, r)), value))
                ?? throw new MockKintoneException(404, "GAIA_RE01", $"updateKey に一致するレコード（{code} = {value}）が見つかりません。");
        }

        static bool SameValue(string type, string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return a.Length == b.Length;
            if (type is Number or Calc or RecordNumber
                && decimal.TryParse(a, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x)
                && decimal.TryParse(b, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var y)) return x == y;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        (string Code, string Name) ActorOf(string? userCode)
        {
            if (userCode == null || string.Equals(userCode, AdministratorCode, StringComparison.OrdinalIgnoreCase)) return (AdministratorCode, AdministratorCode);
            var user = Users.FirstOrDefault(e => string.Equals(e.Code, userCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The user '{userCode}' does not exist.");
            return (user.Code, user.Name);
        }

        /// <summary>データのファイル・テストの口からレコードを足す (値の書き方がゆるい・"$id" などの指定ができる・権限を見ない)。</summary>
        StoredRecord AddRecordCore(MockApp app, JsonObject record, string? userCode)
        {
            var built = BuildRecord(app, null, record, ActorOf(userCode), null, lenient: true);
            if (built.Id == 0) built.Id = app.NextRecordId;
            else if (app.Records.Any(e => e.Id == built.Id)) throw new InvalidOperationException($"The record id {built.Id} already exists.");
            app.NextRecordId = Math.Max(app.NextRecordId, built.Id + 1);
            app.Records.Add(built);
            return built;
        }

        /// <summary>
        /// 書き込み 1 件分の新しいレコードを作る (検証・既定値・テーブルの置き換え・ルックアップ・計算・作成者などのシステムの値)。
        /// existing が null なら新規。版は呼び出し側が決める (新規は 1)。lenient はデータのファイル・テストの口の書き方を受ける。
        /// </summary>
        StoredRecord BuildRecord(MockApp app, StoredRecord? existing, JsonObject record, (string Code, string Name) actor, Caller? caller, bool lenient)
        {
            var errors = new Dictionary<string, List<string>>();
            void AddError(string key, string message)
            {
                if (!errors.TryGetValue(key, out var list)) errors[key] = list = new List<string>();
                list.Add(message);
            }

            var create = existing == null;
            var result = existing?.Clone() ?? new StoredRecord { Revision = 1 };
            var now = Now;
            var sent = new HashSet<string>();
            var copyTargets = app.Fields.Where(e => e.Lookup != null).SelectMany(e => e.Lookup!.FieldMappings.Select(m => m.Key)).ToHashSet();
            var files = CollectFiles(existing);
            var systemOverrides = new Dictionary<string, JsonNode?>();

            if (create)
            {
                foreach (var field in app.Fields.Where(e => !e.IsSystem))
                    result.Values[field.Code] = field.Type == Subtable ? new List<StoredRow>() : DefaultValue(field, now);
            }

            foreach (var (code, property) in record)
            {
                if (code.StartsWith('$'))
                {
                    if (lenient) ApplySpecial(app, result, code, property, systemOverrides, create);
                    continue;
                }
                var field = app.Fields.FirstOrDefault(e => e.Code == code);
                if (field == null) continue; //知らないフィールドコードは無視 (実物と同じ)
                var key = $"record.{code}.value";
                if (!TryGetValue(property, lenient, out var value))
                {
                    if (property is not JsonObject) AddError(key, "不正な値です。");
                    continue;
                }
                if (field.IsSystem)
                {
                    if (create && field.Type is Creator or Modifier or CreatedTime or UpdatedTime)
                    {
                        var converted = ConvertSystem(field, value, lenient);
                        if (converted == null) AddError(key, field.Type is Creator or Modifier ? "ユーザーが見つかりません。" : "日時の形式が不正です。");
                        else systemOverrides[field.Type] = converted;
                    }
                    else AddError(key, "このフィールドの値は変更できません。");
                    continue;
                }
                if (field.Type == Calc || field.IsAutoCalcText)
                {
                    AddError(key, "計算・自動計算のフィールドには値を書き込めません。");
                    continue;
                }
                if (copyTargets.Contains(code))
                {
                    AddError(key, "ルックアップのコピー先のフィールドには値を書き込めません。");
                    continue;
                }
                if (field.Type == Subtable) MergeTable(field, result, value, key, AddError, now, files, lenient);
                else result.Values[code] = Convert(field, value, key, AddError, files, lenient, result.Values.GetValueOrDefault(code) as JsonNode);
                sent.Add(code);
            }
            //値の形の誤りがあっても、必須・重複禁止の誤りも一緒に返す (ルックアップと計算は形が正しいときだけ)
            if (errors.Count == 0)
            {
                foreach (var field in app.Fields.Where(e => e.Lookup != null && (create || sent.Contains(e.Code)))) ApplyLookup(app, field, result, caller);
                Recalculate(app, result);
            }

            foreach (var field in app.Fields.Where(e => !e.IsSystem && (create || sent.Contains(e.Code))))
            {
                if (field.Type == Subtable)
                {
                    var rows = (List<StoredRow>)result.Values[field.Code]!;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        foreach (var inner in field.Fields.Where(e => e.Required && e.Type != Calc && !e.IsAutoCalcText))
                        {
                            if (IsEmptyValue(rows[i].Values.GetValueOrDefault(inner.Code))) AddError($"record.{field.Code}.value[{i}].value.{inner.Code}.value", "必須です。");
                        }
                    }
                    continue;
                }
                var value = result.Values.GetValueOrDefault(field.Code) as JsonNode;
                if (field.Required && field.Type != Calc && !field.IsAutoCalcText && IsEmptyValue(value)) AddError($"record.{field.Code}.value", "必須です。");
                if (field.Unique && !IsEmptyValue(value))
                {
                    var text = ScalarText(value);
                    if (app.Records.Any(r => r.Id != existing?.Id && SameValue(field.Type, ScalarText(StoredValue(app, field, r)), text)))
                        AddError($"record.{field.Code}.value", "値がほかのレコードと重複しています。");
                }
            }
            if (errors.Count > 0) throw MockKintoneException.Validation(errors);

            if (create)
            {
                var creator = systemOverrides.GetValueOrDefault(Creator) ?? UserNode(actor);
                var created = systemOverrides.GetValueOrDefault(CreatedTime) ?? FormatDateTime(now);
                result.Values["@" + Creator] = creator;
                result.Values["@" + CreatedTime] = created;
                result.Values["@" + Modifier] = systemOverrides.GetValueOrDefault(Modifier) ?? creator.DeepClone();
                result.Values["@" + UpdatedTime] = systemOverrides.GetValueOrDefault(UpdatedTime) ?? created.DeepClone();
                if (!systemOverrides.ContainsKey(Status)) result.Values["@" + Status] = (JsonNode)(app.Statuses.FirstOrDefault() ?? "");
                if (!systemOverrides.ContainsKey(StatusAssignee)) result.Values["@" + StatusAssignee] = new JsonArray();
            }
            else
            {
                result.Values["@" + Modifier] = systemOverrides.GetValueOrDefault(Modifier) ?? UserNode(actor);
                result.Values["@" + UpdatedTime] = systemOverrides.GetValueOrDefault(UpdatedTime) ?? FormatDateTime(now);
            }
            foreach (var (type, value) in systemOverrides)
            {
                if (type is Status or StatusAssignee) result.Values["@" + type] = value;
            }
            return result;
        }

        //{"value": 値} から値を取り出す (lenient なら値そのものも受ける)。取り出せなければ false
        static bool TryGetValue(JsonNode? property, bool lenient, out JsonNode? value)
        {
            if (property is JsonObject o && o.TryGetPropertyValue("value", out value)) return true;
            if (lenient)
            {
                value = property;
                return true;
            }
            value = null;
            return false;
        }

        //データのファイル・テストの口の "$id" などの指定
        void ApplySpecial(MockApp app, StoredRecord result, string code, JsonNode? value, Dictionary<string, JsonNode?> overrides, bool create)
        {
            string Text() => value is JsonValue v ? ScalarText(v) : throw new InvalidOperationException($"'{code}' must be a string or a number.");
            switch (code)
            {
                case "$id" when create:
                    result.Id = long.TryParse(Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException("'$id' must be a positive integer.");
                    break;
                case "$revision" when create:
                    result.Revision = long.TryParse(Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0 ? revision : throw new InvalidOperationException("'$revision' must be a positive integer.");
                    break;
                case "$creator" or "$modifier":
                    overrides[code == "$creator" ? Creator : Modifier] = UserNode(ActorOf(Text()));
                    break;
                case "$createdTime" or "$updatedTime":
                    overrides[code == "$createdTime" ? CreatedTime : UpdatedTime] = TryParseDateTime(Text(), out var time) ? FormatDateTime(time) : throw new InvalidOperationException($"'{code}' must be an ISO 8601 date-time with Z or an offset.");
                    break;
                case "$status":
                    var status = Text();
                    if (!app.Statuses.Contains(status)) throw new InvalidOperationException($"'{status}' is not a status of the app {app.Id}.");
                    overrides[Status] = status;
                    break;
                case "$assignee":
                    if (value is not JsonArray array) throw new InvalidOperationException("'$assignee' must be an array of user codes.");
                    overrides[StatusAssignee] = new JsonArray(array.Select(e => (JsonNode)UserNode(ActorOf(ScalarText(e)))).ToArray());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown property '{code}' in the record.");
            }
        }

        JsonNode? ConvertSystem(MockField field, JsonNode? value, bool lenient)
        {
            if (field.Type is Creator or Modifier)
            {
                var code = value is JsonObject o ? o["code"]?.GetValue<string>() : lenient && value is JsonValue v ? ScalarText(v) : null;
                if (code == null) return null;
                if (string.Equals(code, AdministratorCode, StringComparison.OrdinalIgnoreCase)) return UserNode((AdministratorCode, AdministratorCode));
                var user = Users.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));
                return user == null ? null : UserNode((user.Code, user.Name));
            }
            return value is JsonValue t && t.TryGetValue<string>(out var text) && TryParseDateTime(text, out var time) ? FormatDateTime(time) : null;
        }

        static Dictionary<string, JsonObject> CollectFiles(StoredRecord? record)
        {
            var files = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (record == null) return files;
            void Add(JsonNode? node)
            {
                if (node is not JsonArray array) return;
                foreach (var file in array.OfType<JsonObject>())
                {
                    if (file["fileKey"]?.GetValue<string>() is { } key) files[key] = file;
                }
            }
            foreach (var value in record.Values.Values)
            {
                if (value is List<StoredRow> rows) foreach (var row in rows) foreach (var v in row.Values.Values) Add(v);
                else Add(value as JsonNode);
            }
            return files;
        }

        JsonNode? DefaultValue(MockField field, DateTimeOffset now)
        {
            if (field.DefaultNowValue)
            {
                switch (field.Type)
                {
                    case Date: return now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    case Time: return now.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                    case MockFieldTypes.DateTime: return FormatDateTime(now);
                }
            }
            if (field.DefaultValue == null || field.Lookup != null) return EmptyValue(field.Type);
            if (field.Type == UserSelect)
            {
                return new JsonArray(((JsonArray)field.DefaultValue).Select(e =>
                {
                    var code = e is JsonObject o ? o["code"]?.GetValue<string>() ?? "" : ScalarText(e);
                    var user = Users.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));
                    return (JsonNode)UserNode((code, user?.Name ?? code));
                }).ToArray());
            }
            if (field.Type is DropDown && ScalarText(field.DefaultValue).Length == 0) return null;
            return field.DefaultValue.DeepClone();
        }

        StoredRow NewRow(MockField table, DateTimeOffset now)
        {
            var row = new StoredRow { Id = _nextRowId++ };
            foreach (var inner in table.Fields) row.Values[inner.Code] = DefaultValue(inner, now);
            return row;
        }

        void MergeTable(MockField table, StoredRecord result, JsonNode? value, string key, Action<string, string> addError, DateTimeOffset now, Dictionary<string, JsonObject> files, bool lenient)
        {
            if (value is not JsonArray items)
            {
                addError(key, "テーブルの値は配列で指定してください。");
                return;
            }
            var existingRows = result.Values.GetValueOrDefault(table.Code) as List<StoredRow> ?? new List<StoredRow>();
            var used = new HashSet<long>();
            var rows = new List<StoredRow>();
            for (var i = 0; i < items.Count; i++)
            {
                var rowKey = $"{key}[{i}]";
                if (items[i] is not JsonObject item)
                {
                    addError(rowKey, "不正な値です。");
                    continue;
                }
                JsonObject? values;
                long? id = null;
                if (lenient && !item.ContainsKey("value") && !item.ContainsKey("id")) values = item;
                else
                {
                    if (item["id"] is { } idNode)
                    {
                        if (idNode is JsonValue v && long.TryParse(ScalarText(v), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) id = parsed;
                        else addError(rowKey + ".id", "数字でなければなりません。");
                    }
                    values = item["value"] switch
                    {
                        null => null,
                        JsonObject o => o,
                        _ => null,
                    };
                    if (item["value"] is not (null or JsonObject)) addError(rowKey + ".value", "不正な値です。");
                }
                //存在する行 ID なら、その行を残して送った列だけ変える。無い ID・ID なしは新しい行
                var match = id is { } rowId && used.Add(rowId) ? existingRows.FirstOrDefault(e => e.Id == rowId) : null;
                var row = match?.Clone() ?? NewRow(table, now);
                if (values != null)
                {
                    foreach (var (code, property) in values)
                    {
                        var inner = table.Fields.FirstOrDefault(e => e.Code == code);
                        if (inner == null) continue;
                        var innerKey = $"{rowKey}.value.{code}.value";
                        if (!TryGetValue(property, lenient, out var innerValue))
                        {
                            if (property is not JsonObject) addError(innerKey, "不正な値です。");
                            continue;
                        }
                        if (inner.Type == Calc || inner.IsAutoCalcText)
                        {
                            addError(innerKey, "計算・自動計算のフィールドには値を書き込めません。");
                            continue;
                        }
                        row.Values[code] = Convert(inner, innerValue, innerKey, addError, files, lenient, row.Values.GetValueOrDefault(code));
                    }
                }
                rows.Add(row);
            }
            result.Values[table.Code] = rows;
        }

        static readonly Regex NumberPattern = new(@"^-?\d+(\.\d+)?$");

        /// <summary>送られた値を、保存する値の形にする。誤りなら addError して current を返す。</summary>
        JsonNode? Convert(MockField field, JsonNode? value, string key, Action<string, string> addError, Dictionary<string, JsonObject> files, bool lenient, JsonNode? current)
        {
            JsonNode? Fail(string message)
            {
                addError(key, message);
                return current?.DeepClone();
            }

            string? text = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v => v.ToJsonString(),
                _ => null,
            };
            var isScalar = value is null or JsonValue;
            switch (field.Type)
            {
                case SingleLineText or MultiLineText or RichText or Link:
                    return isScalar ? text ?? "" : Fail("不正な値です。");
                case Number:
                    if (!isScalar) return Fail("数字でなければなりません。");
                    var number = text?.Trim() ?? "";
                    if (number.Length == 0) return "";
                    return NumberPattern.IsMatch(number) ? number : Fail("数字でなければなりません。");
                case Date:
                    if (!isScalar) return Fail("日付の形式が不正です。");
                    if (string.IsNullOrEmpty(text)) return null;
                    return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : Fail("日付の形式が不正です。");
                case Time:
                    if (!isScalar) return Fail("時刻の形式が不正です。");
                    if (string.IsNullOrEmpty(text)) return null;
                    return TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : Fail("時刻の形式が不正です。");
                case MockFieldTypes.DateTime:
                    if (!isScalar) return Fail("日時の形式が不正です。");
                    if (string.IsNullOrEmpty(text)) return "";
                    return TryParseDateTime(text, out var instant) ? FormatDateTime(instant) : Fail("日時の形式が不正です。");
                case DropDown:
                    if (!isScalar) return Fail("不正な値です。");
                    if (string.IsNullOrEmpty(text)) return null;
                    return field.Options.Contains(text) ? text : Fail($"「{text}」は選択肢にありません。");
                case RadioButton:
                    if (!isScalar) return Fail("不正な値です。");
                    if (string.IsNullOrEmpty(text)) return Fail("必須です。");
                    return field.Options.Contains(text) ? text : Fail($"「{text}」は選択肢にありません。");
                case CheckBox or MultiSelect:
                    {
                        if (value == null) return new JsonArray();
                        if (value is not JsonArray array) return Fail("配列で指定してください。");
                        var result = new JsonArray();
                        foreach (var item in array)
                        {
                            var option = item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                            if (option == null) return Fail("不正な値です。");
                            if (!field.Options.Contains(option)) return Fail($"「{option}」は選択肢にありません。");
                            result.Add(option);
                        }
                        return result;
                    }
                case UserSelect:
                    {
                        if (value == null) return new JsonArray();
                        if (value is not JsonArray array) return Fail("配列で指定してください。");
                        var result = new JsonArray();
                        foreach (var item in array)
                        {
                            var code = item is JsonObject o ? o["code"]?.GetValue<string>() : lenient && item is JsonValue v ? ScalarText(v) : null;
                            if (string.IsNullOrEmpty(code)) return Fail("不正な値です。");
                            if (string.Equals(code, AdministratorCode, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(UserNode((AdministratorCode, AdministratorCode)));
                                continue;
                            }
                            var user = Users.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));
                            if (user == null) return Fail($"ユーザー（{code}）が見つかりません。");
                            result.Add(UserNode((user.Code, user.Name)));
                        }
                        return result;
                    }
                case MockFieldTypes.File:
                    {
                        if (value == null) return new JsonArray();
                        if (value is not JsonArray array) return Fail("配列で指定してください。");
                        var result = new JsonArray();
                        foreach (var item in array)
                        {
                            if (item is not JsonObject o) return Fail("不正な値です。");
                            var fileKey = o["fileKey"]?.GetValue<string>();
                            if (fileKey != null && files.TryGetValue(fileKey, out var file)) result.Add(file.DeepClone());
                            else if (lenient && fileKey == null && o["name"]?.GetValue<string>() is { } name)
                            {
                                result.Add(new JsonObject
                                {
                                    ["contentType"] = o["contentType"]?.GetValue<string>() ?? "application/octet-stream",
                                    ["fileKey"] = $"mock-file-{_nextFileId++.ToString(CultureInfo.InvariantCulture)}",
                                    ["name"] = name,
                                    ["size"] = o["size"] is JsonValue size ? ScalarText(size) : "0",
                                });
                            }
                            else return Fail($"ファイル（fileKey: {fileKey}）が見つかりません。モックはファイルのアップロードに応えないので、今ある添付の fileKey だけを指定できます。");
                        }
                        return result;
                    }
                default:
                    return Fail("このフィールドの値は変更できません。");
            }
        }

        // ---------------------------------------------------------------- ルックアップ・計算

        void ApplyLookup(MockApp app, MockField field, StoredRecord record, Caller? caller)
        {
            var lookup = field.Lookup!;
            var keyText = ScalarText(record.Values.GetValueOrDefault(field.Code) as JsonNode);
            var source = FindApp(lookup.RelatedApp)!;
            if (keyText.Length == 0)
            {
                foreach (var (target, _) in lookup.FieldMappings) record.Values[target] = EmptyValue(app.FindField(target)!.Value.Field.Type);
                return;
            }
            var notFound = new MockKintoneException(400, "GAIA_LO04",
                $"フィールド「{field.Code}」の値「{keyText}」が、ルックアップの参照先のフィールドにないか、またはアプリやフィールドの閲覧権限がありません。");
            if (!CanView(caller, source)) throw notFound;
            var keyField = source.FindField(lookup.RelatedKeyField)!.Value.Field;
            var matches = source.Records.Where(r =>
            {
                if (keyField.Type == RecordNumber) return keyText == FormatRecordNumber(source, r.Id) || keyText == r.Id.ToString(CultureInfo.InvariantCulture);
                return SameValue(keyField.Type, ScalarText(StoredValue(source, keyField, r)), keyText);
            }).ToList();
            if (matches.Count == 0) throw notFound;
            if (matches.Count > 1) throw new MockKintoneException(400, "GAIA_LO05", $"フィールド「{field.Code}」の値「{keyText}」に合うレコードが、ルックアップの参照先のアプリに複数あります。");
            foreach (var (target, from) in lookup.FieldMappings)
            {
                var targetField = app.FindField(target)!.Value.Field;
                var sourceField = source.FindField(from)!.Value.Field;
                var value = StoredValue(source, sourceField, matches[0]);
                if (targetField.Type is CheckBox or MultiSelect or UserSelect or MockFieldTypes.File)
                {
                    record.Values[target] = value is JsonArray array ? array.DeepClone() : new JsonArray();
                    continue;
                }
                var text = ScalarText(value);
                record.Values[target] = text.Length == 0 ? EmptyValue(targetField.Type) : text;
            }
        }

        /// <summary>計算フィールド・自動計算の文字列を計算し直す (テーブルの行 → テーブルの外の順)。</summary>
        static void Recalculate(MockApp app, StoredRecord record)
        {
            string Top(string code)
            {
                var field = app.Fields.FirstOrDefault(e => e.Code == code);
                return field == null ? string.Empty : ScalarText(StoredValue(app, field, record));
            }

            foreach (var table in app.Fields.Where(e => e.Type == Subtable))
            {
                if (record.Values.GetValueOrDefault(table.Code) is not List<StoredRow> rows) continue;
                foreach (var row in rows)
                {
                    for (var pass = 0; pass < 3; pass++)
                    {
                        foreach (var inner in table.Fields.Where(e => !string.IsNullOrEmpty(e.Expression)))
                        {
                            row.Values[inner.Code] = Evaluate(inner, code => row.Values.TryGetValue(code, out var v) ? ScalarText(v) : Top(code), _ => Array.Empty<object?>());
                        }
                    }
                }
            }
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var field in app.Fields.Where(e => !string.IsNullOrEmpty(e.Expression)))
                {
                    record.Values[field.Code] = Evaluate(field, Top, code =>
                    {
                        var table = app.Fields.FirstOrDefault(t => t.Type == Subtable && t.Fields.Any(f => f.Code == code));
                        if (table == null || record.Values.GetValueOrDefault(table.Code) is not List<StoredRow> rows) return Array.Empty<object?>();
                        return rows.Select(r => (object?)ScalarText(r.Values.GetValueOrDefault(code))).ToList();
                    });
                }
            }
        }

        static JsonNode? Evaluate(MockField field, Func<string, string> value, Func<string, IReadOnlyList<object?>> column)
        {
            var expression = MockExpression.Parse(field.Expression!);
            try
            {
                var result = expression.Evaluate(code => value(code), column);
                return field.Type == Calc ? MockExpression.FormatNumber(MockExpression.ToNumber(result)) : MockExpression.ToText(result);
            }
            catch (Exception e) when (e is DivideByZeroException or FormatException or OverflowException)
            {
                return "";
            }
        }
    }
}
