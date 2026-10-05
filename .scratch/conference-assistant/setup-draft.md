# Local engineering workflow setup — draft

> Historical proposal record. The user approved continuation on 2026-10-05; the canonical specification and issue files track implementation.

The user selected local Markdown tracking on 2026-10-05. The following is the complete proposed setup for review; none of these configuration files has been installed yet.

Two setup choices remain: keep the standard triage labels, and choose whether to create `AGENTS.md` or `CLAUDE.md` (neither currently exists at the repository root). Recommended: standard labels and `AGENTS.md`, consistent with the workspace instructions supplied in this conversation. This small section supplements those instructions; it does not replace the OMX operating contract.

## Proposed instruction-file section

```markdown
## Agent skills

### Issue tracker

Track specifications and implementation tickets as local Markdown. Before creating or updating them, read `docs/agents/issue-tracker.md`.

### Triage labels

Use the standard triage vocabulary described in `docs/agents/triage-labels.md`.

### Domain docs

Use one root glossary and repository ADRs. Before domain exploration or design, read `docs/agents/domain.md`.
```

## Proposed `docs/agents/issue-tracker.md`

```markdown
# Issue tracker: Local Markdown

Specifications and tickets live under `.scratch/<feature-slug>/` in this repository.

- Specification: `spec.md`.
- Implementation tickets: one file per ticket at `issues/<NN>-<slug>.md`, numbered from `01` in dependency order.
- Each ticket has `Status`, `Blocked by`, acceptance criteria, and verification evidence.
- Use the triage vocabulary in `triage-labels.md` for readiness. Use `in-progress` while implementing and `complete` only after verified acceptance.
- Work only on tickets whose blockers are complete.
- Append discussion or decisions under a ticket's `Comments` heading.

When a skill says to publish a specification or ticket, write the corresponding local file. When it says to fetch a ticket, read that file and its comments. Do not create remote issues for this workflow.
```

## Proposed `docs/agents/triage-labels.md`

```markdown
# Triage labels

| Role | Label | Meaning |
| --- | --- | --- |
| Needs triage | `needs-triage` | Requires evaluation |
| Needs information | `needs-info` | Waiting on missing information |
| Ready for agent | `ready-for-agent` | Specified and ready to implement |
| Ready for human | `ready-for-human` | Requires human action |
| Will not fix | `wontfix` | Will not be implemented |

These are local status values; configuring them creates no remote labels.
```

## Proposed `docs/agents/domain.md`

```markdown
# Domain documents

This repository uses a single context.

- Read root `CONTEXT.md` before domain exploration, design, or writing specifications and tests.
- Read relevant decisions in `docs/adr/` when that directory exists.
- If a document is absent, continue. Create glossary entries only as terms are resolved; create ADRs only for consequential tradeoffs that need durable explanation.
- Use the glossary's terms consistently. Keep the glossary free of implementation details.
- Flag conflicts with existing decisions before changing them.
```
