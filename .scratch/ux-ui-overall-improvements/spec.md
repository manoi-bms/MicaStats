# UX, UI & Overall Codebase Improvements

Status: complete
Date: 2026-10-14

Implement the UX, UI, performance, resource-lifecycle, localization, and architectural consolidation improvements identified in the full repository audit across Core Monitoring, Settings, MicaPad, Ask AI, Meeting Assistant, Screen Capture Editor, and StackChan Companion.

## Scope & Phases

1. **Phase 1 (`01-phase1-core-perf-ux.md`)**: Performance & resource-lifecycle fixes (`TelemetryService` drive caching & GPU temp dead-source backoff, `OverlayWindow` sparkline buffer reuse & `Dispose` cleanup, `PanelGraphs` pen/brush caching & hover readouts, `MicaPadWindow` occurrence timer gating, `StackChanPanel` detach on Settings close, `MeetingIntelligence` disposal) plus Core UX/bug fixes (`TaskManagerWindow` shortcuts/context menu/parent filtering/async elevation, `DiagnosticsWindow` editable alert thresholds/sustain, `SettingsWindow` immediate `LaunchOnStartup` registry sync & complete `ResetApp_Click`).
2. **Phase 2 (`02-phase2-micapad-ux.md`)**: MicaPad UX & editor ergonomics (proportional tab shrinking in `OnTabScrollChanged`, preserving `UndoStack` when moving tabs across windows, keyboard navigation & double-click in `ClosedPopup`, Markdown formatting keyboard shortcuts `Ctrl+B`/`Ctrl+I`/`Ctrl+K`/`Ctrl+Shift+K`, preventing `sourceOnly` editor jumps when utility panes open over Split Preview, Find/Replace incremental match scroll & Replace toggle chevron, and preventing `VaultCard` from wiping drafts on internal focus changes).
3. **Phase 3 (`03-phase3-ai-meeting-capture-stackchan.md`)**: AI, Meeting Assistant, Screen Capture & StackChan UX improvements (confirming/updating state on `EndProcess` action pills, `CopyButton` `"Copied"` feedback, confirmation on `OnNewConversation` when busy/non-empty + `Ctrl+N`, allowing post-stop private Q&A in `MeetingIntelligence`/`MeetingWindow`, auto-collapsing audio setup when listening starts, live annotation style updates & crop guard in `CaptureEditorWindow`, and non-blocking manual command dispatch + `"Center"` head reset in `StackChanPanel`).
4. **Phase 4 (`04-phase4-localization-unification.md`)**: Localization & UI/Code unification (routing remaining hardcoded English strings in `StatsPanelViewModel`, `TaskManagerViewModel`, `DiagnosticsWindow`, `CaptureEditorWindow`, `RegionSelectorWindow`, `AskWindow`, and `Conference/*` through `UiText` + catalogs; styling unstyled ModernWpf buttons in MicaPad panes; and consolidating duplicated snapshot scrubbing between `MicaPadWindow.Vault.cs` and `PadWorkspace.cs` plus duplicated PNG/LRU bitmap decoding between `Ai/ChatDiagrams.cs` and `Pad/DiagramPicture.cs`).

## Verification

All phases require red-green TDD tests where applicable, serial test execution via `test.ps1` at `BelowNormal` priority, and a clean staged Release build under `artifacts/`.
