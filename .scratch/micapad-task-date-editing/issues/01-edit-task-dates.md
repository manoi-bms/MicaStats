# Task date editing popup

Status: complete
Blocked by: none

Acceptance criteria:
- Right-click a real Markdown task to edit Start/Finish in a themed popup.
- Validate Gregorian date/time and chronology, preserve untouched precision, and offer Now/Clear.
- Dates remain separate from Markdown; checkbox and Finish stay consistent.
- Save, cancel, metadata-only autosave and one-step undo/redo work across restart/document transfer.
- Drafts close safely on note changes/hide/vault/exit, and stale targets cannot be overwritten.
- Targeted/full tests, review, staged Release, local deployment and a bounded feature shortlist are complete.

Verification evidence:
- Targeted controller/form/window/persistence/rendering/history checks: 75 passed, including the final icon/layout revision.
- Full repository suite: 6,161 passed. Tests/builds ran serially at BelowNormal through test.ps1 and the local .NET SDK.
- Independent code review: approved with no remaining material findings. A discovered Enter-on-Cancel/Now/Clear interception was repaired and covered with a routed-key regression test.
- Four synthetic popup fixtures at 900x640 and 420x260 in Dark/Light passed; inspected screenshots under `artifacts/micapad-task-date-editing/screenshots`. Both fields fit the minimum size without scrolling and Save/Cancel stay visible.
- Staged Release: `artifacts/micapad-task-date-editing/Release`; zero warnings/errors. XML parsing and git diff --check passed.
- Local deployment used normal Quit for PID 64352, preserved a backup at `artifacts/micapad-task-date-editing/deploy-backup-20261006-100144`, and verified SHA-256 equality for all 60 staged/deployed files.
- Installed app restarted as PID 94936; live UI Automation verified MicaPad's enabled editor, opening/closing the Notes picker, and a running process without modifying note text.
- Official-source productivity shortlist: `docs/research/micapad-todo-productivity.md`; durable UI preferences saved in shared project memory and root DESIGN.md.

## Comments

Root owns controller/metadata undo/window wiring/docs/integration; bounded agents own the form, independent tests and official feature research. The current user instruction overrides the earlier no-date-editing preference while preserving separate storage.
