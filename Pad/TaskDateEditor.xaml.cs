using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Pad;

using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>A compact editor for the task dates stored outside the Markdown document.</summary>
    public partial class TaskDateEditor : UserControl
    {
        private const string Format = "yyyy-MM-dd HH:mm";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Func<DateTimeOffset>? _now;
        private Func<DateTimeOffset, DateTimeOffset?, string?>? _save;
        private DateTimeOffset? _startDraft;
        private DateTimeOffset? _finishDraft;
        private string _startShown = "";
        private string _finishShown = "";

        public TaskDateEditor()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>Raised after a successful save or cancellation so the owner can close its popup.</summary>
        public event EventHandler? Dismissed;

        /// <summary>Starts a fresh, unchanged draft for one task.</summary>
        public void Show(TaskDateRecord dates, Func<DateTimeOffset> now,
                         Func<DateTimeOffset, DateTimeOffset?, string?> save)
        {
            ArgumentNullException.ThrowIfNull(dates);
            ArgumentNullException.ThrowIfNull(now);
            ArgumentNullException.ThrowIfNull(save);

            _now = now;
            _save = save;
            SetStart(dates.Created);
            SetFinish(dates.Finished);
            ShowError(null);
            Visibility = Visibility.Visible;
            StartInput.Focus();
            StartInput.SelectAll();
        }

        /// <summary>Releases the draft and callbacks after the popup closes.</summary>
        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            StartInput.Text = "";
            FinishInput.Text = "";
            _startShown = "";
            _finishShown = "";
            _startDraft = null;
            _finishDraft = null;
            _now = null;
            _save = null;
            ShowError(null);
        }

        /// <summary>Validates and commits the draft. Kept internal for focused UI tests.</summary>
        internal void OnSave()
        {
            if (_save == null) return;
            if (!Read(StartInput.Text, _startShown, _startDraft, null, out DateTimeOffset? start) || !start.HasValue)
            {
                ShowError("Enter the start as yyyy-MM-dd HH:mm.");
                StartInput.Focus();
                return;
            }

            DateTimeOffset startValue = start.Value;
            TimeSpan finishOffset = _finishDraft?.Offset ?? startValue.Offset;
            if (!Read(FinishInput.Text, _finishShown, _finishDraft, finishOffset, out DateTimeOffset? finish))
            {
                ShowError("Enter the finish as yyyy-MM-dd HH:mm, or leave it empty.");
                FinishInput.Focus();
                return;
            }

            if (finish.HasValue && finish.Value < startValue)
            {
                ShowError("Finish cannot be earlier than start.");
                FinishInput.Focus();
                return;
            }

            string? error = _save(startValue, finish);
            if (error != null)
            {
                ShowError(error);
                return;
            }

            ShowError(null);
            Dismissed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Cancels the draft without invoking the save callback.</summary>
        internal void OnCancel() => Dismissed?.Invoke(this, EventArgs.Empty);

        internal bool HandleKey(Key key, ModifierKeys modifiers)
        {
            if (key == Key.Escape)
            {
                OnCancel();
                return true;
            }
            if (key == Key.Enter && modifiers == ModifierKeys.None)
            {
                OnSave();
                return true;
            }
            return false;
        }

        private static bool Read(string text, string shown, DateTimeOffset? exact,
                                 TimeSpan? typedOffset, out DateTimeOffset? value)
        {
            string input = text.Trim();
            if (input.Length == 0)
            {
                value = null;
                return typedOffset.HasValue;
            }
            if (exact.HasValue && string.Equals(input, shown, StringComparison.Ordinal))
            {
                value = exact;
                return true;
            }
            if (!DateTime.TryParseExact(input, Format, Inv, DateTimeStyles.None, out DateTime parsed))
            {
                value = null;
                return false;
            }
            try
            {
                value = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified),
                                           typedOffset ?? exact?.Offset ?? TimeSpan.Zero);
                return true;
            }
            catch (ArgumentException)
            {
                value = null;
                return false;
            }
        }

        private void SetStart(DateTimeOffset value)
        {
            _startDraft = value;
            _startShown = value.ToString(Format, Inv);
            StartInput.Text = _startShown;
        }

        private void SetFinish(DateTimeOffset? value)
        {
            _finishDraft = value;
            _finishShown = value?.ToString(Format, Inv) ?? "";
            FinishInput.Text = _finishShown;
        }

        private void ShowError(string? error)
        {
            ErrorText.Text = error ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnStartNowClick(object sender, RoutedEventArgs e)
        {
            if (_now != null) SetStart(_now());
            ShowError(null);
        }

        private void OnFinishNowClick(object sender, RoutedEventArgs e)
        {
            if (_now != null) SetFinish(_now());
            ShowError(null);
        }

        private void OnClearFinishClick(object sender, RoutedEventArgs e)
        {
            SetFinish(null);
            ShowError(null);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e) => OnSave();

        private void OnCancelClick(object sender, RoutedEventArgs e) => OnCancel();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Enter belongs to a focused button. Intercept it only while the user is editing a date;
            // otherwise Cancel would save and the Now/Clear buttons would never run their own action.
            if (e.Key == Key.Enter && e.OriginalSource is Button) return;
            if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }
    }
}
