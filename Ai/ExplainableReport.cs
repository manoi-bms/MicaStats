using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// A saved report as the Diagnostics list shows it, plus whether it has an Explain button:
    /// only slowdown reports do (hardware and diagnostics reports share the folder), and only
    /// while the assistant is on.
    /// </summary>
    public sealed class ExplainableReport
    {
        /// <summary>Wraps one report.</summary>
        public ExplainableReport(SavedReport report, bool canExplain)
        {
            Report = report;
            CanExplain = canExplain;
        }

        /// <summary>The report itself.</summary>
        public SavedReport Report { get; }

        /// <summary>True when the Explain button shows.</summary>
        public bool CanExplain { get; }

        /// <summary>The file name, as before.</summary>
        public string Name => Report.Name;

        /// <summary>When it was written, as before.</summary>
        public string WhenText => Report.WhenText;

        /// <summary>The full path, carried in each button's Tag.</summary>
        public string Path => Report.Path;

        /// <summary>The Explain button's visibility.</summary>
        public Visibility ExplainVisibility => CanExplain ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// True for a slowdown report's file name, by the same rule the data tools use (Task 7's
        /// <c>SlowdownReportFiles</c>), so every Explain button names a report the assistant can read.
        /// </summary>
        public static bool IsSlowdownReport(string fileName) =>
            Kil0bitSystemMonitor.Services.Ai.Tools.SlowdownReportFiles.IsValidId(
                System.IO.Path.GetFileNameWithoutExtension(fileName));

        /// <summary>The rows for a report list.</summary>
        public static IReadOnlyList<ExplainableReport> For(IEnumerable<SavedReport> reports, bool assistantOn) =>
            reports.Select(r => new ExplainableReport(r, assistantOn && IsSlowdownReport(r.Name))).ToList();
    }
}
