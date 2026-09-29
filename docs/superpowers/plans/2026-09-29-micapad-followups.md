# MicaPad — follow-ups after the first release

These are the review findings that were deliberately left out of the MicaPad branch
(`feat/micapad`). None of them loses text on its own, and none blocks the 1.12.0 release. They
are grouped by area, with the most useful first in each group. Items that were fixed before
merge are not listed.

## Saving and files

- **Text written before meta.** A crash in between can leave the last five seconds of edits to
  a file note without the "unsaved" flag.
- `DetachFromFile` pins the title, so a later Save As keeps the old name.
- An invalid path is reported as `NotFound`, not `Failed`.
- A lossy decode is not shown on restore at startup, only on open and reload.
- A no-op line-ending change is neither marked unsaved nor persisted.
- File operations on an offline share run on the UI thread and can stall it.
- Overwrite anyway is not tied to the version the user was shown: if the file changes again to
  another version that cannot be kept, that unseen version is dropped. Pass the stamp shown with
  the question and skip the copy only while the file still has it.
- Reloading a clean stand-in (`SourceStamp.Unverified`) snapshots the stand-in first, which can
  add an empty or stale version to History. Skip that snapshot when the note has no edits.
- Keep mine on a stand-in, then Ctrl+S, replaces the file without a History copy of it.
  Snapshot the file first when the stamp is `Unverified`.
- Two messages still say "changed on disk" for a stand-in: the Overwrite anyway question and
  the "too large to keep a copy of your version" reload message. Use the stand-in wording there too.
- Save As onto the file of a note skipped at restore is allowed (`SaveAs` checks only open
  tabs, and `Restore` removes duplicates by id, not by path), so both notes come back as tabs for
  one file at the next launch. The stamp check still stops a silent overwrite.

## Restore and closed notes

- A note skipped because its text was unreadable comes back at the next launch without its
  caret and scroll position (`PrepareSession` drops its view state).
- A missing note can stay at the top of the closed list until it is deleted.
- `ClosedNotes` reads every `meta.json` on the UI thread. Reopen and DeleteClosed can block the
  UI for up to 2 s while the writer is behind.
- A note marked open but missing from the session list is orphaned: never shown and never
  purged.
- A clean closed file note detached on Reopen (its file now held by another tab) reads that
  file, so it returns with the other tab's text rather than its own (its own is in History).
  Load its newest snapshot instead.
- The reopen-failed message does not name the note; for text that can never be read the
  closed-note refusal (Try again in a moment) is a dead end until the note is deleted.
- A note skipped at restore whose text can never be read blocks opening its file for good, and
  it cannot be deleted from the UI because it is not a closed note. Offer to open the file as a
  new note after keeping what can be kept, or list skipped notes somewhere they can be removed.
- Opening the file of a clean note skipped at restore (both its file and its stored copy were
  unreadable) says it "has unsaved edits", which it does not.
- A skipped note brought back by opening its file skips the file checks the closed-note path
  runs: no undecodable-bytes warning, and a file that is now binary or over 50 MB opens as a
  stand-in instead of being refused.
- The design spec says one file never ends up in two notes (see the Save As case above) and that
  a detached closed note keeps its text (a clean one takes the other tab's text); it also puts
  the Save As onto the note's own file rule in the scratch-note paragraph. Correct the wording.

## Writer and store

- Newer work for a key inherits the backoff of failed work, up to 10 s.
- `Completed` handler exceptions are swallowed. `Enqueue` after `Dispose` drops work silently,
  and `FlushAll` after `Dispose` waits the full timeout.
- A snapshot left as `.txt.ready` by a crash is never promoted or listed.
- `ListSnapshots` and `ReadSnapshot` run outside the note lock. The per-note lock table is
  never trimmed.
- Session ids are not validated before `Path.Combine`.
- `TryRecycle` discards the shell result code, so refusals cannot be diagnosed. The
  `SHFILEOPSTRUCT` layout is x64-only (x86 fails closed).
- History keeps growing for file notes that are opened but never edited.
- Maintenance at startup is not awaited at exit.

## Find and replace

- In regex mode `$` does not match before `\r` in CRLF text. Rewriting `$` alone does not help,
  because patterns like `\s+$` consume the `\r` themselves; a proper fix needs a CRLF-aware
  match mode.
- The 2 s regex timeout applies per match call, so FindAll can run longer in total, and
  FindPrevious scans every match. Regex search runs on the UI thread.
- Replace All rewrites the whole document (caret and scroll reset) and snapshots even when
  nothing changes.
- Highlights lag edits by up to 300 ms, and the match count does not follow the caret.
- Escape in the go-to-line box closes the find bar first when it is open.

## Window and integration

- A MicaPad hotkey that equals a capture hotkey is logged as taken by another application, and
  the menu shows the hotkey even after registration failed.
- Window placement is not per-monitor aware.
- A silent reload resets caret and scroll. Dismissing the info bar with X asks again on the
  next activation.
- Deferred scroll restore is not guarded against a tab change. The + button scrolls away with
  many tabs. The Maximized state can be lost when the window is minimized. Shortcuts fire while
  a tab is being renamed.
- `TaskbarIdentity` ignores HRESULTs.
- A plain second launch (Settings) also waits up to 5 s when no window appears and is then
  dropped without a log line; only `--pad` is logged.
- Copy all gives no feedback when the clipboard is busy.
- Font changes in Settings save on every LostFocus, with no hint when the font does not exist.

## Tests worth adding

- Codec: a NUL at the 8191/8192 boundary; a BOM-only file.
- History policy: exact tier boundaries at 24 h, 7 days and the retention limit.
- Store: concurrency between the UI and writer threads; crash leftovers (`.ready` without a
  target, meta/session `.ready`, the `WriteSource` copy fallback).
- Writer: replace-keeps-position, other keys flowing during a backoff, `Dispose`.
- Window: hide-on-close, flush on deactivate and tab switch, Ctrl+Tab, the refused-delete
  path, invalid regex, wrap-around, single Replace.
- IPC: `SendOpen` end to end; `cbData` at or above 2^31.
