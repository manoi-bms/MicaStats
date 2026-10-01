# MicaPad vault: execution rulings and deferred findings

Every decision taken during subagent-driven execution of the two vault plans (part1-2, part3-4), copied from the execution ledgers before they were deleted. Plan-level rulings R1-R8 are in the plan documents themselves. Branch feat/micapad-vault; final review verdict "with fixes", fix wave reviewed clean (19d4a05..0dafd4f).

## Rulings, in the order made

- Task 3: Ruling: PadStoreTests.A_source_write_replaces_the_file_and_leaves_no_temporary_file counts files in the store root, which now also holds key.bin — count the root's files except NotesKey.FileName (still exactly 2) — the assertion's intent (no temp file left) is unchanged — if wrong, a stray file named key.bin would go unnoticed in that one test
- Task 3: Ruling: (plan-mandated finding) AnyEncryptedFile counted an unreadable probe as "encrypted", so a keyless plain store could be moved aside over a passing lock — contradicts R2; AnyEncryptedFile now lets I/O errors propagate (constructor throws, retried later) — cost if wrong: MicaPad fails to open until the lock clears instead of starting over
- Task 3: Ruling: ReadSnapshot is the only store read that runs without the note's lock; it must not commit a .ready (it would race Task 4's migration, whose wipe could then zero the committed encrypted file) — ReadSnapshot reads .ready-else-target without committing — cost if wrong: a leftover version .ready stays uncommitted until the migration pass (under lock) commits it
- Task 3: Ruling: TextLength opens with FileShare.ReadWrite|Delete so an unlocked History listing never blocks pruning or the migration's replace (minor, taken now because Task 4 depends on it)
- Task 4: Ruling: (plan-mandated finding) "Show folder" wired to ShowInFolder, which rejects directories — ShowInFolder accepts an existing folder too; a static Explorer-start seam keeps tests from launching Explorer — cost if wrong: none visible
- Task 4: Ruling: (cannot-verify item) the locked-folder notice lived only in the run that moved the store; spec 1.4 says "at the first MicaPad window", so the folder path is kept in an encrypted marker file in the store until a window shows the notice — cost if wrong: one extra small file in the store
- Task 4: Ruling: minors taken in this round because they touch the user's "no readable leftovers" goal or first-start UX: NUL-read race in ReadStoreTextInPlace; per-file (not per-note) locking in the pass; peek the first byte before reading a whole file; enumeration I/O errors warn and skip instead of aborting; wipe a plain target before a plain .ready replaces it; wipe+delete leftover .tmp files under the lock; a test that pins the wipe wiring
- Task 6: Ruling: (plan-mandated R3 code) Save deleted vault.bin.ready on every failure, which after a documented ReplaceFile partial failure (1176) is the only copy — delete only the .ready this save created, and only while vault.bin still exists — cost if wrong: a stray .ready may resurrect a change once
- Task 6: Ruling: count before check — the wrong-PIN increment (and its wait) is saved before the PIN is checked, so a vault whose saves fail cannot be guessed at without limit (fail closed) — cost: one extra save per right PIN
- Task 6: Ruling: also taken now: Reset deletes .tmp/.ready before vault.bin; MovedAsideTo under the lock; a saved wait is capped at now+15 min (clock skew); tests for invalid JSON / invalid content moved aside, a Gregorian stamp, and Unlock with a failing save
- Task 6: Ruling: skewed-wait cap is saved back (capped now+15 min persisted) rather than clamped on every read, which would slide forever — implementer's choice accepted
- Ruling: the final whole-branch review runs once after Plan B (Tasks 7-11) over the whole branch (merge-base..HEAD) — both plans ship together on one branch — cost if wrong: Part 1-2 issues found later rather than now (Part 1 is already deployed and verified on the owner's notes)
- Ruling: SecretClipboard.Copy returns bool (false when the clipboard stayed busy) — the code, its tests and Task 10's status message all need it; the Interfaces line is a typo — cost if wrong: none.
- Task 9: Ruling: the reveal's ValueBox copy (Ctrl+C, context menu) is routed through the card's Copy action (the secret clipboard) — a plain copy would put the value in Win+V history, against the owner's "secured" intent — cost if wrong: copying part of a value copies all of it
- Task 10: Ruling: (plan-mandated sequence) when FlushWrites times out, the store keeps a pending scrub for that note and retries it from the window tick once the writer is idle, and the status says older versions may still hold it until then — cost: the value stays in process memory until the retry succeeds
- Task 10: Ruling: (spec question) a file-backed note's own file keeps the value until Ctrl+S (MicaPad never writes a user's file by itself) — the status adds "save the file (Ctrl+S) to remove it there" — cost if wrong: one longer status line
- Task 10: Ruling: taken now — neutral statuses for races (vault created/reset elsewhere, credential deleted while a card waited); a plain-text copy helper for Copy reference
- Ruling: one fix wave takes both Importants and Minors 1 (PendingScrub.ToString), 2 (scrubs left at exit run after FlushAll), 3 (4-byte magic so a UTF-16 BOM plain file is plain), 4 (PIN card shows a running wait on open), 6 (busy clipboard still cleared), 7 (wait cursor), 9 (Change PIN disabled with no vault), 10 (test hygiene) — cost: a slightly larger final wave
- Ruling: added a second sealed copy of the notes key (key.bak) — one 230-byte file was the single point of failure for every note — cost if wrong: one extra small file; behaviour otherwise unchanged
- Final: parked — a damaged header (byte 0 = FF, bytes 1-3 damaged) now reads as plain garbage and the maintenance pass would re-encrypt it, destroying a file a header repair could recover — Ruling: residual, surfaced to the owner (tighter rule: magic → encrypted; non-FF or FF FE → plain; other FF-led → damaged encrypted); needs a 3-byte corruption with byte 0 intact
- Final: parked — GUIDE/spec say a 6-digit PIN falls "within days"; one GPU does it in minutes (12 digits: years) — Ruling: residual wording, surfaced to the owner
- Final: parked — a held key.bak blocks opening even with a good key.bin; a listing failure reports "1 older version" for an unknown count; a scrub throwing while the writer is stuck never shows "may still hold it"; the exit run can drop a scrub before the writer is idle — Ruling: real, rare, deferred

## Deferred minor findings (not fixed)

- Task 1: minor (deferred): StoreCipherTests.cs has a UTF-8 BOM and no trailing newline (repo norm: no BOM)
- Task 1: minor (deferred): no known-vector test pinning the byte layout beyond header/overhead
- Task 2: minor (deferred): AtomicFile.ReadBytes repeats ReadText's commit-or-fallback flow (two copies)
- Task 2: minor (deferred): File.Exists then ReadAllBytes race (mirrors ReadText; propagates as IOException)
- Task 3: minor (deferred): if creating the new key after MoveAside fails, the constructor throws and LockedFolder is lost; the next attempt starts empty and only the log names the folder
- Task 3: minor (deferred): ReadStoreTextInPlace File.Exists-then-ReadAllBytes race returns null+warning for a lock-free reader (acceptable)
- Task 4: minor (deferred): if only a note's history listing fails, that note's current/meta are skipped this pass too (retried next start)
- Task 4: minor (deferred, out of scope): PadWindowTests.cs:88 raw em dash from older commit e2c525ce
- Task 4: minor (deferred): no test for the failed locked-marker write path
- Task 5: minor (deferred): SecretScrubber.Replace scans twice (Count + loop); ScrubSnapshots catches only IO/UnauthorizedAccess, no test for a failed rewrite
- Task 6: minor (deferred to Plan B Task 7): expiry is silent and the key lives until the next access — Task 7 adds the announcement and VaultSession's timer
- Task 6: minor (deferred): Changed fires on no-op calls (Unlock Waiting/NoVault, Reset with no vault, Rename to same label, Dispose) and not on Load
- Task 6: minor (deferred): Unlock/ChangePin doc lacks <exception cref="IOException"> (now every check writes); an older .ready overwritten by a failed save's Move(overwrite) then deleted (pre-existing, needs a prior crash)
- Task 6: minor (carried to Plan B Task 7): Create's guard ignores a lone vault.bin.ready
- Task 7: minor (deferred): no test of VaultSession's real DispatcherTimer re-arm/early-fire path
- Task 7: minor (deferred): a second direct Load() that throws leaves _loaded true with _doc null (Load should clear _loaded first)
- Task 7: minor (deferred): _post(Arm) outside OnTick's try/catch
- Task 8: minor (carried to Task 10): SecretPillGenerator scans only the start line of a multi-line (folded) visual line — scan to CurrentContext.VisualLine.LastDocumentLine.EndOffset
- Task 8: minor (carried to Task 10): SecretClipboard.SetData retries 4x on top of WPF's own retry — one call, catch COMException
- Task 8: minor (deferred): no two-copies-within-30s test; pill tooltip/missing foreground asserted only via helpers
- Task 9: minor (deferred): a throwing unlock/change callback propagates from the click handler (Task 10 host catches); a paste with surrounding spaces is refused
- Task 9: minor (deferred, manual check): reveal opened while the window is already inactive gets no focus-left; IsInOwnMenu relies on PlacementTarget (check the reveal box context menu by hand)
- Task 10: minor (deferred): untested branches ("nothing was unmasked", "PIN changed", modified keys swallowed, Key.System pass-through); Tab swallowed from outside the card; a deferred moved-vault notice can be missed while another notice stays up
- Task 10: minor (deferred): a pending scrub left when the last window closes at exit with the writer still stuck is lost (would need a scrub in the workspace exit flush)
- Task 10: minor (deferred, final review): RetryPendingScrubs clears the list before scrubbing and has no per-entry guard — a ListSnapshots IOException drops pending scrubs; CompleteStore adds the pending entry after the first scrub (a throw skips it); NoteStore.ScrubSnapshots' ListSnapshots is outside its catch
- Task 10: minor (deferred, final review): PendingScrub is a positional record whose ToString prints the value — make it a class or override ToString
- Task 10: minor (deferred): handover of a pending scrub to another window untested; retry status drops the Ctrl+S hint; a version landing during the app's exit flush is never scrubbed
- Task 11: minor (deferred, final fix wave): GUIDE sentence "Within 6 to 12 digits, a longer PIN is stronger." awkward/overlong; not-protected list omits freed SSD blocks; GUIDE test Substring throws if the section is last
- Final review: minor (deferred): notice lost when key creation fails right after MoveAside; the locked-folder notice can be replaced by a disk question; the reviewer's "can wait" list (see report)

## Deployment notes

- Part 1 deploy: backup of the owner's plain MicaPad folder taken to scratchpad before first encrypted run (disclose to owner; delete after verification)
- Part 1 deployed (bc690c1, 09:26): 29 files migrated on the owner's real store, 25/25 texts (4 current + 21 versions) decrypt identical to the plain backup; backup at scratchpad\micapad-backup-20261001-092632 (plain — owner to delete after checking)
