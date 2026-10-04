# AI chat UI — execution rulings and deferred findings

Plan: `docs/superpowers/plans/2026-10-04-ai-chat-ui.md`. Spec: `docs/superpowers/specs/2026-10-04-ai-chat-ui-design.md`, kept current through every review round.

Branch `feat/ai-chat-ui`, from main 46a0b85. It was executed subagent-driven on 2026-10-04 and 2026-10-05:
- eight tasks, each with a task review and its fix rounds;
- an end-to-end probe by the controller with three real models;
- a whole-branch review by Claude opus and Codex in parallel;
- one fix wave, re-checked by both.

The final suite has 5,276 tests passing (4,713 before this branch), and the app builds with 0 warnings.

The owner asked: "please modify ai chat ui to more informative / user friendly , support markdown display / mermaid diagram (the llm should know this so it correct produce output to has best visual display) and has vertical splitter to allow user resize chat area".

## Rulings made during execution

Each ends with what it costs if wrong.

**What was built, and where**

1. **"AI chat UI" is every place an AI answer is shown:** the Ask MicaStats window, MicaPad's AI pane, and the Ask answer in Search notes. They share one renderer, so one change reaches all three. The splitter is for MicaPad, which has the docked pane. Cost: work in a window the owner did not mean, which still gains from it.
2. **Only `mermaid` fences are drawn in answers, on this PC, never through Kroki,** and they follow **Settings → MicaPad → Draw diagrams**. An answer must not be posted to a server. Cost: `dot` and `markmap` answers stay code.
3. **Draw as diagram and Ask AI… are shown rendered, with a Source toggle;** what is inserted is still the text. Cost: one toggle.
4. **Ask AI… on a note not shown as Markdown starts with Source on.** Its answer is often code, and code rendered as Markdown is shown wrongly. Cost: one click to see it rendered.
5. **One remembered width for the AI pane and Search notes** (`PadPaneWidth`: default 360, 260 to 900, the editor held to 320 while dragging). They share the column. Cost: Search notes opens 60 wider than before.
6. **MicaPad's AI pane stays one request at a time.** A follow-up conversation was not asked for and would change what is sent. Cost: a follow-up feature later.
7. **The Ask window remembers its size, not its position,** fitted to the screen it opens on. Cost: two settings.
8. **Table limits: 12 columns, 100 rows; over that it stays text. At most 8 diagrams per answer.** Cost: constants.

**What models really write** (found by the reviews and by the probe with real models)

9. **A table's delimiter row must hold a pipe.** A sentence holding a pipe in a code span, above a `---` rule, had become a table. Cost: none seen.
10. **A line that starts another block ends a table,** with or without a pipe, as in GitHub's Markdown. Cost: in a table without outer pipes, a row starting like a list item ends it.
11. **A code span in a cell ends where the paragraph parser ends it.** `` `C:\` `` in a cell had swallowed the next column. Cost: none seen.
12. **`<br>` in a table cell is a line break.** A pipe table has no other way, and models use it. It is the one HTML tag that means anything. Cost: a parser rule.
13. **A fenced block indented under a numbered step or a bullet is a code block or a diagram.** Before, the user saw backticks and plain lines. Cost: a pre-pass in the parser.
14. **A Mermaid label with a line break is drawn.** The engine could not read Mermaid's own output for it. This failed for a diagram in a note too, since v1.13.0. Cost: none seen.

**Privacy and safety, decided in review**

15. **A link in a table cell obeys the link rules,** like every other link: none clickable in MicaPad, none in Ask MicaStats once the conversation read notes.
16. **Link removal is never delayed by the paced redraw.** Turning links into text when notes are read, and the last redraw of an answer, are immediate; and a paced request made while the shown document is out of date for links draws at once. A gap in the controller's own brief, shown up by the automated security review.
17. **Storing text as a credential clears everything an AI result left:**
    - the AI pane, also when it was closed before (part 1 had parked that);
    - the Search notes answer;
    - the pictures kept for redrawing, and the diagram draws still waiting in the engine with their source text;
    - an Ask MicaStats conversation that had read notes (older code): it is ended and says why;
    - an Ask answer still under way while Ask may read notes, though it has read none yet: its first note tool may be reading at that moment.
    Cost: a conversation or a result is lost.
18. **A failure of a diagram is remembered and not retried by itself;** one that may pass has **Try again**. Cost: a click after the engine was slow to start.
19. **A model-written tool name is plain text, cut at 40 characters,** with control, format and line-separator characters turned into spaces.

**Resources**

20. **A picture is decoded for the screen, never over 1,600 by 2,400; 64 MB are kept, and always the eight used last.** A 4,000 by 3,000 diagram went from 48 MB to 7.7 MB. Cost: a very large diagram is a little soft at 200% scaling.
21. **A streaming answer is redrawn no sooner than four times what its last redraw cost** (100 ms to 2 s), and a timed redraw waits while the mouse button is held over the answer. Measured: one redraw of an answer with a 100 by 12 table took 0.6 s against a 100 ms timer. Cost: a large table updates every two seconds while it streams.
22. **The splitter saves a width only when the pane was moved.** A click on the bar in a narrowed window had overwritten the saved width.

**Process**

23. **Tests run at below-normal priority, the full suite once per task.** Back-to-back full runs at normal priority made the owner's PC unresponsive. Cost: slower runs.
24. **The prompts were tried on three real models before and after the build,** and every diagram type they name is drawn by the bundled Mermaid in a test.
25. **A closed MicaPad window stays reachable in two edge cases (older code): parked, reported.** The running app hides a window on close and closes one for real only by merging or at exit.

## Who found what in the final review

The two final reviewers did not overlap.

- **Codex** (asked for changes):
  - storing a credential left an answer's diagram source in the drawing engine's queue, which went on to draw it (P1);
  - `` `C:\` `` in a table cell swallowed the next column (P2);
  - the Ask window fitted its saved size to the primary screen while it opens on the screen under the pointer (P2);
  - Alt+Tab while an arrow key is held on the splitter left the move unfinished (P3).
- **Claude** (ready after the fix wave; traced all seven security rules across task boundaries and found them to hold):
  - a code block or diagram indented under a numbered step was shown as backticks and plain lines;
  - `<br>` in a table cell was shown as letters;
  - Ask AI's answer was rendered as Markdown even when it was code;
  - an Ask MicaStats conversation that had read notes was not cleared when a credential was stored (older code);
  - three diagram types the Draw as diagram instruction names were drawn by no test, and nothing pinned how the app wires answer diagrams.
- **The re-check of the fix wave** (both reviewers confirmed the wave; Codex: three of its four fixed, the table one for the reported case):
  - both found the same gap: a credential stored while an Ask conversation's first note read is still under way was not covered, because the conversation is marked only when the tool hands its result over. Fixed by the controller, test first;
  - Claude: the Ask window relied on a MicaPad pane to forget the kept pictures. It now forgets them itself;
  - Codex: a backtick inside a link's address in a table cell still merges columns. Parked: a model does not write that.
- **The task reviews:**
  - a sentence above a rule turning into a table (Task 1);
  - picture memory, the Search notes answer keeping its pictures after a credential store, a diagram failure stuck for the session (Task 3);
  - a closed AI pane never cleared on a credential store (Task 4, older code);
  - a click on the splitter overwriting the saved width (Task 5);
  - nothing showing activity after the limited-mode note (Task 6).
- **The controller:**
  - the splitter's listener keeping closed windows registered (read in the code, proven by the implementer's test);
  - a table costing 0.6 s per redraw (measured), which led to the paced redraw;
  - a Mermaid label with a line break failing to draw, found by drawing three real models' answers on the real page;
  - a placeholder in angle brackets in the README, which a renderer drops.
- **The implementers:** an editor minimum held at rest stopping a narrowed window from shrinking the pane (Task 5, found by the new tests, missed by the reviewer's hand trace).
- **The automated security review** during the build: its notices were marked temporary mutation lines, each verified gone at the commit. One of them showed a gap in the controller's own brief: turning links into text was not on the list of redraws that must never wait.

## Parked (real, deferred)

**Drawing**

- **The drawing page and its browser processes stay for the rest of the run** after the first diagram, in Ask as in MicaPad. Proposed: close the page after an idle time; the engine already makes a new one.
- **A slow diagram holds up all drawing for its 15 s limit.** Eight such blocks in one answer delay a note's own diagrams for two minutes.
- **A theme change rebuilds every answer that holds a diagram at once.** Unnoticeable with a few, a freeze with dozens.
- **The hidden page probably still holds the last drawn source in its DOM** until the next draw (inferred, older behaviour).
- **`Clear` is app-wide.** A credential stored in MicaPad also drops the engine's results for an Ask conversation that never read notes; its pictures stay on screen.
- **Copy image copies the decoded size,** not the engine's 2x picture.
- **Two streaming views holding more than 64 MB of pictures between them** evict each other's and decode again.

**Display**

- **A table right under a list item or a quote, with no blank line, is part of that item** (pinned by a test).
- **"Copied" is lost at the next redraw** of a streaming answer; keyboard focus on a button is lost the same way.
- **Two identical Mermaid blocks in one answer share one Source choice.**
- **A tall diagram in Search notes** sits in a 240-high scroller; a 12-column table in a 260-wide pane wraps hard.
- **A backtick inside a link's address in a table cell** is a code span opener to the cell splitter but not to the paragraph parser.
- **Task lists, footnotes and math** in an answer are shown as written.

**Redraw pacing**

- **In MicaPad a picture arriving mid-stream can give two redraws back to back** (the pane's timer and the answer box's own).
- **For a long answer of many blocks, part of the layout can fall after the cost is measured,** so the cost is under-measured.
- **While the mouse button is held over a streaming answer,** the timed redraw retries every 100 ms without limit.
- **The real "pointer held over the answer" check has no test;** tests replace it.

**MicaPad**

- **History keeps its fixed 280 width,** with no splitter.
- **Alt+Tab away and back with an arrow key still held on the splitter:** the move is saved at the focus loss, and what the held key moves after coming back is not.
- **An indented fence loses its indent by characters, not columns:** a body indented with tabs under a four-space fence loses one level too many.
- **A closed MicaPadWindow stays reachable (older code):** each note's `TextProvider` closure captures the window, and the shown document keeps change handlers that lead back to it. The running app hides a window on close and closes one for real only by merging or at exit, so it is an edge case. Proposed: a static provider that holds only the document, and in `Detach` let go of the shown document and unhook the occurrence handler.
- **The Ask window's fit to its screen is unverified on a second screen and at a scaling other than 100%.**

**Tests**

- Six tests depend on garbage collection; ten start the real browser page.
- Load-sensitive, none from this feature's logic: `SearchPaneTests.A_search_runs_off_the_UI_thread`, `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn`, `SearchPipelineTests.A_hanging_embedding_server_times_out_to_words_only`, `PadAiRunnerTests.Every_update_re_arms_the_silence_deadline`, `PadAiRunnerTests.A_slow_consumer_does_not_trip_the_silence_deadline`, `ImageBoardTests.A_line_with_an_image_gets_a_preview_under_it`, and the real-page tests in `DiagramPageTests`.

## End-to-end checklist for the owner

A model that can call tools and writes Markdown is needed. Set it in **Settings → AI**. For MicaPad, turn on **Settings → MicaPad → AI → Use AI in MicaPad**.

**Ask MicaStats**

1. Ask "What is using my memory? Show the shares as a chart too." The answer has a table and a pie chart drawn as a picture. Beside the dots it says "Thinking…", then lines such as "Reading live status…".
2. Under the finished answer: the time and how long it took ("14:32 · 4 s"). Under the question box: the day's count ("3 of 100 today").
3. On the diagram: **Source** shows the Mermaid text, **Copy** copies it, right-click has **Copy image** and **Copy source**. On a code block: **Copy**, then "Copied".
4. Ask "How did the CPU temperature change over the last hour? Show it." A line chart is drawn.
5. Switch the theme with the sun or moon button: the diagram is drawn again for the theme.
6. Ask for a long answer, scroll up while it streams: a round button at the lower right jumps back to the latest text.
7. Resize the window, close it, open it again: it has the size you left.
8. Turn **Settings → MicaPad → Draw diagrams** off and ask for a chart: the Mermaid block is shown as code.

**MicaPad**

9. Open the AI pane or Search notes: drag the bar between the editor and the pane; double-click it to go back to the default; close and reopen MicaPad, the width is kept. Click the bar without moving it in a narrow window: the saved width does not change.
10. In a Markdown note, select a few steps, right-click → **AI → Draw as diagram**. The pane shows the diagram itself. **Source** shows the fenced text; **Insert below** inserts that text, and it is drawn in the note.
11. While it runs: three dots with "Waiting for …" and the model's name, then "Writing…"; at the end "Finished in 4 s". The source line names the host and the model.
12. **Ask AI…** on a Markdown note: "compare these in a table". The answer is a table. On a `.py` or `.cs` file: the answer starts as text, with **Source** on.
13. **Improve writing** on a paragraph: still shown as text, with **Changes**.
14. In **Search notes**, ask a question with Ctrl+Enter: an answer with a table or a diagram is shown rendered there too.

**Safety**

15. In an answer in MicaPad, a link is text and cannot be clicked, also inside a table.
16. With **Let Ask MicaStats search your notes** on, ask Ask MicaStats about a note; then in MicaPad select a word of that note and store it as a credential. The Ask window's conversation is cleared and says why.
17. Run an AI action on a note, close the pane, store a word of that note as a credential, open the pane again: it is empty.
