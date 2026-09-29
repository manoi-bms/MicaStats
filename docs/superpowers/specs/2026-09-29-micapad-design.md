# MicaPad: a built-in notepad that never loses text and never asks to save

**Date:** 2026-09-29
**Status:** approved; implementation plan in `docs/superpowers/plans/2026-09-29-micapad.md`
**Phase:** 1 of 2. Phase 1 is the autosave core described here. Phase 2 (a later spec) brings
editing parity with [notepad4](https://github.com/zufuliu/notepad4); its backlog is listed under
*Out of scope*.

## Problem

The user keeps many notepad2 windows open with unsaved text. Every Windows restart becomes a
chore: each window raises its own "Save changes?" dialog, they must be confirmed one by one, and
anything missed is lost. The text never needed a file name in the first place — it only needed
to survive.

## The constraint this design keeps

**The user's text is on disk within seconds of being typed, always, without being asked.** Every
decision below follows from that: if the text is already saved, shutdown has nothing to ask
about, a crash has nothing to lose, and a file name is optional rather than a precondition.

Two rules carried over from the rest of MicaStats:

- **Never block the UI thread on I/O.** Autosave snapshots the text on the UI thread (cheap) and
  writes it on a background writer. The settings freeze fixed in `83cf15f` is the reminder why.
- **Plain, recoverable files.** Notes are ordinary UTF-8 text files the user can open in Explorer
  if MicaStats itself is broken, written with the same temp-then-replace pattern as
  `ConfigService`.

## Decisions

1. **Phased.** Phase 1 fixes the pain point and ships basic editing (find/replace, go to line,
   word wrap, zoom, line numbers). notepad4 parity is phase 2.
2. **Tabs in one window.** One MicaPad window, a tab per note. Closing a tab never prompts; the
   note moves to a recoverable *Closed notes* list.
3. **Real files use shadow mode.** Editing a file opened from disk autosaves to MicaPad's own
   store. The real file changes only on Ctrl+S.
4. **History = snapshots on pause** with tiered retention (all for 24 h, hourly for 7 days, daily
   for 90 days).
5. **Entry points:** overlay menu, global hotkey, `--pad` command line with Explorer "Open with",
   and reopen at login.
6. **Editor engine: AvalonEdit** (NuGet package `AvalonEdit` 6.3.1.120, namespace
   `ICSharpCode.AvalonEdit`, MIT). WPF-native, virtualized
   rendering, per-document undo stack. Chosen over ScintillaNET (notepad4's engine) because
   hosting Scintilla needs `WindowsFormsHost`, native DLLs per architecture and manual theming
   — costs paid in phase 1 for lexer value that only arrives in phase 2. notepad4's C++ source
   is not ported; its features are reimplemented on AvalonEdit.

## 1. Storage

Root: `%APPDATA%\MicaStats\MicaPad\`, beside `config.json`.

```
MicaPad\
  session.json                      window state and open tabs
  notes\{id}\meta.json              one note's metadata
  notes\{id}\current.txt            the note's latest text, UTF-8 without BOM
  notes\{id}\history\{stamp}.txt    snapshots, full text, UTF-8 without BOM
```

- `{id}` is a GUID in `N` format.
- `{stamp}` is `yyyyMMdd-HHmmss-fff` in local time, formatted with `CultureInfo.InvariantCulture`
  (under the `th-TH` culture a culture-sensitive format would stamp the Buddhist year 2569).
- **`session.json`:** `windowOpen`, window placement (left, top, width, height, maximized),
  `alwaysOnTop`, `zoom`, ordered list of open note ids, active note id, and per note the caret
  offset and vertical scroll offset.
- **`meta.json`:** `title`, `titleIsCustom`, `sourcePath` (null for a scratch note), `created`,
  `modified`, `closedAt` (null while open), `hasUnsavedEdits`, and for file-backed notes
  `encoding` (name plus BOM flag), `lineEnding`, and the source stamp (`lastWriteTimeUtc`,
  `length`) taken at the last load or save.

**Atomic writes, in two phases.** Every store file is written to `name.tmp` and flushed with
`FileStream.Flush(true)`; the complete temp file is then renamed to `name.ready`; finally
`name.ready` is moved over the target (`File.Replace` when the target exists, `File.Move`
otherwise). A crash while writing leaves only a partial `.tmp`, which is never read. A crash
after the rename leaves a `.ready`, which is always complete.

**Loading one note**, most trusted source first:

1. `current.txt.ready` — a complete write whose final replace was interrupted.
2. `current.txt`.
3. The newest file in `history\`.

A corrupt or missing `meta.json` is rebuilt from what exists (title derived from the text,
`created`/`modified` from file times). A corrupt or missing `session.json` is rebuilt from the
notes whose `closedAt` is null, ordered by `modified`.

**Which text a note starts with on restore or reopen.**

- Scratch note: `current.txt` (via the order above).
- File-backed note with `hasUnsavedEdits = true`: `current.txt`, then the disk-change check (§3).
- File-backed note with `hasUnsavedEdits = false`: read fresh from `sourcePath`. `current.txt`
  is authoritative only while `hasUnsavedEdits` is true, and is not written for a file-backed
  note until its first edit.
  - When the file cannot be read (an offline share, a lock), MicaPad's own copy (via the order
    above) is shown instead and its source stamp is marked *unverified*. That copy may be older
    than the file, or empty, so the mark never matches a real stamp: the next disk-change check
    reloads the file (or asks, once the note has edits), and Ctrl+S asks before writing.
- A note whose text cannot be read at all is not opened, so its next autosave cannot overwrite
  that text. On restore it is skipped and stays in the session, to return at the next launch;
  on reopen it stays in the closed list.

## 2. Autosave and shutdown

**Timing.** A note is saved 1 s after its last change; during continuous typing a save is forced
at most 5 s after the first unsaved change. Immediate flush on window deactivation, tab switch,
tab close, Ctrl+S and application exit. Worst case on power loss: about 5 s of typing.

**Threading.** On the UI thread, a save only reads the document's text. `AutosaveWriter` — a
single background writer — performs the writes in order, with a newer pending save for the same
note replacing an older one (last write wins). Every save carries a per-note version number and
the store ignores a write older than one it has already made, so a late background write can
never overwrite a newer synchronous one.

**Session end.** `Application.SessionEnding` (raised by `WM_QUERYENDSESSION`) flushes every
dirty note synchronously and never sets `Cancel`: it waits up to 2 s for the queue to drain,
then writes anything still dirty directly on the calling thread. `session.json` is written last.
No dialog is ever shown. `App.OnExit` performs the same flush.

**After reboot.** MicaStats already starts at login through its Run key (`--startup`). If
`session.json` says `windowOpen` and `PadReopenAtLogin` is on, MicaPad reopens with the same
tabs, active tab, caret, scroll, zoom and placement. A placement on a monitor that no longer
exists falls back to the primary work area. With *Launch on startup* off nothing is lost; the
tabs return the next time MicaStats starts.

**Write failures** (disk full, antivirus lock, read-only folder): retry after 1 s, 2 s, 5 s, then
every 10 s. The status bar shows *Not saved — retrying* in the alert red; each failure is logged
with `DiagnosticsLog.Error("pad", …)`. The text stays in memory and the UI never blocks.

## 3. Real files (shadow mode)

**Opening a file.**

- If any note, open or closed, has the same `sourcePath` (full path, case-insensitive), that
  note is opened (reopened if closed) instead of creating a new one, so its history continues.
  This includes a note skipped on restore because its text could not be read.
  - One file never ends up in two notes. If that note's text cannot be read right now (a closed
    note with unsaved edits, or a note skipped on restore), the file is not opened and an info
    bar says so, rather than a fresh note being created beside it.
  - A closed note whose file another tab now holds (a Save As onto that file while the note was
    closed) reopens as a note of its own: a scratch note under the file's name, keeping its
    text and history.
- Files over 50 MB are refused with a message. Files whose first 8 KB contain a NUL byte and no
  UTF-16 BOM are refused as binary.
- **Encoding detection:** BOM first (UTF-8, UTF-16 LE, UTF-16 BE). Without a BOM: strict UTF-8
  decode succeeds → UTF-8 without BOM; otherwise the system ANSI code page (cp874 on a Thai
  system). This requires `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` once at
  startup; .NET 8 does not include legacy code pages by default.
- **Line ending detection:** the most frequent of CRLF, LF, CR; a file with no line break uses
  CRLF.

**Editing.** Edits autosave to `current.txt` and set `hasUnsavedEdits`; the tab shows `•`. The
real file is untouched. Line endings already in the text are never normalized. Newly typed or
pasted line breaks follow AvalonEdit's rule: they copy the ending of the adjacent line, which is
the file's own ending in every file that has at least one line break.

**Ctrl+S on a file-backed note** writes the document text to `sourcePath` with the note's
encoding, BOM and line ending setting, atomically: a temp file in the same folder, then
`File.Replace` (Win32 `ReplaceFile`, which keeps the target's ACLs, attributes and creation
time). Then `hasUnsavedEdits = false` and the source stamp is updated.

- If the encoding cannot represent a character in the text (checked with an exception-fallback
  encoder before writing), an info bar offers `[Save as UTF-8] [Cancel]`.
- If the file's stamp no longer matches the source stamp (another program changed it, or the
  note shows an *unverified* copy, §1), nothing is written and an info bar offers
  `[Overwrite] [Reload from disk]`. The check runs on every Ctrl+S, because the check on
  activation misses a change made while MicaPad kept the focus. *Overwrite* first keeps the
  file's outside version in History, so it can still be restored. When that version cannot be
  kept (it cannot be read, is not text, or is too large for History), nothing is written and
  the info bar says why and offers `[Overwrite anyway] [Reload from disk]`.
- If the write fails (access denied, path gone), an info bar shows the reason with
  `[Save As…]`. The shadow copy keeps the edits.

**Ctrl+S on a scratch note** opens Save As (default UTF-8 without BOM, CRLF). The note then
becomes file-backed and keeps its id and history. Save As onto another file asks nothing more
(the Save dialog confirmed replacing it); Save As onto the note's own file is checked as Ctrl+S
is.

**Status bar encoding and line ending** are clickable. Encoding: UTF-8, UTF-8 with BOM, UTF-16
LE, UTF-16 BE, ANSI (system code page). Line ending: CRLF, LF. Changing the line ending converts
the whole document as one undoable edit. Either change marks the note as having unsaved edits.

**Disk-change check**, run for file-backed notes on window activation and on restore, comparing
the source stamp:

| Situation | Action |
|---|---|
| Source changed, note has no unsaved edits | Reload silently. On activation the text in memory is snapshotted first; on restore there is nothing to snapshot, because the note loads straight from the file. |
| Source changed, note has unsaved edits | Info bar: `[Reload from disk] [Keep mine]`. Reload snapshots the user's version first. *Keep mine* updates the stamp so the bar does not return until the next outside change. |
| Source deleted | Info bar: `[Save As…] [Keep as note]`. *Keep as note* clears `sourcePath`, making it a scratch note. |

A note showing an *unverified* copy (§1) counts as changed: it reloads silently while it has no
edits of its own, and once it has, the info bar says the file could not be read when the tab
was restored, so the tab may not match the file, rather than that the file changed.

## 4. History and closed notes

**Snapshot triggers.** A snapshot is written only if the text's hash differs from the last
snapshot's, and one of these holds:

- The user has paused typing for at least 3 s and at least 60 s have passed since the last
  snapshot (or since the note was created or opened, if it has none).
- The tab is closing, the application is exiting, or the session is ending.
- Ctrl+S is saving the note to its file.
- A replacing operation is about to run: Reload from disk, Restore version, Replace All,
  line-ending conversion.

Notes whose text exceeds 10 MB still autosave but skip snapshots; the history pane says so.

**Retention.** `HistoryPolicy.SelectToPrune(timestamps, now)` is a pure function:

| Age | Kept |
|---|---|
| under 24 h | every snapshot |
| 1 to 7 days | the newest snapshot in each clock hour |
| 7 days to `PadHistoryDays` (default 90) | the newest snapshot in each calendar day |
| older | none |

The note's newest snapshot is always kept regardless of age. Pruning runs on a background thread
at startup and then every 24 h.

**History pane** (Ctrl+Shift+H or the status-bar History button) opens on the right of the
editor.

- Versions are grouped as Today, Yesterday, then by date. Dates use a fixed Gregorian format
  (`29 Sep 2026`, invariant culture).
- Each row shows the time, the size, and the size change from the previous version.
- Clicking a row shows that version read-only in the editor with a banner:
  `Viewing 14:30 — [Restore] [Copy all] [Back]`.
- **Restore** snapshots the current text, then replaces the document with the old version as a
  single undoable edit.

**Closing a tab** (Ctrl+W, middle-click, ×) never prompts. The note is flushed, snapshotted,
given `closedAt`, and removed from the session. A scratch note that has never contained text is
deleted instead. A file-backed note with unsaved edits keeps them; reopening restores them.

**Closed notes.** Ctrl+Shift+T reopens the most recently closed note. The `▾` button at the end
of the tab strip lists closed notes (title — which for a scratch note is its first line — closed
time, and the file path for a file-backed note) with a search box and
**Reopen** / **Delete** actions.

**Deletion always goes to the Recycle Bin.** Both *Delete* and the automatic purge of notes
closed longer than `PadHistoryDays` move the note's folder to the Recycle Bin through
`SHFileOperation` with `FO_DELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT |
FOF_NOERRORUI`. If recycling fails, the folder is left in place and the failure is logged;
nothing is ever deleted permanently without the Recycle Bin.

## 5. Window and editing

**Window** (`Pad/MicaPadWindow.xaml`). Styled like the Processes window: background `#0E0E13`,
ModernWpfUI dark theme. Title `MicaPad — <note title>`. One MicaPad window per process.

- **Taskbar identity:** the window gets its own AppUserModelID,
  `Kil0bit.SystemMonitor.MicaPad`, through `SHGetPropertyStoreForWindow`, with relaunch command
  `MicaStats.exe --pad`, display name *MicaPad* and a new `Assets/micapad.ico`. It groups
  separately from MicaStats and can be pinned.
- **Layout, top to bottom:** tab strip (`+` new, `▾` closed notes, scroll arrows on overflow;
  tab width 80–200 px), info bar, find/replace bar, the editor with the history pane on its
  right, status bar.
- **Status bar:** `Ln 12, Col 5 | 1,284 chars | UTF-8 | CRLF | Saved 2s ago`. The save state
  reads *Saving…*, *Saved Ns ago*, or *Not saved — retrying*.
- **`≡` menu:** New, Open…, Save, Save As…, Close tab, Reopen closed tab, History, Word wrap,
  Line numbers, Always on top, Font…, Open notes folder, Settings.
- **Closing the window** (×) hides it and sets `windowOpen = false`. Tabs stay in the session
  and are shown again on the next open.

**One editor, many documents.** The window holds a single AvalonEdit `TextEditor`; switching
tabs swaps its `Document`. Each `TextDocument` owns its `UndoStack`, so undo history survives tab
switches. Caret and scroll are saved per tab on switch and restored on return.

**Titles.** A scratch note's title is its first non-empty line, trimmed and cut to 30
characters, or `Untitled N` if empty, until the user renames it (double-click the tab), which
sets `titleIsCustom`. A file-backed note's title is its file name.

**Keys.**

| Keys | Action |
|---|---|
| Ctrl+N / Ctrl+O | New note / Open file |
| Ctrl+S / Ctrl+Shift+S | Save to file / Save As |
| Ctrl+W / Ctrl+Shift+T | Close tab / Reopen closed tab |
| Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1…9 | Switch tab |
| Ctrl+F / Ctrl+H / F3 / Shift+F3 | Find / Replace / Next / Previous |
| Ctrl+G | Go to line |
| Alt+Z | Toggle word wrap |
| Ctrl+wheel, Ctrl+=, Ctrl+-, Ctrl+0 | Zoom in and out, reset zoom |
| Ctrl+Shift+H | History pane |

Ctrl+W closes the tab (the tab-editor convention); in notepad2 it toggles word wrap.

**Find/replace bar** (`Pad/FindReplaceBar.xaml`), custom because AvalonEdit's `SearchPanel`
cannot replace. Fields: find, replace. Toggles: Match case, Whole word, Regex (.NET syntax).
Shows `3 of 17` and highlights every match with a background renderer. Replace All takes a
snapshot first and applies as one undoable edit. Matching is done by the pure
`FindReplaceEngine` over a string; an invalid pattern shows an inline error.

**Go to line** is a small inline box, not a dialog.

## 6. Entry points

- **Overlay menu:** new item `MicaPad\t<hotkey>` (id 1012) after *Processes*
  (`OverlayWindow.cs`, near line 1759). The shortcut text shows the current configured hotkey.
- **Global hotkey:** `PadHotkey`, default `Ctrl+Alt+N`. It shows MicaPad, restores it if
  minimized, and brings it to the foreground. `CaptureHotkeys` changes from mapping ids to
  `CaptureMode` to mapping ids to an `Action`, so the MicaPad key shares its hidden message
  window. The capture keys stay gated by `CaptureHotkeysEnabled`; the MicaPad key registers
  whenever `PadHotkey` is non-empty and valid. `App`'s config listener re-applies on
  `PadHotkey` as well as `CaptureHotkey*`. A combination already taken by another program is
  logged, as the capture keys are today.
- **Command line:** `PadArguments.TryParse(args, out string? path)` returns true when `--pad`
  is present and accepts `--pad` and `--pad "<path>"`, alone or with `--startup`.
  - Another instance already running: send `WM_COPYDATA` to `Kil0bitOverlayWndClass_Main` with
    `dwData = 0x4D504144` ("MPAD") and the path as UTF-16 (empty means "just open"), then exit.
    The overlay's `WndProc` accepts only that `dwData`, ignores payloads over 32,768 characters,
    and dispatches to `App.OpenPad(path)`, which opens the file through the normal open path.
    The app manifest is `asInvoker`, so sender and receiver normally share an integrity level;
    if MicaStats was started elevated, the overlay calls
    `ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW)` so that an unelevated
    Explorer *Open with* still reaches it.
  - The running instance takes its single-instance mutex before its window exists, so a launch
    right after sign-in may find no window yet. A second launch waits up to five seconds for
    the window of a running instance that is still starting; a `--pad` request that still finds
    none is logged.
  - First instance: normal startup, then `App.OpenPad(path)`. The automatic Settings window is
    skipped when `--pad` is present.
  - Without `--pad`, a second instance behaves as today (shows Settings).
- **Installer** (`installer.iss`; the release pipeline is `dotnet publish` then Inno Setup):
  - Start-menu shortcut *MicaPad* → `MicaStats.exe --pad`, icon `micapad.ico`.
  - Registry under `HKA\Software\Classes\Applications\MicaStats.exe`: `FriendlyAppName` =
    *MicaPad*, `shell\open\command` = `"{app}\MicaStats.exe" --pad "%1"`, and `SupportedTypes`
    for `.txt .log .ini .md .json .xml .csv .cfg .conf .yaml .yml`. This lists MicaPad in
    Explorer's *Open with* without making it the default application. Keys are removed on
    uninstall (`uninsdeletekey`).

## 7. Settings

A new *MicaPad* section in the Settings navigation (`Tag="MicaPad"`), backed by new
`AppConfig` properties:

| Property | Default | Meaning |
|---|---|---|
| `PadHotkey` | `Ctrl+Alt+N` | Global shortcut; empty disables it |
| `PadFontFamily` | `Cascadia Mono` (falls back to `Consolas`) | Editor font |
| `PadFontSize` | `14` (DIPs) | Editor font size |
| `PadWordWrap` | `true` | Word wrap, also toggled by Alt+Z |
| `PadShowLineNumbers` | `true` | Line number margin |
| `PadHistoryDays` | `90` | History retention and closed-note purge age |
| `PadReopenAtLogin` | `true` | Reopen MicaPad after login if it was open |

The section also has an *Open notes folder* button.

## Architecture

New code under `Services/Pad/` holds no WPF types and is unit-tested:

| Unit | Responsibility |
|---|---|
| `NoteMeta`, `SessionState` | Serializable records for `meta.json` and `session.json` |
| `AtomicFile` | Two-phase atomic write for store files; same-folder temp-and-replace for source files |
| `NoteStore` | Folder layout, load order and recovery, snapshots, pruning, closed-note purge |
| `AutosaveScheduler` | Debounce (1 s / 5 s) decision, pure |
| `AutosaveWriter` | Single background writer, FIFO, last-write-wins per key, retry with backoff, `FlushAll(timeout)` (the spec's "AutosaveQueue") |
| `PadWorkspace` | The open notes and every rule above that joins them: autosave, snapshots, close and reopen, session, file operations. No WPF types, so it is tested directly |
| `HistoryPolicy` | Snapshot trigger decision and `SelectToPrune` |
| `HistoryRows` | History pane grouping and text, pure |
| `PadIpc` | `WM_COPYDATA` send and validated read |
| `TextFileCodec` | Encoding and line-ending detection, binary and size guards, lossless decode/encode, lossy-character check |
| `FindReplaceEngine` | Find all, next and previous, replace, replace all; case, whole word, regex |
| `NoteTitle` | Title derivation |
| `DiskChangePolicy` | Decision table from §3 |
| `PadArguments` | `--pad` parsing |
| `RecycleBin` | `SHFileOperation` wrapper |

UI under `Pad/`: `MicaPadWindow`, `FindReplaceBar`, `HistoryPane`, the closed-notes popup.
`Helpers/TaskbarIdentity.cs` sets the per-window AppUserModelID and relaunch properties.

Changes to existing code: `App.xaml.cs` (`OpenPad`, `SessionEnding`, `--pad` handling,
code-page provider registration, flush in `OnExit`), `OverlayWindow.cs` (menu item,
`WM_COPYDATA`), `CaptureHotkeys.cs` (id → `Action`), `Models/SystemMetrics.cs` (`AppConfig.Pad*`),
`SettingsWindow` (MicaPad section), `installer.iss`, `Kil0bitSystemMonitor.csproj` (AvalonEdit
package, icon asset).

## Error handling

- Every autosave, snapshot, prune and purge failure is caught, logged under the `pad` category,
  and never surfaces as a dialog. Autosave failures retry as in §2 and show in the status bar.
- Explicit user actions that fail (Ctrl+S, Open, Save As) show an info bar with the reason.
- A note that cannot be loaded at all is skipped with a log entry; its folder is left untouched
  for manual recovery, and the rest of the session loads.
- Unhandled exceptions in MicaPad code reach the existing dispatcher handler, which logs them.

## Testing

**Automated** (xUnit, `tests/Kil0bitSystemMonitor.Tests/Pad*Tests.cs`, one file per unit, temp
folders, an injected `Func<DateTime>` clock; window tests build the real window on one shared
STA thread without showing it):

- `NoteStore`: save/load round trip; a leftover `.ready` wins over its target and a partial
  `.tmp` is ignored; corrupt `meta.json`
  rebuilt; missing `current.txt` falls back to the newest snapshot; corrupt `session.json`
  rebuilt from open notes ordered by `modified`; closed notes excluded from the session;
  reopening a path finds the existing note.
- `HistoryPolicy`: each retention tier on a fixed clock; newest snapshot kept regardless of age;
  pause trigger fires only with ≥ 3 s idle, ≥ 60 s elapsed and a changed hash; forced triggers.
- Snapshot names under `th-TH` culture carry the Gregorian year.
- `TextFileCodec`: detects UTF-8 with BOM, UTF-16 LE/BE, UTF-8 without BOM, cp874 bytes; CRLF,
  LF, CR and mixed; binary and size guards; decode then encode reproduces the original bytes
  exactly for each supported encoding; lossy-character check.
- `FindReplaceEngine`: match case, whole word, regex, replace all with capture groups; an
  invalid pattern returns an error rather than throwing; a zero-length match does not loop.
- `AutosaveScheduler`: 1 s debounce and 5 s maximum latency on a fake clock.
- `AutosaveWriter`: last write wins per key; FIFO order; failed work is retried; `FlushAll`
  waits for pending work.
- `PadWorkspace`: autosave, pause and forced snapshots, close and reopen, session restore,
  session-end flush, and every file operation, against a temp folder and a fake clock.
- `PadIpc`: wrong tag, oversized and odd-length payloads are rejected.
- `PadArguments`: `--pad`, `--pad "C:\a b\x.txt"`, combined with `--startup`.
- `HotkeyParser` parses the default `Ctrl+Alt+N`.
- `NoteTitle` and `DiskChangePolicy` decision tables.

**Manual**, on the user's machine:

1. Open three tabs — a scratch note, a file with unsaved edits, a Thai cp874 file — and restart
   Windows. No MicaPad prompt appears; all three return with caret positions; the file on disk
   is unchanged.
2. End MicaStats in Task Manager while typing, relaunch: at most ~5 s of text is lost.
3. Change a file in another editor while MicaPad holds unsaved edits: the info bar appears;
   after Reload the replaced version is in history.
4. Restore a history version, press Ctrl+Z: the restore is undone.
5. Close a tab, press Ctrl+Shift+T: it returns. Delete a closed note: it is in the Recycle Bin.
6. Explorer *Open with → MicaPad*, with MicaStats running and not running.
7. Ctrl+Alt+N from another application. With the combination taken elsewhere, the conflict is
   logged and nothing crashes.
8. A 50 MB file is refused; typing in a 10 MB note shows no UI stall over 100 ms.

**Docs:** MicaPad section in `README.md` and `GUIDE.md`, AvalonEdit in third-party notices,
ROADMAP entry. Version bump to 1.12.0 at release.

## Out of scope (phase 2: notepad4 parity)

- Syntax highlighting (AvalonEdit's built-in definitions plus `.xshd` files) and code folding
- Bookmarks; mark all occurrences with a count
- Auto-closing brackets and quotes; auto-completion
- Line operations: duplicate, move, sort, trim, join
- Base64 encode/decode, number base conversion, GUID and timestamp insertion, expression
  evaluation
- Clickable URLs, copy as RTF, full screen
- Line diff view in the history pane
- Drag-to-reorder tabs; more than one MicaPad window
