# Organize MicaPad right-click menus

Status: complete
Blocked by: none
Owner: root

## Acceptance criteria

Implement ../spec.md, retain command behavior and verify grouped menus, meaningful help/icons,
noninteractive captions, read-only guards and dark/light popup rendering. Preserve the prior
Markdown split work and integrate the separately owned scroll-sync fix before deployment.

## Verification evidence

Initial targeted run passed the menu, action, task/vault and icon regressions. Fresh focused run
passed 25 checks including the eight new contextual-menu cases (`artifacts/micapad-ui-update/scroll-green.log`).
Dark/light popups were visually inspected at 600 and 260 pixels tall; a second headless layout
pass settles shared gesture widths, and card-bound assertions reject clipping. Independent review
confirmed command guards/callbacks and secret boundaries; its secondary-monitor bounds finding is
repaired with deterministic monitor/DPI sizing coverage.

Final acceptance: full suite 6,272/6,272 pass; final localization/date/scroll checks 185/185 pass;
staged Release builds with no warnings/errors. Local normal-Quit deployment backed up replaced
files and verified all 60 hashes. Installed keyboard opening, translated Find & navigate and
accessible help pass (`artifacts/micapad-ui-update/menu-smoke.log`). Corresponding full test,
targeted test, build and deployment logs are in the same artifact directory.

## Comments

Existing tests cover editing, tab movement/closing, task dates, vault behavior and icon coverage.
Those behavior contracts remain the regression baseline; visible root ordering changes intentionally.
