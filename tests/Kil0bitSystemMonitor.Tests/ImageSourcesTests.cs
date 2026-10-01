using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A web server for image tests: answers as the test says (a 1x1 PNG by default) and records each request.</summary>
    internal sealed class FakeImageHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Uri Uri, bool HasBody)> _requests = new();

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Bytes(DiagramFakes.Png));

        public IReadOnlyList<(HttpMethod Method, Uri Uri, bool HasBody)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (_requests) _requests.Add((request.Method, request.RequestUri!, request.Content != null));
            return Respond(request, cancel);
        }
    }

    /// <summary>Finding, resolving and loading images (spec 6.3 and "Testing": ImageSources), with temp files and a fake web.</summary>
    public class ImageSourcesTests
    {
        private static ImageLoad Load(ImageSources sources, ImageLocation location)
        {
            var task = sources.LoadAsync(location);
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the load did not finish");
            return task.Result;
        }

        [Fact]
        public void An_image_has_its_alt_source_title_and_place()
        {
            var image = Assert.Single(ImageSources.Find("See ![a chart](img/chart.png \"Sales\") here"));

            Assert.Equal(4, image.Start);
            Assert.Equal("![a chart](img/chart.png \"Sales\")".Length, image.Length);
            Assert.Equal("a chart", image.Alt);
            Assert.Equal("img/chart.png", image.Source);
            Assert.Equal("Sales", image.Title);
            Assert.Null(image.Width);
            Assert.Null(image.Height);
        }

        [Theory]
        [InlineData("![x](a.png =200x)", 200.0, null)]
        [InlineData("![x](a.png =x120)", null, 120.0)]
        [InlineData("![x](a.png =200x120)", 200.0, 120.0)]
        [InlineData("![x](a.png 'T' =64x48)", 64.0, 48.0)]
        [InlineData("![x](a.png)", null, null)]
        public void The_wikijs_size_is_in_device_independent_pixels(string line, double? width, double? height)
        {
            var image = Assert.Single(ImageSources.Find(line));

            Assert.Equal("a.png", image.Source);
            Assert.Equal(width, image.Width);
            Assert.Equal(height, image.Height);
        }

        [Fact]
        public void Several_images_on_a_line_are_found_in_order()
        {
            var images = ImageSources.Find("![a](1.png) and ![b](2.png)");

            Assert.Equal(new[] { 0, 16 }, new[] { images[0].Start, images[1].Start });
            Assert.Equal(new[] { "1.png", "2.png" }, new[] { images[0].Source, images[1].Source });
        }

        [Fact]
        public void Spaces_go_in_angle_brackets_or_as_percent_twenty_and_parentheses_pair()
        {
            Assert.Equal(@"C:\My Pictures\a.png", Assert.Single(ImageSources.Find(@"![a](<C:\My Pictures\a.png>)")).Source);
            Assert.Equal("my%20pic.png", Assert.Single(ImageSources.Find("![a](my%20pic.png)")).Source);
            Assert.Equal("https://en.wikipedia.org/wiki/File:A_(b).png",
                         Assert.Single(ImageSources.Find("![w](https://en.wikipedia.org/wiki/File:A_(b).png)")).Source);
            Assert.Equal("a.png", Assert.Single(ImageSources.Find("[![a](a.png)](https://example.com)")).Source);
        }

        [Theory]
        [InlineData("[a link](a.png)")]
        [InlineData("\\![not](a.png)")]
        [InlineData("`![code](a.png)`")]
        [InlineData("![no source]()")]
        [InlineData("![open](a.png")]
        [InlineData("![x](a.png =abc)")]
        [InlineData("![x](a.png =0x10)")]
        [InlineData("![x](a.png \"t\" junk)")]
        public void Text_that_is_no_image_gives_none(string line) => Assert.Empty(ImageSources.Find(line));

        [Fact]
        public void Paths_resolve_absolute_relative_and_from_file_addresses()
        {
            using var dir = new PadTempDir();

            var absolute = ImageSources.Resolve(dir.PathOf("a.png"), null, webAllowed: false);
            Assert.Equal(ImageOrigin.File, absolute.Origin);
            Assert.Equal(dir.PathOf("a.png"), absolute.Address);
            Assert.Null(absolute.Error);

            Assert.Equal(dir.PathOf(Path.Combine("img", "b c.png")), ImageSources.Resolve("img/b%20c.png", dir.Root, false).Address);
            Assert.Equal(dir.PathOf("x.png"), ImageSources.Resolve("sub/../x.png", dir.Root, false).Address);
            Assert.Equal(dir.PathOf("a b.png"), ImageSources.Resolve(new Uri(dir.PathOf("a b.png")).AbsoluteUri, null, false).Address);
            Assert.NotEqual(ImageSources.Resolve("a.png", dir.Root, false).Key, ImageSources.Resolve("b.png", dir.Root, false).Key);
        }

        [Fact]
        public void A_relative_path_in_a_note_needs_a_saved_file() =>
            Assert.Equal("A relative path needs a saved file.", ImageSources.Resolve("pic.png", null, false).Error);

        [Theory]
        [InlineData("/uploads/a.png")]
        [InlineData("C:a.png")]
        [InlineData("ftp://host/a.png")]
        [InlineData(@"\\.\PhysicalDrive0")]
        public void Sources_that_are_no_local_file_or_web_address_are_not_found(string source) =>
            Assert.Equal("Image not found: " + source, ImageSources.Resolve(source, @"C:\notes", webAllowed: true).Error);

        [Fact]
        public void Web_images_wait_for_the_setting()
        {
            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.",
                         ImageSources.Resolve("https://example.com/a.png", null, webAllowed: false).Error);

            var on = ImageSources.Resolve("https://example.com/a.png", null, webAllowed: true);
            Assert.Equal(ImageOrigin.Web, on.Origin);
            Assert.Equal("https://example.com/a.png", on.Address);
            Assert.Null(on.Error);
        }

        [Fact]
        public void A_data_address_is_read_from_the_note_itself()
        {
            using var sources = new ImageSources(new FakeImageHandler());
            string uri = "data:image/png;base64," + Convert.ToBase64String(DiagramFakes.Png);

            var location = ImageSources.Resolve(uri, null, false);
            var load = Load(sources, location);

            Assert.Equal(ImageOrigin.Data, location.Origin);
            Assert.Equal(DiagramFakes.Png, load.Bytes);
            Assert.False(load.Svg);
            Assert.True(location.Key.Length < 100);   // keyed by its hash, not its text
            Assert.Equal("The image could not be read.", ImageSources.Resolve("data:text/plain;base64,QQ==", null, false).Error);
            Assert.Equal("The image could not be read.", ImageSources.Resolve("data:image/svg+xml;utf8,<svg/>", null, false).Error);
        }

        [Fact]
        public void A_file_is_read_and_svg_text_is_known_by_its_content()
        {
            using var dir = new PadTempDir();
            File.WriteAllBytes(dir.PathOf("a.png"), DiagramFakes.Png);
            File.WriteAllText(dir.PathOf("b.png"), "\uFEFF<?xml version=\"1.0\"?>\n" + DiagramFakes.Svg);   // an SVG whatever its name
            using var sources = new ImageSources(new FakeImageHandler());

            var png = Load(sources, ImageSources.Resolve("a.png", dir.Root, false));
            var svg = Load(sources, ImageSources.Resolve("b.png", dir.Root, false));
            var missing = Load(sources, ImageSources.Resolve("gone.png", dir.Root, false));

            Assert.Equal(DiagramFakes.Png, png.Bytes);
            Assert.False(png.Svg);
            Assert.True(svg.Svg);
            Assert.Equal("Image not found: gone.png", missing.Error);
            Assert.True(missing.Lasting);
        }

        [Fact]
        public void Files_over_20_MB_and_downloads_over_10_MB_are_refused()
        {
            using var dir = new PadTempDir();
            using (var big = File.Create(dir.PathOf("big.png"))) big.SetLength(ImageSources.MaxFileBytes + 1);
            var handler = new FakeImageHandler { Respond = (_, _) => Task.FromResult(FakeImageHandler.Bytes(new byte[ImageSources.MaxWebBytes + 1])) };
            using var sources = new ImageSources(handler);

            Assert.Equal(20L * 1024 * 1024, ImageSources.MaxFileBytes);
            Assert.Equal(10 * 1024 * 1024, ImageSources.MaxWebBytes);
            Assert.Equal("The image could not be read.", Load(sources, ImageSources.Resolve("big.png", dir.Root, false)).Error);
            Assert.Equal("The image could not be downloaded (example.com).",
                         Load(sources, ImageSources.Resolve("https://example.com/big.png", null, true)).Error);
        }

        [Fact]
        public void A_download_is_one_get_of_that_address()
        {
            var handler = new FakeImageHandler();
            using var sources = new ImageSources(handler);

            var load = Load(sources, ImageSources.Resolve("https://example.com/pics/a.png?x=1", null, true));

            Assert.Equal(DiagramFakes.Png, load.Bytes);
            Assert.True(load.Lasting);
            var sent = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, sent.Method);
            Assert.Equal("https://example.com/pics/a.png?x=1", sent.Uri.AbsoluteUri);
            Assert.False(sent.HasBody);
            Assert.Equal(TimeSpan.FromSeconds(10), ImageSources.DefaultTimeout);
        }

        [Fact]
        public void A_failed_or_slow_download_says_so_and_passes()
        {
            var refusing = new FakeImageHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) };
            using var sources = new ImageSources(refusing);
            var missing = Load(sources, ImageSources.Resolve("https://example.com/a.png", null, true));
            Assert.Equal("The image could not be downloaded (example.com).", missing.Error);
            Assert.False(missing.Lasting);

            var slow = new FakeImageHandler
            {
                Respond = async (_, cancel) =>
                {
                    await Task.Delay(5000, cancel);
                    return FakeImageHandler.Bytes(DiagramFakes.Png);
                },
            };
            using var impatient = new ImageSources(slow, TimeSpan.FromMilliseconds(100));
            Assert.Equal("The image could not be downloaded (example.com).",
                         Load(impatient, ImageSources.Resolve("https://example.com/a.png", null, true)).Error);
        }

        public static IEnumerable<object[]> RemotePaths() => new[]
        {
            @"\\server\share\x.png",
            "//server/share/x.png",
            "//cdn.example.com/img/a.png",
            "%5C%5Cserver%5Cshare%5Cx.png",
            "%2F%2Fserver%2Fshare%2Fx.png",
            "file://server/share/x.png",
            "file:////server/share/x.png",
            @"\\evil.example@SSL@443\dav\a.png",
        }.Select(x => new object[] { x });

        [Theory]
        [MemberData(nameof(RemotePaths))]
        public void A_remote_path_waits_for_the_web_setting_and_nothing_is_read(string source)
        {
            var off = ImageSources.Resolve(source, @"C:\notes", webAllowed: false);
            Assert.Equal(ImageText.WebOff, off.Error);
            using var sources = new ImageSources(new FakeImageHandler());
            var load = Load(sources, off);
            Assert.Equal(ImageText.WebOff, load.Error);
            Assert.Null(load.Bytes);

            var on = ImageSources.Resolve(source, @"C:\notes", webAllowed: true);
            Assert.Null(on.Error);
            Assert.Equal(ImageOrigin.File, on.Origin);
            Assert.StartsWith(@"\\", on.Address);
        }

        [Fact]
        public void A_path_on_the_share_of_the_tabs_own_folder_is_allowed_with_web_images_off()
        {
            var same = ImageSources.Resolve("img/a.png", @"\\Host\Share\notes", webAllowed: false);
            Assert.Null(same.Error);
            Assert.Equal(@"\\Host\Share\notes\img\a.png", same.Address);
            Assert.Null(ImageSources.Resolve(@"\\HOST\share\other\a.png", @"\\host\SHARE\notes", false).Error);
            Assert.Equal(ImageText.WebOff, ImageSources.Resolve(@"\\host\other\a.png", @"\\host\share\notes", false).Error);
            Assert.Equal(ImageText.WebOff, ImageSources.Resolve(@"\\evil\share\a.png", @"\\host\share\notes", false).Error);
        }

        [Theory]
        [InlineData("//./PhysicalDrive0")]
        [InlineData(@"\\./PhysicalDrive0")]
        [InlineData(@"/\.\PhysicalDrive0")]
        [InlineData("//./pipe/x")]
        [InlineData("//?/C:/a.png")]
        [InlineData(@"\??\C:\a.png")]
        [InlineData(@"\??\UNC\server\share\x.png")]
        public void Device_paths_are_never_read(string source)
        {
            foreach (bool web in new[] { false, true })
                Assert.Equal("Image not found: " + source, ImageSources.Resolve(source, @"C:\notes", web).Error);
        }

        [Fact]
        public void A_file_address_for_localhost_is_a_local_path()
        {
            Assert.Equal(@"C:\x.png", ImageSources.Resolve("file://localhost/C:/x.png", null, false).Address);
            Assert.Equal(@"C:\x.png", ImageSources.Resolve("file:///C:/x.png", null, false).Address);
        }

        [Theory]
        [InlineData("file:///uploads/a.png")]
        [InlineData("file://localhost/uploads/a.png")]
        public void A_file_address_without_a_drive_is_not_found(string source)
        {
            foreach (bool web in new[] { false, true })
            {
                var location = ImageSources.Resolve(source, @"C:\notes", web);
                Assert.Equal("Image not found: " + source, location.Error);
                Assert.Equal("", location.Address);
            }
        }

        [Fact]
        public void Hostile_lines_are_searched_in_linear_time()
        {
            foreach (string unit in new[] { "![x](", "![x](<", "![x](a (", "![x](" + new string('(', 40) })
            {
                string line = string.Concat(Enumerable.Repeat(unit, ImageSources.MaxLineLength / unit.Length));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ImageSources.Find(line);
                Assert.True(clock.ElapsedMilliseconds < 500, unit + " took " + clock.ElapsedMilliseconds + " ms");
            }
        }

        [Fact]
        public void Parentheses_pair_only_to_32_levels_and_angle_brackets_stop_at_the_next_one()
        {
            Assert.Empty(ImageSources.Find("![x](" + new string('(', 33) + "a" + new string(')', 33) + ")"));
            Assert.Single(ImageSources.Find("![x](" + new string('(', 5) + "a" + new string(')', 5) + ")"));
            Assert.Equal("b.png", Assert.Single(ImageSources.Find("![x](<a ![y](<b.png>)")).Source);
        }

        /// <summary>A body that cannot seek, so its answer has no Content-Length and is read as a stream.</summary>
        private sealed class StreamedBody : MemoryStream
        {
            public StreamedBody(int length) : base(new byte[length], writable: false)
            {
            }

            public override bool CanSeek => false;
        }

        [Fact]
        public void A_download_streamed_over_10_MB_is_refused_and_the_request_names_MicaPad()
        {
            string? agent = null;
            int length = 11 * 1024 * 1024;
            var handler = new FakeImageHandler
            {
                Respond = (request, _) =>
                {
                    agent = request.Headers.UserAgent.ToString();
                    var body = new StreamContent(new StreamedBody(length));
                    Assert.Null(body.Headers.ContentLength);   // streamed: no length to refuse it by
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
                },
            };
            using var sources = new ImageSources(handler);
            var location = ImageSources.Resolve("https://example.com/a.png", null, true);

            Assert.Equal("The image could not be downloaded (example.com).", Load(sources, location).Error);
            Assert.Equal("MicaPad", agent);

            length = ImageSources.MaxWebBytes;   // exactly 10 MB is allowed
            var load = Load(sources, location);
            Assert.Null(load.Error);
            Assert.Equal(ImageSources.MaxWebBytes, load.Bytes!.Length);
        }

        [Fact]
        public void A_download_starts_off_the_ui_thread() => UiThread.Run(() =>
        {
            int? sentOn = null;
            var handler = new FakeImageHandler
            {
                Respond = (_, _) =>
                {
                    sentOn = Environment.CurrentManagedThreadId;   // proxy detection runs here, before the first await
                    return Task.FromResult(FakeImageHandler.Bytes(DiagramFakes.Png));
                },
            };
            using var sources = new ImageSources(handler);

            var task = sources.LoadAsync(ImageSources.Resolve("https://example.com/a.png", null, true));
            UiPump.Wait(task);

            Assert.NotNull(task.Result.Bytes);
            Assert.NotNull(sentOn);
            Assert.NotEqual(Environment.CurrentManagedThreadId, sentOn);
        });

        [Fact]
        public void The_web_handler_has_no_cookies_five_redirects_and_the_system_proxy()
        {
            using var handler = Assert.IsType<SocketsHttpHandler>(ImageSources.CreateHandler());

            Assert.False(handler.UseCookies);
            Assert.Equal(5, handler.MaxAutomaticRedirections);
            Assert.True(handler.UseProxy);
            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        }

        [Theory]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
        [InlineData("  \n<?xml version=\"1.0\"?><!-- x --><svg/>", true)]
        [InlineData("<html><body/></html>", false)]
        [InlineData("GIF89a", false)]
        public void Svg_is_known_by_its_first_bytes(string text, bool svg) =>
            Assert.Equal(svg, ImageSources.LooksLikeSvg(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
