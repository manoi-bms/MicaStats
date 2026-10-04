using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        public void MathJax_is_vendored_and_loads_before_the_page_script()
        {
            string folder = DiagramPage.ScriptsFolder;
            string html = File.ReadAllText(Path.Combine(folder, "render.html"));
            int config = html.IndexOf("<script src=\"mathjax-config.js\"></script>", StringComparison.Ordinal);
            int mathjax = html.IndexOf("<script src=\"tex-svg-full.js\"></script>", StringComparison.Ordinal);
            int render = html.IndexOf("<script src=\"render.js\"></script>", StringComparison.Ordinal);

            Assert.True(config > 0 && config < mathjax && mathjax < render, "mathjax-config.js, then tex-svg-full.js, then render.js");
            Assert.Equal("a4354ff94fd868aea0cc6eaaa79a57fda0588646fc46ee3700a349ee0a11cbe6",
                         Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "tex-svg-full.js")))).ToLowerInvariant());
            Assert.Contains("enableMenu: false", File.ReadAllText(Path.Combine(folder, "mathjax-config.js")));
            Assert.Contains("MathJax 3.2.2 (Apache-2.0)", File.ReadAllText(Path.Combine(folder, "THIRD-PARTY.txt")));
        }

        [Fact]
        public void Math_and_chemistry_draw_offline_in_the_text_color() => WithPage(async page =>
        {
            var formula = await Draw(page, "math", @"x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}");

            Assert.True(formula.Error == null, formula.Error);
            Assert.Contains("<path", formula.Svg);
            Assert.DoesNotContain("<text", formula.Svg);   // glyph outlines: no font to load
            Assert.Contains("#1B1B1F", formula.Svg, StringComparison.OrdinalIgnoreCase);
            var (width, height) = PngSize(formula.Png!);
            Assert.InRange(width, (int)(formula.Width * 2) - 1, (int)(formula.Width * 2) + 1);
            Assert.InRange(height, (int)(formula.Height * 2) - 1, (int)(formula.Height * 2) + 1);

            var water = await Draw(page, "math", @"\ce{2H2 + O2 -> 2H2O}", dark: true);

            Assert.True(water.Error == null, water.Error);
            Assert.Contains("#EDEDF2", water.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.True(water.Width > water.Height * 4, "a reaction on one line is wide");
            Assert.Equal(0, page.RefusedRequests);   // MathJax asked the network for nothing
        });

        [Fact]
        public void A_math_mistake_comes_back_as_the_mathjax_message() => WithPage(async page =>
        {
            Assert.Equal("Missing close brace", (await Draw(page, "math", @"\frac{1}{")).Error);

            // The page still draws after it.
            Assert.Null((await Draw(page, "math", "a + b")).Error);
        });

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

        /// <summary>
        /// Every Mermaid type the two system prompts name, with a small source shaped like what
        /// models wrote when asked. "graph" is not named by a prompt: it is the older word for a
        /// flowchart, which models still write.
        /// </summary>
        private static readonly (string Type, string Source)[] PromptedMermaidSamples =
        {
            ("pie", "pie showData\n    title Memory by process\n    \"Chrome\" : 4.2\n    \"Visual Studio\" : 2.8\n    \"Teams\" : 1.1\n    \"Other\" : 3.5"),
            ("xychart-beta", "xychart-beta\n    title \"CPU temperature (" + (char)0xB0 + "C), last hour\"\n    x-axis [\"13:00\",\"13:10\",\"13:20\",\"13:30\",\"13:40\",\"13:50\",\"14:00\"]\n    y-axis \"deg C\" 55 --> 85\n    line [61,64,70,77,82,79,78]"),
            ("flowchart", "flowchart TD\n    A[Slow PC] --> B{Disk at 100%?}\n    B -->|Yes| C[Check top processes]\n    B -->|No| D[Check memory]\n    C --> E[End the busy process]\n    D --> E"),
            ("graph", "graph TD\n    A[Open PR] --> B[CI: Unit Tests & Linter]\n    B -- Fail --> A\n    B -- Pass --> C[Reviewer Approval]"),
        };

        [Fact]
        public void Every_mermaid_type_a_prompt_names_draws_a_picture() => WithPage(async page =>
        {
            foreach (var (type, source) in PromptedMermaidSamples)
            {
                var drawing = await Draw(page, "mermaid", source);

                Assert.True(drawing.Error == null, type + ": " + drawing.Error);
                Assert.True(drawing.Width > 0 && drawing.Height > 0, type + " has no size");
                var (width, height) = PngSize(drawing.Png!);
                _output.WriteLine(type + ": " + width + " x " + height + " px");
                Assert.True(width > 0 && height > 0, type + " has an empty picture");
            }
            Assert.Equal(0, page.RefusedRequests);   // drawing asked the network for nothing
        });

        [Fact]
        public void Every_mermaid_type_the_prompts_quote_is_one_the_drawing_test_covers()
        {
            var covered = PromptedMermaidSamples.Select(s => s.Type).ToHashSet(StringComparer.Ordinal);
            var quoted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string prompt in new[] { Kil0bitSystemMonitor.Services.Ai.AiPrompts.System, Kil0bitSystemMonitor.Services.Pad.Ai.PadAiPrompts.System })
                foreach (string line in prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    if (line.Contains("```mermaid", StringComparison.Ordinal))
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line, "\"([a-z][a-z-]*)\""))
                            quoted.Add(m.Groups[1].Value);

            Assert.Contains("xychart-beta", quoted);   // the extraction finds the rule, so an empty set cannot pass
            Assert.Contains("pie", quoted);
            Assert.Contains("flowchart", quoted);
            Assert.All(quoted, t => Assert.Contains(t, covered));
        }

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
        public void A_picture_keeps_no_script_event_handler_or_javascript_link() => WithPage(async page =>
        {
            var drawing = await Draw(page, "svg",
                "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"120\" height=\"40\" onload=\"alert(1)\">"
                + "<script>alert(2)</script>"
                + "<a href=\"javascript:alert(3)\"><rect width=\"120\" height=\"40\" fill=\"#eee\" onclick=\"alert(4)\"/></a>"
                + "<a xlink:href=\" JavaScript:alert(5)\"><text y=\"20\" OnMouseOver=\"alert(6)\">x</text></a>"
                + "<a href=\"https://kroki.io/\"><circle r=\"4\"/></a>"
                + "</svg>");

            Assert.True(drawing.Error == null, drawing.Error);
            Assert.DoesNotContain("script", drawing.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" on", drawing.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alert", drawing.Svg);
            Assert.Contains("<rect", drawing.Svg);
            Assert.Contains("<text", drawing.Svg);
            Assert.Contains("href=\"https://kroki.io/\"", drawing.Svg);
            Assert.Equal((240, 80), PngSize(drawing.Png!));
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

        private static Task<PageDrawing> DrawImage(DiagramPage page, string svg) =>
            page.DrawAsync(new PageRequest("svg", svg, false, "#1B1B1F", "#FBFBFD", Image: true), CancellationToken.None);

        [Fact]
        public void An_svg_image_is_sized_by_its_width_and_height_in_px_before_its_viewbox() => WithPage(async page =>
        {
            const string icon = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 -960 960 960\">"
                                + "<path d=\"M480-80 80-480l400-400 400 400z\"/></svg>";

            var image = await DrawImage(page, icon);
            Assert.Null(image.Error);
            Assert.Equal(24, image.Width, 3);
            Assert.Equal(24, image.Height, 3);
            Assert.Equal((48, 48), PngSize(image.Png!));

            var px = await DrawImage(page, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"30px\" height=\"10px\" viewBox=\"0 0 3 1\"/>");
            Assert.Equal((30.0, 10.0), (px.Width, px.Height));

            // Without both in px (or unitless) an image falls back to its viewBox.
            var percent = await DrawImage(page, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100%\" height=\"24\" viewBox=\"0 0 48 12\"/>");
            Assert.Equal((48.0, 12.0), (percent.Width, percent.Height));

            // A Kroki picture keeps today's size: its viewBox's.
            var kroki = await Draw(page, "svg", icon);
            Assert.Equal((960.0, 960.0), (kroki.Width, kroki.Height));
        });

        [Fact]
        public void An_unknown_tex_package_says_so_and_the_page_still_draws() => WithPage(async page =>
        {
            Assert.Equal("Unknown TeX package or extension.", (await Draw(page, "math", @"\require{nosuchpkg} x + 1")).Error);

            Assert.Null((await Draw(page, "math", "a + b")).Error);
            Assert.Equal(0, page.RefusedRequests);   // MathJax asked the network for nothing
        });

        [Fact]
        public void The_page_cannot_reach_the_network() => WithPage(async page =>
        {
            string answer = await page.EvaluateForTestAsync("fetch('https://example.com/').then(() => 'reached', () => 'refused')");

            Assert.Contains("\"refused\"", answer);
        });
    }
}
