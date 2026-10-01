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
using System.Windows.Threading;
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

        /// <summary>
        /// A JPEG stored <paramref name="width"/> x <paramref name="height"/>, its top half red and its
        /// bottom half blue, whose EXIF header says <paramref name="orientation"/> (as a phone writes it).
        /// </summary>
        public static byte[] JpegTurned(int width, int height, ushort orientation)
        {
            var pixels = new byte[width * height * 3];
            for (int i = 0; i < width * height; i++)
            {
                bool top = i / width < height / 2;
                pixels[i * 3] = top ? (byte)0 : (byte)255;       // blue
                pixels[i * 3 + 2] = top ? (byte)255 : (byte)0;   // red
            }
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3), null, metadata, null));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        /// <summary>The bitmap's pixels as 32-bit BGRA, row after row.</summary>
        public static byte[] Bgra(BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return pixels;
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
            /// <param name="resizePause">An hour unless a test says otherwise: the first layout's width change must not redraw rows inside a test.</param>
            public Fixture(TimeSpan? resizePause = null)
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
                        ResizePause = resizePause ?? TimeSpan.FromHours(1),
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

            public double Height { get; set; } = 400;

            public double Width { get; set; } = 600;

            public void Render()
            {
                View.Measure(new Size(Width, Height));
                View.Arrange(new Rect(0, 0, Width, Height));
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
        public async Task The_renderer_draws_an_svg_image_on_the_page_without_a_card()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var request = new DiagramRequest(DiagramKinds.SvgImage, DiagramFakes.Svg, PadThemes.Dark, "#EDEDF2", "#0E0E13", null);

            var result = await renderer.RenderAsync(request, new object()).WaitAsync(TimeSpan.FromSeconds(10));   // TimeoutException: the draw did not finish

            Assert.True(result.IsPicture);
            Assert.False(result.Paper);
            Assert.Equal("svg", Assert.Single(page.Requests).Kind);
            Assert.True(page.Requests[0].Image);   // sized by its width and height in px, as a browser does
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

        private static string DataPng(int width) => "data:image/png;base64," + Convert.ToBase64String(ImageFakes.Png(width, 4));

        /// <summary>A PNG whose header claims <paramref name="width"/> x <paramref name="height"/> (the pixels are not there).</summary>
        private static byte[] PngClaiming(int width, int height)
        {
            var bytes = ImageFakes.Png(1, 1);
            void Put(int at, int value)
            {
                bytes[at] = (byte)(value >> 24);
                bytes[at + 1] = (byte)(value >> 16);
                bytes[at + 2] = (byte)(value >> 8);
                bytes[at + 3] = (byte)value;
            }
            Put(16, width);
            Put(20, height);
            uint crc = 0xFFFFFFFF;   // the IHDR chunk's CRC covers its type and 13 data bytes
            for (int i = 12; i < 29; i++)
            {
                crc ^= bytes[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            Put(29, unchecked((int)~crc));
            return bytes;
        }

        [Fact]
        public void A_finished_load_redraws_only_the_lines_that_show_its_source() => UiThread.Run(() =>
        {
            using var f = new Fixture { Height = 6000 };
            var text = new System.Text.StringBuilder();
            for (int i = 1; i <= 30; i++)
            {
                File.WriteAllBytes(f.Dir.PathOf("p" + i + ".png"), ImageFakes.Png(10 + i, 4));
                text.Append("![](p").Append(i).Append(".png)\n");
            }
            File.WriteAllBytes(f.Dir.PathOf("q.png"), ImageFakes.Png(50, 4));
            text.Append("last");
            f.Show(text.ToString());
            f.Settle();
            Assert.NotNull(f.RowUnder(30));
            int before = f.Board.RowsRedrawn;

            var document = f.Editor.Document;
            document.Replace(document.GetLineByNumber(30).Offset + 4, 3, "q");   // line 30 now shows q.png
            f.PumpAndRender();
            f.Board.DrawDue();
            f.PumpAndRender();
            f.Settle();

            Assert.Equal(50, f.PictureUnder(30).Image!.Width);
            Assert.InRange(f.Board.RowsRedrawn - before, 1, 3);   // its own line, not all thirty
            Assert.Equal(11, f.PictureUnder(1).Image!.Width);
        });

        /// <summary>
        /// Blocks the UI thread while the file reads end on the thread pool, so their completions
        /// wait in the dispatcher's queue and run back to back, before the next layout.
        /// </summary>
        private static void LetTheReadsEndTogether() => Thread.Sleep(300);

        [Fact]
        public void Images_on_several_lines_whose_loads_end_together_all_show() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            void Write(int width)
            {
                for (int i = 1; i <= 4; i++) File.WriteAllBytes(f.Dir.PathOf("p" + i + ".png"), ImageFakes.Png(width * i, 4));
            }
            double?[] Widths() => Enumerable.Range(1, 4).Select(n => f.PictureUnder(n).Image?.Width).ToArray();
            Write(10);
            f.Show("![](p1.png)\n![](p2.png)\n![](p3.png)\n![](p4.png)");
            Assert.All(Enumerable.Range(1, 4), n => Assert.True(f.PictureUnder(n).IsDrawing));

            LetTheReadsEndTogether();
            f.Settle();
            Assert.Equal(new double?[] { 10, 20, 30, 40 }, Widths());

            var first = f.Editor.Document;
            f.Show("other text");
            Write(15);   // changed while the tab is away
            f.Editor.Document = first;
            f.Language.Apply(PadLanguages.Markdown);
            f.Render();
            Assert.Equal(new double?[] { 10, 20, 30, 40 }, Widths());   // at once, from the window's cache

            LetTheReadsEndTogether();
            f.Settle();
            Assert.Equal(new double?[] { 15, 30, 45, 60 }, Widths());   // each file read once more, every line redrawn
        });

        [Fact]
        public void The_pause_loads_a_typed_image_even_right_after_another_line_was_redrawn() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("b.png"), ImageFakes.Png(80, 20));
            f.Show("![a](a.png)\n![b](a.png)");
            f.Settle();
            var document = f.Editor.Document;

            document.Replace(document.GetLineByNumber(2).Offset + 5, 1, "b");   // line 2 now shows b.png
            f.PumpAndRender();
            Assert.Equal(1, f.Board.Loads);

            f.View.Redraw(document.GetLineByNumber(1), DispatcherPriority.Normal);   // as a load ending on line 1 does, just before the pause
            f.Board.DrawDue();
            f.PumpAndRender();

            Assert.Equal(2, f.Board.Loads);
            f.Settle();
            Assert.Equal(80, f.PictureUnder(2).Image!.Width);
        });

        [Fact]
        public void A_width_change_fits_every_row_to_the_new_width_after_its_pause() => UiThread.Run(() =>
        {
            using var f = new Fixture(resizePause: TimeSpan.FromMilliseconds(1));
            File.WriteAllBytes(f.Dir.PathOf("wide.jpg"), ImageFakes.Jpeg(2000, 100));
            f.Show("![a](wide.jpg)\ntext\n![b](wide.jpg)");
            f.Settle();
            PumpUntil(() => f.Board.RowsRedrawn > 0, "the first layout's width change");   // from 0 to 600
            f.PumpAndRender();
            Assert.Equal(568, f.PictureUnder(1).Image!.Width);
            int before = f.Board.RowsRedrawn;

            f.Width = 400;
            f.Render();
            PumpUntil(() => f.Board.RowsRedrawn - before == 2, "the rows to be redrawn after the resize");
            f.PumpAndRender();

            Assert.Equal(368, f.PictureUnder(1).Image!.Width);   // 400 less the margin and the gap
            Assert.Equal(368, f.PictureUnder(3).Image!.Width);
        });

        [Fact]
        public void A_data_image_shows_at_once_without_a_redraw() => UiThread.Run(() =>
        {
            using var f = new Fixture();

            f.Show("![d](" + DataPng(30) + ")");

            Assert.False(f.PictureUnder(1).IsDrawing);
            Assert.Equal(30, f.PictureUnder(1).Image!.Width);
            Assert.Equal(1, f.Board.Loads);
            Assert.Equal(0, f.Board.RowsRedrawn);
        });

        [Fact]
        public void A_load_ending_after_the_board_is_detached_or_the_document_changed_changes_nothing() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            f.Show("![a](a.png)");
            var board = f.Board;
            f.Language.Apply(PadLanguages.ById("json")!);   // detaches the board with its load still running

            UiPump.Wait(board.Loading);
            f.PumpAndRender();
            Assert.Equal(0, board.RowsRedrawn);

            f.Language.Apply(PadLanguages.Markdown);
            f.Render();
            var second = f.Board;
            f.Editor.Document = new TextDocument("other");   // the tab's document changed under the running load
            f.Language.Apply(PadLanguages.Markdown);
            UiPump.Wait(second.Loading);
            f.PumpAndRender();
            Assert.Equal(0, second.RowsRedrawn);
        });

        [Fact]
        public void An_image_claiming_over_100_megapixels_is_refused_before_it_is_decoded()
        {
            var big = ImageBoard.Decode(PngClaiming(20000, 20000));
            var ok = ImageBoard.Decode(PngClaiming(10000, 10000));

            Assert.Equal("The image could not be read.", big.Error);
            Assert.True(ok.IsPicture);
            Assert.Equal(10000, ok.PixelWidth);
        }

        /// <summary>The bitmap's rows, each pixel the letter its blue value numbers (1 is a).</summary>
        private static string[] Letters(BitmapSource bitmap)
        {
            var pixels = ImageFakes.Bgra(bitmap);
            int width = bitmap.PixelWidth;
            return Enumerable.Range(0, bitmap.PixelHeight)
                             .Select(y => new string(Enumerable.Range(0, width).Select(x => (char)('a' - 1 + pixels[(y * width + x) * 4])).ToArray()))
                             .ToArray();
        }

        [Theory]
        [InlineData(0, "abc/def")]   // no orientation, or one out of range: as stored
        [InlineData(1, "abc/def")]
        [InlineData(2, "cba/fed")]   // mirrored left to right
        [InlineData(3, "fed/cba")]   // turned half way
        [InlineData(4, "def/abc")]   // mirrored top to bottom
        [InlineData(5, "ad/be/cf")]  // mirrored, then turned a quarter counterclockwise
        [InlineData(6, "da/eb/fc")]  // turned a quarter clockwise (a phone held upright)
        [InlineData(7, "fc/eb/da")]  // mirrored, then turned a quarter clockwise
        [InlineData(8, "cf/be/ad")]  // turned a quarter counterclockwise
        [InlineData(9, "abc/def")]
        public void A_photo_is_turned_and_mirrored_as_its_exif_orientation_says(int orientation, string upright) => UiThread.Run(() =>
        {
            var stored = new byte[3 * 2 * 4];
            for (int i = 0; i < 6; i++)
            {
                stored[i * 4] = (byte)(i + 1);   // a to f in its blue value
                stored[i * 4 + 3] = 255;
            }
            var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, stored, 3 * 4);

            Assert.Equal(upright, string.Join("/", Letters(DiagramPicture.Upright(bitmap, orientation))));
        });

        [Fact]
        public void A_phone_photo_is_shown_upright_as_its_exif_orientation_says() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            var photo = ImageFakes.JpegTurned(64, 32, orientation: 6);   // stored on its side: 64 wide, 32 high
            File.WriteAllBytes(f.Dir.PathOf("photo.jpg"), photo);

            var result = ImageBoard.Decode(photo);
            Assert.Equal((32.0, 64.0), (result.Width, result.Height));
            Assert.Equal(32, result.PixelWidth);

            f.Show("![photo](photo.jpg)");
            f.Settle();
            var picture = f.PictureUnder(1);
            Assert.Equal((32.0, 64.0), (picture.Image!.Width, picture.Image.Height));
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(picture.Image.Source);
            Assert.Equal((32, 64), (bitmap.PixelWidth, bitmap.PixelHeight));
            var pixels = ImageFakes.Bgra(bitmap);
            byte[] Pixel(int x, int y) => pixels.Skip((y * 32 + x) * 4).Take(3).ToArray();
            // Turned a quarter clockwise, the stored top (red) is on the right and its bottom (blue) on the left.
            Assert.True(Pixel(24, 32)[2] > 200 && Pixel(24, 32)[0] < 60, "the right half is red");
            Assert.True(Pixel(8, 32)[0] > 200 && Pixel(8, 32)[2] < 60, "the left half is blue");

            var plain = ImageBoard.Decode(ImageFakes.Jpeg(64, 32));   // no orientation: as stored
            Assert.Equal((64.0, 32.0), (plain.Width, plain.Height));
        });

        [Fact]
        public void Typing_a_new_image_before_an_old_one_does_not_show_the_old_one_in_its_place() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("b.png"), ImageFakes.Png(80, 20));
            f.Show("![a](a.png)");
            f.Settle();

            f.Editor.Document.Insert(0, "![b](b.png) ");
            f.PumpAndRender();

            var typing = f.RowUnder(1)!.Pictures;
            Assert.True(typing[0].IsDrawing);   // Loading, not a copy of the old picture
            Assert.Equal(40, typing[1].Image!.Width);   // a.png, already loaded
            f.Board.DrawDue();
            f.PumpAndRender();
            f.Settle();
            var widths = f.RowUnder(1)!.Pictures.Select(p => p.Image!.Width).ToList();
            Assert.Equal(new[] { 80.0, 40.0 }, widths);
        });

        [Fact]
        public void Half_typed_paths_make_no_entries_until_the_pause() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            f.Show("![a](a.png)");
            f.Settle();
            Assert.Equal(1, f.Board.EntryCount);

            var document = f.Editor.Document;
            foreach (string typed in new[] { "x", "xy", "xyz" })
            {
                document.Replace(document.Text.IndexOf("](", StringComparison.Ordinal) + 2, document.Text.IndexOf(')') - document.Text.IndexOf("](", StringComparison.Ordinal) - 2, typed);
                f.PumpAndRender();
            }

            Assert.Equal(1, f.Board.EntryCount);
            Assert.Equal(1, f.Board.Loads);
        });

        [Fact]
        public void A_link_a_preview_offers_goes_to_the_windows_link_handler() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            var opened = new List<Uri>();
            f.Language.Images = new ImageServices
            {
                Sources = f.Sources,
                Enabled = () => true,
                WebImages = () => false,
                BaseFolder = () => f.Folder,
                Pause = TimeSpan.FromHours(1),
                OpenLink = opened.Add,
            };
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(4, 4));

            f.Show("![a](a.png)");

            var link = new Uri("https://example.com/webview2");
            f.PictureUnder(1).View.OpenLink!(link);
            Assert.Equal(new[] { link }, opened);
        });
    }
}
