# 01: Start a meeting and follow its live transcript

> Historical proposal record. The user approved continuation on 2026-10-05; the canonical specification and issue files track implementation.

Status: draft for review

**What to build:** Open Meeting Assistant, choose the microphone/output and ASR service, start a visibly active session, follow source-labeled transcription, stop, and explicitly save the transcript. ASR2 is preferred; ASR1 is selectable before Start. See the parent draft specification and evidence for the provider contract and limits.

**Blocked by:** None after setup, specification, test seams and dependency choice are settled.

- [ ] App menu opens a themed Meeting Assistant window with device/service selectors and clear Start/Stop states.
- [ ] No audio capture, ASR calls or recording persistence occurs on app startup or merely opening the window.
- [ ] Starting captures the chosen microphone and output as separate sources and uses bounded in-memory audio chunks.
- [ ] ASR2 uses its verified multipart WAV contract; ASR1 uses its compatible endpoint and documented defaults.
- [ ] Final transcript segments show approximate session times and source labels; silence and model control markers are absent.
- [ ] ASR replies require string `text`; only ASR2's recognized leading Qwen envelope is stripped. Invalid/oversized replies produce sanitized failures without logging response bodies.
- [ ] Stop, close, failure and restart handle pending requests and device resources; late results cannot enter a newer session.
- [ ] Device removal, malformed replies, timeouts and backlog limits have visible, recoverable outcomes.
- [ ] Enforce four-second chunks, one in-flight request plus eight queued chunks per source, a thirty-second deadline, and the eight-hour/two-million-character session cap. Test silent output resuming with correct session times.
- [ ] Manual Markdown save preserves received transcript text; raw audio, transcript content and secrets never enter automatic persistence or diagnostics.
- [ ] Session and provider behavior tests pass with scripted sources/HTTP; no live microphone or provider appears in automated tests.
- [ ] Manual verification instructions distinguish known contract evidence from pending hardware/recognition checks.

## Verification

Record focused tests, compile result and the separate Standards/Spec review here during implementation. Hardware checks are not represented as automated coverage.
