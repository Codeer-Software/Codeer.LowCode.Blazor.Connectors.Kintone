using Codeer.LowCode.Blazor.DesignLogic;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// アプリの定義 (メタ情報) を保存したファイル (kintone.&lt;データソース名&gt;.metadata.json)。デザインのプロジェクトのサーバー専用ファイルに置き、
    /// デザインと一緒に配られる (App.zip に入る)。デザイナも Web アプリもこのファイルからアプリの定義を読む (kintone に取りに行かない)。
    /// 接続の情報 (URL・アプリ ID・トークン) とレコードは入らない (アプリ ID は実行時の接続文字列の Apps のものを使う)。
    /// 項目名・選択肢も入れる (画面を作るときの手がかり)。
    /// </summary>
    public sealed class KintoneMetadataFile
    {
        class TableData
        {
            public string Name { get; set; } = string.Empty;
            public List<ColumnData> Columns { get; set; } = new();
        }

        class ColumnData
        {
            /// <summary>フィールドコード。</summary>
            public string Name { get; set; } = string.Empty;
            public KintoneColumnKind Kind { get; set; }
            /// <summary>kintone のフィールドの種類。</summary>
            public string FieldType { get; set; } = string.Empty;
            /// <summary>kintone の画面の項目名。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Label { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool ReadOnly { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool Required { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool Unique { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool Lookup { get; set; }
            /// <summary>選択肢 (画面の順)。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<string>? Options { get; set; }
            /// <summary>テーブルの中のフィールド。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<ColumnData>? Fields { get; set; }
        }

        class FileData
        {
            public List<TableData> Tables { get; set; } = new();
        }

        static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };

        static readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new(StringComparer.OrdinalIgnoreCase);

        //読んだ定義 (場所とデータソースごと)。中身 (プロジェクト) か App.zip の更新日時と大きさ (配られたデザイン) が変わるまで使い回す。
        //値は差し替えるだけで中を変えないので、読む側はロックなしで使える。同じ場所を同時に読み直すのは 1 つだけ (Entry のロック)
        sealed class Entry
        {
            public object Sync { get; } = new();
            public (object Stamp, List<KintoneTableDefinition> Tables)? Loaded { get; set; }
        }

        static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

        readonly string _directory;
        readonly bool _isDeployedDesign;

        KintoneMetadataFile(string directory, bool isDeployedDesign)
        {
            //空は今のフォルダ (本体が App.zip を探す場所と同じ)
            _directory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
            _isDeployedDesign = isDeployedDesign;
        }

        /// <summary>デザインのプロジェクトのファイル (デザイナ用)。</summary>
        /// <param name="projectDirectory">デザインのプロジェクトのフォルダ。</param>
        public static KintoneMetadataFile FromProject(string projectDirectory) => new(projectDirectory, false);

        /// <summary>配られたデザイン (App.zip) の中のファイル (Web アプリ用)。App.zip が差し替わると次の読み出しから新しい内容になる。</summary>
        /// <param name="designFileDirectory">App.zip のあるフォルダ。</param>
        public static KintoneMetadataFile FromDeployedDesign(string designFileDirectory) => new(designFileDirectory, true);

        /// <summary>
        /// ファイル名 (サーバー専用ファイルの置き場からの相対パス)。データソース名の文字 (日本語なども含む)・数字・_・-・. 以外は _ にする
        /// (OS に依らない決まり。Windows のデザイナと Linux の Web アプリで同じ名前になる)。
        /// </summary>
        internal static string GetFileName(string dataSourceName)
            => $"kintone.{string.Concat(dataSourceName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_'))}.metadata.json";

        /// <summary>
        /// 接続の Apps の全部のアプリのフォームの設定を読み直して、プロジェクトのサーバー専用ファイルに保存する (次のデプロイでデザインと一緒に配られる)。
        /// 読めなければファイルを変えない。書く内容を読み直して確かめてから、書き終えてから置き換える (途中で失敗しても前のファイルが残る)。同じファイルの更新は直列にする。
        /// </summary>
        /// <param name="connection">kintone への接続。</param>
        /// <param name="projectDirectory">デザインのプロジェクトのフォルダ。</param>
        public static async Task UpdateAsync(KintoneConnection connection, string projectDirectory)
        {
            var fileName = GetFileName(connection.Settings.Name);
            var updateLock = _updateLocks.GetOrAdd(Path.Combine(Path.GetFullPath(projectDirectory), fileName), _ => new SemaphoreSlim(1, 1));
            await updateLock.WaitAsync();
            try
            {
                var tables = await connection.ReloadTableDefinitionsAsync();
                var bytes = Serialize(tables);
                //書く内容を読み直して、同じ定義になることを確かめてから書く
                var written = Parse(bytes, fileName);
                if (written.Count != tables.Count || written.Sum(e => e.Columns.Count) != tables.Sum(e => e.Columns.Count))
                    throw new KintoneException($"The kintone metadata file '{fileName}' could not be written correctly.");
                DesignDataFileManager.WriteProjectServerFile(projectDirectory, fileName, bytes);
            }
            finally
            {
                updateLock.Release();
            }
        }

        static ColumnData ToData(KintoneColumnDefinition column) => new()
        {
            Name = column.Name,
            Kind = column.Kind,
            FieldType = column.FieldType,
            Label = string.IsNullOrEmpty(column.Label) ? null : column.Label,
            ReadOnly = column.IsReadOnly,
            Required = column.IsRequired,
            Unique = column.IsUnique,
            Lookup = column.IsLookup,
            Options = column.Options,
            Fields = column.SubFields?.Select(ToData).ToList(),
        };

        static KintoneColumnDefinition ToDefinition(ColumnData column) => new()
        {
            Name = column.Name,
            Kind = column.Kind,
            FieldType = column.FieldType,
            Label = column.Label ?? string.Empty,
            IsReadOnly = column.ReadOnly,
            IsRequired = column.Required,
            IsUnique = column.Unique,
            IsLookup = column.Lookup,
            Options = column.Options,
            SubFields = column.Fields?.Where(e => e != null && KintoneTableDefinition.IsUsableColumnName(e.Name)).Select(ToDefinition).ToList(),
        };

        static byte[] Serialize(List<KintoneTableDefinition> tables)
        {
            var data = new FileData
            {
                //表の順は設定 (Apps) の順のまま
                Tables = tables.Select(table => new TableData { Name = table.Name, Columns = table.Columns.Select(ToData).ToList() }).ToList(),
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, _jsonOptions));
        }

        /// <summary>このファイルのアプリの定義。ファイルが無ければ空。</summary>
        internal List<KintoneTableDefinition> Load(string dataSourceName)
        {
            var fileName = GetFileName(dataSourceName);
            var entry = _cache.GetOrAdd((_isDeployedDesign ? "zip|" : "project|") + _directory + "|" + fileName, _ => new Entry());
            lock (entry.Sync)
            {
                if (_isDeployedDesign)
                {
                    //App.zip の更新日時と大きさで差し替えを知る (読む前に取る。読む間に差し替わっても、次の読み出しで日時が違うので読み直す)
                    var stamp = ZipStamp();
                    if (entry.Loaded is { } loaded && loaded.Stamp.Equals(stamp)) return loaded.Tables;
                    using var stream = DesignDataFileManager.GetServerFile(_directory, fileName);
                    var tables = stream == null ? new List<KintoneTableDefinition>() : Parse(stream.ToArray(), fileName);
                    entry.Loaded = (stamp, tables);
                    return tables;
                }
                else
                {
                    //プロジェクトのファイルは中身で比べる (小さいファイルなので読むのは毎回。解釈は変わったときだけ)
                    var bytes = DesignDataFileManager.ReadProjectServerFile(_directory, fileName);
                    var content = bytes == null ? string.Empty : Convert.ToBase64String(bytes);
                    if (entry.Loaded is { } loaded && loaded.Stamp.Equals(content)) return loaded.Tables;
                    var tables = bytes == null ? new List<KintoneTableDefinition>() : Parse(bytes, fileName);
                    entry.Loaded = (content, tables);
                    return tables;
                }
            }
        }

        //App.zip の名前は本体の配置の決まり (DesignDataFileManager が読む <フォルダ>/App.zip)
        (bool Exists, DateTime LastWriteTimeUtc, long Length) ZipStamp()
        {
            var zip = new FileInfo(Path.Combine(_directory, "App.zip"));
            return zip.Exists ? (true, zip.LastWriteTimeUtc, zip.Length) : (false, default, 0);
        }

        /// <summary>アプリ (表) がファイルに無いときのエラー (どこを直せばよいかを入れる)。</summary>
        internal KintoneException TableNotFound(string dataSourceName, string table)
            => new(_isDeployedDesign
                ? $"Table '{table}' is not in the kintone metadata ({GetFileName(dataSourceName)}) of the deployed design. Update the kintone metadata in the designer and deploy the design again."
                : $"Table '{table}' is not in the kintone metadata ({GetFileName(dataSourceName)}) of the design project. Update the kintone metadata (Tools menu of the designer).",
                KintoneErrorKind.NotFound);

        static List<KintoneTableDefinition> Parse(byte[] bytes, string fileName)
        {
            FileData? data;
            try
            {
                data = JsonSerializer.Deserialize<FileData>(bytes, _jsonOptions);
            }
            catch (JsonException e)
            {
                throw new KintoneException($"The kintone metadata file '{fileName}' cannot be read. Update the kintone metadata again.", KintoneErrorKind.NotSupported, e);
            }
            var tables = new List<KintoneTableDefinition>();
            foreach (var table in data?.Tables ?? new())
            {
                //手で直したファイルの null (要素・Columns) は無いものとして読む。同じ名前の表が 2 つあれば先のもの
                if (table == null || !KintoneTableDefinition.IsUsableTableName(table.Name)) continue;
                if (tables.Any(e => string.Equals(e.Name, table.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var definition = new KintoneTableDefinition { Name = table.Name };
                foreach (var column in (table.Columns ?? new()).Where(e => e != null && KintoneTableDefinition.IsUsableColumnName(e.Name)))
                {
                    if (definition.FindColumn(column.Name) == null) definition.Columns.Add(ToDefinition(column));
                }
                tables.Add(definition);
            }
            return tables;
        }
    }
}
