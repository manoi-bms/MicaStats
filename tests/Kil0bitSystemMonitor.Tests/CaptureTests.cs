using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Capture geometry. These are the cases that produce a visibly wrong screenshot rather than
    /// an exception: a backwards drag, a monitor layout whose origin is not (0,0), a loupe near
    /// a corner, a snap that would invert the selection.
    /// </summary>
    public class CaptureGeometryTests
    {
        // The reporting machine's real layout: primary 1920x1080 at the origin, a taller
        // secondary to its right whose top starts 339px down.
        private static readonly List<MonitorInfo> Monitors = new()
        {
            new MonitorInfo("PRIMARY", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), true, 1.0),
            new MonitorInfo("SECOND", new PixelRect(1920, 339, 2560, 1600), new PixelRect(1920, 339, 2560, 1552), false, 1.25),
        };

        [Fact]
        public void Drag_normalises_in_every_direction()
        {
            var expected = new PixelRect(10, 20, 90, 80);
            Assert.Equal(expected, PixelRect.FromPoints(10, 20, 100, 100));
            Assert.Equal(expected, PixelRect.FromPoints(100, 100, 10, 20));
            Assert.Equal(expected, PixelRect.FromPoints(10, 100, 100, 20));
            Assert.Equal(expected, PixelRect.FromPoints(100, 20, 10, 100));
        }

        [Fact]
        public void Virtual_bounds_span_every_monitor()
        {
            var bounds = CaptureGeometry.VirtualBounds(Monitors.Select(m => m.Bounds).ToList());
            Assert.Equal(new PixelRect(0, 0, 4480, 1939), bounds);
        }

        [Fact]
        public void Virtual_bounds_handle_a_monitor_left_of_the_origin()
        {
            // A screen placed to the LEFT of the primary has negative coordinates; a capture that
            // assumed an origin of (0,0) would come out shifted.
            var bounds = CaptureGeometry.VirtualBounds(new List<PixelRect>
            {
                new(0, 0, 1920, 1080),
                new(-1600, -200, 1600, 900),
            });
            Assert.Equal(new PixelRect(-1600, -200, 3520, 1280), bounds);
        }

        [Fact]
        public void Empty_monitor_list_yields_empty_bounds()
        {
            Assert.True(CaptureGeometry.VirtualBounds(new List<PixelRect>()).IsEmpty);
        }

        [Fact]
        public void Clamp_keeps_the_selection_inside_the_desktop()
        {
            var bounds = new PixelRect(0, 0, 1920, 1080);
            Assert.Equal(new PixelRect(1620, 780, 300, 300),
                CaptureGeometry.Clamp(new PixelRect(1800, 900, 300, 300), bounds));
            Assert.Equal(new PixelRect(0, 0, 300, 300),
                CaptureGeometry.Clamp(new PixelRect(-50, -80, 300, 300), bounds));
        }

        [Fact]
        public void Clamp_shrinks_a_selection_larger_than_the_desktop()
        {
            var clamped = CaptureGeometry.Clamp(new PixelRect(-10, -10, 5000, 5000), new PixelRect(0, 0, 1920, 1080));
            Assert.Equal(new PixelRect(0, 0, 1920, 1080), clamped);
        }

        [Fact]
        public void Monitor_lookup_finds_the_containing_screen()
        {
            Assert.Equal("PRIMARY", CaptureGeometry.MonitorAt(Monitors, 100, 100)!.DeviceName);
            Assert.Equal("SECOND", CaptureGeometry.MonitorAt(Monitors, 2500, 800)!.DeviceName);
        }

        [Fact]
        public void Monitor_lookup_falls_back_to_the_nearest_screen_in_a_gap()
        {
            // (500, 1500) is below the primary and left of the secondary: on no monitor at all.
            var nearest = CaptureGeometry.MonitorAt(Monitors, 500, 1500);
            Assert.NotNull(nearest);
            Assert.Equal("PRIMARY", nearest!.DeviceName);
        }

        [Fact]
        public void Window_hit_test_prefers_the_front_most_window()
        {
            var windows = new List<CaptureTarget>
            {
                new(new IntPtr(1), new PixelRect(100, 100, 400, 300), "front"),
                new(new IntPtr(2), new PixelRect(0, 0, 800, 600), "behind"),
            };
            Assert.Equal("front", CaptureGeometry.WindowAt(windows, 200, 200)!.Title);
            Assert.Equal("behind", CaptureGeometry.WindowAt(windows, 50, 50)!.Title);
            Assert.Null(CaptureGeometry.WindowAt(windows, 5000, 5000));
        }

        [Fact]
        public void Snapping_pulls_edges_onto_nearby_guides()
        {
            var guides = new List<PixelRect> { new(100, 100, 400, 300) };
            var snapped = CaptureGeometry.SnapToGuides(new PixelRect(104, 97, 392, 306), guides, 8);
            Assert.Equal(new PixelRect(100, 100, 400, 300), snapped);
        }

        [Fact]
        public void Snapping_leaves_distant_edges_alone()
        {
            var guides = new List<PixelRect> { new(100, 100, 400, 300) };
            var rect = new PixelRect(200, 200, 50, 50);
            Assert.Equal(rect, CaptureGeometry.SnapToGuides(rect, guides, 8));
        }

        [Fact]
        public void Snapping_never_inverts_a_selection()
        {
            // Both edges of a thin selection are near the same guide edge; snapping both to it
            // would collapse the rectangle.
            var guides = new List<PixelRect> { new(100, 100, 400, 300) };
            var rect = new PixelRect(98, 200, 5, 50);
            var snapped = CaptureGeometry.SnapToGuides(rect, guides, 8);
            Assert.True(snapped.Width > 0 && snapped.Height > 0);
        }

        [Fact]
        public void Magnifier_source_stays_inside_the_frame_at_a_corner()
        {
            var bounds = new PixelRect(0, 0, 1920, 1080);
            var atOrigin = CaptureGeometry.MagnifierSource(0, 0, 21, bounds);
            Assert.Equal(new PixelRect(0, 0, 21, 21), atOrigin);

            var atCorner = CaptureGeometry.MagnifierSource(1919, 1079, 21, bounds);
            Assert.Equal(new PixelRect(1899, 1059, 21, 21), atCorner);
        }

        [Fact]
        public void Magnifier_source_is_centred_away_from_edges()
        {
            var src = CaptureGeometry.MagnifierSource(500, 400, 21, new PixelRect(0, 0, 1920, 1080));
            Assert.Equal(new PixelRect(490, 390, 21, 21), src);
        }

        [Fact]
        public void Readout_flips_to_stay_on_screen()
        {
            var bounds = new PixelRect(0, 0, 1920, 1080);
            var (x, y) = CaptureGeometry.PlaceNearCursor(1900, 1060, 200, 60, 16, bounds);
            Assert.True(x + 200 <= bounds.Right);
            Assert.True(y + 60 <= bounds.Bottom);
        }

        [Fact]
        public void Resize_by_a_handle_normalises_when_dragged_past_the_far_edge()
        {
            var rect = new PixelRect(100, 100, 50, 50);
            var flipped = CaptureGeometry.ResizeBy(rect, ResizeHandle.Left, 120, 0);
            Assert.Equal(150, flipped.Left);
            Assert.Equal(220, flipped.Right);
            Assert.True(flipped.Width > 0);
        }

        [Fact]
        public void Handle_hit_test_identifies_corners_and_edges()
        {
            var rect = new PixelRect(100, 100, 200, 150);
            Assert.Equal(ResizeHandle.TopLeft, CaptureGeometry.HandleAt(rect, 101, 102, 6));
            Assert.Equal(ResizeHandle.BottomRight, CaptureGeometry.HandleAt(rect, 299, 249, 6));
            Assert.Equal(ResizeHandle.Left, CaptureGeometry.HandleAt(rect, 100, 180, 6));
            Assert.Equal(ResizeHandle.None, CaptureGeometry.HandleAt(rect, 200, 180, 6));
        }

        [Fact]
        public void Intersect_returns_empty_when_rectangles_do_not_overlap()
        {
            Assert.True(new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(50, 50, 10, 10)).IsEmpty);
        }
    }

    /// <summary>
    /// File naming. The calendar assertion is the important one: this machine runs a Thai
    /// locale, whose default calendar would file a 2026 capture under 2569.
    /// </summary>
    public class CaptureFileNamerTests
    {
        [Fact]
        public void Template_tokens_expand()
        {
            string name = CaptureFileNamer.Format("MicaStats_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}",
                new DateTime(2026, 8, 23, 9, 5, 7));
            Assert.Equal("MicaStats_2026-08-23_09-05-07", name);
        }

        [Fact]
        public void Dates_use_the_gregorian_calendar_regardless_of_locale()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // th-TH defaults to the Buddhist calendar: an unqualified format writes 2569.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                string name = CaptureFileNamer.Format("{yyyy}-{MM}-{dd}", new DateTime(2026, 8, 23));
                Assert.Equal("2026-08-23", name);
                Assert.DoesNotContain("2569", name);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Fact]
        public void Size_and_mode_tokens_expand()
        {
            string name = CaptureFileNamer.Format("{mode}_{w}x{h}", new DateTime(2026, 1, 1), "region", 800, 600);
            Assert.Equal("region_800x600", name);
        }

        [Fact]
        public void Blank_template_falls_back_to_the_default()
        {
            string name = CaptureFileNamer.Format("   ", new DateTime(2026, 8, 23, 9, 5, 7));
            Assert.StartsWith("MicaStats_2026-08-23", name);
        }

        [Fact]
        public void Invalid_filename_characters_are_replaced()
        {
            string name = CaptureFileNamer.Sanitize("shot: a/b\\c*?\"<>|");

            // Char predicates rather than Assert.DoesNotContain(string, string): the substring
            // overload reports confusing positions here, and every invalid character is a
            // single char anyway.
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                Assert.False(name.Contains(bad), $"sanitised name still contains {(int)bad}");

            Assert.Equal("shot- a-b-c------", name);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void Trailing_dots_and_spaces_are_trimmed()
        {
            Assert.Equal("shot", CaptureFileNamer.Sanitize("shot.  "));
            Assert.Equal("capture", CaptureFileNamer.Sanitize("   "));
        }

        [Fact]
        public void Unique_path_appends_a_counter_only_on_collision()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine("C:\\shots", "a.png"),
                Path.Combine("C:\\shots", "a (2).png"),
            };

            Assert.Equal(Path.Combine("C:\\shots", "b.png"),
                CaptureFileNamer.UniquePath("C:\\shots", "b", CaptureFormat.Png, taken.Contains));
            Assert.Equal(Path.Combine("C:\\shots", "a (3).png"),
                CaptureFileNamer.UniquePath("C:\\shots", "a", CaptureFormat.Png, taken.Contains));
        }

        [Theory]
        [InlineData(CaptureFormat.Png, ".png")]
        [InlineData(CaptureFormat.Jpeg, ".jpg")]
        public void Extensions_match_the_format(CaptureFormat format, string expected) =>
            Assert.Equal(expected, CaptureFileNamer.Extension(format));
    }

    /// <summary>Undo/redo and cropping for the annotation editor.</summary>
    public class AnnotationDocumentTests
    {
        private static readonly PixelRect Full = new(0, 0, 800, 600);

        private static ShapeAnnotation Mark(int seed) =>
            new(CaptureTool.Rectangle, new ImgPoint(seed, seed), new ImgPoint(seed + 10, seed + 10));

        [Fact]
        public void New_document_is_clean()
        {
            var doc = new AnnotationDocument(Full);
            Assert.Empty(doc.Items);
            Assert.False(doc.CanUndo);
            Assert.False(doc.CanRedo);
            Assert.False(doc.IsDirty);
            Assert.Equal(Full, doc.Crop);
        }

        [Fact]
        public void Undo_and_redo_walk_the_history()
        {
            var doc = new AnnotationDocument(Full);
            doc.Add(Mark(1));
            doc.Add(Mark(2));
            Assert.Equal(2, doc.Items.Count);

            Assert.True(doc.Undo());
            Assert.Single(doc.Items);
            Assert.True(doc.Undo());
            Assert.Empty(doc.Items);
            Assert.False(doc.Undo());

            Assert.True(doc.Redo());
            Assert.Single(doc.Items);
            Assert.True(doc.Redo());
            Assert.Equal(2, doc.Items.Count);
            Assert.False(doc.Redo());
        }

        [Fact]
        public void A_new_edit_after_undo_discards_the_redo_tail()
        {
            var doc = new AnnotationDocument(Full);
            doc.Add(Mark(1));
            doc.Add(Mark(2));
            doc.Undo();
            doc.Add(Mark(3));

            Assert.False(doc.CanRedo);
            Assert.Equal(2, doc.Items.Count);
        }

        [Fact]
        public void Undoing_does_not_mutate_an_earlier_snapshot()
        {
            var doc = new AnnotationDocument(Full);
            doc.Add(Mark(1));
            var afterFirst = doc.Items.ToList();
            doc.Add(Mark(2));
            doc.Undo();

            Assert.Equal(afterFirst.Count, doc.Items.Count);
            Assert.Same(afterFirst[0], doc.Items[0]);
        }

        [Fact]
        public void Crop_is_clamped_to_the_image_and_is_undoable()
        {
            var doc = new AnnotationDocument(Full);
            doc.ApplyCrop(new PixelRect(700, 500, 400, 400));
            Assert.Equal(new PixelRect(700, 500, 100, 100), doc.Crop);
            Assert.True(doc.IsDirty);

            doc.Undo();
            Assert.Equal(Full, doc.Crop);
        }

        [Fact]
        public void Crop_keeps_annotations_so_undo_restores_them()
        {
            var doc = new AnnotationDocument(Full);
            doc.Add(Mark(500));
            doc.ApplyCrop(new PixelRect(0, 0, 100, 100));

            Assert.Single(doc.Items);   // outside the crop, but not destroyed
            doc.ResetCrop();
            Assert.Equal(Full, doc.Crop);
            Assert.Single(doc.Items);
        }

        [Fact]
        public void Step_numbers_increment_and_survive_undo()
        {
            var doc = new AnnotationDocument(Full);
            Assert.Equal(1, doc.NextStepNumber);

            doc.Add(new StepAnnotation(new ImgPoint(10, 10), doc.NextStepNumber, 12));
            Assert.Equal(2, doc.NextStepNumber);

            doc.Add(new StepAnnotation(new ImgPoint(40, 10), doc.NextStepNumber, 12));
            Assert.Equal(3, doc.NextStepNumber);

            doc.Undo();
            Assert.Equal(2, doc.NextStepNumber);
        }

        [Fact]
        public void Clear_removes_marks_but_keeps_the_crop()
        {
            var doc = new AnnotationDocument(Full);
            doc.ApplyCrop(new PixelRect(0, 0, 200, 200));
            doc.Add(Mark(5));
            doc.Clear();

            Assert.Empty(doc.Items);
            Assert.Equal(new PixelRect(0, 0, 200, 200), doc.Crop);
        }

        [Fact]
        public void History_is_bounded()
        {
            var doc = new AnnotationDocument(Full);
            for (int i = 0; i < 400; i++) doc.Add(Mark(i));

            // Still usable, and undo still works after the cap trims the oldest states.
            Assert.True(doc.CanUndo);
            Assert.True(doc.Undo());
        }
    }

    /// <summary>Hotkey strings, where a parsing slip means a shortcut silently never fires.</summary>
    public class HotkeyParserTests
    {
        [Fact]
        public void Parses_a_standard_combination()
        {
            Assert.True(HotkeyParser.TryParse("Ctrl+Shift+1", out var mods, out uint vk));
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, mods);
            Assert.Equal((uint)'1', vk);
        }

        [Theory]
        [InlineData("ctrl+shift+a")]
        [InlineData("CTRL + SHIFT + A")]
        [InlineData("Control+Shift+A")]
        public void Spelling_and_spacing_are_forgiving(string spec)
        {
            Assert.True(HotkeyParser.TryParse(spec, out var mods, out uint vk));
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, mods);
            Assert.Equal((uint)'A', vk);
        }

        [Fact]
        public void Function_and_named_keys_parse()
        {
            Assert.True(HotkeyParser.TryParse("Alt+F5", out _, out uint f5));
            Assert.Equal(0x74u, f5);

            Assert.True(HotkeyParser.TryParse("Ctrl+PrintScreen", out _, out uint prt));
            Assert.Equal(0x2Cu, prt);
        }

        [Fact]
        public void Windows_key_is_supported()
        {
            Assert.True(HotkeyParser.TryParse("Win+Shift+S", out var mods, out _));
            Assert.True(mods.HasFlag(HotkeyModifiers.Win));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("A")]              // no modifier would swallow the key system-wide
        [InlineData("Ctrl")]           // modifier only
        [InlineData("Ctrl+Nope")]      // unknown key
        [InlineData("Ctrl+A+B")]       // key must be last
        public void Invalid_specifications_are_rejected(string? spec)
        {
            Assert.False(HotkeyParser.TryParse(spec, out _, out _));
        }

        [Fact]
        public void Describe_round_trips_a_parsed_hotkey()
        {
            Assert.True(HotkeyParser.TryParse("ctrl+shift+f9", out var mods, out uint vk));
            Assert.Equal("Ctrl+Shift+F9", HotkeyParser.Describe(mods, vk));
        }
    }

    /// <summary>Config to typed settings.</summary>
    public class CaptureSettingsTests
    {
        [Fact]
        public void Null_config_yields_usable_defaults()
        {
            var s = CaptureSettings.From(null);
            Assert.Equal(CaptureFormat.Png, s.Format);
            Assert.True(s.CopyToClipboard);
            Assert.False(string.IsNullOrWhiteSpace(s.Folder));
        }

        [Fact]
        public void Blank_folder_and_template_fall_back_to_defaults()
        {
            var cfg = new Kil0bitSystemMonitor.Models.AppConfig
            {
                CaptureFolder = "",
                CaptureNameTemplate = "  ",
            };
            var s = CaptureSettings.From(cfg);
            Assert.Equal(CaptureFileNamer.DefaultFolder, s.Folder);
            Assert.Equal(CaptureFileNamer.DefaultTemplate, s.NameTemplate);
        }

        [Fact]
        public void Format_and_redaction_style_map_from_text()
        {
            var cfg = new Kil0bitSystemMonitor.Models.AppConfig
            {
                CaptureFormat = "Jpeg",
                CaptureRedactStyle = "Blur",
                CaptureJpegQuality = 500,      // out of range on purpose
                CaptureDelaySeconds = -4,
            };
            var s = CaptureSettings.From(cfg);

            Assert.Equal(CaptureFormat.Jpeg, s.Format);
            Assert.Equal(RedactStyle.Blur, s.RedactStyle);
            Assert.Equal(100, s.JpegQuality);   // clamped
            Assert.Equal(0, s.DelaySeconds);    // clamped
        }

        [Fact]
        public void Unknown_format_text_falls_back_to_png()
        {
            var cfg = new Kil0bitSystemMonitor.Models.AppConfig { CaptureFormat = "tiff" };
            Assert.Equal(CaptureFormat.Png, CaptureSettings.From(cfg).Format);
        }
    }
}
