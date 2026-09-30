using System;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Auto-closing brackets and quotes: the rules, and typing through the real AvalonEdit path.</summary>
    public class AutoCloseTests
    {
        [Theory]
        [InlineData('(', null, null, AutoCloseAction.Pair)]
        [InlineData('[', ' ', '\n', AutoCloseAction.Pair)]
        [InlineData('{', 'x', ')', AutoCloseAction.Pair)]
        [InlineData('"', ' ', null, AutoCloseAction.Pair)]
        [InlineData('`', '(', ')', AutoCloseAction.Pair)]
        [InlineData(')', 'a', ')', AutoCloseAction.SkipOver)]
        [InlineData('"', 'a', '"', AutoCloseAction.SkipOver)]
        [InlineData('a', null, null, AutoCloseAction.Insert)]
        [InlineData('*', ' ', null, AutoCloseAction.Insert)]
        [InlineData('_', ' ', null, AutoCloseAction.Insert)]
        [InlineData(')', 'a', null, AutoCloseAction.Insert)]
        public void The_rules(char typed, char? before, char? after, AutoCloseAction expected)
        {
            Assert.Equal(expected, AutoClosePolicy.OnType(typed, before, after, hasSelection: false));
        }

        [Theory]
        [InlineData('\'', 'n')]
        [InlineData('"', '3')]
        [InlineData('`', 'z')]
        public void Quotes_do_not_pair_after_a_letter(char quote, char before)
        {
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(quote, before, null, hasSelection: false));
        }

        [Theory]
        [InlineData('(', 'w')]
        [InlineData('"', 'w')]
        [InlineData('[', '1')]
        public void An_opener_before_a_word_does_not_pair(char opener, char after)
        {
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(opener, ' ', after, hasSelection: false));
        }

        [Fact]
        public void A_selection_is_wrapped_by_an_opener_and_replaced_by_anything_else()
        {
            Assert.Equal(AutoCloseAction.Wrap, AutoClosePolicy.OnType('(', null, null, hasSelection: true));
            Assert.Equal(AutoCloseAction.Wrap, AutoClosePolicy.OnType('"', 'a', 'b', hasSelection: true));
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType('x', null, null, hasSelection: true));
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(')', null, null, hasSelection: true));
        }

        [Fact]
        public void Backspace_deletes_an_empty_pair_only()
        {
            Assert.True(AutoClosePolicy.DeletesPair('(', ')'));
            Assert.True(AutoClosePolicy.DeletesPair('"', '"'));
            Assert.False(AutoClosePolicy.DeletesPair('(', ']'));
            Assert.False(AutoClosePolicy.DeletesPair('a', 'a'));
            Assert.False(AutoClosePolicy.DeletesPair(null, ')'));
        }

        private static void WithWindow(bool autoClose, Action<MicaPadWindow> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig { PadAutoClose = autoClose });
            try
            {
                window.LoadSession();
                test(window);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        [Fact]
        public void Typing_an_opener_inserts_the_pair_and_the_closer_steps_over() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("f");
            window.Editor.TextArea.PerformTextInput("(");
            Assert.Equal("f()", window.Editor.Document.Text);
            Assert.Equal(2, window.Editor.CaretOffset);

            window.Editor.TextArea.PerformTextInput("x");
            window.Editor.TextArea.PerformTextInput(")");
            Assert.Equal("f(x)", window.Editor.Document.Text);
            Assert.Equal(4, window.Editor.CaretOffset);
        });

        [Fact]
        public void An_apostrophe_in_prose_types_normally() => WithWindow(true, window =>
        {
            foreach (char c in "don't") window.Editor.TextArea.PerformTextInput(c.ToString());
            Assert.Equal("don't", window.Editor.Document.Text);
        });

        [Fact]
        public void An_opener_wraps_the_selection() => WithWindow(true, window =>
        {
            window.Editor.Document.Text = "word";
            window.Editor.Select(0, 4);
            window.Editor.TextArea.PerformTextInput("\"");
            Assert.Equal("\"word\"", window.Editor.Document.Text);
            Assert.Equal("word", window.Editor.SelectedText);
        });

        [Fact]
        public void Backspace_between_an_empty_pair_removes_both() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("[");
            Assert.Equal("[]", window.Editor.Document.Text);

            Assert.True(window.AutoClose.TryDeletePair());
            Assert.Equal("", window.Editor.Document.Text);

            window.Editor.Document.Text = "a]";
            window.Editor.CaretOffset = 1;
            Assert.False(window.AutoClose.TryDeletePair());
        });

        [Fact]
        public void With_auto_close_off_nothing_is_added() => WithWindow(false, window =>
        {
            window.Editor.TextArea.PerformTextInput("(");
            Assert.Equal("(", window.Editor.Document.Text);
        });

        [Fact]
        public void A_pair_is_one_undo_step() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("{");
            window.Editor.Undo();
            Assert.Equal("", window.Editor.Document.Text);
        });
    }
}
