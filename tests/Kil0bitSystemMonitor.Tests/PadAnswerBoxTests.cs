using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Ai;
using Xunit;

using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// MicaPad's answer box on the shared UI thread, never shown and never inside an Ask window:
    /// Markdown or plain text, read-only, in the pad's light or dark theme.
    /// </summary>
    public class PadAnswerBoxTests
    {
        private static string Rendered(PadAnswerBox box)
        {
            var document = box.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        [Fact]
        public void Markdown_is_rendered_and_the_raw_text_is_kept() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("**bold** and `code`");

            Assert.Equal("bold and code", Rendered(box));
            Assert.Equal("**bold** and `code`", box.Shown);
        });

        [Fact]
        public void Plain_text_keeps_its_markers_and_its_lines() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowPlain("**bold** and `code`\n# not a heading");

            Assert.Equal("**bold** and `code`\n# not a heading", Rendered(box));
            Assert.Equal("**bold** and `code`\n# not a heading", box.Shown);
        });

        [Fact]
        public void A_new_text_replaces_the_old_one() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("first");

            box.ShowPlain("second");
            Assert.Equal("second", Rendered(box));
            Assert.Equal("second", box.Shown);

            box.ShowMarkdown("");
            Assert.Equal("", Rendered(box));
            Assert.Equal("", box.Shown);
        });

        [Fact]
        public void The_box_is_read_only_and_offers_only_Copy_and_Select_all() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("text");

            Assert.True(box.IsReadOnly);
            Assert.True(box.IsDocumentEnabled);   // a code block inside can be selected and copied
            Assert.True(box.IsTabStop);           // keyboard users can reach it to select and copy
            var menu = Assert.IsType<ContextMenu>(box.ContextMenu);
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll },
                menu.Items.OfType<MenuItem>().Select(item => item.Command));
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Same(box, item.CommandTarget));
        });

        [Fact]
        public void A_new_box_is_dark_and_the_theme_changes_its_text_colour() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("text");
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Resources["Ask.Ink"]));

            box.ApplyTheme(true);
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Resources["Ask.Ink"]));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Foreground));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Document.Foreground));

            box.ApplyTheme(false);
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Resources["Ask.Ink"]));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Document.Foreground));   // the text already shown repaints
            Assert.Equal(Wpf(AskPalette.Light.Selection), BrushColor(box.SelectionBrush));
            Assert.NotEqual(Wpf(AskPalette.Dark.Ink), Wpf(AskPalette.Light.Ink));
        });

        [Fact]
        public void Every_menu_in_the_box_follows_the_pad_theme_without_an_ask_window() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ApplyTheme(false);
            box.ShowMarkdown("Run this:\n\n```\ncode\n```");
            var code = Assert.Single(AiAskWindowTests.Descendants<TextBox>(box.Document));
            var menus = new[] { Assert.IsType<ContextMenu>(box.ContextMenu), Assert.IsType<ContextMenu>(code.ContextMenu) };

            foreach (var menu in menus)
            {
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Light.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll },
                code.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Command));
            Assert.All(code.ContextMenu.Items.OfType<MenuItem>(), item => Assert.Same(code, item.CommandTarget));

            box.ApplyTheme(true);

            // A rendered answer is built again for the new theme (a diagram is a bitmap drawn for
            // one theme), so the code block and its menu are new ones: the menus the box has now.
            code = Assert.Single(AiAskWindowTests.Descendants<TextBox>(box.Document));
            menus = new[] { Assert.IsType<ContextMenu>(box.ContextMenu), Assert.IsType<ContextMenu>(code.ContextMenu) };
            foreach (var menu in menus)
            {
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Dark.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll },
                code.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Command));
        });

        [Fact]
        public void The_text_has_no_page_padding_before_and_after_the_box_builds_its_view() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("text");
            Assert.Equal(new Thickness(0), box.Document.PagePadding);

            Assert.True(box.ApplyTemplate());
            Assert.Equal(new Thickness(0), box.Document.PagePadding);

            box.ShowPlain("more");
            Assert.Equal(new Thickness(0), box.Document.PagePadding);
        });

        [Fact]
        public void Markdown_that_cannot_be_rendered_is_shown_as_plain_text_and_reported_once_by_type_only() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var box = new PadAnswerBox
            {
                BuildDocument = _ => throw new InvalidOperationException("the note says hunter2"),
                Warn = warnings.Add,
            };

            box.ShowMarkdown("**bold**\nline two");
            box.ShowMarkdown("**bold**\nline two and more");

            Assert.Equal("**bold**\nline two and more", Rendered(box));
            Assert.Equal("**bold**\nline two and more", box.Shown);
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);   // never the message: it could quote the answer
            Assert.DoesNotContain("bold", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void A_credential_marker_is_shown_as_it_is() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowPlain("user: admin\npassword: {{secret:K7Q2M9XD}}");
            Assert.Equal("user: admin\npassword: {{secret:K7Q2M9XD}}", Rendered(box));

            box.ShowMarkdown("The password is {{secret:K7Q2M9XD}}.");
            Assert.Equal("The password is {{secret:K7Q2M9XD}}.", Rendered(box));
        });

        // ---- links are text: nothing in the box navigates or opens anything ----------------------

        private static List<Hyperlink> Links(PadAnswerBox box) => AiAskWindowTests.Descendants<Hyperlink>(box.Document);

        [Fact]
        public void A_link_is_shown_as_its_label_and_its_address_and_is_not_a_link() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("See [site](https://example.com/x) for more.");

            Assert.Equal("See site (https://example.com/x) for more.", Rendered(box));
            Assert.Empty(Links(box));
            Assert.Equal("See [site](https://example.com/x) for more.", box.Shown);   // Copy still gets what the model wrote
        });

        [Fact]
        public void A_link_dressed_as_a_citation_shows_where_it_goes() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("The key is in the vault [2](https://evil.example/c?d=the+passage).");

            Assert.Equal("The key is in the vault 2 (https://evil.example/c?d=the+passage).", Rendered(box));
            Assert.Empty(Links(box));
        });

        [Fact]
        public void A_bare_address_stays_as_text_and_is_not_a_link() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("Open https://example.com today.");

            Assert.Equal("Open https://example.com today.", Rendered(box));   // once: the label is the address
            Assert.Empty(Links(box));
        });

        [Fact]
        public void A_mail_link_is_text_too() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("Write to [the admin](mailto:admin@example.com) or mailto:help@example.com.");

            Assert.Equal("Write to the admin (mailto:admin@example.com) or mailto:help@example.com.", Rendered(box));
            Assert.Empty(Links(box));
        });

        [Fact]
        public void A_label_keeps_its_style_and_a_label_that_is_another_address_shows_both() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("[**bold** site](https://example.com/x) and [https://good.example](https://evil.example/login)");

            Assert.Equal("bold site (https://example.com/x) and https://good.example (https://evil.example/login)", Rendered(box));
            Assert.Empty(Links(box));
            Run bold = Assert.Single(AiAskWindowTests.Descendants<Run>(box.Document), r => r.Text == "bold");
            Assert.Equal(FontWeights.SemiBold, bold.FontWeight);
        });

        [Fact]
        public void Links_in_a_heading_a_list_and_a_quote_are_text_too() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("# [a](https://a.example/1)\n\n- [b](https://b.example/2)\n  - [c](https://c.example/3)\n\n> [d](https://d.example/4)");

            Assert.Empty(Links(box));
            string text = Rendered(box);
            foreach (string expected in new[] { "a (https://a.example/1)", "b (https://b.example/2)", "c (https://c.example/3)", "d (https://d.example/4)" })
                Assert.Contains(expected, text, StringComparison.Ordinal);
        });

        [Fact]
        public void A_document_from_any_builder_loses_its_links() => UiThread.Run(() =>
        {
            var link = new Hyperlink(new Run("click me")) { NavigateUri = new Uri("https://other.example/path") };
            var box = new PadAnswerBox { BuildDocument = _ => new FlowDocument(new Paragraph(link)) };

            box.ShowMarkdown("anything");

            Assert.Equal("click me (https://other.example/path)", Rendered(box));
            Assert.Empty(Links(box));
        });

        [Fact]
        public void The_box_lays_out_on_its_own_and_wraps_to_the_width_it_is_given() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("# Title\n\nSome **text** with a [link](https://example.com), long enough that it has to wrap onto a second line in a narrow pane.\n\n- one\n- two\n\n```\ncode\n```");

            box.Measure(new Size(300, double.PositiveInfinity));
            box.Arrange(new Rect(box.DesiredSize));
            box.UpdateLayout();

            Assert.InRange(box.DesiredSize.Width, 1, 300);
            Assert.True(box.DesiredSize.Height > 100, "the height follows the text: " + box.DesiredSize.Height);
        });

        [Fact]
        public void A_table_with_a_link_in_a_cell_leaves_no_hyperlink() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("| Name | Count |\n|---|---|\n| [site](https://example.com/a) | 2 |");

            Assert.Empty(ChatDocument.All<Hyperlink>(box.Document));
            Assert.Contains("https://example.com/a", Rendered(box), StringComparison.Ordinal);
            Assert.Single(box.Document.Blocks.OfType<Table>());
        });

        [Fact]
        public void A_code_block_keeps_one_menu_on_its_text_box_and_none_on_its_Copy_button() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("```csharp\nint x;\n```");

            var text = Assert.Single(ChatDocument.All<System.Windows.Controls.TextBox>(box.Document));
            Assert.NotNull(text.ContextMenu);
            var copy = Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(box.Document));
            Assert.Null(copy.ContextMenu);
            Assert.True(box.IsDocumentEnabled);   // without it a button in the document is disabled
            Assert.True(copy.IsEnabled);
        });

        [Fact]
        public void The_Copy_button_of_a_code_block_copies_through_the_shared_hook() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("```\nint x;\n```");
            var copy = Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(box.Document));

            var copied = new List<string>();
            var previous = ChatClipboard.SetText;
            ChatClipboard.SetText = copied.Add;
            try
            {
                copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            finally
            {
                ChatClipboard.SetText = previous;
            }

            Assert.Equal(new[] { "int x;" }, copied);
        });

        /// <summary>
        /// True when a mouse at the middle of <paramref name="button"/>, in <paramref name="box"/>
        /// shown in a real (off screen) window, would reach the button: the button is laid out,
        /// enabled, and hit testing there finds it or something inside it. A click handler alone
        /// proves nothing about the box letting the mouse through.
        /// </summary>
        internal static bool MouseReaches(System.Windows.Controls.RichTextBox box, System.Windows.Controls.Button button)
        {
            var window = new Window
            {
                Width = 600,
                Height = 400,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = box,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                if (!button.IsLoaded || !button.IsEnabled || button.ActualWidth <= 0) return false;
                var middle = new System.Windows.Point(button.ActualWidth / 2, button.ActualHeight / 2);
                var hit = VisualTreeHelper.HitTest(window, button.TranslatePoint(middle, window))?.VisualHit;
                for (DependencyObject? at = hit; at != null; at = VisualTreeHelper.GetParent(at))
                    if (ReferenceEquals(at, button)) return true;
                return false;
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }

        [Fact]
        public void A_mouse_reaches_the_Copy_button_of_a_code_block_in_the_pad_box() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("```cs\nint x;\n```");
            var copy = Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(box.Document));

            Assert.True(MouseReaches(box, copy));
        });

        // ---- Mermaid diagrams: the real adapter over a renderer whose draws the test ends ----------

        private const string Flow = ChatDiagramFakes.Flow;

        private static (PadAnswerBox Box, ChatDiagrams Diagrams, FakeRenderer Renderer) DiagramBox()
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            return (new PadAnswerBox { Diagrams = diagrams }, diagrams, renderer);
        }

        private static List<string> Lines(PadAnswerBox box) =>
            ChatDocument.All<System.Windows.Controls.TextBlock>(box.Document).Select(t => t.Text).ToList();

        private static System.Windows.Controls.Image? Picture(PadAnswerBox box) =>
            ChatDocument.All<System.Windows.Controls.Image>(box.Document).SingleOrDefault();

        private static System.Windows.Controls.Button ButtonNamed(PadAnswerBox box, string content) =>
            Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(box.Document), b => Equals(b.Content, content));

        private static void RaiseClick(System.Windows.Controls.Button button) =>
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        /// <summary>Counts how often the box builds a Markdown document from here on.</summary>
        private static Func<int> CountBuilds(PadAnswerBox box)
        {
            int builds = 0;
            var build = box.BuildDocument;
            box.BuildDocument = raw =>
            {
                builds++;
                return build(raw);
            };
            return () => builds;
        }

        [Fact]
        public Task When_a_draw_ends_the_box_shows_the_picture_without_being_handed_the_text_again() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            string text = "Look:\n\n" + ChatDiagramFakes.Block();

            box.ShowMarkdown(text);

            Assert.Single(renderer.Calls);
            Assert.Null(renderer.Calls[0].Request.KrokiServer);
            Assert.Contains("Drawing the diagram…", Lines(box));
            Assert.Null(Picture(box));

            // The pane skips a draw when the text is unchanged, so the box redraws itself.
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture(width: 100, height: 50));

            Assert.NotNull(Picture(box));
            Assert.DoesNotContain("Drawing the diagram…", Lines(box));
            Assert.Equal(text, box.Shown);
            Assert.Single(renderer.Calls);

            for (int i = 0; i < 20; i++) box.ShowMarkdown(text + "\n\n" + new string('x', i + 1));   // the answer streams on
            Assert.NotNull(Picture(box));
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public void Clear_empties_the_box_and_its_Source_choices_and_forgets_the_pictures() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };
            box.ShowMarkdown(ChatDiagramFakes.Block());
            RaiseClick(ButtonNamed(box, "Source"));
            Assert.Equal(Visibility.Collapsed, Picture(box)!.Visibility);

            box.Clear();

            Assert.Equal("", box.Shown);
            Assert.Equal("", Rendered(box));
            Assert.Empty(ChatDocument.All<System.Windows.Controls.Image>(box.Document));
            Assert.Empty(ChatDocument.All<TextBox>(box.Document));
            Assert.Equal(1, diagrams.Cleared);

            box.ShowMarkdown(ChatDiagramFakes.Block());
            Assert.Equal(Visibility.Visible, Picture(box)!.Visibility);   // the Source choice went with the result
        });

        [Fact]
        public void Clear_does_not_throw_when_the_pictures_cannot_be_forgotten_or_there_are_none() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var broken = new PadAnswerBox { Diagrams = new BrokenDiagrams(), Warn = warnings.Add };
            broken.ShowMarkdown("text");

            broken.Clear();
            new PadAnswerBox().Clear();

            Assert.Equal("", broken.Shown);
            Assert.Contains("NotSupportedException", Assert.Single(warnings), StringComparison.Ordinal);
        });

        private sealed class BrokenDiagrams : IChatDiagrams
        {
            public ChatDiagramState Get(string source, bool dark, Action? whenDone) => new(ChatDiagramStatus.Off);

            public void Forget(string source, bool dark)
            {
            }

            public void Clear() => throw new NotSupportedException("the answer was secret");
        }

        [Fact]
        public void Plain_text_asks_for_no_picture() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };

            box.ShowPlain(ChatDiagramFakes.Block());

            Assert.Empty(diagrams.Gets);
            Assert.Equal(ChatDiagramFakes.Block(), Rendered(box));   // exactly as it would go into the note
        });

        [Fact]
        public Task A_picture_that_arrives_after_plain_text_took_the_box_changes_nothing() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            box.ShowMarkdown(ChatDiagramFakes.Block());
            var builds = CountBuilds(box);

            box.ShowPlain("Something else.");
            var shown = box.Document;
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

            Assert.Equal(0, builds());
            Assert.Same(shown, box.Document);
            Assert.Equal("Something else.", Rendered(box));
        });

        [Fact]
        public Task A_picture_that_arrives_after_Clear_changes_nothing_and_is_not_kept() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            box.ShowMarkdown(ChatDiagramFakes.Block());
            var builds = CountBuilds(box);
            bool told = false;
            diagrams.Get(Flow, true, () => told = true);   // behind the box's own waiter: when this one is told, the box was

            box.Clear();
            renderer.Finish(0, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false));
            await ChatDiagramFakes.Until(() => told, "the end of the draw");

            Assert.Equal(0, builds());
            Assert.Equal("", Rendered(box));
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);   // the cleared draw's outcome was not kept
        });

        [Fact]
        public Task A_picture_that_arrives_after_the_text_changed_shows_the_text_as_it_is_now() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            box.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nThe first ending.");

            box.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nAnother ending.");
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

            Assert.NotNull(Picture(box));
            Assert.Contains("Another ending.", Rendered(box), StringComparison.Ordinal);
            Assert.DoesNotContain("The first ending.", Rendered(box), StringComparison.Ordinal);
            Assert.Equal(ChatDiagramFakes.Block() + "\n\nAnother ending.", box.Shown);
        });

        [Fact]
        public Task A_theme_change_draws_the_diagrams_again_for_the_new_theme_and_leaves_plain_text_alone() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            box.ShowMarkdown(ChatDiagramFakes.Block());
            Assert.True(renderer.Calls[0].Request.Dark);   // a box is dark until it is told
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            var dark = Picture(box)!.Source;

            box.ApplyTheme(false);

            Assert.Equal(2, renderer.Calls.Count);
            Assert.False(renderer.Calls[1].Request.Dark);
            Assert.Null(Picture(box));
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramFakes.Picture());
            Assert.NotSame(dark, Picture(box)!.Source);

            box.ApplyTheme(false);   // the theme it has: nothing is built again
            box.ApplyTheme(true);
            Assert.Same(dark, Picture(box)!.Source);
            Assert.Equal(2, renderer.Calls.Count);

            box.ShowPlain(ChatDiagramFakes.Block());
            var plain = box.Document;
            box.ApplyTheme(false);
            Assert.Same(plain, box.Document);
            Assert.Equal(2, renderer.Calls.Count);
        });

        [Fact]
        public Task A_diagram_that_fails_is_drawn_once_however_often_the_box_is_shown_its_text() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            var builds = CountBuilds(box);
            string text = ChatDiagramFakes.Block();
            box.ShowMarkdown(text);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload));

            Assert.Equal(2, builds());   // the text, and one redraw for the end of the draw
            Assert.Contains("This diagram could not be drawn: " + DiagramText.RuntimeMissing, Lines(box));
            Assert.Empty(Links(box));    // the engine's help link is not offered in an answer

            await Task.Delay(150);       // left alone, it does not go round again
            Assert.Equal(2, builds());
            for (int i = 0; i < 50; i++) box.ShowMarkdown(text + "\n\n" + new string('x', i + 1));
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public Task Try_again_in_the_box_starts_one_new_draw_and_the_box_draws_itself_for_it() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            string text = ChatDiagramFakes.Block();
            box.ShowMarkdown(text);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.EngineStopped, lasting: false));
            var retry = ButtonNamed(box, "Try again");
            Assert.Equal("Draw this diagram again", retry.ToolTip);
            Assert.True(MouseReaches(box, retry));

            RaiseClick(retry);

            Assert.Equal(2, renderer.Calls.Count);                    // one press, one draw; the pane was not handed the text again
            Assert.Contains("Drawing the diagram…", Lines(box));
            Assert.Equal(text, box.Shown);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramFakes.Picture());
            Assert.NotNull(Picture(box));
            Assert.DoesNotContain(ChatDocument.All<System.Windows.Controls.Button>(box.Document), b => Equals(b.Content, "Try again"));
            Assert.Equal(2, renderer.Calls.Count);
        });

        [Fact]
        public void The_source_of_a_diagram_and_its_picture_get_menus_in_the_pads_look() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };
            box.ApplyTheme(false);

            box.ShowMarkdown(ChatDiagramFakes.Block());

            var source = Assert.Single(ChatDocument.All<TextBox>(box.Document));
            var text = Assert.IsType<ContextMenu>(source.ContextMenu);
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll }, text.Items.OfType<MenuItem>().Select(item => item.Command));
            var picture = Assert.IsType<ContextMenu>(Picture(box)!.ContextMenu);
            Assert.Equal(new object[] { "Copy image", "Copy source" }, picture.Items.OfType<MenuItem>().Select(item => item.Header));
            foreach (var menu in new[] { text, picture })
            {
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Light.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }

            // The entries still do their work after the menu changed its look.
            var copied = new List<string>();
            var previous = ChatClipboard.SetText;
            ChatClipboard.SetText = copied.Add;
            try
            {
                picture.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Copy source")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally
            {
                ChatClipboard.SetText = previous;
            }
            Assert.Equal(new[] { Flow }, copied);
        });

        [Fact]
        public void A_mouse_reaches_the_Source_toggle_and_the_Copy_button_of_a_diagram_in_the_pad_box() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };
            box.ShowMarkdown(ChatDiagramFakes.Block());

            Assert.True(MouseReaches(box, ButtonNamed(box, "Source")));
            Assert.True(MouseReaches(box, ButtonNamed(box, "Copy")));
        });

        [Fact]
        public Task Two_boxes_keep_their_Source_choices_and_their_redraws_apart() => UiThread.RunAsync(async () =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var one = new PadAnswerBox { Diagrams = diagrams };
            var two = new PadAnswerBox { Diagrams = diagrams };
            one.ShowMarkdown(ChatDiagramFakes.Block());
            two.ShowMarkdown(ChatDiagramFakes.Block());
            Assert.Single(renderer.Calls);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            Assert.NotNull(Picture(one));
            Assert.NotNull(Picture(two));   // each box was told, and drew itself

            RaiseClick(ButtonNamed(one, "Source"));
            one.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nmore");
            two.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nmore");

            Assert.Equal(Visibility.Collapsed, Picture(one)!.Visibility);   // kept across the redraw
            Assert.Equal(Visibility.Visible, Picture(two)!.Visibility);     // and not shared
        });

        [Fact]
        public void A_text_that_does_not_continue_the_one_shown_forgets_its_Source_choices() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };
            box.ShowMarkdown(ChatDiagramFakes.Block());
            RaiseClick(ButtonNamed(box, "Source"));

            box.ShowPlain(ChatDiagramFakes.Block());       // the same text, as the note would get it
            box.ShowMarkdown(ChatDiagramFakes.Block());
            Assert.Equal(Visibility.Collapsed, Picture(box)!.Visibility);   // still the same result: the choice stays

            box.ShowMarkdown("Another answer.");
            box.ShowMarkdown(ChatDiagramFakes.Block());
            Assert.Equal(Visibility.Visible, Picture(box)!.Visibility);     // another result came between
        });

        [Fact]
        public void A_link_beside_a_diagram_is_still_text() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var box = new PadAnswerBox { Diagrams = diagrams };

            box.ShowMarkdown("See [site](https://example.com/a).\n\n" + ChatDiagramFakes.Block());

            Assert.Empty(Links(box));
            Assert.NotNull(Picture(box));
            Assert.Contains("https://example.com/a", Rendered(box), StringComparison.Ordinal);
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference WaitingBox(ChatDiagrams diagrams)
        {
            var box = new PadAnswerBox { Diagrams = diagrams };
            box.ShowMarkdown(ChatDiagramFakes.Block());
            return new WeakReference(box);
        }

        [Fact]
        public void A_draw_still_running_does_not_keep_its_box_alive() => UiThread.Run(() =>
        {
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };

            WeakReference box = WaitingBox(diagrams);
            Assert.Single(renderer.Calls);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(box.IsAlive);   // a MicaPad window that closed mid-draw is not held until the draw ends
        });

        [Fact]
        public void A_box_without_a_source_of_pictures_shows_mermaid_as_code() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();   // ChatDiagrams.Current is null in tests

            box.ShowMarkdown(ChatDiagramFakes.Block());

            Assert.Null(box.Diagrams);
            Assert.Null(Picture(box));
            Assert.Equal("Copy code", Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(box.Document)).ToolTip);
        });
    }
}
