using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class PrivacyProxyFlowScreenTests
{
    GameObject root;
    PrivacyProxyFlowScreen flow;

    // EditMode does not run Start, and Application.isEditor selects the example photo and mask.
    [SetUp]
    public void SetUp()
    {
        root = new GameObject("Flow test", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        flow = root.AddComponent<PrivacyProxyFlowScreen>();
        var serialized = new SerializedObject(flow);
        serialized.FindProperty("canvas").objectReferenceValue = root.GetComponent<Canvas>();
        serialized.FindProperty("photo").objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/source.png");
        serialized.FindProperty("maskImage").objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/lower_assembly.png");
        serialized.ApplyModifiedPropertiesWithoutUndo();
        Invoke("Start");
    }

    [TearDown]
    public void TearDown()
    {
        Invoke("OnDestroy");
        Object.DestroyImmediate(root);
    }

    void Invoke(string method) => typeof(PrivacyProxyFlowScreen)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, null);

    static EdgeSamSegmenter.Result ExampleMask()
    {
        var png = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/lower_assembly.png");
        return new EdgeSamSegmenter.Result { Mask = MaskContour.MaskFromPng(png), Width = png.width, Height = png.height };
    }

    [Test]
    public void SelectionBoxAddsOneToTheFarEdgeAndKeepsTopLeftOrigin()
    {
        // Clicked pixels are inclusive and y already counts from the top, as EdgeSAM expects.
        var box = PrivacyProxyFlowScreen.SelectionBox(new Vector2Int(40, 30), new Vector2Int(10, 5));
        Assert.AreEqual(Rect.MinMaxRect(10, 5, 41, 31), box);
        Assert.AreEqual(box, PrivacyProxyFlowScreen.SelectionBox(new Vector2Int(10, 5), new Vector2Int(40, 30)));
    }

    [Test]
    public void SelectionBoxCanReachThePhotoEdge()
    {
        // Last pixel of a 1280 x 960 photo: the box ends exactly at the photo size, which EdgeSAM accepts.
        var box = PrivacyProxyFlowScreen.SelectionBox(new Vector2Int(0, 0), new Vector2Int(1279, 959));
        Assert.AreEqual(Rect.MinMaxRect(0, 0, 1280, 960), box);
    }

    [Test]
    public void ResultOfAnOlderPhotoIsDropped()
    {
        Invoke("Capture");
        int oldPhoto = flow.PhotoId;
        Invoke("StartOver"); // "Nova foto" while the segmentation was still running
        Assert.IsFalse(flow.ApplySegmentation(oldPhoto, ExampleMask()));
        Assert.IsFalse(flow.HasMask);

        Invoke("Capture");
        Assert.IsFalse(flow.ApplySegmentation(oldPhoto, ExampleMask()));
        Assert.IsFalse(flow.HasMask);
        Assert.IsTrue(flow.ApplySegmentation(flow.PhotoId, ExampleMask()));
        Assert.IsTrue(flow.HasMask);
    }

    [Test]
    public void NewPhotoClearsTheMask()
    {
        Invoke("Capture");
        Assert.IsTrue(flow.ApplySegmentation(flow.PhotoId, ExampleMask()));
        Invoke("StartOver");
        Assert.IsFalse(flow.HasMask);
    }

    Button CaptureButton()
    {
        foreach (var button in root.GetComponentsInChildren<Button>(true))
            if (button.name == "Capture") return button;
        return null;
    }

    [Test]
    public void DefaultQuestionIsTheFirstOfTheList()
    {
        var file = Resources.Load<TextAsset>("perguntas");
        Assert.IsNotNull(file, "Resources/perguntas.json is missing.");
        var questions = PrivacyProxyFlowScreen.ParseQuestions(file.text);
        Assert.AreEqual("Sugira uma organização que deixe as ferramentas usadas nesta etapa próximas à área de montagem",
            questions[0]);
        Assert.AreEqual(questions[0], flow.Question);
        Assert.AreEqual(questions.Length + 1, flow.QuestionOptionCount); // + "Digitar minha pergunta"
        Assert.IsTrue(flow.CanCapture);
    }

    [Test]
    public void MissingOrBrokenQuestionFileUsesTheBuiltInList()
    {
        var fallback = PrivacyProxyFlowScreen.ParseQuestions(null);
        Assert.AreEqual(6, fallback.Length);
        Assert.AreEqual(fallback, PrivacyProxyFlowScreen.ParseQuestions("{\"perguntas\":[]}"));
    }

    [Test]
    public void TypedQuestionIsUsedAndSurvivesNewPhoto()
    {
        flow.SelectCustomQuestion("  Que peça é esta?  ");
        Assert.AreEqual("Que peça é esta?", flow.Question);
        Invoke("Capture");
        Assert.AreEqual("Que peça é esta?", flow.NewCaptureInfo(1280, 960).question);
        Invoke("StartOver"); // "Nova foto"
        Assert.AreEqual("Que peça é esta?", flow.Question);
    }

    [Test]
    public void EmptyQuestionBlocksTheCapture()
    {
        flow.SelectCustomQuestion("   ");
        Assert.IsFalse(flow.CanCapture);
        Assert.IsFalse(CaptureButton().interactable);
        int before = flow.PhotoId;
        Invoke("Capture");
        Assert.AreEqual(before, flow.PhotoId); // no photo was taken

        flow.SelectQuestion(0);
        Assert.IsTrue(CaptureButton().interactable);
    }

    [Test]
    public void QuestionGoesToInfoJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "privacy-proxy-question-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            flow.SelectQuestion(3);
            var folder = new CaptureFolder(root, flow.NewCaptureInfo(1280, 960));
            folder.SaveInfo();
            var read = JsonUtility.FromJson<CaptureInfo>(File.ReadAllText(Path.Combine(folder.Path, "info.json")));
            Assert.AreEqual("O que é este objeto e para que serve?", read.question);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Records what screen 5 would send; the answer arrives only when the test calls ApplyAiResult.
    sealed class FakeAi
    {
        public int Calls;
        public string Question;
        public Color32[] Pixels;
        public CancellationToken Token;

        public Task<OpenAiTaskClient.Result> Ask(string question, Color32[] pixels, int width, int height,
            CancellationToken token)
        {
            Calls++;
            Question = question;
            Pixels = pixels;
            Token = token;
            return new TaskCompletionSource<OpenAiTaskClient.Result>().Task; // never completes by itself
        }
    }

    FakeAi ReachReview()
    {
        var fake = new FakeAi();
        flow.AskAi = fake.Ask;
        Invoke("Capture");
        Assert.IsTrue(flow.ApplySegmentation(flow.PhotoId, ExampleMask()));
        typeof(PrivacyProxyFlowScreen).GetField("candidate", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(flow, "Electronic circuit board");
        Invoke("EnterReview");
        return fake;
    }

    Color32[] TexturePixels(string field) => ((Texture2D)typeof(PrivacyProxyFlowScreen)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flow)).GetPixels32();

    static bool SamePixels(Color32[] a, Color32[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) return false;
        return true;
    }

    static OpenAiTaskClient.Result Answer(string text) =>
        new OpenAiTaskClient.Result(text, 2.5, OpenAiTaskClient.ErrorKind.None, 200, "");

    [Test]
    public void ProtectedImageIsSentWithTheQuestion()
    {
        var fake = ReachReview();
        Invoke("EnterResponse"); // no choice yet: Continuar is hidden and nothing is sent
        Assert.AreEqual(0, fake.Calls);

        Invoke("KeepProtected");
        Invoke("EnterResponse");
        Assert.AreEqual(1, fake.Calls);
        Assert.AreEqual(flow.Question, fake.Question);
        Assert.IsTrue(SamePixels(TexturePixels("protectedTexture"), fake.Pixels));
        Assert.IsFalse(SamePixels(TexturePixels("shot"), fake.Pixels));
    }

    [Test]
    public void OriginalIsSentOnlyWhenAllowed()
    {
        var fake = ReachReview();
        Invoke("AllowOriginal");
        Invoke("EnterResponse");
        Assert.AreEqual(1, fake.Calls);
        Assert.IsTrue(SamePixels(TexturePixels("shot"), fake.Pixels));
    }

    [Test]
    public void LateAnswerAfterNewPhotoIsIgnored()
    {
        var fake = ReachReview();
        Invoke("KeepProtected");
        Invoke("EnterResponse");
        int oldPhoto = flow.PhotoId;
        Invoke("StartOver"); // "Nova foto" while the call is running
        Assert.IsTrue(fake.Token.IsCancellationRequested);
        Assert.IsFalse(flow.ApplyAiResult(oldPhoto, Answer("resposta atrasada")));
        Assert.AreEqual("", flow.ResponseText);
    }

    [Test]
    public void AnswerIsShownOnScreenFive()
    {
        ReachReview();
        Invoke("KeepProtected");
        Invoke("EnterResponse");
        Assert.IsTrue(flow.ApplyAiResult(flow.PhotoId, Answer("A bancada tem uma placa.")));
        Assert.AreEqual("A bancada tem uma placa.", flow.ResponseText);
    }

    [Test]
    public void ErrorShowsThePanelAndMissingKeyInTheEditorShowsTheExample()
    {
        var panel = root.GetComponent<PrivacyProxyErrorPanel>();
        typeof(PrivacyProxyErrorPanel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(panel, null); // EditMode does not run Awake
        ReachReview();
        Invoke("KeepProtected");
        Invoke("EnterResponse");

        flow.ApplyAiResult(flow.PhotoId, new OpenAiTaskClient.Result("", 0, OpenAiTaskClient.ErrorKind.NoKey, 0, "sem chave"));
        Assert.IsFalse(panel.IsVisible);
        StringAssert.StartsWith("Resposta de exemplo", flow.ResponseText);

        flow.ApplyAiResult(flow.PhotoId,
            new OpenAiTaskClient.Result("", 1, OpenAiTaskClient.ErrorKind.NoNetwork, 0, "sem rede"));
        Assert.IsTrue(panel.IsVisible);
        Assert.AreEqual("", flow.ResponseText);
    }

    [Test]
    public void InfoJsonKeepsEveryField()
    {
        string root = Path.Combine(Path.GetTempPath(), "privacy-proxy-capture-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var folder = new CaptureFolder(root, new CaptureInfo { capture = 3, width = 1280, height = 960, margin_px = 16 });
            folder.Info.box_xyxy = new[] { 10, 5, 41, 31 };
            folder.Info.edgesam_score = 0.96f;
            folder.Info.mask_pixels = 1234;
            folder.Info.description = "Electronic circuit board";
            folder.Info.choice = "protegida";
            folder.Info.sent_image = "protegida";
            folder.Info.openai_model = "gpt-test";
            folder.Info.openai_seconds = 2.5f;
            folder.Info.openai_error = "";
            folder.SaveInfo();
            folder.SaveText("resposta-ia.txt", "Uma placa.");
            folder.SavePng("mascara.png", CaptureFolder.MaskPixels(new[] { true, false, false, true }), 2, 2).Wait();

            StringAssert.StartsWith(Path.Combine(root, "captura-"), folder.Path);
            string json = File.ReadAllText(Path.Combine(folder.Path, "info.json"));
            StringAssert.Contains("\"box_xyxy\"", json);
            var read = JsonUtility.FromJson<CaptureInfo>(json);
            Assert.AreEqual(3, read.capture);
            Assert.AreEqual(new[] { 10, 5, 41, 31 }, read.box_xyxy);
            Assert.AreEqual(0.96f, read.edgesam_score);
            Assert.AreEqual(1234, read.mask_pixels);
            Assert.AreEqual(16, read.margin_px);
            Assert.AreEqual("Electronic circuit board", read.description);
            Assert.AreEqual("protegida", read.choice);
            Assert.IsNotEmpty(read.timestamp);
            Assert.AreEqual("protegida", read.sent_image);
            Assert.AreEqual("gpt-test", read.openai_model);
            Assert.AreEqual(2.5f, read.openai_seconds);
            Assert.AreEqual("", read.openai_error);
            Assert.AreEqual("Uma placa.", File.ReadAllText(Path.Combine(folder.Path, "resposta-ia.txt")));

            var mask = new Texture2D(2, 2);
            mask.LoadImage(File.ReadAllBytes(Path.Combine(folder.Path, "mascara.png")));
            Assert.AreEqual(255, mask.GetPixels32()[0].r); // white = object, rows bottom-up
            Assert.AreEqual(0, mask.GetPixels32()[1].r);
            Object.DestroyImmediate(mask);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
