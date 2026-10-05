using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Capture;
using Microsoft.Extensions.AI;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own UserControl.
using ComboBox = System.Windows.Controls.ComboBox;
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

        /// <summary>Counts Test connection runs; a result from an earlier run is dropped.</summary>
        private int _testRun;

        private int _modelRun;
        private CancellationTokenSource? _modelCancel;
        private IReadOnlyList<AiModelInfo> _models = Array.Empty<AiModelInfo>();
        internal sealed record ModelChoice(AiModelInfo Model)
        {
            public string Id => Model.Id;

            public string Display => Model.ContextTokens > 0
                ? Model.Id + " · " + Model.ContextTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens"
                : Model.Id;

            public override string ToString() => Id;
        }

        /// <summary>Builds the panel; nothing shows until <see cref="Load"/>.</summary>
        public AiSettingsPanel()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
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
                ShowContextWindow();
                HotkeyBox.Text = cfg.AiHotkey;
                HotkeyHint.Text = HotkeyHelp;
                LimitBox.Text = cfg.AiDailyLimit.ToString(CultureInfo.InvariantCulture);
                HistoryToggle.IsOn = cfg.AiHistoryEnabled;
                AskThemeBox.SelectedIndex = cfg.AskTheme == Kil0bitSystemMonitor.Services.Pad.PadThemes.Light ? 1 : 0;
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
            RefreshModelLimits();
            _ = RefreshModelsAsync();
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
            _testRun++;   // a Test connection still running belongs to the other provider
            TestResultText.Text = "";
            TestButton.IsEnabled = true;
            RefreshProvider();
            _ = RefreshModelsAsync();
        }

        private void OnClaudeModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string before = _host.Config.AiClaudeModel;
            _host.Config.AiClaudeModel = ClaudeModelBox.Text;
            ClaudeModelBox.Text = _host.Config.AiClaudeModel;   // the setter trims and restores the default when blank
            if (!string.Equals(before, _host.Config.AiClaudeModel, StringComparison.Ordinal))
            {
                ModelCatalog.Learn(_host.Config, _models);
                _host.Save();
                ModelChangedWithoutRequest();
            }
        }

        private void OnClaudeModelPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || ClaudeModelBox.SelectedItem is not ModelChoice choice) return;
            SavePickedModel(choice.Model, claude: true);
        }

        private void OnCompatibleUrlChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string before = _host.Config.AiCompatibleBaseUrl;
            _host.Config.AiCompatibleBaseUrl = CompatibleUrlBox.Text;
            CompatibleUrlBox.Text = _host.Config.AiCompatibleBaseUrl;
            _host.Save();
            RefreshProvider();
            if (!string.Equals(before, _host.Config.AiCompatibleBaseUrl, StringComparison.Ordinal))
                _ = RefreshModelsAsync();
        }

        private void OnCompatibleModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string before = _host.Config.AiCompatibleModel;
            _host.Config.AiCompatibleModel = CompatibleModelBox.Text;
            CompatibleModelBox.Text = _host.Config.AiCompatibleModel;
            if (!string.Equals(before, _host.Config.AiCompatibleModel, StringComparison.Ordinal))
            {
                ModelCatalog.Learn(_host.Config, _models);
                _host.Save();
                ModelChangedWithoutRequest();
            }
        }

        private void OnCompatibleModelPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || CompatibleModelBox.SelectedItem is not ModelChoice choice) return;
            SavePickedModel(choice.Model, claude: false);
        }

        private void SavePickedModel(AiModelInfo model, bool claude)
        {
            if (_host == null) return;
            bool wasLoading = _modelCancel != null;
            if (wasLoading) CancelModelList(clearStatus: true);
            _loading = true;
            try
            {
                if (claude)
                {
                    _host.Config.AiClaudeModel = model.Id;
                    ClaudeModelBox.Text = _host.Config.AiClaudeModel;
                }
                else
                {
                    _host.Config.AiCompatibleModel = model.Id;
                    CompatibleModelBox.Text = _host.Config.AiCompatibleModel;
                }
                ModelCatalog.Learn(_host.Config, new[] { model });
            }
            finally { _loading = false; }
            _host.Save();
            RefreshModelLimits();
        }

        private void ModelChangedWithoutRequest()
        {
            CancelModelList(clearStatus: _modelCancel != null);
            RefreshModelLimits();
        }

        private void RefreshProvider()
        {
            if (_host == null) return;
            KeyHint.Visibility = Visibility.Collapsed;   // it spoke about the other provider key
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
            if (key.Length == 0)
            {
                box.Clear();
                ShowKeyHint("Paste the key into the box first, then press Save.");
                return;
            }

            try
            {
                _host.Secrets.Set(name, key);
            }
            catch (Exception ex)
            {
                ShowKeyHint("The key could not be stored (" + ex.GetType().Name + "). It is still in the box; press Save to try again.");
                return;
            }

            // The store never throws for a locked or unwritable file, so believe only what it reads back.
            if (!string.Equals(_host.Secrets.Get(name), key, StringComparison.Ordinal))
            {
                ShowKeyHint("The key could not be stored. It is still in the box; press Save to try again.");
                return;
            }

            box.Clear();
            ShowKeyHint("Saved. The key is stored encrypted for your Windows account and is not shown again.");
            RefreshKeys();
            _ = RefreshModelsAsync();
        }

        private void RemoveKey(string name)
        {
            if (_host == null) return;
            _host.Secrets.Remove(name);
            // Has is also false while the file is locked, so the file must be readable too.
            if (!_host.Secrets.CanRead() || _host.Secrets.Has(name))
                ShowKeyHint("The key could not be removed. The file that holds it is not available; try again.");
            else
            {
                ShowKeyHint("Removed.");
                _ = RefreshModelsAsync();
            }
            RefreshKeys();
        }

        private void OnRefreshModels(object sender, RoutedEventArgs e) => _ = RefreshModelsAsync();

        internal async Task RefreshModelsAsync()
        {
            if (_host?.ListModels == null) return;
            CancelModelList(clearStatus: false);
            int run = ++_modelRun;
            var cancel = new CancellationTokenSource();
            _modelCancel = cancel;
            AppConfig snapshot = ModelListSnapshot(_host.Config);
            string key = ModelCatalog.KeyOf(snapshot);
            _models = Array.Empty<AiModelInfo>();
            FillModelBox(CurrentModelBox(), _models);
            ModelStatusText.Text = "Loading…";
            RefreshModelsButton.IsEnabled = false;

            AiModelList list;
            try
            {
                list = await _host.ListModels(snapshot, _host.Secrets, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                list = new AiModelList(Array.Empty<AiModelInfo>(), AiErrorText.Describe(ex), "");
            }
            finally
            {
                if (ReferenceEquals(_modelCancel, cancel)) _modelCancel = null;
                cancel.Dispose();
            }

            if (_host == null || run != _modelRun || !string.Equals(key, ModelCatalog.KeyOf(_host.Config), StringComparison.Ordinal)) return;
            _models = list.Models;
            FillModelBox(CurrentModelBox(), list.Models);
            ModelStatusText.Text = list.Problem == null
                ? list.Models.Count.ToString(CultureInfo.InvariantCulture) + (list.Models.Count == 1 ? " model from " : " models from ") + list.Host
                : list.Problem + " You can still type a model name.";
            RefreshModelsButton.IsEnabled = true;

            if (ModelCatalog.Learn(_host.Config, list.Models)) _host.Save();
            RefreshModelLimits();
        }

        private ComboBox CurrentModelBox() => _host?.Config.AiProvider == AiProviders.OpenAiCompatible
            ? CompatibleModelBox : ClaudeModelBox;

        private static AppConfig ModelListSnapshot(AppConfig config) => new AppConfig
        {
            AiProvider = config.AiProvider,
            AiClaudeModel = config.AiClaudeModel,
            AiCompatibleBaseUrl = config.AiCompatibleBaseUrl,
            AiCompatibleModel = config.AiCompatibleModel,
            AiAssistantEnabled = config.AiAssistantEnabled,
            PadAiEnabled = config.PadAiEnabled,
        };

        private void FillModelBox(ComboBox box, IReadOnlyList<AiModelInfo> models)
        {
            string text = box.Text;
            _loading = true;
            try
            {
                box.Items.Clear();
                foreach (AiModelInfo model in models) box.Items.Add(new ModelChoice(model));
                box.SelectedIndex = -1;
                box.Text = text;
            }
            finally { _loading = false; }
        }

        private void CancelModelList(bool clearStatus)
        {
            _modelRun++;
            _modelCancel?.Cancel();
            _modelCancel = null;
            RefreshModelsButton.IsEnabled = true;
            if (clearStatus) ModelStatusText.Text = "";
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => CancelModelList(clearStatus: false);

        private void OnContextWindowPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || ContextWindowBox.SelectedItem is not ComboBoxItem item) return;
            if (!int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return;
            SaveContextWindow(value, show: false);
        }

        private void OnContextWindowChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string text = ContextWindowBox.Text.Trim();
            if (string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                SaveContextWindow(0);
                return;
            }
            if (long.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long value))
            {
                SaveContextWindow(value > int.MaxValue ? int.MaxValue : (int)value);
                return;
            }
            ShowContextWindow();
        }

        private void SaveContextWindow(int value, bool show = true)
        {
            if (_host == null) return;
            int before = _host.Config.AiContextWindow;
            _host.Config.AiContextWindow = value;
            if (before != _host.Config.AiContextWindow) _host.Save();
            if (show) ShowContextWindow();
            RefreshModelLimits();
        }

        private void ShowContextWindow()
        {
            if (_host == null) return;
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                ContextWindowBox.SelectedIndex = -1;
                ContextWindowBox.Text = _host.Config.AiContextWindow == 0 ? "Auto"
                    : _host.Config.AiContextWindow.ToString("N0", CultureInfo.InvariantCulture);
            }
            finally { _loading = wasLoading; }
        }

        private AiBudget CurrentBudget()
        {
            if (_host == null) return AiBudget.Standard;
            bool matches = string.Equals(_host.Config.AiModelLimitsOf, ModelCatalog.KeyOf(_host.Config), StringComparison.Ordinal);
            return AiBudget.For(matches ? _host.Config.AiModelContext : 0,
                matches ? _host.Config.AiModelOutput : 0, _host.Config.AiContextWindow);
        }

        private void RefreshModelLimits()
        {
            if (_host == null) return;
            AiBudget budget = CurrentBudget();
            if (!budget.InTokens)
            {
                ModelLimitsText.Text = "Context window not known for this model: the standard limits are used. Set it below if you know it.";
                return;
            }
            string source = _host.Config.AiContextWindow > 0 ? "set here" : "from the server";
            ModelLimitsText.Text = "Context window " + budget.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)
                + " tokens (" + source + ") · answers up to " + budget.AskOutputTokens.ToString("N0", CultureInfo.InvariantCulture)
                + " tokens · MicaPad reads up to about " + budget.ReadInput.ToString("N0", CultureInfo.InvariantCulture) + " tokens of text";
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
            int run = ++_testRun;
            TestButton.IsEnabled = false;
            TestResultText.Text = "Testing\u2026";

            AiClientResult result;
            try
            {
                result = _host.CreateClient(_host.Config, _host.Secrets);
            }
            catch (Exception ex)
            {
                Report(run, AiErrorText.Describe(ex));
                return;
            }

            if (result.Client is not { } client)
            {
                Report(run, result.Problem ?? "The provider could not be set up.");
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
                string outcome = text.Length == 0
                    ? "Connected, but the model sent back no text."
                    : "Connected. The model replied: " + (text.Length > 60 ? text.Substring(0, 60) + "\u2026" : text);
                Report(run, outcome + ConnectionContextSuffix());
            }
            catch (Exception ex)
            {
                Report(run, AiErrorText.Describe(ex));
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>Shows a Test connection outcome and re-enables the button, unless a newer run or a provider switch replaced this run.</summary>
        private void Report(int run, string text)
        {
            if (run != _testRun) return;
            TestResultText.Text = text;
            TestButton.IsEnabled = true;
        }

        private string ConnectionContextSuffix()
        {
            AiBudget budget = CurrentBudget();
            return budget.InTokens
                ? " · " + budget.ContextTokens.ToString("N0", CultureInfo.InvariantCulture) + "-token context"
                : "";
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

        // ---- theme ---------------------------------------------------------------------------

        private void OnAskThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _host == null || AskThemeBox.SelectedIndex < 0) return;
            _host.Config.AskTheme = AskThemeBox.SelectedIndex == 1
                ? Kil0bitSystemMonitor.Services.Pad.PadThemes.Light
                : Kil0bitSystemMonitor.Services.Pad.PadThemes.Dark;
            _host.Save();
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
            // DeleteAll swallows I/O errors, so the size afterwards is the truth.
            HistoryHint.Text = (_host.History.SizeBytes() == 0
                ? "History deleted. "
                : "History could not be deleted completely. ") + HistoryHint.Text;
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
            string? token = EnsureToken();
            if (token == null) return;
            TryCopy(token, sensitive: true, "Token copied.");
        }

        private void OnRegenerateToken(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            string token = SecretStore.NewToken();
            _host.Secrets.Set(SecretNames.McpToken, token);
            McpStatusText.Text = string.Equals(_host.Secrets.Get(SecretNames.McpToken), token, StringComparison.Ordinal)
                ? "New token made. Copy the Claude Code command again: the old token no longer works."
                : "The token could not be changed; the old one still works.";
        }

        private void OnCopyDesktopConfig(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            TryCopy(McpConfigSnippets.ClaudeDesktopJson(_host.ExePath), sensitive: false,
                "Copied. In Claude Desktop open Settings > Developer > Edit Config, merge it into claude_desktop_config.json, then restart Claude Desktop.");
        }

        private void OnCopyCodeCommand(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            if (_host.Config.AiMcpMode == AiMcpModes.Http)
            {
                string? token = EnsureToken();
                if (token == null) return;
                TryCopy(McpConfigSnippets.ClaudeCodeHttpCommand(_host.Config.AiMcpHttpPort, token), sensitive: true,
                    "Copied. Run it in a terminal where Claude Code is installed.");
            }
            else
            {
                TryCopy(McpConfigSnippets.ClaudeCodeStdioCommand(_host.ExePath), sensitive: false,
                    "Copied. Run it in a terminal where Claude Code is installed.");
            }
        }

        /// <summary>
        /// Copies text and says so. A busy clipboard (another program holds it) is an everyday event,
        /// not a crash: it becomes a sentence. The text may hold the token, so it is never logged.
        /// </summary>
        private void TryCopy(string text, bool sensitive, string done)
        {
            try
            {
                if (sensitive) _host!.CopySensitive(text);
                else _host!.CopyText(text);
                McpStatusText.Text = done;
            }
            catch (ExternalException)
            {
                McpStatusText.Text = "The clipboard is busy. Try again.";
            }
        }

        /// <summary>
        /// The MCP HTTP token, made and stored on first use. Null, after saying so, when it could
        /// not be stored: a token that was never saved would be copied into a command that fails.
        /// </summary>
        private string? EnsureToken()
        {
            string? token = _host!.Secrets.Get(SecretNames.McpToken);
            if (!string.IsNullOrEmpty(token)) return token;

            token = SecretStore.NewToken();
            _host.Secrets.Set(SecretNames.McpToken, token);
            if (string.Equals(_host.Secrets.Get(SecretNames.McpToken), token, StringComparison.Ordinal)) return token;

            McpStatusText.Text = "The token could not be stored, so nothing was copied. Try again.";
            return null;
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
