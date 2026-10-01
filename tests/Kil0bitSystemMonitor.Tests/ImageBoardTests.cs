using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; this name exists in both.
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Pictures for image tests, made on the UI thread.</summary>
    internal static class ImageFakes
    {
        /// <summary>A transparent PNG of <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
        public static byte[] Png(int width, int height) =>
            Encode(new PngBitmapEncoder(), new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null));

        /// <summary>A black JPEG whose header claims <paramref name="dpi"/> (previews ignore it).</summary>
        public static byte[] Jpeg(int width, int height, double dpi = 96) =>
            Encode(new JpegBitmapEncoder(), new WriteableBitmap(width, height, dpi, dpi, PixelFormats.Bgr24, null));

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Image previews in a Markdown editor (spec 6.3 and "Testing": an image line gets previews),
    /// over temp files, a fake web and a fake renderer. The 600 ms pause is the board's DrawDue,
    /// called by hand; its timer is set to an hour so it never fires inside a test.
    /// </summary>
    public class ImageBoardTests
    {
        private sealed class Fixture : IDisposable
        {
            public Fixture()
            {
                Folder = Dir.Root;
                Sources = new ImageSources(Handler);
                Editor = new TextEditor { Document = new TextDocument() };
                Language = new EditorLanguage(Editor, () => PadPalette.Dark, folds: true)
                {
                    Warn = Warnings.Add,
                    Images = new ImageServices
                    {
                        Sources = Sources,
                        Renderer = Renderer,
                        Enabled = () => Enabled,
                        WebImages = () => Web,
                        BaseFolder = () => Folder,
                        Pause = TimeSpan.FromHours(1),
                        Warn = Warnings.Add,
                    },
                };
            }

            public PadTempDir Dir { get; } = new();
            public FakeImageHandler Handler { get; } = new();
            public FakeRenderer Renderer { get; } = new();
            public List<string> Warnings { get; } = new();
            public ImageSources Sources { get; }
            public TextEditor Editor { get; }
            public EditorLanguage Language { get; }
            public bool Enabled { get; set; } = true;
            public bool Web { get; set; }
            public string? Folder { get; set; }

            public ImageBoard Board => Language.ImageBoard!;
            public TextView View => Editor.TextArea.TextView;

            /// <summary>A new document with <paramref name="text"/>, shown as Markdown and laid out.</summary>
            public void Show(string text)
            {
                Editor.Document = new TextDocument(text);
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            public void Render()
            {
                View.Measure(new Size(600, 400));
                View.Arrange(new Rect(0, 0, 600, 400));
                View.EnsureVisualLines();
            }

            /// <summary>Runs the redraws the dispatcher holds, then lays out again.</summary>
            public void PumpAndRender()
            {
                PadLanguageWindowTests.Pump();
                Render();
            }

            /// <summary>Waits for every load to end, then shows what they gave.</summary>
            public void Settle()
            {
                UiPump.Wait(Board.Loading);
                PumpAndRender();
            }

            public ImageRow? RowUnder(int line) =>
                View.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as ImageRow;

            public DiagramPicture PictureUnder(int line, int index = 0) => RowUnder(line)!.Pictures[index];

            public void Dispose()
            {
                Sources.Dispose();
                Dir.Dispose();
            }
        }

        /// <summary>Pumps the UI thread until <paramref name="condition"/> holds (10 s at most).</summary>
        private static void PumpUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
                PadLanguageWindowTests.Pump();
                Thread.Sleep(5);
            }
        }

        [Fact]
        public void The_pause_after_typing_is_600_ms_as_for_diagrams()
        {
            using var sources = new ImageSources(new FakeImageHandler());
            var services = new ImageServices { Sources = sources, Enabled = () => true, WebImages = () => false, BaseFolder = () => null };

            Assert.Equal(TimeSpan.FromMilliseconds(600), services.Pause);
            Assert.Null(services.Renderer);
        }

        [Fact]
        public void A_line_with_an_image_gets_a_preview_under_it() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("chart.png"), ImageFakes.Png(40, 20));

            f.Show("Intro\n![chart](chart.png \"Sales\")\nafter");

            var waiting = f.PictureUnder(2);
            Assert.True(waiting.IsDrawing);
            Assert.Equal("Loading\u2026", Assert.IsType<TextBlock>(waiting.Child).Text);
            Assert.Null(f.RowUnder(1));
            Assert.Null(f.RowUnder(3));
            Assert.Equal(1, f.Board.Loads);

            f.Settle();

            var picture = f.PictureUnder(2);
            Assert.Equal(40, picture.Image!.Width);
            Assert.Equal(20, picture.Image.Height);
            Assert.Null(picture.ContextMenu);
            Assert.False(picture.View.Menu);
            Assert.Equal("Sales", picture.ToolTip);
            Assert.Equal(1, f.Board.Loads);
            Assert.Empty(f.Warnings);
        });

        [Fact]
        public void Images_on_one_line_sit_side_by_side_with_their_asked_sizes() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("c.jpg"), ImageFakes.Jpeg(40, 20, dpi: 72));

            f.Show("![a](a.png) ![b](a.png =80x) ![c](c.jpg) ![d](a.png =x10)");
            f.Settle();

            var row = f.RowUnder(1)!;
            Assert.Equal(576, row.MaxWidth);   // the 600 px text area less the margin diagrams use
            var sizes = row.Pictures.Select(p => (p.Image!.Width, p.Image.Height)).ToList();
            Assert.Equal(new[] { (40.0, 20.0), (80.0, 40.0), (40.0, 20.0), (20.0, 10.0) }, sizes);   // a 72 dpi JPEG still shows 40 wide
            Assert.All(row.Pictures, p => Assert.Equal(ImageBoard.Gap, p.Margin.Right));
            Assert.Equal(2, f.Board.Loads);   // a.png is read once for its three uses
        });

        [Fact]
        public void A_large_image_fits_the_width_and_is_decoded_at_that_size() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("wide.jpg"), ImageFakes.Jpeg(2000, 100));

            f.Show("![wide](wide.jpg)");
            f.Settle();

            var picture = f.PictureUnder(1);
            Assert.Equal(568, picture.Image!.Width);   // the row's 576 less the gap
            Assert.Equal(28.4, picture.Image.Height, 3);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(picture.Image.Source);
            Assert.Equal((int)Math.Ceiling(568 * picture.View.PixelsPerDip), bitmap.PixelWidth);
        });

        [Fact]
        public void An_image_preview_has_its_asked_size_fitted_to_the_width_and_no_menu() => UiThread.Run(() =>
        {
            var waiting = new DiagramPicture(new DiagramView { Palette = PadPalette.Dark, Menu = false, WaitingText = ImageText.Loading });
            var stretched = new DiagramPicture(new DiagramView
            {
                Result = DiagramResult.Image(DiagramFakes.Png, 1, 1),
                Palette = PadPalette.Dark,
                MaxWidth = 400,
                Width = 200,
                Height = 50,
                Menu = false,
            });
            var tooWide = new DiagramPicture(new DiagramView { Result = DiagramResult.Image(DiagramFakes.Png, 1, 1), Palette = PadPalette.Dark, MaxWidth = 100, Width = 300 });

            Assert.Equal("Loading\u2026", Assert.IsType<TextBlock>(waiting.Child).Text);
            Assert.Equal(200, stretched.Image!.Width);
            Assert.Equal(50, stretched.Image.Height);
            Assert.Equal(Stretch.Fill, stretched.Image.Stretch);
            Assert.Null(stretched.ContextMenu);
            Assert.Equal(100, tooWide.Image!.Width);   // =300x on a square image, fitted to 100
            Assert.Equal(100, tooWide.Image.Height);
            Assert.Equal(Stretch.Uniform, tooWide.Image.Stretch);
            Assert.NotNull(tooWide.ContextMenu);   // Menu is on unless a preview turns it off: diagrams keep theirs
        });

        [Fact]
        public void A_relative_path_in_a_note_and_a_missing_file_say_so() => UiThread.Run(() =>
        {
            using var f = new Fixture { Folder = null };
            string gone = f.Dir.PathOf("gone.png");

            f.Show("![a](pic.png)\n![b](<" + gone + ">)");
            Assert.Equal("A relative path needs a saved file.", f.PictureUnder(1).ErrorText!.Text);
            f.Settle();

            Assert.Equal("Image not found: " + gone, f.PictureUnder(2).ErrorText!.Text);
            Assert.Equal(1, f.Board.Loads);
        });

        [Fact]
        public void A_file_that_is_no_image_cannot_be_read() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllText(f.Dir.PathOf("notes.png"), "hello");

            f.Show("![n](notes.png)");
            f.Settle();

            Assert.Equal("The image could not be read.", f.PictureUnder(1).ErrorText!.Text);
            Assert.Empty(f.Warnings);
        });

        [Fact]
        public void A_web_image_is_not_downloaded_while_web_images_are_off() => UiThread.Run(() =>
        {
            using var f = new Fixture();

            f.Show("![logo](https://example.com/logo.png)");

            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.", f.PictureUnder(1).ErrorText!.Text);
            Assert.Empty(f.Handler.Requests);
            Assert.Equal(0, f.Board.Loads);

            f.Web = true;
            f.Language.RefreshDiagrams();   // Settings → MicaPad → Load images from the web
            f.PumpAndRender();
            f.Settle();

            Assert.Equal(1, f.PictureUnder(1).Image!.Width);
            Assert.Equal("https://example.com/logo.png", Assert.Single(f.Handler.Requests).Uri.AbsoluteUri);

            f.Language.RefreshDiagrams();   // another settings change: a web image that loaded is not fetched again
            f.PumpAndRender();
            f.Settle();
            Assert.Single(f.Handler.Requests);
        });

        [Fact]
        public void An_svg_image_is_drawn_by_the_diagram_page_as_it_is() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            string svg = "<?xml version=\"1.0\"?>\n" + DiagramFakes.Svg;
            File.WriteAllText(f.Dir.PathOf("logo.svg"), "\uFEFF" + svg);

            f.Show("![logo](logo.svg)");
            PumpUntil(() => f.Renderer.Calls.Count == 1, "the SVG to reach the renderer");

            var request = f.Renderer.Calls[0].Request;
            Assert.Same(DiagramKinds.SvgImage, request.Kind);
            Assert.Equal("svg", request.Kind.PageKind);
            Assert.Equal(svg, request.Source);   // without the byte order mark
            Assert.Null(request.KrokiServer);

            f.Renderer.Finish(0, DiagramFakes.Picture(100, 50));
            f.Settle();
            Assert.Equal(100, f.PictureUnder(1).Image!.Width);
        });

        [Fact]
        public void The_renderer_draws_an_svg_image_on_the_page_without_a_card()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var request = new DiagramRequest(DiagramKinds.SvgImage, DiagramFakes.Svg, PadThemes.Dark, "#EDEDF2", "#0E0E13", null);

            var task = renderer.RenderAsync(request, new object());
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");

            Assert.True(task.Result.IsPicture);
            Assert.False(task.Result.Paper);
            Assert.Equal("svg", Assert.Single(page.Requests).Kind);
            Assert.Equal("image/svg", DiagramKinds.SvgImage.EngineId);
            Assert.Equal(request.Key, (request with { Theme = PadThemes.Light }).Key);   // the same picture in both themes
            Assert.NotEqual(request.Key, (request with { Source = "<svg/>" }).Key);
            Assert.DoesNotContain(DiagramKinds.Words, w => ReferenceEquals(DiagramKinds.FromWord(w), DiagramKinds.SvgImage));
        }

        [Fact]
        public void Typing_on_an_image_line_loads_nothing_until_the_pause_and_keeps_the_old_preview() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("b.png"), ImageFakes.Png(80, 20));
            f.Show("![pic](a.png)");
            f.Settle();
            var document = f.Editor.Document;

            document.Replace(document.Text.IndexOf("a.png", StringComparison.Ordinal), 1, "b");   // now b.png
            f.PumpAndRender();

            Assert.Equal(1, f.Board.Loads);                       // nothing while typing
            Assert.Equal(40, f.PictureUnder(1).Image!.Width);      // the old preview stays

            f.Board.DrawDue();                                     // typing paused
            f.PumpAndRender();
            Assert.Equal(2, f.Board.Loads);
            f.Settle();
            Assert.Equal(80, f.PictureUnder(1).Image!.Width);
        });

        [Fact]
        public void Fenced_and_code_images_and_other_languages_get_no_preview() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));

            f.Show("```\n![a](a.png)\n```\n`![b](a.png)` and \\![c](a.png)");

            Assert.Null(f.RowUnder(2));
            Assert.Null(f.RowUnder(4));
            Assert.Equal(0, f.Board.Loads);

            f.Language.Apply(PadLanguages.ById("json")!);
            Assert.Null(f.Language.ImageBoard);
            Assert.Empty(f.View.ElementGenerators.OfType<ImageGenerator>());

            f.Language.Apply(PadLanguages.Markdown);
            Assert.Single(f.View.ElementGenerators.OfType<ImageGenerator>());

            f.Enabled = false;   // Settings → MicaPad → Draw diagrams off
            f.Language.RefreshDiagrams();
            Assert.Null(f.Language.ImageBoard);
            Assert.Empty(f.View.ElementGenerators.OfType<ImageGenerator>());
        });

        [Fact]
        public void A_tab_shown_again_shows_its_images_at_once_and_reads_files_once_more() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            f.Show("![a](a.png)");
            f.Settle();
            var first = f.Editor.Document;
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(60, 20));   // changed while the tab is away

            f.Show("other text");
            f.Editor.Document = first;
            f.Language.Apply(PadLanguages.Markdown);
            f.Render();

            Assert.False(f.PictureUnder(1).IsDrawing);
            Assert.Equal(40, f.PictureUnder(1).Image!.Width);   // at once, from the window's cache
            Assert.Equal(1, f.Board.Loads);                      // and read once more
            f.Settle();
            Assert.Equal(60, f.PictureUnder(1).Image!.Width);
        });
    }
}
