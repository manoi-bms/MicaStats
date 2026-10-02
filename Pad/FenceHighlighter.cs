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
    /// empty state, so a comment or string that spans lines is colored as in a whole file. Each line
    /// is highlighted once, as far down as the block is shown; an edit keeps the lines above it and
    /// highlights again from the edited line on. In a language that calls functions as <c>name(...)</c>,
    /// the names its colors leave plain get the Function color (<see cref="FunctionCallHighlighter.AddTo"/>).
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
        /// <param name="onFailure">Told when highlighting a block throws; that block gets no colors until the next edit.</param>
        public FenceHighlighter(MarkdownDocumentCache cache, Func<string?, IHighlightingDefinition?>? definitionFor = null, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _definitionFor = definitionFor ?? DefaultDefinition;
            _onFailure = onFailure;
        }

        private static IHighlightingDefinition? DefaultDefinition(string? id)
        {
            var language = PadLanguages.ForFence(id);
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

            Follow(document);
            if (!_blocks.TryGetValue(open, out var block))
            {
                try
                {
                    block = Start(document, open);
                }
                catch (Exception ex)
                {
                    block = new Block(null) { Failed = true };
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
                block.Failed = true;
                block.KeepLines(0);
                Failed(ex);
                return null;
            }
            return index < block.Lines.Count ? block.Lines[index] : null;
        }

        /// <summary>
        /// After an edit of lines <paramref name="first"/> to <paramref name="last"/>, the lines whose
        /// colors may have changed beyond the edited ones (AvalonEdit repaints only those): from the
        /// first edited line to the closing line of the block the edit is in, or the document end when
        /// it never closes, if the edit changed that block's opening line or its language has colors.
        /// Null when no other line's colors can have changed.
        /// </summary>
        public (int First, int Last)? LinesToRepaint(TextDocument document, int first, int last)
        {
            int end = BlockEnd(document, first, first, last);
            // Edits of one update group can be lines apart (Replace All): everything between them too.
            if (first < last) end = Math.Max(end, Math.Max(BlockEnd(document, last, first, last), last));
            return end > first ? (first, end) : null;
        }

        /// <summary>The last line of the block holding or opened by line <paramref name="number"/> when an edit there can recolor it; else 0.</summary>
        private int BlockEnd(TextDocument document, int number, int first, int last)
        {
            bool opens = _cache.KindOf(document, number) == MdFence.Delimiter && _cache.OpeningLineOf(document, number) == 0;
            int open = opens ? number : _cache.BlockOpeningOf(document, number);
            if (open == 0) return 0;
            bool openerEdited = open >= first && open <= last;
            if (!openerEdited && _definitionFor(FenceLanguages.IdOfFence(document.GetText(document.GetLineByNumber(open)))) == null) return 0;
            int closing = _cache.ClosingLineOf(document, open);
            return closing > 0 ? closing : document.LineCount;
        }

        /// <summary>
        /// Keeps what the edits since the last call left valid: the blocks above the first changed
        /// line, and in the block holding it the lines above it. A block that failed is tried again.
        /// </summary>
        private void Follow(TextDocument document)
        {
            var version = document.Version;
            if (ReferenceEquals(version, _version)) return;
            var previous = _version;
            _version = version;
            if (previous == null || version == null || !previous.BelongsToSameDocumentAs(version))
            {
                _blocks.Clear();
                return;
            }

            int from = int.MaxValue;
            foreach (var change in previous.GetChangesTo(version)) from = Math.Min(from, change.Offset);
            if (from == int.MaxValue) return;
            int changed = document.GetLineByOffset(Math.Min(from, document.TextLength)).LineNumber;

            List<int>? gone = null;
            foreach (var (open, block) in _blocks)
            {
                if (open >= changed || block.Failed) (gone ??= new List<int>()).Add(open);
                else block.KeepLines(changed - open - 1);
            }
            if (gone != null)
                foreach (int open in gone) _blocks.Remove(open);
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

        private void Fill(TextDocument document, int open, Block block, int index)
        {
            var engine = block.Engine;
            while (engine != null && !block.Stopped && block.Lines.Count <= index)
            {
                var line = document.GetLineByNumber(open + 1 + block.Lines.Count);
                if (line.Length > MarkdownLineTokenizer.MaxInlineLength)
                {
                    block.Stopped = true;
                    break;
                }
                engine.CurrentSpanStack = block.Stack;
                var highlighted = engine.HighlightLine(document, line);
                // A failure here costs only this line its function colors.
                if (block.FunctionCalls) FunctionCallHighlighter.AddTo(highlighted, Failed);
                block.Lines.Add(highlighted);
                block.Stacks.Add(engine.CurrentSpanStack);
            }
        }

        private Block Start(TextDocument document, int open)
        {
            string? id = FenceLanguages.IdOfFence(document.GetText(document.GetLineByNumber(open)));
            var definition = _definitionFor(id);
            return new Block(definition == null ? null : new HighlightingEngine(definition.MainRuleSet))
            {
                FunctionCalls = PadLanguages.ForFence(id)?.FunctionCalls == true,
            };
        }

        private sealed class Block
        {
            public Block(HighlightingEngine? engine) => Engine = engine;

            /// <summary>Null when the language has no colors, or highlighting failed.</summary>
            public HighlightingEngine? Engine;

            /// <summary>The language calls functions as <c>name(...)</c>: its plain names get the Function color.</summary>
            public bool FunctionCalls;

            /// <summary>Highlighting threw: no colors until the next edit.</summary>
            public bool Failed;

            /// <summary>A line over the inline limit ended the colors at index <c>Lines.Count</c>.</summary>
            public bool Stopped;

            /// <summary>The highlighted inside lines, from the first.</summary>
            public readonly List<HighlightedLine> Lines = new();

            /// <summary>The engine's span stack after each of <see cref="Lines"/>.</summary>
            public readonly List<ImmutableStack<HighlightingSpan>> Stacks = new();

            /// <summary>The span stack the next line starts with.</summary>
            public ImmutableStack<HighlightingSpan> Stack => Stacks.Count == 0 ? ImmutableStack<HighlightingSpan>.Empty : Stacks[^1];

            /// <summary>Forgets the lines from index <paramref name="count"/> on; nothing when the block was highlighted no further.</summary>
            public void KeepLines(int count)
            {
                if (count > Lines.Count) return;
                Lines.RemoveRange(count, Lines.Count - count);
                Stacks.RemoveRange(count, Stacks.Count - count);
                Stopped = false;
            }
        }
    }
}
