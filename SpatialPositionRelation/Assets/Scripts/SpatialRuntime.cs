using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 把原生动力学状态和 Qt 的展示参数应用到现有空间位置计算场景。
/// MonoBehaviour 是挂在场景对象上的 Unity 组件；场景挂的是具体类 SpatialSimulation，主要实现继承自这里。
/// </summary>
public abstract class SpatialSimulationBase : MonoBehaviour
{
    /// <summary>
    /// 给轨道位置和地影判据提供统一的地球尺度：半径用千米，下方引力参数用 km³/s²。
    /// 这组常量不用于给场景里的航天器模型定尺寸；场景相对位置按米读。
    /// </summary>
    private const double EarthRadiusKm = 6378.137;
    private const double EarthMu = 398600.4418;
    /// <summary>
    /// 把仿真接到场景中已有的通信组件；后面的灯光、模型、相机等引用也由场景配置提供。
    /// Unity 序列化会保存符合条件的字段及对象引用；这里的引用不是在每一帧重新创建的对象。
    /// </summary>
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

    /// <summary>
    /// 让 Qt 知道总览相机何时完成第一帧，而不是把进程启动当作画面就绪。
    /// 这是渲染后事件的回调，不是每帧执行的 Update；记下 UTC 后解除订阅，未连接时由后续 ready 补报。
    /// </summary>
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

    /// <summary>
    /// 组件销毁时把自己从相机事件里移除，避免对象已经失效还被回调。
    /// OnDestroy 是 Unity 生命周期钩子，即引擎在特定阶段自动调用的方法；-= 表示解除订阅。
    /// </summary>
    protected virtual void OnDestroy()
    {
        Camera.onPostRender -= RecordFirstFrame;
        ClearForecast();
        ClearArmPreview();
        dynamics?.Dispose();
    }
    private float telemetryTimer;
    private long telemetrySequence;
    private Vector3 sunDirection = Vector3.up;
    private Vector3 orbitPositionKm;
    private bool earthShadow;
    private SpatialDynamicsAdapter dynamics;
    private string dynamicsError;
    private SpatialOcclusionForecast forecast;
    private bool forecastFailed;
    private const string GrappleTargetId = "GRAPPLE-FIXTURE";
    private bool demoStarted;
    private SpatialArmPreview armPreview;

    private void ClearForecast()
    {
        forecast?.Dispose();
        forecast = null;
        forecastFailed = false;
    }

    private void UpdateForecast(SpatialCraftAssembly station, SpatialCraftAssembly visitor)
    {
        if (!state.demoDocking || dynamics == null || dynamicsError != null || dynamics.Finished || forecastFailed) return;
        try
        {
            if (forecast == null)
                forecast = new SpatialOcclusionForecast(dynamics, station, visitor, sunDirection, ApplySolarTracking);
            forecast.Work(12, EvaluateForecastOcclusion);
        }
        catch (Exception exception)
        {
            ClearForecast();
            forecastFailed = true;
            SendError("未来遮挡预测中断：" + exception.Message);
        }
    }

    private static Vector2Int EvaluateForecastOcclusion(SpatialSensorView view,
        SpatialCraftAssembly station, SpatialCraftAssembly visitor)
    {
        ResolveSensorTarget(view.configuration, visitor, station,
            out Transform feature, out Renderer[] renderers, out _, out bool surface, out Transform ignore);
        Vector3[] points = surface ? TargetCraftSurfaceSamplePoints(renderers, feature, view.transform.position)
            : TargetSamplePoints(feature, TargetHalfSpan(view.configuration, feature, visitor));
        return view.CountOcclusion(points, ignore);
    }

    private static bool SameOcclusionConfiguration(SpatialStateMessage a, SpatialStateMessage b)
    {
        if (a.demoRunId != b.demoRunId || a.manualSolarDiagnostic != b.manualSolarDiagnostic ||
            a.manualSolarRole != b.manualSolarRole || a.solarArrayDeg != b.solarArrayDeg) return false;
        var left = a.sensors ?? Array.Empty<SpatialSensorConfig>();
        var right = b.sensors ?? Array.Empty<SpatialSensorConfig>();
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (JsonUtility.ToJson(left[i]) != JsonUtility.ToJson(right[i])) return false;
        return JsonUtility.ToJson(a.orbit) == JsonUtility.ToJson(b.orbit);
    }

    /// <summary>
    /// 运行开始时找到通信组件，按 ID 登记两器和传感器，并用默认状态布置场景。
    /// Start 由 Unity 在首次 Update 前调用；字典只登记每个 ID 的第一项，+= 把首帧处理函数订阅到相机事件。
    /// </summary>
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
        try {
            dynamics = new SpatialDynamicsAdapter(craftById[SpatialDynamicsAdapter.StationId], craftById[SpatialDynamicsAdapter.VisitorId]);
        }
        catch (Exception exception) {
            dynamicsError = "7DOF 无法启动：" + exception.Message;
            Debug.LogError(dynamicsError);
        }
        UpdateSolarGeometry(0);
        SetSunVisuals(false);
        ApplyState();
        Debug.Log("SPATIAL_SCENE_READY utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 每帧先收参数，再推进仿真、更新两器和太阳方向，最后按间隔回报结果。
    /// 主线程每帧最多收 16 条报文。轨道日照使用独立倍率，动力学适配器按墙钟累加固定原生子步。
    /// 仿真秒以 double 累加、传给部分函数时显式转为 float；遥测另用不受 Unity 时间缩放影响的 unscaledDeltaTime 累计，间隔 0.2 秒。
    /// Physics.SyncTransforms 把刚改的场景位置和旋转同步给物理系统，供随后射线查询使用。
    /// </summary>
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
        simulationSeconds += Time.deltaTime * timeScale;
        if (TryGetActiveCraft(out var forecastTarget, out var forecastChaser))
            UpdateForecast(forecastChaser, forecastTarget);
        if (dynamics != null && dynamicsError == null && state.demoDocking)
        {
            try {
                if (dynamics.Advance(Time.unscaledDeltaTime, state.dynamicsSpeed)) dynamics.Apply();
            }
            catch (Exception exception) {
                dynamicsError = "7DOF 运行中断：" + exception.Message;
                SendError(dynamicsError);
            }
        }
        UpdateSolarGeometry((float)simulationSeconds);
        if (TryGetActiveCraft(out var target, out var chaser))
        {
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

    /// <summary>
    /// 按协议分流一行 JSON，不让坏状态替换当前仿真输入。
    /// protocol/type 区分 SPR/1 的 state、captureSensor 和 shutdown；退出另对照实例令牌。
    /// demoRunId 改变时重建原生会话，播放和暂停继续使用当前会话。
    /// </summary>
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
            if (incoming?.protocol == "SPR/1" && incoming.type == "presentation")
            {
                ApplyPresentation(JsonUtility.FromJson<SpatialPresentationMessage>(line));
                return;
            }
            if (incoming == null || incoming.protocol != "SPR/1" || incoming.type != "state")
            {
                SendError("协议版本或消息类型无效");
                return;
            }
            if (!(incoming.dynamicsSpeed >= 0.25 && incoming.dynamicsSpeed <= 16)) incoming.dynamicsSpeed = 1;
            if (state.demoRunId != incoming.demoRunId)
            {
                ClearArmPreview();
                demoStarted = false;
                if (dynamics != null)
                {
                    dynamics.Reset();
                    dynamicsError = null;
                }
            }
            if (incoming.demoDocking)
            {
                ClearArmPreview();
                demoStarted = true;
            }
            if (!SameOcclusionConfiguration(state, incoming)) ClearForecast();
            state = incoming;
            forecast?.SetPaused(!state.demoDocking);
            ApplyState();
        }
        catch (Exception exception)
        {
            SendError("状态 JSON 无法解析：" + exception.Message);
        }
    }

    private void ClearArmPreview()
    {
        armPreview?.Dispose();
        armPreview = null;
    }

    private void SetSunVisuals(bool visible)
    {
        if (sunDisc != null) sunDisc.gameObject.SetActive(visible);
        if (sunDirectionLine != null) sunDirectionLine.enabled = visible;
        if (embeddedViews != null) embeddedViews.ShowSunVector = visible;
    }

    private void ApplyPresentation(SpatialPresentationMessage presentation)
    {
        if (presentation == null || presentation.demoRunId != state.demoRunId) return;
        SetSunVisuals(presentation.showSunVector);
        if (dynamics == null || dynamicsError != null || state.demoDocking || demoStarted && !dynamics.Finished ||
            !SpatialArmPreview.ValidAngles(presentation.previewJointDegrees) ||
            !TryGetActiveCraft(out _, out var station) || overviewCamera == null)
        {
            ClearArmPreview();
            return;
        }
        armPreview ??= dynamics.CreateOverviewPreview(overviewCamera);
        armPreview.SetAngles(presentation.previewJointDegrees);
    }

    /// <summary>
    /// 按请求的传感器拍一帧，并原样带回 seq 和 sensorId，方便 Qt 对上请求和响应。
    /// 没有可拍的相机时返回 error；成功时通过 Base64 把 PNG 的二进制字节表示成 JSON 可携带的文本。
    /// </summary>
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

    /// <summary>
    /// 临时把相机画面绘进纹理，导出 PNG 后恢复相机原设置。
    /// 截图长边 640 像素，宽高比取水平和垂直半视场正切之比，两个方向至少 1 像素。
    /// finally 表示无论成功还是抛出异常都执行，负责恢复相机目标并释放临时纹理。
    /// </summary>
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

    /// <summary>
    /// 按输入摆放目标器、追踪器、机械臂和传感器，给几何计算准备当前场景。
    /// 两器根节点及七关节取原生样本，再沿用现有敏感器、日照和总览计算。
    /// 传感器的 mount 选择所属父物体，modelSurface 还要求找到匹配的安装点。
    /// 没找到有效组合时先回退到默认组合；如果默认组合也缺失就报错，不继续设置。
    /// </summary>
    private void ApplyState()
    {
        state.activePair = new SpatialPair { target = SpatialDynamicsAdapter.VisitorId, chaser = SpatialDynamicsAdapter.StationId };
        if (!TryGetActiveCraft(out var target, out var chaser)) return;
        dockingTarget = target.dockingPort;
        foreach (SpatialCraftAssembly assembly in craft) assembly.gameObject.SetActive(assembly == target || assembly == chaser);
        if (embeddedViews != null)
            embeddedViews.ApplySettings(state.overviewMode, state.displaySensorIds);

        dynamics?.Apply();
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

    /// <summary>
    /// 把总览镜头和太阳箭头摆到容易观察的位置，而不是改变航天器的轨道位置。
    /// 焦点取从目标到追踪器的 48% 插值；镜头偏移随距离调整，53° 视场和箭头、日盘位置用于显示。
    /// </summary>
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
    /// <summary>
    /// 在演示限位内找一个最朝向太阳的翼面转角。
    /// 投影的长度平方小于 1e-6 时直接保留上次角度；超过 ±90° 时比较两个端点的点积，而不是直接截断原角度。
    /// 两端点积分差小于 1e-5 时沿用上次的符号（零归为正）。点积这里用来比较翼面法线朝太阳的程度。
    /// </summary>
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

    /// <summary>
    /// 把世界向日方向换成太阳翼零位坐标，再用算出的角度转动铰链。
    /// 先转到铰链父坐标，再用零位旋转的逆变换去掉初始朝向；方向变换不同于位置变换，不加平移。
    /// 手动模式只给被选角色套用限制在 ±90° 的输入角，其余仍按向日方向计算。
    /// </summary>
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

    /// <summary>
    /// 把每个有绑定的太阳翼的转角、转轴和受光面方向装进遥测。
    /// 转轴和法线用世界坐标；入射角是世界法线与向日方向的夹角，单位是度，不是翼的转角。
    /// </summary>
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

    /// <summary>
    /// 把当前一帧的两器状态和每个启用传感器的结果打包送回 Qt。
    /// 相对位置是追踪器世界位置减目标器世界位置，轨道位置另用千米；stateSeq 对应输入，seq 是 Unity 输出序号。
    /// 演示同时量对接口位置差和旋转夹角；时间距末点 0.001 秒内算到终点，判为对准还要求误差小于 0.01 米且小于 0.5°。
    /// </summary>
    private void PublishTelemetry()
    {
        if (!TryGetActiveCraft(out SpatialCraftAssembly activeTarget, out SpatialCraftAssembly activeChaser))
        {
            SendError("遥测暂不可用：未配置有效的活动航天器组合");
            return;
        }
        SpatialDynamicsTelemetry native = dynamicsError == null ? dynamics?.Telemetry() : null;
        var wings = new List<SpatialWingResult>();
        AppendWingResults(activeTarget, wings, true);
        AppendWingResults(activeChaser, wings, false);
        SpatialTelemetryMessage message = new SpatialTelemetryMessage {
            stateSeq = state.seq, demoRunId = state.demoRunId,
            sunArrowOrigin = sunDirectionLine != null ? sunDirectionLine.GetPosition(0) : Vector3.zero,
            sunArrowTip = sunDirectionLine != null ? sunDirectionLine.GetPosition(3) : Vector3.zero,
            wings = wings.ToArray(), showRays = state.showRays, playerWidth = Screen.width, playerHeight = Screen.height,
            demoDocking = state.demoDocking, demoTimeSec = (float)(native?.timeSec ?? 0),
            demoFinished = dynamics?.Finished ?? false, dynamics = native, dynamicsError = dynamicsError,
            seq = ++telemetrySequence,
            utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            simTimeSec = (float)simulationSeconds,
            targetCraftId = activeTarget.craftId,
            chaserCraftId = activeChaser.craftId,
            relativePositionUnity = new[] { activeChaser.transform.position.x - activeTarget.transform.position.x,
                                            activeChaser.transform.position.y - activeTarget.transform.position.y,
                                            activeChaser.transform.position.z - activeTarget.transform.position.z },
            jointsDeg = native != null ? dynamics.JointDegrees : Array.Empty<float>(),
            solarArrayDeg = state.solarArrayDeg,
            sunDirectionUnity = new[] { sunDirection.x, sunDirection.y, sunDirection.z },
            orbitPositionKm = new[] { orbitPositionKm.x, orbitPositionKm.y, orbitPositionKm.z },
            earthShadow = earthShadow
        };
        if (native != null)
        {
            message.dockingAligned = native.mode == 2;
            message.dockingPositionErrorM = (float)native.portDistanceM;
            message.dockingAngleErrorDeg = Quaternion.Angle(native.stationPort.rotation, native.visitorPort.rotation);
        }
        List<SpatialSensorTelemetry> results = new List<SpatialSensorTelemetry>();
        foreach (SpatialSensorView view in sensorViews)
        {
            if (!view.gameObject.activeInHierarchy || view.transform == null) continue;
            SpatialSensorConfig sensorConfig = FindSensorConfig(view.sensorId);
            SpatialCraftAssembly sensorTarget = ResolveSensorTarget(sensorConfig, activeTarget, activeChaser,
                out Transform targetFeature, out Renderer[] targetRenderers, out bool targetResolved,
                out bool useCraftSurfaceSamples, out Transform targetFeatureToIgnore);
            Vector3[] points = useCraftSurfaceSamples
                ? TargetCraftSurfaceSamplePoints(targetRenderers, targetFeature, view.transform.position)
                : TargetSamplePoints(targetFeature, TargetHalfSpan(sensorConfig, targetFeature, sensorTarget));
            view.latest = view.Calculate(targetFeature, points, sunDirection, earthShadow,
                                         activeChaser.transform.rotation, message.seq, activeChaser.armJoints,
                                         targetRenderers, sensorConfig?.targetId ?? "ACTIVE_TARGET",
                                         sensorTarget.craftId, targetResolved, targetFeatureToIgnore,
                                         useCraftSurfaceSamples ? "surface" : "marker");
            if (native != null) forecast?.Fill(view.latest, native.timeSec);
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

    /// <summary>
    /// 把报文里的目标 ID 变成可用于采样的飞行器或部件。
    /// ACTIVE_TARGET 和 DOCK-TARGET 沿用活动目标；其他 ID 首先匹配活动飞行器，再查部件名或 partId。
    /// 查不到会回退到活动目标，同时把 targetResolved 置为 false；首选对接标靶，无标靶时用机体包围盒采样。
    /// </summary>
    private static SpatialCraftAssembly ResolveSensorTarget(SpatialSensorConfig config, SpatialCraftAssembly activeTarget, SpatialCraftAssembly activeChaser,
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
        if (targetId == GrappleTargetId && activeTarget.grappleDatum != null)
        {
            targetFeature = activeTarget.grappleDatum;
            targetFeatureToIgnore = targetFeature.parent;
            targetRenderers = targetFeatureToIgnore.GetComponentsInChildren<Renderer>();
            return activeTarget;
        }
        if (!string.IsNullOrEmpty(targetId) && targetId != "ACTIVE_TARGET" && targetId != "DOCK-TARGET")
        {
            if (targetId == activeChaser.craftId) selectedCraft = activeChaser;
            else if (targetId != activeTarget.craftId)
            {
                foreach (SpatialCraftAssembly assembly in new[] { activeChaser, activeTarget })
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

    /// <summary>
    /// 用模型外面的一个大盒子代替真实曲面，挑朝传感器的那一面放 3×3 个采样点。
    /// 合并启用且激活的 Renderer 世界轴对齐包围盒；传感器方向绝对值最大的分量选面轴，符号选正面或反面。
    /// 盒面内两轴用边长的 −35%、0、35% 从中心取点；没有可用盒子就改取标靶点。
    /// </summary>
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

    /// <summary>
    /// 在标靶平面上放九个测试点，供视场和遮挡逐点检查。
    /// 对接靶标固定半跨距 0.28 m。捕获座按其示意标记配置取样，坐标单位为 m。
    /// </summary>
    private static Vector3[] TargetSamplePoints(Transform target, float halfSpan = 0.28f)
    {
        Vector3 center = target.position;
        Vector3 right = target.right * halfSpan;
        Vector3 up = target.up * halfSpan;
        return new[] { center, center + right, center - right, center + up, center - up,
                       center + right + up, center + right - up, center - right + up, center - right - up };
    }

    private static float TargetHalfSpan(SpatialSensorConfig config, Transform feature, SpatialCraftAssembly craft)
    {
        return config?.targetId == GrappleTargetId && feature == craft.grappleDatum
            && IsFinite(config.sampleRadiusM) && config.sampleRadiusM > 0f ? config.sampleRadiusM : 0.28f;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>
    /// 用历元加仿真秒估计当时从地球指向太阳的单位方向，输出 ECI 分量。
    /// ECI 是以地心为原点、不随地表每天转一圈的惯性坐标；这里先把 UTC 换成儒略日（连续按日计时），再把闰秒和 32.184 秒加入作为 TT（地球时）。
    /// 2451545.0 是 J2000 参考儒略日，36525 是每儒略世纪天数；用世纪数求太阳经度和黄赤交角（黄道与赤道夹角），再投到赤道坐标。
    /// 未找到这里近似式各系数的来源说明；日期解析失败则用 2026-11-21 00:00:00 UTC。
    /// </summary>
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

    /// <summary>
    /// 更新场景里的向日方向，并用地心轨道位置判断飞行器是否进入地影。
    /// 向日方向从 ECI 转到 Unity 时交换 Y/Z；灯光朝反方向表示光从太阳射向场景；地影比较仍在 ECI 中做。
    /// 半长轴取地球半径加至少 100 km 的输入高度，偏心率限制在 0 到 0.8；平均角速度 n=√(μ/a³)，平近点角 M=M₀+n·t。
    /// 用 12 次牛顿迭代近似解 E−e·sin(E)=M，再用 x=a(cos(E)−e)、y=a√(1−e²)sin(E) 得轨道平面位置，经近地点幅角、倾角、升交点赤经转进 ECI。
    /// 旋转结果显式转为 Vector3 的 float 分量；沿太阳方向点积为负且离太阳轴小于地球半径才判为地影，相当于圆柱阴影近似。
    /// </summary>
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


