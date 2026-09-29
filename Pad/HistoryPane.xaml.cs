using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Kil0bitSystemMonitor.Services.Pad;

using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>The history pane: lists versions; the window does the previewing and restoring.</summary>
    public partial class HistoryPane : UserControl
    {
        /// <summary>Builds the pane hidden; <see cref="Show"/> fills and reveals it.</summary>
        public HistoryPane()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>The user picked a version to preview.</summary>
        public event Action<SnapshotInfo>? VersionSelected;

        /// <summary>The user asked to close the pane.</summary>
        public event Action? CloseRequested;

        /// <summary>The rows currently listed.</summary>
        public IReadOnlyList<HistoryRow> Rows { get; private set; } = Array.Empty<HistoryRow>();

        /// <summary>Lists <paramref name="snapshots"/> (newest first) and shows the pane.</summary>
        public void Show(IReadOnlyList<SnapshotInfo> snapshots, DateTime localNow, bool tooLarge)
        {
            Rows = HistoryRows.Build(snapshots, localNow);
            var view = new ListCollectionView(Rows.ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.Group)));
            Versions.ItemsSource = view;

            EmptyText.Text = tooLarge
                ? "This note is over 10 MB, so new versions are no longer kept. It is still saved."
                : Rows.Count == 0
                    ? "No versions yet. One is kept each time you pause after a minute of changes."
                    : "";
            EmptyText.Visibility = EmptyText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        /// <summary>Clears the highlighted version, so picking the same one again previews it again.</summary>
        public void ClearSelection() => Versions.SelectedItem = null;

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Versions.SelectedItem is HistoryRow row) VersionSelected?.Invoke(row.Snapshot);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
