# kintone のモック

kintone の環境が無くても、コネクターを組み込んだアプリとデザイナを動かして試すための、手元で動く kintone REST API の代わりです。
自分のマシンで起動し、コネクターの接続先をモックに向けると、次の操作を通しで試せます。

- フォームの設定の取得 (フィールドの種類・必須・重複禁止・選択肢・ルックアップ・計算式・テーブル)
- レコードの取得 (クエリの条件・`order by`・`limit` / `offset`・件数・取得するフィールドの指定)
- レコードの追加・更新 (ID か重複禁止のフィールドで指定)・削除と、そのときのエラー (必須・重複禁止・選択肢・型・書き込めないフィールド・版の不一致)
- ルックアップのコピー・計算フィールドの再計算・テーブルの行の置き換え
- API トークン認証・パスワード認証 (利用者のログインの確かめ)・ゲストスペース

データはモックのプロセスの中 (メモリ) だけにあります。止めると、最初のデータに戻ります。

> **モックで動いても、実物の kintone で動くとは限りません。** 下の「実物と違う点・実物で確かめていない点」を読んでください。

## 起動

.NET 8 の SDK が要ります。リポジトリのフォルダで:

```
dotnet run --project Source/Kintone.Mock
```

`http://localhost:5084` で待ち受けます (FileMaker のモックの 5082・NetSuite のモックの 5080 と重ならないようにしています)。設定を変えるとき:

```
dotnet run --project Source/Kintone.Mock -- --urls http://localhost:5090 --data C:\work\my-mock-data.json
```

| 引数 | 内容 |
|---|---|
| `--urls` | 待ち受ける URL (`;` 区切りで複数)。既定 `http://localhost:5084` |
| `--data` | アプリ・利用者・初期データのファイル。既定はモックに付いている `mock-data.json` (架空のサンプル) |

起動すると、アプリ (ID・名前・パス)・API トークン・利用者の一覧がコンソールに出ます。終了は Ctrl+C です。

コネクターの接続先は `Host=http://localhost:5084` と、アプリ ID・API トークン (付属のデータは下の表) です。接続文字列の書き方はコネクターの文書に従ってください。トークン・パスワードは架空の値で、実物の kintone とは関係ありません。

## テストから使う

モックは `MockKintone` (アプリ・データ・設定を持つ) と、HTTP に結び付ける拡張メソッド `MapMockKintone` に分かれています。
テストごとにインスタンスを作ると、それぞれが自分のデータを持ち、互いに影響しません。

```csharp
var mock = MockKintone.FromJson(json);                  // FromFile(path) / FromObject(匿名型など) でも作れる
mock.SetTime(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();                        // Microsoft.AspNetCore.TestHost
var app = builder.Build();
app.MapMockKintone(mock);
await app.StartAsync();
var client = app.GetTestClient();
```

TestServer で立てると、モックが接続を切ったとき (`DisconnectNext` / `DisconnectAfterWrite`) にクライアントへ届く例外は `HttpRequestException` ではなく `OperationCanceledException` などになります。実際の通信で接続が切れたときと同じにするには、TestServer の `CreateHandler()` を `DelegatingHandler` で包んで置き換えてください。

| メンバー | 内容 |
|---|---|
| `FromJson(string)` / `FromFile(string)` / `FromObject(object)` | `mock-data.json` の形から作る。誤りがあれば `InvalidDataException` (どこが違うかを含む) |
| `Apps` / `GetApp(appId)` | アプリ (`MockApp`: `Id`・`Name`・`Code`・`GuestSpaceId`・`Fields`・`Statuses`・`Tokens`・`FormRevision`)。`MockField`: `Code`・`Type`・`Label`・`Required`・`Unique`・`Options`・`DefaultValue`・`DefaultNowValue`・`Expression`・`Format`・`Protocol`・`Lookup`・`Fields` (テーブルの中)。`MockApiToken`: `Token`・`Permission` (`MockPermission` の View / Add / Edit / Delete) |
| `Users` | パスワード認証の利用者 (`MockUser`: `Code` (ログイン名)・`Password`・`Name`・`Permissions` (アプリ ID → 権限。null なら全部))。足す・消すしてよい |
| `Clock` / `SetTime(time)` / `AdvanceTime(span)` | 作成日時・更新日時に使う時計。既定は今の時刻。`SetTime` で止め、`AdvanceTime` で進める (止めた時計でも動いている時計でも) |
| `GetRecord(appId, recordId)` / `GetRecords(appId)` | レコードを `records.json` の 1 件と同じ形 (`JsonObject`) で。無ければ null / ID の昇順 |
| `AddRecord(appId, record, user = null)` | レコードを足す (追加の API と同じ検証・既定値・ルックアップ・計算。権限は見ない)。`record` は `{"コード": {"value": 値}}` か `{"コード": 値}` (データのファイルの `records` と同じ書き方・`$id` などの指定も可)。作成者は `user` (省略すると Administrator)。戻り値はレコード ID。足せなければ `InvalidOperationException` |
| `UpdateRecord(appId, recordId, record, user = null)` | レコードを書き換える (更新の API と同じ扱い: 送ったフィールドだけ・テーブルは送った行で置き換え・版を 1 上げる。権限と版は見ない)。別の利用者が先に更新した状況を作るのに使う。戻り値は新しい版 |
| `DeleteRecord(appId, recordId)` | レコードを消す。無ければ false |
| `Uploads` / `GetFileContent(fileKey)` | 一時保管領域のファイル (アップロードの `fileKey` → `UploadedFile`: `Name`・`ContentType`・`Content`。添付したものは無くなる) / 添付したファイルの中身 (レコードの値の `fileKey`。無ければ null) |
| `SetFields(appId, fields)` | フォームの定義 (システムでないフィールド) を差し替える。`fields` はデータのファイルの `fields` と同じ形。無くなったコードの値はレコードから消え、増えたフィールドは既存のレコードでは未入力。フォームの版が 1 上がる |
| `RenameField(appId, oldCode, newCode)` | フィールドコードを変える。値・計算式・ルックアップ (このアプリのコピー先・ほかのアプリからの参照) も付いて行く。フォームの版が 1 上がる |
| `Requests` / `ClearRequests()` | 受けた要求の記録 (`MockRecordedRequest`: `Method`・`EffectiveMethod` (`X-HTTP-Method-Override` を当てたあと)・`MethodOverride`・`Path`・`PathAndQuery` (受けたまま)・`Api` (`records.json` など)・`GuestSpaceId`・`Query` (デコード済み)・`Body`・`ApiTokens`・`LoginName` (パスワードは記録しない)・`UploadedFileName` (アップロードのファイル名))。認証に失敗した要求・障害を仕込んだ要求も含む |
| `FailNext(status, body = null, when = null, count = 1)` | 次の count 回の要求 (`when` を指定すれば、それに合う要求だけ) に、何も実行せずに status を返す。`body` を省略すると kintone の形のエラー (`code` は `MOCK_FAULT`) |
| `DisconnectNext(when = null, count = 1)` | 次の count 回の要求で、何も実行せずに接続を切る |
| `DelayNext(delay, when = null, count = 1)` | 次の count 回の要求を delay だけ待ってから処理する (タイムアウトを試す) |
| `FailAfterWrite(status = 500, body = null, when = null, count = 1)` | 次の count 回の書き込み (追加・更新・削除のうち成功したもの) を、実行したうえで status を返す。`body` を省略すると kintone の形でない応答 (`text/html`。途中の機器が返す形。kintone のエラーコードの付いた応答は「処理されなかった」の意味になるため) |
| `DisconnectAfterWrite(when = null, count = 1)` | 次の count 回の書き込み (成功したもの) を、実行したうえで接続を切る (書き込みは済んだのに応答だけ失われる状況) |
| `ClearFaults()` | 仕込んだ障害を全部取り消す |
| `HandleAsync(MockRequest)` | 要求 1 つを HTTP を通さずに処理する (`MapMockKintone` が使っている) |

`when` の例: `r => r.Api == "record.json" && r.EffectiveMethod == "PUT"`。
障害は「記録 → 遅延 → 実行しない障害 → 認証 → 処理 → 書いたあとの障害」の順に見ます。書き込みが検証などで失敗した要求には、書いたあとの障害は使われません。

## 応える要求

パスは `/k/v1/...` と、ゲストスペースの `/k/guest/{スペース ID}/v1/...` です。ゲストスペースのアプリはゲストスペースのパスでだけ、ほかのアプリは `/k/v1/` でだけ扱えます。下の表のほかの API (`record.json` の GET・`app.json`・カーソル・一括など) は 404 (`MOCK_NOT_SUPPORTED`) です。

| 要求 | 応答 |
|---|---|
| `GET app/form/fields.json?app={id}` | `{"properties": {...}, "revision": "1"}`。閲覧か追加の権限が要る |
| `GET records.json?app=..&query=..&fields[0]=..&totalCount=true` | `{"records": [...], "totalCount": "12"}` (`totalCount` を指定しなければ `null`) |
| `POST records.json` + `X-HTTP-Method-Override: GET`、本文 `{"app":1,"query":"...","fields":[...],"totalCount":true}` | 上と同じ |
| `GET apps.json?limit=1` | `{"apps": [...]}`。パスワード認証だけ (API トークンは 403)。その利用者が閲覧できる、そのパス (通常 / ゲストスペース) のアプリ |
| `POST record.json` 本文 `{"app":1,"record":{...}}` | `{"id":"12","revision":"1"}` |
| `PUT record.json` 本文 `{"app":1,"id":"12","revision":"3","record":{...}}` か `{"app":1,"updateKey":{"field":"Code","value":"C-1"},...}` | `{"revision":"4"}`。`revision` を省略するか `-1` なら版を見ない |
| `DELETE records.json?app=1&ids[0]=12&revisions[0]=3` (本文 `{"app":1,"ids":["12"],"revisions":["3"]}` も可) | `{}`。1 件でも無い・版が違えば、どれも消さない。100 件まで |
| `POST file.json` (multipart/form-data の `file`) | `{"fileKey":"..."}`。一時保管領域に置く (下の「実物と違う点」の添付ファイル) |
| `GET file.json?fileKey=..` | ファイルの中身 (`Content-Type` はファイルの種類)。レコードの値の `fileKey` だけ |

- `app`・`id`・`revision` は数値でも文字でも受けます。本文のある要求は `Content-Type: application/json` が要ります (無ければ 400)
- クエリ文字列の配列は `fields[0]`・`ids[0]` のように添字を付けます (添字は 0〜99)

### 認証と権限

| ヘッダー | 内容 |
|---|---|
| `X-Cybozu-Authorization: base64(ログイン名:パスワード)` | `users` の利用者。両方のヘッダーがあればこちらを使う |
| `X-Cybozu-API-Token: tokA,tokB` | カンマ区切りで 9 個まで。操作するアプリのトークンが含まれていて、そのトークンに要る権限 (取得 = 閲覧、追加 = 追加、更新 = 編集、削除 = 削除) があること。同じアプリのトークンを 2 つ付けるとエラー |

- ルックアップのキーに値を書く (追加・更新) には、参照元のアプリのトークン (閲覧) も一緒に付けます。無ければ `GAIA_LO04`
- 作成者・更新者は、パスワード認証ならその利用者、API トークンなら `Administrator` になります

### クエリ

`条件 order by フィールド asc|desc, ... limit N offset M` (どれも省略可。並びはこの順)。

- 条件は `フィールドコード 演算子 値` を `and` / `or` と括弧で組み合わせます (`and` が `or` より先)
- 値は `"文字"` (`\"` と `\\` でエスケープ) か数値。`= ""` と `in ("")` で未入力を探せます
- `limit` は 0〜500 (既定 100)、`offset` は 0〜10000。超えたら 400
- `order by` が無ければレコード ID の降順。同じ値の行もレコード ID の降順
- `$id` とシステムのフィールド (レコード番号・作成者・作成日時・更新者・更新日時・ステータス・作業者) も使えます。`$revision` は条件にも並びにも使えません
- 関数 (`LOGINUSER()`・`NOW()`・`TODAY()` など) は 400

フィールドの種類ごとに使える演算子 (公式のクエリ記法の表に合わせたもの。使えない組み合わせは 400):

| 種類 | 演算子 |
|---|---|
| `$id`・レコード番号 | `=` `!=` `>` `<` `>=` `<=` `in` `not in` |
| 文字列 1 行・リンク | `=` `!=` `in` `not in` `like` `not like` `is empty` `is not empty` |
| 数値・計算 | `=` `!=` `>` `<` `>=` `<=` `in` `not in` `is empty` `is not empty` |
| 文字列複数行・リッチエディター | `like` `not like` `is empty` `is not empty` |
| チェックボックス・複数選択・ドロップダウン・ユーザー選択 | `in` `not in` `is empty` `is not empty` |
| ラジオボタン・作成者・更新者・作業者 | `in` `not in` |
| ステータス | `=` `!=` `in` `not in` |
| 日付・時刻・日時 | `=` `!=` `>` `<` `>=` `<=` `is empty` `is not empty` |
| 作成日時・更新日時 | `=` `!=` `>` `<` `>=` `<=` |
| 添付ファイル | `like` `not like` `is empty` `is not empty` (ファイル名) |
| テーブルの中のフィールド | 上の表から `=` / `!=` を除き、代わりに `in` / `not in` (どれかの行が合えば、そのレコードが合う) |
| テーブル自体・カテゴリー | 使えない |

- 未入力の行は `!=`・`not in`・`not like` で「合う」(未入力も返す)。大小比較では返りません
- 日付の値は `"2026-01-31"`、時刻は `"09:30"`、日時は `"2026-01-31T09:30:00Z"` (`+09:00` / `+0900` も可)。日時に日付だけを書くと、UTC のその日として比べます
- 並べ替えできない種類: 文字列複数行・リッチエディター・チェックボックス・複数選択・添付ファイル・テーブル (とテーブルの中)・ユーザー選択・作業者・カテゴリー
- 文字の比較・並びは文字コード順 (大文字・小文字を区別)。`like` は部分一致 (英字の大文字・小文字は区別しない)。未入力は昇順で先頭

### レコードの値の形

| 種類 | `value` |
|---|---|
| 文字列 1 行・複数行・リッチエディター・リンク | 文字 (未入力は `""`) |
| 数値・計算 | 数字の文字 (未入力は `""`) |
| ドロップダウン | 文字 (未入力は `null`) |
| ラジオボタン | 文字 (いつも値がある) |
| チェックボックス・複数選択・カテゴリー | 文字の配列 |
| 日付 / 時刻 | `"2026-01-31"` / `"09:30"` (未入力は `null`) |
| 日時・作成日時・更新日時 | UTC の `"2026-01-31T00:30:00Z"` (秒は 00。未入力は `""`) |
| ユーザー選択・作業者 | `[{"code","name"}]` |
| 作成者・更新者 | `{"code","name"}` |
| 添付ファイル | `[{"contentType","fileKey","name","size"}]` |
| テーブル | `[{"id":"1001","value":{"コード":{"type","value"}}}]` |
| レコード番号 | アプリコードがあれば `"ITEM-3"`、無ければ `"3"` |
| `$id` / `$revision` | `{"type":"__ID__","value":"3"}` / `{"type":"__REVISION__","value":"2"}` |

`fields` を省略すると全部のフィールドと `$id`・`$revision` を返します。指定すると、そのフィールドだけ (`$id` / `$revision` も書いたときだけ)。テーブルの中のフィールド・無いフィールドを指定すると 400 です。
プロセス管理が無効のアプリでは、ステータス・作業者を返しません (フォームの設定には `enabled: false` で出ます)。カテゴリーはどのアプリでも無効です。

### 書き込み

- 知らないフィールドコードは無視して成功します (テーブルの中のフィールドをテーブルの外に書いたときも)
- 追加で送らなかったフィールドは既定値 (`defaultValue`・`defaultNowValue`。無ければ未入力)。ラジオボタンは既定値か最初の選択肢。テーブルは 0 行
- 更新は送ったフィールドだけを変えます。必須の確かめは、追加では全部のフィールド、更新では送ったフィールドだけです
- 書き込めないフィールド (400 `CB_VA01`): 計算・自動計算の文字列 1 行・ルックアップのコピー先・レコード番号・ステータス・作業者・カテゴリー。作成者・作成日時・更新者・更新日時は追加でだけ指定でき (公式仕様)、更新では 400
- テーブル: 送ると、配列の順が行の順になります。`id` のある行はその行を残して送った列だけ変え (ほかの列はそのまま)、`id` の無い行と無い `id` の行は新しい行 (行 ID はモックが振る)、送らなかった行は消えます。テーブルを送らなければ明細は変わりません
- ルックアップ: キーに値を書くと、参照元のアプリを `relatedKeyField` で引いて `fieldMappings` のコピー先に写します。参照元に無い値は 400 `GAIA_LO04`、複数合えば 400 `GAIA_LO05`。キーを空にするとコピー先も空。キーを送らない更新では写し直しません
- 計算と自動計算は保存のたびに計算し直します
- 版は追加で 1、更新のたびに +1

値の確かめ (400 `CB_VA01` + `errors`。キーは `record.コード.value`、テーブルの中は `record.テーブル.value[行].value.コード.value`):

| 誤り | メッセージの例 |
|---|---|
| 必須が未入力 | `必須です。` |
| 重複禁止の値が重なる | `値がほかのレコードと重複しています。` |
| 選択肢に無い値 | `「x」は選択肢にありません。` |
| 数値でない (`^-?\d+(\.\d+)?$` に合わない) | `数字でなければなりません。` |
| 日付 (`yyyy-MM-dd`)・時刻 (`HH:mm`)・日時 (時差か `Z` の付いた ISO 8601) の書式違い | `日付の形式が不正です。` など |
| 知らないユーザー・今の添付に無い `fileKey` | `ユーザー（x）が見つかりません。` など |
| 書き込めないフィールド | `このフィールドの値は変更できません。` など |

### エラー

本文は `{"code":"...","id":"...","message":"..."}` (検証エラーは `errors` も)。

| HTTP | code | いつ |
|---|---|---|
| 401 | `CB_AU01` | 認証のヘッダーが無い |
| 401 | `CB_WA01` | パスワード認証のログイン名・パスワードが違う |
| 520 | `GAIA_IA02` | 知らない API トークン |
| 400 | `GAIA_DA03` | 同じアプリの API トークンを 2 つ付けた |
| 400 | `CB_IL02` | API トークンが 10 個以上・本文があるのに `Content-Type` が JSON でない |
| 403 | `GAIA_NO01` | API トークンにそのアプリ・その操作の権限が無い・`apps.json` に API トークン |
| 403 | `CB_NO02` | パスワード認証の利用者に権限が無い |
| 404 | `GAIA_AP01` | アプリが無い (ゲストスペースのパスで通常のアプリ・違うスペースを指したときも) |
| 400 | `GAIA_IL23` | ゲストスペースのアプリを `/k/v1/` で指した |
| 404 | `GAIA_RE01` | レコードが無い (`updateKey` に合うレコードが無いときも) |
| 409 | `GAIA_CO02` | 版 (`revision`) が今の版と違う |
| 400 | `GAIA_IQ03` | クエリの書き方の誤り・使えない演算子・並べ替えできないフィールド・関数 |
| 400 | `GAIA_IQ11` | クエリに知らないフィールド |
| 400 | `GAIA_LO04` / `GAIA_LO05` | ルックアップの参照元に無い (参照元のトークンが無いときも) / 複数ある |
| 400 | `CB_IJ01` | 本文が JSON でない |
| 400 | `CB_VA01` | パラメーター・値の誤り (`limit` / `offset` の範囲外も) |
| 404 | `MOCK_NOT_SUPPORTED` | モックが応えない API |

## データのファイル (mock-data.json)

```json
{
  "users": [
    { "code": "clb_api", "password": "mock-clb-api-pass", "name": "CLB 連携" },
    { "code": "tanaka.ichiro", "password": "mock-tanaka-pass", "name": "田中 一郎", "permissions": { "1": ["view"] } }
  ],
  "apps": [
    {
      "id": 3, "name": "受注", "code": "", "statuses": ["未処理", "処理中", "完了"],
      "tokens": [ { "token": "mock-token-order-all", "permissions": ["view", "add", "edit", "delete"] } ],
      "fields": {
        "OrderNo": { "type": "SINGLE_LINE_TEXT", "label": "受注番号", "required": true, "unique": true },
        "CustomerCode": { "type": "SINGLE_LINE_TEXT", "lookup": { "app": 1, "relatedKeyField": "CustomerCode", "fieldMappings": { "CustomerNameCopy": "CustomerName" } } },
        "CustomerNameCopy": "SINGLE_LINE_TEXT",
        "Stage": { "type": "DROP_DOWN", "options": ["仮", "受注", "出荷済"] },
        "Lines": { "type": "SUBTABLE", "fields": {
          "Quantity": "NUMBER", "UnitPrice": "NUMBER",
          "Subtotal": { "type": "CALC", "expression": "Quantity * UnitPrice" } } },
        "Total": { "type": "CALC", "expression": "SUM(Subtotal)" }
      },
      "records": [
        { "OrderNo": "SO-1", "CustomerCode": "C-001", "Stage": "受注", "Lines": [ { "Quantity": "2", "UnitPrice": "480" } ],
          "$createdTime": "2026-02-01T03:00:00Z", "$creator": "clb_api", "$status": "処理中" }
      ]
    }
  ]
}
```

- `users`: パスワード認証の利用者。`permissions` (アプリ ID → `view` / `add` / `edit` / `delete`) を書かなければ全部のアプリに全部の権限。`Administrator` は使えない名前です
- `apps`: `id` (必須)・`name` (必須)・`code` (アプリコード。英字で始まる英数字と `_`)・`guestSpaceId` (ゲストスペースのアプリ)・`statuses` (プロセス管理のステータス。最初が作成時のステータス。書かなければプロセス管理は無効)・`tokens`・`fields`・`records`
- `fields` のキーがフィールドコード (並びがフォームの順)。値は種類だけ (`"NUMBER"`) か、`type`・`label`・`required`・`unique`・`options`・`defaultValue`・`defaultNowValue`・`expression`・`format`・`protocol`・`lookup`・`fields` (テーブルの中) を持つオブジェクト
- 種類: `SINGLE_LINE_TEXT` `MULTI_LINE_TEXT` `RICH_TEXT` `NUMBER` `CALC` `RADIO_BUTTON` `DROP_DOWN` `CHECK_BOX` `MULTI_SELECT` `DATE` `TIME` `DATETIME` `LINK` `USER_SELECT` `FILE` `SUBTABLE`。
  システムのフィールド (`RECORD_NUMBER` `CREATOR` `CREATED_TIME` `MODIFIER` `UPDATED_TIME` `STATUS` `STATUS_ASSIGNEE` `CATEGORY`) は書かなくても `レコード番号` `作成者` などのコードで付きます。別のコードにしたいときだけ種類を書きます
- フィールドコードはアプリの中で (テーブルの中も含めて) 重ならないこと。`$` で始まる名前・空白や `()=!<>,"` を含む名前は使えません
- `expression`: 計算 (`CALC`。`format` は `NUMBER` だけ) と自動計算の文字列 1 行。数値・`"文字"`・フィールドコード・`+ - * /`・`&` (文字の連結)・括弧・`SUM(テーブルの中のフィールド)` だけ書けます。テーブルの中の計算は同じ行のフィールドとテーブルの外のフィールドを使えます。未入力は 0、0 で割ると未入力 (`""`) になります
- `lookup`: `app` (参照元のアプリ ID)・`relatedKeyField` (参照元のキー)・`fieldMappings` (コピー先 → コピー元)。キーは文字列 1 行か数値、テーブルの外だけ。循環はできません
- `records` の値は、要求の本文と同じ `{"value": 値}` か、値だけ。ユーザー選択はコードの文字の配列でも可、添付は `{"name","contentType","size"}` を書くと `fileKey` を振ります。テーブルは行の値のオブジェクトの配列 (`{"value": {...}}` の形も可)。
  ほかに `$id` (レコード ID。書かなければ順に振る)・`$revision` (初めの版)・`$creator` / `$modifier` (ログイン名)・`$createdTime` / `$updatedTime`・`$status`・`$assignee` (ログイン名の配列) を書けます。レコードは追加の API と同じ検証を通ります (ルックアップは参照元のアプリから先に入れます)
- ファイルに誤りがあると、起動のときにどこが違うかを出して止まります。コメント (`//`) と末尾のカンマは書けます

### 付属のデータ

付いている `mock-data.json` は架空のサンプルです。

| ID | アプリ | パス | アプリコード | フィールド | 件数 |
|---|---|---|---|---|---|
| 1 | 顧客 | `/k/v1/` | なし | `CustomerCode` (必須・重複禁止)・`CustomerName` (必須)・`Status` (ドロップダウン: 見込み / 取引中 / 休止)・`Rank` (ラジオ: A / B / C、既定 B)・`Tags` (チェックボックス: 重要 / 新規 / 要フォロー)・`Website` (リンク)・`Note` (複数行)・`CreditLimit` (数値)・`Since` (日付) | 15 |
| 2 | 商品 | `/k/v1/` | `ITEM` | `ProductCode` (必須・重複禁止)・`ProductName`・`UnitPrice` (数値)・`Category` (ドロップダウン: 文具 / 紙製品 / 事務機器) | 8 |
| 3 | 受注 | `/k/v1/` | なし | `OrderNo` (必須・重複禁止)・`CustomerCode` (ルックアップ: 顧客の `CustomerCode`。`CustomerName` を `CustomerNameCopy` へ)・`CustomerNameCopy`・`OrderDate` (日付)・`Stage` (ドロップダウン: 仮 / 受注 / 出荷済)・`DeliveryAt` (日時)・`StartTime` (時刻)・`Assignee` (ユーザー選択)・`Memo` (リッチエディター)・`Attachments` (添付)・`Lines` (テーブル: `Product`・`Quantity`・`UnitPrice`・`Subtotal` (計算 = `Quantity * UnitPrice`))・`Total` (計算 = `SUM(Subtotal)`)。プロセス管理あり (未処理 / 処理中 / 完了) | 20 (明細 0〜3 行) |
| 4 | ゲスト案件 | `/k/guest/5/v1/` | なし | `Title` (必須)・`Budget` (数値) | 3 |

API トークン (アプリごとに 2 つ):

| アプリ | 全権限 (閲覧・追加・編集・削除) | 閲覧だけ |
|---|---|---|
| 1 顧客 | `mock-token-customer-all` | `mock-token-customer-view` |
| 2 商品 | `mock-token-item-all` | `mock-token-item-view` |
| 3 受注 | `mock-token-order-all` | `mock-token-order-view` |
| 4 ゲスト案件 | `mock-token-guest-all` | `mock-token-guest-view` |

利用者 (パスワード認証): `clb_api` / `mock-clb-api-pass` (CLB 連携)・`yamada.taro` / `mock-yamada-pass` (山田 太郎)・`suzuki.hanako` / `mock-suzuki-pass` (鈴木 花子) は全部の権限、`tanaka.ichiro` / `mock-tanaka-pass` (田中 一郎) はアプリ 1〜3 の閲覧だけ (ゲスト案件は見えない)。

検索・並び・ページングを試しやすいよう、未入力の値 (状態・与信限度額・取引開始日・受注日・進捗・顧客コードなど)・同じ値の行・`"` と `\` を含む値 (顧客 6)・小数 (顧客 13)・版が 3 のレコード (顧客 2)・添付のあるレコード (受注 1・2・7) を混ぜてあります。

## 実物と違う点・実物で確かめていない点

このモックは、**公式の仕様 (cybozu developer network) とコネクターが前提にしている振る舞いどおりに応える**ものです。kintone の実際の応答を写したものではありません。
前提そのものが実物と違っていれば、モックでは動いて実物では動きません。実物で使う前に、開発者ライセンスなどの kintone で確かめてください。

実物と違う (わかっていて単純にした) 点:

- **`like`**: モックは部分一致 (英字の大文字・小文字を区別しない) です。実物は単語検索に近く、語の途中から始まる文字・記号・1 文字の検索などで結果が違うことがあります。ヒット件数の打ち切り (10 万件) の警告もありません
- **文字の比較と並び**: モックは文字コード順です。実物の照合・並び順 (大文字・小文字・全角半角・かな・ドロップダウンの並び) とは違うことがあります
- **応える API**: コネクターが送る 8 種類だけです。単件取得・カーソル・一括・ステータスの更新・プレビューの設定などは 404 です
- **フィールドの種類**: 組織選択・グループ選択・関連レコード一覧・ラベル・スペース・罫線・グループはありません。テーブルの中のルックアップもありません
- **計算式**: 四則・`&`・`SUM` だけです (`ROUND`・`IF`・日時の計算・表示形式 (`format`) の `NUMBER` 以外・桁数 (`displayScale`) の丸めはありません)。未入力を 0 として計算し、0 で割ると未入力にします
- **フィールドの検証**: 文字数・値の範囲・リンクの形式 (URL・メール)・数値の桁数の制限はありません。数値は `-12.5` の形だけを受けます
- **添付ファイル**: アップロード (`POST file.json`) は一時保管領域に置いて `fileKey` を返し、その `fileKey` をレコードに書くと添付されます (レコードの値では別の `fileKey` = `mock-file-N` になり、アップロードの `fileKey` は 1 回しか使えません)。
  ダウンロード (`GET file.json?fileKey=`) はレコードの値の `fileKey` だけで、閲覧できないアプリ・無い `fileKey` は 404 (`GAIA_BL01`)。一時保管領域のファイルは消しません (実物は 3 日)。
  初期データの添付の中身は `mock content of <ファイル名>` の文字です。テストからは `Uploads` (一時保管領域)・`GetFileContent(fileKey)` で中身を確かめられます。
  これらの形 (エラーの返り方・`fileKey` の振り方) は実物で確かめたものではありません
- **権限**: アプリ単位の閲覧・追加・編集・削除だけです。レコード・フィールドのアクセス権、アプリ管理の権限、IP 制限、Basic 認証、OAuth、セッション認証はありません
- **呼び出しの制限**: 同時接続数・1 日の回数・要求の大きさの制限は再現しません。障害 (5xx・切断・遅延) はテストから仕込んだときだけ起きます
- **フォームの設定**: `fields.json` の各フィールドの付帯情報 (`minLength`・`unit`・`align` など) はモックが決めた固定の値です。`lang` などの引数は無視します。プレビューの設定 (`preview/app/form/fields.json`) はありません

実物で確かめていない (仕様から推した・モックが選んだ) 点:

- **エラーの code・HTTP ステータス・メッセージ**: `CB_AU01`・`CB_WA01`・`GAIA_CO02` (409)・`GAIA_RE01` (404)・`GAIA_AP01` (404)・`CB_VA01`・`CB_IJ01` のほかは確かめていません。
  特に、知らない API トークン (520 `GAIA_IA02`)・同じアプリのトークン 2 つ (400 `GAIA_DA03`)・トークン 10 個以上 (400 `CB_IL02`)・`apps.json` に API トークン (403 `GAIA_NO01`)・通常のアプリをゲストスペースのパスで指したとき (404 `GAIA_AP01`)・ゲストスペースのアプリを通常のパスで指したとき (400 `GAIA_IL23`)・`Content-Type` が JSON でないとき (400 `CB_IL02`)・クエリの誤り (`GAIA_IQ03` / `GAIA_IQ11`)・`limit` / `offset` の範囲外 (400 `CB_VA01`)・ルックアップの複数一致 (`GAIA_LO05`)・ルックアップの参照元のトークンが無いとき (`GAIA_LO04`) はモックが選んだものです。メッセージの文言はどれもモックのもので、`errors` のキーの形 (テーブルの中・チェックボックスなど) も確かめていません
- **書き込めないフィールドへの書き込み**: 計算・自動計算・ルックアップのコピー先・ステータスなどに値を送ると、モックは 400 にします。実物が拒否するか無視するか、拒否するならどの code かは確かめていません
- **未入力の値の形**: 日付・時刻・ドロップダウンの未入力を `null`、日時・数値・文字の未入力を `""` にしています。実物でこのとおりかは確かめていません
- **追加でテーブルを送らなかったとき**: モックは 0 行にします。実物が空の行を 1 行作るかは確かめていません
- **日時の値**: 時差の無い値 (`2026-01-01T10:00:00`) を書くと 400 にしています (実物が UTC として受けるかは確かめていません)。秒は切り捨てて `:00` にします。クエリで日時のフィールドに日付だけを書いたときは UTC のその日として比べます (実物は利用者のタイムゾーンかもしれません)。`defaultNowValue` の日付・時刻も UTC です
- **クエリ**: `and` / `or` の優先順位 (モックは `and` が先)・キーワードの大文字 (`AND`・`ORDER BY` もモックは受ける)・句の順 (モックは `order by` → `limit` → `offset` の順だけ)・`order by` の `asc` / `desc` の省略 (モックは 400。実物は省略できるが既定の向きを確かめていない)・未入力の並びの位置 (モックは昇順の先頭)・同じ値の行の順 (モックはレコード ID の降順)・テーブルの中のフィールドの否定 (`not in` などは「どの行も合わない」)・`is empty` のテーブルの行が 0 行のとき (合う)・`like ""` (全部合う)・数値の値を `"` で囲まずに書けること・レコード番号の条件に `ITEM-3` の形を書けることは確かめていません
- **`fields` の指定**: 無いフィールド・テーブルの中のフィールドを指定すると 400 (`CB_VA01`) にしています。実物が無視するかは確かめていません
- **フォームの設定**: ステータス・作業者・カテゴリーをいつも `properties` に入れ、`enabled` で有効かを示しています。実物で無効のときにも入るか、入るなら `enabled` 以外の項目は確かめていません
- **テーブル**: 同じ行 `id` を 2 回送ったときは 2 回目を新しい行にしています。存在しない行 `id` を新しい行にするのは公式の操作例に合わせました
- **`updateKey`**: `id` と一緒に送ったときの扱い (400)・合うレコードが無いときの code (404 `GAIA_RE01`) は確かめていません
- **パスワード認証と API トークンを両方付けたとき**: パスワード認証を使います
- **`apps.json`**: アプリの作成日時・作成者などはモックの固定の値です
