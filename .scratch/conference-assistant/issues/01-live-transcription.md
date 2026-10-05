# 01: Start a meeting and follow its live transcript

Status: complete (automated acceptance; manual hardware check remains)

**What to build:** Open Meeting Assistant, choose the microphone/output and ASR service, start a visibly active session, follow source-labeled transcription, stop, and explicitly save the transcript. ASR2 is preferred; ASR1 is selectable before Start. See the approved parent specification and evidence for the provider contract and limits.

**Blocked by:** None after setup, specification, test seams and dependency choice are settled.

- [x] App menu opens a themed Meeting Assistant window with device/service selectors and clear Start/Stop states.
- [x] No audio capture, ASR calls or recording persistence occurs on app startup or merely opening the window.
- [x] Starting captures the chosen microphone and output as separate sources and uses bounded in-memory audio chunks.
- [x] ASR2 uses its verified multipart WAV contract; ASR1 uses its compatible endpoint and documented defaults.
- [x] Final transcript segments show approximate session times and source labels; silence and model control markers are absent.
- [x] ASR replies require string `text`; only ASR2's recognized leading Qwen envelope is stripped. Invalid/oversized replies produce sanitized failures without logging response bodies.
- [x] Stop, close, failure and restart handle pending requests and device resources; late results cannot enter a newer session.
- [x] Device removal, malformed replies, timeouts and backlog limits have visible, recoverable outcomes.
- [x] Enforce four-second chunks, one in-flight request plus eight queued chunks per source, a thirty-second deadline, and the eight-hour/two-million-character session cap. Test silent output resuming with correct session times.
- [x] Manual Markdown save preserves received transcript text; raw audio, transcript content and secrets never enter automatic persistence or diagnostics.
- [x] Session and provider behavior tests pass with scripted sources/HTTP; no live microphone or provider appears in automated tests.
- [x] Manual verification instructions distinguish known contract evidence from pending hardware/recognition checks.

## Verification

2026-10-05: integrated `artifacts/conference-test.ps1 -Filter Meeting` passed 29/29 (ASR 19, capture 3, session 6, configuration 1), with a fresh app/test build. No live devices were captured. Menu, startup and lifecycle wiring inspected. Final Standards/Spec review and hardware checklist follow during integration. Actual hardware recognition is not represented as automated coverage.

2026-10-05 final integration: all 5,917 tests passed (zero failures/skips), followed by 7/7 capture tests after removing a test-only scheduling race. The staged Release build succeeded with zero warnings/errors. Independent Standards and Spec reviews closed all findings; see ../implementation-review.md. Actual hardware capture/playback and recognition quality remain explicit manual checks in docs/meeting-assistant.md.
