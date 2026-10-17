using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

/// <summary>
/// YuNet on the best GPU through DirectML, or the CPU when no usable GPU answers.
/// Frames are decoded into one reused buffer and never written anywhere.
/// </summary>
public sealed class FaceDetector : IFaceDetector
{
    private const int Size = YuNetDecoder.InputSize;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _input = new float[3 * Size * Size];
    private byte[] _pixels = [];

    private FaceDetector(InferenceSession session, string adapterName, bool onGpu)
    {
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
        AdapterName = adapterName;
        OnGpu = onGpu;
    }

    public string AdapterName { get; }
    public bool OnGpu { get; }

    public static FaceDetector Create(string modelPath, bool forceCpu) =>
        forceCpu ? new FaceDetector(OpenSession(modelPath, -1), "CPU", false) : Create(modelPath);

    public static FaceDetector Create(string modelPath, Func<int, InferenceSession>? sessionFactory = null)
    {
        sessionFactory ??= device => OpenSession(modelPath, device);
        IReadOnlyList<GpuAdapterInfo> adapters = [];
        long? preferred = null;
        try
        {
            adapters = GpuAdapters.EnumerateInDirectMLOrder();
            preferred = GpuAdapters.HighPerformanceLuid();
        }
        catch (Exception) { /* No DXGI (remote session, broken driver): the CPU still works. */ }

        int device = GpuAdapters.ChooseDevice(adapters, preferred);
        if (device >= 0)
        {
            try { return new FaceDetector(sessionFactory(device), adapters[device].Name, true); }
            catch (Exception) { /* DirectML refused this adapter; fall back below. */ }
        }
        return new FaceDetector(sessionFactory(-1), "CPU", false);
    }

    private static InferenceSession OpenSession(string modelPath, int device)
    {
        using var options = new SessionOptions();
        // The owner keeps working while this runs: on the CPU, two threads and no busy-wait spinning.
        options.IntraOpNumThreads = 2;
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        if (device >= 0)
        {
            // DirectML requires these two settings (ONNX Runtime DirectML provider notes).
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(device);
        }
        return new InferenceSession(modelPath, options);
    }

    public IReadOnlyList<DetectedFace> Detect(byte[] jpeg, out double milliseconds, out bool usable)
    {
        var watch = Stopwatch.StartNew();
        var (width, height) = LoadPixels(jpeg);
        usable = HasDetail(width, height);
        var tensor = new DenseTensor<float>(_input, [1, 3, Size, Size]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var outputs = new Dictionary<string, float[]>();
        foreach (var result in results)
            outputs[result.Name] = result.AsEnumerable<float>().ToArray();
        var faces = YuNetDecoder.Decode(outputs, width, height);
        milliseconds = watch.Elapsed.TotalMilliseconds;
        return faces;
    }

    private (int Width, int Height) LoadPixels(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        BitmapSource frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth > Size || frame.PixelHeight > Size)
        {
            double scale = Math.Min((double)Size / frame.PixelWidth, (double)Size / frame.PixelHeight);
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int width = bgr.PixelWidth, height = bgr.PixelHeight, stride = width * 3;
        if (_pixels.Length < stride * height) _pixels = new byte[stride * height];
        bgr.CopyPixels(_pixels, stride, 0);

        // YuNet takes BGR 0..255 planes; the frame fills the top-left, the rest stays zero.
        Array.Clear(_input);
        const int plane = Size * Size;
        for (int y = 0; y < height; y++)
        {
            int source = y * stride, target = y * Size;
            for (int x = 0; x < width; x++, source += 3)
            {
                _input[target + x] = _pixels[source];
                _input[plane + target + x] = _pixels[source + 1];
                _input[2 * plane + target + x] = _pixels[source + 2];
            }
        }
        return (width, height);
    }

    /// <summary>
    /// A covered lens or a dark room gives a black or featureless picture. Such a frame says
    /// nothing about who is there, so presence treats it as no information at all.
    /// </summary>
    private bool HasDetail(int width, int height)
    {
        const int Plane = Size * Size;
        double sum = 0, squares = 0;
        int count = 0;
        for (int y = 0; y < height; y += 4)
        {
            for (int x = 0; x < width; x += 4)
            {
                int index = y * Size + x;
                double luma = 0.114 * _input[index] + 0.587 * _input[Plane + index] + 0.299 * _input[2 * Plane + index];
                sum += luma;
                squares += luma * luma;
                count++;
            }
        }
        if (count == 0) return false;
        double mean = sum / count;
        double deviation = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
        return mean >= 18 && deviation >= 6;
    }

    public void Dispose() => _session.Dispose();
}
