# Living display and verification

Status: complete
Blocked by: none

## Acceptance criteria

- Default character view visibly animates smooth glances, eyelids and gentle bob without fabricating measured activity.
- Thinking, meeting and busy states have distinct illustrative accents; manual face geometry remains intact.
- Character view shows three actual readings, or `--`; charts retain only received samples.
- View dwell progress is identified by page dots, with 12/6/6-second pacing.
- Alerts remain legible and interrupt immediately; Quiet/stale data stop display motion.
- Frame work is rate limited and bounded to the body; USB and LEDs remain responsive; display never calls motors.
- Face animation uses one completed off-screen buffer transfer per frame without LCD erase/feature primitives; standard physical frames remain below 80ms.
- Fresh firmware tests, lint, targeted host tests, staged Release, preview inspection and actual USB probe pass; production script is deployed/reloaded.

## Verification

- Firmware: 67 deterministic tests pass, including single completed-buffer transfer, no physical LCD erase/feature lines, RGB565 pixel count/byte order, manual eye rotation, clock wrapping, activity/alert states and bounded animation.
- Host/localization: 135 targeted tests pass through `test.ps1`; Ruff clean; staged Release succeeds with zero warnings/errors, all run serially at BelowNormal.
- Synthetic states and the 36-second animated preview regenerated and inspected. Preview rasterization/fonts approximate device APIs.
- COM9 physical probe passes: 373 face frames, 107 poses, all three views, max face render 36ms, median/max USB ACK 121.7/242.9ms, alert interruption/recovery and Quiet still. No head commands sent by the probes.
- Installed app PID 52132: 64 staged files backed up, copied and hash-verified. Exact installed production companion loaded through the USB transport after the Settings-driven reload was cancelled; app reconnected and automatic dashboard acknowledged. Preferences remain Quiet Off / AutomaticMotion On.
- Source/staged/installed firmware SHA256 `20266524EAC0316F12779E70253FE0CB64E19D066C0D4B3A678C6DB5B1502F79` (52,928 bytes).
- Bounded independent source review approved without blocking findings. Details and limitations: `artifacts/stackchan-dynamic-ui/validation.md`.
