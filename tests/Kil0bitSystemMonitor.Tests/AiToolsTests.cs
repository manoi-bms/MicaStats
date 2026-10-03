using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using Kil0bitSystemMonitor.Services.Sensors;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiToolsTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        private static MicaTools Tools(FakeMicaData data) =>
            new(data, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

        private static HistoryRow Row(int minutesBeforeNow, float? cpuAvg = null, float? cpuMax = null) => new()
        {
            Utc = Now.AddMinutes(-minutesBeforeNow),
            Seconds = 60,
            CpuAvg = cpuAvg,
            CpuMax = cpuMax,
        };

        private static double Number(JsonNode? node) => node!.GetValue<double>();

        // ----- get_live_status ---------------------------------------------------------------

        [Fact]
        public async Task Live_status_reports_rounded_readings_and_the_recent_window()
        {
            var data = new FakeMicaData();
            data.Stats["cpu"] = new SeriesStats(5f, 12.345f, 40f, 120);

            var result = Assert.IsType<JsonObject>(await Tools(data).GetLiveStatusAsync());

            Assert.Equal("2026-09-30T05:00:00Z", result["time"]!.GetValue<string>());
            Assert.Equal(12.3, Number(result["cpu"]!["usagePercent"]));
            Assert.Equal(3.5, Number(result["cpu"]!["clockGhz"]));
            Assert.Equal(4, result["cpu"]!["cores"]!["count"]!.GetValue<int>());
            Assert.Equal(40.0, Number(result["cpu"]!["cores"]!["max"]));
            Assert.Equal(61.3, Number(result["memory"]!["usedPercent"]));
            Assert.Equal(16.0, Number(result["memory"]!["totalGb"]));
            Assert.Equal(25.0, Number(result["disks"]![0]!["freePercent"]));
            Assert.Equal(250.0, Number(result["network"]!["downKbps"]));
            Assert.Equal(12.3, Number(result["recent"]!["cpu"]!["avg"]));
            Assert.Equal(120, result["recent"]!["cpu"]!["samples"]!.GetValue<int>());
        }

        [Fact]
        public async Task Missing_readings_are_unavailable_with_a_reason_never_zero()
        {
            var result = Assert.IsType<JsonObject>(await Tools(new FakeMicaData()).GetLiveStatusAsync());

            JsonObject temperature = Assert.IsType<JsonObject>(result["cpu"]!["temperatureC"]);
            Assert.True(temperature["unavailable"]!.GetValue<bool>());
            Assert.Contains("temperature", temperature["reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(result["gpu"]!["usagePercent"]!["unavailable"]!.GetValue<bool>());
            Assert.True(result["disks"]![1]!["freePercent"]!["unavailable"]!.GetValue<bool>());
            Assert.True(result["battery"]!["unavailable"]!.GetValue<bool>());
            Assert.True(result["sensors"]!["unavailable"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Live_status_leaves_out_the_ip_address_and_redacts_names_and_paths()
        {
            var data = new FakeMicaData();
            data.Metrics!.NetAdapterName = "DESK-7 Wi-Fi";
            data.Metrics.Sensors = new[]
            {
                new SensorReading("zone.TZ01", "System (TZ01)", SensorCategory.Temperature, 45.55, "C", @"C:\Users\alice\tools\probe.exe"),
            };

            var result = Assert.IsType<JsonObject>(await Tools(data).GetLiveStatusAsync());

            Assert.DoesNotContain("192.168.1.20", ToolJson.ToText(result), StringComparison.Ordinal);
            Assert.Equal("[computer] Wi-Fi", result["network"]!["adapter"]!.GetValue<string>());
            Assert.Equal(@"%USERPROFILE%\tools\probe.exe", result["sensors"]![0]!["source"]!.GetValue<string>());
            Assert.Equal(45.6, Number(result["sensors"]![0]!["value"]));
        }

        /// <summary>
        /// NaN or infinity cannot be written as JSON: one such sensor made the tool text and the
        /// tool pipe frame throw, so no live status reached the model or an MCP client at all.
        /// </summary>
        [Fact]
        public async Task A_reading_that_is_not_a_finite_number_is_unavailable_and_the_result_still_serialises()
        {
            var data = new FakeMicaData();
            data.Metrics!.CpuUsage = float.NaN;
            data.Metrics.Sensors = new[] { new SensorReading("x", "Bad", SensorCategory.Temperature, double.NaN, "C", "HWiNFO") };
            data.Stats["cpu"] = new SeriesStats(float.NegativeInfinity, 1f, float.PositiveInfinity, 3);

            JsonNode result = await Tools(data).GetLiveStatusAsync();

            string text = ToolJson.ToText(result);
            await ToolPipeProtocol.WriteAsync(new MemoryStream(),
                new JsonObject { ["v"] = 1, ["ok"] = true, ["result"] = result.DeepClone() }, CancellationToken.None);
            JsonObject usage = Assert.IsType<JsonObject>(result["cpu"]!["usagePercent"]);
            Assert.True(usage["unavailable"]!.GetValue<bool>());
            Assert.Equal("Not a finite reading.", usage["reason"]!.GetValue<string>());
            Assert.True(result["sensors"]![0]!["value"]!["unavailable"]!.GetValue<bool>());
            Assert.True(result["recent"]!["cpu"]!["max"]!["unavailable"]!.GetValue<bool>());
            Assert.Equal(1.0, Number(result["recent"]!["cpu"]!["avg"]));
            Assert.Contains("Not a finite reading.", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Before_the_first_sample_live_status_is_unavailable()
        {
            var result = await Tools(new FakeMicaData { Metrics = null }).GetLiveStatusAsync();

            Assert.True(result["unavailable"]!.GetValue<bool>());
        }

        [Fact]
        public async Task A_failing_source_becomes_an_error_result_never_an_exception()
        {
            var broken = await Tools(new FakeMicaData { Failure = new InvalidOperationException("counter broke") }).GetLiveStatusAsync();
            var offline = await Tools(new FakeMicaData { Failure = new DataUnavailableException("MicaStats is not running") }).GetLiveStatusAsync();

            Assert.Contains("counter broke", broken["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("MicaStats is not running", offline["error"]!.GetValue<string>());
            Assert.Single(offline.AsObject());
        }

        [Fact]
        public async Task Cancellation_is_the_one_thing_that_escapes()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools(new FakeMicaData()).GetLiveStatusAsync(cts.Token));
        }

        // ----- get_history -------------------------------------------------------------------

        [Fact]
        public async Task History_returns_the_metric_per_point_and_resolves_relative_times()
        {
            var data = new FakeMicaData();
            data.Rows.AddRange(new[] { Row(3, 10f, 15f), Row(2, 20.04f, 25f), Row(1, 30f, 35f) });

            var result = await Tools(data).GetHistoryAsync("CPU", "-1h", "now");

            Assert.Equal((Now.AddHours(-1), Now), data.LastHistoryRange);
            Assert.Equal("cpu", result["metric"]!.GetValue<string>());
            Assert.Equal("2026-09-30T04:00:00Z", result["from"]!.GetValue<string>());
            Assert.Equal(3, result["rows"]!.GetValue<int>());
            Assert.False(result["downsampled"]!.GetValue<bool>());
            JsonArray points = result["points"]!.AsArray();
            Assert.Equal(3, points.Count);
            Assert.Equal("2026-09-30T04:58:00Z", points[1]!["utc"]!.GetValue<string>());
            Assert.Equal(20.0, Number(points[1]!["avg"]));
            Assert.Equal(25.0, Number(points[1]!["max"]));
        }

        [Fact]
        public async Task History_leaves_out_values_that_were_not_measured()
        {
            var data = new FakeMicaData();
            data.Rows.Add(Row(1) with { CpuTempMax = 71f });

            var result = await Tools(data).GetHistoryAsync("cpuTemp", "-1h", "now");

            JsonObject point = result["points"]![0]!.AsObject();
            Assert.False(point.ContainsKey("avgC"));
            Assert.Equal(71.0, Number(point["maxC"]));
        }

        [Fact]
        public async Task History_averages_down_to_max_points()
        {
            var data = new FakeMicaData();
            for (int i = 0; i < 10; i++) data.Rows.Add(Row(10 - i, i * 10f, i * 10f + 5f));

            var result = await Tools(data).GetHistoryAsync("cpu", "-1h", "now", maxPoints: 5);

            JsonArray points = result["points"]!.AsArray();
            Assert.Equal(5, points.Count);
            Assert.True(result["downsampled"]!.GetValue<bool>());
            Assert.Equal(5.0, Number(points[0]!["avg"]));
            Assert.Equal(15.0, Number(points[0]!["max"]));
            Assert.Equal(120, points[0]!["seconds"]!.GetValue<int>());
        }

        [Fact]
        public async Task History_never_returns_more_than_500_points()
        {
            var data = new FakeMicaData();
            for (int i = 0; i < 600; i++) data.Rows.Add(Row(600 - i, 1f, 1f));

            var result = await Tools(data).GetHistoryAsync("cpu", "-1d", "now", maxPoints: 1000);

            Assert.Equal(MicaTools.MaxHistoryPoints, result["points"]!.AsArray().Count);
        }

        /// <summary>
        /// Every field of every point: 500 of them were about 240,000 characters, re-sent with
        /// every later round. The description already asks for at most 60; the server holds it.
        /// </summary>
        [Fact]
        public async Task History_all_never_returns_more_than_60_points()
        {
            var data = new FakeMicaData();
            for (int i = 0; i < 600; i++) data.Rows.Add(Row(600 - i, 1f, 1f));

            var all = await Tools(data).GetHistoryAsync("all", "-1d", "now", maxPoints: 500);
            var cpu = await Tools(data).GetHistoryAsync("cpu", "-1d", "now", maxPoints: 500);

            Assert.Equal(MicaTools.MaxHistoryPointsAll, all["points"]!.AsArray().Count);
            Assert.Equal(60, MicaTools.MaxHistoryPointsAll);
            Assert.True(all["downsampled"]!.GetValue<bool>());
            Assert.Equal(500, cpu["points"]!.AsArray().Count);
        }

        [Fact]
        public async Task History_refuses_unknown_metrics_and_unreadable_times()
        {
            MicaTools tools = Tools(new FakeMicaData());

            var metric = await tools.GetHistoryAsync("fan", "-1h", "now");
            var time = await tools.GetHistoryAsync("cpu", "yesterday", "now");
            var order = await tools.GetHistoryAsync("cpu", "now", "-1h");

            Assert.Contains("Use one of: cpu, cpuTemp", metric["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("Could not read the time 'yesterday'", time["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("is after", order["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task History_all_carries_the_top_process_with_a_redacted_path()
        {
            var data = new FakeMicaData();
            data.Rows.Add(Row(1, 50f, 90f) with
            {
                TopCpuName = "chrome.exe",
                TopCpuPath = @"C:\Users\alice\AppData\Local\Google\Chrome\Application\chrome.exe",
                TopCpuPercent = 41.25f,
                TopRamName = "Code.exe",
                TopRamMb = 1500.5f,
            });

            var result = await Tools(data).GetHistoryAsync("all", "-1h", "now", maxPoints: 60);

            JsonNode point = result["points"]![0]!;
            Assert.Equal(50.0, Number(point["cpuAvg"]));
            Assert.Equal("chrome.exe", point["topCpu"]!["name"]!.GetValue<string>());
            Assert.Equal(@"%USERPROFILE%\AppData\Local\Google\Chrome\Application\chrome.exe", point["topCpu"]!["path"]!.GetValue<string>());
            Assert.Equal(41.3, Number(point["topCpu"]!["cpuPercent"]));
            Assert.Equal(1500.5, Number(point["topMemory"]!["memoryMb"]));
        }

        [Fact]
        public async Task An_empty_range_says_how_to_turn_history_on()
        {
            var result = await Tools(new FakeMicaData()).GetHistoryAsync("ram", "-2d", "now");

            Assert.Empty(result["points"]!.AsArray());
            Assert.Contains("Keep 7 days of history", result["note"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Times_stay_invariant_under_a_thai_culture()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var data = new FakeMicaData();
                data.Rows.Add(Row(1, 12.5f, 20f));

                var result = await Tools(data).GetHistoryAsync("cpu", "-1h", "now");

                Assert.Equal("2026-09-30T04:00:00Z", result["from"]!.GetValue<string>());
                Assert.Equal("2026-09-30T04:59:00Z", result["points"]![0]!["utc"]!.GetValue<string>());
                Assert.Equal(12.5, Number(result["points"]![0]!["avg"]));
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }

        // ----- get_top_processes -------------------------------------------------------------

        [Fact]
        public async Task Top_processes_normalise_the_ranking_and_clamp_the_count()
        {
            var data = new FakeMicaData();

            var result = await Tools(data).GetTopProcessesAsync("RAM", 50);

            Assert.Equal(("memory", 15), data.LastTopRequest);
            Assert.Equal("memory", result["by"]!.GetValue<string>());
        }

        [Fact]
        public async Task Top_processes_carry_identity_rounded_numbers_and_a_redacted_path()
        {
            long created = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            var data = new FakeMicaData();
            data.Processes.Add(new ProcessInfo("chrome.exe", @"C:\Users\alice\AppData\Local\chrome.exe", 4242, created, 45.67f, 512.25, 1024.5));

            var result = await Tools(data).GetTopProcessesAsync("cpu", 5);

            JsonNode p = result["processes"]![0]!;
            Assert.Equal(4242, p["pid"]!.GetValue<int>());
            Assert.Equal(created, p["createTime"]!.GetValue<long>());
            Assert.Equal("2026-09-30T04:00:00Z", p["startedUtc"]!.GetValue<string>());
            Assert.Equal(@"%USERPROFILE%\AppData\Local\chrome.exe", p["path"]!.GetValue<string>());
            Assert.Equal(45.7, Number(p["cpuPercent"]));
            Assert.Equal(512.3, Number(p["memoryMb"]));
            Assert.Equal(1024.5, Number(p["diskKBps"]));
        }

        [Fact]
        public async Task Top_processes_refuse_an_unknown_ranking()
        {
            var result = await Tools(new FakeMicaData()).GetTopProcessesAsync("gpu");

            Assert.Contains("Use cpu, memory or disk", result["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // ----- dispatcher, names, shapes -----------------------------------------------------

        [Fact]
        public async Task Invoke_dispatches_by_name_with_json_arguments()
        {
            var data = new FakeMicaData();
            MicaTools tools = Tools(data);

            var history = await tools.InvokeAsync(ToolNames.GetHistory,
                JsonNode.Parse("{\"metric\":\"cpu\",\"from\":\"-30m\",\"maxPoints\":\"2\"}")!.AsObject());
            await tools.InvokeAsync(ToolNames.GetTopProcesses, JsonNode.Parse("{\"by\":\"disk\",\"count\":3}")!.AsObject());
            var live = await tools.InvokeAsync(ToolNames.GetLiveStatus, null);

            Assert.Equal("cpu", history["metric"]!.GetValue<string>());
            Assert.Equal((Now.AddMinutes(-30), Now), data.LastHistoryRange);
            Assert.Equal(("disk", 3), data.LastTopRequest);
            Assert.NotNull(live["cpu"]);
        }

        [Fact]
        public async Task Invoke_refuses_unknown_tools_including_suggest_action()
        {
            MicaTools tools = Tools(new FakeMicaData());

            var suggest = await tools.InvokeAsync(ToolNames.SuggestAction, null);
            var made_up = await tools.InvokeAsync("delete_everything", null);

            Assert.StartsWith("Unknown tool 'suggest_action'", suggest["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.StartsWith("Unknown tool 'delete_everything'", made_up["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task With_notes_allowed_invoke_still_refuses_suggest_action_and_any_tool_that_writes()
        {
            var reader = new FakeNoteReader();
            var tools = new MicaTools(new FakeMicaData(), new Redactor(@"C:\Users\alice", "alice", "DESK-7"))
            {
                Notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader), () => true, () => true),
            };

            var suggest = await tools.InvokeAsync(ToolNames.SuggestAction, null);
            var write = await tools.InvokeAsync("set_note", new JsonObject { ["noteId"] = "a1", ["text"] = "gone" });
            var search = await tools.InvokeAsync(ToolNames.SearchNotes, new JsonObject { ["query"] = "vpn" });

            Assert.StartsWith("Unknown tool 'suggest_action'", suggest["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.StartsWith("Unknown tool 'set_note'", write["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Null(search["error"]);
            Assert.Equal(1, reader.Searches);
            Assert.Equal(0, reader.Reads);
        }

        [Fact]
        public void The_read_only_list_is_the_nine_tools_in_order()
        {
            Assert.Equal(new[]
            {
                "get_live_status", "get_history", "get_top_processes", "list_slowdown_reports", "get_slowdown_report",
                "list_alerts", "get_hardware", "get_battery", "get_boot_summary",
            }, ToolNames.ReadOnly);
            Assert.DoesNotContain(ToolNames.SuggestAction, ToolNames.ReadOnly);
        }

        [Fact]
        public void The_note_tools_are_a_list_of_their_own_outside_the_nine()
        {
            Assert.Equal(new[] { "search_notes", "get_note" }, ToolNames.Notes);
            Assert.Empty(ToolNames.Notes.Intersect(ToolNames.ReadOnly));
            Assert.DoesNotContain(ToolNames.SuggestAction, ToolNames.Notes);
        }

        [Fact]
        public void Unavailable_and_error_have_one_shape_each()
        {
            JsonObject unavailable = ToolJson.Unavailable("No battery.");
            JsonObject error = ToolJson.Error("Boom.");

            Assert.Equal(2, unavailable.Count);
            Assert.True(unavailable["unavailable"]!.GetValue<bool>());
            Assert.Equal("No battery.", unavailable["reason"]!.GetValue<string>());
            Assert.Single(error);
            Assert.Equal("Boom.", error["error"]!.GetValue<string>());
        }

        [Fact]
        public void Tool_text_keeps_angle_brackets_plus_signs_and_thai_readable()
        {
            const string thai = "\u0E0B\u0E35\u0E1E\u0E35\u0E22\u0E39";
            var node = new JsonObject { ["path"] = @"C:\Users\<user>\a.exe", ["offset"] = "+07:00", ["label"] = thai };

            string text = ToolJson.ToText(node);

            Assert.Contains("<user>", text, StringComparison.Ordinal);
            Assert.Contains("+07:00", text, StringComparison.Ordinal);
            Assert.Contains(thai, text, StringComparison.Ordinal);
            Assert.Equal(thai, JsonNode.Parse(text)!["label"]!.GetValue<string>());
            Assert.Equal("null", ToolJson.ToText(null));
        }
    }
}
