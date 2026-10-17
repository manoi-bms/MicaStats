using System.Linq;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Localization;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class PadLocalizationTests
{
    [Fact]
    public void Thai_task_date_labels_preserve_the_timestamp_and_legacy_import_syntax()
    {
        string before = UiText.Language;
        try
        {
            var created = new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.FromHours(7));
            var task = new Kil0bitSystemMonitor.Services.Pad.TaskDateRecord("task", 0, "Prepare the release", created, created.AddHours(1));
            UiText.SetLanguage("en");
            string legacy = "- [x] Prepare the release " + Kil0bitSystemMonitor.Services.Pad.MarkdownTasks.FormatDates(task);
            UiText.SetLanguage("th");
            string display = Kil0bitSystemMonitor.Services.Pad.MarkdownTasks.FormatDisplayDates(task);
            Assert.Contains("เริ่ม:", display);
            Assert.Contains("เสร็จ:", display);
            Assert.Contains("2026-10-08 09:30", display);
            Assert.Equal(created, task.Created);
            Assert.Equal(created.AddHours(1), task.Finished);
            Assert.NotNull(Kil0bitSystemMonitor.Services.Pad.MarkdownTasks.LegacyDatesOf(legacy, created.Offset));
        }
        finally { UiText.SetLanguage(before); }
    }

    [Fact]
    public void Thai_task_menu_keeps_checkbox_routing_without_language_switch_editing_the_note()
    {
        string before = UiText.Language;
        try
        {
            UiText.SetLanguage("en");
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                const string original = "- [] Prepare the release";
                window.Editor.Text = original;
                window.Editor.Document.UndoStack.ClearAll();
                PadLanguageWindowTests.Render(window);
                var originalDates = window.Editor.TextArea.TextView.GetVisualLine(1)!.Elements.OfType<TaskDateElement>().Single();
                Assert.Contains("created:", originalDates.DisplayText);
                window.Editor.Select(5, 7);
                int caret = window.Editor.CaretOffset;
                int selectionStart = window.Editor.SelectionStart;
                int selectionLength = window.Editor.SelectionLength;

                UiText.SetLanguage("th");
                PadLanguageWindowTests.Pump();
                PadLanguageWindowTests.Render(window);
                var translatedDates = window.Editor.TextArea.TextView.GetVisualLine(1)!.Elements.OfType<TaskDateElement>().Single();
                Assert.Contains("เริ่ม:", translatedDates.DisplayText);
                Assert.NotSame(originalDates, translatedDates);

                Assert.Equal(original, window.Editor.Text);
                Assert.Equal(caret, window.Editor.CaretOffset);
                Assert.Equal(selectionStart, window.Editor.SelectionStart);
                Assert.Equal(selectionLength, window.Editor.SelectionLength);
                Assert.False(window.Editor.Document.UndoStack.CanUndo);

                // Task commands apply at the caret when there is no text selection.
                window.Editor.Select(caret, 0);
                window.RefreshEditorMenu();
                MenuItem[] actions = window.EditorMenu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(UiText.Tr("Complete task"), actions[0].Header);
                Assert.Equal(UiText.Tr("Edit task dates…"), actions[1].Header);

                PadMenuTests.Click(actions[0]);
                Assert.Equal("- [x] Prepare the release", window.Editor.Text);
                Assert.True(window.Editor.Document.UndoStack.CanUndo);
                window.Editor.Undo();
                Assert.Equal(original, window.Editor.Text);
            });
        }
        finally
        {
            UiText.SetLanguage(before);
        }
    }

    [Fact]
    public void Existing_MicaPad_labels_and_generated_footer_switch_Thai_and_back_to_English()
    {
        string before = UiText.Language;
        try
        {
            UiText.SetLanguage("en");
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Assert.Equal("Compare with current", window.CompareButton.Content);
                Assert.Equal("No closed notes.", window.ClosedEmpty.Text);
                Assert.StartsWith("Ln ", window.CaretText.Text);

                UiText.SetLanguage("th");
                PadLanguageWindowTests.Pump();
                Assert.Equal(UiText.Tr("Compare with current"), window.CompareButton.Content);
                Assert.Equal(UiText.Tr("No closed notes."), window.ClosedEmpty.Text);
                Assert.StartsWith("บรรทัด ", window.CaretText.Text);

                UiText.SetLanguage("en");
                PadLanguageWindowTests.Pump();
                Assert.Equal("Compare with current", window.CompareButton.Content);
                Assert.Equal("No closed notes.", window.ClosedEmpty.Text);
                Assert.StartsWith("Ln ", window.CaretText.Text);
            });
        }
        finally
        {
            UiText.SetLanguage(before);
        }
    }
}
