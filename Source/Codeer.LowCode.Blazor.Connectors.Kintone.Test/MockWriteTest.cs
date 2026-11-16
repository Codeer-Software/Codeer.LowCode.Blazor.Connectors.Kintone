using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using Kintone.Mock;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// モック + 本物のコネクター + 本体の ModuleDataIO で、作成・更新・削除・楽観ロック・kintone の検証のエラー・障害を端から端まで確かめる。
    /// 結果はモックの中身 (GetRecord / GetRecords) と送った要求 (Requests) で確かめる。
    /// </summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class MockWriteTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        Task<List<ModuleSubmitResult>> SubmitAsync(ModuleSubmitData data) => _h.IO.SubmitWithTransactionAsync(new() { data });

        async Task<ModuleSubmitResult> AddAsync(string module, Action<ModuleData> fill)
        {
            var tempId = IdFieldData.NewId();
            var data = new ModuleData { Name = module };
            data.Fields["Id"] = tempId;
            fill(data);
            var id = ((IdFieldData)data.Fields["Id"]).Value!;
            return (await SubmitAsync(new ModuleSubmitData { ModuleName = module, Id = id, Add = new() { data } })).Single();
        }

        async Task<ModuleSubmitResult> UpdateAsync(string module, string id, Action<ModuleData> fill)
        {
            var data = new ModuleData { Name = module };
            data.Fields["Id"] = new IdFieldData { Value = id };
            fill(data);
            return (await SubmitAsync(new ModuleSubmitData { ModuleName = module, Id = id, Update = new() { data } })).Single();
        }

        async Task<ModuleSubmitResult> DeleteAsync(string module, string id, OptimisticLockingFieldData? locking = null)
            => (await SubmitAsync(new ModuleSubmitData { ModuleName = module, Id = id, Delete = new() { new ModuleDeleteInfo { ModuleName = module, Id = id, OptimisticLockingFieldData = locking } } })).Single();

        async Task<ModuleData?> DetailAsync(string module, string id)
        {
            var condition = new SearchCondition(module) { Condition = Match("Id.Value", MatchComparison.Equal, id) };
            return (await _h.IO.GetListAsync(condition, 0)).Items.SingleOrDefault();
        }

        static void AssertSucceeded(ModuleSubmitResult result) => Assert.That(result.ExceptionMessage, Is.Empty, result.ExceptionMessage);

        static JsonObject RecordOf(MockRecordedRequest request) => MockHarness.Body(request)["record"]!.AsObject();

        static string? Value(MockRecordedRequest request, string code) => RecordOf(request)[code]?["value"]?.ToJsonString();

        static string? QueryValue(MockRecordedRequest request, string key) => request.Query.FirstOrDefault(e => e.Key == key).Value;

        JsonObject Order(long id) => _h.Record(MockHarness.OrderApp, id)!;

        JsonObject CustomerByCode(string code) => _h.Records(MockHarness.CustomerApp).Single(e => e.Text("CustomerCode") == code);

        // ---------------------------------------------------------------- 作成

        [Test]
        public async Task 作成は採番されたレコードIDがIdになり送った値だけが入る()
        {
            var before = _h.Records(MockHarness.OrderApp).Count;

            var result = await AddAsync("Order", e =>
            {
                e.Fields["OrderNo"] = new TextFieldData { Value = "SO-2026-0100" };
                e.Fields["Customer"] = new LinkFieldData { Value = "C-003" };
                e.Fields["OrderDate"] = new DateFieldData { Value = new DateOnly(2026, 11, 16) };
                e.Fields["Stage"] = new TextFieldData { Value = "仮" };
                e.Fields["StartTime"] = new TimeFieldData { Value = new TimeOnly(8, 45) };
                e.Fields["DeliveryAt"] = new DateTimeFieldData { Value = new DateTime(2026, 11, 20, 1, 30, 0, DateTimeKind.Utc) };
                e.Fields["Memo"] = new TextFieldData { Value = null };
            });

            AssertSucceeded(result);
            Assert.That(result.DestinationId, Is.EqualTo("21"));
            Assert.That(_h.Records(MockHarness.OrderApp), Has.Count.EqualTo(before + 1));
            var row = Order(21);
            Assert.That(row.Text("OrderNo"), Is.EqualTo("SO-2026-0100"));
            Assert.That(row.Text("CustomerCode"), Is.EqualTo("C-003"));
            Assert.That(row.Date("OrderDate"), Is.EqualTo(new DateOnly(2026, 11, 16)));
            Assert.That(row.Text("Stage"), Is.EqualTo("仮"));
            Assert.That(row.Time("StartTime"), Is.EqualTo(new TimeOnly(8, 45)));
            Assert.That(row.DateTimeUtc("DeliveryAt"), Is.EqualTo(new DateTime(2026, 11, 20, 1, 30, 0, DateTimeKind.Utc)));
            Assert.That(row.Text("Memo"), Is.Null);
            //作成は 1 回だけ POST。値が null の項目は送らない
            var post = _h.WriteRequests.Single();
            Assert.That((post.EffectiveMethod, post.Api), Is.EqualTo(("POST", "record.json")));
            Assert.That(RecordOf(post).Select(e => e.Key), Is.EquivalentTo(new[] { "OrderNo", "CustomerCode", "OrderDate", "Stage", "StartTime", "DeliveryAt" }));
            Assert.That(Value(post, "StartTime"), Is.EqualTo("\"08:45\""));
            Assert.That(Value(post, "DeliveryAt"), Is.EqualTo("\"2026-11-20T01:30:00Z\""));
            Assert.That(Value(post, "OrderDate"), Is.EqualTo("\"2026-11-16\""));
            //読み直しても同じ
            var read = (await DetailAsync("Order", "21"))!;
            Assert.That(Field<TimeFieldData>(read, "StartTime")!.Value, Is.EqualTo(new TimeOnly(8, 45)));
            Assert.That(Field<DateTimeFieldData>(read, "DeliveryAt")!.Value, Is.EqualTo(new DateTime(2026, 11, 20, 1, 30, 0, DateTimeKind.Utc)));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 作成で送らなかった項目にはkintoneの初期値が入る()
        {
            var result = await AddAsync("CustomerById", e =>
            {
                e.Fields["CustomerCode"] = new TextFieldData { Value = "C-100" };
                e.Fields["CustomerName"] = new TextFieldData { Value = "初期値の確かめ" };
                e.Fields["Rank"] = new TextFieldData { Value = null };
            });

            AssertSucceeded(result);
            var row = CustomerByCode("C-100");
            Assert.That(result.DestinationId, Is.EqualTo(row.RecordId().ToString(CultureInfo.InvariantCulture)));
            Assert.That(row.Text("Rank"), Is.EqualTo("B"));
            Assert.That(RecordOf(_h.WriteRequests.Single()).Select(e => e.Key), Is.EquivalentTo(new[] { "CustomerCode", "CustomerName" }));
        }

        [Test]
        public async Task 重複禁止の項目をIdにした作成は送ったキーがIdになる()
        {
            var result = await AddAsync("Customer", e =>
            {
                e.Fields["Id"] = new IdFieldData { Value = "C-200" };
                e.Fields["CustomerName"] = new TextFieldData { Value = "手入力のキー \"引用\" \\ 円記号" };
                e.Fields["CreditLimit"] = new NumberFieldData { Value = 12.25m };
                e.Fields["Since"] = new DateFieldData { Value = new DateOnly(2026, 2, 28) };
            });

            AssertSucceeded(result);
            Assert.That(result.DestinationId, Is.EqualTo("C-200"));
            var row = CustomerByCode("C-200");
            Assert.That(row.Text("CustomerName"), Is.EqualTo("手入力のキー \"引用\" \\ 円記号"));
            Assert.That(row.Number("CreditLimit"), Is.EqualTo(12.25m));
            Assert.That(row.Date("Since"), Is.EqualTo(new DateOnly(2026, 2, 28)));
            Assert.That(Field<TextFieldData>((await DetailAsync("Customer", "C-200"))!, "CustomerName")!.Value, Is.EqualTo("手入力のキー \"引用\" \\ 円記号"));
        }

        // ---------------------------------------------------------------- kintone の検証のエラー

        [Test]
        public async Task 必須の項目が無い作成はkintoneの項目のエラーで上がり行は作られない()
        {
            var before = _h.Records(MockHarness.OrderApp).Count;

            var result = await AddAsync("Order", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            var ex = FindKintoneException(result.Exception);
            Assert.That(ex, Is.Not.Null, result.ExceptionMessage);
            Assert.That((ex!.Kind, ex.StatusCode, ex.ErrorCode), Is.EqualTo((KintoneErrorKind.InvalidRequest, (HttpStatusCode?)HttpStatusCode.BadRequest, "CB_VA01")));
            Assert.That(result.ExceptionMessage, Does.Contain("OrderNo").And.Contain("必須です。"));
            Assert.That(_h.Records(MockHarness.OrderApp), Has.Count.EqualTo(before));
        }

        [Test]
        public async Task 必須の項目を空にする更新もエラーで値は変わらない()
        {
            var result = await UpdateAsync("Customer", "C-001", e => e.Fields["CustomerName"] = new TextFieldData { Value = null });

            Assert.That(FindKintoneException(result.Exception)?.ErrorCode, Is.EqualTo("CB_VA01"), result.ExceptionMessage);
            Assert.That(result.ExceptionMessage, Does.Contain("CustomerName"));
            Assert.That(CustomerByCode("C-001").Text("CustomerName"), Is.EqualTo("青葉商事株式会社"));
        }

        [Test]
        public async Task 重複禁止の違反はkintoneの項目のエラーで上がる()
        {
            var created = await AddAsync("Customer", e =>
            {
                e.Fields["Id"] = new IdFieldData { Value = "C-001" };
                e.Fields["CustomerName"] = new TextFieldData { Value = "重複" };
            });
            var updated = await UpdateAsync("Order", "4", e => e.Fields["OrderNo"] = new TextFieldData { Value = "SO-2026-0001" });

            Assert.That(FindKintoneException(created.Exception)?.ErrorCode, Is.EqualTo("CB_VA01"), created.ExceptionMessage);
            Assert.That(created.ExceptionMessage, Does.Contain("CustomerCode").And.Contain("値がほかのレコードと重複しています。"));
            Assert.That(updated.ExceptionMessage, Does.Contain("OrderNo").And.Contain("値がほかのレコードと重複しています。"));
            Assert.That(_h.Records(MockHarness.CustomerApp).Count(e => e.Text("CustomerCode") == "C-001"), Is.EqualTo(1));
            Assert.That(Order(4).Text("OrderNo"), Is.EqualTo("SO-2026-0004"));
        }

        [Test]
        public async Task 選択肢に無い値はエラー()
        {
            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "無い選択肢" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest), result.ExceptionMessage);
            Assert.That(Order(4).Text("Stage"), Is.EqualTo("受注"));
        }

        // ---------------------------------------------------------------- 更新・削除

        [Test]
        public async Task 更新は変えた項目だけをPUTする()
        {
            var before = Order(4);

            var result = await UpdateAsync("Order", "4", e =>
            {
                e.Fields["Stage"] = new TextFieldData { Value = "出荷済" };
                e.Fields["OrderDate"] = new DateFieldData { Value = null };
            });

            AssertSucceeded(result);
            var put = _h.WriteRequests.Single();
            Assert.That((put.EffectiveMethod, put.Api), Is.EqualTo(("PUT", "record.json")));
            var body = MockHarness.Body(put);
            Assert.That(body["id"]!.ToJsonString(), Is.EqualTo("\"4\""));
            Assert.That(body.ContainsKey("revision"), Is.False);
            Assert.That(RecordOf(put).Select(e => e.Key), Is.EquivalentTo(new[] { "Stage", "OrderDate" }));
            var after = Order(4);
            Assert.That(after.Text("Stage"), Is.EqualTo("出荷済"));
            Assert.That(after.Date("OrderDate"), Is.Null);
            //送っていない項目はそのまま
            foreach (var code in new[] { "OrderNo", "CustomerCode", "StartTime", "Memo" }) Assert.That(after.Text(code), Is.EqualTo(before.Text(code)), code);
            Assert.That(after.Revision(), Is.EqualTo(before.Revision() + 1));
        }

        [Test]
        public async Task 削除はDELETEでレコードが消える()
        {
            var before = _h.Records(MockHarness.OrderApp).Count;

            var result = await DeleteAsync("Order", "20");

            AssertSucceeded(result);
            Assert.That(_h.Record(MockHarness.OrderApp, 20), Is.Null);
            Assert.That(_h.Records(MockHarness.OrderApp), Has.Count.EqualTo(before - 1));
            var delete = _h.WriteRequests.Single();
            Assert.That((delete.EffectiveMethod, delete.Api, QueryValue(delete, "app"), QueryValue(delete, "ids[0]")), Is.EqualTo(("DELETE", "records.json", "3", "20")));
            Assert.That(QueryValue(delete, "revisions[0]"), Is.Null);
        }

        [Test]
        public async Task 無いレコードの更新は失敗し削除は0件として通る()
        {
            var updated = await UpdateAsync("Order", "9999", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });
            var deleted = await DeleteAsync("Order", "9999");

            Assert.That(updated.ExceptionMessage, Is.Not.Empty);
            //本体は物理削除の件数を見ない (DB で 0 行の delete と同じ)
            Assert.That(deleted.ExceptionMessage, Is.Empty);
            Assert.That(_h.WriteRequests.Select(e => e.EffectiveMethod), Is.EqualTo(new[] { "PUT", "DELETE" }));
            Assert.That(_h.Records(MockHarness.OrderApp), Has.Count.EqualTo(20));
        }

        [Test]
        public async Task 重複禁止の項目がIdのモジュールの更新はupdateKeyで削除はレコードIDを引いてから()
        {
            var id = CustomerByCode("C-004").RecordId();

            AssertSucceeded(await UpdateAsync("Customer", "C-004", e => e.Fields["CustomerName"] = new TextFieldData { Value = "すずらん文具店 本店" }));
            var put = _h.WriteRequests.Single();
            Assert.That(MockHarness.Body(put)["updateKey"]!.ToJsonString(), Is.EqualTo("{\"field\":\"CustomerCode\",\"value\":\"C-004\"}"));
            Assert.That(MockHarness.Body(put).ContainsKey("id"), Is.False);
            Assert.That(_h.Record(MockHarness.CustomerApp, id)!.Text("CustomerName"), Is.EqualTo("すずらん文具店 本店"));

            _h.Mock.ClearRequests();
            AssertSucceeded(await DeleteAsync("Customer", "C-004"));
            Assert.That(_h.Record(MockHarness.CustomerApp, id), Is.Null);
            var delete = _h.WriteRequests.Single();
            Assert.That(QueryValue(delete, "ids[0]"), Is.EqualTo(id.ToString(CultureInfo.InvariantCulture)));
            Assert.That(MockHarness.Query(_h.ListReads(MockHarness.CustomerApp).Single()), Does.StartWith("CustomerCode in (\"C-004\")"));
        }

        [Test]
        public async Task 重複禁止の項目のIdを変える更新はレコードIDを引いてid指定で書く()
        {
            var id = CustomerByCode("C-015").RecordId();
            var data = new ModuleData { Name = "Customer" };
            //画面でキーを打ち変えたときにクライアントが送る形
            data.Fields["Id"] = new IdFieldData { Value = "@replace:C-015->C-115" };
            data.Fields["CustomerName"] = new TextFieldData { Value = "キーの変更" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "C-015", Update = new() { data } })).Single();

            AssertSucceeded(result);
            var row = _h.Record(MockHarness.CustomerApp, id)!;
            Assert.That((row.Text("CustomerCode"), row.Text("CustomerName")), Is.EqualTo(("C-115", "キーの変更")));
            var put = _h.WriteRequests.Single();
            Assert.That(MockHarness.Body(put)["id"]!.ToJsonString(), Is.EqualTo($"\"{id}\""));
            Assert.That(Value(put, "CustomerCode"), Is.EqualTo("\"C-115\""));
        }

        [Test]
        public async Task 計算フィールドとルックアップのコピー先に値を書こうとすると送らずにエラー()
        {
            var total = await UpdateAsync("Order", "4", e => e.Fields["Total"] = new NumberFieldData { Value = 5 });
            var copy = await UpdateAsync("Order", "4", e => e.Fields["CustomerNameCopy"] = new TextFieldData { Value = "書けない" });
            var created = await AddAsync("Order", e =>
            {
                e.Fields["OrderNo"] = new TextFieldData { Value = "SO-X" };
                e.Fields["Total"] = new NumberFieldData { Value = 5 };
            });

            Assert.That(total.ExceptionMessage, Does.Contain("read-only"));
            Assert.That(copy.ExceptionMessage, Does.Contain("read-only"));
            Assert.That(created.ExceptionMessage, Does.Contain("read-only"));
            Assert.That(_h.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 計算フィールドは保存のあと読める()
        {
            var created = await AddAsync("Order", e =>
            {
                e.Fields["OrderNo"] = new TextFieldData { Value = "SO-CALC" };
                e.Fields["Total"] = new NumberFieldData { Value = null };
            });
            AssertSucceeded(created);

            var read = (await DetailAsync("Order", created.DestinationId))!;

            Assert.That(Order(long.Parse(created.DestinationId, CultureInfo.InvariantCulture)).Number("Total"), Is.EqualTo(0m));
            Assert.That(Field<NumberFieldData>(read, "Total")!.Value, Is.EqualTo(0m));
            Assert.That(RecordOf(_h.WriteRequests.Single()).Select(e => e.Key), Is.EqualTo(new[] { "OrderNo" }));
        }

        // ---------------------------------------------------------------- ルックアップ

        [Test]
        public async Task ルックアップのキーを保存するとコピー先が読める()
        {
            var created = await AddAsync("Order", e =>
            {
                e.Fields["OrderNo"] = new TextFieldData { Value = "SO-LOOKUP" };
                e.Fields["Customer"] = new LinkFieldData { Value = "C-013" };
            });
            AssertSucceeded(created);
            var updated = await UpdateAsync("Order", "5", e => e.Fields["Customer"] = new LinkFieldData { Value = "C-009" });
            AssertSucceeded(updated);

            var read = (await DetailAsync("Order", created.DestinationId))!;
            var read5 = (await DetailAsync("Order", "5"))!;

            Assert.That(Field<TextFieldData>(read, "CustomerNameCopy")!.Value, Is.EqualTo("Blue Harbor Trading"));
            Assert.That(Field<TextFieldData>(read, "Customer.CustomerName")!.Value, Is.EqualTo("Blue Harbor Trading"));
            Assert.That(Field<TextFieldData>(read5, "CustomerNameCopy")!.Value, Is.EqualTo("桜川電機株式会社"));
            Assert.That(Order(5).Text("CustomerNameCopy"), Is.EqualTo("桜川電機株式会社"));
        }

        [Test]
        public async Task 参照先に無いルックアップの値はエラー()
        {
            var created = await AddAsync("Order", e =>
            {
                e.Fields["OrderNo"] = new TextFieldData { Value = "SO-NOLOOKUP" };
                e.Fields["Customer"] = new LinkFieldData { Value = "C-999" };
            });
            var updated = await UpdateAsync("Order", "4", e => e.Fields["Customer"] = new LinkFieldData { Value = "C-999" });

            var ex = FindKintoneException(created.Exception);
            Assert.That(ex, Is.Not.Null, created.ExceptionMessage);
            Assert.That((ex!.Kind, ex.ErrorCode), Is.EqualTo((KintoneErrorKind.InvalidRequest, "GAIA_LO04")));
            Assert.That(FindKintoneException(updated.Exception)?.ErrorCode, Is.EqualTo("GAIA_LO04"), updated.ExceptionMessage);
            Assert.That(_h.Records(MockHarness.OrderApp), Has.Count.EqualTo(20));
            Assert.That(Order(4).Text("CustomerCode"), Is.EqualTo("C-013"));
        }

        // ---------------------------------------------------------------- 楽観ロック ($revision)

        [Test]
        public async Task 楽観ロックは版が合えば更新し版はkintoneが進める()
        {
            var row = (await DetailAsync("CustomerLocked", "2"))!;
            Assert.That(row.Fields[SystemFieldNames.OptimisticLocking], Is.Not.Null);

            var result = await UpdateAsync("CustomerLocked", "2", e =>
            {
                e.Fields["Note"] = new TextFieldData { Value = "ロックつき" };
                e.Fields[SystemFieldNames.OptimisticLocking] = row.Fields[SystemFieldNames.OptimisticLocking];
            });

            AssertSucceeded(result);
            var put = _h.WriteRequests.Single();
            Assert.That(MockHarness.Body(put)["revision"]!.ToJsonString(), Is.EqualTo("\"3\""));
            Assert.That(RecordOf(put).Select(e => e.Key), Is.EqualTo(new[] { "Note" }));
            Assert.That(_h.Record(MockHarness.CustomerApp, 2)!.Revision(), Is.EqualTo(4));
            Assert.That(_h.Record(MockHarness.CustomerApp, 2)!.Text("Note"), Is.EqualTo("ロックつき"));
        }

        [Test]
        public async Task 楽観ロックはほかの人が先に更新していれば競合のエラーで書かない()
        {
            var row = (await DetailAsync("CustomerLocked", "2"))!;
            var revision = _h.Mock.UpdateRecord(MockHarness.CustomerApp, 2, new Dictionary<string, object?> { ["Note"] = "ほかの人の更新" }, "yamada.taro");
            Assert.That(revision, Is.EqualTo(4));

            var result = await UpdateAsync("CustomerLocked", "2", e =>
            {
                e.Fields["Note"] = new TextFieldData { Value = "古い版から" };
                e.Fields[SystemFieldNames.OptimisticLocking] = row.Fields[SystemFieldNames.OptimisticLocking];
            });

            Assert.That(result.ExceptionMessage, Does.Contain("The record has been updated by another user"));
            var after = _h.Record(MockHarness.CustomerApp, 2)!;
            Assert.That((after.Text("Note"), after.Revision()), Is.EqualTo(("ほかの人の更新", 4L)));
        }

        /// <summary>
        /// 本体の物理削除は版を条件に入れない (delete ... where Id のキー = @p だけ。DB でも同じ)。kintone にも revision を送らないので、版が古くても消える。
        /// </summary>
        [Test]
        public async Task 物理削除は本体が版を条件にしないので版が古くても消える()
        {
            var row = (await DetailAsync("CustomerLocked", "2"))!;
            var locking = (OptimisticLockingFieldData)row.Fields[SystemFieldNames.OptimisticLocking];
            _h.Mock.UpdateRecord(MockHarness.CustomerApp, 2, new Dictionary<string, object?> { ["Note"] = "ほかの人の更新" });

            AssertSucceeded(await DeleteAsync("CustomerLocked", "2", locking));

            Assert.That(_h.Record(MockHarness.CustomerApp, 2), Is.Null);
            Assert.That(QueryValue(_h.WriteRequests.Single(), "revisions[0]"), Is.Null);
        }

        // ---------------------------------------------------------------- 障害 (書き込みは再送しない)

        [Test]
        public async Task 作成のあとの5xxは結果不明で行はできている()
        {
            _h.Mock.FailAfterWrite(502, when: e => e.EffectiveMethod == "POST");

            var result = await AddAsync("Order", e => e.Fields["OrderNo"] = new TextFieldData { Value = "SO-FAULT" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(_h.Records(MockHarness.OrderApp).Count(e => e.Text("OrderNo") == "SO-FAULT"), Is.EqualTo(1));
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 作成のあとに接続が切れたら結果不明()
        {
            _h.Mock.DisconnectAfterWrite(e => e.EffectiveMethod == "POST");

            var result = await AddAsync("Order", e => e.Fields["OrderNo"] = new TextFieldData { Value = "SO-CUT" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(_h.Records(MockHarness.OrderApp).Count(e => e.Text("OrderNo") == "SO-CUT"), Is.EqualTo(1));
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 更新のあとに接続が切れたら結果不明()
        {
            _h.Mock.DisconnectAfterWrite(e => e.EffectiveMethod == "PUT");

            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(Order(4).Text("Stage"), Is.EqualTo("仮"));
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 更新のあとの5xxは結果不明()
        {
            _h.Mock.FailAfterWrite(500, when: e => e.EffectiveMethod == "PUT");

            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(Order(4).Text("Stage"), Is.EqualTo("仮"));
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 削除のあとに接続が切れたら結果不明()
        {
            _h.Mock.DisconnectAfterWrite(e => e.EffectiveMethod == "DELETE");

            var result = await DeleteAsync("Order", "20");

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(_h.Record(MockHarness.OrderApp, 20), Is.Null);
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 書き込みのタイムアウトは結果不明でやり直さない()
        {
            await _h.DisposeAsync();
            _h = await MockHarness.StartAsync(edit: e => e.TimeoutSeconds = 1);
            _h.Mock.DelayNext(TimeSpan.FromSeconds(3), e => e.EffectiveMethod == "PUT");

            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 書き込みの前のエラーコードつきの5xxは処理されなかったエラーでやり直さない()
        {
            _h.Mock.FailNext(503, when: e => e.EffectiveMethod == "PUT");

            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            var ex = FindKintoneException(result.Exception);
            Assert.That(ex, Is.Not.Null, result.ExceptionMessage);
            Assert.That((ex!.Kind, ex.ErrorCode), Is.EqualTo((KintoneErrorKind.InvalidRequest, "MOCK_FAULT")));
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
            Assert.That(Order(4).Text("Stage"), Is.EqualTo("受注"));
        }

        [Test]
        public async Task 書き込みのエラーコードの無い5xxは結果不明でやり直さない()
        {
            _h.Mock.FailNext(503, "Service Unavailable", e => e.EffectiveMethod == "PUT");

            var result = await UpdateAsync("Order", "4", e => e.Fields["Stage"] = new TextFieldData { Value = "仮" });

            Assert.That(FindKintoneException(result.Exception)?.Kind, Is.EqualTo(KintoneErrorKind.WriteResultUnknown), result.ExceptionMessage);
            Assert.That(_h.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 読み出しの一時的な503はやり直して成功する()
        {
            await _h.Connection.GetTableDefinitionsAsync();
            _h.Mock.FailNext(503, "busy", e => e.Api == "records.json", 2);

            var items = (await _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = null }, 0)).Items;

            Assert.That(items, Has.Count.EqualTo(15));
            Assert.That(_h.Delays, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task 読み出しで接続が切れたらやり直して成功する()
        {
            await _h.Connection.GetTableDefinitionsAsync();
            _h.Mock.DisconnectNext(e => e.Api == "records.json");

            var item = (await DetailAsync("Order", "1"))!;

            Assert.That(Id(item), Is.EqualTo("1"));
            Assert.That(_h.Delays, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 読み出しの503が続けば4回で諦める()
        {
            await _h.Connection.GetTableDefinitionsAsync();
            _h.Mock.FailNext(503, "busy", e => e.Api == "records.json", 10);

            var ex = Assert.ThrowsAsync<KintoneException>(() => _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = null }, 0));

            Assert.That(ex!.Kind, Is.EqualTo(KintoneErrorKind.ServiceUnavailable));
            Assert.That(_h.Mock.Requests.Count(e => e.Api == "records.json"), Is.EqualTo(4));
        }
    }
}
