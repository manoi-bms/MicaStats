# MicaStats conference assistant

Status: approved for implementation on 2026-10-05. The user authorized continuation of the reviewed plan, test seams, NAudio dependency and setup defaults.

2026-10-05 correction: all voice service addresses are private user configuration. Ship empty ASR1, ASR2 and TTS base URL settings and require configuration under Settings → Meeting before use. ASR2 is a protocol preference only; it never supplies a host. Previously supplied development addresses must not occur in source, tests, public documentation or reachable repository history. Changing service settings stops active voice work before applying them. The user also requested a modern, informative Meeting Assistant redesign, governed by `DESIGN.md`; tickets 05 and 06 track these changes.

## Problem Statement

A conference attendee wants MicaStats to follow both their microphone and the audio they hear, transcribe the meeting automatically, keep useful context ready for questions, and synthesize a chosen response as speech. MicaStats currently provides AI chat and MicaPad notes, but no audio transcription, speech synthesis or meeting-session experience.

## Solution

Add a Meeting Assistant window with explicit Start and Stop controls, microphone and output-device selectors, and visible capture/transcription status. It uses ASR2 by default, with ASR1 available as a selected alternative. During the session it shows a live transcript, running summary, detected questions, and private suggested answers grounded in the conversation and any reference notes the user selects. The user can ask a question directly, save a transcript/report as Markdown, and press Speak to hear a chosen answer or typed text using VoxCPM. Listening pauses visibly during playback and resumes afterward. Captured and synthesized audio stay in memory during normal application use.

The user confirmed these product choices on 2026-10-05: ASR2 preferred; microphone plus selected output device; all recommended private assistance features; manual transcript saving; no raw audio saving; local Markdown tracking. The user subsequently added VoxCPM TTS and confirmed a Speak button with listening paused during playback. Research evidence is recorded in the adjacent research document. The synthetic test artifacts are development evidence, not application audio storage.

## User Stories

1. As an attendee, I want to choose my microphone and output device so that the assistant listens to the intended sources.
2. As an attendee, I want capture to start only when I press Start so that opening MicaStats does not begin a meeting session.
3. As an attendee, I want to see which sources and service are active so that I understand what is being transcribed.
4. As an attendee, I want ASR2 selected by default so that the assistant uses my preferred service.
5. As an attendee, I want to select ASR1 before a session so that I have an alternative when needed.
6. As an attendee, I want Thai and English conversations supported through the selected recognizer's capabilities.
7. As an attendee, I want recognizable speech to appear incrementally with source labels and session times so that I can follow the conversation.
8. As an attendee, I want silence and recognizer control markers excluded from the transcript so that empty input does not create misleading content.
9. As an attendee, I want a running summary and relevant points so that I can catch up while the meeting continues.
10. As an attendee, I want detected questions and suggested answers so that I can prepare a response promptly.
11. As an attendee, I want to type a question myself so that I can ask about something automatic detection missed.
12. As an attendee, I want suggested answers to show their supporting transcript or note references so that I can assess them before speaking.
13. As an attendee, I want unsupported answers to say what information is missing so that guesses are not presented as meeting facts.
14. As an attendee, I want to select reference notes explicitly so that only intended MicaPad material informs this session.
15. As an attendee, I want credential references and protected text excluded so that secrets cannot become meeting context.
16. As an attendee, I want to copy a suggested answer privately so that I decide whether and where to share it.
17. As an attendee, I want analysis to use my configured AI provider so that I do not need a second language-model configuration.
18. As an attendee, I want transcription to remain useful if AI analysis is unavailable so that a chat-provider failure does not stop recognition.
19. As an attendee, I want Stop and closing the window to release capture and cancel pending work so that a session does not keep listening.
20. As an attendee, I want device and service failures shown clearly so that I can correct them and start again.
21. As an attendee, I want to save the transcript and current analysis explicitly so that meeting content persists only when I choose.
22. As an attendee, I want raw audio and transcript content absent from diagnostics and automatic storage so that routine support files do not contain meeting material.
23. As an attendee, I want long sessions and slow services handled with bounded memory and visible limits so that MicaStats remains responsive.
24. As an attendee, I want to press Speak on a chosen answer or typed text so that I can hear it through my chosen playback device.
25. As an attendee, I want supported voice choices and an explicit preview action so that I can choose a voice without automatic playback.
26. As an attendee, I want Stop speaking to cancel synthesis or playback immediately so that I control when speech is heard.
27. As an attendee, I want listening paused visibly during speech playback so that generated speech does not become another conversation turn.
28. As an attendee, I want listening to resume only when the same meeting remains active so that ending a meeting cannot accidentally restart capture.
29. As a user, I want typed-text speech synthesis while a meeting is stopped so that I can use TTS independently.
30. As an attendee, I want a transcript marker for the listening gap so that missed conference speech is not mistaken for silence.

## Implementation Decisions

- A separate Meeting Assistant surface uses the existing WPF styling and app lifecycle. Its entry point is visible from the app menu. Settings store device identifiers and service preferences; startup creates no capture streams or provider requests.
- The proposed capture implementation uses `NAudio.Wasapi` 2.4.0 (one new direct dependency, with Core transitively) for WASAPI microphone capture and render loopback. This compatible .NET 8 dependency was approved by the user with the reviewed plan. Sources remain separate and labeled Microphone and Output. Individual attendees are not inferred from these labels.
- A meeting-session module owns capture, bounded audio queues, transcription ordering, state transitions and cancellation. The UI observes session state and transcript/analysis updates; it does not own the audio pump.
- Each source becomes mono PCM16 at 16 kHz, divided into fixed, non-overlapping four-second in-memory WAV uploads for the first version. A playback pause can finalize a shorter pre-pause chunk; Stop still discards unsent audio. This is near-real-time batch transcription: an update follows each chunk plus service processing time, with no promised subsecond latency. Chunk-boundary recognition quality is an explicit manual validation item; adaptive segmentation is follow-up work if needed.
- The supported provider contract is multipart transcription with a final JSON text response, now verified with synthetic audio on both services. ASR2 defaults to the verified model with language omitted for automatic identification; ASR1 uses its documented model and language defaults. The settings expose the selected service and its model. Both tested routes accepted anonymous requests; adding a key or custom authentication is outside this first implementation unless the service requires it during development.
- Sources carry monotonic session times and stable segment identifiers. Segment order follows audio time, even if service calls complete out of order. ASR requests preserve ordering within each source. Silent loopback can have no callbacks, so elapsed time is not inferred solely from sample count; resuming output retains its true session position. HTTP bodies, raw audio and transcript text do not enter diagnostics.
- Each ASR reply must be a JSON object containing a string `text`. For ASR2 only, strip a leading recognized `language ...<asr_text>` envelope and trim its transcript; an empty result creates no segment. Leave unwrapped text unchanged, including a delimiter spoken within ordinary text. ASR1 uses plain `text`. Missing, malformed or oversized replies are sanitized failures, never transcript entries. Log only safe error categories/status, never provider bodies or exception messages that may contain them.
- Each source permits one in-flight ASR request and eight queued chunks, with a thirty-second request deadline. Queue overflow or device loss stops the whole session visibly, cancels work and preserves received text; no silent device substitution, audio drops, automatic service switch or retry loop. A session also stops at eight hours or two million transcript characters, whichever comes first, offering Save/New session. Bound each ASR response body to 64 KiB and recognized text to 16,384 characters; an oversized response faults visibly rather than being silently truncated.
- Stop cancels capture, queued uploads, analysis and answer generation, releases devices, and rejects late results from that session. Audio not yet transcribed is discarded. Already received transcript text remains visible for manual saving until the window closes or a new session begins.
- AI analysis uses a plain client from the existing provider factory and the model-budget utilities, subject to the existing Assistant enable switch. It does not use the tool-enabled Ask assistant or function-invocation middleware. It is scoped to an explicitly started meeting session, extending the older request-only AI behavior only for this new surface. Transcription can run while analysis is unavailable; a clear analysis status explains configuration or provider errors.
- Analysis coalesces new finalized segments rather than calling the AI model for every audio frame. Proposed cadence: at most one automatic analysis request per ten seconds, only when new text exists, with one in-flight analysis and bounded context. Changing AI settings replaces the client safely; user questions take priority over background analysis.
- Every automatic analysis and manually submitted meeting question consumes one unit from the existing shared daily AI usage meter before dispatch, including an attempted request that subsequently fails or is cancelled. Do not count ASR calls as chat questions or raise the global limit automatically. Display usage/remaining allowance. At the limit, pause AI requests and explain how to adjust Settings; transcription and manual saving continue. With continuous new text, the proposed cadence can use up to 360 automatic requests per hour; the current default of 100 daily requests can therefore pause analysis during a longer meeting.
- The analysis contains summary, key points, questions and private suggested answers. It receives only the bounded meeting context and selected reference notes. Meeting text and notes are data, never permission to invoke system tools, visit links, or send messages.
- Require a JSON analysis result with a string summary; key points carrying text and source IDs; and question entries carrying question text, a suggested answer, missing-information text and source IDs. Each collection is limited to ten items. Direct-question results carry answer text, missing-information text and source IDs. Source IDs refer only to transcript segments or eligible note passages actually included in that request. Validate result types, lengths and all IDs before displaying a completed analysis or answer; malformed output produces a safe analysis status, without silently repairing it through more paid requests. Treat uncited content as unsupported and label it accordingly.
- Each answer distinguishes transcript/note support from missing information. Retain source references through context reduction; a rolling summary must not invent evidence identifiers. The product prepares useful answers, not a guarantee that every possible attendee question is answerable.
- Reference notes use the existing MicaPad reader and credential filtering, with a small read-only metadata-list adapter for local selection. Explicit selection is scoped to this meeting and is its own permission to share only those notes; it does not enable Ask or MCP notes access. MicaPad stores selected text as credentials referenced from ordinary notes; there is no note-level credential flag. Removing a selected reference resets accumulated AI context and derived analysis before rebuilding from the transcript and remaining eligible selections. Storing text from any note as a credential cancels analysis and clears all retained note-derived meeting state, because the existing notification carries no note ID. Preserve Ask's existing callback while notifying the meeting surface, then reload selected references through credential filtering before further analysis or export.
- Manual Save uses a destination picker and writes Markdown containing transcript, source labels/times, current summary and selected suggested answers. Export text is escaped so transcript content cannot impersonate the report's structure. No autosave, raw-audio persistence or transcript restoration on restart.
- TTS uses `<user-configured service address>` with JSON fields `input`, `model=voxcpm-thai`, `voice`, `response_format=wav`, `speed=1.0`, and `stream=false`. A synthetic request verified a finite mono PCM16 WAV at 48 kHz despite OpenAPI describing its response as JSON. The app validates both the response type and WAV structure before playback. No key is currently required by the tested contract.
- The initial voice is `default`. An explicit Refresh voices action reads the service's voice catalog; show the selected ID and display name, and require reselection if a refreshed catalog no longer contains the saved voice. Do not advertise speed control because the service documents it as ignored, or streaming playback because its wire semantics are unverified. No voice enrollment or user voice samples are sent.
- TTS accepts at most 4,096 input characters, one request at a time, with a sixty-second deadline, a 10 MiB response-body cap and a ninety-second decoded-duration cap. Oversized text requires editing; do not truncate or split it into an unbounded series of requests. Validate input before network use, bound body reads even without Content-Length, and decode only supported PCM WAV for the first version. Non-audio or malformed replies become safe errors; they are never forwarded verbatim to logs.
- Speak sends only the explicitly chosen/edited speech text, not the whole transcript or reference collection. Automatic analysis never dispatches TTS. TTS requests do not consume the chat-question quota, as they do not invoke the configured chat model; they remain user-triggered, bounded and separately cancellable. There is no automatic TTS retry.
- Playback uses the same proposed NAudio package through a selected render device in shared mode, with in-memory WAV bytes. Keep the player, reader, stream and acquired device alive until completion. Cancellation stops and joins playback outside its own completion callback, then releases all resources. No additional audio package is proposed.
- Synthesis and WAV validation happen while an existing meeting keeps listening. While standalone synthesis/playback is active, Meeting Start is disabled; the user must Stop speaking before starting capture. Immediately before playback in an active meeting, pause this app's microphone and loopback capture, await both capture-stop barriers, and finalize only pre-pause audio. Already queued ASR for pre-pause speech may complete. If either barrier fails, cancel speech, release both capture sources, fault the meeting visibly and never play or partially resume. The conference itself is not muted or paused.
- After playback ends or is stopped, allow a short 300 ms audio-settling interval, then resume capture only if the same meeting generation remains active and its selected devices remain available. A stopped/closed/replaced session never resumes. Device loss or failed capture restart leaves a visible fault and releases both sources. The listening gap begins when the first capture source stops and ends only when both have resumed; retain its actual bounds even if playback never starts, ends early or restart fails. Show it in the transcript and exports. Buffered playback audio cannot be replayed into ASR on resume.
- A separate speech operation identifier prevents a late response from an earlier Speak from playing after Stop, text replacement or a newer request. Recheck it and the meeting generation after synthesis, after capture-stop barriers, immediately before playback and after settling. Session Stop, window close/reset, selected-answer invalidation, reference removal and the global credential-stored notification cancel affected synthesis/playback and clear retained generated audio/text. Snapshot source IDs with speech derived from an answer, so removing a referenced note invalidates it. Standalone typed speech has no note dependencies. Credential invalidation clears all retained speech state conservatively because its notification carries no note ID.
- Small reusable seams serve actual substitutions: real versus scripted audio, real versus fake ASR, existing chat clients versus scripted chat clients. Avoid broad refactoring of Ask or MicaPad.

## Testing Decisions

Proposed primary test seam: the same meeting-session interface used by the window (start, stop, transcript/status updates, questions, note selection, speech and export). Feed deterministic audio/ASR/chat/TTS/playback inputs at external adapters and assert user-visible outcomes, not private scheduling or class structure.

Contract checks at the ASR adapter validate multipart WAV requests, result normalization, cancellation and sanitized errors using a fake HTTP handler. The existing chat-provider tests, WPF harness and configuration tests provide prior art. A small audio-adapter check validates known PCM fixtures and conversion independently of real recording hardware.

TTS contract checks validate the JSON request, finite PCM WAV decoding, voice catalog, bounded input/body/duration, cancellation, and sanitized errors with fake HTTP. Playback tests cover pause-before-play ordering, partial pause/resume failures, continued listening while synthesis runs, preserving pre-pause speech, actual gap timing, safe resume, Meeting Start during standalone speech, Stop during generation/playback/resume, stale completions and credential/context invalidation. Inject the clock/delay and scripted capture barriers so settling and races are deterministic. Neither the microphone nor speakers are used by automated tests. The user-requested service test uses generated text and a saved test-only audio artifact.

Acceptance coverage includes interleaved sources, silent loopback intervals, model wrappers, stop during transcription/analysis, stale completions after restart, device loss, service errors, all specified resource limits, overlapping analysis triggers, absent AI configuration, daily quota exhaustion, malformed analysis JSON and unknown source IDs, prompt-like transcript content, grounded/manual questions, selected-note removal, storing text as a credential, and explicit-only export. Tests never call a live provider or use the microphone; the separately documented synthetic requests are limited integration checks.

Implementation follows one failing behavior test and its implementation at a time. Run focused tests and compile checks serially at low priority; run the full suite once after integration, then a staged Release build. Review Standards and Spec separately. Hardware capture, real speech accuracy and actual meeting latency remain manual validation items unless the user explicitly starts a controlled live test.

## Out of Scope

- Per-application interception, driver injection, conference bots, automatic meeting joining or hidden capture.
- Automatically sending replies to attendees, automatic speech on new answers, and virtual-microphone injection.
- Attendee identification, guaranteed diarization or acoustic echo cancellation.
- Raw-audio archives, automatic transcript saving or external transcript synchronization.
- General web research, unrestricted notes access or system-control tools invoked from meeting content.
- Undocumented SSE/WebSocket behavior, hard latency guarantees, or claims about server-side audio retention.
- Voice cloning/enrollment, speech-file export, unsupported language guarantees, and uninterrupted simultaneous listening during speech output.

## Further Notes

The selected output device can contain sounds from all applications using it. Speaker playback can re-enter the microphone; keeping source labels and using headphones helps make this limitation understandable. While TTS plays, MicaStats deliberately misses conference speech and marks the gap. Local playback on speakers may also be audible to the conferencing application's microphone; it is not a private audio channel. MicaStats controls its local persistence; the supplied voice services' retention behavior is not established by their published contracts.

The proposed implementation is divided into four independently verifiable feature tickets: 01 live transcription; 02 private analysis and question assistance (blocked by 01); 03 selected-note grounding (blocked by 02); and 04 user-triggered speech output (blocked by 02). Tickets 03 and 04 may proceed independently, but their combined credential/context invalidation behavior must pass final integration verification. The detailed drafts accompany this specification. The user approved the workflow review; implementation tickets are tracked in issues/.

## Implementation verification — 2026-10-05

Tickets 01–04 are implemented. Independent Standards and Spec review findings are resolved and rechecked; details are in [implementation-review.md](implementation-review.md).

| Check | Evidence |
| --- | --- |
| Full repository suite | `artifacts/conference-test.ps1` delegates to `test.ps1`, serialized at BelowNormal priority: 5,917 passed, zero failed, zero skipped, 3 m 5 s. |
| Final targeted regression | Capture tests passed 7/7 after a test-only change awaited the native stop task before simulating a missing-callback timeout. Production code was unchanged after the full suite. |
| Staged Release | `dotnet build Kil0bitSystemMonitor.csproj -c Release -o artifacts/conference-release --nologo -m:1 -p:BuildInParallel=false`, serialized at BelowNormal priority: succeeded with zero warnings/errors. |
| Static checks | Compiler/analyzer checks ran with the builds. Four existing xUnit1031 warnings remain in unrelated diagram/Markdown test files. Working and staged `git diff --check` pass. |
| Dependency restore | Direct NAudio.Wasapi 2.4.0 and transitive NAudio.Core 2.4.0 are present in the resolved assets. |
| Provider compatibility | Synthetic ASR1/ASR2 upload tests and a finite VoxCPM WAV → ASR2 phrase round trip; see research.md. No live microphone or speaker was used. |
| UI/lifecycle integration | Scripted WPF checks cover explicit start, close, standalone speech blocking Start, credential invalidation and replaced-answer cancellation. |

Manual validation remains for the actual microphone and selected output devices, chunk-boundary recognition, Thai/English quality, acoustic feedback and conference latency. The English/Thai guide and controlled checklist are in `docs/meeting-assistant.md`. A permanently blocked native driver cannot be reclaimed safely in-process; pending operations retain ownership and block reuse until cleanup finishes or the application exits.

## Endpoint privacy and UI follow-up verification — 2026-10-05

Tickets 05–06 implement empty user-configured service settings and the modern Meeting Assistant dashboard. The independent privacy review is approved. Final repository verification passed 5,953 tests with zero failures/skips in 2 m 56 s; staged Release passed with zero warnings/errors. Four synthetic screenshots cover unconfigured dark, compact listening light, summary/answers and prepared speech. All 839 publication candidates were scanned without a private endpoint match. The unpublished feature commit is replaced so endpoint-bearing content is not retained in publishable branch history; no remote push is performed.

See [implementation-review.md](implementation-review.md) for test timing findings, focused results and remaining manual hardware validation. All follow-up tests use fake devices/providers, with no live service requests or audio capture/playback.
