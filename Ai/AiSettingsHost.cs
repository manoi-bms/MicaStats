using System;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Everything Settings > AI reads and writes, handed in by the settings window, so the panel
    /// can be built in a test over temp stores and fakes instead of the running app.
    /// </summary>
    public sealed class AiSettingsHost
    {
        /// <summary>The live config the panel edits.</summary>
        public required AppConfig Config { get; init; }

        /// <summary>Writes the config to disk now.</summary>
        public required Action Save { get; init; }

        /// <summary>Where keys and the MCP token go; never the config.</summary>
        public required SecretStore Secrets { get; init; }

        /// <summary>The 7-day history, for its size and Delete history; null hides nothing but disables Delete.</summary>
        public HistoryStore? History { get; init; }

        /// <summary>Today's question count, for the limit line.</summary>
        public Func<UsageMeter?> Usage { get; init; } = () => null;

        /// <summary>Why the local HTTP server is not running (for example a port in use), or null.</summary>
        public Func<string?> McpHttpProblem { get; init; } = () => null;

        /// <summary>Builds the provider client for Test connection.</summary>
        public Func<AppConfig, SecretStore, AiClientResult> CreateClient { get; init; } =
            (config, secrets) => AiProviderFactory.Create(config, secrets);

        /// <summary>Puts text on the clipboard (tests record it instead).</summary>
        public Action<string> CopyText { get; init; } = text => System.Windows.Clipboard.SetText(text);

        /// <summary>The full path to MicaStats.exe, for the MCP snippets.</summary>
        public string ExePath { get; init; } = Environment.ProcessPath ?? "MicaStats.exe";
    }
}
