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
            int w = page.Width;
            var px = new int[w * height];
            Array.Copy(page.Pixels, top * w, px, 0, w * height);
            return new PixelFrame(w, height, px);
        }

        /// <summary>Every pixel a different opaque color: every row and every column is distinct.</summary>
        internal static PixelFrame Noise(int width, int height, int seed)
        {
            var rnd = new Random(seed);
            var px = new int[width * height];
            for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFF000000) | rnd.Next(0xFFFFFF);
            return new PixelFrame(width, height, px);
        }

        /// <summary>Frames of equal height side by side, left to right.</summary>
        internal static PixelFrame Beside(params PixelFrame[] parts)
        {
            int width = parts.Sum(p => p.Width), height = parts[0].Height;
            var px = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                int x = 0;
                foreach (var p in parts)
                {
                    p.Row(y).CopyTo(px.AsSpan(y * width + x, p.Width));
                    x += p.Width;
                }
            }
            return new PixelFrame(width, height, px);
        }

        /// <summary><paramref name="frame"/> with the rectangle painted in <paramref name="color"/>.</summary>
        private static PixelFrame WithBlock(PixelFrame frame, int x0, int y0, int width, int height, int color)
        {
            var px = (int[])frame.Pixels.Clone();
            for (int y = y0; y < y0 + height; y++)
                for (int x = x0; x < x0 + width; x++) px[y * frame.Width + x] = color;
            return new PixelFrame(frame.Width, frame.Height, px);
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
        public void An_unchanged_first_pair_does_not_fix_the_header_and_footer()
        {
            var page = Page(1200);
            int Steady = unchecked((int)0xFF111111);
            var footerRows = Page(100, seed: 3);
            const int caretRow = 20, clockRow = 280;   // the clock sits inside a 100-row footer
            PixelFrame Framed(int top)
            {
                var px = (int[])View(page, top, 300).Pixels.Clone();
                Array.Copy(footerRows.Pixels, 0, px, 200 * W, 100 * W);
                for (int x = 0; x < W; x++) px[clockRow * W + x] = Steady;
                return new PixelFrame(W, 300, px);
            }
            PixelFrame Animated(PixelFrame f, int color) => WithBand(WithBand(f, caretRow, 1, color), clockRow, 1, color);

            var s = new ScrollStitcher(Animated(Framed(0), Steady));
            // Nothing scrolled; only a caret near the top and a clock below it changed.
            Assert.Equal(StitchStep.Unchanged, s.Add(Animated(Framed(0), unchecked((int)0xFF222222)), out _));
            for (int top = 80; top <= 400; top += 80)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top), out _));

            var result = s.Result();
            Assert.Equal(700, result.Height);
            for (int y = 0; y < 600; y++)
            {
                if (y == caretRow) continue;   // the first frame keeps its own caret
                Assert.True(result.Row(y).SequenceEqual(page.Row(y).ToArray()), "body row " + y);
            }
            for (int i = 0; i < 100; i++)
                Assert.True(result.Row(600 + i).SequenceEqual(i == clockRow - 200 ? Framed(0).Row(clockRow).ToArray() : footerRows.Row(i).ToArray()), "footer row " + i);
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

        // ----- Final review fixes -------------------------------------------------------------

        [Fact]
        public void A_short_repeated_motif_does_not_beat_the_real_shift()
        {
            // Rows 290-299 repeat rows 90-99, so shift 290 lines up 10 of 10 rows, while the real
            // shift 90 lines up 198 of 210 because a caret blinks in 12 rows. Most rows wins.
            var page = Page(800);
            var px = (int[])page.Pixels.Clone();
            Array.Copy(page.Pixels, 90 * W, px, 290 * W, 10 * W);
            var repeated = new PixelFrame(W, 800, px);
            var next = (int[])View(repeated, 90, 300).Pixels.Clone();
            for (int y = 40; y < 52; y++) next[y * W + 5] = unchecked((int)0xFFFF0000);

            var s = new ScrollStitcher(View(repeated, 0, 300));

            Assert.Equal(StitchStep.Appended, s.Add(new PixelFrame(W, 300, next), out int added));
            Assert.Equal(90, added);
            Assert.True(s.Result().SameAs(View(repeated, 0, 390)));
        }

        [Fact]
        public void A_floating_widget_at_the_bottom_appears_once()
        {
            // A 30 x 40 chat bubble in the bottom-right corner, over the page, in every frame.
            var page = Page(1200);
            int widget = unchecked((int)0xFF00AA55);
            PixelFrame Floating(int top) => WithBlock(View(page, top, 300), W - 60, 260, 30, 40, widget);

            var s = new ScrollStitcher(Floating(0));
            for (int top = 90; top <= 900; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(Floating(top), out _));

            var result = s.Result();
            Assert.Equal(1200, result.Height);
            Assert.Equal(40, Enumerable.Range(0, result.Height).Count(y => result.Row(y)[W - 45] == widget));
            for (int y = 0; y < 1160; y++)
                Assert.True(result.Row(y).SequenceEqual(page.Row(y).ToArray()), "body row " + y);
        }

        [Fact]
        public void A_clock_in_a_fixed_footer_leaves_no_wrong_body_rows()
        {
            // A 40-row footer that stays put, except a clock in its sixth row that changes every frame.
            var page = Page(1200);
            var footer = Page(40, seed: 5);
            PixelFrame Framed(int top, int tick)
            {
                var px = (int[])View(page, top, 300).Pixels.Clone();
                Array.Copy(footer.Pixels, 0, px, 260 * W, 40 * W);
                for (int x = 10; x < 30; x++) px[265 * W + x] = unchecked((int)0xFF000000) | (0x203040 + tick * 0x010101);
                return new PixelFrame(W, 300, px);
            }

            var s = new ScrollStitcher(Framed(0, 0));
            int tick = 0;
            for (int top = 90; top <= 900; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top, ++tick), out _));

            // 260 body rows show at a time, so scrolled to 900 the body ends at 1160.
            var result = s.Result();
            Assert.Equal(1160 + 40, result.Height);
            Assert.Equal(0, Enumerable.Range(0, 1160).Count(y => !result.Row(y).SequenceEqual(page.Row(y).ToArray())));
            var last = Framed(900, tick);
            for (int i = 0; i < 40; i++)
                Assert.True(result.Row(1160 + i).SequenceEqual(last.Row(260 + i).ToArray()), "footer row " + i);
        }

        [Fact]
        public void A_cut_at_the_height_cap_keeps_the_page_continuous()
        {
            var page = Page(1000);
            var s = new ScrollStitcher(View(page, 0, 300));
            for (int top = 90; top <= 360; top += 90) s.Add(View(page, top, 300), out _);
            Assert.Equal(660, s.Height);

            Assert.True(s.Result(maxHeight: 500).SameAs(View(page, 0, 500)));
        }

        [Fact]
        public void A_cut_keeps_the_sticky_header_and_the_fixed_footer()
        {
            var page = Page(1200);
            int header = unchecked((int)0xFF3366CC), footer = unchecked((int)0xFFCC6633);
            PixelFrame Framed(int top) => WithBand(WithBand(View(page, top, 300), 0, 20, header), 285, 15, footer);
            var s = new ScrollStitcher(Framed(0));
            for (int top = 80; top <= 400; top += 80) s.Add(Framed(top), out _);

            var cut = s.Result(maxHeight: 400);

            Assert.Equal(400, cut.Height);
            for (int y = 0; y < 20; y++) Assert.Equal(header, cut.Row(y)[0]);
            for (int y = 20; y < 385; y++) Assert.True(cut.Row(y).SequenceEqual(page.Row(y).ToArray()), "body row " + y);
            for (int y = 385; y < 400; y++) Assert.Equal(footer, cut.Row(y)[0]);
        }

        [Fact]
        public void Side_panels_that_do_not_scroll_are_left_out_and_the_rest_joins()
        {
            // A 30 px navigation pane on the left and a 60 px panel on the right stay put.
            const int width = 300, left = 30, right = 60;
            var content = Noise(width - left - right, 1200, seed: 21);
            var leftPane = Noise(left, 300, seed: 22);
            var rightPane = Noise(right, 300, seed: 23);
            PixelFrame Framed(int top) => Beside(leftPane, View(content, top, 300), rightPane);

            var s = new ScrollStitcher(Framed(0));
            for (int top = 90; top <= 900; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top), out _));

            var result = s.Result();
            Assert.Equal(width - left - right, result.Width);
            Assert.True(result.SameAs(content));
        }

        [Fact]
        public void A_left_panel_alone_still_leaves_the_scrollbar_strip_out_of_the_comparison()
        {
            // A 30 px pane at the left; at the right, a 24 px strip that is different in every frame.
            const int width = 320, left = 30, strip = 24;
            var content = Noise(width - left - strip, 1200, seed: 31);
            var leftPane = Noise(left, 300, seed: 32);
            PixelFrame Framed(int top) => Beside(leftPane, View(content, top, 300), Noise(strip, 300, seed: 1000 + top));

            var s = new ScrollStitcher(Framed(0));
            for (int top = 90; top <= 900; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top), out _));

            var result = s.Result();
            Assert.Equal(width - left, result.Width);   // the pane goes; the scrollbar strip stays, as without panels
            for (int y = 0; y < result.Height; y++)
                Assert.True(result.Row(y).Slice(0, content.Width).SequenceEqual(content.Row(y)), "row " + y);
        }

        [Fact]
        public void A_view_where_only_the_scrollbar_strip_moved_is_unchanged()
        {
            // Everything left of the strip is the same; inside the strip something scrolled 5 rows.
            const int width = 200, strip = 24;
            var still = Noise(width - strip, 300, seed: 41);
            var bar = Noise(strip, 400, seed: 42);
            var before = Beside(still, View(bar, 0, 300));
            var after = Beside(still, View(bar, 5, 300));

            Assert.Equal(StitchStep.Unchanged, new ScrollStitcher(before).Add(after, out _));
        }

        [Fact]
        public void A_blank_margin_is_not_taken_for_a_side_panel()
        {
            // The page's own white margin stays put too, but it is plain: the image keeps its width.
            var page = Page(1000);
            PixelFrame Margined(int top) => WithBlock(View(page, top, 300), 0, 0, 20, 300, unchecked((int)0xFFFFFFFF));

            var s = new ScrollStitcher(Margined(0));
            Assert.Equal(StitchStep.Appended, s.Add(Margined(90), out _));

            Assert.Equal(W, s.Result().Width);
        }

        [Fact]
        public void A_wide_scrollbar_at_175_percent_still_joins()
        {
            // At 175% a scrollbar is about 30 px; this one is 34 with a 100-row thumb that moves.
            var page = Page(800);
            PixelFrame WithBar(PixelFrame f, int at)
            {
                var px = (int[])f.Pixels.Clone();
                for (int y = 0; y < f.Height; y++)
                    for (int x = W - 34; x < W; x++) px[y * W + x] = y >= at && y < at + 100 ? unchecked((int)0xFF888888) : unchecked((int)0xFFEEEEEE);
                return new PixelFrame(W, f.Height, px);
            }

            Assert.Equal(StitchStep.NoMatch, new ScrollStitcher(WithBar(View(page, 0, 300), 0)).Add(WithBar(View(page, 100, 300), 37), out _));
            var s = new ScrollStitcher(WithBar(View(page, 0, 300), 0), scale: 1.75);
            Assert.Equal(StitchStep.Appended, s.Add(WithBar(View(page, 100, 300), 37), out int added));
            Assert.Equal(100, added);
        }

        [Fact]
        public void The_scrollbar_allowance_follows_the_monitor_scale()
        {
            Assert.Equal(24, ScrollStitcher.ScrollbarColumns(1000, 1.0));
            Assert.Equal(30, ScrollStitcher.ScrollbarColumns(1000, 1.25));
            Assert.Equal(42, ScrollStitcher.ScrollbarColumns(1000, 1.75));
            Assert.Equal(48, ScrollStitcher.ScrollbarColumns(1000, 2.0));
            Assert.Equal(0, ScrollStitcher.ScrollbarColumns(90, 2.0));   // too narrow to spare it
        }

        [Fact]
        public void A_part_changed_in_place_is_the_top_only_where_the_view_also_changes_by_itself()
        {
            // A 40 x 50 block changed and nothing lines up. From two looks that could be a GIF or a
            // pane replaced by a long scroll, so it counts as unmoved only inside a region seen
            // changing while nothing scrolled.
            var page = Page(800);
            var before = WithBlock(View(page, 0, 300), 10, 100, 50, 40, unchecked((int)0xFF102030));
            var after = WithBlock(View(page, 0, 300), 10, 100, 50, 40, unchecked((int)0xFF405060));

            Assert.False(ScrollStitcher.Unmoved(before, after));
            Assert.False(ScrollStitcher.TopReached(before, after, selfMotion: null));
            Assert.True(ScrollStitcher.TopReached(before, after, new PixelRect(10, 100, 50, 40)));
            Assert.True(ScrollStitcher.TopReached(before, after, new PixelRect(0, 90, 80, 70)));
            Assert.False(ScrollStitcher.TopReached(before, after, new PixelRect(10, 200, 50, 40)));
            Assert.Equal(new PixelRect(10, 100, 50, 40), before.DiffBox(after));
        }

        [Fact]
        public void A_few_rows_changed_in_place_are_still_unmoved()
        {
            // A caret or a clock: under 16 rows changed.
            var page = Page(800);
            var before = WithBlock(View(page, 0, 300), 10, 100, 30, 12, unchecked((int)0xFF102030));
            var after = WithBlock(View(page, 0, 300), 10, 100, 30, 12, unchecked((int)0xFF405060));

            Assert.True(ScrollStitcher.Unmoved(before, after));
            Assert.True(ScrollStitcher.TopReached(before, after, selfMotion: null));
        }

        [Fact]
        public void A_scrolling_pane_replaced_under_a_large_fixed_header_has_moved()
        {
            // A 225-row header and 30 px panels stay put; the 75-row pane between the panels was
            // replaced by a long scroll up. Three quarters of the rows still match in place.
            var content = Page(2000);
            var header = Noise(W, 225, seed: 51);
            var leftPanel = Noise(30, 75, seed: 52);
            var rightPanel = Noise(30, 75, seed: 53);
            PixelFrame Layout(int top)
            {
                var pane = Noise(W - 60, 75, seed: 1000 + top);   // nothing in common between two tops
                var px = new int[W * 300];
                Array.Copy(header.Pixels, px, header.Pixels.Length);
                var lower = Beside(leftPanel, pane, rightPanel);
                Array.Copy(lower.Pixels, 0, px, 225 * W, lower.Pixels.Length);
                return new PixelFrame(W, 300, px);
            }

            Assert.False(ScrollStitcher.Unmoved(Layout(1700), Layout(800)));
            Assert.False(ScrollStitcher.TopReached(Layout(1700), Layout(800), selfMotion: null));
            Assert.True(ScrollStitcher.Unmoved(Layout(0), Layout(0)));
        }

        // ----- Probes from the re-review (each failed at e8ce4cc) -----------------------------------

        private static readonly int White = unchecked((int)0xFFFFFFFF), Gray = unchecked((int)0xFFC0C0C0);

        private static int Ink(Random r) => unchecked((int)0xFF000000) | r.Next(0x808080);

        /// <summary>Walks a page top to bottom in <paramref name="step"/>-row steps, then shows the last view again.</summary>
        private static PixelFrame Walk(PixelFrame page, int h, int step, Func<PixelFrame, PixelFrame>? decorate = null)
        {
            decorate ??= f => f;
            var s = new ScrollStitcher(decorate(View(page, 0, h)));
            int top = 0;
            while (top + h < page.Height)
            {
                int next = Math.Min(top + step, page.Height - h);
                Assert.Equal(StitchStep.Appended, s.Add(decorate(View(page, next, h)), out _));
                top = next;
            }
            Assert.Equal(StitchStep.Unchanged, s.Add(decorate(View(page, top, h)), out _));
            return s.Result();
        }

        /// <summary>A spreadsheet-like grid: a gray line every cell, vertical lines every 50 px, sparse text.</summary>
        private static PixelFrame Grid(int width, int height, int cell, int textEvery, int seed)
        {
            var px = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    px[y * width + x] = y % cell == 0 ? Gray : (x % 50 == 0 ? Gray : White);
            for (int c = 0; c * cell < height; c++)
            {
                if (c % textEvery != 0) continue;
                var rr = new Random(seed * 1000 + c);
                for (int r = 6; r < 15 && c * cell + r < height; r++)
                    for (int x = 60; x < 140; x++)
                        if (rr.Next(3) == 0) px[(c * cell + r) * width + x] = Ink(rr);
            }
            return new PixelFrame(width, height, px);
        }

        [Theory]
        [InlineData(21, 3, 600, 100)]   // most-matching-rows joined these at 100 and 37 by turns
        [InlineData(20, 6, 300, 60)]    // no shift at all won outright
        [InlineData(20, 6, 300, 100)]
        [InlineData(20, 8, 300, 100)]   // still empty rows at the bottom make the real overlap short
        public void A_sparse_repeating_grid_joins_at_the_real_shift(int cell, int textEvery, int h, int step)
        {
            var page = Grid(200, 2400, cell, textEvery, 5);

            Assert.True(Walk(page, h, step).SameAs(page));
        }

        /// <summary>
        /// A list view: items 20 rows tall, a distinct name in columns 10-109, and a Type column in
        /// 130-169 that reads the same ("File folder") for the first <paramref name="sameRows"/> rows.
        /// </summary>
        private static PixelFrame List(int width, int height, int sameRows, int seed)
        {
            var rnd = new Random(seed);
            var px = Enumerable.Repeat(White, width * height).ToArray();
            var same = new int[12 * 40];
            var sr = new Random(99);
            for (int i = 0; i < same.Length; i++) same[i] = sr.Next(3) == 0 ? unchecked((int)0xFF202020) : White;
            for (int item = 0; item * 20 < height; item++)
                for (int r = 4; r < 16 && item * 20 + r < height; r++)
                {
                    int y = item * 20 + r;
                    for (int x = 10; x < 110; x++) if (rnd.Next(3) == 0) px[y * width + x] = Ink(rnd);
                    for (int x = 130; x < 170; x++)
                        px[y * width + x] = item * 20 < sameRows ? same[(r - 4) * 40 + x - 130] : (rnd.Next(3) == 0 ? Ink(rnd) : White);
                }
            return new PixelFrame(width, height, px);
        }

        [Theory]
        [InlineData(60)]
        [InlineData(100)]
        public void A_column_that_repeats_down_a_list_is_not_a_side_panel(int step)
        {
            // The Type column is the same on every row and the step is whole items, so it stays put
            // too; but it also lines up under the shift, so it is content, not a panel.
            var page = List(220, 2000, 700, 4);

            Assert.True(Walk(page, 300, step).SameAs(page));
        }

        [Fact]
        public void A_fixed_side_element_is_cut_to_its_own_columns_and_the_blank_gutter_stays()
        {
            // Text in columns 40-170 on every row; a share bar at x 4-23 and a widget at x 185-209
            // stay put. Only the columns out to each element's inner edge are cut.
            var rnd = new Random(9);
            var px = Enumerable.Repeat(White, 240 * 1500).ToArray();
            for (int y = 0; y < 1500; y++)
            {
                int len = 60 + rnd.Next(71);
                for (int x = 40; x < 40 + len; x++) if (rnd.Next(3) == 0) px[y * 240 + x] = Ink(rnd);
            }
            var page = new PixelFrame(240, 1500, px);
            int blue = unchecked((int)0xFF2060E0);
            PixelFrame Paint(PixelFrame f, int x0, int y0, int w, int h)
            {
                var p = (int[])f.Pixels.Clone();
                for (int y = y0; y < y0 + h; y++)
                    for (int x = x0; x < x0 + w; x++) p[y * f.Width + x] = (x + y) % 3 == 0 ? blue : White;
                return new PixelFrame(f.Width, f.Height, p);
            }
            PixelFrame Decorate(PixelFrame f) => Paint(Paint(f, 4, 60, 20, 160), 185, 200, 25, 40);

            var result = Walk(page, 300, 90, Decorate);

            Assert.Equal(185 - 24, result.Width);
            var kept = new int[(185 - 24) * 1500];
            for (int y = 0; y < 1500; y++) page.Row(y).Slice(24, 185 - 24).CopyTo(kept.AsSpan(y * (185 - 24)));
            Assert.True(result.SameAs(new PixelFrame(185 - 24, 1500, kept)));
        }

        [Fact]
        public void Side_panels_are_cut_from_the_scrolled_rows_only_and_the_header_keeps_its_width()
        {
            // A window clicked whole: a 20-row toolbar across the top, a navigation pane at the left.
            const int width = 300, pane = 40;
            var content = Noise(width - pane, 1200, seed: 71);
            var toolbar = Noise(width, 20, seed: 72);
            var nav = new int[pane * 280];
            for (int i = 0; i < nav.Length; i++) nav[i] = i % 7 == 0 ? unchecked((int)0xFF404040) : unchecked((int)0xFFF0F0F0);
            var navPane = new PixelFrame(pane, 280, nav);
            PixelFrame Framed(int top)
            {
                var px = new int[width * 300];
                Array.Copy(toolbar.Pixels, px, toolbar.Pixels.Length);
                var body = Beside(navPane, View(content, top, 280));
                Array.Copy(body.Pixels, 0, px, 20 * width, body.Pixels.Length);
                return new PixelFrame(width, 300, px);
            }

            var s = new ScrollStitcher(Framed(0));
            for (int top = 90; top <= 900; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top), out _));
            var result = s.Result();

            Assert.Equal(width, result.Width);
            Assert.Equal(20 + 1180, result.Height);
            for (int y = 0; y < 20; y++) Assert.True(result.Row(y).SequenceEqual(toolbar.Row(y)), "toolbar row " + y);
            for (int y = 0; y < 1180; y++)
            {
                var row = result.Row(20 + y);
                Assert.True(row.Slice(pane).SequenceEqual(content.Row(y)), "content row " + y);
                Assert.True(row.Slice(0, pane).IndexOfAnyExcept(unchecked((int)0xFFF0F0F0)) < 0, "pane area row " + y);
            }
        }

        [Fact]
        public void A_small_real_move_is_not_unmoved()
        {
            // A sparse view: one 6-row line of text on white moves down by a few rows.
            var rnd = new Random(1);
            var line = new int[6 * 150];
            for (int i = 0; i < line.Length; i++) line[i] = rnd.Next(3) == 0 ? unchecked((int)0xFF000000) : White;
            PixelFrame At(int row)
            {
                var px = new int[200 * 300];
                Array.Fill(px, White);
                for (int r = 0; r < 6; r++) Array.Copy(line, r * 150, px, (row + r) * 200 + 20, 150);
                return new PixelFrame(200, 300, px);
            }

            foreach (int k in new[] { 1, 4, 7 })
                Assert.False(ScrollStitcher.Unmoved(At(100), At(100 + k)), "moved " + k);
        }

        [Fact]
        public void The_soft_footer_does_not_narrow_the_overlap_search()
        {
            // Rows 180-314 are blank. On the second step only rows 135-209 of the new view line up
            // with informative rows: inside the full band, but below the 75-row bottom margin's top.
            var page = Page(1000);
            var px = (int[])page.Pixels.Clone();
            for (int y = 180; y < 315; y++) for (int x = 0; x < W; x++) px[y * W + x] = unchecked((int)0xFFFFFFFF);
            var gapped = new PixelFrame(W, 1000, px);

            var s = new ScrollStitcher(View(gapped, 0, 300));
            for (int top = 90; top <= 630; top += 90)
                Assert.Equal(StitchStep.Appended, s.Add(View(gapped, top, 300), out _));
            Assert.Equal(StitchStep.Appended, s.Add(View(gapped, 700, 300), out _));

            Assert.True(s.Result().SameAs(gapped));
        }

        [Fact]
        public void A_step_longer_than_the_room_above_the_margin_still_joins()
        {
            // First step 60 rows: a 75-row margin under a 20-row header leaves 205 rows above it.
            // The next step is 230 rows; the 25 rows now hidden under the header come from the
            // previous frame.
            var page = Page(1000);
            int header = unchecked((int)0xFF3366CC);
            PixelFrame Framed(int top) => WithBand(View(page, top, 300), 0, 20, header);

            var s = new ScrollStitcher(Framed(0));
            Assert.Equal(StitchStep.Appended, s.Add(Framed(60), out _));
            Assert.Equal(StitchStep.Appended, s.Add(Framed(290), out int added));

            Assert.Equal(230, added);
            var result = s.Result();
            Assert.Equal(590, result.Height);
            for (int y = 0; y < 20; y++) Assert.Equal(header, result.Row(y)[0]);
            for (int y = 20; y < 590; y++) Assert.True(result.Row(y).SequenceEqual(page.Row(y).ToArray()), "body row " + y);
        }

        [Fact]
        public void A_view_that_scrolled_either_way_or_jumped_has_moved()
        {
            var page = Page(1200);

            Assert.False(ScrollStitcher.Unmoved(View(page, 400, 300), View(page, 350, 300)));   // up 50 rows
            Assert.False(ScrollStitcher.Unmoved(View(page, 400, 300), View(page, 450, 300)));   // down 50 rows
            Assert.False(ScrollStitcher.Unmoved(View(page, 400, 300), View(page, 0, 300)));     // up by more than the view
            Assert.True(ScrollStitcher.Unmoved(View(page, 400, 300), View(page, 400, 300)));
        }
    }
}
