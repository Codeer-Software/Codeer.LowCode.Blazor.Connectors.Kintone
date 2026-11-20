using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Match;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>
    /// アプリの定義の保存ファイル。デザイナがプロジェクトのサーバー専用ファイルに書き、デザインと一緒に配られ (App.zip)、
    /// デザイナも Web アプリもそこからアプリの定義を読む (kintone に取りに行かない)。
    /// </summary>
    [NonParallelizable]
    public class KintoneMetadataFileTest
    {
        FakeKintone _fake = null!;
        KintoneConnection _connection = null!;
        string _directory = null!;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeKintone();
            TestMetadata.AddForms(_fake);
            _connection = TestConnections.Create(_fake);
            _directory = Path.Combine(Path.GetTempPath(), "kintone-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            _fake.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        static string FileName => KintoneMetadataFile.GetFileName(TestConnections.DataSourceName);

        string ReadText() => Encoding.UTF8.GetString(DesignDataFileManager.ReadProjectServerFile(_directory, FileName)!);

        void WriteText(string text) => DesignDataFileManager.WriteProjectServerFile(_directory, FileName, Encoding.UTF8.GetBytes(text));

        List<KintoneTableDefinition> Load() => KintoneMetadataFile.FromProject(_directory).Load(TestConnections.DataSourceName);

        [Test]
        public void ファイル名はOSに依らない決まりでデータソース名から決まる()
        {
            Assert.That(FileName, Is.EqualTo("kintone.KT.metadata.json"));
            //英数字・_・-・. 以外は _ (Windows でだけファイル名に使えない文字も、Linux でだけ使えない文字も同じ扱い)
            Assert.That(KintoneMetadataFile.GetFileName("a/b:c*d?e\\f g"), Is.EqualTo("kintone.a_b_c_d_e_f_g.metadata.json"));
            Assert.That(KintoneMetadataFile.GetFileName("顧客_1-2.x"), Is.EqualTo("kintone.顧客_1-2.x.metadata.json"));
        }

        [Test]
        public async Task 保存して読み直すと同じ定義()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            Assert.That(File.Exists(Path.Combine(_directory, FileName)), Is.False, "プロジェクトの直下には書かない");
            var text = ReadText();
            Assert.That(text, Does.Contain("\"Kind\": \"Number\"").And.Contain("顧客 名").And.Contain("\"FieldType\": \"SUBTABLE\""));
            //接続の情報 (URL・アプリ ID・トークン) は入らない
            Assert.That(text, Does.Not.Contain(TestConnections.TokenA).And.Not.Contain("example.cybozu.com").And.Not.Contain("\"AppId\""));

            var expected = await _connection.GetTableDefinitionsAsync();
            var loaded = Load();
            //表の順は設定 (Apps) の順
            Assert.That(loaded.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            foreach (var table in expected)
            {
                var other = loaded.Single(e => e.Name == table.Name);
                Assert.That(other.KeyColumns, Is.EqualTo(table.KeyColumns));
                Assert.That(other.Columns.Select(e => (e.Name, e.Kind, e.FieldType, e.Label, e.IsReadOnly, e.IsRequired, e.IsUnique, e.IsLookup)),
                    Is.EqualTo(table.Columns.Select(e => (e.Name, e.Kind, e.FieldType, e.Label, e.IsReadOnly, e.IsRequired, e.IsUnique, e.IsLookup))));
                Assert.That(other.Columns.Select(e => e.Options), Is.EqualTo(table.Columns.Select(e => e.Options)));
            }
            var lines = loaded.Single(e => e.Name == "Order").FindColumn("Lines")!;
            Assert.That(lines.SubFields!.Select(e => (e.Name, e.Kind, e.IsReadOnly)),
                Is.EqualTo(expected.Single(e => e.Name == "Order").FindColumn("Lines")!.SubFields!.Select(e => (e.Name, e.Kind, e.IsReadOnly))));
            Assert.That(loaded.Single(e => e.Name == "Customer").FindColumn("Rank")!.Options, Is.EqualTo(new[] { "A", "B", "C" }));
        }

        [Test]
        public async Task 更新はフォームの設定を読み直しプロジェクトのファイルの変化は次の読み出しから()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);
            Assert.That(Load().Single(e => e.Name == "Region").FindColumn("Added"), Is.Null);
            _fake.Forms[TestMetadata.RegionApp]["Added"] = JsonNode.Parse("""{"type":"NUMBER","code":"Added","label":"足した"}""");
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            //アプリごとに 1 回ずつ × 2 回
            var forms = _fake.Requests.Where(e => e.IsForm).Select(e => e.QueryParam("app")).ToList();
            Assert.That(forms, Is.EqualTo(new[] { "11", "12", "13", "14", "11", "12", "13", "14" }));
            Assert.That(Load().Single(e => e.Name == "Region").FindColumn("Added"), Is.Not.Null);
            //接続の定義も読み直したものになる
            Assert.That((await _connection.GetTableDefinitionAsync("Region")).FindColumn("Added"), Is.Not.Null);
        }

        [Test]
        public void ファイルが無ければ空()
            => Assert.That(Load(), Is.Empty);

        [Test]
        public void kintoneに届かなければファイルを変えない()
        {
            WriteText("""{"Tables":[]}""");
            _fake.Override(e => e.IsForm, _ => FakeKintone.Error(HttpStatusCode.Unauthorized, "CB_WA01", "Password authentication failed."));
            Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory));
            Assert.That(ReadText(), Is.EqualTo("""{"Tables":[]}"""));
        }

        [Test]
        public void アプリの1つが読めなければ更新は失敗しファイルを作らない()
        {
            _fake.Forms.Remove(TestMetadata.OrderApp);
            var e = Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotFound));
            Assert.That(DesignDataFileManager.ReadProjectServerFile(_directory, FileName), Is.Null);
        }

        [Test]
        public void 壊れたファイルは読めないとエラー()
        {
            WriteText("not json");
            var e = Assert.Throws<KintoneException>(() => Load())!;
            Assert.That(e.Message, Does.Contain("cannot be read").And.Contain("Update the kintone metadata again"));
        }

        [Test]
        public void 書き換えられたファイルは読み直しSQLに書けない名前は外す()
        {
            WriteText("""{"Tables":[{"Name":"A","Columns":[{"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"}]}]}""");
            var first = Load();
            Assert.That(first.Single().Name, Is.EqualTo("A"));
            Assert.That(first.Single().KeyColumns, Is.EqualTo(new[] { "$id" }));

            WriteText("""
                {"Tables":[{"Name":"B","Columns":[
                  {"Name":"X","Kind":"Number","FieldType":"NUMBER","ReadOnly":true,"Unique":true},
                  {"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"},
                  {"Name":"a\"b","Kind":"String","FieldType":"SINGLE_LINE_TEXT"},
                  {"Name":"T","Kind":"Json","FieldType":"SUBTABLE","Fields":[{"Name":"c\"d","Kind":"String"},{"Name":"Q","Kind":"Number","FieldType":"NUMBER"}]}]},
                 {"Name":"bad.name","Columns":[]}]}
                """);
            var second = Load();
            Assert.That(second.Select(e => e.Name), Is.EqualTo(new[] { "B" }));
            Assert.That(second[0].Columns.Select(e => e.Name), Is.EqualTo(new[] { "X", "T" }));
            Assert.That((second[0].Columns[0].Kind, second[0].Columns[0].IsReadOnly, second[0].Columns[0].CanBeRecordKey), Is.EqualTo((KintoneColumnKind.Number, true, true)));
            Assert.That(second[0].Columns[1].SubFields!.Select(e => e.Name), Is.EqualTo(new[] { "Q" }));
            Assert.That(second[0].Columns[1].IsSubtable, Is.True);
        }

        [TestCase("""{"Tables":[null]}""")]
        [TestCase("""{"Tables":null}""")]
        [TestCase("""null""")]
        public void 表がnullのファイルは表の無いものとして読む(string json)
        {
            WriteText(json);
            Assert.That(Load(), Is.Empty);
        }

        [Test]
        public void 列がnullのファイルは列の無いものとして読む()
        {
            WriteText("""
                {"Tables":[
                  {"Name":"A","Columns":null},
                  null,
                  {"Name":"B","Columns":[null,{"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"},null,
                    {"Name":"T","Kind":"Json","FieldType":"SUBTABLE","Fields":[null,{"Name":"Q","Kind":"Number","FieldType":"NUMBER"}]}]}]}
                """);
            var tables = Load();
            Assert.That(tables.Select(e => e.Name), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(tables[0].Columns, Is.Empty);
            Assert.That(tables[0].KeyColumns, Is.EqualTo(new[] { "$id" }));
            Assert.That(tables[1].Columns.Select(e => e.Name), Is.EqualTo(new[] { "X", "T" }));
            Assert.That(tables[1].Columns[1].SubFields!.Select(e => e.Name), Is.EqualTo(new[] { "Q" }));
        }

        [Test]
        public void 同じ名前の表が2つあれば先のものだけ()
        {
            WriteText("""
                {"Tables":[
                  {"Name":"A","Columns":[{"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"}]},
                  {"Name":"B","Columns":[]},
                  {"Name":"A","Columns":[{"Name":"Y","Kind":"Number","FieldType":"NUMBER"}]}]}
                """);
            var tables = Load();
            Assert.That(tables.Select(e => e.Name), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(tables[0].Columns.Select(e => e.Name), Is.EqualTo(new[] { "X" }));
        }

        [Test]
        public async Task 空のフォルダの指定は今のフォルダ()
        {
            //今のフォルダをこのテストの一時フォルダにして確かめる (読むだけ。今のフォルダにファイルを作らない)
            var original = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(_directory);
                Assert.That(KintoneMetadataFile.FromProject("").Load(TestConnections.DataSourceName), Is.Empty);
                Assert.That(KintoneMetadataFile.FromDeployedDesign("").Load(TestConnections.DataSourceName), Is.Empty);

                //今のフォルダのプロジェクトのファイル・App.zip を読む
                WriteText("""{"Tables":[{"Name":"A","Columns":[]}]}""");
                Assert.That(KintoneMetadataFile.FromProject("").Load(TestConnections.DataSourceName).Select(e => e.Name), Is.EqualTo(new[] { "A" }));
                var deploy = Deploy(await WriteProjectAsync(), new DateTime(2026, 1, 1, 10, 0, 0));
                Directory.SetCurrentDirectory(deploy);
                Assert.That(KintoneMetadataFile.FromDeployedDesign("").Load(TestConnections.DataSourceName).Select(e => e.Name),
                    Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            }
            finally
            {
                Directory.SetCurrentDirectory(original);
            }
        }

        // ---------------------------------------------------------------- プロジェクトのファイルを使うアクセサ (デザイナ)

        [Test]
        public async Task プロジェクトのファイルを指定したアクセサはkintoneに取りに行かない()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            using var designer = new KintoneHarness(metadataFile: KintoneMetadataFile.FromProject(_directory));
            var customer = (await designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "Customer"))!;
            Assert.That(customer.Columns.Select(e => e.Name), Does.Contain("CreditLimit"));
            Assert.That((await designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!.Select(e => e.Name),
                Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            Assert.That(designer.Fake.Requests, Is.Empty);
        }

        [Test]
        public async Task プロジェクトにファイルが無ければアプリは無い()
        {
            using var designer = new KintoneHarness(metadataFile: KintoneMetadataFile.FromProject(_directory));
            Assert.That(await designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName), Is.Empty);
            var e = Assert.ThrowsAsync<KintoneException>(() => designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "Customer"))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotFound));
            Assert.That(e.Message, Does.Contain("Update the kintone metadata (Tools menu of the designer)"));
            Assert.That(designer.Fake.Requests, Is.Empty);
        }

        [Test]
        public async Task 表の一覧はファイルにあって接続のAppsにもあるアプリ()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            using var designer = new KintoneHarness(edit: s => s.Apps.RemoveAll(e => e.Name == "Country"), metadataFile: KintoneMetadataFile.FromProject(_directory));
            Assert.That((await designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!.Select(e => e.Name),
                Is.EqualTo(new[] { "Customer", "Region", "Order" }));
            var e = Assert.ThrowsAsync<KintoneException>(() => designer.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "Country"))!;
            Assert.That(e.Message, Does.Contain("is not in Apps of the connection string"));
        }

        // ---------------------------------------------------------------- 配られたデザイン (App.zip。Web アプリ)

        //プロジェクトのフォルダを App.zip にして deploy に置く (デザイナのデプロイと同じく、サーバー専用ファイルは ServerFiles/ の下に入る)
        string Deploy(string project, DateTime lastWriteTime)
        {
            var deploy = Path.Combine(_directory, "deploy");
            Directory.CreateDirectory(deploy);
            var temporary = Path.Combine(_directory, "App.zip.tmp");
            if (File.Exists(temporary)) File.Delete(temporary);
            ZipFile.CreateFromDirectory(project, temporary);
            var zip = Path.Combine(deploy, "App.zip");
            File.Move(temporary, zip, true);
            File.SetLastWriteTime(zip, lastWriteTime);
            return deploy;
        }

        async Task<string> WriteProjectAsync(Action<KintoneConnectionSettings>? edit = null)
        {
            var project = Path.Combine(_directory, "project");
            if (Directory.Exists(project)) Directory.Delete(project, true);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "app.clprj"), "{}");
            using var fake = new FakeKintone();
            TestMetadata.AddForms(fake);
            await KintoneMetadataFile.UpdateAsync(TestConnections.Create(fake, edit: edit), project);
            return project;
        }

        [Test]
        public async Task 配られたデザインのファイルから読み実行時にフォームの設定を取りに行かない()
        {
            var deploy = Deploy(await WriteProjectAsync(), new DateTime(2026, 1, 1, 10, 0, 0));

            using var h = new KintoneHarness(metadataFile: KintoneMetadataFile.FromDeployedDesign(deploy));
            h.Fake.Records[TestMetadata.CustomerApp] = new() { FakeKintone.Record(1, 3, ("Name", "SINGLE_LINE_TEXT", "アルファ")) };
            var list = await h.IO.GetListAsync(new SearchCondition("Customer"), 0);

            Assert.That(list.Items, Has.Count.EqualTo(1));
            Assert.That(h.Fake.Requests.Where(e => e.IsForm), Is.Empty);
            Assert.That(h.Fake.ReadRequests, Is.Not.Empty);
        }

        [Test]
        public async Task 配られたデザインに無いアプリはエラーでフォームの設定を取りに行かない()
        {
            var project = Path.Combine(_directory, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "app.clprj"), "{}");
            var deploy = Deploy(project, new DateTime(2026, 1, 1, 10, 0, 0));

            using var h = new KintoneHarness(metadataFile: KintoneMetadataFile.FromDeployedDesign(deploy));
            var e = Assert.ThrowsAsync<KintoneException>(() => h.IO.GetListAsync(new SearchCondition("Customer"), 0))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotFound));
            Assert.That(e.Message, Does.Contain("deploy the design again"));
            Assert.That(h.Fake.Requests, Is.Empty);
        }

        [Test]
        public async Task AppZipが無ければアプリは無い()
        {
            using var h = new KintoneHarness(metadataFile: KintoneMetadataFile.FromDeployedDesign(_directory));
            Assert.That(await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName), Is.Empty);
            Assert.That(h.Fake.Requests, Is.Empty);
        }

        [Test]
        public async Task AppZipが差し替わると読み直し同じAppZipなら開き直さない()
        {
            //初めは Country の無い定義 (接続の Apps から Country を外して書き出す)
            var deploy = Deploy(await WriteProjectAsync(s => s.Apps.RemoveAll(e => e.Name == "Country")), new DateTime(2026, 1, 1, 10, 0, 0));
            var file = KintoneMetadataFile.FromDeployedDesign(deploy);
            var first = file.Load(TestConnections.DataSourceName);
            Assert.That(first.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Order" }));

            //同じ App.zip のままなら読み直さない (別のインスタンスでも同じものを返す)
            Assert.That(KintoneMetadataFile.FromDeployedDesign(deploy).Load(TestConnections.DataSourceName), Is.SameAs(first));

            Deploy(await WriteProjectAsync(), new DateTime(2026, 1, 1, 11, 0, 0));
            Assert.That(file.Load(TestConnections.DataSourceName).Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
        }

        [Test]
        public async Task アプリIDは接続文字列のAppsのものを使う()
        {
            //デザイナでは Customer = 11 で書き出し、本番では同じ表名が別のアプリ ID
            var deploy = Deploy(await WriteProjectAsync(), new DateTime(2026, 1, 1, 10, 0, 0));
            using var h = new KintoneHarness(edit: s => s.Apps.Single(e => e.Name == "Customer").AppId = 99,
                metadataFile: KintoneMetadataFile.FromDeployedDesign(deploy));
            h.Fake.Records[99] = new() { FakeKintone.Record(1, 3, ("Name", "SINGLE_LINE_TEXT", "アルファ")) };

            var list = await h.IO.GetListAsync(new SearchCondition("Customer"), 0);

            Assert.That(list.Items, Has.Count.EqualTo(1));
            Assert.That(h.Fake.ReadRequests.Select(e => e.App).Distinct(), Is.EqualTo(new long?[] { 99 }));
            Assert.That(h.Fake.Requests.Where(e => e.IsForm), Is.Empty);
        }
    }
}
