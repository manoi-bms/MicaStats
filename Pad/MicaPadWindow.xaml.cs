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
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pen = System.Windows.Media.Pen;

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

        /// <summary>Builds the window over a workspace; call <see cref="LoadSession"/> before showing it.</summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config)
        {
            InitializeComponent();
            _workspace = workspace;
            _config = config;

            ConfigureEditor();

            TabStrip.ItemsSource = _workspace.Open;
            _workspace.Open.CollectionChanged += OnOpenChanged;
            _config.PropertyChanged += OnConfigChanged;
            Editor.TextArea.Caret.PositionChanged += (s, e) => UpdateCaretText();

            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (s, e) =>
            {
                _workspace.Tick();
                UpdateSaveText();
            };

            LoadIcon();
            SourceInitialized += OnSourceInitialized;
            Deactivated += (s, e) => _workspace.FlushPending();
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
                s_current = window;
                window.LoadSession();
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
        }

        private void CloseActiveTab()
        {
            if (_shown != null) CloseTab(_shown);
        }

        private void ReopenClosed()
        {
            var note = _workspace.ReopenLastClosed();
            if (note != null) ShowNote(note);
        }

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
            menu.Items.Add(Item("Close tab", "Ctrl+W", CloseActiveTab));
            menu.Items.Add(Item("Reopen closed tab", "Ctrl+Shift+T", ReopenClosed));
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

        private static ContextMenu NewMenu(UIElement target, PlacementMode placement)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
            ModernWpf.ThemeManager.SetRequestedTheme(menu, ModernWpf.ElementTheme.Dark);
            return menu;
        }

        private static MenuItem Item(string header, string? gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
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
            area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x3F, 0xD2, 0xE4));
            area.SelectionForeground = null;
            area.SelectionBorder = null;
            area.SelectionCornerRadius = 0;
            area.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4));
            area.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
            area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
            Editor.LineNumbersForeground = new SolidColorBrush(Color.FromArgb(0x66, 0xED, 0xED, 0xF2));
        }

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
                SaveText.Foreground = (Brush)FindResource("AlertRed");
                return;
            }

            SaveText.Foreground = (Brush)FindResource("Muted");
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
