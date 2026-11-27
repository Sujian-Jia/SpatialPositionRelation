using UnityEngine;

/// <summary>
/// 单独实现传感器视锥线框的绘制；几何计算和数据统计在同类的另一部分。
/// partial 将类拆进多个文件，但它们仍属于同一个 C# 类型。
/// </summary>
public abstract partial class SpatialSensorViewBase
{
    public bool ShowRays { get; set; }
    public float SensorVerticalFov => configuration == null ? 32 : Mathf.Clamp(configuration.verticalFovDeg, 1, 179);
    public float OpticalAspect => configuration == null ? 1 :
        Mathf.Tan(Mathf.Clamp(configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f)
        / Mathf.Tan(Mathf.Clamp(configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);

    /// <summary>
    /// 在 Unity 每帧稍晚阶段重画视锥线，跟上相机或父物体本帧的位置变化。
    /// LateUpdate 是生命周期钩子，通常在 Update 之后执行，不代表独立线程。
    /// </summary>
    private void LateUpdate()
    {
        DrawFrustum();
    }

    /// <summary>
    /// 用相机位置、方向和水平/垂直视场生成四条视锥边线。
    /// 距离无穷时用 18 米，绘制长度再限制在 4 至 50 米；半宽 x=L·tan(水平半视场)，半高 y=L·tan(垂直半视场)。
    /// 不显示射线或缺配置时禁用线；配置有效时四个角点在相机本地的 z=L 平面上，再旋转和平移到世界坐标。
    /// 遥测的 frustumVertex 读第一条线的起点（即视锥原点），不是远端角点。
    /// </summary>
    public void DrawFrustum()
    {
        float distance = frustumDistance;
        if (frustumLines == null || frustumLines.Length < 4) return;
        if (!ShowRays || configuration == null)
        {
            foreach (LineRenderer line in frustumLines)
                if (line != null) line.enabled = false;
            return;
        }
        float length = Mathf.Clamp(float.IsInfinity(distance) ? 18 : distance, 4, 50);
        float x = length * Mathf.Tan(configuration.horizontalFovDeg * Mathf.Deg2Rad * 0.5f);
        float y = length * Mathf.Tan(configuration.verticalFovDeg * Mathf.Deg2Rad * 0.5f);
        Transform opticalCenter = sensorCamera != null ? sensorCamera.transform : transform;
        Vector3 origin = opticalCenter.position;
        Vector3[] corners = {
            origin + opticalCenter.rotation * new Vector3(-x, -y, length), origin + opticalCenter.rotation * new Vector3(x, -y, length),
            origin + opticalCenter.rotation * new Vector3(x, y, length), origin + opticalCenter.rotation * new Vector3(-x, y, length)};
        for (int i = 0; i < 4; i++)
        {
            if (frustumLines[i] == null) continue;
            frustumLines[i].SetPosition(0, origin);
            frustumLines[i].SetPosition(1, corners[i]);
            frustumLines[i].enabled = true;
        }
        if (latest != null)
        {
            latest.frustumVertex = frustumLines[0] != null ? frustumLines[0].GetPosition(0) : origin;
            latest.frustumOriginErrorM = Vector3.Distance(latest.frustumVertex, origin);
        }
    }

}
