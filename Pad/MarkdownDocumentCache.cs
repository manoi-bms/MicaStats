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
    /// after an edit that can change it (R3): typing in an ordinary paragraph, a list item, a quote,
    /// a fenced block or a table cell never walks the whole note.
    /// </summary>
    internal sealed class MarkdownDocumentCache
    {
        /// <summary>Characters whose typing or removal anywhere can start or end a structure: fences, $$ blocks and table pipes.</summary>
        private static readonly char[] Triggers = { '`', '~', '|', '$' };

        private static readonly char[] LineBreaks = { '\r', '\n' };

        private static readonly char[] SetextStarts = { '=', '-' };

        private static readonly char[] CalloutStarts = { '{' };

        private readonly Action<Exception> _onFailure;
        private TextDocument? _document;
        private MarkdownStructure _structure = MarkdownStructure.Empty;
        private bool _stale = true;
        private bool _pending;

        /// <summary>The span the open update group's edits touched, in the current text's offsets; -1 when none.</summary>
        private int _editFrom = -1;
        private int _editTo;

        /// <param name="onFailure">Told when following an edit fails; the edit itself never sees the exception.</param>
        public MarkdownDocumentCache(Action<Exception>? onFailure = null) => _onFailure = onFailure ?? (_ => { });

        /// <summary>Raised after an edit changed the structure, so lines far from the edit repaint.</summary>
        public event Action? StructureChanged;

        /// <summary>
        /// Raised when an edit (or one update group of edits) is done and the structure follows it,
        /// with the document and the first and last line the edit touched: lines below an edit whose
        /// look follows from it without any fact changing (fenced code colors) repaint from there.
        /// </summary>
        public event Action<TextDocument, int, int>? Edited;

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
            if (_document != null)
            {
                _document.Changed -= OnChanged;
                _document.UpdateFinished -= OnUpdateFinished;
            }
            _pending = false;
            _editFrom = -1;
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
            _document.UpdateFinished += OnUpdateFinished;
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            // AvalonEdit calls every handler the document had when the change began, so a handler
            // that ran earlier in this change may already have detached the cache.
            var document = _document;
            if (document == null || !ReferenceEquals(sender, document)) return;
            try
            {
                TrackEdit(e);
                // Replace All: thousands of edits in one update group rescan once, when it ends.
                if (!_pending && (_stale || TouchesStructure(document, e))) _pending = true;
            }
            catch (Exception ex)
            {
                _stale = true;
                _onFailure(ex);
            }
            if (!document.IsInUpdate) Finish(document);
        }

        private void OnUpdateFinished(object? sender, EventArgs e)
        {
            var document = _document;
            if (document == null || !ReferenceEquals(sender, document)) return;
            Finish(document);
        }

        /// <summary>The edit is done: rescans if it can have changed the structure, then says which lines it touched.</summary>
        private void Finish(TextDocument document)
        {
            try
            {
                if (_pending)
                {
                    _pending = false;
                    Rescan();
                }
            }
            catch (Exception ex)
            {
                _stale = true;
                _onFailure(ex);
            }

            if (_editFrom < 0) return;
            int from = Math.Min(_editFrom, document.TextLength);
            int to = Math.Min(_editTo, document.TextLength);
            _editFrom = -1;
            try
            {
                Edited?.Invoke(document, document.GetLineByOffset(from).LineNumber, document.GetLineByOffset(to).LineNumber);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        /// <summary>Widens the span this update group touched by one change, keeping it in the current text's offsets.</summary>
        private void TrackEdit(DocumentChangeEventArgs e)
        {
            int end = e.Offset + e.InsertionLength;
            if (_editFrom < 0)
            {
                _editFrom = e.Offset;
                _editTo = end;
                return;
            }
            _editFrom = Math.Min(e.GetNewOffset(_editFrom, AnchorMovementType.BeforeInsertion), e.Offset);
            _editTo = Math.Max(e.GetNewOffset(_editTo, AnchorMovementType.AfterInsertion), end);
        }

        private void Rescan()
        {
            var before = _structure;
            Recompute();
            if (!before.Facts.AsSpan().SequenceEqual(_structure.Facts) || !before.Abbreviations.SetEquals(_structure.Abbreviations))
                StructureChanged?.Invoke();
        }

        /// <summary>
        /// Whether an edit can change the structure (R3): it changed the line count; it typed or
        /// removed a fence, $$ or pipe character; the edited line was structural, or its text before
        /// or after the edit can be (<see cref="MarkdownStructure.CanBeStructural"/>); or the line
        /// below makes it matter (an underline below text, a callout below a quote, a delimiter row
        /// below a line with a pipe).
        /// </summary>
        private bool TouchesStructure(TextDocument document, DocumentChangeEventArgs e)
        {
            if (document.LineCount != _structure.LineCount) return true;
            string inserted = e.InsertedText.Text;
            string removed = e.RemovedText.Text;
            if (inserted.IndexOfAny(Triggers) >= 0 || removed.IndexOfAny(Triggers) >= 0) return true;
            // A replacement across lines that kept the line count is rare: rescan rather than reason about each line.
            if (inserted.IndexOfAny(LineBreaks) >= 0 || removed.IndexOfAny(LineBreaks) >= 0) return true;

            var line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength));
            int number = line.LineNumber;
            var facts = _structure.Facts[number - 1];
            if (WasStructural(facts)) return true;

            string now = document.GetText(line);
            int at = e.Offset - line.Offset;
            string before = now.Substring(0, at) + removed + now.Substring(at + inserted.Length);
            if (MarkdownStructure.CanBeStructural(now, number) || MarkdownStructure.CanBeStructural(before, number)) return true;

            bool last = number == document.LineCount;
            // Text typed above a line of dashes or equals signs may make it a setext underline.
            if (!last && StartsWithAny(document, number + 1, SetextStarts) && MarkdownStructure.IsUnderline(TextOf(document, number + 1))) return true;
            // A quote line joins (or leaves) the callout below it.
            if (!last && (MarkdownStructure.IsQuoteLine(now) || MarkdownStructure.IsQuoteLine(before)))
            {
                var below = _structure.Facts[number];
                if (below.Callout != MdCallout.None || below.CalloutClass) return true;
                if (StartsWithAny(document, number + 1, CalloutStarts) && MarkdownStructure.IsCalloutClass(TextOf(document, number + 1))) return true;
            }
            // A line with a pipe (the edit changed none) may head a table over a delimiter row, or sit next to one.
            if (facts.Table == MdTableRole.None && now.IndexOf('|') >= 0)
            {
                if (!last && IsDelimiterAt(document, number + 1)) return true;
                if ((number > 1 && _structure.Facts[number - 2].Table != MdTableRole.None) || (!last && _structure.Facts[number].Table != MdTableRole.None)) return true;
            }
            return false;
        }

        /// <summary>
        /// The line had facts an edit to it can take away. Inside lines of a fence and table rows are
        /// left out: typing there changes nothing unless it types or removes a trigger character.
        /// </summary>
        private static bool WasStructural(MdLineFacts facts) =>
            facts.Fence == MdFence.Delimiter || facts.Table is MdTableRole.Header or MdTableRole.Delimiter || facts.SetextLevel != 0
            || facts.SetextUnderline || facts.FrontMatter || facts.CalloutClass || facts.Callout != MdCallout.None;

        private static string TextOf(TextDocument document, int number) => document.GetText(document.GetLineByNumber(number));

        private static bool IsDelimiterAt(TextDocument document, int number) =>
            MarkdownStructure.IsDelimiterRow(TextOf(document, number), out _);

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
