using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Links in the window: the generator, Ctrl+Click handling and failures.</summary>
    public class PadLinkTests
    {
        [Fact]
        public void The_editor_has_the_safe_generator_and_not_avalonedits_own() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var generators = window.Editor.TextArea.TextView.ElementGenerators;
            Assert.Single(generators.OfType<SafeLinkGenerator>());
            Assert.DoesNotContain(generators, g => g.GetType() == typeof(LinkElementGenerator));
            Assert.False(window.Editor.Options.EnableHyperlinks);
            Assert.False(window.Editor.Options.EnableEmailHyperlinks);
        });

        [Fact]
        public void A_match_that_is_not_an_allowed_link_produces_no_link_element() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var view = window.Editor.TextArea.TextView;
            window.Editor.Document.Text = "bad http://[oops and good https://example.com/ok";
            var visualLine = view.GetOrConstructVisualLine(window.Editor.Document.GetLineByNumber(1));
            var links = visualLine.Elements.OfType<VisualLineLinkText>().ToList();
            Assert.Single(links);
            Assert.Equal(new Uri("https://example.com/ok"), links[0].NavigateUri);
        });

        [Fact]
        public void The_history_preview_has_the_same_safe_setup() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var generators = window.PreviewEditor.TextArea.TextView.ElementGenerators;
            Assert.Single(generators.OfType<SafeLinkGenerator>());
            Assert.DoesNotContain(generators, g => g.GetType() == typeof(LinkElementGenerator));
            Assert.False(window.PreviewEditor.Options.EnableHyperlinks);
            Assert.False(window.PreviewEditor.Options.EnableEmailHyperlinks);
        });

        [Fact]
        public void A_preview_request_is_handled_and_a_file_link_opens_nothing() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.PreviewEditor.Measure(new System.Windows.Size(300, 200)); // not shown, so build the visual tree the event bubbles through
            var opened = new List<Uri>();
            window.OpenLink = uri => { opened.Add(uri); return true; };
            var args = new System.Windows.Navigation.RequestNavigateEventArgs(new Uri("file:///C:/x.exe"), null)
            {
                RoutedEvent = Hyperlink.RequestNavigateEvent,
            };
            window.PreviewEditor.TextArea.TextView.RaiseEvent(args);
            Assert.True(args.Handled);
            Assert.Empty(opened);
        });

        [Fact]
        public void A_web_link_opens_through_the_opener() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var opened = new List<Uri>();
            window.OpenLink = uri => { opened.Add(uri); return true; };

            window.OnLinkRequested(new Uri("https://example.com/a"));

            Assert.Equal(new[] { new Uri("https://example.com/a") }, opened);
        });

        [Fact]
        public void A_navigation_request_for_another_scheme_opens_nothing() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var opened = new List<Uri>();
            window.OpenLink = uri => { opened.Add(uri); return true; };

            window.OnLinkRequested(new Uri("file:///C:/Windows/System32/calc.exe"));

            Assert.Empty(opened);
        });

        [Fact]
        public void The_request_event_is_handled_so_avalonedit_never_starts_a_process() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Measure(new System.Windows.Size(300, 200)); // not shown, so build the visual tree the event bubbles through
            window.OpenLink = uri => true;
            var args = new System.Windows.Navigation.RequestNavigateEventArgs(new Uri("file:///C:/x.exe"), null)
            {
                RoutedEvent = Hyperlink.RequestNavigateEvent,
            };
            window.Editor.TextArea.TextView.RaiseEvent(args);
            Assert.True(args.Handled);
        });

        [Fact]
        public void A_link_that_cannot_be_opened_says_so_in_the_status_bar() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.OpenLink = uri => false;

            window.OnLinkRequested(new Uri("https://example.com"));

            Assert.Equal(Visibility.Visible, window.StatusMessage.Visibility);
            Assert.Equal("That link could not be opened.", window.StatusMessage.Text);
        });

        [Fact]
        public void The_link_color_follows_the_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Dark.MdLink),
                         ((System.Windows.Media.SolidColorBrush)window.Editor.TextArea.TextView.LinkTextForegroundBrush).Color);
            window.ToggleTheme();
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Light.MdLink),
                         ((System.Windows.Media.SolidColorBrush)window.Editor.TextArea.TextView.LinkTextForegroundBrush).Color);
        });
    }
}
