# 09: Compare two ASR services concurrently

Status: complete

Blocked by: none. User requested concurrent recognition on 2026-10-05.

## Contract and implementation plan

Add an explicit ASR2 + ASR1 choice beside the existing single-service choices. Keep the ASR2 default and all endpoint defaults empty. Dual mode requires both configured addresses before capture starts and clearly says the same audio goes to both services. Remember the choice using the existing configuration property.

Each microphone/output chunk starts one request per provider concurrently. Keep the existing bounded source queues, thirty-second deadlines, stop/restart generation protection and maximum session/transcript limits. One provider's failure must not discard the other's successful result. Both failed providers stop the session with a safe error; successful silence remains distinct from failure. Do not change audio capture, routing or playback behavior.

After a provider's first failure, disable it for the remainder of this meeting generation and visibly report that transcription continues with the survivor, including when no speech segment is produced. Starting a new meeting retries both. This avoids repeating a slow provider's timeout for every chunk and filling the bounded queue. Keep `AsrService` as a concrete adapter selector; an optional `CompareBothServices` start flag enables dual orchestration.

Publish one transcript segment per chunk. Prefer nonempty ASR2 text, otherwise nonempty ASR1 text. Preserve both readings and provider success flags in optional immutable comparison metadata (`MeetingAsrComparison`: `Asr2Text`, `Asr1Text`, `Asr2Succeeded`, `Asr1Succeeded`). Successful empty text is different from an unsuccessful request. Compare case and whitespace conservatively; do not erase punctuation, digits or words to claim agreement. Label agreement, different readings, missing speech or unavailable providers honestly. Never claim confidence or proven accuracy improvement. Count both readings toward retained transcript limits.

Show comparison evidence in the live transcript and Markdown, escaping provider text as transcript data. Pass both readings to the existing bounded AI analysis call as alternative interpretations of the same segment, not two independent corroborating sources. The analysis prompt must preserve uncertainty, especially for names, numbers and negation. No additional per-chunk AI calls, automatic transcript rewriting, new dependencies or live service requests are part of this change.

## Acceptance criteria

- [x] Both ASR requests start before either result completes, for each input source.
- [x] Agreement, disagreement, one empty result, both empty results and either provider failing are deterministic and visible where relevant; one chunk never duplicates transcript rows.
- [x] Both-provider failure, timeout, stop, restart, backlog and retained-text limits remain bounded; obsolete completions cannot publish or fault a newer session.
- [x] Single-service behavior remains compatible; dual mode requires two configured endpoints and survives configuration serialization.
- [x] UI, Markdown and AI input retain labeled alternative readings and uncertainty without raw error details, private endpoints or duplicated source IDs.
- [x] Targeted fake-provider/WPF tests, full suite and staged Release pass serially at BelowNormal priority; synthetic UI preview remains readable at minimum size.

## Verification

Verified on 2026-10-05: dual-ASR/session tests passed 30/30; integrated window/analyzer/config/settings/intelligence/speech checks passed 76/76; the final full suite passed 6,020 tests with no failures or skips. Staged Release succeeded with zero warnings/errors. Compiler/analyzer checks and git diff --check passed; four existing xUnit warnings remain in unrelated tests. All builds/tests ran serially at BelowNormal priority. Normal and minimum-size synthetic previews were inspected; summary results remain accessible by scrolling at minimum size. No audio hardware or live service was used. Real recognition accuracy and latency require a representative labeled audio evaluation; automated checks prove orchestration and evidence handling only.
