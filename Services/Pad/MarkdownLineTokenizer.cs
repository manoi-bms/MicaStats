using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How one run of a Markdown line is shown (spec 2.3, and the Wiki.js spec 1-5). Markers stay visible, dimmed.</summary>
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
        TableHeader,
        FootnoteRef,
        Subscript,
        Superscript,
        KbdText,
        Abbreviation,
        MathText,
    }

    /// <summary>What kind of line it is; the background renderer draws quote bars, code shading, callouts and rules from it.</summary>
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
        Table,
        FrontMatter,
        SetextUnderline,
        CalloutClass,
        Definition,
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
    /// styles, then markers — and may overlap (bold inside a heading). <see cref="QuoteDepth"/> is
    /// the number of quote levels (0 for other lines).
    /// </summary>
    public sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans, int QuoteDepth = 0);

    /// <summary>
    /// Markdown styled source, one line at a time, told by <see cref="MdLineFacts"/> what the
    /// document says about the line (fences, tables, setext headings, front matter, callouts).
    /// Deliberately smaller than CommonMark — unclosed or ambiguous markers stay plain text — and
    /// never changes the text, only says how to show it.
    /// </summary>
    public static class MarkdownLineTokenizer
    {
        /// <summary>
        /// Lines longer than this keep their block style but get no inline formatting, so a
        /// pathological line (thousands of unmatched markers) cannot stall typing.
        /// </summary>
        public const int MaxInlineLength = 4000;

        /// <summary>The deepest quote level drawn.</summary>
        public const int MaxQuoteDepth = 6;

        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.CultureInvariant);
        private static readonly Regex RuleRx = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[ \t]*([-*+])[ \t]+(\[[ xX]\])(?=[ \t]|$)[ \t]*", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[ \t]*([-*+])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^[ \t]*(\d{1,9}[.)])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}(>)[ \t]?", RegexOptions.CultureInvariant);
        private static readonly Regex FootnoteDefRx = new(@"^ {0,3}(\[\^[^\]\s]+\]:)", RegexOptions.CultureInvariant);
        private static readonly Regex AbbreviationDefRx = new(@"^(\*\[[^\]]+\]:)", RegexOptions.CultureInvariant);
        private static readonly Regex ReferenceDefRx = new(@"^ {0,3}(\[[^\]^][^\]]*\]:)[ \t]*(\S+)", RegexOptions.CultureInvariant);
        private static readonly Regex FootnoteRefRx = new(@"\G\[\^([^\]\s]+)\]", RegexOptions.CultureInvariant);
        private static readonly Regex KbdRx = new(@"\G<kbd>(.*?)</kbd>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTagRx = new(@"\G(?:<!--.*?-->|</?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?/?>)", RegexOptions.CultureInvariant);
        private static readonly Regex AttributesRx = new(@"\G\{[.#][^{}]*\}[ \t]*$", RegexOptions.CultureInvariant);

        /// <summary>The line's styled runs, the line alone deciding (no tables, setext headings or callouts).</summary>
        public static MdLine Tokenize(string line, MdFence fence) => Tokenize(line, new MdLineFacts(fence));

        /// <summary>
        /// The line's styled runs. <paramref name="abbreviations"/> are the document's defined terms;
        /// whole-word uses get a dotted underline.
        /// </summary>
        public static MdLine Tokenize(string line, MdLineFacts facts, IReadOnlySet<string>? abbreviations = null)
        {
            line ??= "";
            var spans = new List<MdSpan>();
            MdBlock block = Classify(line, facts, out Match? m);
            int depth = 0;
            switch (block)
            {
                case MdBlock.Fence:
                    Add(spans, 0, line.Length, facts.Fence == MdFence.Delimiter ? MdStyle.Marker : MdStyle.CodeBlock);
                    return new MdLine(block, spans);
                case MdBlock.Rule:
                case MdBlock.FrontMatter:
                case MdBlock.SetextUnderline:
                case MdBlock.CalloutClass:
                    Add(spans, 0, line.Length, MdStyle.Marker);
                    return new MdLine(block, spans);
                case MdBlock.Table:
                    TableLine(line, facts.Table, spans);
                    break;
                case MdBlock.Heading when facts.SetextLevel > 0:
                    Add(spans, 0, line.Length, facts.SetextLevel == 1 ? MdStyle.Heading1 : MdStyle.Heading2);
                    Inline(line, 0, line.Length, spans);
                    break;
                case MdBlock.Heading:
                    Add(spans, 0, line.Length, MdStyle.Heading1 + (m!.Groups[1].Length - 1));
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, m.Groups[1].Length, MdStyle.Marker);
                    break;
                case MdBlock.Quote:
                    depth = QuoteMarkers(line, out int text, spans);
                    Add(spans, text, line.Length - text, MdStyle.QuoteText);
                    Inline(line, text, line.Length, spans);
                    break;
                case MdBlock.Definition:
                    Definition(line, m!, spans);
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
            Abbreviations(line, spans, abbreviations);
            return new MdLine(block, spans, depth);
        }

        /// <summary>The kind of a line, the line alone deciding; cheap enough to call per visible line.</summary>
        public static MdBlock BlockOf(string line, MdFence fence) => BlockOf(line, new MdLineFacts(fence));

        /// <summary>The kind of a line without its inline runs.</summary>
        public static MdBlock BlockOf(string line, MdLineFacts facts) => Classify(line ?? "", facts, out _);

        /// <summary>Where the list marker (<c>-</c>, <c>*</c>, <c>+</c>) of a bullet or task line is, or -1.</summary>
        public static int BulletOffset(string line)
        {
            MdBlock block = Classify(line ?? "", new MdLineFacts(MdFence.None), out Match? m);
            return block is MdBlock.Bullet or MdBlock.Task ? m!.Groups[1].Index : -1;
        }

        /// <summary>How many quote levels a line opens with (<c>&gt; &gt; text</c> is 2), up to <see cref="MaxQuoteDepth"/>.</summary>
        public static int QuoteDepth(string line) => QuoteMarkers(line ?? "", out _, null);

        private static MdBlock Classify(string line, MdLineFacts facts, out Match? match)
        {
            match = null;
            if (facts.Fence != MdFence.None) return MdBlock.Fence;
            if (facts.FrontMatter) return MdBlock.FrontMatter;
            if (facts.Table != MdTableRole.None) return MdBlock.Table;
            if (facts.SetextUnderline) return MdBlock.SetextUnderline;
            if (facts.SetextLevel > 0) return MdBlock.Heading;
            if (facts.CalloutClass) return MdBlock.CalloutClass;
            if (RuleRx.IsMatch(line)) return MdBlock.Rule;
            if ((match = HeadingRx.Match(line)).Success) return MdBlock.Heading;
            if ((match = QuoteRx.Match(line)).Success) return MdBlock.Quote;
            if ((match = FootnoteDefRx.Match(line)).Success
                || (match = AbbreviationDefRx.Match(line)).Success
                || (match = ReferenceDefRx.Match(line)).Success) return MdBlock.Definition;
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

        /// <summary>
        /// Dims each <c>&gt;</c> of a quote (spaces allowed between them) and returns the depth;
        /// <paramref name="text"/> is where the quoted text starts (after one optional space).
        /// </summary>
        private static int QuoteMarkers(string line, out int text, List<MdSpan>? spans)
        {
            int i = 0;
            int depth = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            while (i < line.Length && line[i] == '>' && depth < MaxQuoteDepth)
            {
                if (spans != null) Add(spans, i, 1, MdStyle.Marker);
                depth++;
                i++;
                int j = i;
                while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                if (j < line.Length && line[j] == '>')
                {
                    i = j;
                    continue;
                }
                if (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                break;
            }
            text = i;
            return depth;
        }

        /// <summary>A table line: the header bold, the delimiter dimmed, every separating pipe dimmed.</summary>
        private static void TableLine(string line, MdTableRole role, List<MdSpan> spans)
        {
            if (role == MdTableRole.Delimiter)
            {
                Add(spans, 0, line.Length, MdStyle.Marker);
                return;
            }
            if (role == MdTableRole.Header) Add(spans, 0, line.Length, MdStyle.TableHeader);
            Inline(line, 0, line.Length, spans);
            if (line.Length <= MaxInlineLength)
                foreach (int pipe in TableCells.Pipes(line)) Add(spans, pipe, 1, MdStyle.Marker);
        }

        /// <summary><c>[^id]:</c>, <c>*[TERM]:</c> and <c>[id]: url</c> lines: the label dimmed, a reference's address in the link color.</summary>
        private static void Definition(string line, Match m, List<MdSpan> spans)
        {
            var label = m.Groups[1];
            int after = label.Index + label.Length;
            if (m.Groups.Count > 2 && m.Groups[2].Success)
            {
                Add(spans, m.Groups[2].Index, m.Groups[2].Length, MdStyle.LinkText);
                after = m.Groups[2].Index + m.Groups[2].Length;
            }
            Inline(line, after, line.Length, spans);
            Add(spans, label.Index, label.Length, MdStyle.Marker);
        }

        /// <summary>Code spans, math, tags, links, footnotes, emphasis and scripts between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static void Inline(string s, int start, int end, List<MdSpan> spans)
        {
            if (s.Length > MaxInlineLength) return;
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
                {
                    Add(spans, i, 1, MdStyle.Marker);
                    i += 2;
                    continue;
                }
                if (c == '`') { i = CodeSpan(s, i, end, spans); continue; }
                if (c == '$' && TryMath(s, i, end, spans, out int afterMath)) { i = afterMath; continue; }
                if (c == '<' && TryTag(s, i, end, spans, out int afterTag)) { i = afterTag; continue; }
                if (c == '{' && TryAttributes(s, i, end, spans)) { i = end; continue; }
                if (c == '!' && i + 1 < end && s[i + 1] == '[' && TryLink(s, i + 1, end, spans, out int afterImage))
                {
                    Add(spans, i, 1, MdStyle.Marker);
                    i = afterImage;
                    continue;
                }
                if (c == '[' && i + 1 < end && s[i + 1] == '^' && TryFootnote(s, i, end, spans, out int afterNote)) { i = afterNote; continue; }
                if (c == '[' && TryLink(s, i, end, spans, out int afterLink)) { i = afterLink; continue; }
                if (c is '*' or '_' or '~') { i = Emphasis(s, i, end, spans); continue; }
                if (c == '^') { i = Script(s, i, end, spans, '^', MdStyle.Superscript); continue; }
                i++;
            }
        }

        private static bool IsEscapable(char c) => "\\`*_{}[]()#+-.!~>|$^<=".IndexOf(c) >= 0;

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

        /// <summary>
        /// Inline math, <c>$…$</c> or <c>$$…$$</c>: the content must not start or end with a space
        /// and the closing dollar must not be followed by a digit, so "$5 and $10" stays text.
        /// </summary>
        private static bool TryMath(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int n = Run(s, i, end, '$');
            if (n > 2) return false;
            int open = i + n;
            if (open >= end || char.IsWhiteSpace(s[open])) return false;
            int j = open;
            while (j < end)
            {
                if (s[j] == '\\') { j += 2; continue; }
                if (s[j] != '$') { j++; continue; }
                int m = Run(s, j, end, '$');
                if (m == n && !char.IsWhiteSpace(s[j - 1]) && (j + m >= end || !char.IsDigit(s[j + m])))
                {
                    Add(spans, i, n, MdStyle.Marker);
                    Add(spans, open, j - open, MdStyle.MathText);
                    Add(spans, j, n, MdStyle.Marker);
                    after = j + n;
                    return true;
                }
                j += m;
            }
            return false;
        }

        /// <summary><c>&lt;kbd&gt;key&lt;/kbd&gt;</c> (the key on a key background) and other HTML tags and comments (dimmed).</summary>
        private static bool TryTag(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            var key = KbdRx.Match(s, i);
            if (key.Success && key.Index + key.Length <= end)
            {
                var inner = key.Groups[1];
                Add(spans, i, inner.Index - i, MdStyle.Marker);
                Add(spans, inner.Index, inner.Length, MdStyle.KbdText);
                Add(spans, inner.Index + inner.Length, key.Index + key.Length - inner.Index - inner.Length, MdStyle.Marker);
                after = key.Index + key.Length;
                return true;
            }
            var tag = HtmlTagRx.Match(s, i);
            if (!tag.Success || tag.Index + tag.Length > end) return false;
            Add(spans, i, tag.Length, MdStyle.Marker);
            after = i + tag.Length;
            return true;
        }

        /// <summary><c>{.class #id}</c> at the very end of the line (Wiki.js attributes, <c>{.tabset}</c>): dimmed.</summary>
        private static bool TryAttributes(string s, int i, int end, List<MdSpan> spans)
        {
            if (end != s.Length || !AttributesRx.IsMatch(s, i)) return false;
            Add(spans, i, end - i, MdStyle.Marker);
            return true;
        }

        /// <summary><c>[^id]</c>: the id small and raised in the link color, the brackets dimmed.</summary>
        private static bool TryFootnote(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            var note = FootnoteRefRx.Match(s, i);
            if (!note.Success || note.Index + note.Length > end) return false;
            var id = note.Groups[1];
            Add(spans, i, 2, MdStyle.Marker);
            Add(spans, id.Index, id.Length, MdStyle.FootnoteRef);
            Add(spans, id.Index + id.Length, 1, MdStyle.Marker);
            after = i + note.Length;
            return true;
        }

        /// <summary>
        /// <c>[text](address)</c>, <c>[text][id]</c> and <c>[text][]</c>: the text in the link color,
        /// the brackets and the address or id dimmed.
        /// </summary>
        private static bool TryLink(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int close = s.IndexOf(']', i + 1, end - i - 1);
            if (close < 0 || close + 1 >= end) return false;
            int stop;
            if (s[close + 1] == '(') stop = s.IndexOf(')', close + 2, end - close - 2);
            else if (s[close + 1] == '[') stop = s.IndexOf(']', close + 2, end - close - 2);
            else return false;
            if (stop < 0) return false;

            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, close - i - 1, MdStyle.LinkText);
            Inline(s, i + 1, close, spans);
            Add(spans, close, stop - close + 1, MdStyle.Marker);
            after = stop + 1;
            return true;
        }

        /// <summary>
        /// <c>*</c>/<c>_</c> runs of one to three (italic, bold, both), <c>~~</c> (strike) and a single
        /// <c>~</c> (subscript). A closing run of the same length wins; a longer closing run is used
        /// only when no exact one follows (<c>**bold *italic***</c>). Returns where scanning continues.
        /// </summary>
        private static int Emphasis(string s, int i, int end, List<MdSpan> spans)
        {
            char c = s[i];
            int n = Run(s, i, end, c);
            if (c == '~' && n == 1) return Script(s, i, end, spans, '~', MdStyle.Subscript);
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

        /// <summary><c>~sub~</c> and <c>^sup^</c>: one marker each side, no space inside, on one line.</summary>
        private static int Script(string s, int i, int end, List<MdSpan> spans, char c, MdStyle style)
        {
            int n = Run(s, i, end, c);
            if (n != 1) return i + n;
            int j = i + 1;
            while (j < end && s[j] != c && !char.IsWhiteSpace(s[j])) j++;
            if (j >= end || s[j] != c || j == i + 1) return i + 1;
            if (j + 1 < end && s[j + 1] == c) return i + 1;
            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, j - i - 1, style);
            Add(spans, j, 1, MdStyle.Marker);
            return j + 1;
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

        /// <summary>
        /// Whole-word uses of the document's abbreviation terms get a dotted underline — never inside
        /// code, math, keys or markers (R4).
        /// </summary>
        private static void Abbreviations(string line, List<MdSpan> spans, IReadOnlySet<string>? terms)
        {
            if (terms == null || terms.Count == 0 || line.Length > MaxInlineLength) return;
            int styled = spans.Count;
            foreach (string term in terms)
            {
                int at = 0;
                while (at < line.Length && (at = line.IndexOf(term, at, StringComparison.Ordinal)) >= 0)
                {
                    int stop = at + term.Length;
                    bool whole = (at == 0 || !IsWordChar(line[at - 1])) && (stop >= line.Length || !IsWordChar(line[stop]));
                    if (whole && !Covered(spans, styled, at, stop)) spans.Add(new MdSpan(at, term.Length, MdStyle.Abbreviation));
                    at = stop;
                }
            }
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool Covered(List<MdSpan> spans, int count, int from, int to)
        {
            for (int k = 0; k < count; k++)
            {
                var span = spans[k];
                bool hides = span.Style is MdStyle.Code or MdStyle.MathText or MdStyle.KbdText or MdStyle.Marker;
                if (hides && span.Start < to && from < span.Start + span.Length) return true;
            }
            return false;
        }
    }
}
