using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// 表の定義 (メタ情報) を保存したファイル。デザイナはこのファイルから表の定義を読む (kintone に取りに行かない)。
    /// データソースごとに 1 つ、デザインのプロジェクトのフォルダに置く (kintone.&lt;データソース名&gt;.metadata.json)。
    /// 接続の情報 (URL・アプリ ID・トークン) とレコードは入らない。
    /// </summary>
    public static class KintoneMetadataFile
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

        static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, List<KintoneTableDefinition> Tables)> _cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new(StringComparer.OrdinalIgnoreCase);

        //テストで置き換えの途中の失敗を起こすための差し替え口 (一時ファイルに書いたあと、読み直す前に呼ぶ)
        internal static Action<string>? AfterTemporaryFileWritten { get; set; }

        public static string GetPath(string directory, string dataSourceName)
        {
            var name = string.Concat(dataSourceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(directory, $"kintone.{name}.metadata.json");
        }

        /// <summary>
        /// kintone からアプリのフォームの設定を読み直して、ファイルに保存する。
        /// 同じフォルダの一時ファイルに書いて読み直して確かめてから置き換える (途中で失敗しても前のファイルが残る)。同じデータソースの更新は直列にする。
        /// </summary>
        public static async Task UpdateAsync(KintoneConnection connection, string directory)
        {
            var path = GetPath(directory, connection.Settings.Name);
            var updateLock = _updateLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
            await updateLock.WaitAsync();
            try
            {
                await SaveAsync(path, await connection.ReloadTableDefinitionsAsync());
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
            SubFields = column.Fields?.Where(e => KintoneTableDefinition.IsUsableColumnName(e.Name)).Select(ToDefinition).ToList(),
        };

        static async Task SaveAsync(string path, List<KintoneTableDefinition> tables)
        {
            var data = new FileData
            {
                //表の順は設定 (Apps) の順のまま
                Tables = tables.Select(table => new TableData { Name = table.Name, Columns = table.Columns.Select(ToData).ToList() }).ToList(),
            };
            var text = JsonSerializer.Serialize(data, _jsonOptions);

            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, text);
                AfterTemporaryFileWritten?.Invoke(temporary);
                //書けた内容を読み直して、同じ定義になることを確かめてから置き換える
                var written = Parse(await File.ReadAllTextAsync(temporary), path);
                if (written.Count != tables.Count || written.Sum(e => e.Columns.Count) != tables.Sum(e => e.Columns.Count))
                    throw new KintoneException($"The kintone metadata file '{Path.GetFileName(path)}' could not be written correctly.");
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        /// <summary>保存してある定義。ファイルが無ければ null。</summary>
        internal static List<KintoneTableDefinition>? Load(string directory, string dataSourceName)
        {
            var path = GetPath(directory, dataSourceName);
            if (!File.Exists(path)) return null;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc) return cached.Tables;
            var tables = Parse(File.ReadAllText(path), path);
            _cache[path] = (lastWriteTimeUtc, tables);
            return tables;
        }

        static List<KintoneTableDefinition> Parse(string text, string path)
        {
            FileData? data;
            try
            {
                data = JsonSerializer.Deserialize<FileData>(text, _jsonOptions);
            }
            catch (JsonException e)
            {
                throw new KintoneException($"The kintone metadata file '{Path.GetFileName(path)}' cannot be read. Update the kintone metadata again.", KintoneErrorKind.NotSupported, e);
            }
            var tables = new List<KintoneTableDefinition>();
            foreach (var table in data?.Tables ?? new())
            {
                if (!KintoneTableDefinition.IsUsableTableName(table.Name)) continue;
                var definition = new KintoneTableDefinition { Name = table.Name };
                foreach (var column in table.Columns.Where(e => KintoneTableDefinition.IsUsableColumnName(e.Name)))
                {
                    if (definition.FindColumn(column.Name) == null) definition.Columns.Add(ToDefinition(column));
                }
                tables.Add(definition);
            }
            return tables;
        }
    }
}
