using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A drawing page that answers as a test says, or waits until the test calls <see cref="Finish"/>.</summary>
    internal sealed class FakePage : IDiagramPage
    {
        private readonly object _gate = new();
        private readonly List<PageRequest> _requests = new();
        private readonly List<TaskCompletionSource<PageDrawing>> _waiting = new();

        /// <summary>The answer to a request; null (or no function) leaves it waiting.</summary>
        public Func<PageRequest, PageDrawing?>? Answer { get; set; }

        public bool IsBroken { get; set; }

        public bool Disposed { get; private set; }

        /// <summary>Every request so far, oldest first.</summary>
        public IReadOnlyList<PageRequest> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        public Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<PageDrawing>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancel.Register(() => done.TrySetCanceled(cancel));
            lock (_gate)
            {
                _requests.Add(request);
                _waiting.Add(done);
            }
            if (Answer?.Invoke(request) is { } answer) done.TrySetResult(answer);
            return done.Task;
        }

        /// <summary>Answers the oldest request still waiting.</summary>
        public void Finish(PageDrawing drawing)
        {
            TaskCompletionSource<PageDrawing> done;
            lock (_gate) done = _waiting.First(w => !w.Task.IsCompleted);
            done.TrySetResult(drawing);
        }

        public void Dispose() => Disposed = true;
    }

    internal static class DiagramFakes
    {
        /// <summary>A valid 1x1 PNG.</summary>
        public static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        public const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"/>";

        public static PageDrawing Drawn(double width = 100, double height = 50) => new(Svg, Png, width, height, null);

        public static DiagramResult Picture(double width = 100, double height = 50, bool paper = false) =>
            DiagramResult.Picture(Png, Svg, width, height, paper);

        public static DiagramRequest Request(string word = "mermaid", string source = "flowchart LR\n  a --> b",
                                             string theme = PadThemes.Dark, string? server = null) =>
            new(DiagramKinds.FromWord(word)!, source, theme, "#EDEDF2", "#0E0E13", server);

        /// <summary>Waits up to 10 s for a condition another thread makes true.</summary>
        public static void WaitUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>A renderer that records each request and answers when the test says; lasting answers are cached, as the real one does.</summary>
    internal sealed class FakeRenderer : IDiagramRenderer
    {
        public Dictionary<string, DiagramResult> Cache { get; } = new();

        public List<(DiagramRequest Request, object Slot, TaskCompletionSource<DiagramResult> Done)> Calls { get; } = new();

        public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result) => Cache.TryGetValue(key, out result);

        public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
        {
            var done = new TaskCompletionSource<DiagramResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((request, slot, done));
            return done.Task;
        }

        /// <summary>Answers call <paramref name="index"/>; the caller's continuation runs at the next dispatcher pump.</summary>
        public void Finish(int index, DiagramResult result)
        {
            var call = Calls[index];
            if (result.Lasting) Cache[call.Request.Key] = result;
            call.Done.TrySetResult(result);
        }
    }
}
