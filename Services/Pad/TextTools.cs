using System;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a tool does to the note: one edit, or why it cannot apply (the text is then left alone).</summary>
    public readonly record struct ToolOutcome(TextEdit? Edit, string? Problem)
    {
        public static ToolOutcome Fail(string problem) => new(null, problem);

        public static ToolOutcome Apply(TextEdit edit) => new(edit, null);
    }

    /// <summary>
    /// The Tools menu (spec 5.1) as edits over the note's text: Base64, GUIDs, timestamps, and the
    /// shapes the number and evaluate tools take. Pure: each tool returns one <see cref="TextEdit"/>
    /// for the window to apply as a single undoable change, or a problem for the status bar.
    /// Selection tools leave the spaces and line breaks around the selection where they are and work
    /// on what lies between (a line selected with its line break keeps the break).
    /// </summary>
    public static class TextTools
    {
        public const string SelectFirst = "Select some text first";
        public const string NotBase64 = "Not valid Base64";
        public const string NotUtf8 = "Not UTF-8 text";
        public const string DecodesToNothing = "Decodes to empty text";

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>The UTF-8 bytes of <paramref name="text"/> in Base64.</summary>
        public static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Base64 back to UTF-8 text. Line breaks and spaces inside are ignored (Base64 is often
        /// wrapped), missing padding is added, and the URL-safe alphabet (<c>-</c>, <c>_</c>) is read
        /// too. A leading byte order mark is dropped. Bytes that are not UTF-8, or that decode to
        /// control characters other than tab and line breaks (binary data), are not text. Nothing
        /// left (a byte order mark alone) is <see cref="DecodesToNothing"/>: the selection is not
        /// replaced by empty text.
        /// </summary>
        public static (string? Text, string? Problem) Base64Decode(string text)
        {
            var sb = new StringBuilder(text.Length + 3);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
            while (sb.Length % 4 != 0) sb.Append('=');

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(sb.ToString());
            }
            catch (FormatException)
            {
                return (null, NotBase64);
            }

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (null, NotUtf8);
            }

            if (decoded.Length > 0 && decoded[0] == '\uFEFF') decoded = decoded.Substring(1);
            if (decoded.Length == 0) return (null, DecodesToNothing);
            foreach (char c in decoded)
                if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') return (null, NotUtf8);
            return (decoded, null);
        }

        /// <summary>A GUID the way MicaPad inserts it: lowercase, <c>D</c> format.</summary>
        public static string FormatGuid(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture);

        /// <summary><c>2026-09-30T18:05:12+07:00</c>: Gregorian and invariant whatever the culture (th-TH would write 2569).</summary>
        public static string Iso8601(DateTimeOffset now) => now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary><c>2026-09-30</c>.</summary>
        public static string Date(DateTimeOffset now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary><c>1790766312</c>.</summary>
        public static string UnixSeconds(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Runs <paramref name="transform"/> on the selection without the whitespace around it and
        /// replaces just that part; the result is selected. Nothing but whitespace selected:
        /// <see cref="SelectFirst"/>.
        /// </summary>
        public static ToolOutcome OnSelection(string text, int start, int length, Func<string, (string? Text, string? Problem)> transform)
        {
            var (from, count) = Core(text, start, length);
            if (count == 0) return ToolOutcome.Fail(SelectFirst);
            var (result, problem) = transform(text.Substring(from, count));
            if (result == null) return ToolOutcome.Fail(problem ?? SelectFirst);
            return ToolOutcome.Apply(new TextEdit(from, count, result, from, result.Length));
        }

        /// <summary>Puts <paramref name="inserted"/> at the caret, replacing any selection, and leaves the caret after it.</summary>
        public static ToolOutcome Insert(int start, int length, string inserted) =>
            ToolOutcome.Apply(new TextEdit(start, length, inserted, start + inserted.Length, 0));

        /// <summary>Evaluate: appends <c> = result</c> right after the selected expression and selects the result.</summary>
        public static ToolOutcome Evaluate(string text, int start, int length)
        {
            var (from, count) = Core(text, start, length);
            if (count == 0) return ToolOutcome.Fail(SelectFirst);
            var (value, problem) = ExpressionEvaluator.Evaluate(text.Substring(from, count));
            if (problem != null) return ToolOutcome.Fail(problem);
            string result = ExpressionEvaluator.Format(value);
            int end = from + count;
            return ToolOutcome.Apply(new TextEdit(end, 0, " = " + result, end + 3, result.Length));
        }

        /// <summary>The selection without the whitespace at either end, as (start, length).</summary>
        private static (int Start, int Length) Core(string text, int start, int length)
        {
            int from = Math.Clamp(start, 0, text.Length);
            int end = Math.Clamp(start + length, from, text.Length);
            while (from < end && char.IsWhiteSpace(text[from])) from++;
            while (end > from && char.IsWhiteSpace(text[end - 1])) end--;
            return (from, end - from);
        }
    }
}
