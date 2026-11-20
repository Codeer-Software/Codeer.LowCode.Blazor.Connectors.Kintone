using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test
{
    /// <summary>本体の ModuleDataIO を通して (本体が実際に組む SQL を読み替えて)、kintone への要求と戻りを確かめる。</summary>
    [SetUICulture("en-US")]
    [NonParallelizable]
    public class KintoneDbAccessorTest
    {
        KintoneHarness _h = null!;

        [SetUp]
        public void SetUp() => _h = new KintoneHarness();

        [TearDown]
        public void TearDown() => _h.Dispose();

        static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        static string Text(JsonNode? node) => node!.ToJsonString(Relaxed);

        static FieldValueMatchCondition Value(string variable, MatchComparison comparison, object? value)
            => new() { SearchTargetVariable = variable, Comparison = comparison, Value = value == null ? new NullValue() : MultiTypeValue.Create(value) };

        static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        Task<List<ModuleSubmitResult>> SubmitAsync(ModuleSubmitData data) => _h.IO.SubmitWithTransactionAsync(new() { data });

        List<JsonObject> Customers(int count) => Enumerable.Range(1, count).Select(i => KintoneHarness.CustomerRecord(i, $"n{i}")).ToList();

        // ---------------------------------------------------------------- 一覧・件数

        [Test]
        public async Task 条件と並びとページつきの一覧と件数()
        {
            var rows = Customers(4);
            rows.Add(FakeKintone.Record(5, 7,
                ("Code", "SINGLE_LINE_TEXT", "C5"), ("Name", "SINGLE_LINE_TEXT", "Acme"), ("CreditLimit", "NUMBER", "1234.5"), ("IsActive", "NUMBER", "1"),
                ("StartDate", "DATE", "2026-01-01"), ("OpenTime", "TIME", "09:30"), ("LastContact", "DATETIME", "2026-03-04T05:06:07Z"),
                ("Rank", "DROP_DOWN", "A"), ("RegionCode", "SINGLE_LINE_TEXT", "R1")));
            rows.Add(FakeKintone.Record(6, 1, ("Code", "SINGLE_LINE_TEXT", "C6"), ("Name", "SINGLE_LINE_TEXT", "Beta"), ("IsActive", "NUMBER", "0"), ("CreditLimit", "NUMBER", "")));
            _h.Fake.Records[TestMetadata.CustomerApp] = rows;
            _h.Fake.Records[TestMetadata.RegionApp] = new() { KintoneHarness.RegionRecord(1, "R1", "East") };

            var condition = new SearchCondition("Customer") { LimitCount = 2 };
            condition.Condition = MultiMatchCondition.And(
                Value("Name.Value", MatchComparison.Like, "ab%c"),
                Value("IsActive.Value", MatchComparison.Equal, false),
                Value("CreditLimit.Value", MatchComparison.GreaterThanOrEqual, 10m),
                Value("StartDate.Value", MatchComparison.LessThan, new DateOnly(2026, 1, 2)));
            condition.SortConditions.Add(new SortCondition { Variable = "Name.Value", IsDescending = true });

            //ページは 0 始まり。3 ページ目 = offset 4 (偽物は条件で絞らないので、5 件目と 6 件目が返る)
            var result = await _h.IO.GetListAsync(condition, 2);

            const string filter = "Name like \"ab%c\" and IsActive = 0 and (CreditLimit >= 10 and CreditLimit != \"\") and (StartDate < \"2026-01-02\" and StartDate != \"\")";
            var reads = _h.Fake.ReadRequests;
            Assert.That(reads.Select(e => e.App), Is.EqualTo(new long?[] { TestMetadata.CustomerApp, TestMetadata.CustomerApp, TestMetadata.RegionApp }));

            //件数 (1 件だけ読んで totalCount)
            Assert.That(reads[0].WantsTotalCount, Is.True);
            Assert.That(reads[0].Query, Is.EqualTo(filter + " limit 1"));
            Assert.That(reads[0].Fields, Is.EqualTo(new[] { "$id" }));

            //一覧 (並びの最後に $id・offset はページ × 件数)
            Assert.That(reads[1].WantsTotalCount, Is.False);
            Assert.That(reads[1].Query, Is.EqualTo(filter + " order by Name desc, $id asc limit 2 offset 4"));
            Assert.That(reads[1].Fields, Is.EqualTo(new[] { "$id", "Code", "Name", "Note", "CreditLimit", "IsActive", "StartDate", "OpenTime", "LastContact", "Rank", "Tags", "RegionCode" }));
            Assert.That(reads[1].Header("X-HTTP-Method-Override"), Is.EqualTo("GET"));

            //結合先 (LinkField の表示名) は値を集めて読む
            Assert.That(reads[2].Query, Is.EqualTo("Code in (\"R1\") order by $id asc limit 500"));
            Assert.That(reads[2].Fields, Is.EqualTo(new[] { "Code", "RegionName", "$id" }));

            Assert.That(result.TotalCount, Is.EqualTo(6));
            Assert.That(result.PageCount, Is.EqualTo(3));
            Assert.That(result.Items, Has.Count.EqualTo(2));

            var first = result.Items[0].Fields;
            Assert.That(((IdFieldData)first["Id"]).Value, Is.EqualTo("5"));
            Assert.That(((TextFieldData)first["Name"]).Value, Is.EqualTo("Acme"));
            Assert.That(((NumberFieldData)first["CreditLimit"]).Value, Is.EqualTo(1234.5m));
            Assert.That(((BooleanFieldData)first["IsActive"]).Value, Is.True);
            Assert.That(((DateFieldData)first["StartDate"]).Value, Is.EqualTo(new DateOnly(2026, 1, 1)));
            Assert.That(((TimeFieldData)first["OpenTime"]).Value, Is.EqualTo(new TimeOnly(9, 30)));
            Assert.That(((DateTimeFieldData)first["LastContact"]).Value, Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            Assert.That(((TextFieldData)first["Rank"]).Value, Is.EqualTo("A"));
            var link = (LinkFieldData)first["Region"];
            Assert.That(link.Value, Is.EqualTo("R1"));
            Assert.That(link.DisplayText, Is.EqualTo("East"));

            var second = result.Items[1].Fields;
            Assert.That(((BooleanFieldData)second["IsActive"]).Value, Is.False);
            Assert.That(Field<NumberFieldData>(result.Items[1], "CreditLimit")?.Value, Is.Null);
            Assert.That(Field<LinkFieldData>(result.Items[1], "Region")?.Value, Is.Null.Or.Empty);

            //kintone のデータソースは包んだ IDbAccessor に渡らない
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 本体のSQLのIDと版の列は引用符つきのまま通る()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A", revision: 3) };
            var result = await _h.IO.GetListAsync(new SearchCondition("CustomerLocked"), 0);

            var select = _h.Sql.Sqls.Single(e => e.StartsWith("select \"CustomerLocked\".", StringComparison.Ordinal) || e.Contains("from \"Customer\""));
            Assert.That(select, Does.Contain("\"Customer\".\"$id\"").And.Contain("\"Customer\".\"$revision\""));
            Assert.That(_h.ListReads(TestMetadata.CustomerApp).Single().Fields, Is.EquivalentTo(new[] { "$id", "Name", "$revision" }));
            var row = result.Items.Single();
            Assert.That(Field<IdFieldData>(row, "Id")!.Value, Is.EqualTo("5"));
            Assert.That(((DecimalValue)Field<OptimisticLockingFieldData>(row, SystemFieldNames.OptimisticLocking)!.Value).Value, Is.EqualTo(3m));
        }

        [Test]
        public async Task 並びの無い全部の読み出しはIDの順に前の最後のIDより大きいで読み進める()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = Customers(1100);
            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName,
                """select "Customer"."$id" as "id","Customer"."Name" as "name" from "Customer" where "Customer"."Name" <> @p0""",
                new() { ["@p0"] = new ParamAndRawDbTypeName { Value = "x" } });

            Assert.That(rows, Has.Count.EqualTo(1100));
            Assert.That(rows[1099]["id"], Is.EqualTo(1100m));
            Assert.That(_h.ListReads(TestMetadata.CustomerApp).Select(e => e.Query), Is.EqualTo(new[]
            {
                "(Name != \"x\" and Name != \"\") order by $id asc limit 500",
                "((Name != \"x\" and Name != \"\")) and $id > 500 order by $id asc limit 500",
                "((Name != \"x\" and Name != \"\")) and $id > 1000 order by $id asc limit 500",
            }));
        }

        [Test]
        public async Task 並びつきで件数を絞らない一覧はoffsetで読み進め並びの最後にIDを足す()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = Customers(600);
            var condition = new SearchCondition("Customer") { SelectFields = new() { "Id", "Name" } };
            condition.SortConditions.Add(new SortCondition { Variable = "Name.Value" });

            var result = await _h.IO.GetListAsync(condition, 0);

            Assert.That(result.Items, Has.Count.EqualTo(600));
            Assert.That(_h.ListReads(TestMetadata.CustomerApp).Select(e => e.Query), Is.EqualTo(new[]
            {
                "order by Name asc, $id asc limit 500",
                "order by Name asc, $id asc limit 500 offset 500",
            }));
        }

        [Test]
        public void 件数を絞らない一覧がMaxRowsを超えたらエラー()
        {
            _h.Dispose();
            _h = new KintoneHarness(e => e.MaxRows = 5);
            _h.Fake.Records[TestMetadata.CustomerApp] = Customers(9);

            Assert.That(async () => await _h.IO.GetListAsync(new SearchCondition("Customer"), 0),
                Throws.InstanceOf<Exception>().With.Message.Contains("MaxRows"));
            //黙って切り捨てない
            Assert.That(_h.ListReads(TestMetadata.CustomerApp), Has.Count.EqualTo(1));
        }

        [Test]
        public void offsetが10000を超えるページはエラーで一覧を読まない()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = Customers(3);
            Assert.That(async () => await _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = 100 }, 101),
                Throws.InstanceOf<Exception>().With.Message.Contains("10000"));
            Assert.That(_h.ListReads(TestMetadata.CustomerApp), Is.Empty);
        }

        [Test]
        public async Task offsetがちょうど10000のページは読める()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = Customers(3);
            await _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = 100 }, 100);
            Assert.That(_h.ListReads(TestMetadata.CustomerApp).Single().Query, Does.EndWith("limit 100 offset 10000"));
        }

        [Test]
        public async Task 一覧の検索の各形()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(1, "A") };

            async Task<string?> FilterOf(MatchConditionBase condition)
            {
                _h.Fake.ClearRequests();
                await _h.IO.GetListAsync(new SearchCondition("Customer") { Condition = condition, SelectFields = new() { "Id", "Name" }, LimitCount = 10 }, 0);
                var query = _h.ListReads(TestMetadata.CustomerApp).Single().Query!;
                var index = query.IndexOf("order by", StringComparison.Ordinal);
                return index <= 0 ? null : query[..index].TrimEnd();
            }

            Assert.That(await FilterOf(Value("Name.Value", MatchComparison.Equal, "A")), Is.EqualTo("Name = \"A\""));
            Assert.That(await FilterOf(Value("Name.Value", MatchComparison.NotEqual, "A")), Is.EqualTo("(Name != \"A\" and Name != \"\")"));
            Assert.That(await FilterOf(Value("CreditLimit.Value", MatchComparison.LessThan, 1m)), Is.EqualTo("(CreditLimit < 1 and CreditLimit != \"\")"));
            Assert.That(await FilterOf(Value("CreditLimit.Value", MatchComparison.LessThanOrEqual, 1.50m)), Is.EqualTo("(CreditLimit <= 1.5 and CreditLimit != \"\")"));
            Assert.That(await FilterOf(Value("CreditLimit.Value", MatchComparison.GreaterThan, 1m)), Is.EqualTo("(CreditLimit > 1 and CreditLimit != \"\")"));
            Assert.That(await FilterOf(Value("Name.Value", MatchComparison.Equal, null)), Is.EqualTo("Name = \"\""));
            Assert.That(await FilterOf(Value("Name.Value", MatchComparison.NotEqual, null)), Is.EqualTo("Name != \"\""));
            Assert.That(await FilterOf(Value("Note.Value", MatchComparison.Equal, null)), Is.EqualTo("Note is empty"));
            Assert.That(await FilterOf(Value("Rank.Value", MatchComparison.Equal, "A")), Is.EqualTo("Rank in (\"A\")"));
            Assert.That(await FilterOf(Value("Rank.Value", MatchComparison.Equal, null)), Is.EqualTo("Rank in (\"\")"));
            Assert.That(await FilterOf(Value("LastContact.Value", MatchComparison.GreaterThanOrEqual, new DateTime(2026, 3, 4, 5, 6, 7))),
                Is.EqualTo("(LastContact >= \"2026-03-04T05:06:07Z\" and LastContact != \"\")"));
            Assert.That(await FilterOf(Value("OpenTime.Value", MatchComparison.Equal, new TimeOnly(9, 30))), Is.EqualTo("OpenTime = \"09:30\""));
            Assert.That(await FilterOf(Value("Region.Value", MatchComparison.In, new List<string> { "R1", "R2" })),
                Is.EqualTo("RegionCode = \"R1\" or RegionCode = \"R2\""));
            Assert.That(await FilterOf(Value("Name.Value", MatchComparison.Like, "O\"Ne_il\\")), Is.EqualTo("Name like \"O\\\"Ne_il\\\\\""));
            Assert.That(await FilterOf(Value("Id.Value", MatchComparison.Equal, "5")), Is.EqualTo("$id = 5"));

            var or = new MultiMatchCondition { IsOrMatch = true };
            or.Children.Add(Value("Name.Value", MatchComparison.Equal, "A"));
            or.Children.Add(Value("Name.Value", MatchComparison.Equal, "B"));
            var not = new MultiMatchCondition { IsNot = true };
            not.Children.Add(Value("CreditLimit.Value", MatchComparison.Equal, 0m));
            Assert.That(await FilterOf(MultiMatchCondition.And(or, not)), Is.EqualTo("(Name = \"A\" or Name = \"B\") and (CreditLimit != 0 and CreditLimit != \"\")"));
        }

        [Test]
        public async Task 必ず偽の条件は要求を送らずに0行()
        {
            var result = await _h.IO.GetListAsync(new SearchCondition("Customer") { Condition = Value("Region.Value", MatchComparison.In, new List<string>()), LimitCount = 10 }, 0);
            Assert.That(result.Items, Is.Empty);
            Assert.That(result.TotalCount, Is.EqualTo(0));
            Assert.That(_h.Fake.DataRequests, Is.Empty);
        }

        [Test]
        public void 読めない値はKintoneExceptionで項目の名前を入れる()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { FakeKintone.Record(1, 1, ("Name", "SINGLE_LINE_TEXT", "A"), ("StartDate", "DATE", "01/02/2026")) };
            var e = Assert.ThrowsAsync<KintoneException>(() => _h.IO.GetListAsync(new SearchCondition("Customer"), 0))!;
            Assert.That(e.Message, Does.Contain("StartDate").And.Contain("01/02/2026"));
        }

        // ---------------------------------------------------------------- 結合

        [Test]
        public async Task リンク先のリンクは2段の結合で結合元の値を集めて読む()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new()
            {
                KintoneHarness.CustomerRecord(1, "A", "R1"),
                KintoneHarness.CustomerRecord(2, "B", "R2"),
                KintoneHarness.CustomerRecord(3, "C", "R1"),
                KintoneHarness.CustomerRecord(4, "D"),
            };
            _h.Fake.Records[TestMetadata.RegionApp] = new() { KintoneHarness.RegionRecord(11, "R1", "East", 1), KintoneHarness.RegionRecord(12, "R2", "West") };
            _h.Fake.Records[TestMetadata.CountryApp] = new() { KintoneHarness.CountryRecord(1, "日本") };

            var result = await _h.IO.GetListAsync(new SearchCondition("CustomerWithCountry"), 0);

            Assert.That(result.Items.Select(e => Field<LinkFieldData>(e, "Region")?.DisplayText), Is.EqualTo(new[] { "East", "West", "East", null }));
            Assert.That(result.Items.Select(e => Field<TextFieldData>(e, "Region.Country.Name")?.Value ?? Field<LinkFieldData>(e, "Region.Country")?.DisplayText),
                Is.EqualTo(new[] { "日本", null, "日本", null }));

            var region = _h.ListReads(TestMetadata.RegionApp).Single();
            Assert.That(region.Query, Is.EqualTo("Code in (\"R1\", \"R2\") order by $id asc limit 500"));
            Assert.That(region.Fields, Does.Contain("Code").And.Contain("RegionName").And.Contain("CountryId"));
            var country = _h.ListReads(TestMetadata.CountryApp).Single();
            Assert.That(country.Query, Is.EqualTo("$id in (1) order by $id asc limit 500"));

            //本体の SQL は結合先の $id を引用符つきで書く
            Assert.That(_h.Sql.Sqls.Last(e => e.StartsWith("select", StringComparison.Ordinal)), Does.Match("\"Country_[0-9]+\"\\.\"\\$id\""));
        }

        [Test]
        public async Task 結合先の列の条件は結合先を条件で読んでから主表を値で絞る()
        {
            _h.Fake.Records[TestMetadata.RegionApp] = new() { KintoneHarness.RegionRecord(11, "R1", "East", 1), KintoneHarness.RegionRecord(12, "R3", "Eastern", 1) };
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(1, "A", "R1") };

            //リンク先のリンク (Region.Country) の値 = 地域の CountryId
            var result = await _h.IO.GetListAsync(new SearchCondition("CustomerWithCountry") { Condition = Value("Region.Country.Value", MatchComparison.Equal, "1"), LimitCount = 10 }, 0);

            Assert.That(result.Items, Has.Count.EqualTo(1));
            var regionReads = _h.ListReads(TestMetadata.RegionApp);
            Assert.That(regionReads[0].Query, Is.EqualTo("CountryId = 1 order by $id asc limit 500"));
            Assert.That(regionReads[0].Fields, Is.EqualTo(new[] { "Code", "$id" }));
            var customer = _h.ListReads(TestMetadata.CustomerApp).Single();
            Assert.That(customer.Query, Does.StartWith("RegionCode in (\"R1\", \"R3\") order by"));
        }

        [Test]
        public async Task 日時の列から日付の列への結合は0時の日時を日付として突き合わせる()
        {
            //結合元 = 最終連絡 (日時)、結合先 = 取引開始日 (日付)。PostgreSQL と同じく 0:00 の日時だけがその日と等しい
            _h.Fake.Records[TestMetadata.CustomerApp] = new()
            {
                FakeKintone.Record(1, 1, ("Name", "SINGLE_LINE_TEXT", "A"), ("LastContact", "DATETIME", "2026-04-01T15:00:00Z")),
                FakeKintone.Record(2, 1, ("Name", "SINGLE_LINE_TEXT", "B"), ("LastContact", "DATETIME", "2026-04-01T00:00:00Z")),
                FakeKintone.Record(3, 1, ("Name", "SINGLE_LINE_TEXT", "C"), ("LastContact", "DATETIME", "2026-04-01T09:30:00Z")),
                FakeKintone.Record(4, 1, ("Name", "SINGLE_LINE_TEXT", "D"), ("LastContact", "DATETIME", "")),
                FakeKintone.Record(5, 1, ("Name", "SINGLE_LINE_TEXT", "E"), ("StartDate", "DATE", "2026-04-01")),
            };

            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, """
                select "Customer"."Name" as "name","c_1"."Name" as "joined" from "Customer" left join "Customer" as "c_1" on "Customer"."LastContact"="c_1"."StartDate"
                """, new());

            Assert.That(rows.Select(e => ((string)e["name"], (string?)e["joined"])), Is.EqualTo(new (string, string?)[]
            {
                ("A", null), ("B", "E"), ("C", null), ("D", null), ("E", null),
            }));
            //結合先は日付の値で読む (時刻の入った日時は読む値に入れない)
            Assert.That(_h.ListReads(TestMetadata.CustomerApp).Last().Query, Is.EqualTo("StartDate = \"2026-04-01\" order by $id asc limit 500"));
        }

        // ---------------------------------------------------------------- 追加

        [Test]
        public async Task 追加はPOSTで値をkintoneの形で送り作成されたIDを返す()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["Code"] = new TextFieldData { Value = "C-100" };
            add.Fields["Name"] = new TextFieldData { Value = "新規" };
            add.Fields["Note"] = new TextFieldData { Value = null };
            add.Fields["CreditLimit"] = new NumberFieldData { Value = 12.50m };
            add.Fields["IsActive"] = new BooleanFieldData { Value = true };
            add.Fields["StartDate"] = new DateFieldData { Value = new DateOnly(2026, 3, 4) };
            add.Fields["OpenTime"] = new TimeFieldData { Value = new TimeOnly(9, 30) };
            add.Fields["LastContact"] = new DateTimeFieldData { Value = new DateTime(2026, 3, 4, 5, 6, 7) };
            add.Fields["Region"] = new LinkFieldData { Value = "R1" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            Assert.That(result.DestinationId, Is.EqualTo("101"));
            Assert.That(_h.Sql.Sqls, Has.Some.Match("^insert into \"Customer\".*returning \"\\$id\""));

            var post = _h.Fake.WriteRequests.Single();
            Assert.That(post.IsCreate, Is.True);
            Assert.That(post.App, Is.EqualTo(TestMetadata.CustomerApp));
            var record = post.Json!["record"]!.AsObject();
            //null の値 (Note) は送らない
            Assert.That(record.Select(e => e.Key), Is.EquivalentTo(new[] { "Code", "Name", "CreditLimit", "IsActive", "StartDate", "OpenTime", "LastContact", "RegionCode" }));
            Assert.That(Text(record["Name"]), Is.EqualTo("""{"value":"新規"}"""));
            Assert.That(Text(record["CreditLimit"]), Is.EqualTo("""{"value":"12.5"}"""));
            Assert.That(Text(record["IsActive"]), Is.EqualTo("""{"value":"1"}"""));
            Assert.That(Text(record["StartDate"]), Is.EqualTo("""{"value":"2026-03-04"}"""));
            Assert.That(Text(record["OpenTime"]), Is.EqualTo("""{"value":"09:30"}"""));
            Assert.That(Text(record["LastContact"]), Is.EqualTo("""{"value":"2026-03-04T05:06:07Z"}"""));
            Assert.That(Text(record["RegionCode"]), Is.EqualTo("""{"value":"R1"}"""));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 重複禁止のフィールドがIdのモジュールの追加は送った値を返す()
        {
            var add = new ModuleData { Name = "CustomerByCode" };
            add.Fields["Id"] = new IdFieldData { Value = "C-200" };
            add.Fields["Name"] = new TextFieldData { Value = "X" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerByCode", Id = "C-200", Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            Assert.That(result.DestinationId, Is.EqualTo("C-200"));
            Assert.That(Text(_h.Fake.WriteRequests.Single().Json!["record"]), Is.EqualTo("""{"Code":{"value":"C-200"},"Name":{"value":"X"}}"""));
        }

        [Test]
        public async Task 追加の失敗は結果のExceptionMessageになる()
        {
            _h.Fake.Override(e => e.IsCreate, _ => FakeKintone.Error(HttpStatusCode.BadRequest, "CB_VA01", "入力内容が正しくありません。",
                new JsonObject { ["record.Code.value"] = new JsonObject { ["messages"] = new JsonArray("必須です。") } }));
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Name"] = new TextFieldData { Value = "New" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Does.Contain("[Code: 必須です。]").And.Contain("CB_VA01"));
            Assert.That(_h.Fake.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 読み取り専用の列は追加でnullなら送らず値があればエラー()
        {
            var add = new ModuleData { Name = "CustomerCalc" };
            add.Fields["Name"] = new TextFieldData { Value = "New" };
            add.Fields["Total"] = new NumberFieldData { Value = null };
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerCalc", Add = new() { add } });
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(Text(_h.Fake.WriteRequests.Single().Json!["record"]), Is.EqualTo("""{"Name":{"value":"New"}}"""));

            _h.Fake.ClearRequests();
            add.Fields["Total"] = new NumberFieldData { Value = 5 };
            results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerCalc", Add = new() { add } });
            Assert.That(results.Single().ExceptionMessage, Does.Contain("read-only").And.Contain("Total"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 一括追加は1件ずつPOSTし途中で失敗したら作成済みの件数とキーを返す()
        {
            var threshold = ModuleDataIO.BulkAddThreshold;
            ModuleDataIO.BulkAddThreshold = 1;
            try
            {
                //2 件目で失敗
                _h.Fake.Override(e => e.IsCreate && e.Body.Contains("\"B\""), _ => FakeKintone.Error(HttpStatusCode.BadRequest, "CB_VA01", "Validation failed."));
                var adds = new[] { "A", "B", "C" }.Select(name =>
                {
                    var data = new ModuleData { Name = "Customer" };
                    data.Fields["Name"] = new TextFieldData { Value = name };
                    return data;
                }).ToList();

                var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Add = adds, NoTemporaryIdResolution = true });

                var message = results.Single().ExceptionMessage;
                Assert.That(message, Does.Contain("1 of 3 records had been created (keys: 101)"));
                Assert.That(message, Does.Contain("Validation failed."));
                //失敗したところで止める
                Assert.That(_h.Fake.WriteRequests.Select(e => e.Json!["record"]!["Name"]!["value"]!.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }));
                //本体は複数行の insert を 1 回組む
                Assert.That(_h.Sql.Sqls.Count(e => e.StartsWith("insert", StringComparison.Ordinal)), Is.EqualTo(1));
            }
            finally
            {
                ModuleDataIO.BulkAddThreshold = threshold;
            }
        }

        [Test]
        public async Task 一括追加は全部作れれば成功()
        {
            var threshold = ModuleDataIO.BulkAddThreshold;
            ModuleDataIO.BulkAddThreshold = 1;
            try
            {
                var adds = new[] { "A", "B" }.Select(name =>
                {
                    var data = new ModuleData { Name = "Customer" };
                    data.Fields["Name"] = new TextFieldData { Value = name };
                    data.Fields["CreditLimit"] = new NumberFieldData { Value = 1 };
                    return data;
                }).ToList();

                var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Add = adds, NoTemporaryIdResolution = true });

                Assert.That(results.Single().ExceptionMessage, Is.Empty);
                Assert.That(_h.Fake.WriteRequests.Select(e => Text(e.Json!["record"])), Is.EqualTo(new[]
                {
                    """{"Name":{"value":"A"},"CreditLimit":{"value":"1"}}""",
                    """{"Name":{"value":"B"},"CreditLimit":{"value":"1"}}""",
                }));
            }
            finally
            {
                ModuleDataIO.BulkAddThreshold = threshold;
            }
        }

        // ---------------------------------------------------------------- 更新

        [Test]
        public async Task IDで更新はPUTでIDと変えた項目だけを送る()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "5" };
            update.Fields["Name"] = new TextFieldData { Value = null };
            update.Fields["CreditLimit"] = new NumberFieldData { Value = 0 };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "5", Update = new() { update } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            var put = _h.Fake.DataRequests.Single();
            Assert.That(put.IsUpdate, Is.True);
            Assert.That(put.Body, Is.EqualTo("""{"app":11,"id":"5","record":{"Name":{"value":null},"CreditLimit":{"value":"0"}}}"""));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 重複禁止のフィールドがIdなら更新はupdateKeyで送る()
        {
            var update = new ModuleData { Name = "CustomerByCode" };
            update.Fields["Id"] = new IdFieldData { Value = "C-1" };
            update.Fields["Name"] = new TextFieldData { Value = "X" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerByCode", Id = "C-1", Update = new() { update } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            var put = _h.Fake.DataRequests.Single();
            Assert.That(put.Body, Is.EqualTo("""{"app":11,"updateKey":{"field":"Code","value":"C-1"},"record":{"Name":{"value":"X"}}}"""));
        }

        [Test]
        public async Task Idの値を変える更新はレコードIDを引いてIDで送る()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(7, "A", code: "C-1") };

            var count = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, """update "Customer" set "Code"=@p0,"Name"=@p1 where "Code"=@pid0""",
                new() { ["@p0"] = "C-2", ["@p1"] = "B", ["@pid0"] = "C-1" });

            Assert.That(count, Is.EqualTo(1));
            var requests = _h.Fake.DataRequests;
            Assert.That(requests[0].Query, Is.EqualTo("Code in (\"C-1\") limit 2"));
            Assert.That(requests[0].Fields, Is.EqualTo(new[] { "$id", "Code" }));
            Assert.That(requests[1].Body, Is.EqualTo("""{"app":11,"id":"7","record":{"Code":{"value":"C-2"},"Name":{"value":"B"}}}"""));
        }

        //Id の値で $id を引いたときの応答 (偽物は条件で絞らないので、置いたレコードがそのまま返る)
        static List<JsonObject> FoundByKey(string found) => found switch
        {
            "2件" => new() { KintoneHarness.CustomerRecord(7, "A", code: "C-1"), KintoneHarness.CustomerRecord(8, "B", code: "C-1") },
            "値違い" => new() { KintoneHarness.CustomerRecord(7, "A", code: "C-10") },
            "キーの列なし" => new() { FakeKintone.Record(7, 1, ("Name", "SINGLE_LINE_TEXT", "A")) },
            _ => throw new ArgumentException(found),
        };

        [TestCase("2件")]
        [TestCase("値違い")]
        [TestCase("キーの列なし")]
        public async Task Idの値を変える更新は引いたレコードが1件でキーの値が同じでなければ送らない(string found)
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = FoundByKey(found);

            var count = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, """update "Customer" set "Code"=@p0,"Name"=@p1 where "Code"=@pid0""",
                new() { ["@p0"] = "C-2", ["@p1"] = "B", ["@pid0"] = "C-1" });

            Assert.That(count, Is.EqualTo(0));
            Assert.That(_h.Fake.ReadRequests.Single().Query, Is.EqualTo("Code in (\"C-1\") limit 2"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 同じ値でIdの列を書くのは送らない()
        {
            var count = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, """update "Customer" set "Code"=@p0,"Name"=@p1 where "Code"=@pid0""",
                new() { ["@p0"] = "C-1", ["@p1"] = "B", ["@pid0"] = "C-1" });
            Assert.That(count, Is.EqualTo(1));
            Assert.That(_h.Fake.DataRequests.Single().Body, Is.EqualTo("""{"app":11,"updateKey":{"field":"Code","value":"C-1"},"record":{"Name":{"value":"B"}}}"""));
        }

        [Test]
        public void 重複禁止でないフィールドでは書き込めない()
        {
            var e = Assert.ThrowsAsync<KintoneException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, """update "Customer" set "Name"=@p0 where "Name"=@pid0""",
                new() { ["@p0"] = "B", ["@pid0"] = "A" }))!;
            Assert.That(e.Message, Does.Contain("cannot be identified by field 'Name'"));
            Assert.That(_h.Fake.DataRequests, Is.Empty);
        }

        [Test]
        public async Task 読み取り専用の列の更新はエラー()
        {
            var update = new ModuleData { Name = "CustomerCalc" };
            update.Fields["Id"] = new IdFieldData { Value = "5" };
            update.Fields["Total"] = new NumberFieldData { Value = 1 };
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerCalc", Id = "5", Update = new() { update } });

            Assert.That(results.Single().ExceptionMessage, Does.Contain("read-only"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 無い行の更新は更新の失敗()
        {
            _h.Fake.Override(e => e.IsUpdate, _ => FakeKintone.Error(HttpStatusCode.NotFound, "GAIA_RE01", "The record was not found."));
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "9" };
            update.Fields["Name"] = new TextFieldData { Value = "X" };

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "9", Update = new() { update } })).Single();

            Assert.That(result.ExceptionMessage, Is.EqualTo("Update failed"));
            Assert.That(_h.Fake.WriteRequests, Has.Count.EqualTo(1));
        }

        //楽観ロックつきのモジュールを読んで、名前を変えて送る
        async Task<ModuleSubmitResult> UpdateLockedAsync(string name)
        {
            var row = (await _h.IO.GetListAsync(new SearchCondition("CustomerLocked") { Condition = Value("Id.Value", MatchComparison.Equal, "5") }, 0)).Items.Single();
            var update = new ModuleData { Name = "CustomerLocked" };
            update.Fields["Id"] = row.Fields["Id"];
            update.Fields["Name"] = new TextFieldData { Value = name };
            update.Fields[SystemFieldNames.OptimisticLocking] = row.Fields[SystemFieldNames.OptimisticLocking];
            _h.Fake.ClearRequests();
            _h.Sql.Log.Clear();
            return (await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerLocked", Id = "5", Update = new() { update } })).Single();
        }

        [Test]
        public async Task 楽観ロックの版はPUTのrevisionで送りSETの版は送らない()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A", revision: 3) };

            var result = await UpdateLockedAsync("X");

            Assert.That(result.ExceptionMessage, Is.Empty);
            //本体は版を 1 つ進める SET と、今の版の条件を書く
            var sql = _h.Sql.Sqls.Single(e => e.StartsWith("update", StringComparison.Ordinal));
            Assert.That(sql, Does.Match("set .*\"\\$revision\"=@").And.EndWith("\"$revision\"=@poptimistic_current"));
            Assert.That(sql, Does.Contain("where \"$id\"=@pid0"));
            //kintone には版の一致を revision で渡し、版は kintone が進める
            var put = _h.Fake.DataRequests.Single();
            Assert.That(put.Body, Is.EqualTo("""{"app":11,"id":"5","revision":"3","record":{"Name":{"value":"X"}}}"""));
        }

        [Test]
        public async Task 版が違えば本体の楽観ロックのエラー()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A", revision: 3) };
            _h.Fake.Override(e => e.IsUpdate, _ => FakeKintone.Error(HttpStatusCode.Conflict, "GAIA_CO02", "The revision is not the latest."));

            var result = await UpdateLockedAsync("X");

            Assert.That(result.ExceptionMessage, Is.EqualTo("The record has been updated by another user. Reload it and try again"));
            //本体が行の有無を数えて確かめる (キーだけの条件)
            var count = _h.Fake.ReadRequests.Single(e => e.WantsTotalCount);
            Assert.That(count.Query, Is.EqualTo("$id = 5 limit 1"));
        }

        [Test]
        public async Task 楽観ロックで行が消えていれば更新の失敗()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A", revision: 3) };
            _h.Fake.Override(e => e.IsUpdate, _ =>
            {
                _h.Fake.Records[TestMetadata.CustomerApp].Clear();
                return FakeKintone.Error(HttpStatusCode.NotFound, "GAIA_RE01", "The record was not found.");
            });

            var result = await UpdateLockedAsync("X");

            Assert.That(result.ExceptionMessage, Is.EqualTo("Update failed"));
        }

        // ---------------------------------------------------------------- 削除

        [Test]
        public async Task IDで削除はDELETEだけ()
        {
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Delete = new() { new ModuleDeleteInfo { ModuleName = "Customer", Id = "5" } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var delete = _h.Fake.DataRequests.Single();
            Assert.That(delete.IsDelete, Is.True);
            Assert.That(delete.Url, Is.EqualTo(FakeKintone.ApiUrl + "/records.json?app=11&ids%5B0%5D=5"));
            Assert.That(_h.Sql.Sqls.Single(e => e.StartsWith("delete", StringComparison.Ordinal)), Is.EqualTo("delete from \"Customer\" where \"$id\"=@pid0"));
        }

        [Test]
        public async Task 重複禁止のフィールドがIdなら削除はレコードIDを引いてから()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(7, "A", code: "C-1") };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerByCode", Delete = new() { new ModuleDeleteInfo { ModuleName = "CustomerByCode", Id = "C-1" } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var requests = _h.Fake.DataRequests;
            Assert.That(requests[0].Query, Is.EqualTo("Code in (\"C-1\") limit 2"));
            Assert.That(requests[1].Url, Is.EqualTo(FakeKintone.ApiUrl + "/records.json?app=11&ids%5B0%5D=7"));
        }

        [TestCase("2件")]
        [TestCase("値違い")]
        [TestCase("キーの列なし")]
        public async Task 重複禁止のフィールドがIdの削除は引いたレコードが1件でキーの値が同じでなければ送らない(string found)
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = FoundByKey(found);

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerByCode", Delete = new() { new ModuleDeleteInfo { ModuleName = "CustomerByCode", Id = "C-1" } } });

            //本体の物理削除は 0 行でもエラーにしない
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(_h.Fake.ReadRequests.Single().Query, Is.EqualTo("Code in (\"C-1\") limit 2"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
            //削除の SQL の行数も 0 (0 行として返す)
            var count = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, _h.Sql.Sqls.Single(e => e.StartsWith("delete", StringComparison.Ordinal)), new() { ["@pid0"] = "C-1" });
            Assert.That(count, Is.EqualTo(0));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 重複禁止のフィールドの値の行が無ければ削除は送らない()
        {
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerByCode", Delete = new() { new ModuleDeleteInfo { ModuleName = "CustomerByCode", Id = "C-9" } } });

            //本体の物理削除は 0 行でもエラーにしない
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 論理削除は条件に合う行を数えてからPUT()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A") };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerSoft", Delete = new() { new ModuleDeleteInfo { ModuleName = "CustomerSoft", Id = "5" } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var data = _h.Fake.DataRequests;
            Assert.That(data, Has.Count.EqualTo(2));
            Assert.That(data[0].WantsTotalCount, Is.True);
            Assert.That(data[0].Query, Is.EqualTo("$id = 5 and ((Deleted != 1 and Deleted != \"\") or Deleted = \"\") limit 1"));
            Assert.That(data[1].Body, Is.EqualTo("""{"app":11,"id":"5","record":{"Deleted":{"value":"1"}}}"""));
        }

        [Test]
        public async Task 論理削除済みの行の論理削除は送らずに失敗()
        {
            _h.Fake.Override(e => e.WantsTotalCount, _ => FakeKintone.RecordsResponse(Array.Empty<JsonObject>(), 0), 5);

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "CustomerSoft", Delete = new() { new ModuleDeleteInfo { ModuleName = "CustomerSoft", Id = "5" } } });

            Assert.That(results.Single().ExceptionMessage, Is.EqualTo("Update failed"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        [Test]
        public async Task 楽観ロックつきの論理削除は数えてから版つきでPUT()
        {
            _h.Fake.Records[TestMetadata.CustomerApp] = new() { KintoneHarness.CustomerRecord(5, "A", revision: 3) };
            var row = (await _h.IO.GetListAsync(new SearchCondition("CustomerSoftLocked"), 0)).Items.Single();
            _h.Fake.ClearRequests();

            var results = await SubmitAsync(new ModuleSubmitData
            {
                ModuleName = "CustomerSoftLocked",
                Delete = new()
                {
                    new ModuleDeleteInfo
                    {
                        ModuleName = "CustomerSoftLocked",
                        Id = "5",
                        OptimisticLockingFieldData = (OptimisticLockingFieldData)row.Fields[SystemFieldNames.OptimisticLocking],
                    },
                },
            });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var data = _h.Fake.DataRequests;
            Assert.That(data[0].Query, Is.EqualTo("$id = 5 and ((Deleted != 1 and Deleted != \"\") or Deleted = \"\") limit 1"));
            Assert.That(data[1].Body, Is.EqualTo("""{"app":11,"id":"5","revision":"3","record":{"Deleted":{"value":"1"}}}"""));
        }

        // ---------------------------------------------------------------- テーブル (ListField の DbColumn + 行モジュールの JsonField)

        static JsonObject OrderRecord(long id)
            => FakeKintone.Record(id, 4,
                ("OrderNo", "SINGLE_LINE_TEXT", "S-1"),
                ("CustomerCode", "SINGLE_LINE_TEXT", "C1"),
                ("CustomerName", "SINGLE_LINE_TEXT", "Acme"),
                ("Lines", "SUBTABLE", JsonNode.Parse("""
                    [{"id":"17","value":{"Product":{"type":"SINGLE_LINE_TEXT","value":"椅子"},"Qty":{"type":"NUMBER","value":"3"},"Price":{"type":"NUMBER","value":"100"},
                      "Amount":{"type":"CALC","value":"300"},"ShipDate":{"type":"DATE","value":"2026-04-10"},"Options":{"type":"CHECK_BOX","value":["包装"]}}},
                     {"id":"18","value":{"Product":{"type":"SINGLE_LINE_TEXT","value":"机"},"Qty":{"type":"NUMBER","value":"1"},"Price":{"type":"NUMBER","value":""},
                      "Amount":{"type":"CALC","value":""},"ShipDate":{"type":"DATE","value":""},"Options":{"type":"CHECK_BOX","value":[]}}}]
                    """)));

        async Task<ModuleData> ReadOrderAsync()
            => (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = Value("Id.Value", MatchComparison.Equal, "1") }, 0)).Items.Single();

        static List<ModuleData> Lines(ModuleData order) => Field<ListFieldData>(order, "Lines")!.Children;

        [Test]
        public async Task テーブルは行モジュールの一覧で読み行のIDをJSONに持つ()
        {
            _h.Fake.Records[TestMetadata.OrderApp] = new() { OrderRecord(1) };

            var order = await ReadOrderAsync();

            Assert.That(Field<TextFieldData>(order, "CustomerName")!.Value, Is.EqualTo("Acme"));
            var lines = Lines(order);
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(Field<TextFieldData>(lines[0], "Product")!.Value, Is.EqualTo("椅子"));
            Assert.That(Field<NumberFieldData>(lines[0], "Qty")!.Value, Is.EqualTo(3m));
            Assert.That(Field<NumberFieldData>(lines[0], "Amount")!.Value, Is.EqualTo(300m));
            Assert.That(Field<DateFieldData>(lines[0], "ShipDate")!.Value, Is.EqualTo(new DateOnly(2026, 4, 10)));
            Assert.That(Field<NumberFieldData>(lines[1], "Price")?.Value, Is.Null);
            Assert.That(Field<DateFieldData>(lines[1], "ShipDate")?.Value, Is.Null);

            var json = JsonNode.Parse(Field<JsonFieldData>(lines[0], "Json")!.Value!)!.AsObject();
            Assert.That(json["$id"]!.GetValue<string>(), Is.EqualTo("17"));
            Assert.That(Text(json["Options"]), Is.EqualTo("""["包装"]"""));
            Assert.That(JsonNode.Parse(Field<JsonFieldData>(lines[1], "Json")!.Value!)!["$id"]!.GetValue<string>(), Is.EqualTo("18"));

            //テーブルの中身は 1 回の読み出しで来る (行ごとの要求は無い)
            Assert.That(_h.ListReads(TestMetadata.OrderApp), Has.Count.EqualTo(1));
            Assert.That(_h.ListReads(TestMetadata.OrderApp)[0].Fields, Does.Contain("Lines"));
        }

        [Test]
        public async Task テーブルの更新は残す行のIDと新しい行を送り消した行は送らない()
        {
            _h.Fake.Records[TestMetadata.OrderApp] = new() { OrderRecord(1) };
            var order = await ReadOrderAsync();
            var items = Field<ListFieldData>(order, "Lines")!;
            Field<NumberFieldData>(items.Children[0], "Qty")!.Value = 4;
            items.Children.RemoveAt(1);
            var added = new ModuleData { Name = "OrderLine" };
            added.Fields["Product"] = new TextFieldData { Value = "棚" };
            added.Fields["Qty"] = new NumberFieldData { Value = 2 };
            added.Fields["ShipDate"] = new DateFieldData { Value = new DateOnly(2026, 5, 1) };
            items.Children.Add(added);

            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["Lines"] = items;
            _h.Fake.ClearRequests();
            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Id = "1", Update = new() { update } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            var put = _h.Fake.WriteRequests.Single();
            Assert.That(put.IsUpdate, Is.True);
            Assert.That(put.Json!["id"]!.GetValue<string>(), Is.EqualTo("1"));
            var record = put.Json!["record"]!.AsObject();
            Assert.That(record.Select(e => e.Key), Is.EqualTo(new[] { "Lines" }));
            var rows = record["Lines"]!["value"]!.AsArray();
            Assert.That(rows, Has.Count.EqualTo(2));
            //残す行は id つき。計算 (Amount) は送らない
            Assert.That(rows[0]!["id"]!.GetValue<string>(), Is.EqualTo("17"));
            Assert.That(Text(rows[0]!["value"]!["Qty"]), Is.EqualTo("""{"value":"4"}"""));
            Assert.That(Text(rows[0]!["value"]!["Product"]), Is.EqualTo("""{"value":"椅子"}"""));
            Assert.That(rows[0]!["value"]!.AsObject().ContainsKey("Amount"), Is.False);
            //新しい行は id なし
            Assert.That(rows[1]!.AsObject().ContainsKey("id"), Is.False);
            Assert.That(Text(rows[1]!["value"]!["Product"]), Is.EqualTo("""{"value":"棚"}"""));
            Assert.That(Text(rows[1]!["value"]!["Qty"]), Is.EqualTo("""{"value":"2"}"""));
            Assert.That(Text(rows[1]!["value"]!["ShipDate"]), Is.EqualTo("""{"value":"2026-05-01"}"""));
            Assert.That(rows[1]!["value"]!.AsObject().ContainsKey("Amount"), Is.False);
        }

        [Test]
        public async Task テーブルつきの追加は行をPOSTに同梱する()
        {
            var add = new ModuleData { Name = "Order" };
            add.Fields["OrderNo"] = new TextFieldData { Value = "S-9" };
            var items = new ListFieldData();
            var line = new ModuleData { Name = "OrderLine" };
            line.Fields["Product"] = new TextFieldData { Value = "椅子" };
            line.Fields["Qty"] = new NumberFieldData { Value = 1 };
            items.Children.Add(line);
            add.Fields["Lines"] = items;

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            var record = _h.Fake.WriteRequests.Single().Json!["record"]!.AsObject();
            Assert.That(Text(record["OrderNo"]), Is.EqualTo("""{"value":"S-9"}"""));
            var rows = record["Lines"]!["value"]!.AsArray();
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0]!.AsObject().ContainsKey("id"), Is.False);
            Assert.That(Text(rows[0]!["value"]!["Product"]), Is.EqualTo("""{"value":"椅子"}"""));
        }

        [Test]
        public async Task ルックアップのコピー先は書き込めない()
        {
            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["CustomerName"] = new TextFieldData { Value = "X" };
            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Id = "1", Update = new() { update } })).Single();
            Assert.That(result.ExceptionMessage, Does.Contain("read-only").And.Contain("CustomerName"));
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
        }

        // ---------------------------------------------------------------- 振り分け・定義

        [Test]
        public async Task kintone以外のデータソースは包んだIDbAccessorに渡る()
        {
            var a = _h.Accessor;
            Assert.That(a.GetDataSource("Main")!.DataSourceType, Is.EqualTo(DataSourceType.SQLite));
            Assert.That((await a.QueryAsync("Main", "select 1", new()))[0]["inner"], Is.EqualTo("row"));
            Assert.That(await a.ExecuteAsync("Main", "update x set a=1", new()), Is.EqualTo(7));
            Assert.That(await a.InsertAsync("Main", "insert into x(a) values(1)", new()), Is.EqualTo("inner-id"));
            Assert.Throws<NotSupportedException>(() => a.GetConnection("Main"));
            Assert.That(a.GetTransaction("Main"), Is.Null);
            Assert.That(a.ConvertFieldValueToDbValue("Main", "X", 1), Is.EqualTo("inner-converted"));
            Assert.That(a.ConvertDbValueToFieldValue("Main", 1, typeof(int)), Is.EqualTo("inner-converted"));
            Assert.That((await a.ExecuteSqlCommandAsync("Main", new DbSqlCommand { CommandText = "x" })).ReturnValue, Is.EqualTo(1));
            Assert.That((await a.GetCustomTableDefinitionsAsync("Main"))!.Single().Name, Is.EqualTo("inner_table"));
            Assert.That((await a.GetCustomTableDefinitionsAsync("Main", "t"))!.Name, Is.EqualTo("t"));
            Assert.That(_h.Fake.Requests, Is.Empty);
        }

        [Test]
        public void kintoneのデータソースはPostgreSQLとして見せ接続文字列を返さない()
        {
            var dataSource = _h.Accessor.GetDataSource(TestConnections.DataSourceName)!;
            Assert.That(dataSource.Name, Is.EqualTo(TestConnections.DataSourceName));
            Assert.That(dataSource.DataSourceType, Is.EqualTo(DataSourceType.PostgreSQL));
            Assert.That(dataSource.ConnectionString, Is.Empty);
            Assert.That(_h.Accessor.GetTransaction(TestConnections.DataSourceName), Is.Null);
            Assert.That(_h.Accessor.ConvertFieldValueToDbValue(TestConnections.DataSourceName, "X", 1), Is.EqualTo(1));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public void SQLの直接実行とGetConnectionは例外()
        {
            Assert.That(async () => await _h.Accessor.ExecuteSqlCommandAsync(TestConnections.DataSourceName, new DbSqlCommand { CommandText = "select 1" }),
                Throws.InstanceOf<KintoneException>().With.Message.Contains("cannot be executed on a kintone data source"));
            Assert.Throws<KintoneException>(() => _h.Accessor.GetConnection(TestConnections.DataSourceName));
            Assert.That(async () => await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "truncate table \"Customer\"", new()), Throws.InstanceOf<KintoneException>());
            Assert.That(_h.Fake.DataRequests, Is.Empty);
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 表の定義はフォームの設定から作りアプリごとに1回だけ読む()
        {
            var tables = (await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!;
            Assert.That(tables.Select(e => e.Name), Is.EqualTo(new[] { "Customer", "Region", "Country", "Order" }));
            var customer = (await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "Customer"))!;
            Assert.That(customer.Columns.Select(e => e.Name), Does.Contain("$id").And.Contain("$revision").And.Contain("顧客 名").And.Not.Contain("Spacer"));
            Assert.That(_h.Fake.Requests.Count(e => e.IsForm), Is.EqualTo(4));
        }
    }
}
