using System;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

public sealed record HeadPose(double Yaw, double Pitch);

/// <summary>
/// Keeps the main face in the centre of the camera picture, or sweeps to find one.
/// It calibrates itself while it works: after each move it checks how the face moved in the
/// picture. A face that moved the wrong way flips that axis; a face that did not move at all
/// means the servo reached its physical limit, so it stops pushing that way; a weak or strong
/// response resizes the next step. Every pose stays inside yaw ±45° and pitch 5–85°.
/// </summary>
public sealed class HeadAimer
{
    public static readonly HeadPose Rest = new(0, 25);

    private const double DeadZone = 0.06;
    // Hysteresis: start correcting beyond 5% of the picture from the centre, stop within 2.5%,
    // so detector jitter around the centre never makes the head hunt.
    private const double StartBeyond = 0.10, StopWithin = 0.05;
    private const double MaxStep = 12, MinimumChange = 1;
    private const double YawMin = -45, YawMax = 45, PitchMin = 5, PitchMax = 85;
    private const double Noticeable = 0.02, Steady = 0.08;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SearchAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ForgetLimit = TimeSpan.FromMinutes(1);
    private static readonly HeadPose[] Sweep =
    [
        new(-40, 25), new(-20, 25), new(0, 25), new(20, 25), new(40, 25),
        new(-40, 40), new(-20, 40), new(0, 40), new(20, 40), new(40, 40)
    ];

    private readonly Axis _yaw, _pitch;
    private DateTimeOffset? _lastCommand, _lastFace, _stepStarted, _retryAt;
    private Move? _pending;
    private (double X, double Y)? _seen;
    private int _step = -1;

    public HeadAimer(bool invertYaw, bool invertPitch)
    {
        _yaw = new Axis(invertYaw ? -1 : 1, gain: 15, YawMin, YawMax);
        _pitch = new Axis(invertPitch ? -1 : 1, gain: 12, PitchMin, PitchMax);
    }

    /// <summary>What the aimer has learned so far; presence saves it so the next start begins right.</summary>
    public bool InvertYaw => _yaw.Sign < 0;
    public bool InvertPitch => _pitch.Sign < 0;
    public bool Learned => _yaw.Confirmed && _pitch.Confirmed;

    public HeadPose? Next(DateTimeOffset at, DetectedFace? face, HeadPose current)
    {
        if (_lastCommand is { } last && at - last < Settle) return null;
        var pose = face != null ? Follow(at, face, current) : Search(at);
        if (pose != null) _lastCommand = at;
        return pose;
    }

    /// <summary>The last pose from <see cref="Next"/> never reached the robot: forget it, so a still picture is not read as a limit.</summary>
    public void Dropped()
    {
        _pending = null;
        _seen = null;
        _lastCommand = null;
    }

    private HeadPose? Follow(DateTimeOffset at, DetectedFace face, HeadPose current)
    {
        _lastFace = at;
        _step = -1;
        _retryAt = null;
        // Offset from the picture centre, -1..1 on each axis.
        double ex = (face.CenterX - 0.5) / 0.5, ey = (face.CenterY - 0.5) / 0.5;

        if (_pending is { } move)
        {
            // The servo turns slowly and the first pictures after a move can be old ones: judge the
            // move once the picture shows it and has stopped changing; call it a limit only if no
            // change comes at all.
            bool steady = _seen is { } seen && Math.Abs(ex - seen.X) < Steady && Math.Abs(ey - seen.Y) < Steady;
            _seen = (ex, ey);
            bool unclear = Unchanged(move.Yaw, move.Ex, ex) || Unchanged(move.Pitch, move.Ey, ey);
            if ((unclear || !steady) && _lastCommand is { } moved && at - moved < Confirm) return null;
            _pending = null;
            _seen = null;
            _yaw.Learn(at, move.Ex, ex, move.Yaw, move.YawBefore);
            _pitch.Learn(at, move.Ey, ey, move.Pitch, move.PitchBefore);
        }

        double yaw = _yaw.Target(at, current.Yaw, _yaw.Off(ex));
        double pitch = _pitch.Target(at, current.Pitch, _pitch.Off(ey));
        double dYaw = yaw - current.Yaw, dPitch = pitch - current.Pitch;
        if (Math.Abs(dYaw) < MinimumChange && Math.Abs(dPitch) < MinimumChange) return null;
        _pending = new Move(ex, ey, dYaw, dPitch, current.Yaw, current.Pitch);
        return new HeadPose(yaw, pitch);
    }

    private static bool Unchanged(double moved, double before, double after) =>
        Math.Abs(moved) >= MinimumChange && Math.Abs(before) >= DeadZone && Math.Abs(after - before) < Noticeable;

    private HeadPose? Search(DateTimeOffset at)
    {
        _pending = null;
        _lastFace ??= at;
        if (_step < 0)
        {
            if (_retryAt is { } retry ? at < retry : at - _lastFace < SearchAfter) return null;
            _retryAt = null;
            _step = 0;
            _stepStarted = at;
            return Sweep[0];
        }
        if (at - _stepStarted < Dwell) return null;
        _step++;
        _stepStarted = at;
        if (_step < Sweep.Length) return Sweep[_step];
        // Nobody found: rest quietly and try again later instead of sweeping forever.
        _step = -1;
        _retryAt = at + RetryAfter;
        return Rest;
    }

    private sealed record Move(double Ex, double Ey, double Yaw, double Pitch, double YawBefore, double PitchBefore);

    /// <summary>One servo axis: which way moves the face, how far per step, and where it stops.</summary>
    private sealed class Axis(int sign, double gain, double min, double max)
    {
        private int _away, _stuck, _good;
        private double? _limitLow, _limitHigh, _snapTo, _stuckFrom;
        private DateTimeOffset _limitAt;

        private bool _correcting;

        public int Sign { get; private set; } = sign;

        /// <summary>The offset to correct, or 0 when it is close enough to the centre.</summary>
        public double Off(double error)
        {
            _correcting = Math.Abs(error) >= (_correcting ? StopWithin : StartBeyond);
            return _correcting ? error : 0;
        }
        public bool Confirmed => _good >= 2;

        /// <summary>Degrees to turn so the face moves to the centre: -gain × offset, the learned way round.</summary>
        public double Target(DateTimeOffset at, double current, double error)
        {
            if ((_limitLow != null || _limitHigh != null) && at - _limitAt > ForgetLimit)
                _limitLow = _limitHigh = null; // Re-probe now and then: the person may have moved.
            if (_snapTo is { } snap)
            {
                _snapTo = null;
                return snap;
            }
            if (error == 0) return current;
            double step = Math.Clamp(-gain * error * Sign, -MaxStep, MaxStep);
            double target = Math.Clamp(current + step, min, max);
            if (step < 0 && _limitLow is { } low) target = Math.Max(target, low);
            if (step > 0 && _limitHigh is { } high) target = Math.Min(target, high);
            return target;
        }

        /// <summary>Compares the offset before and after a move of <paramref name="moved"/> degrees.</summary>
        public void Learn(DateTimeOffset at, double before, double after, double moved, double from)
        {
            if (Math.Abs(moved) < MinimumChange || Math.Abs(before) < DeadZone) return;
            double change = after - before;
            if (Math.Abs(change) < Noticeable)
            {
                _away = 0;
                if (_stuck++ == 0) _stuckFrom = from;
                if (_stuck < 2) return;
                // The servo was told to move twice and the picture did not change: it is at its limit.
                // Go back to where the first of those moves started and stop pushing that way for a while.
                _stuck = 0;
                double limit = _stuckFrom ?? from;
                if (moved < 0) _limitLow = limit; else _limitHigh = limit;
                _limitAt = at;
                _snapTo = limit;
                return;
            }
            _stuck = 0;
            if (Math.Sign(change) == Math.Sign(before))
            {
                // A reversed axis pushes the face away by about as much as the move should have
                // brought it back; a smaller drift is a person walking faster than one step.
                double expected = Math.Abs(moved) * 0.8 / gain;
                if (Math.Abs(change) < expected / 2) return;
                // The face moved further from the centre: this axis is wired the other way round.
                if (++_away < 2) return;
                _away = 0;
                _good = 0;
                Sign = -Sign;
                _limitLow = _limitHigh = null;
                return;
            }
            _away = 0;
            _good++;
            if (moved < 0 && _limitLow is { } low && from + moved < low) _limitLow = null;
            if (moved > 0 && _limitHigh is { } high && from + moved > high) _limitHigh = null;
            // Error units per degree, measured; aim for 80% of the remaining offset next time.
            double perDegree = Math.Abs(change) / Math.Abs(moved);
            gain = Math.Clamp((gain + 0.8 / perDegree) / 2, 4, 60);
        }
    }
}
