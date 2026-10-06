using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Pad;

using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Every question the vault asks in MicaPad - create, enter or change a PIN, a label to store or
    /// rename, a confirmation - and a revealed value, in one card over the editor area.
    ///
    /// <para>
    /// Each Show* replaces whatever the card was showing; the previous question's callbacks are
    /// dropped, never called. Enter is the primary button, Escape the secondary. A question closes
    /// as Cancel when keyboard focus moves to something else in the window; focus leaving the window
    /// (another app, Windows locking) comes back with it, so the question stays answerable. A reveal
    /// hides as soon as focus leaves the card, the window included. On close every box is emptied
    /// and the timers stop. A PIN or a value is never kept anywhere but the boxes, and a revealed
    /// value is copied only through the card's Copy.
    /// </para>
    /// </summary>
    internal partial class VaultCard : UserControl
    {
        /// <summary>How long a revealed value stays on screen.</summary>
        internal static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(30);

        private const string PinRule = "Use 6 to 12 digits; a longer PIN is stronger.";
        private const string InvalidPinText = "Use 6 to 12 digits.";
        private const string PinsDifferText = "The two PINs differ.";
        private const string NoVaultText = "There is no vault yet.";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly DispatcherTimer _revealTimer;
        private readonly DispatcherTimer _waitTimer;

        private Action<string>? _create;
        private Func<string, UnlockResult>? _unlock;
        private Action? _unlocked;
        private Func<string, string, UnlockResult>? _change;
        private Action<string?>? _labelled;
        private Action? _confirm;
        private Action? _copy;
        private DateTime? _waitUntilUtc;
        private int _version;
        private bool _reconfiguring;

        /// <summary>Builds the card closed.</summary>
        public VaultCard()
        {
            InitializeComponent();
            LabelBox.MaxLength = CredentialVault.MaxLabelLength;
            foreach (var box in PinBoxes)
            {
                box.PreviewTextInput += OnPinTextInput;
                box.PreviewKeyDown += OnPinPreviewKeyDown;
                DataObject.AddPastingHandler(box, OnPinPasting);
            }

            // The revealed value leaves the box only through the card's Copy (the secret clipboard):
            // copy commands run it, and any plain copy or drag of the text is cancelled.
            ValueBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnValueCopy, OnValueCanCopy));
            ValueBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnValueCopy, OnValueCanCopy));
            DataObject.AddCopyingHandler(ValueBox, (s, e) => e.CancelCommand());

            _revealTimer = new DispatcherTimer { Interval = RevealDuration };
            _revealTimer.Tick += (s, e) => OnRevealTimer();
            _waitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _waitTimer.Tick += (s, e) => OnWaitTimer();
            IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
        }

        /// <summary>The clock the PIN wait counts down against; tests replace it.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Raised whenever the card closes, whatever closed it.</summary>
        internal event EventHandler? Closed;

        /// <summary>True while the card shows a question or a value.</summary>
        public bool IsOpen => Mode.Length > 0;

        /// <summary>What the card shows: CreatePin, EnterPin, ChangePin, Store, Rename, Confirm, Reveal, or "" when closed.</summary>
        public string Mode { get; private set; } = "";

        private PasswordBox[] PinBoxes => new[] { PinBox, PinBox2, PinBox3 };

        /// <summary>Asks for a new PIN twice; <paramref name="create"/> gets it once both boxes hold the same valid PIN.</summary>
        public void ShowCreatePin(Action<string> create)
        {
            Begin("CreatePin", "Create a PIN for your credentials",
                "You will need it to see a stored credential again. " + PinRule + " If you forget it, the stored credentials cannot be recovered.",
                "Create PIN", "Cancel");
            ShowField(PinCaption, PinBox, "PIN");
            ShowField(Pin2Caption, PinBox2, "Type it again");
            _create = create;
            FocusFirst();
        }

        /// <summary>
        /// Asks for the PIN, for <paramref name="purpose"/>. <paramref name="unlock"/> checks it; when it
        /// unlocks, the card closes and <paramref name="unlocked"/> runs. Otherwise the card says why.
        /// <paramref name="state"/>, the vault's <see cref="CredentialVault.PinState"/>, shows a wait
        /// left from earlier (input refused, counting down) or the tries left, as the card opens.
        /// </summary>
        public void ShowEnterPin(string purpose, Func<string, UnlockResult> unlock, Action unlocked, PinState? state = null)
        {
            Begin("EnterPin", "Enter your PIN", purpose, "Unlock", "Cancel");
            ShowField(PinCaption, PinBox, "PIN");
            _unlock = unlock;
            _unlocked = unlocked;
            ShowPinState(state);
            FocusFirst();
        }

        /// <summary>
        /// Asks for the current PIN and a new one twice; closes when <paramref name="change"/> answers
        /// Unlocked. <paramref name="state"/> as for <see cref="ShowEnterPin"/>: the current PIN is checked under the same rules.
        /// </summary>
        public void ShowChangePin(Func<string, string, UnlockResult> change, PinState? state = null)
        {
            Begin("ChangePin", "Change PIN", PinRule, "Change PIN", "Cancel");
            ShowField(PinCaption, PinBox, "Current PIN");
            ShowField(Pin2Caption, PinBox2, "New PIN");
            ShowField(Pin3Caption, PinBox3, "Type the new PIN again");
            _change = change;
            ShowPinState(state);
            FocusFirst();
        }

        /// <summary>
        /// Asks for a label before storing a secret of <paramref name="length"/> characters that appears
        /// <paramref name="copies"/> times in the note. <paramref name="store"/> gets the trimmed label, or null for none.
        /// </summary>
        public void ShowStore(int length, int copies, Action<string?> store)
        {
            string body = "It will be replaced by a reference. You need your PIN to see it again.\n\n"
                          + string.Create(Inv, $"Secret: \u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022 ({length} characters)");
            if (copies > 1)
                body += string.Create(Inv, $"\nAppears {copies} times in this note \u2014 every copy will be masked.");

            Begin("Store", "Store as credential", body, "Store", "Cancel");
            ShowField(LabelCaption, LabelBox, "Label (optional)");
            _labelled = store;
            FocusFirst();
        }

        /// <summary>Asks for a new label, starting from <paramref name="current"/>; <paramref name="rename"/> gets it trimmed, or null for none.</summary>
        public void ShowRename(string current, Action<string?> rename)
        {
            Begin("Rename", "Rename credential", "", "Rename", "Cancel");
            ShowField(LabelCaption, LabelBox, "Label");
            LabelBox.Text = current;
            LabelBox.SelectAll();
            _labelled = rename;
            FocusFirst();
        }

        /// <summary>Asks a yes/no question; <paramref name="confirm"/> runs on the primary button, labelled <paramref name="confirmLabel"/> in the alert color.</summary>
        public void ShowConfirm(string title, string message, string confirmLabel, Action confirm)
        {
            Begin("Confirm", title, message, confirmLabel, "Cancel");
            PrimaryButton.ClearValue(StyleProperty);
            PrimaryButton.SetResourceReference(ForegroundProperty, "Pad.AlertRed");
            ButtonIcon.SetGlyph(PrimaryButton, PrimaryGlyph("Confirm", confirmLabel));
            _confirm = confirm;
            FocusFirst();
        }

        /// <summary>Shows a revealed value for <see cref="RevealDuration"/>; Copy runs <paramref name="copy"/>.</summary>
        public void ShowReveal(string title, string value, Action copy)
        {
            Begin("Reveal", title, "Hides in 30 s.", "", "Hide");
            PrimaryButton.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Visible;
            ValueBox.Text = value;
            ValueBox.Visibility = Visibility.Visible;
            _copy = copy;
            _revealTimer.Start();
            FocusFirst();
        }

        /// <summary>Closes the card without answering it.</summary>
        public void Hide() => Close();

        /// <summary>"Too many wrong PINs. Try again in m:ss." - the time left rounded up to whole seconds.</summary>
        internal static string WaitText(TimeSpan left)
        {
            long seconds = left <= TimeSpan.Zero ? 0 : (left.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
            return string.Create(Inv, $"Too many wrong PINs. Try again in {seconds / 60}:{seconds % 60:00}.");
        }

        /// <summary>"Wrong PIN. n more tries before a wait."</summary>
        internal static string TriesText(int left) =>
            left == 1 ? "Wrong PIN. 1 more try before a wait." : string.Create(Inv, $"Wrong PIN. {left} more tries before a wait.");

        /// <summary>The tries left as a card opens, after wrong PINs typed earlier: "n more tries before a wait."</summary>
        internal static string TriesLeftText(int left) =>
            left <= 0 ? "The next wrong PIN starts a wait."
            : left == 1 ? "1 more try before a wait."
            : string.Create(Inv, $"{left} more tries before a wait.");

        /// <summary>A PIN question's opening state: a running wait counts down with input refused; earlier wrong PINs show the tries left.</summary>
        private void ShowPinState(PinState? state)
        {
            if (state == null) return;
            if (state.WaitUntilUtc is { } until && until > UtcNow()) StartWait(until);
            else if (state.TriesBeforeWait < CredentialVault.TriesBeforeFirstWait) SetError(TriesLeftText(state.TriesBeforeWait));
        }

        /// <summary>Enter or the primary button: answers the question.</summary>
        internal void OnPrimary()
        {
            if (_waitUntilUtc != null) return;
            switch (Mode)
            {
                case "CreatePin": SubmitCreate(); break;
                case "EnterPin": SubmitEnter(); break;
                case "ChangePin": SubmitChange(); break;
                case "Store":
                case "Rename": SubmitLabel(); break;
                case "Confirm": CloseThen(_confirm); break;
                case "Reveal": _copy?.Invoke(); break;
            }
        }

        /// <summary>Escape or the secondary button: Cancel, or Hide for a reveal.</summary>
        internal void OnSecondary() => Close();

        /// <summary>The 30-second reveal timer: hides the value.</summary>
        internal void OnRevealTimer()
        {
            _revealTimer.Stop();
            if (Mode == "Reveal") Close();
        }

        /// <summary>The once-a-second wait timer: updates the countdown, and lets PINs in again when it ends.</summary>
        internal void OnWaitTimer()
        {
            if (_waitUntilUtc is not { } until)
            {
                _waitTimer.Stop();
                return;
            }

            var left = until - UtcNow();
            if (left > TimeSpan.Zero)
            {
                SetError(WaitText(left));
                return;
            }

            EndWait();
            SetError("");
            PinBox.Focus();
        }

        private void SubmitCreate()
        {
            string pin = PinBox.Password;
            if (!CredentialVault.IsValidPin(pin)) { Refuse(InvalidPinText, PinBox); return; }
            if (pin != PinBox2.Password) { Refuse(PinsDifferText, PinBox2); return; }

            var create = _create;
            Close();
            create?.Invoke(pin);
        }

        private void SubmitEnter()
        {
            string pin = PinBox.Password;
            if (!CredentialVault.IsValidPin(pin)) { Refuse(InvalidPinText, PinBox); return; }
            if (_unlock == null) return;

            int version = _version;
            var result = _unlock(pin);
            if (version != _version) return;   // the callback moved the card on

            if (result.Outcome == UnlockOutcome.Unlocked) CloseThen(_unlocked);
            else ShowFailure(result);
        }

        private void SubmitChange()
        {
            string current = PinBox.Password;
            string next = PinBox2.Password;
            if (!CredentialVault.IsValidPin(current)) { Refuse(InvalidPinText, PinBox); return; }
            if (!CredentialVault.IsValidPin(next)) { Refuse(InvalidPinText, PinBox2); return; }
            if (next != PinBox3.Password) { Refuse(PinsDifferText, PinBox3); return; }
            if (_change == null) return;

            int version = _version;
            var result = _change(current, next);
            if (version != _version) return;   // the callback moved the card on

            if (result.Outcome == UnlockOutcome.Unlocked) Close();
            else ShowFailure(result);
        }

        private void SubmitLabel()
        {
            string text = LabelBox.Text.Trim();
            CloseThen(_labelled, text.Length == 0 ? null : text);
        }

        /// <summary>A PIN that did not unlock: tries left, a countdown, or no vault.</summary>
        private void ShowFailure(UnlockResult result)
        {
            PinBox.Clear();
            switch (result.Outcome)
            {
                case UnlockOutcome.WrongPin when result.WaitUntilUtc is { } until:
                    StartWait(until);
                    break;
                case UnlockOutcome.WrongPin:
                    SetError(TriesText(result.TriesBeforeWait));
                    PinBox.Focus();
                    break;
                case UnlockOutcome.Waiting:
                    StartWait(result.WaitUntilUtc ?? UtcNow());
                    break;
                case UnlockOutcome.NoVault:
                    SetError(NoVaultText);
                    break;
            }
        }

        private void StartWait(DateTime untilUtc)
        {
            _waitUntilUtc = untilUtc;
            // Disabling the focused box would push focus out of the card, which closes it.
            if (IsKeyboardFocusWithin) SecondaryButton.Focus();
            SetPinInputEnabled(false);
            SetError(WaitText(untilUtc - UtcNow()));
            _waitTimer.Start();
        }

        private void EndWait()
        {
            _waitTimer.Stop();
            _waitUntilUtc = null;
            SetPinInputEnabled(true);
        }

        private void SetPinInputEnabled(bool enabled)
        {
            foreach (var box in PinBoxes) box.IsEnabled = enabled;
            PrimaryButton.IsEnabled = enabled;
        }

        private void Refuse(string error, PasswordBox box)
        {
            SetError(error);
            box.Focus();
            box.SelectAll();
        }

        private void SetError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Clears the card and lays out a new question; the previous one's callbacks are dropped.</summary>
        private void Begin(string mode, string title, string body, string primary, string secondary)
        {
            _reconfiguring = true;
            try
            {
                // Collapsing the focused box would push focus out of the card; park it on a button every card has.
                if (IsKeyboardFocusWithin) SecondaryButton.Focus();
                Reset();

                Mode = mode;
                TitleText.Text = title;
                BodyText.Text = body;
                BodyText.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (UIElement part in new UIElement[] { PinCaption, PinBox, Pin2Caption, PinBox2, Pin3Caption, PinBox3, LabelCaption, LabelBox, ValueBox, CopyButton })
                    part.Visibility = Visibility.Collapsed;

                PrimaryButton.Content = primary;
                PrimaryButton.ContentTemplate = null;
                PrimaryButton.ClearValue(ForegroundProperty);
                PrimaryButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
                ButtonIcon.SetGlyph(PrimaryButton, PrimaryGlyph(mode, primary));
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = secondary;
                ButtonIcon.SetGlyph(SecondaryButton, mode == "Reveal" ? "\uE890" : "\uE711");
                Root.Visibility = Visibility.Visible;
            }
            finally
            {
                _reconfiguring = false;
            }
        }

        private static string PrimaryGlyph(string mode, string label) => mode switch
        {
            "CreatePin" => "\uE72E",
            "EnterPin" => "\uE785",
            "ChangePin" => "\uE70F",
            "Store" => "\uE74E",
            "Rename" => "\uE8AC",
            "Confirm" when label.Equals("Delete", StringComparison.OrdinalIgnoreCase) => "\uE74D",
            "Confirm" => "\uE8FB",
            _ => string.Empty,
        };

        private static void ShowField(TextBlock caption, UIElement field, string text)
        {
            caption.Text = text;
            caption.Visibility = Visibility.Visible;
            field.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Focuses the question's first box once the input that opened the card is done, so a menu
        /// handing focus back to the editor as it closes does not take it from the card.
        /// </summary>
        private void FocusFirst()
        {
            int version = _version;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (version != _version || !IsOpen) return;
                UIElement first = Mode switch
                {
                    "Store" or "Rename" => LabelBox,
                    "Reveal" => ValueBox,
                    "Confirm" => PrimaryButton,
                    _ => PinBox.IsEnabled ? PinBox : SecondaryButton,
                };
                first.Focus();
            }));
        }

        private void CloseThen(Action? next)
        {
            Close();
            next?.Invoke();
        }

        private void CloseThen(Action<string?>? next, string? label)
        {
            Close();
            next?.Invoke(label);
        }

        private void Close()
        {
            if (!IsOpen) return;
            Reset();
            Mode = "";
            Root.Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stops the timers, empties every box, forgets every callback.</summary>
        private void Reset()
        {
            _version++;
            _revealTimer.Stop();
            _waitTimer.Stop();
            _waitUntilUtc = null;
            _create = null;
            _unlock = null;
            _unlocked = null;
            _change = null;
            _labelled = null;
            _confirm = null;
            _copy = null;

            foreach (var box in PinBoxes) box.Clear();
            LabelBox.Clear();
            ValueBox.Text = "";
            SetPinInputEnabled(true);
            SetError("");
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e) => OnPrimary();

        private void OnSecondaryClick(object sender, RoutedEventArgs e) => OnSecondary();

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (Mode == "Reveal") _copy?.Invoke();
        }

        private void OnCardKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsOpen || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OnPrimary();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                OnSecondary();
            }
        }

        /// <summary>The backdrop and the card swallow clicks, so nothing under them is reached.</summary>
        private void OnCardMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue) OnFocusLeft(Keyboard.FocusedElement as DependencyObject);
        }

        /// <summary>
        /// Keyboard focus left the card for <paramref name="now"/> - null when it left the window
        /// (another app, Windows locking). A context menu of one of the card's boxes keeps the card.
        /// Otherwise a reveal hides, whatever took focus. A question closes as Cancel only when
        /// something else in this window took focus; focus that left the window comes back to it.
        /// </summary>
        internal void OnFocusLeft(DependencyObject? now)
        {
            if (_reconfiguring || !IsOpen) return;
            if (now != null && IsInOwnMenu(now)) return;
            if (Mode == "Reveal")
            {
                Close();
                return;
            }

            if (now == null) return;
            var source = PresentationSource.FromDependencyObject(this);
            if (source == null || PresentationSource.FromDependencyObject(now) != source) return;
            Close();
        }

        /// <summary>True when <paramref name="element"/> is in a context menu opened on one of the card's own boxes.</summary>
        private bool IsInOwnMenu(DependencyObject element)
        {
            for (DependencyObject? d = element; d != null; d = ParentOf(d))
            {
                if (d is ContextMenu menu)
                    return menu.PlacementTarget is UIElement target && IsInCard(target);
            }

            return false;
        }

        private bool IsInCard(DependencyObject element)
        {
            for (DependencyObject? d = element; d != null; d = ParentOf(d))
            {
                if (d == this) return true;
            }

            return false;
        }

        private static DependencyObject? ParentOf(DependencyObject d) =>
            (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);

        /// <summary>Ctrl+C, Ctrl+Insert, Cut or the menu's Copy in the reveal box: the card's Copy, never the plain clipboard.</summary>
        private void OnValueCopy(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            if (Mode == "Reveal") _copy?.Invoke();
        }

        private void OnValueCanCopy(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = Mode == "Reveal";
            e.Handled = true;
        }

        private static void OnPinTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!e.Text.All(char.IsAsciiDigit)) e.Handled = true;
        }

        /// <summary>A space is typed without a TextInput event, so it is stopped as a key.</summary>
        private static void OnPinPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) e.Handled = true;
        }

        private static void OnPinPasting(object sender, DataObjectPastingEventArgs e)
        {
            string? text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : e.DataObject.GetDataPresent(DataFormats.Text) ? e.DataObject.GetData(DataFormats.Text) as string
                : null;
            if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiDigit)) e.CancelCommand();
        }
    }
}
