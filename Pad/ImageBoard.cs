using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; this name exists in both.
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>The previews under one line (spec 6.3, R9): one picture per image, side by side while they fit, wrapping otherwise.</summary>
    internal sealed class ImageRow : WrapPanel
    {
        public ImageRow(DocumentLine line, double maxWidth)
        {
            Line = line;
            Orientation = Orientation.Horizontal;
            MaxWidth = maxWidth;
        }

        /// <summary>The document line this row was built for, the one a redraw builds again.</summary>
        internal DocumentLine Line { get; }

        /// <summary>The previews, in the order of the images on the line.</summary>
        internal IReadOnlyList<DiagramPicture> Pictures => Children.OfType<DiagramPicture>().ToList();

        /// <summary>The sources this row shows or waits for; a load that ends redraws only rows holding its source.</summary>
        internal HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        /// <summary>Built while typing, holding a source whose load waits for the pause.</summary>
        internal bool Deferred { get; set; }
    }

    /// <summary>
    /// The image previews of one Markdown document in one editor (spec 6.3): each image's load, kept
    /// by its resolved source; the pause after typing; and redrawing the lines that hold previews
    /// when a load ends or the width changes. Files and downloads are read off the UI thread and
    /// nothing here waits on them. Results go to the window's cache, so a tab shown again shows its
    /// images at once; a file is then read once more, a web or data image is not (R13).
    /// </summary>
    internal sealed class ImageBoard
    {
        /// <summary>The space right of each preview, so previews side by side do not touch.</summary>
        internal const double Gap = 8;

        private const double MinRowWidth = 120;
        private const long MaxPixels = 100_000_000;
        private const double RowMargin = 24;

        private readonly TextEditor _editor;
        private readonly ImageServices _services;
        private readonly Func<PadPalette> _palette;
        private readonly TextDocument _document;
        private readonly DispatcherTimer _pauseTimer;
        private readonly DispatcherTimer _resizeTimer;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<DocumentLine, ImageRow> _rows = new();
        private readonly ConditionalWeakTable<DocumentLine, DiagramResult?[]> _lastShown = new();
        private readonly List<Task> _running = new();
        private bool _attached = true;
        private bool _editing;
        private bool _warned;
        private int _generation;

        public ImageBoard(TextEditor editor, ImageServices services, Func<PadPalette> palette)
        {
            _editor = editor;
            _services = services;
            _palette = palette;
            _document = editor.Document;

            _pauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = services.Pause };
            _pauseTimer.Tick += (s, e) =>
            {
                _pauseTimer.Stop();
                DrawDue();
            };
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = services.ResizePause };
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                RedrawRows(_ => true);
            };

            _document.Changed += OnChanged;
            _editor.TextArea.TextView.SizeChanged += OnViewSizeChanged;
            _editor.TextArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        /// <summary>The document the previews belong to.</summary>
        internal TextDocument Document => _document;

        /// <summary>How many loads this board started; for tests.</summary>
        internal int Loads { get; private set; }

        /// <summary>How many lines this board asked to be built again after a load, the pause or a resize; for tests.</summary>
        internal int RowsRedrawn { get; private set; }

        /// <summary>How many sources this board keeps an entry for; for tests.</summary>
        internal int EntryCount => _entries.Count;

        /// <summary>Ends when every load started so far has ended; for tests.</summary>
        internal Task Loading => Task.WhenAll(_running.ToArray());

        /// <summary>Stops following the document; a load that ends later changes nothing on screen.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _pauseTimer.Stop();
            _resizeTimer.Stop();
            _document.Changed -= OnChanged;
            _editor.TextArea.TextView.SizeChanged -= OnViewSizeChanged;
            _editor.TextArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
            _rows.Clear();
        }

        /// <summary>A setting changed: every preview is asked for again; files are read again, failed downloads tried again (R13).</summary>
        public void Refresh()
        {
            _generation++;
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>Typing paused: the previews in view load their new sources. The pause timer calls it; so do tests.</summary>
        internal void DrawDue()
        {
            _editing = false;
            RedrawRows(row => row.Deferred);
        }

        /// <summary>Logs once per board; a failure here never reaches the editor.</summary>
        internal void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            try
            {
                _services.Warn(message);
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /// <summary>
        /// The row under <paramref name="line"/>: for each image its picture, its error, or
        /// "Loading..." (R15). Starts the loads that are due: not while typing, and not again for a
        /// source this board already loaded.
        /// </summary>
        internal UIElement RowFor(DocumentLine line, IReadOnlyList<ImageRef> images)
        {
            var palette = _palette();
            string? folder = _services.BaseFolder();
            bool web = _services.WebImages();
            var view = _editor.TextArea.TextView;
            double width = ((IScrollInfo)view).ViewportWidth;
            if (!(width > 0)) width = view.ActualWidth;
            double room = Math.Max(MinRowWidth, width - RowMargin);
            double pixelsPerDip = VisualTreeHelper.GetDpi(view).PixelsPerDip;
            var before = _lastShown.TryGetValue(line, out var last) ? last : Array.Empty<DiagramResult?>();
            var shown = new DiagramResult?[images.Count];

            var row = new ImageRow(line, room);
            for (int i = 0; i < images.Count; i++)
            {
                var image = images[i];
                var location = ImageSources.Resolve(image.Source, folder, web);
                var result = location.Error != null ? DiagramResult.Failure(location.Error, lasting: true) : ResultOf(location, row);
                // While a new source waits for the pause or its load, the line's previous preview stays,
                // but only when the line still has as many images: otherwise positions no longer match.
                shown[i] = result ?? (before.Length == images.Count ? before[i] : null);
                var picture = new DiagramPicture(new DiagramView
                {
                    Result = shown[i],
                    Palette = palette,
                    MaxWidth = Math.Max(1, room - Gap),
                    PixelsPerDip = pixelsPerDip,
                    Width = image.Width,
                    Height = image.Height,
                    Menu = false,
                    OpenLink = _services.OpenLink,
                    WaitingText = ImageText.Loading,
                });
                picture.Margin = new Thickness(0, 4, Gap, 8);
                if (!string.IsNullOrEmpty(image.Title)) picture.ToolTip = image.Title;
                row.Children.Add(picture);
            }
            _lastShown.AddOrUpdate(line, shown);
            _rows[line] = row;
            return row;
        }

        /// <summary>
        /// A raster image as a preview result (R11): its pixel size read from its header; "The image
        /// could not be read." when Windows has no codec for it or it is no image.
        /// </summary>
        internal static DiagramResult Decode(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.PixelWidth > 0 && frame.PixelHeight > 0 && (long)frame.PixelWidth * frame.PixelHeight <= MaxPixels) return DiagramResult.Image(bytes, frame.PixelWidth, frame.PixelHeight);
            }
            catch (Exception ex) when (ex is NotSupportedException or FormatException or IOException or ArgumentException
                                           or InvalidOperationException or COMException or OverflowException)
            {
                // No codec, or not an image.
            }
            return DiagramResult.Failure(ImageText.CouldNotRead, lasting: true);
        }

        /// <summary>What this board shows for <paramref name="location"/>; starts its load when one is due.</summary>
        private DiagramResult? ResultOf(ImageLocation location, ImageRow row)
        {
            row.Keys.Add(location.Key);
            if (!_entries.TryGetValue(location.Key, out var entry))
            {
                // An entry is made when a load starts or the cache has the image, not for every
                // half-typed path.
                bool cachedAlready = _services.Cache.TryGet(location.Key, out var hit);
                if (!cachedAlready && _editing)
                {
                    row.Deferred = true;
                    return null;
                }
                entry = new Entry();
                _entries[location.Key] = entry;
                if (cachedAlready) entry.Shown = hit;
            }
            if (entry.Shown == null && _services.Cache.TryGet(location.Key, out var cached))
            {
                entry.Shown = cached;
                if (location.Origin != ImageOrigin.File)
                {
                    entry.Settled = true;
                    entry.SettledGeneration = _generation;
                }
            }
            // A file is read again once per board and after a setting changes; a web or data image that loaded is not.
            bool settled = entry.Settled
                           && (entry.SettledGeneration == _generation || (location.Origin != ImageOrigin.File && entry.Shown is { Lasting: true }));
            if (!settled && !entry.Pending)
            {
                if (_editing) row.Deferred = true;
                else Load(entry, location);
            }
            return entry.Shown;
        }

        private void Load(Entry entry, ImageLocation location)
        {
            entry.Pending = true;
            Loads++;
            var run = new Run();
            var task = LoadAsync(entry, location, run);
            // A load that ended already (a data address) is read by the caller right after this;
            // one that ends later redraws its lines.
            run.Returned = true;
            _running.RemoveAll(t => t.IsCompleted);
            if (!task.IsCompleted) _running.Add(task);
        }

        private async Task LoadAsync(Entry entry, ImageLocation location, Run run)
        {
            int generation = _generation;
            DiagramResult result;
            try
            {
                var loaded = await _services.Sources.LoadAsync(location);
                if (loaded.Bytes == null) result = DiagramResult.Failure(loaded.Error ?? ImageText.CouldNotRead, loaded.Lasting);
                else if (loaded.Svg) result = await DrawSvgAsync(loaded.Bytes, entry);
                else result = Decode(loaded.Bytes);
            }
            catch (Exception ex)
            {
                WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                result = DiagramResult.Failure(ImageText.CouldNotRead, lasting: false);
            }

            entry.Pending = false;
            if (result.IsReplaced) return;
            entry.Shown = result;
            entry.Settled = true;
            entry.SettledGeneration = generation;
            if (result.Lasting) _services.Cache.Add(location.Key, result);
            if (run.Returned && _attached && ReferenceEquals(_editor.Document, _document)) RedrawRows(row => row.Keys.Contains(location.Key));
        }

        /// <summary>An SVG image through the diagram page (R11): turned into a PNG as it is, in both themes.</summary>
        private async Task<DiagramResult> DrawSvgAsync(byte[] bytes, Entry entry)
        {
            if (_services.Renderer is not { } renderer) return DiagramResult.Failure(ImageText.CouldNotRead, lasting: false);
            var palette = _palette();
            string svg = Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);
            var request = new DiagramRequest(DiagramKinds.SvgImage, svg, palette.Name,
                                             DiagramRequest.Css(palette.Text), DiagramRequest.Css(palette.Background), KrokiServer: null);
            var drawn = renderer.TryGetCached(request.Key, out var cached) ? cached : await renderer.RenderAsync(request, entry);
            // The page's own failure names a picture; an image says image. A missing WebView2 keeps its message and link.
            return drawn.IsPicture || drawn.IsReplaced || drawn.HelpLink != null
                ? drawn
                : DiagramResult.Failure(ImageText.CouldNotRead, drawn.Lasting);
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            if (!_attached || !ReferenceEquals(sender, _document)) return;
            _editing = true;
            _pauseTimer.Stop();
            _pauseTimer.Start();
        }

        private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_attached || !e.WidthChanged) return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => RowsInView();

        /// <summary>
        /// Builds again the lines in view whose rows are <paramref name="due"/>, so each fits the width
        /// and shows its latest result. It goes by the line each row was built for, not by the view's
        /// visual lines: those are invalid from the first redraw until the next layout, and loads that
        /// end together run back to back before it.
        /// </summary>
        private void RedrawRows(Func<ImageRow, bool> due)
        {
            if (!_attached) return;
            var view = _editor.TextArea.TextView;
            foreach (var row in RowsInView())
                if (due(row))
                {
                    RowsRedrawn++;
                    view.Redraw(row.Line, DispatcherPriority.Normal);
                }
        }

        /// <summary>
        /// The rows still shown; the rest (their line deleted, scrolled away or built again) are
        /// dropped, so the board holds only the rows in view.
        /// </summary>
        private List<ImageRow> RowsInView()
        {
            var view = _editor.TextArea.TextView;
            var shown = new List<ImageRow>(_rows.Count);
            foreach (var (line, row) in _rows.ToList())
            {
                if (!line.IsDeleted && view.GetVisualLine(line.LineNumber) is { } visual
                    && visual.Elements.OfType<DiagramElement>().Any(e => ReferenceEquals(e.Picture, row))) shown.Add(row);
                else _rows.Remove(line);
            }
            return shown;
        }

        /// <summary>One source's preview in this board: the result shown, a load running, and whether its load ended (in which settings generation).</summary>
        private sealed class Entry
        {
            public DiagramResult? Shown;
            public bool Pending;
            public bool Settled;
            public int SettledGeneration;
        }

        /// <summary>Set once <see cref="Load"/> has returned: a load ending after that redraws its lines.</summary>
        private sealed class Run
        {
            public bool Returned;
        }
    }
}
