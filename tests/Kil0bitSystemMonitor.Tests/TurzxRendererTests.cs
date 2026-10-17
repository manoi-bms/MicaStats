using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Kil0bitSystemMonitor.Services.Turzx;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class TurzxRendererTests
{
    [Theory]
    [InlineData(TurzxOrientation.Landscape, TurzxViewMode.Dashboard, 480, 320)]
    [InlineData(TurzxOrientation.Landscape, TurzxViewMode.Thermal, 480, 320)]
    [InlineData(TurzxOrientation.Landscape, TurzxViewMode.StorageNet, 480, 320)]
    [InlineData(TurzxOrientation.Landscape, TurzxViewMode.Clock, 480, 320)]
    [InlineData(TurzxOrientation.Landscape, TurzxViewMode.Splash, 480, 320)]
    [InlineData(TurzxOrientation.Portrait, TurzxViewMode.Dashboard, 320, 480)]
    [InlineData(TurzxOrientation.Portrait, TurzxViewMode.Thermal, 320, 480)]
    [InlineData(TurzxOrientation.Portrait, TurzxViewMode.StorageNet, 320, 480)]
    [InlineData(TurzxOrientation.Portrait, TurzxViewMode.Clock, 320, 480)]
    [InlineData(TurzxOrientation.Portrait, TurzxViewMode.Splash, 320, 480)]
    public void Render_produces_non_uniform_frame_for_every_orientation_and_view_mode(
        TurzxOrientation orientation,
        TurzxViewMode viewMode,
        int expectedWidth,
        int expectedHeight)
    {
        var renderer = new TurzxRenderer();
        var metrics = CreateSampleMetrics(cpu: 42f, gpu: 64f, ram: 58f);
        var now = new DateTime(2026, 10, 14, 15, 45, 30);

        ushort[] frame = renderer.Render(metrics, orientation, viewMode, now, activeAlert: null);

        Assert.Equal(expectedWidth * expectedHeight, frame.Length);
        ushort background = frame[0];
        Assert.Contains(frame, pixel => pixel != background);
    }

    [Fact]
    public void ComputeDirtyRegions_returns_full_screen_on_first_frame_and_empty_when_unchanged()
    {
        var renderer = new TurzxRenderer();
        var metrics = CreateSampleMetrics(cpu: 35f, gpu: 50f, ram: 60f);
        var now = new DateTime(2026, 10, 14, 15, 45, 00);

        ushort[] frame1 = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, now, null);

        IReadOnlyList<TurzxRect> initial = TurzxRenderer.ComputeDirtyRegions(null, frame1, 480, 320);
        Assert.Single(initial);
        Assert.Equal(new TurzxRect(0, 0, 480, 320), initial[0]);

        var clone = (ushort[])frame1.Clone();
        IReadOnlyList<TurzxRect> unchanged = TurzxRenderer.ComputeDirtyRegions(frame1, clone, 480, 320);
        Assert.Empty(unchanged);
    }

    [Fact]
    public void ComputeDirtyRegions_returns_compact_subrects_when_only_metrics_change()
    {
        var renderer = new TurzxRenderer();
        var now = new DateTime(2026, 10, 14, 15, 45, 10);
        ushort[] frame1 = renderer.Render(
            CreateSampleMetrics(cpu: 25f, gpu: 40f, ram: 55f),
            TurzxOrientation.Landscape,
            TurzxViewMode.Dashboard,
            now,
            null);

        ushort[] frame2 = renderer.Render(
            CreateSampleMetrics(cpu: 78f, gpu: 40f, ram: 55f),
            TurzxOrientation.Landscape,
            TurzxViewMode.Dashboard,
            now,
            null);

        IReadOnlyList<TurzxRect> dirty = TurzxRenderer.ComputeDirtyRegions(frame1, frame2, 480, 320);
        Assert.NotEmpty(dirty);

        int totalPixels = 0;
        foreach (TurzxRect rect in dirty)
        {
            Assert.True(rect.X >= 0 && rect.EndX < 480);
            Assert.True(rect.Y >= 0 && rect.EndY < 320);
            totalPixels += rect.Width * rect.Height;
        }
        // Only the CPU card / sparkline changed, so dirty area should be well under half the 480x320 screen.
        Assert.True(totalPixels < 480 * 320 / 2, $"Expected compact dirty area, got {totalPixels} pixels.");
    }

    [Fact]
    public void Render_draws_high_contrast_alert_banner_when_active_alert_is_present()
    {
        var renderer = new TurzxRenderer();
        var metrics = CreateSampleMetrics(cpu: 95f, gpu: 82f, ram: 70f);
        var now = new DateTime(2026, 10, 14, 15, 46, 00);

        ushort[] normal = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, now, null);
        var rule = new AlertRule("cpu-temp", "CPU temperature", AlertMetric.CpuTemperature, 85, true, 5, true);
        var alert = new AlertEvent(rule, 92, "CPU temperature 92 °C", now);
        ushort[] alerted = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, now, alert);

        IReadOnlyList<TurzxRect> diff = TurzxRenderer.ComputeDirtyRegions(normal, alerted, 480, 320);
        Assert.NotEmpty(diff);
        Assert.Contains(diff, r => r.Y == 0);
    }

    [Fact]
    public void ToBgra32_converts_rgb565_buffer_to_four_bytes_per_pixel()
    {
        ushort[] rgb565 =
        {
            TurzxProtocol.RgbTo565(255, 0, 0),
            TurzxProtocol.RgbTo565(0, 255, 0),
            TurzxProtocol.RgbTo565(0, 0, 255),
        };

        byte[] bgra = TurzxRenderer.ToBgra32(rgb565);
        Assert.Equal(12, bgra.Length);
        // Pixel 0: Red -> B=0, G=0, R>=248, A=255
        Assert.Equal(0, bgra[0]);
        Assert.Equal(0, bgra[1]);
        Assert.True(bgra[2] >= 248);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void Render_uses_antialiased_truetype_typography_with_intermediate_edge_shades()
    {
        var renderer = new TurzxRenderer();
        var metrics = CreateSampleMetrics(cpu: 34f, gpu: 42f, ram: 52f);
        var now = new DateTime(2026, 10, 14, 15, 45, 30);

        ushort[] frame = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, now, null);

        // In the header (rows 0..28), anti-aliased TrueType rendering must produce > 12 distinct RGB565 shades
        // (smooth glyph edge blending), unlike a 1-bit pixel font which only produces 3 flat colors.
        var headerColors = new HashSet<ushort>();
        for (int y = 0; y < 28; y++)
        {
            for (int x = 0; x < 480; x++)
            {
                headerColors.Add(frame[y * 480 + x]);
            }
        }
        Assert.True(headerColors.Count > 12, $"Expected anti-aliased TrueType glyph shades in header, got only {headerColors.Count} colors.");
    }

    [Fact]
    public void ComputeDirtyRegions_aligns_dirty_rects_to_4px_horizontal_and_2px_vertical_boundaries()
    {
        var renderer = new TurzxRenderer();
        var t0 = new DateTime(2026, 10, 14, 15, 45, 01);
        ushort[] frame1 = renderer.Render(
            CreateSampleMetrics(cpu: 31f, gpu: 40f, ram: 55f),
            TurzxOrientation.Landscape,
            TurzxViewMode.Dashboard,
            t0,
            null);

        ushort[] frame2 = renderer.Render(
            CreateSampleMetrics(cpu: 32f, gpu: 40f, ram: 55f),
            TurzxOrientation.Landscape,
            TurzxViewMode.Dashboard,
            t0.AddSeconds(1),
            null);

        IReadOnlyList<TurzxRect> dirty = TurzxRenderer.ComputeDirtyRegions(frame1, frame2, 480, 320);
        Assert.NotEmpty(dirty);
        foreach (TurzxRect rect in dirty)
        {
            Assert.Equal(0, rect.X % 4);
            Assert.Equal(0, rect.Y % 2);
            Assert.True(rect.Width % 4 == 0 || rect.EndX == 479);
            Assert.True(rect.Height % 2 == 0 || rect.EndY == 319);
        }
    }

    [Fact]
    public void Render_Splash_view_and_Dashboard_cards_composite_rich_cyberpunk_artwork_and_icons()
    {
        var renderer = new TurzxRenderer();
        var metrics = CreateSampleMetrics(cpu: 46f, gpu: 68f, ram: 61f);
        var now = new DateTime(2026, 10, 14, 18, 10, 00);

        ushort[] splash = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Splash, now, null);
        var centerHeroColors = new HashSet<ushort>();
        for (int y = 50; y < 220; y++)
        {
            for (int x = 100; x < 380; x++)
            {
                centerHeroColors.Add(splash[y * 480 + x]);
            }
        }
        Assert.True(centerHeroColors.Count > 300, $"Expected rich Cyberpunk Neon HUD splash artwork (>300 colors), got {centerHeroColors.Count}.");

        ushort[] dashboard = renderer.Render(metrics, TurzxOrientation.Landscape, TurzxViewMode.Dashboard, now, null);
        // Top-right area of CPU card (x: 184..228, y: 40..84) should contain the 3D neon CPU icon with > 40 distinct shades.
        var cpuIconColors = new HashSet<ushort>();
        for (int y = 40; y < 84; y++)
        {
            for (int x = 184; x < 228; x++)
            {
                cpuIconColors.Add(dashboard[y * 480 + x]);
            }
        }
        Assert.True(cpuIconColors.Count > 40, $"Expected 3D neon CPU icon composited in CPU card, got only {cpuIconColors.Count} colors.");

        string folder = System.IO.Path.Combine(PadWindowTests.RepoRoot(), "artifacts", "ui-fixtures");
        System.IO.Directory.CreateDirectory(folder);
        SaveRgb565Png(splash, 480, 320, System.IO.Path.Combine(folder, "turzx-splash-preview.png"));
        SaveRgb565Png(dashboard, 480, 320, System.IO.Path.Combine(folder, "turzx-dashboard-preview.png"));
    }

    private static void SaveRgb565Png(ushort[] rgb565, int width, int height, string path)
    {
        byte[] bgra = TurzxRenderer.ToBgra32(rgb565);
        using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static SystemMetrics CreateSampleMetrics(float cpu, float gpu, float ram)
    {
        return new SystemMetrics
        {
            CpuUsage = cpu,
            CpuTemperature = 62f,
            CpuFrequencyGhz = 4.65f,
            GpuUsage = gpu,
            GpuTemperature = 58f,
            GpuVramUsedBytes = 6UL * 1024 * 1024 * 1024,
            RamPercent = ram,
            RamUsedBytes = 18UL * 1024 * 1024 * 1024,
            RamTotalBytes = 32UL * 1024 * 1024 * 1024,
            DiskUsage = 14.5f,
            DiskPercent = 48f,
            NetDownKbps = 12_400f,
            NetUpKbps = 1_850f,
            NetDownText = "12.4 MB/s",
            NetUpText = "1.8 MB/s",
            Disks = new List<DiskMetric>
            {
                new()
                {
                    Name = "C:",
                    SpacePercent = 48f,
                    ActivityPercent = 18f,
                    FreeBytes = 520UL * 1024 * 1024 * 1024,
                    TotalBytes = 1000UL * 1024 * 1024 * 1024,
                },
            },
        };
    }
}
