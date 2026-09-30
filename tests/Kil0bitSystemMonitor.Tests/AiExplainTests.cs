using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Explain buttons: the questions they ask (in the Windows display language, with
    /// invariant dates), when they exist at all, and the alert card's button. Thai text is kept
    /// as \u escapes, as in the code under test.
    /// </summary>
    public class AiExplainTests
    {
        private static readonly CultureInfo English = new("en-US");
        private static readonly CultureInfo Thai = new("th-TH");

        private static AlertEvent HotCpu() =>
            new(AlertRule.Defaults[0], 97.4, "", new DateTime(2026, 9, 30, 14, 2, 0));

        [Fact]
        public void The_slowdown_question_names_the_report_and_its_time()
        {
            Assert.Equal("Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215).",
                ExplainQuestions.ForSlowdownReport("slowdown-20260929-140215", new DateTime(2026, 9, 29, 14, 2, 15), English));
        }

        [Fact]
        public void The_alert_question_names_the_rule_the_time_and_the_reading()
        {
            Assert.Equal("Why did the CPU temperature alert fire at 2026-09-30 14:02? MicaStats reported: 97.4\u00B0C, past the 95\u00B0C you set",
                ExplainQuestions.ForAlert(HotCpu(), English));
        }

        [Fact]
        public void The_process_question_names_the_process_and_its_pid()
        {
            Assert.Equal("What is svchost.exe (PID 1234) doing?", ExplainQuestions.ForProcess("svchost.exe", 1234, English));
        }

        [Fact]
        public void A_thai_display_language_asks_in_thai_with_a_gregorian_date()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                // The owner's format culture: its default calendar is Buddhist-era (2569).
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");

                string report = ExplainQuestions.ForSlowdownReport("slowdown-20260929-140215", new DateTime(2026, 9, 29, 14, 2, 15), Thai);
                string alert = ExplainQuestions.ForAlert(HotCpu(), Thai);
                string process = ExplainQuestions.ForProcess("svchost.exe", 1234, Thai);

                Assert.Equal("\u0E2D\u0E18\u0E34\u0E1A\u0E32\u0E22\u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E21\u0E37\u0E48\u0E2D 2026-09-29 14:02 (id slowdown-20260929-140215)", report);
                Assert.Equal("\u0E17\u0E33\u0E44\u0E21\u0E01\u0E32\u0E23\u0E41\u0E08\u0E49\u0E07\u0E40\u0E15\u0E37\u0E2D\u0E19 CPU temperature \u0E08\u0E36\u0E07\u0E40\u0E01\u0E34\u0E14\u0E02\u0E36\u0E49\u0E19\u0E40\u0E21\u0E37\u0E48\u0E2D 2026-09-30 14:02? MicaStats \u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E27\u0E48\u0E32: 97.4\u00B0C, past the 95\u00B0C you set", alert);
                Assert.Equal("svchost.exe (PID 1234) \u0E01\u0E33\u0E25\u0E31\u0E07\u0E17\u0E33\u0E2D\u0E30\u0E44\u0E23\u0E2D\u0E22\u0E39\u0E48?", process);
                Assert.DoesNotContain("2569", report + alert, StringComparison.Ordinal);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Explain_actions_exist_only_while_the_assistant_is_on()
        {
            Assert.Null(ExplainActions.ForAlert(HotCpu(), false, _ => { }, English));
            Assert.Null(ExplainActions.ForSlowdownReport(@"C:\reports\slowdown-20260929-140215.txt",
                new DateTime(2026, 9, 29, 14, 2, 15), false, _ => { }, English));

            Assert.NotNull(ExplainActions.ForAlert(HotCpu(), true, _ => { }, English));
        }

        [Fact]
        public void An_explain_action_asks_its_question_when_invoked()
        {
            var asked = new List<string?>();

            var report = ExplainActions.ForSlowdownReport(@"C:\reports\slowdown-20260929-140215.txt",
                new DateTime(2026, 9, 29, 14, 2, 15), true, asked.Add, English);
            var alert = ExplainActions.ForAlert(HotCpu(), true, asked.Add, English);
            Assert.Empty(asked);

            report!();
            alert!();

            Assert.Equal(new string?[]
            {
                "Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215).",
                "Why did the CPU temperature alert fire at 2026-09-30 14:02? MicaStats reported: 97.4\u00B0C, past the 95\u00B0C you set",
            }, asked);
        }

        [Fact]
        public void Only_slowdown_reports_are_explainable_and_only_while_the_assistant_is_on()
        {
            var reports = new[]
            {
                new SavedReport(@"C:\reports\slowdown-20260929-140215.txt", "slowdown-20260929-140215.txt", new DateTime(2026, 9, 29, 14, 2, 15), 2048),
                new SavedReport(@"C:\reports\hardware-report-20260929-120000.txt", "hardware-report-20260929-120000.txt", new DateTime(2026, 9, 29, 12, 0, 0), 4096),
            };

            var on = ExplainableReport.For(reports, assistantOn: true);
            var off = ExplainableReport.For(reports, assistantOn: false);

            Assert.Equal(Visibility.Visible, on[0].ExplainVisibility);
            Assert.Equal(Visibility.Collapsed, on[1].ExplainVisibility);
            Assert.All(off, r => Assert.Equal(Visibility.Collapsed, r.ExplainVisibility));
            Assert.Equal("slowdown-20260929-140215.txt", on[0].Name);
            Assert.Equal("2026-09-29 14:02", on[0].WhenText);
            Assert.Equal(@"C:\reports\slowdown-20260929-140215.txt", on[0].Path);
        }

        private static List<Button> Buttons(DependencyObject root)
        {
            var found = new List<Button>();
            if (root is Button button) found.Add(button);
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject element) found.AddRange(Buttons(element));
            }
            return found;
        }

        [Fact]
        public void The_alert_card_has_explain_only_when_given_an_action() => UiThread.Run(() =>
        {
            var plain = AlertToastWindow.Create(HotCpu(), () => { }, null);
            try
            {
                Assert.Equal(new[] { "Show me", "Dismiss" }, Buttons(plain).Select(b => (string)b.Content));
            }
            finally
            {
                plain.Close();
            }

            var withExplain = AlertToastWindow.Create(HotCpu(), () => { }, () => { });
            try
            {
                Assert.Equal(new[] { "Show me", "Explain", "Dismiss" }, Buttons(withExplain).Select(b => (string)b.Content));
            }
            finally
            {
                withExplain.Close();
            }
        });

        [Fact]
        public void Explain_on_the_card_asks_without_opening_diagnostics() => UiThread.Run(() =>
        {
            int opened = 0;
            int explained = 0;
            var toast = AlertToastWindow.Create(HotCpu(), () => opened++, () => explained++);

            var explain = Buttons(toast).Single(b => (string)b.Content == "Explain");
            explain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // also closes the card

            Assert.Equal(1, explained);
            Assert.Equal(0, opened);
        });

        [Fact]
        public void The_diagnostics_and_process_windows_carry_explain_buttons()
        {
            string root = PadWindowTests.RepoRoot();
            string diagnostics = File.ReadAllText(Path.Combine(root, "DiagnosticsWindow.xaml"));
            string processes = File.ReadAllText(Path.Combine(root, "TaskManagerWindow.xaml"));

            Assert.Contains("Click=\"OnExplainReport\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Visibility=\"{Binding ExplainVisibility}\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"ExplainButton\"", processes, StringComparison.Ordinal);
            Assert.Contains("Click=\"OnExplain\"", processes, StringComparison.Ordinal);
        }
    }
}
