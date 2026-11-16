# kintone API調査 — CLBの通常Moduleから使う機能

2026-11-16 ｜ 対象ライブラリ案 `Codeer.LowCode.Blazor.Connectors.Kintone`

kintoneの既存アプリをデータの正本とし、CLBのDesignerでフィールドを選び、一覧・検索・詳細・登録・更新・削除を使うための調査。サイボウズの日本向け公式仕様と、FileMaker版・NetSuite版のソースを確認した。以下の要求・応答例は設計用の例であり、テナントの採取ログではない。CLB側の設定、対応範囲、受入試験は [仕様・設計案](02_kintone_connector_design.md) にまとめた。

## 1. 既存2製品のどの経験が使えるか

| 論点 | FileMaker版 | NetSuite版 | kintoneでの判断 |
|---|---|---|---|
| データソース | 1ファイルのODataメタ情報から表を列挙する | 接続設定に列挙したレコード種別を取り込む | トークン認証で運用中のフォームを読める。一方、複数アプリ検索APIは同認証に非対応。対象アプリIDを設定する |
| 読取と書込 | CLBが生成するPostgreSQL形式のSQLを解釈し、ODataに変換 | Oracle形式のSQLをSuiteQLに変換し、更新はREST Recordへ送る | CLB側はFileMaker版と同じ方言。接続先にはレコードAPIの `query` とJSONを送る |
| 親子 | 独立した表を外部キーで結べる | サブリストは親の列として編集できる。取引明細の参照表もある | 別アプリのレコード、同じレコード内のテーブル、ルックアップのコピー値を分ける。テーブル更新は親に1回 |
| 排他・採番 | 条件を先に確認してから更新する実装。作成の返却キーが常に同じ形とは限らない | 親の保存と明細の差分をまとめるが、既存仕様には後勝ちになる場合がある | POSTで `id` と `revision` が返る。更新・削除時に、編集開始時の版をkintone自身に検査させる |

ソースは [FileMakerのアクセサ][FM]、[NetSuiteのアクセサ][NS]、[NetSuiteの明細仕様][NSDOC]。kintone側は [認証][AUTH]、[フォーム][FORM]、[アプリ一覧][APPS]、[追加][ADD]、[更新][UPDATE]、[削除][DELETE]。

## 2. 接続・認証・実行主体

通常のベースは `https://{tenant}/k/v1/`、ゲストスペースは `https://{tenant}/k/guest/{spaceId}/v1/`。スペースIDとアプリIDは別の値。以下の表のパスはベースからの相対パス。日本向けの外部連携はスタンダード／ワイドが対象。開発にはサイボウズが提供する開発者ライセンスを使える。[コース比較][PLAN]・[開発者ライセンス][DEV]

| 方式 | 公式仕様と設計上の用途 |
|---|---|
| APIトークン | アプリごとに発行し `X-Cybozu-API-Token` に設定。操作はkintone上でAdministratorによる操作として扱われる。付与したレコード閲覧・追加・編集・削除等の権限を用途に合わせる。初版の設計時・実行時はこの方式 |
| 複数トークン | ルックアップで参照元の値をコピーする場合などにリクエスト単位で追加。1要求につき合計9個まで。関係ないアプリのトークンを常時添付しない |
| OAuth | 利用者の認可とスコープで動かす選択肢。各APIの対応方式は異なる。例としてレコード一覧はOAuth対応、カーソル作成の認証欄にはOAuthがない。本人権限でのAPIアクセスとCLBへのログインは個別に設計する |
| パスワード・セッション | パスワード方式はユーザーの資格情報、セッション方式はkintoneのログイン状態を使用する。独立して稼働するCLB Serverの共有接続にはAPIトークンを選ぶ |

CLBにログインした個人のkintone上の閲覧範囲は、アプリトークンに切り替えても自動継承されない。利用者・Module・操作・対象行／項目の認可はCLB Serverで行い、実利用者IDをCLBの監査に残す。テナントにIP制限などがある場合は、Designerからのメタ情報更新とServerからの実行の双方の接続条件を確認する。[認証][AUTH]・[OAuthスコープ][SCOPE]・[カーソル作成][CURNEW]

## 3. API対応表

| CLBでの目的 | メソッドと相対パス | 結果・制約 | 初版案 |
|---|---|---|---|
| 接続先の名前 | `GET app.json?id=42` | 指定したアプリの情報 | Designerの接続確認に任意利用 |
| 複数アプリの検索 | `GET apps.json` | APIトークン不可 | アプリIDを指定して省く |
| 表定義・選択肢 | `GET app/form/fields.json?app=42` | `properties` とアプリ設定の `revision`。運用環境ではレコード閲覧または追加権限で取得可能。プレビュー設定の取得はアプリ管理権限が必要 | 運用環境を取り込む |
| レイアウト・一覧設定 | `GET app/form/layout.json`、`GET app/views.json` | kintone画面の配置・一覧定義 | CLBの画面はDesignerで持つ。後日、初期配置の取込みに利用可 |
| 単件取得 | `GET record.json?app=42&id=123` | `record`。投影指定はない | 制約なしの詳細取得候補。列限定や行制約があれば複数件APIにID条件を足す |
| 一覧・検索・件数 | `GET records.json` | `fields`、`query`、`totalCount`。件数指定時は文字列、無指定時はnull | 一覧・詳細・COUNTの中心 |
| 一括走査 | `POST / GET / DELETE records/cursor.json` | 作成でIDと件数、取得で `records` と `next`、途中終了時は破棄 | 大量出力の拡張候補 |
| 新規 | `POST record.json` | 戻り値は文字列の `id`・`revision`。存在しないフィールドコードが**無視されて登録が成功し得る** | 公開中フォームと照合して送る |
| 更新 | `PUT record.json` | `id` または `updateKey` の一方を指定。`revision` で競合検出。応答は新しい `revision` | 編集は `id` と元の版で実行 |
| 削除（1件を含む） | `DELETE records.json` | `ids`、対応する `revisions`。成功応答は `{}`。最大100件、一部失敗ならその要求内の削除は取り消される | 1件でも配列を使う |
| 複数件の登録／更新 | `POST / PUT records.json` | 各最大100件。更新は `upsert` の指定も可能。重複禁止の文字列1行または数値が `updateKey` 候補 | 通常編集にupsertを使わない。業務キーによる取込は拡張候補 |
| アプリ横断の一括処理 | `POST bulkRequest.json` | 対応する操作を最大20要求。いずれかが失敗すると同じ一括要求内をロールバック。ゲストスペース境界にも制約がある | SQLのトランザクションと連動させない。独立した一括機能の候補 |
| 添付 | `POST file.json`、`GET file.json?fileKey=…` | アップロードはmultipart。返るキーをレコードへ添付。ダウンロードにはレコード読取で得たキーを使う | 初版は名前等の表示。転送と削除は後続 |
| プロセス管理 | `PUT record/status.json`、`PUT record/assignees.json` | 状態遷移の操作名や担当者を指定。通常のレコードPUTでステータスや作業者は更新できない | 現在値の表示。将来の専用アクションに分ける |

根拠は [API一覧][API]、[アプリ情報][APP]、[アプリ検索][APPS]、[フォーム][FORM]、[単件取得][GET]、[複数件取得][LIST]、[追加][ADD]、[更新][UPDATE]、[削除][DELETE]、[複数件登録][ADDS]、[複数件更新][UPDATES]、[一括処理][BULK]、[ファイルアップロード][UPLOAD]、[ダウンロード][DOWNLOAD]、[ステータス更新][STATUS]。

### 受注1件のHTTP対応例

以下は通常スペースの例。ID・版はサーバーの応答を次の操作へ引き継ぐ。認証ヘッダーは省略した。フィールドコード `CustomerName` は対象アプリに作成済みとする。

| 操作 | 要求 | 成功応答の例 |
|---|---|---|
| 作成 | `POST /k/v1/record.json` · `{"app":42,"record":{"CustomerName":{"value":"大阪商事"}}}` | `{"id":"123","revision":"1"}` |
| 表示 | `GET /k/v1/record.json?app=42&id=123` | `record.$id.value` が `"123"`、`record.$revision.value` が `"1"`、`record.CustomerName.value` が `"大阪商事"` |
| 更新 | `PUT /k/v1/record.json` · `{"app":42,"id":"123","revision":"1","record":{"CustomerName":{"value":"大阪商事株式会社"}}}` | `{"revision":"2"}` |
| 削除 | `DELETE /k/v1/records.json` · `{"app":42,"ids":["123"],"revisions":["2"]}` | `{}` |

kintone画面の保存ボタンで動くJavaScriptカスタマイズは、CLB ServerからのREST呼出しでは動かない。導入先で入力チェックや自動補完をこのイベントに書いている場合は、必要な業務ルールをCLB側の入力・サーバー検証にも反映する。サーバーが評価する計算列・必須設定とは個別に試験する。[編集画面の保存イベント][EVENT]・[レコード更新][UPDATE]

## 4. スキーマと値の読み方

`properties` のキー／`code` を外部列名として保存する。表示ラベル、フィールドコード、CLBのField名は別々に保持する。テーブルは `fields` を再帰的に読む。選択肢はラベルと `index` を保持して順序を復元。ルックアップの `relatedApp`、`relatedKeyField`、`fieldMappings` から参照元・コピー先を識別する。レコードの値は原則 `{ "type": "...", "value": ... }`。送信時は通常 `{ "value": ... }`。フォーム定義とデータの形式を別々に扱う。[フォーム][FORM]・[フィールド形式][FIELD]

| kintone型・値 | CLBの取込・保存案 |
|---|---|
| `$id` / `$revision` | レコード内では文字列値。アプリ内の内部IDとレコードの版を表す。フォームの通常列に存在するという仮定を置かず、専用列として扱う。`RECORD_NUMBER` はアプリコードの接頭辞を含み得る表示値 |
| `SINGLE_LINE_TEXT` / `MULTI_LINE_TEXT` / `LINK` | Text系。文字列1行の自動計算、ルックアップのコピー先は表示専用。リンクはリンク種別に従って検証する |
| `NUMBER` / `CALC` | 数値値の文字列表現を正確に扱う。空はゼロと区別。NUMBERは丸めずに表現できる範囲で編集、CALCは読取専用。時刻・計算エラー等で十進数として扱えない計算結果は表示文字列で保持 |
| `DATE` / `TIME` / `DATETIME` | 日付、時刻、瞬間を区別。日付だけをUTC変換しない。日時はUTCで送受信し、画面表示で利用者の時間帯を適用 |
| `RADIO_BUTTON` / `DROP_DOWN` | 1個の選択文字列。選択肢のラベルがAPIで送る値。ラジオには明示クリアを提供しない |
| `CHECK_BOX` / `MULTI_SELECT` | 文字列の配列。チェックボックスは複数選択できるためBooleanにしない。未対応の編集UIでは読み取り専用にする |
| `USER_SELECT` / `ORGANIZATION_SELECT` / `GROUP_SELECT` | コード・表示名の配列。編集に使う識別子はcode。初版は表示用、将来の選択UIで候補取得権限を別に扱う |
| `CREATOR` / `MODIFIER` / `CREATED_TIME` / `UPDATED_TIME` | ユーザーオブジェクトまたは日時。コネクターは表示専用。特権を使った作成時の監査列指定は対象外 |
| `STATUS` / `STATUS_ASSIGNEE` / `CATEGORY` | 有効設定に応じて表示。通常CRUDの書込対象から除く |
| `SUBTABLE` | 親の値は `[{id,value:{…}}]`。一覧APIのfieldsには親のテーブルコードを指定。行内の1列だけを投影する指定はできない |
| ルックアップ / `REFERENCE_TABLE` | ルックアップは元の型＋設定として検出。RESTで返るキー値を、参照先のレコードIDと決めつけない。関連レコード一覧は参照先アプリID・照合項目・絞込を使った別検索の候補 |
| `FILE` / `RICH_TEXT` / 装飾・未知型 | 添付はメタ情報、リッチテキストは初版では安全なテキスト表示。ラベル・罫線はデータ列にしない。未知型は取込診断に出す |

未設定の受信値には `""`、`null`、`[]` がある。RESTで明示クリアする例は、文字列・数値・日時・日付・時刻の各対応型には `null`、複数選択には `[]`。ラジオのように空指定ができない型もある。**要求から省く／値を消す／取得できなかった**を別の状態で持つ。[空値の型別仕様][FIELD]・[更新できない列][UPDATE]

## 5. 通常検索と大量走査

| CLBからの検索 | APIへの変換例・制限 |
|---|---|
| 文字列1行の等価・集合 | `CustomerCode = "C-001"`、`CustomerCode in ("C-001","C-002")`。フィールドコードは取込定義のものだけを使う |
| 数値・日付の範囲 | `Amount >= 1000`、`OrderDate >= "2026-11-01"`。数値はカルチャー非依存、日付はISO形式 |
| 単一選択の等価 | `Stage in ("受注")`。ラジオ・ドロップダウン・チェックボックスの対応演算子は型別に扱う |
| キーワード検索 | `CustomerName like "大阪"`。kintoneの単語検索にはSQLの任意部分一致と差がある。ヒット10万件で打切りの警告が返り得る |
| 並べ替えとページ | 例 `order by OrderDate desc, $id asc limit 50 offset 0`。指定可能な列だけ許可。安定した同順位の順序と、更新中のデータのスナップショットは別の性質 |
| 件数 | ページの条件と同じ `query` で `totalCount=true`。行数・最後のページ長から総件数を推定しない |
| 非対応の例 | 任意SQL、任意JOIN、SQLのGROUP BY／SUM、型をまたぐ照合、前方・後方一致の厳密互換。全件ダウンロードして部分的に代用すると件数・ページ・権限の整合が崩れる |

文字列リテラルの `"` とバックスラッシュはkintoneの規則でエスケープし、その後でJSONまたはURIにエンコードする。URLで `fields[n]` を渡す場合の添字は0〜99、ボディの場合は最大1,000コード。長い条件・投影には公式の `POST` + `X-HTTP-Method-Override: GET` が利用できる。[クエリと型別演算子][QUERY]・[レコード一覧][LIST]・[GETの3方式][GETWAYS]

一覧は1回最大500件、既定100件、offsetは10,000まで。通常のページジャンプにはlimit/offsetを使う。上限以降は「条件を絞ってください」と明示する。全件出力を追加する場合は、任意の対応順序で逐次走査するカーソル、またはID昇順固定の `$id > 前回ID` を選べる。後者を任意順のページ番号に流用しない。[一覧][LIST]・[クエリ][QUERY]

カーソルは1ドメイン10個まで、有効期限は最終作成／取得から10分。作成時に対象レコードが決まり、値は各取得時点のもの。取得終了は `next` で判断し、途中中断で解放する。カーソルGETは位置が進むので、応答を失った場合に通常の参照GETと同じ自動再試行をすると取りこぼし得る。[作成][CURNEW]・[取得][CURNEXT]

## 6. 保存時にデータを失わない条件

**版を渡す。** POSTは採番IDと版を返す。既存行のPUTは画面で読んだ `$revision` をトップレベルの `revision` に、DELETEは `ids[i]` に対応する `revisions[i]` に渡す。省略・−1では検査されない。版の不一致は再読込を求める。フォーム取得の `revision` はアプリ設定の版なので、ここには渡せない。[追加][ADD]・[更新][UPDATE]・[削除][DELETE]・[フォーム][FORM]

**明細を変えたときだけテーブルを送る。** テーブル列をPUTから外せば明細は保持される。テーブルを送ると、載せなかった既存行は削除され、IDのない行は追加され、配列順が表示順になる。残す行はID、変更行はIDと変更列、新規行は入力列で表せる。公式の操作例では、存在しない行IDを指定してもエラーにならず新規行になる場合がある。残す既存行のIDが揃わない画面ではテーブル編集を確定させない。[更新][UPDATE]・[公式テーブル操作例][TABLE]

次はアプリ42、親123、編集時の版7の例。17行目を変更し、18行目を保持、新規行を追加する。処理名やフィールドコードは例示。HTTP本文の形式として利用できる。

```http
PUT /k/v1/record.json
Content-Type: application/json
X-Cybozu-API-Token: <app-token>

{
  "app": 42, "id": "123", "revision": "7",
  "record": {
    "Lines": { "value": [
      { "id": "17", "value": { "Quantity": { "value": "3" } } },
      { "id": "18" },
      { "value": { "Product": { "value": "商品C" }, "Quantity": { "value": "1" } } }
    ] }
  }
}
```

**添付は別の保存手順を持つ。** 追加ファイルを一時領域へアップロードし、親の更新で既存の残すfileKeyと新しいfileKeyを列挙する。未添付ファイルは3日で消える。ファイルのアップロード成功と親レコードの更新成功を別々に記録する。[ファイルのアップロード][UPLOAD]・[ダウンロード][DOWNLOAD]・[更新][UPDATE]・[制限値][LIMIT]

**ルックアップは値をコピーする。** 元アプリのキーが重複禁止などによって一意に特定できること、参照元の読み取り権限、必要なトークンを確認する。コピー先項目は通常PUTで書けない。過去のコピー値を見せるだけなら、元アプリへの都度問い合わせは不要。[データ設計][LOOKUP]・[認証][AUTH]・[更新][UPDATE]

## 7. 運用と最初の疎通

APIの同時リクエストは1ドメイン100まで。他の連携と共用する。日本向け1アプリの日次枠の基準はスタンダード1万、ワイド10万で、契約条件を確認する。一括処理も内部で実行した各APIが日次枠に加算される。アプリ管理画面の使用量とコネクターのレコード一覧・メタ情報・書込の呼出回数を付き合わせる。[共通仕様][COMMON]・[制限値][LIMIT]・[コース比較][PLAN]

開発者ライセンス等で専用アプリを用意し、文字列1行のコードを `CustomerName` に設定、既知の1件を登録する。APIトークンに閲覧権限を付け、**アプリの更新**で設定を反映する。次のPowerShellは設定したアプリのフォームと先頭1件を読む。環境変数にはテナントのHTTPS起点、アプリID、トークンを設定しておく。ゲストスペースは `/k/v1` の部分を読み替える。[開発環境][DEV]・[フォーム][FORM]・[一覧][LIST]

```powershell
$api = $env:KINTONE_ORIGIN.TrimEnd('/') + '/k/v1'
$appId = [long]$env:KINTONE_APP_ID
$headers = @{ 'X-Cybozu-API-Token' = $env:KINTONE_API_TOKEN }
$form = Invoke-RestMethod -Headers $headers -Uri "$api/app/form/fields.json?app=$appId"
$form.properties.CustomerName | Select-Object code, type, label
$query = [Uri]::EscapeDataString('order by $id asc limit 1')
$url = "$api/records.json?app=$appId&query=$query&totalCount=true" +
       '&fields%5B0%5D=%24id&fields%5B1%5D=%24revision&fields%5B2%5D=CustomerName'
$page = Invoke-RestMethod -Headers $headers -Uri $url
$page.records | ForEach-Object {
    [pscustomobject]@{ Id = $_.'$id'.value; Revision = $_.'$revision'.value; Name = $_.CustomerName.value }
}
```

期待結果は、指定のフィールドコード・型と、投入した顧客の名前、ID、版。次に2画面で同じ版を開き、一方を更新後、他方の古い版での更新・削除が失敗して先の変更が残ることを確認する。明細保持・権限・部分失敗の再開は仕様書の受入試験に続く。

## 参照先

[サイボウズ公式API索引][API]・[共通仕様][COMMON]・[フィールド形式][FIELD]・[クエリ仕様][QUERY]・[開発者ライセンス][DEV]。既存実装へのリンクは調査時のコミットに固定した。

[FM]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Server/FileMakerDbAccessor.cs
[NS]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs
[NSDOC]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Docs/NetSuite.md
[API]: https://cybozu.dev/ja/kintone/docs/rest-api/
[AUTH]: https://cybozu.dev/ja/kintone/docs/rest-api/overview/authentication/
[COMMON]: https://cybozu.dev/ja/kintone/docs/rest-api/overview/kintone-rest-api-overview/
[APP]: https://cybozu.dev/ja/kintone/docs/rest-api/apps/get-app/
[APPS]: https://cybozu.dev/ja/kintone/docs/rest-api/apps/get-apps/
[FORM]: https://cybozu.dev/ja/kintone/docs/rest-api/apps/form/get-form-fields/
[FIELD]: https://cybozu.dev/ja/kintone/docs/overview/field-types/
[QUERY]: https://cybozu.dev/ja/kintone/docs/overview/query/
[GET]: https://cybozu.dev/ja/kintone/docs/rest-api/records/get-record/
[LIST]: https://cybozu.dev/ja/kintone/docs/rest-api/records/get-records/
[GETWAYS]: https://cybozu.dev/ja/kintone/tips/best-practices/data-acquisition-operation/rest-api-get-3-ways/
[CURNEW]: https://cybozu.dev/ja/kintone/docs/rest-api/records/create-cursor/
[CURNEXT]: https://cybozu.dev/ja/kintone/docs/rest-api/records/get-cursor/
[ADD]: https://cybozu.dev/ja/kintone/docs/rest-api/records/add-record/
[ADDS]: https://cybozu.dev/ja/kintone/docs/rest-api/records/add-records/
[UPDATE]: https://cybozu.dev/ja/kintone/docs/rest-api/records/update-record/
[UPDATES]: https://cybozu.dev/ja/kintone/docs/rest-api/records/update-records/
[DELETE]: https://cybozu.dev/ja/kintone/docs/rest-api/records/delete-records/
[BULK]: https://cybozu.dev/ja/kintone/docs/rest-api/records/bulk-request/
[TABLE]: https://cybozu.dev/ja/kintone/tips/development/customize/table/table-operations-techniques-on-record-update/
[UPLOAD]: https://cybozu.dev/ja/kintone/docs/rest-api/files/upload-file/
[DOWNLOAD]: https://cybozu.dev/ja/kintone/docs/rest-api/files/download-file/
[STATUS]: https://cybozu.dev/ja/kintone/docs/rest-api/records/update-status/
[LOOKUP]: https://cybozu.dev/ja/kintone/tips/best-practices/colum/basic-data-design-in-kintone/
[SCOPE]: https://cybozu.dev/ja/common/docs/oauth-client/scope-kintone/
[LIMIT]: https://jp.kintone.help/k/ja/admin/limitation/limit
[PLAN]: https://kintone.cybozu.co.jp/price
[DEV]: https://cybozu.dev/ja/kintone/developer-license/
[EVENT]: https://cybozu.dev/ja/kintone/docs/js-api/events/edit/edit-submit-event/
