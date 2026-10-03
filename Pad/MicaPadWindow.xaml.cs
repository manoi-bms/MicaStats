using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using static Kil0bitSystemMonitor.Pad.EditorMenus;

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

        private readonly PadWorkspace _workspace;
        private readonly AppConfig _config;

        /// <summary>The session window this window shows, as asked for at construction; null means the first.</summary>
        private readonly string? _requestedWindowId;

        /// <summary>This window's id in the session (spec 5.3); set by <see cref="LoadSession"/>.</summary>
        private string _windowId = "";

        /// <summary>This window's placement, zoom and on-top state in the session; set by <see cref="LoadSession"/>.</summary>
        private PadWindowState? _state;

        /// <summary>True while the close button has hidden this window (it was the last one).</summary>
        private bool _hidden;

        /// <summary>Each document's change handler, so a document can be handed to another window.</summary>
        private readonly Dictionary<TextDocument, EventHandler<DocumentChangeEventArgs>> _docHandlers = new();

        /// <summary>This window's id in the session; for tests and the window registry.</summary>
        internal string WindowId => _windowId;
        private readonly Dictionary<string, TextDocument> _docs = new();
        private readonly DispatcherTimer _tick;
        private OpenNote? _shown;
        private OpenNote? _renaming;
        private bool _suppressDirty;
        private bool _exiting;
        private PadPalette _palette = PadPalette.Dark;
        private EditorLanguage _language = null!;
        private EditorLanguage _previewLanguage = null!;
        private AutoCloseHandler _autoClose = null!;
        private readonly BookmarkController _bookmarks = new();
        private BookmarkMargin _bookmarkMargin = null!;
        private readonly OccurrenceHighlighter _occurrences = new();
        private DispatcherTimer _occurrenceTimer = null!;
        private ResolvedLanguage _resolved = new(PadLanguages.Plain, false);

        /// <summary>The note whose tab is being renamed, or null; for tests (the popup itself needs a shown window).</summary>
        internal OpenNote? RenamingNote => _renaming;

        /// <summary>Builds the window over the workspace's first window; call <see cref="LoadSession"/> before showing it.</summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config) : this(workspace, config, null)
        {
        }

        /// <summary>
        /// Builds the window for one of the workspace's windows (spec 5.3); null means the first.
        /// Call <see cref="LoadSession"/> before showing it.
        /// </summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config, string? windowId)
        {
            InitializeComponent();
            _workspace = workspace;
            _config = config;
            _requestedWindowId = windowId;

            ConfigureEditor();
            ConfigureLinks(Editor);
            ConfigureLinks(PreviewEditor);
            ConfigureVault();
            Editor.TextArea.TextView.MouseHover += OnEditorMouseHover;
            Editor.TextArea.TextView.MouseHoverStopped += (s, e) => _linkTip.IsOpen = false;
            FindBar.Attach(Editor);
            _language = new EditorLanguage(Editor, () => _palette, folds: true);
            _language.FellBackToPlain += ApplyEditorFont;
            _language.CodeCopy = CopyCode;   // the history preview gets no Copy button
            _previewLanguage = new EditorLanguage(PreviewEditor, () => _palette, folds: false);
            ConfigureDiagrams();
            _diff = new DiffPreview(PreviewEditor, () => _palette);
            _autoClose = new AutoCloseHandler(Editor, () => _config.PadAutoClose);
            _bookmarkMargin = new BookmarkMargin(() => _bookmarks.Lines(Editor.Document), () => _palette);
            Editor.TextArea.LeftMargins.Insert(0, _bookmarkMargin);
            _bookmarks.Changed += OnBookmarksChanged;
            // Background layer: find matches draw on the Selection layer (MatchHighlighter), so they are above these boxes whatever the order here.
            Editor.TextArea.TextView.BackgroundRenderers.Insert(0, _occurrences);
            Editor.DocumentChanged += (s, e) => HookOccurrenceDocument();
            HookOccurrenceDocument();
            _occurrenceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _occurrenceTimer.Tick += (s, e) =>
            {
                _occurrenceTimer.Stop();
                RefreshOccurrences();
            };
            Editor.TextArea.SelectionChanged += (s, e) =>
            {
                _occurrenceTimer.Stop();
                _occurrenceTimer.Start();
            };
            ApplyTheme();
            Editor.ContextMenu = EditorMenu;
            Editor.ContextMenuOpening += (s, e) => RefreshEditorMenu(OpenedByMouse(e));
            Editor.TextArea.PreviewMouseRightButtonDown += OnEditorRightButtonDown;
            PreviewEditor.ContextMenu = PreviewMenu;
            PreviewEditor.ContextMenuOpening += (s, e) => RefreshPreviewMenu(OpenedByMouse(e));
            HistoryPanel.VersionSelected += OnVersionSelected;
            HistoryPanel.CloseRequested += CloseHistory;
            SearchPanel.CloseRequested += CloseSearch;
            SearchPanel.ReturnRequested += () => Editor.Focus();
            SearchPanel.ResultChosen += OpenSearchResult;
            SearchPanel.Run = RunSearchAsync;
            ConfigureAi();
            FindBar.ReplacingAll += () =>
            {
                if (_shown != null) _workspace.SnapshotNow(_shown, SnapshotReason.BeforeReplace);
            };

            _tabDrag = new TabDragController(TabStrip, TabScroller, () => _workspace.TabsOf(_windowId),
                (note, index) => _workspace.MoveTab(note, index),
                () => _workspace.SaveSession());
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
                RetryPendingScrubsOnTick();
            };

            LoadIcon();
            SourceInitialized += OnSourceInitialized;
            Deactivated += (s, e) => _workspace.FlushPending();
            Activated += (s, e) =>
            {
                if (_windowId.Length > 0) _workspace.ActivateWindow(_windowId);
                CheckShownNoteOnDisk();
            };
            Closed += OnClosedForReal;
        }

        /// <summary>Opens MicaStats' settings on the MicaPad section; set by the app.</summary>
        public Action? OpenSettingsRequested { get; set; }

        // ---- windows (spec 5.3) --------------------------------------------------------------

        /// <summary>Every loaded MicaPad window, shown or hidden, in load order. Changed on the UI thread only.</summary>
        private static readonly List<MicaPadWindow> s_windows = new();

        /// <summary>True once <see cref="CloseForExit"/> ran: a window closes once.</summary>
        private bool _closedForExit;

        /// <summary>Shows a window and brings it to the front. Tests replace it: tests never show windows.</summary>
        internal static Action<MicaPadWindow> ShowWindow { get; set; } = window =>
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Editor.Focus();
        };

        /// <summary>The loaded windows over <paramref name="workspace"/>, in load order.</summary>
        internal static IReadOnlyList<MicaPadWindow> WindowsOf(PadWorkspace workspace) =>
            s_windows.Where(w => ReferenceEquals(w._workspace, workspace)).ToList();

        /// <summary>The loaded window showing <paramref name="windowId"/>, or null.</summary>
        private static MicaPadWindow? Registered(PadWorkspace workspace, string windowId) =>
            s_windows.FirstOrDefault(w => ReferenceEquals(w._workspace, workspace) && w._windowId == windowId);

        /// <summary>The most recently active MicaPad window (shown or hidden), or null when none is loaded.</summary>
        public static MicaPadWindow? Current => s_windows.Count == 0 ? null : CurrentOf(s_windows[s_windows.Count - 1]._workspace);

        /// <summary>
        /// The most recently active loaded window over <paramref name="workspace"/>, or null. Tests
        /// ask per workspace: the shared UI test thread can run another test's windows in between.
        /// </summary>
        internal static MicaPadWindow? CurrentOf(PadWorkspace workspace)
        {
            foreach (string id in workspace.ActivationOrder)
                if (Registered(workspace, id) is { } window) return window;
            return WindowsOf(workspace).FirstOrDefault();
        }

        /// <summary>True while this window is hidden by its close button (it was the last one); for tests.</summary>
        internal bool IsHiddenByClose => _hidden;

        /// <summary>
        /// Shows MicaPad (the hotkey, the overlay menu, the Start menu): the most recently active
        /// window comes to the front — or, with <paramref name="path"/>, the window already showing
        /// that file. The first show in a run brings back every window of the session, each where it
        /// was (spec 5.3: reopen at login restores every window), so no tab can wait in a window
        /// nobody shows; the target ends up in front. A window that fails to load is logged and
        /// skipped (its tabs wait in the session for the next start), so the others still come up.
        /// </summary>
        public static MicaPadWindow ShowOrActivate(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path = null)
        {
            workspace.Restore();
            // A file already open in a window goes there; anything else to the most recently active window.
            string targetId = workspace.RouteFile(path);
            if (WindowsOf(workspace).Count == 0)
            {
                // Least recently active first, so each later one comes up in front of it.
                foreach (string id in workspace.ActivationOrder.Reverse().Where(id => id != targetId).ToList())
                    Guard("Bringing back a MicaPad window", () => Create(workspace, config, id, openSettings).Present());
            }
            var window = Registered(workspace, targetId) ?? Create(workspace, config, targetId, openSettings);
            window.Present();
            return window;
        }

        /// <summary>
        /// MicaPad for the hotkey, the overlay, <c>--pad</c> and Open with (spec 5.3), opening
        /// <paramref name="path"/> when given: a file already open in a window brings that window
        /// forward on its tab; any other file opens in the most recently active window.
        /// </summary>
        public static MicaPadWindow Open(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path)
        {
            var window = ShowOrActivate(workspace, config, openSettings, path);
            if (!string.IsNullOrWhiteSpace(path)) window.OpenPath(path);
            window.ShowLockedFolderNotice();
            window.ShowVaultMovedNotice();
            return window;
        }

        /// <summary>
        /// Called with a session window's id as <see cref="Create"/> starts loading it (after any
        /// document was handed over); null in the app. Tests make it throw, standing in for a window
        /// that cannot load.
        /// </summary>
        internal static Action<string>? LoadStarting { get; set; }

        /// <summary>Builds and loads a window for a session window. A half-loaded one is closed, never reused.</summary>
        private static MicaPadWindow Create(PadWorkspace workspace, AppConfig config, string windowId, Action? openSettings,
                                            Action<MicaPadWindow>? beforeLoad = null)
        {
            var window = new MicaPadWindow(workspace, config, windowId) { OpenSettingsRequested = openSettings };
            try
            {
                beforeLoad?.Invoke(window);
                LoadStarting?.Invoke(windowId);
                window.LoadSession();
            }
            catch
            {
                // Never shown: its entry must not reopen at login.
                window._hidden = true;
                window.CloseForExit();
                throw;
            }
            return window;
        }

        /// <summary>Shows this window in front: it counts as open for the next login and as the most recently active.</summary>
        private void Present()
        {
            _hidden = false;
            if (_state != null) _state.Open = true;
            _workspace.ActivateWindow(_windowId);
            _workspace.SaveSession();
            ShowWindow(this);
        }

        /// <summary>This workspace's other loaded windows, most recently active first; none that is closing.</summary>
        private List<MicaPadWindow> OtherWindows()
        {
            var others = new List<MicaPadWindow>();
            foreach (string id in _workspace.ActivationOrder)
            {
                if (id == _windowId) continue;
                if (Registered(_workspace, id) is { } window && !window._exiting) others.Add(window);
            }
            return others;
        }

        /// <summary>Ctrl+Shift+N and ☰ → New window: another window with one new note (spec 5.3), a little below and right of this one.</summary>
        internal MicaPadWindow NewWindow()
        {
            CaptureViewState();                   // the cascade starts from where this window is now
            var state = _workspace.NewWindow(_windowId);
            _workspace.NewNote(state.Id);
            MicaPadWindow window;
            try
            {
                window = Create(_workspace, _config, state.Id, OpenSettingsRequested);
            }
            catch
            {
                // No window shows it: its note comes back here rather than wait unseen for the next start.
                _workspace.CloseWindow(state.Id, _windowId);
                throw;
            }
            window.Present();
            return window;
        }

        /// <summary>
        /// The close button (spec 5.3). With another MicaPad window open, this window's tabs move to
        /// the most recently active of them — documents, undo history and bookmarks with them — and
        /// this window closes for real: no note is closed. The last window hides instead, keeping its tabs.
        /// </summary>
        internal void CloseByUser()
        {
            // Posted from OnClosing: by now the window may be exiting, or closed for good (Detach).
            if (_exiting || !s_windows.Contains(this)) return;
            var target = OtherWindows().FirstOrDefault();
            if (target == null)
            {
                HideKeepingTabs();
                return;
            }
            MergeInto(target);
            CloseForExit();
            target.Present();
        }

        /// <summary>Hides the last window, keeping its tabs for the next show.</summary>
        private void HideKeepingTabs()
        {
            CaptureViewState();                    // the placement from before full screen, if any
            if (IsFullScreen) ToggleFullScreen();  // so MicaPad comes back windowed (GUIDE; Part 4 fix wave)
            _hidden = true;
            if (_state != null) _state.Open = false;
            StopAi();                              // no request runs behind a hidden window
            _workspace.FlushPending();
            _workspace.SaveSession();
            if (IsVisible) Hide();
        }

        /// <summary>Hands every tab of this window to <paramref name="target"/> and takes this window out of the session.</summary>
        private void MergeInto(MicaPadWindow target)
        {
            CaptureViewState();
            _workspace.FlushPending();
            var moving = new List<(OpenNote Note, TextDocument? Document)>();
            foreach (var note in _workspace.TabsOf(_windowId).ToList()) moving.Add((note, ReleaseDocument(note)));
            _workspace.CloseWindow(_windowId, target._windowId);
            foreach (var (note, document) in moving)
                if (document != null && !target.AdoptDocument(note, document))
                    Warn("Window " + target._windowId + " already had a document for note " + note.Id + "; the handed-over one was dropped");
            _workspace.SaveSession();
        }

        /// <summary>
        /// Records every window's tabs, placement and open state and lets them all close for real:
        /// before application exit or session end. After this, WPF closing them merges nothing.
        /// <paramref name="only"/> limits it to one workspace's windows (tests: the shared UI test
        /// thread can run another test's windows in between).
        /// </summary>
        public static void PrepareAllForExit(PadWorkspace? only = null)
        {
            foreach (var window in s_windows.ToList())
                if (only == null || ReferenceEquals(window._workspace, only)) window.PrepareForExit();
        }

        /// <summary>
        /// Binds the window to its session window, builds the documents of its tabs and shows its
        /// active tab. Called once, before the window is first shown. The workspace restore is
        /// idempotent, so every window finds the same notes.
        /// </summary>
        public void LoadSession()
        {
            _workspace.Restore();
            _windowId = _requestedWindowId ?? _workspace.Windows[0].Id;
            _state = _workspace.WindowStateOf(_windowId)
                     ?? throw new InvalidOperationException("MicaPad has no window " + _windowId);
            var tabs = _workspace.TabsOf(_windowId);
            TabStrip.ItemsSource = tabs;
            if (tabs.Count == 0) _workspace.NewNote(_windowId);
            foreach (var note in tabs.ToList()) EnsureDocument(note);

            Topmost = _state.AlwaysOnTop;
            ApplyPlacement();
            ApplyEditorSettings();
            ShowNote(_workspace.ActiveIn(_windowId) ?? tabs[0]);
            if (!s_windows.Contains(this)) s_windows.Add(this);   // registered only once fully loaded
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
            if (_state != null) _state.Open = !_hidden;
        }

        /// <summary>Closes the window for real: tests, a window merged into another, application exit. Safe to call twice.</summary>
        public void CloseForExit()
        {
            if (_closedForExit) return;
            _closedForExit = true;
            PrepareForExit();
            // Detach first: a window that was never shown may not raise Closed.
            Detach();
            Close();
        }

        /// <summary>A new scratch note in a new tab.</summary>
        internal void NewTab() => ShowNote(_workspace.NewNote(_windowId));

        /// <summary>Closes a tab without asking. Closing this window's last one leaves a fresh empty note here.</summary>
        internal void CloseTab(OpenNote note)
        {
            bool wasShown = ReferenceEquals(_shown, note);
            if (wasShown) _shown = null;

            _workspace.Close(note);
            var tabs = _workspace.TabsOf(_windowId);
            if (tabs.Count == 0) _workspace.NewNote(_windowId);
            if (wasShown || _shown == null) ShowNote(_workspace.ActiveIn(_windowId) ?? tabs[0]);
        }

        /// <summary>Shows this window's tab at a zero-based position; an out-of-range index is ignored.</summary>
        internal void SelectTab(int index)
        {
            var tabs = _workspace.TabsOf(_windowId);
            if (index >= 0 && index < tabs.Count) ShowNote(tabs[index]);
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
            // Keys that act on the note's lines or its bookmarks.
            bool noteKey = (ctrl && key is Key.D or Key.J or Key.F2) || (ctrlShift && key is Key.Up or Key.Down)
                           || (modifiers is ModifierKeys.None or ModifierKeys.Shift && key == Key.F2);

            if (ctrl && key == Key.N) NewTab();
            else if (ctrlShift && key == Key.N) NewWindow();
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
            // Esc in the Search notes pane is the pane's own: back to the editor, or from the list to the query.
            else if (modifiers == ModifierKeys.None && key == Key.Escape && SearchPanel.IsKeyboardFocusWithin) return false;
            else if (modifiers == ModifierKeys.None && key == Key.Escape && FindBar.IsOpen) FindBar.Close();
            else if (ctrlShift && key == Key.H) ToggleHistory();
            else if (ctrlShift && key == Key.F) ToggleSearch();
            else if (ctrlShift && key == Key.A) ToggleAi();
            // Esc again, back in the editor, closes the pane (search spec 1); a text box (go to line, rename) keeps its Esc.
            else if (modifiers == ModifierKeys.None && key == Key.Escape && SearchPanel.Visibility == Visibility.Visible
                     && Keyboard.FocusedElement is not System.Windows.Controls.TextBox) CloseSearch();
            else if (modifiers == ModifierKeys.None && key == Key.F11) ToggleFullScreen();
            // Under the history preview the note keys do nothing: swallowed, so AvalonEdit's own
            // Ctrl+D (delete line) cannot reach the hidden editor either. A focused text box keeps them.
            else if (noteKey && PreviewPanel.Visibility == Visibility.Visible) return Keyboard.FocusedElement is not System.Windows.Controls.TextBox;
            else if (ctrl && key == Key.F2) ToggleBookmark();
            else if (modifiers == ModifierKeys.None && key == Key.F2) NextBookmark();
            else if (modifiers == ModifierKeys.Shift && key == Key.F2) PreviousBookmark();
            else if (EditingKeysAllowed && ctrl && key == Key.D) Duplicate(Editor);
            else if (EditingKeysAllowed && ctrlShift && key == Key.Up) MoveLines(down: false);
            else if (EditingKeysAllowed && ctrlShift && key == Key.Down) MoveLines(down: true);
            else if (EditingKeysAllowed && ctrl && key == Key.J) RunLineOperation(Editor, LineOperations.Join);
            else return false;
            return true;
        }

        /// <summary>
        /// Shortcuts that edit the note run only while no text box (find, replace, go to line,
        /// rename) has the keyboard focus, and not while the history preview covers the note, so a
        /// shortcut never edits a line of the note the user cannot see.
        /// </summary>
        private bool EditingKeysAllowed => EditingKeysAllowedFor(Keyboard.FocusedElement);

        /// <summary>The rule behind <see cref="EditingKeysAllowed"/>, for a given focused element; for tests.</summary>
        internal bool EditingKeysAllowedFor(object? focused) =>
            focused is not System.Windows.Controls.TextBox && PreviewPanel.Visibility != Visibility.Visible;

        /// <summary>The close button becomes <see cref="CloseByUser"/> unless the window is really exiting.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                // Closing for real cannot start inside Closing: the merge runs right after it.
                if (OtherWindows().Count > 0) Dispatcher.BeginInvoke(new Action(() => Guard("Closing a MicaPad window", CloseByUser)));
                else HideKeepingTabs();
            }
            base.OnClosing(e);
        }

        private void OnClosedForReal(object? sender, EventArgs e) => Detach();

        // ---- occurrences ---------------------------------------------------------------------

        private TextDocument? _occurrenceDoc;

        /// <summary>Follows the shown document: any edit (undo, Replace All, reload) makes the boxes stale, so clear them and recompute shortly.</summary>
        private void HookOccurrenceDocument()
        {
            if (_occurrenceDoc != null) _occurrenceDoc.Changed -= OnOccurrenceDocumentChanged;
            _occurrenceDoc = Editor.Document;
            if (_occurrenceDoc != null) _occurrenceDoc.Changed += OnOccurrenceDocumentChanged;
        }

        private void OnOccurrenceDocumentChanged(object? sender, DocumentChangeEventArgs e)
        {
            _occurrences.Offsets = Array.Empty<int>();
            _occurrences.Length = 0;
            OccurrenceText.Visibility = Visibility.Collapsed;
            Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
            _occurrenceTimer.Stop();
            _occurrenceTimer.Start();
        }

        /// <summary>The occurrence boxes; for tests.</summary>
        internal OccurrenceHighlighter OccurrenceMarks => _occurrences;

        /// <summary>
        /// Marks every occurrence of the selected word and counts them in the status bar (spec 3.4),
        /// or clears both when the selection is not exactly one whole word or the note is too large.
        /// </summary>
        internal void RefreshOccurrences()
        {
            var document = Editor.Document;
            string word = "";
            (IReadOnlyList<int> Offsets, bool Capped) found = (Array.Empty<int>(), false);

            try
            {
                if (document != null && document.TextLength <= PadLanguages.MaxFormattedChars
                    && Editor.SelectionLength > 0 && Editor.SelectionLength <= OccurrenceFinder.MaxWordLength)
                {
                    string text = document.Text;
                    if (OccurrenceFinder.IsWholeWordSelection(text, Editor.SelectionStart, Editor.SelectionLength))
                    {
                        word = Editor.SelectedText;
                        found = OccurrenceFinder.FindAll(text, word);
                    }
                }
            }
            catch (Exception ex)
            {
                // Spec "Error handling": marking never throws into the UI; the marks clear, logged once.
                _occurrences.ReportFailure(ex);
                word = "";
                found = (Array.Empty<int>(), false);
            }

            _occurrences.Offsets = found.Offsets;
            _occurrences.Length = word.Length;
            Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);

            if (found.Offsets.Count > 0)
            {
                OccurrenceText.Text = OccurrenceFinder.Describe(found.Offsets.Count, found.Capped);
                OccurrenceText.Visibility = Visibility.Visible;
            }
            else
            {
                OccurrenceText.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Stops the timer and unhooks from long-lived objects, its documents among them: one handed
        /// back to another window (a failed Move to new window) must not keep this window's change
        /// handler. The notes still read their text from those documents. Safe to call twice.
        /// </summary>
        private void Detach()
        {
            _previewGeneration++;
            _tick.Stop();
            _occurrenceTimer?.Stop();
            _statusTimer?.Stop();
            _config.PropertyChanged -= OnConfigChanged;
            _workspace.Open.CollectionChanged -= OnOpenChanged;
            foreach (var (document, changed) in _docHandlers) document.Changed -= changed;
            _docHandlers.Clear();
            DetachVault();
            DetachAi();
            s_windows.Remove(this);
        }

        // ---- documents and tabs -------------------------------------------------------------

        /// <summary>This window's document of a note, built on first use; null for a note of another window.</summary>
        private TextDocument? EnsureDocument(OpenNote note)
        {
            if (note.WindowId != _windowId) return null;
            if (_docs.TryGetValue(note.Id, out var existing)) return existing;

            var document = new TextDocument(note.TextProvider());
            document.UndoStack.ClearAll();
            Attach(note, document);
            return document;
        }

        /// <summary>
        /// Makes <paramref name="document"/> this window's document of <paramref name="note"/>: its
        /// edits reach the workspace, the note's text is read from it (autosave, snapshots), and its
        /// saved bookmarks come back.
        /// </summary>
        private void Attach(OpenNote note, TextDocument document)
        {
            EventHandler<DocumentChangeEventArgs> changed = (s, e) =>
            {
                _workspace.NotifyChanged(note, markUnsaved: !_suppressDirty);
                if (ReferenceEquals(_shown, note)) UpdateCharsText();
                // A big paste crosses the 2 MB limit: formatting switches off (and back on) once the
                // change is done. Never inside it: AvalonEdit still calls the document's other
                // handlers for this change, among them the formatting a re-apply would tear down.
                if (ReferenceEquals(_shown, note) && CrossesSizeLimit(document))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(_shown, note) && ReferenceEquals(Editor.Document, document) && CrossesSizeLimit(document))
                            ApplyLanguage();
                    }));
                }
            };
            document.Changed += changed;
            _docHandlers[document] = changed;
            note.TextProvider = () => document.Text;
            _docs[note.Id] = document;
            if (_workspace.Session.Tabs.TryGetValue(note.Id, out var view) && view.Bookmarks != null)
                _bookmarks.Load(document, view.Bookmarks);
        }

        /// <summary>
        /// Lets go of a note's document because its tab is moving to another window: its bookmarks
        /// go to the session, and its change handler and bookmark anchors come off. Returns the
        /// document with its undo history, for <see cref="AdoptDocument"/>; null when this window
        /// never built one. Keeping that history is safe only when this window is closing
        /// (<see cref="MergeInto"/>): <see cref="LetGoOf"/> clears it for a window that stays open.
        /// </summary>
        internal TextDocument? ReleaseDocument(OpenNote note)
        {
            if (!_docs.TryGetValue(note.Id, out var document)) return null;
            if (ReferenceEquals(_shown, note)) LetGoOfShown();
            _workspace.SetBookmarks(note, _bookmarks.Lines(document));
            _bookmarks.Forget(document);
            if (_docHandlers.Remove(document, out var changed)) document.Changed -= changed;
            _docs.Remove(note.Id);
            return document;
        }

        /// <summary>Takes over a document another window released, with whatever undo history it kept. False when this window already had one for the note (the handed-over one is dropped).</summary>
        internal bool AdoptDocument(OpenNote note, TextDocument document)
        {
            // Create hands a document over before LoadSession, when only the requested id is known.
            if (note.WindowId != (_requestedWindowId ?? _windowId) || _docs.ContainsKey(note.Id)) return false;
            Attach(note, document);
            return true;
        }

        /// <summary>
        /// The editor stops showing any note (its tab is leaving this window): caret and scroll are
        /// saved, and formatting and folding come off that document first, so nothing here keeps
        /// listening to it.
        /// </summary>
        private void LetGoOfShown()
        {
            if (_shown != null) SaveViewState(_shown);
            _shown = null;
            HideInfo();
            EndPreview();
            HistoryPanel.Visibility = Visibility.Collapsed;
            _language.Apply(PadLanguages.Plain);
            Editor.Document = new TextDocument();
        }

        /// <summary>True when the shown language's size-limit state no longer matches the document's length.</summary>
        private bool CrossesSizeLimit(TextDocument document) =>
            (document.TextLength > PadLanguages.MaxFormattedChars) != _resolved.TooLarge;

        /// <summary>
        /// Replaces a note's whole text as one undoable edit. With <paramref name="markUnsaved"/>
        /// false the change does not count as an edit of the file (a reload from the file itself).
        /// </summary>
        private void ReplaceText(OpenNote note, string text, bool markUnsaved)
        {
            var document = EnsureDocument(note);
            if (document == null) return;
            var marked = _bookmarks.Lines(document);
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
            if (marked.Count > 0) _bookmarks.Load(document, marked);   // same line numbers; past the end dropped
        }

        private TabDragController _tabDrag = null!;
        internal TabDragController TabDrag => _tabDrag;

        private void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move) return;   // a moved tab keeps its document, bookmarks and folds
            if (e.NewItems != null)
                foreach (OpenNote note in e.NewItems)
                    if (note.WindowId == _windowId) EnsureDocument(note);   // never another window's: its TextProvider is that window's
            if (e.OldItems != null)
                foreach (OpenNote note in e.OldItems)
                {
                    if (_docs.TryGetValue(note.Id, out var gone))
                    {
                        _bookmarks.Forget(gone);
                        if (_docHandlers.Remove(gone, out var changed)) gone.Changed -= changed;
                    }
                    _docs.Remove(note.Id);
                }
        }

        private void ShowNote(OpenNote note)
        {
            if (note.WindowId != _windowId)
            {
                // Another window shows this tab (a file already open there): that window comes forward on it (spec 5.3).
                if (Registered(_workspace, note.WindowId) is { } owner && !ReferenceEquals(owner, this) && !owner._exiting)
                {
                    owner.ShowNote(note);
                    owner.Present();
                }
                return;
            }
            if (_shown != null && !ReferenceEquals(_shown, note))
            {
                SaveViewState(_shown);
                _workspace.FlushPending();
            }

            _shown = note;
            _workspace.SetActive(note);
            Editor.Document = EnsureDocument(note)!;
            ApplyLanguage();
            RefreshOccurrences();
            RestoreViewState(note);
            UpdateCaretText();
            UpdateCharsText();
            UpdateSaveText();
            ScrollTabIntoView(note);
            UpdateFileText();
            if (_infoNote != null && !ReferenceEquals(_infoNote, note)) HideInfo();
            CheckDisk(note);
            if (HistoryPanel.Visibility == Visibility.Visible) ShowHistory();
            RefreshAi();   // Replace selection and Insert below are offered only on the note the result came from
        }

        private void CloseActiveTab()
        {
            if (_shown != null) CloseTab(_shown);
        }

        private void ReopenClosed()
        {
            var note = _workspace.ReopenLastClosed(_windowId);
            if (note != null) ShowNote(note);
            else if (_workspace.ClosedNotes().Count > 0) ShowReopenFailed();   // not just an empty list
        }

        /// <summary>Reopen found the note but could not load it (its text cannot be read right now, say).</summary>
        private void ShowReopenFailed() =>
            ShowInfo("That note could not be reopened right now, so it was left as it is.", null);

        private void CycleTab(int delta)
        {
            var tabs = _workspace.TabsOf(_windowId);
            int count = tabs.Count;
            if (count < 2 || _shown == null) return;
            int index = tabs.IndexOf(_shown);
            ShowNote(tabs[((index + delta) % count + count) % count]);
        }

        // ---- bookmarks -----------------------------------------------------------------------

        /// <summary>The shown tab bookmarked lines; for tests.</summary>
        internal IReadOnlyList<int> BookmarkLines => _bookmarks.Lines(Editor.Document);

        /// <summary>Ctrl+F2: bookmark the caret line, or remove its bookmark.</summary>
        internal void ToggleBookmark() => _bookmarks.Toggle(Editor.Document, Editor.TextArea.Caret.Line);

        /// <summary>F2: the next bookmark, wrapping.</summary>
        internal void NextBookmark() => GoToBookmark(_bookmarks.Next(Editor.Document, Editor.TextArea.Caret.Line));

        /// <summary>Shift+F2: the previous bookmark, wrapping.</summary>
        internal void PreviousBookmark() => GoToBookmark(_bookmarks.Previous(Editor.Document, Editor.TextArea.Caret.Line));

        /// <summary>Clear bookmarks, for the shown tab.</summary>
        internal void ClearBookmarks() => _bookmarks.Clear(Editor.Document);

        /// <summary>Replaces the shown note whole text as an edit; for tests.</summary>
        internal void ReplaceShownText(string text)
        {
            if (_shown != null) ReplaceText(_shown, text, markUnsaved: true);
        }

        private void GoToBookmark(int? line)
        {
            if (line is int l) GoToLine(l);
        }

        /// <summary>
        /// Moves the selected lines past their neighbour (Ctrl+Shift+Up/Down and Lines ▸ Move both
        /// come here), taking the bookmarks along: the block's move one line with it, and the
        /// neighbour's jumps to the other side with the neighbour.
        /// </summary>
        internal void MoveLines(bool down)
        {
            var document = Editor.Document;
            var marked = _bookmarks.Lines(document);
            var (blockStart, blockEnd) = TextLines.Block(document.Text, Editor.SelectionStart, Editor.SelectionLength);
            int first = document.GetLineByOffset(blockStart).LineNumber;
            int last = document.GetLineByOffset(blockEnd).LineNumber;

            if (!RunLineOperation(Editor, down ? LineOperations.MoveDown : LineOperations.MoveUp) || marked.Count == 0) return;
            _bookmarks.Remap(document, marked, down
                ? line => line >= first && line <= last ? line + 1 : line == last + 1 ? first : line
                : line => line >= first && line <= last ? line - 1 : line == first - 1 ? last : line);
        }

        private void OnBookmarksChanged()
        {
            _bookmarkMargin.InvalidateVisual();
            // Inside ShowNote the shown note is set before the editor gets its document: never save
            // one note's bookmarks under another.
            if (_shown == null || !_docs.TryGetValue(_shown.Id, out var shownDocument) || !ReferenceEquals(shownDocument, Editor.Document)) return;
            _workspace.SetBookmarks(_shown, _bookmarks.Lines(Editor.Document));
            _workspace.SaveSession();
        }

        private void SaveViewState(OpenNote note)
        {
            if (EnsureDocument(note) is not { } document) return;   // another window's tab: its window records it
            _workspace.SetTabViewState(note, Editor.CaretOffset, Editor.VerticalOffset);
            _workspace.SetBookmarks(note, _bookmarks.Lines(document));
        }

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
            else
            {
                _tabDrag.Press(note, e.GetPosition(TabStrip));
                ShowNote(note);
            }
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
            menu.Items.Add(Item("Rename…", null, () => BeginRename(note, target), icon: "\uE8AC"));
            menu.Items.Add(Item("Close", null, () => CloseTab(note), icon: "\uE711"));
            menu.Items.Add(Item("Close other tabs", null, () => CloseOtherTabs(note), _workspace.TabsOf(_windowId).Count > 1));

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move to new window", null, () => MoveToNewWindow(note), _workspace.TabsOf(_windowId).Count > 1, icon: "\uE8A7"));
            var others = OtherWindows();
            if (others.Count > 0)
            {
                var moveTo = new MenuItem { Header = "Move to" };
                // Doubled: a menu header reads "_" as an access key, and file names are full of them.
                foreach (var other in others) moveTo.Items.Add(Item(other.ActiveTitle.Replace("_", "__"), null, () => MoveToWindow(note, other)));
                menu.Items.Add(moveTo);
            }

            if (note.Meta.SourcePath is string path)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Copy file path", null, () => CopyFilePath(path), icon: "\uE8C8"));
                menu.Items.Add(Item("Show in folder", null, () => ShowInFolder(path), icon: "\uE838"));
            }
            return menu;
        }

        /// <summary>A window's name in Move to ▸: the title of its active tab.</summary>
        private string ActiveTitle => _workspace.ActiveIn(_windowId)?.Title ?? "MicaPad";

        /// <summary>Tab menu → Move to new window: the tab moves to a new window of its own, with its bookmarks; its undo history starts afresh (<see cref="LetGoOf"/>).</summary>
        internal void MoveToNewWindow(OpenNote note)
        {
            if (note.WindowId != _windowId || _workspace.TabsOf(_windowId).Count < 2) return;
            CaptureViewState();                   // the new window cascades from where this one is
            var document = LetGoOf(note);
            var state = _workspace.NewWindow(_windowId);
            _workspace.MoveToWindow(note, state.Id);
            MicaPadWindow window;
            try
            {
                window = Create(_workspace, _config, state.Id, OpenSettingsRequested, beforeLoad: w =>
                {
                    // Refused, the new window builds its own document from the note's text instead.
                    if (document != null && !w.AdoptDocument(note, document))
                        Warn("The new window " + state.Id + " did not take the document of note " + note.Id + "; it was rebuilt from the note's text");
                });
            }
            catch
            {
                // No window shows it: the tab comes back here, with its bookmarks.
                _workspace.CloseWindow(state.Id, _windowId);
                if (document != null) AdoptDocument(note, document);
                throw;
            }
            window.Present();                     // saves the session
        }

        /// <summary>
        /// Tab menu → Move to ▸ (spec 5.3): the tab moves to <paramref name="target"/> and is shown
        /// there, its undo history starting afresh (<see cref="LetGoOf"/>). Moving this window's only
        /// tab closes this window into that one, undo history and all, as × does.
        /// </summary>
        internal void MoveToWindow(OpenNote note, MicaPadWindow target)
        {
            // The menu was built earlier: the target may have closed since.
            if (note.WindowId != _windowId || ReferenceEquals(target, this) || target._exiting || !s_windows.Contains(target)) return;
            if (_workspace.TabsOf(_windowId).Count == 1)
            {
                MergeInto(target);
                CloseForExit();
            }
            else
            {
                var document = LetGoOf(note);
                _workspace.MoveToWindow(note, target._windowId);
                if (document != null) target.AdoptDocument(note, document);
                _workspace.SaveSession();
            }
            target.ShowNote(note);
            target.Present();
        }

        /// <summary>
        /// A tab leaving this window while the window stays open (Move to, Move to new window): a
        /// neighbour is shown first if it was the shown one, then its document is released with its
        /// undo history cleared. Each undo step holds the text area that made it and puts that text
        /// area's caret and selection back, whatever it shows by then (a redo does so before the
        /// document even starts its update): undo or redo in the other window could leave a
        /// selection here past the end of this window's note (a crash while drawing) or over its
        /// text (the next key replaces it). Closing a window by × keeps the history: its text area goes with it.
        /// </summary>
        private TextDocument? LetGoOf(OpenNote note)
        {
            if (ReferenceEquals(_shown, note))
            {
                var tabs = _workspace.TabsOf(_windowId);
                int index = tabs.IndexOf(note);
                ShowNote(tabs[index + 1 < tabs.Count ? index + 1 : index - 1]);
            }
            var document = ReleaseDocument(note);
            document?.UndoStack.ClearAll();
            return document;
        }

        /// <summary>
        /// Closes every tab except <paramref name="keep"/>, each as an ordinary close: flushed,
        /// snapshotted and moved to Closed notes, with a file's unsaved edits kept.
        /// </summary>
        internal void CloseOtherTabs(OpenNote keep)
        {
            ShowNote(keep);
            foreach (var other in _workspace.TabsOf(_windowId).ToList())
                if (!ReferenceEquals(other, keep)) CloseTab(other);
        }

        private void CopyFilePath(string path) => CopyPlainText(path, "a file path");

        /// <summary>Puts plain text on the clipboard. Tests replace it.</summary>
        internal Action<string> SetClipboardText { get; set; } = text => Clipboard.SetText(text);

        /// <summary>Copies plain text; a busy clipboard is logged as "Copying <paramref name="what"/> failed" and told in a notice.</summary>
        private void CopyPlainText(string text, string what)
        {
            try
            {
                SetClipboardText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Warn("Copying " + what + " failed: " + ex.Message);
                ShowNotice("The clipboard is busy. Try again in a moment.");
            }
        }

        /// <summary>The Copy button of a fenced block (ruling R5): its code goes to the clipboard and the status bar says how many lines.</summary>
        private void CopyCode(string code)
        {
            try
            {
                SetClipboardText(code);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Warn("Copying code failed (" + ex.GetType().Name + ")");
                ShowStatus("Clipboard busy, try again");
                return;
            }
            int lines = TextLines.Split(code).Lines.Count;
            ShowStatus(lines == 1 ? "Copied" : "Copied " + lines + " lines");
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

        /// <summary>How Explorer is started; tests swap it so none is launched.</summary>
        internal static Action<ProcessStartInfo> StartExplorer { get; set; } = info => Process.Start(info);

        /// <summary>
        /// Opens Explorer with the file or folder selected. One that is no longer there is reported
        /// rather than opening some other folder.
        /// </summary>
        internal void ShowInFolder(string path, bool folder = false)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                ShowNotice("That " + (folder ? "folder" : "file") + " is no longer at " + path + ".");
                return;
            }
            try
            {
                StartExplorer(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
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
            var result = _workspace.OpenFile(path, _windowId);
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

        /// <summary>
        /// Once per run: the notes this Windows account could not decrypt were moved to
        /// <see cref="NoteStore.LockedFolder"/>. Nothing was deleted.
        /// </summary>
        internal void ShowLockedFolderNotice()
        {
            string? folder = _workspace.Store.LockedFolder;
            if (folder == null || _workspace.LockedNoticeShown) return;

            _workspace.LockedNoticeShown = true;   // in-run guard: the store forgets the folder, but a failed marker delete must not repeat the notice
            _workspace.Store.ForgetLockedFolder();
            ShowInfo("MicaPad could not decrypt the notes saved before on this Windows account, so they were moved to "
                     + folder + ". Nothing was deleted.", null, "Show folder", () => ShowInFolder(folder, folder: true));
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
                    "Keep as note", () => KeepAsNote(note));
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

        /// <summary>Reads the note's file again into its document; only for this window's own tab (another window's document is not here).</summary>
        internal void Reload(OpenNote note)
        {
            if (note.WindowId != _windowId) return;   // before any change: the stamp must never say "in sync" over old text
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

        /// <summary>Keep as note: the file is gone, so the tab becomes a note, in the note language.</summary>
        internal void KeepAsNote(OpenNote note)
        {
            _workspace.DetachFromFile(note);
            UpdateFileText();
            if (ReferenceEquals(note, _shown)) ApplyLanguage();
        }

        internal void SaveAsPath(OpenNote note, string path) =>
            HandleSaveResult(note, _workspace.SaveAs(note, path), () => SaveAsPath(note, path));

        private void HandleSaveResult(OpenNote note, SaveToFileResult result, Action retry)
        {
            switch (result.Status)
            {
                case SaveToFileStatus.Saved:
                    if (ReferenceEquals(_infoNote, note)) HideInfo();
                    UpdateFileText();
                    UpdateSaveText();
                    if (ReferenceEquals(note, _shown)) ApplyLanguage();   // Save As may have changed the file type
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

        /// <summary>Converts the note's line endings as one undoable edit; only for this window's own tab.</summary>
        internal void ConvertLineEndings(OpenNote note, LineEnding ending)
        {
            if (note.WindowId != _windowId) return;   // before the workspace records the new ending
            string converted = _workspace.ConvertLineEndings(note, ending);
            if (EnsureDocument(note) is { } document && converted != document.Text) ReplaceText(note, converted, markUnsaved: true);
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
            if (SearchPanel.Visibility == Visibility.Visible) SearchPanel.Visibility = Visibility.Collapsed;   // they share the column
            CloseAi(focusEditor: false);                                                                      // and so does the AI pane
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

            _previewVersion = text;
            PreviewEditor.FontFamily = MonoFamily;   // the preview keeps today's look: no reading font
            PreviewEditor.FontSize = Editor.FontSize;
            PreviewEditor.WordWrap = Editor.WordWrap;
            PreviewText.Text = "Viewing " + HistoryRows.When(snapshot.Stamp, DateTime.Now);
            PreviewPanel.Visibility = Visibility.Visible;
            // Compare stays on while the user walks through versions.
            if (_comparing) StartCompare();
            else ShowPreviewVersion();
        }

        private void EndPreview()
        {
            _previewGeneration++;
            _comparing = false;
            _diff.Hide();
            CompareButton.Content = "Compare with current";
            DiffSummary.Visibility = Visibility.Collapsed;
            PreviewPanel.Visibility = Visibility.Collapsed;
            _previewVersion = "";
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
            if (_previewVersion.Length == 0) return;
            try
            {
                Clipboard.SetText(_previewVersion);
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

            string text = _previewVersion;
            _workspace.SnapshotNow(_shown, SnapshotReason.BeforeReplace);
            ReplaceText(_shown, text, markUnsaved: true);
            ShowHistory();
            Editor.Focus();
        }

        // ---- history compare -----------------------------------------------------------------

        private DiffPreview _diff = null!;

        /// <summary>The previewed version's own text: Restore and Copy all use it, whatever the preview shows.</summary>
        private string _previewVersion = "";

        /// <summary>Bumped whenever what the preview should show changes, so a compare that finishes late is dropped.</summary>
        private int _previewGeneration;

        private bool _comparing;

        /// <summary>The compare running off the UI thread, or a finished task; for tests.</summary>
        internal Task CompareTask { get; private set; } = Task.CompletedTask;

        /// <summary>The compare view of the preview; for tests.</summary>
        internal DiffPreview Diff => _diff;

        private void OnCompareClick(object sender, RoutedEventArgs e) => ToggleCompare();

        /// <summary>Compare with current (spec 5.2): the preview shows the version, or its line diff against the note now.</summary>
        internal void ToggleCompare()
        {
            if (PreviewPanel.Visibility != Visibility.Visible) return;
            _comparing = !_comparing;
            if (_comparing) StartCompare();
            else ShowPreviewVersion();
        }

        /// <summary>The preview shows the version itself, and the banner offers the compare.</summary>
        private void ShowPreviewVersion()
        {
            _previewGeneration++;
            ShowVersionText();
            CompareButton.Content = "Compare with current";
            DiffSummary.Visibility = Visibility.Collapsed;
        }

        /// <summary>The version's own text in the preview, in the note's language.</summary>
        private void ShowVersionText()
        {
            _diff.Hide();
            PreviewEditor.Text = _previewVersion;
            _previewLanguage.Apply(PadLanguages.Resolve(_shown?.Meta.Language, _shown?.Meta.SourcePath, _config.PadMarkdown, _previewVersion.Length).Effective);
        }

        /// <summary>
        /// Diffs the version against the note off the UI thread (up to a second for 1 MB) and shows
        /// the result on the dispatcher, unless the preview moved on meanwhile: another version,
        /// Back, compare turned off, or the window closing.
        /// </summary>
        private void StartCompare()
        {
            int generation = ++_previewGeneration;
            ShowVersionText();                        // the version stays on screen until the diff is ready
            CompareButton.Content = "Show this version";
            DiffSummary.Text = "Comparing…";
            DiffSummary.Visibility = Visibility.Visible;

            string version = _previewVersion;
            string current = Editor.Document.Text;
            var dispatcher = Dispatcher;
            CompareTask = Task.Run(() =>
            {
                DiffOutcome? outcome = null;
                string? failure = null;
                try
                {
                    outcome = HistoryDiff.Compare(version, current);
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
                // Guarded: MicaStats has no dispatcher exception handler, so nothing here may throw.
                dispatcher.BeginInvoke(new Action(() => Guard("Showing a compare", () => ShowCompare(generation, outcome, failure))));
            });
        }

        private void ShowCompare(int generation, DiffOutcome? outcome, string? failure)
        {
            if (generation != _previewGeneration || !_comparing || PreviewPanel.Visibility != Visibility.Visible) return;
            if (outcome == null)
            {
                DiagnosticsLog.Warn("pad", "Comparing a version failed (" + failure + ")");
                DiffSummary.Text = "Could not compare";
                return;
            }
            DiffSummary.Text = outcome.Summary;
            if (outcome.TooLarge) return;             // the version itself stays on screen
            _previewLanguage.Apply(PadLanguages.Plain);
            _diff.Show(outcome.Rows);
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
            var note = _workspace.Reopen(row.Id, _windowId);
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

        // ---- search notes (search spec 1) ---------------------------------------------------

        /// <summary>MicaPad's search, set by App when MicaPad first opens; null leaves the pane with words disabled.</summary>
        internal static NoteSearchService? SearchService { get; set; }

        /// <summary>Hands notes to the indexer; the pane reconciles through it each time it opens.</summary>
        internal static SearchFeeder? SearchFeeder { get; set; }

        private void OnSearchButtonClick(object sender, RoutedEventArgs e) => ToggleSearch();

        /// <summary>Ctrl+Shift+F: opens the Search notes pane (closing History and the AI pane), or closes it.</summary>
        internal void ToggleSearch()
        {
            if (SearchPanel.Visibility == Visibility.Visible)
            {
                CloseSearch();
                return;
            }
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                EndPreview();
                HistoryPanel.Visibility = Visibility.Collapsed;
            }
            CloseAi(focusEditor: false);
            SearchFeeder?.FlushPending();
            SearchFeeder?.ReconcileAll();
            // A selection of one line becomes the query, as Ctrl+F does.
            string selected = Editor.SelectedText;
            SearchPanel.Open(selected.Length > 0 && !selected.Contains('\n') ? selected.Trim() : null);
        }

        private void CloseSearch()
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            Editor.Focus();
        }

        private async Task<(IReadOnlyList<SearchRow> Rows, string Status)> RunSearchAsync(string query, CancellationToken cancel)
        {
            var service = SearchService;
            if (service == null) return (Array.Empty<SearchRow>(), "Search is not ready yet.");

            // Off the UI thread: the keyword search and the vector scan take tens of milliseconds over a
            // large index (longer while the vectors are being saved). The rows are built back here.
            var outcome = await Task.Run(() => service.Search.SearchAsync(query, cancel), cancel);
            var open = _workspace.Open.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var rows = outcome.Hits.Select(p => new SearchRow(
                p.NoteId, p.Title, !open.Contains(p.NoteId), p.FirstLine, p.LastLine, p.FirstLineText,
                SearchSnippet.Make(p.Body, query))).ToList();
            return (rows, SearchStatusText.For(outcome, service.Settings()));
        }

        /// <summary>
        /// Shows the result's note and selects its passage: in this window, in the window that has
        /// it open (which comes forward), or reopened here when it was closed (search spec 1).
        /// </summary>
        internal void OpenSearchResult(SearchRow row)
        {
            var note = _workspace.Open.FirstOrDefault(n => n.Id == row.NoteId) ?? _workspace.Reopen(row.NoteId, _windowId);
            if (note == null)
            {
                // Still stored but unreadable right now: it stays closed, and stays in the index.
                if (_workspace.Store.LoadMeta(row.NoteId) != null)
                {
                    ShowReopenFailed();
                    return;
                }
                ShowNotice("That note is no longer there.");
                SearchFeeder?.ReconcileAll();
                return;
            }

            var owner = note.WindowId == _windowId ? this : Registered(_workspace, note.WindowId);
            ShowNote(note);
            owner?.SelectPassage(row);
        }

        /// <summary>Selects the row's passage where its first line is now, and scrolls to it.</summary>
        private void SelectPassage(SearchRow row)
        {
            var document = Editor.Document;
            int first = SearchLocate.FindLine(document.LineCount, n => document.GetText(document.GetLineByNumber(n)), row.FirstLine, row.FirstLineText);
            int last = Math.Clamp(first + (row.LastLine - row.FirstLine), first, document.LineCount);
            var start = document.GetLineByNumber(first);
            var end = document.GetLineByNumber(last);
            Editor.Select(start.Offset, end.EndOffset - start.Offset);
            // Queued behind ShowNote's restore of the note's saved scroll (RestoreViewState, same
            // priority, so it runs first), which would otherwise scroll the passage away again.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Editor.ScrollToLine(first)));
            Editor.Focus();
        }

        // ---- keys, menu, settings -----------------------------------------------------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (KeyBelongsToVaultCard(e)) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            bool handled = false;
            // A shortcut that throws is logged and its key handled, never thrown into MicaStats.
            if (!Guard("The shortcut " + modifiers + "+" + key, () => handled = HandleShortcut(key, modifiers))) handled = true;
            if (handled) e.Handled = true;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Zoom(e.Delta > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
        }

        private void OnMenuButtonClick(object sender, RoutedEventArgs e) => BuildMainMenu().IsOpen = true;

        /// <summary>The ☰ menu, built fresh for each open; for tests (opening it needs a shown window).</summary>
        internal ContextMenu BuildMainMenu()
        {
            var menu = NewMenu(MenuButton, PlacementMode.Bottom);
            menu.Items.Add(Item("New note", "Ctrl+N", NewTab, icon: "\uE710"));
            menu.Items.Add(Item("New window", "Ctrl+Shift+N", () => NewWindow(), icon: "\uE8A7"));
            menu.Items.Add(Item("Open…", "Ctrl+O", OpenWithDialog, icon: "\uE8E5"));
            menu.Items.Add(Item("Save", "Ctrl+S", SaveShown, icon: "\uE74E"));
            menu.Items.Add(Item("Save As…", "Ctrl+Shift+S", () => { if (_shown != null) SaveAs(_shown); }, icon: "\uE792"));
            // Off while a history version covers the note: it would copy the hidden note, not what is shown.
            menu.Items.Add(Item("Copy as RTF", null, CopyAsRtf, enabled: PreviewPanel.Visibility != Visibility.Visible));
            menu.Items.Add(Item("Close tab", "Ctrl+W", CloseActiveTab, icon: "\uE711"));
            menu.Items.Add(Item("Reopen closed tab", "Ctrl+Shift+T", ReopenClosed));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false), icon: "\uE721"));
            menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true), icon: "\uE8AB"));
            menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine, icon: "\uE8AD"));
            menu.Items.Add(Item("History", "Ctrl+Shift+H", ToggleHistory, icon: "\uE81C"));
            menu.Items.Add(Item("Clear bookmarks", null, ClearBookmarks, enabled: BookmarkLines.Count > 0));
            var tools = ToolsMenu(Editor, ShowStatus, () => Now());
            tools.IsEnabled = PreviewPanel.Visibility != Visibility.Visible;   // never edit a note hidden under the history preview
            menu.Items.Add(tools);
            menu.Items.Add(new Separator());
            menu.Items.Add(Check("Word wrap", "Alt+Z", _config.PadWordWrap, ToggleWordWrap));
            menu.Items.Add(Check("Line numbers", null, _config.PadShowLineNumbers,
                () => _config.PadShowLineNumbers = !_config.PadShowLineNumbers));
            menu.Items.Add(Check("Markdown formatting", null, _config.PadMarkdown, () => _config.PadMarkdown = !_config.PadMarkdown));
            menu.Items.Add(Check("Auto-close brackets and quotes", null, _config.PadAutoClose, () => _config.PadAutoClose = !_config.PadAutoClose));
            menu.Items.Add(Check("Always on top", null, Topmost, ToggleTopmost));
            menu.Items.Add(Check("Full screen", "F11", IsFullScreen, ToggleFullScreen));
            menu.Items.Add(Item("Font…", null, ChooseFont, icon: "\uE8D2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open notes folder", null, OpenNotesFolder, icon: "\uE838"));
            menu.Items.Add(Item("Settings", null, () => OpenSettingsRequested?.Invoke(), icon: "\uE713"));
            return menu;
        }

        internal ContextMenu NewMenu(UIElement target, PlacementMode placement)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
            EditorMenus.Style(menu, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            return menu;
        }

        // ---- right-click menus --------------------------------------------------------------

        /// <summary>The editor's right-click menu. One instance; its items are rebuilt each time it opens.</summary>
        internal ContextMenu EditorMenu { get; } = new();

        /// <summary>The history preview's right-click menu: copying only, because the preview is read-only.</summary>
        internal ContextMenu PreviewMenu { get; } = new();

        /// <summary>
        /// Rebuilds the editor menu for the current selection, undo state and theme; on a pill (under
        /// the mouse, or at the caret from the keyboard), the pill menu instead.
        /// </summary>
        internal void RefreshEditorMenu(bool byMouse = false)
        {
            if (MenuReference(Editor, byMouse) is { } reference) FillPillMenu(EditorMenu, reference);
            else FillEditorMenu(EditorMenu, Editor, readOnly: false);
        }

        /// <summary>Rebuilds the history preview's menu; on a pill, its read-only pill menu.</summary>
        internal void RefreshPreviewMenu(bool byMouse = false)
        {
            if (MenuReference(PreviewEditor, byMouse) is { } reference) FillPillMenu(PreviewMenu, reference, readOnly: true);
            else FillEditorMenu(PreviewMenu, PreviewEditor, readOnly: true);
        }

        /// <summary>
        /// The items of a text menu, each disabled when it cannot apply. Later parts add their groups
        /// (Format, Lines, Tools) here, before the Find group.
        /// </summary>
        private void FillEditorMenu(ContextMenu menu, ICSharpCode.AvalonEdit.TextEditor editor, bool readOnly)
        {
            menu.Items.Clear();
            EditorMenus.Style(menu, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            AddEditGroup(menu, editor, readOnly);
            if (readOnly) return;

            if (ReferenceEquals(editor, Editor))
            {
                int copyAt = menu.Items.Cast<object>().ToList().FindIndex(i => i is MenuItem { Header: "Copy" });
                if (copyAt >= 0) menu.Items.Insert(copyAt + 1, Item("Copy as RTF", null, CopyAsRtf));
                AddStoreItem(menu);
            }

            menu.Items.Add(new Separator());
            if (ReferenceEquals(editor, Editor) && ReferenceEquals(_resolved.Effective, PadLanguages.Markdown))
                menu.Items.Add(FormatMenu(editor));
            menu.Items.Add(LinesMenu(editor, MoveLines));
            menu.Items.Add(ToolsMenu(editor, ShowStatus, () => Now()));
            if (ReferenceEquals(editor, Editor)) menu.Items.Add(BuildAiMenu());

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false), icon: "\uE721"));
            menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true), icon: "\uE8AB"));
            menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine, icon: "\uE8AD"));
        }

        // ---- copy as RTF ---------------------------------------------------------------------

        /// <summary>
        /// Puts RTF and plain text on the clipboard; false when it stays busy. The WinForms call
        /// tries again three times, 100 ms apart (spec 4.4). WPF's own call would retry ten times
        /// on each of its two steps, about 4 s on a locked clipboard. Tests replace it.
        /// </summary>
        internal Func<string, string, bool> TrySetClipboard { get; set; } = (rtf, text) =>
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.Rtf, rtf);
            data.SetData(System.Windows.Forms.DataFormats.UnicodeText, text);
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 100);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        };

        /// <summary>
        /// Copies the selection (or the whole note) as RTF and plain text, styled as shown, in the
        /// light palette (spec 4.4). A rectangular (Alt+drag) selection copies its box, unstyled.
        /// </summary>
        internal void CopyAsRtf()
        {
            try
            {
                CopyAsRtfCore();
            }
            catch (Exception ex)
            {
                Warn("Copy as RTF failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                ShowStatus("Copy as RTF failed");
            }
        }

        /// <summary>Where unexpected failures are logged; tests replace it.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>The most characters Copy as RTF will take in one go.</summary>
        internal const int MaxRtfChars = 10_000_000;

        /// <summary>The longest RTF, estimated by <see cref="RtfWriter.EstimatedLength"/>, Copy as RTF will build.</summary>
        internal const long MaxRtfEstimate = 40_000_000;

        private void CopyAsRtfCore()
        {
            var document = Editor.Document;
            var selection = Editor.TextArea.Selection;
            // A box selection's own start and length are those of the lines around it, not the box.
            bool box = selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection && !selection.IsEmpty;
            int start = 0, length = document.TextLength;
            string text;
            if (box)
            {
                text = selection.GetText();
            }
            else
            {
                if (!selection.IsEmpty)
                {
                    start = Editor.SelectionStart;
                    length = Editor.SelectionLength;
                }
                if (length > MaxRtfChars)
                {
                    ShowStatus("Too large to copy as RTF");
                    return;
                }
                text = document.GetText(start, length);
            }
            if (text.Length > MaxRtfChars || RtfWriter.EstimatedLength(text) > MaxRtfEstimate)
            {
                ShowStatus("Too large to copy as RTF");
                return;
            }

            var effective = _resolved.Effective;
            bool markdown = ReferenceEquals(effective, PadLanguages.Markdown);
            IReadOnlyList<RtfRun> runs = box ? Array.Empty<RtfRun>() : RtfRuns.For(document, start, length, effective, markdown);
            // The editor font at its own size: Editor.FontSize includes the window's zoom.
            string font = _config.PadFontFamily.Split(',')[0].Trim();
            string rtf = RtfWriter.Write(text, runs, font, _config.PadFontSize, PadPalette.Light.Text);

            if (!TrySetClipboard(rtf, text)) ShowStatus("Clipboard busy, try again");
        }

        private void OnEditorRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (DiagramPicture.IsInside(source) || CodeCopyLayer.IsInside(source)) return;
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
            if (_state != null) _state.AlwaysOnTop = Topmost;
            _workspace.SaveSession();
        }

        private void Zoom(double delta) => SetZoom((_state?.Zoom ?? 1.0) + delta);

        private void SetZoom(double zoom)
        {
            if (_state == null) return;
            _state.Zoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 4.0);
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
                or nameof(AppConfig.PadWordWrap) or nameof(AppConfig.PadShowLineNumbers) or nameof(AppConfig.PadReadingFont))
            {
                if (Dispatcher.CheckAccess()) ApplyEditorSettings();
                else Dispatcher.BeginInvoke(new Action(ApplyEditorSettings));
            }
            else if (e.PropertyName == nameof(AppConfig.PadTheme))
            {
                if (Dispatcher.CheckAccess()) ApplyTheme();
                else Dispatcher.BeginInvoke(new Action(ApplyTheme));
            }
            else if (e.PropertyName == nameof(AppConfig.PadMarkdown))
            {
                if (Dispatcher.CheckAccess()) ApplyLanguage();
                else Dispatcher.BeginInvoke(new Action(ApplyLanguage));
            }
            else if (e.PropertyName is nameof(AppConfig.PadDiagrams) or nameof(AppConfig.PadKroki) or nameof(AppConfig.PadKrokiServer)
                     or nameof(AppConfig.PadWebImages))
            {
                if (Dispatcher.CheckAccess()) ApplyDiagramSettings();
                else Dispatcher.BeginInvoke(new Action(ApplyDiagramSettings));
            }
        }

        /// <summary>The time the timestamp tools insert. Tests replace it.</summary>
        internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

        private DispatcherTimer? _statusTimer;

        /// <summary>A short message in the status bar for 5 s (a link that failed, a busy clipboard, a tool that cannot apply).</summary>
        internal void ShowStatus(string message)
        {
            StatusMessage.Text = message;
            StatusMessage.Visibility = Visibility.Visible;
            if (_statusTimer == null)
            {
                _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
                _statusTimer.Tick += OnStatusTimer;
            }
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void OnStatusTimer(object? sender, EventArgs e)
        {
            _statusTimer?.Stop();
            StatusMessage.Visibility = Visibility.Collapsed;
        }

        // ---- links ---------------------------------------------------------------------------

        /// <summary>Opens an allowed link in the default browser or mail program; false when that failed. Tests replace it.</summary>
        internal Func<Uri, bool> OpenLink { get; set; } = uri =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                DiagnosticsLog.Warn("pad", "A link could not be opened: " + ex.Message);
                return false;
            }
        };

        /// <summary>Ctrl+Click on a link. Anything but http, https or mailto is ignored, whatever asked for it.</summary>
        internal void OnLinkRequested(Uri uri)
        {
            if (!SafeLinks.IsAllowed(uri)) return;
            if (!OpenLink(uri)) ShowStatus("That link could not be opened.");
        }

        private readonly System.Windows.Controls.ToolTip _linkTip = new() { Content = "Ctrl+Click to open", Placement = PlacementMode.Mouse };

        private void OnEditorMouseHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position is not { } at || Editor.Document == null) return;
            var line = Editor.Document.GetLineByNumber(at.Line);
            if (line.Length > 4000) return;
            if (SafeLinks.LinkAt(Editor.Document.GetText(line), at.Column - 1) == null) return;
            _linkTip.PlacementTarget = Editor.TextArea.TextView;
            _linkTip.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// One link setup for the note editor and the history preview: AvalonEdit's own hyperlinks off
        /// (they would shell-open ftp: and www.), the safe generator on, and RequestNavigate always
        /// handled so AvalonEdit never starts a process itself. The hover tooltip is on the note editor only.
        /// </summary>
        private void ConfigureLinks(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.TextArea.TextView.ElementGenerators.Add(new SafeLinkGenerator());
            editor.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((s, e) => { e.Handled = true; OnLinkRequested(e.Uri); }));
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
            area.TextView.LinkTextForegroundBrush = PadThemeApplier.ToBrush(_palette.MdLink);
            PreviewEditor.TextArea.TextView.LinkTextForegroundBrush = area.TextView.LinkTextForegroundBrush;
            ModernWpf.ThemeManager.SetRequestedTheme(_linkTip, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            Editor.LineNumbersForeground = PadThemeApplier.ToBrush(_palette.LineNumbers);
            PreviewEditor.LineNumbersForeground = Editor.LineNumbersForeground;
            _bookmarkMargin?.InvalidateVisual();
            PreviewEditor.TextArea.SelectionBrush = area.SelectionBrush;
            FindBar.ApplyPalette(_palette);
            AiPanel.ApplyTheme(_palette.IsDark);
            _occurrences.Fill = PadThemeApplier.ToBrush(_palette.Occurrence);
            area.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);

            // Sun (E706) offers the light theme, moon (E708) the dark one.
            ThemeButton.Content = _palette.IsDark ? "\uE706" : "\uE708";
            ThemeButton.ToolTip = _palette.IsDark ? "Switch to light theme" : "Switch to dark theme";
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.FoldingMarkerBrushProperty, PadThemeApplier.ToBrush(_palette.LineNumbers));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.FoldingMarkerBackgroundBrushProperty, PadThemeApplier.ToBrush(_palette.Background));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.SelectedFoldingMarkerBrushProperty, PadThemeApplier.ToBrush(_palette.Accent));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, PadThemeApplier.ToBrush(_palette.Background));
            _language.Redraw();
            _previewLanguage.Redraw();
        }

        /// <summary>The theme button: switches between dark and light, remembered in the config.</summary>
        internal void ToggleTheme() => _config.PadTheme = _palette.IsDark ? PadThemes.Light : PadThemes.Dark;

        private void OnThemeButtonClick(object sender, RoutedEventArgs e) => ToggleTheme();

        /// <summary>Prose in Markdown tabs while Settings -> MicaPad -> Reading font is on (Wiki.js spec 1.1).</summary>
        internal static readonly FontFamily ReadingFont = new("Segoe UI Variable Text, Segoe UI");

        private string? _monoSource;
        private FontFamily? _monoFamily;

        /// <summary>The editor font with its fallbacks; one instance per font name.</summary>
        private FontFamily MonoFamily
        {
            get
            {
                string source = _config.PadFontFamily + ", Cascadia Mono, Consolas";
                if (_monoFamily == null || _monoSource != source)
                {
                    _monoSource = source;
                    _monoFamily = new FontFamily(source);
                }
                return _monoFamily;
            }
        }

        /// <summary>
        /// The editor's font for the shown tab: the reading font for a Markdown tab while it is on,
        /// with code, inline code and tables kept in the editor font; the editor font otherwise.
        /// </summary>
        private void ApplyEditorFont()
        {
            var mono = MonoFamily;
            bool reading = _config.PadReadingFont && ReferenceEquals(_language.Current, PadLanguages.Markdown);
            var family = reading ? ReadingFont : mono;
            var monoFont = reading ? mono : null;
            if (ReferenceEquals(Editor.FontFamily, family) && ReferenceEquals(_language.MonoFont, monoFont)) return;
            Editor.FontFamily = family;
            _language.MonoFont = monoFont;
            _language.Redraw();
        }

        private void ApplyEditorSettings()
        {
            ApplyEditorFont();
            Editor.FontSize = _config.PadFontSize * (_state?.Zoom ?? 1.0);
            Editor.WordWrap = _config.PadWordWrap;
            Editor.ShowLineNumbers = _config.PadShowLineNumbers;
            // Turning line numbers on puts AvalonEdit's margin at the front: the dots stay left of them.
            var margins = Editor.TextArea.LeftMargins;
            int at = margins.IndexOf(_bookmarkMargin);
            if (at > 0) margins.Move(at, 0);
        }

        // ---- status bar ---------------------------------------------------------------------

        private void UpdateCaretText()
        {
            var caret = Editor.TextArea.Caret;
            CaretText.Text = PadText.CaretPosition(caret.Line, caret.Column);
        }

        private void UpdateCharsText() => CharsText.Text = PadText.CharCount(Editor.Document?.TextLength ?? 0);

        // ---- language ------------------------------------------------------------------------

        /// <summary>The shown tab's language, after Auto and the size limit.</summary>
        internal ResolvedLanguage ShownLanguage => _resolved;

        /// <summary>What the shown tab's language installed in the editor.</summary>
        internal EditorLanguage LanguageView => _language;
        internal EditorLanguage PreviewLanguage => _previewLanguage;
        internal AutoCloseHandler AutoClose => _autoClose;

        /// <summary>Shows the current tab in its language (spec 2.1) and names it in the status bar.</summary>
        private void ApplyLanguage()
        {
            if (_shown == null) return;
            _resolved = PadLanguages.Resolve(_shown.Meta.Language, _shown.Meta.SourcePath, _config.PadMarkdown, Editor.Document.TextLength);
            LanguageButton.Content = _resolved.DisplayName;
            _language.Apply(_resolved.Effective);
            ApplyEditorFont();
        }

        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            BuildLanguageMenu(_shown).IsOpen = true;
        }

        /// <summary>The status-bar language menu: Auto, then every language, the tab's choice checked.</summary>
        internal ContextMenu BuildLanguageMenu(OpenNote note)
        {
            var menu = NewMenu(LanguageButton, PlacementMode.Top);
            menu.Items.Add(Check("Auto (by file type)", null, note.Meta.Language == null, () => ChooseLanguage(note, null)));
            menu.Items.Add(new Separator());
            foreach (var language in PadLanguages.All)
            {
                string id = language.Id;
                menu.Items.Add(Check(language.Name, null, note.Meta.Language == id, () => ChooseLanguage(note, id)));
            }
            return menu;
        }

        /// <summary>Sets a tab's language (null for Auto), saves it with the note and repaints.</summary>
        internal void ChooseLanguage(OpenNote note, string? languageId)
        {
            _workspace.SetLanguage(note, languageId);
            if (ReferenceEquals(note, _shown)) ApplyLanguage();
        }

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

        // ---- full screen ---------------------------------------------------------------------

        /// <summary>What full screen replaced, to put back on leaving it; null while windowed.</summary>
        private (WindowStyle Style, ResizeMode Resize, WindowState State, Rect Bounds)? _beforeFullScreen;

        internal bool IsFullScreen => _beforeFullScreen != null;

        /// <summary>True while entering full screen passes through Normal on its way to Maximized.</summary>
        private bool _enteringFullScreen;

        /// <summary>
        /// F11 (spec 4.3): no title bar, the whole monitor (a borderless maximized WPF window covers
        /// the taskbar), tabs, find bar and status bar kept. Again: the previous placement. Never
        /// saved: the session keeps the placement from before.
        /// </summary>
        internal void ToggleFullScreen()
        {
            if (_beforeFullScreen is { } before)
            {
                LeaveFullScreen(before.State);
                return;
            }

            var bounds = WindowState == WindowState.Normal || RestoreBounds.IsEmpty
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _beforeFullScreen = (WindowStyle, ResizeMode, WindowState, bounds);
            _enteringFullScreen = true;
            try
            {
                WindowState = WindowState.Normal;      // style changes apply cleanly from Normal
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            finally
            {
                _enteringFullScreen = false;
            }
        }

        /// <summary>Puts back the frame and bounds from before full screen, in <paramref name="state"/>.</summary>
        private void LeaveFullScreen(WindowState state)
        {
            if (_beforeFullScreen is not { } before) return;
            _beforeFullScreen = null;
            WindowState = WindowState.Normal;
            WindowStyle = before.Style;
            ResizeMode = before.Resize;
            if (!double.IsNaN(before.Bounds.Left)) Left = before.Bounds.Left;
            if (!double.IsNaN(before.Bounds.Top)) Top = before.Bounds.Top;
            if (!double.IsNaN(before.Bounds.Width)) Width = before.Bounds.Width;
            if (!double.IsNaN(before.Bounds.Height)) Height = before.Bounds.Height;
            WindowState = state;
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            FollowWindowState();
        }

        /// <summary>
        /// Win+Down, a restore, or a reopen from the tray (which un-minimizes to Normal) takes a full
        /// screen window out of Maximized. Full screen ends with it; otherwise the window would stay
        /// borderless at Normal size, with no title bar to move or resize it. Minimizing keeps it.
        /// Runs on StateChanged, which WPF raises only for a shown window; tests call it directly.
        /// </summary>
        internal void FollowWindowState()
        {
            if (!_enteringFullScreen && IsFullScreen && WindowState == WindowState.Normal)
                LeaveFullScreen(WindowState.Normal);
        }

        private void ApplyPlacement()
        {
            var place = _state!;
            Width = Math.Max(MinWidth, place.Width);
            Height = Math.Max(MinHeight, place.Height);

            if (place.Left is double left && place.Top is double top &&
                PadPlacement.IsReachable(left, top, Width, Height,
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (place.Maximized) WindowState = WindowState.Maximized;
        }

        /// <summary>Records the shown tab's caret and scroll, and this window's placement.</summary>
        private void CaptureViewState()
        {
            if (_shown != null) SaveViewState(_shown);
            if (_state is not { } place) return;

            place.AlwaysOnTop = Topmost;
            // Full screen is never saved: the placement from before it is what the session keeps.
            if (_beforeFullScreen is { } fs) place.Maximized = fs.State == WindowState.Maximized;
            if (!IsLoaded) return;

            if (_beforeFullScreen == null) place.Maximized = WindowState == WindowState.Maximized;
            Rect bounds = _beforeFullScreen is { } saved ? saved.Bounds
                : WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
            {
                place.Left = bounds.Left;
                place.Top = bounds.Top;
                place.Width = bounds.Width;
                place.Height = bounds.Height;
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
