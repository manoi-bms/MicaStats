using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// What the drawing page is asked: <see cref="Kind"/> is "mermaid", "dot", "markmap" or "svg"
    /// (a picture Kroki drew); the colors are CSS <c>#RRGGBB</c>.
    /// </summary>
    public sealed record PageRequest(string Kind, string Source, bool Dark, string Foreground, string Background);

    /// <summary>The page's answer: the SVG and a PNG with the picture's size in device-independent pixels, or an error.</summary>
    public sealed record PageDrawing(string? Svg, byte[]? Png, double Width, double Height, string? Error);

    /// <summary>The page that draws diagrams (in the app, a hidden WebView2: <c>Pad/DiagramPage</c>).</summary>
    public interface IDiagramPage : IDisposable
    {
        /// <summary>True once the page cannot draw any more (its browser process died).</summary>
        bool IsBroken { get; }

        /// <summary>Draws one diagram; cancelling abandons it.</summary>
        Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel);
    }

    /// <summary>The Microsoft Edge WebView2 Runtime is not installed, so there is no page.</summary>
    public sealed class DiagramRuntimeMissingException : Exception
    {
        public DiagramRuntimeMissingException(Exception? inner = null) : base("The WebView2 Runtime is not installed.", inner)
        {
        }
    }

    /// <summary>Draws diagrams: the renderer, or a test's fake.</summary>
    public interface IDiagramRenderer
    {
        /// <summary>A result already drawn for <paramref name="key"/> (<see cref="DiagramRequest.Key"/>).</summary>
        bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result);

        /// <summary>
        /// Draws <paramref name="request"/>. <paramref name="slot"/> names the block asking: a newer
        /// request with the same slot replaces this one while it still waits (its result is then
        /// <see cref="DiagramResult.Replaced"/>). Never throws.
        /// </summary>
        Task<DiagramResult> RenderAsync(DiagramRequest request, object slot);
    }
}
