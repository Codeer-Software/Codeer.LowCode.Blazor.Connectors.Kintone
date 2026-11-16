using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using Kintone.Mock;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness
{
    /// <summary>
    /// モック (Kintone.Mock) をプロセスの中で立て (実ポートは開かない)、本物のコネクターと本体の ModuleDataIO をつなぐ。
    /// データは mock-data.json (既定の架空のサンプル。テストごとに作り直すので互いに影響しない)。
    /// データソースは "KT" (顧客 1・商品 2・受注 3。全権限のトークン) と "KG" (ゲストスペース 5 のゲスト案件 4)。
    /// モジュール: Customer (Id = 重複禁止の CustomerCode・受注の一覧 Orders) / CustomerById (Id = $id) / CustomerLocked ($revision で楽観ロック) /
    /// Product (Id = ProductCode) / Order (Id = $id・顧客へのリンク Customer (ルックアップのキー CustomerCode)・テーブル Lines) /
    /// OrderLine (テーブルの行モジュール。ルートの JsonField + ノードに結線したフィールド) / Guest (データソース KG)
    /// </summary>
    public sealed class MockHarness : IAsyncDisposable
    {
        public const string Host = "http://localhost";
        public const string GuestDataSourceName = "KG";
        public const long CustomerApp = 1;
        public const long ProductApp = 2;
        public const long OrderApp = 3;
        public const long GuestApp = 4;
        public const long GuestSpace = 5;
        public const string Apps = "Customer:1,Product:2,Order:3";
        public const string Tokens = "mock-token-customer-all,mock-token-item-all,mock-token-order-all";

        WebApplication _app = null!;
        public MockKintone Mock { get; private set; } = null!;
        /// <summary>モックへの HTTP。</summary>
        public HttpMessageHandler Handler { get; private set; } = null!;
        internal KintoneConnection Connection { get; private set; } = null!;
        internal KintoneConnection GuestConnection { get; private set; } = null!;
        /// <summary>読み出しのやり直しで待とうとした時間 (実際には待たない)。</summary>
        public List<TimeSpan> Delays { get; } = new();
        public RecordingDbAccessor Inner { get; } = new();
        public KintoneDbAccessor Accessor { get; private set; } = null!;
        /// <summary>本体が組んだ SQL の記録。</summary>
        public SqlLoggingDbAccessor Sql { get; private set; } = null!;
        public DesignData Design { get; private set; } = null!;
        public ModuleDataIO IO { get; private set; } = null!;

        MockHarness() { }

        class Auth : IAuthenticationContext
        {
            public Task<string> GetCurrentUserIdAsync() => Task.FromResult("U1");
        }

        public static string DataPath => Path.Combine(AppContext.BaseDirectory, "mock-data.json");

        /// <param name="mock">既定は mock-data.json。</param>
        /// <param name="edit">接続 (KT) の設定を変えるとき。</param>
        /// <param name="editDesign">モジュールを足す・変えるとき (ModuleDataIO を作る前に呼ぶ)。</param>
        /// <param name="editGuest">接続 (KG) の設定を変えるとき。</param>
        public static async Task<MockHarness> StartAsync(MockKintone? mock = null, Action<KintoneConnectionSettings>? edit = null, Action<DesignData>? editDesign = null,
            Action<KintoneConnectionSettings>? editGuest = null)
        {
            var h = new MockHarness { Mock = mock ?? MockKintone.FromFile(DataPath) };

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            h._app = builder.Build();
            h._app.MapMockKintone(h.Mock);
            await h._app.StartAsync();
            h.Handler = new AbortAsHttpRequestExceptionHandler(h._app.GetTestServer().CreateHandler());

            h.Connection = h.CreateConnection(CreateSettings(TestConnections.DataSourceName, $"Host={Host};Apps={Apps};ApiTokens={Tokens}", edit));
            h.GuestConnection = h.CreateConnection(CreateSettings(GuestDataSourceName,
                $"Host={Host};Apps=Guest:{GuestApp};ApiTokens=mock-token-guest-all;GuestSpaceId={GuestSpace}", editGuest));
            h.Accessor = new KintoneDbAccessor(h.Inner, new[] { h.Connection, h.GuestConnection });
            h.Sql = new SqlLoggingDbAccessor(h.Accessor);
            h.Design = CreateDesign();
            editDesign?.Invoke(h.Design);
            AdjustDesign(h.Design);
            h.IO = new ModuleDataIO(h.Design, new Auth(), h.Sql, null!);
            return h;
        }

        internal static KintoneConnectionSettings CreateSettings(string name, string connectionString, Action<KintoneConnectionSettings>? edit = null)
        {
            var settings = KintoneConnectionSettings.FromDataSource(new Codeer.LowCode.Blazor.SystemSettings.DataSource { Name = name, ConnectionString = "Provider=Kintone;" + connectionString })!;
            edit?.Invoke(settings);
            return settings;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        /// <summary>このモックへの新しい接続 (定義のキャッシュを持たない)。やり直しは待たずに <see cref="Delays"/> に記録する。</summary>
        internal KintoneConnection CreateConnection(KintoneConnectionSettings settings)
            => new(settings, Handler)
            {
                Delay = e =>
                {
                    Delays.Add(e);
                    return Task.CompletedTask;
                },
            };

        static ModuleDesign Module(string name, string table, string dataSource = TestConnections.DataSourceName)
            => new() { Name = name, DataSourceName = dataSource, DbTable = table, CanCreate = true, CanUpdate = true, CanDelete = true };

        static FieldVariableMatchCondition Link(string target, string variable)
            => new() { SearchTargetVariable = target, Comparison = MatchComparison.Equal, Variable = variable };

        static DesignData CreateDesign()
        {
            var d = new DesignData();
            var modules = (IEditableModuleDesign)d.Modules;

            //顧客: Id は重複禁止の顧客コード (更新は updateKey・削除は $id を引いてから)
            var customer = Module("Customer", "Customer");
            customer.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "CustomerCode", IsManualInput = true });
            customer.Fields.Add(new NumberFieldDesign { Name = "RecordId", DbColumn = "$id" });
            customer.Fields.Add(new TextFieldDesign { Name = "CustomerName", DbColumn = "CustomerName" });
            customer.Fields.Add(new TextFieldDesign { Name = "Status", DbColumn = "Status" });
            customer.Fields.Add(new TextFieldDesign { Name = "Rank", DbColumn = "Rank" });
            customer.Fields.Add(new TextFieldDesign { Name = "Tags", DbColumn = "Tags" });
            customer.Fields.Add(new TextFieldDesign { Name = "Website", DbColumn = "Website" });
            customer.Fields.Add(new TextFieldDesign { Name = "Note", DbColumn = "Note" });
            customer.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "CreditLimit" });
            customer.Fields.Add(new DateFieldDesign { Name = "Since", DbColumn = "Since" });
            customer.Fields.Add(new DateTimeFieldDesign { Name = "Created", DbColumn = "作成日時", SaveAsUtc = true });
            customer.Fields.Add(new ListFieldDesign
            {
                Name = "Orders",
                SearchCondition = new SearchCondition("Order") { Condition = MultiMatchCondition.And(Link("Customer.Value", "Id.Value")) },
            });
            modules.Add(customer);

            var byId = Module("CustomerById", "Customer");
            byId.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            byId.Fields.Add(new TextFieldDesign { Name = "CustomerCode", DbColumn = "CustomerCode" });
            byId.Fields.Add(new TextFieldDesign { Name = "CustomerName", DbColumn = "CustomerName" });
            byId.Fields.Add(new TextFieldDesign { Name = "Rank", DbColumn = "Rank" });
            byId.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "CreditLimit" });
            modules.Add(byId);

            var locked = Module("CustomerLocked", "Customer");
            locked.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            locked.Fields.Add(new TextFieldDesign { Name = "CustomerName", DbColumn = "CustomerName" });
            locked.Fields.Add(new TextFieldDesign { Name = "Note", DbColumn = "Note" });
            locked.Fields.Add(new OptimisticLockingFieldDesign { Name = SystemFieldNames.OptimisticLocking, DbColumn = "$revision", IncrementVersion = true });
            modules.Add(locked);

            var product = Module("Product", "Product");
            product.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "ProductCode", IsManualInput = true });
            product.Fields.Add(new TextFieldDesign { Name = "ProductName", DbColumn = "ProductName" });
            product.Fields.Add(new NumberFieldDesign { Name = "UnitPrice", DbColumn = "UnitPrice" });
            product.Fields.Add(new TextFieldDesign { Name = "Category", DbColumn = "Category" });
            product.Fields.Add(new TextFieldDesign { Name = "RecordNo", DbColumn = "レコード番号" });
            modules.Add(product);

            //受注: Id は $id。顧客へのリンクはルックアップのキー (CustomerCode) を顧客の CustomerCode (= 顧客の Id) に結ぶ
            var order = Module("Order", "Order");
            order.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            order.Fields.Add(new TextFieldDesign { Name = "OrderNo", DbColumn = "OrderNo" });
            order.Fields.Add(new LinkFieldDesign
            {
                Name = "Customer",
                DbColumn = "CustomerCode",
                SearchCondition = new SearchCondition("Customer"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "CustomerName.Value",
            });
            order.Fields.Add(new TextFieldDesign { Name = "CustomerNameCopy", DbColumn = "CustomerNameCopy" });
            order.Fields.Add(new DateFieldDesign { Name = "OrderDate", DbColumn = "OrderDate" });
            order.Fields.Add(new TextFieldDesign { Name = "Stage", DbColumn = "Stage" });
            order.Fields.Add(new DateTimeFieldDesign { Name = "DeliveryAt", DbColumn = "DeliveryAt", SaveAsUtc = true });
            order.Fields.Add(new TimeFieldDesign { Name = "StartTime", DbColumn = "StartTime" });
            order.Fields.Add(new TextFieldDesign { Name = "Memo", DbColumn = "Memo" });
            order.Fields.Add(new TextFieldDesign { Name = "ProcessStatus", DbColumn = "ステータス" });
            order.Fields.Add(new NumberFieldDesign { Name = "Total", DbColumn = "Total" });
            order.Fields.Add(new ListFieldDesign
            {
                Name = "Lines",
                DbColumn = "Lines",
                SearchCondition = new SearchCondition("OrderLine") { LimitCount = null },
                CanCreate = true,
                CanUpdate = true,
                CanDelete = true,
            });
            //リンク越しの項目 (結合先の列)
            order.Fields.Add(new TextFieldDesign { Name = "Customer.CustomerName", DbColumn = "CustomerName" });
            order.Fields.Add(new TextFieldDesign { Name = "Customer.Status", DbColumn = "Status" });
            order.Fields.Add(new NumberFieldDesign { Name = "Customer.CreditLimit", DbColumn = "CreditLimit" });
            modules.Add(order);

            var orderLocked = Module("OrderLocked", "Order");
            orderLocked.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            orderLocked.Fields.Add(new TextFieldDesign { Name = "Stage", DbColumn = "Stage" });
            orderLocked.Fields.Add(new OptimisticLockingFieldDesign { Name = SystemFieldNames.OptimisticLocking, DbColumn = "$revision", IncrementVersion = true });
            modules.Add(orderLocked);

            modules.Add(CreateLineModule());

            var guest = Module("Guest", "Guest", GuestDataSourceName);
            guest.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "$id" });
            guest.Fields.Add(new TextFieldDesign { Name = "Title", DbColumn = "Title" });
            guest.Fields.Add(new NumberFieldDesign { Name = "Budget", DbColumn = "Budget" });
            modules.Add(guest);
            return d;
        }

        /// <summary>テーブル Lines の行モジュール (ルートの JsonField "Json" とノードに結線したフィールド)。</summary>
        internal static ModuleDesign CreateLineModule()
        {
            var line = new ModuleDesign { Name = "OrderLine" };
            var json = new JsonFieldDesign { Name = "Json" };
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Product", NodeType = JsonNodeType.Text });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Quantity", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "UnitPrice", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "Subtotal", NodeType = JsonNodeType.Number });
            line.Fields.Add(json);
            line.Fields.Add(new TextFieldDesign { Name = "Product", DbColumn = "Product" });
            line.Fields.Add(new NumberFieldDesign { Name = "Quantity", DbColumn = "Quantity" });
            line.Fields.Add(new NumberFieldDesign { Name = "UnitPrice", DbColumn = "UnitPrice" });
            line.Fields.Add(new NumberFieldDesign { Name = "Subtotal", DbColumn = "Subtotal" });
            return line;
        }

        //実行時のデザインの読み込み (DesignDataFileManager) と同じ正規化。リンク先のリンク (ドット名) のフィールドを合成する。
        //本体の internal なので、テストだけリフレクションで呼ぶ (KintoneHarness と同じ)
        static void AdjustDesign(DesignData d)
        {
            var adjust = typeof(DesignData).Assembly.GetType("Codeer.LowCode.Blazor.DesignLogic.ModuleDesignConsistencyLogic")!
                .GetMethod("Adjust", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(DesignData) })!;
            adjust.Invoke(null, new object[] { d });
        }

        // ---------------------------------------------------------------- 確かめる道具

        /// <summary>モックのレコード (レコード ID の昇順・records.json の形)。</summary>
        public IReadOnlyList<JsonObject> Records(long app) => Mock.GetRecords(app);

        /// <summary>モックのレコード 1 件。</summary>
        public JsonObject? Record(long app, long id) => Mock.GetRecord(app, id);

        /// <summary>レコードの読み出しの要求 (records.json の GET。件数の要求を含む)。</summary>
        public List<MockRecordedRequest> Reads(long app)
            => Mock.Requests.Where(e => e.Api == "records.json" && e.EffectiveMethod == "GET" && AppOf(e) == app).ToList();

        /// <summary>件数でない読み出しの要求。</summary>
        public List<MockRecordedRequest> ListReads(long app) => Reads(app).Where(e => !WantsTotalCount(e)).ToList();

        /// <summary>件数の要求 (totalCount)。</summary>
        public List<MockRecordedRequest> CountReads(long app) => Reads(app).Where(WantsTotalCount).ToList();

        /// <summary>書き込みの要求 (record.json の POST / PUT と records.json の DELETE)。</summary>
        public List<MockRecordedRequest> WriteRequests
            => Mock.Requests.Where(e => (e.Api == "record.json" && e.EffectiveMethod is "POST" or "PUT") || (e.Api == "records.json" && e.EffectiveMethod == "DELETE")).ToList();

        /// <summary>フォームの設定の要求を除いた要求。</summary>
        public List<MockRecordedRequest> DataRequests => Mock.Requests.Where(e => e.Api != "app/form/fields.json").ToList();

        /// <summary>要求の本文の JSON。</summary>
        public static JsonObject Body(MockRecordedRequest request) => JsonNode.Parse(request.Body!)!.AsObject();

        /// <summary>読み出しの要求のクエリ (本文の query)。</summary>
        public static string Query(MockRecordedRequest request) => Body(request)["query"]!.GetValue<string>();

        static bool WantsTotalCount(MockRecordedRequest request)
            => request.Body != null && Body(request)["totalCount"] is JsonValue v && v.GetValue<bool>();

        static long? AppOf(MockRecordedRequest request)
        {
            if (request.Body != null && Body(request)["app"] is JsonValue v)
                return v.TryGetValue<long>(out var n) ? n : long.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture);
            var app = request.Query.FirstOrDefault(e => e.Key == "app").Value;
            return app == null ? null : long.Parse(app, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// TestServer はサーバーが接続を切ると (モックの DisconnectNext / DisconnectAfterWrite) OperationCanceledException を投げる。
    /// 実際の通信 (SocketsHttpHandler) で接続が切れたときと同じ HttpRequestException にする (呼び出し側の取り消し・タイムアウトはそのまま)。
    /// </summary>
    sealed class AbortAsHttpRequestExceptionHandler : DelegatingHandler
    {
        public AbortAsHttpRequestExceptionHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is not HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("The connection was closed by the server: " + e.Message, e);
            }
        }
    }

    /// <summary>モックを相手にしたテストの共通の道具。</summary>
    public static class MockTestUtil
    {
        public static FieldValueMatchCondition Match(string variable, MatchComparison comparison, object? value)
            => new() { SearchTargetVariable = variable, Comparison = comparison, Value = value == null ? new NullValue() : value as MultiTypeValue ?? MultiTypeValue.Create(value) };

        public static MatchConditionBase Not(MatchConditionBase condition)
        {
            var not = MultiMatchCondition.And(condition);
            ((MultiMatchCondition)not).IsNot = true;
            return not;
        }

        public static string Id(ModuleData data) => ((IdFieldData)data.Fields["Id"]).Value!;

        public static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        static JsonNode? RawValue(JsonObject record, string code) => record[code] is JsonObject field ? field["value"] : null;

        /// <summary>レコードの $id。</summary>
        public static long RecordId(this JsonObject record) => long.Parse(record["$id"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        public static long Revision(this JsonObject record) => long.Parse(record["$revision"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        /// <summary>文字の値 (未入力 = null・"" は null)。作成者などの {code, name} は code。</summary>
        public static string? Text(this JsonObject record, string code) => RawValue(record, code) switch
        {
            JsonObject o => o["code"]?.GetValue<string>(),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0 ? null : s,
            _ => null,
        };

        public static decimal? Number(this JsonObject record, string code)
            => record.Text(code) is { } s ? decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) : null;

        public static DateOnly? Date(this JsonObject record, string code)
            => record.Text(code) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

        public static TimeOnly? Time(this JsonObject record, string code)
            => record.Text(code) is { } s ? TimeOnly.ParseExact(s, "HH:mm", CultureInfo.InvariantCulture) : null;

        public static DateTime? DateTimeUtc(this JsonObject record, string code)
            => record.Text(code) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).UtcDateTime : null;

        /// <summary>配列の値 (チェックボックスなど。ユーザー選択は code)。</summary>
        public static List<string> Array(this JsonObject record, string code)
            => RawValue(record, code) is JsonArray a ? a.Select(e => e is JsonObject o ? o["code"]!.GetValue<string>() : e!.GetValue<string>()).ToList() : new();

        /// <summary>テーブルの行 (行 ID と、行の値 (コード → 値の JSON))。</summary>
        public static List<(long Id, JsonObject Values)> Rows(this JsonObject record, string code)
            => RawValue(record, code) is JsonArray a
                ? a.Select(e => (long.Parse(e!["id"]!.GetValue<string>(), CultureInfo.InvariantCulture), e["value"]!.AsObject())).ToList()
                : new();

        /// <summary>Id の文字 (数値は末尾の 0 を落とす)。</summary>
        public static string KeyText(object? value) => value switch
        {
            decimal d => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        public static KintoneException? FindKintoneException(Exception? e)
        {
            for (; e != null; e = e.InnerException) if (e is KintoneException k) return k;
            return null;
        }
    }
}
