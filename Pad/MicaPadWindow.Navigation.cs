using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using static Kil0bitSystemMonitor.Pad.EditorMenus;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    public partial class MicaPadWindow
    {
        public static readonly DependencyProperty TabPaletteProperty = DependencyProperty.Register(
            nameof(TabPalette), typeof(PadPalette), typeof(MicaPadWindow), new PropertyMetadata(PadPalette.Dark));

        public PadPalette TabPalette
        {
            get => (PadPalette)GetValue(TabPaletteProperty);
            private set => SetValue(TabPaletteProperty, value);
        }

        public static readonly DependencyProperty TabHeaderWidthProperty = DependencyProperty.Register(
            nameof(TabHeaderWidth), typeof(double), typeof(MicaPadWindow), new PropertyMetadata(180d));

        public double TabHeaderWidth
        {
            get => (double)GetValue(TabHeaderWidthProperty);
            set => SetValue(TabHeaderWidthProperty, value);
        }

        private readonly HashSet<OpenNote> _navigationNotes = new();
        private INotifyCollectionChanged? _navigationTabs;

        private void ConfigureOpenNotesNavigation()
        {
            NotesPicker.NoteChosen += ChooseOpenNote;
            NotesPicker.DismissRequested += CancelOpenNotes;
            NotesPicker.ToggleRequested += ToggleOpenNotes;
            _workspace.Open.CollectionChanged += OnNavigationOpenChanged;
            SizeChanged += OnNavigationSizeChanged;
            VaultCard.IsVisibleChanged += OnNavigationVaultVisibleChanged;
            SyncNavigationNotes();
        }

        private void BindOpenNotesNavigation(WindowTabs tabs)
        {
            if (_navigationTabs != null) _navigationTabs.CollectionChanged -= OnNavigationTabsChanged;
            _navigationTabs = tabs;
            _navigationTabs.CollectionChanged += OnNavigationTabsChanged;
            UpdateOpenNotesCount();
        }

        private void SyncNavigationNotes()
        {
            var open = _workspace.Open.ToHashSet();
            foreach (var note in _navigationNotes.Where(n => !open.Contains(n)).ToList())
            {
                note.PropertyChanged -= OnNavigationNoteChanged;
                _navigationNotes.Remove(note);
            }
            foreach (var note in open)
                if (_navigationNotes.Add(note)) note.PropertyChanged += OnNavigationNoteChanged;
        }

        private void OnNavigationOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            SyncNavigationNotes();
            RefreshOpenNotes();
        }

        private void OnNavigationTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateOpenNotesCount();
            RefreshOpenNotes();
        }

        private void OnNavigationNoteChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OpenNote.WindowId)) UpdateOpenNotesCount();
            if (e.PropertyName is nameof(OpenNote.Title) or nameof(OpenNote.WindowId) or nameof(OpenNote.HasUnsavedEdits) or nameof(OpenNote.IsActive) or nameof(OpenNote.TabColorHue))
                RefreshOpenNotes();
        }

        private MenuItem BuildTabColorMenu(OpenNote note)
        {
            var menu = new MenuItem { Header = "Tab color", Icon = ColorSwatch(note.TabColorHue) };
            if (!NoteTabColors.Choices.Any(choice => choice.Hue == note.TabColorHue))
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "Automatic color",
                    Icon = ColorSwatch(note.TabColorHue),
                    IsCheckable = true,
                    IsChecked = true,
                    IsEnabled = false,
                });
                menu.Items.Add(new Separator());
            }
            foreach (var choice in NoteTabColors.Choices)
            {
                int hue = choice.Hue;
                var item = Item(choice.Name, null, () => _workspace.SetTabColorHue(note, hue));
                item.Icon = ColorSwatch(hue);
                item.IsCheckable = true;
                item.IsChecked = note.TabColorHue == hue;
                menu.Items.Add(item);
            }
            return menu;
        }

        private System.Windows.Shapes.Ellipse ColorSwatch(int hue) => new()
        {
            Width = 10,
            Height = 10,
            Fill = PadThemeApplier.ToBrush(NoteTabColors.Accent(hue, _palette)),
        };

        private void UpdateOpenNotesCount()
        {
            int count = TabStrip.Items.Count;
            OpenNotesCount.Text = count.ToString(CultureInfo.InvariantCulture);
            AutomationProperties.SetName(OpenNotesButton, "Open notes, " + OpenNotesCount.Text + " in this window");
        }

        internal void PrepareOpenNotes() => NotesPicker.Begin(_workspace.Open, _shown, _windowId);

        private void RefreshOpenNotes()
        {
            if (OpenNotesPopup.IsOpen) NotesPicker.RefreshNotes(_workspace.Open, _shown, _windowId);
        }

        private void ToggleOpenNotes()
        {
            if (OpenNotesPopup.IsOpen) { CancelOpenNotes(); return; }
            if (_exiting || VaultCard.IsOpen) return;
            CloseTaskDateEditor();
            ClosedPopup.IsOpen = false;
            RenamePopup.IsOpen = false;
            PrepareOpenNotes();
            UpdateOpenNotesBounds();
            OpenNotesPopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (OpenNotesPopup.IsOpen) NotesPicker.FocusSearch();
            }));
        }

        private void OnOpenNotesClick(object sender, RoutedEventArgs e) => ToggleOpenNotes();

        internal void ChooseOpenNote(OpenNote note)
        {
            CloseOpenNotes();
            if (!_workspace.Open.Contains(note)) return;
            // ShowNote also presents the owning window when this note belongs elsewhere.
            ShowNote(note);
            if (note.WindowId == _windowId) Editor.Focus();
        }

        private void CancelOpenNotes()
        {
            CloseOpenNotes();
            if (!_exiting) Editor.Focus();
        }

        internal void CloseOpenNotes()
        {
            OpenNotesPopup.IsOpen = false;
            NotesPicker.Clear();
        }

        private void OnOpenNotesPopupClosed(object? sender, EventArgs e) => NotesPicker.Clear();

        internal void UpdateOpenNotesBounds()
        {
            var content = Content as FrameworkElement;
            double width = content?.ActualWidth > 0 ? content.ActualWidth : ActualWidth > 0 ? ActualWidth : Width;
            double height = content?.ActualHeight > 0 ? content.ActualHeight : ActualHeight > 0 ? ActualHeight : Height;
            OpenNotesPanel.Width = Math.Max(240, Math.Min(480, width - 24));
            OpenNotesPanel.MaxHeight = Math.Max(160, Math.Min(480, height - 56));
            double left = OpenNotesButton.TranslatePoint(new Point(), this).X;
            OpenNotesPopup.HorizontalOffset = Math.Max(12 - left, OpenNotesButton.ActualWidth - OpenNotesPanel.Width);
        }

        private void OnNavigationSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (OpenNotesPopup.IsOpen) UpdateOpenNotesBounds();
            if (TaskDatesPopup.IsOpen) UpdateTaskDateEditorBounds();
        }

        private void OnNavigationVaultVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (VaultCard.IsVisible)
            {
                CloseOpenNotes();
                CloseTaskDateEditor();
            }
        }

        private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || TabScroller.ScrollableWidth <= 0) return;
            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void OnTabKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || sender is not FrameworkElement { Tag: OpenNote note }) return;
            if (e.Key is Key.Enter or Key.Space)
            {
                ShowNote(note);
                Editor.Focus();
            }
            else if (e.Key is Key.Left or Key.Right)
            {
                var tabs = _workspace.TabsOf(_windowId);
                int index = tabs.IndexOf(note) + (e.Key == Key.Left ? -1 : 1);
                if (index >= 0 && index < tabs.Count && TabStrip.ItemContainerGenerator.ContainerFromItem(tabs[index]) is ContentPresenter container
                    && TabStrip.ItemTemplate.FindName("TabBorder", container) is FrameworkElement target)
                {
                    target.Focus();
                    target.BringIntoView();
                }
            }
            else return;
            e.Handled = true;
        }

        private void DetachOpenNotesNavigation()
        {
            CloseOpenNotes();
            if (_navigationTabs != null) _navigationTabs.CollectionChanged -= OnNavigationTabsChanged;
            _navigationTabs = null;
            _workspace.Open.CollectionChanged -= OnNavigationOpenChanged;
            foreach (var note in _navigationNotes) note.PropertyChanged -= OnNavigationNoteChanged;
            _navigationNotes.Clear();
            NotesPicker.NoteChosen -= ChooseOpenNote;
            NotesPicker.DismissRequested -= CancelOpenNotes;
            NotesPicker.ToggleRequested -= ToggleOpenNotes;
            SizeChanged -= OnNavigationSizeChanged;
            VaultCard.IsVisibleChanged -= OnNavigationVaultVisibleChanged;
        }
    }
}
