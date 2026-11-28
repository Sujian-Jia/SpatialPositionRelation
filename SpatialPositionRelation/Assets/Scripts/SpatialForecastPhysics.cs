using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Stable collider islands for forecast queries. The ray moves into each island's
/// local coordinates, leaving copies of the original colliders in static broadphase trees.</summary>
public sealed class SpatialForecastPhysics : IDisposable
{
    private sealed class Island
    {
        public Transform Moving;
        public Scene Scene;
        public PhysicsScene World;
        public Bounds Bounds;
        public bool HasCollider;
        public RaycastHit[] Hits = new RaycastHit[16];
    }

    private readonly string scenePrefix = "7DOF 背景碰撞 " + Guid.NewGuid().ToString("N") + " ";
    private readonly List<Island> islands = new List<Island>();
    private readonly Dictionary<Collider, Transform> original = new Dictionary<Collider, Transform>();

    public void AddCraft(SpatialCraftAssembly craft)
    {
        var roots = new HashSet<Transform>(craft.solarHinges) { craft.transform };
        if (craft.armLinkCarriers != null)
            foreach (Transform link in craft.armLinkCarriers) roots.Add(link);
        var byRoot = new Dictionary<Transform, Island>();
        foreach (Collider source in craft.GetComponentsInChildren<Collider>())
        {
            if (!source.enabled || source.isTrigger || source.GetComponentInParent<SpatialOccluderIdentity>() == null ||
                ((1 << source.gameObject.layer) & Physics.DefaultRaycastLayers) == 0) continue;
            Transform groupRoot = source.transform;
            while (!roots.Contains(groupRoot)) groupRoot = groupRoot.parent;
            if (!byRoot.TryGetValue(groupRoot, out Island group))
            {
                var scene = SceneManager.CreateScene(scenePrefix + islands.Count, new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                group = new Island { Moving = groupRoot, Scene = scene, World = scene.GetPhysicsScene() };
                islands.Add(group);
                byRoot.Add(groupRoot, group);
            }
            var shape = new GameObject(source.name);
            SceneManager.MoveGameObjectToScene(shape, group.Scene);
            var matrix = groupRoot.worldToLocalMatrix * source.transform.localToWorldMatrix;
            shape.transform.SetPositionAndRotation(matrix.GetColumn(3), matrix.rotation);
            shape.transform.localScale = matrix.lossyScale;
            Collider collider = CopyCollider(source, shape);
            original.Add(collider, source.transform);
            source.enabled = false;
        }
    }

    private static Collider CopyCollider(Collider source, GameObject target)
    {
        if (source is MeshCollider mesh)
        {
            var copy = target.AddComponent<MeshCollider>();
            copy.cookingOptions = mesh.cookingOptions;
            copy.convex = mesh.convex;
            copy.sharedMesh = mesh.sharedMesh;
            return copy;
        }
        if (source is BoxCollider box)
        {
            var copy = target.AddComponent<BoxCollider>();
            copy.center = box.center;
            copy.size = box.size;
            return copy;
        }
        if (source is SphereCollider sphere)
        {
            var copy = target.AddComponent<SphereCollider>();
            copy.center = sphere.center;
            copy.radius = sphere.radius;
            return copy;
        }
        if (source is CapsuleCollider capsule)
        {
            var copy = target.AddComponent<CapsuleCollider>();
            copy.center = capsule.center;
            copy.direction = capsule.direction;
            copy.height = capsule.height;
            copy.radius = capsule.radius;
            return copy;
        }
        throw new InvalidOperationException("无法复制预测碰撞体：" + source.GetType().Name);
    }

    public void Seal()
    {
        Physics.SyncTransforms();
        foreach (Island group in islands)
            foreach (GameObject root in group.Scene.GetRootGameObjects())
            {
                Bounds bounds = root.GetComponent<Collider>().bounds;
                if (group.HasCollider) group.Bounds.Encapsulate(bounds);
                else group.Bounds = bounds;
                group.HasCollider = true;
            }
    }

    public bool IsBlocked(Vector3 origin, Vector3 endpoint, Transform ignore)
    {
        foreach (Island group in islands)
        {
            Vector3 localOrigin = group.Moving.InverseTransformPoint(origin);
            Vector3 localRay = group.Moving.InverseTransformPoint(endpoint) - localOrigin;
            float length = localRay.magnitude;
            if (length <= 0) continue;
            Vector3 direction = localRay / length;
            if (!group.Bounds.IntersectRay(new Ray(localOrigin, direction), out float entry) || entry > length) continue;
            int count;
            while ((count = group.World.Raycast(localOrigin, direction, group.Hits, length,
                       Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) == group.Hits.Length)
                Array.Resize(ref group.Hits, group.Hits.Length * 2);
            for (int i = 0; i < count; i++)
            {
                Transform hit = original[group.Hits[i].collider];
                if (ignore == null || (hit != ignore && !hit.IsChildOf(ignore))) return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        foreach (Island group in islands)
            if (group.Scene.isLoaded) SceneManager.UnloadSceneAsync(group.Scene);
    }
}
