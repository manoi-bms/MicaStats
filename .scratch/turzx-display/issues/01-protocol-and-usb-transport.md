# 01: Turzx Protocol & USB Serial Transport

- **Status**: `complete`
- **Blocked by**: none

## Acceptance Criteria

1. `TurzxProtocol` encodes 6-byte coordinate headers (`PackCommand`), `RESET (101)`, `CLEAR (102)`, `SCREEN_OFF (108)`, `SCREEN_ON (109)`, inverted `SET_BRIGHTNESS (110)` (`0% -> 255`, `100% -> 0`), 16-byte `SET_ORIENTATION (121)` for `Portrait`/`ReversePortrait` (`320×480`) and `Landscape`/`ReverseLandscape` (`480×320`), and little-endian RGB565 `DISPLAY_BITMAP (197)` payloads.
2. `TurzxUsbTransport` discovers connected Turzx USB serial ports via `SetupAPI` (`VID_1A86&PID_5722` / `USB35INCHIPSV2` and `VID_1A86&PID_7523`), opens serial ports with `115200 8N1` and `DTR/RTS` enabled, and serializes writes with clean disconnect/dispose semantics.
3. Unit tests in `TurzxProtocolTests` and `TurzxUsbTransportTests` pass via `.\test.ps1`.

## Verification Evidence
- `.\test.ps1 -Filter "TurzxProtocolTests|TurzxUsbTransportTests"` -> `Passed! - Failed: 0, Passed: 25, Skipped: 0, Total: 25, Duration: 31 ms`.

## Comments
- Completed with TDD.
