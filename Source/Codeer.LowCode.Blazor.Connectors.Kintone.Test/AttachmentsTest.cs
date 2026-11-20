using Codeer.LowCode.Blazor.Aggregation;
using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using Kintone.Mock;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness.MockTestUtil;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// 添付ファイルのフィールド (FILE): 読むと kintone の値の JSON、書くと base64 の要素をアップロードして足し deleted の要素を消す
    /// (そのレコードのその列に今ある fileKey だけ)。ダウンロードは現在の利用者が読めるレコードのそのフィールドのファイルだけ。モック (Kintone.Mock) が相手。
    /// </summary>
    public class AttachmentsTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp()
        {
            _h = await MockHarness.StartAsync(editDesign: d =>
            {
                var order = d.Modules.Find("Order")!;
                order.Fields.Add(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
                //添付の列に文字のフィールドを置いたもの (ダウンロードの対象にならない)
                order.Fields.Add(new TextFieldDesign { Name = "FilesText", DbColumn = "Attachments" });

                var modules = (IEditableModuleDesign)d.Modules;
                //Id が重複禁止の受注番号
                var byNo = Module(d, "OrderByNo", "OrderNo", true);
                modules.Add(byNo);
                //論理削除 (納品日時のあるレコードは削除済み)
                var softDeleted = Module(d, "OrderSoftDeleted");
                softDeleted.Fields.Add(new DateTimeFieldDesign { Name = SystemFieldNames.DeletedAt, DbColumn = "DeliveryAt", SaveAsUtc = true });
                modules.Add(softDeleted);
                //行の閲覧条件 (進捗が「仮」のレコードだけ読める)
                var draftOnly = Module(d, "OrderDraftOnly");
                draftOnly.Fields.Add(new TextFieldDesign { Name = "Stage", DbColumn = "Stage" });
                draftOnly.DataReadCondition = new ModuleMatchCondition { ModuleName = "OrderDraftOnly", Condition = MultiMatchCondition.And(Match("Stage.Value", MatchComparison.Equal, "仮")) };
                modules.Add(draftOnly);
                //添付のフィールドの読み取りの権限 (利用者のモジュールが無いので CurrentUser の条件は成り立たない = 読めない)
                var denied = Module(d, "OrderFilesDenied");
                var permission = new PermissionFieldDesign { Name = "Permission1", TargetFields = { "Files" } };
                permission.ReadCondition.ModuleName = "OrderFilesDenied";
                permission.ReadCondition.Condition = Match("CurrentUser.Rank.Value", MatchComparison.GreaterThanOrEqual, 1);
                denied.Fields.Add(permission);
                modules.Add(denied);
                //kintone でないデータソースのモジュール
                var local = new ModuleDesign { Name = "Local", DataSourceName = "Other", DbTable = "Order" };
                local.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
                local.Fields.Add(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
                modules.Add(local);
            });
        }

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        static ModuleDesign Module(DesignData d, string name, string idColumn = "$id", bool manualId = false)
        {
            var module = new ModuleDesign { Name = name, DataSourceName = TestConnections.DataSourceName, DbTable = "Order", CanCreate = true, CanUpdate = true, CanDelete = true };
            module.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = idColumn, IsManualInput = manualId });
            module.Fields.Add(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
            return module;
        }

        static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        //受注 (レコード ID) の添付の値 (records.json の形)
        JsonArray Files(long id) => (JsonArray)_h.Record(MockHarness.OrderApp, id)!["Attachments"]!["value"]!;

        string FileKey(long id, int index = 0) => Files(id)[index]!["fileKey"]!.GetValue<string>();

        Task<int> UpdateAsync(long id, object? value)
            => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "update \"Order\" set \"Attachments\"=@p0 where \"$id\"=@pid0", new() { ["@p0"] = value, ["@pid0"] = id });

        List<MockRecordedRequest> Uploads => _h.Mock.Requests.Where(e => e.Api == "file.json" && e.EffectiveMethod == "POST").ToList();

        List<MockRecordedRequest> Downloads => _h.Mock.Requests.Where(e => e.Api == "file.json" && e.EffectiveMethod == "GET").ToList();

        //PUT の本文の添付の値
        static JsonArray SentFiles(MockRecordedRequest request) => (JsonArray)MockHarness.Body(request)["record"]!["Attachments"]!["value"]!;

        // ---------------------------------------------------------------- 定義・読み出し

        [Test]
        public async Task 表の定義では書ける列で本体には添付の型の名前の文字の列()
        {
            var definition = await _h.Connection.GetTableDefinitionAsync("Order");
            var column = definition.FindColumn("Attachments")!;
            Assert.That((column.IsAttachments, column.IsReadOnly, column.Kind), Is.EqualTo((true, false, KintoneColumnKind.Json)));
            var dbColumn = definition.ToTableDefinition().Columns.Single(e => e.Name == "Attachments");
            Assert.That((dbColumn.RawDbTypeName, dbColumn.NetTypeFullName), Is.EqualTo(("kintone_file", typeof(string).FullName)));
        }

        [Test]
        public void テーブルの中の添付ファイルは読み取り専用のまま()
        {
            using var json = JsonDocument.Parse("""
                {"properties": {
                  "Files": {"type": "FILE", "code": "Files"},
                  "Rows": {"type": "SUBTABLE", "code": "Rows", "fields": {"RowFiles": {"type": "FILE", "code": "RowFiles"}}}
                }}
                """);
            var definition = KintoneTableDefinition.ParseFields("T", json.RootElement);
            Assert.That(definition.FindColumn("Files")!.IsReadOnly, Is.False);
            Assert.That(definition.FindColumn("Rows")!.FindSubField("RowFiles")!.IsReadOnly, Is.True);
        }

        [Test]
        public async Task 読むとkintoneの値のJSONでファイルが無ければnull()
        {
            var item = (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = MultiMatchCondition.And(Match("Id.Value", MatchComparison.Equal, "1")) }, 0)).Items.Single();
            var value = JsonNode.Parse(Field<KintoneAttachmentsFieldData>(item, "Files")!.Value!)!.AsArray();
            Assert.That(value.Single()!["name"]!.GetValue<string>(), Is.EqualTo("見積書_1.pdf"));
            Assert.That(value.Single()!["fileKey"]!.GetValue<string>(), Is.EqualTo(FileKey(1)));
            Assert.That(value.Single()!["size"]!.GetValue<string>(), Is.EqualTo("20480"));

            var empty = (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = MultiMatchCondition.And(Match("Id.Value", MatchComparison.Equal, "3")) }, 0)).Items.Single();
            //空の値 (null) は本体がフィールドのデータを作らない
            Assert.That(Field<KintoneAttachmentsFieldData>(empty, "Files")?.Value, Is.Null);
        }

        // ---------------------------------------------------------------- 書き込み

        [Test]
        public async Task 読んだままの値を書いても送らない()
        {
            var stored = Files(1).ToJsonString();
            var revision = _h.Record(MockHarness.OrderApp, 1)!.Revision();
            Assert.That(await UpdateAsync(1, stored), Is.EqualTo(1));
            Assert.That(await UpdateAsync(1, null), Is.EqualTo(1));
            Assert.That(await UpdateAsync(1, string.Empty), Is.EqualTo(1));
            Assert.That(_h.WriteRequests, Is.Empty);
            Assert.That(Uploads, Is.Empty);
            Assert.That(_h.Record(MockHarness.OrderApp, 1)!.Revision(), Is.EqualTo(revision));
        }

        [Test]
        public async Task 無いレコードの更新は0件で何も送らない()
        {
            var value = new JsonArray(new JsonObject { ["name"] = "c.txt", ["base64"] = Base64("c") }).ToJsonString();
            Assert.That(await UpdateAsync(999, value), Is.EqualTo(0));
            Assert.That(Uploads, Is.Empty);
            Assert.That(_h.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 追加はアップロードして今あるファイルの後ろに足し読んだ版で書く()
        {
            var kept = FileKey(1);
            var revision = _h.Record(MockHarness.OrderApp, 1)!.Revision();
            var value = new JsonArray(
                Files(1)[0]!.DeepClone(),
                new JsonObject { ["name"] = "追加.txt", ["contentType"] = "text/plain", ["base64"] = Base64("ccc") }).ToJsonString();
            Assert.That(await UpdateAsync(1, value), Is.EqualTo(1));

            Assert.That(Uploads.Select(e => e.UploadedFileName), Is.EqualTo(new[] { "追加.txt" }));
            var put = _h.WriteRequests.Single();
            Assert.That(MockHarness.Body(put)["revision"]!.GetValue<string>(), Is.EqualTo(revision.ToString()));
            var sent = SentFiles(put);
            Assert.That(sent[0]!.ToJsonString(), Is.EqualTo(new JsonObject { ["fileKey"] = kept }.ToJsonString()));
            Assert.That(sent.Count, Is.EqualTo(2));

            var files = Files(1);
            Assert.That(files.Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "見積書_1.pdf", "追加.txt" }));
            Assert.That(files[1]!["contentType"]!.GetValue<string>(), Is.EqualTo("text/plain"));
            Assert.That(Encoding.UTF8.GetString(_h.Mock.GetFileContent(FileKey(1, 1))!), Is.EqualTo("ccc"));
            Assert.That(_h.Mock.Uploads, Is.Empty);
        }

        [Test]
        public async Task 追加と削除を一緒に書ける()
        {
            var value = new JsonArray(
                new JsonObject { ["fileKey"] = FileKey(1), ["deleted"] = true },
                new JsonObject { ["name"] = "c.bin", ["base64"] = Base64("cc") }).ToJsonString();
            Assert.That(await UpdateAsync(1, value), Is.EqualTo(1));

            var files = Files(1);
            Assert.That(files.Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "c.bin" }));
            //種類が無ければ application/octet-stream で送る
            Assert.That(files[0]!["contentType"]!.GetValue<string>(), Is.EqualTo("application/octet-stream"));
        }

        [Test]
        public async Task 削除だけならアップロードせずに残すファイルだけ送る()
        {
            var value = new JsonArray(new JsonObject { ["fileKey"] = FileKey(1), ["deleted"] = true }).ToJsonString();
            Assert.That(await UpdateAsync(1, value), Is.EqualTo(1));
            Assert.That(Uploads, Is.Empty);
            Assert.That(SentFiles(_h.WriteRequests.Single()), Is.Empty);
            Assert.That(Files(1), Is.Empty);
        }

        [Test]
        public async Task 別のレコードのfileKeyは消せない()
        {
            var other = FileKey(2);
            var value = new JsonArray(new JsonObject { ["fileKey"] = other, ["deleted"] = true }).ToJsonString();
            Assert.That(await UpdateAsync(1, value), Is.EqualTo(1));
            Assert.That(_h.WriteRequests, Is.Empty);
            Assert.That(FileKey(2), Is.EqualTo(other));
            Assert.That(Files(1).Count, Is.EqualTo(1));
        }

        [Test]
        public async Task クライアントのfileKeyでほかのファイルを残せない()
        {
            //消さない要素の fileKey (別のレコードのもの) は見ない。残すのはこのレコードに今あるものだけ
            var value = new JsonArray(
                new JsonObject { ["fileKey"] = FileKey(2), ["name"] = "見積書_2.pdf", ["size"] = "20480" },
                new JsonObject { ["name"] = "c.txt", ["base64"] = Base64("c") }).ToJsonString();
            await UpdateAsync(1, value);
            Assert.That(SentFiles(_h.WriteRequests.Single()).Select(e => e!["fileKey"]!.GetValue<string>()), Does.Not.Contain(FileKey(2)));
            Assert.That(Files(1).Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "見積書_1.pdf", "c.txt" }));
        }

        [Test]
        public async Task クライアントの余計なキーはkintoneに届かない()
        {
            var value = new JsonArray(new JsonObject
            {
                ["name"] = "c.txt", ["contentType"] = "text/plain", ["base64"] = Base64("c"),
                ["fileKey"] = FileKey(2), ["size"] = "999999", ["extra"] = 1,
            }).ToJsonString();
            await UpdateAsync(1, value);
            foreach (var item in SentFiles(_h.WriteRequests.Single()))
                Assert.That(item!.AsObject().Select(e => e.Key), Is.EqualTo(new[] { "fileKey" }));
            Assert.That(SentFiles(_h.WriteRequests.Single()).Select(e => e!["fileKey"]!.GetValue<string>()), Does.Not.Contain(FileKey(2)));
            Assert.That(Files(1)[1]!["size"]!.GetValue<string>(), Is.EqualTo("1"));
        }

        [Test]
        public async Task アップロードの後で更新が失敗したらファイルは一時保管領域に残る()
        {
            _h.Mock.FailNext(400, when: e => e.Api == "record.json" && e.EffectiveMethod == "PUT");
            var value = new JsonArray(new JsonObject { ["name"] = "c.txt", ["base64"] = Base64("c") }).ToJsonString();
            Assert.ThrowsAsync<KintoneException>(() => UpdateAsync(1, value));
            Assert.That(_h.Mock.Uploads.Values.Select(e => e.Name), Is.EqualTo(new[] { "c.txt" }));
            Assert.That(Files(1).Count, Is.EqualTo(1));
        }

        [Test]
        public async Task 作成では追加だけ送る()
        {
            var value = new JsonArray(
                new JsonObject { ["name"] = "新規.txt", ["contentType"] = "text/plain", ["base64"] = Base64("new") },
                new JsonObject { ["fileKey"] = FileKey(1), ["deleted"] = true }).ToJsonString();
            var id = await _h.Accessor.InsertAsync(TestConnections.DataSourceName, "insert into \"Order\"(\"OrderNo\",\"Attachments\") values(@p0,@p1) returning \"$id\"",
                new() { ["@p0"] = "SO-NEW", ["@p1"] = value });

            var files = Files(long.Parse(id));
            Assert.That(files.Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "新規.txt" }));
            Assert.That(Encoding.UTF8.GetString(_h.Mock.GetFileContent(files[0]!["fileKey"]!.GetValue<string>())!), Is.EqualTo("new"));
            Assert.That(Files(1).Count, Is.EqualTo(1));
        }

        [Test]
        public async Task 作成で足すものが無ければ添付を送らない()
        {
            await _h.Accessor.InsertAsync(TestConnections.DataSourceName, "insert into \"Order\"(\"OrderNo\",\"Attachments\") values(@p0,@p1) returning \"$id\"",
                new() { ["@p0"] = "SO-NEW", ["@p1"] = "[]" });
            Assert.That(MockHarness.Body(_h.WriteRequests.Single())["record"]!.AsObject().ContainsKey("Attachments"), Is.False);
        }

        [Test]
        public async Task 画面からの保存で足して消せる()
        {
            var add = new ModuleData { Name = "Order" };
            add.Fields["Id"] = new IdFieldData { Value = "4" };
            add.Fields["Files"] = new KintoneAttachmentsFieldData { Value = new JsonArray(new JsonObject { ["name"] = "a.txt", ["base64"] = Base64("a") }).ToJsonString() };
            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "4", Update = new() { add } } });
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(Files(4).Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "a.txt" }));

            var delete = new ModuleData { Name = "Order" };
            delete.Fields["Id"] = new IdFieldData { Value = "4" };
            delete.Fields["Files"] = new KintoneAttachmentsFieldData { Value = new JsonArray(new JsonObject { ["fileKey"] = FileKey(4), ["deleted"] = true }).ToJsonString() };
            results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "4", Update = new() { delete } } });
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(Files(4), Is.Empty);
        }

        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("[1]")]
        [TestCase("[{\"base64\":\"YWJj\"}]")]
        [TestCase("[{\"name\":\"x\",\"base64\":\"***\"}]")]
        [TestCase("[{\"name\":\"x\",\"base64\":1}]")]
        [TestCase("[{\"deleted\":true}]")]
        [TestCase("[{\"fileKey\":\"\",\"deleted\":true}]")]
        [TestCase("[{\"fileKey\":\"k\",\"deleted\":true,\"name\":\"x\",\"base64\":\"YWJj\"}]")]
        public void 形が不正ならエラー(string value)
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => UpdateAsync(1, value))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.InvalidRequest));
            Assert.That(_h.WriteRequests, Is.Empty);
            Assert.That(Uploads, Is.Empty);
        }

        [Test]
        public void 条件と並びには使えない()
        {
            Assert.That(FindKintoneException(Assert.CatchAsync(() => _h.IO.GetListAsync(new SearchCondition("Order") { Condition = MultiMatchCondition.And(Match("FilesText.Value", MatchComparison.Like, "a")) }, 0))), Is.Not.Null);
            var sorted = new SearchCondition("Order");
            sorted.SortConditions.Add(new SortCondition { Variable = "Files.Value" });
            Assert.That(FindKintoneException(Assert.CatchAsync(() => _h.IO.GetListAsync(sorted, 0))), Is.Not.Null);
        }

        [TestCase("Files.Value")]
        [TestCase("FilesText.Value")]
        public void 並びに使うとkintoneに送らずにNotSupported(string variable)
        {
            var sorted = new SearchCondition("Order");
            sorted.SortConditions.Add(new SortCondition { Variable = variable });
            var e = FindKintoneException(Assert.CatchAsync(() => _h.IO.GetListAsync(sorted, 0)));
            Assert.That(e?.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
            Assert.That(e!.Message, Does.Contain("'Attachments'"));
            Assert.That(_h.Reads(MockHarness.OrderApp), Is.Empty);
        }

        [TestCase("Files.Value", null)]
        [TestCase("FilesText.Value", null)]
        [TestCase(null, AggregateFunction.CountDistinct)]
        [TestCase(null, AggregateFunction.Min)]
        [TestCase(null, AggregateFunction.Max)]
        public void 集計の軸と集計する列に使うとNotSupported(string? group, AggregateFunction? function)
        {
            var condition = new AggregateCondition("Order");
            if (group != null) condition.Groups.Add(new ValueGroup { Variable = group });
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Count });
            if (function != null) condition.Measures.Add(new AggregateMeasure { Function = function.Value, Variable = "FilesText.Value" });
            var e = FindKintoneException(Assert.CatchAsync(() => _h.IO.AggregateAsync(condition)));
            Assert.That(e?.Kind, Is.EqualTo(KintoneErrorKind.NotSupported));
            Assert.That(e!.Message, Does.Contain("'Attachments'"));
            Assert.That(_h.Reads(MockHarness.OrderApp), Is.Empty);
        }

        // ---------------------------------------------------------------- ダウンロード

        Task<KintoneAttachmentFile?> DownloadAsync(string? module = "Order", string? id = "1", string? field = "Files", string? key = null)
            => _h.Accessor.GetAttachmentAsync(_h.IO, _h.Design, module, id, field, key ?? FileKey(1));

        [Test]
        public async Task ダウンロードはレコードの名前と中身を返す()
        {
            var file = await DownloadAsync();
            Assert.That(file!.FileName, Is.EqualTo("見積書_1.pdf"));
            Assert.That(Encoding.UTF8.GetString(file.Content), Is.EqualTo("mock content of 見積書_1.pdf"));
            Assert.That(Downloads.Single().Query.Single(e => e.Key == "fileKey").Value, Is.EqualTo(FileKey(1)));
        }

        [Test]
        public async Task 足したファイルをダウンロードできる()
        {
            await UpdateAsync(1, new JsonArray(new JsonObject { ["name"] = "b.bin", ["base64"] = Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }) }).ToJsonString());
            var file = await DownloadAsync(key: FileKey(1, 1));
            Assert.That((file!.FileName, file.Content), Is.EqualTo(("b.bin", new byte[] { 0, 1, 2, 255 })));
        }

        [Test]
        public async Task Idが重複禁止のフィールドのモジュールでもダウンロードできる()
        {
            var file = await DownloadAsync(module: "OrderByNo", id: "SO-2026-0001");
            Assert.That(file!.FileName, Is.EqualTo("見積書_1.pdf"));
        }

        [Test]
        public async Task レコードに無いfileKeyは見つからないで中身を読みに行かない()
        {
            Assert.That(await DownloadAsync(key: FileKey(2)), Is.Null);
            Assert.That(await DownloadAsync(key: "no-such-key"), Is.Null);
            Assert.That(await DownloadAsync(id: "2", key: FileKey(1)), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task kintoneに無ければ見つからない()
        {
            _h.Mock.FailNext(404, when: e => e.Api == "file.json", count: 10);
            Assert.That(await DownloadAsync(), Is.Null);
        }

        [TestCase("Order", "1", "FilesText", Description = "添付のフィールドでない")]
        [TestCase("Order", "1", "Stage", Description = "添付の列でない")]
        [TestCase("Order", "1", "Nothing", Description = "フィールドが無い")]
        [TestCase("Nothing", "1", "Files", Description = "モジュールが無い")]
        [TestCase("Order", "x", "Files", Description = "Id が数でない")]
        [TestCase("Order", "", "Files", Description = "Id が空")]
        [TestCase(null, null, null, Description = "何も無い")]
        public async Task 対象が違えば見つからない(string? module, string? id, string? field)
        {
            Assert.That(await DownloadAsync(module, id, field), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task fileKeyが空や制御文字なら見つからない()
        {
            Assert.That(await _h.Accessor.GetAttachmentAsync(_h.IO, _h.Design, "Order", "1", "Files", ""), Is.Null);
            Assert.That(await _h.Accessor.GetAttachmentAsync(_h.IO, _h.Design, "Order", "1", "Files", FileKey(1) + "\n"), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task kintoneでないモジュールは見つからない()
        {
            Assert.That(await DownloadAsync(module: "Local"), Is.Null);
            Assert.That(_h.Inner.Calls, Is.Empty);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task 行の閲覧条件で読めないレコードは見つからない()
        {
            //受注 1 は「受注」(読めない)
            Assert.That(await DownloadAsync(module: "OrderDraftOnly"), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task フィールドの権限で読めなければ見つからない()
        {
            Assert.That(await DownloadAsync(module: "OrderFilesDenied"), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task 論理削除したレコードは見つからない()
        {
            //受注 1 は納品日時がある = 削除済み
            Assert.That(await DownloadAsync(module: "OrderSoftDeleted"), Is.Null);
            //受注 4 は納品日時が無い = 削除されていない
            await UpdateAsync(4, new JsonArray(new JsonObject { ["name"] = "d.txt", ["base64"] = Base64("d") }).ToJsonString());
            var file = await DownloadAsync(module: "OrderSoftDeleted", id: "4", key: FileKey(4));
            Assert.That(file!.FileName, Is.EqualTo("d.txt"));
        }
    }
}
