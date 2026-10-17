using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services.Localization;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// Confirms End all filtered. Returns true from <see cref="Window.ShowDialog"/> only when the
    /// user typed the exact number of processes that will end.
    ///
    /// <para>
    /// The plan is fixed when this opens: processes that appear afterwards are not in it, and a
    /// PID reused afterwards is refused by the identity check in <see cref="ProcessControl.TryEndTask"/>.
    /// </para>
    /// </summary>
    public partial class BulkEndDialog : Window
    {
        private readonly string _expected;

        public BulkEndDialog(BulkEndPlan plan)
        {
            InitializeComponent();

            var inv = CultureInfo.InvariantCulture;
            int count = plan.ToEnd.Count;
            _expected = count.ToString(inv);

            Heading.Text = count == 0
                ? UiText.Tr("Nothing here can be ended")
                : UiText.Format(count == 1 ? "End {0} process?" : "End {0} processes?", _expected);
            Cost.Text = count == 0 ? "" : UiText.Format("Together they are using {0}.",
                TaskManagerViewModel.FormatTotals(TaskManagerViewModel.Totals(plan.ToEnd)));

            Targets.ItemsSource = plan.ToEnd
                .Select(p => p.Name + "   ·   PID " + p.Pid.ToString(inv)
                             + "   ·   " + p.CpuText + "   ·   " + p.WorkingSetText)
                .ToList();

            if (plan.Excluded.Count > 0)
            {
                ExcludedPanel.Visibility = Visibility.Visible;
                ExcludedList.ItemsSource = plan.Excluded
                    .Select(e => e.Process.Name + " (" + e.Process.Pid.ToString(inv) + ") — " + e.Reason)
                    .ToList();
            }

            if (count == 0)
            {
                Instruction.Text = UiText.Tr("Every match is protected from being ended.");
                Confirmation.IsEnabled = false;
            }
            else
            {
                Instruction.Text = UiText.Format("Type {0} to end these processes. This cannot be undone.", _expected);
            }

            Loaded += (s, e) => Confirmation.Focus();
        }

        private void OnConfirmationChanged(object sender, TextChangedEventArgs e) =>
            EndButton.IsEnabled = _expected != "0" && Confirmation.Text.Trim() == _expected;

        private void OnEnd(object sender, RoutedEventArgs e) => DialogResult = true;

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
