# Phase 2: MicaPad UX & Editor Ergonomics

Status: complete
Blocked by: none

## Acceptance criteria

- `MicaPadWindow` calculates `TabHeaderWidth` proportionally (`ViewportWidth / Math.Max(1, _tabs.Count)` clamped to `[100, 180]`) so tabs shrink before scrolling.
- Moving a note tab to another window preserves its `UndoStack` instead of clearing undo history (Note: preserved AvalonEdit `LetGoOf` contract because `CaretPositionRestore` holds a reference to the originating `TextArea`).
- `ClosedPopup` supports keyboard navigation (`Down` from search to list, `Enter` to reopen, double-click on row to reopen).
- `EditorMenus` and `MicaPadWindow.HandleShortcut` support `Ctrl+B` (Bold), `Ctrl+I` (Italic), `Ctrl+K` (Link), and `Ctrl+Shift+K` (Code block) in Markdown notes.
- Opening a utility pane (Search, AI, History) while `PadMarkdownSplitPreview` is enabled preserves the existing `MarkdownSplitViewTests` contract.
- `FindReplaceBar` includes an expand/collapse toggle button (`ToggleReplaceButton`) for Replace; `VaultCard` preserves question cards across focus changes per `VaultCardTests`.

## Comments

- Started Phase 2 implementation.
- Verified Phase 2 tests via `.\test.ps1` (102 passed, 0 failed).
