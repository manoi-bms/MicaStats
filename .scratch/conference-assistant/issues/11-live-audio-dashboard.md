# 11 — Live audio dashboard and expressive meeting UI

Status: complete
Blocked by: none (existing meeting workflow complete)

## Request and scope

Refresh Meeting Assistant with restrained emoji, clearer information hierarchy and real graphical audio waveforms for microphone and selected conference output. Preserve shared capture, ASR/TTS lifecycle, configured endpoints and manual export. Design contract: `DESIGN.md`, particularly Components, Visual language and Responsive behavior.

## Acceptance criteria

- Two labeled three-second amplitude displays reflect actual existing capture buffers, including per-channel peaks, with bounded memory and UI updates.
- Show selected devices, peak level, quiet/no recent audio, near-clipping and paused/stopped/faulted states without implying speech recognition or successful ASR.
- No extra capture clients, endpoint changes, network requests or audio persistence; late old capture data cannot revive a stopped/resumed display.
- Emoji supplement readable labels. Stop stays reachable. Normal/minimum, light/dark, unconfigured and paused states remain usable and have synthetic rendered evidence.
- Targeted deterministic PCM/monitor/lifecycle/UI tests, full suite, staged Release and privacy/diff checks pass; no live hardware or private service use.

## Verification

- Focused capture/monitor/window checks: 30 passed before the transcript follow-up; six deterministic monitor cases cover expiry, callback duration, reset, independent channels, clipping and invalid floats. Window checks cover pause, fault, stop and hidden/closed polling.
- Final integrated full suite: 6,032 passed, zero failed/skipped, 3 m 18 s. Final UI checks after a compact-layout adjustment: 20 passed. Builds/tests ran serially at BelowNormal priority.
- Staged Release: zero warnings/errors. Four existing xUnit1031 warnings remain in unrelated diagram/Markdown test files.
- Synthetic dark/light screenshots inspected at 1180×820 and 780×600, including listening, paused, unconfigured, dual ASR, summary and speech. All 862 publication candidates passed the private-endpoint scan; staged diff check passed.
- No live audio or service calls. Hardware/driver waveform behavior and conference coexistence still require a controlled live check; this change reuses the existing shared capture buffers and introduces no new audio clients.

## Comments

User explicitly requested a more informative, professional and modern interface with emoji and graphical sound waves. The waveforms are short amplitude histories from the same audio consumed by ASR; they do not identify who is speaking or measure recognition quality.
