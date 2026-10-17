# MicaPad contextual menus

Status: complete

Reorganize MicaPad right-click menus to make common commands easy to find and context clear,
following DESIGN.md's professional, modern, informative and accessible visual contract.

- Retain every existing command, meaningful icon, shortcut, enabled guard and undo/privacy behavior.
- Add a compact, noninteractive context caption for source/selection/task, note tabs, credentials,
  history versions and rendered read-only text. Do not repeat selected note text or secret values.
- Put current-task actions first, followed by labeled Edit and Write & tools groups. Keep ordinary
  editing commands directly available; group Find/Replace/Go to line in a Find & navigate submenu.
- Group tab personalization, move/file location and close actions. Explain that closing does not
  delete notes. Group credential use, management and reference/security while retaining read-only guards.
- Use restrained Fluent icons, existing Pad theme tokens, clear keyboard gestures, concise help
  tooltips and automation help for advanced or sensitive actions. Section captions must not become
  focusable commands. Keep Header strings and existing action bindings intact.
- Bound and scroll popup content to the available screen; verify dark/light and constrained fixtures.

No new dependencies, AI/network work or changes to note content/storage. Reuse EditorMenus and
PadMenuStyles. Update existing menu ordering tests only where the requested organization changes;
keep behavior tests and add hierarchy/context/accessibility checks. Run targeted and full tests,
staged Release and local deployment smoke checks serially at BelowNormal priority.

## Verification

Grouped task/editor/tab/history/credential menus retain existing command behavior and guards.
Popup layout fits its monitor/DPI bounds and scrolls at constrained heights; dark/light screenshots
were inspected. The full suite passed 6,272/6,272, followed by 185/185 focused checks after
localization integration. Staged Release built without warnings/errors and all 60 local deployment
file hashes matched. The installed keyboard menu opens and exposes the translated Find & navigate
command with accessible help (`artifacts/micapad-ui-update/menu-smoke.log`).
