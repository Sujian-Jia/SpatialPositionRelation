using System;
using UnityEngine;

[Serializable]
public sealed class SpatialVector3
{
    public float[] value = new float[3];
}

[Serializable]
public sealed class SpatialPair
{
    public string target = "SpaceStation";
    public string chaser = "CrewVehicle";
}

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

[Serializable]
public sealed class SpatialAttitude
{
    public string mode = "euler";
    public float[] eulerDeg = { 0, 0, 0 };
    public float[] quaternionWxyz = { 1, 0, 0, 0 };
}

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

[Serializable]
public sealed class SpatialTrajectoryPoint
{
    public float tSec;
    public float[] relativePositionM = { 0, 0, 20 };
    public float[] relativeEulerDeg = { 0, 0, 0 };
}

[Serializable]
public sealed class SpatialBlockerData
{
    public string partId;
    public string displayName;
    public string category, ownerCraftId, partName;
}

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
    public bool trajectoryForecastAvailable;
    public float trajectoryForecastWindowSec;
    public float trajectoryForecastSampleSec;
    public float trajectoryForecastBlockedSec;
    public float trajectoryForecastOutOfFovSec;
    public float trajectoryForecastPeakBlockedRatio;
    public bool futureOcclusion;
    public float futureOcclusionStartSec;
    public float futureOcclusionEndSec;
}

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
    public bool earthShadow;
    public SpatialSensorTelemetry[] sensors = Array.Empty<SpatialSensorTelemetry>();
}

[Serializable]
public sealed class SpatialWingResult
{
    public string craftId, role, hinge;
    public bool manual;
    public Vector3 faceNormalWorld;
    public float angleDeg, incidenceDeg;
    public Vector3 axisWorld;
}

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

[Serializable]
public sealed class SpatialErrorMessage
{
    public string protocol = "SPR/1";
    public string type = "error";
    public long seq;
    public string utc;
    public string message;
}

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
