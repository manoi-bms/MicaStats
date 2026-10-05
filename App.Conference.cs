using System.Threading.Tasks;
using System;
using Kil0bitSystemMonitor.Conference;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor;

public partial class App
{
    private static MeetingWindow? s_meetingWindow;

    internal static void OpenMeeting()
    {
        if (s_meetingWindow == null && ConfigService?.Config is { } config)
        {
            var capture = new WasapiMeetingCapture();
            var asr = new BmsAsrClient(service => service == AsrService.Asr1
                ? config.MeetingAsr1BaseUrl : config.MeetingAsr2BaseUrl);
            var session = new MeetingSession(capture, asr,
                transcriptStore: new MeetingTranscriptStore(MeetingTranscriptStore.DefaultRoot));
            var analyzer = new MeetingAnalyzer(() => AiProviderFactory.Create(config, AiSecrets), AiUsage,
                () => config.AiDailyLimit, CurrentBudget, () => config.AiAssistantEnabled,
                () => config.MeetingAiResponseLanguage);
            var intelligence = new MeetingIntelligence(session, analyzer);
            var notes = new MeetingNotes(() => PadHostIfStarted?.Workspace, () => PadHost.StartForNoteTools(), Current.Dispatcher,
                new Kil0bitSystemMonitor.Pad.LiveNoteReader(() => PadHostIfStarted?.Workspace,
                    () => PadHostIfStarted?.Search, () => PadHostIfStarted?.Feeder, Current.Dispatcher,
                    () => PadHost.StartForNoteTools()), CurrentBudget);
            var references = new MeetingReferenceSelection(notes, intelligence);
            var tts = new VoxCpmTtsClient(() => config.MeetingTtsBaseUrl);
            var speech = new MeetingSpeech(session, tts, new WasapiMeetingPlayback());
            s_meetingWindow = new MeetingWindow(config, capture, session, asr, intelligence,
                () => (AiUsage.UsedToday, config.AiDailyLimit), references, speech, tts);
            s_meetingWindow.Closed += (_, _) => s_meetingWindow = null;
            s_meetingWindow.Show();
        }
        if (s_meetingWindow != null)
        {
            if (s_meetingWindow.WindowState == System.Windows.WindowState.Minimized) s_meetingWindow.WindowState = System.Windows.WindowState.Normal;
            s_meetingWindow.Activate();
        }
    }

    private static Task? s_meetingExit;

    private static Task StopMeetingAsync() => s_meetingExit ??= StopMeetingForApplicationExitAsync();

    private static async Task StopMeetingForApplicationExitAsync()
    {
        var window = s_meetingWindow;
        if (window == null) return;
        try { await window.StopForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch { Services.DiagnosticsLog.Warn("meeting", "Audio shutdown did not finish before application exit."); }
        if (!await window.FlushTranscriptAsync().ConfigureAwait(false))
            Services.DiagnosticsLog.Warn("meeting", "Transcript save did not finish before application exit.");
    }

    // OnExit is synchronous. The normal Quit path has already awaited this bounded coordinator.
    private static void StopMeetingBeforeExit() => StopMeetingAsync().GetAwaiter().GetResult();

    internal static void ClearMeetingAfterCredentialStored() => s_meetingWindow?.CredentialsStored();

    internal static async Task ApplyMeetingServiceSettingsAsync(Action apply)
    {
        if (s_meetingWindow is { } window)
            await window.ApplyServiceSettingsAsync(apply);
        else
            apply();
    }
}
