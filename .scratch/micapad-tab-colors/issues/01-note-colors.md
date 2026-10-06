# Color each note's tab

Status: complete
Blocked by: none

Acceptance criteria:
- Distinct automatic colors are stable and persisted per note, with named right-click choices.
- Tint, marker, active underline and picker marker follow Dark/Light themes with readable titles.
- Rename/reorder/moves/restart preserve colors; color edits preserve plain text and file state.
- Navigation, focus and small windows remain usable.
- Targeted/full checks, visual inspection, staged Release and deployment are verified.

Verification evidence:
- 133 targeted persistence, real WPF menu/theme/layout, navigation and task-date checks passed.
- Reviewed four synthetic Dark/Light fixtures at 900×640 and 420×260; tabs and matching picker markers remain readable.
- All 360 hues meet 4.5:1 title and 3:1 marker contrast in both themes. Automatic assignment remains distinct for 24 notes.
- Independent review approved after fixing the current automatic-color row and retaining the selected swatch alongside its check.
- All 6,184 repository tests passed through `test.ps1 -NoBuild`, serialized at BelowNormal priority.
- `artifacts/micapad-tab-colors/Release`: staged Release build passed with zero warnings/errors.
- Deployment used normal Quit for PID 94936, then verified all 60 installed file hashes. Backup: `artifacts/micapad-tab-colors/deploy-backup-20261006-103052`.
- Live installed PID 84768 passed `artifacts/micapad-tab-colors/smoke.ps1`: editor enabled, Notes picker search enabled, picker opened/closed, process remained running. No note text was changed by smoke verification.
- Visual evidence: `artifacts/micapad-tab-colors/screenshots/tab-colors-{dark,light}-{900,420}.png`.
- `git diff --check` passed. No dependencies added; the feature reuses existing metadata/autosave, menu and navigation paths.

Changed files: `Services/Pad/NoteTabColors.cs`, `NoteMeta.cs`, `OpenNote.cs`, `PadWorkspace.cs`; `Pad/NoteTabBrushConverter.cs`, `MicaPadWindow.Navigation.cs`, `MicaPadWindow.xaml(.cs)`, `OpenNotesPicker.xaml(.cs)`, `PadMenuStyles.xaml`; `NoteTabColorTests.cs`, `TabColorWindowTests.cs`, `PadMenuTests.cs`; `DESIGN.md`, `CONTEXT.md`, `GUIDE.md` and this specification/ticket.

## Comments

Root owns WPF rendering, tab/picker/menu wiring, design/docs, integration and deployment. Executor owns the pure color helper and appearance metadata/persistence tests. Preserve all prior uncommitted features.

Deployment recovery: secondary launch and the MicaPad Settings command did not surface the previous process's Quit control. Installed files stayed unchanged during those attempts. `artifacts/micapad-tab-colors/open-settings.ps1` located the installed overlay by class/process across native child windows and sent its existing Settings message; normal Quit and deployment then succeeded. This recovery did not change application source or bypass its shutdown/save lifecycle.
