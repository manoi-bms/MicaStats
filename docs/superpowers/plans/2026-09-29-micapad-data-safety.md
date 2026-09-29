# MicaPad Data-Safety Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five data-safety follow-ups left after the MicaPad review, so that no path writes over text the user has not seen, opens one file in two notes, or fails without saying so.

**Architecture:** All rules live in the pure `Services/Pad` layer (`PadWorkspace`, `PadWorkspace.Files`, `DiskChangePolicy`, `PadIpc`) and are unit-tested there. The window (`Pad/MicaPadWindow.xaml.cs`) only turns new statuses into info-bar messages, tested through the existing never-shown window harness. `App.xaml.cs` gets a short wait for a running instance that is still starting.

**Tech Stack:** WPF on .NET 8 (`net8.0-windows`), AvalonEdit 6.3.1.120, xUnit.

**Source of the items:** `docs/superpowers/plans/2026-09-29-micapad-followups.md` (each task removes its item from that file).

## Global Constraints

- **Build and test only with the user-local SDK:** `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. There is no system .NET SDK on this machine; bare `dotnet` fails with "No .NET SDKs were found". Set `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Test command shape: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`.
- **No new package.**
- **Nullable reference types are enabled** project-wide. Do not add `#nullable disable`.
- **Never format a number or date with the ambient culture.** Use `CultureInfo.InvariantCulture`. The owner's Thai locale stamps Buddhist-era years (2569) through defaults.
- **`UseWindowsForms` is on**, so `System.Windows.Forms` and `System.Drawing` are implicit global usings in the app *and* the test project. In any WPF file, alias names that exist in both. If the compiler reports CS0104 (ambiguous reference), add a `using X = System.Windows...X;` alias; never remove the WinForms reference.
- **XML doc comments on every public type and member**, house style: say *why* when the reason is not obvious.
- **Tests never touch `%APPDATA%\MicaStats`.** Every store in a test is rooted in a temp folder (`PadTestEnv`), with no-op `warn`/`error` callbacks.
- **Do not launch MicaStats.** Verification is build plus test suite.
- **Every commit message ends with this trailer**, on its own line after a blank line: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commit with `git commit -F -` and a heredoc, and keep apostrophes out of commit messages.** Never `--amend`. Stage files by explicit path, never `git add -A` or `git add .`.
- Commit messages use Conventional Commits: `fix(pad): …`, `docs(pad): …`.
- The whole suite (800 tests at the start of this plan) must pass at the end of every task.

## File structure

| File | Change | Task |
| --- | --- | --- |
| `Services/Pad/PadWorkspace.Files.cs` | `OutsideVersionNotKept`, `overwriteWithoutCopy`, `TryKeepOutsideVersion`; `ClosedNoteUnreadable` in `OpenFile` | 1, 3 |
| `Services/Pad/DiskChangePolicy.cs` | `SourceStamp.Unverified` | 2 |
| `Services/Pad/PadWorkspace.cs` | `TryLoadInitialText` marks a stand-in copy; `OpenFileStatus.ClosedNoteUnreadable`; `Reopen` detaches a note whose file another tab holds | 2, 3 |
| `Services/Pad/PadIpc.cs` | `FindWithRetry` | 5 |
| `Pad/MicaPadWindow.xaml.cs` | Overwrite-anyway bar; closed-note-unreadable message; reopen-failed message | 1, 3, 4 |
| `App.xaml.cs` | second instance waits for the running window, logs a lost `--pad` | 5 |
| `tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs` | tests | 1, 2, 3 |
| `tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs` | tests | 1, 3, 4 |
| `tests/Kil0bitSystemMonitor.Tests/PadIpcTests.cs` | tests | 5 |
| `docs/superpowers/plans/2026-09-29-micapad-followups.md` | remove each fixed item | 1–5 |

---

### Task 1: Overwrite asks again when the outside version cannot be kept

Today Ctrl+S → **Overwrite** keeps the outside version as a History snapshot first, but when that version cannot be kept (the file became binary, is over 50 MB, cannot be read, or is over the snapshot cap) the save goes ahead with only a log line, and the other program's text is lost unasked. After this task the save stops with a new status, and the window asks **Overwrite anyway** / **Reload from disk**.

**Files:**
- Modify: `Services/Pad/PadWorkspace.Files.cs` (enum `SaveToFileStatus`, `SaveToSource`, `KeepOutsideVersion`)
- Modify: `Pad/MicaPadWindow.xaml.cs` (`Save`, `HandleSaveResult`)
- Modify: `docs/superpowers/plans/2026-09-29-micapad-followups.md`
- Test: `tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs`

**Interfaces:**
- Consumes: `PadWorkspace.SaveToSource(OpenNote note, bool overwriteExternalChanges = false)` (exists).
- Produces: `SaveToFileStatus.OutsideVersionNotKept` (its `SaveToFileResult.Error` holds the reason in words: `"it is larger than 50 MB"`, `"it does not look like text"`, `"it could not be read"`, or `"it is too large to keep in History"`); new signature `SaveToSource(OpenNote note, bool overwriteExternalChanges = false, bool overwriteWithoutCopy = false)`.

- [ ] **Step 1: Write the failing workspace test**

Add to `PadWorkspaceFileTests` (after `Overwrite_writes_the_note_and_keeps_the_outside_version_in_history`):

```csharp
        [Fact]
        public void Overwrite_stops_when_the_outside_version_cannot_be_kept_until_the_user_insists()
        {
            byte[] binary = { 0x4D, 0x5A, 0x00, 0x01 };
            string path = WriteFile("turned-binary.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "mine");
            File.WriteAllBytes(path, binary);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            var result = Ws.SaveToSource(note, overwriteExternalChanges: true);

            Assert.Equal(SaveToFileStatus.OutsideVersionNotKept, result.Status);
            Assert.Equal("it does not look like text", result.Error);
            Assert.Equal(binary, File.ReadAllBytes(path));
            Assert.True(note.HasUnsavedEdits);

            var forced = Ws.SaveToSource(note, overwriteExternalChanges: true, overwriteWithoutCopy: true);

            Assert.Equal(SaveToFileStatus.Saved, forced.Status);
            Assert.Equal(Encoding.UTF8.GetBytes("mine"), File.ReadAllBytes(path));
            Assert.False(note.HasUnsavedEdits);
        }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspaceFileTests"`
Expected: build error — `SaveToFileStatus` has no `OutsideVersionNotKept` and `SaveToSource` has no `overwriteWithoutCopy` parameter.

- [ ] **Step 3: Add the status**

In `Services/Pad/PadWorkspace.Files.cs`, inside `public enum SaveToFileStatus`, after the `ChangedOnDisk` member, add:

```csharp
        /// <summary>
        /// Overwrite was chosen, but the outside version cannot be kept as a version first
        /// (<see cref="SaveToFileResult.Error"/> says why); nothing was written. Saving again with
        /// <c>overwriteWithoutCopy</c> writes anyway, and that version is then gone.
        /// </summary>
        OutsideVersionNotKept,
```

- [ ] **Step 4: Stop the save when the outside version cannot be kept**

In `SaveToSource`, replace the doc comment's `<param name="overwriteExternalChanges">` block and the signature:

```csharp
        /// <param name="overwriteExternalChanges">
        /// The user chose Overwrite: write even though the file changed. The outside version is
        /// kept as a snapshot first, so it can still be restored from history.
        /// </param>
        public SaveToFileResult SaveToSource(OpenNote note, bool overwriteExternalChanges = false)
```

with:

```csharp
        /// <param name="overwriteExternalChanges">
        /// The user chose Overwrite: write even though the file changed. The outside version is
        /// kept as a snapshot first, so it can still be restored from history; when it cannot be
        /// kept, nothing is written (<see cref="SaveToFileStatus.OutsideVersionNotKept"/>).
        /// </param>
        /// <param name="overwriteWithoutCopy">
        /// The user chose Overwrite anyway after <see cref="SaveToFileStatus.OutsideVersionNotKept"/>:
        /// write even though the outside version cannot be kept.
        /// </param>
        public SaveToFileResult SaveToSource(OpenNote note, bool overwriteExternalChanges = false, bool overwriteWithoutCopy = false)
```

Then replace:

```csharp
                if (!overwriteExternalChanges) return new SaveToFileResult(SaveToFileStatus.ChangedOnDisk);
                KeepOutsideVersion(note);
            }
```

with:

```csharp
                if (!overwriteExternalChanges) return new SaveToFileResult(SaveToFileStatus.ChangedOnDisk);
                if (!TryKeepOutsideVersion(note, out string? whyNot))
                {
                    // Losing the other program's text is the user's call, never a side effect.
                    if (!overwriteWithoutCopy) return new SaveToFileResult(SaveToFileStatus.OutsideVersionNotKept, whyNot);
                    _warn("Overwrote " + meta.SourcePath + " without keeping the outside version (" + whyNot + "), as the user chose");
                }
            }
```

- [ ] **Step 5: Make the keep step report why it failed**

Replace the whole `KeepOutsideVersion` method (its doc comment included) at the end of the class:

```csharp
        /// <summary>
        /// Before Overwrite replaces a file another program changed, queues the file's current
        /// text as a snapshot of the note, so the outside version can still be restored. Recorded
        /// as the newest snapshot's hash, so the note's own text is snapshotted after it.
        /// </summary>
        private void KeepOutsideVersion(OpenNote note)
        {
            string path = note.Meta.SourcePath!;
            var outside = ReadSource(path, out var status);
            if (outside == null)
            {
                _warn("The outside version of " + path + " could not be kept before overwriting it (" + status + ")");
                return;
            }
            if (outside.Text.Length > HistoryPolicy.MaxSnapshotChars)
            {
                _warn("The outside version of " + path + " is too large to keep as a version; it was overwritten");
                return;
            }

            DateTime now = _clock();
            EnqueueSnapshot(note.Id, outside.Text, now);
            note.Meta.LastSnapshotHash = HistoryPolicy.Hash(outside.Text);
            note.Meta.LastSnapshotUtc = now;
        }
```

with:

```csharp
        /// <summary>
        /// Before Overwrite replaces a file another program changed, queues the file's current
        /// text as a snapshot of the note, so the outside version can still be restored. Recorded
        /// as the newest snapshot's hash, so the note's own text is snapshotted after it.
        /// False, with the reason in words for the info bar, when that version cannot be kept. A
        /// file that is gone by now has nothing to keep, which counts as kept.
        /// </summary>
        private bool TryKeepOutsideVersion(OpenNote note, out string? whyNot)
        {
            string path = note.Meta.SourcePath!;
            var outside = ReadSource(path, out var status);
            if (outside == null)
            {
                whyNot = status switch
                {
                    OpenFileStatus.NotFound => null,
                    OpenFileStatus.TooLarge => "it is larger than 50 MB",
                    OpenFileStatus.Binary => "it does not look like text",
                    _ => "it could not be read",
                };
                return whyNot == null;
            }
            if (outside.Text.Length > HistoryPolicy.MaxSnapshotChars)
            {
                whyNot = "it is too large to keep in History";
                return false;
            }

            DateTime now = _clock();
            EnqueueSnapshot(note.Id, outside.Text, now);
            note.Meta.LastSnapshotHash = HistoryPolicy.Hash(outside.Text);
            note.Meta.LastSnapshotUtc = now;
            whyNot = null;
            return true;
        }
```

- [ ] **Step 6: Run the workspace tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspaceFileTests"`
Expected: all pass, including `Overwrite_writes_the_note_and_keeps_the_outside_version_in_history` (unchanged behaviour when the version can be kept).

- [ ] **Step 7: Write the failing window test**

Add to `PadWindowTests` (after `Ctrl_s_after_an_outside_change_asks_and_overwrite_writes`):

```csharp
        [Fact]
        public void Overwrite_asks_again_when_the_outside_version_cannot_be_kept() => WithWindow((window, env, config) =>
        {
            byte[] binary = { 0x4D, 0x5A, 0x00, 0x01 };
            string path = env.FileOf("turned-binary.txt");
            File.WriteAllText(path, "base");
            window.OpenPath(path);
            window.Editor.Document.Insert(0, "mine ");
            File.WriteAllBytes(path, binary);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            window.HandleShortcut(Key.S, ModifierKeys.Control);
            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // Overwrite

            Assert.Equal("turned-binary.txt changed on disk, and that version cannot be kept in History: it does not look like text. Overwrite it anyway?",
                window.InfoText.Text);
            Assert.Equal("Overwrite anyway", window.InfoPrimary.Content);
            Assert.Equal("Reload from disk", window.InfoSecondary.Content);
            Assert.Equal(binary, File.ReadAllBytes(path));

            window.CheckShownNoteOnDisk();   // activation: the same question stays
            Assert.Equal("Overwrite anyway", window.InfoPrimary.Content);

            window.InfoPrimary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // Overwrite anyway

            Assert.Equal("mine base", File.ReadAllText(path));
            Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
        });
```

- [ ] **Step 8: Run it to verify it fails**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: FAIL — the info text is the generic `Could not save: …` message.

- [ ] **Step 9: Ask Overwrite anyway in the window**

In `Pad/MicaPadWindow.xaml.cs`, replace:

```csharp
        private void Save(OpenNote note, bool overwriteExternalChanges = false) =>
            HandleSaveResult(note, _workspace.SaveToSource(note, overwriteExternalChanges),
                             () => Save(note, overwriteExternalChanges));
```

with:

```csharp
        private void Save(OpenNote note, bool overwriteExternalChanges = false, bool overwriteWithoutCopy = false) =>
            HandleSaveResult(note, _workspace.SaveToSource(note, overwriteExternalChanges, overwriteWithoutCopy),
                             () => Save(note, overwriteExternalChanges, overwriteWithoutCopy));
```

In `HandleSaveResult`, after the `case SaveToFileStatus.ChangedOnDisk:` block (which ends with `_infoKind = InfoChangedOnDisk;` and `break;`), add:

```csharp
                case SaveToFileStatus.OutsideVersionNotKept:
                    ShowInfo((Path.GetFileName(note.Meta.SourcePath) ?? note.Title) +
                             " changed on disk, and that version cannot be kept in History: " + result.Error + ". Overwrite it anyway?",
                        note,
                        "Overwrite anyway", () => Save(note, overwriteExternalChanges: true, overwriteWithoutCopy: true),
                        "Reload from disk", () => Reload(note));
                    _infoKind = InfoChangedOnDisk;   // the check on activation leaves this question showing
                    break;
```

- [ ] **Step 10: Run the window tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: all pass.

- [ ] **Step 11: Remove the item from the follow-ups list**

In `docs/superpowers/plans/2026-09-29-micapad-followups.md`, delete this bullet (all four lines):

```markdown
- **Overwrite without a history copy.** Ctrl+S → Overwrite keeps the outside version as a
  History snapshot first. If that version cannot be kept (unreadable, binary, larger than 50 MB,
  or over the snapshot size cap), the save still goes ahead with only a log warning. Say so in
  the info bar, or refuse, mirroring how Reload refuses with `EditsWouldBeLost`.
```

- [ ] **Step 12: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes (800 + 2).

```bash
git add Services/Pad/PadWorkspace.Files.cs Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs docs/superpowers/plans/2026-09-29-micapad-followups.md
git commit -F - <<'EOF'
fix(pad): overwrite asks again when the outside version cannot be kept

Overwrite used to go ahead with only a log line when the outside version
was binary, too large or unreadable, losing it unasked. The save now stops
with OutsideVersionNotKept and the window offers Overwrite anyway or
Reload from disk.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: A stand-in copy of a clean file note never passes for the file

MicaPad keeps no copy of a clean file note's text: on restore it reads the file. When the file cannot be read then (an offline share, a lock), the note falls back to MicaPad's stored copy — often empty or older — but keeps the stamp recorded when the file was last read. Once the file is back unchanged, the stamps match, so nothing reloads, and Ctrl+S after typing into the stand-in writes it over the real file without asking. After this task such a note records `SourceStamp.Unverified`, which never equals a real stamp: the next check reloads the file (or asks, once there are edits), and Ctrl+S asks first.

**Files:**
- Modify: `Services/Pad/DiskChangePolicy.cs` (`SourceStamp`)
- Modify: `Services/Pad/PadWorkspace.cs` (`TryLoadInitialText`)
- Modify: `docs/superpowers/plans/2026-09-29-micapad-followups.md`
- Test: `tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs`

**Interfaces:**
- Consumes: `DiskChangePolicy.Decide`, `PadWorkspace.Restore`, `CheckDisk`, `ReloadFromDisk`, `SaveToSource` (all exist, unchanged).
- Produces: `public static readonly SourceStamp SourceStamp.Unverified` (`LastWriteTimeUtc = DateTime.MinValue`, `Length = -1`).

- [ ] **Step 1: Write the failing tests**

Add to `PadWorkspaceFileTests` (after `Restoring_a_clean_file_note_reads_the_file_fresh`):

```csharp
        [Fact]
        public void A_clean_file_note_restored_while_its_file_cannot_be_read_never_passes_for_the_file()
        {
            string path = WriteFile("offline.txt", Encoding.UTF8.GetBytes("the real text"));
            Ws.OpenFile(path);
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            OpenNote note;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                note = Assert.Single(restored.Restore());

            Assert.Equal("", note.TextProvider());   // MicaPad keeps no copy of a clean file
            Assert.Equal(DiskChangeAction.ReloadSilently, restored.CheckDisk(note));

            PadTestEnv.Type(restored, note, "typed over the stand-in");
            Assert.Equal(DiskChangeAction.AskReloadOrKeep, restored.CheckDisk(note));
            Assert.Equal(SaveToFileStatus.ChangedOnDisk, restored.SaveToSource(note).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("the real text"), File.ReadAllBytes(path));

            Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));
            var later = _env.NewWorkspace();
            var again = Assert.Single(later.Restore());
            Assert.Equal("typed over the stand-in", again.TextProvider());
            Assert.Equal(DiskChangeAction.AskReloadOrKeep, later.CheckDisk(again));   // the mark survives a restart
        }

        [Fact]
        public void Once_the_file_can_be_read_the_stand_in_is_replaced_by_the_file()
        {
            string path = WriteFile("back.txt", Encoding.UTF8.GetBytes("the real text"));
            Ws.OpenFile(path);
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            OpenNote note;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                note = Assert.Single(restored.Restore());

            Assert.Equal("the real text", restored.ReloadFromDisk(note, out var status, out _));
            Assert.Equal(OpenFileStatus.Opened, status);
            Assert.Equal(DiskChangeAction.None, restored.CheckDisk(note));
        }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspaceFileTests"`
Expected: the first test FAILS at `Assert.Equal(DiskChangeAction.ReloadSilently, …)` (actual `None`). The second may pass already; it guards the reload path.

- [ ] **Step 3: Add the Unverified stamp**

In `Services/Pad/DiskChangePolicy.cs`, inside `public readonly record struct SourceStamp`, before `Read`, add:

```csharp
        /// <summary>
        /// Recorded for a clean file note whose text came from MicaPad's own copy because the file
        /// could not be read. It never equals a real stamp, so the next check reloads the file (or
        /// asks, once the note has edits) and Ctrl+S asks before writing over a file it never saw.
        /// </summary>
        public static readonly SourceStamp Unverified = new(DateTime.MinValue, -1);
```

- [ ] **Step 4: Mark the stand-in on restore and reopen**

In `Services/Pad/PadWorkspace.cs`, in `TryLoadInitialText`, replace:

```csharp
                    text = decoded.Text;
                    return true;
                }
            }

            if (!_store.TryLoadText(meta.Id, out string? stored))
```

with:

```csharp
                    text = decoded.Text;
                    return true;
                }

                // MicaPad's copy stands in for a file it cannot read now (an offline share, a lock).
                // That copy may be older than the file, or empty, so it must never pass for it.
                meta.SourceStamp = SourceStamp.Unverified;
            }

            if (!_store.TryLoadText(meta.Id, out string? stored))
```

Also extend that method's doc comment: after the sentence ending `from the store.`, add `A clean file note read from the store is marked <see cref="SourceStamp.Unverified"/>.`

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspace"`
Expected: all pass.

- [ ] **Step 6: Remove the item from the follow-ups list**

In `docs/superpowers/plans/2026-09-29-micapad-followups.md`, delete this bullet (all five lines):

```markdown
- **Stale text for a clean file note restored while its file is unreachable.** Autosave does not
  refresh `current.txt` for a clean file-backed note. If the file cannot be read at launch (an
  offline share), the note shows the older `current.txt` as a clean copy. The History
  "Saved to file" snapshot holds the right text. Refresh `current.txt` on Ctrl+S, or restore
  from the newest SavedToFile snapshot.
```

- [ ] **Step 7: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes.

```bash
git add Services/Pad/DiskChangePolicy.cs Services/Pad/PadWorkspace.cs tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs docs/superpowers/plans/2026-09-29-micapad-followups.md
git commit -F - <<'EOF'
fix(pad): a stand-in copy of a clean file note never passes for the file

A clean file note restored while its file could not be read showed the
stored copy under the old stamp, so Ctrl+S could write it over the real
file unasked. Such a note now records SourceStamp.Unverified: the next
check reloads the file, or asks once there are edits, and Ctrl+S asks.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 3: One file, one note

Two ways can leave two notes for one file. (a) Opening a file whose closed note holds unsaved edits in text that cannot be read right now: `Reopen` returns null and `OpenFile` falls through to a fresh, clean note, while the closed note with the edits stays behind. (b) A Save As onto a file while a closed note still names it: reopening that closed note later gives two tabs that save over each other. After this task (a) is refused with a message, and (b) reopens the closed note as a note of its own, under the file's name, with its text and history.

**Files:**
- Modify: `Services/Pad/PadWorkspace.cs` (enum `OpenFileStatus`, `Reopen`)
- Modify: `Services/Pad/PadWorkspace.Files.cs` (`OpenFile`)
- Modify: `Pad/MicaPadWindow.xaml.cs` (`OpenPath`)
- Modify: `docs/superpowers/plans/2026-09-29-micapad-followups.md`
- Test: `tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs`

**Interfaces:**
- Consumes: `PadWorkspace.Reopen(string id)`, `OpenFile(string path)`, private `SamePath(string?, string)` in `PadWorkspace.Files.cs` (same partial class).
- Produces: `OpenFileStatus.ClosedNoteUnreadable`.

- [ ] **Step 1: Write the failing workspace tests**

Add to `PadWorkspaceFileTests` (after `Reopening_a_closed_file_restores_its_unsaved_edits`):

```csharp
        [Fact]
        public void Opening_a_file_whose_closed_note_cannot_be_read_is_refused_rather_than_duplicated()
        {
            string path = WriteFile("draft.txt", Encoding.UTF8.GetBytes("v1"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "v1 plus edits");
            Ws.Close(note);
            _env.Flush();

            using (new FileStream(_env.Store.CurrentPath(note.Id), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var refused = Ws.OpenFile(path);
                Assert.Equal(OpenFileStatus.ClosedNoteUnreadable, refused.Status);
                Assert.Null(refused.Note);
                Assert.Empty(Ws.Open);
            }

            var again = Ws.OpenFile(path);
            Assert.Equal(note.Id, again.Note!.Id);
            Assert.Equal("v1 plus edits", again.Note.TextProvider());
        }

        [Fact]
        public void A_closed_note_whose_file_another_tab_now_holds_reopens_as_a_note_of_its_own()
        {
            string path = WriteFile("twice.txt", Encoding.UTF8.GetBytes("base"));
            var old = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, old, "old edits");
            Ws.Close(old);
            var scratch = Ws.NewNote();
            PadTestEnv.Type(Ws, scratch, "new text");
            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveAs(scratch, path).Status);   // two notes now name the file

            var back = Ws.Reopen(old.Id)!;
            _env.Flush();

            Assert.Equal("old edits", back.TextProvider());
            Assert.False(back.Meta.IsFileBacked);
            Assert.Equal("twice.txt", back.Title);
            Assert.Equal("old edits", _env.DiskText(back));
            Assert.Same(scratch, Assert.Single(Ws.Open, n => n.Meta.IsFileBacked));
            Assert.Equal(Encoding.UTF8.GetBytes("new text"), File.ReadAllBytes(path));
        }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspaceFileTests"`
Expected: build error — `OpenFileStatus` has no `ClosedNoteUnreadable`.

- [ ] **Step 3: Add the status**

In `Services/Pad/PadWorkspace.cs`, inside `public enum OpenFileStatus`, after the `EditsWouldBeLost` member, add:

```csharp
        /// <summary>
        /// The file has a closed note with unsaved edits whose text cannot be read right now.
        /// Opening the file fresh beside it would leave two notes for one file, so nothing was opened.
        /// </summary>
        ClosedNoteUnreadable,
```

- [ ] **Step 4: Refuse instead of opening a duplicate**

In `Services/Pad/PadWorkspace.Files.cs`, in `OpenFile`, replace:

```csharp
                var reopened = Reopen(closed.Id);
                if (reopened != null) return new OpenFileResult(OpenFileStatus.Opened, reopened, lossy);
            }
```

with:

```csharp
                var reopened = Reopen(closed.Id);
                if (reopened != null) return new OpenFileResult(OpenFileStatus.Opened, reopened, lossy);
                // Its unsaved edits are in text that cannot be read right now (Reopen logged why).
                // A fresh note beside it would be a second note for this file.
                if (closed.HasUnsavedEdits) return new OpenFileResult(OpenFileStatus.ClosedNoteUnreadable, null);
            }
```

- [ ] **Step 5: Reopen a note whose file another tab holds as a note of its own**

In `Services/Pad/PadWorkspace.cs`, in `Reopen`, replace:

```csharp
            _recentlyClosed.Remove(id);
            meta.ClosedAtUtc = null;
```

with:

```csharp
            if (meta.IsFileBacked && Open.Any(n => SamePath(n.Meta.SourcePath, meta.SourcePath!)))
            {
                // Another tab holds this file now (a Save As onto it while this note was closed).
                // Two tabs saving one file would overwrite each other, so this note comes back on
                // its own, under the file's name, keeping its text and history.
                meta.TitleIsCustom = true;
                meta.SourcePath = null;
                meta.SourceStamp = null;
                meta.HasUnsavedEdits = false;
            }

            _recentlyClosed.Remove(id);
            meta.ClosedAtUtc = null;
```

Also extend `Reopen`'s doc comment: after `the note stays in the closed list.` add `A file note whose file another tab already holds comes back as a note of its own.`

- [ ] **Step 6: Run the workspace tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWorkspace"`
Expected: all pass.

- [ ] **Step 7: Write the failing window test**

Add to `PadWindowTests` (after `Opening_a_file_shows_it_and_ctrl_s_writes_it`):

```csharp
        [Fact]
        public void Opening_a_file_whose_closed_note_cannot_be_read_says_so() => WithWindow((window, env, config) =>
        {
            string path = env.FileOf("draft.txt");
            File.WriteAllText(path, "v1");
            window.OpenPath(path);
            window.Editor.Document.Insert(2, " plus edits");
            var note = env.Workspace.Active!;
            window.HandleShortcut(Key.W, ModifierKeys.Control);
            env.Flush();

            using (new FileStream(env.Store.CurrentPath(note.Id), FileMode.Open, FileAccess.Read, FileShare.None))
                window.OpenPath(path);

            Assert.Equal("draft.txt has unsaved edits in a closed note that cannot be read right now, so it was not opened. Try again in a moment.",
                window.InfoText.Text);
            Assert.DoesNotContain(env.Workspace.Open, n => n.Meta.IsFileBacked);
        });
```

- [ ] **Step 8: Run it to verify it fails**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: FAIL — the info text is `Could not open …`.

- [ ] **Step 9: Say so in the window**

In `Pad/MicaPadWindow.xaml.cs`, in `OpenPath`, before `default:`, add:

```csharp
                case OpenFileStatus.ClosedNoteUnreadable:
                    ShowInfo(name + " has unsaved edits in a closed note that cannot be read right now, so it was not opened. Try again in a moment.", null);
                    break;
```

- [ ] **Step 10: Run the window tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: all pass.

- [ ] **Step 11: Remove the item from the follow-ups list**

In `docs/superpowers/plans/2026-09-29-micapad-followups.md`, delete this bullet (all three lines):

```markdown
- **Two notes for one file.** Opening a file whose closed note cannot be read opens a second,
  clean note for the same file. `SaveAs` also checks only open tabs, so a closed note can share
  a path.
```

- [ ] **Step 12: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes.

```bash
git add Services/Pad/PadWorkspace.cs Services/Pad/PadWorkspace.Files.cs Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadWorkspaceFileTests.cs tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs docs/superpowers/plans/2026-09-29-micapad-followups.md
git commit -F - <<'EOF'
fix(pad): one file never ends up in two notes

Opening a file whose closed note cannot be read is now refused with a
message instead of opening a second, clean note beside it. A closed note
whose file another tab took over by Save As reopens as a note of its own,
keeping its text and history.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: A reopen that fails says so

Ctrl+Shift+T and the closed-notes list ignore a null from `Reopen` (text that cannot be read right now, a note gone from disk): the click does nothing visible. After this task the info bar says the note could not be reopened.

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs` (`ReopenClosed`, `OnClosedReopenClick`)
- Modify: `docs/superpowers/plans/2026-09-29-micapad-followups.md`
- Test: `tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs`

**Interfaces:**
- Consumes: `PadWorkspace.ReopenLastClosed()`, `Reopen(string id)`, `ClosedNotes()` (exist, unchanged).
- Produces: nothing new.

- [ ] **Step 1: Write the failing window test**

Add to `PadWindowTests` (after `Closing_the_last_tab_leaves_a_fresh_empty_note`):

```csharp
        [Fact]
        public void A_closed_note_that_cannot_be_reopened_says_so() => WithWindow((window, env, config) =>
        {
            window.Editor.Document.Insert(0, "keep this");
            var note = env.Workspace.Active!;
            window.HandleShortcut(Key.W, ModifierKeys.Control);
            env.Flush();

            using (new FileStream(env.Store.CurrentPath(note.Id), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.True(window.HandleShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift));

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("That note could not be reopened right now, so it was left as it is.", window.InfoText.Text);

            window.HandleShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal("keep this", window.Editor.Document.Text);
        });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: FAIL — the info bar is collapsed.

- [ ] **Step 3: Say so in the window**

In `Pad/MicaPadWindow.xaml.cs`, replace:

```csharp
        private void ReopenClosed()
        {
            var note = _workspace.ReopenLastClosed();
            if (note != null) ShowNote(note);
        }
```

with:

```csharp
        private void ReopenClosed()
        {
            var note = _workspace.ReopenLastClosed();
            if (note != null) ShowNote(note);
            else if (_workspace.ClosedNotes().Count > 0) ShowReopenFailed();   // not just an empty list
        }

        /// <summary>Reopen found the note but could not load it (its text cannot be read right now, say).</summary>
        private void ShowReopenFailed() =>
            ShowInfo("That note could not be reopened right now, so it was left as it is.", null);
```

and in `OnClosedReopenClick` replace:

```csharp
            var note = _workspace.Reopen(row.Id);
            if (note != null) ShowNote(note);
```

with:

```csharp
            var note = _workspace.Reopen(row.Id);
            if (note != null) ShowNote(note);
            else ShowReopenFailed();
```

- [ ] **Step 4: Run the window tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadWindowTests"`
Expected: all pass.

- [ ] **Step 5: Remove the item from the follow-ups list**

In `docs/superpowers/plans/2026-09-29-micapad-followups.md`, delete this bullet (both lines):

```markdown
- The window ignores a null from Reopen, so clicking Reopen on a note whose text is locked does
  nothing visible. Show a message.
```

- [ ] **Step 6: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes.

```bash
git add Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadWindowTests.cs docs/superpowers/plans/2026-09-29-micapad-followups.md
git commit -F - <<'EOF'
fix(pad): a reopen that fails says so

Ctrl+Shift+T and the closed-notes list did nothing visible when a note
could not be reopened. The info bar now says it was left as it is.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: A MicaPad launch waits for a running instance that is still starting

MicaStats takes its single-instance mutex before its overlay window exists. A second launch in that gap (a pinned MicaPad button clicked right after sign-in, while MicaStats starts at login) finds no window, drops the request, and logs nothing. After this task the second instance looks for the window for up to five seconds, and logs a `--pad` request that is still lost.

**Files:**
- Modify: `Services/Pad/PadIpc.cs`
- Modify: `App.xaml.cs` (the `if (!createdNew)` block in `OnStartup`)
- Modify: `docs/superpowers/plans/2026-09-29-micapad-followups.md`
- Test: `tests/Kil0bitSystemMonitor.Tests/PadIpcTests.cs`

**Interfaces:**
- Consumes: `FindWindow` P/Invoke in `App` (exists).
- Produces: `public static IntPtr PadIpc.FindWithRetry(Func<IntPtr> find, int attempts, TimeSpan interval, Action<TimeSpan>? sleep = null)`.

- [ ] **Step 1: Write the failing tests**

In `tests/Kil0bitSystemMonitor.Tests/PadIpcTests.cs`, add `using System.Collections.Generic;` to the usings, and add to the class:

```csharp
        [Fact]
        public void The_running_window_is_waited_for_while_it_starts()
        {
            int calls = 0;
            var waits = new List<TimeSpan>();

            IntPtr found = PadIpc.FindWithRetry(() => ++calls < 3 ? IntPtr.Zero : new IntPtr(42),
                                                20, TimeSpan.FromMilliseconds(250), waits.Add);

            Assert.Equal(new IntPtr(42), found);
            Assert.Equal(3, calls);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250) }, waits);
        }

        [Fact]
        public void A_window_that_never_appears_gives_up_after_the_last_attempt()
        {
            int calls = 0;
            var waits = new List<TimeSpan>();

            IntPtr found = PadIpc.FindWithRetry(() => { calls++; return IntPtr.Zero; },
                                                4, TimeSpan.FromMilliseconds(250), waits.Add);

            Assert.Equal(IntPtr.Zero, found);
            Assert.Equal(4, calls);
            Assert.Equal(3, waits.Count);   // no wait after the last attempt
        }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadIpcTests"`
Expected: build error — `PadIpc` has no `FindWithRetry`.

- [ ] **Step 3: Add FindWithRetry**

In `Services/Pad/PadIpc.cs`, after `SendOpen`, add:

```csharp
        /// <summary>
        /// Looks for the running instance's window, retrying while it is still starting: MicaStats
        /// takes its single-instance mutex before its window exists, so a launch in that gap (a
        /// pinned MicaPad button clicked right after sign-in) would otherwise find nothing.
        /// </summary>
        /// <param name="find">One lookup; <see cref="IntPtr.Zero"/> when the window is not there yet.</param>
        /// <param name="attempts">How many lookups at most.</param>
        /// <param name="interval">The pause between lookups; none follows the last one.</param>
        /// <param name="sleep">How to pause; tests record the pauses instead.</param>
        /// <returns>The window, or <see cref="IntPtr.Zero"/> when it never appeared.</returns>
        public static IntPtr FindWithRetry(Func<IntPtr> find, int attempts, TimeSpan interval, Action<TimeSpan>? sleep = null)
        {
            sleep ??= pause => System.Threading.Thread.Sleep(pause);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                IntPtr found = find();
                if (found != IntPtr.Zero) return found;
                if (attempt < attempts) sleep(interval);
            }
            return IntPtr.Zero;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadIpcTests"`
Expected: all pass.

- [ ] **Step 5: Wait for the window in the second instance**

In `App.xaml.cs`, inside `if (!createdNew)`, replace:

```csharp
                // Try to find the existing window to show settings before exiting
                IntPtr existingWnd = FindWindow("Kil0bitOverlayWndClass_Main", null);
                if (existingWnd != IntPtr.Zero)
                {
```

with:

```csharp
                // Find the running instance's window to hand this launch to. It may still be starting
                // (it takes the mutex before its window exists), so wait for it for up to five seconds.
                IntPtr existingWnd = Kil0bitSystemMonitor.Services.Pad.PadIpc.FindWithRetry(
                    () => FindWindow("Kil0bitOverlayWndClass_Main", null), attempts: 20, interval: TimeSpan.FromMilliseconds(250));
                if (existingWnd != IntPtr.Zero)
                {
```

and replace the end of that block:

```csharp
                    else
                    {
                        SendMessage(existingWnd, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                s_mutex.Dispose();
```

with:

```csharp
                    else
                    {
                        SendMessage(existingWnd, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else if (padRequested)
                {
                    Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("pad",
                        "MicaStats is running but its window did not appear within five seconds; the request to open MicaPad" +
                        (padPath != null ? " with " + padPath : "") + " was lost");
                }
                s_mutex.Dispose();
```

- [ ] **Step 6: Build the app**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj`
Expected: build succeeds with no new warnings.

- [ ] **Step 7: Remove the item from the follow-ups list**

In `docs/superpowers/plans/2026-09-29-micapad-followups.md`, delete this bullet (both lines):

```markdown
- A `--pad` request is lost, without a log line, if the running instance has no overlay window
  yet.
```

- [ ] **Step 8: Whole suite, then commit**

Run: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj`
Expected: every test passes.

```bash
git add Services/Pad/PadIpc.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadIpcTests.cs docs/superpowers/plans/2026-09-29-micapad-followups.md
git commit -F - <<'EOF'
fix(pad): a launch waits for a running instance that is still starting

MicaStats takes its mutex before its window exists, so a MicaPad launch
right after sign-in found no window and was dropped without a trace. The
second instance now looks for the window for up to five seconds and logs
a request that is still lost.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
