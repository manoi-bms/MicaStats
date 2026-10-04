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

        // ---- what Fix with AI works on (MicaPad AI part 2, spec 2.2) --------------------------------

        [Fact]
        public void The_source_lines_of_a_block_are_the_lines_strictly_between_its_fences()
        {
            var lines = "# Notes\n```mermaid\nflowchart TD\n  A --> B\n```\ntext\n$$\nx^2\n$$".Split('\n');

            Assert.Equal((3, 4), DiagramBlocks.SourceLines(lines, 2, 5));
            Assert.Equal((8, 8), DiagramBlocks.SourceLines(lines, 7, 9));   // a $$ block is a pair too
        }

        [Fact]
        public void Lines_that_hold_no_diagram_fence_pair_have_no_source_lines()
        {
            var lines = "```mermaid\nflowchart TD\n```\n```js\nlet a;\n```\n```dot\ndigraph {}".Split('\n');

            Assert.Null(DiagramBlocks.SourceLines(lines, 4, 6));    // ordinary code
            Assert.Null(DiagramBlocks.SourceLines(lines, 2, 3));    // line 2 opens nothing
            Assert.Null(DiagramBlocks.SourceLines(lines, 1, 2));    // line 2 closes nothing
            Assert.Null(DiagramBlocks.SourceLines(lines, 1, 6));    // each a fence, not of one block
            Assert.Null(DiagramBlocks.SourceLines(lines, 3, 4));    // a closing fence and the next opening one
            Assert.Null(DiagramBlocks.SourceLines(lines, 7, 8));    // never closed
            Assert.Null(DiagramBlocks.SourceLines(lines, 0, 3));
            Assert.Null(DiagramBlocks.SourceLines(lines, 1, 99));
            Assert.Null(DiagramBlocks.SourceLines(lines, 3, 1));
            Assert.Equal((2, 2), DiagramBlocks.SourceLines(lines, 1, 3));
        }

        [Fact]
        public void A_block_with_nothing_between_its_fences_has_an_empty_range()
        {
            var range = DiagramBlocks.SourceLines("```mermaid\n```".Split('\n'), 1, 2);

            Assert.NotNull(range);
            Assert.True(range!.Value.Last < range.Value.First);
        }

        [Fact]
        public void The_source_lines_of_a_kroki_block_start_after_its_type_line()
        {
            var lines = "```kroki\nplantuml\n@startuml\na -> b\n@enduml\n```\n```kroki\nNot A Type\nx\n```\n```kroki\nd2\n```".Split('\n');

            Assert.Equal((3, 5), DiagramBlocks.SourceLines(lines, 1, 6));
            Assert.Null(DiagramBlocks.SourceLines(lines, 7, 10));   // the type line names no diagram
            var onlyType = DiagramBlocks.SourceLines(lines, 11, 13);
            Assert.True(onlyType!.Value.Last < onlyType.Value.First);
        }

        [Theory]
        [InlineData("```mermaid", "x", "mermaid")]
        [InlineData("~~~DOT my graph", "x", "DOT")]
        [InlineData("$$", "x^2", "math")]
        [InlineData("  $$  ", "x^2", "math")]
        [InlineData("```latex", "x^2", "latex")]
        [InlineData("```kroki", " plantuml ", "plantuml")]
        [InlineData("```KROKI", "mermaid", "mermaid")]
        [InlineData("```", "x", "")]
        [InlineData("text", "x", "")]
        public void The_word_of_a_block_is_its_fence_word_math_for_dollars_and_the_type_line_of_a_kroki_block(string open, string next, string word)
        {
            var lines = new[] { open, next, "```" };

            Assert.Equal(word, DiagramBlocks.WordOf(n => lines[n - 1], 1));
        }
    }
}
