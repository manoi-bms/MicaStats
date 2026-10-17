# Phase 1: Performance, Resource Hygiene & Core Monitoring UX/Bug Fixes

Status: complete
Blocked by: none

## Acceptance criteria

- `TelemetryService` caches `DriveInfo` capacity readings (30s TTL) instead of querying `DriveInfo` every 1s, and applies dead-source backoff in `GetGpuTemperature` so unsupported GPUs do not spawn `nvidia-smi` or WMI queries every 2s.
- `OverlayWindow` reuses preallocated `RectangleF[]` buffers in stacked sparkline renderers and disposes stacked GDI+ brushes and hover timers in `Dispose()`.
- `HistoryBarGraph` and `RingGauge` cache frozen `Pen` / `LinearGradientBrush` instances instead of allocating per render frame, and `HistoryBarGraph` displays a value + relative-time tooltip on mouse hover.
- `MicaPadWindow.OnOccurrenceDocumentChanged` does not start `_occurrenceTimer` on every keystroke when `Editor.SelectionLength == 0` and `_occurrences.Count == 0`.
- `StackChanPanel.Detach()` is invoked on `Unloaded` / `SettingsWindow` close so singleton event handlers do not leak `StackChanPanel` instances.
- `MeetingIntelligence.DisposeAsync()` disposes `_manualCancellation` and `_analyzerGate`.
- `TaskManagerViewModel.RefreshView` matches `ParentName` in addition to `Name` and `Pid`; `TaskManagerWindow` supports `Delete` (End task), `Escape` (clear filter / close), `Ctrl+F` (focus filter), row `ContextMenu`, and async `WaitForExitAsync` in `OnRetryElevated`.
- `DiagnosticsWindow` allows editing alert rule `Threshold` and `SustainSeconds` in the UI and persists them to `Config.AlertRules`.
- `SettingsWindow` syncs `StartupService.SetStartup` immediately when `Config.LaunchOnStartup` changes.

## Comments

- Started Phase 1 implementation.
- Completed Phase 1 and verified with `.\test.ps1 -Filter "TaskManagerTests|SparklineRenderTests|AlertRuleSettingsTests|StackChanPanelTests|LocalizationCoverageTests"` (69 passed, 0 failed).
