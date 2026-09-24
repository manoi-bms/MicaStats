using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;

// System.Windows.Forms is in scope via UseWindowsForms and defines its own Button and
// MessageBox; unaliased references resolve to the wrong one.
using Button = System.Windows.Controls.Button;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The process list.
    ///
    /// <para>
    /// Exists because Windows Task Manager is frequently unusable on the machines this runs on:
    /// slow or failing to open, opening with a frozen or empty list, End task doing nothing,
    /// and costing enough CPU to worsen the stutter it was opened to diagnose. This window does
    /// no sampling of its own — it reads a snapshot MicaStats already has — so it can appear
    /// and populate on a machine that cannot open Task Manager at all.
    /// </para>
    /// </summary>
    public partial class TaskManagerWindow : Window
    {
        /// <summary>
        /// One window at a time. A second would take a second sampler lease and show the same
        /// rows twice for no benefit.
        /// </summary>
        private static TaskManagerWindow? s_open;

        private readonly TaskManagerViewModel _model;

        /// <summary>The detail pane's state, bound from the XAML.</summary>
        public ProcessDetailViewModel Detail { get; } = new();

        /// <summary>The last kill that was refused for lack of privilege, for the retry button.</summary>
        private (int Pid, long CreateTime, string Name)? _pendingElevation;

        private TaskManagerWindow(ProcessSampler sampler)
        {
            InitializeComponent();

            _model = new TaskManagerViewModel(sampler);
            DataContext = _model;

            Closed += (s, e) =>
            {
                _model.Dispose();
                if (ReferenceEquals(s_open, this)) s_open = null;
            };

            Loaded += (s, e) => SearchBox.Focus();
        }

        /// <summary>Shows the window, or brings the existing one forward. Returns either.</summary>
        public static TaskManagerWindow ShowOrActivate(ProcessSampler sampler)
        {
            if (s_open != null)
            {
                if (s_open.WindowState == WindowState.Minimized) s_open.WindowState = WindowState.Normal;
                s_open.Activate();
                return s_open;
            }

            s_open = new TaskManagerWindow(sampler);
            s_open.Show();
            return s_open;
        }

        /// <summary>
        /// The list is synchronised in place, so this fires only when the user changes the
        /// selection or a selected process exits — never merely because a refresh happened.
        /// </summary>
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int count = ProcessList.SelectedItems.Count;

            EndTaskButton.IsEnabled = count > 0;
            EndTaskButton.Content = count > 1
                ? "End " + count.ToString(CultureInfo.InvariantCulture) + " tasks"
                : "End task";

            // A different selection invalidates the previous refusal.
            _pendingElevation = null;
            RetryElevated.Visibility = Visibility.Collapsed;

            if (count == 1 && ProcessList.SelectedItem is ProcessRow row) Detail.Load(row);
            else if (count > 1) Detail.ShowMany(count);
            else Detail.Clear();
        }

        /// <summary>
        /// Sorts by the clicked column, flipping direction on a second click. The header text is
        /// the key, mapped by <see cref="TaskManagerViewModel.ColumnFor"/>.
        /// </summary>
        private void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
            if (TaskManagerViewModel.ColumnFor(header.Column.Header as string) is { } column)
                _model.SortBy(column);
        }

        /// <summary>End all filtered: everything the search currently matches.</summary>
        private void OnEndAllFiltered(object sender, RoutedEventArgs e)
        {
            if (!_model.CanEndAllFiltered) return;
            ConfirmAndEnd(_model.Filtered);
        }

        /// <summary>
        /// Plans against the given processes, asks for typed confirmation, then ends the set off
        /// the UI thread and reports the outcome in the footer. Shared by End all filtered and by
        /// End task on a multi-row selection, so both go through the same exclusions, preview and
        /// verified kill.
        /// </summary>
        private void ConfirmAndEnd(IReadOnlyList<ProcessUsage> candidates)
        {
            // One batch at a time: a second confirm while one is running would race it.
            if (_model.Ending)
            {
                _model.Message = "Still ending the previous batch; try again when it finishes.";
                return;
            }

            var ancestors = BulkEndPlan.AncestorsOf(Environment.ProcessId, _model.Snapshot);
            var plan = BulkEndPlan.Build(candidates, Environment.ProcessId, ancestors);

            var dialog = new BulkEndDialog(plan) { Owner = this };
            if (dialog.ShowDialog() != true || plan.ToEnd.Count == 0) return;

            // Ending is folded into CanEndAllFiltered rather than assigning EndAllButton.IsEnabled
            // directly, so the XAML binding stays live for the rest of the window's life.
            _model.Ending = true;
            _model.Message = "Ending " + plan.ToEnd.Count.ToString(CultureInfo.InvariantCulture) + "…";

            System.Threading.Tasks.Task.Run(() =>
            {
                string outcome;
                try { outcome = BulkEnd.Run(plan.ToEnd).Describe(); }
                catch (Exception ex)
                {
                    outcome = "Ending the processes failed; the log has the detail.";
                    DiagnosticsLog.Error("processes", "Ending selected or filtered processes failed", ex);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _model.Message = outcome;
                    _model.Ending = false;
                    _model.Refresh();
                }));
            });
        }

        private void OnEndTask(object sender, RoutedEventArgs e)
        {
            var selected = ProcessList.SelectedItems.OfType<ProcessRow>().ToList();
            if (selected.Count == 0) return;

            // Several rows: the same exclusions, preview and typed confirmation as End all
            // filtered. One row keeps the immediate End task, with its elevation retry.
            if (selected.Count > 1)
            {
                ConfirmAndEnd(TaskManagerViewModel.Matching(_model.Filtered, selected));
                return;
            }

            var row = selected[0];

            var result = ProcessControl.TryEndTask(row.Pid, row.CreateTime, row.Name, out string message);
            _model.Message = message;

            // Elevation is offered only where it could actually help. It does not make ending
            // csrss.exe a good idea, and it cannot resurrect a process that already exited.
            if (result == EndTaskResult.AccessDenied)
            {
                _pendingElevation = (row.Pid, row.CreateTime, row.Name);
                RetryElevated.Visibility = Visibility.Visible;
            }
            else
            {
                _pendingElevation = null;
                RetryElevated.Visibility = Visibility.Collapsed;
            }

            _model.Refresh();
        }

        /// <summary>
        /// Relaunches this executable elevated to end one process, then exits. MicaStats itself
        /// never holds administrator rights — the consent is for a single termination.
        /// </summary>
        private void OnRetryElevated(object sender, RoutedEventArgs e)
        {
            if (_pendingElevation is not { } target) return;

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                _model.Message = "Could not locate MicaStats to relaunch it.";
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = KillArguments.Switch + " "
                            + target.Pid.ToString(CultureInfo.InvariantCulture) + " "
                            + target.CreateTime.ToString(CultureInfo.InvariantCulture),
                Verb = "runas",          // raises the consent prompt
                UseShellExecute = true,  // required for runas
            };

            try
            {
                using var elevated = System.Diagnostics.Process.Start(psi);
                if (elevated == null)
                {
                    _model.Message = "Windows did not start the elevated helper.";
                    return;
                }

                elevated.WaitForExit(10_000);
                _model.Message = elevated.HasExited && elevated.ExitCode == 0
                    ? "Ended " + target.Name + " as administrator."
                    : "Could not end " + target.Name + " even elevated. Windows may be protecting it.";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Dismissing the consent prompt throws ERROR_CANCELLED. That is an answer, not
                // a fault, and reporting it as an error would say something went wrong when the
                // user simply declined.
                _model.Message = "Cancelled — " + target.Name + " is still running.";
            }

            _pendingElevation = null;
            RetryElevated.Visibility = Visibility.Collapsed;
            _model.Refresh();
        }
    }
}
