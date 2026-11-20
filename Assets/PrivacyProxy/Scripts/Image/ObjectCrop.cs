using System;
using UnityEngine;

// PoC isolated_crop contract. Arrays use GetPixels32 order (bottom row first).
public static class ObjectCrop
{
    public sealed class Result
    {
        public readonly Color32[] Pixels;
        public readonly RectInt Bounds;
        public int Width => Bounds.width;
        public int Height => Bounds.height;

        internal Result(Color32[] pixels, RectInt bounds)
        {
            Pixels = pixels;
            Bounds = bounds;
        }
    }

    public static Result Create(Color32[] photo, bool[] mask, int width, int height)
    {
        if (photo == null) throw new ArgumentNullException(nameof(photo));
        if (mask == null) throw new ArgumentNullException(nameof(mask));
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid image dimensions.");
        if (photo.Length != width * height || mask.Length != photo.Length)
            throw new ArgumentException("Photo and boolean mask must both match width x height.");

        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!mask[y * width + x]) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) throw new ArgumentException("Empty mask: adjust the selection.", nameof(mask));

        int cropWidth = maxX - minX + 1, cropHeight = maxY - minY + 1;
        var pixels = new Color32[cropWidth * cropHeight];
        var gray = new Color32(127, 127, 127, 255);
        for (int y = 0; y < cropHeight; y++)
        for (int x = 0; x < cropWidth; x++)
        {
            int source = (y + minY) * width + x + minX;
            var pixel = mask[source] ? photo[source] : gray;
            pixel.a = 255; // The PoC output is RGB, never a transparent crop.
            pixels[y * cropWidth + x] = pixel;
        }
        return new Result(pixels, new RectInt(minX, minY, cropWidth, cropHeight));
    }
}
