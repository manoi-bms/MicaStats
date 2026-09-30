using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The styled runs MicaPad shows for a stretch of a document, in the <b>light</b> palette
    /// (spec 4.4: RTF is pasted onto white pages). Syntax colors come from the language's
    /// highlighting definition through <see cref="SyntaxColors"/>; Markdown from the tokenizer and
    /// <see cref="MarkdownStyles"/>, with fenced code lines shaded as the editor paints them. Bare
    /// links get the link color in every language, as the editor underlines them. Offsets are
    /// relative to <c>start</c>.
    /// </summary>
    internal static class RtfRuns
    {
        public static IReadOnlyList<RtfRun> For(TextDocument document, int start, int length, PadLanguage language, bool markdown)
        {
            var palette = PadPalette.Light;
            var runs = new List<RtfRun>();
            if (length == 0 || document.TextLength > PadLanguages.MaxFormattedChars) return runs;

            int end = start + length;
            int first = document.GetLineByOffset(start).LineNumber;
            int last = document.GetLineByOffset(end).LineNumber;

            if (markdown) AddMarkdown(runs, document, start, end, first, last, palette);
            else AddSyntax(runs, document, start, end, first, last, language, palette);

            // Last, so the link color wins over what the language gave the same text.
            for (int n = first; n <= last; n++)
            {
                var line = document.GetLineByNumber(n);
                foreach (var (at, count) in SafeLinks.LinksIn(document.GetText(line)))
                    Add(runs, line.Offset + at, count, start, end, palette.MdLink, null, false, false, false, 1);
            }
            return runs;
        }

        private static void AddMarkdown(List<RtfRun> runs, TextDocument document, int start, int end, int first, int last, PadPalette palette)
        {
            var lines = Enumerable.Range(1, document.LineCount).Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
            var fences = FenceTracker.Classify(lines);
            for (int n = first; n <= last; n++)
            {
                var line = document.GetLineByNumber(n);
                // The editor paints a fenced line's whole width (MarkdownBackgroundRenderer); RTF shades its text.
                if (fences[n - 1] != MdFence.None)
                    Add(runs, line.Offset, line.Length, start, end, null, palette.MdCodeBackground, false, false, false, 1);

                var tokens = MarkdownLineTokenizer.Tokenize(lines[n - 1], fences[n - 1]);
                foreach (var span in tokens.Spans)
                {
                    var look = MarkdownStyles.LookOf(span.Style, palette);
                    Add(runs, line.Offset + span.Start, span.Length, start, end,
                        look.Foreground, look.Background, look.Weight == MdWeight.Bold || look.Weight == MdWeight.SemiBold,
                        look.Italic, look.Strike, look.SizeFactor);
                }
            }
        }

        private static void AddSyntax(List<RtfRun> runs, TextDocument document, int start, int end, int first, int last,
                                      PadLanguage language, PadPalette palette)
        {
            if (PadHighlighting.For(language) is not { } definition) return;
            var highlighter = new DocumentHighlighter(document, definition);
            try
            {
                for (int n = first; n <= last; n++)
                {
                    var highlighted = highlighter.HighlightLine(n);
                    foreach (var section in highlighted.Sections)
                    {
                        var color = section.Color;
                        PadColor? original = color.Foreground?.GetColor(null) is System.Windows.Media.Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
                        var paint = SyntaxColors.Resolve(color.Name, original, palette);
                        Add(runs, section.Offset, section.Length, start, end, paint, null,
                            color.FontWeight is { } w && w.ToOpenTypeWeight() >= 600,
                            color.FontStyle is { } s && s != System.Windows.FontStyles.Normal,
                            color.Strikethrough == true, 1);
                    }
                }
            }
            finally
            {
                highlighter.Dispose();
            }
        }

        private static void Add(List<RtfRun> runs, int offset, int length, int start, int end,
                                PadColor? fg, PadColor? bg, bool bold, bool italic, bool strike, double size)
        {
            int from = Math.Max(offset, start);
            int to = Math.Min(offset + length, end);
            if (to <= from) return;
            runs.Add(new RtfRun(from - start, to - from, fg, bg, bold, italic, strike, size));
        }
    }
}
