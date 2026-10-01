using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Xunit.Abstractions;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The real drawing page (spec "Testing": the renderer with a real WebView2), built on the
    /// shared UI thread over a temp user data folder and never shown. Where the WebView2 Runtime
    /// is not installed, the page tests write that they were skipped and pass.
    /// </summary>
    public class DiagramPageTests
    {
        private readonly ITestOutputHelper _output;

        public DiagramPageTests(ITestOutputHelper output) => _output = output;

        private static bool RuntimeInstalled()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }

        private void WithPage(Func<DiagramPage, Task> test)
        {
            if (!RuntimeInstalled())
            {
                _output.WriteLine("Skipped: the Microsoft Edge WebView2 Runtime is not installed.");
                return;
            }
            using var dir = new PadTempDir();
            UiThread.Run(() =>
            {
                var created = DiagramPage.CreateAsync(dir.Root, DiagramPage.ScriptsFolder);
                UiPump.Wait(created, 30_000);
                var page = (DiagramPage)created.Result;
                try
                {
                    UiPump.Wait(test(page), 60_000);
                }
                finally
                {
                    page.Dispose();
                }
            });
        }

        private static Task<PageDrawing> Draw(DiagramPage page, string kind, string source, bool dark = false) =>
            page.DrawAsync(new PageRequest(kind, source, dark, dark ? "#EDEDF2" : "#1B1B1F", dark ? "#0E0E13" : "#FBFBFD"), CancellationToken.None);

        /// <summary>A PNG's width and height from its header.</summary>
        private static (int Width, int Height) PngSize(byte[] png)
        {
            Assert.True(png.Length > 24 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "not a PNG");
            int Read(int at) => png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
            return (Read(16), Read(20));
        }

        [Fact]
        public void The_scripts_are_copied_beside_the_app()
        {
            string folder = DiagramPage.ScriptsFolder;
            string html = File.ReadAllText(Path.Combine(folder, "render.html"));

            foreach (string name in new[] { "frames.js", "mermaid.min.js", "viz-global.js", "d3.min.js", "markmap-view.js", "markmap-lib.js", "render.js" })
            {
                Assert.True(File.Exists(Path.Combine(folder, name)), name + " is missing");
                Assert.Contains("<script src=\"" + name + "\"></script>", html);
            }
            Assert.Contains("@viz-js/viz", File.ReadAllText(Path.Combine(folder, "THIRD-PARTY.txt")));
        }

        [Fact]
        public void Every_built_in_kind_draws_a_png_and_an_svg() => WithPage(async page =>
        {
            var samples = new (string Kind, string Source)[]
            {
                ("mermaid", "flowchart TD\n  A[Start] --> B{Ok?}\n  B -->|Yes| C[Done \u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35]"),
                ("mermaid", "sequenceDiagram\n  Alice->>Bob: Hello\n  Bob-->>Alice: Hi"),
                ("mermaid", "mindmap\n  root((MicaPad))\n    Diagrams\n      Mermaid\n      Graphviz\n    Notes"),
                ("dot", "digraph { rankdir=LR; a -> b -> c }"),
                ("markmap", "# MicaPad\n## Diagrams\n- Mermaid\n- Graphviz\n## Notes"),
            };
            foreach (var (kind, source) in samples)
            {
                var drawing = await Draw(page, kind, source);

                Assert.True(drawing.Error == null, kind + ": " + drawing.Error);
                Assert.Contains("<svg", drawing.Svg);
                Assert.True(drawing.Width > 0 && drawing.Height > 0, kind + " has no size");
                var (width, height) = PngSize(drawing.Png!);
                Assert.InRange(width, (int)(drawing.Width * 2) - 1, (int)(drawing.Width * 2) + 1);
                Assert.InRange(height, (int)(drawing.Height * 2) - 1, (int)(drawing.Height * 2) + 1);
            }
        });

        [Fact]
        public void Syntax_errors_come_back_as_messages() => WithPage(async page =>
        {
            Assert.Contains("Parse error", (await Draw(page, "mermaid", "flowchart TD\n  A -->")).Error);
            Assert.Contains("syntax error", (await Draw(page, "dot", "digraph { a -> }")).Error);
            Assert.Equal(DiagramText.CouldNotRead, (await Draw(page, "svg", "<html>nope</html>")).Error);

            // The page still draws after errors.
            Assert.Null((await Draw(page, "dot", "digraph { a -> b }")).Error);
        });

        [Fact]
        public void A_kroki_svg_in_points_gets_its_size_in_pixels() => WithPage(async page =>
        {
            var drawing = await Draw(page, "svg",
                "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"90pt\" height=\"30pt\"><rect width=\"120\" height=\"40\" fill=\"#eee\"/></svg>");

            Assert.Null(drawing.Error);
            Assert.Equal(120, drawing.Width, 3);
            Assert.Equal(40, drawing.Height, 3);
        });

        [Fact]
        public void A_huge_picture_is_scaled_to_4096_pixels() => WithPage(async page =>
        {
            string chain = string.Join(" -> ", Enumerable.Range(1, 120).Select(i => "n" + i));

            var drawing = await Draw(page, "dot", "digraph { rankdir=LR; " + chain + " }");

            Assert.Null(drawing.Error);
            Assert.True(drawing.Width * 2 > 4096);
            Assert.Equal(4096, PngSize(drawing.Png!).Width);
        });

        [Fact]
        public void Graphviz_follows_the_theme_colors() => WithPage(async page =>
        {
            var light = await Draw(page, "dot", "digraph { a -> b }", dark: false);
            var dark = await Draw(page, "dot", "digraph { a -> b }", dark: true);

            Assert.Contains("#1b1b1f", light.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("#ededf2", dark.Svg, StringComparison.OrdinalIgnoreCase);
        });

        [Fact]
        public void The_page_cannot_reach_the_network() => WithPage(async page =>
        {
            string answer = await page.EvaluateForTestAsync("fetch('https://example.com/').then(() => 'reached', () => 'refused')");

            Assert.Contains("\"refused\"", answer);
        });
    }
}
