# StackChan personality and event alerts

Status: complete (implementation and USB validation; physical motor verification pending owner calibration-alignment confirmation)

The user wants an engaging USB companion: rotating useful screens, expressive LEDs and gentle motor gestures, with abnormal events taking visual priority.

Use three automatically cycling views (overview, metric spotlight, companion). Rotate about every 10 seconds while fresh telemetry is available. Alert events interrupt rotation for the existing bounded event window, display a fixed public metric name, actual value and configured threshold, then resume normally. Do not transmit custom rule labels, drive paths, AI text or meeting content. Unknown readings remain unknown. Stale telemetry stops active effects and rotation. Quiet mode keeps the screen still, LEDs dim and motors still, while retaining visible event information.

LED animations are cooperative and bounded by the configured brightness. Explicit RGB controls suspend ambient effects for the existing manual hold. Automatic head gestures remain opt-in and rate limited; verify stored calibration before physically enabling them. Do not overwrite firmware, files or calibration. Keep protocol v1 request correlation, bounded streams, manual controls and factory BLE compatibility.

Acceptance: deterministic tests for screen cycling, alert interruption/recovery, brightness/manual/stale/quiet behavior, event privacy and gesture throttling; synthetic previews; serial BelowNormal targeted tests and staged build; deploy using normal app exit and verify temporary RAM startup plus real USB acknowledgements. Report any physical motor validation gap explicitly.
