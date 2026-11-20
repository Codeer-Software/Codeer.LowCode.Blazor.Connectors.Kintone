# Codeer.LowCode.Blazor.Connectors.Kintone

[Codeer.LowCode.Blazor](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual) のモジュールを、kintone のアプリに結び付けるコネクターです。

kintone をデータソースの 1 つとして登録すると、DB のテーブルと同じ手順で kintone のアプリからモジュールを作れます。
一覧・検索・詳細・保存といったいつもの操作が、そのまま kintone のレコードの読み書きになります。

- 一覧・検索・件数・作成・更新・削除は kintone の REST API
- 認証はアプリの API トークン (またはパスワード認証)
- テーブル (サブテーブル) は親のモジュールの一覧として、親と一緒に読み書き
- 楽観ロックは kintone のリビジョンで、kintone が古い版の更新・削除を弾く
- kintone のアカウントでアプリにログインする部品 (アカウントの確認) つき
- 添付ファイルの一覧・ダウンロード・追加・削除ができるフィールドつき
- kintone 側へのプラグインやカスタマイズの配備は不要

> この版は実物の kintone での動作確認がまだ済んでいません。検証用の kintone の環境で動作を確かめてから使ってください。

## 使い方

アプリ (サーバー) に `Codeer.LowCode.Blazor.Connectors.Kintone.Server`、デザイナに `Codeer.LowCode.Blazor.Connectors.Kintone.Designer` を追加します。
添付ファイルのフィールドを使うときは、アプリの画面のプロジェクトに `Codeer.LowCode.Blazor.Connectors.Kintone` も追加します (組み込みは [Docs/Kintone.md](Docs/Kintone.md) の「添付ファイル」)。

データソースに kintone の接続を書き、

```json
{
  "Name": "Kintone",
  "DataSourceType": "PostgreSQL",
  "ConnectionString": "Provider=Kintone;Host=https://example.cybozu.com;Apps=Customer:12,Order:13;ApiTokens=<APIトークン>,<APIトークン>"
}
```

アプリとデザイナの `IDbAccessor` を包みます。

```csharp
//アプリ (サーバー。アプリの定義は、デザインと一緒に配られたメタ情報のファイルから読む)
var kintone = new KintoneDbAccessor(new DbAccessor(dataSources), KintoneConnection.FromDataSources(dataSources),
    KintoneMetadataFile.FromDeployedDesign(designFileDirectory));

//デザイナ (アプリの定義は、Tools > kintone のメタ情報を更新 でプロジェクトに保存したファイルから読む)
var kintone = KintoneDesignerInitializer.CreateDbAccessor(new DbAccessor(dataSources), dataSources);
```

デザイナのアプリの `OnStartup` では、`base.OnStartup` の前に `KintoneDesignerInitializer.Initialize(DesignerEnvironment)`、後に `KintoneDesignerInitializer.Setup(DesignerEnvironment)` を呼びます。

設定の項目・モジュールの作り方・動きと制限は [Docs/Kintone.md](Docs/Kintone.md) にあります。

kintone の環境が無くても、手元で動くモック (`dotnet run --project Source/Kintone.Mock`) にアプリとデザイナをつないで試せます。使い方と、実物と違う点は [Docs/KintoneMock.md](Docs/KintoneMock.md) にあります。

コネクターを組み込んだ Web アプリとデザインのサンプルが `Source/Example` にあります。モックにつないで動かす手順は [Docs/Example.md](Docs/Example.md) にあります。

## 制限

- kintone への保存はレコード 1 件ごとに確定し、ロールバックしません
- 集計は条件に合うレコードを読んでコネクターが計算します (読む件数に上限があります)
- SQL・ストアドプロシージャ (ExecuteSqlField・QueryField) は使えません
- プロセス管理の操作・コメントは扱えません

ほかの制限は [Docs/Kintone.md](Docs/Kintone.md) の「制限」にあります。

## ライセンス

MIT
