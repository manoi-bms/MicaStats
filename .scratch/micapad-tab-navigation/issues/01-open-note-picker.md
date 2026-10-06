# Make many open notes easy to find

Status: complete
Blocked by: none

Acceptance criteria:
- Readable tabs have a clear active state and full-title/path tooltips.
- Open notes count and New note remain available during overflow.
- Ctrl+P opens a searchable title/path list with current/other-window identity.
- Keyboard and mouse confirmation switch notes; cancellation preserves the editor state.
- Rename/close/move updates and vault/close lifecycle remain safe.
- Layout fixtures at minimum/normal sizes, targeted/full tests and staged Release pass.

Verification evidence:
- Targeted navigation, picker, tab order/drag, theme, menu and vault checks passed (119 tests). Final navigation/theme/search checks passed after the final focus and test-harness corrections (30 tests).
- Fresh full suite: `./test.ps1 -NoBuild`, 6,105 passed, none failed or skipped. Runs were serial at BelowNormal priority.
- Independent review approved the navigation and the test-only search dispatcher correction; no material findings remain.
- Synthetic normal/minimum fixtures passed at 900x640 and 420x260 in both themes. Images are in `artifacts/micapad-tab-navigation/screenshots/`; dark normal and light minimum layouts were visually inspected.
- Staged Release build in `artifacts/micapad-tab-navigation/Release` succeeded with zero warnings/errors. `git diff --check` passed.
- Deployed using the application's normal Quit path, preserving its shutdown/save flow. All 60 copied files passed SHA256 comparison. Backup: `artifacts/micapad-tab-navigation/deploy-backup-20261006-082358`.
- Relaunched deployed app remained alive. Live UI Automation verified Notes opens/dismisses the picker, a nonmatching query shows the empty state, and clearing restores results, without editing note text. Live key injection was skipped when OS focus remained with another app; keyboard behavior is covered by WPF tests.

## Comments

Follow the official-source research brief and DESIGN.md. Reuse Pad palette resources, existing workspace/window routing and note objects. Keep existing shortcuts and persistence behavior; the picker uses metadata and never scans note bodies.

The first full run exposed an existing search-test classifier that equated any dispatcher-bearing thread with the editor UI thread. `SearchPaneTests.ThreadNotingEmbedder` now checks the actual window dispatcher's `CheckAccess()`. The assertion still requires one query running off that UI thread. Production search behavior did not change; the corrected targeted and full runs passed.
