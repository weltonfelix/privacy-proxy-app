using System;
using UnityEngine;

// Replace the object and a square safety margin before adding any text.
// Arrays use Texture2D.GetPixels32() order: bottom row first, true = object.
public static class AiImageComposer
{
    // Provisional margin selected from the PoC displacement study; not a safety guarantee.
    public const int DefaultMarginPixels = 16;
    public static readonly Color32 CoverColor = new Color32(192, 192, 192, 255);

    public static Color32[] CoverObject(Color32[] photo, bool[] mask, int width, int height,
        out RectInt objectBounds, int margin = DefaultMarginPixels)
    {
        if (photo == null) throw new ArgumentNullException(nameof(photo));
        ValidateMask(mask, width, height);
        if (photo.Length != mask.Length)
            throw new ArgumentException("Photo and mask must have the same dimensions.", nameof(photo));
        if (margin < 0) throw new ArgumentOutOfRangeException(nameof(margin));

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
        if (maxX < 0) throw new ArgumentException("Empty mask: nothing to protect.", nameof(mask));
        objectBounds = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);

        // Two sliding windows dilate in O(width * height), even with a large margin.
        // Clip each window at the image edge; pixels never wrap to another row.
        var horizontal = new bool[mask.Length];
        int rx = Math.Min(margin, width - 1), ry = Math.Min(margin, height - 1);
        for (int y = 0; y < height; y++)
        {
            int row = y * width, count = 0;
            for (int x = 0; x <= rx; x++)
                if (mask[row + x]) count++;
            for (int x = 0; x < width; x++)
            {
                horizontal[row + x] = count > 0;
                if (x >= rx && mask[row + x - rx]) count--;
                if (x < width - rx - 1 && mask[row + x + rx + 1]) count++;
            }
        }

        var result = (Color32[])photo.Clone();
        for (int x = 0; x < width; x++)
        {
            int count = 0;
            for (int y = 0; y <= ry; y++)
                if (horizontal[y * width + x]) count++;
            for (int y = 0; y < height; y++)
            {
                if (count > 0) result[y * width + x] = CoverColor;
                if (y >= ry && horizontal[(y - ry) * width + x]) count--;
                if (y < height - ry - 1 && horizontal[(y + ry + 1) * width + x]) count++;
            }
        }
        return result;
    }

    static void ValidateMask(bool[] mask, int width, int height)
    {
        if (mask == null) throw new ArgumentNullException(nameof(mask));
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid image dimensions.");
        if (mask.Length != width * height)
            throw new ArgumentException("Mask size must match width x height.", nameof(mask));
    }
}
