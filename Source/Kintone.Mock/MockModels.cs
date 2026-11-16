namespace Kintone.Mock
{
    /// <summary>モックが受ける要求 1 つ (HTTP から切り離した形)。</summary>
    public sealed class MockRequest
    {
        public string Method { get; init; } = "GET";

        /// <summary>デコードしたパス (例: "/k/v1/records.json"・"/k/guest/5/v1/record.json")。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>デコードしたクエリ (同じ名前が複数あれば全部)。</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>受けたままのパスとクエリ (記録用)。</summary>
        public string PathAndQuery { get; init; } = string.Empty;

        public string? Body { get; init; }

        /// <summary>Content-Type ヘッダー (無ければ null)。</summary>
        public string? ContentType { get; init; }

        /// <summary>X-Cybozu-API-Token ヘッダー (無ければ null)。</summary>
        public string? ApiToken { get; init; }

        /// <summary>X-Cybozu-Authorization ヘッダー (無ければ null)。</summary>
        public string? CybozuAuthorization { get; init; }

        /// <summary>X-HTTP-Method-Override ヘッダー (無ければ null)。</summary>
        public string? MethodOverride { get; init; }
    }

    /// <summary>応答 1 つ。Abort なら本文を返さずに接続を切る。</summary>
    public sealed class MockResponse
    {
        public int Status { get; init; }
        public string? Body { get; init; }
        public string ContentType { get; init; } = "application/json";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Abort { get; init; }
    }

    /// <summary>受けた要求の記録。</summary>
    public sealed class MockRecordedRequest
    {
        /// <summary>HTTP のメソッド (送られたまま)。</summary>
        public string Method { get; init; } = string.Empty;

        /// <summary>X-HTTP-Method-Override を当てたあとのメソッド (POST + Override: GET なら GET)。</summary>
        public string EffectiveMethod { get; init; } = string.Empty;

        /// <summary>X-HTTP-Method-Override ヘッダー (無ければ null)。</summary>
        public string? MethodOverride { get; init; }

        /// <summary>デコードしたパス。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>受けたままのパスとクエリ (URL エンコードされたまま)。</summary>
        public string PathAndQuery { get; init; } = string.Empty;

        /// <summary>v1/ より後ろの API 名 (例: "records.json"・"app/form/fields.json")。kintone の API の形でなければ null。</summary>
        public string? Api { get; init; }

        /// <summary>ゲストスペースのパス (/k/guest/{id}/v1/) なら、その ID。</summary>
        public long? GuestSpaceId { get; init; }

        /// <summary>デコードしたクエリ (同じ名前が複数あれば全部)。</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>本文 (無ければ null)。</summary>
        public string? Body { get; init; }

        /// <summary>X-Cybozu-API-Token に付いていたトークン (カンマで分けたもの。無ければ空)。</summary>
        public IReadOnlyList<string> ApiTokens { get; init; } = Array.Empty<string>();

        /// <summary>X-Cybozu-Authorization のログイン名 (無い・読めなければ null。パスワードは記録しない)。</summary>
        public string? LoginName { get; init; }

        public override string ToString() => $"{EffectiveMethod} {PathAndQuery}";
    }

    /// <summary>kintone の形のエラー応答にする例外 (モックの中だけで使う)。</summary>
    sealed class MockKintoneException : Exception
    {
        public MockKintoneException(int status, string code, string message, IReadOnlyDictionary<string, List<string>>? errors = null) : base(message)
        {
            Status = status;
            Code = code;
            Errors = errors;
        }

        public int Status { get; }
        public string Code { get; }
        public IReadOnlyDictionary<string, List<string>>? Errors { get; }

        /// <summary>入力の検証エラー (400 CB_VA01 + errors)。</summary>
        public static MockKintoneException Validation(IReadOnlyDictionary<string, List<string>> errors)
            => new(400, "CB_VA01", "入力内容が正しくありません。", errors);

        public static MockKintoneException Validation(string key, string message)
            => Validation(new Dictionary<string, List<string>> { [key] = new List<string> { message } });
    }
}
