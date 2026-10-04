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
using MenuItem = System.Windows.Controls.MenuItem;
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
                        FixWithAi = (open, close, kind, message) => Fixes.Add((open, close, kind, message)),
                        AiOn = () => AiOn,
                        SetUpAi = () => SetUps++,
                    },
                };
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            /// <summary>What Fix with AI was asked for: the fence lines, the fence word and the message.</summary>
            public List<(int Open, int Close, string Kind, string Message)> Fixes { get; } = new();
            public bool AiOn { get; set; } = true;
            public int SetUps { get; private set; }

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
        public void A_picture_is_told_the_screen_scale_of_its_editor() => UiThread.Run(() =>
        {
            var board = new Board("```dot\ndigraph { a -> b }\n```");

            board.Renderer.Finish(0, DiagramFakes.Picture(120, 60));
            board.PumpAndRender();

            Assert.Equal(System.Windows.Media.VisualTreeHelper.GetDpi(board.View).PixelsPerDip, board.PictureUnder(3)!.View.PixelsPerDip);
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
            /// <summary>What every drawing gives.</summary>
            public DiagramResult Result { get; init; } = DiagramFakes.Picture(120, 60);

            public bool TryGetCached(string key, out DiagramResult result)
            {
                result = null!;
                return false;
            }

            public System.Threading.Tasks.Task<DiagramResult> RenderAsync(DiagramRequest request, object slot) =>
                System.Threading.Tasks.Task.FromResult(Result);
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

        [Fact]
        public void A_dollar_block_and_a_math_fence_get_math_pictures() => UiThread.Run(() =>
        {
            var board = new Board("Energy:\n$$\nE = mc^2\n$$\n```latex\n\\int_0^1 x\\,dx\n```");

            Assert.Null(board.PictureUnder(2));   // the opening $$ gets none
            Assert.True(board.PictureUnder(4)!.IsDrawing);
            Assert.NotNull(board.PictureUnder(7));
            Assert.Equal(2, board.Renderer.Calls.Count);
            Assert.All(board.Renderer.Calls, c => Assert.Same(DiagramKinds.Math, c.Request.Kind));
            Assert.Equal("E = mc^2", board.Renderer.Calls[0].Request.Source);
            Assert.Equal("\\int_0^1 x\\,dx", board.Renderer.Calls[1].Request.Source);
            Assert.Null(board.Renderer.Calls[0].Request.KrokiServer);
        });

        [Fact]
        public void The_kroki_form_takes_its_type_from_the_first_line() => UiThread.Run(() =>
        {
            var board = new Board("```kroki\nplantuml\n@startuml\na -> b\n@enduml\n```\n\n```kroki\nmermaid\nflowchart LR\n  a --> b\n```");

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", board.PictureUnder(6)!.ErrorText!.Text);
            var offline = Assert.Single(board.Renderer.Calls);   // mermaid under kroki is drawn on this PC
            Assert.Equal(DiagramEngine.Mermaid, offline.Request.Kind.Engine);
            Assert.Equal("flowchart LR\n  a --> b", offline.Request.Source);
            Assert.Null(offline.Request.KrokiServer);

            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();

            Assert.Equal(2, board.Renderer.Calls.Count);
            var kroki = board.Renderer.Calls[1].Request;
            Assert.Equal("plantuml", kroki.Kind.KrokiType);
            Assert.Equal("@startuml\na -> b\n@enduml", kroki.Source);
            Assert.Equal("https://kroki.io", kroki.KrokiServer);
        });

        // ---- Fix with AI (MicaPad AI part 2, spec 2.2) ---------------------------------------------

        /// <summary>Two Mermaid blocks, on lines 1 to 4 and 6 to 9; the tests make the second one fail.</summary>
        private const string TwoBlocks = "```mermaid\nflowchart LR\n  a --> b\n```\ntext\n```mermaid\nflowchart LR\n  c --> d --\n```\nafter";

        private const string ParseError = "Parse error on line 2";

        /// <summary>A board over <see cref="TwoBlocks"/> whose first block is drawn and whose second shows <see cref="ParseError"/>.</summary>
        private static Board SecondBlockFails(bool aiOn = true)
        {
            var board = new Board(TwoBlocks) { AiOn = aiOn };
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.Renderer.Finish(1, DiagramResult.Failure(ParseError, lasting: true));
            board.PumpAndRender();
            return board;
        }

        /// <summary>The first item of the menu of the error box under line <paramref name="line"/>: Fix with AI, or Set up AI….</summary>
        private static MenuItem FixEntry(Board board, int line) => board.PictureUnder(line)!.ContextMenu!.Items.OfType<MenuItem>().First();

        private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        [Fact]
        public void The_board_tells_the_error_of_the_block_around_a_line_and_nothing_for_a_block_that_renders() => UiThread.Run(() =>
        {
            var board = new Board(TwoBlocks);
            Assert.Null(board.Diagrams.FailureAt(7));                 // still drawing: no error is shown yet

            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.Renderer.Finish(1, DiagramResult.Failure(ParseError, lasting: true));
            board.PumpAndRender();

            var failure = new DiagramFailure(6, 9, "mermaid", ParseError);
            foreach (int line in new[] { 6, 7, 8, 9 }) Assert.Equal(failure, board.Diagrams.FailureAt(line));      // its fences are part of it
            foreach (int line in new[] { 1, 2, 3, 4 }) Assert.Null(board.Diagrams.FailureAt(line));                // this one renders
            foreach (int line in new[] { 5, 10, 0, -1, 11, 99 }) Assert.Null(board.Diagrams.FailureAt(line));      // in no block, or no line
        });

        [Fact]
        public void Fix_with_AI_on_the_error_box_names_the_blocks_lines_its_fence_word_and_the_message() => UiThread.Run(() =>
        {
            var board = SecondBlockFails();

            MenuItem entry = FixEntry(board, 9);
            Assert.Equal("Fix with AI", entry.Header);
            Click(entry);

            Assert.Equal((6, 9, "mermaid", ParseError), Assert.Single(board.Fixes));
            Assert.Equal(0, board.SetUps);
            // The block that renders keeps the picture menu, with no fix in it.
            Assert.Equal(new[] { "Copy picture", "Save as PNG…", "Save as SVG…" },
                         board.PictureUnder(4)!.ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header));
        });

        [Fact]
        public void A_click_names_the_lines_the_block_is_on_at_the_click() => UiThread.Run(() =>
        {
            var board = SecondBlockFails();
            MenuItem entry = FixEntry(board, 9);

            board.Editor.Document.Insert(0, "# Title\n\n");           // the block moved two lines down since its box was drawn
            Click(entry);

            Assert.Equal((8, 11, "mermaid", ParseError), Assert.Single(board.Fixes));
        });

        [Fact]
        public void A_block_that_is_gone_at_the_click_asks_for_no_fix() => UiThread.Run(() =>
        {
            var board = SecondBlockFails();
            MenuItem entry = FixEntry(board, 9);
            var document = board.Editor.Document;

            var opening = document.GetLineByNumber(6);
            document.Remove(opening.Offset, opening.Length);          // its opening fence is deleted: line 9 now opens a block that never closes
            Click(entry);

            Assert.Empty(board.Fixes);
            Assert.Null(board.Diagrams.FailureAt(8));
        });

        [Fact]
        public void While_AI_is_off_the_error_box_entry_reads_Set_up_AI_and_asks_for_no_fix() => UiThread.Run(() =>
        {
            var board = SecondBlockFails(aiOn: false);

            MenuItem entry = FixEntry(board, 9);
            Assert.Equal("Set up AI…", entry.Header);
            Click(entry);

            Assert.Empty(board.Fixes);
            Assert.Equal(1, board.SetUps);
        });

        [Fact]
        public void No_fix_is_offered_for_an_error_that_no_source_can_cure() => UiThread.Run(() =>
        {
            // The board's own notice: Kroki is off.
            var needsKroki = new Board("```plantuml\n@startuml\na -> b\n@enduml\n```");
            Assert.NotNull(needsKroki.PictureUnder(5)!.ErrorText);
            Assert.Null(needsKroki.PictureUnder(5)!.ContextMenu);
            Assert.Null(needsKroki.Diagrams.FailureAt(3));

            // A passing failure: the server is out of reach.
            var unreachable = new Board("```d2\na -> b\n```") { Server = "https://kroki.io" };
            unreachable.Language.RefreshDiagrams();
            unreachable.Render();
            unreachable.Renderer.Finish(0, DiagramResult.Failure("The Kroki server could not be reached (kroki.io).", lasting: false));
            unreachable.PumpAndRender();
            Assert.NotNull(unreachable.PictureUnder(3)!.ErrorText);
            Assert.Null(unreachable.PictureUnder(3)!.ContextMenu);
            Assert.Null(unreachable.Diagrams.FailureAt(2));

            // The board's own notice: the source is too long to draw (and to send).
            var tooLarge = new Board("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength + 1) + "\n```");
            var scroll = (System.Windows.Controls.Primitives.IScrollInfo)tooLarge.View;
            scroll.CanVerticallyScroll = true;
            scroll.SetVerticalOffset(scroll.ExtentHeight - scroll.ViewportHeight);
            tooLarge.Render();
            Assert.Equal("Too large to draw", tooLarge.PictureUnder(3)!.ErrorText!.Text);
            Assert.Null(tooLarge.PictureUnder(3)!.ContextMenu);
            Assert.Null(tooLarge.Diagrams.FailureAt(3));
        });

        [Fact]
        public void What_a_Kroki_server_says_of_the_source_can_be_fixed() => UiThread.Run(() =>
        {
            var board = new Board("```d2\na -> \n```") { Server = "https://kroki.io" };
            board.Language.RefreshDiagrams();
            board.Render();
            board.Renderer.Finish(0, DiagramResult.Failure("syntax error near line 1", lasting: true));
            board.PumpAndRender();

            Assert.Equal(new DiagramFailure(1, 3, "d2", "syntax error near line 1"), board.Diagrams.FailureAt(2));
            Assert.Equal("Fix with AI", FixEntry(board, 3).Header);

            board.Server = null;                                      // Kroki is turned off: the box now says so, and no source cures that
            board.Language.RefreshDiagrams();
            board.Render();
            Assert.Null(board.PictureUnder(3)!.ContextMenu);
            Assert.Null(board.Diagrams.FailureAt(2));
        });

        [Fact]
        public void The_needs_Kroki_notice_is_not_fixable_while_Kroki_is_being_turned_on() => UiThread.Run(() =>
        {
            var board = new Board("```plantuml\n@startuml\na -> b\n@enduml\n```");
            string notice = "PlantUML needs Kroki — turn it on in Settings → MicaPad.";
            Assert.Equal(notice, board.PictureUnder(5)!.ErrorText!.Text);

            board.Server = "https://kroki.io";                        // turned on in Settings; the block is not drawn again yet
            Assert.Null(board.Diagrams.FailureAt(3));                 // its box still shows the notice, which no source cures

            board.Language.RefreshDiagrams();                         // now it is being drawn, and the notice stays until that ends
            board.Render();
            Assert.Single(board.Renderer.Calls);
            Assert.Equal(notice, board.PictureUnder(5)!.ErrorText!.Text);
            Assert.Null(board.PictureUnder(5)!.ContextMenu);
            Assert.Null(board.Diagrams.FailureAt(3));

            board.Renderer.Finish(0, DiagramResult.Failure("Syntax Error? (line: 2)", lasting: true));   // what the server said of the source
            board.PumpAndRender();
            Assert.Equal(new DiagramFailure(1, 5, "plantuml", "Syntax Error? (line: 2)"), board.Diagrams.FailureAt(3));
            Assert.Equal("Fix with AI", FixEntry(board, 5).Header);
        });

        [Fact]
        public void The_too_large_notice_is_not_fixable_while_a_block_made_smaller_waits_for_its_redraw() => UiThread.Run(() =>
        {
            var board = new Board("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength + 1) + "\n```");
            var scroll = (System.Windows.Controls.Primitives.IScrollInfo)board.View;
            scroll.CanVerticallyScroll = true;
            scroll.SetVerticalOffset(scroll.ExtentHeight - scroll.ViewportHeight);
            board.Render();
            Assert.Equal("Too large to draw", board.PictureUnder(3)!.ErrorText!.Text);

            var document = board.Editor.Document;
            var big = document.GetLineByNumber(2);
            document.Replace(big.Offset, big.Length, "digraph { a -> }");   // made small; typing has not paused, so nothing is drawn yet
            Assert.Null(board.Diagrams.FailureAt(2));                 // its box still says Too large to draw

            scroll.SetVerticalOffset(0);
            board.PumpAndRender();                                    // drawn again while typing: the notice stays until the pause
            Assert.Empty(board.Renderer.Calls);
            Assert.Equal("Too large to draw", board.PictureUnder(3)!.ErrorText!.Text);
            Assert.Null(board.PictureUnder(3)!.ContextMenu);
            Assert.Null(board.Diagrams.FailureAt(2));

            board.Diagrams.DrawDue();                                 // the pause: the engine is asked, and says what is wrong with the source
            board.Render();
            board.Renderer.Finish(0, DiagramResult.Failure("syntax error in line 1 near '}'", lasting: true));
            board.PumpAndRender();
            Assert.Equal(new DiagramFailure(1, 3, "dot", "syntax error in line 1 near '}'"), board.Diagrams.FailureAt(2));
        });

        [Fact]
        public void A_math_block_is_named_math_or_by_its_fence_word_and_a_kroki_block_by_its_type_line() => UiThread.Run(() =>
        {
            var board = new Board("$$\nx^{2\n$$\n```latex\n\\frac{1\n```\n```kroki\nmermaid\nflowchart LR\n  a --> b --\n```");
            Assert.Equal(3, board.Renderer.Calls.Count);
            board.Renderer.Finish(0, DiagramResult.Failure("Missing close brace", lasting: true));
            board.Renderer.Finish(1, DiagramResult.Failure("Missing close brace", lasting: true));
            board.Renderer.Finish(2, DiagramResult.Failure(ParseError, lasting: true));
            board.PumpAndRender();

            Assert.Equal(new DiagramFailure(1, 3, "math", "Missing close brace"), board.Diagrams.FailureAt(2));
            Assert.Equal(new DiagramFailure(4, 6, "latex", "Missing close brace"), board.Diagrams.FailureAt(5));
            Assert.Equal(new DiagramFailure(7, 11, "mermaid", ParseError), board.Diagrams.FailureAt(8));

            Click(FixEntry(board, 3));
            Assert.Equal((1, 3, "math", "Missing close brace"), Assert.Single(board.Fixes));
        });

        [Fact]
        public void An_editor_that_gives_no_way_to_fix_has_no_menu_on_its_error_box() => UiThread.Run(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("```dot\ndigraph { a -> }\n```") };
            var language = new EditorLanguage(editor, () => PadPalette.Dark, folds: true)
            {
                Warn = _ => { },
                Diagrams = new DiagramServices
                {
                    Renderer = new ImmediateRenderer { Result = DiagramResult.Failure("syntax error", lasting: true) },
                    Enabled = () => true,
                    KrokiServer = () => null,
                    Pause = TimeSpan.FromHours(1),
                },
            };
            language.Apply(PadLanguages.Markdown);
            editor.TextArea.TextView.Measure(new Size(600, 400));
            editor.TextArea.TextView.Arrange(new Rect(0, 0, 600, 400));
            editor.TextArea.TextView.EnsureVisualLines();

            var picture = editor.TextArea.TextView.GetVisualLine(3)!.Elements.OfType<DiagramElement>().Single().Picture as DiagramPicture;
            Assert.Equal("syntax error", picture!.ErrorText!.Text);
            Assert.Null(picture.ContextMenu);
            Assert.Equal(new DiagramFailure(1, 3, "dot", "syntax error"), language.DiagramBoard!.FailureAt(2));   // the board still knows it
        });
    }
}
