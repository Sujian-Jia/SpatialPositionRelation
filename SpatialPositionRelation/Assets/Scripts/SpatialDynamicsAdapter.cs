using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Owns one native scenario and maps its right-handed SI state to the Unity scene.</summary>
public sealed class SpatialDynamicsAdapter : IDisposable
{
    public const string StationId = "SpaceStation";
    public const string VisitorId = "CrewVehicle";
    private const double Step = 0.001;
    private const int SampleSteps = 20;
    private const int TotalSteps = 80000;
    private const int FrameCount = 168;
    private const string Library = "SpatialNative7Dof";
    private const int TimeIndex = 0, Mode = 1, Q = 2, U = 41;
    private const int StationPort = 78, VisitorPort = 85, StationPortVelocity = 92, VisitorPortVelocity = 95;
    private const int JointFrames = 98, Tool = 147, Grapple = 154, PortDistance = 157, CenterDistance = 158;
    private const int Gap = 159, ToolDistance = 160, Events = 161, LockRequestSetting = 167;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Spr7_Abi();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Spr7_Create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Spr7_Destroy(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Spr7_Advance(IntPtr session, int maxSteps);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Spr7_Read(IntPtr session, [Out] double[] frame, int frameBytes);

    private IntPtr session;
    private int steps;
    private double pendingSeconds;
    private readonly double[] frame = new double[FrameCount];
    private readonly SpatialCraftAssembly station;
    private readonly SpatialCraftAssembly visitor;
    private readonly Transform toolFrame;
    private readonly Transform grappleFrame;
    private readonly Transform gripperVisual;
    private readonly Quaternion gripperOffset;
    private readonly VisualSegment[] armSegments = new VisualSegment[6];
    private readonly Pose[] armPoses = new Pose[7];

    // The CAD chains store incoming offsets; the native link frame owns the following segment.
    // Keep joint/camera nodes on native frames and register the six imported rigid link meshes separately.
    private sealed class VisualSegment
    {
        private readonly Transform mount;
        private readonly float sourceLength;

        public VisualSegment(Transform armRoot, Transform from, Transform to)
        {
            Vector3 axis = to.position - from.position;
            sourceLength = axis.magnitude;
            if (sourceLength < 1e-5f) throw new InvalidOperationException("参考机械臂包含零长度臂段");
            mount = new GameObject("参考臂段 " + from.name).transform;
            mount.SetParent(armRoot, false);
            mount.SetPositionAndRotation(from.position, SegmentRotation(axis, from.rotation));
            for (int i = from.childCount - 1; i >= 0; i--)
            {
                Transform child = from.GetChild(i);
                if (child != to && child.GetComponentInChildren<Renderer>(true) != null)
                    child.SetParent(mount, true);
            }
            if (mount.childCount == 0) throw new InvalidOperationException("参考机械臂段缺少网格：" + from.name);
        }

        public void Apply(Pose from, Vector3 to)
        {
            Vector3 axis = to - from.position;
            mount.SetPositionAndRotation(from.position, SegmentRotation(axis, from.rotation));
            mount.localScale = new Vector3(1, 1, axis.magnitude / sourceLength);
        }

        private static Quaternion SegmentRotation(Vector3 axis, Quaternion joint)
        {
            Vector3 up = Vector3.ProjectOnPlane(joint * Vector3.forward, axis);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(joint * Vector3.up, axis);
            return Quaternion.LookRotation(axis, up);
        }
    }
    public bool Finished => steps == TotalSteps;
    public float[] JointDegrees { get; } = new float[7];

    public SpatialDynamicsAdapter(SpatialCraftAssembly station, SpatialCraftAssembly visitor)
    {
        this.station = station;
        this.visitor = visitor;
        if (station.armJoints.Length != 7 || visitor.armJoints.Length != 7)
            throw new InvalidOperationException("参考装配缺少七关节绑定");
        if (Spr7_Abi() != 1) throw new InvalidOperationException("7DOF 接口版本不匹配");
        // Bind the copied CAD gripper and its actual renderer, downstream of J7.
        Transform wrist = station.armJoints[6];
        foreach (Transform part in wrist.GetComponentsInChildren<Transform>(true))
            if (part.name == "夹爪") { gripperVisual = part; break; }
        if (gripperVisual == null) throw new InvalidOperationException("参考装配缺少 J7 的夹爪部件");
        Renderer[] jaws = gripperVisual.GetComponentsInChildren<Renderer>(true);
        if (jaws.Length == 0) throw new InvalidOperationException("参考夹爪缺少网格");
        Bounds jawBounds = jaws[0].bounds;
        foreach (Renderer jaw in jaws) jawBounds.Encapsulate(jaw.bounds);
        Vector3 normal = (jawBounds.center - wrist.position).normalized;
        Vector3 edge = jawBounds.center + normal * Vector3.Dot(jawBounds.extents,
            new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z)));
        Vector3 gripperContactLocal = gripperVisual.InverseTransformPoint(edge);
        Vector3 up = Vector3.ProjectOnPlane(gripperVisual.up, normal);
        if (up.sqrMagnitude < 1e-5f) up = Vector3.ProjectOnPlane(gripperVisual.right, normal);
        Quaternion sourceContact = Quaternion.LookRotation(-normal, up);
        toolFrame = new GameObject("7DOF 夹爪接触参考系").transform;
        toolFrame.SetParent(gripperVisual, false);
        toolFrame.localPosition = gripperContactLocal;
        gripperOffset = Quaternion.Inverse(sourceContact) * gripperVisual.rotation;
        toolFrame.localRotation = Quaternion.Inverse(gripperOffset);
        grappleFrame = visitor.dockingPort;
        for (int i = 0; i < armSegments.Length; i++)
            armSegments[i] = new VisualSegment(station.transform, station.armJoints[i], station.armJoints[i + 1]);
        visitor.armJoints[0].gameObject.SetActive(false);
        foreach (SpatialOccluderIdentity identity in visitor.GetComponentsInChildren<SpatialOccluderIdentity>(true))
            if (identity.category == "SelfArm") identity.gameObject.SetActive(false);
        Reset();
        SetPose(station.transform, ReadPose(frame, Q));
        SetPose(visitor.transform, ReadPose(frame, Q + 32));
        AlignVisualPort(station, ReadPose(frame, StationPort), Vector3.right);
        AlignVisualPort(visitor, ReadPose(frame, VisitorPort), Vector3.left);
        Apply();
    }

    public void Reset()
    {
        Dispose();
        session = Spr7_Create();
        if (session == IntPtr.Zero) throw new InvalidOperationException("7DOF 工况初始化失败");
        steps = 0;
        pendingSeconds = 0;
        ReadSample();
    }

    public void Dispose()
    {
        if (session == IntPtr.Zero) return;
        Spr7_Destroy(session);
        session = IntPtr.Zero;
    }

    public bool Advance(double wallSeconds, double speed)
    {
        if (Finished || speed <= 0) return false;
        pendingSeconds += wallSeconds * speed;
        // Leave work for rendering and input on slower machines. Retain any time debt.
        long started = Stopwatch.GetTimestamp();
        bool newFrame = false;
        while (pendingSeconds + 1e-12 >= Step && !Finished)
        {
            int budget = (int)Math.Min(SampleSteps, Math.Floor((pendingSeconds + 1e-12) / Step));
            int actual = Spr7_Advance(session, budget);
            if (actual <= 0) throw new InvalidOperationException("7DOF 推进失败");
            steps += actual;
            pendingSeconds -= actual * Step;
            if (steps % SampleSteps == 0)
            {
                ReadSample();
                newFrame = true;
            }
            if ((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency >= 8) break;
        }
        return newFrame;
    }

    private void ReadSample()
    {
        if (Spr7_Read(session, frame, FrameCount * sizeof(double)) != 0)
            throw new InvalidOperationException("7DOF 样本读取失败");
        for (int i = 0; i < JointDegrees.Length; i++) JointDegrees[i] = (float)(frame[Q + 7 + i] * Mathf.Rad2Deg);
    }

    // Reflection P = diag(1,1,-1). Hamilton vector part transforms as an axial vector.
    public static Vector3 Polar(double[] values, int i) => new Vector3((float)values[i], (float)values[i + 1], -(float)values[i + 2]);
    public static Vector3 Axial(double[] values, int i) => -Polar(values, i);
    public static Quaternion Rotation(double[] values, int i)
    {
        Vector3 axis = Axial(values, i + 1);
        return new Quaternion(axis.x, axis.y, axis.z, (float)values[i]);
    }

    private static Pose ReadPose(double[] values, int i) => new Pose(Polar(values, i), Rotation(values, i + 3));
    private static void SetPose(Transform transform, Pose pose) => transform.SetPositionAndRotation(pose.position, pose.rotation);

    private static void AlignVisualPort(SpatialCraftAssembly assembly, Pose port, Vector3 outward)
    {
        Transform model = assembly.referenceVisual;
        Transform face = assembly.demoDockPort;
        if (model == null || face == null) throw new InvalidOperationException("参考装配缺少舱体端面");
        Vector3 native = assembly.transform.InverseTransformPoint(port.position);
        model.localPosition += native - face.localPosition;
        face.localPosition = native;
        face.localRotation = Quaternion.LookRotation(outward, Vector3.up);
    }

    public void Apply()
    {
        SetPose(station.transform, ReadPose(frame, Q));
        SetPose(visitor.transform, ReadPose(frame, Q + 32));
        SetPose(station.dynamicsPort, ReadPose(frame, StationPort));
        SetPose(visitor.dynamicsPort, ReadPose(frame, VisitorPort));
        for (int i = 0; i < armPoses.Length; i++)
        {
            armPoses[i] = ReadPose(frame, JointFrames + i * 7);
            SetPose(station.armJoints[i], armPoses[i]);
        }
        Pose tool = ReadPose(frame, Tool);
        for (int i = 0; i < armSegments.Length; i++)
        {
            int joint = i + 1;
            armSegments[i].Apply(armPoses[joint], joint + 1 < armPoses.Length ? armPoses[joint + 1].position : tool.position);
        }
        gripperVisual.rotation = tool.rotation * gripperOffset;
        gripperVisual.position = tool.position - gripperVisual.TransformVector(toolFrame.localPosition);
        grappleFrame.position = Polar(frame, Grapple);
    }

    public SpatialDynamicsTelemetry Telemetry()
    {
        var result = new SpatialDynamicsTelemetry {
            timeSec = frame[TimeIndex], mode = (int)frame[Mode],
            portDistanceM = frame[PortDistance], centerDistanceM = frame[CenterDistance],
            axialGapM = frame[Gap], toolDistanceM = frame[ToolDistance], lockRequestSettingSec = frame[LockRequestSetting],
            jointAnglesRad = new double[7], eventTimesSec = new double[6],
            stationCenter = ReadPose(frame, Q), visitorCenter = ReadPose(frame, Q + 32),
            stationPort = ReadPose(frame, StationPort), visitorPort = ReadPose(frame, VisitorPort),
            stationPortVelocity = Polar(frame, StationPortVelocity), visitorPortVelocity = Polar(frame, VisitorPortVelocity),
            stationCenterVelocity = Polar(frame, U), visitorCenterVelocity = Polar(frame, U + 31),
            tool = ReadPose(frame, Tool), grapple = Polar(frame, Grapple),
            appliedTool = new Pose(toolFrame.position, toolFrame.rotation),
            jointFrames = (Pose[])armPoses.Clone(), appliedJointFrames = new Pose[armPoses.Length],
            appliedStationPort = station.dynamicsPort.position, appliedVisitorPort = visitor.dynamicsPort.position,
            appliedStationFace = station.demoDockPort.position, appliedVisitorFace = visitor.demoDockPort.position
        };
        for (int i = 0; i < armPoses.Length; i++)
            result.appliedJointFrames[i] = new Pose(station.armJoints[i].position, station.armJoints[i].rotation);
        Array.Copy(frame, Q + 7, result.jointAnglesRad, 0, 7);
        Array.Copy(frame, Events, result.eventTimesSec, 0, 6);
        return result;
    }
}


