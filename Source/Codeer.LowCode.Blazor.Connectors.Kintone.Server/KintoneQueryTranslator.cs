using System.Globalization;
using System.Text;

namespace Codeer.LowCode.Blazor.Connectors.Kintone.Server
{
    enum SqlTokenKind
    {
        Space,
        Word,
        QuotedIdentifier,
        StringLiteral,
        Parameter,
        Number,
        Symbol,
    }

    record SqlToken(SqlTokenKind Kind, string Text)
    {
        public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;
        /// <summary>識別子 (引用符つき・なし)。</summary>
        public bool IsIdentifier => Kind == SqlTokenKind.QuotedIdentifier || Kind == SqlTokenKind.Word;
    }

    /// <summary>読み出しの結果の 1 列。</summary>
    class KintoneResultColumn
    {
        /// <summary>本体に返す行のキー (本体が書いた別名のまま)。</summary>
        public string Key { get; init; } = string.Empty;
        /// <summary>表の別名 (主表か結合)。</summary>
        public string Alias { get; init; } = string.Empty;
        public string Column { get; init; } = string.Empty;
    }

    /// <summary>結合 1 つ (left join "表" as "別名" on "元"."列"="別名"."列")。kintone では結合できないので、主表を読んだあと値を集めて読み、メモリで突き合わせる。</summary>
    class KintoneJoin
    {
        public string Table { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
        public string SourceAlias { get; init; } = string.Empty;
        public string SourceColumn { get; init; } = string.Empty;
        public string DestinationColumn { get; init; } = string.Empty;
        public KintoneTableDefinition Definition { get; init; } = new();
        /// <summary>読む列 (結合先の列・結果の列・並びの列・この結合を元にする結合の元の列)。</summary>
        public List<string> SelectColumns { get; } = new();

        internal void AddSelectColumn(string column)
        {
            if (!SelectColumns.Contains(column)) SelectColumns.Add(column);
        }
    }

    /// <summary>並びの 1 項目。</summary>
    record KintoneOrder(string Alias, string Column, bool Descending);

    /// <summary>読み出しの計画。</summary>
    class KintoneRead
    {
        public string Table { get; init; } = string.Empty;
        public string MainAlias { get; init; } = string.Empty;
        public KintoneTableDefinition Definition { get; init; } = new();
        /// <summary>select count(*)。</summary>
        public bool IsCount { get; init; }
        /// <summary>select * (行のキーは列名)。</summary>
        public bool IsSelectAll { get; init; }
        public List<KintoneResultColumn> Columns { get; } = new();
        public List<KintoneJoin> Joins { get; } = new();
        /// <summary>主表の $select (select * なら空 = 付けない。ただし ROWID / ROWMODID の列がある表は全部の列を書く)。</summary>
        public List<string> SelectColumns { get; } = new();
        /// <summary>
        /// 条件 (畳んだ構文木)。条件なしは null。結合先の列・一覧をまたぐ条件は、別の表を読んで集めた値との一致 (<see cref="InSetNode"/>) になっている。
        /// </summary>
        public FilterNode? Where { get; set; }
        /// <summary>クエリの条件。条件なし・別の表を読んで値を集める条件があるとき (<see cref="Where"/> を実行時に読み替える) は null。</summary>
        public string? Filter { get; set; }
        /// <summary>条件が必ず偽 (1=0)。要求を送らずに 0 行を返す。</summary>
        public bool IsEmpty { get; set; }
        /// <summary>$orderby の項目 (列名 [desc])。主表の列だけ。ページングつきなら最後にキーの列。</summary>
        public List<string> OrderBy { get; } = new();
        /// <summary>並び (結合先の列を含む。メモリで並べるときに使う)。</summary>
        public List<KintoneOrder> Order { get; } = new();
        /// <summary>結合先の列で並べる (主表の行を全部読んで結合してからメモリで並べる)。</summary>
        public bool HasJoinedOrder => Order.Any(e => e.Alias != MainAlias);
        /// <summary>読む行数 (本体がページングを付けたとき)。null なら全部。</summary>
        public int? Limit { get; set; }
        public int Offset { get; set; }
    }

    // ---------------------------------------------------------------- 条件の構文木

    /// <summary>条件の構文木 (クエリの条件 にする前)。</summary>
    abstract record FilterNode;
    record ConstNode(bool Value) : FilterNode;
    record AndNode(List<FilterNode> Children) : FilterNode;
    record OrNode(List<FilterNode> Children) : FilterNode;
    record NotNode(FilterNode Child) : FilterNode;
    enum TermOperator
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        IsNull,
        IsNotNull,
        Like,
        NotLike,
    }

    /// <summary>
    /// 1 つの比較 (列 演算子 値)。意味は SQL のまま: NotEqual・NotLike・大小比較は列が空の行では真にならない。
    /// kintone のクエリにするとき、フィールドの種類に合わせた書き方にする (<see cref="KintoneQueryTranslator.Render"/>)。
    /// </summary>
    record TermNode(KintoneColumnDefinition Column, TermOperator Operator, object? Value) : FilterNode;
    /// <summary>SQL で unknown になる比較 (値が null の比較)。真にも偽にもならない (not を付けても真にならない)。</summary>
    record UnknownNode : FilterNode;
    /// <summary>
    /// 真になる行の条件と偽になる行の条件を別に持つ比較 (結合先の列の比較・一覧をまたぐ条件)。どちらにも入らない行は SQL の unknown。
    /// not の中では False を使う (SQL の 3 値の論理と同じ結果にする)。
    /// </summary>
    record TriNode(FilterNode True, FilterNode False) : FilterNode;
    /// <summary>
    /// 列の値が、別の表を読んで集める値 (<see cref="KeySource"/>) のどれかに一致する。真か偽 (unknown にならない): 列が null の行は偽、
    /// その not は列が null の行も真 (結合先・子の行が無い行を SQL の意味で拾うのに使う)。
    /// </summary>
    record InSetNode(string Column, KeySource Source) : FilterNode;
    /// <summary>列の値が Values のどれかに一致する (<see cref="InSetNode"/> の値を集めたあと)。列が null の行は偽、not は列が null の行も真。</summary>
    sealed record ValuesNode(KintoneColumnDefinition Definition, List<KeyLiteral> Values) : FilterNode
    {
        public string Column => Definition.Name;

        readonly HashSet<string> _keys = Values.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        /// <summary>行の値が一致するか (値の文字 = <see cref="KintoneQueryTranslator.KeyText"/> で比べる)。</summary>
        public bool Contains(object? value) => value != null && _keys.Contains(KintoneQueryTranslator.KeyText(value));
    }
    /// <summary>一覧をまたぐ条件の相関 (子の列 = 親の列)。exists の中でだけ現れ、読み替えで取り除く。</summary>
    record CorrelationNode(string InnerColumn, string OuterColumn) : FilterNode;

    /// <summary>集めた値 1 つ (突き合わせの文字と、クエリの条件 のリテラル)。</summary>
    record KeyLiteral(string Key, string Literal);

    /// <summary>別の表を条件で読んで、1 つの列の値を集める (結合先の列の条件・一覧をまたぐ条件)。</summary>
    sealed class KeySource
    {
        public string Table { get; init; } = string.Empty;
        public KintoneTableDefinition Definition { get; init; } = new();
        /// <summary>その表の条件 (畳む前)。</summary>
        public FilterNode Filter { get; init; } = new ConstNode(true);
        /// <summary>集める列。</summary>
        public string Column { get; init; } = string.Empty;
    }

    // ---------------------------------------------------------------- 集計

    /// <summary>集計の SQL の式。</summary>
    abstract record AggregateExpression;
    /// <summary>列 ("別名"."列")。</summary>
    record AggregateColumn(string Alias, string Column) : AggregateExpression;
    /// <summary>case when 列 is null then 1 else 0 end (空値を最後に並べる印)。</summary>
    record AggregateNullFlag(AggregateColumn Column) : AggregateExpression;
    /// <summary>case 列 when 'a' then 0 ... else n end (候補の順)。</summary>
    record AggregateRank(AggregateColumn Column, List<(string Code, int Rank)> Codes, int Else) : AggregateExpression
    {
        public virtual bool Equals(AggregateRank? other) => other != null && Column == other.Column && Else == other.Else && Codes.SequenceEqual(other.Codes);
        public override int GetHashCode() => HashCode.Combine(Column, Else, Codes.Count);
    }
    /// <summary>集計関数。Function は count (Column が null なら count(*)) / countdistinct / sum / avg / min / max。</summary>
    record AggregateFunctionCall(string Function, AggregateColumn? Column) : AggregateExpression;
    /// <summary>定数 1 (グループの数を数える SQL の中の select 1)。</summary>
    record AggregateConstant : AggregateExpression;

    /// <summary>集計の select の 1 項目。</summary>
    record AggregateItem(string Alias, AggregateExpression Expression);

    /// <summary>集計後の絞り込み 1 つ (集計値 演算子 数値)。</summary>
    record HavingCondition(AggregateFunctionCall Measure, string Operator, decimal Value);

    /// <summary>集計の計画 (本体の AggregateSqlCreator が組む SQL)。</summary>
    class KintoneAggregate
    {
        public string Table { get; init; } = string.Empty;
        public string MainAlias { get; init; } = string.Empty;
        public KintoneTableDefinition Definition { get; init; } = new();
        public List<KintoneJoin> Joins { get; } = new();
        /// <summary>条件 (畳んだもの)。条件なしは null。</summary>
        public FilterNode? Where { get; set; }
        /// <summary>条件が必ず偽 (1=0)。要求を送らない。</summary>
        public bool IsEmpty { get; set; }
        public List<AggregateItem> Items { get; } = new();
        /// <summary>軸の列 (select の列の項目)。group by が無ければ空。</summary>
        public List<AggregateColumn> Groups { get; } = new();
        /// <summary>集計関数 (select と having に出るもの。重ならない)。</summary>
        public List<AggregateFunctionCall> Measures { get; } = new();
        public List<HavingCondition> Having { get; } = new();
        /// <summary>並び (select の別名)。</summary>
        public List<(string Alias, bool Descending)> OrderBy { get; } = new();
        public int? Limit { get; set; }
        public int Offset { get; set; }
        /// <summary>グループの数を数える SQL (select count(*) from (select 1 ... group by ...) x)。</summary>
        public bool IsGroupCount { get; init; }
        /// <summary>行を選ぶ定義 (WithinGroupsOf。inner join (集計) 別名 on ...)。無ければ null。</summary>
        public KintoneWithin? Within { get; set; }
    }

    /// <summary>
    /// 行を選ぶ定義 (本体の WithinGroupsOf): 別の集計 (同じ表) の結果の軸の値の組と一致する行だけを集計する。
    /// On は (この集計の列, 選ぶ集計の結果の別名) の組。空なら選ぶ集計が 1 行でも返せば全部の行 (on 1=1)。空の値は空の値と一致する。
    /// </summary>
    class KintoneWithin
    {
        public KintoneAggregate Plan { get; init; } = new();
        public List<(AggregateColumn Outer, string InnerAlias)> On { get; } = new();
    }

    class KintoneInsert
    {
        public string Table { get; init; } = string.Empty;
        public List<string> Columns { get; } = new();
        /// <summary>行ごとのパラメータ名 (Columns と同じ並び)。一括の追加は複数行。</summary>
        public List<List<string>> Rows { get; } = new();
        /// <summary>returning の列 (無ければ空)。</summary>
        public string ReturningColumn { get; set; } = string.Empty;
    }

    /// <summary>update / delete の対象。</summary>
    class KintoneWrite
    {
        public string Table { get; init; } = string.Empty;
        /// <summary>update の set (列, パラメータ)。delete は空。</summary>
        public List<(string Column, string Parameter)> Values { get; } = new();
        public string KeyColumn { get; set; } = string.Empty;
        public string KeyParameter { get; set; } = string.Empty;
        /// <summary>版の一致 ("$revision"=@p) のパラメータ (楽観ロック)。無ければ null。kintone の更新・削除の revision に渡す。</summary>
        public string? RevisionParameter { get; set; }
        /// <summary>版が空であること ("$revision" is null) が条件。kintone のレコードは必ず版を持つので、合う行は無い。</summary>
        public bool RevisionIsNull { get; set; }
        /// <summary>キーと版の一致のほかに条件があるか (論理削除など)。</summary>
        public bool HasOtherConditions { get; set; }
        /// <summary>where より後ろの SQL から版の条件を除いたもの。条件の確かめに使う。</summary>
        public string WhereSql { get; set; } = string.Empty;
    }

    /// <summary>
    /// 本体 (Codeer.LowCode.Blazor) が PostgreSQL 向けに組み立てた SQL を、kintone の REST API の要求 (クエリ)に読み替える。
    /// 本体が組み立てる形だけを受け付け、それ以外は拒否する (条件を落として続けない)。
    /// </summary>
    static class KintoneQueryTranslator
    {
        /// <summary>結合先を読むときに 1 回の クエリの条件 に入れる値の数。</summary>
        internal const int JoinBatchSize = 100;

        // ---------------------------------------------------------------- 字句

        internal static List<SqlToken> Tokenize(string sql)
        {
            var tokens = new List<SqlToken>();
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c))
                {
                    while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                    tokens.Add(new(SqlTokenKind.Space, " "));
                }
                else if (c == '"')
                {
                    var end = sql.IndexOf('"', i + 1);
                    if (end < 0) throw NotSupported(sql);
                    tokens.Add(new(SqlTokenKind.QuotedIdentifier, sql.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                }
                else if (c == '\'')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (sql.Length <= i) throw NotSupported(sql);
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(sql[i++]);
                    }
                    tokens.Add(new(SqlTokenKind.StringLiteral, sb.ToString()));
                }
                else if (c == '@' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    var start = i++;
                    while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    tokens.Add(new(SqlTokenKind.Parameter, sql[start..i]));
                }
                else if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.')) i++;
                    tokens.Add(new(SqlTokenKind.Number, sql[start..i]));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new(SqlTokenKind.Word, sql[start..i]));
                }
                else
                {
                    var two = i + 1 < sql.Length ? sql.Substring(i, 2) : string.Empty;
                    if (two is "<=" or ">=" or "<>" or "!=" or "||" or "::" or "->")
                    {
                        tokens.Add(new(SqlTokenKind.Symbol, two));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new(SqlTokenKind.Symbol, c.ToString()));
                        i++;
                    }
                }
            }
            return tokens;
        }

        static List<SqlToken> WithoutSpaces(IEnumerable<SqlToken> tokens) => tokens.Where(e => e.Kind != SqlTokenKind.Space).ToList();

        static KintoneException NotSupported(string sql)
            => new("This operation is not supported for a kintone data source: " + Shorten(sql));

        static KintoneException NotSupported(string what, string sql)
            => new($"{what} is not supported for a kintone data source: " + Shorten(sql));

        static string Shorten(string sql)
        {
            var text = string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 200 ? text : text[..200] + "...";
        }

        // ---------------------------------------------------------------- kintone のクエリの書き方

        /// <summary>クエリに書くフィールドコード (そのまま書く)。</summary>
        internal static string Name(string name) => name;

        //値の書式 (条件・キー・書き込みの全部でこの関数を使う)
        const string DateFormat = "yyyy-MM-dd";
        const string TimeFormat = "HH:mm";
        const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        internal static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
        /// <summary>kintone の時刻は分まで。</summary>
        internal static string FormatTime(TimeOnly time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture);
        /// <summary>kintone の日時は UTC の瞬間。種類の無い DateTime は UTC として扱う (DateTimeField の SaveAsUtc)。</summary>
        internal static string FormatDateTime(DateTime dateTime)
            => (dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

        /// <summary>数値を decimal にする。decimal に入らない値 (桁あふれ・NaN・無限大) は KintoneException。</summary>
        internal static decimal ToDecimal(object value)
        {
            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is OverflowException || e is FormatException || e is InvalidCastException)
            {
                throw new KintoneException($"The value '{Convert.ToString(value, CultureInfo.InvariantCulture)}' cannot be used as a kintone number.", KintoneErrorKind.InvalidRequest, e);
            }
        }

        /// <summary>数値の文字 (末尾の 0 を落とす: 1.0 と 1 を同じにする)。</summary>
        internal static string NumberText(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

        /// <summary>クエリの文字の値 ("..."。" と \ は \ を前に付ける)。</summary>
        internal static string StringLiteral(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>
        /// 列の種類に合わせたクエリの値。数値の列に数値でない文字を当てるなど、合う行が無いと決まる値は null。
        /// </summary>
        /// <param name="exactNumberText">
        /// 値を文字で突き合わせる所 (結合・集めた値との一致) で true。数値の列に当てる文字は、数値の文字 (NumberText) と同じ書き方のものだけ通す
        /// ("01" は 1 と一致しない)。条件の値 (比較) は false = 数値として読む。
        /// </param>
        internal static string? LiteralFor(KintoneColumnDefinition column, object value, bool exactNumberText = false)
        {
            switch (column.Kind)
            {
                case KintoneColumnKind.Number:
                    if (value is string text)
                    {
                        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
                        return exactNumberText && NumberText(number) != text ? null : NumberText(number);
                    }
                    if (value is bool b) return b ? "1" : "0";
                    if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal) return NumberText(ToDecimal(value));
                    return null;
                case KintoneColumnKind.Date:
                    return value switch
                    {
                        DateOnly date => StringLiteral(FormatDate(date)),
                        DateTime dateTime => StringLiteral(FormatDate(DateOnly.FromDateTime(dateTime))),
                        string s when DateOnly.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => StringLiteral(FormatDate(d)),
                        _ => null,
                    };
                case KintoneColumnKind.Time:
                    return value switch
                    {
                        TimeOnly time => StringLiteral(FormatTime(time)),
                        TimeSpan span => StringLiteral(FormatTime(TimeOnly.FromTimeSpan(span))),
                        DateTime dateTime => StringLiteral(FormatTime(TimeOnly.FromDateTime(dateTime))),
                        string s when TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) => StringLiteral(FormatTime(t)),
                        _ => null,
                    };
                case KintoneColumnKind.DateTime:
                    return value switch
                    {
                        DateTime dateTime => StringLiteral(FormatDateTime(dateTime)),
                        DateTimeOffset offset => StringLiteral(FormatDateTime(offset.UtcDateTime)),
                        DateOnly date => StringLiteral(FormatDateTime(date.ToDateTime(TimeOnly.MinValue))),
                        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) => StringLiteral(FormatDateTime(o.UtcDateTime)),
                        _ => null,
                    };
                default:
                    return StringLiteral(KeyText(value));
            }
        }

        /// <summary>突き合わせ・Id に使う値の文字 (数値は末尾の 0 を落とす: 1.0 と 1 を同じにする)。</summary>
        internal static string KeyText(object value)
        {
            switch (value)
            {
                case decimal d:
                    return NumberText(d);
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
                    return NumberText(ToDecimal(value));
                case DateOnly date:
                    return FormatDate(date);
                case TimeOnly time:
                    return FormatTime(time);
                case DateTime dateTime:
                    return FormatDateTime(dateTime);
                case DateTimeOffset offset:
                    return FormatDateTime(offset.UtcDateTime);
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        /// <summary>レコードを読む要求のクエリ (条件 order by ... limit N [offset M])。</summary>
        internal static string BuildQuery(string? filter, IReadOnlyCollection<string> orderBy, int limit, int offset)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(filter)) parts.Add(filter);
            if (orderBy.Count != 0) parts.Add("order by " + string.Join(", ", orderBy));
            parts.Add("limit " + limit.ToString(CultureInfo.InvariantCulture));
            if (offset != 0) parts.Add("offset " + offset.ToString(CultureInfo.InvariantCulture));
            return string.Join(" ", parts);
        }

        // ---------------------------------------------------------------- 読み出し

        /// <summary>from の表と結合。exists のサブクエリでは Parent に外側の select がある。</summary>
        internal class Scope
        {
            public Scope? Parent;
            public string Table = string.Empty;
            public string MainAlias = string.Empty;
            public KintoneTableDefinition Definition = new();
            public Dictionary<string, KintoneTableDefinition> Aliases = new(StringComparer.Ordinal);
            public List<KintoneJoin> Joins = new();

            public KintoneJoin Join(string alias) => Joins.First(e => e.Alias == alias);

            /// <summary>結合先の別名から主表までたどったときの、主表の結合元の列。</summary>
            public string RootSourceColumn(string alias)
            {
                var join = Join(alias);
                while (join.SourceAlias != MainAlias) join = Join(join.SourceAlias);
                return join.SourceColumn;
            }
        }

        /// <summary>集計の SQL か (select の項目に関数・case がある / group by / from の後ろがサブクエリ)。一覧の select count(*) は含まない。</summary>
        internal static bool IsAggregate(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            if (tokens.Count == 0 || !tokens[0].IsWord("select")) return false;
            var from = FindTopLevel(tokens, 0, t => t.IsWord("from"));
            if (from < 0) return false;
            if (from + 1 < tokens.Count && tokens[from + 1].IsSymbol("(")) return true;
            if (FindTopLevel(tokens, 0, t => t.IsWord("group")) >= 0) return true;
            //行を選ぶ定義 (WithinGroupsOf) の付いた集計の行数 (select count(*) from ... inner join (集計) 別名 on ...)
            var inner = FindTopLevel(tokens, from, t => t.IsWord("inner"));
            if (0 <= inner && inner + 2 < tokens.Count && tokens[inner + 1].IsWord("join") && tokens[inner + 2].IsSymbol("(")) return true;
            var items = tokens.GetRange(1, from - 1);
            var isPlainCount = items.Count == 4 && items[0].IsWord("count") && items[1].IsSymbol("(") && items[2].IsSymbol("*") && items[3].IsSymbol(")");
            return !isPlainCount && items.Any(e => e.IsSymbol("(") || e.IsWord("case"));
        }

        //括弧の外の最初の位置 (無ければ -1)
        static int FindTopLevel(List<SqlToken> tokens, int start, Func<SqlToken, bool> predicate)
        {
            var depth = 0;
            for (var i = start; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")")) depth--;
                else if (depth == 0 && predicate(tokens[i])) return i;
            }
            return -1;
        }

        /// <summary>
        /// 本体の select を読み出しの計画にする。受け付けるのは:
        /// select "t"."c" as "t_c",... / select count(*) / select * の from "t" [left join "u" as "u_1" on ...] [where ...] [order by ...] [limit N offset M]。
        /// 条件には exists (一覧をまたぐ条件) と結合先の列の比較を含められる。並びには結合先の列を含められる (メモリで並べる)。
        /// </summary>
        internal static async Task<KintoneRead> TranslateSelectAsync(string sql, IReadOnlyDictionary<string, object?> args, Func<string, Task<KintoneTableDefinition>> getDefinition)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            if (tokens.Count == 0 || !tokens[0].IsWord("select")) throw NotSupported(sql);
            if (tokens.Any(e => e.IsSymbol(";"))) throw NotSupported(sql);
            if (IsAggregate(sql)) throw NotSupported("This form of aggregation", sql);

            var (limit, offset) = ReadLimit(tokens, sql);

            //句の位置 (括弧の外だけ見る。exists のサブクエリは括弧の中)
            int from = -1, where = -1, orderBy = -1;
            var depth = 0;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")")) depth--;
                if (depth != 0) continue;
                if (tokens[i].IsWord("from") && from < 0) from = i;
                else if (tokens[i].IsWord("where") && where < 0) where = i;
                else if (tokens[i].IsWord("order") && i + 1 < tokens.Count && tokens[i + 1].IsWord("by") && orderBy < 0) orderBy = i;
                else if (tokens[i].IsWord("group") || tokens[i].IsWord("having") || tokens[i].IsWord("union")) throw NotSupported(sql);
            }
            if (from < 0 || (0 <= where && where < from) || (0 <= orderBy && (orderBy < from || orderBy < where))) throw NotSupported(sql);
            var fromEnd = 0 <= where ? where : 0 <= orderBy ? orderBy : tokens.Count;
            var whereEnd = 0 <= orderBy ? orderBy : tokens.Count;

            //from と結合
            var scope = await ParseFromAsync(tokens.GetRange(from + 1, fromEnd - from - 1), null, getDefinition, sql);
            var mainAlias = scope.MainAlias;
            var definition = scope.Definition;
            var joins = scope.Joins;

            //select の項目
            var selectItems = SplitByComma(tokens.GetRange(1, from - 1), sql);
            var isCount = selectItems.Count == 1 && selectItems[0].Count == 4 && selectItems[0][0].IsWord("count") && selectItems[0][1].IsSymbol("(") &&
                          selectItems[0][2].IsSymbol("*") && selectItems[0][3].IsSymbol(")");
            var isSelectAll = selectItems.Count == 1 && selectItems[0].Count == 1 && selectItems[0][0].IsSymbol("*");
            if ((isCount || isSelectAll) && limit != null) throw NotSupported(sql);
            if (isSelectAll && joins.Count != 0) throw NotSupported(sql);

            var read = new KintoneRead
            {
                Table = scope.Table,
                MainAlias = mainAlias,
                Definition = definition,
                IsCount = isCount,
                IsSelectAll = isSelectAll,
                Limit = limit,
                Offset = offset,
            };
            read.Joins.AddRange(joins);
            if (isSelectAll)
            {
                foreach (var column in definition.Columns) read.Columns.Add(new KintoneResultColumn { Key = column.Name, Alias = mainAlias, Column = column.Name });
            }
            else if (!isCount)
            {
                foreach (var item in selectItems)
                {
                    var q = 0;
                    var column = ReadColumn(item, ref q, scope, sql);
                    var key = column.Column;
                    if (q + 2 == item.Count && item[q].IsWord("as") && item[q + 1].IsIdentifier) key = item[q + 1].Text;
                    else if (q != item.Count) throw NotSupported(sql);
                    read.Columns.Add(new KintoneResultColumn { Key = key, Alias = column.Alias, Column = column.Column });
                }
                //主表の $select: 結果の列と、結合の元にする列
                foreach (var name in read.Columns.Where(e => e.Alias == mainAlias).Select(e => e.Column)
                    .Concat(joins.Where(e => e.SourceAlias == mainAlias).Select(e => e.SourceColumn)))
                {
                    if (!read.SelectColumns.Contains(name)) read.SelectColumns.Add(name);
                }
                foreach (var join in joins)
                {
                    foreach (var name in new[] { join.DestinationColumn }.Concat(read.Columns.Where(e => e.Alias == join.Alias).Select(e => e.Column))
                        .Concat(joins.Where(e => e.SourceAlias == join.Alias).Select(e => e.SourceColumn)))
                    {
                        join.AddSelectColumn(name);
                    }
                }
            }

            //条件
            if (0 <= where)
            {
                var whereTokens = tokens.GetRange(where + 1, whereEnd - where - 1);
                var node = Fold(await new FilterParser(whereTokens, scope, args, sql, getDefinition).ParseAsync());
                if (node is ConstNode constant)
                {
                    if (!constant.Value) read.IsEmpty = true;
                }
                else
                {
                    read.Where = node;
                    if (!HasInSet(node)) read.Filter = Render(node, true);
                }
            }

            //並び
            if (0 <= orderBy)
            {
                var orderTokens = tokens.GetRange(orderBy + 2, tokens.Count - orderBy - 2);
                foreach (var item in SplitByComma(orderTokens, sql))
                {
                    var q = 0;
                    var column = ReadColumn(item, ref q, scope, sql);
                    var descending = false;
                    if (q < item.Count && (item[q].IsWord("desc") || item[q].IsWord("asc")))
                    {
                        descending = item[q].IsWord("desc");
                        q++;
                    }
                    if (q != item.Count) throw NotSupported(sql);
                    read.Order.Add(new KintoneOrder(column.Alias, column.Column, descending));
                    if (column.Alias == mainAlias)
                    {
                        read.OrderBy.Add(Name(column.Column) + (descending ? " desc" : " asc"));
                    }
                    else
                    {
                        //結合先の列の並び ($expand 先の並びは使わない)。主表を読んで結合してからメモリで並べるので、その列も読む
                        if (isCount) throw NotSupported(sql);
                        read.Joins.First(e => e.Alias == column.Alias).AddSelectColumn(column.Column);
                    }
                }
            }
            //ページングつきは順序が決まるよう、並びの最後にキーの列を足す
            if (limit != null)
            {
                foreach (var key in definition.KeyColumns)
                {
                    var name = Name(key);
                    if (!read.OrderBy.Any(e => e == name + " asc" || e == name + " desc")) read.OrderBy.Add(name + " asc");
                }
            }
            return read;
        }

        //末尾の limit N offset M (無ければ null, 0)。読んだ分を tokens から外す
        static (int? Limit, int Offset) ReadLimit(List<SqlToken> tokens, string sql)
        {
            if (tokens.Count < 4) return (null, 0);
            var t = tokens.GetRange(tokens.Count - 4, 4);
            if (!(t[0].IsWord("limit") && t[1].Kind == SqlTokenKind.Number && t[2].IsWord("offset") && t[3].Kind == SqlTokenKind.Number)) return (null, 0);
            if (!int.TryParse(t[1].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ||
                !int.TryParse(t[3].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var o)) throw NotSupported(sql);
            tokens.RemoveRange(tokens.Count - 4, 4);
            return (l, o);
        }

        //from "t" [as "a"] [left join "u" as "u_1" on "a"."x"="u_1"."y" ...]
        static async Task<Scope> ParseFromAsync(List<SqlToken> fromTokens, Scope? parent, Func<string, Task<KintoneTableDefinition>> getDefinition, string sql)
        {
            var scope = new Scope { Parent = parent };
            var p = 0;
            var table = ReadTableName(fromTokens, ref p, sql);
            var mainAlias = table;
            if (p < fromTokens.Count && fromTokens[p].IsWord("as")) p++;
            if (p < fromTokens.Count && fromTokens[p].IsIdentifier && !fromTokens[p].IsWord("left")) mainAlias = fromTokens[p++].Text;
            scope.Table = table;
            scope.MainAlias = mainAlias;
            scope.Definition = await getDefinition(table);
            scope.Aliases[mainAlias] = scope.Definition;

            while (p < fromTokens.Count)
            {
                if (!(fromTokens[p].IsWord("left") && p + 1 < fromTokens.Count && fromTokens[p + 1].IsWord("join"))) throw NotSupported(sql);
                p += 2;
                var joinTable = ReadTableName(fromTokens, ref p, sql);
                if (p < fromTokens.Count && fromTokens[p].IsWord("as")) p++;
                if (fromTokens.Count <= p || !fromTokens[p].IsIdentifier || fromTokens[p].IsWord("on")) throw NotSupported(sql);
                var joinAlias = fromTokens[p++].Text;
                if (scope.Aliases.ContainsKey(joinAlias)) throw NotSupported(sql);
                if (fromTokens.Count <= p || !fromTokens[p++].IsWord("on")) throw NotSupported(sql);
                var left = ReadJoinOperand(fromTokens, ref p, sql);
                if (fromTokens.Count <= p || !fromTokens[p++].IsSymbol("=")) throw NotSupported(sql);
                var right = ReadJoinOperand(fromTokens, ref p, sql);
                //片方が新しい別名 (結合先)、もう片方が先に出てきた別名 (結合元)
                var (source, destination) = left.Alias == joinAlias ? (right, left) : (left, right);
                if (destination.Alias != joinAlias || !scope.Aliases.ContainsKey(source.Alias)) throw NotSupported(sql);
                var joinDefinition = await getDefinition(joinTable);
                CheckColumn(scope.Aliases[source.Alias], source.Column);
                CheckColumn(joinDefinition, destination.Column);
                scope.Aliases[joinAlias] = joinDefinition;
                scope.Joins.Add(new KintoneJoin
                {
                    Table = joinTable,
                    Alias = joinAlias,
                    SourceAlias = source.Alias,
                    SourceColumn = source.Column,
                    DestinationColumn = destination.Column,
                    Definition = joinDefinition,
                });
            }
            return scope;
        }

        static string ReadTableName(List<SqlToken> tokens, ref int p, string sql)
        {
            if (tokens.Count <= p || !tokens[p].IsIdentifier) throw NotSupported(sql);
            var name = tokens[p++].Text;
            //スキーマつき ("s"."t") は扱わない (Kintone の表名に . は無いものとして定義から外している)
            if (p < tokens.Count && tokens[p].IsSymbol(".")) throw NotSupported(sql);
            return name;
        }

        //on の片側: "別名"."列" か CAST("別名"."列" AS VARCHAR(255)) (本体が数値と文字の結合に付ける)
        static (string Alias, string Column) ReadJoinOperand(List<SqlToken> tokens, ref int p, string sql)
        {
            if (p < tokens.Count && tokens[p].IsWord("cast"))
            {
                p++;
                if (tokens.Count <= p || !tokens[p++].IsSymbol("(")) throw NotSupported(sql);
                var inner = ReadQualified(tokens, ref p, sql);
                if (tokens.Count <= p + 1 || !tokens[p].IsWord("as") || !tokens[p + 1].IsWord("varchar")) throw NotSupported(sql);
                p += 2;
                if (p < tokens.Count && tokens[p].IsSymbol("("))
                {
                    if (tokens.Count <= p + 2 || tokens[p + 1].Kind != SqlTokenKind.Number || !tokens[p + 2].IsSymbol(")")) throw NotSupported(sql);
                    p += 3;
                }
                if (tokens.Count <= p || !tokens[p++].IsSymbol(")")) throw NotSupported(sql);
                return inner;
            }
            return ReadQualified(tokens, ref p, sql);
        }

        static (string Alias, string Column) ReadQualified(List<SqlToken> tokens, ref int p, string sql)
        {
            if (tokens.Count <= p + 2 || !tokens[p].IsIdentifier || !tokens[p + 1].IsSymbol(".") || !tokens[p + 2].IsIdentifier) throw NotSupported(sql);
            var result = (tokens[p].Text, tokens[p + 2].Text);
            p += 3;
            return result;
        }

        /// <summary>列の参照: "別名"."列" か "列" (主表)。別名はこの select から外側へ探す (exists の中から外側の表を指す相関)。</summary>
        static (Scope Owner, string Alias, string Column) ReadColumnReference(List<SqlToken> tokens, ref int p, Scope scope, string sql)
        {
            if (tokens.Count <= p || !tokens[p].IsIdentifier || IsKeyword(tokens[p])) throw NotSupported(sql);
            string alias, column;
            Scope? owner = scope;
            if (p + 2 < tokens.Count && tokens[p + 1].IsSymbol("."))
            {
                if (!tokens[p + 2].IsIdentifier) throw NotSupported(sql);
                alias = tokens[p].Text;
                column = tokens[p + 2].Text;
                p += 3;
                while (owner != null && !owner.Aliases.ContainsKey(alias)) owner = owner.Parent;
                if (owner == null) throw NotSupported(sql);
            }
            else
            {
                alias = scope.MainAlias;
                column = tokens[p].Text;
                p++;
            }
            CheckColumn(owner.Aliases[alias], column);
            return (owner, alias, column);
        }

        //列の参照 (この select の表だけ)
        static (string Alias, string Column) ReadColumn(List<SqlToken> tokens, ref int p, Scope scope, string sql)
        {
            var (owner, alias, column) = ReadColumnReference(tokens, ref p, scope, sql);
            if (owner != scope) throw NotSupported(sql);
            return (alias, column);
        }

        static void CheckColumn(KintoneTableDefinition definition, string column)
        {
            if (definition.FindColumn(column) == null) throw new KintoneException($"kintone app '{definition.Name}' has no field '{column}' (or the field type cannot be used as a column).");
        }

        static bool IsKeyword(SqlToken token)
            => token.Kind == SqlTokenKind.Word &&
               (token.IsWord("select") || token.IsWord("from") || token.IsWord("where") || token.IsWord("and") || token.IsWord("or") || token.IsWord("not") ||
                token.IsWord("is") || token.IsWord("null") || token.IsWord("like") || token.IsWord("in") || token.IsWord("exists") || token.IsWord("as") ||
                token.IsWord("cast") || token.IsWord("count") || token.IsWord("case"));

        static List<List<SqlToken>> SplitByComma(List<SqlToken> tokens, string sql)
        {
            var items = new List<List<SqlToken>>();
            var item = new List<SqlToken>();
            var depth = 0;
            foreach (var token in tokens)
            {
                if (token.IsSymbol("(")) depth++;
                else if (token.IsSymbol(")")) depth--;
                if (depth == 0 && token.IsSymbol(","))
                {
                    if (item.Count == 0) throw NotSupported(sql);
                    items.Add(item);
                    item = new();
                    continue;
                }
                item.Add(token);
            }
            if (item.Count == 0) throw NotSupported(sql);
            items.Add(item);
            return items;
        }

        // ---------------------------------------------------------------- 条件

        /// <summary>
        /// 結合先の列の比較を、主表の条件 (真になる行・偽になる行) に持ち上げる。
        /// 結合ごとに「結合先の表を条件で読んで結合先の列の値を集め、結合元の列がそのどれかに一致」に置き換える (多段は主表までくり返す)。
        /// missing は結合先の行が無いとき (結合元が null・相手が無い) の比較の値: true (is null) / false (is not null) / null (unknown)。
        /// </summary>
        static FilterNode LiftJoined(Scope scope, string alias, FilterNode trueFilter, FilterNode falseFilter, bool? missing)
        {
            while (alias != scope.MainAlias)
            {
                var join = scope.Join(alias);
                var trueKeys = new InSetNode(join.SourceColumn, new KeySource { Table = join.Table, Definition = join.Definition, Filter = trueFilter, Column = join.DestinationColumn });
                var falseKeys = new InSetNode(join.SourceColumn, new KeySource { Table = join.Table, Definition = join.Definition, Filter = falseFilter, Column = join.DestinationColumn });
                //結合先の行が無い行は missing の値になる (not (一致) は結合元が空の行も真。InSetNode)
                trueFilter = missing == true ? new NotNode(falseKeys) : trueKeys;
                falseFilter = missing == false ? new NotNode(trueKeys) : falseKeys;
                alias = join.SourceAlias;
            }
            return new TriNode(trueFilter, falseFilter);
        }

        class FilterParser
        {
            readonly List<SqlToken> _tokens;
            readonly Scope _scope;
            readonly IReadOnlyDictionary<string, object?> _args;
            readonly string _sql;
            readonly Func<string, Task<KintoneTableDefinition>> _getDefinition;
            int _p;

            public FilterParser(List<SqlToken> tokens, Scope scope, IReadOnlyDictionary<string, object?> args, string sql, Func<string, Task<KintoneTableDefinition>> getDefinition)
            {
                _tokens = tokens;
                _scope = scope;
                _args = args;
                _sql = sql;
                _getDefinition = getDefinition;
            }

            public async Task<FilterNode> ParseAsync()
            {
                var node = await ParseOrAsync();
                if (_p != _tokens.Count) throw NotSupported(_sql);
                return node;
            }

            SqlToken? Peek(int offset = 0) => _p + offset < _tokens.Count ? _tokens[_p + offset] : null;

            SqlToken Next()
            {
                if (_tokens.Count <= _p) throw NotSupported(_sql);
                return _tokens[_p++];
            }

            async Task<FilterNode> ParseOrAsync()
            {
                var children = new List<FilterNode> { await ParseAndAsync() };
                while (Peek()?.IsWord("or") == true)
                {
                    _p++;
                    children.Add(await ParseAndAsync());
                }
                return children.Count == 1 ? children[0] : new OrNode(children);
            }

            async Task<FilterNode> ParseAndAsync()
            {
                var children = new List<FilterNode> { await ParseUnaryAsync() };
                while (Peek()?.IsWord("and") == true)
                {
                    _p++;
                    children.Add(await ParseUnaryAsync());
                }
                return children.Count == 1 ? children[0] : new AndNode(children);
            }

            async Task<FilterNode> ParseUnaryAsync()
            {
                if (Peek()?.IsWord("not") == true)
                {
                    _p++;
                    return new NotNode(await ParseUnaryAsync());
                }
                return await ParsePrimaryAsync();
            }

            async Task<FilterNode> ParsePrimaryAsync()
            {
                var token = Peek() ?? throw NotSupported(_sql);
                if (token.IsSymbol("("))
                {
                    _p++;
                    var inner = await ParseOrAsync();
                    if (!Next().IsSymbol(")")) throw NotSupported(_sql);
                    return inner;
                }
                //1=1 / 1=0 (本体が「絞らない」「何も返さない」に使う)
                if (token.Kind == SqlTokenKind.Number)
                {
                    var left = Next().Text;
                    if (!Next().IsSymbol("=")) throw NotSupported(_sql);
                    var right = Next();
                    if (right.Kind != SqlTokenKind.Number ||
                        !decimal.TryParse(left, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var l) ||
                        !decimal.TryParse(right.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r)) throw NotSupported(_sql);
                    return new ConstNode(l == r);
                }
                //一覧をまたぐ条件 exists(select 1 from 子 ... where 子.列 = 親.列 and ...)
                if (token.IsWord("exists") && Peek(1)?.IsSymbol("(") == true) return await ParseExistsAsync();

                var (owner, alias, column) = ReadColumnReference(_tokens, ref _p, _scope, _sql);
                var op = Next();

                //列どうしの比較: 一覧をまたぐ条件の相関 (子の主表の列 = 外側の主表の列) だけ
                if (Peek()?.IsIdentifier == true && !IsKeyword(Peek()!) && op.Kind == SqlTokenKind.Symbol)
                {
                    var right = ReadColumnReference(_tokens, ref _p, _scope, _sql);
                    return Correlation((owner, alias, column), op, right);
                }
                if (owner != _scope) throw NotSupported("A condition that refers to the outer table other than the link of a list", _sql);

                var definition = owner.Aliases[alias].FindColumn(column)!;
                var joined = alias != _scope.MainAlias;
                var notNull = CreateTerm(definition, TermOperator.IsNotNull, null);

                if (op.IsWord("is"))
                {
                    var not = false;
                    if (Peek()?.IsWord("not") == true) { _p++; not = true; }
                    if (!Next().IsWord("null")) throw NotSupported(_sql);
                    var isNull = CreateTerm(definition, TermOperator.IsNull, null);
                    if (!joined) return not ? notNull : isNull;
                    //結合先の行が無ければ結合先の列は null
                    return not ? LiftJoined(_scope, alias, notNull, isNull, false) : LiftJoined(_scope, alias, isNull, notNull, true);
                }
                if (op.IsWord("like"))
                {
                    var pattern = Value(Next());
                    if (!Next().IsWord("escape")) throw NotSupported(_sql);
                    var escape = Value(Next());
                    var term = CreateTerm(definition, TermOperator.Like, UnescapeLikePattern(pattern, escape));
                    return joined ? LiftJoined(_scope, alias, term, And(notNull, new NotNode(term)), null) : term;
                }
                if (op.IsWord("in") || (op.IsWord("not") && Peek()?.IsWord("in") == true))
                {
                    var isNot = op.IsWord("not");
                    if (isNot) _p++;
                    if (!Next().IsSymbol("(")) throw NotSupported(_sql);
                    var values = new List<FilterNode>();
                    while (true)
                    {
                        //null の要素との比較は SQL では unknown (In では落ち、Not In は全体が真にならない)
                        var value = Value(Next());
                        if (IsNull(value))
                        {
                            values.Add(new UnknownNode());
                        }
                        else
                        {
                            var term = CreateTerm(definition, TermOperator.Equal, value);
                            values.Add(joined ? LiftJoined(_scope, alias, term, And(notNull, new NotNode(term)), null) : term);
                        }
                        var separator = Next();
                        if (separator.IsSymbol(")")) break;
                        if (!separator.IsSymbol(",")) throw NotSupported(_sql);
                    }
                    FilterNode node = values.Count == 1 ? values[0] : new OrNode(values);
                    return isNot ? new NotNode(node) : node;
                }
                var termOperator = ToTermOperator(op);
                if (termOperator == null) throw NotSupported(_sql);
                //パラメータが null の比較 (= / <> も含む) は SQL では unknown (is null / is not null は上で別に扱う)
                var rightValue = Value(Next());
                if (IsNull(rightValue)) return new UnknownNode();
                var compare = CreateTerm(definition, termOperator.Value, rightValue);
                if (!joined) return compare;
                return LiftJoined(_scope, alias, compare, And(notNull, new NotNode(compare)), null);
            }

            static FilterNode And(FilterNode a, FilterNode b) => new AndNode(new List<FilterNode> { a, b });

            static TermOperator? ToTermOperator(SqlToken op) => op.Kind == SqlTokenKind.Symbol ? op.Text switch
            {
                "=" => TermOperator.Equal,
                "<>" or "!=" => TermOperator.NotEqual,
                "<" => TermOperator.Less,
                "<=" => TermOperator.LessOrEqual,
                ">" => TermOperator.Greater,
                ">=" => TermOperator.GreaterOrEqual,
                _ => null,
            } : null;

            //子の主表の列 = 外側 (1 つ外の select) の主表の列 だけを相関として受け付ける
            FilterNode Correlation((Scope Owner, string Alias, string Column) left, SqlToken op, (Scope Owner, string Alias, string Column) right)
            {
                if (left.Owner == right.Owner) throw NotSupported("Comparing two columns", _sql);
                var (inner, outer) = left.Owner == _scope ? (left, right) : (right, left);
                if (inner.Owner != _scope || outer.Owner != _scope.Parent || !op.IsSymbol("="))
                    throw NotSupported("A condition across a list other than 'child column = parent column'", _sql);
                if (inner.Alias != _scope.MainAlias || outer.Alias != _scope.Parent!.MainAlias)
                    throw NotSupported("A condition across a list that is linked through a joined table", _sql);
                return new CorrelationNode(inner.Column, outer.Column);
            }

            //exists(select 1 from 子 [left join ...] where (子.列 = 親.列) and (...)) → 子の表を条件で読んで子の列の値を集め、親の列がそのどれかに一致
            async Task<FilterNode> ParseExistsAsync()
            {
                _p++;
                if (!Next().IsSymbol("(")) throw NotSupported(_sql);
                var start = _p;
                var depth = 1;
                while (true)
                {
                    var t = Next();
                    if (t.IsSymbol("(")) depth++;
                    else if (t.IsSymbol(")") && --depth == 0) break;
                }
                var inner = _tokens.GetRange(start, _p - 1 - start);
                if (inner.Count < 4 || !inner[0].IsWord("select") || inner[1].Kind != SqlTokenKind.Number || inner[1].Text != "1" || !inner[2].IsWord("from"))
                    throw NotSupported("This form of a condition across a list (exists)", _sql);
                var where = FindTopLevel(inner, 3, e => e.IsWord("where"));
                if (where < 0) throw NotSupported("A condition across a list without a link to the parent", _sql);
                if (FindTopLevel(inner, 3, e => e.IsWord("order") || e.IsWord("group") || e.IsWord("having") || e.IsWord("limit") || e.IsWord("union")) >= 0)
                    throw NotSupported("This form of a condition across a list (exists)", _sql);

                var child = await ParseFromAsync(inner.GetRange(3, where - 3), _scope, _getDefinition, _sql);
                var tree = await new FilterParser(inner.GetRange(where + 1, inner.Count - where - 1), child, _args, _sql, _getDefinition).ParseAsync();
                CorrelationNode? correlation = null;
                tree = ExtractCorrelation(tree, ref correlation);
                if (correlation == null) throw NotSupported("A condition across a list without a link to the parent", _sql);

                var keys = new InSetNode(correlation.OuterColumn, new KeySource { Table = child.Table, Definition = child.Definition, Filter = tree, Column = correlation.InnerColumn });
                //exists は真か偽 (unknown にならない)。親の列が null なら偽 (not exists は真。InSetNode の not)
                return new TriNode(keys, new NotNode(keys));
            }

            //相関は and でつながる位置に 1 つだけ (or / not の中は拒否)。取り除いて「真」にする
            FilterNode ExtractCorrelation(FilterNode node, ref CorrelationNode? found)
            {
                switch (node)
                {
                    case CorrelationNode correlation:
                        if (found != null) throw NotSupported("A condition across a list with more than one link column", _sql);
                        found = correlation;
                        return new ConstNode(true);
                    case AndNode and:
                        {
                            var children = new List<FilterNode>();
                            foreach (var child in and.Children) children.Add(ExtractCorrelation(child, ref found));
                            return new AndNode(children);
                        }
                    default:
                        if (HasCorrelation(node)) throw NotSupported("A condition across a list whose link is inside or / not", _sql);
                        return node;
                }
            }

            static bool HasCorrelation(FilterNode node) => node switch
            {
                CorrelationNode => true,
                NotNode not => HasCorrelation(not.Child),
                AndNode and => and.Children.Any(HasCorrelation),
                OrNode or => or.Children.Any(HasCorrelation),
                _ => false,
            };

            static bool IsNull(object? value) => value == null || value is DBNull;

            object? Value(SqlToken token)
            {
                if (token.Kind != SqlTokenKind.Parameter) throw NotSupported(_sql);
                if (!_args.TryGetValue(token.Text, out var value)) throw new KintoneException($"The value of parameter '{token.Text}' is missing.");
                return value;
            }

            //本体の部分一致のパターンは必ず %文字% で、文字の中の % _ とエスケープ文字はエスケープ文字が前に付いている
            string UnescapeLikePattern(object? pattern, object? escape)
            {
                var text = pattern as string;
                var escapeText = Convert.ToString(escape, CultureInfo.InvariantCulture) ?? string.Empty;
                if (text == null || text.Length < 2 || text[0] != '%' || text[^1] != '%' || escapeText.Length != 1)
                    throw NotSupported("A like pattern other than %text%", _sql);
                var escapeChar = escapeText[0];
                var sb = new StringBuilder();
                var body = text[1..^1];
                for (var i = 0; i < body.Length; i++)
                {
                    var c = body[i];
                    if (c == escapeChar)
                    {
                        if (body.Length <= i + 1) throw NotSupported("A like pattern other than %text%", _sql);
                        sb.Append(body[++i]);
                        continue;
                    }
                    if (c == '%' || c == '_') throw NotSupported("A like pattern other than %text%", _sql);
                    sb.Append(c);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 定数を畳む (and / or / not の中の 1=1 / 1=0 を消す)。positive = 式が真になる条件、false = 式が偽になる条件。
        /// unknown (値が null の比較) はどちらにもならない (SQL の 3 値論理: not (x &lt; null) も 0 行)。
        /// 真の条件と偽の条件を別に持つ比較 (TriNode) は向きに合う方を使う。それらを含まない部分は not をそのまま残す。
        /// </summary>
        internal static FilterNode Fold(FilterNode node, bool positive = true)
        {
            if (!positive && !HasUnknown(node))
            {
                var folded = Fold(node, true);
                return folded is ConstNode c ? new ConstNode(!c.Value) : new NotNode(folded);
            }
            switch (node)
            {
                case UnknownNode:
                    return new ConstNode(false);
                case TriNode tri:
                    return Fold(positive ? tri.True : tri.False, true);
                case NotNode not:
                    return Fold(not.Child, !positive);
                case AndNode and:
                    //偽になる条件は「どれかが偽」
                    return positive ? FoldAnd(and.Children.Select(e => Fold(e, true))) : FoldOr(and.Children.Select(e => Fold(e, false)));
                case OrNode or:
                    return positive ? FoldOr(or.Children.Select(e => Fold(e, true))) : FoldAnd(or.Children.Select(e => Fold(e, false)));
                default:
                    return node;
            }
        }

        static bool HasUnknown(FilterNode node) => node switch
        {
            UnknownNode => true,
            TriNode => true,
            NotNode not => HasUnknown(not.Child),
            AndNode and => and.Children.Any(HasUnknown),
            OrNode or => or.Children.Any(HasUnknown),
            _ => false,
        };

        /// <summary>別の表を読んで値を集める条件を含むか。</summary>
        internal static bool HasInSet(FilterNode node) => node switch
        {
            InSetNode => true,
            TriNode tri => HasInSet(tri.True) || HasInSet(tri.False),
            NotNode not => HasInSet(not.Child),
            AndNode and => and.Children.Any(HasInSet),
            OrNode or => or.Children.Any(HasInSet),
            _ => false,
        };

        static FilterNode FoldAnd(IEnumerable<FilterNode> folded)
        {
            var children = new List<FilterNode>();
            foreach (var child in folded)
            {
                if (child is ConstNode c)
                {
                    if (!c.Value) return new ConstNode(false);
                    continue;
                }
                children.Add(child);
            }
            return children.Count == 0 ? new ConstNode(true) : children.Count == 1 ? children[0] : new AndNode(children);
        }

        static FilterNode FoldOr(IEnumerable<FilterNode> folded)
        {
            var children = new List<FilterNode>();
            foreach (var child in folded)
            {
                if (child is ConstNode c)
                {
                    if (c.Value) return new ConstNode(true);
                    continue;
                }
                children.Add(child);
            }
            return children.Count == 0 ? new ConstNode(false) : children.Count == 1 ? children[0] : new OrNode(children);
        }

        /// <summary>集めた値との一致 (<see cref="ValuesNode"/>) を含むか。</summary>
        internal static bool HasValues(FilterNode node) => node switch
        {
            ValuesNode => true,
            NotNode not => HasValues(not.Child),
            AndNode and => and.Children.Any(HasValues),
            OrNode or => or.Children.Any(HasValues),
            _ => false,
        };

        /// <summary>
        /// 1 つの比較を作る。空になりえない列 ($id・レコード番号・作成日時など) の「空か」は定数にする。
        /// 数値の列に数値でない値を当てた比較など、合う行が無いと決まるものも定数にする。
        /// </summary>
        internal static FilterNode CreateTerm(KintoneColumnDefinition column, TermOperator op, object? value)
        {
            if (op == TermOperator.IsNull) return column.IsNeverEmpty ? new ConstNode(false) : new TermNode(column, op, null);
            if (op == TermOperator.IsNotNull) return column.IsNeverEmpty ? new ConstNode(true) : new TermNode(column, op, null);
            if (op is TermOperator.Like or TermOperator.NotLike) return new TermNode(column, op, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            if (LiteralFor(column, value!) == null)
            {
                //列の種類で書けない値: 等しい行は無い。等しくない行は空でない行の全部。大小は比べられない
                return op switch
                {
                    TermOperator.Equal => new ConstNode(false),
                    TermOperator.NotEqual => CreateTerm(column, TermOperator.IsNotNull, null),
                    _ => throw new KintoneException($"The value '{KeyText(value!)}' cannot be compared with kintone field '{column.Name}'.", KintoneErrorKind.InvalidRequest),
                };
            }
            return new TermNode(column, op, value);
        }

        static TermNode Negate(TermNode term) => term with
        {
            Operator = term.Operator switch
            {
                TermOperator.Equal => TermOperator.NotEqual,
                TermOperator.NotEqual => TermOperator.Equal,
                TermOperator.Less => TermOperator.GreaterOrEqual,
                TermOperator.LessOrEqual => TermOperator.Greater,
                TermOperator.Greater => TermOperator.LessOrEqual,
                TermOperator.GreaterOrEqual => TermOperator.Less,
                TermOperator.IsNull => TermOperator.IsNotNull,
                TermOperator.IsNotNull => TermOperator.IsNull,
                TermOperator.Like => TermOperator.NotLike,
                _ => TermOperator.Like,
            },
        };

        /// <summary>
        /// not を比較まで押し下げる (ド・モルガン。SQL の 3 値の論理でも同じ意味)。kintone のクエリには not (...) が無いので、
        /// 比較は逆の演算子にし (SQL の意味のまま: 逆の比較も列が空の行では真にならない)、集めた値との一致は「not (一致)」のまま残す。
        /// 結果の not は「not (集めた値との一致)」だけ。
        /// </summary>
        internal static FilterNode PushNot(FilterNode node, bool negate = false)
        {
            switch (node)
            {
                case ConstNode constant:
                    return negate ? new ConstNode(!constant.Value) : node;
                case TermNode term:
                    return negate ? Negate(term) : node;
                case ValuesNode:
                    return negate ? new NotNode(node) : node;
                case NotNode not:
                    return PushNot(not.Child, !negate);
                case AndNode and:
                    {
                        var children = and.Children.Select(e => PushNot(e, negate)).ToList();
                        return negate ? new OrNode(children) : new AndNode(children);
                    }
                case OrNode or:
                    {
                        var children = or.Children.Select(e => PushNot(e, negate)).ToList();
                        return negate ? new AndNode(children) : new OrNode(children);
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// 畳んだ条件 → kintone のクエリの条件。値を集める前の条件 (InSetNode) は書けない。
        /// 集めた値との一致 (<see cref="ValuesNode"/>) は真か偽 (列が空なら偽 = 一致しない) で、その not は列が空の行も真にする。
        /// kintone の != / not in / not like が空の行をどう扱うかに頼らないよう、空の行を含めるか除くかを毎回明示して書く。
        /// </summary>
        internal static string Render(FilterNode node, bool isRoot)
        {
            switch (node)
            {
                case NotNode { Child: ValuesNode values }:
                    return RenderNotValues(values);
                case NotNode not:
                    return Render(PushNot(not), isRoot);
                case AndNode and:
                    {
                        var text = string.Join(" and ", and.Children.Select(e => Render(e, false)));
                        return isRoot ? text : "(" + text + ")";
                    }
                case OrNode or:
                    {
                        var text = string.Join(" or ", or.Children.Select(e => Render(e, false)));
                        return isRoot ? text : "(" + text + ")";
                    }
                case TermNode term:
                    return RenderTerm(term);
                case ValuesNode values:
                    return RenderValues(values);
                default:
                    throw new InvalidOperationException();
            }
        }

        static KintoneException OperatorNotSupported(KintoneColumnDefinition column, string what)
            => new($"kintone field '{column.Name}' ({column.FieldType}) cannot be used in a condition of '{what}' (the kintone query has no operator for it).", KintoneErrorKind.NotSupported);

        //列が空か / 空でないか
        static string RenderEmpty(KintoneColumnDefinition column, bool isEmpty)
        {
            var name = Name(column.Name);
            return column.QueryKind switch
            {
                KintoneQueryKind.Text or KintoneQueryKind.Number or KintoneQueryKind.DateTime => $"{name} {(isEmpty ? "=" : "!=")} \"\"",
                KintoneQueryKind.LongText => $"{name} {(isEmpty ? "is empty" : "is not empty")}",
                KintoneQueryKind.Selection => $"{name} {(isEmpty ? "in" : "not in")} (\"\")",
                _ => throw OperatorNotSupported(column, isEmpty ? "is empty" : "is not empty"),
            };
        }

        static string RenderTerm(TermNode term)
        {
            var column = term.Column;
            var name = Name(column.Name);
            var kind = column.QueryKind;
            var canBeEmpty = !column.IsNeverEmpty;
            switch (term.Operator)
            {
                case TermOperator.IsNull:
                    return RenderEmpty(column, true);
                case TermOperator.IsNotNull:
                    return RenderEmpty(column, false);
                case TermOperator.Like:
                    if (kind is not (KintoneQueryKind.Text or KintoneQueryKind.LongText or KintoneQueryKind.RichText)) throw OperatorNotSupported(column, "contains");
                    return $"{name} like {StringLiteral((string)term.Value!)}";
                case TermOperator.NotLike:
                    //空の行では真にならない (SQL の not like)。リッチエディターは空かどうかを書けない
                    if (kind is not (KintoneQueryKind.Text or KintoneQueryKind.LongText)) throw OperatorNotSupported(column, "does not contain");
                    return $"({name} not like {StringLiteral((string)term.Value!)} and {RenderEmpty(column, false)})";
            }

            var literal = LiteralFor(column, term.Value!) ?? throw new InvalidOperationException();
            switch (term.Operator)
            {
                case TermOperator.Equal:
                    if (kind == KintoneQueryKind.Selection) return $"{name} in ({literal})";
                    if (kind is KintoneQueryKind.Text or KintoneQueryKind.Number or KintoneQueryKind.DateTime) return $"{name} = {literal}";
                    throw OperatorNotSupported(column, "=");
                case TermOperator.NotEqual:
                    //空の行では真にならない (SQL の <>)
                    if (kind == KintoneQueryKind.Selection) return canBeEmpty ? $"{name} not in ({literal}, \"\")" : $"{name} not in ({literal})";
                    if (kind is KintoneQueryKind.Text or KintoneQueryKind.Number or KintoneQueryKind.DateTime)
                        return canBeEmpty ? $"({name} != {literal} and {RenderEmpty(column, false)})" : $"{name} != {literal}";
                    throw OperatorNotSupported(column, "<>");
                default:
                    {
                        if (kind is not (KintoneQueryKind.Number or KintoneQueryKind.DateTime)) throw OperatorNotSupported(column, "< / <= / > / >=");
                        var op = term.Operator switch
                        {
                            TermOperator.Less => "<",
                            TermOperator.LessOrEqual => "<=",
                            TermOperator.Greater => ">",
                            _ => ">=",
                        };
                        //空の行では真にならない (SQL の大小の比較)
                        return canBeEmpty ? $"({name} {op} {literal} and {RenderEmpty(column, false)})" : $"{name} {op} {literal}";
                    }
            }
        }

        //集めた値のどれかに一致 (列が空の行は一致しない)
        static string RenderValues(ValuesNode values)
        {
            var column = values.Definition;
            var name = Name(column.Name);
            var literals = values.Values.Select(e => e.Literal).ToList();
            switch (column.QueryKind)
            {
                case KintoneQueryKind.Text:
                case KintoneQueryKind.Number:
                case KintoneQueryKind.Selection:
                    return $"{name} in ({string.Join(", ", literals)})";
                case KintoneQueryKind.DateTime:
                    return literals.Count == 1 ? $"{name} = {literals[0]}" : "(" + string.Join(" or ", literals.Select(e => $"{name} = {e}")) + ")";
                default:
                    throw OperatorNotSupported(column, "in");
            }
        }

        //集めた値のどれにも一致しない (列が空の行も真)
        static string RenderNotValues(ValuesNode values)
        {
            var column = values.Definition;
            var name = Name(column.Name);
            var literals = values.Values.Select(e => e.Literal).ToList();
            string text;
            switch (column.QueryKind)
            {
                case KintoneQueryKind.Text:
                case KintoneQueryKind.Number:
                case KintoneQueryKind.Selection:
                    text = $"{name} not in ({string.Join(", ", literals)})";
                    break;
                case KintoneQueryKind.DateTime:
                    text = literals.Count == 1 ? $"{name} != {literals[0]}" : "(" + string.Join(" and ", literals.Select(e => $"{name} != {e}")) + ")";
                    break;
                default:
                    throw OperatorNotSupported(column, "not in");
            }
            return column.IsNeverEmpty ? text : $"({RenderEmpty(column, true)} or {text})";
        }

        // ---------------------------------------------------------------- 集計

        /// <summary>
        /// 本体の集計の SQL (AggregateSqlCreator) を集計の計画にする。受け付けるのは:
        /// select 軸 as "g0", case when 軸 is null then 1 else 0 end as "z0", [case 軸 when '..' then 0 ... else n end as "r0",] [min(結合先の列) as "d0",]
        /// count(*) / count(distinct 列) / sum / avg / min / max(列) as "m0", ..., count(*) as "n" from ... [where ...] [group by ...] [having ...] [order by 別名 ...] [limit N offset M]
        /// と、グループの数を数える select count(*) from (select 1 as "x" from ... group by ... having ...) x。
        /// 行を選ぶ定義 (WithinGroupsOf) は、結合の最後の inner join (同じ表の集計) 別名 on (列 = 別名."gN" or (列 is null and 別名."gN" is null)) and ... [on 1=1]。
        /// その集計の行数を数える select count(*) from ... inner join (...) ... where ... も受け付ける (別名なしの count(*) 1 つ)。
        /// 日付の丸め (date_trunc など) は拒否する。
        /// </summary>
        internal static async Task<KintoneAggregate> TranslateAggregateAsync(string sql, IReadOnlyDictionary<string, object?> args, Func<string, Task<KintoneTableDefinition>> getDefinition)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            if (tokens.Count == 0 || !tokens[0].IsWord("select")) throw NotSupported(sql);
            if (tokens.Any(e => e.IsSymbol(";"))) throw NotSupported(sql);
            return await TranslateAggregateCoreAsync(tokens, sql, args, getDefinition, true);
        }

        static async Task<KintoneAggregate> TranslateAggregateCoreAsync(List<SqlToken> tokens, string sql, IReadOnlyDictionary<string, object?> args,
            Func<string, Task<KintoneTableDefinition>> getDefinition, bool allowWithin)
        {
            if (tokens.Count == 0 || !tokens[0].IsWord("select")) throw NotSupported(sql);

            //グループの数: select count(*) from ( 集計 ) 別名
            var isGroupCount = false;
            if (tokens.Count > 8 && tokens[1].IsWord("count") && tokens[2].IsSymbol("(") && tokens[3].IsSymbol("*") && tokens[4].IsSymbol(")") &&
                tokens[5].IsWord("from") && tokens[6].IsSymbol("("))
            {
                var close = FindClosingParenthesis(tokens, 6);
                if (close < 0 || close + 2 != tokens.Count || !tokens[close + 1].IsIdentifier) throw NotSupported(sql);
                tokens = tokens.GetRange(7, close - 7);
                isGroupCount = true;
                if (tokens.Count == 0 || !tokens[0].IsWord("select")) throw NotSupported(sql);
            }

            var (limit, offset) = ReadLimit(tokens, sql);
            int from = -1, where = -1, groupBy = -1, having = -1, orderBy = -1, inner = -1;
            var depth = 0;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")")) depth--;
                if (depth != 0) continue;
                if (tokens[i].IsWord("from") && from < 0) from = i;
                else if (tokens[i].IsWord("where") && where < 0) where = i;
                else if (tokens[i].IsWord("group") && i + 1 < tokens.Count && tokens[i + 1].IsWord("by") && groupBy < 0) groupBy = i;
                else if (tokens[i].IsWord("having") && having < 0) having = i;
                else if (tokens[i].IsWord("order") && i + 1 < tokens.Count && tokens[i + 1].IsWord("by") && orderBy < 0) orderBy = i;
                else if (tokens[i].IsWord("inner"))
                {
                    if (!allowWithin || 0 <= inner) throw NotSupported("Nested aggregation within the groups of another aggregation", sql);
                    inner = i;
                }
                else if (tokens[i].IsWord("union")) throw NotSupported(sql);
            }
            var clauses = new[] { from, where, groupBy, having, orderBy };
            if (from < 0) throw NotSupported(sql);
            for (var i = 1; i < clauses.Length; i++)
            {
                if (clauses[i] < 0) continue;
                if (clauses.Take(i).Any(e => e > clauses[i])) throw NotSupported(sql);
            }
            int End(int clause) => clauses.Where(e => e > clause).DefaultIfEmpty(tokens.Count).Min();
            //行を選ぶ定義は from の句の最後 (本体は結合の後ろに置く)
            var fromEnd = End(from);
            if (0 <= inner && !(from < inner && inner < fromEnd)) throw NotSupported(sql);

            var scope = await ParseFromAsync(tokens.GetRange(from + 1, (0 <= inner ? inner : fromEnd) - from - 1), null, getDefinition, sql);
            var plan = new KintoneAggregate
            {
                Table = scope.Table,
                MainAlias = scope.MainAlias,
                Definition = scope.Definition,
                Limit = limit,
                Offset = offset,
                IsGroupCount = isGroupCount,
            };
            plan.Joins.AddRange(scope.Joins);
            if (0 <= inner) plan.Within = await ParseWithinAsync(tokens.GetRange(inner, fromEnd - inner), scope, sql, args, getDefinition);

            //select の項目 (別名つき)。行を選ぶ定義の付いた集計の行数は別名なしの count(*) 1 つ
            var selectItems = SplitByComma(tokens.GetRange(1, from - 1), sql);
            if (plan.Within != null && groupBy < 0 && !isGroupCount && selectItems.Count == 1 && selectItems[0].Count == 4 && selectItems[0][0].IsWord("count") &&
                selectItems[0][1].IsSymbol("(") && selectItems[0][2].IsSymbol("*") && selectItems[0][3].IsSymbol(")"))
            {
                selectItems[0] = selectItems[0].Concat(new[] { new SqlToken(SqlTokenKind.Word, "as"), new SqlToken(SqlTokenKind.QuotedIdentifier, "count") }).ToList();
            }
            foreach (var item in selectItems)
            {
                if (item.Count < 3 || !item[^2].IsWord("as") || !item[^1].IsIdentifier) throw NotSupported(sql);
                var expression = ParseAggregateExpression(item.GetRange(0, item.Count - 2), scope, sql);
                if (expression is AggregateConstant && !isGroupCount) throw NotSupported(sql);
                plan.Items.Add(new AggregateItem(item[^1].Text, expression));
                if (expression is AggregateColumn column) plan.Groups.Add(column);
                if (expression is AggregateFunctionCall call && !plan.Measures.Contains(call)) plan.Measures.Add(call);
            }

            //group by (select の軸と、その空値の印・候補の順だけ)
            if (0 <= groupBy)
            {
                var groupTokens = tokens.GetRange(groupBy + 2, End(groupBy) - groupBy - 2);
                var grouped = new HashSet<AggregateColumn>();
                var groupItems = SplitByComma(groupTokens, sql).Select(e => ParseAggregateExpression(e, scope, sql)).ToList();
                //グループの数を数える SQL は select に軸が無い (group by の列が軸)
                if (isGroupCount)
                {
                    foreach (var column in groupItems.OfType<AggregateColumn>()) if (!plan.Groups.Contains(column)) plan.Groups.Add(column);
                }
                foreach (var expression in groupItems)
                {
                    var column = expression switch
                    {
                        AggregateColumn c => c,
                        AggregateNullFlag f => f.Column,
                        AggregateRank r => r.Column,
                        _ => throw NotSupported(sql),
                    };
                    if (!plan.Groups.Contains(column)) throw NotSupported(sql);
                    if (expression is AggregateColumn) grouped.Add(column);
                }
                if (!plan.Groups.All(grouped.Contains)) throw NotSupported(sql);
            }
            else if (plan.Groups.Count != 0 || plan.Items.Any(e => e.Expression is AggregateNullFlag or AggregateRank))
            {
                throw NotSupported(sql);
            }

            //軸に出ない列 (空値の印・候補の順) は軸の列だけ
            foreach (var item in plan.Items)
            {
                var column = item.Expression switch { AggregateNullFlag f => f.Column, AggregateRank r => r.Column, _ => null };
                if (column != null && !plan.Groups.Contains(column)) throw NotSupported(sql);
            }

            //having 集計値 演算子 数値 [and ...]
            if (0 <= having)
            {
                var havingTokens = tokens.GetRange(having + 1, End(having) - having - 1);
                foreach (var part in SplitTopLevel(havingTokens, e => e.IsWord("and"), sql))
                {
                    var op = FindTopLevel(part, 0, e => e.Kind == SqlTokenKind.Symbol && e.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=");
                    if (op <= 0) throw NotSupported(sql);
                    if (ParseAggregateExpression(part.GetRange(0, op), scope, sql) is not AggregateFunctionCall measure) throw NotSupported(sql);
                    var valueTokens = part.GetRange(op + 1, part.Count - op - 1);
                    var negative = valueTokens.Count == 2 && valueTokens[0].IsSymbol("-");
                    if (negative) valueTokens.RemoveAt(0);
                    if (valueTokens.Count != 1 || valueTokens[0].Kind != SqlTokenKind.Number ||
                        !decimal.TryParse(valueTokens[0].Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) throw NotSupported(sql);
                    if (!plan.Measures.Contains(measure)) plan.Measures.Add(measure);
                    plan.Having.Add(new HavingCondition(measure, part[op].Text == "!=" ? "<>" : part[op].Text, negative ? -value : value));
                }
            }

            //order by 別名 [asc|desc]
            if (0 <= orderBy)
            {
                foreach (var item in SplitByComma(tokens.GetRange(orderBy + 2, End(orderBy) - orderBy - 2), sql))
                {
                    if (!item[0].IsIdentifier || !plan.Items.Any(e => e.Alias == item[0].Text)) throw NotSupported(sql);
                    var descending = false;
                    if (item.Count == 2 && (item[1].IsWord("desc") || item[1].IsWord("asc"))) descending = item[1].IsWord("desc");
                    else if (item.Count != 1) throw NotSupported(sql);
                    plan.OrderBy.Add((item[0].Text, descending));
                }
            }

            //結合で読む列: 結合先の列・この結合の列を使う項目・この結合を元にする結合の元の列
            var columns = plan.Groups.Concat(plan.Measures.Where(e => e.Column != null).Select(e => e.Column!))
                .Concat(plan.Within?.On.Select(e => e.Outer) ?? Enumerable.Empty<AggregateColumn>()).ToList();
            foreach (var join in plan.Joins)
            {
                join.AddSelectColumn(join.DestinationColumn);
                foreach (var column in columns.Where(e => e.Alias == join.Alias)) join.AddSelectColumn(column.Column);
                foreach (var child in plan.Joins.Where(e => e.SourceAlias == join.Alias)) join.AddSelectColumn(child.SourceColumn);
            }

            //条件
            if (0 <= where)
            {
                var node = Fold(await new FilterParser(tokens.GetRange(where + 1, End(where) - where - 1), scope, args, sql, getDefinition).ParseAsync());
                if (node is ConstNode constant)
                {
                    if (!constant.Value) plan.IsEmpty = true;
                }
                else
                {
                    plan.Where = node;
                }
            }
            return plan;
        }

        //inner join ( 集計 ) 別名 on (列 = 別名."gN" or (列 is null and 別名."gN" is null)) and ... / on 1=1
        static async Task<KintoneWithin> ParseWithinAsync(List<SqlToken> tokens, Scope scope, string sql, IReadOnlyDictionary<string, object?> args,
            Func<string, Task<KintoneTableDefinition>> getDefinition)
        {
            if (tokens.Count < 7 || !tokens[0].IsWord("inner") || !tokens[1].IsWord("join") || !tokens[2].IsSymbol("(")) throw NotSupported(sql);
            var close = FindClosingParenthesis(tokens, 2);
            if (close < 0 || tokens.Count < close + 4 || !tokens[close + 1].IsIdentifier || !tokens[close + 2].IsWord("on")) throw NotSupported(sql);
            var alias = tokens[close + 1].Text;
            var plan = await TranslateAggregateCoreAsync(tokens.GetRange(3, close - 3), sql, args, getDefinition, false);
            if (plan.Table != scope.Table || plan.IsGroupCount) throw NotSupported("Aggregation within the groups of another table", sql);
            var within = new KintoneWithin { Plan = plan };

            var conditions = tokens.GetRange(close + 3, tokens.Count - close - 3);
            if (conditions.Count == 3 && conditions[0].Kind == SqlTokenKind.Number && conditions[0].Text == "1" && conditions[1].IsSymbol("=") &&
                conditions[2].Kind == SqlTokenKind.Number && conditions[2].Text == "1")
                return within;
            foreach (var part in SplitTopLevel(conditions, e => e.IsWord("and"), sql))
            {
                if (!part[0].IsSymbol("(") || FindClosingParenthesis(part, 0) != part.Count - 1) throw NotSupported(sql);
                var t = part.GetRange(1, part.Count - 2);
                var p = 0;
                void Expect(Func<SqlToken, bool> predicate)
                {
                    if (t.Count <= p || !predicate(t[p++])) throw NotSupported(sql);
                }
                string Inner()
                {
                    Expect(e => e.IsIdentifier && e.Text == alias);
                    Expect(e => e.IsSymbol("."));
                    if (t.Count <= p || !t[p].IsIdentifier) throw NotSupported(sql);
                    return t[p++].Text;
                }
                var outer = ReadColumn(t, ref p, scope, sql);
                Expect(e => e.IsSymbol("="));
                var innerAlias = Inner();
                Expect(e => e.IsWord("or"));
                Expect(e => e.IsSymbol("("));
                if (ReadColumn(t, ref p, scope, sql) != outer) throw NotSupported(sql);
                Expect(e => e.IsWord("is"));
                Expect(e => e.IsWord("null"));
                Expect(e => e.IsWord("and"));
                if (Inner() != innerAlias) throw NotSupported(sql);
                Expect(e => e.IsWord("is"));
                Expect(e => e.IsWord("null"));
                Expect(e => e.IsSymbol(")"));
                if (p != t.Count) throw NotSupported(sql);
                //選ぶ集計の軸の列だけと突き合わせる
                if (!plan.Items.Any(e => e.Alias == innerAlias && e.Expression is AggregateColumn)) throw NotSupported(sql);
                within.On.Add((new AggregateColumn(outer.Alias, outer.Column), innerAlias));
            }
            return within;
        }

        static int FindClosingParenthesis(List<SqlToken> tokens, int open)
        {
            var depth = 0;
            for (var i = open; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")") && --depth == 0) return i;
            }
            return -1;
        }

        static List<List<SqlToken>> SplitTopLevel(List<SqlToken> tokens, Func<SqlToken, bool> separator, string sql)
        {
            var parts = new List<List<SqlToken>>();
            var current = new List<SqlToken>();
            var depth = 0;
            foreach (var token in tokens)
            {
                if (token.IsSymbol("(")) depth++;
                else if (token.IsSymbol(")")) depth--;
                if (depth == 0 && separator(token))
                {
                    if (current.Count == 0) throw NotSupported(sql);
                    parts.Add(current);
                    current = new();
                    continue;
                }
                current.Add(token);
            }
            if (current.Count == 0) throw NotSupported(sql);
            parts.Add(current);
            return parts;
        }

        static readonly string[] DateFunctions = { "date_trunc", "to_char", "interval", "extract", "date_part", "make_date" };

        /// <summary>日付を期間に丸める軸 (本体の日付の軸) の拒否。kintone のクエリに日付の式が無い。</summary>
        static KintoneException DateBucketNotSupported()
            => new("Grouping by a period of a date (day / week / month / quarter / year) is not supported for a kintone data source. " +
                   "Add a calculated field for the period (for example DATE_FORMAT of the date as the year and month) to the kintone app and group by that field.");

        static AggregateExpression ParseAggregateExpression(List<SqlToken> tokens, Scope scope, string sql)
        {
            if (tokens.Any(e => e.Kind == SqlTokenKind.Word && DateFunctions.Any(f => e.IsWord(f)))) throw DateBucketNotSupported();
            if (tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.Number && tokens[0].Text == "1") return new AggregateConstant();

            var p = 0;
            AggregateColumn Column()
            {
                var (alias, column) = ReadColumn(tokens, ref p, scope, sql);
                return new AggregateColumn(alias, column);
            }
            void Expect(Func<SqlToken, bool> predicate)
            {
                if (tokens.Count <= p || !predicate(tokens[p++])) throw NotSupported(sql);
            }
            AggregateExpression Done(AggregateExpression expression)
            {
                if (p != tokens.Count) throw NotSupported(sql);
                return expression;
            }

            if (tokens.Count > 0 && tokens[0].IsWord("case"))
            {
                p = 1;
                if (tokens.Count > p && tokens[p].IsWord("when"))
                {
                    //case when 列 is null then 1 else 0 end
                    p++;
                    var column = Column();
                    Expect(t => t.IsWord("is"));
                    Expect(t => t.IsWord("null"));
                    Expect(t => t.IsWord("then"));
                    Expect(t => t.Kind == SqlTokenKind.Number && t.Text == "1");
                    Expect(t => t.IsWord("else"));
                    Expect(t => t.Kind == SqlTokenKind.Number && t.Text == "0");
                    Expect(t => t.IsWord("end"));
                    return Done(new AggregateNullFlag(column));
                }
                {
                    //case 列 when 'a' then 0 ... else n end
                    var column = Column();
                    var codes = new List<(string, int)>();
                    while (p < tokens.Count && tokens[p].IsWord("when"))
                    {
                        p++;
                        if (tokens.Count <= p || tokens[p].Kind != SqlTokenKind.StringLiteral) throw NotSupported(sql);
                        var code = tokens[p++].Text;
                        Expect(t => t.IsWord("then"));
                        codes.Add((code, ReadInt()));
                    }
                    Expect(t => t.IsWord("else"));
                    var otherwise = ReadInt();
                    Expect(t => t.IsWord("end"));
                    if (codes.Count == 0) throw NotSupported(sql);
                    return Done(new AggregateRank(column, codes, otherwise));
                }
            }
            if (tokens.Count >= 4 && tokens[0].Kind == SqlTokenKind.Word && tokens[1].IsSymbol("("))
            {
                var function = tokens[0].Text.ToLowerInvariant();
                p = 2;
                AggregateFunctionCall call;
                if (function == "count" && tokens[2].IsSymbol("*"))
                {
                    p = 3;
                    call = new AggregateFunctionCall("count", null);
                }
                else if (function == "count" && tokens[2].IsWord("distinct"))
                {
                    p = 3;
                    call = new AggregateFunctionCall("countdistinct", Column());
                }
                else if (function is "sum" or "avg" or "min" or "max")
                {
                    call = new AggregateFunctionCall(function, Column());
                }
                else
                {
                    throw NotSupported(sql);
                }
                Expect(t => t.IsSymbol(")"));
                return Done(call);
            }
            return Done(Column());

            int ReadInt()
            {
                if (tokens.Count <= p || tokens[p].Kind != SqlTokenKind.Number || !int.TryParse(tokens[p].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    throw NotSupported(sql);
                p++;
                return n;
            }
        }

        // ---------------------------------------------------------------- 書き込み

        internal static bool IsUpdate(string sql) => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("update") == true;
        internal static bool IsDelete(string sql) => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("delete") == true;
        internal static bool IsInsert(string sql) => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("insert") == true;

        /// <summary>
        /// 本体の insert を読む: insert into "t"("a",...) values(@p0,...) returning "k" / insert into "t" default values returning "k" /
        /// 一括 insert into "t" ("a",...) values (@p0,...),(@p1,...)
        /// </summary>
        internal static KintoneInsert ParseInsert(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var i = 0;
            Expect(tokens, ref i, t => t.IsWord("insert"), sql);
            Expect(tokens, ref i, t => t.IsWord("into"), sql);
            var insert = new KintoneInsert { Table = ReadTableName(tokens, ref i, sql) };
            if (i < tokens.Count && tokens[i].IsWord("default"))
            {
                i++;
                Expect(tokens, ref i, t => t.IsWord("values"), sql);
                insert.Rows.Add(new());
            }
            else
            {
                Expect(tokens, ref i, t => t.IsSymbol("("), sql);
                while (true)
                {
                    var column = Next(tokens, ref i, sql);
                    if (!column.IsIdentifier) throw NotSupported(sql);
                    insert.Columns.Add(column.Text);
                    var separator = Next(tokens, ref i, sql);
                    if (separator.IsSymbol(")")) break;
                    if (!separator.IsSymbol(",")) throw NotSupported(sql);
                }
                Expect(tokens, ref i, t => t.IsWord("values"), sql);
                while (true)
                {
                    Expect(tokens, ref i, t => t.IsSymbol("("), sql);
                    var row = new List<string>();
                    while (true)
                    {
                        var parameter = Next(tokens, ref i, sql);
                        if (parameter.Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
                        row.Add(parameter.Text);
                        var separator = Next(tokens, ref i, sql);
                        if (separator.IsSymbol(")")) break;
                        if (!separator.IsSymbol(",")) throw NotSupported(sql);
                    }
                    if (row.Count != insert.Columns.Count) throw NotSupported(sql);
                    insert.Rows.Add(row);
                    if (i < tokens.Count && tokens[i].IsSymbol(","))
                    {
                        i++;
                        continue;
                    }
                    break;
                }
            }
            if (i < tokens.Count && tokens[i].IsWord("returning"))
            {
                i++;
                var column = Next(tokens, ref i, sql);
                if (!column.IsIdentifier) throw NotSupported(sql);
                //複合キー ("a"|| '…' ||"b") の表は書き込めない
                if (i != tokens.Count) throw new KintoneException($"A table with a composite key cannot be written through a kintone data source (table '{insert.Table}').");
                insert.ReturningColumn = column.Text;
            }
            if (i != tokens.Count) throw NotSupported(sql);
            return insert;
        }

        /// <summary>本体の update (update "t" set "c"=@p0,... where "k"=@pid0 [and ...]) を読む。</summary>
        internal static KintoneWrite ParseUpdate(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var i = 0;
            Expect(tokens, ref i, t => t.IsWord("update"), sql);
            var update = new KintoneWrite { Table = ReadTableName(tokens, ref i, sql) };
            Expect(tokens, ref i, t => t.IsWord("set"), sql);
            while (true)
            {
                var column = Next(tokens, ref i, sql);
                if (!column.IsIdentifier) throw NotSupported(sql);
                Expect(tokens, ref i, t => t.IsSymbol("="), sql);
                var parameter = Next(tokens, ref i, sql);
                if (parameter.Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
                update.Values.Add((column.Text, parameter.Text));
                var separator = Next(tokens, ref i, sql);
                if (separator.IsWord("where")) break;
                if (!separator.IsSymbol(",")) throw NotSupported(sql);
            }
            ReadWhere(update, tokens.GetRange(i, tokens.Count - i), sql);
            return update;
        }

        /// <summary>本体の delete (delete from "t" where "k"=@pid0 [and ...]) を読む。</summary>
        internal static KintoneWrite ParseDelete(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var i = 0;
            Expect(tokens, ref i, t => t.IsWord("delete"), sql);
            Expect(tokens, ref i, t => t.IsWord("from"), sql);
            var delete = new KintoneWrite { Table = ReadTableName(tokens, ref i, sql) };
            Expect(tokens, ref i, t => t.IsWord("where"), sql);
            ReadWhere(delete, tokens.GetRange(i, tokens.Count - i), sql);
            return delete;
        }

        //先頭はキーの一致 ("k"=@pid0)。続きは and でつながる条件。版の一致 ("$revision"=@p / "$revision" is null) は分けて持つ (kintone のクエリに版は書けない)
        static void ReadWhere(KintoneWrite write, List<SqlToken> where, string sql)
        {
            if (where.Any(e => e.IsSymbol(";"))) throw NotSupported(sql);
            var parts = SplitTopLevel(where, e => e.IsWord("and"), sql);
            var key = parts[0];
            if (key.Count != 3 || !key[0].IsIdentifier || !key[1].IsSymbol("=") || key[2].Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
            write.KeyColumn = key[0].Text;
            write.KeyParameter = key[2].Text;

            var kept = new List<List<SqlToken>> { key };
            foreach (var part in parts.Skip(1))
            {
                if (part.Count == 3 && part[0].IsIdentifier && part[0].Text == KintoneTableDefinition.RevisionColumn)
                {
                    if (part[1].IsSymbol("=") && part[2].Kind == SqlTokenKind.Parameter && write.RevisionParameter == null)
                    {
                        write.RevisionParameter = part[2].Text;
                        continue;
                    }
                    if (part[1].IsWord("is") && part[2].IsWord("null"))
                    {
                        write.RevisionIsNull = true;
                        continue;
                    }
                }
                kept.Add(part);
            }
            write.HasOtherConditions = 1 < kept.Count;
            write.WhereSql = string.Join(" and ", kept.Select(part => string.Join(" ", part.Select(e => e.Kind switch
            {
                SqlTokenKind.QuotedIdentifier => "\"" + e.Text + "\"",
                SqlTokenKind.StringLiteral => "'" + e.Text.Replace("'", "''") + "'",
                _ => e.Text,
            }))));
        }

        static SqlToken Next(List<SqlToken> tokens, ref int i, string sql)
        {
            if (tokens.Count <= i) throw NotSupported(sql);
            return tokens[i++];
        }

        static void Expect(List<SqlToken> tokens, ref int i, Func<SqlToken, bool> predicate, string sql)
        {
            if (!predicate(Next(tokens, ref i, sql))) throw NotSupported(sql);
        }
    }
}
