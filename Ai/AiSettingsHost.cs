using System;
using System.IO;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// Asks the provider of the settings for its models and their limits, for the model list.
        /// Null (a test that has no provider) asks no one: the model boxes then take a typed name
        /// only. The settings window supplies <see cref="ModelCatalog.ListAsync"/> for Settings.
        /// </summary>
        public Func<AppConfig, SecretStore, System.Threading.CancellationToken, System.Threading.Tasks.Task<AiModelList>>? ListModels { get; init; }

        /// <summary>
        /// Puts text on the clipboard (tests record it instead). Throws <see cref="ExternalException"/>
        /// while another program holds the clipboard; the panel catches that.
        /// </summary>
        public Action<string> CopyText { get; init; } = text => System.Windows.Clipboard.SetText(text);

        /// <summary>
        /// Puts a secret (the MCP token, or a command that contains it) on the clipboard marked to be
        /// left out of Windows clipboard history (Win+V), cloud clipboard sync and clipboard monitors.
        /// Throws <see cref="ExternalException"/> like <see cref="CopyText"/>.
        /// </summary>
        public Action<string> CopySensitive { get; init; } = CopyExcludedFromHistory;

        private static void CopyExcludedFromHistory(string text)
        {
            var data = new System.Windows.DataObject();
            data.SetText(text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            System.Windows.Clipboard.SetDataObject(data, true);
        }

        /// <summary>The full path to MicaStats.exe, for the MCP snippets.</summary>
        public string ExePath { get; init; } = Environment.ProcessPath ?? "MicaStats.exe";
    }
}
