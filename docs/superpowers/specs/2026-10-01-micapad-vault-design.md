# MicaPad: encrypted notes and a credential vault

Approved in conversation with the owner on 2026-10-01. Decisions taken there:

- Notes are locked by Windows (DPAPI, current user), not by a PIN: MicaPad opens with no prompt.
- Credentials are revealed with MicaPad's own PIN (6–12 digits), not Windows Hello.
- Storing a credential never asks for the PIN; revealing one does (public-key vault).

## Why

People paste passwords, API keys and connection strings into MicaPad. Today every note, every
history version and the session are plain UTF-8 files under `%APPDATA%\MicaStats\MicaPad`, readable
by any program, any backup, any cloud sync and anyone who copies the folder. The owner wants:

1. Every piece of MicaPad's own data encrypted before it reaches the disk.
2. A way to select a secret in a note and move it into an encrypted vault, leaving a reference in
   the text; revealing the secret needs a PIN.

## What it protects against, and what it does not

Protects:

- Notepad, Explorer previews, search indexers and other programs opening MicaPad's files.
- Other Windows accounts on the PC, a stolen or copied disk, backups, cloud-synced AppData, another PC.
- Credentials additionally: someone at the unlocked PC without the PIN, and any program running as
  the user while the vault is locked.

Does not protect (stated in GUIDE.md):

- A malicious program already running as the user while MicaPad is open: it can read the screen,
  the editor's memory, and ask DPAPI for the notes key. It can also take a copy of the vault and
  guess PINs offline; each guess costs one PBKDF2 run (600,000 rounds, about 0.3 s on one core), so
  a 6-digit PIN falls in days, a 12-digit one does not. GUIDE.md recommends a longer PIN.
- Keyloggers capturing the PIN, administrators, SYSTEM.
- Plain copies made before this version: notes already in the Windows Recycle Bin, and old disk
  blocks an SSD keeps after a file is overwritten.
- .NET strings cannot be wiped: a secret is in process memory while it is in the editor or shown.
  Byte arrays MicaPad owns (keys, PIN bytes, decrypted values) are zeroed with
  `CryptographicOperations.ZeroMemory` as soon as they are no longer needed.

## What does not change

- Files the user opens from disk (`D:\todo.txt`) are still saved in their own format and encoding
  by Ctrl+S / Save As, so other programs can open them. Only MicaPad's store is encrypted.
- The store layout, file names, atomic two-phase writes, versions, history pruning and the Recycle
  Bin behaviour stay as they are. Only the bytes inside the files change.
- MicaStats' other data (`config.json`, history, logs, `secrets.bin`) is untouched.
- The AI features never see notes or the vault (no tool reads them today; none is added).

## Delivery: four parts, in order

Each part ends green, deployed for the owner's e2e check.

1. Encrypted store: notes key, file format, NoteStore wiring, migration, unreadable-key handling.
2. Vault service: crypto, PIN, lockout, references. Pure services, no UI.
3. Editor: Store as credential, the reference pill and its menu, PIN dialogs, reveal popup,
   clipboard, history scrubbing.
4. Settings → MicaPad credentials group, GUIDE.md.

## Part 1 — Encrypted store

### 1.1 Notes key

- `MicaPad\key.bin`: 32 random bytes (`RandomNumberGenerator`), protected with
  `ProtectedData.Protect(…, entropy "MicaStats.MicaPad.NotesKey.v1", DataProtectionScope.CurrentUser)`,
  written through `AtomicFile.Write`.
- Loaded once when the store opens; held for the life of the process.
- Created only when `key.bin` does not exist **and** no file in the store is encrypted (fresh
  install, or the first launch after this update).

### 1.2 File format

Every store file — `notes\{id}\current.txt`, `meta.json`, `history\*.txt`, and `session.json` — is
written as:

| Bytes | Content |
|---|---|
| 0–3 | Magic `FF 4D 50 45` (0xFF then ASCII `MPE`) |
| 4 | Format version `01` |
| 5–16 | 12-byte random nonce |
| 17…n−17 | AES-256-GCM ciphertext of the UTF-8 bytes the file held before |
| last 16 | GCM tag |

The 5 header bytes are the associated data. Overhead is 33 bytes. NoteStore only ever wrote valid
UTF-8, which never contains the byte 0xFF, so a file that starts with 0xFF is encrypted and any
other file is a plain file from an earlier version — no marker file is needed.

Reading:

- Encrypted and the tag verifies: the text.
- Encrypted and the tag fails (damaged, tampered, other key): the file is unreadable. It is handled
  exactly as an unreadable file is today (`TryLoadText` returns false, meta is rebuilt, session is
  rebuilt, a snapshot is skipped) with a warning that names the file but never its content. An
  unreadable file is never overwritten by a read path.
- Plain (an earlier version's file): read as UTF-8 as today. The next save writes it encrypted.

`SnapshotInfo.Size` stays the size of the text (file length minus 33 for encrypted files), so the
history pane's sizes and deltas do not change.

### 1.3 Migration

When the store opens, a background pass (under each note's lock, so it never races a save)
encrypts every plain store file it finds, including `.ready` leftovers, which are committed first.
For each file: write the encrypted version through `AtomicFile.Write`, after first overwriting the
plain file in place with zeros of the same length and flushing (best effort; a failure is logged
and the encryption still happens). The pass is idempotent: encrypted files are skipped, so a crash
part-way simply resumes at the next start. It logs one line: how many files it encrypted.

### 1.4 When the key cannot be used

- `key.bin` missing while encrypted files exist, or DPAPI refuses it (another PC, another account,
  a reset Windows password): MicaPad renames the whole folder to
  `MicaPad-locked-yyyyMMdd-HHmmss` (beside it), starts an empty store with a new key, and tells the
  user once, at the first MicaPad window, which folder holds the notes it could not open. Nothing
  is deleted.
- Downgrading to an older MicaStats is not supported (it would show encrypted files as garbage);
  the release notes say so.

### 1.5 One choke point

All store reads and writes already go through `NoteStore` (`SaveNote`, `TryLoadText`, `LoadMeta`,
`LoadSession`, `WriteSessionJson`, `WriteSnapshot`, `ReadSnapshot`, `RebuildMeta`). They switch to
`StoreCipher` around `AtomicFile`; `AtomicFile` gains a bytes read (`ReadBytes`) beside `ReadText`.
No other code reads store files. `SaveToSource` keeps writing plain bytes to the user's file.

## Part 2 — Vault service

### 2.1 File

`MicaPad\vault.bin`: the DPAPI-protected (entropy `"MicaStats.MicaPad.Vault.v1"`, current user)
UTF-8 JSON below, written through `AtomicFile.Write`. Absent until the first credential is stored.

```json
{
  "version": 1,
  "publicKey": "<base64 SubjectPublicKeyInfo, RSA 3072>",
  "privateKey": "<base64 encrypted PKCS#8: PBES2, PBKDF2-HMAC-SHA256, 600000 rounds, AES-256-CBC; password = PIN>",
  "failedAttempts": 0,
  "lockedUntilUtc": null,
  "credentials": [
    {
      "id": "K7Q2M9XD",
      "label": "GitHub token",
      "createdUtc": "2026-10-01T09:00:00Z",
      "noteId": "<note id where it was stored>",
      "wrappedKey": "<base64 RSA-OAEP-SHA256 of a 32-byte AES key>",
      "nonce": "<base64 12 bytes>",
      "cipher": "<base64>",
      "tag": "<base64 16 bytes>"
    }
  ]
}
```

- Each value is encrypted with its own random AES-256-GCM key; the associated data is the id, so a
  value cannot be moved to another id. The AES key is wrapped with the public key.
- Ids, labels and dates are readable without the PIN (pills show labels); values are not.
- The PBKDF2 round count is a constructor parameter so tests can use 1,000; a test pins the
  production value at 600,000.

### 2.2 PIN

- 6 to 12 ASCII digits. Created (typed twice) at the first Store as credential.
- Checking a PIN: import the private key with it (`ImportEncryptedPkcs8PrivateKey`) and confirm
  its public half equals `publicKey`. Any failure is a wrong PIN.
- Change PIN: needs the current PIN; the private key is re-exported with the new one.
- Reset vault (forgotten PIN): deletes `vault.bin` after confirmation. References then show as
  missing.

### 2.3 Wrong PINs

Consecutive wrong PINs are counted in `vault.bin`. From the 5th, the vault refuses to check a PIN
for a wait: 30 s after the 5th, doubling with each further wrong PIN, capped at 15 min
(`lockedUntilUtc`). A right PIN resets the count. The PIN dialog shows the remaining tries before a
wait, or the time left. Time comes from an injected clock.

### 2.4 Unlocked state

A right PIN unlocks the vault for 5 minutes (from that PIN, not sliding): the private key stays in
memory, so further reveals ask nothing. It locks — key disposed — when the 5 minutes pass, on
**Lock now**, when Windows locks the session (`SystemEvents.SessionSwitch` SessionLock) or suspends
(`PowerModeChanged` Suspend), and at exit. One state for the whole process: every MicaPad window
shares it.

### 2.5 References

- Text form `{{secret:K7Q2M9XD}}`: 8 characters of Crockford base32 (`0-9 A-H J K M N P-T V-Z`),
  random, unique in the vault.
- `SecretTokens` formats, parses and finds them (regex
  `\{\{secret:([0-9A-HJKMNP-TV-Z]{8})\}\}`), and generates ids.
- `SecretScrubber` replaces every exact occurrence of a value in a text with its reference and
  returns the count (ordinal comparison).

## Part 3 — Editor

### 3.1 Store as credential

Editor right-click menu, new item **Store as credential…** (icon lock), enabled when the selection
is 4 characters to 64 KiB, contains no reference, and the tab is editable (not a history preview).

1. No vault yet: the **Create PIN** dialog (PIN, confirm, the "longer is stronger" hint). Cancel
   stops here.
2. **Store as credential** dialog: Label (optional, at most 60 characters; empty shows as
   "Credential"), the secret as `•••• (N characters)`, and, when the same text appears elsewhere in
   the note, "Appears N times in this note — every copy will be masked."
3. The vault saves the credential first. If that fails, an error is shown and the text is untouched.
4. Every exact occurrence in the note's text becomes the reference, as one edit.
5. The tab's undo history is cleared (undo would bring the secret back), the note is saved at once,
   and every history version of that note that contains the value is rewritten with the reference
   (`NoteStore.ScrubSnapshots`). A version that cannot be rewritten is reported in the status bar
   ("1 older version still holds it").
6. Status bar: "Stored as K7Q2M9XD".

Other notes are not searched.

### 3.2 The pill

A `VisualLineElementGenerator` draws each reference as one inline pill: lock glyph + label (or
"Credential"); tooltip "Stored credential K7Q2M9XD — right-click for options". The caret moves over
it as one unit and Backspace/Delete next to it removes it whole. A reference whose id is not in the
vault draws as "Missing credential" in the muted color. Pill colors are new `PadPalette` entries in
both themes (text ≥ 4.5:1 on its background). Pills draw in every language mode, inside code
fences too.

### 3.3 Pill menu

Right-clicking a pill shows, instead of the editor menu:

| Item | PIN | What it does |
|---|---|---|
| Reveal… | yes, unless unlocked | Popup by the pill: the value in a monospace selectable box, Copy, Hide. Closes after 30 s, on Escape or when it loses focus. Never put into the document. |
| Copy secret | yes, unless unlocked | Clipboard as below. Status bar "Copied — clears in 30 s". |
| Rename label… | no | Changes the label; every pill with that id updates. |
| Unmask | yes, unless unlocked | Replaces this pill with the value, as a normal undoable edit. The vault keeps the credential. |
| Delete credential… | yes, unless unlocked | Confirm ("Notes that use K7Q2M9XD will show it as missing. This cannot be undone."), then removes it from the vault. The text keeps the reference. |
| Copy reference | no | Copies `{{secret:K7Q2M9XD}}`. |
| Lock now | — | Shown while unlocked. |

A missing reference's menu has only Copy reference.

### 3.4 PIN dialog

Digits only, masked, shows wrong-PIN tries left or the wait remaining (and refuses input during a
wait). Themed with the MicaPad palette.

### 3.5 Clipboard

Copy secret puts Unicode text plus the formats `ExcludeClipboardContentFromMonitorProcessing`,
`CanIncludeInClipboardHistory` = 0 and `CanUploadToCloudClipboard` = 0, so Windows clipboard history
and cloud clipboard skip it. After 30 s, if the clipboard sequence number
(`GetClipboardSequenceNumber`) is unchanged since the copy, the clipboard is cleared. Copy, Copy as
RTF, drag, Save As and Ctrl+S carry references as text, never values.

## Part 4 — Settings and guide

Settings → MicaPad gains a **Credentials** group: "N credentials stored" (or "No PIN set yet"),
**Change PIN…**, **Lock now** (enabled while unlocked), **Reset vault…** (confirm: "Deletes every
stored credential. References in your notes will show as missing. This cannot be undone.").
No new `AppConfig` properties.

GUIDE.md, MicaPad section: notes are encrypted for this Windows account on this PC; moving to
another PC means Save As per note; how to store, reveal, copy and unmask a credential; the PIN, the
wait after wrong PINs, Reset vault; what it does not protect against (the list above, in plain
words); a longer PIN is stronger.

## Architecture

New units under `Services/Pad/`, no WPF types, unit-tested directly:

| Unit | Part | Responsibility |
|---|---|---|
| `NotesKey` | 1 | Load or create the DPAPI-protected key; report Ready / Created / Unreadable |
| `StoreCipher` | 1 | Encrypt, decrypt, detect the format |
| `StoreMigration` | 1 | Encrypt plain store files, zero the plain originals; move an unopenable store aside |
| `CredentialVault` | 2 | vault.bin, create/add/rename/delete/reveal/change PIN/reset, lockout, unlock window |
| `SecretTokens` | 2 | Reference format, parse, find, id generation |
| `SecretScrubber` | 2 | Replace a value's occurrences with its reference |

`NoteStore` gains `ScrubSnapshots(id, value, reference)`. WPF adapters under `Pad/`:
`SecretPillGenerator`, `PinDialog`, `StoreSecretDialog`, `RevealPopup`, `SecretClipboard`,
`VaultSession` (process-wide unlock timer and Windows lock/suspend hooks). `EditorMenus` gains the
Store item and the pill menu.

## Error handling

- Nothing secret — note text, values, PINs, keys — is ever part of a log line, an exception message
  shown to the user, or a status message.
- Crypto failures on store files behave like unreadable files today (1.2); a read never overwrites.
- Vault file unreadable (DPAPI refuses it, or it is corrupt): renamed to
  `vault-locked-yyyyMMdd-HHmmss.bin`, the user is told once, references show as missing. Nothing is
  deleted.
- Every vault change is saved atomically before the editor changes; a failed save changes nothing.

## Testing

Automated (xUnit, temp folders only, never the real `%APPDATA%`, UI on the shared `UiThread`):

- Part 1: cipher round trip incl. Thai text and empty text; tag failure on one flipped byte, a
  truncated file, another key; plain detection; every NoteStore read path on an encrypted file, a
  plain file and a damaged one; no store file on disk contains a known plaintext after save,
  snapshot and session writes (scan the bytes); migration encrypts everything incl. `.ready`, is
  idempotent and resumes; `SnapshotInfo.Size` unchanged; key created only when nothing is
  encrypted; an unopenable key moves the folder aside and starts empty.
- Part 2: create/add/reveal round trip; wrong PIN; lockout schedule (5th → 30 s, doubling, 15 min
  cap) with a fake clock; right PIN resets; change PIN keeps every value; reset; a value cannot be
  moved to another id; reference format/parse/find and id alphabet; scrubber counts; production
  rounds = 600,000; vault.bin without DPAPI is unreadable bytes.
- Part 3: the Store item's enabled states (length bounds, reference inside, read-only tab); store
  replaces every occurrence as one edit, clears undo, scrubs history; a failed vault save leaves the
  text; pill generator produces one element per reference and a missing one for an unknown id; pill
  menu items and their PIN needs; unmask is undoable; clipboard seam receives the exclusion formats
  and is cleared after the interval only if unchanged; the unlock window expires on a fake clock and
  on a lock event.
- Part 4: Settings group states; GUIDE line exists.

Manual, on the owner's machine after each deploy:

1. After updating: every old note opens; `current.txt` in Explorer shows scrambled bytes; history
   versions open and compare.
2. Store a password from a note; the pill shows; Ctrl+Z does not bring it back; history versions
   show the reference.
3. Reveal, Copy secret (Win+V history does not show it; clipboard empty after 30 s), Unmask, Delete.
4. Five wrong PINs → wait; lock Windows → the vault asks again.
5. Save As a note with a pill to a `.txt`; open it in Notepad: only the reference.

## Out of scope

Windows Hello; a password-manager list of all credentials; exporting all notes as plain text;
moving notes to another PC; secure erase of old plain files; searching other notes for the same
secret; encrypting files opened from disk.
