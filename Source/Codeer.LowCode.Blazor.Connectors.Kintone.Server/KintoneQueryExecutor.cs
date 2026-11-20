using System.Globalization;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    /// <summary>
    /// 読み出しの計画 (<see cref="KintoneRead"/> / <see cref="KintoneAggregate"/>) を kintone のレコードの読み出しで実行する。
    /// 別の表を読んで値を集める条件 (結合先の列の条件・一覧をまたぐ条件) は、値を集めてから主表の条件 (列 in (a, b, ...)) に読み替える。
    /// 1 回の要求に入れる値の数 (<see cref="KintoneQueryTranslator.JoinBatchSize"/>) を超えるときは要求を分けて全部読み、メモリで重ね・並べ・ページを切る。
    /// どの読み方でも MaxRows を超えたらエラーにする (黙って切り捨てない)。
    /// </summary>
    static class KintoneQueryExecutor
    {
        // ---------------------------------------------------------------- 一覧・件数

        internal static async Task<List<IDictionary<string, object>>> ReadAsync(KintoneConnection connection, KintoneRead read)
        {
            var maxRows = connection.Settings.MaxRows;
            var joins = NeededJoins(read.Joins, read.MainAlias, read.Columns.Select(e => e.Alias).Concat(read.Order.Select(e => e.Alias)));
            var filter = read.IsEmpty ? new ConstNode(false) : read.Where == null ? null : await ResolveAsync(connection, read.Where, read.Definition);
            if (filter is ConstNode constant)
            {
                if (!constant.Value) return read.IsCount ? CountResult(0) : new();
                filter = null;
            }
            var fits = Fits(filter);
            read.Filter = filter == null || !fits ? null : KintoneQueryTranslator.Render(filter, true);

            if (read.IsCount)
            {
                //件数は totalCount (1 行だけ読む要求に付ける)。要求を分けたときは分けて読んだ行を数える
                if (fits) return CountResult(await ReadCountAsync(connection, read.Table, read.Filter));
                var rows = await ReadFilteredAsync(connection, read.Table, read.Definition, filter!, read.Definition.KeyColumns, maxRows);
                return CountResult(rows.Count);
            }
            if (read.Limit == 0) return new();

            List<Dictionary<string, object?>> mainRows;
            Dictionary<string, List<Dictionary<string, object?>?>> rowsByAlias;
            if (fits && !read.HasJoinedOrder)
            {
                //条件・並び・ページングをそのまま kintone に渡す
                mainRows = (await KintoneDbAccessor.ReadRowsAsync(connection, read.Table, read.SelectColumns, read.Filter, read.OrderBy, read.Limit, read.Offset, maxRows))
                    .Select(e => KintoneDbAccessor.ToValues(read.Definition, e)).ToList();
                rowsByAlias = await JoinAsync(connection, read.MainAlias, joins, mainRows);
            }
            else
            {
                //条件に合う行を全部読み (MaxRows まで)、メモリで並べてページを切る
                var columns = read.SelectColumns.Count == 0 ? new List<string>()
                    : read.SelectColumns.Concat(read.Definition.KeyColumns).Concat(read.Order.Where(e => e.Alias == read.MainAlias).Select(e => e.Column)).Distinct().ToList();
                mainRows = filter == null
                    ? await ReadAllAsync(connection, read.Table, read.Definition, columns, maxRows)
                    : await ReadFilteredAsync(connection, read.Table, read.Definition, filter, columns, maxRows);
                if (read.HasJoinedOrder)
                {
                    rowsByAlias = await JoinAsync(connection, read.MainAlias, joins, mainRows);
                    var order = SortOrder(read, rowsByAlias, mainRows.Count);
                    order = Page(order, read.Offset, read.Limit);
                    mainRows = order.Select(i => mainRows[i]).ToList();
                    rowsByAlias = rowsByAlias.ToDictionary(e => e.Key, e => order.Select(i => e.Value[i]).ToList());
                }
                else
                {
                    var single = new Dictionary<string, List<Dictionary<string, object?>?>> { [read.MainAlias] = mainRows.Cast<Dictionary<string, object?>?>().ToList() };
                    var order = Page(SortOrder(read, single, mainRows.Count), read.Offset, read.Limit);
                    mainRows = order.Select(i => mainRows[i]).ToList();
                    rowsByAlias = await JoinAsync(connection, read.MainAlias, joins, mainRows);
                }
            }

            var result = new List<IDictionary<string, object>>();
            for (var i = 0; i < mainRows.Count; i++)
            {
                var row = new Dictionary<string, object>();
                foreach (var column in read.Columns)
                {
                    object? value = null;
                    rowsByAlias[column.Alias][i]?.TryGetValue(column.Column, out value);
                    row[column.Key] = value!;
                }
                result.Add(row);
            }
            return result;
        }

        static List<IDictionary<string, object>> CountResult(long count) => new() { new Dictionary<string, object> { ["count"] = count } };

        //条件に合うレコードの数 (totalCount)。レコードは 1 件だけ読む
        static async Task<long> ReadCountAsync(KintoneConnection connection, string table, string? filter)
        {
            var query = KintoneQueryTranslator.BuildQuery(filter, Array.Empty<string>(), 1, 0);
            return (await connection.ReadRecordsAsync(table, new[] { KintoneTableDefinition.IdColumn }, query, true)).TotalCount!.Value;
        }

        //主表の行に結合先の行を付ける (別名 → 行ごとの値。結合先が無い行は null)
        static async Task<Dictionary<string, List<Dictionary<string, object?>?>>> JoinAsync(KintoneConnection connection, string mainAlias, List<KintoneJoin> joins, List<Dictionary<string, object?>> mainRows)
        {
            var rowsByAlias = new Dictionary<string, List<Dictionary<string, object?>?>> { [mainAlias] = mainRows.Cast<Dictionary<string, object?>?>().ToList() };
            foreach (var join in joins) rowsByAlias[join.Alias] = await KintoneDbAccessor.ReadJoinAsync(connection, join, rowsByAlias[join.SourceAlias]);
            return rowsByAlias;
        }

        //使う別名とその結合元をたどった結合だけ (from の順のまま)。条件だけに使う結合は読まない (条件は値を集めて読み替え済み)
        static List<KintoneJoin> NeededJoins(List<KintoneJoin> joins, string mainAlias, IEnumerable<string> aliases)
        {
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in aliases)
            {
                for (var a = alias; a != mainAlias && needed.Add(a);) a = joins.First(e => e.Alias == a).SourceAlias;
            }
            return joins.Where(e => needed.Contains(e.Alias)).ToList();
        }

        static List<int> Page(List<int> order, int offset, int? limit)
            => order.Skip(offset).Take(limit ?? int.MaxValue).ToList();

        //メモリでの並び。並びの項目のあとにキーの列 (順序を決めるため)。空の値は昇順で先・降順で後 (kintone の order by が空の値を並べる向きは実物で未確認)
        static List<int> SortOrder(KintoneRead read, Dictionary<string, List<Dictionary<string, object?>?>> rowsByAlias, int count)
        {
            var keys = read.Order.Select(e => (e.Alias, e.Column, e.Descending)).ToList();
            foreach (var key in read.Definition.KeyColumns)
            {
                if (!keys.Any(e => e.Alias == read.MainAlias && e.Column == key)) keys.Add((read.MainAlias, key, false));
            }
            object? Value(int row, string alias, string column)
            {
                object? value = null;
                rowsByAlias[alias][row]?.TryGetValue(column, out value);
                return value;
            }
            var order = Enumerable.Range(0, count).ToList();
            order.Sort((a, b) =>
            {
                foreach (var (alias, column, descending) in keys)
                {
                    var compare = CompareNullsFirst(Value(a, alias, column), Value(b, alias, column));
                    if (compare != 0) return descending ? -compare : compare;
                }
                return a.CompareTo(b);
            });
            return order;
        }

        static int CompareNullsFirst(object? a, object? b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;
            return CompareValues(a, b);
        }

        /// <summary>値どうしの比較 (数値は数値・日付と時刻はその型・文字は序数)。型が違えば文字にして比べる。</summary>
        internal static int CompareValues(object a, object b)
        {
            if (a is decimal da && b is decimal db) return da.CompareTo(db);
            if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
            if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
            return string.CompareOrdinal(KintoneQueryTranslator.KeyText(a), KintoneQueryTranslator.KeyText(b));
        }

        // ---------------------------------------------------------------- 条件の読み替え

        /// <summary>
        /// 条件の中の「別の表を読んで集める値との一致」(<see cref="InSetNode"/>) を、値を集めて <see cref="ValuesNode"/> にし、畳み直す。
        /// 集めた値が無ければ偽。table はこの条件で絞る表 (値の書き方を列の種類に合わせる)。
        /// 結果の not は集めた値との一致の直前まで押し下げてある (<see cref="KintoneQueryTranslator.PushNot"/>)。
        /// </summary>
        internal static async Task<FilterNode> ResolveAsync(KintoneConnection connection, FilterNode node, KintoneTableDefinition table)
        {
            var cache = new Dictionary<KeySource, List<object>>(ReferenceEqualityComparer.Instance);
            return KintoneQueryTranslator.PushNot(KintoneQueryTranslator.Fold(await ResolveCoreAsync(connection, KintoneQueryTranslator.Fold(node), table, cache)));
        }

        static async Task<FilterNode> ResolveCoreAsync(KintoneConnection connection, FilterNode node, KintoneTableDefinition table, Dictionary<KeySource, List<object>> cache)
        {
            switch (node)
            {
                case InSetNode inSet:
                    {
                        if (!cache.TryGetValue(inSet.Source, out var values))
                        {
                            values = await ReadKeysAsync(connection, inSet.Source);
                            cache[inSet.Source] = values;
                        }
                        var column = table.FindColumn(inSet.Column)!;
                        var literals = new List<KeyLiteral>();
                        foreach (var value in values)
                        {
                            //列の種類で書けない値 (数字の列に文字など) はその列のどの行とも一致しない
                            var literal = KintoneQueryTranslator.LiteralFor(column, value, true);
                            //突き合わせの文字は列の値と同じ書き方にする (日付の列に当てる 0:00 の日時は日付)
                            var key = column.Kind == KintoneColumnKind.Date && value is DateTime dateTime ? DateOnly.FromDateTime(dateTime) : value;
                            if (literal != null) literals.Add(new KeyLiteral(KintoneQueryTranslator.KeyText(key), literal));
                        }
                        return literals.Count == 0 ? new ConstNode(false) : new ValuesNode(column, literals);
                    }
                case NotNode not:
                    return new NotNode(await ResolveCoreAsync(connection, not.Child, table, cache));
                case AndNode and:
                    {
                        var children = new List<FilterNode>();
                        foreach (var child in and.Children) children.Add(await ResolveCoreAsync(connection, child, table, cache));
                        return new AndNode(children);
                    }
                case OrNode or:
                    {
                        var children = new List<FilterNode>();
                        foreach (var child in or.Children) children.Add(await ResolveCoreAsync(connection, child, table, cache));
                        return new OrNode(children);
                    }
                default:
                    return node;
            }
        }

        //別の表を条件で読んで、1 つの列の値を集める (重ならない・null は除く)。読む行は MaxRows まで
        static async Task<List<object>> ReadKeysAsync(KintoneConnection connection, KeySource source)
        {
            var filter = await ResolveAsync(connection, source.Filter, source.Definition);
            if (filter is ConstNode { Value: false }) return new();
            var columns = new List<string> { source.Column };
            var rows = filter is ConstNode
                ? await ReadAllAsync(connection, source.Table, source.Definition, columns, connection.Settings.MaxRows)
                : await ReadFilteredAsync(connection, source.Table, source.Definition, filter, columns, connection.Settings.MaxRows);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var values = new List<object>();
            foreach (var row in rows)
            {
                if (!row.TryGetValue(source.Column, out var value) || value == null) continue;
                if (seen.Add(KintoneQueryTranslator.KeyText(value))) values.Add(value);
            }
            return values;
        }

        /// <summary>条件を 1 回の要求に書けるか (集めた値と比較の数が <see cref="KintoneQueryTranslator.JoinBatchSize"/> 以下。集めた値が無い条件は本体が書いたまま)。</summary>
        internal static bool Fits(FilterNode? filter) => filter == null || !HasValues(filter) || Atoms(filter) <= KintoneQueryTranslator.JoinBatchSize;

        static bool HasValues(FilterNode node) => KintoneQueryTranslator.HasValues(node);

        //比較の数 (集めた値は値の数。その not は列が空の行を明示する比較も数える)
        static int Atoms(FilterNode node) => node switch
        {
            ValuesNode values => values.Values.Count,
            NotNode { Child: ValuesNode values } => values.Values.Count + 1,
            TermNode => 1,
            NotNode not => Atoms(not.Child),
            AndNode and => and.Children.Sum(Atoms),
            OrNode or => or.Children.Sum(Atoms),
            _ => 0,
        };

        //条件なしで全部読む (MaxRows まで)
        static async Task<List<Dictionary<string, object?>>> ReadAllAsync(KintoneConnection connection, string table, KintoneTableDefinition definition, List<string> columns, int maxRows)
        {
            return (await KintoneDbAccessor.ReadRowsAsync(connection, table, columns, null, Array.Empty<string>(), null, 0, maxRows))
                .Select(e => KintoneDbAccessor.ToValues(definition, e)).ToList();
        }

        /// <summary>
        /// 条件に合う行を全部読む (順不同・MaxRows まで)。columns が空なら $select を付けない (全部の列)。
        /// 1 回の要求に書けない条件 (集めた値と比較の数が <see cref="KintoneQueryTranslator.JoinBatchSize"/> を超える) は、and でつながる条件ごとに分けて:
        /// <list type="bullet">
        /// <item>集めた値を含まない条件 (主表の比較) と、集めた値を含む条件のうち収まるもの (値の少ない順) は要求に書く</item>
        /// <item>残りの条件は読んだ行をメモリで評価する。要求には、その条件の集めた値との一致を「真」に緩めた条件 (主表の比較だけ) を書いて読む行を絞る。
        ///   条件の中の主表の比較の真偽は、その比較を足した要求でキーを読んで知る (比較ごとに 1 回。kintone の比較の意味のまま)</item>
        /// <item>要求に書いた条件に読む行を絞る一致が無ければ、メモリで評価する条件のうち「集めた値との一致の or」を 1 つ選び、値を組に分けて要求に書く (組ごとに 1 回)</item>
        /// </list>
        /// 要求の数は (組の数) × (1 + メモリで評価する条件の中の主表の比較の数) まで (条件の大きさに比例する)。分けた結果はキーで重ねを除き、合わせて MaxRows を超えたらエラー。
        /// </summary>
        internal static async Task<List<Dictionary<string, object?>>> ReadFilteredAsync(
            KintoneConnection connection, string table, KintoneTableDefinition definition, FilterNode filter, IEnumerable<string> columns, int maxRows)
        {
            var select = columns.ToList();
            filter = KintoneQueryTranslator.PushNot(filter);
            if (filter is ConstNode constant) return constant.Value ? await ReadAllAsync(connection, table, definition, select, maxRows) : new();
            if (Fits(filter)) return await ReadOnceAsync(connection, table, definition, filter, select, maxRows);

            //and でつながる条件ごとに、要求に書くか (inRequest) メモリで評価するかを決める
            var conjuncts = filter is AndNode top ? top.Children : new List<FilterNode> { filter };
            var relaxed = conjuncts.Select(Relax).ToList();
            var inRequest = conjuncts.Select(e => !HasValues(e)).ToArray();
            var indices = Enumerable.Range(0, conjuncts.Count).ToList();
            //主表の比較の真偽を知る要求 (比較 and 要求の条件) も 1 回に収まるよう、その比較の分を空けておく
            var allTerms = new Dictionary<string, FilterNode>(StringComparer.Ordinal);
            foreach (var i in indices.Where(i => !inRequest[i])) CollectTerms(conjuncts[i], allTerms);
            var termRoom = allTerms.Values.Select(Atoms).DefaultIfEmpty(0).Max();
            var used = indices.Sum(i => inRequest[i] ? Atoms(conjuncts[i]) : Atoms(relaxed[i]));
            foreach (var i in indices.Where(i => !inRequest[i]).OrderBy(i => Atoms(conjuncts[i])).ToList())
            {
                var extra = Atoms(conjuncts[i]) - Atoms(relaxed[i]);
                if (KintoneQueryTranslator.JoinBatchSize - termRoom < used + extra) continue;
                inRequest[i] = true;
                used += extra;
            }
            var memory = indices.Where(i => !inRequest[i]).ToList();

            //要求に書いた条件に読む行を絞る一致が無ければ、メモリで評価する条件のうち値の少ない「一致の or」を、値を組に分けて要求に書く
            int? driver = null;
            if (!indices.Any(i => inRequest[i] && IsDrivable(conjuncts[i])))
                driver = memory.Where(i => IsDrivable(conjuncts[i])).OrderBy(i => Atoms(conjuncts[i])).Select(i => (int?)i).FirstOrDefault();
            var baseFilter = KintoneQueryTranslator.Fold(new AndNode(indices.Select(i => inRequest[i] ? conjuncts[i] : relaxed[i]).ToList()));

            //メモリで評価する条件の中の主表の比較 (集めた値を含まない一番外側の部分)。真偽は要求でキーを読んで知る
            var terms = new Dictionary<string, FilterNode>(StringComparer.Ordinal);
            foreach (var i in memory.Where(i => i != driver)) CollectTerms(conjuncts[i], terms);

            var pieces = new List<FilterNode?> { null };
            if (driver != null)
            {
                var room = KintoneQueryTranslator.JoinBatchSize - Atoms(baseFilter) - termRoom;
                if (0 < room)
                {
                    pieces = DriverPieces(conjuncts[driver.Value], room);
                }
                else
                {
                    //組に分けても 1 回に書けないときは、その条件もメモリで評価する
                    CollectTerms(conjuncts[driver.Value], terms);
                    driver = null;
                }
            }
            var evaluate = memory.Where(i => i != driver).Select(i => conjuncts[i]).ToList();

            var readColumns = select;
            if (select.Count != 0)
                readColumns = select.Concat(definition.KeyColumns).Concat(evaluate.SelectMany(ValueColumns)).Distinct().ToList();

            var result = new List<Dictionary<string, object?>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var piece in pieces)
            {
                var unit = piece == null ? baseFilter : KintoneQueryTranslator.Fold(new AndNode(new List<FilterNode> { piece, baseFilter }));
                if (unit is ConstNode { Value: false }) continue;
                var rows = unit is ConstNode
                    ? await ReadAllAsync(connection, table, definition, readColumns, maxRows)
                    : await ReadOnceAsync(connection, table, definition, unit, readColumns, maxRows);

                //主表の比較ごとに、この要求の中でその比較が真の行のキー
                var termKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                if (rows.Count != 0)
                {
                    foreach (var (text, term) in terms)
                    {
                        var termFilter = KintoneQueryTranslator.Fold(new AndNode(new List<FilterNode> { term, unit }));
                        var termRows = termFilter is ConstNode c
                            ? (c.Value ? await ReadAllAsync(connection, table, definition, definition.KeyColumns.ToList(), maxRows) : new())
                            : await ReadOnceAsync(connection, table, definition, termFilter, definition.KeyColumns.ToList(), maxRows);
                        termKeys[text] = termRows.Select(e => RowKey(definition, e)).ToHashSet(StringComparer.Ordinal);
                    }
                }

                foreach (var row in rows)
                {
                    var key = RowKey(definition, row);
                    if (seen.Contains(key)) continue;
                    if (!evaluate.All(e => Evaluate(e, row, term => termKeys[KintoneQueryTranslator.Render(term, true)].Contains(key)))) continue;
                    seen.Add(key);
                    result.Add(row);
                }
                if (maxRows < result.Count) throw TooManyRows(maxRows);
            }
            return result;
        }

        //1 回の要求で読む (MaxRows まで)
        static async Task<List<Dictionary<string, object?>>> ReadOnceAsync(
            KintoneConnection connection, string table, KintoneTableDefinition definition, FilterNode filter, List<string> select, int maxRows)
        {
            return (await KintoneDbAccessor.ReadRowsAsync(connection, table, select, KintoneQueryTranslator.Render(filter, true), Array.Empty<string>(), null, 0, maxRows))
                .Select(e => KintoneDbAccessor.ToValues(definition, e)).ToList();
        }

        //集めた値との一致 (とその not) を「真」に緩めた条件 (主表の比較だけが残る。元の条件に合う行はこの条件にも合う)
        static FilterNode Relax(FilterNode node) => KintoneQueryTranslator.Fold(RelaxCore(node));

        static FilterNode RelaxCore(FilterNode node) => node switch
        {
            ValuesNode or NotNode { Child: ValuesNode } => new ConstNode(true),
            AndNode and => new AndNode(and.Children.Select(RelaxCore).ToList()),
            OrNode or => new OrNode(or.Children.Select(RelaxCore).ToList()),
            _ => node,
        };

        //値を組に分けて要求に書ける条件: 集めた値との一致か、一致と主表の比較の or (一致を 1 つ以上含む)
        static bool IsDrivable(FilterNode node) => node switch
        {
            ValuesNode => true,
            OrNode or => or.Children.Any(e => e is ValuesNode) && or.Children.All(e => e is ValuesNode || !HasValues(e)),
            _ => false,
        };

        //値を組に分けた要求の条件: 一致は値 room 個ずつ、主表の比較はまとめて 1 つ。どれかに合う行がその条件に合う行
        static List<FilterNode?> DriverPieces(FilterNode driver, int room)
        {
            var children = driver is OrNode or ? or.Children : new List<FilterNode> { driver };
            var pieces = new List<FilterNode?>();
            foreach (var values in children.OfType<ValuesNode>())
            {
                for (var start = 0; start < values.Values.Count; start += room)
                    pieces.Add(new ValuesNode(values.Definition, values.Values.Skip(start).Take(room).ToList()));
            }
            var others = children.Where(e => e is not ValuesNode).ToList();
            if (others.Count != 0) pieces.Add(others.Count == 1 ? others[0] : new OrNode(others));
            return pieces;
        }

        //集めた値を含まない一番外側の部分 (主表の比較) を集める。キーは $filter の文字
        static void CollectTerms(FilterNode node, Dictionary<string, FilterNode> terms)
        {
            switch (node)
            {
                case ConstNode:
                case ValuesNode:
                case NotNode { Child: ValuesNode }:
                    return;
                case AndNode all when HasValues(all):
                    foreach (var child in all.Children) CollectTerms(child, terms);
                    return;
                case OrNode any when HasValues(any):
                    foreach (var child in any.Children) CollectTerms(child, terms);
                    return;
                default:
                    terms.TryAdd(KintoneQueryTranslator.Render(node, true), node);
                    return;
            }
        }

        static IEnumerable<string> ValueColumns(FilterNode node) => node switch
        {
            ValuesNode values => new[] { values.Column },
            NotNode not => ValueColumns(not.Child),
            AndNode and => and.Children.SelectMany(ValueColumns),
            OrNode or => or.Children.SelectMany(ValueColumns),
            _ => Array.Empty<string>(),
        };

        //メモリでの評価。集めた値との一致は真か偽 (列が空なら一致しない = 偽、その not は真)。主表の比較の真偽は term で知る
        static bool Evaluate(FilterNode node, Dictionary<string, object?> row, Func<FilterNode, bool> term)
        {
            if (!HasValues(node)) return node is ConstNode c ? c.Value : term(node);
            return node switch
            {
                ValuesNode values => values.Contains(row.GetValueOrDefault(values.Column)),
                NotNode { Child: ValuesNode values } => !values.Contains(row.GetValueOrDefault(values.Column)),
                AndNode and => and.Children.All(e => Evaluate(e, row, term)),
                OrNode or => or.Children.Any(e => Evaluate(e, row, term)),
                _ => throw new InvalidOperationException(),
            };
        }

        static KintoneException TooManyRows(int maxRows)
            => new($"The query returned more than {maxRows} rows (MaxRows of the kintone data source). Narrow the condition or use paging.", KintoneErrorKind.InvalidRequest);

        static string RowKey(KintoneTableDefinition definition, Dictionary<string, object?> row)
            => string.Join("\u0001", definition.KeyColumns.Select(e => row.GetValueOrDefault(e) is { } value ? KintoneQueryTranslator.KeyText(value) : "\u0000"));

        // ---------------------------------------------------------------- 集計

        //集計の 1 単位 (主表の 1 行と、結合先の行)
        sealed class Member
        {
            public Dictionary<string, Dictionary<string, object?>?> Rows { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// 集計を実行する。kintone に集計の API は無いので、条件に合う行を読んで (MaxRows まで。超えたらエラー) メモリで集計する。
        /// 結合先の列 (軸・集計値) は結合元の値で結合先を読んで突き合わせる。
        /// </summary>
        internal static async Task<List<IDictionary<string, object>>> AggregateAsync(KintoneConnection connection, KintoneAggregate plan)
        {
            var maxRows = connection.Settings.MaxRows;
            var filter = plan.IsEmpty ? new ConstNode(false) : plan.Where == null ? null : await ResolveAsync(connection, plan.Where, plan.Definition);
            var isEmpty = filter is ConstNode { Value: false };
            if (filter is ConstNode) filter = null;

            //行を選ぶ定義 (WithinGroupsOf): 先にその集計を実行して、選ばれた軸の値の組を集める。1 行も無ければどの行も選ばれない
            HashSet<string>? withinKeys = null;
            var withinColumns = plan.Within?.On.Select(e => e.Outer).ToList() ?? new();
            if (plan.Within != null && !isEmpty)
            {
                var selected = await AggregateAsync(connection, plan.Within.Plan);
                if (selected.Count == 0) isEmpty = true;
                withinKeys = selected.Select(row => TupleKey(plan.Within.On.Select(e => row.TryGetValue(e.InnerAlias, out var value) ? value : null))).ToHashSet(StringComparer.Ordinal);
            }

            //細かく集計する列 (主表): 主表の軸と、行を選ぶ定義で突き合わせる主表の列と、結合先の列の元になる主表の列
            var mainGroups = plan.Groups.Where(e => e.Alias == plan.MainAlias).Select(e => e.Column).ToList();
            var joinedColumns = plan.Groups.Concat(plan.Measures.Where(e => e.Column != null).Select(e => e.Column!)).Concat(withinColumns)
                .Where(e => e.Alias != plan.MainAlias).ToList();
            var fineColumns = mainGroups.Concat(withinColumns.Where(e => e.Alias == plan.MainAlias).Select(e => e.Column))
                .Concat(joinedColumns.Select(e => RootSourceColumn(plan, e.Alias))).Distinct().ToList();
            var mainMeasures = plan.Measures.Where(e => e.Column != null && e.Column.Alias == plan.MainAlias).ToList();

            var members = new List<Member>();
            if (!isEmpty)
            {
                var columns = fineColumns.Concat(mainMeasures.Select(e => e.Column!.Column)).Concat(plan.Definition.KeyColumns).Distinct().ToList();
                var rows = filter == null
                    ? await ReadAllAsync(connection, plan.Table, plan.Definition, columns, maxRows)
                    : await ReadFilteredAsync(connection, plan.Table, plan.Definition, filter, columns, maxRows);
                foreach (var row in rows)
                {
                    var member = new Member();
                    member.Rows[plan.MainAlias] = row;
                    members.Add(member);
                }
                //結合先 (結合元の値で読み、突き合わせる)
                foreach (var join in NeededJoins(plan.Joins, plan.MainAlias, joinedColumns.Select(e => e.Alias)))
                {
                    var joined = await KintoneDbAccessor.ReadJoinAsync(connection, join, members.Select(e => e.Rows.GetValueOrDefault(join.SourceAlias)).ToList());
                    for (var i = 0; i < members.Count; i++) members[i].Rows[join.Alias] = joined[i];
                }
                //行を選ぶ定義に合う行だけ残す
                if (withinKeys != null && plan.Within!.On.Count != 0)
                    members = members.Where(e => withinKeys.Contains(TupleKey(withinColumns.Select(c => ValueOf(e, c))))).ToList();
            }

            //軸でまとめる (軸なしは 1 グループ。行が無くても 1 行返す)
            var groups = new List<List<Member>>();
            if (plan.Groups.Count == 0)
            {
                groups.Add(members);
            }
            else
            {
                var index = new Dictionary<string, List<Member>>(StringComparer.Ordinal);
                foreach (var member in members)
                {
                    var key = string.Join("\u0001", plan.Groups.Select(e => ValueOf(member, e) is { } value ? "v" + KintoneQueryTranslator.KeyText(value) : "\u0000"));
                    if (!index.TryGetValue(key, out var list))
                    {
                        list = new();
                        index[key] = list;
                        groups.Add(list);
                    }
                    list.Add(member);
                }
            }

            var results = new List<Dictionary<string, object?>>();
            foreach (var group in groups)
            {
                var measures = plan.Measures.ToDictionary(e => e, e => Compute(e, group));
                if (!plan.Having.All(e => IsMatch(measures[e.Measure], e.Operator, e.Value))) continue;
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                var first = group.FirstOrDefault();
                foreach (var item in plan.Items)
                {
                    row[item.Alias] = item.Expression switch
                    {
                        AggregateColumn column => first == null ? null : ValueOf(first, column),
                        AggregateNullFlag flag => (first == null ? null : ValueOf(first, flag.Column)) == null ? 1L : 0L,
                        AggregateRank rank => Rank(rank, first == null ? null : ValueOf(first, rank.Column)),
                        AggregateFunctionCall call => measures[call],
                        _ => 1L,
                    };
                }
                results.Add(row);
            }

            if (plan.IsGroupCount) return CountResult(results.Count);

            //並び (PostgreSQL と同じく空の値は昇順で後・降順で先)。同じ順位は元の順のまま
            var ordered = results.Select((row, i) => (row, i)).ToList();
            ordered.Sort((a, b) =>
            {
                foreach (var (alias, descending) in plan.OrderBy)
                {
                    var x = a.row.GetValueOrDefault(alias);
                    var y = b.row.GetValueOrDefault(alias);
                    var compare = x == null ? (y == null ? 0 : 1) : y == null ? -1 : CompareValues(x, y);
                    if (compare != 0) return descending ? -compare : compare;
                }
                return a.i.CompareTo(b.i);
            });
            return ordered.Skip(plan.Offset).Take(plan.Limit ?? int.MaxValue)
                .Select(e => (IDictionary<string, object>)e.row.ToDictionary(x => x.Key, x => x.Value!)).ToList();
        }

        static string RootSourceColumn(KintoneAggregate plan, string alias)
        {
            var join = plan.Joins.First(e => e.Alias == alias);
            while (join.SourceAlias != plan.MainAlias) join = plan.Joins.First(e => e.Alias == join.SourceAlias);
            return join.SourceColumn;
        }

        //軸の値の組の突き合わせの文字 (空の値は空の値と一致する。値は KeyText で比べる = 軸でまとめるときと同じ)
        static string TupleKey(IEnumerable<object?> values)
            => string.Join("\u0001", values.Select(e => e is { } value ? "v" + KintoneQueryTranslator.KeyText(value) : "\u0000"));

        static object? ValueOf(Member member, AggregateColumn column)
        {
            object? value = null;
            member.Rows.GetValueOrDefault(column.Alias)?.TryGetValue(column.Column, out value);
            return column.Bucket == null ? value : Truncate(value, column.Bucket);
        }

        /// <summary>
        /// PostgreSQL の date_trunc と同じ丸め (結果は種類の無い日時。日付の列は 0:00 の日時として丸める)。週は月曜始まり。
        /// 日時 (UTC) は値の壁時計のまま丸める (本体が時差を ShiftMinutes で渡す)。
        /// 時差と年度の月のずれは PostgreSQL の interval と同じく足し引きする (月の加減で日が無ければその月の末日)。
        /// </summary>
        internal static DateTime? Truncate(object? value, DateBucketSpec bucket)
        {
            DateTime c;
            switch (value)
            {
                case null: return null;
                case DateTime dateTime: c = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified); break;
                case DateOnly date: c = date.ToDateTime(TimeOnly.MinValue); break;
                default: throw new KintoneException($"A value of '{value.GetType().Name}' cannot be grouped by a period of a date.", KintoneErrorKind.NotSupported);
            }
            c = c.AddMinutes(bucket.ShiftMinutes);
            switch (bucket.Unit)
            {
                case "hour": return new DateTime(c.Year, c.Month, c.Day, c.Hour, 0, 0);
                case "day": return c.Date;
                case "week": return c.Date.AddDays(-(((int)c.DayOfWeek + 6) % 7));
                case "month": return new DateTime(c.Year, c.Month, 1);
            }
            var s = c.AddMonths(-bucket.FiscalMonths);
            var start = bucket.Unit == "quarter" ? new DateTime(s.Year, (s.Month - 1) / 3 * 3 + 1, 1) : new DateTime(s.Year, 1, 1);
            return start.AddMonths(bucket.FiscalMonths);
        }

        static long Rank(AggregateRank rank, object? value)
        {
            if (value == null) return rank.Else;
            var text = KintoneQueryTranslator.KeyText(value);
            foreach (var (code, order) in rank.Codes)
            {
                if (code == text) return order;
            }
            return rank.Else;
        }

        //SQL と同じ意味: 件数は行数、それ以外は空の値を除いて集計し、値が 1 つも無ければ null
        static object? Compute(AggregateFunctionCall call, List<Member> members)
        {
            if (call.Column == null) return (long)members.Count;
            var values = members.Select(e => ValueOf(e, call.Column)).Where(e => e != null).Select(e => e!).ToList();
            switch (call.Function)
            {
                case "countdistinct":
                    return (long)values.Select(KintoneQueryTranslator.KeyText).Distinct(StringComparer.Ordinal).Count();
                case "sum":
                    return values.Count == 0 ? null : values.Sum(KintoneQueryTranslator.ToDecimal);
                case "avg":
                    return values.Count == 0 ? null : values.Sum(KintoneQueryTranslator.ToDecimal) / values.Count;
                case "min":
                    return values.Count == 0 ? null : values.Aggregate((a, b) => CompareValues(a, b) <= 0 ? a : b);
                case "max":
                    return values.Count == 0 ? null : values.Aggregate((a, b) => CompareValues(a, b) >= 0 ? a : b);
                default:
                    throw new InvalidOperationException();
            }
        }

        static bool IsMatch(object? value, string op, decimal right)
        {
            if (value == null) return false;
            var left = KintoneQueryTranslator.ToDecimal(value);
            return op switch
            {
                "=" => left == right,
                "<>" => left != right,
                "<" => left < right,
                "<=" => left <= right,
                ">" => left > right,
                ">=" => left >= right,
                _ => throw new InvalidOperationException(),
            };
        }
    }
}
