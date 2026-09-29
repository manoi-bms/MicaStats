using System.Globalization;
using System.IO;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// A note's tab title. Scratch notes are named by what they say, so a restart full of tabs
    /// reads as "Shopping list", "Server IPs" — not "Untitled 1..9".
    /// </summary>
    public static class NoteTitle
    {
        /// <summary>Longest title taken from text.</summary>
        public const int MaxLength = 30;

        /// <summary>The first non-empty line, trimmed and cut to <see cref="MaxLength"/>; <c>Untitled N</c> when there is none.</summary>
        public static string FromText(string text, int untitledNumber)
        {
            int start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start == text.Length) return Untitled(untitledNumber);

            int end = start;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n' && end - start < MaxLength) end++;

            // Never cut between the two halves of a surrogate pair.
            if (end - start == MaxLength && char.IsHighSurrogate(text[end - 1])) end--;

            return text.Substring(start, end - start).TrimEnd();
        }

        /// <summary>The placeholder title of an empty scratch note.</summary>
        public static string Untitled(int number) => "Untitled " + number.ToString(CultureInfo.InvariantCulture);

        /// <summary>A file-backed note's title.</summary>
        public static string ForFile(string path) => Path.GetFileName(path);
    }
}
