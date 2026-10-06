# Stable note colors

Status: complete

Give MicaPad notes stable individual tab colors to aid visual recognition. Automatically spread new or previously uncolored notes across available hues, keeping saved colors through rename, reorder, window moves, close/reopen and restart. Persist only appearance metadata in the encrypted note store; do not change note text, file dirty state or history.

Use restrained theme-aware backgrounds, small color markers and a matching active underline. Keep titles neutral and readable, and retain semibold active titles, hover/focus states, close controls and keyboard navigation. Repeat the color marker in the Open notes picker. Right-click a tab to choose a named color with a visible swatch and check mark. Fit normal and minimum widths in Dark and Light, with at least 4.5:1 title contrast and 3:1 marker contrast. Color assists recognition; titles and current-state words remain authoritative.

No dependencies or new dialog. Follow root DESIGN.md and the owner's modern, informative, user-friendly UI preference. Verify meaningful color stability/persistence, source files and pending text saves, menu actions, theme/layout fixtures and existing navigation. Run checks serially at BelowNormal, stage Release under artifacts and deploy with existing authorization through normal Quit.

Verified 2026-10-06: 133 targeted checks and all 6,184 repository tests passed. Four synthetic Dark/Light fixtures were inspected at 900×640 and 420×260. Independent review approved. Staged Release built without warnings/errors; normal Quit preserved pending saves, all 60 installed file hashes matched, and the live editor/Notes picker smoke passed. See `issues/01-note-colors.md` for evidence paths.
