using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// A ScreenSpaceOverlay canvas does not show in the headset. On the Quest this moves it into the
// world and clicks its buttons with the right controller.
// In the Editor the canvas stays flat and the EventSystem handles the mouse.
// Like Horizon OS panels, a bar under the panel moves it: point at the bar, hold the trigger, and
// the panel follows the ray while turning to face the user.
[RequireComponent(typeof(Canvas))]
public sealed class QuestCanvasPointer : MonoBehaviour
{
    Transform pointer;
    RectTransform cursor;
    Canvas canvas;
    GraphicRaycaster raycaster;
    Transform head;
    Image grabBar;
    bool dragging;
    float grabDistance;
    Vector3 grabLocal; // grab point in panel space, so the panel does not jump to the ray

    static readonly Color BarIdle = new(1f, 1f, 1f, 0.35f);
    static readonly Color BarHover = new(1f, 1f, 1f, 0.9f);

    IEnumerator Start()
    {
        if (Application.isEditor)
        {
            enabled = false;
            yield break;
        }
        foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            if (camera.stereoTargetEye == StereoTargetEyeMask.None)
                camera.enabled = false; // the Editor's preview camera
        yield return null; // let the head pose settle before placing the panel

        canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        raycaster = GetComponent<GraphicRaycaster>();
        var scaler = GetComponent<CanvasScaler>();
        if (scaler) scaler.dynamicPixelsPerUnit = 3;
        var rect = (RectTransform)transform;
        rect.sizeDelta = new Vector2(1200, 800);
        rect.localScale = Vector3.one * 0.001f; // 1 px = 1 mm
        head = Camera.main.transform;
        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (forward == Vector3.zero) forward = Vector3.forward;
        rect.SetPositionAndRotation(head.position + forward * 1.4f, Quaternion.LookRotation(forward));

        MakeImage("Background", new Color(0.1f, 0.1f, 0.12f, 0.92f), rect.sizeDelta).transform.SetAsFirstSibling();
        grabBar = MakeImage("GrabBar", BarIdle, new Vector2(240, 24));
        grabBar.rectTransform.anchoredPosition = new Vector2(0, -rect.sizeDelta.y / 2 - 40);
        cursor = MakeImage("Cursor", Color.cyan, new Vector2(16, 16)).rectTransform;
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig) pointer = rig.rightControllerAnchor;
        else Debug.LogError("Quest canvas: the scene has no OVRCameraRig.", this);
    }

    void Update()
    {
        if (!pointer) return;
        var ray = new Ray(pointer.position, pointer.forward);
        if (dragging)
        {
            Drag(ray);
            return;
        }
        var plane = new Plane(-transform.forward, transform.position);
        bool onPanel = plane.Raycast(ray, out float distance);
        cursor.gameObject.SetActive(onPanel);
        if (!onPanel) return;
        var hit = ray.GetPoint(distance);
        cursor.position = hit;
        bool overBar = grabBar.rectTransform.rect.Contains(grabBar.rectTransform.InverseTransformPoint(hit));
        grabBar.color = overBar ? BarHover : BarIdle;
        if (!OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) return;
        if (overBar)
        {
            dragging = true;
            grabDistance = distance;
            grabLocal = transform.InverseTransformPoint(hit);
            return;
        }
        var eventSystem = EventSystem.current;
        if (!eventSystem || !raycaster || !canvas.worldCamera) return;
        var eventData = new PointerEventData(eventSystem)
        {
            position = canvas.worldCamera.WorldToScreenPoint(hit),
            button = PointerEventData.InputButton.Left
        };
        var hits = new List<RaycastResult>();
        raycaster.Raycast(eventData, hits);
        if (hits.Count == 0) return;

        // Dispatch to the topmost UI target so buttons and image click handlers both work.
        eventData.pointerCurrentRaycast = eventData.pointerPressRaycast = hits[0];
        eventData.pointerPress = eventData.pointerClick = hits[0].gameObject;
        ExecuteEvents.Execute(hits[0].gameObject, eventData, ExecuteEvents.pointerClickHandler);
    }

    void Drag(Ray ray)
    {
        if (!OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            dragging = false;
            return;
        }
        var target = ray.GetPoint(grabDistance);
        var facing = Vector3.ProjectOnPlane(target - head.position, Vector3.up);
        var rotation = facing.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(facing) : transform.rotation;
        var offset = rotation * Vector3.Scale(grabLocal, transform.lossyScale);
        transform.SetPositionAndRotation(target - offset, rotation);
        cursor.position = target;
    }

    Image MakeImage(string name, Color color, Vector2 size)
    {
        var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        image.rectTransform.SetParent(transform, false);
        image.rectTransform.sizeDelta = size;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }
}
