# MicaPad task tracking

Status: complete

The initial inline-date implementation described below was superseded by the user's later request
for separate protected metadata. Current behavior is specified in `../micapad-task-metadata/spec.md`.

Support `- []`, `- [ ]`, `- [x]`, and `- [X]` Markdown tasks. The user chose readable dates appended directly to each line, and requested a flow that stays productive without interruptions.

Dates use local Gregorian `yyyy-MM-dd HH:mm`: `(created: DATE)` and `(created: DATE; finished: DATE)`. Keep creation through edits and moves. Completion records its first time; reopening removes completion, and completing again records a new time. Existing undated tasks get the time tracking first sees them; historical creation/completion cannot be inferred. Dates remain ordinary text in encrypted autosave, history, copy and Markdown exports.

Mute the suffix using the existing palette. Ctrl+Enter completes/reopens the current task, also available in the context menu. Enter continues a task with a fresh checkbox and independent creation date; Enter on an empty task exits the list. End and Shift+End stop before dates. Keep the caret before appended dates and combine automatic changes with the originating undo step. Do not rewrite during undo/redo, add dialogs, new dependencies or network calls. Code fences, math blocks, front matter, tables and non-Markdown notes are excluded. When a tracked task becomes prose or a code example, remove only its managed date suffix; preserve untouched lookalike text.

Implement a pure Markdown task formatter, a document controller using the existing incremental Markdown cache, and bounded editor key/menu integration. Validate pure dates, locale, undo/redo, caret/Enter/menu behavior, exclusion and save/reopen; then full tests and staged Release serially at BelowNormal priority.

Verification: final `./test.ps1 -NoBuild` passed all 6,091 tests with no failures or skips. Targeted task, WPF window, formatter, menu and bookmark tests passed before the full run. The final staged Release build under `artifacts/micapad-tasks/Release` succeeded with zero warnings or errors. Builds and tests ran serially at BelowNormal priority. Independent review found no remaining material issues; `git diff --check` passed.
