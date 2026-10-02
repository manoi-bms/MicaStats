# Scrolling capture — execution rulings and deferred findings

Plan: `docs/superpowers/plans/2026-10-02-scrolling-capture.md`. Spec: `docs/superpowers/specs/2026-10-02-scrolling-capture-design.md`, kept current through every round. Its **Known limits** section lists what is parked.

Branch `feat/scrolling-capture` is stacked on `feat/micapad-search` (b1fc32a). It was executed subagent-driven on 2026-10-02 and 2026-10-03:
- four tasks, each with a task review;
- a whole-branch review by Claude opus and Codex in parallel;
- one 12-item fix wave, then four regression rounds, each re-checked by both reviewers.

The final suite has 3,666 tests passing, 144 of them scroll tests.

## Rulings made during execution

Each ends with what it costs if wrong.

1. **Stacked on the search branch, not on main.** The README edits and the owner's unmerged search work would conflict otherwise. Cost: a rebase onto main if the search branch is rejected.
2. **File names keep the existing `{mode}` value.** Every mode passes "capture" today. Cost: one string.
3. **The top seek sends 10 notches per step, capped at 300.** One notch at a time would take minutes on long pages. Cost: a constant.
4. **Uniform rows are not counted when matching, and a winning shift of 0 means "unchanged".** Blank space and a blinking caret must not fake a scroll. Cost: a few lines.
5. **Slow apps.** Each scroll waits at least 150 ms before the first grab, and the end and the top are confirmed by a second look 400 ms later. An app slower than one poll would otherwise end the capture early. Cost: about 0.5 s at the end of each capture.
6. **The status card uses `WDA_EXCLUDEFROMCAPTURE`.** It can never land in a frame. Cost: one flag, and the card is hidden from the owner's own recordings while a capture runs.
7. **Final review fix wave.**
   - Shifts are ranked by evidence.
   - A soft footer: floating widgets and changing footers appear once.
   - Side panels are left out of the comparison.
   - The scrollbar strip is scaled for DPI.
   - The top check is tolerant.
   - Wheel input goes only to the picked window (bring it to the front when wheel routing is not hover).
   - Notes at the caps, a notice for an area that is too small, and pins fit the screen.
   - Cost: a larger fix diff.
8. **Side panels.** With a header or footer, the panel's columns in the scrolled rows are filled with the panel's background colour and the image keeps its full width. Without either, the image is cut to the part that scrolled. "The joined image is the scrolled content." Cost: a sidebar missing from the image, as documented.
9. **The top check probes one notch instead of timing animations.** On a change that lines up under no shift, MicaStats sends one notch up and looks again; a second probe follows if needed. This was chosen after the owner asked for research. ShareX and longshot judge movement only by runs of matching rows. Cost: one or two notches per ambiguous step at the top.
10. **Soft-margin leniency is a fallback only.** Shifts are ranked strictly first. A label in the margin must still rule out a wrong shift. Cost: a floating widget plus a duplicated block can still win (a known limit).
11. **The fix wave was bounded after round 4.** Each round of this heuristic found new exotic cases. After round 4, only silent loss or duplication on common pages would block. The final check found none: 1,350 realistic synthetic pages came out exact or stopped with a note. Cost: the known limits reach the owner's e2e.

## Parked (real, deferred)

See the spec's **Known limits** for the full list with inputs. In short:
- repeating content with no unique rows;
- tall uniform regions under an animation;
- a widget plus a duplicated block;
- vertically moving animations at the top (a 35 s seek, but the image is correct);
- large animations in the down phase (NoMatch);
- short contents-column sections and wheel input 500 ms or more late (NoMatch);
- lazy images loading 600 ms or more late;
- content under a floating side element;
- gradients between gutters.

Also deferred:
- hover effects under the centred pointer;
- memory at 3,840 px wide;
- the settle stays exact (a full 800 ms per step while something animates);
- the grid-with-hover probe (4 of 12 wrong, the same as before round 2).

## End-to-end checklist for the owner

Shortcut **Ctrl+Shift+4**, or overlay menu → **Capture Scrolling…**, or Settings → Capture → **Capture scrolling**.

1. A long web page in Edge or Chrome, dragged around the page area: the image starts at the top and ends at the bottom, and the sticky header appears once.
2. A page with a floating chat or help button: the button appears once, at the bottom.
3. A docs or wiki page with a sticky contents column: the article is complete, and the column is cut or filled.
4. A long Explorer list, clicking the whole window: check that the navigation pane fill looks right and that the icon and Type columns are kept (hover highlight).
5. A long MicaPad note: the caret line is fine and the note is complete.
6. In Windows Settings, turn **Scroll inactive windows when I hover over them** off. Start a capture from the Settings test button: the picked window is scrolled, not Settings.
7. Press Esc during the seek to the top (cancels) and during the scroll down (keeps what was joined).
8. Drag an area smaller than 50 × 50: the notice appears beside it.
9. Pin a very tall capture: the pin fits the screen.
10. A page with a GIF or video near the top: the capture starts promptly at the top. The known limit is an animation that moves vertically.
