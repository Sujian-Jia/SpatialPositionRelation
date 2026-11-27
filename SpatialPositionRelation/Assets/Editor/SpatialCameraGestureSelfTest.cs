using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Uses Unity's IMGUI dispatcher and the production OnGUI handler to test the actual camera transform.
/// <summary>
/// 这是项目已有的编辑器相机手势检查窗口，会把编辑器事件送给总览处理代码并比较相机位姿。
/// 下方通过反射按字符串名称访问私有方法和字段；这是从类型获取成员信息的机制。
/// </summary>
public sealed class SpatialCameraGestureSelfTest : EditorWindow
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo DrawEmbeddedViews = typeof(SpatialEmbeddedViews).GetMethod("OnGUI", PrivateInstance);
    private static readonly FieldInfo OverviewSlot = typeof(SpatialEmbeddedViews).GetField("overviewSlot", PrivateInstance);
    private static readonly Vector2 DragStart = new Vector2(420, 240);
    private SpatialEmbeddedViews views;
    private EventType expectedEvent;
    private int receivedEvents;
    private bool dispatching;

    // Window event dispatch requires a running Editor with its normal graphics context.
    public static void RunAndQuit()
    {
        EditorApplication.delayCall += () => {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        };
    }

    [MenuItem("Spatial Demo/Run Camera Gesture Checks")]
    public static void Run()
    {
        var test = CreateInstance<SpatialCameraGestureSelfTest>();
        var cameraObject = new GameObject("Camera Gesture Test") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            test.titleContent = new GUIContent("Camera Gesture Checks");
            test.position = new Rect(40, 40, 800, 500);
            test.ShowUtility();
            test.views = cameraObject.AddComponent<SpatialEmbeddedViews>();
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            test.views.overviewCamera = camera;
            OverviewSlot.SetValue(test.views, new Rect(0, 0, 800, 500));

            var focus = new Vector3(2, 3, 4);
            var offset = new Vector3(0, 17, -20);
            test.views.SetOverviewPose(focus, offset, 56);
            test.views.ApplySettings("fixed", Array.Empty<string>());
            test.views.SetOverviewPose(focus, offset, 56);
            Vector3 fixedPosition = camera.transform.position;
            Quaternion fixedRotation = camera.transform.rotation;
            test.Drag(2, EventModifiers.None, new Vector2(50, 20));
            test.Drag(0, EventModifiers.Alt, new Vector2(40, 15));
            Require(SamePose(camera, fixedPosition, fixedRotation), "FixedModeIgnoresGestures");

            test.views.ApplySettings("drag", Array.Empty<string>());
            Vector3 expectedPan = (camera.transform.up * 20 - camera.transform.right * 50)
                                  * (offset.magnitude * 0.0018f);
            test.Drag(2, EventModifiers.None, new Vector2(50, 20));
            Require(SamePose(camera, fixedPosition + expectedPan, fixedRotation), "MiddleDragTranslatesWithoutRotating");
            Debug.Log("SPATIAL_CAMERA_GESTURE panMeters=" + expectedPan.ToString("F4"));

            test.Drag(0, EventModifiers.Alt, new Vector2(40, 15));
            Vector3 freeFocus = focus + expectedPan;
            Vector3 rotatedOffset = Quaternion.Euler(-15 * 0.32f, 40 * 0.32f, 0) * offset;
            Require(Vector3.Distance(camera.transform.position, freeFocus + rotatedOffset) < 0.0001f
                    && Quaternion.Angle(camera.transform.rotation, fixedRotation) > 1f
                    && Vector3.Dot(camera.transform.forward, (freeFocus - camera.transform.position).normalized) > 0.99999f,
                    "AltLeftDragOrbitsPanAdjustedFocus");
            Debug.Log("SPATIAL_CAMERA_GESTURE orbitDegrees=" + Quaternion.Angle(camera.transform.rotation, fixedRotation).ToString("F4"));

            test.Send(EventType.ScrollWheel, 0, EventModifiers.None, Vector2.up * -3);
            float expectedZoom = Mathf.Exp(-3 * 0.12f);
            Require(Vector3.Distance(camera.transform.position, freeFocus + rotatedOffset * expectedZoom) < 0.0001f,
                    "ScrollZoomsFreePose");
            Vector3 freePosition = camera.transform.position;
            Quaternion freeRotation = camera.transform.rotation;
            test.views.ApplySettings("fixed", Array.Empty<string>());
            test.views.SetOverviewPose(focus, offset, 56);
            Require(SamePose(camera, fixedPosition, fixedRotation), "FixedPoseRestored");
            test.views.ApplySettings("drag", Array.Empty<string>());
            test.views.SetOverviewPose(focus, offset, 56);
            Require(SamePose(camera, freePosition, freeRotation), "FreePoseRestoresPanOrbitAndZoom");
            Debug.Log("SPATIAL_CAMERA_GESTURE_TESTS_PASS cases=6");
        }
        finally
        {
            test.Close();
            Object.DestroyImmediate(cameraObject);
        }
    }

    private void Drag(int button, EventModifiers modifier, Vector2 delta)
    {
        Send(EventType.MouseDown, button, modifier, Vector2.zero);
        Send(EventType.MouseDrag, button, modifier, delta);
        Send(EventType.MouseUp, button, modifier, delta);
    }

    private void Send(EventType type, int button, EventModifiers modifier, Vector2 delta)
    {
        expectedEvent = type;
        int before = receivedEvents;
        dispatching = true;
        try
        {
            SendEvent(new Event {
                type = type, mousePosition = DragStart + delta, delta = delta, button = button,
                modifiers = modifier, clickCount = 1
            });
        }
        finally
        {
            dispatching = false;
        }
        if (receivedEvents != before + 1)
            throw new InvalidOperationException("Camera test IMGUI event was not dispatched: " + type);
    }

    private void OnGUI()
    {
        if (!dispatching || views == null || Event.current.type != expectedEvent) return;
        DrawEmbeddedViews.Invoke(views, null);
        receivedEvents++;
    }

    private static bool SamePose(Camera camera, Vector3 position, Quaternion rotation)
    {
        return Vector3.Distance(camera.transform.position, position) < 0.0001f
            && Quaternion.Angle(camera.transform.rotation, rotation) < 0.05f;
    }

    private static void Require(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException("Camera gesture check failed: " + name);
        Debug.Log("SPATIAL_CAMERA_GESTURE_CASE_PASS " + name);
    }
}
