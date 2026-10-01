using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The structure of the shown Markdown document (<see cref="MarkdownStructure"/>: fences, $$
    /// blocks, tables, setext headings, front matter, callouts, abbreviations), shared by the
    /// colorizer, the background renderer, the generators and the diagram pictures. Rescans only
    /// after an edit that can change it (R3), so ordinary typing never walks the whole note.
    /// </summary>
    internal sealed class MarkdownDocumentCache
    {
        /// <summary>Characters whose typing or removal can change the structure.</summary>
        private static readonly char[] Triggers = { '`', '~', '|', '$', '=', '-', '>', '{', '[', '*' };

        /// <summary>Characters that start a structural line (a table pipe does not: typing in a row changes nothing).</summary>
        private static readonly char[] LineStarts = { '`', '~', '$', '=', '-', '>', '{', '*' };

        private readonly Action<Exception> _onFailure;
        private TextDocument? _document;
        private MarkdownStructure _structure = MarkdownStructure.Empty;
        private bool _stale = true;

        /// <param name="onFailure">Told when following an edit fails; the edit itself never sees the exception.</param>
        public MarkdownDocumentCache(Action<Exception>? onFailure = null) => _onFailure = onFailure ?? (_ => { });

        /// <summary>Raised after an edit changed the structure, so lines far from the edit repaint.</summary>
        public event Action? StructureChanged;

        /// <summary>How many times the whole document was scanned; for tests.</summary>
        internal int Recomputes { get; private set; }

        /// <summary>The fence kind of a line (1-based) of <paramref name="document"/>.</summary>
        public MdFence KindOf(TextDocument document, int lineNumber) => Get(document, s => s.Fences, lineNumber, MdFence.None);

        /// <summary>What line <paramref name="lineNumber"/> is in its document.</summary>
        public MdLineFacts FactsOf(TextDocument document, int lineNumber) =>
            Get(document, s => s.Facts, lineNumber, new MdLineFacts(MdFence.None));

        /// <summary>The 1-based line that opened the fence line <paramref name="lineNumber"/> closes, or 0 when it closes none.</summary>
        public int OpeningLineOf(TextDocument document, int lineNumber) => Get(document, s => s.Openings, lineNumber, 0);

        /// <summary>The 1-based line that closes the fence line <paramref name="lineNumber"/> opens, or 0 (it opens none, or never closes).</summary>
        public int ClosingLineOf(TextDocument document, int lineNumber) => Get(document, s => s.Closings, lineNumber, 0);

        /// <summary>For a line inside a fenced or $$ block, the 1-based line that opened it; else 0.</summary>
        public int BlockOpeningOf(TextDocument document, int lineNumber) => Get(document, s => s.BlockOpenings, lineNumber, 0);

        /// <summary>The abbreviation terms the document defines.</summary>
        public IReadOnlySet<string> AbbreviationsOf(TextDocument document)
        {
            Track(document);
            if (_stale) Recompute();
            return _structure.Abbreviations;
        }

        /// <summary>Stops following the document.</summary>
        public void Detach()
        {
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            _structure = MarkdownStructure.Empty;
            _stale = true;
        }

        private T Get<T>(TextDocument document, Func<MarkdownStructure, T[]> part, int lineNumber, T none)
        {
            Track(document);
            if (_stale) Recompute();
            var values = part(_structure);
            int index = lineNumber - 1;
            return index >= 0 && index < values.Length ? values[index] : none;
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
            // AvalonEdit calls every handler the document had when the change began, so a handler
            // that ran earlier in this change may already have detached the cache.
            var document = _document;
            if (document == null || !ReferenceEquals(sender, document)) return;
            try
            {
                if (!_stale && !TouchesStructure(document, e)) return;
                var before = _structure;
                Recompute();
                if (!before.Facts.AsSpan().SequenceEqual(_structure.Facts) || !before.Abbreviations.SetEquals(_structure.Abbreviations))
                    StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _stale = true;
                _onFailure(ex);
            }
        }

        /// <summary>Whether an edit can change the structure (R3).</summary>
        private bool TouchesStructure(TextDocument document, DocumentChangeEventArgs e)
        {
            if (document.LineCount != _structure.LineCount) return true;
            if (e.InsertedText.Text.IndexOfAny(Triggers) >= 0 || e.RemovedText.Text.IndexOfAny(Triggers) >= 0) return true;

            int first = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
            int last = document.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, document.TextLength)).LineNumber;
            for (int number = first; number <= last; number++)
            {
                var facts = _structure.Facts[number - 1];
                if (facts.Fence == MdFence.Delimiter || facts.Table == MdTableRole.Header || facts.SetextLevel != 0
                    || facts.SetextUnderline || facts.FrontMatter || facts.CalloutClass) return true;
                if (StartsWithAny(document, number, LineStarts)) return true;
                // Text typed on a line may make the dashes below it a setext underline.
                if (number < document.LineCount && StartsWithAny(document, number + 1, new[] { '=', '-' })) return true;
            }
            return false;
        }

        /// <summary>The line's first character after at most three spaces is one of <paramref name="chars"/>.</summary>
        private static bool StartsWithAny(TextDocument document, int number, char[] chars)
        {
            var line = document.GetLineByNumber(number);
            string start = document.GetText(line.Offset, Math.Min(line.Length, 4));
            int i = 0;
            while (i < start.Length && i < 3 && start[i] == ' ') i++;
            return i < start.Length && chars.Contains(start[i]);
        }

        private void Recompute()
        {
            var document = _document!;
            var lines = new string[document.LineCount];
            foreach (var line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            _structure = MarkdownStructure.Scan(lines);
            _stale = false;
            Recomputes++;
        }
    }
}
