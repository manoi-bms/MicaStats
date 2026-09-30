using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Tools ▸ Evaluate (spec 5.1): arithmetic on decimal and <c>0x</c> numbers with
    /// <c>+ - * / % ^</c>, parentheses and unary minus, computed in <see cref="decimal"/> so
    /// 0.1 + 0.2 is 0.3. MicaPad's own recursive-descent parser: nothing is compiled or executed,
    /// and every problem (a syntax error, division by zero, a number or result too large, nesting
    /// too deep) comes back as a message, never as an exception.
    ///
    /// <para>
    /// Precedence, loosest first: <c>+ -</c>; <c>* / %</c>; unary minus; <c>^</c>, which is
    /// right-associative and binds tighter than unary minus, as in mathematics: <c>2^3^2</c> is
    /// 512 and <c>-2^2</c> is −4. The exponent may itself be negative (<c>2^-1</c>).
    /// </para>
    /// </summary>
    public static class ExpressionEvaluator
    {
        /// <summary>Deeper nesting than this is refused: the recursion must never overflow the stack.</summary>
        public const int MaxDepth = 200;

        public const string SyntaxError = "Not a valid expression";
        public const string DivisionByZero = "Division by zero";
        public const string ResultTooLarge = "The result is too large";
        public const string NumberTooLarge = "A number is too large";
        public const string NotReal = "The result is not a real number";
        public const string TooDeep = "The expression is nested too deeply";

        /// <summary>The value of <paramref name="expression"/>, or why there is none.</summary>
        public static (decimal Value, string? Problem) Evaluate(string expression)
        {
            var parser = new Parser(expression);
            try
            {
                decimal value = parser.ParseExpression();
                parser.SkipSpaces();
                return parser.AtEnd ? (value, null) : (0m, SyntaxError);
            }
            catch (EvalException ex)
            {
                return (0m, ex.Message);
            }
            catch (OverflowException)
            {
                return (0m, ResultTooLarge);
            }
            catch (DivideByZeroException)
            {
                return (0m, DivisionByZero);
            }
        }

        /// <summary>The result as MicaPad writes it: invariant digits, at most 15 decimals, no trailing zeros, never "-0".</summary>
        public static string Format(decimal value)
        {
            decimal rounded = Math.Round(value, 15, MidpointRounding.AwayFromZero);
            if (rounded == 0m) rounded = 0m;
            return rounded.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        private sealed class EvalException : Exception
        {
            public EvalException(string message) : base(message)
            {
            }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            private int _depth;

            public Parser(string text) => _text = text;

            public bool AtEnd => _pos >= _text.Length;

            public void SkipSpaces()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
            }

            // expression := term (('+' | '-') term)*
            public decimal ParseExpression()
            {
                Enter();
                decimal value = ParseTerm();
                while (true)
                {
                    SkipSpaces();
                    if (Accept('+')) value += ParseTerm();
                    else if (Accept('-')) value -= ParseTerm();
                    else break;
                }
                _depth--;
                return value;
            }

            // term := unary (('*' | '/' | '%') unary)*
            private decimal ParseTerm()
            {
                decimal value = ParseUnary();
                while (true)
                {
                    SkipSpaces();
                    if (Accept('*'))
                    {
                        value *= ParseUnary();
                    }
                    else if (Accept('/'))
                    {
                        decimal divisor = ParseUnary();
                        if (divisor == 0m) throw new EvalException(DivisionByZero);
                        value /= divisor;
                    }
                    else if (Accept('%'))
                    {
                        decimal divisor = ParseUnary();
                        if (divisor == 0m) throw new EvalException(DivisionByZero);
                        value %= divisor;
                    }
                    else
                    {
                        break;
                    }
                }
                return value;
            }

            // unary := ('-' | '+') unary | power
            private decimal ParseUnary()
            {
                SkipSpaces();
                if (Accept('-'))
                {
                    Enter();
                    decimal value = -ParseUnary();
                    _depth--;
                    return value;
                }
                if (Accept('+'))
                {
                    Enter();
                    decimal value = ParseUnary();
                    _depth--;
                    return value;
                }
                return ParsePower();
            }

            // power := primary ('^' unary)?   — the exponent is a unary, so 2^3^2 = 2^(3^2) and 2^-1 works
            private decimal ParsePower()
            {
                decimal value = ParsePrimary();
                SkipSpaces();
                if (!Accept('^')) return value;
                Enter();
                decimal exponent = ParseUnary();
                _depth--;
                return Power(value, exponent);
            }

            // primary := number | '(' expression ')'
            private decimal ParsePrimary()
            {
                SkipSpaces();
                if (Accept('('))
                {
                    decimal value = ParseExpression();
                    SkipSpaces();
                    if (!Accept(')')) throw new EvalException(SyntaxError);
                    return value;
                }
                return ParseNumber();
            }

            private decimal ParseNumber()
            {
                int start = _pos;
                if (_pos + 1 < _text.Length && _text[_pos] == '0' && (_text[_pos + 1] == 'x' || _text[_pos + 1] == 'X'))
                {
                    _pos += 2;
                    int digits = _pos;
                    while (_pos < _text.Length && Uri.IsHexDigit(_text[_pos])) _pos++;
                    if (_pos == digits) throw new EvalException(SyntaxError);
                    if (!ulong.TryParse(_text.AsSpan(digits, _pos - digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex))
                        throw new EvalException(NumberTooLarge);
                    return hex;
                }

                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
                if (_pos < _text.Length && _text[_pos] == '.')
                {
                    _pos++;
                    while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
                }
                string number = _text.Substring(start, _pos - start);
                if (number.Length == 0 || number == ".") throw new EvalException(SyntaxError);
                if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                    throw new EvalException(NumberTooLarge);
                return value;
            }

            private bool Accept(char c)
            {
                if (_pos < _text.Length && _text[_pos] == c)
                {
                    _pos++;
                    return true;
                }
                return false;
            }

            private void Enter()
            {
                if (++_depth > MaxDepth) throw new EvalException(TooDeep);
            }

            /// <summary>
            /// A whole exponent is exact (by squaring; decimal throws on overflow, which is reported
            /// as too large). A negative one raises the reciprocal, so 2^-100 is a tiny number (shown
            /// as 0), never "too large", and 0.1^-100 is too large, never a division by zero. Any other
            /// exponent goes through double: a negative base then has no real result.
            /// </summary>
            private static decimal Power(decimal value, decimal exponent)
            {
                if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= 10_000)
                {
                    long n = (long)exponent;
                    if (n < 0)
                    {
                        if (value == 0m) throw new EvalException(DivisionByZero);
                        value = 1m / value;
                        n = -n;
                    }
                    decimal result = 1m;
                    decimal factor = value;
                    while (n > 0)
                    {
                        if ((n & 1) == 1) result *= factor;
                        n >>= 1;
                        if (n > 0) factor *= factor;   // only while a higher bit is left, so it overflows only when the result would
                    }
                    return result;
                }

                double d = Math.Pow((double)value, (double)exponent);
                if (double.IsNaN(d)) throw new EvalException(NotReal);
                if (double.IsInfinity(d) || Math.Abs(d) > (double)decimal.MaxValue) throw new EvalException(ResultTooLarge);
                return (decimal)d;
            }
        }
    }
}
