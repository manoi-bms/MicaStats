using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Draws diagrams for every MicaPad window (spec 2.3, 2.4): one draw at a time, a newer request
    /// for the same block replacing a waiting older one, results kept in memory (64, least
    /// recently used out; nothing on disk). Built-in kinds go to the drawing page; Kroki kinds first go
    /// to the Kroki server (when one is set and still in use, and only once the page exists), then to
    /// the page as an SVG. The page is created on the first draw (within the draw limit) and replaced
    /// after it breaks or a draw runs over the limit.
    /// Never throws: every failure is a result, and a warning names only the engine and the
    /// exception type. The queue and cache are locked; the page is created, used and dropped only by
    /// the pump (one draw at a time), and <see cref="Dispose"/> may come from any thread.
    /// </summary>
    public sealed class DiagramRenderer : IDiagramRenderer, IDisposable
    {
        public static readonly TimeSpan DefaultDrawLimit = TimeSpan.FromSeconds(15);

        private readonly Func<Task<IDiagramPage>> _createPage;
        private readonly Action<string> _warn;
        private readonly TimeSpan _drawLimit;
        private readonly KrokiClient? _kroki;
        private readonly Func<string?>? _krokiServerNow;
        private readonly DiagramCache _cache = new();
        private readonly List<Job> _waiting = new();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _shutdown = new();
        private IDiagramPage? _page;
        private bool _pumping;
        private bool _disposed;

        /// <param name="krokiServerNow">
        /// The server Kroki may use right now (null: Kroki is off), read just before a post: a draw
        /// that waited while Kroki was turned off or its server changed is not sent.
        /// </param>
        public DiagramRenderer(Func<Task<IDiagramPage>> createPage, Action<string>? warn = null, TimeSpan? drawLimit = null, KrokiClient? kroki = null,
                               Func<string?>? krokiServerNow = null)
        {
            _createPage = createPage;
            _warn = warn ?? (_ => { });
            _drawLimit = drawLimit ?? DefaultDrawLimit;
            _kroki = kroki;
            _krokiServerNow = krokiServerNow;
        }

        /// <summary>How many results the cache holds; for tests.</summary>
        internal int CachedCount
        {
            get { lock (_gate) return _cache.Count; }
        }

        public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result)
        {
            lock (_gate) return _cache.TryGet(key, out result);
        }

        public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
        {
            Job job;
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(DiagramResult.Failure(DiagramText.Failed, lasting: false));
                int waiting = _waiting.FindIndex(j => ReferenceEquals(j.Slot, slot));
                if (_cache.TryGet(request.Key, out var cached))
                {
                    if (waiting >= 0)
                    {
                        _waiting[waiting].Done.TrySetResult(DiagramResult.Replaced);
                        _waiting.RemoveAt(waiting);
                    }
                    return Task.FromResult(cached);
                }

                job = new Job(request, slot);
                if (waiting >= 0)
                {
                    _waiting[waiting].Done.TrySetResult(DiagramResult.Replaced);
                    _waiting[waiting] = job;
                }
                else
                {
                    _waiting.Add(job);
                }
                if (_pumping) return job.Done.Task;
                _pumping = true;
            }
            _ = PumpAsync();
            return job.Done.Task;
        }

        /// <summary>Answers every draw still waiting, abandons the running one and closes the page.</summary>
        public void Dispose()
        {
            List<Job> left;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                left = new List<Job>(_waiting);
                _waiting.Clear();
            }
            foreach (var job in left) job.Done.TrySetResult(DiagramResult.Failure(DiagramText.Failed, lasting: false));
            _shutdown.Cancel();
            DropPage();
            _kroki?.Dispose();
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                Job job;
                DiagramResult? drawnMeanwhile;
                lock (_gate)
                {
                    if (_waiting.Count == 0 || _disposed)
                    {
                        _pumping = false;
                        return;
                    }
                    job = _waiting[0];
                    _waiting.RemoveAt(0);
                    // Another block with the same text may have been drawn while this one waited.
                    _cache.TryGet(job.Request.Key, out drawnMeanwhile);
                }
                if (drawnMeanwhile != null)
                {
                    job.Done.TrySetResult(drawnMeanwhile);
                    continue;
                }

                DiagramResult result;
                try
                {
                    result = await DrawAsync(job.Request);
                }
                catch (Exception ex)
                {
                    Warn(job.Request.Kind.Name + " drawing failed (" + ex.GetType().Name + ")");
                    DropPage();
                    result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
                }

                lock (_gate)
                {
                    if (_disposed) result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
                    else if (result.Lasting) _cache.Add(job.Request.Key, result);
                }
                job.Done.TrySetResult(result);
            }
        }

        private async Task<DiagramResult> DrawAsync(DiagramRequest request)
        {
            if (!request.Kind.NeedsKroki)
            {
                var page = new PageRequest(request.Kind.PageKind, request.Source, request.Dark, request.Foreground, request.Background);
                return await DrawOnPageAsync(request.Kind, page, paper: false);
            }

            if (request.KrokiServer == null || _kroki == null)
                return DiagramResult.Failure(DiagramText.NeedsKroki(request.Kind), lasting: false);

            // The page first: without it (no WebView2 Runtime) the block's text is sent nowhere.
            var (_, noPage) = await PageAsync();
            if (noPage != null) return noPage;

            if (_krokiServerNow != null)
            {
                string? now = _krokiServerNow();
                if (now == null) return DiagramResult.Failure(DiagramText.NeedsKroki(request.Kind), lasting: false);
                if (now != request.KrokiServer) return DiagramResult.Replaced;   // the board asks again for the new server
            }

            var kroki = await _kroki.DrawAsync(request.KrokiServer, request.Kind.KrokiType!, request.Source, _shutdown.Token);
            if (kroki.Svg == null) return DiagramResult.Failure(kroki.Error ?? DiagramText.Failed, kroki.Lasting);

            // Kroki's colors cannot follow the theme: the picture goes on a light card in both (spec 3).
            var drawn = await DrawOnPageAsync(request.Kind, new PageRequest("svg", kroki.Svg, false, "#000000", "#FFFFFF"), paper: true);
            return drawn.IsPicture ? drawn : DiagramResult.Failure(drawn.Error ?? DiagramText.Failed, lasting: false, drawn.HelpLink);
        }

        private async Task<DiagramResult> DrawOnPageAsync(DiagramKind kind, PageRequest request, bool paper)
        {
            var (page, noPage) = await PageAsync();
            if (page == null) return noPage!;

            PageDrawing drawing;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                limit.CancelAfter(_drawLimit);
                try
                {
                    drawing = await page.DrawAsync(request, limit.Token);
                }
                catch (OperationCanceledException)
                {
                    if (_shutdown.IsCancellationRequested) return DiagramResult.Failure(DiagramText.Failed, lasting: false);
                    Warn(kind.Name + " drawing took longer than "
                          + _drawLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s; the page was closed");
                    DropPage();
                    return DiagramResult.Failure(DiagramText.TookTooLong, lasting: false);
                }
            }

            if (page.IsBroken)
            {
                DropPage();
                return DiagramResult.Failure(DiagramText.EngineStopped, lasting: false);
            }
            if (drawing.Error != null) return DiagramResult.Failure(drawing.Error, lasting: true);
            if (drawing.Png == null || drawing.Svg == null || !(drawing.Width > 0) || !(drawing.Height > 0))
                return DiagramResult.Failure(DiagramText.Failed, lasting: false);
            return DiagramResult.Picture(drawing.Png, drawing.Svg, drawing.Width, drawing.Height, paper);
        }

        /// <summary>
        /// The drawing page, created when there is none (or the last one broke), or why there is
        /// none: the runtime is missing, the page did not start within the draw limit (it is closed
        /// whenever it does arrive), or the renderer was disposed meanwhile.
        /// </summary>
        private async Task<(IDiagramPage? Page, DiagramResult? Failure)> PageAsync()
        {
            if (_page is { IsBroken: true }) DropPage();
            if (_page is { } existing) return (existing, null);
            lock (_gate)
            {
                if (_disposed) return (null, DiagramResult.Failure(DiagramText.Failed, lasting: false));
            }

            IDiagramPage created;
            try
            {
                var creating = _createPage();
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                {
                    if (await Task.WhenAny(creating, Task.Delay(_drawLimit, limit.Token)) != creating)
                    {
                        _ = DisposeWhenCreatedAsync(creating);
                        if (_shutdown.IsCancellationRequested) return (null, DiagramResult.Failure(DiagramText.Failed, lasting: false));
                        Warn("The diagram page took longer than "
                              + _drawLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s to start");
                        return (null, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false));
                    }
                    limit.Cancel();   // stops the timer
                }
                created = await creating;
            }
            catch (DiagramRuntimeMissingException)
            {
                return (null, DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload));
            }

            bool disposed;
            lock (_gate)
            {
                disposed = _disposed;
                if (!disposed) _page = created;
            }
            if (!disposed) return (created, null);

            try
            {
                created.Dispose();
            }
            catch (Exception ex)
            {
                Warn("Closing the diagram page failed (" + ex.GetType().Name + ")");
            }
            return (null, DiagramResult.Failure(DiagramText.Failed, lasting: false));
        }

        /// <summary>Closes a page whose draw stopped waiting for it, as soon as it exists.</summary>
        private static async Task DisposeWhenCreatedAsync(Task<IDiagramPage> creating)
        {
            try
            {
                (await creating).Dispose();
            }
            catch (Exception)
            {
                // It never started, or it could not be closed: nothing more can be done for it.
            }
        }

        private void Warn(string message)
        {
            try
            {
                _warn(message);
            }
            catch
            {
                // a warning that cannot be written must not stop the queue
            }
        }

        private void DropPage()
        {
            IDiagramPage? page;
            lock (_gate)
            {
                page = _page;
                _page = null;
            }
            try
            {
                page?.Dispose();
            }
            catch (Exception ex)
            {
                Warn("Closing the diagram page failed (" + ex.GetType().Name + ")");
            }
        }

        private sealed class Job
        {
            public Job(DiagramRequest request, object slot)
            {
                Request = request;
                Slot = slot;
            }

            public DiagramRequest Request { get; }

            public object Slot { get; }

            public TaskCompletionSource<DiagramResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
