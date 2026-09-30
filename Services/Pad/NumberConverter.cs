using System;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The bases Tools ▸ Convert number writes.</summary>
    public enum NumberBase
    {
        Decimal,
        Hex,
        Binary,
        Octal,
    }

    /// <summary>
    /// Tools ▸ Convert number (spec 5.1): one integer in decimal, <c>0x</c> hex, <c>0b</c> binary or
    /// <c>0o</c> octal, with an optional sign and underscores between digits, written in another
    /// base. Any value that fits in 64 bits, signed or unsigned: −2⁶³ to 2⁶⁴−1. A negative number
    /// keeps its sign in every base (<c>-0x3</c>), never a two's complement. Only ASCII digits count.
    /// </summary>
    public static class NumberConverter
    {
        public const string NotANumber = "Not a whole number (try 255, 0xFF, 0b1010 or 0o17)";
        public const string TooLarge = "That number does not fit in 64 bits";

        /// <summary>Reads one integer; false with the reason when <paramref name="text"/> is not one that fits in 64 bits.</summary>
        public static bool TryParse(string text, out bool negative, out ulong magnitude, out string? problem)
        {
            negative = false;
            magnitude = 0;
            problem = NotANumber;

            string s = text.Trim();
            int i = 0;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                negative = s[i] == '-';
                i++;
            }

            int radix = 10;
            if (i + 1 < s.Length && s[i] == '0')
            {
                radix = char.ToLowerInvariant(s[i + 1]) switch { 'x' => 16, 'b' => 2, 'o' => 8, _ => 10 };
                if (radix != 10) i += 2;
            }

            string digits = s.Substring(i);
            // Underscores only between digits: 1_000 and 0xFF_FF, not _1, 1_ or 0x_FF.
            if (digits.Length == 0 || digits[0] == '_' || digits[^1] == '_') return false;

            foreach (char c in digits)
            {
                if (c == '_') continue;
                int digit = DigitValue(c);
                if (digit < 0 || digit >= radix) return false;
                try
                {
                    magnitude = checked(magnitude * (ulong)radix + (ulong)digit);
                }
                catch (OverflowException)
                {
                    problem = TooLarge;
                    return false;
                }
            }

            if (negative && magnitude > 1UL << 63)
            {
                problem = TooLarge;
                return false;
            }
            if (magnitude == 0) negative = false;   // "-0" is 0
            problem = null;
            return true;
        }

        /// <summary>The number in <paramref name="target"/>: <c>255</c>, <c>0xFF</c>, <c>0b11111111</c>, <c>0o377</c>.</summary>
        public static string Format(bool negative, ulong magnitude, NumberBase target)
        {
            string body = target switch
            {
                NumberBase.Hex => "0x" + magnitude.ToString("X", CultureInfo.InvariantCulture),
                NumberBase.Binary => "0b" + InBase(magnitude, 2),
                NumberBase.Octal => "0o" + InBase(magnitude, 8),
                _ => magnitude.ToString(CultureInfo.InvariantCulture),
            };
            return negative ? "-" + body : body;
        }

        /// <summary>One number converted, or why it cannot be: for the Tools menu.</summary>
        public static (string? Text, string? Problem) Convert(string text, NumberBase target) =>
            TryParse(text, out bool negative, out ulong magnitude, out string? problem)
                ? (Format(negative, magnitude, target), null)
                : (null, problem);

        private static int DigitValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static string InBase(ulong value, int radix)
        {
            if (value == 0) return "0";
            var sb = new StringBuilder();
            while (value > 0)
            {
                sb.Insert(0, (char)('0' + (int)(value % (ulong)radix)));
                value /= (ulong)radix;
            }
            return sb.ToString();
        }
    }
}
