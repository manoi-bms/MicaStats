using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Utils;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The colors of the lines inside a fenced block whose info word names a MicaPad language (spec
    /// 1.3). AvalonEdit's highlighting engine runs over the block from its first inside line with an
    /// empty state, so a comment or string that spans lines is colored as in a whole file. Each
    /// block is highlighted once per document version, line by line as far down as it is shown.
    /// A block over <see cref="MaxBlockLines"/> lines, or one with a line over the inline limit,
    /// gets no colors from there on.
    /// </summary>
    internal sealed class FenceHighlighter
    {
        internal const int MaxBlockLines = 2000;

        private readonly MarkdownDocumentCache _cache;
        private readonly Dictionary<int, Block> _blocks = new();
        private ITextSourceVersion? _version;

        public FenceHighlighter(MarkdownDocumentCache cache) => _cache = cache;

        /// <summary>The highlighting of inside line <paramref name="lineNumber"/>, or null when it gets no colors.</summary>
        public HighlightedLine? HighlightLine(TextDocument document, int lineNumber)
        {
            int open = _cache.BlockOpeningOf(document, lineNumber);
            if (open == 0 || lineNumber - open > MaxBlockLines) return null;

            if (!ReferenceEquals(document.Version, _version))
            {
                _blocks.Clear();
                _version = document.Version;
            }
            if (!_blocks.TryGetValue(open, out var block))
            {
                block = Start(document, open);
                _blocks[open] = block;
            }

            int index = lineNumber - open - 1;
            while (block.Engine != null && block.Lines.Count <= index)
            {
                var line = document.GetLineByNumber(open + 1 + block.Lines.Count);
                if (line.Length > MarkdownLineTokenizer.MaxInlineLength)
                {
                    block.Engine = null;
                    break;
                }
                block.Engine.CurrentSpanStack = block.Stack;
                block.Lines.Add(block.Engine.HighlightLine(document, line));
                block.Stack = block.Engine.CurrentSpanStack;
            }
            return index < block.Lines.Count ? block.Lines[index] : null;
        }

        private static Block Start(TextDocument document, int open)
        {
            string? id = FenceLanguages.IdOfFence(document.GetText(document.GetLineByNumber(open)));
            var language = PadLanguages.ById(id);
            var definition = language == null ? null : PadHighlighting.For(language);
            return new Block(definition == null ? null : new HighlightingEngine(definition.MainRuleSet));
        }

        private sealed class Block
        {
            public Block(HighlightingEngine? engine) => Engine = engine;

            public HighlightingEngine? Engine;

            public ImmutableStack<HighlightingSpan> Stack = ImmutableStack<HighlightingSpan>.Empty;

            public readonly List<HighlightedLine> Lines = new();
        }
    }
}
