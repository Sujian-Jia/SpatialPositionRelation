# 组内七轴机械臂装配标定

## 构型与模型入口

己方 `SpaceStation` 携带 `bq/基座/关节1/PRT2_SW0001/关节1坐标` 七轴分支。来访器 `CrewVehicle` 主动近距接近，组内 `轴/轴顶点` 是抓捕接口。对接端口仅在二次接触与锁紧时作为闭合基准。分离后两器按原生会话运动。原始 Referenced Prefab 为 [服务航天器.prefab](D:/Project/261121SpatialPositionRelation/SpatialPositionRelation/Assets/Prefabs/服务航天器.prefab)，候选包中的七段与安装件均复用其网格。

上一轮适配器为六段分配关节位置并按运行距离拉伸，保留了独立的模型基座和目标轴。新版从 FBX 顶点测量实际接合面。按近端到远端，将 CAD 实体 1、2、5、3、4、6、7 依次登记到七个原生段。源实体 3 和 4 的长杆都从其节点向下一关节伸出，源实体 5 是短执行器。原 CAD 节点序号直接用作段号会把长杆归到错误的段。第一段从 C++ `armMount` 起算，第七段收在 C++ `ArmTool`。基座后安装面和目标 `InsertionPoint` 贴合各自舱体网格表面。夹爪接触基准来自参考控制器 `basePlate`，目标接触基准来自 `轴顶点`。

## 标定和换模

- `SpatialArmCalibration.cs` 在项目侧生成挂载节点，使用原始 Mesh、材质和连杆轴向标定。舱体、基座、抓捕座之间的距离由原生安装点与舱体碰撞表面确定。
- `SpatialDynamicsAdapter.cs` 将原生位姿映射到 Unity，复用 `P = diag(1,1,−1)`。刚性段沿用标定，柔性第 4、5 段展示原生远端挠曲。原始 CAD Prefab 和 FBX `.meta` 保持原样。
- 更换组内或真实装配时，先在编辑器里比对 Prefab 的近远端坐标和组件层级，核对 `基座`、七个远端关节、`夹爪`、`轴顶点` 及 `InsertionPoint`。在 `SpatialSceneBuilder` 指定新的组内 Prefab，再在 `SpatialArmCalibration` 登记实装基准和米制尺寸。最后用 TCP 回归复核端口、捕获座、七段连接和机位。
- 组内模型两处装配跨距由舱体网格和原生挂点算得，基座约 1.370 m、捕获座约 0.334 m。该装配服务于本工况的算法和显示验收。实装尺寸校核需要实物图纸和接口坐标。

## 维护和回滚

- Qt 改动位于 `mainwindowui.cpp`、`telemetrydisplay.cpp`。文案原值为「空间站 · 组内参考模型」，新值为「空间站 · 参考模型」。待机画面保留「显示射线」开关。
- Unity 新增装配标定类、预测控制类、预测碰撞查询类及视场着色器。更新 `SpatialDynamicsAdapter`、`SpatialModelBindings`、`SpatialProtocol`、`SpatialRuntime`、两个传感器分部、生成器及其生成的场景和视场材质。
- 原生计算目录零改动。包装层 `Spr7Api.h/.cpp` 增加 `Spr7_Clone` 和 `Spr7_ReadCalibration`，保留 v1 样本布局。`regression.py` 和 `player-regression.py` 分别对照 C++ 和真实 Windows Player。原生参数、正逆运动学、接触、锁紧与释放时序沿用交付。
- 回滚模型标定时恢复生成场景及装配映射。回滚发布包时从 `dist-20261127_135723_996-before-mesh-verified` 恢复程序和依赖，保存当前数据目录。

详见 [机械臂归因报告](D:/Project/261121SpatialPositionRelation/SpatialPositionRelation/Docs/机械臂断续与附着_归因报告.md)。

## 原生与显示关键帧

下表报告最新 Player 实际收到的抽样帧。端面差为网格接合基准之间的最大距离。接触间距单列，保留原生动力学残差。

| 阶段 | 仿真时间 | 实体抓捕间距 | 原生抓捕间距 | 七段最大接合差 |
| --- | ---: | ---: | ---: | ---: |
| 初态 | 0.00 s | 330.0001 mm | 330.0000 mm | 1.77 μm |
| 软接触 | 6.62 s | 16.2938 mm | 16.2937 mm | 4.21 μm |
| 搬运 | 25.04 s | 0.070 μm | 0.00018 μm | 2.94 μm |
| 二次接触后、锁紧前 | 57.92 s | 0.179 μm | 低于 0.001 μm | 1.54 μm |
| 分离前 | 64.00 s | 2.4903 mm | 2.4903 mm | 2.07 μm |
| 分离后终态 | 80.00 s | 1.254102 m | 1.254102 m | 1.47 μm |

锁紧阶段原生端口间距与速度差为 0。实体接合误差反映 Unity 位姿应用和标定基准的数值一致性。
