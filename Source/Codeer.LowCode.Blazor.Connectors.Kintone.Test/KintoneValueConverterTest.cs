using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>レコードの値 ⇔ 列の値 (テーブルの行の JSON を含む)。</summary>
    public class KintoneValueConverterTest
    {
        static KintoneTableDefinition Order
        {
            get
            {
                using var document = JsonDocument.Parse("{\"properties\":" + TestMetadata.Order + "}");
                return KintoneTableDefinition.ParseFields("Order", document.RootElement);
            }
        }

        static KintoneColumnDefinition Lines => Order.FindColumn("Lines")!;

        static KintoneColumnDefinition Column(string fieldType, string? format = null)
            => new() { Name = "F", FieldType = fieldType, Kind = KintoneTableDefinition.GetKind(fieldType, format)!.Value };

        static object? Read(KintoneColumnDefinition column, string json)
        {
            using var document = JsonDocument.Parse(json);
            return KintoneValueConverter.FromRecordValue("T", column, document.RootElement);
        }

        static object? Read(string fieldType, string json, string? format = null) => Read(Column(fieldType, format), json);

        static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        static string Write(KintoneColumnDefinition column, object? value) => KintoneValueConverter.ToRecordField("T", column, value).ToJsonString(Relaxed);

        static string Write(string fieldType, object? value) => Write(Column(fieldType), value);

        // ---------------------------------------------------------------- 読み出し

        [TestCase("SINGLE_LINE_TEXT", "null")]
        [TestCase("SINGLE_LINE_TEXT", "\"\"")]
        [TestCase("NUMBER", "\"\"")]
        [TestCase("NUMBER", "null")]
        [TestCase("DATE", "\"\"")]
        [TestCase("TIME", "null")]
        [TestCase("DATETIME", "\"\"")]
        [TestCase("CHECK_BOX", "[]")]
        [TestCase("USER_SELECT", "[]")]
        [TestCase("SUBTABLE", "[]")]
        [TestCase("CREATOR", "{\"code\":\"\",\"name\":\"\"}")]
        public void 未入力はnull(string fieldType, string json)
            => Assert.That(Read(fieldType, json), Is.Null);

        [Test]
        public void 文字の値()
        {
            Assert.That(Read("SINGLE_LINE_TEXT", "\"大阪 \\\"商事\\\"\""), Is.EqualTo("大阪 \"商事\""));
            Assert.That(Read("RECORD_NUMBER", "\"APP-12\""), Is.EqualTo("APP-12"));
            Assert.That(Read("RECORD_NUMBER", "12"), Is.EqualTo("12"));
            //作成者・更新者は {code, name} の code (ログイン名)
            Assert.That(Read("CREATOR", "{\"code\":\"sato\",\"name\":\"佐藤\"}"), Is.EqualTo("sato"));
            Assert.That(Read("MODIFIER", "{\"code\":\"suzuki\",\"name\":\"鈴木\"}"), Is.EqualTo("suzuki"));
        }

        [Test]
        public void 数値の値()
        {
            Assert.That(Read("NUMBER", "\"1234.5\""), Is.EqualTo(1234.5m));
            Assert.That(Read("NUMBER", "\"-0.001\""), Is.EqualTo(-0.001m));
            Assert.That(Read("NUMBER", "\"1.5E+3\""), Is.EqualTo(1500m));
            Assert.That(Read("NUMBER", "42"), Is.EqualTo(42m));
            Assert.That(Read("CALC", "\"12.25\""), Is.EqualTo(12.25m));
            //桁の多い数を double を経由せずに読む
            Assert.That(Read("NUMBER", "\"12345678901234567890.123456789\""), Is.EqualTo(12345678901234567890.123456789m));
        }

        [Test]
        public void 日付と時刻と日時の値()
        {
            Assert.That(Read("DATE", "\"2026-01-02\""), Is.EqualTo(new DateOnly(2026, 1, 2)));
            Assert.That(Read("TIME", "\"09:30\""), Is.EqualTo(new TimeOnly(9, 30)));
            Assert.That(Read("TIME", "\"23:59:58\""), Is.EqualTo(new TimeOnly(23, 59, 58)));

            var utc = (DateTime)Read("DATETIME", "\"2026-03-04T05:06:07Z\"")!;
            Assert.That(utc, Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc));
            //時差つきは UTC にする
            Assert.That(Read("DATETIME", "\"2026-03-04T14:06:07+09:00\""), Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)));
            Assert.That(Read("CREATED_TIME", "\"2026-03-04T05:06Z\""), Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc)));
            Assert.That(Read("CALC", "\"2026-03-04T05:06:00Z\"", "DATETIME"), Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc)));
            Assert.That(Read("CALC", "\"2026-03-04\"", "DATE"), Is.EqualTo(new DateOnly(2026, 3, 4)));
            Assert.That(Read("CALC", "\"12:30\"", "TIME"), Is.EqualTo(new TimeOnly(12, 30)));
            //時間 (HOUR_MINUTE) は文字のまま
            Assert.That(Read("CALC", "\"49時間30分\"", "HOUR_MINUTE"), Is.EqualTo("49時間30分"));
        }

        [TestCase("NUMBER", "\"1,234\"")]
        [TestCase("NUMBER", "\" 12\"")]
        [TestCase("NUMBER", "\"１２\"")]
        [TestCase("NUMBER", "\"abc\"")]
        [TestCase("NUMBER", "true")]
        [TestCase("NUMBER", "\"1e30\"")]
        [TestCase("DATE", "\"2026/01/02\"")]
        [TestCase("DATE", "\"2026-02-30\"")]
        [TestCase("DATE", "\"2026-01-02T00:00:00Z\"")]
        [TestCase("TIME", "\"9:30\"")]
        [TestCase("TIME", "\"25:00\"")]
        [TestCase("TIME", "\"09:30 PM\"")]
        [TestCase("DATETIME", "\"2026-03-04T05:06:07\"")]
        [TestCase("DATETIME", "\"2026-03-04 05:06:07Z\"")]
        [TestCase("DATETIME", "\"2026-03-04\"")]
        [TestCase("DATETIME", "\"2026-03-04T05:06:07+0900\"")]
        [TestCase("SINGLE_LINE_TEXT", "{\"a\":1}")]
        [TestCase("SINGLE_LINE_TEXT", "[\"a\"]")]
        [TestCase("CHECK_BOX", "\"A\"")]
        [TestCase("CHECK_BOX", "{\"a\":1}")]
        [TestCase("DATE", "{\"code\":\"x\"}")]
        public void 決まった書式でない値はKintoneException(string fieldType, string json)
        {
            var e = Assert.Throws<KintoneException>(() => Read(fieldType, json))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.Unknown));
            Assert.That(e.Message, Does.Contain("app 'T', field 'F'"));
        }

        [TestCase("#N/A!", "NUMBER")]
        [TestCase("#ERROR", "DATE")]
        [TestCase("x", "DATETIME")]
        [TestCase("x", "TIME")]
        public void 計算フィールドの読めない値はnull(string text, string format)
            => Assert.That(Read("CALC", $"\"{text}\"", format), Is.Null);

        [Test]
        public void 読めない値のメッセージの値は長ければ切る()
        {
            var e = Assert.Throws<KintoneException>(() => Read("NUMBER", "\"" + new string('x', 300) + "\""))!;
            Assert.That(e.Message, Does.Contain(new string('x', 100) + "..."));
            Assert.That(e.Message, Does.Not.Contain(new string('x', 101)));
        }

        [Test]
        public void 配列の値は生のJSONの文字()
        {
            Assert.That(Read("CHECK_BOX", "[\"VIP\",\"新規\"]"), Is.EqualTo("[\"VIP\",\"新規\"]"));
            Assert.That(Read("USER_SELECT", "[{\"code\":\"sato\",\"name\":\"佐藤\"}]"), Is.EqualTo("[{\"code\":\"sato\",\"name\":\"佐藤\"}]"));
            Assert.That(Read("FILE", "[{\"fileKey\":\"k1\",\"name\":\"a.png\",\"size\":\"10\"}]"), Is.EqualTo("[{\"fileKey\":\"k1\",\"name\":\"a.png\",\"size\":\"10\"}]"));
        }

        [Test]
        public void テーブルは行のJSON配列で数値は数値で日付は文字で配列は配列で無い項目はnull()
        {
            var text = (string)Read(Lines, """
                [{"id":"17","value":{"Product":{"type":"SINGLE_LINE_TEXT","value":"椅子"},"Qty":{"type":"NUMBER","value":"3"},"Price":{"type":"NUMBER","value":""},
                  "Amount":{"type":"CALC","value":"300"},"ShipDate":{"type":"DATE","value":"2026-04-10"},"Options":{"type":"CHECK_BOX","value":["包装"]}}},
                 {"id":18,"value":{"Product":{"type":"SINGLE_LINE_TEXT","value":"机"},"Options":{"type":"CHECK_BOX","value":[]},"Unknown":{"type":"NUMBER","value":"1"}}}]
                """)!;
            Assert.That(text, Is.EqualTo(
                """[{"$id":"17","Product":"椅子","Qty":3,"Price":null,"Amount":300,"ShipDate":"2026-04-10","Options":["包装"]},""" +
                """{"$id":"18","Product":"机","Qty":null,"Price":null,"Amount":null,"ShipDate":null,"Options":null}]"""));
        }

        [Test]
        public void テーブルの行の読めない値はKintoneException()
        {
            var e = Assert.Throws<KintoneException>(() => Read(Lines, """[{"id":"1","value":{"Qty":{"type":"NUMBER","value":"abc"}}}]"""))!;
            Assert.That(e.Message, Does.Contain("field 'Qty'"));
        }

        [Test]
        public void レコードは定義の列だけを値にし無い列はnull()
        {
            var definition = Order;
            using var document = JsonDocument.Parse("""
                {"$id":{"type":"__ID__","value":"5"},"$revision":{"type":"__REVISION__","value":"7"},"OrderNo":{"type":"SINGLE_LINE_TEXT","value":"S-1"},
                 "CustomerName":"broken","Extra":{"type":"SINGLE_LINE_TEXT","value":"x"}}
                """);
            var values = KintoneValueConverter.ToValues(definition, document.RootElement);
            Assert.That(values.Keys, Is.EqualTo(definition.Columns.Select(e => e.Name)));
            Assert.That(values["$id"], Is.EqualTo(5m));
            Assert.That(values["$revision"], Is.EqualTo(7m));
            Assert.That(values["OrderNo"], Is.EqualTo("S-1"));
            Assert.That(values["CustomerName"], Is.Null);
            Assert.That(values["Lines"], Is.Null);
        }

        // ---------------------------------------------------------------- 書き込み

        [Test]
        public void 文字の書き込み()
        {
            Assert.That(Write("SINGLE_LINE_TEXT", "大阪\"商事\""), Is.EqualTo("{\"value\":\"大阪\\\"商事\\\"\"}"));
            Assert.That(Write("SINGLE_LINE_TEXT", null), Is.EqualTo("{\"value\":null}"));
            Assert.That(Write("SINGLE_LINE_TEXT", DBNull.Value), Is.EqualTo("{\"value\":null}"));
            Assert.That(Write("SINGLE_LINE_TEXT", true), Is.EqualTo("{\"value\":\"1\"}"));
            Assert.That(Write("SINGLE_LINE_TEXT", 1.50m), Is.EqualTo("{\"value\":\"1.5\"}"));
            Assert.That(Write("DROP_DOWN", "A"), Is.EqualTo("{\"value\":\"A\"}"));
        }

        [Test]
        public void 数値は末尾の0を落とした文字で書く()
        {
            Assert.That(Write("NUMBER", 12.50m), Is.EqualTo("{\"value\":\"12.5\"}"));
            Assert.That(Write("NUMBER", 3), Is.EqualTo("{\"value\":\"3\"}"));
            Assert.That(Write("NUMBER", 2.5d), Is.EqualTo("{\"value\":\"2.5\"}"));
            Assert.That(Write("NUMBER", -100m), Is.EqualTo("{\"value\":\"-100\"}"));
            Assert.That(Write("NUMBER", true), Is.EqualTo("{\"value\":\"1\"}"));
            Assert.That(Write("NUMBER", false), Is.EqualTo("{\"value\":\"0\"}"));
            Assert.That(Write("NUMBER", "1.25"), Is.EqualTo("{\"value\":\"1.25\"}"));
            Assert.That(Write("NUMBER", ""), Is.EqualTo("{\"value\":null}"));
            Assert.That(Write("NUMBER", null), Is.EqualTo("{\"value\":null}"));
        }

        [Test]
        public void 数値にできない値はInvalidRequest()
        {
            Assert.That(Assert.Throws<KintoneException>(() => Write("NUMBER", "abc"))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(Assert.Throws<KintoneException>(() => Write("NUMBER", double.NaN))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(Assert.Throws<KintoneException>(() => Write("NUMBER", 1e300))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
        }

        [Test]
        public void 日付と時刻と日時の書き込み()
        {
            Assert.That(Write("DATE", new DateOnly(2026, 3, 4)), Is.EqualTo("{\"value\":\"2026-03-04\"}"));
            Assert.That(Write("DATE", new DateTime(2026, 3, 4, 23, 0, 0)), Is.EqualTo("{\"value\":\"2026-03-04\"}"));
            Assert.That(Write("DATE", "2026-03-04"), Is.EqualTo("{\"value\":\"2026-03-04\"}"));
            Assert.That(Write("DATE", ""), Is.EqualTo("{\"value\":null}"));
            //kintone の時刻は分まで
            Assert.That(Write("TIME", new TimeOnly(9, 30, 15)), Is.EqualTo("{\"value\":\"09:30\"}"));
            Assert.That(Write("TIME", new TimeSpan(18, 5, 0)), Is.EqualTo("{\"value\":\"18:05\"}"));
            Assert.That(Write("DATETIME", new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)), Is.EqualTo("{\"value\":\"2026-03-04T05:06:07Z\"}"));
            //種類の無い DateTime は UTC として扱う (SaveAsUtc)
            Assert.That(Write("DATETIME", new DateTime(2026, 3, 4, 5, 6, 7)), Is.EqualTo("{\"value\":\"2026-03-04T05:06:07Z\"}"));
            var local = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
            Assert.That(Write("DATETIME", local), Is.EqualTo("{\"value\":\"" + local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"}"));
            Assert.That(Write("DATETIME", new DateTimeOffset(2026, 3, 4, 14, 6, 7, TimeSpan.FromHours(9))), Is.EqualTo("{\"value\":\"2026-03-04T05:06:07Z\"}"));
        }

        [TestCase("DATE", "2026/13/01")]
        [TestCase("DATE", "x")]
        [TestCase("TIME", "x")]
        [TestCase("DATETIME", "x")]
        public void 日付にできない値はInvalidRequest(string fieldType, string value)
            => Assert.That(Assert.Throws<KintoneException>(() => Write(fieldType, value))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));

        [Test]
        public void 選択の配列の書き込み()
        {
            Assert.That(Write("CHECK_BOX", "[\"VIP\",\"新規\"]"), Is.EqualTo("{\"value\":[\"VIP\",\"新規\"]}"));
            Assert.That(Write("MULTI_SELECT", null), Is.EqualTo("{\"value\":[]}"));
            Assert.That(Write("CHECK_BOX", " "), Is.EqualTo("{\"value\":[]}"));
            Assert.That(Write("CHECK_BOX", "null"), Is.EqualTo("{\"value\":[]}"));
            //ユーザー・組織・グループは [{code}]。文字か {code, ...} を受け付ける
            Assert.That(Write("USER_SELECT", "[\"sato\",{\"code\":\"suzuki\",\"name\":\"x\"}]"), Is.EqualTo("{\"value\":[{\"code\":\"sato\"},{\"code\":\"suzuki\"}]}"));
            Assert.That(Write("GROUP_SELECT", "[\"g1\"]"), Is.EqualTo("{\"value\":[{\"code\":\"g1\"}]}"));
        }

        [TestCase("CHECK_BOX", "not json")]
        [TestCase("CHECK_BOX", "{\"a\":1}")]
        [TestCase("CHECK_BOX", "[1]")]
        [TestCase("CHECK_BOX", "[\"\"]")]
        [TestCase("CHECK_BOX", "[{\"code\":\"x\"}]")]
        [TestCase("USER_SELECT", "[{\"name\":\"x\"}]")]
        public void 配列にできない値はInvalidRequest(string fieldType, string value)
            => Assert.That(Assert.Throws<KintoneException>(() => Write(fieldType, value))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));

        [Test]
        public void テーブルの書き込みはidのある行とない行を送り読み取り専用と定義に無いキーは送らない()
        {
            var json = Write(Lines, """
                [{"$id":"17","Product":"椅子","Qty":3,"Amount":300,"Unknown":"x"},
                 {"Product":"机","ShipDate":null,"Options":null,"Qty":"2.50"},
                 {"$id":18},
                 {"$id":"","Price":1.5,"ShipDate":"2026-04-10","Options":["包装"]}]
                """);
            Assert.That(json, Is.EqualTo(
                "{\"value\":[" +
                "{\"id\":\"17\",\"value\":{\"Product\":{\"value\":\"椅子\"},\"Qty\":{\"value\":\"3\"}}}," +
                "{\"value\":{\"Product\":{\"value\":\"机\"},\"Qty\":{\"value\":\"2.5\"},\"ShipDate\":{\"value\":null},\"Options\":{\"value\":[]}}}," +
                "{\"id\":\"18\",\"value\":{}}," +
                "{\"value\":{\"Price\":{\"value\":\"1.5\"},\"ShipDate\":{\"value\":\"2026-04-10\"},\"Options\":{\"value\":[\"包装\"]}}}" +
                "]}"));
        }

        [Test]
        public void 空のテーブルは空の配列で送る()
        {
            Assert.That(Write(Lines, null), Is.EqualTo("{\"value\":[]}"));
            Assert.That(Write(Lines, "[]"), Is.EqualTo("{\"value\":[]}"));
        }

        [TestCase("[1]")]
        [TestCase("[{\"Qty\":{\"a\":1}}]")]
        [TestCase("[{\"Qty\":\"abc\"}]")]
        [TestCase("[{\"Options\":\"包装\"}]")]
        [TestCase("{\"Qty\":1}")]
        public void テーブルにできない値はInvalidRequest(string value)
            => Assert.That(Assert.Throws<KintoneException>(() => Write(Lines, value))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
    }
}
