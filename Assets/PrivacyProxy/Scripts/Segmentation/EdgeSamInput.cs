using UnityEngine;

// Turns a photo into the EdgeSAM encoder input, as in PoC/vendor/EdgeSAM/edge_sam/onnx/predictor_onnx.py:
// longest side scaled to 1024 px, colors normalized by the dataset mean and std, black padding to 1024 × 1024.
// Kept apart from the model code so the contour code can reuse it. Pure C#: safe to call from a background thread.
public static class EdgeSamInput
{
    public const int Side = 1024;
    static readonly float[] Mean = { 123.675f, 116.28f, 103.53f };
    static readonly float[] Std = { 58.395f, 57.12f, 57.375f };

    // Longest-side factor from photo pixels to the 1024 scale.
    public static float Scale(int width, int height) => (float)Side / Mathf.Max(width, height);

    // Size of the photo inside the 1024 square, rounded like the PoC's ResizeLongestSide.
    // Box corners and the mask use nw/width and nh/height per axis, as the PoC does.
    public static Vector2Int ScaledSize(int width, int height)
    {
        float s = Scale(width, height);
        return new Vector2Int((int)(width * s + 0.5f), (int)(height * s + 0.5f));
    }

    // pixels: width*height, rows bottom-up like Texture2D.GetPixels32. into: 3*1024*1024, channels R, G, B, rows top-down.
    public static void Prepare(Color32[] pixels, int width, int height, float[] into)
    {
        System.Array.Clear(into, 0, into.Length); // zeros outside the scaled photo are the padding
        const int plane = Side * Side;
        float s = Scale(width, height);
        var scaled = ScaledSize(width, height);
        int nw = scaled.x, nh = scaled.y;
        for (int y = 0; y < nh; y++)
        for (int x = 0; x < nw; x++)
        {
            // Area average approximates PIL's antialiased downscaling used by the PoC.
            int x0 = (int)(x / s), x1 = Mathf.Min(Mathf.CeilToInt((x + 1) / s), width);
            int y0 = (int)(y / s), y1 = Mathf.Min(Mathf.CeilToInt((y + 1) / s), height);
            float r = 0, g = 0, b = 0;
            int n = 0;
            for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                var c = pixels[(height - 1 - yy) * width + xx];
                r += c.r;
                g += c.g;
                b += c.b;
                n++;
            }
            into[0 * plane + y * Side + x] = (r / n - Mean[0]) / Std[0];
            into[1 * plane + y * Side + x] = (g / n - Mean[1]) / Std[1];
            into[2 * plane + y * Side + x] = (b / n - Mean[2]) / Std[2];
        }
    }
}
