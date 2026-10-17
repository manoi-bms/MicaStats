# Responsive Markdown split view and synchronized scrolling

Status: complete

The user reports an unresponsive split view and requests both panes follow the same content.
Preserve the left editable source/right read-only preview introduced by the previous feature.

- Build and run deterministic failing WPF integration tests for source-to-preview and
  preview-to-source scrolling on synthetic Markdown with headings, paragraphs, lists, tables
  and code whose rendered heights differ from source heights.
- Align corresponding content using source/render anchors rather than assuming equal scroll
  percentages. Prevent feedback loops, preserve active pane control and follow the current edit.
- Retain a stable content anchor while preview updates and during resize/theme changes. Debounce
  typing and coalesce expensive work; measure the reproducible responsiveness case before fixing it.
- Clear mappings and pending work on note/pane/vault/hide/exit transitions and reject stale work.
  Bounded previews must report limits and not imply matching content beyond what is rendered.
- Preserve document text, caret/selection where user scrolling should not move them, task metadata,
  undo, autosave and existing privacy guards. No AI/network calls or new dependencies.

Root owns serial BelowNormal test/build execution and integration. A bounded executor owns the
preview/scroll implementation and new tests; root owns the independent menu redesign. Verify targeted
and full suites, normal/minimum theme fixtures and deployed scroll behavior before completion.

## Verification

Shown-window regressions cover both directions, tall code, wrapped paragraphs, fractional-width
reflow, edit following, divider/theme changes, anchor caching, feedback suppression and render retry.
Full suite 6,272/6,272 and final related checks 185/185 pass. Staged Release builds with zero
warnings/errors; normal-Quit local deployment verified all 60 file hashes. Installed split UI
and actual source-to-preview/preview-to-source scrolling pass, restoring the original view without
reading or changing note content (`artifacts/micapad-ui-update/split-smoke.log`, `scroll-smoke.log`).
