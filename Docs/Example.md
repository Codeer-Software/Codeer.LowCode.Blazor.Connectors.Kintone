# サンプルアプリ (Source/Example) を手元で動かす

`Source/Example` は、コネクターを組み込んだ Web アプリ (Cookie 認証のテンプレート) とデザイナ、そのデザインのプロジェクトです。
kintone のモックにつないで、顧客・商品・受注 (テーブルの明細つき) の一覧・検索・詳細・保存と、
ルックアップ・リンク越しの項目での検索と並び・集計・kintone のアカウントでのログインを試せます。

| フォルダ | 内容 |
|---|---|
| `LowCodeApp.Server` / `LowCodeApp.Client` / `LowCodeApp.Client.Shared` | Web アプリ。コネクターの組み込みは `LowCodeApp.Server/Infrastructure/DataService.cs` の 2 行 |
| `LowCodeApp.Designer` | コネクターを組み込んだデザイナ |
| `Design` | デザインのプロジェクト (`app.clprj`)。モジュール `Customer` / `Product` / `Order` / `OrderLine` (kintone)、`SalesByCustomer` (見本の画面) と `AppUser` (ログインするユーザー・SQLite) |
| `Data/main.sql` | ログインするユーザーの表と、最初のユーザー (admin / admin)・kintone のアカウントでログインするユーザー (山田 太郎) |

.NET 10 の SDK が要ります (デザイナとモックは .NET 8)。CLB のライセンスを登録したマシンで動かします。
以下のパスは例です。変えるときは、それぞれの設定を同じ値にそろえます。

## 1. モックを起動する

```
dotnet run --project Source/Kintone.Mock
```

`http://localhost:5084` で待ち受けます。モックのアプリは 顧客 (アプリ ID 1)・商品 (2)・受注 (3) です。接続文字列は次のとおりで、このあと 2 か所で使います。

```
Provider=Kintone;Host=http://localhost:5084;Apps=Customer:1,Product:2,Order:3;ApiTokens=mock-token-customer-all,mock-token-item-all,mock-token-order-all
```

- `Apps` の `Customer` / `Product` / `Order` は、デザインのモジュールの表名 (`DbTable`) です。実物の kintone につなぐときは、アプリ ID をその環境のものにします (表名は変えません)
- `ApiTokens` はモックのアプリの API トークン (架空のもの) です。受注のルックアップで顧客のアプリも読むので、3 つとも並べます

モックを止めるとデータは最初に戻ります。モックの使い方は [KintoneMock.md](KintoneMock.md) にあります。

## 2. デザイナの設定を作る

`Source/Example/Design/designer.settings.Development.json` を作ります (リポジトリには入れません)。

```json
{
  "ConnectionStrings": {
    "Main": "Data Source=C:/Codeer.LowCode.Blazor.Local/Data/kintone_example.db;",
    "Kintone": "Provider=Kintone;Host=http://localhost:5084;Apps=Customer:1,Product:2,Order:3;ApiTokens=mock-token-customer-all,mock-token-item-all,mock-token-order-all"
  },
  "CurrentDeployInfoName": "local",
  "DeployInfo": {
    "local": {
      "DeployMethod": "FileSystem",
      "AppName": "App",
      "Directory": "C:\\Codeer.LowCode.Blazor.Local\\Designs\\Kintone"
    }
  }
}
```

アプリとフィールドの定義は `Design/kintone.Kintone.metadata.json` (モックの定義から作ったもの) から読みます。モックのデータのファイルを変えたときは、デザイナで Tools > kintone のメタ情報を更新 を実行します。

## 3. ログインするユーザーの DB を作る

`C:\Codeer.LowCode.Blazor.Local\Data` のフォルダを作ってから、デザイナをビルドして (WPF なので VS の MSBuild)、`sql` コマンドで `Data/main.sql` を流します。

```
LowCodeApp.Designer.exe sql Source/Example/Design --datasource Main --file Source/Example/Data/main.sql --out sql-result.json
```

## 4. デザインを送る

デザイナで `Source/Example/Design/app.clprj` を開き、デプロイします。`C:\Codeer.LowCode.Blazor.Local\Designs\Kintone\App.zip` ができます。

## 5. アプリを起動する

kintone の接続文字列は `appsettings` に書かず、環境変数で渡します (PowerShell の例)。

```
$env:ConnectionStrings__Kintone = "Provider=Kintone;Host=http://localhost:5084;Apps=Customer:1,Product:2,Order:3;ApiTokens=mock-token-customer-all,mock-token-item-all,mock-token-order-all"
dotnet run --project Source/Example/LowCodeApp.Server --launch-profile http
```

`http://localhost:5085` を開き、admin / admin でログインします。メニューの「顧客」から一覧・検索・詳細・保存を試せます。保存はモックのデータに入ります。

### kintone のアカウントでログインする

ログインの画面で「kintone のアカウントでログイン」にチェックを付け、モックのユーザー `yamada.taro` / `mock-yamada-pass` (架空のもの) でログインします。ホームに「ログイン中: 山田 太郎」と出ます。

- アプリ (`AccountController.LoginKintone`) は、kintone (モック) にそのログイン名とパスワードで入れるかを確かめ、そのログイン名で `AppUser` の kintone ログイン名の列 (`kintone_login_name`) を引いてログインさせます
- `AppUser` に行が無いユーザー (モックの `suzuki.hanako`) と、パスワードの違うユーザーはログインできません
- ログインのあとのデータの読み書きは、接続文字列の API トークンのままです (kintone の更新者は API トークンの扱いになります)
- 設定は `appsettings.json` の `KintoneLogin` (`DataSourceName` = 確かめる kintone のデータソース)。`DataSourceName` を空にすると、チェックボックスは出ません
- 失敗が続いたログイン名は、ID / パスワードのログインと同じ設定 (`LoginLockout`) でロックします

| モジュール | 内容 |
|---|---|
| `Customer` (メニューの「顧客」) | kintone の 顧客 アプリ。Id は重複禁止の顧客コード (手入力)。状態 (ドロップダウン)・ランク (ラジオボタン) は Enum の選択、更新日時は表示だけ。詳細の下に「この顧客の受注」の一覧 (関連レコード一覧に当たるもの) |
| `Product` (「商品」) | 商品 アプリ。Id は kintone のレコード ID (`$id`)。レコード番号 (アプリコードつき) は表示だけ、分類は Enum の選択 |
| `Order` (「受注」) | 受注 アプリ。顧客へのリンク (ルックアップのキー `CustomerCode`)・ルックアップでコピーされた顧客名・計算フィールドの合計・プロセス管理のステータス (どれも表示だけ)・楽観ロック (`$revision`) と、テーブルの明細 |
| `OrderLine` | 受注のテーブル `Lines` の行 (行の JSON をノードにした JsonField)。商品は商品のモジュールを候補にした選択、小計 (計算) は表示だけ。メニューには出しません |
| `SalesByCustomer` (「顧客別の売上」) | 受注の合計金額を 顧客 × 進捗 で集計するクロス集計 (スクリプトの `ModuleAggregator`)。金額の多い 5 社だけを出し、合計もその 5 社の分 |

そのほかの見本:

- 受注の一覧に顧客名 (リンク越しの項目 `Customer.CustomerName`) を出し、顧客名で検索・並べ替えます (並びはページフレームの受注のリンクの設定)
- 明細は受注の詳細の中で足す・直す・消すことができ、受注と一緒に 1 回で保存されます。小計と合計は保存のあとで読み直すと出ます
- 選択肢の Enum (`Design/Enums/顧客状態.enum.json` など) は、メタ情報のファイルの選択肢 (`Options`) を写したものです。kintone には選択肢の文字がそのまま入ります

DB とデザインの置き場所は `LowCodeApp.Server/appsettings.Development.json` (`ConnectionStrings:Main`・`DesignFileDirectory`) です。環境変数 `ConnectionStrings__Main`・`DesignFileDirectory` でも上書きできます。
