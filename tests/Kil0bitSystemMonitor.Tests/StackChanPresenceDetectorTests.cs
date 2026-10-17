using System;
using System.Collections.Generic;
using System.IO;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class StackChanPresenceDetectorTests
{
    private static readonly GpuAdapterInfo Amd = new("AMD Radeon(TM) 890M Graphics", 0x1111, false);
    private static readonly GpuAdapterInfo Nvidia = new("NVIDIA RTX PRO 1000 Blackwell Generation Laptop GPU", 0x2222, false);
    private static readonly GpuAdapterInfo Basic = new("Microsoft Basic Render Driver", 0x3333, true);

    [Fact]
    public void ChooseDevice_prefers_high_performance_luid() =>
        Assert.Equal(1, GpuAdapters.ChooseDevice([Amd, Nvidia], Nvidia.Luid));

    [Fact]
    public void ChooseDevice_skips_software_adapter_and_falls_back_to_first_hardware()
    {
        Assert.Equal(1, GpuAdapters.ChooseDevice([Basic, Amd], Basic.Luid));
        Assert.Equal(1, GpuAdapters.ChooseDevice([Basic, Amd], null));
    }

    [Fact]
    public void ChooseDevice_returns_cpu_when_no_hardware() =>
        Assert.Equal(-1, GpuAdapters.ChooseDevice([Basic], null));

    [Fact]
    public void Decode_single_anchor()
    {
        var outputs = EmptyOutputs();
        SetAnchor(outputs, stride: 8, row: 10, col: 20, score: 0.9f, logSize: MathF.Log(4));

        var face = Assert.Single(YuNetDecoder.Decode(outputs, 320, 240));

        Assert.Equal(20f * 8 / 320, face.CenterX, 3);
        Assert.Equal(10f * 8 / 240, face.CenterY, 3);
        Assert.Equal(32f / 320, face.Width, 3);
        Assert.Equal(32f / 240, face.Height, 3);
        Assert.Equal(0.9f, face.Score, 3);
    }

    [Fact]
    public void Decode_suppresses_overlapping_boxes()
    {
        var outputs = EmptyOutputs();
        SetAnchor(outputs, stride: 8, row: 10, col: 20, score: 0.9f, logSize: MathF.Log(8));
        SetAnchor(outputs, stride: 8, row: 10, col: 21, score: 0.8f, logSize: MathF.Log(8));

        Assert.Single(YuNetDecoder.Decode(outputs, 320, 240));
    }

    [Fact]
    public void Decode_drops_faces_outside_source_region()
    {
        var outputs = EmptyOutputs();
        SetAnchor(outputs, stride: 8, row: 10, col: 60, score: 0.9f, logSize: MathF.Log(4));

        Assert.Empty(YuNetDecoder.Decode(outputs, 320, 240));
    }

    [Fact]
    public void falls_back_to_cpu_when_directml_fails()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yunet-2023mar.onnx");
        using var detector = FaceDetector.Create(model, device => device >= 0
            ? throw new InvalidOperationException("DirectML unavailable")
            : new InferenceSession(model));

        Assert.False(detector.OnGpu);
        Assert.Equal("CPU", detector.AdapterName);
    }

    [Fact]
    public void Dark_or_flat_frames_are_not_usable_but_a_lit_scene_is()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yunet-2023mar.onnx");
        using var detector = FaceDetector.Create(model, forceCpu: true);

        detector.Detect(Jpeg((x, y) => 4), out _, out bool dark);
        detector.Detect(Jpeg((x, y) => 128), out _, out bool flat);
        detector.Detect(Jpeg((x, y) => (byte)(40 + (x * 7 + y * 13) % 180)), out _, out bool lit);

        Assert.False(dark);
        Assert.False(flat);
        Assert.True(lit);
    }

    private static byte[] Jpeg(Func<int, int, byte> luma)
    {
        var pixels = new byte[320 * 240 * 3];
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 320; x++)
                pixels[(y * 320 + x) * 3] = pixels[(y * 320 + x) * 3 + 1] = pixels[(y * 320 + x) * 3 + 2] = luma(x, y);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(320, 240, 96, 96,
            System.Windows.Media.PixelFormats.Bgr24, null, pixels, 960);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Dictionary<string, float[]> EmptyOutputs()
    {
        var outputs = new Dictionary<string, float[]>();
        foreach (int stride in new[] { 8, 16, 32 })
        {
            int cells = (640 / stride) * (640 / stride);
            outputs[$"cls_{stride}"] = new float[cells];
            outputs[$"obj_{stride}"] = new float[cells];
            outputs[$"bbox_{stride}"] = new float[cells * 4];
        }
        return outputs;
    }

    private static void SetAnchor(Dictionary<string, float[]> outputs, int stride, int row, int col, float score, float logSize)
    {
        int index = row * (640 / stride) + col;
        outputs[$"cls_{stride}"][index] = score;
        outputs[$"obj_{stride}"][index] = score;
        outputs[$"bbox_{stride}"][index * 4 + 2] = logSize;
        outputs[$"bbox_{stride}"][index * 4 + 3] = logSize;
    }
}
