using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

public enum PresenceState { Unknown, Present, Away }

public enum PresenceEventKind { Arrived, Left, Welcome, OnlookerStarted, OnlookerEnded, BreakDue, LockDue }

public sealed record PresenceEvent(PresenceEventKind Kind, TimeSpan AwayFor = default);

public sealed record PresenceOptions(TimeSpan DozeAfter, TimeSpan WelcomeAfterAway, TimeSpan OnlookerAfter,
    TimeSpan OnlookerClearAfter, TimeSpan BreakAfter, TimeSpan BreakResetAway, TimeSpan? LockAfter)
{
    public static PresenceOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(5), null);
}

/// <summary>
/// Turns per-frame face lists into presence decisions. Pure: time arrives with each call.
/// No frames at all means Unknown (camera covered, stream stalled), never Away.
/// </summary>
public sealed class PresenceTracker(PresenceOptions options)
{
    private static readonly TimeSpan StreamLost = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackgroundAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CandidateLost = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackgroundForgotten = TimeSpan.FromMinutes(5);
    // Still means within a quarter of its own size: tolerant of detector jitter on small faces.
    private const float StillShift = 0.25f;
    private const float SecondaryScore = 0.8f;

    private readonly bool[] _recent = new bool[3];
    private readonly List<Candidate> _candidates = [];
    private int _recentCount;
    private DateTimeOffset? _lastFrameAt, _lastFaceAt, _noFaceSince, _awayStarted, _presentSince,
        _twoFacesSince, _clearSince, _unknownSince;
    private bool _previousHadFace, _breakFired, _lockFired, _breakSuppressed;

    public PresenceState State { get; private set; } = PresenceState.Unknown;
    public DetectedFace? MainFace { get; private set; }
    public bool OnlookerActive { get; private set; }
    /// <summary>Faces counted in the latest frame, background faces excluded.</summary>
    public int FaceCount { get; private set; }

    public void SetBreakSuppressed(bool suppressed) => _breakSuppressed = suppressed;

    public IReadOnlyList<PresenceEvent> Observe(DateTimeOffset at, IReadOnlyList<DetectedFace> faces, bool usable = true)
    {
        var events = new List<PresenceEvent>();
        // A covered lens or a dark room shows nothing: that is no information, not "nobody here".
        if (!usable) return events;
        _lastFrameAt = at;
        var counted = CountFaces(at, faces, out bool becameBackground);
        FaceCount = counted.Count;
        MainFace = counted.Count == 0 ? null : counted[0];
        bool hasFace = MainFace != null;

        _recent[_recentCount % 3] = hasFace;
        _recentCount++;
        if (hasFace)
        {
            _lastFaceAt = at;
            _noFaceSince = null;
        }
        else
        {
            // Measure absence from the last face seen; after Unknown, from the first fresh frame.
            _noFaceSince ??= _previousHadFace ? _lastFaceAt : at;
        }
        _previousHadFace = hasFace;

        int recentFaces = 0;
        for (int index = 0; index < Math.Min(3, _recentCount); index++)
            if (_recent[index]) recentFaces++;
        if (State != PresenceState.Present && recentFaces >= 2)
            Arrive(at, events);
        else if (State != PresenceState.Away && !hasFace && _noFaceSince is { } since && at - since >= options.DozeAfter)
        {
            State = PresenceState.Away;
            _awayStarted = since;
            _lockFired = false;
            events.Add(new PresenceEvent(PresenceEventKind.Left));
        }

        UpdateOnlooker(at, counted.Count, becameBackground, events);
        EvaluateTimers(at, events);
        return events;
    }

    public IReadOnlyList<PresenceEvent> Tick(DateTimeOffset at)
    {
        var events = new List<PresenceEvent>();
        if (_lastFrameAt is { } last && at - last >= StreamLost && State != PresenceState.Unknown)
        {
            // Nothing seen is not "nobody here": stop every timer until frames return.
            State = PresenceState.Unknown;
            _unknownSince = last;
            MainFace = null;
            FaceCount = 0;
            _noFaceSince = null;
            _previousHadFace = false;
            _recentCount = 0;
            Array.Clear(_recent);
            _twoFacesSince = null;
            _clearSince = null;
            // An unseen onlooker cannot be confirmed; the vault stays locked, the cover lifts.
            if (OnlookerActive)
            {
                OnlookerActive = false;
                events.Add(new PresenceEvent(PresenceEventKind.OnlookerEnded));
            }
        }
        if (State != PresenceState.Unknown) EvaluateTimers(at, events);
        return events;
    }

    private void Arrive(DateTimeOffset at, List<PresenceEvent> events)
    {
        var previous = State;
        State = PresenceState.Present;
        // Time the camera saw nothing is not time at the desk: the break timer pauses over it.
        if (previous == PresenceState.Unknown && _unknownSince is { } blind && _presentSince is { } present)
            _presentSince = present + (at - blind);
        _unknownSince = null;
        TimeSpan awayFor = previous == PresenceState.Away && _awayStarted is { } started ? at - started : TimeSpan.Zero;
        if (_presentSince == null || awayFor >= options.BreakResetAway)
        {
            _presentSince = at;
            _breakFired = false;
        }
        events.Add(new PresenceEvent(PresenceEventKind.Arrived, awayFor));
        if (previous == PresenceState.Away && awayFor >= options.WelcomeAfterAway)
            events.Add(new PresenceEvent(PresenceEventKind.Welcome, awayFor));
    }

    private void EvaluateTimers(DateTimeOffset at, List<PresenceEvent> events)
    {
        if (State == PresenceState.Present && !_breakFired && !_breakSuppressed &&
            _presentSince is { } since && at - since >= options.BreakAfter)
        {
            _breakFired = true;
            events.Add(new PresenceEvent(PresenceEventKind.BreakDue));
        }
        if (State == PresenceState.Away && !_lockFired && options.LockAfter is { } lockAfter &&
            _awayStarted is { } away && at - away >= lockAfter)
        {
            _lockFired = true;
            events.Add(new PresenceEvent(PresenceEventKind.LockDue, at - away));
        }
    }

    private void UpdateOnlooker(DateTimeOffset at, int faces, bool becameBackground, List<PresenceEvent> events)
    {
        if (faces >= 2)
        {
            _clearSince = null;
            _twoFacesSince ??= at;
            if (!OnlookerActive && at - _twoFacesSince >= options.OnlookerAfter)
            {
                OnlookerActive = true;
                events.Add(new PresenceEvent(PresenceEventKind.OnlookerStarted));
            }
            return;
        }
        _twoFacesSince = null;
        if (!OnlookerActive) return;
        _clearSince ??= at;
        if (becameBackground || at - _clearSince >= options.OnlookerClearAfter)
        {
            OnlookerActive = false;
            _clearSince = null;
            events.Add(new PresenceEvent(PresenceEventKind.OnlookerEnded));
        }
    }

    /// <summary>
    /// Faces that count, largest first. A secondary face that stays perfectly still for 30 s
    /// (a poster or photo) becomes background until it moves; the main face never does.
    /// </summary>
    private List<DetectedFace> CountFaces(DateTimeOffset at, IReadOnlyList<DetectedFace> faces, out bool becameBackground)
    {
        becameBackground = false;
        var matches = new Candidate?[faces.Count];
        for (int index = 0; index < faces.Count; index++)
        {
            Candidate? best = null;
            float bestShift = float.MaxValue;
            foreach (var candidate in _candidates)
            {
                float shift = Shift(faces[index], candidate.First);
                if (shift <= StillShift && shift < bestShift) { best = candidate; bestShift = shift; }
            }
            matches[index] = best;
        }

        int main = -1;
        for (int index = 0; index < faces.Count; index++)
        {
            if (matches[index] is { Background: true }) continue;
            if (main < 0 || Area(faces[index]) > Area(faces[main])) main = index;
        }

        var counted = new List<DetectedFace>();
        if (main >= 0) counted.Add(faces[main]);
        for (int index = 0; index < faces.Count; index++)
        {
            if (index == main) continue;
            // An unsure second face (a reflection, a pattern) never raises the guard.
            if (faces[index].Score < SecondaryScore) continue;
            var candidate = matches[index];
            if (candidate == null)
            {
                _candidates.Add(new Candidate(faces[index], at));
                counted.Add(faces[index]);
                continue;
            }
            candidate.LastSeen = at;
            if (!candidate.Background && at - candidate.FirstSeen >= BackgroundAfter)
            {
                candidate.Background = true;
                becameBackground = true;
            }
            if (!candidate.Background) counted.Add(faces[index]);
        }
        // A poster that flickers out of detection is remembered, so it does not re-arm the guard.
        _candidates.RemoveAll(candidate =>
            at - candidate.LastSeen > (candidate.Background ? BackgroundForgotten : CandidateLost));
        return counted;
    }

    private static float Area(DetectedFace face) => face.Width * face.Height;

    /// <summary>How far a face moved from a reference, in units of the reference's size (size change counts too).</summary>
    private static float Shift(DetectedFace face, DetectedFace reference)
    {
        float size = Math.Max(reference.Width, 1e-4f);
        float centre = MathF.Sqrt(MathF.Pow((face.CenterX - reference.CenterX) / size, 2) +
                                  MathF.Pow((face.CenterY - reference.CenterY) / Math.Max(reference.Height, 1e-4f), 2));
        return Math.Max(centre, Math.Abs(face.Width / size - 1));
    }

    private sealed class Candidate(DetectedFace first, DateTimeOffset seen)
    {
        public DetectedFace First { get; } = first;
        public DateTimeOffset FirstSeen { get; } = seen;
        public DateTimeOffset LastSeen { get; set; } = seen;
        public bool Background { get; set; }
    }
}
