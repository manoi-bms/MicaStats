# 02: Turzx RGB565 Cyber-Instrument Renderer & Dirty-Region Diffing

- **Status**: `complete`
- **Blocked by**: `01-protocol-and-usb-transport.md`

## Acceptance Criteria

1. `TurzxRenderer` renders all 4 view modes (`Dashboard`, `Thermal`, `StorageNet`, `Clock`) in both `Landscape` (`480×320`) and `Portrait` (`320×480`) into a `ushort[]` RGB565 framebuffer with zero GDI+/WPF thread affinity.
2. Active `AlertEvent` notifications render a high-contrast alert banner overlay at the top of the frame.
3. `ComputeDirtyRegions` returns a single full-screen `TurzxRect` when the previous frame is null or mismatched, `0` rects when frames are identical, and compact dirty rectangles when only dynamic text/sparklines change.
4. `ToBgra32` converts an RGB565 buffer into BGRA32 pixel bytes for the Settings preview image.
5. Unit tests in `TurzxRendererTests` pass via `.\test.ps1`.

## Verification Evidence
- `.\test.ps1 -Filter "TurzxRendererTests"` -> `Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 38 ms`.

## Comments
- Completed with TDD.
