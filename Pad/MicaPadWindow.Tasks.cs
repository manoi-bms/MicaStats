using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;
using static Kil0bitSystemMonitor.Pad.EditorMenus;

namespace Kil0bitSystemMonitor.Pad
{
    public partial class MicaPadWindow
    {
        private readonly Dictionary<TextDocument, TaskDateController> _taskDates = new();
        private TaskDateGenerator? _taskDateGenerator;

        private void ConfigureTaskDates()
        {
            TaskDatesEditor.Dismissed += OnTaskDatesEditorDismissed;
            _taskDateGenerator = new TaskDateGenerator(
                (document, line) => _taskDates.TryGetValue(document, out var tasks) ? tasks.DatesOfLine(line) : null,
                () => _palette);
            Editor.TextArea.TextView.ElementGenerators.Add(_taskDateGenerator);
        }

        private void TrackTaskDates(OpenNote note, TextDocument document)
        {
            var tasks = new TaskDateController(document, note.Meta,
                () => ReferenceEquals(PadLanguages.Resolve(note.Meta.Language, note.Meta.SourcePath, _config.PadMarkdown, document.TextLength).Effective, PadLanguages.Markdown),
                () => Now(), () =>
                {
                    _workspace.NotifyTaskDatesChanged(note);
                    if (ReferenceEquals(Editor.Document, document)) Editor.TextArea.TextView.Redraw();
                });
            _taskDates.Add(document, tasks);
            tasks.Initialize();
        }

        private void UntrackTaskDates(TextDocument document)
        {
            if (_taskDates.Remove(document, out var tasks)) tasks.Dispose();
        }

        private void DetachTaskDates()
        {
            CloseTaskDateEditor();
            TaskDatesEditor.Dismissed -= OnTaskDatesEditorDismissed;
            if (_taskDateGenerator != null) Editor.TextArea.TextView.ElementGenerators.Remove(_taskDateGenerator);
            foreach (var tasks in _taskDates.Values) tasks.Dispose();
            _taskDates.Clear();
        }

        private bool IsCurrentTask(bool allowSelection = false) => !Editor.IsReadOnly && (allowSelection || Editor.SelectionLength == 0)
            && _taskDates.TryGetValue(Editor.Document, out var tasks)
            && tasks.IsTaskLine(Editor.Document.GetLineByOffset(Editor.CaretOffset).LineNumber);

        private bool HandleTaskKey(Key key, ModifierKeys modifiers)
        {
            if (key != Key.Enter || !Editor.IsKeyboardFocusWithin) return false;
            if (key != Key.Enter || !IsCurrentTask()) return false;
            if (modifiers == ModifierKeys.Control) return ToggleCurrentTask();
            return modifiers == ModifierKeys.None && ContinueCurrentTask();
        }

        internal bool ToggleCurrentTask()
        {
            if (!IsCurrentTask()) return false;
            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            if (MarkdownTasks.Toggle(Editor.Document.GetText(line)) is not { } edit) return false;
            Editor.Document.Replace(line.Offset + edit.Offset, edit.Length, edit.Text);
            return true;
        }

        internal bool ContinueCurrentTask()
        {
            if (!IsCurrentTask()) return false;
            var document = Editor.Document;
            var line = document.GetLineByOffset(Editor.CaretOffset);
            string newline = _shown?.Meta.LineEnding switch
            {
                LineEnding.Lf => "\n",
                LineEnding.Cr => "\r",
                _ => "\r\n",
            };
            if (MarkdownTasks.Continue(document.GetText(line), Editor.CaretOffset - line.Offset, newline) is not { } edit) return false;
            ApplyEdit(Editor, edit with { Offset = line.Offset + edit.Offset, SelectionStart = line.Offset + edit.SelectionStart });
            return true;
        }

        private void AddTaskMenuItem(ContextMenu menu)
        {
            if (!IsCurrentTask()) return;
            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            bool done = MarkdownTasks.IsFinished(Editor.Document.GetText(line));
            menu.Items.Add(Item(done ? "Reopen task" : "Complete task", "Ctrl+Enter", () => ToggleCurrentTask(),
                icon: done ? "\uE7A7" : "\uE930"));
            menu.Items.Add(Item("Edit task dates…", null, ShowTaskDateEditor, icon: "\uE787"));
        }

        internal void ShowTaskDateEditor()
        {
            if (_exiting || VaultCard.IsOpen || !IsCurrentTask()) return;
            var document = Editor.Document;
            var line = document.GetLineByOffset(Editor.CaretOffset);
            var controller = _taskDates[document];
            if (controller.DatesOfLine(line.LineNumber) is not { } dates) return;

            CloseOpenNotes();
            RenamePopup.IsOpen = false;
            ClosedPopup.IsOpen = false;
            TaskDatesEditor.Show(dates, () => Now(), (start, finish) =>
            {
                if (!ReferenceEquals(Editor.Document, document) || Editor.IsReadOnly
                    || !_taskDates.TryGetValue(document, out var current)) return "This task is no longer available.";
                var latest = current.DatesOfTask(dates.Id);
                if (latest == null || latest.Created != dates.Created || latest.Finished != dates.Finished)
                    return "The task changed. Close this popup and open its dates again.";
                return current.TrySetDates(dates.Id, start, finish) ? null : "This task can no longer be changed.";
            });
            UpdateTaskDateEditorBounds();
            TaskDatesPopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!TaskDatesPopup.IsOpen) return;
                TaskDatesEditor.StartInput.Focus();
                TaskDatesEditor.StartInput.SelectAll();
            }));
        }

        internal void CloseTaskDateEditor()
        {
            TaskDatesPopup.IsOpen = false;
            TaskDatesEditor.Hide();
        }

        private void UpdateTaskDateEditorBounds()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            TaskDatesEditor.Width = Math.Max(240, Math.Min(360, width - 24));
            TaskDatesEditor.MaxHeight = Math.Max(160, height - 48);
        }

        private void OnTaskDatesEditorDismissed(object? sender, EventArgs e)
        {
            CloseTaskDateEditor();
            if (!_exiting) Editor.Focus();
        }

        private void OnTaskDatesPopupClosed(object? sender, EventArgs e) => TaskDatesEditor.Hide();
    }
}
