using UnityEngine;

/// <summary>
/// 这是场景里实际挂载的通信组件；收发工作继承自 SpatialTcpBridgeBase。
/// DefaultExecutionOrder 的 −100 把它排在默认顺序值 0 之前。
/// </summary>
[DefaultExecutionOrder(-100)]
public sealed class SpatialTcpBridge : SpatialTcpBridgeBase
{
}
