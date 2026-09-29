# MicaPad — follow-ups after the first release

These are the review findings that were deliberately left out of the MicaPad branch
(`feat/micapad`). None of them loses text on its own, and none blocks the 1.12.0 release. They
are grouped by area, with the most useful first in each group. Items that were fixed before
merge are not listed.

## Saving and files

- **Stale text for a clean file note restored while its file is unreachable.** Autosave does not
  refresh `current.txt` for a clean file-backed note. If the file cannot be read at launch (an
  offline share), the note shows the older `current.txt` as a clean copy. The History
  "Saved to file" snapshot holds the right text. Refresh `current.txt` on Ctrl+S, or restore
  from the newest SavedToFile snapshot.
- **Two notes for one file.** Opening a file whose closed note cannot be read opens a second,
  clean note for the same file. `SaveAs` also checks only open tabs, so a closed note can share
  a path.
- **Text written before meta.** A crash in between can leave the last five seconds of edits to
  a file note without the "unsaved" flag.
- `DetachFromFile` pins the title, so a later Save As keeps the old name.
- An invalid path is reported as `NotFound`, not `Failed`.
- A lossy decode is not shown on restore at startup, only on open and reload.
- A no-op line-ending change is neither marked unsaved nor persisted.
- File operations on an offline share run on the UI thread and can stall it.

## Restore and closed notes

- A note skipped because its text was unreadable comes back at the next launch without its
  caret and scroll position (`PrepareSession` drops its view state).
- The window ignores a null from Reopen, so clicking Reopen on a note whose text is locked does
  nothing visible. Show a message.
- A missing note can stay at the top of the closed list until it is deleted.
- `ClosedNotes` reads every `meta.json` on the UI thread. Reopen and DeleteClosed can block the
  UI for up to 2 s while the writer is behind.
- A note marked open but missing from the session list is orphaned: never shown and never
  purged.

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

- A `--pad` request is lost, without a log line, if the running instance has no overlay window
  yet.
- A MicaPad hotkey that equals a capture hotkey is logged as taken by another application, and
  the menu shows the hotkey even after registration failed.
- Window placement is not per-monitor aware.
- A silent reload resets caret and scroll. Dismissing the info bar with X asks again on the
  next activation.
- Deferred scroll restore is not guarded against a tab change. The + button scrolls away with
  many tabs. The Maximized state can be lost when the window is minimized. Shortcuts fire while
  a tab is being renamed.
- `TaskbarIdentity` ignores HRESULTs.
- Copy all gives no feedback when the clipboard is busy.
- Font changes in Settings save on every LostFocus, with no hint when the font does not exist.

## Tests worth adding

- Codec: a NUL at the 8191/8192 boundary; a BOM-only file.
- History policy: exact tier boundaries at 24 h, 7 days and the retention limit.
- Store: concurrency between the UI and writer threads; crash leftovers (`.ready` without a
  target, meta/session `.ready`, the `WriteSource` copy fallback).
- Writer: replace-keeps-position, other keys flowing during a backoff, `Dispose`.
- Window: hide-on-close, flush on deactivate and tab switch, Ctrl+Tab, Ctrl+Shift+T, the
  refused-delete path, invalid regex, wrap-around, single Replace.
- IPC: `SendOpen` end to end; `cbData` at or above 2^31.
