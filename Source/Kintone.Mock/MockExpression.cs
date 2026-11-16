using System.Globalization;
using System.Text;

namespace Kintone.Mock
{
    /// <summary>
    /// 計算フィールド・自動計算の文字列の式 (モックが分かる範囲だけ)。
    /// 数値・"文字"・フィールドコード・+ - * /・&amp; (文字の連結)・括弧・SUM(テーブルの列)。
    /// </summary>
    sealed class MockExpression
    {
        readonly Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> _evaluate;

        MockExpression(Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> evaluate, List<string> fields, List<string> sums)
        {
            _evaluate = evaluate;
            FieldReferences = fields;
            SumReferences = sums;
        }

        /// <summary>式が直接使うフィールドコード。</summary>
        public IReadOnlyList<string> FieldReferences { get; }

        /// <summary>SUM( ) に書いたフィールドコード。</summary>
        public IReadOnlyList<string> SumReferences { get; }

        /// <summary>評価する。field はフィールドの値 (decimal / string / null)、column は SUM の対象の列の値。0 で割ると DivideByZeroException。</summary>
        public object? Evaluate(Func<string, object?> field, Func<string, IReadOnlyList<object?>> column) => _evaluate(field, column);

        /// <summary>式を読む。読めなければ FormatException。</summary>
        public static MockExpression Parse(string text)
        {
            var parser = new Parser(text);
            var node = parser.ParseConcat();
            if (parser.Peek().Kind != TokenKind.End) throw new FormatException($"Unexpected '{parser.Peek().Text}' in the expression '{text}'.");
            return new MockExpression(node, parser.Fields, parser.Sums);
        }

        public static decimal ToNumber(object? value) => value switch
        {
            null => 0m,
            decimal d => d,
            string s when s.Length == 0 => 0m,
            string s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException($"'{s}' is not a number."),
            _ => throw new FormatException("Not a number."),
        };

        public static string ToText(object? value) => value switch
        {
            null => string.Empty,
            decimal d => FormatNumber(d),
            _ => value.ToString() ?? string.Empty,
        };

        public static string FormatNumber(decimal value)
        {
            var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        enum TokenKind { End, Number, String, Identifier, Symbol }

        readonly record struct Token(TokenKind Kind, string Text);

        sealed class Parser
        {
            readonly List<Token> _tokens = new();
            int _position;
            public List<string> Fields { get; } = new();
            public List<string> Sums { get; } = new();

            public Parser(string text)
            {
                var i = 0;
                while (i < text.Length)
                {
                    var c = text[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                    {
                        var start = i;
                        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                        _tokens.Add(new Token(TokenKind.Number, text[start..i]));
                        continue;
                    }
                    if (c == '"')
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (true)
                        {
                            if (i >= text.Length) throw new FormatException($"Unterminated string in the expression '{text}'.");
                            if (text[i] == '"') { i++; break; }
                            if (text[i] == '\\' && i + 1 < text.Length) i++;
                            sb.Append(text[i++]);
                        }
                        _tokens.Add(new Token(TokenKind.String, sb.ToString()));
                        continue;
                    }
                    if ("+-*/&(),".Contains(c))
                    {
                        _tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                        i++;
                        continue;
                    }
                    if (IsIdentifierChar(c))
                    {
                        var start = i;
                        while (i < text.Length && IsIdentifierChar(text[i])) i++;
                        _tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
                        continue;
                    }
                    throw new FormatException($"Unexpected character '{c}' in the expression '{text}'.");
                }
                _tokens.Add(new Token(TokenKind.End, string.Empty));
            }

            static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '・' || c == '＄' || c == '￥' || c == '$';

            public Token Peek() => _tokens[_position];

            Token Next() => _tokens[_position++];

            bool TakeSymbol(string symbol)
            {
                if (Peek().Kind == TokenKind.Symbol && Peek().Text == symbol)
                {
                    _position++;
                    return true;
                }
                return false;
            }

            public Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> ParseConcat()
            {
                var left = ParseAdditive();
                while (TakeSymbol("&"))
                {
                    var l = left;
                    var r = ParseAdditive();
                    left = (f, c) => ToText(l(f, c)) + ToText(r(f, c));
                }
                return left;
            }

            Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> ParseAdditive()
            {
                var left = ParseTerm();
                while (true)
                {
                    var l = left;
                    if (TakeSymbol("+"))
                    {
                        var r = ParseTerm();
                        left = (f, c) => ToNumber(l(f, c)) + ToNumber(r(f, c));
                    }
                    else if (TakeSymbol("-"))
                    {
                        var r = ParseTerm();
                        left = (f, c) => ToNumber(l(f, c)) - ToNumber(r(f, c));
                    }
                    else return left;
                }
            }

            Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> ParseTerm()
            {
                var left = ParseUnary();
                while (true)
                {
                    var l = left;
                    if (TakeSymbol("*"))
                    {
                        var r = ParseUnary();
                        left = (f, c) => ToNumber(l(f, c)) * ToNumber(r(f, c));
                    }
                    else if (TakeSymbol("/"))
                    {
                        var r = ParseUnary();
                        left = (f, c) => ToNumber(l(f, c)) / ToNumber(r(f, c));
                    }
                    else return left;
                }
            }

            Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> ParseUnary()
            {
                if (TakeSymbol("-"))
                {
                    var operand = ParseUnary();
                    return (f, c) => -ToNumber(operand(f, c));
                }
                return ParsePrimary();
            }

            Func<Func<string, object?>, Func<string, IReadOnlyList<object?>>, object?> ParsePrimary()
            {
                var token = Next();
                switch (token.Kind)
                {
                    case TokenKind.Number:
                        if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                            throw new FormatException($"'{token.Text}' is not a number.");
                        return (_, _) => number;
                    case TokenKind.String:
                        var text = token.Text;
                        return (_, _) => text;
                    case TokenKind.Symbol when token.Text == "(":
                        var inner = ParseConcat();
                        if (!TakeSymbol(")")) throw new FormatException("')' is missing in the expression.");
                        return inner;
                    case TokenKind.Identifier:
                        if (TakeSymbol("("))
                        {
                            if (!string.Equals(token.Text, "SUM", StringComparison.Ordinal))
                                throw new FormatException($"The function '{token.Text}' is not supported by the mock (only SUM).");
                            var argument = Next();
                            if (argument.Kind != TokenKind.Identifier || !TakeSymbol(")"))
                                throw new FormatException("SUM takes one field code of a table column.");
                            Sums.Add(argument.Text);
                            var code = argument.Text;
                            return (_, c) => c(code).Aggregate(0m, (sum, v) => sum + ToNumber(v));
                        }
                        Fields.Add(token.Text);
                        var name = token.Text;
                        return (f, _) => f(name);
                    default:
                        throw new FormatException($"Unexpected '{token.Text}' in the expression.");
                }
            }
        }
    }
}
