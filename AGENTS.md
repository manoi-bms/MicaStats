## Agent skills

### Issue tracker

Track specifications and implementation tickets as local Markdown. Before creating or updating them, read `docs/agents/issue-tracker.md`.

### Triage labels

Use the standard triage vocabulary in `docs/agents/triage-labels.md`.

### Domain docs

Use the root glossary and repository ADRs. Before domain exploration or design, read `docs/agents/domain.md`.

### Verification

Run builds and tests serially at BelowNormal priority. Use `test.ps1` for tests. Never build into the running application's Release directory; stage Release output under `artifacts/`.

Use bounded native subagents for independent implementation or review slices when useful. Shared-file owners coordinate changes, and the leader owns final integration and verification.
