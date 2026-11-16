using Codeer.LowCode.Blazor.Aggregation;
using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Text.Json.Nodes;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// 集計 (本体の AggregateAsync)。kintone に集計の API は無いので、条件に合う行を読んでメモリで集計する。
    /// 期待値はモックの中身から SQL の意味 (空の値は件数以外で数えない・値が無ければ null・空の値は 1 つの軸) で計算する。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockAggregateTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        IReadOnlyList<JsonObject> Orders => _h.Records(MockHarness.OrderApp);
        Dictionary<string, JsonObject> CustomersByCode => _h.Records(MockHarness.CustomerApp).ToDictionary(e => e.Text("CustomerCode")!);

        JsonObject? CustomerOf(JsonObject order) => order.Text("CustomerCode") is { } code && CustomersByCode.TryGetValue(code, out var c) ? c : null;

        Task<AggregateResult> AggregateAsync(AggregateCondition condition) => _h.IO.AggregateAsync(condition);

        static decimal? Sum(IEnumerable<decimal?> values) => values.Any(e => e != null) ? values.Where(e => e != null).Sum() : null;

        [Test]
        public async Task 軸ごとの件数と合計と重ならない件数を集計後の条件と並びと上限で()
        {
            var condition = new AggregateCondition("Order") { LimitCount = 2 };
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.CountDistinct, Variable = "Stage.Value" });
            condition.Condition = Match("Total.Value", MatchComparison.GreaterThan, 1000m);
            condition.Having.Add(new AggregateHaving { MeasureIndex = 1, Comparison = MatchComparison.GreaterThan, Value = 20000 });
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Measure, Index = 1, IsDescending = true });

            var result = await AggregateAsync(condition);

            var rows = Orders.Where(e => e.Number("Total") > 1000m).ToList();
            var groups = rows.GroupBy(e => e.Text("CustomerCode"))
                .Select(g => (g.Key, Count: g.Count(), Sum: Sum(g.Select(e => e.Number("Total"))), Stages: g.Select(e => e.Text("Stage")).Where(e => e != null).Distinct().Count()))
                .Where(e => e.Sum > 20000m).OrderByDescending(e => e.Sum).ToList();
            Assert.That(groups, Has.Count.GreaterThan(2));
            Assert.That(result.Rows.Select(e => (string?)e.Key(0)), Is.EqualTo(groups.Take(2).Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Number(0)), Is.EqualTo(groups.Take(2).Select(e => (decimal?)e.Count)));
            Assert.That(result.Rows.Select(e => e.Number(1)), Is.EqualTo(groups.Take(2).Select(e => e.Sum)));
            Assert.That(result.Rows.Select(e => e.Number(2)), Is.EqualTo(groups.Take(2).Select(e => (decimal?)e.Stages)));
            //軸の表示名はリンク先 (顧客) の顧客名
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.EqualTo(groups.Take(2).Select(e => e.Key == null ? null : CustomersByCode[e.Key].Text("CustomerName"))));
            Assert.That(result.TotalCount, Is.EqualTo(rows.Count));
            Assert.That(result.GroupCount, Is.EqualTo(groups.Count));
            Assert.That(result.IsLimited, Is.True);
        }

        [Test]
        public async Task 軸なしの件数と合計と平均と最小と最大()
        {
            var condition = new AggregateCondition("Order");
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Avg, Variable = "Total.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Min, Variable = "OrderDate.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Max, Variable = "OrderNo.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Max, Variable = "DeliveryAt.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.CountDistinct, Variable = "Customer.Value" });

            var result = await AggregateAsync(condition);

            var totals = Orders.Select(e => e.Number("Total")).Where(e => e != null).Select(e => e!.Value).ToList();
            var row = result.Rows.Single();
            Assert.That(row.Number(0), Is.EqualTo(Orders.Count));
            Assert.That(row.Number(1), Is.EqualTo(totals.Sum()));
            Assert.That(row.Number(2), Is.EqualTo(totals.Sum() / totals.Count).Within(0.0001m));
            Assert.That(row.Value(3), Is.EqualTo(Orders.Select(e => e.Date("OrderDate")).Where(e => e != null).Min()));
            Assert.That(row.Value(4), Is.EqualTo(Orders.Select(e => e.Text("OrderNo")).Max(StringComparer.Ordinal)));
            Assert.That(row.Value(5), Is.EqualTo(Orders.Select(e => e.DateTimeUtc("DeliveryAt")).Where(e => e != null).Max()));
            Assert.That(row.Number(6), Is.EqualTo(Orders.Select(e => e.Text("CustomerCode")).Where(e => e != null).Distinct().Count()));
            Assert.That(result.TotalCount, Is.EqualTo(Orders.Count));
        }

        [Test]
        public async Task 空の値は1つの軸にまとまる()
        {
            var condition = new AggregateCondition("Order");
            condition.Groups.Add(new ValueGroup { Variable = "Stage.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });

            var result = await AggregateAsync(condition);

            var expected = Orders.GroupBy(e => e.Text("Stage") ?? "(空)").ToDictionary(g => g.Key, g => (Count: (decimal?)g.Count(), Sum: Sum(g.Select(e => e.Number("Total")))));
            var actual = result.Rows.ToDictionary(e => (string?)e.Key(0) ?? "(空)", e => (Count: e.Number(0), Sum: e.Number(1)));
            Assert.That(actual, Is.EquivalentTo(expected));
            Assert.That(actual.ContainsKey("(空)"), Is.True);
        }

        [Test]
        public async Task 条件に合う行が無くても軸なしは1行()
        {
            var condition = new AggregateCondition("Order") { Condition = Match("Total.Value", MatchComparison.GreaterThan, 99999999m) };
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });

            var result = await AggregateAsync(condition);

            var row = result.Rows.Single();
            Assert.That(row.Number(0), Is.EqualTo(0));
            Assert.That(row.Value(1), Is.Null);
            Assert.That(result.TotalCount, Is.EqualTo(0));
        }

        [Test]
        public async Task リンク先の列を軸にする()
        {
            var condition = new AggregateCondition("Order");
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Status.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Max, Variable = "Total.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Customer.CreditLimit.Value" });

            var result = await AggregateAsync(condition);

            //SQL の left join と同じく、受注の行ごとに顧客の値を数える (同じ顧客の受注が 2 件なら 2 回)
            var expected = Orders.GroupBy(e => CustomerOf(e)?.Text("Status") ?? "(空)").ToDictionary(g => g.Key, g => (
                Count: (decimal?)g.Count(),
                Max: g.Select(e => e.Number("Total")).Max(),
                Sum: Sum(g.Select(e => CustomerOf(e)?.Number("CreditLimit")))));
            var actual = result.Rows.ToDictionary(e => (string?)e.Key(0) ?? "(空)", e => (Count: e.Number(0), Max: e.Number(1), Sum: e.Number(2)));
            Assert.That(actual, Is.EquivalentTo(expected));
        }

        [Test]
        public async Task リンク先の列の条件で絞って集計する()
        {
            var condition = new AggregateCondition("Order") { Condition = Match("Customer.CustomerName.Value", MatchComparison.Like, "みなと") };
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });

            var result = await AggregateAsync(condition);

            var expected = Orders.Where(e => CustomerOf(e)?.Text("CustomerName")?.Contains("みなと") == true).GroupBy(e => e.Text("CustomerCode")!)
                .ToDictionary(g => g.Key, g => Sum(g.Select(e => e.Number("Total"))));
            Assert.That(expected, Is.Not.Empty);
            Assert.That(result.Rows.ToDictionary(e => (string)e.Key(0)!, e => e.Number(0)), Is.EquivalentTo(expected));
            Assert.That(MockHarness.Query(_h.ListReads(MockHarness.OrderApp).Last()), Does.StartWith("CustomerCode in (\"C-007\")"));
        }

        [Test]
        public async Task 集計後の条件で全部外れると0行()
        {
            var condition = new AggregateCondition("Order");
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });
            condition.Having.Add(new AggregateHaving { MeasureIndex = 0, Comparison = MatchComparison.GreaterThan, Value = 99999999 });

            var result = await AggregateAsync(condition);

            Assert.That(result.Rows, Is.Empty);
            Assert.That(result.TotalCount, Is.EqualTo(Orders.Count));
        }

        [Test]
        public async Task 軸の昇順で並べると空の軸は後()
        {
            var condition = new AggregateCondition("Order");
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Group, Index = 0 });

            var result = await AggregateAsync(condition);

            //本体の集計の並び: 空の軸を最後 → リンクの表示名 (顧客名) → 値。空の扱いは PostgreSQL と同じ (昇順で後)
            var expected = Orders.Select(e => e.Text("CustomerCode")).Distinct().OrderBy(e => e == null ? 1 : 0)
                .ThenBy(e => e == null ? null : CustomersByCode[e].Text("CustomerName"), StringComparer.Ordinal).ThenBy(e => e, StringComparer.Ordinal).ToList();
            Assert.That(result.Rows.Select(e => (string?)e.Key(0)), Is.EqualTo(expected));
        }

        //合計の上位 3 件の顧客
        static AggregateCondition TopCustomers(string variable) => new("Order")
        {
            Groups = { new ValueGroup { Variable = variable } },
            Measures = { new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" } },
            SortConditions = { new AggregateSort { Target = AggregateSortTarget.Measure, Index = 0, IsDescending = true } },
            LimitCount = 3,
        };

        //合計の降順で上位 3 件 (PostgreSQL と同じく空の合計は降順で先・同じ合計は軸の昇順)
        static HashSet<string> Top(IEnumerable<(string Key, decimal? Sum)> groups)
            => groups.OrderBy(e => e.Sum == null ? 0 : 1).ThenByDescending(e => e.Sum).ThenBy(e => e.Key, StringComparer.Ordinal).Take(3).Select(e => e.Key).ToHashSet();

        [Test]
        public async Task 別の集計の結果で行を選んで集計する()
        {
            var condition = new AggregateCondition("Order") { WithinGroupsOf = TopCustomers("Customer.Value") };
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Groups.Add(new ValueGroup { Variable = "Stage.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });

            var result = await AggregateAsync(condition);

            var top = Top(Orders.GroupBy(e => e.Text("CustomerCode") ?? "").Select(g => (g.Key, Sum: Sum(g.Select(e => e.Number("Total"))))));
            var expected = Orders.Where(e => top.Contains(e.Text("CustomerCode") ?? "")).GroupBy(e => (e.Text("CustomerCode") ?? "", e.Text("Stage") ?? ""))
                .ToDictionary(g => g.Key, g => (Count: (decimal?)g.Count(), Sum: Sum(g.Select(e => e.Number("Total")))));
            var actual = result.Rows.ToDictionary(e => ((string?)e.Key(0) ?? "", (string?)e.Key(1) ?? ""), e => (Count: e.Number(0), Sum: e.Number(1)));
            Assert.That(actual, Is.EquivalentTo(expected));
            Assert.That(result.TotalCount, Is.EqualTo(Orders.Count(e => top.Contains(e.Text("CustomerCode") ?? ""))));
        }

        [Test]
        public async Task 別の集計の結果で選んだ行の総計を集計後の条件つきで()
        {
            var condition = new AggregateCondition("Order") { WithinGroupsOf = TopCustomers("Customer.CustomerName.Value") };
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });
            condition.Having.Add(new AggregateHaving { MeasureIndex = 0, Comparison = MatchComparison.GreaterThan, Value = 0 });

            var result = await AggregateAsync(condition);

            var top = Top(Orders.GroupBy(e => CustomerOf(e)?.Text("CustomerName") ?? "").Select(g => (g.Key, Sum: Sum(g.Select(e => e.Number("Total"))))));
            var selected = Orders.Where(e => top.Contains(CustomerOf(e)?.Text("CustomerName") ?? "")).ToList();
            Assert.That(result.Rows.Single().Number(0), Is.EqualTo(Sum(selected.Select(e => e.Number("Total")))));
            Assert.That(result.TotalCount, Is.EqualTo(selected.Count));
        }

        [Test]
        public async Task 別の集計が1行も返さなければ何も選ばない()
        {
            var within = TopCustomers("Customer.Value");
            within.Having.Add(new AggregateHaving { MeasureIndex = 0, Comparison = MatchComparison.GreaterThan, Value = 99999999 });
            var condition = new AggregateCondition("Order") { WithinGroupsOf = within };
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });

            var result = await AggregateAsync(condition);

            Assert.That(result.Rows, Is.Empty);
        }

        [TestCase(DateBucket.Month)]
        [TestCase(DateBucket.Year)]
        [TestCase(DateBucket.Day)]
        public void 日付の期間での集計は要求を送る前に拒否する(DateBucket bucket)
        {
            var condition = new AggregateCondition("Order");
            condition.Groups.Add(new DateGroup { Variable = "OrderDate.Value", Bucket = bucket });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });

            var ex = Assert.ThrowsAsync<KintoneException>(() => AggregateAsync(condition));

            Assert.That(ex!.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
            Assert.That(ex.Message, Does.Contain("DATE_FORMAT"));
            Assert.That(_h.DataRequests, Is.Empty);
        }

        [Test]
        public async Task 集計で読む行がMaxRowsを超えたらエラー()
        {
            await using var h = await MockHarness.StartAsync(edit: s => s.MaxRows = 10);
            var condition = new AggregateCondition("Order");
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });

            var ex = Assert.ThrowsAsync<KintoneException>(() => h.IO.AggregateAsync(condition));

            Assert.That(ex!.Message, Does.Contain("MaxRows"));
        }

        [Test]
        public async Task クロス集計で上位の行だけを選ぶと選んだ行の分だけ集計する()
        {
            //顧客 × 進捗 で合計。合計の多い顧客 3 社だけ
            var condition = new AggregateCondition("Order") { LimitCount = 3 };
            condition.Groups.Add(new ValueGroup { Variable = "Customer.Value" });
            condition.Groups.Add(new ValueGroup { Variable = "Stage.Value" });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Total.Value" });
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Measure, Index = 0, IsDescending = true });

            var table = await CrossTabBuilder.BuildAsync(condition, 1, async conditions =>
            {
                Assert.That(conditions.Count(e => e.WithinGroupsOf != null), Is.GreaterThan(0));
                var results = new List<AggregateResult>();
                foreach (var e in conditions) results.Add(await AggregateAsync(e));
                return results;
            });

            var lines = Orders.Select(e => (Customer: e.Text("CustomerCode") ?? "", Stage: e.Text("Stage") ?? "", Total: e.Number("Total"))).ToList();
            var top = Top(lines.GroupBy(e => e.Customer).Select(g => (g.Key, Sum: Sum(g.Select(e => e.Total)))));
            var expected = lines.Where(e => top.Contains(e.Customer)).GroupBy(e => (e.Customer, e.Stage)).ToDictionary(g => g.Key, g => Sum(g.Select(e => e.Total)));

            Assert.That(table, Is.Not.Null);
            Assert.That(table!.Rows.Select(e => KeyText(e.Keys[0].Value.GetValue())), Is.EquivalentTo(top));
            var actual = new Dictionary<(string, string), decimal?>();
            for (var r = 0; r < table.Rows.Count; r++)
            {
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    var value = table.Cell(0, r, c).GetValue();
                    if (value == null) continue;
                    actual[(KeyText(table.Rows[r].Keys[0].Value.GetValue()), KeyText(table.Columns[c].Keys[0].Value.GetValue()))] = Convert.ToDecimal(value);
                }
            }
            Assert.That(actual, Is.EquivalentTo(expected));
        }
    }
}
