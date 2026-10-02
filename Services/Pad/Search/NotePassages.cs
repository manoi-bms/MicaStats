using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Cuts a note into passages (spec 3.1): at Markdown headings and blank lines, packed up to
    /// about <see cref="TargetChars"/>, never over <see cref="MaxChars"/>. A fenced code block stays
    /// whole when it fits; anything longer is cut at line boundaries, and a single longer line at
    /// <see cref="MaxChars"/>. Only the first <see cref="MaxNoteChars"/> of a note are read.
    /// </summary>
    public static class NotePassages
    {
        public const int TargetChars = 800;
        public const int MaxChars = 1500;
        public const int MaxNoteChars = 2 * 1024 * 1024;
        public const int MaxFirstLineChars = 200;

        private const string HeadingSeparator = " › ";
        private static readonly Regex Secret = new(SecretTokens.Pattern, RegexOptions.CultureInvariant);
        private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant);

        /// <summary>Credential references replaced by <c>[credential]</c>; the vault is never read.</summary>
        public static string WithoutSecrets(string text) => Secret.Replace(text, "[credential]");

        /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
        public static string HashOf(string sentText) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sentText))).ToLowerInvariant();

        /// <summary>The note's passages in order. Blank text has none.</summary>
        public static IReadOnlyList<Passage> Cut(string noteId, string title, string text)
        {
            if (text.Length > MaxNoteChars) text = text.Substring(0, MaxNoteChars);
            text = WithoutSecrets(text);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var passages = new List<Passage>();
            int first = -1, last = -1;
            string heading = "";

            // A passage's body is the note's own lines first..last, blank lines included.
            string Span(int from, int to) => string.Join("\n", lines, from, to - from + 1);

            void Emit()
            {
                if (first >= 0)
                {
                    string body = Span(first, last);
                    if (body.Trim().Length > 0) passages.Add(Make(noteId, title, heading, first, last, lines, body));
                }
                first = -1;
            }

            foreach (var block in Blocks(lines))
            {
                if (block.StartsSection) Emit();

                if (Span(block.First, block.Last).Length > MaxChars)
                {
                    Emit();
                    foreach (var piece in Pieces(lines, block.First, block.Last))
                        passages.Add(Make(noteId, title, block.Heading, piece.First, piece.Last, lines, piece.Text));
                    continue;
                }

                if (first >= 0 && Span(first, block.Last).Length > TargetChars) Emit();
                if (first < 0)
                {
                    first = block.First;
                    heading = block.Heading;
                }
                last = block.Last;
            }
            Emit();
            return passages;
        }

        private static Passage Make(string noteId, string title, string heading, int first, int last, string[] lines, string body)
        {
            string sent = (heading.Length > 0 ? title + HeadingSeparator + heading : title) + "\n\n" + body;
            // Trim a bounded prefix only: a 2 MB line is cut into hundreds of pieces that all start on it.
            string line = lines[first];
            if (line.Length > MaxFirstLineChars * 4) line = line.Substring(0, MaxFirstLineChars * 4);
            string firstLine = line.Trim();
            if (firstLine.Length > MaxFirstLineChars) firstLine = firstLine.Substring(0, MaxFirstLineChars);
            return new Passage(noteId, title, heading, first + 1, last + 1, firstLine, body, sent, HashOf(sent));
        }

        private readonly record struct Block(int First, int Last, string Heading, bool StartsSection);

        /// <summary>Paragraphs, headings (each starting a section) and fenced blocks, 0-based lines.</summary>
        private static List<Block> Blocks(string[] lines)
        {
            var blocks = new List<Block>();
            var headings = new List<string>();
            int start = -1;

            string Path() => string.Join(HeadingSeparator, headings.Where(h => h.Length > 0));

            void Flush(int end)
            {
                if (start >= 0) blocks.Add(new Block(start, end, Path(), false));
                start = -1;
            }

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (FenceOf(line) is string fence)
                {
                    Flush(i - 1);
                    int end = i + 1;
                    while (end < lines.Length && !lines[end].TrimStart().StartsWith(fence, StringComparison.Ordinal)) end++;
                    if (end >= lines.Length) end = lines.Length - 1;
                    blocks.Add(new Block(i, end, Path(), false));
                    i = end + 1;
                    continue;
                }

                var match = Heading.Match(line);
                if (match.Success)
                {
                    Flush(i - 1);
                    int level = match.Groups[1].Length;
                    while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                    while (headings.Count < level - 1) headings.Add("");
                    headings.Add(match.Groups[2].Value.Trim());
                    blocks.Add(new Block(i, i, Path(), true));
                    i++;
                    continue;
                }

                if (line.Trim().Length == 0) Flush(i - 1);
                else if (start < 0) start = i;
                i++;
            }
            Flush(lines.Length - 1);
            return blocks;
        }

        /// <summary>The fence that opens a code block on this line (``` or ~~~, three or more), or null.</summary>
        private static string? FenceOf(string line)
        {
            string t = line.TrimStart();
            if (t.Length < 3 || (t[0] != '`' && t[0] != '~')) return null;
            int n = 0;
            while (n < t.Length && t[n] == t[0]) n++;
            return n >= 3 ? t.Substring(0, n) : null;
        }

        /// <summary>Lines first..last cut at line boundaries into pieces of at most <see cref="MaxChars"/>.</summary>
        private static IEnumerable<(int First, int Last, string Text)> Pieces(string[] lines, int first, int last)
        {
            var text = new StringBuilder();
            int pieceFirst = first;
            for (int i = first; i <= last; i++)
            {
                string line = lines[i];
                if (line.Length > MaxChars)
                {
                    if (text.Length > 0) { yield return (pieceFirst, i - 1, text.ToString()); text.Clear(); }
                    for (int at = 0; at < line.Length; at += MaxChars)
                        yield return (i, i, line.Substring(at, Math.Min(MaxChars, line.Length - at)));
                    continue;
                }
                if (text.Length > 0 && text.Length + 1 + line.Length > MaxChars)
                {
                    yield return (pieceFirst, i - 1, text.ToString());
                    text.Clear();
                }
                if (text.Length == 0) pieceFirst = i;
                else text.Append('\n');
                text.Append(line);
            }
            if (text.ToString().Trim().Length > 0) yield return (pieceFirst, last, text.ToString());
        }
    }
}
