using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Finding diagram blocks and their sources (spec section 1).</summary>
    public class DiagramBlocksTests
    {
        private static System.Collections.Generic.IReadOnlyList<DiagramBlock> Find(string text)
        {
            var lines = text.Split('\n');
            return DiagramBlocks.Find(lines, FenceTracker.Classify(lines));
        }

        [Fact]
        public void Openings_pair_each_closing_fence_with_its_opening_line()
        {
            var lines = "a\n```\nx\n```\n~~~\n~~~\n```js".Split('\n');

            var openings = FenceTracker.Openings(FenceTracker.Classify(lines));

            Assert.Equal(new[] { 0, 0, 0, 2, 0, 5, 0 }, openings);
        }

        [Fact]
        public void A_diagram_block_has_its_kind_lines_and_source()
        {
            var blocks = Find("# Notes\n```mermaid\nflowchart TD\n  A --> B\n```\ntext\n~~~dot\ndigraph { a -> b }\n~~~");

            Assert.Equal(2, blocks.Count);
            Assert.Equal("Mermaid", blocks[0].Kind.Name);
            Assert.Equal(2, blocks[0].OpenLine);
            Assert.Equal(5, blocks[0].CloseLine);
            Assert.Equal("flowchart TD\n  A --> B", blocks[0].Source);
            Assert.False(blocks[0].TooLarge);
            Assert.Equal("Graphviz", blocks[1].Kind.Name);
            Assert.Equal("digraph { a -> b }", blocks[1].Source);
        }

        [Fact]
        public void Code_blocks_and_unclosed_blocks_are_not_diagrams()
        {
            Assert.Empty(Find("```js\nlet a;\n```"));
            Assert.Empty(Find("```mermaid\nflowchart TD\n  A --> B"));
        }

        [Fact]
        public void A_blank_block_is_not_drawn() => Assert.Empty(Find("```mermaid\n   \n\n```"));

        [Fact]
        public void A_longer_fence_keeps_shorter_ones_inside_its_source()
        {
            var block = Assert.Single(Find("````markmap\n# Root\n```\ncode\n```\n````"));

            Assert.Equal("# Root\n```\ncode\n```", block.Source);
            Assert.Equal(6, block.CloseLine);
        }

        [Fact]
        public void A_source_over_50000_characters_is_too_large_and_not_read()
        {
            string big = new string('x', DiagramBlocks.MaxSourceLength + 1);

            var block = Assert.Single(Find("```dot\n" + big + "\n```"));

            Assert.True(block.TooLarge);
            Assert.Equal("", block.Source);
            Assert.False(Assert.Single(Find("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength) + "\n```")).TooLarge);
        }

        [Fact]
        public void Read_takes_one_block_from_line_numbers()
        {
            var lines = new[] { "```puml", "@startuml", "a -> b", "@enduml", "```" };

            var block = DiagramBlocks.Read(n => lines[n - 1], 1, 5);

            Assert.NotNull(block);
            Assert.Equal("PlantUML", block!.Kind.Name);
            Assert.Equal("@startuml\na -> b\n@enduml", block.Source);
            Assert.Null(DiagramBlocks.Read(n => lines[n - 1], 2, 5));   // line 2 is no fence
            Assert.Null(DiagramBlocks.Read(n => lines[n - 1], 5, 5));
        }

        [Fact]
        public void An_upper_case_word_with_a_title_still_counts()
        {
            var block = Assert.Single(Find("```MERMAID my flow\nflowchart LR\n  a --> b\n```"));

            Assert.Equal(DiagramEngine.Mermaid, block.Kind.Engine);
        }

        [Fact]
        public void Math_blocks_are_dollar_blocks_and_math_fences()
        {
            var blocks = Find("$$\nx^2\n$$\n```tex\n\\sqrt{2}\n```\n$$ not alone");

            Assert.Equal(2, blocks.Count);
            Assert.Same(DiagramKinds.Math, blocks[0].Kind);
            Assert.Equal("x^2", blocks[0].Source);
            Assert.Equal(1, blocks[0].OpenLine);
            Assert.Equal(3, blocks[0].CloseLine);
            Assert.Same(DiagramKinds.Math, blocks[1].Kind);
            Assert.Equal("\\sqrt{2}", blocks[1].Source);
        }

        [Fact]
        public void The_kroki_form_reads_its_type_line()
        {
            var blocks = Find("```kroki\nd2\na -> b\n```\n```kroki\nbpmnx2\n<x/>\n```\n```kroki\nNot A Type\nx\n```\n```kroki\nplantuml\n```");

            Assert.Equal(2, blocks.Count);
            Assert.Equal("D2", blocks[0].Kind.Name);
            Assert.Equal("a -> b", blocks[0].Source);
            Assert.Equal(1, blocks[0].OpenLine);
            Assert.Equal(4, blocks[0].CloseLine);
            Assert.Equal("bpmnx2", blocks[1].Kind.KrokiType);
            Assert.Equal("<x/>", blocks[1].Source);
        }
    }
}
