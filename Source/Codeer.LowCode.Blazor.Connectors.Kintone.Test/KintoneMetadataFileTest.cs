using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using System.Net;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
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
            KintoneMetadataFile.AfterTemporaryFileWritten = null;
            _fake.Dispose();
            Directory.Delete(_directory, true);
        }

        string FilePath => KintoneMetadataFile.GetPath(_directory, TestConnections.DataSourceName);

        [Test]
        public void ファイル名()
        {
            Assert.That(Path.GetFileName(FilePath), Is.EqualTo("kintone.KT.metadata.json"));
            Assert.That(Path.GetFileName(KintoneMetadataFile.GetPath(_directory, "a/b:c")), Is.EqualTo("kintone.a_b_c.metadata.json"));
        }

        [Test]
        public async Task 保存して読み直すと同じ定義()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            var text = File.ReadAllText(FilePath);
            Assert.That(text, Does.Contain("\"Kind\": \"Number\"").And.Contain("顧客 名").And.Contain("\"FieldType\": \"SUBTABLE\""));
            //接続の情報 (URL・アプリ ID・トークン) は入らない
            Assert.That(text, Does.Not.Contain(TestConnections.TokenA).And.Not.Contain("example.cybozu.com").And.Not.Contain("\"AppId\""));
            //一時ファイルは残らない
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));

            var expected = await _connection.GetTableDefinitionsAsync();
            var loaded = KintoneMetadataFile.Load(_directory, TestConnections.DataSourceName)!;
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
        public async Task 更新はフォームの設定を読み直す()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);
            _fake.Forms[TestMetadata.RegionApp]["Added"] = JsonNode.Parse("""{"type":"NUMBER","code":"Added","label":"足した"}""");
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);

            //アプリごとに 1 回ずつ × 2 回
            var forms = _fake.Requests.Where(e => e.IsForm).Select(e => e.QueryParam("app")).ToList();
            Assert.That(forms, Is.EqualTo(new[] { "11", "12", "13", "14", "11", "12", "13", "14" }));
            Assert.That(KintoneMetadataFile.Load(_directory, TestConnections.DataSourceName)!.Single(e => e.Name == "Region").FindColumn("Added"), Is.Not.Null);
            //接続の定義も読み直したものになる
            Assert.That((await _connection.GetTableDefinitionAsync("Region")).FindColumn("Added"), Is.Not.Null);
        }

        [Test]
        public void ファイルが無ければnull()
            => Assert.That(KintoneMetadataFile.Load(_directory, "None"), Is.Null);

        [Test]
        public async Task 一時ファイルが壊れたら前のファイルを残す()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);
            var before = File.ReadAllText(FilePath);

            KintoneMetadataFile.AfterTemporaryFileWritten = path => File.WriteAllText(path, "{ broken");
            Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory));

            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task 一時ファイルの中身が足りなければ前のファイルを残す()
        {
            await KintoneMetadataFile.UpdateAsync(_connection, _directory);
            var before = File.ReadAllText(FilePath);

            KintoneMetadataFile.AfterTemporaryFileWritten = path => File.WriteAllText(path, """{"Tables":[]}""");
            var e = Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory))!;
            Assert.That(e.Message, Does.Contain("could not be written correctly"));
            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(before));
        }

        [Test]
        public void kintoneに届かなければファイルを作らない()
        {
            _fake.Override(e => e.IsForm, _ => FakeKintone.Error(HttpStatusCode.Unauthorized, "CB_WA01", "Password authentication failed."));
            Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void アプリの1つが読めなければ更新は失敗する()
        {
            _fake.Forms.Remove(TestMetadata.OrderApp);
            var e = Assert.ThrowsAsync<KintoneException>(() => KintoneMetadataFile.UpdateAsync(_connection, _directory))!;
            Assert.That(e.Kind, Is.EqualTo(KintoneErrorKind.NotFound));
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void 壊れたファイルは読めないとエラー()
        {
            File.WriteAllText(FilePath, "not json");
            var e = Assert.Throws<KintoneException>(() => KintoneMetadataFile.Load(_directory, TestConnections.DataSourceName))!;
            Assert.That(e.Message, Does.Contain("cannot be read").And.Contain("Update the kintone metadata again"));
        }

        [Test]
        public void 書き換えられたファイルは読み直しSQLに書けない名前は外す()
        {
            File.WriteAllText(FilePath, """{"Tables":[{"Name":"A","Columns":[{"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"}]}]}""");
            var first = KintoneMetadataFile.Load(_directory, TestConnections.DataSourceName)!;
            Assert.That(first.Single().Name, Is.EqualTo("A"));
            Assert.That(first.Single().KeyColumns, Is.EqualTo(new[] { "$id" }));

            File.WriteAllText(FilePath, """
                {"Tables":[{"Name":"B","Columns":[
                  {"Name":"X","Kind":"Number","FieldType":"NUMBER","ReadOnly":true,"Unique":true},
                  {"Name":"X","Kind":"String","FieldType":"SINGLE_LINE_TEXT"},
                  {"Name":"a\"b","Kind":"String","FieldType":"SINGLE_LINE_TEXT"},
                  {"Name":"T","Kind":"Json","FieldType":"SUBTABLE","Fields":[{"Name":"c\"d","Kind":"String"},{"Name":"Q","Kind":"Number","FieldType":"NUMBER"}]}]},
                 {"Name":"bad.name","Columns":[]}]}
                """);
            File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddMinutes(1));
            var second = KintoneMetadataFile.Load(_directory, TestConnections.DataSourceName)!;
            Assert.That(second.Select(e => e.Name), Is.EqualTo(new[] { "B" }));
            Assert.That(second[0].Columns.Select(e => e.Name), Is.EqualTo(new[] { "X", "T" }));
            Assert.That((second[0].Columns[0].Kind, second[0].Columns[0].IsReadOnly, second[0].Columns[0].CanBeRecordKey), Is.EqualTo((KintoneColumnKind.Number, true, true)));
            Assert.That(second[0].Columns[1].SubFields!.Select(e => e.Name), Is.EqualTo(new[] { "Q" }));
            Assert.That(second[0].Columns[1].IsSubtable, Is.True);
        }
    }
}
