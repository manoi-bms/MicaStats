using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The tool names, shared by the in-app assistant, the tool pipe and the MCP servers so the
    /// three can never drift apart.
    /// </summary>
    public static class ToolNames
    {
        /// <summary>
        /// Tool names as the model and MCP clients see them. <see cref="SuggestAction"/> exists
        /// only inside the app: it records a button for the user and is never offered over MCP.
        /// </summary>
        public const string GetLiveStatus = "get_live_status", GetHistory = "get_history", GetTopProcesses = "get_top_processes",
            ListSlowdownReports = "list_slowdown_reports", GetSlowdownReport = "get_slowdown_report", ListAlerts = "list_alerts",
            GetHardware = "get_hardware", GetBattery = "get_battery", GetBootSummary = "get_boot_summary", SuggestAction = "suggest_action";

        /// <summary>The nine read-only tools in catalogue order: exactly what MCP exposes.</summary>
        public static IReadOnlyList<string> ReadOnly { get; } = new[]
        {
            GetLiveStatus, GetHistory, GetTopProcesses, ListSlowdownReports, GetSlowdownReport,
            ListAlerts, GetHardware, GetBattery, GetBootSummary,
        };
    }
}
