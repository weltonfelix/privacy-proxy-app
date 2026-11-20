using System;
using UnityEngine;
using UnityEngine.UI;

// Local composition only: returns the exact pixels to preview/save, never sends them.
public static class AiImageExporter
{
    const int ExportLayer = 31;

    // The caller owns the returned texture. Text must be a reviewed category/shape;
    // composing it does not check the candidate's privacy or the mask's accuracy.
    public static Texture2D Compose(Texture2D photo, bool[] mask, string reviewedDescription,
        int margin = AiImageComposer.DefaultMarginPixels)
    {
        if (photo == null) throw new ArgumentNullException(nameof(photo));
        if (!photo.isReadable) throw new ArgumentException("Enable Read/Write on the photo.", nameof(photo));
        if (string.IsNullOrWhiteSpace(reviewedDescription))
            throw new ArgumentException("Provide a reviewed category or shape.", nameof(reviewedDescription));

        var covered = AiImageComposer.CoverObject(photo.GetPixels32(), mask, photo.width, photo.height,
            out var bounds, margin);
        Texture2D background = null, corners = null, output = null;
        Sprite cardSprite = null;
        RenderTexture target = null;
        GameObject root = null;
        var previous = RenderTexture.active;
        try
        {
            background = new Texture2D(photo.width, photo.height, TextureFormat.RGBA32, false);
            background.filterMode = FilterMode.Point;
            background.wrapMode = TextureWrapMode.Clamp;
            background.SetPixels32(covered);
            background.Apply();

            target = RenderTexture.GetTemporary(photo.width, photo.height, 24, RenderTextureFormat.ARGB32);
            target.antiAliasing = 1;
            root = new GameObject("Protected image export") { hideFlags = HideFlags.HideAndDontSave };
            // Keep the temporary camera away from the scene's objects, on its own layer.
            root.transform.position = new Vector3(0, 0, -10000);
            var camera = root.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = photo.height / 2f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.gray;
            camera.cullingMask = 1 << ExportLayer;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = target;

            var canvasObject = Child("Export canvas", root.transform, typeof(Canvas));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1;
            canvas.pixelPerfect = true;
            var image = Child("Covered photo", canvas.transform, typeof(RawImage)).GetComponent<RawImage>();
            Stretch(image.rectTransform, 0);
            image.texture = background;
            image.raycastTarget = false;

            corners = RoundedCorners();
            cardSprite = Sprite.Create(corners, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f),
                100, 0, SpriteMeshType.FullRect, new Vector4(8, 8, 8, 8));
            var card = Child("Description card", canvas.transform, typeof(Image)).GetComponent<Image>();
            card.sprite = cardSprite;
            card.type = Image.Type.Sliced;
            card.raycastTarget = false;
            var cardRect = card.rectTransform;
            cardRect.anchorMin = cardRect.anchorMax = Vector2.zero;
            float cardWidth = Mathf.Min(photo.width, Mathf.Clamp(bounds.width, 240, 400));
            float cardHeight = Mathf.Min(photo.height, 120);
            cardRect.sizeDelta = new Vector2(cardWidth, cardHeight);
            cardRect.anchoredPosition = new Vector2(
                Mathf.Clamp(bounds.x + bounds.width / 2f, cardWidth / 2f, photo.width - cardWidth / 2f),
                Mathf.Clamp(bounds.y + bounds.height / 2f, cardHeight / 2f, photo.height - cardHeight / 2f));
            var text = Child("Reviewed description", card.transform, typeof(Text)).GetComponent<Text>();
            Stretch(text.rectTransform, Mathf.Min(12, Mathf.Min(cardWidth, cardHeight) / 4f));
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = reviewedDescription.Trim();
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleCenter;
            text.supportRichText = false;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = 26;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;

            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            output = new Texture2D(photo.width, photo.height, TextureFormat.RGBA32, false);
            output.name = "Protected image with description";
            output.ReadPixels(new Rect(0, 0, photo.width, photo.height), 0, 0, false);
            output.Apply();
            return output;
        }
        catch
        {
            Release(output);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            // In Play mode Destroy is deferred, so disable before releasing its textures.
            if (root != null)
            {
                root.SetActive(false);
                root.GetComponent<Camera>().targetTexture = null;
            }
            Release(root);
            Release(cardSprite);
            Release(corners);
            Release(background);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    static GameObject Child(string name, Transform parent, params Type[] components)
    {
        var child = new GameObject(name, typeof(RectTransform));
        child.layer = ExportLayer;
        child.transform.SetParent(parent, false);
        foreach (var component in components) child.AddComponent(component);
        return child;
    }

    static void Stretch(RectTransform rect, float padding)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.one * padding;
        rect.offsetMax = -Vector2.one * padding;
    }

    static Texture2D RoundedCorners()
    {
        var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[32 * 32];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            float dx = Mathf.Max(8 - (x + 0.5f), x + 0.5f - 24, 0);
            float dy = Mathf.Max(8 - (y + 0.5f), y + 0.5f - 24, 0);
            pixels[y * 32 + x] = new Color32(255, 255, 255, (byte)(dx * dx + dy * dy <= 64 ? 255 : 0));
        }
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    static void Release(UnityEngine.Object resource)
    {
        if (resource == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
        else UnityEngine.Object.DestroyImmediate(resource);
    }
}
