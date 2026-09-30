using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools ▸ Convert number: one integer in any of four bases, into another.</summary>
    public class NumberConverterTests
    {
        [Theory]
        [InlineData("255", NumberBase.Hex, "0xFF")]
        [InlineData("255", NumberBase.Binary, "0b11111111")]
        [InlineData("255", NumberBase.Octal, "0o377")]
        [InlineData("0xFF", NumberBase.Decimal, "255")]
        [InlineData("0XfF", NumberBase.Decimal, "255")]
        [InlineData("0b1010", NumberBase.Decimal, "10")]
        [InlineData("0o17", NumberBase.Decimal, "15")]
        [InlineData("-3", NumberBase.Decimal, "-3")]
        [InlineData("-3", NumberBase.Hex, "-0x3")]
        [InlineData("-3", NumberBase.Binary, "-0b11")]
        [InlineData("+42", NumberBase.Hex, "0x2A")]
        [InlineData(" 42 ", NumberBase.Hex, "0x2A")]
        [InlineData("1_000_000", NumberBase.Hex, "0xF4240")]
        [InlineData("0xFFFF_FFFF", NumberBase.Decimal, "4294967295")]
        [InlineData("007", NumberBase.Decimal, "7")]
        [InlineData("0", NumberBase.Binary, "0b0")]
        [InlineData("-0", NumberBase.Hex, "0x0")]
        [InlineData("18446744073709551615", NumberBase.Hex, "0xFFFFFFFFFFFFFFFF")]      // 2^64 - 1
        [InlineData("0xFFFFFFFFFFFFFFFF", NumberBase.Decimal, "18446744073709551615")]
        [InlineData("-9223372036854775808", NumberBase.Hex, "-0x8000000000000000")]     // -2^63
        [InlineData("-0x8000000000000000", NumberBase.Decimal, "-9223372036854775808")]
        public void Converts(string text, NumberBase target, string expected)
        {
            var (result, problem) = NumberConverter.Convert(text, target);
            Assert.Null(problem);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("18446744073709551616", NumberConverter.TooLarge)]          // 2^64
        [InlineData("0x1_0000_0000_0000_0000", NumberConverter.TooLarge)]       // 2^64 in hex
        [InlineData("-9223372036854775809", NumberConverter.TooLarge)]          // -2^63 - 1
        [InlineData("12ab", NumberConverter.NotANumber)]
        [InlineData("0x", NumberConverter.NotANumber)]
        [InlineData("0b102", NumberConverter.NotANumber)]
        [InlineData("1.5", NumberConverter.NotANumber)]
        [InlineData("_1", NumberConverter.NotANumber)]
        [InlineData("1_", NumberConverter.NotANumber)]
        [InlineData("0x_FF", NumberConverter.NotANumber)]
        [InlineData("--5", NumberConverter.NotANumber)]
        [InlineData("1 000", NumberConverter.NotANumber)]
        [InlineData("๒๕๕", NumberConverter.NotANumber)]                          // Thai digits are not ASCII digits
        [InlineData("", NumberConverter.NotANumber)]
        public void Refuses(string text, string problem)
        {
            var (result, reason) = NumberConverter.Convert(text, NumberBase.Hex);
            Assert.Null(result);
            Assert.Equal(problem, reason);
        }
    }
}
