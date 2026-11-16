namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>kintone のアカウントの確認の結果の種類。</summary>
    public enum KintoneAccountStatus
    {
        /// <summary>ログイン名とパスワードで kintone に入れた。</summary>
        Succeeded,
        /// <summary>ログイン名かパスワードが違う (kintone が 401 を返した・入力が空)。</summary>
        InvalidCredentials,
        /// <summary>確かめられなかった (kintone に届かない・パスワード認証が使えない設定 など)。</summary>
        Failed,
    }

    /// <summary><see cref="KintoneConnection.VerifyAccountAsync"/> の結果。パスワードは入らない。</summary>
    public sealed class KintoneAccountResult
    {
        KintoneAccountResult(KintoneAccountStatus status, string loginName, string message)
        {
            Status = status;
            LoginName = loginName;
            Message = message;
        }

        public KintoneAccountStatus Status { get; }

        public bool Succeeded => Status == KintoneAccountStatus.Succeeded;

        /// <summary>ログイン名 (入力されたもの)。成功しなければ空。</summary>
        public string LoginName { get; }

        /// <summary>失敗の理由 (ログ用。利用者に見せる文言ではない)。成功なら空。</summary>
        public string Message { get; }

        internal static KintoneAccountResult Success(string loginName) => new(KintoneAccountStatus.Succeeded, loginName, string.Empty);
        internal static KintoneAccountResult Invalid(string message) => new(KintoneAccountStatus.InvalidCredentials, string.Empty, message);
        internal static KintoneAccountResult Fail(string message) => new(KintoneAccountStatus.Failed, string.Empty, message);
    }
}
