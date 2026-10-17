# StackChan living display

Status: complete
Date: 2026-10-14

The owner finds the working USB companion visually static and wants a lively character. Live inspection confirms USB telemetry is active and Quiet mode is off. The previous five-second glances and occasional blinks are too subtle.

Make the character the default screen, with larger expressive eyes, smooth gaze and eyelids, a gentle bob, and distinct illustrative activity animations. Keep real CPU/RAM/GPU readings visible beneath the face. Spend 12 seconds on the character and 6 seconds each on overview and metric spotlight; show the view's dwell progress. Real abnormal events immediately interrupt with readable values and thresholds. Preserve existing manual avatar commands, truthful unknown/stale data, bounded rendering/USB polling, and Quiet mode's still display. Screen effects never issue motor commands or alter saved preferences.

No new dependencies, touch behavior, firmware flashing, device-file writes, calibration changes, or physical motor testing. Use the existing RAM companion loader. Completion requires deterministic animation/bounds/privacy regressions, a rendered animated preview, staged build, and physical USB animation/alert verification followed by reloading the production companion.

During validation the owner reported visible flicker. Render each face into a reusable off-screen RGB565 buffer and transfer it only after completion; never erase the face on the LCD between animation frames. Keep the standard face render below the 80ms animation interval, preserve manual rotations, and verify the raw transfer uses the pixel count and correct byte order.

## Evidence

Evidence: `artifacts/stackchan-dynamic-ui/validation.md`, animated previews, and the passing physical USB probe. The probe observed all three views, 107 distinct poses and a 36ms maximum face frame. Firmware tests (67), targeted host/localization tests (135), Ruff and the staged Release passed. The exact production script was hash-verified, loaded into StackChan RAM and connected to the installed app; live telemetry was acknowledged. Quiet mode remains Off and the owner's AutomaticMotion preference remains On. The probes sent no head commands. RAM loading remains temporary until the robot restarts; visual panel scan tearing and physical motor alignment were not measured.
