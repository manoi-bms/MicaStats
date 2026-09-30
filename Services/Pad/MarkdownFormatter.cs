using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One replacement in the text, and the selection to show after it.</summary>
    public readonly record struct TextEdit(int Offset, int Length, string Text, int SelectionStart, int SelectionLength);

    /// <summary>The line prefixes the Format menu toggles.</summary>
    public enum LinePrefix
    {
        Heading1,
        Heading2,
        Heading3,
        Bullet,
        Numbered,
        Task,
        Quote,
    }

    /// <summary>
    /// The edits behind MicaPad's Format menu (spec 2.4). Pure: each returns one replacement, which
    /// the window applies as a single undoable change. Line endings already in the text are kept.
    /// </summary>
    public static class MarkdownFormatter
    {
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private static readonly Regex HeadingRx = new(@"^(#{1,6})[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[-*+][ \t]+\[[ xX]\][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[-*+][ \t]+(?!\[[ xX]\])", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^\d{1,9}[.)][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex AnyListRx = new(@"^([-*+][ \t]+\[[ xX]\][ \t]+|[-*+][ \t]+|\d{1,9}[.)][ \t]+)", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^>[ \t]?", RegexOptions.CultureInvariant);

        /// <summary>
        /// Wraps the selection in <paramref name="marker"/>, or unwraps it when the selection already
        /// starts and ends with it, or has it just outside. With nothing selected, inserts the pair and
        /// puts the caret between. A line break at the end of the selection (Home, Shift+Down) is left
        /// outside; a selection across lines wraps each line on its own (<see cref="WrapLines"/>).
        /// </summary>
        public static TextEdit Wrap(string text, int start, int length, string marker)
        {
            while (length > 0 && (text[start + length - 1] == '\r' || text[start + length - 1] == '\n')) length--;
            if (text.IndexOfAny(LineBreaks, start, length) >= 0) return WrapLines(text, start, length, marker);

            string selected = text.Substring(start, length);
            int m = marker.Length;

            // Selection starts and ends with marker - only unwrap if neighbor chars are not also the marker
            if (length >= 2 * m && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
            {
                // For single-char markers, only unwrap if the char after the opening marker is not the marker
                if (m == 1 && length > m && selected[m] == marker[0])
                {
                    // Don't unwrap: next char is the same marker (e.g., **hello** with * marker)
                }
                else
                {
                    return new TextEdit(start, length, selected.Substring(m, length - 2 * m), start, length - 2 * m);
                }
            }

            // Markers just outside selection - only unwrap if neighbor chars are not also the marker
            if (length > 0 && start >= m && start + length + m <= text.Length
                && string.CompareOrdinal(text, start - m, marker, 0, m) == 0
                && string.CompareOrdinal(text, start + length, marker, 0, m) == 0)
            {
                // For single-char markers, only unwrap if the char before the opening marker is not the marker
                if (m == 1 && start > m && text[start - 2] == marker[0])
                {
                    // Don't unwrap: previous char is the same marker
                }
                else
                {
                    return new TextEdit(start - m, length + 2 * m, selected, start - m, length);
                }
            }

            return new TextEdit(start, length, marker + selected + marker, start + m, length);
        }

        /// <summary>
        /// Wrap for a selection across lines, as one edit. Markdown formatting never crosses a line,
        /// so each line's selected part is wrapped on its own; blank lines are left alone, and
        /// indentation and trailing spaces stay outside the markers. If every line already has the
        /// marker it is removed from each; otherwise it is added to those that lack it. Line breaks
        /// are kept, and the whole block is selected afterwards.
        /// </summary>
        private static TextEdit WrapLines(string text, int start, int length, string marker)
        {
            int m = marker.Length;
            var (lines, breaks) = TextLines.Split(text.Substring(start, length));
            var parts = lines.Select(SplitSpaces).ToList();
            bool unwrap = parts.All(p => p.Core.Length == 0 || IsWrapped(p.Core, marker));

            for (int i = 0; i < lines.Count; i++)
            {
                var (lead, core, trail) = parts[i];
                if (core.Length == 0) continue;
                if (unwrap) core = core.Substring(m, core.Length - 2 * m);
                else if (!IsWrapped(core, marker)) core = marker + core + marker;
                lines[i] = lead + core + trail;
            }

            string block = TextLines.Join(lines, breaks);
            return new TextEdit(start, length, block, start, block.Length);
        }

        /// <summary>
        /// True when <paramref name="content"/> starts and ends with the marker. A one-character
        /// marker followed by the same character is not it: <c>**x**</c> is bold, not italic.
        /// </summary>
        private static bool IsWrapped(string content, string marker)
        {
            int m = marker.Length;
            return content.Length >= 2 * m
                && content.StartsWith(marker, StringComparison.Ordinal)
                && content.EndsWith(marker, StringComparison.Ordinal)
                && !(m == 1 && content.Length > m && content[m] == marker[0]);
        }

        /// <summary>A line's leading spaces and tabs, the rest up to its trailing ones, and those; a blank line is all lead.</summary>
        private static (string Lead, string Core, string Trail) SplitSpaces(string line)
        {
            var (lead, rest) = Split(line);
            string core = rest.TrimEnd(' ', '\t');
            return (lead, core, rest.Substring(core.Length));
        }

        /// <summary>Turns the selection into <c>[selection](url)</c> and selects <c>url</c> to type over.</summary>
        public static TextEdit Link(string text, int start, int length)
        {
            string selected = text.Substring(start, length);
            return new TextEdit(start, length, "[" + selected + "](url)", start + selected.Length + 3, 3);
        }

        /// <summary>
        /// Toggles a prefix on every line the selection touches (or the caret's line). If every
        /// non-blank line already has it, it is removed; otherwise it is added to those that lack it,
        /// replacing another heading level or list kind. Blank lines are left alone unless every line
        /// is blank. The whole changed block is selected afterwards.
        /// </summary>
        public static TextEdit Prefix(string text, int start, int length, LinePrefix kind)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            var (lines, breaks) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));

            bool[] targets = lines.Select(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (!targets.Any(t => t)) targets = lines.Select(_ => true).ToArray();

            bool remove = true;
            for (int i = 0; i < lines.Count; i++)
                if (targets[i] && !Has(Split(lines[i]).Content, kind)) { remove = false; break; }

            int number = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!targets[i]) continue;
                var (indent, content) = Split(lines[i]);
                lines[i] = indent + (remove ? Remove(content, kind) : Add(content, kind, ++number));
            }

            string block = TextLines.Join(lines, breaks);
            return new TextEdit(blockStart, blockEnd - blockStart, block, blockStart, block.Length);
        }

        /// <summary>Puts <c>```</c> lines around the selected lines, using the note's line ending.</summary>
        public static TextEdit CodeBlock(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string newline = TextLines.NewlineOf(text);
            string fenced = "```" + newline + block + newline + "```";
            return new TextEdit(blockStart, blockEnd - blockStart, fenced, blockStart + 3 + newline.Length, block.Length);
        }

        private static bool Has(string text, LinePrefix kind) => kind switch
        {
            LinePrefix.Heading1 => HeadingLevel(text) == 1,
            LinePrefix.Heading2 => HeadingLevel(text) == 2,
            LinePrefix.Heading3 => HeadingLevel(text) == 3,
            LinePrefix.Bullet => BulletRx.IsMatch(text),
            LinePrefix.Numbered => NumberedRx.IsMatch(text),
            LinePrefix.Task => TaskRx.IsMatch(text),
            _ => QuoteRx.IsMatch(text),
        };

        private static string Remove(string text, LinePrefix kind) => kind switch
        {
            LinePrefix.Heading1 or LinePrefix.Heading2 or LinePrefix.Heading3 => HeadingRx.Replace(text, "", 1),
            LinePrefix.Quote => QuoteRx.Replace(text, "", 1),
            _ => AnyListRx.Replace(text, "", 1),
        };

        private static string Add(string text, LinePrefix kind, int number) => kind switch
        {
            LinePrefix.Heading1 => "# " + HeadingRx.Replace(text, "", 1),
            LinePrefix.Heading2 => "## " + HeadingRx.Replace(text, "", 1),
            LinePrefix.Heading3 => "### " + HeadingRx.Replace(text, "", 1),
            LinePrefix.Bullet => "- " + AnyListRx.Replace(text, "", 1),
            LinePrefix.Numbered => number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + AnyListRx.Replace(text, "", 1),
            LinePrefix.Task => "- [ ] " + AnyListRx.Replace(text, "", 1),
            _ => QuoteRx.IsMatch(text) ? text : "> " + text,  // Don't nest quotes on lines that already have them
        };

        private static int HeadingLevel(string text)
        {
            var m = HeadingRx.Match(text);
            return m.Success ? m.Groups[1].Length : 0;
        }

        /// <summary>A line's leading spaces and tabs, and the rest.</summary>
        private static (string Indent, string Content) Split(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return (line.Substring(0, i), line.Substring(i));
        }

    }
}
