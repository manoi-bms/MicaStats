# AI chat UI: richer answers, a resizable pane, and saying what is going on

The owner asked on 2026-10-04: "please modify ai chat ui to more informative / user friendly , support markdown display / mermaid diagram (the llm should know this so it correct produce output to has best visual display) and has vertical splitter to allow user resize chat area".

Lines marked **(R)** are rulings made without asking, under the owner's standing goal ("autonomous continue implement until finish"). Each can be changed.

## What "AI chat UI" means here

**(R)** Every place MicaStats shows an AI answer:

- the **Ask MicaStats** window;
- MicaPad's **AI pane** (the result of an AI action);
- the **Ask** answer in MicaPad's **Search notes** pane.

All three draw an answer with the same code (`ChatMarkdown`, then `ChatDocument`), so one change to it reaches all three. The splitter is for MicaPad, where the two panes are docked beside the editor at a fixed width. Ask MicaStats is a window of its own, and it remembers its size.

Not in scope: follow-up questions in MicaPad's AI pane (it stays one request at a time), new AI actions, and any change to what is sent, to consent, or to the note tools.

## 1. What an answer can show

### 1.1 Tables

- A Markdown pipe table is shown as a table: a header row, a delimiter row (`| --- | :---: | ---: |`), then body rows. The outer pipes are optional. A pipe inside a code span, or written `\|`, does not split a cell.
- *(Added after Task 1's review.)* The delimiter row must hold a pipe itself: a line of only dashes under a line of text is a rule, as it always was. A pipe inside a code span does not make a line a header either.
- The delimiter row sets the number of columns and each column's alignment (left, centre, right). A body row with fewer cells is padded; extra cells are dropped.
- Cells hold the same inline styles as a paragraph: bold, italic, code, strike-through and links.
- The table ends at a blank line or at a line that starts another block (a heading, a list item, a quote, a fence, a rule), whether or not that line holds a pipe. A line of plain text right under the last row is one more row, with one cell.
- **Limits (R):** at most 12 columns and 100 body rows. A table over either limit is not a table: its lines stay as they are today, plain text with their pipes. Text is never lost.
- A header row whose delimiter row has not arrived yet (an answer still streaming) is a paragraph until it does.
- Look: header cells in semi-bold on the code background, a thin divider under each row, cell text wrapping inside its column. The table is as wide as the answer.

### 1.2 Code blocks

- A code block gets a small header: the language word as written (nothing when there is none) and a **Copy** button, which copies the block's text and reads **Copied** for a moment.
- The code stays selectable, with its own Copy and Select all menu.

### 1.3 Mermaid diagrams

- A fenced block whose language word is `mermaid` is drawn as a picture, once its closing fence has arrived. While the answer streams and the fence is still open, it is shown as code.
- **(R)** Only `mermaid`. Every other fence is code, as today: `dot`, `markmap`, `svg`, and every type that needs Kroki. The prompt names Mermaid, and an answer must never be posted to a Kroki server.
- It is drawn by the page MicaPad already uses for its own diagrams: a hidden browser page that loads only the app's bundled files and refuses every other request. Nothing leaves the PC.
- Above the picture: the word **Diagram**, a **Source** toggle (shows the Mermaid text in place of the picture) and **Copy** (copies the Mermaid text). The picture's right-click menu has **Copy image** and **Copy source**.
- The picture is shown at its own size, and scaled down to the answer's width when it is wider. It is never enlarged.
- While it is being drawn: "Drawing the diagram…" above the source.
- When it cannot be drawn: the source as a code block, and under it "This diagram could not be drawn: " and the renderer's message. This covers a syntax error from the model, a missing WebView2 Runtime, and a draw that took too long. Nothing is thrown.
- A failure is remembered and not tried again by itself, so a diagram that fails cannot make the answer redraw for ever. *(Added after Task 3's review.)* A failure that may pass (the engine was still starting, a draw took too long, the runtime was missing) has a **Try again** button; a syntax error has none.
- It follows the theme: a dark answer gets a dark diagram, and switching the theme draws it again. An answer without a diagram is only repainted, as before.
- **Limits (R):** at most 8 diagrams per answer; a source over the size MicaPad draws in a note is not drawn. A ninth diagram is shown as code; an over-size one as code with "Too large to draw".
- **(R)** Diagrams in answers follow **Settings → MicaPad → Draw diagrams**. While it is off, a Mermaid block is code.
- The pictures are kept in memory only, as MicaPad's are.
- **Memory (R).** *(Added after Task 3's review.)* A picture is decoded no larger than the screen needs (its own size times the display scaling) and never over 1,600 by 2,400 pixels. Up to 64 MB of pictures are kept for redrawing, and always the eight used last, so one answer never loses its own. The pictures of answers still on screen stay with those answers until **New chat** or the window closes.

### 1.4 What an answer never does

- **It loads nothing.** An image written as `![alt](address)` stays text. No HTML is interpreted. No address is fetched to draw anything.
- **The link rules do not change.** In MicaPad, and in Ask MicaStats once the conversation read notes, no link is clickable. That holds for a link inside a table cell too.
- A diagram is a picture: nothing in it can be clicked.

### 1.5 Keeping up while an answer streams

*(Added during the build, after measuring: one redraw of an answer with a 100 by 12 table takes about 0.6 s, and a streaming answer was redrawn every 100 ms.)*

- A streaming answer is drawn again no sooner than 100 ms after the last time, and no sooner than four times what that redraw took, measured to the end of its layout; never longer than 2 s. So a heavy answer takes a fifth of the window's time, not all of it.
- A redraw on the timer waits while the left mouse button is held over the answer. The document is rebuilt on every redraw, so without this a click that began on **Copy** or **Source** could end on the button's replacement and be lost.
- The last redraw, when the answer ends, is immediate and waits for nothing.
- A picture that arrives is drawn with the next paced redraw, and waits for the mouse in the same way; several pictures arriving together give one redraw.
- This holds in the Ask MicaStats window, the AI pane and the Search notes answer.

## 2. The model is told

Both system prompts say what the answer is shown as, so the model writes for it.

**Ask MicaStats** (`AiPrompts.System`), two rules added:

> - Your answer is shown as rendered Markdown: headings, bold, lists, tables and fenced code blocks. Use a table to compare numbers across several items (processes, disks, days), and a fenced code block with its language for commands or code. No HTML and no images.
> - A fenced code block that starts with ```mermaid is drawn as a diagram. Use one only when a picture explains better than text: "pie" for shares of a whole, "xychart-beta" for a value over time, "flowchart" for steps or causes. Keep it small (at most about 12 items), put labels that hold punctuation in double quotes, and always give the key numbers in text or a table too, because a diagram that cannot be drawn is shown as its source.

**MicaPad** (`PadAiPrompts.System`), the rule for answers becomes:

> - For a summary, an explanation or a question, answer briefly in Markdown, in the language of the text unless the user writes in another language. The answer is shown rendered: headings, lists, tables and fenced code blocks, and a fenced code block that starts with ```mermaid is drawn as a diagram. Use a table or a diagram only when it makes the answer clearer. No HTML and no images.

- The rewrite rule is unchanged: a rewrite comes back as the text only.
- **(R)** Every Mermaid type a prompt names is drawn by the bundled Mermaid in a test, so a prompt never asks for a type the app cannot draw.
- The Ask MicaStats prompt is one constant, as before, so a provider can cache it.

## 3. MicaPad's side pane

### 3.1 A splitter

- A vertical bar between the editor and the side pane. Dragging it resizes the pane. It is there only while a pane is shown.
- The AI pane and Search notes share the column, so they share the width.
- **(R)** Default 360 (today the AI pane is 320 and Search notes 300). At least 260, at most 900, and never so wide that the editor has less than 320. When the window is too narrow for both, the pane keeps 260.
- A double-click on the bar goes back to the default. It can be moved with the arrow keys.
- The width is remembered across restarts, one value for every MicaPad window (`PadPaneWidth`). It is saved when a drag ends.

### 3.2 Preview and source

- **Draw as diagram** and **Ask AI…** now show their result rendered, as **Summarize** and **Explain** do. So Draw as diagram shows the diagram itself before anything is inserted.
- A **Source** toggle, beside **Changes**, shows the text exactly as **Insert below** and **Replace selection** would put it in the note. It is offered whenever a result is shown rendered and there is text to show: not while the pane waits for an instruction, and not for a request that was refused or failed with nothing.
- A rewrite (Improve, Fix, Shorten, Translate, Fix diagram) is still shown as its text, with **Changes**.
- What is inserted or replaced does not change: always the text, never the rendering.

### 3.3 Saying what is going on

- While a request runs, three dots move in the pane, with a line: "Waiting for <model>…" until the first words arrive, then "Writing…". Without a model name: "Waiting for the model…".
- When it ends: "Finished in 4 s". Over a minute: "Finished in 1 min 5 s". A stopped or failed request says what it already says.
- The source line names the model after the destination: "Selection, 412 characters · to api.anthropic.com (claude-sonnet-5-5)". The line wraps and is never cut short, so the destination is always readable.

## 4. The Ask MicaStats window

- **What it is doing.** Beside the moving dots: "Thinking…", and while a tool runs, what it does: "Reading live status…", "Checking top processes…", "Looking at history…", "Listing slowdown reports…", "Reading a slowdown report…", "Checking alerts…", "Reading hardware info…", "Checking the battery…", "Checking startup times…", "Searching notes…", "Reading a note…". Another tool: "Using <name>…". It goes when answer text arrives, and comes back if a tool is used after that.
- **How long it took.** Under a finished answer, after the time: "14:32 · 4 s".
- **How many questions are left.** The hint under the question box ends with the day's count: "Enter to send · Shift+Enter for a new line · 12 of 100 today". It is refreshed when the window opens and after each answer.
- **Jump to the latest.** When the transcript is scrolled away from its end, a round button at its lower right jumps back to it.
- **(R) It remembers its size** across restarts (`AskWidth`, `AskHeight`), never smaller than its minimum and never larger than the screen's work area. It still opens centred.

## 5. Failures

- Drawing an answer never throws. Markdown that cannot be rendered is shown as plain text, as today.
- A diagram that fails is a code block with the reason (1.3). A missing WebView2 Runtime is one such reason, said once per diagram.
- A width or a size that cannot be read from the settings is the default.
- Logging: a failure is logged by its exception type only, never with answer text.

## 6. Privacy and safety

Nothing here sends anything new. What matters:

- **No fetch.** Drawing an answer makes no network request. The diagram page refuses every address but the app's own bundled files, by its Content-Security-Policy and by a request filter; that page and its tests exist already.
- **No Kroki.** An answer's diagram is drawn only on this PC.
- **Links.** `RemoveLinks` must leave no link anywhere in a document, tables included.
- **Copy** goes through one replaceable hook, so a test never touches the real clipboard.
- **The AI pane's Clear** (a credential was stored from the note) also drops the Source toggle, the diagrams shown and the answer pictures kept for redrawing. The Search notes answer is dropped the same way. *(Added after Task 4's review.)* A pane that was closed before the credential was stored is cleared too: it still held its last result, unseen. (Part 1 had parked this; with pictures kept as well, it is fixed here.)
- **Consent.** No consent check in MicaPad's window files is moved, removed or reordered.

Known limit: the drawing engine keeps the last 64 pictures in memory, as it does for a note's own diagrams. A picture drawn from a result that was later cleared stays there until it is pushed out or MicaStats exits. It is never shown again and never written to disk.

## 7. Tests

- Pure: the table parser (shapes, alignment, limits, escapes, streaming halves, text never lost), the closed-fence flag, durations, the pane width rule, the window size rule.
- UI, on the shared UI thread with fakes: a table in a document, a link in a cell taken out, the code header and its Copy, a diagram's four states (off, drawing, drawn, failed) with a fake drawing engine, a redraw when a draw ends, the theme change, the pane's Source toggle, the activity lines, the splitter's limits.
- Real drawing page: every Mermaid type the prompts name draws.
- No test uses the network, the real clipboard, `%APPDATA%`, or a running MicaStats.

## 8. Docs

`GUIDE.md` and `README.md` (English and Thai): what answers can show, that diagrams are drawn on this PC, the splitter, Preview and Source, and the new lines in the Ask window. No real service address appears in any file of the repository; an example host is written `llm.example.com`.
