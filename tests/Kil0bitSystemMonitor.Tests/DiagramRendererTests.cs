using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The renderer's queue, cache and failures (spec 2.3, 2.4, "Error handling"), with a fake page.</summary>
    public class DiagramRendererTests
    {
        private static DiagramResult Wait(Task<DiagramResult> task)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");
            return task.Result;
        }

        private static DiagramRenderer Over(FakePage page, Action<string>? warn = null) =>
            new(() => Task.FromResult<IDiagramPage>(page), warn);

        [Fact]
        public void A_built_in_kind_is_drawn_on_the_page_and_cached()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn(120, 60) };
            int created = 0;
            using var renderer = new DiagramRenderer(() =>
            {
                created++;
                return Task.FromResult<IDiagramPage>(page);
            });
            var request = DiagramFakes.Request("graphviz", "digraph { a -> b }", PadThemes.Light);

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.True(result.IsPicture);
            Assert.Equal(120, result.Width);
            Assert.Equal(60, result.Height);
            Assert.False(result.Paper);
            Assert.Equal(new PageRequest("dot", "digraph { a -> b }", false, "#EDEDF2", "#0E0E13"), Assert.Single(page.Requests));
            Assert.True(renderer.TryGetCached(request.Key, out var cached));
            Assert.Same(result, cached);

            var again = renderer.RenderAsync(request, new object());
            Assert.True(again.IsCompleted);
            Assert.Same(result, again.Result);
            Assert.Single(page.Requests);
            Assert.Equal(1, created);
        }

        [Fact]
        public void One_draw_runs_at_a_time()
        {
            var page = new FakePage();
            using var renderer = Over(page);
            var first = renderer.RenderAsync(DiagramFakes.Request(source: "flowchart LR\n  a"), new object());
            var second = renderer.RenderAsync(DiagramFakes.Request(source: "flowchart LR\n  b"), new object());

            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the first draw");
            Thread.Sleep(50);
            Assert.Single(page.Requests);   // the second waits its turn

            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(first).IsPicture);
            DiagramFakes.WaitUntil(() => page.Requests.Count == 2, "the second draw");
            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(second).IsPicture);
        }

        [Fact]
        public void A_newer_request_for_the_same_block_replaces_the_waiting_one()
        {
            var page = new FakePage();
            using var renderer = Over(page);
            var block = new object();
            var running = renderer.RenderAsync(DiagramFakes.Request(source: "a"), new object());
            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the first draw");

            var older = renderer.RenderAsync(DiagramFakes.Request(source: "b"), block);
            var newer = renderer.RenderAsync(DiagramFakes.Request(source: "c"), block);

            Assert.True(Wait(older).IsReplaced);
            page.Finish(DiagramFakes.Drawn());
            Wait(running);
            DiagramFakes.WaitUntil(() => page.Requests.Count == 2, "the newer draw");
            Assert.Equal("c", page.Requests[1].Source);
            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(newer).IsPicture);
        }

        [Fact]
        public void A_syntax_error_is_a_lasting_result_and_is_cached()
        {
            var page = new FakePage { Answer = _ => new PageDrawing(null, null, 0, 0, "Parse error on line 1") };
            using var renderer = Over(page);
            var bad = DiagramFakes.Request(source: "bad");

            var result = Wait(renderer.RenderAsync(bad, new object()));

            Assert.False(result.IsPicture);
            Assert.Equal("Parse error on line 1", result.Error);
            Assert.True(result.Lasting);
            Assert.True(renderer.TryGetCached(bad.Key, out _));
        }

        [Fact]
        public void A_draw_over_the_limit_is_abandoned_and_the_next_one_gets_a_new_page()
        {
            var pages = new List<FakePage>();
            using var renderer = new DiagramRenderer(() =>
            {
                var page = new FakePage();
                lock (pages) pages.Add(page);
                return Task.FromResult<IDiagramPage>(page);
            }, drawLimit: TimeSpan.FromMilliseconds(100));
            var request = DiagramFakes.Request();

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("Took too long to draw.", result.Error);
            Assert.False(result.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            lock (pages) Assert.True(pages[0].Disposed);

            var next = renderer.RenderAsync(request, new object());
            DiagramFakes.WaitUntil(() =>
            {
                lock (pages) return pages.Count == 2 && pages[1].Requests.Count == 1;
            }, "a new page");
            lock (pages) pages[1].Finish(DiagramFakes.Drawn());
            Assert.True(Wait(next).IsPicture);
        }

        [Fact]
        public void A_broken_page_is_replaced_and_its_error_is_not_cached()
        {
            int made = 0;
            var first = new FakePage();
            first.Answer = _ =>
            {
                first.IsBroken = true;
                return new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped);
            };
            var second = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(++made == 1 ? first : second));
            var request = DiagramFakes.Request();

            var failed = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal(DiagramText.EngineStopped, failed.Error);
            Assert.False(failed.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            Assert.True(first.Disposed);

            Assert.True(Wait(renderer.RenderAsync(request, new object())).IsPicture);
            Assert.Equal(2, made);
        }

        [Fact]
        public void A_missing_runtime_says_so_with_a_link_and_is_tried_again_next_time()
        {
            int tries = 0;
            using var renderer = new DiagramRenderer(() =>
            {
                tries++;
                throw new DiagramRuntimeMissingException();
            });
            var request = DiagramFakes.Request();

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("Diagrams need the Microsoft Edge WebView2 Runtime.", result.Error);
            Assert.Equal(DiagramText.RuntimeDownload, result.HelpLink);
            Assert.False(result.Lasting);
            Wait(renderer.RenderAsync(request, new object()));
            Assert.Equal(2, tries);
        }

        [Fact]
        public void A_failure_is_a_result_and_its_warning_names_only_the_engine_and_the_exception()
        {
            var warnings = new List<string>();
            var page = new FakePage { Answer = _ => throw new InvalidOperationException("secret diagram text") };
            using var renderer = Over(page, warnings.Add);

            var result = Wait(renderer.RenderAsync(DiagramFakes.Request(source: "secret diagram text"), new object()));

            Assert.Equal(DiagramText.Failed, result.Error);
            Assert.False(result.Lasting);
            Assert.Equal("Mermaid drawing failed (InvalidOperationException)", Assert.Single(warnings));
        }

        [Fact]
        public void Kroki_kinds_without_a_server_say_they_need_Kroki()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = Over(page);

            var result = Wait(renderer.RenderAsync(DiagramFakes.Request("plantuml", "a -> b"), new object()));

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", result.Error);
            Assert.Empty(page.Requests);
        }

        [Fact]
        public void Dispose_answers_every_draw_and_closes_the_page()
        {
            var page = new FakePage();
            var renderer = Over(page);
            var running = renderer.RenderAsync(DiagramFakes.Request(source: "a"), new object());
            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the draw");
            var waiting = renderer.RenderAsync(DiagramFakes.Request(source: "b"), new object());

            renderer.Dispose();

            Assert.Equal(DiagramText.Failed, Wait(waiting).Error);
            Assert.Equal(DiagramText.Failed, Wait(running).Error);
            Assert.True(page.Disposed);
            Assert.Equal(DiagramText.Failed, Wait(renderer.RenderAsync(DiagramFakes.Request(source: "c"), new object())).Error);
        }

        [Fact]
        public void The_cache_keeps_64_and_drops_the_least_recently_used()
        {
            var cache = new DiagramCache();
            for (int i = 0; i < 64; i++) cache.Add("k" + i, DiagramFakes.Picture());
            Assert.True(cache.TryGet("k0", out _));   // k0 is now the most recently used

            cache.Add("k64", DiagramFakes.Picture());

            Assert.Equal(64, cache.Count);
            Assert.True(cache.TryGet("k0", out _));
            Assert.False(cache.TryGet("k1", out _));
            Assert.True(cache.TryGet("k64", out _));
        }

        [Fact]
        public void The_key_changes_with_engine_server_theme_and_source()
        {
            string key = DiagramCacheKey.Of("mermaid", null, "Dark", "a");

            Assert.Equal(64, key.Length);
            Assert.Equal(key, DiagramCacheKey.Of("mermaid", null, "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("graphviz", null, "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", "https://kroki.io", "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", null, "Light", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", null, "Dark", "b"));
        }

        [Fact]
        public void A_kroki_picture_has_one_key_for_both_themes_and_one_per_server()
        {
            var dark = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Dark, "https://kroki.io");
            var light = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Light, "https://kroki.io");
            var other = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Dark, "http://localhost:8000");

            Assert.Equal(dark.Key, light.Key);
            Assert.NotEqual(dark.Key, other.Key);
            Assert.NotEqual(DiagramFakes.Request(theme: PadThemes.Dark).Key, DiagramFakes.Request(theme: PadThemes.Light).Key);
            Assert.True(dark.Dark);
            Assert.False(light.Dark);
        }

        [Fact]
        public void Css_colors_drop_the_alpha() => Assert.Equal("#0E0E13", DiagramRequest.Css(PadColor.Parse("#FF0E0E13")));
    }
}
