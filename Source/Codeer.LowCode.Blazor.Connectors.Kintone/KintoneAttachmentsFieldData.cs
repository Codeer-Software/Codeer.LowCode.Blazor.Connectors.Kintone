using Codeer.LowCode.Blazor.Repository.Data;

namespace Codeer.LowCode.Blazor.Connectors.Kintone
{
    /// <summary>
    /// 添付のフィールドのデータ。Value は kintone の添付ファイルのフィールド 1 つ分と同じ形の JSON の配列の文字 (ファイルが無ければ null)。
    /// 読んだ値は [{"contentType","fileKey","name","size"}...]。保存で送る値には、足すファイル {"name","contentType","base64"} と消すファイル {"fileKey","deleted":true} が入る。
    /// </summary>
    public class KintoneAttachmentsFieldData() : ValueFieldDataBase<string>(typeof(KintoneAttachmentsFieldData).FullName!), ICloneable<KintoneAttachmentsFieldData>
    {
        public KintoneAttachmentsFieldData Clone() => (KintoneAttachmentsFieldData)MemberwiseClone();
    }
}
