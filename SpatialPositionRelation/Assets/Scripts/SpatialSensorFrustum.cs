using UnityEngine;

public abstract partial class SpatialSensorViewBase
{
    public bool ShowRays { get; set; }
    public float SensorVerticalFov => configuration == null ? 32 : Mathf.Clamp(configuration.verticalFovDeg, 1, 179);
    public float OpticalAspect => configuration == null ? 1 :
        Mathf.Tan(Mathf.Clamp(configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f)
        / Mathf.Tan(Mathf.Clamp(configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);

    private const int ConeSegments = 64;
    private static readonly int[] ConeFaces = CreateConeFaces();
    private readonly Vector3[] coneVertices = new Vector3[ConeSegments + 1];
    private Mesh frustumMesh;
    private Vector3 previousFrustumSize;

    private void LateUpdate() => DrawFrustum();

    private void OnDestroy()
    {
        if (frustumMesh != null) Destroy(frustumMesh);
    }

    public void DrawFrustum()
    {
        if (frustumFilter == null || frustumSurface == null) return;
        frustumSurface.enabled = ShowRays && configuration != null;
        if (!frustumSurface.enabled) return;
        float length = Mathf.Clamp(float.IsInfinity(frustumDistance) ? 18 : frustumDistance, 4, 50);
        float x = length * Mathf.Tan(configuration.horizontalFovDeg * Mathf.Deg2Rad * 0.5f);
        float y = length * Mathf.Tan(configuration.verticalFovDeg * Mathf.Deg2Rad * 0.5f);
        Vector3 size = new Vector3(x, y, length);
        bool create = frustumMesh == null;
        if (create)
        {
            frustumMesh = new Mesh { name = "Open sensor cone" };
            frustumFilter.sharedMesh = frustumMesh;
        }
        if (create || size != previousFrustumSize)
        {
            // The surface is under the optical camera, but the legacy lengths are world metres.
            Transform meshSpace = frustumFilter.transform;
            // Retain the horizontal/vertical edge angles and axial range of the original overlay.
            // Equal angles make a circle. Different angles make an ellipse with those same half-axes.
            for (int i = 0; i < ConeSegments; i++)
            {
                float angle = i * (2f * Mathf.PI / ConeSegments);
                coneVertices[i + 1] = ToMeshSpace(meshSpace,
                    new Vector3(x * Mathf.Cos(angle), y * Mathf.Sin(angle), length));
            }
            frustumMesh.vertices = coneVertices;
            if (create) frustumMesh.triangles = ConeFaces;
            frustumMesh.RecalculateBounds();
            previousFrustumSize = size;
        }
        if (latest != null)
        {
            latest.frustumVertex = frustumFilter.transform.position;
            latest.frustumOriginErrorM = Vector3.Distance(latest.frustumVertex, sensorCamera.transform.position);
        }
    }

    private static int[] CreateConeFaces()
    {
        var triangles = new int[3 * ConeSegments];
        for (int i = 0; i < ConeSegments; i++)
        {
            triangles[3 * i + 1] = i + 1;
            triangles[3 * i + 2] = (i + 1) % ConeSegments + 1;
        }
        return triangles;
    }

    private static Vector3 ToMeshSpace(Transform meshSpace, Vector3 opticalMetres) =>
        meshSpace.InverseTransformVector(meshSpace.rotation * opticalMetres);
}
