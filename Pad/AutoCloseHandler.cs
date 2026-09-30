using System;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services.Pad;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Connects <see cref="AutoClosePolicy"/> to an editor: it looks at each typed character before
    /// AvalonEdit inserts it, and at Backspace. Each change it makes is one undoable edit. A
    /// rectangular selection is left to AvalonEdit.
    /// </summary>
    internal sealed class AutoCloseHandler
    {
        private readonly TextEditor _editor;
        private readonly Func<bool> _enabled;

        public AutoCloseHandler(TextEditor editor, Func<bool> enabled)
        {
            _editor = editor;
            _enabled = enabled;
            editor.TextArea.TextEntering += OnTextEntering;
            editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        }

        private bool Active => _enabled() && !_editor.IsReadOnly && _editor.TextArea.Selection is not RectangleSelection;

        private void OnTextEntering(object? sender, TextCompositionEventArgs e)
        {
            if (!Active || e.Text.Length != 1) return;

            var document = _editor.Document;
            int caret = _editor.CaretOffset;
            bool hasSelection = _editor.SelectionLength > 0;
            char typed = e.Text[0];
            char? before = !hasSelection && caret > 0 ? document.GetCharAt(caret - 1) : null;
            char? after = !hasSelection && caret < document.TextLength ? document.GetCharAt(caret) : null;

            switch (AutoClosePolicy.OnType(typed, before, after, hasSelection))
            {
                case AutoCloseAction.Pair:
                    document.Insert(caret, typed.ToString() + AutoClosePolicy.CloserOf(typed));
                    _editor.CaretOffset = caret + 1;
                    e.Handled = true;
                    break;
                case AutoCloseAction.SkipOver:
                    _editor.CaretOffset = caret + 1;
                    e.Handled = true;
                    break;
                case AutoCloseAction.Wrap:
                    int start = _editor.SelectionStart;
                    int length = _editor.SelectionLength;
                    document.Replace(start, length, typed + _editor.SelectedText + AutoClosePolicy.CloserOf(typed));
                    _editor.Select(start + 1, length);
                    e.Handled = true;
                    break;
            }
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift && TryDeletePair()) e.Handled = true;
        }

        /// <summary>Backspace between an empty pair: removes both halves as one edit. Returns false when it does not apply.</summary>
        internal bool TryDeletePair()
        {
            if (!Active || _editor.SelectionLength > 0) return false;
            var document = _editor.Document;
            int caret = _editor.CaretOffset;
            if (caret == 0 || caret >= document.TextLength) return false;
            if (!AutoClosePolicy.DeletesPair(document.GetCharAt(caret - 1), document.GetCharAt(caret))) return false;
            document.Remove(caret - 1, 2);
            return true;
        }
    }
}
