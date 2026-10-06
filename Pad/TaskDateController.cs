using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>Tracks task dates in encrypted metadata; normal edits never insert dates into Markdown.</summary>
    internal sealed class TaskDateController : IDisposable
    {
        private sealed record TrackedTask(TextAnchor Anchor, TaskDateRecord Dates);

        private readonly TextDocument _document;
        private readonly NoteMeta _meta;
        private readonly Func<bool> _enabled;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action _changed;
        private readonly MarkdownDocumentCache _cache = new();
        private Dictionary<DocumentLine, TrackedTask> _tracked = new();
        private TaskDateRecord[] _before = Array.Empty<TaskDateRecord>();
        private bool _initialized;
        private bool _initializing;
        private bool _active;
        private bool _activatePending;
        private bool _scanAll;
        private bool _replaying;
        private bool _replacedAll;

        public TaskDateController(TextDocument document, NoteMeta meta, Func<bool> enabled,
                                  Func<DateTimeOffset> now, Action? changed = null)
        {
            _document = document;
            _meta = meta;
            _enabled = enabled;
            _now = now;
            _changed = changed ?? (() => { });
            document.UpdateStarted += OnUpdateStarted;
            document.Changed += OnDocumentChanged;
            document.UpdateFinished += OnUpdateFinished;
            _cache.StructureChanged += OnStructureChanged;
            _cache.Edited += OnEdited;
            _meta.TaskDatesRestored += OnTaskDatesRestored;
            Initialize();
        }

        internal int StructureScans => _cache.Recomputes;
        internal int RecordSorts { get; private set; }

        private void Activate()
        {
            if (_active) return;
            _active = true;
            _scanAll = true;
            _cache.FactsOf(_document, 1);
        }

        private void Deactivate()
        {
            _cache.Detach();
            _active = false;
            _initialized = false;
            _activatePending = false;
            _tracked.Clear();
        }

        private void OnUpdateStarted(object? sender, EventArgs e)
        {
            _replaying = false;
            _replacedAll = false;
            _before = _meta.TaskDates.ToArray();
        }

        private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
        {
            if (!_document.UndoStack.AcceptChanges) _replaying = true;
            if (e.Offset == 0 && e.RemovalLength > 0 && e.InsertionLength == _document.TextLength) _replacedAll = true;
            if (_initializing) return;
            if (!_enabled()) { Deactivate(); return; }
            if (_active) return;
            Activate();
            _activatePending = true;
        }

        private void OnUpdateFinished(object? sender, EventArgs e)
        {
            if (!_activatePending || _initializing) return;
            _activatePending = false;
            Initialize();
            if (!_replaying) RecordUndo();
        }

        private void OnStructureChanged() => _scanAll = true;

        public bool IsTaskLine(int lineNumber)
        {
            if (!_enabled()) return false;
            Activate();
            var facts = _cache.FactsOf(_document, lineNumber);
            return facts.Fence == MdFence.None && !facts.FrontMatter && facts.Table == MdTableRole.None
                   && MarkdownTasks.IsTask(_document.GetText(_document.GetLineByNumber(lineNumber)));
        }

        public TaskDateRecord? DatesOfLine(int lineNumber)
        {
            if (!_enabled() || !_initialized || lineNumber < 1 || lineNumber > _document.LineCount) return null;
            return _tracked.TryGetValue(_document.GetLineByNumber(lineNumber), out var task) ? task.Dates : null;
        }

        public TaskDateRecord? DatesOfTask(string taskId) => _enabled() && _initialized
            ? _tracked.Values.FirstOrDefault(task => task.Dates.Id == taskId)?.Dates : null;

        /// <summary>Explicit date correction; completion and its dates form one undoable action.</summary>
        public bool TrySetDates(string taskId, DateTimeOffset start, DateTimeOffset? finish)
        {
            if (!_enabled() || !_document.UndoStack.AcceptChanges || (finish is { } end && end < start)) return false;
            Initialize();
            var entry = _tracked.FirstOrDefault(pair => pair.Value.Dates.Id == taskId);
            if (entry.Value == null || entry.Value.Anchor.IsDeleted || !IsTaskLine(entry.Key.LineNumber)) return false;
            var previous = entry.Value.Dates;
            if (previous.Created == start && previous.Finished == finish) return true;

            var before = _meta.TaskDates.ToArray();
            var line = entry.Key;
            bool changeCheckbox = MarkdownTasks.IsFinished(_document.GetText(line)) != finish.HasValue;
            using (_document.RunUpdate())
            {
                if (changeCheckbox && MarkdownTasks.Toggle(_document.GetText(line)) is { } edit)
                    _document.Replace(line.Offset + edit.Offset, edit.Length, edit.Text);
                var corrected = previous with { Created = start, Finished = finish, Offset = line.Offset, Text = _document.GetText(line) };
                _tracked[line] = entry.Value with { Dates = corrected };
                _meta.TaskDates = _meta.TaskDates.Select(record => record.Id == taskId ? corrected : record).ToList();
            }
            if (!changeCheckbox)
            {
                _document.UndoStack.StartUndoGroup();
                try { _document.UndoStack.Push(new TaskDatesUndo(_meta, before, _meta.TaskDates.ToArray(), metadataOnly: true)); }
                finally { _document.UndoStack.EndUndoGroup(); }
            }
            _changed();
            return true;
        }

        private void OnTaskDatesRestored()
        {
            if (_enabled()) Reconcile(1, _document.LineCount, matchSaved: true);
            _changed();
        }

        public void Initialize()
        {
            if (!_enabled()) { Deactivate(); return; }
            if (_initialized || _initializing) return;
            Activate();
            _initializing = true;
            bool legacy = _meta.TaskDatesVersion == 0;
            var imported = new Dictionary<DocumentLine, LegacyTaskDates>();
            try
            {
                // Only pre-existing metadata trusts generated suffixes, once at initial load.
                // New notes/imports start at version 1; typed date-like text is ordinary text.
                if (legacy)
                {
                    for (int number = 1; number <= _document.LineCount; number++)
                    {
                        var line = _document.GetLineByNumber(number);
                        if (IsTaskLine(number) && MarkdownTasks.LegacyDatesOf(_document.GetText(line), _now().Offset) is { } dates)
                            imported.Add(line, dates);
                    }
                    _meta.TaskDatesVersion = 1;
                    if (imported.Count > 0)
                    {
                        using (_document.RunUpdate())
                            foreach (var (line, dates) in imported.OrderByDescending(pair => pair.Key.Offset))
                                _document.Remove(line.Offset + dates.Offset, dates.Length);
                        _document.UndoStack.ClearAll(); // Migration cannot reintroduce editable dates via Undo.
                    }
                }
                Reconcile(1, _document.LineCount, matchSaved: true, imported);
                _initialized = true;
            }
            finally { _initializing = false; }
            if (legacy) _changed();
        }

        private void OnEdited(TextDocument document, int first, int last)
        {
            if (_initializing) return;
            if (!_enabled()) { Deactivate(); return; }
            // All grouped undo operations restore metadata before UpdateFinished.
            Reconcile(first, last, matchSaved: _replaying || _replacedAll);
            if (!_replaying) RecordUndo();
            else _changed();
        }

        private void Reconcile(int first, int last, bool matchSaved, Dictionary<DocumentLine, LegacyTaskDates>? imported = null)
        {
            var previous = _meta.TaskDates.ToArray();
            var savedByOffset = matchSaved
                ? previous.GroupBy(record => record.Offset).ToDictionary(group => group.Key, group => group.First())
                : new Dictionary<int, TaskDateRecord>();
            if (_scanAll || matchSaved)
            {
                _scanAll = false;
                first = 1;
                last = _document.LineCount;
            }
            var used = new HashSet<string>();
            var next = new Dictionary<DocumentLine, TrackedTask>();
            var anchored = new Dictionary<DocumentLine, TrackedTask>();
            if (!matchSaved)
                foreach (var task in _tracked.Values)
                    if (!task.Anchor.IsDeleted)
                    {
                        var line = _document.GetLineByOffset(task.Anchor.Offset);
                        anchored.TryAdd(line, task);
                        // Reserve unchanged tasks before matching moved bodies. Inserting a copy
                        // must create a fresh record rather than steal the original task's dates.
                        if ((line.LineNumber < first || line.LineNumber > last
                            || (IsTaskLine(line.LineNumber)
                                && task.Dates.Text == _document.GetText(line)))
                            && next.TryAdd(line, task)) used.Add(task.Dates.Id);
                    }
            // Only displaced/edited records need body matching; untouched tasks have
            // already retained their anchors. Previous records are in document order.
            var unmatched = previous.Where(record => !used.Contains(record.Id)).ToArray();
            var savedByText = unmatched.GroupBy(record => record.Text)
                .ToDictionary(group => group.Key, group => new Queue<TaskDateRecord>(group));
            var savedByIdentity = unmatched
                .GroupBy(record => MarkdownTasks.Identity(record.Text))
                .ToDictionary(group => group.Key, group => new Queue<TaskDateRecord>(group));

            DateTimeOffset now = _now();
            var edited = new List<(DocumentLine Line, string Text)>();
            for (int number = first; number <= last; number++)
            {
                var line = _document.GetLineByNumber(number);
                if (!IsTaskLine(number)) { next.Remove(line); continue; }
                string text = _document.GetText(line);
                edited.Add((line, text));
                if (!next.ContainsKey(line))
                {
                    TaskDateRecord? saved = null;
                    if (savedByOffset.TryGetValue(line.Offset, out var exact) && !used.Contains(exact.Id)
                        && exact.Text == text) saved = exact;
                    else saved = TakeMatch(savedByText, text, used);
                    if (saved != null)
                    {
                        used.Add(saved.Id);
                        next.Add(line, Track(line, text, saved));
                    }
                }
            }

            // Exact text distinguishes duplicate bodies with different checkbox states.
            // Match those across the whole edit before treating a toggle as a body match.
            foreach (var (line, text) in edited)
                if (!next.ContainsKey(line) && TakeMatch(savedByIdentity, MarkdownTasks.Identity(text), used) is { } saved)
                {
                    used.Add(saved.Id);
                    next.Add(line, Track(line, text, saved));
                }

            // Match all moved bodies first; only then let genuine body edits keep
            // the record at their surviving anchor. Sort and move rewrite line text.
            foreach (var (line, text) in edited)
            {
                if (!next.TryGetValue(line, out var task))
                {
                    if (anchored.TryGetValue(line, out var original) && used.Add(original.Dates.Id)) task = original;
                    else
                    {
                        var migration = imported != null && imported.TryGetValue(line, out var dates) ? dates : (LegacyTaskDates?)null;
                        task = Track(line, text, new TaskDateRecord(Guid.NewGuid().ToString("N"), line.Offset, text,
                            migration?.Created ?? now, migration?.Finished));
                    }
                }
                next[line] = task with
                {
                    Dates = task.Dates with
                    {
                        Offset = line.Offset,
                        Text = text,
                        Finished = MarkdownTasks.IsFinished(text) ? task.Dates.Finished ?? now : null,
                    },
                };
            }

            // Ordinary edits scan touched lines only; anchors update every persisted location.
            // Surviving anchors stay in document order even when the task body was
            // edited. Reuse that order so typing in a middle task never sorts the list.
            var ordered = anchored.Keys.Where(next.ContainsKey)
                .Select(line => new KeyValuePair<DocumentLine, TrackedTask>(line, next[line]))
                .Concat(next.Where(pair => !anchored.ContainsKey(pair.Key))).ToArray();
            bool needsSort = false;
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].Key.Offset > ordered[i].Key.Offset) { needsSort = true; break; }
            if (needsSort)
            {
                Array.Sort(ordered, (left, right) => left.Key.Offset.CompareTo(right.Key.Offset));
                RecordSorts++;
            }
            _tracked = new Dictionary<DocumentLine, TrackedTask>(next.Count);
            var records = new List<TaskDateRecord>(next.Count);
            foreach (var (line, task) in ordered)
            {
                var record = task.Dates.Offset == line.Offset ? task.Dates : task.Dates with { Offset = line.Offset };
                _tracked.Add(line, ReferenceEquals(record, task.Dates) ? task : task with { Dates = record });
                records.Add(record);
            }
            _meta.TaskDates = records;
            if (!previous.SequenceEqual(records)) _changed();
        }

        private static TaskDateRecord? TakeMatch(Dictionary<string, Queue<TaskDateRecord>> records, string text, HashSet<string> used)
        {
            if (!records.TryGetValue(text, out var matches)) return null;
            while (matches.Count > 0 && used.Contains(matches.Peek().Id)) matches.Dequeue();
            return matches.Count > 0 ? matches.Dequeue() : null;
        }

        private TrackedTask Track(DocumentLine line, string text, TaskDateRecord dates)
        {
            var box = MarkdownTasks.Toggle(text)!.Value;
            var anchor = _document.CreateAnchor(line.Offset + box.Offset + box.Length);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            return new TrackedTask(anchor, dates);
        }

        private void RecordUndo()
        {
            var after = _meta.TaskDates.ToArray();
            if (!_document.UndoStack.AcceptChanges || !_document.UndoStack.CanUndo || _before.SequenceEqual(after)) return;
            _document.UndoStack.StartContinuedUndoGroup(_document.UndoStack.LastGroupDescriptor);
            try { _document.UndoStack.Push(new TaskDatesUndo(_meta, _before, after)); }
            finally { _document.UndoStack.EndUndoGroup(); }
        }

        // Holds metadata rather than a window, so moving a document preserves safe undo.
        private sealed class TaskDatesUndo(NoteMeta meta, TaskDateRecord[] before, TaskDateRecord[] after, bool metadataOnly = false) : IUndoableOperation
        {
            public void Undo() => Restore(before);
            public void Redo() => Restore(after);

            private void Restore(TaskDateRecord[] records)
            {
                meta.TaskDates = records.ToList();
                if (metadataOnly) meta.NotifyTaskDatesRestored();
            }
        }

        public void Dispose()
        {
            _document.UpdateStarted -= OnUpdateStarted;
            _document.Changed -= OnDocumentChanged;
            _document.UpdateFinished -= OnUpdateFinished;
            _cache.StructureChanged -= OnStructureChanged;
            _cache.Edited -= OnEdited;
            _meta.TaskDatesRestored -= OnTaskDatesRestored;
            Deactivate();
        }
    }
}
