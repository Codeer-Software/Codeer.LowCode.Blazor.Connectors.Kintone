using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// 添付ファイルのフィールド (FILE) の列の値の読み解き。値は kintone のレコードの値と同じ形の JSON の配列の文字。
    /// 読むと [{"contentType","fileKey","name","size"}]。書くときに意味を持つのは、base64 を持つ要素 (追加: name・contentType・base64) と
    /// deleted が true の要素 (削除: fileKey) だけ。それ以外の要素 (読んだままのファイル)・ほかのキーは見ない (クライアントの fileKey で残すファイルを決めない)。
    /// </summary>
    static class KintoneAttachments
    {
        /// <summary>送るときに contentType が無いファイルの種類。</summary>
        internal const string DefaultContentType = "application/octet-stream";

        /// <summary>fileKey として受け付ける長さの上限 (URL・比較に使う前に確かめる)。</summary>
        const int MaxFileKeyLength = 256;

        /// <summary>足すファイル 1 つ。</summary>
        internal sealed record Addition(string Name, string ContentType, byte[] Content);

        /// <summary>1 列分の変更 (足すファイルと消すファイルの fileKey)。</summary>
        internal sealed class Change
        {
            public List<Addition> Additions { get; } = new();
            public List<string> Deletions { get; } = new();
            public bool IsEmpty => Additions.Count == 0 && Deletions.Count == 0;
        }

        /// <summary>fileKey の形 (空でない・長すぎない・制御文字を含まない)。kintone の形 (英数字) より緩くして、kintone の書式の変化で壊れないようにする。</summary>
        internal static bool IsFileKey(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxFileKeyLength && !text.Any(char.IsControl);

        /// <summary>
        /// 書き込みの値 → 変更。null・空の文字は変更なし。形が不正 (JSON の配列でない・要素が JSON のオブジェクトでない・追加に name か base64 が無い / 読めない・
        /// 削除に fileKey が無い・base64 と deleted の両方を持つ) なら KintoneException (InvalidRequest)。
        /// </summary>
        internal static Change ParseChange(string table, string column, object? value)
        {
            var change = new Change();
            if (value is DBNull) value = null;
            if (value == null) return change;
            if (value is not string text) throw Invalid(table, column, "the value is not text");
            if (string.IsNullOrWhiteSpace(text)) return change;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                throw new KintoneException(Message(table, column, "the value is not JSON"), KintoneErrorKind.InvalidRequest, e);
            }
            if (root is not JsonArray array) throw Invalid(table, column, "the value is not a JSON array");

            foreach (var node in array)
            {
                if (node is not JsonObject item) throw Invalid(table, column, "an element is not a JSON object");
                var deleted = item["deleted"] is JsonValue d && d.TryGetValue<bool>(out var flag) && flag;
                var hasBase64 = item["base64"] != null;
                if (deleted && hasBase64) throw Invalid(table, column, "an element has both base64 and deleted");
                if (deleted)
                {
                    var fileKey = GetString(item, "fileKey");
                    if (!IsFileKey(fileKey)) throw Invalid(table, column, "an element to delete has no valid fileKey");
                    if (!change.Deletions.Contains(fileKey!, StringComparer.Ordinal)) change.Deletions.Add(fileKey!);
                    continue;
                }
                if (!hasBase64) continue;

                var name = GetString(item, "name");
                var base64 = GetString(item, "base64");
                if (string.IsNullOrEmpty(name)) throw Invalid(table, column, "an element to add has no name");
                if (base64 == null) throw Invalid(table, column, $"the base64 of '{name}' is not text");
                var content = FromBase64(base64) ?? throw Invalid(table, column, $"the base64 of '{name}' cannot be read");
                var contentType = GetString(item, "contentType");
                change.Additions.Add(new Addition(name, string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType, content));
            }
            return change;
        }

        /// <summary>読んだ値 (kintone が返した並び) にあるファイルの fileKey (並びの順)。読めなければ空。</summary>
        internal static List<string> ReadFileKeys(string? stored)
            => ReadFiles(stored).Select(e => e.FileKey).ToList();

        /// <summary>読んだ値にあるファイル (fileKey と名前)。fileKey の無い要素・deleted が true の要素は入れない。読めなければ空。</summary>
        internal static List<(string FileKey, string? Name)> ReadFiles(string? stored)
        {
            var list = new List<(string, string?)>();
            if (string.IsNullOrWhiteSpace(stored)) return list;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(stored);
            }
            catch (JsonException)
            {
                return list;
            }
            if (root is not JsonArray array) return list;
            foreach (var item in array.OfType<JsonObject>())
            {
                if (item["deleted"] is JsonValue d && d.TryGetValue<bool>(out var flag) && flag) continue;
                var fileKey = GetString(item, "fileKey");
                if (IsFileKey(fileKey)) list.Add((fileKey!, GetString(item, "name")));
            }
            return list;
        }

        /// <summary>
        /// 更新で送る値: 今ある fileKey から消すもの (今あるものと一致したものだけ) を除き、新しい fileKey を後ろに足した並び ([{fileKey}])。
        /// 変わらなければ null (送らない)。
        /// </summary>
        internal static JsonArray? ToUpdateValue(IReadOnlyList<string> stored, IReadOnlyCollection<string> deletions, IReadOnlyList<string> uploaded)
        {
            var kept = stored.Where(e => !deletions.Contains(e, StringComparer.Ordinal)).ToList();
            if (kept.Count == stored.Count && uploaded.Count == 0) return null;
            return ToValue(kept.Concat(uploaded));
        }

        /// <summary>fileKey の並び → kintone の値 ([{fileKey}])。</summary>
        internal static JsonArray ToValue(IEnumerable<string> fileKeys)
            => new(fileKeys.Select(e => (JsonNode?)new JsonObject { ["fileKey"] = e }).ToArray());

        static byte[]? FromBase64(string text)
        {
            if (text.Length % 4 != 0) return null;
            var buffer = new byte[text.Length / 4 * 3];
            return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
        }

        static string? GetString(JsonObject item, string name)
            => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        static string Message(string table, string column, string reason)
            => $"The value of the attachment field '{column}' of kintone app '{table}' is invalid: {reason}. " +
               "Write a JSON array of files to add ({\"name\",\"contentType\",\"base64\"}) and files to delete ({\"fileKey\",\"deleted\":true}).";

        static KintoneException Invalid(string table, string column, string reason)
            => new(Message(table, column, reason), KintoneErrorKind.InvalidRequest);
    }
}
