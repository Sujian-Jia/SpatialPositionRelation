using UnityEngine;

public abstract partial class SpatialSensorViewBase
{
    public bool ShowRays { get; set; }
    public float SensorVerticalFov => configuration == null ? 32 : Mathf.Clamp(configuration.verticalFovDeg, 1, 179);
    public float OpticalAspect => configuration == null ? 1 :
        Mathf.Tan(Mathf.Clamp(configuration.horizontalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f)
        / Mathf.Tan(Mathf.Clamp(configuration.verticalFovDeg, 1, 179) * Mathf.Deg2Rad * 0.5f);

    private void LateUpdate()
    {
        DrawFrustum();
    }

    public void DrawFrustum()
    {
        float distance = frustumDistance;
        if (frustumLines == null || frustumLines.Length < 4) return;
        if (!ShowRays || configuration == null)
        {
            foreach (LineRenderer line in frustumLines)
                if (line != null) line.enabled = false;
            return;
        }
        float length = Mathf.Clamp(float.IsInfinity(distance) ? 18 : distance, 4, 50);
        float x = length * Mathf.Tan(configuration.horizontalFovDeg * Mathf.Deg2Rad * 0.5f);
        float y = length * Mathf.Tan(configuration.verticalFovDeg * Mathf.Deg2Rad * 0.5f);
        Transform opticalCenter = sensorCamera != null ? sensorCamera.transform : transform;
        Vector3 origin = opticalCenter.position;
        Vector3[] corners = {
            origin + opticalCenter.rotation * new Vector3(-x, -y, length), origin + opticalCenter.rotation * new Vector3(x, -y, length),
            origin + opticalCenter.rotation * new Vector3(x, y, length), origin + opticalCenter.rotation * new Vector3(-x, y, length)};
        for (int i = 0; i < 4; i++)
        {
            if (frustumLines[i] == null) continue;
            frustumLines[i].SetPosition(0, origin);
            frustumLines[i].SetPosition(1, corners[i]);
            frustumLines[i].enabled = true;
        }
        if (latest != null)
        {
            latest.frustumVertex = frustumLines[0] != null ? frustumLines[0].GetPosition(0) : origin;
            latest.frustumOriginErrorM = Vector3.Distance(latest.frustumVertex, origin);
        }
    }

}
