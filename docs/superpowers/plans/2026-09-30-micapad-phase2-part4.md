# MicaPad phase 2, Part 4: links, tab reorder, full screen, Copy as RTF — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Web and mail links in notes open with Ctrl+Click (and nothing else can ever be launched from text), tabs reorder by dragging, F11 gives a distraction-free full screen, and *Copy as RTF* pastes the note into Word or Outlook with the colors and styles MicaPad shows.

**Architecture:** Pure rules in `Services/Pad` (`SafeLinks`, `TabDrop`, `RtfWriter`), unit-tested without WPF; thin WPF adapters in `Pad/` (`SafeLinkGenerator`, `TabDragController`, `RtfRuns`) wired by `MicaPadWindow`. AvalonEdit's own hyperlink option stays off; MicaPad's generator only ever produces http/https/mailto links, and the window handles every navigation request itself.

**Tech Stack:** .NET 8 WPF, AvalonEdit 6.3.1.120 (`LinkElementGenerator` — protected `(Regex)` ctor, `protected virtual Uri GetUriFromMatch(Match)`; `VisualLineLinkText.OnMouseDown` raises `Hyperlink.RequestNavigateEvent` first and only starts a process itself when nobody handled it — verified by reflection; `TextView.LinkTextForegroundBrush`; `DocumentHighlighter.HighlightLine`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micapad-phase2-design.md` — Part 4 (4.1–4.4), Error handling (link opening and clipboard), Testing Part 4 and manual item 5.

## Global Constraints

- Links: `http://`, `https://` and `mailto:` addresses are underlined in the link color; **Ctrl+Click** opens them in the default browser or mail program; the tooltip reads *Ctrl+Click to open*. No other scheme is ever opened — not `file:`, not paths, not custom protocols. A failure to open is logged and shown in the status bar.
- Tabs: dragging a tab past the system drag threshold moves it; the other tabs make room while dragging; dropping sets the new order, saved to the session. The strip scrolls when dragging past its edge. A plain click still just selects the tab; double-click still renames.
- Full screen: `F11` (and *Full screen* in `☰`) hides the title bar and fills the current monitor, keeping tabs, find bar and status bar. `F11` again restores the previous placement. Not saved: MicaPad always reopens windowed, at the placement it had before full screen.
- Copy as RTF: in the right-click menu and `☰`; copies the selection (or the whole note when nothing is selected) as RTF **and** plain text, with the colors, bold, italic, strike and heading sizes shown — always in the **light** palette. The font is the editor font. A busy clipboard is retried three times over 300 ms; failing that the status bar says *Clipboard busy, try again*.
- Status bar messages (new in this part, reused by Part 5): shown for 5 s next to the counts, then gone.
- New code under `Services/Pad/` holds no WPF types. Only MicaPad changes.
- Tests never touch the real `%APPDATA%`, never launch MicaStats, never start Explorer, never open a browser or mail program (the link opener is injected), never use the network, never create their own STA thread (use `UiThread.Run`). Never build or publish into `bin\Release`.
- Build/test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`; prefix full-suite runs with `timeout 300`. `CultureInfo.InvariantCulture` for number text.
- Commits: `git add` exact paths, `-m` messages (no heredoc), never amend, `Co-Authored-By:` trailer naming the model that wrote the commit.

## Review Focus

1. **Only safe links, ever** — `file:`, `javascript:`, a UNC path, `C:\x`, `ms-settings:`, `https:` with nothing after it, and a URL with a trailing `.`/`)` must not produce a clickable link that launches anything or includes the punctuation (Task 1: `Only_web_and_mail_links_are_allowed`, `Trailing_punctuation_is_not_part_of_the_link`, `A_navigation_request_for_another_scheme_opens_nothing`).
2. **A click on a tab is still a click** — a tiny mouse jitter below the drag threshold never reorders; double-click rename and middle-click close still work (Task 2: `A_move_below_the_threshold_does_not_drag`).
3. **Full screen never sticks** — a restart after exiting while in full screen reopens windowed at the old size (Task 3: `Leaving_full_screen_restores_style_state_and_bounds`, `The_saved_placement_is_the_one_before_full_screen`).
4. **RTF escaping and Thai text** — `\`, `{`, `}`, tabs, CRLF/LF/CR, Thai and emoji survive a paste (Task 4: `Special_characters_are_escaped`, `Thai_and_emoji_are_written_as_unicode`).
5. **Clipboard busy** — the copy never throws into the UI; three retries then the status message (Task 4: `A_busy_clipboard_is_retried_then_reported`).

## File structure

| File | Task | Responsibility |
|---|---|---|
| `Services/Pad/SafeLinks.cs` (new) | 1 | The link pattern, allowed schemes, the link under a column |
| `Pad/SafeLinkGenerator.cs` (new) | 1 | AvalonEdit link generator that refuses anything unsafe |
| `Pad/MicaPadWindow.xaml(.cs)` | 1–4 | Wiring: generator, navigation, tooltip, status message, drag controller, full screen, Copy as RTF |
| `Services/Pad/PadWorkspace.cs` | 2 | `MoveTab` |
| `Services/Pad/TabDrop.cs` (new), `Pad/TabDragController.cs` (new) | 2 | Where a dragged tab lands; the mouse handling |
| `Services/Pad/RtfWriter.cs` (new), `Pad/RtfRuns.cs` (new) | 4 | Styled runs → RTF; the runs MicaPad shows, in the light palette |
| `Pad/EditorMenus.cs` | 4 | *Copy as RTF* item |
| `GUIDE.md`, `README.md` | 1–4 | User docs and both key tables |
| tests: `SafeLinksTests.cs`, `TabDropTests.cs`, `RtfWriterTests.cs`, `PadLinkTests.cs`, `PadTabDragTests.cs`, `PadFullScreenTests.cs`, `PadCopyRtfTests.cs` (new) | all | |

Focused test command (Git Bash, repo root):

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>" -nologo
```

---

### Task 1: Safe links, and status bar messages

**Files:**
- Create: `Services/Pad/SafeLinks.cs`, `Pad/SafeLinkGenerator.cs`
- Modify: `Pad/MicaPadWindow.xaml` (status message), `Pad/MicaPadWindow.xaml.cs`, `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/SafeLinksTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadLinkTests.cs`

**Interfaces:**
- Produces: `SafeLinks.Pattern` (string), `SafeLinks.TryCreate(string text)` → `Uri?`, `SafeLinks.IsAllowed(Uri uri)`, `SafeLinks.LinkAt(string line, int column)` → `(int Start, int Length)?`; `internal sealed class SafeLinkGenerator : LinkElementGenerator`; window: `internal Func<Uri, bool> OpenLink { get; set; }` (returns false on failure; default shell-executes), `internal void ShowStatus(string message)`, `StatusMessage` (XAML `TextBlock`), `internal void OnLinkRequested(Uri uri)` (for tests).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/SafeLinksTests.cs`:

```csharp
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Which text becomes a link, and which links may ever be opened.</summary>
    public class SafeLinksTests
    {
        private static string[] Matches(string text) =>
            Regex.Matches(text, SafeLinks.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(m => m.Value).ToArray();

        [Theory]
        [InlineData("https://example.com/a?b=1#c")]
        [InlineData("http://intranet/page")]
        [InlineData("mailto:someone@example.com")]
        [InlineData("HTTPS://EXAMPLE.COM")]
        public void Only_web_and_mail_links_are_allowed(string text)
        {
            var uri = SafeLinks.TryCreate(text);
            Assert.NotNull(uri);
            Assert.True(SafeLinks.IsAllowed(uri!));
        }

        [Theory]
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("javascript:alert(1)")]
        [InlineData("ms-settings:privacy")]
        [InlineData("C:\\Windows\\notepad.exe")]
        [InlineData("\\\\server\\share\\x.exe")]
        [InlineData("ftp://example.com/x")]
        [InlineData("https:")]
        [InlineData("mailto:")]
        [InlineData("")]
        public void Anything_else_is_refused(string text)
        {
            Assert.Null(SafeLinks.TryCreate(text));
        }

        [Theory]
        [InlineData("see https://example.com/x.", "https://example.com/x")]
        [InlineData("(https://example.com/a)", "https://example.com/a")]
        [InlineData("go to https://example.com, then", "https://example.com")]
        [InlineData("\"https://example.com/q\"", "https://example.com/q")]
        [InlineData("mail mailto:a@b.co; thanks", "mailto:a@b.co")]
        public void Trailing_punctuation_is_not_part_of_the_link(string text, string link)
        {
            Assert.Equal(new[] { link }, Matches(text));
        }

        [Fact]
        public void Other_schemes_are_not_even_matched()
        {
            Assert.Empty(Matches("file:///c:/x javascript:alert(1) ms-settings:privacy www.example.com"));
        }

        [Fact]
        public void The_link_under_a_column()
        {
            string line = "read https://example.com/doc now";
            Assert.Equal((5, 23), SafeLinks.LinkAt(line, 10));
            Assert.Equal((5, 23), SafeLinks.LinkAt(line, 5));
            Assert.Null(SafeLinks.LinkAt(line, 2));
            Assert.Null(SafeLinks.LinkAt(line, 29));
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/PadLinkTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~SafeLinksTests|FullyQualifiedName~PadLinkTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/SafeLinks.cs`**

```csharp
using System;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The only links MicaPad ever opens (spec 4.1): http, https and mailto. Text in a note can never
    /// launch a program — not file:, not paths, not custom protocols.
    /// </summary>
    public static class SafeLinks
    {
        /// <summary>
        /// A web or mail address in text. It never ends with sentence punctuation or a closing
        /// bracket or quote, so "see https://a.com/x." links "https://a.com/x".
        /// </summary>
        public const string Pattern = @"\b(?:https?://|mailto:)[^\s<>""'`]*[^\s<>""'`.,;:!?)\]}]";

        private static readonly Regex Rx = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The address as a link, or null when it is not an allowed, well-formed one.</summary>
        public static Uri? TryCreate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            return IsAllowed(uri) ? uri : null;
        }

        /// <summary>True for http and https with a host, and mailto with an address.</summary>
        public static bool IsAllowed(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return false;
            return uri.Scheme switch
            {
                "http" or "https" => !string.IsNullOrEmpty(uri.Host),
                "mailto" => uri.AbsoluteUri.Length > "mailto:".Length,
                _ => false,
            };
        }

        /// <summary>The link covering <paramref name="column"/> (zero-based) in a line, or null.</summary>
        public static (int Start, int Length)? LinkAt(string line, int column)
        {
            foreach (Match m in Rx.Matches(line))
            {
                if (column >= m.Index && column < m.Index + m.Length && TryCreate(m.Value) != null)
                    return (m.Index, m.Length);
            }
            return null;
        }
    }
}
```

(`"https:"` fails `Uri.TryCreate` or has no host; `"mailto:"` has nothing after the colon; `ftp://`, `file:`, paths and custom schemes fail `IsAllowed`.)

- [ ] **Step 4: Write `Pad/SafeLinkGenerator.cs`**

```csharp
using System;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Underlines http, https and mailto addresses (spec 4.1). Only <see cref="SafeLinks"/> patterns
    /// match, and a match that is not an allowed link produces no element at all. Ctrl+Click raises
    /// RequestNavigate, which the window handles itself.
    /// </summary>
    internal sealed class SafeLinkGenerator : LinkElementGenerator
    {
        public SafeLinkGenerator()
            : base(new Regex(SafeLinks.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            RequireControlModifierForClick = true;
        }

        protected override Uri? GetUriFromMatch(Match match) => SafeLinks.TryCreate(match.Value);
    }
}
```

(If `GetUriFromMatch`'s declared return type is non-nullable `Uri`, keep `Uri?` only if the compiler accepts it; otherwise return `null!` — AvalonEdit's `ConstructElementFromMatch` skips a null URI. Verify with a focused probe that a disallowed match yields no `VisualLineLinkText`; say what you found in the report.)

- [ ] **Step 5: Status bar message**

`Pad/MicaPadWindow.xaml`, status bar, after the `OccurrenceText` TextBlock:

```xml
                <TextBlock x:Name="StatusMessage" VerticalAlignment="Center" FontSize="11.5" Foreground="{DynamicResource Pad.Accent}"
                           Margin="16,0,0,0" Visibility="Collapsed" TextTrimming="CharacterEllipsis" />
```

`Pad/MicaPadWindow.xaml.cs`:

```csharp
        private DispatcherTimer? _statusTimer;

        /// <summary>A short message in the status bar for 5 s (a link that failed, a busy clipboard, a tool that cannot apply).</summary>
        internal void ShowStatus(string message)
        {
            StatusMessage.Text = message;
            StatusMessage.Visibility = Visibility.Visible;
            _statusTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick -= OnStatusTimer;
            _statusTimer.Tick += OnStatusTimer;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void OnStatusTimer(object? sender, EventArgs e)
        {
            _statusTimer?.Stop();
            StatusMessage.Visibility = Visibility.Collapsed;
        }
```

and `_statusTimer?.Stop();` in `Detach()`.

- [ ] **Step 6: Wire links into the window**

In `Pad/MicaPadWindow.xaml.cs`:

1. `ConfigureEditor()`: keep `EnableHyperlinks = false` and `EnableEmailHyperlinks = false`; add `Editor.TextArea.TextView.ElementGenerators.Add(new SafeLinkGenerator());`.
2. Constructor (after `ConfigureEditor()`): `Editor.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((s, e) => { e.Handled = true; OnLinkRequested(e.Uri); }));` — handled always, so AvalonEdit never starts a process itself.
3. Members:

```csharp
        // ---- links ---------------------------------------------------------------------------

        /// <summary>Opens an allowed link in the default browser or mail program; false when that failed. Tests replace it.</summary>
        internal Func<Uri, bool> OpenLink { get; set; } = uri =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                DiagnosticsLog.Warn("pad", "A link could not be opened: " + ex.Message);
                return false;
            }
        };

        /// <summary>Ctrl+Click on a link. Anything but http, https or mailto is ignored, whatever asked for it.</summary>
        internal void OnLinkRequested(Uri uri)
        {
            if (!SafeLinks.IsAllowed(uri)) return;
            if (!OpenLink(uri)) ShowStatus("That link could not be opened.");
        }
```

4. Tooltip *Ctrl+Click to open*:

```csharp
        private readonly ToolTip _linkTip = new() { Content = "Ctrl+Click to open", Placement = PlacementMode.Mouse };
```

   In the constructor:

```csharp
            Editor.TextArea.TextView.MouseHover += OnEditorMouseHover;
            Editor.TextArea.TextView.MouseHoverStopped += (s, e) => _linkTip.IsOpen = false;
```

```csharp
        private void OnEditorMouseHover(object sender, MouseEventArgs e)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position is not { } at || Editor.Document == null) return;
            var line = Editor.Document.GetLineByNumber(at.Line);
            if (line.Length > 4000) return;
            if (SafeLinks.LinkAt(Editor.Document.GetText(line), at.Column - 1) == null) return;
            _linkTip.PlacementTarget = Editor.TextArea.TextView;
            _linkTip.IsOpen = true;
            e.Handled = true;
        }
```

5. `ApplyTheme()`: `area.TextView.LinkTextForegroundBrush = PadThemeApplier.ToBrush(_palette.MdLink);` next to the other TextView brushes, and `ModernWpf.ThemeManager.SetRequestedTheme(_linkTip, ...)` the same way the menus get their theme.
6. `using System.Windows.Documents;` (Hyperlink), `using System.Windows.Navigation;` (RequestNavigateEventHandler) as needed.

- [ ] **Step 7: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 8: Document it**

`GUIDE.md`, `## 📝 MicaPad`, before `### Where notes live`:

```markdown
### Links, tabs and view

**Links**: `http://`, `https://` and `mailto:` addresses are underlined; **Ctrl+Click** opens them
in your browser or mail program. Nothing else in a note is ever opened — not files, paths or other
protocols.

```

README English *Inside MicaPad* key table, a row after the bookmark row: `| **Ctrl+Click** a link | Open it (web and mail links only) |`; Thai table: `| **Ctrl+Click** ลิงก์ | เปิดลิงก์ (เฉพาะเว็บและอีเมล) |`.

- [ ] **Step 9: Commit**

```bash
git add Services/Pad/SafeLinks.cs Pad/SafeLinkGenerator.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/SafeLinksTests.cs tests/Kil0bitSystemMonitor.Tests/PadLinkTests.cs
git commit -m "feat(pad): Ctrl+Click web and mail links - nothing else is ever opened" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 2: Drag to reorder tabs

**Files:**
- Create: `Services/Pad/TabDrop.cs`, `Pad/TabDragController.cs`
- Modify: `Services/Pad/PadWorkspace.cs` (`MoveTab`), `Pad/MicaPadWindow.xaml.cs` (controller), `GUIDE.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/TabDropTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadTabDragTests.cs`

**Interfaces:**
- Produces: `PadWorkspace.MoveTab(OpenNote note, int index)` (no save; clamps); `TabDrop.TargetIndex(double pointerX, IReadOnlyList<(double Left, double Width)> tabs, int dragged)`; `TabDrop.PastThreshold(double dx, double dy, double minX, double minY)`; `internal sealed class TabDragController` (ctor `(ItemsControl strip, ScrollViewer scroller, Func<IReadOnlyList<OpenNote>> tabs, Action<OpenNote, int> move, Action dropped)`; `internal void Press(OpenNote note, Point at)`, `internal bool MoveTo(Point at, IReadOnlyList<(double Left, double Width)> layout)` (true once dragging), `internal void Release()`, `internal bool IsDragging`).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/TabDropTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Where a dragged tab lands.</summary>
    public class TabDropTests
    {
        // Three tabs, 100 wide, touching: centers at 50, 150, 250.
        private static readonly (double Left, double Width)[] Tabs = { (0, 100), (100, 100), (200, 100) };

        [Theory]
        [InlineData(0, 10, 0)]      // still over itself
        [InlineData(0, 160, 1)]     // past the second tab's center
        [InlineData(0, 290, 2)]     // past the last center
        [InlineData(2, 120, 1)]     // dragged left, before the second tab's center... after the first
        [InlineData(2, 20, 0)]      // before the first center
        [InlineData(1, 140, 1)]     // over its own place
        [InlineData(0, -50, 0)]     // left of the strip
        [InlineData(0, 900, 2)]     // right of the strip
        public void The_target_is_the_count_of_other_tab_centers_left_of_the_pointer(int dragged, double x, int expected)
        {
            Assert.Equal(expected, TabDrop.TargetIndex(x, Tabs, dragged));
        }

        [Theory]
        [InlineData(2, 0, false)]
        [InlineData(4, 0, true)]
        [InlineData(0, 4, true)]
        [InlineData(-4, 0, true)]
        public void A_drag_starts_past_the_system_threshold(double dx, double dy, bool expected)
        {
            Assert.Equal(expected, TabDrop.PastThreshold(dx, dy, 4, 4));
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/PadTabDragTests.cs`:

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Reordering tabs by dragging, and keeping the order.</summary>
    public class PadTabDragTests
    {
        private static readonly (double Left, double Width)[] Layout = { (0, 100), (100, 100), (200, 100) };

        [Fact]
        public void Move_tab_reorders_and_the_order_survives_a_restart() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var a = env.Workspace.NewNote();
            var b = env.Workspace.NewNote();
            var c = env.Workspace.NewNote();
            foreach (var (note, text) in new[] { (a, "a"), (b, "b"), (c, "c") }) PadTestEnv.Type(env.Workspace, note, text);
            env.Workspace.FlushPending();              // notes with text on disk, as a real session has

            env.Workspace.MoveTab(c, 0);
            env.Workspace.SaveSession();
            env.Flush();

            Assert.Equal(new[] { c, a, b }, env.Workspace.Open.ToArray());
            var again = env.NewWorkspace();
            again.Restore();
            Assert.Equal(new[] { c.Id, a.Id, b.Id }, again.Open.Select(n => n.Id).ToArray());
        });

        [Fact]
        public void Move_tab_clamps_the_index() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var a = env.Workspace.NewNote();
            var b = env.Workspace.NewNote();
            env.Workspace.MoveTab(a, 99);
            Assert.Equal(new[] { b, a }, env.Workspace.Open.ToArray());
            env.Workspace.MoveTab(a, -3);
            Assert.Equal(new[] { a, b }, env.Workspace.Open.ToArray());
        });

        [Fact]
        public void A_drag_moves_the_tab_live_and_saves_once_on_drop() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            window.NewTab();
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            Assert.True(drag.MoveTo(new Point(170, 12), Layout));
            Assert.True(drag.IsDragging);
            Assert.Equal(1, env.Workspace.Open.IndexOf(first));        // the others made room already

            drag.MoveTo(new Point(290, 12), Layout);
            Assert.Equal(2, env.Workspace.Open.IndexOf(first));

            drag.Release();
            Assert.False(drag.IsDragging);
            Assert.Equal(env.Workspace.Open.Select(n => n.Id), env.Workspace.Session.OpenNoteIds);
        });

        [Fact]
        public void A_move_below_the_threshold_does_not_drag() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var first = env.Workspace.Open[0];
            window.NewTab();
            var drag = window.TabDrag;

            drag.Press(first, new Point(50, 10));
            Assert.False(drag.MoveTo(new Point(51, 11), Layout));
            drag.Release();

            Assert.Equal(0, env.Workspace.Open.IndexOf(first));
            Assert.False(drag.IsDragging);
        });
    }
}
```

(`PadLanguageWindowTests.WithWindow` passes `(window, env, config)`; `window.NewTab()` is internal. Check that `PadTestEnv`'s default `post` runs inline — `NewNote` and `SaveSession` need no dispatcher here.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~TabDropTests|FullyQualifiedName~PadTabDragTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/TabDrop.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Where a dragged tab lands (spec 4.2). Pure: the controller passes the tabs' current layout.</summary>
    public static class TabDrop
    {
        /// <summary>
        /// The index the dragged tab belongs at: the number of other tabs whose center is left of
        /// the pointer. Clamped by construction to 0..count-1.
        /// </summary>
        public static int TargetIndex(double pointerX, IReadOnlyList<(double Left, double Width)> tabs, int dragged)
        {
            int index = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                if (i == dragged) continue;
                if (tabs[i].Left + tabs[i].Width / 2 < pointerX) index++;
            }
            return index;
        }

        /// <summary>True once the pointer moved past the system drag threshold on either axis.</summary>
        public static bool PastThreshold(double dx, double dy, double minX, double minY) =>
            Math.Abs(dx) >= minX || Math.Abs(dy) >= minY;
    }
}
```

- [ ] **Step 4: `PadWorkspace.MoveTab`**

```csharp
        /// <summary>Moves an open note's tab to <paramref name="index"/> (clamped). The caller saves the session when the drag ends.</summary>
        public void MoveTab(OpenNote note, int index)
        {
            int from = Open.IndexOf(note);
            if (from < 0) return;
            int to = Math.Clamp(index, 0, Open.Count - 1);
            if (to != from) Open.Move(from, to);
        }
```

Check that nothing listening to `Open.CollectionChanged` treats a `Move` as a close and reopen: `MicaPadWindow.OnOpenChanged` removes documents for `e.OldItems` — for `NotifyCollectionChangedAction.Move`, `OldItems` and `NewItems` both hold the moved note. Make `OnOpenChanged` return early on `NotifyCollectionChangedAction.Move` (a moved tab keeps its document, bookmarks and folds), and add a test: after `MoveTab`, the moved note's document is the same instance (`window.Editor.Document` when shown) and its text unchanged.

- [ ] **Step 5: Write `Pad/TabDragController.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Pad;

using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Drag to reorder tabs (spec 4.2). A press on a tab arms it; moving past the system drag
    /// threshold starts the drag, and from then on the tab moves live to where the pointer is (the
    /// others make room); release ends it and saves once. The strip scrolls near its edges.
    /// Positions are in the strip's coordinates.
    /// </summary>
    internal sealed class TabDragController
    {
        private const double EdgeScroll = 24;

        private readonly ItemsControl _strip;
        private readonly ScrollViewer _scroller;
        private readonly Func<IReadOnlyList<OpenNote>> _tabs;
        private readonly Action<OpenNote, int> _move;
        private readonly Action _dropped;
        private OpenNote? _pressed;
        private Point _pressAt;

        public TabDragController(ItemsControl strip, ScrollViewer scroller, Func<IReadOnlyList<OpenNote>> tabs,
                                 Action<OpenNote, int> move, Action dropped)
        {
            _strip = strip;
            _scroller = scroller;
            _tabs = tabs;
            _move = move;
            _dropped = dropped;
            strip.PreviewMouseMove += OnPreviewMouseMove;
            strip.PreviewMouseLeftButtonUp += (s, e) => { if (IsDragging) e.Handled = true; Release(); };
            strip.LostMouseCapture += (s, e) => Release();
        }

        internal bool IsDragging { get; private set; }

        /// <summary>A left press on a tab (the window calls this from the tab's mouse-down).</summary>
        internal void Press(OpenNote note, Point at)
        {
            _pressed = note;
            _pressAt = at;
            IsDragging = false;
        }

        /// <summary>The pointer moved with the button down. Returns true while dragging.</summary>
        internal bool MoveTo(Point at, IReadOnlyList<(double Left, double Width)> layout)
        {
            if (_pressed == null) return false;
            if (!IsDragging)
            {
                if (!TabDrop.PastThreshold(at.X - _pressAt.X, at.Y - _pressAt.Y,
                        SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
                    return false;
                IsDragging = true;
            }

            var tabs = _tabs();
            int dragged = IndexOf(tabs, _pressed);
            if (dragged < 0) { Release(); return false; }
            int target = TabDrop.TargetIndex(at.X, layout, dragged);
            if (target != dragged) _move(_pressed, target);
            return true;
        }

        /// <summary>The button went up (or capture was lost): ends a drag and saves the order once.</summary>
        internal void Release()
        {
            bool wasDragging = IsDragging;
            _pressed = null;
            IsDragging = false;
            if (Mouse.Captured == _strip) _strip.ReleaseMouseCapture();
            if (wasDragging) _dropped();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            bool wasDragging = IsDragging;
            if (!MoveTo(e.GetPosition(_strip), Layout())) return;
            if (!wasDragging) _strip.CaptureMouse();

            double x = e.GetPosition(_scroller).X;
            if (x < EdgeScroll) _scroller.LineLeft();
            else if (x > _scroller.ActualWidth - EdgeScroll) _scroller.LineRight();
            e.Handled = true;
        }

        /// <summary>Each tab's left edge and width in the strip, in tab order.</summary>
        private IReadOnlyList<(double Left, double Width)> Layout()
        {
            var tabs = _tabs();
            var layout = new List<(double, double)>(tabs.Count);
            foreach (var note in tabs)
            {
                if (_strip.ItemContainerGenerator.ContainerFromItem(note) is FrameworkElement container && container.IsVisible)
                {
                    var left = container.TranslatePoint(new Point(0, 0), _strip).X;
                    layout.Add((left, container.ActualWidth));
                }
                else
                {
                    layout.Add((double.MaxValue / 4, 0));
                }
            }
            return layout;
        }

        private static int IndexOf(IReadOnlyList<OpenNote> tabs, OpenNote note)
        {
            for (int i = 0; i < tabs.Count; i++) if (ReferenceEquals(tabs[i], note)) return i;
            return -1;
        }
    }
}
```

- [ ] **Step 6: The window**

1. Field and property: `private TabDragController _tabDrag = null!;` and `internal TabDragController TabDrag => _tabDrag;`.
2. Constructor, after `TabStrip.ItemsSource = _workspace.Open;`:

```csharp
            _tabDrag = new TabDragController(TabStrip, TabScroller, () => _workspace.Open,
                (note, index) => _workspace.MoveTab(note, index),
                () => _workspace.SaveSession());
```

3. `OnTabMouseLeftButtonDown`: for a single click, call `_tabDrag.Press(note, e.GetPosition(TabStrip));` before `ShowNote(note)` (the click still selects at once; a drag then moves the already-selected tab). Double-click keeps renaming and does not press.
4. `OnOpenChanged`: return at once for `NotifyCollectionChangedAction.Move` (see Step 4).

- [ ] **Step 7: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 8: Document it** — in `GUIDE.md`, `### Links, tabs and view`, add: `**Tabs**: drag a tab to move it; the order is kept after a restart.`

- [ ] **Step 9: Commit**

```bash
git add Services/Pad/TabDrop.cs Services/Pad/PadWorkspace.cs Pad/TabDragController.cs Pad/MicaPadWindow.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/TabDropTests.cs tests/Kil0bitSystemMonitor.Tests/PadTabDragTests.cs
git commit -m "feat(pad): drag tabs to reorder them; the order is kept" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 3: Full screen

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs`, `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadFullScreenTests.cs`

**Interfaces:**
- Produces: `MicaPadWindow.ToggleFullScreen()` (internal), `IsFullScreen` (internal); F11 in `HandleShortcut`; *Full screen* check item (F11) in `☰`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Kil0bitSystemMonitor.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>F11 full screen: in, out, and never saved.</summary>
    public class PadFullScreenTests
    {
        [Fact]
        public void F11_hides_the_title_bar_and_fills_the_monitor() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.True(window.HandleShortcut(Key.F11, ModifierKeys.None));

            Assert.True(window.IsFullScreen);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
        });

        [Fact]
        public void Leaving_full_screen_restores_style_state_and_bounds() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Normal;
            window.Left = 120;
            window.Top = 80;
            window.Width = 820;
            window.Height = 560;
            var style = window.WindowStyle;
            var resize = window.ResizeMode;

            window.ToggleFullScreen();
            window.ToggleFullScreen();

            Assert.False(window.IsFullScreen);
            Assert.Equal(style, window.WindowStyle);
            Assert.Equal(resize, window.ResizeMode);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal((120.0, 80.0, 820.0, 560.0), (window.Left, window.Top, window.Width, window.Height));
        });

        [Fact]
        public void A_maximized_window_comes_back_maximized() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Maximized;
            window.ToggleFullScreen();
            window.ToggleFullScreen();
            Assert.Equal(WindowState.Maximized, window.WindowState);
        });

        [Fact]
        public void The_saved_placement_is_the_one_before_full_screen() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.WindowState = WindowState.Normal;
            window.ToggleFullScreen();
            window.PrepareForExit();
            Assert.False(env.Workspace.Session.Maximized);     // full screen is maximized underneath; it must not be saved as such
        });

        [Fact]
        public void The_menu_offers_full_screen_with_its_key() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var menu = window.BuildMainMenu();
            var item = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Full screen");
            Assert.Equal("F11", item.InputGestureText);
            Assert.False(item.IsChecked);
        });
    }
}
```

`BuildMainMenu()` is new: move the body of `OnMenuButtonClick` (everything but `menu.IsOpen = true`) into `internal ContextMenu BuildMainMenu()`, so tests can read the ☰ items; `OnMenuButtonClick` becomes `BuildMainMenu().IsOpen = true;`.

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadFullScreenTests"`). Expected: build FAILS.

- [ ] **Step 3: Implement**

```csharp
        // ---- full screen ---------------------------------------------------------------------

        /// <summary>What full screen replaced, to put back on leaving it; null while windowed.</summary>
        private (WindowStyle Style, ResizeMode Resize, WindowState State, Rect Bounds)? _beforeFullScreen;

        internal bool IsFullScreen => _beforeFullScreen != null;

        /// <summary>
        /// F11 (spec 4.3): no title bar, the whole monitor (a borderless maximized WPF window covers
        /// the taskbar), tabs, find bar and status bar kept. Again: the previous placement. Never
        /// saved — the session keeps the placement from before.
        /// </summary>
        internal void ToggleFullScreen()
        {
            if (_beforeFullScreen is { } before)
            {
                _beforeFullScreen = null;
                WindowState = WindowState.Normal;
                WindowStyle = before.Style;
                ResizeMode = before.Resize;
                Left = before.Bounds.Left;
                Top = before.Bounds.Top;
                Width = before.Bounds.Width;
                Height = before.Bounds.Height;
                WindowState = before.State;
                PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
                return;
            }

            var bounds = WindowState == WindowState.Normal || RestoreBounds.IsEmpty
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _beforeFullScreen = (WindowStyle, ResizeMode, WindowState, bounds);
            WindowState = WindowState.Normal;      // style changes apply cleanly from Normal
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
```

(`Width`/`Height` may be `NaN` on a never-shown window if not set; the tests set them. Guard: if `double.IsNaN(bounds.Width)` use `ActualWidth`/`MinWidth`.)

`CaptureViewState()`: when `_beforeFullScreen is { } fs`, record `session.Maximized = fs.State == WindowState.Maximized` and the bounds from `fs.Bounds` instead of the live window; this must run before the `if (!IsLoaded) return;` check for `Maximized` (so the test sees it headless) — restructure so `Maximized` is recorded from `fs` whenever full screen, and placement bounds only when loaded as today.

`HandleShortcut`: `else if (modifiers == ModifierKeys.None && key == Key.F11) ToggleFullScreen();` (not gated by `EditingKeysAllowed`).

`☰` (in `BuildMainMenu`), after *Always on top*: `menu.Items.Add(Check("Full screen", "F11", IsFullScreen, ToggleFullScreen));`

- [ ] **Step 4: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 5: Document it** — GUIDE `### Links, tabs and view`: `**Full screen**: **F11** hides the title bar and fills the screen; **F11** again brings the window back. MicaPad always reopens windowed.`; README key tables: `| **F11** | Full screen |` / `| **F11** | เต็มจอ |`.

- [ ] **Step 6: Commit**

```bash
git add Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadFullScreenTests.cs
git commit -m "feat(pad): F11 full screen, never saved" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 4: Copy as RTF

**Files:**
- Create: `Services/Pad/RtfWriter.cs`, `Pad/RtfRuns.cs`
- Modify: `Pad/EditorMenus.cs` (item), `Pad/MicaPadWindow.xaml.cs` (☰ item, `CopyAsRtf`, clipboard retry), `GUIDE.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/RtfWriterTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadCopyRtfTests.cs`

**Interfaces:**
- Produces: `public readonly record struct RtfRun(int Start, int Length, PadColor? Foreground, PadColor? Background, bool Bold, bool Italic, bool Strike, double SizeFactor)`; `RtfWriter.Write(string text, IReadOnlyList<RtfRun> runs, string fontFamily, double fontSizePx, PadColor foreground)` → string; `internal static class RtfRuns` — `IReadOnlyList<RtfRun> For(TextDocument document, int start, int length, PadLanguage language, bool markdown)` (offsets relative to `start`); window: `internal Func<IDataObject, bool> TrySetClipboard { get; set; }` (false when busy), `internal void CopyAsRtf()`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/RtfWriterTests.cs`:

```csharp
using System;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Styled text to RTF.</summary>
    public class RtfWriterTests
    {
        private static readonly PadColor Black = PadColor.Parse("#1B1B1F");

        private static string Write(string text, params RtfRun[] runs) => RtfWriter.Write(text, runs, "Cascadia Mono", 14, Black);

        [Fact]
        public void A_document_has_the_font_color_table_and_size()
        {
            string rtf = Write("hi");
            Assert.StartsWith(@"{\rtf1\ansi", rtf);
            Assert.Contains(@"{\fonttbl{\f0\fmodern Cascadia Mono;}}", rtf);
            Assert.Contains(@"\red27\green27\blue31;", rtf);
            Assert.Contains(@"\fs21 ", rtf);              // 14 px = 10.5 pt = 21 half-points
            Assert.EndsWith("}", rtf);
            Assert.Contains("hi", rtf);
        }

        [Fact]
        public void Special_characters_are_escaped()
        {
            string rtf = Write("a\\b{c}d\te");
            Assert.Contains(@"a\\b\{c\}d\tab e", rtf);
        }

        [Theory]
        [InlineData("a\r\nb")]
        [InlineData("a\nb")]
        [InlineData("a\rb")]
        public void Every_line_ending_becomes_a_paragraph(string text)
        {
            string rtf = Write(text);
            Assert.Contains(@"a\par" + "\r\n" + "b", rtf);
        }

        [Fact]
        public void Thai_and_emoji_are_written_as_unicode()
        {
            string rtf = Write("ไทย 😀");
            Assert.Contains(@"\u3652?", rtf);             // ไ U+0E44
            Assert.Contains(@"\u-10179?\u-8704?", rtf);  // 😀 as a UTF-16 surrogate pair, signed
            Assert.DoesNotContain("ไ", rtf);
        }

        [Fact]
        public void Runs_become_bold_italic_strike_color_and_size()
        {
            var red = PadColor.Parse("#C42B1C");
            string rtf = Write("Title body",
                new RtfRun(0, 5, red, null, true, false, false, 1.6),
                new RtfRun(6, 4, null, null, false, true, true, 1));

            Assert.Contains(@"\red196\green43\blue28;", rtf);
            Assert.Matches(@"\\cf2\\b\\fs34 Title", rtf);      // 21 * 1.6 = 33.6, rounded to 34
            Assert.Matches(@"\\i\\strike\\fs21 body|\\i\\strike body", rtf);
        }

        [Fact]
        public void Overlapping_runs_combine_and_a_later_color_wins()
        {
            var a = PadColor.Parse("#111111");
            var b = PadColor.Parse("#222222");
            string rtf = Write("xyz",
                new RtfRun(0, 3, a, null, true, false, false, 1),
                new RtfRun(1, 1, b, null, false, true, false, 1));
            // x: bold a; y: bold italic b; z: bold a
            Assert.Matches(@"\\cf2\\b\\fs21 x", rtf);
            Assert.Matches(@"\\cf3\\b\\i\\fs21 y", rtf);
        }

        [Fact]
        public void A_background_uses_character_shading()
        {
            var shade = PadColor.Parse("#EEEEF2");
            string rtf = Write("code", new RtfRun(0, 4, null, shade, false, false, false, 1));
            Assert.Contains(@"\chcbpat2", rtf);
        }
    }
}
```

(The exact control-word order in the last three tests follows the writer below: color, then `\b`, `\i`, `\strike`, `\chcbpat`, then `\fs`, then a space before the text. `\cf1` is the default foreground at color table index 1.)

`tests/Kil0bitSystemMonitor.Tests/PadCopyRtfTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Copy as RTF from the window: what is copied, in which colors, and a busy clipboard.</summary>
    public class PadCopyRtfTests
    {
        [Fact]
        public void The_selection_is_copied_as_rtf_and_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            window.Editor.Document.Text = "# Title\n**bold** rest";
            window.Editor.Select(8, 8);

            window.CopyAsRtf();

            Assert.Equal("**bold**", copied!.GetData(DataFormats.UnicodeText));
            string rtf = (string)copied.GetData(DataFormats.Rtf);
            Assert.Contains(@"\b", rtf);
            Assert.Contains("bold", rtf);
        });

        [Fact]
        public void Nothing_selected_copies_the_whole_note() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            window.Editor.Document.Text = "one\ntwo";
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            Assert.Equal("one\ntwo", copied!.GetData(DataFormats.UnicodeText));
        });

        [Fact]
        public void Colors_come_from_the_light_palette_even_in_the_dark_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{ \"key\": \"value\" }");
            window.Editor.Select(0, 0);
            Assert.True(window.Palette.IsDark);

            window.CopyAsRtf();

            string rtf = (string)copied!.GetData(DataFormats.Rtf);
            var light = PadPalette.Light.SyntaxString;
            Assert.Contains($@"\red{light.R}\green{light.G}\blue{light.B};", rtf);
            var dark = PadPalette.Dark.SyntaxString;
            Assert.DoesNotContain($@"\red{dark.R}\green{dark.G}\blue{dark.B};", rtf);
        });

        [Fact]
        public void A_busy_clipboard_is_retried_then_reported() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int attempts = 0;
            window.TrySetClipboard = data => { attempts++; return false; };
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(4, attempts);                          // the first try and three retries
            Assert.Equal("Clipboard busy, try again", window.StatusMessage.Text);
        });

        [Fact]
        public void A_clipboard_that_frees_up_is_used() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int attempts = 0;
            window.TrySetClipboard = data => ++attempts >= 2;
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(2, attempts);
            Assert.NotEqual(Visibility.Visible, window.StatusMessage.Visibility);
        });

        [Fact]
        public void Both_menus_offer_copy_as_rtf() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.RefreshEditorMenu();
            Assert.Contains("Copy as RTF", PadMenuTests.Headers(window.EditorMenu));
            Assert.Contains("Copy as RTF", PadMenuTests.Headers(window.BuildMainMenu()));
        });
    }
}
```

(`PadMenuTests.Headers` is `internal static`; if it is private, make it internal. `BuildMainMenu` comes from Task 3. Update `PadMenuTests.The_editor_menu_lists_edit_then_find_items` for the new item: `"Copy", "Copy as RTF", "Paste"`.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~RtfWriterTests|FullyQualifiedName~PadCopyRtfTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/RtfWriter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A styled stretch of text for <see cref="RtfWriter"/>; runs may overlap (later ones win on color).</summary>
    public readonly record struct RtfRun(int Start, int Length, PadColor? Foreground, PadColor? Background,
                                         bool Bold, bool Italic, bool Strike, double SizeFactor);

    /// <summary>
    /// Text plus styled runs → RTF (spec 4.4): one font, a color table, bold, italic, strike,
    /// character shading and relative sizes. Every character outside ASCII is written as \uN? so
    /// Thai and emoji survive; line endings of any kind become \par.
    /// </summary>
    public static class RtfWriter
    {
        private readonly record struct Style(PadColor Foreground, PadColor? Background, bool Bold, bool Italic, bool Strike, double Size);

        public static string Write(string text, IReadOnlyList<RtfRun> runs, string fontFamily, double fontSizePx, PadColor foreground)
        {
            var styles = new Style[text.Length];
            var plain = new Style(foreground, null, false, false, false, 1);
            for (int i = 0; i < styles.Length; i++) styles[i] = plain;
            foreach (var run in runs)
            {
                int end = Math.Min(text.Length, run.Start + run.Length);
                for (int i = Math.Max(0, run.Start); i < end; i++)
                {
                    var s = styles[i];
                    styles[i] = new Style(
                        run.Foreground ?? s.Foreground,
                        run.Background ?? s.Background,
                        s.Bold || run.Bold,
                        s.Italic || run.Italic,
                        s.Strike || run.Strike,
                        run.SizeFactor != 1 ? run.SizeFactor : s.Size);
                }
            }

            var colors = new List<PadColor> { foreground };
            int ColorIndex(PadColor c)
            {
                int i = colors.IndexOf(c);
                if (i < 0) { colors.Add(c); i = colors.Count - 1; }
                return i + 1;                                     // \colortbl starts with the "auto" entry
            }

            int baseHalfPoints = (int)Math.Round(fontSizePx * 0.75 * 2, MidpointRounding.AwayFromZero);
            var body = new StringBuilder();
            Style? current = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    body.Append(@"\par").Append("\r\n");
                    continue;
                }

                var style = styles[i];
                if (current != style)
                {
                    body.Append(@"\plain\f0")
                        .Append(@"\cf").Append(ColorIndex(style.Foreground).ToString(CultureInfo.InvariantCulture));
                    if (style.Bold) body.Append(@"\b");
                    if (style.Italic) body.Append(@"\i");
                    if (style.Strike) body.Append(@"\strike");
                    if (style.Background is PadColor bg) body.Append(@"\chcbpat").Append(ColorIndex(bg).ToString(CultureInfo.InvariantCulture));
                    int halfPoints = (int)Math.Round(baseHalfPoints * style.Size, MidpointRounding.AwayFromZero);
                    body.Append(@"\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    current = style;
                }

                switch (c)
                {
                    case '\\': body.Append(@"\\"); break;
                    case '{': body.Append(@"\{"); break;
                    case '}': body.Append(@"\}"); break;
                    case '\t': body.Append(@"\tab "); break;
                    default:
                        if (c < 0x80) body.Append(c);
                        else body.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                        break;
                }
            }

            var rtf = new StringBuilder();
            rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0");
            rtf.Append(@"{\fonttbl{\f0\fmodern ").Append(Escape(fontFamily)).Append(";}}");
            rtf.Append(@"{\colortbl ;");
            foreach (var c in colors)
                rtf.Append(@"\red").Append(c.R.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\green").Append(c.G.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\blue").Append(c.B.ToString(CultureInfo.InvariantCulture)).Append(';');
            rtf.Append('}');
            rtf.Append(@"\f0\fs").Append(baseHalfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ');
            rtf.Append(body);
            rtf.Append('}');
            return rtf.ToString();
        }

        private static string Escape(string s) => s.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}");
    }
}
```

Note: the color table is built while the body is written, so the header is assembled after the body — as above. The first test's `\fs21 ` comes from the header; the run tests' `\fs34 Title` from the body. The default foreground is `\cf1`; the first extra color is `\cf2`.

- [ ] **Step 4: Write `Pad/RtfRuns.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The styled runs MicaPad shows for a stretch of a document, in the <b>light</b> palette
    /// (spec 4.4: RTF is pasted onto white pages). Syntax colors come from the language's
    /// highlighting definition through <see cref="SyntaxColors"/>; Markdown from the tokenizer and
    /// <see cref="MarkdownStyles"/>. Plain text has none. Offsets are relative to <c>start</c>.
    /// </summary>
    internal static class RtfRuns
    {
        public static IReadOnlyList<RtfRun> For(TextDocument document, int start, int length, PadLanguage language, bool markdown)
        {
            var palette = PadPalette.Light;
            var runs = new List<RtfRun>();
            if (length == 0 || document.TextLength > PadLanguages.MaxFormattedChars) return runs;

            int end = start + length;
            int first = document.GetLineByOffset(start).LineNumber;
            int last = document.GetLineByOffset(end).LineNumber;

            if (markdown)
            {
                var lines = Enumerable.Range(1, document.LineCount).Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
                var fences = FenceTracker.Classify(lines);
                for (int n = first; n <= last; n++)
                {
                    var line = document.GetLineByNumber(n);
                    var tokens = MarkdownLineTokenizer.Tokenize(lines[n - 1], fences[n - 1]);
                    foreach (var span in tokens.Spans)
                    {
                        var look = MarkdownStyles.LookOf(span.Style, palette);
                        Add(runs, line.Offset + span.Start, span.Length, start, end,
                            look.Foreground, look.Background, look.Weight == MdWeight.Bold || look.Weight == MdWeight.SemiBold,
                            look.Italic, look.Strike, look.SizeFactor);
                    }
                }
                return runs;
            }

            if (PadHighlighting.For(language) is not { } definition) return runs;
            var highlighter = new DocumentHighlighter(document, definition);
            try
            {
                for (int n = first; n <= last; n++)
                {
                    var highlighted = highlighter.HighlightLine(n);
                    foreach (var section in highlighted.Sections)
                    {
                        var color = section.Color;
                        PadColor? original = color.Foreground?.GetColor(null) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
                        var paint = SyntaxColors.Resolve(color.Name, original, palette);
                        Add(runs, section.Offset, section.Length, start, end, paint, null,
                            color.FontWeight is { } w && w.ToOpenTypeWeight() >= 600,
                            color.FontStyle is { } s && s != System.Windows.FontStyles.Normal,
                            color.Strikethrough == true, 1);
                    }
                }
            }
            finally
            {
                highlighter.Dispose();
            }
            return runs;
        }

        private static void Add(List<RtfRun> runs, int offset, int length, int start, int end,
                                PadColor? fg, PadColor? bg, bool bold, bool italic, bool strike, double size)
        {
            int from = Math.Max(offset, start);
            int to = Math.Min(offset + length, end);
            if (to <= from) return;
            runs.Add(new RtfRun(from - start, to - from, fg, bg, bold, italic, strike, size));
        }
    }
}
```

(Check the real names before writing: `MdLook` fields `Foreground`, `Background`, `SizeFactor`, `Weight`, `Italic`, `Strike`; `MdWeight` values; `MarkdownLineTokenizer.Tokenize(string, MdFence)` returns `MdLine` with `Spans`; `FenceTracker.Classify(IReadOnlyList<string>)` returns `MdFence[]`; `PadHighlighting.For(PadLanguage)`; `HighlightedLine.Sections` with `Offset`, `Length`, `Color`. Adjust to what the code has; say so in the report.)

- [ ] **Step 5: The window and menus**

In `Pad/MicaPadWindow.xaml.cs`:

```csharp
        // ---- copy as RTF ---------------------------------------------------------------------

        /// <summary>Puts data on the clipboard; false when the clipboard is busy. Tests replace it.</summary>
        internal Func<IDataObject, bool> TrySetClipboard { get; set; } = data =>
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        };

        /// <summary>
        /// Copies the selection (or the whole note) as RTF and plain text, styled as shown, in the
        /// light palette (spec 4.4). A busy clipboard is tried again three times over 300 ms.
        /// </summary>
        internal void CopyAsRtf()
        {
            var document = Editor.Document;
            int start = Editor.SelectionLength > 0 ? Editor.SelectionStart : 0;
            int length = Editor.SelectionLength > 0 ? Editor.SelectionLength : document.TextLength;
            string text = document.GetText(start, length);

            var effective = _resolved.Effective;
            bool markdown = ReferenceEquals(effective, PadLanguages.Markdown);
            var runs = RtfRuns.For(document, start, length, effective, markdown);
            // The editor font at its own size: Editor.FontSize includes the window's zoom.
            string font = _config.PadFontFamily.Split(',')[0].Trim();
            string rtf = RtfWriter.Write(text, runs, font, _config.PadFontSize, PadPalette.Light.Text);

            var data = new DataObject();
            data.SetData(DataFormats.Rtf, rtf);
            data.SetData(DataFormats.UnicodeText, text);

            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (TrySetClipboard(data)) return;
                if (attempt < 3) System.Threading.Thread.Sleep(100);
            }
            ShowStatus("Clipboard busy, try again");
        }
```

(`ApplyEditorSettings` sets `Editor.FontSize = _config.PadFontSize * Session.Zoom`, hence the config values above.)

Menus: in `EditorMenus.AddEditGroup`, after *Copy* and only when not read-only, an item is not possible without the window — instead add `Item("Copy as RTF", null, CopyAsRtf)` in the window's `FillEditorMenu` right after `AddEditGroup` by inserting it at the index after "Copy" (find the "Copy" item's index and `Insert(index + 1, ...)`), for the main editor only. In `BuildMainMenu`, after *Save As…*: `menu.Items.Add(Item("Copy as RTF", null, CopyAsRtf));`.

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 7: Document it** — GUIDE `### Links, tabs and view`: `**Copy as RTF** (right-click or **☰**): copies the selection — or the whole note — with its colors, bold, italic and heading sizes, ready to paste into Word or Outlook. It always uses light-theme colors, for white pages.`

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/RtfWriter.cs Pad/RtfRuns.cs Pad/EditorMenus.cs Pad/MicaPadWindow.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/RtfWriterTests.cs tests/Kil0bitSystemMonitor.Tests/PadCopyRtfTests.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): Copy as RTF in the light palette, for Word and Outlook" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

## After the plan (controller)

1. Whole suite green (three consecutive runs); build 0 warnings.
2. Deploy for the owner's e2e: stop MicaStats, build Release into `bin\Release\net8.0-windows`, relaunch.
3. Owner's manual checks (spec manual item 5): Ctrl+Click a link; a `file:` link does nothing; drag tabs; F11; paste Copy as RTF into Word.
