using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// kintone をデータソースとして見せる IDbAccessor。kintone 用のデータソースへの読み書きは kintone の REST API へ、
    /// それ以外のデータソースは包んだ IDbAccessor へそのまま渡す。
    /// kintone への書き込みはトランザクションに入らない (1 件ごとに確定し、ロールバックで戻らない)。
    /// </summary>
    public class KintoneDbAccessor : IDbAccessor
    {
        readonly IDbAccessor _inner;
        readonly Dictionary<string, KintoneConnection> _connections;
        readonly KintoneMetadataFile? _metadataFile;

        /// <param name="inner">kintone 以外のデータソースを受け持つ IDbAccessor。</param>
        /// <param name="connections">kintone への接続 (<see cref="KintoneConnection.FromDataSources"/>)。</param>
        /// <param name="metadataFile">
        /// アプリの定義の出所。指定するとアプリの定義をこのファイルから読み、kintone に取りに行かない (ファイルに無いアプリ (表) は無い)。
        /// デザイナは <see cref="KintoneMetadataFile.FromProject"/>、Web アプリは <see cref="KintoneMetadataFile.FromDeployedDesign"/>。
        /// null ならアプリごとに最初の 1 回 kintone のフォームの設定 (app/form/fields.json) を読む。
        /// </param>
        public KintoneDbAccessor(IDbAccessor inner, IEnumerable<KintoneConnection> connections, KintoneMetadataFile? metadataFile = null)
        {
            _inner = inner;
            _connections = connections.ToDictionary(e => e.Settings.Name);
            _metadataFile = metadataFile;
        }

        KintoneConnection? Find(string dataSourceName)
            => _connections.TryGetValue(dataSourceName, out var connection) ? connection : null;

        //アプリの定義。ファイルから読むときは、接続文字列の Apps にあるアプリだけ (アプリ ID は Apps のものを使う。ファイルにアプリ ID は無い)
        Task<KintoneTableDefinition> GetDefinitionAsync(KintoneConnection connection, string table)
        {
            if (_metadataFile == null) return connection.GetTableDefinitionAsync(table);
            if (!connection.Settings.Apps.Any(e => e.Name == table))
                throw new KintoneException($"kintone data source '{connection.Settings.Name}': table '{table}' is not in Apps of the connection string.", KintoneErrorKind.NotFound);
            var definition = _metadataFile.Load(connection.Settings.Name).FirstOrDefault(e => e.Name == table) ?? throw _metadataFile.TableNotFound(connection.Settings.Name, table);
            return Task.FromResult(definition);
        }

        Func<string, Task<KintoneTableDefinition>> DefinitionResolver(KintoneConnection connection) => table => GetDefinitionAsync(connection, table);

        public DataSource? GetDataSource(string dataSource)
        {
            if (Find(dataSource) == null) return _inner.GetDataSource(dataSource);
            //本体には PostgreSQL として見せる (識別子を "..." で囲み、limit / offset でページングする方言)。接続の設定は返さない
            return new DataSource { Name = dataSource, DataSourceType = DataSourceType.PostgreSQL };
        }

        public void StartTransaction() => _inner.StartTransaction();
        public Task CommitAsync() => _inner.CommitAsync();
        public Task RollbackAsync() => _inner.RollbackAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public DbTableDefinitionCache? DbTableDefinitionCache => _inner.DbTableDefinitionCache;

        public int CommandTimeoutSeconds
        {
            get => _inner.CommandTimeoutSeconds;
            set => _inner.CommandTimeoutSeconds = value;
        }

        public DbConnection GetConnection(string dataSourceName)
        {
            if (Find(dataSourceName) == null) return _inner.GetConnection(dataSourceName);
            throw new KintoneException("A kintone data source has no database connection (SQL cannot be executed on it directly).");
        }

        public IDbTransaction? GetTransaction(string dataSourceName)
            => Find(dataSourceName) == null ? _inner.GetTransaction(dataSourceName) : null;

        public object? ConvertFieldValueToDbValue(string dataSourceName, string rawDbTypeName, object? value)
            => Find(dataSourceName) == null ? _inner.ConvertFieldValueToDbValue(dataSourceName, rawDbTypeName, value) : value;

        public object? ConvertDbValueToFieldValue(string dataSourceName, object dbValue, Type fieldType)
            => Find(dataSourceName) == null ? _inner.ConvertDbValueToFieldValue(dataSourceName, dbValue, fieldType) : null;

        /// <summary>kintone のデータソースでは、SQL・ストアドプロシージャは実行できない。</summary>
        public async Task<DbSqlCommandResult> ExecuteSqlCommandAsync(string dataSourceName, DbSqlCommand command)
        {
            if (Find(dataSourceName) == null) return await _inner.ExecuteSqlCommandAsync(dataSourceName, command);
            throw new KintoneException("SQL and stored procedures cannot be executed on a kintone data source.");
        }

        // ---------------------------------------------------------------- 定義

        public async Task<List<DbTableDefinition>?> GetCustomTableDefinitionsAsync(string dataSourceName)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName);
            //ファイルから読むときは「ファイルにあって Apps にもあるアプリ」
            if (_metadataFile != null)
                return _metadataFile.Load(connection.Settings.Name).Where(e => connection.Settings.Apps.Any(a => a.Name == e.Name)).Select(e => e.ToTableDefinition()).ToList();
            return (await connection.GetTableDefinitionsAsync()).Select(e => e.ToTableDefinition()).ToList();
        }

        public async Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableNames)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName, tableNames);
            return (await GetDefinitionAsync(connection, tableNames)).ToTableDefinition();
        }

        // ---------------------------------------------------------------- 読み出し

        public async Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.QueryAsync(dataSourceName, query, args);
            return await QueryCoreAsync(connection, query, args.ToDictionary(e => e.Key, e => e.Value.Value));
        }

        async Task<List<IDictionary<string, object>>> QueryCoreAsync(KintoneConnection connection, string query, Dictionary<string, object?> args)
        {
            if (KintoneQueryTranslator.IsAggregate(query))
                return await KintoneQueryExecutor.AggregateAsync(connection, await KintoneQueryTranslator.TranslateAggregateAsync(query, args, DefinitionResolver(connection)));
            return await KintoneQueryExecutor.ReadAsync(connection, await KintoneQueryTranslator.TranslateSelectAsync(query, args, DefinitionResolver(connection)));
        }

        static KintoneException TooManyRows(int maxRows)
            => new($"The query returned more than {maxRows} rows (MaxRows of the kintone data source). Narrow the condition or use paging.", KintoneErrorKind.InvalidRequest);

        static KintoneException OffsetTooLarge()
            => new($"kintone cannot skip more than {KintoneConnection.MaxOffset} records (offset). Narrow the condition.", KintoneErrorKind.InvalidRequest);

        /// <summary>
        /// 条件に合うレコードを読む (1 回の要求は 500 件まで)。
        /// limit あり = offset で読み進める (offset は kintone の上限 10000 まで。超えたらエラー)。
        /// limit なし・並びなし = レコード ID の順に「前の最後の ID より大きい」で読み進める (件数の上限なし)。
        /// limit なし・並びあり = offset で読み進める。limit なしでは maxRows を超えたらエラー (黙って切り捨てない)。
        /// </summary>
        internal static async Task<List<JsonElement>> ReadRowsAsync(KintoneConnection connection, string table, IEnumerable<string> columns, string? filter,
            IReadOnlyCollection<string> orderBy, int? limit, int offset, int? maxRows)
        {
            var fields = columns.Distinct().ToList();
            if (fields.Count != 0 && !fields.Contains(KintoneTableDefinition.IdColumn)) fields.Add(KintoneTableDefinition.IdColumn);
            var rows = new List<JsonElement>();

            if (limit == null && orderBy.Count == 0)
            {
                var order = new[] { KintoneTableDefinition.IdColumn + " asc" };
                string? last = null;
                while (true)
                {
                    var seek = last == null ? filter
                        : string.IsNullOrEmpty(filter) ? $"{KintoneTableDefinition.IdColumn} > {last}"
                        : $"({filter}) and {KintoneTableDefinition.IdColumn} > {last}";
                    var page = await connection.ReadRecordsAsync(table, fields, KintoneQueryTranslator.BuildQuery(seek, order, KintoneConnection.PageSize, 0), false);
                    rows.AddRange(page.Records);
                    if (maxRows != null && maxRows < rows.Count) throw TooManyRows(maxRows.Value);
                    if (page.Records.Count < KintoneConnection.PageSize) return rows;
                    last = GetRecordId(table, page.Records[^1]);
                }
            }

            var sort = orderBy.ToList();
            if (limit == null && !sort.Any(e => e.StartsWith(KintoneTableDefinition.IdColumn + " ", StringComparison.Ordinal))) sort.Add(KintoneTableDefinition.IdColumn + " asc");
            while (limit == null || rows.Count < limit)
            {
                var size = limit == null ? KintoneConnection.PageSize : Math.Min(KintoneConnection.PageSize, limit.Value - rows.Count);
                var skip = offset + rows.Count;
                if (KintoneConnection.MaxOffset < skip) throw OffsetTooLarge();
                var page = await connection.ReadRecordsAsync(table, fields, KintoneQueryTranslator.BuildQuery(filter, sort, size, skip), false);
                rows.AddRange(page.Records);
                if (limit == null && maxRows != null && maxRows < rows.Count) throw TooManyRows(maxRows.Value);
                if (page.Records.Count < size) break;
            }
            return rows;
        }

        //レコード ID (数字だけ)。読めなければエラー (続きを読めない)
        static string GetRecordId(string table, JsonElement record)
        {
            if (record.TryGetProperty(KintoneTableDefinition.IdColumn, out var field) && field.ValueKind == JsonValueKind.Object &&
                field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && IsRecordId(value.GetString()!))
                return value.GetString()!;
            throw new KintoneException($"kintone did not return the record id of a record of app '{table}'.", KintoneErrorKind.Unknown);
        }

        static bool IsRecordId(string text) => text.Length != 0 && text.Length <= 18 && text.All(e => e is >= '0' and <= '9');

        //結合先の行: 結合元の値を集めて 100 個ずつ読み、値で突き合わせる (同じ値の行が複数あれば最初の 1 行)。
        //読む行は合わせて MaxRows まで (超えたらエラー。結合先の列が一意でない結合で、ほかの読み出しと同じく上限なしに読まない)
        internal static async Task<List<Dictionary<string, object?>?>> ReadJoinAsync(KintoneConnection connection, KintoneJoin join, List<Dictionary<string, object?>?> sourceRows)
        {
            var maxRows = connection.Settings.MaxRows;
            var read = 0;
            var destination = join.Definition.FindColumn(join.DestinationColumn)!;
            var keys = new List<KeyLiteral>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in sourceRows)
            {
                if (row == null || !row.TryGetValue(join.SourceColumn, out var value) || value == null) continue;
                var key = JoinKey(value);
                if (!seen.Add(key)) continue;
                var literal = KintoneQueryTranslator.LiteralFor(destination, value, true);
                if (literal != null) keys.Add(new KeyLiteral(key, literal));
            }

            var found = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            for (var start = 0; start < keys.Count; start += KintoneQueryTranslator.JoinBatchSize)
            {
                var batch = keys.Skip(start).Take(KintoneQueryTranslator.JoinBatchSize).ToList();
                var filter = KintoneQueryTranslator.Render(new ValuesNode(destination, batch), true);
                var elements = await ReadRowsAsync(connection, join.Table, join.SelectColumns, filter, Array.Empty<string>(), null, 0, maxRows);
                read += elements.Count;
                if (maxRows < read) throw TooManyRows(maxRows);
                foreach (var element in elements)
                {
                    var values = ToValues(join.Definition, element);
                    if (!values.TryGetValue(join.DestinationColumn, out var value) || value == null) continue;
                    found.TryAdd(KintoneQueryTranslator.KeyText(value), values);
                }
            }

            return sourceRows.Select(row =>
            {
                if (row == null || !row.TryGetValue(join.SourceColumn, out var value) || value == null) return null;
                return found.TryGetValue(JoinKey(value), out var joined) ? joined : null;
            }).ToList();

            //突き合わせの文字は結合先の列の値と同じ書き方にする (日付の列に当てる 0:00 の日時は日付。時刻の入った日時はどの日付とも一致しないので日時のまま)
            string JoinKey(object value)
                => KintoneQueryTranslator.KeyText(destination.Kind == KintoneColumnKind.Date && value is DateTime { TimeOfDay.Ticks: 0 } dateTime ? DateOnly.FromDateTime(dateTime) : value);
        }

        //レコード → 列名と .NET の値 (定義にある列だけ)
        internal static Dictionary<string, object?> ToValues(KintoneTableDefinition definition, JsonElement record)
            => KintoneValueConverter.ToValues(definition, record);

        // ---------------------------------------------------------------- 書き込み

        public async Task<string> InsertAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.InsertAsync(dataSourceName, query, args);

            var insert = KintoneQueryTranslator.ParseInsert(query);
            if (insert.Rows.Count != 1) throw new KintoneException("This operation is not supported for a kintone data source.");
            return await InsertRowAsync(connection, insert, insert.Rows[0], args);
        }

        public async Task<int> ExecuteAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.ExecuteAsync(dataSourceName, query, args);

            if (KintoneQueryTranslator.IsUpdate(query)) return await UpdateAsync(connection, KintoneQueryTranslator.ParseUpdate(query), args);
            if (KintoneQueryTranslator.IsDelete(query)) return await DeleteAsync(connection, KintoneQueryTranslator.ParseDelete(query), args);
            if (KintoneQueryTranslator.IsInsert(query))
            {
                //一括の追加 (1 件ずつ作る。途中で失敗しても、そこまでに作った行は残る)
                var insert = KintoneQueryTranslator.ParseInsert(query);
                var created = new List<string>();
                foreach (var row in insert.Rows)
                {
                    try
                    {
                        created.Add(await InsertRowAsync(connection, insert, row, args));
                    }
                    catch (KintoneException e) when (1 < insert.Rows.Count)
                    {
                        throw new KintoneException($"Inserting records into '{insert.Table}' failed: {created.Count} of {insert.Rows.Count} records had been created" +
                            (created.Count == 0 ? "." : $" (keys: {string.Join(", ", created)}).") + " " + e.Message, e.Kind, e.StatusCode, e.ErrorCode, e);
                    }
                }
                return insert.Rows.Count;
            }
            throw new KintoneException("This operation is not supported for a kintone data source.");
        }

        static KintoneException NotRecordKey(KintoneTableDefinition definition, string column)
            => new($"A record of kintone app '{definition.Name}' cannot be identified by field '{column}'. Bind the Id field to '{KintoneTableDefinition.IdColumn}' (record id) or to a single-line text / number field that prohibits duplicate values.");

        static KintoneColumnDefinition GetColumn(KintoneTableDefinition definition, string name)
            => definition.FindColumn(name) ?? throw new KintoneException($"kintone app '{definition.Name}' has no field '{name}'.");

        static object? GetArgument(Dictionary<string, object?> args, string parameter)
        {
            if (!args.TryGetValue(parameter, out var value)) throw new KintoneException($"The value of parameter '{parameter}' is missing.");
            return value is DBNull ? null : value;
        }

        static bool IsEmpty(object? value) => value == null || value is string { Length: 0 };

        /// <summary>
        /// 作成。値が null の列は (どの列も) 送らない (新しい行なので送らなければ空で、kintone の初期値・自動入力が潰れない)。
        /// 読み取り専用の列 (計算・ルックアップのコピー先・レコード ID など) は値があればエラー。版 ($revision) は kintone が付けるので送らない。
        /// 戻り値は Id の列の値: レコード ID ($id) なら作成されたレコードの ID、それ以外 (重複禁止のフィールド) なら送った値。
        /// </summary>
        async Task<string> InsertRowAsync(KintoneConnection connection, KintoneInsert insert, List<string> parameters, Dictionary<string, object?> args)
        {
            var definition = await GetDefinitionAsync(connection, insert.Table);
            var returning = string.IsNullOrEmpty(insert.ReturningColumn) ? KintoneTableDefinition.IdColumn : insert.ReturningColumn;
            if (returning != KintoneTableDefinition.IdColumn && !GetColumn(definition, returning).CanBeRecordKey) throw NotRecordKey(definition, returning);

            var record = new JsonObject();
            var attachments = new Dictionary<string, KintoneAttachments.Change>(StringComparer.Ordinal);
            object? returningValue = null;
            foreach (var (name, parameter) in insert.Columns.Zip(parameters))
            {
                var column = GetColumn(definition, name);
                var value = GetArgument(args, parameter);
                if (name == KintoneTableDefinition.RevisionColumn) continue;
                if (column.IsAttachments)
                {
                    attachments[name] = KintoneAttachments.ParseChange(definition.Name, name, value);
                    continue;
                }
                if (value == null) continue;
                if (column.IsReadOnly)
                    throw new KintoneException($"Field '{name}' of kintone app '{definition.Name}' is read-only and cannot be written.");
                if (name == returning) returningValue = value;
                record[name] = KintoneValueConverter.ToRecordField(definition.Name, column, value);
            }
            if (returning != KintoneTableDefinition.IdColumn && IsEmpty(returningValue))
                throw new KintoneException($"The value of field '{returning}' (the Id of the record) is required to create a record of kintone app '{definition.Name}'.");

            //添付: 作成では足すファイルだけ (消す指定は見ない)。アップロードして得た fileKey を並べる
            foreach (var (name, change) in attachments)
            {
                if (change.Additions.Count == 0) continue;
                record[name] = new JsonObject { ["value"] = KintoneAttachments.ToValue(await UploadAsync(connection, change)) };
            }

            var created = await connection.CreateAsync(insert.Table, record);
            return returning == KintoneTableDefinition.IdColumn ? created.Id : KintoneQueryTranslator.KeyText(returningValue!);
        }

        //書き込みの対象 (キーの列とその値)。キーの値が無ければエラー
        static (KintoneTableDefinition Definition, KintoneColumnDefinition Key, object Value) GetTarget(KintoneTableDefinition definition, KintoneWrite write, Dictionary<string, object?> args)
        {
            var key = GetColumn(definition, write.KeyColumn);
            if (write.KeyColumn != KintoneTableDefinition.IdColumn && !key.CanBeRecordKey) throw NotRecordKey(definition, write.KeyColumn);
            var value = GetArgument(args, write.KeyParameter);
            if (IsEmpty(value)) throw new KintoneException($"The key of the kintone record to write is missing (app '{definition.Name}').");
            return (definition, key, value!);
        }

        //版の条件。条件なしは (true, null)。版が空であること・空の版との一致が条件なら、合う行は無い (false)
        static (bool CanMatch, string? Revision) GetRevision(KintoneWrite write, Dictionary<string, object?> args)
        {
            if (write.RevisionIsNull) return (false, null);
            if (write.RevisionParameter == null) return (true, null);
            var value = GetArgument(args, write.RevisionParameter);
            if (IsEmpty(value)) return (false, null);
            var text = KintoneQueryTranslator.KeyText(value!);
            return IsRecordId(text) ? (true, text) : (false, null);
        }

        //キーと版の一致のほかに条件 (論理削除など) が付いた書き込みは、条件に合う行があるかを先に数えて確かめてから、キー指定で書く。
        //確かめてから書くまでの間に別の更新が入ることは防げない (版の条件があれば kintone が版で弾く)
        async Task<bool> ExistsAsync(KintoneConnection connection, KintoneWrite write, Dictionary<string, object?> args)
        {
            if (!write.HasOtherConditions) return true;
            var rows = await QueryCoreAsync(connection, $"select count(*) from \"{write.Table}\" where {write.WhereSql}", args);
            var count = rows.FirstOrDefault()?.Values.FirstOrDefault();
            return count != null && 0 < Convert.ToDecimal(count, CultureInfo.InvariantCulture);
        }

        //ExistsAsync と同じ条件 (キー・論理削除など。版は除く) でレコードを読み、添付の列に今ある fileKey と版を返す。1 件でなければ null
        async Task<(string Revision, Dictionary<string, List<string>> FileKeys)?> ReadStoredAttachmentsAsync(KintoneConnection connection, KintoneWrite write,
            Dictionary<string, object?> args, List<string> columns)
        {
            var select = string.Join(", ", new[] { KintoneTableDefinition.RevisionColumn }.Concat(columns).Select(e => $"\"{e}\""));
            var rows = await QueryCoreAsync(connection, $"select {select} from \"{write.Table}\" where {write.WhereSql}", args);
            if (rows.Count != 1) return null;
            var row = rows[0];
            var revision = row.TryGetValue(KintoneTableDefinition.RevisionColumn, out var r) && r != null ? KintoneQueryTranslator.KeyText(r) : string.Empty;
            if (!IsRecordId(revision)) return null;
            return (revision, columns.ToDictionary(e => e, e => KintoneAttachments.ReadFileKeys(row.TryGetValue(e, out var value) ? value as string : null)));
        }

        //足すファイルを 1 つずつアップロードして fileKey を並べる。アップロードの後で更新が失敗しても、添付されなかったファイルは kintone が 3 日で消す
        static async Task<List<string>> UploadAsync(KintoneConnection connection, KintoneAttachments.Change change)
        {
            var keys = new List<string>();
            foreach (var addition in change.Additions) keys.Add(await connection.UploadFileAsync(addition.Name, addition.ContentType, addition.Content));
            return keys;
        }

        //キーの値からレコード ID を引く。無ければ null
        static async Task<string?> FindRecordIdAsync(KintoneConnection connection, KintoneTableDefinition definition, KintoneColumnDefinition key, object value)
        {
            if (key.Name == KintoneTableDefinition.IdColumn)
            {
                var id = KintoneQueryTranslator.KeyText(value);
                return IsRecordId(id) ? id : null;
            }
            var literal = KintoneQueryTranslator.LiteralFor(key, value);
            if (literal == null) return null;
            var filter = KintoneQueryTranslator.Render(new ValuesNode(key, new() { new KeyLiteral(KintoneQueryTranslator.KeyText(value), literal) }), true);
            //キーの列も読み、1 件だけで値がキーと同じときだけ使う (kintone の一致が思ったより広くても、別のレコードを書かない・消さない)
            var page = await connection.ReadRecordsAsync(definition.Name, new[] { KintoneTableDefinition.IdColumn, key.Name },
                KintoneQueryTranslator.BuildQuery(filter, Array.Empty<string>(), 2, 0), false);
            if (page.Records.Count != 1) return null;
            var record = page.Records[0];
            if (!record.TryGetProperty(key.Name, out var field) || field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("value", out var found)) return null;
            //クエリに書いた値と同じ書き方で比べる。読めない値のレコードは対象にしない
            object? foundValue;
            try
            {
                foundValue = KintoneValueConverter.FromRecordValue(definition.Name, key, found);
            }
            catch (KintoneException)
            {
                return null;
            }
            if (foundValue == null || KintoneQueryTranslator.LiteralFor(key, foundValue) != literal) return null;
            return GetRecordId(definition.Name, record);
        }

        /// <summary>
        /// 更新。対象は、Id の列がレコード ID ($id) ならその ID、重複禁止のフィールドならその値 (updateKey)。
        /// 版の条件 (楽観ロック) は kintone の revision に渡す (版が違えば 0 行)。SET の版 ($revision) は送らない (kintone が 1 つ進める)。
        /// </summary>
        async Task<int> UpdateAsync(KintoneConnection connection, KintoneWrite update, Dictionary<string, object?> args)
        {
            var (definition, key, keyValue) = GetTarget(await GetDefinitionAsync(connection, update.Table), update, args);

            var record = new JsonObject();
            var attachments = new Dictionary<string, KintoneAttachments.Change>(StringComparer.Ordinal);
            var changesKey = false;
            foreach (var (name, parameter) in update.Values)
            {
                var column = GetColumn(definition, name);
                var value = GetArgument(args, parameter);
                if (name == KintoneTableDefinition.RevisionColumn) continue;
                if (column.IsAttachments)
                {
                    attachments[name] = KintoneAttachments.ParseChange(definition.Name, name, value);
                    continue;
                }
                if (name == key.Name)
                {
                    //Id の列を同じ値で書くのは何もしない。別の値に変えるときは、レコード ID で対象を決めて書く
                    if (value != null && KintoneQueryTranslator.KeyText(value) == KintoneQueryTranslator.KeyText(keyValue)) continue;
                    changesKey = true;
                }
                if (column.IsReadOnly)
                    throw new KintoneException($"Field '{name}' of kintone app '{definition.Name}' is read-only and cannot be written.");
                record[name] = KintoneValueConverter.ToRecordField(definition.Name, column, value);
            }

            var (canMatch, revision) = GetRevision(update, args);
            if (!canMatch) return 0;
            if (attachments.Count == 0)
            {
                if (!await ExistsAsync(connection, update, args)) return 0;
            }
            else
            {
                //添付: 存在の確認と同じ条件でレコードの今の fileKey を読み、「今あるもの - 消す指定 (今あるものと一致したものだけ) + 新しいもの」を送る
                var stored = await ReadStoredAttachmentsAsync(connection, update, args, attachments.Keys.ToList());
                if (stored == null) return 0;
                var wrote = false;
                foreach (var (name, change) in attachments)
                {
                    var keys = stored.Value.FileKeys[name];
                    var deletions = change.Deletions.Where(e => keys.Contains(e, StringComparer.Ordinal)).ToList();
                    if (change.Additions.Count == 0 && deletions.Count == 0) continue;
                    var uploaded = change.Additions.Count == 0 ? new List<string>() : await UploadAsync(connection, change);
                    var value = KintoneAttachments.ToUpdateValue(keys, deletions, uploaded);
                    if (value == null) continue;
                    record[name] = new JsonObject { ["value"] = value };
                    wrote = true;
                }
                //書くものが無ければ送らない (読んだ値をそのまま書いた・消す指定がどれもこのレコードのものでない)
                if (record.Count == 0) return 1;
                //読んだ版で書く (読んでから書くまでに別の更新が入ったら kintone が弾く = 間に足された添付を消さない)
                if (wrote && revision == null) revision = stored.Value.Revision;
            }

            if (key.Name == KintoneTableDefinition.IdColumn || changesKey)
            {
                var id = await FindRecordIdAsync(connection, definition, key, keyValue);
                if (id == null) return 0;
                return await connection.UpdateAsync(update.Table, id, null, revision, record) ? 1 : 0;
            }
            var keyNode = KintoneValueConverter.ToRecordField(definition.Name, key, keyValue)["value"]?.DeepClone();
            return await connection.UpdateAsync(update.Table, null, (key.Name, keyNode), revision, record) ? 1 : 0;
        }

        /// <summary>削除。Id の列がレコード ID でなければ、先にレコード ID を引く。版の条件は kintone の revision に渡す。</summary>
        async Task<int> DeleteAsync(KintoneConnection connection, KintoneWrite delete, Dictionary<string, object?> args)
        {
            var (definition, key, keyValue) = GetTarget(await GetDefinitionAsync(connection, delete.Table), delete, args);
            var (canMatch, revision) = GetRevision(delete, args);
            if (!canMatch) return 0;
            if (!await ExistsAsync(connection, delete, args)) return 0;
            var id = await FindRecordIdAsync(connection, definition, key, keyValue);
            if (id == null) return 0;
            return await connection.DeleteAsync(delete.Table, id, revision) ? 1 : 0;
        }

        // ---------------------------------------------------------------- 添付ファイルのダウンロード

        /// <summary>添付のフィールド (クライアント用パッケージの KintoneAttachmentsFieldDesign) の型の名前。このパッケージはクライアント用パッケージを参照しないので名前で見る。</summary>
        internal const string AttachmentsFieldDesignTypeFullName = "Codeer.LowCode.Blazor.Connectors.Kintone.KintoneAttachmentsFieldDesign";

        /// <summary>
        /// 添付のフィールドのファイル 1 つの中身を、現在の利用者の権限で読む (ホストのダウンロードの Controller から呼ぶ)。
        /// 手順: モジュールがこのアクセサの kintone のデータソースで、フィールドが添付のフィールド (表の添付ファイルの列) で、Id がレコードを決められる列であることを確かめる →
        /// io の通常の読み出し (モジュール・行・フィールドの権限・論理削除が効く) で Id が一致するレコードのそのフィールドだけを読む →
        /// その値に key の fileKey があることを確かめる → レコードの値にあった fileKey で kintone から中身を読む。
        /// どれかが満たせなければ null (理由は区別しない。失敗でも例外を投げない)。
        /// 連携用の認証 (API トークン) はアプリのどの添付も読めるので、fileKey だけで中身を返す口をホストに作らない。
        /// </summary>
        /// <param name="io">現在の利用者の ModuleDataIO。</param>
        /// <param name="designData">io と同じデザイン。</param>
        /// <param name="moduleName">モジュール名。</param>
        /// <param name="id">レコードの Id (モジュールの Id のフィールドの値)。</param>
        /// <param name="fieldName">添付のフィールドの名前。</param>
        /// <param name="key">ファイルの fileKey (レコードを読んだときの値)。</param>
        public async Task<KintoneAttachmentFile?> GetAttachmentAsync(ModuleDataIO io, DesignData designData, string? moduleName, string? id, string? fieldName, string? key)
        {
            try
            {
                if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(id) || !KintoneAttachments.IsFileKey(key)) return null;

                var module = designData.Modules.Find(moduleName);
                if (module == null || string.IsNullOrEmpty(module.DataSourceName)) return null;
                var connection = Find(module.DataSourceName);
                if (connection == null) return null;
                var field = module.Fields.FirstOrDefault(e => e.Name == fieldName);
                if (field?.TypeFullName != AttachmentsFieldDesignTypeFullName) return null;
                //型は名前で見たので、列の名前は公開のプロパティから読む
                if (field.GetType().GetProperty("DbColumn")?.GetValue(field) is not string dbColumn) return null;
                var definition = await GetDefinitionAsync(connection, module.DbTable);
                if (definition.FindColumn(dbColumn) is not { IsAttachments: true }) return null;
                var idField = module.Fields.OfType<IdFieldDesign>().FirstOrDefault();
                if (idField == null || definition.FindColumn(idField.DbColumn) is not { } idColumn) return null;
                if (idColumn.Name != KintoneTableDefinition.IdColumn && !idColumn.CanBeRecordKey) return null;

                var condition = new SearchCondition(moduleName)
                {
                    Condition = MultiMatchCondition.And(new FieldValueMatchCondition
                    {
                        SearchTargetVariable = idField.Name + ".Value",
                        Comparison = MatchComparison.Equal,
                        Value = MultiTypeValue.Create(id),
                    }),
                };
                condition.SelectFields.Add(fieldName);
                var rows = (await io.GetListAsync(condition, 0)).Items;
                if (rows.Count != 1 || !rows[0].Fields.TryGetValue(fieldName, out var data) || data is not ValueFieldDataBase<string> value) return null;
                //クライアントの文字は突き合わせだけに使い、kintone へはレコードの値にあった fileKey を送る
                var file = KintoneAttachments.ReadFiles(value.Value).FirstOrDefault(e => string.Equals(e.FileKey, key, StringComparison.Ordinal));
                if (file.FileKey == null) return null;

                var content = await connection.DownloadFileAsync(file.FileKey);
                return new KintoneAttachmentFile(string.IsNullOrEmpty(file.Name) ? "download" : file.Name, content);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
