using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// 結合先の列・一覧の条件で値を集める読み方。集めた値が 1 回の要求に入らない (100 を超える) ときの要求の分割と、
    /// 分けても結果が SQL の 3 値の論理と同じになること、MaxRows を超えたらエラーになること。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockValueSetConditionTest
    {
        static async Task<List<string>> IdsAsync(MockHarness h, string module, MatchConditionBase condition, int? limit = null)
            => (await h.IO.GetListAsync(new SearchCondition(module) { Condition = condition, LimitCount = limit }, 0)).Items.Select(Id).ToList();

        //名前が「大量 nnn」の顧客を count 社と、その受注を 1 件ずつ (受注の Stage は 3 件に 1 件が空)
        static void AddMany(MockHarness h, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var code = "M-" + i.ToString("000", CultureInfo.InvariantCulture);
                h.Mock.AddRecord(MockHarness.CustomerApp, new Dictionary<string, object?>
                {
                    ["CustomerCode"] = code,
                    ["CustomerName"] = "大量 " + i.ToString("000", CultureInfo.InvariantCulture),
                    ["CreditLimit"] = i % 5 == 0 ? "" : (i * 1000).ToString(CultureInfo.InvariantCulture),
                });
                h.Mock.AddRecord(MockHarness.OrderApp, new Dictionary<string, object?>
                {
                    ["OrderNo"] = "MO-" + i.ToString("000", CultureInfo.InvariantCulture),
                    ["CustomerCode"] = code,
                    ["Stage"] = i % 3 == 0 ? "" : "受注",
                });
            }
            h.Mock.ClearRequests();
        }

        static JsonObject? CustomerOf(MockHarness h, JsonObject order)
            => order.Text("CustomerCode") is { } code ? h.Records(MockHarness.CustomerApp).FirstOrDefault(e => e.Text("CustomerCode") == code) : null;

        static List<string> RecordIdsDesc(IEnumerable<JsonObject> records)
            => records.Select(e => e.RecordId()).OrderByDescending(e => e).Select(e => e.ToString(CultureInfo.InvariantCulture)).ToList();

        //1 回の要求のクエリに書いた値 ("..." の数)
        static int ValueCount(string query) => Regex.Matches(query, "\"(?:[^\"\\\\]|\\\\.)*\"").Count;

        // ---------------------------------------------------------------- 分割

        [Test]
        public async Task 集めた値が100を超えると要求を分けて全部読む()
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 250);

            var ids = await IdsAsync(h, "Order", Match("Customer.CustomerName.Value", MatchComparison.Like, "大量"));

            var expected = RecordIdsDesc(h.Records(MockHarness.OrderApp).Where(e => CustomerOf(h, e)?.Text("CustomerName")?.Contains("大量") == true));
            Assert.That(expected, Has.Count.EqualTo(250));
            Assert.That(ids, Is.EqualTo(expected));
            var queries = h.ListReads(MockHarness.OrderApp).Select(MockHarness.Query).ToList();
            Assert.That(queries, Has.Count.GreaterThanOrEqualTo(3));
            Assert.That(queries.Select(ValueCount), Is.All.LessThanOrEqualTo(KintoneQueryTranslator.JoinBatchSize));
        }

        [Test]
        public async Task 分けて読んでもページと件数は合う()
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 250);
            var search = new SearchCondition("Order") { Condition = Match("Customer.CustomerName.Value", MatchComparison.Like, "大量"), LimitCount = 30 };

            var page = await h.IO.GetListAsync(search, 2);

            var expected = RecordIdsDesc(h.Records(MockHarness.OrderApp).Where(e => CustomerOf(h, e)?.Text("CustomerName")?.Contains("大量") == true));
            Assert.That(expected, Has.Count.EqualTo(250));
            Assert.That(page.TotalCount, Is.EqualTo(expected.Count));
            Assert.That(page.Items.Select(Id), Is.EqualTo(expected.Skip(60).Take(30)));
        }

        [Test]
        public async Task 分けて読む結合先の列の否定は空の行を返さない()
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 250);

            var ids = await IdsAsync(h, "Order", Not(Match("Customer.CustomerName.Value", MatchComparison.Like, "大量 0")));

            var expected = RecordIdsDesc(h.Records(MockHarness.OrderApp).Where(e => CustomerOf(h, e)?.Text("CustomerName") is { } n && !n.Contains("大量 0")));
            Assert.That(ids, Is.EqualTo(expected));
        }

        [Test]
        public async Task 分けて読む結合先の列の空との一致()
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 250);

            var isNull = await IdsAsync(h, "Order", Match("Customer.CreditLimit.Value", MatchComparison.Equal, null));
            var notIsNull = await IdsAsync(h, "Order", Not(Match("Customer.CreditLimit.Value", MatchComparison.Equal, null)));

            var orders = h.Records(MockHarness.OrderApp);
            Assert.That(isNull, Is.EqualTo(RecordIdsDesc(orders.Where(e => CustomerOf(h, e)?.Number("CreditLimit") == null))));
            Assert.That(notIsNull, Is.EqualTo(RecordIdsDesc(orders.Where(e => CustomerOf(h, e)?.Number("CreditLimit") != null))));
        }

        [Test]
        public async Task 一覧の条件で集めた値が100を超えても全部読む()
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 250);

            var ids = await IdsAsync(h, "Customer", Match("Orders.Stage.Value", MatchComparison.Equal, "受注"));
            var notIds = await IdsAsync(h, "Customer", Not(Match("Orders.Stage.Value", MatchComparison.Equal, "受注")));

            var customers = h.Records(MockHarness.CustomerApp);
            var orders = h.Records(MockHarness.OrderApp);
            bool Received(JsonObject c) => orders.Any(o => o.Text("CustomerCode") == c.Text("CustomerCode") && o.Text("Stage") == "受注");
            var expected = customers.Where(Received).Select(e => e.Text("CustomerCode")!).OrderByDescending(e => e, StringComparer.Ordinal).ToList();
            Assert.That(expected, Has.Count.GreaterThan(KintoneQueryTranslator.JoinBatchSize));
            Assert.That(ids, Is.EqualTo(expected));
            Assert.That(notIds, Is.EqualTo(customers.Where(e => !Received(e)).Select(e => e.Text("CustomerCode")!).OrderByDescending(e => e, StringComparer.Ordinal)));
            Assert.That(h.ListReads(MockHarness.CustomerApp).Select(MockHarness.Query).Select(ValueCount), Is.All.LessThanOrEqualTo(KintoneQueryTranslator.JoinBatchSize));
        }

        [TestCase(3)]
        [TestCase(6)]
        public async Task 値の多い条件のorがandに並んでも結果は合う(int count)
        {
            await using var h = await MockHarness.StartAsync();
            AddMany(h, 200);
            var ors = Enumerable.Range(1, count).Select(i => (MatchConditionBase)MultiMatchCondition.Or(
                Match("Customer.CustomerName.Value", MatchComparison.Like, "大量 00"),
                Match("Customer.CustomerName.Value", MatchComparison.Like, "大量 " + i.ToString("00", CultureInfo.InvariantCulture)))).ToArray();
            var search = new SearchCondition("Order") { Condition = MultiMatchCondition.And(ors), LimitCount = 20 };

            var result = await h.IO.GetListAsync(search, 0);

            var expected = RecordIdsDesc(h.Records(MockHarness.OrderApp).Where(e => CustomerOf(h, e)?.Text("CustomerName") is { } n && n.StartsWith("大量 00", StringComparison.Ordinal)));
            Assert.That(expected, Has.Count.EqualTo(10));
            Assert.That(result.TotalCount, Is.EqualTo(10));
            Assert.That(result.Items.Select(Id), Is.EqualTo(expected));
            Assert.That(h.ListReads(MockHarness.OrderApp).Select(MockHarness.Query).Select(ValueCount), Is.All.LessThanOrEqualTo(KintoneQueryTranslator.JoinBatchSize));
        }

        // ---------------------------------------------------------------- MaxRows

        [Test]
        public async Task 結合先の値を集める読み出しもMaxRowsを超えたらエラー()
        {
            await using var h = await MockHarness.StartAsync(edit: s => s.MaxRows = 50);
            AddMany(h, 60);
            var search = new SearchCondition("Order") { Condition = Match("Customer.CustomerName.Value", MatchComparison.Like, "大量"), LimitCount = 1 };

            var ex = Assert.ThrowsAsync<KintoneException>(() => h.IO.GetListAsync(search, 0));

            Assert.That(ex!.Message, Does.Contain("MaxRows"));
        }

        [Test]
        public async Task 分けて読む行がMaxRowsを超えたらエラー()
        {
            await using var h = await MockHarness.StartAsync(edit: s => s.MaxRows = 200);
            AddMany(h, 150);
            var search = new SearchCondition("Order") { Condition = Match("Customer.CustomerName.Value", MatchComparison.Like, "大量"), LimitCount = 10 };

            var ok = await h.IO.GetListAsync(search, 0);
            Assert.That(ok.TotalCount, Is.EqualTo(150));

            for (var i = 150; i < 230; i++)
            {
                var code = "M-" + i.ToString("000", CultureInfo.InvariantCulture);
                h.Mock.AddRecord(MockHarness.CustomerApp, new Dictionary<string, object?> { ["CustomerCode"] = code, ["CustomerName"] = "大量 " + i });
                h.Mock.AddRecord(MockHarness.OrderApp, new Dictionary<string, object?> { ["OrderNo"] = "MO-" + i, ["CustomerCode"] = code });
            }
            var ex = Assert.ThrowsAsync<KintoneException>(() => h.IO.GetListAsync(search, 0));
            Assert.That(ex!.Message, Does.Contain("MaxRows"));
        }

        // ---------------------------------------------------------------- 乱数の条件と SQL の 3 値の論理の突き合わせ (結合先の列・一覧)

        sealed record Case(MatchConditionBase Condition, Func<JsonObject, bool?> Evaluate, string Text);

        sealed class Generator
        {
            readonly Random _random;
            readonly Func<JsonObject, JsonObject?> _customer;
            readonly Func<JsonObject, string, bool> _hasOrder;
            bool _usedList;

            public Generator(int seed, Func<JsonObject, JsonObject?> customer, Func<JsonObject, string, bool> hasOrderOfSameCustomerWithStage)
            {
                _random = new Random(seed);
                _customer = customer;
                _hasOrder = hasOrderOfSameCustomerWithStage;
            }

            public Case Next()
            {
                _usedList = false;
                return Node(0);
            }

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

            Case Leaf()
            {
                while (true)
                {
                    switch (_random.Next(7))
                    {
                        case 0:
                            {
                                var text = Pick("大量", "大量 0", "大量 1", "株式会社", "該当なし");
                                return new Case(Match("Customer.CustomerName.Value", MatchComparison.Like, text),
                                    row => _customer(row)?.Text("CustomerName") is { } n ? n.Contains(text, StringComparison.Ordinal) : null, $"name like {text}");
                            }
                        case 1:
                            return new Case(Match("Customer.CreditLimit.Value", MatchComparison.Equal, null), row => _customer(row)?.Number("CreditLimit") == null, "credit is null");
                        case 2:
                            return new Case(Match("Customer.CustomerName.Value", MatchComparison.NotEqual, null), row => _customer(row)?.Text("CustomerName") != null, "name is not null");
                        case 3:
                            {
                                var n = Pick(5000m, 100000m, 2000000m);
                                return new Case(Match("Customer.CreditLimit.Value", MatchComparison.GreaterThan, n),
                                    row => _customer(row)?.Number("CreditLimit") is { } v ? v > n : null, $"credit > {n}");
                            }
                        case 4:
                            {
                                var s = Pick("受注", "仮");
                                var c = _random.Next(2) == 0 ? MatchComparison.Equal : MatchComparison.NotEqual;
                                return new Case(Match("Stage.Value", c, s), row => row.Text("Stage") is { } v ? (v == s) == (c == MatchComparison.Equal) : null, $"stage {c} {s}");
                            }
                        case 5:
                            return new Case(Match("Stage.Value", MatchComparison.Equal, null), row => row.Text("Stage") == null, "stage is null");
                        default:
                            {
                                //同じ一覧の条件が and に並ぶと本体が 1 つの exists にまとめるので、1 つの条件に 1 つだけ
                                if (_usedList) continue;
                                _usedList = true;
                                var s = Pick("受注", "出荷済");
                                return new Case(Match("Customer.Orders.Stage.Value", MatchComparison.Equal, s), row => _hasOrder(row, s), $"customer.orders.stage = {s}");
                            }
                    }
                }
            }

            T Pick<T>(params T[] values) => values[_random.Next(values.Length)];
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task 乱数の条件でもSQLの3値の論理と同じ結果(bool split)
        {
            await using var h = await MockHarness.StartAsync(editDesign: d =>
            {
                //リンク先の一覧 (受注 → 顧客 → 顧客の受注)
                d.Modules.Find("Order")!.LinkFieldNames.Add("Customer.Orders");
            });
            if (split) AddMany(h, 160);
            var orders = h.Records(MockHarness.OrderApp);
            var customers = h.Records(MockHarness.CustomerApp).ToDictionary(e => e.Text("CustomerCode")!);
            JsonObject? CustomerOf(JsonObject order) => order.Text("CustomerCode") is { } code && customers.TryGetValue(code, out var c) ? c : null;
            bool HasOrder(JsonObject order, string stage)
                => CustomerOf(order) is { } c && orders.Any(o => o.Text("CustomerCode") == c.Text("CustomerCode") && o.Text("Stage") == stage);
            var generator = new Generator(split ? 7 : 3, CustomerOf, HasOrder);
            var failures = new List<string>();

            for (var i = 0; i < 50; i++)
            {
                var c = generator.Next();
                var expected = RecordIdsDesc(orders.Where(e => c.Evaluate(e) == true));
                try
                {
                    var ids = await IdsAsync(h, "Order", c.Condition);
                    if (!ids.SequenceEqual(expected)) failures.Add($"{c.Text}: expected {expected.Count} rows [{string.Join(",", expected.Take(20))}] but {ids.Count} [{string.Join(",", ids.Take(20))}]");
                    var page = await h.IO.GetListAsync(new SearchCondition("Order") { Condition = c.Condition, LimitCount = 7 }, 0);
                    if (page.TotalCount != expected.Count || !page.Items.Select(Id).SequenceEqual(expected.Take(7)))
                        failures.Add($"{c.Text}: page count {page.TotalCount} (expected {expected.Count})");
                }
                catch (Exception e)
                {
                    failures.Add($"{c.Text}: {e.GetType().Name} {e.Message}");
                }
            }

            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }
    }
}
