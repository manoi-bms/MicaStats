# 04: Speak a chosen answer or typed text with VoxCPM

Status: complete (automated acceptance; manual hardware check remains)

**What to build:** Add speech output to the Meeting Assistant using the user-supplied VoxCPM service. The user can choose a suggested answer or enter text, generate speech explicitly, select a playback device, and stop synthesis/playback. Standalone typed-text synthesis also works while the meeting is stopped.

**Blocked by:** 02 — private meeting assistance. Reference-note support in 03 and speech output can be developed independently after 02; their integration must verify credential/context invalidation before feature completion.

- [x] Use the documented and tested service contract at `<user-configured service address>`; advertise only documented/tested language, voice and audio-format capabilities.
- [x] POST JSON to `/v1/audio/speech` with model `voxcpm-thai`, chosen voice (default `default`), WAV response and streaming disabled; validate returned PCM WAV before playback. Voice refresh is explicit; no ignored speed or unverified streaming controls are shown.
- [x] A user action sends only the chosen speech text to TTS; ASR updates or newly generated answers never start speaking automatically.
- [x] The UI offers editable text, Speak, Stop speaking, playback-device selection and visible generation/playback/error state.
- [x] Playback is local to the selected device; virtual-microphone injection and automatic conference replies remain outside scope.
- [x] Generated audio is bounded and stays in memory during normal use. No audio, speech text or raw provider errors enter automatic persistence or diagnostic logs.
- [x] Only one synthesis/playback operation is active. Stop, window close, session reset, device loss and context invalidation cancel obsolete work and reject late responses.
- [x] Meeting Start stays disabled during standalone speech generation/playback. Stop speaking completes before capture may start.
- [x] Confirmed feedback policy: pause both ASR sources immediately before playback, await their stop barriers, preserve eligible pre-pause speech, mark the resulting listening gap, and resume after playback/settling only if the same meeting is still active. Synthesis alone does not pause capture.
- [x] Enforce 4,096 input characters, a sixty-second deadline, 10 MiB body and ninety-second decoded audio caps, with no automatic retry or silent truncation.
- [x] Stop speaking cannot restart a stopped meeting. Playback failure restores an eligible active meeting's listening state or displays a capture fault.
- [x] A failed pause/resume of either source releases both and faults the meeting; playback never starts after a partial pause. Gap timing covers first source stopped through both resumed, including cancelled playback and failures.
- [x] Recheck speech/meeting identities after every asynchronous transition. Speech from an answer retains its source IDs for note invalidation; standalone typed text has no note dependencies, while global credential storage cancels all speech conservatively.
- [x] A removed reference or stored credential cancels speech based on invalidated suggestions and clears retained speech/audio state; existing Ask and meeting credential protection remain intact.
- [x] Automated tests use fake TTS HTTP/audio outputs, an injected clock/delay and scripted capture barriers to validate payload, audio decoding, size/time limits, stop/start races, partial pause failure, device selection and capture/playback interaction without touching live devices.
- [x] A separately recorded synthetic-text service test proves transport/audio response compatibility; intelligibility, languages, latency and hardware output claims remain limited to actual evidence.
- [x] English/Thai documentation explains the full microphone/output → ASR → analysis → user-triggered TTS workflow and any listening gap.

## Verification

Record provider-contract evidence, focused tests, compile result, and separate Standards/Spec reviews during implementation. Run final combined verification after 01–04 and their note/TTS invalidation interaction are complete.

2026-10-05 final integration: all 5,917 tests passed (zero failures/skips), followed by 7/7 capture tests after removing a test-only scheduling race. The staged Release build succeeded with zero warnings/errors. Independent Standards and Spec reviews closed all findings; see ../implementation-review.md. Actual hardware capture/playback and recognition quality remain explicit manual checks in docs/meeting-assistant.md.
