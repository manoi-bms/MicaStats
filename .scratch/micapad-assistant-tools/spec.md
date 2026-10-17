# MicaPad assistant tools

Status: in-progress

## Outcome

Reuse the existing .NET/MEAI foundation to let Ask AI inspect Markdown tasks and propose task changes or credential storage inside its selected text/current note. Keep writing actions, provider settings, quota and cancellation behavior intact. No new package, shell access, cross-note access, external MCP connection or release is part of this change.

## Behavior

- Ask AI offers a bounded, sequential tool loop: list tasks, propose completion/reopening, scan explicitly labelled password candidates locally, and propose storing candidate IDs.
- Candidates and stored references are masked before model dispatch; model/tool payloads contain opaque IDs and bounded redacted metadata. Detection is conservative and does not promise to recognize all secrets.
- Proposed changes appear after a successful request in an inline review with task/credential icons, readable effects, individual selection and an explicit Apply selected button. No proposal directly edits a document or vault.
- Apply rechecks the request, shown note, document identity, full document snapshot, read-only/preview state and AI enablement. Changed or cancelled requests cannot apply proposals; retry rebuilds the snapshot.
- Task edits use the existing checkbox controller so created/finished dates remain separate, app-managed metadata. Duplicate proposals collapse by target.
- Selected credentials are encrypted as one vault save before references replace exact copies in the current note. Existing PIN creation, undo clearing, AI/preview clearing and queued history scrubbing apply. A failed vault save leaves the note untouched. File-backed notes retain the existing explicit-save requirement.
- Thai is the default UI; English is supported. Follow DESIGN.md: meaningful icons, clear privacy/status text, theme resources, keyboard access and scrolling at constrained widths/heights.

## Verification

Use fake providers and temporary workspaces/vaults. Prove the actual second model round receives tool results, hard budgets terminate repeated calls, cancellation/quota/disposal still work, no candidate plaintext appears in outbound messages/results, stale/read-only proposals cannot mutate, checked task changes update tracking, and failed batch vault saves do not partially add credentials. Check review controls in Thai/English, dark/light and narrow layouts. Run targeted tests then the full suite serially via test.ps1; stage Release build under artifacts at BelowNormal priority.
