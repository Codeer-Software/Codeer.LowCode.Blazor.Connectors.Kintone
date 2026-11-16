namespace LowCodeApp.Server.Infrastructure
{
    /// <summary>
    /// kintone のアカウントでのログイン (appsettings の KintoneLogin)。DataSourceName が空なら使わない。
    /// kintone が確かめたログイン名を、ユーザーモジュールの LoginAccountContractField の ExternalLoginName (空なら LoginName) の列と突き合わせる。
    /// ログインのあとのデータの読み書きは、データソースの接続文字列の認証 (連携用の API トークン) のまま。
    /// </summary>
    public class KintoneLoginSettings
    {
        /// <summary>アカウントを確かめる kintone のデータソースの名前。</summary>
        public string DataSourceName { get; set; } = string.Empty;

        public bool IsEnabled => !string.IsNullOrEmpty(DataSourceName);
    }
}
