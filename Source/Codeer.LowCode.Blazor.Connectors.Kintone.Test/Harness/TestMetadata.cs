using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness
{
    /// <summary>
    /// テスト用のフォームの設定 (app/form/fields.json の properties)。架空のアプリ。形は kintone の公式の仕様の説明から書いたもので、実物の応答の採取ではない。
    /// </summary>
    public static class TestMetadata
    {
        public const long CustomerApp = 11;
        public const long RegionApp = 12;
        public const long CountryApp = 13;
        public const long OrderApp = 14;

        /// <summary>接続文字列の Apps。</summary>
        public const string Apps = "Customer:11,Region:12,Country:13,Order:14";

        /// <summary>顧客 (Code = 重複禁止の文字列 1 行・RegionCode で地域へ)。</summary>
        public const string Customer = """
            {
              "Code": { "type": "SINGLE_LINE_TEXT", "code": "Code", "label": "顧客コード", "required": true, "unique": true, "expression": "" },
              "Name": { "type": "SINGLE_LINE_TEXT", "code": "Name", "label": "顧客名", "required": false, "unique": false },
              "Note": { "type": "MULTI_LINE_TEXT", "code": "Note", "label": "メモ" },
              "Body": { "type": "RICH_TEXT", "code": "Body", "label": "本文" },
              "Url": { "type": "LINK", "code": "Url", "label": "URL", "protocol": "WEB" },
              "CreditLimit": { "type": "NUMBER", "code": "CreditLimit", "label": "与信限度額", "unique": "false" },
              "IsActive": { "type": "NUMBER", "code": "IsActive", "label": "有効" },
              "Deleted": { "type": "NUMBER", "code": "Deleted", "label": "削除" },
              "StartDate": { "type": "DATE", "code": "StartDate", "label": "取引開始日" },
              "OpenTime": { "type": "TIME", "code": "OpenTime", "label": "開店時刻" },
              "LastContact": { "type": "DATETIME", "code": "LastContact", "label": "最終連絡" },
              "Rank": {
                "type": "DROP_DOWN", "code": "Rank", "label": "ランク",
                "options": { "B": { "label": "B", "index": "1" }, "A": { "label": "A", "index": "0" }, "C": { "label": "C", "index": "2" } }
              },
              "Tags": {
                "type": "CHECK_BOX", "code": "Tags", "label": "タグ",
                "options": { "VIP": { "label": "VIP", "index": "0" }, "新規": { "label": "新規", "index": "1" } }
              },
              "Owner": { "type": "USER_SELECT", "code": "Owner", "label": "担当者" },
              "RegionCode": { "type": "SINGLE_LINE_TEXT", "code": "RegionCode", "label": "地域コード" },
              "Total": { "type": "CALC", "code": "Total", "label": "合計", "expression": "CreditLimit*2", "format": "NUMBER" },
              "Label": { "type": "SINGLE_LINE_TEXT", "code": "Label", "label": "表示名", "expression": "Code & Name" },
              "RecordNo": { "type": "RECORD_NUMBER", "code": "RecordNo", "label": "レコード番号" },
              "Creator": { "type": "CREATOR", "code": "Creator", "label": "作成者" },
              "Created": { "type": "CREATED_TIME", "code": "Created", "label": "作成日時" },
              "Updated": { "type": "UPDATED_TIME", "code": "Updated", "label": "更新日時" },
              "Status": { "type": "STATUS", "code": "Status", "label": "ステータス", "enabled": false },
              "Attachments": { "type": "FILE", "code": "Attachments", "label": "添付" },
              "顧客 名": { "type": "SINGLE_LINE_TEXT", "code": "顧客 名", "label": "日本語の名前" },
              "Spacer": { "type": "SPACER", "code": "Spacer" },
              "Related": { "type": "REFERENCE_TABLE", "code": "Related", "label": "関連" },
              "Group1": { "type": "GROUP", "code": "Group1", "label": "グループ" }
            }
            """;

        /// <summary>地域 (Code = 重複禁止・CountryId で国の $id へ)。</summary>
        public const string Region = """
            {
              "Code": { "type": "SINGLE_LINE_TEXT", "code": "Code", "label": "地域コード", "unique": true },
              "RegionName": { "type": "SINGLE_LINE_TEXT", "code": "RegionName", "label": "地域名" },
              "CountryId": { "type": "NUMBER", "code": "CountryId", "label": "国" }
            }
            """;

        /// <summary>国 ($id がキー)。</summary>
        public const string Country = """
            {
              "CountryName": { "type": "SINGLE_LINE_TEXT", "code": "CountryName", "label": "国名" }
            }
            """;

        /// <summary>受注 (ルックアップで顧客名をコピー・テーブル Lines)。</summary>
        public const string Order = """
            {
              "OrderNo": { "type": "SINGLE_LINE_TEXT", "code": "OrderNo", "label": "受注番号", "unique": true },
              "CustomerCode": {
                "type": "SINGLE_LINE_TEXT", "code": "CustomerCode", "label": "顧客コード",
                "lookup": {
                  "relatedApp": { "app": "11", "code": "" },
                  "relatedKeyField": "Code",
                  "fieldMappings": [ { "field": "CustomerName", "relatedField": "Name" } ],
                  "lookupPickerFields": [ "Code", "Name" ]
                }
              },
              "CustomerName": { "type": "SINGLE_LINE_TEXT", "code": "CustomerName", "label": "顧客名" },
              "Lines": {
                "type": "SUBTABLE", "code": "Lines", "label": "明細",
                "fields": {
                  "Product": { "type": "SINGLE_LINE_TEXT", "code": "Product", "label": "商品" },
                  "Qty": { "type": "NUMBER", "code": "Qty", "label": "数量" },
                  "Price": { "type": "NUMBER", "code": "Price", "label": "単価" },
                  "Amount": { "type": "CALC", "code": "Amount", "label": "金額", "expression": "Qty*Price", "format": "NUMBER" },
                  "ShipDate": { "type": "DATE", "code": "ShipDate", "label": "出荷日" },
                  "Options": { "type": "CHECK_BOX", "code": "Options", "label": "オプション", "options": { "包装": { "label": "包装", "index": "0" } } }
                }
              }
            }
            """;

        public static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

        /// <summary>偽物にアプリ全部のフォームを入れる。</summary>
        public static void AddForms(FakeKintone fake)
        {
            fake.Forms[CustomerApp] = Parse(Customer);
            fake.Forms[RegionApp] = Parse(Region);
            fake.Forms[CountryApp] = Parse(Country);
            fake.Forms[OrderApp] = Parse(Order);
        }
    }
}
