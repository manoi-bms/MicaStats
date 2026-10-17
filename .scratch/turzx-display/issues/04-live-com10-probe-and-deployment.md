# 04: Live COM10 Hardware Probe, Full Suite Verification & Release Deployment

- **Status**: `complete`
- **Blocked by**: `03-service-config-and-settings-ui.md`

## Acceptance Criteria

1. Live hardware verification against `COM10` (`USB\VID_1A86&PID_5722\USB35INCHIPSV2`) discovers the port, connects, sets orientation and brightness, and renders a live telemetry dashboard frame onto the physical 3.5" IPS screen without error.
2. Full test suite passes via `.\test.ps1` at `BelowNormal` priority.
3. Staged Release build under `artifacts/turzx-display/Release` is deployed to `bin/Release/net8.0-windows/` and `MicaStats.exe` is restarted and verified running.

## Verification Evidence
- `.\test.ps1 -Filter "Turzx|LocalizationCoverageTests"` -> `Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46, Duration: 2 s` (including `Live_hardware_probe_discovers_turzx_and_renders_live_dashboard_when_connected` on `COM10`).
- `.\test.ps1 -NoBuild` -> `Passed! - Failed: 0, Passed: 6526, Skipped: 0, Total: 6526, Duration: 3 m 29 s`.
- Staged Release build (`artifacts/turzx-display/Release`, `0 Warning(s), 0 Error(s)`) deployed to `bin/Release/net8.0-windows/` and `MicaStats.exe` restarted with `TurzxEnabled=true`, `TurzxAutoConnect=true`, `TurzxPortName=COM10` (PID `58584`).

## Comments
- Phase 4 complete.
