using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

/// <summary>A located face, normalised 0..1 to the source frame. Never an identity.</summary>
public sealed record DetectedFace(float CenterX, float CenterY, float Width, float Height, float Score);

/// <summary>
/// Turns YuNet's 640x640 head outputs into faces. The source frame sits at the top-left
/// of the padded input, so anchors centred in the padding are not real faces.
/// </summary>
public static class YuNetDecoder
{
    public const int InputSize = 640;
    private static readonly int[] Strides = [8, 16, 32];

    public static IReadOnlyList<DetectedFace> Decode(IReadOnlyDictionary<string, float[]> outputs,
        int sourceWidth, int sourceHeight, float threshold = 0.6f, float nmsIou = 0.3f)
    {
        var candidates = new List<Box>();
        foreach (int stride in Strides)
        {
            if (!outputs.TryGetValue($"cls_{stride}", out var cls) ||
                !outputs.TryGetValue($"obj_{stride}", out var obj) ||
                !outputs.TryGetValue($"bbox_{stride}", out var bbox))
                continue;
            int columns = InputSize / stride;
            int cells = Math.Min(columns * columns, Math.Min(cls.Length, Math.Min(obj.Length, bbox.Length / 4)));
            for (int index = 0; index < cells; index++)
            {
                float score = MathF.Sqrt(Math.Clamp(cls[index], 0f, 1f) * Math.Clamp(obj[index], 0f, 1f));
                if (score < threshold) continue;
                int row = index / columns, column = index % columns;
                float centerX = (column + bbox[index * 4]) * stride;
                float centerY = (row + bbox[index * 4 + 1]) * stride;
                if (centerX >= sourceWidth || centerY >= sourceHeight) continue;
                float width = MathF.Exp(bbox[index * 4 + 2]) * stride;
                float height = MathF.Exp(bbox[index * 4 + 3]) * stride;
                candidates.Add(new Box(centerX - width / 2, centerY - height / 2,
                    centerX + width / 2, centerY + height / 2, score));
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<Box>();
        foreach (var candidate in candidates)
        {
            bool overlaps = false;
            foreach (var other in kept)
            {
                if (Iou(candidate, other) > nmsIou) { overlaps = true; break; }
            }
            if (!overlaps) kept.Add(candidate);
        }

        var faces = new List<DetectedFace>(kept.Count);
        foreach (var box in kept)
        {
            float centerX = (box.X1 + box.X2) / 2, centerY = (box.Y1 + box.Y2) / 2;
            faces.Add(new DetectedFace(centerX / sourceWidth, centerY / sourceHeight,
                (box.X2 - box.X1) / sourceWidth, (box.Y2 - box.Y1) / sourceHeight, box.Score));
        }
        return faces;
    }

    private static float Iou(Box a, Box b)
    {
        float width = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float height = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float intersection = width * height;
        float union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private readonly record struct Box(float X1, float Y1, float X2, float Y2, float Score);
}
