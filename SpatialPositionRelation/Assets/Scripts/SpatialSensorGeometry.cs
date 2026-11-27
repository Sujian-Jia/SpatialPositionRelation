using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 按传感器位姿、视场和物理射线估算目标可见程度与受光程度。
/// partial 表示同一个类拆在多个源文件；MonoBehaviour 可由 Unity 放进场景并随生命周期运行。
/// </summary>
public abstract partial class SpatialSensorViewBase : MonoBehaviour
{
    /// <summary>
    /// 描述目标表面投影近似算法的离散采样参数。
    /// 每个面朝传感器的盒面切成 4×4 个小格；最多挑 8 个 Renderer；太阳圆面另在中心外取 8 点；日面的角半径常数单位为度。
    /// 这些是计算近似用的固定取值，不是场景距离。
    /// </summary>
    private const int ProjectedAreaGridResolution = 4;
    private const int ProjectedAreaMaxRenderers = 8;
    private const int SunDiskRingSamples = 8;
    private const float SunAngularRadiusDeg = 0.2666f;

    public string sensorId;
    public Camera sensorCamera;
    public LineRenderer[] frustumLines = Array.Empty<LineRenderer>();
    [NonSerialized] public SpatialSensorConfig configuration;
    [NonSerialized] public SpatialSensorTelemetry latest = new SpatialSensorTelemetry();
    private readonly Dictionary<string, SpatialBlockerData> blockers = new Dictionary<string, SpatialBlockerData>();

    private Transform installationPoint;
    private float frustumDistance = 18;

    /// <summary>
    /// 把配置文件里的传感器安装位置、角度和视场套用到当前场景对象。
    /// Transform 是场景物体的位置、旋转和父子层级；localPosition 和 localRotation 都相对父物体。
    /// 位置输入按米解释。模型安装点存在时通过父空间变换取得偏移，抵消模型层级的缩放，保留米制偏移。
    /// 相机垂直视场限制到 1° 至 179°；宽高比另由配置角的半角正切求比，分母至少为 0.001。
    /// </summary>
    public void ApplyConfiguration(SpatialSensorConfig config, Transform mount, Transform installation = null)
    {
        if (config == null) return;
        configuration = config;
        sensorId = config.id;
        Transform parent = mount != null ? mount : transform.parent;
        if (parent != null && transform.parent != parent) transform.SetParent(parent, false);
        float[] position = config.localPositionM ?? Array.Empty<float>();
        float[] euler = config.localEulerDeg ?? Array.Empty<float>();
        installationPoint = installation;
        Vector3 offsetM = new Vector3(Value(position, 0), Value(position, 1), Value(position, 2));
        // 安装点随机构继承姿态。偏移在父坐标轴中用米表达，避免被 FBX 的比例再次缩小。
        transform.localPosition = installation != null
            ? parent.InverseTransformVector(parent.rotation * offsetM) : offsetM;
        transform.localRotation = Quaternion.Euler(Value(euler, 0), Value(euler, 1), Value(euler, 2));
        if (sensorCamera != null)
        {
            sensorCamera.enabled = config.enabled;
            sensorCamera.fieldOfView = Mathf.Clamp(config.verticalFovDeg, 1, 179);
            sensorCamera.aspect = Mathf.Tan(config.horizontalFovDeg * Mathf.Deg2Rad * 0.5f)
                                  / Mathf.Max(0.001f, Mathf.Tan(config.verticalFovDeg * Mathf.Deg2Rad * 0.5f));
        }
    }

    /// <summary>
    /// 汇总一个传感器当前的目标可见、遮挡和太阳照射数据。
    /// 输入点逐个按视场锥和射线判遮挡；后续投影面积和太阳可见度用各自的采样近似。
    /// 可见率=visible/inFov（无入场点时设 0）；Qt 表格另以 visible/total 显示点数比。归一化光照=地影时 0，否则 max(0, 入口法线·向日单位向量)×日面可见率。配置关闭或缺目标/采样时保留 OutOfFov 初始状态。
    /// </summary>
    public SpatialSensorTelemetry Calculate(Transform target, Vector3[] samplePoints, Vector3 sunDirection,
                                            bool earthShadow, Quaternion bodyRotation, long sequence,
                                            Transform[] adjustmentJoints = null,
                                            Renderer[] targetRenderers = null, string targetId = "ACTIVE_TARGET",
                                            string targetCraftId = "", bool targetResolved = true,
                                            Transform targetFeatureToIgnore = null, string pointKind = "marker")
    {
        SpatialSensorTelemetry result = new SpatialSensorTelemetry {
            id = sensorId,
            targetId = targetId,
            targetCraftId = targetCraftId,
            targetResolved = targetResolved,
            status = "OutOfFov",
            recommendation = "目标在视场外"
        };
        blockers.Clear();
        if (configuration == null || !configuration.enabled || target == null || samplePoints == null)
            return result;

        result.totalPointCount = samplePoints.Length;
        result.pointKind = pointKind;
        MeasureMarkerVisibility(samplePoints, targetFeatureToIgnore, blockers,
            out result.inFovCount, out result.visibleCount, out float minimumDistance);
        int blockedCount = result.inFovCount - result.visibleCount;

        result.visibleRatio = result.inFovCount == 0 ? 0 : (float)result.visibleCount / result.inFovCount;
        MeasureProjectedTargetArea(targetRenderers, result);
        if (result.inFovCount > 0)
        {
            if (blockedCount == 0) { result.status = "Visible"; result.recommendation = "当前视线正常"; }
            else if (result.visibleCount == 0) { result.status = "FullyBlocked"; result.recommendation = "调整航天器姿态或机械臂避开遮挡"; }
            else { result.status = "PartiallyBlocked"; result.recommendation = "调整姿态或机构以提高目标可见率"; }
        }
        result.blockers = new List<SpatialBlockerData>(blockers.Values).ToArray();

        float cosine = Mathf.Clamp(Vector3.Dot(transform.forward.normalized, sunDirection.normalized), -1, 1);
        result.sunIncidenceDeg = Mathf.Acos(cosine) * Mathf.Rad2Deg;
        result.incidenceCosine = cosine;
        result.illuminationAvailable = sunDirection.sqrMagnitude > 0.5f;
        MeasureSunVisibility(sunDirection, result);
        // 入口法线指向镜头外，拍摄视场范围与直射辐照度的半空间是两套判据。
        result.sunVisibleFraction = 1f - result.sunOcclusionRatio;
        result.normalizedIllumination = earthShadow ? 0 : Mathf.Max(0, cosine) * result.sunVisibleFraction;
        result.illuminationReason = earthShadow ? "earthShadow" : cosine <= 0 ? "behindEntrance"
            : result.sunVisibleFraction <= 0 ? "structureShadow" : "direct";
        result.warning = configuration.warningEnabled && result.normalizedIllumination >= configuration.warningThreshold;
        if (result.warning)
            result.recommendation = FindAttitudeRecommendation(sunDirection, bodyRotation,
                                                                result.normalizedIllumination, adjustmentJoints);
        frustumDistance = minimumDistance;
        result.installationPoint = installationPoint != null ? installationPoint.position : transform.position;
        result.configuredOpticalCenter = transform.position;
        result.installationPath = installationPoint != null ? installationPoint.parent.name + "/" + installationPoint.name : "";
        result.opticalCenter = sensorCamera != null ? sensorCamera.transform.position : transform.position;
        result.installationToCameraErrorM = Vector3.Distance(result.installationPoint, result.opticalCenter);
        return result;
    }

    /// <summary>
    /// 把每个样本换到传感器本地坐标，先判断是否落进视锥，再向目标发射遮挡射线。
    /// 相机朝本地 +Z；边界公式是 z&gt;0、|x|≤z·tan(水平半视场)、|y|≤z·tan(垂直半视场)。
    /// 离原点小于 0.001 米时直接视为可见，且不更新最近距离；无合格距离时保留正无穷，画线代码另给默认值。
    /// </summary>
    internal void MeasureMarkerVisibility(Vector3[] samplePoints, Transform targetFeatureToIgnore,
        Dictionary<string, SpatialBlockerData> identities, out int inFovCount, out int visibleCount, out float minimumDistance)
    {
        float hTangent = Mathf.Tan(Mathf.Clamp(configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);
        float vTangent = Mathf.Tan(Mathf.Clamp(configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);
        minimumDistance = float.PositiveInfinity;
        inFovCount = 0;
        visibleCount = 0;
        foreach (Vector3 point in samplePoints)
        {
            Vector3 local = transform.InverseTransformPoint(point);
            if (local.z <= 0 || Mathf.Abs(local.x) > local.z * hTangent || Mathf.Abs(local.y) > local.z * vTangent)
                continue;
            inFovCount++;
            Vector3 ray = point - transform.position;
            float distance = ray.magnitude;
            if (distance < 0.001f) { visibleCount++; continue; }
            if (distance < minimumDistance) minimumDistance = distance;
            SpatialOccluderIdentity firstBlocker = FindFirstIdentityBlocker(
                transform.position, point, distance, targetFeatureToIgnore);
            if (firstBlocker == null) visibleCount++;
            else
            {
                if (identities != null) AddBlocker(identities, firstBlocker);
            }
        }

    }

    /// <summary>
    /// 用 Renderer 的世界轴对齐包围盒和格点，近似估计目标总投影面积、视场内面积和无遮挡面积。
    /// 每个盒面格点的权重 = 面积/16 × max(0, 面法线·朝传感器单位向量)；背向的面没有正投影贡献。
    /// 对小于等于 1e-6 的面或权重跳过；包围盒面代替真实网格表面，所以这是几何近似，不是精确网格面积。
    /// 面积比例分母无贡献时设为 0。
    /// </summary>
    private void MeasureProjectedTargetArea(Renderer[] targetRenderers, SpatialSensorTelemetry result)
    {
        float totalProjectedArea = 0;
        float projectedAreaInFov = 0;
        float visibleProjectedArea = 0;
        Vector3[] unitAxes = { Vector3.right, Vector3.up, Vector3.forward };

        foreach (Renderer renderer in SelectProjectedAreaRenderers(targetRenderers))
        {
                
                Bounds bounds = renderer.bounds;
                Vector3 size = bounds.size;
                Vector3 toSensor = (transform.position - bounds.center).normalized;
                for (int normalAxis = 0; normalAxis < 3; normalAxis++)
                {
                    int uAxis = (normalAxis + 1) % 3;
                    int vAxis = (normalAxis + 2) % 3;
                    float faceArea = Axis(size, uAxis) * Axis(size, vAxis);
                    if (faceArea <= 0.000001f) continue;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Vector3 faceNormal = unitAxes[normalAxis] * sign;
                        float projectedWeight = faceArea * Mathf.Max(0, Vector3.Dot(faceNormal, toSensor));
                        if (projectedWeight <= 0.000001f) continue;
                        float sampleWeight = projectedWeight
                            / (ProjectedAreaGridResolution * ProjectedAreaGridResolution);
                        for (int u = 0; u < ProjectedAreaGridResolution; u++)
                        for (int v = 0; v < ProjectedAreaGridResolution; v++)
                        {
                            Vector3 point = bounds.center;
                            SetAxis(ref point, normalAxis, Axis(bounds.center, normalAxis)
                                + sign * Axis(bounds.extents, normalAxis));
                            SetAxis(ref point, uAxis, Axis(bounds.min, uAxis)
                                + (u + 0.5f) * Axis(size, uAxis) / ProjectedAreaGridResolution);
                            SetAxis(ref point, vAxis, Axis(bounds.min, vAxis)
                                + (v + 0.5f) * Axis(size, vAxis) / ProjectedAreaGridResolution);

                            totalProjectedArea += sampleWeight;
                            result.targetProjectedAreaSampleCount++;
                            if (!IsPointInFov(point)) continue;
                            projectedAreaInFov += sampleWeight;
                            result.targetAreaInFovSampleCount++;
                            float distance = Vector3.Distance(transform.position, point);
                            SpatialOccluderIdentity blocker = FindFirstIdentityBlocker(
                                transform.position, point, distance, renderer.transform);
                            if (blocker == null)
                            {
                                visibleProjectedArea += sampleWeight;
                                result.visibleTargetAreaSampleCount++;
                            }
                            else AddBlocker(blockers, blocker);
                        }
                    }
                }
        }

        result.targetAreaInFovRatio = totalProjectedArea <= 0 ? 0 : projectedAreaInFov / totalProjectedArea;
        result.visibleTargetAreaRatio = totalProjectedArea <= 0 ? 0 : visibleProjectedArea / totalProjectedArea;
        result.blockers = new List<SpatialBlockerData>(blockers.Values).ToArray();
    }

    /// <summary>
    /// 为投影估算挑选有限数量、从传感器方向看投影较大的 Renderer。
    /// 跳过空、禁用、未激活或几乎没有包围盒尺寸的对象；只接受 MeshRenderer 与 SkinnedMeshRenderer。
    /// 按包围盒三个投影面的加权尺度排分，最多保留 8 个；这是性能上限而非模型里的总零件数。
    /// </summary>
    private List<Renderer> SelectProjectedAreaRenderers(Renderer[] targetRenderers)
    {
        List<Renderer> selected = new List<Renderer>(ProjectedAreaMaxRenderers);
        List<float> scores = new List<float>(ProjectedAreaMaxRenderers);
        if (targetRenderers == null) return selected;
        foreach (Renderer renderer in targetRenderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                || (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))) continue;
            Vector3 size = renderer.bounds.size;
            if (size.sqrMagnitude < 0.000001f) continue;
            Vector3 direction = (transform.position - renderer.bounds.center).normalized;
            float score = size.y * size.z * Mathf.Abs(direction.x)
                        + size.x * size.z * Mathf.Abs(direction.y)
                        + size.x * size.y * Mathf.Abs(direction.z);
            int insertAt = 0;
            while (insertAt < scores.Count && scores[insertAt] >= score) insertAt++;
            if (insertAt >= ProjectedAreaMaxRenderers) continue;
            selected.Insert(insertAt, renderer);
            scores.Insert(insertAt, score);
            if (selected.Count > ProjectedAreaMaxRenderers)
            {
                selected.RemoveAt(selected.Count - 1);
                scores.RemoveAt(scores.Count - 1);
            }
        }
        return selected;
    }

    /// <summary>
    /// 只判断一个世界点是否位于传感器前方的矩形视场锥中，不检查它是否被遮住。
    /// 逆变换到传感器本地坐标后与水平、垂直半视场正切比较；正好落在边界仍算在视场内。
    /// </summary>
    private bool IsPointInFov(Vector3 point)
    {
        Vector3 local = transform.InverseTransformPoint(point);
        float hTangent = Mathf.Tan(Mathf.Clamp(configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);
        float vTangent = Mathf.Tan(Mathf.Clamp(configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);
        return local.z > 0 && Mathf.Abs(local.x) <= local.z * hTangent
               && Mathf.Abs(local.y) <= local.z * vTangent;
    }

    private static void AddBlocker(Dictionary<string, SpatialBlockerData> destination, SpatialOccluderIdentity hit)
    {
        // 同名的参考模型能同时装到两器上，归属取自命中实例的父层级。
        SpatialCraftAssembly owner = hit.GetComponentInParent<SpatialCraftAssembly>();
        string key = owner == null ? hit.partId : owner.GetInstanceID() + ":" + hit.partId;
        if (destination.ContainsKey(key)) return;
        destination.Add(key, new SpatialBlockerData { partId = hit.partId, displayName = hit.displayName,
            partName = hit.partName, category = hit.category, ownerCraftId = owner != null ? owner.craftId : "" });
    }

    /// <summary>
    /// 沿传感器到目标点的线段查最近的、带遮挡身份标签的物体。
    /// 距离小于 0.001 米不查；射线长度在目标前截去 0.03 米，避免射到目标表面本身；触发器排除。
    /// RaycastAll 命中先按距离排序，忽略目标自身及子物体；无标签碰撞体会继续略过，直到有身份标签的遮挡物或射线到头。
    /// </summary>
    private SpatialOccluderIdentity FindFirstIdentityBlocker(Vector3 origin, Vector3 point, float distance,
                                                             Transform ignoredGeometry = null)
    {
        Vector3 ray = point - origin;
        if (distance < 0.001f) return null;
        RaycastHit[] hits = Physics.RaycastAll(origin, ray / distance, Mathf.Max(0, distance - 0.03f),
                                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            if (ignoredGeometry != null && IsSameOrChild(hits[i].collider.transform, ignoredGeometry)) continue;
            SpatialOccluderIdentity identity = hits[i].collider.GetComponentInParent<SpatialOccluderIdentity>();
            if (identity != null) return identity;
        }
        return null;
    }

    private static bool IsSameOrChild(Transform candidate, Transform root)
    {
        return candidate == root || candidate.IsChildOf(root);
    }

    private static float Axis(Vector3 vector, int axis)
    {
        return axis == 0 ? vector.x : axis == 1 ? vector.y : vector.z;
    }

    private static void SetAxis(ref Vector3 vector, int axis, float value)
    {
        if (axis == 0) vector.x = value;
        else if (axis == 1) vector.y = value;
        else vector.z = value;
    }

    /// <summary>
    /// 以太阳圆盘中心和圆周样点发射射线，粗略估算太阳能否直达传感器。
    /// 若向日向量太短（长度平方不大于 1e-6）不发射射线；参考轴阈值 0.9 用来避免叉积接近零。
    /// 圆盘角半径取 0.2666°，共用 1 个中心点和 8 个圆周点。每个点取最近的任意碰撞体，不要求遮挡体带身份标签。
    /// 遮挡率 = 1 − 可见样点数 / 总样点数；零方向提前返回，此时没有样点。
    /// </summary>
    private void MeasureSunVisibility(Vector3 sunDirection, SpatialSensorTelemetry result)
    {
        Vector3 sun = sunDirection.sqrMagnitude > 0.000001f ? sunDirection.normalized : Vector3.zero;
        if (sun == Vector3.zero) return;
        Vector3 reference = Mathf.Abs(Vector3.Dot(sun, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
        Vector3 tangent = Vector3.Cross(sun, reference).normalized;
        Vector3 bitangent = Vector3.Cross(sun, tangent).normalized;
        List<string> blockerIds = new List<string>();
        result.sunSampleCount = SunDiskRingSamples + 1;
        float diskRadius = Mathf.Tan(SunAngularRadiusDeg * Mathf.Deg2Rad);

        for (int i = 0; i < result.sunSampleCount; i++)
        {
            Vector3 rayDirection = sun;
            if (i > 0)
            {
                float angle = (i - 1) * Mathf.PI * 2 / SunDiskRingSamples;
                Vector3 offset = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
                rayDirection = (sun + offset * diskRadius).normalized;
            }
            Vector3 origin = transform.position + rayDirection * 0.05f;
            RaycastHit[] hits = Physics.RaycastAll(origin, rayDirection, 10000f,
                                                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Collider blocker = null;
            for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
            {
                if (hits[hitIndex].distance <= 0.05f) continue;
                blocker = hits[hitIndex].collider;
                break;
            }
            if (blocker == null)
            {
                result.sunVisibleSampleCount++;
                continue;
            }
            SpatialOccluderIdentity identity = blocker.GetComponentInParent<SpatialOccluderIdentity>();
            string id = identity != null && !string.IsNullOrEmpty(identity.partId)
                ? identity.partId : "UNIDENTIFIED:" + blocker.gameObject.name;
            if (!blockerIds.Contains(id)) blockerIds.Add(id);
        }
        result.sunBlockerIds = blockerIds.ToArray();
        result.sunOcclusionRatio = result.sunSampleCount == 0 ? 0
            : 1f - (float)result.sunVisibleSampleCount / result.sunSampleCount;
    }

    /// <summary>
    /// 用离散的小角度试探，找一个几何点积更小的机械臂或本体调整建议。
    /// 每次试 5° 到 90°、每 5° 一档、两个方向；并不实际转动场景，也不是连续优化。
    /// 预测值 max(0, 朝向点积) 是几何照射近似；只有比当前最优低超过 0.001 才替换建议。
    /// 机械臂绕其局部前向，机体试探绕列出的横滚/俯仰/偏航候选轴；无候选改善就保留初始提示。
    /// </summary>
    private string FindAttitudeRecommendation(Vector3 sunDirection, Quaternion bodyRotation, float current,
                                              Transform[] adjustmentJoints)
    {
        float best = current;
        string suggestion = "已达光照告警阈值；当前姿态范围内未找到改善方向";
        if (adjustmentJoints != null)
        {
            for (int jointIndex = 0; jointIndex < adjustmentJoints.Length; jointIndex++)
            {
                Transform joint = adjustmentJoints[jointIndex];
                if (joint == null || (transform != joint && !transform.IsChildOf(joint))) continue;
                Quaternion jointRotation = joint.rotation;
                for (int angle = 5; angle <= 90; angle += 5)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Quaternion delta = Quaternion.AngleAxis(sign * angle, Vector3.forward);
                        Vector3 candidateForward = jointRotation * delta * Quaternion.Inverse(jointRotation)
                                                   * transform.forward;
                        float predicted = Mathf.Max(0, Vector3.Dot(candidateForward.normalized,
                                                                   sunDirection.normalized));
                        if (predicted >= best - 0.001f) continue;
                        best = predicted;
                        suggestion = string.Format(CultureInfo.InvariantCulture,
                            "建议机械臂 J{0}{1} {2}°；几何照度预计降至 {3:P1}",
                            jointIndex + 1, sign > 0 ? "+" : "-", angle, predicted);
                    }
                }
            }
        }

        Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        string[] names = { "横滚", "俯仰", "偏航" };
        for (int angle = 5; angle <= 90; angle += 5)
        {
            for (int axis = 0; axis < axes.Length; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Quaternion candidate = bodyRotation * Quaternion.AngleAxis(sign * angle, axes[axis]);
                    Vector3 candidateForward = candidate * Quaternion.Inverse(bodyRotation) * transform.forward;
                    float predicted = Mathf.Max(0, Vector3.Dot(candidateForward.normalized, sunDirection.normalized));
                    if (predicted >= best - 0.001f) continue;
                    best = predicted;
                    suggestion = string.Format(CultureInfo.InvariantCulture,
                        "建议航天器{0}{1} {2}°；几何照度预计降至 {3:P1}",
                        names[axis], sign > 0 ? "+" : "-", angle, predicted);
                }
            }
        }
        return suggestion;
    }

    private static float Value(float[] values, int index)
    {
        return values != null && values.Length > index ? values[index] : 0;
    }
}
