using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pen = System.Windows.Media.Pen;
using Clipboard = System.Windows.Clipboard;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad: tabs of notes that save themselves.
    ///
    /// <para>
    /// Deliberately thin. Every rule about when text reaches disk, which versions are kept and
    /// what closing a tab means lives in <see cref="PadWorkspace"/>, which has no WPF types and
    /// is tested directly. This class turns those rules into documents, tabs, menus and keys.
    /// </para>
    ///
    /// <para>
    /// The close button hides the window instead of closing it: the notes stay open, the next
    /// show is instant, and nothing is ever asked. Only <see cref="PrepareForExit"/> lets it close
    /// for real, when MicaStats itself exits or Windows ends the session.
    /// </para>
    /// </summary>
    public partial class MicaPadWindow : Window
    {
        private const double ZoomStep = 0.1;
        private static MicaPadWindow? s_current;

        private readonly PadWorkspace _workspace;
        private readonly AppConfig _config;
        private readonly Dictionary<string, TextDocument> _docs = new();
        private readonly DispatcherTimer _tick;
        private OpenNote? _shown;
        private OpenNote? _renaming;
        private bool _suppressDirty;
        private bool _exiting;
        private PadPalette _palette = PadPalette.Dark;

        /// <summary>The note whose tab is being renamed, or null; for tests (the popup itself needs a shown window).</summary>
        internal OpenNote? RenamingNote => _renaming;

        /// <summary>Builds the window over a workspace; call <see cref="LoadSession"/> before showing it.</summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config)
        {
            InitializeComponent();
            _workspace = workspace;
            _config = config;

            ConfigureEditor();
            FindBar.Attach(Editor);
            ApplyTheme();
            Editor.ContextMenu = EditorMenu;
            Editor.ContextMenuOpening += (s, e) => RefreshEditorMenu();
            Editor.TextArea.PreviewMouseRightButtonDown += OnEditorRightButtonDown;
            PreviewEditor.ContextMenu = PreviewMenu;
            PreviewEditor.ContextMenuOpening += (s, e) => RefreshPreviewMenu();
            HistoryPanel.VersionSelected += OnVersionSelected;
            HistoryPanel.CloseRequested += CloseHistory;
            FindBar.ReplacingAll += () =>
            {
                if (_shown != null) _workspace.SnapshotNow(_shown, SnapshotReason.BeforeReplace);
            };

            TabStrip.ItemsSource = _workspace.Open;
            _workspace.Open.CollectionChanged += OnOpenChanged;
            _config.PropertyChanged += OnConfigChanged;
            Editor.TextArea.Caret.PositionChanged += (s, e) => UpdateCaretText();

            // Normal, not Background: sustained typing keeps input work queued, and a tick below it
            // could starve past the five-second autosave promise.
            _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (s, e) =>
            {
                _workspace.Tick();
                UpdateSaveText();
            };

            LoadIcon();
            SourceInitialized += OnSourceInitialized;
            Deactivated += (s, e) => _workspace.FlushPending();
            Activated += (s, e) => CheckShownNoteOnDisk();
            Closed += OnClosedForReal;
        }

        /// <summary>The window, when one exists (shown or hidden).</summary>
        public static MicaPadWindow? Current => s_current;

        /// <summary>Opens MicaStats' settings on the MicaPad section; set by the app.</summary>
        public Action? OpenSettingsRequested { get; set; }

        /// <summary>Shows MicaPad, creating it on first use, and brings it to the front.</summary>
        public static MicaPadWindow ShowOrActivate(PadWorkspace workspace, AppConfig config, Action? openSettings)
        {
            var window = s_current;
            if (window == null)
            {
                window = new MicaPadWindow(workspace, config) { OpenSettingsRequested = openSettings };
                try
                {
                    window.LoadSession();
                }
                catch
                {
                    // Cached only once built: a half-loaded window must not be the one the next show reuses.
                    window.CloseForExit();
                    throw;
                }
                s_current = window;
            }

            workspace.Session.WindowOpen = true;
            workspace.SaveSession();

            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Editor.Focus();
            return window;
        }

        /// <summary>
        /// Restores the saved tabs into documents and shows the active one. Called once, before the
        /// window is first shown. The workspace restore is idempotent, so a recreated window picks
        /// up the same notes.
        /// </summary>
        public void LoadSession()
        {
            _workspace.Restore();
            if (_workspace.Open.Count == 0) _workspace.NewNote();
            foreach (var note in _workspace.Open) EnsureDocument(note);

            Topmost = _workspace.Session.AlwaysOnTop;
            ApplyPlacement();
            ApplyEditorSettings();
            ShowNote(_workspace.Active ?? _workspace.Open[0]);
            _tick.Start();
        }

        /// <summary>
        /// Records where every tab stands and whether MicaPad is showing, for the next login, and
        /// lets the window close for real. Call before application exit or on session end.
        /// </summary>
        public void PrepareForExit()
        {
            if (_exiting) return;
            _exiting = true;
            CaptureViewState();
            _workspace.Session.WindowOpen = IsVisible;
        }

        /// <summary>Closes the window for real (tests and application exit).</summary>
        public void CloseForExit()
        {
            PrepareForExit();
            // Detach first: a window that was never shown may not raise Closed.
            Detach();
            Close();
        }

        /// <summary>A new scratch note in a new tab.</summary>
        internal void NewTab() => ShowNote(_workspace.NewNote());

        /// <summary>Closes a tab without asking. Closing the last one leaves a fresh empty note.</summary>
        internal void CloseTab(OpenNote note)
        {
            bool wasShown = ReferenceEquals(_shown, note);
            if (wasShown) _shown = null;

            _workspace.Close(note);
            if (_workspace.Open.Count == 0) _workspace.NewNote();
            if (wasShown || _shown == null) ShowNote(_workspace.Active ?? _workspace.Open[0]);
        }

        /// <summary>Shows the tab at a zero-based position; an out-of-range index is ignored.</summary>
        internal void SelectTab(int index)
        {
            if (index >= 0 && index < _workspace.Open.Count) ShowNote(_workspace.Open[index]);
        }

        /// <summary>
        /// Runs the window shortcut for a key. Returns false when it is not one, leaving the key to
        /// the editor. Called from PreviewKeyDown, so it sees keys before AvalonEdit does.
        /// </summary>
        internal bool HandleShortcut(Key key, ModifierKeys modifiers)
        {
            bool ctrl = modifiers == ModifierKeys.Control;
            bool ctrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            bool alt = modifiers == ModifierKeys.Alt;

            if (ctrl && key == Key.N) NewTab();
            else if (ctrl && key == Key.W) CloseActiveTab();
            else if (ctrlShift && key == Key.T) ReopenClosed();
            else if (ctrl && key == Key.Tab) CycleTab(+1);
            else if (ctrlShift && key == Key.Tab) CycleTab(-1);
            else if (ctrl && key >= Key.D1 && key <= Key.D9) SelectTab(key - Key.D1);
            else if (alt && key == Key.Z) ToggleWordWrap();
            else if (ctrl && (key == Key.OemPlus || key == Key.Add)) Zoom(ZoomStep);
            else if (ctrl && (key == Key.OemMinus || key == Key.Subtract)) Zoom(-ZoomStep);
            else if (ctrl && (key == Key.D0 || key == Key.NumPad0)) SetZoom(1.0);
            else if (ctrl && key == Key.O) OpenWithDialog();
            else if (ctrl && key == Key.S) SaveShown();
            else if (ctrlShift && key == Key.S) { if (_shown != null) SaveAs(_shown); }
            else if (ctrl && key == Key.F) FindBar.Open(replace: false);
            else if (ctrl && key == Key.H) FindBar.Open(replace: true);
            else if (modifiers == ModifierKeys.None && key == Key.F3) FindBar.FindNext();
            else if (modifiers == ModifierKeys.Shift && key == Key.F3) FindBar.FindPrevious();
            else if (ctrl && key == Key.G) ShowGoToLine();
            else if (modifiers == ModifierKeys.None && key == Key.Escape && FindBar.IsOpen) FindBar.Close();
            else if (ctrlShift && key == Key.H) ToggleHistory();
            else return false;
            return true;
        }

        /// <summary>Turns the close button into a hide unless the window is really exiting.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                CaptureViewState();
                _workspace.Session.WindowOpen = false;
                _workspace.FlushPending();
                _workspace.SaveSession();
                Hide();
            }
            base.OnClosing(e);
        }

        private void OnClosedForReal(object? sender, EventArgs e) => Detach();

        /// <summary>Stops the timer and unhooks from long-lived objects. Safe to call twice.</summary>
        private void Detach()
        {
            _tick.Stop();
            _config.PropertyChanged -= OnConfigChanged;
            _workspace.Open.CollectionChanged -= OnOpenChanged;
            if (ReferenceEquals(s_current, this)) s_current = null;
        }

        // ---- documents and tabs -------------------------------------------------------------

        private TextDocument EnsureDocument(OpenNote note)
        {
            if (_docs.TryGetValue(note.Id, out var existing)) return existing;

            var document = new TextDocument(note.TextProvider());
            document.UndoStack.ClearAll();
            document.Changed += (s, e) =>
            {
                _workspace.NotifyChanged(note, markUnsaved: !_suppressDirty);
                if (ReferenceEquals(_shown, note)) UpdateCharsText();
            };
            note.TextProvider = () => document.Text;
            _docs[note.Id] = document;
            return document;
        }

        /// <summary>
        /// Replaces a note's whole text as one undoable edit. With <paramref name="markUnsaved"/>
        /// false the change does not count as an edit of the file (a reload from the file itself).
        /// </summary>
        private void ReplaceText(OpenNote note, string text, bool markUnsaved)
        {
            var document = EnsureDocument(note);
            bool previous = _suppressDirty;
            _suppressDirty = !markUnsaved;
            try
            {
                document.Replace(0, document.TextLength, text);
            }
            finally
            {
                _suppressDirty = previous;
            }
        }

        private void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (OpenNote note in e.NewItems) EnsureDocument(note);
            if (e.OldItems != null)
                foreach (OpenNote note in e.OldItems) _docs.Remove(note.Id);
        }

        private void ShowNote(OpenNote note)
        {
            if (_shown != null && !ReferenceEquals(_shown, note))
            {
                SaveViewState(_shown);
                _workspace.FlushPending();
            }

            _shown = note;
            _workspace.SetActive(note);
            Editor.Document = EnsureDocument(note);
            RestoreViewState(note);
            UpdateCaretText();
            UpdateCharsText();
            UpdateSaveText();
            ScrollTabIntoView(note);
            UpdateFileText();
            if (_infoNote != null && !ReferenceEquals(_infoNote, note)) HideInfo();
            CheckDisk(note);
            if (HistoryPanel.Visibility == Visibility.Visible) ShowHistory();
        }

        private void CloseActiveTab()
        {
            if (_shown != null) CloseTab(_shown);
        }

        private void ReopenClosed()
        {
            var note = _workspace.ReopenLastClosed();
            if (note != null) ShowNote(note);
            else if (_workspace.ClosedNotes().Count > 0) ShowReopenFailed();   // not just an empty list
        }

        /// <summary>Reopen found the note but could not load it (its text cannot be read right now, say).</summary>
        private void ShowReopenFailed() =>
            ShowInfo("That note could not be reopened right now, so it was left as it is.", null);

        private void CycleTab(int delta)
        {
            int count = _workspace.Open.Count;
            if (count < 2 || _shown == null) return;
            int index = _workspace.Open.IndexOf(_shown);
            ShowNote(_workspace.Open[((index + delta) % count + count) % count]);
        }

        private void SaveViewState(OpenNote note) =>
            _workspace.SetTabViewState(note, Editor.CaretOffset, Editor.VerticalOffset);

        private void RestoreViewState(OpenNote note)
        {
            if (_workspace.Session.Tabs.TryGetValue(note.Id, out var view))
            {
                Editor.CaretOffset = Math.Clamp(view.CaretOffset, 0, Editor.Document.TextLength);
                double offset = view.VerticalOffset;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Editor.ScrollToVerticalOffset(offset)));
            }
            else
            {
                Editor.CaretOffset = 0;
            }
        }

        private void ScrollTabIntoView(OpenNote note)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (TabStrip.ItemContainerGenerator.ContainerFromItem(note) is FrameworkElement container)
                    container.BringIntoView();
            }));
        }

        private void OnTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not OpenNote note) return;
            if (e.ClickCount == 2) BeginRename(note, (FrameworkElement)sender);
            else ShowNote(note);
            e.Handled = true;
        }

        private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if ((sender as FrameworkElement)?.Tag is OpenNote note)
            {
                CloseTab(note);
                e.Handled = true;
            }
        }

        private void OnTabCloseClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OpenNote note) CloseTab(note);
            e.Handled = true;
        }

        /// <summary>Right-click on a tab: its menu, without switching to it first.</summary>
        private void OnTabMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement anchor || anchor.Tag is not OpenNote note) return;
            var menu = BuildTabMenu(note, anchor);
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// A tab's right-click menu. Closing from it is the ordinary close: nothing is deleted and
        /// nothing is asked. A tab backed by a real file adds its path and folder.
        /// </summary>
        internal ContextMenu BuildTabMenu(OpenNote note, FrameworkElement? anchor)
        {
            FrameworkElement target = anchor ?? TabStrip;
            var menu = NewMenu(target, PlacementMode.MousePoint);
            menu.Items.Add(Item("Rename…", null, () => BeginRename(note, target)));
            menu.Items.Add(Item("Close", null, () => CloseTab(note)));
            menu.Items.Add(Item("Close other tabs", null, () => CloseOtherTabs(note), _workspace.Open.Count > 1));

            if (note.Meta.SourcePath is string path)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Copy file path", null, () => CopyFilePath(path)));
                menu.Items.Add(Item("Show in folder", null, () => ShowInFolder(path)));
            }
            return menu;
        }

        /// <summary>
        /// Closes every tab except <paramref name="keep"/>, each as an ordinary close: flushed,
        /// snapshotted and moved to Closed notes, with a file's unsaved edits kept.
        /// </summary>
        internal void CloseOtherTabs(OpenNote keep)
        {
            ShowNote(keep);
            foreach (var other in new List<OpenNote>(_workspace.Open))
                if (!ReferenceEquals(other, keep)) CloseTab(other);
        }

        private void CopyFilePath(string path)
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                DiagnosticsLog.Warn("pad", "Copying a file path failed: " + ex.Message);
                ShowNotice("The clipboard is busy. Try again in a moment.");
            }
        }

        /// <summary>
        /// A passing notice from a menu action. It never replaces a disk question (file gone, changed
        /// on disk) that is waiting for an answer; it is logged instead.
        /// </summary>
        private void ShowNotice(string message)
        {
            if (_infoKind != null)
            {
                DiagnosticsLog.Warn("pad", message);
                return;
            }
            ShowInfo(message, null);
        }

        /// <summary>
        /// Opens Explorer with the file selected. A file that is no longer there is reported rather
        /// than opening some other folder.
        /// </summary>
        internal void ShowInFolder(string path)
        {
            if (!File.Exists(path))
            {
                ShowNotice("That file is no longer at " + path + ".");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DiagnosticsLog.Warn("pad", "Could not show a file in its folder: " + ex.Message);
                ShowNotice("Explorer could not be opened.");
            }
        }

        private void OnNewTabClick(object sender, RoutedEventArgs e) => NewTab();

        private void OnTabScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var visibility = TabScroller.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollLeftButton.Visibility = visibility;
            ScrollRightButton.Visibility = visibility;
        }

        private void OnScrollTabsLeft(object sender, RoutedEventArgs e) => TabScroller.LineLeft();

        private void OnScrollTabsRight(object sender, RoutedEventArgs e) => TabScroller.LineRight();

        private void BeginRename(OpenNote note, FrameworkElement anchor)
        {
            _renaming = note;
            RenameBox.Text = note.Title;
            RenamePopup.PlacementTarget = anchor;
            RenamePopup.IsOpen = true;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _renaming != null) _workspace.Rename(_renaming, RenameBox.Text);
            else if (e.Key != Key.Escape) return;

            RenamePopup.IsOpen = false;
            _renaming = null;
            Editor.Focus();
            e.Handled = true;
        }

        // ---- files --------------------------------------------------------------------------

        private const string FileFilter =
            "Text files|*.txt;*.log;*.ini;*.md;*.json;*.xml;*.csv;*.cfg;*.conf;*.yaml;*.yml|All files|*.*";

        private Action? _infoPrimary;
        private Action? _infoSecondary;
        private OpenNote? _infoNote;

        /// <summary>
        /// Which disk question the info bar is asking about <see cref="_infoNote"/>, or null for any
        /// other message. Lets the check on activation skip a question already showing without
        /// letting an unrelated message (lossy text, a failed save) hide the question.
        /// </summary>
        private string? _infoKind;

        private const string InfoChangedOnDisk = "changed-on-disk";
        private const string InfoFileGone = "file-gone";
        private const string InfoUnreachable = "unreachable";

        /// <summary>Opens a file in a tab, or reports in the info bar why it was not opened.</summary>
        public void OpenPath(string path)
        {
            var result = _workspace.OpenFile(path);
            string name = Path.GetFileName(path);
            switch (result.Status)
            {
                case OpenFileStatus.Opened:
                case OpenFileStatus.AlreadyOpen:
                    ShowNote(result.Note!);
                    if (result.Lossy) ShowLossyWarning(result.Note!);
                    break;
                case OpenFileStatus.NotFound:
                    ShowInfo("File not found: " + path, null);
                    break;
                case OpenFileStatus.TooLarge:
                    ShowInfo(name + " is larger than 50 MB and was not opened.", null);
                    break;
                case OpenFileStatus.Binary:
                    ShowInfo(name + " looks like a binary file and was not opened.", null);
                    break;
                case OpenFileStatus.ClosedNoteUnreadable:
                    ShowInfo(name + " has unsaved edits in a note that cannot be read right now, so it was not opened. Try again in a moment.", null);
                    break;
                default:
                    ShowInfo("Could not open " + path + ".", null);
                    break;
            }
        }

        /// <summary>Compares the shown note with its file; runs on every activation.</summary>
        internal void CheckShownNoteOnDisk()
        {
            if (_shown != null) CheckDisk(_shown);
        }

        /// <summary>
        /// Shows the info bar with a message and up to two actions. <paramref name="about"/> ties the
        /// bar to a note so switching tabs hides a question that no longer applies.
        /// </summary>
        internal void ShowInfo(string message, OpenNote? about, string? primaryLabel = null, Action? primary = null,
                               string? secondaryLabel = null, Action? secondary = null)
        {
            InfoText.Text = message;
            _infoNote = about;
            _infoKind = null;   // the disk questions set it after calling this
            _infoPrimary = primary;
            _infoSecondary = secondary;
            InfoPrimary.Content = primaryLabel;
            InfoPrimary.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
            InfoSecondary.Content = secondaryLabel;
            InfoSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
            InfoBar.Visibility = Visibility.Visible;
        }

        /// <summary>Hides the info bar and forgets its actions.</summary>
        internal void HideInfo()
        {
            InfoBar.Visibility = Visibility.Collapsed;
            _infoPrimary = null;
            _infoSecondary = null;
            _infoNote = null;
            _infoKind = null;
        }

        private void OnInfoPrimaryClick(object sender, RoutedEventArgs e)
        {
            var action = _infoPrimary;
            HideInfo();
            action?.Invoke();
        }

        private void OnInfoSecondaryClick(object sender, RoutedEventArgs e)
        {
            var action = _infoSecondary;
            HideInfo();
            action?.Invoke();
        }

        private void OnInfoCloseClick(object sender, RoutedEventArgs e) => HideInfo();

        private void CheckDisk(OpenNote note)
        {
            var action = _workspace.CheckDisk(note);
            if (action == DiskChangeAction.None)
            {
                HideDiskQuestion(note);   // the file is back as it was: a question about it no longer applies
                return;
            }
            if (action == DiskChangeAction.ReloadSilently)
            {
                Reload(note);
                return;
            }

            string? path = note.Meta.SourcePath;
            string name = Path.GetFileName(path) ?? note.Title;
            string kind = action == DiskChangeAction.AskReloadOrKeep ? InfoChangedOnDisk
                : IsFolderMissing(path) ? InfoUnreachable
                : InfoFileGone;

            // The same question is already showing: do not flicker it on every activation. Only
            // the same one: a lossy or failed-save message for this note must not hide it.
            if (InfoBar.Visibility == Visibility.Visible && ReferenceEquals(_infoNote, note) && _infoKind == kind) return;

            if (kind == InfoChangedOnDisk)
            {
                ShowInfo(IsStandIn(note)
                        ? name + " could not be read when this tab was restored, so this tab may not match the file."
                        : name + " changed on disk.",
                    note,
                    "Reload from disk", () => Reload(note),
                    "Keep mine", () => _workspace.KeepMine(note));
            }
            else if (kind == InfoFileGone)
            {
                ShowInfo(name + " no longer exists.", note,
                    "Save As…", () => SaveAs(note),
                    "Keep as note", () => { _workspace.DetachFromFile(note); UpdateFileText(); });
            }
            else
            {
                // Its folder is missing too: far more likely an offline share or an unplugged drive
                // than a deletion. Keep as note would detach a file that still exists, so nothing is offered.
                ShowInfo(name + " cannot be reached right now.", note);
            }
            _infoKind = kind;
        }

        /// <summary>
        /// True when the tab shows MicaPad's own copy of a file it could not read at restore or
        /// reopen (<see cref="SourceStamp.Unverified"/>). MicaPad never saw that file, so a disk
        /// question must not claim the file changed; it says the file was not read instead.
        /// </summary>
        private static bool IsStandIn(OpenNote note) => note.Meta.SourceStamp == SourceStamp.Unverified;

        /// <summary>True when the file's folder is missing as well as the file.</summary>
        private static bool IsFolderMissing(string? path)
        {
            string? folder = path == null ? null : Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(folder) && !Directory.Exists(folder);
        }

        /// <summary>Hides a disk question about <paramref name="note"/> that no longer applies.</summary>
        private void HideDiskQuestion(OpenNote note)
        {
            if (_infoKind != null && ReferenceEquals(_infoNote, note)) HideInfo();
        }

        private void Reload(OpenNote note)
        {
            string? text = _workspace.ReloadFromDisk(note, out var status, out bool lossy);
            string name = Path.GetFileName(note.Meta.SourcePath) ?? note.Title;
            if (text == null)
            {
                ShowInfo(status == OpenFileStatus.EditsWouldBeLost
                    ? name + " changed on disk, but this note is too large to keep a copy of your version, so it was not reloaded. Use Save As to keep your version first."
                    : "Could not reload " + name + ": " + ReloadFailureReason(status) + ".", note);
                return;
            }
            ReplaceText(note, text, markUnsaved: false);
            UpdateFileText();
            HideDiskQuestion(note);   // a silent reload answers any question still showing about the file
            if (lossy) ShowLossyWarning(note);
        }

        /// <summary>Why a reload failed, in words rather than an enum name.</summary>
        private static string ReloadFailureReason(OpenFileStatus status) => status switch
        {
            OpenFileStatus.NotFound => "the file is gone",
            OpenFileStatus.TooLarge => "it is now larger than 50 MB",
            OpenFileStatus.Binary => "it no longer looks like text",
            _ => "it could not be read",
        };

        /// <summary>
        /// Warns that the file held bytes its encoding could not decode: they show as the
        /// replacement character, and saving writes them that way.
        /// </summary>
        private void ShowLossyWarning(OpenNote note)
        {
            const char Replacement = (char)0xFFFD;
            string name = Path.GetFileName(note.Meta.SourcePath) ?? note.Title;
            ShowInfo(name + " has bytes that are not valid " + TextFileCodec.Describe(note.Meta.Encoding, note.Meta.CodePage) +
                     ". They show as " + Replacement + ", and saving will write them that way.", note);
        }

        private void OpenWithDialog()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = FileFilter };
            if (dialog.ShowDialog(this) != true) return;
            foreach (string file in dialog.FileNames) OpenPath(file);
        }

        private void SaveShown()
        {
            if (_shown != null) Save(_shown);
        }

        private void Save(OpenNote note, bool overwriteExternalChanges = false, bool overwriteWithoutCopy = false) =>
            HandleSaveResult(note, _workspace.SaveToSource(note, overwriteExternalChanges, overwriteWithoutCopy),
                             () => Save(note, overwriteExternalChanges, overwriteWithoutCopy));

        private void SaveAs(OpenNote note)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = FileFilter,
                AddExtension = true,
                DefaultExt = ".txt",
                FileName = note.Meta.IsFileBacked ? Path.GetFileName(note.Meta.SourcePath) : SafeFileName(note.Title) + ".txt",
                InitialDirectory = note.Meta.IsFileBacked
                    ? Path.GetDirectoryName(note.Meta.SourcePath)
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true) return;
            string? path = dialog.FileName;
            if (string.IsNullOrEmpty(path)) return;
            SaveAsPath(note, path);
        }

        private void SaveAsPath(OpenNote note, string path) =>
            HandleSaveResult(note, _workspace.SaveAs(note, path), () => SaveAsPath(note, path));

        private void HandleSaveResult(OpenNote note, SaveToFileResult result, Action retry)
        {
            switch (result.Status)
            {
                case SaveToFileStatus.Saved:
                    if (ReferenceEquals(_infoNote, note)) HideInfo();
                    UpdateFileText();
                    UpdateSaveText();
                    break;
                case SaveToFileStatus.NeedsSaveAs:
                    SaveAs(note);
                    break;
                case SaveToFileStatus.Lossy:
                    ShowInfo(TextFileCodec.Describe(note.Meta.Encoding, note.Meta.CodePage) + " cannot store some characters in this note. Nothing was saved.",
                        note,
                        "Save as UTF-8", () =>
                        {
                            _workspace.SetEncoding(note, PadEncoding.Utf8, 0);
                            UpdateFileText();
                            retry();
                        },
                        "Cancel", () => { });
                    break;
                case SaveToFileStatus.ChangedOnDisk:
                    ShowInfo((Path.GetFileName(note.Meta.SourcePath) ?? note.Title) + (IsStandIn(note)
                            ? " could not be read when this tab was restored, so saving could replace text you have not seen."
                            : " changed on disk since it was opened."),
                        note,
                        "Overwrite", () => Save(note, overwriteExternalChanges: true),
                        "Reload from disk", () => Reload(note));
                    _infoKind = InfoChangedOnDisk;
                    break;
                case SaveToFileStatus.OutsideVersionNotKept:
                    ShowInfo((Path.GetFileName(note.Meta.SourcePath) ?? note.Title) +
                             " changed on disk, and that version cannot be kept in History: " + result.Error + ". Overwrite it anyway?",
                        note,
                        "Overwrite anyway", () => Save(note, overwriteExternalChanges: true, overwriteWithoutCopy: true),
                        "Reload from disk", () => Reload(note));
                    _infoKind = InfoChangedOnDisk;   // the check on activation leaves this question showing
                    break;
                default:
                    ShowInfo("Could not save: " + result.Error + " Your text is kept here.", note, "Save As…", () => SaveAs(note));
                    break;
            }
        }

        private static string SafeFileName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = title.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string name = new string(chars).Trim();
            return name.Length == 0 ? "Untitled" : name;
        }

        private void UpdateFileText()
        {
            if (_shown == null) return;
            EncodingButton.Content = TextFileCodec.Describe(_shown.Meta.Encoding, _shown.Meta.CodePage);
            EolButton.Content = TextFileCodec.Describe(_shown.Meta.LineEnding);
        }

        private void OnEncodingClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            var note = _shown;
            int ansi = TextFileCodec.SystemAnsiCodePage;
            var menu = NewMenu(EncodingButton, PlacementMode.Top);
            foreach (var (encoding, codePage) in new[]
            {
                (PadEncoding.Utf8, 0), (PadEncoding.Utf8Bom, 0), (PadEncoding.Utf16Le, 0), (PadEncoding.Utf16Be, 0), (PadEncoding.Ansi, ansi),
            })
            {
                var chosen = encoding;
                int chosenPage = codePage;
                menu.Items.Add(Check(TextFileCodec.Describe(encoding, codePage), null, note.Meta.Encoding == encoding, () =>
                {
                    _workspace.SetEncoding(note, chosen, chosenPage);
                    UpdateFileText();
                }));
            }
            menu.IsOpen = true;
        }

        private void OnEolClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            var note = _shown;
            var menu = NewMenu(EolButton, PlacementMode.Top);
            foreach (var ending in new[] { LineEnding.CrLf, LineEnding.Lf })
            {
                var chosen = ending;
                menu.Items.Add(Check(TextFileCodec.Describe(ending), null, note.Meta.LineEnding == ending,
                    () => ConvertLineEndings(note, chosen)));
            }
            menu.IsOpen = true;
        }

        private void ConvertLineEndings(OpenNote note, LineEnding ending)
        {
            string converted = _workspace.ConvertLineEndings(note, ending);
            if (converted != EnsureDocument(note).Text) ReplaceText(note, converted, markUnsaved: true);
            UpdateFileText();
        }

        // ---- go to line ---------------------------------------------------------------------

        /// <summary>Shows the go-to-line box over the editor, seeded with the current line.</summary>
        internal void ShowGoToLine()
        {
            GoToLineInput.Text = Editor.TextArea.Caret.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
            GoToLineBox.Visibility = Visibility.Visible;
            GoToLineInput.Focus();
            GoToLineInput.SelectAll();
        }

        /// <summary>Moves the caret to the start of a line, clamped to the document.</summary>
        internal void GoToLine(int line)
        {
            var document = Editor.Document;
            line = Math.Clamp(line, 1, document.LineCount);
            Editor.CaretOffset = document.GetLineByNumber(line).Offset;
            Editor.ScrollToLine(line);
        }

        private void OnGoToLineKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (int.TryParse(GoToLineInput.Text.Trim(), System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int line))
                {
                    GoToLine(line);
                }
            }
            else if (e.Key != Key.Escape)
            {
                return;
            }

            GoToLineBox.Visibility = Visibility.Collapsed;
            Editor.Focus();
            e.Handled = true;
        }

        private void OnGoToLineLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            GoToLineBox.Visibility = Visibility.Collapsed;

        // ---- history and closed notes -------------------------------------------------------

        private List<ClosedNoteRow> _closedRows = new();

        private void OnHistoryClick(object sender, RoutedEventArgs e) => ToggleHistory();

        private void ToggleHistory()
        {
            if (HistoryPanel.Visibility == Visibility.Visible) CloseHistory();
            else ShowHistory();
        }

        private void ShowHistory()
        {
            if (_shown == null) return;
            EndPreview();
            // A pause or forced snapshot may still be queued; list what is really on disk.
            _workspace.FlushWrites(TimeSpan.FromSeconds(1));
            HistoryPanel.Show(_workspace.History(_shown), DateTime.Now,
                              Editor.Document.TextLength > HistoryPolicy.MaxSnapshotChars);
        }

        private void CloseHistory()
        {
            EndPreview();
            HistoryPanel.Visibility = Visibility.Collapsed;
            Editor.Focus();
        }

        private void OnVersionSelected(SnapshotInfo snapshot)
        {
            string? text = _workspace.ReadSnapshot(snapshot);
            if (text == null)
            {
                ShowInfo("That version could not be read.", _shown);
                return;
            }

            PreviewEditor.FontFamily = Editor.FontFamily;
            PreviewEditor.FontSize = Editor.FontSize;
            PreviewEditor.WordWrap = Editor.WordWrap;
            PreviewEditor.Text = text;
            PreviewText.Text = "Viewing " + HistoryRows.When(snapshot.Stamp, DateTime.Now);
            PreviewPanel.Visibility = Visibility.Visible;
        }

        private void EndPreview()
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewEditor.Text = "";
            HistoryPanel.ClearSelection();
        }

        private void OnPreviewBackClick(object sender, RoutedEventArgs e)
        {
            EndPreview();
            Editor.Focus();
        }

        private void OnPreviewCopyClick(object sender, RoutedEventArgs e)
        {
            if (PreviewEditor.Text.Length == 0) return;
            try
            {
                Clipboard.SetText(PreviewEditor.Text);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                DiagnosticsLog.Warn("pad", "Copying a version failed: " + ex.Message);
            }
        }

        /// <summary>Snapshots the current text, then puts the old version in as one undoable edit.</summary>
        private void OnPreviewRestoreClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null || PreviewPanel.Visibility != Visibility.Visible) return;

            string text = PreviewEditor.Text;
            _workspace.SnapshotNow(_shown, SnapshotReason.BeforeReplace);
            ReplaceText(_shown, text, markUnsaved: true);
            ShowHistory();
            Editor.Focus();
        }

        private void OnClosedNotesClick(object sender, RoutedEventArgs e)
        {
            _closedRows = new List<ClosedNoteRow>(HistoryRows.Closed(_workspace.ClosedNotes(), DateTime.Now));
            ClosedSearch.Text = "";
            FilterClosed();
            ClosedPopup.IsOpen = true;
            ClosedSearch.Focus();
        }

        private void OnClosedSearchChanged(object sender, TextChangedEventArgs e) => FilterClosed();

        private void FilterClosed()
        {
            string query = ClosedSearch.Text.Trim();
            var rows = _closedRows.FindAll(r => r.Matches(query));
            ClosedList.ItemsSource = rows;
            ClosedEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnClosedReopenClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClosedNoteRow row) return;
            ClosedPopup.IsOpen = false;
            var note = _workspace.Reopen(row.Id);
            if (note != null) ShowNote(note);
            else ShowReopenFailed();
        }

        private void OnClosedDeleteClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClosedNoteRow row) return;

            if (!_workspace.DeleteClosed(row.Id))
            {
                // Kept, and still listed: close the popup so the explanation is not hidden behind it.
                ClosedPopup.IsOpen = false;
                ShowInfo("That note could not be deleted right now, so it was kept.", null);
                return;
            }

            _closedRows.Remove(row);
            FilterClosed();
        }

        // ---- keys, menu, settings -----------------------------------------------------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Zoom(e.Delta > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
        }

        private void OnMenuButtonClick(object sender, RoutedEventArgs e)
        {
            var menu = NewMenu(MenuButton, PlacementMode.Bottom);
            menu.Items.Add(Item("New note", "Ctrl+N", NewTab));
            menu.Items.Add(Item("Open…", "Ctrl+O", OpenWithDialog));
            menu.Items.Add(Item("Save", "Ctrl+S", SaveShown));
            menu.Items.Add(Item("Save As…", "Ctrl+Shift+S", () => { if (_shown != null) SaveAs(_shown); }));
            menu.Items.Add(Item("Close tab", "Ctrl+W", CloseActiveTab));
            menu.Items.Add(Item("Reopen closed tab", "Ctrl+Shift+T", ReopenClosed));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false)));
            menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true)));
            menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine));
            menu.Items.Add(Item("History", "Ctrl+Shift+H", ToggleHistory));
            menu.Items.Add(new Separator());
            menu.Items.Add(Check("Word wrap", "Alt+Z", _config.PadWordWrap, ToggleWordWrap));
            menu.Items.Add(Check("Line numbers", null, _config.PadShowLineNumbers,
                () => _config.PadShowLineNumbers = !_config.PadShowLineNumbers));
            menu.Items.Add(Check("Always on top", null, Topmost, ToggleTopmost));
            menu.Items.Add(Item("Font…", null, ChooseFont));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open notes folder", null, OpenNotesFolder));
            menu.Items.Add(Item("Settings", null, () => OpenSettingsRequested?.Invoke()));
            menu.IsOpen = true;
        }

        internal ContextMenu NewMenu(UIElement target, PlacementMode placement)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            return menu;
        }

        private static MenuItem Item(string header, string? gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            item.Click += (s, e) => action();
            return item;
        }

        private static MenuItem Check(string header, string? gesture, bool isChecked, Action action)
        {
            var item = Item(header, gesture, action);
            item.IsCheckable = true;
            item.IsChecked = isChecked;
            return item;
        }

        // ---- right-click menus --------------------------------------------------------------

        /// <summary>The editor's right-click menu. One instance; its items are rebuilt each time it opens.</summary>
        internal ContextMenu EditorMenu { get; } = new();

        /// <summary>The history preview's right-click menu: copying only, because the preview is read-only.</summary>
        internal ContextMenu PreviewMenu { get; } = new();

        /// <summary>Rebuilds the editor menu for the current selection, undo state and theme.</summary>
        internal void RefreshEditorMenu() => FillEditorMenu(EditorMenu, Editor, readOnly: false);

        /// <summary>Rebuilds the history preview's menu.</summary>
        internal void RefreshPreviewMenu() => FillEditorMenu(PreviewMenu, PreviewEditor, readOnly: true);

        /// <summary>
        /// The items of a text menu, each disabled when it cannot apply. Later parts add their groups
        /// (Format, Lines, Tools) here, before the Find group.
        /// </summary>
        private void FillEditorMenu(ContextMenu menu, ICSharpCode.AvalonEdit.TextEditor editor, bool readOnly)
        {
            menu.Items.Clear();
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            bool hasSelection = editor.SelectionLength > 0;
            bool hasText = editor.Document != null && editor.Document.TextLength > 0;

            if (!readOnly)
            {
                menu.Items.Add(Item("Undo", "Ctrl+Z", () => editor.Undo(), editor.CanUndo));
                menu.Items.Add(Item("Redo", "Ctrl+Y", () => editor.Redo(), editor.CanRedo));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Cut", "Ctrl+X", () => editor.Cut(), hasSelection));
            }
            menu.Items.Add(Item("Copy", "Ctrl+C", () => editor.Copy(), hasSelection));
            if (!readOnly)
            {
                menu.Items.Add(Item("Paste", "Ctrl+V", () => editor.Paste(), ClipboardHasText()));
                menu.Items.Add(Item("Delete", "Del", () => System.Windows.Input.ApplicationCommands.Delete.Execute(null, editor.TextArea), hasSelection));
            }
            menu.Items.Add(Item("Select all", "Ctrl+A", () => editor.SelectAll(), hasText));

            if (!readOnly)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false)));
                menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true)));
                menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine));
            }
        }

        /// <summary>True when Paste has something to paste. A busy clipboard counts as yes: Paste itself then tries.</summary>
        private static bool ClipboardHasText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return true;
            }
        }

        private void OnEditorRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position is { } at) PlaceCaretForMenu(Editor.Document.GetOffset(at.Location));
        }

        /// <summary>
        /// A right-click moves the caret to the click, like Notepad, unless it lands inside the
        /// selection: then the selection stays, so Cut and Copy act on it.
        /// </summary>
        internal void PlaceCaretForMenu(int offset)
        {
            offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
            int start = Editor.SelectionStart;
            int end = start + Editor.SelectionLength;
            if (Editor.SelectionLength > 0 && offset >= start && offset <= end) return;

            Editor.TextArea.ClearSelection();
            Editor.CaretOffset = offset;
        }

        private void ToggleWordWrap() => _config.PadWordWrap = !_config.PadWordWrap;

        private void ToggleTopmost()
        {
            Topmost = !Topmost;
            _workspace.Session.AlwaysOnTop = Topmost;
            _workspace.SaveSession();
        }

        private void Zoom(double delta) => SetZoom(_workspace.Session.Zoom + delta);

        private void SetZoom(double zoom)
        {
            _workspace.Session.Zoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 4.0);
            ApplyEditorSettings();
        }

        private void ChooseFont()
        {
            try
            {
                using var current = new System.Drawing.Font(_config.PadFontFamily, (float)(_config.PadFontSize * 72.0 / 96.0));
                using var dialog = new System.Windows.Forms.FontDialog
                {
                    Font = current,
                    ShowEffects = false,
                    AllowScriptChange = false,
                    FontMustExist = true,
                };
                var owner = new Win32Owner(new WindowInteropHelper(this).Handle);
                if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;

                _config.PadFontFamily = dialog.Font.FontFamily.Name;
                _config.PadFontSize = Math.Round(dialog.Font.SizeInPoints * 96.0 / 72.0, 1);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DiagnosticsLog.Warn("pad", "The font dialog failed: " + ex.Message);
            }
        }

        private void OpenNotesFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _workspace.Store.Root + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DiagnosticsLog.Warn("pad", "Could not open the notes folder: " + ex.Message);
            }
        }

        private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppConfig.PadFontFamily) or nameof(AppConfig.PadFontSize)
                or nameof(AppConfig.PadWordWrap) or nameof(AppConfig.PadShowLineNumbers))
            {
                if (Dispatcher.CheckAccess()) ApplyEditorSettings();
                else Dispatcher.BeginInvoke(new Action(ApplyEditorSettings));
            }
            else if (e.PropertyName == nameof(AppConfig.PadTheme))
            {
                if (Dispatcher.CheckAccess()) ApplyTheme();
                else Dispatcher.BeginInvoke(new Action(ApplyTheme));
            }
        }

        private void ConfigureEditor()
        {
            var options = Editor.Options;
            options.EnableHyperlinks = false;
            options.EnableEmailHyperlinks = false;
            options.HighlightCurrentLine = true;
            options.EnableRectangularSelection = true;
            options.ConvertTabsToSpaces = false;

            var area = Editor.TextArea;
            area.SelectionForeground = null;
            area.SelectionBorder = null;
            area.SelectionCornerRadius = 0;
            area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        }

        /// <summary>The palette MicaPad is painted with.</summary>
        internal PadPalette Palette => _palette;

        /// <summary>
        /// Paints MicaPad in the theme the config names: the Pad.* brushes the XAML reads, the
        /// ModernWpf controls, the editor and find highlights, the theme button and the title bar.
        /// Only this window changes; the rest of MicaStats keeps its look.
        /// </summary>
        private void ApplyTheme()
        {
            _palette = PadPalette.For(_config.PadTheme);
            PadThemeApplier.ApplyResources(Resources, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(this, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            var area = Editor.TextArea;
            area.SelectionBrush = PadThemeApplier.ToBrush(_palette.Selection);
            area.Caret.CaretBrush = PadThemeApplier.ToBrush(_palette.Caret);
            area.TextView.CurrentLineBackground = PadThemeApplier.ToBrush(_palette.CurrentLine);
            Editor.LineNumbersForeground = PadThemeApplier.ToBrush(_palette.LineNumbers);
            PreviewEditor.LineNumbersForeground = Editor.LineNumbersForeground;
            PreviewEditor.TextArea.SelectionBrush = area.SelectionBrush;
            FindBar.ApplyPalette(_palette);

            // Sun (E706) offers the light theme, moon (E708) the dark one.
            ThemeButton.Content = _palette.IsDark ? "\uE706" : "\uE708";
            ThemeButton.ToolTip = _palette.IsDark ? "Switch to light theme" : "Switch to dark theme";
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
        }

        /// <summary>The theme button: switches between dark and light, remembered in the config.</summary>
        internal void ToggleTheme() => _config.PadTheme = _palette.IsDark ? PadThemes.Light : PadThemes.Dark;

        private void OnThemeButtonClick(object sender, RoutedEventArgs e) => ToggleTheme();

        private void ApplyEditorSettings()
        {
            Editor.FontFamily = new FontFamily(_config.PadFontFamily + ", Cascadia Mono, Consolas");
            Editor.FontSize = _config.PadFontSize * _workspace.Session.Zoom;
            Editor.WordWrap = _config.PadWordWrap;
            Editor.ShowLineNumbers = _config.PadShowLineNumbers;
        }

        // ---- status bar ---------------------------------------------------------------------

        private void UpdateCaretText()
        {
            var caret = Editor.TextArea.Caret;
            CaretText.Text = PadText.CaretPosition(caret.Line, caret.Column);
        }

        private void UpdateCharsText() => CharsText.Text = PadText.CharCount(Editor.Document?.TextLength ?? 0);

        private void UpdateSaveText()
        {
            if (_shown == null) return;

            string title = "MicaPad — " + _shown.Title;
            if (Title != title) Title = title;

            if (_shown.SaveState == SaveState.Failed)
            {
                SaveText.Text = "Not saved — retrying";
                SaveText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");
                return;
            }

            SaveText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.Muted");
            if (_shown.SaveState == SaveState.Saving || _workspace.HasPendingChanges(_shown)) SaveText.Text = "Saving…";
            else if (_shown.LastSavedUtc is DateTime saved) SaveText.Text = PadText.SavedAgo(DateTime.UtcNow - saved);
            else SaveText.Text = "Saved";
        }

        // ---- placement, identity, icon ------------------------------------------------------

        private void ApplyPlacement()
        {
            var session = _workspace.Session;
            Width = Math.Max(MinWidth, session.Width);
            Height = Math.Max(MinHeight, session.Height);

            if (session.Left is double left && session.Top is double top &&
                PadPlacement.IsReachable(left, top, Width, Height,
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (session.Maximized) WindowState = WindowState.Maximized;
        }

        /// <summary>Records the active tab's caret and scroll, and the window's placement.</summary>
        private void CaptureViewState()
        {
            if (_shown != null) SaveViewState(_shown);

            var session = _workspace.Session;
            session.AlwaysOnTop = Topmost;
            if (!IsLoaded) return;

            session.Maximized = WindowState == WindowState.Maximized;
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
            {
                session.Left = bounds.Left;
                session.Top = bounds.Top;
                session.Width = bounds.Width;
                session.Height = bounds.Height;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MicaStats.exe");
            string icon = Path.Combine(AppContext.BaseDirectory, "micapad.ico");
            TaskbarIdentity.Apply(hwnd, "Kil0bit.SystemMonitor.MicaPad", "\"" + exe + "\" " + PadArguments.Flag, "MicaPad", icon + ",0");
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
        }

        private void LoadIcon()
        {
            string icon = Path.Combine(AppContext.BaseDirectory, "micapad.ico");
            if (!File.Exists(icon)) return;
            try
            {
                Icon = BitmapFrame.Create(new Uri(icon));
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                DiagnosticsLog.Warn("pad", "The MicaPad icon could not be loaded: " + ex.Message);
            }
        }

        /// <summary>Lets a WinForms dialog be modal to this WPF window.</summary>
        private sealed class Win32Owner : System.Windows.Forms.IWin32Window
        {
            public Win32Owner(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }
    }
}
