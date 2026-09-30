using System.Linq;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>F11 full screen: in, out, and never saved.</summary>
    public class PadFullScreenTests
    {
        [Fact]
        public void F11_hides_the_title_bar_and_fills_the_monitor() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.True(window.HandleShortcut(Key.F11, ModifierKeys.None));

            Assert.True(window.IsFullScreen);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
        });

        [Fact]
        public void Leaving_full_screen_restores_style_state_and_bounds() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Normal;
            window.Left = 120;
            window.Top = 80;
            window.Width = 820;
            window.Height = 560;
            var style = window.WindowStyle;
            var resize = window.ResizeMode;

            window.ToggleFullScreen();
            window.ToggleFullScreen();

            Assert.False(window.IsFullScreen);
            Assert.Equal(style, window.WindowStyle);
            Assert.Equal(resize, window.ResizeMode);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal((120.0, 80.0, 820.0, 560.0), (window.Left, window.Top, window.Width, window.Height));
        });

        [Fact]
        public void A_maximized_window_comes_back_maximized() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Maximized;
            window.ToggleFullScreen();
            window.ToggleFullScreen();
            Assert.Equal(WindowState.Maximized, window.WindowState);
        });

        [Fact]
        public void The_saved_placement_is_the_one_before_full_screen() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Normal;
            window.ToggleFullScreen();
            window.PrepareForExit();
            Assert.False(env.Workspace.Session.Maximized);     // full screen is maximized underneath; it must not be saved as such
        });

        [Fact]
        public void The_menu_offers_full_screen_with_its_key() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var menu = window.BuildMainMenu();
            var item = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Full screen");
            Assert.Equal("F11", item.InputGestureText);
            Assert.False(item.IsChecked);
        });
    }
}
