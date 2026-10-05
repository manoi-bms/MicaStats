# AI model limits: implementation and verification

The remaining work from the Claude Code session was completed on `feat/ai-chat-ui`.
The token estimator, budget and provider catalog from Tasks 1–3 were retained; the
unfinished edits were integrated into Tasks 4–6.

## Behavior delivered

- Ask MicaStats uses the selected model's answer and conversation budgets. Old
  exchanges are omitted from requests as whole groups, including their tool calls
  and results; the displayed conversation is retained. A completed turn retains
  the trimming status, and a length-limited answer retains the cut-short note.
- Ask's note reads and retained tool results use token and line limits from the
  budget. The tool description advertises the same line limit. Note access checks,
  credential cleaning, notes take-back and the fixed MCP limits remain in force.
- Model discovery starts in Settings or on AI use. `ModelLimitLearner` owns the
  once-per-model attempt state for the app run, runs the provider lookup off the UI
  thread, and applies results on the caller's UI thread. Results are discarded
  after a model/host change, newer learned settings, AI being disabled or shutdown.
  A cached unknown window may be checked once again in the next app run.
- Settings has editable model choices, Refresh, discovery status, the effective
  limits and an Auto/manual context window. Typing does not request the catalog.
  Request cancellation and generation checks reject stale results. Starting a new
  lookup clears old choices without discarding the typed model name.
- The pending manual-window fallback is complete: when the provider reports
  neither context nor output limits, manually setting a window keeps requested
  outputs at 4,096 tokens or less. Input limits still follow that window.
- README (English and Thai) and GUIDE explain discovery, dynamic limits, defaults
  and the two new Ask notices. `test.ps1` provides the documented low-priority test
  entry point using the installed user-local SDK when present.

## Review findings resolved

- WPF's selection update overwrote a bare model ID with its decorated label. The
  item template now draws the label while the selection text remains the ID.
- Picking Auto could clear the editable text through a nested selection change.
  The pick handler now leaves WPF to finish its selection update.
- Old server choices remained selectable during a refresh. They are now cleared
  before the new request, so old metadata cannot be associated with a new server.
- Final and limited-mode tool-result flattening retained a fixed character cap.
  Both paths now receive the same token cap as retained conversation results.
- The note-tool description still advertised 400 lines; it now names the current
  model budget. An empty answer ending at the length limit no longer loses the
  cut-short note to the generic empty-answer message.

An independent Codex code review checked these fixes and found no remaining
substantive blockers. Focused regression tests cover them, including final versus
tool-round length finishes, valid JSON after note-result shortening, stale model
lists and background learning without network access in tests.

## Verification

- Full suite: `./test.ps1 -NoBuild` — **5,801 passed, zero failed or skipped**, in
  2 minutes 46 seconds. The test host's `BelowNormal` priority was checked while
  it ran. Output is in the local `artifacts/model-limits-full-tests.log`.
- The final focused runs passed: learner/budget **62**, Settings **50**, and
  assistant/window/note-tool wiring **222**. The earlier focused run also checked
  MicaPad, the provider catalog and documentation.
- `git diff --check` passed, and no temporary `MUTATION` marker remained. The
  MicaPad request/consent window files were unchanged; the MicaTools consent checks
  kept their order and only gained budget values passed through to note reads.
- Test compilation reports four existing xUnit1031 warnings in
  `DiagramRendererTests` and `MarkdownExtrasTests`; none is in the changed tests.
- Final Release output is staged in `artifacts/model-limits-release` before local
  deployment, keeping builds away from the running app's `bin/Release` folder.

## Manual checks

The automated tests use fake providers and temporary stores. These checks remain
for a live provider and normal desktop use:

- Open Settings → AI, choose a provider model, refresh, and try a typed model ID.
- Compare Auto with a smaller manual context window and confirm the effective
  limits. Try a provider that reports no context metadata.
- Ask a long follow-up conversation and confirm the retained trimming notice;
  confirm a length-limited answer is labelled as cut short.
- Use MicaPad with a large English/Thai selection and confirm a request is accepted
  or refused according to the displayed model's limits.

No release tag or GitHub publication is part of this implementation.
