# Screen validation and local deployment
Status: complete
Blocked by: 02-renderer (complete)

Acceptance: serial BelowNormal firmware checks, applicable .NET checks and staged
Release; normal Quit/backup/hash-verified local deployment; physical USB startup,
hello and face/RGB/dashboard acknowledgements. No head movement, reset, flash,
calibration or persistent device writes. State physical screenshot limitations.

Evidence: 40 firmware tests and 111 targeted StackChan/localization .NET tests
passed, serialized at BelowNormal; .NET used test.ps1. Staged Release has zero
warnings/errors. Independent code review approved, including isolated-history
points and complete Neutral payload regressions. Normal Quit, backups and 64
matching deployment hashes passed. Installed app PID51988 loaded the bundled
script into RAM on COM9; the actual 320x240 and Montserrat12/18/24 checks passed
before USB-ready and correlated hello. Live app face/RGB and automatic dashboard
acknowledgements passed. Automatic motion remained off; no head commands,
flash/reset/calibration/device-file writes. Synthetic previews approximate the
built-in font rasterization; no physical camera/framebuffer capture was taken.
Evidence: artifacts/stackchan-screen/validation.md, ui-smoke.json,
telemetry-smoke.json, deployment.json and previews/states.png.
