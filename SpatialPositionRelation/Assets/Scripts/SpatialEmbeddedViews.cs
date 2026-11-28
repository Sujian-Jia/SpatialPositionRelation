using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 把总览和选中的传感器相机排进同一个 Unity 窗口，处理鼠标和视图模式。
/// 多个 Camera 渲染到同一屏幕的不同矩形；这些显示设置与传感器的几何计算分别维护。
/// </summary>
public sealed class SpatialEmbeddedViews : MonoBehaviour
{
    public Camera overviewCamera;
    public SpatialSensorView[] sensorViews = Array.Empty<SpatialSensorView>();
    public SpatialTcpBridge tcpBridge;

    private readonly HashSet<string> visibleSensorIds = new HashSet<string>();
    private readonly List<Rect> sensorSlots = new List<Rect>();
    private readonly List<SpatialSensorView> slotSensors = new List<SpatialSensorView>();
    private readonly List<SpatialSensorView> visibleSensors = new List<SpatialSensorView>();
    private Vector3 focus;
    private Vector3 overviewOffset = new Vector3(0, 17, -20);
    private float overviewFov = 56;
    public Vector3 SunDisplayPoint { get; set; }
    public bool ShowSunVector { get; set; }
    private readonly SpatialOverviewLabels labels = new SpatialOverviewLabels();
    private Camera backdrop;

    public void SetCraftRoles(SpatialCraftAssembly target, SpatialCraftAssembly chaser) => labels.Bind(target, chaser);
    private Vector3 pan;
    private float zoom = 1;
    private int dragButton = -1;
    private float overviewYaw;
    private float overviewPitch;
    private bool dragEnabled;
    private string expandedSensorId;
    private Rect overviewSlot;
    private bool guiStylesReady;
    private GUIStyle controlLabel, controlToggle, panelLabel;

    private void PrepareGuiStyles()
    {
        if (guiStylesReady) return;
        controlLabel = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        controlToggle = new GUIStyle(GUI.skin.toggle) { fontSize = 16 };
        panelLabel = new GUIStyle(GUI.skin.box) { fontSize = 16 };
        guiStylesReady = true;
    }

    /// <summary>
    /// 首次绘制前关掉各传感器相机并建一台背景相机，随后由布局决定开启哪些机位。
    /// 这是 Unity 的 Start 钩子；背景相机渲染深度更低，剔除掩码 0 表示只清背景，不画场景物体。
    /// </summary>
    private void Start()
    {
        if (sensorViews == null) sensorViews = Array.Empty<SpatialSensorView>();
        foreach (SpatialSensorView view in sensorViews)
        {
            if (view == null || view.sensorCamera == null) continue;
            view.sensorCamera.enabled = false;
        }
        backdrop = gameObject.AddComponent<Camera>();
        backdrop.depth = overviewCamera.depth - 1;
        backdrop.clearFlags = CameraClearFlags.SolidColor;
        backdrop.cullingMask = 0;
        backdrop.backgroundColor = new Color(0.012f, 0.02f, 0.032f);
        ApplyOverviewPose();
    }

    public void ApplySettings(string mode, string[] sensorIds)
    {
        dragEnabled = mode == "drag";
        if (!dragEnabled) dragButton = -1;
        visibleSensorIds.Clear();
        if (sensorIds != null)
        {
            foreach (string id in sensorIds)
                if (!string.IsNullOrEmpty(id)) visibleSensorIds.Add(id);
        }
        else AddInitialVisibleSensors();
    }

    public void SetOverviewPose(Vector3 target, Vector3 offset, float fieldOfView)
    {
        focus = target;
        overviewOffset = offset;
        overviewFov = fieldOfView;
        ApplyOverviewPose();
    }

    /// <summary>
    /// 本帧位姿就绪后重新排相机画面：左边总览、右边按显示机位等分，或单独放大一个机位。
    /// LateUpdate 在 Update 之后；窗口像素宽高至少取 1，右列取窗口宽 32% 与高度 72% 的较小值。
    /// 高度先转 float 再除机位数，保留小数像素。单独放大时按槽位宽高比调整垂直视场，避免把两轴各自拉伸。
    /// </summary>
    private void LateUpdate()
    {
        if (overviewCamera == null) return;
        int width = Mathf.Max(1, Screen.width);
        int height = Mathf.Max(1, Screen.height);
        visibleSensors.Clear();
        bool expandedAvailable = false;
        foreach (SpatialSensorView view in sensorViews)
        {
            if (view == null || view.sensorCamera == null) continue;
            if (!view.isActiveAndEnabled || !visibleSensorIds.Contains(view.sensorId))
            {
                view.sensorCamera.enabled = false;
                continue;
            }
            visibleSensors.Add(view);
            expandedAvailable |= expandedSensorId == view.sensorId;
        }
        if (!expandedAvailable) expandedSensorId = null;
        float railWidth = visibleSensors.Count == 0 ? 0 : Mathf.Min(width * 0.32f, height * 0.72f);
        float overviewWidth = width - railWidth;
        overviewSlot = new Rect(0, 0, overviewWidth, height);
        overviewCamera.enabled = expandedSensorId == null;
        if (expandedSensorId == null)
        {
            overviewCamera.rect = ToCameraRect(overviewSlot, width, height);
            overviewCamera.aspect = overviewSlot.width / height;
        }

        sensorSlots.Clear();
        slotSensors.Clear();
        float slotHeight = visibleSensors.Count == 0 ? 0 : (float)height / visibleSensors.Count;
        for (int i = 0; i < visibleSensors.Count; i++)
        {
            SpatialSensorView view = visibleSensors[i];
            if (expandedSensorId != null && expandedSensorId != view.sensorId)
            {
                view.sensorCamera.enabled = false;
                continue;
            }
            Rect slot = expandedSensorId != null ? new Rect(0, 0, width, height)
                : new Rect(overviewWidth, i * slotHeight, railWidth, slotHeight);
            sensorSlots.Add(slot);
            slotSensors.Add(view);
            // 画面等比缩放，保留配置规定的横纵视场，否则子视图会显示计算视场之外的内容。
            float aspect = view.OpticalAspect;
            float verticalFov = view.SensorVerticalFov;
            if (expandedSensorId != null)
            {
                float slotAspect = slot.width / slot.height;
                // 独占时等比铺满，只裁去视场超出画布的一维。不会拉伸，也不会显示配置视场外的内容。
                float verticalHalfTangent = Mathf.Tan(verticalFov * Mathf.Deg2Rad * 0.5f);
                view.sensorCamera.fieldOfView = 2f * Mathf.Atan(verticalHalfTangent * Mathf.Min(1, aspect / slotAspect)) * Mathf.Rad2Deg;
                view.sensorCamera.rect = ToCameraRect(slot, width, height);
                view.sensorCamera.aspect = slotAspect;
            }
            else
            {
                float contentWidth = Mathf.Min(slot.width, slot.height * aspect);
                float contentHeight = contentWidth / aspect;
                Rect content = new Rect(slot.center.x - contentWidth / 2, slot.center.y - contentHeight / 2,
                    contentWidth, contentHeight);
                view.sensorCamera.fieldOfView = verticalFov;
                view.sensorCamera.rect = ToCameraRect(content, width, height);
                view.sensorCamera.aspect = aspect;
            }
            view.sensorCamera.enabled = true;
        }
        if (backdrop != null) backdrop.enabled = visibleSensors.Count > 0;
        ApplyOverviewPose();
    }

    /// <summary>
    /// 窗口失焦时结束本次相机拖拽，避免回来后继续用旧的鼠标按钮状态。
    /// Unity 在应用焦点变化时调用这个钩子；−1 表示未记录拖动按键。
    /// </summary>
    private void OnApplicationFocus(bool focused)
    {
        if (!focused) dragButton = -1;
    }

    /// <summary>
    /// 绘制切换开关和机位标签，并把总览区鼠标输入换成相机平移、旋转或缩放。
    /// 这是 Unity 即时模式界面回调，可能同一帧收到多种事件；Event.current 表示当前调用对应的事件。
    /// </summary>
    private void OnGUI()
    {
        if (overviewCamera == null) return;
        PrepareGuiStyles();
        Event current = Event.current;
        Vector2 pointer = current.mousePosition;
        Rect controls = new Rect(8, 8, Mathf.Min(340, overviewSlot.width - 16), 159);
        bool inOverview = expandedSensorId == null && overviewSlot.Contains(pointer) && !controls.Contains(pointer);
        if (current.type == EventType.MouseDown)
        {
            if (current.button == 0 && current.clickCount >= 2)
            {
                if (expandedSensorId != null)
                {
                    expandedSensorId = null;
                    current.Use();
                }
                else for (int i = 0; i < sensorSlots.Count; i++)
                {
                    if (!sensorSlots[i].Contains(pointer)) continue;
                    expandedSensorId = slotSensors[i].sensorId;
                    current.Use();
                    break;
                }
            }
            if (current.type == EventType.MouseDown && dragEnabled && inOverview
                && (current.button == 0 || current.button == 2))
            {
                dragButton = current.button;
                current.Use();
            }
        }
        else if (current.type == EventType.MouseDrag && current.button == dragButton)
        {
            if (dragButton == 0)
            {
                overviewYaw += current.delta.x * 0.32f;
                overviewPitch = Mathf.Clamp(overviewPitch - current.delta.y * 0.32f, -75, 75);
            }
            else
            {
                Transform cameraTransform = overviewCamera.transform;
                float scale = overviewOffset.magnitude * zoom * 0.0018f;
                pan += (cameraTransform.up * current.delta.y - cameraTransform.right * current.delta.x) * scale;
            }
            ApplyOverviewPose();
            current.Use();
        }
        else if (current.type == EventType.MouseUp && current.button == dragButton)
        {
            dragButton = -1;
            current.Use();
        }
        else if (current.type == EventType.ScrollWheel && dragEnabled && inOverview)
        {
            zoom = Mathf.Clamp(zoom * Mathf.Exp(current.delta.y * 0.12f), 0.15f, 6);
            ApplyOverviewPose();
            current.Use();
        }

        if (expandedSensorId != null)
        {
            GUI.color = Color.white;
            string cameraLabel = slotSensors.Count == 1 ? SensorName(slotSensors[0]) + " · " : "";
            GUI.Box(new Rect(8, 8, 380, 26), cameraLabel + "双击还原 · 等比铺满", panelLabel);
            return;
        }

        Color previous = GUI.color;
        GUI.color = Color.white;
        GUI.Box(controls, GUIContent.none);
        GUI.Label(new Rect(16, 12, 320, 22), "总览视角", controlLabel);
        bool fixedView = !dragEnabled;
        if (GUI.Toggle(new Rect(16, 36, 110, 24), fixedView, "固定视角", controlToggle) && dragEnabled)
        {
            dragEnabled = false;
            dragButton = -1;
            SendSettings();
        }
        bool movableView = dragEnabled;
        if (GUI.Toggle(new Rect(132, 36, 150, 24), movableView, "可拖动视角", controlToggle) && !dragEnabled)
        {
            dragEnabled = true;
            SendSettings();
        }

        GUI.Label(new Rect(16, 62, 320, 20), "相机画面", controlLabel);
        int columns = 2;
        float itemWidth = (controls.width - 20) / columns;
        for (int i = 0; i < sensorViews.Length; i++)
        {
            SpatialSensorView view = sensorViews[i];
            if (view == null || view.sensorCamera == null) continue;
            int column = i % columns;
            int row = i / columns;
            Rect toggleRect = new Rect(16 + column * itemWidth, 84 + row * 22, itemWidth, 22);
            bool selected = visibleSensorIds.Contains(view.sensorId);
            bool next = GUI.Toggle(toggleRect, selected, SensorName(view), controlToggle);
            if (next != selected)
            {
                if (next) visibleSensorIds.Add(view.sensorId);
                else visibleSensorIds.Remove(view.sensorId);
                SendSettings();
            }
        }
        GUI.color = previous;

        if (dragEnabled)
            GUI.Box(new Rect(8, Screen.height - 30, 320, 22), "左键拖动环绕 / 中键平移 / 滚轮缩放", panelLabel);
        if (current.type == EventType.Repaint)
            labels.Draw(overviewCamera, overviewSlot, controls, panelLabel, SunDisplayPoint, ShowSunVector, sensorViews);

        for (int i = 0; i < sensorSlots.Count; i++)
        {
            Rect slot = sensorSlots[i];
            Color border = new Color(0.28f, 0.78f, 0.72f, 0.95f);
            DrawBorder(slot, 2, border);
            Color header = new Color(0.02f, 0.04f, 0.06f, 0.78f);
            GUI.color = header;
            GUI.DrawTexture(new Rect(slot.x, slot.y, slot.width, 24), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(slot.x + 6, slot.y + 4, slot.width - 12, 20), SensorName(slotSensors[i]), controlLabel);
        }
    }

    /// <summary>
    /// 用焦点和偏移放置总览镜头；拖拽模式还叠加用户的平移、旋转和缩放。
    /// 相机位置是焦点加偏移，LookAt 再让镜头朝焦点；这些只影响观察视角。
    /// </summary>
    private void ApplyOverviewPose()
    {
        if (overviewCamera == null) return;
        Vector3 lookAt = focus;
        Vector3 offset = overviewOffset;
        if (dragEnabled)
        {
            lookAt += pan;
            offset = Quaternion.Euler(overviewPitch, overviewYaw, 0) * overviewOffset * zoom;
        }
        overviewCamera.transform.position = lookAt + offset;
        overviewCamera.transform.LookAt(lookAt);
        overviewCamera.fieldOfView = overviewFov;
    }

    /// <summary>
    /// 把左上为原点的像素矩形换成相机使用的左下原点、0 到 1 的归一化矩形。
    /// 用矩形底边像素坐标除以高度，再用 1 减结果，完成 Y 方向翻转。
    /// </summary>
    private static Rect ToCameraRect(Rect screenRect, int width, int height)
    {
        return new Rect(screenRect.x / width, 1f - (screenRect.y + screenRect.height) / height,
                        screenRect.width / width, screenRect.height / height);
    }

    private static void DrawBorder(Rect rect, float thickness, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static string SensorName(SpatialSensorView view)
    {
        string id = view.sensorId ?? string.Empty;
        if (id == "CRDS-PORT") return "CRDS";
        if (id == "TV-DOCK-WIDE") return "对接相机 A";
        if (id == "TV-DOCK-NARROW") return "对接相机 B";
        if (id == "TV-ARM-WRIST") return "腕部相机 A";
        if (id == "TV-ARM-WRIST-B") return "腕部相机 B";
        if (id == "TV-ARM-ELBOW") return "肘部相机";
        return id;
    }
    private void AddInitialVisibleSensors()
    {
        for (int i = 0; i < sensorViews.Length && visibleSensorIds.Count < 2; i++)
            if (sensorViews[i] != null && sensorViews[i].sensorCamera != null)
                visibleSensorIds.Add(sensorViews[i].sensorId);
    }

    /// <summary>
    /// 把用户选的总览模式和显示机位送到 Qt，由 Qt 保存这两项设置。
    /// 消息类型为 uiEvent，事件名为 viewSettings；序号这里取 UTC 的 Ticks，与仿真的遥测计数分开。
    /// </summary>
    private void SendSettings()
    {
        if (tcpBridge == null) return;
        List<string> ids = new List<string>();
        foreach (SpatialSensorView view in sensorViews)
            if (view != null && visibleSensorIds.Contains(view.sensorId)) ids.Add(view.sensorId);
        tcpBridge.SendJson(JsonUtility.ToJson(new SpatialUiEventMessage {
            seq = DateTime.UtcNow.Ticks,
            utc = DateTime.UtcNow.ToString("o"),
            name = "viewSettings",
            overviewMode = dragEnabled ? "drag" : "fixed",
            displaySensorIds = ids.ToArray()
        }));
    }
}
