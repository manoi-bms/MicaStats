using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Window-level contracts for the Markdown source/preview workspace.</summary>
    public sealed class MarkdownSplitViewTests
    {
        private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        [Fact]
        public void The_remembered_view_defaults_off_and_the_preview_ratio_is_bounded_and_round_trips()
        {
            var config = new AppConfig();
            Assert.False(config.PadMarkdownSplitPreview);
            Assert.Equal(0.5, config.PadMarkdownPreviewRatio);

            config.PadMarkdownPreviewRatio = 0.1;
            Assert.Equal(0.2, config.PadMarkdownPreviewRatio);
            config.PadMarkdownPreviewRatio = 0.95;
            Assert.Equal(0.8, config.PadMarkdownPreviewRatio);
            config.PadMarkdownPreviewRatio = double.NaN;
            Assert.Equal(0.5, config.PadMarkdownPreviewRatio);

            var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig
            {
                PadMarkdownSplitPreview = true,
                PadMarkdownPreviewRatio = 0.63,
                PadPaneWidth = 517,
            }))!;
            Assert.True(restored.PadMarkdownSplitPreview);
            Assert.Equal(0.63, restored.PadMarkdownPreviewRatio);
            Assert.Equal(517, restored.PadPaneWidth);
            Assert.False(JsonSerializer.Deserialize<AppConfig>("{}")!.PadMarkdownSplitPreview);
            Assert.Equal(0.5, JsonSerializer.Deserialize<AppConfig>("{}")!.PadMarkdownPreviewRatio);
        }

        [Fact]
        public void Ctrl_Shift_M_opens_source_and_preview_without_changing_the_document_caret_selection_or_undo() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                const string markdown = "# Plan\n\n- [ ] Write tests\n\n```csharp\nvar answer = 42;\n```";
                window.Editor.Document.Text = markdown;
                window.Editor.Document.UndoStack.ClearAll();
                window.Editor.Document.Insert(markdown.Length, "\nnext");
                window.Editor.Select(2, 4);
                window.Editor.CaretOffset = 6;
                string before = window.Editor.Document.Text;
                OpenNote note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                var trackedTask = Assert.Single(note.Meta.TaskDates);

                Assert.True(window.CanMarkdownPreview);
                Assert.True(window.HandleShortcut(Key.M, CtrlShift));
                window.FlushMarkdownPreview();

                Assert.True(window.MarkdownPreviewVisible);
                Assert.True(config.PadMarkdownSplitPreview);
                Assert.Equal(Visibility.Visible, window.MarkdownPreviewPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.MarkdownSourceHeader.Visibility);
                Assert.Equal(before, window.Editor.Document.Text);
                Assert.Equal(6, window.Editor.CaretOffset);
                Assert.Equal(2, window.Editor.SelectionStart);
                Assert.Equal(4, window.Editor.SelectionLength);
                Assert.True(window.Editor.Document.UndoStack.CanUndo);
                Assert.Same(PadLanguages.Markdown, window.LanguageView.Current);
                Assert.True(window.LanguageView.SourceOnly);
                Assert.False(window.LanguageView.HasMarkdown);
                Assert.Equal(before, window.MarkdownPreviewPanel.Shown);
                Assert.Equal(trackedTask, Assert.Single(note.Meta.TaskDates));

                Assert.True(window.HandleShortcut(Key.M, CtrlShift));
                Assert.False(window.MarkdownPreviewVisible);
                Assert.False(config.PadMarkdownSplitPreview);
                Assert.False(window.LanguageView.SourceOnly);
                Assert.True(window.LanguageView.HasMarkdown);
                Assert.Equal(before, window.Editor.Document.Text);
                Assert.True(window.Editor.Document.UndoStack.CanUndo);
                Assert.Equal(trackedTask, Assert.Single(note.Meta.TaskDates));
            });

        [Fact]
        public void Typing_marks_the_preview_updating_and_flush_publishes_the_latest_source_once() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.Editor.Document.Text = "# First";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Assert.Equal("# First", window.MarkdownPreviewPanel.Shown);

                window.Editor.Document.Insert(window.Editor.Document.TextLength, "\n\n- [ ] one");
                window.Editor.Document.Insert(window.Editor.Document.TextLength, "\n- [x] two");

                Assert.Equal("# First", window.MarkdownPreviewPanel.Shown);
                Assert.Equal("Updating…", window.MarkdownPreviewPanel.StatusText.Text);
                window.FlushMarkdownPreview();
                Assert.Equal(window.Editor.Document.Text, window.MarkdownPreviewPanel.Shown);
                Assert.Equal("Live", window.MarkdownPreviewPanel.StatusText.Text);
            });

        [Fact]
        public void A_non_markdown_tab_cannot_preview_but_the_remembered_choice_returns_on_the_markdown_tab() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                OpenNote markdown = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                window.Editor.Document.Text = "# Kept";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();

                PadLanguageWindowTests.OpenFile(window, env, "data.json", "{ \"ok\": true }");
                Assert.False(window.CanMarkdownPreview);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.True(config.PadMarkdownSplitPreview);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                Assert.Equal("json", window.LanguageView.Current.Id);

                int markdownIndex = env.Workspace.TabsOf(window.WindowId).IndexOf(markdown);
                window.SelectTab(markdownIndex);
                window.FlushMarkdownPreview();
                Assert.True(window.CanMarkdownPreview);
                Assert.True(window.MarkdownPreviewVisible);
                Assert.Equal("# Kept", window.MarkdownPreviewPanel.Shown);
            });

        [Fact]
        public void Search_and_history_temporarily_take_the_right_side_then_restore_the_selected_preview() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.Editor.Document.Text = "# Workspace";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();

                window.ToggleSearch();
                Assert.Equal(Visibility.Visible, window.SearchPanel.Visibility);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                window.ToggleSearch();
                Assert.True(window.MarkdownPreviewVisible);
                window.FlushMarkdownPreview();

                Assert.True(window.HandleShortcut(Key.H, CtrlShift));
                Assert.Equal(Visibility.Visible, window.HistoryPanel.Visibility);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                Assert.True(window.HandleShortcut(Key.H, CtrlShift));
                Assert.True(window.MarkdownPreviewVisible);
                Assert.True(config.PadMarkdownSplitPreview);
            });

        [Fact]
        public void The_AI_pane_temporarily_replaces_the_preview_and_closing_it_resumes_the_same_source() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.AiEnabled = () => true;
                window.Editor.Document.Text = "# Local plan\n\nNo request is sent by this fixture.";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();

                window.ToggleAi();
                Assert.Equal(Visibility.Visible, window.AiPanel.Visibility);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                Assert.True(config.PadMarkdownSplitPreview);

                window.AiPaneHasFocus = () => true;
                window.ToggleAi();
                window.FlushMarkdownPreview();
                Assert.Equal(Visibility.Collapsed, window.AiPanel.Visibility);
                Assert.True(window.MarkdownPreviewVisible);
                Assert.Equal(window.Editor.Document.Text, window.MarkdownPreviewPanel.Shown);
            });

        [Fact]
        public void A_vault_card_releases_the_preview_and_cancel_restores_the_remembered_view() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.Editor.Document.Text = "synthetic-secret";
                window.Editor.SelectAll();
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Assert.Equal("synthetic-secret", window.MarkdownPreviewPanel.Shown);

                window.StoreSelection();

                Assert.True(window.VaultCard.IsOpen);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                Assert.True(config.PadMarkdownSplitPreview);

                window.VaultCard.OnSecondary();
                PadLanguageWindowTests.Pump();
                window.FlushMarkdownPreview();
                Assert.False(window.VaultCard.IsOpen);
                Assert.True(window.MarkdownPreviewVisible);
                Assert.Equal("synthetic-secret", window.MarkdownPreviewPanel.Shown);
            });

        [Fact]
        public void Opening_the_preview_builds_the_note_once_despite_config_notifications() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.Editor.Text = "# One rendering";
                int builds = 0;
                var build = window.MarkdownPreviewPanel.PreviewBox.MarkdownDocumentFactory;
                window.MarkdownPreviewPanel.PreviewBox.MarkdownDocumentFactory = (source, render) =>
                {
                    builds++;
                    return build(source, render);
                };
                window.ToggleMarkdownPreview();
                Assert.Equal(1, builds);
            });

        [Fact]
        public void Successful_credential_storage_never_restores_the_raw_value_to_the_preview() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                env.Vault.Load();
                env.Vault.Create("246810");
                window.Editor.Text = "synthetic-secret";
                window.Editor.SelectAll();
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                var rendered = new System.Collections.Generic.List<string>();
                var build = window.MarkdownPreviewPanel.PreviewBox.MarkdownDocumentFactory;
                window.MarkdownPreviewPanel.PreviewBox.MarkdownDocumentFactory = (source, render) =>
                {
                    rendered.Add(source);
                    return build(source, render);
                };

                window.StoreSelection();
                window.VaultCard.PrimaryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                PadLanguageWindowTests.Pump();
                window.FlushMarkdownPreview();

                Assert.Single(env.Vault.Credentials);
                Assert.DoesNotContain("synthetic-secret", window.Editor.Text);
                Assert.NotEmpty(rendered);
                Assert.All(rendered, source => Assert.DoesNotContain("synthetic-secret", source));
                Assert.True(window.MarkdownPreviewVisible);
                Assert.Equal(window.Editor.Text, window.MarkdownPreviewPanel.Shown);
            });

        [Fact]
        public void Hiding_releases_the_preview_and_presenting_the_window_resumes_the_remembered_view() =>
            PadWindowsTests.WithWindows((window, env, shown) =>
            {
                window.Editor.Document.Text = "# Hidden fixture";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Assert.Equal("# Hidden fixture", window.MarkdownPreviewPanel.Shown);
                window.Editor.Document.Insert(window.Editor.Document.TextLength, "\nlatest source");
                Assert.Equal("Updating…", window.MarkdownPreviewPanel.StatusText.Text);

                window.CloseByUser();

                Assert.True(window.IsHiddenByClose);
                Assert.False(window.MarkdownPreviewVisible);
                Assert.Equal("", window.MarkdownPreviewPanel.Shown);

                MicaPadWindow front = MicaPadWindow.ShowOrActivate(env.Workspace, new AppConfig(), null);
                front.FlushMarkdownPreview();
                Assert.Same(window, front);
                Assert.True(front.MarkdownPreviewVisible);
                Assert.Equal("# Hidden fixture\nlatest source", front.MarkdownPreviewPanel.Shown);
                Assert.Equal(new[] { window }, shown);
            });

        [Fact]
        public void Closing_the_window_releases_the_previewed_note_content() =>
            UiThread.Run(() =>
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
                var window = new MicaPadWindow(env.Workspace, new AppConfig());
                window.LoadSession();
                window.Editor.Document.Text = "# Private fixture\n\nsynthetic only";
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Assert.NotEmpty(window.MarkdownPreviewPanel.Shown);

                window.CloseForExit();

                Assert.Equal("", window.MarkdownPreviewPanel.Shown);
                Assert.False(window.MarkdownPreviewPanel.IsLimited);
            });
    }
}
