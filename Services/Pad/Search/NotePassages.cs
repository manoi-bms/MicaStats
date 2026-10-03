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
    /// <see cref="MaxChars"/>. Only the first <see cref="MaxNoteChars"/> of a note are read, counted
    /// once its credential references are taken out.
    /// </summary>
    public static class NotePassages
    {
        public const int TargetChars = 800;
        public const int MaxChars = 1500;
        public const int MaxNoteChars = 2 * 1024 * 1024;
        public const int MaxFirstLineChars = 200;

        /// <summary>A heading's text is cut to this length, so a huge heading line is not copied whole into every piece cut from it.</summary>
        public const int MaxHeadingChars = 200;

        private const string HeadingSeparator = " › ";
        private const string Cleaned = "[credential]";
        private static readonly Regex Secret = new(SecretTokens.Pattern, RegexOptions.CultureInvariant);

        /// <summary>The start of a reference that is not whole: <c>{{secret:</c> and as much of the id as there is.</summary>
        private const string Opening = @"\{\{secret:" + SecretTokens.IdClass + "{0,8}";

        /// <summary>
        /// A reference cut short at the end of a text: <c>{{secret:</c> and as much of the id as
        /// was left. An automatic title is the first 30 characters of a note's first line
        /// (<see cref="NoteTitle.FromText"/>), which can end inside a reference.
        /// </summary>
        private static readonly Regex CutSecret = new(Opening + @"\}?\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// A reference cut at its end, anywhere in a text: <c>{{secret:</c>, as much of the id as
        /// there is, and what is there of the closing braces. Whole references are gone before
        /// this is used, so it never meets one.
        /// </summary>
        private static readonly Regex CutAtEnd = new(Opening + @"\}{0,2}", RegexOptions.CultureInvariant);

        /// <summary>
        /// A reference cut at its start, where a cut leaves it: at the very start of a text.
        /// What is left of the opening (<c>ecret:</c> and shorter) may come first, then the last
        /// 1 to 8 characters of the id and the closing braces. The same characters in the middle
        /// of a text are not a cut reference: <c>{{NAME}}</c>, <c>x^{2^{3}}</c> and nested JSON
        /// end the same way, and cleaning them would change what the user searches for or asks.
        /// </summary>
        private static readonly Regex CutAtStart = new(
            @"\A(?:\{secret:|secret:|ecret:|cret:|ret:|et:|t:|:)?" + SecretTokens.IdClass + @"{1,8}\}\}", RegexOptions.CultureInvariant);

        /// <summary>Credential references replaced by <c>[credential]</c>; the vault is never read.</summary>
        public static string WithoutSecrets(string text) => Secret.Replace(text, Cleaned);

        /// <summary>
        /// A title with its credential references replaced by <c>[credential]</c>, a reference
        /// cut short at its end too: half a reference is not a reference, so it would not be
        /// cleaned, and part of the credential's id would be indexed and sent.
        /// </summary>
        public static string TitleWithoutSecrets(string title) => CutSecret.Replace(WithoutSecrets(title), Cleaned);

        /// <summary>
        /// Text a user typed or selected (a question, an instruction, a search query) with its
        /// credential references replaced by <c>[credential]</c>, and a reference cut by a
        /// selection too: cut at its end (<c>{{secret:K7Q2</c>, anywhere in the text) or at its
        /// start (<c>M9XD}}</c>, which a selection leaves at the very start of the text). Text
        /// that holds no part of a reference comes back as it is, templates and JSON included.
        /// </summary>
        public static string WithoutSecretParts(string text) =>
            CutAtStart.Replace(CutAtEnd.Replace(WithoutSecrets(text), Cleaned), Cleaned);

        /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
        public static string HashOf(string sentText) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sentText))).ToLowerInvariant();

        /// <summary>The note's passages in order. Blank text has none. Credential references become <c>[credential]</c> in the title too.</summary>
        public static IReadOnlyList<Passage> Cut(string noteId, string title, string text)
        {
            // Cleaned before it is cut: a reference across the limit would be cut in half, and half
            // a reference is not cleaned, so part of the credential's id would be indexed and sent.
            text = WithoutSecrets(text);
            if (text.Length > MaxNoteChars) text = text.Substring(0, MaxNoteChars);
            title = TitleWithoutSecrets(title);
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

                var (level, headingText) = HeadingOf(line);
                if (level > 0)
                {
                    Flush(i - 1);
                    while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                    while (headings.Count < level - 1) headings.Add("");
                    headings.Add(headingText);
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

        /// <summary>
        /// A Markdown heading line's level and text, or level 0: up to 3 spaces, 1 to 6 <c>#</c>, then
        /// a space, a tab or the line's end. A closing run of <c>#</c> is dropped only after a space or
        /// tab (<c># C#</c> stays "C#"). The text is cut at <see cref="MaxHeadingChars"/>. One pass over
        /// the line, however long.
        /// </summary>
        private static (int Level, string Text) HeadingOf(string line)
        {
            static bool Blank(char c) => c == ' ' || c == '\t';

            int at = 0;
            while (at < 3 && at < line.Length && line[at] == ' ') at++;
            int level = 0;
            while (at + level < line.Length && line[at + level] == '#') level++;
            if (level == 0 || level > 6) return (0, "");
            int start = at + level;
            if (start < line.Length && !Blank(line[start])) return (0, "");

            int end = line.Length;
            while (end > start && Blank(line[end - 1])) end--;
            int run = end;
            while (run > start && line[run - 1] == '#') run--;
            if (run < end && (run == start || Blank(line[run - 1]))) end = run;   // the closing run
            while (end > start && Blank(line[end - 1])) end--;
            while (start < end && Blank(line[start])) start++;

            string text = line.Substring(start, Math.Min(end - start, MaxHeadingChars));
            return (level, text.Trim());
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
