using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Conference;
using NAudio.Wave;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingAudioMonitorTests
{
    [Fact]
    public void Callback_duration_covers_its_captured_interval_without_artificial_silent_bins()
    {
        var clock = new ManualTimeProvider();
        var monitor = new MeetingAudioMonitor(clock);
        monitor.RecordPeak(0.4f, TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        monitor.RecordPeak(0.8f, TimeSpan.FromMilliseconds(100));

        var snapshot = monitor.GetSnapshot();
        Assert.Equal(new[] { 0.4f, 0.4f, 0.8f, 0.8f }, snapshot.Peaks[^4..]);
        clock.Advance(TimeSpan.FromSeconds(4));
        monitor.RecordPeak(0.2f, TimeSpan.FromMilliseconds(100));
        snapshot = monitor.GetSnapshot();
        Assert.All(snapshot.Peaks[..^2], peak => Assert.Equal(0f, peak));
        Assert.Equal(new[] { 0.2f, 0.2f }, snapshot.Peaks[^2..]);
    }

    [Fact]
    public void Snapshot_is_chronological_bounded_and_expires_old_bins()
    {
        var clock = new ManualTimeProvider();
        var monitor = new MeetingAudioMonitor(clock);

        MeetingAudioSnapshot empty = monitor.GetSnapshot();
        Assert.Null(empty.Age);
        Assert.Equal(MeetingAudioMonitor.BinCount, empty.Peaks.Length);
        Assert.All(empty.Peaks, peak => Assert.Equal(0f, peak));

        monitor.RecordPeak(0.25f);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        monitor.RecordPeak(0.8f);

        MeetingAudioSnapshot active = monitor.GetSnapshot();
        Assert.Equal(0.25f, active.Peaks[^2]);
        Assert.Equal(0.8f, active.Peaks[^1]);
        Assert.Equal(0.8f, active.Peak);
        Assert.Equal(TimeSpan.Zero, active.Age);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        MeetingAudioSnapshot noLongerRecent = monitor.GetSnapshot();
        Assert.Equal(0f, noLongerRecent.Peak);
        Assert.Equal(TimeSpan.FromMilliseconds(250), noLongerRecent.Age);

        clock.Advance(TimeSpan.FromSeconds(3));
        MeetingAudioSnapshot expired = monitor.GetSnapshot();
        Assert.All(expired.Peaks, peak => Assert.Equal(0f, peak));
        Assert.Equal(0f, expired.Peak);
        Assert.Equal(TimeSpan.FromMilliseconds(3_250), expired.Age);
    }

    [Fact]
    public void Reset_removes_history_and_last_frame_age()
    {
        var clock = new ManualTimeProvider();
        var monitor = new MeetingAudioMonitor(clock);
        monitor.RecordPeak(0.75f);

        monitor.Reset();

        MeetingAudioSnapshot snapshot = monitor.GetSnapshot();
        Assert.Null(snapshot.Age);
        Assert.Equal(0f, snapshot.Peak);
        Assert.All(snapshot.Peaks, peak => Assert.Equal(0f, peak));
    }

    [Fact]
    public void Pcm_adapter_reports_strongest_channel_before_mono_downmix()
    {
        var peaks = new List<float>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Microphone,
            new WaveFormat(16_000, 16, 2),
            _ => { },
            (peak, _) => peaks.Add(peak));

        adapter.Append(ToBytes(new short[] { 32_767, -32_768, 16_384, -16_384 }), TimeSpan.Zero);

        float peak = Assert.Single(peaks);
        Assert.Equal(1f, peak);
    }

    [Fact]
    public void Pcm_adapter_clamps_finite_float_levels_and_ignores_nonfinite_samples()
    {
        var peaks = new List<float>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Output,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2),
            _ => { },
            (peak, _) => peaks.Add(peak));

        adapter.Append(ToBytes(new[] { 2f, -0.25f }), TimeSpan.Zero);
        adapter.Append(ToBytes(new[] { float.NaN, float.PositiveInfinity }), TimeSpan.FromMilliseconds(1));

        Assert.Equal(new[] { 1f, 0f }, peaks);
    }

    [Fact]
    public void Monitor_sanitizes_external_peak_values()
    {
        var monitor = new MeetingAudioMonitor(new ManualTimeProvider());

        monitor.RecordPeak(float.NaN);
        monitor.RecordPeak(4f);

        MeetingAudioSnapshot snapshot = monitor.GetSnapshot();
        Assert.Equal(1f, snapshot.Peaks[^1]);
        Assert.Equal(1f, snapshot.Peak);
    }

    private static byte[] ToBytes(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToBytes(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
