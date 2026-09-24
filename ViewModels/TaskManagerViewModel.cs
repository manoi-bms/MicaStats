using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>Which column the process list is ordered by.</summary>
    public enum ProcessSortColumn { Name, Pid, Parent, Cpu, Memory, Disk, Uptime, Threads, Handles }

    /// <summary>What the filtered rows cost between them, for the footer.</summary>
    public readonly record struct ProcessTotals(int Count, float Cpu, long Memory, long Disk);

    /// <summary>
    /// One line in the process list.
    ///
    /// <para>
    /// Identity is (<see cref="Pid"/>, <see cref="CreateTime"/>) rather than the pid alone,
    /// because pids are recycled and the End task path has to be able to prove it is ending the
    /// process the user actually selected.
    /// </para>
    ///
    /// <para>
    /// Mirrors <c>SensorRow</c> in <see cref="StatsPanelViewModel"/> rather than using that
    /// class's private <c>Set</c> helper, which is a member of it and returns void.
    /// </para>
    /// </summary>
    public sealed class ProcessRow : INotifyPropertyChanged
    {
        private string _cpu = "—";
        private string _memory = "";
        private string _disk = "";
        private string _parent = "";
        private string _uptime = "";
        private string _threads = "";
        private string _handles = "";

        public ProcessRow(string name, int pid, long createTime)
        {
            Name = name;
            Pid = pid;
            CreateTime = createTime;
        }

        public string Name { get; }
        public int Pid { get; }
        public long CreateTime { get; }

        public string Cpu
        {
            get => _cpu;
            set { if (_cpu != value) { _cpu = value; Raise(nameof(Cpu)); } }
        }

        public string Memory
        {
            get => _memory;
            set { if (_memory != value) { _memory = value; Raise(nameof(Memory)); } }
        }

        public string Disk
        {
            get => _disk;
            set { if (_disk != value) { _disk = value; Raise(nameof(Disk)); } }
        }

        /// <summary>Parent name and PID, <c>(gone)</c> for an orphan. Mutable: a parent can exit.</summary>
        public string Parent
        {
            get => _parent;
            set { if (_parent != value) { _parent = value; Raise(nameof(Parent)); } }
        }

        /// <summary>How long the process has run, rendered from the creation time already in the snapshot.</summary>
        public string Uptime
        {
            get => _uptime;
            set { if (_uptime != value) { _uptime = value; Raise(nameof(Uptime)); } }
        }

        /// <summary>Thread count, read from the same kernel buffer as everything else rather than a per-row query.</summary>
        public string Threads
        {
            get => _threads;
            set { if (_threads != value) { _threads = value; Raise(nameof(Threads)); } }
        }

        /// <summary>Open handle count, read from the same kernel buffer as everything else rather than a per-row query.</summary>
        public string Handles
        {
            get => _handles;
            set { if (_handles != value) { _handles = value; Raise(nameof(Handles)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Shapes the sampler's snapshot into a sortable, searchable list.
    ///
    /// <para>
    /// The sort and filter are static and pure so they can be tested without a sampler, a
    /// window or a dispatcher. They decide what the user sees, which makes them the parts most
    /// worth proving and the parts most likely to be quietly wrong.
    /// </para>
    /// </summary>
    public sealed class TaskManagerViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>
        /// Matches a name substring, or a pid exactly when the term is a number. Exact rather
        /// than substring on pids: searching 42 should not bury the answer under 420 and 1042.
        /// </summary>
        public static IReadOnlyList<ProcessUsage> Filter(IReadOnlyList<ProcessUsage> all, string term)
        {
            if (all == null) return Array.Empty<ProcessUsage>();
            if (string.IsNullOrWhiteSpace(term)) return all;

            string t = term.Trim();
            if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                return all.Where(p => p.Pid == pid).ToList();

            // The parent name is searched too, so typing "bash" shows everything a bash started
            // — which is how a family of leaked children is found and then ended together.
            return all.Where(p =>
                    p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.ParentName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        /// <summary>
        /// Orders in place. Ties break by pid so the ordering is total: rows with equal values
        /// must not swap places between ticks, because a list that reshuffles under the cursor
        /// is unusable at exactly the moment this window gets opened.
        /// </summary>
        public static void Sort(List<ProcessUsage> rows, ProcessSortColumn column, bool descending)
        {
            Comparison<ProcessUsage> compare = column switch
            {
                ProcessSortColumn.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                ProcessSortColumn.Pid => (a, b) => a.Pid.CompareTo(b.Pid),
                ProcessSortColumn.Parent => (a, b) => string.Compare(a.ParentName, b.ParentName, StringComparison.OrdinalIgnoreCase),
                ProcessSortColumn.Cpu => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
                ProcessSortColumn.Memory => (a, b) => a.WorkingSet.CompareTo(b.WorkingSet),
                ProcessSortColumn.Disk => (a, b) => a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec),
                // Longer uptime is an OLDER creation time, so the comparison is reversed.
                ProcessSortColumn.Uptime => (a, b) => b.CreateTime.CompareTo(a.CreateTime),
                ProcessSortColumn.Threads => (a, b) => a.Threads.CompareTo(b.Threads),
                ProcessSortColumn.Handles => (a, b) => a.Handles.CompareTo(b.Handles),
                _ => (a, b) => 0,
            };

            rows.Sort((a, b) =>
            {
                int c = compare(a, b);
                if (descending) c = -c;
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });
        }

        /// <summary>
        /// The sort column a header text stands for, or null. Kept beside <see cref="Sort"/> so
        /// renaming a header in the XAML cannot silently disconnect it from its column without
        /// failing a test.
        /// </summary>
        public static ProcessSortColumn? ColumnFor(string? header) => header switch
        {
            "Name" => ProcessSortColumn.Name,
            "PID" => ProcessSortColumn.Pid,
            "Parent" => ProcessSortColumn.Parent,
            "CPU" => ProcessSortColumn.Cpu,
            "Memory" => ProcessSortColumn.Memory,
            "Disk" => ProcessSortColumn.Disk,
            "Uptime" => ProcessSortColumn.Uptime,
            "Threads" => ProcessSortColumn.Threads,
            "Handles" => ProcessSortColumn.Handles,
            _ => null,
        };

        /// <summary>What a set of rows costs between them.</summary>
        public static ProcessTotals Totals(IReadOnlyList<ProcessUsage> rows)
        {
            if (rows == null || rows.Count == 0) return new ProcessTotals(0, 0f, 0L, 0L);

            float cpu = 0f;
            long memory = 0, disk = 0;
            foreach (var r in rows)
            {
                cpu += r.CpuPercent;
                memory += r.WorkingSet;
                disk += r.DiskBytesPerSec;
            }
            return new ProcessTotals(rows.Count, cpu, memory, disk);
        }

        /// <summary>
        /// The footer's summary, e.g. "37 processes · 4.2% CPU · 1.8 GB · 12 MB/s". Tells you what
        /// a filtered group is costing before you decide to end it.
        /// </summary>
        public static string FormatTotals(ProcessTotals t)
        {
            string count = t.Count.ToString(CultureInfo.InvariantCulture)
                           + (t.Count == 1 ? " process" : " processes");
            string cpu = t.Cpu.ToString("F1", CultureInfo.InvariantCulture) + "% CPU";
            string disk = t.Disk > 0 ? ProcessUsage.FormatRate(t.Disk) : "0 B/s";
            return count + " · " + cpu + " · " + ProcessUsage.FormatBytes(t.Memory) + " · " + disk;
        }

        /// <summary>
        /// How long a process has run, at the one scale that matters: seconds, then minutes,
        /// then hours and minutes, then days and hours. A clock that moved backwards reads as 0s
        /// rather than a negative age.
        /// </summary>
        public static string FormatUptime(TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
            var inv = CultureInfo.InvariantCulture;

            if (uptime.TotalMinutes < 1) return ((int)uptime.TotalSeconds).ToString(inv) + "s";
            if (uptime.TotalHours < 1) return ((int)uptime.TotalMinutes).ToString(inv) + "m";
            if (uptime.TotalDays < 1)
                return ((int)uptime.TotalHours).ToString(inv) + "h " + uptime.Minutes.ToString("00", inv) + "m";
            return ((int)uptime.TotalDays).ToString(inv) + "d " + uptime.Hours.ToString(inv) + "h";
        }

        /// <summary>The Parent column: "bash.exe (1234)", <c>(gone)</c>, or empty.</summary>
        public static string ParentText(ProcessUsage row)
        {
            if (string.IsNullOrEmpty(row.ParentName)) return "";
            if (row.ParentName == ProcessTree.Gone) return ProcessTree.Gone;
            return row.ParentName + " (" + row.ParentPid.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// Makes <paramref name="rows"/> hold exactly <paramref name="ordered"/>, in that order,
        /// reusing every existing row whose process is still there.
        ///
        /// <para>
        /// A WPF selection holds row objects, and clearing the collection drops it. Moving an
        /// existing object and updating its properties in place does not — so a selection of one
        /// row or many, and the scroll position, survive every refresh. Identity is
        /// (pid, creation time): a PID reused by a different process gets a new row, never the
        /// old one, so a selection can never slide onto an unrelated process.
        /// </para>
        /// </summary>
        public static void SyncRows(ObservableCollection<ProcessRow> rows, IReadOnlyList<ProcessUsage> ordered)
        {
            var wanted = new HashSet<(int, long)>();
            foreach (var p in ordered) wanted.Add((p.Pid, p.CreateTime));

            // Drop what is gone, back to front so the indices ahead stay valid.
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains((rows[i].Pid, rows[i].CreateTime))) rows.RemoveAt(i);
            }

            var existing = new Dictionary<(int, long), ProcessRow>(rows.Count);
            foreach (var r in rows) existing[(r.Pid, r.CreateTime)] = r;

            // Everything before i is final, so a reused row is always found at or after i.
            for (int i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                if (i < rows.Count && rows[i].Pid == p.Pid && rows[i].CreateTime == p.CreateTime) continue;

                if (existing.TryGetValue((p.Pid, p.CreateTime), out var row))
                    rows.Move(rows.IndexOf(row), i);
                else
                    rows.Insert(i, new ProcessRow(p.Name, p.Pid, p.CreateTime));
            }
        }

        /// <summary>
        /// The processes behind a set of selected rows, in list order, matched by identity.
        /// A selected row whose process is no longer in <paramref name="source"/> is left out.
        /// </summary>
        public static IReadOnlyList<ProcessUsage> Matching(IReadOnlyList<ProcessUsage> source, IEnumerable<ProcessRow> selected)
        {
            var ids = new HashSet<(int, long)>();
            foreach (var r in selected) ids.Add((r.Pid, r.CreateTime));

            var result = new List<ProcessUsage>();
            foreach (var p in source)
            {
                if (ids.Contains((p.Pid, p.CreateTime))) result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// CPU share for display. Before a second sample there is no delta, so this reads as a
        /// dash: 0.0% would be a lie indistinguishable from the frozen list being replaced.
        /// </summary>
        public static string CpuTextFor(ProcessUsage row, bool hasCpuData) =>
            hasCpuData ? row.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";

        private readonly ProcessSampler _sampler;
        private readonly Action _onUpdated;
        private bool _disposed;

        private string _searchText = "";
        private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;
        private bool _sortDescending = true;
        private bool _ending;
        private int _count;
        private string _emptyMessage = "";
        private string _message = "";
        private string _totals = "0 processes";
        private IReadOnlyList<ProcessUsage> _filtered = Array.Empty<ProcessUsage>();
        private IReadOnlyList<ProcessUsage> _snapshot = Array.Empty<ProcessUsage>();

        public TaskManagerViewModel(ProcessSampler sampler)
        {
            _sampler = sampler;

            // Updated arrives on the sampler's background thread; Rows is bound to the UI.
            _onUpdated = () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
            _sampler.Updated += _onUpdated;
            _sampler.Retain();

            // Render whatever is already in memory rather than waiting up to two seconds for
            // the next tick. When the panel or the slowdown recorder already holds a lease this
            // paints a full list on the first frame, which is the whole point on a busy machine.
            Refresh();
        }

        public ObservableCollection<ProcessRow> Rows { get; } = new();

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEndAllFiltered));
                Refresh();
            }
        }

        /// <summary>
        /// The rows the filter currently matches, as data rather than as display rows. What
        /// End all filtered plans against.
        /// </summary>
        public IReadOnlyList<ProcessUsage> Filtered => _filtered;

        /// <summary>The full snapshot the list was built from, for walking parent chains.</summary>
        public IReadOnlyList<ProcessUsage> Snapshot => _snapshot;

        /// <summary>
        /// End all filtered is offered only while a filter is typed and matches something, and
        /// while no batch from a previous click is still running. With no filter, "all filtered"
        /// is every process on the machine, and the button is simply not available rather than
        /// asking; while a batch runs, a second click would plan against rows that are already
        /// mid-termination.
        /// </summary>
        public bool CanEndAllFiltered => !_ending && !string.IsNullOrWhiteSpace(_searchText) && _count > 0;

        /// <summary>
        /// Set for the duration of a bulk end so <see cref="CanEndAllFiltered"/> — and the button
        /// bound to it — goes false without the window ever assigning the button's IsEnabled
        /// directly, which would replace the binding with a local value for the rest of the
        /// window's life.
        /// </summary>
        public bool Ending
        {
            get => _ending;
            set
            {
                if (_ending == value) return;
                _ending = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEndAllFiltered));
            }
        }

        /// <summary>Row count after filtering.</summary>
        public int Count
        {
            get => _count;
            private set
            {
                if (_count == value) return;
                _count = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Footer));
                OnPropertyChanged(nameof(CanEndAllFiltered));
            }
        }

        /// <summary>
        /// Why the list is empty, when it is. An empty list must never be how this window
        /// reports a problem — that is one of the four symptoms it exists to replace.
        /// </summary>
        public string EmptyMessage
        {
            get => _emptyMessage;
            private set { if (_emptyMessage != value) { _emptyMessage = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The outcome of the last End task, or empty. Held here rather than written straight
        /// onto a label so it survives the two-second refresh: a result the user cannot finish
        /// reading is barely better than the silence this window exists to replace.
        /// </summary>
        public string Message
        {
            get => _message;
            set { if (_message != value) { _message = value; OnPropertyChanged(); OnPropertyChanged(nameof(Footer)); } }
        }

        /// <summary>One line: the last result if there is one, then what the filtered rows cost.</summary>
        public string Footer => string.IsNullOrEmpty(_message) ? _totals : _message + "   ·   " + _totals;

        /// <summary>Sets the sort column, flipping direction when the same column is chosen twice.</summary>
        public void SortBy(ProcessSortColumn column)
        {
            if (_sortColumn == column) _sortDescending = !_sortDescending;
            else { _sortColumn = column; _sortDescending = column != ProcessSortColumn.Name; }
            Refresh();
        }

        public void Refresh()
        {
            if (_disposed) return;

            var snapshot = _sampler.AllProcesses;
            var rows = Filter(snapshot, _searchText).ToList();
            Sort(rows, _sortColumn, _sortDescending);
            _snapshot = snapshot;
            _filtered = rows;

            // Synchronised in place rather than rebuilt: rows are reused and moved, never
            // replaced, so a selection of any size and the scroll position survive every tick.
            SyncRows(Rows, rows);

            bool hasCpu = _sampler.HasCpuData;
            var now = DateTime.Now;
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < rows.Count; i++)
            {
                var p = rows[i];
                Rows[i].Cpu = CpuTextFor(p, hasCpu);
                Rows[i].Memory = p.WorkingSetText;
                Rows[i].Disk = p.DiskBytesPerSec > 0 ? p.DiskText : "—";
                Rows[i].Parent = ParentText(p);
                Rows[i].Uptime = p.CreateTime > 0 ? FormatUptime(now - DateTime.FromFileTime(p.CreateTime)) : "";
                Rows[i].Threads = p.Threads.ToString(inv);
                Rows[i].Handles = p.Handles.ToString(inv);
            }

            _totals = FormatTotals(Totals(rows));
            OnPropertyChanged(nameof(Footer));
            Count = rows.Count;
            EmptyMessage = rows.Count > 0
                ? ""
                : snapshot.Count == 0
                    ? "Waiting for the first sample…"
                    : "No process matches “" + _searchText.Trim() + "”.";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sampler.Updated -= _onUpdated;
            _sampler.Release();
        }
    }
}
