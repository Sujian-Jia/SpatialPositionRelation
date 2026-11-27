using UnityEngine;

/// <summary>
/// 给会挡住视线的模型部件贴身份标签，使射线命中后能找到部件名称和归属。
/// 继承的字段保存标签内容；是否会被射线命中由物理碰撞体决定。
/// </summary>
public sealed class SpatialOccluderIdentity : SpatialOccluderIdentityBase
{
}
