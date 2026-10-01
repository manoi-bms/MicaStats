using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The credential vault in the MicaPad window: Store as credential, the pills and their menu,
    /// the PIN and reveal card, and the notice for a vault moved aside.
    /// </summary>
    public class PadVaultTests
    {
        private const string Pin = "246810";
        private const string SaveFailed = "The credential vault could not be saved right now.";
        private const string DecryptFailed = "This credential could not be decrypted.";

        private static void WithWindow(Action<MicaPadWindow, PadTestEnv, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, env, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        /// <summary>A vault with a PIN and one credential, "hunter2" labelled "Bank"; locked.</summary>
        private static string Bank(PadTestEnv env)
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            return env.Vault.Add("hunter2", "Bank", null);
        }

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
            using var clipboard = new FakeSecretClipboard();
            string id = Bank(env);

            window.CopyCredential(id);                 // locked: the PIN first
            Assert.Equal("EnterPin", window.VaultCard.Mode);
            Assert.Equal("To copy \u201CBank\u201D.", window.VaultCard.BodyText.Text);
            Assert.Empty(clipboard.Copied);
            window.VaultCard.PinBox.Password = Pin;
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal(new[] { "hunter2" }, clipboard.Copied);
            Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, clipboard.Scheduled);
            Assert.Equal("Copied \u2014 clears in 30 s", window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);

            clipboard.Busy = true;
            window.CopyCredential(id);                 // unlocked now: straight to the clipboard
            Assert.Equal("Clipboard busy, try again", window.StatusMessage.Text);
            Assert.Single(clipboard.Copied);

            // The reveal card's Copy is the same copy.
            clipboard.Busy = false;
            window.RevealCredential(id);
            Click(window.VaultCard.CopyButton);
            Assert.Equal(new[] { "hunter2", "hunter2" }, clipboard.Copied);
            Assert.Equal("Reveal", window.VaultCard.Mode);
        });

        [Fact]
        public void Delete_asks_to_confirm_and_needs_the_pin() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);

            window.DeleteCredential(id);
            Assert.Equal("EnterPin", window.VaultCard.Mode);
            Assert.Equal("To delete \u201CBank\u201D.", window.VaultCard.BodyText.Text);
            window.VaultCard.PinBox.Password = Pin;
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("Confirm", window.VaultCard.Mode);
            Assert.Equal("Delete credential", window.VaultCard.TitleText.Text);
            Assert.Equal("Delete \u201CBank\u201D? Notes that use " + id + " will show it as missing. This cannot be undone.",
                         window.VaultCard.BodyText.Text);
            Assert.Equal("Delete", window.VaultCard.PrimaryButton.Content);
            Click(window.VaultCard.SecondaryButton);   // Cancel keeps it
            Assert.False(window.VaultCard.IsOpen);
            Assert.NotNull(env.Vault.Find(id));

            window.DeleteCredential(id);               // unlocked now: straight to the question
            Assert.Equal("Confirm", window.VaultCard.Mode);
            Click(window.VaultCard.PrimaryButton);
            Assert.Null(env.Vault.Find(id));
        });

        [Fact]
        public void A_delete_confirmed_after_the_vault_locked_asks_for_the_pin_again() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            env.Vault.Unlock(Pin);
            window.DeleteCredential(id);
            Assert.Equal("Confirm", window.VaultCard.Mode);

            env.Vault.Lock();                          // Windows locked while the question waited
            Assert.Equal("Confirm", window.VaultCard.Mode);
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("EnterPin", window.VaultCard.Mode);
            Assert.NotNull(env.Vault.Find(id));
            window.VaultCard.PinBox.Password = Pin;
            Click(window.VaultCard.PrimaryButton);
            Assert.Null(env.Vault.Find(id));
        });

        [Fact]
        public void Rename_needs_no_pin() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);

            window.RenameCredential(id);
            Assert.Equal("Rename", window.VaultCard.Mode);
            Assert.Equal("Bank", window.VaultCard.LabelBox.Text);
            window.VaultCard.LabelBox.Text = "Bank login";
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("Bank login", env.Vault.Find(id)!.Label);
            Assert.False(env.Vault.IsUnlocked);
        });

        [Fact]
        public void Lock_now_locks_and_says_so() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            env.Vault.Unlock(Pin);
            window.Editor.Text = SecretTokens.Format(id);

            var menu = new ContextMenu();
            window.FillPillMenu(menu, window.ReferenceAt(0)!.Value);
            Click(menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Lock now"));

            Assert.False(env.Vault.IsUnlocked);
            Assert.Equal("Locked", window.StatusMessage.Text);
        });

        [Fact]
        public void A_reveal_hides_when_the_vault_locks() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            env.Vault.Unlock(Pin);
            window.RevealCredential(id);
            Assert.Equal("hunter2", window.VaultCard.ValueBox.Text);

            env.Vault.Lock();

            Assert.False(window.VaultCard.IsOpen);
            Assert.Equal("", window.VaultCard.ValueBox.Text);
        });

        [Fact]
        public void A_moved_vault_is_announced_once() => WithWindow((window, env, config) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(env.Vault.FilePath)!);
            File.WriteAllBytes(env.Vault.FilePath, new byte[] { 1, 2, 3, 4, 5 });   // not a vault this account can open
            Assert.True(env.Vault.EnsureLoaded());
            string moved = env.Vault.MovedAsideTo!;
            var started = new List<ProcessStartInfo>();
            var original = MicaPadWindow.StartExplorer;
            MicaPadWindow.StartExplorer = started.Add;
            try
            {
                window.ShowVaultMovedNotice();

                Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
                Assert.Equal("MicaPad could not open the credential vault on this Windows account, so it was moved to "
                             + moved + ". Stored credentials show as missing.", window.InfoText.Text);
                Assert.Equal("Show folder", window.InfoPrimary.Content);
                Assert.True(env.Workspace.VaultNoticeShown);
                Click(window.InfoPrimary);
                Assert.Equal("/select,\"" + moved + "\"", Assert.Single(started).Arguments);
                Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);

                window.ShowVaultMovedNotice();
                Assert.Equal(Visibility.Collapsed, window.InfoBar.Visibility);
            }
            finally
            {
                MicaPadWindow.StartExplorer = original;
            }
        });

        [Fact]
        public void The_moved_vault_notice_waits_for_another_notice_to_go() => WithWindow((window, env, config) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(env.Vault.FilePath)!);
            File.WriteAllBytes(env.Vault.FilePath, new byte[] { 1, 2, 3, 4, 5 });
            Assert.True(env.Vault.EnsureLoaded());
            window.ShowInfo("Another notice.", null);

            window.ShowVaultMovedNotice();
            Assert.Equal("Another notice.", window.InfoText.Text);
            Assert.False(env.Workspace.VaultNoticeShown);

            window.HideInfo();
            window.ShowVaultMovedNotice();
            Assert.StartsWith("MicaPad could not open the credential vault", window.InfoText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Pills_draw_in_the_preview_too() => WithWindow((window, env, config) =>
        {
            Assert.Single(window.Editor.TextArea.TextView.ElementGenerators.OfType<SecretPillGenerator>());
            Assert.Single(window.PreviewEditor.TextArea.TextView.ElementGenerators.OfType<SecretPillGenerator>());
            string id = Bank(env);

            window.PreviewEditor.Text = "old " + SecretTokens.Format(id);

            var pill = Assert.Single(Pills(window.PreviewEditor));
            Assert.Contains("Bank", PillText(pill));
        });

        [Fact]
        public void A_theme_switch_repaints_the_pills() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            window.Editor.Text = "pw: " + SecretTokens.Format(id);
            string before = PillBack(window);
            Assert.Equal(PadThemeApplier.ToBrush(window.Palette.PillBack).ToString(), before);

            config.PadTheme = window.Palette.IsDark ? PadThemes.Light : PadThemes.Dark;

            string after = PillBack(window);
            Assert.Equal(PadThemeApplier.ToBrush(window.Palette.PillBack).ToString(), after);
            Assert.NotEqual(before, after);
        });

        [Fact]
        public void The_menu_is_for_the_reference_at_the_caret_when_opened_from_the_keyboard() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            string reference = SecretTokens.Format(id);
            window.Editor.Text = "pw: " + reference + " end";

            window.Editor.CaretOffset = 4;                         // at the start of the pill
            window.RefreshEditorMenu();
            Assert.Equal(new[] { "Reveal\u2026", "Copy secret", "Rename label\u2026", "Unmask", "Delete credential\u2026", "-", "Copy reference" },
                         PadMenuTests.Headers(window.EditorMenu));

            window.Editor.CaretOffset = 4 + reference.Length;      // just after it: the editor menu
            window.RefreshEditorMenu();
            Assert.Contains("Store as credential\u2026", PadMenuTests.Headers(window.EditorMenu));

            // The history preview is read-only: only what reads.
            window.PreviewEditor.Text = "old " + reference;
            window.PreviewEditor.CaretOffset = 4;
            window.RefreshPreviewMenu();
            Assert.Equal(new[] { "Reveal\u2026", "Copy secret", "-", "Copy reference" }, PadMenuTests.Headers(window.PreviewMenu));
            window.PreviewEditor.CaretOffset = 0;
            window.RefreshPreviewMenu();
            Assert.Equal(new[] { "Copy", "Select all" }, PadMenuTests.Headers(window.PreviewMenu));
        });

        [Fact]
        public void The_editor_menu_offers_store_after_copy_as_rtf_only_where_it_applies() => WithWindow((window, env, config) =>
        {
            window.Editor.Text = "pw: hunter2 and {{secret:K7Q2M9XD}}";
            window.Editor.Select(4, 7);
            window.RefreshEditorMenu();
            var headers = PadMenuTests.Headers(window.EditorMenu);
            int at = Array.IndexOf(headers, "Store as credential\u2026");
            Assert.Equal("Copy as RTF", headers[at - 1]);
            var item = PadMenuTests.ItemOf(window.EditorMenu, "Store as credential\u2026");
            Assert.True(item.IsEnabled);
            Assert.Equal("\uE72E", item.Icon);

            window.Editor.Select(3, 3);
            window.RefreshEditorMenu();
            Assert.False(PadMenuTests.ItemOf(window.EditorMenu, "Store as credential\u2026").IsEnabled);

            window.Editor.Select(18, 10);                          // part of a reference: storing it would break the reference
            Assert.False(window.CanStoreSelection());
            window.Editor.Select(12, 6);                           // "and {{" runs into it
            Assert.False(window.CanStoreSelection());

            window.Editor.Text = new string('x', 65_537);
            window.Editor.Select(0, 65_536);
            Assert.True(window.CanStoreSelection());
            window.Editor.Select(0, 65_537);
            Assert.False(window.CanStoreSelection());

            const string box = "123456\n789012";
            window.Editor.Text = box;
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            window.Editor.TextArea.TextView.EnsureVisualLines();
            window.Editor.TextArea.Selection = new RectangleSelection(window.Editor.TextArea, new TextViewPosition(1, 1), new TextViewPosition(2, 6));
            Assert.False(window.CanStoreSelection());              // a rectangle is not one value

            window.Editor.Select(0, 6);
            Assert.True(window.CanStoreSelection());
            window.PreviewPanel.Visibility = Visibility.Visible;   // a history version covers the note
            Assert.False(window.CanStoreSelection());
        });

        [Fact]
        public void Storing_never_nests_references_and_leaves_other_tabs_alone() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            var other = window.Editor.Document;
            window.Editor.Text = "secret in another tab";
            window.NewTab();
            window.Editor.Text = "old={{secret:K7Q2M9XD}} new=secret again=secret";
            window.Editor.Select(window.Editor.Text.IndexOf("new=", StringComparison.Ordinal) + 4, 6);

            window.StoreSelection();
            Assert.Contains("Appears 2 times in this note", window.VaultCard.BodyText.Text, StringComparison.Ordinal);
            Click(window.VaultCard.PrimaryButton);

            string reference = SecretTokens.Format(Assert.Single(env.Vault.Credentials).Id);
            Assert.Equal("old={{secret:K7Q2M9XD}} new=" + reference + " again=" + reference, window.Editor.Text);
            Assert.Equal("secret in another tab", other.Text);
        });

        [Fact]
        public void A_note_that_changed_while_the_card_waited_stores_nothing() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();
            window.StoreSelection();

            window.NewTab();                           // another tab came forward
            Click(window.VaultCard.PrimaryButton);

            Assert.Empty(env.Vault.Credentials);
            Assert.Equal("The note changed; nothing was stored.", window.StatusMessage.Text);
        });

        [Fact]
        public void A_store_reports_versions_that_still_hold_the_value() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            var note = env.Workspace.Open.Single();
            window.Editor.Text = "pw=hunter2";
            env.Workspace.SnapshotNow(note, SnapshotReason.Pause);
            env.Workspace.FlushWrites(TimeSpan.FromSeconds(5));
            var version = Assert.Single(env.Store.ListSnapshots(note.Id));
            window.Editor.SelectAll();
            window.StoreSelection();

            using (new FileStream(version.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(window.VaultCard.PrimaryButton);
            }

            string id = Assert.Single(env.Vault.Credentials).Id;
            Assert.Equal("Stored as " + id + " \u2014 1 older version still holds it", window.StatusMessage.Text);
        });

        [Fact]
        public void The_history_list_is_refreshed_after_a_store() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            var note = env.Workspace.Open.Single();
            window.Editor.Text = "pw=hunter2";
            env.Workspace.SnapshotNow(note, SnapshotReason.Pause);
            window.Editor.SelectAll();
            window.StoreSelection();
            window.HistoryPanel.Visibility = Visibility.Visible;   // the pane is open, its list not read yet
            Assert.Empty(window.HistoryPanel.Rows);

            Click(window.VaultCard.PrimaryButton);

            Assert.NotEmpty(window.HistoryPanel.Rows);
        });

        [Fact]
        public void Change_pin_needs_a_vault() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            window.ShowChangePin();
            Assert.Equal("No PIN set yet", window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);

            env.Vault.Create(Pin);
            window.ShowChangePin();
            Assert.Equal("ChangePin", window.VaultCard.Mode);
            window.VaultCard.PinBox.Password = Pin;
            window.VaultCard.PinBox2.Password = "13579246";
            window.VaultCard.PinBox3.Password = "13579246";
            Click(window.VaultCard.PrimaryButton);

            Assert.False(window.VaultCard.IsOpen);
            Assert.Equal(UnlockOutcome.Unlocked, env.Vault.Unlock("13579246").Outcome);
        });

        [Fact]
        public void Vault_save_failures_in_a_card_are_reported_not_thrown() => WithWindow((window, env, config) =>
        {
            var warnings = new List<string>();
            window.Warn = warnings.Add;
            string id = Bank(env);

            using (new FileStream(env.Vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                window.RevealCredential(id);
                window.VaultCard.PinBox.Password = Pin;
                Click(window.VaultCard.PrimaryButton);     // checking a PIN saves the try first
                Assert.Equal(SaveFailed, window.StatusMessage.Text);
                Assert.False(window.VaultCard.IsOpen);

                window.ShowStatus("");
                window.RenameCredential(id);
                window.VaultCard.LabelBox.Text = "Card";
                Click(window.VaultCard.PrimaryButton);
                Assert.Equal(SaveFailed, window.StatusMessage.Text);

                window.ShowStatus("");
                window.ShowChangePin();
                window.VaultCard.PinBox.Password = Pin;
                window.VaultCard.PinBox2.Password = "13579246";
                window.VaultCard.PinBox3.Password = "13579246";
                Click(window.VaultCard.PrimaryButton);
                Assert.Equal(SaveFailed, window.StatusMessage.Text);
                Assert.False(window.VaultCard.IsOpen);
            }

            Assert.Equal("Bank", env.Vault.Find(id)!.Label);
            Assert.False(env.Vault.IsUnlocked);
            Assert.NotEmpty(warnings);
            Assert.All(warnings, w => Assert.DoesNotContain(Pin, w, StringComparison.Ordinal));
            Assert.All(warnings, w => Assert.DoesNotContain("hunter2", w, StringComparison.Ordinal));
        });

        [Fact]
        public void A_pin_that_cannot_be_created_stops_the_store() => WithWindow((window, env, config) =>
        {
            window.Warn = _ => { };
            env.Vault.Load();
            Directory.CreateDirectory(env.Vault.FilePath + ".tmp");   // the vault cannot be written
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();

            window.StoreSelection();
            window.VaultCard.PinBox.Password = Pin;
            window.VaultCard.PinBox2.Password = Pin;
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal(SaveFailed, window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);
            Assert.False(env.Vault.Exists);
            Assert.Equal("hunter2", window.Editor.Text);
        });

        [Fact]
        public void A_value_that_does_not_decrypt_is_reported() => WithWindow((window, env, config) =>
        {
            window.Warn = _ => { };
            using var clipboard = new FakeSecretClipboard();
            string id = Bank(env);
            DamageValue(env.Vault.FilePath);
            env.Vault.Load();
            env.Vault.Unlock(Pin);
            window.Editor.Text = "pw: " + SecretTokens.Format(id);

            window.RevealCredential(id);
            Assert.Equal(DecryptFailed, window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);

            window.ShowStatus("");
            window.CopyCredential(id);
            Assert.Equal(DecryptFailed, window.StatusMessage.Text);
            Assert.Empty(clipboard.Copied);

            window.ShowStatus("");
            window.UnmaskCredential(window.ReferenceAt(5)!.Value);
            Assert.Equal(DecryptFailed, window.StatusMessage.Text);
            Assert.Equal("pw: " + SecretTokens.Format(id), window.Editor.Text);
        });

        [Fact]
        public void Keys_go_to_the_open_card_not_to_the_window() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            env.Vault.Unlock(Pin);
            window.FindBar.Open(replace: false);       // the window's own Escape would close it
            window.DeleteCredential(id);
            Assert.Equal("Confirm", window.VaultCard.Mode);

            // Focus outside the card (the editor; its text area has no parent in an unshown window):
            // Escape still cancels the card, and only the card.
            var escape = PreviewKey(window.Editor, Key.Escape);
            Assert.True(escape.Handled);
            Assert.False(window.VaultCard.IsOpen);
            Assert.True(window.FindBar.IsOpen);
            Assert.NotNull(env.Vault.Find(id));

            // A key typed in the card is left to the card.
            window.DeleteCredential(id);
            var inside = PreviewKey(window.VaultCard.PrimaryButton, Key.Escape);
            Assert.False(inside.Handled);
            Assert.True(window.FindBar.IsOpen);
            Assert.Equal("Confirm", window.VaultCard.Mode);

            // Any other key from outside does nothing to the note.
            var letter = PreviewKey(window.Editor, Key.N);
            Assert.True(letter.Handled);
            Assert.Equal("Confirm", window.VaultCard.Mode);

            // Enter answers the card.
            PreviewKey(window.Editor, Key.Enter);
            Assert.Null(env.Vault.Find(id));
        });

        [Fact]
        public void Focus_returns_to_the_editor_when_the_card_closes() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);

            window.RenameCredential(id);
            Click(window.VaultCard.SecondaryButton);
            Assert.Same(window.Editor.TextArea, FocusManager.GetFocusedElement(window));

            // Focus that moved on to something else stays there.
            window.RenameCredential(id);
            FocusManager.SetFocusedElement(window, window.HistoryButton);
            Click(window.VaultCard.SecondaryButton);
            Assert.Same(window.HistoryButton, FocusManager.GetFocusedElement(window));
        });

        // ---- fix round 1 --------------------------------------------------------------------

        private const string StillSaving = " \u2014 older versions are still being saved; they will be cleaned when that finishes";
        private const string Gone = "This credential is no longer stored.";

        [Fact]
        public void Versions_still_being_written_are_cleaned_once_the_writer_is_done() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            var note = env.Workspace.Open.Single();
            window.Editor.Text = "pw=hunter2";
            var gate = new ManualResetEventSlim();   // not disposed: the writer thread may still be leaving Wait
            try
            {
                env.Writer.Enqueue("busy", () => gate.Wait());                 // the writer is stuck on other work
                env.Workspace.SnapshotNow(note, SnapshotReason.Pause);         // a version of the old text waits behind it
                window.Editor.SelectAll();
                window.StoreSelection();
                Click(window.VaultCard.PrimaryButton);

                string id = Assert.Single(env.Vault.Credentials).Id;
                Assert.Equal("Stored as " + id + StillSaving, window.StatusMessage.Text);
                Assert.Equal(1, window.RetryPendingScrubs());                  // the tick: the writer is still busy, so it waits
            }
            finally
            {
                gate.Set();
            }

            Assert.True(env.Workspace.FlushWrites(TimeSpan.FromSeconds(5)));
            Assert.Contains(env.Store.ListSnapshots(note.Id), v => env.Store.ReadSnapshot(v)!.Contains("hunter2", StringComparison.Ordinal));   // it landed after the first scrub

            Assert.Equal(0, window.RetryPendingScrubs());                      // the tick, with the writer idle
            Assert.Equal(0, window.PendingScrubCount);
            Assert.All(env.Store.ListSnapshots(note.Id), v => Assert.DoesNotContain("hunter2", env.Store.ReadSnapshot(v)));
        });

        [Fact]
        public void A_window_closing_first_cleans_the_versions_on_its_way_out() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            var note = env.Workspace.Open.Single();
            window.Editor.Text = "pw=hunter2";
            var gate = new ManualResetEventSlim();
            try
            {
                env.Writer.Enqueue("busy", () => gate.Wait());
                env.Workspace.SnapshotNow(note, SnapshotReason.Pause);
                window.Editor.SelectAll();
                window.StoreSelection();
                Click(window.VaultCard.PrimaryButton);
                Assert.Equal(1, window.PendingScrubCount);
            }
            finally
            {
                gate.Set();                                                    // the writer catches up as the window closes
            }

            window.CloseForExit();

            Assert.Equal(0, window.PendingScrubCount);
            Assert.True(env.Workspace.FlushWrites(TimeSpan.FromSeconds(5)));
            Assert.NotEmpty(env.Store.ListSnapshots(note.Id));
            Assert.All(env.Store.ListSnapshots(note.Id), v => Assert.DoesNotContain("hunter2", env.Store.ReadSnapshot(v)));
        });

        [Fact]
        public void A_file_backed_note_says_its_file_keeps_the_value_until_saved() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            string path = env.FileOf("keys.txt");
            File.WriteAllText(path, "pw=hunter2");
            window.OpenPath(path);
            window.Editor.Select(3, 7);

            window.StoreSelection();
            Click(window.VaultCard.PrimaryButton);

            string id = Assert.Single(env.Vault.Credentials).Id;
            Assert.Equal("pw=" + SecretTokens.Format(id), window.Editor.Text);
            Assert.Equal("Stored as " + id + " \u2014 save the file (Ctrl+S) to remove it there", window.StatusMessage.Text);
            Assert.Equal("pw=hunter2", File.ReadAllText(path));   // MicaPad never writes the user's file by itself
        });

        [Fact]
        public void A_pin_set_meanwhile_is_reported() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();
            window.StoreSelection();
            Assert.Equal("CreatePin", window.VaultCard.Mode);

            env.Vault.Create("13579246");                     // another window, or Settings, set a PIN while the card waited
            window.VaultCard.PinBox.Password = Pin;
            window.VaultCard.PinBox2.Password = Pin;
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("A PIN was set meanwhile; try again.", window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);
            Assert.Empty(env.Vault.Credentials);
            Assert.Equal("hunter2", window.Editor.Text);
            Assert.Equal(UnlockOutcome.Unlocked, env.Vault.Unlock("13579246").Outcome);
        });

        [Fact]
        public void A_vault_reset_while_the_card_waited_stores_nothing() => WithWindow((window, env, config) =>
        {
            env.Vault.Load();
            env.Vault.Create(Pin);
            window.Editor.Text = "hunter2";
            window.Editor.SelectAll();
            window.StoreSelection();
            Assert.Equal("Store", window.VaultCard.Mode);

            env.Vault.Reset();                                 // Settings > Reset vault
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal("The credential vault was reset; nothing was stored.", window.StatusMessage.Text);
            Assert.Equal("hunter2", window.Editor.Text);
            Assert.False(env.Vault.Exists);
        });

        [Fact]
        public void A_credential_deleted_while_the_pin_card_waited_is_reported() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            window.RevealCredential(id);
            Assert.Equal("EnterPin", window.VaultCard.Mode);

            env.Vault.Unlock(Pin);                             // another window deletes it meanwhile
            env.Vault.Delete(id);
            env.Vault.Lock();
            window.VaultCard.PinBox.Password = Pin;
            Click(window.VaultCard.PrimaryButton);

            Assert.Equal(Gone, window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);

            // A menu built before the deletion says so too.
            window.ShowStatus("");
            window.CopyCredential(id);
            Assert.Equal(Gone, window.StatusMessage.Text);
            Assert.False(window.VaultCard.IsOpen);
        });

        [Fact]
        public void Copy_reference_copies_the_reference_as_plain_text() => WithWindow((window, env, config) =>
        {
            string id = Bank(env);
            window.Editor.Text = SecretTokens.Format(id);
            var copied = new List<string>();
            window.SetClipboardText = copied.Add;
            var menu = new ContextMenu();
            window.FillPillMenu(menu, window.ReferenceAt(0)!.Value);
            var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Copy reference");

            Click(item);
            Assert.Equal(new[] { SecretTokens.Format(id) }, copied);

            var warnings = new List<string>();
            window.Warn = warnings.Add;
            window.SetClipboardText = _ => throw new System.Runtime.InteropServices.ExternalException("busy");
            Click(item);
            Assert.Equal("Copying a credential reference failed: busy", Assert.Single(warnings));
            Assert.Equal("The clipboard is busy. Try again in a moment.", window.InfoText.Text);
        });

        // ---- helpers ------------------------------------------------------------------------

        /// <summary>Flips a bit of the first credential's tag inside the sealed vault file, so its value no longer decrypts.</summary>
        private static void DamageValue(string path)
        {
            byte[] entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.Vault.v1");
            var doc = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser))!;
            var entry = doc["credentials"]!.AsArray()[0]!;
            byte[] tag = Convert.FromBase64String((string)entry["tag"]!);
            tag[0] ^= 1;
            entry["tag"] = Convert.ToBase64String(tag);
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(doc.ToJsonString()), entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Lays out an editor's text as a shown window would and returns its pills.</summary>
        private static List<InlineObjectElement> Pills(TextEditor editor)
        {
            var view = editor.TextArea.TextView;
            view.Measure(new Size(800, 400));
            view.Arrange(new Rect(0, 0, 800, 400));
            view.EnsureVisualLines();
            return view.VisualLines.SelectMany(l => l.Elements).OfType<InlineObjectElement>().ToList();
        }

        private static string PillText(InlineObjectElement pill) =>
            string.Concat(((Panel)((Border)pill.Element).Child).Children.OfType<TextBlock>().Select(t => t.Text));

        private static string PillBack(MicaPadWindow window) =>
            ((Border)Assert.Single(Pills(window.Editor)).Element).Background.ToString();

        /// <summary>Raises PreviewKeyDown on <paramref name="target"/>, tunnelling from the window as a real key would.</summary>
        private static KeyEventArgs PreviewKey(UIElement target, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new NoSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(args);
            return args;
        }

        /// <summary>A key event needs a source; this one has no window behind it.</summary>
        private sealed class NoSource : PresentationSource
        {
            public override Visual? RootVisual { get; set; }

            public override bool IsDisposed => false;

            protected override CompositionTarget? GetCompositionTargetCore() => null;
        }

        /// <summary>The secret clipboard's seams, swapped for fakes and put back on dispose.</summary>
        private sealed class FakeSecretClipboard : IDisposable
        {
            private readonly Func<IDataObject, bool> _set = SecretClipboard.SetData;
            private readonly Func<uint> _sequence = SecretClipboard.SequenceNumber;
            private readonly Action _clear = SecretClipboard.Clear;
            private readonly Action<TimeSpan, Action> _after = SecretClipboard.After;
            private uint _number = 7;

            public FakeSecretClipboard()
            {
                SecretClipboard.SetData = data =>
                {
                    if (Busy) return false;
                    Copied.Add((string)data.GetData(DataFormats.UnicodeText));
                    _number++;
                    return true;
                };
                SecretClipboard.SequenceNumber = () => _number;
                SecretClipboard.Clear = () => { };
                SecretClipboard.After = (delay, then) => Scheduled.Add(delay);
            }

            public bool Busy { get; set; }

            public List<string> Copied { get; } = new();

            public List<TimeSpan> Scheduled { get; } = new();

            public void Dispose()
            {
                SecretClipboard.ClearIfStillOurs();   // forgets the fake copy while the fakes are still in place
                SecretClipboard.SetData = _set;
                SecretClipboard.SequenceNumber = _sequence;
                SecretClipboard.Clear = _clear;
                SecretClipboard.After = _after;
            }
        }
    }
}
