using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using Kintone.Mock;
using System.Globalization;
using System.Text.Json.Nodes;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// 受注のテーブル Lines (ListField の DbColumn + 行モジュールの JsonField)。読む・行を足す・直す・消す・全部消す・親と一緒に作る、
    /// と、テーブルに足した日付・時刻・日時・ドロップダウンの項目 (JsonField の Date / Time / DateTime のノード) の往復。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockSubtableTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        async Task<ModuleData> ReadOrderAsync(long id)
            => (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, id.ToString(CultureInfo.InvariantCulture)) }, 0)).Items.Single();

        //行が無いテーブルは列が null で、本体は null の列のフィールドのデータを作らない (行なし = データなし)
        static ListFieldData Lines(ModuleData order) => Field<ListFieldData>(order, "Lines") ?? new ListFieldData();

        static string? RowId(ModuleData line) => JsonNode.Parse(Field<JsonFieldData>(line, "Json")!.Value!)!["$id"]?.GetValue<string>();

        async Task<ModuleSubmitResult> SaveLinesAsync(long id, ListFieldData lines)
        {
            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = id.ToString(CultureInfo.InvariantCulture) };
            update.Fields["Lines"] = lines;
            _h.Mock.ClearRequests();
            return (await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = id.ToString(CultureInfo.InvariantCulture), Update = new() { update } } })).Single();
        }

        static ModuleData NewLine(string product, decimal? quantity, decimal? price)
        {
            var line = new ModuleData { Name = "OrderLine" };
            line.Fields["Product"] = new TextFieldData { Value = product };
            line.Fields["Quantity"] = new NumberFieldData { Value = quantity };
            line.Fields["UnitPrice"] = new NumberFieldData { Value = price };
            return line;
        }

        static void AssertSucceeded(ModuleSubmitResult result) => Assert.That(result.ExceptionMessage, Is.Empty, result.ExceptionMessage);

        JsonObject Order(long id) => _h.Record(MockHarness.OrderApp, id)!;

        JsonArray SentRows() => MockHarness.Body(_h.WriteRequests.Single())["record"]!["Lines"]!["value"]!.AsArray();

        // ---------------------------------------------------------------- 読む

        [Test]
        public async Task テーブルの行を読む()
        {
            var order = await ReadOrderAsync(7);
            var empty = await ReadOrderAsync(4);

            var expected = Order(7).Rows("Lines");
            var lines = Lines(order).Children;
            Assert.That(lines, Has.Count.EqualTo(3));
            for (var i = 0; i < lines.Count; i++)
            {
                var row = expected[i].Values;
                Assert.That(RowId(lines[i]), Is.EqualTo(expected[i].Id.ToString(CultureInfo.InvariantCulture)));
                Assert.That(Field<TextFieldData>(lines[i], "Product")!.Value, Is.EqualTo(row["Product"]!["value"]!.GetValue<string>()));
                Assert.That(Field<NumberFieldData>(lines[i], "Quantity")!.Value, Is.EqualTo(decimal.Parse(row["Quantity"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture)));
                Assert.That(Field<NumberFieldData>(lines[i], "UnitPrice")!.Value, Is.EqualTo(decimal.Parse(row["UnitPrice"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture)));
                Assert.That(Field<NumberFieldData>(lines[i], "Subtotal")!.Value, Is.EqualTo(decimal.Parse(row["Subtotal"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture)));
            }
            Assert.That(Lines(empty).Children, Is.Empty);
            //テーブルの中身は親の読み出しに入って来る (行ごとの要求は無い)
            Assert.That(_h.ListReads(MockHarness.OrderApp), Has.Count.EqualTo(2));
        }

        // ---------------------------------------------------------------- 書く

        [Test]
        public async Task 行を直すと行IDはそのままで計算も変わる()
        {
            var lines = Lines(await ReadOrderAsync(7));
            var ids = Order(7).Rows("Lines").Select(e => e.Id).ToList();
            Field<NumberFieldData>(lines.Children[1], "Quantity")!.Value = 10;

            AssertSucceeded(await SaveLinesAsync(7, lines));

            var rows = Order(7).Rows("Lines");
            Assert.That(rows.Select(e => e.Id), Is.EqualTo(ids));
            Assert.That(rows[1].Values["Quantity"]!["value"]!.GetValue<string>(), Is.EqualTo("10"));
            Assert.That(rows[1].Values["Subtotal"]!["value"]!.GetValue<string>(), Is.EqualTo("4800"));
            Assert.That(Order(7).Number("Total"), Is.EqualTo(1620m + 4800m + 4000m));
            //残す行は id つき。計算 (Subtotal) は送らない
            var sent = SentRows();
            Assert.That(sent.Select(e => e!["id"]?.GetValue<string>()), Is.EqualTo(ids.Select(e => e.ToString(CultureInfo.InvariantCulture))));
            Assert.That(sent.Select(e => e!["value"]!.AsObject().ContainsKey("Subtotal")), Is.All.False);
            //読み直しても同じ
            var reread = Lines(await ReadOrderAsync(7)).Children;
            Assert.That(Field<NumberFieldData>(reread[1], "Subtotal")!.Value, Is.EqualTo(4800m));
        }

        [Test]
        public async Task 行を足す()
        {
            var lines = Lines(await ReadOrderAsync(1));
            var ids = Order(1).Rows("Lines").Select(e => e.Id).ToList();
            lines.Children.Add(NewLine("ホッチキス", 2, 650));

            AssertSucceeded(await SaveLinesAsync(1, lines));

            var rows = Order(1).Rows("Lines");
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Id, Is.EqualTo(ids[0]));
            Assert.That(rows[1].Id, Is.Not.EqualTo(ids[0]));
            Assert.That(rows[1].Values["Product"]!["value"]!.GetValue<string>(), Is.EqualTo("ホッチキス"));
            Assert.That(rows[1].Values["Subtotal"]!["value"]!.GetValue<string>(), Is.EqualTo("1300"));
            Assert.That(Order(1).Number("Total"), Is.EqualTo(1600m + 1300m));
            Assert.That(SentRows()[1]!.AsObject().ContainsKey("id"), Is.False);
            var reread = Lines(await ReadOrderAsync(1)).Children;
            Assert.That(RowId(reread[1]), Is.EqualTo(rows[1].Id.ToString(CultureInfo.InvariantCulture)));
        }

        [Test]
        public async Task 空の行モジュールに空の項目があっても足せる()
        {
            var lines = Lines(await ReadOrderAsync(4));
            lines.Children.Add(NewLine("数量なし", null, null));

            AssertSucceeded(await SaveLinesAsync(4, lines));

            var rows = Order(4).Rows("Lines");
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Values["Quantity"]!["value"]!.GetValue<string>(), Is.Empty);
            var reread = Lines(await ReadOrderAsync(4)).Children.Single();
            Assert.That(Field<NumberFieldData>(reread, "Quantity")?.Value, Is.Null);
            Assert.That(Field<TextFieldData>(reread, "Product")!.Value, Is.EqualTo("数量なし"));
        }

        [Test]
        public async Task 行を消す()
        {
            var lines = Lines(await ReadOrderAsync(7));
            var ids = Order(7).Rows("Lines").Select(e => e.Id).ToList();
            lines.Children.RemoveAt(1);

            AssertSucceeded(await SaveLinesAsync(7, lines));

            Assert.That(Order(7).Rows("Lines").Select(e => e.Id), Is.EqualTo(new[] { ids[0], ids[2] }));
            Assert.That(Order(7).Number("Total"), Is.EqualTo(1620m + 4000m));
            Assert.That(Lines(await ReadOrderAsync(7)).Children, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task 行を全部消す()
        {
            var lines = Lines(await ReadOrderAsync(7));
            lines.Children.Clear();

            AssertSucceeded(await SaveLinesAsync(7, lines));

            Assert.That(Order(7).Rows("Lines"), Is.Empty);
            Assert.That(Order(7).Number("Total"), Is.EqualTo(0m));
            Assert.That(Lines(await ReadOrderAsync(7)).Children, Is.Empty);
        }

        [Test]
        public async Task 親と一緒に行を作る()
        {
            var add = new ModuleData { Name = "Order" };
            add.Fields["Id"] = IdFieldData.NewId();
            add.Fields["OrderNo"] = new TextFieldData { Value = "SO-LINES" };
            var lines = new ListFieldData();
            lines.Children.Add(NewLine("付箋 75mm", 3, 220));
            lines.Children.Add(NewLine("封筒 長形 3 号 100 枚", 2, 540));
            add.Fields["Lines"] = lines;

            var result = (await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = ((IdFieldData)add.Fields["Id"]).Value!, Add = new() { add } } })).Single();

            AssertSucceeded(result);
            var id = long.Parse(result.DestinationId, CultureInfo.InvariantCulture);
            var rows = Order(id).Rows("Lines");
            Assert.That(rows.Select(e => e.Values["Product"]!["value"]!.GetValue<string>()), Is.EqualTo(new[] { "付箋 75mm", "封筒 長形 3 号 100 枚" }));
            Assert.That(Order(id).Number("Total"), Is.EqualTo(660m + 1080m));
            var reread = Lines(await ReadOrderAsync(id)).Children;
            Assert.That(reread.Select(e => Field<NumberFieldData>(e, "Subtotal")!.Value), Is.EqualTo(new decimal?[] { 660m, 1080m }));
            Assert.That(reread.Select(RowId), Is.EqualTo(rows.Select(e => e.Id.ToString(CultureInfo.InvariantCulture))));
        }

        // ---------------------------------------------------------------- 日付・時刻・日時・ドロップダウンの項目

        //受注のフォームのテーブルに 出荷日 (DATE)・出荷時刻 (TIME)・到着日時 (DATETIME)・梱包 (DROP_DOWN) を足す
        const string OrderFieldsWithDates = """
            {
              "OrderNo": {"type": "SINGLE_LINE_TEXT", "label": "受注番号", "required": true, "unique": true},
              "CustomerCode": {"type": "SINGLE_LINE_TEXT", "label": "顧客コード", "lookup": {"app": 1, "relatedKeyField": "CustomerCode", "fieldMappings": {"CustomerNameCopy": "CustomerName"}}},
              "CustomerNameCopy": {"type": "SINGLE_LINE_TEXT", "label": "顧客名"},
              "OrderDate": {"type": "DATE", "label": "受注日"},
              "Stage": {"type": "DROP_DOWN", "label": "進捗", "options": ["仮", "受注", "出荷済"]},
              "DeliveryAt": {"type": "DATETIME", "label": "納品日時"},
              "StartTime": {"type": "TIME", "label": "作業開始時刻"},
              "Assignee": {"type": "USER_SELECT", "label": "担当者"},
              "Memo": {"type": "RICH_TEXT", "label": "メモ"},
              "Attachments": {"type": "FILE", "label": "添付ファイル"},
              "Lines": {"type": "SUBTABLE", "label": "明細", "fields": {
                "Product": {"type": "SINGLE_LINE_TEXT", "label": "商品"},
                "Quantity": {"type": "NUMBER", "label": "数量"},
                "UnitPrice": {"type": "NUMBER", "label": "単価"},
                "Subtotal": {"type": "CALC", "label": "小計", "expression": "Quantity * UnitPrice"},
                "ShipDate": {"type": "DATE", "label": "出荷日"},
                "ShipTime": {"type": "TIME", "label": "出荷時刻"},
                "ArriveAt": {"type": "DATETIME", "label": "到着日時"},
                "Packing": {"type": "DROP_DOWN", "label": "梱包", "options": ["箱", "袋"]}
              }},
              "Total": {"type": "CALC", "label": "合計", "expression": "SUM(Subtotal)"}
            }
            """;

        static Task<MockHarness> StartWithDatesAsync()
        {
            var mock = MockKintone.FromFile(MockHarness.DataPath);
            mock.SetFields(MockHarness.OrderApp, OrderFieldsWithDates);
            return MockHarness.StartAsync(mock, editDesign: d =>
            {
                var line = d.Modules.Find("OrderLine")!;
                var json = (JsonFieldDesign)line.Fields.Single(e => e.Name == "Json");
                json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "ShipDate", NodeType = JsonNodeType.Date });
                json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "ShipTime", NodeType = JsonNodeType.Time });
                json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "ArriveAt", NodeType = JsonNodeType.DateTime });
                json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Packing", NodeType = JsonNodeType.Text });
                line.Fields.Add(new DateFieldDesign { Name = "ShipDate", DbColumn = "ShipDate" });
                line.Fields.Add(new TimeFieldDesign { Name = "ShipTime", DbColumn = "ShipTime" });
                line.Fields.Add(new DateTimeFieldDesign { Name = "ArriveAt", DbColumn = "ArriveAt", SaveAsUtc = true });
                line.Fields.Add(new TextFieldDesign { Name = "Packing", DbColumn = "Packing" });
            });
        }

        [Test]
        public async Task テーブルの日付時刻日時ドロップダウンは書いて読んでも同じ値()
        {
            await using var h = await StartWithDatesAsync();
            var order = (await h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, "1") }, 0)).Items.Single();
            var lines = Lines(order);
            //既存の行の足した項目は未入力
            Assert.That(Field<DateFieldData>(lines.Children[0], "ShipDate")?.Value, Is.Null);
            Assert.That(Field<TimeFieldData>(lines.Children[0], "ShipTime")?.Value, Is.Null);
            Assert.That(Field<DateTimeFieldData>(lines.Children[0], "ArriveAt")?.Value, Is.Null);
            Assert.That(Field<TextFieldData>(lines.Children[0], "Packing")?.Value, Is.Null);

            Field<DateFieldData>(lines.Children[0], "ShipDate")!.Value = new DateOnly(2026, 12, 24);
            Field<TimeFieldData>(lines.Children[0], "ShipTime")!.Value = new TimeOnly(13, 45);
            Field<DateTimeFieldData>(lines.Children[0], "ArriveAt")!.Value = new DateTime(2026, 12, 25, 1, 30, 0, DateTimeKind.Utc);
            Field<TextFieldData>(lines.Children[0], "Packing")!.Value = "箱";
            var added = NewLine("付箋 75mm", 1, 220);
            added.Fields["ShipDate"] = new DateFieldData { Value = new DateOnly(2027, 1, 5) };
            added.Fields["ShipTime"] = new TimeFieldData { Value = new TimeOnly(9, 0) };
            added.Fields["ArriveAt"] = new DateTimeFieldData { Value = new DateTime(2027, 1, 6, 23, 59, 0, DateTimeKind.Utc) };
            added.Fields["Packing"] = new TextFieldData { Value = "袋" };
            lines.Children.Add(added);

            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["Lines"] = lines;
            h.Mock.ClearRequests();
            var result = (await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "1", Update = new() { update } } })).Single();

            AssertSucceeded(result);
            //kintone に入った値 (kintone の書き方)
            var rows = h.Record(MockHarness.OrderApp, 1)!.Rows("Lines");
            Assert.That(rows, Has.Count.EqualTo(2));
            string? V(int i, string code) => rows[i].Values[code]!["value"]?.GetValue<string>();
            Assert.That((V(0, "ShipDate"), V(0, "ShipTime"), V(0, "ArriveAt"), V(0, "Packing")), Is.EqualTo(("2026-12-24", "13:45", "2026-12-25T01:30:00Z", "箱")));
            Assert.That((V(1, "ShipDate"), V(1, "ShipTime"), V(1, "ArriveAt"), V(1, "Packing")), Is.EqualTo(("2027-01-05", "09:00", "2027-01-06T23:59:00Z", "袋")));
            //読み直しても同じ
            var reread = Lines((await h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, "1") }, 0)).Items.Single()).Children;
            Assert.That(Field<DateFieldData>(reread[0], "ShipDate")!.Value, Is.EqualTo(new DateOnly(2026, 12, 24)));
            Assert.That(Field<TimeFieldData>(reread[0], "ShipTime")!.Value, Is.EqualTo(new TimeOnly(13, 45)));
            Assert.That(Field<DateTimeFieldData>(reread[0], "ArriveAt")!.Value, Is.EqualTo(new DateTime(2026, 12, 25, 1, 30, 0, DateTimeKind.Utc)));
            Assert.That(Field<TextFieldData>(reread[0], "Packing")!.Value, Is.EqualTo("箱"));
            Assert.That(Field<DateFieldData>(reread[1], "ShipDate")!.Value, Is.EqualTo(new DateOnly(2027, 1, 5)));
            Assert.That(Field<TimeFieldData>(reread[1], "ShipTime")!.Value, Is.EqualTo(new TimeOnly(9, 0)));
            Assert.That(Field<DateTimeFieldData>(reread[1], "ArriveAt")!.Value, Is.EqualTo(new DateTime(2027, 1, 6, 23, 59, 0, DateTimeKind.Utc)));
            Assert.That(Field<TextFieldData>(reread[1], "Packing")!.Value, Is.EqualTo("袋"));

            //もう一度そのまま保存しても値は変わらない (読んだ形をそのまま書ける)
            AssertSucceeded((await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "1", Update = new() { new ModuleData { Name = "Order", Fields = { ["Id"] = new IdFieldData { Value = "1" }, ["Lines"] = Lines((await h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, "1") }, 0)).Items.Single()) } } } } })).Single());
            var again = h.Record(MockHarness.OrderApp, 1)!.Rows("Lines");
            Assert.That(again.Select(e => e.Values["ArriveAt"]!["value"]!.GetValue<string>()), Is.EqualTo(new[] { "2026-12-25T01:30:00Z", "2027-01-06T23:59:00Z" }));
            Assert.That(again.Select(e => e.Values["ShipTime"]!["value"]!.GetValue<string>()), Is.EqualTo(new[] { "13:45", "09:00" }));
        }

        [Test]
        public async Task テーブルの日付時刻日時を空にする()
        {
            await using var h = await StartWithDatesAsync();
            ModuleData Read() => h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, "1") }, 0).Result.Items.Single();
            async Task SaveAsync(ListFieldData lines)
            {
                var update = new ModuleData { Name = "Order" };
                update.Fields["Id"] = new IdFieldData { Value = "1" };
                update.Fields["Lines"] = lines;
                AssertSucceeded((await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "1", Update = new() { update } } })).Single());
            }
            var lines = Lines(Read());
            Field<DateFieldData>(lines.Children[0], "ShipDate")!.Value = new DateOnly(2026, 12, 24);
            Field<TimeFieldData>(lines.Children[0], "ShipTime")!.Value = new TimeOnly(13, 45);
            Field<DateTimeFieldData>(lines.Children[0], "ArriveAt")!.Value = new DateTime(2026, 12, 25, 1, 30, 0, DateTimeKind.Utc);
            await SaveAsync(lines);

            lines = Lines(Read());
            Field<DateFieldData>(lines.Children[0], "ShipDate")!.Value = null;
            Field<TimeFieldData>(lines.Children[0], "ShipTime")!.Value = null;
            Field<DateTimeFieldData>(lines.Children[0], "ArriveAt")!.Value = null;
            await SaveAsync(lines);

            var row = h.Record(MockHarness.OrderApp, 1)!.Rows("Lines")[0].Values;
            Assert.That(row["ShipDate"]!["value"]?.GetValue<string>(), Is.Null.Or.Empty);
            Assert.That(row["ShipTime"]!["value"]?.GetValue<string>(), Is.Null.Or.Empty);
            Assert.That(row["ArriveAt"]!["value"]?.GetValue<string>(), Is.Null.Or.Empty);
            var reread = Lines(Read()).Children[0];
            Assert.That(Field<DateFieldData>(reread, "ShipDate")?.Value, Is.Null);
            Assert.That(Field<TimeFieldData>(reread, "ShipTime")?.Value, Is.Null);
            Assert.That(Field<DateTimeFieldData>(reread, "ArriveAt")?.Value, Is.Null);
        }
    }
}
