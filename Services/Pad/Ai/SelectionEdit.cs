using System;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>The edit plans for Replace selection and Insert below (MicaPad AI spec 3.3).</summary>
    public static class SelectionEdit
    {
        private const char CR = (char)13;
        private const char LF = (char)10;

        /// <summary>Removes leading and trailing lines that are empty or whitespace only, and nothing else.</summary>
        public static string Clean(string result)
        {
            result ??= "";
            int start = 0;
            // Skip whole blank lines at the top, keeping the first text line's indentation.
            while (true)
            {
                int i = start;
                while (i < result.Length && result[i] != CR && result[i] != LF && char.IsWhiteSpace(result[i])) i++;
                if (i >= result.Length) return "";
                if (result[i] != CR && result[i] != LF) break;
                if (result[i] == CR && i + 1 < result.Length && result[i + 1] == LF) i++;
                start = i + 1;
            }
            // The last text line keeps its own trailing spaces; only the blank lines after it go.
            int last = result.Length - 1;
            while (last > start && char.IsWhiteSpace(result[last])) last--;
            int end = last + 1;
            while (end < result.Length && result[end] != CR && result[end] != LF) end++;
            return result.Substring(start, end - start);
        }

        public static TextEdit Replace(int offset, int length, string result) =>
            new(offset, length, result, offset, result.Length);

        public static TextEdit InsertBelow(string documentText, int endOffset, string result, string newline)
        {
            documentText ??= "";
            int at = Math.Clamp(endOffset, 0, documentText.Length);
            // Trailing line breaks (any number, CRLF never split) belong to the line before them.
            while (at > 0 && (documentText[at - 1] == LF || documentText[at - 1] == CR)) at--;
            while (at < documentText.Length && documentText[at] != CR && documentText[at] != LF) at++;
            string text = newline + newline + result;
            return new TextEdit(at, 0, text, at + 2 * newline.Length, result.Length);
        }
    }
}
