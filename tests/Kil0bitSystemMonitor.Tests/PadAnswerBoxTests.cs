using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            foreach (var menu in menus)
            {
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Dark.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }
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
            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(width: 100, height: 50), box);

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

            Assert.Null(box.PendingRedraw);   // told of the picture, the box asked for no redraw: none comes later either
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
            renderer.Calls[0].Done.TrySetResult(DiagramFakes.Picture());   // the picture arrives; not through Finish, so the fake engine keeps nothing either
            await ChatDiagramFakes.Until(() => told, "the end of the draw");

            Assert.Null(box.PendingRedraw);                                                   // told of the picture, the cleared box asked for no redraw
            Assert.Equal(0, builds());
            Assert.Equal("", Rendered(box));
            Assert.Equal(0, diagrams.PicturesKept);                                           // the cleared draw's picture was not kept
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);   // so asking for it means drawing it again
            Assert.Equal(2, renderer.Calls.Count);
        });

        [Fact]
        public Task A_picture_that_arrives_after_the_text_changed_shows_the_text_as_it_is_now() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            box.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nThe first ending.");

            box.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nAnother ending.");
            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), box);

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
            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), box);
            var dark = Picture(box)!.Source;

            box.ApplyTheme(false);

            Assert.Equal(2, renderer.Calls.Count);
            Assert.False(renderer.Calls[1].Request.Dark);
            Assert.Null(Picture(box));
            await FinishAndRedraw(diagrams, renderer, 1, DiagramFakes.Picture(), box);
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
        public void A_theme_change_builds_nothing_for_an_answer_without_a_diagram_and_the_theme_is_kept_for_the_next_one() => UiThread.Run(() =>
        {
            var (box, _, renderer) = DiagramBox();
            const string text = "Some **text**.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```cs\nint x;\n```";
            box.ShowMarkdown(text);
            var builds = CountBuilds(box);
            var shown = box.Document;

            box.ApplyTheme(false);

            // Before diagrams a theme switch was a swap of brushes. For an answer that asked for no picture it still is.
            Assert.Equal(0, builds());
            Assert.Same(shown, box.Document);
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Document.Foreground));   // repainted all the same
            Assert.Empty(renderer.Calls);

            box.ShowMarkdown(text + "\n\n" + ChatDiagramFakes.Block());   // the theme was recorded: a diagram that comes later is drawn for it
            Assert.False(Assert.Single(renderer.Calls).Request.Dark);
            int before = builds();

            box.ApplyTheme(true);                                         // and now there is a picture to ask for again
            Assert.Equal(before + 1, builds());
            Assert.True(renderer.Calls[1].Request.Dark);
        });

        [Fact]
        public Task A_diagram_that_fails_is_drawn_once_however_often_the_box_is_shown_its_text() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, renderer) = DiagramBox();
            var builds = CountBuilds(box);
            string text = ChatDiagramFakes.Block();
            box.ShowMarkdown(text);

            await FinishAndRedraw(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload), box);

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
            await FinishAndRedraw(diagrams, renderer, 0, DiagramResult.Failure(DiagramText.EngineStopped, lasting: false), box);
            var retry = ButtonNamed(box, "Try again");
            Assert.Equal("Draw this diagram again", retry.ToolTip);
            Assert.True(MouseReaches(box, retry));

            RaiseClick(retry);
            await Redrawn(box);                                       // the press asks for the redraw; the box's timer makes it

            Assert.Equal(2, renderer.Calls.Count);                    // one press, one draw; the pane was not handed the text again
            Assert.Contains("Drawing the diagram…", Lines(box));
            Assert.Equal(text, box.Shown);

            await FinishAndRedraw(diagrams, renderer, 1, DiagramFakes.Picture(), box);
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

            await FinishAndRedraw(diagrams, renderer, 0, DiagramFakes.Picture(), one, two);
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

        // ---- keeping up while an answer streams (AI chat UI spec 1.5) ------------------------------
        // The pane paces the text it hands over. The box paces only what it draws by itself: a picture that arrived.

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        /// <summary>Waits, with the dispatcher free, until no redraw of these boxes is waiting for its timer.</summary>
        private static Task Redrawn(params PadAnswerBox[] boxes) =>
            ChatDiagramFakes.Until(() => boxes.All(box => box.PendingRedraw is null), "the redraw that waited");

        /// <summary>
        /// Ends a draw, and waits for the redraw each of these boxes asked for: a box that is told
        /// of a picture does not draw it then, its timer does.
        /// </summary>
        private static async Task FinishAndRedraw(ChatDiagrams diagrams, FakeRenderer renderer, int index, DiagramResult result, params PadAnswerBox[] boxes)
        {
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, index, result);
            await Redrawn(boxes);
        }

        /// <summary>
        /// A box that shows a link and one diagram, "being drawn" until the test says otherwise.
        /// The redraw it handed over is <c>Gets[0].WhenDone</c>: calling it is a picture arriving,
        /// as the adapter tells it (inline, on the UI thread).
        /// </summary>
        private static (PadAnswerBox Box, FakeChatDiagrams Diagrams, Func<int> Builds) WaitingForAPicture(Func<bool> pointerHeld)
        {
            var diagrams = new FakeChatDiagrams();
            var box = new PadAnswerBox { Diagrams = diagrams, PointerHeld = pointerHeld };
            box.ShowMarkdown("See [site](https://example.com/a).\n\n" + ChatDiagramFakes.Block());
            return (box, diagrams, CountBuilds(box));
        }

        [Fact]
        public Task A_picture_that_arrives_is_drawn_by_the_boxes_timer_and_several_arrivals_give_one_redraw() => UiThread.RunAsync(async () =>
        {
            var (box, diagrams, builds) = WaitingForAPicture(() => false);
            Action arrive = diagrams.Gets[0].WhenDone!;
            diagrams.Answer = (_, _) => ChatDiagramFakes.Drawn();

            arrive();                                             // as the adapter tells its waiters: inline, several in a row
            arrive();
            arrive();

            Assert.Equal(0, builds());                            // not inside whatever ended the draw
            Assert.Null(Picture(box));
            Assert.NotNull(box.PendingRedraw);
            await Redrawn(box);
            Assert.Equal(1, builds());
            Assert.NotNull(Picture(box));
            Assert.Empty(Links(box));                             // the timer's redraw goes the way every draw goes: its links are text
            Assert.Contains("https://example.com/a", Rendered(box), StringComparison.Ordinal);
            Assert.Same(arrive, diagrams.Gets[^1].WhenDone);      // the same delegate at every build: the adapter tells a box once

            await Task.Delay(60);                                 // and nothing more by itself
            Assert.Equal(1, builds());
            Assert.Null(box.PendingRedraw);
        });

        [Fact]
        public void A_picture_that_arrives_is_drawn_no_sooner_than_the_last_draw_allows() => UiThread.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var (box, diagrams, builds) = WaitingForAPicture(() => false);
            box.LastRedrawCost = Ms(300);

            diagrams.Gets[0].WhenDone!();

            Assert.Equal(0, builds());
            RedrawWaits.AssertWaits(box.PendingRedraw, Ms(1200), clock);
        });

        [Fact]
        public Task A_picture_that_arrives_waits_while_the_pointer_is_held_and_the_timer_runs_again_for_the_plain_interval() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            int asked = 0;
            var (box, diagrams, builds) = WaitingForAPicture(() => { asked++; return held; });
            box.RedrawInterval = Ms(20);
            box.LastRedrawCost = Ms(15);                          // the redraw is due 60 ms after the last draw

            diagrams.Gets[0].WhenDone!();
            await ChatDiagramFakes.Until(() => asked > 0, "the redraw timer");

            Assert.Equal(0, builds());                            // a click that began on a button in the answer is not lost
            Assert.Equal(Ms(20), box.PendingRedraw);              // the plain interval, not the paced one

            held = false;
            await Redrawn(box);
            Assert.Equal(1, builds());
        });

        [Fact]
        public void Text_handed_to_the_box_is_drawn_at_once_while_the_pointer_is_held_and_whatever_the_last_draw_cost() => UiThread.Run(() =>
        {
            var (box, diagrams, builds) = WaitingForAPicture(() => true);
            box.LastRedrawCost = TimeSpan.FromSeconds(1);
            diagrams.Gets[0].WhenDone!();                         // a redraw waits for the box's timer
            Assert.NotNull(box.PendingRedraw);
            string more = box.Shown + "\n\nMore.";

            box.ShowMarkdown(more);                               // the pane paced this already: the box draws it now

            Assert.Equal(1, builds());
            Assert.Contains("More.", Rendered(box), StringComparison.Ordinal);
            Assert.Null(box.PendingRedraw);                       // and that draw showed whatever the redraw was waiting to show

            diagrams.Gets[^1].WhenDone!();
            Assert.NotNull(box.PendingRedraw);
            box.ShowPlain(more);                                  // Source was turned on
            Assert.Equal(more, Rendered(box));
            Assert.Null(box.PendingRedraw);                       // plain text has no picture to wait for
            Assert.Equal(1, builds());
        });

        [Fact]
        public void A_theme_change_draws_the_box_again_at_once_while_the_pointer_is_held_and_whatever_the_last_draw_cost() => UiThread.Run(() =>
        {
            var (box, diagrams, builds) = WaitingForAPicture(() => true);
            box.LastRedrawCost = TimeSpan.FromSeconds(1);

            box.ApplyTheme(false);

            Assert.Equal(1, builds());
            Assert.False(diagrams.Gets[^1].Dark);                 // the picture is asked for in the new theme, now
            Assert.Null(box.PendingRedraw);
        });

        [Fact]
        public void Clear_stops_a_redraw_that_waits_and_a_picture_that_arrives_later_starts_none_and_no_cost_is_stored() => UiThread.Run(() =>
        {
            var (box, diagrams, builds) = WaitingForAPicture(() => true);
            Action arrive = diagrams.Gets[0].WhenDone!;
            arrive();
            Assert.NotNull(box.PendingRedraw);

            box.Clear();                                          // a credential was stored: nothing may draw the old answer again

            Assert.Null(box.PendingRedraw);
            Assert.Equal("", Rendered(box));
            arrive();                                             // the draw of the answer that went ends
            Assert.Null(box.PendingRedraw);
            RedrawWaits.ToLoaded();
            Assert.Equal(TimeSpan.Zero, box.LastRedrawCost);      // not even what drawing it cost is kept
            Assert.Equal(0, builds());
            Assert.Equal("", Rendered(box));
        });

        [Fact]
        public Task A_PointerHeld_that_throws_counts_as_not_held_and_is_reported_once_by_its_type() => UiThread.RunAsync(async () =>
        {
            var warnings = new List<string>();
            var (box, diagrams, builds) = WaitingForAPicture(() => throw new InvalidOperationException("the answer says hunter2"));
            box.Warn = warnings.Add;
            box.RedrawInterval = Ms(10);

            diagrams.Gets[0].WhenDone!();
            await Redrawn(box);
            diagrams.Gets[^1].WhenDone!();
            await Redrawn(box);

            Assert.Equal(2, builds());                            // each drawn: nothing held it back
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
        });

        /// <summary>
        /// Why the cost is read from a callback at Loaded priority. The box is in a real window,
        /// so the layout of a new document is done by the dispatcher (at Render priority) after
        /// the draw has returned. The cost is stored after that layout, not before it.
        /// </summary>
        [Fact]
        public void What_a_draw_cost_is_stored_after_the_layout_it_caused() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
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
                RedrawWaits.ToLoaded();                           // whatever showing the window left to do

                TimeSpan notYet = TimeSpan.FromTicks(-1);         // no cost a draw has
                box.LastRedrawCost = notYet;
                int layoutsBeforeTheCost = 0;
                box.LayoutUpdated += (_, _) =>
                {
                    if (box.LastRedrawCost == notYet) layoutsBeforeTheCost++;
                };
                double height = box.DesiredSize.Height;
                string table = "| a | b | c |\n|---|---|---|\n" + string.Concat(Enumerable.Range(1, 30).Select(i => "| " + i + " | two | three |\n"));

                box.ShowMarkdown(table);

                Assert.Equal(notYet, box.LastRedrawCost);         // not when the build returns
                Assert.Equal(0, layoutsBeforeTheCost);            // and nothing is laid out yet: the dispatcher does that
                RedrawWaits.ToLoaded();

                Assert.True(layoutsBeforeTheCost > 0, "the new document was laid out before its cost was stored");
                Assert.True(box.LastRedrawCost > TimeSpan.Zero, "the cost of the draw is stored");
                Assert.True(box.IsMeasureValid && box.IsArrangeValid, "nothing is left to lay out");
                Assert.True(box.DesiredSize.Height > height, "the table is laid out: " + box.DesiredSize.Height + " against " + height);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });

        [Fact]
        public void A_text_that_does_not_continue_the_one_shown_forgets_what_the_last_draw_cost() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("An answer");
            box.LastRedrawCost = Ms(300);

            box.ShowMarkdown("An answer, and more of it");
            Assert.Equal(Ms(300), box.LastRedrawCost);            // the same answer, streaming on

            box.ShowMarkdown("Another answer");
            Assert.Equal(TimeSpan.Zero, box.LastRedrawCost);      // a heavy answer does not slow the first redraws of the next
        });

        [Fact]
        public Task Try_again_in_the_box_under_a_held_pointer_is_drawn_when_the_button_is_released_and_needs_no_new_text() => UiThread.RunAsync(async () =>
        {
            bool held = true;
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Failed("It took too long", canRetry: true) };
            var box = new PadAnswerBox { Diagrams = diagrams, RedrawInterval = Ms(20), PointerHeld = () => held };
            box.ShowMarkdown(ChatDiagramFakes.Block());
            var builds = CountBuilds(box);
            int asked = diagrams.Gets.Count;

            RaiseClick(ButtonNamed(box, "Try again"));            // the click itself comes with the pointer over the answer

            Assert.Single(diagrams.Forgotten);
            Assert.Equal(0, builds());
            Assert.NotNull(box.PendingRedraw);                    // asked for, though the text is the same: the timer will draw it
            await Task.Delay(80);                                 // some ticks, all under the held button
            Assert.Equal(0, builds());
            Assert.NotNull(box.PendingRedraw);

            held = false;                                         // the button is released
            await Redrawn(box);
            Assert.Equal(1, builds());                            // the same text, built again; the pane handed over nothing
            Assert.Equal(asked + 1, diagrams.Gets.Count);         // and that build asks for the diagram, which starts its draw
            Assert.Equal(ChatDiagramFakes.Block(), box.Shown);
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference BoxWithARedrawWaiting()
        {
            var diagrams = new FakeChatDiagrams();
            var box = new PadAnswerBox { Diagrams = diagrams, PointerHeld = () => false };
            box.ShowMarkdown(ChatDiagramFakes.Block());
            box.LastRedrawCost = Ms(500);                         // the next redraw waits two seconds: its timer runs through the collections
            box.ShowMarkdown(ChatDiagramFakes.Block() + "\n\nMore.");   // a draw whose cost is not stored yet
            diagrams.Gets[0].WhenDone!();
            Assert.NotNull(box.PendingRedraw);
            return new WeakReference(box);
        }

        [Fact]
        public void A_redraw_that_waits_and_a_cost_not_yet_stored_do_not_keep_their_box_alive() => UiThread.Run(() =>
        {
            WeakReference box = BoxWithARedrawWaiting();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(box.IsAlive);   // neither the timer that runs nor the callback that waits holds the box
        });
    }
}
