using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Repository.Design;
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
        readonly string? _metadataDirectory;

        /// <param name="inner">kintone 以外のデータソースを受け持つ IDbAccessor。</param>
        /// <param name="connections">kintone への接続 (<see cref="KintoneConnection.FromDataSources"/>)。</param>
        /// <param name="metadataDirectory">
        /// 指定すると、表の定義をこのフォルダに保存したファイル (<see cref="KintoneMetadataFile"/>) から答える (kintone に取りに行かない)。デザイナ用。
        /// </param>
        public KintoneDbAccessor(IDbAccessor inner, IEnumerable<KintoneConnection> connections, string? metadataDirectory = null)
        {
            _inner = inner;
            _connections = connections.ToDictionary(e => e.Settings.Name);
            _metadataDirectory = metadataDirectory;
        }

        KintoneConnection? Find(string dataSourceName)
            => _connections.TryGetValue(dataSourceName, out var connection) ? connection : null;

        //保存してある定義。ファイルから答えない設定なら null
        List<KintoneTableDefinition>? LoadSavedDefinitions(KintoneConnection connection)
            => string.IsNullOrEmpty(_metadataDirectory) ? null : KintoneMetadataFile.Load(_metadataDirectory, connection.Settings.Name) ?? new();

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
            if (LoadSavedDefinitions(connection) is { } saved) return saved.Select(e => e.ToTableDefinition()).ToList();
            return (await connection.GetTableDefinitionsAsync()).Select(e => e.ToTableDefinition()).ToList();
        }

        public async Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableNames)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName, tableNames);
            if (LoadSavedDefinitions(connection) is { } saved)
            {
                var definition = saved.FirstOrDefault(e => e.Name == tableNames);
                if (definition == null)
                    throw new KintoneException($"Table '{tableNames}' is not in the saved kintone metadata of data source '{dataSourceName}'. Update the kintone metadata.");
                return definition.ToTableDefinition();
            }
            return (await connection.GetTableDefinitionAsync(tableNames)).ToTableDefinition();
        }

        // ---------------------------------------------------------------- 読み出し

        public async Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.QueryAsync(dataSourceName, query, args);
            return await QueryCoreAsync(connection, query, args.ToDictionary(e => e.Key, e => e.Value.Value));
        }

        static async Task<List<IDictionary<string, object>>> QueryCoreAsync(KintoneConnection connection, string query, Dictionary<string, object?> args)
        {
            if (KintoneQueryTranslator.IsAggregate(query))
                return await KintoneQueryExecutor.AggregateAsync(connection, await KintoneQueryTranslator.TranslateAggregateAsync(query, args, connection.GetTableDefinitionAsync));
            return await KintoneQueryExecutor.ReadAsync(connection, await KintoneQueryTranslator.TranslateSelectAsync(query, args, connection.GetTableDefinitionAsync));
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
                var key = KintoneQueryTranslator.KeyText(value);
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
                return found.TryGetValue(KintoneQueryTranslator.KeyText(value), out var joined) ? joined : null;
            }).ToList();
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
        static async Task<string> InsertRowAsync(KintoneConnection connection, KintoneInsert insert, List<string> parameters, Dictionary<string, object?> args)
        {
            var definition = await connection.GetTableDefinitionAsync(insert.Table);
            var returning = string.IsNullOrEmpty(insert.ReturningColumn) ? KintoneTableDefinition.IdColumn : insert.ReturningColumn;
            if (returning != KintoneTableDefinition.IdColumn && !GetColumn(definition, returning).CanBeRecordKey) throw NotRecordKey(definition, returning);

            var record = new JsonObject();
            object? returningValue = null;
            foreach (var (name, parameter) in insert.Columns.Zip(parameters))
            {
                var column = GetColumn(definition, name);
                var value = GetArgument(args, parameter);
                if (name == KintoneTableDefinition.RevisionColumn) continue;
                if (value == null) continue;
                if (column.IsReadOnly)
                    throw new KintoneException($"Field '{name}' of kintone app '{definition.Name}' is read-only and cannot be written.");
                if (name == returning) returningValue = value;
                record[name] = KintoneValueConverter.ToRecordField(definition.Name, column, value);
            }
            if (returning != KintoneTableDefinition.IdColumn && IsEmpty(returningValue))
                throw new KintoneException($"The value of field '{returning}' (the Id of the record) is required to create a record of kintone app '{definition.Name}'.");

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
        static async Task<bool> ExistsAsync(KintoneConnection connection, KintoneWrite write, Dictionary<string, object?> args)
        {
            if (!write.HasOtherConditions) return true;
            var rows = await QueryCoreAsync(connection, $"select count(*) from \"{write.Table}\" where {write.WhereSql}", args);
            var count = rows.FirstOrDefault()?.Values.FirstOrDefault();
            return count != null && 0 < Convert.ToDecimal(count, CultureInfo.InvariantCulture);
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
        static async Task<int> UpdateAsync(KintoneConnection connection, KintoneWrite update, Dictionary<string, object?> args)
        {
            var (definition, key, keyValue) = GetTarget(await connection.GetTableDefinitionAsync(update.Table), update, args);

            var record = new JsonObject();
            var changesKey = false;
            foreach (var (name, parameter) in update.Values)
            {
                var column = GetColumn(definition, name);
                var value = GetArgument(args, parameter);
                if (name == KintoneTableDefinition.RevisionColumn) continue;
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
            if (!await ExistsAsync(connection, update, args)) return 0;

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
        static async Task<int> DeleteAsync(KintoneConnection connection, KintoneWrite delete, Dictionary<string, object?> args)
        {
            var (definition, key, keyValue) = GetTarget(await connection.GetTableDefinitionAsync(delete.Table), delete, args);
            var (canMatch, revision) = GetRevision(delete, args);
            if (!canMatch) return 0;
            if (!await ExistsAsync(connection, delete, args)) return 0;
            var id = await FindRecordIdAsync(connection, definition, key, keyValue);
            if (id == null) return 0;
            return await connection.DeleteAsync(delete.Table, id, revision) ? 1 : 0;
        }
    }
}
