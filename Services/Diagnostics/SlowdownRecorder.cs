using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Diagnostics
{
    /// <summary>What pushed the machine over the line.</summary>
    public enum SlowdownCause
    {
        None,
        Cpu,
        Disk,
        Memory,
        /// <summary>The user asked for a recording by hand, without any threshold being crossed.</summary>
        Manual
    }

    /// <summary>One second-scale snapshot of what the machine was doing.</summary>
    public sealed record SlowdownFrame(
        DateTime At,
        float CpuPercent,
        float RamPercent,
        long DiskBytesPerSec,
        IReadOnlyList<ProcessUsage> TopCpu,
        IReadOnlyList<ProcessUsage> TopDisk,
        IReadOnlyList<ProcessUsage> TopRam)
    {
        /// <summary>The process using most CPU in this frame, or null.</summary>
        public ProcessUsage? BusiestCpu => TopCpu.Count > 0 ? TopCpu[0] : null;

        /// <summary>The process moving most data in this frame, or null.</summary>
        public ProcessUsage? BusiestDisk => TopDisk.Count > 0 ? TopDisk[0] : null;
    }

    /// <summary>When the recorder should decide the machine is struggling.</summary>
    public sealed record SlowdownThresholds(
        float CpuPercent,
        long DiskBytesPerSec,
        float MemoryPercent,
        int SustainSeconds)
    {
        /// <summary>
        /// Defaults chosen to fire on a real stall and stay quiet during ordinary work. A build
        /// or a video export saturates the CPU for minutes and is not a fault, so the CPU line
        /// sits high and the sustain window is long enough to ignore a brief spike.
        /// </summary>
        public static SlowdownThresholds Default { get; } = new(
            CpuPercent: 90f,
            DiskBytesPerSec: 150L * 1024 * 1024,   // 150 MB/s sustained is contention, not use
            MemoryPercent: 92f,
            SustainSeconds: 8);
    }

    /// <summary>
    /// Decides when a run of frames amounts to a slowdown.
    ///
    /// <para>
    /// Pure and clock-free: every decision comes from the timestamps carried by the frames, so
    /// a test can drive an hour of history through it in a millisecond and assert exactly when
    /// it fires.
    /// </para>
    /// </summary>
    public sealed class SlowdownDetector
    {
        /// <summary>Minimum gap between recordings, so one bad afternoon does not produce fifty files.</summary>
        public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

        private DateTime? _cpuSince;
        private DateTime? _diskSince;
        private DateTime? _memorySince;
        private DateTime _lastFired = DateTime.MinValue;

        /// <summary>
        /// Feeds one frame and reports the cause when a threshold has just been sustained long
        /// enough, or <see cref="SlowdownCause.None"/> otherwise.
        /// </summary>
        public SlowdownCause Feed(SlowdownFrame frame, SlowdownThresholds thresholds)
        {
            if (frame == null || thresholds == null) return SlowdownCause.None;

            var sustain = TimeSpan.FromSeconds(Math.Max(1, thresholds.SustainSeconds));

            bool cpu = Track(ref _cpuSince, frame.CpuPercent >= thresholds.CpuPercent, frame.At, sustain);
            bool disk = Track(ref _diskSince, frame.DiskBytesPerSec >= thresholds.DiskBytesPerSec, frame.At, sustain);
            bool memory = Track(ref _memorySince, frame.RamPercent >= thresholds.MemoryPercent, frame.At, sustain);

            if (!cpu && !disk && !memory) return SlowdownCause.None;

            // A recording just after the last one would capture the same episode twice.
            if (_lastFired != DateTime.MinValue && frame.At - _lastFired < Cooldown)
                return SlowdownCause.None;

            _lastFired = frame.At;

            // Reset the runs so a single long episode fires once, not once per frame.
            _cpuSince = _diskSince = _memorySince = null;

            // Disk contention is reported ahead of CPU: when both are saturated, the stall the
            // user feels is almost always the one waiting on I/O.
            if (disk) return SlowdownCause.Disk;
            if (memory) return SlowdownCause.Memory;
            return SlowdownCause.Cpu;
        }

        /// <summary>Tracks one condition's continuous run, returning true once it is long enough.</summary>
        private static bool Track(ref DateTime? since, bool active, DateTime now, TimeSpan sustain)
        {
            if (!active) { since = null; return false; }
            since ??= now;
            return now - since.Value >= sustain;
        }

        /// <summary>Forgets all state, e.g. after the user changed the thresholds.</summary>
        public void Reset()
        {
            _cpuSince = _diskSince = _memorySince = null;
            _lastFired = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Renders a captured window as the text report. Pure over the frames so the shape can be
    /// pinned by tests, in the same spirit as <see cref="HardwareInfo.HardwareReportWriter"/>.
    /// </summary>
    public static class SlowdownReportWriter
    {
        public static string Write(IReadOnlyList<SlowdownFrame> frames, SlowdownCause cause,
                                   SlowdownThresholds thresholds, string appVersion)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.AppendLine("==============================================================");
            sb.AppendLine(" MicaStats Slowdown Report");
            sb.AppendLine(" Version   : MicaStats " + appVersion);
            sb.AppendLine(" Trigger   : " + Describe(cause, thresholds));

            if (frames == null || frames.Count == 0)
            {
                sb.AppendLine("==============================================================");
                sb.AppendLine();
                sb.AppendLine("No samples were held when this report was written.");
                return sb.ToString();
            }

            // InvariantCulture throughout: the default would also swap the CALENDAR, stamping
            // Buddhist-era years on a Thai machine.
            sb.AppendLine(" Window    : " + Stamp(frames[0].At) + "  to  " + Stamp(frames[^1].At));
            sb.AppendLine(" Samples   : " + frames.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("==============================================================");
            sb.AppendLine();

            sb.AppendLine("[Timeline]");
            sb.AppendLine("  time      cpu    mem    disk         busiest process");
            sb.AppendLine("  --------  -----  -----  -----------  ----------------------------");
            foreach (var f in frames)
            {
                var lead = f.BusiestDisk != null && f.BusiestDisk.DiskBytesPerSec > 0
                    ? f.BusiestDisk
                    : f.BusiestCpu;

                string who = lead == null
                    ? "-"
                    : lead.Name + " (" + lead.Pid.ToString(CultureInfo.InvariantCulture) + ")";

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0}  {1,5}  {2,5}  {3,11}  {4}",
                    f.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    f.CpuPercent.ToString("F0", CultureInfo.InvariantCulture) + "%",
                    f.RamPercent.ToString("F0", CultureInfo.InvariantCulture) + "%",
                    ProcessUsage.FormatRate(f.DiskBytesPerSec),
                    who));
            }

            AppendOffenders(sb, frames);

            sb.AppendLine();
            sb.AppendLine("Read the timeline from the bottom: the last rows are the moment the");
            sb.AppendLine("threshold was crossed, and the rows above show what led up to it.");
            return sb.ToString();
        }

        /// <summary>Aggregates the window so the culprit is named rather than merely present.</summary>
        private static void AppendOffenders(StringBuilder sb, IReadOnlyList<SlowdownFrame> frames)
        {
            var cpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var disk = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in frames)
            {
                foreach (var p in f.TopCpu)
                {
                    cpu.TryGetValue(p.Name, out double v);
                    cpu[p.Name] = v + p.CpuPercent;
                }
                foreach (var p in f.TopDisk)
                {
                    disk.TryGetValue(p.Name, out double v);
                    disk[p.Name] = v + p.DiskBytesPerSec;
                }
            }

            sb.AppendLine();
            sb.AppendLine("[Worst offenders across the window]");
            AppendRanked(sb, "  By CPU  ", cpu, frames.Count,
                v => v.ToString("F1", CultureInfo.InvariantCulture) + "% average");
            AppendRanked(sb, "  By disk ", disk, frames.Count,
                v => ProcessUsage.FormatRate((long)v) + " average");
        }

        private static void AppendRanked(StringBuilder sb, string heading,
                                         Dictionary<string, double> totals, int frameCount,
                                         Func<double, string> format)
        {
            if (totals.Count == 0 || frameCount <= 0)
            {
                sb.AppendLine(heading + ": nothing measurable");
                return;
            }

            var ranked = new List<KeyValuePair<string, double>>(totals);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

            sb.AppendLine(heading + ":");
            int shown = 0;
            foreach (var pair in ranked)
            {
                if (shown >= 5) break;
                double mean = pair.Value / frameCount;
                if (mean <= 0) continue;
                sb.AppendLine("      " + pair.Key.PadRight(30) + format(mean));
                shown++;
            }
            if (shown == 0) sb.AppendLine("      nothing measurable");
        }

        private static string Stamp(DateTime at) =>
            at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        public static string Describe(SlowdownCause cause, SlowdownThresholds t) => cause switch
        {
            SlowdownCause.Cpu => "CPU stayed at or above " +
                t.CpuPercent.ToString("F0", CultureInfo.InvariantCulture) + "% for " +
                t.SustainSeconds.ToString(CultureInfo.InvariantCulture) + " s",
            SlowdownCause.Disk => "Disk stayed at or above " +
                ProcessUsage.FormatRate(t.DiskBytesPerSec) + " for " +
                t.SustainSeconds.ToString(CultureInfo.InvariantCulture) + " s",
            SlowdownCause.Memory => "Memory stayed at or above " +
                t.MemoryPercent.ToString("F0", CultureInfo.InvariantCulture) + "% for " +
                t.SustainSeconds.ToString(CultureInfo.InvariantCulture) + " s",
            SlowdownCause.Manual => "Recorded by hand",
            _ => "None",
        };

        /// <summary>A short sentence for the notification and the log.</summary>
        public static string Headline(IReadOnlyList<SlowdownFrame> frames, SlowdownCause cause)
        {
            if (frames == null || frames.Count == 0) return "Nothing was captured.";
            var last = frames[^1];

            return cause switch
            {
                SlowdownCause.Disk when last.BusiestDisk != null =>
                    last.BusiestDisk.Name + " was moving " + last.BusiestDisk.DiskText,
                SlowdownCause.Cpu when last.BusiestCpu != null =>
                    last.BusiestCpu.Name + " was using " + last.BusiestCpu.CpuText + " of the CPU",
                SlowdownCause.Memory =>
                    "Memory was " + last.RamPercent.ToString("F0", CultureInfo.InvariantCulture) + "% full",
                _ => "Captured " + frames.Count.ToString(CultureInfo.InvariantCulture) + " samples",
            };
        }
    }

    /// <summary>
    /// Keeps a rolling window of what the machine has been doing, and saves it when the
    /// machine struggles — or when the user asks, right after feeling a stall.
    ///
    /// <para>
    /// This exists because Task Manager only ever shows the present instant. By the time a
    /// freeze is over and a window has been opened, the process responsible has finished and
    /// left nothing behind. Windows keeps no retrospective anywhere, so the only way to answer
    /// "what was that?" is to have been recording already.
    /// </para>
    /// </summary>
    public sealed class SlowdownRecorder : IDisposable
    {
        /// <summary>Reports older than this are deleted, so the folder cannot grow without bound.</summary>
        public const int KeepReports = 30;

        private readonly ProcessSampler _sampler;
        private readonly SlowdownDetector _detector = new();
        private readonly object _gate = new();
        private readonly Queue<SlowdownFrame> _frames = new();
        private readonly Func<SystemMetrics?> _metrics;
        private readonly Action _sampleRequested;

        private int _windowSeconds = 300;
        private bool _running;
        private bool _disposed;

        public SlowdownRecorder(ProcessSampler sampler, Func<SystemMetrics?> metrics)
        {
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _sampleRequested = OnSample;
        }

        /// <summary>Thresholds in force. Replacing them resets the detector's running state.</summary>
        public SlowdownThresholds Thresholds { get; private set; } = SlowdownThresholds.Default;

        /// <summary>Seconds of history retained. Clamped so a report stays readable.</summary>
        public int WindowSeconds
        {
            get => _windowSeconds;
            set => _windowSeconds = Math.Clamp(value, 60, 900);
        }

        /// <summary>Whether a report is written automatically when a threshold is crossed.</summary>
        public bool AutoCapture { get; set; } = true;

        /// <summary>Raised after a report is written, with the file path and a one-line summary.</summary>
        public event Action<string, string>? Captured;

        /// <summary>Frames currently held. Used by the live view in the diagnostics window.</summary>
        public IReadOnlyList<SlowdownFrame> Frames
        {
            get { lock (_gate) return new List<SlowdownFrame>(_frames); }
        }

        public bool IsRunning => _running;

        public void SetThresholds(SlowdownThresholds thresholds)
        {
            Thresholds = thresholds ?? SlowdownThresholds.Default;
            _detector.Reset();
        }

        /// <summary>Begins recording. Idempotent.</summary>
        public void Start()
        {
            if (_disposed || _running) return;
            _running = true;
            _sampler.Updated += _sampleRequested;
            _sampler.Retain();
            DiagnosticsLog.Log("slowdown", "Recording started, " +
                _windowSeconds.ToString(CultureInfo.InvariantCulture) + " s window");
        }

        /// <summary>Stops recording and drops the retained window. Idempotent.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _sampler.Updated -= _sampleRequested;
            _sampler.Release();
            lock (_gate) _frames.Clear();
            _detector.Reset();
            DiagnosticsLog.Log("slowdown", "Recording stopped");
        }

        /// <summary>Called on the sampler's thread after each process sample.</summary>
        private void OnSample()
        {
            if (!_running || _disposed) return;

            try
            {
                var metrics = _metrics();
                var frame = BuildFrame(metrics);

                SlowdownCause cause;
                lock (_gate)
                {
                    _frames.Enqueue(frame);
                    TrimLocked(frame.At);
                    cause = _detector.Feed(frame, Thresholds);
                }

                if (cause != SlowdownCause.None && AutoCapture) Capture(cause);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("slowdown", "Sample failed", ex);
            }
        }

        private SlowdownFrame BuildFrame(SystemMetrics? metrics)
        {
            var topDisk = _sampler.TopByDisk;

            // Machine-wide disk traffic is summed from the same per-process figures rather than
            // read from a separate counter, so the total and the named culprit can never
            // disagree with each other in the report.
            long total = 0;
            foreach (var p in topDisk) total += p.DiskBytesPerSec;

            return new SlowdownFrame(
                At: DateTime.Now,
                CpuPercent: metrics?.CpuUsage ?? 0f,
                RamPercent: metrics?.RamPercent ?? 0f,
                DiskBytesPerSec: total,
                TopCpu: _sampler.TopByCpu,
                TopDisk: topDisk,
                TopRam: _sampler.TopByRam);
        }

        private void TrimLocked(DateTime now)
        {
            var cutoff = now - TimeSpan.FromSeconds(_windowSeconds);
            while (_frames.Count > 0 && _frames.Peek().At < cutoff) _frames.Dequeue();
        }

        /// <summary>
        /// Writes the retained window to a report. Safe to call from the UI thread — the file
        /// is small and the frames are already in memory.
        /// </summary>
        /// <returns>The path written, or null when there was nothing to write.</returns>
        public string? Capture(SlowdownCause cause)
        {
            List<SlowdownFrame> frames;
            lock (_gate) frames = new List<SlowdownFrame>(_frames);
            if (frames.Count == 0) return null;

            try
            {
                Directory.CreateDirectory(ReportDir);

                string version = typeof(SlowdownRecorder).Assembly.GetName().Version?.ToString(3) ?? "?";
                string text = SlowdownReportWriter.Write(frames, cause, Thresholds, version);

                string path = Path.Combine(ReportDir,
                    "slowdown-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));

                Prune();

                string headline = SlowdownReportWriter.Headline(frames, cause);
                DiagnosticsLog.Log("slowdown",
                    SlowdownReportWriter.Describe(cause, Thresholds) + " — " + headline + " — saved " + path);
                Captured?.Invoke(path, headline);
                return path;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("slowdown", "Report write failed", ex);
                return null;
            }
        }

        /// <summary>Where reports land, beside the hardware reports and the log.</summary>
        public static string ReportDir => Path.Combine(DiagnosticsLog.DataDir, "reports");

        /// <summary>Deletes the oldest slowdown reports past <see cref="KeepReports"/>.</summary>
        private static void Prune()
        {
            try
            {
                // The pattern is load-bearing: hardware reports share this folder and must not
                // be swept up by the retention policy.
                var files = new DirectoryInfo(ReportDir).GetFiles("slowdown-*.txt");
                if (files.Length <= KeepReports) return;

                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeepReports; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
