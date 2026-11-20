using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// info.json of one capture. Field names are what the PoC notebooks read.
[Serializable]
public sealed class CaptureInfo
{
    public int capture;
    public string timestamp;
    public int width, height;
    public int[] box_xyxy; // the box given to EdgeSAM: origin top-left, far edge exclusive
    public float edgesam_score;
    public int mask_pixels;
    public int margin_px;
    public string description;
    public string choice; // "original" or "protegida"; empty until the user chooses
    public string question; // the question chosen on screen 1 for the external AI
    public string sent_image; // "protegida" or "original": what screen 5 sent to OpenAI (enviada.png)
    public string openai_model;
    public float openai_seconds;
    public string openai_error; // empty on success, else "<kind>: <message>"
}

// Saida/captura-<date and time>/ with foto.png, mascara.png, imagem-ia.png, enviada.png, resposta-ia.txt
// and info.json. Every file is written as soon as it exists. Local files only; this class sends nothing.
public sealed class CaptureFolder
{
    public readonly string Path;
    public readonly CaptureInfo Info;

    public CaptureFolder(string root, CaptureInfo info)
    {
        Info = info;
        Info.timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Path = System.IO.Path.Combine(root, "captura-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Try(() => Directory.CreateDirectory(Path));
    }

    public void SaveInfo() =>
        Try(() => File.WriteAllText(System.IO.Path.Combine(Path, "info.json"), JsonUtility.ToJson(Info, true)));

    public void SaveText(string name, string text) =>
        Try(() => File.WriteAllText(System.IO.Path.Combine(Path, name), text));

    // pixels: rows bottom-up, like GetPixels32. Encoded and written off the main thread;
    // the array must not change afterwards.
    public Task SavePng(string name, Color32[] pixels, int width, int height) => Task.Run(() => Try(() =>
        File.WriteAllBytes(System.IO.Path.Combine(Path, name),
            ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height))));

    // White = object, black = background.
    public static Color32[] MaskPixels(bool[] mask)
    {
        var pixels = new Color32[mask.Length];
        var white = new Color32(255, 255, 255, 255);
        var black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < mask.Length; i++) pixels[i] = mask[i] ? white : black;
        return pixels;
    }

    // A failed write must not stop the flow.
    static void Try(Action write)
    {
        try
        {
            write();
        }
        catch (Exception error)
        {
            Debug.LogError("Capture folder: " + error.Message);
        }
    }
}
