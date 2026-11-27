using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 这是项目已有的编辑器排查工具，用不同场景或组件组合构建 Player，以便定位启动问题。
/// 这里的 smoke 指快速覆盖启动路径的冒烟检查入口，正式演示构建入口是 SpatialSceneBuilder.BuildWindowsPlayer。
/// </summary>
public static class SpatialPlayerSmokeTest
{
    public static void BuildDefaultScenePlayer()
    {
        string output = Path.GetFullPath("../evidence/build/unity-smoke/SpatialSmoke.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Smoke Player build failed: " + report.summary.result);
        Debug.Log("SPATIAL_DEFAULT_SCENE_SMOKE_BUILD_OK path=" + output
                  + " bytes=" + report.summary.totalSize);
    }

    public static void BuildWithoutReferencePrefabsPlayer()
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/SpatialDemo.unity", OpenSceneMode.Single);
        var referenceRoots = new System.Collections.Generic.List<GameObject>();
        foreach (SpatialCraftAssembly craft in UnityEngine.Object.FindObjectsByType<SpatialCraftAssembly>(FindObjectsSortMode.None))
        {
            Transform reference = craft.transform.Find("ReferenceAssembly_" + craft.craftId);
            if (reference != null) referenceRoots.Add(reference.gameObject);
        }
        foreach (GameObject referenceRoot in referenceRoots) UnityEngine.Object.DestroyImmediate(referenceRoot);

        string variantScene = "Assets/Scenes/SpatialDemo_NoReferenceTest.unity";
        if (!EditorSceneManager.SaveScene(scene, variantScene))
            throw new InvalidOperationException("Could not save no-reference test scene.");

        string output = Path.GetFullPath("../evidence/build/unity-no-reference/SpatialNoReference.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { variantScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        AssetDatabase.DeleteAsset(variantScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("No-reference Player build failed: " + report.summary.result);
        Debug.Log("SPATIAL_NO_REFERENCE_PLAYER_BUILD_OK path=" + output
                  + " bytes=" + report.summary.totalSize);
    }

    public static void BuildReferencePrefabIsolationPlayers()
    {
        BuildReferencePrefabVariant(new[] { "SpaceStation", "CrewVehicle" }, "mixed-pair");
        BuildReferencePrefabVariant(new[] { "CrewVehicle", "CargoVehicle" }, "duplicate-bianque");
        Debug.Log("SPATIAL_REFERENCE_PREFAB_COMBINATION_BUILDS_OK variants=2");
    }

    public static void BuildAllReferencePrefabResavedPlayer()
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/SpatialDemo.unity", OpenSceneMode.Single);
        EditorSceneManager.MarkSceneDirty(scene);
        string variantScene = "Assets/Scenes/SpatialDemo_ReferenceTest_all-resaved.unity";
        if (!EditorSceneManager.SaveScene(scene, variantScene))
            throw new InvalidOperationException("Could not save full reference test scene.");

        string output = Path.GetFullPath("../evidence/build/unity-reference-isolation/all-resaved/SpatialUnity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { variantScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        AssetDatabase.DeleteAsset(variantScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Resaved full reference Player build failed: " + report.summary.result);
        Debug.Log("SPATIAL_ALL_REFERENCE_RESAVED_PLAYER_BUILD_OK path=" + output
                  + " bytes=" + report.summary.totalSize);
    }

    public static void BuildTcpBridgeOnlyPlayer()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject cameraObject = new GameObject("SmokeCamera");
        cameraObject.AddComponent<Camera>();
        GameObject bridgeObject = new GameObject("SmokeTcpBridge");
        bridgeObject.AddComponent<SpatialTcpBridge>();

        string testScene = "Assets/Scenes/SpatialTcpBridgeTest.unity";
        if (!EditorSceneManager.SaveScene(scene, testScene))
            throw new InvalidOperationException("Could not save TCP bridge smoke scene.");
        string output = Path.GetFullPath("../evidence/build/unity-tcp-bridge-only/SpatialBridge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { testScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        AssetDatabase.DeleteAsset(testScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("TCP bridge smoke build failed: " + report.summary.result);
        Debug.Log("SPATIAL_TCP_BRIDGE_ONLY_PLAYER_BUILD_OK path=" + output
                  + " bytes=" + report.summary.totalSize);
    }

    public static void BuildMinimalSimulationPlayer()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.2f);

        GameObject cameraObject = new GameObject("SimulationCamera");
        cameraObject.transform.position = new Vector3(-12, 8, -24);
        cameraObject.transform.LookAt(Vector3.zero);
        Camera camera = cameraObject.AddComponent<Camera>();

        GameObject sunObject = new GameObject("SimulationSun");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;

        SpatialCraftAssembly target = CreateSmokeCraft("SpaceStation", new Vector3(0, 0, 0));
        SpatialCraftAssembly chaser = CreateSmokeCraft("CrewVehicle", new Vector3(-20, 0, 0));
        target.dockingPort = target.transform;
        chaser.dockingPort = chaser.transform;

        GameObject system = new GameObject("SpatialSimulationSmoke");
        SpatialTcpBridge bridge = system.AddComponent<SpatialTcpBridge>();
        SpatialSimulation simulation = system.AddComponent<SpatialSimulation>();
        simulation.tcpBridge = bridge;
        simulation.sunLight = sun;
        simulation.dockingTarget = target.transform;
        simulation.craft = new[] { target, chaser };
        simulation.sensorViews = Array.Empty<SpatialSensorView>();
        simulation.overviewCamera = camera;

        string testScene = "Assets/Scenes/SpatialSimulationTest.unity";
        if (!EditorSceneManager.SaveScene(scene, testScene))
            throw new InvalidOperationException("Could not save simulation smoke scene.");
        string output = Path.GetFullPath("../evidence/build/unity-minimal-simulation/SpatialSimulation.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { testScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        AssetDatabase.DeleteAsset(testScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Minimal simulation smoke build failed: " + report.summary.result);
        Debug.Log("SPATIAL_MINIMAL_SIMULATION_PLAYER_BUILD_OK path=" + output
                  + " bytes=" + report.summary.totalSize);
    }

    public static void BuildSpatialComponentIsolationPlayers()
    {
        Scene craftScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AddSmokeCamera();
        new GameObject("SpatialTcpBridge").AddComponent<SpatialTcpBridge>();
        SpatialCraftAssembly craft = new GameObject("CraftAssemblyOnly").AddComponent<SpatialCraftAssembly>();
        craft.craftId = "SpaceStation";
        craft.solarHinges = Array.Empty<Transform>();
        craft.armJoints = Array.Empty<Transform>();
        SaveBuildAndDelete(craftScene, "SpatialCraftAssemblyTest",
                           "../evidence/build/unity-component-isolation/craft-only/SpatialUnity.exe",
                           "SPATIAL_CRAFT_ASSEMBLY_ONLY_PLAYER_BUILD_OK");

        Scene simulationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Camera camera = AddSmokeCamera();
        SpatialTcpBridge bridge = new GameObject("SpatialTcpBridge").AddComponent<SpatialTcpBridge>();
        SpatialSimulation simulation = new GameObject("SpatialSimulation").AddComponent<SpatialSimulation>();
        simulation.enabled = false;
        simulation.tcpBridge = bridge;
        simulation.overviewCamera = camera;
        simulation.craft = Array.Empty<SpatialCraftAssembly>();
        simulation.sensorViews = Array.Empty<SpatialSensorView>();
        SaveBuildAndDelete(simulationScene, "SpatialSimulationDisabledTest",
                           "../evidence/build/unity-component-isolation/simulation-only/SpatialUnity.exe",
                           "SPATIAL_DISABLED_SIMULATION_ONLY_PLAYER_BUILD_OK");
        Debug.Log("SPATIAL_COMPONENT_ISOLATION_BUILDS_OK variants=2");
    }

    private static Camera AddSmokeCamera()
    {
        GameObject cameraObject = new GameObject("SmokeCamera");
        cameraObject.transform.position = new Vector3(-12, 8, -24);
        cameraObject.transform.LookAt(Vector3.zero);
        return cameraObject.AddComponent<Camera>();
    }

    private static void SaveBuildAndDelete(Scene scene, string sceneName, string outputPath, string sentinel)
    {
        string testScene = "Assets/Scenes/" + sceneName + ".unity";
        if (!EditorSceneManager.SaveScene(scene, testScene))
            throw new InvalidOperationException("Could not save diagnostic scene: " + sceneName);
        string output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { testScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        AssetDatabase.DeleteAsset(testScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Diagnostic Player build failed: " + sceneName);
        Debug.Log(sentinel + " path=" + output + " bytes=" + report.summary.totalSize);
    }

    private static SpatialCraftAssembly CreateSmokeCraft(string id, Vector3 position)
    {
        GameObject root = new GameObject(id);
        root.transform.position = position;
        SpatialCraftAssembly craft = root.AddComponent<SpatialCraftAssembly>();
        craft.craftId = id;
        craft.solarHinges = Array.Empty<Transform>();
        craft.armJoints = Array.Empty<Transform>();
        GameObject hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hull.name = id + "_Hull";
        hull.transform.SetParent(root.transform, false);
        hull.transform.localScale = new Vector3(4, 2, 2);
        return craft;
    }

    private static void BuildReferencePrefabVariant(string[] keepCraftIds, string variantName)
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/SpatialDemo.unity", OpenSceneMode.Single);
        var referenceRoots = new System.Collections.Generic.List<GameObject>();
        foreach (SpatialCraftAssembly craft in UnityEngine.Object.FindObjectsByType<SpatialCraftAssembly>(FindObjectsSortMode.None))
        {
            if (Array.IndexOf(keepCraftIds, craft.craftId) >= 0) continue;
            Transform reference = craft.transform.Find("ReferenceAssembly_" + craft.craftId);
            if (reference != null) referenceRoots.Add(reference.gameObject);
        }
        foreach (GameObject referenceRoot in referenceRoots) UnityEngine.Object.DestroyImmediate(referenceRoot);

        string variantScene = "Assets/Scenes/SpatialDemo_ReferenceTest_" + variantName + ".unity";
        if (!EditorSceneManager.SaveScene(scene, variantScene))
            throw new InvalidOperationException("Could not save reference test scene: " + variantName);

        string output = Path.GetFullPath("../evidence/build/unity-reference-isolation/" + variantName + "/SpatialUnity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { variantScene },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        AssetDatabase.DeleteAsset(variantScene);
        AssetDatabase.SaveAssets();
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Reference isolation build failed: " + variantName
                                                + " / " + report.summary.result);
        Debug.Log("SPATIAL_REFERENCE_ISOLATION_BUILD_OK variant=" + variantName
                  + " bytes=" + report.summary.totalSize);
    }
}
