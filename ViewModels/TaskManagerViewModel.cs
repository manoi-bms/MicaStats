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
    public enum ProcessSortColumn { Name, Pid, Cpu, Memory, Disk }

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

            return all.Where(p => p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
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
                ProcessSortColumn.Cpu => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
                ProcessSortColumn.Memory => (a, b) => a.WorkingSet.CompareTo(b.WorkingSet),
                ProcessSortColumn.Disk => (a, b) => a.DiskBytesPerSec.CompareTo(b.DiskBytesPerSec),
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
        private int _count;
        private string _emptyMessage = "";
        private string _message = "";

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
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); Refresh(); } }
        }

        /// <summary>Row count after filtering, for the header.</summary>
        public int Count
        {
            get => _count;
            private set { if (_count != value) { _count = value; OnPropertyChanged(); OnPropertyChanged(nameof(Footer)); } }
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

        /// <summary>One line: the last result if there is one, then the count.</summary>
        public string Footer
        {
            get
            {
                string count = _count.ToString(CultureInfo.InvariantCulture) + " processes";
                return string.IsNullOrEmpty(_message) ? count : _message + "   ·   " + count;
            }
        }

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

            // Rebuild only when the set of processes changes; otherwise update in place, so a
            // selection and a scroll position survive a tick. Rebuilding every two seconds
            // would move the row out from under the cursor at the moment it is being read.
            bool sameSet = Rows.Count == rows.Count;
            if (sameSet)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (Rows[i].Pid == rows[i].Pid && Rows[i].CreateTime == rows[i].CreateTime) continue;
                    sameSet = false;
                    break;
                }
            }

            if (!sameSet)
            {
                Rows.Clear();
                foreach (var p in rows) Rows.Add(new ProcessRow(p.Name, p.Pid, p.CreateTime));
            }

            bool hasCpu = _sampler.HasCpuData;
            for (int i = 0; i < rows.Count; i++)
            {
                Rows[i].Cpu = CpuTextFor(rows[i], hasCpu);
                Rows[i].Memory = rows[i].WorkingSetText;
                Rows[i].Disk = rows[i].DiskBytesPerSec > 0 ? rows[i].DiskText : "—";
            }

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
