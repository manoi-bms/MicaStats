using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// What drawing a diagram gave: a picture (a PNG at twice its size, the SVG, and its size in
    /// device-independent pixels) or an error message. Lasting results — pictures and syntax
    /// errors — are cached; passing ones (a server out of reach, a timeout) are not.
    /// </summary>
    public sealed class DiagramResult
    {
        private DiagramResult()
        {
        }

        public byte[]? Png { get; private init; }

        public string? Svg { get; private init; }

        /// <summary>The picture's natural width in device-independent pixels.</summary>
        public double Width { get; private init; }

        public double Height { get; private init; }

        public string? Error { get; private init; }

        /// <summary>A page that helps with the error (the WebView2 Runtime download), or null.</summary>
        public Uri? HelpLink { get; private init; }

        /// <summary>True for a Kroki picture: drawn on a light card in both themes.</summary>
        public bool Paper { get; private init; }

        /// <summary>True when the same request would give the same result again.</summary>
        public bool Lasting { get; private init; }

        /// <summary>True when a newer request for the same block took this one's place before it was drawn.</summary>
        public bool IsReplaced { get; private init; }

        public bool IsPicture => Png != null;

        public static DiagramResult Picture(byte[] png, string svg, double width, double height, bool paper) =>
            new() { Png = png, Svg = svg, Width = width, Height = height, Paper = paper, Lasting = true };

        public static DiagramResult Failure(string error, bool lasting, Uri? helpLink = null) =>
            new() { Error = error, Lasting = lasting, HelpLink = helpLink };

        public static DiagramResult Replaced { get; } = new() { IsReplaced = true };
    }

    /// <summary>
    /// One diagram to draw: its kind and source, MicaPad's theme with its text and background
    /// colors as CSS (<c>#RRGGBB</c>), and the Kroki server (null while Kroki is off).
    /// </summary>
    public sealed record DiagramRequest(DiagramKind Kind, string Source, string Theme, string Foreground, string Background, string? KrokiServer)
    {
        /// <summary>True unless the theme is Light.</summary>
        public bool Dark => Theme != PadThemes.Light;

        /// <summary>
        /// The cache key (spec 2.4): engine, Kroki server, theme and source. A Kroki picture is the
        /// same in both themes (it sits on a light card), so its theme part is "paper" (R3).
        /// </summary>
        public string Key => DiagramCacheKey.Of(Kind.EngineId, Kind.NeedsKroki ? KrokiServer : null, Kind.NeedsKroki ? "paper" : Theme, Source);

        /// <summary>A palette color as CSS <c>#RRGGBB</c>; the alpha is dropped.</summary>
        public static string Css(PadColor color) =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
    }

    /// <summary>The words MicaPad shows about diagrams.</summary>
    public static class DiagramText
    {
        public const string Drawing = "Drawing\u2026";
        public const string TooLarge = "Too large to draw";
        public const string TookTooLong = "Took too long to draw.";
        public const string RuntimeMissing = "Diagrams need the Microsoft Edge WebView2 Runtime.";
        public const string EngineStopped = "The diagram engine stopped. It starts again with the next drawing.";
        public const string Failed = "The diagram could not be drawn.";
        public const string CouldNotRead = "The picture could not be read.";

        /// <summary>Where the WebView2 Runtime is downloaded.</summary>
        public static readonly Uri RuntimeDownload = new("https://developer.microsoft.com/microsoft-edge/webview2/");

        /// <summary>"PlantUML needs Kroki — turn it on in Settings → MicaPad."</summary>
        public static string NeedsKroki(DiagramKind kind) => kind.Name + " needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.";

        /// <summary>"The Kroki server could not be reached (kroki.io)."</summary>
        public static string Unreachable(string host) => "The Kroki server could not be reached (" + host + ").";
    }

    /// <summary>The cache key of a drawing: a SHA-256 of its engine, Kroki server, theme and source.</summary>
    public static class DiagramCacheKey
    {
        /// <summary>64 upper-case hex characters.</summary>
        public static string Of(string engine, string? krokiServer, string theme, string source)
        {
            string text = engine + "\0" + (krokiServer ?? "") + "\0" + theme + "\0" + source;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }
}
