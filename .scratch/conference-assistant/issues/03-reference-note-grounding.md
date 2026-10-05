# 03: Prepare answers using selected MicaPad reference notes

Status: complete (automated acceptance; manual hardware check remains)

**What to build:** Select MicaPad reference notes for the meeting and use their eligible content alongside the transcript to prepare supported answers. Clearly identify note references and keep selection, credential changes and context limits consistent across the session and export.

**Blocked by:** 02 — private meeting assistance.

- [x] The meeting UI allows explicit selection and removal of reference notes without automatically reading the whole workspace into AI context.
- [x] Existing note access and credential filtering are reused; credential references and protected payloads never become meeting context. A local read-only metadata list supports selection.
- [x] Suggested answers distinguish note support from transcript support and do not invent source identifiers.
- [x] Removing a reference resets derived context. Storing text from any note as a credential cancels analysis and clears all note-derived meeting state, preserving Ask's existing credential notification and re-reading eligible selected content before reuse.
- [x] Note context follows the selected model's budget; the UI makes omitted or unavailable references understandable.
- [x] Manual export contains only eligible retained transcript/analysis content and validated source references.
- [x] Tests verify explicit selection, unavailable notes, storing text as a credential, preserved Ask notification, note removal, bounded context and unsupported questions.
- [x] English/Thai documentation and the manual microphone/output smoke checklist describe the completed workflow.

## Verification

Record focused tests and the separate Standards/Spec review here. After all four tickets, including note-context invalidation during TTS, run the full suite once and a staged Release build; record results and remaining manual hardware/recognition/playback checks in the parent specification.

2026-10-05 final integration: all 5,917 tests passed (zero failures/skips), followed by 7/7 capture tests after removing a test-only scheduling race. The staged Release build succeeded with zero warnings/errors. Independent Standards and Spec reviews closed all findings; see ../implementation-review.md. Actual hardware capture/playback and recognition quality remain explicit manual checks in docs/meeting-assistant.md.
