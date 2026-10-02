# Scrolling Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new **Scrolling** capture mode. It scrolls the area the user picks from top to bottom with real mouse-wheel input, joins the frames into one tall image, and opens that image in the annotation editor.

**Architecture:**
- **Pure units** in `Services/Capture/`:
  - `PixelFrame`, the pixels of one frame;
  - `ScrollStitcher`, which joins frames by row hashes and handles sticky headers and footers;
  - `ScrollCaptureRun`, the loop, built on an injected target, wait and cancellation;
  - `ScrollStatusPlacement`, which places the status card.
- **Win32 adapter:** `ScreenScrollTarget`, which grabs with BitBlt and scrolls with SendInput.
- **Integration:** `CaptureService` adds a Scrolling case. Hotkeys, the overlay menu and Settings get one entry each.

**Tech Stack:** C# / .NET 8 WPF, Win32 (`SendInput`, `SetCursorPos`, `RegisterHotKey`), xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-02-scrolling-capture-design.md`

## Global Constraints

- Tests never move the real pointer, never send real input, never grab the real screen, never touch `%APPDATA%`, never launch MicaStats. Everything that touches Win32 sits behind `IScrollTarget`. Tests use fakes.
- Never build into `bin\Release`. Build and test with the user-local SDK: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~<Class>"`.
- UI tests run only on the shared UI thread: `UiThread.Run(() => { ... })`.
- The build must gain no warnings; only the 4 old xUnit1031 remain.
  - Never call `Task.Wait`, `.Result` or `GetAwaiter().GetResult()` in a test body.
  - Never compare a `string[]` with a `List<string>` in `Assert.Equal`.
  - This machine runs th-TH: pass `StringComparison.Ordinal` to string `IndexOf`, `Contains` and `StartsWith`, and format numbers with `CultureInfo.InvariantCulture`.
- Values from the spec, verbatim:
  - areas under 50 × 50 px are refused;
  - the rightmost 24 columns are ignored when matching;
  - a match needs 70% of the overlapping rows;
  - at least 8 overlap rows;
  - scroll to the top with up to 300 notches;
  - one notch (120 wheel units) per step down;
  - settle by polling every 50 ms, giving up after 800 ms;
  - stop at 20,000 px tall or after 500 steps;
  - default shortcut **Ctrl+Shift+4**.
- Logs (area `capture`) carry counts, sizes and the stop reason only, never image content.
- Commits: explicit `git add <files>`, `git commit -m`, never amend. The message ends with `Co-Authored-By: <model> <noreply@anthropic.com>`.

## Rulings in this plan (beyond the spec)

- File names keep the `{mode}` value `capture`, like every other mode (`CaptureService.Finish` already passes `"capture"` for all of them). The spec line about `scrolling` is dropped.
- Scrolling to the top sends 10 notches per step, up to 300 notches in all. One notch at a time would take minutes on a long page.
- Uniform rows (one color across the matched columns, such as blank lines) are ignored when matching. Otherwise blank space would match at every shift.
- If shift 0 scores at least as well as the best shift, the step counts as the end of the content (Unchanged), not a match. A caret blinking in a view that did not move must not count as a scroll.

## Review Focus

1. **A long blank gap taller than the view**, such as a page with a big empty section. Frames during the gap are identical, so capture stops there. That is acceptable, and the stop reason "end of content" is recorded. Test `Identical_frames_end_the_capture`.
2. **Smooth-scrolling apps that are still moving when grabbed.** Settling must wait until two grabs agree. Test `An_animated_scroll_is_settled_before_joining`.
3. **A page with a sticky header and a fixed footer.** Each appears exactly once in the result. Test `A_sticky_header_and_a_fixed_footer_appear_once`.
4. **Esc pressed while scrolling.** Capture stops and keeps what was joined. Esc pressed during the scroll to the top cancels with no image. Tests `Cancel_while_scrolling_keeps_what_was_joined` and `Cancel_during_scroll_to_top_cancels`.
5. **A view that changes in a way that cannot be joined** (a carousel, a video). Capture stops with NoMatch instead of producing garbage. Test `An_unrelated_frame_stops_with_no_match`.

---

### Task 1: Frames and the stitcher

**Files:**
- Create: `Services/Capture/PixelFrame.cs`
- Create: `Services/Capture/ScrollStitcher.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/ScrollStitcherTests.cs`

**Interfaces (produces):**
- `public sealed class PixelFrame { PixelFrame(int width, int height, int[] pixels); int Width; int Height; int[] Pixels; ReadOnlySpan<int> Row(int y); bool SameAs(PixelFrame other); static PixelFrame Stack(IReadOnlyList<int[]> rows, int width); }` stores pixels as 32-bit ARGB, row-major.
- `public enum StitchStep { Appended, Unchanged, NoMatch, WidthChanged }`
- `public sealed class ScrollStitcher { const int IgnoreRightColumns = 24; const double MatchThreshold = 0.70; const int MinOverlapRows = 8; const int MinBandRows = 16; ScrollStitcher(PixelFrame first); int Height { get; } StitchStep Add(PixelFrame next, out int addedRows); PixelFrame Result(int maxHeight = int.MaxValue); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Linq;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class ScrollStitcherTests
    {
        private const int W = 120;

        /// <summary>A tall page whose every row is distinct (two colors per row, at a seeded position).</summary>
        internal static PixelFrame Page(int height, int seed = 7)
        {
            var rnd = new Random(seed);
            var px = new int[W * height];
            for (int y = 0; y < height; y++)
            {
                int bg = unchecked((int)0xFFFFFFFF), ink = unchecked((int)0xFF000000) | rnd.Next(0xFFFFFF);
                int a = rnd.Next(W - 40), b = a + 1 + rnd.Next(30);
                for (int x = 0; x < W; x++) px[y * W + x] = x >= a && x <= b ? ink : bg;
            }
            return new PixelFrame(W, height, px);
        }

        /// <summary>The view of <paramref name="page"/> from row <paramref name="top"/>, <paramref name="height"/> rows tall.</summary>
        internal static PixelFrame View(PixelFrame page, int top, int height)
        {
            var px = new int[W * height];
            Array.Copy(page.Pixels, top * W, px, 0, W * height);
            return new PixelFrame(W, height, px);
        }

        private static PixelFrame WithBand(PixelFrame frame, int fromRow, int rows, int color)
        {
            var px = (int[])frame.Pixels.Clone();
            for (int y = fromRow; y < fromRow + rows; y++)
                for (int x = 0; x < W; x++) px[y * W + x] = color;
            return new PixelFrame(W, frame.Height, px);
        }

        [Fact]
        public void A_plain_scroll_joins_exactly()
        {
            var page = Page(1000);
            var s = new ScrollStitcher(View(page, 0, 300));
            for (int top = 90; top <= 630; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(View(page, top, 300), out _));
            Assert.Equal(StitchStep.Appended, s.Add(View(page, 700, 300), out int last));   // the last step is shorter
            Assert.Equal(70, last);
            Assert.Equal(StitchStep.Unchanged, s.Add(View(page, 700, 300), out _));          // the end of the page

            var result = s.Result();
            Assert.True(result.SameAs(View(page, 0, result.Height)));
            Assert.Equal(1000, result.Height);
        }

        [Fact]
        public void Identical_frames_end_the_capture()
        {
            var page = Page(400);
            var s = new ScrollStitcher(View(page, 0, 300));
            Assert.Equal(StitchStep.Unchanged, s.Add(View(page, 0, 300), out int added));
            Assert.Equal(0, added);
            Assert.Equal(300, s.Height);
        }

        [Fact]
        public void A_sticky_header_and_a_fixed_footer_appear_once()
        {
            var page = Page(1200);
            int header = unchecked((int)0xFF3366CC), footer = unchecked((int)0xFFCC6633);
            PixelFrame Framed(int top) => WithBand(WithBand(View(page, top, 300), 0, 20, header), 285, 15, footer);
            // Header and footer must differ row by row to be recognisable: give them a second color stripe.
            var s = new ScrollStitcher(Framed(0));
            for (int top = 80; top <= 900; top += 80) s.Add(Framed(top), out _);

            var result = s.Result();
            int headerRows = Enumerable.Range(0, result.Height).Count(y => result.Row(y)[0] == header);
            int footerRows = Enumerable.Range(0, result.Height).Count(y => result.Row(y)[0] == footer);
            Assert.Equal(20, headerRows);
            Assert.Equal(15, footerRows);
            Assert.Equal(header, result.Row(0)[0]);
            Assert.Equal(footer, result.Row(result.Height - 1)[0]);
        }

        [Fact]
        public void A_scrollbar_thumb_in_the_rightmost_columns_does_not_break_the_match()
        {
            var page = Page(800);
            PixelFrame WithThumb(PixelFrame f, int at)
            {
                var px = (int[])f.Pixels.Clone();
                for (int y = 0; y < f.Height; y++)
                    for (int x = W - 12; x < W; x++) px[y * W + x] = y >= at && y < at + 40 ? unchecked((int)0xFF888888) : unchecked((int)0xFFEEEEEE);
                return new PixelFrame(W, f.Height, px);
            }
            var s = new ScrollStitcher(WithThumb(View(page, 0, 300), 0));
            Assert.Equal(StitchStep.Appended, s.Add(WithThumb(View(page, 100, 300), 37), out int added));
            Assert.Equal(100, added);
        }

        [Fact]
        public void A_small_change_such_as_a_caret_still_joins()
        {
            var page = Page(800);
            var next = View(page, 120, 300);
            var px = (int[])next.Pixels.Clone();
            for (int y = 40; y < 52; y++) px[y * W + 5] = unchecked((int)0xFFFF0000);   // a caret in 12 rows
            var s = new ScrollStitcher(View(page, 0, 300));
            Assert.Equal(StitchStep.Appended, s.Add(new PixelFrame(W, 300, px), out int added));
            Assert.Equal(120, added);
        }

        [Fact]
        public void An_unrelated_frame_stops_with_no_match()
        {
            var s = new ScrollStitcher(View(Page(800, seed: 1), 0, 300));
            Assert.Equal(StitchStep.NoMatch, s.Add(View(Page(800, seed: 2), 0, 300), out _));
        }

        [Fact]
        public void A_frame_of_another_size_is_refused()
        {
            var s = new ScrollStitcher(View(Page(800), 0, 300));
            Assert.Equal(StitchStep.WidthChanged, s.Add(new PixelFrame(W, 299, new int[W * 299]), out _));
            Assert.Equal(StitchStep.WidthChanged, s.Add(new PixelFrame(W + 10, 300, new int[(W + 10) * 300]), out _));
        }

        [Fact]
        public void A_large_scroll_with_a_small_overlap_still_joins()
        {
            var page = Page(800);
            var s = new ScrollStitcher(View(page, 0, 300));
            Assert.Equal(StitchStep.Appended, s.Add(View(page, 290, 300), out int added));   // 10 rows of overlap
            Assert.Equal(290, added);
            Assert.True(s.Result().SameAs(View(page, 0, 590)));
        }

        [Fact]
        public void Blank_rows_do_not_create_false_matches()
        {
            var page = Page(900);
            var px = (int[])page.Pixels.Clone();
            for (int y = 300; y < 360; y++) for (int x = 0; x < W; x++) px[y * W + x] = unchecked((int)0xFFFFFFFF);   // a blank gap
            var gapped = new PixelFrame(W, 900, px);
            var s = new ScrollStitcher(View(gapped, 100, 300));
            Assert.Equal(StitchStep.Appended, s.Add(View(gapped, 190, 300), out int added));
            Assert.Equal(90, added);
        }

        [Fact]
        public void Result_is_cut_at_the_height_cap()
        {
            var page = Page(1000);
            var s = new ScrollStitcher(View(page, 0, 300));
            s.Add(View(page, 200, 300), out _);
            Assert.Equal(450, s.Result(maxHeight: 450).Height);
        }
    }
}
```

Note for the implementer: `A_sticky_header_and_a_fixed_footer_appear_once` gives the header and footer one solid color each. Those rows are identical between frames at the same position, so the stitcher recognises them as static.

- [ ] **Step 2: Run the tests and see them fail.** Run `... --filter "FullyQualifiedName~ScrollStitcherTests"`. It should fail to build, because `PixelFrame` is missing.

- [ ] **Step 3: Write `PixelFrame.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>One captured frame: 32-bit ARGB pixels, row by row from the top.</summary>
    public sealed class PixelFrame
    {
        public PixelFrame(int width, int height, int[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels.Length != width * height)
                throw new ArgumentException("The pixel count must be width × height.", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public int[] Pixels { get; }

        public ReadOnlySpan<int> Row(int y) => Pixels.AsSpan(y * Width, Width);

        public bool SameAs(PixelFrame other) =>
            Width == other.Width && Height == other.Height && Pixels.AsSpan().SequenceEqual(other.Pixels);

        /// <summary>Rows (each <paramref name="width"/> pixels) stacked into one frame.</summary>
        public static PixelFrame Stack(IReadOnlyList<int[]> rows, int width)
        {
            var px = new int[rows.Count * width];
            for (int y = 0; y < rows.Count; y++) Array.Copy(rows[y], 0, px, y * width, width);
            return new PixelFrame(width, rows.Count, px);
        }
    }
}
```

- [ ] **Step 4: Write `ScrollStitcher.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What adding a frame did.</summary>
    public enum StitchStep { Appended, Unchanged, NoMatch, WidthChanged }

    /// <summary>
    /// Joins the frames of a scrolling capture into one tall image (scrolling capture spec 4).
    /// Rows that stay put between the first two frames at the top and bottom are a header and
    /// a footer: the header is kept from the first frame, the footer from the last.
    ///
    /// <para>
    /// Between them, the shift is the one whose overlapping rows match best. Rows are compared
    /// by a hash that leaves out the rightmost <see cref="IgnoreRightColumns"/> pixels (a
    /// scrollbar), and uniform rows such as blank lines are not counted. A shift needs at least
    /// <see cref="MatchThreshold"/> of the counted rows to match, and at least
    /// <see cref="MinOverlapRows"/> counted rows.
    /// </para>
    /// </summary>
    public sealed class ScrollStitcher
    {
        public const int IgnoreRightColumns = 24;
        public const double MatchThreshold = 0.70;
        public const int MinOverlapRows = 8;
        public const int MinBandRows = 16;

        private readonly int _width;
        private readonly int _height;
        private readonly List<int[]> _band = new();
        private int[][] _header = Array.Empty<int[]>();
        private int[][] _footer = Array.Empty<int[]>();
        private PixelFrame _last;
        private int _top = -1, _bottom = -1;

        public ScrollStitcher(PixelFrame first)
        {
            _width = first.Width;
            _height = first.Height;
            _last = first;
        }

        /// <summary>The joined height so far.</summary>
        public int Height => _top < 0 ? _height : _header.Length + _band.Count + _footer.Length;

        public StitchStep Add(PixelFrame next, out int addedRows)
        {
            addedRows = 0;
            if (next.Width != _width || next.Height != _height) return StitchStep.WidthChanged;
            if (next.SameAs(_last)) return StitchStep.Unchanged;

            ulong[] prev = Hashes(_last), cur = Hashes(next);
            if (_top < 0)
            {
                int top = 0;
                while (top < _height && prev[top] == cur[top]) top++;
                int bottom = 0;
                while (bottom < _height - top && prev[_height - 1 - bottom] == cur[_height - 1 - bottom]) bottom++;
                if (_height - top - bottom < MinBandRows) { top = 0; bottom = 0; }
                _top = top;
                _bottom = bottom;
                _header = Rows(_last, 0, top);
                _band.AddRange(Rows(_last, top, _height - bottom));
                _footer = Rows(_last, _height - bottom, _height);
            }

            int band = _height - _top - _bottom;
            bool[] informative = Informative(next, _top, band);
            int dy = FindShift(prev.AsSpan(_top, band), cur.AsSpan(_top, band), informative);
            if (dy == 0) return StitchStep.Unchanged;
            if (dy < 0) return StitchStep.NoMatch;

            _band.AddRange(Rows(next, _top + band - dy, _top + band));
            _footer = Rows(next, _height - _bottom, _height);
            _last = next;
            addedRows = dy;
            return StitchStep.Appended;
        }

        /// <summary>Header, joined band, footer; the band is cut so the whole is at most <paramref name="maxHeight"/> rows.</summary>
        public PixelFrame Result(int maxHeight = int.MaxValue)
        {
            if (_top < 0) return _last;
            var rows = new List<int[]>(_header);
            int room = Math.Max(0, maxHeight - _header.Length - _footer.Length);
            for (int i = 0; i < _band.Count && i < room; i++) rows.Add(_band[i]);
            rows.AddRange(_footer);
            return PixelFrame.Stack(rows, _width);
        }

        /// <summary>
        /// The shift (rows the content moved up) that best lines <paramref name="next"/> up with
        /// <paramref name="prev"/>: 0 when not moving fits as well as any shift, -1 when none fits.
        /// </summary>
        internal static int FindShift(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> next, bool[] nextInformative)
        {
            int band = prev.Length;
            int best = -1, bestMatches = 0;
            double bestRatio = 0;
            for (int dy = 0; dy <= band - MinOverlapRows; dy++)
            {
                int counted = 0, matches = 0;
                for (int i = 0; i < band - dy; i++)
                {
                    if (!nextInformative[i]) continue;
                    counted++;
                    if (next[i] == prev[i + dy]) matches++;
                }
                if (counted < MinOverlapRows) continue;
                double ratio = (double)matches / counted;
                if (ratio < MatchThreshold) continue;
                if (best < 0 || ratio > bestRatio + 1e-9 || (Math.Abs(ratio - bestRatio) <= 1e-9 && matches > bestMatches))
                {
                    best = dy;
                    bestRatio = ratio;
                    bestMatches = matches;
                }
            }
            return best;
        }

        private ulong[] Hashes(PixelFrame frame)
        {
            int columns = _width > 2 * IgnoreRightColumns ? _width - IgnoreRightColumns : _width;
            var hashes = new ulong[frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                ulong h = 14695981039346656037UL;
                var row = frame.Row(y);
                for (int x = 0; x < columns; x++)
                {
                    h ^= (uint)row[x];
                    h *= 1099511628211UL;
                }
                hashes[y] = h;
            }
            return hashes;
        }

        private bool[] Informative(PixelFrame frame, int from, int count)
        {
            int columns = _width > 2 * IgnoreRightColumns ? _width - IgnoreRightColumns : _width;
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var row = frame.Row(from + i);
                int first = row[0];
                for (int x = 1; x < columns; x++)
                    if (row[x] != first) { result[i] = true; break; }
            }
            return result;
        }

        private static int[][] Rows(PixelFrame frame, int from, int to)
        {
            var rows = new int[Math.Max(0, to - from)][];
            for (int y = from; y < to; y++) rows[y - from] = frame.Row(y).ToArray();
            return rows;
        }
    }
}
```

Note for the implementer, on `FindShift` and dy=0: a view that did not move scores perfectly at dy=0. That returns Unchanged, which is right. A view that did move scores best at its real shift. Because the comparison is "strictly better", ties at dy=0 win, so a static view never counts as moving.

Check this against `A_sticky_header_and_a_fixed_footer_appear_once`. In the band, the band rows at dy=0 must not match better than the real shift. Page rows are distinct, so they don't. If the test fails, investigate before changing thresholds.

- [ ] **Step 5: Run the tests and see them pass.** The same filter must pass, then the full suite once.
- [ ] **Step 6: Commit:** `feat(capture): frames and a stitcher that joins scrolled frames, sticky header and footer once`

---

### Task 2: The capture loop (`ScrollCaptureRun`)

**Files:**
- Create: `Services/Capture/ScrollCaptureRun.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/ScrollCaptureRunTests.cs`

**Interfaces:**
- Consumes `PixelFrame` and `ScrollStitcher` from Task 1.
- Produces:
  - `public interface IScrollTarget { PixelFrame Grab(); void Scroll(int notches); }`, where a positive value scrolls down (content moves up) and a negative value scrolls up.
  - `public enum ScrollStop { End, MaxHeight, MaxSteps, Cancelled, NoMatch, SizeChanged, Unscrollable }`
  - `public sealed record ScrollCaptureResult(PixelFrame? Image, ScrollStop Stop, int Frames);`
  - `public sealed record ScrollCaptureOptions(int MaxHeight = 20000, int MaxSteps = 500, int MaxTopNotches = 300, int TopNotchesPerStep = 10, int SettlePollMs = 50, int SettleTimeoutMs = 800);`
  - `public sealed class ScrollCaptureRun { ScrollCaptureRun(IScrollTarget target, Action<int> wait, Func<bool> cancelled, Action<int>? progress = null, ScrollCaptureOptions? options = null); ScrollCaptureResult Run(); }`

Behavior (spec 3):
1. **Settle.** Grab, then repeat: wait `SettlePollMs` and grab again, until two grabs agree or `SettleTimeoutMs` has passed in total waiting. Return the last grab.
2. **Scroll to the top.** Repeat until the frame stops changing or `MaxTopNotches` have been sent: settle, `Scroll(-TopNotchesPerStep)`, settle, and compare. If cancelled here, return `(null, Cancelled, 0)`.
3. **Capture down.** Start the stitcher with the settled top frame. Each step: `Scroll(1)`, settle, then `Add`:
   - `Unchanged` stops with End.
   - `NoMatch` stops with NoMatch.
   - `WidthChanged` stops with SizeChanged.
   - `Appended` reports progress with the joined height.

   Stop with MaxHeight when the height reaches `MaxHeight`, with MaxSteps after `MaxSteps` steps, and with Cancelled when `cancelled()` returns true. In each of those cases, return the joined image so far, cut to `MaxHeight`.
4. **Unscrollable.** If the very first step down returns Unchanged, the area did not scroll at all. Return the single frame with `Unscrollable`.

- [ ] **Step 1: Write the failing tests** with a `FakePage : IScrollTarget`:
  - The content is `ScrollStitcherTests.Page(n)`, an `internal static` helper.
  - The view is height `v` at offset `top`.
  - `Scroll(n)` moves `top` by `n * notchRows`, clamped to `[0, content - v]`.
  - `Grab()` returns `ScrollStitcherTests.View(content, top, v)`.
  - An optional `animate` flag makes the first grab after a scroll show the view halfway between the old and the new offset; later grabs show the final offset.
  - `wait` is a no-op that adds to a counter. `cancelled` is a settable `Func`.

  Tests:
  - `Starts_at_the_top_and_captures_the_whole_page`: content 2000 rows, view 300 rows, start offset 900, notch 90 rows. The result equals the content, and Stop is `End`.
  - `An_animated_scroll_is_settled_before_joining`: same setup with `animate` on. The result still equals the content.
  - `Stops_at_the_height_cap`: content 3000 rows with `MaxHeight` 1000. The result is 1000 rows, and Stop is `MaxHeight`.
  - `Stops_at_the_step_cap`: `MaxSteps` 3 gives Stop `MaxSteps`.
  - `Cancel_while_scrolling_keeps_what_was_joined`: cancel after the 4th grab during capture. Stop is `Cancelled`, and the image is not null and taller than the view.
  - `Cancel_during_scroll_to_top_cancels`: cancel before anything else. Stop is `Cancelled`, and the image is null.
  - `An_area_that_does_not_scroll_returns_its_one_frame`: content equals the view. Stop is `Unscrollable`, and the image is 300 rows.
  - `A_view_that_changes_unrelatedly_stops_with_no_match`: after the 2nd step, `Grab` returns a different page. Stop is `NoMatch`, and the image is not null.
  - `Progress_reports_the_joined_height`: the reported heights increase.

- [ ] **Step 2: Run and see them fail. Step 3: Implement exactly the behavior above.** Use a private `Settle()` helper. Keep the class free of Win32 and WPF.
- [ ] **Step 4: Run and see them pass.** Then run the full suite once.
- [ ] **Step 5: Commit:** `feat(capture): the scrolling capture loop - to the top, settle, step down, stop at the end or a cap`

---

### Task 3: Win32 adapter, status card and integration

**Files:**
- Create: `Services/Capture/ScreenScrollTarget.cs` (Win32: grab and scroll)
- Create: `Services/Capture/ScrollStatusPlacement.cs` (pure: where the card goes)
- Create: `Capture/ScrollStatusWindow.cs` (the small topmost status card, built in code like `RegionSelectorWindow`)
- Modify: `Services/Capture/CaptureSettings.cs` (`CaptureMode.Scrolling`)
- Modify: `Services/Capture/CaptureService.cs` (the `Scrolling` case in `Grab`)
- Modify: `Capture/RegionSelectorWindow.cs` (`Pick(..., string? hint = null)`, which replaces the hint line when given)
- Modify: `Services/Capture/CaptureHotkeys.cs` (`HotkeyTarget.CaptureScrolling`, the plan entry and the action)
- Modify: `Models/SystemMetrics.cs` (`CaptureHotkeyScrolling`, default `"Ctrl+Shift+4"`, beside the other three)
- Modify: `OverlayWindow.cs` (menu item `1024 "Capture Scrolling…\tCtrl+Shift+4"` after 1023, and its dispatch)
- Modify: `SettingsWindow.xaml(.cs)` (a **Scrolling** test button beside the Region and Window test buttons)
- Test: `tests/Kil0bitSystemMonitor.Tests/ScrollCaptureWiringTests.cs`

**Interfaces:**
- `internal sealed class ScreenScrollTarget : IScrollTarget, IDisposable`, constructed as `ScreenScrollTarget(PixelRect area)`.
  - `Grab()` calls `ScreenCaptureEngine.CaptureRect(area, includeCursor: false)` and copies the result into a `PixelFrame` with `LockBits` in `Format32bppArgb`.
  - `Scroll(n)` saves the cursor position on first use, moves the pointer with `SetCursorPos` to the area's centre, and sends `SendInput` with one `MOUSEEVENTF_WHEEL` input per notch, `mouseData = -120 * sign(n)`. Positive n sends negative wheel data, which scrolls down.
  - `Dispose()` restores the saved cursor position.
- `ScreenCaptureEngine`: add `public static BitmapSource ToBitmapSource(PixelFrame frame)`, which builds a frozen BGRA32 image with `BitmapSource.Create`.
- `public static class ScrollStatusPlacement { static PixelRect Place(PixelRect area, PixelRect monitorWork, int cardWidth, int cardHeight); }`. It places the card centred horizontally on the area, above the area if it fits within the monitor's work area, otherwise below, otherwise in the work area's top-left corner. Tests cover each case.
- Esc while capturing: a temporary `RegisterHotKey` of `VK_ESCAPE` with no modifiers, on the status window's handle. It sets the cancel flag, and the hotkey is always unregistered in a `finally`. If registration fails because another app owns Esc, log it; the capture then stops only at the end or a cap, and the card says "Stops at the end of the page".
- The wait is the existing `WaitPumping` pattern, a `DispatcherFrame` driven by a timer, so the card repaints and `WM_HOTKEY` arrives between steps.
- `CaptureService.Grab`, `CaptureMode.Scrolling` case:
  1. Run `RegionSelectorWindow.Pick(false, hint: "Click the part that scrolls, or drag around it   ·   Esc cancel")`. Return null if cancelled.
  2. If the area is under 50 × 50, return `(null, null)` and log it. Showing a notice is optional; reuse an existing notice helper if there is one, otherwise log only.
  3. Show the status card.
  4. Run a `ScrollCaptureRun` over a `ScreenScrollTarget` with the pumping wait, the Esc flag, and progress that updates the card text to "Scrolling capture: {height:N0} px · Esc to stop", formatted with InvariantCulture.
  5. Close the card and dispose the target.
  6. Log the frames, the final height and the stop reason.
  7. Return `(ToBitmapSource(result.Image), note)`, where the note is null for End, "Nothing scrolled in that area" for Unscrollable, and "Stopped: the view changed in a way MicaStats could not follow" for NoMatch and SizeChanged. Return `(null, null)` when the image is null.
- `CaptureHotkeys.Plan` adds `cfg.CaptureHotkeyScrolling` after the Screen key, while capture hotkeys are on.

- [ ] **Step 1: Write the failing tests:**
  - `ScrollStatusPlacement`: above, below, and the corner fallback.
  - `ToBitmapSource(PixelFrame)` round trip: pixel (0,0) and size survive. Run it on `UiThread`.
  - `CaptureHotkeys.Plan` includes Scrolling while capture hotkeys are on, and leaves it out while they are off.
  - The `AppConfig` default for `CaptureHotkeyScrolling` is `"Ctrl+Shift+4"`.
  - `CaptureMode.Scrolling` exists. Check `CaptureSettingsTests` still passes.
- [ ] **Step 2: Run and see them fail. Step 3: Implement. Step 4: Run and see them pass, then the full suite once.**
- [ ] **Step 5: Commit:** `feat(capture): Scrolling mode - picks an area, scrolls it with the wheel, joins the frames into the editor`

---

### Task 4: Guide and README

**Files:** `GUIDE.md` (capture section and shortcut table) and `README.md` (the "Screen capture and annotation" bullets, the shortcut table, the What's New list, plus the Thai mirror of each).

- [ ] Describe the Scrolling mode in plain words:
  - pick the part that scrolls;
  - MicaStats scrolls to the top and then down by itself, and keeps the view visible;
  - sticky headers and footers appear once;
  - Esc stops and keeps what was captured;
  - it stops at the end, or at 20,000 px;
  - it opens in the editor;
  - **Ctrl+Shift+4**, or **Capture Scrolling…** in the overlay menu.
- [ ] Commit: `docs: scrolling capture in the guide and README`
