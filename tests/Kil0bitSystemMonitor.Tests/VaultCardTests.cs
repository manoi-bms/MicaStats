using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The vault card: what each question shows, what it accepts, what it refuses.</summary>
    public class VaultCardTests
    {
        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        [Fact]
        public void Creating_a_pin_needs_the_same_valid_pin_twice() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            var created = new List<string>();
            card.ShowCreatePin(created.Add);
            Assert.Equal("Create a PIN for your credentials", card.TitleText.Text);

            card.PinBox.Password = "12345";
            card.PinBox2.Password = "12345";
            Click(card.PrimaryButton);
            Assert.Equal("Use 6 to 12 digits.", card.ErrorText.Text);

            card.PinBox.Password = "123456";
            card.PinBox2.Password = "123457";
            Click(card.PrimaryButton);
            Assert.Equal("The two PINs differ.", card.ErrorText.Text);

            card.PinBox2.Password = "123456";
            Click(card.PrimaryButton);
            Assert.Equal(new[] { "123456" }, created);
            Assert.False(card.IsOpen);
            Assert.Equal("", card.PinBox.Password);
        });

        [Fact]
        public void Wrong_pins_show_the_tries_left_then_a_countdown() => UiThread.Run(() =>
        {
            var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var card = new VaultCard { UtcNow = () => now };
            var results = new Queue<UnlockResult>(new[]
            {
                new UnlockResult(UnlockOutcome.WrongPin, 1),
                new UnlockResult(UnlockOutcome.WrongPin, 0, now.AddSeconds(30)),
            });
            bool unlocked = false;
            card.ShowEnterPin("To reveal \u201CBank\u201D.", _ => results.Dequeue(), () => unlocked = true);

            card.PinBox.Password = "000000";
            Click(card.PrimaryButton);
            Assert.Equal("Wrong PIN. 1 more try before a wait.", card.ErrorText.Text);
            Assert.True(card.IsOpen);

            card.PinBox.Password = "000000";
            Click(card.PrimaryButton);
            Assert.Equal("Too many wrong PINs. Try again in 0:30.", card.ErrorText.Text);
            Assert.False(card.PinBox.IsEnabled);
            Assert.False(card.PrimaryButton.IsEnabled);
            Assert.False(unlocked);
        });

        [Fact]
        public void The_right_pin_closes_the_card_and_continues() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            bool unlocked = false;
            card.ShowEnterPin("To reveal it.", pin => new UnlockResult(pin == "246810" ? UnlockOutcome.Unlocked : UnlockOutcome.WrongPin, 4), () => unlocked = true);

            card.PinBox.Password = "246810";
            Click(card.PrimaryButton);

            Assert.True(unlocked);
            Assert.False(card.IsOpen);
        });

        [Theory]
        [InlineData(30, "Too many wrong PINs. Try again in 0:30.")]
        [InlineData(61, "Too many wrong PINs. Try again in 1:01.")]
        [InlineData(900, "Too many wrong PINs. Try again in 15:00.")]
        public void Wait_text(int seconds, string expected) => Assert.Equal(expected, VaultCard.WaitText(TimeSpan.FromSeconds(seconds)));

        [Fact]
        public void Store_shows_the_length_and_the_copies_never_the_secret() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            string? stored = "unset";
            card.ShowStore(12, 3, label => stored = label);

            Assert.Contains("(12 characters)", card.BodyText.Text);
            Assert.Contains("Appears 3 times in this note \u2014 every copy will be masked.", card.BodyText.Text);
            card.LabelBox.Text = "  Bank  ";
            Click(card.PrimaryButton);

            Assert.Equal("Bank", stored);
            Assert.Equal(CredentialVault.MaxLabelLength, card.LabelBox.MaxLength);
        });

        [Fact]
        public void One_copy_says_nothing_about_copies() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            card.ShowStore(8, 1, _ => { });

            Assert.DoesNotContain("Appears", card.BodyText.Text);
        });

        [Fact]
        public void A_reveal_shows_the_value_and_hides_by_itself() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            int copied = 0;
            card.ShowReveal("Bank", "hunter2", () => copied++);

            Assert.Equal("hunter2", card.ValueBox.Text);
            Assert.True(card.ValueBox.IsReadOnly);
            Click(card.CopyButton);
            Assert.Equal(1, copied);

            card.OnRevealTimer();   // the 30-second timer
            Assert.False(card.IsOpen);
            Assert.Equal("", card.ValueBox.Text);
        });

        [Fact]
        public void A_new_question_drops_the_previous_one() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            bool firstCalled = false;
            card.ShowRename("Old", _ => firstCalled = true);
            card.ShowConfirm("Delete credential", "Delete it?", "Delete", () => { });

            Assert.Equal("Confirm", card.Mode);
            Click(card.SecondaryButton);
            Assert.False(firstCalled);
            Assert.False(card.IsOpen);
        });

        // Beyond the brief: the digits-only boxes and the Enter/Escape keys, raised without a window.

        [Fact]
        public void Pin_boxes_refuse_anything_but_digits() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            card.ShowEnterPin("To reveal it.", _ => new UnlockResult(UnlockOutcome.WrongPin, 4), () => { });

            Assert.True(Type(card.PinBox, "a").Handled);
            Assert.True(Type(card.PinBox, "\u0661").Handled);   // an Arabic-Indic one: a digit, not an ASCII one
            Assert.False(Type(card.PinBox, "7").Handled);
            Assert.True(Paste(card.PinBox, "12 34").CommandCancelled);
            Assert.False(Paste(card.PinBox, "123456").CommandCancelled);
            Assert.Equal(12, card.PinBox.MaxLength);
        });

        [Fact]
        public void Enter_answers_the_card_and_escape_cancels_it() => UiThread.Run(() =>
        {
            var card = new VaultCard();
            var created = new List<string>();
            card.ShowCreatePin(created.Add);
            card.PinBox.Password = "123456";
            card.PinBox2.Password = "123456";
            Press(card.PinBox2, Key.Enter);
            Assert.Equal(new[] { "123456" }, created);

            bool confirmed = false;
            int closed = 0;
            card.Closed += (s, e) => closed++;
            card.ShowConfirm("Delete credential", "Delete it?", "Delete", () => confirmed = true);
            Press(card.TitleText, Key.Escape);
            Assert.False(confirmed);
            Assert.False(card.IsOpen);
            Assert.Equal(1, closed);
        });

        private static TextCompositionEventArgs Type(UIElement target, string text)
        {
            var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text))
            {
                RoutedEvent = UIElement.PreviewTextInputEvent,
            };
            target.RaiseEvent(args);
            return args;
        }

        private static DataObjectPastingEventArgs Paste(UIElement target, string text)
        {
            var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText);
            target.RaiseEvent(args);
            return args;
        }

        private static void Press(UIElement target, Key key) =>
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new NoSource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });

        /// <summary>A key event needs a source; this one has no window behind it.</summary>
        private sealed class NoSource : PresentationSource
        {
            public override Visual? RootVisual { get; set; }

            public override bool IsDisposed => false;

            protected override CompositionTarget? GetCompositionTargetCore() => null;
        }
    }
}
