using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.OperatingModel;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>添付のフィールド (クライアント用パッケージ) のうち、モジュールを作らずに確かめられるところ。</summary>
    public class AttachmentsFieldTest
    {
        const string Stored = "[{\"contentType\":\"text/plain\",\"fileKey\":\"k1\",\"name\":\"a.txt\",\"size\":\"3\"},{\"fileKey\":\"k2\",\"name\":\"b.txt\",\"size\":4}]";

        [Test]
        public void サーバーが名前で見る型の名前と一致する()
            => Assert.That(typeof(KintoneAttachmentsFieldDesign).FullName, Is.EqualTo(KintoneDbAccessor.AttachmentsFieldDesignTypeFullName));

        [Test]
        public void デザインとデータはJSONで型ごと往復する()
        {
            KintoneFieldsInitializer.Initialize();
            var design = new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments", MaxFileSizeMB = 3 };
            var restored = (KintoneAttachmentsFieldDesign)JsonConverterEx.DeserializeObject<FieldDesignBase>(JsonConverterEx.SerializeObject(design, typeof(FieldDesignBase)))!;
            Assert.That((restored.DbColumn, restored.MaxFileSizeMB), Is.EqualTo(("Attachments", 3)));

            var data = new KintoneAttachmentsFieldData { Value = Stored };
            var restoredData = (KintoneAttachmentsFieldData)JsonConverterEx.DeserializeObject<FieldDataBase>(JsonConverterEx.SerializeObject(data, typeof(FieldDataBase)))!;
            Assert.That(restoredData.Value, Is.EqualTo(Stored));
        }

        [Test]
        public void 既定の上限は10MBで検索の部品を持たない()
        {
            var design = new KintoneAttachmentsFieldDesign();
            Assert.That(design.MaxFileSizeMB, Is.EqualTo(10));
            Assert.That(design.GetSearchWebComponentTypeFullName(), Is.Empty);
            Assert.That(design.GetSearchControlTypeFullName(), Is.Empty);
        }

        [Test]
        public void レコードのコピーでは写さない()
            => Assert.That(((IConfirmCopyFieldDesign)new KintoneAttachmentsFieldDesign()).NeedCopy, Is.False);

        [Test]
        public async Task 読んだ値は変更なしでファイルの並びになる()
        {
            var field = new KintoneAttachmentsField(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
            await field.InitializeDataAsync(new KintoneAttachmentsFieldData { Value = Stored });
            Assert.That(field.IsModified, Is.False);
            Assert.That(field.GetSubmitData().FieldData, Is.Null);
            //大きさは文字でも数値でも読む
            Assert.That(field.Entries.Select(e => (e.FileKey, e.Name, e.Size, e.ContentType, e.IsAdded, e.Deleted)),
                Is.EqualTo(new[] { ("k1", "a.txt", (long?)3, "text/plain", false, false), ("k2", "b.txt", (long?)4, (string?)null, false, false) }));
        }

        [Test]
        public async Task 値が無ければファイルは無い()
        {
            var field = new KintoneAttachmentsField(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
            await field.InitializeDataAsync(new KintoneAttachmentsFieldData { Value = null });
            Assert.That(field.Entries, Is.Empty);
            await field.InitializeDataAsync(new KintoneAttachmentsFieldData { Value = "not json" });
            Assert.That(field.Entries, Is.Empty);
        }

        [Test]
        public async Task 消せるのはfileKeyの分かる読んだファイルだけ()
        {
            var field = new KintoneAttachmentsField(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
            //読み直さない保存の後の足したファイル (fileKey が無い) と、保存前の足したファイル (base64)
            await field.InitializeDataAsync(new KintoneAttachmentsFieldData
            {
                Value = "[{\"fileKey\":\"k1\",\"name\":\"a.txt\"},{\"name\":\"saved.txt\",\"size\":\"1\"},{\"name\":\"new.txt\",\"base64\":\"YQ==\"}]",
            });
            Assert.That(field.Entries.Select(KintoneAttachmentsField.CanDelete), Is.EqualTo(new[] { true, false, false }));
            Assert.That(field.Entries[2].Size, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// 自動保存 (本体の AutoSubmitField。レコードを読み直さない軽い保存) の後の添付のフィールド。本体の Module を画面側のサービス (Harness/ClientServices) で作り、
    /// 保存を送信の途中で止めて、送信の間の操作を再現する。保存の後は、送った分だけが保存済みになり、送信の間の操作は変更として残る。
    /// </summary>
    public class AttachmentsFieldAutoSubmitTest
    {
        const string Stored = "[{\"contentType\":\"text/plain\",\"fileKey\":\"k1\",\"name\":\"a.txt\",\"size\":\"3\"},{\"fileKey\":\"k2\",\"name\":\"b.txt\",\"size\":\"4\"}]";

        ClientServices _services = null!;
        Module _module = null!;

        KintoneAttachmentsField Files => _module.GetField<KintoneAttachmentsField>("Files")!;
        TextField Title => _module.GetField<TextField>("Title")!;

        [SetUp]
        public async Task SetUp()
        {
            var design = new DesignData();
            var order = new ModuleDesign { Name = "Order", DataSourceName = "Kintone", DbTable = "Order", CanCreate = true, CanUpdate = true };
            order.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            order.Fields.Add(new KintoneAttachmentsFieldDesign { Name = "Files", DbColumn = "Attachments" });
            order.Fields.Add(new AutoSubmitFieldDesign { Name = "AutoSubmit", DelayMilliseconds = 0 });
            //保存の後処理が終わった目印 (本体はフィールドの並びの順に保存済みにするので、添付より後ろに置く)
            order.Fields.Add(new TextFieldDesign { Name = "Title", DbColumn = "Title" });
            ((IEditableModuleDesign)design.Modules).Add(order);
            _services = new ClientServices(design);

            var data = new ModuleData { Name = "Order" };
            data.Fields["Id"] = new IdFieldData { Value = "1" };
            data.Fields["Files"] = new KintoneAttachmentsFieldData { Value = Stored };
            data.Fields["Title"] = new TextFieldData { Value = "A" };
            _module = await ModuleCreationService.CreateModuleAsync(_services.Core, data);
        }

        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        static string Base64(string text) => Convert.ToBase64String(Bytes(text));

        //自動保存を 1 回: 送信の間に during を行い、保存の後処理が終わるまで待つ
        async Task AutoSubmitAsync(Func<Task>? during = null)
        {
            await Title.SetValueAsync(Title.Value + "+");
            _services.HoldSubmit();
            _module.GetField<AutoSubmitField>("AutoSubmit")!.ScheduleSubmit();
            await _services.WaitSubmitEnteredAsync();
            if (during != null) await during();
            _services.ReleaseSubmit();
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (Title.IsModified)
            {
                if (limit < DateTime.UtcNow) Assert.Fail("自動保存が終わらない");
                await Task.Delay(10);
            }
        }

        //送ったデータの添付の値
        string? SentValue(int index = 0)
            => ((KintoneAttachmentsFieldData)_services.Submits[index].Single().Update.Single().Fields["Files"]).Value;

        string? SubmitValue => (Files.GetSubmitData().FieldData as KintoneAttachmentsFieldData)?.Value;

        KintoneAttachmentsField.Entry Entry(string name) => Files.Entries.Single(e => e.Name == name);

        [Test]
        public async Task 送った値のままなら足した分は名前と大きさだけになり消した分は外れて変更なし()
        {
            Assert.That(await Files.AddFileAsync("x.txt", "text/plain", Bytes("xyz12")), Is.True);
            await Files.SetDeletedAsync(Entry("b.txt"), true);

            await AutoSubmitAsync();

            Assert.That(SentValue(), Does.Contain(Base64("xyz12")).And.Contain("\"deleted\":true"));
            Assert.That(Files.Entries.Select(e => (e.FileKey, e.Name, e.Size, e.Base64, e.Deleted)),
                Is.EqualTo(new[] { ("k1", "a.txt", (long?)3, (string?)null, false), ((string?)null, "x.txt", (long?)5, (string?)null, false) }));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.GetSubmitData().FieldData, Is.Null);
            //足したファイルは読み直すまで fileKey が分からないので消せない
            Assert.That(KintoneAttachmentsField.CanDelete(Entry("x.txt")), Is.False);
        }

        [Test]
        public async Task 送信の間に足したファイルだけが変更として残る()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.AddFileAsync("y.txt", null, Bytes("second")));

            Assert.That(SentValue(), Does.Contain(Base64("first")).And.Not.Contain(Base64("second")));
            Assert.That(Entry("x.txt").IsAdded, Is.False);
            Assert.That(Entry("y.txt").Base64, Is.EqualTo(Base64("second")));
            Assert.That(Files.IsModified, Is.True);
            //次の保存で送るのは後から足した分だけ (先の分をもう一度足さない)
            Assert.That(SubmitValue, Does.Contain(Base64("second")).And.Not.Contain(Base64("first")));

            await AutoSubmitAsync();
            Assert.That(SentValue(1), Does.Contain(Base64("second")).And.Not.Contain(Base64("first")));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.Entries.Select(e => (e.Name, e.IsAdded)), Is.EqualTo(new[] { ("a.txt", false), ("b.txt", false), ("x.txt", false), ("y.txt", false) }));
        }

        [Test]
        public async Task 送信の間に付けた消す印は残り送った追加は保存済みになる()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.SetDeletedAsync(Entry("a.txt"), true));

            Assert.That(SentValue(), Does.Not.Contain("deleted"));
            Assert.That(Entry("x.txt").IsAdded, Is.False);
            Assert.That(Entry("a.txt").Deleted, Is.True);
            Assert.That(Files.IsModified, Is.True);
            Assert.That(SubmitValue, Does.Contain("\"fileKey\":\"k1\"").And.Contain("\"deleted\":true").And.Not.Contain("base64"));
            //印を外せば変更なしに戻る (保存済みの状態は x.txt を含む)
            await Files.SetDeletedAsync(Entry("a.txt"), false);
            Assert.That(Files.IsModified, Is.False);
        }

        [Test]
        public async Task 同じ名前と中身を2つ足して1つだけ送っていたら1つだけ保存済み()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("same"));

            await AutoSubmitAsync(() => Files.AddFileAsync("x.txt", null, Bytes("same")));

            Assert.That(Files.Entries.Where(e => e.Name == "x.txt").Select(e => e.IsAdded), Is.EqualTo(new[] { false, true }));
            Assert.That(Files.IsModified, Is.True);
            var resent = JsonNode.Parse(SubmitValue!)!.AsArray();
            Assert.That(resent.Count(e => e!["base64"] != null), Is.EqualTo(1));
        }

        [Test]
        public async Task 送信の間に取り消した足すファイルは名前だけの保存済みの行になる()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.RemoveAddedAsync(Entry("x.txt")));

            //kintone には付いたので、画面にも名前だけの行 (中身なし・fileKey なし) として出す
            Assert.That(SentValue(), Does.Contain(Base64("first")));
            Assert.That(Files.Entries.Select(e => (e.FileKey, e.Name, e.Base64, e.Deleted)),
                Is.EqualTo(new[] { ("k1", "a.txt", (string?)null, false), ("k2", "b.txt", null, false), ((string?)null, "x.txt", null, false) }));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.GetSubmitData().FieldData, Is.Null);
        }

        [Test]
        public async Task データありで初期化した後にデータなしで読み直すと空になり変更なし()
        {
            //ファイルのあるレコードを開き、消す印を付ける
            await Files.SetDeletedAsync(Entry("a.txt"), true);
            await Files.SetDeletedAsync(Entry("b.txt"), true);
            Assert.That(Files.IsModified, Is.True);

            //読み直したレコードに列のキーが無い (値が null の列) と、本体は InitializeDataAsync(null) を呼ぶ
            await Files.InitializeDataAsync(null);

            Assert.That(Files.Entries, Is.Empty);
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.GetSubmitData().FieldData, Is.Null);
        }

        [Test]
        public async Task 送った分に追加も削除も無ければ何も変えない()
        {
            //並びだけ変えた値 (足すものも消すものも無い)
            const string reordered = "[{\"fileKey\":\"k2\",\"name\":\"b.txt\",\"size\":\"4\"},{\"fileKey\":\"k1\",\"name\":\"a.txt\",\"size\":\"3\"}]";
            await Files.SetDataAsync(new KintoneAttachmentsFieldData { Value = reordered });
            Assert.That(Files.IsModified, Is.True);

            await AutoSubmitAsync();

            Assert.That(SentValue(), Is.EqualTo(reordered));
            Assert.That(Files.IsModified, Is.True);
            Assert.That(((KintoneAttachmentsFieldData)Files.GetData()!).Value, Is.EqualTo(reordered));
            Assert.That(Files.Entries.Select(e => e.FileKey), Is.EqualTo(new[] { "k2", "k1" }));
        }
    }
}
