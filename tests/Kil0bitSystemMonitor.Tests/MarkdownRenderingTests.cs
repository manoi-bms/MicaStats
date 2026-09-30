using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Markdown in the editor: the fence cache, the colorizer on a real TextView, and the window wiring.</summary>
    public class MarkdownRenderingTests
    {
        [Fact]
        public void The_cache_classifies_fences_and_follows_edits() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\n```\nx\n```\nb");
            var cache = new MarkdownDocumentCache();
            int changes = 0;
            cache.FencesChanged += () => changes++;

            Assert.Equal(MdFence.Inside, cache.KindOf(document, 3));
            Assert.Equal(MdFence.None, cache.KindOf(document, 5));

            // Typing after the closing fence stops it closing: line 5 is now inside.
            document.Insert(document.GetLineByNumber(4).EndOffset, "x");
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 5));
            Assert.Equal(1, changes);
        });

        [Fact]
        public void Ordinary_typing_does_not_rescan_the_document() => UiThread.Run(() =>
        {
            var document = new TextDocument("# Title\nsome text\n```\ncode\n```");
            var cache = new MarkdownDocumentCache();
            cache.KindOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(document.GetLineByNumber(2).EndOffset, " more words");
            document.Insert(document.GetLineByNumber(4).EndOffset, "();");

            Assert.Equal(before, cache.Recomputes);
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 4));
        });

        [Fact]
        public void The_colorizer_sizes_headings_and_bolds_bold_text() => UiThread.Run(() =>
        {
            var document = new TextDocument("# Title\nsome **bold** text");
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = document };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();

            var plain = view.GetVisualLine(2)!.Elements.First(e => e.RelativeTextOffset == 0);
            double baseSize = plain.TextRunProperties.FontRenderingEmSize;
            var title = view.GetVisualLine(1)!.Elements.Last();
            Assert.Equal(baseSize * 1.6, title.TextRunProperties.FontRenderingEmSize, 3);

            var bold = view.GetVisualLine(2)!.Elements.Single(e => e.RelativeTextOffset == 7);
            Assert.Equal(FontWeights.Bold, bold.TextRunProperties.Typeface.Weight);
        });

        [Fact]
        public void A_markdown_note_gets_the_colorizer_the_background_and_bullets() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var view = window.Editor.TextArea.TextView;
            Assert.True(window.LanguageView.HasMarkdown);
            Assert.Single(view.LineTransformers.OfType<MarkdownColorizer>());
            Assert.Single(view.BackgroundRenderers.OfType<MarkdownBackgroundRenderer>());
            Assert.Single(view.ElementGenerators.OfType<BulletGenerator>());
        });

        [Fact]
        public void Plain_text_and_code_files_have_no_markdown() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var note = env.Workspace.Active!;
            window.ChooseLanguage(note, "plain");
            var view = window.Editor.TextArea.TextView;
            Assert.False(window.LanguageView.HasMarkdown);
            Assert.Empty(view.LineTransformers.OfType<MarkdownColorizer>());
            Assert.Empty(view.BackgroundRenderers.OfType<MarkdownBackgroundRenderer>());
            Assert.Empty(view.ElementGenerators.OfType<BulletGenerator>());

            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{}");
            Assert.False(window.LanguageView.HasMarkdown);
        });

        [Fact]
        public void The_markdown_switch_takes_it_away_and_back() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            config.PadMarkdown = false;
            Assert.False(window.LanguageView.HasMarkdown);

            config.PadMarkdown = true;
            Assert.True(window.LanguageView.HasMarkdown);
            Assert.Single(window.Editor.TextArea.TextView.LineTransformers.OfType<MarkdownColorizer>());
        });

        [Fact]
        public void Settings_offers_the_markdown_switch()
        {
            string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            Assert.Contains("x:Name=\"PadMarkdownToggle\"", xaml);
        }
    }
}
