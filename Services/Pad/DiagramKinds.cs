using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Who draws a diagram: one of the engines built into MicaPad, or a Kroki server.</summary>
    public enum DiagramEngine
    {
        Mermaid,
        Graphviz,
        Markmap,
        Kroki,

        /// <summary>TeX formulas and <c>\ce{}</c> chemistry, drawn by MathJax on the page (Markdown spec 6.1).</summary>
        Math,

        /// <summary>An SVG image from a note (spec 6.3): the page only turns it into a PNG, as it does Kroki's pictures.</summary>
        Svg,
    }

    /// <summary>One kind of diagram block: its name for messages, its engine and, for Kroki, the type sent.</summary>
    public sealed class DiagramKind
    {
        internal DiagramKind(string name, DiagramEngine engine, string? krokiType)
        {
            Name = name;
            Engine = engine;
            KrokiType = krokiType;
        }

        /// <summary>The name messages use: "Mermaid", "PlantUML".</summary>
        public string Name { get; }

        public DiagramEngine Engine { get; }

        /// <summary>The diagram type in Kroki's URL; null for the built-in engines.</summary>
        public string? KrokiType { get; }

        /// <summary>True when only a Kroki server can draw it.</summary>
        public bool NeedsKroki => Engine == DiagramEngine.Kroki;

        /// <summary>The engine part of a cache key: "mermaid", "graphviz", "markmap", "math" or "kroki/&lt;type&gt;".</summary>
        public string EngineId => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "graphviz",
            DiagramEngine.Markmap => "markmap",
            DiagramEngine.Math => "math",
            DiagramEngine.Svg => "image/svg",
            _ => "kroki/" + KrokiType,
        };

        /// <summary>What the drawing page is asked for: "mermaid", "dot", "markmap", "math", or "svg" for a picture drawn elsewhere (Kroki's).</summary>
        public string PageKind => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "dot",
            DiagramEngine.Markmap => "markmap",
            DiagramEngine.Math => "math",
            _ => "svg",
        };
    }

    /// <summary>
    /// The fence words that make a fenced block a diagram (spec section 1; math and the Wiki.js
    /// kroki form, Markdown spec 6.1 and 6.2), compared without case, and the <c>$$</c> line that
    /// opens a math block. Any other word is ordinary code.
    /// </summary>
    public static class DiagramKinds
    {
        private static readonly Regex KrokiTypeRx = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);

        /// <summary>Math: a <c>$$</c> block, or a fence whose word is math, latex or tex.</summary>
        public static DiagramKind Math { get; } = new("Math", DiagramEngine.Math, null);

        /// <summary>
        /// The Wiki.js kroki form: the block's first inside line names the type. Never drawn as it
        /// is: <see cref="DiagramBlocks.Read"/> replaces it by the kind <see cref="FromKrokiType"/> gives.
        /// </summary>
        public static DiagramKind KrokiForm { get; } = new("Kroki", DiagramEngine.Kroki, null);

        /// <summary>An SVG image (Markdown spec 6.3 and ruling R11). Not a fence word: image previews ask for it.</summary>
        public static DiagramKind SvgImage { get; } = new("SVG image", DiagramEngine.Svg, null);

        // After Math and KrokiForm: static initializers run in order, and Build adds both.
        private static readonly Dictionary<string, DiagramKind> ByWord = Build();

        /// <summary>Every fence word, lower case.</summary>
        public static IEnumerable<string> Words => ByWord.Keys;

        /// <summary>The kind a fence word names, or null for ordinary code.</summary>
        public static DiagramKind? FromWord(string? word) =>
            word != null && ByWord.TryGetValue(word, out var kind) ? kind : null;

        /// <summary>
        /// The kind an opening line names: a line that is only <c>$$</c> opens a math block; a fence
        /// names its kind by the first word of its info string (<c>```mermaid title</c> is Mermaid).
        /// At most three spaces before either; null for anything else.
        /// </summary>
        public static DiagramKind? FromFence(string openingLine) =>
            FenceTracker.IsMathDelimiter(openingLine) ? Math : FromWord(FenceTracker.InfoWord(openingLine));

        /// <summary>
        /// The kind the type line of a kroki block names (Markdown spec 6.2): a MicaPad word gives its
        /// kind, so mermaid and dot are drawn on this PC; any other lower-case word of letters and
        /// digits is sent to Kroki as that type, named by itself; math, kroki and anything else name none.
        /// </summary>
        public static DiagramKind? FromKrokiType(string? line)
        {
            string type = (line ?? "").Trim();
            if (type.Length == 0) return null;
            if (FromWord(type) is { } known)
                return ReferenceEquals(known, KrokiForm) || known.Engine == DiagramEngine.Math ? null : known;
            return KrokiTypeRx.IsMatch(type) ? new DiagramKind(type, DiagramEngine.Kroki, type) : null;
        }

        private static Dictionary<string, DiagramKind> Build()
        {
            var map = new Dictionary<string, DiagramKind>(StringComparer.OrdinalIgnoreCase);
            void Add(DiagramKind kind, params string[] words)
            {
                foreach (string word in words) map.Add(word, kind);
            }

            Add(new DiagramKind("Mermaid", DiagramEngine.Mermaid, null), "mermaid", "mmd");
            Add(new DiagramKind("Graphviz", DiagramEngine.Graphviz, null), "dot", "graphviz", "gv");
            Add(new DiagramKind("Markmap", DiagramEngine.Markmap, null), "markmap");
            Add(Math, "math", "latex", "tex");
            Add(KrokiForm, "kroki");
            Add(Kroki("PlantUML", "plantuml"), "plantuml", "puml");
            Add(Kroki("C4 with PlantUML", "c4plantuml"), "c4plantuml");
            Add(Kroki("D2", "d2"), "d2");
            Add(Kroki("BPMN", "bpmn"), "bpmn");
            Add(Kroki("Excalidraw", "excalidraw"), "excalidraw");
            Add(Kroki("Vega", "vega"), "vega");
            Add(Kroki("Vega-Lite", "vegalite"), "vegalite", "vega-lite");
            Add(Kroki("WaveDrom", "wavedrom"), "wavedrom");
            Add(Kroki("Ditaa", "ditaa"), "ditaa");
            Add(Kroki("Structurizr", "structurizr"), "structurizr");
            Add(Kroki("Nomnoml", "nomnoml"), "nomnoml");
            Add(Kroki("Pikchr", "pikchr"), "pikchr");
            Add(Kroki("Svgbob", "svgbob"), "svgbob");
            Add(Kroki("DBML", "dbml"), "dbml");
            Add(Kroki("ERD", "erd"), "erd");
            Add(Kroki("Bytefield", "bytefield"), "bytefield");
            Add(Kroki("BlockDiag", "blockdiag"), "blockdiag");
            Add(Kroki("SeqDiag", "seqdiag"), "seqdiag");
            Add(Kroki("ActDiag", "actdiag"), "actdiag");
            Add(Kroki("NwDiag", "nwdiag"), "nwdiag");
            Add(Kroki("PacketDiag", "packetdiag"), "packetdiag");
            Add(Kroki("RackDiag", "rackdiag"), "rackdiag");
            Add(Kroki("TikZ", "tikz"), "tikz");
            Add(Kroki("UMLet", "umlet"), "umlet");
            Add(Kroki("Symbolator", "symbolator"), "symbolator");
            Add(Kroki("WireViz", "wireviz"), "wireviz");
            return map;
        }

        private static DiagramKind Kroki(string name, string type) => new(name, DiagramEngine.Kroki, type);
    }
}
