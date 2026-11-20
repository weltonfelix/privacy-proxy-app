using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Describes the selected object with Qwen3-VL running on the headset.
// Inference stays on the device; the crop never leaves this process.
public sealed class QwenDescriptionGenerator : IDescriptionGenerator, IDisposable
{
    // Only a broad category: no brands, text, codes or invented details.
    public const string Prompt = "Name the main object or assembly using only a broad, generic category. "
                        + "Return one short noun phrase. No explanation, brands, model names, codes, or function.";

    readonly QwenClient qwen = new QwenClient();

    public double LastSeconds { get; private set; }
    public double CheckSeconds { get; private set; } // file check in LoadAsync
    public double LoadSeconds { get; private set; } // model load in LoadAsync

    // Call once from the main thread when the screen opens. Returns null when ready, else a message for the screen.
    public async Task<string> LoadAsync(string dataPath, bool checkFiles)
    {
        string dir = Path.Combine(dataPath, "models");
        var files = ModelCheck.Parse(Resources.Load<TextAsset>("qwen_models").text).files; // [0] model, [1] mmproj
        if (checkFiles)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string error = ModelCheck.CheckSizes(dir, files);
            if (error != null) return error;
            string stamp = Path.Combine(dataPath, "models_verified.txt");
            var todo = ModelCheck.Unverified(dir, files, stamp);
            Debug.Log($"Model check: hashing {todo.Length} of {files.Length} files");
            if (todo.Length > 0) error = await Task.Run(() => ModelCheck.CheckHashes(dir, todo, stamp));
            CheckSeconds = timer.Elapsed.TotalSeconds;
            if (error != null) return error;
        }
        var result = await qwen.LoadAsync(Path.Combine(dir, files[0].name), Path.Combine(dir, files[1].name)).ConfigureAwait(false);
        LoadSeconds = result.Seconds;
        Debug.Log($"Qwen load: code {result.Code}, {result.Seconds:F3} s");
        return result.Code == QwenNative.Ok ? null : "Falha ao carregar: " + result.Error;
    }

    // Call from the main thread: QwenClient requires it for the describe and for its Cancel.
    public async Task<string> DescribeAsync(ObjectCrop.Result crop, CancellationToken cancellationToken)
    {
        if (crop == null) throw new ArgumentNullException(nameof(crop));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] rgb = ToRgb(crop);
        QwenClient.Result result;
        using (cancellationToken.Register(qwen.Cancel))
            result = await qwen.DescribeAsync(rgb, crop.Width, crop.Height, Prompt).ConfigureAwait(false);
        LastSeconds = result.Seconds;
        Debug.Log($"Qwen describe: code {result.Code}, {result.Seconds:F3} s, text \"{result.Text}\"");
        return TextOrThrow(result, cancellationToken);
    }

    public void Dispose() => qwen.Dispose();

    // Crop rows are bottom-up, as GetPixels32; the library wants them top-down.
    public static byte[] ToRgb(ObjectCrop.Result crop) => ToRgbTopDown(crop.Pixels, crop.Width, crop.Height);

    public static byte[] ToRgbTopDown(Color32[] px, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var c = px[(h - 1 - y) * w + x];
            int i = (y * w + x) * 3;
            rgb[i] = c.r;
            rgb[i + 1] = c.g;
            rgb[i + 2] = c.b;
        }
        return rgb;
    }

    public static string TextOrThrow(QwenClient.Result result, CancellationToken cancellationToken)
    {
        if (result.Code == QwenNative.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (result.Code != QwenNative.Ok) throw new InvalidOperationException(result.Error);
        if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("O Qwen devolveu um texto vazio.");
        return result.Text;
    }
}
