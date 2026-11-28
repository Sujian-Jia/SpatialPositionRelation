using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class SpatialOcclusionForecast : IDisposable
{
    private readonly Scene scene;
    private readonly SpatialForecastPhysics physics = new SpatialForecastPhysics();
    private SpatialDynamicsAdapter geometry;
    private SpatialDynamicsAdapter.ForecastSamples rollout;
    private readonly Dictionary<string, List<Sample>> sensors = new Dictionary<string, List<Sample>>();
    private readonly SpatialSensorView[] views;
    private readonly Vector3 sun;
    private readonly Action<SpatialCraftAssembly, Vector3, bool> trackSolar;
    private readonly SpatialCraftAssembly station;
    private readonly SpatialCraftAssembly visitor;
    private bool needsFirstSample = true;
    private double through;

    private readonly struct Sample
    {
        public readonly double time;
        public readonly int inView, blocked;
        public Sample(double time, int inView, int blocked)
        {
            this.time = time;
            this.inView = inView;
            this.blocked = blocked;
        }
    }

    public SpatialOcclusionForecast(SpatialDynamicsAdapter source, SpatialCraftAssembly liveStation,
        SpatialCraftAssembly liveVisitor, Vector3 sunDirection,
        Action<SpatialCraftAssembly, Vector3, bool> applySolarTracking)
    {
        scene = SceneManager.CreateScene("7DOF 遮挡预测 " + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        try
        {
            station = Copy(liveStation);
            visitor = Copy(liveVisitor);
            views = station.GetComponentsInChildren<SpatialSensorView>();
            var originals = liveStation.GetComponentsInChildren<SpatialSensorView>();
            physics.AddCraft(station);
            physics.AddCraft(visitor);
            physics.Seal();
            foreach (SpatialSensorView view in views)
            {
                view.enabled = false;
                view.ForecastRay = physics.IsBlocked;
                SpatialSensorView original = Array.Find(originals, sensor => sensor.sensorId == view.sensorId);
                view.configuration = original.configuration;
                if (view.configuration != null && view.configuration.enabled)
                    sensors.Add(view.sensorId, new List<Sample>());
            }
            geometry = source.ForForecastGeometry(station, visitor);
            rollout = source.Fork();
            sun = sunDirection;
            trackSolar = applySolarTracking;
            through = source.TimeSec;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private SpatialCraftAssembly Copy(SpatialCraftAssembly source)
    {
        var copy = Object.Instantiate(source);
        SceneManager.MoveGameObjectToScene(copy.gameObject, scene);
        foreach (Camera camera in copy.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true)) renderer.forceRenderingOff = true;
        return copy;
    }

    public void SetPaused(bool paused) => rollout?.SetPaused(paused);

    public void Work(double milliseconds, Func<SpatialSensorView, SpatialCraftAssembly, SpatialCraftAssembly, Vector2Int> evaluate)
    {
        if (rollout == null) return;
        long started = Stopwatch.GetTimestamp();
        while (rollout.TryRead(out double[] sample))
        {
            geometry.ApplyForecastFrame(sample);
            needsFirstSample = false;
            trackSolar(station, sun, false);
            trackSolar(visitor, sun, true);
            foreach (SpatialSensorView view in views)
            {
                if (!sensors.TryGetValue(view.sensorId, out List<Sample> samples)) continue;
                Vector2Int counts = evaluate(view, station, visitor);
                samples.Add(new Sample(geometry.TimeSec, counts.x, counts.y));
            }
            through = geometry.TimeSec;
            if (through == SpatialDynamicsAdapter.EndTime)
            {
                rollout.Dispose();
                rollout = null;
                return;
            }
            if ((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency >= milliseconds) break;
        }
    }

    public void Fill(SpatialSensorTelemetry result, double now)
    {
        if (!sensors.TryGetValue(result.id, out List<Sample> samples) || now >= SpatialDynamicsAdapter.EndTime) return;
        result.trajectoryForecastWindowSec = (float)(SpatialDynamicsAdapter.EndTime - now);
        result.trajectoryForecastSampleSec = (float)SpatialDynamicsAdapter.SampleSeconds;
        result.trajectoryForecastThroughSec = (float)through;
        if (needsFirstSample || through <= now) return;
        result.trajectoryForecastAvailable = true;
        result.trajectoryForecastComplete = through == SpatialDynamicsAdapter.EndTime;
        int blocked = 0, outOfFov = 0;
        bool firstIntervalClosed = false;
        int first = Mathf.RoundToInt((float)((now - samples[0].time) / SpatialDynamicsAdapter.SampleSeconds));
        for (int i = Math.Max(0, first); i < samples.Count; i++)
        {
            Sample sample = samples[i];
            if (sample.blocked == 0)
            {
                if (sample.inView == 0 && i+1 < samples.Count) outOfFov++;
                if (result.futureOcclusion && !firstIntervalClosed)
                {
                    result.futureOcclusionEndSec = (float)sample.time;
                    firstIntervalClosed = true;
                }
                continue;
            }
            if (i+1 < samples.Count) blocked++;
            result.trajectoryForecastPeakBlockedRatio = Mathf.Max(result.trajectoryForecastPeakBlockedRatio,
                (float)sample.blocked / sample.inView);
            if (!result.futureOcclusion)
            {
                result.futureOcclusion = true;
                result.futureOcclusionStartSec = (float)Math.Max(now, sample.time);
                result.futureOcclusionEndSec = (float)through;
            }
        }
        result.trajectoryForecastBlockedSec = (float)(blocked * SpatialDynamicsAdapter.SampleSeconds);
        result.trajectoryForecastOutOfFovSec = (float)(outOfFov * SpatialDynamicsAdapter.SampleSeconds);
    }

    public void Dispose()
    {
        rollout?.Dispose();
        rollout = null;
        physics.Dispose();
        if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
    }
}
