# AI: the model's own limits, and a model list in Settings — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app learns the selected model's context window and largest output from the provider, sizes what it sends and accepts from them, trims a long Ask conversation to fit, and lets the user pick the model from a list loaded from the provider.

**Architecture:** A pure `AiBudget` turns a window (or none) into every limit the AI code uses; with no window it is exactly today's numbers. A `ModelCatalog` asks the provider for its models and their limits over the same HTTP path as a question; what it learns for the chosen model is kept in the settings with the provider, address and model it belongs to. `App` resolves the budget for the current settings and hands it to Ask MicaStats and MicaPad where they are built. The settings page turns the two model boxes into lists filled from the catalog.

**Tech Stack:** C# / .NET 8 / WPF, ModernWpf, `Microsoft.Extensions.AI` 10.10, the `Anthropic` SDK 12.51 (its model list carries `MaxInputTokens` and `MaxTokens`), the `OpenAI` SDK 2.14 (its model type has only an id, so the compatible list is read as raw JSON), xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-05-ai-model-limits-and-model-list-design.md` (binding). The AI specs before it still bind what is sent and when: `docs/superpowers/specs/2026-10-03-micapad-ai-writing-and-answers-design.md`, `docs/superpowers/specs/2026-10-04-micapad-ai-note-tools-and-diagrams-design.md`, `docs/superpowers/specs/2026-10-04-ai-chat-ui-design.md`.

## Global Constraints

- **With the window unknown, nothing changes.** `AiBudget.Standard` holds today's numbers exactly (Ask output 2,000 tokens; MicaPad output 4,096; rewrite 8,000 characters; read 24,000 characters; reply 64,000 characters; kept tool result 20,000 characters; `get_note` 16,000 characters and 400 lines; 8 passages). Every existing test that pins one of them passes unchanged with no window known.
- **Consent.** Do not add, remove, move or reorder any `AiIsOff()` check in `Pad/MicaPadWindow.Ai.cs` or `Pad/MicaPadWindow.AskNotes.cs`, or the per-surface checks in `Services/Ai/Tools/MicaTools.cs`. No task adds a path to `runner.RunAsync`. Nothing is awaited between the last check and the request.
- **The provider is asked for its models only** in Settings → AI, or in the background at the first AI request of a run whose model limits are unknown. Never while every AI feature is off (`AiAssistantEnabled` and `PadAiEnabled` both false and the settings page not asking), and never at startup by itself.
- **The list request goes only to the configured provider** (`https://api.anthropic.com` for Claude; the configured base URL for a compatible server), with the saved key, through the same `HttpMessageHandler` seam and timeout rules as `AiProviderFactory`. The key is never logged or shown.
- **What a provider returns is data.** Ids: plain text, at most 200 characters, control and format characters removed, empty dropped, at most 500 models. Window: a whole number from 1,024; over 2,000,000 counts as 2,000,000; anything else is unknown. Output limit: never above the window. The answer is read up to 4 MB.
- **Ceilings that no provider can raise:** Ask output 16,000 tokens; MicaPad output 32,000 tokens; `get_note` 64,000 tokens of text and 4,000 lines; a kept tool result 80,000 tokens; 20 passages.
- **What goes into a note, and what a pane names, do not change.** Only how much may be sent.
- **Credentials.** Every text still passes the existing masking and cleaning before it is sent; no part of a `{{secret:ID}}` marker leaves the PC.
- **Exact strings:**
  - "This text is too long for a rewrite with this model: about {n} tokens, and it can take about {m}. Select less text."
  - "This text is too long for this model: about {n} tokens, and it can take about {m}. Select less text."
  - "The answer was cut short at the length limit."
  - "Earlier turns are no longer sent to the model: the conversation is longer than it can take."
  - Settings: "Loading…"; "{count} models from {host}" ("1 model from {host}"); "{problem} You can still type a model name."; "Context window {tokens} tokens (from the server) · answers up to {out} tokens · MicaPad reads up to about {read} tokens of text"; with the user's number: "(set here)" in place of "(from the server)"; "Context window not known for this model: the standard limits are used. Set it below if you know it."; a list line: "{id} · {tokens} tokens", or the id alone; "Refresh"; "Auto".
  - Numbers in sentences use `N0` with the invariant culture ("262,144").
- **Logging:** a failure by its exception type and the host only. Never a key, a path or query of an address, or any text.
- **Tests:**
  - never touch `%APPDATA%`, never launch MicaStats, use no network (a scripted `HttpMessageHandler`, as `AiProviderFactoryTests` and `AiAssistantTests` do) and no real clipboard;
  - UI tests run only on the shared `UiThread`;
  - never block on a task in a `[Fact]` body (xUnit1031).
- **Culture:** the machine is th-TH. `CultureInfo.InvariantCulture` for numbers, `StringComparison.Ordinal` for comparisons.
- **Unicode escapes:** write `(char)0x....` instead of `\uXXXX` in source. Literal "…" and "·" are fine.
- **No real service address** in any file: an example host is `llm.example.com`.
- **Run tests only through the low-priority runner** (the owner works on this PC): `& '<workspace>\test.ps1' -Filter "FullyQualifiedName~AiBudgetTests"` (`-Detail` for messages, `-NoBuild` to skip the build; no `-Filter` runs the whole suite). Class filters while working; the full suite once, at the end of a task. Never several full runs in a row. After restoring a mutated file, set its timestamp to now so the build recompiles it.
- Never build into `bin\Release`. The app builds with 0 warnings. The suite is 5,276 tests before this plan.
- **Mutation checks:** mark each temporary line with the word `MUTATION`, restore every one, and confirm `git grep -nI --untracked "MUTATION" -- . ":(exclude)docs"` prints nothing before each commit.
- **Commits:** explicit `git add` of your files, `-m`, never amend, never reset, never `--no-verify`, with a `Co-Authored-By` trailer naming your own model.
- **README constraint:** the bullet starting `* **Markdown the way Wiki.js shows it**` stays one single line containing **Copy**. Keep a blank line after every list and before every heading. Text in angle brackets is dropped by a Markdown renderer: write it in backticks.
- **Load-sensitive tests that are not yours:** `SearchPaneTests.A_search_runs_off_the_UI_thread`, `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn`, `SearchPipelineTests.A_hanging_embedding_server_times_out_to_words_only`, `PadAiRunnerTests.Every_update_re_arms_the_silence_deadline`, `PadAiRunnerTests.A_slow_consumer_does_not_trip_the_silence_deadline`, `ImageBoardTests.A_line_with_an_image_gets_a_preview_under_it`, and the real-page tests in `DiagramPageTests`. If only those fail in the full run, rerun that class once and say so.

## Review Focus

1. **A provider that reports nothing** (OpenAI, Ollama), or no list at all: every limit is today's, nothing is refused that was accepted before, and no request waits on the list. (Tasks 1, 3, 4)
2. **A hostile or broken list:** a window of -1, 0, 1e30, a string; 10,000 models; ids with control characters or 5,000 characters; an answer of 50 MB; HTML in place of JSON. Nothing throws, nothing is shown as markup, and no limit passes a ceiling. (Task 2)
3. **Thai text.** A Thai note of 50,000 characters is about 50,000 tokens by the estimate; it must be refused for a small window and accepted for a large one, and the sentence must say tokens. (Tasks 1, 3)
4. **A long Ask conversation.** After trimming, every tool call sent still has its result and every result its call; the system prompt and the question are there; a conversation that read notes is still taken back when access changes. (Task 4)
5. **All AI off.** With `AiAssistantEnabled` and `PadAiEnabled` both false, starting the app and using it for an hour asks no provider for anything. (Tasks 2, 4)
6. **The settings page.** Typing in the model box never asks the provider; a list that arrives after the provider was switched is dropped; a typed name that is not in the list is kept. (Task 5)

---

### Task 1: The token estimate and the budget

**Files:**
- Create: `Services/Ai/TokenEstimate.cs`, `Services/Ai/AiBudget.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/TokenEstimateTests.cs`, `tests/Kil0bitSystemMonitor.Tests/AiBudgetTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class TokenEstimate
  {
      /// A quarter of a token for each ASCII character, one for each other UTF-16 unit that is not the low half of a pair, rounded up. Null or empty is 0. Never throws; linear.
      public static int Of(string? text);
  }

  public sealed record AiBudget
  {
      public int ContextTokens { get; init; }          // the window in use; 0 when unknown
      public int AskOutputTokens { get; init; }
      public int PadOutputTokens { get; init; }
      public int RewriteInput { get; init; }           // characters when ContextTokens is 0, estimated tokens otherwise
      public int ReadInput { get; init; }              // the same
      public int PadReplyChars { get; init; }
      public int HistoryTokens { get; init; }          // an Ask conversation as sent again; 48,000 when the window is unknown
      public int NoteReadTokens { get; init; }         // get_note in Ask, per call; 0 when unknown (the caller then uses its character cap)
      public int NoteReadLines { get; init; }
      public int KeptResultTokens { get; init; }       // a tool result kept in the conversation; 0 when unknown
      public int NotesSources { get; init; }
      public bool InTokens => ContextTokens > 0;

      public static AiBudget Standard { get; }         // today's numbers; ContextTokens 0
      /// reportedContext and reportedOutput as the provider gave them (0 when it gave none); userContext the number set in Settings (0 for Auto).
      public static AiBudget For(int reportedContext, int reportedOutput, int userContext);
      /// The window in use: the user's number when the provider gave none; the smaller of the two when both; the provider's when the user set none; 0 when neither.
      public static int WindowInUse(int reportedContext, int userContext);
  }
  ```

**Rules:** the table of spec section 2.2, exactly, with integer division rounding down. `Standard` is: 0, 2000, 4096, 8000, 24000, 64000, 48000, 0, 400, 0, 8. A window under 1,024 from either source counts as none. A window over 2,000,000 counts as 2,000,000. A reported output over 0 caps both output numbers; it never raises them.

- [ ] **Step 1: Write failing tests:** the estimate for empty, ASCII (4 characters is 1; 5 is 2), Thai (one each), an emoji (one), mixed text, a 5-million-character string in linear time; `Standard`'s eleven numbers; `For` at 8,192, 32,000, 128,000, 262,144 and 1,048,576 (every column of the spec's table); a reported output of 8,192 capping both outputs and the rewrite input that follows from it; `WindowInUse` for the four combinations and for values under 1,024 and over 2,000,000; no number above its ceiling for any window from 0 to `int.MaxValue` (a loop over powers of two and their neighbours).
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the two classes, then the full suite.**
- [ ] **Step 5: Commit** (`feat(ai): a token estimate, and the limits that follow a model's context window`).

---

### Task 2: Asking the provider for its models

**Files:**
- Create: `Services/Ai/ModelCatalog.cs`
- Modify: `Services/Ai/AiProviderFactory.cs` (share how the HTTP client, the key and the address are made; no behaviour change), `Models/SystemMetrics.cs` (the settings below)
- Test: `tests/Kil0bitSystemMonitor.Tests/ModelCatalogTests.cs`, `AiConfigTests.cs`

**Interfaces:**
- Consumes: `AiProviders`, `SecretStore`, `SecretNames`, `AiProviderFactory`'s handler seam and `RequestTimeout`.
- Produces:
  ```csharp
  public sealed record AiModelInfo(string Id, int ContextTokens, int MaxOutputTokens);   // 0 when not reported
  public sealed record AiModelList(IReadOnlyList<AiModelInfo> Models, string? Problem, string Host);   // Problem is a sentence for the user, or null

  public static class ModelCatalog
  {
      public const int MaxModels = 500, MaxIdChars = 200, MaxAnswerBytes = 4 * 1024 * 1024;
      public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);
      /// Asks the provider of these settings for its models. Never throws: a failure is a Problem.
      public static Task<AiModelList> ListAsync(AppConfig config, SecretStore secrets, HttpMessageHandler? handler, CancellationToken ct);
      /// The compatible server's list as it is parsed: pure, for tests.
      internal static IReadOnlyList<AiModelInfo> ParseCompatible(string json);
      /// What the limits belong to: provider, the address's scheme, host and port, and the model, in one string.
      public static string KeyOf(AppConfig config);
  }

  // AppConfig (names start with "Ai" on purpose; see the existing ApplyAiSettings):
  public int AiContextWindow { get; set; }        // the user's number; 0 is Auto; else clamped 1,024..2,000,000
  public int AiModelContext { get; set; }         // last reported for the chosen model; 0 unknown
  public int AiModelOutput { get; set; }          // the same, largest output
  public string AiModelLimitsOf { get; set; }     // ModelCatalog.KeyOf when the two were learned; "" when never
  ```

**Rules:**
- Claude: through the SDK's model list with the same client settings `AiProviderFactory` uses (key, base URL, retries, handler). Follow pages until none is left or `MaxModels` is reached. No key: `Problem` is the factory's existing sentence for a missing key, and nothing is sent.
- Compatible: `GET {base}/models` (`base` trimmed of a trailing slash), `Authorization: Bearer {key}` only when a key is saved, `Accept: application/json`. A base URL that is not http or https: the factory's existing sentence, nothing sent. Parse `data` (an array) or, when the answer is itself an array, that. Field order for the window and the output as in spec 1.1; a number may arrive as a JSON number or a string of digits.
- Sanitising and ceilings as in the Global Constraints. Models keep the provider's order; duplicates of an id are dropped after the first.
- A failure sentence comes from `AiErrorText.Describe` where one exists for the exception; it never holds the key.
- The settings: defaults 0, 0, 0, "". Setting the provider, a model or the base URL does not clear the learned limits; they are ignored by whoever reads them when `AiModelLimitsOf` differs from `KeyOf(config)`.

- [ ] **Step 1: Write failing tests** (scripted handler; never the network): the compatible list with each window field name and each output field name; `data` and a bare array; numbers as strings; a missing, negative, zero, fractional, huge and non-numeric window; ids with control characters, of 5,000 characters, empty, duplicated; 10,000 models; an answer over 4 MB; HTML; an empty body; a 401, a 404, a timeout, a connection failure (each a sentence, none throws, none holds the key); the request line, the `Authorization` header with and without a key, and that the host asked is the configured one; a base URL with a trailing slash and with a path; the Claude list over two pages with `max_input_tokens` and `max_tokens`; Claude with no key sends nothing; `KeyOf` differs when the provider, the host, the port or the model differs and is the same for a different path case only if the address is; the four settings' defaults, clamping and round trip.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the touched classes, then the full suite.**
- [ ] **Step 5: Commit** (`feat(ai): the provider is asked for its models and their limits`).

---

### Task 3: MicaPad uses the budget

**Files:**
- Modify: `Services/Pad/Ai/PadAiAction.cs`, `Services/Pad/Ai/AiSession.cs`, `Services/Pad/Ai/PadAiRunner.cs`, `Services/Pad/Ai/NotesQuestion.cs`, `Pad/MicaPadWindow.Ai.cs` and `Pad/MicaPadWindow.AskNotes.cs` (only where a session, a runner or the sources are built: one more value passed), `App.Ai.cs` (`CurrentBudget`, `CreatePadAiRunner`)
- Test: `PadAiActionTests.cs`, `AiSessionTests.cs`, `PadAiRunnerTests.cs`, `NotesQuestionTests.cs`, `MicaPadAiTests.cs`

**Interfaces:**
- Consumes: Task 1's `AiBudget`, `TokenEstimate`; Task 2's settings and `ModelCatalog.KeyOf`.
- Produces:
  ```csharp
  // App:
  internal static AiBudget CurrentBudget();           // AiBudget.For(learned limits if AiModelLimitsOf == KeyOf(config) else 0, 0, AiContextWindow); Standard without a config
  // PadAiAction:
  public string? TooLong(string text, AiBudget budget);   // null when it fits; the existing TooLong(int chars) stays and is what a Standard budget uses
  // AiSession constructor gains one optional parameter at the end: AiBudget? budget = null (null is Standard)
  // PadAiRunner: its constructor or RunAsync takes the budget's PadOutputTokens and PadReplyChars; the existing constants are the defaults
  // NotesQuestion: Sources(hits, int max = MaxSources)
  // MicaPadWindow gains a seam beside AiDestination and AiModel:
  internal Func<AiBudget> AiBudgetNow { get; set; }   // default App.CurrentBudget; a failure to read it is Standard
  ```

**Rules:**
- With `budget.InTokens` false: every behaviour and sentence is today's, through the existing code paths.
- With it true: the source text is measured with `TokenEstimate.Of`; over `RewriteInput` (a rewrite) or `ReadInput` (the others) the session is refused with the sentence of the Global Constraints, the two numbers in `N0`.
- The runner sends `PadOutputTokens` as the output limit and stops a reply at `PadReplyChars`.
- Ask your notes sends up to `NotesSources` passages (never more than the search returned).
- The budget is read once, where the session is built, by the window's seam: synchronous, guarded like the other seams, nothing awaited. The same budget is used for the refusal and for the request of that session.

- [ ] **Step 1: Write failing tests:** `TooLong` with a token budget for an ASCII text and a Thai text of the same length (the Thai one refused, the ASCII one accepted), at the limit and one over, and the two sentences word for word; with `Standard` the old sentences; a session refused by the budget makes no request (through the window); the runner's request carries the budget's output limit and stops at its reply cap; 20 sources at a 262,144 window and 8 with none; the window reads the budget once per request and a throwing seam gives the standard limits.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** with `git diff` that no line containing `AiIsOff` or `RunAsync` was added, removed or moved in the two window files.
- [ ] **Step 5: Run the touched classes, then the full suite.**
- [ ] **Step 6: Commit** (`feat(micapad): AI actions take as much text as the model can, and say so in tokens when it is too much`).

---

### Task 4: Ask MicaStats uses the budget, trims a long conversation, and says when a reply was cut

**Files:**
- Create: `Services/Ai/ConversationTrim.cs`
- Modify: `Services/Ai/AiAssistant.cs`, `Services/Ai/FinalAnswerChatClient.cs` (`ToolHistory`'s kept-result cap takes a value), `Services/Pad/Ai/NoteTools.cs` (a per-call limit for the Ask path), `Services/Ai/AiToolFunctions.cs`, `Services/Ai/Tools/MicaTools.cs` (only to pass the limit through for Ask; the per-surface checks stay as they are), `App.Ai.cs` (`CreateAskSetup`, the background list request), `Ai/AskWindow.xaml.cs` (the two new lines)
- Test: `ConversationTrimTests.cs` (new), `AiAssistantTests.cs`, `NoteToolsTests.cs`, `AiAskWindowTests.cs`, `NoteToolsWiringTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2; Task 3's `App.CurrentBudget`.
- Produces:
  ```csharp
  public static class ConversationTrim
  {
      /// The messages to send: the system message, then the newest whole exchanges that fit maxTokens (by TokenEstimate over text, tool arguments and tool results), always including the last user message. An exchange is a user message and everything up to the next user message. dropped says whether anything was left out.
      public static IReadOnlyList<ChatMessage> Fit(IReadOnlyList<ChatMessage> messages, int maxTokens, out bool dropped);
  }
  // AiAssistantOptions gains: AiBudget Budget (default AiBudget.Standard). MaxOutputTokens, when not set by a test, is Budget.AskOutputTokens.
  // AssistantUpdateKind gains: CutShort, Trimmed (each with no text of its own; the window owns the sentences)
  // NoteTools.GetNoteAsync gains an optional limit (estimated tokens and lines) used by the Ask path; MCP passes none and keeps MaxChars and MaxLines.
  // App: internal static void LearnModelLimitsOnce();   // the background request of spec 1.3
  ```

**Rules:**
- Every request of a question (each tool round, the final answer, limited mode) sends `ConversationTrim.Fit(…, Budget.HistoryTokens)`. The conversation object is not changed by it. `Trimmed` is yielded once per question when something was dropped.
- A finish reason of `Length` on the final answer yields `CutShort`. The window shows the sentence as the turn's note; a stopped or failed answer keeps its own note.
- The kept-result cap is `Budget.KeptResultTokens` (estimated tokens) when the budget is in tokens, else the existing 20,000 characters; a note result over it is still shortened as valid JSON by the existing `NoteTools.Shortened`.
- `get_note` in Ask: up to `Budget.NoteReadTokens` estimated tokens and `Budget.NoteReadLines` lines; the `lineCount` the model may ask for is clamped to that. `truncated` and `cutInLine` behave as today.
- `LearnModelLimitsOnce`: at most once per run and per `ModelCatalog.KeyOf`; only when the limits for the current key are not known; only when called from a place where an AI request is being made (Ask's setup, MicaPad's runner factory); on a background task that never throws; on success it writes the three settings for the chosen model (and saves) if the key still matches. It is never called at startup and never when both AI switches are off.
- The notes take-back (`ToolHistory.TakeBackNotes`), `NotesRead`/`NotesEverRead` and the link rules are untouched; trimming happens after the take-back, on the list about to be sent.

- [ ] **Step 1: Write failing tests:** `Fit` with nothing to drop; dropping the oldest exchange whole; an exchange with tool calls and results dropped together (never a call without its result); the last user message always kept even when it alone is over the limit; the system message always first; a result's size counted; `dropped`; an assistant request carries the budget's output limit; `CutShort` for a final `Length`, not for a tool round; `Trimmed` once; the kept cap in tokens; `get_note` at a 262,144 window returns more than 16,000 characters of ASCII text and not more than the estimate allows of Thai text, and MCP still gets 16,000; the window shows the two sentences; `LearnModelLimitsOnce` asks once, writes the settings, does nothing with both switches off, and a failure changes nothing.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the touched classes, then the full suite.**
- [ ] **Step 5: Commit** (`feat(ai): Ask MicaStats sizes its answers and its conversation to the model, and says when an answer was cut short`).

---

### Task 5: Settings → AI: a model list, the limits in use, the context window

**Files:**
- Modify: `Ai/AiSettingsPanel.xaml`, `Ai/AiSettingsPanel.xaml.cs`, `Ai/AiSettingsHost.cs`
- Test: `AiSettingsTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces:
  ```csharp
  // AiSettingsHost gains a seam: Func<AppConfig, SecretStore, CancellationToken, Task<AiModelList>> ListModels   // default ModelCatalog.ListAsync with no handler
  // AiSettingsPanel: ClaudeModelBox and CompatibleModelBox become editable ComboBoxes (same names); new: RefreshModelsButton, ModelStatusText, ModelLimitsText, ContextWindowBox (editable ComboBox)
  ```

**Rules:**
- The list is asked for: in `Load`, when the provider changes, when the base URL lost focus with a changed value, when a key was saved or removed, and by **Refresh**. A request under way is cancelled by a newer one; an answer from an earlier request or for the other provider is dropped (the existing `_testRun` pattern).
- While it loads: "Loading…". Then the status sentence. The box's items are the models, each shown as "{id} · {tokens} tokens" or the id alone; the box's text is always the bare id.
- A typed name is saved on lost focus, as today. A pick is saved at once. Either way, if the name is in the list, its limits are written to `AiModelContext`, `AiModelOutput` and `AiModelLimitsOf`; if it is not, they are left as they are (and so no longer match the key).
- `ModelLimitsText` is rebuilt from `AiBudget.For` of the current settings whenever the model, the list or the context window changes.
- `ContextWindowBox`: "Auto" and 8,000, 16,000, 32,000, 64,000, 128,000, 200,000, 256,000, 1,000,000; a typed number is accepted (digits, with or without separators), clamped by the setting; anything else returns to what was saved.
- Test connection's result adds " · {tokens}-token context" when the window in use is known.
- No keystroke in any box asks the provider.

- [ ] **Step 1: Write failing tests** (a fake `ListModels`): `Load` asks once and fills the box of the current provider; the lines' text; a pick saves the model and its limits and makes one save; a typed name not in the list is kept and leaves the limits unmatched; typing asks nothing; the base URL change, a key saved and removed, the provider change and Refresh each ask once; an answer that arrives after a provider switch is dropped; a failure shows its sentence and the box still accepts a name; the limits line for a known window, for the user's number, and for none; the context window box for Auto, a list value, a typed number, nonsense; the existing settings tests still pass (the count of saves in `Changing_a_control_writes_the_config_and_saves` is updated only if a new save is really made, and say why).
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the class, then the full suite; build with 0 warnings.**
- [ ] **Step 5: Commit** (`feat(ai): the model is picked from the provider's list, and Settings shows the limits in use`).

---

### Task 6: Docs

**Files:**
- Modify: `GUIDE.md`, `README.md` (English and Thai)
- Test: the docs checks in `PadAiSettingsTests.cs` (or the class that holds them)

**Rules:**
- GUIDE: where the providers are described, the model list, Refresh, the limits line, the context window setting (what Auto means; why a number: cost, or a server that reports none); where the size limits are stated (8,000 and 24,000 characters, 2,000 output tokens, 16,000 characters for `get_note`), say they are the standard limits used when the model's window is not known, and that with a known window they follow it, with the short table of spec 2.2; the two new Ask lines; that the list is asked for only in Settings or when AI is used.
- README, English and Thai: the same in short, in the existing AI bullets and the "Since v1.14.0" block.
- No real service address; `llm.example.com` for an example. Blank lines and backticks as in the Global Constraints.
- Docs checks for the new strings, in both languages, written first.

- [ ] **Step 1: Write the failing docs checks.**
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Write the docs.**
- [ ] **Step 4: Run the touched classes, then the full suite.**
- [ ] **Step 5: Commit** (`docs: the model list, the context window, and limits that follow the model`).
