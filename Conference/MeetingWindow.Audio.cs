using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow
{
    private readonly DispatcherTimer _audioTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };

    private void InitializeAudioMonitor()
    {
        _audioTimer.Tick += AudioTimerTick;
        IsVisibleChanged += (_, _) => RefreshAudioMonitor();
        StateChanged += (_, _) => RefreshAudioMonitor();
    }

    private void AudioTimerTick(object? sender, EventArgs e) => RefreshAudioMonitor();

    internal void RefreshAudioMonitor()
    {
        bool listening = !_closing && !_closed && _session.State == MeetingState.Listening;
        bool shouldPoll = listening && IsLoaded && IsVisible && WindowState != WindowState.Minimized;
        _audioTimer.IsEnabled = shouldPoll;

        UpdateSource(MeetingSource.Microphone, MicrophoneWaveform, MicrophoneAudioStatus, MicrophoneLevel);
        UpdateSource(MeetingSource.Output, OutputWaveform, OutputAudioStatus, OutputLevel);

        void UpdateSource(MeetingSource source, MeetingWaveform waveform, TextBlock status, TextBlock level)
        {
            var snapshot = listening ? (_capture as IMeetingAudioMonitor)?.GetAudioSnapshot(source) : null;
            waveform.SetPeaks(snapshot?.Peaks ?? Array.Empty<float>());
            float peak = snapshot?.Peak ?? 0;
            bool recent = snapshot?.Age is { } age && age <= TimeSpan.FromMilliseconds(500);
            if (!recent) peak = 0;
            level.Text = !listening || snapshot?.Age == null ? "— dBFS" : peak > 0
                ? $"{(20 * Math.Log10(peak)).ToString("0", CultureInfo.InvariantCulture)} dBFS" : "−∞ dBFS";
            status.Text = !listening ? _session.State switch
            {
                MeetingState.Paused => "⏸ Paused for speech",
                MeetingState.Faulted => "⚠ Capture stopped",
                _ => "Not listening",
            } : snapshot == null ? "Audio display unavailable"
                : snapshot.Age == null ? "Waiting for audio"
                : !recent ? "No recent audio"
                : peak >= 0.98f ? "⚠ Near clipping · reduce input level"
                : peak >= 0.01f ? "● Sound detected" : "Quiet";
        }
    }
}
