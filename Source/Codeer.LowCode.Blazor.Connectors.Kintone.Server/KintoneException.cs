using System.Net;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>kintone との連携の失敗の種類。</summary>
    public enum KintoneErrorKind
    {
        /// <summary>設定の誤り・このコネクターが扱えない操作。</summary>
        NotSupported,
        /// <summary>認証に失敗した・アクセス権が無い (401 / 403)。</summary>
        Authentication,
        /// <summary>対象が無い (404)。</summary>
        NotFound,
        /// <summary>入力・kintone の検証のエラー (400 など)。</summary>
        InvalidRequest,
        /// <summary>kintone 側・通信の障害 (429・5xx・つながらない)。</summary>
        ServiceUnavailable,
        /// <summary>書き込みを送ったあと結果を受け取れなかった。kintone 側で処理されたかどうかは分からない。</summary>
        WriteResultUnknown,
        /// <summary>上のどれにも当たらない応答。</summary>
        Unknown,
    }

    /// <summary>kintone との連携の失敗。</summary>
    public class KintoneException : Exception
    {
        public KintoneErrorKind Kind { get; }

        /// <summary>HTTP ステータス (応答を受け取っていれば)。</summary>
        public HttpStatusCode? StatusCode { get; }

        /// <summary>kintone のエラーコード (応答の code)。</summary>
        public string ErrorCode { get; } = string.Empty;

        public KintoneException(string message, KintoneErrorKind kind = KintoneErrorKind.NotSupported, Exception? innerException = null)
            : base(message, innerException)
        {
            Kind = kind;
        }

        public KintoneException(string message, KintoneErrorKind kind, HttpStatusCode? statusCode, string errorCode, Exception? innerException = null)
            : base(message, innerException)
        {
            Kind = kind;
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }
}
