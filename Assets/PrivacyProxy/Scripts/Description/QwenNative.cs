using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// The four calls of Native/include/pp_qwen.h. On the Quest they go to libpp_qwen.so;
// in the Editor a fake Qwen waits 15 s and answers "Electronic circuit board".
// Blocking calls: run them off the main thread (see QwenClient).
public static class QwenNative
{
    public const int Ok = 0, Error = 1, Cancelled = 2;

#if UNITY_ANDROID && !UNITY_EDITOR
    const string Lib = "pp_qwen";

    [DllImport(Lib)] static extern int pp_qwen_load([MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string mmprojPath);
    [DllImport(Lib)] static extern int pp_qwen_describe(byte[] rgb, int width, int height,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
                                                        byte[] outText, int outTextSize, out double outSeconds);
    [DllImport(Lib)] static extern int pp_qwen_cancel();
    [DllImport(Lib)] static extern int pp_qwen_free();
    [DllImport(Lib)] static extern IntPtr pp_qwen_last_error();

    public static int Load(string modelPath, string mmprojPath) => pp_qwen_load(modelPath, mmprojPath);
    public static int Cancel() => pp_qwen_cancel();
    public static int Free() => pp_qwen_free();
    public static string LastError() => Marshal.PtrToStringUTF8(pp_qwen_last_error());

    public static int Describe(byte[] rgb, int width, int height, string prompt, out string text, out double seconds)
    {
        var buffer = new byte[4096];
        int code = pp_qwen_describe(rgb, width, height, prompt, buffer, buffer.Length, out seconds);
        int len = Array.IndexOf(buffer, (byte)0);
        text = Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
        return code;
    }
#else
    static volatile bool s_cancel, s_loaded;
    static string s_error = "";

    public static int Load(string modelPath, string mmprojPath)
    {
        s_loaded = true;
        s_error = "";
        return Ok;
    }

    public static int Cancel()
    {
        s_cancel = true;
        return Ok;
    }

    public static int Free()
    {
        s_loaded = false;
        return Ok;
    }

    public static string LastError() => s_error;

    public static int Describe(byte[] rgb, int width, int height, string prompt, out string text, out double seconds)
    {
        var start = DateTime.UtcNow;
        s_cancel = false;
        text = "";
        seconds = 0;
        if (!s_loaded)
        {
            s_error = "model not loaded; call pp_qwen_load first";
            return Error;
        }
        while ((DateTime.UtcNow - start).TotalSeconds < 15 && !s_cancel)
            Thread.Sleep(50);
        seconds = (DateTime.UtcNow - start).TotalSeconds;
        if (!s_loaded)
        {
            s_error = "model freed during describe"; // on the Quest this would crash the app
            return Error;
        }
        if (s_cancel)
            return Cancelled;
        text = "Electronic circuit board";
        return Ok;
    }
#endif
}
