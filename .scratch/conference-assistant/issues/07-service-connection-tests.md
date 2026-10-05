# 07: Test configured voice services before saving

Status: complete

Blocked by: none; private endpoint settings are complete.

- [x] Each ASR/TTS settings card provides explicit Test connection and Cancel actions with inline progress/results.
- [x] Tests use the current draft URL without saving it or changing an active meeting. Invalid/blank URLs send nothing.
- [x] ASR uploads generated silence through the real adapter; TTS generates a fixed test phrase and validates the returned WAV without playback. No real microphone, meeting data, or reference notes are used.
- [x] A bounded timeout, cancellation on editing/closing, and stale-result guards prevent misleading status. Errors never echo endpoints or provider bodies.
- [x] Settings explicitly explain that ASR1/ASR2 are alternatives; only one is needed. TTS is optional for speech output. Readiness reports capabilities rather than requiring three services.
- [x] Fake-provider and WPF control tests cover success, malformed/error responses, cancellation, unsaved input and stale completion; full suite and staged Release pass.

## Verification

Verified 2026-10-05. The full suite passed 5,972 tests with zero failures/skips in 3 minutes. After a final status-text correction and its additional regression, the focused connection/controller/settings/configuration group passed 28/28. Staged Release then built with zero warnings/errors. All builds/tests ran serially at BelowNormal priority using the existing test runner. Four pre-existing xUnit1031 warnings remain in unrelated tests.

Independent review approved the final changes. A low-severity observation that programmatic loading looked like a user edit was resolved with accurate “Not tested for this address” status and a zero-request WPF regression. `git diff --check` is clean. All 844 publication candidates scanned without private endpoint matches. No live service calls, microphone capture, speaker playback or remote publication were part of verification.
