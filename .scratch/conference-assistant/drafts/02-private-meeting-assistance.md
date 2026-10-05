# 02: Keep a running summary and prepare private answers

> Historical proposal record. The user approved continuation on 2026-10-05; the canonical specification and issue files track implementation.

Status: draft for review

**What to build:** While a meeting is transcribed, display a running summary, key points, detected questions and suggested answers. Let the user ask a question directly and copy the answer. Use the existing configured AI provider, with bounded context and explicit support from the meeting transcript.

**Blocked by:** 01 — live transcription.

- [ ] New finalized text triggers coalesced analysis only during an active session, with one automatic request in flight and bounded model input/output.
- [ ] Missing or failed AI configuration shows an actionable status while transcription remains usable.
- [ ] Each automatic analysis/manual question consumes the existing shared daily allowance before dispatch. At exhaustion, analysis pauses visibly while transcription continues; the global limit is not raised automatically.
- [ ] The UI exposes summary, key points, questions and private suggested answers, plus a direct question composer and Copy.
- [ ] Answers cite valid transcript segments when supported and explain missing information when they are not.
- [ ] The bounded JSON analysis/answer contract is validated, including every cited source ID against the actual request context. Invalid output cannot silently become a claimed supported answer.
- [ ] Transcript content cannot authorize tool calls, automatic replies or external navigation.
- [ ] Use the configured plain chat client without the tool-enabled Ask assistant or invocation middleware.
- [ ] Stop, close, restart and settings changes cancel obsolete work and prevent stale analysis from replacing current results.
- [ ] Manual export can include the current analysis and suggestions, while automatic persistence remains disabled.
- [ ] Behavioral tests cover coalescing, long sessions, a question arriving during analysis, failures, cancellation and adversarial transcript content.
- [ ] User documentation explains near-real-time batching, private suggestions and the limits of answer grounding.

## Verification

Record focused tests, compile result and the separate Standards/Spec review here during implementation.
