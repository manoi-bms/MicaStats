# 10: Select the AI response language

Status: complete

Blocked by: none.

The user reports English summaries and suggested answers for a Thai transcript. The current analysis prompt specifies structure and grounding but no output-language policy.

Follow-up: the user explicitly requested a language selector. Add AI language in Summary & answers with Follow transcript (default), Thai and English. Remember the choice. An explicit Thai/English selection takes precedence; Follow transcript matches the predominant spoken language and may honor an explicit language request in the direct question. Changes invalidate old analysis and prepared speech through existing cancellation/generation handling and refresh active analysis without restarting capture.

- [x] Instruct analysis to write the summary, points, detected questions, answers and missing-information explanations in the predominant transcript language, including Thai for Thai conversations.
- [x] Preserve names and technical terms; do not translate a Thai conversation to English merely because application labels or the system prompt are English.
- [x] Add the three UI choices and configuration round trip, retaining Follow transcript for old/unknown values. Honor explicit selection first and direct-question language requests only in Follow transcript mode.
- [x] Changing language clears old analysis/prepared speech and prevents a late old-language result from appearing, while capture continues.
- [x] Keep transcript/ASR alternatives/notes as untrusted data and preserve uncertainty; budget the actual language-specific system prompt.
- [x] Cover the Thai transcript and question paths with scripted-provider tests, preserving grounding and the single existing AI usage charge.
- [x] Run focused tests, full suite and staged Release. Do not claim a live model quality evaluation from scripted tests.

## Verification

Verified on 2026-10-05: 76 integrated focused tests passed, including language configuration round trips, Thai/English/automatic prompt policies, existing AI quota and context limits, and real WPF selection changes rejecting obsolete results and clearing prepared speech. An independent read-only review confirmed persistence and cancellation wiring. After the minimum-size layout fix, its rendered regression passed and synthetic previews at 780×600 and 1180×820 were inspected. The final full suite passed 6,020 tests with no failures or skips; staged Release succeeded with zero warnings/errors. All ran serially at BelowNormal priority. Scripted providers verify the requested language policy, not actual live model language quality.
