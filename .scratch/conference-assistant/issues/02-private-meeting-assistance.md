# 02: Keep a running summary and prepare private answers

Status: complete (automated acceptance; manual hardware check remains)

**What to build:** While a meeting is transcribed, display a running summary, key points, detected questions and suggested answers. Let the user ask a question directly and copy the answer. Use the existing configured AI provider, with bounded context and explicit support from the meeting transcript.

**Blocked by:** 01 — live transcription.

- [x] New finalized text triggers coalesced analysis only during an active session, with one automatic request in flight and bounded model input/output.
- [x] Missing or failed AI configuration shows an actionable status while transcription remains usable.
- [x] Each automatic analysis/manual question consumes the existing shared daily allowance before dispatch. At exhaustion, analysis pauses visibly while transcription continues; the global limit is not raised automatically.
- [x] The UI exposes summary, key points, questions and private suggested answers, plus a direct question composer and Copy.
- [x] Answers cite valid transcript segments when supported and explain missing information when they are not.
- [x] The bounded JSON analysis/answer contract is validated, including every cited source ID against the actual request context. Invalid output cannot silently become a claimed supported answer.
- [x] Transcript content cannot authorize tool calls, automatic replies or external navigation.
- [x] Use the configured plain chat client without the tool-enabled Ask assistant or invocation middleware.
- [x] Stop, close, restart and settings changes cancel obsolete work and prevent stale analysis from replacing current results.
- [x] Manual export can include the current analysis and suggestions, while automatic persistence remains disabled.
- [x] Behavioral tests cover coalescing, long sessions, a question arriving during analysis, failures, cancellation and adversarial transcript content.
- [x] User documentation explains near-real-time batching, private suggestions and the limits of answer grounding.

## Verification

Record focused tests, compile result and the separate Standards/Spec review here during implementation.


2026-10-05: analyzer 18/18, intelligence scheduling 6/6 and WPF workflow 1/1 passed; app and test projects compiled. Integrated meeting run passed all except an escaping assertion subsequently corrected to inspect decoded JSON. Final combined verification follows tickets 03 and 04.

2026-10-05 final integration: all 5,917 tests passed (zero failures/skips), followed by 7/7 capture tests after removing a test-only scheduling race. The staged Release build succeeded with zero warnings/errors. Independent Standards and Spec reviews closed all findings; see ../implementation-review.md. Actual hardware capture/playback and recognition quality remain explicit manual checks in docs/meeting-assistant.md.
