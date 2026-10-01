using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Image previews in the real MicaPad window (spec 6.3) and the two Settings cards (spec 7), built on the UI thread and never shown.</summary>
    public class ImageWindowTests
    {
        private static void WithImageWindow(Action<MicaPadWindow, PadTestEnv, AppConfig, FakeImageHandler> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var handler = new FakeImageHandler();
            using var sources = new ImageSources(handler);
            var before = MicaPadWindow.ImageLoader;
            MicaPadWindow.ImageLoader = sources;
            MicaPadWindow? window = null;
            try
            {
                window = new MicaPadWindow(env.Workspace, config);
                window.LoadSession();
                test(window, env, config, handler);
            }
            finally
            {
                window?.CloseForExit();
                MicaPadWindow.ImageLoader = before;
            }
        });

        /// <summary>Runs what is queued, lays the editor out and returns the previews under line <paramref name="line"/>, or null.</summary>
        private static ImageRow? RowUnder(MicaPadWindow window, int line)
        {
            PadLanguageWindowTests.Pump();
            PadLanguageWindowTests.Render(window);
            return window.Editor.TextArea.TextView.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as ImageRow;
        }

        [Fact]
        public void A_relative_image_needs_a_saved_file_and_resolves_beside_one() => WithImageWindow((window, env, config, handler) =>
        {
            window.Editor.Document.Text = "![chart](chart.png)";
            Assert.Equal("A relative path needs a saved file.", RowUnder(window, 1)!.Pictures[0].ErrorText!.Text);

            File.WriteAllBytes(env.FileOf("chart.png"), DiagramFakes.Png);
            PadLanguageWindowTests.OpenFile(window, env, "doc.md", "# Doc\n![chart](chart.png)");
            window.LanguageView.ImageBoard!.DrawDue();
            RowUnder(window, 2);
            UiPump.Wait(window.LanguageView.ImageBoard!.Loading);

            Assert.Equal(1, RowUnder(window, 2)!.Pictures[0].Image!.Width);
            Assert.Empty(window.PreviewEditor.TextArea.TextView.ElementGenerators.OfType<ImageGenerator>());
        });

        [Fact]
        public void Load_images_from_the_web_and_draw_diagrams_follow_the_settings() => WithImageWindow((window, env, config, handler) =>
        {
            window.Editor.Document.Text = "![logo](https://example.com/logo.png)";
            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.", RowUnder(window, 1)!.Pictures[0].ErrorText!.Text);
            Assert.Empty(handler.Requests);

            config.PadWebImages = true;
            window.LanguageView.ImageBoard!.DrawDue();   // as if typing had paused
            RowUnder(window, 1);
            UiPump.Wait(window.LanguageView.ImageBoard!.Loading);

            Assert.Equal(1, RowUnder(window, 1)!.Pictures[0].Image!.Width);
            Assert.Equal("https://example.com/logo.png", Assert.Single(handler.Requests).Uri.AbsoluteUri);

            config.PadDiagrams = false;
            Assert.Null(RowUnder(window, 1));
            Assert.Null(window.LanguageView.ImageBoard);
        });

        [Fact]
        public void Settings_has_the_reading_font_and_web_image_cards()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            string code = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml.cs"));

            Assert.Contains("x:Name=\"PadReadingFontToggle\"", xaml);
            Assert.Contains("Text=\"Reading font\"", xaml);
            Assert.Contains("Text=\"Prose in Markdown notes uses Segoe UI; code and tables stay in the editor font.\"", xaml);
            Assert.Contains("x:Name=\"PadWebImagesToggle\"", xaml);
            Assert.Contains("Text=\"Load images from the web\"", xaml);
            Assert.Contains("Text=\"Images with an http or https address are downloaded when the note is shown. Off: only images on this PC are shown.\"", xaml);
            Assert.Contains("markmap and math block in Markdown notes, drawn on this PC, and a preview under each image.", xaml);
            Assert.Contains("cfg.PadReadingFont = PadReadingFontToggle.IsOn;", code);
            Assert.Contains("cfg.PadWebImages = PadWebImagesToggle.IsOn;", code);
            Assert.Contains("PadReadingFontToggle.IsOn = cfg.PadReadingFont;", code);
            Assert.Contains("PadWebImagesToggle.IsOn = cfg.PadWebImages;", code);
        }

        [Fact]
        public void The_app_gives_micapad_its_image_loader_and_disposes_it()
        {
            string app = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "App.xaml.cs"));

            Assert.Contains("Kil0bitSystemMonitor.Pad.MicaPadWindow.ImageLoader = s_images;", app);
            Assert.Contains("s_images?.Dispose();", app);
        }
    }
}
