using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Counts the questions asked today, so a runaway loop or a stuck key cannot spend without
    /// limit. One Send or one Explain is one question however many tool rounds it takes; the
    /// count resets at local midnight, when the user's day does. Stored as
    /// <c>{"date":"yyyy-MM-dd","count":n}</c>; a missing or damaged file counts as zero.
    /// </summary>
    public sealed class UsageMeter
    {
        private readonly string _path;
        private readonly Func<DateTime> _localClock;
        private readonly object _gate = new();
        private string _date = "";
        private int _count;

        /// <summary>Reads the count kept in <paramref name="path"/>; <paramref name="localClock"/> gives local time.</summary>
        public UsageMeter(string path, Func<DateTime> localClock)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _localClock = localClock ?? throw new ArgumentNullException(nameof(localClock));
            Load();
        }

        /// <summary>%APPDATA%\MicaStats\ai-usage.json.</summary>
        public static string DefaultPath => Path.Combine(DiagnosticsLog.DataDir, "ai-usage.json");

        /// <summary>Questions asked since local midnight.</summary>
        public int UsedToday
        {
            get
            {
                lock (_gate) return _date == Today() ? _count : 0;
            }
        }

        /// <summary>The next local midnight, when the count starts again from zero.</summary>
        public DateTime ResetsAtLocal => _localClock().Date.AddDays(1);

        /// <summary>
        /// Counts one question and returns true, or returns false without counting when
        /// <paramref name="dailyLimit"/> (at least 1) questions were already asked today.
        /// </summary>
        public bool TryConsume(int dailyLimit)
        {
            lock (_gate)
            {
                string today = Today();
                if (_date != today)
                {
                    _date = today;
                    _count = 0;
                }
                if (_count >= Math.Max(1, dailyLimit)) return false;
                _count++;
                Save();
                return true;
            }
        }

        private string Today() => _localClock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject o) return;
                string? date = o["date"]?.GetValue<string>();
                int count = o["count"]?.GetValue<int>() ?? 0;
                if (date == null || count < 0) return;
                _date = date;
                _count = count;
            }
            catch (Exception)
            {
                // Any unreadable file (bad JSON, duplicate keys, wrong types) counts as no usage yet.
                // A damaged file costs at most one day's count; the limit protects spending, not history.
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, new JsonObject { ["date"] = _date, ["count"] = _count }.ToJsonString());
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The in-memory count still holds for this session.
            }
        }
    }
}
