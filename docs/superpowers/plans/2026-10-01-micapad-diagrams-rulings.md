# MicaPad diagrams — execution rulings and deferred findings

Plan: `docs/superpowers/plans/2026-10-01-micapad-diagrams.md` (spec
`docs/superpowers/specs/2026-10-01-micapad-diagrams-design.md`). Branch `feat/micapad-diagrams`,
from main f765f15. Executed subagent-driven on 2026-10-01: eight tasks, each with a task review,
then a whole-branch review and one fix wave. Final suite: 2705 tests passing.

## Rulings made during execution

Each with what it costs if wrong.

1. **Task 2 — renderer hardening beyond the plan's code.**
   - A cache hit now replaces the block's waiting draw.
   - A page created after Dispose is closed.
   - A throwing warn callback can no longer stall the queue.
   - The threading rule is stated in the class comment.
   - Cost if wrong: a few lines.
2. **Task 5 → 7 — a right-click on a picture keeps the caret and the selection.** `OnEditorRightButtonDown` returns early inside a `DiagramPicture` (`DiagramPicture.IsInside`). Cost if wrong: a few lines in the window.
3. **Task 6 — a folded heading no longer shows its section's last diagram.**
   - Rule: a picture is placed only when the closing fence, or the block's opening fence, starts the visual line.
   - Also: an immediate renderer answer never redraws during measure.
   - Also: the ineffective `DefaultClosed` was dropped. A hidden block whose fence is retyped comes back unfolded, and its button says so.
   - Cost if wrong: small.
4. **Task 6 — "the repo is CRLF" was rejected.** Every file involved has 0 CR characters. LF stands.
5. **Final review — one fix wave.**
   - The Kroki server box always shows the server actually used (refused entries put the current one back and say so; the box is committed before Kroki is switched on).
   - No Kroki post after Kroki is off or the server changed, and no redirects.
   - Pictures are decoded at the drawn size, with at most 24 kept decoded.
   - The queue reuses a draw made meanwhile.
   - WebView2 crash dumps stay local and SmartScreen lookups are off.
   - Page start is limited to 15 s, and a late page is closed.
   - The page exists before anything is posted to Kroki, so a missing runtime never sends text.
   - Picture markup loses `<script>`, `on*` attributes and `javascript:` links.
   - Cost if wrong: a larger fix diff.

## Parked (real, deferred)

- **Monitor scale change.** Pictures decoded at 100% stay soft after the window moves to a higher-scale monitor, until their line is drawn again. Follow-up: redraw picture lines on `DpiChanged`.
- **Live resize.** Each new width means a new decode, so a live resize with word wrap decodes the visible pictures again. Follow-up: round the decode width up to coarse steps, or reuse a wider decode.
- **Saved SVG files.** The script stripping is partial: `<set>`/`<animate>` to `javascript:` and HTML inside `foreignObject` survive in saved SVG files. Only files opened elsewhere are exposed. Follow-up: an element and attribute allowlist.
- **Page start that never finishes.** Each draw starts another page, and each late page is closed. Follow-up: reuse the start already in progress.

## Deferred minors (left as they are)

- **Queue sharing.** Kroki network time shares the one-at-a-time queue (first in, first out). The "already failed, do not ask again" memory (R6) is kept per board, and a closed tab's waiting draws are not withdrawn.
- **Highlights.** The code shading and the current-line highlight show through transparent pictures.
- **Hidden page lifetime.** The page lives until MicaStats exits. Releasing it after some idle minutes is the owner's decision.
- **Kroki server box.** It commits only on LostFocus or a MicaPad toggle in Settings. Nothing reaches an unapproved host.
- **Settings tests.** The Settings handlers are tested through the pure `KrokiClient.ResolveEntry`, not through the window.
- **Right-click test.** It covers `DiagramPicture.IsInside`, not the handler.
- **Smaller items.**
  - A whitespace-only block over 50,000 characters reads "Too large".
  - `FromFence` accepts a two-character fence (its callers pass only real fences).
  - Picture menu items stay enabled with no callback.
  - No editor-level Home/End/selection test across the picture row (the R9 spike covers it).

## Owner's manual e2e checklist

- **Drawing:**
  - A note with a Mermaid mindmap, a flowchart, a dot graph and a Markmap.
  - Switch dark/light.
  - Hide code / Show code.
  - Typing speed in a note with many diagrams.
  - MicaStats' memory while scrolling it.
- **Exports:**
  - Copy picture into Word (it should be the light version).
  - Save as SVG, opened in a browser.
  - Right-click a picture while text is selected (the selection stays).
- **Kroki:**
  - Turn it on with kroki.io for a PlantUML mindmap and a D2 diagram.
  - With it off, check the message.
  - Type `localhost:8000` in the server box (it is refused and the box shows the server in use).
  - Turn Kroki off while a slow server is answering.
- **After an idle spell:** an uncached Markmap after MicaStats has idled 10 minutes or more.
