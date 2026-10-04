using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>What a <see cref="ChatBlock"/> is.</summary>
    public enum ChatBlockKind
    {
        Paragraph,
        Heading,
        Bullet,
        Numbered,
        Quote,
        Code,
        Rule,
        Table,
    }

    /// <summary>How a table column's text sits in its cells.</summary>
    public enum ChatAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>One table cell: its text as styled runs (empty for an empty cell).</summary>
    public sealed record ChatCell(IReadOnlyList<ChatRun> Runs);

    /// <summary>
    /// A table: one alignment per column, a header row and body rows. Every row has exactly as many
    /// cells as there are columns.
    /// </summary>
    public sealed record ChatTable(IReadOnlyList<ChatAlign> Aligns, IReadOnlyList<ChatCell> Header,
                                   IReadOnlyList<IReadOnlyList<ChatCell>> Rows);

    /// <summary>
    /// A piece of text with one style. A run whose text is exactly <c>"\n"</c> is a line break.
    /// <see cref="Link"/> is set only for an address <see cref="SafeLinks"/> allows.
    /// </summary>
    public sealed record ChatRun(string Text, bool Bold = false, bool Italic = false, bool Code = false,
                                 bool Strike = false, Uri? Link = null)
    {
        /// <summary>True for a line break.</summary>
        public bool IsLineBreak => Text == "\n";
    }

    /// <summary>One block of an answer, as <see cref="ChatMarkdown.Parse"/> found it.</summary>
    public sealed class ChatBlock
    {
        private readonly List<ChatRun> _runs = new();

        internal ChatBlock(ChatBlockKind kind, int level = 0, int depth = 0, int number = 0, string language = "", string code = "",
                            ChatTable? table = null, bool closed = true)
        {
            Kind = kind;
            Level = level;
            Depth = depth;
            Number = number;
            Language = language;
            Code = code;
            Table = table;
            Closed = closed;
        }

        /// <summary>What the block is.</summary>
        public ChatBlockKind Kind { get; }

        /// <summary>The styled text of a paragraph, heading, list item or quote; empty for code and rules.</summary>
        public IReadOnlyList<ChatRun> Runs => _runs;

        /// <summary>A heading's level, 1 to 3 (levels 4 to 6 come back as 3).</summary>
        public int Level { get; }

        /// <summary>A list item's nesting, 0 at the top.</summary>
        public int Depth { get; }

        /// <summary>A numbered item's own number, as written.</summary>
        public int Number { get; }

        /// <summary>A code block's language, or empty.</summary>
        public string Language { get; }

        /// <summary>A code block's text, verbatim, lines joined with <c>\n</c>.</summary>
        public string Code { get; }

        /// <summary>A table block's header, alignments and rows; null for every other kind.</summary>
        public ChatTable? Table { get; }

        /// <summary>
        /// For a code block, whether its closing fence line was seen (false while the fence is still
        /// open at the end of the text, as in a streaming answer); true for every other kind.
        /// </summary>
        public bool Closed { get; }

        internal void Add(IReadOnlyList<ChatRun> runs) => _runs.AddRange(runs);

        internal void AddBreak() => _runs.Add(new ChatRun("\n"));
    }

    /// <summary>
    /// The Markdown an answer arrives in, as blocks and styled runs for the Ask window: paragraphs
    /// (a single newline is a line break, as in chat apps), headings, bullet and numbered lists,
    /// quotes, fenced code and rules; bold, italic, code, strike-through and links inline.
    ///
    /// <para>
    /// Deliberately smaller than CommonMark, like MicaPad's tokenizer, whose line classification
    /// and fence tracking it shares: an unclosed or ambiguous marker stays literal text, and text
    /// is never lost. Unlike that tokenizer the markers are consumed, not shown. Only http, https
    /// and mailto addresses become links (<see cref="SafeLinks"/>); any other target leaves its
    /// text plain. Pure: no WPF.
    /// </para>
    /// </summary>
    public static class ChatMarkdown
    {
        /// <summary>
        /// Lines longer than this get no inline styling, so a pathological line (thousands of
        /// unmatched markers) cannot stall the re-render of a streaming answer.
        /// </summary>
        public const int MaxInlineLength = 4000;

        /// <summary>
        /// The deepest list nesting kept; deeper items stay at this depth. Each level is a nested
        /// WPF List, and layout recursing thousands of levels deep could overflow the stack.
        /// </summary>
        public const int MaxListDepth = 6;

        /// <summary>
        /// The most columns a table may have. A wider one is not a table: its lines are parsed as
        /// text, so a pathological answer cannot build an enormous grid.
        /// </summary>
        public const int MaxTableColumns = 12;

        /// <summary>The most body rows a table may have; a longer one is not a table (see <see cref="MaxTableColumns"/>).</summary>
        public const int MaxTableRows = 100;

        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.CultureInvariant);
        private static readonly Regex ClosingHashesRx = new(@"(?:^|[ \t]+)#+[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^([ \t]*)[-*+][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^([ \t]*)([0-9]{1,9})[.)][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}>[ \t]?", RegexOptions.CultureInvariant);
        private static readonly Regex FenceRx = new(@"^( {0,3})(?:`{3,}|~{3,})[ \t]*(\S*)", RegexOptions.CultureInvariant);
        private static readonly Regex UrlRx = new(@"\G" + SafeLinks.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The blocks of <paramref name="text"/>, in order. Any line ending works.</summary>
        public static IReadOnlyList<ChatBlock> Parse(string? text)
        {
            var blocks = new List<ChatBlock>();
            if (string.IsNullOrEmpty(text)) return blocks;

            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            MdFence[] fences = FenceTracker.Classify(lines, mathBlocks: false);

            var levels = new List<int>();   // the indentation of each open list level
            ChatBlock? open = null;         // a paragraph, quote or item the next line may continue
            bool blank = false;             // a blank line came after `open`
            int itemContent = 0;            // where the last item's text starts
            int noTableThrough = -1;        // lines up to here belong to a table over the limits: text, as before tables

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (fences[i] == MdFence.Delimiter)
                {
                    // Always an opening fence: the loop below swallows the closing one.
                    Match fence = FenceRx.Match(line);
                    int indent = fence.Groups[1].Length;
                    var code = new List<string>();
                    int j = i + 1;
                    for (; j < lines.Length && fences[j] == MdFence.Inside; j++) code.Add(Unindent(lines[j], indent));
                    // Past the last line means the text ended inside the fence (still streaming).
                    blocks.Add(new ChatBlock(ChatBlockKind.Code, language: fence.Groups[2].Value, code: string.Join("\n", code),
                        closed: j < lines.Length));
                    i = j;   // the closing fence, or past the end
                    open = null;
                    blank = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    blank = true;
                    continue;
                }

                MdBlock kind = MarkdownLineTokenizer.BlockOf(line, MdFence.None);

                // MicaPad's tokenizer takes any Unicode digit (a Thai numeral, say) as a list number. Here
                // only an ASCII number that parses starts a numbered item; anything else is text.
                Match? numberMatch = null;
                int number = 0;
                if (kind == MdBlock.Numbered
                    && !((numberMatch = NumberedRx.Match(line)).Success
                         && int.TryParse(numberMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
                {
                    kind = MdBlock.Paragraph;
                }

                // A table needs a header line that is plain text, not part of an open quote or list item
                // (those lines continue it), and a delimiter line right after it.
                bool continues = open is { Kind: ChatBlockKind.Quote or ChatBlockKind.Bullet or ChatBlockKind.Numbered }
                    && (!blank || (open.Kind != ChatBlockKind.Quote && Width(Leading(line)) >= itemContent));
                if (i > noTableThrough && !continues && (kind is MdBlock.Paragraph or MdBlock.Definition)
                    && TryTable(lines, fences, i, ref noTableThrough, out ChatTable? table, out int lastRow))
                {
                    blocks.Add(new ChatBlock(ChatBlockKind.Table, table: table));
                    levels.Clear();
                    open = null;
                    blank = false;
                    i = lastRow;
                    continue;
                }

                switch (kind)
                {
                    case MdBlock.Rule:
                        blocks.Add(new ChatBlock(ChatBlockKind.Rule));
                        levels.Clear();
                        open = null;
                        break;

                    case MdBlock.Heading:
                    {
                        Match m = HeadingRx.Match(line);
                        string content = ClosingHashesRx.Replace(line.Substring(m.Length).Trim(), "");
                        var heading = new ChatBlock(ChatBlockKind.Heading, level: Math.Min(3, m.Groups[1].Length));
                        heading.Add(ParseInline(content));
                        blocks.Add(heading);
                        levels.Clear();
                        open = null;
                        break;
                    }

                    case MdBlock.Quote:
                    {
                        string content = line;
                        Match m;
                        while ((m = QuoteRx.Match(content)).Success) content = content.Substring(m.Length);
                        if (open is { Kind: ChatBlockKind.Quote } && !blank)
                        {
                            open.AddBreak();
                        }
                        else
                        {
                            open = new ChatBlock(ChatBlockKind.Quote);
                            blocks.Add(open);
                            levels.Clear();
                        }
                        open.Add(ParseInline(content.Trim()));
                        break;
                    }

                    case MdBlock.Bullet:
                    case MdBlock.Task:
                    case MdBlock.Numbered:
                    {
                        bool numbered = kind == MdBlock.Numbered;
                        Match m = numbered ? numberMatch! : BulletRx.Match(line);
                        int depth = DepthOf(levels, Width(m.Groups[1].Value));
                        itemContent = Width(line.Substring(0, m.Length));
                        open = new ChatBlock(numbered ? ChatBlockKind.Numbered : ChatBlockKind.Bullet, depth: depth, number: number);
                        open.Add(ParseInline(line.Substring(m.Length).Trim()));
                        blocks.Add(open);
                        break;
                    }

                    default:
                    {
                        string content = line.Trim();
                        bool isItem = open is { Kind: ChatBlockKind.Bullet or ChatBlockKind.Numbered };
                        if (open != null && !blank)
                        {
                            // A line right after a paragraph, quote or item continues it.
                            open.AddBreak();
                        }
                        else if (isItem && blank && Width(Leading(line)) >= itemContent)
                        {
                            // An indented paragraph after a blank line stays in its item.
                            open!.AddBreak();
                            open.AddBreak();
                        }
                        else
                        {
                            open = new ChatBlock(ChatBlockKind.Paragraph);
                            blocks.Add(open);
                            levels.Clear();
                        }
                        open.Add(ParseInline(content));
                        break;
                    }
                }
                blank = false;
            }
            return blocks;
        }

        /// <summary>The styled runs of one line of text; adjacent runs with the same style are merged.</summary>
        public static IReadOnlyList<ChatRun> ParseInline(string text)
        {
            var writer = new RunWriter();
            if (text.Length > MaxInlineLength) writer.Text(text, default);
            else Inline(text, 0, text.Length, default, writer);
            return writer.Finish();
        }

        // ---- blocks ----------------------------------------------------------------------------

        /// <summary>
        /// The list depth of an item indented <paramref name="indent"/> columns, updating the open
        /// levels; never deeper than <see cref="MaxListDepth"/>.
        /// </summary>
        private static int DepthOf(List<int> levels, int indent)
        {
            while (levels.Count > 0 && levels[^1] > indent) levels.RemoveAt(levels.Count - 1);
            if (levels.Count == 0 || levels[^1] < indent) levels.Add(indent);
            return Math.Min(levels.Count - 1, MaxListDepth);
        }

        /// <summary>Columns of leading whitespace; a tab moves to the next multiple of four.</summary>
        private static int Width(string prefix)
        {
            int columns = 0;
            foreach (char c in prefix)
            {
                if (c == '\t') columns += 4 - columns % 4;
                else columns++;
            }
            return columns;
        }

        private static string Leading(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line.Substring(0, i);
        }

        /// <summary>A code line without up to <paramref name="indent"/> leading spaces (its fence's own indentation).</summary>
        private static string Unindent(string line, int indent)
        {
            int i = 0;
            while (i < indent && i < line.Length && line[i] == ' ') i++;
            return line.Substring(i);
        }

        // ---- tables ----------------------------------------------------------------------------

        /// <summary>
        /// A table whose header is <c>lines[i]</c>, if a delimiter line follows and the counts agree;
        /// <paramref name="last"/> is its final line. A table over the limits is none, and
        /// <paramref name="noTableThrough"/> is set so its lines are all read as text, as they were
        /// before tables. Every step is linear in the lines it reads, and each line is read once.
        /// </summary>
        private static bool TryTable(string[] lines, MdFence[] fences, int i, ref int noTableThrough,
                                     out ChatTable? table, out int last)
        {
            table = null;
            last = i;
            if (i + 1 >= lines.Length || fences[i + 1] != MdFence.None || !HasPipe(lines[i])) return false;

            List<ChatAlign>? aligns = Delimiter(lines[i + 1]);
            if (aligns == null) return false;
            int columns = aligns.Count;
            if (columns > MaxTableColumns)
            {
                noTableThrough = i + 1;
                return false;
            }

            List<string>? header = SplitRow(lines[i], columns, out bool tooMany);
            if (tooMany || header == null || header.Count != columns) return false;

            // Find where the body ends before splitting any cell, so a huge one costs a line scan only.
            int end = i + 2;
            while (end < lines.Length && fences[end] == MdFence.None && !string.IsNullOrWhiteSpace(lines[end])
                   && (HasPipe(lines[end]) || !StartsBlock(lines[end]))) end++;
            if (end - (i + 2) > MaxTableRows)
            {
                noTableThrough = end - 1;
                return false;
            }

            // Past the inline limit a line gets no styling, exactly as a paragraph line does.
            var head = new List<ChatCell>(columns);
            foreach (string cell in header) head.Add(MakeCell(cell, lines[i].Length <= MaxInlineLength));

            var rows = new List<IReadOnlyList<ChatCell>>(end - (i + 2));
            for (int r = i + 2; r < end; r++)
            {
                List<string> parts = SplitRow(lines[r], columns, out _)!;   // a cell past the last column is cut
                bool styled = lines[r].Length <= MaxInlineLength;
                var row = new List<ChatCell>(columns);
                for (int c = 0; c < columns; c++) row.Add(MakeCell(c < parts.Count ? parts[c] : "", styled));
                rows.Add(row);
            }

            table = new ChatTable(aligns, head, rows);
            last = end - 1;
            return true;
        }

        private static ChatCell MakeCell(string text, bool styled) =>
            new(styled ? ParseInline(text) : (text.Length == 0 ? new List<ChatRun>() : new List<ChatRun> { new ChatRun(text) }));

        /// <summary>True if a line holds a pipe that no backslash escapes.</summary>
        private static bool HasPipe(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '|') { i++; continue; }
                if (line[i] == '|') return true;
            }
            return false;
        }

        /// <summary>Whether a line without a pipe starts a block of its own, so it ends a table.</summary>
        private static bool StartsBlock(string line)
        {
            MdBlock kind = MarkdownLineTokenizer.BlockOf(line, MdFence.None);
            // A Unicode digit is no list number here (see Parse).
            if (kind == MdBlock.Numbered) return NumberedRx.IsMatch(line);
            return kind is not (MdBlock.Paragraph or MdBlock.Definition);
        }

        /// <summary>
        /// The alignments of a delimiter line (cells of <c>-</c> with an optional <c>:</c> at either
        /// end), or null if the line is not one. More than <see cref="MaxTableColumns"/> cells give a
        /// list one longer than that, which is all the caller needs to know.
        /// </summary>
        private static List<ChatAlign>? Delimiter(string line)
        {
            List<string> cells = SplitRow(line, MaxTableColumns + 1, out _)!;
            var aligns = new List<ChatAlign>(cells.Count);
            foreach (string cell in cells)
            {
                bool left = cell.StartsWith(':');
                bool right = cell.Length > 1 && cell.EndsWith(':');
                int from = left ? 1 : 0;
                int to = cell.Length - (right ? 1 : 0);
                if (to <= from) return null;
                for (int k = from; k < to; k++)
                    if (cell[k] != '-') return null;
                aligns.Add(left && right ? ChatAlign.Center : right ? ChatAlign.Right : ChatAlign.Left);
            }
            return aligns;
        }

        /// <summary>
        /// The trimmed cells of a table line: the outer pipes dropped, split on pipes that are not
        /// escaped and not inside a code span. A <c>\|</c> stays for the inline parser, which shows a
        /// pipe, except inside a code span, where it becomes a plain pipe. At most
        /// <paramref name="limit"/> cells are returned; <paramref name="tooMany"/> says more followed.
        /// Linear in the line: code spans are matched by one pass over the backtick runs.
        /// </summary>
        private static List<string> SplitRow(string line, int limit, out bool tooMany)
        {
            tooMany = false;
            string s = line.Trim();
            var cells = new List<string>();
            var cell = new StringBuilder();
            List<(int Pos, int Len, int Next)> ticks = BacktickRuns(s);
            int run = 0;
            int i = s.Length > 0 && s[0] == '|' ? 1 : 0;
            bool trailing = false;   // the line ended with a separator: no cell follows it

            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1]))
                {
                    cell.Append(c).Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    while (run < ticks.Count && ticks[run].Pos < i) run++;
                    (int pos, int len, int next) = ticks[run];
                    if (next >= 0)
                    {
                        int close = ticks[next].Pos + ticks[next].Len;
                        for (int k = pos; k < close; k++)
                        {
                            if (s[k] == '\\' && k + 1 < close && s[k + 1] == '|') k++;   // an escaped pipe is a pipe in code
                            cell.Append(s[k]);
                        }
                        i = close;
                        run = next + 1;
                    }
                    else
                    {
                        cell.Append(s, pos, len);
                        i = pos + len;
                        run++;
                    }
                    continue;
                }
                if (c == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    if (i == s.Length - 1) trailing = true;
                    else if (cells.Count == limit)
                    {
                        tooMany = true;
                        return cells;
                    }
                    i++;
                    continue;
                }
                cell.Append(c);
                i++;
            }
            if (!trailing) cells.Add(cell.ToString().Trim());
            return cells;
        }

        /// <summary>
        /// The runs of backticks in a line (an escaped backtick is no run), each with the index of the
        /// next run of the same length, which would close it, or -1. One pass from the right.
        /// </summary>
        private static List<(int Pos, int Len, int Next)> BacktickRuns(string s)
        {
            var runs = new List<(int Pos, int Len, int Next)>();
            if (s.IndexOf('`') < 0) return runs;
            for (int i = 0; i < s.Length;)
            {
                if (s[i] == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1])) { i += 2; continue; }
                if (s[i] != '`') { i++; continue; }
                int n = Run(s, i, s.Length, '`');
                runs.Add((i, n, -1));
                i += n;
            }
            var nextOfLength = new Dictionary<int, int>();
            for (int r = runs.Count - 1; r >= 0; r--)
            {
                runs[r] = (runs[r].Pos, runs[r].Len, nextOfLength.TryGetValue(runs[r].Len, out int next) ? next : -1);
                nextOfLength[runs[r].Len] = r;
            }
            return runs;
        }

        // ---- inline ----------------------------------------------------------------------------

        /// <summary>The style that applies inside markers; a run adds <c>Code</c> itself.</summary>
        private readonly record struct Format(bool Bold, bool Italic, bool Strike, Uri? Link);

        /// <summary>Code spans, links, emphasis and bare addresses between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static void Inline(string s, int start, int end, Format format, RunWriter writer)
        {
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
                {
                    writer.Text(s[i + 1], format);
                    i += 2;
                    continue;
                }
                if (c == '`') { i = CodeSpan(s, i, end, format, writer); continue; }
                if (c == '[' && TryLink(s, i, end, format, writer, out int afterLink)) { i = afterLink; continue; }
                if (c is '*' or '_' or '~') { i = Emphasis(s, i, end, format, writer); continue; }
                if (format.Link == null && (c is 'h' or 'H' or 'm' or 'M') && TryAddress(s, i, end, format, writer, out int afterAddress))
                {
                    i = afterAddress;
                    continue;
                }
                writer.Text(c, format);
                i++;
            }
        }

        /// <summary>
        /// Markers a backslash makes literal. Not the backslash itself, so a path such as
        /// <c>\\server\share</c> shows as written.
        /// </summary>
        private static bool IsEscapable(char c) => "`*_~[]()#>!|".IndexOf(c) >= 0;

        private static int Run(string s, int i, int end, char c)
        {
            int j = i;
            while (j < end && s[j] == c) j++;
            return j - i;
        }

        /// <summary>A run of backticks closed by a run of the same length; unclosed backticks are text.</summary>
        private static int CodeSpan(string s, int i, int end, Format format, RunWriter writer)
        {
            int n = Run(s, i, end, '`');
            int close = FindCodeClose(s, i + n, end, n);
            if (close < 0)
            {
                writer.Text(s.Substring(i, n), format);
                return i + n;
            }
            string code = s.Substring(i + n, close - i - n);
            if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code.Substring(1, code.Length - 2);
            writer.Code(code, format);
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
        /// <c>[text](address "title")</c>. The text keeps its styles; the address may hold balanced
        /// parentheses. An address <see cref="SafeLinks"/> refuses leaves the text plain.
        /// </summary>
        private static bool TryLink(string s, int i, int end, Format format, RunWriter writer, out int after)
        {
            after = i;
            int close = FindBracketClose(s, i, end);
            if (close < 0 || close + 1 >= end || s[close + 1] != '(') return false;
            int paren = FindParenClose(s, close + 1, end);
            if (paren < 0) return false;

            string target = s.Substring(close + 2, paren - close - 2).Trim();
            if (target.StartsWith('<') && target.IndexOf('>') > 0) target = target.Substring(1, target.IndexOf('>') - 1);
            else if (target.IndexOfAny(new[] { ' ', '\t' }) is int space and >= 0) target = target.Substring(0, space);

            Uri? uri = SafeLinks.TryCreate(target);
            var inner = format with { Link = uri ?? format.Link };
            if (close == i + 1) writer.Text(target, inner);   // [](address): show the address
            else Inline(s, i + 1, close, inner, writer);
            after = paren + 1;
            return true;
        }

        /// <summary>The <c>]</c> matching the <c>[</c> at <paramref name="open"/>, skipping escapes and code, or -1.</summary>
        private static int FindBracketClose(string s, int open, int end)
        {
            int depth = 0;
            for (int j = open; j < end; j++)
            {
                char c = s[j];
                if (c == '\\') { j++; continue; }
                if (c == '`')
                {
                    int ticks = Run(s, j, end, '`');
                    int codeClose = FindCodeClose(s, j + ticks, end, ticks);
                    j = (codeClose < 0 ? j + ticks : codeClose + ticks) - 1;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return j;
            }
            return -1;
        }

        /// <summary>The <c>)</c> matching the <c>(</c> at <paramref name="open"/>, or -1.</summary>
        private static int FindParenClose(string s, int open, int end)
        {
            int depth = 0;
            for (int j = open; j < end; j++)
            {
                char c = s[j];
                if (c == '\\') { j++; continue; }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return j;
            }
            return -1;
        }

        /// <summary>
        /// <c>*</c>/<c>_</c> runs of one to three (italic, bold, both) and <c>~~</c> (strike). A
        /// closing run of the same length wins; a longer closing run is used only when no exact one
        /// follows (<c>**bold *italic***</c>). Returns where scanning continues.
        /// </summary>
        private static int Emphasis(string s, int i, int end, Format format, RunWriter writer)
        {
            char c = s[i];
            int n = Run(s, i, end, c);
            if ((c == '~' && n != 2) || n > 3 || !CanOpen(s, i, n, end, c)) return Literal(s, i, n, format, writer);

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
                    if (m == n) return Close(s, i, n, j, c, format, writer);
                    if (m > n && fallback < 0)
                    {
                        fallback = j;
                        fallbackRun = m;
                    }
                }
                j += m;
            }
            return fallback >= 0
                ? Close(s, i, n, fallback + fallbackRun - n, c, format, writer)
                : Literal(s, i, n, format, writer);
        }

        private static int Literal(string s, int i, int n, Format format, RunWriter writer)
        {
            writer.Text(s.Substring(i, n), format);
            return i + n;
        }

        private static int Close(string s, int open, int n, int close, char c, Format format, RunWriter writer)
        {
            Format inner = c == '~'
                ? format with { Strike = true }
                : format with { Bold = format.Bold || n >= 2, Italic = format.Italic || n != 2 };
            Inline(s, open + n, close, inner, writer);
            return close + n;
        }

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
        /// A bare http, https or mailto address. It ends at the end of the styled range and never
        /// with a marker or sentence punctuation, so <c>**https://a.com**</c> links <c>https://a.com</c>.
        /// </summary>
        private static bool TryAddress(string s, int i, int end, Format format, RunWriter writer, out int after)
        {
            after = i;
            Match m = UrlRx.Match(s, i);
            if (!m.Success) return false;
            int length = Math.Min(m.Length, end - i);
            while (length > 0 && "*_~.,;:!?".IndexOf(s[i + length - 1]) >= 0) length--;
            string address = s.Substring(i, length);
            Uri? uri = SafeLinks.TryCreate(address);
            if (uri == null) return false;
            writer.Text(address, format with { Link = uri });
            after = i + length;
            return true;
        }

        /// <summary>Collects runs, merging text of the same style.</summary>
        private sealed class RunWriter
        {
            private readonly List<ChatRun> _runs = new();
            private readonly StringBuilder _pending = new();
            private Format _format;

            public void Text(char c, Format format)
            {
                Switch(format);
                _pending.Append(c);
            }

            public void Text(string text, Format format)
            {
                if (text.Length == 0) return;
                Switch(format);
                _pending.Append(text);
            }

            public void Code(string text, Format format)
            {
                Flush();
                if (text.Length > 0) Add(new ChatRun(text, format.Bold, format.Italic, true, format.Strike, format.Link));
            }

            public List<ChatRun> Finish()
            {
                Flush();
                return _runs;
            }

            private void Switch(Format format)
            {
                if (_pending.Length > 0 && !Same(format, _format)) Flush();
                _format = format;
            }

            private void Flush()
            {
                if (_pending.Length == 0) return;
                Add(new ChatRun(_pending.ToString(), _format.Bold, _format.Italic, false, _format.Strike, _format.Link));
                _pending.Clear();
            }

            private void Add(ChatRun run)
            {
                if (_runs.Count > 0 && _runs[^1] is var last && last.Bold == run.Bold && last.Italic == run.Italic
                    && last.Code == run.Code && last.Strike == run.Strike && ReferenceEquals(last.Link, run.Link))
                {
                    _runs[^1] = last with { Text = last.Text + run.Text };
                }
                else
                {
                    _runs.Add(run);
                }
            }

            /// <summary>Links compare by instance: two links to one address stay two links.</summary>
            private static bool Same(Format a, Format b) =>
                a.Bold == b.Bold && a.Italic == b.Italic && a.Strike == b.Strike && ReferenceEquals(a.Link, b.Link);
        }
    }
}
