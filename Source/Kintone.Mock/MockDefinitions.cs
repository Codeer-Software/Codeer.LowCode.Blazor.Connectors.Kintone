using System.Text.Json.Nodes;

namespace Kintone.Mock
{
    /// <summary>フィールドの種類 (kintone の type の文字)。</summary>
    public static class MockFieldTypes
    {
        public const string SingleLineText = "SINGLE_LINE_TEXT";
        public const string MultiLineText = "MULTI_LINE_TEXT";
        public const string RichText = "RICH_TEXT";
        public const string Number = "NUMBER";
        public const string Calc = "CALC";
        public const string RadioButton = "RADIO_BUTTON";
        public const string DropDown = "DROP_DOWN";
        public const string CheckBox = "CHECK_BOX";
        public const string MultiSelect = "MULTI_SELECT";
        public const string Date = "DATE";
        public const string Time = "TIME";
        public const string DateTime = "DATETIME";
        public const string Link = "LINK";
        public const string UserSelect = "USER_SELECT";
        public const string File = "FILE";
        public const string Subtable = "SUBTABLE";
        public const string RecordNumber = "RECORD_NUMBER";
        public const string Creator = "CREATOR";
        public const string CreatedTime = "CREATED_TIME";
        public const string Modifier = "MODIFIER";
        public const string UpdatedTime = "UPDATED_TIME";
        public const string Status = "STATUS";
        public const string StatusAssignee = "STATUS_ASSIGNEE";
        public const string Category = "CATEGORY";

        /// <summary>レコードの $id / $revision の type。</summary>
        public const string Id = "__ID__";
        public const string Revision = "__REVISION__";

        /// <summary>利用者が定義できる (システムでない) 種類。</summary>
        public static readonly IReadOnlySet<string> UserTypes = new HashSet<string>
        {
            SingleLineText, MultiLineText, RichText, Number, Calc, RadioButton, DropDown, CheckBox, MultiSelect,
            Date, Time, DateTime, Link, UserSelect, File, Subtable,
        };

        /// <summary>どのアプリにもある種類と、既定のフィールドコード (定義に同じ種類があれば、そのコードを使う)。</summary>
        public static readonly IReadOnlyList<(string Type, string Code)> SystemTypes = new[]
        {
            (RecordNumber, "レコード番号"),
            (Creator, "作成者"),
            (CreatedTime, "作成日時"),
            (Modifier, "更新者"),
            (UpdatedTime, "更新日時"),
            (Status, "ステータス"),
            (StatusAssignee, "作業者"),
            (Category, "カテゴリー"),
        };

        public static bool IsSystem(string type) => SystemTypes.Any(e => e.Type == type);

        public static bool HasOptions(string type) => type is RadioButton or DropDown or CheckBox or MultiSelect;
    }

    /// <summary>API トークン・利用者の権限。</summary>
    [Flags]
    public enum MockPermission
    {
        None = 0,
        View = 1,
        Add = 2,
        Edit = 4,
        Delete = 8,
        All = View | Add | Edit | Delete,
    }

    /// <summary>モックの利用者 (パスワード認証で使う)。</summary>
    public sealed class MockUser
    {
        /// <summary>ログイン名 (ユーザーのコード)。</summary>
        public string Code { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// <summary>表示名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>アプリ ID → 権限。null ならすべてのアプリに全部の権限。</summary>
        public Dictionary<long, MockPermission>? Permissions { get; set; }

        public MockPermission GetPermission(long appId)
            => Permissions == null ? MockPermission.All : Permissions.TryGetValue(appId, out var p) ? p : MockPermission.None;
    }

    /// <summary>アプリの API トークン。</summary>
    public sealed class MockApiToken
    {
        public string Token { get; set; } = string.Empty;
        public MockPermission Permission { get; set; } = MockPermission.View;
    }

    /// <summary>ルックアップの設定。</summary>
    public sealed class MockLookup
    {
        /// <summary>参照元のアプリ ID。</summary>
        public long RelatedApp { get; set; }

        /// <summary>参照元のアプリでキーにするフィールド。</summary>
        public string RelatedKeyField { get; set; } = string.Empty;

        /// <summary>コピー先 (このアプリのフィールド) → コピー元 (参照元のアプリのフィールド)。</summary>
        public List<KeyValuePair<string, string>> FieldMappings { get; } = new();
    }

    /// <summary>フォームのフィールド 1 つの定義。</summary>
    public sealed class MockField
    {
        public string Code { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public bool Required { get; set; }
        public bool Unique { get; set; }

        /// <summary>選択肢 (ラジオボタン・ドロップダウン・チェックボックス・複数選択)。並びが index。</summary>
        public List<string> Options { get; } = new();

        /// <summary>既定値 (値の形。文字・文字の配列・ユーザーのコードの配列)。</summary>
        public JsonNode? DefaultValue { get; set; }

        /// <summary>日付・時刻・日時で、作成時の今を既定値にする。</summary>
        public bool DefaultNowValue { get; set; }

        /// <summary>計算式 (計算フィールド・自動計算の文字列 1 行)。</summary>
        public string? Expression { get; set; }

        /// <summary>計算フィールドの表示形式 (モックは NUMBER だけ計算する)。</summary>
        public string Format { get; set; } = "NUMBER";

        /// <summary>リンクの種類 (WEB / CALL / MAIL)。</summary>
        public string Protocol { get; set; } = "WEB";

        /// <summary>ルックアップならその設定。</summary>
        public MockLookup? Lookup { get; set; }

        /// <summary>テーブルの中のフィールド。</summary>
        public List<MockField> Fields { get; } = new();

        public bool IsSystem => MockFieldTypes.IsSystem(Type);

        /// <summary>自動計算の文字列 1 行。</summary>
        public bool IsAutoCalcText => Type == MockFieldTypes.SingleLineText && !string.IsNullOrEmpty(Expression);
    }

    /// <summary>kintone のアプリ 1 つ (フォームの定義・トークン・レコード)。</summary>
    public sealed class MockApp
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>アプリコード (レコード番号の接頭辞)。無ければ空。</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>ゲストスペースのアプリなら、そのスペースの ID。</summary>
        public long? GuestSpaceId { get; set; }

        /// <summary>フォームのフィールド (システムのフィールドも含む・並びがフォームの順)。</summary>
        public List<MockField> Fields { get; } = new();

        /// <summary>プロセス管理のステータス (最初が作成時のステータス)。空ならプロセス管理は無効。</summary>
        public List<string> Statuses { get; } = new();

        public bool ProcessManagementEnabled => Statuses.Count > 0;

        public List<MockApiToken> Tokens { get; } = new();

        /// <summary>フォームの設定の版 (fields.json の revision)。</summary>
        public long FormRevision { get; set; } = 1;

        internal List<StoredRecord> Records { get; } = new();
        internal long NextRecordId { get; set; } = 1;

        /// <summary>フィールドコードからフィールド (テーブルの中も探す)。Table はテーブルの中ならそのテーブル。</summary>
        public (MockField Field, MockField? Table)? FindField(string code)
        {
            foreach (var field in Fields)
            {
                if (field.Code == code) return (field, null);
                foreach (var inner in field.Fields)
                {
                    if (inner.Code == code) return (inner, field);
                }
            }
            return null;
        }

        /// <summary>システムのフィールド (種類で)。</summary>
        public MockField GetSystemField(string type) => Fields.First(e => e.Type == type);

        /// <summary>このアプリのフィールドすべて (テーブルの中も)。</summary>
        public IEnumerable<MockField> AllFields() => Fields.SelectMany(e => new[] { e }.Concat(e.Fields));
    }

    /// <summary>保存したレコード。値はレコードの value の形 (テーブルは List&lt;StoredRow&gt;)。</summary>
    sealed class StoredRecord
    {
        public long Id { get; set; }
        public long Revision { get; set; }
        public Dictionary<string, object?> Values { get; set; } = new(StringComparer.Ordinal);

        public StoredRecord Clone() => new()
        {
            Id = Id,
            Revision = Revision,
            Values = Values.ToDictionary(e => e.Key, e => e.Value switch
            {
                List<StoredRow> rows => (object?)rows.Select(r => r.Clone()).ToList(),
                JsonNode node => node.DeepClone(),
                var other => other,
            }, StringComparer.Ordinal),
        };
    }

    /// <summary>テーブルの行。</summary>
    sealed class StoredRow
    {
        public long Id { get; set; }
        public Dictionary<string, JsonNode?> Values { get; set; } = new(StringComparer.Ordinal);

        public StoredRow Clone() => new() { Id = Id, Values = Values.ToDictionary(e => e.Key, e => e.Value?.DeepClone(), StringComparer.Ordinal) };
    }
}
