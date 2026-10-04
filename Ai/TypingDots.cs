using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Three small dots fading in turn: an answer is being waited for. The Ask window shows them
    /// until an answer starts, MicaPad's AI pane while a request runs.
    ///
    /// <para>
    /// They move at 30 frames a second, and only while the control is loaded and visible, itself
    /// and everything it stands in: MicaStats draws in software, where an animation that runs
    /// for a pane nobody sees costs CPU for nothing. Hiding the dots, or a panel around them,
    /// stops them; showing them again starts them.
    /// </para>
    /// <para>
    /// The dots have no colour of their own. Whoever uses them gives <see cref="Fill"/> a brush
    /// or, as each place has its own palette, a resource by its key (<c>Ask.Muted</c>,
    /// <c>Pad.Muted</c>), which the dots follow when the theme changes it.
    /// </para>
    /// </summary>
    internal sealed class TypingDots : StackPanel
    {
        /// <summary>The colour of the dots.</summary>
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(TypingDots),
            new PropertyMetadata(null, (dots, change) => ((TypingDots)dots).Paint((Brush?)change.NewValue)));

        private readonly Ellipse[] _dots = new Ellipse[3];
        private bool _moving;

        /// <summary>Builds the three dots in a row, still and with no colour.</summary>
        public TypingDots()
        {
            Orientation = Orientation.Horizontal;
            for (int i = 0; i < _dots.Length; i++)
            {
                _dots[i] = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0) };
                Children.Add(_dots[i]);
            }

            Loaded += (_, _) => Animate(IsVisible);
            Unloaded += (_, _) => Animate(false);
            IsVisibleChanged += (_, _) => Animate(IsVisible && IsLoaded);
        }

        /// <summary>The colour of the dots: a brush, or a resource reference set on <see cref="FillProperty"/>.</summary>
        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        private void Paint(Brush? fill)
        {
            foreach (Ellipse dot in _dots) dot.Fill = fill;
        }

        /// <summary>Three dots fading in turn at 30 frames a second, or no animation at all.</summary>
        private void Animate(bool on)
        {
            if (on == _moving) return;   // moving already (loaded and shown are told separately): the fades are not started over
            _moving = on;
            for (int i = 0; i < _dots.Length; i++)
            {
                if (!on)
                {
                    _dots[i].BeginAnimation(OpacityProperty, null);
                    continue;
                }
                var fade = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(150 * i),
                };
                Timeline.SetDesiredFrameRate(fade, 30);
                _dots[i].BeginAnimation(OpacityProperty, fade);
            }
        }
    }
}
