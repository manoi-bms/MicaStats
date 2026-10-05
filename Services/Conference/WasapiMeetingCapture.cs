using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Captures a selected microphone and render endpoint as separate, bounded ASR chunks.</summary>
public sealed class WasapiMeetingCapture : IMeetingCapture, IMeetingAudioMonitor
{
    private const string CaptureFault = "Audio capture stopped because a selected device became unavailable.";
    private static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SourceCapture? _microphone;
    private SourceCapture? _output;
    private string? _microphoneId;
    private string? _outputId;
    private Action<MeetingAudioChunk>? _onChunk;
    private Action<string>? _onFault;
    private long _sessionTimestamp;
    private volatile bool _started;
    private volatile bool _paused;
    private volatile bool _faulted;
    private bool _disposed;
    private int _faultSignal;
    private Task? _pendingCleanup;

    public WasapiMeetingCapture(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source)
    {
        ThrowIfDisposed();
        DataFlow flow = source == MeetingSource.Microphone ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch
        {
            // A machine can legitimately have no default endpoint of this type.
        }

        MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var devices = new List<MeetingDevice>(endpoints.Count);
        foreach (MMDevice endpoint in endpoints)
        {
            using (endpoint)
                devices.Add(new MeetingDevice(endpoint.ID, endpoint.FriendlyName, endpoint.ID == defaultId));
        }
        return devices;
    }

    MeetingAudioSnapshot IMeetingAudioMonitor.GetAudioSnapshot(MeetingSource source)
    {
        SourceCapture? holder = source switch
        {
            MeetingSource.Microphone => Volatile.Read(ref _microphone),
            MeetingSource.Output => Volatile.Read(ref _output),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        if (holder is null)
            return EmptyAudioSnapshot();

        MeetingAudioSnapshot snapshot = holder.Monitor.GetSnapshot();
        SourceCapture? current = source == MeetingSource.Microphone
            ? Volatile.Read(ref _microphone)
            : Volatile.Read(ref _output);
        return ReferenceEquals(holder, current) ? snapshot : EmptyAudioSnapshot();
    }

    public async Task StartAsync(
        string microphoneId,
        string outputId,
        Action<MeetingAudioChunk> onChunk,
        Action<string> onFault,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microphoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        ArgumentNullException.ThrowIfNull(onChunk);
        ArgumentNullException.ThrowIfNull(onFault);
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_started)
                throw new InvalidOperationException("Audio capture is already active.");
            if (_pendingCleanup is not null)
            {
                if (!_pendingCleanup.IsCompleted)
                    throw new MeetingException("The previous audio devices are still releasing.");
                if (!_pendingCleanup.IsCompletedSuccessfully)
                    throw new MeetingException("The previous audio devices were not released safely.");
            }
            _pendingCleanup = null;

            _microphoneId = microphoneId;
            _outputId = outputId;
            _onChunk = onChunk;
            _onFault = onFault;
            _sessionTimestamp = _timeProvider.GetTimestamp();
            _faulted = false;
            _faultSignal = 0;
            _paused = false;
            // Mark the session before either source starts so an immediate device-stop event is not lost.
            _started = true;
            await StartSourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DisposeSources(discardPartial: true);
            ClearSession();
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken) =>
        PauseAsync(cancellationToken, onFirstSourceStopped: null);

    public async Task PauseAsync(
        CancellationToken cancellationToken,
        Action<TimeSpan>? onFirstSourceStopped = null)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_started || _paused)
                return;

            await StopSourcesAsync(cancellationToken, flushPartial: true, onFirstSourceStopped).ConfigureAwait(false);
            _paused = true;
        }
        catch
        {
            await FaultAndReleaseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_started || !_paused || _faulted)
                return;

            await StartSourcesAsync(cancellationToken).ConfigureAwait(false);
            _paused = false;
        }
        catch
        {
            await FaultAndReleaseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started)
                return;

            try
            {
                await StopSourcesAsync(CancellationToken.None, flushPartial: false).ConfigureAwait(false);
            }
            finally
            {
                ClearSession();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            try
            {
                if (_started)
                    await StopSourcesAsync(CancellationToken.None, flushPartial: false).ConfigureAwait(false);
            }
            finally
            {
                ClearSession();
                _disposed = true;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task StartSourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_microphoneId is null || _outputId is null || _onChunk is null)
            throw new InvalidOperationException("Capture session has not been configured.");

        try
        {
            _microphone = CreateSource(MeetingSource.Microphone, _microphoneId);
            _output = CreateSource(MeetingSource.Output, _outputId);
            _microphone.Capture.StartRecording();
            _output.Capture.StartRecording();
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DisposeSources(discardPartial: true);
            throw new MeetingException("The selected audio devices could not be started.");
        }
    }

    private SourceCapture CreateSource(MeetingSource source, string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch
        {
            throw new MeetingException("A selected audio device is no longer available.");
        }

        try
        {
            IWaveIn capture = source == MeetingSource.Microphone
                ? new NAudio.CoreAudioApi.WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared }
                : new WasapiLoopbackCapture(device) { ShareMode = AudioClientShareMode.Shared };
            var monitor = new MeetingAudioMonitor(_timeProvider);
            var holder = new SourceCapture(
                device,
                capture,
                new MeetingPcmAdapter(source, capture.WaveFormat, DeliverChunk, monitor.RecordPeak),
                monitor);
            capture.DataAvailable += (_, args) => OnData(holder, args);
            capture.RecordingStopped += (_, args) => OnStopped(holder, args);
            return holder;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private void OnData(SourceCapture holder, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0 || holder.ExpectedStop || !IsCurrent(holder))
            return;

        try
        {
            TimeSpan end = _timeProvider.GetElapsedTime(_sessionTimestamp);
            TimeSpan duration = TimeSpan.FromSeconds((double)args.BytesRecorded / holder.Capture.WaveFormat.AverageBytesPerSecond);
            TimeSpan start = end > duration ? end - duration : TimeSpan.Zero;
            holder.Adapter.Append(args.Buffer.AsSpan(0, args.BytesRecorded), start);
        }
        catch
        {
            BeginUnexpectedFault();
        }
    }

    private bool IsCurrent(SourceCapture holder) =>
        ReferenceEquals(holder, Volatile.Read(ref _microphone)) ||
        ReferenceEquals(holder, Volatile.Read(ref _output));

    private void OnStopped(SourceCapture holder, StoppedEventArgs args)
    {
        holder.PauseBoundary?.Notify(_timeProvider.GetElapsedTime(_sessionTimestamp));
        if (args.Exception is null)
            holder.Stopped.TrySetResult();
        else
            holder.Stopped.TrySetException(new MeetingException(CaptureFault));

        if (!holder.ExpectedStop)
            BeginUnexpectedFault();
    }

    private void DeliverChunk(MeetingAudioChunk chunk)
    {
        Action<MeetingAudioChunk>? callback = _onChunk;
        if (callback is null || _faulted)
            return;

        try
        {
            callback(chunk);
        }
        catch
        {
            BeginUnexpectedFault();
        }
    }

    private void BeginUnexpectedFault()
    {
        if (!_started || Interlocked.Exchange(ref _faultSignal, 1) != 0)
            return;

        _faulted = true;
        Action<string>? callback = _onFault;
        _ = Task.Run(async () =>
        {
            bool notify = false;
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_started)
                {
                    await FaultAndReleaseAsync().ConfigureAwait(false);
                    notify = true;
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (notify)
            {
                try { callback?.Invoke(CaptureFault); } catch { }
            }
        });
    }

    private async Task FaultAndReleaseAsync()
    {
        _faulted = true;
        try
        {
            await StopSourcesAsync(CancellationToken.None, flushPartial: false).ConfigureAwait(false);
        }
        catch
        {
            // StopSources detaches both sources and makes a bounded release attempt in all cases.
        }
        _paused = false;
    }

    private async Task StopSourcesAsync(
        CancellationToken cancellationToken,
        bool flushPartial,
        Action<TimeSpan>? onFirstSourceStopped = null)
    {
        SourceCapture? microphone = _microphone;
        SourceCapture? output = _output;
        _microphone = null;
        _output = null;
        if (microphone is null && output is null)
            return;

        NativeCaptureStopAttempt? stopAttempt = null;
        try
        {
            SourceCapture[] sources = new[] { microphone, output }.OfType<SourceCapture>().ToArray();
            var pauseBoundary = onFirstSourceStopped is null ? null : new FirstStopBoundary(onFirstSourceStopped);
            foreach (SourceCapture source in sources)
            {
                source.ExpectedStop = true;
                source.Monitor.Reset();
                source.PauseBoundary = pauseBoundary;
            }

            stopAttempt = NativeCaptureStopAttempt.Start(
                sources.Select(source => (Action)(() => RequestNativeStop(source))),
                sources.Select(source => source.Stopped.Task));
            await stopAttempt.WaitAsync(
                NativeStopTimeout,
                cancellationToken,
                static (timeout, token) => Task.Delay(timeout, token)).ConfigureAwait(false);
            if (flushPartial)
            {
                microphone?.Adapter.FlushPartial();
                output?.Adapter.FlushPartial();
            }
            else
            {
                microphone?.Adapter.DiscardPartial();
                output?.Adapter.DiscardPartial();
            }
        }
        finally
        {
            await DisposeSourcesAsync(
                microphone,
                output,
                stopAttempt?.StopCallsFinished ?? Task.CompletedTask).ConfigureAwait(false);
        }

        static void RequestNativeStop(SourceCapture source)
        {
            try
            {
                source.Capture.StopRecording();
            }
            catch
            {
                var safeError = new MeetingException(CaptureFault);
                source.Stopped.TrySetException(safeError);
                throw safeError;
            }
        }
    }

    private async Task DisposeSourcesAsync(
        SourceCapture? microphone,
        SourceCapture? output,
        Task nativeStopCalls)
    {
        Task cleanup = ReleaseAfterNativeStopsAsync(nativeStopCalls, microphone, output);
        if (!nativeStopCalls.IsCompleted)
        {
            TrackPendingCleanup(cleanup);
            return;
        }

        try
        {
            await cleanup.WaitAsync(NativeStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TrackPendingCleanup(cleanup);
            throw new MeetingException("Audio capture resources did not release in time.");
        }
        catch
        {
            TrackPendingCleanup(cleanup);
            throw new MeetingException("Audio capture resources could not be released safely.");
        }
    }

    private static async Task ReleaseAfterNativeStopsAsync(
        Task nativeStopCalls,
        SourceCapture? microphone,
        SourceCapture? output)
    {
        try { await nativeStopCalls.ConfigureAwait(false); } catch { }
        await Task.Run(() =>
        {
            try
            {
                microphone?.Dispose();
            }
            finally
            {
                output?.Dispose();
            }
        }).ConfigureAwait(false);
    }

    private void TrackPendingCleanup(Task cleanup)
    {
        _pendingCleanup = cleanup;
        _ = cleanup.ContinueWith(
            completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeSources(bool discardPartial)
    {
        SourceCapture? microphone = _microphone;
        SourceCapture? output = _output;
        _microphone = null;
        _output = null;
        if (discardPartial)
        {
            microphone?.Adapter.DiscardPartial();
            output?.Adapter.DiscardPartial();
        }
        microphone?.Monitor.Reset();
        output?.Monitor.Reset();
        microphone?.Dispose();
        output?.Dispose();
    }

    private void ClearSession()
    {
        _started = false;
        _paused = false;
        _faulted = false;
        _faultSignal = 0;
        _microphoneId = null;
        _outputId = null;
        _onChunk = null;
        _onFault = null;
    }

    private static MeetingAudioSnapshot EmptyAudioSnapshot() =>
        new(new float[MeetingAudioMonitor.BinCount], 0f, null);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class SourceCapture : IDisposable
    {
        public SourceCapture(
            MMDevice device,
            IWaveIn capture,
            MeetingPcmAdapter adapter,
            MeetingAudioMonitor monitor)
        {
            Device = device;
            Capture = capture;
            Adapter = adapter;
            Monitor = monitor;
        }

        public MMDevice Device { get; }
        public IWaveIn Capture { get; }
        public MeetingPcmAdapter Adapter { get; }
        public MeetingAudioMonitor Monitor { get; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ExpectedStop;
        public FirstStopBoundary? PauseBoundary { get; set; }

        public void Dispose()
        {
            Capture.Dispose();
            Device.Dispose();
        }
    }

    private sealed class FirstStopBoundary
    {
        private readonly Action<TimeSpan> _callback;
        private int _notified;

        public FirstStopBoundary(Action<TimeSpan> callback)
        {
            _callback = callback;
        }

        public void Notify(TimeSpan stoppedAt)
        {
            if (Interlocked.Exchange(ref _notified, 1) != 0)
                return;

            try { _callback(stoppedAt); } catch { }
        }
    }
}

/// <summary>Owns off-thread native stop calls until both calls and their callbacks finish.</summary>
internal sealed class NativeCaptureStopAttempt
{
    private readonly Task _allStopped;

    private NativeCaptureStopAttempt(Task stopCallsFinished, Task allStopped)
    {
        StopCallsFinished = stopCallsFinished;
        _allStopped = allStopped;
    }

    internal Task StopCallsFinished { get; }

    internal static NativeCaptureStopAttempt Start(
        IEnumerable<Action> stopCalls,
        IEnumerable<Task> stoppedCallbacks)
    {
        Task stopCallsFinished = Task.WhenAll(stopCalls.Select(call => Task.Run(call)));
        Task callbacksFinished = Task.WhenAll(stoppedCallbacks);
        return new NativeCaptureStopAttempt(
            stopCallsFinished,
            Task.WhenAll(stopCallsFinished, callbacksFinished));
    }

    internal async Task WaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        Task timeoutTask = delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(_allStopped, timeoutTask).ConfigureAwait(false);
        if (completed != _allStopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new MeetingException("Audio capture did not stop in time.");
        }

        await _allStopped.ConfigureAwait(false);
    }
}

/// <summary>Streaming native-format adapter used by the WASAPI boundary and deterministic tests.</summary>
internal sealed class MeetingPcmAdapter
{
    private const int OutputRate = 16_000;
    private const int ChunkSamples = OutputRate * 4;
    private static readonly TimeSpan CallbackGapTolerance = TimeSpan.FromMilliseconds(100);
    private readonly MeetingSource _source;
    private readonly WaveFormat _format;
    private readonly Action<MeetingAudioChunk> _onChunk;
    private readonly Action<float, TimeSpan>? _onPeak;
    private readonly List<short> _samples = new(ChunkSamples);
    private byte[] _remainder = Array.Empty<byte>();
    private TimeSpan? _chunkStart;
    private TimeSpan? _expectedNextBlockStart;
    private long _inputFrames;
    private long _outputSamples;
    private double _downsampleSum;
    private int _downsampleCount;

    internal MeetingPcmAdapter(
        MeetingSource source,
        WaveFormat format,
        Action<MeetingAudioChunk> onChunk,
        Action<float, TimeSpan>? onPeak = null)
    {
        _source = source;
        _format = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;
        _onChunk = onChunk;
        _onPeak = onPeak;
        ValidateFormat(_format);
    }

    internal void Append(ReadOnlySpan<byte> bytes, TimeSpan sessionStart)
    {
        TimeSpan blockStart = sessionStart;
        if (_expectedNextBlockStart is TimeSpan expected)
        {
            if (sessionStart - expected > CallbackGapTolerance)
            {
                // Loopback emits no callbacks during silence. Keep pre-gap audio separate and
                // re-anchor the next speech to the monotonic session clock.
                FlushPartial();
            }
            else
            {
                // Ignore normal callback scheduling jitter so adjacent chunks do not overlap or drift.
                blockStart = expected;
            }
        }

        int frameBytes = _format.BlockAlign;
        byte[] input;
        if (_remainder.Length == 0)
        {
            input = bytes.ToArray();
        }
        else
        {
            input = new byte[_remainder.Length + bytes.Length];
            _remainder.CopyTo(input, 0);
            bytes.CopyTo(input.AsSpan(_remainder.Length));
        }

        int completeLength = input.Length - (input.Length % frameBytes);
        _remainder = input.AsSpan(completeLength).ToArray();
        if (completeLength == 0)
            return;

        int frames = completeLength / frameBytes;
        int blockOutputSamples = 0;
        float blockPeak = 0f;
        for (int frame = 0; frame < frames; frame++)
        {
            float mono = ReadMono(input.AsSpan(frame * frameBytes, frameBytes), out float framePeak);
            blockPeak = Math.Max(blockPeak, framePeak);
            long expectedOutput = ((_inputFrames + 1) * OutputRate) / _format.SampleRate;
            if (_format.SampleRate > OutputRate)
            {
                _downsampleSum += mono;
                _downsampleCount++;
                if (expectedOutput > _outputSamples)
                {
                    AddSample(
                        (float)(_downsampleSum / _downsampleCount),
                        blockStart + TimeSpan.FromSeconds((double)blockOutputSamples++ / OutputRate));
                    _downsampleSum = 0;
                    _downsampleCount = 0;
                }
            }
            else
            {
                while (_outputSamples < expectedOutput)
                    AddSample(mono, blockStart + TimeSpan.FromSeconds((double)blockOutputSamples++ / OutputRate));
            }
            _inputFrames++;
        }

        try { _onPeak?.Invoke(blockPeak, TimeSpan.FromSeconds((double)frames / _format.SampleRate)); } catch { }

        _expectedNextBlockStart = blockStart + TimeSpan.FromSeconds((double)frames / _format.SampleRate);
    }

    internal void FlushPartial()
    {
        if (_samples.Count > 0 && _chunkStart is TimeSpan start)
            Emit(start, _samples.Count);
        ResetStream();
    }

    internal void DiscardPartial()
    {
        _samples.Clear();
        ResetStream();
    }

    private void AddSample(float value, TimeSpan sampleTime)
    {
        value = Math.Clamp(value, -1f, 1f);
        _chunkStart ??= sampleTime;
        _samples.Add((short)Math.Round(value * (value < 0 ? 32768f : 32767f)));
        _outputSamples++;
        if (_samples.Count != ChunkSamples)
            return;

        Emit(_chunkStart.Value, ChunkSamples);
        _chunkStart = null;
    }

    private void Emit(TimeSpan start, int sampleCount)
    {
        short[] payload = _samples.Take(sampleCount).ToArray();
        _samples.RemoveRange(0, sampleCount);
        _onChunk(new MeetingAudioChunk(
            _source,
            start,
            TimeSpan.FromSeconds((double)sampleCount / OutputRate),
            CreateWave(payload)));
    }

    private float ReadMono(ReadOnlySpan<byte> frame, out float peak)
    {
        int bytesPerSample = _format.BitsPerSample / 8;
        double total = 0;
        peak = 0f;
        for (int channel = 0; channel < _format.Channels; channel++)
        {
            ReadOnlySpan<byte> sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
            float value = _format.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat when bytesPerSample == 4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)),
                WaveFormatEncoding.Pcm when bytesPerSample == 1 => (sample[0] - 128) / 128f,
                WaveFormatEncoding.Pcm when bytesPerSample == 2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                WaveFormatEncoding.Pcm when bytesPerSample == 3 => ReadInt24(sample) / 8388608f,
                WaveFormatEncoding.Pcm when bytesPerSample == 4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                _ => throw new MeetingException("The selected audio device uses an unsupported sample format."),
            };
            if (!float.IsFinite(value))
                value = 0f;
            total += value;
            peak = Math.Max(peak, Math.Min(Math.Abs(value), 1f));
        }
        return (float)(total / _format.Channels);
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private static void ValidateFormat(WaveFormat format)
    {
        bool supported = format.SampleRate > 0 && format.Channels > 0 &&
            ((format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32) ||
             (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32));
        if (!supported)
            throw new MeetingException("The selected audio device uses an unsupported sample format.");
    }

    internal static byte[] CreateWave(short[] samples)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(OutputRate);
        writer.Write(OutputRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (short sample in samples)
            writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    private void ResetStream()
    {
        _samples.Clear();
        _remainder = Array.Empty<byte>();
        _chunkStart = null;
        _expectedNextBlockStart = null;
        _inputFrames = 0;
        _outputSamples = 0;
        _downsampleSum = 0;
        _downsampleCount = 0;
    }
}
