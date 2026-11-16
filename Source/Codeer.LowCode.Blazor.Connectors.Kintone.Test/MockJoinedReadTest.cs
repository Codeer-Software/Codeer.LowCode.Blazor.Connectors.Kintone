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
    /// 受注 → 顧客のリンク (ルックアップのキー CustomerCode = 顧客の CustomerCode) をまたぐ表示・検索・並びと、
    /// 顧客の受注の一覧 (Orders) をまたぐ条件 (存在しない を含む)。結合は別読みしてメモリで突き合わせる。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockJoinedReadTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        async Task<List<string>> IdsAsync(string module, MatchConditionBase? condition, params SortCondition[] sorts)
        {
            var search = new SearchCondition(module) { Condition = condition };
            search.SortConditions.AddRange(sorts);
            return (await _h.IO.GetListAsync(search, 0)).Items.Select(Id).ToList();
        }

        IReadOnlyList<JsonObject> Customers => _h.Records(MockHarness.CustomerApp);
        IReadOnlyList<JsonObject> Orders => _h.Records(MockHarness.OrderApp);

        JsonObject? CustomerOf(JsonObject order)
            => order.Text("CustomerCode") is { } code ? Customers.FirstOrDefault(e => e.Text("CustomerCode") == code) : null;

        IEnumerable<JsonObject> OrdersOf(JsonObject customer) => Orders.Where(e => e.Text("CustomerCode") == customer.Text("CustomerCode"));

        static List<string> RecordIdsDesc(IEnumerable<JsonObject> records)
            => records.Select(e => e.RecordId()).OrderByDescending(e => e).Select(e => e.ToString(CultureInfo.InvariantCulture)).ToList();

        static List<string> CodesDesc(IEnumerable<JsonObject> customers)
            => customers.Select(e => e.Text("CustomerCode")!).OrderByDescending(e => e, StringComparer.Ordinal).ToList();

        //受注が指している顧客 (C-010) を消す (kintone でもルックアップの参照先のレコードは消せる。受注のキーは残る)
        long PointToMissingCustomer()
        {
            var customer = Customers.Single(e => e.Text("CustomerCode") == "C-010");
            Assert.That(_h.Mock.DeleteRecord(MockHarness.CustomerApp, customer.RecordId()), Is.True);
            return Orders.First(e => e.Text("CustomerCode") == "C-010").RecordId();
        }

        // ---------------------------------------------------------------- 表示

        [Test]
        public async Task リンク先の列を表示する()
        {
            var items = await _h.IO.GetListAsync(new SearchCondition("Order"), 0);

            Assert.That(items.Items.Select(Id), Is.EqualTo(RecordIdsDesc(Orders)));
            var byId = items.Items.ToDictionary(Id);
            foreach (var order in Orders)
            {
                var item = byId[order.RecordId().ToString(CultureInfo.InvariantCulture)];
                var customer = CustomerOf(order);
                Assert.That(Field<LinkFieldData>(item, "Customer")?.Value, Is.EqualTo(order.Text("CustomerCode")));
                Assert.That(Field<TextFieldData>(item, "Customer.CustomerName")?.Value, Is.EqualTo(customer?.Text("CustomerName")), order.Text("OrderNo"));
                Assert.That(Field<TextFieldData>(item, "Customer.Status")?.Value, Is.EqualTo(customer?.Text("Status")));
                Assert.That(Field<NumberFieldData>(item, "Customer.CreditLimit")?.Value, Is.EqualTo(customer?.Number("CreditLimit")));
            }
            //リンク先は値を集めて in で 1 回
            var customerReads = _h.ListReads(MockHarness.CustomerApp).Select(MockHarness.Query).ToList();
            Assert.That(customerReads, Has.Count.EqualTo(1));
            Assert.That(customerReads[0], Does.StartWith("CustomerCode in ("));
        }

        [Test]
        public async Task 相手の無いリンクの表示は空()
        {
            var id = PointToMissingCustomer();

            var item = (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, id.ToString(CultureInfo.InvariantCulture)) }, 0)).Items.Single();

            Assert.That(Field<LinkFieldData>(item, "Customer")?.Value, Is.EqualTo("C-010"));
            Assert.That(Field<TextFieldData>(item, "Customer.CustomerName")?.Value, Is.Null);
        }

        // ---------------------------------------------------------------- 検索

        [TestCase("株式会社")]
        [TestCase("港南")]
        [TestCase("該当なし")]
        public async Task リンク先の列のlikeと否定(string text)
        {
            var ids = await IdsAsync("Order", Match("Customer.CustomerName.Value", MatchComparison.Like, text));
            var notIds = await IdsAsync("Order", Not(Match("Customer.CustomerName.Value", MatchComparison.Like, text)));

            Assert.That(ids, Is.EqualTo(RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Text("CustomerName")?.Contains(text, StringComparison.Ordinal) == true))));
            //結合先の列が無い (顧客コードが空の) 受注は not でも unknown で返さない
            Assert.That(notIds, Is.EqualTo(RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Text("CustomerName") is { } n && !n.Contains(text, StringComparison.Ordinal)))));
            if (text == "該当なし")
            {
                Assert.That(ids, Is.Empty);
                Assert.That(_h.ListReads(MockHarness.OrderApp), Is.Empty.Or.Count.EqualTo(1));
            }
        }

        [Test]
        public async Task リンク先の列の空との一致は結合元が空の行と相手の無い行も返す()
        {
            var missing = PointToMissingCustomer();

            var isNull = await IdsAsync("Order", Match("Customer.CustomerName.Value", MatchComparison.Equal, null));
            var notIsNotNull = await IdsAsync("Order", Not(Match("Customer.CustomerName.Value", MatchComparison.NotEqual, null)));
            var notNull = await IdsAsync("Order", Match("Customer.CustomerName.Value", MatchComparison.NotEqual, null));

            var expected = RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Text("CustomerName") == null));
            Assert.That(expected, Does.Contain(missing.ToString(CultureInfo.InvariantCulture)));
            Assert.That(isNull, Is.EqualTo(expected));
            Assert.That(notIsNotNull, Is.EqualTo(expected));
            Assert.That(notNull, Is.EqualTo(RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Text("CustomerName") != null))));
        }

        [TestCase(MatchComparison.Equal)]
        [TestCase(MatchComparison.NotEqual)]
        public async Task リンク先の選択の列の一致(MatchComparison comparison)
        {
            var ids = await IdsAsync("Order", Match("Customer.Status.Value", comparison, "取引中"));

            Assert.That(ids, Is.EqualTo(RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Text("Status") is { } s && (s == "取引中") == (comparison == MatchComparison.Equal)))));
        }

        [Test]
        public async Task リンク先の数値の否定は空の行を返さない()
        {
            var ids = await IdsAsync("Order", Not(Match("Customer.CreditLimit.Value", MatchComparison.GreaterThan, 1000000m)));

            Assert.That(ids, Is.EqualTo(RecordIdsDesc(Orders.Where(e => CustomerOf(e)?.Number("CreditLimit") is { } n && !(n > 1000000m)))));
        }

        [Test]
        public async Task リンクの値の一致とリンク先の列と自分の列の組み合わせ()
        {
            var byLink = await IdsAsync("Order", Match("Customer.Value", MatchComparison.Equal, "C-004"));
            var mixed = await IdsAsync("Order", MultiMatchCondition.Or(
                MultiMatchCondition.And(Match("Customer.Status.Value", MatchComparison.Equal, "取引中"), Match("Stage.Value", MatchComparison.Equal, "仮")),
                Match("Customer.CustomerName.Value", MatchComparison.Like, "こもれび")));

            Assert.That(byLink, Is.EqualTo(RecordIdsDesc(Orders.Where(e => e.Text("CustomerCode") == "C-004"))));
            Assert.That(mixed, Is.EqualTo(RecordIdsDesc(Orders.Where(e =>
                (CustomerOf(e)?.Text("Status") == "取引中" && e.Text("Stage") == "仮") || CustomerOf(e)?.Text("CustomerName")?.Contains("こもれび") == true))));
        }

        // ---------------------------------------------------------------- 並び

        [TestCase(false)]
        [TestCase(true)]
        public async Task リンク先の列で並べる(bool descending)
        {
            var search = new SearchCondition("Order") { LimitCount = 6 };
            search.SortConditions.Add(new SortCondition { Variable = "Customer.CustomerName.Value", IsDescending = descending });

            var pages = new List<string>();
            for (var page = 0; page < 4; page++)
            {
                var result = await _h.IO.GetListAsync(search, page);
                Assert.That(result.TotalCount, Is.EqualTo(Orders.Count));
                pages.AddRange(result.Items.Select(Id));
            }

            //空 (顧客の無い受注) は昇順で先・降順で後。同じ値は $id の昇順 (並びの最後のキー)
            var keyed = Orders.Select(e => (Order: e, Name: CustomerOf(e)?.Text("CustomerName"))).ToList();
            var ordered = descending
                ? keyed.OrderBy(e => e.Name == null ? 1 : 0).ThenByDescending(e => e.Name, StringComparer.Ordinal)
                : keyed.OrderBy(e => e.Name == null ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal);
            var expected = ordered.ThenBy(e => e.Order.RecordId()).Select(e => e.Order.RecordId().ToString(CultureInfo.InvariantCulture)).ToList();
            Assert.That(pages, Is.EqualTo(expected));
        }

        // ---------------------------------------------------------------- 一覧 (顧客の受注) をまたぐ条件

        [Test]
        public async Task 一覧の条件で顧客を絞る()
        {
            var ids = await IdsAsync("Customer", Match("Orders.Stage.Value", MatchComparison.Equal, "仮"));

            var expected = CodesDesc(Customers.Where(c => OrdersOf(c).Any(o => o.Text("Stage") == "仮")));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ids, Is.EqualTo(expected));
        }

        [Test]
        public async Task 一覧の条件の否定は受注の無い顧客も返す()
        {
            var ids = await IdsAsync("Customer", Not(Match("Orders.Stage.Value", MatchComparison.Equal, "仮")));

            var expected = CodesDesc(Customers.Where(c => !OrdersOf(c).Any(o => o.Text("Stage") == "仮")));
            Assert.That(ids, Is.EqualTo(expected));
            Assert.That(ids, Does.Contain("C-002"), "受注の無い顧客");
        }

        [Test]
        public async Task 一覧が存在する存在しない()
        {
            var exists = await IdsAsync("Customer", new FieldValueMatchCondition { SearchTargetVariable = "Orders", Comparison = MatchComparison.Exists });
            var notExists = await IdsAsync("Customer", new FieldValueMatchCondition { SearchTargetVariable = "Orders", Comparison = MatchComparison.NotExists });
            var notNotExists = await IdsAsync("Customer", Not(new FieldValueMatchCondition { SearchTargetVariable = "Orders", Comparison = MatchComparison.NotExists }));

            var expected = CodesDesc(Customers.Where(c => OrdersOf(c).Any()));
            Assert.That(exists, Is.EqualTo(expected));
            Assert.That(notExists, Is.EqualTo(CodesDesc(Customers.Where(c => !OrdersOf(c).Any()))));
            Assert.That(notNotExists, Is.EqualTo(expected));
        }

        [Test]
        public async Task 一覧の数値と日付の条件の組み合わせ()
        {
            var ids = await IdsAsync("Customer", MultiMatchCondition.And(
                Match("Orders.Total.Value", MatchComparison.GreaterThan, 20000m),
                Match("Status.Value", MatchComparison.Equal, "取引中")));
            var dated = await IdsAsync("Customer", Match("Orders.OrderDate.Value", MatchComparison.GreaterThanOrEqual, new DateOnly(2026, 9, 1)));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(c => c.Text("Status") == "取引中" && OrdersOf(c).Any(o => o.Number("Total") > 20000m)))));
            Assert.That(dated, Is.EqualTo(CodesDesc(Customers.Where(c => OrdersOf(c).Any(o => o.Date("OrderDate") >= new DateOnly(2026, 9, 1))))));
        }

        [Test]
        public async Task 一覧の行の空の条件()
        {
            //受注の受注日が空の顧客 (受注 9 = C-013)。受注の無い顧客は含まない
            var ids = await IdsAsync("Customer", Match("Orders.OrderDate.Value", MatchComparison.Equal, null));

            Assert.That(ids, Is.EqualTo(CodesDesc(Customers.Where(c => OrdersOf(c).Any(o => o.Date("OrderDate") == null)))));
        }

        [Test]
        public async Task 顧客の受注の一覧を読む()
        {
            var customer = (await _h.IO.GetListAsync(new SearchCondition("Customer") { Condition = Match("Id.Value", MatchComparison.Equal, "C-007") }, 0)).Items.Single();
            var orders = await IdsAsync("Order", Match("Customer.Value", MatchComparison.Equal, Id(customer)));

            Assert.That(orders, Is.EqualTo(RecordIdsDesc(Orders.Where(e => e.Text("CustomerCode") == "C-007"))));
        }
    }
}
