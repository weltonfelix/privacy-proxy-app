using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Sends the user's question and the protected image to the OpenAI Responses API.
// Call from Unity's main thread: it encodes the PNG and drives the UnityWebRequest there.
// Never logs; never puts the key or the base64 image in a result.
public static class OpenAiTaskClient
{
    public const string Url = "https://api.openai.com/v1/responses";
    public const string KeyFileName = "openai_key.txt";
    public const string KeyEnvVar = "OPENAI_API_KEY";

    public const string Instructions =
        "Responda à pergunta do usuário sobre a bancada. " +
        "Por privacidade, alguns objetos podem estar cobertos por um retângulo cinza, e o texto escrito em cima " +
        "do retângulo é uma descrição curta do objeto coberto. " +
        "Use essa descrição como a informação sobre aquele objeto (ele existe e está ali), sem tentar adivinhar " +
        "detalhes que não estão nela. " +
        "Se a pergunta depender de um detalhe coberto, diga que não dá para ver.";

    public enum ErrorKind { None, NoKey, NoNetwork, Timeout, ApiError }

    public readonly struct Result
    {
        public readonly string Text;
        public readonly double Seconds;
        public readonly ErrorKind Error;
        public readonly long HttpStatus; // 0 when no HTTP answer
        public readonly string ErrorMessage;

        public Result(string text, double seconds, ErrorKind error, long httpStatus, string errorMessage)
        {
            Text = text;
            Seconds = seconds;
            Error = error;
            HttpStatus = httpStatus;
            ErrorMessage = errorMessage;
        }
    }

    // Resources/openai_config.json; missing fields keep these defaults.
    [Serializable]
    public class Config
    {
        public string model = "gpt-5.4-mini";
        public string reasoningEffort = "low"; // reasoning tokens count toward maxOutputTokens; "" for models without it
        public float timeoutSeconds = 60;
        public int maxOutputTokens = 400;
    }

    public static Config LoadConfig()
    {
        var asset = Resources.Load<TextAsset>("openai_config");
        if (asset == null) return new Config();
        try { return JsonUtility.FromJson<Config>(asset.text) ?? new Config(); }
        catch (ArgumentException) { return new Config(); }
    }

    public static string KeyFilePath => Path.Combine(Application.persistentDataPath, KeyFileName);

    // Throws OperationCanceledException when the caller's token cancels (the request is aborted).
    public static async Task<Result> AskAsync(string question, Color32[] pixels, int width, int height,
        CancellationToken token, Config config = null)
    {
        config ??= LoadConfig();
        var clock = Stopwatch.StartNew();
#if UNITY_EDITOR
        string key = ResolveKey(Environment.GetEnvironmentVariable(KeyEnvVar), KeyFilePath);
#else
        string key = ResolveKey(null, KeyFilePath);
#endif
        if (key == null)
            return new Result("", 0, ErrorKind.NoKey, 0, $"Sem chave da OpenAI: copie {KeyFileName} para o óculos.");

        byte[] body = Encoding.UTF8.GetBytes(BuildRequestJson(config.model, Instructions, config.maxOutputTokens,
            question, EncodePng(pixels, width, height), config.reasoningEffort));

        using var request = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(body) { contentType = "application/json" },
            downloadHandler = new DownloadHandlerBuffer(),
        };
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + key);

        var operation = request.SendWebRequest();
        bool timedOut = false;
        // Polled on the main thread: Abort must not run from a timer or token callback thread.
        while (!operation.isDone)
        {
            if (token.IsCancellationRequested)
            {
                request.Abort();
                token.ThrowIfCancellationRequested();
            }
            if (clock.Elapsed.TotalSeconds >= config.timeoutSeconds)
            {
                timedOut = true;
                request.Abort();
                break;
            }
            await Task.Yield();
        }

        var result = Classify(timedOut, request.result, request.responseCode, request.downloadHandler?.text,
            request.error, clock.Elapsed.TotalSeconds);
        return Redact(result, key);
    }

    // Pure: env var wins when set; else the trimmed file content. Null when neither has a key.
    public static string ResolveKey(string envValue, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue.Trim();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        string fromFile = File.ReadAllText(filePath).Trim();
        return fromFile.Length > 0 ? fromFile : null;
    }

    // Main thread only. Pixels are Unity order (bottom row first), as GetPixels32 returns them.
    public static string EncodePng(Color32[] pixels, int width, int height)
    {
        if (pixels == null || width <= 0 || height <= 0 || pixels.Length != width * height)
            throw new ArgumentException($"pixels has {pixels?.Length ?? 0} entries, expected {width}x{height}");
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            texture.SetPixels32(pixels);
            texture.Apply();
            return Convert.ToBase64String(ImageConversion.EncodeToPNG(texture));
        }
        finally
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    // Pure. Built by hand: the two content parts have different fields, which JsonUtility cannot express.
    public static string BuildRequestJson(string model, string instructions, int maxOutputTokens,
        string question, string pngBase64, string reasoningEffort = null)
    {
        return "{\"model\":" + Quote(model) +
               (string.IsNullOrEmpty(reasoningEffort) ? "" : ",\"reasoning\":{\"effort\":" + Quote(reasoningEffort) + "}") +
               ",\"instructions\":" + Quote(instructions) +
               ",\"max_output_tokens\":" + maxOutputTokens +
               ",\"input\":[{\"role\":\"user\",\"content\":[" +
               "{\"type\":\"input_text\",\"text\":" + Quote(question) + "}," +
               "{\"type\":\"input_image\",\"image_url\":" + Quote("data:image/png;base64," + pngBase64) + "}]}]}";
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder((s?.Length ?? 0) + 2).Append('"');
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    [Serializable] class ContentPart { public string type; public string text; }
    [Serializable] class OutputItem { public string type; public ContentPart[] content; }
    [Serializable] class ApiErrorBody { public string message; }
    [Serializable] class ResponseBody { public string output_text; public OutputItem[] output; public ApiErrorBody error; }

    static ResponseBody Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonUtility.FromJson<ResponseBody>(json); }
        catch (ArgumentException) { return null; }
    }

    // Pure: the answer text from a Responses API body, or "" when it has none.
    public static string ReadText(string json)
    {
        var body = Parse(json);
        if (body == null) return "";
        if (!string.IsNullOrEmpty(body.output_text)) return body.output_text;
        var sb = new StringBuilder();
        foreach (var item in body.output ?? Array.Empty<OutputItem>())
        foreach (var part in item?.content ?? Array.Empty<ContentPart>())
            if (part?.type == "output_text") sb.Append(part.text);
        return sb.ToString();
    }

    // Pure: {"error":{"message":...}} message, or null.
    public static string ReadErrorMessage(string json)
    {
        string message = Parse(json)?.error?.message;
        return string.IsNullOrEmpty(message) ? null : message;
    }

    // Pure: maps how the request ended to a typed result.
    public static Result Classify(bool timedOut, UnityWebRequest.Result requestResult, long httpStatus,
        string responseBody, string requestError, double seconds)
    {
        if (timedOut)
            return new Result("", seconds, ErrorKind.Timeout, 0, "A OpenAI não respondeu a tempo.");
        if (requestResult == UnityWebRequest.Result.ConnectionError)
            return new Result("", seconds, ErrorKind.NoNetwork, 0, "Sem conexão com a OpenAI: " + requestError);
        if (requestResult != UnityWebRequest.Result.Success || httpStatus >= 400)
            return new Result("", seconds, ErrorKind.ApiError, httpStatus,
                ReadErrorMessage(responseBody) ?? $"HTTP {httpStatus} {requestError}".Trim());
        string text = ReadText(responseBody);
        if (text.Length == 0)
            return new Result("", seconds, ErrorKind.ApiError, httpStatus, "A resposta da OpenAI veio sem texto.");
        return new Result(text, seconds, ErrorKind.None, httpStatus, "");
    }

    // The API echoes a masked key on 401; drop any key-looking token anyway.
    static Result Redact(Result r, string key)
    {
        if (string.IsNullOrEmpty(r.ErrorMessage)) return r;
        string message = Regex.Replace(r.ErrorMessage.Replace(key, "***"), @"sk-[A-Za-z0-9_\-\*]+", "sk-***");
        return new Result(r.Text, r.Seconds, r.Error, r.HttpStatus, message);
    }
}
