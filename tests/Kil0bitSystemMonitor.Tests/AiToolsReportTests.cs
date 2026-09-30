using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiToolsReportTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        private static MicaTools Tools(FakeMicaData data) =>
            new(data, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

        private static double Number(JsonNode? node) => node!.GetValue<double>();

        private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>A report in the exact layout SlowdownReportWriter.Write produces.</summary>
        private const string CpuReport =
            "==============================================================\r\n" +
            " MicaStats Slowdown Report\r\n" +
            " Version   : MicaStats 1.11.0\r\n" +
            " Trigger   : CPU stayed at or above 90% for 8 s\r\n" +
            " Window    : 2026-09-30 11:55:00  to  2026-09-30 12:00:00\r\n" +
            " Samples   : 300\r\n" +
            "==============================================================\r\n" +
            "\r\n" +
            "[Timeline]\r\n" +
            "  12:00:00    97%    61%       12 MB/s  chrome.exe (4242)\r\n" +
            "\r\n" +
            "[Worst offenders across the window]\r\n" +
            "  By CPU  :\r\n" +
            "      chrome.exe                    45.2% average\r\n" +
            "  By disk :\r\n" +
            "      MsMpEng.exe                   12 MB/s average\r\n";

        private static SavedReport Report(string id, DateTime localAt) =>
            new(@"C:\reports\" + id + ".txt", id + ".txt", localAt, 1000);

        // ----- slowdown reports --------------------------------------------------------------

        [Fact]
        public async Task The_report_list_gives_id_time_trigger_and_summary_newest_first()
        {
            DateTime newer = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc).ToLocalTime();
            var data = new FakeMicaData();
            data.Reports.Add(Report("slowdown-20260930-110000", newer));
            data.Reports.Add(Report("slowdown-20260929-090000", newer.AddDays(-1)));
            data.ReportTexts["slowdown-20260930-110000"] = CpuReport;

            var result = await Tools(data).ListSlowdownReportsAsync();

            Assert.Equal(2, result["total"]!.GetValue<int>());
            JsonNode first = result["reports"]![0]!;
            Assert.Equal("slowdown-20260930-110000", first["id"]!.GetValue<string>());
            Assert.Equal("2026-09-30T04:00:00Z", first["utc"]!.GetValue<string>());
            Assert.Equal("CPU stayed at or above 90% for 8 s", first["trigger"]!.GetValue<string>());
            Assert.Equal("busiest by CPU: chrome.exe 45.2% average; busiest by disk: MsMpEng.exe 12 MB/s average",
                first["summary"]!.GetValue<string>());
            Assert.Equal("The report could not be read.", result["reports"]![1]!["summary"]!.GetValue<string>());
        }

        [Fact]
        public async Task The_report_list_clamps_its_limit_to_1_through_30()
        {
            var data = new FakeMicaData();
            for (int i = 0; i < 40; i++)
                data.Reports.Add(Report("slowdown-20260930-1" + i.ToString("00", CultureInfo.InvariantCulture) + "000", DateTime.Now.AddMinutes(-i)));

            var many = await Tools(data).ListSlowdownReportsAsync(limit: 100);
            var none = await Tools(data).ListSlowdownReportsAsync(limit: 0);

            Assert.Equal(30, many["reports"]!.AsArray().Count);
            Assert.Single(none["reports"]!.AsArray());
        }

        [Fact]
        public void A_report_without_samples_says_so()
        {
            (string? trigger, string summary) = MicaTools.Summarize(
                " Trigger   : Recorded by hand\r\n=====\r\n\r\nNo samples were held when this report was written.\r\n");

            Assert.Equal("Recorded by hand", trigger);
            Assert.Equal("No samples were held when the report was written.", summary);
        }

        [Fact]
        public async Task A_report_comes_back_redacted()
        {
            var data = new FakeMicaData();
            data.ReportTexts["slowdown-20260930-110000"] = CpuReport + "  path C:\\Users\\alice\\game.exe on DESK-7\r\n";

            var result = await Tools(data).GetSlowdownReportAsync("slowdown-20260930-110000");

            string text = result["text"]!.GetValue<string>();
            Assert.Contains("%USERPROFILE%\\game.exe on [computer]", text);
            Assert.DoesNotContain("alice", text);
            Assert.False(result["truncated"]!.GetValue<bool>());
        }

        [Fact]
        public async Task A_malformed_id_is_refused_without_reading_anything()
        {
            var data = new FakeMicaData();

            var result = await Tools(data).GetSlowdownReportAsync(@"..\hardware-report-20260930-100000");

            Assert.Contains("is not a slowdown report id", result["error"]!.GetValue<string>());
            Assert.Empty(data.ReportReads);
        }

        [Fact]
        public async Task An_unknown_id_is_an_error()
        {
            var result = await Tools(new FakeMicaData()).GetSlowdownReportAsync("slowdown-20200101-000000");

            Assert.Contains("No slowdown report has the id", result["error"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_very_long_report_keeps_its_head_and_its_tail()
        {
            var data = new FakeMicaData();
            data.ReportTexts["slowdown-20260930-110000"] = "HEAD" + new string('x', 50_000) + "TAIL";

            var result = await Tools(data).GetSlowdownReportAsync("slowdown-20260930-110000");

            string text = result["text"]!.GetValue<string>();
            Assert.True(result["truncated"]!.GetValue<bool>());
            Assert.StartsWith("HEAD", text);
            Assert.EndsWith("TAIL", text);
            Assert.Contains("10008 characters of the timeline left out", text);
            Assert.True(text.Length < 40_100);
        }

        // ----- alerts ------------------------------------------------------------------------

        [Fact]
        public async Task Alerts_list_the_rules_and_the_raised_alerts_since_a_time()
        {
            var data = new FakeMicaData();
            data.Rules.AddRange(AlertRule.Defaults);
            AlertRule cpuTemp = AlertRule.Defaults[0];
            data.Alerts.Add(new AlertEvent(cpuTemp, 97.25, "", Now.AddMinutes(-10).ToLocalTime()));
            data.Alerts.Add(new AlertEvent(cpuTemp, 96.0, "", Now.AddHours(-2).ToLocalTime()));

            var result = await Tools(data).ListAlertsAsync("-30m");

            Assert.Equal(4, result["rules"]!.AsArray().Count);
            Assert.Equal("above", result["rules"]![0]!["firesWhen"]!.GetValue<string>());
            Assert.Equal(95.0, Number(result["rules"]![0]!["threshold"]));
            JsonArray raised = result["raised"]!.AsArray();
            Assert.Single(raised);
            Assert.Equal("cpu-temp", raised[0]!["ruleId"]!.GetValue<string>());
            Assert.Equal(97.3, Number(raised[0]!["value"]));
            Assert.Equal("\u00B0C", raised[0]!["unit"]!.GetValue<string>());
            Assert.Equal(Iso(Now.AddMinutes(-10)), raised[0]!["utc"]!.GetValue<string>());
            Assert.Equal("2026-09-30T04:30:00Z", result["since"]!.GetValue<string>());
        }

        [Fact]
        public async Task Alerts_refuse_an_unreadable_since()
        {
            var result = await Tools(new FakeMicaData()).ListAlertsAsync("last week");

            Assert.Contains("Could not read the time 'last week'", result["error"]!.GetValue<string>());
        }

        // ----- hardware, battery, boot -------------------------------------------------------

        [Fact]
        public async Task The_hardware_report_is_redacted_and_a_missing_one_is_unavailable()
        {
            var found = await Tools(new FakeMicaData { HardwareText = "  Machine ....: DESK-7\n  CPU ....: Ryzen 7" }).GetHardwareAsync();
            var missing = await Tools(new FakeMicaData()).GetHardwareAsync();

            Assert.Equal("  Machine ....: [computer]\n  CPU ....: Ryzen 7", found["report"]!.GetValue<string>());
            Assert.True(missing["unavailable"]!.GetValue<bool>());
        }

        [Fact]
        public async Task A_pc_without_a_battery_says_so()
        {
            var result = await Tools(new FakeMicaData()).GetBatteryAsync();

            Assert.Equal("This PC has no battery.", result["reason"]!.GetValue<string>());
        }

        [Fact]
        public async Task The_battery_reports_charge_draw_time_left_and_health()
        {
            var data = new FakeMicaData
            {
                BatteryReading = new BatteryReading(Present: true, OnAcPower: false, Charging: false, Discharging: true,
                                                    Percent: 80, RemainingMwh: 40_000, RateMw: 10_000, VoltageMv: 11_000),
                Health = new BatteryHealth(new[] { new BatteryPack("P1", "LION", 50_000, 45_000, 321) }),
            };

            var result = await Tools(data).GetBatteryAsync();

            Assert.Equal(80, result["percent"]!.GetValue<int>());
            Assert.Equal(10.0, Number(result["watts"]));
            Assert.Equal(240.0, Number(result["minutesLeft"]));
            Assert.Equal(90.0, Number(result["health"]!["healthPercent"]));
            Assert.Equal(10.0, Number(result["health"]!["wearPercent"]));
            Assert.Equal("Normal", result["health"]!["verdict"]!.GetValue<string>());
            Assert.Equal(321.0, Number(result["health"]!["cycleCount"]));
        }

        [Fact]
        public async Task Unknown_battery_health_is_unavailable()
        {
            var data = new FakeMicaData
            {
                BatteryReading = new BatteryReading(true, true, true, false, 55, 30_000, 20_000, 12_000),
            };

            var result = await Tools(data).GetBatteryAsync();

            Assert.True(result["health"]!["unavailable"]!.GetValue<bool>());
            Assert.True(result["minutesToFull"]!["unavailable"]!.GetValue<bool>());
        }

        [Fact]
        public async Task The_boot_summary_has_boots_trend_and_startup_names_but_no_command_lines()
        {
            DateTime at = Now.AddHours(-3).ToLocalTime();
            var data = new FakeMicaData
            {
                Boot = new BootAnalysis
                {
                    Boots = new[]
                    {
                        new BootRecord(at, 30_000, 20_000, 10_000, 12, false),
                        new BootRecord(at.AddDays(-1), 20_000, 15_000, 5_000, 11, false),
                    },
                    Delays = new[] { new StartupDelay(StartupDelayKind.Application, "OneDrive.exe", "Microsoft OneDrive", "Microsoft", 4_500, 1_200, at) },
                    Entries = new[] { new StartupEntry("OneDrive", "\"C:\\Users\\alice\\OneDrive.exe\" /background", "HKCU Run", StartupScope.CurrentUser, true) },
                },
            };

            var result = await Tools(data).GetBootSummaryAsync();

            Assert.Equal(30.0, Number(result["boots"]![0]!["seconds"]));
            Assert.Equal(Iso(Now.AddHours(-3)), result["boots"]![0]!["utc"]!.GetValue<string>());
            Assert.Equal(10.0, Number(result["trendSeconds"]));
            Assert.Equal("Microsoft OneDrive", result["slowedLastBoot"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("OneDrive", result["startupPrograms"]![0]!["name"]!.GetValue<string>());
            Assert.DoesNotContain("/background", ToolJson.ToText(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_boot_log_that_could_not_be_read_is_unavailable_with_its_reason()
        {
            var data = new FakeMicaData { Boot = new BootAnalysis { Problem = "The boot log could not be read." } };

            var result = await Tools(data).GetBootSummaryAsync();

            Assert.Equal("The boot log could not be read.", result["reason"]!.GetValue<string>());
        }

        [Fact]
        public async Task Invoke_dispatches_every_report_tool()
        {
            var data = new FakeMicaData();
            data.ReportTexts["slowdown-20260930-110000"] = CpuReport;
            MicaTools tools = Tools(data);

            var list = await tools.InvokeAsync(ToolNames.ListSlowdownReports, JsonNode.Parse("{\"limit\":1}")!.AsObject());
            var report = await tools.InvokeAsync(ToolNames.GetSlowdownReport, JsonNode.Parse("{\"id\":\"slowdown-20260930-110000\"}")!.AsObject());
            var others = new[]
            {
                await tools.InvokeAsync(ToolNames.ListAlerts, null),
                await tools.InvokeAsync(ToolNames.GetHardware, null),
                await tools.InvokeAsync(ToolNames.GetBattery, null),
                await tools.InvokeAsync(ToolNames.GetBootSummary, null),
            };

            Assert.NotNull(list["reports"]);
            Assert.Contains("MicaStats Slowdown Report", report["text"]!.GetValue<string>());
            Assert.All(others, r => Assert.Null(r["error"]));
        }
    }
}
