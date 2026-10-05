# 12 — Readable transcript, stable scrolling and working answer controls

Status: complete
Blocked by: none (integrated with ticket 11)

## Acceptance criteria

- Compact selectable transcript uses source labels, time ranges and readable body text; source IDs remain in tooltips/export. Comparison alternatives and listening gaps stay explicit.
- Append updates follow new text when at the bottom; preserve reading position and selection when reviewing earlier content. Repeated refreshes must not reset to the top. Provide Jump to latest.
- Summary & answers explains empty/inactive states, validates blank questions, submits with Enter, prevents duplicate manual requests, shows progress and actionable errors.
- Copy includes visible answer context and confirms success. Prepare speech transfers the selected answer to Speech without automatic playback. Preserve selected question across automatic refresh where still present.
- Verify actual scroll geometry, source ordering, control clicks, selection persistence and compact rendering with fake devices/providers. Preserve capture, quota, language and context invalidation behavior.

## Verification

- Full suite: 6,032 passed, zero failures/skips, 3 m 18 s. Final WPF UI checks after adjusting the compact layout: 20 passed. Staged Release: zero warnings/errors.
- Real WPF scrolling assertions verify initial live follow, preserved reading offset and selection, late chronological insertion and unchanged refreshes. Synthetic rendering confirms source/time labels, Thai text, ASR alternatives and the visible question button at normal size.
- Click/key tests verify inactive guidance, blank validation, Enter submission, duplicate-request prevention, busy/read-only input state, complete copy payload and clipboard failure feedback, explicit speech preparation, preserved question selection and empty-answer controls.
- All service/device and clipboard boundaries in these tests are fake; the real clipboard, network, microphone and playback were not used. Existing AI language, credential invalidation, TTS and session tests remain green.
- Repository lookup independently confirmed the original click handlers were wired. The fixes address missing feedback, unconditional selection resets and transcript replacement; no new AI or capture requests are introduced by display updates.
