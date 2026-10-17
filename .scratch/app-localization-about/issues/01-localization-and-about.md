# Localize interface and redesign About

Status: complete
Blocked by: none

## Acceptance criteria

Implement the parent specification's language preference, shared catalogs, translated UI
and responsive About artwork/cards. Preserve existing Markdown/menu repairs.

## Verification

Full regression: 6,272/6,272 pass. Final targeted localization, date-rendering and scroll checks:
185/185 pass. Staged Release: zero warnings/errors. Normal Quit, backup, copy and all 60 deployed
file hashes verified. Live Settings and language/About smoke checks pass, including immediate
Thai/English label changes, persisted/restored Thai preference, navigation and informative cards.
Evidence: `artifacts/micapad-ui-update/full-tests-localization-final.log`,
`localization-regressions-final.log`, `release-build.log`, `deploy.log`,
`settings-smoke.log` and `language-about-smoke.log`. Dark/light and narrow/wide About screenshots
are in that folder's `screenshots/`. Independent final code review approved with no actionable findings.

## Comments

The owner's requested default is Thai. UI language and AI answer language are separate settings.
