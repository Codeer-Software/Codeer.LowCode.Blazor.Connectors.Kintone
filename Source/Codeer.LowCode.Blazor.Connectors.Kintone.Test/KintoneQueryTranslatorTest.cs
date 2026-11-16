using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>本体が組み立てる SQL (PostgreSQL 向け) → kintone のクエリ。</summary>
    public class KintoneQueryTranslatorTest
    {
        const char Escape = (char)1;

        static readonly Dictionary<string, KintoneTableDefinition> _definitions = new()
        {
            ["Customer"] = Parse("Customer", TestMetadata.Customer),
            ["Region"] = Parse("Region", TestMetadata.Region),
            ["Country"] = Parse("Country", TestMetadata.Country),
            ["Order"] = Parse("Order", TestMetadata.Order),
        };

        static KintoneTableDefinition Parse(string name, string properties)
        {
            using var document = JsonDocument.Parse("{\"properties\":" + properties + "}");
            return KintoneTableDefinition.ParseFields(name, document.RootElement);
        }

        static Task<KintoneTableDefinition> GetDefinition(string table)
            => _definitions.TryGetValue(table, out var definition) ? Task.FromResult(definition) : throw new KintoneException("no table " + table, KintoneErrorKind.NotFound);

        static Task<KintoneRead> TranslateAsync(string sql, params (string Name, object? Value)[] args)
            => KintoneQueryTranslator.TranslateSelectAsync(sql, args.ToDictionary(e => e.Name, e => e.Value), GetDefinition);

        const string SelectCustomer = """select "Customer"."$id" as "Customer_$id","Customer"."Name" as "Customer_Name" from "Customer" """;

        //1 つの条件 (where の後ろ) を読み替えたクエリの条件
        static async Task<string?> FilterAsync(string where, params (string Name, object? Value)[] args)
            => (await TranslateAsync(SelectCustomer + "where " + where, args)).Filter;

        static async Task<string?> FilterAsync(string column, string op, object? value)
            => await FilterAsync($"\"Customer\".\"{column}\" {op} @p0", ("@p0", value));

        // ---------------------------------------------------------------- 字句・select の形

        [Test]
        public void IDと版の名前は引用符つきの識別子として読む()
        {
            var tokens = KintoneQueryTranslator.Tokenize("""select "Customer"."$id" as "Customer_$id" from "Customer" where "$revision"=@poptimistic_current""")
                .Where(e => e.Kind != SqlTokenKind.Space).ToList();
            Assert.That(tokens.Where(e => e.Kind == SqlTokenKind.QuotedIdentifier).Select(e => e.Text),
                Is.EqualTo(new[] { "Customer", "$id", "Customer_$id", "Customer", "$revision" }));
            Assert.That(tokens.Last(), Is.EqualTo(new SqlToken(SqlTokenKind.Parameter, "@poptimistic_current")));
        }

        [Test]
        public async Task 一覧のselectは列と別名と主表の読む列()
        {
            var read = await TranslateAsync(SelectCustomer + """order by "Customer"."Name" desc limit 10 offset 20""");
            Assert.That(read.Table, Is.EqualTo("Customer"));
            Assert.That(read.Columns.Select(e => (e.Key, e.Alias, e.Column)), Is.EqualTo(new[]
            {
                ("Customer_$id", "Customer", "$id"),
                ("Customer_Name", "Customer", "Name"),
            }));
            Assert.That(read.SelectColumns, Is.EqualTo(new[] { "$id", "Name" }));
            Assert.That(read.Filter, Is.Null);
            Assert.That(read.Where, Is.Null);
            //ページングつきは並びの最後に $id
            Assert.That(read.OrderBy, Is.EqualTo(new[] { "Name desc", "$id asc" }));
            Assert.That((read.Limit, read.Offset), Is.EqualTo(((int?)10, 20)));
        }

        [Test]
        public async Task ページングなしの並びにはキーを足さない()
        {
            var read = await TranslateAsync(SelectCustomer + """order by "Customer"."CreditLimit" asc""");
            Assert.That(read.OrderBy, Is.EqualTo(new[] { "CreditLimit asc" }));
            Assert.That(read.Limit, Is.Null);
            //並びに $id があればもう足さない
            read = await TranslateAsync(SelectCustomer + """order by "Customer"."$id" desc limit 5 offset 0""");
            Assert.That(read.OrderBy, Is.EqualTo(new[] { "$id desc" }));
        }

        [Test]
        public async Task 件数とselect全部()
        {
            var count = await TranslateAsync("""select count(*) from "Customer" where "Customer"."Name" = @p0""", ("@p0", "A"));
            Assert.That(count.IsCount, Is.True);
            Assert.That(count.Filter, Is.EqualTo("Name = \"A\""));

            var all = await TranslateAsync("""select * from "Region" """);
            Assert.That(all.IsSelectAll, Is.True);
            Assert.That(all.Columns.Select(e => e.Key), Is.EqualTo(new[] { "$id", "$revision", "Code", "RegionName", "CountryId" }));
            Assert.That(all.SelectColumns, Is.Empty);
        }

        [Test]
        public void 件数とselect全部にページングは付けられない()
        {
            Assert.ThrowsAsync<KintoneException>(() => TranslateAsync("""select count(*) from "Customer" limit 1 offset 0"""));
            Assert.ThrowsAsync<KintoneException>(() => TranslateAsync("""select * from "Customer" limit 1 offset 0"""));
        }

        [Test]
        public void BuildQueryは条件と並びとlimitとoffset()
        {
            Assert.That(KintoneQueryTranslator.BuildQuery(null, Array.Empty<string>(), 500, 0), Is.EqualTo("limit 500"));
            Assert.That(KintoneQueryTranslator.BuildQuery("Name = \"A\"", new[] { "Name desc", "$id asc" }, 10, 20),
                Is.EqualTo("Name = \"A\" order by Name desc, $id asc limit 10 offset 20"));
            Assert.That(KintoneQueryTranslator.BuildQuery("", new[] { "$id asc" }, 1, 0), Is.EqualTo("order by $id asc limit 1"));
        }

        // ---------------------------------------------------------------- 演算子 (フィールドの種類ごと)

        [TestCase("Name", "=", "A", "Name = \"A\"")]
        [TestCase("Url", "=", "https://example.cybozu.com/x", "Url = \"https://example.cybozu.com/x\"")]
        [TestCase("Name", "<>", "A", "(Name != \"A\" and Name != \"\")")]
        [TestCase("Name", "!=", "A", "(Name != \"A\" and Name != \"\")")]
        [TestCase("CreditLimit", "=", 100, "CreditLimit = 100")]
        [TestCase("CreditLimit", "<>", 100, "(CreditLimit != 100 and CreditLimit != \"\")")]
        [TestCase("CreditLimit", "<", 100, "CreditLimit < 100")]
        [TestCase("CreditLimit", "<=", 100, "CreditLimit <= 100")]
        [TestCase("CreditLimit", ">", 100, "CreditLimit > 100")]
        [TestCase("CreditLimit", ">=", 100, "CreditLimit >= 100")]
        [TestCase("Total", ">", 1, "Total > 1")]
        [TestCase("$id", "=", 5, "$id = 5")]
        [TestCase("$id", "<>", 5, "$id != 5")]
        [TestCase("$id", ">", 5, "$id > 5")]
        [TestCase("RecordNo", "<>", 3, "RecordNo != \"3\"")]
        [TestCase("Rank", "=", "A", "Rank in (\"A\")")]
        [TestCase("Rank", "<>", "A", "Rank not in (\"A\", \"\")")]
        [TestCase("Tags", "=", "VIP", "Tags in (\"VIP\")")]
        [TestCase("Owner", "=", "sato", "Owner in (\"sato\")")]
        [TestCase("Creator", "<>", "sato", "Creator not in (\"sato\")")]
        [TestCase("Status", "=", "完了", "Status in (\"完了\")")]
        public async Task 比較(string column, string op, object value, string expected)
            => Assert.That(await FilterAsync(column, op, value), Is.EqualTo(expected));

        [Test]
        public async Task 日付と時刻と日時の比較()
        {
            Assert.That(await FilterAsync("StartDate", ">=", new DateOnly(2026, 4, 1)), Is.EqualTo("StartDate >= \"2026-04-01\""));
            Assert.That(await FilterAsync("StartDate", "<>", new DateOnly(2026, 4, 1)), Is.EqualTo("(StartDate != \"2026-04-01\" and StartDate != \"\")"));
            Assert.That(await FilterAsync("OpenTime", "<", new TimeOnly(9, 5, 30)), Is.EqualTo("OpenTime < \"09:05\""));
            Assert.That(await FilterAsync("LastContact", "=", new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Unspecified)), Is.EqualTo("LastContact = \"2026-04-01T09:00:00Z\""));
            Assert.That(await FilterAsync("LastContact", ">", new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.FromHours(9))), Is.EqualTo("LastContact > \"2026-04-01T00:00:00Z\""));
            Assert.That(await FilterAsync("Created", "<", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo("Created < \"2026-04-01T00:00:00Z\""));
        }

        [TestCase("Note", "=")]
        [TestCase("Note", "<>")]
        [TestCase("Body", "=")]
        [TestCase("Name", "<")]
        [TestCase("Name", ">=")]
        [TestCase("Rank", "<")]
        [TestCase("Attachments", "=")]
        public void クエリに演算子の無い比較はNotSupported(string column, string op)
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => FilterAsync(column, op, "A"))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
            Assert.That(e.Message, Does.Contain($"'{column}'"));
        }

        // ---------------------------------------------------------------- 空 (is null) を明示する

        [TestCase("Name", "Name = \"\"", "Name != \"\"")]
        [TestCase("CreditLimit", "CreditLimit = \"\"", "CreditLimit != \"\"")]
        [TestCase("StartDate", "StartDate = \"\"", "StartDate != \"\"")]
        [TestCase("Note", "Note is empty", "Note is not empty")]
        [TestCase("Rank", "Rank in (\"\")", "Rank not in (\"\")")]
        [TestCase("Tags", "Tags in (\"\")", "Tags not in (\"\")")]
        public async Task 空かどうかは種類ごとの書き方(string column, string isNull, string isNotNull)
        {
            Assert.That(await FilterAsync($"\"Customer\".\"{column}\" is null"), Is.EqualTo(isNull));
            Assert.That(await FilterAsync($"\"Customer\".\"{column}\" is not null"), Is.EqualTo(isNotNull));
        }

        [TestCase("$id")]
        [TestCase("RecordNo")]
        [TestCase("Created")]
        [TestCase("Creator")]
        public async Task 空になりえない列の空かどうかは定数(string column)
        {
            var isNull = await TranslateAsync(SelectCustomer + $"where \"Customer\".\"{column}\" is null");
            Assert.That((isNull.IsEmpty, isNull.Filter), Is.EqualTo((true, (string?)null)));
            var isNotNull = await TranslateAsync(SelectCustomer + $"where \"Customer\".\"{column}\" is not null");
            Assert.That((isNotNull.IsEmpty, isNotNull.Where, isNotNull.Filter), Is.EqualTo((false, (FilterNode?)null, (string?)null)));
        }

        [TestCase("Body")]
        [TestCase("Attachments")]
        public void 空かどうかを書けない列はNotSupported(string column)
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => FilterAsync($"\"Customer\".\"{column}\" is null"))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
        }

        // ---------------------------------------------------------------- not の押し下げ

        [Test]
        public async Task notは比較の逆にして空の行を含めない()
        {
            Assert.That(await FilterAsync("""not ("Customer"."Name" = @p0)""", ("@p0", "A")), Is.EqualTo("(Name != \"A\" and Name != \"\")"));
            Assert.That(await FilterAsync("""not ("Customer"."CreditLimit" > @p0)""", ("@p0", 5)), Is.EqualTo("CreditLimit <= 5"));
            Assert.That(await FilterAsync("""not ("Customer"."Rank" = @p0)""", ("@p0", "A")), Is.EqualTo("Rank not in (\"A\", \"\")"));
            Assert.That(await FilterAsync("""not ("Customer"."Name" is null)"""), Is.EqualTo("Name != \"\""));
            Assert.That(await FilterAsync("""not (not ("Customer"."Name" = @p0))""", ("@p0", "A")), Is.EqualTo("Name = \"A\""));
        }

        [Test]
        public async Task notはドモルガンで押し下げる()
        {
            Assert.That(await FilterAsync("""not ("Customer"."Name" = @p0 and "Customer"."CreditLimit" >= @p1)""", ("@p0", "A"), ("@p1", 5)),
                Is.EqualTo("(Name != \"A\" and Name != \"\") or CreditLimit < 5"));
            Assert.That(await FilterAsync("""not ("Customer"."Name" = @p0 or "Customer"."Note" is null)""", ("@p0", "A")),
                Is.EqualTo("(Name != \"A\" and Name != \"\") and Note is not empty"));
        }

        [Test]
        public async Task andとorは括弧を付けて入れ子にする()
        {
            Assert.That(await FilterAsync("""("Customer"."Name" = @p0 or "Customer"."Name" = @p1) and "Customer"."CreditLimit" > @p2""", ("@p0", "A"), ("@p1", "B"), ("@p2", 1)),
                Is.EqualTo("(Name = \"A\" or Name = \"B\") and CreditLimit > 1"));
        }

        // ---------------------------------------------------------------- null の値 (unknown)・定数

        [TestCase("=")]
        [TestCase("<>")]
        [TestCase("<")]
        public async Task 値がnullの比較は0行(string op)
        {
            var read = await TranslateAsync(SelectCustomer + $"where \"Customer\".\"Name\" {op} @p0", ("@p0", null));
            Assert.That(read.IsEmpty, Is.True);
            //not を付けても真にならない
            read = await TranslateAsync(SelectCustomer + $"where not (\"Customer\".\"Name\" {op} @p0)", ("@p0", DBNull.Value));
            Assert.That(read.IsEmpty, Is.True);
        }

        [Test]
        public async Task 定数の条件()
        {
            var always = await TranslateAsync(SelectCustomer + "where 1=1");
            Assert.That((always.IsEmpty, always.Where), Is.EqualTo((false, (FilterNode?)null)));
            Assert.That((await TranslateAsync(SelectCustomer + "where 1=0")).IsEmpty, Is.True);
            Assert.That(await FilterAsync("""1=1 and "Customer"."Name" = @p0""", ("@p0", "A")), Is.EqualTo("Name = \"A\""));
            Assert.That(await FilterAsync("""1=0 or "Customer"."Name" = @p0""", ("@p0", "A")), Is.EqualTo("Name = \"A\""));
        }

        [Test]
        public async Task 数値の列に数値でない値()
        {
            Assert.That((await TranslateAsync(SelectCustomer + """where "Customer"."CreditLimit" = @p0""", ("@p0", "abc"))).IsEmpty, Is.True);
            Assert.That(await FilterAsync("CreditLimit", "<>", "abc"), Is.EqualTo("CreditLimit != \"\""));
            var e = Assert.ThrowsAsync<KintoneException>(() => FilterAsync("CreditLimit", "<", "abc"))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            //空になりえない列の <> は定数
            var read = await TranslateAsync(SelectCustomer + """where "Customer"."$id" <> @p0""", ("@p0", "abc"));
            Assert.That((read.IsEmpty, read.Where), Is.EqualTo((false, (FilterNode?)null)));
        }

        // ---------------------------------------------------------------- 部分一致

        [Test]
        public async Task 部分一致はエスケープを外して前後の百分率記号を取る()
        {
            Assert.That(await FilterAsync("""
                "Customer"."Name" like @p0 escape @pescape
                """, ("@p0", "%abc%"), ("@pescape", Escape)), Is.EqualTo("Name like \"abc\""));
            Assert.That(await FilterAsync("""
                "Customer"."Name" like @p0 escape @pescape
                """, ("@p0", $"%5{Escape}%{Escape}_\"x\"%"), ("@pescape", Escape)), Is.EqualTo("Name like \"5%_\\\"x\\\"\""));
            Assert.That(await FilterAsync("""
                "Customer"."Note" like @p0 escape @pescape
                """, ("@p0", "%a%"), ("@pescape", Escape)), Is.EqualTo("Note like \"a\""));
            Assert.That(await FilterAsync("""
                "Customer"."Body" like @p0 escape @pescape
                """, ("@p0", "%a%"), ("@pescape", Escape)), Is.EqualTo("Body like \"a\""));
        }

        [Test]
        public async Task 部分一致のnotは空の行を含めない()
        {
            Assert.That(await FilterAsync("""
                not ("Customer"."Name" like @p0 escape @pescape)
                """, ("@p0", "%a%"), ("@pescape", Escape)), Is.EqualTo("(Name not like \"a\" and Name != \"\")"));
            Assert.That(await FilterAsync("""
                not ("Customer"."Note" like @p0 escape @pescape)
                """, ("@p0", "%a%"), ("@pescape", Escape)), Is.EqualTo("(Note not like \"a\" and Note is not empty)"));
            //リッチエディターは空かどうかを書けないので「含まない」は書けない
            Assert.ThrowsAsync<KintoneException>(() => FilterAsync("""
                not ("Customer"."Body" like @p0 escape @pescape)
                """, ("@p0", "%a%"), ("@pescape", Escape)));
        }

        [TestCase("Name", "abc")]
        [TestCase("Name", "%abc")]
        [TestCase("Name", "abc%")]
        [TestCase("Name", "%a%c%")]
        [TestCase("Name", "%a_c%")]
        [TestCase("CreditLimit", "%1%")]
        [TestCase("Rank", "%A%")]
        public void 部分一致にできないパターンと列は拒否(string column, string pattern)
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => FilterAsync($"\"Customer\".\"{column}\" like @p0 escape @pescape", ("@p0", pattern), ("@pescape", Escape)))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
        }

        [Test]
        public void escapeの無い部分一致は拒否()
            => Assert.ThrowsAsync<KintoneException>(() => FilterAsync("""
                "Customer"."Name" like @p0
                """, ("@p0", "%a%")));

        // ---------------------------------------------------------------- in / not in

        [Test]
        public async Task inとnotin()
        {
            Assert.That(await FilterAsync("""
                "Customer"."Name" in (@p0, @p1)
                """, ("@p0", "A"), ("@p1", "B")), Is.EqualTo("Name = \"A\" or Name = \"B\""));
            Assert.That(await FilterAsync("""
                "Customer"."Rank" in (@p0)
                """, ("@p0", "A")), Is.EqualTo("Rank in (\"A\")"));
            Assert.That(await FilterAsync("""
                "Customer"."CreditLimit" not in (@p0, @p1)
                """, ("@p0", 1), ("@p1", 2)), Is.EqualTo("(CreditLimit != 1 and CreditLimit != \"\") and (CreditLimit != 2 and CreditLimit != \"\")"));
        }

        [Test]
        public async Task inの中のnullは落としnotinは0行()
        {
            Assert.That(await FilterAsync("""
                "Customer"."Name" in (@p0, @p1)
                """, ("@p0", "A"), ("@p1", null)), Is.EqualTo("Name = \"A\""));
            var read = await TranslateAsync(SelectCustomer + """
                where "Customer"."Name" not in (@p0, @p1)
                """, ("@p0", "A"), ("@p1", null));
            Assert.That(read.IsEmpty, Is.True);
        }

        // ---------------------------------------------------------------- 値の書き方

        [TestCase("a\"b\\c", "\"a\\\"b\\\\c\"")]
        [TestCase("顧客 名", "\"顧客 名\"")]
        [TestCase("", "\"\"")]
        public async Task 文字の値は引用符と逆斜線を前置する(string value, string literal)
            => Assert.That(await FilterAsync("Name", "=", value), Is.EqualTo("Name = " + literal));

        [Test]
        public async Task 数値の値は引用符を付けず末尾の0を落とす()
        {
            Assert.That(await FilterAsync("CreditLimit", "=", 1.50m), Is.EqualTo("CreditLimit = 1.5"));
            Assert.That(await FilterAsync("CreditLimit", "=", 100.000m), Is.EqualTo("CreditLimit = 100"));
            Assert.That(await FilterAsync("CreditLimit", "=", -3L), Is.EqualTo("CreditLimit = -3"));
            Assert.That(await FilterAsync("CreditLimit", "=", "12.0"), Is.EqualTo("CreditLimit = 12"));
            Assert.That(await FilterAsync("IsActive", "=", true), Is.EqualTo("IsActive = 1"));
            Assert.That(await FilterAsync("IsActive", "=", false), Is.EqualTo("IsActive = 0"));
        }

        [Test]
        public void 値を書く関数()
        {
            Assert.That(KintoneQueryTranslator.FormatTime(new TimeOnly(23, 59, 59)), Is.EqualTo("23:59"));
            Assert.That(KintoneQueryTranslator.FormatDateTime(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), Is.EqualTo("2026-01-02T03:04:05Z"));
            var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
            Assert.That(KintoneQueryTranslator.FormatDateTime(local), Is.EqualTo(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
            Assert.That(KintoneQueryTranslator.KeyText(5.0m), Is.EqualTo("5"));
            Assert.That(KintoneQueryTranslator.KeyText(5), Is.EqualTo("5"));
            Assert.That(KintoneQueryTranslator.KeyText("5"), Is.EqualTo("5"));
            Assert.That(Assert.Throws<KintoneException>(() => KintoneQueryTranslator.ToDecimal(double.NaN))!.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
        }

        [Test]
        public async Task 日付の列の値()
        {
            Assert.That(await FilterAsync("StartDate", "=", new DateTime(2026, 4, 1, 23, 0, 0)), Is.EqualTo("StartDate = \"2026-04-01\""));
            Assert.That(await FilterAsync("StartDate", "=", "2026-04-01"), Is.EqualTo("StartDate = \"2026-04-01\""));
            //書式の合わない文字は一致する行が無い
            Assert.That((await TranslateAsync(SelectCustomer + """where "Customer"."StartDate" = @p0""", ("@p0", "2026/04/01"))).IsEmpty, Is.True);
            Assert.That(await FilterAsync("OpenTime", "=", TimeSpan.FromMinutes(75)), Is.EqualTo("OpenTime = \"01:15\""));
            Assert.That(await FilterAsync("LastContact", "=", new DateOnly(2026, 4, 1)), Is.EqualTo("LastContact = \"2026-04-01T00:00:00Z\""));
        }

        // ---------------------------------------------------------------- 結合・一覧をまたぐ条件

        const string SelectWithRegion = """
            select "Customer"."$id" as "Customer_$id","Customer"."Name" as "Customer_Name","Region_1"."RegionName" as "Region_1_RegionName","Country_2"."CountryName" as "Country_2_CountryName"
            from "Customer"
            left join "Region" as "Region_1" on "Customer"."RegionCode"="Region_1"."Code"
            left join "Country" as "Country_2" on CAST("Region_1"."CountryId" AS VARCHAR(255))=CAST("Country_2"."$id" AS VARCHAR(255))
            """;

        [Test]
        public async Task 結合は結合先ごとに読む列を持つ()
        {
            var read = await TranslateAsync(SelectWithRegion);
            Assert.That(read.SelectColumns, Is.EqualTo(new[] { "$id", "Name", "RegionCode" }));
            Assert.That(read.Joins.Select(e => (e.Table, e.Alias, e.SourceAlias, e.SourceColumn, e.DestinationColumn)), Is.EqualTo(new[]
            {
                ("Region", "Region_1", "Customer", "RegionCode", "Code"),
                ("Country", "Country_2", "Region_1", "CountryId", "$id"),
            }));
            Assert.That(read.Joins[0].SelectColumns, Is.EqualTo(new[] { "Code", "RegionName", "CountryId" }));
            Assert.That(read.Joins[1].SelectColumns, Is.EqualTo(new[] { "$id", "CountryName" }));
        }

        [Test]
        public async Task 結合先の列の条件は値を集める条件になりクエリは実行時に作る()
        {
            var read = await TranslateAsync(SelectWithRegion + """where "Country_2"."CountryName" = @p0 and "Customer"."Name" = @p1""", ("@p0", "日本"), ("@p1", "A"));
            Assert.That(read.Filter, Is.Null);
            Assert.That(KintoneQueryTranslator.HasInSet(read.Where!), Is.True);
            var and = (AndNode)read.Where!;
            var region = (InSetNode)and.Children[0];
            Assert.That((region.Column, region.Source.Table, region.Source.Column), Is.EqualTo(("RegionCode", "Region", "Code")));
            var country = (InSetNode)region.Source.Filter;
            Assert.That((country.Column, country.Source.Table, country.Source.Column), Is.EqualTo(("CountryId", "Country", "$id")));
            Assert.That(and.Children[1], Is.InstanceOf<TermNode>());
        }

        [Test]
        public async Task 結合先の列で並べるときはその列も読む()
        {
            var read = await TranslateAsync(SelectWithRegion + """order by "Region_1"."RegionName" asc""");
            Assert.That(read.HasJoinedOrder, Is.True);
            Assert.That(read.OrderBy, Is.Empty);
            Assert.That(read.Order, Is.EqualTo(new[] { new KintoneOrder("Region_1", "RegionName", false) }));
        }

        [Test]
        public async Task 一覧をまたぐ条件は子の表から値を集める()
        {
            var read = await TranslateAsync(SelectCustomer + """
                where exists(select 1 from "Order" where ("Order"."CustomerCode" = "Customer"."Code") and ("Order"."OrderNo" = @p0))
                """, ("@p0", "S-1"));
            Assert.That(read.Filter, Is.Null);
            var keys = (InSetNode)read.Where!;
            Assert.That((keys.Column, keys.Source.Table, keys.Source.Column), Is.EqualTo(("Code", "Order", "CustomerCode")));

            var not = await TranslateAsync(SelectCustomer + """
                where not exists(select 1 from "Order" where ("Order"."CustomerCode" = "Customer"."Code"))
                """);
            Assert.That(((NotNode)not.Where!).Child, Is.InstanceOf<InSetNode>());
        }

        [TestCase("""exists(select 1 from "Order" where "Order"."OrderNo" = @p0)""")]
        [TestCase("""exists(select 1 from "Order" where "Order"."CustomerCode" = "Customer"."Code" or "Order"."OrderNo" = @p0)""")]
        [TestCase("""exists(select 1 from "Order" where "Order"."CustomerCode" <> "Customer"."Code")""")]
        [TestCase("""exists(select 1 from "Order" where "Order"."CustomerCode" = "Customer"."Code" order by "Order"."OrderNo")""")]
        [TestCase("""exists(select "Order"."OrderNo" from "Order" where "Order"."CustomerCode" = "Customer"."Code")""")]
        public void 本体の形でない一覧をまたぐ条件は拒否(string condition)
            => Assert.ThrowsAsync<KintoneException>(() => FilterAsync(condition, ("@p0", "S-1")));

        // ---------------------------------------------------------------- 拒否する形

        [TestCase("""select "Customer"."Name" as "n" from "Customer" group by "Customer"."Name" """)]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" union select "Region"."Code" as "n" from "Region" """)]
        [TestCase("""select "Customer"."Name" as "n" from "Customer"; delete from "Customer" """)]
        [TestCase("""select upper("Customer"."Name") as "n" from "Customer" """)]
        [TestCase("""select "Customer"."Name" as "n" from "s"."Customer" """)]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" inner join "Region" as "r" on "Customer"."RegionCode"="r"."Code" """)]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" where "Customer"."Name" = "Customer"."Code" """)]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" where "Customer"."Name" = 'A'""")]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" where "Customer"."Name" = @p0 limit 10""")]
        [TestCase("""select "Customer"."Name" + 1 as "n" from "Customer" """)]
        [TestCase("""update "Customer" set "Name"=@p0""")]
        [TestCase("""select "Customer"."Name" as "n" from "Customer" where "Customer"."Name" = @p0 order by lower("Customer"."Name")""")]
        public void 本体が組み立てない形は拒否(string sql)
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => TranslateAsync(sql, ("@p0", "A")))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
        }

        [Test]
        public void 定義に無い列とパラメータの無い値はエラー()
        {
            var noField = Assert.ThrowsAsync<KintoneException>(() => FilterAsync("Missing", "=", "A"))!;
            Assert.That(noField.Message, Does.Contain("has no field 'Missing'"));
            //テーブルの列は条件に書けない (列として扱わない)
            Assert.ThrowsAsync<KintoneException>(() => TranslateAsync("""select "Customer"."Spacer" as "s" from "Customer" """));
            var noArg = Assert.ThrowsAsync<KintoneException>(() => TranslateAsync(SelectCustomer + """where "Customer"."Name" = @p9"""))!;
            Assert.That(noArg.Message, Does.Contain("@p9"));
        }

        // ---------------------------------------------------------------- 書き込みの SQL

        [Test]
        public void insertの列とパラメータとreturning()
        {
            var insert = KintoneQueryTranslator.ParseInsert("""insert into "Customer"("Name","CreditLimit") values(@p0,@p1) returning "$id" """);
            Assert.That(insert.Table, Is.EqualTo("Customer"));
            Assert.That(insert.Columns, Is.EqualTo(new[] { "Name", "CreditLimit" }));
            Assert.That(insert.Rows, Is.EqualTo(new[] { new[] { "@p0", "@p1" } }));
            Assert.That(insert.ReturningColumn, Is.EqualTo("$id"));

            var bulk = KintoneQueryTranslator.ParseInsert("""insert into "Customer" ("Name") values (@p0),(@p1),(@p2)""");
            Assert.That(bulk.Rows, Has.Count.EqualTo(3));
            Assert.That(bulk.ReturningColumn, Is.Empty);

            var empty = KintoneQueryTranslator.ParseInsert("""insert into "Customer" default values returning "$id" """);
            Assert.That(empty.Rows.Single(), Is.Empty);
        }

        [TestCase("""insert into "Customer"("Name") values('A')""")]
        [TestCase("""insert into "Customer"("Name","Code") values(@p0)""")]
        [TestCase("""insert into "Customer"("Name") select "Name" from "Region" """)]
        public void 本体の形でないinsertは拒否(string sql)
            => Assert.Throws<KintoneException>(() => KintoneQueryTranslator.ParseInsert(sql));

        [Test]
        public void 複合キーの表のinsertは拒否()
        {
            var e = Assert.Throws<KintoneException>(() => KintoneQueryTranslator.ParseInsert("""insert into "Customer"("Name") values(@p0) returning "Code"|| '_' ||"Name" """))!;
            Assert.That(e.Message, Does.Contain("composite key"));
        }

        [Test]
        public void updateの版の条件はクエリの条件から外して持つ()
        {
            var update = KintoneQueryTranslator.ParseUpdate("""update "Customer" set "Name"=@p0,"$revision"=@p1 where "$id"=@pid0 and "$revision"=@poptimistic_current""");
            Assert.That(update.Table, Is.EqualTo("Customer"));
            Assert.That(update.Values, Is.EqualTo(new[] { ("Name", "@p0"), ("$revision", "@p1") }));
            Assert.That((update.KeyColumn, update.KeyParameter), Is.EqualTo(("$id", "@pid0")));
            Assert.That(update.RevisionParameter, Is.EqualTo("@poptimistic_current"));
            Assert.That(update.RevisionIsNull, Is.False);
            Assert.That(update.HasOtherConditions, Is.False);
            Assert.That(update.WhereSql, Is.EqualTo("\"$id\" = @pid0"));
        }

        [Test]
        public void 版がnullの条件とほかの条件()
        {
            var update = KintoneQueryTranslator.ParseUpdate("""update "Customer" set "Deleted"=@p0 where "$id"=@pid0 and "$revision" is null and ("Deleted"<>@pnot_deleted or "Deleted" is null)""");
            Assert.That(update.RevisionParameter, Is.Null);
            Assert.That(update.RevisionIsNull, Is.True);
            Assert.That(update.HasOtherConditions, Is.True);
            Assert.That(update.WhereSql, Is.EqualTo("\"$id\" = @pid0 and ( \"Deleted\" <> @pnot_deleted or \"Deleted\" is null )"));
        }

        [Test]
        public void deleteのキーと版()
        {
            var delete = KintoneQueryTranslator.ParseDelete("""delete from "Customer" where "Code"=@pid0 and "$revision"=@prev""");
            Assert.That((delete.Table, delete.KeyColumn, delete.KeyParameter, delete.RevisionParameter), Is.EqualTo(("Customer", "Code", "@pid0", "@prev")));
            Assert.That(delete.Values, Is.Empty);
            Assert.That(delete.HasOtherConditions, Is.False);
        }

        [Test]
        public void 版の条件が2つあれば2つ目はほかの条件として残す()
        {
            var update = KintoneQueryTranslator.ParseUpdate("""update "Customer" set "Name"=@p0 where "$id"=@pid0 and "$revision"=@a and "$revision"=@b""");
            Assert.That(update.RevisionParameter, Is.EqualTo("@a"));
            Assert.That(update.HasOtherConditions, Is.True);
            Assert.That(update.WhereSql, Is.EqualTo("\"$id\" = @pid0 and \"$revision\" = @b"));
        }

        [TestCase("""update "Customer" set "Name"=@p0""")]
        [TestCase("""update "Customer" set "Name"='A' where "$id"=@pid0""")]
        [TestCase("""update "Customer" set "Name"=@p0 where "$id">@pid0""")]
        [TestCase("""update "Customer" set "Name"=@p0 where "$id"=@pid0; delete from "Customer" where "$id"=@pid0""")]
        [TestCase("""delete from "Customer" """)]
        [TestCase("""delete from "Customer" where "$id" in (@pid0)""")]
        public void 本体の形でないupdateとdeleteは拒否(string sql)
            => Assert.Throws<KintoneException>(() =>
            {
                if (KintoneQueryTranslator.IsUpdate(sql)) KintoneQueryTranslator.ParseUpdate(sql);
                else KintoneQueryTranslator.ParseDelete(sql);
            });

        [Test]
        public void 文の種類()
        {
            Assert.That(KintoneQueryTranslator.IsInsert(" insert into \"t\"(\"a\") values(@p0)"), Is.True);
            Assert.That(KintoneQueryTranslator.IsUpdate("UPDATE \"t\" set \"a\"=@p0 where \"k\"=@p1"), Is.True);
            Assert.That(KintoneQueryTranslator.IsDelete("delete from \"t\" where \"k\"=@p1"), Is.True);
            Assert.That(KintoneQueryTranslator.IsAggregate("""select count(*) from "Customer" """), Is.False);
            Assert.That(KintoneQueryTranslator.IsAggregate("""select "Customer"."Rank" as "g0", count(*) as "n" from "Customer" group by "Customer"."Rank" """), Is.True);
        }
    }
}
