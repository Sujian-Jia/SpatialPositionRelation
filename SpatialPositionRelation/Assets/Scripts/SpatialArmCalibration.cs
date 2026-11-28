using System;
using System.Linq;
using UnityEngine;

/// <summary>Registers the existing reference meshes against native mating datums once per assembly.</summary>
public static class SpatialArmCalibration
{
    public static void Register(SpatialCraftAssembly station, SpatialCraftAssembly visitor,
        Pose stationMount, Vector3 visitorGrapple, Vector3[] restOffsets)
    {
        Transform[] sourceDistal = station.armJoints;
        Transform sourceMount = sourceDistal[0].parent.parent;
        Transform pedestal = sourceMount.parent;
        if (sourceMount.name != "关节1" || pedestal.name != "基座")
            throw new InvalidOperationException("七轴机械臂缺少基座安装基准");
        Transform grapple = visitor.referenceVisual.Find("轴/轴顶点");
        Transform insertion = grapple != null ? grapple.Find("InsertionPoint") : null;
        Transform gripper = sourceDistal[6].GetComponentsInChildren<Transform>(true).Single(part => part.name == "夹爪");
        if (insertion == null || !grapple.CompareTag("Gripper"))
            throw new InvalidOperationException("目标装配缺少轴/轴顶点/InsertionPoint");

        Vector3 mount = station.transform.TransformPoint(stationMount.position);
        Vector3 capture = visitor.transform.TransformPoint(visitorGrapple);
        Vector3 mountFoot = HullSurface(station, mount);
        Vector3 captureFoot = HullSurface(visitor, capture);
        station.armBaseSeat = Anchor(station.transform, "基座安装面", mountFoot);
        station.armBasePivot = Anchor(station.transform, "固定端轴心", mount);
        station.armBasePivot.localRotation = stationMount.rotation;
        visitor.grappleSeat = Anchor(visitor.transform, "捕获座安装面", captureFoot);

        if (station.armSourceMeshes == null || station.armSourceMeshes.Length != 7)
            throw new InvalidOperationException("缺少连杆端面标定，请重新生成演示场景");

        station.armTool = Anchor(station.transform, "原生夹爪参考系", gripper.position);
        Vector3 gripperPosition = gripper.position;
        // Native +Z is the contact direction. A reflected polar vector points along Unity -Z.
        station.armTool.rotation = Quaternion.LookRotation(-gripper.forward, gripper.up);
        gripper.SetParent(station.armTool, true);
        station.gripperDatum = gripper;
        station.armWrist.SetParent(station.armTool, true);
        // Keep the CAD camera mounting axes and location on the same physical wrist.
        station.armWrist.position = gripperPosition;

        station.armJoints = new Transform[7];
        station.armLinkTips = new Transform[7];
        station.armLinkCarriers = new Transform[7];
        station.armLinkSeats = new Transform[7];
        for (int i = 0; i < 7; i++)
        {
            Transform source = station.armSourceMeshes[i];
            Vector3 near = source.TransformPoint(station.armSourceNear[i]);
            Vector3 far = source.TransformPoint(station.armSourceFar[i]);
            float length = Vector3.Distance(near, far);
            Transform joint = Anchor(i == 0 ? station.transform : station.armJoints[i-1], $"7DOF J{i+1}", near);
            station.armJoints[i] = joint;
            Transform carrier = Anchor(joint, "组内连杆 " + (i + 1), near);
            carrier.rotation = ChordBasis(far - near, source.up, source.right);
            source.SetParent(carrier, true);
            station.armLinkSeats[i] = Anchor(source, "FBX 近端接合面中心", near);
            station.armLinkTips[i] = Anchor(source, "FBX 远端接合面中心", far);
            station.armLinkCarriers[i] = carrier;
            carrier.localPosition = Vector3.zero;
            carrier.localRotation = NativeChordBasis(restOffsets[i]);
            carrier.localScale = new Vector3(1, 1, restOffsets[i].magnitude / length);
            Debug.Log($"SPATIAL_CAD_LINK index={i+1} mesh={source.name} sourceSpanM={length:F6} " +
                      $"nativeLengthM={restOffsets[i].magnitude:F6} scaleZ={carrier.localScale.z:F6}");
        }
        // CAD elbow is on the distal end of link 4, which is native J5.
        station.armElbow.SetParent(station.armJoints[4], false);
        station.armElbow.localPosition = Vector3.zero;
        station.armElbow.localRotation = Quaternion.identity;

        station.armBaseFoot = FitPedestal(pedestal, station.armBasePivot, station.armBaseSeat);
        Transform fixtureBody = grapple.parent;
        Vector3 tip = grapple.position;
        Vector3 shaft = tip - insertion.position;
        Transform fixtureCarrier = Anchor(visitor.transform, "组内目标捕获座", tip);
        fixtureCarrier.rotation = ChordBasis(shaft, fixtureBody.up, fixtureBody.forward);
        float modelLength = shaft.magnitude;
        fixtureBody.SetParent(fixtureCarrier, true);
        fixtureCarrier.SetPositionAndRotation(capture, ChordBasis(capture - captureFoot, visitor.transform.up, visitor.transform.right));
        fixtureCarrier.localScale = new Vector3(1, 1, (capture - captureFoot).magnitude / modelLength);
        visitor.grappleDatum = grapple;
        visitor.grappleFoot = insertion;

        // The duplicate model represents the visitor. Hide its unused arm as a whole, including its fixed pedestal.
        visitor.armJoints[0].parent.parent.parent.gameObject.SetActive(false);
        foreach (SpatialOccluderIdentity identity in visitor.GetComponentsInChildren<SpatialOccluderIdentity>(true))
            if (identity.category == "SelfArm") identity.gameObject.SetActive(false);
        Debug.Log($"SPATIAL_CAD_ATTACHMENT baseSpanM={Vector3.Distance(mount,mountFoot):F6} " +
                  $"fixtureSpanM={Vector3.Distance(capture,captureFoot):F6} tip={grapple.name} gripper={gripper.name}");
    }

    public static Quaternion NativeChordBasis(Vector3 chord) => ChordBasis(chord, Vector3.forward, Vector3.up);

    private static Quaternion ChordBasis(Vector3 chord, Vector3 up, Vector3 alternative)
    {
        if (chord.sqrMagnitude < 1e-10f) throw new InvalidOperationException("机械臂标定基准的两端重合");
        Vector3 normal = Vector3.ProjectOnPlane(up, chord);
        if (normal.sqrMagnitude < 1e-6f) normal = Vector3.ProjectOnPlane(alternative, chord);
        return Quaternion.LookRotation(chord, normal);
    }

    private static Transform Anchor(Transform parent, string name, Vector3 world)
    {
        var marker = new GameObject(name).transform;
        marker.SetParent(parent, false);
        marker.position = world;
        return marker;
    }

    private static Vector3 HullSurface(SpatialCraftAssembly craft, Vector3 pivot)
    {
        Transform hull = craft.referenceVisual.Find("航天器本身");
        Collider[] hullColliders = hull.GetComponentsInChildren<Collider>();
        if (hullColliders.Length == 0) throw new InvalidOperationException("舱体缺少可标定表面");
        Bounds bounds = hullColliders[0].bounds;
        foreach (Collider collider in hullColliders) bounds.Encapsulate(collider.bounds);
        Vector3 direction = (bounds.center - pivot).normalized;
        // Start outside the shell even if a replacement hull puts the datum inside its bounds.
        var ray = new Ray(pivot - direction * bounds.size.magnitude, direction);
        float distance = float.PositiveInfinity;
        Vector3 foot = Vector3.zero;
        foreach (Collider collider in hullColliders)
            if (!collider.isTrigger && collider.bounds.IntersectRay(ray) &&
                collider.Raycast(ray, out RaycastHit hit, bounds.size.magnitude * 3) && hit.distance < distance)
            {
                distance = hit.distance;
                foot = hit.point;
            }
        if (float.IsPositiveInfinity(distance)) throw new InvalidOperationException("无法在舱体网格上定位安装面：" + craft.craftId);
        return foot;
    }

    private static Transform FitPedestal(Transform pedestal, Transform pivot, Transform seat)
    {
        MeshFilter filter = pedestal.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("缺少基座实体");
        // The first CAD joint extends along the pedestal's local +Z. Read the rear mounting plane
        // from the actual base mesh rather than stretching or translating the spacecraft.
        Vector3 sourceBack = pedestal.TransformPoint(new Vector3(0, 0, filter.sharedMesh.bounds.min.z));
        Vector3 sourcePivot = pedestal.position;
        Vector3 sourceSpan = sourcePivot - sourceBack;
        Transform carrier = Anchor(pivot.parent, "组内机械臂基座", sourcePivot);
        carrier.rotation = ChordBasis(sourceSpan, pedestal.up, pedestal.right);
        // Copy only the fixed base mesh and collider. The CAD joint branch has its own datums.
        var baseMesh = new GameObject(pedestal.name);
        baseMesh.transform.SetParent(carrier, false);
        baseMesh.transform.SetPositionAndRotation(pedestal.position, pedestal.rotation);
        baseMesh.transform.localScale = pedestal.lossyScale;
        baseMesh.layer = pedestal.gameObject.layer;
        baseMesh.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        baseMesh.AddComponent<MeshRenderer>().sharedMaterials = pedestal.GetComponent<Renderer>().sharedMaterials;
        baseMesh.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        if (pedestal.TryGetComponent(out SpatialOccluderIdentity original))
        {
            var identity = baseMesh.AddComponent<SpatialOccluderIdentity>();
            identity.category = original.category;
            identity.partId = original.partId;
            identity.partName = original.partName;
            identity.displayName = original.displayName;
        }
        pedestal.GetComponent<Renderer>().enabled = false;
        foreach (Collider collider in pedestal.GetComponents<Collider>()) collider.enabled = false;
        Vector3 targetSpan = pivot.position - seat.position;
        Transform foot = Anchor(carrier, "基座后接合点", sourceBack);
        carrier.SetPositionAndRotation(pivot.position, ChordBasis(targetSpan, pivot.up, pivot.right));
        carrier.localScale = new Vector3(1, 1, targetSpan.magnitude / sourceSpan.magnitude);
        return foot;
    }
}
