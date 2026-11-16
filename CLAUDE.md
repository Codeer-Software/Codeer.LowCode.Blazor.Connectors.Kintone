# Codeer.LowCode.Blazor.Connectors.Kintone - Project Guide

## プロジェクト概要

Codeer.LowCode.Blazor (CLB) のモジュールを kintone (サイボウズ) のアプリに結び付けるコネクター。
NuGet パッケージ `Codeer.LowCode.Blazor.Connectors.Kintone.Server` / `Codeer.LowCode.Blazor.Connectors.Kintone.Designer` (MIT) として配布する。

- **ターゲット**: .NET 8.0 / C# (Nullable 有効, ImplicitUsings 有効)
- **本体リポジトリ**: `C:\tfs\codeer\Codeer.LowCode.Blazor` (参照用、直接編集しない)。本体は NuGet パッケージで参照する
- **作りの手本**: `C:\GitHub\Codeer.LowCode.Blazor.Connectors.FileMaker` (SQL の読み替えの構造・テストのハーネス・Example・Designer の結線はここから写した) と
  `C:\GitHub\Codeer.LowCode.Blazor.Connectors.NetSuite` (明細 = 親の JSON 配列の列。kintone のテーブルはこの形)
- **利用者向けドキュメント**: `Docs/Kintone.md` / モックは `Docs/KintoneMock.md` / サンプルは `Docs/Example.md`
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
│   ├── KintoneMetadataFile.cs        # 定義を保存したファイル (デザイナ用)
│   ├── KintoneQueryTranslator.cs     # 本体の SQL → kintone のクエリ (読み出し・集計の計画・書き込みの対象)
│   ├── KintoneQueryExecutor.cs       # 読み出し・集計の計画の実行 (値を集める条件の読み替え・要求の分割・メモリでの並び・メモリでの集計)
│   ├── KintoneValueConverter.cs      # レコードの値 ⇔ 列の値 (テーブル = 行の JSON 配列の文字)
│   └── KintoneException.cs
├── Codeer.LowCode.Blazor.Connectors.Kintone.Designer/ # デザイナ用 (WPF)
│   ├── KintoneDesignerInitializer.cs # Initialize(env) = SpecDocs の登録 / Setup(env) = Tools のメニュー (メタ情報の更新) / CreateDbAccessor (定義はプロジェクトのフォルダのファイルから読む)
│   └── SpecDocs/Kintone.md           # AI が kintone のモジュールを設計するための仕様 (デザイン側のことだけを書く)
├── Kintone.Mock/                                      # kintone の REST API のモック (ASP.NET Core)。パッケージにしない
├── Codeer.LowCode.Blazor.Connectors.Kintone.Test/     # NUnit。HTTP の偽物 (Harness/FakeKintone) の単体テストと、モック (TestServer) + 本体の ModuleDataIO の結合テスト (Mock*Test・Harness/MockHarness)
└── Example/                                           # サンプル (パッケージにしない)。動かし方は Docs/Example.md
```

パッケージは 2 つ: サーバーは `.Server`、デザイナは `.Designer` (`.Server` に依存)。無印の名前はフロント用フィールドのために空けてある。

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
| CHECK_BOX / MULTI_SELECT / USER_SELECT / ORGANIZATION_SELECT / GROUP_SELECT / FILE / CATEGORY / STATUS_ASSIGNEE | string (JSON 配列の生の文字) | 空の配列は null。書くときは文字の配列か `[{code}]` |
| SUBTABLE | string (行の JSON 配列) | 下記 |
| 関連レコード一覧・グループ・ラベルなど | 列にしない | |

- 必須でも列の定義は null 可 (NOT NULL にすると本体が空を型の既定値に置き換えて書く)
- 読み取り専用 (`IsReadOnly`): `$id` / `$revision`・CALC・RECORD_NUMBER・CREATOR・MODIFIER・CREATED_TIME・UPDATED_TIME・STATUS・STATUS_ASSIGNEE・CATEGORY・FILE・文字列 1 行の自動計算・ルックアップのコピー先。
  作成で値があればエラー (null は送らない)、更新の対象に入っていたらエラー
- 作成の本文に値が null の列は入れない (kintone の初期値を潰さない)

### テーブル (SUBTABLE)

親の列の値 = 行の JSON 配列の文字 (本体の列モードの一覧 = ListField の DbColumn + 行のモジュールの JsonField。NetSuite 版の明細と同じ形)。

- 読む: `[{"$id": "行ID", "コード": 値, ...}]`。数値は JSON の数値、日付・時刻・日時は kintone の文字のまま、配列の値は配列のまま、未入力は null。行が無ければ列は null
- 書く: 行に `$id` があればその行 (送った項目だけ変わる)、無ければ新しい行。配列に無い今の行は kintone が削除する (kintone の PUT の仕様。コネクターで差分を計算しない)。
  読み取り専用の項目・定義に無いキーは送らない。`$id` は本体が「結線していないキー」として行に温存する
- 親をまたいでテーブルの行を読む表は無い (未実装)

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
- **空の扱いを毎回明示する。** SQL の 3 値論理に合わせ、`!=`・`not in`・`not like` には「空でない」を付ける (`(a != "x" and a != "")`)。集めた値との一致の not は空の行も真 (`(a = "" or a not in (...))`)。
  空の判定は `= ""` (Text / Number / DateTime)・`is empty` (LongText)・`in ("")` (Selection)。空になりえない列 (`$id`・レコード番号・作成日時など) の空の判定は定数に畳む
- like は本体の `%文字%` だけ受け付けて `like "文字"`。kintone の like は語の単位の検索で、SQL の部分一致と同じ結果になるとは限らない
- 値: 文字は `"..."` (`\` と `"` を `\` でエスケープ)、数値はそのまま、日付 `"yyyy-MM-dd"`、時刻 `"HH:mm"`、日時 `"yyyy-MM-ddTHH:mm:ssZ"` (UTC)
- 版の条件 (`"$revision"=@p`) は where から取り出して kintone の `revision` に渡す (クエリには書かない)。`"$revision" is null`・空の版との一致は合う行なし (0 行)。
  SET / insert の `$revision` は送らない (kintone が 1 つ進める = 本体の `IncrementVersion` と同じ値になる)
- キーと版のほかに条件 (論理削除など) がある更新・削除は、先に `select count(*)` で確かめてから書く (その間は防げない。版の条件があれば kintone が弾く)

読み出しのページ送り (`KintoneDbAccessor.ReadRowsAsync`。1 回 500 件まで):

- limit あり: offset で読む。offset が 10000 (kintone の上限) を超えたらエラー
- limit なし・並びなし: `$id > 前の最後の ID order by $id asc limit 500` で読み進める (offset の上限に当たらない)
- limit なし・並びあり: offset で読む (並びの最後に `$id asc` を足す)
- limit なしは `MaxRows` を超えたらエラー。カーソル API は使っていない

集計: 常に行を読んでメモリで集計 (`MaxRows` まで)。日付を期間に丸める軸は拒否し、kintone の計算フィールド (`DATE_FORMAT`) を案内する。行を選ぶ定義 (WithinGroupsOf) も FileMaker 版と同じくメモリで処理する。

本体が組む SQL の形が変わるとここが壊れる。テストは本体の `ModuleDataIO` を実際に通す (本体のパッケージを上げたらこのテストで確かめる)。

### 接続 (KintoneConnection)

- 長生きのオブジェクト (HttpClient・定義を持つ)。`FromDataSources` は設定が同じなら同じ接続を返す
- 定義: 実行時はアプリごとに `GET app/form/fields.json` を読んでプロセスの間持つ (kintone のフォームを変えたらアプリを再起動)。
  デザイナは保存したファイル `kintone.<データソース名>.metadata.json` から読む (kintone に取りに行くのは Tools > kintone のメタ情報を更新 のときだけ)
- リダイレクト (3xx) には従わない
- 再試行: 読み出しは 429 / 502 / 503 / 504 / 通信失敗を 3 回まで。書き込みは再送しない。書き込みを送ったあとの通信失敗・タイムアウト・エラーコードの無い 5xx は `WriteResultUnknown`。
  kintone のエラーコード (`code`) が付いた応答は (520 を含め) 「処理されなかった」と確定したエラーとして扱う
- エラー: `KintoneException` (`Kind`: Authentication / NotFound / InvalidRequest / NotSupported / ServiceUnavailable / WriteResultUnknown / Unknown)。応答の `code` / `message` / `errors` (項目ごとのメッセージ) を入れる。トークン・パスワード・接続文字列は入れない

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
- **集計は行を読んでメモリ。** 日付の期間の軸は拒否
- **選択肢はメタ情報の更新で取り込み、Enum の定義に写す** (SpecDocs で AI に指示)。画面のたびに kintone から読まない
- **SQL・ストアドプロシージャ (ExecuteSqlCommand) は拒否。**
- **Starter (ホストのテンプレート) には組み込まない。** 使うアプリが 2 行足す

## 守ること

- 本体は core のリポで変える (このリポから直接編集しない)。足りない口はユーザーに相談
- ホスト (Starter) に置くべきもの (Controller・DI の要求) をライブラリに持ち込まない
- 実物の kintone で確かめていないことを「確認済み」と書かない
- 公開リポジトリ。ホスト名・アカウント・トークン・パスワードの実物をコード・テスト・ドキュメントに書かない
- 利用者向けの文書 (README・Docs) は日本語。会社名の表記は Codeer

## 未検証・未実装 (2026-11-16 時点)

**実物の kintone には一度もつないでいない。** テストは HTTP の偽物とモックが相手。モックは `Docs/01` の記述から作ったもので、実物の動きの裏付けではない。

実物で確かめること:

- 数値・日付のフィールドの空の検索が `= ""` / `!= ""` で書けるか
- `!=` / `not in` / `not like` / 大小の比較が、空の行をどう扱うか (空の条件を明示して書いているので、どちらでも結果は同じになるはず)
- `in (...)` の値の数の上限・クエリの長さの上限 (今は値 100 個ずつ)
- 数値・`$id` の値を引用符なしで書けるか (`$id > 123`・`数値 in (1, 2)`)
- `limit 1` + `totalCount` の件数、offset 10000 の境界
- order by で空の値が並ぶ位置、並べられないフィールドの種類
- like の一致のしかた (語の区切り)
- 業務エラーの HTTP ステータス (520 の使われ方) とエラーコード (GAIA_RE01 / GAIA_CO02) の出方
- updateKey の更新で、record にキーのフィールドを入れたときの動き (今は入れない)
- 空の record の PUT で版が進むか
- テーブルの PUT (id つきの行は送った項目だけ変わる・無い行は消える)
- 2 要素認証・SAML の利用者のパスワード認証 (ログイン)
- ゲストスペース・Basic 認証・クライアント証明書の環境 (Basic 認証・クライアント証明書は未対応)

未実装:

- テーブルの行を親をまたいで読む表、作成者などの表示名の列
- 添付ファイルの転送、プロセス管理の操作、コメント
- 利用者本人の権限での読み書き (OAuth など)、カーソル API、一括の書き込み API (bulkRequest)
- 日付の期間の集計の軸
