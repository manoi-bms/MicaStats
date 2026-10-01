using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Pictures in a Markdown editor (spec section 4 and "Testing": editor), over a fake renderer.
    /// The 600 ms pause is the board's DrawDue, called by hand (R8); its timer is set to an hour so
    /// it never fires inside a test.
    /// </summary>
    public class DiagramBoardTests
    {
        private sealed class Board
        {
            public Board(string text, PadPalette? palette = null)
            {
                Palette = palette ?? PadPalette.Dark;
                Editor = new TextEditor { Document = new TextDocument(text) };
                Language = new EditorLanguage(Editor, () => Palette, folds: true)
                {
                    Warn = Warnings.Add,
                    Diagrams = new DiagramServices
                    {
                        Renderer = Renderer,
                        Enabled = () => Enabled,
                        KrokiServer = () => Server,
                        Pause = TimeSpan.FromHours(1),
                        TrySetClipboardImage = png =>
                        {
                            Clipboard = png;
                            return ClipboardWorks;
                        },
                        AskSavePath = (name, filter) =>
                        {
                            AskedName = name;
                            AskedFilter = filter;
                            return SavePath;
                        },
                        ShowStatus = Status.Add,
                        Warn = Warnings.Add,
                    },
                };
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            public PadPalette Palette { get; set; }
            public TextEditor Editor { get; }
            public EditorLanguage Language { get; }
            public FakeRenderer Renderer { get; } = new();
            public bool Enabled { get; set; } = true;
            public string? Server { get; set; }
            public List<string> Status { get; } = new();
            public List<string> Warnings { get; } = new();
            public byte[]? Clipboard { get; private set; }
            public bool ClipboardWorks { get; set; } = true;
            public string? SavePath { get; set; }
            public string? AskedName { get; private set; }
            public string? AskedFilter { get; private set; }

            public DiagramBoard Diagrams => Language.DiagramBoard!;
            public TextView View => Editor.TextArea.TextView;

            public void Render()
            {
                View.Measure(new Size(600, 400));
                View.Arrange(new Rect(0, 0, 600, 400));
                View.EnsureVisualLines();
            }

            /// <summary>Runs what the dispatcher has queued (a finished draw's continuation, a fence repaint), then lays out again.</summary>
            public void PumpAndRender()
            {
                PadLanguageWindowTests.Pump();
                Render();
            }

            /// <summary>The picture under line <paramref name="number"/>, or null.</summary>
            public DiagramPicture? PictureUnder(int number) =>
                View.GetVisualLine(number)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as DiagramPicture;
        }

        [Fact]
        public void The_pause_after_typing_is_600_ms() =>
            Assert.Equal(TimeSpan.FromMilliseconds(600), new DiagramServices { Renderer = new FakeRenderer(), Enabled = () => true, KrokiServer = () => null }.Pause);

        [Fact]
        public void The_fence_cache_pairs_closing_and_opening_lines() => UiThread.Run(() =>
        {
            var document = new TextDocument("```mermaid\nx\n```\n~~~\ny");
            var cache = new MarkdownDocumentCache();

            Assert.Equal(1, cache.OpeningLineOf(document, 3));
            Assert.Equal(3, cache.ClosingLineOf(document, 1));
            Assert.Equal(0, cache.OpeningLineOf(document, 4));   // opens and never closes
            Assert.Equal(0, cache.ClosingLineOf(document, 4));
            Assert.Equal(0, cache.OpeningLineOf(document, 2));

            document.Insert(0, "```\n");   // "```mermaid" is now code inside the new block
            Assert.Equal(1, cache.OpeningLineOf(document, 4));
            Assert.Equal(0, cache.OpeningLineOf(document, 2));
            Assert.Equal(4, cache.ClosingLineOf(document, 1));
        });

        [Fact]
        public void A_mermaid_block_gets_one_picture_under_its_closing_fence() => UiThread.Run(() =>
        {
            var board = new Board("# Notes\n```mermaid\nflowchart LR\n  a --> b\n```\nafter");

            Assert.Null(board.PictureUnder(2));
            Assert.Null(board.PictureUnder(6));
            Assert.True(board.PictureUnder(5)!.IsDrawing);
            var call = Assert.Single(board.Renderer.Calls);
            Assert.Equal("flowchart LR\n  a --> b", call.Request.Source);
            Assert.Equal(PadThemes.Dark, call.Request.Theme);
            Assert.Equal(DiagramRequest.Css(PadPalette.Dark.Text), call.Request.Foreground);
            Assert.Equal(DiagramRequest.Css(PadPalette.Dark.Background), call.Request.Background);
            Assert.Null(call.Request.KrokiServer);

            board.Renderer.Finish(0, DiagramFakes.Picture(120, 60));
            board.PumpAndRender();

            Assert.Equal(120, board.PictureUnder(5)!.Image!.Width);
            Assert.Single(board.Renderer.Calls);   // drawn again from the cache
            Assert.Equal(1, board.Diagrams.Draws);
        });

        [Fact]
        public void Another_language_or_draw_diagrams_off_gets_no_picture() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            Assert.NotNull(board.PictureUnder(4));

            board.Language.Apply(PadLanguages.ById("json")!);
            board.Render();
            Assert.Null(board.Language.DiagramBoard);
            Assert.Null(board.PictureUnder(4));
            Assert.Empty(board.View.ElementGenerators.OfType<DiagramGenerator>());

            board.Language.Apply(PadLanguages.Markdown);
            board.Enabled = false;
            board.Language.RefreshDiagrams();
            board.Render();
            Assert.Null(board.Language.DiagramBoard);
            Assert.Null(board.PictureUnder(4));

            board.Enabled = true;
            board.Language.RefreshDiagrams();
            board.Render();
            Assert.NotNull(board.PictureUnder(4));
            Assert.Single(board.View.ElementGenerators.OfType<DiagramGenerator>());
        });

        [Fact]
        public void An_unclosed_block_gets_no_picture_until_it_is_closed() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b");
            Assert.Empty(board.Renderer.Calls);

            board.Editor.Document.Insert(board.Editor.Document.TextLength, "\n```");
            board.PumpAndRender();

            Assert.True(board.PictureUnder(4)!.IsDrawing);
            Assert.Empty(board.Renderer.Calls);   // still typing

            board.Diagrams.DrawDue();
            board.Render();
            Assert.Single(board.Renderer.Calls);
        });

        [Fact]
        public void Typing_in_a_block_draws_once_after_the_pause_and_keeps_the_old_picture() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture(120, 60));
            board.PumpAndRender();
            var document = board.Editor.Document;

            document.Insert(document.GetLineByNumber(3).EndOffset, " --> c");
            board.PumpAndRender();
            document.Insert(document.GetLineByNumber(3).EndOffset, " --> d");
            board.View.Redraw();
            board.PumpAndRender();

            Assert.Single(board.Renderer.Calls);                      // nothing while typing
            Assert.Equal(120, board.PictureUnder(4)!.Image!.Width);   // the old picture stays

            board.Diagrams.DrawDue();
            board.Render();

            Assert.Equal(2, board.Renderer.Calls.Count);
            Assert.Equal("flowchart LR\n  a --> b --> c --> d", board.Renderer.Calls[1].Request.Source);
            Assert.Equal(120, board.PictureUnder(4)!.Image!.Width);   // still, until the new one is ready

            board.Renderer.Finish(1, DiagramFakes.Picture(200, 60));
            board.PumpAndRender();
            Assert.Equal(200, board.PictureUnder(4)!.Image!.Width);
        });

        [Fact]
        public void An_error_result_shows_the_error_box() => UiThread.Run(() =>
        {
            var board = new Board("```dot\ndigraph { a -> }\n```");

            board.Renderer.Finish(0, DiagramResult.Failure("syntax error in line 1 near '}'", lasting: true));
            board.PumpAndRender();

            Assert.Equal("syntax error in line 1 near '}'", board.PictureUnder(3)!.ErrorText!.Text);
        });

        [Fact]
        public void A_block_over_the_limit_says_it_is_too_large_and_is_not_drawn() => UiThread.Run(() =>
        {
            var board = new Board("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength + 1) + "\n```");
            // AvalonEdit splits a 50,001-character line into ~460 rows, so the closing fence is far below the first screen: scroll to it.
            var scroll = (System.Windows.Controls.Primitives.IScrollInfo)board.View;
            scroll.CanVerticallyScroll = true;   // a ScrollViewer sets this; without an owner, scrolling is refused
            scroll.SetVerticalOffset(scroll.ExtentHeight - scroll.ViewportHeight);
            board.Render();

            Assert.Equal("Too large to draw", board.PictureUnder(3)!.ErrorText!.Text);
            Assert.Empty(board.Renderer.Calls);
        });

        [Fact]
        public void A_plantuml_block_needs_kroki_until_it_is_on() => UiThread.Run(() =>
        {
            var board = new Board("```plantuml\n@startuml\na -> b\n@enduml\n```");

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", board.PictureUnder(5)!.ErrorText!.Text);
            Assert.Empty(board.Renderer.Calls);

            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();

            var call = Assert.Single(board.Renderer.Calls);
            Assert.Equal("https://kroki.io", call.Request.KrokiServer);
            Assert.Equal("plantuml", call.Request.Kind.KrokiType);
        });

        [Fact]
        public void A_passing_failure_is_not_asked_again_until_the_settings_change() => UiThread.Run(() =>
        {
            var board = new Board("```d2\na -> b\n```");
            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();
            board.Renderer.Finish(0, DiagramResult.Failure("The Kroki server could not be reached (kroki.io).", lasting: false));
            board.PumpAndRender();
            Assert.Equal("The Kroki server could not be reached (kroki.io).", board.PictureUnder(3)!.ErrorText!.Text);

            for (int i = 0; i < 3; i++)   // scrolled away and back, repainted
            {
                board.View.Redraw();
                board.Render();
            }
            Assert.Single(board.Renderer.Calls);

            board.Language.RefreshDiagrams();   // Kroki or its server changed in Settings
            board.Render();
            Assert.Equal(2, board.Renderer.Calls.Count);
        });

        [Fact]
        public void Hide_code_folds_the_lines_inside_the_block_and_show_code_unfolds_them() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```\nafter");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();
            var document = board.Editor.Document;
            var closing = document.GetLineByNumber(4);
            var manager = board.Language.Folding!.Manager!;
            Assert.Equal("Hide code", board.PictureUnder(4)!.CodeButton!.Content);

            board.PictureUnder(4)!.View.ToggleCode!();
            board.Render();

            var fold = Assert.Single(manager.AllFoldings, f => f.IsFolded);
            Assert.Equal(document.GetLineByNumber(1).EndOffset, fold.StartOffset);
            Assert.Equal(document.GetLineByNumber(3).EndOffset, fold.EndOffset);
            Assert.True(board.Diagrams.IsCodeHidden(closing));
            Assert.Equal("Show code", board.PictureUnder(4)!.CodeButton!.Content);

            board.Language.Folding.Update();   // the recompute after an edit keeps it folded
            Assert.Single(manager.AllFoldings, f => f.IsFolded);

            board.PictureUnder(4)!.View.ToggleCode!();
            board.Render();
            Assert.DoesNotContain(manager.AllFoldings, f => f.IsFolded);
            Assert.False(board.Diagrams.IsCodeHidden(closing));
            Assert.Equal("Hide code", board.PictureUnder(4)!.CodeButton!.Content);
        });

        [Fact]
        public void Only_the_blocks_in_view_are_drawn() => UiThread.Run(() =>
        {
            string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => "```dot\ndigraph { a" + i + " -> b }\n```\ntext"));

            var board = new Board(text);

            Assert.InRange(board.Renderer.Calls.Count, 1, 10);
        });

        [Fact]
        public void A_draw_finishing_after_the_board_detached_changes_nothing() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            var first = board.Diagrams;

            board.Editor.Document = new TextDocument("other text");
            board.Language.Apply(PadLanguages.Markdown);
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            Assert.NotSame(first, board.Language.DiagramBoard);
            Assert.Equal("other text", board.Editor.Document.Text);
            Assert.Empty(board.Warnings);
        });

        [Fact]
        public void A_theme_switch_draws_the_other_theme_once() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            board.Palette = PadPalette.Light;
            board.Language.Redraw();
            board.Render();
            Assert.Equal(PadThemes.Light, board.Renderer.Calls[1].Request.Theme);
            board.Renderer.Finish(1, DiagramFakes.Picture());
            board.PumpAndRender();

            board.Palette = PadPalette.Dark;
            board.Language.Redraw();
            board.Render();
            Assert.Equal(2, board.Renderer.Calls.Count);   // the dark picture came from the cache
        });

        [Fact]
        public void Copy_picture_in_the_dark_theme_copies_the_light_drawing() => UiThread.Run(() =>
        {
            var board = new Board("```dot\ndigraph { a -> b }\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            board.PictureUnder(3)!.View.CopyPicture!();
            var light = board.Renderer.Calls[1];
            Assert.Equal(PadThemes.Light, light.Request.Theme);
            Assert.Equal(DiagramRequest.Css(PadPalette.Light.Text), light.Request.Foreground);
            byte[] png = (byte[])DiagramFakes.Png.Clone();
            board.Renderer.Finish(1, DiagramResult.Picture(png, DiagramFakes.Svg, 100, 50, paper: false));
            board.PumpAndRender();

            Assert.Same(png, board.Clipboard);
            Assert.Empty(board.Status);

            board.ClipboardWorks = false;
            board.PictureUnder(3)!.View.CopyPicture!();   // the light drawing is cached now
            Assert.Equal("Clipboard busy, try again", Assert.Single(board.Status));
        });

        [Fact]
        public void A_folded_heading_section_ending_in_a_diagram_shows_no_picture() => UiThread.Run(() =>
        {
            var board = new Board("# S\n```mermaid\nflowchart LR\n  a --> b\n```\n\n# Next");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();
            var document = board.Editor.Document;
            var manager = board.Language.Folding!.Manager!;
            Assert.NotNull(board.PictureUnder(5));

            var section = manager.AllFoldings.First(f => document.GetLineByOffset(f.StartOffset).LineNumber == 1);
            section.IsFolded = true;
            board.PumpAndRender();
            Assert.Empty(board.View.GetVisualLine(1)!.Elements.OfType<DiagramElement>());

            section.IsFolded = false;
            board.PumpAndRender();
            Assert.NotNull(board.PictureUnder(5));
        });

        [Fact]
        public void A_renderer_that_answers_at_once_shows_the_picture_in_the_first_render() => UiThread.Run(() =>
        {
            var renderer = new ImmediateRenderer();
            var editor = new TextEditor { Document = new TextDocument("```dot\ndigraph { a -> b }\n```") };
            var warnings = new List<string>();
            var language = new EditorLanguage(editor, () => PadPalette.Dark, folds: true)
            {
                Warn = warnings.Add,
                Diagrams = new DiagramServices { Renderer = renderer, Enabled = () => true, KrokiServer = () => null, Pause = TimeSpan.FromHours(1), Warn = warnings.Add },
            };
            language.Apply(PadLanguages.Markdown);
            editor.TextArea.TextView.Measure(new Size(600, 400));
            editor.TextArea.TextView.Arrange(new Rect(0, 0, 600, 400));
            editor.TextArea.TextView.EnsureVisualLines();

            var picture = editor.TextArea.TextView.GetVisualLine(3)!.Elements.OfType<DiagramElement>().Single().Picture as DiagramPicture;
            Assert.Equal(120, picture!.Image!.Width);
            PadLanguageWindowTests.Pump();
            Assert.Empty(warnings);
        });

        private sealed class ImmediateRenderer : IDiagramRenderer
        {
            public bool TryGetCached(string key, out DiagramResult result)
            {
                result = null!;
                return false;
            }

            public System.Threading.Tasks.Task<DiagramResult> RenderAsync(DiagramRequest request, object slot) =>
                System.Threading.Tasks.Task.FromResult(DiagramFakes.Picture(120, 60));
        }

        [Fact]
        public void An_older_draw_finishing_after_a_newer_request_is_ignored() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            var document = board.Editor.Document;
            document.Insert(document.GetLineByNumber(3).Offset, "  a --> c\n");
            board.Diagrams.DrawDue();
            board.Render();
            Assert.Equal(2, board.Renderer.Calls.Count);

            board.Renderer.Finish(0, DiagramFakes.Picture(111, 60));   // the draw of the old text
            board.PumpAndRender();
            Assert.True(board.PictureUnder(5)!.IsDrawing);

            board.Renderer.Finish(1, DiagramFakes.Picture(200, 60));
            board.PumpAndRender();
            Assert.Equal(200, board.PictureUnder(5)!.Image!.Width);
        });

        [Fact]
        public void Save_as_writes_the_picture_where_the_user_chose() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var board = new Board("```dot\ndigraph { a -> b }\n```", PadPalette.Light);
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();
            var view = board.PictureUnder(3)!.View;

            board.SavePath = dir.PathOf("flow.svg");
            view.SaveSvg!();
            Assert.Equal("diagram.svg", board.AskedName);
            Assert.Equal("SVG picture (*.svg)|*.svg", board.AskedFilter);
            Assert.Equal(DiagramFakes.Svg, File.ReadAllText(board.SavePath));

            board.SavePath = dir.PathOf("flow.png");
            view.SavePng!();
            Assert.Equal("diagram.png", board.AskedName);
            Assert.Equal("PNG picture (*.png)|*.png", board.AskedFilter);
            Assert.Equal(DiagramFakes.Png, File.ReadAllBytes(board.SavePath));

            board.SavePath = null;   // the user cancelled
            view.SavePng!();
            Assert.Single(board.Renderer.Calls);   // every export came from the cache
            Assert.Empty(board.Status);
        });
    }
}
