using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Five-screen flow. On the Quest: headset camera, EdgeSAM and Qwen. In the Editor: the example
// photo and mask with the simulated description, so the scene still works without a headset.
public sealed class PrivacyProxyFlowScreen : MonoBehaviour
{
    [SerializeField] Canvas canvas;
    [SerializeField] Texture2D photo, maskImage; // Editor example only, never written to
    [SerializeField] PassthroughCameraAccess cameraAccess;
    [SerializeField] ModelAsset encoderAsset, decoderAsset;
    [SerializeField] int margin = AiImageComposer.DefaultMarginPixels;
    string responseText;
    PrivacyProxyErrorPanel errorPanel;
    PrivacyProxyErrorSimulator errorSimulator;

    const int MinSelectionSide = 10;
    const float SelectionLineWidth = 6f;
    readonly List<GameObject> pages = new List<GameObject>();
    readonly List<AspectRatioFitter> photoFitters = new List<AspectRatioFitter>();
    Texture2D shot; // the frozen photo: our own texture, shown by every screen after the capture
    Color32[] shotPixels;
    Texture2D outlineTexture, cropTexture, protectedTexture;
    EdgeSamSegmenter sam;
    QwenDescriptionGenerator qwen; // null in the Editor, where the description is simulated
    bool modelLoading;
    string modelError;
    int cpuHolds;
    OVRManager.ProcessorPerformanceLevel cpuLevel;
    bool real, freezing, segmenting;
    readonly List<RawImage> contourViews = new List<RawImage>();
    RawImage cropView;
    int captureNumber; // photos taken in this run, for the logs
    CaptureFolder capture; // null in EditMode tests
    int photoId; // changes on every new photo and on "Nova foto", so late results can be told apart
    RawImage livePreview;
    Text captureStatus;
    bool[] mask;
    ObjectCrop.Result crop;
    DescriptionSession descriptionSession;
    string candidate;
    string localSelectionStatus;
    int pageIndex = -1;
    double screenEnteredAt;
    bool hasFirstCorner, hasSelection, allowsOriginal, hasReviewChoice;
    Vector2Int firstCorner, secondCorner;
    RectTransform selectionPhotoRect;
    RawImage selectionPhoto, selectionFill;
    RectTransform[] selectionEdges;
    RectTransform[] selectionCornerMarkers;
    Button cancelSelectionButton, confirmSelectionButton, generateButton, continueDescriptionButton, continueResponseButton;
    Text generateButtonLabel;
    RectTransform generationSpinner;
    Text selectionStatus, descriptionStatus, candidateLabel, choiceLabel, reviewStatus;
    Button infoButton, responseInfoButton;
    GameObject descriptionCard, responseInfoCard;
    RawImage sharePreview;

    // Question sent with the photo. Presets come from Resources/perguntas.json; the last option is typed.
    static readonly string[] DefaultQuestions =
    {
        "Sugira uma organização que deixe as ferramentas usadas nesta etapa próximas à área de montagem",
        "Identifique itens fora de conformidade ou fora do lugar nesta bancada",
        "Quais ferramentas estão faltando para a montagem atual?",
        "O que é este objeto e para que serve?",
        "Descreva a bancada e o que está sobre ela",
        "Há algo perigoso ou fora do lugar nesta bancada?"
    };
    const string TypeQuestionOption = "Digitar minha pergunta";
    const string EmptyQuestionWarning = "Digite uma pergunta para poder capturar.";
    string[] questions = DefaultQuestions;
    int questionIndex; // questions.Length = the typed question
    string customQuestion = "";
    Text questionLabel;
    InputField questionInput; // Editor only; the Quest uses the system keyboard
    TouchScreenKeyboard keyboard;
    Button captureButton;
    readonly List<Text> questionHeaders = new List<Text>();

    // Screen 5. Tests swap AskAi for a fake, so they never reach the network.
    public Func<string, Color32[], int, int, CancellationToken, Task<OpenAiTaskClient.Result>> AskAi;
    OpenAiTaskClient.Config aiConfig;
    CancellationTokenSource aiCancel;
    RawImage responsePhoto;
    Text responseSubtitle, sentVersionLabel, responseLabel, responseStatus;
    RectTransform responseContent, sendingSpinner;

    [Serializable]
    sealed class QuestionList
    {
        public string[] perguntas;
    }

    void Start()
    {
        try
        {
            ValidateInputs();
            real = !Application.isEditor;
            aiConfig = OpenAiTaskClient.LoadConfig();
            AskAi ??= (question, pixels, width, height, token) =>
                OpenAiTaskClient.AskAsync(question, pixels, width, height, token, aiConfig);
            shot = new Texture2D(real ? 1280 : photo.width, real ? 960 : photo.height, TextureFormat.RGBA32, false);
            shot.name = "Frozen photo";
            shot.wrapMode = TextureWrapMode.Clamp;
            if (!real)
            {
                shot.SetPixels32(photo.GetPixels32());
                shot.Apply();
            }
            errorSimulator = GetComponent<PrivacyProxyErrorSimulator>();
            errorPanel = canvas.GetComponent<PrivacyProxyErrorPanel>();
            if (errorPanel == null) errorPanel = canvas.gameObject.AddComponent<PrivacyProxyErrorPanel>();
            qwen = real ? new QwenDescriptionGenerator() : null;
            modelLoading = real;
            descriptionSession = new DescriptionSession(real ? qwen : new SimulatedDescriptionGenerator());
            descriptionSession.Changed += OnDescriptionChanged;
            var questionAsset = Resources.Load<TextAsset>("perguntas");
            questions = ParseQuestions(questionAsset != null ? questionAsset.text : null);
            BuildPages();
            NavigateTo(0);
            if (real)
            {
                double loadStart = Now;
                sam = new EdgeSamSegmenter(encoderAsset, decoderAsset);
                FlowStepLogger.Append(0, "edgesam_load", Now - loadStart);
                LoadQwen();
            }
            FlowStepLogger.Append(0, "app_ready", Now);
            if (errorSimulator != null && errorSimulator.MissingModelFiles)
                ShowMissingModelFiles();
        }
        catch (Exception error)
        {
            Debug.LogError("Privacy Proxy flow could not start: " + error.Message, this);
        }
    }

    void ValidateInputs()
    {
        if (canvas == null || photo == null || maskImage == null)
            throw new InvalidOperationException("Assign Canvas, Photo and Mask Image.");
        var responseAsset = Resources.Load<TextAsset>("example_ai_response");
        if (responseAsset == null)
            throw new InvalidOperationException("Resources/example_ai_response.txt is missing.");
        if (!photo.isReadable || !maskImage.isReadable)
            throw new InvalidOperationException("Enable Read/Write on the photo and mask.");
        if (photo.width != maskImage.width || photo.height != maskImage.height)
            throw new InvalidOperationException("Photo and mask dimensions differ.");
        if (string.IsNullOrWhiteSpace(responseAsset.text))
            throw new InvalidOperationException("The example AI response file is empty.");
        responseText = responseAsset.text.Trim();
    }

    void BuildPages()
    {
        BuildCapturePage();
        BuildSelectionPage();
        BuildDescriptionPage();
        BuildReviewPage();
        BuildResponsePage();
    }

    void BuildCapturePage()
    {
        var page = CreatePage("Screen 1 — Captura");
        Title(page.transform, "1. Capturar foto", real
            ? "Aponte o óculos para o objeto e capture."
            : "Exemplo local: sem o óculos, a foto não vem da câmera.");
        Button(page.transform, "Previous question", "<", .04f, .73f, .12f, .83f, () => StepQuestion(-1));
        var questionButton = Button(page.transform, "Question", "", .13f, .73f, .87f, .83f, OpenKeyboard);
        questionLabel = questionButton.transform.Find("Text").GetComponent<Text>();
        questionLabel.fontSize = 20;
        Button(page.transform, "Next question", ">", .88f, .73f, .96f, .83f, () => StepQuestion(1));
        if (!real) questionInput = QuestionInput(page.transform, .13f, .73f, .87f, .83f);
        livePreview = Preview(page.transform, "Live camera", real ? null : shot, .12f, .28f, .88f, .71f);
        livePreview.enabled = !real; // a RawImage without a texture draws solid white
        captureStatus = Label(page.transform, "Capture status", "", 20, .05f, .21f, .95f, .27f);
        captureButton = Button(page.transform, "Capture", real ? "Capturar" : "Usar foto de exemplo", .25f, .10f, .75f, .20f,
            Capture);
        RefreshQuestion();
    }

    InputField QuestionInput(Transform parent, float x0, float y0, float x1, float y1)
    {
        var item = Element(parent, "Question input", x0, y0, x1, y1);
        var image = item.AddComponent<Image>();
        image.color = new Color(.97f, .97f, .97f, 1f);
        var text = TextLabel(item.transform, "", 20, .02f, 0, .98f, 1);
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        var placeholder = TextLabel(item.transform, "Digite sua pergunta", 20, .02f, 0, .98f, 1);
        placeholder.color = new Color(.45f, .45f, .45f);
        placeholder.alignment = TextAnchor.MiddleLeft;
        var field = item.AddComponent<InputField>();
        field.targetGraphic = image;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.lineType = InputField.LineType.SingleLine;
        field.onValueChanged.AddListener(SelectCustomQuestion);
        item.SetActive(false);
        return field;
    }

    public static string[] ParseQuestions(string json)
    {
        try
        {
            var list = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<QuestionList>(json);
            var found = new List<string>();
            if (list?.perguntas != null)
                foreach (var question in list.perguntas)
                    if (!string.IsNullOrWhiteSpace(question)) found.Add(question.Trim());
            if (found.Count > 0) return found.ToArray();
        }
        catch (ArgumentException error)
        {
            Debug.LogError("Flow: perguntas.json could not be read: " + error.Message);
        }
        return DefaultQuestions;
    }

    public string Question => questionIndex < questions.Length ? questions[questionIndex] : customQuestion.Trim();
    public bool CanCapture => !string.IsNullOrWhiteSpace(Question);
    public int QuestionOptionCount => questions.Length + 1;

    public void SelectQuestion(int index)
    {
        questionIndex = (index % QuestionOptionCount + QuestionOptionCount) % QuestionOptionCount;
        RefreshQuestion();
    }

    void StepQuestion(int step) => SelectQuestion(questionIndex + step);

    public void SelectCustomQuestion(string text)
    {
        customQuestion = text ?? "";
        questionIndex = questions.Length;
        if (questionInput != null && questionInput.text != customQuestion) questionInput.SetTextWithoutNotify(customQuestion);
        RefreshQuestion();
    }

    // The Quest has no InputField support through QuestCanvasPointer, so the question button opens the
    // system keyboard (requiresSystemKeyboard in OculusProjectConfig). Update reads it when it closes.
    void OpenKeyboard()
    {
        if (!real || questionIndex != questions.Length) return;
        keyboard = TouchScreenKeyboard.Open(customQuestion, TouchScreenKeyboardType.Default, false, false, false, false,
            "Digite sua pergunta");
    }

    void RefreshQuestion()
    {
        if (questionLabel == null) return;
        bool custom = questionIndex == questions.Length;
        questionLabel.text = !custom ? questions[questionIndex]
            : !string.IsNullOrWhiteSpace(customQuestion) ? customQuestion
            : real ? TypeQuestionOption + " (toque para abrir o teclado)" : TypeQuestionOption;
        if (questionInput != null) questionInput.gameObject.SetActive(custom);
        captureButton.interactable = CanCapture;
        if (!CanCapture) captureStatus.text = EmptyQuestionWarning;
        else if (captureStatus.text == EmptyQuestionWarning) captureStatus.text = "";
    }

    public CaptureInfo NewCaptureInfo(int width, int height) => new CaptureInfo
        { capture = captureNumber, width = width, height = height, margin_px = margin, question = Question };

    void BuildSelectionPage()
    {
        var page = CreatePage("Screen 2 — Seleção");
        Title(page.transform, "2. Selecionar objeto", "Clique em dois cantos opostos ao redor do objeto.");
        selectionPhoto = Preview(page.transform, "Selectable photo", shot, .08f, .26f, .92f, .82f);
        selectionPhoto.raycastTarget = true;
        selectionPhotoRect = selectionPhoto.rectTransform;
        selectionPhotoFillAndEdges();
        var eventTrigger = selectionPhoto.gameObject.AddComponent<EventTrigger>();
        var click = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        click.callback.AddListener(OnSelectionPhotoClicked);
        eventTrigger.triggers.Add(click);
        selectionStatus = Label(page.transform, "Selection status", "Marque dois cantos opostos.", 20, .05f, .18f, .95f, .24f);
        cancelSelectionButton = Button(page.transform, "Cancel selection", "Cancelar", .12f, .07f, .44f, .16f,
            StartOver);
        confirmSelectionButton = Button(page.transform, "Confirm selection", "Confirmar seleção", .56f, .07f, .88f, .16f,
            ConfirmSelection);
        confirmSelectionButton.interactable = false;
        confirmSelectionButton.gameObject.SetActive(false);
    }

    void selectionPhotoFillAndEdges()
    {
        var fillObject = Element(selectionPhotoRect, "Selection fill", 0, 0, 1, 1);
        selectionFill = fillObject.AddComponent<RawImage>();
        selectionFill.color = new Color(.12f, .42f, 1f, .16f);
        selectionFill.raycastTarget = false;
        selectionFill.gameObject.SetActive(false);
        selectionEdges = new RectTransform[4];
        for (int i = 0; i < selectionEdges.Length; i++)
        {
            var edge = Element(selectionPhotoRect, "Selection edge " + i, .5f, .5f, .5f, .5f);
            var image = edge.AddComponent<Image>();
            image.color = new Color(.08f, .38f, 1f, 1f);
            image.raycastTarget = false;
            edge.gameObject.SetActive(false);
            selectionEdges[i] = edge.GetComponent<RectTransform>();
        }
        selectionCornerMarkers = new RectTransform[2];
        for (int i = 0; i < selectionCornerMarkers.Length; i++)
        {
            var marker = Element(selectionPhotoRect, "Selection corner marker " + i, .5f, .5f, .5f, .5f);
            marker.GetComponent<RectTransform>().sizeDelta = new Vector2(24, 24);
            var border = marker.AddComponent<Image>();
            border.color = Color.white;
            border.raycastTarget = false;
            var center = Element(marker.transform, "Marker center", .15f, .15f, .85f, .85f);
            var centerImage = center.AddComponent<Image>();
            centerImage.color = new Color(.04f, .24f, .9f, 1f);
            centerImage.raycastTarget = false;
            marker.SetActive(false);
            selectionCornerMarkers[i] = marker.GetComponent<RectTransform>();
        }
    }

    void BuildDescriptionPage()
    {
        var page = CreatePage("Screen 3 — Descrição");
        Title(page.transform, "3. Descrição local", "A descrição é uma candidata; confira antes de compartilhar.");
        AddContour(Preview(page.transform, "Description source", shot, .04f, .34f, .48f, .80f));
        cropView = Preview(page.transform, "Description crop", null, .52f, .34f, .96f, .80f);
        cropView.enabled = false;
        photoFitters.Remove(cropView.GetComponent<AspectRatioFitter>()); // it follows the crop, not the photo
        Label(page.transform, "Source label", "Foto congelada", 20, .04f, .81f, .48f, .87f);
        Label(page.transform, "Crop label", "Recorte local", 20, .52f, .81f, .96f, .87f);
        descriptionStatus = Label(page.transform, "Description status", "Pronto para gerar.", 20, .04f, .25f, .96f, .32f);
        candidateLabel = Label(page.transform, "Candidate description", "", 23, .04f, .17f, .96f, .24f);
        candidateLabel.color = new Color(1f, .88f, .45f);
        Button(page.transform, "Back to selection", "Voltar", .04f, .06f, .25f, .14f,
            () => { descriptionSession.Cancel(); ResetSelection(); NavigateTo(1); }); // back = redo the selection
        generateButton = Button(page.transform, "Generate description", "Gerar descrição", .30f, .06f, .70f, .14f,
            GenerateDescription);
        generateButtonLabel = generateButton.transform.Find("Text").GetComponent<Text>();
        CreateGenerationSpinner();
        Button(page.transform, "Continue to review", "Continuar", .53f, .06f, .94f, .14f,
            () => { if (!string.IsNullOrEmpty(candidate)) EnterReview(); });
        continueDescriptionButton = page.transform.Find("Continue to review").GetComponent<Button>();
        continueDescriptionButton.gameObject.SetActive(false);
    }

    void BuildReviewPage()
    {
        var page = CreatePage("Screen 4 — Revisão");
        Title(page.transform, "4. Revisar compartilhamento", "A visão local continua com o objeto real.");
        Label(page.transform, "Local label", "Visão local", 20, .04f, .83f, .60f, .89f);
        Label(page.transform, "Share label", "Prévia do compartilhamento", 20, .64f, .83f, .96f, .89f);
        var local = Preview(page.transform, "Local photo", shot, .04f, .31f, .60f, .82f);
        AddContour(local);
        infoButton = InfoButton(local.transform, "Description info", ToggleDescription);
        descriptionCard = InfoCard(page.transform, "Description card", .08f, .44f, .53f, .75f, ToggleDescription);
        sharePreview = Preview(page.transform, "Selected version", null, .64f, .44f, .96f, .82f);
        choiceLabel = Label(page.transform, "Selected version label", "Escolha uma versão para continuar.", 20, .64f, .35f, .96f, .43f);
        reviewStatus = Label(page.transform, "Review status", "", 18, .04f, .02f, .96f, .06f);
        Label(page.transform, "Permission prompt", "Escolha qual versão deseja compartilhar.", 22, .04f, .24f, .96f, .30f);
        Button(page.transform, "Allow original", "Permitir foto original", .08f, .12f, .48f, .21f, AllowOriginal);
        Button(page.transform, "Keep protected", "Manter versão protegida", .52f, .12f, .92f, .21f, KeepProtected);
        continueResponseButton = Button(page.transform, "Continue to response", "Continuar", .30f, .06f, .70f, .11f,
            EnterResponse);
        continueResponseButton.gameObject.SetActive(false);
    }

    void BuildResponsePage()
    {
        var page = CreatePage("Screen 5 — Resposta");
        responseSubtitle = Title(page.transform, "5. Resposta da IA", "");
        sentVersionLabel = Label(page.transform, "Sent version label", "", 20, .04f, .83f, .60f, .89f);
        Label(page.transform, "Answer label", "Resposta", 20, .64f, .83f, .96f, .89f);
        responsePhoto = Preview(page.transform, "Response photo", null, .04f, .31f, .60f, .82f); // the sent image
        responsePhoto.enabled = false;
        responseInfoButton = InfoButton(responsePhoto.transform, "Response description info", ToggleResponseInfo);
        responseInfoCard = InfoCard(page.transform, "Response description card", .08f, .44f, .53f, .75f, ToggleResponseInfo);
        var scrollRoot = Element(page.transform, "Response scroll area", .64f, .31f, .96f, .82f);
        var scrollBackground = scrollRoot.AddComponent<Image>();
        scrollBackground.color = new Color(.97f, .97f, .97f, 1f);
        var scroll = scrollRoot.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        var viewport = Element(scrollRoot.transform, "Viewport", .02f, .02f, .98f, .98f);
        var maskImage = viewport.AddComponent<Mask>();
        maskImage.showMaskGraphic = false;
        viewport.AddComponent<Image>().color = Color.white;
        var content = Element(viewport.transform, "Content", 0, 0, 1, 1);
        var contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(.5f, 1);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0, 600);
        responseContent = contentRect;
        responseLabel = Label(content.transform, "Response text", "", 25, .03f, .03f, .97f, .97f);
        responseLabel.color = Color.black;
        responseLabel.alignment = TextAnchor.UpperLeft;
        responseLabel.verticalOverflow = VerticalWrapMode.Overflow;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.content = content.GetComponent<RectTransform>();
        scroll.movementType = ScrollRect.MovementType.Clamped;
        responseStatus = Label(page.transform, "Response status", "", 20, .04f, .22f, .96f, .29f);
        sendingSpinner = CreateSpinner(responseStatus.transform.parent, .30f);
        Button(page.transform, "New photo", "Nova foto", .25f, .10f, .75f, .20f, StartOver);
    }

    // Loaded once, when the app opens. The first load also checks the files, which takes a while.
    async void LoadQwen()
    {
        string error;
        try
        {
            error = await qwen.LoadAsync(Application.persistentDataPath, true);
        }
        catch (Exception e)
        {
            error = "Não consegui carregar os modelos: " + e.Message;
        }
        if (this == null) return;
        if (qwen.CheckSeconds > 0) FlowStepLogger.Append(0, "model_check", qwen.CheckSeconds * 1000);
        if (error == null) FlowStepLogger.Append(0, "qwen_load", qwen.LoadSeconds * 1000);
        modelLoading = false;
        modelError = error;
        if (error != null)
        {
            Debug.LogError("Flow: " + error, this);
            ShowMissingModelFiles();
        }
        ShowGenerateReady();
    }

    void ShowGenerateReady()
    {
        SetGeneratingVisual(false);
        generateButton.interactable = !modelLoading;
        descriptionStatus.text = modelLoading ? "Carregando a IA local... (na primeira vez, confere os arquivos)"
            : modelError != null ? "Os arquivos de IA não foram carregados." : "Pronto para gerar.";
    }

    // Raise the CPU clock only while the model runs. A cancelled run can
    // still be ending when the next one starts, so the level goes back only after the last one.
    void HoldCpu()
    {
        if (cpuHolds++ > 0) return;
        cpuLevel = OVRManager.suggestedCpuPerfLevel;
        OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
    }

    void ReleaseCpu()
    {
        if (--cpuHolds == 0) OVRManager.suggestedCpuPerfLevel = cpuLevel;
    }

    async void GenerateDescription()
    {
        if (descriptionSession == null || descriptionSession.IsGenerating || crop == null || modelLoading) return;
        if (modelError != null)
        {
            ShowMissingModelFiles();
            return;
        }
        if (errorSimulator != null && errorSimulator.ConsumeQwenFailure())
        {
            errorPanel.Show("Falha ao gerar a descrição", "O Qwen não conseguiu gerar uma descrição. Tente novamente.",
                "Tentar de novo", GenerateDescription);
            return;
        }
        candidate = "";
        candidateLabel.text = "";
        generateButton.gameObject.SetActive(true);
        generateButtonLabel.text = "Gerando descrição";
        SetButtonHorizontalRange(generateButton, .30f, .70f);
        SetGeneratingVisual(true);
        continueDescriptionButton.gameObject.SetActive(false);
        descriptionStatus.text = qwen != null ? "Gerando descrição..." : "Gerando descrição... (simulação de 15 s)";
        try
        {
            DescriptionSession.Outcome outcome;
            int number = captureNumber;
            double start = Now;
            if (qwen != null) HoldCpu();
            try
            {
                outcome = await descriptionSession.GenerateAsync(crop);
            }
            finally
            {
                if (qwen != null) ReleaseCpu();
            }
            // Discarded: cancelled or from an older photo. Whoever invalidated it already reset the screen.
            if (this == null || outcome == DescriptionSession.Outcome.Discarded) return;
            SetGeneratingVisual(false);
            if (outcome == DescriptionSession.Outcome.Failed)
            {
                descriptionStatus.text = "Falha ao gerar: " + descriptionSession.Error;
                generateButtonLabel.text = "Tentar novamente";
                SetButtonHorizontalRange(generateButton, .30f, .70f);
                generateButton.gameObject.SetActive(true);
                errorPanel.Show("Falha ao gerar a descrição", descriptionSession.Error,
                    "Tentar de novo", GenerateDescription);
                return;
            }
            FlowStepLogger.Append(number, "qwen", Now - start);
            candidate = descriptionSession.Candidate;
            candidateLabel.text = string.IsNullOrEmpty(candidate) ? "" : "Candidata (revisar): " + candidate;
            descriptionStatus.text = string.IsNullOrEmpty(candidate)
                ? "Geração concluída sem descrição. Tente novamente."
                : qwen != null ? $"Concluído em {qwen.LastSeconds:F1} s. A descrição exige revisão."
                : "Simulação concluída. A descrição exige revisão.";
            generateButtonLabel.text = string.IsNullOrEmpty(candidate) ? "Tentar novamente" : "Gerar novamente";
            SetButtonHorizontalRange(generateButton, string.IsNullOrEmpty(candidate) ? .30f : .06f,
                string.IsNullOrEmpty(candidate) ? .70f : .47f);
            SetButtonHorizontalRange(continueDescriptionButton, .53f, .94f);
            generateButton.gameObject.SetActive(true);
            continueDescriptionButton.gameObject.SetActive(!string.IsNullOrEmpty(candidate));
        }
        catch (Exception error)
        {
            if (this == null) return;
            SetGeneratingVisual(false);
            descriptionStatus.text = "Falha ao gerar: " + error.Message;
            generateButtonLabel.text = "Tentar novamente";
            SetButtonHorizontalRange(generateButton, .30f, .70f);
            generateButton.gameObject.SetActive(true);
            errorPanel.Show("Falha ao gerar a descrição", error.Message, "Tentar de novo", GenerateDescription);
        }
    }

    void Capture()
    {
        if (!CanCapture)
        {
            captureStatus.text = EmptyQuestionWarning;
            return;
        }
        if ((errorSimulator != null && errorSimulator.CameraPermissionDenied) || (real && !HasCameraPermission))
        {
            // Asks again when the system still allows it; the camera starts by itself once granted.
            if (real) OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.PassthroughCameraAccess });
            errorPanel.Show("Acesso à câmera negado",
                "Não foi possível acessar a câmera. Verifique a permissão de câmera nas configurações do app e tente novamente.");
            return;
        }
        if (!real)
        {
            BeginPhoto(photo.GetPixels32(), photo.width, photo.height);
            return;
        }
        if (freezing || cameraAccess == null || !cameraAccess.IsPlaying) return;
        freezing = true;
        captureStatus.text = "Capturando...";
        // Reads the frame back without blocking; PassthroughCameraAccess.GetColors() stalls the frame.
        AsyncGPUReadback.Request(cameraAccess.GetTexture(), 0, TextureFormat.RGBA32, OnFrozen);
    }

    void OnFrozen(AsyncGPUReadbackRequest request)
    {
        if (this == null) return;
        freezing = false;
        captureStatus.text = "";
        if (pageIndex != 0) return;
        if (request.hasError)
        {
            errorPanel.Show("Não foi possível capturar a foto", "A câmera não entregou a imagem. Tente capturar de novo.");
            return;
        }
        var pixels = request.GetData<Color32>().ToArray(); // rows bottom-up, like GetPixels32
        for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
        BeginPhoto(pixels, request.width, request.height);
    }

    // A new frozen photo. The array is handed to EdgeSAM, so it is never changed afterwards.
    void BeginPhoto(Color32[] pixels, int width, int height)
    {
        ClearPhoto();
        shotPixels = pixels;
        if (shot.width != width || shot.height != height) shot.Reinitialize(width, height);
        shot.SetPixels32(pixels);
        shot.Apply();
        SetPhotoAspect(width, height);
        captureNumber++;
        foreach (var header in questionHeaders) header.text = "Pergunta: " + Question;
        // The step log has no text field, so the question goes in the step name.
        FlowStepLogger.Append(captureNumber, "question: " + Question, 0);
        if (Application.isPlaying)
        {
            string root = Application.isEditor ? System.IO.Path.GetDirectoryName(Application.dataPath) : Application.persistentDataPath;
            capture = new CaptureFolder(System.IO.Path.Combine(root, "Saida"), NewCaptureInfo(width, height));
            capture.SavePng("foto.png", pixels, width, height);
            capture.SaveInfo();
        }
        if (sam != null) ReadPhoto(captureNumber, pixels, width, height); // not awaited: the encoder runs while the user draws the box
        NavigateTo(1);
    }

    async void ReadPhoto(int number, Color32[] pixels, int width, int height)
    {
        var read = await sam.SetPhotoAsync(pixels, width, height);
        if (read.Ok) FlowStepLogger.Append(number, "encoder", read.Seconds * 1000);
    }

    static bool HasCameraPermission =>
        OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess);

    static double Now => Time.realtimeSinceStartupAsDouble * 1000; // ms

    void SetPhotoAspect(int width, int height)
    {
        foreach (var fitter in photoFitters) fitter.aspectRatio = (float)width / height;
    }

    void OnDescriptionChanged()
    {
        if (descriptionSession == null || descriptionStatus == null) return;
        if (descriptionSession.State == DescriptionSession.Status.Cancelled)
        {
            SetGeneratingVisual(false);
            descriptionStatus.text = "Geração cancelada. Você pode tentar novamente.";
            generateButtonLabel.text = "Gerar descrição";
            SetButtonHorizontalRange(generateButton, .30f, .70f);
            generateButton.gameObject.SetActive(true);
        }
    }

    void OnSelectionPhotoClicked(BaseEventData data)
    {
        if (pageIndex != 1 || hasSelection || !(data is PointerEventData pointer)
            || pointer.button != PointerEventData.InputButton.Left)
            return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                selectionPhotoRect, pointer.position, pointer.pressEventCamera, out var local)
            || !selectionPhotoRect.rect.Contains(local)) return;

        var rect = selectionPhotoRect.rect;
        int x = Mathf.Clamp(Mathf.FloorToInt((local.x - rect.xMin) / rect.width * shot.width), 0, shot.width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt((rect.yMax - local.y) / rect.height * shot.height), 0, shot.height - 1);
        var point = new Vector2Int(x, y);
        if (!hasFirstCorner)
        {
            firstCorner = point;
            hasFirstCorner = true;
            Place(selectionCornerMarkers[0], PixelToX(x), PixelToY(y), 24, 24);
            selectionCornerMarkers[0].gameObject.SetActive(true);
            selectionStatus.text = $"Primeiro canto ({x}, {y}). Marque o canto oposto.";
            return;
        }

        secondCorner = point;
        if (Mathf.Abs(firstCorner.x - secondCorner.x) + 1 < MinSelectionSide
            || Mathf.Abs(firstCorner.y - secondCorner.y) + 1 < MinSelectionSide)
        {
            ResetSelection("A caixa precisa ter pelo menos 10 px de cada lado. Marque novamente.");
            return;
        }
        hasSelection = true;
        Place(selectionCornerMarkers[1], PixelToX(x), PixelToY(y), 24, 24);
        selectionCornerMarkers[1].gameObject.SetActive(true);
        DrawSelectionBox();
        confirmSelectionButton.interactable = true;
        confirmSelectionButton.gameObject.SetActive(true);
        localSelectionStatus = FormatSelection();
        selectionStatus.text = "Confira a caixa azul. " + localSelectionStatus;
    }

    // The two clicked pixels are inclusive corners, so the far edge gets +1. Origin at the top-left
    // corner of the photo, as EdgeSamSegmenter.SegmentAsync expects.
    public static Rect SelectionBox(Vector2Int a, Vector2Int b) => Rect.MinMaxRect(
        Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x) + 1, Mathf.Max(a.y, b.y) + 1);

    public int PhotoId => photoId;
    public bool HasMask => mask != null;

    async void ConfirmSelection()
    {
        if (!hasSelection || segmenting) return;
        if (errorSimulator != null && errorSimulator.EmptyMask)
        {
            ResetSelection("Marque o objeto inteiro e tente novamente.");
            errorPanel.Show("Nenhum objeto encontrado",
                "A segmentação não encontrou objeto dentro da seleção. Marque o objeto inteiro na tela 2 e confirme novamente.");
            return;
        }
        Debug.Log("Flow selection box_xyxy: " + localSelectionStatus);
        int id = photoId;
        EdgeSamSegmenter.Result result;
        if (sam == null) // Editor: the example mask stands in for EdgeSAM
            result = new EdgeSamSegmenter.Result
                { Mask = MaskContour.MaskFromPng(maskImage), Width = shot.width, Height = shot.height };
        else
        {
            segmenting = true;
            confirmSelectionButton.interactable = false;
            selectionStatus.text = "Procurando o objeto...";
            result = await sam.SegmentAsync(SelectionBox(firstCorner, secondCorner));
            if (this == null || id != photoId) return; // the photo changed meanwhile and already reset the screen
            segmenting = false;
            if (result.Ok) FlowStepLogger.Append(captureNumber, "decoder", result.Seconds * 1000);
        }
        ApplySegmentation(id, result);
    }

    // Returns false when the result is dropped: it belongs to an older photo, or it has no mask.
    public bool ApplySegmentation(int id, EdgeSamSegmenter.Result result)
    {
        if (id != photoId) return false;
        if (!result.Ok)
        {
            ResetSelection("Marque o objeto inteiro e tente novamente.");
            errorPanel.Show(result.Error == EdgeSamSegmenter.EmptyMaskError
                ? "Nenhum objeto encontrado" : "Falha na segmentação", result.Error);
            return false;
        }
        mask = result.Mask;
        Release(outlineTexture);
        Release(cropTexture);
        outlineTexture = MaskContour.DrawOutline(mask, shot.width, shot.height, 5);
        double cropStart = Now;
        crop = ObjectCrop.Create(shotPixels, mask, shot.width, shot.height);
        FlowStepLogger.Append(captureNumber, "crop", Now - cropStart);
        if (capture != null)
        {
            var box = SelectionBox(firstCorner, secondCorner);
            capture.Info.box_xyxy = new[] { (int)box.xMin, (int)box.yMin, (int)box.xMax, (int)box.yMax };
            capture.Info.edgesam_score = result.Score;
            capture.Info.mask_pixels = result.MaskPixels;
            capture.SavePng("mascara.png", CaptureFolder.MaskPixels(mask), shot.width, shot.height);
            capture.SaveInfo();
        }
        cropTexture = MakeTexture(crop.Pixels, crop.Width, crop.Height, "Flow object crop");
        foreach (var view in contourViews)
        {
            view.texture = outlineTexture;
            view.enabled = true;
        }
        cropView.texture = cropTexture;
        cropView.GetComponent<AspectRatioFitter>().aspectRatio = (float)crop.Width / crop.Height;
        cropView.enabled = true;
        descriptionSession.NewPhoto();
        candidate = "";
        candidateLabel.text = "";
        continueDescriptionButton.gameObject.SetActive(false);
        generateButtonLabel.text = "Gerar descrição";
        SetButtonHorizontalRange(generateButton, .30f, .70f);
        generateButton.gameObject.SetActive(true);
        ShowGenerateReady();
        NavigateTo(2);
        return true;
    }

    void ShowMissingModelFiles()
    {
        var manifest = Resources.Load<TextAsset>("qwen_models");
        if (manifest == null)
        {
            errorPanel.Show("Arquivos de IA ausentes", "Não foi possível localizar a lista local dos arquivos de IA.");
            return;
        }

        var files = ModelCheck.Parse(manifest.text).files;
        var commands = new List<string>();
        var names = new List<string>();
        foreach (var file in files)
        {
            commands.Add(ModelCheck.CopyCommand(file.name));
            names.Add(file.name);
        }
        errorPanel.Show("Arquivos de IA ausentes",
            "Os arquivos necessários para a IA não foram encontrados:\n"
            + string.Join("\n", names.ToArray())
            + "\n\nCopie cada arquivo com o comando correspondente:\n"
            + string.Join("\n", commands.ToArray()));
    }

    void ResetSelection(string message = "Marque dois cantos opostos.")
    {
        firstCorner = secondCorner = Vector2Int.zero;
        hasFirstCorner = hasSelection = false;
        if (selectionFill != null) selectionFill.gameObject.SetActive(false);
        if (selectionEdges != null) foreach (var edge in selectionEdges) edge.gameObject.SetActive(false);
        if (selectionCornerMarkers != null)
            foreach (var marker in selectionCornerMarkers) marker.gameObject.SetActive(false);
        if (confirmSelectionButton != null)
        {
            confirmSelectionButton.interactable = false;
            confirmSelectionButton.gameObject.SetActive(false);
        }
        if (selectionStatus != null) selectionStatus.text = message;
    }

    string FormatSelection() => $"[{Mathf.Min(firstCorner.x, secondCorner.x)}, {Mathf.Min(firstCorner.y, secondCorner.y)}, "
        + $"{Mathf.Max(firstCorner.x, secondCorner.x)}, {Mathf.Max(firstCorner.y, secondCorner.y)}]";

    void DrawSelectionBox()
    {
        float x0 = PixelToX(Mathf.Min(firstCorner.x, secondCorner.x));
        float x1 = PixelToX(Mathf.Max(firstCorner.x, secondCorner.x));
        float y0 = PixelToY(Mathf.Min(firstCorner.y, secondCorner.y));
        float y1 = PixelToY(Mathf.Max(firstCorner.y, secondCorner.y));
        Place(selectionFill.rectTransform, (x0 + x1) * .5f, (y0 + y1) * .5f,
            x1 - x0 + SelectionLineWidth, y0 - y1 + SelectionLineWidth);
        selectionFill.gameObject.SetActive(true);
        Place(selectionEdges[0], (x0 + x1) * .5f, y1, x1 - x0 + SelectionLineWidth, SelectionLineWidth);
        Place(selectionEdges[1], (x0 + x1) * .5f, y0, x1 - x0 + SelectionLineWidth, SelectionLineWidth);
        Place(selectionEdges[2], x0, (y0 + y1) * .5f, SelectionLineWidth, y0 - y1 + SelectionLineWidth);
        Place(selectionEdges[3], x1, (y0 + y1) * .5f, SelectionLineWidth, y0 - y1 + SelectionLineWidth);
        foreach (var edge in selectionEdges) edge.gameObject.SetActive(true);
    }

    void EnterReview()
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        Release(protectedTexture);
        double composeStart = Now;
        protectedTexture = AiImageExporter.Compose(shot, mask, candidate, margin);
        FlowStepLogger.Append(captureNumber, "ai_image", Now - composeStart);
        if (capture != null)
        {
            capture.Info.description = candidate;
            capture.Info.choice = "";
            capture.SavePng("imagem-ia.png", protectedTexture.GetPixels32(), shot.width, shot.height);
            capture.SaveInfo();
        }
        var localPage = pages[3].transform;
        var localView = localPage.Find("Local photo frame/Local photo").GetComponent<RawImage>();
        descriptionCard.GetComponentInChildren<Text>().text =
            "Descrição candidata (revisar):\n" + candidate;
        hasReviewChoice = false;
        allowsOriginal = false;
        choiceLabel.text = "Escolha uma versão para continuar.";
        reviewStatus.text = "Ao continuar, só a versão escolhida vai para a IA, com a pergunta.";
        continueResponseButton.gameObject.SetActive(false);
        sharePreview.texture = protectedTexture;
        sharePreview.enabled = true;
        NavigateTo(3);
        Canvas.ForceUpdateCanvases();
        SetInfoButtonPosition(infoButton, localView.rectTransform);
    }

    void AllowOriginal() => SetReviewChoice(true, "Será usada a foto original na prévia de compartilhamento.");
    void KeepProtected() => SetReviewChoice(false, "Será usada a versão protegida na prévia de compartilhamento.");

    void EnterResponse()
    {
        if (!hasReviewChoice) return;
        responsePhoto.texture = allowsOriginal ? shot : protectedTexture;
        responsePhoto.enabled = true;
        sentVersionLabel.text = allowsOriginal ? "Enviada à IA: foto original" : "Enviada à IA: versão protegida";
        responseInfoCard.GetComponentInChildren<Text>().text = "Descrição candidata (revisar):\n" + candidate;
        NavigateTo(4);
        Canvas.ForceUpdateCanvases();
        SetInfoButtonPosition(responseInfoButton, responsePhoto.rectTransform);
        SendToAi();
    }

    // The only image that leaves the device: the one reviewed on screen 4, protected
    // unless the user chose "Permitir foto original", with the question from screen 1.
    async void SendToAi()
    {
        if (!hasReviewChoice || protectedTexture == null) return;
        aiCancel?.Cancel();
        aiCancel = new CancellationTokenSource();
        var token = aiCancel.Token;
        int id = photoId, number = captureNumber, width = shot.width, height = shot.height;
        bool original = allowsOriginal;
        var pixels = original ? shotPixels : protectedTexture.GetPixels32();
        if (capture != null)
        {
            capture.Info.sent_image = original ? "original" : "protegida";
            capture.Info.openai_model = aiConfig.model;
            capture.Info.openai_seconds = 0;
            capture.Info.openai_error = "";
            capture.SavePng("enviada.png", pixels, width, height);
            capture.SaveInfo();
        }
        ShowSending(true);
        OpenAiTaskClient.Result result;
        try
        {
            result = await AskAi(Question, pixels, width, height, token);
        }
        catch (OperationCanceledException)
        {
            return; // "Nova foto" already reset the screen
        }
        catch (Exception error)
        {
            result = new OpenAiTaskClient.Result("", 0, OpenAiTaskClient.ErrorKind.ApiError, 0, error.Message);
        }
        if (this == null) return;
        if (ApplyAiResult(id, result) && result.Error == OpenAiTaskClient.ErrorKind.None)
            FlowStepLogger.Append(number, "openai", result.Seconds * 1000);
    }

    public string ResponseText => responseLabel.text;

    // Returns false when the answer belongs to an older photo ("Nova foto" during the call).
    public bool ApplyAiResult(int id, OpenAiTaskClient.Result result)
    {
        if (id != photoId) return false;
        ShowSending(false);
        bool ok = result.Error == OpenAiTaskClient.ErrorKind.None;
        if (capture != null)
        {
            capture.Info.openai_seconds = (float)result.Seconds;
            capture.Info.openai_error = ok ? "" : result.Error + ": " + result.ErrorMessage;
            if (ok) capture.SaveText("resposta-ia.txt", result.Text);
            capture.SaveInfo();
        }
        if (ok)
        {
            responseSubtitle.text = $"Resposta de {aiConfig?.model} em {result.Seconds:F1} s.";
            SetResponse(result.Text);
        }
        else if (result.Error == OpenAiTaskClient.ErrorKind.NoKey && !real)
        {
            responseSubtitle.text = "Resposta de exemplo: sem chave da OpenAI no Editor.";
            SetResponse("Resposta de exemplo\n\n" + responseText);
        }
        else
        {
            responseSubtitle.text = "A IA não respondeu.";
            SetResponse("");
            var (title, message) = AiErrorText(result);
            errorPanel.Show(title, message, "Tentar de novo", SendToAi);
        }
        return true;
    }

    static (string, string) AiErrorText(OpenAiTaskClient.Result result) => result.Error switch
    {
        OpenAiTaskClient.ErrorKind.NoKey => ("Sem chave da OpenAI",
            result.ErrorMessage + " Use tools/copiar_chave_openai.sh e tente de novo."),
        OpenAiTaskClient.ErrorKind.NoNetwork => ("Sem conexão",
            "Não foi possível falar com a OpenAI. Confira o Wi-Fi do óculos e tente de novo."),
        OpenAiTaskClient.ErrorKind.Timeout => ("A IA demorou demais", result.ErrorMessage + " Tente de novo."),
        _ => ("Erro da OpenAI", (result.HttpStatus > 0 ? $"Status {result.HttpStatus}: " : "") + result.ErrorMessage),
    };

    void ShowSending(bool sending)
    {
        sendingSpinner.gameObject.SetActive(sending);
        responseStatus.text = sending ? "Enviando para a IA..." : "";
        if (!sending) return;
        responseSubtitle.text = "Aguardando a resposta.";
        SetResponse("");
    }

    // The scroll content grows with the answer, so long answers can be scrolled.
    void SetResponse(string text)
    {
        responseLabel.text = text;
        Canvas.ForceUpdateCanvases();
        responseContent.sizeDelta = new Vector2(0, Mathf.Max(600, responseLabel.preferredHeight / .94f + 20));
        responseContent.anchoredPosition = Vector2.zero;
    }

    void SetReviewChoice(bool allowOriginal, string status)
    {
        allowsOriginal = allowOriginal;
        hasReviewChoice = true;
        sharePreview.texture = allowsOriginal ? shot : protectedTexture;
        sharePreview.enabled = sharePreview.texture != null;
        choiceLabel.text = allowsOriginal ? "Será enviada: foto original" : "Será enviada: versão protegida";
        reviewStatus.text = status;
        continueResponseButton.gameObject.SetActive(true);
        if (capture != null)
        {
            capture.Info.choice = allowsOriginal ? "original" : "protegida";
            capture.SaveInfo();
        }
    }

    void StartOver()
    {
        NavigateTo(0);
        ClearPhoto();
    }

    // Nothing from the previous photo survives: mask, crop, description and their textures.
    void ClearPhoto()
    {
        photoId++; // a segmentation or AI answer still running belongs to the old photo
        segmenting = false;
        aiCancel?.Cancel();
        aiCancel = null;
        ShowSending(false);
        SetResponse("");
        sentVersionLabel.text = "";
        Hide(responsePhoto);
        mask = null;
        crop = null;
        capture = null;
        candidate = "";
        Release(outlineTexture);
        Release(cropTexture);
        Release(protectedTexture);
        outlineTexture = cropTexture = protectedTexture = null;
        foreach (var view in contourViews) Hide(view);
        Hide(cropView);
        Hide(sharePreview);
        descriptionSession.NewPhoto(); // also cancels a description still being generated
        candidateLabel.text = "";
        continueDescriptionButton.gameObject.SetActive(false);
        generateButtonLabel.text = "Gerar descrição";
        SetButtonHorizontalRange(generateButton, .30f, .70f);
        generateButton.gameObject.SetActive(true);
        ShowGenerateReady();
        allowsOriginal = false;
        hasReviewChoice = false;
        continueResponseButton.gameObject.SetActive(false);
        descriptionCard.SetActive(false);
        responseInfoCard.SetActive(false);
        ResetSelection();
    }

    static void Hide(RawImage view)
    {
        view.texture = null;
        view.enabled = false; // a RawImage without a texture draws solid white
    }

    void ToggleDescription() => descriptionCard.SetActive(!descriptionCard.activeSelf);
    void ToggleResponseInfo() => responseInfoCard.SetActive(!responseInfoCard.activeSelf);

    void NavigateTo(int nextPage)
    {
        if (nextPage < 0 || nextPage >= pages.Count) throw new ArgumentOutOfRangeException(nameof(nextPage));
        if (pageIndex >= 0)
            FlowScreenLogger.Append(pageIndex + 1, Time.realtimeSinceStartupAsDouble - screenEnteredAt, candidate);
        for (int i = 0; i < pages.Count; i++) pages[i].SetActive(i == nextPage);
        pageIndex = nextPage;
        screenEnteredAt = Time.realtimeSinceStartupAsDouble;
        Debug.Log("Flow: screen " + (pageIndex + 1));
    }

    GameObject CreatePage(string name)
    {
        var page = Element(canvas.transform, name, 0, 0, 1, 1);
        var background = page.AddComponent<Image>();
        background.color = new Color(.025f, .03f, .045f, 1f);
        background.raycastTarget = false;
        if (pages.Count > 0) // screens 2 to 5 show the question chosen on screen 1
        {
            var header = Label(page.transform, "Question header", "", 16, .04f, .975f, .96f, 1f);
            header.color = new Color(.75f, .78f, .85f);
            questionHeaders.Add(header);
        }
        pages.Add(page);
        return page;
    }

    Text Title(Transform parent, string title, string subtitle)
    {
        Label(parent, "Title", title, 30, .04f, .91f, .96f, .98f);
        return Label(parent, "Subtitle", subtitle, 20, .04f, .85f, .96f, .91f);
    }

    RawImage Preview(Transform parent, string name, Texture texture, float x0, float y0, float x1, float y1)
    {
        var frame = Element(parent, name + " frame", x0, y0, x1, y1);
        var background = frame.AddComponent<Image>();
        background.color = new Color(.12f, .13f, .16f, 1f);
        background.raycastTarget = false;
        var imageObject = Element(frame.transform, name, 0, 0, 1, 1);
        var image = imageObject.AddComponent<RawImage>();
        image.texture = texture;
        image.color = Color.white;
        image.raycastTarget = false;
        var fitter = imageObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        var ratioSource = texture != null ? texture : shot;
        fitter.aspectRatio = (float)ratioSource.width / ratioSource.height;
        if (ratioSource == shot) photoFitters.Add(fitter); // follows the photo size after each capture
        return image;
    }

    void AddContour(RawImage image)
    {
        var overlayObject = Element(image.transform, "Mask contour", 0, 0, 1, 1);
        var overlay = overlayObject.AddComponent<RawImage>();
        overlay.color = Color.white;
        overlay.raycastTarget = false;
        overlay.enabled = false; // until there is a mask
        contourViews.Add(overlay);
    }

    Button InfoButton(Transform parent, string name, UnityEngine.Events.UnityAction action)
    {
        var item = Element(parent, name, .5f, .5f, .5f, .5f);
        item.GetComponent<RectTransform>().sizeDelta = new Vector2(48, 48);
        var image = item.AddComponent<Image>();
        image.color = new Color(.08f, .38f, 1f, .96f);
        var button = item.AddComponent<Button>();
        button.targetGraphic = image;
        TextLabel(item.transform, "i", 28, 0, 0, 1, 1);
        button.onClick.AddListener(action);
        return button;
    }

    void SetInfoButtonPosition(Button button, RectTransform imageRect)
    {
        int maxX = -1, maxY = -1;
        for (int y = 0; y < shot.height; y++)
        for (int x = 0; x < shot.width; x++)
        {
            if (!mask[y * shot.width + x]) continue;
            maxX = Mathf.Max(maxX, x);
            maxY = Mathf.Max(maxY, y);
        }
        if (maxX < 0) return;
        int topY = shot.height - 1 - maxY;
        var rect = imageRect.rect;
        var buttonRect = (RectTransform)button.transform;
        buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(.5f, .5f);
        buttonRect.anchoredPosition = new Vector2(
            rect.xMin + (maxX + .5f) / shot.width * rect.width,
            rect.yMax - (topY + .5f) / shot.height * rect.height);
    }

    GameObject InfoCard(Transform parent, string name, float x0, float y0, float x1, float y1,
        UnityEngine.Events.UnityAction closeAction)
    {
        var card = Element(parent, name, x0, y0, x1, y1);
        card.AddComponent<Image>().color = new Color(.97f, .97f, .97f, .98f);
        var text = Label(card.transform, "Description text", "", 24, .05f, .08f, .95f, .86f);
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        Button(card.transform, "Close info", "Fechar", .70f, .87f, .96f, .98f, closeAction);
        card.SetActive(false);
        return card;
    }

    Button Button(Transform parent, string name, string text, float x0, float y0, float x1, float y1,
        UnityEngine.Events.UnityAction action)
    {
        var item = Element(parent, name, x0, y0, x1, y1);
        var image = item.AddComponent<Image>();
        image.color = new Color(.12f, .28f, .48f, 1f);
        var button = item.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.highlightedColor = new Color(.2f, .42f, .68f, 1f);
        colors.disabledColor = new Color(.14f, .16f, .19f, 1f);
        button.colors = colors;
        var label = TextLabel(item.transform, text, 23, 0, 0, 1, 1);
        label.raycastTarget = false;
        button.onClick.AddListener(action);
        return button;
    }

    Text Label(Transform parent, string name, string text, int size,
        float x0, float y0, float x1, float y1) => TextLabel(Element(parent, name, x0, y0, x1, y1).transform,
            text, size, 0, 0, 1, 1);

    Text TextLabel(Transform parent, string text, int size,
        float x0, float y0, float x1, float y1)
    {
        var labelObject = Element(parent, "Text", x0, y0, x1, y1);
        var label = labelObject.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = size;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.text = text;
        label.supportRichText = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;
        return label;
    }

    static GameObject Element(Transform parent, string name, float x0, float y0, float x1, float y1)
    {
        var item = new GameObject(name, typeof(RectTransform));
        item.transform.SetParent(parent, false);
        var rect = item.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(x0, y0);
        rect.anchorMax = new Vector2(x1, y1);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return item;
    }

    float PixelToX(int x) => selectionPhotoRect.rect.xMin + (x + .5f) / shot.width * selectionPhotoRect.rect.width;
    float PixelToY(int y) => selectionPhotoRect.rect.yMax - (y + .5f) / shot.height * selectionPhotoRect.rect.height;

    static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    static void SetButtonHorizontalRange(Button button, float min, float max)
    {
        var rect = (RectTransform)button.transform;
        rect.anchorMin = new Vector2(min, rect.anchorMin.y);
        rect.anchorMax = new Vector2(max, rect.anchorMax.y);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    static Texture2D MakeTexture(Color32[] pixels, int width, int height, string name)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = name;
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    void OnDestroy()
    {
        aiCancel?.Cancel();
        if (descriptionSession != null)
        {
            descriptionSession.Changed -= OnDescriptionChanged;
            descriptionSession.Dispose();
        }
        sam?.Dispose(); // each frees its model only after the call still running has ended
        qwen?.Dispose();
        Release(outlineTexture);
        Release(cropTexture);
        Release(protectedTexture);
        Release(shot);
    }

    void Update()
    {
        if (generationSpinner != null && generationSpinner.gameObject.activeSelf)
            generationSpinner.Rotate(0, 0, -360f * Time.unscaledDeltaTime);
        if (sendingSpinner != null && sendingSpinner.gameObject.activeSelf)
            sendingSpinner.Rotate(0, 0, -360f * Time.unscaledDeltaTime);
        if (keyboard != null && keyboard.status != TouchScreenKeyboard.Status.Visible)
        {
            if (keyboard.status == TouchScreenKeyboard.Status.Done) SelectCustomQuestion(keyboard.text);
            keyboard = null; // cancelled or lost focus: keep the previous text
        }
        if (!real || pageIndex != 0 || cameraAccess == null || livePreview == null) return;
        if (!cameraAccess.IsPlaying)
        {
            livePreview.enabled = false;
            captureStatus.text = !HasCameraPermission ? "Sem permissão de câmera."
                : cameraAccess.enabled ? "Abrindo a câmera..." : "A câmera não abriu.";
        }
        else if (!livePreview.enabled)
        {
            livePreview.texture = cameraAccess.GetTexture();
            livePreview.enabled = true;
            SetPhotoAspect(cameraAccess.CurrentResolution.x, cameraAccess.CurrentResolution.y);
            captureStatus.text = "";
        }
    }

    void CreateGenerationSpinner() => generationSpinner = CreateSpinner(generateButton.transform, .84f);

    RectTransform CreateSpinner(Transform parent, float x)
    {
        var spinner = Element(parent, "Spinner", x, .5f, x, .5f);
        spinner.GetComponent<RectTransform>().sizeDelta = new Vector2(28, 28);
        var spinnerRect = spinner.GetComponent<RectTransform>();
        const int segmentCount = 8;
        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i * 360f / segmentCount;
            float radians = angle * Mathf.Deg2Rad;
            var segment = Element(spinner.transform, "Spinner segment " + i, .5f, .5f, .5f, .5f);
            var segmentRect = segment.GetComponent<RectTransform>();
            Place(segmentRect, Mathf.Sin(radians) * 8f, Mathf.Cos(radians) * 8f, 3f, 6f);
            segmentRect.localRotation = Quaternion.Euler(0, 0, -angle);
            var image = segment.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 1f - i * .09f);
            image.raycastTarget = false;
        }
        spinner.SetActive(false);
        return spinnerRect;
    }

    void SetGeneratingVisual(bool generating)
    {
        generateButton.interactable = !generating;
        generationSpinner.gameObject.SetActive(generating);
        SetTextHorizontalRange(generateButtonLabel, generating ? .04f : 0f, generating ? .72f : 1f);
    }

    static void SetTextHorizontalRange(Text text, float min, float max)
    {
        var rect = (RectTransform)text.transform;
        rect.anchorMin = new Vector2(min, rect.anchorMin.y);
        rect.anchorMax = new Vector2(max, rect.anchorMax.y);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    static void Release(UnityEngine.Object resource)
    {
        if (resource == null) return;
        if (Application.isPlaying) Destroy(resource);
        else DestroyImmediate(resource);
    }
}
