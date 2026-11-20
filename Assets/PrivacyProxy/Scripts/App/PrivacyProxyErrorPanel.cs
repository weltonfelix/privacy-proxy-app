using System;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas), typeof(GraphicRaycaster))]
public sealed class PrivacyProxyErrorPanel : MonoBehaviour
{
    GameObject overlay, retryObject;
    Text titleText, messageText, retryText;
    Button okButton, retryButton;
    Action retryAction;

    public bool IsVisible => overlay != null && overlay.activeSelf;

    void Awake()
    {
        Build();
        overlay.SetActive(false);
    }

    public void Show(string title, string message, string retryLabel = null, Action onRetry = null)
    {
        titleText.text = title;
        messageText.text = message;
        retryAction = onRetry;
        retryObject.SetActive(onRetry != null);
        if (onRetry != null) retryText.text = retryLabel ?? "Tentar de novo";
        overlay.transform.SetAsLastSibling();
        overlay.SetActive(true);
    }

    void Build()
    {
        overlay = new GameObject("Error dialog", typeof(RectTransform), typeof(Image));
        overlay.transform.SetParent(transform, false);
        Stretch(overlay.GetComponent<RectTransform>());
        var backdrop = overlay.GetComponent<Image>();
        backdrop.color = new Color(0, 0, 0, .78f);
        backdrop.raycastTarget = true;

        var card = Element(overlay.transform, "Error card", .12f, .08f, .88f, .92f, typeof(Image));
        card.GetComponent<Image>().color = new Color(.96f, .97f, .99f, 1f);
        titleText = MakeText(card.transform, "Error title", 30, .06f, .72f, .94f, .94f);
        titleText.color = new Color(.08f, .12f, .18f, 1f);
        titleText.fontStyle = FontStyle.Bold;
        messageText = MakeText(card.transform, "Error message", 22, .07f, .23f, .93f, .70f);
        messageText.color = new Color(.12f, .14f, .18f, 1f);
        messageText.alignment = TextAnchor.MiddleLeft;
        messageText.verticalOverflow = VerticalWrapMode.Overflow;

        okButton = MakeButton(card.transform, "Error OK", "OK", .57f, .05f, .93f, .20f, Hide);
        retryObject = Element(card.transform, "Error retry", .07f, .05f, .48f, .20f, typeof(Image), typeof(Button));
        retryButton = retryObject.GetComponent<Button>();
        retryButton.targetGraphic = retryObject.GetComponent<Image>();
        retryObject.GetComponent<Image>().color = new Color(.16f, .34f, .57f, 1f);
        retryText = MakeText(retryObject.transform, "Retry label", 20, 0, 0, 1, 1);
        retryText.raycastTarget = false;
        retryButton.onClick.AddListener(Retry);
    }

    void Retry()
    {
        var action = retryAction;
        Hide();
        action?.Invoke();
    }

    void Hide() => overlay.SetActive(false);

    static Button MakeButton(Transform parent, string name, string label, float x0, float y0, float x1, float y1,
        UnityEngine.Events.UnityAction action)
    {
        var item = Element(parent, name, x0, y0, x1, y1, typeof(Image), typeof(Button));
        var image = item.GetComponent<Image>();
        image.color = new Color(.16f, .34f, .57f, 1f);
        var button = item.GetComponent<Button>();
        button.targetGraphic = image;
        var text = MakeText(item.transform, label, 20, 0, 0, 1, 1);
        text.raycastTarget = false;
        button.onClick.AddListener(action);
        return button;
    }

    static Text MakeText(Transform parent, string name, int size, float x0, float y0, float x1, float y1)
    {
        var item = Element(parent, name, x0, y0, x1, y1, typeof(Text));
        var text = item.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.supportRichText = false;
        return text;
    }

    static GameObject Element(Transform parent, string name, float x0, float y0, float x1, float y1,
        params Type[] components)
    {
        var item = new GameObject(name, typeof(RectTransform));
        item.transform.SetParent(parent, false);
        foreach (var component in components) item.AddComponent(component);
        var rect = item.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(x0, y0);
        rect.anchorMax = new Vector2(x1, y1);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return item;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
