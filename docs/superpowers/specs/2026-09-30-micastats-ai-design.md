# MicaStats AI — design

Date: 2026-09-30. Status: approved in brainstorming, awaiting written-spec review.

## 1. Purpose and scope

MicaStats already measures a lot (CPU, GPU, RAM, disks, network, battery, sensors, processes,
slowdown reports, alerts, boot and battery diagnostics) but leaves the reading to the user. This
project makes it answer questions about the PC and explain what it measured, in two ways that share
one set of data tools:

- **A. In-app assistant.** An *Ask MicaStats* window and one-click *Explain* buttons, backed by an
  AI provider the user chooses.
- **B. MCP data source.** Claude Desktop, Claude Code and other MCP clients can query MicaStats'
  data directly, with no AI key or cost inside MicaStats.

Plus the history both need: a **7-day on-disk metrics history**.

Out of scope here (a later sub-project, "C"): learned per-PC baselines, "unusual for this PC"
alerts, forecasts (disk full in N days, battery wear trend), automatic AI notes on alerts. The
7-day history built here is their foundation.

Non-goals: the AI never acts on its own; no background or scheduled AI calls; no shared API key
shipped with the app; no telemetry to the author.

## 2. Decisions (from brainstorming)

| Topic | Decision |
| --- | --- |
| Scope | A + B together, over one shared set of read-only data tools |
| Providers | Claude (Anthropic's official .NET SDK) or any OpenAI-compatible endpoint (base URL + model + optional key: OpenAI, Azure, OpenRouter, Ollama, LM Studio, …) |
| Privacy | Metrics, hardware model names, process names **and paths** may be sent; the user's profile folder is replaced by `%USERPROFILE%`. Never window titles, command lines, environment variables, IP or MAC addresses, the computer name or the user name |
| Surfaces | Ask window (right-click menu + hotkey) and Explain buttons on slowdown reports, alert toasts and process rows |
| Actions | Answers may carry suggested-action buttons; nothing happens until the user clicks, and MicaStats' existing safety checks apply. MCP is read-only |
| History | 7 days: one row per minute for the last 24 h, one row per 5 minutes after that |
| Language | Answers follow the language of the question; Explain questions are written in the Windows display language |
| Settings | A new *AI* section, everything off by default: separate switches for the assistant, the history and MCP, and a choice of MCP connection: Off / stdio bridge / local HTTP |
| MCP transport | Default *stdio bridge*: `MicaStats.exe --mcp` speaks MCP on stdio and forwards to the running app over a per-user named pipe. Optional *local HTTP* on `127.0.0.1` with a bearer token |

## 3. Architecture

All rules live in pure, WPF-free services under `Services/`, tested with xUnit like MicaPad;
windows stay thin.

| Unit | Location | Responsibility |
| --- | --- | --- |
| History | `Services/History/` | `MinuteAggregator` (pure: 1 s snapshots → one row per minute), `HistoryStore` (daily files: append, read a range, thin, clean up), `HistoryRecorder` (wires telemetry to the aggregator and store, owns the once-a-minute process snapshot) |
| Data tools | `Services/Ai/Tools/` | `MicaTools`: the read-only tool catalogue (§5) over narrow data-source interfaces; `Redactor` applied to every result; compact JSON output |
| Assistant | `Services/Ai/` | `AiProviderFactory` (settings → `IChatClient`), `AiAssistant` (conversation, system prompt, tool loop, limits, fallback), `UsageMeter` (daily count), `SuggestedAction` records |
| Secrets | `Services/Ai/SecretStore.cs` | DPAPI (CurrentUser) store for the Claude key, the compatible-endpoint key and the MCP HTTP token |
| Tool pipe | `Services/Ai/Mcp/ToolPipe*.cs` | `ToolPipeServer` in the running app, `ToolPipeClient` in the bridge; length-prefixed JSON (§8) |
| MCP | `Services/Ai/Mcp/` | `McpBridge` (the `--mcp` stdio server), `McpHttpHost` (local HTTP mode), both exposing exactly the §5 tools minus `suggest_action` |
| UI | `Ai/AskWindow.xaml(.cs)`, Explain buttons in `DiagnosticsWindow`, `AlertToastWindow`, `TaskManagerWindow`; *AI* section in `SettingsWindow` | Presentation only |

New packages: `Anthropic` (official Claude SDK, implements `IChatClient`),
`Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI` (OpenAI-compatible endpoints) and
`ModelContextProtocol` (official MCP C# SDK). Exact versions are pinned in the plan.

## 4. History

- **Recording.** Runs only while *Keep 7 days of history* is on. Each telemetry snapshot (1 s)
  feeds `MinuteAggregator`; at each UTC minute boundary it emits one row, appended by
  `HistoryStore`. Once a minute `HistoryRecorder` takes one process snapshot; the CPU-time delta
  from the previous one gives that minute's top process by CPU, and the largest working set the top
  process by RAM (one kernel snapshot per minute, no continuous sampling).
- **Files.** `%APPDATA%\MicaStats\history\yyyyMMdd.csv`, one file per UTC day, UTF-8, first line a
  header with a format version. Timestamps are UTC ISO-8601 (`yyyy-MM-ddTHH:mm:ssZ`), all numbers
  and dates written with `CultureInfo.InvariantCulture`.
- **Columns** (an empty field means unavailable, never 0): `utc`, `seconds` (60 or 300), CPU avg
  and max %, CPU temperature avg and max °C, RAM used avg and max %, GPU avg and max %, GPU
  temperature max °C, network up and down avg kbps, disk: lowest free space % across fixed disks
  (plus total read/write rate if telemetry has it), battery % (last) and on-AC flag, top CPU process
  (name, redacted path, avg %), top RAM process (name, MB). The plan maps each column to its
  `SystemMetrics` field and drops any the telemetry does not expose.
- **Retention.** At startup and at each UTC midnight: a day file whose day ended more than 24 h
  ago is rewritten with 5-minute rows (averages of averages, max of maxes, the most frequent top
  process), and a day file whose day ended more than 7 days ago is deleted. So the latest 24–48 h
  are always per-minute. About 10 MB at most.
- **Reading.** Files are opened with `FileShare.ReadWrite` (the bridge reads while the app
  writes); a half-written last line is skipped; rows are sorted by `utc` on read, so a clock that
  jumps back only reorders. *Delete history* in Settings removes the folder.

## 5. Data tools

Every tool is read-only, returns compact JSON, passes its result through `Redactor`, reports a
missing reading as `"unavailable"` with a reason (never 0), and turns an exception into
`{"error": "<message>"}` instead of throwing.

| Tool | Arguments | Returns |
| --- | --- | --- |
| `get_live_status` | — | Current snapshot (CPU, per-core summary, temps, RAM, GPU, disks, network, battery, sensors) plus min/avg/max over the in-memory ~2 minutes |
| `get_history` | `metric` (enum of §4 columns), `from`, `to` (ISO UTC or relative like `-1h`, `-2d`), optional `maxPoints` (≤ 500) | Rows in range, downsampled to `maxPoints` |
| `get_top_processes` | `by` (`cpu`, `memory`, `disk`), `count` (≤ 15) | Name, redacted path, PID, start time, CPU %, working set MB, disk KB/s; takes a sampler lease, two snapshots 2 s apart, then releases it |
| `list_slowdown_reports` | `limit` (≤ 30) | Id, time, trigger, one-line summary |
| `get_slowdown_report` | `id` | The report text, redacted |
| `list_alerts` | `since` (optional) | The rules (enabled, threshold, sustain) and recent raised/cleared events |
| `get_hardware` | — | CPU, GPU, RAM, board and disk model names and sizes |
| `get_battery` | — | Charge, health, wear, design and full capacity, cycle count if known |
| `get_boot_summary` | — | Recent boot durations and trend |
| `suggest_action` (in-app only) | `kind` (`end_process`, `record_slowdown`, `open_diagnostics`, `open_process_window`), `target` (PID + start time for `end_process`), `reason` | Records a suggestion; executes nothing |

## 6. Assistant

- **Providers.** `AiProviderFactory` builds an `IChatClient` from settings: Claude via the
  `Anthropic` SDK (`AsIChatClient(model)`), or an OpenAI-compatible endpoint via
  `Microsoft.Extensions.AI.OpenAI` with the configured base URL, model and key. Both are wrapped
  with function invocation over `MicaTools`.
- **System prompt.** States what MicaStats is and what the tools return; answer in the language of
  the user's latest message; use tools rather than guessing; say when data is unavailable; never
  claim to have done anything; propose actions only through `suggest_action`; keep answers short
  and practical. The prompt and tool list are marked cacheable where the provider supports it.
- **Limits.** At most 8 tool rounds per question (then the model is asked to answer without
  tools), at most 2,000 output tokens per answer, and a daily question limit (default 100) counted
  by `UsageMeter` and reset at local midnight. One Send or one Explain counts as one question,
  however many tool rounds it takes; *Test connection* does not count.
- **Fallback.** If the endpoint rejects tools (some local models), the question is retried without
  tools with a short live summary (status + top processes) attached, and the answer is marked
  *limited mode*.
- **When it runs.** Only when the user presses Send or Explain. No background calls.

## 7. User interface

- **Ask MicaStats window.** Opened from the right-click menu (*Ask MicaStats…*) and a hotkey
  (default Ctrl+Alt+A, configurable; a hotkey taken by another program is logged the same way as
  MicaPad's). A conversation view with streamed answers, a text box, Send and Stop, and New
  conversation. Under each answer: a *Details* line listing the tools used with their arguments,
  and any suggested-action buttons.
- **Explain buttons.** On a slowdown report (Diagnostics > Slowdowns), on an alert toast and on a
  process row (process window). Each opens the Ask window and sends a question naming the item, in
  the Windows display language, e.g. "Explain slowdown report 2026-09-29 14:02", "Why did the CPU
  temperature alert fire?", "What is svchost.exe (PID 1234) doing?". Hidden while the assistant is
  off.
- **Suggested actions.** A button such as *End chrome.exe* or *Record a slowdown now*. On click,
  MicaStats re-checks the target (for a process: same PID and start time, as `ProcessControl`
  already does), applies the critical-process guard and the existing elevation path, then acts.
  Buttons stop working when their conversation is cleared.

## 8. MCP

- **Stdio bridge (default mode).** The user adds MicaStats to Claude Desktop or Claude Code with
  the snippet from *Copy config* (full path to `MicaStats.exe`, argument `--mcp`). In `--mcp` mode
  the process skips the single-instance mutex, all UI and all monitoring, writes nothing but MCP
  protocol to stdout (logs go to the diagnostics log), serves the tool list, and forwards each call
  to `\\.\pipe\MicaStats.Tools.<user SID>`.
- **Pipe protocol.** Length-prefixed UTF-8 JSON: request `{"v":1,"tool":"…","args":{…}}`, reply
  `{"v":1,"ok":true,"result":…}` or `{"v":1,"ok":false,"error":"…"}`. Up to 4 concurrent clients,
  10 s per call; a version mismatch returns a clear error. The pipe's security descriptor grants
  only the current user's SID and carries a medium integrity label, so an unelevated client can
  connect when MicaStats runs elevated.
- **Offline.** When no pipe answers, the bridge serves `get_history`, `list_slowdown_reports` and
  `get_slowdown_report` from the files directly and returns "MicaStats is not running" for live
  tools.
- **Off.** When the MCP setting is Off (read from `config.json`), the pipe and HTTP servers do not
  run and every bridge call returns "MCP is turned off in MicaStats Settings".
- **Local HTTP mode.** The running app serves MCP at `http://127.0.0.1:<port>/mcp` (default port
  47831): bound to loopback only, bearer token required (401 otherwise), Host/Origin must be
  localhost (403 otherwise, against DNS rebinding), request bodies capped at 1 MB. Only while
  MicaStats runs. The plan verifies whether the MCP SDK can serve this without the ASP.NET Core
  runtime; the design requires that no extra runtime be installed for users who leave it off.
- **Exposed tools.** Exactly §5 minus `suggest_action`.

## 9. Settings, secrets, privacy and cost

- **Settings > AI** (new section after MicaPad; everything off by default):
  - *Assistant* switch; provider (*Claude* / *OpenAI-compatible*); Claude: API key and model
    (default `claude-haiku-4-5`; `claude-sonnet-5-5`, `claude-opus-5-5` or any typed name);
    compatible: base URL, model, optional key; *Test connection*; hotkey; daily question limit.
    A one-line note says where data goes ("stays on this PC" for a loopback URL, else the host).
  - *Keep 7 days of history* switch, current size, *Delete history*.
  - *MCP for Claude Desktop/Code*: Off / Stdio bridge / Local HTTP; *Copy Claude Desktop config*
    and *Copy Claude Code command*; for HTTP: port, token with *Regenerate* and *Copy*.
- **Config.** New `AppConfig` properties (names final in the plan): `AiAssistantEnabled`,
  `AiProvider`, `AiClaudeModel`, `AiCompatibleBaseUrl`, `AiCompatibleModel`, `AiHotkey`,
  `AiDailyLimit`, `AiHistoryEnabled`, `AiMcpMode`, `AiMcpHttpPort`. No secret is ever stored in
  `config.json`.
- **Secrets.** `%APPDATA%\MicaStats\secrets.bin`, DPAPI with CurrentUser scope, holding the Claude
  key, the compatible key and the MCP HTTP token. Never logged, never shown again after saving (the
  field shows *Saved* and *Remove*).
- **Redaction** (every tool result, in-app and MCP): the user's profile folder → `%USERPROFILE%`;
  any other `C:\Users\<name>` → `C:\Users\<user>`; the computer name and user name removed; IP and
  MAC addresses dropped. Window titles, command lines and environment variables are never
  collected by any tool.
- **Logging.** The diagnostics log records failures only (provider, HTTP status, tool name), never
  questions, answers or tool data.

## 10. Errors and edge cases

- **Provider.** No key → a prompt with a button to Settings > AI. 401 → "The key was rejected".
  Network failure or no reply in 60 s → message with *Retry*. 429/529 → the SDK retries twice, then
  a wait message. Unknown model → the server's message. Tools unsupported → limited mode (§6).
  Stop and closing the window cancel the request. The question stays in the box after any failure.
- **Tools.** Failures become `{"error": …}` for the model; missing sensors are `unavailable`; the
  8-round cap forces a final answer.
- **Actions.** Re-validated on click (§7).
- **History.** A failed write is logged once and retried next minute; half-written lines skipped;
  clock jumps tolerated; UTC storage, local display, InvariantCulture throughout.
- **MCP.** Pipe busy or slow → per-call error; port in use → Settings shows it and it is logged;
  settings changes take effect at once (turning MCP off stops both servers; turning the assistant
  off hides Explain and frees the hotkey; a provider change applies from the next question).

## 11. Testing

xUnit, as for MicaPad. No test calls a real provider or the network, touches `%APPDATA%` (temp
roots, no-op logging) or launches MicaStats.

- History: aggregation (averages, peaks, boundaries, unavailable), store (append, range read,
  thinning, 7-day cleanup, half-written line, clock jump, InvariantCulture under a Thai culture).
- Redactor: table-driven cases for each rule in §9.
- Tools: each against fake data sources — JSON shape, `unavailable`, `{"error"}`, sampler lease
  released.
- Assistant: a scripted fake `IChatClient` — tool loop, 8-round cap, daily limit and reset,
  limited-mode fallback, cancellation, `suggest_action` recorded but not executed.
- Secrets: DPAPI round trip; the key never appears in serialized config or the log.
- Pipe: a test pipe name — request/reply, version mismatch, timeout, offline fallback.
- MCP: the SDK client over in-memory streams — tool list equals the read-only set, calls return
  data, Off refuses. HTTP on a random free port — 401, 403, 200, body cap.
- UI (never-shown windows): the Ask window renders a streamed answer, *Details* and a suggestion
  button whose click re-validates through a fake executor; Settings has the AI section; the
  assistant switch hides Explain.

**Manual checklist (owner, after the build):** a real Claude key; Ollama locally; Claude Desktop
through the stdio bridge; Claude Code over HTTP; MicaStats elevated with an unelevated client; a
Thai question answered in Thai; Explain on a real slowdown report.

## 12. To verify during planning

- Current versions and APIs of `Anthropic`, `Microsoft.Extensions.AI(.OpenAI)` and
  `ModelContextProtocol`, including prompt caching through `IChatClient` and streaming with tools.
- Whether the MCP SDK's HTTP server can run without the ASP.NET Core runtime (for example over
  `HttpListener`); if not, how local HTTP mode is delivered without adding a runtime for everyone.
- That `MicaStats.exe` (a GUI-subsystem app) can serve stdio when launched with redirected handles;
  if not, a tiny console companion executable is shipped instead.
- The pipe security descriptor with a medium integrity label.
- Which `SystemMetrics` fields back each history column.
