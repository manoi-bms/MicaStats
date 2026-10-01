using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Diagram pictures in the real MicaPad window, built on the UI thread and never shown, over a fake renderer.</summary>
    public class DiagramWindowTests
    {
        private static void WithDiagramWindow(Action<MicaPadWindow, FakeRenderer, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var renderer = new FakeRenderer();
            var before = MicaPadWindow.DiagramRenderer;
            MicaPadWindow.DiagramRenderer = renderer;
            MicaPadWindow? window = null;
            try
            {
                window = new MicaPadWindow(env.Workspace, config);
                window.LoadSession();
                test(window, renderer, config);
            }
            finally
            {
                window?.CloseForExit();
                MicaPadWindow.DiagramRenderer = before;
            }
        });

        /// <summary>Lays the editor out and returns the picture under line <paramref name="line"/>, or null.</summary>
        private static DiagramPicture? PictureUnder(MicaPadWindow window, int line)
        {
            PadLanguageWindowTests.Render(window);
            return window.Editor.TextArea.TextView.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as DiagramPicture;
        }

        [Fact]
        public void A_note_with_a_mermaid_block_shows_its_picture() => WithDiagramWindow((window, renderer, config) =>
        {
            window.Editor.Document.Text = "# Plan\n```mermaid\nflowchart LR\n  a --> b\n```";
            Assert.True(PictureUnder(window, 5)!.IsDrawing);

            window.LanguageView.DiagramBoard!.DrawDue();   // as if typing had paused
            Assert.NotNull(PictureUnder(window, 5));
            var call = Assert.Single(renderer.Calls);
            Assert.Equal(PadThemes.Dark, call.Request.Theme);
            Assert.Null(call.Request.KrokiServer);

            renderer.Finish(0, DiagramFakes.Picture(120, 60));
            PadLanguageWindowTests.Pump();
            Assert.Equal(120, PictureUnder(window, 5)!.Image!.Width);
        });

        [Fact]
        public void Settings_turn_kroki_on_and_pictures_off() => WithDiagramWindow((window, renderer, config) =>
        {
            window.Editor.Document.Text = "```puml\n@startuml\na -> b\n@enduml\n```";
            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", PictureUnder(window, 5)!.ErrorText!.Text);

            config.PadKroki = true;
            config.PadKrokiServer = "http://localhost:8000";
            window.LanguageView.DiagramBoard!.DrawDue();
            PictureUnder(window, 5);
            Assert.Equal("http://localhost:8000", Assert.Single(renderer.Calls).Request.KrokiServer);

            config.PadDiagrams = false;
            Assert.Null(PictureUnder(window, 5));
            Assert.Null(window.LanguageView.DiagramBoard);

            config.PadDiagrams = true;
            Assert.NotNull(PictureUnder(window, 5));
        });

        [Fact]
        public void Copy_picture_puts_the_light_png_on_the_clipboard() => WithDiagramWindow((window, renderer, config) =>
        {
            var copied = new List<byte[]>();
            window.TrySetClipboardImage = png =>
            {
                copied.Add(png);
                return true;
            };
            window.Editor.Document.Text = "```dot\ndigraph { a -> b }\n```";
            PictureUnder(window, 3);
            window.LanguageView.DiagramBoard!.DrawDue();
            PictureUnder(window, 3);
            renderer.Finish(0, DiagramFakes.Picture());
            PadLanguageWindowTests.Pump();

            PictureUnder(window, 3)!.View.CopyPicture!();
            Assert.Equal(PadThemes.Light, renderer.Calls[1].Request.Theme);
            renderer.Finish(1, DiagramFakes.Picture());
            PadLanguageWindowTests.Pump();

            Assert.Same(DiagramFakes.Png, Assert.Single(copied));
        });

        [Fact]
        public void Without_a_renderer_no_pictures_are_installed() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "```mermaid\nflowchart LR\n  a --> b\n```";
            PadLanguageWindowTests.Render(window);

            Assert.Null(window.LanguageView.DiagramBoard);
            Assert.Empty(window.Editor.TextArea.TextView.ElementGenerators.OfType<DiagramGenerator>());
        });

        [Fact]
        public void The_history_preview_never_shows_pictures() => WithDiagramWindow((window, renderer, config) =>
        {
            Assert.NotNull(window.LanguageView.DiagramBoard);
            Assert.Empty(window.PreviewEditor.TextArea.TextView.ElementGenerators.OfType<DiagramGenerator>());
        });

        [Fact]
        public void A_right_click_target_inside_a_picture_is_recognized_but_the_text_view_is_not() => WithDiagramWindow((window, renderer, config) =>
        {
            window.Editor.Document.Text = "```mermaid\nflowchart LR\n  a --> b\n```";
            PictureUnder(window, 4);
            window.LanguageView.DiagramBoard!.DrawDue();
            PictureUnder(window, 4);
            renderer.Finish(0, DiagramFakes.Picture());
            PadLanguageWindowTests.Pump();

            var picture = PictureUnder(window, 4)!;
            Assert.True(DiagramPicture.IsInside(picture));
            Assert.True(DiagramPicture.IsInside(picture.Image));
            Assert.False(DiagramPicture.IsInside(window.Editor.TextArea.TextView));
            Assert.False(DiagramPicture.IsInside(null));
        });
    }
}
