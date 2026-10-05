using System;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Optional polling boundary for bounded, non-persistent capture level data.</summary>
internal interface IMeetingAudioMonitor
{
    MeetingAudioSnapshot GetAudioSnapshot(MeetingSource source);
}

/// <summary>Three seconds of chronological 50 ms peaks plus the latest short-term level.</summary>
internal sealed record MeetingAudioSnapshot(float[] Peaks, float Peak, TimeSpan? Age);

/// <summary>Stores a fixed three-second window of capture peaks without retaining audio.</summary>
internal sealed class MeetingAudioMonitor
{
    internal const int BinCount = 60;
    private const long BinTicks = TimeSpan.TicksPerMillisecond * 50;
    private const int RecentBinCount = 5;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly float[] _peaks = new float[BinCount];
    private readonly long[] _slots = new long[BinCount];
    private long _originTimestamp;
    private long? _lastFrameTimestamp;

    internal MeetingAudioMonitor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Array.Fill(_slots, long.MinValue);
        _originTimestamp = _timeProvider.GetTimestamp();
    }

    internal void RecordPeak(float peak, TimeSpan duration = default)
    {
        peak = float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f;
        long timestamp = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            long currentSlot = GetSlot(timestamp);
            // One callback can contain several display intervals. Its peak describes that
            // captured duration; treating it as an instant would draw artificial silent gaps.
            int bins = (int)Math.Clamp(Math.Ceiling((double)duration.Ticks / BinTicks), 1, BinCount);
            for (long slot = currentSlot - bins + 1; slot <= currentSlot; slot++)
            {
                int index = GetIndex(slot);
                if (_slots[index] != slot)
                {
                    _slots[index] = slot;
                    _peaks[index] = peak;
                }
                else if (peak > _peaks[index])
                {
                    _peaks[index] = peak;
                }
            }

            _lastFrameTimestamp = timestamp;
        }
    }

    internal MeetingAudioSnapshot GetSnapshot()
    {
        long now = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            var chronological = new float[BinCount];
            long currentSlot = GetSlot(now);
            long firstSlot = currentSlot - BinCount + 1;
            for (int offset = 0; offset < BinCount; offset++)
            {
                long slot = firstSlot + offset;
                int index = GetIndex(slot);
                if (_slots[index] == slot)
                    chronological[offset] = _peaks[index];
            }

            float recentPeak = 0f;
            for (int offset = BinCount - RecentBinCount; offset < BinCount; offset++)
                recentPeak = Math.Max(recentPeak, chronological[offset]);

            TimeSpan? age = _lastFrameTimestamp is long last
                ? NonNegative(_timeProvider.GetElapsedTime(last, now))
                : null;
            return new MeetingAudioSnapshot(chronological, recentPeak, age);
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_peaks);
            Array.Fill(_slots, long.MinValue);
            _originTimestamp = _timeProvider.GetTimestamp();
            _lastFrameTimestamp = null;
        }
    }

    private long GetSlot(long timestamp) =>
        _timeProvider.GetElapsedTime(_originTimestamp, timestamp).Ticks / BinTicks;

    private static int GetIndex(long slot) => (int)((slot % BinCount + BinCount) % BinCount);

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
