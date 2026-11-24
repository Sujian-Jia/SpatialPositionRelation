using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public abstract class SpatialSimulationBase : MonoBehaviour
{
    private const double EarthRadiusKm = 6378.137;
    private const double EarthMu = 398600.4418;
    public SpatialTcpBridge tcpBridge;
    public Light sunLight;
    public Transform sunDisc;
    public Transform dockingTarget;
    public SpatialCraftAssembly[] craft = Array.Empty<SpatialCraftAssembly>();
    public SpatialSensorView[] sensorViews = Array.Empty<SpatialSensorView>();
    public Camera overviewCamera;
    public SpatialEmbeddedViews embeddedViews;
    public LineRenderer sunDirectionLine;

    private SpatialStateMessage state = new SpatialStateMessage();
    private readonly Dictionary<string, SpatialCraftAssembly> craftById = new Dictionary<string, SpatialCraftAssembly>();
    private readonly Dictionary<string, SpatialSensorView> sensorById = new Dictionary<string, SpatialSensorView>();
    private double simulationSeconds;
    private int announcedConnection;
    private string firstFrameUtc;

    private void RecordFirstFrame(Camera camera)
    {
        if (camera != overviewCamera || firstFrameUtc != null) return;
        firstFrameUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Camera.onPostRender -= RecordFirstFrame;
        Debug.Log("SPATIAL_FIRST_FRAME utc=" + firstFrameUtc);
        if (tcpBridge != null && tcpBridge.InstanceToken.Length != 0 && tcpBridge.IsConnected)
            tcpBridge.SendJson(JsonUtility.ToJson(new SpatialLifecycleMessage {
                type = "firstFrame", instanceToken = tcpBridge.InstanceToken, utc = firstFrameUtc
            }));
    }

    protected virtual void OnDestroy() => Camera.onPostRender -= RecordFirstFrame;
    private float telemetryTimer;
    private long telemetrySequence;
    private Vector3 sunDirection = Vector3.up;
    private Vector3 orbitPositionKm;
    private bool earthShadow;
    private double trajectoryActivationSec;
    private bool forecastDirty;
    private readonly Dictionary<string, TrajectoryOcclusion> trajectoryForecast = new Dictionary<string, TrajectoryOcclusion>();

    private sealed class TrajectoryOcclusion
    {
        public SpatialSensorView view;
        public Transform feature;
        public Transform ignoredFeature;
        public Renderer[] renderers;
        public Vector3[] fixedSamplePoints;
        public float windowSec;
        public float sampleSec;
        public float blockedSec;
        public float outOfFovSec;
        public float peakBlockedRatio;
        public readonly List<Vector2> occlusionWindows = new List<Vector2>();
    }

    protected virtual void Start()
    {
        if (tcpBridge == null) tcpBridge = GetComponent<SpatialTcpBridge>();
        craft = craft ?? Array.Empty<SpatialCraftAssembly>();
        sensorViews = sensorViews ?? Array.Empty<SpatialSensorView>();
        foreach (SpatialCraftAssembly assembly in craft)
            if (assembly != null && !craftById.ContainsKey(assembly.craftId)) craftById.Add(assembly.craftId, assembly);
        foreach (SpatialSensorView sensor in sensorViews)
            if (sensor != null && !sensorById.ContainsKey(sensor.sensorId)) sensorById.Add(sensor.sensorId, sensor);
        Camera.onPostRender += RecordFirstFrame;
        UpdateSolarGeometry(0);
        ApplyState();
        Debug.Log("SPATIAL_SCENE_READY utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    protected virtual void Update()
    {
        if (tcpBridge != null)
        {
            int connection = tcpBridge.ConnectionId;
            if (tcpBridge.InstanceToken.Length != 0 && tcpBridge.IsConnected && connection != announcedConnection)
            {
                announcedConnection = connection;
                tcpBridge.SendJson(JsonUtility.ToJson(new SpatialLifecycleMessage {
                    type = "ready", instanceToken = tcpBridge.InstanceToken,
                    utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    firstFrameRendered = firstFrameUtc != null, firstFrameUtc = firstFrameUtc
                }));
            }
            for (int count = 0; count < 16 && tcpBridge.TryDequeue(out string line); count++)
                ProcessMessage(line);
        }
        SpatialOrbit orbit = state.orbit ?? new SpatialOrbit();
        double timeScale = Math.Max(0, orbit.timeScale);
        if (state.demoDocking && timeScale == 0) timeScale = 60;
        if (!state.demoPrepared) simulationSeconds += Time.deltaTime * timeScale;
        if (state.demoDocking && state.trajectory != null)
            simulationSeconds = Math.Min(simulationSeconds, state.trajectory[state.trajectory.Length - 1].tSec);
        UpdateSolarGeometry((float)simulationSeconds);
        if (TryGetActiveCraft(out var target, out var chaser))
        {
            if (state.trajectory != null) ApplyTrajectoryPose(target, chaser, TrajectoryTimeSec());
            ApplySolarTracking(target, sunDirection, true);
            ApplySolarTracking(chaser, sunDirection, false);
            RefreshOverview(target, chaser);
        }
        Physics.SyncTransforms();

        telemetryTimer += Time.unscaledDeltaTime;
        if (telemetryTimer >= 0.2f)
        {
            telemetryTimer = 0;
            PublishTelemetry();
        }
    }

    private void ProcessMessage(string line)
    {
        if (line.StartsWith("__ERROR__", StringComparison.Ordinal))
        {
            Debug.LogError("Spatial TCP listener: " + line.Substring(9));
            return;
        }
        try
        {
            SpatialStateMessage incoming = JsonUtility.FromJson<SpatialStateMessage>(line);
            if (incoming?.protocol == "SPR/1" && incoming.type == "shutdown")
            {
                string token = JsonUtility.FromJson<SpatialLifecycleMessage>(line).instanceToken;
                if (tcpBridge != null && tcpBridge.InstanceToken.Length != 0 && token == tcpBridge.InstanceToken)
                    Application.Quit();
                return;
            }
            if (incoming != null && incoming.protocol == "SPR/1" && incoming.type == "captureSensor")
            {
                CaptureSensor(JsonUtility.FromJson<SpatialCaptureMessage>(line));
                return;
            }
            if (incoming == null || incoming.protocol != "SPR/1" || incoming.type != "state")
            {
                SendError("协议版本或消息类型无效");
                return;
            }
            if (!IsValidTrajectory(incoming.trajectory))
            {
                SendError("轨迹无效。请检查时间递增、有限坐标和采样点数量");
                return;
            }
            if (incoming.trajectory == null || incoming.trajectory.Length == 0)
                incoming.trajectory = null;
            if ((incoming.demoDocking || incoming.demoPrepared) && incoming.trajectory == null)
            {
                SendError("对接演示缺少轨迹");
                return;
            }
            if (state.demoRunId != incoming.demoRunId) simulationSeconds = 0;
            if (state.trajectoryTimeBase != incoming.trajectoryTimeBase
                || !SameTrajectory(state.trajectory, incoming.trajectory))
                trajectoryActivationSec = simulationSeconds;
            state = incoming;
            ApplyState();
        }
        catch (Exception exception)
        {
            SendError("状态 JSON 无法解析：" + exception.Message);
        }
    }

    private void CaptureSensor(SpatialCaptureMessage request)
    {
        var reply = new SpatialCaptureMessage {
            type = "sensorFrame", seq = request.seq, sensorId = request.sensorId,
            utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
        if (string.IsNullOrEmpty(request.sensorId)
            || !sensorById.TryGetValue(request.sensorId, out SpatialSensorView sensor)
            || !sensor.isActiveAndEnabled || sensor.sensorCamera == null || sensor.configuration == null)
        {
            reply.error = "所选传感器未启用或没有可用相机";
        }
        else
        {
            try { reply.pngBase64 = Convert.ToBase64String(RenderSensorPng(sensor)); }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                reply.error = "Unity 未能渲染所选相机";
            }
        }
        tcpBridge?.SendJson(JsonUtility.ToJson(reply));
    }

    private static byte[] RenderSensorPng(SpatialSensorView sensor)
    {
        Camera camera = sensor.sensorCamera;
        const int longestSide = 640;
        float h = Mathf.Clamp(sensor.configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f;
        float v = Mathf.Clamp(sensor.configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f;
        float aspect = Mathf.Tan(h) / Mathf.Tan(v);
        int width = Mathf.Max(1, Mathf.RoundToInt(longestSide * Mathf.Min(1, aspect)));
        int height = Mathf.Max(1, Mathf.RoundToInt(longestSide * Mathf.Min(1, 1 / aspect)));
        RenderTexture originalTarget = camera.targetTexture;
        RenderTexture originalActive = RenderTexture.active;
        Rect originalRect = camera.rect;
        float originalAspect = camera.aspect;
        RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        Texture2D pixels = null;
        try
        {
            camera.targetTexture = target;
            camera.rect = new Rect(0, 0, 1, 1);
            camera.aspect = aspect;
            camera.Render();
            RenderTexture.active = target;
            pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            return pixels.EncodeToPNG();
        }
        finally
        {
            camera.targetTexture = originalTarget;
            camera.rect = originalRect;
            camera.aspect = originalAspect;
            RenderTexture.active = originalActive;
            RenderTexture.ReleaseTemporary(target);
            if (pixels != null) Destroy(pixels);
        }
    }

    private void ApplyState()
    {
        SpatialPair pair = state.activePair ?? new SpatialPair();
        if (string.IsNullOrEmpty(pair.target) || string.IsNullOrEmpty(pair.chaser)
            || !craftById.ContainsKey(pair.target) || !craftById.ContainsKey(pair.chaser)
            || pair.target == pair.chaser)
        {
            pair.target = "SpaceStation";
            pair.chaser = "CrewVehicle";
        }
        state.activePair = pair;
        if (!craftById.TryGetValue(pair.target, out SpatialCraftAssembly target)
            || !craftById.TryGetValue(pair.chaser, out SpatialCraftAssembly chaser))
        {
            SendError("当前场景缺少有效的 SpaceStation/CrewVehicle 组合");
            return;
        }
        dockingTarget = target.dockingPort;
        foreach (SpatialCraftAssembly assembly in craft) assembly.gameObject.SetActive(assembly == target || assembly == chaser);
        if (embeddedViews != null)
            embeddedViews.ApplySettings(state.overviewMode, state.displaySensorIds);

        target.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        float distance = Mathf.Clamp(state.relativeDistanceM, 1, 1000);
        Vector3 relativePosition = new Vector3(-distance, 0, 0);
        if (state.trajectory != null)
            ApplyTrajectoryPose(target, chaser, TrajectoryTimeSec());
        else
            chaser.transform.SetPositionAndRotation(target.transform.position + relativePosition, ReadAttitude(state.attitude));
        forecastDirty = !state.demoPrepared;
        if (state.demoPrepared) trajectoryForecast.Clear();

        float[] joints = state.jointsDeg ?? Array.Empty<float>();
        Transform[] armJoints = chaser.armJoints ?? Array.Empty<Transform>();
        for (int i = 0; i < armJoints.Length; i++)
        {
            float angle = i < joints.Length ? joints[i] : 0;
            Quaternion baseRotation = i < chaser.armBaseRotations.Length
                ? chaser.armBaseRotations[i] : Quaternion.identity;
            armJoints[i].localRotation = baseRotation * Quaternion.AngleAxis(angle, Vector3.forward);
        }
        foreach (SpatialSensorView view in sensorViews)
        {
            SpatialSensorConfig config = FindSensorConfig(view.sensorId);
            if (config == null) { view.gameObject.SetActive(false); continue; }
            Transform mount = config.mount == "ArmWrist" ? chaser.armWrist
                : config.mount == "ArmElbow" ? chaser.armElbow : chaser.transform;
            bool surfacePosition = config.positionReference == "modelSurface";
            Transform installation = surfacePosition ? chaser.FindCameraInstallation(config.id) : null;
            if (installation != null && installation.parent != mount) installation = null;
            // 缺少该关节或模型安装点时停用这一机位，避免退回到无物理意义的根节点。
            if (mount == null || (surfacePosition && installation == null))
            {
                view.gameObject.SetActive(false);
                continue;
            }
            view.ShowRays = state.showRays && (string.IsNullOrEmpty(state.raySensorId) || state.raySensorId == config.id);
            if (!view.ShowRays) view.DrawFrustum();
            view.ApplyConfiguration(config, installation != null ? installation : mount, installation);
            view.gameObject.SetActive(config.enabled);
            if (view.sensorCamera != null) view.sensorCamera.enabled = false;
        }
        RefreshOverview(target, chaser);
    }

    private void RefreshOverview(SpatialCraftAssembly target, SpatialCraftAssembly chaser)
    {
        if (overviewCamera != null)
        {
            float distance = Vector3.Distance(chaser.transform.position, target.transform.position);
            Vector3 focus = Vector3.Lerp(target.transform.position, chaser.transform.position, 0.48f);
            Vector3 offset = target.transform.rotation * new Vector3(distance * 0.16f + 1.6f,
                3.8f + distance * 0.19f, -Mathf.Max(10, distance * 1.13f));
            if (embeddedViews != null)
            {
                embeddedViews.SetOverviewPose(focus, offset, 53);
                embeddedViews.SetCraftRoles(target, chaser);
            }
            else
            {
                overviewCamera.transform.position = focus + offset;
                overviewCamera.transform.LookAt(focus);
                overviewCamera.fieldOfView = 53;
            }
        }
        if (sunDirectionLine != null)
        {
            Vector3 origin = target.transform.TransformPoint(new Vector3(0.55f, 0.2f, -1.25f));
            Vector3 tip = origin + sunDirection * 2.9f;
            sunDirectionLine.SetPosition(0, origin);
            sunDirectionLine.SetPosition(1, Vector3.Lerp(origin, tip, 0.76f));
            sunDirectionLine.SetPosition(2, Vector3.Lerp(origin, tip, 0.77f));
            sunDirectionLine.SetPosition(3, tip);
            // 日盘缩尺显示在向日箭头延长线上，与照明计算共用向日方向。
            Vector3 sunDisplayPoint = origin + sunDirection * 5.8f;
            if (sunDisc != null) sunDisc.position = sunDisplayPoint;
            if (embeddedViews != null) embeddedViews.SunDisplayPoint = sunDisplayPoint;
        }
    }

    // 太阳方向投影到转轴法平面后，以受光面法线为零位求有符号夹角。
    // 参考装配缺实际限位，因此使用 ±90°。向日方向平行于轴时保留上一帧转角。
    internal static float BestSolarAngle(Vector3 axis, Vector3 normal, Vector3 towardSun, float previous)
    {
        Vector3 projected = Vector3.ProjectOnPlane(towardSun, axis);
        if (projected.sqrMagnitude < 1e-6f) return previous;
        float ideal = Vector3.SignedAngle(normal, projected, axis);
        if (Mathf.Abs(ideal) <= 90) return ideal;
        float positive = Vector3.Dot(Quaternion.AngleAxis(90, axis) * normal, towardSun);
        float negative = Vector3.Dot(Quaternion.AngleAxis(-90, axis) * normal, towardSun);
        if (Mathf.Abs(positive - negative) < 1e-5f) return previous < 0 ? -90 : 90;
        return positive > negative ? 90 : -90;
    }

    private void ApplySolarTracking(SpatialCraftAssembly assembly, Vector3 direction, bool target)
    {
        if (assembly.solarAngles == null) assembly.solarAngles = new float[assembly.solarHinges.Length];
        for (int i = 0; i < assembly.solarHinges.Length; i++)
        {
            Transform hinge = assembly.solarHinges[i];
            Quaternion zero = assembly.solarBaseRotations[i];
            Vector3 worldDirectionInZeroFrame = Quaternion.Inverse(zero) * hinge.parent.InverseTransformDirection(direction);
            float angle = IsManualSolar(target) ? Mathf.Clamp(state.solarArrayDeg, -90, 90)
                : BestSolarAngle(assembly.solarLocalAxes[i], assembly.solarFaceNormals[i], worldDirectionInZeroFrame, assembly.solarAngles[i]);
            assembly.solarAngles[i] = angle;
            hinge.localRotation = zero * Quaternion.AngleAxis(angle, assembly.solarLocalAxes[i]);
        }
    }

    private bool IsManualSolar(bool target)
    {
        if (!state.manualSolarDiagnostic) return false;
        if (state.manualSolarRole == "both") return true;
        return target == (state.manualSolarRole != "chaser");
    }

    private void AppendWingResults(SpatialCraftAssembly assembly, List<SpatialWingResult> results, bool target)
    {
        if (assembly.solarAngles == null) return;
        for (int i = 0; i < assembly.solarHinges.Length; i++)
        {
            Transform hinge = assembly.solarHinges[i];
            results.Add(new SpatialWingResult { craftId = assembly.craftId, hinge = hinge.name,
                role = target ? "target" : "chaser", manual = IsManualSolar(target),
                faceNormalWorld = hinge.TransformDirection(assembly.solarFaceNormals[i]),
                angleDeg = assembly.solarAngles[i], axisWorld = hinge.TransformDirection(assembly.solarLocalAxes[i]),
                incidenceDeg = Vector3.Angle(hinge.TransformDirection(assembly.solarFaceNormals[i]), sunDirection) });
        }
    }

    private bool TryGetActiveCraft(out SpatialCraftAssembly target, out SpatialCraftAssembly chaser)
    {
        target = null;
        chaser = null;
        SpatialPair pair = state.activePair;
        return pair != null && !string.IsNullOrEmpty(pair.target) && !string.IsNullOrEmpty(pair.chaser)
            && craftById.TryGetValue(pair.target, out target) && craftById.TryGetValue(pair.chaser, out chaser);
    }

    private float TrajectoryTimeSec()
    {
        return (float)(state.trajectoryTimeBase == "onActivation"
            ? simulationSeconds - trajectoryActivationSec : simulationSeconds);
    }

    private void ApplyTrajectoryPose(SpatialCraftAssembly target, SpatialCraftAssembly chaser, float time)
    {
        if ((state.demoDocking || state.demoPrepared) && target.demoDockPort != null && chaser.demoDockPort != null)
        {
            Transform targetPort = target.demoDockPort;
            Transform chaserPort = chaser.demoDockPort;
            Vector3 offset = SampleTrajectory(state.trajectory, time);
            Quaternion variation = SampleTrajectoryRotation(state.trajectory, time);
            Quaternion facing = Quaternion.LookRotation(-targetPort.forward, targetPort.up);
            Quaternion body = facing * variation * Quaternion.Inverse(chaserPort.localRotation);
            Vector3 portPosition = targetPort.position + targetPort.right * offset.x + targetPort.up * offset.y
                + targetPort.forward * (offset.z + state.demoGapM);
            chaser.transform.SetPositionAndRotation(portPosition - body * chaserPort.localPosition, body);
            return;
        }
        Vector3 position = SampleTrajectory(state.trajectory, time);
        Quaternion rotation = ReadAttitude(state.attitude) * SampleTrajectoryRotation(state.trajectory, time);
        chaser.transform.SetPositionAndRotation(target.transform.position + position, rotation);
    }

    private void BuildTrajectoryForecast(SpatialCraftAssembly activeTarget, SpatialCraftAssembly activeChaser)
    {
        forecastDirty = false;
        trajectoryForecast.Clear();
        SpatialTrajectoryPoint[] points = state.trajectory;
        if (points == null || points.Length < 2) return;
        float start = points[0].tSec;
        float window = points[points.Length - 1].tSec - start;
        float step = state.trajectoryForecastStepSec;
        if (!IsFinite(step) || step <= 0) step = 10f;
        int sampleCount = Mathf.CeilToInt(Mathf.Clamp(window / step, 1f, 120f));
        float sampleSec = window / sampleCount;
        foreach (SpatialSensorView view in sensorViews)
        {
            if (!view.isActiveAndEnabled) continue;
            ResolveSensorTarget(FindSensorConfig(view.sensorId), activeTarget, out Transform feature,
                out Renderer[] renderers, out bool resolved, out bool useSurfaceSamples, out Transform ignored);
            if (!resolved || feature == null) continue;
            trajectoryForecast.Add(view.sensorId, new TrajectoryOcclusion {
                view = view, feature = feature, ignoredFeature = ignored,
                renderers = useSurfaceSamples ? renderers : null,
                fixedSamplePoints = !useSurfaceSamples && !feature.IsChildOf(activeChaser.transform)
                    ? TargetSamplePoints(feature) : null,
                windowSec = window, sampleSec = sampleSec
            });
        }
        if (trajectoryForecast.Count == 0) return;

        // Use the live raycast and installed models, then restore the exact current pose before rendering.
        Transform chaser = activeChaser.transform;
        Vector3 savedPosition = chaser.position;
        Quaternion savedRotation = chaser.rotation;
        float[] savedAngles = activeChaser.solarAngles == null ? null : (float[])activeChaser.solarAngles.Clone();
        float[] savedTargetAngles = activeTarget.solarAngles == null ? null : (float[])activeTarget.solarAngles.Clone();
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float sampleTime = start + (i + 0.5f) * sampleSec;
                ApplyTrajectoryPose(activeTarget, activeChaser, sampleTime);
                if (state.demoDocking)
                {
                    Vector3 forecastSun = ComputeSunEci(sampleTime, state.orbit ?? new SpatialOrbit());
                    forecastSun = new Vector3(forecastSun.x, forecastSun.z, forecastSun.y);
                    ApplySolarTracking(activeTarget, forecastSun, true);
                    ApplySolarTracking(activeChaser, forecastSun, false);
                }
                Physics.SyncTransforms();
                foreach (TrajectoryOcclusion forecast in trajectoryForecast.Values)
                {
                    Vector3[] samples = forecast.fixedSamplePoints ?? (forecast.renderers != null
                        ? TargetCraftSurfaceSamplePoints(forecast.renderers, forecast.feature, forecast.view.transform.position)
                        : TargetSamplePoints(forecast.feature));
                    forecast.view.MeasureMarkerVisibility(samples, forecast.ignoredFeature, null,
                        out int inFov, out int visible, out _);
                    if (inFov == 0) forecast.outOfFovSec += sampleSec;
                    else if (visible < inFov)
                    {
                        forecast.blockedSec += sampleSec;
                        float windowStart = start + i * sampleSec;
                        float windowEnd = windowStart + sampleSec;
                        int last = forecast.occlusionWindows.Count - 1;
                        if (last >= 0 && Mathf.Abs(forecast.occlusionWindows[last].y - windowStart) < 0.001f)
                            forecast.occlusionWindows[last] = new Vector2(forecast.occlusionWindows[last].x, windowEnd);
                        else forecast.occlusionWindows.Add(new Vector2(windowStart, windowEnd));
                        forecast.peakBlockedRatio = Mathf.Max(forecast.peakBlockedRatio, (float)(inFov - visible) / inFov);
                    }
                }
            }
        }
        finally
        {
            chaser.SetPositionAndRotation(savedPosition, savedRotation);
            activeChaser.solarAngles = savedAngles;
            activeTarget.solarAngles = savedTargetAngles;
            ApplySolarTracking(activeTarget, sunDirection, true);
            ApplySolarTracking(activeChaser, sunDirection, false);
            Physics.SyncTransforms();
        }
    }

    private void PublishTelemetry()
    {
        if (!TryGetActiveCraft(out SpatialCraftAssembly activeTarget, out SpatialCraftAssembly activeChaser))
        {
            SendError("遥测暂不可用：未配置有效的活动航天器组合");
            return;
        }
        if (forecastDirty) BuildTrajectoryForecast(activeTarget, activeChaser);
        var wings = new List<SpatialWingResult>();
        AppendWingResults(activeTarget, wings, true);
        AppendWingResults(activeChaser, wings, false);
        SpatialTelemetryMessage message = new SpatialTelemetryMessage {
            stateSeq = state.seq, demoRunId = state.demoRunId,
            sunArrowOrigin = sunDirectionLine != null ? sunDirectionLine.GetPosition(0) : Vector3.zero,
            sunArrowTip = sunDirectionLine != null ? sunDirectionLine.GetPosition(3) : Vector3.zero,
            wings = wings.ToArray(), showRays = state.showRays, playerWidth = Screen.width, playerHeight = Screen.height,
            demoDocking = state.demoDocking, demoTimeSec = state.demoDocking ? TrajectoryTimeSec() : 0,
            seq = ++telemetrySequence,
            utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            simTimeSec = (float)simulationSeconds,
            targetCraftId = activeTarget.craftId,
            chaserCraftId = activeChaser.craftId,
            relativePositionUnity = new[] { activeChaser.transform.position.x - activeTarget.transform.position.x,
                                            activeChaser.transform.position.y - activeTarget.transform.position.y,
                                            activeChaser.transform.position.z - activeTarget.transform.position.z },
            jointsDeg = state.jointsDeg ?? Array.Empty<float>(),
            solarArrayDeg = state.solarArrayDeg,
            sunDirectionUnity = new[] { sunDirection.x, sunDirection.y, sunDirection.z },
            orbitPositionKm = new[] { orbitPositionKm.x, orbitPositionKm.y, orbitPositionKm.z },
            earthShadow = earthShadow
        };
        if (state.demoDocking && state.trajectory != null && activeTarget.demoDockPort != null && activeChaser.demoDockPort != null)
        {
            Transform targetPort = activeTarget.demoDockPort;
            Transform chaserPort = activeChaser.demoDockPort;
            message.demoFinished = message.demoTimeSec >= state.trajectory[state.trajectory.Length - 1].tSec - 0.001f;
            message.dockingPositionErrorM = Vector3.Distance(chaserPort.position, targetPort.position + targetPort.forward * state.demoGapM);
            message.dockingAngleErrorDeg = Quaternion.Angle(chaserPort.rotation, Quaternion.LookRotation(-targetPort.forward, targetPort.up));
            message.dockingAligned = message.demoFinished && message.dockingPositionErrorM < 0.01f && message.dockingAngleErrorDeg < 0.5f;
        }
        List<SpatialSensorTelemetry> results = new List<SpatialSensorTelemetry>();
        foreach (SpatialSensorView view in sensorViews)
        {
            if (!view.gameObject.activeInHierarchy || view.transform == null) continue;
            SpatialSensorConfig sensorConfig = FindSensorConfig(view.sensorId);
            SpatialCraftAssembly sensorTarget = ResolveSensorTarget(sensorConfig, activeTarget,
                out Transform targetFeature, out Renderer[] targetRenderers, out bool targetResolved,
                out bool useCraftSurfaceSamples, out Transform targetFeatureToIgnore);
            Vector3[] points = useCraftSurfaceSamples
                ? TargetCraftSurfaceSamplePoints(targetRenderers, targetFeature, view.transform.position)
                : TargetSamplePoints(targetFeature);
            view.latest = view.Calculate(targetFeature, points, sunDirection, earthShadow,
                                         activeChaser.transform.rotation, message.seq, activeChaser.armJoints,
                                         targetRenderers, sensorConfig?.targetId ?? "ACTIVE_TARGET",
                                         sensorTarget.craftId, targetResolved, targetFeatureToIgnore,
                                         useCraftSurfaceSamples ? "surface" : "marker");
            if (trajectoryForecast.TryGetValue(view.sensorId, out TrajectoryOcclusion forecast))
            {
                view.latest.trajectoryForecastAvailable = true;
                view.latest.trajectoryForecastWindowSec = forecast.windowSec;
                view.latest.trajectoryForecastSampleSec = forecast.sampleSec;
                view.latest.trajectoryForecastBlockedSec = forecast.blockedSec;
                view.latest.trajectoryForecastOutOfFovSec = forecast.outOfFovSec;
                view.latest.trajectoryForecastPeakBlockedRatio = forecast.peakBlockedRatio;
                float now = TrajectoryTimeSec();
                foreach (Vector2 window in forecast.occlusionWindows)
                {
                    if (window.y <= now + forecast.sampleSec) continue;
                    view.latest.futureOcclusion = true;
                    view.latest.futureOcclusionStartSec = Mathf.Max(window.x, now + forecast.sampleSec);
                    view.latest.futureOcclusionEndSec = window.y;
                    break;
                }
            }
            view.DrawFrustum();
            results.Add(view.latest);
        }
        message.sensors = results.ToArray();
        tcpBridge?.SendJson(JsonUtility.ToJson(message));
    }

    private SpatialSensorConfig FindSensorConfig(string id)
    {
        if (state.sensors == null) return null;
        for (int i = 0; i < state.sensors.Length; i++)
            if (state.sensors[i] != null && state.sensors[i].id == id) return state.sensors[i];
        return null;
    }

    private SpatialCraftAssembly ResolveSensorTarget(SpatialSensorConfig config, SpatialCraftAssembly activeTarget,
                                                      out Transform targetFeature, out Renderer[] targetRenderers,
                                                      out bool targetResolved, out bool useCraftSurfaceSamples,
                                                      out Transform targetFeatureToIgnore)
    {
        SpatialCraftAssembly selectedCraft = activeTarget;
        targetFeature = null;
        targetRenderers = null;
        targetResolved = true;
        useCraftSurfaceSamples = false;
        targetFeatureToIgnore = null;
        string targetId = config != null ? config.targetId : null;
        if (!string.IsNullOrEmpty(targetId) && targetId != "ACTIVE_TARGET" && targetId != "DOCK-TARGET")
        {
            if (craftById.TryGetValue(targetId, out SpatialCraftAssembly configuredTarget)
                && configuredTarget != null && configuredTarget.gameObject.activeInHierarchy)
                selectedCraft = configuredTarget;
            else
            {
                foreach (SpatialCraftAssembly assembly in craft)
                {
                    if (assembly == null || !assembly.gameObject.activeInHierarchy) continue;
                    foreach (Transform candidate in assembly.GetComponentsInChildren<Transform>(true))
                    {
                        SpatialOccluderIdentity identity = candidate.GetComponent<SpatialOccluderIdentity>();
                        if (candidate.name != targetId && (identity == null || identity.partId != targetId)) continue;
                        targetFeature = candidate;
                        targetRenderers = candidate.GetComponentsInChildren<Renderer>(true);
                        targetFeatureToIgnore = candidate;
                        return assembly;
                    }
                }
                targetResolved = false;
            }
        }
        useCraftSurfaceSamples = selectedCraft.dockingPort == null;
        targetFeature = selectedCraft.dockingPort != null ? selectedCraft.dockingPort : selectedCraft.transform;
        if (!useCraftSurfaceSamples) targetFeatureToIgnore = targetFeature;
        targetRenderers = selectedCraft.GetComponentsInChildren<Renderer>(true);
        return selectedCraft;
    }

    private static Vector3[] TargetCraftSurfaceSamplePoints(Renderer[] renderers, Transform fallback,
                                                            Vector3 sensorPosition)
    {
        Bounds bounds = new Bounds();
        bool initialized = false;
        if (renderers != null)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else bounds.Encapsulate(renderer.bounds);
            }
        }
        if (!initialized) return TargetSamplePoints(fallback);

        Vector3 towardSensor = sensorPosition - bounds.center;
        int normalAxis = Mathf.Abs(towardSensor.x) >= Mathf.Abs(towardSensor.y)
            ? (Mathf.Abs(towardSensor.x) >= Mathf.Abs(towardSensor.z) ? 0 : 2)
            : (Mathf.Abs(towardSensor.y) >= Mathf.Abs(towardSensor.z) ? 1 : 2);
        int uAxis = (normalAxis + 1) % 3;
        int vAxis = (normalAxis + 2) % 3;
        float sign = SampleAxis(towardSensor, normalAxis) >= 0 ? 1 : -1;
        Vector3[] points = new Vector3[9];
        float[] fractions = { -0.35f, 0f, 0.35f };
        int index = 0;
        for (int u = 0; u < fractions.Length; u++)
        for (int v = 0; v < fractions.Length; v++)
        {
            Vector3 point = bounds.center;
            SetSampleAxis(ref point, normalAxis, SampleAxis(bounds.center, normalAxis) + sign * SampleAxis(bounds.extents, normalAxis));
            SetSampleAxis(ref point, uAxis, SampleAxis(bounds.center, uAxis) + fractions[u] * SampleAxis(bounds.size, uAxis));
            SetSampleAxis(ref point, vAxis, SampleAxis(bounds.center, vAxis) + fractions[v] * SampleAxis(bounds.size, vAxis));
            points[index++] = point;
        }
        return points;
    }

    private static float SampleAxis(Vector3 vector, int axis)
    {
        return axis == 0 ? vector.x : axis == 1 ? vector.y : vector.z;
    }

    private static void SetSampleAxis(ref Vector3 vector, int axis, float value)
    {
        if (axis == 0) vector.x = value;
        else if (axis == 1) vector.y = value;
        else vector.z = value;
    }

    private static Vector3[] TargetSamplePoints(Transform target)
    {
        Vector3 center = target.position;
        Vector3 right = target.right * 0.28f;
        Vector3 up = target.up * 0.28f;
        return new[] { center, center + right, center - right, center + up, center - up,
                       center + right + up, center + right - up, center - right + up, center - right - up };
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool IsValidTrajectory(SpatialTrajectoryPoint[] points)
    {
        if (points == null) return true;
        if (points.Length > 2048) return false;
        float previous = -1f;
        foreach (SpatialTrajectoryPoint point in points)
        {
            if (point == null || !IsFinite(point.tSec) || point.tSec < 0 || point.tSec <= previous
                || !IsFiniteVector(point.relativePositionM)
                || (point.relativeEulerDeg != null && point.relativeEulerDeg.Length != 0 && !IsFiniteVector(point.relativeEulerDeg)))
                return false;
            previous = point.tSec;
        }
        return true;
    }

    private static bool IsFiniteVector(float[] values) => values != null && values.Length == 3
        && IsFinite(values[0]) && IsFinite(values[1]) && IsFinite(values[2]);

    private static bool SameTrajectory(SpatialTrajectoryPoint[] first, SpatialTrajectoryPoint[] second)
    {
        if (first == second) return true;
        if (first == null || second == null || first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
            if (first[i].tSec != second[i].tSec || !ReadVector(first[i].relativePositionM).Equals(ReadVector(second[i].relativePositionM))
                || !ReadVector(first[i].relativeEulerDeg).Equals(ReadVector(second[i].relativeEulerDeg))) return false;
        return true;
    }

    private static int TrajectoryUpperSample(SpatialTrajectoryPoint[] points, float time, out float fraction)
    {
        fraction = 0f;
        if (points.Length == 1 || time <= points[0].tSec) return 0;
        for (int i = 1; i < points.Length; i++)
        {
            if (time > points[i].tSec) continue;
            fraction = Mathf.InverseLerp(points[i - 1].tSec, points[i].tSec, time);
            return i;
        }
        fraction = 1f;
        return points.Length - 1;
    }

    private Vector3 SampleTrajectory(SpatialTrajectoryPoint[] points, float time)
    {
        int upper = TrajectoryUpperSample(points, time, out float t);
        return Vector3.Lerp(ReadVector(points[Mathf.Max(0, upper - 1)].relativePositionM),
            ReadVector(points[upper].relativePositionM), state.demoDocking ? Mathf.SmoothStep(0, 1, t) : t);
    }

    private static Quaternion SampleTrajectoryRotation(SpatialTrajectoryPoint[] points, float time)
    {
        int upper = TrajectoryUpperSample(points, time, out float t);
        return Quaternion.Slerp(Quaternion.Euler(ReadVector(points[Mathf.Max(0, upper - 1)].relativeEulerDeg)),
            Quaternion.Euler(ReadVector(points[upper].relativeEulerDeg)), t);
    }

    private static Quaternion ReadAttitude(SpatialAttitude attitude)
    {
        if (attitude == null) return Quaternion.identity;
        if (attitude.mode == "quaternion" && attitude.quaternionWxyz != null && attitude.quaternionWxyz.Length >= 4)
            return new Quaternion(attitude.quaternionWxyz[1], attitude.quaternionWxyz[2],
                                 attitude.quaternionWxyz[3], attitude.quaternionWxyz[0]).normalized;
        return Quaternion.Euler(ReadVector(attitude.eulerDeg));
    }

    private static Vector3 ReadVector(float[] values)
    {
        return values != null && values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.zero;
    }

    private static Vector3 ComputeSunEci(float elapsedSeconds, SpatialOrbit orbit)
    {
        DateTime epoch;
        if (!DateTime.TryParse(orbit.epochUtc, CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out epoch))
            epoch = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        DateTime instant = epoch.AddSeconds(elapsedSeconds);
        double julianDateTt = instant.ToOADate() + 2415018.5 + (32.184 + orbit.leapSeconds) / 86400.0;
        double centuries = (julianDateTt - 2451545.0) / 36525.0;
        double meanLongitude = 280.46646 + 36000.76983 * centuries + 0.0003032 * centuries * centuries;
        double meanAnomaly = Radians(357.52911 + 35999.05029 * centuries - 0.0001537 * centuries * centuries);
        double equationOfCenter = (1.914602 - 0.004817 * centuries - 0.000014 * centuries * centuries)
                                  * Math.Sin(meanAnomaly)
                                  + (0.019993 - 0.000101 * centuries) * Math.Sin(2 * meanAnomaly)
                                  + 0.000289 * Math.Sin(3 * meanAnomaly);
        double solarNodeLongitude = Radians(125.04 - 1934.136 * centuries);
        double eclipticLongitude = Radians(meanLongitude + equationOfCenter - 0.00569
                                           - 0.00478 * Math.Sin(solarNodeLongitude));
        double obliquity = Radians(23.0 + 26.0 / 60.0 + 21.448 / 3600.0
                                  - (46.8150 * centuries + 0.00059 * centuries * centuries
                                     - 0.001813 * centuries * centuries * centuries) / 3600.0
                                  + 0.00256 * Math.Cos(solarNodeLongitude));
        return new Vector3((float)Math.Cos(eclipticLongitude),
                                    (float)(Math.Cos(obliquity) * Math.Sin(eclipticLongitude)),
                                    (float)(Math.Sin(obliquity) * Math.Sin(eclipticLongitude))).normalized;
    }

    private void UpdateSolarGeometry(float elapsedSeconds)
    {
        SpatialOrbit orbit = state.orbit ?? new SpatialOrbit();
        Vector3 sunEci = ComputeSunEci(elapsedSeconds, orbit);
        sunDirection = new Vector3(sunEci.x, sunEci.z, sunEci.y).normalized;
        if (sunLight != null) sunLight.transform.rotation = Quaternion.LookRotation(-sunDirection);

        double altitude = Math.Max(100, orbit.altitudeKm);
        double semiMajor = EarthRadiusKm + altitude;
        double eccentricity = Math.Max(0, Math.Min(0.8, orbit.eccentricity));
        double n = Math.Sqrt(EarthMu / (semiMajor * semiMajor * semiMajor));
        double m = Radians(orbit.meanAnomalyDeg) + n * elapsedSeconds;
        double eccentricAnomaly = m;
        for (int i = 0; i < 12; i++) eccentricAnomaly -= (eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly) - m)
                                                               / (1 - eccentricity * Math.Cos(eccentricAnomaly));
        double x = semiMajor * (Math.Cos(eccentricAnomaly) - eccentricity);
        double y = semiMajor * Math.Sqrt(1 - eccentricity * eccentricity) * Math.Sin(eccentricAnomaly);
        double omega = Radians(orbit.argPeriapsisDeg), raan = Radians(orbit.raanDeg), inclination = Radians(orbit.inclinationDeg);
        double cw = Math.Cos(omega), sw = Math.Sin(omega), co = Math.Cos(raan), so = Math.Sin(raan);
        double ci = Math.Cos(inclination), si = Math.Sin(inclination);
        orbitPositionKm = new Vector3(
            (float)((co * cw - so * sw * ci) * x + (-co * sw - so * cw * ci) * y),
            (float)((so * cw + co * sw * ci) * x + (-so * sw + co * cw * ci) * y),
            (float)(sw * si * x + cw * si * y));
        Vector3 orbitVector = orbitPositionKm;
        float alongSun = Vector3.Dot(orbitVector, sunEci.normalized);
        Vector3 perpendicular = orbitVector - alongSun * sunEci.normalized;
        earthShadow = alongSun < 0 && perpendicular.magnitude < EarthRadiusKm;
    }

    private void SendError(string text)
    {
        if (tcpBridge == null) return;
        tcpBridge.SendJson(JsonUtility.ToJson(new SpatialErrorMessage {
            seq = ++telemetrySequence, utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), message = text
        }));
    }

    private static double Radians(double degrees) { return degrees * Math.PI / 180.0; }
}

