# Proposal review

> Historical proposal record. The user approved continuation on 2026-10-05; the canonical specification and issue files track implementation.

An independent architect reviewed the draft specification and three ticket drafts on 2026-10-05. The sequence was judged coherent. Material gaps were repaired before requesting approval:

- Preserve the shared daily AI allowance for automatic analysis and manual questions; keep transcription usable when the allowance is exhausted.
- Use a plain configured chat client without the Ask assistant's tool loop. Validate bounded structured analysis and actual source identifiers.
- Specify chunk duration, concurrency/queue limits, request timeout, response limits and the overall retained-transcript/session cap.
- Match MicaPad's real credential model: references within ordinary notes, and a payload-free notification whenever text is stored as a credential. Fan out that notification without replacing Ask's handler; clear and reload meeting note context safely.
- Require provider-specific leading-wrapper parsing and safe failures without raw provider exceptions or bodies in diagnostics.

Remaining decisions are explicit workflow gates: local setup choices, the new `NAudio.Wasapi` dependency, and user review of the specification, proposed test seams and ticket breakdown. The feature has not been implemented. Synthetic ASR requests establish transport compatibility only; speech quality, hardware behavior and live latency remain unverified.

## TTS scope addition

The user added VoxCPM TTS after the initial three-ticket review and confirmed manual Speak with listening paused during playback. Ticket 04 now adds speech output, depending on 02; its note-invalidation interaction with 03 is part of final integration verification. The specification includes a finite-WAV provider contract, bounded synthesis, a playback device, stop/cancellation handling, capture-stop barriers before playback and guarded resume afterward.

A bounded follow-up architecture review identified three lifecycle repairs, now incorporated: block Meeting Start during standalone speech; fault and release both capture sources if a pause is only partially successful; and preserve actual listening-gap bounds plus the chosen answer's source IDs. Tests use an injected clock/delay and capture barriers to verify Stop/start/resume races deterministically. No further product decision was required. The resulting four-ticket proposal still awaits the outstanding workflow and dependency approvals before implementation.

The user-requested synthetic VoxCPM test returned PCM WAV, and one upload of that generated clip to ASR2 reproduced the exact test phrase. This establishes finite-audio service compatibility, not implemented application playback or microphone behavior.
