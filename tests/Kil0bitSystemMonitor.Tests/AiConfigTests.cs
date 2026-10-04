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
            config.AiContextWindow = 32000;
            config.AiModelContext = 262144;
            config.AiModelOutput = 8192;
            config.AiModelLimitsOf = "OpenAiCompatible|http://127.0.0.1:1234|llama3.2";

            Assert.Equal(14, names.Distinct().Count());
            Assert.All(names, name => Assert.StartsWith("Ai", name, StringComparison.Ordinal));
        }

        // ----- The model's limits (spec 2026-10-05, 1.3 and 2.3) ------------------------------

        [Fact]
        public void The_model_limit_settings_start_as_auto_and_never_learned()
        {
            var config = new AppConfig();

            Assert.Equal(0, config.AiContextWindow);
            Assert.Equal(0, config.AiModelContext);
            Assert.Equal(0, config.AiModelOutput);
            Assert.Equal("", config.AiModelLimitsOf);
        }

        [Fact]
        public void An_older_config_without_the_model_limit_settings_gets_auto_and_never_learned()
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"AiClaudeModel\": \"claude-sonnet-5-5\"}")!;

            Assert.Equal("claude-sonnet-5-5", config.AiClaudeModel);
            Assert.Equal(0, config.AiContextWindow);
            Assert.Equal(0, config.AiModelContext);
            Assert.Equal(0, config.AiModelOutput);
            Assert.Equal("", config.AiModelLimitsOf);
        }

        [Theory]
        [InlineData(0, 0)]                       // Auto
        [InlineData(-1, 0)]
        [InlineData(int.MinValue, 0)]
        [InlineData(1, 1024)]
        [InlineData(1023, 1024)]
        [InlineData(1024, 1024)]
        [InlineData(262_144, 262_144)]
        [InlineData(2_000_000, 2_000_000)]
        [InlineData(2_000_001, 2_000_000)]
        [InlineData(int.MaxValue, 2_000_000)]
        public void The_users_context_window_is_auto_or_from_1024_to_two_million(int set, int expected)
        {
            var config = new AppConfig { AiContextWindow = 64_000 };

            config.AiContextWindow = set;

            Assert.Equal(expected, config.AiContextWindow);
        }

        [Theory]
        [InlineData(0, 0)]                       // not reported
        [InlineData(-5, 0)]
        [InlineData(int.MinValue, 0)]
        [InlineData(1, 0)]
        [InlineData(1023, 0)]
        [InlineData(1024, 1024)]
        [InlineData(262_144, 262_144)]
        [InlineData(2_000_000, 2_000_000)]
        [InlineData(2_000_001, 2_000_000)]
        [InlineData(int.MaxValue, 2_000_000)]
        public void A_learned_window_is_unknown_or_from_1024_to_two_million(int set, int expected)
        {
            var config = new AppConfig { AiModelContext = 64_000 };

            config.AiModelContext = set;

            Assert.Equal(expected, config.AiModelContext);
        }

        [Theory]
        [InlineData(0, 0)]                       // not reported
        [InlineData(-5, 0)]
        [InlineData(int.MinValue, 0)]
        [InlineData(1, 0)]
        [InlineData(255, 0)]
        [InlineData(256, 256)]
        [InlineData(8192, 8192)]
        [InlineData(2_000_000, 2_000_000)]
        [InlineData(2_000_001, 2_000_000)]
        [InlineData(int.MaxValue, 2_000_000)]
        public void A_learned_output_is_unknown_or_from_256_to_two_million(int set, int expected)
        {
            var config = new AppConfig { AiModelOutput = 4096 };

            config.AiModelOutput = set;

            Assert.Equal(expected, config.AiModelOutput);
        }

        [Fact]
        public void What_the_learned_limits_belong_to_is_trimmed_and_at_most_600_characters()
        {
            var config = new AppConfig();

            config.AiModelLimitsOf = "  Claude|https://api.anthropic.com|claude-haiku-4-5 \r\n";
            Assert.Equal("Claude|https://api.anthropic.com|claude-haiku-4-5", config.AiModelLimitsOf);

            config.AiModelLimitsOf = null!;
            Assert.Equal("", config.AiModelLimitsOf);

            config.AiModelLimitsOf = "   ";
            Assert.Equal("", config.AiModelLimitsOf);

            config.AiModelLimitsOf = new string('k', 600);
            Assert.Equal(600, config.AiModelLimitsOf.Length);

            config.AiModelLimitsOf = new string('k', 5000);
            Assert.Equal(new string('k', 600), config.AiModelLimitsOf);
        }

        [Fact]
        public void A_cut_at_600_characters_never_leaves_half_a_surrogate_pair()
        {
            // U+1F600 is two UTF-16 units, here at 599 and 600: the cut at 600 would keep only the first.
            string pair = char.ConvertFromUtf32(0x1F600);
            var config = new AppConfig { AiModelLimitsOf = new string('k', 599) + pair + "tail" };

            Assert.Equal(new string('k', 599), config.AiModelLimitsOf);
            // What is kept must be writable to config.json and come back the same.
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
            Assert.Equal(config.AiModelLimitsOf, back.AiModelLimitsOf);
        }

        [Fact]
        public void The_model_limit_settings_survive_a_round_trip()
        {
            var config = new AppConfig
            {
                AiContextWindow = 128_000,
                AiModelContext = 262_144,
                AiModelOutput = 16_384,
                AiModelLimitsOf = "OpenAiCompatible|https://llm.example.com:8443|org/Some-Model",
            };

            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

            Assert.Equal(128_000, back.AiContextWindow);
            Assert.Equal(262_144, back.AiModelContext);
            Assert.Equal(16_384, back.AiModelOutput);
            Assert.Equal("OpenAiCompatible|https://llm.example.com:8443|org/Some-Model", back.AiModelLimitsOf);
        }

        [Fact]
        public void A_config_file_with_limits_out_of_range_is_read_as_the_nearest_valid_ones()
        {
            // config.json is a file people edit by hand; what is read goes through the same setters.
            var config = JsonSerializer.Deserialize<AppConfig>(
                "{\"AiContextWindow\": -7, \"AiModelContext\": 12, \"AiModelOutput\": 99999999, \"AiModelLimitsOf\": null}")!;

            Assert.Equal(0, config.AiContextWindow);
            Assert.Equal(0, config.AiModelContext);
            Assert.Equal(2_000_000, config.AiModelOutput);
            Assert.Equal("", config.AiModelLimitsOf);

            var small = JsonSerializer.Deserialize<AppConfig>("{\"AiContextWindow\": 500, \"AiModelOutput\": 100}")!;
            Assert.Equal(1024, small.AiContextWindow);
            Assert.Equal(0, small.AiModelOutput);
        }

        [Fact]
        public void Changing_the_provider_the_model_or_the_address_keeps_the_learned_limits()
        {
            // They are not cleared: whoever reads them compares AiModelLimitsOf with ModelCatalog.KeyOf.
            var config = new AppConfig
            {
                AiContextWindow = 64_000,
                AiModelContext = 262_144,
                AiModelOutput = 16_384,
                AiModelLimitsOf = "Claude|https://api.anthropic.com|claude-haiku-4-5",
            };

            config.AiProvider = AiProviders.OpenAiCompatible;
            config.AiClaudeModel = "claude-sonnet-5-5";
            config.AiCompatibleBaseUrl = "https://llm.example.com/v1";
            config.AiCompatibleModel = "some-model";

            Assert.Equal(64_000, config.AiContextWindow);
            Assert.Equal(262_144, config.AiModelContext);
            Assert.Equal(16_384, config.AiModelOutput);
            Assert.Equal("Claude|https://api.anthropic.com|claude-haiku-4-5", config.AiModelLimitsOf);
        }

        [Fact]
        public void A_limit_set_to_the_value_it_has_notifies_nobody()
        {
            // App re-applies the AI wiring on every Ai* notice; a refresh that learns the same numbers must cost nothing.
            var config = new AppConfig
            {
                AiContextWindow = 64_000,
                AiModelContext = 262_144,
                AiModelOutput = 16_384,
                AiModelLimitsOf = "Claude|https://api.anthropic.com|claude-haiku-4-5",
            };
            int notices = 0;
            config.PropertyChanged += (s, e) => notices++;

            config.AiContextWindow = 64_000;
            config.AiModelContext = 262_144;
            config.AiModelOutput = 16_384;
            config.AiModelLimitsOf = " Claude|https://api.anthropic.com|claude-haiku-4-5 ";
            config.AiModelContext = 5_000_000;      // clamps to two million: one notice
            config.AiModelContext = 9_000_000;      // the same two million: none

            Assert.Equal(1, notices);
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
