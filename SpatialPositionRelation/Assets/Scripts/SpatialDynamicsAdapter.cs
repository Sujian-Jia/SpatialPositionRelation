using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Owns one native scenario and maps its right-handed SI state to the Unity scene.</summary>
public sealed class SpatialDynamicsAdapter : IDisposable
{
    public const string StationId = "SpaceStation";
    public const string VisitorId = "CrewVehicle";
    internal const double Step = 0.001;
    internal const int SampleSteps = 20;
    internal const int TotalSteps = 80000;
    public const double EndTime = TotalSteps * Step;
    public const double SampleSeconds = SampleSteps * Step;
    private const int FrameCount = 168;
    private const string Library = "SpatialNative7Dof";
    private const int TimeIndex = 0, Mode = 1, Q = 2, U = 41;
    private const int StationPort = 78, VisitorPort = 85, StationPortVelocity = 92, VisitorPortVelocity = 95;
    private const int JointFrames = 98, Tool = 147, Grapple = 154, PortDistance = 157, CenterDistance = 158;
    private const int Gap = 159, ToolDistance = 160, Events = 161, LockRequestSetting = 167;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Spr7_Abi();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Spr7_Create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Spr7_Clone(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Spr7_Destroy(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Spr7_ReadCalibration(IntPtr session, [Out] double[] values, int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Spr7_Advance(IntPtr session, int maxSteps);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Spr7_Read(IntPtr session, [Out] double[] frame, int frameBytes);

    private IntPtr session;
    private int steps;
    private double pendingSeconds;
    private readonly double[] frame = new double[FrameCount];
    private readonly SpatialCraftAssembly station;
    private readonly SpatialCraftAssembly visitor;
    private readonly Pose[] armPoses = new Pose[7];
    private readonly Vector3[] restOffsets = new Vector3[7];
    private readonly Vector3[] restScales = new Vector3[7];
    private readonly Pose stationMount;
    private readonly Vector3 visitorGrapple;

    public double TimeSec => frame[TimeIndex];
    public bool Finished => steps == TotalSteps;
    public float[] JointDegrees { get; } = new float[7];

    public SpatialDynamicsAdapter(SpatialCraftAssembly station, SpatialCraftAssembly visitor)
    {
        this.station = station;
        this.visitor = visitor;
        if (station.armJoints.Length != 7 || visitor.armJoints.Length != 7)
            throw new InvalidOperationException("参考装配缺少七关节绑定");
        if (Spr7_Abi() != 1) throw new InvalidOperationException("7DOF 接口版本不匹配");
        try
        {
            Reset();
            var calibration = new double[31];
            if (Spr7_ReadCalibration(session, calibration, calibration.Length) != 0)
                throw new InvalidOperationException("7DOF 装配基准读取失败");
            stationMount = ReadPose(calibration, 0);
            visitorGrapple = Polar(calibration, 7);
            for (int i = 0; i < restOffsets.Length; i++) restOffsets[i] = Polar(calibration, 10 + 3*i);
            SetPose(station.transform, ReadPose(frame, Q));
            SetPose(visitor.transform, ReadPose(frame, Q + 32));
            AlignVisualPort(station, ReadPose(frame, StationPort), Vector3.right);
            AlignVisualPort(visitor, ReadPose(frame, VisitorPort), Vector3.left);
            Physics.SyncTransforms();
            SpatialArmCalibration.Register(station, visitor, stationMount, visitorGrapple, restOffsets);
            for (int i = 0; i < restScales.Length; i++) restScales[i] = station.armLinkCarriers[i].localScale;
            Apply();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ForecastSamples Fork() => new ForecastSamples(session, frame, steps);

    // Reflection of the native axial +Z is Unity +Z. Preview deltas are applied
    // in the post-joint local frames of a render-only copy of the calibrated assembly.
    internal SpatialArmPreview CreateOverviewPreview(Camera camera) =>
        new SpatialArmPreview(station, camera, JointDegrees, Vector3.forward);

    public SpatialDynamicsAdapter ForForecastGeometry(SpatialCraftAssembly stationCopy, SpatialCraftAssembly visitorCopy)
        => new SpatialDynamicsAdapter(this, stationCopy, visitorCopy);

    private SpatialDynamicsAdapter(SpatialDynamicsAdapter source, SpatialCraftAssembly station, SpatialCraftAssembly visitor)
    {
        this.station = station;
        this.visitor = visitor;
        stationMount = source.stationMount;
        visitorGrapple = source.visitorGrapple;
        Array.Copy(source.restOffsets, restOffsets, restOffsets.Length);
        Array.Copy(source.restScales, restScales, restScales.Length);
    }

    internal void ApplyForecastFrame(double[] sample)
    {
        Array.Copy(sample, frame, FrameCount);
        Apply();
    }

    // Only this worker touches the cloned C++ handle. Unity objects stay on the main thread.
    public sealed class ForecastSamples : IDisposable
    {
        private readonly ConcurrentQueue<double[]> samples = new ConcurrentQueue<double[]>();
        private readonly object gate = new object();
        private readonly Thread worker;
        private readonly IntPtr snapshot;
        private bool paused, disposed;
        private int steps;
        private Exception failure;

        internal ForecastSamples(IntPtr session, double[] lastSample, int steps)
        {
            snapshot = Spr7_Clone(session);
            if (snapshot == IntPtr.Zero) throw new InvalidOperationException("7DOF 无法克隆预测会话");
            this.steps = steps;
            samples.Enqueue((double[])lastSample.Clone());
            worker = new Thread(Rollout) { IsBackground = true, Name = "7DOF forecast" };
            try { worker.Start(); }
            catch { Spr7_Destroy(snapshot); throw; }
        }

        private void Rollout()
        {
            try
            {
                while (steps < TotalSteps)
                {
                    lock (gate)
                    {
                        while (paused && !disposed) Monitor.Wait(gate);
                        if (disposed) return;
                        int actual = Spr7_Advance(snapshot, SampleSteps);
                        if (actual <= 0) throw new InvalidOperationException("7DOF 预测样本推进失败");
                        steps += actual;
                        var sample = new double[FrameCount];
                        if (Spr7_Read(snapshot, sample, FrameCount * sizeof(double)) != 0)
                            throw new InvalidOperationException("7DOF 预测样本读取失败");
                        samples.Enqueue(sample);
                    }
                    // Give a waiting pause/reset control priority at this sample boundary.
                    Thread.Yield();
                }
            }
            catch (Exception exception) { Volatile.Write(ref failure, exception); }
            finally { Spr7_Destroy(snapshot); }
        }

        public bool TryRead(out double[] sample)
        {
            var error = Volatile.Read(ref failure);
            if (error != null) throw new InvalidOperationException(error.Message, error);
            return samples.TryDequeue(out sample);
        }

        public void SetPaused(bool value)
        {
            lock (gate)
            {
                paused = value;
                if (!paused) Monitor.PulseAll(gate);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                Monitor.PulseAll(gate);
            }
        }
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
        SetPose(station.armTool, tool);
        // Only the two FE members deform. Their authoritative distal locations are the
        // following native joint origins, already including the C++ flexible tip deflection.
        for (int i = 3; i <= 4; i++)
        {
            Vector3 chord = Quaternion.Inverse(armPoses[i].rotation) * (armPoses[i+1].position - armPoses[i].position);
            Transform carrier = station.armLinkCarriers[i];
            Vector3 rest = restOffsets[i].normalized;
            Vector3 current = chord.normalized;
            Vector3 cross = Vector3.Cross(rest, current);
            var bend = new Quaternion(cross.x, cross.y, cross.z, 1 + Vector3.Dot(rest, current));
            carrier.localRotation = bend.normalized * SpatialArmCalibration.NativeChordBasis(restOffsets[i]);
            carrier.localScale = new Vector3(restScales[i].x, restScales[i].y,
                restScales[i].z * chord.magnitude / restOffsets[i].magnitude);
        }
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
            appliedTool = new Pose(station.armTool.position, station.armTool.rotation),
            appliedGripper = station.gripperDatum.position, appliedGrapple = visitor.grappleDatum.position,
            basePivot = station.armBasePivot.position, baseFoot = station.armBaseFoot.position,
            baseSeat = station.armBaseSeat.position, grappleFoot = visitor.grappleFoot.position, grappleSeat = visitor.grappleSeat.position,
            linkStarts = new Vector3[7], linkEnds = new Vector3[7],
            jointFrames = (Pose[])armPoses.Clone(), appliedJointFrames = new Pose[armPoses.Length],
            appliedStationPort = station.dynamicsPort.position, appliedVisitorPort = visitor.dynamicsPort.position,
            appliedStationFace = station.demoDockPort.position, appliedVisitorFace = visitor.demoDockPort.position
        };
        for (int i = 0; i < armPoses.Length; i++)
        {
            result.appliedJointFrames[i] = new Pose(station.armJoints[i].position, station.armJoints[i].rotation);
            result.linkStarts[i] = station.armLinkSeats[i].position;
            result.linkEnds[i] = station.armLinkTips[i].position;
        }
        Array.Copy(frame, Q + 7, result.jointAnglesRad, 0, 7);
        Array.Copy(frame, Events, result.eventTimesSec, 0, 6);
        return result;
    }
}


