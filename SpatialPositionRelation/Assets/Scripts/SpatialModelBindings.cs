using System;
using UnityEngine;

/// <summary>
/// 保存遮挡部件的 ID、显示名和分类，让一次碰撞查询能对应到人的部件名称。
/// 射线命中后可沿场景父级找此类组件及飞行器组件，区分两个引用同一模型的实例。
/// </summary>
public abstract class SpatialOccluderIdentityBase : MonoBehaviour
{
    public string partName;
    public string partId;
    public string displayName;
    public string category;
}

/// <summary>
/// 把预制体内散布的部件集中成一份可查的绑定表，供运行时控制与计算。
/// 预制体是场景对象及其子层级的可复用模板；这里的 Transform 引用指向已放进场景的实例节点。
/// Unity 序列化把可保存的字段值和对象引用写入场景或预制体资源，重新载入时再恢复。
/// </summary>
public abstract class SpatialCraftAssemblyBase : MonoBehaviour
{
    public string craftId;
    /// <summary>
    /// 按同一索引配对翼的铰链、零位旋转、铰链局部转轴和受光面法线。
    /// solarAngles 是运行中更新的角度数组；NonSerialized 表示不把这个字段存进场景或预制体。
    /// </summary>
    public Transform[] solarHinges = Array.Empty<Transform>();
    public Quaternion[] solarBaseRotations = Array.Empty<Quaternion>();
    public Vector3[] solarLocalAxes = Array.Empty<Vector3>();
    public Vector3[] solarFaceNormals = Array.Empty<Vector3>();
    [NonSerialized] public float[] solarAngles;
    /// <summary>
    /// 区分给演示对接用的舱体端面基准与给视觉采样用的 dockingPort 标靶。
    /// 二者都由 Transform 提供位置和轴方向，来源可追到编辑器构建脚本的 CreateCraft。
    /// </summary>
    public Transform demoDockPort;
    public Transform referenceVisual, dynamicsPort;
    public Transform[] armJoints = Array.Empty<Transform>();
    public Quaternion[] armBaseRotations = Array.Empty<Quaternion>();
    public Transform armElbow;
    public Transform armWrist;
    public Transform dockingPort;
    public Transform[] cameraInstallations = Array.Empty<Transform>();

    /// <summary>
    /// 按传感器 ID 找模型上的安装基准；没有匹配项就返回 null，让调用方决定是否停用该机位。
    /// 这里用节点名与 sensorId 比较，没有用坐标距离查找。
    /// </summary>
    public Transform FindCameraInstallation(string sensorId)
    {
        foreach (Transform installation in cameraInstallations)
            if (installation != null && installation.name == sensorId) return installation;
        return null;
    }
}
