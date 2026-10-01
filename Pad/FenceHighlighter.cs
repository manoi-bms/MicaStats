using System;
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
    /// A block with more than <see cref="MaxBlockLines"/> lines inside gets no colors at all; one with a
    /// line over the inline limit gets none from that line on; one that throws gets none until the next edit.
    /// </summary>
    internal sealed class FenceHighlighter
    {
        internal const int MaxBlockLines = 2000;

        private readonly MarkdownDocumentCache _cache;
        private readonly Dictionary<int, Block> _blocks = new();
        private ITextSourceVersion? _version;

        private readonly Func<string?, IHighlightingDefinition?> _definitionFor;
        private readonly Action<Exception>? _onFailure;

        /// <param name="definitionFor">The highlighting of a fence's language id (a test seam); null uses MicaPad's languages.</param>
        /// <param name="onFailure">Told when highlighting a block throws; that block gets no colors until the next document version.</param>
        public FenceHighlighter(MarkdownDocumentCache cache, Func<string?, IHighlightingDefinition?>? definitionFor = null, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _definitionFor = definitionFor ?? DefaultDefinition;
            _onFailure = onFailure;
        }

        private static IHighlightingDefinition? DefaultDefinition(string? id)
        {
            var language = PadLanguages.ById(id);
            return language == null ? null : PadHighlighting.For(language);
        }

        /// <summary>The highlighting of inside line <paramref name="lineNumber"/>, or null when it gets no colors.</summary>
        public HighlightedLine? HighlightLine(TextDocument document, int lineNumber)
        {
            int open = _cache.BlockOpeningOf(document, lineNumber);
            if (open == 0) return null;
            int closing = _cache.ClosingLineOf(document, open);
            int last = closing > 0 ? closing - 1 : document.LineCount;
            if (last - open > MaxBlockLines) return null;

            if (!ReferenceEquals(document.Version, _version))
            {
                _blocks.Clear();
                _version = document.Version;
            }
            if (!_blocks.TryGetValue(open, out var block))
            {
                try
                {
                    block = Start(document, open);
                }
                catch (Exception ex)
                {
                    block = new Block(null);
                    Failed(ex);
                }
                _blocks[open] = block;
            }

            int index = lineNumber - open - 1;
            try
            {
                Fill(document, open, block, index);
            }
            catch (Exception ex)
            {
                block.Engine = null;
                block.Lines.Clear();
                Failed(ex);
                return null;
            }
            return index < block.Lines.Count ? block.Lines[index] : null;
        }

        private void Failed(Exception ex)
        {
            try
            {
                _onFailure?.Invoke(ex);
            }
            catch (Exception)
            {
                // Reporting is best effort.
            }
        }

        private static void Fill(TextDocument document, int open, Block block, int index)
        {
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
        }

        private Block Start(TextDocument document, int open)
        {
            string? id = FenceLanguages.IdOfFence(document.GetText(document.GetLineByNumber(open)));
            var definition = _definitionFor(id);
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
