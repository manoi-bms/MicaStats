using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The questions the Explain buttons ask, in the Windows display language: Thai when it is
    /// Thai, English otherwise. The assistant answers in the language of the question, so this
    /// is what makes an Explain answer arrive in the user's own language.
    /// </summary>
    /// <remarks>
    /// Dates use the invariant culture: a Thai-format date carries a Buddhist-era year (2569),
    /// which a model could read as a date 543 years ahead. Thai text is written as \u escapes so
    /// the file stays ASCII.
    /// </remarks>
    public static class ExplainQuestions
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm";

        /// <summary>
        /// "Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215)." The id lets the
        /// model fetch the report with <c>get_slowdown_report</c> straight away.
        /// </summary>
        /// <param name="reportId">The report's file name without extension.</param>
        /// <param name="localTime">When the report was written, local time.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForSlowdownReport(string reportId, DateTime localTime, CultureInfo ui)
        {
            string when = localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? "\u0E2D\u0E18\u0E34\u0E1A\u0E32\u0E22\u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E21\u0E37\u0E48\u0E2D " + when + " (id " + reportId + ")"
                : "Explain slowdown report " + when + " (id " + reportId + ").";
        }

        /// <summary>"Why did the CPU temperature alert fire at ...? MicaStats reported: ..."</summary>
        /// <param name="alert">The alert as the card showed it.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForAlert(AlertEvent alert, CultureInfo ui)
        {
            string when = alert.At.ToString(TimeFormat, CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? "\u0E17\u0E33\u0E44\u0E21\u0E01\u0E32\u0E23\u0E41\u0E08\u0E49\u0E07\u0E40\u0E15\u0E37\u0E2D\u0E19 " + alert.Rule.Label
                  + " \u0E08\u0E36\u0E07\u0E40\u0E01\u0E34\u0E14\u0E02\u0E36\u0E49\u0E19\u0E40\u0E21\u0E37\u0E48\u0E2D " + when
                  + "? MicaStats \u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E27\u0E48\u0E32: " + alert.Message
                : "Why did the " + alert.Rule.Label + " alert fire at " + when + "? MicaStats reported: " + alert.Message;
        }

        /// <summary>"What is svchost.exe (PID 1234) doing?"</summary>
        /// <param name="name">The image name, e.g. svchost.exe.</param>
        /// <param name="pid">Its process id.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForProcess(string name, int pid, CultureInfo ui)
        {
            string id = pid.ToString(CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? name + " (PID " + id + ") \u0E01\u0E33\u0E25\u0E31\u0E07\u0E17\u0E33\u0E2D\u0E30\u0E44\u0E23\u0E2D\u0E22\u0E39\u0E48?"
                : "What is " + name + " (PID " + id + ") doing?";
        }

        private static bool IsThai(CultureInfo ui) =>
            string.Equals(ui.TwoLetterISOLanguageName, "th", StringComparison.OrdinalIgnoreCase);
    }
}
