# 03: TurzxDisplayService, AppConfig & Settings UI Panel

- **Status**: `complete`
- **Blocked by**: `02-renderer-and-dirty-rects.md`

## Acceptance Criteria

1. `AppConfig` persists `TurzxEnabled`, `TurzxAutoConnect`, `TurzxPortName`, `TurzxDeviceName`, `TurzxOrientation`, `TurzxBrightness`, and `TurzxViewMode` with safe clamping/normalization and factory-reset defaults.
2. `TurzxDisplayService` manages discovery, connection, orientation/brightness sync, incremental dirty-rect frame pushing from `ObserveMetrics` and `NotifyAlert`, and sends `SCREEN_OFF (108)` on disconnect/dispose.
3. `App.Turzx.cs` wires `TurzxDisplayService` into `App.xaml.cs` alongside `StartStackChan`/`StopStackChan`.
4. `Companion/TurzxPanel.xaml` and `Companion/TurzxPanel.xaml.cs` provide connection controls, orientation, view mode, brightness slider, test pattern button, and live preview in `SettingsWindow`, with complete Thai translations in `Services/Localization/Catalogs/turzx.json`.
5. Unit tests in `TurzxServiceTests`, `TurzxPanelTests`, and `LocalizationCoverageTests` pass via `.\test.ps1`.

## Verification Evidence
- `.\test.ps1 -Filter "TurzxServiceTests|TurzxPanelTests|LocalizationCoverageTests"` -> `Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.

## Comments
- Phase 3 complete.
