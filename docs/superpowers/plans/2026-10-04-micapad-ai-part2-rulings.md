# MicaPad AI, part 2 — execution rulings and deferred findings

Plan: `docs/superpowers/plans/2026-10-04-micapad-ai-part2.md`. Spec: `docs/superpowers/specs/2026-10-04-micapad-ai-note-tools-and-diagrams-design.md`, kept current through every review round.

Branch `feat/micapad-ai`, on top of part 1 (which ends at cf9335f). It was executed subagent-driven on 2026-10-04:
- four tasks, each with a task review and its fix rounds;
- a whole-branch review by Claude opus and Codex in parallel;
- one fix wave, re-checked by both;
- one small residual wave.

The final suite has 4,711 tests passing (six full runs in a row at the end), and the app builds with 0 warnings.

The owner chose "ok do 1+2 then 3+4". This part is 3 (notes as tools for Ask MicaStats and for MCP clients) and 4 (diagram help: **Draw as diagram** and **Fix with AI**). Part 1's rulings are in `2026-10-03-micapad-ai-part1-rulings.md`; its privacy rules bind this part too.

## Rulings made during execution

Each ends with what it costs if wrong.

**Design**

1. **Two switches, both off by default, independent of `PadAiEnabled`:** `AiNotesInAsk` and `AiNotesInMcp`. The readers differ: Ask sends to the chosen provider, an MCP client is another program. Cost: one extra toggle.
2. **The `Redactor` is not applied to note-tool results; credentials are cleaned.** A question such as "what is the server address in my notes" must get the address. Cost: names and IP addresses in notes reach the reader the user allowed; the privacy lines say so.
3. **The note tools need the running app.** The offline bridge answers "MicaStats is not running": notes are encrypted for the app's vault and index. Cost: MCP clients cannot read notes while the app is closed.
4. **The first note-tool call starts the notes without a window.** It restores the saved session first, so the exit flush cannot overwrite it, and reconciles the index once. "Search my notes from Claude Code right after a restart" is the main use. Cost: a start path to maintain beside `OpenPad`.
5. **That start goes ahead only when the saved session loads normally.** A missing or unreadable `session.json` gives "Notes are not ready: open MicaPad once" and starts nothing. Cost: one manual open after an upgrade.
6. **The start's `Restore` keeps the normal loaders.** It is the app starting its notes, with the same writes the next MicaPad open would make. "Reading never writes" binds the lookups and the index build. Cost: a repair happens earlier than the user opened MicaPad.
7. **`get_note` returns up to 16,000 characters per call** (24,000 as first built), so a result of plain text kept in a conversation fits the kept-result cap of 20,000 characters. Text that grows when written as JSON (emoji, quotes) can still be over. A note result over the cap is then kept as valid JSON with fewer whole lines or passages, never cut in the middle, and never with more of the note than a plain cut would keep. Cost: more calls for a long note.
8. **Draw as diagram produces Mermaid only.** It is drawn offline, and it is what models write best. Cost: one instruction string.
9. **Draw as diagram and Fix diagram are offered only where a diagram is drawn** (Markdown notes, and `.txt` while Markdown formatting is on). Cost: one condition.
10. **Fix with AI selects the block's source and runs through part 1's `RunAiAsync` path.** One consent path, and the user sees what will be sent and replaced. Cost: the selection changes on a click.
11. **Fix is offered only for lasting errors about the block's own source.** Not for a block that is too large, a missing runtime, a server that cannot be reached or a timeout: a rewrite cannot fix those. Cost: one condition.

**Privacy and safety, decided in review**

12. **A read tool never changes a note's own files.** A note id is exactly 32 characters of `0-9a-f`, and the lookups use peeks that read in place. Found as a Critical in the task review: an id in another letter case made the loader rebuild `meta.json`. Proven by a byte-identical store test.
13. **Note-derived content is not sent again once access has changed.** When the Ask switch is off, or the destination host differs from the one the notes were read under, the kept tool results and the answers that followed a note read are replaced before the next request. This reversed an earlier "document it" ruling: the rule that a setting is checked right before each request covers what a conversation resends. Cost: earlier answers leave a conversation's context after a provider change.
14. **What counts as a note read is the strict side.** A note-tool result stops counting only when it can hold no note text: a function that threw, a JSON object with `error`, or a string that starts with the function-calling loop's own error sentence (a note tool the model called while it was not offered). Every other result counts, a plain string included, because a result shortened to text is one. Cost: an answer after some other error string is replaced needlessly.
15. **Links are plain text for the rest of a conversation that read notes**, in Ask MicaStats as in MicaPad. The conversation resends tool results, so a later answer can be steered too. Cost: an address is copied by hand.
16. **An `end_process` suggestion is dropped once a conversation has read notes.** Pasted text in a note could steer the model into suggesting it, with a model-written reason. The other suggestion kinds stay. Cost: the user ends a process from the process window.
17. **"Once read notes" lasts until New conversation.** Rules 15 and 16 read one lasting fact (`NotesEverRead`), which a take-back does not undo. "Notes are in what would be sent" (`NotesRead`) is a separate fact, ended by a take-back for a new host. Cost: one more field.
18. **Credential markers.**
    - Titles, headings and text from the note tools are cleaned, including a marker cut at its end.
    - A renderer's error message sent with a fix is cleaned of a marker cut anywhere, and of every id the block itself holds, before it is shortened.
    - The question typed in Ask MicaStats is cleaned (older code).
    - A Kroki block's source is posted with `[credential]` in place of each marker (older code).
19. **A fix stays inside its block.** A reply in a code fence is unwrapped; a reply with a line that would close the block cannot replace it; there is no **Insert below** for a fix. The block's fence is read from the note at **Replace selection** and at **Try again**. While a finished fix is shown, the pane re-reads it once per batch of edits, not per edit. Cost: a rare valid reply is refused.
20. **The production wiring of the two switches is pinned by a test.** Swapping them, or writing `() => true`, used to pass the suite.

**Process**

21. **The fix wave was one dispatch to a fresh fixer**, then a re-check by both reviewers, then one small residual wave of five items. Cost: none seen.
22. **The UI test harness now runs one test body at a time.** About 50 UI test classes share one dispatcher; sync tests entered at a higher priority and pumped nested frames, so async tests could wait 40 seconds to start and then hit their own time limit. As shipped, 2 of 4 full runs failed. A test's time limit now counts from when its body starts. The product has no such race. Cost: a UI test body that hangs holds up the ones behind it.
23. **One test read a note's file while the writer thread was still saving it** (`The_live_reader_finds_and_reads_a_closed_note_and_an_open_note_with_unsaved_text`). A probe showed the writer still busy at that line in 3 of 6 runs. The test now waits for the writer first. The product's own lookups already treat a file that cannot be read right now as unknown.

## Who found what in the final review

- **Codex:**
  - an answer that quoted a note stayed in the conversation after the Ask switch went off (P1);
  - a file opened in a tab after the first lookup was not found until edited;
  - the block's fence was read once and reused, so a shortened fence let a reply close the block;
  - with a finished fix shown, every edit re-read the whole document (re-check).
- **Claude:**
  - a credential id reaching the provider in a renderer's message with one brace or none;
  - a marker cut at its end coming back from the note tools;
  - Draw as diagram offered where no diagram is drawn;
  - an `end_process` suggestion still offered after a note read;
  - kept note results following the conversation to a new provider;
  - the kept-result cap below `get_note`'s cap;
  - the Ask question not cleaned of markers;
  - nothing pinning the switches' wiring;
  - the cause of the intermittent test hang, in the test harness (re-check);
  - a note tool that was never offered counting as a note read (re-check);
  - a kept note result heavy in emoji or quotes still cut mid-JSON (re-check);
  - an `end_process` suggestion coming back after a provider-change take-back while links stayed plain (re-check).
- **The controller:** a test reading a note's file while the writer was still saving it, found in the last full-suite runs.
- **The task reviews:** a read tool rewriting `meta.json` (Critical, Task 2); a long line cut without a sign and a lone surrogate throwing into the MCP server (Task 1); a reply breaking out of its block (Task 3).
- **The automated security review** during the build:
  - the peek methods still delegating to the writing loaders, and `TryLoadText` as a second write path;
  - in the residual wave, a notice on a temporary line that showed a fault in the controller's own brief: "a plain string holds no notes" would have let a note result shortened to text survive a take-back. The brief was narrowed to rule 14 before the commit.
  - Its other notices were temporary mutation lines; each was verified gone at the commit.

## Parked (real, deferred)

- **The first note-tool call can freeze the UI** once per run while `Restore` reads file-backed tabs. It is the cost of opening MicaPad, from a new trigger.
- **The HTTP host has no per-call deadline.**
- **Notes purged by the 24-hour maintenance stay in the index** until the next reconcile.
- **The exit order** (MCP servers stopped before the pad objects are disposed) is pinned by a source-order test only.
- **A bare credential id that a renderer cut short** (part of the id, no brace) is not cleaned. It is a handle with no meaning off this PC.
- **A `$$`-wrapped math reply is refused**, not unwrapped.
- **With meaning search on, the lazy start begins the usual background embedding**, and the first call after a restart waits for the whole keyword index.
- **A fresh note result over the cap is still cut as text for a request without tools** (limited mode). That text is sent once and not kept in the conversation.
- **`FinalAnswerChatClient` does two jobs under one name.**
- **Tests:**
  - some depend on real time;
  - the docs tests were written after the docs;
  - `DiagramPageTests.Every_built_in_kind_draws_a_png_and_an_svg` failed twice in about 37 full runs, in about 1 second each time (a real WebView2 test from v1.13.0; its message was not captured, cause not found);
  - a blocking UI test body has no time limit, and now holds the turn while it hangs;
  - 4 xUnit1031 warnings in `DiagramRendererTests.cs` and `MarkdownExtrasTests.cs`.
- **Load-sensitive tests seen during the build, none from this feature:** `SearchPaneTests.A_search_runs_off_the_UI_thread`, `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn`, `SearchPipelineTests.A_hanging_embedding_server_times_out_to_words_only`, `PadAiRunnerTests.Every_update_re_arms_the_silence_deadline`.

## End-to-end checklist for the owner

The switches are in **Settings → MicaPad → AI**. Both are off after the update. Part 1's checklist still applies.

**Notes in Ask MicaStats**

1. With **Let Ask MicaStats search your notes** off, ask "what do my notes say about …". Ask answers without your notes.
2. Turn it on and ask again. The answer uses a note; a link in it shows as text and cannot be clicked, for the rest of that conversation.
3. Ask about a note that holds a stored credential. The answer shows `[credential]`, never the value or the pill's id.
4. In the same conversation, turn the switch off and ask a follow-up. The model no longer knows what the note said.
5. With the switch on, read a note, then change the provider in **Settings → AI** to another host and ask a follow-up. The same: what was read is not sent to the new host.
6. **New conversation**, then ask something that needs no notes: links are clickable again.

**Notes for MCP clients**

7. With **Let MCP clients search your notes** off, an MCP client (Claude Code) lists nine tools and no note tools.
8. Turn it on. The client lists `search_notes` and `get_note`; a search returns passages with note titles, and `get_note` returns the lines.
9. Restart MicaStats and call `search_notes` without opening MicaPad. It answers. Then open MicaPad: tabs and windows are as you left them.
10. Close MicaStats and call a note tool. It answers that MicaStats is not running.
11. Open a file in a MicaPad tab, then search for a phrase in it from the client. It is found without editing the file.

**Diagram help**

12. In a Markdown note, select a few lines that describe steps, right-click → **AI → Draw as diagram**. A `mermaid` block streams in the pane; **Insert below** adds it and it is drawn.
13. In a `.cs` note, the AI menu has no **Draw as diagram**.
14. Break a Mermaid block (delete an arrow's target). Right-click the red error box → **Fix with AI**; the pane names the block's lines and the host; **Replace selection** puts the fix inside the same fences and the diagram draws.
15. A block that is too large, or a Kroki block with the server off, offers no **Fix with AI**.
16. With **Use AI in MicaPad** off, the error box's menu reads **Set up AI…** and nothing is sent.
