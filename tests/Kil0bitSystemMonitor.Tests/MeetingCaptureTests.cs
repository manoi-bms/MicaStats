using System;
using System.Collections.Generic;
using System.IO;
using Kil0bitSystemMonitor.Services.Conference;
using NAudio.Wave;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingCaptureTests
{
    [Fact]
    public void Adapter_converts_native_stereo_pcm_to_four_second_mono_16khz_wav()
    {
        var chunks = new List<MeetingAudioChunk>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Microphone,
            new WaveFormat(8_000, 16, 2),
            chunks.Add);

        short[] native = new short[8_000 * 4 * 2];
        for (int frame = 0; frame < native.Length / 2; frame++)
        {
            native[frame * 2] = 12_000;
            native[frame * 2 + 1] = -4_000;
        }

        adapter.Append(ToBytes(native), TimeSpan.FromSeconds(7));

        MeetingAudioChunk chunk = Assert.Single(chunks);
        Assert.Equal(MeetingSource.Microphone, chunk.Source);
        Assert.Equal(TimeSpan.FromSeconds(7), chunk.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), chunk.Duration);
        using var reader = new WaveFileReader(new MemoryStream(chunk.Wav, writable: false));
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(64_000, reader.Length / 2);

        byte[] sample = new byte[2];
        Assert.Equal(2, reader.Read(sample, 0, sample.Length));
        Assert.InRange(BitConverter.ToInt16(sample), 3_990, 4_010);
    }

    [Fact]
    public void Pause_flushes_a_partial_chunk_and_resume_uses_the_new_session_time()
    {
        var chunks = new List<MeetingAudioChunk>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Output,
            new WaveFormat(16_000, 16, 1),
            chunks.Add);

        adapter.Append(new byte[16_000 * 2], TimeSpan.FromSeconds(20));
        adapter.FlushPartial();
        adapter.Append(new byte[16_000 * 2], TimeSpan.FromSeconds(45));
        adapter.FlushPartial();

        Assert.Collection(chunks,
            first =>
            {
                Assert.Equal(TimeSpan.FromSeconds(20), first.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), first.Duration);
            },
            resumed =>
            {
                Assert.Equal(TimeSpan.FromSeconds(45), resumed.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), resumed.Duration);
            });
    }

    [Fact]
    public void Stop_discards_a_partial_chunk()
    {
        var chunks = new List<MeetingAudioChunk>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Microphone,
            new WaveFormat(16_000, 16, 1),
            chunks.Add);

        adapter.Append(new byte[16_000 * 2], TimeSpan.Zero);
        adapter.DiscardPartial();

        Assert.Empty(chunks);
    }

    [Fact]
    public void A_full_chunk_followed_by_silence_reanchors_to_the_next_callback_time()
    {
        var chunks = new List<MeetingAudioChunk>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Output,
            new WaveFormat(16_000, 16, 1),
            chunks.Add);

        adapter.Append(new byte[16_000 * 2 * 4], TimeSpan.FromSeconds(2));
        adapter.Append(new byte[16_000 * 2 * 4], TimeSpan.FromSeconds(30));

        Assert.Collection(chunks,
            first =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), first.Start);
                Assert.Equal(TimeSpan.FromSeconds(4), first.Duration);
            },
            afterSilence =>
            {
                Assert.Equal(TimeSpan.FromSeconds(30), afterSilence.Start);
                Assert.Equal(TimeSpan.FromSeconds(4), afterSilence.Duration);
            });
    }

    [Fact]
    public void Silence_flushes_a_partial_chunk_before_starting_a_new_timeline()
    {
        var chunks = new List<MeetingAudioChunk>();
        var adapter = new MeetingPcmAdapter(
            MeetingSource.Output,
            new WaveFormat(16_000, 16, 1),
            chunks.Add);

        adapter.Append(new byte[16_000 * 2], TimeSpan.FromSeconds(3));
        adapter.Append(new byte[16_000 * 2], TimeSpan.FromSeconds(20));
        adapter.FlushPartial();

        Assert.Collection(chunks,
            beforeSilence =>
            {
                Assert.Equal(TimeSpan.FromSeconds(3), beforeSilence.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), beforeSilence.Duration);
            },
            afterSilence =>
            {
                Assert.Equal(TimeSpan.FromSeconds(20), afterSilence.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), afterSilence.Duration);
            });
    }

    [Fact]
    public async Task Native_stop_is_bounded_when_the_completion_callback_never_arrives()
    {
        var missingCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeCaptureStopAttempt attempt = NativeCaptureStopAttempt.Start(
            new Action[] { () => { } },
            new Task[] { missingCallback.Task });
        await attempt.StopCallsFinished;

        Task waiting = attempt.WaitAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            (_, _) => timeout.Task);
        timeout.SetResult();

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() => waiting);
        Assert.Equal("Audio capture did not stop in time.", error.Message);
        Assert.True(attempt.StopCallsFinished.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_blocked_native_stop_call_remains_owned_after_the_bounded_wait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeCaptureStopAttempt attempt = NativeCaptureStopAttempt.Start(
            new Action[]
            {
                () =>
                {
                    entered.SetResult();
                    release.Task.GetAwaiter().GetResult();
                },
            },
            new Task[] { callback.Task });
        await entered.Task;

        Task waiting = attempt.WaitAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            (_, _) => timeout.Task);
        timeout.SetResult();

        await Assert.ThrowsAsync<MeetingException>(() => waiting);
        Assert.False(attempt.StopCallsFinished.IsCompleted);
        release.SetResult();
        await attempt.StopCallsFinished;
    }

    private static byte[] ToBytes(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
