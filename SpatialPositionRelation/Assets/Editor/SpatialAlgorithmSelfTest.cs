using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class SpatialAlgorithmSelfTest
{
    /// <summary>
    /// 这是项目已有的编辑器自检入口，用临时传感器、目标和遮挡物调用实际几何方法。
    /// MenuItem 使它出现在 Unity 编辑器菜单；正常运行时的调用入口在 SpatialSimulationBase。
    /// </summary>
    [MenuItem("Spatial Demo/Run Occlusion Algorithm Checks")]
    public static void RunBatch()
    {
        GameObject sensorObject = new GameObject("AlgorithmTestSensor");
        GameObject targetObject = new GameObject("AlgorithmTestTarget");
        GameObject blockerObject = null;
        try
        {
            SpatialSensorView sensor = sensorObject.AddComponent<SpatialSensorView>();
            sensor.sensorId = "TEST-SENSOR";
            sensor.configuration = new SpatialSensorConfig {
                id = "TEST-SENSOR", enabled = true, horizontalFovDeg = 30, verticalFovDeg = 30,
                warningEnabled = false, localPositionM = new[] { 0f, 0f, 0f }, localEulerDeg = new[] { 0f, 0f, 0f }
            };
            Vector3[] points = { new Vector3(-0.6f, 0, 8), new Vector3(0.6f, 0, 8) };
            Physics.SyncTransforms();

            SpatialSensorTelemetry result = sensor.Calculate(targetObject.transform,
                new[] { new Vector3(8, 0, 8) }, Vector3.right, false, Quaternion.identity, 1);
            Require(result.status == "OutOfFov", "OutOfFov", result.status);

            result = sensor.Calculate(targetObject.transform, points, Vector3.right, false, Quaternion.identity, 2);
            Require(result.status == "Visible" && result.visibleRatio == 1, "Visible", result.status);

            blockerObject = CreateBlocker("TargetDockingCollider", Vector3.forward * 4,
                                          new Vector3(4, 4, 0.5f), "TARGET_DOCK", "目标对接机构");
            Physics.SyncTransforms();
            result = sensor.Calculate(targetObject.transform, points, Vector3.right, false, Quaternion.identity, 2,
                                      targetFeatureToIgnore: blockerObject.transform);
            Require(result.status == "Visible" && result.blockers.Length == 0,
                    "TargetDockingNotOccluder", result.status + " blockers=" + result.blockers.Length);
            UnityEngine.Object.DestroyImmediate(blockerObject);
            blockerObject = null;

            blockerObject = CreateBlocker("PartialArmBlocker", new Vector3(-0.3f, 0, 4), new Vector3(0.24f, 0.5f, 0.5f),
                                          "ARM_LINK_2", "机械臂连杆 2");
            Physics.SyncTransforms();
            bool directHit = Physics.Raycast(new Vector3(-0.3f, 0, 0), Vector3.forward, out RaycastHit probeHit,
                                              8, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Debug.Log("SPATIAL_PHYSICS_PROBE hit=" + directHit + " collider="
                      + (directHit ? probeHit.collider.name : "none") + " blocker=" + blockerObject.transform.position);
            Require(directHit && probeHit.collider.gameObject == blockerObject, "PhysicsProbe",
                    directHit ? probeHit.collider.name : "no hit");
            result = sensor.Calculate(targetObject.transform, points, Vector3.right, false, Quaternion.identity, 3);
            Require(result.status == "PartiallyBlocked" && result.visibleCount == 1,
                    "PartiallyBlocked", result.status + " visible=" + result.visibleCount);
            Require(result.blockers.Length == 1 && result.blockers[0].partId == "ARM_LINK_2",
                    "BlockerIdentity", result.blockers.Length == 0 ? "none" : result.blockers[0].partId);

            UnityEngine.Object.DestroyImmediate(blockerObject);
            blockerObject = CreateBlocker("FullPanelBlocker", Vector3.forward * 4, new Vector3(8, 8, 0.5f),
                                          "SOLAR_ARRAY_1", "太阳翼 1");
            Physics.SyncTransforms();
            result = sensor.Calculate(targetObject.transform, points, Vector3.right, false, Quaternion.identity, 4);
            Require(result.status == "FullyBlocked" && result.visibleRatio == 0, "FullyBlocked", result.status);
            UnityEngine.Object.DestroyImmediate(blockerObject);
            blockerObject = null;

            VerifyProjectedTargetArea(sensor, targetObject);
            VerifyLightWarning(sensor, targetObject);
            VerifyOrbitAndCoordinateAdapter();
            Debug.Log("SPATIAL_ALGORITHM_TESTS_OK cases=26 fov=pass visible=pass targetExclusion=pass targetArea=pass partialBlock=pass fullBlock=pass identity=pass orbit=pass axisAdapter=pass lightWarning=pass sunOcclusion=pass jointRecommendation=pass");
        }
        finally
        {
            if (blockerObject != null) UnityEngine.Object.DestroyImmediate(blockerObject);
            UnityEngine.Object.DestroyImmediate(sensorObject);
            UnityEngine.Object.DestroyImmediate(targetObject);
        }
    }

    private static void VerifyProjectedTargetArea(SpatialSensorView sensor, GameObject target)
    {
        sensor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        sensor.configuration = new SpatialSensorConfig {
            id = "TEST-AREA", enabled = true, horizontalFovDeg = 40, verticalFovDeg = 40,
            warningEnabled = false
        };
        GameObject targetModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GameObject areaBlocker = null;
        try
        {
            targetModel.transform.SetPositionAndRotation(new Vector3(3.5f, 0, 10), Quaternion.identity);
            targetModel.transform.localScale = new Vector3(4, 2, 1);
            Renderer[] renderers = { targetModel.GetComponent<Renderer>() };
            SpatialOccluderIdentity targetIdentity = targetModel.AddComponent<SpatialOccluderIdentity>();
            targetIdentity.partId = "TEST_TARGET_SURFACE";
            targetIdentity.displayName = "测试目标表面";
            Vector3[] dockingPoints = { new Vector3(0, 0, 10) };
            Physics.SyncTransforms();
            SpatialSensorTelemetry result = sensor.Calculate(target.transform, dockingPoints, Vector3.forward,
                false, Quaternion.identity, 9, null, renderers, "ACTIVE_TARGET", "SpaceStation");
            Require(result.targetProjectedAreaSampleCount > 0, "TargetAreaSamples", result.targetProjectedAreaSampleCount.ToString());
            Require(result.targetAreaInFovRatio > 0.35f && result.targetAreaInFovRatio < 0.75f,
                    "PartialTargetAreaInFov", result.targetAreaInFovRatio.ToString("F3"));
            Require(!Array.Exists(result.blockers, item => item.partId == "TEST_TARGET_SURFACE"),
                    "TargetSurfaceNotSelfOccluder", result.blockers.Length.ToString());

            areaBlocker = CreateBlocker("ProjectedAreaBlocker", new Vector3(0.8f, 0, 5),
                                        new Vector3(1, 3, 0.5f), "AREA_BLOCKER", "投影面积遮挡板");
            Physics.SyncTransforms();
            result = sensor.Calculate(target.transform, dockingPoints, Vector3.forward,
                false, Quaternion.identity, 10, null, renderers, "ACTIVE_TARGET", "SpaceStation");
            Require(result.visibleTargetAreaRatio < result.targetAreaInFovRatio,
                    "TargetVisibleAreaReducedByOcclusion",
                    result.visibleTargetAreaRatio.ToString("F3") + "/" + result.targetAreaInFovRatio.ToString("F3"));
            Require(Array.Exists(result.blockers, item => item.partId == "AREA_BLOCKER"),
                    "TargetAreaBlockerIdentity", result.blockers.Length.ToString());
        }
        finally
        {
            if (areaBlocker != null) UnityEngine.Object.DestroyImmediate(areaBlocker);
            UnityEngine.Object.DestroyImmediate(targetModel);
        }
    }

    private static void VerifyLightWarning(SpatialSensorView sensor, GameObject target)
    {
        sensor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        sensor.configuration = new SpatialSensorConfig {
            id = "TEST-LIGHT", enabled = true, horizontalFovDeg = 40, verticalFovDeg = 40,
            warningEnabled = true, warningThreshold = 0.3f
        };
        Vector3[] points = { new Vector3(0, 0, 10) };
        SpatialSensorTelemetry result = sensor.Calculate(target.transform, points, Vector3.forward,
                                                          false, Quaternion.identity, 5);
        Require(result.warning && Mathf.Abs(result.normalizedIllumination - 1) < 0.001f,
                "LightWarningAboveThreshold", result.normalizedIllumination.ToString("F3"));
        Require(result.sunSampleCount == 9 && result.sunOcclusionRatio == 0,
                "UnobstructedSolarDisk", result.sunVisibleSampleCount + "/" + result.sunSampleCount);
        Require(result.recommendation.StartsWith("建议", StringComparison.Ordinal),
                "LightAdjustmentRecommendation", result.recommendation);
        Require(result.recommendation.Contains("预计降至") && result.recommendation.Contains("0.0"),
                "LightRecommendationReducesIllumination", result.recommendation);

        Transform originalParent = sensor.transform.parent;
        GameObject jointObject = new GameObject("AlgorithmTestArmJoint");
        sensor.transform.SetParent(jointObject.transform, false);
        sensor.transform.localRotation = Quaternion.Euler(0, 90, 0);
        result = sensor.Calculate(target.transform, points, Vector3.right, false, Quaternion.identity, 8,
                                  new[] { jointObject.transform });
        Require(result.recommendation.Contains("机械臂 J1"),
                "ArmJointAdjustmentRecommendation", result.recommendation);
        sensor.transform.SetParent(originalParent, false);
        sensor.transform.localRotation = Quaternion.identity;
        UnityEngine.Object.DestroyImmediate(jointObject);

        Vector3 thresholdDirection = new Vector3(Mathf.Sqrt(0.91f), 0, 0.3f).normalized;
        result = sensor.Calculate(target.transform, points, thresholdDirection, false,
                                  Quaternion.identity, 6);
        Require(result.warning && result.normalizedIllumination >= 0.3f,
                "LightWarningAtThreshold", result.normalizedIllumination.ToString("F3"));

        Vector3 belowDirection = new Vector3(Mathf.Sqrt(1 - 0.299f * 0.299f), 0, 0.299f).normalized;
        result = sensor.Calculate(target.transform, points, belowDirection, false,
                                  Quaternion.identity, 7);
        Require(!result.warning && result.normalizedIllumination < 0.3f,
                "LightWarningBelowThreshold", result.normalizedIllumination.ToString("F3"));

        result = sensor.Calculate(target.transform, points, Vector3.forward, true, Quaternion.identity, 11);
        Require(result.normalizedIllumination == 0 && !result.warning,
                "EarthShadowDirectIlluminationZero", result.normalizedIllumination.ToString("F3"));

        GameObject solarBlocker = CreateBlocker("SolarDiskBlocker", new Vector3(0, 0, 4),
                                                new Vector3(0.01f, 0.2f, 0.2f),
                                                "SUN_TEST_BLOCKER", "太阳遮挡验证板");
        try
        {
            Physics.SyncTransforms();
            result = sensor.Calculate(target.transform, points, Vector3.forward, false, Quaternion.identity, 12);
            Require(result.sunOcclusionRatio > 0 && result.sunOcclusionRatio < 1,
                    "PartialSolarDiskOcclusion", result.sunOcclusionRatio.ToString("F3"));
            Require(Mathf.Abs(result.normalizedIllumination - (1 - result.sunOcclusionRatio)) < 0.001f,
                    "IlluminationIncludesUnobstructedFraction", result.normalizedIllumination.ToString("F3"));
            Require(Array.Exists(result.sunBlockerIds, id => id == "SUN_TEST_BLOCKER"),
                    "SolarBlockerIdentity", string.Join(",", result.sunBlockerIds));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(solarBlocker);
        }
    }

    private static void VerifyOrbitAndCoordinateAdapter()
    {
        GameObject root = new GameObject("AlgorithmTestSimulation");
        try
        {
            SpatialSimulation simulation = root.AddComponent<SpatialSimulation>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo stateField = typeof(SpatialSimulationBase).GetField("state", flags);
            FieldInfo positionField = typeof(SpatialSimulationBase).GetField("orbitPositionKm", flags);
            FieldInfo sunField = typeof(SpatialSimulationBase).GetField("sunDirection", flags);
            MethodInfo updateMethod = typeof(SpatialSimulationBase).GetMethod("UpdateSolarGeometry", flags);
            if (stateField == null || positionField == null || sunField == null || updateMethod == null)
                throw new InvalidOperationException("Orbital test could not resolve simulation members.");

            stateField.SetValue(simulation, new SpatialStateMessage {
                orbit = new SpatialOrbit {
                    altitudeKm = 600, eccentricity = 0, inclinationDeg = 0, raanDeg = 0,
                    argPeriapsisDeg = 0, meanAnomalyDeg = 0, epochUtc = "2000-01-01T12:00:00Z"
                }
            });
            updateMethod.Invoke(simulation, new object[] { 0f });
            Vector3 position = (Vector3)positionField.GetValue(simulation);
            Require(Mathf.Abs(position.x - 6978.137f) < 0.02f && Mathf.Abs(position.y) < 0.01f
                    && Mathf.Abs(position.z) < 0.01f,
                    "CircularOrbitPosition", position.ToString("F3"));

            Vector3 sun = (Vector3)sunField.GetValue(simulation);
            Vector3 expectedSun = new Vector3(0.179f, -0.391f, -0.903f);
            Require(Mathf.Abs(sun.magnitude - 1) < 0.001f && Vector3.Distance(sun, expectedSun) < 0.01f,
                    "J2000SunDirectionUnity", sun.ToString("F3"));

            double r = Math.Sqrt(0.5);
            Quaternion reflected = SpatialDynamicsAdapter.Rotation(new[] { r, r, 0, 0 }, 0);
            Vector3 actual = reflected * SpatialDynamicsAdapter.Polar(new[] { 2.0, -3, 4 }, 0);
            Vector3 expected = SpatialDynamicsAdapter.Polar(new[] { 2.0, -4, -3 }, 0);
            Vector3 ax = SpatialDynamicsAdapter.Axial(new[] { 1.0, 0, 0 }, 0);
            Vector3 byCross = Vector3.Cross(Vector3.up, -Vector3.forward);
            Require(Vector3.Distance(actual, expected) < 1e-5f && Vector3.Distance(ax, byCross) < 1e-5f,
                    "DynamicsReflectionRotationAndAxialAxis", actual.ToString("F3"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static GameObject CreateBlocker(string name, Vector3 position, Vector3 size, string id, string label)
    {
        GameObject blocker = new GameObject(name);
        blocker.transform.position = position;
        BoxCollider collider = blocker.AddComponent<BoxCollider>();
        collider.size = size;
        SpatialOccluderIdentity identity = blocker.AddComponent<SpatialOccluderIdentity>();
        identity.partId = id;
        identity.displayName = label;
        identity.category = "SelfArm";
        return blocker;
    }

    private static void Require(bool condition, string name, string actual)
    {
        if (!condition) throw new InvalidOperationException(name + " expected pass; actual=" + actual);
        Debug.Log("SPATIAL_ALGORITHM_CASE_PASS " + name + " actual=" + actual);
    }
}

