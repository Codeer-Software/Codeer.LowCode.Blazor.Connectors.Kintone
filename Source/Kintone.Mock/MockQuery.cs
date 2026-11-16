using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static Kintone.Mock.MockFieldTypes;

namespace Kintone.Mock
{
    /// <summary>レコードの取得の query (条件・order by・limit・offset) を読んで、レコードに当てる。</summary>
    sealed class MockQuery
    {
        public const int DefaultLimit = 100;
        public const int MaxLimit = 500;
        public const int MaxOffset = 10000;

        public Condition? Where { get; private set; }
        public List<(Target Target, bool Descending)> OrderBy { get; } = new();
        public int Limit { get; private set; } = DefaultLimit;
        public int Offset { get; private set; }

        /// <summary>条件・並べ替えの対象 ($id・$revision・フィールド)。</summary>
        public sealed class Target
        {
            public string Code { get; init; } = string.Empty;

            /// <summary>$id / $revision なら null。</summary>
            public MockField? Field { get; init; }

            /// <summary>テーブルの中のフィールドならそのテーブル。</summary>
            public MockField? Table { get; init; }

            public string Type => Field?.Type ?? (Code == "$id" ? Id : Revision);
        }

        // ---------------------------------------------------------------- 型ごとの演算子

        const string Eq = "=", Ne = "!=", Gt = ">", Lt = "<", Ge = ">=", Le = "<=", In = "in", NotIn = "not in",
            Like = "like", NotLike = "not like", IsEmpty = "is empty", IsNotEmpty = "is not empty";

        static readonly string[] Comparison = { Eq, Ne, Gt, Lt, Ge, Le };
        static readonly string[] Inclusion = { In, NotIn };
        static readonly string[] Likes = { Like, NotLike };
        static readonly string[] Empties = { IsEmpty, IsNotEmpty };

        /// <summary>公式のクエリ記法の表の、フィールドの種類ごとに使える演算子。テーブルの中は = / != の代わりに in / not in。</summary>
        public static IReadOnlySet<string> AllowedOperators(string type, bool inTable)
        {
            string[] ops = type switch
            {
                Id or RecordNumber => Comparison.Concat(Inclusion).ToArray(),
                SingleLineText or Link => new[] { Eq, Ne }.Concat(Inclusion).Concat(Likes).Concat(Empties).ToArray(),
                Number or Calc => Comparison.Concat(Inclusion).Concat(Empties).ToArray(),
                MultiLineText or RichText => Likes.Concat(Empties).ToArray(),
                CheckBox or MultiSelect or DropDown => Inclusion.Concat(Empties).ToArray(),
                RadioButton => Inclusion,
                UserSelect => Inclusion.Concat(Empties).ToArray(),
                Creator or Modifier or StatusAssignee or Category => Inclusion,
                Status => new[] { Eq, Ne }.Concat(Inclusion).ToArray(),
                Date or Time or MockFieldTypes.DateTime => Comparison.Concat(Empties).ToArray(),
                CreatedTime or UpdatedTime => Comparison,
                MockFieldTypes.File => Likes.Concat(Empties).ToArray(),
                _ => Array.Empty<string>(),
            };
            var set = new HashSet<string>(ops);
            if (inTable && set.Contains(Eq))
            {
                set.Remove(Eq);
                set.Remove(Ne);
                set.Add(In);
                set.Add(NotIn);
            }
            return set;
        }

        /// <summary>order by に使えない種類。</summary>
        public static bool IsSortable(Target target)
            => target.Table == null && target.Type is not (Revision or MultiLineText or RichText or CheckBox or MultiSelect or MockFieldTypes.File or Subtable
                or UserSelect or StatusAssignee or Category);

        static MockKintoneException SyntaxError(string message) => new(400, "GAIA_IQ03", "クエリ記法が間違っています。" + message);

        // ---------------------------------------------------------------- 読む

        enum TokenKind { End, String, Word, Symbol }

        readonly record struct Token(TokenKind Kind, string Text);

        List<Token> _tokens = new();
        int _position;
        MockApp _app = null!;

        public static MockQuery Parse(string? text, MockApp app)
        {
            var query = new MockQuery { _app = app, _tokens = Tokenize(text ?? string.Empty) };
            query.ParseQuery();
            return query;
        }

        static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw SyntaxError("文字列の \" が閉じていません。");
                        if (text[i] == '"') { i++; break; }
                        if (text[i] == '\\')
                        {
                            if (i + 1 >= text.Length) throw SyntaxError("文字列の \\ のあとに文字がありません。");
                            i++;
                        }
                        sb.Append(text[i++]);
                    }
                    tokens.Add(new Token(TokenKind.String, sb.ToString()));
                    continue;
                }
                if (c is '(' or ')' or ',')
                {
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                    i++;
                    continue;
                }
                if (c is '=' or '!' or '<' or '>')
                {
                    var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
                    if (two is "!=" or ">=" or "<=")
                    {
                        tokens.Add(new Token(TokenKind.Symbol, two));
                        i += 2;
                        continue;
                    }
                    if (c == '!') throw SyntaxError("「!」の使い方が間違っています。");
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                    i++;
                    continue;
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ',' or '"' or '=' or '!' or '<' or '>')) i++;
                tokens.Add(new Token(TokenKind.Word, text[start..i]));
            }
            tokens.Add(new Token(TokenKind.End, string.Empty));
            return tokens;
        }

        Token Peek(int ahead = 0) => _tokens[Math.Min(_position + ahead, _tokens.Count - 1)];

        Token Next() => _tokens[_position++];

        bool IsKeyword(Token token, string keyword) => token.Kind == TokenKind.Word && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);

        bool TakeKeyword(string keyword)
        {
            if (!IsKeyword(Peek(), keyword)) return false;
            _position++;
            return true;
        }

        void ExpectKeyword(string keyword)
        {
            if (!TakeKeyword(keyword)) throw SyntaxError($"「{keyword}」がありません ({Describe(Peek())} の前)。");
        }

        bool TakeSymbol(string symbol)
        {
            if (Peek().Kind != TokenKind.Symbol || Peek().Text != symbol) return false;
            _position++;
            return true;
        }

        void ExpectSymbol(string symbol)
        {
            if (!TakeSymbol(symbol)) throw SyntaxError($"「{symbol}」がありません ({Describe(Peek())} の前)。");
        }

        static string Describe(Token token) => token.Kind == TokenKind.End ? "クエリの終わり" : $"「{token.Text}」";

        bool AtClauseKeyword() => Peek().Kind == TokenKind.End || IsKeyword(Peek(), "order") || IsKeyword(Peek(), "limit") || IsKeyword(Peek(), "offset");

        void ParseQuery()
        {
            if (!AtClauseKeyword()) Where = ParseOr();
            if (TakeKeyword("order"))
            {
                ExpectKeyword("by");
                do
                {
                    var target = ParseTarget(forSort: true);
                    bool descending;
                    if (TakeKeyword("asc")) descending = false;
                    else if (TakeKeyword("desc")) descending = true;
                    else throw SyntaxError($"order by の「{target.Code}」に asc か desc を付けてください (モックは省略を受け付けません)。");
                    OrderBy.Add((target, descending));
                } while (TakeSymbol(","));
            }
            if (TakeKeyword("limit"))
            {
                Limit = ParseInteger("limit");
                if (Limit > MaxLimit) throw new MockKintoneException(400, "CB_VA01", "入力内容が正しくありません。",
                    new Dictionary<string, List<string>> { ["query"] = new() { $"limit には {MaxLimit} 以下の値を指定してください。" } });
            }
            if (TakeKeyword("offset"))
            {
                Offset = ParseInteger("offset");
                if (Offset > MaxOffset) throw new MockKintoneException(400, "CB_VA01", "入力内容が正しくありません。",
                    new Dictionary<string, List<string>> { ["query"] = new() { $"offset には {MaxOffset} 以下の値を指定してください。" } });
            }
            if (Peek().Kind != TokenKind.End) throw SyntaxError($"{Describe(Peek())} は使えません。");
        }

        int ParseInteger(string clause)
        {
            var token = Next();
            if (token.Kind != TokenKind.Word || !int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw SyntaxError($"{clause} には 0 以上の整数を指定してください。");
            return value;
        }

        Condition ParseOr()
        {
            var items = new List<Condition> { ParseAnd() };
            while (TakeKeyword("or")) items.Add(ParseAnd());
            return items.Count == 1 ? items[0] : new OrCondition(items);
        }

        Condition ParseAnd()
        {
            var items = new List<Condition> { ParsePrimary() };
            while (TakeKeyword("and")) items.Add(ParsePrimary());
            return items.Count == 1 ? items[0] : new AndCondition(items);
        }

        Condition ParsePrimary()
        {
            if (TakeSymbol("("))
            {
                var inner = ParseOr();
                ExpectSymbol(")");
                return inner;
            }
            var target = ParseTarget(forSort: false);
            string op;
            var token = Next();
            if (token.Kind == TokenKind.Symbol && token.Text is Eq or Ne or Gt or Lt or Ge or Le) op = token.Text;
            else if (IsKeyword(token, "in")) op = In;
            else if (IsKeyword(token, "like")) op = Like;
            else if (IsKeyword(token, "not"))
            {
                if (TakeKeyword("in")) op = NotIn;
                else if (TakeKeyword("like")) op = NotLike;
                else throw SyntaxError($"「not」のあとに {Describe(Peek())} は使えません。");
            }
            else if (IsKeyword(token, "is"))
            {
                var not = TakeKeyword("not");
                ExpectKeyword("empty");
                op = not ? IsNotEmpty : IsEmpty;
            }
            else throw SyntaxError($"「{target.Code}」のあとに演算子がありません ({Describe(token)})。");

            if (!AllowedOperators(target.Type, target.Table != null).Contains(op))
            {
                throw new MockKintoneException(400, "GAIA_IQ03",
                    $"クエリ記法が間違っています。フィールド「{target.Code}」({target.Type}{(target.Table != null ? "・テーブルの中" : string.Empty)}) に演算子「{op}」は使えません。");
            }

            var literals = new List<Literal>();
            if (op is In or NotIn)
            {
                ExpectSymbol("(");
                do literals.Add(ParseLiteral(target)); while (TakeSymbol(","));
                ExpectSymbol(")");
            }
            else if (op is not (IsEmpty or IsNotEmpty)) literals.Add(ParseLiteral(target));
            return new CompareCondition(target, op, literals);
        }

        Target ParseTarget(bool forSort)
        {
            var token = Next();
            if (token.Kind != TokenKind.Word || IsReserved(token.Text))
                throw SyntaxError($"フィールドコードがありません ({Describe(token)})。");
            if (Peek().Kind == TokenKind.Symbol && Peek().Text == "(")
                throw SyntaxError($"関数「{token.Text}()」はモックでは使えません。");
            Target target;
            if (token.Text == "$id") target = new Target { Code = "$id" };
            else if (token.Text == "$revision") target = new Target { Code = "$revision" };
            else
            {
                var found = _app.FindField(token.Text);
                if (found == null || !IsQueryable(found.Value.Field)) throw new MockKintoneException(400, "GAIA_IQ11", $"指定したフィールド（{token.Text}）が見つかりません。");
                target = new Target { Code = token.Text, Field = found.Value.Field, Table = found.Value.Table };
            }
            if (forSort)
            {
                if (!IsSortable(target)) throw new MockKintoneException(400, "GAIA_IQ03", $"クエリ記法が間違っています。フィールド「{target.Code}」({target.Type}) で並べ替えはできません。");
            }
            else if (target.Code == "$revision" || target.Type == Subtable)
            {
                throw new MockKintoneException(400, "GAIA_IQ03", $"クエリ記法が間違っています。フィールド「{target.Code}」は条件に使えません。");
            }
            return target;
        }

        bool IsQueryable(MockField field) => field.Type switch
        {
            Status or StatusAssignee => _app.ProcessManagementEnabled,
            Category => false,
            _ => true,
        };

        static bool IsReserved(string word) => word.ToLowerInvariant() is "and" or "or" or "in" or "not" or "like" or "is" or "empty" or "order" or "by" or "asc" or "desc" or "limit" or "offset";

        Literal ParseLiteral(Target target)
        {
            var token = Next();
            string raw;
            if (token.Kind == TokenKind.String) raw = token.Text;
            else if (token.Kind == TokenKind.Word)
            {
                if (Peek().Kind == TokenKind.Symbol && Peek().Text == "(") throw SyntaxError($"関数「{token.Text}()」はモックでは使えません。");
                if (!decimal.TryParse(token.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                    throw SyntaxError($"値「{token.Text}」は \" で囲んでください。");
                raw = token.Text;
            }
            else throw SyntaxError($"値がありません ({Describe(token)})。");
            return Literal.Create(raw, target, _app);
        }

        // ---------------------------------------------------------------- 値

        enum Kind { Text, Number, Date, Time, DateTime, Multi }

        static Kind KindOf(string type) => type switch
        {
            Id or Revision or RecordNumber or Number or Calc => Kind.Number,
            Date => Kind.Date,
            Time => Kind.Time,
            MockFieldTypes.DateTime or CreatedTime or UpdatedTime => Kind.DateTime,
            CheckBox or MultiSelect or UserSelect or StatusAssignee or Category or MockFieldTypes.File => Kind.Multi,
            _ => Kind.Text,
        };

        sealed class Literal
        {
            public string Raw { get; init; } = string.Empty;
            public decimal Number { get; init; }
            public DateTimeOffset Start { get; init; }
            public DateTimeOffset End { get; init; }
            public bool DateOnly { get; init; }

            public bool IsEmpty => Raw.Length == 0;

            public static Literal Create(string raw, Target target, MockApp app)
            {
                if (raw.Length == 0) return new Literal { Raw = raw };
                var bad = () => SyntaxError($"フィールド「{target.Code}」({target.Type}) に値「{raw}」は使えません。");
                switch (KindOf(target.Type))
                {
                    case Kind.Number:
                        var text = raw;
                        if (target.Type == RecordNumber && app.Code.Length > 0 && text.StartsWith(app.Code + "-", StringComparison.Ordinal)) text = text[(app.Code.Length + 1)..];
                        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw bad();
                        return new Literal { Raw = raw, Number = number };
                    case Kind.Date:
                        if (!System.DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw bad();
                        return new Literal { Raw = raw };
                    case Kind.Time:
                        if (!TimeOnly.TryParseExact(raw, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw bad();
                        return new Literal { Raw = raw };
                    case Kind.DateTime:
                        if (System.DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        {
                            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                            return new Literal { Raw = raw, DateOnly = true, Start = start, End = start.AddDays(1) };
                        }
                        if (!MockKintone.TryParseDateTime(raw, out var instant)) throw bad();
                        return new Literal { Raw = raw, Start = instant, End = instant.AddMinutes(1) };
                    default:
                        return new Literal { Raw = raw };
                }
            }
        }

        /// <summary>レコードの対象の値 (テーブルの中なら行ごと)。</summary>
        static List<JsonNode?> ValuesOf(Target target, StoredRecord record)
        {
            if (target.Field == null) return new List<JsonNode?> { JsonValue.Create(target.Code == "$id" ? record.Id : record.Revision) };
            if (target.Field.Type == RecordNumber) return new List<JsonNode?> { JsonValue.Create(record.Id) };
            if (target.Table != null)
            {
                var rows = record.Values.TryGetValue(target.Table.Code, out var value) && value is List<StoredRow> list ? list : new List<StoredRow>();
                return rows.Select(r => r.Values.TryGetValue(target.Field.Code, out var v) ? v : null).ToList();
            }
            return new List<JsonNode?> { record.Values.TryGetValue(MockKintone.ValueKey(target.Field), out var stored) ? stored as JsonNode : null };
        }

        static string ScalarText(JsonNode? node) => node switch
        {
            null => string.Empty,
            JsonObject o => o["code"]?.GetValue<string>() ?? string.Empty,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => string.Empty,
        };

        static List<string> MultiTexts(string type, JsonNode? node)
        {
            if (node is not JsonArray array) return new List<string>();
            return array.Select(e => e switch
            {
                JsonObject o when type == MockFieldTypes.File => o["name"]?.GetValue<string>() ?? string.Empty,
                JsonObject o => o["code"]?.GetValue<string>() ?? string.Empty,
                _ => ScalarText(e),
            }).ToList();
        }

        static bool IsEmptyValue(string type, JsonNode? node)
            => KindOf(type) == Kind.Multi ? MultiTexts(type, node).Count == 0 : ScalarText(node).Length == 0;

        /// <summary>空でない値と、空でない比較の値を比べる (負・0・正)。</summary>
        static int CompareValue(string type, string value, Literal literal)
        {
            switch (KindOf(type))
            {
                case Kind.Number:
                    return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                        ? number.CompareTo(literal.Number) : string.CompareOrdinal(value, literal.Raw);
                case Kind.DateTime:
                    return MockKintone.TryParseDateTime(value, out var instant) ? instant.CompareTo(literal.Start) : string.CompareOrdinal(value, literal.Raw);
                default:
                    return string.CompareOrdinal(value, literal.Raw);
            }
        }

        static bool Equal(string type, string value, Literal literal)
        {
            if (literal.IsEmpty) return value.Length == 0;
            if (value.Length == 0) return false;
            if (KindOf(type) == Kind.DateTime)
            {
                if (!MockKintone.TryParseDateTime(value, out var instant)) return false;
                return instant >= literal.Start && instant < literal.End;
            }
            return CompareValue(type, value, literal) == 0;
        }

        static bool Order(string type, string value, string op, Literal literal)
        {
            if (literal.IsEmpty || value.Length == 0) return false;
            if (KindOf(type) == Kind.DateTime && literal.DateOnly)
            {
                if (!MockKintone.TryParseDateTime(value, out var instant)) return false;
                return op switch
                {
                    Gt => instant >= literal.End,
                    Ge => instant >= literal.Start,
                    Lt => instant < literal.Start,
                    _ => instant < literal.End,
                };
            }
            var c = CompareValue(type, value, literal);
            return op switch { Gt => c > 0, Ge => c >= 0, Lt => c < 0, _ => c <= 0 };
        }

        /// <summary>値 1 つ (テーブルなら 1 行) に、否定でない演算子を当てる。</summary>
        static bool MatchPositive(string type, JsonNode? node, string op, List<Literal> literals)
        {
            if (KindOf(type) == Kind.Multi)
            {
                var values = MultiTexts(type, node);
                return op switch
                {
                    In => literals.Any(l => l.IsEmpty ? values.Count == 0 : values.Contains(l.Raw, StringComparer.Ordinal)),
                    Like => values.Any(v => v.Contains(literals[0].Raw, StringComparison.OrdinalIgnoreCase)),
                    IsEmpty => values.Count == 0,
                    _ => throw new InvalidOperationException(op),
                };
            }
            var value = ScalarText(node);
            return op switch
            {
                Eq => Equal(type, value, literals[0]),
                Gt or Ge or Lt or Le => Order(type, value, op, literals[0]),
                In => literals.Any(l => Equal(type, value, l)),
                Like => literals[0].IsEmpty || value.Contains(literals[0].Raw, StringComparison.OrdinalIgnoreCase),
                IsEmpty => value.Length == 0,
                _ => throw new InvalidOperationException(op),
            };
        }

        // ---------------------------------------------------------------- 条件

        public abstract class Condition
        {
            public abstract bool Match(StoredRecord record);
        }

        sealed class AndCondition : Condition
        {
            readonly List<Condition> _items;
            public AndCondition(List<Condition> items) => _items = items;
            public override bool Match(StoredRecord record) => _items.All(e => e.Match(record));
        }

        sealed class OrCondition : Condition
        {
            readonly List<Condition> _items;
            public OrCondition(List<Condition> items) => _items = items;
            public override bool Match(StoredRecord record) => _items.Any(e => e.Match(record));
        }

        sealed class CompareCondition : Condition
        {
            readonly Target _target;
            readonly string _op;
            readonly List<Literal> _literals;

            public CompareCondition(Target target, string op, List<Literal> literals)
            {
                _target = target;
                _op = op;
                _literals = literals;
            }

            public override bool Match(StoredRecord record)
            {
                var values = ValuesOf(_target, record);
                var type = _target.Type;
                //否定は肯定の結果を裏返す (未入力の行も「合う」)。テーブルは「どの行も合わない」
                var (positive, negate) = _op switch
                {
                    Ne => (Eq, true),
                    NotIn => (In, true),
                    NotLike => (Like, true),
                    IsNotEmpty => (IsEmpty, true),
                    _ => (_op, false),
                };
                bool result;
                if (_target.Table != null)
                {
                    result = positive == IsEmpty && !negate
                        ? values.Count == 0 || values.Any(v => MatchPositive(type, v, IsEmpty, _literals))
                        : positive == IsEmpty
                            ? values.All(v => MatchPositive(type, v, IsEmpty, _literals))
                            : values.Any(v => MatchPositive(type, v, positive, _literals));
                }
                else result = MatchPositive(type, values[0], positive, _literals);
                return negate ? !result : result;
            }
        }

        // ---------------------------------------------------------------- 並べ替え

        /// <summary>条件に合うレコードを、order by (無ければレコード ID の降順) で並べる。同じ値はレコード ID の降順。</summary>
        public List<StoredRecord> FilterAndSort(IEnumerable<StoredRecord> records)
        {
            var list = records.Where(e => Where == null || Where.Match(e)).ToList();
            list.Sort((a, b) =>
            {
                foreach (var (target, descending) in OrderBy)
                {
                    var c = CompareForSort(target, a, b);
                    if (c != 0) return descending ? -c : c;
                }
                return b.Id.CompareTo(a.Id);
            });
            return list;
        }

        /// <summary>並べ替えの比較 (未入力がいちばん小さい)。</summary>
        static int CompareForSort(Target target, StoredRecord a, StoredRecord b)
        {
            var x = ScalarText(ValuesOf(target, a)[0]);
            var y = ScalarText(ValuesOf(target, b)[0]);
            if (x.Length == 0 || y.Length == 0) return (x.Length == 0 ? 0 : 1) - (y.Length == 0 ? 0 : 1);
            switch (KindOf(target.Type))
            {
                case Kind.Number:
                    var nx = decimal.Parse(x, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    var ny = decimal.Parse(y, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    return nx.CompareTo(ny);
                case Kind.DateTime:
                    if (MockKintone.TryParseDateTime(x, out var dx) && MockKintone.TryParseDateTime(y, out var dy)) return dx.CompareTo(dy);
                    return string.CompareOrdinal(x, y);
                default:
                    return string.CompareOrdinal(x, y);
            }
        }
    }
}
