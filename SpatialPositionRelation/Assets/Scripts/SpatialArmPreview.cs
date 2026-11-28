using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Overview-only reference meshes. No sensors, colliders, scripts, or native handles are copied.
internal sealed class SpatialArmPreview : IDisposable
{
    private readonly GameObject root;
    private readonly Camera camera;
    private readonly Transform[] joints = new Transform[7];
    private readonly Quaternion[] seedRotations = new Quaternion[7];
    private readonly float[] seedDegrees;
    private readonly Vector3 jointAxis;
    private readonly int layer;
    private readonly List<MeshRenderer> originals = new List<MeshRenderer>();
    private bool[] savedVisibility;
    private bool drawing;

    public SpatialArmPreview(SpatialCraftAssembly station, Camera camera, float[] seedDegrees, Vector3 jointAxis)
    {
        this.camera = camera;
        this.seedDegrees = (float[])seedDegrees.Clone();
        this.jointAxis = jointAxis;
        layer = LayerMask.NameToLayer("SpatialOverviewOverlay");
        if (layer < 0) throw new InvalidOperationException("总览显示层不可用");
        root = new GameObject("Arm overview preview");
        try
        {
            for (int i = 0; i < joints.Length; i++)
            {
                Transform source = station.armJoints[i];
                Transform joint = NewNode(i == 0 ? root.transform : joints[i - 1], "J" + (i + 1));
                if (i == 0)
                {
                    joint.SetPositionAndRotation(source.position, source.rotation);
                    joint.localScale = source.lossyScale;
                }
                else CopyLocal(source, joint);
                joints[i] = joint;
                seedRotations[i] = joint.localRotation;
                CopyMeshTree(station.armLinkCarriers[i], joint);
            }
            Transform tool = NewNode(joints[6], "Tool");
            tool.SetPositionAndRotation(station.armTool.position, station.armTool.rotation);
            CopyMeshTree(station.gripperDatum, tool);
            savedVisibility = new bool[originals.Count];
            Camera.onPreCull += BeginOverview;
            Camera.onPostRender += EndOverview;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static bool ValidAngles(float[] values)
    {
        if (values == null || values.Length != 7) return false;
        foreach (float value in values)
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        return true;
    }

    public void SetAngles(float[] degrees)
    {
        for (int i = 0; i < joints.Length; i++)
        {
            float delta = Mathf.DeltaAngle(seedDegrees[i], degrees[i]);
            joints[i].localRotation = seedRotations[i] * Quaternion.AngleAxis(delta, jointAxis);
        }
    }

    private Transform NewNode(Transform parent, string name)
    {
        var node = new GameObject(name) { layer = layer };
        node.transform.SetParent(parent, false);
        return node.transform;
    }

    private static void CopyLocal(Transform from, Transform to)
    {
        to.SetLocalPositionAndRotation(from.localPosition, from.localRotation);
        to.localScale = from.localScale;
    }

    private void CopyMeshTree(Transform source, Transform parent)
    {
        if (!source.gameObject.activeInHierarchy) return;
        Transform copy = NewNode(parent, source.name);
        CopyLocal(source, copy);
        if (source.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null &&
            source.TryGetComponent(out MeshRenderer renderer) && renderer.enabled && !renderer.forceRenderingOff)
        {
            copy.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var clone = copy.gameObject.AddComponent<MeshRenderer>();
            clone.sharedMaterials = renderer.sharedMaterials;
            clone.shadowCastingMode = ShadowCastingMode.Off;
            originals.Add(renderer);
        }
        foreach (Transform child in source) CopyMeshTree(child, copy);
    }

    // Hide the original meshes only while the overview camera draws. Sensor cameras and
    // captures still see the unmodified physical arm, even while the sliders are active.
    private void BeginOverview(Camera renderingCamera)
    {
        if (renderingCamera != camera || drawing) return;
        drawing = true;
        for (int i = 0; i < originals.Count; i++)
        {
            savedVisibility[i] = originals[i].forceRenderingOff;
            originals[i].forceRenderingOff = true;
        }
    }

    private void EndOverview(Camera renderingCamera)
    {
        if (renderingCamera != camera || !drawing) return;
        drawing = false;
        for (int i = 0; i < originals.Count; i++)
            if (originals[i] != null) originals[i].forceRenderingOff = savedVisibility[i];
    }

    public void Dispose()
    {
        EndOverview(camera);
        Camera.onPreCull -= BeginOverview;
        Camera.onPostRender -= EndOverview;
        root.SetActive(false);
        UnityEngine.Object.Destroy(root);
    }
}
