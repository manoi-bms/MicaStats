# Diagnose and fix split scrolling/responsiveness

Status: complete
Blocked by: none
Owner: markdown_scroll_sync (bounded executor); root owns integration and verification

## Acceptance criteria

Implement ../spec.md after a red-capable feedback loop proves the current bug, then verify both
scroll directions, edit following, resize/theme behavior, cleanup and responsiveness evidence.

## Verification evidence

Behavioral red: `test.ps1 -Filter FullyQualifiedName~MarkdownScrollSyncTests` compiled and failed
both real WPF scroll integration cases (`artifacts/micapad-ui-update/scroll-red.log`). Source to
preview left Section 18 at Y=2954.68 instead of the visible -24..226.8 range; preview to source
left source line 1 visible instead of lines 140..147. The fixture mixes headings, lists, tables
and fenced code. Fresh green, broader regressions, staged build and deployment remain pending.

Ranked falsifiable hypotheses, recorded before implementation:
1. Missing source/preview scroll-event wiring causes the observed lack of movement; event hooks
   should change the opposite viewport in the reproduced tests.
2. Height ratios drift across differently rendered block types; source-line/block anchors should
   align Section 18 despite typography differences.
3. Numeric offset restoration loses content position after edits above the view; preserving a
   content anchor should retain the edited section through document replacement.
4. Unguarded bidirectional events cause feedback jumps; coalesced directional transfers should
   remain within one pixel after a further dispatcher turn.
5. Stale events after pane/note/vault/hide transitions can affect inactive views; lifecycle guards
   and cancelling queued work should keep those events inert.

Strengthened the headless harness to assert that the initiating scroll owner actually moved.
With only the two new event subscriptions disabled temporarily, both matching assertions still
failed; subscriptions were restored in `finally`. See `scroll-red-confirmed.log`. Restored code
passed all six initial sync tests plus preview/menu checks (25 total, `scroll-green.log`).

Independent review found tall-block progress, asynchronous feedback, per-scroll tree traversal,
same-source render retry, and resize/theme position risks. Three additional regressions reproduced
tall-code misalignment (Y=928.94 and source line 39 instead of 80..84) and missing render retry
(one build instead of two), `scroll-edge-red.log`. These repairs are in progress; do not treat the
initial green result as final acceptance.

Final focused regression run passed 25/25 (`scroll-final-focused.log`, 10.78 s), including both
directions, tall-code line matching, within-line progress in a 520-word wrapped paragraph,
achievable end clamping without bounce, render retry, edit following, divider reflow and theme
document replacement. Repeated scrolling over 1,950 headings reuses measured anchor geometry
and the existing document; tests assert geometry was actually collected before checking reuse.

Two independent diagnoses resolved responsiveness defects. Deferred ScrollViewer commands
published stale zero viewport/extent data; synchronization now uses the presenter's live
IScrollInfo metrics and applies clamped offsets directly. A real shown-window test then exposed
an endless wrapped-source layout loop: fractional split columns measured AvalonEdit at 413 DIP
and arranged it at 412 DIP. The managed dump `reflow-hang-16444.dmp` showed repeated
TextView.BuildVisualLine/MeasureOverride work. Disabling layout rounding on EditorArea alone
made the unchanged reflow case finish in about one second (`reflow-column-rounding.log`).
Editor-only/preview-only rounding changes, horizontal-scrollbar changes and stopping the status
timer did not fix it. Those experiments and all temporary instrumentation were removed; window
chrome retains pixel rounding and snapping. Fresh final focused tests verify the production fix.

Final acceptance: full suite 6,272/6,272 pass; final related checks 185/185 pass after language
integration. Staged Release builds with zero warnings/errors. Local deployment used normal Quit,
backed up overwritten files and verified all 60 hashes. Installed split actions/read-only preview
and both scroll directions pass, with the original view restored and no note content read or changed.
Evidence: `artifacts/micapad-ui-update/full-tests-localization-final.log`,
`localization-regressions-final.log`, `release-build.log`, `deploy.log`, `split-smoke.log`,
`scroll-smoke.log`. Existing unsupported Markdown/image/math fallback boundaries remain explicit.

## Comments

The earlier split view deliberately used independent scrolling. The latest user instruction
supersedes that interaction choice: panes should now follow corresponding content in both directions.
