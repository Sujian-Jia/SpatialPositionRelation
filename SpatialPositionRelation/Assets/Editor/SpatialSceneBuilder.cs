using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SpatialSceneBuilder
{
    private const string ScenePath = "Assets/Scenes/SpatialDemo.unity";
    private const string MaterialRoot = "Assets/Spatial/Materials";
    private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

    /// <summary>
    /// 在编辑器中搭建完整演示场景，把两套预制体实例、灯、相机和通信组件连起来。
    /// MenuItem 把静态方法放到 Unity 菜单里；运行时代码读的是已经保存的场景引用。
    /// </summary>
    [MenuItem("Spatial Demo/Build Demo Scene")]
    public static void BuildDemoScene()
    {
        EnsureFolder("Assets/Spatial");
        EnsureFolder(MaterialRoot);
        CreateMaterials();
        Material skybox = LoadStarfield();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.16f, 0.19f, 0.23f);
        RenderSettings.skybox = skybox;
        // Preserve the dark default reflection probe from the previous scene.
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
        RenderSettings.customReflection = null;

        SpatialCraftAssembly station = CreateCraft("SpaceStation", "己方航天器", "Assets/Prefabs/服务航天器.prefab");
        SpatialCraftAssembly crew = CreateCraft("CrewVehicle", "目标航天器", "Assets/Prefabs/服务航天器.prefab");
        CreateCraft("CargoVehicle", "货运飞船", "Assets/Prefabs/bianqueCraft.prefab");
        CreateCraft("OpticalModule", "光学仓", "Assets/Prefabs/bianqueCraft.prefab");
        if (station.armJoints.Length != 7 || crew.armJoints.Length != 7)
            throw new InvalidOperationException("服务航天器 reference prefab must expose the seven imported joint coordinates.");
        if (station.solarHinges.Length != 2 || crew.solarHinges.Length != 2 || station.dockingPort == null
            || station.dockingPort.name != "机械臂到达点的坐标")
            throw new InvalidOperationException("服务航天器 reference prefab is missing its imported solar wings or docking anchor.");

        SpatialSensorView[] sensors = CreateSensors(station, Materials["frustum"]);
        Camera overview = CreateOverviewCamera();
        Light sun = CreateSun();
        AddEarth();
        // 参考场景的太阳具有方向光和可视日盘。此处用总览图层呈现可视日盘。
        GameObject sunDisc = CreateVisualSphere(null, "Sun_Visual_ReferenceScale", Vector3.zero, 0.55f, Materials["sun"]);
        sunDisc.layer = LayerMask.NameToLayer("SpatialOverviewOverlay");
        LineRenderer sunDirectionLine = CreateSunDirectionLine();

        GameObject system = new GameObject("SpatialPositionRelation_Runtime");
        SpatialTcpBridge bridge = system.AddComponent<SpatialTcpBridge>();
        SpatialSimulation simulation = system.AddComponent<SpatialSimulation>();
        simulation.tcpBridge = bridge;
        simulation.sunLight = sun;
        simulation.sunDisc = sunDisc.transform;
        simulation.dockingTarget = station.dockingPort;
        simulation.craft = UnityEngine.Object.FindObjectsByType<SpatialCraftAssembly>(FindObjectsSortMode.None);
        simulation.sensorViews = sensors;
        simulation.overviewCamera = overview;
        simulation.embeddedViews = system.AddComponent<SpatialEmbeddedViews>();
        simulation.embeddedViews.overviewCamera = overview;
        simulation.embeddedViews.sensorViews = sensors;
        simulation.embeddedViews.tcpBridge = bridge;
        simulation.sunDirectionLine = sunDirectionLine;
        simulation.transform.SetAsFirstSibling();

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new InvalidOperationException("Could not save scene: " + ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("SPATIAL_SCENE_BUILD_OK scene=" + ScenePath + " craft=" + simulation.craft.Length
                  + " sensors=" + sensors.Length + " referencePrefabs=2 syntheticCraftMeshes=0 armJoints=7 solarHinges=2");
    }

    /// <summary>
    /// 先生成演示场景，再用它打包独立运行的 Windows Player，让 Qt 可以启动这个可执行程序。
    /// BuildPlayerOptions 指定场景和输出路径；这是编辑器构建入口，不会在 Player 每帧执行。
    /// </summary>
    public static void BuildWindowsPlayer()
    {
        BuildDemoScene();
        string output = Path.GetFullPath("Build/Windows/SpatialUnity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        PlayerSettings.productName = "SpatialUnity";
        PlayerSettings.companyName = "SpatialPositionRelation";
        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 800;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, "com.spatialpositionrelation.demo");
        BuildPlayerOptions options = new BuildPlayerOptions {
            scenes = new[] { ScenePath },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Windows Player build result: " + report.summary.result
                                                + ", errors=" + report.summary.totalErrors);
        Debug.Log("SPATIAL_PLAYER_BUILD_OK path=" + output + " bytes=" + report.summary.totalSize
                  + " warnings=" + report.summary.totalWarnings);
    }

    private static Material LoadStarfield()
    {
        const string folder = "Assets/Spatial/Skybox/";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(folder + "Skybox.mat");
        if (material == null || material.shader.name != "Skybox/6 Sided")
            throw new InvalidOperationException("Reference six-sided starfield material is missing.");
        foreach (string face in new[] { "Front", "Back", "Left", "Right", "Up", "Down" })
        {
            string path = folder + face + "_Tex.png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null || material.GetTexture("_" + face + "Tex") == null)
                throw new InvalidOperationException("Missing starfield face: " + path);
            TextureImporterPlatformSettings standalone = importer.GetPlatformTextureSettings("Standalone");
            bool changed = importer.maxTextureSize != 2048 || importer.wrapMode != TextureWrapMode.Clamp
                || importer.alphaSource != TextureImporterAlphaSource.None || !standalone.overridden
                || standalone.maxTextureSize != 2048 || standalone.format != TextureImporterFormat.DXT1;
            if (changed)
            {
                importer.maxTextureSize = 2048;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.alphaSource = TextureImporterAlphaSource.None;
                standalone.overridden = true;
                standalone.maxTextureSize = 2048;
                standalone.format = TextureImporterFormat.DXT1;
                importer.SetPlatformTextureSettings(standalone);
                importer.SaveAndReimport();
            }
        }
        Debug.Log("SPATIAL_STARFIELD_OK faces=6 maxSize=2048 textureFormat=DXT1");
        return material;
    }

    private static void CreateMaterials()
    {
        Materials.Clear();
        Materials["earth"] = MakeMaterial("Earth", Color.white, 0.08f, 0.18f);
        Materials["earth"].mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Assets/Spatial/Materials/2k_earth_daymap.jpg");
        if (Materials["earth"].mainTexture == null)
            throw new InvalidOperationException("Reference Earth texture is missing from Assets/Spatial/Materials.");
        Materials["sun"] = MakeMaterial("Sun", new Color(1f, 0.72f, 0.32f), 0f, 0.1f);
        Shader arrowShader = Shader.Find("Unlit/Color");
        if (arrowShader != null)
        {
            Materials["sun"].shader = arrowShader;
            Materials["sun"].color = new Color(1f, 0.72f, 0.32f);
            EditorUtility.SetDirty(Materials["sun"]);
        }
        Materials["frustum"] = MakeMaterial("SensorFrustum", new Color(0.24f, 0.88f, 0.72f), 0.2f, 0.05f);
        if (arrowShader != null)
        {
            Materials["frustum"].shader = arrowShader;
            Materials["frustum"].color = new Color(0.27f, 0.96f, 0.65f);
            EditorUtility.SetDirty(Materials["frustum"]);
        }
        Materials["referenceCraft"] = MakeMaterial("ReferenceSpacecraftFallback", new Color(0.56f, 0.62f, 0.67f), 0.48f, 0.28f);
    }

    private static Material MakeMaterial(string name, Color color, float metallic, float smoothness)
    {
        string path = MaterialRoot + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("No supported shader found for " + name);
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.name = name;
        material.color = color;
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        if (name == "Sun" && material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 2.1f);
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>
    /// 把一个飞行器预制体实例转成运行时可操控、可查询的飞行器。
    /// 太阳翼在各铰链的局部坐标中用包围盒最长轴作转轴、最短轴作初始法线，再将法线符号按机体前向统一。
    /// 若找到舱体，演示口取包围盒的一个 X 端面，Y/Z 取中心；追踪器取 +X 端，其余取 −X 端。
    /// </summary>
    private static SpatialCraftAssembly CreateCraft(string id, string displayName, string prefabPath)
    {
        if (string.IsNullOrEmpty(prefabPath))
            throw new InvalidOperationException("A copied reference spacecraft prefab is required for " + id);

        GameObject root = new GameObject(id);
        root.name = displayName + " [" + id + "]";
        SpatialCraftAssembly assembly = root.AddComponent<SpatialCraftAssembly>();
        assembly.craftId = id;
        GameObject model = AddReferenceVisual(root.transform, prefabPath, id);
        if (model == null) throw new InvalidOperationException("Could not instantiate reference prefab: " + prefabPath);

        assembly.referenceVisual = model.transform;
        assembly.dynamicsPort = new GameObject("7DOF 对接口参考系").transform;
        assembly.dynamicsPort.SetParent(root.transform, false);
        assembly.dockingPort = FindDockingAnchor(model.transform);
        assembly.armJoints = FindLongestJointChain(model.transform);
        assembly.armBaseRotations = CaptureLocalRotations(assembly.armJoints);
        assembly.solarHinges = FindSolarHinges(model.transform);
        assembly.solarBaseRotations = CaptureLocalRotations(assembly.solarHinges);
        assembly.solarLocalAxes = new Vector3[assembly.solarHinges.Length];
        assembly.solarFaceNormals = new Vector3[assembly.solarHinges.Length];
        Vector3[] basis = { Vector3.right, Vector3.up, Vector3.forward };
        for (int i = 0; i < assembly.solarHinges.Length; i++)
        {
            Transform hinge = assembly.solarHinges[i];
            Bounds wing = CalculateBoundsInParentSpace(hinge.GetComponentsInChildren<Renderer>(true), hinge);
            int[] axes = { 0, 1, 2 };
            Array.Sort(axes, (left, right) => wing.size[left].CompareTo(wing.size[right]));
            // 用户的对日演示绕板面长边。镜像翼的导入局部轴不同，分别在各自铰链零位求轴。
            assembly.solarLocalAxes[i] = basis[axes[2]];
            Vector3 normal = basis[axes[0]];
            // 将镜像翼的名义受光面统一到机体参考方向，后续可按标定替换。
            if (Vector3.Dot(hinge.TransformDirection(normal), root.transform.forward) < 0) normal = -normal;
            assembly.solarFaceNormals[i] = normal;
            Debug.Log($"SPATIAL_WING_GEOMETRY craft={id} hinge={hinge.name} localBounds={wing.size:F4} "
                + $"span={assembly.solarLocalAxes[i]:F0} face={normal:F0} worldSpan={hinge.TransformDirection(assembly.solarLocalAxes[i]):F3}");
        }
        Transform hull = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(item => item.name == "航天器本身");
        if (hull != null)
        {
            Bounds hullBounds = CalculateBoundsInParentSpace(hull.GetComponentsInChildren<Renderer>(true), root.transform);
            bool approachSide = id == "SpaceStation";
            var port = new GameObject("演示对接基准_舱体端面").transform;
            port.SetParent(root.transform, false);
            port.localPosition = new Vector3(approachSide ? hullBounds.max.x : hullBounds.min.x, hullBounds.center.y, hullBounds.center.z);
            port.localRotation = Quaternion.LookRotation(approachSide ? Vector3.right : Vector3.left, Vector3.up);
            assembly.demoDockPort = port;
            Debug.Log($"SPATIAL_DEMO_PORT craft={id} pos={port.localPosition:F4} outward={port.forward:F3} hull={hullBounds.size:F4}");
        }
        if (assembly.armJoints.Length > 0)
        {
            assembly.armElbow = assembly.armJoints[Mathf.Min(3, assembly.armJoints.Length - 1)];
            assembly.armWrist = assembly.armJoints[assembly.armJoints.Length - 1];
        }

        assembly.cameraInstallations = CreateCameraInstallations(assembly, hull != null ? hull : model.transform);

        string solarAxes = string.Join(";", assembly.solarHinges.Select((hinge, index) =>
            hinge.name + " position=" + hinge.localPosition.ToString("F3")
            + " localRotation=" + hinge.localRotation.eulerAngles.ToString("F1")
            + " selectedLocalAxis=" + assembly.solarLocalAxes[index].ToString("F0")
            + " worldAxis=" + hinge.TransformDirection(assembly.solarLocalAxes[index]).ToString("F3")));
        Debug.Log("SPATIAL_REFERENCE_ARTICULATION craft=" + id + " joints=" + assembly.armJoints.Length
                  + " jointChain=" + string.Join(">", assembly.armJoints.Select(joint => joint.name))
                  + " solarHinges=" + solarAxes
                  + " solarRotationAxis=parallel-per-hinge-reference-frame"
                  + " dockingAnchor=" + assembly.dockingPort.name);
        return assembly;
    }

    private static Transform[] CreateCameraInstallations(SpatialCraftAssembly craft, Transform hull)
    {
        Physics.SyncTransforms();
        var installations = new List<Transform>();
        AddCameraInstallation(installations, craft.transform, hull, null, "CRDS-PORT", 0.25f, 0);
        AddCameraInstallation(installations, craft.transform, hull, null, "TV-DOCK-WIDE", 0, 0.18f);
        AddCameraInstallation(installations, craft.transform, hull, null, "TV-DOCK-NARROW", 0, -0.18f);
        AddCameraInstallation(installations, craft.armWrist, craft.armWrist, null, "TV-ARM-WRIST", 0, 0);
        AddCameraInstallation(installations, craft.armWrist, craft.armWrist, null, "TV-ARM-WRIST-B", 0.12f, 0);
        AddCameraInstallation(installations, craft.armElbow, craft.armElbow,
            craft.armJoints.Length > 4 ? craft.armJoints[4] : null, "TV-ARM-ELBOW", 0, 0);
        return installations.ToArray();
    }

    /// <summary>
    /// 在没有标定镜头节点的模型上，用从外朝内的射线寻找镜头安装基准。
    /// 沿父物体的 +X 观察，筛掉未启用的碰撞体、触发器和指定下游关节子树；依次缩小横向偏移直到命中。
    /// 找到后在命中点沿光轴向外留 2 毫米；没找到就不创建安装点，运行态可据此停用对应机位。
    /// </summary>
    private static void AddCameraInstallation(List<Transform> installations, Transform parent,
        Transform geometry, Transform downstreamJoint, string id, float yOffsetM, float zOffsetM)
    {
        if (parent == null || geometry == null) return;
        Collider[] colliders = geometry.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy
                && (downstreamJoint == null || !collider.transform.IsChildOf(downstreamJoint))).ToArray();
        if (colliders.Length == 0) return;
        Bounds bounds = colliders[0].bounds;
        foreach (Collider collider in colliders) bounds.Encapsulate(collider.bounds);
        Vector3 forward = parent.right; // 默认光轴为安装基准 +X，采样自壳体外侧朝内。
        float radius = bounds.extents.magnitude + 0.1f;
        Vector3 offset = parent.up * yOffsetM + parent.forward * zOffsetM;
        RaycastHit nearest = default;
        bool found = false;
        foreach (float scale in new[] { 1f, 0.65f, 0.35f, 0f })
        {
            Ray ray = new Ray(bounds.center + forward * radius + offset * scale, -forward);
            foreach (Collider collider in colliders)
            {
                if (!collider.Raycast(ray, out RaycastHit hit, radius * 2) || (found && hit.distance >= nearest.distance))
                    continue;
                nearest = hit;
                found = true;
            }
            if (found) break;
        }
        if (!found)
        {
            Debug.LogWarning($"SPATIAL_CAMERA_INSTALLATION_MISSING sensor={id} parent={parent.name}");
            return;
        }
        // 参考模型没有已标定的镜头节点。安装基准位于命中的外表面，沿光轴留 2 mm 入口间隙。
        Transform anchor = new GameObject(id).transform;
        anchor.SetParent(parent, false);
        anchor.position = nearest.point + forward * 0.002f;
        installations.Add(anchor);
        Debug.Log($"SPATIAL_CAMERA_INSTALLATION sensor={id} parent={parent.name} part={nearest.collider.name} "
            + $"surfaceWorldM={nearest.point:F5} entranceWorldM={anchor.position:F5} axisWorld={forward:F5}");
    }

    /// <summary>
    /// 实例化飞行器模板，把模型居中缩放，并为几何射线补上物理碰撞体和部件标识。
    /// 预制体是一份对象层级模板，InstantiatePrefab 把它放进场景成为实例；从模板加载失败会抛出异常。
    /// 模型先绕 Y 转 90°；最长包围盒边超过 0.001 才按 5.5/最长边同比缩放，再按缩放后的盒中心平移居中。
    /// </summary>
    private static GameObject AddReferenceVisual(Transform parent, string path, string craftId)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new InvalidOperationException("Reference prefab unavailable: " + path);
        GameObject model = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (model == null) throw new InvalidOperationException("Could not instantiate reference prefab: " + path);
        model.name = "ReferenceAssembly_" + craftId;
        model.transform.SetParent(parent, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.Euler(0, 90, 0);
        foreach (Transform child in model.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
        foreach (MonoBehaviour behaviour in model.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour != null) behaviour.enabled = false;

        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidOperationException("Reference prefab has no renderable children: " + path);
        MeshFilter[] meshFilters = model.GetComponentsInChildren<MeshFilter>(true);
        int missingMeshes = meshFilters.Count(filter => filter.sharedMesh == null);
        if (missingMeshes > 0)
            throw new InvalidOperationException("Reference prefab has " + missingMeshes + " unresolved imported meshes: " + path);

        Bounds bounds = CalculateBoundsInParentSpace(renderers, parent);
        float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        float scale = longest > 0.001f ? 5.5f / longest : 1;
        model.transform.localScale *= scale;
        model.transform.localPosition = -bounds.center * scale;

        int fallbackMaterialSlots = 0;
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length == 0) materials = new[] { Materials["referenceCraft"] };
            else
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null) continue;
                    materials[i] = Materials["referenceCraft"];
                    fallbackMaterialSlots++;
                }
            }
            renderer.sharedMaterials = materials;
        }

        int addedMeshColliders = 0;
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
            MeshCollider collider = filter.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            addedMeshColliders++;
        }

        Collider[] colliders = model.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (collider is MeshCollider mesh && mesh.attachedRigidbody == null && !mesh.isTrigger)
                mesh.convex = false;
            SpatialOccluderIdentity identity = collider.GetComponent<SpatialOccluderIdentity>();
            if (identity == null) identity = collider.gameObject.AddComponent<SpatialOccluderIdentity>();
            string relativePath = GetModelPartPath(collider.transform, model.transform);
            identity.partId = craftId + "." + relativePath;
            identity.displayName = collider.gameObject.name;
            identity.category = GetPartCategory(relativePath);
            identity.partName = ReadablePartName(collider.transform, model.transform, identity.category);
        }

        Debug.Log("SPATIAL_REFERENCE_MODEL prefab=" + path + " craft=" + craftId
                  + " renderers=" + renderers.Length + " meshFilters=" + meshFilters.Length
                  + " unresolvedMeshes=" + missingMeshes + " fallbackMaterialSlots=" + fallbackMaterialSlots
                  + " colliders=" + colliders.Length + " addedMeshColliders=" + addedMeshColliders
                  + " instanceScale=" + scale + " centeredBoundsCenter=" + Vector3.zero
                  + " centeredBoundsSize=" + bounds.size * scale);
        return model;
    }

    /// <summary>
    /// 量模型在指定父物体坐标中的包围范围，给居中、缩放、对接基准和翼轴选择使用。
    /// 把各 Renderer 包围盒角点换到指定坐标后合并极值；包围盒用矩形盒近似包含模型。
    /// </summary>
    private static Bounds CalculateBoundsInParentSpace(Renderer[] renderers, Transform parent)
    {
        Bounds bounds = new Bounds();
        bool initialized = false;
        foreach (Renderer renderer in renderers)
        {
            Bounds localBounds = renderer.localBounds;
            Vector3 center = localBounds.center;
            Vector3 extents = localBounds.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localPoint = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 point = parent.InverseTransformPoint(renderer.transform.TransformPoint(localPoint));
                if (!initialized)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else bounds.Encapsulate(point);
            }
        }
        return bounds;
    }

    private static string GetModelPartPath(Transform part, Transform modelRoot)
    {
        List<string> segments = new List<string>();
        Transform current = part;
        while (current != null && current != modelRoot)
        {
            string name = current.name.Replace("/", "_").Replace("\\", "_").Replace(".", "_");
            segments.Add(name + "[" + current.GetSiblingIndex() + "]");
            current = current.parent;
        }
        segments.Reverse();
        return string.Join(".", segments);
    }

    private static string ReadablePartName(Transform part, Transform model, string category)
    {
        // 让标签包含有区分度的机构名，跳过 CAD 通用的“本身/坐标”父节点。
        string armPart = "";
        for (Transform node = part; node != null && node != model; node = node.parent)
        {
            string name = node.name;
            if (category == "SolarArray" &&
                (name.Contains("太阳翼") || name.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) >= 0))
                return name;
            if (category == "SelfArm" && armPart.Length == 0 &&
                (name.Contains("关节") || name.Contains("腕") || name.Contains("肘") || name.Contains("机械臂")))
                armPart = name.Contains("关节") ? "机械臂 · " + name : name;
            bool chinese = name.Any(c => c >= '\u4e00' && c <= '\u9fff');
            if (!chinese || name.Contains("坐标") || name.Contains("本身") || name.Contains("原点")) continue;
            if (category == "SolarArray") continue;
            if (category == "SelfArm" && name.Contains("关节")) continue;
            return name;
        }
        if (category == "SolarArray") return "太阳翼";
        if (category == "SelfArm") return armPart.Length == 0 ? "机械臂" : armPart;
        return "舱体结构件";
    }

    private static string GetPartCategory(string relativePath)
    {
        if (relativePath.IndexOf("关节", StringComparison.OrdinalIgnoreCase) >= 0
            || relativePath.IndexOf("机械臂", StringComparison.OrdinalIgnoreCase) >= 0
            || relativePath.IndexOf("腕", StringComparison.OrdinalIgnoreCase) >= 0)
            return "SelfArm";
        if (relativePath.IndexOf("太阳翼", StringComparison.OrdinalIgnoreCase) >= 0
            || relativePath.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) >= 0)
            return "SolarArray";
        return "SelfSpacecraft";
    }

    private static Transform FindDockingAnchor(Transform model)
    {
        Transform anchor = model.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(item => item.name == "机械臂到达点的坐标" || item.name == "接口类型1");
        if (anchor != null) return anchor;
        return model.parent != null ? model.parent : model;
    }

    private static Transform[] FindSolarHinges(Transform model)
    {
        Transform[] all = model.GetComponentsInChildren<Transform>(true);
        List<Transform> hinges = new List<Transform>();
        foreach (string name in new[] { "太阳翼1", "太阳翼2" })
        {
            Transform match = all.Where(item => item.name == name)
                .OrderByDescending(item => item.GetComponentsInChildren<Renderer>(true).Length)
                .FirstOrDefault();
            if (match != null) hinges.Add(match);
        }
        return hinges.ToArray();
    }

    private static Transform[] FindLongestJointChain(Transform model)
    {
        Transform[] all = model.GetComponentsInChildren<Transform>(true);
        List<Transform> best = new List<Transform>();
        foreach (Transform start in all.Where(item => JointIndexFromName(item.name) == 1))
        {
            List<Transform> chain = new List<Transform> { start };
            Transform current = start;
            for (int jointIndex = 2; jointIndex <= 7; jointIndex++)
            {
                Transform next = null;
                for (int childIndex = 0; childIndex < current.childCount; childIndex++)
                {
                    Transform child = current.GetChild(childIndex);
                    if (JointIndexFromName(child.name) != jointIndex) continue;
                    next = child;
                    break;
                }
                if (next == null) break;
                chain.Add(next);
                current = next;
            }
            if (chain.Count > best.Count) best = chain;
        }
        return best.ToArray();
    }

    private static int JointIndexFromName(string name)
    {
        if (!name.StartsWith("关节", StringComparison.Ordinal)) return 0;
        int indexEnd = 2;
        while (indexEnd < name.Length && char.IsDigit(name[indexEnd])) indexEnd++;
        if (indexEnd == 2 || name.IndexOf("坐标", indexEnd, StringComparison.Ordinal) != indexEnd) return 0;
        return int.TryParse(name.Substring(2, indexEnd - 2), out int index) ? index : 0;
    }

    private static Quaternion[] CaptureLocalRotations(Transform[] transforms)
    {
        return transforms.Select(item => item.localRotation).ToArray();
    }

    private static SpatialSensorView[] CreateSensors(SpatialCraftAssembly chaser, Material lineMaterial)
    {
        int overlayLayer = LayerMask.NameToLayer("SpatialOverviewOverlay");
        if (overlayLayer < 0) throw new InvalidOperationException("Missing SpatialOverviewOverlay layer.");
        string[] ids = {
            "CRDS-PORT", "TV-DOCK-WIDE", "TV-DOCK-NARROW",
            "TV-ARM-WRIST", "TV-ARM-WRIST-B", "TV-ARM-ELBOW"
        };
        SpatialSensorView[] views = new SpatialSensorView[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            GameObject sensorObject = new GameObject("SensorPose_" + ids[i]);
            Transform mount = chaser.transform;
            if (ids[i].StartsWith("TV-ARM-WRIST", StringComparison.Ordinal)) mount = chaser.armWrist;
            else if (ids[i] == "TV-ARM-ELBOW") mount = chaser.armElbow;
            sensorObject.transform.SetParent(mount != null ? mount : chaser.transform, false);
            SpatialSensorView view = sensorObject.AddComponent<SpatialSensorView>();
            view.sensorId = ids[i];
            GameObject cameraObject = new GameObject("SensorCamera_" + ids[i]);
            cameraObject.transform.SetParent(sensorObject.transform, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 2000;
            camera.depth = 5 + i;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.cullingMask &= ~(1 << overlayLayer);
            camera.backgroundColor = new Color(0.012f, 0.02f, 0.032f);
            camera.enabled = false;
            view.sensorCamera = camera;
            LineRenderer[] lines = new LineRenderer[4];
            for (int line = 0; line < lines.Length; line++)
            {
                GameObject lineObject = new GameObject("FOV_Ray_" + (line + 1));
                lineObject.layer = overlayLayer;
                lineObject.transform.SetParent(sensorObject.transform, false);
                LineRenderer renderer = lineObject.AddComponent<LineRenderer>();
                renderer.useWorldSpace = true;
                renderer.positionCount = 2;
                renderer.startWidth = 0.018f;
                renderer.endWidth = 0.018f;
                renderer.sharedMaterial = lineMaterial;
                renderer.enabled = false;
                lines[line] = renderer;
            }
            view.frustumLines = lines;
            views[i] = view;
        }
        return views;
    }

    private static GameObject CreateVisualSphere(Transform parent, string objectName, Vector3 position, float diameter, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        part.name = objectName;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = Vector3.one * diameter;
        part.GetComponent<Renderer>().sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
        return part;
    }

    private static Camera CreateOverviewCamera()
    {
        GameObject cameraObject = new GameObject("DemoOverviewCamera");
        cameraObject.transform.position = new Vector3(-11, 11, -24);
        cameraObject.transform.LookAt(Vector3.zero);
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.backgroundColor = new Color(0.006f, 0.012f, 0.021f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 1000;
        camera.fieldOfView = 56;
        return camera;
    }

    private static Light CreateSun()
    {
        GameObject sun = new GameObject("Sun_DirectionalLight");
        Light light = sun.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.3f;
        light.color = new Color(1, 0.93f, 0.79f);
        sun.transform.rotation = Quaternion.Euler(24, -38, 0);
        return light;
    }

    private static void AddEarth()
    {
        GameObject earth = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        earth.name = "Earth_Visual_ReferenceScale";
        earth.transform.position = new Vector3(4, -7, 15);
        earth.transform.localScale = Vector3.one * 10;
        earth.GetComponent<Renderer>().sharedMaterial = Materials["earth"];
        UnityEngine.Object.DestroyImmediate(earth.GetComponent<Collider>());
    }

    private static LineRenderer CreateSunDirectionLine()
    {
        GameObject lineObject = new GameObject("Sun_Direction_Arrow");
        lineObject.layer = LayerMask.NameToLayer("SpatialOverviewOverlay");
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 4;
        line.widthCurve = new AnimationCurve(new Keyframe(0, 0.075f), new Keyframe(0.76f, 0.075f),
            new Keyframe(0.77f, 0.42f), new Keyframe(1, 0));
        line.sharedMaterial = Materials["sun"];
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string folder = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folder);
    }
}
