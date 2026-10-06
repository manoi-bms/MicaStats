using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Presents a task's separately stored dates after its Markdown line. The visual text occupies
    /// wrapping space but no document characters, so selection, copying and editing see only the
    /// note's Markdown.
    /// </summary>
    internal sealed class TaskDateGenerator : VisualLineElementGenerator
    {
        private readonly Func<TextDocument, int, TaskDateRecord?> _lookup;
        private readonly Func<PadPalette> _palette;

        public TaskDateGenerator(Func<TextDocument, int, TaskDateRecord?> lookup, Func<PadPalette> palette)
        {
            _lookup = lookup;
            _palette = palette;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            int lastLine = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
            for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= lastLine; line = line.NextLine)
            {
                if (line.EndOffset < startOffset) continue;
                if (_lookup(document, line.LineNumber) != null) return line.EndOffset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(offset);
            if (line.EndOffset != offset || _lookup(document, line.LineNumber) is not { } task) return null;

            return new TaskDateElement(" " + MarkdownTasks.FormatDates(task), _palette().Muted);
        }
    }

    /// <summary>A non-document text run whose only caret stop is the task line's real end.</summary>
    internal sealed class TaskDateElement : VisualLineElement
    {
        private readonly string _text;
        private readonly PadColor _color;

        public TaskDateElement(string text, PadColor color) : base(text.Length, 0)
        {
            _text = text;
            DisplayText = text;
            _color = color;
        }

        internal string DisplayText { get; }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            int relativeOffset = startVisualColumn - VisualColumn;
            TextRunProperties.SetForegroundBrush(PadThemeApplier.ToBrush(_color));
            TextRunProperties.SetTextDecorations(new TextDecorationCollection());
            return new TextCharacters(_text, relativeOffset, _text.Length - relativeOffset, TextRunProperties);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(
            int visualColumnLimit,
            ITextRunConstructionContext context)
        {
            int length = Math.Clamp(visualColumnLimit - VisualColumn, 0, _text.Length);
            var range = new CharacterBufferRange(_text, 0, length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(
                length,
                new CultureSpecificCharacterBufferRange(TextRunProperties.CultureInfo, range));
        }

        public override bool IsWhitespace(int visualColumn)
        {
            int index = visualColumn - VisualColumn;
            return index >= 0 && index < _text.Length && char.IsWhiteSpace(_text[index]);
        }

        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn;

        public override int GetRelativeOffset(int visualColumn) => RelativeTextOffset;

        public override bool HandlesLineBorders => true;

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            if (direction == LogicalDirection.Forward) return visualColumn < VisualColumn ? VisualColumn : -1;
            return visualColumn > VisualColumn ? VisualColumn : -1;
        }
    }
}
