using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The AI settings: everything off by default, older configs upgrade, values stay valid.</summary>
    public class AiConfigTests
    {
        [Fact]
        public void The_defaults_match_the_spec()
        {
            var config = new AppConfig();

            Assert.False(config.AiAssistantEnabled);
            Assert.Equal(AiProviders.Claude, config.AiProvider);
            Assert.Equal("claude-haiku-4-5", config.AiClaudeModel);
            Assert.Equal("http://localhost:11434/v1", config.AiCompatibleBaseUrl);
            Assert.Equal("", config.AiCompatibleModel);
            Assert.Equal("Ctrl+Alt+A", config.AiHotkey);
            Assert.Equal(100, config.AiDailyLimit);
            Assert.False(config.AiHistoryEnabled);
            Assert.Equal(AiMcpModes.Off, config.AiMcpMode);
            Assert.Equal(47831, config.AiMcpHttpPort);
        }

        [Fact]
        public void An_older_config_without_ai_settings_gets_the_defaults()
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!;

            Assert.False(config.ShowCpu);
            Assert.False(config.AiAssistantEnabled);
            Assert.Equal(AiProviders.Claude, config.AiProvider);
            Assert.Equal("Ctrl+Alt+A", config.AiHotkey);
            Assert.False(config.AiHistoryEnabled);
            Assert.Equal(AiMcpModes.Off, config.AiMcpMode);
            Assert.Equal(47831, config.AiMcpHttpPort);
        }

        [Fact]
        public void Ai_settings_survive_a_round_trip()
        {
            var config = new AppConfig
            {
                AiAssistantEnabled = true,
                AiProvider = AiProviders.OpenAiCompatible,
                AiClaudeModel = "claude-sonnet-5-5",
                AiCompatibleBaseUrl = "http://127.0.0.1:1234/v1",
                AiCompatibleModel = "llama3.2",
                AiHotkey = "Ctrl+Shift+A",
                AiDailyLimit = 250,
                AiHistoryEnabled = true,
                AiMcpMode = AiMcpModes.Http,
                AiMcpHttpPort = 48000,
            };

            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

            Assert.True(back.AiAssistantEnabled);
            Assert.Equal(AiProviders.OpenAiCompatible, back.AiProvider);
            Assert.Equal("claude-sonnet-5-5", back.AiClaudeModel);
            Assert.Equal("http://127.0.0.1:1234/v1", back.AiCompatibleBaseUrl);
            Assert.Equal("llama3.2", back.AiCompatibleModel);
            Assert.Equal("Ctrl+Shift+A", back.AiHotkey);
            Assert.Equal(250, back.AiDailyLimit);
            Assert.True(back.AiHistoryEnabled);
            Assert.Equal(AiMcpModes.Http, back.AiMcpMode);
            Assert.Equal(48000, back.AiMcpHttpPort);
        }

        [Fact]
        public void Unknown_choices_fall_back_and_numbers_stay_in_range()
        {
            var config = new AppConfig();

            config.AiProvider = "Gemini";
            Assert.Equal(AiProviders.Claude, config.AiProvider);
            config.AiProvider = "openaicompatible";
            Assert.Equal(AiProviders.OpenAiCompatible, config.AiProvider);
            config.AiProvider = null!;
            Assert.Equal(AiProviders.Claude, config.AiProvider);

            config.AiMcpMode = "stdio";
            Assert.Equal(AiMcpModes.Stdio, config.AiMcpMode);
            config.AiMcpMode = "HTTP";
            Assert.Equal(AiMcpModes.Http, config.AiMcpMode);
            config.AiMcpMode = "Pipe";
            Assert.Equal(AiMcpModes.Off, config.AiMcpMode);

            config.AiDailyLimit = 0;
            Assert.Equal(1, config.AiDailyLimit);
            config.AiDailyLimit = 50000;
            Assert.Equal(10000, config.AiDailyLimit);

            config.AiMcpHttpPort = 80;
            Assert.Equal(1024, config.AiMcpHttpPort);
            config.AiMcpHttpPort = 70000;
            Assert.Equal(65535, config.AiMcpHttpPort);
        }

        [Fact]
        public void Text_settings_are_trimmed_and_blank_ones_fall_back()
        {
            var config = new AppConfig();

            config.AiClaudeModel = "  claude-opus-5-5 ";
            Assert.Equal("claude-opus-5-5", config.AiClaudeModel);
            config.AiClaudeModel = "   ";
            Assert.Equal("claude-haiku-4-5", config.AiClaudeModel);

            config.AiCompatibleBaseUrl = " https://openrouter.ai/api/v1 ";
            Assert.Equal("https://openrouter.ai/api/v1", config.AiCompatibleBaseUrl);
            config.AiCompatibleBaseUrl = "";
            Assert.Equal("http://localhost:11434/v1", config.AiCompatibleBaseUrl);

            config.AiCompatibleModel = " qwen2.5 ";
            Assert.Equal("qwen2.5", config.AiCompatibleModel);
            config.AiCompatibleModel = null!;
            Assert.Equal("", config.AiCompatibleModel);

            config.AiHotkey = null!;
            Assert.Equal("", config.AiHotkey);
        }

        [Fact]
        public void Every_ai_setting_notifies_under_a_name_starting_with_ai()
        {
            // App re-applies the AI wiring for every property name that starts with "Ai".
            var config = new AppConfig();
            var names = new List<string>();
            config.PropertyChanged += (s, e) => names.Add(e.PropertyName!);

            config.AiAssistantEnabled = true;
            config.AiProvider = AiProviders.OpenAiCompatible;
            config.AiClaudeModel = "claude-sonnet-5-5";
            config.AiCompatibleBaseUrl = "http://127.0.0.1:1234/v1";
            config.AiCompatibleModel = "llama3.2";
            config.AiHotkey = "Ctrl+Shift+A";
            config.AiDailyLimit = 5;
            config.AiHistoryEnabled = true;
            config.AiMcpMode = AiMcpModes.Stdio;
            config.AiMcpHttpPort = 50000;

            Assert.Equal(10, names.Distinct().Count());
            Assert.All(names, name => Assert.StartsWith("Ai", name));
        }

        [Fact]
        public void The_default_hotkey_parses()
        {
            Assert.True(HotkeyParser.TryParse(new AppConfig().AiHotkey, out var modifiers, out uint key));
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, modifiers);
            Assert.Equal((uint)'A', key);
        }
    }
}
