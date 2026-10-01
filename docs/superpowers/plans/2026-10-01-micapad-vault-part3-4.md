# MicaPad credential vault in the editor — Implementation Plan (Parts 3–4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the credential vault in MicaPad's hands: select text → Store as credential (PIN created the first time), references drawn as pills, a pill menu to reveal, copy, rename, unmask, delete and lock, PIN entry with waits, a clipboard that forgets, history scrubbing, a Settings group, and the guide.

**Architecture:** The App owns one `CredentialVault` for the process (Settings can use it while MicaPad is closed) and hands it to `PadWorkspace` through its options; `VaultSession` locks it when Windows locks or sleeps and when the 5 minutes run out. In the window, `SecretPillGenerator` draws references, `VaultCard` (one overlay card over the editor with a mode per question) asks for PINs, labels and confirmations and shows revealed values, `SecretClipboard` copies values out of clipboard history and clears them, and a new partial `MicaPadWindow.Vault.cs` wires the flows. Pure helpers stay under `Services/Pad/`.

**Tech Stack:** WPF .NET 8, AvalonEdit 6.3.1.120 (VisualLineElementGenerator, InlineObjectElement), ModernWpfUI 0.9.6, Microsoft.Win32.SystemEvents, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-micapad-vault-design.md` (Parts 3–4). Builds on `docs/superpowers/plans/2026-10-01-micapad-vault-part1-2.md` (done: encrypted store, `CredentialVault`, `SecretTokens`, `SecretScrubber`, `NoteStore.ScrubSnapshots`).

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-vault`. Commit on it; never push, merge or tag.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 300 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys). Never launch MicaStats. Never start Explorer or any process from a test (use the seams).
- Tests: temp folders only (`PadTempDir`, `PadTestEnv`), never the real `%APPDATA%`; no network; UI tests only on the shared `UiThread.Run`, windows built but never shown; vaults in tests use `rounds: 1000`.
- New code under `Services/` holds no WPF types. LF line endings, UTF-8 without BOM, trailing newline.
- Nothing secret — a credential value, a PIN, a key — is ever part of a log line, a warning, an exception message shown to the user, or a status message. Warnings name ids and files only.
- Glyphs and non-ASCII: `\uXXXX` in C#, `&#xXXXX;` in XAML. The Write/Edit tools can decode escapes into raw characters; after writing, `LC_ALL=C grep -c '[^[:print:][:space:]]' <file>` must print 0 for every file you wrote, and fix with a command that cannot decode (PowerShell `[IO.File]::ReadAllText/Replace/WriteAllText` with `[Text.UTF8Encoding]::new($false)`) if not.
- Exact values from the spec: reference `{{secret:ID}}`; Store enabled for a selection of 4 characters to 64 KiB (65,536 characters) with no reference inside, in the editable editor; label at most 60 characters, empty shows as "Credential"; unlock 5 minutes; reveal hides after 30 s; clipboard cleared after 30 s only if unchanged; lock glyph U+E72E (Segoe Fluent Icons).
- Commits: `git add` exact paths, `git commit -m "..."` (no heredoc), never amend, end with `Co-Authored-By: <your model name> <noreply@anthropic.com>`.
- Never weaken an existing assertion. Do not dispatch subagents.

## Rulings on the spec

- **R5 — the revealed value shows in the vault card, not a popup by the pill.** One overlay card over the editor does every vault question (PINs, label, confirmations) and the reveal; it hides after 30 s, on Escape, on Hide, and when focus leaves it. Cost if wrong: the value appears centered rather than beside the pill.
- **R6 — Settings → Change PIN… opens MicaPad and shows the Change PIN card there,** so there is one PIN entry UI. Lock now and Reset vault… act from Settings directly (Reset confirms with a ContentDialog, as Settings' own reset does).
- **R7 — the vault belongs to the App,** created on first use beside the notes (`%APPDATA%\MicaStats\MicaPad\vault.bin`) and handed to `PadWorkspace` through `PadWorkspaceOptions.Vault`, so Settings and every MicaPad window share one unlock state.
- **R8 — the pill menu opens for the reference under the mouse;** from the keyboard (menu key / Shift+F10), for the reference the caret is inside or at the start of.

## Review Focus

1. Storing a value that also appears elsewhere in the note, in other tabs, or inside an existing reference: every exact copy in this note is replaced in one edit, references are never nested, other tabs are untouched (Task 10 test).
2. The note is saved and its versions scrubbed after the replacement: no version written before or after the store still holds the value (Task 10 test reads every version back).
3. A wrong PIN typed five times, then the wait, then the right PIN: the card shows tries left, refuses input during the wait with a countdown, and accepts the right PIN after (Task 9 test with a fake clock).
4. The vault locking while a card or the reveal is open (Windows lock, timer): the reveal hides, a pending PIN question stays answerable (Task 7/9 tests).
5. A theme switch with pills on screen: pills repaint in the new palette (Task 8 test).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Modify `Services/Pad/CredentialVault.cs` | 7 | `IsLoaded`, `EnsureLoaded()` |
| Modify `Services/Pad/PadWorkspace.cs` | 7 | `PadWorkspaceOptions.Vault`, `PadWorkspace.Vault`, `VaultNoticeShown` |
| Create `Pad/VaultSession.cs` | 7 | Lock on Windows lock/suspend and at the end of the unlock |
| Modify `App.xaml.cs` | 7, 11 | `PadVault`, `VaultSession` start, hand the vault to the workspace, clear the clipboard at exit; `OpenPadVault` |
| Modify `tests/…/PadTestEnv.cs` | 7 | A test vault (1000 rounds) in the temp store |
| Modify `Services/Pad/PadPalette.cs` | 8 | `PillBack`, `PillBorder`, `PillText`, `PillMissingText` |
| Create `Pad/SecretPillGenerator.cs` | 8 | Draw references as pills |
| Create `Pad/SecretClipboard.cs` | 8 | Copy a value outside clipboard history; clear after 30 s if unchanged |
| Create `Pad/VaultCard.xaml`, `Pad/VaultCard.xaml.cs` | 9 | The overlay card and its modes |
| Create `Pad/MicaPadWindow.Vault.cs`; modify `Pad/MicaPadWindow.xaml`, `Pad/MicaPadWindow.xaml.cs` | 10 | Store flow, pill menu, notice, redraw |
| Modify `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `GUIDE.md` | 11 | Credentials group, guide |
| Create `Services/Pad/VaultStatusText.cs` | 11 | The Settings status line |

---

### Task 7: The vault in the App and the workspace; VaultSession

**Files:**
- Modify: `Services/Pad/CredentialVault.cs`, `Services/Pad/PadWorkspace.cs`, `App.xaml.cs`, `tests/Kil0bitSystemMonitor.Tests/PadTestEnv.cs`
- Create: `Pad/VaultSession.cs`
- Test: add to `tests/Kil0bitSystemMonitor.Tests/CredentialVaultTests.cs`; create `tests/Kil0bitSystemMonitor.Tests/VaultSessionTests.cs`

**Interfaces:**
- Consumes: `CredentialVault` (Load, Lock, IsUnlocked, UnlockedUntilUtc, Changed, MovedAsideTo).
- Produces: `public bool CredentialVault.IsLoaded`, `public bool CredentialVault.EnsureLoaded()`; `public CredentialVault? PadWorkspaceOptions.Vault { get; init; }`; `public CredentialVault? PadWorkspace.Vault { get; }`; `public bool PadWorkspace.VaultNoticeShown { get; set; }`; `PadTestEnv.Vault` (a `CredentialVault`); `internal sealed class VaultSession : IDisposable` with `VaultSession(CredentialVault vault, Func<DateTime> utcNow, Action<Action> post)`, `void Start()`, `internal void OnSessionSwitch(SessionSwitchReason reason)`, `internal void OnPowerModeChanged(PowerModes mode)`, `internal TimeSpan? ExpiryIn`, `internal void OnExpiryTimer()`; `internal static CredentialVault App.PadVault`.

- [ ] **Step 1: Failing tests**

In `CredentialVaultTests.cs`:

```csharp
        [Fact]
        public void EnsureLoaded_loads_once_and_reports_a_file_it_cannot_read_yet()
        {
            Created().Add("x1234", null, null);
            var vault = New();
            Assert.False(vault.IsLoaded);

            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(vault.EnsureLoaded());
                Assert.False(vault.IsLoaded);
            }

            Assert.True(vault.EnsureLoaded());
            Assert.True(vault.IsLoaded);
            Assert.Single(vault.Credentials);
            File.Delete(VaultPath);
            Assert.True(vault.EnsureLoaded());   // once loaded, it does not read again
            Assert.Single(vault.Credentials);
        }
```

`VaultSessionTests.cs`:

```csharp
using System;
using System.IO;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Win32;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>When the vault locks by itself: Windows locked, the PC asleep, the 5 minutes over.</summary>
    public class VaultSessionTests : IDisposable
    {
        private const string Pin = "246810";
        private readonly PadTempDir _dir = new();
        private readonly FakeClock _clock = new();
        private readonly CredentialVault _vault;
        private readonly VaultSession _session;

        public VaultSessionTests()
        {
            _vault = new CredentialVault(Path.Combine(_dir.Root, CredentialVault.FileName), () => _clock.UtcNow, 1000, _ => { });
            _vault.Load();
            _vault.Create(Pin);
            _vault.Unlock(Pin);
            _session = new VaultSession(_vault, () => _clock.UtcNow, action => action());
        }

        public void Dispose()
        {
            _session.Dispose();
            _dir.Dispose();
        }

        [Theory]
        [InlineData(SessionSwitchReason.SessionLock, false)]
        [InlineData(SessionSwitchReason.ConsoleDisconnect, false)]
        [InlineData(SessionSwitchReason.RemoteDisconnect, false)]
        [InlineData(SessionSwitchReason.SessionUnlock, true)]
        [InlineData(SessionSwitchReason.SessionLogon, true)]
        public void Locking_windows_locks_the_vault(SessionSwitchReason reason, bool stillUnlocked)
        {
            _session.OnSessionSwitch(reason);

            Assert.Equal(stillUnlocked, _vault.IsUnlocked);
        }

        [Theory]
        [InlineData(PowerModes.Suspend, false)]
        [InlineData(PowerModes.Resume, true)]
        [InlineData(PowerModes.StatusChange, true)]
        public void Sleep_locks_the_vault(PowerModes mode, bool stillUnlocked)
        {
            _session.OnPowerModeChanged(mode);

            Assert.Equal(stillUnlocked, _vault.IsUnlocked);
        }

        [Fact]
        public void The_timer_is_set_for_the_end_of_the_unlock_and_locks_then()
        {
            _clock.Advance(60);
            Assert.Equal(TimeSpan.FromMinutes(4), _session.ExpiryIn);

            _clock.Advance(240);
            int changed = 0;
            _vault.Changed += (s, e) => changed++;
            _session.OnExpiryTimer();

            Assert.False(_vault.IsUnlocked);
            Assert.Null(_session.ExpiryIn);
        }
    }
}
```

`OnExpiryTimer` locks through `Lock()` only when the unlock really ended (the clock may lag the timer); with the expiry announcement below, `Changed` fires exactly once — end the test with `Assert.Equal(1, changed);`.

Tests that swap `SecretClipboard` seams (Tasks 8 and 10) do so inside `UiThread.Run`, which serializes them on the shared UI thread; keep it that way.

- [ ] **Step 2: Run to verify they fail** — `--filter "FullyQualifiedName~CredentialVaultTests|FullyQualifiedName~VaultSessionTests"`; expected: build error.

- [ ] **Step 3: `CredentialVault.EnsureLoaded`** — add a `private bool _loaded;` set to true at the end of every successful `Load()` path (Missing, Ready, MovedAside all count as loaded), and:

```csharp
        /// <summary>Whether <see cref="Load"/> has run to the end once.</summary>
        public bool IsLoaded
        {
            get { lock (_gate) return _loaded; }
        }

        /// <summary>
        /// Loads the file the first time; later calls do nothing. False when it cannot be read right
        /// now (another program holds it): the caller says so and tries again later.
        /// </summary>
        public bool EnsureLoaded()
        {
            if (IsLoaded) return true;
            try
            {
                Load();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn("The credential vault could not be read right now: " + ex.Message);
                return false;
            }
        }
```

`Create` must also set `_loaded = true` (a vault created here is loaded).

An unlock that runs out is dropped silently inside `UnlockedKey()` (a getter must not raise events), so nobody would hear about it. Add `private bool _expiredUnannounced;`, set it where `UnlockedKey()` drops an expired key, and make `Lock()` raise `Changed` when it held a key **or** `_expiredUnannounced` was set (clearing the flag). Test in `CredentialVaultTests`:

```csharp
        [Fact]
        public void Lock_announces_an_unlock_that_ran_out_unnoticed()
        {
            var vault = Created();
            vault.Unlock(Pin);
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            _clock.Advance(300);
            Assert.False(vault.IsUnlocked);   // dropped quietly by the getter
            Assert.Equal(0, changed);

            vault.Lock();
            vault.Lock();
            Assert.Equal(1, changed);
        }
```

- [ ] **Step 4: The workspace carries the vault** — in `PadWorkspaceOptions`:

```csharp
        /// <summary>The credential vault MicaPad's windows use; null leaves the credential commands disabled. The app passes its one vault; tests pass their own.</summary>
        public CredentialVault? Vault { get; init; }
```

In `PadWorkspace` (constructor stores it; properties next to `Store`):

```csharp
        /// <summary>The credential vault, shared by every window and by Settings; null when the app gave none.</summary>
        public CredentialVault? Vault { get; }

        /// <summary>Whether a window already told the user the vault was moved aside: once per run.</summary>
        public bool VaultNoticeShown { get; set; }
```

`PadTestEnv` gains `public CredentialVault Vault { get; }`, created before the workspaces as `new CredentialVault(Path.Combine(Store.Root, CredentialVault.FileName), () => Clock.UtcNow, rounds: 1000, warn: _ => { })`, and every workspace it builds gets `Vault = Vault` in its options. (`Clock` must be initialised before `Vault`; it already is a property initialiser.)

- [ ] **Step 5: `Pad/VaultSession.cs`**

```csharp
using System;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Win32;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Locks the credential vault when it should not stay open: Windows locks or disconnects the
    /// session, the PC goes to sleep, or the unlock's five minutes run out. One per process, started
    /// by the app. SystemEvents raise on their own thread, so every lock is posted to the UI thread
    /// (<c>post</c>), where the vault's Changed handlers expect to run.
    /// </summary>
    internal sealed class VaultSession : IDisposable
    {
        private readonly CredentialVault _vault;
        private readonly Func<DateTime> _utcNow;
        private readonly Action<Action> _post;
        private DispatcherTimer? _timer;
        private bool _started;

        public VaultSession(CredentialVault vault, Func<DateTime> utcNow, Action<Action> post)
        {
            _vault = vault;
            _utcNow = utcNow;
            _post = post;
            _vault.Changed += OnVaultChanged;
        }

        /// <summary>Listens to Windows. Not in tests: they call the handlers directly.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            SystemEvents.SessionSwitch += OnSystemSessionSwitch;
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        }

        /// <summary>How long until the unlock ends, or null while locked.</summary>
        internal TimeSpan? ExpiryIn => _vault.UnlockedUntilUtc is DateTime until ? until - _utcNow() : null;

        internal void OnSessionSwitch(SessionSwitchReason reason)
        {
            if (reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                _post(_vault.Lock);
        }

        internal void OnPowerModeChanged(PowerModes mode)
        {
            if (mode == PowerModes.Suspend) _post(_vault.Lock);
        }

        /// <summary>The timer fired: lock if the unlock is really over (the vault checks the clock itself), else wait the rest.</summary>
        internal void OnExpiryTimer()
        {
            _timer?.Stop();
            if (_vault.IsUnlocked) Arm();
            else _vault.Lock();
        }

        public void Dispose()
        {
            _vault.Changed -= OnVaultChanged;
            _timer?.Stop();
            if (!_started) return;
            SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        }

        private void OnSystemSessionSwitch(object? sender, SessionSwitchEventArgs e) => OnSessionSwitch(e.Reason);

        private void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => OnPowerModeChanged(e.Mode);

        private void OnVaultChanged(object? sender, EventArgs e) => _post(Arm);

        private void Arm()
        {
            if (ExpiryIn is not TimeSpan left)
            {
                _timer?.Stop();
                return;
            }

            if (Dispatcher.FromThread(System.Threading.Thread.CurrentThread) == null) return;   // tests without a dispatcher drive OnExpiryTimer themselves
            _timer ??= new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Interval = left > TimeSpan.Zero ? left + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50);
            _timer.Stop();
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                OnExpiryTimer();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("pad", "Locking the credential vault failed (" + ex.GetType().Name + ")");
            }
        }
    }
}
```

- [ ] **Step 6: The App** — next to `s_padStore`:

```csharp
        private static Kil0bitSystemMonitor.Services.Pad.CredentialVault? s_padVault;
        private static Kil0bitSystemMonitor.Pad.VaultSession? s_vaultSession;

        /// <summary>
        /// MicaPad's credential vault (<c>vault.bin</c> beside the notes), one for the process: every
        /// MicaPad window and Settings share it and its unlock. Created on first use; loaded by
        /// whoever needs it (<see cref="Kil0bitSystemMonitor.Services.Pad.CredentialVault.EnsureLoaded"/>).
        /// </summary>
        internal static Kil0bitSystemMonitor.Services.Pad.CredentialVault PadVault
        {
            get
            {
                if (s_padVault != null) return s_padVault;
                s_padVault = new Kil0bitSystemMonitor.Services.Pad.CredentialVault(
                    System.IO.Path.Combine(Kil0bitSystemMonitor.Services.Pad.NoteStore.DefaultRoot, Kil0bitSystemMonitor.Services.Pad.CredentialVault.FileName),
                    () => DateTime.UtcNow);
                var dispatcher = Current.Dispatcher;
                s_vaultSession = new Kil0bitSystemMonitor.Pad.VaultSession(s_padVault, () => DateTime.UtcNow, action => dispatcher.BeginInvoke(action));
                s_vaultSession.Start();
                return s_padVault;
            }
        }
```

In `OpenPad`, the workspace options get `Vault = PadVault`. In `OnExit`, after `FlushPad()`: `s_vaultSession?.Dispose(); s_padVault?.Lock();` (Task 8 adds the clipboard clear here).

- [ ] **Step 7: Run the focused tests, then the full suite.**

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/CredentialVault.cs Services/Pad/PadWorkspace.cs Pad/VaultSession.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadTestEnv.cs tests/Kil0bitSystemMonitor.Tests/CredentialVaultTests.cs tests/Kil0bitSystemMonitor.Tests/VaultSessionTests.cs
git commit -m "feat(pad): one credential vault for the process, handed to MicaPad; it locks when Windows locks or sleeps and after 5 minutes" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 8: Pill colors, SecretPillGenerator, SecretClipboard

**Files:**
- Modify: `Services/Pad/PadPalette.cs` (four colors in both palettes and in `Resources()`), `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs` (contrast rules), `App.xaml.cs` (clipboard clear at exit)
- Create: `Pad/SecretPillGenerator.cs`, `Pad/SecretClipboard.cs`
- Test: create `tests/Kil0bitSystemMonitor.Tests/SecretPillTests.cs`, `tests/Kil0bitSystemMonitor.Tests/SecretClipboardTests.cs`

**Interfaces:**
- Consumes: `SecretTokens.Find`, `CredentialInfo`, `PadPalette`, `PadThemeApplier.ToBrush`.
- Produces:
  - `PadPalette.PillBack`, `PillBorder`, `PillText`, `PillMissingText` (opaque).
  - `internal sealed class SecretPillGenerator : VisualLineElementGenerator` — `SecretPillGenerator(Func<string, CredentialInfo?> find, Func<PadPalette> palette, Func<double> fontSize)`; `internal static string LabelOf(CredentialInfo? info)` ("Credential" for an empty label, "Missing credential" for null); `internal static string ToolTipOf(string id, CredentialInfo? info)`.
  - `internal static class SecretClipboard` — `const int ClearSeconds = 30`; `static void Copy(string value)`; `static void ClearIfStillOurs()`; seams `internal static Func<IDataObject, bool> SetData`, `internal static Func<uint> SequenceNumber`, `internal static Action Clear`, `internal static Action<TimeSpan, Action> After` (schedules the clear; the app uses a DispatcherTimer).

- [ ] **Step 1: Failing tests**

Palette (`PadPaletteTests.ContrastRules` gains): `("PillText", "PillBack", 4.5), ("PillMissingText", "PillBack", 4.5), ("PillText", "Background", 4.5)`.

`SecretPillTests.cs`:

```csharp
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>References drawn as pills: one element per reference, its label, its colors.</summary>
    public class SecretPillTests
    {
        private static readonly CredentialInfo Bank = new("K7Q2M9XD", "Bank", DateTime.UtcNow, null);

        [Fact]
        public void Labels_and_tooltips()
        {
            Assert.Equal("Bank", SecretPillGenerator.LabelOf(Bank));
            Assert.Equal("Credential", SecretPillGenerator.LabelOf(Bank with { Label = "" }));
            Assert.Equal("Missing credential", SecretPillGenerator.LabelOf(null));
            Assert.Equal("Stored credential K7Q2M9XD \u2014 right-click for options", SecretPillGenerator.ToolTipOf("K7Q2M9XD", Bank));
            Assert.Equal("No stored credential K7Q2M9XD", SecretPillGenerator.ToolTipOf("K7Q2M9XD", null));
        }

        [Fact]
        public void Each_reference_becomes_one_element_covering_all_of_it() => UiThread.Run(() =>
        {
            var palette = PadPalette.Dark;
            var editor = new TextEditor { Text = "a {{secret:K7Q2M9XD}} b {{secret:00000000}}" };
            var generator = new SecretPillGenerator(id => id == Bank.Id ? Bank : null, () => palette, () => 14);
            editor.TextArea.TextView.ElementGenerators.Add(generator);
            editor.Measure(new Size(800, 200));
            editor.Arrange(new Rect(0, 0, 800, 200));
            editor.TextArea.TextView.EnsureVisualLines();

            var line = editor.TextArea.TextView.VisualLines.Single();
            var pills = line.Elements.OfType<InlineObjectElement>().ToList();

            Assert.Equal(2, pills.Count);
            Assert.All(pills, p => Assert.Equal(SecretTokens.Format("K7Q2M9XD").Length, p.DocumentLength));
            Assert.Contains("Bank", Text(pills[0].Element));
            Assert.Contains("Missing credential", Text(pills[1].Element));
            Assert.Equal(PadThemeApplier.ToBrush(palette.PillBack).ToString(), ((Border)pills[0].Element).Background.ToString());

            palette = PadPalette.Light;   // a theme switch redraws in the new palette
            editor.TextArea.TextView.Redraw();
            editor.TextArea.TextView.EnsureVisualLines();
            var redrawn = editor.TextArea.TextView.VisualLines.Single().Elements.OfType<InlineObjectElement>().First();
            Assert.Equal(PadThemeApplier.ToBrush(PadPalette.Light.PillBack).ToString(), ((Border)redrawn.Element).Background.ToString());
        });

        private static string Text(UIElement element) =>
            string.Concat(((Panel)((Border)element).Child).Children.OfType<TextBlock>().Select(t => t.Text));
    }
}
```

`SecretClipboardTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A copied value stays out of clipboard history and is cleared after 30 s unless something else was copied.</summary>
    public class SecretClipboardTests
    {
        private sealed class Fake : IDisposable
        {
            private readonly Func<IDataObject, bool> _set = SecretClipboard.SetData;
            private readonly Func<uint> _seq = SecretClipboard.SequenceNumber;
            private readonly Action _clear = SecretClipboard.Clear;
            private readonly Action<TimeSpan, Action> _after = SecretClipboard.After;

            public List<IDataObject> Set { get; } = new();
            public uint Sequence { get; set; } = 7;
            public int Cleared { get; private set; }
            public List<(TimeSpan Delay, Action Then)> Scheduled { get; } = new();

            public Fake()
            {
                SecretClipboard.SetData = data => { Set.Add(data); Sequence++; return true; };
                SecretClipboard.SequenceNumber = () => Sequence;
                SecretClipboard.Clear = () => Cleared++;
                SecretClipboard.After = (delay, then) => Scheduled.Add((delay, then));
            }

            public void Dispose()
            {
                SecretClipboard.SetData = _set;
                SecretClipboard.SequenceNumber = _seq;
                SecretClipboard.Clear = _clear;
                SecretClipboard.After = _after;
            }
        }

        [Fact]
        public void A_copy_is_kept_out_of_history_and_cloud_sync() => UiThread.Run(() =>
        {
            using var fake = new Fake();

            Assert.True(SecretClipboard.Copy("hunter2"));

            var data = Assert.Single(fake.Set);
            Assert.Equal("hunter2", data.GetData(DataFormats.UnicodeText));
            Assert.True(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"));
            Assert.Equal(new byte[4], ((MemoryStream)data.GetData("CanIncludeInClipboardHistory")).ToArray());
            Assert.Equal(new byte[4], ((MemoryStream)data.GetData("CanUploadToCloudClipboard")).ToArray());
            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(fake.Scheduled).Delay);
        });

        [Fact]
        public void After_30_seconds_it_is_cleared_if_nothing_else_was_copied() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");

            fake.Scheduled[0].Then();

            Assert.Equal(1, fake.Cleared);
        });

        [Fact]
        public void Something_copied_since_is_left_alone() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");
            fake.Sequence++;   // the user copied something else

            fake.Scheduled[0].Then();
            SecretClipboard.ClearIfStillOurs();

            Assert.Equal(0, fake.Cleared);
        });

        [Fact]
        public void At_exit_a_value_still_on_the_clipboard_is_cleared() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");

            SecretClipboard.ClearIfStillOurs();
            fake.Scheduled[0].Then();   // the timer firing later finds nothing of ours

            Assert.Equal(1, fake.Cleared);
        });

        [Fact]
        public void A_busy_clipboard_reports_failure() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.SetData = _ => false;

            Assert.False(SecretClipboard.Copy("hunter2"));
            Assert.Empty(fake.Scheduled);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Palette colors** — properties (with a summary each) next to `Occurrence`, both palettes, and `Pair(...)` lines in `Resources()`:

| Color | Dark | Light |
|---|---|---|
| `PillBack` | `#12303A` | `#DCEFF1` |
| `PillBorder` | `#2A6A75` | `#9FD0D6` |
| `PillText` | `#3FD2E4` | `#06707C` |
| `PillMissingText` | `#9A9AA3` | `#5C5C66` |

If a contrast rule fails, adjust that value (keep the hue) and say so in the report.

- [ ] **Step 4: `Pad/SecretPillGenerator.cs`**

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Draws each <c>{{secret:ID}}</c> reference as one pill: a lock and the credential's label, or
    /// "Missing credential" when the vault has no such id. The pill stands for the whole reference,
    /// so the caret steps over it and Backspace or Delete next to it removes it whole; copying still
    /// copies the reference text. Lines longer than 4,000 characters are not scanned (as links).
    /// </summary>
    internal sealed class SecretPillGenerator : VisualLineElementGenerator
    {
        private const int MaxLineLength = 4000;
        private readonly Func<string, CredentialInfo?> _find;
        private readonly Func<PadPalette> _palette;
        private readonly Func<double> _fontSize;

        public SecretPillGenerator(Func<string, CredentialInfo?> find, Func<PadPalette> palette, Func<double> fontSize)
        {
            _find = find;
            _palette = palette;
            _fontSize = fontSize;
        }

        /// <summary>What a pill says.</summary>
        internal static string LabelOf(CredentialInfo? info) =>
            info == null ? "Missing credential" : info.Label.Length == 0 ? "Credential" : info.Label;

        /// <summary>A pill's tooltip.</summary>
        internal static string ToolTipOf(string id, CredentialInfo? info) =>
            info == null ? "No stored credential " + id : "Stored credential " + id + " \u2014 right-click for options";

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(startOffset);
            if (line.Length > MaxLineLength) return -1;

            string text = document.GetText(line.Offset, line.Length);
            foreach (var reference in SecretTokens.Find(text))
            {
                int offset = line.Offset + reference.Offset;
                if (offset >= startOffset) return offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(offset);
            string text = document.GetText(line.Offset, line.Length);
            foreach (var reference in SecretTokens.Find(text))
            {
                if (line.Offset + reference.Offset != offset) continue;
                return new InlineObjectElement(reference.Length, Pill(reference.Id));
            }
            return null;
        }

        private UIElement Pill(string id)
        {
            var palette = _palette();
            var info = _find(id);
            double size = Math.Max(8, _fontSize() * 0.9);
            var foreground = PadThemeApplier.ToBrush(info == null ? palette.PillMissingText : palette.PillText);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "\uE72E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size * 0.85,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            row.Children.Add(new TextBlock { Text = LabelOf(info), FontSize = size, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });

            return new Border
            {
                Child = row,
                Background = PadThemeApplier.ToBrush(palette.PillBack),
                BorderBrush = PadThemeApplier.ToBrush(palette.PillBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 6, 0),
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = ToolTipOf(id, info),
                Cursor = System.Windows.Input.Cursors.Arrow,
            };
        }
    }
}
```

- [ ] **Step 5: `Pad/SecretClipboard.cs`**

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a credential's value on the clipboard the way password managers do: marked so Windows
    /// clipboard history (Win+V) and cloud clipboard skip it, and cleared after
    /// <see cref="ClearSeconds"/> seconds — but only if the clipboard still holds it, judged by the
    /// clipboard sequence number, so something the user copied since is never wiped. At exit a value
    /// still on the clipboard is cleared too.
    /// </summary>
    internal static class SecretClipboard
    {
        /// <summary>How long a copied value stays.</summary>
        public const int ClearSeconds = 30;

        private static uint? s_ours;

        /// <summary>Puts data on the clipboard; false when it stays busy. Tests replace it.</summary>
        internal static Func<IDataObject, bool> SetData { get; set; } = data =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (COMException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            return false;
        };

        /// <summary>The clipboard's change counter. Tests replace it.</summary>
        internal static Func<uint> SequenceNumber { get; set; } = GetClipboardSequenceNumber;

        /// <summary>Empties the clipboard. Tests replace it.</summary>
        internal static Action Clear { get; set; } = () =>
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
                // Busy: the value stays; nothing better to do.
            }
        };

        /// <summary>Runs an action after a delay on the UI thread. Tests replace it.</summary>
        internal static Action<TimeSpan, Action> After { get; set; } = (delay, then) =>
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                then();
            };
            timer.Start();
        };

        /// <summary>Copies <paramref name="value"/>; false when the clipboard stayed busy.</summary>
        public static bool Copy(string value)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, value);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[1]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            if (!SetData(data)) return false;

            uint ours = SequenceNumber();
            s_ours = ours;
            After(TimeSpan.FromSeconds(ClearSeconds), () => ClearIf(ours));
            return true;
        }

        /// <summary>Clears the clipboard if it still holds the last value copied here (at exit).</summary>
        public static void ClearIfStillOurs()
        {
            if (s_ours is uint ours) ClearIf(ours);
        }

        private static void ClearIf(uint ours)
        {
            if (s_ours != ours) return;
            s_ours = null;
            if (SequenceNumber() == ours) Clear();
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
```

In `App.OnExit`, after `s_padVault?.Lock();`: `Kil0bitSystemMonitor.Pad.SecretClipboard.ClearIfStillOurs();` (inside the existing try).

- [ ] **Step 6: Run the focused tests (`SecretPillTests|SecretClipboardTests|PadPaletteTests`), then the full suite.**

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/PadPalette.cs Pad/SecretPillGenerator.cs Pad/SecretClipboard.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs tests/Kil0bitSystemMonitor.Tests/SecretPillTests.cs tests/Kil0bitSystemMonitor.Tests/SecretClipboardTests.cs
git commit -m "feat(pad): references drawn as pills; a clipboard for secrets that skips history and clears itself" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 9: VaultCard — every vault question in one overlay card

**Files:**
- Create: `Pad/VaultCard.xaml`, `Pad/VaultCard.xaml.cs`
- Test: create `tests/Kil0bitSystemMonitor.Tests/VaultCardTests.cs`

**Interfaces:**
- Consumes: `CredentialVault` (`IsValidPin`, `UnlockResult`, `UnlockOutcome`), `CredentialVault.MaxLabelLength`.
- Produces: `internal partial class VaultCard : UserControl` with:
  - `internal Func<DateTime> UtcNow { get; set; }` (tests use a fake clock), `internal event EventHandler? Closed`.
  - `void ShowCreatePin(Action<string> create)` — calls `create(pin)` when both boxes hold the same valid PIN.
  - `void ShowEnterPin(string purpose, Func<string, UnlockResult> unlock, Action unlocked)` — calls `unlock(pin)`; on `Unlocked` closes and calls `unlocked()`; otherwise shows the error line.
  - `void ShowChangePin(Func<string, string, UnlockResult> change)` — current/new/confirm; closes on `Unlocked`.
  - `void ShowStore(int length, int copies, Action<string?> store)`.
  - `void ShowRename(string current, Action<string?> rename)`.
  - `void ShowConfirm(string title, string message, string confirmLabel, Action confirm)`.
  - `void ShowReveal(string title, string value, Action copy)` — hides after 30 s.
  - `void Hide()`; `bool IsOpen`; `string Mode` (for tests: "CreatePin", "EnterPin", "ChangePin", "Store", "Rename", "Confirm", "Reveal", or "" when closed).
  - `internal static string WaitText(TimeSpan left)` → `"Too many wrong PINs. Try again in m:ss."`; `internal static string TriesText(int left)` → `"Wrong PIN. 1 more try before a wait."` / `"Wrong PIN. {n} more tries before a wait."`.
  - Named parts for tests: `TitleText`, `BodyText`, `ErrorText`, `PinBox`, `PinBox2`, `PinBox3`, `LabelBox`, `ValueBox`, `PrimaryButton`, `SecondaryButton`, `CopyButton`.

Card text (exact):

| Mode | Title | Body | Fields | Primary | Secondary |
|---|---|---|---|---|---|
| CreatePin | Create a PIN for your credentials | You will need it to see a stored credential again. Use 6 to 12 digits; a longer PIN is stronger. If you forget it, the stored credentials cannot be recovered. | PIN, Type it again | Create PIN | Cancel |
| EnterPin | Enter your PIN | the purpose passed in, e.g. "To reveal \u201CBank\u201D." | PIN | Unlock | Cancel |
| ChangePin | Change PIN | Use 6 to 12 digits; a longer PIN is stronger. | Current PIN, New PIN, Type the new PIN again | Change PIN | Cancel |
| Store | Store as credential | It will be replaced by a reference. You need your PIN to see it again. — then "Secret: \u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022 ({n} characters)" and, when copies > 1, "Appears {copies} times in this note \u2014 every copy will be masked." | Label (optional) | Store | Cancel |
| Rename | Rename credential | (none) | Label | Rename | Cancel |
| Confirm | the title passed in | the message passed in | (none) | the label passed in (red: `Pad.AlertRed`) | Cancel |
| Reveal | the title passed in | "Hides in 30 s." | the value, read-only, selectable, monospace, wrapping | Copy | Hide |

Errors: an invalid PIN → "Use 6 to 12 digits."; two new PINs that differ → "The two PINs differ."; `WrongPin` without a wait → `TriesText`; `WrongPin` with `WaitUntilUtc`, or `Waiting` → `WaitText`, updated every second by a DispatcherTimer, with the PIN boxes and Primary disabled until the wait ends; `NoVault` → "There is no vault yet.".

Behavior: PIN boxes are `PasswordBox` with `MaxLength="12"`; typed and pasted characters other than ASCII digits are refused (`PreviewTextInput` + `DataObject.Pasting`). Enter = Primary, Escape = Secondary (or Hide). The card takes keyboard focus when shown (the first box), and closes (as Cancel) when focus leaves it to something outside the card (`IsKeyboardFocusWithinChanged` → false). Every PasswordBox is cleared on close; the reveal's `ValueBox.Text` is set to "" on close. Each Show* replaces whatever the card was showing (the previous mode's callbacks are dropped, never called). The 30-second reveal timer and the wait timer stop on close. Colors are the window's `Pad.*` resources (`Pad.Popup`, `Pad.PopupBorder`, `Pad.WindowText`, `Pad.Muted`, `Pad.Accent`, `Pad.AlertRed`, `Pad.Background` for the backdrop at 60% opacity); layout: a 380 px wide card centered over a translucent backdrop that covers the editor area and swallows mouse clicks.

- [ ] **Step 1: Failing tests** — `VaultCardTests.cs` (helpers `Click(Button)` raise `ButtonBase.ClickEvent`; `Key(UIElement, Key)` raises `KeyDown` with a `KeyEventArgs` on `Keyboard.PrimaryDevice` and a `PresentationSource`-less target via `new KeyEventArgs(Keyboard.PrimaryDevice, new HwndSource(0,0,0,0,0,"t",IntPtr.Zero), 0, key) { RoutedEvent = Keyboard.KeyDownEvent }` — if that is not possible without showing a window, call the card's internal `OnPrimary()`/`OnSecondary()` instead):

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The vault card: what each question shows, what it accepts, what it refuses.</summary>
    public class VaultCardTests
    {
        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        [Fact]
        public void Creating_a_pin_needs_the_same_valid_pin_twice() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            var created = new List<string>();
            card.ShowCreatePin(created.Add);
            Assert.Equal("Create a PIN for your credentials", card.TitleText.Text);

            card.PinBox.Password = "12345";
            card.PinBox2.Password = "12345";
            Click(card.PrimaryButton);
            Assert.Equal("Use 6 to 12 digits.", card.ErrorText.Text);

            card.PinBox.Password = "123456";
            card.PinBox2.Password = "123457";
            Click(card.PrimaryButton);
            Assert.Equal("The two PINs differ.", card.ErrorText.Text);

            card.PinBox2.Password = "123456";
            Click(card.PrimaryButton);
            Assert.Equal(new[] { "123456" }, created);
            Assert.False(card.IsOpen);
            Assert.Equal("", card.PinBox.Password);
        });

        [Fact]
        public void Wrong_pins_show_the_tries_left_then_a_countdown() => UiThread.Run(() =>
        {
            var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var card = new VaultCard { UtcNow = () => now };
            var results = new Queue<UnlockResult>(new[]
            {
                new UnlockResult(UnlockOutcome.WrongPin, 1),
                new UnlockResult(UnlockOutcome.WrongPin, 0, now.AddSeconds(30)),
            });
            bool unlocked = false;
            card.ShowEnterPin("To reveal \u201CBank\u201D.", _ => results.Dequeue(), () => unlocked = true);

            card.PinBox.Password = "000000";
            Click(card.PrimaryButton);
            Assert.Equal("Wrong PIN. 1 more try before a wait.", card.ErrorText.Text);
            Assert.True(card.IsOpen);

            card.PinBox.Password = "000000";
            Click(card.PrimaryButton);
            Assert.Equal("Too many wrong PINs. Try again in 0:30.", card.ErrorText.Text);
            Assert.False(card.PinBox.IsEnabled);
            Assert.False(card.PrimaryButton.IsEnabled);
            Assert.False(unlocked);
        });

        [Fact]
        public void The_right_pin_closes_the_card_and_continues() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            bool unlocked = false;
            card.ShowEnterPin("To reveal it.", pin => new UnlockResult(pin == "246810" ? UnlockOutcome.Unlocked : UnlockOutcome.WrongPin, 4), () => unlocked = true);

            card.PinBox.Password = "246810";
            Click(card.PrimaryButton);

            Assert.True(unlocked);
            Assert.False(card.IsOpen);
        });

        [Theory]
        [InlineData(30, "Too many wrong PINs. Try again in 0:30.")]
        [InlineData(61, "Too many wrong PINs. Try again in 1:01.")]
        [InlineData(900, "Too many wrong PINs. Try again in 15:00.")]
        public void Wait_text(int seconds, string expected) => Assert.Equal(expected, VaultCard.WaitText(TimeSpan.FromSeconds(seconds)));

        [Fact]
        public void Store_shows_the_length_and_the_copies_never_the_secret() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            string? stored = "unset";
            card.ShowStore(12, 3, label => stored = label);

            Assert.Contains("(12 characters)", card.BodyText.Text);
            Assert.Contains("Appears 3 times in this note \u2014 every copy will be masked.", card.BodyText.Text);
            card.LabelBox.Text = "  Bank  ";
            Click(card.PrimaryButton);

            Assert.Equal("Bank", stored);
            Assert.Equal(CredentialVault.MaxLabelLength, card.LabelBox.MaxLength);
        });

        [Fact]
        public void One_copy_says_nothing_about_copies() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            card.ShowStore(8, 1, _ => { });

            Assert.DoesNotContain("Appears", card.BodyText.Text);
        });

        [Fact]
        public void A_reveal_shows_the_value_and_hides_by_itself() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            int copied = 0;
            card.ShowReveal("Bank", "hunter2", () => copied++);

            Assert.Equal("hunter2", card.ValueBox.Text);
            Assert.True(card.ValueBox.IsReadOnly);
            Click(card.CopyButton);
            Assert.Equal(1, copied);

            card.OnRevealTimer();   // the 30-second timer
            Assert.False(card.IsOpen);
            Assert.Equal("", card.ValueBox.Text);
        });

        [Fact]
        public void A_new_question_drops_the_previous_one() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            bool firstCalled = false;
            card.ShowRename("Old", _ => firstCalled = true);
            card.ShowConfirm("Delete credential", "Delete it?", "Delete", () => { });

            Assert.Equal("Confirm", card.Mode);
            Click(card.SecondaryButton);
            Assert.False(firstCalled);
            Assert.False(card.IsOpen);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement `VaultCard.xaml` and `VaultCard.xaml.cs`** to the table and behavior above. Structure: a root `Grid` (Visibility Collapsed when closed) holding a backdrop `Border` (`Pad.Background`, Opacity 0.6) and a centered card `Border` (`Pad.Popup` background, `Pad.PopupBorder` 1 px, CornerRadius 8, Padding 20, Width 380) with a `StackPanel`: `TitleText` (16, SemiBold, `Pad.WindowText`), `BodyText` (12.5, wrap, `Pad.Muted`), field captions (12, `Pad.Muted`) + `PinBox`/`PinBox2`/`PinBox3`/`LabelBox`/`ValueBox` (each collapsed unless its mode uses it; `ValueBox` uses `FontFamily="Cascadia Mono, Consolas"`, `TextWrapping="Wrap"`, `MaxHeight="160"`, vertical scroll), `ErrorText` (12, `Pad.AlertRed`, collapsed when empty), and a right-aligned button row `CopyButton` (Reveal only), `PrimaryButton` (accent style), `SecondaryButton`. Expose `internal void OnRevealTimer()` and `internal void OnWaitTimer()` for tests; the real DispatcherTimers call them. Trim the label and pass null for an empty one. Never log or put a PIN or value anywhere but the boxes.

- [ ] **Step 4: Run the tests, then the full suite.**

- [ ] **Step 5: Commit**

```bash
git add Pad/VaultCard.xaml Pad/VaultCard.xaml.cs tests/Kil0bitSystemMonitor.Tests/VaultCardTests.cs
git commit -m "feat(pad): VaultCard, one overlay card for PINs, labels, confirmations and revealed values" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 10: The window — Store as credential, the pill menu, the moved-vault notice

**Files:**
- Create: `Pad/MicaPadWindow.Vault.cs` (partial class; keeps the 2,470-line window file from growing)
- Modify: `Pad/MicaPadWindow.xaml` (a `local:VaultCard x:Name="VaultCard"` over the editor area: `Grid.Row="3"`, spanning the editor grid, last child so it is on top), `Pad/MicaPadWindow.xaml.cs` (constructor: `ConfigureVault()`; `FillEditorMenu`: the Store item and the pill menu; `ApplyTheme`: redraw pills; `Open`: `ShowVaultMovedNotice()`; `Detach`/close: unhook the vault)
- Test: create `tests/Kil0bitSystemMonitor.Tests/PadVaultTests.cs`

**Interfaces:**
- Consumes: Task 7 (`_workspace.Vault`, `EnsureLoaded`, `VaultNoticeShown`), Task 8 (`SecretPillGenerator`, `SecretClipboard`), Task 9 (`VaultCard`), Part 1–2 (`CredentialVault`, `SecretTokens`, `SecretScrubber`, `NoteStore.ScrubSnapshots`).
- Produces (internal, for tests): `bool CanStoreSelection()`, `void StoreSelection()`, `SecretReference? ReferenceAt(int offset)`, `void FillPillMenu(ContextMenu menu, SecretReference reference)`, `void RevealCredential(string id)`, `void CopyCredential(string id)`, `void UnmaskCredential(SecretReference reference)`, `void DeleteCredential(string id)`, `void RenameCredential(string id)`, `void ShowVaultMovedNotice()`, `void ShowChangePin()`.

Behavior (spec 3.1–3.5 with rulings R5, R8):

1. `ConfigureVault()` (constructor): if `_workspace.Vault` is null, nothing; else add a `SecretPillGenerator(id => vault.IsLoaded ? vault.Find(id) : null, () => _palette, () => Editor.FontSize)` to **both** `Editor` and `PreviewEditor`; subscribe `vault.Changed` → `Dispatcher.BeginInvoke` redraw both TextViews; the window's close path (where it detaches documents) unsubscribes. `ApplyTheme` redraws both TextViews after the palette changes (the generator reads the palette at draw time). `VaultCard.UtcNow` stays real.
2. `CanStoreSelection()`: the vault exists in the workspace and `EnsureLoaded()` is true, the selection is in `Editor` (not the preview), is a simple (not rectangular) selection, has 4 to 65,536 characters, and `!SecretTokens.Contains(selectedText)`. `FillEditorMenu` for `Editor` adds, right after the edit group's "Copy as RTF" (or after "Copy" when absent), `Item("Store as credential\u2026", null, StoreSelection, CanStoreSelection(), icon: "\uE72E")`.
3. `StoreSelection()`: capture `note = _shown`, `document = Editor.Document`, `value = Editor.SelectedText`. If `!vault.Exists` → `VaultCard.ShowCreatePin(pin => { vault.Create(pin); ShowStoreCard(...); })`; else `ShowStoreCard`. `ShowStoreCard` → `VaultCard.ShowStore(value.Length, SecretScrubber.Count(document.Text, value), label => CompleteStore(note, document, value, label))`.
4. `CompleteStore`: if `_shown` is no longer `note` or its document is no longer `document`, stop with status "The note changed; nothing was stored." Otherwise:
   1. `id = vault.Add(value, label, note.Id)`; on `IOException`, `UnauthorizedAccessException` or `CryptographicException`: status "Could not store the credential: the vault could not be saved." and stop (the text is untouched).
   2. `reference = SecretTokens.Format(id)`; replace every exact occurrence of `value` in `document` from the last to the first, inside `document.BeginUpdate()`/`EndUpdate()`; then `document.UndoStack.ClearAll()`.
   3. `_workspace.FlushPending(); _workspace.FlushWrites(TimeSpan.FromSeconds(2));` then `int left = _workspace.Store.ScrubSnapshots(note.Id, value, reference);`.
   4. Status: `left == 0` → "Stored as {id}"; `left == 1` → "Stored as {id} \u2014 1 older version still holds it"; else "Stored as {id} \u2014 {left} older versions still hold it".
   5. If the history pane is open, refresh its list (call the method the window already uses after a snapshot).
5. `ReferenceAt(offset)`: the reference on the offset's line with `Offset <= offset < Offset + Length`, or whose `Offset == offset`.
6. Context menu (R8): in `RefreshEditorMenu` (and the preview's), find the reference at the mouse position (`Editor.GetPositionFromPoint(Mouse.GetPosition(Editor))` → offset) when the menu was opened by the mouse, else at `Editor.CaretOffset`; if there is one, call `FillPillMenu` instead of `FillEditorMenu`. Pill menu items, in order, each through `Guard` like every other item: Reveal\u2026 (`\uE7B3`), Copy secret (`\uE8C8`), Rename label\u2026 (`\uE8AC`), Unmask (`\uE785`), Delete credential\u2026 (`\uE74D`), separator, Copy reference (`\uE71B`), and Lock now (`\uE72E`) only while `vault.IsUnlocked`. A missing reference (`vault.Find(id) == null`) gets only Copy reference. In the read-only preview: Reveal\u2026, Copy secret, Copy reference only.
7. `WithUnlocked(string purpose, Action then)`: if `vault.IsUnlocked`, `then()`; else `VaultCard.ShowEnterPin(purpose, vault.Unlock, then)`. Purposes: "To reveal \u201C{label}\u201D.", "To copy \u201C{label}\u201D.", "To unmask \u201C{label}\u201D.", "To delete \u201C{label}\u201D." (label via `SecretPillGenerator.LabelOf`).
8. `RevealCredential(id)`: `WithUnlocked` → `VaultCard.ShowReveal(label, vault.Reveal(id), () => CopyCredential(id))`. `CopyCredential(id)`: `WithUnlocked` → `SecretClipboard.Copy(vault.Reveal(id))` → status "Copied \u2014 clears in 30 s" or "Clipboard busy, try again". `UnmaskCredential(reference)`: `WithUnlocked` → replace the reference's range with `vault.Reveal(id)` as a normal (undoable) edit, after checking the document still holds that reference at that offset. `DeleteCredential(id)`: `WithUnlocked` → `VaultCard.ShowConfirm("Delete credential", "Delete \u201C{label}\u201D? Notes that use {id} will show it as missing. This cannot be undone.", "Delete", () => vault.Delete(id))`. `RenameCredential(id)`: `VaultCard.ShowRename(info.Label, label => vault.Rename(id, label))`. Copy reference: the reference text through the window's existing plain-text clipboard path. Lock now: `vault.Lock()` → status "Locked".
   A `CryptographicException` from `Reveal` → status "This credential could not be decrypted." A locked vault while a card waits is fine: the next action asks again.
9. `ShowVaultMovedNotice()` (called from `Open` next to `ShowLockedFolderNotice`): if the vault loaded and `vault.MovedAsideTo` is set and `!_workspace.VaultNoticeShown` → set it and `ShowInfo("MicaPad could not open the credential vault on this Windows account, so it was moved to " + path + ". Stored credentials show as missing.", null, "Show folder", () => ShowInFolder(path))`.
10. `ShowChangePin()` (Task 11 calls it from Settings): `VaultCard.ShowChangePin(vault.ChangePin)` when the vault exists; else status "No PIN set yet".
11. Every vault call that can throw `IOException`/`UnauthorizedAccessException` reports "The credential vault could not be saved right now." in the status bar instead of throwing (the menu `Guard` logs anything else).

- [ ] **Step 1: Failing tests** — `PadVaultTests.cs`, with a `WithWindow` helper copied from `PadWindowTests` (same shape: `PadTestEnv` with `post: dispatcher.BeginInvoke`, `new MicaPadWindow(env.Workspace, config)`, `LoadSession()`, `CloseForExit()` in finally). Swap `SecretClipboard.SetData/After/SequenceNumber/Clear` with fakes (restore in finally) in tests that copy. Tests:

```csharp
        [Fact]
        public void Store_is_offered_only_for_a_plain_selection_of_4_to_65536_characters() => WithWindow((window, env, config) =>
        {
            window.Editor.Text = "pw: hunter2 and {{secret:K7Q2M9XD}}";
            window.Editor.Select(4, 3);
            Assert.False(window.CanStoreSelection());                    // 3 characters
            window.Editor.Select(4, 7);
            Assert.True(window.CanStoreSelection());                     // "hunter2"
            window.Editor.Select(4, window.Editor.Text.Length - 4);
            Assert.False(window.CanStoreSelection());                    // holds a reference
        });

        [Fact]
        public void Storing_masks_every_copy_clears_undo_and_scrubs_the_versions() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            var note = env.Workspace.Open.Single();
            window.Editor.Text = "a=hunter2\nb=hunter2";
            env.Workspace.SnapshotNow(note, SnapshotReason.Pause);       // an older version that holds it
            window.Editor.Select(2, 7);

            window.StoreSelection();
            window.VaultCard.LabelBox.Text = "Bank";
            Click(window.VaultCard.PrimaryButton);

            string id = Assert.Single(env.Vault.Credentials).Id;
            string reference = SecretTokens.Format(id);
            Assert.Equal("a=" + reference + "\nb=" + reference, window.Editor.Text);
            Assert.False(window.Editor.CanUndo);
            env.Workspace.FlushWrites(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("hunter2", env.Store.LoadText(note.Id));
            Assert.All(env.Store.ListSnapshots(note.Id), v => Assert.DoesNotContain("hunter2", env.Store.ReadSnapshot(v)));
            Assert.Equal("Stored as " + id, window.StatusMessage.Text);
            env.Vault.Unlock("246810");
            Assert.Equal("hunter2", env.Vault.Reveal(id));
        });

        [Fact]
        public void The_first_store_creates_the_pin() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();

            window.StoreSelection();
            Assert.Equal("CreatePin", window.VaultCard.Mode);
            window.VaultCard.PinBox.Password = "246810";
            window.VaultCard.PinBox2.Password = "246810";
            Click(window.VaultCard.PrimaryButton);
            Assert.Equal("Store", window.VaultCard.Mode);
            Click(window.VaultCard.PrimaryButton);

            Assert.True(env.Vault.Exists);
            Assert.Single(env.Vault.Credentials);
        });

        [Fact]
        public void A_vault_that_cannot_be_saved_leaves_the_text() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();
            window.StoreSelection();
            using (new FileStream(env.Vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(window.VaultCard.PrimaryButton);
            }

            Assert.Equal("hunter2", window.Editor.Text);
            Assert.Equal("Could not store the credential: the vault could not be saved.", window.StatusMessage.Text);
        });

        [Fact]
        public void The_pill_menu_offers_what_applies() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            string id = env.Vault.Add("hunter2", "Bank", null);
            window.Editor.Text = "x " + SecretTokens.Format(id) + " " + SecretTokens.Format("ZZZZZZZZ");

            var menu = new ContextMenu();
            window.FillPillMenu(menu, window.ReferenceAt(3)!.Value);
            Assert.Equal(new[] { "Reveal\u2026", "Copy secret", "Rename label\u2026", "Unmask", "Delete credential\u2026", "Copy reference" },
                         menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray());

            env.Vault.Unlock("246810");
            window.FillPillMenu(menu, window.ReferenceAt(3)!.Value);
            Assert.Equal("Lock now", menu.Items.OfType<MenuItem>().Last().Header);

            window.FillPillMenu(menu, window.ReferenceAt(window.Editor.Text.Length - 5)!.Value);
            Assert.Equal(new[] { "Copy reference" }, menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray());
        });

        [Fact]
        public void Reveal_asks_for_the_pin_then_shows_the_value() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            string id = env.Vault.Add("hunter2", "Bank", null);

            window.RevealCredential(id);
            Assert.Equal("EnterPin", window.VaultCard.Mode);
            Assert.Equal("To reveal \u201CBank\u201D.", window.VaultCard.BodyText.Text);
            window.VaultCard.PinBox.Password = "246810";
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("Reveal", window.VaultCard.Mode);
            Assert.Equal("hunter2", window.VaultCard.ValueBox.Text);
            window.RevealCredential(id);   // unlocked now: no PIN asked
            Assert.Equal("Reveal", window.VaultCard.Mode);
        });

        [Fact]
        public void Unmask_puts_the_value_back_as_an_undoable_edit() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create("246810");
            string id = env.Vault.Add("hunter2", "Bank", null);
            env.Vault.Unlock("246810");
            window.Editor.Text = "pw: " + SecretTokens.Format(id);

            window.UnmaskCredential(window.ReferenceAt(5)!.Value);

            Assert.Equal("pw: hunter2", window.Editor.Text);
            Assert.True(window.Editor.CanUndo);
            Assert.NotNull(env.Vault.Find(id));   // the vault keeps it
        });

        [Fact]
        public void Copy_secret_uses_the_secret_clipboard() => WithWindow((window, env, config) =>
        {
            // swap SecretClipboard seams for fakes here; restore in finally
            ...
            Assert.Equal("Copied \u2014 clears in 30 s", window.StatusMessage.Text);
        });

        [Fact]
        public void Delete_asks_to_confirm_and_needs_the_pin() => WithWindow((window, env, config) => { ... });

        [Fact]
        public void A_moved_vault_is_announced_once() => WithWindow((window, env, config) => { ... });   // write garbage to vault.bin, EnsureLoaded, ShowVaultMovedNotice twice

        [Fact]
        public void Pills_draw_in_the_preview_too() => WithWindow((window, env, config) => { ... });   // the preview's TextView has a SecretPillGenerator
```

(The implementer writes the `...` bodies to the behavior list above; every assertion names exact text from this task.)

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement** `MicaPadWindow.Vault.cs` and the small hooks in `MicaPadWindow.xaml(.cs)` to the behavior list. Keep `MicaPadWindow.xaml.cs` changes to the named hooks.

- [ ] **Step 4: Run `PadVaultTests|PadWindowTests|PadMenuTests|PadThemeTests`, then the full suite.**

- [ ] **Step 5: Commit**

```bash
git add Pad/MicaPadWindow.Vault.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadVaultTests.cs
git commit -m "feat(pad): Store as credential, pills with a menu to reveal, copy, rename, unmask, delete and lock; versions scrubbed" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 11: Settings → MicaPad credentials group, guide

**Files:**
- Create: `Services/Pad/VaultStatusText.cs`
- Modify: `SettingsWindow.xaml` (a card after the Theme card in `PadSection`), `SettingsWindow.xaml.cs` (`LoadPadSettings` fills it; three handlers), `App.xaml.cs` (`OpenPadVault`), `GUIDE.md` (MicaPad section)
- Test: create `tests/Kil0bitSystemMonitor.Tests/VaultStatusTextTests.cs`; add to `PadThemeTests.cs` (or the test that already reads `SettingsWindow.xaml`) a check that the card exists with its three buttons; a GUIDE check like the existing guide tests (find one with `grep -rn "GUIDE.md" tests/`).

**Interfaces:**
- Produces: `public static string VaultStatusText.Describe(bool loaded, bool exists, int count, bool unlocked)`; `internal static void App.OpenPadVault()` (opens MicaPad and calls `ShowChangePin()` on its current window).

`VaultStatusText.Describe`:

| loaded | exists | count | unlocked | Text |
|---|---|---|---|---|
| false | — | — | — | "The vault could not be read right now." |
| true | false | — | — | "No PIN set yet. Select text in MicaPad and choose Store as credential." |
| true | true | 0 | false | "No credentials stored. Locked." |
| true | true | 1 | false | "1 credential stored. Locked." |
| true | true | n | false | "{n} credentials stored. Locked." |
| true | true | n | true | same count sentence, then "Unlocked." |

Settings card (same look as its neighbors; icon `&#xE72E;`): title "Credentials", status line `PadVaultStatus` (from `Describe` with `App.PadVault`, after `EnsureLoaded()`), and a button row: "Change PIN…" (`App.OpenPadVault()`), "Lock now" (enabled while unlocked; `App.PadVault.Lock()`, then refresh), "Reset vault…" (enabled when a vault exists; a `ContentDialog` like the one at `SettingsWindow.xaml.cs:290`, Title "Reset the credential vault?", Content "Deletes every stored credential. References in your notes will show as missing. This cannot be undone.", PrimaryButtonText "Reset vault", CloseButtonText "Cancel", DefaultButton Close → `App.PadVault.Reset()`, refresh). The status refreshes when the section loads and on the vault's `Changed` while the window is open (unsubscribe on close).

GUIDE.md, in the MicaPad section after "Real files", a subsection "### Credentials and encryption" covering, in plain words: notes are encrypted for this Windows account on this PC (moving PCs: Save As per note); Store as credential (selection of 4+ characters, label, every copy in the note masked, older versions too, cannot be undone with Ctrl+Z); the pill and its menu (Reveal, Copy secret — kept out of Win+V history and cleared after 30 s —, Rename label, Unmask, Delete credential, Copy reference, Lock now); the PIN (6–12 digits, created the first time, unlocks for 5 minutes, locks when Windows locks or sleeps; a wait after 5 wrong PINs, doubling up to 15 minutes; a longer PIN is stronger); Settings → MicaPad → Credentials (Change PIN, Lock now, Reset vault — the way out of a forgotten PIN, deleting every credential); what it does not protect against (a malicious program running as you while MicaPad is open; keyloggers; administrators; plain copies from before this version, such as notes in the Recycle Bin).

- [ ] **Step 1: Failing tests** (`VaultStatusTextTests` as a Theory over the table; the XAML and GUIDE checks).
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the focused tests, then the full suite.**
- [ ] **Step 5: Commit**

```bash
git add Services/Pad/VaultStatusText.cs SettingsWindow.xaml SettingsWindow.xaml.cs App.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/VaultStatusTextTests.cs <the test file with the XAML/GUIDE checks>
git commit -m "feat(pad): Settings > MicaPad > Credentials (Change PIN, Lock now, Reset vault); guide" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

**Controller, after Task 11's review:** final whole-branch review over both plans' range, one fix wave, then deploy for the owner's e2e (spec Testing → Manual 2–5).
