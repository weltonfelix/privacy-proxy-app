using System;
using UnityEngine;

// Draws the outline of a boolean mask as a transparent image, to put over the photo.
// Mask format (same as EdgeSamSegmenter returns): bool[width * height], true = object,
// rows in the order of Texture2D.GetPixels32() (bottom row first). Nothing is flipped here.
public static class MaskContour
{
    // PoC mask PNG (white = object) -> same format the EdgeSAM returns.
    // Swapping to the real EdgeSAM later means replacing only the call to this function.
    public static bool[] MaskFromPng(Texture2D png)
    {
        if (png == null) throw new ArgumentNullException(nameof(png));
        if (!png.isReadable) throw new ArgumentException("Enable Read/Write on the mask texture.");
        var pixels = png.GetPixels32();
        var mask = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            mask[i] = pixels[i].r > 127;
        return mask;
    }

    // Red outline, transparent everywhere else, same size as the photo.
    // An object pixel is on the edge when one of its 4 neighbours is background.
    // Outside the image counts as background, so an object cut by the image border
    // also gets an outline there.
    public static Texture2D DrawOutline(bool[] mask, int width, int height, int thickness = 3)
    {
        if (mask == null) throw new ArgumentNullException(nameof(mask));
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid image dimensions.");
        if (thickness < 1 || thickness > 15 || thickness % 2 == 0)
            throw new ArgumentOutOfRangeException(nameof(thickness), "Use an odd thickness from 1 to 15.");
        if (mask.Length != width * height)
            throw new ArgumentException("mask size does not match width x height");

        var red = new Color32(255, 0, 0, 255);
        var pixels = new Color32[width * height]; // all (0,0,0,0) = transparent
        int radius = Mathf.Max(0, thickness / 2);  // thickness 3 -> radius 1
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!mask[y * width + x] || !IsEdge(mask, width, height, x, y))
                continue;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                    pixels[ny * width + nx] = red;
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "Generated mask contour";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Point;
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    static bool IsEdge(bool[] mask, int w, int h, int x, int y)
    {
        return x == 0 || !mask[y * w + x - 1]
            || x == w - 1 || !mask[y * w + x + 1]
            || y == 0 || !mask[(y - 1) * w + x]
            || y == h - 1 || !mask[(y + 1) * w + x];
    }
}
