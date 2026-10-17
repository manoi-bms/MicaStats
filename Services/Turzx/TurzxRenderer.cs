using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Color = System.Drawing.Color;
using FontFamily = System.Drawing.FontFamily;
using FontStyle = System.Drawing.FontStyle;
using Pen = System.Drawing.Pen;

namespace Kil0bitSystemMonitor.Services.Turzx;

/// <summary>
/// Anti-aliased TrueType RGB565 Cyberpunk Neon HUD renderer for the Turzx 3.5" IPS USB-C display
/// (<c>480×320</c> Landscape and <c>320×480</c> Portrait) with AI-generated splash artwork,
/// 3D neon RGBA hardware icons, and 4-pixel aligned dirty-rectangle diffing.
/// </summary>
public sealed class TurzxRenderer
{
    private const int HistoryCapacity = 48;

    private static readonly object AssetLock = new();
    private static readonly Bitmap? SplashLandscape = LoadSplashResource("MicaStats.Turzx.splash-landscape.png");
    private static readonly Bitmap? SplashPortrait = LoadSplashResource("MicaStats.Turzx.splash-portrait.png");
    private static readonly Bitmap? BackdropLandscape = CreateDimmedBackdrop(SplashLandscape, 0.30f);
    private static readonly Bitmap? BackdropPortrait = CreateDimmedBackdrop(SplashPortrait, 0.30f);
    private static readonly Bitmap? IconCpu = LoadIconResource("MicaStats.Turzx.icon-cpu.png");
    private static readonly Bitmap? IconGpu = LoadIconResource("MicaStats.Turzx.icon-gpu.png");
    private static readonly Bitmap? IconRam = LoadIconResource("MicaStats.Turzx.icon-ram.png");
    private static readonly Bitmap? IconNetDisk = LoadIconResource("MicaStats.Turzx.icon-netdisk.png");

    private static readonly Color ColorBgDeep = Color.FromArgb(9, 13, 22);
    private static readonly Color ColorBgHeader = Color.FromArgb(215, 11, 17, 30);
    private static readonly Color ColorBgCard = Color.FromArgb(206, 13, 20, 35);
    private static readonly Color ColorBgCardAlt = Color.FromArgb(218, 16, 25, 42);
    private static readonly Color ColorBgHudGlass = Color.FromArgb(212, 8, 13, 24);
    private static readonly Color ColorBorder = Color.FromArgb(180, 42, 68, 108);
    private static readonly Color ColorTrack = Color.FromArgb(220, 24, 36, 58);
    private static readonly Color ColorTextPrimary = Color.FromArgb(242, 248, 255);
    private static readonly Color ColorTextMuted = Color.FromArgb(152, 178, 212);
    private static readonly Color ColorCyan = Color.FromArgb(0, 224, 255);
    private static readonly Color ColorGreen = Color.FromArgb(16, 238, 154);
    private static readonly Color ColorAmber = Color.FromArgb(255, 188, 32);
    private static readonly Color ColorPurple = Color.FromArgb(192, 112, 255);
    private static readonly Color ColorRed = Color.FromArgb(255, 68, 88);
    private static readonly Color ColorAlertBg = Color.FromArgb(235, 148, 20, 36);

    private static readonly Font FontHeaderTitle = CreateFont("Segoe UI", 14f, FontStyle.Bold);
    private static readonly Font FontHeaderBadge = CreateFont("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font FontHeaderClock = CreateFont("Segoe UI", 14f, FontStyle.Bold);
    private static readonly Font FontCardTitle = CreateFont("Segoe UI", 11.5f, FontStyle.Bold);
    private static readonly Font FontValueLarge = CreateFont("Segoe UI", 28f, FontStyle.Bold);
    private static readonly Font FontValueCompact = CreateFont("Segoe UI", 22f, FontStyle.Bold);
    private static readonly Font FontSubline = CreateFont("Segoe UI", 11.5f, FontStyle.Bold);
    private static readonly Font FontNetPrimary = CreateFont("Segoe UI", 14.5f, FontStyle.Bold);
    private static readonly Font FontNetSecondary = CreateFont("Segoe UI", 11.5f, FontStyle.Bold);
    private static readonly Font FontClockLandscape = CreateFont("Segoe UI", 52f, FontStyle.Bold);
    private static readonly Font FontClockPortrait = CreateFont("Segoe UI", 38f, FontStyle.Bold);
    private static readonly Font FontClockDate = CreateFont("Segoe UI", 15f, FontStyle.Bold);
    private static readonly Font FontSummary = CreateFont("Segoe UI", 13.5f, FontStyle.Bold);
    private static readonly Font FontSplashHeroClock = CreateFont("Segoe UI", 32f, FontStyle.Bold);
    private static readonly Font FontSplashSubtitle = CreateFont("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font FontSplashBadgeTitle = CreateFont("Segoe UI", 10.5f, FontStyle.Bold);
    private static readonly Font FontSplashBadgeValue = CreateFont("Segoe UI", 15.5f, FontStyle.Bold);

    private readonly float[] _cpuHistory = new float[HistoryCapacity];
    private readonly float[] _gpuHistory = new float[HistoryCapacity];
    private readonly float[] _ramHistory = new float[HistoryCapacity];
    private readonly float[] _netHistory = new float[HistoryCapacity];
    private int _historyCount;
    private DateTime _lastRecordedTimestamp = DateTime.MinValue;

    public static bool HasEmbeddedArtwork =>
        SplashLandscape != null && SplashPortrait != null &&
        IconCpu != null && IconGpu != null && IconRam != null && IconNetDisk != null;

    public ushort[] Render(
        SystemMetrics? metrics,
        TurzxOrientation orientation,
        TurzxViewMode viewMode,
        DateTime now,
        AlertEvent? activeAlert = null)
    {
        (int width, int height) = TurzxDimensions.ForOrientation(orientation);
        var m = metrics ?? new SystemMetrics();

        if (now != _lastRecordedTimestamp)
        {
            RecordHistory(m);
            _lastRecordedTimestamp = now;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = CreateGraphics(bitmap))
        {
            g.Clear(ColorBgDeep);
            bool landscape = width >= height;

            if (viewMode == TurzxViewMode.Splash)
            {
                DrawSplashView(g, width, height, m, now, landscape, activeAlert);
            }
            else
            {
                DrawBackdrop(g, width, height, landscape);
                DrawHeader(g, width, now, activeAlert);

                switch (viewMode)
                {
                    case TurzxViewMode.Thermal:
                        DrawThermalView(g, width, height, m, landscape);
                        break;
                    case TurzxViewMode.StorageNet:
                        DrawStorageNetView(g, width, height, m, landscape);
                        break;
                    case TurzxViewMode.Clock:
                        DrawClockView(g, width, height, m, now, landscape);
                        break;
                    default:
                        DrawDashboardView(g, width, height, m, landscape);
                        break;
                }

                if (activeAlert != null)
                    DrawAlertBanner(g, width, activeAlert);
            }
        }

        return BitmapToRgb565(bitmap);
    }

    public ushort[] RenderTestPattern(TurzxOrientation orientation, DateTime now)
    {
        var demoMetrics = new SystemMetrics
        {
            CpuUsage = 38f,
            CpuTemperature = 54f,
            CpuFrequencyGhz = 4.85f,
            GpuUsage = 64f,
            GpuTemperature = 59f,
            GpuVramUsedBytes = 8UL * 1024 * 1024 * 1024,
            RamPercent = 56f,
            RamUsedBytes = 18UL * 1024 * 1024 * 1024,
            RamTotalBytes = 32UL * 1024 * 1024 * 1024,
            DiskPercent = 44f,
            DiskUsage = 12.8f,
            NetDownKbps = 18_400f,
            NetUpKbps = 2_400f,
            NetDownText = "18.0 MB/s",
            NetUpText = "2.3 MB/s",
        };
        return Render(demoMetrics, orientation, TurzxViewMode.Splash, now, activeAlert: null);
    }

    public static IReadOnlyList<TurzxRect> ComputeDirtyRegions(
        ushort[]? previousFrame,
        ushort[] currentFrame,
        int width,
        int height,
        int bandHeight = 16)
    {
        ArgumentNullException.ThrowIfNull(currentFrame);
        if (width <= 0 || height <= 0 || currentFrame.Length != width * height)
            throw new ArgumentException("Frame dimensions do not match buffer length.");

        if (previousFrame == null || previousFrame.Length != currentFrame.Length)
            return new[] { new TurzxRect(0, 0, width, height) };

        int step = Math.Max(8, bandHeight);
        var rects = new List<TurzxRect>();

        for (int bandY = 0; bandY < height; bandY += step)
        {
            int bandEndY = Math.Min(height - 1, bandY + step - 1);
            int minX = int.MaxValue;
            int maxX = -1;
            int minY = int.MaxValue;
            int maxY = -1;

            for (int y = bandY; y <= bandEndY; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (previousFrame[rowOffset + x] != currentFrame[rowOffset + x])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX)
                continue;

            TurzxRect candidate = AlignRect(minX, minY, maxX, maxY, width, height);
            if (rects.Count > 0)
            {
                TurzxRect prev = rects[^1];
                bool verticallyAdjacent = candidate.Y <= prev.EndY + 6;
                int unionMinX = Math.Min(prev.X, candidate.X);
                int unionMaxX = Math.Max(prev.EndX, candidate.EndX);
                int unionWidth = unionMaxX - unionMinX + 1;
                if (verticallyAdjacent && unionWidth <= Math.Max(prev.Width, candidate.Width) + 64)
                {
                    int unionMinY = Math.Min(prev.Y, candidate.Y);
                    int unionMaxY = Math.Max(prev.EndY, candidate.EndY);
                    rects[^1] = AlignRect(unionMinX, unionMinY, unionMaxX, unionMaxY, width, height);
                    continue;
                }
            }

            rects.Add(candidate);
        }

        return rects;
    }

    private static TurzxRect AlignRect(int minX, int minY, int maxX, int maxY, int frameWidth, int frameHeight)
    {
        int x0 = Math.Max(0, minX & ~3);
        int y0 = Math.Max(0, minY & ~1);
        int x1 = Math.Min(frameWidth - 1, ((maxX + 4) & ~3) - 1);
        int y1 = Math.Min(frameHeight - 1, ((maxY + 2) & ~1) - 1);
        return new TurzxRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    public static byte[] ToBgra32(ushort[] rgb565)
    {
        ArgumentNullException.ThrowIfNull(rgb565);
        var bgra = new byte[rgb565.Length * 4];
        int dst = 0;
        for (int i = 0; i < rgb565.Length; i++)
        {
            (byte r, byte g, byte b) = TurzxProtocol.RgbFrom565(rgb565[i]);
            bgra[dst++] = b;
            bgra[dst++] = g;
            bgra[dst++] = r;
            bgra[dst++] = 255;
        }
        return bgra;
    }

    private static Graphics CreateGraphics(Bitmap bitmap)
    {
        Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    private static ushort[] BitmapToRgb565(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = new ushort[width * height];
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] raw = new byte[stride * height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            int dst = 0;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x * 4;
                    byte b = raw[idx];
                    byte g = raw[idx + 1];
                    byte r = raw[idx + 2];
                    pixels[dst++] = TurzxProtocol.RgbTo565(r, g, b);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return pixels;
    }

    private void RecordHistory(SystemMetrics m)
    {
        PushSample(_cpuHistory, Math.Clamp(m.CpuUsage, 0f, 100f));
        PushSample(_gpuHistory, Math.Clamp(m.GpuUsage, 0f, 100f));
        PushSample(_ramHistory, Math.Clamp(m.RamPercent, 0f, 100f));
        float netPercent = Math.Clamp((m.NetDownKbps + m.NetUpKbps) / 250f, 0f, 100f);
        PushSample(_netHistory, netPercent);
        if (_historyCount < HistoryCapacity)
            _historyCount++;
    }

    private static void PushSample(float[] buffer, float value)
    {
        Array.Copy(buffer, 1, buffer, 0, buffer.Length - 1);
        buffer[^1] = value;
    }

    private static void DrawBackdrop(Graphics g, int width, int height, bool landscape)
    {
        Bitmap? backdrop = landscape ? BackdropLandscape : BackdropPortrait;
        if (backdrop == null) return;
        lock (AssetLock)
        {
            g.DrawImage(backdrop, 0, 0, width, height);
        }
    }

    private static void DrawHeader(Graphics g, int width, DateTime now, AlertEvent? activeAlert)
    {
        using (var bg = new SolidBrush(ColorBgHeader))
            g.FillRectangle(bg, 0, 0, width, 30);
        using (var border = new SolidBrush(ColorBorder))
            g.FillRectangle(border, 0, 29, width, 1);
        using (var topGlow = new SolidBrush(Color.FromArgb(140, 0, 224, 255)))
            g.FillRectangle(topGlow, 0, 0, width / 3, 2);

        using (var pill = new SolidBrush(activeAlert != null ? ColorRed : ColorCyan))
            g.FillRectangle(pill, 8, 7, 4, 16);

        DrawString(g, "MICASTATS", FontHeaderTitle, ColorTextPrimary, 16, 5, 130);
        if (width >= 400)
            DrawString(g, "CYBER HUD 3.5\"", FontHeaderBadge, ColorCyan, 112, 7, 130);

        string timeText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DrawStringRight(g, timeText, FontHeaderClock, ColorTextPrimary, width - 10, 6);
    }

    private static void DrawAlertBanner(Graphics g, int width, AlertEvent alert)
    {
        using (var bg = new SolidBrush(ColorAlertBg))
            g.FillRectangle(bg, 0, 0, width, 30);
        using (var border = new SolidBrush(ColorRed))
            g.FillRectangle(border, 0, 29, width, 1);

        string text = string.IsNullOrWhiteSpace(alert.Message)
            ? "ALERT: THRESHOLD BREACHED"
            : "ALERT: " + alert.Message.ToUpperInvariant();
        DrawString(g, text, FontHeaderTitle, ColorTextPrimary, 10, 5, width - 20);
    }

    private void DrawSplashView(Graphics g, int width, int height, SystemMetrics m, DateTime now, bool landscape, AlertEvent? activeAlert)
    {
        Bitmap? splash = landscape ? SplashLandscape : SplashPortrait;
        if (splash != null)
        {
            lock (AssetLock)
            {
                g.DrawImage(splash, 0, 0, width, height);
            }
        }

        // Top translucent glass HUD bar
        using (var topGlass = new SolidBrush(ColorBgHudGlass))
            g.FillRectangle(topGlass, 0, 0, width, 36);
        using (var cyanLine = new SolidBrush(ColorCyan))
            g.FillRectangle(cyanLine, 0, 35, width / 2, 1);
        using (var purpleLine = new SolidBrush(ColorPurple))
            g.FillRectangle(purpleLine, width / 2, 35, width - width / 2, 1);

        using (var pill = new SolidBrush(ColorCyan))
            g.FillRectangle(pill, 10, 8, 4, 20);

        DrawString(g, "MICASTATS", FontHeaderTitle, ColorTextPrimary, 18, 4, 140);
        DrawString(g, "QUANTUM TELEMETRY CORE", FontSplashSubtitle, ColorCyan, 18, 19, 200);

        string clockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DrawStringRight(g, clockText, FontHeaderClock, ColorTextPrimary, width - 10, 4);
        string dateText = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DrawStringRight(g, dateText, FontSplashSubtitle, ColorGreen, width - 10, 19);

        if (landscape)
        {
            // Bottom translucent glass telemetry dock (480x76)
            int dockY = height - 76;
            using (var dockBg = new SolidBrush(ColorBgHudGlass))
                g.FillRectangle(dockBg, 8, dockY, width - 16, 68);
            using (var dockBorder = new Pen(ColorBorder, 1f))
                g.DrawRectangle(dockBorder, 8, dockY, width - 17, 67);
            using (var accentLeft = new SolidBrush(ColorCyan))
                g.FillRectangle(accentLeft, 8, dockY, 42, 2);
            using (var accentRight = new SolidBrush(ColorGreen))
                g.FillRectangle(accentRight, width - 50, dockY, 42, 2);

            int cellW = (width - 24) / 4; // 114
            DrawSplashMetricPill(g, 12, dockY + 6, cellW - 4, 56, "CPU", FormatPercent(m.CpuUsage), m.CpuUsage, ColorCyan, IconCpu);
            DrawSplashMetricPill(g, 12 + cellW, dockY + 6, cellW - 4, 56, "GPU", FormatPercent(m.GpuUsage), m.GpuUsage, ColorGreen, IconGpu);
            DrawSplashMetricPill(g, 12 + cellW * 2, dockY + 6, cellW - 4, 56, "RAM", FormatPercent(m.RamPercent), m.RamPercent, ColorPurple, IconRam);
            float netPercent = Math.Clamp((m.NetDownKbps + m.NetUpKbps) / 250f, 8f, 100f);
            DrawSplashMetricPill(g, 12 + cellW * 3, dockY + 6, cellW - 4, 56, "DISK", FormatPercent(m.DiskPercent), Math.Max(m.DiskPercent, netPercent), ColorAmber, IconNetDisk);
        }
        else
        {
            // Portrait bottom 2x2 glass telemetry dock (320x124)
            int dockY = height - 126;
            using (var dockBg = new SolidBrush(ColorBgHudGlass))
                g.FillRectangle(dockBg, 8, dockY, width - 16, 118);
            using (var dockBorder = new Pen(ColorBorder, 1f))
                g.DrawRectangle(dockBorder, 8, dockY, width - 17, 117);

            int cellW = (width - 24) / 2;
            DrawSplashMetricPill(g, 12, dockY + 6, cellW - 4, 50, "CPU", FormatPercent(m.CpuUsage), m.CpuUsage, ColorCyan, IconCpu);
            DrawSplashMetricPill(g, 12 + cellW, dockY + 6, cellW - 4, 50, "GPU", FormatPercent(m.GpuUsage), m.GpuUsage, ColorGreen, IconGpu);
            DrawSplashMetricPill(g, 12, dockY + 62, cellW - 4, 50, "RAM", FormatPercent(m.RamPercent), m.RamPercent, ColorPurple, IconRam);
            DrawSplashMetricPill(g, 12 + cellW, dockY + 62, cellW - 4, 50, "DISK", FormatPercent(m.DiskPercent), m.DiskPercent, ColorAmber, IconNetDisk);
        }

        if (activeAlert != null)
            DrawAlertBanner(g, width, activeAlert);
    }

    private static void DrawSplashMetricPill(
        Graphics g,
        int x,
        int y,
        int width,
        int height,
        string label,
        string valueText,
        float percent,
        Color accentColor,
        Bitmap? icon)
    {
        using (var bg = new SolidBrush(Color.FromArgb(145, 16, 24, 42)))
            g.FillRectangle(bg, x, y, width, height);
        using (var topBar = new SolidBrush(accentColor))
            g.FillRectangle(topBar, x, y, 22, 2);

        int iconSize = 32;
        if (icon != null)
        {
            DrawIconWithGlow(g, icon, x + 4, y + 4, iconSize, accentColor);
        }

        int textX = x + 38;
        int textW = Math.Max(24, width - 40);
        DrawString(g, label, FontSplashBadgeTitle, ColorTextMuted, textX, y + 3, textW);
        DrawString(g, valueText, FontSplashBadgeValue, ColorTextPrimary, textX, y + 16, textW);

        DrawProgressBar(g, x + 6, y + height - 10, width - 12, 5, percent, accentColor, ColorTrack);
    }

    private void DrawDashboardView(Graphics g, int width, int height, SystemMetrics m, bool landscape)
    {
        if (landscape)
        {
            int pad = 8;
            int top = 36;
            int cardW = (width - pad * 3) / 2;   // 228
            int cardH = (height - top - pad * 2) / 2; // 134

            DrawMetricCard(
                g, pad, top, cardW, cardH,
                "CPU LOAD",
                FormatPercent(m.CpuUsage),
                FormatCpuSubline(m),
                m.CpuUsage,
                SelectLoadColor(m.CpuUsage, ColorCyan),
                _cpuHistory,
                IconCpu);

            DrawMetricCard(
                g, pad * 2 + cardW, top, cardW, cardH,
                "GPU CORE",
                FormatPercent(m.GpuUsage),
                FormatGpuSubline(m),
                m.GpuUsage,
                SelectLoadColor(m.GpuUsage, ColorGreen),
                _gpuHistory,
                IconGpu);

            DrawMetricCard(
                g, pad, top + cardH + pad, cardW, cardH,
                "MEMORY",
                FormatPercent(m.RamPercent),
                FormatRamSubline(m),
                m.RamPercent,
                SelectLoadColor(m.RamPercent, ColorPurple),
                _ramHistory,
                IconRam);

            DrawDiskNetCard(
                g, pad * 2 + cardW, top + cardH + pad, cardW, cardH, m);
        }
        else
        {
            int pad = 8;
            int top = 36;
            int cardW = width - pad * 2;
            int cardH = (height - top - pad * 4) / 4; // ~103

            DrawMetricCard(
                g, pad, top, cardW, cardH,
                "CPU LOAD",
                FormatPercent(m.CpuUsage),
                FormatCpuSubline(m),
                m.CpuUsage,
                SelectLoadColor(m.CpuUsage, ColorCyan),
                _cpuHistory,
                IconCpu);

            DrawMetricCard(
                g, pad, top + (cardH + pad), cardW, cardH,
                "GPU CORE",
                FormatPercent(m.GpuUsage),
                FormatGpuSubline(m),
                m.GpuUsage,
                SelectLoadColor(m.GpuUsage, ColorGreen),
                _gpuHistory,
                IconGpu);

            DrawMetricCard(
                g, pad, top + (cardH + pad) * 2, cardW, cardH,
                "MEMORY",
                FormatPercent(m.RamPercent),
                FormatRamSubline(m),
                m.RamPercent,
                SelectLoadColor(m.RamPercent, ColorPurple),
                _ramHistory,
                IconRam);

            DrawDiskNetCard(
                g, pad, top + (cardH + pad) * 3, cardW, cardH, m);
        }
    }

    private void DrawThermalView(Graphics g, int width, int height, SystemMetrics m, bool landscape)
    {
        int pad = 8;
        int top = 36;
        float cpuTemp = m.CpuTemperature >= 0 ? m.CpuTemperature : m.CpuUsage * 0.65f + 35f;
        float gpuTemp = m.GpuTemperature >= 0 ? m.GpuTemperature : m.GpuUsage * 0.60f + 34f;
        string cpuTempLabel = m.CpuTemperature >= 0
            ? $"{Math.Round(m.CpuTemperature):0}°C"
            : $"{Math.Round(m.CpuUsage):0}%";
        string gpuTempLabel = m.GpuTemperature >= 0
            ? $"{Math.Round(m.GpuTemperature):0}°C"
            : $"{Math.Round(m.GpuUsage):0}%";

        if (landscape)
        {
            int cardW = (width - pad * 3) / 2;
            int cardH = height - top - pad;
            DrawMetricCard(
                g, pad, top, cardW, cardH,
                "CPU THERMAL",
                cpuTempLabel,
                $"LOAD {Math.Round(m.CpuUsage):0}%  {FormatClock(m.CpuFrequencyGhz)}",
                Math.Clamp(cpuTemp, 0f, 100f),
                SelectThermalColor(cpuTemp),
                _cpuHistory,
                IconCpu);

            DrawMetricCard(
                g, pad * 2 + cardW, top, cardW, cardH,
                "GPU THERMAL",
                gpuTempLabel,
                $"LOAD {Math.Round(m.GpuUsage):0}%  VRAM {FormatGiB(m.GpuVramUsedBytes)}",
                Math.Clamp(gpuTemp, 0f, 100f),
                SelectThermalColor(gpuTemp),
                _gpuHistory,
                IconGpu);
        }
        else
        {
            int cardW = width - pad * 2;
            int cardH = (height - top - pad * 2) / 2;
            DrawMetricCard(
                g, pad, top, cardW, cardH,
                "CPU THERMAL",
                cpuTempLabel,
                $"LOAD {Math.Round(m.CpuUsage):0}%  {FormatClock(m.CpuFrequencyGhz)}",
                Math.Clamp(cpuTemp, 0f, 100f),
                SelectThermalColor(cpuTemp),
                _cpuHistory,
                IconCpu);

            DrawMetricCard(
                g, pad, top + cardH + pad, cardW, cardH,
                "GPU THERMAL",
                gpuTempLabel,
                $"LOAD {Math.Round(m.GpuUsage):0}%  VRAM {FormatGiB(m.GpuVramUsedBytes)}",
                Math.Clamp(gpuTemp, 0f, 100f),
                SelectThermalColor(gpuTemp),
                _gpuHistory,
                IconGpu);
        }
    }

    private void DrawStorageNetView(Graphics g, int width, int height, SystemMetrics m, bool landscape)
    {
        int pad = 8;
        int top = 36;
        int cardW = width - pad * 2;
        int halfH = (height - top - pad * 2) / 2;

        DrawCard(g, pad, top, cardW, halfH, ColorBgCard, ColorCyan);
        if (IconNetDisk != null)
            DrawIconWithGlow(g, IconNetDisk, pad + cardW - 48, top + 6, 40, ColorCyan);
        DrawString(g, "NETWORK THROUGHPUT", FontCardTitle, ColorCyan, pad + 10, top + 6, cardW - 60);
        DrawString(g, $"DN {NormalizeRate(m.NetDownText, m.NetDownKbps)}", FontNetPrimary, ColorTextPrimary, pad + 10, top + 24, cardW - 60);
        DrawString(g, $"UP {NormalizeRate(m.NetUpText, m.NetUpKbps)}", FontNetPrimary, ColorGreen, pad + 10, top + 46, cardW - 60);
        if (!string.IsNullOrWhiteSpace(m.NetIpAddress))
            DrawString(g, $"IP {m.NetIpAddress}", FontSubline, ColorTextMuted, pad + 10, top + 70, cardW - 20);

        int sparkX = landscape ? pad + cardW / 2 : pad + 10;
        int sparkY = landscape ? top + 46 : top + halfH - 42;
        int sparkW = landscape ? cardW / 2 - 16 : cardW - 20;
        int sparkH = landscape ? halfH - 56 : 32;
        if (sparkH >= 16 && sparkW >= 40)
            DrawSparkline(g, sparkX, sparkY, sparkW, sparkH, _netHistory, ColorCyan);

        int diskTop = top + halfH + pad;
        DrawCard(g, pad, diskTop, cardW, halfH, ColorBgCard, ColorAmber);
        DrawString(g, $"STORAGE  /  IO {m.DiskUsage:0.0} MB/S", FontCardTitle, ColorAmber, pad + 10, diskTop + 6, cardW - 20);

        int rowY = diskTop + 26;
        if (m.Disks is { Count: > 0 })
        {
            int shown = 0;
            foreach (DiskMetric disk in m.Disks)
            {
                if (rowY + 26 > diskTop + halfH - 4 || shown >= 3) break;
                string label = $"{disk.Name}  {Math.Round(disk.SpacePercent):0}% USED";
                DrawString(g, label, FontSubline, ColorTextPrimary, pad + 10, rowY, cardW - 20);
                DrawProgressBar(g, pad + 10, rowY + 16, cardW - 20, 8, disk.SpacePercent, ColorAmber, ColorTrack);
                rowY += 28;
                shown++;
            }
        }
        else
        {
            DrawString(g, $"DISK USED {Math.Round(m.DiskPercent):0}%", FontNetPrimary, ColorTextPrimary, pad + 10, rowY, cardW - 20);
            DrawProgressBar(g, pad + 10, rowY + 26, cardW - 20, 12, m.DiskPercent, ColorAmber, ColorTrack);
        }
    }

    private static void DrawClockView(Graphics g, int width, int height, SystemMetrics m, DateTime now, bool landscape)
    {
        int pad = 10;
        int top = 38;
        int cardW = width - pad * 2;
        int clockH = height - top - 78;

        DrawCard(g, pad, top, cardW, clockH, ColorBgCard, ColorCyan);
        string clockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Font clockFont = landscape ? FontClockLandscape : FontClockPortrait;
        int clockY = top + Math.Max(12, (clockH - 64) / 2 - 8);
        DrawStringCentered(g, clockText, clockFont, ColorTextPrimary, pad, clockY, cardW);

        string dateText = now.ToString("yyyy-MM-dd  ddd", CultureInfo.InvariantCulture).ToUpperInvariant();
        DrawStringCentered(g, dateText, FontClockDate, ColorCyan, pad, clockY + (landscape ? 60 : 48), cardW);

        int stripY = top + clockH + 8;
        int stripH = height - stripY - 8;
        DrawCard(g, pad, stripY, cardW, stripH, ColorBgCardAlt, ColorBorder);

        string summary = $"CPU {Math.Round(m.CpuUsage):0}%   GPU {Math.Round(m.GpuUsage):0}%   RAM {Math.Round(m.RamPercent):0}%";
        DrawString(g, summary, FontSummary, ColorGreen, pad + 12, stripY + 8, cardW - 24);
        DrawString(g, $"DN {NormalizeRate(m.NetDownText, m.NetDownKbps)}   UP {NormalizeRate(m.NetUpText, m.NetUpKbps)}", FontSubline, ColorTextMuted, pad + 12, stripY + 30, cardW - 24);
    }

    private static void DrawMetricCard(
        Graphics g,
        int x,
        int y,
        int width,
        int height,
        string title,
        string primaryValue,
        string subline,
        float percent,
        Color accentColor,
        float[] sparkline,
        Bitmap? icon)
    {
        DrawCard(g, x, y, width, height, ColorBgCard, accentColor);

        bool largeCard = height >= 115;
        int iconSize = largeCard ? 42 : 34;
        if (icon != null)
        {
            DrawIconWithGlow(g, icon, x + width - iconSize - 6, y + 6, iconSize, accentColor);
        }

        int textMaxW = width - iconSize - 18;
        DrawString(g, title, FontCardTitle, ColorTextMuted, x + 10, y + 6, textMaxW);

        Font valueFont = largeCard ? FontValueLarge : FontValueCompact;
        DrawString(g, primaryValue, valueFont, ColorTextPrimary, x + 8, y + 20, textMaxW);

        int subY = largeCard ? y + 54 : y + 46;
        DrawString(g, subline, FontSubline, accentColor, x + 10, subY, width - 20);

        int barY = y + height - 16;
        int sparkTop = subY + 18;
        int sparkHeight = barY - sparkTop - 5;
        if (sparkHeight >= 12)
        {
            DrawSparkline(g, x + 10, sparkTop, width - 20, sparkHeight, sparkline, accentColor);
        }

        DrawProgressBar(g, x + 10, barY, width - 20, 8, percent, accentColor, ColorTrack);
    }

    private void DrawDiskNetCard(Graphics g, int x, int y, int width, int height, SystemMetrics m)
    {
        DrawCard(g, x, y, width, height, ColorBgCard, ColorAmber);

        bool largeCard = height >= 115;
        int iconSize = largeCard ? 42 : 34;
        if (IconNetDisk != null)
        {
            DrawIconWithGlow(g, IconNetDisk, x + width - iconSize - 6, y + 6, iconSize, ColorAmber);
        }

        int textMaxW = width - iconSize - 18;
        DrawString(g, "NET & STORAGE", FontCardTitle, ColorTextMuted, x + 10, y + 6, textMaxW);

        string down = "DN " + NormalizeRate(m.NetDownText, m.NetDownKbps);
        string up = "UP " + NormalizeRate(m.NetUpText, m.NetUpKbps);
        DrawString(g, down, FontNetPrimary, ColorCyan, x + 10, y + 22, textMaxW);
        DrawString(g, up, FontNetSecondary, ColorGreen, x + 10, y + 42, textMaxW);

        string diskLine = $"DISK {Math.Round(m.DiskPercent):0}%  /  IO {m.DiskUsage:0.0} MB/S";
        DrawString(g, diskLine, FontNetSecondary, ColorAmber, x + 10, y + 58, width - 20);

        int barY = y + height - 16;
        int sparkTop = y + 74;
        int sparkHeight = barY - sparkTop - 5;
        if (sparkHeight >= 10)
            DrawSparkline(g, x + 10, sparkTop, width - 20, sparkHeight, _netHistory, ColorAmber);

        DrawProgressBar(g, x + 10, barY, width - 20, 8, m.DiskPercent, ColorAmber, ColorTrack);
    }

    private static void DrawIconWithGlow(Graphics g, Bitmap icon, int x, int y, int size, Color glowColor)
    {
        using (var outerGlow = new SolidBrush(Color.FromArgb(36, glowColor.R, glowColor.G, glowColor.B)))
            g.FillEllipse(outerGlow, x - 3, y - 3, size + 6, size + 6);
        using (var innerGlow = new SolidBrush(Color.FromArgb(64, glowColor.R, glowColor.G, glowColor.B)))
            g.FillEllipse(innerGlow, x + 2, y + 2, size - 4, size - 4);

        lock (AssetLock)
        {
            g.DrawImage(icon, x, y, size, size);
        }
    }

    private static void DrawCard(Graphics g, int x, int y, int width, int height, Color bg, Color accent)
    {
        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, x, y, width, height);
        using (var borderPen = new Pen(ColorBorder, 1f))
            g.DrawRectangle(borderPen, x, y, width - 1, height - 1);
        using (var accentBrush = new SolidBrush(accent))
        {
            // Top-left cyber corner bracket
            g.FillRectangle(accentBrush, x, y, Math.Min(42, width), 2);
            g.FillRectangle(accentBrush, x, y, 2, Math.Min(20, height));
            // Bottom-right cyber corner tick
            if (width > 24 && height > 24)
            {
                g.FillRectangle(accentBrush, x + width - 18, y + height - 2, 18, 2);
                g.FillRectangle(accentBrush, x + width - 2, y + height - 14, 2, 14);
            }
        }
    }

    private static void DrawProgressBar(Graphics g, int x, int y, int width, int height, float percent, Color fill, Color track)
    {
        using (var trackBrush = new SolidBrush(track))
            g.FillRectangle(trackBrush, x, y, width, height);
        int filled = (int)Math.Round(Math.Clamp(percent, 0f, 100f) / 100.0 * width);
        if (filled > 0)
        {
            using (var fillBrush = new SolidBrush(fill))
                g.FillRectangle(fillBrush, x, y, filled, height);
            if (filled >= 3)
            {
                using var headBrush = new SolidBrush(ColorTextPrimary);
                g.FillRectangle(headBrush, x + filled - 2, y, 2, height);
            }
        }
    }

    private static void DrawSparkline(Graphics g, int x, int y, int width, int height, float[] values, Color color)
    {
        if (width <= 4 || height <= 4 || values.Length == 0) return;
        using (var baseBrush = new SolidBrush(ColorBorder))
            g.FillRectangle(baseBrush, x, y + height - 1, width, 1);

        using var topBrush = new SolidBrush(color);
        using var fillBrush = new SolidBrush(Color.FromArgb(100, color.R, color.G, color.B));

        int count = values.Length;
        for (int col = 0; col < width; col++)
        {
            int idx = (col * count) / width;
            float val = Math.Clamp(values[idx], 0f, 100f);
            int barH = (int)Math.Round(val / 100.0 * (height - 2));
            if (barH <= 0) continue;

            int px = x + col;
            int topY = y + height - 1 - barH;
            g.FillRectangle(topBrush, px, topY, 1, 1);
            if (barH > 1)
                g.FillRectangle(fillBrush, px, topY + 1, 1, barH - 1);
        }
    }

    private static void DrawString(Graphics g, string text, Font font, Color color, int x, int y, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(text, font, brush, new RectangleF(x, y, maxWidth, font.Height + 6), format);
    }

    private static void DrawStringRight(Graphics g, string text, Font font, Color color, int rightX, int y)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, new PointF(rightX, y), format);
    }

    private static void DrawStringCentered(Graphics g, string text, Font font, Color color, int x, int y, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, new RectangleF(x, y, width, font.Height + 8), format);
    }

    private static Font CreateFont(string family, float pixelSize, FontStyle style)
    {
        try
        {
            return new Font(family, pixelSize, style, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, pixelSize, style, GraphicsUnit.Pixel);
        }
    }

    private static Bitmap? LoadSplashResource(string resourceName)
    {
        try
        {
            Assembly asm = typeof(TurzxRenderer).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var raw = new Bitmap(stream);
            var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
            }
            return copy;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? CreateDimmedBackdrop(Bitmap? source, float brightness)
    {
        if (source == null) return null;
        try
        {
            var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dimmed))
            {
                g.Clear(ColorBgDeep);
                using var attrs = new ImageAttributes();
                var matrix = new ColorMatrix(new[]
                {
                    new float[] { brightness, 0, 0, 0, 0 },
                    new float[] { 0, brightness, 0, 0, 0 },
                    new float[] { 0, 0, brightness * 1.15f, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0.02f, 0.03f, 0.06f, 0, 1 },
                });
                attrs.SetColorMatrix(matrix);
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, source.Width, source.Height),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel,
                    attrs);
            }
            return dimmed;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? LoadIconResource(string resourceName)
    {
        Bitmap? bmp = LoadSplashResource(resourceName);
        if (bmp == null) return null;

        // Key out any residual light/white outer halo pixels so the 3D neon icon composites cleanly on dark glass
        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] raw = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            for (int y = 0; y < bmp.Height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int idx = rowOffset + x * 4;
                    byte b = raw[idx];
                    byte g = raw[idx + 1];
                    byte r = raw[idx + 2];
                    byte a = raw[idx + 3];
                    if (a == 0) continue;

                    int minChannel = Math.Min(r, Math.Min(g, b));
                    int maxChannel = Math.Max(r, Math.Max(g, b));
                    int saturation = maxChannel - minChannel;

                    // Neutral white/gray studio background pixels have high minChannel and low saturation
                    if (minChannel > 215 && saturation < 32)
                    {
                        raw[idx + 3] = 0;
                    }
                    else if (minChannel > 175 && saturation < 42)
                    {
                        float fade = Math.Clamp((215f - minChannel) / 40f, 0f, 1f);
                        raw[idx + 3] = (byte)Math.Round(a * fade);
                    }
                }
            }

            Marshal.Copy(raw, 0, data.Scan0, raw.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private static Color SelectLoadColor(float percent, Color normalColor) =>
        percent >= 90f ? ColorRed : percent >= 75f ? ColorAmber : normalColor;

    private static Color SelectThermalColor(float celsius) =>
        celsius >= 85f ? ColorRed : celsius >= 70f ? ColorAmber : ColorCyan;

    private static string FormatPercent(float value) =>
        $"{Math.Clamp(Math.Round(value), 0, 100):0}%";

    private static string FormatCpuSubline(SystemMetrics m)
    {
        string temp = m.CpuTemperature >= 0 ? $"{Math.Round(m.CpuTemperature):0}°C" : "TEMP --";
        string clock = FormatClock(m.CpuFrequencyGhz);
        return string.IsNullOrEmpty(clock) ? temp : $"{temp}  /  {clock}";
    }

    private static string FormatGpuSubline(SystemMetrics m)
    {
        string temp = m.GpuTemperature >= 0 ? $"{Math.Round(m.GpuTemperature):0}°C" : "TEMP --";
        string vram = m.GpuVramUsedBytes > 0 ? $"VRAM {FormatGiB(m.GpuVramUsedBytes)}" : "GPU";
        return $"{temp}  /  {vram}";
    }

    private static string FormatRamSubline(SystemMetrics m)
    {
        if (m.RamTotalBytes > 0)
            return $"{FormatGiB(m.RamUsedBytes)} / {FormatGiB(m.RamTotalBytes)}";
        return $"COMMIT {Math.Round(m.CommitPercent):0}%";
    }

    private static string FormatClock(float ghz) =>
        ghz > 0.1f ? $"{ghz:0.00} GHz" : string.Empty;

    private static string FormatGiB(ulong bytes) =>
        bytes == 0 ? "0.0 GB" : $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB";

    private static string NormalizeRate(string? formatted, float kbps)
    {
        if (!string.IsNullOrWhiteSpace(formatted))
            return formatted.Trim().ToUpperInvariant();
        return kbps >= 1024f ? $"{kbps / 1024f:0.0} MB/S" : $"{kbps:0} KB/S";
    }
}
