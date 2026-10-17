using System;
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanHeadAimerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    private static readonly HeadPose Centre = new(0, 30);

    private static DetectedFace FaceAt(float x, float y) => new(x, y, 0.2f, 0.25f, 0.9f);

    /// <summary>
    /// A desk camera on the robot's head: the face appears where the head points relative to the
    /// person. Signs model mirrored cameras or servo wiring; a stuck servo stops at its limit.
    /// </summary>
    private sealed class Camera(double faceYaw, double facePitch, int yawSign = 1, int pitchSign = 1,
        double frameDegrees = 50, double? pitchStuckBelow = null)
    {
        public double FaceYaw { get; set; } = faceYaw;

        public double ActualPitch(HeadPose pose) => pitchStuckBelow is { } stuck ? Math.Max(pose.Pitch, stuck) : pose.Pitch;

        public (double X, double Y) Error(HeadPose pose) =>
            (yawSign * (pose.Yaw - FaceYaw) / frameDegrees * 2,
             pitchSign * (ActualPitch(pose) - facePitch) / (frameDegrees * 0.75) * 2);

        public DetectedFace? See(HeadPose pose)
        {
            var (x, y) = Error(pose);
            if (Math.Abs(x) > 1 || Math.Abs(y) > 1) return null;
            return FaceAt((float)(0.5 + x / 2), (float)(0.5 + y / 2));
        }
    }

    private static (HeadPose Pose, List<HeadPose> Commands) Track(HeadAimer aimer, Camera camera, HeadPose start, double seconds)
    {
        var pose = start;
        var commands = new List<HeadPose>();
        for (var at = T0; at < T0.AddSeconds(seconds); at = at.AddMilliseconds(250))
        {
            if (aimer.Next(at, camera.See(pose), pose) is { } next)
            {
                commands.Add(next);
                pose = next;
            }
        }
        return (pose, commands);
    }

    [Fact]
    public void centred_face_inside_deadzone_sends_nothing() =>
        Assert.Null(new HeadAimer(false, false).Next(T0, FaceAt(0.52f, 0.48f), Centre));

    [Fact]
    public void centres_a_face_low_in_the_picture_by_tilting_down()
    {
        var camera = new Camera(faceYaw: -8, facePitch: 10);
        var (pose, _) = Track(new HeadAimer(false, false), camera, new HeadPose(0, 45), 30);

        var (x, y) = camera.Error(pose);
        Assert.InRange(x, -0.12, 0.12);
        Assert.InRange(y, -0.12, 0.12);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, -1)]
    public void learns_a_reversed_axis_from_how_the_face_moves(int yawSign, int pitchSign)
    {
        var camera = new Camera(faceYaw: 12, facePitch: 30, yawSign, pitchSign);
        var aimer = new HeadAimer(false, false);
        var (pose, _) = Track(aimer, camera, new HeadPose(0, 40), 40);

        var (x, y) = camera.Error(pose);
        Assert.InRange(x, -0.12, 0.12);
        Assert.InRange(y, -0.12, 0.12);
        Assert.Equal(yawSign < 0, aimer.InvertYaw);
        Assert.Equal(pitchSign < 0, aimer.InvertPitch);
        Assert.True(aimer.Learned);
    }

    [Fact]
    public void stops_pushing_a_servo_that_cannot_move_further()
    {
        // Visible at the stuck angle (30) but centring it would need pitch 18, below the stuck servo.
        var camera = new Camera(faceYaw: 5, facePitch: 18, pitchStuckBelow: 30);
        var (pose, commands) = Track(new HeadAimer(false, false), camera, new HeadPose(0, 45), 60);

        // Downward pitch pushes in the second half: after learning the limit there should be almost none.
        int pushes = Enumerable.Range(commands.Count / 2, commands.Count - commands.Count / 2)
            .Count(index => index > 0 && commands[index].Pitch < commands[index - 1].Pitch - 0.5);
        Assert.True(pushes <= 2, $"kept pushing pitch past the stuck servo ({pushes} pushes)");
        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
    }

    /// <summary>
    /// Like the robot on the desk: the servo turns slowly toward each command and every picture
    /// shows the head as it was a moment earlier, with a little detector jitter.
    /// </summary>
    private static HeadPose TrackSlowServo(HeadAimer aimer, Camera camera, HeadPose start, double seconds,
        double degreesPerSecond, int latencyMs, bool frozenWhileMoving = false)
    {
        var actual = start;
        var target = start;
        var history = new List<(DateTimeOffset At, HeadPose Pose)>();
        var still = start;
        var random = new Random(7);
        static double Toward(double from, double to, double limit) => from + Math.Clamp(to - from, -limit, limit);
        for (var at = T0; at < T0.AddSeconds(seconds); at = at.AddMilliseconds(250))
        {
            double limit = degreesPerSecond * 0.25;
            actual = new HeadPose(Toward(actual.Yaw, target.Yaw, limit), Toward(actual.Pitch, target.Pitch, limit));
            history.Add((at, actual));
            var seen = history.LastOrDefault(entry => entry.At <= at.AddMilliseconds(-latencyMs)).Pose ?? start;
            // The robot sends no new picture while its servo turns: the last still one repeats.
            if (frozenWhileMoving) seen = still = actual == target ? seen : still;
            var face = camera.See(seen);
            if (face != null)
                face = face with { CenterX = face.CenterX + (float)((random.NextDouble() - 0.5) * 0.02) };
            if (aimer.Next(at, face, target) is { } next) target = next;
        }
        return actual;
    }

    [Theory]
    [InlineData(8, 500, false)]
    [InlineData(7, 300, true)] // Measured: about 7°/s at tracking speed; the picture stays still until the move ends.
    public void centres_a_face_when_the_servo_is_slow_and_pictures_lag(double degreesPerSecond, int latencyMs, bool frozenWhileMoving)
    {
        // Measured on the robot: 20° of yaw moves the face a quarter of the picture.
        var camera = new Camera(faceYaw: 20, facePitch: 10, frameDegrees: 80);
        var aimer = new HeadAimer(false, false);
        var pose = TrackSlowServo(aimer, camera, new HeadPose(0, 10), 40, degreesPerSecond, latencyMs, frozenWhileMoving);

        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
        Assert.False(aimer.InvertYaw);
    }

    [Fact]
    public void centres_a_face_in_a_few_moves_with_the_robots_camera()
    {
        // Measured: 20° of yaw moves the face 26% of the picture width.
        var camera = new Camera(faceYaw: 25, facePitch: 30, frameDegrees: 77);
        var (pose, commands) = Track(new HeadAimer(false, false), camera, new HeadPose(0, 30), 20);

        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
        Assert.True(commands.Count <= 3, $"took {commands.Count} moves");
    }

    [Fact]
    public void small_jitter_around_the_centre_does_not_move_the_head()
    {
        var aimer = new HeadAimer(false, false);
        Assert.Null(aimer.Next(T0, FaceAt(0.535f, 0.47f), Centre));
        Assert.Null(aimer.Next(T0.AddSeconds(1), FaceAt(0.465f, 0.535f), Centre));
    }

    [Fact]
    public void once_correcting_it_finishes_close_to_the_centre()
    {
        // Started far off, the head keeps going until the face is within a few percent of the centre.
        var camera = new Camera(faceYaw: 12, facePitch: 30, frameDegrees: 77);
        var (pose, _) = Track(new HeadAimer(false, false), camera, new HeadPose(0, 30), 20);

        Assert.InRange(camera.Error(pose).X, -0.05, 0.05);
    }

    [Fact]
    public void keeps_a_walking_person_in_the_picture_and_centres_when_they_stop()
    {
        // Measured robot timing: the servo turns about 40°/s and pictures show it about 0.5 s later.
        var camera = new Camera(faceYaw: -20, facePitch: 30, frameDegrees: 77);
        var aimer = new HeadAimer(false, false);
        var actual = new HeadPose(-20, 30);
        var target = actual;
        var history = new List<(DateTimeOffset At, HeadPose Pose)>();
        double worst = 0;
        for (var at = T0; at < T0.AddSeconds(20); at = at.AddMilliseconds(250))
        {
            double elapsed = (at - T0).TotalSeconds;
            camera.FaceYaw = Math.Clamp(-20 + 4 * elapsed, -20, 20); // Walks 40° in 10 s, then stops.
            double step = 40 * 0.25;
            actual = new HeadPose(actual.Yaw + Math.Clamp(target.Yaw - actual.Yaw, -step, step), target.Pitch);
            history.Add((at, actual));
            var seen = history.LastOrDefault(entry => entry.At <= at.AddMilliseconds(-500)).Pose ?? actual;
            var face = camera.See(seen);
            if (elapsed is > 2 and < 10) worst = Math.Max(worst, Math.Abs(camera.Error(actual).X));
            if (aimer.Next(at, face, target) is { } next) target = next;
        }

        Assert.True(worst < 0.5, $"fell {worst:0.00} behind while the person walked");
        Assert.InRange(camera.Error(actual).X, -0.12, 0.12);
    }

    [Fact]
    public void search_looks_first_where_the_face_was_last_seen_and_at_its_tilt()
    {
        var aimer = new HeadAimer(false, false);
        var seenAt = new HeadPose(10, 8); // A low desk camera: the face shows only at a low tilt.
        Assert.Null(aimer.Next(T0, FaceAt(0.5f, 0.5f), seenAt));
        var poses = new List<HeadPose>();
        for (var at = T0.AddSeconds(4); at < T0.AddSeconds(30); at = at.AddMilliseconds(250))
            if (aimer.Next(at, null, new HeadPose(-40, 25)) is { } pose) poses.Add(pose);

        Assert.Equal(seenAt, poses[0]);
        Assert.Contains(poses, pose => pose.Pitch == 8 && pose.Yaw <= -20);
        Assert.Contains(poses, pose => pose.Pitch == 8 && pose.Yaw >= 20);
    }

    [Fact]
    public void a_glance_away_from_the_centre_holds_the_head_still()
    {
        var aimer = new HeadAimer(false, false);
        Assert.Null(aimer.Next(T0, FaceAt(0.5f, 0.5f), Centre));
        for (var at = T0.AddSeconds(0.25); at < T0.AddSeconds(15); at = at.AddMilliseconds(250))
            Assert.Null(aimer.Next(at, null, Centre, present: true));
    }

    [Fact]
    public void returns_to_where_the_face_was_after_the_head_was_moved_away()
    {
        var aimer = new HeadAimer(false, false);
        Assert.Null(aimer.Next(T0, FaceAt(0.5f, 0.5f), Centre));
        var pushed = new HeadPose(-30, 15);
        HeadPose? back = null;
        for (var at = T0.AddSeconds(0.25); back == null && at < T0.AddSeconds(5); at = at.AddMilliseconds(250))
            back = aimer.Next(at, null, pushed, present: true);

        Assert.Equal(Centre, back);
    }

    [Fact]
    public void turns_toward_the_edge_a_walking_person_left_by()
    {
        // A narrow picture, so the walk takes the face fully out of view.
        var camera = new Camera(faceYaw: 10, facePitch: 30, frameDegrees: 40);
        var aimer = new HeadAimer(false, false);
        var pose = new HeadPose(0, 30);
        var at = T0;
        bool lost = false;
        void Run(double seconds, double walkPerSecond)
        {
            for (var end = at.AddSeconds(seconds); at < end; at = at.AddMilliseconds(250))
            {
                camera.FaceYaw = Math.Min(camera.FaceYaw + walkPerSecond * 0.25, 45);
                var face = camera.See(pose);
                lost |= face == null;
                if (aimer.Next(at, face, pose, present: true) is { } next) pose = next;
            }
        }
        Run(15, 0); // Learns the direction while centring.
        Run(3, 25); // Walks out of the picture faster than the head follows.
        Run(10, 0);

        Assert.True(lost, "the walk should take the face out of the picture");
        Assert.True(camera.See(pose) != null, $"head at {pose}, inverted {aimer.InvertYaw}, face at yaw {camera.FaceYaw}");
        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
    }

    [Fact]
    public void a_move_that_was_never_sent_is_not_learned_as_a_limit()
    {
        // The robot link was busy, so these moves were dropped; the face cannot have moved.
        var camera = new Camera(faceYaw: 15, facePitch: 30, frameDegrees: 80);
        var aimer = new HeadAimer(false, false);
        var pose = new HeadPose(0, 30);
        var at = T0;
        for (; at < T0.AddSeconds(10); at = at.AddMilliseconds(250))
            if (aimer.Next(at, camera.See(pose), pose) != null) aimer.Dropped();

        for (; at < T0.AddSeconds(25); at = at.AddMilliseconds(250))
            if (aimer.Next(at, camera.See(pose), pose) is { } next) pose = next;

        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
    }

    [Fact]
    public void a_dropped_move_can_be_retried_at_once()
    {
        var aimer = new HeadAimer(false, false);
        Assert.NotNull(aimer.Next(T0, FaceAt(0.2f, 0.5f), Centre));
        aimer.Dropped();
        Assert.NotNull(aimer.Next(T0.AddMilliseconds(250), FaceAt(0.2f, 0.5f), Centre));
    }

    [Fact]
    public void takes_bigger_steps_when_the_face_barely_moves_per_degree()
    {
        var camera = new Camera(faceYaw: 35, facePitch: 30, frameDegrees: 160);
        var (pose, _) = Track(new HeadAimer(false, false), camera, new HeadPose(0, 30), 30);

        Assert.InRange(camera.Error(pose).X, -0.12, 0.12);
    }

    [Fact]
    public void pose_is_clamped_to_safe_ranges()
    {
        var pose = new HeadAimer(false, false).Next(T0, FaceAt(0f, 0f), new HeadPose(40, 84));
        Assert.NotNull(pose);
        Assert.InRange(pose!.Yaw, -45, 45);
        Assert.InRange(pose.Pitch, 5, 85);

        pose = new HeadAimer(false, false).Next(T0, FaceAt(1f, 1f), new HeadPose(-40, 6));
        Assert.NotNull(pose);
        Assert.InRange(pose!.Yaw, -45, 45);
        Assert.InRange(pose.Pitch, 5, 85);
    }

    [Fact]
    public void commands_wait_for_the_servo_to_settle_and_the_picture_to_show_it()
    {
        var aimer = new HeadAimer(false, false);
        Assert.NotNull(aimer.Next(T0, FaceAt(0.9f, 0.5f), Centre));
        Assert.Null(aimer.Next(T0.AddMilliseconds(300), FaceAt(0.9f, 0.5f), Centre));
        // An unchanged picture may be an old one: keep waiting rather than call it a limit.
        Assert.Null(aimer.Next(T0.AddMilliseconds(650), FaceAt(0.9f, 0.5f), Centre));
        Assert.Null(aimer.Next(T0.AddMilliseconds(900), FaceAt(0.7f, 0.5f), Centre));
        Assert.NotNull(aimer.Next(T0.AddMilliseconds(1150), FaceAt(0.7f, 0.5f), Centre));
    }

    [Fact]
    public void search_starts_after_3s_and_visits_ten_poses()
    {
        var aimer = new HeadAimer(false, false);
        var poses = Run(aimer, T0, TimeSpan.FromSeconds(20), out var firstAt);

        Assert.Equal(T0.AddSeconds(3), firstAt);
        HeadPose[] expected =
        [
            new(-40, 25), new(-20, 25), new(0, 25), new(20, 25), new(40, 25),
            new(-40, 40), new(-20, 40), new(0, 40), new(20, 40), new(40, 40), HeadAimer.Rest
        ];
        Assert.Equal(expected, poses);
    }

    [Fact]
    public void failed_search_rests_then_waits_120s()
    {
        var aimer = new HeadAimer(false, false);
        Run(aimer, T0, TimeSpan.FromSeconds(20), out _);

        Assert.Empty(Run(aimer, T0.AddSeconds(20), TimeSpan.FromSeconds(100), out _));
        Assert.Equal(new HeadPose(-40, 25), Run(aimer, T0.AddSeconds(120), TimeSpan.FromSeconds(20), out _)[0]);
    }

    [Fact]
    public void face_during_search_stops_search()
    {
        var aimer = new HeadAimer(false, false);
        Run(aimer, T0, TimeSpan.FromSeconds(4), out _);

        Assert.Null(aimer.Next(T0.AddSeconds(4.5), FaceAt(0.5f, 0.5f), Centre));
        Assert.Empty(Run(aimer, T0.AddSeconds(4.6), TimeSpan.FromSeconds(2.5), out _));
    }

    private static List<HeadPose> Run(HeadAimer aimer, DateTimeOffset from, TimeSpan duration, out DateTimeOffset? firstAt)
    {
        var poses = new List<HeadPose>();
        firstAt = null;
        for (var at = from; at < from + duration; at = at.AddMilliseconds(100))
        {
            if (aimer.Next(at, null, Centre) is { } pose)
            {
                firstAt ??= at;
                poses.Add(pose);
            }
        }
        return poses;
    }
}
