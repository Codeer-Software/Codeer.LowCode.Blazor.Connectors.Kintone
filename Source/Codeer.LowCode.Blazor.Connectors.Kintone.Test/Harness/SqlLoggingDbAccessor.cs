using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Data;
using System.Data.Common;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Test.Harness
{
    /// <summary>本体が組んだ SQL を記録して、そのまま中の IDbAccessor (KintoneDbAccessor) に渡す。</summary>
    public class SqlLoggingDbAccessor : IDbAccessor
    {
        readonly IDbAccessor _inner;

        public SqlLoggingDbAccessor(IDbAccessor inner) => _inner = inner;

        /// <summary>(メソッド, SQL, 引数)。</summary>
        public List<(string Method, string Sql, Dictionary<string, object?> Args)> Log { get; } = new();

        public List<string> Sqls => Log.Select(e => e.Sql).ToList();

        public DataSource? GetDataSource(string dataSource) => _inner.GetDataSource(dataSource);
        public void StartTransaction() => _inner.StartTransaction();
        public Task CommitAsync() => _inner.CommitAsync();
        public Task RollbackAsync() => _inner.RollbackAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public DbConnection GetConnection(string dataSourceName) => _inner.GetConnection(dataSourceName);
        public IDbTransaction? GetTransaction(string dataSourceName) => _inner.GetTransaction(dataSourceName);
        public DbTableDefinitionCache? DbTableDefinitionCache => _inner.DbTableDefinitionCache;

        public object? ConvertFieldValueToDbValue(string dataSourceName, string rawDbTypeName, object? value)
            => _inner.ConvertFieldValueToDbValue(dataSourceName, rawDbTypeName, value);

        public object? ConvertDbValueToFieldValue(string dataSourceName, object dbValue, Type fieldType)
            => _inner.ConvertDbValueToFieldValue(dataSourceName, dbValue, fieldType);

        public Task<List<DbTableDefinition>?> GetCustomTableDefinitionsAsync(string dataSourceName) => _inner.GetCustomTableDefinitionsAsync(dataSourceName);

        public Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableNames) => _inner.GetCustomTableDefinitionsAsync(dataSourceName, tableNames);

        public Task<DbSqlCommandResult> ExecuteSqlCommandAsync(string dataSourceName, DbSqlCommand command) => _inner.ExecuteSqlCommandAsync(dataSourceName, command);

        public Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            Log.Add((nameof(QueryAsync), query, args.ToDictionary(e => e.Key, e => e.Value.Value)));
            return _inner.QueryAsync(dataSourceName, query, args);
        }

        public Task<int> ExecuteAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            Log.Add((nameof(ExecuteAsync), query, new Dictionary<string, object?>(args)));
            return _inner.ExecuteAsync(dataSourceName, query, args);
        }

        public Task<string> InsertAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            Log.Add((nameof(InsertAsync), query, new Dictionary<string, object?>(args)));
            return _inner.InsertAsync(dataSourceName, query, args);
        }
    }
}
