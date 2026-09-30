# MicaStats AI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let MicaStats answer questions about the PC and explain what it measured — an *Ask MicaStats* window and one-click *Explain* buttons over Claude or any OpenAI-compatible endpoint, plus MicaStats as a read-only MCP data source for Claude Desktop and Claude Code — on top of a new 7-day metrics history, all opt-in from a new *AI* section in Settings.

**Architecture:** Pure, WPF-free services under `Services/History` (7-day history), `Services/Ai/Tools` (nine read-only data tools with one redactor), `Services/Ai` (provider factory, assistant loop, secrets, usage limit) and `Services/Ai/Mcp` (per-user named pipe, `MicaStats.exe --mcp` stdio bridge, loopback HTTP host), all unit-tested with xUnit. Thin WPF surfaces in `Ai/` (Ask window, Settings panel) plus Explain buttons in existing windows. All app wiring lives in `App.Ai.cs`.

**Tech Stack:** WPF on .NET 8 (`net8.0-windows`, framework-dependent), `Anthropic` 12.51.0, `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.AI.OpenAI` 10.10.1, `ModelContextProtocol.Core` 2.2.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micastats-ai-design.md`. Every SDK call in this plan was verified in a throwaway spike before planning (package versions, prompt caching through `IChatClient`, the tool loop with a fake client, MCP over stdio inside a WinExe, MCP over `HttpListener` without ASP.NET Core, the named pipe with a medium integrity label).

## Amendments to the spec (decided while planning)

1. **History top process:** each minute the recorder takes a short `ProcessSampler` lease (up to 6 s) and records `TopByCpu[0]` / `TopByRam[0]`; exe paths come from `ProcessPaths.TryGetPath` (QueryFullProcessImageNameW), stored already redacted. `ProcessUsage` has no cumulative CPU time, so the spec's "CPU-time delta over the minute" becomes "a 2-second sample each minute".
2. **Local HTTP MCP** runs on `HttpListener` bound to `http://127.0.0.1:{port}/mcp/` with the SDK's `StreamableHttpServerTransport { Stateless = true }` per request. No ASP.NET Core runtime is needed (verified).
3. **Packages:** `ModelContextProtocol.Core` only (not the hosting package). `ProtectedData` needs no package (it ships in the Windows Desktop reference pack).
4. **Alerts:** `AlertMonitor` keeps its last 50 raised events in memory (`Recent`) for `list_alerts`; there was no alert history before.
5. **Right-click menu:** the overlay's real menu is the native popup in `OverlayWindow.cs`; *Ask MicaStats…* is added there (`ContextMenuWindow` is unused).
6. **Process-window Explain** is a footer button acting on the selected process (rows have no buttons).
7. **Settings > AI** is a `UserControl` (`Ai/AiSettingsPanel`) hosted in a new `AiSection`, so it can be tested without the real config file.
8. **Anthropic caching** uses one 1-hour breakpoint at the end of the system prompt, which also caches the tool list (the API caches tools, then system, as a prefix).
9. **OpenAI-compatible hosts** receive `max_tokens` (older local servers ignore `max_completion_tokens`), except `*.openai.com` and `*.openai.azure.com`, which keep `max_completion_tokens`.
10. **`--mcp`** is handled right after the `--kill` block in `App.OnStartup`, before `base.OnStartup` and the single-instance mutex; the pipe server runs in the app only while MCP mode is *Stdio*; in *Local HTTP* mode a stdio bridge falls back to files (the docs say to pick the mode that matches the client).

## Global Constraints

- **Build and test only with the user-local SDK:** `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. There is no system .NET SDK on this machine; bare `dotnet` fails with "No .NET SDKs were found". Set `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Test command shape: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`; the whole suite without `--filter`.
- **Packages:** only `Anthropic` 12.51.0, `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.AI.OpenAI` 10.10.1 and `ModelContextProtocol.Core` 2.2.0 (added in Task 1). No other package. `<NoWarn>` gains `MEAI001;MCPEXP001;OPENAI001;SCME0001` (experimental-API ids the SDKs need), in the app and test projects.
- The first restore of the new packages can take longer than a 2-minute tool timeout: run it in the background and wait for it (Task 1 says how).
- **Nullable reference types are enabled** project-wide. Do not add `#nullable disable`.
- **Never format or parse a number or date with the ambient culture.** Use `CultureInfo.InvariantCulture`. The owner's Thai locale stamps Buddhist-era years (2569) through defaults.
- **String assertions in tests use ordinal comparison** (`StringComparison.Ordinal` or parsed JSON): culture-sensitive `Contains` under ICU can match text it should not.
- **`UseWindowsForms` is on**, so `System.Windows.Forms` and `System.Drawing` are implicit global usings in the app *and* the test project. In any WPF file, alias names that exist in both (`Button`, `Clipboard`, `TextBox`, `Application`, `Color`, `Brush`, …). If the compiler reports CS0104, add a `using X = System.Windows...X;` alias; never remove the WinForms reference.
- **WPF implicit usings do not include `System.IO` or `System.Net.Http`:** every file that uses them has explicit `using` lines. A file that imports `Anthropic.Models.Messages` must not rely on the name `Type` (it declares one).
- **XML doc comments on every public type and member**, house style: say *why* when the reason is not obvious. `Services/ProcessControl.cs` and `Services/ConfigService.cs` are the reference.
- **Tests never touch `%APPDATA%\MicaStats`.** Every store in a test is rooted in a temp folder (`AiTestEnv`, `PadTempDir`), and every test passes no-op or capturing `warn`/`error` callbacks so nothing reaches `DiagnosticsLog`.
- **No test calls a real AI provider or the network.** Provider tests use an offline `HttpMessageHandler`; the assistant uses a scripted fake `IChatClient`; MCP tests use in-memory streams or `HttpListener` on a free loopback port.
- **Do not launch MicaStats** during implementation (no test starts `MicaStats.exe`, including `--mcp`). The owner runs a production instance. Verification is build plus test suite; the manual checklist in Task 16 is for the owner.
- **The diagnostics log never receives questions, answers, tool data or secrets** — failures only (provider, status code, tool name).
- **Secrets never go into `config.json`**; they live in `%APPDATA%\MicaStats\secrets.bin` (DPAPI, current user).
- **Every commit message ends with a Co-Authored-By trailer** on its own line after a blank line, naming the model that wrote the commit as your harness attribution reminder gives it (the plan's commit blocks show `Claude Sonnet 5.5`; use your own model name).
- **Commit with `git commit -F -` and a heredoc, and keep apostrophes out of commit messages** (the shell wrapper breaks on them). Never `--amend`. Stage files by explicit path, never `git add -A` or `git add .`.
- Commit messages use Conventional Commits: `feat(ai): …`, `test(ai): …`, `docs(ai): …`, `fix(ai): …`.
- Thai text inside C# string literals is written as `\u` escapes (for example `"ส..."`), so source files stay ASCII-safe; Markdown docs may contain Thai directly.
- **Baseline suite: 817 tests** at the start (main `a38f121`). It must never go down; each task states the expected count after it.

## File structure

| Area | Files | Tasks |
| --- | --- | --- |
| Settings and secrets | `Models/SystemMetrics.cs` (AI block of `AppConfig`), `Services/Ai/AiSettings.cs` (`AiProviders`, `AiMcpModes`, `SecretNames`), `Services/Ai/SecretStore.cs`, `Kil0bitSystemMonitor.csproj`, tests `AiTestEnv.cs` | 1 |
| Redaction | `Services/Ai/Tools/Redactor.cs` | 2 |
| History | `Services/History/HistoryRow.cs`, `MinuteAggregator.cs`, `HistoryCsv.cs`, `HistoryStore.cs`, `ProcessPaths.cs`, `TopProcessSource.cs`, `HistoryRecorder.cs`; `Services/Diagnostics/AlertMonitor.cs` (`Recent`) | 3, 4, 5 |
| App wiring | `App.Ai.cs` (new, all AI wiring, anchors), `App.xaml.cs` (hook lines) | 5, 7, 10, 11, 12, 13 |
| Data tools | `Services/Ai/Tools/IMicaData.cs`, `ToolNames.cs`, `TimeRange.cs`, `ToolJson.cs`, `MicaTools.cs`, `MicaTools.Reports.cs`, `SlowdownReportFiles.cs`, `LiveMicaData.cs`, `OfflineMicaData.cs` | 6, 7 |
| Assistant | `Services/Ai/UsageMeter.cs`, `AiPrompts.cs`, `AiProviderFactory.cs`, `AiErrorText.cs`, `AiConversation.cs`, `AiAssistant.cs` (+ internal helpers) | 8 |
| Pipe and MCP | `Services/Ai/Mcp/ToolPipe*.cs`, `McpArguments.cs`, `McpToolSet.cs`, `McpBridge.cs`, `McpHttpHost.cs`, `McpConfigSnippets.cs` | 9, 10, 11 |
| UI | `Ai/AskWindow.xaml(.cs)`, `Ai/SuggestedActionRunner.cs`, `Services/Capture/CaptureHotkeys.cs`, `OverlayWindow.cs` (menu), `Services/Ai/ExplainQuestions.cs`, `DiagnosticsWindow`, `AlertToastWindow.cs`, `TaskManagerWindow`, `Ai/AiSettingsPanel.xaml(.cs)`, `SettingsWindow` | 12, 13, 14 |
| Docs | `README.md` (English and Thai), `GUIDE.md`, `ROADMAP.md` | 15 |

Expected suite size: 817 at the start; 913 after Task 5; 1018 after Task 8; 1103 after Task 11; 1153 after Task 14 (Tasks 15 and 16 add none); each task states its own count.

---

### Task 1: AI settings, SecretStore and the AI packages

Everything later needs three things first: the four AI packages (restored once, in the background, because the first restore downloads about 16 MB and can outrun a 2-minute tool timeout), the ten `Ai*` settings in `AppConfig` (all off by default, never a secret), and a DPAPI store for the API keys and the MCP token. This task also adds `AiTestEnv`, the temp-folder test helper every AI and history test uses.

**Files:**
- Modify: `Kil0bitSystemMonitor.csproj` (four PackageReferences, `<NoWarn>`)
- Modify: `tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj` (`<NoWarn>`)
- Create: `Services/Ai/AiSettings.cs` (`AiProviders`, `AiMcpModes`, `SecretNames`)
- Create: `Services/Ai/SecretStore.cs`
- Modify: `Models/SystemMetrics.cs` (class `AppConfig`, new `// ----- AI -----` block)
- Create: `tests/Kil0bitSystemMonitor.Tests/AiTestEnv.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/AiConfigTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiSecretStoreTests.cs`

**Interfaces:**
- Consumes: `Kil0bitSystemMonitor.Services.Pad.AtomicFile.Write(string path, byte[] bytes)`, `AtomicFile.ReadText(string path)`, `AtomicFile.ReadySuffix` (exist); `DiagnosticsLog.DataDir` (exists); `HotkeyParser.TryParse(string?, out HotkeyModifiers, out uint)` (exists); test `FakeClock` in `PadTestEnv.cs` (exists).
- Produces:
  - `namespace Kil0bitSystemMonitor.Services.Ai`: `public static class AiProviders { const string Claude = "Claude"; const string OpenAiCompatible = "OpenAiCompatible"; }`, `public static class AiMcpModes { const string Off = "Off"; const string Stdio = "Stdio"; const string Http = "Http"; }`, `public static class SecretNames { const string ClaudeKey = "claude-key"; const string CompatibleKey = "compatible-key"; const string McpToken = "mcp-token"; }`
  - `public sealed class SecretStore { SecretStore(string path, Action<string>? warn = null); static string DefaultPath { get; } /* %APPDATA%\MicaStats\secrets.bin */; bool Has(string name); string? Get(string name); void Set(string name, string value); void Remove(string name); static string NewToken(); }`. No cache: every call reads the file; `Set`/`Remove` read-modify-write under a lock shared by every instance on the same path, so short-lived instances are safe. Never throws.
  - `AppConfig`: `bool AiAssistantEnabled` (false), `string AiProvider` (`AiProviders.Claude`; OpenAiCompatible stays, anything else becomes Claude), `string AiClaudeModel` ("claude-haiku-4-5"; trimmed, blank becomes the default), `string AiCompatibleBaseUrl` ("http://localhost:11434/v1"; trimmed, blank becomes the default), `string AiCompatibleModel` (""; trimmed, null becomes ""), `string AiHotkey` ("Ctrl+Alt+A"; null becomes ""), `int AiDailyLimit` (100, clamped 1-10000), `bool AiHistoryEnabled` (false), `string AiMcpMode` (`AiMcpModes.Off`; Stdio/Http stay, anything else becomes Off), `int AiMcpHttpPort` (47831, clamped 1024-65535). Each raises `PropertyChanged` with its own name (all start with "Ai").
  - Test helper `internal sealed class AiTestEnv : IDisposable { string Root; string PathOf(string name); FakeClock Clock /* UtcNow { get; set; }, Advance(double seconds); starts 2026-09-30 10:00:00Z */; Action<string> Warn; IReadOnlyList<string> Warnings; void Dispose(); }`

- [ ] **Step 1: Add the AI packages and the experimental-API ids**

In `Kil0bitSystemMonitor.csproj`, replace:

```xml
        <!-- App Options -->
        <ApplicationManifest>app.manifest</ApplicationManifest>
    </PropertyGroup>
```

with:

```xml
        <!-- App Options -->
        <ApplicationManifest>app.manifest</ApplicationManifest>

        <!--
          The AI packages mark some members experimental, and using one is a build error until its
          id is listed here. MEAI001: Microsoft.Extensions.AI. MCPEXP001: ModelContextProtocol.
          OPENAI001: the OpenAI SDK. SCME0001: System.ClientModel JSON patching
          (ChatCompletionOptions.Patch, which sends max_tokens to older OpenAI-compatible servers).
        -->
        <NoWarn>$(NoWarn);MEAI001;MCPEXP001;OPENAI001;SCME0001</NoWarn>
    </PropertyGroup>
```

Then replace:

```xml
        <PackageReference Include="System.Management" Version="8.0.0" />
    </ItemGroup>
```

with:

```xml
        <PackageReference Include="System.Management" Version="8.0.0" />
    </ItemGroup>

    <!--
      MicaStats AI. Anthropic: the official Claude SDK, usable as an IChatClient.
      Microsoft.Extensions.AI and .OpenAI: the chat abstraction, tool calling and every
      OpenAI-compatible endpoint. ModelContextProtocol.Core: the MCP server behind the stdio bridge
      and local HTTP mode; the ASP.NET Core hosting package is deliberately not used.
    -->
    <ItemGroup>
        <PackageReference Include="Anthropic" Version="12.51.0" />
        <PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" />
        <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.10.1" />
        <PackageReference Include="ModelContextProtocol.Core" Version="2.2.0" />
    </ItemGroup>
```

In `tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`, replace:

```xml
        <IsPublishable>false</IsPublishable>
    </PropertyGroup>
```

with:

```xml
        <IsPublishable>false</IsPublishable>
        <!-- The same experimental-API ids as the app (see Kil0bitSystemMonitor.csproj), for tests that use those APIs. -->
        <NoWarn>$(NoWarn);MEAI001;MCPEXP001;OPENAI001;SCME0001</NoWarn>
    </PropertyGroup>
```

- [ ] **Step 2: Restore in the background**

The first restore downloads the packages and can take longer than a 2-minute tool timeout, so start it in the background (Bash tool: `run_in_background: true`) and wait for its completion notice before going on:

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" restore tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: exit code 0, `Restored ...\Kil0bitSystemMonitor.csproj` and `Restored ...\Kil0bitSystemMonitor.Tests.csproj`, no `error NU` lines. `OpenAI` 2.14.0 and `System.Text.Json` / `Microsoft.Extensions.*` 10.0.x arrive as dependencies.

- [ ] **Step 3: Confirm the packages alone change nothing**

The packages bring System.Text.Json 10, which now serializes `config.json`; the existing suite covers that.

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --no-restore`
Expected: build succeeds with 0 errors; 817 tests pass.

- [ ] **Step 4: Write the test helper and the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiTestEnv.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A throwaway folder, a clock moved by hand and a warning collector for the AI and history
    /// tests. Nothing here reaches %APPDATA% or DiagnosticsLog.
    /// </summary>
    internal sealed class AiTestEnv : IDisposable
    {
        private readonly List<string> _warnings = new();

        public AiTestEnv()
        {
            Root = Path.Combine(Path.GetTempPath(), "micastats-ai-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Warn = message =>
            {
                lock (_warnings) _warnings.Add(message);
            };
        }

        /// <summary>The folder, created by the constructor and deleted by <see cref="Dispose"/>.</summary>
        public string Root { get; }

        /// <summary>A path inside the folder; nothing is created.</summary>
        public string PathOf(string name) => Path.Combine(Root, name);

        /// <summary>The clock handed to stores and recorders; starts at 2026-09-30 10:00:00 UTC.</summary>
        public FakeClock Clock { get; } = new() { UtcNow = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc) };

        /// <summary>A warn callback that only records, safe from any thread.</summary>
        public Action<string> Warn { get; }

        /// <summary>Every message passed to <see cref="Warn"/> so far.</summary>
        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiConfigTests.cs`:

```csharp
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
```

Create `tests/Kil0bitSystemMonitor.Tests/AiSecretStoreTests.cs`:

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The DPAPI secret store: round trip, nothing in clear, never a throw, never a secret in config.</summary>
    public class AiSecretStoreTests
    {
        [Fact]
        public void A_saved_secret_reads_back_in_a_new_store()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");

            new SecretStore(path, env.Warn).Set(SecretNames.ClaudeKey, "sk-ant-test-123");
            var again = new SecretStore(path, env.Warn);

            Assert.True(again.Has(SecretNames.ClaudeKey));
            Assert.Equal("sk-ant-test-123", again.Get(SecretNames.ClaudeKey));
            Assert.False(again.Has(SecretNames.CompatibleKey));
            Assert.Null(again.Get(SecretNames.CompatibleKey));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Two_stores_on_one_file_never_drop_each_others_entries()
        {
            // The app creates a short-lived store per MCP HTTP request while Settings holds its own.
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            var settings = new SecretStore(path, env.Warn);
            var request = new SecretStore(path, env.Warn);

            settings.Set(SecretNames.ClaudeKey, "sk-ant-1");
            request.Set(SecretNames.McpToken, "token-1");

            Assert.Equal("token-1", settings.Get(SecretNames.McpToken));
            Assert.Equal("sk-ant-1", request.Get(SecretNames.ClaudeKey));

            Parallel.For(0, 40, i =>
            {
                var store = i % 2 == 0 ? settings : new SecretStore(path, env.Warn);
                store.Set("name-" + i, "value-" + i);
            });

            var fresh = new SecretStore(path, env.Warn);
            for (int i = 0; i < 40; i++) Assert.Equal("value-" + i, fresh.Get("name-" + i));
            Assert.Equal("sk-ant-1", fresh.Get(SecretNames.ClaudeKey));
            Assert.Equal("token-1", fresh.Get(SecretNames.McpToken));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void The_file_never_holds_the_secret_in_clear_and_needs_the_micastats_entropy()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            new SecretStore(path, env.Warn).Set(SecretNames.McpToken, "plain-token-value");

            string text = File.ReadAllText(path);
            byte[] sealedBytes = Convert.FromBase64String(text);

            Assert.DoesNotContain("plain-token-value", text);
            Assert.DoesNotContain("plain-token-value", Encoding.UTF8.GetString(sealedBytes));
            Assert.ThrowsAny<CryptographicException>(() =>
                ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser));
            byte[] plain = ProtectedData.Unprotect(sealedBytes, Encoding.UTF8.GetBytes("MicaStats.Secrets.v1"),
                                                   DataProtectionScope.CurrentUser);
            Assert.Contains("plain-token-value", Encoding.UTF8.GetString(plain));
        }

        [Fact]
        public void Values_are_trimmed_and_a_blank_value_removes_the_secret()
        {
            using var env = new AiTestEnv();
            var store = new SecretStore(env.PathOf("secrets.bin"), env.Warn);

            store.Set(SecretNames.CompatibleKey, "  sk-or-123  ");
            Assert.Equal("sk-or-123", store.Get(SecretNames.CompatibleKey));

            store.Set(SecretNames.CompatibleKey, "   ");
            Assert.False(store.Has(SecretNames.CompatibleKey));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Remove_keeps_the_others_and_removing_the_last_deletes_the_file()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            var store = new SecretStore(path, env.Warn);
            store.Set(SecretNames.ClaudeKey, "a");
            store.Set(SecretNames.McpToken, "b");

            store.Remove(SecretNames.ClaudeKey);

            Assert.Null(store.Get(SecretNames.ClaudeKey));
            Assert.Equal("b", store.Get(SecretNames.McpToken));

            store.Remove(SecretNames.McpToken);
            Assert.False(File.Exists(path));

            store.Remove(SecretNames.McpToken);   // nothing left: still no error
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void A_missing_file_reads_as_empty_and_creates_nothing()
        {
            using var env = new AiTestEnv();
            var store = new SecretStore(Path.Combine(env.PathOf("none"), "secrets.bin"), env.Warn);

            Assert.False(store.Has(SecretNames.ClaudeKey));
            Assert.Null(store.Get(SecretNames.ClaudeKey));
            store.Remove(SecretNames.ClaudeKey);

            Assert.False(Directory.Exists(env.PathOf("none")));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void A_damaged_file_reads_as_empty_warns_once_and_can_be_replaced()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            File.WriteAllText(path, "not base64 at all!!");
            var store = new SecretStore(path, env.Warn);

            Assert.Null(store.Get(SecretNames.ClaudeKey));
            Assert.False(store.Has(SecretNames.ClaudeKey));
            Assert.Single(env.Warnings);

            store.Set(SecretNames.ClaudeKey, "sk-new");

            Assert.Equal("sk-new", new SecretStore(path, env.Warn).Get(SecretNames.ClaudeKey));
            Assert.Single(env.Warnings);
        }

        [Fact]
        public void A_failed_save_warns_without_the_secret_and_keeps_nothing()
        {
            using var env = new AiTestEnv();
            string blocker = env.PathOf("blocker");
            File.WriteAllText(blocker, "a file where the folder should be");
            var store = new SecretStore(Path.Combine(blocker, "secrets.bin"), env.Warn);

            store.Set(SecretNames.ClaudeKey, "sk-ant-secret-999");

            Assert.False(store.Has(SecretNames.ClaudeKey));
            string warning = Assert.Single(env.Warnings);
            Assert.DoesNotContain("sk-ant-secret-999", warning);
        }

        [Fact]
        public void New_tokens_are_long_url_safe_and_different()
        {
            string a = SecretStore.NewToken();
            string b = SecretStore.NewToken();

            Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", b);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_default_path_is_beside_the_config()
        {
            Assert.Equal(Path.Combine(DiagnosticsLog.DataDir, "secrets.bin"), SecretStore.DefaultPath);
        }

        [Fact]
        public void No_setting_is_shaped_like_a_secret()
        {
            // config.json is the file people attach to bug reports.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new AppConfig()));
            foreach (var property in doc.RootElement.EnumerateObject())
                Assert.DoesNotMatch("(?i)api.?key|secret|token|password", property.Name);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiConfigTests|FullyQualifiedName~AiSecretStoreTests"`
Expected: build FAILS: CS0234 `The type or namespace name 'Ai' does not exist in the namespace 'Kil0bitSystemMonitor.Services'`, and CS1061/CS0117 for `AppConfig.AiAssistantEnabled` and the other new settings.

- [ ] **Step 6: Add the setting values and secret names**

Create `Services/Ai/AiSettings.cs`:

```csharp
namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The values <see cref="Kil0bitSystemMonitor.Models.AppConfig.AiProvider"/> may hold. Strings,
    /// like every other choice in <c>config.json</c>, so the file stays readable and a value this
    /// build does not know falls back to Claude instead of failing to load.
    /// </summary>
    public static class AiProviders
    {
        /// <summary>Anthropic's Claude, through the official Anthropic SDK.</summary>
        public const string Claude = "Claude";

        /// <summary>Any endpoint that speaks the OpenAI chat API: OpenAI, Azure, OpenRouter, Ollama, LM Studio.</summary>
        public const string OpenAiCompatible = "OpenAiCompatible";
    }

    /// <summary>How MCP clients reach MicaStats, stored in <see cref="Kil0bitSystemMonitor.Models.AppConfig.AiMcpMode"/>.</summary>
    public static class AiMcpModes
    {
        /// <summary>No pipe, no HTTP server; the <c>--mcp</c> bridge answers every call with a refusal.</summary>
        public const string Off = "Off";

        /// <summary><c>MicaStats.exe --mcp</c> on stdio, forwarding to the running app over a per-user named pipe.</summary>
        public const string Stdio = "Stdio";

        /// <summary>The running app serves MCP on 127.0.0.1 with a bearer token.</summary>
        public const string Http = "Http";
    }

    /// <summary>
    /// Names of the values <see cref="SecretStore"/> keeps. Secrets never go into <c>config.json</c>.
    /// </summary>
    public static class SecretNames
    {
        /// <summary>The Anthropic API key.</summary>
        public const string ClaudeKey = "claude-key";

        /// <summary>The optional key of the OpenAI-compatible endpoint.</summary>
        public const string CompatibleKey = "compatible-key";

        /// <summary>The bearer token local-HTTP MCP clients must send.</summary>
        public const string McpToken = "mcp-token";
    }
}
```

- [ ] **Step 7: Write the secret store**

Create `Services/Ai/SecretStore.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The API keys and the MCP HTTP token, encrypted for the current Windows user.
    ///
    /// <para>
    /// DPAPI with <see cref="DataProtectionScope.CurrentUser"/> and a MicaStats-specific entropy:
    /// only this Windows account on this PC can read the file back, so a copied
    /// <c>secrets.bin</c> is useless elsewhere, and nothing secret ever reaches
    /// <c>config.json</c>, which people attach to bug reports. The file is the base64 text of the
    /// protected bytes of a small JSON object, written through <see cref="AtomicFile"/> so a crash
    /// cannot leave half of it.
    /// </para>
    ///
    /// <para>
    /// Never throws. A failure goes to <c>warn</c> once per kind, and a value is never part of a
    /// message.
    /// </para>
    ///
    /// <para>
    /// Nothing is cached: every call reads the file again, and <see cref="Set"/> and
    /// <see cref="Remove"/> read, change and write it under one lock shared by every instance on
    /// the same path. The app creates short-lived stores (one per MCP HTTP request) while Settings
    /// and the Ask window hold their own, and none of them may drop another's entry.
    /// </para>
    /// </summary>
    public sealed class SecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.Secrets.v1");
        private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly Action<string> _warn;
        private readonly object _gate;
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        /// <param name="path">The file; its folder is created on the first save.</param>
        /// <param name="warn">Told about failures, never about values.</param>
        public SecretStore(string path, Action<string>? warn = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _warn = warn ?? (_ => { });
            _gate = Gates.GetOrAdd(GateKey(path), _ => new object());
        }

        /// <summary><c>%APPDATA%\MicaStats\secrets.bin</c>, beside <c>config.json</c>.</summary>
        public static string DefaultPath => Path.Combine(DiagnosticsLog.DataDir, "secrets.bin");

        /// <summary>Whether a value is saved under <paramref name="name"/>; Settings shows "Saved" from this.</summary>
        public bool Has(string name) => Get(name) != null;

        /// <summary>The saved value, or null when there is none or the file cannot be read.</summary>
        public string? Get(string name)
        {
            lock (_gate)
            {
                var all = Load();
                return all != null && all.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value)
                    ? value
                    : null;
            }
        }

        /// <summary>
        /// Saves <paramref name="value"/>, trimmed (a pasted key often carries a space or a line
        /// break). A blank value removes the name instead. When the save fails the old state stays
        /// and <c>warn</c> is told, so <see cref="Has"/> tells the truth afterwards.
        /// </summary>
        public void Set(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Remove(name);
                return;
            }

            lock (_gate)
            {
                var all = Load();
                if (all == null) return;   // the file is there but locked right now: never overwrite it blind
                all[name] = value.Trim();
                Save(all);
            }
        }

        /// <summary>Forgets <paramref name="name"/>; removing the last value deletes the file.</summary>
        public void Remove(string name)
        {
            lock (_gate)
            {
                var all = Load();
                if (all == null || !all.Remove(name)) return;
                Save(all);
            }
        }

        /// <summary>
        /// A fresh bearer token: 32 random bytes as base64url without padding (43 characters), safe
        /// in an HTTP header and a command line.
        /// </summary>
        public static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// Every saved value. Null when the file exists but cannot be opened right now (locked,
        /// access denied), so a write does not replace secrets that are only out of reach for a
        /// moment. A file that can be opened but not decrypted (another account, another PC,
        /// damage) reads as empty, so a new value can replace it.
        /// </summary>
        private Dictionary<string, string>? Load()
        {
            string? text;
            try
            {
                text = AtomicFile.ReadText(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WarnOnce("open", "The saved AI keys could not be opened (" + ex.GetType().Name + ")");
                return null;
            }

            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>(StringComparer.Ordinal);

            byte[]? plain = null;
            try
            {
                plain = ProtectedData.Unprotect(Convert.FromBase64String(text.Trim()), Entropy, DataProtectionScope.CurrentUser);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
                _warned.Remove("open");
                _warned.Remove("decrypt");
                return values == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(values, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                WarnOnce("decrypt", "The saved AI keys could not be read (" + ex.GetType().Name + "); enter them again in Settings > AI");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }

        private void Save(Dictionary<string, string> all)
        {
            byte[]? plain = null;
            try
            {
                if (all.Count == 0)
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    if (File.Exists(_path + AtomicFile.ReadySuffix)) File.Delete(_path + AtomicFile.ReadySuffix);
                }
                else
                {
                    string? folder = Path.GetDirectoryName(Path.GetFullPath(_path));
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    plain = JsonSerializer.SerializeToUtf8Bytes(all);
                    byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                    AtomicFile.Write(_path, Encoding.ASCII.GetBytes(Convert.ToBase64String(protectedBytes)));
                }
                _warned.Remove("write");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                           or NotSupportedException or ArgumentException)
            {
                WarnOnce("write", "The AI keys could not be saved (" + ex.GetType().Name + ")");
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }

        private void WarnOnce(string kind, string message)
        {
            if (_warned.Add(kind)) _warn(message);
        }

        /// <summary>One lock per file, however the path was spelled.</summary>
        private static string GateKey(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }
}
```

- [ ] **Step 8: Add the AI settings to AppConfig**

In `Models/SystemMetrics.cs`, inside `class AppConfig`, replace:

```csharp
        /// <summary>Reopen MicaPad at login when it was open at shutdown.</summary>
        public bool PadReopenAtLogin { get => _padReopenAtLogin; set { Set(ref _padReopenAtLogin, value); } }
```

with:

```csharp
        /// <summary>Reopen MicaPad at login when it was open at shutdown.</summary>
        public bool PadReopenAtLogin { get => _padReopenAtLogin; set { Set(ref _padReopenAtLogin, value); } }

        // ----- AI ---------------------------------------------------------------------------
        // Everything is off by default. No key or token is ever stored here: config.json is the
        // file people attach to bug reports, so secrets live in SecretStore (DPAPI) instead.

        private const string DefaultAiClaudeModel = "claude-haiku-4-5";
        private const string DefaultAiCompatibleBaseUrl = "http://localhost:11434/v1";

        private bool _aiAssistantEnabled;
        private string _aiProvider = Kil0bitSystemMonitor.Services.Ai.AiProviders.Claude;
        private string _aiClaudeModel = DefaultAiClaudeModel;
        private string _aiCompatibleBaseUrl = DefaultAiCompatibleBaseUrl;
        private string _aiCompatibleModel = "";
        private string _aiHotkey = "Ctrl+Alt+A";
        private int _aiDailyLimit = 100;
        private bool _aiHistoryEnabled;
        private string _aiMcpMode = Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Off;
        private int _aiMcpHttpPort = 47831;

        /// <summary>The Ask window, the Explain buttons and the AI hotkey.</summary>
        public bool AiAssistantEnabled { get => _aiAssistantEnabled; set { Set(ref _aiAssistantEnabled, value); } }

        /// <summary>
        /// <c>"Claude"</c> or <c>"OpenAiCompatible"</c> (see <c>AiProviders</c>). Anything else, such as a
        /// typo or a newer build's value, falls back to Claude rather than leaving no provider.
        /// </summary>
        public string AiProvider
        {
            get => _aiProvider;
            set
            {
                Set(ref _aiProvider,
                    string.Equals(value, Kil0bitSystemMonitor.Services.Ai.AiProviders.OpenAiCompatible, StringComparison.OrdinalIgnoreCase)
                        ? Kil0bitSystemMonitor.Services.Ai.AiProviders.OpenAiCompatible
                        : Kil0bitSystemMonitor.Services.Ai.AiProviders.Claude);
            }
        }

        /// <summary>The Claude model id; any typed name is accepted, a blank one means claude-haiku-4-5.</summary>
        public string AiClaudeModel
        {
            get => _aiClaudeModel;
            set { Set(ref _aiClaudeModel, string.IsNullOrWhiteSpace(value) ? DefaultAiClaudeModel : value.Trim()); }
        }

        /// <summary>Base URL of the OpenAI-compatible endpoint; blank means Ollama on this PC.</summary>
        public string AiCompatibleBaseUrl
        {
            get => _aiCompatibleBaseUrl;
            set { Set(ref _aiCompatibleBaseUrl, string.IsNullOrWhiteSpace(value) ? DefaultAiCompatibleBaseUrl : value.Trim()); }
        }

        /// <summary>The model name at the compatible endpoint. Empty until the user picks one.</summary>
        public string AiCompatibleModel { get => _aiCompatibleModel; set { Set(ref _aiCompatibleModel, value?.Trim() ?? ""); } }

        /// <summary>Global shortcut that opens Ask MicaStats, in <c>HotkeyParser</c> syntax. Empty turns it off.</summary>
        public string AiHotkey { get => _aiHotkey; set { Set(ref _aiHotkey, value ?? ""); } }

        /// <summary>Questions (Send or Explain) allowed per local day; keeps a runaway loop from spending money.</summary>
        public int AiDailyLimit { get => _aiDailyLimit; set { Set(ref _aiDailyLimit, Math.Clamp(value, 1, 10000)); } }

        /// <summary>Keep 7 days of per-minute history on disk for the assistant and MCP clients.</summary>
        public bool AiHistoryEnabled { get => _aiHistoryEnabled; set { Set(ref _aiHistoryEnabled, value); } }

        /// <summary>
        /// <c>"Off"</c>, <c>"Stdio"</c> or <c>"Http"</c> (see <c>AiMcpModes</c>). Anything else is Off,
        /// so a damaged setting never opens a server.
        /// </summary>
        public string AiMcpMode
        {
            get => _aiMcpMode;
            set
            {
                string mode = Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Off;
                if (string.Equals(value, Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Stdio, StringComparison.OrdinalIgnoreCase))
                    mode = Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Stdio;
                else if (string.Equals(value, Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Http, StringComparison.OrdinalIgnoreCase))
                    mode = Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Http;
                Set(ref _aiMcpMode, mode);
            }
        }

        /// <summary>Loopback port of the local HTTP MCP server; kept out of the privileged range.</summary>
        public int AiMcpHttpPort { get => _aiMcpHttpPort; set { Set(ref _aiMcpHttpPort, Math.Clamp(value, 1024, 65535)); } }
```

`StringComparison` and `Math` come from the file's existing `using System;`.

- [ ] **Step 9: Check that ProtectedData resolves without a package**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --no-restore`
Expected: `Build succeeded`, 0 errors. `ProtectedData` comes from the Windows Desktop reference pack (`Microsoft.WindowsDesktop.App.Ref\8.0.x\ref\net8.0\System.Security.Cryptography.ProtectedData.dll`), which `UseWPF` already brings in, so no `System.Security.Cryptography.ProtectedData` package is added (verified by building the whole plan against `main`).

- [ ] **Step 10: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiConfigTests|FullyQualifiedName~AiSecretStoreTests|FullyQualifiedName~PadConfigTests|FullyQualifiedName~AppConfigTests"`
Expected: PASS: the 18 new tests and every existing `PadConfigTests` and `AppConfigTests` test.

- [ ] **Step 11: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (817 + 18 = 835).

```bash
git add Kil0bitSystemMonitor.csproj tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj Services/Ai/AiSettings.cs Services/Ai/SecretStore.cs Models/SystemMetrics.cs tests/Kil0bitSystemMonitor.Tests/AiTestEnv.cs tests/Kil0bitSystemMonitor.Tests/AiConfigTests.cs tests/Kil0bitSystemMonitor.Tests/AiSecretStoreTests.cs
git commit -F - <<'EOF'
feat(ai): AI settings, DPAPI secret store and the AI packages

Adds the Anthropic, Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI
and ModelContextProtocol.Core packages for the work that follows, the ten
AI settings in AppConfig (all off by default), and SecretStore, which keeps
the API keys and the MCP token encrypted for the current Windows user and
never in config.json. AiTestEnv gives the AI and history tests a temp
folder, a fake clock and a warning collector.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: Redactor

Every tool result, in-app and over MCP, passes through one redactor before it leaves MicaStats. It keeps process names and paths (they are what answers are about) but replaces the user's own profile folder with `%USERPROFILE%`, other profile folders with `X:\Users\<user>`, the computer and user names with `[computer]` and `[user]`, and IP and MAC addresses with `[ip]` and `[mac]`, in that order. It works on plain text (slowdown reports) and on JSON trees (tool results), changing string values only.

**Files:**
- Create: `Services/Ai/Tools/Redactor.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/AiRedactorTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (`namespace Kil0bitSystemMonitor.Services.Ai.Tools`): `public sealed class Redactor { Redactor(string userProfile, string userName, string machineName); static Redactor ForCurrentUser(); string Redact(string text); JsonNode? RedactJson(JsonNode? node); }`. `RedactJson` redacts every string value in place (never property names) and returns the node; a bare string value comes back as a new node. Tokens: `%USERPROFILE%`, `X:\Users\<user>`, `[computer]`, `[user]`, `[ip]`, `[mac]`. Redacting twice changes nothing more.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiRedactorTests.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What the redactor hides, one rule per case, and what it must leave alone.</summary>
    public class AiRedactorTests
    {
        private static readonly Redactor R = new(@"C:\Users\Manoi", "Manoi", "DESKTOP-ABC123");

        [Theory]
        // The user's own profile folder, any case, either separator.
        [InlineData(@"C:\Users\Manoi\AppData\Local\app.exe", @"%USERPROFILE%\AppData\Local\app.exe")]
        [InlineData(@"c:/users/manoi/Documents/a.txt", @"%USERPROFILE%/Documents/a.txt")]
        [InlineData(@"C:\Users\Manoi", @"%USERPROFILE%")]
        [InlineData(@"Opened C:\Users\MANOI\x.txt today", @"Opened %USERPROFILE%\x.txt today")]
        // Anybody else's profile folder.
        [InlineData(@"C:\Users\Bob\Desktop\x.txt", @"C:\Users\<user>\Desktop\x.txt")]
        [InlineData(@"D:\Users\Jane Doe\file.txt", @"D:\Users\<user>\file.txt")]
        [InlineData(@"C:\Users\Manoi2\x", @"C:\Users\<user>\x")]
        [InlineData(@"C:\Users\Bob", @"C:\Users\<user>")]
        // The computer and user names, as whole words only.
        [InlineData("Computer DESKTOP-ABC123 is slow", "Computer [computer] is slow")]
        [InlineData("desktop-abc123", "[computer]")]
        [InlineData("DESKTOP-ABC1234", "DESKTOP-ABC1234")]
        [InlineData("Signed in as Manoi.", "Signed in as [user].")]
        [InlineData("Manoiko", "Manoiko")]
        // Addresses, without eating clock times, dates or code.
        [InlineData("Address 192.168.1.20 is up", "Address [ip] is up")]
        [InlineData("Gateway 10.0.0.1.", "Gateway [ip].")]
        [InlineData("999.1.1.1 and 1.2.3", "999.1.1.1 and 1.2.3")]
        [InlineData("fe80::1c2d:3e4f:5a6b:7c8d", "[ip]")]
        [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334", "[ip]")]
        [InlineData("loopback ::1 only", "loopback [ip] only")]
        [InlineData("At 10:00:00 via global::System", "At 10:00:00 via global::System")]
        [InlineData("MAC AA-BB-CC-DD-EE-FF", "MAC [mac]")]
        [InlineData("mac aa:bb:cc:dd:ee:0f.", "mac [mac].")]
        [InlineData("Report 2026-09-30 14:02", "Report 2026-09-30 14:02")]
        // Nothing to hide.
        [InlineData("chrome.exe uses 12% CPU", "chrome.exe uses 12% CPU")]
        public void Redacts(string input, string expected) => Assert.Equal(expected, R.Redact(input));

        [Fact]
        public void Names_shorter_than_three_characters_are_left_alone()
        {
            var r = new Redactor(@"C:\Users\Al", "Al", "PC");

            Assert.Equal(@"Al met PC at C:\Temp", r.Redact(@"Al met PC at C:\Temp"));
            Assert.Equal(@"%USERPROFILE%\x.txt", r.Redact(@"C:\Users\Al\x.txt"));   // the folder rule still applies
        }

        [Fact]
        public void The_current_user_profile_becomes_the_placeholder()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string text = Redactor.ForCurrentUser().Redact(Path.Combine(profile, "AppData", "x.exe"));

            Assert.Equal(@"%USERPROFILE%\AppData\x.exe", text);
        }

        [Fact]
        public void Json_string_values_are_redacted_in_place_and_names_and_numbers_are_not()
        {
            JsonNode node = JsonNode.Parse("""{"Manoi":"C:\\Users\\Manoi\\a.exe","n":5,"list":["192.168.0.1",3,{"deep":"DESKTOP-ABC123"}],"flag":true,"none":null}""")!;

            JsonNode? back = R.RedactJson(node);

            Assert.Same(node, back);
            Assert.Equal("""{"Manoi":"%USERPROFILE%\\a.exe","n":5,"list":["[ip]",3,{"deep":"[computer]"}],"flag":true,"none":null}""",
                         node.ToJsonString());
        }

        [Fact]
        public void A_bare_json_string_comes_back_redacted_and_null_stays_null()
        {
            JsonNode? back = R.RedactJson(JsonValue.Create("mac AA-BB-CC-DD-EE-FF"));

            Assert.Equal("mac [mac]", back!.GetValue<string>());
            Assert.Null(R.RedactJson(null));
        }

        [Fact]
        public void Redacting_twice_changes_nothing_more()
        {
            string once = R.Redact(@"C:\Users\Manoi\a and C:\Users\Bob\b on DESKTOP-ABC123 at 10.1.2.3");

            Assert.Equal(@"%USERPROFILE%\a and C:\Users\<user>\b on [computer] at [ip]", once);
            Assert.Equal(once, R.Redact(once));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiRedactorTests"`
Expected: build FAILS: CS0234 `The type or namespace name 'Tools' does not exist in the namespace 'Kil0bitSystemMonitor.Services.Ai'`.

- [ ] **Step 3: Write the redactor**

Create `Services/Ai/Tools/Redactor.cs`:

```csharp
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Removes what identifies the person or the PC from text before it leaves MicaStats, whether
    /// for an AI provider or for an MCP client.
    ///
    /// <para>
    /// Process names and paths stay: they are what the answers are about. The user's own profile
    /// folder becomes <c>%USERPROFILE%</c>, any other profile folder <c>X:\Users\&lt;user&gt;</c>, the
    /// computer and user names <c>[computer]</c> and <c>[user]</c>, and IP and MAC addresses
    /// <c>[ip]</c> and <c>[mac]</c>. The rules run in that order, so a profile path is replaced whole
    /// before its user name could be picked out of it. Every rule leaves its own output alone, so
    /// redacting twice changes nothing more.
    /// </para>
    ///
    /// <para>
    /// When in doubt it hides more rather than less: a spaced name followed by a slash later in the
    /// sentence may take a few extra words with it. Over-redaction costs a little context; a leak
    /// cannot be taken back.
    /// </para>
    /// </summary>
    public sealed class Redactor
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // X:\Users\<name>. The name runs to the next separator, so "Jane Doe" is caught whole, or,
        // with no separator after it, to the next white space.
        private static readonly Regex OtherProfile = new(
            @"(?<!\w)([A-Za-z]):([\\/]+)(Users)([\\/]+)(?:[^\\/:*?""<>|\r\n]+(?=[\\/])|[^\\/:*?""<>|\s]+)",
            Options | RegexOptions.Compiled);

        // Four octets of 0-255, not part of a longer dotted number.
        private static readonly Regex IPv4 = new(
            @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\d|\.\d)",
            Options | RegexOptions.Compiled);

        // Eight groups, or a "::" compression. A clock time such as 10:00:00 has neither, and a
        // C# name such as global::System fails the word-character guards.
        private static readonly Regex IPv6 = new(
            @"(?<![\w:])(?:(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}|(?:[0-9A-F]{1,4}(?::[0-9A-F]{1,4}){0,6})?::(?:[0-9A-F]{1,4}(?::[0-9A-F]{1,4}){0,6})?)(?![\w:])",
            Options | RegexOptions.Compiled);

        // Six pairs joined by one kind of separator, dash or colon.
        private static readonly Regex Mac = new(
            @"(?<![\w:-])[0-9A-F]{2}([:-])[0-9A-F]{2}(?:\1[0-9A-F]{2}){4}(?![\w:-])",
            Options | RegexOptions.Compiled);

        private readonly Regex? _ownProfile;
        private readonly Regex? _machine;
        private readonly Regex? _user;

        /// <param name="userProfile">The profile folder that becomes <c>%USERPROFILE%</c>; blank skips the rule.</param>
        /// <param name="userName">Replaced as a whole word when 3 or more characters long.</param>
        /// <param name="machineName">Replaced as a whole word when 3 or more characters long.</param>
        public Redactor(string userProfile, string userName, string machineName)
        {
            _ownProfile = ProfilePattern(userProfile);
            _machine = WordPattern(machineName);
            _user = WordPattern(userName);
        }

        /// <summary>A redactor for the Windows account MicaStats runs under.</summary>
        public static Redactor ForCurrentUser() =>
            new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

        /// <summary>The text with every identifying part replaced by its token.</summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            if (_ownProfile != null) text = _ownProfile.Replace(text, "%USERPROFILE%");
            text = OtherProfile.Replace(text, "${1}:${2}${3}${4}<user>");
            if (_machine != null) text = _machine.Replace(text, "[computer]");
            if (_user != null) text = _user.Replace(text, "[user]");
            text = IPv4.Replace(text, "[ip]");
            text = IPv6.Replace(text, "[ip]");
            return Mac.Replace(text, "[mac]");
        }

        /// <summary>
        /// Redacts every string value in the tree in place and returns <paramref name="node"/>.
        /// Property names are left alone: they are the tool's own vocabulary. A bare string value
        /// cannot be changed in place, so it comes back as a new node.
        /// </summary>
        public JsonNode? RedactJson(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;

                case JsonObject obj:
                    foreach (var pair in obj.ToList())
                    {
                        if (pair.Value is JsonValue value)
                        {
                            JsonNode redacted = RedactValue(value);
                            if (!ReferenceEquals(redacted, value)) obj[pair.Key] = redacted;
                        }
                        else
                        {
                            RedactJson(pair.Value);
                        }
                    }
                    return obj;

                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue value)
                        {
                            JsonNode redacted = RedactValue(value);
                            if (!ReferenceEquals(redacted, value)) array[i] = redacted;
                        }
                        else
                        {
                            RedactJson(array[i]);
                        }
                    }
                    return array;

                case JsonValue single:
                    return RedactValue(single);

                default:
                    return node;
            }
        }

        /// <summary>The value itself when it is not a string or needs no change, else a new string value.</summary>
        private JsonNode RedactValue(JsonValue value)
        {
            if (value.GetValueKind() != JsonValueKind.String) return value;

            string text = value.TryGetValue(out string? s) && s != null
                ? s
                : JsonSerializer.Deserialize<string>(value.ToJsonString()) ?? "";
            string redacted = Redact(text);
            return redacted == text ? value : JsonValue.Create(redacted)!;
        }

        /// <summary>
        /// The profile folder with either separator, in any case, and only as a whole folder:
        /// <c>C:\Users\Manoi2</c> is somebody else.
        /// </summary>
        private static Regex? ProfilePattern(string? profile)
        {
            if (string.IsNullOrWhiteSpace(profile)) return null;

            string[] parts = profile.Trim().Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;   // a bare drive is not a profile

            string body = string.Join(@"[\\/]+", parts.Select(Regex.Escape));
            return new Regex(@"(?<!\w)" + body + @"(?![^\\/\s""'<>|,;:.)\]}])", Options);
        }

        /// <summary>A whole-word pattern, or null for a name too short to replace safely.</summary>
        private static Regex? WordPattern(string? word)
        {
            if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3) return null;
            return new Regex(@"(?<!\w)" + Regex.Escape(word.Trim()) + @"(?!\w)", Options);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiRedactorTests"`
Expected: PASS, 29 tests (24 theory rows and 5 facts).

- [ ] **Step 5: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (835 + 29 = 864).

```bash
git add Services/Ai/Tools/Redactor.cs tests/Kil0bitSystemMonitor.Tests/AiRedactorTests.cs
git commit -F - <<'EOF'
feat(ai): Redactor for every tool result

Replaces the own profile folder, other profile folders, the computer
and user names, and IP and MAC addresses with fixed tokens, in text and
in the string values of a JSON tree. Process names and paths stay.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 3: HistoryRow and MinuteAggregator

The history keeps one row per UTC minute. This task defines the row and the pure aggregator that turns the one-second telemetry snapshots into it: averages and peaks, the lowest free-space share across ready drives, the last battery reading, and the minute's top processes. A reading never seen in the minute stays null (unavailable), never 0.

**Files:**
- Create: `Services/History/HistoryRow.cs` (`TopProcessSample`, `HistoryRow`)
- Create: `Services/History/MinuteAggregator.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/HistoryAggregatorTests.cs`

**Interfaces:**
- Consumes: `SystemMetrics` (`Models/SystemMetrics.cs`): `CpuUsage`, `CpuTemperature` (<= 0 unavailable), `RamPercent`, `GpuUsage` (< 0 unavailable), `GpuTemperature` (<= 0 unavailable), `NetUpKbps`, `NetDownKbps`, `Disks` (`DiskMetric.FreeBytes`, `TotalBytes`; 0 total = not ready), `DiskUsage` (activity %), `BatteryPercent` (< 0 no battery), `BatteryOnAc`, `HasBattery`.
- Produces (`namespace Kil0bitSystemMonitor.Services.History`):
  - `public sealed record TopProcessSample(string? CpuName, string? CpuPath, float? CpuPercent, string? RamName, float? RamMb);`
  - `public sealed record HistoryRow { DateTime Utc; int Seconds; float? CpuAvg, CpuMax, CpuTempAvg, CpuTempMax, RamAvg, RamMax, GpuAvg, GpuMax, GpuTempMax, NetUpAvg, NetDownAvg, DiskFreeMinPercent, DiskActivityMax, BatteryPercent; bool? OnAc; string? TopCpuName, TopCpuPath; float? TopCpuPercent; string? TopRamName; float? TopRamMb; }` (all `{ get; init; }`)
  - `public sealed class MinuteAggregator { HistoryRow? Add(SystemMetrics m, DateTime utc); void SetTop(TopProcessSample sample); HistoryRow? Flush(); internal static DateTime ToUtc(DateTime time); internal static DateTime MinuteOf(DateTime time); }`. `ToUtc`: Local is converted, Unspecified is taken as UTC. `MinuteOf`: the UTC minute start. Both are used by Tasks 4 and 5.
  - `DiskActivityMax` is recorded only from snapshots with at least one disk.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/HistoryAggregatorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>One-second snapshots become one row per UTC minute; missing readings stay missing.</summary>
    public class HistoryAggregatorTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static DiskMetric Disk(string name, double freeGb, double totalGb) => new()
        {
            Name = name,
            FreeBytes = (ulong)(freeGb * 1024 * 1024 * 1024),
            TotalBytes = (ulong)(totalGb * 1024 * 1024 * 1024),
        };

        private static SystemMetrics Metrics(float cpu = 10, float ram = 40, float cpuTemp = -1, float gpu = 5,
                                             float gpuTemp = -1, float up = 1, float down = 2, float diskActivity = 3,
                                             int battery = -1, bool onAc = false, List<DiskMetric>? disks = null) => new()
        {
            CpuUsage = cpu,
            RamPercent = ram,
            CpuTemperature = cpuTemp,
            GpuUsage = gpu,
            GpuTemperature = gpuTemp,
            NetUpKbps = up,
            NetDownKbps = down,
            DiskUsage = diskActivity,
            BatteryPercent = battery,
            BatteryOnAc = onAc,
            Disks = disks ?? new List<DiskMetric> { Disk("0 C:", 50, 100) },
        };

        [Fact]
        public void A_minute_of_samples_becomes_one_row_when_the_next_minute_starts()
        {
            var agg = new MinuteAggregator();

            Assert.Null(agg.Add(Metrics(cpu: 10, ram: 40, up: 100, down: 300), T0.AddSeconds(5)));
            Assert.Null(agg.Add(Metrics(cpu: 20, ram: 50, up: 200, down: 100), T0.AddSeconds(25)));
            Assert.Null(agg.Add(Metrics(cpu: 60, ram: 60, up: 300, down: 200), T0.AddSeconds(59.999)));
            HistoryRow? row = agg.Add(Metrics(cpu: 99), T0.AddMinutes(1));   // exactly on the boundary: the next minute

            Assert.NotNull(row);
            Assert.Equal(T0, row!.Utc);
            Assert.Equal(DateTimeKind.Utc, row.Utc.Kind);
            Assert.Equal(60, row.Seconds);
            Assert.Equal(30f, row.CpuAvg);
            Assert.Equal(60f, row.CpuMax);
            Assert.Equal(50f, row.RamAvg);
            Assert.Equal(60f, row.RamMax);
            Assert.Equal(200f, row.NetUpAvg);
            Assert.Equal(200f, row.NetDownAvg);
            Assert.Equal(50f, row.DiskFreeMinPercent);
        }

        [Fact]
        public void Readings_that_were_never_available_stay_empty_not_zero()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpuTemp: -1, gpu: -1, gpuTemp: -1, battery: -1, disks: new List<DiskMetric>()), T0.AddSeconds(1));
            agg.Add(Metrics(cpuTemp: -1, gpu: -1, gpuTemp: -1, battery: -1, disks: new List<DiskMetric>()), T0.AddSeconds(2));

            HistoryRow row = agg.Flush()!;

            Assert.Null(row.CpuTempAvg);
            Assert.Null(row.CpuTempMax);
            Assert.Null(row.GpuAvg);
            Assert.Null(row.GpuMax);
            Assert.Null(row.GpuTempMax);
            Assert.Null(row.DiskFreeMinPercent);
            Assert.Null(row.DiskActivityMax);
            Assert.Null(row.BatteryPercent);
            Assert.Null(row.OnAc);
            Assert.Null(row.TopCpuName);
            Assert.Null(row.TopRamMb);
            Assert.Equal(10f, row.CpuAvg);   // what was read is still there
        }

        [Fact]
        public void Temperatures_average_only_the_seconds_that_had_a_reading()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpuTemp: 50, gpuTemp: -1), T0.AddSeconds(1));
            agg.Add(Metrics(cpuTemp: -1, gpuTemp: 65), T0.AddSeconds(2));
            agg.Add(Metrics(cpuTemp: 70, gpuTemp: 60), T0.AddSeconds(3));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(60f, row.CpuTempAvg);
            Assert.Equal(70f, row.CpuTempMax);
            Assert.Equal(65f, row.GpuTempMax);
        }

        [Fact]
        public void Disk_free_is_the_lowest_share_on_any_ready_drive_and_activity_the_peak()
        {
            var agg = new MinuteAggregator();
            var disks = new List<DiskMetric> { Disk("0 C:", 50, 100), Disk("1 D:", 20, 200), new DiskMetric { Name = "2 E:" } };
            agg.Add(Metrics(diskActivity: 3, disks: disks), T0.AddSeconds(1));
            agg.Add(Metrics(diskActivity: 80, disks: disks), T0.AddSeconds(2));
            agg.Add(Metrics(diskActivity: 12, disks: disks), T0.AddSeconds(3));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(10f, row.DiskFreeMinPercent);   // D: 20 of 200 GB; E: was not ready and does not count
            Assert.Equal(80f, row.DiskActivityMax);
        }

        [Fact]
        public void Battery_is_the_last_reading_of_the_minute()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(battery: 80, onAc: true), T0.AddSeconds(5));
            agg.Add(Metrics(battery: 79, onAc: false), T0.AddSeconds(30));
            agg.Add(Metrics(battery: -1), T0.AddSeconds(45));   // a missed reading does not erase the last one

            HistoryRow row = agg.Flush()!;

            Assert.Equal(79f, row.BatteryPercent);
            Assert.False(row.OnAc);
        }

        [Fact]
        public void A_clock_that_jumps_back_finishes_the_minute_too()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: 10), T0.AddMinutes(5).AddSeconds(10));

            HistoryRow? finished = agg.Add(Metrics(cpu: 20), T0.AddMinutes(2));
            HistoryRow? rest = agg.Flush();

            Assert.Equal(T0.AddMinutes(5), finished!.Utc);
            Assert.Equal(10f, finished.CpuAvg);
            Assert.Equal(T0.AddMinutes(2), rest!.Utc);
            Assert.Equal(20f, rest.CpuAvg);
        }

        [Fact]
        public void Flush_finishes_the_minute_in_progress_once()
        {
            Assert.Null(new MinuteAggregator().Flush());

            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: 42), T0.AddSeconds(10));

            HistoryRow? row = agg.Flush();

            Assert.Equal(T0, row!.Utc);
            Assert.Equal(42f, row.CpuMax);
            Assert.Null(agg.Flush());
        }

        [Fact]
        public void The_top_processes_belong_to_the_minute_they_were_set_in()
        {
            var top = new TopProcessSample("chrome.exe", @"%USERPROFILE%\chrome.exe", 42.5f, "code.exe", 812.3f);
            var agg = new MinuteAggregator();
            agg.Add(Metrics(), T0.AddSeconds(5));
            agg.SetTop(top);
            agg.Add(Metrics(), T0.AddSeconds(30));

            HistoryRow row = agg.Add(Metrics(), T0.AddMinutes(1).AddSeconds(5))!;
            HistoryRow next = agg.Flush()!;

            Assert.Equal("chrome.exe", row.TopCpuName);
            Assert.Equal(@"%USERPROFILE%\chrome.exe", row.TopCpuPath);
            Assert.Equal(42.5f, row.TopCpuPercent);
            Assert.Equal("code.exe", row.TopRamName);
            Assert.Equal(812.3f, row.TopRamMb);
            Assert.Null(next.TopCpuName);
            Assert.Null(next.TopRamName);
        }

        [Fact]
        public void Nan_readings_are_ignored()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(cpu: float.NaN), T0.AddSeconds(1));
            agg.Add(Metrics(cpu: 40), T0.AddSeconds(2));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(40f, row.CpuAvg);
            Assert.Equal(40f, row.CpuMax);
        }

        [Fact]
        public void An_unspecified_time_is_read_as_utc()
        {
            var agg = new MinuteAggregator();
            agg.Add(Metrics(), DateTime.SpecifyKind(T0.AddSeconds(5), DateTimeKind.Unspecified));

            HistoryRow row = agg.Flush()!;

            Assert.Equal(T0, row.Utc);
            Assert.Equal(DateTimeKind.Utc, row.Utc.Kind);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryAggregatorTests"`
Expected: build FAILS: CS0234 `The type or namespace name 'History' does not exist in the namespace 'Kil0bitSystemMonitor.Services'`.

- [ ] **Step 3: Write the row types**

Create `Services/History/HistoryRow.cs`:

```csharp
using System;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The busiest processes at one moment of a minute: by CPU, with its exe path (already
    /// redacted), and by memory. A null field means that ranking had nothing to offer.
    /// </summary>
    public sealed record TopProcessSample(string? CpuName, string? CpuPath, float? CpuPercent, string? RamName, float? RamMb);

    /// <summary>
    /// One line of the history: a minute of readings, or five minutes once the day has been
    /// thinned. Every reading is nullable because an unreadable sensor must stay distinguishable
    /// from a zero: a missing temperature is not a cold CPU.
    /// </summary>
    public sealed record HistoryRow
    {
        /// <summary>Start of the minute (or 5-minute bucket), always <see cref="DateTimeKind.Utc"/>.</summary>
        public DateTime Utc { get; init; }

        /// <summary>Seconds the row covers: 60, or 300 once its day has been thinned.</summary>
        public int Seconds { get; init; }

        /// <summary>Average CPU use, %.</summary>
        public float? CpuAvg { get; init; }

        /// <summary>Highest one-second CPU use, %.</summary>
        public float? CpuMax { get; init; }

        /// <summary>Average CPU temperature, degrees C, over the seconds that had a reading.</summary>
        public float? CpuTempAvg { get; init; }

        /// <summary>Highest CPU temperature, degrees C.</summary>
        public float? CpuTempMax { get; init; }

        /// <summary>Average memory in use, %.</summary>
        public float? RamAvg { get; init; }

        /// <summary>Highest memory in use, %.</summary>
        public float? RamMax { get; init; }

        /// <summary>Average GPU use, %.</summary>
        public float? GpuAvg { get; init; }

        /// <summary>Highest GPU use, %.</summary>
        public float? GpuMax { get; init; }

        /// <summary>Highest GPU temperature, degrees C.</summary>
        public float? GpuTempMax { get; init; }

        /// <summary>Average upload rate, kbps as telemetry reports it.</summary>
        public float? NetUpAvg { get; init; }

        /// <summary>Average download rate, kbps as telemetry reports it.</summary>
        public float? NetDownAvg { get; init; }

        /// <summary>Lowest free space on any ready drive, % of that drive.</summary>
        public float? DiskFreeMinPercent { get; init; }

        /// <summary>Highest disk activity of the busiest drive, %.</summary>
        public float? DiskActivityMax { get; init; }

        /// <summary>Battery charge at the end of the row, %; null on a desktop.</summary>
        public float? BatteryPercent { get; init; }

        /// <summary>Whether the PC was on mains power at the end of the row; null on a desktop.</summary>
        public bool? OnAc { get; init; }

        /// <summary>The process using the most CPU.</summary>
        public string? TopCpuName { get; init; }

        /// <summary>Its exe path, redacted.</summary>
        public string? TopCpuPath { get; init; }

        /// <summary>Its CPU share, % of the whole machine.</summary>
        public float? TopCpuPercent { get; init; }

        /// <summary>The process with the largest working set.</summary>
        public string? TopRamName { get; init; }

        /// <summary>Its working set, MB.</summary>
        public float? TopRamMb { get; init; }
    }
}
```

- [ ] **Step 4: Write the aggregator**

Create `Services/History/MinuteAggregator.cs`:

```csharp
using System;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// Folds one-second telemetry snapshots into one <see cref="HistoryRow"/> per UTC minute.
    ///
    /// <para>
    /// Pure and clock-free (the caller passes each snapshot's time), so a test can feed a minute in
    /// microseconds. A minute ends when a snapshot arrives from any other minute, forward or
    /// backward: a clock that jumps back only produces an out-of-order row, which the store sorts
    /// on read. Not thread-safe; <see cref="HistoryRecorder"/> holds a lock around it.
    /// </para>
    /// </summary>
    public sealed class MinuteAggregator
    {
        /// <summary>Running average and peak of one reading; empty when never seen.</summary>
        private struct Stat
        {
            private double _sum;
            private float _max;

            public int Count { get; private set; }

            public void Add(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return;
                if (Count == 0 || value > _max) _max = value;
                _sum += value;
                Count++;
            }

            public float? Average => Count == 0 ? null : (float)(_sum / Count);

            public float? Peak => Count == 0 ? null : _max;
        }

        private DateTime _minute;
        private int _samples;
        private Stat _cpu, _cpuTemp, _ram, _gpu, _gpuTemp, _netUp, _netDown, _diskActivity;
        private float? _diskFreeMin;
        private float? _battery;
        private bool? _onAc;
        private TopProcessSample? _top;

        /// <summary>
        /// Adds one snapshot taken at <paramref name="utc"/>. Returns the finished row when this
        /// snapshot belongs to a different minute than the one in progress, else null.
        /// </summary>
        public HistoryRow? Add(SystemMetrics m, DateTime utc)
        {
            ArgumentNullException.ThrowIfNull(m);

            DateTime minute = MinuteOf(utc);
            HistoryRow? finished = null;
            if (_samples > 0 && minute != _minute) finished = Finish();
            if (_samples == 0) _minute = minute;
            _samples++;

            _cpu.Add(m.CpuUsage);
            if (m.CpuTemperature > 0) _cpuTemp.Add(m.CpuTemperature);
            _ram.Add(m.RamPercent);
            if (m.GpuUsage >= 0) _gpu.Add(m.GpuUsage);
            if (m.GpuTemperature > 0) _gpuTemp.Add(m.GpuTemperature);
            _netUp.Add(m.NetUpKbps);
            _netDown.Add(m.NetDownKbps);

            // Telemetry reports DiskUsage = 0 when it has no disk counters at all, so activity only
            // counts when the snapshot lists a disk.
            if (m.Disks != null && m.Disks.Count > 0)
            {
                _diskActivity.Add(m.DiskUsage);
                foreach (var disk in m.Disks)
                {
                    // A drive that was not ready reports zero total; it has not run out of space.
                    if (disk == null || disk.TotalBytes == 0) continue;
                    float free = (float)(disk.FreeBytes * 100d / disk.TotalBytes);
                    if (_diskFreeMin == null || free < _diskFreeMin) _diskFreeMin = free;
                }
            }

            if (m.HasBattery)
            {
                _battery = m.BatteryPercent;
                _onAc = m.BatteryOnAc;
            }

            return finished;
        }

        /// <summary>Records the top processes for the minute in progress (or the next one, if none is).</summary>
        public void SetTop(TopProcessSample sample) => _top = sample;

        /// <summary>Finishes the minute in progress; null when it holds no snapshot.</summary>
        public HistoryRow? Flush() => _samples == 0 ? null : Finish();

        /// <summary>The same instant as UTC: Local is converted, Unspecified is taken to be UTC already.</summary>
        internal static DateTime ToUtc(DateTime time) => time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time,
        };

        /// <summary>The start of the UTC minute holding <paramref name="time"/>.</summary>
        internal static DateTime MinuteOf(DateTime time)
        {
            DateTime utc = ToUtc(time);
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        }

        private HistoryRow Finish()
        {
            var row = new HistoryRow
            {
                Utc = _minute,
                Seconds = 60,
                CpuAvg = _cpu.Average,
                CpuMax = _cpu.Peak,
                CpuTempAvg = _cpuTemp.Average,
                CpuTempMax = _cpuTemp.Peak,
                RamAvg = _ram.Average,
                RamMax = _ram.Peak,
                GpuAvg = _gpu.Average,
                GpuMax = _gpu.Peak,
                GpuTempMax = _gpuTemp.Peak,
                NetUpAvg = _netUp.Average,
                NetDownAvg = _netDown.Average,
                DiskFreeMinPercent = _diskFreeMin,
                DiskActivityMax = _diskActivity.Peak,
                BatteryPercent = _battery,
                OnAc = _onAc,
                TopCpuName = _top?.CpuName,
                TopCpuPath = _top?.CpuPath,
                TopCpuPercent = _top?.CpuPercent,
                TopRamName = _top?.RamName,
                TopRamMb = _top?.RamMb,
            };

            _samples = 0;
            _cpu = _cpuTemp = _ram = _gpu = _gpuTemp = _netUp = _netDown = _diskActivity = default;
            _diskFreeMin = null;
            _battery = null;
            _onAc = null;
            _top = null;
            return row;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryAggregatorTests"`
Expected: PASS, 10 tests.

- [ ] **Step 6: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (864 + 10 = 874).

```bash
git add Services/History/HistoryRow.cs Services/History/MinuteAggregator.cs tests/Kil0bitSystemMonitor.Tests/HistoryAggregatorTests.cs
git commit -F - <<'EOF'
feat(ai): history rows and the minute aggregator

Folds the one-second telemetry snapshots into one row per UTC minute:
averages and peaks, the lowest free space on any ready drive, the last
battery reading and the top processes. A reading never seen in the
minute stays empty instead of becoming zero.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: HistoryCsv and HistoryStore

The rows go to `%APPDATA%\MicaStats\history\yyyyMMdd.csv`, one file per UTC day, so the offline `--mcp` bridge can read them while the app writes. `HistoryCsv` owns the line format (InvariantCulture, empty field = unavailable) and the 5-minute combine; `HistoryStore` owns the files: append, range read that skips damaged and half-written lines, thinning of days that ended over 24 h ago, deletion after 7 days, size and delete-all. It never throws for I/O and warns once per kind of failure.

**Files:**
- Create: `Services/History/HistoryCsv.cs`
- Create: `Services/History/HistoryStore.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/HistoryCsvTests.cs`, `tests/Kil0bitSystemMonitor.Tests/HistoryStoreTests.cs`

**Interfaces:**
- Consumes: `HistoryRow`, `MinuteAggregator.ToUtc(DateTime)` (Task 3); `Kil0bitSystemMonitor.Services.Pad.AtomicFile.Write(string, byte[])`, `AtomicFile.ReadySuffix`, `AtomicFile.TempSuffix` (exist); `DiagnosticsLog.DataDir` (exists); `AiTestEnv` (Task 1).
- Produces (`namespace Kil0bitSystemMonitor.Services.History`):
  - `public static class HistoryCsv { const string VersionLine = "# MicaStats history v1"; static string ColumnLine { get; } static string Format(HistoryRow row); static bool TryParse(string line, out HistoryRow? row); static HistoryRow Combine(IReadOnlyList<HistoryRow> rows, DateTime bucketUtc, int seconds); }`. Columns: `utc,seconds,cpu_avg,cpu_max,cpu_temp_avg,cpu_temp_max,ram_avg,ram_max,gpu_avg,gpu_max,gpu_temp_max,net_up_avg_kbps,net_down_avg_kbps,disk_free_min_pct,disk_activity_max,battery_pct,on_ac,top_cpu_name,top_cpu_path,top_cpu_pct,top_ram_name,top_ram_mb`. Time `yyyy-MM-ddTHH:mm:ssZ`, numbers with one decimal, `on_ac` `1`/`0`, text fields CSV-quoted when they hold a comma or a quote.
  - `public sealed class HistoryStore { HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null); static string DefaultFolder { get; } /* %APPDATA%\MicaStats\history */; const int RetentionDays = 7; string Folder { get; } void Append(HistoryRow row); IReadOnlyList<HistoryRow> Read(DateTime fromUtc, DateTime toUtc); void Maintain(); long SizeBytes(); void DeleteAll(); }`. The constructor touches no disk; `Append` creates the folder. Thread-safe with one lock; files opened with `FileShare.ReadWrite` (reads also `FileShare.Delete`); never throws for I/O.

- [ ] **Step 1: Write the failing CSV tests**

Create `tests/Kil0bitSystemMonitor.Tests/HistoryCsvTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The history line format: round trips, empty means unavailable, no culture leaks in.</summary>
    public class HistoryCsvTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static HistoryRow Full() => new()
        {
            Utc = T0,
            Seconds = 60,
            CpuAvg = 12.5f,
            CpuMax = 88.1f,
            CpuTempAvg = 54.2f,
            CpuTempMax = 71f,
            RamAvg = 63.4f,
            RamMax = 70f,
            GpuAvg = 3.3f,
            GpuMax = 20.8f,
            GpuTempMax = 48f,
            NetUpAvg = 12.7f,
            NetDownAvg = 1450.2f,
            DiskFreeMinPercent = 18.6f,
            DiskActivityMax = 97.3f,
            BatteryPercent = 81f,
            OnAc = true,
            TopCpuName = "chrome.exe",
            TopCpuPath = @"%USERPROFILE%\AppData\Local\Google\Chrome\chrome.exe",
            TopCpuPercent = 23.4f,
            TopRamName = "Code.exe",
            TopRamMb = 812.3f,
        };

        [Fact]
        public void A_full_row_round_trips()
        {
            string line = HistoryCsv.Format(Full());

            Assert.Equal("2026-09-30T10:00:00Z,60,12.5,88.1,54.2,71.0,63.4,70.0,3.3,20.8,48.0,12.7,1450.2,18.6,97.3,81.0,1," +
                         "chrome.exe,%USERPROFILE%\\AppData\\Local\\Google\\Chrome\\chrome.exe,23.4,Code.exe,812.3", line);
            Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
            Assert.Equal(Full(), back);
            Assert.Equal(DateTimeKind.Utc, back!.Utc.Kind);
        }

        [Fact]
        public void An_unavailable_reading_is_an_empty_field_never_zero()
        {
            var row = new HistoryRow { Utc = T0, Seconds = 60 };

            string line = HistoryCsv.Format(row);

            Assert.Equal("2026-09-30T10:00:00Z,60" + new string(',', 20), line);
            Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
            Assert.Equal(row, back);
        }

        [Theory]
        [InlineData("th-TH")]   // Buddhist-era calendar: 2026 would print as 2569
        [InlineData("de-DE")]   // comma decimal separator
        public void The_current_culture_changes_nothing(string culture)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);

                string line = HistoryCsv.Format(Full());

                Assert.StartsWith("2026-09-30T10:00:00Z,60,12.5,88.1,", line);
                Assert.True(HistoryCsv.TryParse(line, out HistoryRow? back));
                Assert.Equal(2026, back!.Utc.Year);
                Assert.Equal(Full(), back);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Names_with_commas_and_quotes_survive()
        {
            var row = new HistoryRow
            {
                Utc = T0,
                Seconds = 60,
                TopCpuName = "odd, \"name\".exe",
                TopCpuPath = @"C:\Program Files\A, B\odd.exe",
                TopRamName = "\"q\"",
            };

            Assert.True(HistoryCsv.TryParse(HistoryCsv.Format(row), out HistoryRow? back));
            Assert.Equal(row, back);
        }

        [Fact]
        public void Headers_and_damaged_lines_are_rejected()
        {
            string good = HistoryCsv.Format(Full());
            Assert.True(HistoryCsv.TryParse(good, out _));

            Assert.False(HistoryCsv.TryParse(HistoryCsv.VersionLine, out _));
            Assert.False(HistoryCsv.TryParse(HistoryCsv.ColumnLine, out _));
            Assert.False(HistoryCsv.TryParse("", out _));
            Assert.False(HistoryCsv.TryParse(good.Substring(0, 40), out _));                    // cut short
            Assert.False(HistoryCsv.TryParse(good.Replace(",12.5,", ",twelve,"), out _));       // not a number
            Assert.False(HistoryCsv.TryParse(good.Replace(",81.0,1,", ",81.0,yes,"), out _));   // not a flag
            Assert.False(HistoryCsv.TryParse("2026-09-30T10:00:00Z,60" + new string(',', 17) + "\"cut, mid", out _));   // open quote
        }

        [Fact]
        public void Combine_averages_averages_keeps_peaks_and_the_most_frequent_top_process()
        {
            // Given newest first on purpose: Combine orders by time itself.
            var rows = new List<HistoryRow>
            {
                new() { Utc = T0.AddMinutes(4), Seconds = 60, CpuAvg = 50, CpuMax = 55, DiskFreeMinPercent = 60,
                        TopCpuName = "a.exe", TopCpuPercent = 30, TopRamName = "n.exe", TopRamMb = 300 },
                new() { Utc = T0.AddMinutes(3), Seconds = 60, CpuAvg = 40, CpuMax = 45, DiskFreeMinPercent = 50, BatteryPercent = 78,
                        TopCpuName = "A.EXE", TopCpuPath = @"C:\A\a.exe", TopCpuPercent = 40, TopRamName = "n.exe", TopRamMb = 200 },
                new() { Utc = T0.AddMinutes(2), Seconds = 60, CpuAvg = 30, CpuMax = 35, DiskFreeMinPercent = 38, OnAc = false,
                        TopCpuName = "b.exe", TopCpuPercent = 60, TopRamName = "n.exe", TopRamMb = 100 },
                new() { Utc = T0.AddMinutes(1), Seconds = 60, CpuAvg = 20, CpuMax = 90, DiskFreeMinPercent = 35, BatteryPercent = 79, OnAc = true,
                        TopCpuName = "b.exe", TopCpuPercent = 50, TopRamName = "m.exe", TopRamMb = 900 },
                new() { Utc = T0, Seconds = 60, CpuAvg = 10, CpuMax = 15, DiskFreeMinPercent = 40, BatteryPercent = 80, OnAc = true,
                        TopCpuName = "a.exe", TopCpuPercent = 20, TopRamName = "m.exe", TopRamMb = 800 },
            };

            HistoryRow five = HistoryCsv.Combine(rows, T0, 300);

            Assert.Equal(T0, five.Utc);
            Assert.Equal(300, five.Seconds);
            Assert.Equal(30f, five.CpuAvg);                 // (10 + 20 + 30 + 40 + 50) / 5
            Assert.Equal(90f, five.CpuMax);
            Assert.Equal(35f, five.DiskFreeMinPercent);
            Assert.Equal(78f, five.BatteryPercent);         // the last reading by time
            Assert.False(five.OnAc);                        // the last reading by time
            Assert.Null(five.GpuAvg);                       // never read
            Assert.Equal("a.exe", five.TopCpuName);         // three times in any case, b.exe twice
            Assert.Equal(@"C:\A\a.exe", five.TopCpuPath);
            Assert.Equal(30f, five.TopCpuPercent);          // (20 + 40 + 30) / 3
            Assert.Equal("n.exe", five.TopRamName);
            Assert.Equal(200f, five.TopRamMb);              // (100 + 200 + 300) / 3
        }
    }
}
```

- [ ] **Step 2: Write the failing store tests**

Create `tests/Kil0bitSystemMonitor.Tests/HistoryStoreTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The daily history files: append, range read, thinning, retention, failures.</summary>
    public class HistoryStoreTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static HistoryStore NewStore(AiTestEnv env) =>
            new(env.PathOf("history"), () => env.Clock.UtcNow, env.Warn);

        private static HistoryRow Row(DateTime utc, float cpu = 10) =>
            new() { Utc = utc, Seconds = 60, CpuAvg = cpu, CpuMax = cpu };

        [Fact]
        public void Append_writes_a_versioned_header_once_and_one_line_per_row()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);

            store.Append(Row(T0));
            store.Append(Row(T0.AddMinutes(1), 20));

            string[] lines = File.ReadAllLines(Path.Combine(store.Folder, "20260930.csv"));
            Assert.Equal(new[]
            {
                HistoryCsv.VersionLine,
                HistoryCsv.ColumnLine,
                HistoryCsv.Format(Row(T0)),
                HistoryCsv.Format(Row(T0.AddMinutes(1), 20)),
            }, lines);
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Each_row_goes_to_the_file_of_its_utc_day()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            var late = new DateTime(2026, 9, 30, 23, 59, 0, DateTimeKind.Utc);

            store.Append(Row(late.AddMinutes(1)));
            store.Append(Row(late));

            Assert.True(File.Exists(Path.Combine(store.Folder, "20260930.csv")));
            Assert.True(File.Exists(Path.Combine(store.Folder, "20261001.csv")));
            Assert.Equal(new[] { late, late.AddMinutes(1) },
                         store.Read(late.AddHours(-1), late.AddHours(1)).Select(r => r.Utc));
        }

        [Fact]
        public void Read_is_inclusive_and_sorted_even_after_a_clock_jump()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0.AddMinutes(5)));
            store.Append(Row(T0.AddMinutes(6)));
            store.Append(Row(T0.AddMinutes(2)));   // the clock jumped back
            store.Append(Row(T0.AddMinutes(3)));

            var rows = store.Read(T0.AddMinutes(2), T0.AddMinutes(5));

            Assert.Equal(new[] { T0.AddMinutes(2), T0.AddMinutes(3), T0.AddMinutes(5) }, rows.Select(r => r.Utc));
        }

        [Fact]
        public void A_half_written_last_line_is_skipped_and_the_next_row_starts_on_a_new_line()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0));
            string file = Path.Combine(store.Folder, "20260930.csv");
            File.AppendAllText(file, "2026-09-30T10:01:00Z,60,33.");   // a crash in the middle of a write

            Assert.Equal(new[] { T0 }, store.Read(T0, T0.AddHours(1)).Select(r => r.Utc));

            store.Append(Row(T0.AddMinutes(2)));

            Assert.Equal(new[] { T0, T0.AddMinutes(2) }, store.Read(T0, T0.AddHours(1)).Select(r => r.Utc));
        }

        [Fact]
        public void Damaged_lines_are_skipped()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            Directory.CreateDirectory(store.Folder);
            File.WriteAllText(Path.Combine(store.Folder, "20260930.csv"),
                HistoryCsv.VersionLine + "\n" + HistoryCsv.ColumnLine + "\n" +
                "garbage,line\n" +
                HistoryCsv.Format(Row(T0)) + "\r\n" +
                "2026-09-30T10:01:00Z,sixty" + new string(',', 20) + "\n");

            HistoryRow row = Assert.Single(store.Read(T0, T0.AddHours(1)));

            Assert.Equal(T0, row.Utc);
        }

        [Fact]
        public void Another_reader_can_read_while_the_file_is_open_for_writing()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(T0));
            string file = Path.Combine(store.Folder, "20260930.csv");

            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bridge = new HistoryStore(store.Folder, () => env.Clock.UtcNow, env.Warn);   // as the --mcp bridge would
                Assert.Single(bridge.Read(T0, T0.AddHours(1)));

                store.Append(Row(T0.AddMinutes(1)));
                Assert.Equal(2, bridge.Read(T0, T0.AddHours(1)).Count);
            }
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Maintain_thins_days_that_ended_over_a_day_ago_to_five_minute_rows()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            var old = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
            var recent = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 10; i++) store.Append(Row(old.AddMinutes(i), i * 10));
            for (int i = 0; i < 3; i++) store.Append(Row(recent.AddMinutes(i), 50));
            env.Clock.UtcNow = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            store.Maintain();
            store.Maintain();   // a second pass finds nothing left to thin

            var thinned = store.Read(old, old.AddHours(1));
            Assert.Equal(2, thinned.Count);
            Assert.Equal(old, thinned[0].Utc);
            Assert.Equal(300, thinned[0].Seconds);
            Assert.Equal(20f, thinned[0].CpuAvg);   // (0 + 10 + 20 + 30 + 40) / 5
            Assert.Equal(40f, thinned[0].CpuMax);
            Assert.Equal(old.AddMinutes(5), thinned[1].Utc);
            Assert.Equal(70f, thinned[1].CpuAvg);
            Assert.Equal(90f, thinned[1].CpuMax);
            Assert.Equal(HistoryCsv.VersionLine, File.ReadLines(Path.Combine(store.Folder, "20260930.csv")).First());

            var kept = store.Read(recent, recent.AddHours(1));   // that day ended only 12 h ago
            Assert.Equal(3, kept.Count);
            Assert.All(kept, r => Assert.Equal(60, r.Seconds));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Maintain_deletes_days_past_the_retention_and_leaves_other_files()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            store.Append(Row(new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc)));
            store.Append(Row(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc)));
            File.WriteAllText(Path.Combine(store.Folder, "notes.txt"), "mine");
            env.Clock.UtcNow = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

            store.Maintain();

            Assert.False(File.Exists(Path.Combine(store.Folder, "20261002.csv")));   // ended 7.5 days ago
            Assert.True(File.Exists(Path.Combine(store.Folder, "20261003.csv")));    // ended 6.5 days ago
            Assert.True(File.Exists(Path.Combine(store.Folder, "notes.txt")));
            Assert.Single(store.Read(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), env.Clock.UtcNow));
        }

        [Fact]
        public void Size_and_delete_all()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);
            Assert.Equal(0L, store.SizeBytes());

            store.Append(Row(T0));
            Assert.Equal(new FileInfo(Path.Combine(store.Folder, "20260930.csv")).Length, store.SizeBytes());

            store.DeleteAll();
            Assert.False(Directory.Exists(store.Folder));
            Assert.Equal(0L, store.SizeBytes());
            Assert.Empty(store.Read(T0, T0.AddDays(1)));

            store.Append(Row(T0));   // recording goes on after a delete
            Assert.Single(store.Read(T0, T0.AddDays(1)));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void An_unusable_folder_warns_once_and_never_throws()
        {
            using var env = new AiTestEnv();
            string blocked = env.PathOf("blocked");
            File.WriteAllText(blocked, "a file where the folder should be");
            var store = new HistoryStore(blocked, () => env.Clock.UtcNow, env.Warn);

            store.Append(Row(T0));
            store.Append(Row(T0.AddMinutes(1)));
            Assert.Empty(store.Read(T0, T0.AddDays(1)));
            store.Maintain();
            Assert.Equal(0L, store.SizeBytes());
            store.DeleteAll();

            string warning = Assert.Single(env.Warnings);
            Assert.Contains("could not be written", warning);
            Assert.True(File.Exists(blocked));
        }

        [Fact]
        public void Appends_from_many_threads_all_land()
        {
            using var env = new AiTestEnv();
            var store = NewStore(env);

            Parallel.For(0, 120, i => store.Append(Row(T0.AddMinutes(i), i)));

            var rows = store.Read(T0, T0.AddHours(2));
            Assert.Equal(120, rows.Count);
            for (int i = 0; i < rows.Count; i++) Assert.Equal(T0.AddMinutes(i), rows[i].Utc);
        }

        [Fact]
        public void Creating_and_reading_an_empty_store_touches_no_disk()
        {
            using var env = new AiTestEnv();
            string folder = env.PathOf("history");

            var store = new HistoryStore(folder, () => env.Clock.UtcNow, env.Warn);
            Assert.Empty(store.Read(T0, T0.AddDays(7)));
            store.Maintain();

            Assert.Equal(folder, store.Folder);
            Assert.Equal(0L, store.SizeBytes());
            Assert.False(Directory.Exists(folder));
            Assert.Equal(Path.Combine(DiagnosticsLog.DataDir, "history"), HistoryStore.DefaultFolder);
            Assert.Empty(env.Warnings);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryCsvTests|FullyQualifiedName~HistoryStoreTests"`
Expected: build FAILS: CS0103 `The name 'HistoryCsv' does not exist in the current context` and CS0246 for `HistoryStore`.

- [ ] **Step 4: Write the line format**

Create `Services/History/HistoryCsv.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The history file format: one CSV line per <see cref="HistoryRow"/>, after a version line and
    /// a column line.
    ///
    /// <para>
    /// Every number and date goes through <see cref="CultureInfo.InvariantCulture"/>: a Thai locale
    /// would otherwise write Buddhist-era years (2569), a German one decimal commas, and neither the
    /// offline MCP bridge nor a later build could read the file back. An empty field means
    /// "unavailable", never 0.
    /// </para>
    /// </summary>
    public static class HistoryCsv
    {
        /// <summary>First line of every file; changes only if a column ever changes meaning.</summary>
        public const string VersionLine = "# MicaStats history v1";

        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        private static readonly string[] Columns =
        {
            "utc", "seconds",
            "cpu_avg", "cpu_max", "cpu_temp_avg", "cpu_temp_max",
            "ram_avg", "ram_max",
            "gpu_avg", "gpu_max", "gpu_temp_max",
            "net_up_avg_kbps", "net_down_avg_kbps",
            "disk_free_min_pct", "disk_activity_max",
            "battery_pct", "on_ac",
            "top_cpu_name", "top_cpu_path", "top_cpu_pct",
            "top_ram_name", "top_ram_mb",
        };

        /// <summary>Indexes of the columns that hold numbers (everything but time, seconds, flag and names).</summary>
        private static readonly int[] NumberColumns = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 19, 21 };

        /// <summary>The column names, the second line of every file.</summary>
        public static string ColumnLine { get; } = string.Join(",", Columns);

        /// <summary>The row as one line, without a line break.</summary>
        public static string Format(HistoryRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            var fields = new[]
            {
                MinuteAggregator.ToUtc(row.Utc).ToString(TimeFormat, CultureInfo.InvariantCulture),
                row.Seconds.ToString(CultureInfo.InvariantCulture),
                Number(row.CpuAvg), Number(row.CpuMax), Number(row.CpuTempAvg), Number(row.CpuTempMax),
                Number(row.RamAvg), Number(row.RamMax),
                Number(row.GpuAvg), Number(row.GpuMax), Number(row.GpuTempMax),
                Number(row.NetUpAvg), Number(row.NetDownAvg),
                Number(row.DiskFreeMinPercent), Number(row.DiskActivityMax),
                Number(row.BatteryPercent), row.OnAc == null ? "" : row.OnAc.Value ? "1" : "0",
                Text(row.TopCpuName), Text(row.TopCpuPath), Number(row.TopCpuPercent),
                Text(row.TopRamName), Number(row.TopRamMb),
            };
            return string.Join(",", fields);
        }

        /// <summary>
        /// Reads one line back. False for the header lines, a blank line, and any line with the
        /// wrong number of fields or a value that does not parse, such as a line cut short.
        /// </summary>
        public static bool TryParse(string line, out HistoryRow? row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return false;

            List<string>? f = Split(line.TrimEnd('\r'));
            if (f == null || f.Count != Columns.Length) return false;

            if (!DateTime.TryParseExact(f[0], TimeFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
                return false;
            if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                return false;

            var n = new float?[Columns.Length];
            foreach (int i in NumberColumns)
                if (!TryNumber(f[i], out n[i])) return false;

            bool? onAc;
            if (f[16].Length == 0) onAc = null;
            else if (f[16] == "1") onAc = true;
            else if (f[16] == "0") onAc = false;
            else return false;

            row = new HistoryRow
            {
                Utc = utc,
                Seconds = seconds,
                CpuAvg = n[2], CpuMax = n[3], CpuTempAvg = n[4], CpuTempMax = n[5],
                RamAvg = n[6], RamMax = n[7],
                GpuAvg = n[8], GpuMax = n[9], GpuTempMax = n[10],
                NetUpAvg = n[11], NetDownAvg = n[12],
                DiskFreeMinPercent = n[13], DiskActivityMax = n[14],
                BatteryPercent = n[15], OnAc = onAc,
                TopCpuName = OrNull(f[17]), TopCpuPath = OrNull(f[18]), TopCpuPercent = n[19],
                TopRamName = OrNull(f[20]), TopRamMb = n[21],
            };
            return true;
        }

        /// <summary>
        /// Several rows as one row starting at <paramref name="bucketUtc"/> and covering
        /// <paramref name="seconds"/>: the average of the averages, the highest peak, the lowest free
        /// space, the last battery reading by time, and the top process named most often (any case;
        /// a tie goes to the earliest), with its first known path and its average share.
        /// </summary>
        public static HistoryRow Combine(IReadOnlyList<HistoryRow> rows, DateTime bucketUtc, int seconds)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var ordered = rows.OrderBy(r => r.Utc).ToList();
            string? topCpu = MostFrequent(ordered.Select(r => r.TopCpuName));
            string? topRam = MostFrequent(ordered.Select(r => r.TopRamName));
            var cpuRows = ordered.Where(r => topCpu != null && string.Equals(r.TopCpuName, topCpu, StringComparison.OrdinalIgnoreCase)).ToList();
            var ramRows = ordered.Where(r => topRam != null && string.Equals(r.TopRamName, topRam, StringComparison.OrdinalIgnoreCase)).ToList();

            return new HistoryRow
            {
                Utc = MinuteAggregator.ToUtc(bucketUtc),
                Seconds = seconds,
                CpuAvg = Average(ordered, r => r.CpuAvg),
                CpuMax = Highest(ordered, r => r.CpuMax),
                CpuTempAvg = Average(ordered, r => r.CpuTempAvg),
                CpuTempMax = Highest(ordered, r => r.CpuTempMax),
                RamAvg = Average(ordered, r => r.RamAvg),
                RamMax = Highest(ordered, r => r.RamMax),
                GpuAvg = Average(ordered, r => r.GpuAvg),
                GpuMax = Highest(ordered, r => r.GpuMax),
                GpuTempMax = Highest(ordered, r => r.GpuTempMax),
                NetUpAvg = Average(ordered, r => r.NetUpAvg),
                NetDownAvg = Average(ordered, r => r.NetDownAvg),
                DiskFreeMinPercent = Lowest(ordered, r => r.DiskFreeMinPercent),
                DiskActivityMax = Highest(ordered, r => r.DiskActivityMax),
                BatteryPercent = ordered.Select(r => r.BatteryPercent).LastOrDefault(v => v.HasValue),
                OnAc = ordered.Select(r => r.OnAc).LastOrDefault(v => v.HasValue),
                TopCpuName = topCpu,
                TopCpuPath = cpuRows.Select(r => r.TopCpuPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                TopCpuPercent = Average(cpuRows, r => r.TopCpuPercent),
                TopRamName = topRam,
                TopRamMb = Average(ramRows, r => r.TopRamMb),
            };
        }

        private static string Number(float? value) =>
            value is float v && !float.IsNaN(v) && !float.IsInfinity(v)
                ? ((double)v).ToString("0.0", CultureInfo.InvariantCulture)
                : "";

        /// <summary>A name or path as one field: line breaks become spaces, commas and quotes are quoted.</summary>
        private static string Text(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value.Replace('\r', ' ').Replace('\n', ' ');
            return clean.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + clean.Replace("\"", "\"\"") + "\"" : clean;
        }

        private static string? OrNull(string field) => field.Length == 0 ? null : field;

        private static bool TryNumber(string field, out float? value)
        {
            value = null;
            if (field.Length == 0) return true;
            if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;
            value = parsed;
            return true;
        }

        /// <summary>Splits one CSV line; null when a quoted field is never closed (a line cut short).</summary>
        private static List<string>? Split(string line)
        {
            var fields = new List<string>(Columns.Length);
            var current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c != '"') current.Append(c);
                    else if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"' && current.Length == 0) quoted = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }

            if (quoted) return null;
            fields.Add(current.ToString());
            return fields;
        }

        private static string? MostFrequent(IEnumerable<string?> names)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstSeen = new List<string>();
            foreach (string? name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (counts.TryGetValue(name, out int count)) counts[name] = count + 1;
                else { counts[name] = 1; firstSeen.Add(name); }
            }

            string? best = null;
            int bestCount = 0;
            foreach (string name in firstSeen)
            {
                if (counts[name] > bestCount) { best = name; bestCount = counts[name]; }
            }
            return best;
        }

        private static float? Average(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            return values.Count == 0 ? null : (float)values.Average();
        }

        private static float? Highest(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).ToList();
            return values.Count == 0 ? null : values.Max();
        }

        private static float? Lowest(IEnumerable<HistoryRow> rows, Func<HistoryRow, float?> pick)
        {
            var values = rows.Select(pick).Where(v => v.HasValue).ToList();
            return values.Count == 0 ? null : values.Min();
        }
    }
}
```

- [ ] **Step 5: Write the store**

Create `Services/History/HistoryStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The 7-day history on disk: <c>yyyyMMdd.csv</c> per UTC day in <see cref="Folder"/>.
    ///
    /// <para>
    /// The last 24 to 48 hours stay per minute; <see cref="Maintain"/> rewrites a day that ended
    /// more than 24 hours ago as 5-minute rows and deletes a day that ended more than
    /// <see cref="RetentionDays"/> days ago, which keeps the folder near 10 MB at most.
    /// </para>
    ///
    /// <para>
    /// Files are opened with <see cref="FileShare.ReadWrite"/> so the <c>--mcp</c> bridge can read
    /// while the app appends. Every public member takes one lock and never throws for I/O: a
    /// failure is reported through <c>warn</c> once per kind until that kind works again. The
    /// constructor touches no disk; the folder appears with the first row.
    /// </para>
    /// </summary>
    public sealed class HistoryStore
    {
        /// <summary>Days a day file is kept after the day ended.</summary>
        public const int RetentionDays = 7;

        private const int ThinnedSeconds = 300;
        private static readonly TimeSpan KeepMinuteRowsFor = TimeSpan.FromHours(24);
        private static readonly Regex DayFileName = new(@"^\d{8}\.csv$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly Func<DateTime> _utcClock;
        private readonly Action<string> _warn;
        private readonly object _gate = new();
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

        /// <param name="folder">Where the day files live; created by the first <see cref="Append"/>.</param>
        /// <param name="utcClock">"Now" for <see cref="Maintain"/>.</param>
        /// <param name="warn">Told about I/O failures, once per kind.</param>
        public HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null)
        {
            Folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _utcClock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _warn = warn ?? (_ => { });
        }

        /// <summary><c>%APPDATA%\MicaStats\history</c>.</summary>
        public static string DefaultFolder => Path.Combine(DiagnosticsLog.DataDir, "history");

        /// <summary>The folder holding the day files.</summary>
        public string Folder { get; }

        /// <summary>
        /// Adds a row to the file of its UTC day, writing the two header lines when the file is new
        /// or empty. A line cut short by an earlier crash is ended first, so it can never swallow
        /// this row.
        /// </summary>
        public void Append(HistoryRow row)
        {
            if (row == null) return;

            lock (_gate)
            {
                string path = "";
                try
                {
                    path = PathFor(row.Utc);
                    Directory.CreateDirectory(Folder);
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

                    var text = new StringBuilder();
                    if (stream.Length == 0)
                    {
                        text.Append(Header());
                    }
                    else
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        if (stream.ReadByte() != '\n') text.Append('\n');
                    }
                    text.Append(HistoryCsv.Format(row)).Append('\n');

                    byte[] bytes = Utf8NoBom.GetBytes(text.ToString());
                    stream.Seek(0, SeekOrigin.End);
                    stream.Write(bytes, 0, bytes.Length);
                    Succeeded("write");
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("write", "History could not be written to " + (path.Length > 0 ? path : Folder) + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Every row from <paramref name="fromUtc"/> to <paramref name="toUtc"/>, both included,
        /// sorted by time. Damaged lines and a last line still being written are skipped.
        /// </summary>
        public IReadOnlyList<HistoryRow> Read(DateTime fromUtc, DateTime toUtc)
        {
            DateTime from = MinuteAggregator.ToUtc(fromUtc);
            DateTime to = MinuteAggregator.ToUtc(toUtc);
            var rows = new List<HistoryRow>();
            if (from > to) return rows;

            lock (_gate)
            {
                foreach (var file in DayFiles())
                {
                    if (file.Day < from.Date || file.Day > to.Date) continue;
                    foreach (HistoryRow row in ReadFile(file.FilePath))
                        if (row.Utc >= from && row.Utc <= to) rows.Add(row);
                }
            }
            return rows.OrderBy(r => r.Utc).ToList();
        }

        /// <summary>
        /// Thins every day that ended more than 24 hours ago to 5-minute rows and deletes every day
        /// that ended more than <see cref="RetentionDays"/> days ago. Files that are not day files
        /// are left alone. Safe to run at any time; a day already thinned is not rewritten.
        /// </summary>
        public void Maintain()
        {
            DateTime now = MinuteAggregator.ToUtc(_utcClock());
            lock (_gate)
            {
                foreach (var file in DayFiles())
                {
                    TimeSpan sinceDayEnded = now - file.Day.AddDays(1);
                    try
                    {
                        if (sinceDayEnded > TimeSpan.FromDays(RetentionDays))
                        {
                            File.Delete(file.FilePath);
                            DeleteIfThere(file.FilePath + AtomicFile.ReadySuffix);
                            DeleteIfThere(file.FilePath + AtomicFile.TempSuffix);
                        }
                        else if (sinceDayEnded > KeepMinuteRowsFor)
                        {
                            Thin(file.FilePath);
                        }
                        Succeeded("maintain");
                    }
                    catch (Exception ex) when (IsIo(ex))
                    {
                        Failed("maintain", "History maintenance failed on " + file.FilePath + ": " + ex.Message);
                    }
                }
            }
        }

        /// <summary>Bytes on disk in <see cref="Folder"/>, for Settings; 0 when there is no folder.</summary>
        public long SizeBytes()
        {
            lock (_gate)
            {
                try
                {
                    if (!Directory.Exists(Folder)) return 0;
                    return new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("size", "The size of the history folder could not be read: " + ex.Message);
                    return 0;
                }
            }
        }

        /// <summary>Removes the whole folder (Settings > AI > Delete history). Recording, if on, starts a new one.</summary>
        public void DeleteAll()
        {
            lock (_gate)
            {
                try
                {
                    if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
                    Succeeded("delete");
                }
                catch (Exception ex) when (IsIo(ex))
                {
                    Failed("delete", "The history folder could not be deleted: " + ex.Message);
                }
            }
        }

        private string PathFor(DateTime utc) =>
            Path.Combine(Folder, MinuteAggregator.ToUtc(utc).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");

        private static string Header() => HistoryCsv.VersionLine + "\n" + HistoryCsv.ColumnLine + "\n";

        /// <summary>The day files present, with the UTC day each one holds.</summary>
        private List<(DateTime Day, string FilePath)> DayFiles()
        {
            var files = new List<(DateTime Day, string FilePath)>();
            if (!Directory.Exists(Folder)) return files;

            try
            {
                foreach (string path in Directory.EnumerateFiles(Folder))
                {
                    string name = Path.GetFileName(path);
                    if (!DayFileName.IsMatch(name)) continue;
                    if (DateTime.TryParseExact(name.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day))
                        files.Add((day, path));
                }
                Succeeded("list");
            }
            catch (Exception ex) when (IsIo(ex))
            {
                Failed("list", "The history folder could not be listed: " + ex.Message);
            }
            return files;
        }

        /// <summary>
        /// The complete rows of one file. Anything after the last line break is a line still being
        /// written (or cut short by a crash) and is ignored.
        /// </summary>
        private List<HistoryRow> ReadFile(string path)
        {
            var rows = new List<HistoryRow>();
            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
                Succeeded("read");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return rows;
            }
            catch (Exception ex) when (IsIo(ex))
            {
                Failed("read", "History could not be read from " + path + ": " + ex.Message);
                return rows;
            }

            int end = text.LastIndexOf('\n');
            if (end < 0) return rows;
            foreach (string line in text.Substring(0, end).Split('\n'))
                if (HistoryCsv.TryParse(line, out HistoryRow? row) && row != null) rows.Add(row);
            return rows;
        }

        /// <summary>Rewrites one day file as 5-minute rows, atomically; a day with no 1-minute rows is left alone.</summary>
        private void Thin(string path)
        {
            List<HistoryRow> rows = ReadFile(path);
            if (rows.Count == 0 || rows.All(r => r.Seconds >= ThinnedSeconds)) return;

            var text = new StringBuilder(Header());
            foreach (var bucket in rows.GroupBy(r => BucketOf(r.Utc)).OrderBy(g => g.Key))
                text.Append(HistoryCsv.Format(HistoryCsv.Combine(bucket.ToList(), bucket.Key, ThinnedSeconds))).Append('\n');

            AtomicFile.Write(path, Utf8NoBom.GetBytes(text.ToString()));
        }

        private static DateTime BucketOf(DateTime utc)
        {
            long bucketTicks = TimeSpan.FromSeconds(ThinnedSeconds).Ticks;
            DateTime u = MinuteAggregator.ToUtc(utc);
            return new DateTime(u.Ticks - u.Ticks % bucketTicks, DateTimeKind.Utc);
        }

        private static void DeleteIfThere(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static bool IsIo(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

        private void Failed(string kind, string message)
        {
            if (_failing.Add(kind)) _warn(message);
        }

        private void Succeeded(string kind) => _failing.Remove(kind);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryCsvTests|FullyQualifiedName~HistoryStoreTests"`
Expected: PASS, 19 tests (7 in `HistoryCsvTests`, counting both culture rows, and 12 in `HistoryStoreTests`).

- [ ] **Step 7: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (874 + 19 = 893).

```bash
git add Services/History/HistoryCsv.cs Services/History/HistoryStore.cs tests/Kil0bitSystemMonitor.Tests/HistoryCsvTests.cs tests/Kil0bitSystemMonitor.Tests/HistoryStoreTests.cs
git commit -F - <<'EOF'
feat(ai): history files with thinning and 7-day retention

One CSV file per UTC day, written with InvariantCulture and read with
FileShare.ReadWrite so the MCP bridge can read while the app writes.
Days that ended over 24 h ago become 5-minute rows, days older than 7
days are deleted, damaged and half-written lines are skipped, and a
failure is logged once instead of thrown.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: Top-process source, HistoryRecorder, AlertMonitor.Recent and App.Ai.cs

This task turns the history on. `ProcessPaths` reads an exe path by pid; `SamplerTopProcessSource` takes a short lease on the shared `ProcessSampler` once a minute (Retain, wait for CPU data, at most 6 s, Release) and reports the top process by CPU and by memory (spec amendment 1). `HistoryRecorder` feeds telemetry ticks through `MinuteAggregator` into `HistoryStore` while *Keep 7 days of history* is on. `AlertMonitor` starts keeping its last 50 raised alerts for `list_alerts`. `App.Ai.cs` is created with the four anchors every later task builds on, and `App.xaml.cs` gets its two hook lines.

**Files:**
- Create: `Services/History/ProcessPaths.cs`
- Create: `Services/History/TopProcessSource.cs` (`ITopProcessSource`, `SamplerTopProcessSource`)
- Create: `Services/History/HistoryRecorder.cs`
- Modify: `Services/Diagnostics/AlertMonitor.cs` (`Recent`, `Remember`)
- Create: `App.Ai.cs`
- Modify: `App.xaml.cs` (`StartAi(...)` after `StartDiagnostics(config);`, `StopAi();` in `OnExit`)
- Test: `tests/Kil0bitSystemMonitor.Tests/HistoryTopProcessTests.cs`, `tests/Kil0bitSystemMonitor.Tests/HistoryRecorderTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiRecentAlertsTests.cs`

**Interfaces:**
- Consumes: `HistoryStore`, `HistoryRow` (Task 4, 3); `MinuteAggregator { Add; SetTop; Flush; internal static ToUtc; internal static MinuteOf }` (Task 3); `TopProcessSample` (Task 3); `Redactor.ForCurrentUser()`, `Redactor.Redact(string)` (Task 2); `AppConfig.AiHistoryEnabled` (Task 1); `AiTestEnv` (Task 1); existing `ProcessSampler { void Retain(); void Release(); bool Enabled; bool HasCpuData; IReadOnlyList<ProcessUsage> TopByCpu, TopByRam; }`, `ProcessUsage(string Name, int Pid, float CpuPercent, long WorkingSet)`, `TelemetryService.MetricsUpdated` (`event Action<SystemMetrics>?`, raised on the timer thread), `App.SharedProcessSampler`, `App.ConfigService`, private static `App.s_alerts`, `AlertEvent`, `AlertRule.Defaults`, `MetricsHistory(int capacity)`.
- Produces:
  - `namespace Kil0bitSystemMonitor.Services.History`: `public static class ProcessPaths { static string? TryGetPath(int pid); }`; `public interface ITopProcessSource { Task<TopProcessSample?> SampleAsync(CancellationToken ct); }` (never throws; null on timeout or cancellation); `public sealed class SamplerTopProcessSource : ITopProcessSource { SamplerTopProcessSource(ProcessSampler sampler, TimeSpan? timeout = null); }` (default 6 s; `CpuPath` redacted); `public sealed class HistoryRecorder : IDisposable { HistoryRecorder(HistoryStore store, ITopProcessSource top, Func<DateTime> utcClock, Action<string>? warn = null); bool IsRunning { get; } void Start(); void Stop(); void OnMetrics(SystemMetrics m); void Dispose(); internal Task Maintenance { get; } }`.
  - `AlertMonitor`: `public IReadOnlyList<AlertEvent> Recent { get; }` (newest first, last 50 raised, a copy), `internal void Remember(AlertEvent alert)`.
  - `App.Ai.cs` (`public partial class App`, file-scoped namespace `Kil0bitSystemMonitor`): `public static HistoryStore History { get; }`, `internal static AlertMonitor? AlertMonitorForAi { get; }`, `internal static void StartAi(ConfigService config, TelemetryService telemetry, MetricsHistory history, Dispatcher ui)`, `internal static void ApplyAiSettings()`, `internal static void StopAi()`, private `static Action<string> AiWarn(string area)`. Anchor lines, exactly: `    // AI anchor: members` (class level, last line of the class), `        // AI anchor: start` (last line of `StartAi`; `config`, `telemetry`, `history`, `ui` in scope), `        // AI anchor: apply` (last line of `ApplyAiSettings`; local `AppConfig config`, non-null, in scope), `        // AI anchor: stop` (first line of `StopAi`).

- [ ] **Step 1: Write the failing process tests**

Create `tests/Kil0bitSystemMonitor.Tests/HistoryTopProcessTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Exe paths by pid and the once-a-minute top-process sample. Against the live system, like
    /// ProcessSamplerTests: the sampler walks a kernel structure that only reality can check.
    /// </summary>
    public class HistoryTopProcessTests
    {
        [Fact]
        public void The_path_of_a_running_process_is_found()
        {
            string? path = ProcessPaths.TryGetPath(Environment.ProcessId);

            Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        [InlineData(int.MaxValue)]
        public void There_is_no_path_for_an_impossible_pid(int pid) => Assert.Null(ProcessPaths.TryGetPath(pid));

        [Fact]
        public async Task The_sampler_source_names_the_top_processes_and_releases_its_lease()
        {
            using var sampler = new ProcessSampler();
            var source = new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8));

            TopProcessSample? sample = await source.SampleAsync(CancellationToken.None);

            Assert.NotNull(sample);
            Assert.False(string.IsNullOrWhiteSpace(sample!.CpuName));
            Assert.False(string.IsNullOrWhiteSpace(sample.RamName));
            Assert.InRange(sample.CpuPercent!.Value, 0f, 100f);
            Assert.True(sample.RamMb > 0);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.DoesNotContain(profile, sample.CpuPath ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(sampler.Enabled);   // the lease went back
        }

        [Fact]
        public async Task A_lease_someone_else_holds_is_left_alone()
        {
            using var sampler = new ProcessSampler();
            sampler.Retain();   // as the slowdown recorder does all day

            await new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8)).SampleAsync(CancellationToken.None);

            Assert.True(sampler.Enabled);
            sampler.Release();
            Assert.False(sampler.Enabled);
        }

        [Fact]
        public async Task No_data_in_time_gives_null()
        {
            var sampler = new ProcessSampler();
            sampler.Dispose();   // a disposed sampler never produces a sample
            var waited = Stopwatch.StartNew();

            TopProcessSample? sample = await new SamplerTopProcessSource(sampler, TimeSpan.FromMilliseconds(300))
                .SampleAsync(CancellationToken.None);

            Assert.Null(sample);
            Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(300));
        }

        [Fact]
        public async Task A_cancelled_sample_gives_null_and_releases_the_lease()
        {
            using var sampler = new ProcessSampler();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            TopProcessSample? sample = await new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8)).SampleAsync(cts.Token);

            Assert.Null(sample);
            Assert.False(sampler.Enabled);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryTopProcessTests"`
Expected: build FAILS: CS0103 `The name 'ProcessPaths' does not exist in the current context` and CS0246 for `SamplerTopProcessSource`.

- [ ] **Step 3: Write ProcessPaths**

Create `Services/History/ProcessPaths.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// The full exe path of a process, for the history's top-CPU column.
    ///
    /// <para>
    /// <c>QueryFullProcessImageNameW</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, the one
    /// access right an unelevated process gets for almost every other process, where
    /// <see cref="System.Diagnostics.Process.MainModule"/> is refused for a third of them. Called
    /// for one pid once a minute, never per row.
    /// </para>
    /// </summary>
    public static class ProcessPaths
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>The exe path, or null when the process is gone, protected, or <paramref name="pid"/> is not a pid.</summary>
        public static string? TryGetPath(int pid)
        {
            if (pid <= 0) return null;

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var buffer = new char[32768];   // the longest path Windows can return
                int size = buffer.Length;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
                    ? new string(buffer, 0, size)
                    : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
```

- [ ] **Step 4: Write the top-process source**

Create `Services/History/TopProcessSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>Where <see cref="HistoryRecorder"/> gets each minute's top processes; a fake in tests.</summary>
    public interface ITopProcessSource
    {
        /// <summary>
        /// The busiest processes right now. Never throws: null when no ranking arrived in time or
        /// <paramref name="ct"/> was cancelled.
        /// </summary>
        Task<TopProcessSample?> SampleAsync(CancellationToken ct);
    }

    /// <summary>
    /// Reads the top processes from the shared <see cref="ProcessSampler"/> with a short lease.
    ///
    /// <para>
    /// The sampler only runs while someone holds a lease, and CPU share is a difference between
    /// two samples two seconds apart. So each call retains it, waits until it has CPU data (at
    /// once when the slowdown recorder or an open window already holds a lease, about two seconds
    /// otherwise, never longer than the timeout), reads the two rankings, and releases it. Nothing
    /// samples continuously on the history's behalf.
    /// </para>
    /// </summary>
    public sealed class SamplerTopProcessSource : ITopProcessSource
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
        private static readonly Lazy<Redactor> CurrentUser = new(Redactor.ForCurrentUser);

        private readonly ProcessSampler _sampler;
        private readonly TimeSpan _timeout;

        /// <param name="sampler">Normally <c>App.SharedProcessSampler</c>.</param>
        /// <param name="timeout">How long to wait for CPU data; 6 seconds when null.</param>
        public SamplerTopProcessSource(ProcessSampler sampler, TimeSpan? timeout = null)
        {
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _timeout = timeout ?? TimeSpan.FromSeconds(6);
        }

        /// <inheritdoc/>
        public async Task<TopProcessSample?> SampleAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return null;

            _sampler.Retain();
            try
            {
                var waited = Stopwatch.StartNew();
                while (!_sampler.HasCpuData)
                {
                    if (waited.Elapsed >= _timeout) return null;
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
                return Read();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                // Runs unattended once a minute; a missing top process is only a gap in one row.
                return null;
            }
            finally
            {
                _sampler.Release();
            }
        }

        private TopProcessSample? Read()
        {
            ProcessUsage? cpu = FirstOf(_sampler.TopByCpu);
            ProcessUsage? ram = FirstOf(_sampler.TopByRam);
            if (cpu == null && ram == null) return null;

            // Stored redacted (spec: "redacted path"); tool output is redacted again on the way out.
            string? path = cpu == null ? null : ProcessPaths.TryGetPath(cpu.Pid);
            return new TopProcessSample(
                cpu?.Name,
                path == null ? null : CurrentUser.Value.Redact(path),
                cpu?.CpuPercent,
                ram?.Name,
                ram == null ? null : (float)(ram.WorkingSet / 1024d / 1024d));
        }

        private static ProcessUsage? FirstOf(IReadOnlyList<ProcessUsage> ranking) => ranking.Count > 0 ? ranking[0] : null;
    }
}
```

- [ ] **Step 5: Run the process tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryTopProcessTests"`
Expected: PASS, 8 tests (the sampler tests take about 2 s each).

- [ ] **Step 6: Write the failing recorder tests**

Create `tests/Kil0bitSystemMonitor.Tests/HistoryRecorderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Telemetry ticks to history rows: on/off, top processes, maintenance, failures.</summary>
    public class HistoryRecorderTests
    {
        private static readonly DateTime T0 = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        private static readonly TopProcessSample Chrome =
            new("chrome.exe", @"%USERPROFILE%\AppData\Local\chrome.exe", 42.5f, "code.exe", 812.3f);

        /// <summary>A top-process source that answers however the test says, and counts the calls.</summary>
        private sealed class FakeTop : ITopProcessSource
        {
            private readonly Func<CancellationToken, Task<TopProcessSample?>> _answer;
            private int _calls;

            public FakeTop(Func<CancellationToken, Task<TopProcessSample?>> answer) => _answer = answer;

            public int Calls => Volatile.Read(ref _calls);

            public Task<TopProcessSample?> SampleAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                return _answer(ct);
            }
        }

        private static (HistoryStore Store, HistoryRecorder Recorder, FakeTop Top) Build(
            AiTestEnv env, Func<CancellationToken, Task<TopProcessSample?>>? top = null)
        {
            var store = new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow, env.Warn);
            var fake = new FakeTop(top ?? (_ => Task.FromResult<TopProcessSample?>(Chrome)));
            return (store, new HistoryRecorder(store, fake, () => env.Clock.UtcNow, env.Warn), fake);
        }

        /// <summary>One telemetry tick, <paramref name="seconds"/> after 10:00 UTC.</summary>
        private static void Tick(AiTestEnv env, HistoryRecorder recorder, double seconds, float cpu = 10)
        {
            env.Clock.UtcNow = T0.AddSeconds(seconds);
            recorder.OnMetrics(new SystemMetrics { CpuUsage = cpu, RamPercent = 50 });
        }

        private static IReadOnlyList<HistoryRow> Rows(HistoryStore store) => store.Read(T0, T0.AddHours(1));

        [Fact]
        public void A_finished_minute_is_written_when_the_next_one_starts()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            recorder.Start();

            Tick(env, recorder, 5, cpu: 10);
            Tick(env, recorder, 30, cpu: 30);
            Assert.Empty(Rows(store));

            Tick(env, recorder, 65, cpu: 99);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal(T0, row.Utc);
            Assert.Equal(20f, row.CpuAvg);
            Assert.Equal(30f, row.CpuMax);
            recorder.Dispose();
        }

        [Fact]
        public void Nothing_is_recorded_while_stopped_and_stop_writes_the_minute_in_progress()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env);

            Tick(env, recorder, 5);
            Tick(env, recorder, 65);
            Assert.False(recorder.IsRunning);
            Assert.Empty(Rows(store));

            recorder.Start();
            Assert.True(recorder.IsRunning);
            Tick(env, recorder, 125, cpu: 40);
            recorder.Stop();
            Assert.False(recorder.IsRunning);

            Tick(env, recorder, 185);
            Tick(env, recorder, 245);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal(T0.AddMinutes(2), row.Utc);
            Assert.Equal(40f, row.CpuAvg);
            Assert.Equal(1, top.Calls);
        }

        [Fact]
        public void Each_minute_asks_for_the_top_processes_once_and_keeps_them_with_that_minute()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env);
            recorder.Start();

            Tick(env, recorder, 5);
            Tick(env, recorder, 35);
            Tick(env, recorder, 65);

            HistoryRow row = Assert.Single(Rows(store));
            Assert.Equal("chrome.exe", row.TopCpuName);
            Assert.Equal(@"%USERPROFILE%\AppData\Local\chrome.exe", row.TopCpuPath);
            Assert.Equal(42.5f, row.TopCpuPercent);
            Assert.Equal("code.exe", row.TopRamName);
            Assert.Equal(812.3f, row.TopRamMb);
            Assert.Equal(2, top.Calls);   // 10:00 and 10:01
            recorder.Dispose();
        }

        [Fact]
        public void A_top_sample_that_arrives_after_its_minute_is_dropped()
        {
            using var env = new AiTestEnv();
            var pending = new TaskCompletionSource<TopProcessSample?>();
            var (store, recorder, top) = Build(env, _ => pending.Task);
            recorder.Start();

            Tick(env, recorder, 5);    // asks for 10:00
            Tick(env, recorder, 65);   // 10:00 is written without it; no second request while one is out
            pending.SetResult(Chrome);
            recorder.Stop();

            var rows = Rows(store);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Null(r.TopCpuName));
            Assert.Equal(1, top.Calls);
        }

        [Fact]
        public async Task Start_runs_the_maintenance()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            store.Append(new HistoryRow { Utc = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), Seconds = 60, CpuAvg = 5 });
            string file = Path.Combine(store.Folder, "20260920.csv");
            Assert.True(File.Exists(file));

            recorder.Start();
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(file));   // that day ended over 9 days ago
            recorder.Dispose();
        }

        [Fact]
        public async Task A_new_utc_day_runs_the_maintenance_again()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);
            store.Append(new HistoryRow { Utc = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc), Seconds = 60, CpuAvg = 5 });
            string file = Path.Combine(store.Folder, "20260924.csv");
            env.Clock.UtcNow = new DateTime(2026, 10, 1, 23, 59, 30, DateTimeKind.Utc);

            recorder.Start();
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(file));    // its day ended 6 days 23:59:30 ago

            env.Clock.UtcNow = new DateTime(2026, 10, 2, 0, 0, 10, DateTimeKind.Utc);
            recorder.OnMetrics(new SystemMetrics { CpuUsage = 5 });
            await recorder.Maintenance.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(file));   // now over 7 days
            recorder.Dispose();
        }

        [Fact]
        public void Start_and_stop_are_idempotent_and_dispose_is_final()
        {
            using var env = new AiTestEnv();
            var (store, recorder, _) = Build(env);

            recorder.Start();
            recorder.Start();
            Assert.True(recorder.IsRunning);
            Tick(env, recorder, 5);
            recorder.Stop();
            recorder.Stop();
            Assert.Single(Rows(store));   // the second Stop wrote nothing more

            recorder.Dispose();
            recorder.Start();
            Assert.False(recorder.IsRunning);
        }

        [Fact]
        public void A_failing_top_source_warns_once_and_rows_are_still_written()
        {
            using var env = new AiTestEnv();
            var (store, recorder, top) = Build(env, _ => throw new InvalidOperationException("sampler gone"));
            recorder.Start();

            Tick(env, recorder, 5);
            Tick(env, recorder, 65);
            Tick(env, recorder, 125);
            recorder.Stop();

            var rows = Rows(store);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Null(r.TopCpuName));
            Assert.Equal(3, top.Calls);
            string warning = Assert.Single(env.Warnings);
            Assert.Contains("sampler gone", warning);
        }
    }
}
```

- [ ] **Step 7: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryRecorderTests"`
Expected: build FAILS: CS0246 `The type or namespace name 'HistoryRecorder' could not be found`.

- [ ] **Step 8: Write the recorder**

Create `Services/History/HistoryRecorder.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>
    /// Feeds the telemetry ticks into a <see cref="MinuteAggregator"/> and writes each finished
    /// minute to the <see cref="HistoryStore"/>, while <c>Keep 7 days of history</c> is on.
    ///
    /// <para>
    /// At the first tick of each minute it asks the <see cref="ITopProcessSource"/> for the busiest
    /// processes; the answer joins that minute's row if it arrives before the minute ends, and is
    /// dropped otherwise. At most one request is out at a time. Maintenance (thinning and cleanup)
    /// runs off the telemetry thread at <see cref="Start"/> and whenever the UTC date changes.
    /// </para>
    ///
    /// <para>
    /// <see cref="OnMetrics"/> arrives on the telemetry timer thread and <see cref="Start"/> /
    /// <see cref="Stop"/> on the UI thread, so the aggregator lives behind one lock. A write that
    /// fails is logged once by the store and the next minute simply tries again.
    /// </para>
    /// </summary>
    public sealed class HistoryRecorder : IDisposable
    {
        private readonly HistoryStore _store;
        private readonly ITopProcessSource _top;
        private readonly Func<DateTime> _utcClock;
        private readonly Action<string> _warn;
        private readonly object _gate = new();

        private MinuteAggregator _aggregator = new();
        private CancellationTokenSource _cts = new();
        private Task _maintenance = Task.CompletedTask;
        private DateTime _minute;
        private DateTime _date;
        private bool _running;
        private bool _topPending;
        private bool _warnedTop;
        private bool _disposed;

        /// <param name="store">Where finished minutes go.</param>
        /// <param name="top">Asked once a minute for the busiest processes.</param>
        /// <param name="utcClock">The time of each tick; telemetry snapshots carry none.</param>
        /// <param name="warn">Told when the top-process source fails, once.</param>
        public HistoryRecorder(HistoryStore store, ITopProcessSource top, Func<DateTime> utcClock, Action<string>? warn = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _top = top ?? throw new ArgumentNullException(nameof(top));
            _utcClock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _warn = warn ?? (_ => { });
        }

        /// <summary>Whether ticks are being recorded.</summary>
        public bool IsRunning
        {
            get { lock (_gate) return _running; }
        }

        /// <summary>The latest maintenance run, so tests can wait for it.</summary>
        internal Task Maintenance
        {
            get { lock (_gate) return _maintenance; }
        }

        /// <summary>Starts recording with an empty minute and runs the maintenance in the background. Idempotent.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running || _disposed) return;
                _running = true;
                _aggregator = new MinuteAggregator();
                _cts = new CancellationTokenSource();
                _minute = default;
                _date = MinuteAggregator.ToUtc(_utcClock()).Date;
                _maintenance = Task.Run(RunMaintenance);
            }
        }

        /// <summary>Stops recording and writes the minute in progress, so turning history off loses nothing. Idempotent.</summary>
        public void Stop()
        {
            HistoryRow? last;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                last = _aggregator.Flush();
                _cts.Cancel();
            }
            if (last != null) _store.Append(last);
        }

        /// <summary>
        /// One telemetry snapshot (<c>TelemetryService.MetricsUpdated</c>, timer thread). Ignored while
        /// stopped. Writes the previous minute when this one starts a new minute, asks for that
        /// minute's top processes, and starts the maintenance when the UTC date has changed.
        /// </summary>
        public void OnMetrics(SystemMetrics m)
        {
            if (m == null) return;

            HistoryRow? finished;
            DateTime minute;
            bool sampleTop = false;
            bool maintain = false;
            CancellationToken token = default;

            lock (_gate)
            {
                if (!_running) return;

                DateTime utc = MinuteAggregator.ToUtc(_utcClock());
                minute = MinuteAggregator.MinuteOf(utc);
                finished = _aggregator.Add(m, utc);

                if (minute != _minute)
                {
                    _minute = minute;
                    if (!_topPending)
                    {
                        _topPending = true;
                        sampleTop = true;
                        token = _cts.Token;
                    }
                }

                if (utc.Date != _date)
                {
                    _date = utc.Date;
                    maintain = true;
                }
            }

            if (finished != null) _store.Append(finished);
            if (maintain)
            {
                var task = Task.Run(RunMaintenance);
                lock (_gate) _maintenance = task;
            }
            if (sampleTop) _ = SampleTopAsync(minute, token);
        }

        /// <summary>Stops for good; <see cref="Start"/> does nothing afterwards.</summary>
        public void Dispose()
        {
            Stop();
            lock (_gate) _disposed = true;
        }

        private async Task SampleTopAsync(DateTime minute, CancellationToken ct)
        {
            try
            {
                TopProcessSample? sample = await _top.SampleAsync(ct).ConfigureAwait(false);
                if (sample == null) return;

                lock (_gate)
                {
                    // A sample that arrives after its minute was written belongs to no row.
                    if (_running && _minute == minute) _aggregator.SetTop(sample);
                }
            }
            catch (Exception ex)
            {
                bool first;
                lock (_gate)
                {
                    first = !_warnedTop;
                    _warnedTop = true;
                }
                if (first) _warn("History could not read the top processes: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (_gate) _topPending = false;
            }
        }

        private void RunMaintenance()
        {
            try
            {
                _store.Maintain();
            }
            catch (Exception ex)
            {
                _warn("History maintenance failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
```

- [ ] **Step 9: Run the recorder tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~HistoryRecorderTests"`
Expected: PASS, 8 tests.

- [ ] **Step 10: Write the failing recent-alerts tests**

Raising an alert through `AlertMonitor` also writes to `DiagnosticsLog` (`%APPDATA%`), so these tests drive the list through `Remember`, the one line `OnMetrics` gains.

Create `tests/Kil0bitSystemMonitor.Tests/AiRecentAlertsTests.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The last raised alerts, kept in memory for the list_alerts tool.</summary>
    public class AiRecentAlertsTests
    {
        private static AlertEvent Event(int n) =>
            new(AlertRule.Defaults[0], n, "", new DateTime(2026, 9, 30, 10, 0, 0).AddMinutes(n));

        [Fact]
        public void Nothing_is_recent_before_an_alert_fires()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            Assert.Empty(monitor.Recent);
        }

        [Fact]
        public void Recent_lists_the_newest_first_and_keeps_fifty()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            for (int i = 0; i < 60; i++) monitor.Remember(Event(i));

            var recent = monitor.Recent;
            Assert.Equal(50, recent.Count);
            Assert.Equal(59d, recent[0].Value);
            Assert.Equal(10d, recent[49].Value);
        }

        [Fact]
        public void Recent_is_a_copy()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);
            monitor.Remember(Event(1));

            var before = monitor.Recent;
            monitor.Remember(Event(2));

            Assert.Single(before);
            Assert.Equal(2, monitor.Recent.Count);
        }

        [Fact]
        public void Alerts_can_be_remembered_and_read_from_many_threads()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            Parallel.For(0, 500, i =>
            {
                monitor.Remember(Event(i));
                Assert.InRange(monitor.Recent.Count, 1, 50);
            });

            Assert.Equal(50, monitor.Recent.Count);
        }
    }
}
```

- [ ] **Step 11: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiRecentAlertsTests"`
Expected: build FAILS: CS1061 `'AlertMonitor' does not contain a definition for 'Recent'` (and for `Remember`).

- [ ] **Step 12: Keep the recent alerts in AlertMonitor**

In `Services/Diagnostics/AlertMonitor.cs`, replace:

```csharp
        private IReadOnlyList<AlertRule> _rules = AlertRule.Defaults;
        private bool _running;
        private bool _disposed;
```

with:

```csharp
        private IReadOnlyList<AlertRule> _rules = AlertRule.Defaults;
        private bool _running;
        private bool _disposed;

        /// <summary>How many raised alerts <see cref="Recent"/> keeps.</summary>
        private const int RecentCapacity = 50;

        private readonly object _recentGate = new();
        private readonly List<AlertEvent> _recent = new();
```

Then replace:

```csharp
        /// <summary>Whether a given rule is currently breached.</summary>
        public bool IsFiring(string ruleId) => _evaluator.IsFiring(ruleId);
```

with:

```csharp
        /// <summary>Whether a given rule is currently breached.</summary>
        public bool IsFiring(string ruleId) => _evaluator.IsFiring(ruleId);

        /// <summary>
        /// The last 50 alerts raised, newest first, for the AI <c>list_alerts</c> tool. A copy taken
        /// under a lock: alerts are raised on the UI thread while the tool pipe and the MCP server
        /// read from their own threads. Memory only, so it starts empty at each launch.
        /// </summary>
        public IReadOnlyList<AlertEvent> Recent
        {
            get { lock (_recentGate) return _recent.ToArray(); }
        }

        /// <summary>Adds a raised alert to <see cref="Recent"/>, dropping the oldest beyond 50.</summary>
        internal void Remember(AlertEvent alert)
        {
            lock (_recentGate)
            {
                _recent.Insert(0, alert);
                if (_recent.Count > RecentCapacity) _recent.RemoveRange(RecentCapacity, _recent.Count - RecentCapacity);
            }
        }
```

Then, in `OnMetrics`, directly after the line

```csharp
                    var alert = new AlertEvent(rule, value, detail, now);
```

add the line:

```csharp
                    Remember(alert);
```

(`System.Collections.Generic` is already imported by the file.)

- [ ] **Step 13: Run the recent-alerts tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiRecentAlertsTests|FullyQualifiedName~DiagnosticsTests"`
Expected: PASS: the 4 new tests and every existing `DiagnosticsTests` test.

- [ ] **Step 14: Create App.Ai.cs with the anchors**

Create `App.Ai.cs` at the repository root. It uses a file-scoped namespace so the anchor lines sit at exactly 4 spaces (class level) and 8 spaces (method body); later tasks replace an anchor line with their code followed by the same anchor line.

```csharp
using System;
using System.Threading;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor;

// All of MicaStats AI wiring lives in this file: the 7-day history here, and the assistant, the
// tool pipe and the MCP servers as later work adds them at the "AI anchor" lines. App.xaml.cs only
// calls StartAi after the diagnostics have started and StopAi on exit.
public partial class App
{
    private static HistoryStore? s_historyStore;
    private static HistoryRecorder? s_historyRecorder;
    private static TelemetryService? s_aiTelemetry;
    private static Action<SystemMetrics>? s_aiMetricsHandler;

    /// <summary>
    /// The 7-day history files. Always there (creating it touches no disk), so Settings can show
    /// the size and delete the folder while recording is off, and the tools can read old days.
    /// </summary>
    public static HistoryStore History => LazyInitializer.EnsureInitialized(ref s_historyStore, CreateHistoryStore);

    /// <summary>The alert monitor, for the <c>list_alerts</c> tool; null before the diagnostics start.</summary>
    internal static AlertMonitor? AlertMonitorForAi => s_alerts;

    /// <summary>
    /// Brings up the AI side once the diagnostics exist: the history recorder, fed by every
    /// telemetry tick, and a re-apply whenever a setting whose name starts with "Ai" changes.
    /// Nothing here blocks startup or reaches the network.
    /// </summary>
    internal static void StartAi(ConfigService config, TelemetryService telemetry, MetricsHistory history, Dispatcher ui)
    {
        try
        {
            s_historyRecorder = new HistoryRecorder(History, new SamplerTopProcessSource(SharedProcessSampler),
                                                    () => DateTime.UtcNow, AiWarn("history"));

            // The telemetry timer thread, once a second; the recorder ignores ticks while history is off.
            s_aiTelemetry = telemetry;
            s_aiMetricsHandler = metrics => s_historyRecorder?.OnMetrics(metrics);
            telemetry.MetricsUpdated += s_aiMetricsHandler;

            config.Config.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null && e.PropertyName.StartsWith("Ai", StringComparison.Ordinal))
                    ui.BeginInvoke(new Action(ApplyAiSettings));
            };

            // Queued, not called: everything StartAi builds, including what later work adds at the
            // anchor below, exists before the settings are applied for the first time.
            ui.BeginInvoke(new Action(ApplyAiSettings));
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Starting the history failed", ex);
        }
        // AI anchor: start
    }

    /// <summary>
    /// Pushes the AI settings into the running services. Runs on the UI thread, at startup and
    /// after any change to a setting whose name starts with "Ai"; every start and stop is idempotent.
    /// </summary>
    internal static void ApplyAiSettings()
    {
        AppConfig? config = ConfigService?.Config;
        if (config == null) return;

        try
        {
            if (config.AiHistoryEnabled) s_historyRecorder?.Start();
            else s_historyRecorder?.Stop();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Applying the history setting failed", ex);
        }
        // AI anchor: apply
    }

    /// <summary>
    /// Stops the AI side on exit, before the shared process sampler goes: the history recorder
    /// may hold a sampler lease for its once-a-minute top process. Never throws.
    /// </summary>
    internal static void StopAi()
    {
        // AI anchor: stop
        try
        {
            if (s_aiTelemetry != null && s_aiMetricsHandler != null) s_aiTelemetry.MetricsUpdated -= s_aiMetricsHandler;
            s_aiTelemetry = null;

            // Writes the minute in progress, so an exit loses at most the seconds since the last tick.
            s_historyRecorder?.Dispose();
            s_historyRecorder = null;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Stopping the history failed", ex);
        }
    }

    private static HistoryStore CreateHistoryStore() =>
        new(HistoryStore.DefaultFolder, () => DateTime.UtcNow, AiWarn("history"));

    /// <summary>
    /// A warn callback into the diagnostics log under <paramref name="area"/>. For failures only:
    /// questions, answers, tool data and secrets never go to the log.
    /// </summary>
    private static Action<string> AiWarn(string area) => message => DiagnosticsLog.Warn(area, message);

    // AI anchor: members
}
```

`ConfigService` in a parameter list is the type (only types are looked up there); `ConfigService?.Config` in `ApplyAiSettings` is App's existing static property, exactly as in `ApplyDiagnosticsSettings`.

- [ ] **Step 15: Hook StartAi and StopAi into App.xaml.cs**

In `App.xaml.cs`, `OnStartup`, replace:

```csharp
            StartDiagnostics(config);

            // Windows is shutting down or signing out: every MicaPad note reaches disk now, and
```

with:

```csharp
            StartDiagnostics(config);

            // The 7-day history now, and the assistant and the MCP servers as they are added (App.Ai.cs).
            StartAi(config, m_telemetry, m_history, Dispatcher);

            // Windows is shutting down or signing out: every MicaPad note reaches disk now, and
```

In `OnExit`, replace:

```csharp
                m_captureHotkeys?.Dispose();
                // The watchdog owns nothing else here — its scans are a static kernel snapshot,
```

with:

```csharp
                m_captureHotkeys?.Dispose();
                // Before anything the AI tools read, and before the shared sampler: the history
                // recorder may hold a sampler lease for its once-a-minute top process.
                StopAi();
                // The watchdog owns nothing else here — its scans are a static kernel snapshot,
```

(The second quoted line contains an em dash, as in the file; if an editor mangles it, locate the spot by the line `m_captureHotkeys?.Dispose();` alone, which appears once, and insert the three new lines directly after it.)

- [ ] **Step 16: Build the app**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: `Build succeeded`, 0 errors. Then confirm the anchors are present exactly once each:

Run: `grep -c "// AI anchor: " App.Ai.cs`
Expected: `4`

- [ ] **Step 17: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (893 + 20 = 913).

```bash
git add Services/History/ProcessPaths.cs Services/History/TopProcessSource.cs Services/History/HistoryRecorder.cs Services/Diagnostics/AlertMonitor.cs App.Ai.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/HistoryTopProcessTests.cs tests/Kil0bitSystemMonitor.Tests/HistoryRecorderTests.cs tests/Kil0bitSystemMonitor.Tests/AiRecentAlertsTests.cs
git commit -F - <<'EOF'
feat(ai): record the 7-day history and add the App.Ai.cs wiring

HistoryRecorder feeds each telemetry tick into the minute aggregator
while Keep 7 days of history is on, and once a minute takes a short
lease on the shared process sampler for the top processes, with exe
paths read by pid and redacted. AlertMonitor keeps its last 50 raised
alerts for list_alerts. App.Ai.cs holds all AI wiring with anchors for
the tasks that follow; App.xaml.cs only calls StartAi and StopAi.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 6: Tool data contracts, TimeRange, ToolJson and the first data tools

The assistant and the MCP servers read MicaStats' data through one read-only tool catalogue. This task builds its foundation: the `IMicaData` seam (so the tools are tested against a hand-written fake), the shared tool names, the time parser for `now` / `-6h` / ISO times, the two shared JSON shapes, and `MicaTools` with `get_live_status`, `get_history`, `get_top_processes` and the name-based dispatcher the tool pipe and MCP call. Every result is a JSON object, redacted, with missing readings marked unavailable (never 0) and exceptions turned into `{"error": ...}`.

**Files:**
- Create: `Services/Ai/Tools/IMicaData.cs` (`SeriesStats`, `ProcessInfo`, `DataUnavailableException`, `IMicaData`)
- Create: `Services/Ai/Tools/ToolNames.cs`
- Create: `Services/Ai/Tools/TimeRange.cs`
- Create: `Services/Ai/Tools/ToolJson.cs`
- Create: `Services/Ai/Tools/MicaTools.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/AiFakeMicaData.cs` (test helper `FakeMicaData`)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiTimeRangeTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiToolsTests.cs`

**Interfaces:**
- Consumes:
  - Task 2: `public sealed class Redactor { public Redactor(string userProfile, string userName, string machineName); public JsonNode? RedactJson(JsonNode? node); }` (`Kil0bitSystemMonitor.Services.Ai.Tools`).
  - Task 3: `public sealed record HistoryRow { DateTime Utc; int Seconds; float? CpuAvg, CpuMax, CpuTempAvg, CpuTempMax, RamAvg, RamMax, GpuAvg, GpuMax, GpuTempMax, NetUpAvg, NetDownAvg, DiskFreeMinPercent, DiskActivityMax, BatteryPercent; bool? OnAc; string? TopCpuName, TopCpuPath; float? TopCpuPercent; string? TopRamName; float? TopRamMb; }` (init properties, `Kil0bitSystemMonitor.Services.History`).
  - Task 4: `public static HistoryRow HistoryCsv.Combine(IReadOnlyList<HistoryRow> rows, DateTime bucketUtc, int seconds)` (avg of avgs, max of maxes).
  - Existing: `SystemMetrics`, `DiskMetric` (`Kil0bitSystemMonitor.Models`), `SensorReading`, `SensorCategory` (`Services.Sensors`), `SavedReport`, `AlertRule`, `AlertEvent`, `BatteryReading`, `BatteryHealth`, `BootAnalysis` (`Services.Diagnostics`).
- Produces (namespace `Kil0bitSystemMonitor.Services.Ai.Tools`):
  - `public sealed record SeriesStats(float Min, float Avg, float Max, int Count);`
  - `public sealed record ProcessInfo(string Name, string? Path, int Pid, long CreateTime, float CpuPercent, double WorkingSetMb, double DiskKBps);`
  - `public sealed class DataUnavailableException : Exception { public DataUnavailableException(string message); }`
  - `public interface IMicaData` exactly as contract.md (Tasks 6-7).
  - `public static class ToolNames` (ten constants, `IReadOnlyList<string> ReadOnly`, the nine read-only names in order).
  - `public static class TimeRange { public static bool TryParse(string? text, DateTime nowUtc, out DateTime utc); }`
  - `public static class ToolJson { public static JsonObject Unavailable(string reason); public static JsonObject Error(string message); public static string ToText(JsonNode? node); internal static readonly JsonSerializerOptions TextOptions; }` — `Unavailable` is `{"unavailable":true,"reason":...}`, `Error` is `{"error":...}`, `ToText` is compact JSON with relaxed escaping, `"null"` for null.
  - `public sealed partial class MicaTools { public MicaTools(IMicaData data, Redactor redactor); public const int MaxHistoryPoints = 500; Task<JsonNode> GetLiveStatusAsync(CancellationToken ct = default); Task<JsonNode> GetHistoryAsync(string metric, string from, string to, int maxPoints = 200, CancellationToken ct = default); Task<JsonNode> GetTopProcessesAsync(string by = "cpu", int count = 10, CancellationToken ct = default); Task<JsonNode> InvokeAsync(string tool, JsonObject? args, CancellationToken ct = default); }` — `InvokeAsync` dispatches these three now (Task 7 adds the other six); an unknown name, including `suggest_action`, returns `{"error":"Unknown tool '<name>'. MicaStats offers: ..."}`. Only `OperationCanceledException` for the caller's token escapes a tool.
  - Test helper `internal sealed class FakeMicaData : IMicaData` (settable data, `Failure` makes every member throw, records `LatestCalls`, `LastTopRequest`, `LastHistoryRange`, `ReportReads`; `static SystemMetrics Sample()`).
  - JSON field names used by later tasks and the system prompt: live status `time`, `utcOffset`, `cpu{usagePercent,kernelPercent,cores,clockGhz,temperatureC}`, `memory`, `gpu`, `disks[]`, `network`, `battery`, `sensors`, `recent`; history `metric`, `from`, `to`, `utcOffset`, `rows`, `downsampled`, `points[{utc,seconds,...}]`, `note`; top processes `by`, `time`, `processes[{name,path,pid,createTime,startedUtc,startedLocal,cpuPercent,memoryMb,diskKBps}]`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiFakeMicaData.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A hand-written <see cref="IMicaData"/>: every member returns what the test put in and
    /// records how it was asked. Setting <see cref="Failure"/> makes every member throw it.
    /// </summary>
    internal sealed class FakeMicaData : IMicaData
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        public bool IsLive { get; set; } = true;

        public SystemMetrics? Metrics { get; set; } = Sample();

        public Dictionary<string, SeriesStats> Stats { get; } = new();

        public List<ProcessInfo> Processes { get; } = new();

        public List<HistoryRow> Rows { get; } = new();

        public List<SavedReport> Reports { get; } = new();

        public Dictionary<string, string> ReportTexts { get; } = new();

        public List<AlertRule> Rules { get; } = new();

        public List<AlertEvent> Alerts { get; } = new();

        public string? HardwareText { get; set; }

        public BatteryReading? BatteryReading { get; set; }

        public BatteryHealth? Health { get; set; }

        public BootAnalysis? Boot { get; set; }

        public Exception? Failure { get; set; }

        public int LatestCalls { get; private set; }

        public (string By, int Count)? LastTopRequest { get; private set; }

        public (DateTime From, DateTime To)? LastHistoryRange { get; private set; }

        public List<string> ReportReads { get; } = new();

        public SystemMetrics? Latest()
        {
            Check();
            LatestCalls++;
            return Metrics;
        }

        public IReadOnlyDictionary<string, SeriesStats> RecentStats()
        {
            Check();
            return Stats;
        }

        public Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct)
        {
            Check();
            LastTopRequest = (by, count);
            return Task.FromResult<IReadOnlyList<ProcessInfo>>(Processes);
        }

        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc)
        {
            Check();
            LastHistoryRange = (fromUtc, toUtc);
            return Rows;
        }

        public IReadOnlyList<SavedReport> SlowdownReports()
        {
            Check();
            return Reports;
        }

        public string? ReadSlowdownReport(string id)
        {
            Check();
            ReportReads.Add(id);
            return ReportTexts.TryGetValue(id, out string? text) ? text : null;
        }

        public IReadOnlyList<AlertRule> AlertRules()
        {
            Check();
            return Rules;
        }

        public IReadOnlyList<AlertEvent> RecentAlerts()
        {
            Check();
            return Alerts;
        }

        public Task<string?> HardwareReportAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(HardwareText);
        }

        public BatteryReading? Battery()
        {
            Check();
            return BatteryReading;
        }

        public Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(Health);
        }

        public Task<BootAnalysis?> BootAsync(CancellationToken ct)
        {
            Check();
            return Task.FromResult(Boot);
        }

        private void Check()
        {
            if (Failure != null) throw Failure;
        }

        /// <summary>
        /// A desktop with no temperature source, no GPU counter, a drive that is not ready and
        /// no battery, so every "unavailable" path has something to report.
        /// </summary>
        public static SystemMetrics Sample() => new()
        {
            CpuUsage = 12.34f,
            CpuSystem = 3.21f,
            CoreUsage = new[] { 10f, 20f, 30f, 40f },
            CpuFrequencyGhz = 3.456f,
            CpuTemperature = -1f,
            RamPercent = 61.26f,
            RamUsedBytes = 10UL * 1024 * 1024 * 1024,
            RamTotalBytes = 16UL * 1024 * 1024 * 1024,
            CommitPercent = 70f,
            CachedBytes = 2UL * 1024 * 1024 * 1024,
            GpuUsage = -1f,
            GpuTemperature = -1f,
            Disks = new List<DiskMetric>
            {
                new() { Name = "0 C:", FreeBytes = 100UL * 1024 * 1024 * 1024, TotalBytes = 400UL * 1024 * 1024 * 1024, ActivityPercent = 5f },
                new() { Name = "1 D:" },
            },
            NetUpKbps = 12.5f,
            NetDownKbps = 250f,
            NetAdapterName = "Wi-Fi",
            NetIpAddress = "192.168.1.20",
            BatteryPercent = -1,
        };
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiTimeRangeTests.cs`:

```csharp
using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiTimeRangeTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("now", "2026-09-30T05:00:00Z")]
        [InlineData(" NOW ", "2026-09-30T05:00:00Z")]
        [InlineData("-90s", "2026-09-30T04:58:30Z")]
        [InlineData("-30m", "2026-09-30T04:30:00Z")]
        [InlineData("-6h", "2026-09-29T23:00:00Z")]
        [InlineData("-2d", "2026-09-28T05:00:00Z")]
        [InlineData("-2D", "2026-09-28T05:00:00Z")]
        [InlineData("2026-09-29T10:15:00Z", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29T10:15:00", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29T17:15:00+07:00", "2026-09-29T10:15:00Z")]
        [InlineData("2026-09-29", "2026-09-29T00:00:00Z")]
        public void Reads_now_relative_and_iso_times_as_utc(string text, string expected)
        {
            Assert.True(TimeRange.TryParse(text, Now, out DateTime utc));

            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(expected, utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("yesterday")]
        [InlineData("-5x")]
        [InlineData("5m")]
        [InlineData("-9999999d")]
        [InlineData("30/09/2026")]
        [InlineData("-\u0E51\u0E52m")]
        public void Refuses_anything_else(string? text)
        {
            Assert.False(TimeRange.TryParse(text, Now, out _));
        }

        [Fact]
        public void A_thai_culture_does_not_shift_the_year()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");

                Assert.True(TimeRange.TryParse("2026-09-29T10:15:00Z", Now, out DateTime utc));

                Assert.Equal(2026, utc.Year);
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiToolsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
            Assert.Contains("temperature", temperature["reason"]!.GetValue<string>());
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

            Assert.Contains("counter broke", broken["error"]!.GetValue<string>());
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

        [Fact]
        public async Task History_refuses_unknown_metrics_and_unreadable_times()
        {
            MicaTools tools = Tools(new FakeMicaData());

            var metric = await tools.GetHistoryAsync("fan", "-1h", "now");
            var time = await tools.GetHistoryAsync("cpu", "yesterday", "now");
            var order = await tools.GetHistoryAsync("cpu", "now", "-1h");

            Assert.Contains("Use one of: cpu, cpuTemp", metric["error"]!.GetValue<string>());
            Assert.Contains("Could not read the time 'yesterday'", time["error"]!.GetValue<string>());
            Assert.Contains("is after", order["error"]!.GetValue<string>());
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
            Assert.Contains("Keep 7 days of history", result["note"]!.GetValue<string>());
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

            Assert.Contains("Use cpu, memory or disk", result["error"]!.GetValue<string>());
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

            Assert.StartsWith("Unknown tool 'suggest_action'", suggest["error"]!.GetValue<string>());
            Assert.StartsWith("Unknown tool 'delete_everything'", made_up["error"]!.GetValue<string>());
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
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiTimeRangeTests|FullyQualifiedName~AiToolsTests"`
Expected: build FAILS with CS0246 — `The type or namespace name 'IMicaData' could not be found` (and the same for `MicaTools`, `TimeRange`, `ToolJson`, `SeriesStats`, `ProcessInfo`).

- [ ] **Step 3: Add the data contracts**

Create `Services/Ai/Tools/IMicaData.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>Lowest, mean and highest value of one metric over the in-memory window (about two minutes).</summary>
    public sealed record SeriesStats(float Min, float Avg, float Max, int Count);

    /// <summary>
    /// One process as the tools report it. <see cref="CreateTime"/> is the kernel FILETIME that,
    /// with <see cref="Pid"/>, names this process rather than a pid slot Windows may reuse: a
    /// suggested End action carries both, so MicaStats can check the target again before acting.
    /// </summary>
    public sealed record ProcessInfo(string Name, string? Path, int Pid, long CreateTime,
                                     float CpuPercent, double WorkingSetMb, double DiskKBps);

    /// <summary>
    /// A data source cannot supply a reading at all, for example live data in the MCP bridge
    /// while MicaStats is not running. The tools hand its message to the model unchanged, so it
    /// must read as a plain sentence.
    /// </summary>
    public sealed class DataUnavailableException : Exception
    {
        /// <summary>Creates the exception with the sentence the model will see.</summary>
        public DataUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Everything the data tools read, behind one narrow seam: <c>LiveMicaData</c> in the running
    /// app, <c>OfflineMicaData</c> in the MCP bridge when MicaStats is not running, and a
    /// hand-written fake in tests. Members may throw; <see cref="MicaTools"/> turns every
    /// exception into an <c>{"error": ...}</c> result.
    /// </summary>
    public interface IMicaData
    {
        /// <summary>The current time, UTC. Relative times such as <c>-6h</c> are resolved against it.</summary>
        DateTime UtcNow { get; }

        /// <summary>True in the running app; false in the offline bridge, which has no live readings.</summary>
        bool IsLive { get; }

        /// <summary>The latest telemetry snapshot, or null before the first one has arrived.</summary>
        SystemMetrics? Latest();

        /// <summary>
        /// Min, average and max over the in-memory window, keyed <c>cpu</c>, <c>ram</c>,
        /// <c>gpu</c>, <c>temp</c>, <c>netUp</c> and <c>netDown</c>. A key is missing when that
        /// metric has no data, so a missing sensor never shows up as a row of zeros.
        /// </summary>
        IReadOnlyDictionary<string, SeriesStats> RecentStats();

        /// <summary>The busiest <paramref name="count"/> processes by <c>cpu</c>, <c>memory</c> or <c>disk</c>, busiest first.</summary>
        Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct);

        /// <summary>Recorded history rows between the two instants (inclusive), oldest first.</summary>
        IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc);

        /// <summary>Saved slowdown reports (<c>slowdown-*.txt</c> only), newest first.</summary>
        IReadOnlyList<SavedReport> SlowdownReports();

        /// <summary>The text of one slowdown report, or null when <paramref name="id"/> (the file name without extension) is unknown.</summary>
        string? ReadSlowdownReport(string id);

        /// <summary>The alert rules in force.</summary>
        IReadOnlyList<AlertRule> AlertRules();

        /// <summary>Alerts raised since MicaStats started, newest first.</summary>
        IReadOnlyList<AlertEvent> RecentAlerts();

        /// <summary>The hardware report text, or null when it could not be gathered.</summary>
        Task<string?> HardwareReportAsync(CancellationToken ct);

        /// <summary>A live battery sample, or null when the PC has no battery.</summary>
        BatteryReading? Battery();

        /// <summary>Battery wear figures, or null when there is no battery or Windows reports none.</summary>
        Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct);

        /// <summary>Boot history from the Windows event log, or null when it could not be read.</summary>
        Task<BootAnalysis?> BootAsync(CancellationToken ct);
    }
}
```

- [ ] **Step 4: Add the tool names**

Create `Services/Ai/Tools/ToolNames.cs`:

```csharp
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
```

- [ ] **Step 5: Add the time parser**

Create `Services/Ai/Tools/TimeRange.cs`:

```csharp
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Reads the times a model or MCP client passes to the tools: <c>now</c>, a relative time
    /// such as <c>-90s</c>, <c>-30m</c>, <c>-6h</c> or <c>-2d</c>, or an ISO-8601 time. A time
    /// without an offset is UTC, because every time the tools return is UTC.
    /// </summary>
    public static class TimeRange
    {
        // [0-9] rather than \d: \d also matches Thai and other Unicode digits.
        private static readonly Regex Relative = new("^-([0-9]{1,7})([smhd])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex IsoStart = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}", RegexOptions.CultureInvariant);

        /// <summary>Ten years: further back is a typo, and far enough back would overflow DateTime.</summary>
        private const double MaxBackSeconds = 3650d * 86400d;

        /// <summary>
        /// Parses <paramref name="text"/> against <paramref name="nowUtc"/>. Returns false for
        /// anything else, including dates written in a local calendar: the parse is invariant, so
        /// a Thai (Buddhist-era) system culture cannot shift a year by 543.
        /// </summary>
        public static bool TryParse(string? text, DateTime nowUtc, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            DateTime now = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

            if (string.Equals(t, "now", StringComparison.OrdinalIgnoreCase))
            {
                utc = now;
                return true;
            }

            Match m = Relative.Match(t);
            if (m.Success)
            {
                double amount = double.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                double seconds = char.ToLowerInvariant(m.Groups[2].Value[0]) switch
                {
                    's' => amount,
                    'm' => amount * 60d,
                    'h' => amount * 3600d,
                    _ => amount * 86400d,
                };
                if (seconds > MaxBackSeconds) return false;
                utc = now.AddSeconds(-seconds);
                return true;
            }

            if (!IsoStart.IsMatch(t)) return false;
            if (!DateTime.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                return false;
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
    }
}
```

- [ ] **Step 6: Add the shared JSON shapes**

Create `Services/Ai/Tools/ToolJson.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The two shapes every tool shares, so the model and MCP clients learn them once (a reading
    /// that could not be taken, a tool call that failed), and the one way tool JSON becomes text.
    /// </summary>
    public static class ToolJson
    {
        /// <summary>
        /// Relaxed escaping: the default encoder writes <c>&lt;user&gt;</c>, <c>+07:00</c> and Thai
        /// text as <c>\uXXXX</c> escapes, which a model reads badly and pays for in tokens. Safe
        /// here because tool text never goes into HTML.
        /// </summary>
        internal static readonly JsonSerializerOptions TextOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// <c>{"unavailable": true, "reason": "..."}</c>: a reading that was not measured. It is
        /// never written as 0, because a missing temperature probe must not read as a cold CPU.
        /// </summary>
        public static JsonObject Unavailable(string reason) => new()
        {
            ["unavailable"] = true,
            ["reason"] = reason,
        };

        /// <summary><c>{"error": "..."}</c>: the tool could not answer; the message says why.</summary>
        public static JsonObject Error(string message) => new()
        {
            ["error"] = message,
        };

        /// <summary>
        /// Compact JSON text for a model or an MCP client, with <see cref="TextOptions"/>
        /// escaping; <c>null</c> for a null node.
        /// </summary>
        public static string ToText(JsonNode? node) => node == null ? "null" : node.ToJsonString(TextOptions);
    }
}
```

- [ ] **Step 7: Add the first three tools and the dispatcher**

Create `Services/Ai/Tools/MicaTools.cs`. `FromLocal` is used by the report tools of Task 7; it is here with the other time helpers.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.History;
using Kil0bitSystemMonitor.Services.Sensors;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The read-only data tools, shared by the in-app assistant and the MCP servers.
    ///
    /// <para>
    /// Every tool returns a JSON object and never throws for a data problem: an exception
    /// becomes <c>{"error": ...}</c> (<see cref="ToolJson.Error"/>), a reading that was not
    /// measured becomes <see cref="ToolJson.Unavailable"/> rather than 0, numbers are rounded to
    /// one decimal, times are UTC ISO-8601 written with the invariant culture, and the whole
    /// result passes through <see cref="Redactor.RedactJson"/> before it leaves. Only
    /// cancellation escapes, so a Stop in the Ask window really stops.
    /// </para>
    /// </summary>
    public sealed partial class MicaTools
    {
        /// <summary>Most points <c>get_history</c> returns; longer ranges are averaged down to this.</summary>
        public const int MaxHistoryPoints = 500;

        private const int MaxTopProcesses = 15;

        private const string NoHistory =
            "No history in this range. MicaStats records history only while Keep 7 days of history is on (Settings > AI).";

        /// <summary>The metric names <c>get_history</c> accepts, in the order its description lists them.</summary>
        internal static IReadOnlyList<string> HistoryMetrics { get; } = new[]
        {
            "cpu", "cpuTemp", "ram", "gpu", "gpuTemp", "netUp", "netDown",
            "diskFree", "diskActivity", "battery", "topProcesses", "all",
        };

        private readonly IMicaData _data;
        private readonly Redactor _redactor;

        /// <summary>Creates the tools over one data source; every result is redacted with <paramref name="redactor"/>.</summary>
        public MicaTools(IMicaData data, Redactor redactor)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        }

        /// <summary>
        /// <c>get_live_status</c>: the latest snapshot (CPU, per-core summary, temperatures, memory,
        /// GPU, disks, network, battery, sensors) plus min/avg/max over the in-memory window.
        /// </summary>
        public Task<JsonNode> GetLiveStatusAsync(CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildLiveStatus()), ct);

        /// <summary>
        /// <c>get_history</c>: recorded rows of <paramref name="metric"/> between two times
        /// (<see cref="TimeRange"/> forms), averaged down to at most <paramref name="maxPoints"/>
        /// (clamped to 1..<see cref="MaxHistoryPoints"/>).
        /// </summary>
        public Task<JsonNode> GetHistoryAsync(string metric, string from, string to, int maxPoints = 200, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildHistory(metric, from, to, maxPoints)), ct);

        /// <summary>
        /// <c>get_top_processes</c>: the busiest processes by <c>cpu</c>, <c>memory</c> or
        /// <c>disk</c>; <paramref name="count"/> is clamped to 1..15.
        /// </summary>
        public Task<JsonNode> GetTopProcessesAsync(string by = "cpu", int count = 10, CancellationToken ct = default) =>
            RunAsync(() => BuildTopProcessesAsync(by, count, ct), ct);

        /// <summary>
        /// Runs a read-only tool by name with JSON arguments, for the tool pipe and MCP. An
        /// unknown name (including <c>suggest_action</c>, which is in-app only) is an error
        /// result, never an exception.
        /// </summary>
        public async Task<JsonNode> InvokeAsync(string tool, JsonObject? args, CancellationToken ct = default)
        {
            switch (tool)
            {
                case ToolNames.GetLiveStatus:
                    return await GetLiveStatusAsync(ct).ConfigureAwait(false);
                case ToolNames.GetHistory:
                    return await GetHistoryAsync(ArgText(args, "metric") ?? "", ArgText(args, "from") ?? "-1h",
                        ArgText(args, "to") ?? "now", ArgInt(args, "maxPoints", 200), ct).ConfigureAwait(false);
                case ToolNames.GetTopProcesses:
                    return await GetTopProcessesAsync(ArgText(args, "by") ?? "cpu", ArgInt(args, "count", 10), ct).ConfigureAwait(false);
                default:
                    return _redactor.RedactJson(ToolJson.Error("Unknown tool '" + tool + "'. MicaStats offers: " +
                        string.Join(", ", ToolNames.ReadOnly) + "."))!;
            }
        }

        // ----- Shared plumbing ---------------------------------------------------------------

        /// <summary>Runs one tool body: exceptions become error results, and the result is redacted.</summary>
        private async Task<JsonNode> RunAsync(Func<Task<JsonNode>> body, CancellationToken ct)
        {
            JsonNode result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DataUnavailableException ex)
            {
                result = ToolJson.Error(ex.Message);
            }
            catch (Exception ex)
            {
                result = ToolJson.Error("MicaStats could not read this: " + ex.Message);
            }
            return _redactor.RedactJson(result) ?? ToolJson.Error("The tool returned nothing.");
        }

        /// <summary>A string argument; numbers and objects come back as their JSON text.</summary>
        private static string? ArgText(JsonObject? args, string name)
        {
            if (args == null || !args.TryGetPropertyValue(name, out JsonNode? node) || node == null) return null;
            if (node is JsonValue value && value.TryGetValue(out string? text)) return text;
            return node.ToJsonString();
        }

        /// <summary>An integer argument, also accepted as a whole-number double or a numeric string.</summary>
        private static int ArgInt(JsonObject? args, string name, int fallback)
        {
            if (args == null || !args.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value) return fallback;
            if (value.TryGetValue(out int i)) return i;
            if (value.TryGetValue(out double d) && double.IsFinite(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            if (value.TryGetValue(out string? s) &&
                int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
            return fallback;
        }

        /// <summary>A number rounded to one decimal.</summary>
        private static JsonNode Num(double value) =>
            JsonValue.Create(Math.Round(value, 1, MidpointRounding.AwayFromZero));

        /// <summary>A number when it was measured, otherwise <see cref="ToolJson.Unavailable"/> with the reason.</summary>
        private static JsonNode Reading(bool measured, double value, string reason) =>
            measured && double.IsFinite(value) ? Num(value) : ToolJson.Unavailable(reason);

        /// <summary>Adds a history value only when it was measured: a missing field means unavailable, never 0.</summary>
        private static void Put(JsonObject into, string name, float? value)
        {
            if (value is float v && float.IsFinite(v)) into[name] = Num(v);
        }

        private static double Gb(ulong bytes) => bytes / 1073741824d;

        /// <summary>UTC as <c>yyyy-MM-ddTHH:mm:ssZ</c>, invariant (a Thai culture would write year 2569).</summary>
        internal static string Iso(DateTime utc) =>
            AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>The same instant in this PC's local time, for answers the user reads.</summary>
        internal static string LocalText(DateTime utc) =>
            AsUtc(utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>This PC's UTC offset at <paramref name="utc"/>, e.g. <c>+07:00</c>, so the model can convert times.</summary>
        internal static string UtcOffset(DateTime utc)
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(AsUtc(utc));
            return (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>Treats an unspecified kind as UTC: history rows and the data clock are UTC.</summary>
        private static DateTime AsUtc(DateTime t) => t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };

        /// <summary>Treats an unspecified kind as local: file times, alert times and event-log times are local.</summary>
        private static DateTime FromLocal(DateTime t) =>
            t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime();

        // ----- get_live_status ---------------------------------------------------------------

        private JsonObject BuildLiveStatus()
        {
            SystemMetrics? m = _data.Latest();
            if (m == null) return ToolJson.Unavailable("No measurement has arrived yet. Try again in a few seconds.");

            const string Unknown = "Windows did not report it.";
            DateTime now = _data.UtcNow;

            var cpu = new JsonObject
            {
                ["usagePercent"] = Num(m.CpuUsage),
                ["kernelPercent"] = Reading(m.CpuSystem >= 0, m.CpuSystem, "The kernel-time counter could not be read."),
                ["cores"] = CoreSummary(m.CoreUsage),
                ["clockGhz"] = Reading(m.CpuFrequencyGhz > 0, m.CpuFrequencyGhz, Unknown),
                ["temperatureC"] = Reading(m.CpuTemperature > 0, m.CpuTemperature,
                    "No CPU temperature source is running (Core Temp, HWiNFO or a similar tool publishes it)."),
            };

            var memory = new JsonObject
            {
                ["usedPercent"] = Num(m.RamPercent),
                ["usedGb"] = Reading(m.RamUsedBytes > 0, Gb(m.RamUsedBytes), Unknown),
                ["totalGb"] = Reading(m.RamTotalBytes > 0, Gb(m.RamTotalBytes), Unknown),
                ["commitPercent"] = Reading(m.CommitPercent > 0, m.CommitPercent, Unknown),
                ["cachedGb"] = Reading(m.CachedBytes > 0, Gb(m.CachedBytes), Unknown),
            };

            var gpu = new JsonObject
            {
                ["usagePercent"] = Reading(m.GpuUsage >= 0, m.GpuUsage, "No GPU usage counter was found."),
                ["temperatureC"] = Reading(m.GpuTemperature > 0, m.GpuTemperature, "No GPU temperature source was found."),
                ["memoryUsedGb"] = Reading(m.GpuVramUsedBytes > 0, Gb(m.GpuVramUsedBytes), Unknown),
            };

            var disks = new JsonArray();
            foreach (DiskMetric d in m.Disks)
            {
                bool ready = d.TotalBytes > 0;
                const string NotReady = "The drive was not ready.";
                disks.Add(new JsonObject
                {
                    ["name"] = d.Name,
                    ["freePercent"] = Reading(ready, ready ? d.FreeBytes * 100d / d.TotalBytes : 0, NotReady),
                    ["freeGb"] = Reading(ready, Gb(d.FreeBytes), NotReady),
                    ["totalGb"] = Reading(ready, Gb(d.TotalBytes), NotReady),
                    ["activityPercent"] = Num(d.ActivityPercent),
                });
            }

            // The adapter's IP address is deliberately left out: addresses never leave the PC.
            var network = new JsonObject
            {
                ["upKbps"] = Num(m.NetUpKbps),
                ["downKbps"] = Num(m.NetDownKbps),
            };
            if (!string.IsNullOrWhiteSpace(m.NetAdapterName)) network["adapter"] = m.NetAdapterName;

            JsonNode battery = !m.HasBattery
                ? ToolJson.Unavailable("This PC has no battery.")
                : new JsonObject
                {
                    ["percent"] = m.BatteryPercent,
                    ["onAc"] = m.BatteryOnAc,
                    ["charging"] = m.BatteryCharging,
                    ["watts"] = Reading(m.BatteryWatts > 0, m.BatteryWatts, "Idle or not reported."),
                    ["minutesLeft"] = Reading(m.BatteryMinutesLeft >= 0, m.BatteryMinutesLeft, "Cannot be estimated right now."),
                    ["healthPercent"] = Reading(m.BatteryHealthPercent >= 0, m.BatteryHealthPercent, "Not read yet."),
                };

            JsonNode sensors;
            if (m.Sensors.Count == 0)
            {
                sensors = ToolJson.Unavailable("No sensor source is available on this PC.");
            }
            else
            {
                var list = new JsonArray();
                foreach (SensorReading s in m.Sensors)
                {
                    list.Add(new JsonObject
                    {
                        ["label"] = s.Label,
                        ["kind"] = s.Category.ToString(),
                        ["value"] = Num(s.Value),
                        ["unit"] = s.Unit,
                        ["source"] = s.Source,
                    });
                }
                sensors = list;
            }

            var recent = new JsonObject();
            foreach (KeyValuePair<string, SeriesStats> pair in _data.RecentStats())
            {
                recent[pair.Key] = new JsonObject
                {
                    ["min"] = Num(pair.Value.Min),
                    ["avg"] = Num(pair.Value.Avg),
                    ["max"] = Num(pair.Value.Max),
                    ["samples"] = pair.Value.Count,
                };
            }

            return new JsonObject
            {
                ["time"] = Iso(now),
                ["utcOffset"] = UtcOffset(now),
                ["cpu"] = cpu,
                ["memory"] = memory,
                ["gpu"] = gpu,
                ["disks"] = disks,
                ["network"] = network,
                ["battery"] = battery,
                ["sensors"] = sensors,
                ["recent"] = recent,
            };
        }

        /// <summary>Count, min, average and max of the per-core loads, and the busiest core's index.</summary>
        private static JsonNode CoreSummary(float[] cores)
        {
            if (cores.Length == 0) return ToolJson.Unavailable("Per-core counters are unavailable.");
            int busiest = 0;
            for (int i = 1; i < cores.Length; i++) if (cores[i] > cores[busiest]) busiest = i;
            return new JsonObject
            {
                ["count"] = cores.Length,
                ["min"] = Num(cores.Min()),
                ["avg"] = Num(cores.Average()),
                ["max"] = Num(cores[busiest]),
                ["busiestIndex"] = busiest,
            };
        }

        // ----- get_history -------------------------------------------------------------------

        private JsonObject BuildHistory(string metric, string from, string to, int maxPoints)
        {
            string wanted = (metric ?? "").Trim();
            string? name = HistoryMetrics.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (name == null)
                return ToolJson.Error("Unknown metric '" + metric + "'. Use one of: " + string.Join(", ", HistoryMetrics) + ".");

            DateTime now = _data.UtcNow;
            if (!TimeRange.TryParse(from, now, out DateTime fromUtc)) return BadTime(from);
            if (!TimeRange.TryParse(to, now, out DateTime toUtc)) return BadTime(to);
            if (fromUtc > toUtc) return ToolJson.Error("from (" + Iso(fromUtc) + ") is after to (" + Iso(toUtc) + ").");

            IReadOnlyList<HistoryRow> rows = _data.History(fromUtc, toUtc);
            IReadOnlyList<HistoryRow> points = Downsample(rows, Math.Clamp(maxPoints, 1, MaxHistoryPoints));

            var array = new JsonArray();
            foreach (HistoryRow row in points)
            {
                var point = new JsonObject { ["utc"] = Iso(row.Utc), ["seconds"] = row.Seconds };
                AddMetric(point, name, row);
                array.Add(point);
            }

            var result = new JsonObject
            {
                ["metric"] = name,
                ["from"] = Iso(fromUtc),
                ["to"] = Iso(toUtc),
                ["utcOffset"] = UtcOffset(now),
                ["rows"] = rows.Count,
                ["downsampled"] = points.Count < rows.Count,
                ["points"] = array,
            };
            if (rows.Count == 0) result["note"] = NoHistory;
            return result;
        }

        private static JsonObject BadTime(string? text) => ToolJson.Error("Could not read the time '" + text +
            "'. Use now, -90s, -30m, -6h, -2d or an ISO-8601 UTC time such as 2026-09-30T08:00:00Z.");

        /// <summary>
        /// Averages consecutive rows into at most <paramref name="maxPoints"/> buckets with the
        /// same rules the store uses when it thins old days (<see cref="HistoryCsv.Combine"/>).
        /// </summary>
        internal static IReadOnlyList<HistoryRow> Downsample(IReadOnlyList<HistoryRow> rows, int maxPoints)
        {
            if (rows.Count <= maxPoints) return rows;
            var result = new List<HistoryRow>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
            {
                int start = (int)((long)i * rows.Count / maxPoints);
                int end = (int)((long)(i + 1) * rows.Count / maxPoints);
                var chunk = new List<HistoryRow>(end - start);
                int seconds = 0;
                for (int j = start; j < end; j++)
                {
                    chunk.Add(rows[j]);
                    seconds += rows[j].Seconds;
                }
                result.Add(HistoryCsv.Combine(chunk, chunk[0].Utc, seconds));
            }
            return result;
        }

        private static void AddMetric(JsonObject point, string metric, HistoryRow r)
        {
            switch (metric)
            {
                case "cpu": Put(point, "avg", r.CpuAvg); Put(point, "max", r.CpuMax); break;
                case "cpuTemp": Put(point, "avgC", r.CpuTempAvg); Put(point, "maxC", r.CpuTempMax); break;
                case "ram": Put(point, "avg", r.RamAvg); Put(point, "max", r.RamMax); break;
                case "gpu": Put(point, "avg", r.GpuAvg); Put(point, "max", r.GpuMax); break;
                case "gpuTemp": Put(point, "maxC", r.GpuTempMax); break;
                case "netUp": Put(point, "avgKbps", r.NetUpAvg); break;
                case "netDown": Put(point, "avgKbps", r.NetDownAvg); break;
                case "diskFree": Put(point, "minFreePercent", r.DiskFreeMinPercent); break;
                case "diskActivity": Put(point, "maxActivityPercent", r.DiskActivityMax); break;
                case "battery":
                    Put(point, "percent", r.BatteryPercent);
                    if (r.OnAc is bool onAc) point["onAc"] = onAc;
                    break;
                case "topProcesses":
                    AddTop(point, r);
                    break;
                default: // all
                    Put(point, "cpuAvg", r.CpuAvg); Put(point, "cpuMax", r.CpuMax);
                    Put(point, "cpuTempAvgC", r.CpuTempAvg); Put(point, "cpuTempMaxC", r.CpuTempMax);
                    Put(point, "ramAvg", r.RamAvg); Put(point, "ramMax", r.RamMax);
                    Put(point, "gpuAvg", r.GpuAvg); Put(point, "gpuMax", r.GpuMax); Put(point, "gpuTempMaxC", r.GpuTempMax);
                    Put(point, "netUpKbps", r.NetUpAvg); Put(point, "netDownKbps", r.NetDownAvg);
                    Put(point, "diskMinFreePercent", r.DiskFreeMinPercent); Put(point, "diskMaxActivityPercent", r.DiskActivityMax);
                    Put(point, "batteryPercent", r.BatteryPercent);
                    if (r.OnAc is bool ac) point["onAc"] = ac;
                    AddTop(point, r);
                    break;
            }
        }

        private static void AddTop(JsonObject point, HistoryRow r)
        {
            if (!string.IsNullOrEmpty(r.TopCpuName))
            {
                var top = new JsonObject { ["name"] = r.TopCpuName };
                if (!string.IsNullOrEmpty(r.TopCpuPath)) top["path"] = r.TopCpuPath;
                Put(top, "cpuPercent", r.TopCpuPercent);
                point["topCpu"] = top;
            }
            if (!string.IsNullOrEmpty(r.TopRamName))
            {
                var top = new JsonObject { ["name"] = r.TopRamName };
                Put(top, "memoryMb", r.TopRamMb);
                point["topMemory"] = top;
            }
        }

        // ----- get_top_processes -------------------------------------------------------------

        private async Task<JsonNode> BuildTopProcessesAsync(string by, int count, CancellationToken ct)
        {
            string? key = (by ?? "").Trim().ToLowerInvariant() switch
            {
                "cpu" => "cpu",
                "memory" or "ram" or "mem" => "memory",
                "disk" or "io" => "disk",
                _ => null,
            };
            if (key == null) return ToolJson.Error("Unknown ranking '" + by + "'. Use cpu, memory or disk.");

            int n = Math.Clamp(count, 1, MaxTopProcesses);
            IReadOnlyList<ProcessInfo> list = await _data.TopProcessesAsync(key, n, ct).ConfigureAwait(false);

            var array = new JsonArray();
            foreach (ProcessInfo p in list.Take(n))
            {
                var item = new JsonObject { ["name"] = p.Name };
                if (!string.IsNullOrEmpty(p.Path)) item["path"] = p.Path;
                item["pid"] = p.Pid;
                item["createTime"] = p.CreateTime;
                if (TryFileTime(p.CreateTime, out DateTime started))
                {
                    item["startedUtc"] = Iso(started);
                    item["startedLocal"] = LocalText(started);
                }
                item["cpuPercent"] = Num(p.CpuPercent);
                item["memoryMb"] = Num(p.WorkingSetMb);
                item["diskKBps"] = Num(p.DiskKBps);
                array.Add(item);
            }

            return new JsonObject
            {
                ["by"] = key,
                ["time"] = Iso(_data.UtcNow),
                ["processes"] = array,
            };
        }

        private static bool TryFileTime(long fileTime, out DateTime utc)
        {
            utc = default;
            if (fileTime <= 0) return false;
            try
            {
                utc = DateTime.FromFileTimeUtc(fileTime);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiTimeRangeTests|FullyQualifiedName~AiToolsTests"`
Expected: 42 passed, 0 failed. If a redaction assertion fails, check `Redactor` (Task 2) first: timestamps such as `2026-09-30T04:00:00Z` must survive redaction untouched, and `DESK-7` must become `[computer]`.

- [ ] **Step 9: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (913 + 42 = 955).

```bash
git add Services/Ai/Tools/IMicaData.cs Services/Ai/Tools/ToolNames.cs Services/Ai/Tools/TimeRange.cs Services/Ai/Tools/ToolJson.cs Services/Ai/Tools/MicaTools.cs tests/Kil0bitSystemMonitor.Tests/AiFakeMicaData.cs tests/Kil0bitSystemMonitor.Tests/AiTimeRangeTests.cs tests/Kil0bitSystemMonitor.Tests/AiToolsTests.cs
git commit -F - <<'EOF'
feat(ai): read-only data tools for live status, history and top processes

The assistant and the MCP servers share one tool catalogue behind the
IMicaData seam. get_live_status, get_history and get_top_processes return
redacted JSON objects: missing readings are marked unavailable, never 0,
failures become an error object, numbers are rounded and times are UTC
written with the invariant culture. InvokeAsync dispatches by tool name
for the tool pipe and MCP.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 7: The report tools, live and offline data, and the App wiring

This task completes the catalogue with `list_slowdown_reports`, `get_slowdown_report`, `list_alerts`, `get_hardware`, `get_battery` and `get_boot_summary`, and adds the two real data sources: `LiveMicaData` for the running app (UI-thread reads of `MetricsHistory`, a short sampler lease per process ranking that is always released, cached hardware and boot data) and `OfflineMicaData` for the MCP bridge when MicaStats is not running (history files and slowdown reports only; every live tool answers "MicaStats is not running"). `App.Ai.cs` then builds `App.AiTools` at startup.

**Files:**
- Create: `Services/Ai/Tools/SlowdownReportFiles.cs`
- Create: `Services/Ai/Tools/MicaTools.Reports.cs`
- Modify: `Services/Ai/Tools/MicaTools.cs` (`InvokeAsync` dispatches the six new tools)
- Create: `Services/Ai/Tools/OfflineMicaData.cs`
- Create: `Services/Ai/Tools/LiveMicaData.cs`
- Modify: `App.Ai.cs` (anchors `members` and `start`: `AiTools`)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiToolsReportTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiMicaDataTests.cs`

**Interfaces:**
- Consumes:
  - Task 6: `IMicaData`, `MicaTools` (private helpers `RunAsync`, `Num`, `Reading`, `Iso`, `LocalText`, `FromLocal`, `BadTime`, `ArgText`, `ArgInt` in the same partial class), `ToolJson`, `TimeRange`, `ToolNames`, `ProcessInfo`, `SeriesStats`, `DataUnavailableException`, test helper `FakeMicaData`.
  - Task 4: `public sealed class HistoryStore { public HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null); public void Append(HistoryRow row); public IReadOnlyList<HistoryRow> Read(DateTime fromUtc, DateTime toUtc); }`.
  - Task 5: `public static string? ProcessPaths.TryGetPath(int pid)` (`Services.History`); `AlertMonitor.Recent` (`IReadOnlyList<AlertEvent>`, newest first); in `App.Ai.cs`: `public static HistoryStore History { get; }`, `internal static AlertMonitor? AlertMonitorForAi`, `StartAi(ConfigService config, TelemetryService telemetry, MetricsHistory history, Dispatcher ui)` with the anchor lines `    // AI anchor: members` and `        // AI anchor: start`.
  - Task 1: test helper `AiTestEnv` (`Root`, `PathOf(string)`, `Clock.UtcNow` get/set, `Dispose()`).
  - Existing: `MetricsHistory` (`Cpu`, `Ram`, `Gpu`, `Temp`, `NetUp`, `NetDown`, `Latest`, `Append`), `Series` (`Count`, indexer), `ProcessSampler` (`Retain`, `Release`, `HasCpuData`, `AllProcesses`, `Enabled`), `ProcessUsage`, `BatteryMonitor.ReadCached(int)`, `BatteryMonitor.GetHealthAsync(bool)`, `BatteryEstimate`, `HardwareInfoService.Gather()`, `HardwareInfoService.AppVersion`, `HardwareReportWriter.Write(HardwareSnapshot, string)`, `BootAnalyzer.Gather()`, `SlowdownRecorder.ReportDir`, `AlertMonitor(MetricsHistory, BatteryMonitor?)`, `AlertMonitor.Rules`, App's `SharedProcessSampler` and `Battery`, test helper `UiThread.Run(Action)`.
- Produces:
  - `MicaTools`: `Task<JsonNode> ListSlowdownReportsAsync(int limit = 10, CancellationToken ct = default)` (clamped 1..30; each item `id`, `utc`, `local`, `trigger`, `summary`), `GetSlowdownReportAsync(string id, CancellationToken ct = default)` (`id`, `truncated`, `text`), `ListAlertsAsync(string? since = null, CancellationToken ct = default)` (`rules[]`, `raised[]`, `note`, `since`), `GetHardwareAsync` (`report`), `GetBatteryAsync`, `GetBootSummaryAsync` (all `CancellationToken ct = default`); `InvokeAsync` now dispatches all nine read-only tools.
  - `public sealed class LiveMicaData : IMicaData { public LiveMicaData(MetricsHistory history, Dispatcher ui, ProcessSampler sampler, HistoryStore store, Func<AlertMonitor?> alerts, Func<BatteryMonitor?> battery, Func<DateTime> utcClock); }`
  - `public sealed class OfflineMicaData : IMicaData { public OfflineMicaData(HistoryStore store, string reportDir, Func<DateTime> utcClock); internal const string NotRunningMessage = "MicaStats is not running"; }`
  - `App.Ai.cs`: `public static Services.Ai.Tools.MicaTools? AiTools { get; private set; }` — built in `StartAi` from `LiveMicaData` and `Redactor.ForCurrentUser()`; null if that failed (logged).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiToolsReportTests.cs`:

```csharp
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
```

Create `tests/Kil0bitSystemMonitor.Tests/AiMicaDataTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiMicaDataTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

        private static HistoryStore Store(AiTestEnv env)
        {
            env.Clock.UtcNow = Now;
            return new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow, _ => { });
        }

        private static Dispatcher UiDispatcher()
        {
            Dispatcher? ui = null;
            UiThread.Run(() => ui = Dispatcher.CurrentDispatcher);
            return ui!;
        }

        private static string WriteReport(string folder, string name, DateTime lastWriteUtc, string text = "report")
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        // ----- offline -----------------------------------------------------------------------

        [Fact]
        public void Offline_serves_history_and_only_slowdown_reports_from_disk()
        {
            using var env = new AiTestEnv();
            HistoryStore store = Store(env);
            store.Append(new HistoryRow { Utc = Now.AddMinutes(-1), Seconds = 60, CpuAvg = 12f });
            string reports = env.PathOf("reports");
            Directory.CreateDirectory(reports);
            WriteReport(reports, "slowdown-20260930-110000.txt", Now.AddHours(-1), "newer");
            WriteReport(reports, "slowdown-20260929-090000.txt", Now.AddDays(-1), "older");
            WriteReport(reports, "hardware-report-20260930-100000.txt", Now);
            WriteReport(reports, "slowdown-notes.txt", Now);
            var offline = new OfflineMicaData(store, reports, () => env.Clock.UtcNow);

            Assert.False(offline.IsLive);
            Assert.Single(offline.History(Now.AddHours(-1), Now));
            Assert.Equal(new[] { "slowdown-20260930-110000.txt", "slowdown-20260929-090000.txt" },
                offline.SlowdownReports().Select(r => r.Name));
            Assert.Equal("older", offline.ReadSlowdownReport("slowdown-20260929-090000"));
            Assert.Null(offline.ReadSlowdownReport(@"..\reports\hardware-report-20260930-100000"));
            Assert.Null(offline.ReadSlowdownReport("slowdown-20200101-000000"));
        }

        [Fact]
        public async Task Offline_live_tools_say_MicaStats_is_not_running()
        {
            using var env = new AiTestEnv();
            var offline = new OfflineMicaData(Store(env), env.PathOf("reports"), () => env.Clock.UtcNow);
            var tools = new MicaTools(offline, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

            var ex = Assert.Throws<DataUnavailableException>(() => offline.Latest());
            var live = await tools.GetLiveStatusAsync();
            var top = await tools.GetTopProcessesAsync();
            var history = await tools.GetHistoryAsync("cpu", "-1h", "now");

            Assert.Equal("MicaStats is not running", ex.Message);
            Assert.Equal("MicaStats is not running", live["error"]!.GetValue<string>());
            Assert.Equal("MicaStats is not running", top["error"]!.GetValue<string>());
            Assert.NotNull(history["points"]);
        }

        // ----- live --------------------------------------------------------------------------

        [Fact]
        public void Live_latest_is_null_before_the_first_sample_and_read_on_the_ui_thread()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            var live = new LiveMicaData(history, UiDispatcher(), sampler, Store(env), () => null, () => null, () => env.Clock.UtcNow);

            Assert.Null(live.Latest());

            UiThread.Run(() => history.Append(new SystemMetrics { CpuUsage = 20f }));

            Assert.Equal(20f, live.Latest()!.CpuUsage);
            Assert.True(live.IsLive);
            Assert.Equal(Now, live.UtcNow);
        }

        [Fact]
        public void Live_recent_stats_skip_series_that_have_no_readings()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            var live = new LiveMicaData(history, UiDispatcher(), sampler, Store(env), () => null, () => null, () => env.Clock.UtcNow);
            UiThread.Run(() =>
            {
                foreach (float cpu in new[] { 10f, 20f, 30f })
                    history.Append(new SystemMetrics { CpuUsage = cpu, RamPercent = 50f, GpuUsage = -1f, CpuTemperature = -1f, GpuTemperature = -1f });
            });

            IReadOnlyDictionary<string, SeriesStats> stats = live.RecentStats();

            Assert.Equal(new SeriesStats(10f, 20f, 30f, 3), stats["cpu"]);
            Assert.True(stats.ContainsKey("ram"));
            Assert.False(stats.ContainsKey("gpu"));
            Assert.False(stats.ContainsKey("temp"));
        }

        [Fact]
        public async Task Live_top_processes_rank_by_memory_and_release_the_sampler()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var live = new LiveMicaData(new MetricsHistory(capacity: 8), UiDispatcher(), sampler, Store(env),
                () => null, () => null, () => env.Clock.UtcNow);

            IReadOnlyList<ProcessInfo> top = await live.TopProcessesAsync("memory", 3, CancellationToken.None);

            Assert.Equal(3, top.Count);
            Assert.All(top, p => Assert.True(p.Pid > 0 && p.CreateTime > 0));
            Assert.True(top[0].WorkingSetMb >= top[1].WorkingSetMb && top[1].WorkingSetMb >= top[2].WorkingSetMb);
            Assert.False(sampler.Enabled);
        }

        [Fact]
        public async Task Live_top_processes_release_the_sampler_when_cancelled()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var live = new LiveMicaData(new MetricsHistory(capacity: 8), UiDispatcher(), sampler, Store(env),
                () => null, () => null, () => env.Clock.UtcNow);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.TopProcessesAsync("cpu", 5, cts.Token));

            Assert.False(sampler.Enabled);
        }

        [Fact]
        public void Live_alerts_and_history_come_from_the_app_services()
        {
            using var env = new AiTestEnv();
            using var sampler = new ProcessSampler();
            var history = new MetricsHistory(capacity: 8);
            HistoryStore store = Store(env);
            store.Append(new HistoryRow { Utc = Now.AddMinutes(-2), Seconds = 60, RamAvg = 40f });
            AlertMonitor? monitor = null;
            var live = new LiveMicaData(history, UiDispatcher(), sampler, store, () => monitor, () => null, () => env.Clock.UtcNow);

            Assert.Empty(live.AlertRules());
            Assert.Empty(live.RecentAlerts());

            monitor = new AlertMonitor(history, null);

            Assert.Equal(AlertRule.Defaults.Count, live.AlertRules().Count);
            Assert.Empty(live.RecentAlerts());
            Assert.Equal(40f, live.History(Now.AddHours(-1), Now).Single().RamAvg);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiToolsReportTests|FullyQualifiedName~AiMicaDataTests"`
Expected: build FAILS — CS1061 `'MicaTools' does not contain a definition for 'ListSlowdownReportsAsync'` (and `Summarize`, `GetSlowdownReportAsync`, `ListAlertsAsync`, ...), CS0246 `The type or namespace name 'OfflineMicaData' could not be found` and the same for `LiveMicaData`.

- [ ] **Step 3: Add the slowdown report files helper**

Create `Services/Ai/Tools/SlowdownReportFiles.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Lists and reads slowdown reports in a report folder, for both the live and the offline
    /// data source. Only <c>slowdown-yyyyMMdd-HHmmss.txt</c> files count: the hardware and
    /// diagnostics reports in the same folder are not slowdowns, and an id is checked against
    /// that exact shape before it becomes a path, so an id can never walk out of the folder.
    /// </summary>
    internal static class SlowdownReportFiles
    {
        // [0-9] rather than \d: \d also matches Thai and other Unicode digits.
        private static readonly Regex IdPattern = new("^slowdown-[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant);

        /// <summary>True for an id the recorder could have written, e.g. <c>slowdown-20260930-140200</c>.</summary>
        public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

        /// <summary>The slowdown reports in <paramref name="folder"/>, newest first; empty when it is missing or unreadable.</summary>
        public static IReadOnlyList<SavedReport> List(string folder)
        {
            var result = new List<SavedReport>();
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) return result;
                foreach (FileInfo file in dir.GetFiles("slowdown-*.txt"))
                {
                    if (!IsValidId(Path.GetFileNameWithoutExtension(file.Name))) continue;
                    result.Add(new SavedReport(file.FullName, file.Name, file.LastWriteTime, file.Length));
                }
                result.Sort((a, b) => b.At.CompareTo(a.At));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        /// <summary>The text of report <paramref name="id"/>, or null when the id is malformed, missing or unreadable.</summary>
        public static string? Read(string folder, string id)
        {
            if (!IsValidId(id)) return null;
            string path = Path.Combine(folder, id + ".txt");
            try
            {
                if (!File.Exists(path)) return null;
                // Shared read: the recorder may be writing or pruning while the bridge reads.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
```

- [ ] **Step 4: Add the report, alert, hardware, battery and boot tools**

Create `Services/Ai/Tools/MicaTools.Reports.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    // The tools over saved and slow-to-gather data: slowdown reports, alerts, hardware,
    // battery and boot. Same rules as MicaTools.cs: JSON objects only, errors as results.
    public sealed partial class MicaTools
    {
        private const int MaxReports = 30;

        /// <summary>
        /// Longest report text returned. A 15-minute window is 900 timeline rows; the head (the
        /// header) and the tail (the moment the threshold was crossed, and the offenders) matter most.
        /// </summary>
        private const int MaxReportChars = 40_000;

        private const int ReportHeadChars = 4_000;
        private const int MaxBoots = 10;
        private const int MaxBootDelays = 10;
        private const int MaxStartupPrograms = 40;

        /// <summary><c>list_slowdown_reports</c>: id, time, trigger and a one-line summary, newest first; <paramref name="limit"/> is clamped to 1..30.</summary>
        public Task<JsonNode> ListSlowdownReportsAsync(int limit = 10, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildReportList(limit)), ct);

        /// <summary><c>get_slowdown_report</c>: the report text, redacted; the middle of a very long timeline is left out.</summary>
        public Task<JsonNode> GetSlowdownReportAsync(string id, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildReport(id)), ct);

        /// <summary><c>list_alerts</c>: the rules in force and the alerts raised since MicaStats started, optionally only those after <paramref name="since"/>.</summary>
        public Task<JsonNode> ListAlertsAsync(string? since = null, CancellationToken ct = default) =>
            RunAsync(() => Task.FromResult<JsonNode>(BuildAlerts(since)), ct);

        /// <summary><c>get_hardware</c>: CPU, board, memory, graphics, storage and Windows details as the hardware report text.</summary>
        public Task<JsonNode> GetHardwareAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildHardwareAsync(ct), ct);

        /// <summary><c>get_battery</c>: charge, power, time left, health, wear, capacities and cycle count.</summary>
        public Task<JsonNode> GetBatteryAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildBatteryAsync(ct), ct);

        /// <summary><c>get_boot_summary</c>: recent boot durations, the trend, what slowed the last boot, and the startup programs.</summary>
        public Task<JsonNode> GetBootSummaryAsync(CancellationToken ct = default) =>
            RunAsync(() => BuildBootAsync(ct), ct);

        // ----- slowdown reports --------------------------------------------------------------

        private JsonObject BuildReportList(int limit)
        {
            IReadOnlyList<SavedReport> reports = _data.SlowdownReports();
            var array = new JsonArray();
            foreach (SavedReport report in reports.Take(Math.Clamp(limit, 1, MaxReports)))
            {
                string id = Path.GetFileNameWithoutExtension(report.Name);
                DateTime utc = FromLocal(report.At);
                (string? trigger, string summary) = Summarize(_data.ReadSlowdownReport(id));
                var item = new JsonObject { ["id"] = id, ["utc"] = Iso(utc), ["local"] = LocalText(utc) };
                if (trigger != null) item["trigger"] = trigger;
                item["summary"] = summary;
                array.Add(item);
            }
            return new JsonObject { ["total"] = reports.Count, ["reports"] = array };
        }

        private JsonObject BuildReport(string id)
        {
            string key = (id ?? "").Trim();
            if (!SlowdownReportFiles.IsValidId(key))
                return ToolJson.Error("'" + id + "' is not a slowdown report id. Ids look like slowdown-20260930-140200; list_slowdown_reports gives them.");

            string? text = _data.ReadSlowdownReport(key);
            if (text == null)
                return ToolJson.Error("No slowdown report has the id '" + key + "'. Call list_slowdown_reports for the current ids.");

            bool truncated = text.Length > MaxReportChars;
            if (truncated)
            {
                int omitted = text.Length - MaxReportChars;
                text = text[..ReportHeadChars] +
                       "\n[... " + omitted.ToString(CultureInfo.InvariantCulture) + " characters of the timeline left out ...]\n" +
                       text[^(MaxReportChars - ReportHeadChars)..];
            }
            return new JsonObject { ["id"] = key, ["truncated"] = truncated, ["text"] = text };
        }

        /// <summary>
        /// The trigger line and the first worst offender by CPU and by disk, read from the report
        /// text that <c>SlowdownReportWriter.Write</c> produces.
        /// </summary>
        internal static (string? Trigger, string Summary) Summarize(string? text)
        {
            if (text == null) return (null, "The report could not be read.");

            string? trigger = null, topCpu = null, topDisk = null;
            bool empty = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (trigger == null && line.StartsWith("Trigger", StringComparison.Ordinal) && line.Contains(':'))
                    trigger = line[(line.IndexOf(':') + 1)..].Trim();
                else if (line.StartsWith("No samples were held", StringComparison.Ordinal))
                    empty = true;
                else if (line.StartsWith("By CPU", StringComparison.Ordinal))
                    topCpu = FirstOffender(lines, i);
                else if (line.StartsWith("By disk", StringComparison.Ordinal))
                    topDisk = FirstOffender(lines, i);
            }

            if (empty) return (trigger, "No samples were held when the report was written.");
            var parts = new List<string>();
            if (topCpu != null) parts.Add("busiest by CPU: " + topCpu);
            if (topDisk != null) parts.Add("busiest by disk: " + topDisk);
            return (trigger, parts.Count == 0 ? "No busy process was measured." : string.Join("; ", parts));
        }

        private static string? FirstOffender(string[] lines, int headingIndex)
        {
            if (lines[headingIndex].Trim().EndsWith("nothing measurable", StringComparison.Ordinal)) return null;
            if (headingIndex + 1 >= lines.Length) return null;
            string next = Regex.Replace(lines[headingIndex + 1].Trim(), "\\s{2,}", " ");
            return next.Length == 0 || next == "nothing measurable" ? null : next;
        }

        // ----- alerts ------------------------------------------------------------------------

        private JsonObject BuildAlerts(string? since)
        {
            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since))
            {
                if (!TimeRange.TryParse(since, _data.UtcNow, out DateTime parsed)) return BadTime(since);
                sinceUtc = parsed;
            }

            var rules = new JsonArray();
            foreach (AlertRule rule in _data.AlertRules())
            {
                rules.Add(new JsonObject
                {
                    ["id"] = rule.Id,
                    ["label"] = rule.Label,
                    ["metric"] = rule.Metric.ToString(),
                    ["threshold"] = Num(rule.Threshold),
                    ["unit"] = rule.Unit.Trim(),
                    ["firesWhen"] = rule.Above ? "above" : "below",
                    ["sustainSeconds"] = rule.SustainSeconds,
                    ["enabled"] = rule.Enabled,
                });
            }

            var raised = new JsonArray();
            foreach (AlertEvent alert in _data.RecentAlerts())
            {
                DateTime utc = FromLocal(alert.At);
                if (sinceUtc is DateTime from && utc < from) continue;
                raised.Add(new JsonObject
                {
                    ["ruleId"] = alert.Rule.Id,
                    ["title"] = alert.Title,
                    ["message"] = alert.Message,
                    ["value"] = Num(alert.Value),
                    ["unit"] = alert.Rule.Unit.Trim(),
                    ["utc"] = Iso(utc),
                    ["local"] = LocalText(utc),
                });
            }

            var result = new JsonObject
            {
                ["rules"] = rules,
                ["raised"] = raised,
                ["note"] = "Raised alerts are kept in memory since MicaStats started (the last 50); cleared alerts are not recorded.",
            };
            if (sinceUtc is DateTime s) result["since"] = Iso(s);
            return result;
        }

        // ----- hardware, battery, boot -------------------------------------------------------

        private async Task<JsonNode> BuildHardwareAsync(CancellationToken ct)
        {
            string? report = await _data.HardwareReportAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(report)
                ? ToolJson.Unavailable("The hardware details could not be read.")
                : new JsonObject { ["report"] = report };
        }

        private async Task<JsonNode> BuildBatteryAsync(CancellationToken ct)
        {
            BatteryReading? reading = _data.Battery();
            if (reading == null || !reading.Present) return ToolJson.Unavailable("This PC has no battery.");
            BatteryHealth? health = await _data.BatteryHealthAsync(ct).ConfigureAwait(false);

            var result = new JsonObject
            {
                ["percent"] = reading.Percent,
                ["onAc"] = reading.OnAcPower,
                ["charging"] = reading.Charging,
                ["watts"] = Reading(reading.RateMw > 0, reading.Watts, "Idle or not reported."),
            };

            if (reading.Discharging)
            {
                TimeSpan? left = BatteryEstimate.TimeToEmpty(reading.RemainingMwh, reading.RateMw);
                result["minutesLeft"] = Reading(left.HasValue, left?.TotalMinutes ?? 0, "Cannot be estimated right now.");
            }
            if (reading.Charging)
            {
                TimeSpan? toFull = health == null ? null
                    : BatteryEstimate.TimeToFull(reading.RemainingMwh, health.FullChargeCapacityMwh, reading.RateMw);
                result["minutesToFull"] = Reading(toFull.HasValue, toFull?.TotalMinutes ?? 0, "Cannot be estimated right now.");
            }

            if (health == null || !health.Any || health.HealthPercent < 0)
            {
                result["health"] = ToolJson.Unavailable("Windows did not report the design capacity of this battery.");
            }
            else
            {
                result["health"] = new JsonObject
                {
                    ["healthPercent"] = Num(health.HealthPercent),
                    ["wearPercent"] = Num(Math.Max(0d, 100d - health.HealthPercent)),
                    ["verdict"] = BatteryEstimate.HealthVerdict(health.HealthPercent),
                    ["designCapacityMwh"] = health.DesignCapacityMwh,
                    ["fullChargeCapacityMwh"] = health.FullChargeCapacityMwh,
                    ["cycleCount"] = Reading(health.CycleCount > 0, health.CycleCount, "The battery does not report a cycle count."),
                    ["packs"] = health.Packs.Count,
                };
            }
            return result;
        }

        private async Task<JsonNode> BuildBootAsync(CancellationToken ct)
        {
            BootAnalysis? boot = await _data.BootAsync(ct).ConfigureAwait(false);
            if (boot == null) return ToolJson.Unavailable("The boot history could not be read.");
            if (boot.Boots.Count == 0) return ToolJson.Unavailable(boot.Problem ?? "Windows has not recorded a boot yet.");

            var boots = new JsonArray();
            foreach (BootRecord b in boot.Boots.Take(MaxBoots))
            {
                DateTime utc = FromLocal(b.BootAt);
                boots.Add(new JsonObject
                {
                    ["utc"] = Iso(utc),
                    ["local"] = LocalText(utc),
                    ["seconds"] = Num(b.Seconds),
                    ["mainPathSeconds"] = Num(b.MainPathMs / 1000d),
                    ["afterSignInSeconds"] = Num(b.PostBootMs / 1000d),
                    ["startupApps"] = b.StartupAppCount,
                    ["slowerThanUsual"] = b.IsDegradation,
                });
            }

            var delays = new JsonArray();
            foreach (StartupDelay d in boot.Delays.Take(MaxBootDelays))
            {
                var item = new JsonObject
                {
                    ["kind"] = d.Kind.ToString(),
                    ["name"] = d.DisplayName,
                    ["seconds"] = Num(d.TotalMs / 1000d),
                    ["delaySeconds"] = Num(d.DegradationMs / 1000d),
                };
                if (!string.IsNullOrWhiteSpace(d.Company)) item["company"] = d.Company;
                delays.Add(item);
            }

            // Names only: a startup entry's command line can carry anything, and no tool ever
            // sends a command line.
            var programs = new JsonArray();
            foreach (StartupEntry e in boot.Entries.Take(MaxStartupPrograms))
            {
                programs.Add(new JsonObject { ["name"] = e.Name, ["enabled"] = e.Enabled, ["scope"] = e.Scope.ToString() });
            }

            return new JsonObject
            {
                ["boots"] = boots,
                ["averageSeconds"] = Num(boot.AverageSeconds),
                ["trendSeconds"] = Num(boot.TrendSeconds),
                ["slowedLastBoot"] = delays,
                ["startupPrograms"] = programs,
            };
        }
    }
}
```

- [ ] **Step 5: Dispatch the new tools by name**

In `Services/Ai/Tools/MicaTools.cs`, inside `InvokeAsync`, replace:

```csharp
                case ToolNames.GetTopProcesses:
                    return await GetTopProcessesAsync(ArgText(args, "by") ?? "cpu", ArgInt(args, "count", 10), ct).ConfigureAwait(false);
                default:
```

with:

```csharp
                case ToolNames.GetTopProcesses:
                    return await GetTopProcessesAsync(ArgText(args, "by") ?? "cpu", ArgInt(args, "count", 10), ct).ConfigureAwait(false);
                case ToolNames.ListSlowdownReports:
                    return await ListSlowdownReportsAsync(ArgInt(args, "limit", 10), ct).ConfigureAwait(false);
                case ToolNames.GetSlowdownReport:
                    return await GetSlowdownReportAsync(ArgText(args, "id") ?? "", ct).ConfigureAwait(false);
                case ToolNames.ListAlerts:
                    return await ListAlertsAsync(ArgText(args, "since"), ct).ConfigureAwait(false);
                case ToolNames.GetHardware:
                    return await GetHardwareAsync(ct).ConfigureAwait(false);
                case ToolNames.GetBattery:
                    return await GetBatteryAsync(ct).ConfigureAwait(false);
                case ToolNames.GetBootSummary:
                    return await GetBootSummaryAsync(ct).ConfigureAwait(false);
                default:
```

- [ ] **Step 6: Add the offline data source**

Create `Services/Ai/Tools/OfflineMicaData.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The data source of the MCP bridge when no MicaStats instance answers the tool pipe. It
    /// serves what is on disk (history files and slowdown reports); every live reading throws
    /// <see cref="DataUnavailableException"/> with <see cref="NotRunningMessage"/>, which the
    /// tools pass to the MCP client as <c>{"error": "MicaStats is not running"}</c>.
    /// </summary>
    public sealed class OfflineMicaData : IMicaData
    {
        /// <summary>The error every live tool returns in the offline bridge.</summary>
        internal const string NotRunningMessage = "MicaStats is not running";

        private readonly HistoryStore _store;
        private readonly string _reportDir;
        private readonly Func<DateTime> _clock;

        /// <summary>Reads history from <paramref name="store"/> and slowdown reports from <paramref name="reportDir"/>.</summary>
        public OfflineMicaData(HistoryStore store, string reportDir, Func<DateTime> utcClock)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _reportDir = reportDir ?? throw new ArgumentNullException(nameof(reportDir));
            _clock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
        }

        /// <inheritdoc/>
        public DateTime UtcNow => _clock();

        /// <inheritdoc/>
        public bool IsLive => false;

        /// <inheritdoc/>
        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc) => _store.Read(fromUtc, toUtc);

        /// <inheritdoc/>
        public IReadOnlyList<SavedReport> SlowdownReports() => SlowdownReportFiles.List(_reportDir);

        /// <inheritdoc/>
        public string? ReadSlowdownReport(string id) => SlowdownReportFiles.Read(_reportDir, id);

        /// <inheritdoc/>
        public SystemMetrics? Latest() => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, SeriesStats> RecentStats() => throw NotRunning();

        /// <inheritdoc/>
        public Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyList<AlertRule> AlertRules() => throw NotRunning();

        /// <inheritdoc/>
        public IReadOnlyList<AlertEvent> RecentAlerts() => throw NotRunning();

        /// <inheritdoc/>
        public Task<string?> HardwareReportAsync(CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public BatteryReading? Battery() => throw NotRunning();

        /// <inheritdoc/>
        public Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct) => throw NotRunning();

        /// <inheritdoc/>
        public Task<BootAnalysis?> BootAsync(CancellationToken ct) => throw NotRunning();

        private static DataUnavailableException NotRunning() => new(NotRunningMessage);
    }
}
```

- [ ] **Step 7: Add the live data source**

Create `Services/Ai/Tools/LiveMicaData.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.HardwareInfo;
using Kil0bitSystemMonitor.Services.History;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// The data source of the running app. <see cref="MetricsHistory"/> is UI-thread only, so
    /// live readings are taken through the UI dispatcher; tool calls arrive on other threads
    /// (the assistant loop, the tool pipe) and never touch the series directly.
    /// </summary>
    public sealed class LiveMicaData : IMicaData
    {
        /// <summary>How long a process ranking may take: two sampler passes 2 s apart, plus slack.</summary>
        internal static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(6);

        /// <summary>The hardware report and boot history take a second or more and change rarely.</summary>
        private static readonly TimeSpan SlowCacheTtl = TimeSpan.FromMinutes(10);

        private readonly MetricsHistory _history;
        private readonly Dispatcher _ui;
        private readonly ProcessSampler _sampler;
        private readonly HistoryStore _store;
        private readonly Func<AlertMonitor?> _alerts;
        private readonly Func<BatteryMonitor?> _battery;
        private readonly Func<DateTime> _clock;
        private readonly SlowCache<string?> _hardware;
        private readonly SlowCache<BootAnalysis?> _boot;

        /// <summary>
        /// Wires the tools to the app's own services. <paramref name="alerts"/> and
        /// <paramref name="battery"/> are read on each call, because they may start after the tools.
        /// </summary>
        public LiveMicaData(MetricsHistory history, Dispatcher ui, ProcessSampler sampler, HistoryStore store,
                            Func<AlertMonitor?> alerts, Func<BatteryMonitor?> battery, Func<DateTime> utcClock)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
            _battery = battery ?? throw new ArgumentNullException(nameof(battery));
            _clock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
            _hardware = new SlowCache<string?>(
                () => HardwareReportWriter.Write(HardwareInfoService.Gather(), HardwareInfoService.AppVersion), _clock);
            _boot = new SlowCache<BootAnalysis?>(() => BootAnalyzer.Gather(), _clock);
        }

        /// <inheritdoc/>
        public DateTime UtcNow => _clock();

        /// <inheritdoc/>
        public bool IsLive => true;

        /// <inheritdoc/>
        public SystemMetrics? Latest() => OnUi(() => _history.Cpu.Count == 0 ? null : _history.Latest);

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, SeriesStats> RecentStats() => OnUi(() =>
        {
            var stats = new Dictionary<string, SeriesStats>();
            AddStats(stats, "cpu", _history.Cpu);
            AddStats(stats, "ram", _history.Ram);
            AddStats(stats, "gpu", _history.Gpu);
            AddStats(stats, "temp", _history.Temp);
            AddStats(stats, "netUp", _history.NetUp);
            AddStats(stats, "netDown", _history.NetDown);
            return (IReadOnlyDictionary<string, SeriesStats>)stats;
        });

        /// <summary>
        /// Takes a sampler lease, waits for a sample with CPU deltas (two passes, about 2 s),
        /// ranks every process and releases the lease, whatever happens.
        /// </summary>
        public async Task<IReadOnlyList<ProcessInfo>> TopProcessesAsync(string by, int count, CancellationToken ct)
        {
            _sampler.Retain();
            try
            {
                var waited = Stopwatch.StartNew();
                while (!_sampler.HasCpuData)
                {
                    if (waited.Elapsed > SampleTimeout)
                        throw new DataUnavailableException("The process list did not arrive within 6 seconds.");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                IReadOnlyList<ProcessUsage> all = _sampler.AllProcesses;
                IEnumerable<ProcessUsage> ranked = by switch
                {
                    "memory" => all.OrderByDescending(p => p.WorkingSet),
                    "disk" => all.OrderByDescending(p => p.DiskBytesPerSec),
                    _ => all.OrderByDescending(p => p.CpuPercent),
                };
                return ranked.Take(Math.Max(1, count))
                    .Select(p => new ProcessInfo(p.Name, ProcessPaths.TryGetPath(p.Pid), p.Pid, p.CreateTime,
                        p.CpuPercent, p.WorkingSet / 1048576d, p.DiskBytesPerSec / 1024d))
                    .ToList();
            }
            finally
            {
                _sampler.Release();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<HistoryRow> History(DateTime fromUtc, DateTime toUtc) => _store.Read(fromUtc, toUtc);

        /// <inheritdoc/>
        public IReadOnlyList<SavedReport> SlowdownReports() => SlowdownReportFiles.List(SlowdownRecorder.ReportDir);

        /// <inheritdoc/>
        public string? ReadSlowdownReport(string id) => SlowdownReportFiles.Read(SlowdownRecorder.ReportDir, id);

        /// <inheritdoc/>
        public IReadOnlyList<AlertRule> AlertRules() => _alerts()?.Rules ?? Array.Empty<AlertRule>();

        /// <inheritdoc/>
        public IReadOnlyList<AlertEvent> RecentAlerts() => _alerts()?.Recent ?? Array.Empty<AlertEvent>();

        /// <inheritdoc/>
        public Task<string?> HardwareReportAsync(CancellationToken ct) => _hardware.GetAsync(ct);

        /// <inheritdoc/>
        public BatteryReading? Battery()
        {
            BatteryReading reading = BatteryMonitor.ReadCached();
            return reading.Present ? reading : null;
        }

        /// <inheritdoc/>
        public async Task<BatteryHealth?> BatteryHealthAsync(CancellationToken ct)
        {
            // A desktop has nothing to read, and asking would spawn powercfg for nothing.
            if (Battery() == null) return null;
            BatteryMonitor? monitor = _battery();
            if (monitor == null) return null;
            BatteryHealth health = await monitor.GetHealthAsync().WaitAsync(ct).ConfigureAwait(false);
            return health.Any ? health : null;
        }

        /// <inheritdoc/>
        public Task<BootAnalysis?> BootAsync(CancellationToken ct) => _boot.GetAsync(ct);

        /// <summary>Min/avg/max of the non-negative samples; negative values are "unavailable" sentinels.</summary>
        private static void AddStats(Dictionary<string, SeriesStats> into, string key, Series series)
        {
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            int n = 0;
            for (int i = 0; i < series.Count; i++)
            {
                float v = series[i];
                if (v < 0 || !float.IsFinite(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                n++;
            }
            if (n > 0) into[key] = new SeriesStats(min, (float)(sum / n), max, n);
        }

        private T OnUi<T>(Func<T> read)
        {
            if (_ui.CheckAccess()) return read();
            if (_ui.HasShutdownStarted) throw new DataUnavailableException("MicaStats is closing.");
            return _ui.Invoke(read);
        }

        /// <summary>One slow value, gathered on the thread pool and kept for <see cref="SlowCacheTtl"/>.</summary>
        private sealed class SlowCache<T>
        {
            private readonly Func<T> _load;
            private readonly Func<DateTime> _clock;
            private readonly object _gate = new();
            private DateTime _at;
            private bool _has;
            private T _value = default!;

            public SlowCache(Func<T> load, Func<DateTime> clock)
            {
                _load = load;
                _clock = clock;
            }

            public async Task<T> GetAsync(CancellationToken ct)
            {
                lock (_gate)
                {
                    if (_has && _clock() - _at < SlowCacheTtl) return _value;
                }
                T value = await Task.Run(_load, ct).WaitAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _value = value;
                    _at = _clock();
                    _has = true;
                }
                return value;
            }
        }
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiToolsReportTests|FullyQualifiedName~AiMicaDataTests|FullyQualifiedName~AiToolsTests"`
Expected: 45 passed (the 16 + 7 new tests, and Task 6's 22 `AiToolsTests` again because `InvokeAsync` changed), 0 failed. The two `Live_top_processes_*` tests use the real `ProcessSampler` (two kernel snapshots, about 2 s); they touch no network and no `%APPDATA%`.

- [ ] **Step 9: Build the tools in App.Ai.cs**

In `App.Ai.cs`, replace the line:

```csharp
    // AI anchor: members
```

with:

```csharp
    /// <summary>
    /// The read-only data tools over the running app, shared by the assistant and the tool pipe;
    /// null until <see cref="StartAi"/> has built them, or if building them failed (logged).
    /// </summary>
    public static Services.Ai.Tools.MicaTools? AiTools { get; private set; }

    // AI anchor: members
```

Then replace the line:

```csharp
        // AI anchor: start
```

with:

```csharp
        try
        {
            // Live readings go through the UI dispatcher, process rankings take a short lease on the
            // shared sampler, and the alert and battery monitors are looked up on each call.
            AiTools = new Services.Ai.Tools.MicaTools(
                new Services.Ai.Tools.LiveMicaData(history, ui, SharedProcessSampler, History,
                    () => AlertMonitorForAi, () => Battery, () => DateTime.UtcNow),
                Services.Ai.Tools.Redactor.ForCurrentUser());
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("ai", "Starting the data tools failed", ex);
        }
        // AI anchor: start
```

(`App.Ai.cs` has a file-scoped `namespace Kil0bitSystemMonitor;`, so `Services.Ai.Tools.MicaTools` names `Kil0bitSystemMonitor.Services.Ai.Tools.MicaTools` without a new `using` line. `history` and `ui` are `StartAi`'s parameters; `SharedProcessSampler` and `Battery` are App's existing static properties.)

- [ ] **Step 10: Build the app**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: `Build succeeded`, 0 errors. Then confirm the anchors survived:

Run: `grep -c "// AI anchor: " App.Ai.cs`
Expected: `4`

- [ ] **Step 11: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (955 + 23 = 978).

```bash
git add Services/Ai/Tools/SlowdownReportFiles.cs Services/Ai/Tools/MicaTools.Reports.cs Services/Ai/Tools/MicaTools.cs Services/Ai/Tools/OfflineMicaData.cs Services/Ai/Tools/LiveMicaData.cs App.Ai.cs tests/Kil0bitSystemMonitor.Tests/AiToolsReportTests.cs tests/Kil0bitSystemMonitor.Tests/AiMicaDataTests.cs
git commit -F - <<'EOF'
feat(ai): report, alert, hardware, battery and boot tools with live and offline data

The catalogue now covers slowdown reports, alerts, hardware, battery and
boot. LiveMicaData reads the running app (UI-thread series, a sampler lease
released after every ranking, cached hardware and boot data) and
OfflineMicaData serves history and reports from disk for the MCP bridge,
answering MicaStats is not running for live tools. App.AiTools is built
at startup.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 8: The assistant

The in-app assistant: a daily question counter that resets at local midnight, the system prompt, the provider factory (Claude through the official Anthropic SDK, or any OpenAI-compatible endpoint with the `max_tokens` patch), one-sentence error texts, and `AiAssistant`, which offers the nine read-only tools plus `suggest_action` as `AIFunction`s, runs the tool loop through `FunctionInvokingChatClient` (8 rounds, then one forced answer without tools), streams `AssistantUpdate`s, marks the system prompt for Claude prompt caching, records suggested actions without running them, and falls back to limited mode (a live snapshot, no tools) when a non-Claude endpoint rejects tools. Tests use a scripted `IChatClient` and an offline `HttpMessageHandler`; nothing reaches a provider. The task commits twice: the building blocks, then the assistant.

**Files:**
- Create: `Services/Ai/SuggestedAction.cs` (`SuggestedActionKind`, `SuggestedAction`)
- Create: `Services/Ai/AiConversation.cs` (`AiConversation`, `AssistantUpdateKind`, `AssistantUpdate`)
- Create: `Services/Ai/UsageMeter.cs`
- Create: `Services/Ai/AiPrompts.cs`
- Create: `Services/Ai/AiErrorText.cs`
- Create: `Services/Ai/AiProviderFactory.cs` (`AiClientResult`, `AiProviderFactory`, internal `MaxTokensChatClient`)
- Create: `Services/Ai/ClaudeCache.cs` (internal)
- Create: `Services/Ai/AiToolFunctions.cs` (internal)
- Create: `Services/Ai/FinalAnswerChatClient.cs` (internal `FinalAnswerChatClient`, `ToolHistory`)
- Create: `Services/Ai/AiAssistant.cs` (`AiAssistantOptions`, `AiAssistant`)
- Create: `tests/Kil0bitSystemMonitor.Tests/AiScriptedHttpHandler.cs` (test helper `ScriptedHttpHandler`)
- Create: `tests/Kil0bitSystemMonitor.Tests/AiScriptedChatClient.cs` (test helper `ScriptedChatClient`)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiUsageMeterTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiErrorTextTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiProviderFactoryTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiAssistantTests.cs`

**Interfaces:**
- Consumes:
  - Tasks 6-7: `MicaTools` (all nine tool methods, `GetLiveStatusAsync`, `GetTopProcessesAsync("cpu", 5, ct)` for the limited-mode snapshot), `ToolNames`, `ToolJson.Error`, `ToolJson.ToText`, `ToolJson.TextOptions`, `Redactor`, `ProcessInfo`, test helper `FakeMicaData`.
  - Task 1: `AppConfig.AiProvider`, `AiClaudeModel`, `AiCompatibleBaseUrl`, `AiCompatibleModel`; `AiProviders.Claude` / `OpenAiCompatible`; `SecretNames.ClaudeKey` / `CompatibleKey`; `public sealed class SecretStore { public SecretStore(string path, Action<string>? warn = null); public string? Get(string name); public void Set(string name, string value); }`; test helper `AiTestEnv`.
  - Existing: `DiagnosticsLog.DataDir`.
  - Packages (Task 1): `Anthropic` 12.51.0 (`AnthropicClient { ApiKey, HttpClient, MaxRetries }`, `AsIChatClient(model, defaultMaxOutputTokens)`, `TextContent.WithCacheControl(Ttl.Ttl1h)`, `Anthropic.Exceptions.AnthropicApiException { StatusCode, ResponseBody }`, `AnthropicIOException`), `Microsoft.Extensions.AI` 10.10.0 (`ChatClientBuilder.UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 8)`, `AIFunctionFactory.Create(Delegate, AIFunctionFactoryOptions)`, `DelegatingChatClient`, `ToChatResponse()`), `Microsoft.Extensions.AI.OpenAI` 10.10.1 (`OpenAIClient`, `OpenAIClientOptions { Endpoint, Transport }`, `HttpClientPipelineTransport`, `ChatCompletionOptions.Patch.Set` under `SCME0001`, `System.ClientModel.ClientResultException { Status, GetRawResponse() }`).
- Produces (namespace `Kil0bitSystemMonitor.Services.Ai`), exactly as contract.md Task 8:
  - `public enum SuggestedActionKind { EndProcess, RecordSlowdown, OpenDiagnostics, OpenProcessWindow }`; `public sealed record SuggestedAction(SuggestedActionKind Kind, string Label, string Reason, int? Pid = null, long? CreateTime = null, string? ProcessName = null);`
  - `public sealed class AiConversation { List<ChatMessage> Messages { get; } List<SuggestedAction> Suggestions { get; } void Clear(); }`
  - `public enum AssistantUpdateKind { Text, ToolUsed, Suggestion, LimitedMode, Error, Done }`; `public sealed record AssistantUpdate(AssistantUpdateKind Kind, string? Text = null, string? ToolName = null, string? ToolArgs = null, SuggestedAction? Suggestion = null);`
  - `public sealed class UsageMeter { UsageMeter(string path, Func<DateTime> localClock); static string DefaultPath { get; } /* %APPDATA%\MicaStats\ai-usage.json */; int UsedToday { get; } DateTime ResetsAtLocal { get; } bool TryConsume(int dailyLimit); }`
  - `public static class AiPrompts { public const string System; public static string LimitedModeContext(JsonNode liveSummary); internal const string ToolLimitReached; }`
  - `public sealed record AiClientResult(IChatClient? Client, string? Problem, bool IsClaude);` `public static class AiProviderFactory { public static AiClientResult Create(AppConfig config, SecretStore secrets, HttpMessageHandler? handler = null); }` — Problems: "Add an API key in Settings > AI.", "Choose a model in Settings > AI.", "Enter a valid http or https base URL in Settings > AI.".
  - `public static class AiErrorText { public static string Describe(Exception ex); internal static bool IsToolsUnsupported(Exception ex); }` — 401 gives "The key was rejected. Check it in Settings > AI.".
  - `public sealed class AiAssistantOptions { int MaxToolRounds = 8; int MaxOutputTokens = 2000; Func<int> DailyLimit = () => 100; }`
  - `public sealed class AiAssistant { AiAssistant(IChatClient client, bool isClaude, MicaTools tools, UsageMeter usage, AiAssistantOptions options); IAsyncEnumerable<AssistantUpdate> AskAsync(AiConversation conversation, string question, CancellationToken ct); }` — behaviour: see the class doc comment and the controller notes (one `Done` last; failures one `Error`; failed or cancelled questions leave the conversation as it was; one `TryConsume` per non-empty question).
  - Test helpers `internal sealed class ScriptedHttpHandler : HttpMessageHandler` and `internal sealed class ScriptedChatClient : IChatClient`.

- [ ] **Step 1: Write the failing tests for the building blocks**

Create `tests/Kil0bitSystemMonitor.Tests/AiScriptedHttpHandler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// An offline HTTP endpoint for the provider SDKs: records every request and answers from a
    /// script. No test reaches the network or a real provider.
    /// </summary>
    internal sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Func<Sent, (HttpStatusCode Status, string ContentType, string Body)> _respond;

        /// <summary>One request: URL, the key header (x-api-key or Authorization) and the body.</summary>
        public sealed record Sent(string Url, string Auth, string Body)
        {
            public bool Streaming => Body.Contains("\"stream\":true", StringComparison.Ordinal);
        }

        public ScriptedHttpHandler(Func<Sent, (HttpStatusCode Status, string ContentType, string Body)> respond) => _respond = respond;

        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string auth = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? key)
                ? "x-api-key=" + string.Join(",", key)
                : request.Headers.Authorization?.ToString() ?? "";
            var sent = new Sent(request.RequestUri!.ToString(), auth, body);
            lock (Requests) Requests.Add(sent);
            (HttpStatusCode status, string contentType, string text) = _respond(sent);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, contentType) };
        }

        // ----- Canned provider replies -------------------------------------------------------

        public static string ClaudeText(string text) =>
            "{\"id\":\"msg_2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"" + text +
            "\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}";

        public const string ClaudeStreamText =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_3\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Stream\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ed ok\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        public const string ClaudeStreamToolUse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_01\",\"name\":\"get_live_status\",\"input\":{}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        public const string ClaudeUnauthorized =
            "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}";

        public const string OpenAiText =
            "{\"id\":\"c2\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Core 4 is idle.\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":5,\"total_tokens\":10}}";

        public const string OpenAiStreamText =
            "data: {\"id\":\"c3\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Str\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c3\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama3.2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"eamed\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        public const string OllamaNoTools =
            "{\"error\":{\"message\":\"registry.ollama.ai/library/gemma:2b does not support tools\",\"type\":\"api_error\",\"param\":null,\"code\":null}}";
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiUsageMeterTests.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiUsageMeterTests
    {
        [Fact]
        public void Counts_questions_until_the_daily_limit()
        {
            using var env = new AiTestEnv();
            var meter = new UsageMeter(env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));

            Assert.True(meter.TryConsume(2));
            Assert.True(meter.TryConsume(2));
            Assert.False(meter.TryConsume(2));
            Assert.Equal(2, meter.UsedToday);
        }

        [Fact]
        public void The_count_survives_a_restart()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            var first = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));
            first.TryConsume(2);
            first.TryConsume(2);

            var second = new UsageMeter(path, () => new DateTime(2026, 9, 30, 18, 0, 0));

            Assert.Equal(2, second.UsedToday);
            Assert.False(second.TryConsume(2));
        }

        [Fact]
        public void The_count_starts_again_at_local_midnight()
        {
            using var env = new AiTestEnv();
            DateTime now = new(2026, 9, 30, 23, 59, 0);
            var meter = new UsageMeter(env.PathOf("ai-usage.json"), () => now);
            meter.TryConsume(1);
            Assert.False(meter.TryConsume(1));
            Assert.Equal(new DateTime(2026, 10, 1), meter.ResetsAtLocal);

            now = new DateTime(2026, 10, 1, 0, 0, 1);

            Assert.Equal(0, meter.UsedToday);
            Assert.True(meter.TryConsume(1));
            Assert.Equal(new DateTime(2026, 10, 2), meter.ResetsAtLocal);
        }

        [Fact]
        public void A_damaged_file_counts_as_zero()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            File.WriteAllText(path, "not json {");

            var meter = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));

            Assert.Equal(0, meter.UsedToday);
            Assert.True(meter.TryConsume(1));
        }

        [Fact]
        public void The_file_keeps_a_gregorian_date_under_a_thai_culture()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var meter = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));

                meter.TryConsume(5);

                JsonNode saved = JsonNode.Parse(File.ReadAllText(path))!;
                Assert.Equal("2026-09-30", saved["date"]!.GetValue<string>());
                Assert.Equal(1, saved["count"]!.GetValue<int>());
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiErrorTextTests.cs`:

```csharp
using System;
using System.ClientModel;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiErrorTextTests
    {
        [Fact]
        public void A_rejected_key_points_to_settings()
        {
            var claude = new AnthropicUnauthorizedException(new HttpRequestException("401"))
            {
                StatusCode = HttpStatusCode.Unauthorized,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}",
            };
            var http = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(claude));
            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(http));
        }

        [Fact]
        public void Rate_limits_and_overload_ask_the_user_to_wait()
        {
            var limited = new AnthropicRateLimitException(new HttpRequestException("429"))
            {
                StatusCode = (HttpStatusCode)429,
                ResponseBody = "",
            };
            var overloaded = new AnthropicApiException("overloaded", new HttpRequestException("529"))
            {
                StatusCode = (HttpStatusCode)529,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            };

            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(limited));
            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(overloaded));
        }

        [Fact]
        public void An_unknown_model_shows_the_server_message()
        {
            var notFound = new AnthropicNotFoundException(new HttpRequestException("404"))
            {
                StatusCode = HttpStatusCode.NotFound,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-nope\"}}",
            };

            Assert.Equal("The AI service said: model: claude-nope", AiErrorText.Describe(notFound));
        }

        [Fact]
        public void Network_failures_and_timeouts_have_their_own_sentences()
        {
            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(new HttpRequestException("No such host is known.")));
            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(new AnthropicIOException("I/O exception", new HttpRequestException("reset"))));
            Assert.Equal(AiErrorText.TimedOut, AiErrorText.Describe(new TaskCanceledException("timeout", new TimeoutException())));
        }

        [Fact]
        public void Anything_else_keeps_its_first_line()
        {
            Assert.Equal("The AI request failed: boom", AiErrorText.Describe(new InvalidOperationException("boom\nstack")));
        }

        [Theory]
        [InlineData("registry.ollama.ai/library/gemma:2b does not support tools", true)]
        [InlineData("\"auto\" tool choice requires --enable-auto-tool-choice and --tool-call-parser to be set", true)]
        [InlineData("tools param requires --jinja flag", true)]
        [InlineData("model 'llama9' not found", false)]
        public void Tool_refusals_are_recognised_by_their_message(string message, bool expected)
        {
            Assert.Equal(expected, AiErrorText.IsToolsUnsupported(new ClientResultException(message)));
        }

        [Fact]
        public void Auth_network_and_cancellation_failures_are_never_tool_refusals()
        {
            var unauthorized = new HttpRequestException("tools are not supported", null, HttpStatusCode.Unauthorized);

            Assert.False(AiErrorText.IsToolsUnsupported(unauthorized));
            Assert.False(AiErrorText.IsToolsUnsupported(new HttpRequestException("tools host is not supported")));
            Assert.False(AiErrorText.IsToolsUnsupported(new OperationCanceledException("tools not supported")));
        }
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiProviderFactoryTests.cs`:

```csharp
using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiProviderFactoryTests
    {
        private static SecretStore Secrets(AiTestEnv env, string? claudeKey = null, string? compatibleKey = null)
        {
            var store = new SecretStore(env.PathOf("secrets.bin"), _ => { });
            if (claudeKey != null) store.Set(SecretNames.ClaudeKey, claudeKey);
            if (compatibleKey != null) store.Set(SecretNames.CompatibleKey, compatibleKey);
            return store;
        }

        private static AppConfig Compatible(string baseUrl, string model) => new()
        {
            AiProvider = AiProviders.OpenAiCompatible,
            AiCompatibleBaseUrl = baseUrl,
            AiCompatibleModel = model,
        };

        [Fact]
        public void Claude_without_a_key_asks_for_one()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env));

            Assert.Null(result.Client);
            Assert.Equal("Add an API key in Settings > AI.", result.Problem);
            Assert.True(result.IsClaude);
        }

        [Fact]
        public void A_compatible_endpoint_without_a_model_asks_for_one()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(Compatible("http://localhost:11434/v1", " "), Secrets(env));

            Assert.Null(result.Client);
            Assert.Equal("Choose a model in Settings > AI.", result.Problem);
            Assert.False(result.IsClaude);
        }

        [Fact]
        public void A_base_url_that_is_not_http_is_refused()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(Compatible("ftp://localhost/v1", "llama3.2"), Secrets(env));

            Assert.Null(result.Client);
            Assert.Contains("base URL", result.Problem);
        }

        [Fact]
        public async Task Claude_calls_the_messages_api_with_the_key_the_model_and_the_output_cap()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("CPU is fine.")));

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-test"), handler);
            ChatResponse response = await result.Client!.GetResponseAsync("How is my CPU?");

            Assert.Equal("CPU is fine.", response.Text);
            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url);
            Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal("claude-haiku-4-5", body["model"]!.GetValue<string>());
            Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_local_server_gets_max_tokens_and_a_placeholder_key()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.OpenAiText));

            AiClientResult result = AiProviderFactory.Create(Compatible("http://localhost:11434/v1", "llama3.2"), Secrets(env), handler);
            ChatResponse response = await result.Client!.GetResponseAsync("CPU?", new ChatOptions { MaxOutputTokens = 2000 });

            Assert.Equal("Core 4 is idle.", response.Text);
            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("http://localhost:11434/v1/chat/completions", sent.Url);
            Assert.Equal("Bearer none", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal("llama3.2", body["model"]!.GetValue<string>());
            Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
            Assert.Null(body["max_completion_tokens"]);
        }

        [Fact]
        public async Task OpenAI_itself_keeps_max_completion_tokens()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.OpenAiText));

            AiClientResult result = AiProviderFactory.Create(Compatible("https://api.openai.com/v1", "gpt-5-mini"),
                Secrets(env, compatibleKey: "sk-test"), handler);
            await result.Client!.GetResponseAsync("CPU?", new ChatOptions { MaxOutputTokens = 2000 });

            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("Bearer sk-test", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal(2000, body["max_completion_tokens"]!.GetValue<int>());
            Assert.Null(body["max_tokens"]);
        }

        [Fact]
        public async Task A_rejected_key_reads_as_such_for_both_providers()
        {
            using var env = new AiTestEnv();
            var claudeHandler = new ScriptedHttpHandler(_ => (HttpStatusCode.Unauthorized, "application/json", ScriptedHttpHandler.ClaudeUnauthorized));
            var openAiHandler = new ScriptedHttpHandler(_ => (HttpStatusCode.Unauthorized, "application/json",
                "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}"));
            IChatClient claude = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-bad"), claudeHandler).Client!;
            IChatClient openAi = AiProviderFactory.Create(Compatible("https://api.openai.com/v1", "gpt-5-mini"),
                Secrets(env, compatibleKey: "sk-bad"), openAiHandler).Client!;

            Exception claudeError = await Assert.ThrowsAnyAsync<Exception>(() => claude.GetResponseAsync("hi"));
            Exception openAiError = await Assert.ThrowsAnyAsync<Exception>(() => openAi.GetResponseAsync("hi"));

            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(claudeError));
            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(openAiError));
            Assert.Single(claudeHandler.Requests);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiUsageMeterTests|FullyQualifiedName~AiErrorTextTests|FullyQualifiedName~AiProviderFactoryTests"`
Expected: build FAILS with CS0246 — `The type or namespace name 'UsageMeter' could not be found` (and the same for `AiErrorText`, `AiProviderFactory`, `AiClientResult`).

- [ ] **Step 3: Add the suggested action**

Create `Services/Ai/SuggestedAction.cs`:

```csharp
namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>What a suggested-action button would do. The assistant only proposes; the user clicks.</summary>
    public enum SuggestedActionKind
    {
        /// <summary>End one process, identified by pid and start time so a recycled pid is never hit.</summary>
        EndProcess,

        /// <summary>Save a slowdown report of the last minutes now.</summary>
        RecordSlowdown,

        /// <summary>Open the Diagnostics window.</summary>
        OpenDiagnostics,

        /// <summary>Open the process window.</summary>
        OpenProcessWindow,
    }

    /// <summary>
    /// A button the model proposed through <c>suggest_action</c>. Nothing runs when it is
    /// created: the Ask window shows it, and a click re-checks the target (same pid and start
    /// time, the critical-process guard) before MicaStats acts.
    /// </summary>
    /// <param name="Kind">What the button does.</param>
    /// <param name="Label">The button text, in the language of the conversation.</param>
    /// <param name="Reason">One sentence from the model saying why it helps.</param>
    /// <param name="Pid">For <see cref="SuggestedActionKind.EndProcess"/>: the process id.</param>
    /// <param name="CreateTime">For <see cref="SuggestedActionKind.EndProcess"/>: the process start time as a FILETIME.</param>
    /// <param name="ProcessName">For <see cref="SuggestedActionKind.EndProcess"/>: the image name the model saw.</param>
    public sealed record SuggestedAction(SuggestedActionKind Kind, string Label, string Reason,
                                         int? Pid = null, long? CreateTime = null, string? ProcessName = null);
}
```

- [ ] **Step 4: Add the conversation and the update stream types**

Create `Services/Ai/AiConversation.cs`:

```csharp
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// One Ask window conversation: the messages sent back to the model on each question (never
    /// the system prompt, which the assistant adds per request) and the suggested actions of the
    /// latest answer.
    /// </summary>
    public sealed class AiConversation
    {
        /// <summary>User questions, assistant answers and tool calls with their results, oldest first.</summary>
        public List<ChatMessage> Messages { get; } = new();

        /// <summary>
        /// The suggestions of the latest answer. Cleared when a new question starts and by
        /// <see cref="Clear"/>, so a button whose suggestion is gone must stop working.
        /// </summary>
        public List<SuggestedAction> Suggestions { get; } = new();

        /// <summary>Starts over: forgets every message and suggestion.</summary>
        public void Clear()
        {
            Messages.Clear();
            Suggestions.Clear();
        }
    }

    /// <summary>What an <see cref="AssistantUpdate"/> carries.</summary>
    public enum AssistantUpdateKind
    {
        /// <summary>A piece of the answer text, to append to what is shown.</summary>
        Text,

        /// <summary>A tool ran: <see cref="AssistantUpdate.ToolName"/> with <see cref="AssistantUpdate.ToolArgs"/> (JSON), for the Details line.</summary>
        ToolUsed,

        /// <summary>A suggested-action button (<see cref="AssistantUpdate.Suggestion"/>).</summary>
        Suggestion,

        /// <summary>The endpoint cannot use tools; this answer comes from a snapshot. <see cref="AssistantUpdate.Text"/> explains.</summary>
        LimitedMode,

        /// <summary>The question failed; <see cref="AssistantUpdate.Text"/> is a sentence for the user. The question is not kept.</summary>
        Error,

        /// <summary>Always the last update of every question, whatever happened before it.</summary>
        Done,
    }

    /// <summary>One step of an answer as the Ask window renders it.</summary>
    public sealed record AssistantUpdate(AssistantUpdateKind Kind, string? Text = null, string? ToolName = null,
                                         string? ToolArgs = null, SuggestedAction? Suggestion = null);
}
```

- [ ] **Step 5: Add the daily question counter**

Create `Services/Ai/UsageMeter.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Counts the questions asked today, so a runaway loop or a stuck key cannot spend without
    /// limit. One Send or one Explain is one question however many tool rounds it takes; the
    /// count resets at local midnight, when the user's day does. Stored as
    /// <c>{"date":"yyyy-MM-dd","count":n}</c>; a missing or damaged file counts as zero.
    /// </summary>
    public sealed class UsageMeter
    {
        private readonly string _path;
        private readonly Func<DateTime> _localClock;
        private readonly object _gate = new();
        private string _date = "";
        private int _count;

        /// <summary>Reads the count kept in <paramref name="path"/>; <paramref name="localClock"/> gives local time.</summary>
        public UsageMeter(string path, Func<DateTime> localClock)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _localClock = localClock ?? throw new ArgumentNullException(nameof(localClock));
            Load();
        }

        /// <summary>%APPDATA%\MicaStats\ai-usage.json.</summary>
        public static string DefaultPath => Path.Combine(DiagnosticsLog.DataDir, "ai-usage.json");

        /// <summary>Questions asked since local midnight.</summary>
        public int UsedToday
        {
            get
            {
                lock (_gate) return _date == Today() ? _count : 0;
            }
        }

        /// <summary>The next local midnight, when the count starts again from zero.</summary>
        public DateTime ResetsAtLocal => _localClock().Date.AddDays(1);

        /// <summary>
        /// Counts one question and returns true, or returns false without counting when
        /// <paramref name="dailyLimit"/> (at least 1) questions were already asked today.
        /// </summary>
        public bool TryConsume(int dailyLimit)
        {
            lock (_gate)
            {
                string today = Today();
                if (_date != today)
                {
                    _date = today;
                    _count = 0;
                }
                if (_count >= Math.Max(1, dailyLimit)) return false;
                _count++;
                Save();
                return true;
            }
        }

        private string Today() => _localClock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject o) return;
                string? date = o["date"]?.GetValue<string>();
                int count = o["count"]?.GetValue<int>() ?? 0;
                if (date == null || count < 0) return;
                _date = date;
                _count = count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                          or InvalidOperationException or FormatException)
            {
                // A damaged file costs at most one day's count; the limit protects spending, not history.
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, new JsonObject { ["date"] = _date, ["count"] = _count }.ToJsonString());
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The in-memory count still holds for this session.
            }
        }
    }
}
```

- [ ] **Step 6: Add the prompts**

Create `Services/Ai/AiPrompts.cs`:

```csharp
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Tools;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The fixed texts the assistant sends. The system prompt is one constant so Claude can
    /// cache it: a single changed character would miss the cache on every question.
    /// </summary>
    public static class AiPrompts
    {
        /// <summary>The system prompt for every question.</summary>
        public const string System = """
            You are the assistant built into MicaStats, a Windows system monitor. You answer questions about this PC using MicaStats' read-only tools.

            Tools:
            - get_live_status: current CPU, memory, GPU, disks, network, battery and sensors, plus min/avg/max over the last two minutes.
            - get_history: recorded history for up to 7 days (only while "Keep 7 days of history" is on).
            - get_top_processes: the busiest processes right now by cpu, memory or disk, with pid and createTime.
            - list_slowdown_reports and get_slowdown_report: reports MicaStats saved when the PC struggled.
            - list_alerts: the alert rules and the alerts raised recently.
            - get_hardware, get_battery, get_boot_summary: hardware models, battery health, boot times.
            - suggest_action: offer the user a button (end a process, record a slowdown, open Diagnostics, open the process window).

            Rules:
            - Answer in the language of the user's latest message.
            - Use the tools instead of guessing, and call only the ones you need.
            - Times in tool results are UTC; a "local" field or "utcOffset" gives this PC's local time. Tell the user local times.
            - A value marked "unavailable" was not measured: say so, and never treat it as 0. A result with "error" failed: say what could not be read.
            - You cannot change anything on this PC. Never claim you did something. To propose an action, call suggest_action; the user decides by clicking its button. To end a process, first get its pid and createTime from get_top_processes.
            - Never suggest ending Windows system processes (System, csrss, wininit, services, smss, lsass, winlogon, svchost).
            - Keep answers short and practical: the likely cause first, then what to do. Use a short list when it helps.
            - Paths under the user's folder appear as %USERPROFILE%; the computer name, user name and network addresses are removed for privacy.
            """;

        /// <summary>
        /// Sent after the last allowed tool round, with no tools offered, so the model answers
        /// with what it has instead of asking for more.
        /// </summary>
        internal const string ToolLimitReached =
            "You have used every tool call allowed for this question. Answer now with the data you already have, and say briefly what you could not check.";

        /// <summary>
        /// Appended to the question in limited mode (an endpoint that cannot call tools): a
        /// snapshot of the PC so the model still answers from real numbers.
        /// </summary>
        public static string LimitedModeContext(JsonNode liveSummary) =>
            "(Limited mode: this AI endpoint cannot call MicaStats tools, so here is a snapshot of the PC taken just now, as JSON. " +
            "Answer from it, and say so when it does not cover the question.)\n" + ToolJson.ToText(liveSummary);
    }
}
```

- [ ] **Step 7: Add the error texts**

Create `Services/Ai/AiErrorText.cs`:

```csharp
using System;
using System.ClientModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Anthropic.Exceptions;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Turns a provider failure into one sentence for the Ask window. Both SDKs throw their own
    /// types (<see cref="AnthropicApiException"/> with the HTTP status and body,
    /// <see cref="ClientResultException"/> for OpenAI-compatible endpoints), so the status and
    /// the server's own message are read from whichever arrived.
    /// </summary>
    public static class AiErrorText
    {
        /// <summary>Shown for a 401.</summary>
        internal const string KeyRejected = "The key was rejected. Check it in Settings > AI.";

        /// <summary>Shown when nothing answered in time.</summary>
        internal const string TimedOut = "The AI service did not answer within 60 seconds. Try again.";

        /// <summary>Shown when the endpoint could not be reached at all.</summary>
        internal const string Unreachable = "Could not reach the AI service. Check the network, or the base URL in Settings > AI, and try again.";

        /// <summary>Shown for 429, 503 and 529 after the SDK's own retries.</summary>
        internal const string Busy = "The AI service is busy or rate-limited. Wait a minute and try again.";

        private const int MaxServerMessage = 300;

        // A server saying it cannot do tool calls: Ollama "does not support tools", vLLM
        // "tool choice requires --enable-auto-tool-choice", llama.cpp "tools param requires --jinja".
        private static readonly Regex ToolsRejected = new(
            "\\btools?\\b.*\\b(support|supported|enable|enabled|requires?|not allowed|unrecognized|unknown)\\b|" +
            "\\b(support|supported|enable|enabled)\\b.*\\btools?\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        /// <summary>A user-facing sentence for <paramref name="ex"/>; never the raw stack or type name.</summary>
        public static string Describe(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            if (IsTimeout(ex)) return TimedOut;

            int status = StatusOf(ex);
            if (status == 0)
            {
                if (ex is HttpRequestException || ex is AnthropicIOException || ex is ClientResultException)
                    return Unreachable;
                return "The AI request failed: " + Shorten(FirstLine(ex.Message));
            }

            string server = ServerMessage(ex);
            return status switch
            {
                401 => KeyRejected,
                403 => "The AI service refused the request (403). Check that this key may use this model.",
                429 or 503 or 529 => Busy,
                _ => "The AI service said: " + (server.Length > 0 ? server : "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        }

        /// <summary>
        /// True when an endpoint rejected the request because it cannot call tools (some local
        /// models), which switches the assistant to limited mode instead of failing.
        /// </summary>
        internal static bool IsToolsUnsupported(Exception ex)
        {
            if (ex is OperationCanceledException || IsTimeout(ex)) return false;
            int status = StatusOf(ex);
            if (status is 401 or 403 or 429 || status >= 502) return false;
            if (status == 0 && (ex is HttpRequestException || ex is AnthropicIOException)) return false;
            string text = ServerMessage(ex);
            return ToolsRejected.IsMatch(text.Length > 0 ? text : ex.Message);
        }

        private static bool IsTimeout(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is TimeoutException || e is TaskCanceledException) return true;
            }
            return false;
        }

        private static int StatusOf(Exception ex) => ex switch
        {
            AnthropicApiException api => (int)api.StatusCode,
            ClientResultException result => result.Status,
            HttpRequestException http when http.StatusCode.HasValue => (int)http.StatusCode.Value,
            _ => 0,
        };

        /// <summary>The server's own error message from the response body, or empty.</summary>
        private static string ServerMessage(Exception ex)
        {
            string? body = null;
            try
            {
                body = ex switch
                {
                    AnthropicApiException api => api.ResponseBody,
                    ClientResultException result => result.GetRawResponse()?.Content?.ToString(),
                    _ => null,
                };
            }
            catch (Exception readFailure) when (readFailure is InvalidOperationException or ObjectDisposedException)
            {
                body = null;
            }
            if (string.IsNullOrWhiteSpace(body)) return "";

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                // Anthropic and OpenAI both use {"error": {"message": "..."}}; some servers send {"error": "..."}.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String) return Shorten(error.GetString() ?? "");
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) &&
                        message.ValueKind == JsonValueKind.String)
                        return Shorten(message.GetString() ?? "");
                }
            }
            catch (JsonException)
            {
                // Not JSON: fall through to the raw text.
            }
            return Shorten(FirstLine(body));
        }

        private static string FirstLine(string text)
        {
            string trimmed = text.Trim();
            int newline = trimmed.IndexOf('\n');
            return (newline < 0 ? trimmed : trimmed[..newline]).Trim();
        }

        private static string Shorten(string text) =>
            text.Length <= MaxServerMessage ? text : text[..MaxServerMessage] + "...";
    }
}
```

- [ ] **Step 8: Add the provider factory**

Create `Services/Ai/AiProviderFactory.cs`. `ChatCompletionOptions.Patch` is experimental (`SCME0001`, already in `<NoWarn>` since Task 1).

```csharp
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Kil0bitSystemMonitor.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The chat client for the current settings, or the reason there is none. <see cref="IsClaude"/>
    /// tells the assistant it may mark the system prompt for Anthropic prompt caching.
    /// </summary>
    /// <param name="Client">The ready client, or null when <paramref name="Problem"/> says what is missing.</param>
    /// <param name="Problem">A sentence for the Ask window, e.g. "Add an API key in Settings > AI.".</param>
    /// <param name="IsClaude">True for the Claude provider.</param>
    public sealed record AiClientResult(IChatClient? Client, string? Problem, bool IsClaude);

    /// <summary>
    /// Builds the <see cref="IChatClient"/> for AI settings: Claude through the official
    /// Anthropic SDK, or any OpenAI-compatible endpoint (OpenAI, Azure, OpenRouter, Ollama,
    /// LM Studio). Built per question, so a provider change applies from the next question.
    /// </summary>
    public static class AiProviderFactory
    {
        /// <summary>No reply within this time fails the request.</summary>
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Answer length cap, sent as the default output limit.</summary>
        internal const int MaxOutputTokens = 2000;

        internal const string NoKey = "Add an API key in Settings > AI.";
        internal const string NoModel = "Choose a model in Settings > AI.";
        internal const string BadUrl = "Enter a valid http or https base URL in Settings > AI.";

        /// <summary>
        /// Creates the client for <paramref name="config"/>. <paramref name="handler"/> replaces
        /// the network for tests (it is never disposed here); the app passes null.
        /// </summary>
        public static AiClientResult Create(AppConfig config, SecretStore secrets, HttpMessageHandler? handler = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(secrets);

            if (config.AiProvider == AiProviders.OpenAiCompatible) return CreateCompatible(config, secrets, handler);

            string? key = secrets.Get(SecretNames.ClaudeKey);
            if (string.IsNullOrWhiteSpace(key)) return new AiClientResult(null, NoKey, IsClaude: true);

            var anthropic = new AnthropicClient
            {
                ApiKey = key.Trim(),
                HttpClient = NewHttpClient(handler),
                // 429 and 529 are retried twice by the SDK before the Ask window says "busy".
                MaxRetries = 2,
            };
            IChatClient client = anthropic.AsIChatClient(config.AiClaudeModel, MaxOutputTokens);
            return new AiClientResult(client, null, IsClaude: true);
        }

        private static AiClientResult CreateCompatible(AppConfig config, SecretStore secrets, HttpMessageHandler? handler)
        {
            string model = (config.AiCompatibleModel ?? "").Trim();
            if (model.Length == 0) return new AiClientResult(null, NoModel, IsClaude: false);

            if (!Uri.TryCreate((config.AiCompatibleBaseUrl ?? "").Trim(), UriKind.Absolute, out Uri? endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                return new AiClientResult(null, BadUrl, IsClaude: false);

            // The key is optional (a local Ollama has none), but the SDK rejects an empty one.
            string? key = secrets.Get(SecretNames.CompatibleKey);
            var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "none" : key.Trim());
            var openAi = new OpenAIClient(credential, new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(NewHttpClient(handler)),
            });

            IChatClient client = openAi.GetChatClient(model).AsIChatClient();
            // OpenAI itself wants max_completion_tokens; most other servers only know max_tokens.
            if (!IsOpenAiHost(endpoint)) client = new MaxTokensChatClient(client);
            return new AiClientResult(client, null, IsClaude: false);
        }

        private static bool IsOpenAiHost(Uri endpoint) =>
            endpoint.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith("openai.azure.com", StringComparison.OrdinalIgnoreCase);

        private static HttpClient NewHttpClient(HttpMessageHandler? handler) =>
            new(handler ?? new SocketsHttpHandler(), disposeHandler: handler == null) { Timeout = RequestTimeout };
    }

    /// <summary>
    /// Sends the output limit as <c>max_tokens</c> instead of <c>max_completion_tokens</c>:
    /// Microsoft.Extensions.AI.OpenAI writes the newer name, which older OpenAI-compatible
    /// servers ignore, leaving answers unbounded.
    /// </summary>
    internal sealed class MaxTokensChatClient : DelegatingChatClient
    {
        public MaxTokensChatClient(IChatClient inner) : base(inner) { }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, Patch(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, Patch(options), cancellationToken);

        private static ChatOptions? Patch(ChatOptions? options)
        {
            if (options?.MaxOutputTokens is not int max || options.RawRepresentationFactory != null) return options;
            ChatOptions patched = options.Clone();
            patched.MaxOutputTokens = null;
            patched.RawRepresentationFactory = _ =>
            {
                var raw = new OpenAI.Chat.ChatCompletionOptions();
                raw.Patch.Set("$.max_tokens"u8, max);
                return raw;
            };
            return patched;
        }
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiUsageMeterTests|FullyQualifiedName~AiErrorTextTests|FullyQualifiedName~AiProviderFactoryTests"`
Expected: 22 passed, 0 failed. The provider tests talk only to `ScriptedHttpHandler`; the Anthropic client never retries a 401, so each rejected-key test sends one request.

- [ ] **Step 10: Whole suite, then commit the building blocks**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (978 + 22 = 1000).

```bash
git add Services/Ai/SuggestedAction.cs Services/Ai/AiConversation.cs Services/Ai/UsageMeter.cs Services/Ai/AiPrompts.cs Services/Ai/AiErrorText.cs Services/Ai/AiProviderFactory.cs tests/Kil0bitSystemMonitor.Tests/AiScriptedHttpHandler.cs tests/Kil0bitSystemMonitor.Tests/AiUsageMeterTests.cs tests/Kil0bitSystemMonitor.Tests/AiErrorTextTests.cs tests/Kil0bitSystemMonitor.Tests/AiProviderFactoryTests.cs
git commit -F - <<'EOF'
feat(ai): usage meter, prompts, provider factory and error text

The building blocks of the assistant: a daily question count that resets
at local midnight, the system prompt, a factory that builds the Claude or
OpenAI-compatible chat client from settings (60 s timeout, max_tokens for
servers that do not know the newer name) and one-sentence error texts for
rejected keys, rate limits, timeouts and unreachable endpoints.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

- [ ] **Step 11: Write the failing assistant tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiScriptedChatClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A fake model: answers each request with the next scripted step (a tool call, text, an
    /// exception, or waiting until cancelled), or with <see cref="Otherwise"/> once the script
    /// runs out. Records every request it receives. Text is streamed in two chunks.
    /// </summary>
    internal sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<Request, CancellationToken, Task<ChatMessage>>> _script = new();
        private int _calls;

        /// <summary>One request as the model saw it.</summary>
        public sealed record Request(List<ChatMessage> Messages, ChatOptions? Options)
        {
            public List<string> ToolNames => Options?.Tools?.Select(t => t.Name).ToList() ?? new List<string>();
        }

        public List<Request> Requests { get; } = new();

        /// <summary>The answer once the script is used up; by default the text "Done.".</summary>
        public Func<Request, ChatMessage> Otherwise { get; set; } = _ => new ChatMessage(ChatRole.Assistant, "Done.");

        public ScriptedChatClient Reply(string text)
        {
            _script.Enqueue((_, _) => Task.FromResult(new ChatMessage(ChatRole.Assistant, text)));
            return this;
        }

        public ScriptedChatClient Call(string tool, Dictionary<string, object?>? args = null)
        {
            _script.Enqueue((_, _) => Task.FromResult(CallMessage(tool, args)));
            return this;
        }

        public ScriptedChatClient Fail(Exception ex)
        {
            _script.Enqueue((_, _) => Task.FromException<ChatMessage>(ex));
            return this;
        }

        public ScriptedChatClient Hang()
        {
            _script.Enqueue(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ChatMessage(ChatRole.Assistant, "never");
            });
            return this;
        }

        /// <summary>An assistant message asking for one tool call with a fresh call id.</summary>
        public ChatMessage CallMessage(string tool, Dictionary<string, object?>? args = null)
        {
            string id = "call_" + Interlocked.Increment(ref _calls).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, tool, args ?? new Dictionary<string, object?>())]);
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                         CancellationToken cancellationToken = default) =>
            new ChatResponse(await NextAsync(messages, options, cancellationToken));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatMessage reply = await NextAsync(messages, options, cancellationToken);
            if (reply.Contents.OfType<FunctionCallContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, reply.Contents.ToList());
                yield break;
            }
            string text = reply.Text;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[..(text.Length / 2)]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[(text.Length / 2)..]);
        }

        private async Task<ChatMessage> NextAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
        {
            var request = new Request(messages.ToList(), options);
            Requests.Add(request);
            await Task.Yield();
            return _script.Count > 0 ? await _script.Dequeue()(request, ct) : Otherwise(request);
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/AiAssistantTests.cs`:

```csharp
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiAssistantTests : IDisposable
    {
        private readonly AiTestEnv _env = new();
        private readonly FakeMicaData _data = new();
        private readonly ScriptedChatClient _model = new();
        private readonly AiConversation _conversation = new();
        private readonly UsageMeter _usage;
        private int _limit = 100;

        public AiAssistantTests()
        {
            _usage = new UsageMeter(_env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));
        }

        public void Dispose() => _env.Dispose();

        private MicaTools Tools() => new(_data, new Redactor(@"C:\Users\alice", "alice", "DESK-7"));

        private AiAssistant Assistant(bool isClaude = false, IChatClient? client = null) =>
            new(client ?? _model, isClaude, Tools(), _usage, new AiAssistantOptions { DailyLimit = () => _limit });

        private async Task<List<AssistantUpdate>> AskAsync(AiAssistant assistant, string question, CancellationToken ct = default)
        {
            var updates = new List<AssistantUpdate>();
            await foreach (AssistantUpdate update in assistant.AskAsync(_conversation, question, ct)) updates.Add(update);
            // Every question ends with exactly one Done, whatever happened before it.
            Assert.Equal(AssistantUpdateKind.Done, updates[^1].Kind);
            Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Done);
            return updates;
        }

        private static string TextOf(IEnumerable<AssistantUpdate> updates) =>
            string.Concat(updates.Where(u => u.Kind == AssistantUpdateKind.Text).Select(u => u.Text));

        private static IEnumerable<AIContent> Contents(ScriptedChatClient.Request request) =>
            request.Messages.SelectMany(m => m.Contents);

        // ----- the tool loop -----------------------------------------------------------------

        [Fact]
        public async Task A_question_runs_a_tool_and_streams_the_answer()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("CPU is fine.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "How is my CPU?");

            AssistantUpdate used = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            Assert.Equal(ToolNames.GetLiveStatus, used.ToolName);
            Assert.Equal("{}", used.ToolArgs);
            Assert.Equal("CPU is fine.", TextOf(updates));
            Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant },
                _conversation.Messages.Select(m => m.Role));
            Assert.Equal("How is my CPU?", _conversation.Messages[0].Text);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task Tool_results_reach_the_model_as_json_not_as_a_quoted_string()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("Fine.");

            await AskAsync(Assistant(), "CPU?");

            FunctionResultContent result = Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single();
            JsonElement json = Assert.IsType<JsonElement>(result.Result);
            Assert.Equal(JsonValueKind.Object, json.ValueKind);
            Assert.Equal(12.3, json.GetProperty("cpu").GetProperty("usagePercent").GetDouble());
        }

        [Fact]
        public async Task Every_request_carries_the_system_prompt_the_tools_and_the_output_cap()
        {
            _model.Reply("Hello.");

            await AskAsync(Assistant(), "Hi");

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(ChatRole.System, request.Messages[0].Role);
            Assert.Equal(AiPrompts.System, request.Messages[0].Text);
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction), request.ToolNames);
            Assert.Equal(2000, request.Options!.MaxOutputTokens);
            Assert.DoesNotContain(_conversation.Messages, m => m.Role == ChatRole.System);
        }

        [Fact]
        public async Task The_previous_exchange_is_sent_with_the_next_question()
        {
            _model.Reply("First.").Reply("Second.");
            AiAssistant assistant = Assistant();

            await AskAsync(assistant, "One?");
            await AskAsync(assistant, "Two?");

            Assert.Equal(new[] { "", "One?", "First.", "Two?" },
                _model.Requests[1].Messages.Select(m => m.Role == ChatRole.System ? "" : m.Text));
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task After_eight_tool_rounds_the_model_must_answer_without_tools()
        {
            _model.Otherwise = request => request.ToolNames.Count > 0
                ? _model.CallMessage(ToolNames.GetLiveStatus)
                : new ChatMessage(ChatRole.Assistant, "Final answer.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Watch it closely");

            Assert.Equal(9, _model.Requests.Count);
            Assert.All(_model.Requests.Take(8), r => Assert.Equal(10, r.ToolNames.Count));
            ScriptedChatClient.Request last = _model.Requests[8];
            Assert.Empty(last.ToolNames);
            Assert.DoesNotContain(Contents(last), c => c is FunctionCallContent or FunctionResultContent);
            Assert.Equal(AiPrompts.ToolLimitReached, last.Messages[^1].Text);
            Assert.Equal(8, _data.LatestCalls);
            Assert.Equal(8, updates.Count(u => u.Kind == AssistantUpdateKind.ToolUsed));
            Assert.Equal("Final answer.", TextOf(updates));
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task A_model_that_never_stops_asking_for_tools_still_ends_with_an_answer()
        {
            _model.Otherwise = _ => _model.CallMessage(ToolNames.GetLiveStatus);

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Loop");

            Assert.Equal(AiAssistant.NoAnswer, TextOf(updates));
            var answered = new HashSet<string>(_conversation.Messages.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().Select(r => r.CallId));
            Assert.All(_conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
                call => Assert.Contains(call.CallId, answered));
            Assert.Equal(AiAssistant.NoAnswer, _conversation.Messages[^1].Text);
        }

        // ----- limits and failures -----------------------------------------------------------

        [Fact]
        public async Task The_daily_limit_stops_a_question_before_any_request()
        {
            _limit = 1;
            _model.Reply("One.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "First");

            List<AssistantUpdate> updates = await AskAsync(assistant, "Second");

            AssistantUpdate error = updates[0];
            Assert.Equal(AssistantUpdateKind.Error, error.Kind);
            Assert.Contains("daily limit set in Settings > AI", error.Text);
            Assert.Equal(2, updates.Count);
            Assert.Single(_model.Requests);
            Assert.Equal(new[] { "First", "One." }, _conversation.Messages.Select(m => m.Text));
        }

        [Fact]
        public async Task A_provider_failure_is_one_error_and_the_question_is_not_kept()
        {
            _model.Fail(new HttpRequestException("No such host is known."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Equal(new[] { AssistantUpdateKind.Error, AssistantUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.Unreachable, updates[0].Text);
            Assert.Empty(_conversation.Messages);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task A_failure_after_a_tool_round_leaves_nothing_behind()
        {
            _model.Call(ToolNames.GetLiveStatus).Fail(new HttpRequestException("Connection reset."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Contains(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task Cancelling_ends_quietly_and_forgets_the_question()
        {
            _model.Hang();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Slow question", cts.Token);

            Assert.Single(updates);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task An_empty_question_is_refused_without_counting()
        {
            List<AssistantUpdate> updates = await AskAsync(Assistant(), "   ");

            Assert.Equal(AiAssistant.EmptyQuestion, updates[0].Text);
            Assert.Equal(0, _usage.UsedToday);
            Assert.Empty(_model.Requests);
        }

        // ----- suggest_action ----------------------------------------------------------------

        [Fact]
        public async Task Suggest_action_records_a_button_and_does_nothing_else()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?>
            {
                ["kind"] = "end_process",
                ["pid"] = 4242,
                ["createTime"] = 134037504000000000L,
                ["processName"] = "chrome.exe",
                ["reason"] = "It uses most of the CPU.",
            }).Reply("You could end chrome.exe.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "What is slowing me down?");

            SuggestedAction action = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Suggestion).Suggestion!;
            Assert.Equal(new SuggestedAction(SuggestedActionKind.EndProcess, "End chrome.exe", "It uses most of the CPU.",
                4242, 134037504000000000L, "chrome.exe"), action);
            Assert.Equal(action, Assert.Single(_conversation.Suggestions));
            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.True(result.GetProperty("recorded").GetBoolean());
            Assert.Equal(0, _data.LatestCalls);
            Assert.Null(_data.LastTopRequest);
        }

        [Fact]
        public async Task A_suggestion_without_its_target_is_refused_to_the_model()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "end_process", ["reason"] = "Busy." })
                  .Reply("I could not suggest that.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "End it");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Suggestion);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.Contains("pid and createTime", result.GetProperty("error").GetString());
        }

        [Fact]
        public async Task Suggestions_are_cleared_when_the_next_question_starts()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "open_diagnostics", ["reason"] = "See the reports." })
                  .Reply("Open Diagnostics.")
                  .Reply("Nothing else.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "Where are my reports?");
            Assert.Equal("Open Diagnostics", Assert.Single(_conversation.Suggestions).Label);

            await AskAsync(assistant, "Thanks");

            Assert.Empty(_conversation.Suggestions);
        }

        // ----- limited mode ------------------------------------------------------------------

        [Fact]
        public async Task An_endpoint_without_tools_answers_in_limited_mode_from_a_live_snapshot()
        {
            _data.Processes.Add(new ProcessInfo("chrome.exe", null, 4242, 1, 80f, 900, 0));
            _model.Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: CPU 12%.")
                  .Reply("Still limited.");
            AiAssistant assistant = Assistant();

            List<AssistantUpdate> first = await AskAsync(assistant, "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, first[0].Kind);
            Assert.Equal("From the snapshot: CPU 12%.", TextOf(first));
            ScriptedChatClient.Request retry = _model.Requests[1];
            Assert.Empty(retry.ToolNames);
            string sent = retry.Messages[^1].Text;
            Assert.StartsWith("How is my PC?", sent);
            Assert.Contains("Limited mode", sent);
            Assert.Contains("usagePercent", sent);
            Assert.Contains("chrome.exe", sent);
            Assert.Equal(new[] { "How is my PC?", "From the snapshot: CPU 12%." }, _conversation.Messages.Select(m => m.Text));
            Assert.Equal(1, _usage.UsedToday);

            List<AssistantUpdate> second = await AskAsync(assistant, "And now?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, second[0].Kind);
            Assert.Equal(3, _model.Requests.Count);
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task Claude_never_falls_back_to_limited_mode()
        {
            _model.Fail(new ClientResultException("tools are not supported"));

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true), "CPU?");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.LimitedMode);
            Assert.Equal(AssistantUpdateKind.Error, updates[0].Kind);
            Assert.Empty(_conversation.Messages);
        }

        // ----- through the real SDKs, offline ------------------------------------------------

        [Fact]
        public async Task Claude_runs_the_tool_loop_with_a_cached_system_prompt()
        {
            var handler = new ScriptedHttpHandler(sent => (HttpStatusCode.OK, "text/event-stream",
                sent.Body.Contains("tool_result", StringComparison.Ordinal)
                    ? ScriptedHttpHandler.ClaudeStreamText
                    : ScriptedHttpHandler.ClaudeStreamToolUse));
            var secrets = new SecretStore(_env.PathOf("secrets.bin"), _ => { });
            secrets.Set(SecretNames.ClaudeKey, "sk-ant-test");
            AiClientResult claude = AiProviderFactory.Create(new AppConfig(), secrets, handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true, client: claude.Client), "How is my CPU?");

            Assert.Equal(ToolNames.GetLiveStatus, Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed).ToolName);
            Assert.Equal("Streamed ok", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
            JsonNode system = first["system"]![0]!;
            Assert.Equal(AiPrompts.System, system["text"]!.GetValue<string>());
            Assert.Equal("1h", system["cache_control"]!["ttl"]!.GetValue<string>());
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction),
                first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
            Assert.True(first["stream"]!.GetValue<bool>());
            JsonNode toolResult = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray()
                .SelectMany(m => m!["content"]!.AsArray())
                .First(c => c!["type"]!.GetValue<string>() == "tool_result")!;
            JsonNode sentResult = JsonNode.Parse(toolResult["content"]!.GetValue<string>())!;
            Assert.Equal(12.3, sentResult["cpu"]!["usagePercent"]!.GetValue<double>());
        }

        [Fact]
        public async Task A_local_model_without_tools_answers_in_limited_mode_through_the_real_sdk()
        {
            var handler = new ScriptedHttpHandler(sent => JsonNode.Parse(sent.Body)!["tools"] != null
                ? (HttpStatusCode.BadRequest, "application/json", ScriptedHttpHandler.OllamaNoTools)
                : (HttpStatusCode.OK, "text/event-stream", ScriptedHttpHandler.OpenAiStreamText));
            var config = new AppConfig
            {
                AiProvider = AiProviders.OpenAiCompatible,
                AiCompatibleBaseUrl = "http://localhost:11434/v1",
                AiCompatibleModel = "gemma:2b",
            };
            AiClientResult local = AiProviderFactory.Create(config, new SecretStore(_env.PathOf("secrets.bin"), _ => { }), handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(client: local.Client), "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, updates[0].Kind);
            Assert.Equal("Streamed", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode retry = JsonNode.Parse(handler.Requests[1].Body)!;
            Assert.Null(retry["tools"]);
            Assert.Equal(2000, retry["max_tokens"]!.GetValue<int>());
            string question = retry["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            Assert.StartsWith("How is my PC?", question, StringComparison.Ordinal);
            Assert.Contains("Limited mode", question, StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 12: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiAssistantTests"`
Expected: build FAILS with CS0246 — `The type or namespace name 'AiAssistant' could not be found` (and `AiAssistantOptions`).

- [ ] **Step 13: Add Claude prompt caching**

Create `Services/Ai/ClaudeCache.cs`. It is the only file that imports `Anthropic.Models.Messages`, whose `Type` would otherwise clash with `System.Type`.

```csharp
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Anthropic prompt caching for the assistant, kept in its own file because
    /// <c>Anthropic.Models.Messages</c> declares names such as <c>Type</c> that clash with
    /// <c>System</c> wherever both are imported.
    ///
    /// <para>
    /// One breakpoint at the end of the system prompt caches the tool list too: the API caches
    /// the prefix tools, then system. A second breakpoint on the tools is avoided on purpose,
    /// because the API rejects a request whose later breakpoint has a longer TTL than an earlier one.
    /// </para>
    /// </summary>
    internal static class ClaudeCache
    {
        /// <summary>The system message with a one-hour cache breakpoint at its end.</summary>
        public static ChatMessage SystemMessage(string prompt) =>
            new(ChatRole.System, [new TextContent(prompt).WithCacheControl(Ttl.Ttl1h)]);
    }
}
```

- [ ] **Step 14: Add the tools as AI functions**

Create `Services/Ai/AiToolFunctions.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The assistant's tools as <see cref="AIFunction"/>s: the nine read-only
    /// <see cref="MicaTools"/> plus the in-app <c>suggest_action</c>. Each returns a
    /// <see cref="JsonElement"/>, so a result reaches the model as JSON rather than as a
    /// JSON-quoted string, and the parameter descriptions become the schema the model reads.
    /// </summary>
    internal static class AiToolFunctions
    {
        /// <summary>Most suggested actions one answer may carry.</summary>
        public const int MaxSuggestions = 3;

        /// <summary>The nine read-only tools, in <see cref="ToolNames.ReadOnly"/> order.</summary>
        public static IReadOnlyList<AIFunction> ReadOnly(MicaTools tools)
        {
            var t = new Target(tools);
            return new[]
            {
                AIFunctionFactory.Create(t.GetLiveStatus, Options(ToolNames.GetLiveStatus,
                    "Current readings of this PC: CPU (total, per-core summary, temperature, clock), memory, GPU, disks " +
                    "(free space, activity), network rates, battery and sensors, plus min/avg/max over the last two minutes.")),
                AIFunctionFactory.Create(t.GetHistory, Options(ToolNames.GetHistory,
                    "Recorded history of one metric between two times: one row per minute for the last 24 hours, one per " +
                    "5 minutes up to 7 days, averaged down to maxPoints. Recorded only while Keep 7 days of history is on. " +
                    "A missing field means the reading was unavailable in that interval (never 0).")),
                AIFunctionFactory.Create(t.GetTopProcesses, Options(ToolNames.GetTopProcesses,
                    "The busiest processes right now, measured over about 2 seconds: name, path, pid, createTime, CPU %, " +
                    "memory MB and disk KB/s. pid and createTime identify a process for suggest_action.")),
                AIFunctionFactory.Create(t.ListSlowdownReports, Options(ToolNames.ListSlowdownReports,
                    "Slowdown reports MicaStats saved when the PC struggled (or the user recorded one), newest first: id, " +
                    "time, trigger and a one-line summary.")),
                AIFunctionFactory.Create(t.GetSlowdownReport, Options(ToolNames.GetSlowdownReport,
                    "The text of one slowdown report: a per-second timeline of CPU, memory, disk and the busiest process, " +
                    "then the worst offenders. The last timeline rows are the moment the threshold was crossed.")),
                AIFunctionFactory.Create(t.ListAlerts, Options(ToolNames.ListAlerts,
                    "The alert rules (threshold, sustain time, on or off) and the alerts raised since MicaStats started, newest first.")),
                AIFunctionFactory.Create(t.GetHardware, Options(ToolNames.GetHardware,
                    "Hardware of this PC as a text report: CPU, mainboard, memory modules, graphics, storage and Windows version.")),
                AIFunctionFactory.Create(t.GetBattery, Options(ToolNames.GetBattery,
                    "Battery charge, power draw, time left, health (full versus design capacity), wear and cycle count.")),
                AIFunctionFactory.Create(t.GetBootSummary, Options(ToolNames.GetBootSummary,
                    "Recent Windows boot durations (newest first), the trend (seconds the latest boot was slower than the " +
                    "earlier average), what slowed the last boot, and the programs set to start at sign-in.")),
            };
        }

        /// <summary>
        /// The in-app <c>suggest_action</c> tool. It validates the arguments and hands the
        /// suggestion to <paramref name="record"/>, which returns null when it was kept or a
        /// sentence saying why not. It never runs anything.
        /// </summary>
        public static AIFunction SuggestAction(Func<SuggestedAction, string?> record) =>
            AIFunctionFactory.Create(new Suggester(record).Suggest, Options(ToolNames.SuggestAction,
                "Offer the user a button for an action MicaStats can do: end_process, record_slowdown, open_diagnostics " +
                "or open_process_window. Nothing happens until the user clicks it, and MicaStats checks the target again " +
                "first. Use it only when the action clearly helps, at most " +
                MaxSuggestions.ToString(CultureInfo.InvariantCulture) + " per answer."));

        /// <summary>
        /// Checks and completes a suggestion: a known kind, a reason, and for end_process a pid
        /// and createTime. Null with <paramref name="error"/> set when something is missing.
        /// </summary>
        internal static SuggestedAction? Build(string? kind, string? reason, string? label, int? pid, long? createTime,
                                               string? processName, out string? error)
        {
            error = null;
            SuggestedActionKind? parsed = (kind ?? "").Trim().ToLowerInvariant() switch
            {
                "end_process" => SuggestedActionKind.EndProcess,
                "record_slowdown" => SuggestedActionKind.RecordSlowdown,
                "open_diagnostics" => SuggestedActionKind.OpenDiagnostics,
                "open_process_window" => SuggestedActionKind.OpenProcessWindow,
                _ => null,
            };
            if (parsed is not SuggestedActionKind k)
            {
                error = "Unknown kind '" + kind + "'. Use end_process, record_slowdown, open_diagnostics or open_process_window.";
                return null;
            }

            string why = Clip((reason ?? "").Trim(), 300);
            if (why.Length == 0)
            {
                error = "Give a short reason the user can read.";
                return null;
            }

            string? text = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            if (k == SuggestedActionKind.EndProcess)
            {
                if (pid is not > 0 || createTime is not > 0)
                {
                    error = "end_process needs the pid and createTime of the process, from get_top_processes.";
                    return null;
                }
                string name = (processName ?? "").Trim();
                text ??= "End " + (name.Length > 0 ? name : "PID " + pid.Value.ToString(CultureInfo.InvariantCulture));
                return new SuggestedAction(k, Clip(text, 60), why, pid, createTime, name.Length > 0 ? name : null);
            }

            text ??= k switch
            {
                SuggestedActionKind.RecordSlowdown => "Record a slowdown now",
                SuggestedActionKind.OpenDiagnostics => "Open Diagnostics",
                _ => "Open the process list",
            };
            return new SuggestedAction(k, Clip(text, 60), why);
        }

        /// <summary>A tool result as the <see cref="JsonElement"/> the function-calling loop sends on.</summary>
        internal static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node, ToolJson.TextOptions);

        private static AIFunctionFactoryOptions Options(string name, string description) =>
            new() { Name = name, Description = description };

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

        /// <summary>The read-only tools as instance methods, so the parameter descriptions become the schema.</summary>
        private sealed class Target
        {
            private readonly MicaTools _tools;

            public Target(MicaTools tools) => _tools = tools;

            public async Task<JsonElement> GetLiveStatus(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetLiveStatusAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetHistory(
                [Description("One of: cpu, cpuTemp, ram, gpu, gpuTemp, netUp, netDown, diskFree, diskActivity, battery, topProcesses, all.")] string metric,
                [Description("Start: now, a relative time such as -30m, -6h or -2d, or an ISO-8601 UTC time.")] string from,
                [Description("End, in the same forms; usually now.")] string to = "now",
                [Description("Most points to return, 1-500. Use fewer for long ranges, and at most 60 with all.")] int maxPoints = 200,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetHistoryAsync(metric, from, to, maxPoints, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetTopProcesses(
                [Description("Ranking: cpu, memory or disk.")] string by = "cpu",
                [Description("How many processes, 1-15.")] int count = 10,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetTopProcessesAsync(by, count, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> ListSlowdownReports(
                [Description("How many reports, 1-30, newest first.")] int limit = 10,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.ListSlowdownReportsAsync(limit, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetSlowdownReport(
                [Description("The report id from list_slowdown_reports, e.g. slowdown-20260930-140200.")] string id,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.GetSlowdownReportAsync(id, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> ListAlerts(
                [Description("Optional: only alerts raised after this time (-6h, -2d or ISO-8601 UTC).")] string? since = null,
                CancellationToken cancellationToken = default) =>
                ToElement(await _tools.ListAlertsAsync(since, cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetHardware(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetHardwareAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetBattery(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetBatteryAsync(cancellationToken).ConfigureAwait(false));

            public async Task<JsonElement> GetBootSummary(CancellationToken cancellationToken) =>
                ToElement(await _tools.GetBootSummaryAsync(cancellationToken).ConfigureAwait(false));
        }

        /// <summary>The suggest_action body, bound to one question's recorder.</summary>
        private sealed class Suggester
        {
            private readonly Func<SuggestedAction, string?> _record;

            public Suggester(Func<SuggestedAction, string?> record) => _record = record;

            public JsonElement Suggest(
                [Description("end_process, record_slowdown, open_diagnostics or open_process_window.")] string kind,
                [Description("One short sentence in the user's language: why this helps.")] string reason,
                [Description("Button text in the user's language, e.g. End chrome.exe. Optional.")] string? label = null,
                [Description("end_process only: the pid from get_top_processes.")] int? pid = null,
                [Description("end_process only: the createTime from get_top_processes.")] long? createTime = null,
                [Description("end_process only: the process name.")] string? processName = null)
            {
                SuggestedAction? action = Build(kind, reason, label, pid, createTime, processName, out string? error);
                if (action == null) return ToElement(ToolJson.Error(error!));
                string? refused = _record(action);
                if (refused != null) return ToElement(ToolJson.Error(refused));
                return ToElement(new JsonObject
                {
                    ["recorded"] = true,
                    ["note"] = "The user now sees a button labelled '" + action.Label + "'. Nothing has been done; do not say it was.",
                });
            }
        }
    }
}
```

- [ ] **Step 15: Add the final-answer client and the history tidying**

Create `Services/Ai/FinalAnswerChatClient.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Sits under the function-invoking client and handles its last request. After
    /// <c>MaximumIterationsPerRequest</c> rounds that client sends one more request with no
    /// tools; the history then still holds tool calls and results, which some APIs reject
    /// without a tool list. This rewrites them as plain text and asks the model to answer now.
    /// Requests that offer tools pass through untouched.
    /// </summary>
    internal sealed class FinalAnswerChatClient : DelegatingChatClient
    {
        public FinalAnswerChatClient(IChatClient inner) : base(inner) { }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(Prepare(messages, options), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(Prepare(messages, options), options, cancellationToken);

        private static IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (options?.Tools is { Count: > 0 }) return messages;
            List<ChatMessage> flat = ToolHistory.Flatten(messages);
            flat.Add(new ChatMessage(ChatRole.User, AiPrompts.ToolLimitReached));
            return flat;
        }
    }

    /// <summary>Tidies tool calls in a conversation for requests and for keeping.</summary>
    internal static class ToolHistory
    {
        private const int MaxResultChars = 20_000;

        /// <summary>
        /// The messages worth keeping from one answer: text, tool calls that got a result, and
        /// the results. A call left without a result (the model asked after the last round) is
        /// dropped, because sending it back would fail the next question.
        /// </summary>
        public static List<ChatMessage> KeepAnswered(IEnumerable<ChatMessage> messages)
        {
            List<ChatMessage> list = messages.ToList();
            var answered = new HashSet<string>(list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId));
            var kept = new List<ChatMessage>();
            foreach (ChatMessage message in list)
            {
                var contents = new List<AIContent>();
                foreach (AIContent content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                        case FunctionCallContent call when answered.Contains(call.CallId):
                        case FunctionResultContent:
                            contents.Add(content);
                            break;
                    }
                }
                if (contents.Count > 0) kept.Add(new ChatMessage(message.Role, contents));
            }
            return kept;
        }

        /// <summary>
        /// The same conversation with every tool call and result written as text, for a request
        /// that offers no tools. System messages pass through unchanged (they may carry a cache
        /// breakpoint); tool results become user text.
        /// </summary>
        public static List<ChatMessage> Flatten(IEnumerable<ChatMessage> messages)
        {
            var names = new Dictionary<string, string>();
            var flat = new List<ChatMessage>();
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.System)
                {
                    flat.Add(message);
                    continue;
                }

                var parts = new List<string>();
                foreach (AIContent content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            parts.Add(text.Text);
                            break;
                        case FunctionCallContent call:
                            names[call.CallId] = call.Name;
                            parts.Add("[Called MicaStats tool " + call.Name + " with " + ArgsJson(call.Arguments) + "]");
                            break;
                        case FunctionResultContent result:
                            string name = names.TryGetValue(result.CallId, out string? n) ? n : "a MicaStats tool";
                            parts.Add("[Result of " + name + ": " + ResultText(result.Result) + "]");
                            break;
                    }
                }
                if (parts.Count == 0) continue;
                ChatRole role = message.Role == ChatRole.Tool ? ChatRole.User : message.Role;
                flat.Add(new ChatMessage(role, string.Join("\n", parts)));
            }
            return flat;
        }

        /// <summary>Tool arguments as compact JSON, <c>{}</c> when there are none.</summary>
        public static string ArgsJson(IDictionary<string, object?>? arguments) =>
            arguments == null || arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments, ToolJson.TextOptions);

        private static string ResultText(object? result)
        {
            string text = result switch
            {
                null => "null",
                JsonElement element => element.GetRawText(),
                string s => s,
                _ => JsonSerializer.Serialize(result, ToolJson.TextOptions),
            };
            return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "...";
        }
    }
}
```

- [ ] **Step 16: Add the assistant**

Create `Services/Ai/AiAssistant.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>Limits for one <see cref="AiAssistant"/>.</summary>
    public sealed class AiAssistantOptions
    {
        /// <summary>Tool rounds per question; after the last one the model must answer without tools.</summary>
        public int MaxToolRounds { get; init; } = 8;

        /// <summary>Output cap per request, in tokens.</summary>
        public int MaxOutputTokens { get; init; } = 2000;

        /// <summary>Questions allowed per local day; read at each question so a Settings change applies at once.</summary>
        public Func<int> DailyLimit { get; init; } = () => 100;
    }

    /// <summary>
    /// Answers one question at a time: adds the system prompt, offers the tools, runs the tool
    /// loop through <see cref="FunctionInvokingChatClient"/> and streams the answer as
    /// <see cref="AssistantUpdate"/>s.
    ///
    /// <para>
    /// <see cref="AskAsync"/> never throws for a provider failure or a cancellation: every
    /// question ends with exactly one <see cref="AssistantUpdateKind.Done"/>, after an
    /// <see cref="AssistantUpdateKind.Error"/> when it failed. A failed or cancelled question
    /// is taken back out of the conversation; the daily count keeps it. An endpoint that
    /// rejects tools (some local models) gets the question again without tools, with a live
    /// snapshot attached (<see cref="AssistantUpdateKind.LimitedMode"/>); Claude never does.
    /// </para>
    ///
    /// <para>
    /// The iterator resumes on the caller's context, so the conversation is changed on the
    /// thread that enumerates it (the UI thread in the Ask window).
    /// </para>
    /// </summary>
    public sealed class AiAssistant
    {
        internal const string EmptyQuestion = "Type a question first.";
        internal const string NoAnswer = "I could not finish an answer within the tool limit. Try a narrower question.";
        internal const string LimitedModeNote =
            "This AI endpoint cannot use MicaStats tools, so this answer comes from a snapshot of the PC taken now (limited mode).";

        private static readonly AssistantUpdate Done = new(AssistantUpdateKind.Done);

        private readonly IChatClient _client;
        private readonly IChatClient _toolClient;
        private readonly bool _isClaude;
        private readonly MicaTools _tools;
        private readonly UsageMeter _usage;
        private readonly AiAssistantOptions _options;
        private readonly IReadOnlyList<AIFunction> _readOnlyTools;
        private bool _toolsUnsupported;

        /// <summary>
        /// Creates the assistant over a provider client (<see cref="AiProviderFactory"/>);
        /// <paramref name="isClaude"/> turns on prompt caching and turns off the limited-mode fallback.
        /// </summary>
        public AiAssistant(IChatClient client, bool isClaude, MicaTools tools, UsageMeter usage, AiAssistantOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _usage = usage ?? throw new ArgumentNullException(nameof(usage));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _isClaude = isClaude;
            _readOnlyTools = AiToolFunctions.ReadOnly(tools);
            _toolClient = new ChatClientBuilder(new FinalAnswerChatClient(client))
                .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = Math.Max(1, options.MaxToolRounds))
                .Build();
        }

        /// <summary>
        /// Asks <paramref name="question"/> in <paramref name="conversation"/> and streams the
        /// answer. Counts one question against the daily limit before any request is made.
        /// </summary>
        public async IAsyncEnumerable<AssistantUpdate> AskAsync(AiConversation conversation, string question,
                                                                [EnumeratorCancellation] CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            conversation.Suggestions.Clear();

            string text = (question ?? "").Trim();
            if (text.Length == 0)
            {
                yield return new AssistantUpdate(AssistantUpdateKind.Error, EmptyQuestion);
                yield return Done;
                yield break;
            }

            int limit = Math.Max(1, _options.DailyLimit());
            if (!_usage.TryConsume(limit))
            {
                yield return new AssistantUpdate(AssistantUpdateKind.Error, LimitText(limit));
                yield return Done;
                yield break;
            }

            int start = conversation.Messages.Count;
            conversation.Messages.Add(new ChatMessage(ChatRole.User, text));

            if (!_toolsUnsupported)
            {
                var turn = new Turn();
                var updates = new List<ChatResponseUpdate>();
                var calls = new Dictionary<string, FunctionCallContent>();
                bool anyText = false;
                Exception? failure = null;

                IAsyncEnumerator<ChatResponseUpdate> stream = _toolClient
                    .GetStreamingResponseAsync(WithSystem(conversation.Messages), ToolOptions(turn), ct)
                    .GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        (bool moved, Exception? error) = await MoveAsync(stream);
                        if (error != null)
                        {
                            failure = error;
                            break;
                        }
                        if (!moved) break;

                        ChatResponseUpdate update = stream.Current;
                        updates.Add(update);
                        foreach (AIContent content in update.Contents)
                        {
                            if (content is FunctionCallContent call)
                            {
                                calls[call.CallId] = call;
                            }
                            else if (content is FunctionResultContent result &&
                                     calls.TryGetValue(result.CallId, out FunctionCallContent? ran) &&
                                     ran.Name != ToolNames.SuggestAction)
                            {
                                yield return new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: ran.Name,
                                                                 ToolArgs: ToolHistory.ArgsJson(ran.Arguments));
                            }
                        }
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            anyText = true;
                            yield return new AssistantUpdate(AssistantUpdateKind.Text, update.Text);
                        }
                        foreach (SuggestedAction action in turn.Drain())
                        {
                            conversation.Suggestions.Add(action);
                            yield return new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: action);
                        }
                    }
                }
                finally
                {
                    await DisposeQuietly(stream);
                }

                if (failure == null)
                {
                    conversation.Messages.AddRange(ToolHistory.KeepAnswered(updates.ToChatResponse().Messages));
                    if (!anyText)
                    {
                        conversation.Messages.Add(new ChatMessage(ChatRole.Assistant, NoAnswer));
                        yield return new AssistantUpdate(AssistantUpdateKind.Text, NoAnswer);
                    }
                    yield return Done;
                    yield break;
                }

                bool fallBack = !ct.IsCancellationRequested && !_isClaude && !anyText && AiErrorText.IsToolsUnsupported(failure);
                if (!fallBack)
                {
                    conversation.Suggestions.Clear();
                    conversation.Messages.RemoveRange(start, conversation.Messages.Count - start);
                    if (!ct.IsCancellationRequested)
                        yield return new AssistantUpdate(AssistantUpdateKind.Error, AiErrorText.Describe(failure));
                    yield return Done;
                    yield break;
                }
                _toolsUnsupported = true;
            }

            // Limited mode: the same question without tools, with a live snapshot attached.
            yield return new AssistantUpdate(AssistantUpdateKind.LimitedMode, LimitedModeNote);

            JsonNode? snapshot = await SnapshotAsync(ct);
            var answer = new StringBuilder();
            Exception? limitedFailure = null;
            if (snapshot != null)
            {
                List<ChatMessage> messages = LimitedMessages(conversation.Messages, text, snapshot);
                IAsyncEnumerator<ChatResponseUpdate> plain = _client
                    .GetStreamingResponseAsync(messages, new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens }, ct)
                    .GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        (bool moved, Exception? error) = await MoveAsync(plain);
                        if (error != null)
                        {
                            limitedFailure = error;
                            break;
                        }
                        if (!moved) break;
                        string piece = plain.Current.Text;
                        if (string.IsNullOrEmpty(piece)) continue;
                        answer.Append(piece);
                        yield return new AssistantUpdate(AssistantUpdateKind.Text, piece);
                    }
                }
                finally
                {
                    await DisposeQuietly(plain);
                }
            }

            if (snapshot == null || limitedFailure != null)
            {
                conversation.Messages.RemoveRange(start, conversation.Messages.Count - start);
                if (limitedFailure != null && !ct.IsCancellationRequested)
                    yield return new AssistantUpdate(AssistantUpdateKind.Error, AiErrorText.Describe(limitedFailure));
                yield return Done;
                yield break;
            }

            if (answer.Length == 0) answer.Append(NoAnswer);
            conversation.Messages.Add(new ChatMessage(ChatRole.Assistant, answer.ToString()));
            yield return Done;
        }

        private static string LimitText(int limit) =>
            "You have asked " + limit.ToString(CultureInfo.InvariantCulture) +
            " questions today, the daily limit set in Settings > AI. The count starts again at midnight.";

        private ChatMessage SystemMessage() => _isClaude
            ? ClaudeCache.SystemMessage(AiPrompts.System)
            : new ChatMessage(ChatRole.System, AiPrompts.System);

        private List<ChatMessage> WithSystem(List<ChatMessage> conversation)
        {
            var messages = new List<ChatMessage>(conversation.Count + 1) { SystemMessage() };
            messages.AddRange(conversation);
            return messages;
        }

        private ChatOptions ToolOptions(Turn turn)
        {
            var tools = new List<AITool>(_readOnlyTools.Count + 1);
            tools.AddRange(_readOnlyTools);
            tools.Add(AiToolFunctions.SuggestAction(turn.Record));
            return new ChatOptions { Tools = tools, MaxOutputTokens = _options.MaxOutputTokens };
        }

        /// <summary>System prompt, earlier turns as plain text, then the question with the snapshot appended.</summary>
        private List<ChatMessage> LimitedMessages(List<ChatMessage> conversation, string question, JsonNode snapshot)
        {
            var messages = new List<ChatMessage> { SystemMessage() };
            messages.AddRange(ToolHistory.Flatten(conversation.GetRange(0, conversation.Count - 1)));
            messages.Add(new ChatMessage(ChatRole.User, question + "\n\n" + AiPrompts.LimitedModeContext(snapshot)));
            return messages;
        }

        /// <summary>Live status plus the top five processes by CPU, or null when cancelled.</summary>
        private async Task<JsonNode?> SnapshotAsync(CancellationToken ct)
        {
            try
            {
                JsonNode status = await _tools.GetLiveStatusAsync(ct);
                JsonNode top = await _tools.GetTopProcessesAsync("cpu", 5, ct);
                return new JsonObject { ["status"] = status, ["topProcesses"] = top };
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static async Task<(bool Moved, Exception? Error)> MoveAsync(IAsyncEnumerator<ChatResponseUpdate> stream)
        {
            try
            {
                return (await stream.MoveNextAsync(), null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
        }

        private static async Task DisposeQuietly(IAsyncEnumerator<ChatResponseUpdate> stream)
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception)
            {
                // The request already failed or finished; a second error would only hide the first.
            }
        }

        /// <summary>The suggestions one question records, handed to the conversation as they arrive.</summary>
        private sealed class Turn
        {
            private readonly object _gate = new();
            private readonly List<SuggestedAction> _pending = new();
            private int _count;

            /// <summary>Keeps <paramref name="action"/>; returns why not when the answer already has the most allowed.</summary>
            public string? Record(SuggestedAction action)
            {
                lock (_gate)
                {
                    if (_count >= AiToolFunctions.MaxSuggestions)
                        return "At most " + AiToolFunctions.MaxSuggestions.ToString(CultureInfo.InvariantCulture) +
                               " suggestions per answer.";
                    _count++;
                    _pending.Add(action);
                    return null;
                }
            }

            public List<SuggestedAction> Drain()
            {
                lock (_gate)
                {
                    var drained = new List<SuggestedAction>(_pending);
                    _pending.Clear();
                    return drained;
                }
            }
        }
    }
}
```

- [ ] **Step 17: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiAssistantTests"`
Expected: 18 passed, 0 failed. `After_eight_tool_rounds_the_model_must_answer_without_tools` pins `FunctionInvokingChatClient`'s behaviour (8 requests with tools, then one without); the two `*_real_sdk` / `Claude_runs_*` tests run the real Anthropic and OpenAI clients against `ScriptedHttpHandler`, offline.

- [ ] **Step 18: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1000 + 18 = 1018).

```bash
git add Services/Ai/ClaudeCache.cs Services/Ai/AiToolFunctions.cs Services/Ai/FinalAnswerChatClient.cs Services/Ai/AiAssistant.cs tests/Kil0bitSystemMonitor.Tests/AiScriptedChatClient.cs tests/Kil0bitSystemMonitor.Tests/AiAssistantTests.cs
git commit -F - <<'EOF'
feat(ai): assistant with tool loop, limits, limited mode and suggested actions

AiAssistant offers the nine read-only tools and suggest_action to the
model, runs at most 8 tool rounds and then asks for an answer without
tools, streams text, tool use and suggestions as updates, counts one
question per Send against the daily limit and takes a failed or cancelled
question back out of the conversation. Claude gets a cached system prompt;
an endpoint that rejects tools is asked again with a live snapshot in
limited mode. suggest_action only records a button.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 9: Tool pipe (protocol, server with a user-only medium-label descriptor, client)

The `--mcp` bridge is a separate process, so it reaches the running MicaStats over a per-user named pipe. This task builds the length-prefixed JSON protocol, the server (only the current user's SID may connect, a medium integrity label lets an unelevated bridge reach an elevated MicaStats, the first-instance flag refuses a name someone else already serves, up to 4 clients at once) and a one-call-per-connection client with a 1 s connect wait and a per-call timeout. Everything is tested in-process on GUID-suffixed test pipe names, never the real one.

**Files:**
- Create: `Services/Ai/Mcp/ToolInvoker.cs`
- Create: `Services/Ai/Mcp/ToolPipeProtocol.cs`
- Create: `Services/Ai/Mcp/ToolPipeNative.cs`
- Create: `Services/Ai/Mcp/ToolPipeServer.cs`
- Create: `Services/Ai/Mcp/ToolPipeClient.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/McpPipeProtocolTests.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/McpPipeTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (framework only: `System.IO.Pipes`, `System.Text.Json.Nodes`, Win32 `CreateNamedPipeW` as proven in the spike's Item8).
- Produces (namespace `Kil0bitSystemMonitor.Services.Ai.Mcp`):
  - `public delegate Task<JsonNode> ToolInvoker(string tool, JsonObject? args, CancellationToken ct);`
  - `public static class ToolPipeProtocol { public const int Version = 1; public const int MaxFrameBytes = 1_048_576; public static string DefaultPipeName(); public static Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct); public static Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct); }` (null at end of stream; `InvalidDataException` on a bad or oversized frame; `WriteAsync` throws `InvalidDataException` and writes nothing over 1 MB)
  - `public sealed class ToolPipeServer : IDisposable { public ToolPipeServer(string pipeName, ToolInvoker invoke, Action<string>? warn = null); public const int MaxClients = 4; public bool IsRunning { get; } public void Start(); public void Stop(); }` (`Start` throws `IOException` "... is already in use by another process." when the name is taken)
  - `public sealed class ToolPipeUnavailableException : Exception { public ToolPipeUnavailableException(string message); }`
  - `public static class ToolPipeClient { public static Task<JsonNode> CallAsync(string pipeName, string tool, JsonObject? args, TimeSpan timeout, CancellationToken ct); }` (`ToolPipeUnavailableException` when nothing accepts within 1 s; `TimeoutException` "MicaStats did not answer within N s."; `InvalidOperationException` with the server sentence for `ok:false`, a version mismatch, or a broken/unreadable reply; `OperationCanceledException` when `ct` is cancelled)
  - `internal static class ToolPipeNative { internal static string SddlFor(SecurityIdentifier user); internal static SafePipeHandle Create(string pipeName, string sddl, bool firstInstance, int maxInstances); internal static string ReadSddl(SafeHandle handle, uint information); internal const uint DACL_SECURITY_INFORMATION, LABEL_SECURITY_INFORMATION; }`
  - Wire: request `{"v":1,"tool":"...","args":{...}}`; reply `{"v":1,"ok":true,"result":...}` or `{"v":1,"ok":false,"error":"..."}`.

- [ ] **Step 1: Write the failing protocol tests**

Create `tests/Kil0bitSystemMonitor.Tests/McpPipeProtocolTests.cs`:

```csharp
using System.Buffers.Binary;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The tool pipe frame: 4-byte little-endian length, then UTF-8 JSON, nothing over 1 MB.</summary>
public class McpPipeProtocolTests
{
    private static MemoryStream Raw(int declaredLength, byte[] body)
    {
        var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_message_is_written_as_a_little_endian_length_and_utf8_json()
    {
        using var stream = new MemoryStream();

        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["v"] = 1, ["tool"] = "get_history" }, CancellationToken.None);

        byte[] bytes = stream.ToArray();
        string json = "{\"v\":1,\"tool\":\"get_history\"}";
        Assert.Equal(json.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(json, Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
    }

    [Fact]
    public async Task Frames_round_trip_in_order_and_the_end_of_the_stream_reads_as_null()
    {
        using var stream = new MemoryStream();
        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["n"] = 1, ["name"] = "\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe" }, CancellationToken.None);
        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["n"] = 2 }, CancellationToken.None);
        stream.Position = 0;

        JsonObject? first = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);
        JsonObject? second = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);
        JsonObject? end = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(1, (int?)first!["n"]);
        Assert.Equal("\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe", (string?)first["name"]);
        Assert.Equal(2, (int?)second!["n"]);
        Assert.Null(end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ToolPipeProtocol.MaxFrameBytes + 1)]
    public async Task A_frame_length_outside_the_limits_is_refused_before_reading_the_body(int declared)
    {
        using MemoryStream stream = Raw(declared, Array.Empty<byte>());

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_cut_header_is_refused()
    {
        using var stream = new MemoryStream(new byte[] { 5, 0 });

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_cut_body_is_refused()
    {
        using MemoryStream stream = Raw(20, Encoding.UTF8.GetBytes("{\"v\":1}"));

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public async Task A_body_that_is_not_a_json_object_is_refused(string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using MemoryStream stream = Raw(bytes.Length, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_message_over_one_megabyte_is_refused_and_nothing_is_written()
    {
        using var stream = new MemoryStream();
        var huge = new JsonObject { ["text"] = new string('x', ToolPipeProtocol.MaxFrameBytes) };

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.WriteAsync(stream, huge, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void The_default_pipe_name_carries_the_current_user_sid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        Assert.Equal("MicaStats.Tools." + identity.User!.Value, ToolPipeProtocol.DefaultPipeName());
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpPipeProtocolTests"`
Expected: build error CS0246 - the type or namespace `ToolPipeProtocol` could not be found (and the namespace `Kil0bitSystemMonitor.Services.Ai.Mcp` does not exist yet).

- [ ] **Step 3: Create the invoker delegate**

Create `Services/Ai/Mcp/ToolInvoker.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Runs one read-only tool by name. The running app passes <c>MicaTools.InvokeAsync</c>; the
/// <c>--mcp</c> bridge passes a forwarder to the running app. Both the tool pipe server and the
/// MCP tool set take this shape, so neither knows which one is behind it.
/// </summary>
public delegate Task<JsonNode> ToolInvoker(string tool, JsonObject? args, CancellationToken ct);
```

- [ ] **Step 4: Create the protocol**

Create `Services/Ai/Mcp/ToolPipeProtocol.cs`:

```csharp
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The wire format between the <c>--mcp</c> bridge and the running MicaStats: each message is a
/// 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
///
/// <para>
/// A length prefix rather than one JSON object per line, so a reader always knows how much to
/// wait for and can refuse an oversized frame before allocating it. A request is
/// <c>{"v":1,"tool":"...","args":{...}}</c>; a reply is <c>{"v":1,"ok":true,"result":...}</c> or
/// <c>{"v":1,"ok":false,"error":"..."}</c>.
/// </para>
/// </summary>
public static class ToolPipeProtocol
{
    /// <summary>
    /// The protocol version both ends put in <c>"v"</c>. A bridge from one MicaStats version talking
    /// to a running app from another is refused with a clear error instead of guessing.
    /// </summary>
    public const int Version = 1;

    /// <summary>The largest frame either end writes or accepts (1 MB), so a bad peer cannot make the other allocate without limit.</summary>
    public const int MaxFrameBytes = 1_048_576;

    /// <summary>
    /// The pipe name for the current Windows user: <c>MicaStats.Tools.</c> plus the user's SID.
    /// The SID keeps two signed-in users apart (fast user switching, a terminal server); the
    /// pipe's security descriptor is what actually keeps everyone else out.
    /// </summary>
    public static string DefaultPipeName()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return "MicaStats.Tools." + (identity.User?.Value ?? "anonymous");
    }

    /// <summary>Writes one frame. Throws <see cref="InvalidDataException"/> (and writes nothing) when the message is over <see cref="MaxFrameBytes"/>.</summary>
    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (body.Length > MaxFrameBytes)
            throw new InvalidDataException("The message is " + body.Length.ToString(CultureInfo.InvariantCulture) +
                " bytes, over the tool pipe limit of " + MaxFrameBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");

        // Header and body in one write, so a reader never sees a header without its body behind it.
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Null when the stream ends cleanly before a new frame (the peer hung up);
    /// <see cref="InvalidDataException"/> for a frame that is cut short, empty, over
    /// <see cref="MaxFrameBytes"/>, not JSON, or not a JSON object.
    /// </summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = 0;
        while (got < header.Length)
        {
            int read = await stream.ReadAsync(header.AsMemory(got), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (got == 0) return null;
                throw new InvalidDataException("The tool pipe frame header was cut short.");
            }
            got += read;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException("The tool pipe frame length " + length.ToString(CultureInfo.InvariantCulture) + " is not allowed.");

        byte[] body = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The tool pipe frame was cut short.", ex);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The tool pipe frame is not JSON.", ex);
        }
        return node as JsonObject ?? throw new InvalidDataException("The tool pipe frame is not a JSON object.");
    }
}
```

- [ ] **Step 5: Run the protocol tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpPipeProtocolTests"`
Expected: 12 passed.

- [ ] **Step 6: Write the failing pipe tests**

Create `tests/Kil0bitSystemMonitor.Tests/McpPipeTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// The tool pipe end to end, in-process: a server on a unique test pipe name and the real
/// client. Nothing here touches the user's real pipe name.
/// </summary>
public class McpPipeTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static string TestPipeName() => "MicaStats.Tools.Test." + Guid.NewGuid().ToString("N");

    private static ToolPipeServer StartServer(string name, ToolInvoker invoke)
    {
        var server = new ToolPipeServer(name, invoke);
        server.Start();
        return server;
    }

    private static ToolInvoker Constant(JsonObject result) =>
        (tool, args, ct) => Task.FromResult<JsonNode>(result.DeepClone());

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task A_call_reaches_the_tool_with_its_arguments_and_returns_the_result()
    {
        string name = TestPipeName();
        string? seenTool = null;
        string? seenArgs = null;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            seenTool = tool;
            seenArgs = args?.ToJsonString();
            return Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 12.5 });
        });

        JsonNode result = await ToolPipeClient.CallAsync(name, "get_top_processes", new JsonObject { ["by"] = "cpu" }, TenSeconds, CancellationToken.None);

        Assert.Equal("{\"cpu\":12.5}", result.ToJsonString());
        Assert.Null(result.Parent);
        Assert.Equal("get_top_processes", seenTool);
        Assert.Equal("{\"by\":\"cpu\"}", seenArgs);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task One_connection_can_carry_several_requests()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool }));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        foreach (string tool in new[] { "get_battery", "get_hardware" })
        {
            await ToolPipeProtocol.WriteAsync(pipe, new JsonObject { ["v"] = 1, ["tool"] = tool }, CancellationToken.None);
            JsonObject? reply = await ToolPipeProtocol.ReadAsync(pipe, CancellationToken.None);

            Assert.Equal(1, (int?)reply!["v"]);
            Assert.Equal(true, (bool?)reply["ok"]);
            Assert.Equal(tool, (string?)reply["result"]!["tool"]);
        }
    }

    [Fact]
    public async Task A_request_from_another_protocol_version_is_refused_with_a_clear_error()
    {
        string name = TestPipeName();
        int calls = 0;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<JsonNode>(new JsonObject());
        });
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        await ToolPipeProtocol.WriteAsync(pipe, new JsonObject { ["v"] = 2, ["tool"] = "get_battery" }, CancellationToken.None);
        JsonObject? reply = await ToolPipeProtocol.ReadAsync(pipe, CancellationToken.None);

        Assert.Equal(false, (bool?)reply!["ok"]);
        Assert.Contains("version mismatch", (string?)reply["error"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task The_client_refuses_a_reply_from_another_protocol_version()
    {
        string name = TestPipeName();
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serve = Task.Run(async () =>
        {
            await fake.WaitForConnectionAsync();
            await ToolPipeProtocol.ReadAsync(fake, CancellationToken.None);
            await ToolPipeProtocol.WriteAsync(fake, new JsonObject { ["v"] = 2, ["ok"] = true, ["result"] = new JsonObject() }, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));

        Assert.Contains("version mismatch", ex.Message);
        await serve;
    }

    [Fact]
    public async Task A_failing_tool_reaches_the_caller_as_its_message()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => throw new InvalidOperationException("The sensor could not be read."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolPipeClient.CallAsync(name, "get_live_status", null, TenSeconds, CancellationToken.None));

        Assert.Equal("The sensor could not be read.", ex.Message);
    }

    [Fact]
    public async Task A_call_that_takes_too_long_times_out()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new JsonObject();
        });
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => ToolPipeClient.CallAsync(name, "get_live_status", null, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal("MicaStats did not answer within 0.3 s.", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_server_means_unavailable_within_about_a_second()
    {
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<ToolPipeUnavailableException>(
            () => ToolPipeClient.CallAsync(TestPipeName(), "get_live_status", null, TenSeconds, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_caller_gets_a_cancellation_not_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToolPipeClient.CallAsync(TestPipeName(), "get_live_status", null, TenSeconds, cts.Token));
    }

    [Fact]
    public void The_descriptor_admits_only_the_current_user_and_carries_a_medium_label()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier me = identity.User!;

        Assert.Equal("D:P(A;;GA;;;" + me.Value + ")S:(ML;;NW;;;ME)", ToolPipeNative.SddlFor(me));
    }

    [Fact]
    public async Task The_live_pipe_has_a_user_only_dacl_and_the_medium_label()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, Constant(new JsonObject()));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        string dacl = ToolPipeNative.ReadSddl(pipe.SafePipeHandle, ToolPipeNative.DACL_SECURITY_INFORMATION);
        string label = ToolPipeNative.ReadSddl(pipe.SafePipeHandle, ToolPipeNative.LABEL_SECURITY_INFORMATION);

        Assert.StartsWith("D:P(", dacl);
        Assert.Equal(1, CountOf(dacl, "(A;"));
        Assert.Contains(";;;" + identity.User!.Value + ")", dacl);
        Assert.Contains("(ML;;NW;;;ME)", label);
    }

    [Fact]
    public void A_name_another_process_already_serves_is_refused()
    {
        string name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
        using var server = new ToolPipeServer(name, Constant(new JsonObject()));

        var ex = Assert.Throws<IOException>(() => server.Start());

        Assert.Contains("already in use", ex.Message);
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void A_second_server_on_the_same_name_is_refused()
    {
        string name = TestPipeName();
        using ToolPipeServer first = StartServer(name, Constant(new JsonObject()));
        using var second = new ToolPipeServer(name, Constant(new JsonObject()));

        Assert.Throws<IOException>(() => second.Start());
        Assert.True(first.IsRunning);
    }

    [Fact]
    public async Task Stop_closes_the_pipe_and_frees_the_name()
    {
        string name = TestPipeName();
        ToolPipeServer first = StartServer(name, Constant(new JsonObject { ["n"] = 1 }));

        first.Stop();

        Assert.False(first.IsRunning);
        await Assert.ThrowsAsync<ToolPipeUnavailableException>(
            () => ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None));
        using ToolPipeServer second = StartServer(name, Constant(new JsonObject { ["n"] = 2 }));
        JsonNode result = await ToolPipeClient.CallAsync(name, "get_battery", null, TenSeconds, CancellationToken.None);
        Assert.Equal(2, (int?)result["n"]);
        first.Dispose();
    }

    [Fact]
    public async Task At_most_four_calls_run_at_once_and_the_rest_wait_their_turn()
    {
        string name = TestPipeName();
        int running = 0;
        int peak = 0;
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
            await Task.Delay(200, ct);
            Interlocked.Decrement(ref running);
            return new JsonObject { ["tool"] = tool };
        });

        JsonNode[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(
            i => ToolPipeClient.CallAsync(name, "t" + i, null, TenSeconds, CancellationToken.None)));

        Assert.Equal(Enumerable.Range(0, 6).Select(i => "t" + i), results.Select(r => (string?)r["tool"]));
        Assert.InRange(Volatile.Read(ref peak), 2, ToolPipeServer.MaxClients);
    }
}
```

- [ ] **Step 7: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpPipeTests"`
Expected: build error CS0246 - `ToolPipeServer`, `ToolPipeClient`, `ToolPipeUnavailableException` and `ToolPipeNative` could not be found.

- [ ] **Step 8: Create the native pipe helper**

This is the spike's Item8 option B (`CreateNamedPipeW` from the SDDL `D:P(A;;GA;;;<SID>)S:(ML;;NW;;;ME)`, read back as `S:AI(ML;;NW;;;ME)`), plus a flag for the first instance only. Create `Services/Ai/Mcp/ToolPipeNative.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Creates the tool pipe with a security descriptor that <see cref="System.IO.Pipes.PipeSecurity"/>
/// cannot express: a DACL that lets in only the current user, plus a medium mandatory
/// integrity label.
///
/// <para>
/// The label is why this is native code. A pipe created by an elevated MicaStats would carry
/// the high integrity level of its creator, and the no-write-up rule would then refuse the
/// unelevated bridge that Claude Desktop starts. <c>S:(ML;;NW;;;ME)</c> puts the pipe at medium,
/// so the same user's normal processes can connect while lower ones (a sandboxed browser tab)
/// still cannot. Setting it in the call that creates the pipe leaves no moment when the pipe
/// exists without it.
/// </para>
/// </summary>
internal static class ToolPipeNative
{
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint SDDL_REVISION_1 = 1;
    private const int SE_KERNEL_OBJECT = 6;

    /// <summary>Asks <see cref="ReadSddl"/> for the DACL.</summary>
    internal const uint DACL_SECURITY_INFORMATION = 0x00000004;

    /// <summary>Asks <see cref="ReadSddl"/> for the mandatory integrity label.</summary>
    internal const uint LABEL_SECURITY_INFORMATION = 0x00000010;

    /// <summary>Win32 ERROR_ACCESS_DENIED: the first-instance flag met a name that already exists.</summary>
    internal const int ErrorAccessDenied = 5;

    /// <summary>Win32 ERROR_PIPE_BUSY: every allowed instance of the name exists already.</summary>
    internal const int ErrorPipeBusy = 231;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out uint SecurityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr SecurityDescriptor, uint RequestedStringSDRevision, uint SecurityInformation,
        out IntPtr StringSecurityDescriptor, out uint StringSecurityDescriptorLen);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int ObjectType, uint SecurityInfo,
        IntPtr ppsidOwner, IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// The descriptor for <paramref name="user"/>: a protected DACL (no inherited entries)
    /// granting only that SID full access, and a medium no-write-up integrity label.
    /// </summary>
    internal static string SddlFor(SecurityIdentifier user) => "D:P(A;;GA;;;" + user.Value + ")S:(ML;;NW;;;ME)";

    /// <summary>
    /// Creates one server instance of <c>\\.\pipe\&lt;pipeName&gt;</c>: duplex, overlapped, byte
    /// mode, remote clients rejected. <paramref name="firstInstance"/> adds
    /// FILE_FLAG_FIRST_PIPE_INSTANCE, so creation fails if any other process already owns the name
    /// (it could otherwise read every request the bridge sends). Every instance of one name must
    /// pass the same <paramref name="maxInstances"/>. Throws <see cref="Win32Exception"/>.
    /// </summary>
    internal static SafePipeHandle Create(string pipeName, string sddl, bool firstInstance, int maxInstances)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out IntPtr sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0,
            };
            uint openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0u);
            SafePipeHandle handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, openMode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                (uint)maxInstances, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return handle;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// The SDDL text of one part (<see cref="DACL_SECURITY_INFORMATION"/> or
    /// <see cref="LABEL_SECURITY_INFORMATION"/>) of the descriptor of the object behind
    /// <paramref name="handle"/>. The handle needs READ_CONTROL, which a connected client end
    /// has. Throws <see cref="Win32Exception"/>.
    /// </summary>
    internal static string ReadSddl(SafeHandle handle, uint information)
    {
        uint rc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, information, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
        if (rc != 0) throw new Win32Exception((int)rc);
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, SDDL_REVISION_1, information, out IntPtr text, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                return Marshal.PtrToStringUni(text) ?? "";
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(sd);
        }
    }
}
```

- [ ] **Step 9: Create the server**

Create `Services/Ai/Mcp/ToolPipeServer.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The running app's end of the tool pipe: answers <c>--mcp</c> bridge requests by calling a
/// <see cref="ToolInvoker"/>, up to <see cref="MaxClients"/> connections at a time.
///
/// <para>
/// The pipe admits only the current user and carries a medium integrity label (see
/// <see cref="ToolPipeNative"/>). The first instance is created with the first-instance flag,
/// so <see cref="Start"/> fails rather than share a name another process already serves, and
/// the next instance is always created before a connected one is handed off, so the name never
/// has zero instances while the server runs and cannot be taken over in between.
/// </para>
/// </summary>
public sealed class ToolPipeServer : IDisposable
{
    /// <summary>How many connections are served at once; a further client waits for a free slot.</summary>
    public const int MaxClients = 4;

    /// <summary>
    /// How long one tool may run on behalf of the pipe. Longer than the bridge's own 10 s wait,
    /// so the bridge reports the timeout; this only frees the slot of a tool that never returns.
    /// </summary>
    private static readonly TimeSpan ServerCallTimeout = TimeSpan.FromSeconds(15);

    private readonly string _pipeName;
    private readonly ToolInvoker _invoke;
    private readonly Action<string> _warn;
    private readonly string _sddl;
    private readonly object _gate = new();
    private readonly HashSet<NamedPipeServerStream> _open = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>A server for <paramref name="pipeName"/> (see <see cref="ToolPipeProtocol.DefaultPipeName"/>); nothing is created until <see cref="Start"/>.</summary>
    /// <param name="warn">Receives failure notes for the diagnostics log: never tool arguments or results.</param>
    public ToolPipeServer(string pipeName, ToolInvoker invoke, Action<string>? warn = null)
    {
        _pipeName = pipeName;
        _invoke = invoke;
        _warn = warn ?? (_ => { });
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        _sddl = ToolPipeNative.SddlFor(identity.User ?? throw new InvalidOperationException("The current Windows user has no SID."));
    }

    /// <summary>True from a successful <see cref="Start"/> until <see cref="Stop"/>, or until the accept loop fails.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    /// <summary>
    /// Creates the pipe and starts accepting. Does nothing while already running. Throws
    /// <see cref="IOException"/> when the name is already served by another process (or another
    /// server) or the pipe cannot be created.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;

            NamedPipeServerStream first = CreateInstance(firstInstance: true);
            _open.Add(first);
            var cts = new CancellationTokenSource();
            var slots = new SemaphoreSlim(MaxClients, MaxClients);
            _cts = cts;
            _loop = Task.Run(() => AcceptLoopAsync(first, slots, cts.Token));
        }
    }

    /// <summary>Stops accepting, drops every connection and frees the pipe name. Safe to call repeatedly.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        NamedPipeServerStream[] open;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            open = _open.ToArray();
            _open.Clear();
        }
        if (cts == null) return;

        cts.Cancel();
        foreach (NamedPipeServerStream pipe in open) pipe.Dispose();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop logs its own failures; stopping must not throw.
        }
    }

    /// <summary>Same as <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(NamedPipeServerStream listening, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    // A client connected and left before it was accepted. Replace the instance,
                    // creating the new one first so the name is never left without one.
                    NamedPipeServerStream stale = listening;
                    listening = Track(CreateInstance(firstInstance: false));
                    Close(stale);
                    continue;
                }

                NamedPipeServerStream connected = listening;
                try
                {
                    await slots.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Close(connected);
                    return;
                }

                // The next instance exists before this one is served (and later closed).
                listening = Track(CreateInstance(firstInstance: false));
                _ = Task.Run(() => ServeAsync(connected, slots, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                _warn("The tool pipe stopped accepting connections: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Close(listening);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonObject? request;
                try
                {
                    request = await ToolPipeProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    await ToolPipeProtocol.WriteAsync(pipe, Failure("The request could not be read: " + ex.Message), ct).ConfigureAwait(false);
                    return;
                }
                if (request == null) return;   // the bridge hung up

                JsonObject reply = await AnswerAsync(request, ct).ConfigureAwait(false);
                await ToolPipeProtocol.WriteAsync(pipe, reply, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopping, or the bridge went away mid-call: nothing to answer.
        }
        catch (Exception ex)
        {
            _warn("A tool pipe connection failed: " + ex.GetType().Name);
        }
        finally
        {
            Close(pipe);
            slots.Release();
        }
    }

    private async Task<JsonObject> AnswerAsync(JsonObject request, CancellationToken ct)
    {
        if (request["v"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version) || version != ToolPipeProtocol.Version)
        {
            string sent = request["v"]?.ToJsonString() ?? "none";
            return Failure("Tool pipe version mismatch: this MicaStats speaks version " +
                ToolPipeProtocol.Version.ToString(CultureInfo.InvariantCulture) + " but the caller sent " + sent +
                ". Update MicaStats so the bridge and the running app are the same version.");
        }

        if (request["tool"] is not JsonValue toolValue || !toolValue.TryGetValue(out string? tool) || string.IsNullOrEmpty(tool))
            return Failure("The request names no tool.");

        JsonNode? argsNode = request["args"];
        if (argsNode is not null and not JsonObject) return Failure("The tool arguments must be a JSON object.");
        request.Remove("args");   // detached, so the tool may keep or re-parent it

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ServerCallTimeout);
        try
        {
            JsonNode result = await _invoke(tool, (JsonObject?)argsNode, deadline.Token).ConfigureAwait(false);
            return new JsonObject
            {
                ["v"] = ToolPipeProtocol.Version,
                ["ok"] = true,
                ["result"] = result.Parent == null ? result : result.DeepClone(),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _warn("Tool " + tool + " took too long over the tool pipe");
            return Failure("The tool " + tool + " took too long.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _warn("Tool " + tool + " failed over the tool pipe: " + ex.GetType().Name);
            return Failure(ex.Message);
        }
    }

    private static JsonObject Failure(string error) => new()
    {
        ["v"] = ToolPipeProtocol.Version,
        ["ok"] = false,
        ["error"] = error,
    };

    private NamedPipeServerStream CreateInstance(bool firstInstance)
    {
        try
        {
            // One more instance than served clients: the one waiting for the next caller.
            var handle = ToolPipeNative.Create(_pipeName, _sddl, firstInstance, MaxClients + 1);
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ToolPipeNative.ErrorAccessDenied or ToolPipeNative.ErrorPipeBusy)
        {
            throw new IOException("The tool pipe " + _pipeName + " is already in use by another process.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new IOException("The tool pipe could not be created: " + ex.Message, ex);
        }
    }

    private NamedPipeServerStream Track(NamedPipeServerStream pipe)
    {
        lock (_gate) _open.Add(pipe);
        return pipe;
    }

    private void Close(NamedPipeServerStream pipe)
    {
        lock (_gate) _open.Remove(pipe);
        pipe.Dispose();
    }
}
```

- [ ] **Step 10: Create the client**

Create `Services/Ai/Mcp/ToolPipeClient.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Nothing answered on the tool pipe: MicaStats is not running, is not serving the pipe (MCP
/// is not set to the stdio bridge), or every slot stayed busy for the whole connect wait. The
/// bridge answers from files on disk instead.
/// </summary>
public sealed class ToolPipeUnavailableException : Exception
{
    /// <summary>Creates the exception with a sentence for the diagnostics log.</summary>
    public ToolPipeUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// The bridge's end of the tool pipe: one connection per call, so a MicaStats restart between
/// two calls costs nothing.
/// </summary>
public static class ToolPipeClient
{
    /// <summary>How long to wait for the pipe to accept the connection before calling MicaStats absent.</summary>
    private const int ConnectTimeoutMs = 1000;

    /// <summary>
    /// Runs <paramref name="tool"/> in the running MicaStats and returns its result, detached from
    /// the reply. Throws <see cref="ToolPipeUnavailableException"/> when nothing accepts the
    /// connection within 1 s; <see cref="TimeoutException"/> when no reply arrives within
    /// <paramref name="timeout"/>; <see cref="InvalidOperationException"/> carrying the server's
    /// sentence for <c>ok:false</c>, a version mismatch, or a broken or unreadable reply; and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<JsonNode> CallAsync(string pipeName, string tool, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ToolPipeUnavailableException("Nothing answered on the MicaStats tool pipe.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolPipeUnavailableException("The MicaStats tool pipe refused this user.");
        }
        catch (IOException ex)
        {
            throw new ToolPipeUnavailableException("The MicaStats tool pipe could not be opened: " + ex.Message);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        JsonObject? reply;
        try
        {
            await ToolPipeProtocol.WriteAsync(pipe, new JsonObject
            {
                ["v"] = ToolPipeProtocol.Version,
                ["tool"] = tool,
                ["args"] = args?.DeepClone(),
            }, deadline.Token).ConfigureAwait(false);
            reply = await ToolPipeProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("MicaStats did not answer within " +
                timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The connection to MicaStats was lost before it answered.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("MicaStats sent a reply that could not be read.", ex);
        }

        if (reply == null) throw new InvalidOperationException("MicaStats closed the tool pipe without answering.");

        if (reply["v"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version) || version != ToolPipeProtocol.Version)
            throw new InvalidOperationException("Tool pipe version mismatch: this bridge speaks version " +
                ToolPipeProtocol.Version.ToString(CultureInfo.InvariantCulture) + " but MicaStats answered with " +
                (reply["v"]?.ToJsonString() ?? "none") + ". Update MicaStats so the bridge and the running app are the same version.");

        bool ok = reply["ok"] is JsonValue okValue && okValue.TryGetValue(out bool okFlag) && okFlag;
        if (!ok)
        {
            string error = reply["error"] is JsonValue errorValue && errorValue.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
                ? text
                : "MicaStats reported an error without a message.";
            throw new InvalidOperationException(error);
        }

        JsonNode? result = reply["result"];
        if (result == null) throw new InvalidOperationException("MicaStats answered without a result.");
        reply.Remove("result");
        return result;
    }
}
```

- [ ] **Step 11: Run the pipe tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpPipe"`
Expected: 26 passed (12 protocol + 14 pipe). The whole run takes a few seconds: two tests each wait out the 1 s connect timeout on purpose.

- [ ] **Step 12: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1018 + 26 = 1044).

```bash
git add Services/Ai/Mcp/ToolInvoker.cs Services/Ai/Mcp/ToolPipeProtocol.cs Services/Ai/Mcp/ToolPipeNative.cs Services/Ai/Mcp/ToolPipeServer.cs Services/Ai/Mcp/ToolPipeClient.cs tests/Kil0bitSystemMonitor.Tests/McpPipeProtocolTests.cs tests/Kil0bitSystemMonitor.Tests/McpPipeTests.cs
git commit -F - <<'EOF'
feat(ai): tool pipe between the MCP bridge and the running app

Length-prefixed UTF-8 JSON over a per-user named pipe. The pipe is
created with a DACL that admits only the current user SID and a medium
integrity label, so an unelevated bridge can reach an elevated MicaStats
while lower processes cannot. The first instance refuses a name another
process already serves, the next instance always exists before a
connected one is handed off, and at most four callers are served at
once. The client makes one connection per call, waits 1 s for the pipe
and applies a per-call timeout.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 10: MCP stdio bridge (`MicaStats.exe --mcp`) and the pipe lifecycle

Claude Desktop and Claude Code start `MicaStats.exe --mcp` and talk MCP over its stdin/stdout. `McpToolSet` exposes the nine read-only tools (annotated read-only, non-destructive, closed-world); `McpBridge` serves them on stdio and forwards each call over the tool pipe, answers from files on disk when MicaStats is not running, and refuses every call while MCP is Off (read from `config.json` on every call). `App.OnStartup` gets the `--mcp` branch right after `--kill` (no window, no mutex, nothing but MCP on stdout), and `App.Ai.cs` runs the pipe server while `AiMcpMode` is `Stdio`. Tests drive the real MCP protocol with the SDK client over in-memory streams; nothing launches MicaStats.

**Files:**
- Create: `Services/Ai/Mcp/McpArguments.cs`
- Create: `Services/Ai/Mcp/McpToolSet.cs`
- Create: `Services/Ai/Mcp/McpBridge.cs`
- Modify: `App.xaml.cs` (`OnStartup`: the `--mcp` branch after the `--kill` block)
- Modify: `App.Ai.cs` (anchors `members`, `start`, `apply`, `stop`: the tool pipe lifecycle)
- Test: `tests/Kil0bitSystemMonitor.Tests/McpInMemory.cs` (helper)
- Test: `tests/Kil0bitSystemMonitor.Tests/McpToolSetTests.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/McpBridgeTests.cs`

**Interfaces:**
- Consumes:
  - Task 9: `ToolInvoker`, `ToolPipeServer(string pipeName, ToolInvoker invoke, Action<string>? warn = null)`, `Start()`, `Dispose()`, `ToolPipeClient.CallAsync(...)`, `ToolPipeUnavailableException`, `ToolPipeProtocol.DefaultPipeName()`.
  - Task 1: `Kil0bitSystemMonitor.Services.Ai.AiMcpModes { Off = "Off", Stdio = "Stdio", Http = "Http" }`; `AppConfig.AiMcpMode` (string); test helper `AiTestEnv` (`new AiTestEnv()`, `Root`, `PathOf(string)`, `Clock.UtcNow`, `Dispose()`).
  - Task 2: `Redactor(string userProfile, string userName, string machineName)`, `Redactor.ForCurrentUser()`.
  - Task 4: `HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null)`, `HistoryStore.DefaultFolder`.
  - Tasks 6-7: `ToolNames` (constants and `ReadOnly`), `ToolJson.Error(string message)` (`{"error": message}`), `MicaTools(IMicaData data, Redactor redactor)`, `MicaTools.InvokeAsync(string tool, JsonObject? args, CancellationToken ct = default)`, `OfflineMicaData(HistoryStore store, string reportDir, Func<DateTime> utcClock)` (live members throw `DataUnavailableException("MicaStats is not running")`), `App.AiTools` (`MicaTools?`).
  - Task 5: `App.Ai.cs` anchors `// AI anchor: members`, `// AI anchor: start`, `// AI anchor: apply`, `// AI anchor: stop`.
  - Existing: `SlowdownRecorder.ReportDir`, `DiagnosticsLog.DataDir` / `Log` / `Warn` / `Error`, `App.ConfigService`.
- Produces (namespace `Kil0bitSystemMonitor.Services.Ai.Mcp`):
  - `public static class McpArguments { public const string Flag = "--mcp"; public static bool TryParse(IReadOnlyList<string> args); }` (any argument, case-insensitive)
  - `public static class McpToolSet { public const string ServerName = "micastats"; public const string Instructions; public static string CurrentVersion { get; } public static McpServerOptions CreateOptions(ToolInvoker invoke, string serverVersion); }` (the nine read-only tools via `McpServerTool.Create(Delegate, ReadOnly = true, Destructive = false, OpenWorld = false)`, results as JSON text with non-ASCII kept readable)
  - `public static class McpBridge { public const string OffMessage = "MCP is turned off in MicaStats Settings"; public static int RunStdio(); public static string ReadMcpMode(string configPath); public static ToolInvoker CreateForwarder(string pipeName, Func<string> mode, MicaTools offline, TimeSpan callTimeout); internal static readonly TimeSpan CallTimeout; internal static string DefaultConfigPath { get; } internal static McpServerOptions CreateBridgeOptions(string configPath, string pipeName, MicaTools offline, TimeSpan callTimeout); }`
  - `App.Ai.cs`: `private static ToolPipeServer? s_toolPipe;` and `private static void ApplyToolPipe()`; the pipe runs while `AiMcpMode == AiMcpModes.Stdio` and `AiTools` exists.
  - Test helper `internal sealed class McpInMemory : IAsyncDisposable { static Task<McpInMemory> ConnectAsync(McpServerOptions options); McpClient Client { get; } Task<string> CallTextAsync(string tool, Dictionary<string, object?>? args = null); }`

- [ ] **Step 1: Write the in-memory MCP helper and the failing tool-set tests**

The helper is the spike's Item5 round trip (two `System.IO.Pipelines.Pipe`s, `StreamServerTransport`, `StreamClientTransport`, `McpClient.CreateAsync`). Create `tests/Kil0bitSystemMonitor.Tests/McpInMemory.cs`:

```csharp
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// An MCP server and the SDK's own client joined by two in-memory pipes: the real protocol,
/// with no process and no stdio.
/// </summary>
internal sealed class McpInMemory : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
    private McpServer? _server;
    private Task? _run;

    private McpInMemory()
    {
    }

    /// <summary>The connected client.</summary>
    public McpClient Client { get; private set; } = null!;

    /// <summary>Starts a server with <paramref name="options"/> and connects a client to it.</summary>
    public static async Task<McpInMemory> ConnectAsync(McpServerOptions options)
    {
        var session = new McpInMemory();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverTransport = new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "test");
        session._server = McpServer.Create(serverTransport, options);
        session._run = session._server.RunAsync(session._cts.Token);
        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream());
        session.Client = await McpClient.CreateAsync(clientTransport, cancellationToken: session._cts.Token);
        return session;
    }

    /// <summary>Calls a tool and returns its text content.</summary>
    public async Task<string> CallTextAsync(string tool, Dictionary<string, object?>? args = null)
    {
        CallToolResult result = await Client.CallToolAsync(tool, args, cancellationToken: _cts.Token);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    /// <summary>Disconnects the client and stops the server.</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _cts.Cancel();
        try
        {
            if (_run != null) await _run;
        }
        catch (OperationCanceledException)
        {
        }
        if (_server != null) await _server.DisposeAsync();
        _cts.Dispose();
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/McpToolSetTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Client;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The MCP tool list and argument mapping, through the SDK client over in-memory streams.</summary>
public class McpToolSetTests
{
    private sealed class RecordingInvoker
    {
        public readonly List<(string Tool, string? Args)> Calls = new();
        public JsonNode Reply { get; set; } = new JsonObject { ["ok"] = 1 };

        public Task<JsonNode> Invoke(string tool, JsonObject? args, CancellationToken ct)
        {
            lock (Calls) Calls.Add((tool, args?.ToJsonString()));
            return Task.FromResult(Reply.DeepClone());
        }
    }

    [Fact]
    public async Task The_server_lists_exactly_the_nine_read_only_tools()
    {
        var invoker = new RecordingInvoker();
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "9.8.7"));

        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync();

        Assert.Equal(ToolNames.ReadOnly.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.SuggestAction);
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
            Assert.False(t.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
        Assert.Equal(McpToolSet.ServerName, mcp.Client.ServerInfo.Name);
        Assert.Equal("9.8.7", mcp.Client.ServerInfo.Version);
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task The_history_schema_requires_metric_and_from_and_hides_the_cancellation_token()
    {
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(new RecordingInvoker().Invoke, "1.0.0"));

        McpClientTool history = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == ToolNames.GetHistory);
        JsonElement schema = history.JsonSchema;

        string[] properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "from", "maxPoints", "metric", "to" }, properties);
        string[] required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "from", "metric" }, required);
    }

    [Theory]
    [InlineData(ToolNames.GetLiveStatus, "{}", null)]
    [InlineData(ToolNames.GetHistory, "{\"metric\":\"cpu\",\"from\":\"-1h\"}", "{\"metric\":\"cpu\",\"from\":\"-1h\",\"to\":\"now\",\"maxPoints\":200}")]
    [InlineData(ToolNames.GetHistory, "{\"metric\":\"all\",\"from\":\"-2d\",\"to\":\"-1d\",\"maxPoints\":50}", "{\"metric\":\"all\",\"from\":\"-2d\",\"to\":\"-1d\",\"maxPoints\":50}")]
    [InlineData(ToolNames.GetTopProcesses, "{}", "{\"by\":\"cpu\",\"count\":10}")]
    [InlineData(ToolNames.GetTopProcesses, "{\"by\":\"memory\",\"count\":3}", "{\"by\":\"memory\",\"count\":3}")]
    [InlineData(ToolNames.ListSlowdownReports, "{}", "{\"limit\":10}")]
    [InlineData(ToolNames.GetSlowdownReport, "{\"id\":\"slowdown-20260930-140200\"}", "{\"id\":\"slowdown-20260930-140200\"}")]
    [InlineData(ToolNames.ListAlerts, "{}", "{}")]
    [InlineData(ToolNames.ListAlerts, "{\"since\":\"-24h\"}", "{\"since\":\"-24h\"}")]
    [InlineData(ToolNames.GetHardware, "{}", null)]
    [InlineData(ToolNames.GetBattery, "{}", null)]
    [InlineData(ToolNames.GetBootSummary, "{}", null)]
    public async Task Each_tool_forwards_its_name_and_arguments_keyed_like_mica_tools(string tool, string argsJson, string? expectedArgs)
    {
        var invoker = new RecordingInvoker();
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "1.0.0"));
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)!;

        await mcp.CallTextAsync(tool, args);

        var call = Assert.Single(invoker.Calls);
        Assert.Equal(tool, call.Tool);
        Assert.Equal(expectedArgs, call.Args);
    }

    [Fact]
    public async Task The_result_comes_back_as_json_text_with_non_ascii_kept_readable()
    {
        var invoker = new RecordingInvoker { Reply = new JsonObject { ["name"] = "\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe", ["cpu"] = 7.5 } };
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(invoker.Invoke, "1.0.0"));

        string text = await mcp.CallTextAsync(ToolNames.GetLiveStatus);

        Assert.Equal("{\"name\":\"\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe\",\"cpu\":7.5}", text);
    }

    [Fact]
    public async Task An_invoker_failure_comes_back_as_an_error_object()
    {
        ToolInvoker failing = (tool, args, ct) => throw new InvalidOperationException("The pipe broke.");
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpToolSet.CreateOptions(failing, "1.0.0"));

        string text = await mcp.CallTextAsync(ToolNames.GetBattery);

        Assert.Equal("The pipe broke.", (string?)JsonNode.Parse(text)!["error"]);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpToolSetTests"`
Expected: build error CS0103 / CS0246 - `McpToolSet` does not exist.

- [ ] **Step 3: Create the argument parser**

Create `Services/Ai/Mcp/McpArguments.cs`:

```csharp
namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Recognises <c>MicaStats.exe --mcp</c>, the command line Claude Desktop and Claude Code use to
/// start the stdio bridge.
///
/// <para>
/// Looser than <see cref="Kil0bitSystemMonitor.Services.KillArguments"/> on purpose: the bridge
/// grants nothing (it serves read-only data to the same user), so the flag is found anywhere on
/// the line and in any case, and a client that adds its own arguments still reaches it.
/// </para>
/// </summary>
public static class McpArguments
{
    /// <summary>The switch that selects the stdio bridge.</summary>
    public const string Flag = "--mcp";

    /// <summary>True when any argument is <see cref="Flag"/> (case-insensitive).</summary>
    public static bool TryParse(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Create the MCP tool set**

`McpServerTool.Create(Delegate, McpServerToolCreateOptions)` over typed methods is the spike's Item5 variant (c): a `string` result reaches the client as plain text, `[Description]` and default values become the input schema, and the `CancellationToken` is bound by the SDK. Create `Services/Ai/Mcp/McpToolSet.cs`:

```csharp
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The MCP face of the nine read-only data tools, shared by the stdio bridge and the local
/// HTTP host. <c>suggest_action</c> is in-app only and never listed here: MCP stays read-only.
///
/// <para>
/// Each tool is a typed method, so MCP clients see a proper input schema, and each forwards to
/// a <see cref="ToolInvoker"/> with arguments keyed by the <c>MicaTools</c> parameter names.
/// Results go back as JSON text; non-ASCII (a Thai process name) stays readable rather than
/// becoming <c>\u</c> escapes.
/// </para>
/// </summary>
public static class McpToolSet
{
    /// <summary>The server name MCP clients see, and the key the config snippets use.</summary>
    public const string ServerName = "micastats";

    /// <summary>What the server tells the client about itself at connection time.</summary>
    public const string Instructions =
        "MicaStats is a system monitor running on this Windows PC. Every tool is read-only and returns compact JSON. " +
        "A reading the PC does not provide is reported as unavailable with a reason, never as 0. Times are UTC. " +
        "Paths under the user's profile folder are shown as %USERPROFILE%.";

    /// <summary>The MicaStats version, for <c>serverInfo</c>.</summary>
    public static string CurrentVersion => typeof(McpToolSet).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Fresh server options listing the nine read-only tools, all annotated read-only,
    /// non-destructive and closed-world. Fresh each call because the SDK may adjust the options
    /// object of the server it creates, and the HTTP host creates one server per request.
    /// </summary>
    public static McpServerOptions CreateOptions(ToolInvoker invoke, string serverVersion)
    {
        var handlers = new Handlers(invoke);
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = serverVersion },
            ServerInstructions = Instructions,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
            {
                Tool(handlers.GetLiveStatus, ToolNames.GetLiveStatus,
                    "Current readings: CPU total and per core, temperatures, RAM, GPU, disks, network, battery and sensors, plus min/avg/max over the last two minutes."),
                Tool(handlers.GetHistory, ToolNames.GetHistory,
                    "Stored per-minute history (last 24 h per minute, up to 7 days in 5-minute rows) for one metric over a time range. Empty when history is turned off in MicaStats."),
                Tool(handlers.GetTopProcesses, ToolNames.GetTopProcesses,
                    "The busiest processes right now by CPU, memory or disk, with name, path, PID, start time, CPU %, working set MB and disk KB/s. Takes about 2 seconds."),
                Tool(handlers.ListSlowdownReports, ToolNames.ListSlowdownReports,
                    "Saved slowdown reports, newest first: id, time, what triggered it and a one-line summary."),
                Tool(handlers.GetSlowdownReport, ToolNames.GetSlowdownReport,
                    "The full text of one slowdown report: a per-second timeline and the worst offending processes."),
                Tool(handlers.ListAlerts, ToolNames.ListAlerts,
                    "The alert rules (enabled, threshold, how long it must last) and the alerts raised recently."),
                Tool(handlers.GetHardware, ToolNames.GetHardware,
                    "Hardware inventory: CPU, GPU, RAM, mainboard and disk models and sizes."),
                Tool(handlers.GetBattery, ToolNames.GetBattery,
                    "Battery charge, health, wear, design and full-charge capacity and cycle count. Unavailable on a desktop."),
                Tool(handlers.GetBootSummary, ToolNames.GetBootSummary,
                    "Recent Windows boot durations, their trend, and the apps, drivers and services that slowed startup."),
            },
        };
    }

    private static McpServerTool Tool(Delegate method, string name, string description) =>
        McpServerTool.Create(method, new McpServerToolCreateOptions
        {
            Name = name,
            Description = description,
            ReadOnly = true,
            Destructive = false,
            OpenWorld = false,
        });

    /// <summary>
    /// The tool methods. Parameter names and defaults are the MCP input schema; the
    /// <see cref="CancellationToken"/> is bound by the SDK and not shown to clients.
    /// </summary>
    private sealed class Handlers
    {
        private readonly ToolInvoker _invoke;

        public Handlers(ToolInvoker invoke) => _invoke = invoke;

        public Task<string> GetLiveStatus(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetLiveStatus, null, cancellationToken);

        public Task<string> GetHistory(
            [Description("One of: cpu, cpuTemp, ram, gpu, gpuTemp, netUp, netDown, diskFree, diskActivity, battery, topProcesses, all.")] string metric,
            [Description("Start of the range: ISO-8601 UTC such as 2026-09-30T08:00:00Z, or relative such as -90s, -30m, -6h, -2d.")] string from,
            [Description("End of the range, in the same forms, or now.")] string to = "now",
            [Description("Most rows to return, 1 to 500; a longer range is downsampled.")] int maxPoints = 200,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetHistory, new JsonObject
            {
                ["metric"] = metric,
                ["from"] = from,
                ["to"] = to,
                ["maxPoints"] = maxPoints,
            }, cancellationToken);

        public Task<string> GetTopProcesses(
            [Description("Sort by cpu, memory or disk.")] string by = "cpu",
            [Description("How many processes, 1 to 15.")] int count = 10,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetTopProcesses, new JsonObject { ["by"] = by, ["count"] = count }, cancellationToken);

        public Task<string> ListSlowdownReports(
            [Description("How many reports, 1 to 30.")] int limit = 10,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.ListSlowdownReports, new JsonObject { ["limit"] = limit }, cancellationToken);

        public Task<string> GetSlowdownReport(
            [Description("The report id from list_slowdown_reports, for example slowdown-20260930-140200.")] string id,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.GetSlowdownReport, new JsonObject { ["id"] = id }, cancellationToken);

        public Task<string> ListAlerts(
            [Description("Only alerts at or after this time: ISO-8601 UTC or relative such as -24h. Omit for all recent alerts.")] string? since = null,
            CancellationToken cancellationToken = default) =>
            CallAsync(ToolNames.ListAlerts, since == null ? new JsonObject() : new JsonObject { ["since"] = since }, cancellationToken);

        public Task<string> GetHardware(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetHardware, null, cancellationToken);

        public Task<string> GetBattery(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetBattery, null, cancellationToken);

        public Task<string> GetBootSummary(CancellationToken cancellationToken) =>
            CallAsync(ToolNames.GetBootSummary, null, cancellationToken);

        private async Task<string> CallAsync(string tool, JsonObject? args, CancellationToken ct)
        {
            JsonNode result;
            try
            {
                result = await _invoke(tool, args, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A tool never throws at the model: the failure becomes data it can read.
                result = ToolJson.Error(ex.Message);
            }
            return result.ToJsonString(TextOptions);
        }
    }
}
```

- [ ] **Step 5: Run the tool-set tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpToolSetTests"`
Expected: 16 passed.

- [ ] **Step 6: Write the failing bridge tests**

Create `tests/Kil0bitSystemMonitor.Tests/McpBridgeTests.cs`:

```csharp
using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using ModelContextProtocol.Client;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// The <c>--mcp</c> bridge without a process: argument parsing, the mode read from config.json,
/// and the forwarder against a test pipe, the offline tools and Off.
/// </summary>
public class McpBridgeTests : IDisposable
{
    private const string SlowdownReport =
        "==============================================================\n" +
        " MicaStats Slowdown Report\n" +
        " Version   : MicaStats 1.11.0\n" +
        " Trigger   : Recorded by hand\n" +
        " Window    : 2026-09-30 10:00:00  to  2026-09-30 10:05:00\n" +
        " Samples   : 0\n" +
        "==============================================================\n" +
        "No samples were held when this report was written.\n";

    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private readonly AiTestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string TestPipeName() => "MicaStats.Tools.Test." + Guid.NewGuid().ToString("N");

    private MicaTools OfflineTools()
    {
        string reports = _env.PathOf("reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "slowdown-20260930-100000.txt"), SlowdownReport);
        var store = new HistoryStore(_env.PathOf("history"), () => _env.Clock.UtcNow);
        var data = new OfflineMicaData(store, reports, () => _env.Clock.UtcNow);
        return new MicaTools(data, new Redactor(_env.Root, "tester", "TESTPC"));
    }

    private static ToolPipeServer StartServer(string name, ToolInvoker invoke)
    {
        var server = new ToolPipeServer(name, invoke);
        server.Start();
        return server;
    }

    [Theory]
    [InlineData(new[] { "--mcp" }, true)]
    [InlineData(new[] { "--MCP" }, true)]
    [InlineData(new[] { "--startup", "--mcp" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--pad" }, false)]
    [InlineData(new[] { "--mcpx" }, false)]
    [InlineData(new[] { "mcp" }, false)]
    public void The_mcp_flag_is_recognised_anywhere_on_the_command_line(string[] args, bool expected)
    {
        Assert.Equal(expected, McpArguments.TryParse(args));
    }

    [Theory]
    [InlineData("{\"AiMcpMode\":\"Stdio\"}", AiMcpModes.Stdio)]
    [InlineData("{\"Theme\":\"Dark\",\"AiMcpMode\":\"Http\"}", AiMcpModes.Http)]
    [InlineData("{\"AiMcpMode\":\"Off\"}", AiMcpModes.Off)]
    [InlineData("{\"AiMcpMode\":\"stdio\"}", AiMcpModes.Off)]
    [InlineData("{\"AiMcpMode\":3}", AiMcpModes.Off)]
    [InlineData("{}", AiMcpModes.Off)]
    [InlineData("[]", AiMcpModes.Off)]
    [InlineData("not json", AiMcpModes.Off)]
    [InlineData("", AiMcpModes.Off)]
    public void The_mode_comes_from_config_json_and_anything_unclear_is_off(string configText, string expected)
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, configText);

        Assert.Equal(expected, McpBridge.ReadMcpMode(config));
    }

    [Fact]
    public void A_missing_config_is_off()
    {
        Assert.Equal(AiMcpModes.Off, McpBridge.ReadMcpMode(_env.PathOf("missing.json")));
    }

    [Fact]
    public void The_config_is_read_while_the_app_holds_it_open_for_writing()
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\"}");
        using var writer = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(AiMcpModes.Stdio, McpBridge.ReadMcpMode(config));
    }

    [Fact]
    public async Task Off_refuses_every_tool_without_touching_the_pipe()
    {
        string name = TestPipeName();
        int calls = 0;
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<JsonNode>(new JsonObject());
        });
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Off, OfflineTools(), TenSeconds);

        foreach (string tool in ToolNames.ReadOnly)
        {
            JsonNode result = await forward(tool, null, CancellationToken.None);
            Assert.Equal("MCP is turned off in MicaStats Settings", (string?)result["error"]);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task With_the_app_running_a_call_is_answered_by_the_app()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) =>
            Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool, ["args"] = args?.DeepClone() }));
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode result = await forward(ToolNames.GetTopProcesses, new JsonObject { ["by"] = "disk" }, CancellationToken.None);

        Assert.Equal("{\"tool\":\"get_top_processes\",\"args\":{\"by\":\"disk\"}}", result.ToJsonString());
    }

    [Fact]
    public async Task Without_the_app_history_and_reports_come_from_disk_and_live_tools_say_so()
    {
        ToolInvoker forward = McpBridge.CreateForwarder(TestPipeName(), () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode live = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);
        JsonNode list = await forward(ToolNames.ListSlowdownReports, new JsonObject { ["limit"] = 10 }, CancellationToken.None);
        JsonNode report = await forward(ToolNames.GetSlowdownReport, new JsonObject { ["id"] = "slowdown-20260930-100000" }, CancellationToken.None);

        Assert.Equal("MicaStats is not running", (string?)live["error"]);
        Assert.Contains("slowdown-20260930-100000", list.ToJsonString());
        Assert.Contains("Recorded by hand", report.ToJsonString());
    }

    [Fact]
    public async Task A_failure_inside_the_app_comes_back_as_an_error_object()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => throw new InvalidOperationException("The battery could not be read."));
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TenSeconds);

        JsonNode result = await forward(ToolNames.GetBattery, null, CancellationToken.None);

        Assert.Equal("The battery could not be read.", (string?)result["error"]);
    }

    [Fact]
    public async Task A_slow_app_comes_back_as_a_timeout_error_object()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, async (tool, args, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new JsonObject();
        });
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => AiMcpModes.Stdio, OfflineTools(), TimeSpan.FromMilliseconds(300));

        JsonNode result = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);

        Assert.Equal("MicaStats did not answer within 0.3 s.", (string?)result["error"]);
    }

    [Fact]
    public async Task The_mode_is_asked_on_every_call()
    {
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 7 }));
        string mode = AiMcpModes.Off;
        ToolInvoker forward = McpBridge.CreateForwarder(name, () => mode, OfflineTools(), TenSeconds);

        JsonNode before = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);
        mode = AiMcpModes.Stdio;
        JsonNode after = await forward(ToolNames.GetLiveStatus, null, CancellationToken.None);

        Assert.Equal(McpBridge.OffMessage, (string?)before["error"]);
        Assert.Equal("{\"cpu\":7}", after.ToJsonString());
    }

    [Fact]
    public async Task The_bridge_server_serves_the_nine_tools_and_follows_config_json_between_calls()
    {
        string config = _env.PathOf("config.json");
        File.WriteAllText(config, "{\"AiMcpMode\":\"Off\"}");
        string name = TestPipeName();
        using ToolPipeServer server = StartServer(name, (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["cpu"] = 7 }));
        await using McpInMemory mcp = await McpInMemory.ConnectAsync(McpBridge.CreateBridgeOptions(config, name, OfflineTools(), TenSeconds));

        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync();
        string off = await mcp.CallTextAsync(ToolNames.GetLiveStatus);
        File.WriteAllText(config, "{\"AiMcpMode\":\"Stdio\"}");
        string on = await mcp.CallTextAsync(ToolNames.GetLiveStatus);

        Assert.Equal(ToolNames.ReadOnly.Count, tools.Count);
        Assert.Equal("MCP is turned off in MicaStats Settings", (string?)JsonNode.Parse(off)!["error"]);
        Assert.Equal("{\"cpu\":7}", on);
    }
}
```

- [ ] **Step 7: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpBridgeTests"`
Expected: build error CS0103 - `McpBridge` does not exist.

- [ ] **Step 8: Create the bridge**

`RunStdio` is the spike's WinEcho `--mcp` path (`StdioServerTransport` inside a WinExe; the SDK opens the raw stdin/stdout handles with UTF-8, verified in its IL), run on the thread pool because `OnStartup` calls it on the WPF thread. Create `Services/Ai/Mcp/McpBridge.cs`:

```csharp
using System.IO;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.History;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// <c>MicaStats.exe --mcp</c>: an MCP server on stdin/stdout for Claude Desktop and Claude Code,
/// which forwards every tool call to the running MicaStats over the tool pipe.
///
/// <para>
/// It runs in its own short-lived process that the MCP client starts and stops: no window, no
/// single-instance mutex, no monitoring. When MicaStats is not running (or is not serving the
/// pipe) the bridge answers from files on disk, so history and slowdown reports still work and
/// live tools say "MicaStats is not running". The MCP setting is read from config.json on
/// every call, so turning MCP off in Settings takes effect for a bridge already running.
/// </para>
/// </summary>
public static class McpBridge
{
    /// <summary>The error every call returns while MCP is Off in Settings.</summary>
    public const string OffMessage = "MCP is turned off in MicaStats Settings";

    /// <summary>How long the bridge waits for the running app to answer one call.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where ConfigService keeps the settings: <c>%APPDATA%\MicaStats\config.json</c>.</summary>
    internal static string DefaultConfigPath => Path.Combine(DiagnosticsLog.DataDir, "config.json");

    /// <summary>
    /// Serves MCP on stdio until the client closes stdin, then returns the process exit code
    /// (0 after a normal end, 1 after a failure, which goes to the diagnostics log). Blocking.
    /// Writes nothing but MCP to stdout.
    /// </summary>
    public static int RunStdio()
    {
        // The SDK writes the raw stdout handle, not Console.Out, so this cannot silence MCP; it
        // only makes sure a stray Console.Write anywhere can never corrupt the stream.
        Console.SetOut(TextWriter.Null);
        try
        {
            // Called on the WPF thread from OnStartup. Blocking that thread on async work that
            // resumes on its SynchronizationContext would deadlock, so the work runs on the pool.
            Task.Run(RunStdioAsync).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("mcp", "The MCP bridge stopped", ex);
            return 1;
        }
    }

    private static async Task RunStdioAsync()
    {
        Func<DateTime> clock = () => DateTime.UtcNow;
        Action<string> warn = message => DiagnosticsLog.Warn("mcp", message);
        var offline = new MicaTools(
            new OfflineMicaData(new HistoryStore(HistoryStore.DefaultFolder, clock, warn), SlowdownRecorder.ReportDir, clock),
            Redactor.ForCurrentUser());
        McpServerOptions options = CreateBridgeOptions(DefaultConfigPath, ToolPipeProtocol.DefaultPipeName(), offline, CallTimeout);

        await using var transport = new StdioServerTransport(McpToolSet.ServerName);
        await using McpServer server = McpServer.Create(transport, options);
        DiagnosticsLog.Log("mcp", "MCP bridge started");
        await server.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The bridge's server options: the nine read-only tools over a forwarder that reads the mode
    /// from <paramref name="configPath"/> on every call. Separate from <see cref="RunStdio"/> so
    /// tests can drive the same server over in-memory streams.
    /// </summary>
    internal static McpServerOptions CreateBridgeOptions(string configPath, string pipeName, MicaTools offline, TimeSpan callTimeout) =>
        McpToolSet.CreateOptions(
            CreateForwarder(pipeName, () => ReadMcpMode(configPath), offline, callTimeout),
            McpToolSet.CurrentVersion);

    /// <summary>
    /// <c>AiMcpMode</c> from config.json: <see cref="AiMcpModes.Stdio"/> or
    /// <see cref="AiMcpModes.Http"/>, and <see cref="AiMcpModes.Off"/> for anything else,
    /// including a missing, locked or unreadable file. Opened with full sharing so the app's
    /// own save is never blocked.
    /// </summary>
    public static string ReadMcpMode(string configPath)
    {
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(nameof(Kil0bitSystemMonitor.Models.AppConfig.AiMcpMode), out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                string? mode = value.GetString();
                if (string.Equals(mode, AiMcpModes.Stdio, StringComparison.Ordinal)) return AiMcpModes.Stdio;
                if (string.Equals(mode, AiMcpModes.Http, StringComparison.Ordinal)) return AiMcpModes.Http;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // Unreadable counts as Off: the bridge never serves data the user may have turned off.
        }
        return AiMcpModes.Off;
    }

    /// <summary>
    /// The bridge's tool invoker. Off (per <paramref name="mode"/>, asked on every call): every
    /// call returns <see cref="OffMessage"/> as an error. Otherwise the call goes to the running
    /// app over <paramref name="pipeName"/>; when nothing answers there, to
    /// <paramref name="offline"/> (the file-backed tools); a timeout, a version mismatch or a
    /// failure inside the app comes back as an error object. Never throws except for
    /// cancellation.
    /// </summary>
    public static ToolInvoker CreateForwarder(string pipeName, Func<string> mode, MicaTools offline, TimeSpan callTimeout)
    {
        return async (tool, args, ct) =>
        {
            string current = mode();
            if (current != AiMcpModes.Stdio && current != AiMcpModes.Http) return ToolJson.Error(OffMessage);

            try
            {
                return await ToolPipeClient.CallAsync(pipeName, tool, args, callTimeout, ct).ConfigureAwait(false);
            }
            catch (ToolPipeUnavailableException)
            {
                return await offline.InvokeAsync(tool, args, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return ToolJson.Error(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ToolJson.Error(ex.Message);
            }
        };
    }
}
```

- [ ] **Step 9: Run the bridge tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpBridgeTests"`
Expected: 25 passed. `Without_the_app_history_and_reports_come_from_disk_and_live_tools_say_so` takes about 3 s: each of its three calls waits out the 1 s connect timeout before falling back to the files.

- [ ] **Step 10: Add the `--mcp` branch to `App.OnStartup`**

In `App.xaml.cs`, replace:

```csharp
                System.Environment.Exit(
                    killResult == Kil0bitSystemMonitor.Services.EndTaskResult.Terminated ? 0 : 1);
                return;
            }

            base.OnStartup(e);
```

with:

```csharp
                System.Environment.Exit(
                    killResult == Kil0bitSystemMonitor.Services.EndTaskResult.Terminated ? 0 : 1);
                return;
            }

            // MicaStats.exe --mcp: Claude Desktop or Claude Code started the MCP stdio bridge.
            //
            // Like --kill it runs before base.OnStartup and the single-instance mutex: the bridge
            // is a second process by design (it reaches the running MicaStats over the tool pipe),
            // so it must not take the mutex, open a window, or start any monitoring. RunStdio
            // blocks until the client closes stdin, and nothing but MCP ever reaches stdout.
            if (Kil0bitSystemMonitor.Services.Ai.Mcp.McpArguments.TryParse(e.Args))
            {
                System.Environment.Exit(Kil0bitSystemMonitor.Services.Ai.Mcp.McpBridge.RunStdio());
                return;
            }

            base.OnStartup(e);
```

- [ ] **Step 11: Run the tool pipe while MCP is set to the stdio bridge**

In `App.Ai.cs`, replace the line `    // AI anchor: members` with:

```csharp
    /// <summary>
    /// The tool pipe the <c>--mcp</c> bridge forwards to. Runs only while the AI settings have
    /// MCP set to the stdio bridge, so a user who never turns MCP on has no pipe at all.
    /// </summary>
    private static Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeServer? s_toolPipe;

    /// <summary>
    /// Starts or stops the tool pipe to match <c>AiMcpMode</c>. Idempotent: called at the end of
    /// <see cref="StartAi"/> and on every AI setting change. A pipe that cannot start (another
    /// session of the same user already serves the name) is logged and tried again at the next
    /// change.
    /// </summary>
    private static void ApplyToolPipe()
    {
        var config = ConfigService?.Config;
        Kil0bitSystemMonitor.Services.Ai.Tools.MicaTools? tools = AiTools;
        if (config == null || tools == null ||
            config.AiMcpMode != Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Stdio)
        {
            if (s_toolPipe != null)
            {
                s_toolPipe.Dispose();
                s_toolPipe = null;
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Tool pipe for the stdio bridge stopped");
            }
            return;
        }
        if (s_toolPipe != null) return;

        try
        {
            var server = new Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeServer(
                Kil0bitSystemMonitor.Services.Ai.Mcp.ToolPipeProtocol.DefaultPipeName(),
                tools.InvokeAsync,
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", message));
            server.Start();
            s_toolPipe = server;
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Tool pipe for the stdio bridge started");
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "The tool pipe for the stdio bridge could not start", ex);
        }
    }

    // AI anchor: members
```

In `App.Ai.cs`, replace the line `        // AI anchor: start` with:

```csharp
        // After AiTools exists (Task 7's code above this line).
        ApplyToolPipe();
        // AI anchor: start
```

In `App.Ai.cs`, replace the line `        // AI anchor: apply` with:

```csharp
        ApplyToolPipe();
        // AI anchor: apply
```

In `App.Ai.cs`, replace the line `        // AI anchor: stop` with:

```csharp
        // Guarded here because this runs before StopAi's own try: a throw would skip the rest
        // of App.OnExit's teardown, including the config flush.
        try
        {
            s_toolPipe?.Dispose();
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping the tool pipe failed", ex);
        }
        s_toolPipe = null;
        // AI anchor: stop
```

- [ ] **Step 12: Build the app**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`
Expected: `Build succeeded`, 0 errors, no new warnings.

- [ ] **Step 13: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1044 + 41 = 1085).

```bash
git add Services/Ai/Mcp/McpArguments.cs Services/Ai/Mcp/McpToolSet.cs Services/Ai/Mcp/McpBridge.cs App.xaml.cs App.Ai.cs tests/Kil0bitSystemMonitor.Tests/McpInMemory.cs tests/Kil0bitSystemMonitor.Tests/McpToolSetTests.cs tests/Kil0bitSystemMonitor.Tests/McpBridgeTests.cs
git commit -F - <<'EOF'
feat(ai): MCP stdio bridge behind MicaStats.exe --mcp

The nine read-only data tools are exposed over MCP, annotated read-only,
non-destructive and closed-world; suggest_action is never listed. The
bridge runs right after the --kill branch, before the mutex and any UI,
and forwards each call over the tool pipe. Without a running app it
answers history and slowdown reports from disk and says MicaStats is
not running for live tools. MCP set to Off in config.json refuses every
call. The running app serves the pipe while MCP is set to Stdio.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 11: Local HTTP MCP host and client config snippets

Local HTTP mode serves the same nine tools from the running app at `http://127.0.0.1:<port>/mcp` using `HttpListener` and the SDK's stateless `StreamableHttpServerTransport` (the spike's Item6), so no ASP.NET Core runtime is needed. Because http.sys hands an IP-bound listener every request whatever its Host header says (probed: `Host: evil.example` reached the app), the host checks loopback caller, Host and Origin (403), the bearer token (401), POST only (405) and a 1 MB body cap (413) itself. `McpConfigSnippets` builds the Claude Desktop JSON and the Claude Code commands for Settings, and `App.Ai.cs` runs the host while `AiMcpMode` is `Http`, creating the token on first use and reporting a busy port through `AiMcpHttpProblem`.

**Files:**
- Create: `Services/Ai/Mcp/McpHttpHost.cs`
- Create: `Services/Ai/Mcp/McpConfigSnippets.cs`
- Modify: `App.Ai.cs` (anchors `members`, `start`, `apply`, `stop`: the HTTP host lifecycle)
- Test: `tests/Kil0bitSystemMonitor.Tests/McpHttpHostTests.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/McpConfigSnippetsTests.cs`

**Interfaces:**
- Consumes:
  - Task 10: `McpToolSet.CreateOptions(ToolInvoker invoke, string serverVersion)`, `McpToolSet.ServerName`, `McpToolSet.CurrentVersion`, `McpArguments.Flag`; Task 9: `ToolInvoker`.
  - Task 6: `ToolNames` (`ReadOnly`, `GetBattery`); Task 7: `App.AiTools`, `MicaTools.InvokeAsync(...)`.
  - Task 1: `SecretStore(string path, Action<string>? warn = null)`, `SecretStore.DefaultPath`, `Has(string)`, `Get(string)`, `Set(string, string)`, `SecretStore.NewToken()`, `SecretNames.McpToken`, `AiMcpModes.Http`, `AppConfig.AiMcpMode`, `AppConfig.AiMcpHttpPort` (int, 1024-65535, default 47831).
  - Task 5: `App.Ai.cs` anchors (`members`, `start`, `apply`, `stop`); existing `App.ConfigService`, `DiagnosticsLog`.
- Produces:
  - `public sealed class McpHttpHost : IDisposable { public McpHttpHost(int port, Func<string?> token, Func<McpServerOptions> options, Action<string>? warn = null); public const int MaxBodyBytes = 1_048_576; public bool IsRunning { get; } public bool TryStart(out string? problem); public void Stop(); }` (problem "Port N is in use." for Win32 errors 32 and 183)
  - `public static class McpConfigSnippets { public static string ClaudeDesktopJson(string exePath); public static string ClaudeCodeStdioCommand(string exePath); public static string ClaudeCodeHttpCommand(int port, string token); }`
  - `App.Ai.cs`: `public static string? AiMcpHttpProblem { get; private set; }` (for Settings), private `s_mcpHttp`, `s_mcpHttpPort`, `ApplyMcpHttp()`, `ReadMcpHttpToken()`, `EnsureMcpHttpToken()`.

- [ ] **Step 1: Write the failing snippet and HTTP host tests**

Create `tests/Kil0bitSystemMonitor.Tests/McpConfigSnippetsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The exact text Settings copies for Claude Desktop and Claude Code.</summary>
public class McpConfigSnippetsTests
{
    private const string Exe = @"C:\Program Files\MicaStats\MicaStats.exe";

    [Fact]
    public void Claude_desktop_json_starts_the_exe_with_the_mcp_flag()
    {
        string json = McpConfigSnippets.ClaudeDesktopJson(Exe);

        JsonNode server = JsonNode.Parse(json)!["mcpServers"]!["micastats"]!;
        Assert.Equal(Exe, (string?)server["command"]);
        Assert.Equal("[\"--mcp\"]", server["args"]!.ToJsonString());
        Assert.Contains("\"C:\\\\Program Files\\\\MicaStats\\\\MicaStats.exe\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Claude_desktop_json_keeps_a_thai_folder_name_readable()
    {
        string exe = "C:\\\u0E42\u0E1B\u0E23\u0E41\u0E01\u0E23\u0E21\\MicaStats.exe";

        string json = McpConfigSnippets.ClaudeDesktopJson(exe);

        Assert.Contains("\u0E42\u0E1B\u0E23\u0E41\u0E01\u0E23\u0E21", json);
        Assert.Equal(exe, (string?)JsonNode.Parse(json)!["mcpServers"]!["micastats"]!["command"]);
    }

    [Fact]
    public void Claude_code_stdio_command_quotes_the_path_and_passes_the_flag_after_the_separator()
    {
        Assert.Equal(
            "claude mcp add --scope user micastats -- \"C:\\Program Files\\MicaStats\\MicaStats.exe\" --mcp",
            McpConfigSnippets.ClaudeCodeStdioCommand(Exe));
    }

    [Fact]
    public void Claude_code_http_command_names_the_loopback_url_and_the_bearer_token()
    {
        Assert.Equal(
            "claude mcp add --transport http --scope user micastats http://127.0.0.1:47831/mcp --header \"Authorization: Bearer abc_DEF-123\"",
            McpConfigSnippets.ClaudeCodeHttpCommand(47831, "abc_DEF-123"));
    }
}
```

Create `tests/Kil0bitSystemMonitor.Tests/McpHttpHostTests.cs` (the happy path uses the SDK's `HttpClientTransport` with `AdditionalHeaders`, verified to exist on `HttpClientTransportOptions` in `ModelContextProtocol.Core` 2.2.0):

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// Local HTTP mode on a free loopback port: the SDK's HTTP client for the happy path, raw
/// requests for every refusal. Nothing leaves 127.0.0.1.
/// </summary>
public class McpHttpHostTests
{
    private const string Token = "test-token-0123456789";
    private const string ListTools = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Endpoint(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/mcp";

    private static McpHttpHost StartHost(int port, Func<string?>? token = null)
    {
        ToolInvoker tools = (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool });
        var host = new McpHttpHost(port, token ?? (() => Token), () => McpToolSet.CreateOptions(tools, "1.0.0"));
        Assert.True(host.TryStart(out string? problem), problem);
        return host;
    }

    private static async Task<HttpResponseMessage> SendAsync(int port, HttpMethod method, string? bearer, string body = ListTools,
        string? host = null, string? origin = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(method, Endpoint(port));
        if (method != HttpMethod.Get) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (host != null) request.Headers.Host = host;
        if (origin != null) request.Headers.Add("Origin", origin);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task An_mcp_client_with_the_token_lists_and_calls_the_tools()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint(port)),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        CallToolResult result = await client.CallToolAsync(ToolNames.GetBattery, cancellationToken: cts.Token);

        Assert.True(host.IsRunning);
        Assert.Equal(ToolNames.ReadOnly.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("{\"tool\":\"get_battery\"}", string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task A_request_without_the_right_token_gets_401(string? bearer)
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, bearer);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Without_a_stored_token_every_request_gets_401()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port, token: () => null);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_rebinding_host_header_gets_403_even_with_the_token()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token,
            host: "evil.example:" + port.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("null")]
    public async Task A_foreign_origin_gets_403_even_with_the_token(string origin)
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, origin: origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_gets_405()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Get, Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_one_megabyte_gets_413()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, body: new string('x', McpHttpHost.MaxBodyBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_json_rpc_gets_400()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, body: "not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void A_port_in_use_is_reported_and_nothing_listens()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        try
        {
            int port = ((IPEndPoint)holder.LocalEndpoint).Port;
            using var host = new McpHttpHost(port, () => Token, () => McpToolSet.CreateOptions((t, a, ct) => Task.FromResult<JsonNode>(new JsonObject()), "1.0.0"));

            Assert.False(host.TryStart(out string? problem));
            Assert.Equal("Port " + port.ToString(CultureInfo.InvariantCulture) + " is in use.", problem);
            Assert.False(host.IsRunning);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void Stop_frees_the_port_for_the_next_host()
    {
        int port = FreePort();
        McpHttpHost first = StartHost(port);

        first.Stop();

        Assert.False(first.IsRunning);
        using McpHttpHost second = StartHost(port);
        Assert.True(second.IsRunning);
        first.Dispose();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpHttpHostTests|FullyQualifiedName~McpConfigSnippetsTests"`
Expected: build error CS0246 / CS0103 - `McpHttpHost` and `McpConfigSnippets` do not exist.

- [ ] **Step 3: Create the HTTP host**

Create `Services/Ai/Mcp/McpHttpHost.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Local HTTP mode: MCP (streamable HTTP, stateless) at <c>http://127.0.0.1:&lt;port&gt;/mcp</c>
/// on <see cref="HttpListener"/>, so no ASP.NET Core runtime is needed.
///
/// <para>
/// http.sys delivers every request that reaches 127.0.0.1:port to this listener whatever its
/// Host header says, so the checks here are the whole defence: loopback callers only, a Host
/// and (when sent) an Origin of 127.0.0.1 or localhost, against DNS rebinding from a web page
/// (403), then the bearer token (401), POST only (405) and bodies up to
/// <see cref="MaxBodyBytes"/> (413). Each POST gets a fresh stateless server, so nothing is
/// kept between requests.
/// </para>
/// </summary>
public sealed class McpHttpHost : IDisposable
{
    /// <summary>The largest request body read (1 MB); a larger one is refused with 413.</summary>
    public const int MaxBodyBytes = 1_048_576;

    private const int ErrorSharingViolation = 32;   // a socket already holds the port
    private const int ErrorAlreadyExists = 183;     // another http.sys listener has the prefix

    private readonly int _port;
    private readonly Func<string?> _token;
    private readonly Func<McpServerOptions> _options;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>A host for <paramref name="port"/>; nothing listens until <see cref="TryStart"/>.</summary>
    /// <param name="token">
    /// The bearer token, asked on every request so a token regenerated in Settings applies at
    /// once. Null or empty refuses every request.
    /// </param>
    /// <param name="options">Server options for one request; see <see cref="McpToolSet.CreateOptions"/>.</param>
    /// <param name="warn">Receives failure notes for the diagnostics log: never request bodies, results or the token.</param>
    public McpHttpHost(int port, Func<string?> token, Func<McpServerOptions> options, Action<string>? warn = null)
    {
        _port = port;
        _token = token;
        _options = options;
        _warn = warn ?? (_ => { });
    }

    /// <summary>True between a successful <see cref="TryStart"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _listener is { IsListening: true }; }
    }

    /// <summary>
    /// Starts listening. False, with a sentence for Settings in <paramref name="problem"/>, when
    /// the port is taken ("Port 47831 is in use.") or listening fails otherwise. True (and no
    /// problem) when already running.
    /// </summary>
    public bool TryStart(out string? problem)
    {
        lock (_gate)
        {
            problem = null;
            if (_listener != null) return true;

            string portText = _port.ToString(CultureInfo.InvariantCulture);
            var listener = new HttpListener();
            try
            {
                // 127.0.0.1, not localhost: a localhost prefix makes http.sys listen on every interface.
                listener.Prefixes.Add("http://127.0.0.1:" + portText + "/mcp/");
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                problem = ex.ErrorCode is ErrorSharingViolation or ErrorAlreadyExists
                    ? "Port " + portText + " is in use."
                    : "Local HTTP could not start on port " + portText + ": " + ex.Message;
                return false;
            }
            catch (ArgumentException)
            {
                listener.Close();
                problem = "Port " + portText + " is not a valid port.";
                return false;
            }

            var cts = new CancellationTokenSource();
            _listener = listener;
            _cts = cts;
            _loop = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            return true;
        }
    }

    /// <summary>Stops listening and frees the port. Safe to call repeatedly; <see cref="TryStart"/> may follow.</summary>
    public void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            loop = _loop;
            _listener = null;
            _cts = null;
            _loop = null;
        }
        if (listener == null) return;

        // Not disposed: requests still finishing hold its token.
        cts?.Cancel();
        try { listener.Stop(); } catch (ObjectDisposedException) { }
        listener.Close();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop logs its own failures; stopping must not throw.
        }
    }

    /// <summary>Same as <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !listener.IsListening) return;   // Stop closed the listener
                _warn("Local HTTP MCP could not accept a request: " + ex.GetType().Name);
                continue;
            }
            _ = Task.Run(() => HandleAsync(context, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            if (!IsLocalCaller(request))
            {
                response.StatusCode = 403;
                return;
            }
            if (!HasToken(request.Headers["Authorization"]))
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Bearer");
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // No standalone server-to-client stream and no sessions to delete: clients accept 405.
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            byte[]? body = await ReadBodyAsync(request.InputStream, ct).ConfigureAwait(false);
            if (body == null)
            {
                response.StatusCode = 413;
                return;
            }

            JsonRpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                message = null;
            }
            if (message == null)
            {
                response.StatusCode = 400;
                return;
            }

            await ServeAsync(message, response, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) _warn("A local HTTP MCP request failed: " + ex.GetType().Name);
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }   // too late once the body started
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }   // the caller may be gone already
        }
    }

    /// <summary>Loopback caller, a Host of 127.0.0.1 or localhost on this port, and no foreign Origin.</summary>
    private bool IsLocalCaller(HttpListenerRequest request)
    {
        if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address)) return false;

        // Url is built from the Host header, which a rebinding page controls.
        Uri? url = request.Url;
        if (url == null || url.Port != _port || !IsLoopbackName(url.Host)) return false;

        string? origin = request.Headers["Origin"];
        if (origin == null) return true;   // not a browser, or a same-origin request
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) &&
               (originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps) &&
               IsLoopbackName(originUri.Host);
    }

    private static bool IsLoopbackName(string host) =>
        host == "127.0.0.1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>Bearer</c> header equal to the current token, compared in constant time.</summary>
    private bool HasToken(string? authorization)
    {
        const string Scheme = "Bearer ";
        if (authorization == null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        string? expected;
        try
        {
            expected = _token();
        }
        catch (Exception ex)
        {
            _warn("The local HTTP MCP token could not be read: " + ex.GetType().Name);
            return false;
        }
        if (string.IsNullOrEmpty(expected)) return false;

        byte[] given = Encoding.UTF8.GetBytes(authorization.Substring(Scheme.Length).Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// The whole body, or null once it passes <see cref="MaxBodyBytes"/>. Reads rather than
    /// trusting Content-Length, which a chunked request does not send.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>One stateless MCP exchange: a fresh transport and server for this request only.</summary>
    private async Task ServeAsync(JsonRpcMessage message, HttpListenerResponse response, CancellationToken ct)
    {
        var transport = new StreamableHttpServerTransport { Stateless = true };
        McpServer server = McpServer.Create(transport, _options());
        try
        {
            _ = server.RunAsync(ct);
            bool wrote = await transport.HandlePostRequestAsync(message, response.OutputStream, _ =>
            {
                // Called before the first byte of the reply, while headers can still change.
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache";
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);

            // A notification or a response from the client: accepted, nothing to send back.
            if (!wrote) response.StatusCode = 202;
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Create the config snippets**

Create `Services/Ai/Mcp/McpConfigSnippets.cs`:

```csharp
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The text behind Settings' Copy buttons: how Claude Desktop and Claude Code reach MicaStats.
/// Built here rather than in the window so the exact strings are tested.
/// </summary>
public static class McpConfigSnippets
{
    /// <summary>
    /// A <c>claude_desktop_config.json</c> fragment that starts <paramref name="exePath"/> with
    /// <c>--mcp</c>. Real JSON (backslashes escaped), indented, with non-ASCII folder names kept
    /// readable.
    /// </summary>
    public static string ClaudeDesktopJson(string exePath)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpToolSet.ServerName] = new JsonObject
                {
                    ["command"] = exePath,
                    ["args"] = new JsonArray(McpArguments.Flag),
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// The Claude Code command that adds the stdio bridge for every project of this user. The
    /// <c>--</c> keeps Claude Code from reading <c>--mcp</c> as one of its own options.
    /// </summary>
    public static string ClaudeCodeStdioCommand(string exePath) =>
        "claude mcp add --scope user " + McpToolSet.ServerName + " -- \"" + exePath + "\" " + McpArguments.Flag;

    /// <summary>The Claude Code command that adds local HTTP mode, bearer token included.</summary>
    public static string ClaudeCodeHttpCommand(int port, string token) =>
        "claude mcp add --transport http --scope user " + McpToolSet.ServerName +
        " http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/mcp" +
        " --header \"Authorization: Bearer " + token + "\"";
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~McpHttpHostTests|FullyQualifiedName~McpConfigSnippetsTests"`
Expected: 18 passed (14 host + 4 snippets). No URL ACL or elevation is needed for a `127.0.0.1` prefix.

- [ ] **Step 6: Run the HTTP host while MCP is set to Local HTTP**

In `App.Ai.cs`, replace the line `    // AI anchor: members` with:

```csharp
    /// <summary>The local HTTP MCP host; runs only while MCP is set to Local HTTP.</summary>
    private static Kil0bitSystemMonitor.Services.Ai.Mcp.McpHttpHost? s_mcpHttp;

    /// <summary>The port <see cref="s_mcpHttp"/> listens on, so a port change restarts it.</summary>
    private static int s_mcpHttpPort;

    /// <summary>
    /// Why local HTTP mode is not serving, for example "Port 47831 is in use.", or null while it
    /// serves or is not chosen. Settings shows it under the MCP choice.
    /// </summary>
    public static string? AiMcpHttpProblem { get; private set; }

    /// <summary>
    /// Starts, restarts (the port changed) or stops the local HTTP host to match the AI
    /// settings. Idempotent: called at the end of <see cref="StartAi"/> and on every AI setting
    /// change, so a port that was busy is tried again at the next change.
    /// </summary>
    private static void ApplyMcpHttp()
    {
        var config = ConfigService?.Config;
        Kil0bitSystemMonitor.Services.Ai.Tools.MicaTools? tools = AiTools;
        bool wanted = config != null && tools != null &&
                      config.AiMcpMode == Kil0bitSystemMonitor.Services.Ai.AiMcpModes.Http;
        int port = config?.AiMcpHttpPort ?? 0;

        if (s_mcpHttp != null && (!wanted || port != s_mcpHttpPort))
        {
            s_mcpHttp.Dispose();
            s_mcpHttp = null;
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp", "Local HTTP MCP stopped");
        }
        if (!wanted || tools == null)
        {
            AiMcpHttpProblem = null;
            return;
        }
        if (s_mcpHttp != null) return;

        try
        {
            EnsureMcpHttpToken();
            string version = Kil0bitSystemMonitor.Services.Ai.Mcp.McpToolSet.CurrentVersion;
            Kil0bitSystemMonitor.Services.Ai.Mcp.ToolInvoker invoke = tools.InvokeAsync;
            var host = new Kil0bitSystemMonitor.Services.Ai.Mcp.McpHttpHost(
                port,
                ReadMcpHttpToken,
                () => Kil0bitSystemMonitor.Services.Ai.Mcp.McpToolSet.CreateOptions(invoke, version),
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", message));
            if (host.TryStart(out string? problem))
            {
                s_mcpHttp = host;
                s_mcpHttpPort = port;
                AiMcpHttpProblem = null;
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Log("mcp",
                    "Local HTTP MCP listening on 127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                host.Dispose();
                // Logged once per new problem, not again at every settings change while it lasts.
                if (problem != AiMcpHttpProblem)
                    Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("mcp", "Local HTTP MCP did not start: " + problem);
                AiMcpHttpProblem = problem;
            }
        }
        catch (Exception ex)
        {
            AiMcpHttpProblem = "Local HTTP could not start. The diagnostics log has the details.";
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Local HTTP MCP could not start", ex);
        }
    }

    /// <summary>
    /// The local HTTP bearer token, read afresh for every request so a token regenerated in
    /// Settings applies at once. Null when it cannot be read, which refuses the request.
    /// </summary>
    private static string? ReadMcpHttpToken()
    {
        try
        {
            return new Kil0bitSystemMonitor.Services.Ai.SecretStore(Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath)
                .Get(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates the local HTTP bearer token the first time local HTTP mode starts.</summary>
    private static void EnsureMcpHttpToken()
    {
        var secrets = new Kil0bitSystemMonitor.Services.Ai.SecretStore(
            Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath,
            message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", message));
        if (!secrets.Has(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken))
            secrets.Set(Kil0bitSystemMonitor.Services.Ai.SecretNames.McpToken,
                Kil0bitSystemMonitor.Services.Ai.SecretStore.NewToken());
    }

    // AI anchor: members
```

In `App.Ai.cs`, replace the line `        // AI anchor: start` with:

```csharp
        ApplyMcpHttp();
        // AI anchor: start
```

In `App.Ai.cs`, replace the line `        // AI anchor: apply` with:

```csharp
        ApplyMcpHttp();
        // AI anchor: apply
```

In `App.Ai.cs`, replace the line `        // AI anchor: stop` with:

```csharp
        // Guarded for the same reason as the tool pipe above: StopAi must never throw.
        try
        {
            s_mcpHttp?.Dispose();
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("mcp", "Stopping local HTTP MCP failed", ex);
        }
        s_mcpHttp = null;
        // AI anchor: stop
```

- [ ] **Step 7: Build the app**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`
Expected: `Build succeeded`, 0 errors, no new warnings.

- [ ] **Step 8: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1085 + 18 = 1103).

```bash
git add Services/Ai/Mcp/McpHttpHost.cs Services/Ai/Mcp/McpConfigSnippets.cs App.Ai.cs tests/Kil0bitSystemMonitor.Tests/McpHttpHostTests.cs tests/Kil0bitSystemMonitor.Tests/McpConfigSnippetsTests.cs
git commit -F - <<'EOF'
feat(ai): local HTTP MCP host and client config snippets

MCP over streamable HTTP in stateless mode on HttpListener at
127.0.0.1, so no ASP.NET Core runtime is needed. http.sys delivers
every request on the port whatever its Host header, so the host itself
refuses non-loopback callers and foreign Host or Origin headers (403),
a missing or wrong bearer token (401), anything but POST (405) and
bodies over 1 MB (413). The app runs it while MCP is set to Local HTTP,
creates the token on first use and reports a busy port for Settings.
The snippets give the Claude Desktop JSON and Claude Code commands.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 12: Ask MicaStats window, suggested actions, hotkey and menu item

The *Ask MicaStats* window: a conversation view that renders what `AiAssistant` streams (text, a *Details* line naming each tool used with its arguments, suggestion buttons, a limited-mode note), with Send, Stop, New conversation, and Retry / Open Settings when something fails; the question stays in the box after any failure. A suggestion button runs only on a click, through `SuggestedActionRunner`, which checks a process target again (same PID, same start time, same name, the critical-process guard, never MicaStats itself) before `ProcessControl` ends it. The window is opened by a new hotkey (registered only while the assistant is on) and an overlay menu item shown only while it is on. The window takes its assistant as a delegate, so tests drive it with a scripted stream, and once with a real `AiAssistant` over a fake `IChatClient`.

**Files:**
- Create: `Ai/SuggestedActionRunner.cs`, `Ai/AskSetup.cs`, `Ai/AskTurnView.cs`, `Ai/AskWindow.xaml`, `Ai/AskWindow.xaml.cs`
- Modify: `App.Ai.cs` (members anchor: `AiSecrets`, `AiUsage`, `OpenAsk`, `CreateAskSetup`; apply anchor: close the window when the assistant is switched off)
- Modify: `Services/Capture/CaptureHotkeys.cs` (4th ctor parameter, `Plan`, AI shortcut)
- Modify: `App.xaml.cs` (`CaptureHotkeys` construction and the hotkey `PropertyChanged` filter)
- Modify: `OverlayWindow.cs` (menu item 1013 "Ask MicaStats…", `AskMenuText`)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiSuggestedActionRunnerTests.cs`, `tests/Kil0bitSystemMonitor.Tests/UiPump.cs`, `tests/Kil0bitSystemMonitor.Tests/AiAskWindowTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiHotkeyTests.cs`

**Interfaces:**
- Consumes (Task 1): `AppConfig.AiAssistantEnabled`, `AppConfig.AiHotkey` ("Ctrl+Alt+A", null → ""), `AppConfig.AiDailyLimit`; `public sealed class SecretStore { public SecretStore(string path, Action<string>? warn = null); public static string DefaultPath { get; } }`.
- Consumes (Task 4/5): `public static HistoryStore History { get; }`; `public HistoryStore(string folder, Func<DateTime> utcClock, Action<string>? warn = null)`; `AiTestEnv.Root`, `AiTestEnv.PathOf(string)`, `AiTestEnv.Clock.UtcNow`, `AiTestEnv.Dispose()`; `App.Ai.cs` anchors `    // AI anchor: members` and `        // AI anchor: apply`.
- Consumes (Tasks 6-7): `public sealed class MicaTools { public MicaTools(IMicaData data, Redactor redactor); }`; `public sealed class OfflineMicaData : IMicaData { public OfflineMicaData(HistoryStore store, string reportDir, Func<DateTime> utcClock); }`; `public Redactor(string userProfile, string userName, string machineName)`; `public static MicaTools? AiTools { get; }` (App).
- Consumes (Task 8): `public enum SuggestedActionKind { EndProcess, RecordSlowdown, OpenDiagnostics, OpenProcessWindow }`; `public sealed record SuggestedAction(SuggestedActionKind Kind, string Label, string Reason, int? Pid = null, long? CreateTime = null, string? ProcessName = null)`; `public sealed class AiConversation { List<ChatMessage> Messages; List<SuggestedAction> Suggestions; void Clear(); }`; `public enum AssistantUpdateKind { Text, ToolUsed, Suggestion, LimitedMode, Error, Done }`; `public sealed record AssistantUpdate(AssistantUpdateKind Kind, string? Text = null, string? ToolName = null, string? ToolArgs = null, SuggestedAction? Suggestion = null)`; `public sealed class UsageMeter { public UsageMeter(string path, Func<DateTime> localClock); public static string DefaultPath { get; } public int UsedToday { get; } }`; `public sealed record AiClientResult(IChatClient? Client, string? Problem, bool IsClaude)`; `AiProviderFactory.Create(AppConfig config, SecretStore secrets, HttpMessageHandler? handler = null)`; `AiErrorText.Describe(Exception ex)`; `public sealed class AiAssistantOptions { MaxToolRounds; MaxOutputTokens; Func<int> DailyLimit }`; `public AiAssistant(IChatClient client, bool isClaude, MicaTools tools, UsageMeter usage, AiAssistantOptions options)`; `IAsyncEnumerable<AssistantUpdate> AskAsync(AiConversation conversation, string question, CancellationToken ct)`.
- Consumes (existing): `ProcessControl.TryEndTask(int pid, long createTime, string name, out string message)` → `EndTaskResult`; `ProcessControl.IsCriticalProcess(string name)`; `ProcessSampler.SnapshotOnce()` → `IReadOnlyList<ProcessSampler.RawProcess>` (`RawProcess(int Pid, int ParentPid, string Name, long CreateTime, double CpuSeconds)`); `App.Recorder`, `SlowdownRecorder.Capture(SlowdownCause)`, `DiagnosticsWindow.ShowDiagnostics(int)`, `TaskManagerWindow.ShowOrActivate(ProcessSampler)`, `App.ShowSettingsSection(string)`, `HotkeyParser.TryParse/Describe`.
- Produces: `public partial class AskWindow : Window { public static AskWindow ShowOrActivate(string? question = null); public static AskWindow? Current { get; } }`; `public static class SuggestedActionRunner { public static string Run(SuggestedAction action); }`; `public delegate IAsyncEnumerable<AssistantUpdate> AskStream(AiConversation conversation, string question, CancellationToken ct)`; `public sealed record AskSetup(AskStream? Ask, string? Problem, IDisposable? Resource = null)`; `App.OpenAsk(string? question = null)` (public static); `internal static SecretStore App.AiSecrets`; `internal static UsageMeter App.AiUsage`; `CaptureHotkeys(Dispatcher dispatcher, Func<AppConfig?> config, Action openPad, Action openAi)`; settings section tag `"AI"` opened by the window's Open Settings button (Task 14 adds the section).

#### Part A: the suggested-action runner

- [ ] **Step 1: Write the failing runner tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiSuggestedActionRunnerTests.cs`:

```csharp
using System.Collections.Generic;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// A suggestion button re-checks its target on click. These drive the runner through a fake
    /// host, so no real process is looked up or ended.
    /// </summary>
    public class AiSuggestedActionRunnerTests
    {
        private sealed class FakeHost : ISuggestedActionHost
        {
            public readonly List<ProcessSampler.RawProcess> Running = new();
            public readonly List<(int Pid, long CreateTime, string Name)> Ended = new();
            public EndTaskResult Result = EndTaskResult.Terminated;
            public string Message = "Ended chrome.exe.";
            public int Recorded;
            public int DiagnosticsOpened;
            public int ProcessWindowsOpened;

            public int OwnProcessId => 4242;

            public IReadOnlyList<ProcessSampler.RawProcess> Processes() => Running;

            public EndTaskResult EndProcess(int pid, long createTime, string name, out string message)
            {
                Ended.Add((pid, createTime, name));
                message = Message;
                return Result;
            }

            public string RecordSlowdown()
            {
                Recorded++;
                return "Saved slowdown-20260930-101500.txt. It is listed in Diagnostics > Slowdowns.";
            }

            public void OpenDiagnostics() => DiagnosticsOpened++;

            public void OpenProcessWindow() => ProcessWindowsOpened++;
        }

        private static FakeHost HostWith(params (int Pid, string Name, long CreateTime)[] processes)
        {
            var host = new FakeHost();
            foreach (var p in processes)
                host.Running.Add(new ProcessSampler.RawProcess(p.Pid, 1, p.Name, p.CreateTime, 0));
            return host;
        }

        private static SuggestedAction End(string name, int? pid, long? createTime) =>
            new(SuggestedActionKind.EndProcess, "End " + name, "It is using most of the CPU", pid, createTime, name);

        [Fact]
        public void Ends_the_process_when_pid_start_time_and_name_still_match()
        {
            var host = HostWith((1234, "chrome.exe", 555));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("Ended chrome.exe.", message);
            Assert.Equal(new[] { (1234, 555L, "chrome.exe") }, host.Ended);
        }

        [Fact]
        public void Refuses_when_the_pid_now_has_a_different_start_time()
        {
            var host = HostWith((1234, "chrome.exe", 999));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("chrome.exe (PID 1234) is no longer running, or its PID now belongs to another process. Nothing was ended.", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_when_the_pid_now_belongs_to_a_different_program()
        {
            var host = HostWith((1234, "notepad.exe", 555));

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("PID 1234 is now notepad.exe, not chrome.exe. Nothing was ended.", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_a_core_windows_process_without_trying()
        {
            var host = HostWith((700, "lsass.exe", 555));

            string message = SuggestedActionRunner.Run(End("lsass.exe", 700, 555), host);

            Assert.StartsWith("lsass.exe is a core Windows process.", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_without_an_exact_target()
        {
            var host = HostWith((1234, "chrome.exe", 555));

            string noPid = SuggestedActionRunner.Run(End("chrome.exe", null, null), host);
            string noStart = SuggestedActionRunner.Run(End("chrome.exe", 1234, null), host);

            Assert.StartsWith("This suggestion does not say exactly which process to end", noPid);
            Assert.StartsWith("This suggestion does not say exactly which process to end", noStart);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Refuses_to_end_micastats_itself()
        {
            var host = HostWith((4242, "MicaStats.exe", 555));

            string message = SuggestedActionRunner.Run(End("MicaStats.exe", 4242, 555), host);

            Assert.StartsWith("That is MicaStats itself", message);
            Assert.Empty(host.Ended);
        }

        [Fact]
        public void Access_denied_points_to_the_process_window()
        {
            var host = HostWith((1234, "chrome.exe", 555));
            host.Result = EndTaskResult.AccessDenied;
            host.Message = "chrome.exe runs at a higher privilege level than MicaStats, which is unelevated.";

            string message = SuggestedActionRunner.Run(End("chrome.exe", 1234, 555), host);

            Assert.Equal("chrome.exe runs at a higher privilege level than MicaStats, which is unelevated."
                         + " To end it anyway, open Processes, select it, press End task, then Retry as administrator.", message);
        }

        [Fact]
        public void The_other_kinds_go_to_the_host()
        {
            var host = new FakeHost();

            string recorded = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.RecordSlowdown, "Record a slowdown now", "It just stalled"), host);
            string diagnostics = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.OpenDiagnostics, "Open Diagnostics", "See the reports"), host);
            string processes = SuggestedActionRunner.Run(new SuggestedAction(SuggestedActionKind.OpenProcessWindow, "Open Processes", "See every process"), host);

            Assert.Equal("Saved slowdown-20260930-101500.txt. It is listed in Diagnostics > Slowdowns.", recorded);
            Assert.Equal("Opened Diagnostics.", diagnostics);
            Assert.Equal("Opened Processes.", processes);
            Assert.Equal(1, host.Recorded);
            Assert.Equal(1, host.DiagnosticsOpened);
            Assert.Equal(1, host.ProcessWindowsOpened);
            Assert.Empty(host.Ended);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiSuggestedActionRunnerTests"`
Expected: build error — the namespace `Kil0bitSystemMonitor.Ai` and the types `ISuggestedActionHost` and `SuggestedActionRunner` do not exist.

- [ ] **Step 3: Write the runner**

Create `Ai/SuggestedActionRunner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Everything a suggestion may do to the machine, behind an interface so the checks in
    /// <see cref="SuggestedActionRunner"/> can be tested with a fake. The live implementation
    /// goes through <see cref="ProcessControl"/> and the existing windows.
    /// </summary>
    internal interface ISuggestedActionHost
    {
        /// <summary>This process, which a suggestion must never end.</summary>
        int OwnProcessId { get; }

        /// <summary>Every running process with its start time, read now.</summary>
        IReadOnlyList<ProcessSampler.RawProcess> Processes();

        /// <summary>Ends one process; <see cref="ProcessControl.TryEndTask"/> in the live host.</summary>
        EndTaskResult EndProcess(int pid, long createTime, string name, out string message);

        /// <summary>Saves the slowdown recorder's window and returns the sentence to show.</summary>
        string RecordSlowdown();

        /// <summary>Opens the Diagnostics window.</summary>
        void OpenDiagnostics();

        /// <summary>Opens the process window.</summary>
        void OpenProcessWindow();
    }

    /// <summary>
    /// Runs a suggested action after its button is clicked, and only then.
    ///
    /// <para>
    /// The model proposed the action some seconds or minutes earlier, and its words are not
    /// trusted: a process target is looked up again and must still have the same PID, the same
    /// start time and the same name; the critical-process guard is applied to the name read now,
    /// not the one the model gave; MicaStats never ends itself. What remains goes through the
    /// same <see cref="ProcessControl.TryEndTask"/> the process window uses. The elevation retry
    /// stays in the process window, where the user can see what they are elevating for.
    /// </para>
    /// </summary>
    public static class SuggestedActionRunner
    {
        /// <summary>Runs <paramref name="action"/> against the live machine and returns the sentence to show.</summary>
        public static string Run(SuggestedAction action) => Run(action, LiveHost.Instance);

        /// <summary>Runs <paramref name="action"/> through <paramref name="host"/> (tests pass a fake).</summary>
        internal static string Run(SuggestedAction action, ISuggestedActionHost host)
        {
            switch (action.Kind)
            {
                case SuggestedActionKind.EndProcess:
                    return EndProcess(action, host);
                case SuggestedActionKind.RecordSlowdown:
                    return host.RecordSlowdown();
                case SuggestedActionKind.OpenDiagnostics:
                    host.OpenDiagnostics();
                    return "Opened Diagnostics.";
                case SuggestedActionKind.OpenProcessWindow:
                    host.OpenProcessWindow();
                    return "Opened Processes.";
                default:
                    return "MicaStats does not know how to do that, so nothing was done.";
            }
        }

        private static string EndProcess(SuggestedAction action, ISuggestedActionHost host)
        {
            if (action.Pid is not int pid || action.CreateTime is not long createTime || pid <= 0)
                return "This suggestion does not say exactly which process to end, so nothing was ended. Open Processes to choose one.";

            if (pid == host.OwnProcessId)
                return "That is MicaStats itself, so nothing was ended. Quit it from the overlay menu instead.";

            string pidText = pid.ToString(CultureInfo.InvariantCulture);
            string label = string.IsNullOrWhiteSpace(action.ProcessName) ? "That process" : action.ProcessName!;

            ProcessSampler.RawProcess? live = null;
            foreach (var process in host.Processes())
            {
                if (process.Pid == pid)
                {
                    live = process;
                    break;
                }
            }

            if (live is not { } found || found.CreateTime != createTime)
                return label + " (PID " + pidText + ") is no longer running, or its PID now belongs to another process. Nothing was ended.";

            if (!string.IsNullOrWhiteSpace(action.ProcessName) &&
                !string.Equals(found.Name, action.ProcessName, StringComparison.OrdinalIgnoreCase))
                return "PID " + pidText + " is now " + found.Name + ", not " + action.ProcessName + ". Nothing was ended.";

            // Applied here as well as inside TryEndTask, to the name read just now.
            if (ProcessControl.IsCriticalProcess(found.Name))
                return found.Name + " is a core Windows process. Ending it would stop the machine immediately, so MicaStats will not do it.";

            EndTaskResult result = host.EndProcess(pid, createTime, found.Name, out string message);
            return result == EndTaskResult.AccessDenied
                ? message + " To end it anyway, open Processes, select it, press End task, then Retry as administrator."
                : message;
        }

        /// <summary>The real machine: the kernel process list, ProcessControl and the app's windows.</summary>
        private sealed class LiveHost : ISuggestedActionHost
        {
            public static readonly LiveHost Instance = new();

            public int OwnProcessId => Environment.ProcessId;

            public IReadOnlyList<ProcessSampler.RawProcess> Processes() => ProcessSampler.SnapshotOnce();

            public EndTaskResult EndProcess(int pid, long createTime, string name, out string message) =>
                ProcessControl.TryEndTask(pid, createTime, name, out message);

            public string RecordSlowdown()
            {
                var recorder = App.Recorder;
                if (recorder == null || !recorder.IsRunning)
                {
                    DiagnosticsWindow.ShowDiagnostics(0);
                    return "Slowdown recording is off, so there is nothing to save. Switch it on in Diagnostics > Slowdowns.";
                }

                string? path = recorder.Capture(SlowdownCause.Manual);
                DiagnosticsWindow.ShowDiagnostics(0);
                return path == null
                    ? "Nothing has been sampled yet. Try again in a few seconds."
                    : "Saved " + Path.GetFileName(path) + ". It is listed in Diagnostics > Slowdowns.";
            }

            public void OpenDiagnostics() => DiagnosticsWindow.ShowDiagnostics();

            public void OpenProcessWindow() => TaskManagerWindow.ShowOrActivate(App.SharedProcessSampler);
        }
    }
}
```

- [ ] **Step 4: Run the runner tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiSuggestedActionRunnerTests"`
Expected: 8 passed.

#### Part B: the window

- [ ] **Step 5: Add the UI pump test helper**

Create `tests/Kil0bitSystemMonitor.Tests/UiPump.cs`:

```csharp
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Waits for a task on the UI test thread while that thread's dispatcher keeps running, so
    /// the task's awaits (which resume on the dispatcher) can finish. Call inside
    /// <see cref="UiThread.Run"/>; a plain Wait there would deadlock.
    /// </summary>
    internal static class UiPump
    {
        /// <summary>Pumps until <paramref name="task"/> completes, then rethrows its failure if any.</summary>
        public static void Wait(Task task, int timeoutMs = 10_000)
        {
            var frame = new DispatcherFrame();
            Task.WhenAny(task, Task.Delay(timeoutMs))
                .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);

            Assert.True(task.IsCompleted,
                "The task did not finish within " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms");
            task.GetAwaiter().GetResult();
        }
    }
}
```

- [ ] **Step 6: Write the failing window tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiAskWindowTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Kil0bitSystemMonitor.Services.History;
using Microsoft.Extensions.AI;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Ask MicaStats window, built for real on the UI test thread but never shown. Most tests
    /// feed it a scripted stream of assistant updates; the last one runs a real AiAssistant over a
    /// fake model. No provider, network or %APPDATA% is involved.
    /// </summary>
    public class AiAskWindowTests
    {
        private static readonly SuggestedAction EndChrome = new(
            SuggestedActionKind.EndProcess, "End chrome.exe", "It has used most of the CPU for ten minutes", 1234, 555L, "chrome.exe");

        /// <summary>Scripted setups, the questions asked, and what the runner was handed.</summary>
        private sealed class Harness
        {
            public readonly Queue<AskSetup> Setups = new();
            public readonly List<string> Questions = new();
            public readonly List<SuggestedAction> Ran = new();
            public int SettingsOpened;

            public AskSetup Answer(params AssistantUpdate[] updates) =>
                new((conversation, question, ct) =>
                {
                    Questions.Add(question);
                    return Play(updates, ct);
                }, null);

            public AskWindow Build() => new(
                () => Setups.Dequeue(),
                () => SettingsOpened++,
                action =>
                {
                    Ran.Add(action);
                    return "Ended chrome.exe.";
                });
        }

        private static void WithWindow(Action<AskWindow, Harness> test) => UiThread.Run(() =>
        {
            var harness = new Harness();
            var window = harness.Build();
            try
            {
                test(window, harness);
            }
            finally
            {
                window.Close();
            }
        });

        private static void Click(UIElement element) =>
            element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void Send(AskWindow window, string question)
        {
            window.QuestionBox.Text = question;
            Click(window.SendButton);
            UiPump.Wait(window.Pending!);
        }

        private static async IAsyncEnumerable<AssistantUpdate> Play(
            AssistantUpdate[] updates, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        private static async IAsyncEnumerable<AssistantUpdate> Hang(
            TaskCompletionSource reached, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AssistantUpdate(AssistantUpdateKind.Text, "Partial");
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new AssistantUpdate(AssistantUpdateKind.Done);
        }

        [Fact]
        public void A_streamed_answer_fills_one_turn_and_clears_the_box() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "The CPU "),
                new AssistantUpdate(AssistantUpdateKind.Text, "is fine."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "How is the CPU?");

            var turn = Assert.Single(window.Turns);
            Assert.Equal("How is the CPU?", turn.Question.Text);
            Assert.Equal("The CPU is fine.", turn.Answer.Text);
            Assert.Equal(Visibility.Collapsed, turn.Details.Visibility);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
            Assert.Equal(new[] { "How is the CPU?" }, h.Questions);
        });

        [Fact]
        public void Tools_used_are_listed_on_the_details_line() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_live_status"),
                new AssistantUpdate(AssistantUpdateKind.ToolUsed, ToolName: "get_top_processes", ToolArgs: "{\"by\":\"cpu\",\"count\":5}"),
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is the busiest."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "What is busy?");

            var turn = Assert.Single(window.Turns);
            Assert.Equal(Visibility.Visible, turn.Details.Visibility);
            Assert.Equal("Details: get_live_status; get_top_processes {\"by\":\"cpu\",\"count\":5}", turn.Details.Text);
            Assert.Equal("Chrome is the busiest.", turn.Answer.Text);
        });

        [Fact]
        public void A_suggestion_runs_only_when_clicked_and_goes_through_the_runner() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is using most of the CPU."),
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: EndChrome),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Why is it slow?");

            var button = Assert.Single(window.Turns[0].ActionButtons);
            Assert.Equal("End chrome.exe", button.Content);
            Assert.Empty(h.Ran);
            Assert.Equal("Suggestions do nothing until you click them.", window.StatusText.Text);

            Click(button);

            Assert.Equal(new[] { EndChrome }, h.Ran);
            Assert.Equal("Ended chrome.exe.", window.StatusText.Text);
        });

        [Fact]
        public void New_conversation_clears_the_transcript_and_disarms_old_suggestions() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "Chrome is using most of the CPU."),
                new AssistantUpdate(AssistantUpdateKind.Suggestion, Suggestion: EndChrome),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            Send(window, "Why is it slow?");
            var oldButton = window.Turns[0].ActionButtons[0];

            Click(window.NewButton);

            Assert.Empty(window.Turns);
            Assert.Empty(window.TranscriptPanel.Children);
            Assert.Equal(Visibility.Visible, window.EmptyText.Visibility);

            Click(oldButton);

            Assert.Empty(h.Ran);
            Assert.StartsWith("That suggestion belongs to a conversation that was cleared", window.StatusText.Text);
        });

        [Fact]
        public void A_setup_problem_offers_settings_and_keeps_the_question() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(new AskSetup(null, "Add an API key in Settings > AI."));

            Send(window, "Hello?");

            Assert.Empty(window.Turns);
            Assert.Equal("Add an API key in Settings > AI.", window.StatusText.Text);
            Assert.Equal(Visibility.Visible, window.SettingsButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.Equal("Hello?", window.QuestionBox.Text);

            Click(window.SettingsButton);

            Assert.Equal(1, h.SettingsOpened);
        });

        [Fact]
        public void An_error_offers_retry_and_keeps_the_question_until_an_answer_arrives() => WithWindow((window, h) =>
        {
            const string failure = "MicaStats could not reach the provider. Check the connection and try again.";
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Error, failure),
                new AssistantUpdate(AssistantUpdateKind.Done)));
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "The disk has 40 GB free."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Is the disk full?");

            Assert.Equal(failure, window.StatusText.Text);
            Assert.Equal(failure, window.Turns[0].Note.Text);
            Assert.Equal(Visibility.Visible, window.RetryButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SettingsButton.Visibility);
            Assert.Equal("Is the disk full?", window.QuestionBox.Text);

            Click(window.RetryButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal(2, window.Turns.Count);
            Assert.Equal("The disk has 40 GB free.", window.Turns[1].Answer.Text);
            Assert.Equal(Visibility.Collapsed, window.RetryButton.Visibility);
            Assert.Equal("", window.QuestionBox.Text);
            Assert.Equal(new[] { "Is the disk full?", "Is the disk full?" }, h.Questions);
        });

        [Fact]
        public void Stop_cancels_the_answer_and_keeps_the_question() => WithWindow((window, h) =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Setups.Enqueue(new AskSetup((conversation, question, ct) => Hang(reached, ct), null));

            window.QuestionBox.Text = "Why is it slow?";
            Click(window.SendButton);
            UiPump.Wait(reached.Task);

            Assert.Equal("Partial", window.Turns[0].Answer.Text);
            Assert.True(window.StopButton.IsEnabled);
            Assert.False(window.SendButton.IsEnabled);

            Click(window.StopButton);
            UiPump.Wait(window.Pending!);

            Assert.Equal("Stopped.", window.Turns[0].Note.Text);
            Assert.Equal("Why is it slow?", window.QuestionBox.Text);
            Assert.True(window.SendButton.IsEnabled);
            Assert.False(window.StopButton.IsEnabled);
        });

        [Fact]
        public void Limited_mode_is_marked_on_the_answer() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.LimitedMode, "Limited mode: this model cannot use tools."),
                new AssistantUpdate(AssistantUpdateKind.Text, "Memory looks fine."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            Send(window, "Memory?");

            var turn = window.Turns[0];
            Assert.Equal(Visibility.Visible, turn.Note.Visibility);
            Assert.Equal("Limited mode: this model cannot use tools.", turn.Note.Text);
            Assert.Equal("Memory looks fine.", turn.Answer.Text);
        });

        [Fact]
        public void Explain_asks_its_question_at_once() => WithWindow((window, h) =>
        {
            h.Setups.Enqueue(h.Answer(
                new AssistantUpdate(AssistantUpdateKind.Text, "It hosts Windows services."),
                new AssistantUpdate(AssistantUpdateKind.Done)));

            UiPump.Wait(window.Ask("What is svchost.exe (PID 1234) doing?"));

            Assert.Equal(new[] { "What is svchost.exe (PID 1234) doing?" }, h.Questions);
            Assert.Equal("What is svchost.exe (PID 1234) doing?", window.Turns[0].Question.Text);
            Assert.Equal("It hosts Windows services.", window.Turns[0].Answer.Text);
        });

        /// <summary>A model that answers in two streamed chunks and never asks for a tool.</summary>
        private sealed class TwoChunkClient : IChatClient
        {
            public int Calls;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CPU is fine.")));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Calls++;
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "CPU is ");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "fine.");
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose() { }
        }

        [Fact]
        public void A_real_assistant_streams_its_answer_into_the_window() => UiThread.Run(() =>
        {
            using var env = new AiTestEnv();
            var store = new HistoryStore(env.PathOf("history"), () => env.Clock.UtcNow);
            var tools = new MicaTools(
                new OfflineMicaData(store, env.PathOf("reports"), () => env.Clock.UtcNow),
                new Redactor(@"C:\Users\tester", "tester", "TESTPC"));
            var usage = new UsageMeter(env.PathOf("ai-usage.json"),
                () => new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Local));
            var client = new TwoChunkClient();
            var assistant = new AiAssistant(client, false, tools, usage, new AiAssistantOptions());
            var window = new AskWindow(() => new AskSetup(assistant.AskAsync, null), () => { }, _ => "");
            try
            {
                Send(window, "How is the CPU?");

                var turn = Assert.Single(window.Turns);
                Assert.Equal("CPU is fine.", turn.Answer.Text);
                Assert.True(client.Calls >= 1);
                Assert.Equal(1, usage.UsedToday);
                Assert.Equal("", window.QuestionBox.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
```

- [ ] **Step 7: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiAskWindowTests"`
Expected: build error — `AskSetup`, `AskWindow` and `AskTurnView` do not exist.

- [ ] **Step 8: Add the setup types**

Create `Ai/AskSetup.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// One question to the assistant: the updates it streams back. <see cref="AiAssistant.AskAsync"/>
    /// has exactly this shape; tests pass a scripted stream instead.
    /// </summary>
    public delegate IAsyncEnumerable<AssistantUpdate> AskStream(AiConversation conversation, string question, CancellationToken ct);

    /// <summary>
    /// What one Send needs, built fresh for each question so a provider, model or key change
    /// applies from the next one: a way to ask, or the sentence saying why there is none.
    /// </summary>
    /// <param name="Ask">Asks one question; null when <paramref name="Problem"/> says why not.</param>
    /// <param name="Problem">A user-facing sentence such as "Add an API key in Settings > AI.".</param>
    /// <param name="Resource">Disposed once the answer ends: the provider client built for this question.</param>
    public sealed record AskSetup(AskStream? Ask, string? Problem, IDisposable? Resource = null);
}
```

- [ ] **Step 9: Add the turn view**

Create `Ai/AskTurnView.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services.Ai;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// One question and its answer in the Ask window, built in code: the question, the streamed
    /// answer, a note (limited mode, stopped, an error), the Details line naming each tool used
    /// with its arguments, and the suggested-action buttons.
    /// </summary>
    internal sealed class AskTurnView
    {
        private static readonly Brush Card = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Ink = Frozen(Color.FromArgb(0xE9, 0xED, 0xED, 0xF2));
        private static readonly Brush Muted = Frozen(Color.FromArgb(0x88, 0xED, 0xED, 0xF2));
        private static readonly Brush Amber = Frozen(Color.FromRgb(0xE8, 0xA5, 0x3C));
        private static readonly Brush Cyan = Frozen(Color.FromRgb(0x3F, 0xD2, 0xE4));

        private readonly List<string> _tools = new();

        /// <summary>Builds the visuals for one question.</summary>
        public AskTurnView(string question)
        {
            Question = new TextBlock
            {
                Text = question,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = Cyan,
                Margin = new Thickness(0, 0, 0, 6),
            };
            Answer = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ink };
            Note = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Amber,
                FontSize = 11.5,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Details = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

            var stack = new StackPanel();
            stack.Children.Add(Question);
            stack.Children.Add(Answer);
            stack.Children.Add(Note);
            stack.Children.Add(Details);
            stack.Children.Add(Actions);

            Root = new Border
            {
                Background = Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack,
            };
        }

        /// <summary>The element added to the transcript.</summary>
        public Border Root { get; }

        /// <summary>The question as asked.</summary>
        public TextBlock Question { get; }

        /// <summary>The answer, growing as it streams.</summary>
        public TextBlock Answer { get; }

        /// <summary>Limited mode, Stopped, or an error sentence; collapsed until used.</summary>
        public TextBlock Note { get; }

        /// <summary>"Details: tool args; tool args", collapsed until a tool is used.</summary>
        public TextBlock Details { get; }

        /// <summary>Holds the suggestion buttons.</summary>
        public WrapPanel Actions { get; }

        /// <summary>The suggestion buttons, in the order they arrived.</summary>
        public List<Button> ActionButtons { get; } = new();

        /// <summary>Adds streamed text to the answer.</summary>
        public void AppendText(string text) => Answer.Text += text;

        /// <summary>Records one tool call on the Details line.</summary>
        public void AddTool(string name, string? args)
        {
            _tools.Add(Describe(name, args));
            Details.Text = "Details: " + string.Join("; ", _tools);
            Details.Visibility = Visibility.Visible;
        }

        /// <summary>Shows a note under the answer, replacing any earlier one.</summary>
        public void ShowNote(string text)
        {
            Note.Text = text;
            Note.Visibility = Visibility.Visible;
        }

        /// <summary>Adds a suggestion button; <paramref name="onClick"/> runs only when it is clicked.</summary>
        public Button AddAction(SuggestedAction action, Action onClick)
        {
            var button = new Button
            {
                Content = action.Label,
                ToolTip = action.Reason,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4, 10, 4),
            };
            button.Click += (s, e) => onClick();
            ActionButtons.Add(button);
            Actions.Children.Add(button);
            Actions.Visibility = Visibility.Visible;
            return button;
        }

        /// <summary>A tool and its arguments as the Details line shows them; empty arguments are left out.</summary>
        internal static string Describe(string name, string? args)
        {
            string trimmed = args?.Trim() ?? "";
            return trimmed.Length == 0 || trimmed == "{}" ? name : name + " " + trimmed;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
```

- [ ] **Step 10: Add the window's markup**

Create `Ai/AskWindow.xaml`:

```xml
<Window
    x:Class="Kil0bitSystemMonitor.Ai.AskWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.modernwpf.com/2019"
    Title="Ask MicaStats"
    Width="560" Height="660" MinWidth="380" MinHeight="340"
    WindowStartupLocation="CenterScreen"
    Background="#0E0E13"
    Foreground="#E9EDEDF2"
    FontFamily="Segoe UI Variable Text, Segoe UI"
    ui:ThemeManager.RequestedTheme="Dark"
    UseLayoutRounding="True"
    SnapsToDevicePixels="True">

    <!--
      Ask MicaStats: a conversation about this PC. The window only renders what the assistant
      streams (text, the tools it used, suggestions). Providers, limits and tools live in
      Services/Ai; a suggestion runs only when its button is clicked, through
      SuggestedActionRunner, which checks the target again first.
    -->

    <Grid Margin="16,12,16,14">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <Grid Grid.Row="0" Margin="0,0,0,10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="Ask MicaStats" FontSize="18" FontWeight="SemiBold" />
                <TextBlock Text="Answers come from this PC's live readings, history and reports. Nothing changes until you click a suggestion."
                           FontSize="11.5" Foreground="#88EDEDF2" TextWrapping="Wrap" Margin="0,2,12,0" />
            </StackPanel>
            <Button x:Name="NewButton" Grid.Column="1" Content="New conversation" Click="OnNewConversation"
                    VerticalAlignment="Top" Padding="10,4" />
        </Grid>

        <ScrollViewer x:Name="TranscriptScroll" Grid.Row="1"
                      VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
            <StackPanel>
                <TextBlock x:Name="EmptyText" Foreground="#88EDEDF2" TextWrapping="Wrap" Margin="2,8,2,8"
                           Text="Ask anything about this PC: why it was slow a minute ago, what is using the memory, whether the CPU runs too hot. A question in Thai is answered in Thai." />
                <StackPanel x:Name="TranscriptPanel" />
            </StackPanel>
        </ScrollViewer>

        <Grid Grid.Row="2" Margin="0,8,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="StatusText" FontSize="11.5" Foreground="#B0E9EDF2" TextWrapping="Wrap"
                       VerticalAlignment="Center" />
            <Button x:Name="SettingsButton" Grid.Column="1" Content="Open Settings &gt; AI" Click="OnOpenSettings"
                    Visibility="Collapsed" Margin="8,0,0,0" Padding="10,4" />
            <Button x:Name="RetryButton" Grid.Column="2" Content="Retry" Click="OnRetry"
                    Visibility="Collapsed" Margin="8,0,0,0" Padding="10,4" />
        </Grid>

        <Grid Grid.Row="3">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBox x:Name="QuestionBox" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="38" MaxHeight="140"
                     VerticalScrollBarVisibility="Auto" PreviewKeyDown="OnQuestionKeyDown"
                     ui:ControlHelper.PlaceholderText="Ask about this PC (Enter sends, Shift+Enter adds a line)" />
            <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Bottom" Margin="8,0,0,0">
                <Button x:Name="SendButton" Content="Send" Click="OnSend" Padding="14,6" Margin="0,0,6,0" />
                <Button x:Name="StopButton" Content="Stop" Click="OnStop" Padding="12,6" IsEnabled="False" />
            </StackPanel>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 11: Add the window's code**

Create `Ai/AskWindow.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Ai;

// UseWindowsForms puts System.Windows.Forms in scope, which has its own KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Ask MicaStats: a conversation about this PC.
    ///
    /// <para>
    /// Deliberately thin. Providers, the tool loop, limits and the limited-mode fallback live in
    /// <see cref="AiAssistant"/>; this window renders the updates it streams and never acts on its
    /// own. A suggestion runs only when its button is clicked, through the runner it was given
    /// (<see cref="SuggestedActionRunner.Run(SuggestedAction)"/> in the app).
    /// </para>
    ///
    /// <para>
    /// Everything it needs arrives as delegates, so a test builds it over a scripted stream with
    /// no provider, network or running app. The question stays in the box after any failure.
    /// </para>
    /// </summary>
    public partial class AskWindow : Window
    {
        private const string LimitedModeNote =
            "Limited mode: this model could not use MicaStats' tools, so the answer rests on a short summary of the PC right now.";

        private static AskWindow? s_current;

        private readonly Func<AskSetup> _setup;
        private readonly Action _openSettings;
        private readonly Func<SuggestedAction, string> _runAction;
        private readonly AiConversation _conversation = new();
        private readonly List<AskTurnView> _turns = new();
        private CancellationTokenSource? _cts;

        /// <summary>Raised by New conversation; a suggestion from an earlier conversation no longer runs.</summary>
        private int _generation;

        /// <summary>Builds the window over its collaborators; the app passes the live ones.</summary>
        /// <param name="setup">Builds what one Send needs, or says why it cannot.</param>
        /// <param name="openSettings">Opens Settings on the AI section.</param>
        /// <param name="runAction">Runs a clicked suggestion and returns the sentence to show.</param>
        internal AskWindow(Func<AskSetup> setup, Action openSettings, Func<SuggestedAction, string> runAction)
        {
            InitializeComponent();
            _setup = setup;
            _openSettings = openSettings;
            _runAction = runAction;

            // Closing the window cancels an answer in progress, like Stop.
            Closed += (s, e) =>
            {
                _cts?.Cancel();
                if (ReferenceEquals(s_current, this)) s_current = null;
            };
            UpdateButtons();
        }

        /// <summary>The open window, or null.</summary>
        public static AskWindow? Current => s_current;

        /// <summary>
        /// Shows the window, creating it on first use, and brings it forward. With a
        /// <paramref name="question"/> (an Explain button), asks it at once.
        /// </summary>
        public static AskWindow ShowOrActivate(string? question = null)
        {
            var window = s_current;
            if (window == null)
            {
                window = new AskWindow(App.CreateAskSetup, () => App.ShowSettingsSection("AI"), SuggestedActionRunner.Run);
                s_current = window;
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            if (question != null) window.Ask(question);
            else window.QuestionBox.Focus();
            return window;
        }

        /// <summary>Closes the window if it is open: the assistant was switched off.</summary>
        internal static void CloseIfOpen() => s_current?.Close();

        /// <summary>The turns shown, oldest first.</summary>
        internal IReadOnlyList<AskTurnView> Turns => _turns;

        /// <summary>The latest Send, for tests to wait on.</summary>
        internal Task? Pending { get; private set; }

        /// <summary>True while an answer is streaming.</summary>
        internal bool IsBusy => _cts != null;

        /// <summary>Puts <paramref name="question"/> in the box and sends it, unless an answer is still streaming.</summary>
        internal Task Ask(string question)
        {
            QuestionBox.Text = question;
            if (IsBusy)
            {
                StatusText.Text = "Your question is in the box. Press Send when this answer has finished, or Stop it first.";
                return Task.CompletedTask;
            }
            return StartSend();
        }

        /// <summary>Cancels the answer in progress; the question stays in the box.</summary>
        internal void Stop() => _cts?.Cancel();

        /// <summary>Starts over: clears the transcript and the conversation, and disarms old suggestions.</summary>
        internal void NewConversation()
        {
            _cts?.Cancel();
            _generation++;
            _conversation.Clear();
            _turns.Clear();
            TranscriptPanel.Children.Clear();
            EmptyText.Visibility = Visibility.Visible;
            HideProblem();
            StatusText.Text = "";
        }

        private Task StartSend()
        {
            Pending = SendAsync();
            return Pending;
        }

        private async Task SendAsync()
        {
            string question = QuestionBox.Text.Trim();
            if (question.Length == 0 || IsBusy) return;
            HideProblem();

            AskSetup setup;
            try
            {
                setup = _setup();
            }
            catch (Exception ex)
            {
                ShowProblem(AiErrorText.Describe(ex), offerSettings: true, offerRetry: false);
                return;
            }

            if (setup.Ask is not { } ask)
            {
                setup.Resource?.Dispose();
                ShowProblem(setup.Problem ?? "The assistant is not set up yet. Open Settings > AI.", offerSettings: true, offerRetry: false);
                return;
            }

            AskTurnView turn = AddTurn(question);
            int generation = _generation;
            var cts = new CancellationTokenSource();
            _cts = cts;
            UpdateButtons();
            StatusText.Text = "Thinking\u2026";
            bool failed = false;

            try
            {
                await foreach (AssistantUpdate update in ask(_conversation, question, cts.Token))
                {
                    switch (update.Kind)
                    {
                        case AssistantUpdateKind.Text:
                            if (!string.IsNullOrEmpty(update.Text)) turn.AppendText(update.Text);
                            StatusText.Text = "";
                            break;
                        case AssistantUpdateKind.ToolUsed:
                            if (!string.IsNullOrEmpty(update.ToolName))
                            {
                                turn.AddTool(update.ToolName, update.ToolArgs);
                                StatusText.Text = "Looking up " + update.ToolName + "\u2026";
                            }
                            break;
                        case AssistantUpdateKind.Suggestion:
                            if (update.Suggestion is { } action)
                                turn.AddAction(action, () => RunSuggestion(action, generation));
                            break;
                        case AssistantUpdateKind.LimitedMode:
                            turn.ShowNote(update.Text ?? LimitedModeNote);
                            break;
                        case AssistantUpdateKind.Error:
                            failed = true;
                            string message = update.Text ?? "The assistant could not answer.";
                            turn.ShowNote(message);
                            ShowProblem(message, offerSettings: message.Contains("Settings > AI", StringComparison.Ordinal), offerRetry: true);
                            break;
                        case AssistantUpdateKind.Done:
                            break;
                    }
                    TranscriptScroll.ScrollToEnd();
                }
            }
            catch (OperationCanceledException)
            {
                failed = true;
                turn.ShowNote("Stopped.");
                if (generation == _generation) StatusText.Text = "Stopped. Your question is still in the box.";
            }
            catch (Exception ex)
            {
                failed = true;
                string message = AiErrorText.Describe(ex);
                turn.ShowNote(message);
                ShowProblem(message, offerSettings: false, offerRetry: true);
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                setup.Resource?.Dispose();
                UpdateButtons();
            }

            if (failed || generation != _generation) return;

            // Only the question that was answered is cleared; anything typed meanwhile stays.
            if (QuestionBox.Text.Trim() == question) QuestionBox.Clear();
            if (turn.Answer.Text.Length == 0 && turn.ActionButtons.Count == 0)
                turn.ShowNote("The model sent back no text. Try asking again.");
            StatusText.Text = turn.ActionButtons.Count > 0 ? "Suggestions do nothing until you click them." : "";
        }

        private AskTurnView AddTurn(string question)
        {
            var turn = new AskTurnView(question);
            _turns.Add(turn);
            TranscriptPanel.Children.Add(turn.Root);
            EmptyText.Visibility = Visibility.Collapsed;
            TranscriptScroll.ScrollToEnd();
            return turn;
        }

        private void RunSuggestion(SuggestedAction action, int generation)
        {
            if (generation != _generation)
            {
                StatusText.Text = "That suggestion belongs to a conversation that was cleared, so it does nothing now.";
                return;
            }

            try
            {
                StatusText.Text = _runAction(action);
            }
            catch (Exception ex)
            {
                StatusText.Text = "That did not work: " + ex.Message;
            }
        }

        private void ShowProblem(string message, bool offerSettings, bool offerRetry)
        {
            StatusText.Text = message;
            SettingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = offerRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideProblem()
        {
            SettingsButton.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            SendButton.IsEnabled = !IsBusy;
            StopButton.IsEnabled = IsBusy;
        }

        private void OnSend(object sender, RoutedEventArgs e) => StartSend();

        private void OnStop(object sender, RoutedEventArgs e) => Stop();

        private void OnRetry(object sender, RoutedEventArgs e) => StartSend();

        private void OnNewConversation(object sender, RoutedEventArgs e) => NewConversation();

        private void OnOpenSettings(object sender, RoutedEventArgs e) => _openSettings();

        /// <summary>Enter sends, Shift+Enter adds a line, Escape stops an answer in progress.</summary>
        private void OnQuestionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                StartSend();
            }
            else if (e.Key == Key.Escape && IsBusy)
            {
                e.Handled = true;
                Stop();
            }
        }
    }
}
```

- [ ] **Step 12: Wire the window into the app**

In `App.Ai.cs`, replace the line `    // AI anchor: members` with the block below, which ends with the same anchor line. `AiSecrets` is declared here and nowhere else (Task 11 reads the MCP token through short-lived `SecretStore` instances, which is safe because `SecretStore` re-reads the file on every call); Task 14's Settings panel uses this `AiSecrets`.

```csharp
    // ---- Ask MicaStats -------------------------------------------------------------------

    private static Kil0bitSystemMonitor.Services.Ai.SecretStore? s_aiSecrets;
    private static Kil0bitSystemMonitor.Services.Ai.UsageMeter? s_aiUsage;

    /// <summary>
    /// The one DPAPI secret store (API keys and the MCP token), created on first use. One
    /// instance for the whole app, so a key saved in Settings is the one the next question uses.
    /// </summary>
    internal static Kil0bitSystemMonitor.Services.Ai.SecretStore AiSecrets =>
        System.Threading.LazyInitializer.EnsureInitialized(ref s_aiSecrets, () =>
            new Kil0bitSystemMonitor.Services.Ai.SecretStore(
                Kil0bitSystemMonitor.Services.Ai.SecretStore.DefaultPath,
                message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", message)));

    /// <summary>Today's question count, shared by every Send and Explain; created on first use.</summary>
    internal static Kil0bitSystemMonitor.Services.Ai.UsageMeter AiUsage =>
        System.Threading.LazyInitializer.EnsureInitialized(ref s_aiUsage, () =>
            new Kil0bitSystemMonitor.Services.Ai.UsageMeter(
                Kil0bitSystemMonitor.Services.Ai.UsageMeter.DefaultPath, () => DateTime.Now));

    /// <summary>
    /// Shows Ask MicaStats and, with a <paramref name="question"/>, asks it at once (the Explain
    /// buttons). From the overlay menu, the hotkey and Explain.
    /// </summary>
    public static void OpenAsk(string? question = null)
    {
        try
        {
            Kil0bitSystemMonitor.Ai.AskWindow.ShowOrActivate(question);
        }
        catch (Exception ex)
        {
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("ai", "Opening Ask MicaStats failed", ex);
        }
    }

    /// <summary>
    /// What one Send or Explain needs, built fresh each time so a provider, model or key change
    /// applies from the next question. Never throws: a problem comes back as the sentence to show.
    /// </summary>
    internal static Kil0bitSystemMonitor.Ai.AskSetup CreateAskSetup()
    {
        var config = ConfigService?.Config;
        if (config == null)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "MicaStats is still starting. Try again in a moment.");
        if (!config.AiAssistantEnabled)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "The assistant is off. Turn it on in Settings > AI.");
        var tools = AiTools;
        if (tools == null)
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, "The data tools are not ready yet. Try again in a moment.");

        try
        {
            var result = Kil0bitSystemMonitor.Services.Ai.AiProviderFactory.Create(config, AiSecrets);
            if (result.Client == null)
                return new Kil0bitSystemMonitor.Ai.AskSetup(null,
                    result.Problem ?? "The AI provider could not be set up. Check Settings > AI.");

            var assistant = new Kil0bitSystemMonitor.Services.Ai.AiAssistant(
                result.Client, result.IsClaude, tools, AiUsage,
                new Kil0bitSystemMonitor.Services.Ai.AiAssistantOptions { DailyLimit = () => config.AiDailyLimit });
            return new Kil0bitSystemMonitor.Ai.AskSetup(assistant.AskAsync, null, result.Client);
        }
        catch (Exception ex)
        {
            // The type only: a message could quote a URL or a server reply.
            Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("ai", "Setting up the provider failed (" + ex.GetType().Name + ")");
            return new Kil0bitSystemMonitor.Ai.AskSetup(null, Kil0bitSystemMonitor.Services.Ai.AiErrorText.Describe(ex));
        }
    }

    // AI anchor: members
```

Then, in `App.Ai.cs`, replace the line `        // AI anchor: apply` with:

```csharp
        // Switching the assistant off closes Ask MicaStats, which also stops an answer in
        // progress. The hotkey (App.xaml.cs) and the overlay menu item follow the same switch.
        if (ConfigService?.Config.AiAssistantEnabled != true)
            Current?.Dispatcher.BeginInvoke(new Action(Kil0bitSystemMonitor.Ai.AskWindow.CloseIfOpen));
        // AI anchor: apply
```

- [ ] **Step 13: Run the window tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiAskWindowTests"`
Expected: 10 passed. If `A_real_assistant_streams_its_answer_into_the_window` fails on the usage count or the text, read Task 8's `AiAssistant.AskAsync` before changing this test: the window must show exactly what the assistant streams, and one Send must count once.

#### Part C: hotkey and overlay menu

- [ ] **Step 14: Write the failing hotkey and menu tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiHotkeyTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Which global shortcuts are registered, and the overlay menu's Ask entry. Planned without
    /// any Win32 call, so no test registers a real system-wide hotkey.
    /// </summary>
    public class AiHotkeyTests
    {
        [Fact]
        public void The_ai_shortcut_is_planned_only_while_the_assistant_is_on()
        {
            var off = new AppConfig { AiAssistantEnabled = false, AiHotkey = "Ctrl+Alt+A" };
            Assert.DoesNotContain(CaptureHotkeys.Plan(off), p => p.Target == HotkeyTarget.Ai);

            var on = new AppConfig { AiAssistantEnabled = true, AiHotkey = "Ctrl+Alt+A" };
            var entry = Assert.Single(CaptureHotkeys.Plan(on), p => p.Target == HotkeyTarget.Ai);

            Assert.Equal("Ctrl+Alt+A", entry.Spec);
            Assert.Equal("ai", entry.Area);
            Assert.Equal("Ask MicaStats", entry.Label);
        }

        [Fact]
        public void An_empty_ai_shortcut_plans_nothing()
        {
            var cfg = new AppConfig { AiAssistantEnabled = true, AiHotkey = "" };

            Assert.DoesNotContain(CaptureHotkeys.Plan(cfg), p => p.Target == HotkeyTarget.Ai);
        }

        [Fact]
        public void The_existing_shortcuts_are_planned_as_before()
        {
            var cfg = new AppConfig { CaptureHotkeysEnabled = true, PadHotkey = "Ctrl+Alt+N", AiAssistantEnabled = false };

            var plan = CaptureHotkeys.Plan(cfg);

            Assert.Equal(
                new[] { HotkeyTarget.CaptureRegion, HotkeyTarget.CaptureWindow, HotkeyTarget.CaptureScreen, HotkeyTarget.Pad },
                plan.Select(p => p.Target));
            Assert.Equal(
                new[] { cfg.CaptureHotkeyRegion, cfg.CaptureHotkeyWindow, cfg.CaptureHotkeyFullScreen, "Ctrl+Alt+N" },
                plan.Select(p => p.Spec));
            Assert.Empty(CaptureHotkeys.Plan(new AppConfig { CaptureHotkeysEnabled = false, PadHotkey = "", AiAssistantEnabled = false }));
        }

        [Fact]
        public void The_overlay_menu_offers_ask_only_while_the_assistant_is_on()
        {
            Assert.Null(OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = false }));
            Assert.Equal("Ask MicaStats\u2026\tCtrl+Alt+A",
                OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = true, AiHotkey = "ctrl+alt+a" }));
            Assert.Equal("Ask MicaStats\u2026",
                OverlayWindow.AskMenuText(new AppConfig { AiAssistantEnabled = true, AiHotkey = "" }));
        }
    }
}
```

- [ ] **Step 15: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiHotkeyTests"`
Expected: build error — `CaptureHotkeys.Plan`, `HotkeyTarget` and `OverlayWindow.AskMenuText` do not exist.

- [ ] **Step 16: Plan the shortcuts and register the AI one**

Replace the whole content of `Services/Capture/CaptureHotkeys.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What a registered shortcut does.</summary>
    internal enum HotkeyTarget
    {
        CaptureRegion,
        CaptureWindow,
        CaptureScreen,
        Pad,
        Ai,
    }

    /// <summary>One shortcut <see cref="CaptureHotkeys.Apply"/> registers, decided before any Win32 call.</summary>
    /// <param name="Spec">The combination as configured, in <see cref="HotkeyParser"/> syntax.</param>
    /// <param name="Area">The diagnostics log area its registration is reported under.</param>
    /// <param name="Label">The name the log uses for it.</param>
    /// <param name="Target">What pressing it does.</param>
    internal readonly record struct HotkeyPlan(string? Spec, string Area, string Label, HotkeyTarget Target);

    /// <summary>
    /// System-wide capture shortcuts, via <c>RegisterHotKey</c> on a hidden message window.
    ///
    /// <para>
    /// Registration can legitimately fail — another application may already own the
    /// combination, and Windows itself reserves several. That is reported to the diagnostics log
    /// and the remaining hotkeys still register, rather than the whole feature going quiet with
    /// no explanation.
    /// </para>
    ///
    /// <para>
    /// The same hidden window also carries MicaPad's shortcut and Ask MicaStats'. Each
    /// registration maps to an action. MicaPad's key registers whether or not the capture
    /// shortcuts are switched on; Ask MicaStats' only while the assistant is, so switching the
    /// assistant off frees the combination for other programs.
    /// </para>
    /// </summary>
    public sealed class CaptureHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private readonly Dispatcher _dispatcher;
        private readonly Func<AppConfig?> _config;
        private readonly Dictionary<int, Action> _registered = new();
        private readonly Action _openPad;
        private readonly Action _openAi;
        private HwndSource? _source;
        private int _nextId = 0xA100;

        /// <summary>Creates the hotkey host; nothing registers until <see cref="Apply"/>.</summary>
        /// <param name="dispatcher">The UI dispatcher that capture runs on.</param>
        /// <param name="config">Supplies the current config, read on every Apply and every trigger.</param>
        /// <param name="openPad">Opens MicaPad; queued onto the dispatcher, never run inside the window procedure.</param>
        /// <param name="openAi">Opens Ask MicaStats; queued the same way.</param>
        public CaptureHotkeys(Dispatcher dispatcher, Func<AppConfig?> config, Action openPad, Action openAi)
        {
            _dispatcher = dispatcher;
            _config = config;
            _openPad = openPad;
            _openAi = openAi;
        }

        /// <summary>Registers the configured shortcuts. Safe to call again to re-apply changes.</summary>
        public void Apply()
        {
            Unregister();

            var cfg = _config();
            if (cfg == null) return;

            var plan = Plan(cfg);
            if (plan.Count == 0) return;

            EnsureWindow();
            if (_source == null) return;

            foreach (var entry in plan)
                Register(entry.Spec, entry.Area, entry.Label, ActionFor(entry.Target));
        }

        /// <summary>
        /// The shortcuts <paramref name="cfg"/> asks for, in registration order: the three capture
        /// keys while capture shortcuts are on, MicaPad's when set, and Ask MicaStats' when set
        /// and the assistant is on.
        /// </summary>
        internal static IReadOnlyList<HotkeyPlan> Plan(AppConfig cfg)
        {
            var plan = new List<HotkeyPlan>();
            if (cfg.CaptureHotkeysEnabled)
            {
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyRegion, "capture", nameof(CaptureMode.Region), HotkeyTarget.CaptureRegion));
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyWindow, "capture", nameof(CaptureMode.ActiveWindow), HotkeyTarget.CaptureWindow));
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyFullScreen, "capture", nameof(CaptureMode.Screen), HotkeyTarget.CaptureScreen));
            }

            if (!string.IsNullOrWhiteSpace(cfg.PadHotkey))
                plan.Add(new HotkeyPlan(cfg.PadHotkey, "pad", "MicaPad", HotkeyTarget.Pad));

            if (cfg.AiAssistantEnabled && !string.IsNullOrWhiteSpace(cfg.AiHotkey))
                plan.Add(new HotkeyPlan(cfg.AiHotkey, "ai", "Ask MicaStats", HotkeyTarget.Ai));

            return plan;
        }

        private Action ActionFor(HotkeyTarget target) => target switch
        {
            HotkeyTarget.CaptureRegion => () => CaptureService.Start(CaptureMode.Region, _config(), _dispatcher),
            HotkeyTarget.CaptureWindow => () => CaptureService.Start(CaptureMode.ActiveWindow, _config(), _dispatcher),
            HotkeyTarget.CaptureScreen => () => CaptureService.Start(CaptureMode.Screen, _config(), _dispatcher),
            // Queued, not called: the handler runs inside WndProc, and opening a window there would re-enter it.
            HotkeyTarget.Pad => () => _dispatcher.BeginInvoke(_openPad),
            HotkeyTarget.Ai => () => _dispatcher.BeginInvoke(_openAi),
            _ => () => { },
        };

        private void EnsureWindow()
        {
            if (_source != null) return;
            try
            {
                // A message-only window: never visible, exists solely to receive WM_HOTKEY.
                var parameters = new HwndSourceParameters("MicaStatsHotkeys")
                {
                    Width = 0,
                    Height = 0,
                    ParentWindow = (IntPtr)(-3),   // HWND_MESSAGE
                };
                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                _source = null;
                DiagnosticsLog.Error("capture", "Could not create the hotkey window", ex);
            }
        }

        private void Register(string? spec, string area, string label, Action action)
        {
            if (!HotkeyParser.TryParse(spec, out var mods, out uint vk))
            {
                if (!string.IsNullOrWhiteSpace(spec))
                    DiagnosticsLog.Warn(area, $"Hotkey '{spec}' for {label} is not a valid combination");
                return;
            }

            int id = _nextId++;
            // NOREPEAT: holding the keys down must not fire a stream of actions.
            if (RegisterHotKey(_source!.Handle, id, (uint)(mods | HotkeyModifiers.NoRepeat), vk))
            {
                _registered[id] = action;
                DiagnosticsLog.Log(area, $"Hotkey {HotkeyParser.Describe(mods, vk)} -> {label}");
            }
            else
            {
                DiagnosticsLog.Warn(area, $"Hotkey {spec} for {label} is already taken by another application");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        private void Unregister()
        {
            if (_source == null) return;
            foreach (int id in _registered.Keys)
            {
                try { UnregisterHotKey(_source.Handle, id); } catch { }
            }
            _registered.Clear();
        }

        /// <summary>Unregisters every shortcut and destroys the hidden window.</summary>
        public void Dispose()
        {
            Unregister();
            try
            {
                _source?.RemoveHook(WndProc);
                _source?.Dispose();
            }
            catch { }
            _source = null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
```

- [ ] **Step 17: Pass the Ask action and re-apply on the assistant switch**

In `App.xaml.cs`, replace:

```csharp
            m_captureHotkeys = new Kil0bitSystemMonitor.Services.Capture.CaptureHotkeys(
                Dispatcher, () => m_config?.Config, () => OpenPad(null));
            m_captureHotkeys.Apply();
            config.Config.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null &&
                    (e.PropertyName.StartsWith("CaptureHotkey", StringComparison.Ordinal) ||
                     e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.PadHotkey)))
                    Dispatcher.BeginInvoke(new Action(() => m_captureHotkeys?.Apply()));
            };
```

with:

```csharp
            m_captureHotkeys = new Kil0bitSystemMonitor.Services.Capture.CaptureHotkeys(
                Dispatcher, () => m_config?.Config, () => OpenPad(null), () => OpenAsk(null));
            m_captureHotkeys.Apply();
            config.Config.PropertyChanged += (s, e) =>
            {
                // The Ask MicaStats key registers only while the assistant is on, so the switch
                // re-applies too: turning the assistant off frees the combination.
                if (e.PropertyName != null &&
                    (e.PropertyName.StartsWith("CaptureHotkey", StringComparison.Ordinal) ||
                     e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.PadHotkey) ||
                     e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.AiHotkey) ||
                     e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.AiAssistantEnabled)))
                    Dispatcher.BeginInvoke(new Action(() => m_captureHotkeys?.Apply()));
            };
```

- [ ] **Step 18: Add the overlay menu item**

In `OverlayWindow.cs`, replace:

```csharp
        /// <summary>The tab-separated shortcut column for MicaPad's menu item; empty when no valid hotkey is set.</summary>
        private string PadShortcutLabel() =>
            Services.Capture.HotkeyParser.TryParse(_config.Config.PadHotkey, out var mods, out uint vk)
                ? "\t" + Services.Capture.HotkeyParser.Describe(mods, vk)
                : "";
```

with:

```csharp
        /// <summary>The tab-separated shortcut column for MicaPad's menu item; empty when no valid hotkey is set.</summary>
        private string PadShortcutLabel() =>
            Services.Capture.HotkeyParser.TryParse(_config.Config.PadHotkey, out var mods, out uint vk)
                ? "\t" + Services.Capture.HotkeyParser.Describe(mods, vk)
                : "";

        /// <summary>
        /// The menu's Ask MicaStats entry with its shortcut column, or null while the assistant is
        /// off: the item is left out rather than shown doing nothing.
        /// </summary>
        internal static string? AskMenuText(AppConfig config)
        {
            if (!config.AiAssistantEnabled) return null;
            return Services.Capture.HotkeyParser.TryParse(config.AiHotkey, out var mods, out uint vk)
                ? "Ask MicaStats\u2026\t" + Services.Capture.HotkeyParser.Describe(mods, vk)
                : "Ask MicaStats\u2026";
        }
```

Then replace the line:

```csharp
                    AppendMenu(hMenu, 0, 1012, "MicaPad" + PadShortcutLabel());
```

with:

```csharp
                    AppendMenu(hMenu, 0, 1012, "MicaPad" + PadShortcutLabel());
                    if (AskMenuText(_config.Config) is string askText) AppendMenu(hMenu, 0, 1013, askText);
```

and replace the line:

```csharp
                    else if (ch == 1012) _dispatcher.BeginInvoke(() => App.OpenPad(null));
```

with:

```csharp
                    else if (ch == 1012) _dispatcher.BeginInvoke(() => App.OpenPad(null));
                    else if (ch == 1013) _dispatcher.BeginInvoke(() => App.OpenAsk(null));
```

- [ ] **Step 19: Run the hotkey tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiHotkeyTests"`
Expected: 4 passed.

- [ ] **Step 20: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1103 + 22 = 1125).

```bash
git add Ai/SuggestedActionRunner.cs Ai/AskSetup.cs Ai/AskTurnView.cs Ai/AskWindow.xaml Ai/AskWindow.xaml.cs App.Ai.cs Services/Capture/CaptureHotkeys.cs App.xaml.cs OverlayWindow.cs tests/Kil0bitSystemMonitor.Tests/UiPump.cs tests/Kil0bitSystemMonitor.Tests/AiSuggestedActionRunnerTests.cs tests/Kil0bitSystemMonitor.Tests/AiAskWindowTests.cs tests/Kil0bitSystemMonitor.Tests/AiHotkeyTests.cs
git commit -F - <<'EOF'
feat(ai): Ask MicaStats window, suggested actions, hotkey and menu item

A conversation window that renders the streamed answer, a Details line
naming each tool used with its arguments, a limited-mode note and
suggestion buttons. Send, Stop, New conversation, Retry and Open
Settings; the question stays in the box after any failure. Suggestions
run only on a click, and the runner checks a process again first: same
PID, start time and name, the critical-process guard, never MicaStats
itself. Ctrl+Alt+A and an overlay menu item open it while the assistant
is on.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 13: Explain buttons on slowdown reports, alert toasts and the process window

One-click *Explain* buttons that open Ask MicaStats with a question naming the item, written in the Windows display language (Thai when it is Thai, English otherwise, dates always in the invariant culture so a Thai calendar never stamps 2569). They appear on each saved slowdown report in Diagnostics > Slowdowns, on the alert card (and the "machine just struggled" card for a new report), and as a footer button acting on the selected process in the process window; all of them are absent while the assistant is off, and the two windows follow the switch live.

**Files:**
- Create: `Services/Ai/ExplainQuestions.cs`, `Ai/ExplainActions.cs`, `Ai/ExplainableReport.cs`
- Modify: `AlertToastWindow.cs` (`ShowFor` gains `onExplain`, `Create`, the Explain button)
- Modify: `App.xaml.cs` (the two `AlertToastWindow.ShowFor` call sites in `StartDiagnostics`)
- Modify: `DiagnosticsWindow.xaml`, `DiagnosticsWindow.xaml.cs` (Explain beside Open on slowdown reports)
- Modify: `TaskManagerWindow.xaml`, `TaskManagerWindow.xaml.cs` (Explain footer button)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiExplainTests.cs`

**Interfaces:**
- Consumes (Task 12): `public static void App.OpenAsk(string? question = null)`.
- Consumes (Task 7): `internal static bool SlowdownReportFiles.IsValidId(string? id)` (`Kil0bitSystemMonitor.Services.Ai.Tools`; true for `slowdown-yyyyMMdd-HHmmss`).
- Consumes (existing): `public sealed record AlertEvent(AlertRule Rule, double Value, string Detail, DateTime At)` (`Title`, `Message`); `AlertRule.Defaults`, `AlertRule.Label`; `public sealed record SavedReport(string Path, string Name, DateTime At, long Bytes)` (`WhenText`); `ProcessRow.Name`, `ProcessRow.Pid`; `ToastButton.Create(string text, Color accent, bool primary, Color onInk, Action onClick)`; `App.ConfigService`; `PadWindowTests.RepoRoot()` (tests).
- Produces: `public static class ExplainQuestions { public static string ForSlowdownReport(string reportId, DateTime localTime, CultureInfo ui); public static string ForAlert(AlertEvent alert, CultureInfo ui); public static string ForProcess(string name, int pid, CultureInfo ui); }` (namespace `Kil0bitSystemMonitor.Services.Ai`); `public static AlertToastWindow ShowFor(AlertEvent alert, Action onOpen, Action? onExplain = null)`; `public static class ExplainActions { bool AssistantOn; Visibility ButtonVisibility; Action? ForAlert(AlertEvent); Action? ForSlowdownReport(string reportPath, DateTime localTime); void ExplainProcess(string name, int pid); }`; `public sealed class ExplainableReport`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiExplainTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The Explain buttons: the questions they ask (in the Windows display language, with
    /// invariant dates), when they exist at all, and the alert card's button. Thai text is kept
    /// as \u escapes, as in the code under test.
    /// </summary>
    public class AiExplainTests
    {
        private static readonly CultureInfo English = new("en-US");
        private static readonly CultureInfo Thai = new("th-TH");

        private static AlertEvent HotCpu() =>
            new(AlertRule.Defaults[0], 97.4, "", new DateTime(2026, 9, 30, 14, 2, 0));

        [Fact]
        public void The_slowdown_question_names_the_report_and_its_time()
        {
            Assert.Equal("Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215).",
                ExplainQuestions.ForSlowdownReport("slowdown-20260929-140215", new DateTime(2026, 9, 29, 14, 2, 15), English));
        }

        [Fact]
        public void The_alert_question_names_the_rule_the_time_and_the_reading()
        {
            Assert.Equal("Why did the CPU temperature alert fire at 2026-09-30 14:02? MicaStats reported: 97.4\u00B0C, past the 95\u00B0C you set",
                ExplainQuestions.ForAlert(HotCpu(), English));
        }

        [Fact]
        public void The_process_question_names_the_process_and_its_pid()
        {
            Assert.Equal("What is svchost.exe (PID 1234) doing?", ExplainQuestions.ForProcess("svchost.exe", 1234, English));
        }

        [Fact]
        public void A_thai_display_language_asks_in_thai_with_a_gregorian_date()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                // The owner's format culture: its default calendar is Buddhist-era (2569).
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");

                string report = ExplainQuestions.ForSlowdownReport("slowdown-20260929-140215", new DateTime(2026, 9, 29, 14, 2, 15), Thai);
                string alert = ExplainQuestions.ForAlert(HotCpu(), Thai);
                string process = ExplainQuestions.ForProcess("svchost.exe", 1234, Thai);

                Assert.Equal("\u0E2D\u0E18\u0E34\u0E1A\u0E32\u0E22\u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E21\u0E37\u0E48\u0E2D 2026-09-29 14:02 (id slowdown-20260929-140215)", report);
                Assert.Equal("\u0E17\u0E33\u0E44\u0E21\u0E01\u0E32\u0E23\u0E41\u0E08\u0E49\u0E07\u0E40\u0E15\u0E37\u0E2D\u0E19 CPU temperature \u0E08\u0E36\u0E07\u0E40\u0E01\u0E34\u0E14\u0E02\u0E36\u0E49\u0E19\u0E40\u0E21\u0E37\u0E48\u0E2D 2026-09-30 14:02? MicaStats \u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E27\u0E48\u0E32: 97.4\u00B0C, past the 95\u00B0C you set", alert);
                Assert.Equal("svchost.exe (PID 1234) \u0E01\u0E33\u0E25\u0E31\u0E07\u0E17\u0E33\u0E2D\u0E30\u0E44\u0E23\u0E2D\u0E22\u0E39\u0E48?", process);
                Assert.DoesNotContain("2569", report + alert);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Explain_actions_exist_only_while_the_assistant_is_on()
        {
            Assert.Null(ExplainActions.ForAlert(HotCpu(), false, _ => { }, English));
            Assert.Null(ExplainActions.ForSlowdownReport(@"C:\reports\slowdown-20260929-140215.txt",
                new DateTime(2026, 9, 29, 14, 2, 15), false, _ => { }, English));

            Assert.NotNull(ExplainActions.ForAlert(HotCpu(), true, _ => { }, English));
        }

        [Fact]
        public void An_explain_action_asks_its_question_when_invoked()
        {
            var asked = new List<string?>();

            var report = ExplainActions.ForSlowdownReport(@"C:\reports\slowdown-20260929-140215.txt",
                new DateTime(2026, 9, 29, 14, 2, 15), true, asked.Add, English);
            var alert = ExplainActions.ForAlert(HotCpu(), true, asked.Add, English);
            Assert.Empty(asked);

            report!();
            alert!();

            Assert.Equal(new string?[]
            {
                "Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215).",
                "Why did the CPU temperature alert fire at 2026-09-30 14:02? MicaStats reported: 97.4\u00B0C, past the 95\u00B0C you set",
            }, asked);
        }

        [Fact]
        public void Only_slowdown_reports_are_explainable_and_only_while_the_assistant_is_on()
        {
            var reports = new[]
            {
                new SavedReport(@"C:\reports\slowdown-20260929-140215.txt", "slowdown-20260929-140215.txt", new DateTime(2026, 9, 29, 14, 2, 15), 2048),
                new SavedReport(@"C:\reports\hardware-report-20260929-120000.txt", "hardware-report-20260929-120000.txt", new DateTime(2026, 9, 29, 12, 0, 0), 4096),
            };

            var on = ExplainableReport.For(reports, assistantOn: true);
            var off = ExplainableReport.For(reports, assistantOn: false);

            Assert.Equal(Visibility.Visible, on[0].ExplainVisibility);
            Assert.Equal(Visibility.Collapsed, on[1].ExplainVisibility);
            Assert.All(off, r => Assert.Equal(Visibility.Collapsed, r.ExplainVisibility));
            Assert.Equal("slowdown-20260929-140215.txt", on[0].Name);
            Assert.Equal("2026-09-29 14:02", on[0].WhenText);
            Assert.Equal(@"C:\reports\slowdown-20260929-140215.txt", on[0].Path);
        }

        private static List<Button> Buttons(DependencyObject root)
        {
            var found = new List<Button>();
            if (root is Button button) found.Add(button);
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject element) found.AddRange(Buttons(element));
            }
            return found;
        }

        [Fact]
        public void The_alert_card_has_explain_only_when_given_an_action() => UiThread.Run(() =>
        {
            var plain = AlertToastWindow.Create(HotCpu(), () => { }, null);
            try
            {
                Assert.Equal(new[] { "Show me", "Dismiss" }, Buttons(plain).Select(b => (string)b.Content));
            }
            finally
            {
                plain.Close();
            }

            var withExplain = AlertToastWindow.Create(HotCpu(), () => { }, () => { });
            try
            {
                Assert.Equal(new[] { "Show me", "Explain", "Dismiss" }, Buttons(withExplain).Select(b => (string)b.Content));
            }
            finally
            {
                withExplain.Close();
            }
        });

        [Fact]
        public void Explain_on_the_card_asks_without_opening_diagnostics() => UiThread.Run(() =>
        {
            int opened = 0;
            int explained = 0;
            var toast = AlertToastWindow.Create(HotCpu(), () => opened++, () => explained++);

            var explain = Buttons(toast).Single(b => (string)b.Content == "Explain");
            explain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // also closes the card

            Assert.Equal(1, explained);
            Assert.Equal(0, opened);
        });

        [Fact]
        public void The_diagnostics_and_process_windows_carry_explain_buttons()
        {
            string root = PadWindowTests.RepoRoot();
            string diagnostics = File.ReadAllText(Path.Combine(root, "DiagnosticsWindow.xaml"));
            string processes = File.ReadAllText(Path.Combine(root, "TaskManagerWindow.xaml"));

            Assert.Contains("Click=\"OnExplainReport\"", diagnostics);
            Assert.Contains("Visibility=\"{Binding ExplainVisibility}\"", diagnostics);
            Assert.Contains("x:Name=\"ExplainButton\"", processes);
            Assert.Contains("Click=\"OnExplain\"", processes);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiExplainTests"`
Expected: build error — `ExplainQuestions`, `ExplainActions`, `ExplainableReport` and `AlertToastWindow.Create` do not exist.

- [ ] **Step 3: Write the questions**

Create `Services/Ai/ExplainQuestions.cs`:

```csharp
using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The questions the Explain buttons ask, in the Windows display language: Thai when it is
    /// Thai, English otherwise. The assistant answers in the language of the question, so this
    /// is what makes an Explain answer arrive in the user's own language.
    /// </summary>
    /// <remarks>
    /// Dates use the invariant culture: a Thai-format date carries a Buddhist-era year (2569),
    /// which a model could read as a date 543 years ahead. Thai text is written as \u escapes so
    /// the file stays ASCII.
    /// </remarks>
    public static class ExplainQuestions
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm";

        /// <summary>
        /// "Explain slowdown report 2026-09-29 14:02 (id slowdown-20260929-140215)." The id lets the
        /// model fetch the report with <c>get_slowdown_report</c> straight away.
        /// </summary>
        /// <param name="reportId">The report's file name without extension.</param>
        /// <param name="localTime">When the report was written, local time.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForSlowdownReport(string reportId, DateTime localTime, CultureInfo ui)
        {
            string when = localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? "\u0E2D\u0E18\u0E34\u0E1A\u0E32\u0E22\u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E40\u0E04\u0E23\u0E37\u0E48\u0E2D\u0E07\u0E0A\u0E49\u0E32\u0E40\u0E21\u0E37\u0E48\u0E2D " + when + " (id " + reportId + ")"
                : "Explain slowdown report " + when + " (id " + reportId + ").";
        }

        /// <summary>"Why did the CPU temperature alert fire at ...? MicaStats reported: ..."</summary>
        /// <param name="alert">The alert as the card showed it.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForAlert(AlertEvent alert, CultureInfo ui)
        {
            string when = alert.At.ToString(TimeFormat, CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? "\u0E17\u0E33\u0E44\u0E21\u0E01\u0E32\u0E23\u0E41\u0E08\u0E49\u0E07\u0E40\u0E15\u0E37\u0E2D\u0E19 " + alert.Rule.Label
                  + " \u0E08\u0E36\u0E07\u0E40\u0E01\u0E34\u0E14\u0E02\u0E36\u0E49\u0E19\u0E40\u0E21\u0E37\u0E48\u0E2D " + when
                  + "? MicaStats \u0E23\u0E32\u0E22\u0E07\u0E32\u0E19\u0E27\u0E48\u0E32: " + alert.Message
                : "Why did the " + alert.Rule.Label + " alert fire at " + when + "? MicaStats reported: " + alert.Message;
        }

        /// <summary>"What is svchost.exe (PID 1234) doing?"</summary>
        /// <param name="name">The image name, e.g. svchost.exe.</param>
        /// <param name="pid">Its process id.</param>
        /// <param name="ui">The Windows display language.</param>
        public static string ForProcess(string name, int pid, CultureInfo ui)
        {
            string id = pid.ToString(CultureInfo.InvariantCulture);
            return IsThai(ui)
                ? name + " (PID " + id + ") \u0E01\u0E33\u0E25\u0E31\u0E07\u0E17\u0E33\u0E2D\u0E30\u0E44\u0E23\u0E2D\u0E22\u0E39\u0E48?"
                : "What is " + name + " (PID " + id + ") doing?";
        }

        private static bool IsThai(CultureInfo ui) =>
            string.Equals(ui.TwoLetterISOLanguageName, "th", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Write the Explain actions and the report rows**

Create `Ai/ExplainActions.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The Explain buttons: each opens Ask MicaStats with a question naming the item, in the
    /// Windows display language (<see cref="CultureInfo.CurrentUICulture"/>). Every button is
    /// absent while the assistant is off, so callers ask here rather than read the config.
    /// </summary>
    public static class ExplainActions
    {
        /// <summary>True while the assistant is switched on in Settings > AI.</summary>
        public static bool AssistantOn => App.ConfigService?.Config.AiAssistantEnabled == true;

        /// <summary>Visible while the assistant is on, collapsed otherwise.</summary>
        public static Visibility ButtonVisibility => AssistantOn ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The alert card's Explain action, or null while the assistant is off (no button).</summary>
        public static Action? ForAlert(AlertEvent alert) =>
            ForAlert(alert, AssistantOn, App.OpenAsk, CultureInfo.CurrentUICulture);

        /// <summary>The Explain action for one saved slowdown report, or null while the assistant is off.</summary>
        /// <param name="reportPath">The report's full path; its file name without extension is the report id.</param>
        /// <param name="localTime">When it was written, local time.</param>
        public static Action? ForSlowdownReport(string reportPath, DateTime localTime) =>
            ForSlowdownReport(reportPath, localTime, AssistantOn, App.OpenAsk, CultureInfo.CurrentUICulture);

        /// <summary>Asks what one process is doing: the process window's Explain button.</summary>
        public static void ExplainProcess(string name, int pid) =>
            App.OpenAsk(ExplainQuestions.ForProcess(name, pid, CultureInfo.CurrentUICulture));

        /// <summary><see cref="ForAlert(AlertEvent)"/> with its inputs passed in (tests).</summary>
        internal static Action? ForAlert(AlertEvent alert, bool assistantOn, Action<string?> ask, CultureInfo ui) =>
            assistantOn ? () => ask(ExplainQuestions.ForAlert(alert, ui)) : null;

        /// <summary><see cref="ForSlowdownReport(string, DateTime)"/> with its inputs passed in (tests).</summary>
        internal static Action? ForSlowdownReport(string reportPath, DateTime localTime, bool assistantOn,
            Action<string?> ask, CultureInfo ui) =>
            assistantOn
                ? () => ask(ExplainQuestions.ForSlowdownReport(Path.GetFileNameWithoutExtension(reportPath), localTime, ui))
                : null;
    }
}
```

Create `Ai/ExplainableReport.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Services.Diagnostics;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// A saved report as the Diagnostics list shows it, plus whether it has an Explain button:
    /// only slowdown reports do (hardware and diagnostics reports share the folder), and only
    /// while the assistant is on.
    /// </summary>
    public sealed class ExplainableReport
    {
        /// <summary>Wraps one report.</summary>
        public ExplainableReport(SavedReport report, bool canExplain)
        {
            Report = report;
            CanExplain = canExplain;
        }

        /// <summary>The report itself.</summary>
        public SavedReport Report { get; }

        /// <summary>True when the Explain button shows.</summary>
        public bool CanExplain { get; }

        /// <summary>The file name, as before.</summary>
        public string Name => Report.Name;

        /// <summary>When it was written, as before.</summary>
        public string WhenText => Report.WhenText;

        /// <summary>The full path, carried in each button's Tag.</summary>
        public string Path => Report.Path;

        /// <summary>The Explain button's visibility.</summary>
        public Visibility ExplainVisibility => CanExplain ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// True for a slowdown report's file name, by the same rule the data tools use (Task 7's
        /// <c>SlowdownReportFiles</c>), so every Explain button names a report the assistant can read.
        /// </summary>
        public static bool IsSlowdownReport(string fileName) =>
            Kil0bitSystemMonitor.Services.Ai.Tools.SlowdownReportFiles.IsValidId(
                System.IO.Path.GetFileNameWithoutExtension(fileName));

        /// <summary>The rows for a report list.</summary>
        public static IReadOnlyList<ExplainableReport> For(IEnumerable<SavedReport> reports, bool assistantOn) =>
            reports.Select(r => new ExplainableReport(r, assistantOn && IsSlowdownReport(r.Name))).ToList();
    }
}
```

- [ ] **Step 5: Add Explain to the alert card**

In `AlertToastWindow.cs`, replace the line `        private AlertToastWindow(AlertEvent alert)` with:

```csharp
        private AlertToastWindow(AlertEvent alert, Action? onExplain)
```

Replace the line `            Content = BuildCard(alert);` with:

```csharp
            Content = BuildCard(alert, onExplain);
```

Replace:

```csharp
        /// <summary>Shows a notice for one breached rule.</summary>
        public static AlertToastWindow ShowFor(AlertEvent alert, Action onOpen)
        {
            var toast = new AlertToastWindow(alert);
            toast.OpenRequested += onOpen;
            ToastStack.Add(toast, MaxOnScreen);
            toast.Show();
            return toast;
        }
```

with:

```csharp
        /// <summary>
        /// Shows a notice for one breached rule. <paramref name="onExplain"/>, when given, adds an
        /// Explain button that asks the assistant about it; callers pass null while the assistant
        /// is off, so the button is simply not there.
        /// </summary>
        public static AlertToastWindow ShowFor(AlertEvent alert, Action onOpen, Action? onExplain = null)
        {
            var toast = Create(alert, onOpen, onExplain);
            ToastStack.Add(toast, MaxOnScreen);
            toast.Show();
            return toast;
        }

        /// <summary>Builds a notice without showing it; <see cref="ShowFor"/> shows it, tests do not.</summary>
        internal static AlertToastWindow Create(AlertEvent alert, Action onOpen, Action? onExplain)
        {
            var toast = new AlertToastWindow(alert, onExplain);
            toast.OpenRequested += onOpen;
            return toast;
        }
```

Replace the line `        private UIElement BuildCard(AlertEvent alert)` with:

```csharp
        private UIElement BuildCard(AlertEvent alert, Action? onExplain)
```

Replace the line:

```csharp
            buttons.Children.Add(ToastButton.Create("Dismiss", Amber, primary: false, Color.FromRgb(0x2A, 0x18, 0x06), Close));
```

with:

```csharp
            if (onExplain != null)
            {
                buttons.Children.Add(ToastButton.Create("Explain", Amber, primary: false, Color.FromRgb(0x2A, 0x18, 0x06), () =>
                {
                    onExplain();
                    Close();
                }));
            }
            buttons.Children.Add(ToastButton.Create("Dismiss", Amber, primary: false, Color.FromRgb(0x2A, 0x18, 0x06), Close));
```

- [ ] **Step 6: Pass the Explain actions at the two card call sites**

In `App.xaml.cs`, replace:

```csharp
                        AlertToastWindow.ShowFor(
                            new Kil0bitSystemMonitor.Services.Diagnostics.AlertEvent(rule, 0, headline, DateTime.Now),
                            () => DiagnosticsWindow.ShowDiagnostics(0));
```

with:

```csharp
                        AlertToastWindow.ShowFor(
                            new Kil0bitSystemMonitor.Services.Diagnostics.AlertEvent(rule, 0, headline, DateTime.Now),
                            () => DiagnosticsWindow.ShowDiagnostics(0),
                            Kil0bitSystemMonitor.Ai.ExplainActions.ForSlowdownReport(path, DateTime.Now));
```

and replace:

```csharp
                s_alerts.Raised += alert =>
                    AlertToastWindow.ShowFor(alert, () => DiagnosticsWindow.ShowDiagnostics(3));
```

with:

```csharp
                s_alerts.Raised += alert =>
                    AlertToastWindow.ShowFor(alert, () => DiagnosticsWindow.ShowDiagnostics(3),
                        Kil0bitSystemMonitor.Ai.ExplainActions.ForAlert(alert));
```

- [ ] **Step 7: Add Explain to the slowdown report rows**

In `DiagnosticsWindow.xaml`, replace the line `    xmlns:hlp="clr-namespace:Kil0bitSystemMonitor.Helpers"` with:

```xml
    xmlns:hlp="clr-namespace:Kil0bitSystemMonitor.Helpers"
    xmlns:ai="clr-namespace:Kil0bitSystemMonitor.Ai"
```

Then replace:

```xml
                                        <DataTemplate DataType="{x:Type dg:SavedReport}">
                                            <Grid Margin="0,0,0,4">
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="*" />
                                                    <ColumnDefinition Width="Auto" />
                                                </Grid.ColumnDefinitions>
                                                <StackPanel>
                                                    <TextBlock Style="{StaticResource SpecValue}" Text="{Binding Name}"
                                                               TextTrimming="CharacterEllipsis" />
                                                    <TextBlock Style="{StaticResource SubLabel}" Text="{Binding WhenText}" />
                                                </StackPanel>
                                                <Button Grid.Column="1" Style="{StaticResource QuickButton}"
                                                        Content="Open" Click="OnOpenReport"
                                                        Tag="{Binding Path}" VerticalAlignment="Center" />
                                            </Grid>
                                        </DataTemplate>
```

with:

```xml
                                        <DataTemplate DataType="{x:Type ai:ExplainableReport}">
                                            <Grid Margin="0,0,0,4">
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="*" />
                                                    <ColumnDefinition Width="Auto" />
                                                    <ColumnDefinition Width="Auto" />
                                                </Grid.ColumnDefinitions>
                                                <StackPanel>
                                                    <TextBlock Style="{StaticResource SpecValue}" Text="{Binding Name}"
                                                               TextTrimming="CharacterEllipsis" />
                                                    <TextBlock Style="{StaticResource SubLabel}" Text="{Binding WhenText}" />
                                                </StackPanel>
                                                <!-- Slowdown reports only, and only while the assistant is on. -->
                                                <Button Grid.Column="1" Style="{StaticResource QuickButton}"
                                                        Content="Explain" Click="OnExplainReport"
                                                        Tag="{Binding Path}" Visibility="{Binding ExplainVisibility}"
                                                        VerticalAlignment="Center" />
                                                <Button Grid.Column="2" Style="{StaticResource QuickButton}"
                                                        Content="Open" Click="OnOpenReport"
                                                        Tag="{Binding Path}" VerticalAlignment="Center" />
                                            </Grid>
                                        </DataTemplate>
```

In `DiagnosticsWindow.xaml.cs`, replace the line `using Kil0bitSystemMonitor.Helpers;` with:

```csharp
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Helpers;
```

Replace the line `            PreviewKeyDown += OnPreviewKeyDown;` with:

```csharp
            PreviewKeyDown += OnPreviewKeyDown;

            // The Explain buttons follow the assistant switch while the window is open.
            var watched = Config;
            if (watched != null)
            {
                watched.PropertyChanged += OnConfigChanged;
                Closed += (s, e) => watched.PropertyChanged -= OnConfigChanged;
            }
```

Replace:

```csharp
        private void ApplyReports()
        {
            _reports = DiagnosticsService.ListReports();
            ReportList.ItemsSource = _reports;
            NoReportsText.Visibility = _reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
```

with:

```csharp
        private void ApplyReports()
        {
            _reports = DiagnosticsService.ListReports();
            ReportList.ItemsSource = ExplainableReport.For(_reports, ExplainActions.AssistantOn);
            NoReportsText.Visibility = _reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
```

Insert immediately **above** the line `        private void OnSaveReport(object sender, RoutedEventArgs e)`:

```csharp
        /// <summary>Asks Ask MicaStats to explain one slowdown report (the button's Tag is its path).</summary>
        private void OnExplainReport(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string path) return;
            var report = _reports.Find(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            ExplainActions.ForSlowdownReport(path, report?.At ?? DateTime.Now)?.Invoke();
        }

        private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppConfig.AiAssistantEnabled))
                Dispatcher.BeginInvoke(new Action(ApplyReports));
        }

```

- [ ] **Step 8: Add Explain to the process window footer**

In `TaskManagerWindow.xaml`, replace:

```xml
                <Button x:Name="RetryElevated" Style="{StaticResource ActionButton}"
                        Content="Retry as administrator" Visibility="Collapsed"
                        Click="OnRetryElevated" />
```

with:

```xml
                <Button x:Name="RetryElevated" Style="{StaticResource ActionButton}"
                        Content="Retry as administrator" Visibility="Collapsed"
                        Click="OnRetryElevated" />
                <!-- Acts on the one selected process; shown only while the assistant is on. -->
                <Button x:Name="ExplainButton" Style="{StaticResource ActionButton}"
                        Content="Explain" IsEnabled="False" Visibility="Collapsed"
                        ToolTip="Ask MicaStats what the selected process is doing"
                        Click="OnExplain" />
```

In `TaskManagerWindow.xaml.cs`, replace the line `using Kil0bitSystemMonitor.Services;` with:

```csharp
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services;
```

Replace the line `            Loaded += (s, e) => SearchBox.Focus();` with:

```csharp
            Loaded += (s, e) => SearchBox.Focus();

            // Explain exists only while the assistant is on, and follows the switch live.
            ExplainButton.Visibility = ExplainActions.ButtonVisibility;
            var config = App.ConfigService?.Config;
            if (config != null)
            {
                config.PropertyChanged += OnConfigChanged;
                Closed += (s, e) => config.PropertyChanged -= OnConfigChanged;
            }
```

Replace the line `            EndTaskButton.IsEnabled = count > 0;` with:

```csharp
            EndTaskButton.IsEnabled = count > 0;
            ExplainButton.IsEnabled = count == 1;
```

Insert immediately **above** the line `        private void OnEndTask(object sender, RoutedEventArgs e)`:

```csharp
        /// <summary>Asks Ask MicaStats what the one selected process is doing.</summary>
        private void OnExplain(object sender, RoutedEventArgs e)
        {
            if (ProcessList.SelectedItems.Count == 1 && ProcessList.SelectedItem is ProcessRow row)
                ExplainActions.ExplainProcess(row.Name, row.Pid);
        }

        private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Kil0bitSystemMonitor.Models.AppConfig.AiAssistantEnabled))
                Dispatcher.BeginInvoke(new Action(() => ExplainButton.Visibility = ExplainActions.ButtonVisibility));
        }

```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiExplainTests|FullyQualifiedName~ToastButtonTests"`
Expected: 10 + 6 passed (the toast button tests are unchanged).

- [ ] **Step 10: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1125 + 10 = 1135).

```bash
git add Services/Ai/ExplainQuestions.cs Ai/ExplainActions.cs Ai/ExplainableReport.cs AlertToastWindow.cs App.xaml.cs DiagnosticsWindow.xaml DiagnosticsWindow.xaml.cs TaskManagerWindow.xaml TaskManagerWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/AiExplainTests.cs
git commit -F - <<'EOF'
feat(ai): Explain buttons on slowdown reports, alerts and processes

Each opens Ask MicaStats with a question naming the item, in the
Windows display language, with dates in the invariant culture so a
Thai calendar never stamps 2569. Slowdown reports get Explain beside
Open, the alert card gets an Explain button, and the process window
gets a footer button for the selected process. All of them are absent
while the assistant is off and follow the switch live.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 14: Settings > AI

A new *AI* section in Settings, after MicaPad: the assistant switch; the provider (Claude or OpenAI-compatible) with model, base URL and API keys (a key goes to the DPAPI secret store on **Save** and is never shown again: the box turns into *Saved* and *Remove*); a one-line note saying where questions go ("stays on this PC" for a loopback URL, else the host); **Test connection** (one tiny request off the UI thread, not counted against the daily limit); the hotkey with MicaPad's validation; the daily limit with today's count; the 7-day history switch, its size and **Delete history**; and MCP (Off / Stdio bridge / Local HTTP, port, token **Copy** / **Regenerate**, **Copy Claude Desktop config**, **Copy Claude Code command**, and the HTTP problem the app reports). The section is a `UserControl` fed by an `AiSettingsHost`, so it is tested over temp stores and a fake client.

**Files:**
- Create: `Services/Ai/AiPrivacyNote.cs`, `Ai/AiSettingsHost.cs`, `Ai/AiSettingsPanel.xaml`, `Ai/AiSettingsPanel.xaml.cs`
- Modify: `SettingsWindow.xaml` (namespace, nav item "AI", `AiSection`), `SettingsWindow.xaml.cs` (`SelectSection`, `LoadAiSettings`)
- Test: `tests/Kil0bitSystemMonitor.Tests/AiSettingsTests.cs`

**Interfaces:**
- Consumes (Task 1): all `AppConfig.Ai*` properties and their setter rules; `AiProviders.Claude` / `AiProviders.OpenAiCompatible`; `AiMcpModes.Off` / `Stdio` / `Http`; `SecretNames.ClaudeKey` / `CompatibleKey` / `McpToken`; `SecretStore.Has/Get/Set/Remove`, `SecretStore.NewToken()`.
- Consumes (Task 4/5): `HistoryStore.SizeBytes()`, `HistoryStore.DeleteAll()`, `HistoryStore.Append(HistoryRow)`, `public static HistoryStore App.History`, `AiTestEnv`.
- Consumes (Task 8): `AiProviderFactory.Create(AppConfig config, SecretStore secrets, HttpMessageHandler? handler = null)`, `AiClientResult(IChatClient? Client, string? Problem, bool IsClaude)`, `AiErrorText.Describe(Exception)`, `UsageMeter.UsedToday`.
- Consumes (Task 11): `McpConfigSnippets.ClaudeDesktopJson(string exePath)`, `ClaudeCodeStdioCommand(string exePath)`, `ClaudeCodeHttpCommand(int port, string token)`; `public static string? App.AiMcpHttpProblem`.
- Consumes (Task 12): `App.AiSecrets`, `App.AiUsage`; `UiPump.Wait(Task)` (tests).
- Produces: `public static class AiPrivacyNote { public static string Describe(string provider, string? compatibleBaseUrl); }`; `public sealed class AiSettingsHost`; `public partial class AiSettingsPanel : UserControl { public void Load(AiSettingsHost host); }`; Settings navigation tag `"AI"` understood by `SettingsWindow.SelectSection` (used by `App.ShowSettingsSection("AI")` from the Ask window).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AiSettingsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
                CopyText = Copied.Add,
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
            Assert.Contains("stays on this PC", panel.PrivacyText.Text);
            Assert.Equal(0, rig.Saves);
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
            Assert.Contains("openrouter.ai", panel.PrivacyText.Text);

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
            Assert.StartsWith("Not a valid shortcut", panel.HotkeyHint.Text);

            panel.HotkeyBox.Text = "";
            LoseFocus(panel.HotkeyBox);
            Assert.Equal("", rig.Config.AiHotkey);
            Assert.StartsWith("Shortcut off", panel.HotkeyHint.Text);
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
            Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(rig.Config));

            Click(panel.RemoveClaudeKeyButton);

            Assert.False(rig.Secrets.Has(SecretNames.ClaudeKey));
            Assert.Equal(Visibility.Visible, panel.ClaudeKeyEntry.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.ClaudeKeySaved.Visibility);

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
            Assert.StartsWith("History deleted.", panel.HistoryHint.Text);
        });

        [Fact]
        public void The_limit_line_shows_todays_count() => WithPanel((panel, rig) =>
        {
            Assert.True(rig.Usage.TryConsume(100));
            panel.Load(rig.Host());

            Assert.StartsWith("Used today: 1 of 100.", panel.LimitHint.Text);
        });

        [Fact]
        public void Settings_has_an_ai_section()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));

            Assert.Contains("Tag=\"AI\"", xaml);
            Assert.Contains("x:Name=\"AiSection\"", xaml);
            Assert.Contains("<ai:AiSettingsPanel x:Name=\"AiPanel\"", xaml);
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
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiSettingsTests|FullyQualifiedName~AiPrivacyNoteTests"`
Expected: build error — `AiSettingsPanel`, `AiSettingsHost` and `AiPrivacyNote` do not exist.

- [ ] **Step 3: Write the privacy note**

Create `Services/Ai/AiPrivacyNote.cs`:

```csharp
using System;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// The line under the provider settings that says where questions go: "stays on this PC"
    /// for a server on this machine, otherwise the host that receives them. Worked out from the
    /// settings alone, so it is true before anything has been sent.
    /// </summary>
    public static class AiPrivacyNote
    {
        private const string Removed = " Your profile folder, computer name, user name and IP addresses are removed first.";

        /// <summary>The note for a provider and, for an OpenAI-compatible one, its base URL.</summary>
        /// <param name="provider">An <see cref="AiProviders"/> value.</param>
        /// <param name="compatibleBaseUrl">The compatible endpoint's base URL; ignored for Claude.</param>
        public static string Describe(string provider, string? compatibleBaseUrl)
        {
            if (!string.Equals(provider, AiProviders.OpenAiCompatible, StringComparison.Ordinal))
                return "Questions and the PC data they need go to Anthropic (api.anthropic.com)." + Removed;

            if (!Uri.TryCreate(compatibleBaseUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "The base URL is not a valid http or https address, so nothing can be sent.";

            if (uri.IsLoopback)
                return "Everything stays on this PC (" + uri.Host + ").";

            return "Questions and the PC data they need go to " + uri.Host + "." + Removed;
        }
    }
}
```

- [ ] **Step 4: Write the host object**

Create `Ai/AiSettingsHost.cs`:

```csharp
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
```

- [ ] **Step 5: Write the panel's markup**

Create `Ai/AiSettingsPanel.xaml`:

```xml
<UserControl
    x:Class="Kil0bitSystemMonitor.Ai.AiSettingsPanel"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.modernwpf.com/2019">

    <!--
      Settings > AI, hosted in SettingsWindow's AiSection. Thin: the rules live in Services/Ai,
      and AiSettingsHost supplies the config and the stores, so a test builds this panel over
      temp folders. A key goes to the DPAPI secret store on Save and is never shown again: the
      box turns into "Saved" and a Remove button.
    -->

    <UserControl.Resources>
        <Style x:Key="Card" TargetType="Border">
            <Setter Property="Background" Value="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" />
            <Setter Property="BorderBrush" Value="{DynamicResource SystemControlElevationBorderBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Margin" Value="0,0,0,12" />
            <Setter Property="Padding" Value="20,16" />
        </Style>
        <Style x:Key="CardIcon" TargetType="ui:FontIcon">
            <Setter Property="FontSize" Value="20" />
            <Setter Property="Foreground" Value="{DynamicResource SystemAccentColorBrush}" />
            <Setter Property="Margin" Value="0,0,20,0" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style x:Key="CardTitle" TargetType="TextBlock">
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="FontSize" Value="15" />
        </Style>
        <Style x:Key="CardHint" TargetType="TextBlock">
            <Setter Property="Opacity" Value="0.6" />
            <Setter Property="FontSize" Value="12.5" />
            <Setter Property="TextWrapping" Value="Wrap" />
        </Style>
        <Style x:Key="FieldLabel" TargetType="TextBlock">
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="Margin" Value="0,0,0,4" />
        </Style>
    </UserControl.Resources>

    <StackPanel>

        <!-- Assistant -->
        <Border Style="{StaticResource Card}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <ui:FontIcon Glyph="&#xE8BD;" Style="{StaticResource CardIcon}" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                    <TextBlock Text="Ask MicaStats" Style="{StaticResource CardTitle}" />
                    <TextBlock Style="{StaticResource CardHint}"
                               Text="A window for questions about this PC, and Explain buttons on slowdown reports, alerts and processes. Nothing is sent until you press Send or Explain." />
                </StackPanel>
                <ui:ToggleSwitch Grid.Column="2" x:Name="AssistantToggle" Toggled="OnAssistantToggled" VerticalAlignment="Center" />
            </Grid>
        </Border>

        <!-- Provider -->
        <Border Style="{StaticResource Card}">
            <StackPanel>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <ui:FontIcon Glyph="&#xE774;" Style="{StaticResource CardIcon}" />
                    <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                        <TextBlock Text="Provider" Style="{StaticResource CardTitle}" />
                        <TextBlock Style="{StaticResource CardHint}"
                                   Text="Claude with your own API key, or any OpenAI-compatible server: OpenAI, Azure, OpenRouter, or Ollama and LM Studio on this PC." />
                    </StackPanel>
                    <ComboBox Grid.Column="2" x:Name="ProviderBox" Width="180" VerticalAlignment="Center" SelectionChanged="OnProviderChanged">
                        <ComboBoxItem Content="Claude" />
                        <ComboBoxItem Content="OpenAI-compatible" />
                    </ComboBox>
                </Grid>

                <StackPanel x:Name="ClaudePanel" Margin="40,16,0,0">
                    <TextBlock Text="Model" Style="{StaticResource FieldLabel}" />
                    <TextBlock Style="{StaticResource CardHint}"
                               Text="claude-haiku-4-5 is quick and cheap; claude-sonnet-5-5 and claude-opus-5-5 think harder. Any model name works." />
                    <TextBox x:Name="ClaudeModelBox" Width="260" HorizontalAlignment="Left" Margin="0,6,0,14" LostFocus="OnClaudeModelChanged" />
                    <TextBlock Text="API key" Style="{StaticResource FieldLabel}" />
                    <StackPanel x:Name="ClaudeKeyEntry" Orientation="Horizontal">
                        <PasswordBox x:Name="ClaudeKeyBox" Width="260" />
                        <Button x:Name="SaveClaudeKeyButton" Content="Save" Click="OnSaveClaudeKey" Margin="8,0,0,0" Padding="14,5" />
                    </StackPanel>
                    <StackPanel x:Name="ClaudeKeySaved" Orientation="Horizontal" Visibility="Collapsed">
                        <TextBlock Text="Saved" VerticalAlignment="Center" Opacity="0.8" />
                        <Button x:Name="RemoveClaudeKeyButton" Content="Remove" Click="OnRemoveClaudeKey" Margin="12,0,0,0" Padding="14,5" />
                    </StackPanel>
                </StackPanel>

                <StackPanel x:Name="CompatiblePanel" Margin="40,16,0,0" Visibility="Collapsed">
                    <TextBlock Text="Base URL" Style="{StaticResource FieldLabel}" />
                    <TextBox x:Name="CompatibleUrlBox" Width="320" HorizontalAlignment="Left" Margin="0,0,0,14" LostFocus="OnCompatibleUrlChanged" />
                    <TextBlock Text="Model" Style="{StaticResource FieldLabel}" />
                    <TextBox x:Name="CompatibleModelBox" Width="260" HorizontalAlignment="Left" Margin="0,0,0,14" LostFocus="OnCompatibleModelChanged" />
                    <TextBlock Text="API key (not needed for Ollama or LM Studio)" Style="{StaticResource FieldLabel}" />
                    <StackPanel x:Name="CompatibleKeyEntry" Orientation="Horizontal">
                        <PasswordBox x:Name="CompatibleKeyBox" Width="260" />
                        <Button x:Name="SaveCompatibleKeyButton" Content="Save" Click="OnSaveCompatibleKey" Margin="8,0,0,0" Padding="14,5" />
                    </StackPanel>
                    <StackPanel x:Name="CompatibleKeySaved" Orientation="Horizontal" Visibility="Collapsed">
                        <TextBlock Text="Saved" VerticalAlignment="Center" Opacity="0.8" />
                        <Button x:Name="RemoveCompatibleKeyButton" Content="Remove" Click="OnRemoveCompatibleKey" Margin="12,0,0,0" Padding="14,5" />
                    </StackPanel>
                </StackPanel>

                <TextBlock x:Name="KeyHint" Margin="40,8,0,0" Style="{StaticResource CardHint}" Visibility="Collapsed" />
                <TextBlock x:Name="PrivacyText" Margin="40,14,0,0" Style="{StaticResource CardHint}" />
                <StackPanel Orientation="Horizontal" Margin="40,14,0,0">
                    <Button x:Name="TestButton" Content="Test connection" Click="OnTestConnection" Padding="14,6" />
                    <TextBlock x:Name="TestResultText" VerticalAlignment="Center" Margin="12,0,0,0" TextWrapping="Wrap" MaxWidth="420" />
                </StackPanel>
            </StackPanel>
        </Border>

        <!-- Shortcut -->
        <Border Style="{StaticResource Card}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <ui:FontIcon Glyph="&#xE765;" Style="{StaticResource CardIcon}" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                    <TextBlock Text="Shortcut" Style="{StaticResource CardTitle}" />
                    <TextBlock x:Name="HotkeyHint" Style="{StaticResource CardHint}" />
                </StackPanel>
                <TextBox Grid.Column="2" x:Name="HotkeyBox" Width="160" VerticalAlignment="Center" LostFocus="OnHotkeyChanged" />
            </Grid>
        </Border>

        <!-- Daily limit -->
        <Border Style="{StaticResource Card}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <ui:FontIcon Glyph="&#xE823;" Style="{StaticResource CardIcon}" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                    <TextBlock Text="Questions per day" Style="{StaticResource CardTitle}" />
                    <TextBlock x:Name="LimitHint" Style="{StaticResource CardHint}" />
                </StackPanel>
                <TextBox Grid.Column="2" x:Name="LimitBox" Width="100" VerticalAlignment="Center" LostFocus="OnLimitChanged" />
            </Grid>
        </Border>

        <!-- History -->
        <Border Style="{StaticResource Card}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <ui:FontIcon Glyph="&#xE81C;" Style="{StaticResource CardIcon}" />
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                    <TextBlock Text="Keep 7 days of history" Style="{StaticResource CardTitle}" />
                    <TextBlock x:Name="HistoryHint" Style="{StaticResource CardHint}" />
                    <Button x:Name="DeleteHistoryButton" Content="Delete history" Click="OnDeleteHistory"
                            HorizontalAlignment="Left" Margin="0,8,0,0" Padding="14,5" />
                </StackPanel>
                <ui:ToggleSwitch Grid.Column="2" x:Name="HistoryToggle" Toggled="OnHistoryToggled" VerticalAlignment="Center" />
            </Grid>
        </Border>

        <!-- MCP -->
        <Border Style="{StaticResource Card}">
            <StackPanel>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <ui:FontIcon Glyph="&#xE703;" Style="{StaticResource CardIcon}" />
                    <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                        <TextBlock Text="MCP for Claude Desktop and Claude Code" Style="{StaticResource CardTitle}" />
                        <TextBlock x:Name="McpHint" Style="{StaticResource CardHint}" />
                    </StackPanel>
                    <ComboBox Grid.Column="2" x:Name="McpModeBox" Width="160" VerticalAlignment="Center" SelectionChanged="OnMcpModeChanged">
                        <ComboBoxItem Content="Off" />
                        <ComboBoxItem Content="Stdio bridge" />
                        <ComboBoxItem Content="Local HTTP" />
                    </ComboBox>
                </Grid>

                <StackPanel x:Name="McpHttpPanel" Margin="40,16,0,0" Visibility="Collapsed">
                    <TextBlock Text="Port" Style="{StaticResource FieldLabel}" />
                    <TextBox x:Name="McpPortBox" Width="100" HorizontalAlignment="Left" Margin="0,0,0,14" LostFocus="OnMcpPortChanged" />
                    <TextBlock Text="Token" Style="{StaticResource FieldLabel}" />
                    <TextBlock Style="{StaticResource CardHint}"
                               Text="Clients send it as a bearer token. It is stored encrypted for your Windows account and never shown." />
                    <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                        <Button x:Name="CopyTokenButton" Content="Copy token" Click="OnCopyToken" Padding="14,5" />
                        <Button x:Name="RegenerateTokenButton" Content="Regenerate" Click="OnRegenerateToken" Margin="8,0,0,0" Padding="14,5" />
                    </StackPanel>
                </StackPanel>

                <StackPanel Orientation="Horizontal" Margin="40,16,0,0">
                    <Button x:Name="CopyDesktopButton" Content="Copy Claude Desktop config" Click="OnCopyDesktopConfig" Padding="14,5" />
                    <Button x:Name="CopyCodeButton" Content="Copy Claude Code command" Click="OnCopyCodeCommand" Margin="8,0,0,0" Padding="14,5" />
                </StackPanel>
                <TextBlock x:Name="McpProblemText" Margin="40,10,0,0" TextWrapping="Wrap" Foreground="#FFE8A53C" Visibility="Collapsed" />
                <TextBlock x:Name="McpStatusText" Margin="40,8,0,0" Style="{StaticResource CardHint}" />
            </StackPanel>
        </Border>
    </StackPanel>
</UserControl>
```

- [ ] **Step 6: Write the panel's code**

Create `Ai/AiSettingsPanel.xaml.cs`:

```csharp
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
```

- [ ] **Step 7: Add the section to Settings**

In `SettingsWindow.xaml`, replace the line `    xmlns:local="clr-namespace:Kil0bitSystemMonitor"` with:

```xml
    xmlns:local="clr-namespace:Kil0bitSystemMonitor"
    xmlns:ai="clr-namespace:Kil0bitSystemMonitor.Ai"
```

Insert immediately **above** the line `                <ui:NavigationViewItem Content="Diagnostics" Tag="Diagnostics">`:

```xml
                <ui:NavigationViewItem Content="AI" Tag="AI">
                    <ui:NavigationViewItem.Icon>
                        <ui:FontIcon Glyph="&#xE8BD;" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
```

Insert immediately **above** the line `                    <!-- Diagnostics -->`:

```xml
                    <!-- AI -->
                    <StackPanel x:Name="AiSection" Visibility="Collapsed">
                        <TextBlock Text="AI" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,8"/>
                        <TextBlock Text="Ask MicaStats about this PC, and let Claude Desktop or Claude Code read its data. Everything here is off until you turn it on, and nothing is sent anywhere until you press Send, Explain or Test connection." Opacity="0.6" FontSize="13" TextWrapping="Wrap" Margin="0,0,0,20"/>
                        <ai:AiSettingsPanel x:Name="AiPanel"/>
                    </StackPanel>

```

In `SettingsWindow.xaml.cs`, replace the line `                PadSection.Visibility = Visibility.Collapsed;` with:

```csharp
                PadSection.Visibility = Visibility.Collapsed;
                AiSection.Visibility = Visibility.Collapsed;
```

Replace the line `                    case "MicaPad": PadSection.Visibility = Visibility.Visible; LoadPadSettings(); break;` with:

```csharp
                    case "MicaPad": PadSection.Visibility = Visibility.Visible; LoadPadSettings(); break;
                    case "AI": AiSection.Visibility = Visibility.Visible; LoadAiSettings(); break;
```

Insert immediately **above** the line `        private void OnCaptureSettingToggled(object sender, RoutedEventArgs e)`:

```csharp
        // ---- AI -------------------------------------------------------------------------------

        /// <summary>
        /// Hands the AI panel the live config and stores. The panel holds every rule, so this is
        /// the only AI code in the settings window.
        /// </summary>
        private void LoadAiSettings()
        {
            try
            {
                AiPanel.Load(new Kil0bitSystemMonitor.Ai.AiSettingsHost
                {
                    Config = _config.Config,
                    Save = _config.SaveConfig,
                    Secrets = App.AiSecrets,
                    History = App.History,
                    Usage = () => App.AiUsage,
                    McpHttpProblem = () => App.AiMcpHttpProblem,
                });
            }
            catch (Exception ex)
            {
                Kil0bitSystemMonitor.Services.DiagnosticsLog.Error("ai", "Could not load AI settings", ex);
            }
        }

```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~AiSettingsTests|FullyQualifiedName~AiPrivacyNoteTests|FullyQualifiedName~Settings_has_a_micapad_section"`
Expected: 12 + 6 + 1 passed.

- [ ] **Step 9: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1135 + 18 = 1153).

```bash
git add Services/Ai/AiPrivacyNote.cs Ai/AiSettingsHost.cs Ai/AiSettingsPanel.xaml Ai/AiSettingsPanel.xaml.cs SettingsWindow.xaml SettingsWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/AiSettingsTests.cs
git commit -F - <<'EOF'
feat(ai): Settings AI section

Assistant switch, provider with model, base URL and keys (saved to the
DPAPI store and never shown again), a note saying where questions go,
Test connection that is not counted, the hotkey, the daily limit with
the count for today, the 7-day history switch with its size and Delete,
and MCP: Off, stdio bridge or local HTTP, port, token Copy and
Regenerate, and Copy buttons for Claude Desktop and Claude Code.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 15: Documentation (README in English and Thai, GUIDE, ROADMAP)

The README gains the AI feature in both its English and Thai halves (feature section, the Ctrl+Alt+A shortcut, the technology row); the GUIDE gets an "AI assistant and MCP" section with the setup steps, privacy, limits and the Claude Desktop / Claude Code snippets; the ROADMAP marks the feature as shipping in the next release and lists the follow-up sub-project. Prose only; the Thai text is written directly in the Markdown (UTF-8).

**Files:**
- Modify: `README.md`, `GUIDE.md`, `ROADMAP.md`

**Interfaces:** none (prose). The defaults quoted (Ctrl+Alt+A, 100 questions a day, port 47831, `claude-haiku-4-5`, `http://localhost:11434/v1`) must match Task 1's `AppConfig` defaults; the snippets below are Task 11's `McpConfigSnippets` output for the default install path `C:\Program Files\MicaStats\MicaStats.exe` (server name `micastats`, `--scope user`, `--mcp` after `--`), checked against its tests.

- [ ] **Step 1: README — English**

In `README.md`, insert immediately **above** the line `### Windows 11 interface`:

```markdown
### Ask MicaStats: answers about your PC

* **Ask in plain words** — "why was it slow ten minutes ago?", "what is using my memory?", "is 90 °C normal for this CPU?" — and get an answer built from MicaStats' own readings: live status, the busiest processes, slowdown reports, alerts, hardware, battery, boot times and up to **7 days of history**
* **Explain buttons** on slowdown reports (Diagnostics), alert notices and the selected process (Processes) ask for you, in your Windows display language; a question in Thai is answered in Thai
* **Your choice of AI**: Claude with your own Anthropic API key, or any OpenAI-compatible server — OpenAI, Azure, OpenRouter, or **Ollama / LM Studio on this PC**, in which case nothing leaves the machine
* **Suggestions, never actions**: an answer can offer a button such as *End chrome.exe* or *Record a slowdown now*; nothing happens until you click, and the usual checks apply (same process and start time, core Windows processes refused)
* **Private by design**: your profile folder, computer name, user name, IP and MAC addresses are removed before anything is sent; window titles, command lines and environment variables are never collected; keys are stored encrypted for your Windows account (DPAPI) and never shown again
* **Claude Desktop and Claude Code** can read the same data through **MCP**, with no key or cost inside MicaStats: a stdio bridge (`MicaStats.exe --mcp`) or a token-protected local HTTP endpoint on `127.0.0.1`. Read-only
* Everything is **off until you turn it on** in **Settings → AI**; a daily question limit (100 by default) keeps the cost predictable, and the assistant only runs when you press Send or Explain

```

Replace the line `| **Ctrl+Alt+N** | Show MicaPad |` with:

```markdown
| **Ctrl+Alt+N** | Show MicaPad |
| **Ctrl+Alt+A** | Ask MicaStats (while the assistant is on in **Settings → AI**) |
```

Replace the line `| Text editor         | AvalonEdit (MIT), for MicaPad                |` with:

```markdown
| Text editor         | AvalonEdit (MIT), for MicaPad                |
| AI                  | Anthropic .NET SDK, Microsoft.Extensions.AI, MCP C# SDK (ModelContextProtocol.Core) |
```

- [ ] **Step 2: README — Thai**

In `README.md`, insert immediately **above** the line `### หน้าตาแบบ Windows 11`:

```markdown
### Ask MicaStats: ถามเรื่องเครื่องของคุณได้ด้วยภาษาธรรมดา

* **ถามเป็นภาษาพูด** เช่น "เมื่อสิบนาทีก่อนเครื่องช้าเพราะอะไร?", "อะไรกินหน่วยความจำอยู่?", "CPU 90 °C ถือว่าปกติไหม?" แล้วได้คำตอบที่อ้างอิงข้อมูลจริงของ MicaStats เอง ทั้งสถานะปัจจุบัน โปรเซสที่ทำงานหนักที่สุด รายงานเครื่องช้า การแจ้งเตือน ฮาร์ดแวร์ แบตเตอรี่ เวลาบูต และ **ประวัติย้อนหลังได้ถึง 7 วัน**
* **ปุ่ม Explain** บนรายงานเครื่องช้า (Diagnostics) การแจ้งเตือนที่มุมจอ และโปรเซสที่เลือกไว้ (Processes) จะตั้งคำถามให้เองตามภาษาที่ Windows แสดงผล ถามเป็นภาษาไทยก็ได้คำตอบเป็นภาษาไทย
* **เลือก AI ได้เอง**: Claude ด้วยคีย์ API ของ Anthropic ของคุณเอง หรือเซิร์ฟเวอร์ที่รองรับรูปแบบ OpenAI เช่น OpenAI, Azure, OpenRouter หรือ **Ollama / LM Studio ที่รันบนเครื่องนี้** ซึ่งข้อมูลจะไม่ออกจากเครื่องเลย
* **แนะนำเท่านั้น ไม่ลงมือเอง**: คำตอบอาจมีปุ่มอย่าง *End chrome.exe* หรือ *Record a slowdown now* แต่จะไม่มีอะไรเกิดขึ้นจนกว่าคุณจะกด และยังผ่านการตรวจสอบแบบเดิมทุกครั้ง (ต้องเป็นโปรเซสเดิมที่เวลาเริ่มทำงานตรงกัน และไม่ยอมปิดโปรเซสหลักของ Windows)
* **ความเป็นส่วนตัวมาก่อน**: ลบโฟลเดอร์โปรไฟล์ ชื่อเครื่อง ชื่อผู้ใช้ ที่อยู่ IP และ MAC ออกก่อนส่งทุกครั้ง ไม่เก็บชื่อหน้าต่าง คำสั่งที่ใช้เรียกโปรแกรม หรือตัวแปรสภาพแวดล้อม และคีย์ถูกเข้ารหัสด้วยบัญชี Windows ของคุณ (DPAPI) ไม่แสดงให้เห็นอีกหลังบันทึก
* **Claude Desktop และ Claude Code** อ่านข้อมูลชุดเดียวกันได้ผ่าน **MCP** โดยไม่ต้องใช้คีย์หรือเสียค่าใช้จ่ายใน MicaStats เลือกได้ระหว่าง stdio bridge (`MicaStats.exe --mcp`) หรือ HTTP ภายในเครื่องที่ `127.0.0.1` ซึ่งต้องใช้โทเคน ทั้งสองแบบอ่านข้อมูลได้อย่างเดียว
* ทุกอย่าง **ปิดไว้จนกว่าคุณจะเปิด** ใน **Settings → AI** มีเพดานจำนวนคำถามต่อวัน (ค่าเริ่มต้น 100) เพื่อคุมค่าใช้จ่าย และผู้ช่วยจะทำงานเฉพาะเมื่อคุณกด Send หรือ Explain เท่านั้น

```

Replace the line `| **Ctrl+Alt+N** | เปิด MicaPad |` with:

```markdown
| **Ctrl+Alt+N** | เปิด MicaPad |
| **Ctrl+Alt+A** | เปิด Ask MicaStats (เมื่อเปิดผู้ช่วยไว้ใน **Settings → AI**) |
```

Replace the line `| โปรแกรมแก้ไขข้อความ | AvalonEdit (MIT) สำหรับ MicaPad |` with:

```markdown
| โปรแกรมแก้ไขข้อความ | AvalonEdit (MIT) สำหรับ MicaPad |
| AI | Anthropic .NET SDK, Microsoft.Extensions.AI และ MCP C# SDK (ModelContextProtocol.Core) |
```

- [ ] **Step 3: GUIDE — the AI section**

In `GUIDE.md`, insert immediately **above** the line `## 🔩 Hardware Inspector`:

````markdown
## 🤖 AI assistant and MCP

MicaStats measures a lot and used to leave the reading to you. **Ask MicaStats** answers
questions about this PC from MicaStats' own data, and **MCP** lets Claude Desktop or Claude Code
read the same data. Everything is **off until you turn it on** in **Settings → AI**, and nothing
is sent anywhere until you press **Send**, **Explain** or **Test connection**.

### Setting it up

1. **Settings → AI → Ask MicaStats**: turn it on.
2. **Provider**:
   - **Claude** — paste an API key from [console.anthropic.com](https://console.anthropic.com/)
     and press **Save**. The default model, `claude-haiku-4-5`, is quick and cheap;
     `claude-sonnet-5-5` and `claude-opus-5-5` think harder, and any model name works.
   - **OpenAI-compatible** — a base URL, a model and, if the server needs one, a key. For a model
     on this PC with [Ollama](https://ollama.com/): run `ollama pull llama3.2`, then use base URL
     `http://localhost:11434/v1` and model `llama3.2`, with no key. LM Studio's server is
     `http://localhost:1234/v1`. OpenAI, Azure and OpenRouter work the same way with their URL
     and key.
3. **Test connection** sends one tiny request and shows the reply. It does not count toward the
   daily limit.

A key is stored encrypted for your Windows account (DPAPI) in `%APPDATA%\MicaStats\secrets.bin`,
never in `config.json`, and is never shown again: the box turns into **Saved** and **Remove**.
The line under the provider says where questions go — *Everything stays on this PC* for a local
server, otherwise the host name.

### Asking

Open it from **Ask MicaStats…** in the overlay's right-click menu or with **Ctrl+Alt+A** (change
the shortcut in **Settings → AI**). Type a question and press **Enter** (**Shift+Enter** adds a
line):

- "Why was the PC slow ten minutes ago?"
- "What is using my memory right now?"
- "Did the CPU run hotter today than yesterday?" (needs the 7-day history, below)
- "ทำไมเครื่องช้าเมื่อเช้านี้?" — a question in Thai is answered in Thai

The answer streams in. Under it, **Details** lists what MicaStats looked up, with the arguments
(for example `get_top_processes {"by":"cpu","count":5}`), so you can see what the answer rests
on. **Stop** cancels; **New conversation** starts over. If something fails, the question stays in
the box and **Retry** sends it again; a missing key or model offers **Open Settings > AI**.

**Explain buttons** ask for you, in your Windows display language:

- **Diagnostics → Slowdowns**: **Explain** beside each saved slowdown report
- **Alert cards**: **Explain** beside **Show me**
- **Processes**: select one process and press **Explain**

They appear only while the assistant is on.

### Suggestions, never actions

An answer can end with buttons such as **End chrome.exe**, **Record a slowdown now**, **Open
Diagnostics** or **Open Processes**. Nothing happens until you click. Ending a process goes
through the same checks as the process window: the PID must still belong to the same program with
the same start time, core Windows processes are refused, and MicaStats never ends itself. A
process that needs administrator rights is left to the process window's **Retry as
administrator**. Buttons from a cleared conversation do nothing.

### Limits and cost

- One **Send** or **Explain** counts as one question, however many lookups it takes. The default
  limit is **100 a day**, reset at local midnight; **Settings → AI** shows today's count
- At most eight lookups per question and 2,000 output tokens per answer
- A local model that cannot use tools still answers, in **limited mode**, from a short summary of
  the PC's current state

### Privacy

Sent: readings, hardware model names, process names and their paths. Removed first: your profile
folder (shown as `%USERPROFILE%`), other users' folder names, the computer name, your user name,
IP and MAC addresses. Never collected by any tool: window titles, command lines, environment
variables. The diagnostics log records failures only — never questions, answers or data.

### 7-day history

**Keep 7 days of history** records one row a minute — CPU, temperatures, memory, GPU, network,
disk, battery, and the busiest process by CPU and by memory — to `%APPDATA%\MicaStats\history\`,
one CSV file per day. After a day the rows are thinned to one every five minutes; after seven
days the file is deleted, about 10 MB at most. **Delete history** removes it all. The assistant
and MCP use it to answer questions about the past.

### MCP for Claude Desktop and Claude Code

MCP lets an AI app you already use read MicaStats' data directly — no key or cost inside
MicaStats, and **read-only**: the same lookups as the assistant, without suggestions. Choose the
connection in **Settings → AI → MCP**.

**Stdio bridge (recommended).** The AI app starts `MicaStats.exe --mcp`, which reads from the
running MicaStats over a private pipe open to your Windows account only. Nothing listens on the
network.

- **Claude Desktop**: press **Copy Claude Desktop config**, then in Claude Desktop open
  **Settings → Developer → Edit Config** and merge it into `claude_desktop_config.json`:

  ```json
  {
    "mcpServers": {
      "micastats": {
        "command": "C:\\Program Files\\MicaStats\\MicaStats.exe",
        "args": ["--mcp"]
      }
    }
  }
  ```

  Restart Claude Desktop and ask, for example, "Use MicaStats: what slowed my PC down today?"
- **Claude Code**: press **Copy Claude Code command** and run it in a terminal:

  ```powershell
  claude mcp add --scope user micastats -- "C:\Program Files\MicaStats\MicaStats.exe" --mcp
  ```

When MicaStats is not running, the bridge still answers from the files on disk (history and
slowdown reports) and says "MicaStats is not running" for live readings.

> **Pick the mode that matches your client's config.** The private pipe runs only while MCP is
> set to **Stdio bridge**. A Claude Desktop or Claude Code config that starts `MicaStats.exe --mcp`
> while MCP is set to **Local HTTP** gets the files on disk only, and every live reading says
> "MicaStats is not running".

**Local HTTP.** While MicaStats runs it serves MCP at `http://127.0.0.1:47831/mcp` (the port can
be changed) — this PC only, and every request needs the token. **Copy Claude Code command** gives
the complete command with your token:

```powershell
claude mcp add --transport http --scope user micastats http://127.0.0.1:47831/mcp --header "Authorization: Bearer <token>"
```

**Regenerate** makes a new token; a client still using the old one is refused until you copy the
command again. If the port is taken, **Settings → AI** says so and the diagnostics log records it.

> The snippets above use the default install folder and port. The **Copy** buttons always produce
> the exact text for your installation — prefer them.

Setting MCP to **Off** stops the pipe and the HTTP server; an AI app that still calls MicaStats is
told "MCP is turned off in MicaStats Settings".

---

````

Then, in the Overlay Controls list, replace:

```markdown
- **Record Slowdown Now**: Saves the last few minutes of per-process activity to a report. Use
  it immediately after the machine stutters, while the rolling window still holds what happened.
```

with:

```markdown
- **Record Slowdown Now**: Saves the last few minutes of per-process activity to a report. Use
  it immediately after the machine stutters, while the rolling window still holds what happened.
- **Ask MicaStats…**: Opens the AI assistant. Shown only while it is on in **Settings → AI**.
```

- [ ] **Step 4: ROADMAP**

In `ROADMAP.md`, insert immediately **above** the line `## Explicitly not planned`:

```markdown
## MicaStats AI — ships in the next release

An *Ask MicaStats* window and Explain buttons backed by Claude or any OpenAI-compatible endpoint
(Ollama and LM Studio included), an MCP data source for Claude Desktop and Claude Code (stdio
bridge or local HTTP), and a 7-day on-disk metrics history. Everything is off by default, MCP is
read-only, and every result is redacted before it leaves the PC. Design:
`docs/superpowers/specs/2026-09-30-micastats-ai-design.md`.

Next, built on the 7-day history:

- Learned per-PC baselines, and alerts for readings that are unusual *for this PC*
- Forecasts: a drive full in N days, the battery wear trend
- AI notes on alerts, written only when the user asks for them

```

- [ ] **Step 5: Check the edits**

Run (PowerShell tool):

```powershell
(Select-String -Path README.md -Pattern "Ask MicaStats" -Encoding utf8).Count
(Select-String -Path README.md -Pattern "Ctrl\+Alt\+A" -Encoding utf8).Count
(Select-String -Path GUIDE.md -Pattern "AI assistant and MCP" -Encoding utf8).Count
(Select-String -Path ROADMAP.md -Pattern "ships in the next release" -Encoding utf8).Count
git diff --stat -- README.md GUIDE.md ROADMAP.md
```

Expected: at least 4, 2, 1 and 1; the diff touches only the three files. Open `README.md` in MicaPad or VS Code and confirm the Thai section renders (no `?` or mojibake): the Edit tool keeps the file's UTF-8 encoding.

- [ ] **Step 6: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (1153; this task adds none).

```bash
git add README.md GUIDE.md ROADMAP.md
git commit -F - <<'EOF'
docs(ai): Ask MicaStats and MCP in the README, guide and roadmap

English and Thai feature sections, the Ctrl+Alt+A shortcut and the
technology row in the README; a guide section covering setup with
Claude or Ollama, asking, Explain, suggestions, limits, privacy, the
7-day history, and Claude Desktop and Claude Code over the stdio bridge
or local HTTP; the roadmap marks the feature for the next release.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 16: Final verification and hand-off

Proves the whole plan: the suite passes three times in a row, a Release publish contains the new libraries and nothing from ASP.NET Core, the output size is recorded, the diagnostics log cannot receive questions, answers, tool data or secrets, and every spec requirement maps to code. Then the owner gets the manual checklist for what only a real machine, a real key and real MCP clients can show.

**Files:** none changed unless a check fails (a fix then gets its own test and commit).

**Interfaces:** consumes everything from Tasks 1-15; produces nothing new.

- [ ] **Step 1: Whole suite, three times**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj` three times, one after the other.
Expected: every test passes every time, with the same count each run: 1153 (the 817 of the baseline plus the 336 added in Tasks 1-14). A test that fails only sometimes is a bug (usually a missing `UiPump.Wait` or a shared temp path): fix it before going on.

- [ ] **Step 2: Release publish into the scratchpad**

Run (PowerShell tool). `$out` is `%TEMP%\micastats-ai-publish-check`, outside the repository. Never publish into `release-output`: that is the release pipeline's folder.

```powershell
$out = Join-Path $env:TEMP "micastats-ai-publish-check"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish Kil0bitSystemMonitor.csproj --configuration Release --runtime win-x64 --self-contained false --output $out
foreach ($dll in "Anthropic.dll", "Microsoft.Extensions.AI.dll", "Microsoft.Extensions.AI.Abstractions.dll", "Microsoft.Extensions.AI.OpenAI.dll", "OpenAI.dll", "System.ClientModel.dll", "ModelContextProtocol.Core.dll", "ICSharpCode.AvalonEdit.dll", "micapad.ico") {
    "{0,-45} {1}" -f $dll, (Test-Path (Join-Path $out $dll))
}
"ASP.NET Core files: " + (Get-ChildItem $out -Recurse -Filter "Microsoft.AspNetCore*.dll").Count
$mb = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
[string]::Format([Globalization.CultureInfo]::InvariantCulture, "Publish output: {0:0.0} MB", $mb)
if (Test-Path release-output) {
    $previous = (Get-ChildItem release-output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    [string]::Format([Globalization.CultureInfo]::InvariantCulture, "release-output (last release, read only): {0:0.0} MB", $previous)
}
```

Expected: publish succeeds with no new warnings beyond the documented `<NoWarn>` ids; every `Test-Path` prints `True`; `ASP.NET Core files: 0`; the output is roughly the last release's size plus about 16 MB (the spike's measurement). Write both sizes into the hand-off message. Then delete the folder: `Remove-Item -Recurse -Force $out`.

- [ ] **Step 3: Privacy audit of the logging**

Run (PowerShell tool):

```powershell
git grep -n "DiagnosticsLog\." -- Services/Ai Services/History Ai App.Ai.cs
git grep -n -i -E "key|token|secret" -- Models/SystemMetrics.cs
```

Expected: every `DiagnosticsLog` call in the first list logs only an area, a tool name, a provider name, an HTTP status, an exception type or a fixed sentence — never a question, an answer, a tool result, a key or a token, and never `ex.Message` from a provider call. The second list shows no AI key, token or secret property in `AppConfig` (hotkey properties such as `AiHotkey` are fine). Any violation is a bug: move the text into a small pure message builder, pin it with a test that asserts the question, answer or key is absent, then fix the call before continuing.

- [ ] **Step 4: Spec coverage check**

Walk `docs/superpowers/specs/2026-09-30-micastats-ai-design.md` section by section (1-11) and name, for each requirement, the task and the file that implements it and the test that pins it. In particular confirm: §6 limits (8 rounds, 2,000 tokens, daily limit, reset at local midnight, Test connection not counted), §7 (streamed answer, Details line, Stop, New conversation, suggestion buttons re-validated, Explain in the display language, Explain hidden while off), §8 (Off refuses, offline bridge, medium-label pipe, HTTP 401/403/body cap), §9 (every Settings > AI control, secrets never shown or stored in config), §10 (the question stays after a failure, port-in-use shown in Settings). A gap is a bug: fix it with a test first.

- [ ] **Step 5: Hand the manual checklist to the owner**

These need the real machine, real keys and real MCP clients, so the owner runs them after installing a build of this branch (quit the production MicaStats first; the single-instance mutex would otherwise route the new build's launch to it). Report the publish size from Step 2 alongside.

1. **Real Claude key.** Settings → AI: turn the assistant on, provider Claude, paste a key, **Save**. The box turns into **Saved** / **Remove** and the key is not visible anywhere. **Test connection** says "Connected"; the "Used today" count does not change. Open `%APPDATA%\MicaStats\config.json` and search for `sk-ant`: no match.
2. **A first question.** Press **Ctrl+Alt+A**, ask "Why is my PC slow right now?". The answer streams; **Details** lists the lookups with arguments; "Used today" goes up by one. Press **Stop** during a long answer: it stops and the question stays in the box.
3. **Ollama.** Install Ollama, `ollama pull llama3.2`, provider OpenAI-compatible, base URL `http://localhost:11434/v1`, model `llama3.2`, no key. The privacy line reads "Everything stays on this PC (localhost)". Ask a question and get an answer. Then try a model without tool support (for example `gemma:2b`): the answer carries the **limited mode** note.
4. **Claude Desktop through the stdio bridge.** MCP → **Stdio bridge** → **Copy Claude Desktop config**, merge it into Claude Desktop's config, restart Claude Desktop. With MicaStats running in Stdio mode, ask "Use MicaStats to list my top processes by CPU": the tool call succeeds with live data. Quit MicaStats and ask about the last hour: history and slowdown reports still answer, live tools say "MicaStats is not running". Start MicaStats with MCP set to **Local HTTP** and ask again from Claude Desktop: files only, live tools say "MicaStats is not running" (the pipe runs only in Stdio mode). Set MCP **Off**: Claude is told "MCP is turned off in MicaStats Settings".
5. **Claude Code over HTTP.** MCP → **Local HTTP** → **Copy Claude Code command**, run it, then `/mcp` in Claude Code shows micastats connected; ask about the CPU. **Regenerate** the token: the old registration fails with 401 until the new command is added. Occupy the port first (`python -m http.server 47831 --bind 127.0.0.1`), switch to Local HTTP: Settings shows "Port 47831 is in use." and the diagnostics log records it.
6. **Elevated MicaStats, unelevated client.** Run MicaStats as administrator with the stdio bridge, and Claude Desktop normally: the tool calls still return live data (the pipe's medium integrity label works).
7. **Thai.** Ask "ทำไมเครื่องช้าเมื่อเช้านี้?": the answer is in Thai. With Windows' display language set to Thai, an **Explain** question is written in Thai and its date reads 2026, not 2569.
8. **Explain on a real slowdown report.** Diagnostics → Slowdowns → **Record what just happened** while something is busy, then **Explain** on the new report: the answer names the busiest process from that report. Also press **Explain** on an alert card (lower the memory rule's threshold to trigger one) and on a process selected in Processes.
9. **A suggested action.** Start a CPU hog (`powershell -c "while($true){}"`), ask "Something is hogging the CPU, can you stop it?". A suggestion **End powershell.exe (PID n)** appears (the button always names the checked process and PID, whatever the model wrote; the model's reason is the tooltip); nothing happens until you click it; after the click the process is gone. Ask again, press **New conversation**, then click nothing: old buttons are gone. A suggestion for a process that has since exited says so and ends nothing.
10. **Switching off.** Turn the assistant off: the Ask window closes, Ctrl+Alt+A no longer opens it (and another program can take the combination), the overlay menu item and every Explain button disappear.
11. **History.** Turn on **Keep 7 days of history**, wait ten minutes: `%APPDATA%\MicaStats\history\` holds today's CSV with one row a minute. Ask "What was my CPU doing in the last ten minutes?". **Delete history** empties the folder and the size drops to 0 KB.
12. **MCP mode switch.** In Settings → AI switch MCP from **Stdio bridge** to **Off** and back to **Stdio bridge**: the diagnostics log shows "Tool pipe for the stdio bridge started" and "stopped" lines, and a Claude Desktop question gets live data again.
13. **Explain buttons live.** Toggling the assistant on and off shows and hides the **Explain** buttons live, in Diagnostics (slowdown reports) and in the process window. The process-window **Explain** button is enabled only with exactly one process selected.
14. **Clipboard.** **Copy token** and **Copy Claude Code command** (Local HTTP mode) put the text on the clipboard, and it does NOT appear in Windows clipboard history (Win+V). With another app holding the clipboard, Copy shows "The clipboard is busy. Try again." instead of crashing.
15. **Gateway environment variables.** With a variable such as `ANTHROPIC_BASE_URL` set in the user environment (as Claude Code gateways do), a Claude question in MicaStats still goes to Anthropic and works with the key saved in Settings.
16. **Local models.** A local model without tool support (for example `gemma:2b` in Ollama) answers in "limited mode"; a model that stops sending data shows the timeout message after about 60 seconds.
17. **The log stays clean.** After all of the above, open `%APPDATA%\MicaStats\logs\micastats.log` and the bridge's own `mcp-bridge.log` beside it: no question text, no answer text, no key, no token.

- [ ] **Step 6: Finish the branch**

Use the `superpowers:finishing-a-development-branch` skill. The version bump and the release happen after the owner's checklist passes, through the existing release process — not in this plan.

---

