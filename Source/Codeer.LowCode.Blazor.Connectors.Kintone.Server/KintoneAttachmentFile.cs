namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>kintone の添付ファイル 1 つ (<see cref="KintoneDbAccessor.GetAttachmentAsync"/> の結果)。</summary>
    public sealed class KintoneAttachmentFile
    {
        internal KintoneAttachmentFile(string fileName, byte[] content)
        {
            FileName = fileName;
            Content = content;
        }

        /// <summary>ファイル名 (レコードの値の名前)。応答のヘッダに入れるときはホストが安全な形にする。</summary>
        public string FileName { get; }

        /// <summary>中身。</summary>
        public byte[] Content { get; }
    }
}
