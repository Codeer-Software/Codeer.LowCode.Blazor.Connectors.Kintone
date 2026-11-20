# Codeer.LowCode.Blazor.Connectors.Kintone - Project Guide

## プロジェクト概要

Codeer.LowCode.Blazor (CLB) のモジュールを kintone (サイボウズ) のアプリに結び付けるコネクター。
NuGet パッケージ `Codeer.LowCode.Blazor.Connectors.Kintone.Server` / `Codeer.LowCode.Blazor.Connectors.Kintone` (画面の添付ファイルのフィールド) / `Codeer.LowCode.Blazor.Connectors.Kintone.Designer` (MIT) として配布する。

- **ターゲット**: .NET 8.0 / C# (Nullable 有効, ImplicitUsings 有効)
- **本体リポジトリ**: `C:\tfs\codeer\Codeer.LowCode.Blazor` (参照用、直接編集しない)。本体は NuGet パッケージで参照する
- **作りの手本**: `C:\GitHub\Codeer.LowCode.Blazor.Connectors.FileMaker` (SQL の読み替えの構造・テストのハーネス・Example・Designer の結線はここから写した) と
  `C:\GitHub\Codeer.LowCode.Blazor.Connectors.NetSuite` (明細 = 親の JSON 配列の列。kintone のテーブルはこの形)。
  添付ファイルのフィールド (クライアント用パッケージ・ダウンロードの判定・Example の Controller) は `C:\GitHub\Codeer.LowCode.Blazor.Connectors.Pleasanter` から写した
- **利用者向けドキュメント**: `Docs/Kintone.md` / モックは `Docs/KintoneMock.md` / サンプルは `Docs/Example.md`
- **TODO**: `Docs/TODO.md` (これから対応する予定のもの。TODO はこのファイルに置いて Git で管理する)
- **調査資料**: `Docs/01_kintone_api_inventory.md` (API 調査) / `Docs/02_kintone_connector_design.md` (設計案。外部の調査者の提案で、このリポの決定ではない。採らなかった点は下の「決めてあること」)

## リポジトリ構成

```
Source/
├── Codeer.LowCode.Blazor.Connectors.Kintone.slnx
├── Codeer.LowCode.Blazor.Connectors.Kintone.Server/   # サーバー側ライブラリ (WPF・ASP.NET に依存しない)
│   ├── KintoneDbAccessor.cs          # IDbAccessor のデコレータ (公開の入口)。読み出しのページ送り・作成/更新/削除
│   ├── KintoneConnection.cs          # 接続 1 つ: 認証・送信 (再試行・エラー分類)・定義・レコードの読み書き・利用者のアカウントの確認 (VerifyAccountAsync)
│   ├── KintoneAccountResult.cs       # VerifyAccountAsync の結果 (Succeeded / InvalidCredentials / Failed)
│   ├── KintoneConnectionSettings.cs  # ConnectionString "Provider=Kintone;..." の読み取り
│   ├── KintoneTableDefinition.cs     # フォームの設定 (app/form/fields.json) → 表と列の定義。フィールドの種類ごとの列の種類・クエリで使える演算子
│   ├── KintoneMetadataFile.cs        # 定義を保存したファイル (本体のサーバー専用ファイル。デザイナと Web アプリが読む)
│   ├── KintoneQueryTranslator.cs     # 本体の SQL → kintone のクエリ (読み出し・集計の計画・書き込みの対象)
│   ├── KintoneQueryExecutor.cs       # 読み出し・集計の計画の実行 (値を集める条件の読み替え・要求の分割・メモリでの並び・メモリでの集計)
│   ├── KintoneValueConverter.cs      # レコードの値 ⇔ 列の値 (テーブル = 行の JSON 配列の文字)
│   ├── KintoneAttachments.cs         # 添付 (FILE) の書く値の読み取り (足す = base64 / 消す = deleted) と kintone に送る値
│   ├── KintoneAttachmentFile.cs      # GetAttachmentAsync の結果 (名前と中身)
│   └── KintoneException.cs
├── Codeer.LowCode.Blazor.Connectors.Kintone/          # 画面用 (Razor クラスライブラリ)。添付ファイルのフィールド (KintoneAttachmentsField*) と KintoneFieldsInitializer。.Server は参照しない (型名の文字で見分ける)
├── Codeer.LowCode.Blazor.Connectors.Kintone.Designer/ # デザイナ用 (WPF)
│   ├── KintoneDesignerInitializer.cs # Initialize(env) = SpecDocs の登録 / Setup(env) = Tools のメニュー (メタ情報の更新) / CreateDbAccessor (定義はプロジェクトのメタ情報のファイルから読む)
│   └── SpecDocs/Kintone.md           # AI が kintone のモジュールを設計するための仕様 (デザイン側のことだけを書く)
├── Kintone.Mock/                                      # kintone の REST API のモック (ASP.NET Core)。パッケージにしない
├── Codeer.LowCode.Blazor.Connectors.Kintone.Test/     # NUnit。HTTP の偽物 (Harness/FakeKintone) の単体テストと、モック (TestServer) + 本体の ModuleDataIO の結合テスト (Mock*Test・Harness/MockHarness)
└── Example/                                           # サンプル (パッケージにしない)。動かし方は Docs/Example.md
```

パッケージは 3 つ: サーバーは `.Server`、画面のフィールドは無印 (`Codeer.LowCode.Blazor.Connectors.Kintone`。添付ファイルを使うアプリだけ)、デザイナは `.Designer` (`.Server` と無印に依存)。

## ビルド・テスト

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.Kintone.Server -m:1
MSBUILDDISABLENODEREUSE=1 dotnet test Codeer.LowCode.Blazor.Connectors.Kintone.Test -m:1
```

`.Designer` と `Example/LowCodeApp.Designer` は WPF なので dotnet CLI ではなく VS の MSBuild でビルドする
(`"C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe" <csproj> -restore -m:1 -v:m -p:UseSharedCompilation=false`)。

## 設計: kintone を IDbAccessor として見せる

`IDbAccessor` はホストが実装を渡す公開のインターフェース。kintone 用のデータソースだけを横取りするデコレータ (`KintoneDbAccessor`) にする。**本体への変更は無い。**

```
ModuleDataIO (認可・インターセプタ・一覧/保存の組み立て = 本体のまま)
  └─ ModuleDataIOForDb が SQL を組み立てる (kintone 用データソースは PostgreSQL 方言)
       └─ KintoneDbAccessor
            ├─ kintone のデータソース → KintoneQueryTranslator → KintoneConnection (REST API)
            └─ それ以外 → 包んだ IDbAccessor (DbAccessor)
```

### 設定

データソース (本体の DataSources) に書く。`DataSourceType` は `PostgreSQL` と書かせる (本体の enum に足さない)。

```
Provider=Kintone;Host=https://example.cybozu.com;Apps=Customer:12,Order:13;ApiTokens=<トークン>,<トークン>
```

| キー | 内容 |
|---|---|
| `Provider` | `Kintone` 固定 |
| `Host` | kintone の URL (`https://` 必須。`http://` は `localhost` / `127.0.0.1` だけ = モック用) |
| `Apps` | `表名:アプリID` をカンマで並べる。表名はこの設定で付ける名前 (モジュールの DbTable)。環境 (開発・本番) でアプリ ID が違っても表名は同じにできる |
| `ApiTokens` | アプリの API トークンをカンマで並べる (最大 9)。毎回全部を `X-Cybozu-API-Token` に付ける (アプリをまたぐルックアップの保存に、相手のアプリのトークンも要る) |
| `User` / `Password` | API トークンの代わりのパスワード認証 (`X-Cybozu-Authorization`)。`ApiTokens` と片方だけ |
| `GuestSpaceId` | ゲストスペースのアプリのとき (`/k/guest/<id>/v1`) |
| `MaxRows` | ページングなしの読み出し・集計で読む行数の上限 (既定 10000。超えたら黙って切らずにエラー) |
| `TimeoutSeconds` | 1 要求のタイムアウト (既定 100) |

### 名前の対応

- 表名 (DbTable) = `Apps` で付けた名前。`.` と `"` を含む名前は不可
- 列名 (DbColumn) = フィールドコードそのまま (日本語もそのまま)。レコード ID = `$id`、版 = `$revision` (kintone のクエリ・応答の名前と同じ)
- モジュールの Id = `$id`。重複禁止の文字列 (1 行)・数値のフィールド (`CanBeRecordKey`) でもよい (更新は updateKey、削除は先に `$id` を引く。Id の値を変える更新は `$id` を引いて id 指定で書く)

### 型 (KintoneTableDefinition.GetKind)

| kintone | 列 | 備考 |
|---|---|---|
| SINGLE_LINE_TEXT / MULTI_LINE_TEXT / RICH_TEXT / LINK / RADIO_BUTTON / DROP_DOWN / RECORD_NUMBER / STATUS | string | |
| CREATOR / MODIFIER | string | `{code, name}` の code (ログイン名) |
| NUMBER / CALC (既定) | decimal | CALC は format で DATETIME / DATE / TIME / 文字 (HOUR_MINUTE・DAY_HOUR_MINUTE)。CALC の読めない値は null |
| DATE / TIME | DateOnly / TimeOnly | `yyyy-MM-dd` / `HH:mm` |
| DATETIME / CREATED_TIME / UPDATED_TIME | DateTime (UTC) | 列型名は `timestamp with time zone` (デザイナが SaveAsUtc を既定でオンにする) |
| CHECK_BOX / MULTI_SELECT / USER_SELECT / ORGANIZATION_SELECT / GROUP_SELECT / FILE / CATEGORY / STATUS_ASSIGNEE | string (JSON 配列の生の文字) | 空の配列は null。書くときは文字の配列か `[{code}]`。FILE は列型名 `kintone_file`、書き方は下の「添付ファイル」 |
| SUBTABLE | string (行の JSON 配列) | 下記 |
| 関連レコード一覧・グループ・ラベルなど | 列にしない | |

- 必須でも列の定義は null 可 (NOT NULL にすると本体が空を型の既定値に置き換えて書く)
- 読み取り専用 (`IsReadOnly`): `$id` / `$revision`・CALC・RECORD_NUMBER・CREATOR・MODIFIER・CREATED_TIME・UPDATED_TIME・STATUS・STATUS_ASSIGNEE・CATEGORY・テーブルの中の FILE・文字列 1 行の自動計算・ルックアップのコピー先。
  作成で値があればエラー (null は送らない)、更新の対象に入っていたらエラー
- 作成の本文に値が null の列は入れない (kintone の初期値を潰さない)

### テーブル (SUBTABLE)

親の列の値 = 行の JSON 配列の文字 (本体の列モードの一覧 = ListField の DbColumn + 行のモジュールの JsonField。NetSuite 版の明細と同じ形)。

- 読む: `[{"$id": "行ID", "コード": 値, ...}]`。数値は JSON の数値、日付・時刻・日時は kintone の文字のまま、配列の値は配列のまま、未入力は null。行が無ければ列は null
- 書く: 行に `$id` があればその行 (送った項目だけ変わる)、無ければ新しい行。配列に無い今の行は kintone が削除する (kintone の PUT の仕様。コネクターで差分を計算しない)。
  読み取り専用の項目・定義に無いキーは送らない。`$id` は本体が「結線していないキー」として行に温存する
- 親をまたいでテーブルの行を読む表は無い (未実装)

### 添付ファイル (FILE)

画面は `KintoneAttachmentsFieldDesign` (無印パッケージ。DbColumn・MaxFileSizeMB 既定 10・`NeedCopy` false・検索の部品なし・スクリプトに出さない)。Pleasanter の添付のフィールドの写し。

- 書く値 (`KintoneAttachments.ParseChange`): JSON 配列の要素のうち `base64` があるもの = 足す (`name` 必須・`contentType` 省略で octet-stream)、`deleted: true` = 消す (`fileKey` 必須)。それ以外の要素は見ない (クライアントの fileKey・size を信じない)。形が違えば InvalidRequest
- 更新: 添付の列があれば、存在の確かめと同じ条件で `$revision` と添付の列を読み直す (無ければ 0 行)。消す = 読んだ fileKey との一致 (ordinal) だけ。足すものは 1 つずつ `POST file.json` (multipart) し、値 = 今の fileKey − 消すもの + 新しい fileKey (`[{fileKey}]`)。変化の無い列は送らない・送るものが無ければ PUT しない。
  添付を書くとき SQL に版が無ければ読んだ版を付ける (読んでから書くまでの更新を kintone に弾かせる)
- 作成: 足すものだけ (消すは無視)
- アップロードの後で PUT が失敗したら一時保管領域に残す (kintone が 3 日で消す)。アップロードは送り直す (二重でも残るのは一時ファイルだけ)
- ダウンロード: `KintoneDbAccessor.GetAttachmentAsync(io, designData, module, id, field, key)`。モジュール・フィールドの型名 (`AttachmentsFieldDesignTypeFullName`)・列が FILE か・Id が `$id` か重複禁止のフィールドかを見て、`io.GetListAsync` (現在の利用者の権限・行の閲覧条件・論理削除・フィールドの権限が効く) で 1 件読み、その値に key があるときだけ `GET file.json?fileKey=` で読む。失敗は全部 null (理由を分けない)
- Controller は Example だけ (`KintoneAttachmentsController`)。ライブラリに持たない
- テーブルの中の FILE は読み取り専用のまま
- 添付 (FILE)・テーブル (SUBTABLE) の列は条件・並び・集計に使えない。条件は `OperatorNotSupported`、並び (読み出しの order by。結合先の列も) と集計 (軸・日付の軸・集計する列) は
  `KintoneQueryTranslator.CheckComparable` が `NotSupported` で拒否する (kintone に送らない・メモリで JSON の文字を比べない)
- 画面の `AcceptChanges` (読み直さない保存の後): 送った値の base64 の要素 (足した) と deleted (消した) だけを保存済みにし、送信の間の追加・印は変更として残す
  (同じ名前・中身の追加は送った数だけ)。送った追加を送信の間に取り消していたら (今の一覧に無い)、kintone には付いたので名前だけの保存済みの行 (中身・fileKey なし) として足す。送った値に追加も削除も無ければ何もしない。テストは `AttachmentsFieldAutoSubmitTest` (本体の AutoSubmitField を `Harness/ClientServices` で動かし、送信の途中で止める)。
  一度に選べるのは 100 個まで (`e.FileCount` で先に入力エラー)

### SQL の読み替え (KintoneQueryTranslator)

本体が組む形だけを受け付け、それ以外は `KintoneException` (`NotSupported`) で拒否する (条件を落として続けない)。構造は FileMaker 版の ODataTranslator と同じ
(字句解析 → 条件の構文木 `FilterNode` → 畳む → 文字にする。結合は別読みしてメモリで突き合わせ・exists と結合先の列の条件は値を集めて一致の条件に・要求の分割)。

| 本体の SQL | kintone |
|---|---|
| `select ... from "t" [left join ...] [where ...] [order by ...] [limit N offset M]` | `POST records.json` + `X-HTTP-Method-Override: GET` (本文 `{app, query, fields}`)。query = `条件 order by 列 asc, ... limit N offset M` |
| `select count(*) ...` | 同じ要求を `limit 1` + `totalCount: true` で送り、`totalCount` を読む |
| 集計 | 条件に合う行を読んでメモリで集計 (kintone に集計の API は無い) |
| `insert ... returning "<Id の列>"` | `POST record.json`。応答の id を返す (Id が重複禁止のフィールドなら送った値) |
| 一括 insert | 1 件ずつ POST。途中で失敗したら、そこまでに作れた件数とキーをエラーに入れる |
| `update ... where "<Id の列>"=@p [and "$revision"=@v] [and ...]` | `PUT record.json` (`id` か `updateKey`、`revision`)。GAIA_RE01 (無い)・GAIA_CO02 (版が違う) は 0 行 |
| `delete from ... where ...` | `DELETE records.json?app=&ids[0]=&revisions[0]=` |

kintone のクエリに固有のこと:

- **`not (...)` が無い。** not を比較まで押し下げ (`PushNot`。ド・モルガン)、演算子を逆にする
- **フィールドの種類ごとに使える演算子が違う** (`KintoneQueryKind`)。書けない組み合わせは `NotSupported`
  - Text (文字列 1 行・リンク): `= != in not in like not like` / Number (数値・計算・レコード番号・`$id`): `= != > < >= <= in not in` / DateTime (日付・時刻・日時): `= != > < >= <=`
  - LongText (複数行): `like / not like / is empty / is not empty` / RichText: `like` / Selection (選択・ユーザー・ステータス): `in / not in`。「等しい」は `in (値)`
- **空の扱いを毎回明示する。** SQL の 3 値論理に合わせ、`!=`・`not in`・`not like`・大小の比較には「空でない」を付ける (`(a != "x" and a != "")`・`(a < 5 and a != "")`)。集めた値との一致の not は空の行も真 (`(a = "" or a not in (...))`)。
  空の判定は `= ""` (Text / Number / DateTime)・`is empty` (LongText)・`in ("")` (Selection)。空になりえない列 (`$id`・レコード番号・作成日時など) の空の判定は定数に畳む
- like は本体の `%文字%` だけ受け付けて `like "文字"`。kintone の like は語の単位の検索で、SQL の部分一致と同じ結果になるとは限らない
- 値: 文字は `"..."` (`\` と `"` を `\` でエスケープ)、数値はそのまま、日付 `"yyyy-MM-dd"`、時刻 `"HH:mm"`、日時 `"yyyy-MM-ddTHH:mm:ssZ"` (UTC)
- 日付の列に時刻の入った日時・時刻の列に分より細かい時刻を当てた比較は、PostgreSQL の比較と同じ意味に読み替える (`KintoneQueryTranslator.CreateTerm` / `Coarser`。列の値はその日の 0:00・その分の 0 秒):
  `=` は合う行なし、`<>` は空でない行、`<` と `<=` は `<= 切り捨てた値`、`>` と `>=` は `> 切り捨てた値`。値をそのまま突き合わせる所 (集めた値との一致・結合) では、そういう値はどの行とも一致しない。
  本体は DateField の値を日付で渡すので、ふつうは DateTimeField を日付の列に結んだときだけ起きる
- ユーザー・組織の列 (CREATOR / MODIFIER / USER_SELECT / ORGANIZATION_SELECT / STATUS_ASSIGNEE) に kintone のクエリの関数の形の値 (`LOGINUSER()`・`PRIMARY_ORGANIZATION()`。大文字小文字・空白を問わない) を当てた条件は送らない
  (`LiteralFor` が null = 合う行なし・`<>` は空でない行)。kintone の関数は引用符なしで書く決まりで、コネクターは値を `"..."` で書くが、`"..."` の中を kintone が関数として読まないことは実物で確かめていないため
- 上の 2 つと、数値の列に数値でない文字・日付の列に書式の違う文字など「どの行とも等しくならない値」との `=` は、PostgreSQL と同じく空でない行では偽・空の行では unknown にする
  (`NeverEqual` = `TriNode(真 = 行なし, 偽 = 空でない行)`。`<>` はその not)。not の中でも空の行は合わない: `not (列 = 値)`・`列 Not In (値…)` は空でない行、`not (列 <> 値)` は 0 行。空になりえない列では「空でない」は定数の真
- 版の条件 (`"$revision"=@p`) は where から取り出して kintone の `revision` に渡す (クエリには書かない)。`"$revision" is null`・空の版との一致は合う行なし (0 行)。
  SET / insert の `$revision` は送らない (kintone が 1 つ進める = 本体の `IncrementVersion` と同じ値になる)
- キーと版のほかに条件 (論理削除など) がある更新・削除は、先に `select count(*)` で確かめてから書く (その間は防げない。版の条件があれば kintone が弾く)

読み出しのページ送り (`KintoneDbAccessor.ReadRowsAsync`。1 回 500 件まで):

- limit あり: offset で読む。offset が 10000 (kintone の上限) を超えたらエラー
- limit なし・並びなし: `$id > 前の最後の ID order by $id asc limit 500` で読み進める (offset の上限に当たらない)
- limit なし・並びあり: offset で読む (並びの最後に `$id asc` を足す)
- limit なしは `MaxRows` を超えたらエラー。カーソル API は使っていない

集計: 常に行を読んでメモリで集計 (`MaxRows` まで)。行を選ぶ定義 (WithinGroupsOf) も FileMaker 版と同じくメモリで処理する。並びは PostgreSQL と同じく空の値が昇順で後。

日付の軸 (本体の `DateGroup`) は、本体が書く PostgreSQL の丸めの式だけを受け付け (`KintoneQueryTranslator.ReadGroupKey`)、メモリで同じ意味に計算する (`KintoneQueryExecutor.Truncate`。プリザンター版と同じ):
`date_trunc('hour' | 'day' | 'week' | 'month', C)`・`(date_trunc('quarter' | 'year', S)[ + interval 'F months'])` (C = 列 か `(列 + interval 'N minutes')`、S = C か `(C - interval 'F months')`)。
週は月曜始まり・年と四半期は年度の開始月で切る・UTC 保存の日時 (kintone の日時は UTC) は時差 (N 分。負もある) を足してから壁時計で丸める・月の加減は日が無ければ月末 (PostgreSQL の interval と同じ)。
値は期間の開始の種類の無い日時 (日付の列は 0:00 の日時として丸める)。本体が時は日時、それ以外は日付にする。空の値は 1 つのグループ。ほかの日付の式 (`to_char` など) は拒否する

本体が組む SQL の形が変わるとここが壊れる。テストは本体の `ModuleDataIO` を実際に通す (本体のパッケージを上げたらこのテストで確かめる)。

### 接続 (KintoneConnection)

- 長生きのオブジェクト (HttpClient・定義を持つ)。`FromDataSources` は設定が同じなら同じ接続を返す
- 定義: アクセサに定義のファイル (下の「定義のファイル」) を渡さないときだけ、アプリごとに `GET app/form/fields.json` を読んでプロセスの間持つ
- リダイレクト (3xx) には従わない
- 再試行: 読み出しは 429 / 502 / 503 / 504 / 通信失敗を 3 回まで。書き込みは再送しない。書き込みを送ったあとの通信失敗・タイムアウト・エラーコードの無い 5xx は `WriteResultUnknown`。
  kintone のエラーコード (`code`) が付いた応答は (520 を含め) 「処理されなかった」と確定したエラーとして扱う
- エラー: `KintoneException` (`Kind`: Authentication / NotFound / InvalidRequest / NotSupported / ServiceUnavailable / WriteResultUnknown / Unknown)。応答の `code` / `message` / `errors` (項目ごとのメッセージ) を入れる。トークン・パスワード・接続文字列は入れない

### 定義のファイル (メタ情報)

`kintone.<データソース名>.metadata.json` にアプリ (表) と列 (フィールドコード・種類・項目名・読み取り専用・必須・重複禁止・ルックアップ・選択肢・テーブルの中のフィールド) を書く。接続先・アプリ ID・トークンは書かない。
**置き場所は本体のサーバー専用ファイル** (デザインのプロジェクトの ServerFiles。デザインと一緒に App.zip で配られ、画面へは送られない)。前の置き場所 (プロジェクトの直下) は読まない。

- 書く: `KintoneMetadataFile.UpdateAsync(connection, projectDirectory)` (static) が Apps の全部のフォームの設定を読み、内容を読み直して確かめてから `DesignDataFileManager.WriteProjectServerFile` で書く (書き終えてから置き換える・読めなければファイルを変えない・同じファイルの更新は直列)
- 読む: `KintoneDbAccessor` の 3 つ目の引数。デザイナは `FromProject(projectDirectory)` (`ReadProjectServerFile`)、Web アプリは `FromDeployedDesign(designFileDirectory)` (`GetServerFile`)。
  **ファイルを渡したアクセサは実行時に fields.json を呼ばない。** ファイルに無いアプリは `NotFound` のエラー (デザイナでメタ情報を更新して配り直すよう案内する。黙って kintone に取りに行かない)。
  ファイルがまったく無ければアプリは無い (表の一覧は空)。アプリ ID は接続文字列の `Apps` のもの (ファイルにアプリ ID は無い = 検証と本番でアプリ ID が違ってもデザインは同じ)。表の一覧は「ファイルにあって `Apps` にもあるアプリ」
- ファイル名のデータソース名は文字 (日本語なども含む)・数字 (`char.IsLetterOrDigit`)・`_`・`-`・`.` 以外を `_` にする (OS に依らない。Windows のデザイナと Linux の Web アプリで同じ名前)
- キャッシュ: 場所 (プロジェクト / 配られたデザイン・フォルダ・データソース) ごとに static に持つ。配られたデザインは `App.zip` の更新日時と大きさが変わったときだけ zip を開き直す (読む前に日時を取る。読む間に差し替わっても次の読み出しで読み直す)。
  プロジェクトはファイルを毎回読み、中身が変わったときだけ解釈し直す。読み直しは場所ごとに 1 つずつ (ロック)・解釈した定義は差し替えるだけで中を変えない (読む側はロックなし)。
  App.zip の名前は本体の配置の決まり (`<designFileDirectory>/App.zip`) を前提にしている
- ファイルを渡さない 2 引数の形は従来どおりアプリごとに最初の 1 回 fields.json を読む (テスト・ファイルを使わないホスト用)

### kintone のアカウントでのログイン

- **ライブラリは確認だけ (`KintoneConnection.VerifyAccountAsync(loginName, password)`)。Controller・Cookie・利用者の表はホスト (Example)。** 失敗は例外でなく `KintoneAccountResult`
- 利用者のパスワード認証で `GET apps.json?limit=1` を 1 回 (送り直さない)。401 = InvalidCredentials。データの読み書きは連携用の認証 (API トークン) のまま

## 決めてあること (2026-11-16)

- **`DataSourceType` は `PostgreSQL` と書かせる。** 本体の enum に足さない
- **1 データソース = 複数アプリ (`Apps`)。** `Docs/02` の「1 データソース = 1 アプリ」は採らない (アプリ間のリンク・ルックアップ・結合は同じデータソースの中でしか出来ない)
- **表名は設定で付ける。** アプリ ID・アプリ名を表名にしない (環境で変わる・重なる)
- **API トークンは接続文字列に直接書く** (FileMaker / NetSuite 版と同じ。秘密の置き場所はホストの設定の仕組みに任せる)。`Docs/02` の間接参照のキーは採らない
- **列名は `$id` / `$revision`。** `Docs/02` の `__kt_id` などの別名は採らない
- **保存はレコードごとに確定する。** トランザクションに入らない・ロールバックで戻らない。DB と混ざる保存も拒否しない
- **連携用の認証 1 つでつなぐ。** 利用者ごとの認可は CLB (モジュールの権限) が担保する
- **楽観ロックは kintone の revision。** 更新・削除の要求に revision を付け、kintone が原子的に弾く
- **テーブルは親の JSON 配列の列。** 行の差分は kintone の PUT の仕様に任せる
- **集計は行を読んでメモリ。** 日付の期間の軸は本体の PostgreSQL の丸めの式だけを受け付けてメモリで計算する (2026-11-20)
- **定義はメタ情報のファイル (本体のサーバー専用ファイル) から読む。** デザイナも Web アプリも実行時に kintone へ定義を取りに行かない (2026-11-20)
- **選択肢はメタ情報の更新で取り込み、Enum の定義に写す** (SpecDocs で AI に指示)。画面のたびに kintone から読まない
- **SQL・ストアドプロシージャ (ExecuteSqlCommand) は拒否。**
- **Starter (ホストのテンプレート) には組み込まない。** 使うアプリが 2 行足す
- **添付ファイルは専用のフィールド (Pleasanter 版と同じ作り)** (2026-11-20)。値の JSON のキーは kintone の値と同じ lowerCamel (`fileKey`・`name`・`contentType`・`size`) に `base64`・`deleted` を足したもの。
  消すのは保存の直前に読み直した値にある fileKey だけ。添付を書く更新は版を付ける。テーブルの中の FILE は対象外。「1 ファイルの上限」は画面だけの確かめ

## 守ること

- 本体は core のリポで変える (このリポから直接編集しない)。足りない口はユーザーに相談
- ホスト (Starter) に置くべきもの (Controller・DI の要求) をライブラリに持ち込まない
- 実物の kintone で確かめていないことを「確認済み」と書かない
- 公開リポジトリ。ホスト名・アカウント・トークン・パスワードの実物をコード・テスト・ドキュメントに書かない
- 利用者向けの文書 (README・Docs) は日本語。会社名の表記は Codeer

## 未検証・未実装 (2026-11-20 時点)

**実物の kintone には一度もつないでいない。** テストは HTTP の偽物とモックが相手。モックは `Docs/01` の記述から作ったもので、実物の動きの裏付けではない。

実物で確かめること:

- 数値・日付・計算のフィールドの空の検索が `= ""` / `!= ""` で書けるか (`!=`・大小の比較にはいつも `!= ""` を付けているので、書けなければその比較が全部失敗する)
- `!=` / `not in` / `not like` / 大小の比較が、空の行をどう扱うか (空の条件を明示して書いているので、どちらでも結果は同じになるはず)
- `in (...)` の値の数の上限・クエリの長さの上限 (今は値 100 個ずつ)
- 数値・`$id` の値を引用符なしで書けるか (`$id > 123`・`数値 in (1, 2)`)
- `limit 1` + `totalCount` の件数、offset 10000 の境界
- order by で空の値が並ぶ位置、並べられないフィールドの種類
- like の一致のしかた (語の区切り)
- 業務エラーの HTTP ステータス (520 の使われ方) とエラーコード (GAIA_RE01 / GAIA_CO02) の出方
- 重複禁止のフィールドの一致の広さ (大文字と小文字など)。Id が重複禁止のフィールドの削除・Id の変更は、キーで引いて 1 件だけ・値が同じときだけ書く (2 件返ると 0 行)
- updateKey の更新で、record にキーのフィールドを入れたときの動き (今は入れない)
- 空の record の PUT で版が進むか
- フォームを変えたあとメタ情報を更新して配り直していないとき、無くなった・種類の変わったフィールドへの書き込みが成功に見えないか (古い定義のまま書く)
- ユーザー・組織の列の条件に `"LOGINUSER()"` (引用符つき) を書いたとき kintone が関数として読むか (コネクターはその形の値を送らないので結果は変わらない)
- テーブルの PUT (id つきの行は送った項目だけ変わる・無い行は消える)
- 2 要素認証・SAML の利用者のパスワード認証 (ログイン)
- ゲストスペース・Basic 認証・クライアント証明書の環境 (Basic 認証・クライアント証明書は未対応)
- 添付: 読むたびに fileKey が同じか (違うと削除の突き合わせが当たらず、消したはずのファイルが黙って残る)・アップロードの multipart の形 (日本語のファイル名を `filename="..."` に UTF-8 のまま入れている)・
  アップロードの fileKey が 1 回しか使えないか・ダウンロードの無い fileKey のエラー (モックは 404 GAIA_BL01)・大きさの上限を超えたときのエラー

未実装:

- テーブルの行を親をまたいで読む表、作成者などの表示名の列
- プロセス管理の操作、コメント
- 添付ファイルの名前の変更・並べ替え・プレビュー、テーブルの中の添付ファイルの書き込み
- 利用者本人の権限での読み書き (OAuth など)、カーソル API、一括の書き込み API (bulkRequest)

知っている制限:

- 28 桁を超える数値は読めない・丸まる (列は decimal)
- 書き込む時刻は分に切り捨てる (kintone の時刻は `HH:mm`)
- 添付を足した保存がエラー (結果不明を含む) のとき kintone 側の更新だけ済んでいることがある (ロールバックで戻らない)。画面は足したファイルを未保存のまま持つので、そのまま保存し直すと同じファイルがもう一度付く (利用者向けには「kintone のレコードを確かめてから保存し直す」と Docs に書いた)
