# Markdown source and live preview

Status: complete
Blocked by: none

Owner: root; delegate only bounded renderer/test/review slices with explicit file ownership.

## Acceptance criteria

- Implement the source/preview behavior and professional, accessible controls specified in ../spec.md.
- Preserve existing note, task, history, search, AI and vault behavior, and retain separate width/view preferences.
- Document the action, shortcut and rendering limits in GUIDE.md and the resolved terminology in CONTEXT.md.
- Complete targeted tests, full regression tests, staged Release build, screenshot inspection and local deployment smoke checks.

## Verification evidence

- 2026-10-07: 174 focused preview/language/pane/AI-answer/vault tests passed after the lifecycle fixes.
- Final full suite via `test.ps1`: 6,226 passed, 0 failed, 0 skipped. Existing test analyzer warnings are unchanged.
- Staged Release under `artifacts/micapad-markdown-split/Release`: succeeded with 0 warnings and 0 errors.
- Inspected synthetic light/dark screenshots at 900x640 and 420x260. Captions, source, selectable preview, divider and footer actions fit; preview scrolls independently.
- Independent code/spec review found no remaining blocking issues. Repeated render errors and successful credential storage have regression coverage; source and action controls have accessible names and meaningful icons.
- Local deployment completed through normal Quit; all 60 staged/deployed file hashes match. Previous files are preserved in `artifacts/micapad-markdown-split/deploy-backup-20261007-092822`.
- Live application PID 96908 passed editor/Notes picker, split-preview source/read-only body/Refresh/Close and Settings Quit/Save smoke checks. The smoke check restored the previous split-view preference without editing note content.
- Logs and screenshots: `artifacts/micapad-markdown-split/`. Deployment helper was narrowed to owned native app windows after a desktop-wide UI Automation timeout; application code and staged files were unaffected by that failed first attempt.

## Changed files

- `Pad/MicaPadWindow.xaml`, `.xaml.cs`, `.MarkdownPreview.cs`, `.Diagrams.cs`, `.Vault.cs`: two-pane layout, actions, divider, remembered view and lifecycle integration.
- `Pad/MarkdownPreviewPane.xaml`, `.xaml.cs`, `MarkdownPreviewDocument.cs`, `PadAnswerBox.cs`: bounded read-only rendering, status/empty/fallback feedback and local diagram reuse.
- `Pad/EditorLanguage.cs`, `Models/SystemMetrics.cs`: literal source presentation and independent persisted preferences.
- `tests/Kil0bitSystemMonitor.Tests/MarkdownPreviewPaneTests.cs`, `MarkdownSplitViewTests.cs`, `MarkdownSplitLayoutTests.cs`: render, lifecycle, source/task invariants and layout coverage.
- `DESIGN.md`, `GUIDE.md`, `CONTEXT.md`, this issue and its specification: interaction design, user instructions, terminology and evidence.

## Comments

Repository lookup found a reusable rendered-answer surface, while history's PreviewPanel remains a separate workflow. Final rendering capabilities will be recorded after the implementation review.

The source-only view preserves documents, caret, selection, undo and encrypted task metadata.
Rendering boundaries are documented in the spec and GUIDE.md: ordinary images, HTML, math and
extended syntax use readable text fallbacks; long previews are bounded with an explicit notice.
