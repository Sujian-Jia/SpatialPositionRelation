using System;
using UnityEngine;

public abstract class SpatialOccluderIdentityBase : MonoBehaviour
{
    public string partName;
    public string partId;
    public string displayName;
    public string category;
}

public abstract class SpatialCraftAssemblyBase : MonoBehaviour
{
    public string craftId;
    public Transform[] solarHinges = Array.Empty<Transform>();
    public Quaternion[] solarBaseRotations = Array.Empty<Quaternion>();
    public Vector3[] solarLocalAxes = Array.Empty<Vector3>();
    public Vector3[] solarFaceNormals = Array.Empty<Vector3>();
    [NonSerialized] public float[] solarAngles;
    public Transform demoDockPort;
    public Transform[] armJoints = Array.Empty<Transform>();
    public Quaternion[] armBaseRotations = Array.Empty<Quaternion>();
    public Transform armElbow;
    public Transform armWrist;
    public Transform dockingPort;
    public Transform[] cameraInstallations = Array.Empty<Transform>();

    public Transform FindCameraInstallation(string sensorId)
    {
        foreach (Transform installation in cameraInstallations)
            if (installation != null && installation.name == sensorId) return installation;
        return null;
    }
}
