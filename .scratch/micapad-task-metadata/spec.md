# Read-only task dates outside Markdown

Status: complete

Historical specification: `.scratch/micapad-task-date-editing/spec.md` supersedes the prohibition on an explicit date editor. Separate encrypted storage and read-only Markdown annotations remain current behavior.

The user's latest instruction supersedes inline editable dates: store task creation/completion timestamps separately from note text, and do not provide an editor for those values.

Use the existing encrypted per-note `meta.json`, physically separate from `current.txt`, for immutable task records. Keep Markdown, clipboard, export, external source files, search and AI note content free of generated timestamp text. Display the dates beside tasks through AvalonEdit's visual layer only. Reuse Pad theme colors and preserve normal typing, selection, End, Enter continuation and Ctrl+Enter completion.

Maintain a stable generated task ID during edits with document anchors; persist matching text/offsets for reopening. Creation is fixed, completing records the current time, reopening clears completion, and undo/redo restores the corresponding metadata without resampling the clock. Duplicate tasks have separate IDs. Exclude code/math/front matter/tables, non-Markdown notes and oversized notes.

Existing stored notes with old generated suffixes migrate once: import valid dates, remove the managed suffix from editable text and save both through existing encrypted autosave. Before persisting the version-0 to version-1 transition, remove former generated suffixes from eligible task lines in encrypted History snapshots too, preserving names, stamps, structure and newlines. Retry failed migration through the existing writer; do not persist its completion on failure. New notes/imports and already migrated notes never treat date-like user text as metadata. History remains plain Markdown; do not show current annotations in historical previews.

Verify migration, immutable metadata, editing/undo/restart, duplicates/line shifts, exclusions, file source preservation, encrypted storage and read-only rendering. Run tests/builds serially at BelowNormal priority via test.ps1, stage Release under artifacts and deploy through normal Quit using existing user authorization.

Verified and locally deployed on 2026-10-06. Evidence: [implementation ticket](issues/01-read-only-task-dates.md).
