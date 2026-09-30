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
        public void Removing_the_indent_before_backticks_makes_a_fence_without_a_backtick_typed() => UiThread.Run(() =>
        {
            var document = new TextDocument("    ```\ncode\n    ```");
            var cache = new MarkdownDocumentCache();
            Assert.Equal(MdFence.None, cache.KindOf(document, 1));
            Assert.Equal(MdFence.None, cache.KindOf(document, 2));

            document.Remove(document.GetLineByNumber(3).Offset, 4);
            document.Remove(0, 4);

            Assert.Equal(MdFence.Delimiter, cache.KindOf(document, 1));
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 2));
            Assert.Equal(MdFence.Delimiter, cache.KindOf(document, 3));

            document.Insert(0, "x");   // a character before ``` undoes it
            Assert.Equal(MdFence.None, cache.KindOf(document, 2));
        });

        [Fact]
        public void A_cache_detached_by_an_earlier_handler_of_the_same_change_ignores_it() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\n```\nx\n```\nb");
            var cache = new MarkdownDocumentCache();
            // Subscribed first, as the window's own handler is: it switches formatting off mid-change.
            document.Changed += (s, e) => cache.Detach();
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 3));

            // AvalonEdit still calls the detached cache for this change; it must not throw.
            document.Insert(0, "```\n");

            Assert.Equal(MdFence.Delimiter, cache.KindOf(document, 1));
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

        private static void Render(TextView view)
        {
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();
        }

        [Theory]
        [InlineData("markdown", "# Title\n- item\n> quote\n```\ncode\n```")]
        [InlineData("json", "{\n  \"a\": [1, true, \"x\"]\n}")]
        public void A_colorizer_that_throws_is_caught_and_the_editor_falls_back_to_plain_text(string languageId, string text) => UiThread.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Document = new TextDocument(text) };
            var warnings = new System.Collections.Generic.List<string>();
            var language = new EditorLanguage(editor, () => throw new System.InvalidOperationException("no palette"), folds: false)
            {
                Warn = warnings.Add,
            };
            language.Apply(PadLanguages.ById(languageId)!);
            Assert.True(language.HasMarkdown || language.HasSyntaxColors);

            Render(editor.TextArea.TextView);   // every line's colorizing throws; none of it escapes
            PadLanguageWindowTests.Pump();

            Assert.Single(warnings);
            Assert.False(language.HasMarkdown);
            Assert.False(language.HasSyntaxColors);
            Assert.Same(PadLanguages.Plain, language.Current);

            language.Apply(PadLanguages.ById(languageId)!);   // the window asking again keeps it plain
            Assert.Same(PadLanguages.Plain, language.Current);
        });

        [Fact]
        public void The_background_renderer_and_bullets_report_a_failure_instead_of_throwing() => UiThread.Run(() =>
        {
            var view = new TextView { Document = new TextDocument("```\ncode\n```\n- item") };
            Render(view);
            var failures = new System.Collections.Generic.List<System.Exception>();
            var cache = new MarkdownDocumentCache(failures.Add);

            var background = new MarkdownBackgroundRenderer(cache, () => throw new System.InvalidOperationException(), failures.Add);
            using (var context = new System.Windows.Media.DrawingVisual().RenderOpen()) background.Draw(view, context);
            Assert.Single(failures);

            // Outside a line's construction there is no context to read: the generator bows out.
            var bullets = new BulletGenerator(cache, failures.Add);
            Assert.Equal(-1, bullets.GetFirstInterestedOffset(0));
            Assert.Equal(2, failures.Count);
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
        public void Fence_shading_draws_under_the_current_line_highlight() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var renderers = window.Editor.TextArea.TextView.BackgroundRenderers.ToList();
            int shading = renderers.FindIndex(r => r is MarkdownBackgroundRenderer);
            int currentLine = renderers.FindIndex(r => r.GetType().Name == "CurrentLineHighlightRenderer");

            Assert.True(window.Editor.Options.HighlightCurrentLine);
            Assert.True(currentLine >= 0);
            Assert.True(shading >= 0 && shading < currentLine, "the fence shading must be drawn first");
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
