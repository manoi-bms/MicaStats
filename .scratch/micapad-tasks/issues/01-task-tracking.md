# Track Markdown todos without interrupting editing

Status: complete
Blocked by: none

Acceptance criteria:
- Both checkbox spellings record readable creation and completion dates inline.
- Creation stays stable; reopen and later completion use the expected completion date.
- Dates persist as part of note text and survive undo/redo without restamping.
- Enter, Ctrl+Enter, End/Shift+End and context menu provide a continuous editing flow.
- Code examples and other note languages remain intact.
- Targeted/full tests and a staged Release build pass at BelowNormal priority.

Verification evidence: final `./test.ps1 -NoBuild` passed 6,091 tests (zero failures or skips). Targeted task/controller/WPF editor regressions and existing formatter/menu/bookmark tests passed before the full run. Staged Release build under `artifacts/micapad-tasks/Release` passed with zero warnings or errors, serially at BelowNormal priority. Independent review found no remaining material issues. `git diff --check` passed.

## Comments

The user explicitly chose inline dates and then requested unobtrusive, creative productivity improvements. Use existing Markdown styling and incremental document structure; no separate metadata store is needed.

Task dates are muted and completed task text is struck through independently. Regular typing retains the caret before dates; formatting preserves selection. Converting a tracked task to prose or a code example cleans up only its managed suffix. Existing undated tasks use the first observed time, since historical dates cannot be recovered. No dependencies or network calls were added.
