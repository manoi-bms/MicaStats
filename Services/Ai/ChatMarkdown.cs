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
    }

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

        internal ChatBlock(ChatBlockKind kind, int level = 0, int depth = 0, int number = 0, string language = "", string code = "")
        {
            Kind = kind;
            Level = level;
            Depth = depth;
            Number = number;
            Language = language;
            Code = code;
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
            MdFence[] fences = FenceTracker.Classify(lines);

            var levels = new List<int>();   // the indentation of each open list level
            ChatBlock? open = null;         // a paragraph, quote or item the next line may continue
            bool blank = false;             // a blank line came after `open`
            int itemContent = 0;            // where the last item's text starts

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
                    blocks.Add(new ChatBlock(ChatBlockKind.Code, language: fence.Groups[2].Value, code: string.Join("\n", code)));
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
