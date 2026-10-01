using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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

        [Theory]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
        [InlineData("  \n<?xml version=\"1.0\"?><!-- x --><svg/>", true)]
        [InlineData("<html><body/></html>", false)]
        [InlineData("GIF89a", false)]
        public void Svg_is_known_by_its_first_bytes(string text, bool svg) =>
            Assert.Equal(svg, ImageSources.LooksLikeSvg(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
