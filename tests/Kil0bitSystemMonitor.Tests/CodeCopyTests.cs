using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Copy button on fenced blocks (ruling R5): where it shows, what it copies, and where it never appears.</summary>
    public class CodeCopyTests
    {
        private const string Prose = "Some prose.\n\n```cs\nint a = 1;\nint b = 2;\n```\n\nMore prose.";

        /// <summary>A bare editor showing Markdown with the Copy button, laid out as a shown 600 x 400 editor would be.</summary>
        private sealed class Pad
        {
            public Pad(string text)
            {
                Editor = new TextEditor { Document = new TextDocument(text) };
                Language = new EditorLanguage(Editor, () => Palette, folds: false) { CodeCopy = Copied.Add, Warn = Warnings.Add };
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            public PadPalette Palette { get; set; } = PadPalette.Dark;
            public TextEditor Editor { get; }
            public EditorLanguage Language { get; }
            public List<string> Copied { get; } = new();
            public List<string> Warnings { get; } = new();
            public TextView View => Editor.TextArea.TextView;
            public CodeCopyLayer Layer => Assert.Single(View.Layers.OfType<CodeCopyLayer>());

            public void Render()
            {
                View.Measure(new Size(600, 400));
                View.Arrange(new Rect(0, 0, 600, 400));
                View.EnsureVisualLines();
            }

            /// <summary>Where line <paramref name="number"/> starts on screen, in text-view coordinates.</summary>
            public double TopOf(int number) => View.GetVisualLine(number)!.VisualTop - View.VerticalOffset;

            /// <summary>Scrolls as the editor's scroll viewer would, then lays the new lines out.</summary>
            public void ScrollTo(double offset)
            {
                var scroll = (IScrollInfo)View;
                scroll.CanVerticallyScroll = true;   // a ScrollViewer sets this; without an owner, scrolling is refused
                scroll.SetVerticalOffset(offset);
            }
        }

        private static Color ColorOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

        [Fact]
        public void The_button_follows_the_mouse_over_fenced_blocks() => UiThread.Run(() =>
        {
            var pad = new Pad(Prose);
            var layer = pad.Layer;
            Assert.False(layer.ButtonShown);

            layer.ShowFor(4);

            Assert.True(layer.ButtonShown);
            Assert.Equal(3, layer.BlockOpeningLine);
            var bounds = layer.ButtonBounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0);
            Assert.InRange(bounds.Right, pad.View.ActualWidth - 8, pad.View.ActualWidth);
            Assert.InRange(bounds.Top, pad.TopOf(3), pad.TopOf(3) + 4);
            layer.UpdateLayout();
            Assert.Equal(bounds.TopLeft, layer.Button.TranslatePoint(new Point(0, 0), pad.View));
            // The mouse reaches the button where it sits, and the text everywhere else.
            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Assert.True(CodeCopyLayer.IsInside(VisualTreeHelper.HitTest(pad.View, center)!.VisualHit));
            Assert.False(CodeCopyLayer.IsInside(VisualTreeHelper.HitTest(pad.View, new Point(20, pad.TopOf(4) + 2))!.VisualHit));

            layer.ShowFor(1);
            Assert.False(layer.ButtonShown);
            layer.ShowFor(3);
            Assert.True(layer.ButtonShown);
            Assert.Equal(3, layer.BlockOpeningLine);
            layer.ShowFor(8);
            Assert.False(layer.ButtonShown);
            layer.ShowFor(6);
            Assert.True(layer.ButtonShown);
            Assert.Equal(3, layer.BlockOpeningLine);
            layer.ShowFor(7);
            Assert.False(layer.ButtonShown);

            // The mouse itself: a point over a line of the block, then one below the last line.
            layer.MouseAt(new Point(100, pad.TopOf(5) + 2));
            Assert.True(layer.ButtonShown);
            Assert.Equal(3, layer.BlockOpeningLine);
            layer.MouseAt(new Point(100, pad.TopOf(8) + 2));
            Assert.False(layer.ButtonShown);
            layer.MouseAt(new Point(100, 390));
            Assert.False(layer.ButtonShown);
        });

        [Fact]
        public void A_block_scrolled_past_its_fence_keeps_the_button_on_its_first_visible_line() => UiThread.Run(() =>
        {
            string code = string.Join("\n", Enumerable.Range(1, 80).Select(i => "line " + i + ";"));
            var pad = new Pad("Prose.\n```cs\n" + code + "\n```\nEnd.");
            pad.ScrollTo(pad.View.GetVisualTopByDocumentLine(30));
            pad.Render();
            var layer = pad.Layer;

            layer.ShowFor(40);

            Assert.True(layer.ButtonShown);
            Assert.Equal(2, layer.BlockOpeningLine);
            Assert.InRange(layer.ButtonBounds.Top, 0, 4);
            Assert.InRange(layer.ButtonBounds.Right, pad.View.ActualWidth - 8, pad.View.ActualWidth);
        });

        [Fact]
        public void The_button_keeps_to_the_right_edge_when_the_text_view_is_resized() => UiThread.Run(() =>
        {
            var pad = new Pad(Prose);
            var layer = pad.Layer;
            layer.ShowFor(4);
            layer.UpdateLayout();

            pad.View.Measure(new Size(900, 400));
            pad.View.Arrange(new Rect(0, 0, 900, 400));
            layer.UpdateLayout();

            Assert.True(layer.ButtonShown);
            Assert.InRange(layer.ButtonBounds.Right, 900 - 8, 900);
            Assert.Equal(layer.ButtonBounds.TopLeft, layer.Button.TranslatePoint(new Point(0, 0), pad.View));
        });

        [Fact]
        public void Over_the_button_it_stays_shown_where_it_covers_a_line_outside_the_block() => UiThread.Run(() =>
        {
            string prose = string.Join("\n", Enumerable.Range(1, 80).Select(i => "Prose " + i + "."));
            var pad = new Pad("Prose.\n```cs\nint a;\n```\n" + prose);
            pad.Editor.FontSize = 10;   // a small zoom: the button is taller than a line
            pad.ScrollTo(pad.View.GetVisualTopByDocumentLine(4));   // only the closing fence of the block is visible
            pad.Render();
            var layer = pad.Layer;

            layer.ShowFor(4);
            var bounds = layer.ButtonBounds;
            var low = new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - 1);
            Assert.True(pad.TopOf(5) < low.Y);   // the button's lower part covers the prose line below the fence

            layer.MouseAt(low);
            Assert.True(layer.ButtonShown);
            Assert.Equal(2, layer.BlockOpeningLine);
            layer.MouseAt(new Point(20, low.Y));   // the same line beside the button is prose
            Assert.False(layer.ButtonShown);
        });

        [Fact]
        public void Tilde_fences_and_longer_fences_around_backticks_copy_their_inside_text() => UiThread.Run(() =>
        {
            var tilde = new Pad("Prose.\n~~~\nplain code\n~~~\nProse.");
            tilde.Layer.ShowFor(3);
            Assert.True(tilde.Layer.ButtonShown);
            Assert.Equal(2, tilde.Layer.BlockOpeningLine);
            tilde.Layer.Click();
            Assert.Equal("plain code", Assert.Single(tilde.Copied));

            var nested = new Pad("````md\n```cs\nint a;\n```\n````");
            foreach (int line in new[] { 2, 3, 4 })
            {
                nested.Layer.ShowFor(line);
                Assert.True(nested.Layer.ButtonShown);
                Assert.Equal(1, nested.Layer.BlockOpeningLine);
            }
            nested.Layer.Click();
            Assert.Equal("```cs\nint a;\n```", Assert.Single(nested.Copied));
        });

        [Fact]
        public void A_folded_block_shows_the_button_on_its_fence_row_and_copies_the_hidden_code() => UiThread.Run(() =>
        {
            string text = "Prose.\n```mermaid\nflowchart LR\n  a --> b\n```\nProse.";
            var pad = new Pad(text);
            var document = pad.Editor.Document;
            // Hide code's fold: from the end of the opening fence line to the end of the last inside line.
            var foldings = ICSharpCode.AvalonEdit.Folding.FoldingManager.Install(pad.Editor.TextArea);
            foldings.CreateFolding(document.GetLineByNumber(2).EndOffset, document.GetLineByNumber(4).EndOffset).IsFolded = true;
            pad.Render();
            var layer = pad.Layer;
            Assert.Equal(4, pad.View.GetVisualLine(2)!.LastDocumentLine.LineNumber);   // one row: the fence and the hidden code

            layer.MouseAt(new Point(20, pad.TopOf(2) + 2));
            Assert.True(layer.ButtonShown);
            Assert.Equal(2, layer.BlockOpeningLine);
            Assert.InRange(layer.ButtonBounds.Top, pad.TopOf(2), pad.TopOf(2) + 4);
            layer.Click();
            Assert.Equal("flowchart LR\n  a --> b", Assert.Single(pad.Copied));

            layer.ShowFor(5);
            Assert.True(layer.ButtonShown);
            Assert.Equal(2, layer.BlockOpeningLine);
        });

        [Fact]
        public void Copy_copies_the_inside_lines_and_leaves_the_editor_alone() => UiThread.Run(() =>
        {
            string text = "First prose line.\r\n```cs\r\nint a = 1;\r\n\r\nint b = 2;\r\n```\r\nLast prose line.";
            var pad = new Pad(text);
            int last = text.LastIndexOf("prose", StringComparison.Ordinal);
            pad.Editor.Select(last, 5);
            var layer = pad.Layer;

            layer.ShowFor(4);
            Assert.True(layer.ButtonShown);
            layer.Click();

            Assert.Equal("int a = 1;\r\n\r\nint b = 2;", Assert.Single(pad.Copied));
            Assert.Equal(text, pad.Editor.Text);
            Assert.Equal(last + 5, pad.Editor.CaretOffset);
            Assert.Equal(last, pad.Editor.SelectionStart);
            Assert.Equal(5, pad.Editor.SelectionLength);
            Assert.False(layer.Button.Focusable);
        });

        [Fact]
        public void Every_fenced_block_with_code_gets_the_button_and_copies_only_its_code() => UiThread.Run(() =>
        {
            // An unclosed block copies to the end of the note.
            var unclosed = new Pad("Prose.\n```cs\nint a;\nint b;");
            unclosed.Layer.ShowFor(4);
            Assert.True(unclosed.Layer.ButtonShown);
            Assert.Equal(2, unclosed.Layer.BlockOpeningLine);
            unclosed.Layer.Click();
            Assert.Equal("int a;\nint b;", Assert.Single(unclosed.Copied));

            // The empty line after a note's last line break is not code: no trailing break (R5).
            var unclosedBreak = new Pad("Prose.\n```cs\nint a;\n\nint b;\n");
            unclosedBreak.Layer.ShowFor(6);
            Assert.True(unclosedBreak.Layer.ButtonShown);
            unclosedBreak.Layer.Click();
            Assert.Equal("int a;\n\nint b;", Assert.Single(unclosedBreak.Copied));

            // A block with no inside lines, or only blank ones, has nothing to copy.
            var empty = new Pad("```cs\n```\nProse.\n```js\n");
            foreach (int line in new[] { 1, 2, 3, 4, 5 })
            {
                empty.Layer.ShowFor(line);
                Assert.False(empty.Layer.ButtonShown);
            }
            var blank = new Pad("```cs\n  \n\t\n\n```");
            foreach (int line in new[] { 1, 2, 3, 4, 5 })
            {
                blank.Layer.ShowFor(line);
                Assert.False(blank.Layer.ButtonShown);
            }

            // Math and diagram source are fenced blocks too.
            var math = new Pad("Prose.\n$$\nE = mc^2\n$$");
            math.Layer.ShowFor(4);
            Assert.True(math.Layer.ButtonShown);
            Assert.Equal(2, math.Layer.BlockOpeningLine);
            math.Layer.Click();
            Assert.Equal("E = mc^2", Assert.Single(math.Copied));

            var diagram = new Pad("```mermaid\nflowchart LR\n  a --> b\n```");
            diagram.Layer.ShowFor(2);
            Assert.True(diagram.Layer.ButtonShown);
            diagram.Layer.Click();
            Assert.Equal("flowchart LR\n  a --> b", Assert.Single(diagram.Copied));
        });

        [Fact]
        public void Scrolling_an_edit_or_the_mouse_leaving_hides_the_button_until_the_next_mouse_move() => UiThread.Run(() =>
        {
            string code = string.Join("\n", Enumerable.Range(1, 80).Select(i => "line " + i + ";"));
            var pad = new Pad("```cs\n" + code + "\n```");
            var layer = pad.Layer;

            layer.ShowFor(3);
            Assert.True(layer.ButtonShown);
            pad.ScrollTo(pad.View.GetVisualTopByDocumentLine(10));
            Assert.False(layer.ButtonShown);
            pad.Render();
            layer.ShowFor(12);
            Assert.True(layer.ButtonShown);

            pad.Editor.Document.Insert(pad.Editor.Document.GetLineByNumber(12).Offset, "x");
            Assert.False(layer.ButtonShown);
            pad.Render();
            layer.ShowFor(12);
            Assert.True(layer.ButtonShown);

            pad.View.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.False(layer.ButtonShown);
            layer.ShowFor(12);
            Assert.True(layer.ButtonShown);
        });

        [Fact]
        public void The_button_says_copy_takes_no_focus_and_follows_the_theme() => UiThread.Run(() =>
        {
            var pad = new Pad(Prose);
            var layer = pad.Layer;
            layer.ShowFor(4);
            var button = layer.Button;

            Assert.Equal("Copy code", button.ToolTip);
            Assert.Equal("Copy code", AutomationProperties.GetName(button));
            Assert.False(button.Focusable);
            button.ApplyTemplate();
            var texts = new List<string>();
            void Collect(DependencyObject node)
            {
                if (node is TextBlock block) texts.Add(block.Text);
                foreach (object child in LogicalTreeHelper.GetChildren(node))
                    if (child is DependencyObject d) Collect(d);
            }
            Collect(button);
            Assert.Equal(2, texts.Count);
            Assert.Equal("\uE8C8", texts[0]);
            Assert.Equal("Copy", texts[1]);

            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Dark.Popup), ColorOf(button.Background));
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Dark.PopupBorder), ColorOf(button.BorderBrush));
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Dark.TextSoft), ColorOf(button.Foreground));

            pad.Palette = PadPalette.Light;
            pad.Language.Redraw();

            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Light.Popup), ColorOf(button.Background));
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Light.PopupBorder), ColorOf(button.BorderBrush));
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Light.TextSoft), ColorOf(button.Foreground));
        });

        [Fact]
        public void Only_markdown_tabs_of_the_main_editor_get_the_button() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            CodeCopyLayer[] Layers(TextEditor editor) => editor.TextArea.TextView.Layers.OfType<CodeCopyLayer>().ToArray();
            var note = env.Workspace.Active!;
            Assert.Single(Layers(window.Editor));
            Assert.Same(Layers(window.Editor)[0], window.LanguageView.CodeCopyLayer);

            window.ChooseLanguage(note, "json");
            Assert.Empty(Layers(window.Editor));
            Assert.Null(window.LanguageView.CodeCopyLayer);
            window.ChooseLanguage(note, null);
            Assert.Single(Layers(window.Editor));

            PadLanguageWindowTests.OpenFile(window, env, "a.json", "```cs\nint a;\n```");
            int json = env.Workspace.Open.Count - 1;
            Assert.Empty(Layers(window.Editor));
            window.SelectTab(0);
            window.SelectTab(json);
            window.SelectTab(0);
            Assert.Single(Layers(window.Editor));

            // The history preview shows Markdown without the button.
            env.Store.WriteSnapshot(note.Id, "```cs\nint a;\n```", new DateTime(2026, 10, 2, 10, 0, 0));
            env.Store.WriteSnapshot(note.Id, "```cs\nint b;\n```", new DateTime(2026, 10, 2, 11, 0, 0));
            Assert.True(window.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
            window.HistoryPanel.Versions.SelectedItem = window.HistoryPanel.Rows[1];
            Assert.True(window.PreviewLanguage.HasMarkdown);
            Assert.Empty(Layers(window.PreviewEditor));
            Assert.Null(window.PreviewLanguage.CodeCopyLayer);
        });

        [Fact]
        public void The_window_copies_the_code_and_says_how_many_lines() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = new List<string>();
            var warnings = new List<string>();
            window.SetClipboardText = copied.Add;
            window.Warn = warnings.Add;
            window.Editor.Document.Text = "```cs\nint secret = 1;\nint b;\n```\n$$\nx\n$$";
            PadLanguageWindowTests.Render(window);
            var layer = window.LanguageView.CodeCopyLayer!;

            layer.ShowFor(2);
            layer.Click();
            Assert.Equal("int secret = 1;\nint b;", Assert.Single(copied));
            Assert.Equal("Copied 2 lines", window.StatusMessage.Text);

            layer.ShowFor(6);
            layer.Click();
            Assert.Equal("x", copied[^1]);
            Assert.Equal("Copied", window.StatusMessage.Text);

            window.SetClipboardText = _ => throw new ExternalException("busy");
            layer.ShowFor(2);
            layer.Click();
            Assert.Equal("Clipboard busy, try again", window.StatusMessage.Text);
            string warning = Assert.Single(warnings);
            Assert.Contains("ExternalException", warning);
            Assert.DoesNotContain("secret", warning);

            // An unclosed block at the end of a note that ends with a line break: two lines, no trailing break.
            window.SetClipboardText = copied.Add;
            window.Editor.Document.Text = "```cs\nint a;\nint b;\n";
            PadLanguageWindowTests.Render(window);
            layer.ShowFor(2);
            layer.Click();
            Assert.Equal("int a;\nint b;", copied[^1]);
            Assert.Equal("Copied 2 lines", window.StatusMessage.Text);

            window.ToggleTheme();
            Assert.Equal(PadThemeApplier.ToColor(window.Palette.Popup), ColorOf(layer.Button.Background));
        });

        [Fact]
        public void Pressing_the_button_leaves_the_caret_and_selection_alone() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            string text = "Prose here.\n```cs\nint a;\nint b;\n```\nMore prose here.";
            window.Editor.Document.Text = text;
            // Not shown: lay the whole editor out, so the button sits in the tree the mouse events travel through.
            window.Editor.Measure(new Size(600, 400));
            window.Editor.Arrange(new Rect(0, 0, 600, 400));
            window.Editor.TextArea.TextView.EnsureVisualLines();
            int last = text.LastIndexOf("prose", StringComparison.Ordinal);
            window.Editor.Select(last, 5);
            var layer = window.LanguageView.CodeCopyLayer!;
            layer.ShowFor(3);
            var button = layer.Button;

            // The same press on the text itself would move the caret and drop the selection.
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

            Assert.Equal(text, window.Editor.Text);
            Assert.Equal(last + 5, window.Editor.CaretOffset);
            Assert.Equal(last, window.Editor.SelectionStart);
            Assert.Equal(5, window.Editor.SelectionLength);

            // A right-click on the button is recognized, so the window's handler leaves the caret alone there; the text view is not.
            Assert.True(CodeCopyLayer.IsInside(button));
            Assert.True(CodeCopyLayer.IsInside(((System.Windows.Controls.Panel)button.Content).Children[0]));
            Assert.False(CodeCopyLayer.IsInside(window.Editor.TextArea.TextView));
        });

        [Fact]
        public void A_credential_in_a_block_is_copied_as_its_reference_never_as_the_secret() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            string id = env.Vault.Add("hunter2", "Bank", null);
            env.Vault.Unlock("246810");
            Assert.True(env.Vault.IsUnlocked);   // even with the vault open, the note holds only the reference
            var copied = new List<string>();
            window.SetClipboardText = copied.Add;
            string line = "password = " + SecretTokens.Format(id);
            window.Editor.Document.Text = "```ini\n" + line + "\n```";
            PadLanguageWindowTests.Render(window);
            var layer = window.LanguageView.CodeCopyLayer!;

            layer.ShowFor(2);
            layer.Click();

            Assert.Equal(line, Assert.Single(copied));
            Assert.DoesNotContain("hunter2", copied[0]);
        });

        [Fact]
        public void The_guide_and_the_readme_mention_the_copy_button()
        {
            string root = PadWindowTests.RepoRoot();
            string guide = File.ReadAllText(Path.Combine(root, "GUIDE.md"));
            string code = guide.Substring(guide.IndexOf("- **Code**:", StringComparison.Ordinal));
            code = code.Substring(0, code.IndexOf("\n- ", 1, StringComparison.Ordinal));
            Assert.Contains("**Copy**", code);
            Assert.Contains("top-right", code);

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            string english = readme.Split('\n').Single(l => l.StartsWith("* **Markdown the way Wiki.js shows it**", StringComparison.Ordinal));
            Assert.Contains("**Copy**", english);
            string thai = readme.Split('\n').Single(l => l.StartsWith("* **Markdown \u0E41\u0E1A\u0E1A\u0E17\u0E35\u0E48 Wiki.js \u0E41\u0E2A\u0E14\u0E07**", StringComparison.Ordinal));
            Assert.Contains("**Copy**", thai);
        }
    }
}
