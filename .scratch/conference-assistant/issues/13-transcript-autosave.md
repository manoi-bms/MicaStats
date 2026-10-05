# 13 — Automatic transcript saving and recovery

Status: complete
Blocked by: none

## Acceptance criteria

- Save received transcript segments, source/time/ID metadata, ASR alternatives and listening gaps automatically in the user's app-data folder. No audio, endpoints or reference-note content in this store.
- Reuse MicaPad's background writer and atomic-file recovery. Flush pending saves on normal close/exit. Report saving/failure visibly and keep a failed close recoverable.
- Restore the latest received transcript on opening Meeting Assistant, without starting devices, ASR, AI or speech. Keep prior meetings as readable local Markdown when starting a new one.
- Provide a saved-transcripts folder action and explain autosave versus manual analysis/report export. This supersedes the original manual-only transcript persistence requirement.
- Test automatic saving without close, recreation/restart, dual readings/gaps, interrupted writes, separate meeting archives, and failed writes/retry. Stage a verified Release for the requested local deployment.

## Verification

- Full suite: 6,042 passed, zero failures/skips (3 m 15 s). After the final Stop-during-save guard and unsupported-format recovery check, 246 focused meeting/persistence tests passed. Four existing xUnit1031 warnings remain in unrelated diagram/Markdown tests.
- Twelve persistence/reopen checks cover saving while active, final flush, recreated sessions, Thai text, both ASR readings, gaps, previous-meeting retention, partial/ready atomic writes, malformed/unsupported recovery files, blocked storage/retry, path containment, and cancellation before a delayed new start.
- WPF close/reopen confirms the received transcript and save status return with no device/provider starts. The 780 × 600 synthetic recovery screenshot shows the save indicator and Saved transcripts button without clipping.
- Staged Release: zero warnings/errors. Diff checks pass. Publication scan: 866 candidate files, zero private service endpoint matches. Tests use synthetic text and fake services/devices; no real meeting audio or network requests.
- Autosave uses the existing MicaPad writer and AtomicFile; no new dependencies. Abrupt termination can still lose a pending write or audio awaiting recognition. Current AI results remain manual-export content.
