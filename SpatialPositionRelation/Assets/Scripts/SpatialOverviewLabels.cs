using UnityEngine;

// 只在总览中绘制屏幕空间标签，锚点跟随参考模型当前可见轮廓。
internal sealed class SpatialOverviewLabels
{
    private SpatialCraftAssembly target, chaser;
    private Renderer[] targetParts, chaserParts;

    public void Bind(SpatialCraftAssembly nextTarget, SpatialCraftAssembly nextChaser)
    {
        if (target != nextTarget)
        {
            target = nextTarget;
            targetParts = target.GetComponentsInChildren<MeshRenderer>();
        }
        if (chaser != nextChaser)
        {
            chaser = nextChaser;
            chaserParts = chaser.GetComponentsInChildren<MeshRenderer>();
        }
    }

    public void Draw(Camera camera, Rect viewport, Rect controls, GUIStyle style, Vector3 sun,
        SpatialSensorView[] sensors)
    {
        Rect first = Place(camera, viewport, controls, targetParts, out Vector2 firstTip);
        Rect second = Place(camera, viewport, controls, chaserParts, out Vector2 secondTip);
        if (first.width > 0 && second.width > 0 && first.Overlaps(second))
        {
            float left = Mathf.Clamp((first.center.x + second.center.x) / 2 - 124, 4, viewport.xMax - 252);
            bool targetLeft = firstTip.x <= secondTip.x;
            first.x = left + (targetLeft ? 0 : 128);
            second.x = left + (targetLeft ? 128 : 0);
            first.y = second.y = Mathf.Min(first.y, second.y);
            if (first.Overlaps(controls) || second.Overlaps(controls)) first.y = second.y = controls.yMax + 5;
        }
        DrawRole(first, firstTip, "目标航天器", new Color(0.54f, 0.93f, 1f), style);
        DrawRole(second, secondTip, "追踪航天器", new Color(0.82f, 1f, 0.63f), style);
        if (Project(camera, sun, out Vector2 solarPixel) && viewport.Contains(solarPixel) && !controls.Contains(solarPixel))
        {
            GUI.color = new Color(1f, 0.83f, 0.52f);
            GUI.Box(new Rect(Mathf.Clamp(solarPixel.x + 11, 4, viewport.xMax - 143), solarPixel.y - 28, 139, 26),
                "太阳（方向示意）", style);
            GUI.color = Color.white;
        }
        foreach (SpatialSensorView sensor in sensors)
        {
            if (sensor == null || !sensor.isActiveAndEnabled || !sensor.ShowRays || sensor.sensorCamera == null) continue;
            if (!Project(camera, sensor.sensorCamera.transform.position, out Vector2 lens)
                || !viewport.Contains(lens) || controls.Contains(lens)) continue;
            GUI.color = new Color(0.35f, 1f, 0.68f);
            GUI.DrawTexture(new Rect(lens.x - 3, lens.y - 3, 6, 6), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }

    private static bool Project(Camera camera, Vector3 world, out Vector2 pixel)
    {
        Vector3 screen = camera.WorldToScreenPoint(world);
        pixel = new Vector2(screen.x, Screen.height - screen.y);
        return screen.z > camera.nearClipPlane;
    }

    private static Rect Place(Camera camera, Rect viewport, Rect controls, Renderer[] parts, out Vector2 tip)
    {
        tip = default;
        if (parts == null || parts.Length == 0 || viewport.width < 260) return default;
        Bounds bounds = parts[0].bounds;
        foreach (Renderer part in parts)
            if (part != null && part.enabled && part.gameObject.activeInHierarchy) bounds.Encapsulate(part.bounds);
        if (!Project(camera, bounds.center, out tip) || !viewport.Contains(tip)) return default;
        Vector3 d = bounds.extents;
        for (int i = 0; i < 8; ++i)
        {
            Vector3 corner = bounds.center + new Vector3((i & 1) == 0 ? -d.x : d.x,
                (i & 2) == 0 ? -d.y : d.y, (i & 4) == 0 ? -d.z : d.z);
            if (Project(camera, corner, out Vector2 point)) tip.y = Mathf.Min(tip.y, point.y);
        }
        Rect box = new Rect(Mathf.Clamp(tip.x - 60, 4, viewport.xMax - 124), Mathf.Max(4, tip.y - 33), 120, 26);
        if (box.Overlaps(controls)) box.y = controls.yMax + 5;
        return box;
    }

    private static void DrawRole(Rect box, Vector2 tip, string text, Color color, GUIStyle style)
    {
        if (box.width <= 0) return;
        GUI.color = color;
        Vector2 from = new Vector2(box.center.x, box.yMax);
        if (tip.y > from.y)
        {
            Vector2 delta = tip - from;
            Matrix4x4 matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, from);
            GUI.DrawTexture(new Rect(from.x, from.y, delta.magnitude, 2), Texture2D.whiteTexture);
            GUI.matrix = matrix;
        }
        GUI.Box(box, text, style);
        GUI.color = Color.white;
    }
}
