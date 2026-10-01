using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>References drawn as pills: one element per reference, its label, its colors.</summary>
    public class SecretPillTests
    {
        private static readonly CredentialInfo Bank = new("K7Q2M9XD", "Bank", DateTime.UtcNow, null);

        [Fact]
        public void Labels_and_tooltips()
        {
            Assert.Equal("Bank", SecretPillGenerator.LabelOf(Bank));
            Assert.Equal("Credential", SecretPillGenerator.LabelOf(Bank with { Label = "" }));
            Assert.Equal("Missing credential", SecretPillGenerator.LabelOf(null));
            Assert.Equal("Stored credential K7Q2M9XD \u2014 right-click for options", SecretPillGenerator.ToolTipOf("K7Q2M9XD", Bank));
            Assert.Equal("No stored credential K7Q2M9XD", SecretPillGenerator.ToolTipOf("K7Q2M9XD", null));
        }

        [Fact]
        public void Each_reference_becomes_one_element_covering_all_of_it() => UiThread.Run(() =>
        {
            var palette = PadPalette.Dark;
            var editor = new TextEditor { Text = "a {{secret:K7Q2M9XD}} b {{secret:00000000}}" };
            var generator = new SecretPillGenerator(id => id == Bank.Id ? Bank : null, () => palette, () => 14);
            editor.TextArea.TextView.ElementGenerators.Add(generator);
            var view = editor.TextArea.TextView;   // detached editors get no visual lines until the view itself is laid out
            view.Measure(new Size(800, 200));
            view.Arrange(new Rect(0, 0, 800, 200));
            editor.TextArea.TextView.EnsureVisualLines();

            var line = editor.TextArea.TextView.VisualLines.Single();
            var pills = line.Elements.OfType<InlineObjectElement>().ToList();

            Assert.Equal(2, pills.Count);
            Assert.All(pills, p => Assert.Equal(SecretTokens.Format("K7Q2M9XD").Length, p.DocumentLength));
            Assert.Contains("Bank", Text(pills[0].Element));
            Assert.Contains("Missing credential", Text(pills[1].Element));
            Assert.Equal(PadThemeApplier.ToBrush(palette.PillBack).ToString(), ((Border)pills[0].Element).Background.ToString());

            palette = PadPalette.Light;   // a theme switch redraws in the new palette
            editor.TextArea.TextView.Redraw();
            editor.TextArea.TextView.EnsureVisualLines();
            var redrawn = editor.TextArea.TextView.VisualLines.Single().Elements.OfType<InlineObjectElement>().First();
            Assert.Equal(PadThemeApplier.ToBrush(PadPalette.Light.PillBack).ToString(), ((Border)redrawn.Element).Background.ToString());
        });

        [Fact]
        public void The_scan_runs_line_by_line_to_the_end_offset_and_skips_long_lines()
        {
            string text = "a\n" + new string('x', 4001) + " {{secret:00000000}}\nb {{secret:K7Q2M9XD}}";
            var document = new ICSharpCode.AvalonEdit.Document.TextDocument(text);
            int last = text.LastIndexOf("{{", StringComparison.Ordinal);

            Assert.Equal(last, SecretPillGenerator.FirstReference(document, 0, document.TextLength));   // the long line is skipped
            Assert.Equal(-1, SecretPillGenerator.FirstReference(document, 0, last - 1));               // not past the end offset
            Assert.Equal(-1, SecretPillGenerator.FirstReference(document, last + 1, document.TextLength));
        }

        [Fact]
        public void A_folded_visual_line_draws_the_pill_after_the_fold_and_not_the_one_inside() => UiThread.Run(() =>
        {
            string text = "start\nhidden {{secret:00000000}}\nend {{secret:K7Q2M9XD}}";
            var editor = new TextEditor { Text = text };
            var foldings = ICSharpCode.AvalonEdit.Folding.FoldingManager.Install(editor.TextArea);
            foldings.CreateFolding(3, text.IndexOf("end", StringComparison.Ordinal) + 3).IsFolded = true;
            editor.TextArea.TextView.ElementGenerators.Add(new SecretPillGenerator(id => id == Bank.Id ? Bank : null, () => PadPalette.Dark, () => 14));
            var view = editor.TextArea.TextView;
            view.Measure(new Size(800, 200));
            view.Arrange(new Rect(0, 0, 800, 200));
            view.EnsureVisualLines();

            var line = Assert.Single(view.VisualLines);   // three document lines, one visual line
            Assert.Equal(3, line.LastDocumentLine.LineNumber);
            var pill = Assert.Single(line.Elements.OfType<InlineObjectElement>());
            Assert.Contains("Bank", Text(pill.Element));
            ICSharpCode.AvalonEdit.Folding.FoldingManager.Uninstall(foldings);
        });

        private static string Text(UIElement element) =>
            string.Concat(((Panel)((Border)element).Child).Children.OfType<TextBlock>().Select(t => t.Text));
    }
}
