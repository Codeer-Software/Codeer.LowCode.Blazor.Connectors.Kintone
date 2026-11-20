using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Kintone.Mock
{
    public sealed partial class MockKintone
    {
        // ---------------------------------------------------------------- 添付ファイル (file.json)
        //
        // アップロード (POST file.json・multipart の file) は一時保管領域に置いて、アップロードの fileKey ({"fileKey": "..."}) を返す。
        // その fileKey をレコードの追加・更新の添付ファイルの値に書くと添付され、レコードの値ではそれとは別の fileKey (mock-file-N) になる。
        // ダウンロード (GET file.json?fileKey=) はレコードの値の fileKey だけ (アップロードの fileKey は使えない)。
        // 実物の一時保管領域は 3 日で消えるが、モックは消さない。

        /// <summary>アップロードしたファイル (一時保管領域)。</summary>
        public sealed record UploadedFile(string Name, string ContentType, byte[] Content);

        readonly Dictionary<string, UploadedFile> _uploads = new(StringComparer.Ordinal);
        readonly Dictionary<string, byte[]> _fileContents = new(StringComparer.Ordinal);

        /// <summary>一時保管領域にあるファイル (アップロードしてまだ添付していないもの。アップロードの fileKey → ファイル)。</summary>
        public IReadOnlyDictionary<string, UploadedFile> Uploads
        {
            get
            {
                lock (_lock) return new Dictionary<string, UploadedFile>(_uploads);
            }
        }

        /// <summary>添付したファイルの中身 (レコードの値の fileKey)。無ければ null。</summary>
        public byte[]? GetFileContent(string fileKey)
        {
            lock (_lock) return _fileContents.TryGetValue(fileKey, out var content) ? content : null;
        }

        UploadedFile? TakeUpload(string fileKey)
        {
            if (!_uploads.Remove(fileKey, out var upload)) return null;
            return upload;
        }

        //添付したファイルを置き、レコードの値の要素 ({contentType, fileKey, name, size}) を返す
        JsonObject StoreFile(string name, string contentType, byte[] content)
        {
            var fileKey = $"mock-file-{_nextFileId++.ToString(CultureInfo.InvariantCulture)}";
            _fileContents[fileKey] = content;
            return new JsonObject
            {
                ["contentType"] = contentType,
                ["fileKey"] = fileKey,
                ["name"] = name,
                ["size"] = content.LongLength.ToString(CultureInfo.InvariantCulture),
            };
        }

        MockResponse UploadFileApi(MockRequest request, MockRecordedRequest recorded)
        {
            if (request.ContentType == null || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
                !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new MockKintoneException(400, "CB_IL02", "不正なリクエストです。ファイルは multipart/form-data で送ってください。");
            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) throw new MockKintoneException(400, "CB_IL02", "不正なリクエストです。multipart の boundary がありません。");

            //本文はメモリの上にあるので、待たずに読める
            var reader = new MultipartReader(boundary, new MemoryStream(request.BodyBytes ?? Array.Empty<byte>()));
            UploadedFile? file = null;
            while (reader.ReadNextSectionAsync().GetAwaiter().GetResult() is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, "file", StringComparison.Ordinal)) continue;
                var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value;
                if (string.IsNullOrEmpty(name)) name = HeaderUtilities.RemoveQuotes(disposition.FileName).Value?.Replace("\\\"", "\"").Replace("\\\\", "\\");
                if (string.IsNullOrEmpty(name)) throw new MockKintoneException(400, "CB_VA01", "ファイル名がありません。");
                using var memory = new MemoryStream();
                section.Body.CopyTo(memory);
                file = new UploadedFile(name, string.IsNullOrEmpty(section.ContentType) ? "application/octet-stream" : section.ContentType, memory.ToArray());
                break;
            }
            if (file == null) throw new MockKintoneException(400, "CB_VA01", "ファイルがありません。");
            recorded.UploadedFileName = file.Name;
            var key = Guid.NewGuid().ToString("D");
            _uploads[key] = file;
            return Json(200, new JsonObject { ["fileKey"] = key });
        }

        MockResponse DownloadFileApi(MockRequest request, Caller caller)
        {
            var fileKey = request.Query.FirstOrDefault(e => e.Key == "fileKey").Value;
            if (string.IsNullOrEmpty(fileKey)) throw new MockKintoneException(400, "CB_VA01", "fileKey を指定してください。");
            //レコードの値にあって、そのアプリを閲覧できるファイルだけ (アップロードの fileKey は使えない)
            foreach (var app in _apps)
            {
                if (!CanView(caller, app)) continue;
                foreach (var record in app.Records)
                {
                    var files = CollectFiles(record);
                    if (!files.TryGetValue(fileKey, out var file)) continue;
                    var content = _fileContents.TryGetValue(fileKey, out var bytes) ? bytes : Array.Empty<byte>();
                    return new MockResponse { Status = 200, BinaryBody = content, ContentType = file["contentType"]?.GetValue<string>() ?? "application/octet-stream" };
                }
            }
            throw new MockKintoneException(404, "GAIA_BL01", $"指定したファイル（fileKey: {fileKey}）が見つかりません。");
        }
    }
}
