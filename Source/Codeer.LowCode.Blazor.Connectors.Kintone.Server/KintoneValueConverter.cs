using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// kintone のレコードの値 ({"type": ..., "value": ...} の value) と、列の値 (.NET の値) の読み替え。
    /// テーブル (SUBTABLE) は、列の値を行の JSON 配列の文字 (本体の列モードの一覧 = ListField の DbColumn) にする。
    /// 行のキーはフィールドコードそのまま、行の ID は <see cref="RowIdKey"/>。
    /// </summary>
    static class KintoneValueConverter
    {
        /// <summary>テーブルの行の JSON で、行の ID を入れるキー。</summary>
        internal const string RowIdKey = "$id";

        static readonly Regex NumberText = new(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);
        static readonly Regex DateText = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);
        static readonly Regex TimeText = new(@"^[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?$", RegexOptions.CultureInvariant);
        static readonly Regex DateTimeText = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant);
        static readonly string[] TimeFormats = { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" };

        // ---------------------------------------------------------------- 読み出し

        /// <summary>レコード (フィールドコード → {type, value}) → 列名と .NET の値 (定義にある列だけ)。</summary>
        internal static Dictionary<string, object?> ToValues(KintoneTableDefinition definition, JsonElement record)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in definition.Columns)
            {
                values[column.Name] = record.TryGetProperty(column.Name, out var field) && field.ValueKind == JsonValueKind.Object && field.TryGetProperty("value", out var value)
                    ? FromRecordValue(definition.Name, column, value)
                    : null;
            }
            return values;
        }

        /// <summary>
        /// kintone が返した値 → 列の種類の .NET の値。決まった書式だけを受け付け、読めない値は KintoneException (別の値や文字のままにしない)。
        /// 未入力 (null・""・空の配列) は null。日時は UTC の DateTime。計算フィールドの読めない値 (計算のエラーなど) は null。
        /// </summary>
        internal static object? FromRecordValue(string table, KintoneColumnDefinition column, JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0) return null;

            if (column.Kind == KintoneColumnKind.Json)
            {
                if (value.ValueKind != JsonValueKind.Array) throw Unreadable(table, column, value);
                if (value.GetArrayLength() == 0) return null;
                return column.IsSubtable ? ToRowsJson(table, column, value).ToJsonString(JsonOptions) : value.GetRawText();
            }

            //作成者・更新者は {code, name}。列の値はログイン名 (code)
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (column.Kind == KintoneColumnKind.String && value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                    return code.GetString()!.Length == 0 ? null : code.GetString();
                throw Unreadable(table, column, value);
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
            switch (column.Kind)
            {
                case KintoneColumnKind.String:
                    if (text != null) return text;
                    if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
                    break;
                case KintoneColumnKind.Number:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d)) return d;
                    if (text != null && NumberText.IsMatch(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                    break;
                case KintoneColumnKind.Date:
                    if (text != null && DateText.IsMatch(text) &&
                        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
                    break;
                case KintoneColumnKind.Time:
                    if (text != null && TimeText.IsMatch(text) &&
                        TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
                    break;
                case KintoneColumnKind.DateTime:
                    if (text != null && DateTimeText.IsMatch(text) &&
                        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)) return dateTime.UtcDateTime;
                    break;
            }
            //計算フィールドは、計算できなかったときに数値・日付でない文字を返す
            if (column.FieldType == "CALC") return null;
            throw Unreadable(table, column, value);
        }

        static KintoneException Unreadable(string table, KintoneColumnDefinition column, JsonElement value)
        {
            var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (100 < raw.Length) raw = raw[..100] + "...";
            var expected = column.Kind switch
            {
                KintoneColumnKind.Number => "a number",
                KintoneColumnKind.Date => "a date (yyyy-MM-dd)",
                KintoneColumnKind.Time => "a time (HH:mm)",
                KintoneColumnKind.DateTime => "a date and time (yyyy-MM-ddTHH:mm:ssZ)",
                KintoneColumnKind.Json => "an array",
                _ => "text",
            };
            return new KintoneException($"kintone returned a value that cannot be read as {expected} (app '{table}', field '{column.Name}'): '{raw}'.", KintoneErrorKind.Unknown);
        }

        static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        //テーブルの値 ([{id, value: {コード: {type, value}}}]) → 行の JSON 配列 ([{"$id": "17", "コード": 値}])。
        //数値は JSON の数値、日付・時刻・日時は kintone の文字のまま、配列の値 (チェックボックスなど) は配列のまま、未入力は null
        static JsonArray ToRowsJson(string table, KintoneColumnDefinition subtable, JsonElement rows)
        {
            var array = new JsonArray();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var result = new JsonObject();
                if (row.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    result[RowIdKey] = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
                if (row.TryGetProperty("value", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    foreach (var subField in subtable.SubFields ?? new())
                    {
                        if (!fields.TryGetProperty(subField.Name, out var field) || field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("value", out var value))
                        {
                            result[subField.Name] = null;
                            continue;
                        }
                        result[subField.Name] = ToRowNode(table, subField, value);
                    }
                }
                array.Add(result);
            }
            return array;
        }

        static JsonNode? ToRowNode(string table, KintoneColumnDefinition subField, JsonElement value)
        {
            if (subField.Kind == KintoneColumnKind.Json)
                return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() != 0 ? JsonNode.Parse(value.GetRawText()) : null;
            return FromRecordValue(table, subField, value) switch
            {
                null => null,
                decimal d => JsonValue.Create(d),
                DateOnly date => JsonValue.Create(KintoneQueryTranslator.FormatDate(date)),
                TimeOnly time => JsonValue.Create(KintoneQueryTranslator.FormatTime(time)),
                DateTime dateTime => JsonValue.Create(KintoneQueryTranslator.FormatDateTime(dateTime)),
                var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
            };
        }

        // ---------------------------------------------------------------- 書き込み

        /// <summary>
        /// 列の値 → レコードの本文のフィールド ({"value": ...})。null は未入力にする (文字・数値・日付は null、配列は [])。
        /// </summary>
        internal static JsonObject ToRecordField(string table, KintoneColumnDefinition column, object? value)
        {
            if (value is DBNull) value = null;
            return new JsonObject { ["value"] = column.Kind == KintoneColumnKind.Json ? ToArrayValue(table, column, value) : ToScalarNode(table, column, value) };
        }

        static KintoneException InvalidValue(string table, KintoneColumnDefinition column, object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (100 < text.Length) text = text[..100] + "...";
            return new KintoneException($"The value '{text}' cannot be written to kintone field '{column.Name}' ({column.FieldType}) of app '{table}'.", KintoneErrorKind.InvalidRequest);
        }

        static JsonNode? ToScalarNode(string table, KintoneColumnDefinition column, object? value)
        {
            if (value == null) return null;
            switch (column.Kind)
            {
                case KintoneColumnKind.Number:
                    {
                        var number = value switch
                        {
                            bool b => b ? 1m : 0m,
                            string s when s.Length == 0 => (decimal?)null,
                            string s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : throw InvalidValue(table, column, value),
                            _ => KintoneQueryTranslator.ToDecimal(value),
                        };
                        return number == null ? null : JsonValue.Create(KintoneQueryTranslator.NumberText(number.Value));
                    }
                case KintoneColumnKind.Date:
                case KintoneColumnKind.Time:
                case KintoneColumnKind.DateTime:
                    {
                        if (value is string { Length: 0 }) return null;
                        //クエリの値と同じ書式にする ("..." を外す)
                        var literal = KintoneQueryTranslator.LiteralFor(column, value) ?? throw InvalidValue(table, column, value);
                        return JsonValue.Create(literal[1..^1]);
                    }
                default:
                    return JsonValue.Create(value switch
                    {
                        bool b => b ? "1" : "0",
                        _ => KintoneQueryTranslator.KeyText(value),
                    });
            }
        }

        //配列の値のフィールド。列の値は JSON 配列の文字
        static JsonArray ToArrayValue(string table, KintoneColumnDefinition column, object? value)
        {
            var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return new JsonArray();
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                throw new KintoneException($"The value of kintone field '{column.Name}' of app '{table}' is not a JSON array.", KintoneErrorKind.InvalidRequest, e);
            }
            if (node == null) return new JsonArray();
            if (node is not JsonArray array) throw new KintoneException($"The value of kintone field '{column.Name}' of app '{table}' is not a JSON array.", KintoneErrorKind.InvalidRequest);

            if (column.IsSubtable) return ToSubtableValue(table, column, array);
            return ToSimpleArray(table, column, array);
        }

        //チェックボックス・複数選択は文字の配列、ユーザー・組織・グループの選択は [{code}] (文字か {code, ...} を受け付ける)
        static JsonArray ToSimpleArray(string table, KintoneColumnDefinition column, JsonArray array)
        {
            var result = new JsonArray();
            var isEntity = column.FieldType is "USER_SELECT" or "ORGANIZATION_SELECT" or "GROUP_SELECT";
            foreach (var element in array)
            {
                string? text = null;
                if (element is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String) text = scalar.GetValue<string>();
                else if (isEntity && element is JsonObject entity && entity["code"] is JsonValue code && code.GetValueKind() == JsonValueKind.String) text = code.GetValue<string>();
                if (string.IsNullOrEmpty(text)) throw InvalidValue(table, column, element?.ToJsonString());
                result.Add(isEntity ? new JsonObject { ["code"] = text } : JsonValue.Create(text));
            }
            return result;
        }

        /// <summary>
        /// 行の JSON 配列 → テーブルの値。行は ID ($id) があればその行 (送った項目だけ変わる)、無ければ新しい行。配列に無い今の行は kintone が削除する。
        /// 行の項目は定義にある書き込める項目だけを送る (計算・ルックアップのコピー先は送らない。定義に無いキーも送らない)。
        /// </summary>
        static JsonArray ToSubtableValue(string table, KintoneColumnDefinition subtable, JsonArray rows)
        {
            var result = new JsonArray();
            foreach (var element in rows)
            {
                if (element is not JsonObject row)
                    throw new KintoneException($"A row of kintone table '{subtable.Name}' of app '{table}' is not a JSON object.", KintoneErrorKind.InvalidRequest);
                var item = new JsonObject();
                if (row[RowIdKey] is JsonValue id)
                {
                    var idText = id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : id.ToJsonString();
                    if (!string.IsNullOrEmpty(idText)) item["id"] = idText;
                }
                var fields = new JsonObject();
                foreach (var subField in subtable.SubFields ?? new())
                {
                    if (subField.IsReadOnly || !row.ContainsKey(subField.Name)) continue;
                    fields[subField.Name] = new JsonObject { ["value"] = ToRowValue(table, subField, row[subField.Name]) };
                }
                item["value"] = fields;
                result.Add(item);
            }
            return result;
        }

        static JsonNode? ToRowValue(string table, KintoneColumnDefinition subField, JsonNode? node)
        {
            if (subField.Kind == KintoneColumnKind.Json)
            {
                if (node == null) return new JsonArray();
                if (node is not JsonArray array) throw InvalidValue(table, subField, node.ToJsonString());
                return ToSimpleArray(table, subField, array);
            }
            if (node == null) return null;
            if (node is not JsonValue scalar) throw InvalidValue(table, subField, node.ToJsonString());
            object? value = scalar.GetValueKind() switch
            {
                JsonValueKind.String => scalar.GetValue<string>(),
                JsonValueKind.Number => decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw InvalidValue(table, subField, scalar.ToJsonString()),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
            return ToScalarNode(table, subField, value);
        }
    }
}
