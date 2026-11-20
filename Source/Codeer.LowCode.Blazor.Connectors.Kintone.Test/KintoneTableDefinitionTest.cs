using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>フォームの設定 (app/form/fields.json) → 表と列の定義。</summary>
    public class KintoneTableDefinitionTest
    {
        static KintoneTableDefinition Parse(string name, string properties)
        {
            using var document = JsonDocument.Parse("{\"properties\":" + properties + ",\"revision\":\"3\"}");
            return KintoneTableDefinition.ParseFields(name, document.RootElement);
        }

        static KintoneTableDefinition Customer => Parse("Customer", TestMetadata.Customer);
        static KintoneTableDefinition Order => Parse("Order", TestMetadata.Order);

        [Test]
        public void 先頭にIdと版の列を足しフォームの順に列にする()
        {
            var table = Customer;
            Assert.That(table.Name, Is.EqualTo("Customer"));
            Assert.That(table.KeyColumns, Is.EqualTo(new[] { "$id" }));
            Assert.That(table.Columns.Select(e => e.Name), Is.EqualTo(new[]
            {
                "$id", "$revision", "Code", "Name", "Note", "Body", "Url", "CreditLimit", "IsActive", "Deleted", "StartDate", "OpenTime", "LastContact",
                "Rank", "Tags", "Owner", "RegionCode", "Total", "Label", "RecordNo", "Creator", "Created", "Updated", "Status", "Attachments", "顧客 名",
            }));
            var id = table.FindColumn("$id")!;
            Assert.That((id.Kind, id.FieldType, id.IsReadOnly), Is.EqualTo((KintoneColumnKind.Number, "__ID__", true)));
            var revision = table.FindColumn("$revision")!;
            Assert.That((revision.Kind, revision.FieldType, revision.IsReadOnly), Is.EqualTo((KintoneColumnKind.Number, "__REVISION__", true)));
            Assert.That(table.FindColumn("Name")!.Label, Is.EqualTo("顧客名"));
        }

        [TestCase("SINGLE_LINE_TEXT", null, "String")]
        [TestCase("MULTI_LINE_TEXT", null, "String")]
        [TestCase("RICH_TEXT", null, "String")]
        [TestCase("LINK", null, "String")]
        [TestCase("RADIO_BUTTON", null, "String")]
        [TestCase("DROP_DOWN", null, "String")]
        [TestCase("RECORD_NUMBER", null, "String")]
        [TestCase("CREATOR", null, "String")]
        [TestCase("MODIFIER", null, "String")]
        [TestCase("STATUS", null, "String")]
        [TestCase("NUMBER", null, "Number")]
        [TestCase("CALC", null, "Number")]
        [TestCase("CALC", "NUMBER", "Number")]
        [TestCase("CALC", "NUMBER_DIGIT", "Number")]
        [TestCase("CALC", "DATETIME", "DateTime")]
        [TestCase("CALC", "DATE", "Date")]
        [TestCase("CALC", "TIME", "Time")]
        [TestCase("CALC", "HOUR_MINUTE", "String")]
        [TestCase("CALC", "DAY_HOUR_MINUTE", "String")]
        [TestCase("DATE", null, "Date")]
        [TestCase("TIME", null, "Time")]
        [TestCase("DATETIME", null, "DateTime")]
        [TestCase("CREATED_TIME", null, "DateTime")]
        [TestCase("UPDATED_TIME", null, "DateTime")]
        [TestCase("CHECK_BOX", null, "Json")]
        [TestCase("MULTI_SELECT", null, "Json")]
        [TestCase("USER_SELECT", null, "Json")]
        [TestCase("ORGANIZATION_SELECT", null, "Json")]
        [TestCase("GROUP_SELECT", null, "Json")]
        [TestCase("FILE", null, "Json")]
        [TestCase("CATEGORY", null, "Json")]
        [TestCase("STATUS_ASSIGNEE", null, "Json")]
        [TestCase("SUBTABLE", null, "Json")]
        public void 種類の対応(string type, string? format, string kind)
            => Assert.That(KintoneTableDefinition.GetKind(type, format)?.ToString(), Is.EqualTo(kind));

        [TestCase("REFERENCE_TABLE")]
        [TestCase("GROUP")]
        [TestCase("LABEL")]
        [TestCase("SPACER")]
        [TestCase("HR")]
        [TestCase("UNKNOWN_NEW_TYPE")]
        public void 列にしない種類(string type)
        {
            Assert.That(KintoneTableDefinition.GetKind(type, null), Is.Null);
            var table = Parse("T", $$$"""{"F":{"type":"{{{type}}}","code":"F"}}""");
            Assert.That(table.FindColumn("F"), Is.Null);
        }

        [Test]
        public void CALCの書式で列の種類が変わる()
        {
            var table = Parse("T", """
                {"N":{"type":"CALC","code":"N","format":"NUMBER_DIGIT"},"D":{"type":"CALC","code":"D","format":"DATE"},
                 "T":{"type":"CALC","code":"T","format":"TIME"},"DT":{"type":"CALC","code":"DT","format":"DATETIME"},"H":{"type":"CALC","code":"H","format":"HOUR_MINUTE"}}
                """);
            Assert.That(table.Columns.Skip(2).Select(e => (e.Name, e.Kind)), Is.EqualTo(new[]
            {
                ("N", KintoneColumnKind.Number), ("D", KintoneColumnKind.Date), ("T", KintoneColumnKind.Time), ("DT", KintoneColumnKind.DateTime), ("H", KintoneColumnKind.String),
            }));
            Assert.That(table.Columns.Skip(2).All(e => e.IsReadOnly), Is.True);
        }

        [Test]
        public void 読み取り専用の列()
        {
            var readOnly = Customer.Columns.Where(e => e.IsReadOnly).Select(e => e.Name);
            //Code は expression が空 (自動計算でない)。添付ファイル (Attachments) は添付のフィールドで書ける
            Assert.That(readOnly, Is.EqualTo(new[] { "$id", "$revision", "Total", "Label", "RecordNo", "Creator", "Created", "Updated", "Status" }));
        }

        [Test]
        public void ルックアップのコピー先は読み取り専用でルックアップのフィールドは書ける()
        {
            var order = Order;
            var lookup = order.FindColumn("CustomerCode")!;
            Assert.That((lookup.IsLookup, lookup.IsReadOnly), Is.EqualTo((true, false)));
            var copied = order.FindColumn("CustomerName")!;
            Assert.That((copied.IsLookup, copied.IsReadOnly), Is.EqualTo((false, true)));
        }

        [Test]
        public void テーブルの中のルックアップのコピー先も読み取り専用()
        {
            var table = Parse("T", """
                {"Rows":{"type":"SUBTABLE","code":"Rows","fields":{
                  "Key":{"type":"SINGLE_LINE_TEXT","code":"Key","lookup":{"relatedApp":{"app":"1"},"relatedKeyField":"Code","fieldMappings":[{"field":"Copied","relatedField":"Name"}]}},
                  "Copied":{"type":"SINGLE_LINE_TEXT","code":"Copied"},
                  "Free":{"type":"SINGLE_LINE_TEXT","code":"Free"}}}}
                """);
            var rows = table.FindColumn("Rows")!;
            Assert.That(rows.SubFields!.Select(e => (e.Name, e.IsReadOnly, e.IsLookup)), Is.EqualTo(new[] { ("Key", false, true), ("Copied", true, false), ("Free", false, false) }));
        }

        [Test]
        public void テーブルは行のフィールドを持つ()
        {
            var lines = Order.FindColumn("Lines")!;
            Assert.That(lines.IsSubtable, Is.True);
            Assert.That(lines.Kind, Is.EqualTo(KintoneColumnKind.Json));
            Assert.That(lines.QueryKind, Is.EqualTo(KintoneQueryKind.None));
            Assert.That(lines.SubFields!.Select(e => (e.Name, e.Kind, e.IsReadOnly)), Is.EqualTo(new[]
            {
                ("Product", KintoneColumnKind.String, false), ("Qty", KintoneColumnKind.Number, false), ("Price", KintoneColumnKind.Number, false),
                ("Amount", KintoneColumnKind.Number, true), ("ShipDate", KintoneColumnKind.Date, false), ("Options", KintoneColumnKind.Json, false),
            }));
            Assert.That(lines.FindSubField("Qty")!.Label, Is.EqualTo("数量"));
            Assert.That(lines.FindSubField("Nothing"), Is.Null);
            Assert.That(Order.FindColumn("OrderNo")!.SubFields, Is.Null);
        }

        [Test]
        public void テーブルの中のテーブルと列にしない種類は行のフィールドにしない()
        {
            var table = Parse("T", """
                {"Rows":{"type":"SUBTABLE","code":"Rows","fields":{
                  "Inner":{"type":"SUBTABLE","code":"Inner","fields":{}},
                  "Space":{"type":"SPACER","code":"Space"},
                  "A":{"type":"NUMBER","code":"A"}}},
                 "Empty":{"type":"SUBTABLE","code":"Empty"}}
                """);
            Assert.That(table.FindColumn("Rows")!.SubFields!.Select(e => e.Name), Is.EqualTo(new[] { "A" }));
            Assert.That(table.FindColumn("Empty")!.SubFields, Is.Empty);
        }

        [Test]
        public void 選択肢はindexの順()
        {
            var table = Customer;
            Assert.That(table.FindColumn("Rank")!.Options, Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That(table.FindColumn("Tags")!.Options, Is.EqualTo(new[] { "VIP", "新規" }));
            Assert.That(table.FindColumn("Name")!.Options, Is.Null);
        }

        [Test]
        public void 必須と重複禁止と文字のtrue()
        {
            var table = Customer;
            var code = table.FindColumn("Code")!;
            Assert.That((code.IsRequired, code.IsUnique), Is.EqualTo((true, true)));
            var name = table.FindColumn("Name")!;
            Assert.That((name.IsRequired, name.IsUnique), Is.EqualTo((false, false)));
            Assert.That(table.FindColumn("CreditLimit")!.IsUnique, Is.False);
            Assert.That(Parse("T", """{"N":{"type":"NUMBER","code":"N","unique":"true","required":"TRUE"}}""").FindColumn("N")!.IsUnique, Is.True);
        }

        [Test]
        public void レコードのキーにできる列()
        {
            var table = Parse("T", """
                {"Text":{"type":"SINGLE_LINE_TEXT","code":"Text","unique":true},"Num":{"type":"NUMBER","code":"Num","unique":true},
                 "Plain":{"type":"SINGLE_LINE_TEXT","code":"Plain"},"Long":{"type":"MULTI_LINE_TEXT","code":"Long","unique":true},
                 "Date":{"type":"DATE","code":"Date","unique":true},"No":{"type":"RECORD_NUMBER","code":"No"}}
                """);
            Assert.That(table.Columns.Where(e => e.CanBeRecordKey).Select(e => e.Name), Is.EqualTo(new[] { "$id", "Text", "Num" }));
        }

        [TestCase("$id", "Number")]
        [TestCase("$revision", "None")]
        [TestCase("Code", "Text")]
        [TestCase("Url", "Text")]
        [TestCase("CreditLimit", "Number")]
        [TestCase("Total", "Number")]
        [TestCase("RecordNo", "Number")]
        [TestCase("StartDate", "DateTime")]
        [TestCase("OpenTime", "DateTime")]
        [TestCase("LastContact", "DateTime")]
        [TestCase("Created", "DateTime")]
        [TestCase("Updated", "DateTime")]
        [TestCase("Note", "LongText")]
        [TestCase("Body", "RichText")]
        [TestCase("Rank", "Selection")]
        [TestCase("Tags", "Selection")]
        [TestCase("Owner", "Selection")]
        [TestCase("Creator", "Selection")]
        [TestCase("Status", "Selection")]
        [TestCase("Attachments", "None")]
        public void クエリで使える演算子の組(string column, string kind)
            => Assert.That(Customer.FindColumn(column)!.QueryKind.ToString(), Is.EqualTo(kind));

        [Test]
        public void 空になりえない列()
        {
            var never = Customer.Columns.Where(e => e.IsNeverEmpty).Select(e => e.Name);
            Assert.That(never, Is.EqualTo(new[] { "$id", "$revision", "RecordNo", "Creator", "Created", "Updated" }));
        }

        [Test]
        public void 本体に見せる列の型()
        {
            var columns = Customer.ToTableDefinition().Columns.ToDictionary(e => e.Name);
            Assert.That((columns["$id"].NetTypeFullName, columns["$id"].RawDbTypeName), Is.EqualTo((typeof(decimal).FullName, "numeric")));
            Assert.That((columns["Name"].NetTypeFullName, columns["Name"].RawDbTypeName), Is.EqualTo((typeof(string).FullName, "varchar")));
            Assert.That((columns["CreditLimit"].NetTypeFullName, columns["CreditLimit"].RawDbTypeName), Is.EqualTo((typeof(decimal).FullName, "numeric")));
            Assert.That((columns["StartDate"].NetTypeFullName, columns["StartDate"].RawDbTypeName), Is.EqualTo((typeof(DateOnly).FullName, "date")));
            Assert.That((columns["OpenTime"].NetTypeFullName, columns["OpenTime"].RawDbTypeName), Is.EqualTo((typeof(TimeOnly).FullName, "time")));
            Assert.That((columns["LastContact"].NetTypeFullName, columns["LastContact"].RawDbTypeName), Is.EqualTo((typeof(DateTime).FullName, "timestamp with time zone")));
            Assert.That((columns["Tags"].NetTypeFullName, columns["Tags"].RawDbTypeName), Is.EqualTo((typeof(string).FullName, "text")));
            //必須 (Code) も null を許す列として見せる
            Assert.That(columns.Values.All(e => e.IsNullable), Is.True);
            Assert.That(Order.ToTableDefinition().Columns.Single(e => e.Name == "Lines").RawDbTypeName, Is.EqualTo("text"));
        }

        [Test]
        public void SQLに書けない名前の列とIdと版の名前のフィールドは外す()
        {
            var table = Parse("T", """
                {"a\"b":{"type":"SINGLE_LINE_TEXT","code":"a\"b"},"$id":{"type":"SINGLE_LINE_TEXT","code":"$id"},"$revision":{"type":"NUMBER","code":"$revision"},
                 "ok.name":{"type":"SINGLE_LINE_TEXT","code":"ok.name"},"Key":{"type":"SINGLE_LINE_TEXT","code":"Code2"}}
                """);
            Assert.That(table.Columns.Select(e => (e.Name, e.FieldType)), Is.EqualTo(new[]
            {
                ("$id", "__ID__"), ("$revision", "__REVISION__"), ("ok.name", "SINGLE_LINE_TEXT"), ("Code2", "SINGLE_LINE_TEXT"),
            }));
            Assert.That(KintoneTableDefinition.IsUsableTableName("a.b"), Is.False);
            Assert.That(KintoneTableDefinition.IsUsableTableName("a\"b"), Is.False);
            Assert.That(KintoneTableDefinition.IsUsableTableName(" "), Is.False);
            Assert.That(KintoneTableDefinition.IsUsableTableName("受注"), Is.True);
        }

        [TestCase("{}")]
        [TestCase("[]")]
        [TestCase("{\"properties\":[]}")]
        public void propertiesが無ければエラー(string json)
        {
            using var document = JsonDocument.Parse(json);
            var e = Assert.Throws<KintoneException>(() => KintoneTableDefinition.ParseFields("T", document.RootElement))!;
            Assert.That(e.Message, Does.Contain("could not be read"));
        }
    }
}
