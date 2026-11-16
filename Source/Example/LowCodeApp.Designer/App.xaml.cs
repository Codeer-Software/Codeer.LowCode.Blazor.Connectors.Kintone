using Codeer.LowCode.Blazor.Connectors.Kintone.Designer;
using Codeer.LowCode.Blazor.Designer;
using Codeer.LowCode.Blazor.Extras.Designer;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace LowCodeApp.Designer;

public partial class App : DesignerApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        //DB への接続 (kintone のデータソースは DbAccessorFactory で包む)
        Services.AddSingleton<IDbAccessorFactory, DbAccessorFactory>();

        //Extras のフィールド (Example/Design のユーザーモジュールのログイン設定)
        ExtrasDesignerInitializer.Initialize(BlazorRuntime);

        //kintone: AI 用の仕様ドキュメントの登録と、メタ情報をファイルから読む設定。base.OnStartup より前に呼ぶ
        KintoneDesignerInitializer.Initialize(DesignerEnvironment);

        base.OnStartup(e);

        //kintone: メニュー Tools > kintone のメタ情報を更新。base.OnStartup の後に呼ぶ
        KintoneDesignerInitializer.Setup(DesignerEnvironment);
    }
}
