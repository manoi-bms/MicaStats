using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>One process's share of recent CPU time, its working set and its disk traffic.</summary>
    public sealed record ProcessUsage(string Name, int Pid, float CpuPercent, long WorkingSet)
    {
        /// <summary>Bytes read from disk per second over the last interval.</summary>
        public long DiskReadBytesPerSec { get; init; }

        /// <summary>Bytes written to disk per second over the last interval.</summary>
        public long DiskWriteBytesPerSec { get; init; }

        /// <summary>Combined disk traffic per second — how a process is ranked as a disk hog.</summary>
        public long DiskBytesPerSec => DiskReadBytesPerSec + DiskWriteBytesPerSec;

        /// <summary>Working set rendered for display, e.g. "412 MB".</summary>
        public string WorkingSetText => WorkingSet >= 1024L * 1024 * 1024
            ? $"{WorkingSet / 1024d / 1024d / 1024d:F1} GB"
            : $"{WorkingSet / 1024d / 1024d:F0} MB";

        /// <summary>CPU share rendered for display.</summary>
        public string CpuText => $"{CpuPercent:F1}%";

        /// <summary>Disk traffic rendered for display, e.g. "549 MB/s".</summary>
        public string DiskText => FormatRate(DiskBytesPerSec);

        /// <summary>Byte rate in the largest unit that keeps the number readable.</summary>
        public static string FormatRate(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "0";
            double v = bytesPerSecond;
            if (v >= 1024d * 1024 * 1024) return $"{v / 1024d / 1024d / 1024d:F1} GB/s";
            if (v >= 1024d * 1024) return $"{v / 1024d / 1024d:F0} MB/s";
            if (v >= 1024d) return $"{v / 1024d:F0} KB/s";
            return $"{bytesPerSecond} B/s";
        }
    }

    /// <summary>
    /// Ranks processes by CPU and by memory, sampling only while <see cref="Enabled"/> is set.
    ///
    /// <para>
    /// Uses <c>NtQuerySystemInformation</c> rather than <see cref="System.Diagnostics.Process"/>.
    /// This is a correctness requirement, not an optimisation: the application runs unelevated, and
    /// reading <c>Process.TotalProcessorTime</c> throws access-denied for roughly a third of
    /// processes — measured at 244 of 847 on a normal desktop, holding 58-64% of all CPU activity,
    /// including every one of the top consumers. A list built that way omits the answer. The kernel
    /// call has no such access check and already carries the timings that
    /// <see cref="System.Diagnostics.Process"/> discards.
    /// </para>
    /// </summary>
    public sealed class ProcessSampler : IDisposable
    {
        /// <summary>
        /// Sampling cadence. Deliberately slower than the telemetry tick: kernel and user times
        /// advance in 15.625ms quantums, so at one second a process must burn over 1.5% of a core
        /// to register at all, and polling faster mostly produces zeros.
        /// </summary>
        private const int SampleIntervalMs = 2000;

        /// <summary>How many rows the panel shows.</summary>
        public const int TopCount = 5;

        private const int SystemProcessInformation = 5;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

        // Field offsets into SYSTEM_PROCESS_INFORMATION for x64. The struct is walked by hand
        // because the project does not enable unsafe blocks, so a fixed-buffer struct would not
        // compile.
        private const int OffNextEntry = 0x00;
        private const int OffCycleTime = 0x18;
        private const int OffCreateTime = 0x20;
        private const int OffUserTime = 0x28;
        private const int OffKernelTime = 0x30;
        private const int OffImageNameLength = 0x38;
        private const int OffImageNameBuffer = 0x40;
        private const int OffUniqueProcessId = 0x50;
        private const int OffWorkingSetSize = 0x90;

        // Cumulative I/O byte totals, in the SAME buffer as everything above. Reading disk
        // activity therefore costs two more Marshal.ReadInt64 calls per process and no extra
        // syscall — as opposed to the "\Process(*)\IO Read Bytes/sec" performance counter,
        // whose per-instance enumeration is the exact pattern that made GPU sampling cost
        // seconds per tick before it was replaced with a single snapshot.
        //
        // Both offsets were verified against Win32_Process on this OS build: the top 25
        // processes by read volume matched the WMI figures exactly.
        private const int OffReadTransferCount = 0xE8;
        private const int OffWriteTransferCount = 0xF0;

        private readonly object _gate = new();
        private readonly Dictionary<long, Previous> _previous = new();
        private readonly int _processorCount;
        private System.Threading.Timer? _timer;
        private IntPtr _buffer;
        private int _bufferSize;
        private long _lastTimestamp;
        private bool _enabled;
        private int _clients;
        private bool _disposed;

        private readonly record struct Previous(long CreateTime, long CpuTime, ulong CycleTime,
                                                ulong ReadBytes, ulong WriteBytes);

        public ProcessSampler()
        {
            // GetActiveProcessorCount, not Environment.ProcessorCount: the latter respects the
            // process's CPU affinity, so a monitor launched with a restricted affinity mask would
            // divide by too small a number and inflate every percentage.
            int count = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
            _processorCount = count > 0 ? count : Environment.ProcessorCount;
        }

        /// <summary>Most recent ranking by CPU share. Never null.</summary>
        public IReadOnlyList<ProcessUsage> TopByCpu { get; private set; } = Array.Empty<ProcessUsage>();

        /// <summary>Most recent ranking by working set. Never null.</summary>
        public IReadOnlyList<ProcessUsage> TopByRam { get; private set; } = Array.Empty<ProcessUsage>();

        /// <summary>Most recent ranking by disk traffic. Never null.</summary>
        public IReadOnlyList<ProcessUsage> TopByDisk { get; private set; } = Array.Empty<ProcessUsage>();

        /// <summary>Raised on a background thread after each sample.</summary>
        public event Action? Updated;

        /// <summary>
        /// Registers interest in sampling. The sampler runs while at least one caller holds a
        /// lease, so the stats panel and the slowdown recorder can both want it without either
        /// switching the other off. Balance every call with <see cref="Release"/>.
        /// </summary>
        public void Retain()
        {
            if (_disposed) return;
            if (System.Threading.Interlocked.Increment(ref _clients) == 1) Enabled = true;
        }

        /// <summary>Drops one lease, stopping the sampler when the last one goes.</summary>
        public void Release()
        {
            if (_disposed) return;
            if (System.Threading.Interlocked.Decrement(ref _clients) <= 0)
            {
                System.Threading.Interlocked.Exchange(ref _clients, 0);
                Enabled = false;
            }
        }

        /// <summary>
        /// Whether to sample. Turning this off stops the timer and drops the retained results, so a
        /// closed panel costs nothing and holds no state.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;

                lock (_gate)
                {
                    if (_enabled)
                    {
                        // Fire once immediately so the first sample establishes a baseline, then
                        // settle into the slower cadence.
                        _timer ??= new System.Threading.Timer(_ => Sample(), null,
                            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                        _timer.Change(0, SampleIntervalMs);
                    }
                    else
                    {
                        _timer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                        _previous.Clear();
                        _lastTimestamp = 0;
                        TopByCpu = Array.Empty<ProcessUsage>();
                        TopByRam = Array.Empty<ProcessUsage>();
                        TopByDisk = Array.Empty<ProcessUsage>();
                    }
                }
            }
        }

        private void Sample()
        {
            if (_disposed || !_enabled) return;

            try
            {
                lock (_gate)
                {
                    if (!TryQuery()) return;

                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double elapsedSeconds = _lastTimestamp == 0
                        ? 0
                        : (now - _lastTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                    _lastTimestamp = now;

                    var byCpu = new List<ProcessUsage>(512);
                    var byRam = new List<ProcessUsage>(512);
                    var byDisk = new List<ProcessUsage>(512);
                    var seen = new HashSet<long>();

                    IntPtr entry = _buffer;
                    while (true)
                    {
                        int next = Marshal.ReadInt32(entry, OffNextEntry);

                        long pid = Marshal.ReadIntPtr(entry, OffUniqueProcessId).ToInt64();
                        long createTime = Marshal.ReadInt64(entry, OffCreateTime);
                        long cpuTime = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
                        ulong cycles = unchecked((ulong)Marshal.ReadInt64(entry, OffCycleTime));
                        long workingSet = Marshal.ReadIntPtr(entry, OffWorkingSetSize).ToInt64();
                        ulong readBytes = unchecked((ulong)Marshal.ReadInt64(entry, OffReadTransferCount));
                        ulong writeBytes = unchecked((ulong)Marshal.ReadInt64(entry, OffWriteTransferCount));

                        // PID 0 is the idle process and is not a real consumer. PID 4 (System) is
                        // kept: it was measured as a genuine top-three consumer.
                        if (pid != 0)
                        {
                            seen.Add(pid);
                            string name = ReadImageName(entry, pid);
                            float percent = 0f;
                            long readRate = 0, writeRate = 0;

                            if (elapsedSeconds > 0 &&
                                _previous.TryGetValue(pid, out Previous prev) &&
                                prev.CreateTime == createTime)   // guards against PID reuse
                            {
                                long delta = cpuTime - prev.CpuTime;
                                if (delta > 0)
                                {
                                    // CPU time is in 100ns units.
                                    double busySeconds = delta / 10_000_000d;
                                    percent = (float)(busySeconds / (elapsedSeconds * _processorCount) * 100d);
                                    if (percent < 0) percent = 0;
                                    if (percent > 100) percent = 100;
                                }

                                // The kernel totals only ever climb, so an apparent decrease
                                // means the counter wrapped or the process was replaced; report
                                // nothing rather than a nonsensical negative rate.
                                readRate = Rate(readBytes, prev.ReadBytes, elapsedSeconds);
                                writeRate = Rate(writeBytes, prev.WriteBytes, elapsedSeconds);
                            }

                            var usage = new ProcessUsage(name, (int)pid, percent, workingSet)
                            {
                                DiskReadBytesPerSec = readRate,
                                DiskWriteBytesPerSec = writeRate,
                            };
                            byCpu.Add(usage);
                            byRam.Add(usage);
                            if (usage.DiskBytesPerSec > 0) byDisk.Add(usage);
                            _previous[pid] = new Previous(createTime, cpuTime, cycles, readBytes, writeBytes);
                        }

                        if (next == 0) break;
                        entry = IntPtr.Add(entry, next);
                    }

                    // Drop bookkeeping for processes that have exited.
                    if (_previous.Count > seen.Count)
                    {
                        List<long>? gone = null;
                        foreach (var key in _previous.Keys)
                        {
                            if (!seen.Contains(key)) (gone ??= new List<long>()).Add(key);
                        }
                        if (gone != null) foreach (var key in gone) _previous.Remove(key);
                    }

                    byCpu.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
                    byRam.Sort((a, b) => b.WorkingSet.CompareTo(a.WorkingSet));
                    byDisk.Sort((a, b) => b.DiskBytesPerSec.CompareTo(a.DiskBytesPerSec));

                    TopByCpu = Trim(byCpu);
                    TopByRam = Trim(byRam);
                    TopByDisk = Trim(byDisk);
                }

                Updated?.Invoke();
            }
            catch
            {
                // Process detail is supplementary; never let it disturb the rest of the app.
            }
        }

        /// <summary>
        /// Bytes per second between two cumulative kernel totals. Returns 0 when the totals
        /// went backwards, which only happens on a wrap or a mis-matched process.
        /// </summary>
        public static long Rate(ulong current, ulong previous, double elapsedSeconds)
        {
            if (elapsedSeconds <= 0 || current <= previous) return 0;
            double rate = (current - previous) / elapsedSeconds;
            return rate >= long.MaxValue ? long.MaxValue : (long)rate;
        }

        private static IReadOnlyList<ProcessUsage> Trim(List<ProcessUsage> all)
        {
            int n = Math.Min(TopCount, all.Count);
            var result = new ProcessUsage[n];
            for (int i = 0; i < n; i++) result[i] = all[i];
            return result;
        }

        /// <summary>
        /// Fills the buffer with a fresh snapshot, growing it on STATUS_INFO_LENGTH_MISMATCH. The
        /// required size scales with thread count rather than process count, so it moves around.
        /// </summary>
        private bool TryQuery()
        {
            if (_bufferSize == 0)
            {
                _bufferSize = 1 << 21; // 2 MB covers a typical desktop's ~12k threads
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                uint status = NtQuerySystemInformation(SystemProcessInformation, _buffer, (uint)_bufferSize, out uint needed);
                if (status == 0) return true;
                if (status != STATUS_INFO_LENGTH_MISMATCH) return false;

                // Grow past what the kernel asked for: more processes may appear before the retry.
                int target = Math.Max(_bufferSize * 2, (int)needed + (64 * 1024));
                Marshal.FreeHGlobal(_buffer);
                _bufferSize = target;
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }
            return false;
        }

        private static string ReadImageName(IntPtr entry, long pid)
        {
            try
            {
                ushort byteLength = unchecked((ushort)Marshal.ReadInt16(entry, OffImageNameLength));
                IntPtr text = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
                if (text != IntPtr.Zero && byteLength > 0)
                {
                    string name = Marshal.PtrToStringUni(text, byteLength / 2) ?? "";
                    if (name.Length > 0) return name;
                }
            }
            catch { }

            // The kernel reports no image name for the idle and system processes.
            return pid == 4 ? "System" : $"pid {pid}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;

            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                    _bufferSize = 0;
                }
                _previous.Clear();
            }
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(int systemInformationClass,
            IntPtr systemInformation, uint systemInformationLength, out uint returnLength);

        [DllImport("kernel32.dll")]
        private static extern int GetActiveProcessorCount(ushort groupNumber);
    }
}
