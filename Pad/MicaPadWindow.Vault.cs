using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services.Pad;
using static Kil0bitSystemMonitor.Pad.EditorMenus;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's side of the credential vault (spec Part 3): Store as credential, the pills and
    /// their menu, the vault card's questions and revealed values, and the notice for a vault moved
    /// aside. The vault belongs to the app (<see cref="PadWorkspace.Vault"/>): every window and
    /// Settings share it and its unlock. Nothing here puts a value or a PIN into a status, a log
    /// line or a notice.
    /// </summary>
    public partial class MicaPadWindow
    {
        private const string VaultSaveFailedText = "The credential vault could not be saved right now.";
        private const string DecryptFailedText = "This credential could not be decrypted.";
        private const string NoteChangedText = "The note changed; nothing was stored.";
        private const string GoneText = "This credential is no longer stored.";

        /// <summary>The shortest selection Store as credential takes.</summary>
        private const int MinStoreLength = 4;

        /// <summary>The length of every reference, <c>{{secret:XXXXXXXX}}</c>.</summary>
        private static readonly int ReferenceLength = SecretTokens.Format(new string('0', SecretTokens.IdLength)).Length;

        /// <summary>True while a redraw of the pills is queued, so a burst of vault changes redraws once.</summary>
        private bool _pillRedrawQueued;

        /// <summary>The vault, or null when the app gave MicaPad none.</summary>
        private CredentialVault? Vault => _workspace.Vault;

        /// <summary>
        /// Pills in the note editor and the history preview, repainted whenever the vault changes (a
        /// label, a deletion); and the card hands the keyboard back to the editor when it closes.
        /// </summary>
        private void ConfigureVault()
        {
            VaultCard.Closed += OnVaultCardClosed;
            if (Vault is not { } vault) return;

            foreach (var editor in new[] { Editor, PreviewEditor })
            {
                // First, so a pill wins over any other element starting at the same offset.
                editor.TextArea.TextView.ElementGenerators.Insert(0,
                    new SecretPillGenerator(id => vault.IsLoaded ? vault.Find(id) : null, () => _palette, () => Editor.FontSize));
            }
            vault.Changed += OnVaultChanged;
        }

        /// <summary>
        /// The close path: versions still waiting for a scrub get it now, the shared vault must not
        /// keep this window alive, and an open card empties its boxes.
        /// </summary>
        private void DetachVault()
        {
            FinishPendingScrubs();
            if (Vault is { } vault) vault.Changed -= OnVaultChanged;
            VaultCard.Closed -= OnVaultCardClosed;
            VaultCard.Hide();
        }

        /// <summary>
        /// The vault, read from disk on first use; null when MicaPad has none or it cannot be read
        /// right now (another program holds it: the next use tries again). Pills drawn before the
        /// first read show every credential as missing, so every window draws them again.
        /// </summary>
        private CredentialVault? LoadedVault()
        {
            if (Vault is not { } vault) return null;
            if (vault.IsLoaded) return vault;
            if (!vault.EnsureLoaded()) return null;

            foreach (var window in WindowsOf(_workspace).Append(this).Distinct()) window.RedrawPills();
            return vault;
        }

        private void RedrawPills()
        {
            Editor.TextArea.TextView.Redraw();
            PreviewEditor.TextArea.TextView.Redraw();
        }

        /// <summary>
        /// A label changed, a credential came or went, the vault locked. A revealed value never
        /// outlives the unlock (Lock now, Windows locking, the five minutes running out); the pills
        /// redraw once the change is done.
        /// </summary>
        private void OnVaultChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnVaultChanged(sender, e)));
                return;
            }

            if (VaultCard.Mode == "Reveal" && Vault is { IsUnlocked: false }) VaultCard.Hide();

            if (_pillRedrawQueued) return;
            _pillRedrawQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _pillRedrawQueued = false;
                RedrawPills();
            }));
        }

        // ---- the vault card and the keyboard -------------------------------------------------

        /// <summary>
        /// Called first on every key the window sees. While the vault card is open it has the
        /// keyboard: no window shortcut runs, and a key from elsewhere in the window (focus not in
        /// the card yet) never reaches the note. Escape cancels the card and Enter answers it; any
        /// other key does nothing, except Alt keys, so Alt+F4 still closes the window. A menu (its
        /// own popup window, such as a card box's Copy menu) keeps its keys. True when the window's
        /// shortcuts must leave the key alone.
        /// </summary>
        private bool KeyBelongsToVaultCard(KeyEventArgs e)
        {
            if (!VaultCard.IsOpen) return false;
            if (e.OriginalSource is not DependencyObject source || IsInside(VaultCard, source)) return true;   // the card's own Enter, Escape and PIN digits
            if (e.Key == Key.System) return true;
            if (PresentationSource.FromDependencyObject(source) != PresentationSource.FromVisual(this)) return true;

            e.Handled = true;
            if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return true;
            if (e.Key == Key.Escape) VaultCard.OnSecondary();
            else if (e.Key == Key.Enter) VaultCard.OnPrimary();
            return true;
        }

        /// <summary>
        /// The card closed: the keyboard goes back to the editor it covered. Focus that already went
        /// to something else stays there; a window in the background only notes the editor, which
        /// gets the keyboard when the window comes back.
        /// </summary>
        private void OnVaultCardClosed(object? sender, EventArgs e)
        {
            TextEditor editor = PreviewPanel.Visibility == Visibility.Visible ? PreviewEditor : Editor;
            var focused = (IsActive ? Keyboard.FocusedElement : FocusManager.GetFocusedElement(this)) as DependencyObject;
            if (focused != null && !ReferenceEquals(focused, this) && !IsInside(VaultCard, focused)) return;

            if (IsActive) editor.Focus();
            else FocusManager.SetFocusedElement(this, editor.TextArea);
        }

        private static bool IsInside(DependencyObject ancestor, DependencyObject element)
        {
            for (DependencyObject? d = element; d != null; d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, ancestor)) return true;
            }
            return false;
        }

        /// <summary>
        /// Runs a vault step from a menu or a card answer, never throwing into MicaStats. A vault that
        /// cannot be saved, for a step that reveals a value that does not decrypt, and a credential
        /// deleted (or a vault reset) elsewhere while a card waited are reported in the status bar;
        /// anything else is logged by <see cref="EditorMenus.Guard"/>.
        /// </summary>
        private void VaultStep(string what, Action step, bool reveals = false)
        {
            Guard(what, () =>
            {
                try
                {
                    step();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn(what + ": the credential vault could not be saved (" + ex.GetType().Name + ": " + ex.Message + ")");
                    ShowStatus(VaultSaveFailedText);
                }
                catch (CryptographicException ex) when (reveals)
                {
                    Warn(what + ": a stored credential could not be decrypted (" + ex.GetType().Name + ")");
                    ShowStatus(DecryptFailedText);
                }
                catch (Exception ex) when (ex is KeyNotFoundException || (ex is InvalidOperationException && Vault is { Exists: false }))
                {
                    ShowStatus(GoneText);
                }
            });
        }

        /// <summary>The credential, or null after saying it is gone: deleted, or the vault reset, since the menu or card was shown.</summary>
        private CredentialInfo? Stored(CredentialVault vault, string id)
        {
            var info = vault.Find(id);
            if (info == null) ShowStatus(GoneText);
            return info;
        }

        /// <summary>
        /// A PIN check from the card. Every check saves the try first, so a vault that cannot be
        /// saved checks nothing: the card closes (dropping this answer) and the status bar says why.
        /// Nothing a check throws reaches the card's click handler.
        /// </summary>
        private UnlockResult CheckPin(string what, Func<UnlockResult> check)
        {
            try
            {
                return WithWaitCursor(check);
            }
            catch (Exception ex)
            {
                bool unsaved = ex is IOException or UnauthorizedAccessException;
                Warn(what + (unsaved ? ": the credential vault could not be saved (" : " failed (") + ex.GetType().Name + ": " + ex.Message + ")");
                if (unsaved) ShowStatus(VaultSaveFailedText);
                VaultCard.Hide();
                return new UnlockResult(UnlockOutcome.NoVault);
            }
        }

        /// <summary>
        /// Checking, creating or changing a PIN takes a moment (PBKDF2; an RSA key for a new PIN):
        /// the wait cursor shows meanwhile and the previous one comes back, whatever happens.
        /// </summary>
        private static T WithWaitCursor<T>(Func<T> work)
        {
            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                return work();
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }
        }

        /// <summary>
        /// Runs <paramref name="then"/> now while the vault is unlocked; else asks for the PIN first, for
        /// <paramref name="purpose"/>, the card showing a wait or the tries left from earlier wrong PINs.
        /// </summary>
        private void WithUnlocked(CredentialVault vault, string purpose, string what, Action then, bool reveals = false)
        {
            Action step = () => VaultStep(what, then, reveals);
            if (vault.IsUnlocked) step();
            else VaultCard.ShowEnterPin(purpose, pin => CheckPin("Checking a PIN", () => vault.Unlock(pin)), step, vault.PinState);
        }

        private static string Quoted(CredentialInfo info) => "\u201C" + SecretPillGenerator.LabelOf(info) + "\u201D";

        // ---- store as credential -------------------------------------------------------------

        /// <summary>
        /// Store as credential applies: a vault MicaPad can read, and in the note editor (not under a
        /// history version) a plain selection of 4 to 65,536 characters that neither holds a reference
        /// nor runs into one.
        /// </summary>
        internal bool CanStoreSelection()
        {
            if (_shown == null || Vault == null || PreviewPanel.Visibility == Visibility.Visible) return false;
            var selection = Editor.TextArea.Selection;
            if (selection.IsEmpty || selection is RectangleSelection) return false;

            int start = Editor.SelectionStart;
            int length = Editor.SelectionLength;
            if (length < MinStoreLength || length > CredentialVault.MaxSecretLength) return false;
            if (LoadedVault() == null) return false;

            var document = Editor.Document;
            return !SecretTokens.Contains(document.GetText(start, length)) && !OverlapsReference(document, start, length);
        }

        /// <summary>True when a reference overlaps the range: storing that text would break the reference.</summary>
        private static bool OverlapsReference(TextDocument document, int start, int length)
        {
            // Any reference overlapping the range lies within one reference length of it.
            int from = Math.Max(0, start - ReferenceLength + 1);
            int to = Math.Min(document.TextLength, start + length + ReferenceLength - 1);
            foreach (var reference in SecretTokens.Find(document.GetText(from, to - from)))
            {
                int at = from + reference.Offset;
                if (at < start + length && at + reference.Length > start) return true;
            }
            return false;
        }

        /// <summary>The note editor menu's Store as credential..., right after Copy as RTF (or Copy); only when MicaPad has a vault.</summary>
        private void AddStoreItem(ContextMenu menu)
        {
            if (Vault == null) return;
            var items = menu.Items.Cast<object>().ToList();
            int after = items.FindIndex(i => i is MenuItem { Header: "Copy as RTF" });
            if (after < 0) after = items.FindIndex(i => i is MenuItem { Header: "Copy" });
            menu.Items.Insert(after + 1, Item("Store as credential\u2026", null, StoreSelection, CanStoreSelection(), icon: "\uE72E"));
        }

        /// <summary>Store as credential (spec 3.1): the PIN is created first when there is no vault yet, then the card asks for a label.</summary>
        internal void StoreSelection()
        {
            if (!CanStoreSelection() || LoadedVault() is not { } vault || _shown is not { } note) return;
            var document = Editor.Document;
            string value = Editor.SelectedText;

            if (vault.Exists)
            {
                ShowStoreCard(vault, note, document, value);
                return;
            }
            VaultCard.ShowCreatePin(pin => VaultStep("Creating the credential PIN", () =>
            {
                try
                {
                    WithWaitCursor(() =>
                    {
                        vault.Create(pin);
                        return true;
                    });
                }
                catch (InvalidOperationException)
                {
                    // Another window, or Settings, created the vault while this card waited: this PIN is not its PIN.
                    ShowStatus("A PIN was set meanwhile; try again.");
                    return;
                }
                ShowStoreCard(vault, note, document, value);
            }));
        }

        private void ShowStoreCard(CredentialVault vault, OpenNote note, TextDocument document, string value) =>
            VaultCard.ShowStore(value.Length, SecretScrubber.Count(document.Text, value),
                label => Guard("Storing a credential", () => CompleteStore(vault, note, document, value, label)));

        /// <summary>
        /// The label is in: the vault saves the value first (a failed save leaves the text alone),
        /// then every copy in the note becomes the reference as one edit, the undo history goes (an
        /// undo would bring the value back), and the note and its older versions are rewritten on disk.
        /// </summary>
        private void CompleteStore(CredentialVault vault, OpenNote note, TextDocument document, string value, string? label)
        {
            // The card waited: another tab may be showing, or the text may have changed under it.
            bool same = ReferenceEquals(_shown, note) && ReferenceEquals(Editor.Document, document)
                        && _docs.TryGetValue(note.Id, out var own) && ReferenceEquals(own, document);
            var copies = same ? SecretScrubber.Find(document.Text, value) : Array.Empty<int>();
            if (copies.Count == 0)
            {
                ShowStatus(NoteChangedText);
                return;
            }

            string id;
            try
            {
                id = vault.Add(value, label, note.Id);
            }
            catch (InvalidOperationException) when (!vault.Exists)
            {
                ShowStatus("The credential vault was reset; nothing was stored.");   // by Settings, while the card waited
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Warn("Storing a credential: the credential vault could not be saved (" + ex.GetType().Name + ": " + ex.Message + ")");
                ShowStatus("Could not store the credential: the vault could not be saved.");
                return;
            }

            // Last copy first, so the offsets of the earlier ones still hold.
            string reference = SecretTokens.Format(id);
            document.BeginUpdate();
            try
            {
                for (int i = copies.Count - 1; i >= 0; i--) document.Replace(copies[i], value.Length, reference);
            }
            finally
            {
                document.EndUpdate();
            }
            document.UndoStack.ClearAll();

            // The note on disk and its versions. A version of the old text still queued (a pause, a
            // replace, a close) can land after this scrub while the writer is behind, and a version
            // may not be rewritable right now: the scrub is queued first, so nothing can lose it,
            // and leaves the queue only once it ran clean with the writer idle (RetryPendingScrubs
            // tries again, keeping the value until then).
            var scrub = new PendingScrub(note.Id, id, value, reference);
            QueueScrub(scrub);
            _workspace.FlushPending();
            bool written = _workspace.FlushWrites(TimeSpan.FromSeconds(2));
            int left = Scrub(scrub);
            if (written && left == 0) _pendingScrubs.Remove(scrub);

            string status = !written ? "Stored as " + id + " \u2014 older versions are still being saved; they will be cleaned when that finishes"
                : left < 0 ? "Stored as " + id + MayStillHoldText
                : StoredText(id, left);
            // MicaPad never writes the user's file by itself: it keeps the value until the user saves it.
            if (note.Meta.IsFileBacked) status += " \u2014 save the file (Ctrl+S) to remove it there";
            ShowStatus(status);
            if (HistoryPanel.Visibility == Visibility.Visible) ShowHistory();
        }

        private static string StoredText(string id, int left) =>
            left == 0 ? "Stored as " + id
            : left == 1 ? "Stored as " + id + " \u2014 1 older version still holds it"
            : "Stored as " + id + " \u2014 " + left.ToString(CultureInfo.InvariantCulture) + " older versions still hold it";

        /// <summary>The status for a scrub that threw: nothing is known about the versions.</summary>
        private const string MayStillHoldText = " \u2014 older versions may still hold it; MicaPad will try again";

        /// <summary>The first wait between tries of a scrub that did not run clean; it doubles up to <see cref="MaxScrubRetryMs"/>.</summary>
        private const long FirstScrubRetryMs = 2000;

        /// <summary>The longest wait between tries, so a version that never becomes rewritable is not tried (and logged) every two seconds.</summary>
        private const long MaxScrubRetryMs = 5 * 60 * 1000;

        /// <summary>Scrubs not yet run clean, with the writer idle; each holds its value until then.</summary>
        private readonly List<PendingScrub> _pendingScrubs = new();

        /// <summary>When the tick may next ask whether the writer is idle (asking makes it retry failed work at once).</summary>
        private long _nextScrubTryMs;

        /// <summary>The wait before the next try while scrubs keep failing.</summary>
        private long _scrubRetryMs = FirstScrubRetryMs;

        /// <summary>Scrubs still waiting; for tests.</summary>
        internal int PendingScrubCount => _pendingScrubs.Count;

        /// <summary>Queues a scrub; the tick tries it within two seconds.</summary>
        private void QueueScrub(PendingScrub scrub)
        {
            _pendingScrubs.Add(scrub);
            _scrubRetryMs = FirstScrubRetryMs;
            _nextScrubTryMs = Math.Min(_nextScrubTryMs, Environment.TickCount64 + FirstScrubRetryMs);
        }

        /// <summary>
        /// One scrub of a note's versions: how many may still hold the value, or -1 when the scrub
        /// threw (logged with the ids and the exception's type only).
        /// </summary>
        private int Scrub(PendingScrub scrub)
        {
            try
            {
                int left = _workspace.Store.ScrubSnapshots(scrub.NoteId, scrub.Value, scrub.Reference);
                scrub.LastLeft = left;
                return left;
            }
            catch (Exception ex)
            {
                Warn("Removing stored credential " + scrub.Id + " from the older versions of note " + scrub.NoteId + " failed (" + ex.GetType().Name + ")");
                return -1;
            }
        }

        /// <summary>
        /// The window tick: the pending scrubs run once the writer is idle, asked at most every two
        /// seconds; while scrubs keep failing, the wait doubles up to five minutes.
        /// </summary>
        private void RetryPendingScrubsOnTick()
        {
            if (_pendingScrubs.Count == 0 || Environment.TickCount64 < _nextScrubTryMs) return;
            _nextScrubTryMs = Environment.TickCount64 + FirstScrubRetryMs;
            Guard("Removing a stored credential from older versions", () => RetryPendingScrubs());
            if (_pendingScrubs.Count > 0 && _scrubRetryMs > FirstScrubRetryMs) _nextScrubTryMs = Environment.TickCount64 + _scrubRetryMs;
        }

        /// <summary>
        /// Once the writer is idle (every queued version is on disk), runs each pending scrub once.
        /// One that leaves no version holding its value is dropped with the value; one that throws
        /// or leaves versions behind stays queued, and the status says so when that count changes.
        /// Returns how many scrubs still wait.
        /// </summary>
        internal int RetryPendingScrubs()
        {
            if (_pendingScrubs.Count == 0 || !_workspace.FlushWrites(TimeSpan.Zero)) return _pendingScrubs.Count;

            foreach (var scrub in _pendingScrubs.ToList())
            {
                int before = scrub.LastLeft;
                int left = Scrub(scrub);
                if (left == 0)
                {
                    _pendingScrubs.Remove(scrub);
                    continue;
                }
                if (left != before) ShowStatus(left < 0 ? "Stored as " + scrub.Id + MayStillHoldText : StoredText(scrub.Id, left));
            }

            _scrubRetryMs = _pendingScrubs.Count == 0 ? FirstScrubRetryMs : Math.Min(_scrubRetryMs * 2, MaxScrubRetryMs);
            return _pendingScrubs.Count;
        }

        /// <summary>
        /// The close path: the scrubs still waiting get a short flush and run now. Any that did not
        /// run clean with the writer idle go to another open window of the workspace, which keeps
        /// retrying; with none left (application exit), to the workspace, which runs them once more
        /// after the last flush (<see cref="PadWorkspace.RunPendingScrubs"/>).
        /// </summary>
        private void FinishPendingScrubs()
        {
            if (_pendingScrubs.Count == 0) return;
            bool idle = _workspace.FlushWrites(TimeSpan.FromSeconds(1));
            var due = _pendingScrubs.ToList();
            _pendingScrubs.Clear();

            var heir = OtherWindows().FirstOrDefault();
            foreach (var scrub in due)
            {
                if (Scrub(scrub) == 0 && idle) continue;
                if (heir != null) heir.QueueScrub(scrub);
                else _workspace.AddPendingScrub(scrub);
            }
        }

        // ---- the pill menu (spec 3.3, R8) ----------------------------------------------------

        /// <summary>The reference in the note editor whose pill covers <paramref name="offset"/> (the caret inside it or at its start).</summary>
        internal SecretReference? ReferenceAt(int offset) => ReferenceIn(Editor.Document, offset);

        private static SecretReference? ReferenceIn(TextDocument? document, int offset)
        {
            if (document == null || offset < 0 || offset > document.TextLength) return null;
            var line = document.GetLineByOffset(offset);
            if (line.Length > SecretPillGenerator.MaxLineLength) return null;   // drawn as text, not a pill

            foreach (var found in SecretTokens.Find(document.GetText(line.Offset, line.Length)))
            {
                int at = line.Offset + found.Offset;
                if ((at <= offset && offset < at + found.Length) || at == offset) return new SecretReference(at, found.Length, found.Id);
            }
            return null;
        }

        /// <summary>
        /// The reference a right-click menu is for (R8): the one under the mouse when the mouse
        /// opened the menu, else the one the caret is inside or at the start of. Anywhere on a pill
        /// counts: the position in front of the pill is taken, never the one after it.
        /// </summary>
        private SecretReference? MenuReference(TextEditor editor, bool byMouse)
        {
            if (Vault == null || editor.Document == null) return null;
            if (!byMouse) return ReferenceIn(editor.Document, editor.CaretOffset);

            var view = editor.TextArea.TextView;
            var point = Mouse.GetPosition(view) + new Vector(view.ScrollOffset.X, Math.Floor(view.ScrollOffset.Y));
            return view.GetPositionFloor(point) is { } at ? ReferenceIn(editor.Document, editor.Document.GetOffset(at.Location)) : null;
        }

        /// <summary>A menu opened from the keyboard (menu key, Shift+F10) reports its cursor at -1, -1.</summary>
        private static bool OpenedByMouse(ContextMenuEventArgs e) => e.CursorLeft != -1 || e.CursorTop != -1;

        /// <summary>
        /// The pill menu, in place of the text menu. A credential the vault does not have gets Copy
        /// reference only; the read-only history preview gets what reads (Reveal, Copy secret, Copy reference).
        /// </summary>
        internal void FillPillMenu(ContextMenu menu, SecretReference reference, bool readOnly = false)
        {
            menu.Items.Clear();
            EditorMenus.Style(menu, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            string id = reference.Id;
            var vault = LoadedVault();
            bool stored = vault?.Find(id) != null;
            if (stored)
            {
                menu.Items.Add(Item("Reveal\u2026", null, () => RevealCredential(id), icon: "\uE7B3"));
                menu.Items.Add(Item("Copy secret", null, () => CopyCredential(id), icon: "\uE8C8"));
                if (!readOnly)
                {
                    menu.Items.Add(Item("Rename label\u2026", null, () => RenameCredential(id), icon: "\uE8AC"));
                    menu.Items.Add(Item("Unmask", null, () => UnmaskCredential(reference), icon: "\uE785"));
                    menu.Items.Add(Item("Delete credential\u2026", null, () => DeleteCredential(id), icon: "\uE74D"));
                }
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(Item("Copy reference", null, () => CopyPlainText(SecretTokens.Format(id), "a credential reference"), icon: "\uE71B"));
            if (stored && !readOnly && vault!.IsUnlocked) menu.Items.Add(Item("Lock now", null, () => LockVault(vault), icon: "\uE72E"));
        }

        /// <summary>Reveal...: the value in the card for 30 seconds, after the PIN unless unlocked. Its Copy is Copy secret.</summary>
        internal void RevealCredential(string id)
        {
            if (LoadedVault() is not { } vault || Stored(vault, id) is not { } info) return;
            string label = SecretPillGenerator.LabelOf(info);
            WithUnlocked(vault, "To reveal " + Quoted(info) + ".", "Revealing a credential",
                () => VaultCard.ShowReveal(label, vault.Reveal(id), () => CopyCredential(id)), reveals: true);
        }

        /// <summary>Copy secret: the value on the clipboard, kept out of clipboard history and cleared after 30 seconds.</summary>
        internal void CopyCredential(string id)
        {
            if (LoadedVault() is not { } vault || Stored(vault, id) is not { } info) return;
            WithUnlocked(vault, "To copy " + Quoted(info) + ".", "Copying a credential",
                () => ShowStatus(SecretClipboard.Copy(vault.Reveal(id)) ? "Copied \u2014 clears in 30 s" : "Clipboard busy, try again"),
                reveals: true);
        }

        /// <summary>Unmask: the reference becomes its value again, as an ordinary undoable edit. The vault keeps the credential.</summary>
        internal void UnmaskCredential(SecretReference reference)
        {
            if (LoadedVault() is not { } vault || Stored(vault, reference.Id) is not { } info) return;
            var document = Editor.Document;
            WithUnlocked(vault, "To unmask " + Quoted(info) + ".", "Unmasking a credential", () =>
            {
                // The PIN question may have waited: the reference must still be where the menu found it.
                if (!ReferenceEquals(Editor.Document, document) || !Holds(document, reference))
                {
                    ShowStatus("The note changed; nothing was unmasked.");
                    return;
                }
                document.Replace(reference.Offset, reference.Length, vault.Reveal(reference.Id));
            }, reveals: true);
        }

        private static bool Holds(TextDocument document, SecretReference reference) =>
            reference.Offset >= 0 && reference.Offset + reference.Length <= document.TextLength
            && document.GetText(reference.Offset, reference.Length) == SecretTokens.Format(reference.Id);

        /// <summary>Delete credential...: after the PIN, a confirmation; the notes keep their references, shown as missing.</summary>
        internal void DeleteCredential(string id)
        {
            if (LoadedVault() is not { } vault || Stored(vault, id) is not { } info) return;
            string purpose = "To delete " + Quoted(info) + ".";
            WithUnlocked(vault, purpose, "Deleting a credential", () => VaultCard.ShowConfirm(
                "Delete credential",
                "Delete " + Quoted(info) + "? Notes that use " + id + " will show it as missing. This cannot be undone.",
                "Delete",
                // The vault may have locked while the question waited: then the PIN is asked again.
                () => WithUnlocked(vault, purpose, "Deleting a credential", () => vault.Delete(id))));
        }

        /// <summary>Rename label...: labels are not secret, so no PIN.</summary>
        internal void RenameCredential(string id)
        {
            if (LoadedVault() is not { } vault || Stored(vault, id) is not { } info) return;
            VaultCard.ShowRename(info.Label, label => VaultStep("Renaming a credential", () => vault.Rename(id, label)));
        }

        private void LockVault(CredentialVault vault)
        {
            vault.Lock();
            ShowStatus("Locked");
        }

        /// <summary>Change PIN (Settings opens MicaPad for it, R6): the card asks for the current PIN and the new one twice.</summary>
        internal void ShowChangePin()
        {
            if (LoadedVault() is not { Exists: true } vault)
            {
                ShowStatus("No PIN set yet");
                return;
            }
            VaultCard.ShowChangePin((current, next) => CheckPin("Changing the PIN", () =>
            {
                var result = vault.ChangePin(current, next);
                if (result.Outcome == UnlockOutcome.Unlocked) ShowStatus("PIN changed");
                return result;
            }), vault.PinState);
        }

        /// <summary>
        /// Once per run: the vault this Windows account could not open was moved aside, so stored
        /// credentials show as missing. Nothing was deleted. Another notice showing goes first; this
        /// one then waits for the next time MicaPad opens.
        /// </summary>
        internal void ShowVaultMovedNotice()
        {
            if (_workspace.VaultNoticeShown || LoadedVault() is not { MovedAsideTo: string path }) return;
            if (InfoBar.Visibility == Visibility.Visible) return;

            _workspace.VaultNoticeShown = true;
            ShowInfo("MicaPad could not open the credential vault on this Windows account, so it was moved to " + path
                     + ". Stored credentials show as missing.", null, "Show folder", () => ShowInFolder(path));
        }
    }
}
