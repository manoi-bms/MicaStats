# Separate task dates from editable note content

Status: complete
Blocked by: none

Acceptance criteria:
- Created/finished dates are stored in encrypted metadata separately from Markdown.
- Visible annotations cannot be selected, typed into, pasted over or copied as note text.
- New task date-like text cannot forge metadata; creation and controlled completion remain automatic.
- Legacy generated suffixes migrate once, preserving their valid timestamps.
- Undo/redo, line shifts, duplicate tasks, restart, file-backed notes and vault lifecycle remain consistent.
- Targeted/full tests, staged Release and local deployment pass.

Verification evidence:
- Targeted behavior/rendering/storage/history/editing regressions: 89 passed through `test.ps1`.
- Full suite: 6,136 passed, zero failed/skipped through `test.ps1 -NoBuild` at BelowNormal priority.
- Staged Release: `artifacts/micapad-task-metadata/Release`, zero warnings/errors. Builds/tests ran serially.
- Independent bounded code review approved the final implementation with no remaining material findings; reviewer did not run a duplicate build/test loop.
- Large-checklist regression: twenty edits in the middle of 2,000 tasks retain identity and trigger no structural rescan or task-record sort.
- Current-text and encrypted History migration, failed-history-read retry, new date-like text, source file preservation, duplicate copying, pending/completed duplicate moves, sorting and metadata undo/redo are covered.
- Renderer tests prove zero document length, plain selection/copy, real line-end caret/Backspace, muted styling and annotation wrapping in both themes.
- `git diff --check` clean.
- Local deployment used normal Quit of PID 53336, preserving the application's normal autosave/shutdown path. All 60 copied files have SHA-256 hashes matching the staged build. Prior binaries were backed up under `artifacts/micapad-task-metadata/deploy-backup-20261006-085958`.
- Installed app restarted as PID 64352 from `bin/Release/net8.0-windows/MicaStats.exe`; live UI Automation confirmed MicaPad's editor ready, Notes picker open/dismiss, and process still running. No real-note content was typed or printed during the smoke check.
- Four existing xUnit1031 analyzer warnings remain in unrelated DiagramRendererTests/MarkdownExtrasTests during test compilation. No new warnings in the production Release build. Owner manual checks have not been reported; automated WPF and live startup/navigation checks are recorded above.

## Comments

Keep existing task shortcuts and checklist syntax. Dates in metadata are application-managed; there is no timestamp editing UI. Root owns controller/window integration and final verification; bounded agents own persistence, pure syntax and window test slices.
