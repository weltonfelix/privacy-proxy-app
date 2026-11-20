using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;

// No network: only the pure parts of OpenAiTaskClient. Window > General > Test Runner > EditMode.
public class OpenAiTaskClientTests
{
    const string DataPrefix = "data:image/png;base64,";

    [Serializable] class Part { public string type; public string text; public string image_url; }
    [Serializable] class Message { public string role; public Part[] content; }
    [Serializable] class Request { public string model; public string instructions; public int max_output_tokens; public Message[] input; }

    [Test]
    public void RequestHasModelQuestionAndPngDataUrl()
    {
        var pixels = new Color32[4 * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(10, 20, 30, 255);
        string question = "Qual \"cabo\" uso?\nLinha 2 \\ fim";
        string json = OpenAiTaskClient.BuildRequestJson("gpt-test", OpenAiTaskClient.Instructions, 123,
            question, OpenAiTaskClient.EncodePng(pixels, 4, 3));

        var request = JsonUtility.FromJson<Request>(json); // also proves the escaping is valid JSON
        Assert.AreEqual("gpt-test", request.model);
        Assert.AreEqual(OpenAiTaskClient.Instructions, request.instructions);
        Assert.AreEqual(123, request.max_output_tokens);
        Assert.AreEqual("user", request.input[0].role);
        Assert.AreEqual("input_text", request.input[0].content[0].type);
        Assert.AreEqual(question, request.input[0].content[0].text);
        Assert.AreEqual("input_image", request.input[0].content[1].type);

        string url = request.input[0].content[1].image_url;
        StringAssert.StartsWith(DataPrefix, url);
        byte[] png = Convert.FromBase64String(url.Substring(DataPrefix.Length));
        CollectionAssert.AreEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' },
            new[] { png[0], png[1], png[2], png[3] });
        // each part carries only its own field
        StringAssert.DoesNotContain("\"image_url\":\"\"", json);
        StringAssert.DoesNotContain("reasoning", json);
        StringAssert.Contains("\"reasoning\":{\"effort\":\"low\"}",
            OpenAiTaskClient.BuildRequestJson("gpt-test", "i", 1, "q", "AAAA", "low"));
        Assert.AreEqual("gpt-5.4-mini", OpenAiTaskClient.LoadConfig().model);
    }

    [Test]
    public void QuoteEscapesControlCharacters()
    {
        Assert.AreEqual("\"a\\u0001b\\t\"", OpenAiTaskClient.Quote("a\u0001b\t"));
    }

    [Test]
    public void ReadsTextFromOutputItems()
    {
        const string body = "{\"id\":\"resp_1\",\"output\":[" +
            "{\"type\":\"reasoning\",\"content\":[]}," +
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[" +
            "{\"type\":\"output_text\",\"text\":\"Use o cabo \",\"annotations\":[]}," +
            "{\"type\":\"output_text\",\"text\":\"vermelho.\",\"annotations\":[]}]}],\"error\":null}";
        var r = OpenAiTaskClient.Classify(false, UnityWebRequest.Result.Success, 200, body, null, 1.5);
        Assert.AreEqual(OpenAiTaskClient.ErrorKind.None, r.Error);
        Assert.AreEqual("Use o cabo vermelho.", r.Text);
        Assert.AreEqual(1.5, r.Seconds);
    }

    [Test]
    public void PrefersTopLevelOutputText()
    {
        Assert.AreEqual("ok", OpenAiTaskClient.ReadText("{\"output_text\":\"ok\",\"output\":[]}"));
    }

    [TestCase(401, "Incorrect API key provided.")]
    [TestCase(429, "Rate limit reached.")]
    [TestCase(500, "The server had an error.")]
    public void HttpErrorsBecomeApiErrorWithStatusAndMessage(int status, string message)
    {
        string body = "{\"error\":{\"message\":\"" + message + "\",\"type\":\"x\",\"code\":null}}";
        var r = OpenAiTaskClient.Classify(false, UnityWebRequest.Result.ProtocolError, status, body,
            "HTTP/1.1 " + status, 0.2);
        Assert.AreEqual(OpenAiTaskClient.ErrorKind.ApiError, r.Error);
        Assert.AreEqual(status, r.HttpStatus);
        Assert.AreEqual(message, r.ErrorMessage);
    }

    [Test]
    public void TimeoutBecomesTimeout()
    {
        var r = OpenAiTaskClient.Classify(true, UnityWebRequest.Result.ConnectionError, 0, null, "Request aborted", 60);
        Assert.AreEqual(OpenAiTaskClient.ErrorKind.Timeout, r.Error);
    }

    [Test]
    public void ConnectionErrorBecomesNoNetwork()
    {
        var r = OpenAiTaskClient.Classify(false, UnityWebRequest.Result.ConnectionError, 0, null,
            "Cannot resolve destination host", 0.1);
        Assert.AreEqual(OpenAiTaskClient.ErrorKind.NoNetwork, r.Error);
    }

    [Test]
    public void MissingKeyGivesNoKey()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        Assert.IsNull(OpenAiTaskClient.ResolveKey(null, missing));
        Assert.IsNull(OpenAiTaskClient.ResolveKey("  ", missing));
    }

    [Test]
    public void KeyComesFromEnvThenTrimmedFile()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(file, "  sk-file \n");
            Assert.AreEqual("sk-env", OpenAiTaskClient.ResolveKey(" sk-env ", file));
            Assert.AreEqual("sk-file", OpenAiTaskClient.ResolveKey(null, file));
            File.WriteAllText(file, "\n");
            Assert.IsNull(OpenAiTaskClient.ResolveKey(null, file));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void AskWithoutKeyReturnsNoKeyWithoutNetwork()
    {
        // Skips when this machine has a key: the call would then go to the network.
        if (OpenAiTaskClient.ResolveKey(Environment.GetEnvironmentVariable(OpenAiTaskClient.KeyEnvVar),
                OpenAiTaskClient.KeyFilePath) != null)
            Assert.Ignore("A key is configured here; not calling the API.");
        var r = OpenAiTaskClient.AskAsync("x", new Color32[1], 1, 1, default).Result;
        Assert.AreEqual(OpenAiTaskClient.ErrorKind.NoKey, r.Error);
    }
}
