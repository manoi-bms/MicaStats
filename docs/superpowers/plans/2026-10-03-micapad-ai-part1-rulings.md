# MicaPad AI, part 1 — execution rulings and deferred findings

Plan: `docs/superpowers/plans/2026-10-03-micapad-ai-part1.md`. Spec: `docs/superpowers/specs/2026-10-03-micapad-ai-writing-and-answers-design.md`, kept current through every review round.

Branch `feat/micapad-ai`, from main e37acbe (v1.14.0). It was executed subagent-driven on 2026-10-03 and 2026-10-04:
- seven tasks, each with a task review and its fix rounds;
- a whole-branch review by Claude opus and Codex in parallel;
- one fix wave, re-checked by both.

The final suite has 4,265 tests passing, and the app builds with 0 warnings.

The owner asked: "we already has ai llm api support , please check how we can use it to enhance function and feature in MicaPad", then chose "ok do 1+2 then 3+4". This part is 1 (AI on selected text) and 2 (ask your notes). Part 2 (notes as tools for Ask MicaStats and MCP, diagram help) follows with its own spec.

## Rulings made during execution

Each ends with what it costs if wrong.

**Design**

1. **`PadAiEnabled` is independent of `AiAssistantEnabled`.** That switch turns off the Ask window, not all LLM use. Cost: one extra condition.
2. **The provider, key and daily limit are shared with Ask MicaStats.** One place for keys and one budget. Cost: a user who wants separate budgets has none.
3. **The `Redactor` is not applied to note text.** A rewrite must return the user's own words. Cost: names and IP addresses in notes reach the provider the user chose; the settings page says where text goes.
4. **Limits:** 8,000 characters for a rewrite, 24,000 for a read or custom action, 4,096 output tokens, 8 sources, replies capped at 64,000 characters. Cost: constants.
5. **A stopped, failed, cut-short or filtered reply never replaces the selection.** It may be inserted below. Cost: one flag.
6. **An AI session runs once.** Try again builds a new one. Cost: a few lines.

**Privacy and safety, decided in review**

7. **The setting is checked where the text is read.** Every entry point refuses when AI is off, and it is checked again right before each request, with nothing awaited in between. A setting that cannot be read counts as off. An automated security review raised this mid-build; it was fixed and then proven by mutation.
8. **Try again and a typed instruction run on the text the pane names**, never on another tab's note or a new selection. Cost: the user runs the menu action again for new text.
9. **Links in an answer are never clickable.** They show as "label (address)". Pasted text in a note could steer the model into emitting a link whose address carries other passages. Cost: an address is copied by hand.
10. **Note text and source passages are wrapped as data** in a tag the text cannot close (`<note>`, or `<note-x>` and longer when the text holds that tag). Cost: one prompt format.
11. **Credential markers.**
    - A placeholder takes a prefix that does not occur in the text.
    - A selection that cuts a marker is widened to the whole marker.
    - A marker cut by a selection is cleaned in a question, an instruction and a search query.
    - **Narrowed after the re-check:** a start-cut fragment is cleaned only at the very start of the text. Mid-text look-alikes such as `{{NAME}}`, math and JSON are left as typed. Cost: a fragment of an id typed by hand in the middle of a sentence reaches the provider; it is a handle with no meaning off this PC.
12. **The destination host is shown where the action runs** (the pane's source line, the notes status). A provider change in Settings → AI would otherwise redirect MicaPad's text unseen. Cost: a few characters of UI.
13. **Storing text as a credential closes the AI pane for that note and clears a notes answer.** Both could hold the plain value. Cost: a result is lost.

**Process**

14. **Citations are plain numbers matched to numbered result rows.** Jumping from a citation is a follow-up. Cost: one more click.
15. **The fix wave was one dispatch to a fresh fixer**, then a re-check by both reviewers, then two residual fixes by the controller with RED and GREEN evidence. Cost: none seen.

## Who found what in the final review

- **Codex:**
  - a content-filter stop treated as a complete reply;
  - action text able to close its data wrapper;
  - the over-broad fragment cleaning that damaged `{{ENV}}` and JSON (re-check);
  - a leftover typing timer cancelling a running answer (re-check).
- **Claude:**
  - a credential marker fragment reaching the model from a question or an instruction;
  - key repeat on Ctrl+Enter sending one counted request per repeat;
  - the docs denying what a question sends;
  - nine smaller items: Stop sharing a slot with Replace, Insert below turning Replace off, the plain value left in the pane after a store, nothing naming the destination, and others.
- **The automated security review** during the build: the consent gate guarding only some paths, and Try again sending another note's text. Both were fixed before the task reviews closed.

## Parked (real, deferred)

- **Search result rows keep a stored value's snippet** until the next search (plain search has done this since v1.14.0).
- **A pane closed by hand before a store keeps its text in memory.** It is never shown again.
- **Look-alike tags** (`<//note>`, full-width brackets, zero-width characters) do not change the wrapper tag. No real closing tag can be written. Hardening: a random suffix per request.
- **"Cut short at the length limit"** is shown for endings that are not a length limit (`pause_turn`, a tool-call stop).
- **The destination may be trimmed** in a narrow pane; it sits at the end of a one-line source line.
- **The first Ask after opening the pane** on a large store waits for the index with nothing shown and no timeout.
- **Typing in the query box clears a finished answer** once the live search runs. Reopening the pane does too.
- **The source header line** (title, heading) is named as data in the prompt but sits outside the tag.
- **Insert below** then undo is not detected. With the range gone and the caret on an empty line, the result goes after the previous text line.
- **A source note moved to another window** leaves a message in the old window that cannot be followed there.
- **Turning the setting off mid-stream** does not cancel a request already sent.
- **Code:** the redraw throttle is written twice (AI pane, Search pane); the clipboard try/catch three times; `AskNotesAsync` repeats `RunSearchAsync`'s search call.
- **Tests:**
  - some depend on real time (50 to 300 ms), the real keyboard state or font metrics;
  - the one line passing `e.IsRepeat` is untested;
  - the settings tests read the XAML and code-behind as text.
- **Load-sensitive tests seen during the build, none from this feature:** `SearchPaneTests.A_search_runs_off_the_UI_thread`, `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn`, `SearchPipelineTests.A_hanging_embedding_server_times_out_to_words_only`.

## End-to-end checklist for the owner

Turn it on first: **Settings → MicaPad → AI → Use AI in MicaPad**. The provider and key are the ones in **Settings → AI**.

1. With the toggle off: the AI menu shows only **Set up AI…**, Ctrl+Shift+A says how to turn it on, and **Ask** in Search notes shows the turn-on sentence. Nothing is sent.
2. Select an English paragraph, right-click → **AI → Improve writing**. The result streams in the pane; **Changes** shows the diff; **Replace selection** swaps it; one Ctrl+Z restores it.
3. The same with a Thai paragraph, and **Translate to English** / **Translate to Thai**.
4. Select text that holds a stored credential pill, run **Fix spelling and grammar**: the pill is still a pill after Replace, and the pane never shows the secret value.
5. **Summarize** with nothing selected on a long note: the answer is rendered Markdown; **Insert below** adds it at the end.
6. **Ctrl+Shift+A**, type an instruction, Enter. Check the source line names the text and the host ("· to …").
7. While a reply streams: type elsewhere in the note, then Replace still lands on the right text. Type inside the selection: Replace is off with a reason.
8. Switch tabs while a result is shown: the buttons are off; back on the source note they return. **Try again** on another tab sends nothing.
9. **Stop** mid-reply: the text stays, marked "Stopped", and cannot replace the selection.
10. Search notes: type a question, **Ctrl+Enter**. The answer streams above the rows; rows 1 to n are numbered; a citation [2] matches row 2; clicking the row opens the note.
11. A question whose answer is only in a closed note.
12. A link in an answer shows as text and cannot be clicked.
13. With a local Ollama model in Settings → AI: the privacy line says everything stays on this PC, and the pane says "· to this PC".
14. Set the daily limit to 1 in Settings → AI: the second action says the limit is reached and sends nothing.
