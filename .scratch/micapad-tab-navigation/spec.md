# MicaPad open-note navigation

Status: complete

The user asked for research and a friendlier tab design when many notes are open. Research is recorded in `docs/research/micapad-open-note-navigation.md`; decisions live in root `DESIGN.md`.

Retain readable horizontal document tabs and existing drag/close/keyboard behavior. Show full title/path tooltips, emphasize the active note with shape/text as well as color, and show close buttons on the active tab or hover without changing tab width. Keep New note outside the scroll area and support mouse-wheel tab scrolling.

Add an always-visible Open notes button with this window's count and Ctrl+P. Open a non-modal, theme-aware searchable vertical picker with descriptive titles, file paths or scratch-note identity, current/other-window markers and an empty state. Filter by title/path, not note content. Search starts focused; arrows move selection; Enter or one click switches through the existing ShowNote path; Escape cancels. Highlighting/filtering must not switch or edit the note. Collection/title/window changes must refresh the open picker, and closing it must release note references. Close it on vault entry, hide and application exit.

No dependencies, schema changes, network calls, or default permanent sidebar. Preserve existing Ctrl+Tab order and numeric shortcuts. Verify at 900x640 and 420x260 in dark/light themes with synthetic notes, then serial BelowNormal targeted tests, full tests via test.ps1, and a staged Release build.

Implemented and deployed to the owner's existing Release application. Verification evidence and the local backup path are recorded in `issues/01-open-note-picker.md`.
