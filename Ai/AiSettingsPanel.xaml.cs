using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Capture;
using Microsoft.Extensions.AI;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own UserControl.
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Settings > AI. Every change is written to the config and saved at once; App reacts to
    /// the changed <c>Ai*</c> properties (hotkey, servers, history recorder). Keys and the MCP
    /// token go to the secret store and are never displayed. Nothing here counts against the
    /// daily question limit, Test connection included.
    /// </summary>
    public partial class AiSettingsPanel : UserControl
    {
        private const string HotkeyHelp = "Opens Ask MicaStats from anywhere while the assistant is on. Leave empty to turn it off.";

        private AiSettingsHost? _host;

        /// <summary>Suppresses change handlers while the panel is being filled from the config.</summary>
        private bool _loading;

        /// <summary>Builds the panel; nothing shows until <see cref="Load"/>.</summary>
        public AiSettingsPanel()
        {
            InitializeComponent();
        }

        /// <summary>Fills the panel from the host's config and stores. Safe to call again.</summary>
        public void Load(AiSettingsHost host)
        {
            _host = host;
            _loading = true;
            try
            {
                var cfg = host.Config;
                AssistantToggle.IsOn = cfg.AiAssistantEnabled;
                ProviderBox.SelectedIndex = cfg.AiProvider == AiProviders.OpenAiCompatible ? 1 : 0;
                ClaudeModelBox.Text = cfg.AiClaudeModel;
                CompatibleUrlBox.Text = cfg.AiCompatibleBaseUrl;
                CompatibleModelBox.Text = cfg.AiCompatibleModel;
                HotkeyBox.Text = cfg.AiHotkey;
                HotkeyHint.Text = HotkeyHelp;
                LimitBox.Text = cfg.AiDailyLimit.ToString(CultureInfo.InvariantCulture);
                HistoryToggle.IsOn = cfg.AiHistoryEnabled;
                McpModeBox.SelectedIndex = cfg.AiMcpMode switch
                {
                    AiMcpModes.Stdio => 1,
                    AiMcpModes.Http => 2,
                    _ => 0,
                };
                McpPortBox.Text = cfg.AiMcpHttpPort.ToString(CultureInfo.InvariantCulture);
                TestResultText.Text = "";
                KeyHint.Visibility = Visibility.Collapsed;
                McpStatusText.Text = "";
            }
            finally
            {
                _loading = false;
            }

            RefreshProvider();
            RefreshKeys();
            RefreshUsage();
            RefreshHistory();
            RefreshMcp();
        }

        // ---- assistant and provider --------------------------------------------------------

        private void OnAssistantToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            _host.Config.AiAssistantEnabled = AssistantToggle.IsOn;
            _host.Save();
        }

        private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || ProviderBox.SelectedIndex < 0) return;
            _host.Config.AiProvider = ProviderBox.SelectedIndex == 1 ? AiProviders.OpenAiCompatible : AiProviders.Claude;
            _host.Save();
            TestResultText.Text = "";
            RefreshProvider();
        }

        private void OnClaudeModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            _host.Config.AiClaudeModel = ClaudeModelBox.Text;
            ClaudeModelBox.Text = _host.Config.AiClaudeModel;   // the setter trims and restores the default when blank
            _host.Save();
        }

        private void OnCompatibleUrlChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            _host.Config.AiCompatibleBaseUrl = CompatibleUrlBox.Text;
            CompatibleUrlBox.Text = _host.Config.AiCompatibleBaseUrl;
            _host.Save();
            RefreshProvider();
        }

        private void OnCompatibleModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            _host.Config.AiCompatibleModel = CompatibleModelBox.Text;
            CompatibleModelBox.Text = _host.Config.AiCompatibleModel;
            _host.Save();
        }

        private void RefreshProvider()
        {
            if (_host == null) return;
            bool compatible = _host.Config.AiProvider == AiProviders.OpenAiCompatible;
            ClaudePanel.Visibility = compatible ? Visibility.Collapsed : Visibility.Visible;
            CompatiblePanel.Visibility = compatible ? Visibility.Visible : Visibility.Collapsed;
            PrivacyText.Text = AiPrivacyNote.Describe(_host.Config.AiProvider, _host.Config.AiCompatibleBaseUrl);
        }

        // ---- keys ----------------------------------------------------------------------------

        private void OnSaveClaudeKey(object sender, RoutedEventArgs e) => SaveKey(SecretNames.ClaudeKey, ClaudeKeyBox);

        private void OnSaveCompatibleKey(object sender, RoutedEventArgs e) => SaveKey(SecretNames.CompatibleKey, CompatibleKeyBox);

        private void OnRemoveClaudeKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.ClaudeKey);

        private void OnRemoveCompatibleKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.CompatibleKey);

        /// <summary>Stores the typed key and empties the box; the key is never put back on screen.</summary>
        private void SaveKey(string name, PasswordBox box)
        {
            if (_host == null) return;
            string key = box.Password.Trim();
            box.Clear();
            if (key.Length == 0)
            {
                ShowKeyHint("Paste the key into the box first, then press Save.");
                return;
            }

            try
            {
                _host.Secrets.Set(name, key);
                ShowKeyHint("Saved. The key is stored encrypted for your Windows account and is not shown again.");
            }
            catch (Exception ex)
            {
                ShowKeyHint("The key could not be stored (" + ex.GetType().Name + ").");
            }
            RefreshKeys();
        }

        private void RemoveKey(string name)
        {
            if (_host == null) return;
            _host.Secrets.Remove(name);
            ShowKeyHint("Removed.");
            RefreshKeys();
        }

        private void ShowKeyHint(string text)
        {
            KeyHint.Text = text;
            KeyHint.Visibility = Visibility.Visible;
        }

        private void RefreshKeys()
        {
            if (_host == null) return;
            bool claude = _host.Secrets.Has(SecretNames.ClaudeKey);
            ClaudeKeyEntry.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
            ClaudeKeySaved.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;

            bool compatible = _host.Secrets.Has(SecretNames.CompatibleKey);
            CompatibleKeyEntry.Visibility = compatible ? Visibility.Collapsed : Visibility.Visible;
            CompatibleKeySaved.Visibility = compatible ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- test connection -----------------------------------------------------------------

        // The discard is deliberate: TestConnectionAsync catches every failure and shows it.
        private void OnTestConnection(object sender, RoutedEventArgs e) => _ = TestConnectionAsync();

        /// <summary>
        /// Sends one tiny request with the current settings, off the UI thread. It goes straight
        /// to the provider, not through the assistant, so it never counts toward the daily limit.
        /// </summary>
        internal async Task TestConnectionAsync()
        {
            if (_host == null) return;
            TestButton.IsEnabled = false;
            TestResultText.Text = "Testing\u2026";

            AiClientResult result;
            try
            {
                result = _host.CreateClient(_host.Config, _host.Secrets);
            }
            catch (Exception ex)
            {
                TestResultText.Text = AiErrorText.Describe(ex);
                TestButton.IsEnabled = true;
                return;
            }

            if (result.Client is not { } client)
            {
                TestResultText.Text = result.Problem ?? "The provider could not be set up.";
                TestButton.IsEnabled = true;
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                ChatResponse reply = await Task.Run(() => client.GetResponseAsync(
                    new[] { new ChatMessage(ChatRole.User, "Reply with the single word OK.") },
                    new ChatOptions { MaxOutputTokens = 32 },
                    timeout.Token));
                string text = reply.Text.Trim();
                TestResultText.Text = text.Length == 0
                    ? "Connected, but the model sent back no text."
                    : "Connected. The model replied: " + (text.Length > 60 ? text.Substring(0, 60) + "\u2026" : text);
            }
            catch (Exception ex)
            {
                TestResultText.Text = AiErrorText.Describe(ex);
            }
            finally
            {
                client.Dispose();
                TestButton.IsEnabled = true;
            }
        }

        // ---- shortcut and limit --------------------------------------------------------------

        private void OnHotkeyChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string text = HotkeyBox.Text.Trim();

            if (text.Length == 0)
            {
                _host.Config.AiHotkey = "";
                HotkeyHint.Text = "Shortcut off. Ask MicaStats is still in the overlay's right-click menu.";
                _host.Save();
                return;
            }

            if (HotkeyParser.TryParse(text, out var mods, out uint vk))
            {
                string normal = HotkeyParser.Describe(mods, vk);
                HotkeyBox.Text = normal;
                HotkeyHint.Text = HotkeyHelp;
                _host.Config.AiHotkey = normal;
                _host.Save();
            }
            else
            {
                HotkeyHint.Text = "Not a valid shortcut. Use one or more of Ctrl, Alt, Shift, Win and one key, like Ctrl+Alt+A.";
            }
        }

        private void OnLimitChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (int.TryParse(LimitBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit))
            {
                _host.Config.AiDailyLimit = limit;
                LimitBox.Text = _host.Config.AiDailyLimit.ToString(CultureInfo.InvariantCulture);   // clamped by the setter
                _host.Save();
                RefreshUsage();
            }
            else
            {
                LimitHint.Text = "Enter a whole number from 1 to 10000.";
            }
        }

        private void RefreshUsage()
        {
            if (_host == null) return;
            var usage = _host.Usage();
            string used = usage == null
                ? ""
                : "Used today: " + usage.UsedToday.ToString(CultureInfo.InvariantCulture) + " of "
                  + _host.Config.AiDailyLimit.ToString(CultureInfo.InvariantCulture) + ". ";
            LimitHint.Text = used + "Each Send or Explain counts once, however many lookups it takes; Test connection does not count. Resets at midnight.";
        }

        // ---- history -------------------------------------------------------------------------

        private void OnHistoryToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            _host.Config.AiHistoryEnabled = HistoryToggle.IsOn;
            _host.Save();
        }

        private void OnDeleteHistory(object sender, RoutedEventArgs e)
        {
            if (_host?.History == null) return;
            _host.History.DeleteAll();
            RefreshHistory();
            HistoryHint.Text = "History deleted. " + HistoryHint.Text;
        }

        private void RefreshHistory()
        {
            if (_host == null) return;
            long bytes = _host.History?.SizeBytes() ?? 0;
            HistoryHint.Text = "One row a minute (every five minutes after a day) of CPU, memory, temperatures, disk, network, battery and the busiest process, so questions about the past can be answered. Kept on this PC for 7 days; using "
                               + FormatSize(bytes) + ".";
            DeleteHistoryButton.IsEnabled = _host.History != null;
        }

        /// <summary>A size in KB or MB, invariant culture.</summary>
        internal static string FormatSize(long bytes) =>
            bytes >= 1024 * 1024
                ? (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : Math.Ceiling(bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";

        // ---- MCP -----------------------------------------------------------------------------

        private void OnMcpModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || McpModeBox.SelectedIndex < 0) return;
            _host.Config.AiMcpMode = McpModeBox.SelectedIndex switch
            {
                1 => AiMcpModes.Stdio,
                2 => AiMcpModes.Http,
                _ => AiMcpModes.Off,
            };
            _host.Save();
            McpStatusText.Text = "";
            RefreshMcp();
            // The app starts or stops the servers on the change; read its verdict once it has.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RefreshMcp));
        }

        private void OnMcpPortChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (int.TryParse(McpPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
            {
                _host.Config.AiMcpHttpPort = port;
                McpPortBox.Text = _host.Config.AiMcpHttpPort.ToString(CultureInfo.InvariantCulture);   // clamped by the setter
                _host.Save();
                McpStatusText.Text = "";
                RefreshMcp();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RefreshMcp));
            }
            else
            {
                McpStatusText.Text = "Enter a port number from 1024 to 65535.";
            }
        }

        private void OnCopyToken(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            _host.CopyText(EnsureToken());
            McpStatusText.Text = "Token copied.";
        }

        private void OnRegenerateToken(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            _host.Secrets.Set(SecretNames.McpToken, SecretStore.NewToken());
            McpStatusText.Text = "New token made. Copy the Claude Code command again: the old token no longer works.";
        }

        private void OnCopyDesktopConfig(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            _host.CopyText(McpConfigSnippets.ClaudeDesktopJson(_host.ExePath));
            McpStatusText.Text = "Copied. In Claude Desktop open Settings > Developer > Edit Config, merge it into claude_desktop_config.json, then restart Claude Desktop.";
        }

        private void OnCopyCodeCommand(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            bool http = _host.Config.AiMcpMode == AiMcpModes.Http;
            _host.CopyText(http
                ? McpConfigSnippets.ClaudeCodeHttpCommand(_host.Config.AiMcpHttpPort, EnsureToken())
                : McpConfigSnippets.ClaudeCodeStdioCommand(_host.ExePath));
            McpStatusText.Text = "Copied. Run it in a terminal where Claude Code is installed.";
        }

        /// <summary>The MCP HTTP token, made and stored on first use.</summary>
        private string EnsureToken()
        {
            string? token = _host!.Secrets.Get(SecretNames.McpToken);
            if (string.IsNullOrEmpty(token))
            {
                token = SecretStore.NewToken();
                _host.Secrets.Set(SecretNames.McpToken, token);
            }
            return token;
        }

        private void RefreshMcp()
        {
            if (_host == null) return;
            string mode = _host.Config.AiMcpMode;
            string port = _host.Config.AiMcpHttpPort.ToString(CultureInfo.InvariantCulture);

            McpHttpPanel.Visibility = mode == AiMcpModes.Http ? Visibility.Visible : Visibility.Collapsed;
            // The Desktop config starts the stdio bridge, which forwards only while the mode is Stdio.
            CopyDesktopButton.IsEnabled = mode == AiMcpModes.Stdio;
            CopyCodeButton.IsEnabled = mode != AiMcpModes.Off;
            McpHint.Text = mode switch
            {
                AiMcpModes.Stdio => "Claude starts MicaStats.exe --mcp, which reads this running MicaStats over a private pipe. Nothing listens on the network. Read-only.",
                AiMcpModes.Http => "While MicaStats runs it answers MCP at http://127.0.0.1:" + port + "/mcp, on this PC only, for clients that send the token. Read-only. A client that starts MicaStats.exe --mcp sees only the files on disk in this mode.",
                _ => "Off. Claude Desktop and Claude Code cannot read MicaStats data.",
            };

            string? problem = mode == AiMcpModes.Http ? _host.McpHttpProblem() : null;
            McpProblemText.Text = problem ?? "";
            McpProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
