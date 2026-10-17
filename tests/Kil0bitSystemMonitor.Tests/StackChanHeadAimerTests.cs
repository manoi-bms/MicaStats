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
        public double ActualPitch(HeadPose pose) => pitchStuckBelow is { } stuck ? Math.Max(pose.Pitch, stuck) : pose.Pitch;

        public (double X, double Y) Error(HeadPose pose) =>
            (yawSign * (pose.Yaw - faceYaw) / frameDegrees * 2,
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
    public void commands_wait_for_the_servo_to_settle()
    {
        var aimer = new HeadAimer(false, false);
        Assert.NotNull(aimer.Next(T0, FaceAt(0.9f, 0.5f), Centre));
        Assert.Null(aimer.Next(T0.AddMilliseconds(300), FaceAt(0.9f, 0.5f), Centre));
        Assert.NotNull(aimer.Next(T0.AddMilliseconds(650), FaceAt(0.9f, 0.5f), Centre));
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
