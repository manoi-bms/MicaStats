using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The fence words that make a block a diagram (spec section 1).</summary>
    public class DiagramKindsTests
    {
        [Theory]
        [InlineData("mermaid", DiagramEngine.Mermaid, null, "Mermaid")]
        [InlineData("mmd", DiagramEngine.Mermaid, null, "Mermaid")]
        [InlineData("dot", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("graphviz", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("gv", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("markmap", DiagramEngine.Markmap, null, "Markmap")]
        [InlineData("plantuml", DiagramEngine.Kroki, "plantuml", "PlantUML")]
        [InlineData("puml", DiagramEngine.Kroki, "plantuml", "PlantUML")]
        [InlineData("c4plantuml", DiagramEngine.Kroki, "c4plantuml", "C4 with PlantUML")]
        [InlineData("d2", DiagramEngine.Kroki, "d2", "D2")]
        [InlineData("bpmn", DiagramEngine.Kroki, "bpmn", "BPMN")]
        [InlineData("excalidraw", DiagramEngine.Kroki, "excalidraw", "Excalidraw")]
        [InlineData("vega", DiagramEngine.Kroki, "vega", "Vega")]
        [InlineData("vegalite", DiagramEngine.Kroki, "vegalite", "Vega-Lite")]
        [InlineData("vega-lite", DiagramEngine.Kroki, "vegalite", "Vega-Lite")]
        [InlineData("wavedrom", DiagramEngine.Kroki, "wavedrom", "WaveDrom")]
        [InlineData("ditaa", DiagramEngine.Kroki, "ditaa", "Ditaa")]
        [InlineData("structurizr", DiagramEngine.Kroki, "structurizr", "Structurizr")]
        [InlineData("nomnoml", DiagramEngine.Kroki, "nomnoml", "Nomnoml")]
        [InlineData("pikchr", DiagramEngine.Kroki, "pikchr", "Pikchr")]
        [InlineData("svgbob", DiagramEngine.Kroki, "svgbob", "Svgbob")]
        [InlineData("dbml", DiagramEngine.Kroki, "dbml", "DBML")]
        [InlineData("erd", DiagramEngine.Kroki, "erd", "ERD")]
        [InlineData("bytefield", DiagramEngine.Kroki, "bytefield", "Bytefield")]
        [InlineData("blockdiag", DiagramEngine.Kroki, "blockdiag", "BlockDiag")]
        [InlineData("seqdiag", DiagramEngine.Kroki, "seqdiag", "SeqDiag")]
        [InlineData("actdiag", DiagramEngine.Kroki, "actdiag", "ActDiag")]
        [InlineData("nwdiag", DiagramEngine.Kroki, "nwdiag", "NwDiag")]
        [InlineData("packetdiag", DiagramEngine.Kroki, "packetdiag", "PacketDiag")]
        [InlineData("rackdiag", DiagramEngine.Kroki, "rackdiag", "RackDiag")]
        [InlineData("tikz", DiagramEngine.Kroki, "tikz", "TikZ")]
        [InlineData("umlet", DiagramEngine.Kroki, "umlet", "UMLet")]
        [InlineData("symbolator", DiagramEngine.Kroki, "symbolator", "Symbolator")]
        [InlineData("wireviz", DiagramEngine.Kroki, "wireviz", "WireViz")]
        public void Every_word_maps_to_its_engine_type_and_name(string word, DiagramEngine engine, string? krokiType, string name)
        {
            var kind = DiagramKinds.FromWord(word);

            Assert.NotNull(kind);
            Assert.Equal(engine, kind!.Engine);
            Assert.Equal(krokiType, kind.KrokiType);
            Assert.Equal(name, kind.Name);
            Assert.Equal(engine == DiagramEngine.Kroki, kind.NeedsKroki);
            Assert.Same(kind, DiagramKinds.FromWord(word.ToUpperInvariant()));
        }

        [Fact]
        public void There_are_38_words_all_lower_case()
        {
            var words = DiagramKinds.Words.ToList();

            Assert.Equal(38, words.Count);
            Assert.All(words, w => Assert.Equal(w.ToLowerInvariant(), w));
        }

        [Theory]
        [InlineData("js")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("mermaid2")]
        public void Any_other_word_is_code(string? word) => Assert.Null(DiagramKinds.FromWord(word));

        [Fact]
        public void The_engine_id_and_page_kind_follow_the_engine()
        {
            Assert.Equal("mermaid", DiagramKinds.FromWord("mermaid")!.EngineId);
            Assert.Equal("graphviz", DiagramKinds.FromWord("gv")!.EngineId);
            Assert.Equal("markmap", DiagramKinds.FromWord("markmap")!.EngineId);
            Assert.Equal("kroki/plantuml", DiagramKinds.FromWord("puml")!.EngineId);

            Assert.Equal("mermaid", DiagramKinds.FromWord("mmd")!.PageKind);
            Assert.Equal("dot", DiagramKinds.FromWord("graphviz")!.PageKind);
            Assert.Equal("markmap", DiagramKinds.FromWord("markmap")!.PageKind);
            Assert.Equal("svg", DiagramKinds.FromWord("d2")!.PageKind);
        }

        [Theory]
        [InlineData("```mermaid", "Mermaid")]
        [InlineData("~~~ dot", "Graphviz")]
        [InlineData("   ```PlantUML title here", "PlantUML")]
        [InlineData("````d2", "D2")]
        [InlineData("```vega-lite\t{}", "Vega-Lite")]
        public void An_opening_fence_names_its_kind_by_the_first_word(string line, string name) =>
            Assert.Equal(name, DiagramKinds.FromFence(line)!.Name);

        [Theory]
        [InlineData("```")]
        [InlineData("```js")]
        [InlineData("    ```mermaid")]   // four spaces: indented code, not a fence
        [InlineData("mermaid")]
        [InlineData("")]
        public void Other_lines_name_no_kind(string line) => Assert.Null(DiagramKinds.FromFence(line));

        [Theory]
        [InlineData("math")]
        [InlineData("latex")]
        [InlineData("TeX")]
        public void Math_words_name_the_math_engine(string word)
        {
            var kind = DiagramKinds.FromWord(word);

            Assert.Same(DiagramKinds.Math, kind);
            Assert.Equal(DiagramEngine.Math, kind!.Engine);
            Assert.Equal("Math", kind.Name);
            Assert.Equal("math", kind.EngineId);
            Assert.Equal("math", kind.PageKind);
            Assert.False(kind.NeedsKroki);
        }

        [Theory]
        [InlineData("$$")]
        [InlineData("   $$  ")]
        [InlineData("```math")]
        public void A_dollar_line_or_a_math_fence_opens_a_math_block(string line) => Assert.Same(DiagramKinds.Math, DiagramKinds.FromFence(line));

        [Theory]
        [InlineData("$$ x")]
        [InlineData("    $$")]
        [InlineData("$$$")]
        [InlineData("$")]
        public void Other_dollar_lines_open_nothing(string line) => Assert.Null(DiagramKinds.FromFence(line));

        [Theory]
        [InlineData("plantuml", "PlantUML", "plantuml", DiagramEngine.Kroki)]
        [InlineData("  D2 ", "D2", "d2", DiagramEngine.Kroki)]
        [InlineData("vega-lite", "Vega-Lite", "vegalite", DiagramEngine.Kroki)]
        [InlineData("mermaid", "Mermaid", null, DiagramEngine.Mermaid)]
        [InlineData("graphviz", "Graphviz", null, DiagramEngine.Graphviz)]
        [InlineData("svgbob2", "svgbob2", "svgbob2", DiagramEngine.Kroki)]
        public void A_kroki_type_line_names_its_kind(string line, string name, string? type, DiagramEngine engine)
        {
            var kind = DiagramKinds.FromKrokiType(line);

            Assert.NotNull(kind);
            Assert.Equal(name, kind!.Name);
            Assert.Equal(type, kind.KrokiType);
            Assert.Equal(engine, kind.Engine);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Not a type")]
        [InlineData("Foo")]
        [InlineData("my-type")]
        [InlineData("kroki")]
        [InlineData("math")]
        public void Other_kroki_type_lines_name_no_kind(string? line) => Assert.Null(DiagramKinds.FromKrokiType(line));
    }
}
