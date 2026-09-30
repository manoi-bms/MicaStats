# MicaStats AI — follow-ups after the first release

These are review findings deliberately left out of the AI branch (`feat/ai`). None of them leaks
data or breaks a feature on its own, and none blocks the release. The final whole-branch review
triaged every deferred item; this list keeps the ones worth doing later, grouped by area.

## Assistant and providers

- The in-app assistant is built per question, so a local endpoint that rejects tools costs one
  failing request with tools on every question. Cache the "no tools" result per provider settings.
- A new HTTP handler per question (never pooled); a shared handler would reuse connections.
- Tool execution time counts against the 60 s silence window, and a 429/529 whose SDK retry waits
  exceed 60 s reads as a timeout rather than "busy".
- A proxy that returns an HTML error page (502) is shown raw (cut to 300 characters); show the HTTP
  status instead.
- The Claude cache breakpoint is below Haiku 4.5's caching minimum (ignored, harmless); the 1-hour
  TTL doubles the write price on Sonnet and Opus.
- An API key is sent over plain `http://` to a non-local compatible endpoint without a warning in
  the privacy note.
- `UsageMeter.TryConsume` writes its small file on the thread that enumerates `AskAsync` (the UI).
- Breaking out of `AskAsync` early (instead of cancelling) leaves the question in the conversation.

## Ask window, Explain and Settings

- WPF treats the first `_` in a button text as an access key, so an **End my_app.exe (PID n)**
  button shows `myapp.exe`; wrap the text in a `TextBlock`.
- UI writes from an old answer's stream (status line, error) are not gated on the conversation
  generation after **New conversation** (the assistant normally sends nothing after a cancel).
- Explain on an alert toast is decided when the card is created; switching the assistant off during
  the card's 30 s life still shows Explain (the Ask window then says the assistant is off).
- Explain on a report missing from the list falls back to the current time.
- **Ask** overwrites a question already typed in the box while an answer is running.
- **Regenerate** with no previous token and a locked secret file says "the old one still works".
- No test that **Test connection** runs off the UI thread / disables its button mid-flight; the
  daily-limit lower clamp and non-number input are untested.

## MCP (pipe, bridge, HTTP host)

- `mcp-bridge.log`: every MCP client starts its own bridge, and two bridges appending at the same
  moment can overwrite part of each other's line. Open with append-only rights or put the PID in
  the file name.
- A pipe reply over 1 MB closes the connection without an `ok:false` answer (largest result today
  is about 240 KB).
- A same-user client that never reads its reply can hold a pipe slot (the bridge always reads).
- HTTP host: per-request transport created before its `try`, `RunAsync` task unobserved, no backoff
  on repeated accept failures, a client dropping mid-body logs noise.
- The Claude Code stdio command quotes the path but does not escape `$`, backticks or `%VAR%`
  (only exotic install paths).
- The bridge computes the config path itself instead of sharing ConfigService's.

## Data tools and history

- `SlowCache` (hardware, boot) has no single-flight, and a cancelled load is never cached.
- Reading the in-memory graphs marshals to the UI thread with `Dispatcher.Invoke` and no timeout.
- `get_battery` right after startup (battery monitor not created yet) says Windows did not report
  the design capacity.
- A report is truncated before redaction (a name cut at the edge could leave a partial token; the
  other-profile rule still catches paths).
- History: `Combine` averages are not weighted by row length (matters only after a clock jump);
  partial first/last minutes are stored as 60 s; a stray `99991231.csv` makes maintenance warn.
- A ProcessSampler `Retain` racing `Dispose` can re-create the timer at shutdown.

## Redactor

- Over-redaction (safe side): a bare `::` and dotted version numbers become `[ip]`.
- Leading-zero IPv4 addresses and profiles outside `X:\Users` (UNC, `Documents and Settings`) are
  not redacted; user and computer names shorter than 3 characters are kept (documented).

## Tests and process

- Older tests (`UpdateTests`) write the real `%APPDATA%\MicaStats\logs\micastats.log`; give
  `DiagnosticsLog` a test root so no test touches the owner's log.
- Some coverage gaps listed per task in the review ledger (offline tools' exact errors, UI-thread
  no-deadlock, chunked 413 and notification 202 on the HTTP host, the real clipboard default).
