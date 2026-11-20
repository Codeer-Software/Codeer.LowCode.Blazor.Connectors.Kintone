using Codeer.LowCode.Blazor.Connectors.Kintone.Server;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.Designer.Views.Windows;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Globalization;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Designer
{
    public static class KintoneDesignerInitializer
    {
        const string Prefix = "Codeer.LowCode.Blazor.Connectors.Kintone.Designer.SpecDocs.";

        static DesignerEnvironment? _environment;

        static bool IsJapanese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

        /// <summary>
        /// kintone のモジュールを設計するための AI 用の仕様ドキュメントを登録する (ai-refresh が _specs/Kintone.md に書き出す)。
        /// デザイナのアプリの OnStartup で、base.OnStartup(e) より前に呼ぶ。
        /// これを呼ぶと、<see cref="CreateDbAccessor"/> の IDbAccessor は kintone のアプリの定義を、プロジェクトに保存したメタ情報のファイル (サーバー専用ファイル) から読む
        /// (kintone に取りに行くのは Tools のメニューで更新したときだけ。headless の CLI でも同じ)。
        /// </summary>
        public static void Initialize(DesignerEnvironment designerEnvironment)
        {
            _environment = designerEnvironment;
            //添付のフィールドをツールボックスに出す (本体は読み込み済みのアセンブリからフィールドの型を探す)
            KintoneFieldsInitializer.Initialize();
            var assembly = typeof(KintoneDesignerInitializer).Assembly;
            foreach (var name in assembly.GetManifestResourceNames()
                         .Where(e => e.StartsWith(Prefix, StringComparison.Ordinal) && e.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e, StringComparer.Ordinal))
            {
                SpecDocCatalog.RegisterEmbedded(assembly, name);
            }
        }

        /// <summary>
        /// メニュー Tools &gt; kintone のメタ情報を更新 を足す。デザイナのアプリの OnStartup で、base.OnStartup(e) の後に呼ぶ。
        /// </summary>
        public static void Setup(DesignerEnvironment designerEnvironment)
        {
            _environment = designerEnvironment;
            designerEnvironment.AddMainMenu(() => UpdateMetadata(designerEnvironment), "Tools",
                IsJapanese ? "kintone のメタ情報を更新" : "Update kintone Metadata");
        }

        /// <summary>
        /// デザイナ用の IDbAccessor (IDbAccessorFactory の実装で使う)。kintone 以外のデータソースは inner が受け持つ。
        /// kintone のアプリの定義はプロジェクトのメタ情報のファイル (サーバー専用ファイル) から読む。プロジェクトを開いていなければ kintone のフォームの設定を読む。
        /// </summary>
        public static IDbAccessor CreateDbAccessor(IDbAccessor inner, IEnumerable<DataSource> dataSources)
        {
            var directory = GetProjectDirectory();
            return new KintoneDbAccessor(inner, KintoneConnection.FromDataSources(dataSources),
                string.IsNullOrEmpty(directory) ? null : KintoneMetadataFile.FromProject(directory));
        }

        static string? GetProjectDirectory()
        {
            if (_environment == null) return null;
            try
            {
                return _environment.CurrentFileDirectory;
            }
            catch (NullReferenceException)
            {
                //プロジェクトを開いていない
                return null;
            }
        }

        static async void UpdateMetadata(DesignerEnvironment environment)
        {
            try
            {
                var directory = GetProjectDirectory();
                if (string.IsNullOrEmpty(directory)) return;

                var connections = KintoneConnection.FromDataSources(environment.GetDesignerSettings().DataSources);
                if (connections.Count == 0)
                {
                    environment.ShowToast(IsJapanese ? "kintone のデータソースがありません。" : "There is no kintone data source.", false);
                    return;
                }
                foreach (var connection in connections) await KintoneMetadataFile.UpdateAsync(connection, directory);

                environment.RefreshDatabase();
                environment.ShowToast(IsJapanese ? "kintone のメタ情報を更新しました。" : "kintone metadata has been updated.", true);
            }
            catch (Exception e)
            {
                MessageWindow.Show(e.Message, "Error");
            }
        }
    }
}
