# StackChan companion

Implement a local, opt-in MicaStats companion for M5Stack StackChan factory DANCE firmware over Windows BLE GATT. Also support an optional custom UIFlow 2.0 companion via Nordic UART GATT with newline-framed JSON. Verified protocols are pinned in `docs/research/stackchan-hardware-support.md` and `firmware/uiflow2/README.md`. USB supplies power/debug and is not a verified factory control interface.

## Acceptance
- Dedicated bilingual Settings page: bounded BLE discovery, selected device connection, disconnect, capability/status/error feedback, remembered device and opt-in reconnect.
- Manual facial presets and feature editing, independent left/right RGB colors and transitions, conservative positional head controls and cancellable short gestures. No unsupported Home/Stop/calibration/torque commands.
- Companion reactions to current CPU/RAM/GPU load, diagnostic alerts, AI request activity and meeting activity; no private text sent. Quiet mode dims RGB and prevents all new head commands. Automatic head motion is off by default.
- Serialized, bounded writes; portable 20-byte framed factory protocol; UTF-8 JSON limited to 2048 bytes. No stale queued actions after disconnect, cancellation or settings changes. Background polling cannot block telemetry/UI.
- Settings persist through the existing configuration service. Native Windows SDK projection, no added application dependency. BLE service unavailable/incompatible firmware has actionable recovery.
- Tests prove framing/ranges, service cancellation and reactions, config persistence and localized UI. Run serial BelowNormal tests using test.ps1; stage Release under artifacts.
- Clearly state factory limitations: DANCE must remain open; no arbitrary dashboard text or audio via this verified profile. Do not flash firmware.
- Optional UIFlow 2.0 profile is discovered only by exact custom companion name, then NUS capabilities are probed. Stream safe numeric CPU/RAM/GPU and predefined activity to its live screen; include script and setup/calibration instructions. Do not imply generic UIFlow firmware alone implements this companion protocol. Microphone/camera/audio streaming are outside these implemented profiles.

## Design
Windows GATT transport implements IStackChanTransport; pure protocol encoder validates and frames commands. App-owned StackChanService owns connection sessions, serialized commands, reactions and lifecycle. Settings hosts a theme-aware panel. Activity scopes aggregate AI requests and meeting sessions without coupling BLE to application content.

## Verification boundary
Automated fake-transport and rendered UI checks prove app behavior. Real BLE discovery/control is a separate hardware check and must be reported honestly if the robot is not advertising.
