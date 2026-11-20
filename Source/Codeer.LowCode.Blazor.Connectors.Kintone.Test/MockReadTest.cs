using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Globalization;
using System.Text.Json.Nodes;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// モック + 本物のコネクター + 本体の ModuleDataIO で、定義・一覧・ページ送り・件数・並び・検索を端から端まで確かめる。
    /// 期待値はモックの中身 (GetRecords) から計算する (応答をそのまま信じない)。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockReadTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        async Task<List<ModuleData>> ListAsync(SearchCondition condition, int page = 0) => (await _h.IO.GetListAsync(condition, page)).Items;

        async Task<List<string>> IdsAsync(string module, MatchConditionBase? condition, params SortCondition[] sorts)
        {
            var search = new SearchCondition(module) { Condition = condition };
            search.SortConditions.AddRange(sorts);
            return (await _h.IO.GetListAsync(search, 0)).Items.Select(Id).ToList();
        }

        IReadOnlyList<JsonObject> Customers => _h.Records(MockHarness.CustomerApp);
        IReadOnlyList<JsonObject> Orders => _h.Records(MockHarness.OrderApp);

        //既定の並び (Id の降順)。顧客の Id は顧客コード
        static List<string> CodesDesc(IEnumerable<JsonObject> customers)
            => customers.Select(e => e.Text("CustomerCode")!).OrderByDescending(e => e, StringComparer.Ordinal).ToList();

        static List<string> RecordIdsDesc(IEnumerable<JsonObject> records)
            => records.Select(e => e.RecordId()).OrderByDescending(e => e).Select(e => e.ToString(CultureInfo.InvariantCulture)).ToList();

        string LastQuery(long app) => MockHarness.Query(_h.ListReads(app).Last());

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task 定義はフォームから作り列は全部nullableで計算とルックアップのコピー先は読み取り専用()
        {
            var tables = (await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!;

            Assert.That(tables.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Product", "Order" }));
            var customer = tables[0].Columns.ToDictionary(e => e.Name);
            Assert.That(customer.Keys, Does.Contain("$id").And.Contain("$revision").And.Contain("CustomerCode").And.Contain("Since").And.Contain("作成日時"));
            Assert.That(customer["CreditLimit"].NetTypeFullName, Is.EqualTo(typeof(decimal).FullName));
            Assert.That(customer["Since"].NetTypeFullName, Is.EqualTo(typeof(DateOnly).FullName));
            Assert.That(customer["作成日時"].NetTypeFullName, Is.EqualTo(typeof(DateTime).FullName));
            Assert.That(tables.SelectMany(e => e.Columns).Select(e => e.IsNullable), Is.All.True);
            var order = tables[2].Columns.ToDictionary(e => e.Name);
            Assert.That(order["StartTime"].NetTypeFullName, Is.EqualTo(typeof(TimeOnly).FullName));
            Assert.That(order["Lines"].NetTypeFullName, Is.EqualTo(typeof(string).FullName));

            var definition = await _h.Connection.GetTableDefinitionAsync("Order");
            Assert.That(definition.KeyColumns, Is.EqualTo(new[] { "$id" }));
            var readOnly = definition.Columns.Where(e => e.IsReadOnly).Select(e => e.Name).ToList();
            Assert.That(readOnly, Does.Contain("Total").And.Contain("CustomerNameCopy").And.Contain("$id").And.Contain("ステータス"));
            Assert.That(readOnly, Does.Not.Contain("CustomerCode").And.Not.Contain("OrderDate"));
            Assert.That(definition.FindColumn("Lines")!.FindSubField("Subtotal")!.IsReadOnly, Is.True);
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        // ---------------------------------------------------------------- 全件・値

        [Test]
        public async Task 全件は既定でIdの降順で値は列の型で返る()
        {
            var items = await ListAsync(new SearchCondition("Customer"));

            Assert.That(items.Select(Id), Is.EqualTo(CodesDesc(Customers)));
            var byId = items.ToDictionary(Id);
            foreach (var row in Customers)
            {
                var item = byId[row.Text("CustomerCode")!];
                Assert.That(Field<NumberFieldData>(item, "RecordId")!.Value, Is.EqualTo((decimal)row.RecordId()));
                Assert.That(Field<TextFieldData>(item, "CustomerName")!.Value, Is.EqualTo(row.Text("CustomerName")));
                Assert.That(Field<TextFieldData>(item, "Status")?.Value, Is.EqualTo(row.Text("Status")));
                Assert.That(Field<TextFieldData>(item, "Rank")?.Value, Is.EqualTo(row.Text("Rank")));
                Assert.That(Field<TextFieldData>(item, "Website")?.Value, Is.EqualTo(row.Text("Website")));
                Assert.That(Field<TextFieldData>(item, "Note")?.Value, Is.EqualTo(row.Text("Note")));
                Assert.That(Field<NumberFieldData>(item, "CreditLimit")?.Value, Is.EqualTo(row.Number("CreditLimit")));
                Assert.That(Field<DateFieldData>(item, "Since")?.Value, Is.EqualTo(row.Date("Since")));
                Assert.That(Field<DateTimeFieldData>(item, "Created")?.Value, Is.EqualTo(row.DateTimeUtc("作成日時")));
                var tags = Field<TextFieldData>(item, "Tags")?.Value;
                if (row.Array("Tags").Count == 0) Assert.That(tags, Is.Null);
                else Assert.That(JsonNode.Parse(tags!)!.AsArray().Select(e => e!.GetValue<string>()), Is.EqualTo(row.Array("Tags")));
            }
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 受注の日付時刻日時と計算フィールドの値()
        {
            var items = (await ListAsync(new SearchCondition("Order"))).ToDictionary(Id);

            Assert.That(items.Keys, Is.EqualTo(RecordIdsDesc(Orders)));
            foreach (var row in Orders)
            {
                var item = items[row.RecordId().ToString(CultureInfo.InvariantCulture)];
                Assert.That(Field<TextFieldData>(item, "OrderNo")!.Value, Is.EqualTo(row.Text("OrderNo")));
                Assert.That(Field<DateFieldData>(item, "OrderDate")?.Value, Is.EqualTo(row.Date("OrderDate")));
                Assert.That(Field<TimeFieldData>(item, "StartTime")?.Value, Is.EqualTo(row.Time("StartTime")));
                var delivery = Field<DateTimeFieldData>(item, "DeliveryAt")?.Value;
                Assert.That(delivery, Is.EqualTo(row.DateTimeUtc("DeliveryAt")));
                Assert.That(Field<NumberFieldData>(item, "Total")?.Value, Is.EqualTo(row.Number("Total")));
                Assert.That(Field<TextFieldData>(item, "Memo")?.Value, Is.EqualTo(row.Text("Memo")));
                Assert.That(Field<TextFieldData>(item, "ProcessStatus")?.Value, Is.EqualTo(row.Text("ステータス")));
            }
        }

        [Test]
        public async Task 名前に引用符と円記号を含む値の一致()
        {
            var name = Customers.Single(e => e.Text("CustomerCode") == "C-006").Text("CustomerName")!;

            var ids = await IdsAsync("Customer", Match("CustomerName.Value", MatchComparison.Equal, name));
            var like = await IdsAsync("Customer", Match("CustomerName.Value", MatchComparison.Like, "\"クォート\""));
            var note = await IdsAsync("Customer", Match("Note.Value", MatchComparison.Like, "\" と \\"));

            Assert.That(ids, Is.EqualTo(new[] { "C-006" }));
            Assert.That(like, Is.EqualTo(new[] { "C-006" }));
            Assert.That(note, Is.EqualTo(new[] { "C-006" }));
        }

        // ---------------------------------------------------------------- 比較 (種類ごと)

        static bool Compare<T>(T? value, MatchComparison comparison, T operand) where T : struct, IComparable<T>
            => value != null && comparison switch
            {
                MatchComparison.Equal => value.Value.CompareTo(operand) == 0,
                MatchComparison.NotEqual => value.Value.CompareTo(operand) != 0,
                MatchComparison.LessThan => value.Value.CompareTo(operand) < 0,
                MatchComparison.LessThanOrEqual => value.Value.CompareTo(operand) <= 0,
                MatchComparison.GreaterThan => value.Value.CompareTo(operand) > 0,
                _ => value.Value.CompareTo(operand) >= 0,
            };

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        [TestCase(MatchComparison.LessThan)]
        [TestCase(MatchComparison.LessThanOrEqual)]
        [TestCase(MatchComparison.GreaterThan)]
        [TestCase(MatchComparison.GreaterThanOrEqual)]
        public async Task 数値の各比較は空欄の行を返さない(MatchComparison comparison)
        {
            var ids = await IdsAsync("Customer", Match("CreditLimit.Value", comparison, 1200000m));

            var expected = CodesDesc(Customers.Where(e => Compare(e.Number("CreditLimit"), comparison, 1200000m)));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ids, Is.EqualTo(expected));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        [TestCase(MatchComparison.LessThan)]
        [TestCase(MatchComparison.LessThanOrEqual)]
        [TestCase(MatchComparison.GreaterThan)]
        [TestCase(MatchComparison.GreaterThanOrEqual)]
        public async Task 日付の各比較は空欄の行を返さない(MatchComparison comparison)
        {
            var operand = new DateOnly(2020, 7, 15);
            var ids = await IdsAsync("Customer", Match("Since.Value", comparison, operand));

            var expected = CodesDesc(Customers.Where(e => Compare(e.Date("Since"), comparison, operand)));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ids, Is.EqualTo(expected));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        [TestCase(MatchComparison.LessThan)]
        [TestCase(MatchComparison.GreaterThanOrEqual)]
        public async Task 時刻の各比較は空欄の行を返さない(MatchComparison comparison)
        {
            var operand = new TimeOnly(12, 0);
            var ids = await IdsAsync("Order", Match("StartTime.Value", comparison, operand));

            var expected = RecordIdsDesc(Orders.Where(e => Compare(e.Time("StartTime"), comparison, operand)));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ids, Is.EqualTo(expected));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        [TestCase(MatchComparison.LessThan)]
        [TestCase(MatchComparison.GreaterThan)]
        public async Task 日時の各比較は空欄の行を返さない(MatchComparison comparison)
        {
            var operand = new DateTime(2026, 4, 22, 3, 30, 0, DateTimeKind.Utc);
            var ids = await IdsAsync("Order", Match("DeliveryAt.Value", comparison, operand));

            var expected = RecordIdsDesc(Orders.Where(e => Compare(e.DateTimeUtc("DeliveryAt"), comparison, operand)));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ids, Is.EqualTo(expected));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        public async Task 文字の等しい等しくないは空欄の行を返さない(MatchComparison comparison)
        {
            var ids = await IdsAsync("Product", Match("ProductName.Value", comparison, "ホッチキス"));
            var website = await IdsAsync("Customer", Match("Website.Value", comparison, "https://aoba.example.com"));

            var products = _h.Records(MockHarness.ProductApp);
            Assert.That(ids, Is.EquivalentTo(products.Where(e => (e.Text("ProductName") == "ホッチキス") == (comparison == MatchComparison.Equal)).Select(e => e.Text("ProductCode"))));
            Assert.That(website, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Website") != null && (e.Text("Website") == "https://aoba.example.com") == (comparison == MatchComparison.Equal)))));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        public async Task 選択の等しい等しくないは空欄の行を返さない(MatchComparison comparison)
        {
            var status = await IdsAsync("Customer", Match("Status.Value", comparison, "取引中"));
            var rank = await IdsAsync("Customer", Match("Rank.Value", comparison, "A"));

            Assert.That(status, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Status") != null && (e.Text("Status") == "取引中") == (comparison == MatchComparison.Equal)))));
            Assert.That(rank, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Rank") != null && (e.Text("Rank") == "A") == (comparison == MatchComparison.Equal)))));
            if (comparison == MatchComparison.Equal) Assert.That(LastQuery(MockHarness.CustomerApp), Does.StartWith("Rank in (\"A\")"));
        }

        [Test]
        public async Task レコード番号と作成日時の比較()
        {
            var ids = await IdsAsync("CustomerById", Match("Id.Value", MatchComparison.GreaterThan, "10"));
            var created = await IdsAsync("Customer", Match("Created.Value", MatchComparison.GreaterThanOrEqual, new DateTime(2026, 1, 13, 0, 0, 0, DateTimeKind.Utc)));

            Assert.That(ids, Is.EqualTo(RecordIdsDesc(Customers.Where(e => e.RecordId() > 10))));
            Assert.That(created, Is.EqualTo(CodesDesc(Customers.Where(e => e.DateTimeUtc("作成日時") >= new DateTime(2026, 1, 13, 0, 0, 0, DateTimeKind.Utc)))));
        }

        // ---------------------------------------------------------------- 空

        [TestCase("CreditLimit", "CreditLimit")]
        [TestCase("Status", "Status")]
        [TestCase("Since", "Since")]
        [TestCase("Note", "Note")]
        [TestCase("Website", "Website")]
        public async Task 空との一致と空でない(string field, string code)
        {
            var isNull = await IdsAsync("Customer", Match(field + ".Value", MatchComparison.Equal, null));
            var notNull = await IdsAsync("Customer", Match(field + ".Value", MatchComparison.NotEqual, null));
            var notIsNull = await IdsAsync("Customer", Not(Match(field + ".Value", MatchComparison.Equal, null)));

            var expected = CodesDesc(Customers.Where(e => e.Text(code) == null));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(isNull, Is.EqualTo(expected));
            Assert.That(notNull, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text(code) != null))));
            Assert.That(notIsNull, Is.EqualTo(notNull));
        }

        [Test]
        public async Task 空の判定の書き方は種類ごと()
        {
            await IdsAsync("Customer", Match("CreditLimit.Value", MatchComparison.Equal, null));
            var number = LastQuery(MockHarness.CustomerApp);
            await IdsAsync("Customer", Match("Note.Value", MatchComparison.Equal, null));
            var longText = LastQuery(MockHarness.CustomerApp);
            await IdsAsync("Customer", Match("Status.Value", MatchComparison.Equal, null));
            var selection = LastQuery(MockHarness.CustomerApp);

            Assert.That(number, Does.StartWith("CreditLimit = \"\""));
            Assert.That(longText, Does.StartWith("Note is empty"));
            Assert.That(selection, Does.StartWith("Status in (\"\")"));
        }

        [Test]
        public async Task 未入力のチェックボックスとの一致()
        {
            var isNull = await IdsAsync("Customer", Match("Tags.Value", MatchComparison.Equal, null));
            var notNull = await IdsAsync("Customer", Match("Tags.Value", MatchComparison.NotEqual, null));

            Assert.That(isNull, Is.EqualTo(CodesDesc(Customers.Where(e => e.Array("Tags").Count == 0))));
            Assert.That(notNull, Is.EqualTo(CodesDesc(Customers.Where(e => e.Array("Tags").Count != 0))));
        }

        [Test]
        public async Task 空になりえない列の空の判定は要求に書かない()
        {
            var none = await IdsAsync("CustomerById", Match("Id.Value", MatchComparison.Equal, null));
            var all = await IdsAsync("CustomerById", Not(Match("Id.Value", MatchComparison.Equal, null)));

            Assert.That(none, Is.Empty);
            Assert.That(all, Has.Count.EqualTo(Customers.Count));
        }

        // ---------------------------------------------------------------- in / like

        [Test]
        public async Task inとnot_inはnot_inが空の行を返さない()
        {
            var values = new List<string> { "取引中", "休止" };
            var inIds = await IdsAsync("Customer", Match("Status.Value", MatchComparison.In, values));
            var notInIds = await IdsAsync("Customer", Match("Status.Value", MatchComparison.NotIn, values));
            var numbers = await IdsAsync("Customer", Match("CreditLimit.Value", MatchComparison.In, new List<decimal> { 0m, 600000m, 4500000.5m }));
            var notNumbers = await IdsAsync("Customer", Match("CreditLimit.Value", MatchComparison.NotIn, new List<decimal> { 0m, 600000m }));
            var texts = await IdsAsync("Customer", Match("CustomerName.Value", MatchComparison.In, new List<string> { "港南物産", "みなと印刷", "該当なし" }));

            Assert.That(inIds, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Status") is { } s && values.Contains(s)))));
            Assert.That(notInIds, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Status") is { } s && !values.Contains(s)))));
            Assert.That(numbers, Is.EqualTo(new[] { "C-015", "C-013", "C-005" }));
            Assert.That(notNumbers, Is.EqualTo(CodesDesc(Customers.Where(e => e.Number("CreditLimit") is { } n && n != 0m && n != 600000m))));
            Assert.That(texts, Is.EqualTo(new[] { "C-007", "C-002" }));
        }

        [Test]
        public async Task 空のinはどの行にも合わず空のnot_inは全部の行()
        {
            var inIds = await IdsAsync("Customer", Match("Status.Value", MatchComparison.In, new List<string>()));
            var notInIds = await IdsAsync("Customer", Match("Status.Value", MatchComparison.NotIn, new List<string>()));

            //本体が SQL で 1=0 / 1=1 にする (空の行も含めて全部)
            Assert.That(inIds, Is.Empty);
            Assert.That(notInIds, Is.EqualTo(CodesDesc(Customers)));
        }

        [TestCase("株式会社")]
        [TestCase("港南")]
        [TestCase("該当なし")]
        public async Task likeは部分一致(string text)
        {
            var ids = await IdsAsync("Customer", Match("CustomerName.Value", MatchComparison.Like, text));
            var notIds = await IdsAsync("Customer", Not(Match("CustomerName.Value", MatchComparison.Like, text)));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("CustomerName")!.Contains(text, StringComparison.Ordinal)))));
            Assert.That(notIds, Is.EqualTo(CodesDesc(Customers.Where(e => !e.Text("CustomerName")!.Contains(text, StringComparison.Ordinal)))));
            Assert.That(LastQuery(MockHarness.CustomerApp), Does.StartWith($"(CustomerName not like \"{text}\" and CustomerName != \"\")"));
        }

        [Test]
        public async Task 複数行の文字とリッチテキストのlike()
        {
            var note = await IdsAsync("Customer", Match("Note.Value", MatchComparison.Like, "取引"));
            var notNote = await IdsAsync("Customer", Not(Match("Note.Value", MatchComparison.Like, "取引")));
            var memo = await IdsAsync("Order", Match("Memo.Value", MatchComparison.Like, "メモ"));

            Assert.That(note, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Note")?.Contains("取引") == true))));
            Assert.That(notNote, Is.EqualTo(CodesDesc(Customers.Where(e => e.Text("Note") is { } n && !n.Contains("取引")))));
            Assert.That(memo, Is.EqualTo(RecordIdsDesc(Orders.Where(e => e.Text("Memo")?.Contains("メモ") == true))));
        }

        /// <summary>kintone (モック) の like は大文字小文字を区別しない。PostgreSQL の LIKE は区別する (結果が SQL と違う所)。</summary>
        [Test]
        public async Task likeは大文字小文字を区別しない()
        {
            var ids = await IdsAsync("Customer", Match("CustomerName.Value", MatchComparison.Like, "blue harbor"));

            Assert.That(ids, Is.EqualTo(new[] { "C-013" }));
        }

        [Test]
        public async Task 選択のlikeは書けないのでエラー()
        {
            var ex = Assert.ThrowsAsync<KintoneException>(() => IdsAsync("Customer", Match("Status.Value", MatchComparison.Like, "取引")));

            Assert.That(ex!.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
            Assert.That(_h.ListReads(MockHarness.CustomerApp), Is.Empty);
        }

        [Test]
        public async Task 日付の範囲は空欄の行を返さない()
        {
            var ids = await IdsAsync("Customer", MultiMatchCondition.And(
                Match("Since.Value", MatchComparison.GreaterThanOrEqual, new DateOnly(2020, 1, 1)),
                Match("Since.Value", MatchComparison.LessThan, new DateOnly(2025, 1, 1))));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(e => e.Date("Since") is { } d && d >= new DateOnly(2020, 1, 1) && d < new DateOnly(2025, 1, 1)))));
        }

        // ---------------------------------------------------------------- and / or / not

        [Test]
        public async Task 入れ子のandとorとnot()
        {
            //(Status = 取引中 and not (CreditLimit > 2000000)) or (Rank = C and Note like 取引)
            var condition = MultiMatchCondition.Or(
                MultiMatchCondition.And(
                    Match("Status.Value", MatchComparison.Equal, "取引中"),
                    Not(Match("CreditLimit.Value", MatchComparison.GreaterThan, 2000000m))),
                MultiMatchCondition.And(
                    Match("Rank.Value", MatchComparison.Equal, "C"),
                    Match("Note.Value", MatchComparison.Like, "取引")));

            var ids = await IdsAsync("Customer", condition);

            var expected = Customers.Where(e =>
                (e.Text("Status") == "取引中" && e.Number("CreditLimit") is { } n && !(n > 2000000m))
                || (e.Text("Rank") == "C" && e.Text("Note")?.Contains("取引") == true));
            Assert.That(ids, Is.EqualTo(CodesDesc(expected)));
        }

        [Test]
        public async Task 否定の否定は元の条件と同じ()
        {
            var ids = await IdsAsync("Customer", Not(Not(Match("CreditLimit.Value", MatchComparison.LessThan, 1000000m))));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(e => e.Number("CreditLimit") < 1000000m))));
        }

        [Test]
        public async Task 否定のorは空欄の行を返さない()
        {
            //not (CreditLimit < 1000000 or Since < 2020-01-01): どちらかが空なら unknown で返さない
            var ids = await IdsAsync("Customer", Not(MultiMatchCondition.Or(
                Match("CreditLimit.Value", MatchComparison.LessThan, 1000000m),
                Match("Since.Value", MatchComparison.LessThan, new DateOnly(2020, 1, 1)))));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(e =>
                e.Number("CreditLimit") is { } n && e.Date("Since") is { } d && !(n < 1000000m) && !(d < new DateOnly(2020, 1, 1))))));
        }

        [Test]
        public async Task 否定のandは片方が偽なら空欄の行も返す()
        {
            //not (CreditLimit > 1000000 and Status = 取引中): Status が取引中でない行は CreditLimit が空でも真
            var ids = await IdsAsync("Customer", Not(MultiMatchCondition.And(
                Match("CreditLimit.Value", MatchComparison.GreaterThan, 1000000m),
                Match("Status.Value", MatchComparison.Equal, "取引中"))));

            bool? Gt(JsonObject e) => e.Number("CreditLimit") is { } n ? n > 1000000m : null;
            bool? Eq(JsonObject e) => e.Text("Status") is { } s ? s == "取引中" : null;
            var expected = Customers.Where(e =>
            {
                var a = Gt(e);
                var b = Eq(e);
                bool? and = a == false || b == false ? false : a == true && b == true ? true : null;
                return and == false;
            });
            Assert.That(ids, Is.EqualTo(CodesDesc(expected)));
            Assert.That(ids, Does.Contain("C-012"), "CreditLimit が空・Status が休止の行");
        }

        // ---------------------------------------------------------------- どの行とも等しくならない値 (PostgreSQL の 3 値の論理)

        //本体の形の SQL (where だけ差し替える) で読んだ行のレコード ID (昇順)
        async Task<List<long>> RecordIdsBySqlAsync(string from, string where, params (string Name, object? Value)[] args)
        {
            var table = from.Split('"')[1];
            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, $"select \"{table}\".\"$id\" as \"id\" from {from} where {where}",
                args.ToDictionary(e => e.Name, e => new Codeer.LowCode.Blazor.DataIO.Db.ParamAndRawDbTypeName { Value = e.Value }));
            return rows.Select(e => Convert.ToInt64(e["id"], CultureInfo.InvariantCulture)).OrderBy(e => e).ToList();
        }

        static List<long> RecordIds(IEnumerable<JsonObject> records) => records.Select(e => e.RecordId()).OrderBy(e => e).ToList();

        //(表, 列, 合わない値 2 つ, 列が空でない行)
        static (string Table, string Column, object First, object Second, Func<JsonObject, bool> NotEmpty) NeverEqualCase(string name) => name switch
        {
            "日付の列に時刻の入った値" => ("Order", "OrderDate", new DateTime(2026, 4, 22, 15, 30, 0), new DateTime(2026, 5, 2, 0, 0, 1), e => e.Text("OrderDate") != null),
            "時刻の列に秒のある値" => ("Order", "StartTime", new TimeOnly(9, 15, 30), new TimeOnly(10, 30, 1), e => e.Text("StartTime") != null),
            "ユーザーの列に関数の形の文字" => ("Order", "Assignee", "LOGINUSER()", "primary_organization( )", e => e.Array("Assignee").Count != 0),
            "数値の列に数値でない文字" => ("Customer", "CreditLimit", "abc", "1x", e => e.Text("CreditLimit") != null),
            "日付の列に書式違いの文字" => ("Customer", "Since", "2019/04/01", "20190401", e => e.Text("Since") != null),
            _ => throw new ArgumentException(name),
        };

        [TestCase("日付の列に時刻の入った値")]
        [TestCase("時刻の列に秒のある値")]
        [TestCase("ユーザーの列に関数の形の文字")]
        [TestCase("数値の列に数値でない文字")]
        [TestCase("日付の列に書式違いの文字")]
        public async Task どの行とも等しくならない値の否定は空でない行だけ(string name)
        {
            var (table, column, first, second, notEmpty) = NeverEqualCase(name);
            var c = $"\"{table}\".\"{column}\"";
            var records = _h.Records(table == "Order" ? MockHarness.OrderApp : MockHarness.CustomerApp);
            var nonEmpty = RecordIds(records.Where(notEmpty));
            Assert.That(nonEmpty, Is.Not.Empty.And.Count.LessThan(records.Count), "空の行と値のある行が混ざっている");

            Task<List<long>> Ids(string where) => RecordIdsBySqlAsync($"\"{table}\"", where, ("@p0", first), ("@p1", second));
            Assert.That(await Ids($"not ({c} = @p0)"), Is.EqualTo(nonEmpty), "not (列 = 値)");
            Assert.That(await Ids($"{c} not in (@p0, @p1)"), Is.EqualTo(nonEmpty), "列 Not In (値, 値)");
            Assert.That(await Ids($"{c} <> @p0"), Is.EqualTo(nonEmpty), "列 <> 値");
            Assert.That(await Ids($"not ({c} <> @p0)"), Is.Empty, "not (列 <> 値)");
            Assert.That(await Ids($"{c} = @p0"), Is.Empty, "列 = 値");
        }

        [Test]
        public async Task Inの一部だけが合わない値は合う値の分だけ効く()
        {
            var bad = new DateTime(2026, 4, 22, 15, 30, 0);
            var good = new DateOnly(2026, 4, 22);

            var inIds = await RecordIdsBySqlAsync("\"Order\"", "\"Order\".\"OrderDate\" in (@p0, @p1)", ("@p0", bad), ("@p1", good));
            var notInIds = await RecordIdsBySqlAsync("\"Order\"", "\"Order\".\"OrderDate\" not in (@p0, @p1)", ("@p0", bad), ("@p1", good));

            Assert.That(inIds, Is.EqualTo(RecordIds(Orders.Where(e => e.Date("OrderDate") == good))).And.Not.Empty);
            //空の行は not in でも unknown (合わない)
            Assert.That(notInIds, Is.EqualTo(RecordIds(Orders.Where(e => e.Date("OrderDate") is { } d && d != good))));
        }

        [Test]
        public async Task orの中のどの行とも等しくならない値()
        {
            var bad = new DateTime(2026, 4, 22, 15, 30, 0);
            //受注日が空の受注 (受注 9) の進捗を仮でないものにする (not の中で受注日が unknown になる行)
            _h.Mock.UpdateRecord(MockHarness.OrderApp, Orders.Single(e => e.Text("OrderDate") == null).RecordId(), new Dictionary<string, object?> { ["Stage"] = "受注" });

            var or = await RecordIdsBySqlAsync("\"Order\"", "\"Order\".\"OrderDate\" = @p0 or \"Order\".\"Stage\" = @p1", ("@p0", bad), ("@p1", "仮"));
            var notOr = await RecordIdsBySqlAsync("\"Order\"", "not (\"Order\".\"OrderDate\" = @p0 or \"Order\".\"Stage\" = @p1)", ("@p0", bad), ("@p1", "仮"));
            var orNotEqual = await RecordIdsBySqlAsync("\"Order\"", "\"Order\".\"OrderDate\" <> @p0 or \"Order\".\"Stage\" = @p1", ("@p0", bad), ("@p1", "仮"));

            Assert.That(or, Is.EqualTo(RecordIds(Orders.Where(e => e.Text("Stage") == "仮"))));
            //not (unknown か偽 or ...) は、日付が空でなく (偽) 進捗が空でなく仮でない行
            Assert.That(notOr, Is.EqualTo(RecordIds(Orders.Where(e => e.Text("OrderDate") != null && e.Text("Stage") is { } s && s != "仮"))));
            Assert.That(notOr, Does.Not.Contain(Orders.Single(e => e.Text("OrderDate") == null).RecordId()));
            Assert.That(orNotEqual, Is.EqualTo(RecordIds(Orders.Where(e => e.Text("OrderDate") != null || e.Text("Stage") == "仮"))));
        }

        [TestCase("Since", "日付")]
        [TestCase("CreditLimit", "数値")]
        public async Task 結合先の列のどの行とも等しくならない値(string column, string kind)
        {
            object bad = kind == "日付" ? new DateTime(2019, 4, 1, 15, 30, 0) : "abc";
            //受注の 1 件を、取引開始日・与信限度額が空の顧客 (C-003) に向ける
            _h.Mock.UpdateRecord(MockHarness.OrderApp, Orders.First(e => e.Text("CustomerCode") == "C-001").RecordId(), new Dictionary<string, object?> { ["CustomerCode"] = "C-003" });
            const string from = "\"Order\" left join \"Customer\" as \"Customer_1\" on \"Order\".\"CustomerCode\"=\"Customer_1\".\"CustomerCode\"";
            var c = $"\"Customer_1\".\"{column}\"";
            var customers = Customers.ToDictionary(e => e.Text("CustomerCode")!);
            //結合先の行があり、その列が空でない受注 (顧客の無い受注・顧客の列が空の受注は unknown)
            var expected = RecordIds(Orders.Where(e => e.Text("CustomerCode") is { } code && customers.TryGetValue(code, out var customer) && customer.Text(column) != null));
            Assert.That(expected, Is.Not.Empty.And.Count.LessThan(Orders.Count(e => e.Text("CustomerCode") != null)), "結合先の列が空の受注もある");

            Assert.That(await RecordIdsBySqlAsync(from, $"not ({c} = @p0)", ("@p0", bad)), Is.EqualTo(expected), "not (列 = 値)");
            Assert.That(await RecordIdsBySqlAsync(from, $"{c} not in (@p0)", ("@p0", bad)), Is.EqualTo(expected), "列 Not In (値)");
            Assert.That(await RecordIdsBySqlAsync(from, $"{c} <> @p0", ("@p0", bad)), Is.EqualTo(expected), "列 <> 値");
            Assert.That(await RecordIdsBySqlAsync(from, $"not ({c} <> @p0)", ("@p0", bad)), Is.Empty, "not (列 <> 値)");
            Assert.That(await RecordIdsBySqlAsync(from, $"{c} = @p0", ("@p0", bad)), Is.Empty, "列 = 値");
        }

        // ---------------------------------------------------------------- 乱数の条件と SQL の 3 値の論理の突き合わせ

        sealed record Case(MatchConditionBase Condition, Func<JsonObject, bool?> Evaluate, string Text);

        sealed class Generator
        {
            readonly Random _random;

            public Generator(int seed) => _random = new Random(seed);

            public Case Next() => Node(0);

            Case Node(int depth)
            {
                var r = _random.Next(depth == 0 ? 3 : 5);
                if (depth >= 3 || r >= 3) return Leaf();
                if (r == 0)
                {
                    var child = Node(depth + 1);
                    return new Case(Not(child.Condition), row => !child.Evaluate(row), $"not ({child.Text})");
                }
                var children = Enumerable.Range(0, 2 + _random.Next(2)).Select(_ => Node(depth + 1)).ToList();
                if (r == 1)
                {
                    return new Case(MultiMatchCondition.And(children.Select(e => e.Condition).ToArray()), row =>
                    {
                        var values = children.Select(e => e.Evaluate(row)).ToList();
                        return values.Any(e => e == false) ? false : values.All(e => e == true) ? true : null;
                    }, "(" + string.Join(" and ", children.Select(e => e.Text)) + ")");
                }
                return new Case(MultiMatchCondition.Or(children.Select(e => e.Condition).ToArray()), row =>
                {
                    var values = children.Select(e => e.Evaluate(row)).ToList();
                    return values.Any(e => e == true) ? true : values.All(e => e == false) ? false : null;
                }, "(" + string.Join(" or ", children.Select(e => e.Text)) + ")");
            }

            static MatchComparison Comparison(int i) => i switch
            {
                0 => MatchComparison.Equal,
                1 => MatchComparison.NotEqual,
                2 => MatchComparison.LessThan,
                3 => MatchComparison.LessThanOrEqual,
                4 => MatchComparison.GreaterThan,
                _ => MatchComparison.GreaterThanOrEqual,
            };

            static bool? Cmp<T>(T? value, MatchComparison comparison, T operand) where T : struct, IComparable<T>
                => value == null ? null : Compare(value, comparison, operand);

            Case Leaf()
            {
                switch (_random.Next(10))
                {
                    case 0:
                        {
                            var c = Comparison(_random.Next(6));
                            var n = Pick(0m, 600000m, 1200000m, 4500000.5m);
                            return new Case(Match("CreditLimit.Value", c, n), row => Cmp(row.Number("CreditLimit"), c, n), $"credit {c} {n}");
                        }
                    case 1:
                        {
                            var c = Comparison(_random.Next(6));
                            var d = Pick(new DateOnly(2018, 11, 20), new DateOnly(2021, 1, 1), new DateOnly(2026, 2, 1));
                            return new Case(Match("Since.Value", c, d), row => Cmp(row.Date("Since"), c, d), $"since {c} {d:yyyy-MM-dd}");
                        }
                    case 2:
                        {
                            var c = _random.Next(2) == 0 ? MatchComparison.Equal : MatchComparison.NotEqual;
                            var s = Pick("取引中", "見込み", "休止");
                            return new Case(Match("Status.Value", c, s), row => row.Text("Status") is { } v ? (v == s) == (c == MatchComparison.Equal) : null, $"status {c} {s}");
                        }
                    case 3:
                        {
                            var c = _random.Next(2) == 0 ? MatchComparison.In : MatchComparison.NotIn;
                            var values = Pick(new List<string> { "取引中" }, new List<string> { "見込み", "休止" });
                            return new Case(Match("Status.Value", c, values), row => row.Text("Status") is { } v ? values.Contains(v) == (c == MatchComparison.In) : null,
                                $"status {c} ({string.Join(",", values)})");
                        }
                    case 4:
                        {
                            var text = Pick("株式会社", "港南", "物産", "該当なし");
                            return new Case(Match("CustomerName.Value", MatchComparison.Like, text), row => row.Text("CustomerName") is { } v ? v.Contains(text, StringComparison.Ordinal) : null,
                                $"name like {text}");
                        }
                    case 5:
                        {
                            var text = Pick("取引", "メモ", "支店");
                            return new Case(Match("Note.Value", MatchComparison.Like, text), row => row.Text("Note") is { } v ? v.Contains(text, StringComparison.Ordinal) : null,
                                $"note like {text}");
                        }
                    case 6:
                        {
                            var field = Pick(("CreditLimit", "CreditLimit"), ("Since", "Since"), ("Status", "Status"), ("Note", "Note"));
                            return new Case(Match(field.Item1 + ".Value", MatchComparison.Equal, null), row => row.Text(field.Item2) == null, $"{field.Item1} is null");
                        }
                    case 7:
                        {
                            var field = Pick(("CreditLimit", "CreditLimit"), ("Since", "Since"), ("Status", "Status"), ("Note", "Note"));
                            return new Case(Match(field.Item1 + ".Value", MatchComparison.NotEqual, null), row => row.Text(field.Item2) != null, $"{field.Item1} is not null");
                        }
                    case 8:
                        {
                            var c = _random.Next(2) == 0 ? MatchComparison.Equal : MatchComparison.NotEqual;
                            var s = Pick("A", "B", "C");
                            return new Case(Match("Rank.Value", c, s), row => row.Text("Rank") is { } v ? (v == s) == (c == MatchComparison.Equal) : null, $"rank {c} {s}");
                        }
                    default:
                        {
                            var c = _random.Next(2) == 0 ? MatchComparison.In : MatchComparison.NotIn;
                            var values = Pick(new List<decimal> { 0m, 300000m }, new List<decimal> { 5000000m, 8000000m, 150000m });
                            return new Case(Match("CreditLimit.Value", c, values), row => row.Number("CreditLimit") is { } v ? values.Contains(v) == (c == MatchComparison.In) : null,
                                $"credit {c} ({string.Join(",", values)})");
                        }
                }
            }

            T Pick<T>(params T[] values) => values[_random.Next(values.Length)];
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task 乱数の条件でもSQLの3値の論理と同じ結果(int seed)
        {
            var generator = new Generator(seed);
            var customers = Customers;
            var failures = new List<string>();

            for (var i = 0; i < 80; i++)
            {
                var c = generator.Next();
                var expected = CodesDesc(customers.Where(e => c.Evaluate(e) == true));
                List<string> ids;
                try
                {
                    ids = await IdsAsync("Customer", c.Condition);
                }
                catch (Exception e)
                {
                    failures.Add($"{c.Text}: {e.GetType().Name} {e.Message}");
                    continue;
                }
                if (!ids.SequenceEqual(expected))
                    failures.Add($"{c.Text}: expected [{string.Join(",", expected)}] but [{string.Join(",", ids)}] / {LastQuery(MockHarness.CustomerApp)}");
            }

            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }

        // ---------------------------------------------------------------- 件数・ページ送り

        [Test]
        public async Task ページ送りと件数()
        {
            var condition = new SearchCondition("Customer") { LimitCount = 4 };
            condition.SortConditions.Add(new SortCondition { Variable = "CustomerName.Value" });
            var all = Customers.OrderBy(e => e.Text("CustomerName"), StringComparer.Ordinal).ThenByDescending(e => e.RecordId()).Select(e => e.Text("CustomerCode")!).ToList();

            var pages = new List<string>();
            for (var page = 0; page < 4; page++)
            {
                var result = await _h.IO.GetListAsync(condition, page);
                Assert.That(result.TotalCount, Is.EqualTo(Customers.Count));
                pages.AddRange(result.Items.Select(Id));
            }

            Assert.That(pages, Is.EqualTo(all));
            Assert.That(MockHarness.Query(_h.ListReads(MockHarness.CustomerApp)[1]), Does.EndWith("order by CustomerName asc, $id asc limit 4 offset 4"));
            Assert.That(_h.CountReads(MockHarness.CustomerApp), Is.Not.Empty);
            Assert.That(MockHarness.Query(_h.CountReads(MockHarness.CustomerApp)[0]), Does.EndWith("limit 1"));
        }

        [Test]
        public async Task 件数は条件付きでも合う()
        {
            var result = await _h.IO.GetListAsync(new SearchCondition("Customer") { Condition = Match("Status.Value", MatchComparison.Equal, "取引中"), LimitCount = 2 }, 1);

            var expected = CodesDesc(Customers.Where(e => e.Text("Status") == "取引中"));
            Assert.That(result.TotalCount, Is.EqualTo(expected.Count));
            Assert.That(result.Items.Select(Id), Is.EqualTo(expected.Skip(2).Take(2)));
        }

        [Test]
        public async Task 並びなし件数なしの全件はレコードIDで読み進める()
        {
            for (var i = 0; i < 1100; i++) _h.Mock.AddRecord(MockHarness.ProductApp, new Dictionary<string, object?> { ["ProductCode"] = "B-" + i.ToString("0000", CultureInfo.InvariantCulture), ["UnitPrice"] = "100" });
            _h.Mock.ClearRequests();

            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, "SELECT \"Product\".\"ProductCode\" FROM \"Product\"", new());

            Assert.That(rows, Has.Count.EqualTo(1108));
            var queries = _h.ListReads(MockHarness.ProductApp).Select(MockHarness.Query).ToList();
            Assert.That(queries, Has.Count.EqualTo(3));
            Assert.That(queries[0], Is.EqualTo("order by $id asc limit 500"));
            Assert.That(queries[1], Does.StartWith("$id > ").And.EndWith("order by $id asc limit 500"));
        }

        [Test]
        public async Task 並びありの全件はoffsetで読む()
        {
            for (var i = 0; i < 600; i++) _h.Mock.AddRecord(MockHarness.ProductApp, new Dictionary<string, object?> { ["ProductCode"] = "B-" + i.ToString("0000", CultureInfo.InvariantCulture), ["UnitPrice"] = (i % 7).ToString(CultureInfo.InvariantCulture) });
            _h.Mock.ClearRequests();

            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, "SELECT \"Product\".\"ProductCode\" FROM \"Product\" ORDER BY \"Product\".\"UnitPrice\" DESC", new());

            var expected = _h.Records(MockHarness.ProductApp).OrderByDescending(e => e.Number("UnitPrice")).ThenBy(e => e.RecordId()).Select(e => e.Text("ProductCode")).ToList();
            Assert.That(rows.Select(e => (string?)e["ProductCode"]), Is.EqualTo(expected));
            var queries = _h.ListReads(MockHarness.ProductApp).Select(MockHarness.Query).ToList();
            Assert.That(queries, Is.EqualTo(new[] { "order by UnitPrice desc, $id asc limit 500","order by UnitPrice desc, $id asc limit 500 offset 500" }));
        }

        [Test]
        public async Task 全件がMaxRowsを超えたらエラー()
        {
            await using var h = await MockHarness.StartAsync(edit: s => s.MaxRows = 10);

            var ex = Assert.ThrowsAsync<KintoneException>(() => h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = null }, 0));
            var ok = await h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = 5 }, 0);

            Assert.That(ex!.Message, Does.Contain("MaxRows"));
            Assert.That(ok.Items, Has.Count.EqualTo(5));
        }

        [Test]
        public async Task offsetが上限を超えたらエラー()
        {
            var condition = new SearchCondition("Customer") { LimitCount = 500 };

            var ex = Assert.ThrowsAsync<KintoneException>(() => _h.IO.GetListAsync(condition, 21));

            Assert.That(ex!.Message, Does.Contain("10000"));
        }

        // ---------------------------------------------------------------- 並び

        [Test]
        public async Task 文字の並びは降順と2つの項目()
        {
            var desc = await IdsAsync("Customer", null, new SortCondition { Variable = "CustomerName.Value", IsDescending = true });
            var two = await IdsAsync("Customer", null, new SortCondition { Variable = "Rank.Value" }, new SortCondition { Variable = "CreditLimit.Value", IsDescending = true });

            Assert.That(desc, Is.EqualTo(Customers.OrderByDescending(e => e.Text("CustomerName"), StringComparer.Ordinal).Select(e => e.Text("CustomerCode"))));
            //空の値は昇順で先・降順で後 (kintone の並び)。PostgreSQL の既定 (昇順で後) と逆
            var expected = Customers.OrderBy(e => e.Text("Rank"), StringComparer.Ordinal)
                .ThenByDescending(e => e.Number("CreditLimit") ?? decimal.MinValue).ThenBy(e => e.RecordId()).Select(e => e.Text("CustomerCode"));
            Assert.That(two, Is.EqualTo(expected));
        }

        /// <summary>空の値の並び: kintone (モック) は昇順で先。PostgreSQL の既定は昇順で後 (結果が SQL と違う所)。</summary>
        [Test]
        public async Task 空の値は昇順で先に並ぶ()
        {
            var asc = await IdsAsync("Customer", null, new SortCondition { Variable = "CreditLimit.Value" });

            var empties = Customers.Where(e => e.Number("CreditLimit") == null).Select(e => e.Text("CustomerCode")!).ToHashSet();
            Assert.That(asc.Take(empties.Count), Is.EquivalentTo(empties));
            Assert.That(asc.Skip(empties.Count), Is.EqualTo(Customers.Where(e => e.Number("CreditLimit") != null)
                .OrderBy(e => e.Number("CreditLimit")).ThenBy(e => e.RecordId()).Select(e => e.Text("CustomerCode"))));
        }

        [Test]
        public async Task 日付の並び()
        {
            var ids = await IdsAsync("Order", null, new SortCondition { Variable = "OrderDate.Value", IsDescending = true });

            var expected = Orders.OrderByDescending(e => e.Date("OrderDate") ?? DateOnly.MinValue).ThenBy(e => e.RecordId()).Select(e => e.RecordId().ToString(CultureInfo.InvariantCulture));
            Assert.That(ids, Is.EqualTo(expected));
        }

        // ---------------------------------------------------------------- 詳細・ゲストスペース

        [Test]
        public async Task 詳細はIdで1件()
        {
            var customer = (await ListAsync(new SearchCondition("Customer") { Condition = Match("Id.Value", MatchComparison.Equal, "C-013") })).Single();
            var order = (await ListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, "4") })).Single();

            Assert.That(Field<TextFieldData>(customer, "CustomerName")!.Value, Is.EqualTo("Blue Harbor Trading"));
            Assert.That(Field<NumberFieldData>(customer, "CreditLimit")!.Value, Is.EqualTo(4500000.5m));
            Assert.That(Field<TextFieldData>(order, "OrderNo")!.Value, Is.EqualTo("SO-2026-0004"));
        }

        [Test]
        public async Task ゲストスペースのアプリはゲストのパスで読む()
        {
            var items = await ListAsync(new SearchCondition("Guest"));
            var budget = await IdsAsync("Guest", Match("Budget.Value", MatchComparison.Equal, null));

            Assert.That(items.Select(e => Field<TextFieldData>(e, "Title")!.Value), Is.EqualTo(_h.Records(MockHarness.GuestApp).Reverse().Select(e => e.Text("Title"))));
            Assert.That(budget, Is.EqualTo(new[] { "2" }));
            Assert.That(_h.Reads(MockHarness.GuestApp), Is.Not.Empty);
            Assert.That(_h.Reads(MockHarness.GuestApp).Select(e => e.GuestSpaceId), Is.All.EqualTo(MockHarness.GuestSpace));
        }
    }
}
