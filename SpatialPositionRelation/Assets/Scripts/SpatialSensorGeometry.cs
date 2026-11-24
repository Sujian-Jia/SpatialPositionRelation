using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public abstract partial class SpatialSensorViewBase : MonoBehaviour
{
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
