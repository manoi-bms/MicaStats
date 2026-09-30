using System;
using System.Globalization;
using System.IO;
using System.Windows;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The Explain buttons: each opens Ask MicaStats with a question naming the item, in the
    /// Windows display language (<see cref="CultureInfo.CurrentUICulture"/>). Every button is
    /// absent while the assistant is off, so callers ask here rather than read the config.
    /// </summary>
    public static class ExplainActions
    {
        /// <summary>True while the assistant is switched on in Settings > AI.</summary>
        public static bool AssistantOn => App.ConfigService?.Config.AiAssistantEnabled == true;

        /// <summary>Visible while the assistant is on, collapsed otherwise.</summary>
        public static Visibility ButtonVisibility => AssistantOn ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The alert card's Explain action, or null while the assistant is off (no button).</summary>
        public static Action? ForAlert(AlertEvent alert) =>
            ForAlert(alert, AssistantOn, App.OpenAsk, CultureInfo.CurrentUICulture);

        /// <summary>The Explain action for one saved slowdown report, or null while the assistant is off.</summary>
        /// <param name="reportPath">The report's full path; its file name without extension is the report id.</param>
        /// <param name="localTime">When it was written, local time.</param>
        public static Action? ForSlowdownReport(string reportPath, DateTime localTime) =>
            ForSlowdownReport(reportPath, localTime, AssistantOn, App.OpenAsk, CultureInfo.CurrentUICulture);

        /// <summary>Asks what one process is doing: the process window's Explain button.</summary>
        public static void ExplainProcess(string name, int pid) =>
            App.OpenAsk(ExplainQuestions.ForProcess(name, pid, CultureInfo.CurrentUICulture));

        /// <summary><see cref="ForAlert(AlertEvent)"/> with its inputs passed in (tests).</summary>
        internal static Action? ForAlert(AlertEvent alert, bool assistantOn, Action<string?> ask, CultureInfo ui) =>
            assistantOn ? () => ask(ExplainQuestions.ForAlert(alert, ui)) : null;

        /// <summary><see cref="ForSlowdownReport(string, DateTime)"/> with its inputs passed in (tests).</summary>
        internal static Action? ForSlowdownReport(string reportPath, DateTime localTime, bool assistantOn,
            Action<string?> ask, CultureInfo ui) =>
            assistantOn
                ? () => ask(ExplainQuestions.ForSlowdownReport(Path.GetFileNameWithoutExtension(reportPath), localTime, ui))
                : null;
    }
}
