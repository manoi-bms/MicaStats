# Phase 4: Localization, UI Consistency & Shared Component Consolidation

Status: complete
Blocked by: 03-phase3-ai-meeting-capture-stackchan.md

## Acceptance criteria

- Remaining hardcoded English strings in `StatsPanelViewModel`, `TaskManagerViewModel`, `DiagnosticsWindow`, `CaptureEditorWindow`, `RegionSelectorWindow`, `AskWindow`, and `Conference/*` use `UiText` with English and Thai catalog entries.
- Unstyled ModernWpf buttons in `SearchPane`, `HistoryPane`, `AiPane`, and `FindReplaceBar` use consistent Pad button styles, and `MicaPadWindow` status bar separates left metrics from right actions without vertical editor jumping.
- Duplicated snapshot scrubbing retry queues in `MicaPadWindow.Vault.cs` and `PadWorkspace.cs` are consolidated, and duplicated PNG header/LRU bitmap decode helpers between `Ai/ChatDiagrams.cs` and `Pad/DiagramPicture.cs` are unified.
- Full test suite (`test.ps1`) and staged Release build under `artifacts/` pass with zero errors.
