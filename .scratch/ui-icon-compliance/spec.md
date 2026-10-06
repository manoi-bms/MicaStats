# Consistent action icons

Status: complete

Audit and correct missing or unsuitable action icons in MicaPad popup menus, action panels/dialogs, and Settings including imported AI/search settings. Follow DESIGN.md: restrained professional Fluent icons beside clear text, meaningful section cues, inherited themes, accessible names and keyboard behavior. Preserve all current task-date, navigation, color, vault and Settings functionality.

Cover dynamic/loading button labels so changing Content retains the icon. Reuse the existing menu icon argument and a small button content-template helper rather than duplicate button structures or alter commands. Keep selector choices and compact status controls readable at minimum width; a parent icon or existing color swatch may provide the cue for choices. No dependencies or unrelated features.

Verify menu coverage and actual icon/label rendering, dynamic states, accessible names, and layout in both themes. Use hardware/network-free fixtures for visual inspection. Run checks serially at BelowNormal through test.ps1, stage Release under artifacts, deploy through normal Quit, verify file hashes and live UI availability.

Completed 2026-10-06: corrected missing and unsuitable menu/button/section icons, retained dynamic content and accessible names, and repaired narrow action/title/footer layouts. Full suite: 6,199 passed; final focused checks: 28 passed. Independent review approved and 26 synthetic theme/size fixtures passed visual inspection. Staged Release built with zero warnings/errors at `artifacts/ui-icon-compliance/Release`. Deployment backed up the previous installation, verified all 60 file hashes and relaunched PID 45476. Live editor/Notes picker and Settings open/close checks passed. See the implementation ticket for files and evidence paths.
