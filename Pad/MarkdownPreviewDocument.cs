using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Automation;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Button = System.Windows.Controls.Button;
using DocumentList = System.Windows.Documents.List;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>Builds MicaPad's note preview without changing the Markdown source.</summary>
    internal static class MarkdownPreviewDocument
    {
        internal static readonly DependencyProperty SourceLineProperty = DependencyProperty.RegisterAttached(
            "SourceLine", typeof(int), typeof(MarkdownPreviewDocument), new FrameworkPropertyMetadata(0));
        internal static readonly DependencyProperty SourceEndLineProperty = DependencyProperty.RegisterAttached(
            "SourceEndLine", typeof(int), typeof(MarkdownPreviewDocument), new FrameworkPropertyMetadata(0));

        private static readonly Regex TaskBox = new(
            @"^[ \t]*[-*+][ \t]+(?<box>\[[ xX]?\])(?=[ \t]|$)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Uses the shared chat renderer after replacing task markers with read-only checkbox
        /// glyphs. Fenced examples, front matter and table content remain literal.
        /// </summary>
        internal static FlowDocument Build(string markdown, ChatRender render)
        {
            string source = WithTaskGlyphs(markdown ?? "");
            IReadOnlyList<ChatBlock> parsed = ChatMarkdown.Parse(source);
            FlowDocument document = ChatDocument.Build(parsed, render);
            AddSourceAnchors(document, parsed);
            foreach (Button button in ChatDocument.All<Button>(document))
            {
                string? glyph = (button.Content as string) switch
                {
                    "Copy" => "\uE8C8",
                    "Source" => "\uE943",
                    "Try again" => "\uE72C",
                    _ => null,
                };
                if (glyph == null) continue;
                ButtonIcon.SetGlyph(button, glyph);
                AutomationProperties.SetName(button, button.ToolTip as string ?? button.Content as string ?? "");
            }
            return document;
        }

        internal static int SourceLine(DependencyObject element) => (int)element.GetValue(SourceLineProperty);
        internal static int SourceEndLine(DependencyObject element) => (int)element.GetValue(SourceEndLineProperty);

        private static void AddSourceAnchors(FlowDocument document, IReadOnlyList<ChatBlock> parsed)
        {
            List<DependencyObject> elements = RenderedBlocks(document);
            int count = Math.Min(parsed.Count, elements.Count);
            for (int i = 0; i < count; i++)
            {
                elements[i].SetValue(SourceLineProperty, parsed[i].SourceLine);
                elements[i].SetValue(SourceEndLineProperty, parsed[i].SourceEndLine);
            }
        }

        private static List<DependencyObject> RenderedBlocks(FlowDocument document)
        {
            var result = new List<DependencyObject>();
            foreach (Block block in document.Blocks)
            {
                if (block is DocumentList list) AddListItems(list, result);
                else result.Add(block);
            }
            return result;
        }

        private static void AddListItems(DocumentList list, List<DependencyObject> result)
        {
            foreach (ListItem item in list.ListItems)
            {
                result.Add(item);
                foreach (Block block in item.Blocks)
                    if (block is DocumentList nested) AddListItems(nested, result);
            }
        }

        internal static string WithTaskGlyphs(string markdown)
        {
            markdown ??= "";
            List<LinePart> parts = Lines(markdown);
            var text = new string[parts.Count];
            for (int i = 0; i < parts.Count; i++) text[i] = parts[i].Text;

            MdLineFacts[] facts = MarkdownStructure.Scan(text).Facts;
            var result = new StringBuilder(markdown.Length);
            for (int i = 0; i < parts.Count; i++)
            {
                string line = parts[i].Text;
                if (MarkdownLineTokenizer.BlockOf(line, facts[i]) == MdBlock.Task)
                {
                    Match task = TaskBox.Match(line);
                    if (task.Success)
                    {
                        Group box = task.Groups["box"];
                        bool finished = box.Value is "[x]" or "[X]";
                        line = line.Substring(0, box.Index) + (finished ? "☑" : "☐") + line.Substring(box.Index + box.Length);
                    }
                }
                result.Append(line).Append(parts[i].Ending);
            }
            return result.ToString();
        }

        private static List<LinePart> Lines(string text)
        {
            var lines = new List<LinePart>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\r' or '\n')) continue;
                int endingLength = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                lines.Add(new LinePart(text.Substring(start, i - start), text.Substring(i, endingLength)));
                i += endingLength - 1;
                start = i + 1;
            }
            lines.Add(new LinePart(text.Substring(start), ""));
            return lines;
        }

        private sealed record LinePart(string Text, string Ending);
    }
}
