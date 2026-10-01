using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.History;
using Microsoft.Extensions.AI;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Settings > AI, built on the UI test thread over temp stores, a fake provider client and a
    /// recording clipboard. Nothing reaches %APPDATA%, the network or the real clipboard.
    /// </summary>
    public class AiSettingsTests
    {
        /// <summary>The panel's world: config, stores, and what it saved and copied.</summary>
        private sealed class Rig : IDisposable
        {
            public const string Exe = @"C:\Program Files\MicaStats\MicaStats.exe";

            public readonly AiTestEnv Env = new();
            public readonly AppConfig Config = new();
            public readonly SecretStore Secrets;
            public readonly HistoryStore History;
            public readonly UsageMeter Usage;
            public readonly List<string> Copied = new();
            public readonly List<string> CopiedSensitive = new();
            public Exception? CopyFailure;
            public int Saves;
            public string? McpProblem;
            public Func<AppConfig, SecretStore, AiClientResult> ClientFactory =
                (config, secrets) => new AiClientResult(null, "No provider in this test.", false);

            public Rig()
            {
                Secrets = new SecretStore(Env.PathOf("secrets.bin"));
                History = new HistoryStore(Env.PathOf("history"), () => Env.Clock.UtcNow);
                Usage = new UsageMeter(Env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Local));
            }

            public AiSettingsHost Host() => new()
            {
                Config = Config,
                Save = () => Saves++,
                Secrets = Secrets,
                History = History,
                Usage = () => Usage,
                McpHttpProblem = () => McpProblem,
                CreateClient = (config, secrets) => ClientFactory(config, secrets),
                CopyText = text =>
                {
                    if (CopyFailure != null) throw CopyFailure;
                    Copied.Add(text);
                },
                CopySensitive = text =>
                {
                    if (CopyFailure != null) throw CopyFailure;
                    Copied.Add(text);
                    CopiedSensitive.Add(text);
                },
                ExePath = Exe,
            };

            public void Dispose() => Env.Dispose();
        }

        /// <summary>A provider client that answers once, or throws.</summary>
        private sealed class ReplyClient : IChatClient
        {
            private readonly string? _reply;
            private readonly Exception? _failure;
            public int Calls;
            public ChatOptions? LastOptions;

            public ReplyClient(string? reply, Exception? failure = null)
            {
                _reply = reply;
                _failure = failure;
            }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                LastOptions = options;
                if (_failure != null) throw _failure;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Test connection asks for one reply, not a stream.");

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose() { }
        }

        private static void WithPanel(Action<AiSettingsPanel, Rig> test) => UiThread.Run(() =>
        {
            using var rig = new Rig();
            var panel = new AiSettingsPanel();
            panel.Load(rig.Host());
            test(panel, rig);
        });

        private static void Click(UIElement element) =>
            element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void LoseFocus(UIElement element) =>
            element.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

        /// <summary>Every piece of text the panel displays.</summary>
        private static IEnumerable<string> AllText(DependencyObject root)
        {
            if (root is TextBlock block) yield return block.Text;
            if (root is System.Windows.Controls.TextBox box) yield return box.Text;
            if (root is ContentControl control && control.Content is string content) yield return content;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject element)
                {
                    foreach (string text in AllText(element)) yield return text;
                }
            }
        }

        [Fact]
        public void Loading_fills_every_control_from_the_config() => WithPanel((panel, rig) =>
        {
            rig.Config.AiAssistantEnabled = true;
            rig.Config.AiProvider = AiProviders.OpenAiCompatible;
            rig.Config.AiCompatibleModel = "llama3.2";
            rig.Config.AiHotkey = "Ctrl+Alt+Q";
            rig.Config.AiDailyLimit = 40;
            rig.Config.AiHistoryEnabled = true;
            rig.Config.AiMcpMode = AiMcpModes.Http;
            rig.Config.AiMcpHttpPort = 50000;

            panel.Load(rig.Host());

            Assert.True(panel.AssistantToggle.IsOn);
            Assert.Equal(1, panel.ProviderBox.SelectedIndex);
            Assert.Equal(Visibility.Collapsed, panel.ClaudePanel.Visibility);
            Assert.Equal(Visibility.Visible, panel.CompatiblePanel.Visibility);
            Assert.Equal("http://localhost:11434/v1", panel.CompatibleUrlBox.Text);
            Assert.Equal("llama3.2", panel.CompatibleModelBox.Text);
            Assert.Equal("Ctrl+Alt+Q", panel.HotkeyBox.Text);
            Assert.Equal("40", panel.LimitBox.Text);
            Assert.True(panel.HistoryToggle.IsOn);
            Assert.Equal(2, panel.McpModeBox.SelectedIndex);
            Assert.Equal(Visibility.Visible, panel.McpHttpPanel.Visibility);
            Assert.Equal("50000", panel.McpPortBox.Text);
            Assert.Contains("stays on this PC", panel.PrivacyText.Text, StringComparison.Ordinal);
            Assert.Equal(0, rig.Saves);
        });

        [Fact]
        public void The_ask_theme_choice_loads_from_and_writes_to_the_config() => WithPanel((panel, rig) =>
        {
            rig.Config.AskTheme = "Light";
            panel.Load(rig.Host());
            Assert.Equal(1, panel.AskThemeBox.SelectedIndex);
            Assert.Equal(0, rig.Saves);

            panel.AskThemeBox.SelectedIndex = 0;
            Assert.Equal("Dark", rig.Config.AskTheme);
            Assert.Equal(1, rig.Saves);

            panel.AskThemeBox.SelectedIndex = 1;
            Assert.Equal("Light", rig.Config.AskTheme);
            Assert.Equal("Dark", rig.Config.PadTheme);   // MicaPad's own choice is a separate setting
        });

        [Fact]
        public void Changing_a_control_writes_the_config_and_saves() => WithPanel((panel, rig) =>
        {
            panel.AssistantToggle.IsOn = true;
            Assert.True(rig.Config.AiAssistantEnabled);

            panel.ProviderBox.SelectedIndex = 1;
            Assert.Equal(AiProviders.OpenAiCompatible, rig.Config.AiProvider);
            Assert.Equal(Visibility.Visible, panel.CompatiblePanel.Visibility);

            panel.CompatibleUrlBox.Text = "  https://openrouter.ai/api/v1 ";
            LoseFocus(panel.CompatibleUrlBox);
            Assert.Equal("https://openrouter.ai/api/v1", rig.Config.AiCompatibleBaseUrl);
            Assert.Contains("openrouter.ai", panel.PrivacyText.Text, StringComparison.Ordinal);

            panel.HistoryToggle.IsOn = true;
            Assert.True(rig.Config.AiHistoryEnabled);

            panel.LimitBox.Text = "50000";
            LoseFocus(panel.LimitBox);
            Assert.Equal(10000, rig.Config.AiDailyLimit);
            Assert.Equal("10000", panel.LimitBox.Text);

            panel.McpModeBox.SelectedIndex = 2;
            Assert.Equal(AiMcpModes.Http, rig.Config.AiMcpMode);
            Assert.Equal(Visibility.Visible, panel.McpHttpPanel.Visibility);

            panel.McpPortBox.Text = "80";
            LoseFocus(panel.McpPortBox);
            Assert.Equal(1024, rig.Config.AiMcpHttpPort);
            Assert.Equal("1024", panel.McpPortBox.Text);

            Assert.Equal(7, rig.Saves);
        });

        [Fact]
        public void The_shortcut_is_validated_like_micapads() => WithPanel((panel, rig) =>
        {
            panel.HotkeyBox.Text = "ctrl+alt+q";
            LoseFocus(panel.HotkeyBox);
            Assert.Equal("Ctrl+Alt+Q", rig.Config.AiHotkey);
            Assert.Equal("Ctrl+Alt+Q", panel.HotkeyBox.Text);

            panel.HotkeyBox.Text = "Banana";
            LoseFocus(panel.HotkeyBox);
            Assert.Equal("Ctrl+Alt+Q", rig.Config.AiHotkey);
            Assert.StartsWith("Not a valid shortcut", panel.HotkeyHint.Text, StringComparison.Ordinal);

            panel.HotkeyBox.Text = "";
            LoseFocus(panel.HotkeyBox);
            Assert.Equal("", rig.Config.AiHotkey);
            Assert.StartsWith("Shortcut off", panel.HotkeyHint.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void A_saved_key_goes_to_the_secret_store_and_is_never_shown() => WithPanel((panel, rig) =>
        {
            const string key = "sk-ant-test-0123456789abcdef";
            Assert.Equal(Visibility.Visible, panel.ClaudeKeyEntry.Visibility);

            panel.ClaudeKeyBox.Password = key;
            Click(panel.SaveClaudeKeyButton);

            Assert.Equal(key, rig.Secrets.Get(SecretNames.ClaudeKey));
            Assert.Equal("", panel.ClaudeKeyBox.Password);
            Assert.Equal(Visibility.Collapsed, panel.ClaudeKeyEntry.Visibility);
            Assert.Equal(Visibility.Visible, panel.ClaudeKeySaved.Visibility);
            Assert.DoesNotContain(AllText(panel), text => text.Contains(key, StringComparison.Ordinal));
            Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(rig.Config), StringComparison.Ordinal);

            rig.Secrets.Set(SecretNames.CompatibleKey, "compatible-key-value");
            rig.Secrets.Set(SecretNames.McpToken, "mcp-token-value");
            panel.Load(rig.Host());

            Click(panel.RemoveClaudeKeyButton);

            Assert.False(rig.Secrets.Has(SecretNames.ClaudeKey));
            Assert.Equal("compatible-key-value", rig.Secrets.Get(SecretNames.CompatibleKey));
            Assert.Equal("mcp-token-value", rig.Secrets.Get(SecretNames.McpToken));
            Assert.StartsWith("Removed.", panel.KeyHint.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, panel.ClaudeKeyEntry.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.ClaudeKeySaved.Visibility);

            rig.Secrets.Remove(SecretNames.CompatibleKey);
            Click(panel.SaveCompatibleKeyButton);   // nothing typed

            Assert.False(rig.Secrets.Has(SecretNames.CompatibleKey));
            Assert.Equal(Visibility.Visible, panel.KeyHint.Visibility);
        });

        [Fact]
        public void Test_connection_sends_one_small_request_and_is_not_counted() => WithPanel((panel, rig) =>
        {
            var client = new ReplyClient("OK");
            rig.ClientFactory = (config, secrets) => new AiClientResult(client, null, true);
            string usageBefore = panel.LimitHint.Text;

            UiPump.Wait(panel.TestConnectionAsync());

            Assert.Equal("Connected. The model replied: OK", panel.TestResultText.Text);
            Assert.Equal(1, client.Calls);
            Assert.True(client.LastOptions?.MaxOutputTokens <= 32);
            Assert.Equal(0, rig.Usage.UsedToday);
            Assert.Equal(usageBefore, panel.LimitHint.Text);
            Assert.True(panel.TestButton.IsEnabled);
        });

        [Fact]
        public void Test_connection_reports_a_setup_problem_or_a_failure_in_words() => WithPanel((panel, rig) =>
        {
            rig.ClientFactory = (config, secrets) => new AiClientResult(null, "Add an API key in Settings > AI.", true);
            UiPump.Wait(panel.TestConnectionAsync());
            Assert.Equal("Add an API key in Settings > AI.", panel.TestResultText.Text);

            var failure = new HttpRequestException("No route to host");
            rig.ClientFactory = (config, secrets) => new AiClientResult(new ReplyClient(null, failure), null, false);
            UiPump.Wait(panel.TestConnectionAsync());
            Assert.Equal(AiErrorText.Describe(failure), panel.TestResultText.Text);
        });

        [Fact]
        public void The_copy_buttons_copy_the_snippets_for_the_chosen_mode() => WithPanel((panel, rig) =>
        {
            Assert.False(panel.CopyDesktopButton.IsEnabled);
            Assert.False(panel.CopyCodeButton.IsEnabled);

            panel.McpModeBox.SelectedIndex = 1;   // stdio bridge
            Click(panel.CopyDesktopButton);
            Click(panel.CopyCodeButton);

            Assert.Equal(new[]
            {
                McpConfigSnippets.ClaudeDesktopJson(Rig.Exe),
                McpConfigSnippets.ClaudeCodeStdioCommand(Rig.Exe),
            }, rig.Copied);

            panel.McpModeBox.SelectedIndex = 2;   // local HTTP
            Assert.False(panel.CopyDesktopButton.IsEnabled);
            Click(panel.CopyCodeButton);

            string? token = rig.Secrets.Get(SecretNames.McpToken);
            Assert.False(string.IsNullOrEmpty(token));
            Assert.Equal(McpConfigSnippets.ClaudeCodeHttpCommand(47831, token!), rig.Copied[^1]);
        });

        [Fact]
        public void Regenerate_replaces_the_token_and_the_token_is_never_shown() => WithPanel((panel, rig) =>
        {
            panel.McpModeBox.SelectedIndex = 2;
            Click(panel.CopyTokenButton);
            string first = rig.Copied[^1];
            Assert.Equal(first, rig.Secrets.Get(SecretNames.McpToken));

            Click(panel.RegenerateTokenButton);

            string? second = rig.Secrets.Get(SecretNames.McpToken);
            Assert.False(string.IsNullOrEmpty(second));
            Assert.NotEqual(first, second);
            Assert.DoesNotContain(AllText(panel),
                text => text.Contains(first, StringComparison.Ordinal) || text.Contains(second!, StringComparison.Ordinal));
        });

        [Fact]
        public void A_port_problem_from_the_app_is_shown_in_http_mode() => WithPanel((panel, rig) =>
        {
            rig.McpProblem = "Port 47831 is in use.";
            rig.Config.AiMcpMode = AiMcpModes.Http;
            panel.Load(rig.Host());

            Assert.Equal("Port 47831 is in use.", panel.McpProblemText.Text);
            Assert.Equal(Visibility.Visible, panel.McpProblemText.Visibility);

            panel.McpModeBox.SelectedIndex = 1;

            Assert.Equal(Visibility.Collapsed, panel.McpProblemText.Visibility);
        });

        [Fact]
        public void A_problem_the_app_finds_after_a_change_is_read_once_its_queued_work_has_run() => WithPanel((panel, rig) =>
        {
            // App reacts to an Ai* change with work queued at Normal priority (ApplyAiSettings), and that
            // work is what sets the problem; reading it straight after the change would show the old answer.
            rig.Config.PropertyChanged += (s, e) =>
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => rig.McpProblem = "Port 47831 is in use."));

            panel.McpModeBox.SelectedIndex = 2;
            Assert.Equal(Visibility.Collapsed, panel.McpProblemText.Visibility);

            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal("Port 47831 is in use.", panel.McpProblemText.Text);
            Assert.Equal(Visibility.Visible, panel.McpProblemText.Visibility);

            // The same for a new port: the app clears the problem, and the panel reads that afterwards too.
            rig.Config.PropertyChanged += (s, e) =>
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => rig.McpProblem = null));
            panel.McpPortBox.Text = "50000";
            LoseFocus(panel.McpPortBox);
            Assert.Equal(Visibility.Visible, panel.McpProblemText.Visibility);

            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(Visibility.Collapsed, panel.McpProblemText.Visibility);
        });

        [Fact]
        public void Delete_history_empties_the_store_and_says_so() => WithPanel((panel, rig) =>
        {
            rig.History.Append(new HistoryRow
            {
                Utc = new DateTime(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc),
                Seconds = 60,
                CpuAvg = 12.5f,
                CpuMax = 40f,
            });
            Assert.True(rig.History.SizeBytes() > 0);

            Click(panel.DeleteHistoryButton);

            Assert.Equal(0, rig.History.SizeBytes());
            Assert.StartsWith("History deleted.", panel.HistoryHint.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void The_limit_line_shows_todays_count() => WithPanel((panel, rig) =>
        {
            Assert.True(rig.Usage.TryConsume(100));
            panel.Load(rig.Host());

            Assert.StartsWith("Used today: 1 of 100.", panel.LimitHint.Text, StringComparison.Ordinal);
        });

        /// <summary>Holds a file open and unshareable, the way another program (or a scanner) can.</summary>
        private static FileStream Lock(string path) =>
            new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        [Fact]
        public void A_busy_clipboard_becomes_a_sentence_not_a_crash() => WithPanel((panel, rig) =>
        {
            rig.CopyFailure = new System.Runtime.InteropServices.COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0));
            panel.McpModeBox.SelectedIndex = 2;

            Click(panel.CopyTokenButton);
            Assert.Equal("The clipboard is busy. Try again.", panel.McpStatusText.Text);

            panel.McpStatusText.Text = "";
            Click(panel.CopyCodeButton);
            Assert.Equal("The clipboard is busy. Try again.", panel.McpStatusText.Text);

            panel.McpModeBox.SelectedIndex = 1;
            panel.McpStatusText.Text = "";
            Click(panel.CopyDesktopButton);
            Assert.Equal("The clipboard is busy. Try again.", panel.McpStatusText.Text);

            rig.CopyFailure = new System.Runtime.InteropServices.ExternalException("busy");
            panel.McpStatusText.Text = "";
            Click(panel.CopyCodeButton);
            Assert.Equal("The clipboard is busy. Try again.", panel.McpStatusText.Text);
            Assert.Empty(rig.Copied);
        });

        [Fact]
        public void Only_the_token_and_the_http_command_take_the_sensitive_clipboard_path() => WithPanel((panel, rig) =>
        {
            panel.McpModeBox.SelectedIndex = 1;
            Click(panel.CopyDesktopButton);
            Click(panel.CopyCodeButton);
            Assert.Equal(2, rig.Copied.Count);
            Assert.Empty(rig.CopiedSensitive);

            panel.McpModeBox.SelectedIndex = 2;
            Click(panel.CopyTokenButton);
            Click(panel.CopyCodeButton);

            string token = rig.Secrets.Get(SecretNames.McpToken)!;
            Assert.Equal(new[] { token, McpConfigSnippets.ClaudeCodeHttpCommand(47831, token) }, rig.CopiedSensitive);
        });

        [Fact]
        public void A_key_that_could_not_be_stored_is_not_called_saved_and_stays_in_the_box() => WithPanel((panel, rig) =>
        {
            rig.Secrets.Set(SecretNames.CompatibleKey, "existing");   // makes the file exist so it can be locked
            panel.Load(rig.Host());
            const string key = "sk-ant-test-0123456789abcdef";
            panel.ClaudeKeyBox.Password = key;

            using (Lock(rig.Env.PathOf("secrets.bin")))
            {
                Click(panel.SaveClaudeKeyButton);
            }

            Assert.StartsWith("The key could not be stored.", panel.KeyHint.Text, StringComparison.Ordinal);
            Assert.Equal(key, panel.ClaudeKeyBox.Password);
            Assert.Equal(Visibility.Visible, panel.ClaudeKeyEntry.Visibility);

            Click(panel.SaveClaudeKeyButton);   // the file is free again

            Assert.Equal(key, rig.Secrets.Get(SecretNames.ClaudeKey));
            Assert.StartsWith("Saved.", panel.KeyHint.Text, StringComparison.Ordinal);
            Assert.Equal("", panel.ClaudeKeyBox.Password);
        });

        [Fact]
        public void A_key_that_could_not_be_removed_is_not_called_removed() => WithPanel((panel, rig) =>
        {
            rig.Secrets.Set(SecretNames.ClaudeKey, "sk-ant-test-0123456789abcdef");
            panel.Load(rig.Host());

            using (Lock(rig.Env.PathOf("secrets.bin")))
            {
                Click(panel.RemoveClaudeKeyButton);
            }

            Assert.StartsWith("The key could not be removed.", panel.KeyHint.Text, StringComparison.Ordinal);
            Assert.True(rig.Secrets.Has(SecretNames.ClaudeKey));
        });

        [Fact]
        public void A_token_that_could_not_be_changed_or_stored_is_reported() => WithPanel((panel, rig) =>
        {
            panel.McpModeBox.SelectedIndex = 2;
            Click(panel.CopyTokenButton);
            string first = rig.Secrets.Get(SecretNames.McpToken)!;
            rig.Copied.Clear();

            using (Lock(rig.Env.PathOf("secrets.bin")))
            {
                Click(panel.RegenerateTokenButton);
            }

            Assert.Equal("The token could not be changed; the old one still works.", panel.McpStatusText.Text);
            Assert.Equal(first, rig.Secrets.Get(SecretNames.McpToken));

            // With no saved token and the file locked, a token cannot be made and kept, so nothing
            // is copied (a Claude key keeps the file in existence so it can be locked).
            rig.Secrets.Remove(SecretNames.McpToken);
            rig.Secrets.Set(SecretNames.ClaudeKey, "sk-ant-test-0123456789abcdef");
            using (Lock(rig.Env.PathOf("secrets.bin")))
            {
                Click(panel.CopyTokenButton);
                Assert.Equal("The token could not be stored, so nothing was copied. Try again.", panel.McpStatusText.Text);
                panel.McpStatusText.Text = "";
                Click(panel.CopyCodeButton);
                Assert.Equal("The token could not be stored, so nothing was copied. Try again.", panel.McpStatusText.Text);
            }

            Assert.Empty(rig.Copied);
        });

        [Fact]
        public void History_that_could_not_be_deleted_is_not_called_deleted() => WithPanel((panel, rig) =>
        {
            rig.History.Append(new HistoryRow
            {
                Utc = new DateTime(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc),
                Seconds = 60,
                CpuAvg = 12.5f,
                CpuMax = 40f,
            });
            string file = Directory.GetFiles(rig.History.Folder)[0];

            using (Lock(file))
            {
                Click(panel.DeleteHistoryButton);
            }

            Assert.StartsWith("History could not be deleted completely.", panel.HistoryHint.Text, StringComparison.Ordinal);
            Assert.True(rig.History.SizeBytes() > 0);
        });

        [Fact]
        public void A_test_result_that_arrives_after_a_provider_switch_is_dropped() => WithPanel((panel, rig) =>
        {
            var gate = new TaskCompletionSource<bool>();
            rig.ClientFactory = (config, secrets) => new AiClientResult(new SlowClient(gate.Task), null, true);

            Task running = panel.TestConnectionAsync();
            panel.ProviderBox.SelectedIndex = 1;
            Assert.Equal("", panel.TestResultText.Text);
            Assert.True(panel.TestButton.IsEnabled);

            gate.SetResult(true);
            UiPump.Wait(running);

            Assert.Equal("", panel.TestResultText.Text);
        });

        [Fact]
        public void The_key_hint_about_one_provider_is_hidden_after_switching_to_the_other() => WithPanel((panel, rig) =>
        {
            panel.ClaudeKeyBox.Password = "sk-ant-test-0123456789abcdef";
            Click(panel.SaveClaudeKeyButton);
            Assert.Equal(Visibility.Visible, panel.KeyHint.Visibility);

            panel.ProviderBox.SelectedIndex = 1;

            Assert.Equal(Visibility.Collapsed, panel.KeyHint.Visibility);
        });

        /// <summary>A client whose reply waits for a gate.</summary>
        private sealed class SlowClient : IChatClient
        {
            private readonly Task _gate;
            public SlowClient(Task gate) => _gate = gate;

            public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                await _gate.ConfigureAwait(false);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }

        [Fact]
        public void Settings_has_an_ai_section()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));

            Assert.Contains("Tag=\"AI\"", xaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"AiSection\"", xaml, StringComparison.Ordinal);
            Assert.Contains("<ai:AiSettingsPanel x:Name=\"AiPanel\"", xaml, StringComparison.Ordinal);
        }
    }

    /// <summary>The one line that says where questions go, written from the settings alone.</summary>
    public class AiPrivacyNoteTests
    {
        private const string Removed = " Your profile folder, computer name, user name and IP addresses are removed first.";

        [Fact]
        public void Claude_goes_to_anthropic()
        {
            Assert.Equal("Questions and the PC data they need go to Anthropic (api.anthropic.com)." + Removed,
                AiPrivacyNote.Describe(AiProviders.Claude, "http://localhost:11434/v1"));
        }

        [Theory]
        [InlineData("http://localhost:11434/v1", "localhost")]
        [InlineData("http://127.0.0.1:1234/v1", "127.0.0.1")]
        [InlineData("http://[::1]:8080/v1", "[::1]")]
        public void A_loopback_server_stays_on_this_pc(string url, string host)
        {
            Assert.Equal("Everything stays on this PC (" + host + ").", AiPrivacyNote.Describe(AiProviders.OpenAiCompatible, url));
        }

        [Theory]
        [InlineData("http://localhost.evil.com/v1", "localhost.evil.com")]
        [InlineData("http://127.0.0.1.nip.io/v1", "127.0.0.1.nip.io")]
        [InlineData("http://localhost@evil.com/v1", "evil.com")]
        public void A_lookalike_host_is_named_not_trusted(string url, string host)
        {
            Assert.Equal("Questions and the PC data they need go to " + host + "." + Removed,
                AiPrivacyNote.Describe(AiProviders.OpenAiCompatible, url));
        }

        [Fact]
        public void An_ipv4_mapped_loopback_stays_on_this_pc()
        {
            Assert.Equal("Everything stays on this PC ([::ffff:127.0.0.1]).",
                AiPrivacyNote.Describe(AiProviders.OpenAiCompatible, "http://[::ffff:127.0.0.1]:8080/v1"));
        }

        [Fact]
        public void A_remote_server_is_named()
        {
            Assert.Equal("Questions and the PC data they need go to api.openai.com." + Removed,
                AiPrivacyNote.Describe(AiProviders.OpenAiCompatible, "https://api.openai.com/v1"));
        }

        [Fact]
        public void A_broken_url_says_so()
        {
            Assert.Equal("The base URL is not a valid http or https address, so nothing can be sent.",
                AiPrivacyNote.Describe(AiProviders.OpenAiCompatible, "not a url"));
        }
    }
}
