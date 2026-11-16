using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    enum KintoneColumnKind
    {
        String,
        /// <summary>数値 (.NET では decimal)。</summary>
        Number,
        Date,
        Time,
        /// <summary>日時 (UTC の瞬間)。</summary>
        DateTime,
        /// <summary>値が配列のフィールド (複数選択・ユーザー選択・添付・テーブルなど)。列の値は JSON 配列の文字。</summary>
        Json,
    }

    /// <summary>kintone のクエリで使える演算子の組 (フィールドの種類で決まる)。</summary>
    enum KintoneQueryKind
    {
        /// <summary>条件に書けない (テーブル・添付の中身など)。</summary>
        None,
        /// <summary>文字列 1 行・リンク: = != in not in like not like。</summary>
        Text,
        /// <summary>数値・計算・レコード番号・$id: = != &gt; &lt; &gt;= &lt;= in not in。</summary>
        Number,
        /// <summary>日付・時刻・日時: = != &gt; &lt; &gt;= &lt;=。</summary>
        DateTime,
        /// <summary>文字列複数行: like / not like / is empty / is not empty。</summary>
        LongText,
        /// <summary>リッチエディター: like だけ (空かどうかを書けないので not like は使わない)。</summary>
        RichText,
        /// <summary>選択 (ラジオ・ドロップダウン・チェックボックス・複数選択・ステータス・ユーザー): in / not in。</summary>
        Selection,
    }

    class KintoneColumnDefinition
    {
        /// <summary>フィールドコード (モジュールの DbColumn)。レコードの ID と版は $id / $revision。</summary>
        public string Name { get; init; } = string.Empty;
        public KintoneColumnKind Kind { get; init; }
        /// <summary>kintone のフィールドの種類 (SINGLE_LINE_TEXT など。$id は __ID__、$revision は __REVISION__)。</summary>
        public string FieldType { get; init; } = string.Empty;
        /// <summary>kintone の画面の項目名。</summary>
        public string Label { get; init; } = string.Empty;
        /// <summary>書き込めない列 (計算・自動計算・ルックアップのコピー先・レコード番号・作成者など)。</summary>
        public bool IsReadOnly { get; init; }
        /// <summary>必須。</summary>
        public bool IsRequired { get; init; }
        /// <summary>重複禁止。</summary>
        public bool IsUnique { get; init; }
        /// <summary>ルックアップのフィールド (保存のときに kintone が参照元からコピーする)。</summary>
        public bool IsLookup { get; init; }
        /// <summary>選択肢 (ラジオ・ドロップダウン・チェックボックス・複数選択)。画面の順。無ければ null。</summary>
        public List<string>? Options { get; init; }
        /// <summary>テーブルの中のフィールド。テーブルでなければ null。</summary>
        public List<KintoneColumnDefinition>? SubFields { get; init; }

        public bool IsSubtable => FieldType == KintoneTableDefinition.SubtableType;

        public KintoneColumnDefinition? FindSubField(string code) => SubFields?.FirstOrDefault(e => e.Name == code);

        /// <summary>値が空になりえない列 (空かどうかの条件を書かない・書けない)。</summary>
        public bool IsNeverEmpty => FieldType is KintoneTableDefinition.IdType or KintoneTableDefinition.RevisionType or "RECORD_NUMBER" or "CREATED_TIME" or "UPDATED_TIME" or "CREATOR" or "MODIFIER";

        /// <summary>モジュールの Id にして、その値で更新・削除の対象を決められる列か ($id か、重複禁止の文字列 1 行・数値)。</summary>
        public bool CanBeRecordKey => FieldType == KintoneTableDefinition.IdType || (IsUnique && FieldType is "SINGLE_LINE_TEXT" or "NUMBER");

        public KintoneQueryKind QueryKind => FieldType switch
        {
            KintoneTableDefinition.IdType or "RECORD_NUMBER" or "NUMBER" or "CALC" => KintoneQueryKind.Number,
            "SINGLE_LINE_TEXT" or "LINK" => KintoneQueryKind.Text,
            "DATE" or "TIME" or "DATETIME" or "CREATED_TIME" or "UPDATED_TIME" => KintoneQueryKind.DateTime,
            "MULTI_LINE_TEXT" => KintoneQueryKind.LongText,
            "RICH_TEXT" => KintoneQueryKind.RichText,
            "RADIO_BUTTON" or "DROP_DOWN" or "CHECK_BOX" or "MULTI_SELECT" or "STATUS" or "STATUS_ASSIGNEE" or "CATEGORY" or
            "CREATOR" or "MODIFIER" or "USER_SELECT" or "ORGANIZATION_SELECT" or "GROUP_SELECT" => KintoneQueryKind.Selection,
            _ => KintoneQueryKind.None,
        };
    }

    /// <summary>
    /// 表 (kintone のアプリ) 1 つの定義。フォームの設定 (app/form/fields.json) から作る。
    /// </summary>
    class KintoneTableDefinition
    {
        /// <summary>レコード ID の列。</summary>
        internal const string IdColumn = "$id";
        /// <summary>レコードの版 (リビジョン) の列。</summary>
        internal const string RevisionColumn = "$revision";
        internal const string IdType = "__ID__";
        internal const string RevisionType = "__REVISION__";
        internal const string SubtableType = "SUBTABLE";

        public string Name { get; init; } = string.Empty;
        public List<KintoneColumnDefinition> Columns { get; } = new();

        /// <summary>主キーの列 ($id)。</summary>
        public IReadOnlyList<string> KeyColumns { get; } = new[] { IdColumn };

        public KintoneColumnDefinition? FindColumn(string name) => Columns.FirstOrDefault(e => e.Name == name);

        public DbTableDefinition ToTableDefinition()
        {
            var table = new DbTableDefinition { Name = Name };
            foreach (var column in Columns)
            {
                var (netType, rawType) = column.Kind switch
                {
                    KintoneColumnKind.Number => (typeof(decimal), "numeric"),
                    KintoneColumnKind.Date => (typeof(DateOnly), "date"),
                    KintoneColumnKind.Time => (typeof(TimeOnly), "time"),
                    //UTC の瞬間を持つ列型の名前にしておく (デザイナが DateTimeField の SaveAsUtc を既定でオンにする)
                    KintoneColumnKind.DateTime => (typeof(DateTime), "timestamp with time zone"),
                    KintoneColumnKind.Json => (typeof(string), "text"),
                    _ => (typeof(string), "varchar"),
                };
                table.Columns.Add(new DbColumnDefinition
                {
                    Name = column.Name,
                    NetTypeFullName = netType.FullName!,
                    RawDbTypeName = rawType,
                    //必須の項目も null を許す列として見せる。NOT NULL にすると、本体が空の値を型の既定値 (0・0001-01-01) に置き換えて書いてしまう。
                    //必須かどうかは kintone が保存のときに確かめる
                    IsNullable = true,
                });
            }
            return table;
        }

        /// <summary>本体が SQL に書けない名前 (表は . を含むと スキーマ.表 と読まれる。" は識別子を閉じてしまう)。</summary>
        internal static bool IsUsableTableName(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('.') && !name.Contains('"');
        internal static bool IsUsableColumnName(string name) => !string.IsNullOrEmpty(name) && !name.Contains('"');

        // ---------------------------------------------------------------- フォームの設定

        /// <summary>フィールドの種類 → 列の種類。列にしない種類 (関連レコード一覧・グループ・ラベルなど) は null。</summary>
        internal static KintoneColumnKind? GetKind(string fieldType, string? calcFormat)
        {
            switch (fieldType)
            {
                case "SINGLE_LINE_TEXT":
                case "MULTI_LINE_TEXT":
                case "RICH_TEXT":
                case "LINK":
                case "RADIO_BUTTON":
                case "DROP_DOWN":
                case "RECORD_NUMBER":
                case "CREATOR":
                case "MODIFIER":
                case "STATUS":
                    return KintoneColumnKind.String;
                case "NUMBER":
                    return KintoneColumnKind.Number;
                case "CALC":
                    return calcFormat switch
                    {
                        "DATETIME" => KintoneColumnKind.DateTime,
                        "DATE" => KintoneColumnKind.Date,
                        "TIME" => KintoneColumnKind.Time,
                        //時間 (HOUR_MINUTE)・日数と時間 (DAY_HOUR_MINUTE) は数値にも時刻にもならない文字
                        "HOUR_MINUTE" or "DAY_HOUR_MINUTE" => KintoneColumnKind.String,
                        _ => KintoneColumnKind.Number,
                    };
                case "DATE":
                    return KintoneColumnKind.Date;
                case "TIME":
                    return KintoneColumnKind.Time;
                case "DATETIME":
                case "CREATED_TIME":
                case "UPDATED_TIME":
                    return KintoneColumnKind.DateTime;
                case "CHECK_BOX":
                case "MULTI_SELECT":
                case "USER_SELECT":
                case "ORGANIZATION_SELECT":
                case "GROUP_SELECT":
                case "FILE":
                case "CATEGORY":
                case "STATUS_ASSIGNEE":
                case SubtableType:
                    return KintoneColumnKind.Json;
                default:
                    return null;
            }
        }

        //kintone が値を決める種類 (書き込めない)
        static bool IsSystemValue(string fieldType)
            => fieldType is "CALC" or "RECORD_NUMBER" or "CREATOR" or "MODIFIER" or "CREATED_TIME" or "UPDATED_TIME" or "STATUS" or "STATUS_ASSIGNEE" or "CATEGORY" or "FILE";

        /// <summary>
        /// フォームの設定 (app/form/fields.json の応答) を定義にする。先頭に $id と $revision を足す。
        /// </summary>
        internal static KintoneTableDefinition ParseFields(string tableName, JsonElement response)
        {
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                throw new KintoneException($"kintone: the form settings of app '{tableName}' could not be read (no 'properties').", KintoneErrorKind.Unknown);

            var table = new KintoneTableDefinition { Name = tableName };
            table.Columns.Add(new KintoneColumnDefinition { Name = IdColumn, Kind = KintoneColumnKind.Number, FieldType = IdType, Label = "ID", IsReadOnly = true });
            table.Columns.Add(new KintoneColumnDefinition { Name = RevisionColumn, Kind = KintoneColumnKind.Number, FieldType = RevisionType, Label = "Revision", IsReadOnly = true });

            //ルックアップのコピー先は書き込めない
            var copied = new HashSet<string>(StringComparer.Ordinal);
            CollectLookupDestinations(properties, copied);
            foreach (var property in properties.EnumerateObject())
            {
                var column = CreateColumn(property, copied, true);
                if (column != null && table.FindColumn(column.Name) == null) table.Columns.Add(column);
            }
            return table;
        }

        static void CollectLookupDestinations(JsonElement properties, HashSet<string> copied)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var field = property.Value;
                if (field.ValueKind != JsonValueKind.Object) continue;
                if (field.TryGetProperty("lookup", out var lookup) && lookup.ValueKind == JsonValueKind.Object &&
                    lookup.TryGetProperty("fieldMappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mapping in mappings.EnumerateArray())
                    {
                        if (mapping.ValueKind == JsonValueKind.Object && GetText(mapping, "field") is { Length: > 0 } code) copied.Add(code);
                    }
                }
                if (field.TryGetProperty("fields", out var inner) && inner.ValueKind == JsonValueKind.Object) CollectLookupDestinations(inner, copied);
            }
        }

        static KintoneColumnDefinition? CreateColumn(JsonProperty property, HashSet<string> copied, bool allowSubtable)
        {
            var field = property.Value;
            if (field.ValueKind != JsonValueKind.Object) return null;
            var code = GetText(field, "code") ?? property.Name;
            var type = GetText(field, "type") ?? string.Empty;
            if (!IsUsableColumnName(code) || code is IdColumn or RevisionColumn) return null;
            var kind = GetKind(type, GetText(field, "format"));
            if (kind == null) return null;
            if (type == SubtableType && !allowSubtable) return null;

            List<KintoneColumnDefinition>? subFields = null;
            if (type == SubtableType)
            {
                subFields = new();
                if (field.TryGetProperty("fields", out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in inner.EnumerateObject())
                    {
                        var subField = CreateColumn(child, copied, false);
                        if (subField != null && subFields.All(e => e.Name != subField.Name)) subFields.Add(subField);
                    }
                }
            }

            var isLookup = field.TryGetProperty("lookup", out var lookup) && lookup.ValueKind == JsonValueKind.Object;
            //文字列 1 行の自動計算 (expression がある) は kintone が値を決める
            var isAutoCalculated = type == "SINGLE_LINE_TEXT" && !string.IsNullOrEmpty(GetText(field, "expression"));
            return new KintoneColumnDefinition
            {
                Name = code,
                Kind = kind.Value,
                FieldType = type,
                Label = GetText(field, "label") ?? string.Empty,
                IsReadOnly = IsSystemValue(type) || isAutoCalculated || copied.Contains(code),
                IsRequired = GetBool(field, "required"),
                IsUnique = GetBool(field, "unique"),
                IsLookup = isLookup,
                Options = ReadOptions(field),
                SubFields = subFields,
            };
        }

        //選択肢 (options: { "ラベル": { "label": "ラベル", "index": "0" } })。index の順
        static List<string>? ReadOptions(JsonElement field)
        {
            if (!field.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object) return null;
            var list = new List<(string Label, long Index, int Position)>();
            var position = 0;
            foreach (var option in options.EnumerateObject())
            {
                var label = option.Value.ValueKind == JsonValueKind.Object ? GetText(option.Value, "label") ?? option.Name : option.Name;
                long index = position;
                if (option.Value.ValueKind == JsonValueKind.Object && GetText(option.Value, "index") is { } text && long.TryParse(text, out var parsed)) index = parsed;
                list.Add((label, index, position++));
            }
            return list.OrderBy(e => e.Index).ThenBy(e => e.Position).Select(e => e.Label).ToList();
        }

        static string? GetText(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        static bool GetBool(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return false;
            return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
