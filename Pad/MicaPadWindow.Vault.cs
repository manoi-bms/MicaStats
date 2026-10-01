using System;
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

        /// <summary>The close path: the shared vault must not keep this window alive, and an open card empties its boxes.</summary>
        private void DetachVault()
        {
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
        /// cannot be saved, or, for a step that reveals, a value that does not decrypt, is reported
        /// in the status bar; anything else is logged by <see cref="EditorMenus.Guard"/>.
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
            });
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
                return check();
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

        /// <summary>Runs <paramref name="then"/> now while the vault is unlocked; else asks for the PIN first, for <paramref name="purpose"/>.</summary>
        private void WithUnlocked(CredentialVault vault, string purpose, string what, Action then, bool reveals = false)
        {
            Action step = () => VaultStep(what, then, reveals);
            if (vault.IsUnlocked) step();
            else VaultCard.ShowEnterPin(purpose, pin => CheckPin("Checking a PIN", () => vault.Unlock(pin)), step);
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
                vault.Create(pin);
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

            _workspace.FlushPending();
            _workspace.FlushWrites(TimeSpan.FromSeconds(2));
            int left = _workspace.Store.ScrubSnapshots(note.Id, value, reference);
            ShowStatus(left == 0 ? "Stored as " + id
                : left == 1 ? "Stored as " + id + " \u2014 1 older version still holds it"
                : "Stored as " + id + " \u2014 " + left.ToString(CultureInfo.InvariantCulture) + " older versions still hold it");
            if (HistoryPanel.Visibility == Visibility.Visible) ShowHistory();
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
            menu.Items.Add(Item("Copy reference", null, () => CopyFilePath(SecretTokens.Format(id)), icon: "\uE71B"));
            if (stored && !readOnly && vault!.IsUnlocked) menu.Items.Add(Item("Lock now", null, () => LockVault(vault), icon: "\uE72E"));
        }

        /// <summary>Reveal...: the value in the card for 30 seconds, after the PIN unless unlocked. Its Copy is Copy secret.</summary>
        internal void RevealCredential(string id)
        {
            if (LoadedVault() is not { } vault || vault.Find(id) is not { } info) return;
            string label = SecretPillGenerator.LabelOf(info);
            WithUnlocked(vault, "To reveal " + Quoted(info) + ".", "Revealing a credential",
                () => VaultCard.ShowReveal(label, vault.Reveal(id), () => CopyCredential(id)), reveals: true);
        }

        /// <summary>Copy secret: the value on the clipboard, kept out of clipboard history and cleared after 30 seconds.</summary>
        internal void CopyCredential(string id)
        {
            if (LoadedVault() is not { } vault || vault.Find(id) is not { } info) return;
            WithUnlocked(vault, "To copy " + Quoted(info) + ".", "Copying a credential",
                () => ShowStatus(SecretClipboard.Copy(vault.Reveal(id)) ? "Copied \u2014 clears in 30 s" : "Clipboard busy, try again"),
                reveals: true);
        }

        /// <summary>Unmask: the reference becomes its value again, as an ordinary undoable edit. The vault keeps the credential.</summary>
        internal void UnmaskCredential(SecretReference reference)
        {
            if (LoadedVault() is not { } vault || vault.Find(reference.Id) is not { } info) return;
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
            if (LoadedVault() is not { } vault || vault.Find(id) is not { } info) return;
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
            if (LoadedVault() is not { } vault || vault.Find(id) is not { } info) return;
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
            }));
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
