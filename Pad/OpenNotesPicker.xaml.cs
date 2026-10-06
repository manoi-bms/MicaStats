using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Pad;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>Metadata-only quick picker for notes already open in MicaPad.</summary>
    public partial class OpenNotesPicker : UserControl
    {
        public static readonly DependencyProperty TabPaletteProperty = DependencyProperty.Register(
            nameof(TabPalette), typeof(PadPalette), typeof(OpenNotesPicker), new PropertyMetadata(PadPalette.Dark));

        public PadPalette TabPalette
        {
            get => (PadPalette)GetValue(TabPaletteProperty);
            set => SetValue(TabPaletteProperty, value);
        }

        private IReadOnlyList<OpenNote> _notes = Array.Empty<OpenNote>();
        private OpenNote? _current;
        private string _windowId = "";
        private bool _changingQuery;

        public OpenNotesPicker()
        {
            InitializeComponent();
            NotesList.ItemsSource = Array.Empty<OpenNoteRow>();
        }

        /// <summary>The user confirmed an open note.</summary>
        public event Action<OpenNote>? NoteChosen;

        /// <summary>The user cancelled the picker.</summary>
        public event Action? DismissRequested;

        /// <summary>Ctrl+P was pressed inside the picker, requesting the host's normal toggle path.</summary>
        public event Action? ToggleRequested;

        /// <summary>Starts a new picker session and selects the current note when possible.</summary>
        internal void Begin(IEnumerable<OpenNote> notes, OpenNote? current, string currentWindowId)
        {
            _changingQuery = true;
            SearchInput.Text = "";
            _changingQuery = false;
            SetNotes(notes, current, currentWindowId, preferredId: current?.Id);
        }

        /// <summary>Refreshes live note metadata without disturbing the query or surviving selection.</summary>
        internal void RefreshNotes(IEnumerable<OpenNote> notes, OpenNote? current, string currentWindowId)
        {
            string? selectedId = (NotesList.SelectedItem as OpenNoteRow)?.Note.Id;
            SetNotes(notes, current, currentWindowId, selectedId);
        }

        /// <summary>Releases every note reference when the host dismisses the picker.</summary>
        internal void Clear()
        {
            _changingQuery = true;
            SearchInput.Text = "";
            _changingQuery = false;
            _notes = Array.Empty<OpenNote>();
            _current = null;
            _windowId = "";
            NotesList.ItemsSource = Array.Empty<OpenNoteRow>();
            NotesList.SelectedItem = null;
            UpdateState(0, 0);
        }

        internal void FocusSearch()
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
        }

        /// <summary>Handles only picker navigation, leaving text editing keys to the search box.</summary>
        internal bool HandleKey(Key key, ModifierKeys modifiers)
        {
            if (key == Key.P && modifiers == ModifierKeys.Control)
            {
                ToggleRequested?.Invoke();
                return true;
            }
            if (modifiers != ModifierKeys.None) return false;

            switch (key)
            {
                case Key.Down:
                    MoveSelection(1);
                    return true;
                case Key.Up:
                    MoveSelection(-1);
                    return true;
                case Key.Enter:
                    ChooseSelected();
                    return true;
                case Key.Escape:
                    DismissRequested?.Invoke();
                    return true;
                default:
                    return false;
            }
        }

        private void SetNotes(IEnumerable<OpenNote> notes, OpenNote? current, string currentWindowId, string? preferredId)
        {
            _notes = notes.ToArray();
            _current = current;
            _windowId = currentWindowId ?? "";
            ApplyFilter(preferredId);
        }

        private void ApplyFilter(string? preferredId = null)
        {
            string[] tokens = SearchInput.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rows = _notes
                .Where(note => Matches(note, tokens))
                .Select(note => OpenNoteRow.From(note, _current, _windowId))
                .ToArray();

            NotesList.ItemsSource = rows;
            OpenNoteRow? selected = preferredId == null ? null : rows.FirstOrDefault(row => row.Note.Id == preferredId);
            selected ??= rows.FirstOrDefault(row => row.IsCurrent);
            selected ??= rows.FirstOrDefault();
            NotesList.SelectedItem = selected;
            if (selected != null) NotesList.ScrollIntoView(selected);
            UpdateState(rows.Length, _notes.Count);
        }

        private static bool Matches(OpenNote note, IReadOnlyList<string> tokens)
        {
            string title = DisplayTitle(note);
            string path = note.Meta.SourcePath ?? "";
            return tokens.All(token => title.Contains(token, StringComparison.OrdinalIgnoreCase)
                                       || path.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateState(int shown, int total)
        {
            bool hasQuery = SearchInput.Text.Trim().Length > 0;
            ResultCount.Text = hasQuery && shown != total
                ? $"{shown} of {total} notes"
                : total == 1 ? "1 note" : $"{total} notes";
            EmptyText.Text = total == 0 ? "No notes are open." : "No open notes match this search.";
            EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            NotesList.Visibility = shown == 0 ? Visibility.Collapsed : Visibility.Visible;
            SearchPlaceholder.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MoveSelection(int delta)
        {
            int count = NotesList.Items.Count;
            if (count == 0) return;
            int index = NotesList.SelectedIndex;
            index = index < 0 ? (delta > 0 ? 0 : count - 1) : Math.Clamp(index + delta, 0, count - 1);
            NotesList.SelectedIndex = index;
            NotesList.ScrollIntoView(NotesList.SelectedItem);
        }

        private void ChooseSelected()
        {
            if (NotesList.SelectedItem is OpenNoteRow row) NoteChosen?.Invoke(row.Note);
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsInitialized) return;
            SearchPlaceholder.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_changingQuery) ApplyFilter((NotesList.SelectedItem as OpenNoteRow)?.Note.Id);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        private void OnNotesClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(NotesList, e.OriginalSource as DependencyObject)
                is ListBoxItem { DataContext: OpenNoteRow row })
            {
                NoteChosen?.Invoke(row.Note);
            }
        }

        private static string DisplayTitle(OpenNote note) =>
            string.IsNullOrWhiteSpace(note.Title) ? $"Untitled {note.Meta.UntitledNumber}" : note.Title;
    }

    internal sealed record OpenNoteRow(OpenNote Note, string Title, string Detail, bool IsCurrent, bool Unsaved)
    {
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnsavedVisibility => Unsaved ? Visibility.Visible : Visibility.Collapsed;
        public string ToolTip => Title + Environment.NewLine + Detail;
        public string AutomationName => Title + ", " + Detail
                                        + (IsCurrent ? ", current" : "")
                                        + (Unsaved ? ", unsaved" : "");

        internal static OpenNoteRow From(OpenNote note, OpenNote? current, string currentWindowId)
        {
            string title = string.IsNullOrWhiteSpace(note.Title) ? $"Untitled {note.Meta.UntitledNumber}" : note.Title;
            string detail = note.Meta.SourcePath is { Length: > 0 } path
                ? path
                : $"Note {note.Meta.UntitledNumber}";
            if (!string.Equals(note.WindowId, currentWindowId, StringComparison.Ordinal)) detail += " · Other window";
            return new OpenNoteRow(note, title, detail,
                current != null && string.Equals(note.Id, current.Id, StringComparison.Ordinal),
                note.HasUnsavedEdits);
        }
    }
}
