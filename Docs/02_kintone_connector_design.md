# Codeer.LowCode.Blazor.Connectors.Kintone — 仕様・設計案

2026-11-16 ｜ 初版の実装引き継ぎ用 ｜ [kintone API調査](01_kintone_api_inventory.md)

**利用者が行う設定は「このkintoneアプリを使う」「この項目を画面に置く」の2つを基本にする。** CLBの通常Moduleをそのまま使い、DataSourceを選ぶとkintoneの運用中フォームがテーブル定義として見える。たとえば、kintoneの受注アプリを指定し、受注番号・顧客名・日付を一覧に、商品・数量を明細に置く。保存結果は元の受注アプリで確認できる。URL、HTTPメソッド、JSONの対応表をModuleごとに手で設定する作業は設けない。

## 1. 初版の範囲と採用理由

| 項目 | 初版の仕様案 | 拡張候補 |
|---|---|---|
| 接続単位 | 1 DataSource＝1 kintoneアプリ。1アプリから用途・アクセス権の違う複数Moduleを作成可 | 利用者OAuthによるアプリ検索・個人権限での実行 |
| 既存アプリの取込 | アプリIDを指定。公開中のフィールド、選択肢、テーブル内列、読取専用属性を保存。Designer・AIはオフライン利用可 | kintoneの既存レイアウトや一覧の取込 |
| 基本CRUD | 一覧、対応する検索・ソート、件数、詳細、作成、元の版を指定した更新・物理削除 | 独自の論理削除設定、一括upsert、大量エクスポート |
| 明細 | SUBTABLEは親のListField＋行のJsonFieldで表示・編集。ヘッダーと同じ要求で保存 | 独立した別アプリを子Moduleにする関連一覧 |
| 選択・参照 | ラジオ／ドロップダウンは既存の候補から選択。ルックアップ値とコピー済み列は表示。複数選択・ユーザー・添付等は表示から開始 | ルックアップ元の検索と更新、ユーザー選択、添付転送、プロセスのアクション |
| 問い合わせ | 1アプリ内で条件・投影・順序・件数をAPIへ渡す | 業務キーを明示した別アプリ参照。任意SQL、横断JOINや汎用集計はサポート対象に含めない |

1アプリ＝1 DataSourceを選ぶのは、トークンと権限の発行単位をそのまま運用設定にでき、Designerで安定した表名を使えるため。アプリ一覧APIに追加の管理用資格情報を要求せずに動く。将来、1接続で多数アプリを扱う要望が出たら、アプリIDと資格情報の組を複数登録する設計に拡張できる。[アプリ一覧の認証方式][APPS]・[トークンの仕様][AUTH]

## 2. 既存コードで確認した接続点

| 確認箇所 | 継承する実装・設計 | kintone固有の実装 |
|---|---|---|
| `FileMakerDbAccessor`、`NetSuiteDbAccessor` | `IDbAccessor` を包み、対象外データソースはinnerへ委譲。通常Moduleのアクセス制御・保存経路を保つ | `KintoneDbAccessor` で対象を選別。FileMaker版と同じPostgreSQL方言をCLBに提示 |
| FileMaker版の `DataService` と `IDbAccessorFactory.Create` | Serverの `CustomizedModuleDataIO` にラッパーを渡す。DesignerのFactoryでも同じラッパーを使う | kintoneのアプリIDとトークン参照を接続ごとに解決。SQL DBおよび両既存コネクターとも併用可 |
| `FileMakerDesignerInitializer` とNetSuiteのメタ情報保存 | `Initialize` でAI向け仕様を登録、`Setup` でToolsメニュー追加。手動更新時のみ通信し、成功時に `RefreshDatabase()` | スキーマは環境別アプリIDから取込む。更新中に失敗しても直前のファイルを保持 |
| `ODataTranslator` と `SuiteQlTranslator` | CLBの生成SQLとパラメーターを読み取る境界、列別名復元、非対応形を明示する考え方 | 読み替える先はkintoneの型別クエリ。ODataやSuiteQLの式、NetSuiteの列名小文字化は移植しない |
| NetSuiteの `ListField.DbColumn` と `NetSuiteSublistJson` | 親ModuleにJSON配列を格納するListField、行ModuleにJsonFieldを置く形 | 行の照合キーはテーブル行ID。残す行を全て列挙し、親の元のrevisionで競合を防ぐ |
| FileMakerの `MockWriteTest` | 通常のSubmitと削除時の `ModuleDeleteInfo.OptimisticLockingFieldData` で旧版が伝わる試験を参照 | 版のWHERE条件をkintoneの更新・削除要求のrevisionに移し、CLBからは版を加算しない |

参照したコミットはFileMaker `a0227e376f14`、NetSuite `d7d35a1703ae`。[FileMakerアクセサ][FMACCESS]・[Serverへの組込み][FMHOST]・[Designer初期化][FMDES]・[FileMakerのSQL変換][FMT]・[NetSuiteのSQL変換][NST]・[NetSuiteメタ情報][NSMETA]・[明細の変換][NSSUB]・[FileMakerの保存試験][FMTEST]

## 3. プロジェクトと公開設定

既存2版の構成に合わせてServerとDesignerを別パッケージにする。最初の互換候補は既存版のServerと同じ `net8.0`、Designerは `net8.0-windows` / WPF。参照元で使用しているCLB本体1.3.68、Designer 1.3.32を最初の統合試験の基準にする。CData等の外部ドライバは不要。Serverから `HttpClient` と `System.Text.Json` で接続する。以下は新規クラスの**提案名**。[既存Serverプロジェクト][NSPROJ]・[既存Designerプロジェクト][FMPROJ]

| 構成 | 責務 |
|---|---|
| `Codeer.LowCode.Blazor.Connectors.Kintone.Server` | `KintoneDbAccessor` の振分け・戻り値、`KintoneConnection` のHTTP・認証・エラー、`KintoneQueryTranslator` の条件・投影、フォームとCLB列の対応、明細変換 |
| `Codeer.LowCode.Blazor.Connectors.Kintone.Designer` | `KintoneDesignerInitializer`、メタ情報更新、フィールド型・選択肢の提示、`SpecDocs/Kintone.md` の登録 |
| `Kintone.Mock` とテスト | 機密を含まない固定フォームとレコード、リビジョン競合、明細置換、障害応答を再現。公式仕様・実機の採取例と突き合わせる |
| `Example` | 受注・明細の通常Module、ローカルDBのModuleを同じアプリで動かす。Designerで取込→Module作成→通常画面で保存するまでを見せる |

**設定例**。同じ論理名 `KintoneOrders` を開発・本番で使い、環境ごとにHost、AppId、認証情報を差し替える。ここで `ApiTokenKey` はホスト側の設定キーで、実際のトークン値をCLBのデザインに書き込まない。

```json
{
  "DataSources": [{
    "Name": "KintoneOrders",
    "DataSourceType": "PostgreSQL",
    "ConnectionString": "Provider=Kintone;Host=https://sample.cybozu.com;AppId=42;ApiTokenKey=Kintone:Orders:Token"
  }]
}
```

サーバーでは `IConfiguration["Kintone:Orders:Token"]` 等で値を解決する。Designerには同名のローカル設定で閲覧用トークンを割り当てられる。任意設定は `GuestSpaceId`、`TimeoutSeconds`、`MaxRows`、`MaxConcurrency`。後者は接続ごとの独立枠を増やさず、同一ホストで枠を共有する。HTTPS起点と数値IDを検証し、リダイレクト先へトークンを送らない。対象Providerで設定不備なら設定エラー、別Providerはinnerへそのまま渡す。

## 4. Designerで受注Moduleを作る

1. Designerのデータソースに `KintoneOrders` を登録し、［Tools］→［kintoneのメタ情報を更新］。運用中のアプリ42からフォームを取得する。
2. `kintone.KintoneOrders.metadata.json` をデザインプロジェクトに保存。記録するのは形式版、取得時のアプリ設定版、論理表、フィールドコード・型・ラベル・必須・一意・選択肢・自動計算・コピー先・テーブル内列。実レコードやトークンは含めない。別プロセスと同時更新する場合は完成したスナップショット単位で読み替え、途中のJSONを読ませない。
3. 標準のテーブル選択で `KintoneOrders → Records` を選び、受注Moduleを作成する。固定の論理表名 `Records` はテナントのアプリ表示名やアプリIDが変わっても維持する。取り込んだ列は標準のDbColumn候補へ出す。
4. 下表の項目で一覧と詳細を作る。自動的に関連付けられない設定は、Designerの診断と埋込のAI向け仕様に必要値を表示する。編集可能にしたModuleには、システムIDと楽観ロック両方の結付けを検査する。
5. フォーム変更時は差分を表示。ラベルだけの変更、追加列、既存コードの消失、型変更、選択肢の削除を区別し、既存のField名・画面配置を保持する。コードの変更をラベル一致で自動再結合しない。初回取込前は更新方法を表示する。

| CLB側 | 受注Moduleでの設定例 | kintone上の意味 |
|---|---|---|
| DataSourceName / DbTable | `KintoneOrders` / `Records` | 接続設定のアプリ42 |
| IdField | `DbColumn=__kt_id`、自動採番 | `$id`。レコード番号の表示値や業務キーと区別 |
| OptimisticLockingField | `DbColumn=__kt_revision`。読み出した元値を保持、`IncrementVersion=false` を最初に検証 | `$revision`。クライアントの時刻や計算値は使用しない |
| 顧客、受注日、進捗 | `CustomerName`→TextField、`OrderDate`→DateField、`Stage`→SelectField | 進捗は業務用のDROP_DOWN。プロセス管理のSTATUSとは別 |
| 明細 | 親 `ListField.DbColumn=Lines`。行ModuleはDataSource・DbTableなし、ルートJsonFieldに商品・数量・非表示の行IDノード | 親のSUBTABLE。行のDbColumnには `Product`・`Quantity` を使う |
| 更新日時、計算金額 | `IsViewOnly`。サーバーの書込許可リストからも外す | 保存後の値は再読込で取得 |

外部コードの大文字小文字・日本語をそのまま保存する。システム別名 `__kt_id`、`__kt_revision`、行ノード用 `__kt_row_id` と実フィールドコードが衝突した場合は、スナップショットに衝突のない外部列用別名を保存し、以後同じ対応を使う。物理コードを別名へ変更する要求は送らない。選択肢はインデックス順で候補化する。開発と本番でアプリID／フォーム版番号が違っても、Moduleが使用するコード・型・選択肢などの互換性を確認して配備できる。[既存Designerの更新][FMDES]・[明細Moduleの構成例][NSDOC]・[フォームの定義][FORM]

## 5. 実行時のルーティングと読取契約

Serverの `DataService` は既存のDbAccessorを作成し、外側を `KintoneDbAccessor` で包んでModuleDataIOに渡す。メタ情報付きの同アクセサはDesignerの `IDbAccessorFactory` から返す。標準のGetList／Submitに別の公開APIを増やす必要はない。FileMaker・NetSuiteを併用する場合は判定に一致したラッパーだけが処理し、他はinnerへ渡す。[既存の組込み例][FMHOST]・[既存アクセサ][FMACCESS]

| CLBの呼出し | kintone用の処理 | 呼出し元への契約 |
|---|---|---|
| `GetDataSource` / `GetCustomTableDefinitionsAsync` の2オーバーロード | 対象データソースをPostgreSQL方言として提示し、単一表Recordsと型付き列定義を返す | Designer・AIの通常参照はローカル定義。認証情報は公開しない |
| `QueryAsync` の一覧・詳細 | CLB生成SQLのSELECT列・別名、WHERE、ORDER、LIMIT、OFFSET、型付き引数を解析する。対象アプリ、許可列、行条件を確定後、kintoneのfieldsとqueryに変換 | 戻り値辞書のキーはCLBのSELECT別名。補助取得したID・revision・明細元値を未許可の画面列として公開しない |
| `QueryAsync` の件数 | 単一表の `COUNT(*)` 相当を、同じ検索／認可条件で `totalCount=true`、最小投影・少量取得に変換 | 文字列件数を範囲検査して数値化。null・打切り警告を確定件数0として返さない |
| `InsertAsync` / `ExecuteAsync` | INSERTは新規作成、UPDATEは単一ID＋旧版指定、DELETEは単一ID＋旧版配列。通常のBulkAdd経路は後述 | 採番ID・成功件数／例外を返す。更新・物理削除の失敗は通常Submitの画面にも伝える |
| 接続・トランザクション | 他データソースの `GetConnection` 等はinnerへ。kintoneの直接SQL／StoredProcedureはサポート外。ローカルDBの取引開始・終了は既存通り | 既にkintoneで完了した要求はCLBのSQL Rollbackで戻らない |

**変換例**。画面で「進捗＝受注」「受注日≧11月1日」、受注日の降順、20件ずつの2ページ目を指定した場合は、下記に相当する検索を送る。実際のSQL文字列は対象CLBバージョンの統合試験で採取し、生成形を固定する。

```json
{
  "app": 42,
  "fields": ["$id", "$revision", "CustomerName", "OrderDate", "Stage"],
  "query": "(Stage in (\"受注\") and OrderDate >= \"2026-11-01\") order by OrderDate desc, $id asc limit 20 offset 20",
  "totalCount": true
}
```

- 型付き引数→値リテラル変換と、列定義→外部コード変換を分ける。条件の括弧・AND／ORを維持。NULLと空文字、文字列と数値、ドロップダウンとテキストには型別規則を適用する。型ごとの空欄検索・否定でCLBの期待結果と一致することを確認した組合せから有効化する。
- `MatchComparison.Like` を表示する場合は「キーワード検索」と明示する。SQLのワイルドカードや厳密な前方・後方一致を同じ意味として変換しない。未対応の条件・JOIN・集計に対しては、条件を落とす前に例外を返す。サーバー上の行制約も同じ変換対象に含める。
- 1回のlimitは最大500。明示limitが大きい場合は同一条件・順序で分割し、offset上限を越える必要があるときは範囲外を返す。ページなしの検索は `MaxRows` を超えたら取得過多として条件の絞込みを求める。無制限取得・黙示的な切り捨てを避ける。詳細のIDが分かっていても行制約・投影が必要な場合は `records.json` で取得する。
- GETのページングは更新と並行すると境界が動き得る。最終ソートに一意なIDを補う。件数と一覧が別々の `QueryAsync` として到来するCLB経路では、無条件に1要求に合流させない。キャッシュする場合は同一要求、同一主体・条件・権限内に限る。[クエリ][QUERY]・[一覧][LIST]

## 6. 書込契約と障害時の扱い

### 親レコード

| 操作 | サーバー側の処理 |
|---|---|
| 追加 | デザインと運用フォームで許可された入力済み列だけをPOST。未入力はフィールド単位で省き、既定値が使えるようにする。既知の読取専用・非許可列への明示的な変更は拒否。返ったidをCLBのDestinationIdにし、必要なら親を再読込して計算値・行ID・新しい版を反映 |
| 編集 | 開いた時点の `$id`・`$revision` をSubmitまで運ぶ。単純な場合は変更列＋元の版を一度のPUTに載せる。行制約を確かめる必要があれば、ID＋許可条件で照合し、許可された行を特定してから**元の版**でPUTする。サーバー管理列へ版を書き込むSETは生成しない |
| 削除 | IDと元の版を1要素ずつの `ids`・`revisions` に入れてDELETE。件数を見ないCLB側の経路でも競合・権限不足・失敗を正常終了させない。削除済みを競合／不存在として扱う条件は実応答のコードで検証 |
| 編集結果 | PUTの返却版、または保存後再取得の版を画面に反映。更新成功後の再読込に失敗した場合は「保存済み・再表示が必要」とし、書込を繰り返さない |

旧版の伝達は最初の技術検証項目。 `OptimisticLockingFieldDesign` をシステム列に結び、更新時の生成WHEREと削除時の `OptimisticLockingFieldData` に旧版が現れるか、**通常UI→次のHTTPリクエスト**まで調べる。未指定・−1は書込前に拒否。アクセサだけで旧版が得られない経路には、既存アプリの `CustomizedModuleDataIO` の対象Module限定フックで保存コンテキストを渡す。保存直前にGETした最新版を旧版の代わりにしない。[保存試験の先例][FMTEST]・[単件更新][UPDATE]・[削除][DELETE]

新規POSTは未知コードがあっても成功し得る。初版では**1回のSubmit内の各対象アプリについて最初の書込前に運用フォームを取得**し、使用列・型・選択肢・書込可否を配備した定義と比較する。非互換なら該当項目を知らせ、レコード書込要求の発行前に止める。フォーム照合と書込をまたぐ設定変更は原子的に防げないため、運用中にフォームを改版する案件は変更後のスモークテストで保存結果まで確認する。複数レコードSubmitで照合を繰り返さない。[レコード登録][ADD]

### SUBTABLEの編集

テーブルは親1件の保存単位。v1では検索・ページングで一部しか出ない明細を編集対象にしない。行表示と親の保存の間で必要な情報は、元の親ID・元のrevision、残す行の順序、既存行ID、追加・変更・明示削除の区別。予約ノード `__kt_row_id` は行ModuleのJSON往復で保持する。既存行の値は許可された列だけ返す。

1. **表示**。親詳細からテーブルを1回で取得。既存の行IDを保ったまま行Moduleに展開する。Designerで明細の絞込・ページ上限・行ID欠落を検出した場合は明細編集を無効にする。
2. **ヘッダーのみ変更**。明細列をPUTから省く。未表示、未ロード、編集なしを空配列へ変換しない。
3. **明細の変更**。保存前に、必要に応じて現在の親を読み、認可・旧版一致・テーブルの既存行IDを確認する。未編集列まで差分扱いしない。残す行はID、変更行はID＋変更列、新規はIDなし。明示した削除行だけを省き、並べ替えは最終行順で送る。行全削除の意図があれば `value: []`。
4. **差分の出所**。CLBが完成配列のみ渡す場合は、完全にロードした編集対象の元の行ID集合・同じ版と突き合わせる。元の完全性を確認できない場合や同一IDの重複・別の親から来た行IDを検出した場合は書込前に停止。必要ならSubmitフックで削除行ID／明細編集の有無を明示して伝える。単にJSONが欠けただけで全削除にしない。
5. **確定**。親の変更列と明細をまとめ、元の親revisionを指定した1回のPUT／新規は1回のPOSTで送る。競合は再読込へ。成功後に行IDを再取得してから次の編集を許可する。APIへの追加・更新・削除を明細行ごとに繰り返さない。

[API送信例](01_kintone_api_inventory.md#6-保存時にデータを失わない条件)・[kintoneのテーブル更新仕様][UPDATE]・[NetSuiteのListField実例][NSDOC]

### 異常系・複数件

| 状態 | CLB画面／呼出し元に返すこと | 自動処理 |
|---|---|---|
| 認証・権限・入力エラー | 対象Module、操作、項目単位のエラー。kintoneの `code`・問い合わせ用 `id`、HTTPステータス、返れば `errors` を記録。管理者ログと利用者表示を分ける | 書込を再送しない。トークンや業務データ本文は通常ログに出さない |
| 旧版による競合 | 「他の利用者が更新しました。再読み込みしてください」。現在値を保つ | 自動マージ／強制上書きしない |
| 429・一時障害 | 検索は再試行後に失敗理由を通知。ドメイン枠・日次枠・検索打切りを区別 | 通常の読取は回数を制限した指数バックオフ＋揺らぎ。Retry-Afterが返れば尊重。カーソルGETは位置を進めるため別扱い |
| 作成・更新・削除の送信後にタイムアウト／応答喪失 | 「結果未確定」。既知ID・業務キー等で照合する。作成IDが不明で一意な業務キーもないときは、管理者が照合するまで手動再開 | POSTを含む書込の一律再送なし。HTTPverbがPUTでもプロセス遷移等を機械的に再送しない |
| 複数レコードSubmitの途中失敗 | 確定した行のID、失敗行の位置、未実行範囲、結果不明の行を分ける | 初版は既存版と同様に順次書込。CLBの複数行INSERT形も行別に処理し、成功分を再送しない。単一の親とテーブルは常に1要求 |
| 同じSubmitにSQL DB・複数アプリが混在 | kintone側で既に確定した更新を示す。ローカルDBのロールバックと外部の確定状況を別々に通知 | `bulkRequest` は将来、対象操作と参加アプリを限定した一括機能として検討する |

共有トークン利用時、ブラウザーから届いたフィールド、レコードID、表示済みフラグだけで更新を許可しない。ServerがModule定義・利用者権限・行条件・操作可能な列を解決し、明細内にも適用する。認可条件をkintoneで表現できないModuleは該当操作を無効にする。認可対象外のレコードの存在をメッセージで明かさない。[認証][AUTH]・[既存版での部分失敗の扱い][NSACCESS]

## 7. 実装順と受入試験

検証用アプリ「受注」に `ExternalNo`（重複禁止文字列）、`CustomerName`、`OrderDate`、`Stage`（仮／受注）、`Amount`（数値）、`Lines`（文字列Product・数値Quantity）、コピー先項目・計算列を用意する。明細2行の既知レコードを作り、レコード閲覧のみのトークンとCRUD用トークンを発行。アプリコードも設定して表示番号と内部IDの取り違えを検出する。[開発者ライセンス][DEV]・[疎通用PowerShell](01_kintone_api_inventory.md#7-運用と最初の疎通)

| 順 | 実装／試験 | 合格条件 |
|---|---|---|
| 1 | 既存版と同じCLBから実際のSQL・引数・別名を採取。List／Count／詳細／Insert／Update／Delete、元revisionの往復、明細行IDの往復を記録 | 通常エントリーで必要な情報が残る。足りない値は対象限定のSubmitフックで明示的に渡せる。SDK単体で通っただけでは合格にしない |
| 2 | APIトークンで公開フォームを取得。DesignerのTools更新、オフライン再起動、ai-refresh、フィールドコードの変更・ラベル変更・通信断 | 定義・選択肢・明細内列が出る。ラベルだけの変更は既存バインドを維持。削除／型変更を通知。通信断で旧スナップショットを保持。Git差分に秘密・実データなし |
| 3 | 一覧で2条件AND・OR、0件、同順位、日付境界、引用符・バックスラッシュ、日本語、選択肢、閲覧外の行を含むデータを照合 | 期待ID集合・順序・件数がkintoneと一致。別名と型も一致。未対応JOIN・検索はHTTP発行前に診断。許可外は件数にも出ない |
| 4 | 500／501件、offset 10,000／10,001、MaxRows超過、キーワード打切りの模擬応答 | 対応範囲で重複・欠落がない。対応外は明示エラー。打切りを正確な総件数と表示しない |
| 5 | 通常UIで新規→2回続けて編集→削除。途中で別画面から変更し古い版で更新／削除。POST後の応答のみ喪失も注入 | 受注番号などの表示用・業務用の番号と内部IDを混同せず、採番・新revisionが次画面に伝わる。競合は片方だけ成功。旧版欠落は未送信。喪失時は同じPOSTを二度送らない |
| 6 | 明細2行A・Bで、ヘッダーのみ→Aの数量変更＋C追加→B削除・並べ替え→明示全削除。別画面で明細を増やしてから古い画面で保存。行ID欠落・部分ロードも試す | 各段階で残す行のID・未編集列・順序が一致。未編集のテーブルは要求に含まれない。競合で他者の行が消えない。不完全な一覧からの保存は拒否 |
| 7 | 未送信／空文字／null／空配列、必須・重複、一部列のみ表示、フォームのコード変更、ロール別アクセス、閲覧用トークンでの保存 | 未入力で既定値が生きる。クリアは対象列のみ。変更済みフォームに対する誤列・権限不足を成功扱いしない。閲覧者の列・行権限が維持される |
| 8 | ローカルDBと同時登録したDataSourceで各通常画面を操作。3件中2件目を失敗させる。エラー後に確定分を照合 | 既存DB操作は従来どおり。1件目確定、2件目失敗または結果不明、3件目未実行を区別。再開で1件目が重複しない |
| 9 | 実テナントで「Designerから受注Module作成→一覧検索→明細を含めて登録・編集→kintoneで照合」。通常／ゲストスペース、読み取り専用、再読込後の値も確認 | 公開対象の構成ごとに、匿名化した要求・応答、画面での結果、未対応機能を記録する。Mockの成功と実機の合否を分ける |

**最初の着手点**は試験1と、実テナントでの「フォーム取得・1件取得・元revision付き更新」。この3要求とCLBの通常Submitの形が揃えば、読み書きの境界を確定できる。続いて受注Moduleと明細1本を縦に通してから対応型を広げる。今回の成果物はAPI調査と設計案までで、コネクター実装、テナント接続、受入試験は未実施。

[FMACCESS]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Server/FileMakerDbAccessor.cs
[FMHOST]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Example/LowCodeApp.Server/Infrastructure/DataService.cs
[FMDES]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Designer/FileMakerDesignerInitializer.cs
[FMT]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Server/ODataTranslator.cs
[FMTEST]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Test/MockWriteTest.cs
[FMPROJ]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/a0227e376f140aa9fabc753260205e5f1dd8703a/Source/Codeer.LowCode.Blazor.Connectors.FileMaker.Designer/Codeer.LowCode.Blazor.Connectors.FileMaker.Designer.csproj
[NSMETA]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteMetadataFile.cs
[NSSUB]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteSublistJson.cs
[NSACCESS]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs
[NST]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/SuiteQlTranslator.cs
[NSPROJ]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/Codeer.LowCode.Blazor.Connectors.NetSuite.Server.csproj
[NSDOC]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/d7d35a1703aeb2456397787074d6e1d7fe8d20c9/Docs/NetSuite.md
[AUTH]: https://cybozu.dev/ja/kintone/docs/rest-api/overview/authentication/
[APPS]: https://cybozu.dev/ja/kintone/docs/rest-api/apps/get-apps/
[FORM]: https://cybozu.dev/ja/kintone/docs/rest-api/apps/form/get-form-fields/
[LIST]: https://cybozu.dev/ja/kintone/docs/rest-api/records/get-records/
[QUERY]: https://cybozu.dev/ja/kintone/docs/overview/query/
[ADD]: https://cybozu.dev/ja/kintone/docs/rest-api/records/add-record/
[UPDATE]: https://cybozu.dev/ja/kintone/docs/rest-api/records/update-record/
[DELETE]: https://cybozu.dev/ja/kintone/docs/rest-api/records/delete-records/
[DEV]: https://cybozu.dev/ja/kintone/developer-license/
