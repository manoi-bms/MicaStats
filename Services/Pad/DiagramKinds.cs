using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Who draws a diagram: one of the three engines built into MicaPad, or a Kroki server.</summary>
    public enum DiagramEngine
    {
        Mermaid,
        Graphviz,
        Markmap,
        Kroki,
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

        /// <summary>The engine part of a cache key: "mermaid", "graphviz", "markmap" or "kroki/&lt;type&gt;".</summary>
        public string EngineId => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "graphviz",
            DiagramEngine.Markmap => "markmap",
            _ => "kroki/" + KrokiType,
        };

        /// <summary>What the drawing page is asked for: "mermaid", "dot", "markmap", or "svg" for Kroki's picture.</summary>
        public string PageKind => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "dot",
            DiagramEngine.Markmap => "markmap",
            _ => "svg",
        };
    }

    /// <summary>
    /// The fence words that make a fenced block a diagram (spec section 1), compared without case.
    /// Any other word is ordinary code.
    /// </summary>
    public static class DiagramKinds
    {
        private static readonly Dictionary<string, DiagramKind> ByWord = Build();

        /// <summary>Every fence word, lower case.</summary>
        public static IEnumerable<string> Words => ByWord.Keys;

        /// <summary>The kind a fence word names, or null for ordinary code.</summary>
        public static DiagramKind? FromWord(string? word) =>
            word != null && ByWord.TryGetValue(word, out var kind) ? kind : null;

        /// <summary>
        /// The kind an opening fence line names by the first word of its info string
        /// (<c>```mermaid title</c> is Mermaid), or null. At most three spaces before the fence.
        /// </summary>
        public static DiagramKind? FromFence(string openingLine) => FromWord(FenceTracker.InfoWord(openingLine));

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
