using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class PadActionIconTests
    {
        [Fact]
        public void Icon_template_keeps_dynamic_button_content_and_automation_name() => UiThread.Run(() =>
        {
            var button = new Button { Content = "First action", Padding = new Thickness(10, 4, 10, 4) };
            ButtonIcon.SetGlyph(button, "\uE8C8");
            Layout(button);

            TextBlock actionGlyph = Assert.Single(Descendants<TextBlock>(button), text => text.Name == "ActionGlyph");
            Assert.IsType<DecorativeIcon>(actionGlyph);
            Assert.Equal("\uE8C8", actionGlyph.Text);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(actionGlyph));
            Assert.Equal("First action", Assert.Single(Descendants<ContentPresenter>(button), item => item.Name == "ActionLabel").Content);
            Assert.Equal("First action", new ButtonAutomationPeer(button).GetName());

            DataTemplate template = Assert.IsType<DataTemplate>(button.ContentTemplate);
            button.Content = "Updated action";
            Layout(button);

            Assert.Same(template, button.ContentTemplate);
            Assert.Equal("Updated action", Assert.Single(Descendants<ContentPresenter>(button), item => item.Name == "ActionLabel").Content);
            Assert.Equal("Updated action", new ButtonAutomationPeer(button).GetName());
        });

        [Fact]
        public void Secondary_panels_give_each_labeled_action_a_fluent_icon() => UiThread.Run(() =>
        {
            var ai = new AiPane();
            Assert.IsType<DecorativeIcon>(ai.HeadingGlyph);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(ai.HeadingGlyph));
            Assert.Equal("\uE99A", ai.HeadingGlyph.Text);
            AssertGlyphs(new Dictionary<Button, string>
            {
                [ai.StopButton] = "\uE71A",
                [ai.ReplaceButton] = "\uE8AB",
                [ai.InsertButton] = "\uE710",
                [ai.CopyButton] = "\uE8C8",
                [ai.RetryButton] = "\uE72C",
            });

            var search = new SearchPane();
            Assert.IsType<DecorativeIcon>(search.HeadingGlyph);
            Assert.Equal("\uE721", search.HeadingGlyph.Text);
            AssertGlyphs(new Dictionary<Button, string>
            {
                [search.AskButton] = "\uE8BD",
                [search.AnswerStop] = "\uE71A",
                [search.AnswerCopy] = "\uE8C8",
            });

            var find = new FindReplaceBar();
            AssertGlyphs(new Dictionary<Button, string>
            {
                [find.ReplaceButton] = "\uE8AB",
                [find.ReplaceAllButton] = "\uE8AB",
            });

            var history = new HistoryPane();
            Assert.IsType<DecorativeIcon>(history.HeadingGlyph);
            Assert.Equal("\uE81C", history.HeadingGlyph.Text);
        });

        [Fact]
        public void Vault_actions_change_icons_with_the_visible_action_without_replacing_labels() => UiThread.Run(() =>
        {
            var card = new VaultCard();

            card.ShowCreatePin(_ => { });
            AssertAction(card.PrimaryButton, "Create PIN", "\uE72E");
            AssertAction(card.SecondaryButton, "Cancel", "\uE711");

            card.ShowEnterPin("To reveal it.", _ => new UnlockResult(UnlockOutcome.WrongPin, 4), () => { });
            AssertAction(card.PrimaryButton, "Unlock", "\uE785");

            card.ShowChangePin((_, _) => new UnlockResult(UnlockOutcome.Unlocked));
            AssertAction(card.PrimaryButton, "Change PIN", "\uE70F");

            card.ShowStore(8, 1, _ => { });
            AssertAction(card.PrimaryButton, "Store", "\uE74E");

            card.ShowRename("Old", _ => { });
            AssertAction(card.PrimaryButton, "Rename", "\uE8AC");

            card.ShowConfirm("Delete credential", "Delete it?", "Delete", () => { });
            AssertAction(card.PrimaryButton, "Delete", "\uE74D");
            Layout(card.PrimaryButton);
            Assert.Equal("Delete", new ButtonAutomationPeer(card.PrimaryButton).GetName());

            card.ShowReveal("Bank", "secret", () => { });
            AssertAction(card.CopyButton, "Copy", "\uE8C8");
            AssertAction(card.SecondaryButton, "Hide", "\uE890");
            Assert.Equal(Visibility.Collapsed, card.PrimaryButton.Visibility);
        });

        private static void AssertGlyphs(IReadOnlyDictionary<Button, string> expected)
        {
            foreach ((Button button, string glyph) in expected)
            {
                Assert.False(string.IsNullOrWhiteSpace(button.Content as string));
                Assert.Equal(glyph, ButtonIcon.GetGlyph(button));
                Assert.NotNull(button.ContentTemplate);
            }
        }

        private static void AssertAction(Button button, string label, string glyph)
        {
            Assert.Equal(label, button.Content);
            Assert.Equal(glyph, ButtonIcon.GetGlyph(button));
            Assert.NotNull(button.ContentTemplate);
        }

        private static void Layout(FrameworkElement element)
        {
            element.Measure(new Size(360, 80));
            element.Arrange(new Rect(0, 0, Math.Max(1, element.DesiredSize.Width), Math.Max(1, element.DesiredSize.Height)));
            element.UpdateLayout();
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }
    }
}
