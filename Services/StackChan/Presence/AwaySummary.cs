using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

/// <summary>
/// What happened while the owner was away, as at most three short ASCII lines for the
/// robot's welcome card (its font has no Thai). Counts and public readings only.
/// </summary>
internal sealed class AwaySummary
{
    private DateTimeOffset? _since;
    private int _alerts, _aiTasks, _meetings;
    private string? _peakName;
    private float _peakValue;
    private DateTimeOffset _peakAt;

    public bool Active => _since != null;

    public void Begin(DateTimeOffset at)
    {
        _since = at;
        _alerts = _aiTasks = _meetings = 0;
        _peakName = null;
        _peakValue = 0;
    }

    public void End() => _since = null;

    public void NoteAlert()
    {
        if (Active) _alerts++;
    }

    public void NoteActivityEnded(StackChanActivity activity)
    {
        if (!Active) return;
        if (activity == StackChanActivity.Ai) _aiTasks++;
        else if (activity == StackChanActivity.Meeting) _meetings++;
    }

    public void NoteMetrics(DateTimeOffset at, float cpu, float ram, float gpu)
    {
        if (!Active) return;
        foreach (var (name, value) in new[] { ("CPU", cpu), ("RAM", ram), ("GPU", gpu) })
        {
            if (!float.IsFinite(value) || value <= _peakValue) continue;
            _peakName = name;
            _peakValue = Math.Min(value, 100);
            _peakAt = at;
        }
    }

    public IReadOnlyList<string> Lines(DateTimeOffset now)
    {
        var lines = new List<string>(3);
        var away = _since is { } since ? now - since : TimeSpan.Zero;
        lines.Add(away.TotalMinutes >= 60
            ? string.Format(CultureInfo.InvariantCulture, "Away {0} h {1:00} min", (int)away.TotalHours, away.Minutes)
            : string.Format(CultureInfo.InvariantCulture, "Away {0} min", Math.Max(1, (int)away.TotalMinutes)));

        var events = new List<string>();
        if (_alerts > 0) events.Add(Count(_alerts, "alert"));
        if (_aiTasks > 0) events.Add(Count(_aiTasks, "AI task"));
        if (_meetings > 0) events.Add(Count(_meetings, "meeting"));
        string happened = events.Count == 0 ? "All quiet while you were away" : string.Join(", ", events);
        while (happened.Length > 32 && events.Count > 1)
        {
            events.RemoveAt(events.Count - 1);
            happened = string.Join(", ", events);
        }
        lines.Add(happened.Length > 32 ? happened[..32] : happened);

        if (_peakName != null)
            // Invariant culture: a Thai locale would otherwise stamp Buddhist-calendar text.
            lines.Add(string.Format(CultureInfo.InvariantCulture, "Peak {0} {1:0}% at {2}", _peakName, _peakValue,
                _peakAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));
        End();
        return lines;
    }

    private static string Count(int count, string noun) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1}{2}", count, noun, count == 1 ? "" : "s");
}
