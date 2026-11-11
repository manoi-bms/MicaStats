using System;

namespace MicaStats.Pi.Telemetry;

public enum Availability { NoDataYet, Value, Unavailable }

/// <summary>
/// A fixed-capacity ring of recent samples for one metric.
///
/// Semantics ported from the Windows app's Services/MetricsHistory.cs: indexing
/// is oldest-to-newest so a renderer walks left to right without knowing where
/// the buffer wraps; capacity is allocated once so steady-state sampling never
/// allocates; an unreadable sensor is never drawn as a flat zero line.
/// </summary>
public sealed class Series
{
    private readonly float[] _buffer;
    private int _head;      // next write position
    private int _count;
    private bool _sawValue;
    private bool _everUnavailable;

    /// <summary>Rolling peak for autoscaled series, decayed so one spike does not flatten the graph forever.</summary>
    private float _peak;

    /// <summary>Fraction of the peak retained per sample when the current value is lower.</summary>
    private const float PeakDecay = 0.98f;

    public Series(int capacity, float peakFloor = 0f)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new float[capacity];
        PeakFloor = peakFloor;
        _peak = peakFloor;
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    /// <summary>Lower bound for the autoscale peak, so an idle series is not amplified to full scale.</summary>
    public float PeakFloor { get; }

    /// <summary>Current autoscale ceiling: the decayed maximum, never below <see cref="PeakFloor"/>.</summary>
    public float Peak => Math.Max(PeakFloor, _peak);

    public Availability Availability
    {
        get
        {
            if (_sawValue) return Availability.Value;
            return _everUnavailable ? Availability.Unavailable : Availability.NoDataYet;
        }
    }

    /// <summary>The most recent sample, or 0 when empty.</summary>
    public float Latest => _count == 0 ? 0f : this[_count - 1];

    /// <summary>Oldest-to-newest access: index 0 is the oldest held sample.</summary>
    public float this[int index]
    {
        get
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            int pos = _head - _count + index;
            if (pos < 0) pos += Capacity;
            return _buffer[pos];
        }
    }

    public void Add(float value)
    {
        _buffer[_head] = value;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        _sawValue = true;
        _peak = value >= _peak ? value : Math.Max(PeakFloor, _peak * PeakDecay);
    }

    /// <summary>Record that the source could not be read. Deliberately adds no fake sample.</summary>
    public void MarkUnavailable() => _everUnavailable = true;
}
