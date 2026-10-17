using System;
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanPresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    private static readonly DetectedFace Owner = new(0.5f, 0.45f, 0.3f, 0.4f, 0.95f);
    private static readonly DetectedFace Behind = new(0.8f, 0.3f, 0.1f, 0.14f, 0.9f);
    private static readonly DetectedFace[] None = [];

    private readonly PresenceTracker _tracker;
    private readonly List<PresenceEvent> _events = [];
    private DateTimeOffset _now = T0;

    public StackChanPresenceTrackerTests() : this(PresenceOptions.Default) { }

    private StackChanPresenceTrackerTests(PresenceOptions options) => _tracker = new PresenceTracker(options);

    private static StackChanPresenceTrackerTests With(PresenceOptions options) => new(options);

    private void Feed(double seconds, params DetectedFace[] faces)
    {
        // Frames every 250 ms (4 fps) for the given duration.
        var end = _now.AddSeconds(seconds);
        while (_now < end)
        {
            _events.AddRange(_tracker.Observe(_now, faces));
            _now = _now.AddMilliseconds(250);
        }
    }

    private void Idle(double seconds)
    {
        var end = _now.AddSeconds(seconds);
        while (_now < end)
        {
            _events.AddRange(_tracker.Tick(_now));
            _now = _now.AddSeconds(1);
        }
    }

    private int Count(PresenceEventKind kind) => _events.Count(e => e.Kind == kind);

    [Fact]
    public void arrives_after_two_of_three_frames()
    {
        _events.AddRange(_tracker.Observe(_now, [Owner]));
        Assert.NotEqual(PresenceState.Present, _tracker.State);
        _events.AddRange(_tracker.Observe(_now.AddMilliseconds(250), None));
        _events.AddRange(_tracker.Observe(_now.AddMilliseconds(500), [Owner]));

        Assert.Equal(PresenceState.Present, _tracker.State);
        Assert.Equal(1, Count(PresenceEventKind.Arrived));
        Assert.Equal(Owner, _tracker.MainFace);
    }

    [Fact]
    public void dozes_after_10s_without_face()
    {
        Feed(2, Owner);
        Feed(9.5, None);
        Assert.Equal(PresenceState.Present, _tracker.State);
        Feed(1, None);

        Assert.Equal(PresenceState.Away, _tracker.State);
        Assert.Equal(1, Count(PresenceEventKind.Left));
    }

    [Fact]
    public void welcome_only_after_two_minutes_away()
    {
        Feed(2, Owner);
        Feed(60, None);
        Feed(2, Owner);
        Assert.Equal(0, Count(PresenceEventKind.Welcome));

        Feed(125, None);
        Feed(2, Owner);
        var welcome = Assert.Single(_events, e => e.Kind == PresenceEventKind.Welcome);
        Assert.True(welcome.AwayFor >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void no_frames_is_unknown_not_away()
    {
        var test = With(PresenceOptions.Default with { LockAfter = TimeSpan.FromSeconds(30) });
        test.Feed(2, Owner);
        test.Idle(60);

        Assert.Equal(PresenceState.Unknown, test._tracker.State);
        Assert.Equal(0, test.Count(PresenceEventKind.Left));
        Assert.Equal(0, test.Count(PresenceEventKind.LockDue));
    }

    [Fact]
    public void covered_or_dark_frames_are_unknown_not_away()
    {
        var test = With(PresenceOptions.Default with { LockAfter = TimeSpan.FromSeconds(30) });
        test.Feed(2, Owner);
        var end = test._now.AddSeconds(60);
        while (test._now < end)
        {
            test._events.AddRange(test._tracker.Observe(test._now, None, usable: false));
            test._events.AddRange(test._tracker.Tick(test._now));
            test._now = test._now.AddMilliseconds(250);
        }

        Assert.Equal(PresenceState.Unknown, test._tracker.State);
        Assert.Equal(0, test.Count(PresenceEventKind.Left));
        Assert.Equal(0, test.Count(PresenceEventKind.LockDue));
    }

    [Fact]
    public void onlooker_ends_when_the_stream_goes_unknown()
    {
        Feed(2, Owner);
        Feed(2, Owner, Behind);
        Assert.True(_tracker.OnlookerActive);

        Idle(6);

        Assert.False(_tracker.OnlookerActive);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerEnded));
    }

    [Fact]
    public void jittering_poster_still_becomes_background()
    {
        Feed(2, Owner);
        var end = _now.AddSeconds(31);
        int frame = 0;
        while (_now < end)
        {
            // About one pixel of detector jitter on a small QVGA face.
            float jitter = (frame++ % 2 == 0 ? 1 : -1) * 0.003f;
            _events.AddRange(_tracker.Observe(_now, [Owner, Behind with { CenterX = Behind.CenterX + jitter, CenterY = Behind.CenterY - jitter }]));
            _now = _now.AddMilliseconds(250);
        }

        Assert.False(_tracker.OnlookerActive);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerStarted));
    }

    [Fact]
    public void flickering_poster_stays_background_when_it_returns()
    {
        Feed(2, Owner);
        Feed(31, Owner, Behind);
        Feed(5, Owner);
        Feed(5, Owner, Behind);

        Assert.Equal(1, Count(PresenceEventKind.OnlookerStarted));
        Assert.False(_tracker.OnlookerActive);
    }

    [Fact]
    public void low_confidence_second_face_is_ignored()
    {
        Feed(2, Owner);
        Feed(3, Owner, Behind with { Score = 0.7f });

        Assert.Equal(0, Count(PresenceEventKind.OnlookerStarted));
    }

    [Fact]
    public void onlooker_needs_1_5s_of_two_faces()
    {
        Feed(2, Owner);
        Feed(1.25, Owner, Behind);
        Assert.False(_tracker.OnlookerActive);
        Feed(0.5, Owner, Behind);

        Assert.True(_tracker.OnlookerActive);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerStarted));
    }

    [Fact]
    public void brief_passerby_under_1_5s_is_ignored()
    {
        Feed(2, Owner);
        Feed(1, Owner, Behind);
        Feed(3, Owner);

        Assert.Equal(0, Count(PresenceEventKind.OnlookerStarted));
    }

    [Fact]
    public void onlooker_clears_after_2s_single_face()
    {
        Feed(2, Owner);
        Feed(2, Owner, Behind);
        Feed(1.75, Owner);
        Assert.True(_tracker.OnlookerActive);
        Feed(0.5, Owner);

        Assert.False(_tracker.OnlookerActive);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerEnded));
    }

    [Fact]
    public void static_second_face_is_background_after_30s()
    {
        Feed(2, Owner);
        Feed(31, Owner, Behind);

        Assert.False(_tracker.OnlookerActive);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerStarted));
        Assert.Equal(1, Count(PresenceEventKind.OnlookerEnded));

        // It stays ignored until it moves.
        Feed(10, Owner, Behind);
        Assert.Equal(1, Count(PresenceEventKind.OnlookerStarted));
        Feed(2, Owner, Behind with { CenterX = 0.6f });
        Assert.Equal(2, Count(PresenceEventKind.OnlookerStarted));
    }

    [Fact]
    public void break_due_once_after_50min_and_reset_by_5min_away()
    {
        Feed(49 * 60, Owner);
        Assert.Equal(0, Count(PresenceEventKind.BreakDue));
        Feed(61, Owner);
        Assert.Equal(1, Count(PresenceEventKind.BreakDue));
        Feed(10 * 60, Owner);
        Assert.Equal(1, Count(PresenceEventKind.BreakDue));

        Feed(6 * 60, None);
        Feed(49 * 60, Owner);
        Assert.Equal(1, Count(PresenceEventKind.BreakDue));
        Feed(61, Owner);
        Assert.Equal(2, Count(PresenceEventKind.BreakDue));
    }

    [Fact]
    public void break_timer_pauses_while_the_camera_is_unknown()
    {
        Feed(40 * 60, Owner);
        Idle(20 * 60);
        Feed(9 * 60, Owner);
        Assert.Equal(0, Count(PresenceEventKind.BreakDue));

        Feed(2 * 60, Owner);
        Assert.Equal(1, Count(PresenceEventKind.BreakDue));
    }

    [Fact]
    public void break_suppressed_during_meeting()
    {
        _tracker.SetBreakSuppressed(true);
        Feed(51 * 60, Owner);

        Assert.Equal(0, Count(PresenceEventKind.BreakDue));
    }

    [Fact]
    public void lock_due_once_per_away_when_enabled()
    {
        var test = With(PresenceOptions.Default with { LockAfter = TimeSpan.FromMinutes(5) });
        test.Feed(2, Owner);
        test.Feed(4 * 60, None);
        Assert.Equal(0, test.Count(PresenceEventKind.LockDue));
        test.Feed(2 * 60, None);
        Assert.Equal(1, test.Count(PresenceEventKind.LockDue));
        test.Feed(10 * 60, None);
        Assert.Equal(1, test.Count(PresenceEventKind.LockDue));

        test.Feed(2, Owner);
        test.Feed(6 * 60, None);
        Assert.Equal(2, test.Count(PresenceEventKind.LockDue));
    }

    [Fact]
    public void lock_never_due_when_disabled()
    {
        Feed(2, Owner);
        Feed(30 * 60, None);

        Assert.Equal(0, Count(PresenceEventKind.LockDue));
    }
}
