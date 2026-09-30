using System;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Which lines of the shown document are fenced code, shared by the Markdown colorizer, the
    /// background renderer and the bullet generator. Rescans only after an edit that can move a
    /// fence (a backtick or tilde, a new or removed line, or an edit on a delimiter line), so
    /// ordinary typing never walks the whole note.
    /// </summary>
    internal sealed class MarkdownDocumentCache
    {
        private static readonly char[] FenceChars = { '`', '~' };

        private TextDocument? _document;
        private MdFence[] _kinds = Array.Empty<MdFence>();
        private bool _stale = true;

        /// <summary>Raised after an edit changed which lines are fenced, so lines far from the edit repaint.</summary>
        public event Action? FencesChanged;

        /// <summary>How many times the whole document was scanned; for tests.</summary>
        internal int Recomputes { get; private set; }

        /// <summary>The fence kind of a line (1-based) of <paramref name="document"/>.</summary>
        public MdFence KindOf(TextDocument document, int lineNumber)
        {
            Track(document);
            if (_stale) Recompute();
            int index = lineNumber - 1;
            return index >= 0 && index < _kinds.Length ? _kinds[index] : MdFence.None;
        }

        /// <summary>Stops following the document.</summary>
        public void Detach()
        {
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            _kinds = Array.Empty<MdFence>();
            _stale = true;
        }

        private void Track(TextDocument document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _document.Changed += OnChanged;
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            var document = _document!;
            if (!_stale && !TouchesFences(document, e)) return;
            var before = _kinds;
            Recompute();
            if (!before.AsSpan().SequenceEqual(_kinds)) FencesChanged?.Invoke();
        }

        private bool TouchesFences(TextDocument document, DocumentChangeEventArgs e)
        {
            if (document.LineCount != _kinds.Length) return true;
            if (e.InsertedText.Text.IndexOfAny(FenceChars) >= 0 || e.RemovedText.Text.IndexOfAny(FenceChars) >= 0) return true;
            int line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
            return _kinds[line - 1] == MdFence.Delimiter;
        }

        private void Recompute()
        {
            var document = _document!;
            var lines = new string[document.LineCount];
            foreach (var line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            _kinds = FenceTracker.Classify(lines);
            _stale = false;
            Recomputes++;
        }
    }
}
