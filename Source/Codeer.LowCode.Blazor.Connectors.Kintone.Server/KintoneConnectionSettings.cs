using Codeer.LowCode.Blazor.SystemSettings;
using System.Data.Common;
using System.Globalization;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>データソースで扱う kintone のアプリ 1 つ (表名とアプリ ID)。</summary>
    public class KintoneAppSettings
    {
        /// <summary>表名 (モジュールの DbTable)。設定で付ける名前で、環境ごとにアプリ ID が違っても同じ名前にする。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>kintone のアプリ ID。</summary>
        public long AppId { get; set; }
    }

    /// <summary>
    /// kintone への接続 1 つ分の設定。データソース (DataSources) の ConnectionString に "Provider=Kintone;..." の形で書く。
    /// </summary>
    public class KintoneConnectionSettings
    {
        /// <summary>ConnectionString の Provider の値。これがあるデータソースを kintone として扱う。</summary>
        public const string ProviderName = "Kintone";

        /// <summary>1 回の要求に付けられる API トークンの数 (kintone の上限)。</summary>
        public const int MaxApiTokens = 9;

        /// <summary>データソース名 (モジュールの DataSourceName)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>kintone の URL (例: https://sample.cybozu.com)。</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>扱うアプリ (Apps=表名:アプリID,表名:アプリID)。</summary>
        public List<KintoneAppSettings> Apps { get; set; } = new();

        /// <summary>API トークン (ApiTokens=トークン,トークン)。アプリごとに 1 つ。要求には全部を付ける。User / Password と同時には書けない。</summary>
        public List<string> ApiTokens { get; set; } = new();

        /// <summary>パスワード認証のログイン名。API トークンの代わりに使う。</summary>
        public string User { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>ゲストスペースのアプリならスペース ID。データソースのアプリは全部そのゲストスペースのもの。</summary>
        public long? GuestSpaceId { get; set; }

        /// <summary>ページングなしの読み出しで受け取る行の上限。超えたらエラーにする (黙って切り捨てない)。</summary>
        public int MaxRows { get; set; } = 10000;

        /// <summary>1 回の要求のタイムアウト (秒)。</summary>
        public int TimeoutSeconds { get; set; } = 100;

        /// <summary>REST API の URL ({Host}/k/v1 か、ゲストスペースなら {Host}/k/guest/{スペース ID}/v1)。</summary>
        public string ApiUrl => GuestSpaceId == null
            ? $"{Host.TrimEnd('/')}/k/v1"
            : $"{Host.TrimEnd('/')}/k/guest/{GuestSpaceId.Value.ToString(CultureInfo.InvariantCulture)}/v1";

        /// <summary>パスワード認証でつなぐか (User があれば)。</summary>
        public bool UsesPassword => !string.IsNullOrEmpty(User);

        /// <summary>
        /// データソースが kintone 用 (ConnectionString が Provider=Kintone) なら設定を読む。違えば null。
        /// </summary>
        public static KintoneConnectionSettings? FromDataSource(DataSource dataSource)
        {
            DbConnectionStringBuilder builder;
            try
            {
                builder = new DbConnectionStringBuilder { ConnectionString = dataSource.ConnectionString };
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (!builder.TryGetValue("Provider", out var provider) ||
                !string.Equals(provider?.ToString(), ProviderName, StringComparison.OrdinalIgnoreCase)) return null;

            string Get(string key) => builder.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
            int GetInt(string key, int defaultValue) => int.TryParse(Get(key), out var v) && 0 < v ? v : defaultValue;
            List<string> GetList(string key) => Get(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var defaults = new KintoneConnectionSettings();
            var settings = new KintoneConnectionSettings
            {
                Name = dataSource.Name,
                Host = Get(nameof(Host)).TrimEnd('/'),
                ApiTokens = GetList(nameof(ApiTokens)),
                User = Get(nameof(User)),
                Password = Get(nameof(Password)),
                MaxRows = GetInt(nameof(MaxRows), defaults.MaxRows),
                TimeoutSeconds = GetInt(nameof(TimeoutSeconds), defaults.TimeoutSeconds),
            };
            var guestSpace = Get(nameof(GuestSpaceId));
            if (!string.IsNullOrEmpty(guestSpace))
            {
                //読めない値は Validate で設定の誤りにする (0 は通さない)
                settings.GuestSpaceId = long.TryParse(guestSpace, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
            }
            foreach (var item in GetList(nameof(Apps)))
            {
                //表名:アプリ ID。読めない組は Validate で設定の誤りにする (AppId = 0)
                var separator = item.LastIndexOf(':');
                var name = separator < 0 ? item : item[..separator].Trim();
                var appId = separator < 0 ? string.Empty : item[(separator + 1)..].Trim();
                settings.Apps.Add(new KintoneAppSettings
                {
                    Name = name,
                    AppId = long.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0,
                });
            }
            return settings;
        }

        internal void Validate()
        {
            KintoneException Invalid(string message) => new($"kintone data source '{Name}': {message}");

            if (string.IsNullOrWhiteSpace(Host)) throw Invalid($"{nameof(Host)} is required.");
            //https だけ。このマシン自身 (localhost / 127.0.0.1) で動かすモックに限り http を許す (API トークン・パスワードを平文で流さない)
            if (!Uri.TryCreate(Host, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))))
                throw Invalid($"{nameof(Host)} must be an https URL.");
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw Invalid($"{nameof(Host)} must not have a query or a fragment.");
            if (GuestSpaceId != null && GuestSpaceId <= 0) throw Invalid($"{nameof(GuestSpaceId)} must be a space id (a positive number).");

            if (Apps.Count == 0) throw Invalid($"{nameof(Apps)} is required (Apps=TableName:AppId,TableName:AppId).");
            foreach (var app in Apps)
            {
                if (app.AppId <= 0 || !KintoneTableDefinition.IsUsableTableName(app.Name))
                    throw Invalid($"{nameof(Apps)} must be a list of 'TableName:AppId' (the table name cannot contain '.' or '\"').");
            }
            if (Apps.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != Apps.Count) throw Invalid($"{nameof(Apps)} has the same table name twice.");
            if (Apps.Select(e => e.AppId).Distinct().Count() != Apps.Count) throw Invalid($"{nameof(Apps)} has the same app id twice.");

            if (UsesPassword)
            {
                if (ApiTokens.Count != 0) throw Invalid($"Write either {nameof(ApiTokens)} or {nameof(User)} / {nameof(Password)}, not both.");
                if (string.IsNullOrEmpty(Password)) throw Invalid($"{nameof(Password)} is required.");
                if (User.Contains(':')) throw Invalid($"{nameof(User)} cannot contain ':'.");
            }
            else
            {
                if (ApiTokens.Count == 0) throw Invalid($"{nameof(ApiTokens)} (or {nameof(User)} / {nameof(Password)}) is required.");
                if (MaxApiTokens < ApiTokens.Count)
                    throw Invalid($"{nameof(ApiTokens)} can have up to {MaxApiTokens} tokens (the limit of kintone). Split the data source, or use {nameof(User)} / {nameof(Password)}.");
            }
        }

        //同じ接続先・同じ認証なら同じ接続 (定義のキャッシュ) を使う。トークン・パスワードは変えたら別の接続になるように含める
        internal string GetIdentityKey()
            => string.Join("\n", Name, Host, string.Join(",", Apps.Select(e => e.Name + ":" + e.AppId.ToString(CultureInfo.InvariantCulture))),
                string.Join(",", ApiTokens), User, Password, GuestSpaceId, MaxRows, TimeoutSeconds);
    }
}
