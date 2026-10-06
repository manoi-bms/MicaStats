# Edit task dates from the context menu

Status: complete

The user's latest instruction enables explicit editing of tracked start/finish date and time from the Markdown task's right-click menu. This supersedes the previous prohibition on a timestamp editing UI; dates still live in separate encrypted metadata and remain outside the editable Markdown source.

Add **Edit task dates…** beside Complete/Reopen task. Open a compact, themed popup with Start, optional Finish, Now controls, Clear finish, Save and Cancel. Use explicit Gregorian `yyyy-MM-dd HH:mm` input and inline errors for invalid dates or a finish earlier than start. Preserve existing seconds/offset for unchanged fields; explicit Now uses the injected current time exactly. Setting Finish completes the checkbox; clearing Finish reopens it. No change occurs until Save; Escape/outside click cancels, and switching notes, hiding, vault display or exit clears the draft. Saving must reject a removed/replaced target or conflicting dates.

Keep stable task identity, automatic dates, plain copy/export, language/structure exclusions, and metadata-only saving for date corrections that do not change checkbox state. Every save is one undoable action; undo/redo refreshes annotation and autosave even when no document characters changed. Document transfer retains safe undo without retaining an old controller/window. No new dependencies.

The user also requested suggestions for productivity, usability and reliability. Research a small official-source shortlist, suggest priorities, and do not implement additional task-management features without a separate instruction.

Verify controller/date validation, metadata-only and combined undo, popup cancellation/stale targets, source files, autosave/restart and narrow/dark/light layouts. Run tests/builds serially at BelowNormal through test.ps1; stage Release under artifacts and deploy using the existing authorization and normal Quit.

The owner's UI preference is captured in root DESIGN.md: modern, professional, informative and user-friendly dialogs with meaningful icons/images. The date popup uses existing Fluent font icons with text labels on its calendar header and action buttons, and keeps Save/Cancel visible while fields scroll when needed.

Verified and deployed on 2026-10-06; see the implementation ticket for evidence. Productivity follow-ups are recorded in `docs/research/micapad-todo-productivity.md`.
