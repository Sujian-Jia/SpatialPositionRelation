using UnityEngine;

/// <summary>
/// 这是场景里实际挂载的仿真组件；从这里跳到 SpatialSimulationBase 看每帧计算。
/// 冒号表示继承，所以这里没有重复写 Start 和 Update。
/// </summary>
public sealed class SpatialSimulation : SpatialSimulationBase
{
}
