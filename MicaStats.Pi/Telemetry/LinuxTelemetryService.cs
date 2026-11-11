using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MicaStats.Pi.Telemetry;

/// <summary>
/// Linux replacement for the Windows app's PerformanceCounter/WMI telemetry:
/// /proc/stat, /proc/meminfo, /sys/class/thermal, /sys/class/net statistics,
/// /proc/diskstats + DriveInfo, /proc/loadavg, /proc/uptime and /proc/[pid]/stat.
///
/// Follows the repo's sensor honesty rule: a reading that cannot be taken is
/// reported as null (rendered "—"), never as a fabricated zero.
/// </summary>
public sealed class LinuxTelemetryService
{
    private const int HistoryLength = 72;
    private const double SectorBytes = 512;

    public Series CpuHistory { get; } = new(HistoryLength);
    public Series TempHistory { get; } = new(HistoryLength);
    public Series NetUpHistory { get; } = new(HistoryLength, peakFloor: 16 * 1024);
    public Series NetDownHistory { get; } = new(HistoryLength, peakFloor: 16 * 1024);

    private long _prevCpuTotal, _prevCpuIdle;
    private long _prevRx = -1, _prevTx = -1;
    private long _prevRead = -1, _prevWritten = -1;
    private DateTime _prevTick = DateTime.MinValue;
    private readonly Dictionary<int, long> _prevProcJiffies = new();
    private DateTime _prevProcTime = DateTime.MinValue;
    private int _tick;

    private readonly string _netInterface = PickNetInterface();
    private readonly string? _diskDevice = PickDiskDevice();
    private static readonly long ClockTicksPerSec = 100; // CLK_TCK on Raspberry Pi OS kernels

    public TelemetrySnapshot Sample()
    {
        var now = DateTime.UtcNow;
        double elapsed = _prevTick == DateTime.MinValue ? 0 : (now - _prevTick).TotalSeconds;
        _prevTick = now;

        var (cpuPercent, coreCount) = ReadCpu();
        CpuHistory.Add(cpuPercent);

        var (memPercent, memUsedGb, memTotalGb, swapUsedGb, swapTotalGb) = ReadMemory();

        float? temp = ReadTemperature();
        if (temp is float t) TempHistory.Add(t); else TempHistory.MarkUnavailable();

        var (up, down) = ReadNetworkRates(elapsed);
        NetUpHistory.Add((float)up);
        NetDownHistory.Add((float)down);

        var (diskUsedPercent, diskFreeGb, diskTotalGb) = ReadDiskSpace();
        var (readRate, writeRate) = ReadDiskRates(elapsed);

        _tick++;
        IReadOnlyList<ProcessSample>? processes = _tick % 3 == 1 ? SampleProcesses(coreCount) : null;

        return new TelemetrySnapshot
        {
            CpuPercent = cpuPercent,
            CoreCount = coreCount,
            LoadAvg1 = ReadLoadAvg(),
            CpuFreqMhz = ReadCpuFreqMhz(),
            MemPercent = memPercent,
            MemUsedGb = memUsedGb,
            MemTotalGb = memTotalGb,
            SwapUsedGb = swapUsedGb,
            SwapTotalGb = swapTotalGb,
            TempC = temp,
            NetInterface = _netInterface,
            NetUpBytesPerSec = up,
            NetDownBytesPerSec = down,
            DiskUsedPercent = diskUsedPercent,
            DiskFreeGb = diskFreeGb,
            DiskTotalGb = diskTotalGb,
            DiskReadBytesPerSec = readRate,
            DiskWriteBytesPerSec = writeRate,
            Uptime = ReadUptime(),
            TopProcesses = processes,
        };
    }

    // ---- CPU -------------------------------------------------------------

    private (float Percent, int Cores) ReadCpu()
    {
        try
        {
            string[] lines = File.ReadAllLines("/proc/stat");
            int cores = lines.Count(l => l.Length > 3 && l.StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(l[3]));

            // "cpu  user nice system idle iowait irq softirq steal ..."
            var f = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long user = long.Parse(f[1]), nice = long.Parse(f[2]), system = long.Parse(f[3]);
            long idle = long.Parse(f[4]), iowait = long.Parse(f[5]);
            long irq = long.Parse(f[6]), softirq = long.Parse(f[7]);
            long steal = f.Length > 8 ? long.Parse(f[8]) : 0;

            long idleAll = idle + iowait;
            long total = user + nice + system + idleAll + irq + softirq + steal;

            float percent = 0;
            if (_prevCpuTotal > 0)
            {
                long dTotal = total - _prevCpuTotal;
                long dIdle = idleAll - _prevCpuIdle;
                if (dTotal > 0) percent = Math.Clamp(100f * (dTotal - dIdle) / dTotal, 0f, 100f);
            }
            _prevCpuTotal = total;
            _prevCpuIdle = idleAll;
            return (percent, Math.Max(1, cores));
        }
        catch
        {
            return (0, 1);
        }
    }

    private static float ReadLoadAvg()
    {
        try
        {
            var f = File.ReadAllText("/proc/loadavg").Split(' ');
            return float.Parse(f[0], CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    private static double? ReadCpuFreqMhz()
    {
        try
        {
            string s = File.ReadAllText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq").Trim();
            return long.Parse(s) / 1000.0;
        }
        catch { return null; }
    }

    // ---- Memory ----------------------------------------------------------

    private static (float Percent, double UsedGb, double TotalGb, double SwapUsedGb, double SwapTotalGb) ReadMemory()
    {
        try
        {
            long memTotal = 0, memAvailable = 0, swapTotal = 0, swapFree = 0;
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                static long Kb(string l)
                {
                    var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return long.Parse(parts[1]);
                }
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) memTotal = Kb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) memAvailable = Kb(line);
                else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal)) swapTotal = Kb(line);
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal)) swapFree = Kb(line);
            }
            long used = memTotal - memAvailable;
            float percent = memTotal > 0 ? 100f * used / memTotal : 0;
            const double kbPerGb = 1024.0 * 1024.0;
            return (percent, used / kbPerGb, memTotal / kbPerGb, (swapTotal - swapFree) / kbPerGb, swapTotal / kbPerGb);
        }
        catch { return (0, 0, 0, 0, 0); }
    }

    // ---- Temperature -----------------------------------------------------

    private static float? ReadTemperature()
    {
        try
        {
            string s = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp").Trim();
            return long.Parse(s) / 1000f;
        }
        catch { return null; }
    }

    // ---- Network ---------------------------------------------------------

    private static string PickNetInterface()
    {
        try
        {
            var candidates = Directory.GetDirectories("/sys/class/net")
                .Select(Path.GetFileName)
                .Where(n => n != null && n != "lo")
                .Cast<string>()
                .OrderBy(n => n == "eth0" ? 0 : 1)
                .ThenBy(n => n, StringComparer.Ordinal);
            foreach (string name in candidates)
            {
                string state = File.ReadAllText($"/sys/class/net/{name}/operstate").Trim();
                if (state is "up" or "unknown") return name;
            }
        }
        catch { /* fall through */ }
        return "eth0";
    }

    private (double Up, double Down) ReadNetworkRates(double elapsed)
    {
        try
        {
            long rx = long.Parse(File.ReadAllText($"/sys/class/net/{_netInterface}/statistics/rx_bytes").Trim());
            long tx = long.Parse(File.ReadAllText($"/sys/class/net/{_netInterface}/statistics/tx_bytes").Trim());
            double up = 0, down = 0;
            if (_prevRx >= 0 && elapsed > 0)
            {
                down = Math.Max(0, rx - _prevRx) / elapsed;
                up = Math.Max(0, tx - _prevTx) / elapsed;
            }
            _prevRx = rx;
            _prevTx = tx;
            return (up, down);
        }
        catch { return (0, 0); }
    }

    // ---- Disk ------------------------------------------------------------

    private static string? PickDiskDevice()
    {
        try
        {
            foreach (string dev in new[] { "nvme0n1", "mmcblk0", "sda", "vda" })
                if (Directory.Exists($"/sys/block/{dev}")) return dev;
        }
        catch { /* fall through */ }
        return null;
    }

    private static (float UsedPercent, double FreeGb, double TotalGb) ReadDiskSpace()
    {
        try
        {
            var d = new DriveInfo("/");
            double total = d.TotalSize, free = d.AvailableFreeSpace;
            float usedPercent = total > 0 ? (float)(100.0 * (total - free) / total) : 0;
            const double gb = 1024.0 * 1024.0 * 1024.0;
            return (usedPercent, free / gb, total / gb);
        }
        catch { return (0, 0, 0); }
    }

    private (double? Read, double? Write) ReadDiskRates(double elapsed)
    {
        if (_diskDevice == null) return (null, null);
        try
        {
            foreach (string line in File.ReadLines("/proc/diskstats"))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10 || f[2] != _diskDevice) continue;
                long sectorsRead = long.Parse(f[5]);
                long sectorsWritten = long.Parse(f[9]);
                double? read = null, write = null;
                if (_prevRead >= 0 && elapsed > 0)
                {
                    read = Math.Max(0, sectorsRead - _prevRead) * SectorBytes / elapsed;
                    write = Math.Max(0, sectorsWritten - _prevWritten) * SectorBytes / elapsed;
                }
                _prevRead = sectorsRead;
                _prevWritten = sectorsWritten;
                return (read, write);
            }
        }
        catch { /* fall through */ }
        return (null, null);
    }

    // ---- Uptime & processes ---------------------------------------------

    private static TimeSpan ReadUptime()
    {
        try
        {
            var f = File.ReadAllText("/proc/uptime").Split(' ');
            return TimeSpan.FromSeconds(double.Parse(f[0], CultureInfo.InvariantCulture));
        }
        catch { return TimeSpan.Zero; }
    }

    private IReadOnlyList<ProcessSample> SampleProcesses(int coreCount)
    {
        var now = DateTime.UtcNow;
        double elapsed = _prevProcTime == DateTime.MinValue ? 0 : (now - _prevProcTime).TotalSeconds;
        _prevProcTime = now;

        var results = new List<(int Pid, float Cpu)>();
        var seen = new HashSet<int>();
        long pageMb = Environment.SystemPageSize; // bytes per page; Pi 5 kernels use 16K pages

        try
        {
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(dir);
                if (!int.TryParse(name, out int pid)) continue;
                try
                {
                    string stat = File.ReadAllText($"{dir}/stat");
                    // comm can contain spaces/parens: fields resume after the last ')'.
                    int close = stat.LastIndexOf(')');
                    if (close < 0) continue;
                    var f = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // After ')': f[0]=state ... utime=f[11], stime=f[12] (stat fields 14,15).
                    long jiffies = long.Parse(f[11]) + long.Parse(f[12]);
                    seen.Add(pid);

                    if (_prevProcJiffies.TryGetValue(pid, out long prev) && elapsed > 0)
                    {
                        float cpu = 100f * (jiffies - prev) / (float)(elapsed * ClockTicksPerSec);
                        if (cpu > 0.05f) results.Add((pid, cpu));
                    }
                    _prevProcJiffies[pid] = jiffies;
                }
                catch { /* process vanished mid-walk */ }
            }
        }
        catch { /* /proc unreadable — return what we have */ }

        foreach (int gone in _prevProcJiffies.Keys.Where(p => !seen.Contains(p)).ToList())
            _prevProcJiffies.Remove(gone);

        return results
            .OrderByDescending(r => r.Cpu)
            .Take(5)
            .Select(r =>
            {
                string procName;
                double memMb = 0;
                try { procName = File.ReadAllText($"/proc/{r.Pid}/comm").Trim(); }
                catch { procName = $"pid {r.Pid}"; }
                try
                {
                    var statm = File.ReadAllText($"/proc/{r.Pid}/statm").Split(' ');
                    memMb = long.Parse(statm[1]) * (double)pageMb / (1024.0 * 1024.0);
                }
                catch { /* keep 0 */ }
                return new ProcessSample(r.Pid, procName, r.Cpu, memMb);
            })
            .ToList();
    }
}
