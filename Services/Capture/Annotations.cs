using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>Which editing tool is active.</summary>
    public enum CaptureTool
    {
        Select, Rectangle, Ellipse, Arrow, Line, Pen, Highlighter, Text, Redact, Step, Crop
    }

    /// <summary>How a redaction hides what is underneath.</summary>
    public enum RedactStyle { Pixelate, Blur, Solid }

    /// <summary>
    /// A point in <b>image pixel</b> space (not DIPs), doubles so freehand strokes stay smooth
    /// when the editor is displaying the image zoomed.
    /// </summary>
    public readonly record struct ImgPoint(double X, double Y);

    /// <summary>
    /// One drawn mark. Annotations are immutable records on purpose: the editor's undo history
    /// is then just a list of previous lists, with no cloning to get wrong and no way for an
    /// undone edit to keep mutating live state.
    /// </summary>
    public abstract record Annotation
    {
        public string ColorHex { get; init; } = "#FF453A";
        public double Thickness { get; init; } = 3;
    }

    /// <summary>Rectangle, ellipse, arrow or straight line, defined by a drag from Start to End.</summary>
    public sealed record ShapeAnnotation(CaptureTool Shape, ImgPoint Start, ImgPoint End) : Annotation
    {
        public bool Filled { get; init; }
    }

    /// <summary>Freehand pen or translucent highlighter.</summary>
    public sealed record StrokeAnnotation(IReadOnlyList<ImgPoint> Points, bool Highlighter) : Annotation;

    /// <summary>A text label anchored at its top-left.</summary>
    public sealed record TextAnnotation(ImgPoint Origin, string Text, double FontSize) : Annotation;

    /// <summary>An area hidden by pixelation, blur or a solid block — the redaction tool.</summary>
    public sealed record RedactAnnotation(ImgPoint Start, ImgPoint End, RedactStyle Style) : Annotation
    {
        /// <summary>Edge of one mosaic tile / blur radius, in image pixels.</summary>
        public double Strength { get; init; } = 12;
    }

    /// <summary>A numbered badge, for walkthrough screenshots.</summary>
    public sealed record StepAnnotation(ImgPoint Center, int Number, double Radius) : Annotation;

    /// <summary>
    /// The editable state of one capture: its annotations plus the crop applied to the source
    /// image, with undo/redo.
    ///
    /// <para>
    /// History is snapshot-based rather than operation-based. A screenshot carries a handful of
    /// marks, so copying the list on each edit costs nothing, and it removes the classic undo
    /// bug where an operation's inverse is subtly wrong. Because annotations are immutable, a
    /// snapshot is a shallow list copy.
    /// </para>
    /// </summary>
    public sealed class AnnotationDocument
    {
        private readonly record struct State(List<Annotation> Items, PixelRect Crop);

        private readonly List<State> _history = new();
        private int _index;

        public AnnotationDocument(PixelRect fullBounds)
        {
            FullBounds = fullBounds;
            _history.Add(new State(new List<Annotation>(), fullBounds));
            _index = 0;
        }

        /// <summary>The untouched image bounds, in image pixels, origin (0,0).</summary>
        public PixelRect FullBounds { get; }

        private State Current => _history[_index];

        public IReadOnlyList<Annotation> Items => new ReadOnlyCollection<Annotation>(Current.Items);

        /// <summary>The visible region of the source image after cropping.</summary>
        public PixelRect Crop => Current.Crop;

        public bool CanUndo => _index > 0;
        public bool CanRedo => _index < _history.Count - 1;
        public bool IsDirty => Current.Items.Count > 0 || Crop != FullBounds;

        /// <summary>The number the next step badge should carry.</summary>
        public int NextStepNumber =>
            Current.Items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

        public void Add(Annotation annotation)
        {
            if (annotation == null) return;
            var items = new List<Annotation>(Current.Items) { annotation };
            Push(new State(items, Current.Crop));
        }

        public bool RemoveLast()
        {
            if (Current.Items.Count == 0) return false;
            var items = new List<Annotation>(Current.Items);
            items.RemoveAt(items.Count - 1);
            Push(new State(items, Current.Crop));
            return true;
        }

        public bool Remove(Annotation annotation)
        {
            if (annotation == null || !Current.Items.Contains(annotation)) return false;
            var items = new List<Annotation>(Current.Items);
            items.Remove(annotation);
            Push(new State(items, Current.Crop));
            return true;
        }

        /// <summary>
        /// Crops to <paramref name="rect"/>, expressed in the ORIGINAL image's coordinates and
        /// clamped to it. Annotations keep their original coordinates; the renderer offsets by
        /// the crop, so undoing a crop restores marks that fell outside it.
        /// </summary>
        public void ApplyCrop(PixelRect rect)
        {
            var clamped = rect.Intersect(FullBounds);
            if (clamped.IsEmpty || clamped == Current.Crop) return;
            Push(new State(new List<Annotation>(Current.Items), clamped));
        }

        public void ResetCrop()
        {
            if (Current.Crop == FullBounds) return;
            Push(new State(new List<Annotation>(Current.Items), FullBounds));
        }

        public void Clear()
        {
            if (Current.Items.Count == 0) return;
            Push(new State(new List<Annotation>(), Current.Crop));
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            _index--;
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            _index++;
            return true;
        }

        private void Push(State state)
        {
            // A new edit after undoing discards the redo tail, the standard editor contract.
            if (_index < _history.Count - 1) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
            _history.Add(state);
            _index = _history.Count - 1;

            // Bound the history so a long annotation session cannot grow without limit.
            const int maxStates = 200;
            if (_history.Count > maxStates)
            {
                _history.RemoveRange(0, _history.Count - maxStates);
                _index = _history.Count - 1;
            }
        }
    }
}
