using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools ▸ Evaluate: MicaPad's own arithmetic, precedence first, every problem a message.</summary>
    public class ExpressionEvaluatorTests
    {
        [Theory]
        [InlineData("1+2*3", "7")]
        [InlineData("(1+2)*3", "9")]
        [InlineData("100-10-1", "89")]                  // left-associative
        [InlineData("64/4/2", "8")]
        [InlineData("2^3^2", "512")]                    // right-associative: 2^(3^2)
        [InlineData("-2^2", "-4")]                      // ^ binds tighter than unary minus
        [InlineData("(-2)^2", "4")]
        [InlineData("2^-1", "0.5")]
        [InlineData("2^-100", "0")]                     // tiny, not too large: nothing within 15 decimals
        [InlineData("10^-3", "0.001")]
        [InlineData("2*-3", "-6")]
        [InlineData("--3", "3")]
        [InlineData("-3+5", "2")]
        [InlineData("10%4", "2")]
        [InlineData("7/2", "3.5")]
        [InlineData("0.1+0.2", "0.3")]                  // decimal, not double
        [InlineData("1/3", "0.333333333333333")]        // at most 15 decimals
        [InlineData(".5+.5", "1")]
        [InlineData("1.5*1.07", "1.605")]
        [InlineData("0xFF+1", "256")]
        [InlineData("0x10*2", "32")]
        [InlineData("4^0.5", "2")]
        [InlineData("2^64", "18446744073709551616")]     // exact
        [InlineData("0^0", "1")]
        [InlineData(" 3 * ( 4 - 1 ) ", "9")]
        public void Evaluates(string expression, string expected)
        {
            var (value, problem) = ExpressionEvaluator.Evaluate(expression);
            Assert.Null(problem);
            Assert.Equal(expected, ExpressionEvaluator.Format(value));
        }

        [Theory]
        [InlineData("1/0", ExpressionEvaluator.DivisionByZero)]
        [InlineData("5%0", ExpressionEvaluator.DivisionByZero)]
        [InlineData("0^-1", ExpressionEvaluator.DivisionByZero)]
        [InlineData("1+", ExpressionEvaluator.SyntaxError)]
        [InlineData("(1+2", ExpressionEvaluator.SyntaxError)]
        [InlineData("1+2)", ExpressionEvaluator.SyntaxError)]
        [InlineData("2 3", ExpressionEvaluator.SyntaxError)]
        [InlineData("2x", ExpressionEvaluator.SyntaxError)]
        [InlineData("abc", ExpressionEvaluator.SyntaxError)]
        [InlineData("1,000+1", ExpressionEvaluator.SyntaxError)]
        [InlineData("0x", ExpressionEvaluator.SyntaxError)]
        [InlineData(".", ExpressionEvaluator.SyntaxError)]
        [InlineData("", ExpressionEvaluator.SyntaxError)]
        [InlineData("2+2 = 4", ExpressionEvaluator.SyntaxError)]
        [InlineData("2^1000", ExpressionEvaluator.ResultTooLarge)]
        [InlineData("0.1^-100", ExpressionEvaluator.ResultTooLarge)]                          // 10^100, not a division by zero
        [InlineData("79228162514264337593543950335+1", ExpressionEvaluator.ResultTooLarge)]   // decimal.MaxValue + 1
        [InlineData("99999999999999999999999999999", ExpressionEvaluator.NumberTooLarge)]     // 29 digits
        [InlineData("0x1FFFFFFFFFFFFFFFF", ExpressionEvaluator.NumberTooLarge)]
        [InlineData("(-8)^(1/3)", ExpressionEvaluator.NotReal)]
        public void Reports_problems_instead_of_throwing(string expression, string problem)
        {
            Assert.Equal(problem, ExpressionEvaluator.Evaluate(expression).Problem);
        }

        [Fact]
        public void Deep_nesting_is_an_error_not_a_crash()
        {
            string parentheses = new string('(', 10_000) + "1" + new string(')', 10_000);
            string minuses = new string('-', 10_000) + "1";
            string powers = string.Join("^", Enumerable.Repeat("1", 10_000));

            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(parentheses).Problem);
            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(minuses).Problem);
            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(powers).Problem);
            Assert.Equal("1", ExpressionEvaluator.Format(ExpressionEvaluator.Evaluate(new string('(', 50) + "1" + new string(')', 50)).Value));
        }

        [Fact]
        public void The_result_never_reads_minus_zero()
        {
            Assert.Equal("0", ExpressionEvaluator.Format(ExpressionEvaluator.Evaluate("-0").Value));
            Assert.Equal("0", ExpressionEvaluator.Format(-0.0000000000000001m));
        }
    }
}
