using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How one run of a Markdown line is shown (spec 2.3). Markers stay visible, dimmed.</summary>
    public enum MdStyle
    {
        Marker,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Bold,
        Italic,
        BoldItalic,
        Strike,
        Code,
        LinkText,
        ListMarker,
        TaskDone,
        QuoteText,
        CodeBlock,
    }

    /// <summary>What kind of line it is; the background renderer draws quote bars, code shading and rules from it.</summary>
    public enum MdBlock
    {
        Paragraph,
        Heading,
        Quote,
        Fence,
        Rule,
        Bullet,
        Numbered,
        Task,
    }

    /// <summary>Whether a line belongs to a fenced code block.</summary>
    public enum MdFence
    {
        None,
        Delimiter,
        Inside,
    }

    /// <summary>A run of a line: characters <see cref="Start"/> to <see cref="Start"/> + <see cref="Length"/>.</summary>
    public readonly record struct MdSpan(int Start, int Length, MdStyle Style);

    /// <summary>
    /// One tokenized line. <see cref="Spans"/> are in application order — block style, then inline
    /// styles, then markers — and may overlap (bold inside a heading).
    /// </summary>
    public sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans);

    /// <summary>
    /// Markdown styled source, one line at a time: headings, emphasis, code, links, lists, tasks,
    /// quotes, rules. Deliberately smaller than CommonMark — unclosed or ambiguous markers stay
    /// plain text — and never changes the text, only says how to show it.
    /// </summary>
    public static class MarkdownLineTokenizer
    {
        /// <summary>
        /// Lines longer than this keep their block style but get no inline formatting, so a
        /// pathological line (thousands of unmatched markers) cannot stall typing.
        /// </summary>
        public const int MaxInlineLength = 4000;

        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.CultureInvariant);
        private static readonly Regex RuleRx = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[ \t]*([-*+])[ \t]+(\[[ xX]\])(?=[ \t]|$)[ \t]*", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[ \t]*([-*+])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^[ \t]*(\d{1,9}[.)])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}(>)[ \t]?", RegexOptions.CultureInvariant);

        /// <summary>The line's styled runs.</summary>
        public static MdLine Tokenize(string line, MdFence fence)
        {
            line ??= "";
            var spans = new List<MdSpan>();
            MdBlock block = Classify(line, fence, out Match? m);
            switch (block)
            {
                case MdBlock.Fence:
                    Add(spans, 0, line.Length, fence == MdFence.Delimiter ? MdStyle.Marker : MdStyle.CodeBlock);
                    break;
                case MdBlock.Rule:
                    Add(spans, 0, line.Length, MdStyle.Marker);
                    break;
                case MdBlock.Heading:
                    Add(spans, 0, line.Length, MdStyle.Heading1 + (m!.Groups[1].Length - 1));
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, m.Groups[1].Length, MdStyle.Marker);
                    break;
                case MdBlock.Quote:
                    Add(spans, m!.Length, line.Length - m.Length, MdStyle.QuoteText);
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, 1, MdStyle.Marker);
                    break;
                case MdBlock.Task:
                    Add(spans, m!.Groups[1].Index, 1, MdStyle.ListMarker);
                    if (m.Groups[2].Value[1] != ' ') Add(spans, m.Length, line.Length - m.Length, MdStyle.TaskDone);
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[2].Index, 3, MdStyle.Marker);
                    break;
                case MdBlock.Bullet:
                case MdBlock.Numbered:
                    Add(spans, m!.Groups[1].Index, m.Groups[1].Length, MdStyle.ListMarker);
                    Inline(line, m.Length, line.Length, spans);
                    break;
                default:
                    Inline(line, 0, line.Length, spans);
                    break;
            }
            return new MdLine(block, spans);
        }

        /// <summary>The kind of a line without its inline runs; cheap enough to call per visible line.</summary>
        public static MdBlock BlockOf(string line, MdFence fence) => Classify(line ?? "", fence, out _);

        /// <summary>Where the list marker (<c>-</c>, <c>*</c>, <c>+</c>) of a bullet or task line is, or -1.</summary>
        public static int BulletOffset(string line)
        {
            MdBlock block = Classify(line ?? "", MdFence.None, out Match? m);
            return block is MdBlock.Bullet or MdBlock.Task ? m!.Groups[1].Index : -1;
        }

        private static MdBlock Classify(string line, MdFence fence, out Match? match)
        {
            match = null;
            if (fence != MdFence.None) return MdBlock.Fence;
            if (RuleRx.IsMatch(line)) return MdBlock.Rule;
            if ((match = HeadingRx.Match(line)).Success) return MdBlock.Heading;
            if ((match = QuoteRx.Match(line)).Success) return MdBlock.Quote;
            if ((match = TaskRx.Match(line)).Success) return MdBlock.Task;
            if ((match = BulletRx.Match(line)).Success) return MdBlock.Bullet;
            if ((match = NumberedRx.Match(line)).Success) return MdBlock.Numbered;
            match = null;
            return MdBlock.Paragraph;
        }

        private static void Add(List<MdSpan> spans, int start, int length, MdStyle style)
        {
            if (length > 0) spans.Add(new MdSpan(start, length, style));
        }

        /// <summary>Code spans, links and emphasis between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static void Inline(string s, int start, int end, List<MdSpan> spans)
        {
            if (s.Length > MaxInlineLength) return;
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1])) { i += 2; continue; }
                if (c == '`') { i = CodeSpan(s, i, end, spans); continue; }
                if (c == '[' && TryLink(s, i, end, spans, out int afterLink)) { i = afterLink; continue; }
                if (c is '*' or '_' or '~') { i = Emphasis(s, i, end, spans); continue; }
                i++;
            }
        }

        private static bool IsEscapable(char c) => "\\`*_{}[]()#+-.!~>|".IndexOf(c) >= 0;

        private static int Run(string s, int i, int end, char c)
        {
            int j = i;
            while (j < end && s[j] == c) j++;
            return j - i;
        }

        /// <summary>A run of backticks closed by a run of the same length; unclosed backticks are text.</summary>
        private static int CodeSpan(string s, int i, int end, List<MdSpan> spans)
        {
            int n = Run(s, i, end, '`');
            int close = FindCodeClose(s, i + n, end, n);
            if (close < 0) return i + n;
            Add(spans, i, n, MdStyle.Marker);
            Add(spans, i + n, close - i - n, MdStyle.Code);
            Add(spans, close, n, MdStyle.Marker);
            return close + n;
        }

        /// <summary>The start of the next run of exactly <paramref name="n"/> backticks, or -1.</summary>
        private static int FindCodeClose(string s, int from, int end, int n)
        {
            int j = from;
            while (j < end)
            {
                if (s[j] != '`') { j++; continue; }
                int m = Run(s, j, end, '`');
                if (m == n) return j;
                j += m;
            }
            return -1;
        }

        /// <summary><c>[text](address)</c>: the text in the link color, the brackets and address dimmed.</summary>
        private static bool TryLink(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int close = s.IndexOf(']', i + 1, end - i - 1);
            if (close < 0 || close + 1 >= end || s[close + 1] != '(') return false;
            int paren = s.IndexOf(')', close + 2, end - close - 2);
            if (paren < 0) return false;

            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, close - i - 1, MdStyle.LinkText);
            Inline(s, i + 1, close, spans);
            Add(spans, close, paren - close + 1, MdStyle.Marker);
            after = paren + 1;
            return true;
        }

        /// <summary>
        /// <c>*</c>/<c>_</c> runs of one to three (italic, bold, both) and <c>~~</c> (strike). A
        /// closing run of the same length wins; a longer closing run is used only when no exact one
        /// follows (<c>**bold *italic***</c>). Returns where scanning continues.
        /// </summary>
        private static int Emphasis(string s, int i, int end, List<MdSpan> spans)
        {
            char c = s[i];
            int n = Run(s, i, end, c);
            if ((c == '~' && n != 2) || n > 3 || !CanOpen(s, i, n, end, c)) return i + n;

            int fallback = -1;
            int fallbackRun = 0;
            int j = i + n;
            while (j < end)
            {
                char d = s[j];
                if (d == '\\' && j + 1 < end) { j += 2; continue; }
                if (d == '`')
                {
                    int ticks = Run(s, j, end, '`');
                    int codeClose = FindCodeClose(s, j + ticks, end, ticks);
                    j = codeClose < 0 ? j + ticks : codeClose + ticks;
                    continue;
                }
                if (d != c) { j++; continue; }

                int m = Run(s, j, end, c);
                if (CanClose(s, j, m, end, c))
                {
                    if (m == n) return Close(s, i, n, j, c, spans);
                    if (m > n && fallback < 0)
                    {
                        fallback = j;
                        fallbackRun = m;
                    }
                }
                j += m;
            }
            return fallback >= 0 ? Close(s, i, n, fallback + fallbackRun - n, c, spans) : i + n;
        }

        private static int Close(string s, int open, int n, int close, char c, List<MdSpan> spans)
        {
            Add(spans, open, n, MdStyle.Marker);
            Add(spans, open + n, close - open - n, StyleOf(c, n));
            Inline(s, open + n, close, spans);
            Add(spans, close, n, MdStyle.Marker);
            return close + n;
        }

        private static MdStyle StyleOf(char c, int n) =>
            c == '~' ? MdStyle.Strike : n switch { 1 => MdStyle.Italic, 2 => MdStyle.Bold, _ => MdStyle.BoldItalic };

        /// <summary>An opening run is followed by text; <c>_</c> also needs a word boundary before it.</summary>
        private static bool CanOpen(string s, int i, int n, int end, char c)
        {
            int after = i + n;
            if (after >= end || char.IsWhiteSpace(s[after])) return false;
            return c != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
        }

        /// <summary>A closing run follows text; <c>_</c> also needs a word boundary after it.</summary>
        private static bool CanClose(string s, int j, int m, int end, char c)
        {
            if (j == 0 || char.IsWhiteSpace(s[j - 1])) return false;
            int after = j + m;
            return c != '_' || after >= end || !char.IsLetterOrDigit(s[after]);
        }
    }
}
