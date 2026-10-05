using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Plays an in-memory PCM WAV through a selected shared-mode render endpoint.</summary>
public sealed class WasapiMeetingPlayback : IMeetingPlayback
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);
    private readonly IMeetingPlaybackOperationFactory _factory;
    private readonly TimeSpan _stopTimeout;
    private readonly Func<TimeSpan, Task> _delay;
    private int _active;

    public WasapiMeetingPlayback() : this(
        new WasapiPlaybackOperationFactory(),
        DefaultStopTimeout,
        timeout => Task.Delay(timeout)) { }

    internal WasapiMeetingPlayback(
        IMeetingPlaybackOperationFactory factory,
        TimeSpan? stopTimeout = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _factory = factory;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _delay = delay ?? (timeout => Task.Delay(timeout));
    }

    public async Task PlayAsync(byte[] wav, string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (wav.Length == 0)
            throw new MeetingException("Speech audio is empty.");
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new MeetingException("Speech playback is already active.");

        IMeetingPlaybackOperation? operation = null;
        Task? nativeStopFinished = null;
        try
        {
            IMeetingPlaybackOperation created;
            try
            {
                created = _factory.Create(wav, deviceId);
                operation = created;
            }
            catch (MeetingException)
            {
                throw;
            }
            catch
            {
                throw new MeetingException("Speech playback could not open the selected output device.");
            }

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int stopStarted = 0;
            created.Stopped += OnStopped;
            try
            {
                created.Play();
                using CancellationTokenRegistration registration = cancellationToken.Register(RequestStop);
                Task first = await Task.WhenAny(stopped.Task, stopRequested.Task).ConfigureAwait(false);
                if (first == stopRequested.Task)
                {
                    Task finished = await Task.WhenAny(stopped.Task, _delay(_stopTimeout)).ConfigureAwait(false);
                    if (finished != stopped.Task)
                        throw new MeetingPlaybackStopException();
                }
                await stopped.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                created.Stopped -= OnStopped;
            }

            void RequestStop()
            {
                if (Interlocked.Exchange(ref stopStarted, 1) != 0)
                    return;

                nativeStopFinished = stopFinished.Task;
                stopRequested.TrySetResult();
                _ = Task.Run(() =>
                {
                    try
                    {
                        created.Stop();
                    }
                    catch
                    {
                        stopped.TrySetException(new MeetingPlaybackStopException());
                    }
                    finally
                    {
                        stopFinished.TrySetResult();
                    }
                });
            }

            void OnStopped(Exception? error)
            {
                if (error is null)
                    stopped.TrySetResult();
                else
                    stopped.TrySetException(new MeetingException("Speech playback stopped unexpectedly."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MeetingPlaybackStopException)
        {
            throw;
        }
        catch (MeetingException)
        {
            throw;
        }
        catch
        {
            throw new MeetingException("Speech playback failed.");
        }
        finally
        {
            if (operation is not null && nativeStopFinished is { IsCompleted: false })
            {
                IMeetingPlaybackOperation retained = operation;
                operation = null;
                _ = ReleaseAfterNativeStopAsync(retained, nativeStopFinished);
            }
            else
            {
                try { operation?.Dispose(); } catch { }
                finally { Volatile.Write(ref _active, 0); }
            }
        }
    }

    private async Task ReleaseAfterNativeStopAsync(IMeetingPlaybackOperation operation, Task nativeStopFinished)
    {
        try
        {
            await nativeStopFinished.ConfigureAwait(false);
        }
        catch
        {
            // The stop path reports its safe error through the playback completion task.
        }
        finally
        {
            try { operation.Dispose(); } catch { }
            finally { Volatile.Write(ref _active, 0); }
        }
    }
}

internal interface IMeetingPlaybackOperationFactory
{
    IMeetingPlaybackOperation Create(byte[] wav, string deviceId);
}

internal interface IMeetingPlaybackOperation : IDisposable
{
    event Action<Exception?>? Stopped;
    void Play();
    void Stop();
}

internal sealed class WasapiPlaybackOperationFactory : IMeetingPlaybackOperationFactory
{
    public IMeetingPlaybackOperation Create(byte[] wav, string deviceId)
    {
        MemoryStream? stream = null;
        WaveFileReader? reader = null;
        MMDevice? device = null;
        WasapiOut? player = null;
        try
        {
            stream = new MemoryStream(wav, writable: false);
            reader = new WaveFileReader(stream);
            if (reader.Length == 0 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
                reader.WaveFormat.BitsPerSample != 16)
                throw new MeetingException("Speech audio must be a non-empty PCM16 WAV.");

            using (var enumerator = new MMDeviceEnumerator())
                device = enumerator.GetDevice(deviceId);
            player = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
            player.Init(reader);
            return new WasapiPlaybackOperation(stream, reader, device, player);
        }
        catch (MeetingException)
        {
            DisposePartial(player, reader, stream, device);
            throw;
        }
        catch
        {
            DisposePartial(player, reader, stream, device);
            throw new MeetingException("Speech playback could not open the selected output device.");
        }
    }

    private static void DisposePartial(
        WasapiOut? player,
        WaveFileReader? reader,
        MemoryStream? stream,
        MMDevice? device)
    {
        try { player?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { stream?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
    }
}

internal sealed class WasapiPlaybackOperation : IMeetingPlaybackOperation
{
    private readonly MemoryStream _stream;
    private readonly WaveFileReader _reader;
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private bool _disposed;

    public WasapiPlaybackOperation(
        MemoryStream stream,
        WaveFileReader reader,
        MMDevice device,
        WasapiOut player)
    {
        _stream = stream;
        _reader = reader;
        _device = device;
        _player = player;
        _player.PlaybackStopped += PlayerOnPlaybackStopped;
    }

    public event Action<Exception?>? Stopped;

    public void Play() => _player.Play();

    public void Stop() => _player.Stop();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _player.PlaybackStopped -= PlayerOnPlaybackStopped;
        try
        {
            _player.Dispose();
        }
        finally
        {
            try
            {
                _reader.Dispose();
            }
            finally
            {
                try { _stream.Dispose(); }
                finally { _device.Dispose(); }
            }
        }
    }

    private void PlayerOnPlaybackStopped(object? sender, StoppedEventArgs args) =>
        Stopped?.Invoke(args.Exception);
}
