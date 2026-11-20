using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness
{
    /// <summary>テスト用の設定・接続を作る。値は架空のもの。</summary>
    public static class TestConnections
    {
        public const string DataSourceName = "KT";
        public const string TokenA = "fake-token-a";
        public const string TokenB = "fake-token-b";

        internal static KintoneConnectionSettings CreateSettings(Action<KintoneConnectionSettings>? edit = null)
        {
            var settings = KintoneConnectionSettings.FromDataSource(new Codeer.LowCode.Blazor.SystemSettings.DataSource
            {
                Name = DataSourceName,
                ConnectionString = $"Provider=Kintone;Host={FakeKintone.Host};Apps={TestMetadata.Apps};ApiTokens={TokenA},{TokenB}",
            })!;
            edit?.Invoke(settings);
            return settings;
        }

        /// <summary>待ち時間を記録だけして待たない接続。</summary>
        internal static KintoneConnection Create(FakeKintone fake, List<TimeSpan>? delays = null, Action<KintoneConnectionSettings>? edit = null)
        {
            var connection = new KintoneConnection(CreateSettings(edit), fake);
            connection.Delay = e =>
            {
                delays?.Add(e);
                return Task.CompletedTask;
            };
            return connection;
        }
    }

    /// <summary>
    /// 本体の ModuleDataIO を KintoneDbAccessor (中身は FakeKintone) で動かす。データソース名は "KT"。
    /// モジュール: Customer (Id = $id・Region へのリンク) / CustomerByCode (Id = 重複禁止の Code) / CustomerLocked ($revision で楽観ロック・IncrementVersion) /
    /// CustomerSoft (Deleted で論理削除) / CustomerSoftLocked (論理削除 + 楽観ロック) / CustomerCalc (計算・レコード番号) /
    /// CustomerWithCountry (Customer → Region → Country の 2 段) / Region (Id = Code) / Country (Id = $id) /
    /// Order (テーブル Lines = ListField の DbColumn) / OrderLine (行モジュール。ルートの JsonField + ノードに結線したフィールド)
    /// </summary>
    public sealed class KintoneHarness : IDisposable
    {
        public FakeKintone Fake { get; } = new();
        public RecordingDbAccessor Inner { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        internal KintoneConnection Connection { get; }
        public KintoneDbAccessor Accessor { get; }
        /// <summary>本体が組んだ SQL の記録 (ModuleDataIO はこれを通して Accessor を呼ぶ)。</summary>
        public SqlLoggingDbAccessor Sql { get; }
        public DesignData Design { get; }
        public ModuleDataIO IO { get; }

        class Auth : IAuthenticationContext
        {
            public Task<string> GetCurrentUserIdAsync() => Task.FromResult("U1");
        }

        /// <param name="edit">接続の設定を変えるとき。</param>
        /// <param name="metadataFile">アプリの定義の出所 (null なら kintone のフォームの設定を読む)。</param>
        public KintoneHarness(Action<KintoneConnectionSettings>? edit = null, KintoneMetadataFile? metadataFile = null)
        {
            TestMetadata.AddForms(Fake);
            Connection = TestConnections.Create(Fake, Delays, edit);
            Accessor = new KintoneDbAccessor(Inner, new[] { Connection }, metadataFile);
            Sql = new SqlLoggingDbAccessor(Accessor);
            Design = CreateDesign();
            IO = new ModuleDataIO(Design, new Auth(), Sql, null!);
        }

        public void Dispose() => Fake.Dispose();

        static ModuleDesign Module(string name, string table)
            => new() { Name = name, DataSourceName = TestConnections.DataSourceName, DbTable = table, CanCreate = true, CanUpdate = true, CanDelete = true };

        static LinkFieldDesign RegionLink() => new()
        {
            Name = "Region",
            DbColumn = "RegionCode",
            SearchCondition = new SearchCondition("Region"),
            ValueVariable = "Id.Value",
            DisplayTextVariable = "Name.Value",
        };

        static DesignData CreateDesign()
        {
            var d = new DesignData();
            var modules = (IEditableModuleDesign)d.Modules;

            var country = Module("Country", "Country");
            country.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            country.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "CountryName" });
            modules.Add(country);

            var region = Module("Region", "Region");
            region.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "Code" });
            region.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "RegionName" });
            region.Fields.Add(new LinkFieldDesign
            {
                Name = "Country",
                DbColumn = "CountryId",
                SearchCondition = new SearchCondition("Country"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "Name.Value",
            });
            modules.Add(region);

            var customer = Module("Customer", "Customer");
            customer.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            customer.Fields.Add(new TextFieldDesign { Name = "Code", DbColumn = "Code" });
            customer.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            customer.Fields.Add(new TextFieldDesign { Name = "Note", DbColumn = "Note" });
            customer.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "CreditLimit" });
            customer.Fields.Add(new BooleanFieldDesign { Name = "IsActive", DbColumn = "IsActive" });
            customer.Fields.Add(new DateFieldDesign { Name = "StartDate", DbColumn = "StartDate" });
            customer.Fields.Add(new TimeFieldDesign { Name = "OpenTime", DbColumn = "OpenTime" });
            customer.Fields.Add(new DateTimeFieldDesign { Name = "LastContact", DbColumn = "LastContact", SaveAsUtc = true });
            customer.Fields.Add(new TextFieldDesign { Name = "Rank", DbColumn = "Rank" });
            customer.Fields.Add(new TextFieldDesign { Name = "Tags", DbColumn = "Tags" });
            customer.Fields.Add(RegionLink());
            modules.Add(customer);

            var byCode = Module("CustomerByCode", "Customer");
            byCode.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "Code" });
            byCode.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            byCode.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "CreditLimit" });
            modules.Add(byCode);

            var locked = Module("CustomerLocked", "Customer");
            locked.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            locked.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            locked.Fields.Add(new OptimisticLockingFieldDesign { Name = SystemFieldNames.OptimisticLocking, DbColumn = "$revision", IncrementVersion = true });
            modules.Add(locked);

            var soft = Module("CustomerSoft", "Customer");
            soft.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            soft.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            soft.Fields.Add(new BooleanFieldDesign { Name = SystemFieldNames.LogicalDelete, DbColumn = "Deleted" });
            modules.Add(soft);

            var softLocked = Module("CustomerSoftLocked", "Customer");
            softLocked.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            softLocked.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            softLocked.Fields.Add(new BooleanFieldDesign { Name = SystemFieldNames.LogicalDelete, DbColumn = "Deleted" });
            softLocked.Fields.Add(new OptimisticLockingFieldDesign { Name = SystemFieldNames.OptimisticLocking, DbColumn = "$revision", IncrementVersion = true });
            modules.Add(softLocked);

            var calc = Module("CustomerCalc", "Customer");
            calc.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            calc.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            calc.Fields.Add(new NumberFieldDesign { Name = "Total", DbColumn = "Total" });
            calc.Fields.Add(new TextFieldDesign { Name = "RecordNo", DbColumn = "RecordNo" });
            modules.Add(calc);

            var multi = Module("CustomerWithCountry", "Customer");
            multi.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            multi.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "Name" });
            multi.Fields.Add(RegionLink());
            //リンク先のリンク (Customer → Region → Country の 2 段の結合になる)
            multi.LinkFieldNames.Add("Region.Country");
            modules.Add(multi);

            var order = Module("Order", "Order");
            order.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            order.Fields.Add(new TextFieldDesign { Name = "OrderNo", DbColumn = "OrderNo" });
            order.Fields.Add(new TextFieldDesign { Name = "CustomerCode", DbColumn = "CustomerCode" });
            order.Fields.Add(new TextFieldDesign { Name = "CustomerName", DbColumn = "CustomerName" });
            order.Fields.Add(new ListFieldDesign
            {
                Name = "Lines",
                DbColumn = "Lines",
                SearchCondition = new SearchCondition("OrderLine") { LimitCount = null },
                CanCreate = true,
                CanUpdate = true,
                CanDelete = true,
            });
            modules.Add(order);

            var line = new ModuleDesign { Name = "OrderLine" };
            var json = new JsonFieldDesign { Name = "Json" };
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Product", NodeType = JsonNodeType.Text });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Qty", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Price", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Amount", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "ShipDate", NodeType = JsonNodeType.Date });
            line.Fields.Add(json);
            line.Fields.Add(new TextFieldDesign { Name = "Product", DbColumn = "Product" });
            line.Fields.Add(new NumberFieldDesign { Name = "Qty", DbColumn = "Qty" });
            line.Fields.Add(new NumberFieldDesign { Name = "Price", DbColumn = "Price" });
            line.Fields.Add(new NumberFieldDesign { Name = "Amount", DbColumn = "Amount" });
            line.Fields.Add(new DateFieldDesign { Name = "ShipDate", DbColumn = "ShipDate" });
            modules.Add(line);

            //実行時のデザインの読み込み (DesignDataFileManager) と同じ正規化。リンク先のリンク (ドット名) のフィールドを合成する。
            //本体の internal なので、テストだけリフレクションで呼ぶ
            var adjust = typeof(DesignData).Assembly.GetType("Codeer.LowCode.Blazor.DesignLogic.ModuleDesignConsistencyLogic")!
                .GetMethod("Adjust", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(DesignData) })!;
            adjust.Invoke(null, new object[] { d });
            return d;
        }

        /// <summary>顧客のレコード (kintone の応答の形)。</summary>
        public static JsonObject CustomerRecord(long id, string name, string? regionCode = null, long revision = 1, string? code = null)
        {
            var fields = new List<(string, string, JsonNode?)>
            {
                ("Code", "SINGLE_LINE_TEXT", code ?? $"C{id}"),
                ("Name", "SINGLE_LINE_TEXT", name),
                ("RegionCode", "SINGLE_LINE_TEXT", regionCode ?? string.Empty),
            };
            return FakeKintone.Record(id, revision, fields.ToArray());
        }

        public static JsonObject RegionRecord(long id, string code, string name, long? countryId = null)
            => FakeKintone.Record(id, 1, ("Code", "SINGLE_LINE_TEXT", code), ("RegionName", "SINGLE_LINE_TEXT", name),
                ("CountryId", "NUMBER", countryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));

        public static JsonObject CountryRecord(long id, string name)
            => FakeKintone.Record(id, 1, ("CountryName", "SINGLE_LINE_TEXT", name));

        /// <summary>読み出しの要求 (アプリ ID で絞る)。</summary>
        public List<RecordedRequest> Reads(long app) => Fake.ReadRequests.Where(e => e.App == app).ToList();

        /// <summary>件数でない読み出しの要求。</summary>
        public List<RecordedRequest> ListReads(long app) => Reads(app).Where(e => !e.WantsTotalCount).ToList();
    }
}
