using System;
using UnityEngine;

[Serializable]
public sealed class SpatialVector3
{
    public float[] value = new float[3];
}

/// <summary>
/// 给这次计算选出目标器与追踪器；target 是参考对象，chaser 是相对它运动的对象。
/// Serializable 表示这类数据可以被序列化，也就是转换成可保存或发送的表示；这里由 Unity 的 JsonUtility 与 JSON 互转。
/// </summary>
[Serializable]
public sealed class SpatialPair
{
    public string target = "CrewVehicle";
    public string chaser = "SpaceStation";
}

/// <summary>
/// 集中保存轨道传播和太阳方向估算读取的输入字段。
/// altitudeKm 用千米，各轨道角用度，timeScale 是仿真时间倍率；epochUtc 是起算时刻的 UTC 文本，leapSeconds 是闰秒数。
/// </summary>
[Serializable]
public sealed class SpatialOrbit
{
    public double altitudeKm = 600;
    public double eccentricity = 0.001;
    public double inclinationDeg = 51.6;
    public double raanDeg;
    public double argPeriapsisDeg;
    public double meanAnomalyDeg;
    public double timeScale = 60;
    public double leapSeconds = 37;
    public string epochUtc = "2026-11-21T00:00:00Z";
}

/// <summary>
/// 让发端用绕轴角度或四元数指定追踪器的朝向。
/// mode 选择表示法；eulerDeg 按 X、Y、Z 填角度，quaternionWxyz 把标量 W 放第一位，接收时会重排。
/// </summary>
[Serializable]
public sealed class SpatialAttitude
{
    public string mode = "euler";
    public float[] eulerDeg = { 0, 0, 0 };
    public float[] quaternionWxyz = { 1, 0, 0, 0 };
}

/// <summary>
/// 让 Qt 逐个启停传感器并设置安装、视场、目标和告警阈值。
/// mount 选父物体，positionReference 为 modelSurface 时还需模型安装点；偏移用米，欧拉角和水平/垂直视场用度。
/// warningThreshold 与归一化光照比例比较；targetId 用于查找活动目标器或具体部件。
/// </summary>
[Serializable]
public sealed class SpatialSensorConfig
{
    public string id;
    public string name;
    public string kind;
    public bool enabled = true;
    public string mount = "ChaserHull";
    public string positionReference;
    public float[] localPositionM = { 0, 0, 0 };
    public float[] localEulerDeg = { 0, 90, 0 };
    public float horizontalFovDeg = 32;
    public float verticalFovDeg = 32;
    public bool warningEnabled;
    public float warningThreshold = 0.3f;
    public string targetId = "ACTIVE_TARGET";
    public float sampleRadiusM = 0.35f;
}

/// <summary>
/// 定义 Qt 发给 Unity 的完整配置快照，拖动控件后会重新提交一份。
/// SPR/1 state 用 demoDocking 和 dynamicsSpeed 控制原生会话播放，demoRunId 改变时复位。
/// solarArrayDeg 是独立的手动检查角。旧关节、距离、姿态、配对和轨迹字段只兼容报文读取。
/// 7DOF 姿态、关节和相对位置的来源是原生会话。
/// </summary>
[Serializable]
public sealed class SpatialStateMessage
{
    public string protocol;
    public string type;
    public long seq;
    public string utc;
    public float relativeDistanceM = 20;
    public SpatialPair activePair = new SpatialPair();
    public float[] jointsDeg = new float[7];
    public float solarArrayDeg;
    public bool showRays;
    public bool manualSolarDiagnostic;
    public string manualSolarRole, raySensorId;
    public int demoRunId;
    public bool demoDocking;
    public double dynamicsSpeed = 1;
    public bool demoPrepared;
    public float demoGapM = 0.12f;
    public SpatialOrbit orbit = new SpatialOrbit();
    public SpatialAttitude attitude = new SpatialAttitude();
    public SpatialSensorConfig[] sensors = Array.Empty<SpatialSensorConfig>();
    public string viewSensorId;
    public string overviewMode = "fixed";
    public string[] displaySensorIds = Array.Empty<string>();
    public SpatialTrajectoryPoint[] trajectory;
    public string trajectoryTimeBase;
    public float trajectoryForecastStepSec = 10f;
}

// Presentation has a separate envelope so state, telemetry and native ABI retain their semantics.
[Serializable]
public sealed class SpatialPresentationMessage
{
    public int demoRunId;
    public bool showSunVector;
    public float[] previewJointDegrees;
}

/// <summary>
/// 记录轨迹上某个仿真时刻的相对位置与姿态。
/// tSec 用秒，relativePositionM 用米，relativeEulerDeg 用度；对接演示的位置分量对应目标口的右、上、前轴。
/// </summary>
[Serializable]
public sealed class SpatialTrajectoryPoint
{
    public float tSec;
    public float[] relativePositionM = { 0, 0, 20 };
    public float[] relativeEulerDeg = { 0, 0, 0 };
}

/// <summary>
/// 告诉 Qt 挡住视线的是哪个部件，便于在表格里说明来源。
/// partId 是部件标识，ownerCraftId 是在命中物体的父层级找到的飞行器标识；找不到时可为空。
/// </summary>
[Serializable]
public sealed class SpatialBlockerData
{
    public string partId;
    public string displayName;
    public string category, ownerCraftId, partName;
}

/// <summary>
/// 把一个传感器算出的可见度、受光和遮挡预测回传给 Qt。
/// visibleRatio 是可见点数除以视场内点数；两个面积比例都以总投影面积权重为分母；面积近似和样点计数是两组量。
/// normalizedIllumination 是地影、入口法线点积和日面可见率合成的无量纲比例。时间字段用仿真秒，角度用度，位置和误差按米读。
/// </summary>
[Serializable]
public sealed class SpatialSensorTelemetry
{
    public string id;
    public Vector3 opticalCenter, installationPoint, configuredOpticalCenter, frustumVertex;
    public string installationPath;
    public float installationToCameraErrorM;
    public float frustumOriginErrorM;
    public string targetId;
    public string targetCraftId;
    public bool targetResolved;
    public string status;
    public int totalPointCount, inFovCount;
    public string pointKind, illuminationReason;
    public bool illuminationAvailable;
    public float incidenceCosine, sunVisibleFraction;
    public int visibleCount;
    public float visibleRatio;
    public int targetProjectedAreaSampleCount;
    public int targetAreaInFovSampleCount;
    public int visibleTargetAreaSampleCount;
    public float targetAreaInFovRatio;
    public float visibleTargetAreaRatio;
    public SpatialBlockerData[] blockers = Array.Empty<SpatialBlockerData>();
    public float sunIncidenceDeg;
    public int sunSampleCount;
    public int sunVisibleSampleCount;
    public float sunOcclusionRatio;
    public string[] sunBlockerIds = Array.Empty<string>();
    public float normalizedIllumination;
    public bool warning;
    public string recommendation;
    public bool trajectoryForecastAvailable, trajectoryForecastComplete;
    public float trajectoryForecastThroughSec;
    public float trajectoryForecastWindowSec;
    public float trajectoryForecastSampleSec;
    public float trajectoryForecastBlockedSec;
    public float trajectoryForecastOutOfFovSec;
    public float trajectoryForecastPeakBlockedRatio;
    public bool futureOcclusion;
    public float futureOcclusionStartSec;
    public float futureOcclusionEndSec;
}

/// <summary>
/// 把 Unity 当前两器状态和传感器结果打包成一条遥测回传消息。
/// seq 是 Unity 输出序号，stateSeq 对应 Qt 的输入序号，demoRunId 关联演示轮次；utc 是发送时刻，simTimeSec 是仿真秒。
/// relativePositionUnity 按 Unity 世界轴给出米制位置差；sunDirectionUnity 是单位方向；orbitPositionKm 是 ECI 轨道位置，单位千米。
/// </summary>
[Serializable]
public sealed class SpatialTelemetryMessage
{
    public string protocol = "SPR/1";
    public string type = "telemetry";
    public long seq;
    public string utc;
    public float simTimeSec;
    public string targetCraftId;
    public string chaserCraftId;
    public float[] relativePositionUnity = new float[3];
    public float[] jointsDeg = new float[7];
    public float solarArrayDeg;
    public SpatialWingResult[] wings;
    public bool showRays;
    public int playerWidth, playerHeight;
    public bool demoDocking, demoFinished, dockingAligned;
    public float demoTimeSec, dockingPositionErrorM, dockingAngleErrorDeg;
    public long stateSeq;
    public int demoRunId;
    public Vector3 sunArrowOrigin, sunArrowTip;
    public float[] sunDirectionUnity = new float[3];
    public float[] orbitPositionKm = new float[3];
    public SpatialDynamicsTelemetry dynamics;
    public string dynamicsError;
    public bool earthShadow;
    public SpatialSensorTelemetry[] sensors = Array.Empty<SpatialSensorTelemetry>();
}

/// <summary>
/// 把每片已绑定太阳翼的方向和角度回传，供 Qt 展示自动或手动状态。
/// angleDeg 是铰链转角，incidenceDeg 是受光面法线与向日方向的夹角，二者均用度；轴和法线以世界坐标表示。
/// </summary>
[Serializable]
public sealed class SpatialWingResult
{
    public string craftId, role, hinge;
    public bool manual;
    public Vector3 faceNormalWorld;
    public float angleDeg, incidenceDeg;
    public Vector3 axisWorld;
}

/// <summary>
/// 让两端确认连接实例、播放器已就绪或首帧已经画出，并支持按实例请求退出。
/// instanceToken 随 Qt 启动 Unity 的参数传递，ready 带回令牌和可能已经发生的首帧信息；shutdown 接收时也会比对令牌。
/// </summary>
[Serializable]
public sealed class SpatialLifecycleMessage
{
    public string protocol = "SPR/1";
    public string type;
    public string instanceToken;
    public string utc;
    public bool firstFrameRendered;
    public string firstFrameUtc;
}

/// <summary>
/// 把 Unity 检出的状态报文问题回报给 Qt 状态栏。
/// message 是给人看的提示，seq 使用 Unity 的输出计数，与 Qt 的 state 序号独立。
/// </summary>
[Serializable]
public sealed class SpatialErrorMessage
{
    public string protocol = "SPR/1";
    public string type = "error";
    public long seq;
    public string utc;
    public string message;
}

/// <summary>
/// 把 Unity 视图菜单的选择回传给 Qt 保存。
/// name 为 viewSettings 时传 overviewMode 与 displaySensorIds 两项；seq 由视图组件自己计数。
/// </summary>
[Serializable]
public sealed class SpatialUiEventMessage
{
    public string protocol = "SPR/1";
    public string type = "uiEvent";
    public long seq;
    public string utc;
    public string name;
    public string overviewMode;
    public string[] displaySensorIds = Array.Empty<string>();
}

/// <summary>
/// 用同一组字段请求或回报传感器截图。
/// type 区分 captureSensor 与 sensorFrame；seq 与 sensorId 用于配对，pngBase64 用文本携带 PNG，失败时填 error。
/// </summary>
[Serializable]
public sealed class SpatialCaptureMessage
{
    public string protocol = "SPR/1";
    public string type;
    public long seq;
    public string utc;
    public string sensorId;
    public string pngBase64;
    public string error;
}

[Serializable]
public sealed class SpatialDynamicsTelemetry
{
    public double timeSec, portDistanceM, centerDistanceM, axialGapM, toolDistanceM, lockRequestSettingSec;
    public int mode;
    public double[] jointAnglesRad, eventTimesSec;
    public Pose stationCenter, visitorCenter, stationPort, visitorPort, tool, appliedTool;
    public Pose[] jointFrames, appliedJointFrames;
    public Vector3 grapple, stationPortVelocity, visitorPortVelocity, stationCenterVelocity, visitorCenterVelocity;
    public Vector3 appliedStationPort, appliedVisitorPort, appliedStationFace, appliedVisitorFace;
    public Vector3 appliedGripper, appliedGrapple, basePivot, baseFoot, baseSeat, grappleFoot, grappleSeat;
    public Vector3[] linkStarts, linkEnds;
}


