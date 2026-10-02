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
        public void An_unchanged_first_pair_does_not_fix_the_header_and_footer()
        {
            var page = Page(1200);
            var footerRows = Page(100, seed: 3);
            const int caretRow = 20, clockRow = 190;
            PixelFrame Framed(int top)
            {
                var px = (int[])View(page, top, 300).Pixels.Clone();
                Array.Copy(footerRows.Pixels, 0, px, 200 * W, 100 * W);
                return new PixelFrame(W, 300, px);
            }
            PixelFrame Animated(PixelFrame f, int color) => WithBand(WithBand(f, caretRow, 1, color), clockRow, 1, color);

            var s = new ScrollStitcher(Animated(Framed(0), unchecked((int)0xFF111111)));
            // Nothing scrolled; only a caret near the top and a clock below it changed.
            Assert.Equal(StitchStep.Unchanged, s.Add(Animated(Framed(0), unchecked((int)0xFF222222)), out _));
            for (int top = 80; top <= 400; top += 80)
                Assert.Equal(StitchStep.Appended, s.Add(Framed(top), out _));

            var result = s.Result();
            Assert.Equal(700, result.Height);
            for (int y = 0; y < 600; y++)
            {
                if (y == caretRow || y == clockRow) continue;   // the first frame keeps its own caret and clock
                Assert.True(result.Row(y).SequenceEqual(page.Row(y).ToArray()), "body row " + y);
            }
            for (int i = 0; i < 100; i++)
                Assert.True(result.Row(600 + i).SequenceEqual(footerRows.Row(i).ToArray()), "footer row " + i);
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
