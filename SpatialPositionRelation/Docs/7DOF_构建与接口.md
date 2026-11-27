# 7DOF 方案 A · 构建、接口与模型标定

2026-11-27。主体和符号约定以 [7DOF 集成门禁](7DOF_集成门禁.md) 的方案 A 为准。`station` 是己方，拥有七关节机械臂及六路敏感器。`visitor` 是目标，负责初段主动接近。SPR/1 `chaser` 仍指己方，ID 为 `SpaceStation`。目标 ID 为 `CrewVehicle`。两器实例均使用组内 `Assets/Prefabs/服务航天器.prefab`。

## 构建与运行

从项目根目录执行 Windows PowerShell 命令。当前环境使用 Qt 6.12.0 MinGW x64、MinGW 13.1、Unity 2022.3.62f3、Git 和 Python 3。构建会从 7DOF 原始交付及其 `validation/composite` 加载模型和基线。

```powershell
powershell -ExecutionPolicy Bypass -File Spatial_Qt/build.ps1
```

构建先运行 native Release 两组自检、`arm` 和 `station` 全程基线比对，再运行 Unity 算法自检、构建 Win64 Player、构建 Qt Release 并部署。`-DistributionDirectory` 可以输出到独立验证目录。Qt、Unity 和 native 构建日志在 `Spatial_Qt/SpatialPositionRelation/build/logs`。原生 CSV、事件时钟对比与 DLL 位于 `Spatial_Qt/SpatialPositionRelation/build/native7dof`。

双击 `dist/SpatialPositionRelation.exe`。点「播放」推进 arm 工况，「暂停」冻结当前步骤，「复位」返回原生初态。「倍率」控制逐步推进的速度，轨道页倍率控制太阳与地影显示。固有的仿真步长为 0.001 s，原生样本间隔为 0.02 s。Unity 主线程按时间累加，保留未执行的子步时间，设备吞吐不足时实际播放时间可能长于倍率换算。点「运行状态」→「查看中文运行记录」查看用户日志。窗口、相机、帆板与记录的操作见发布包使用说明。

```powershell
py -3 SpatialPositionRelation/Native7Dof/player-regression.py dist/Unity/SpatialUnity.exe dist/config/default_config.json 7DOF_masscenter_real_constraint/validation/composite/results_arm_composite.csv Spatial_Qt/SpatialPositionRelation/build/native7dof/player-regression
```

## SPR/1 与 P/Invoke

- 保留 UTF-8 JSONL 和原有 `state`、`telemetry` 类型。`demoDocking` 控制播放，`demoRunId` 变化时重建会话，`dynamicsSpeed` 取 0.25 至 16。旧关节、距离、姿态和轨迹输入可出现在报文中，动力学状态由本机原生会话输出。
- `telemetry.dynamics` 带 `timeSec`、`mode`、`jointAnglesRad`、`eventTimesSec`、两器质心/端口位姿、速度、工具位姿及应用后的参考系。距离字段为 `portDistanceM`、`centerDistanceM`、`axialGapM`、`toolDistanceM`。`telemetry.jointsDeg` 保留度制，`relativePositionUnity` 为己方质心减目标质心的 Unity 世界米制向量。预报字段保留，本工况的 `trajectoryForecastAvailable=false`。
- ABI 在 `Native7Dof/Spr7Api.h`。`Spr7_Create` 固定选择 `ArmCapture` 和 `RigidComposite`。`Spr7_Advance` 限制在 20 步采样边界，`Spr7_Read` 只在采样边界读取，`Spr7_Destroy` 释放。`Spr7Frame` 以 168 个连续 double 传输。锁紧设置时刻为 58.000 s。首次发出的原生控制器锁紧命令在 58.001 s，原状态机接受并切到锁紧在 58.002 s。
- 原 CLI 按样本边界截断浮点时钟。DLL 用相同的时钟传给原控制器和事件判断，同时为 RK4 固定传入 0.001 s。报告保留此前用单一浮点累加时钟引起的 1 ms 抓捕偏差。

## 模型与适配边界

| 项目 | 处理 |
| --- | --- |
| 世界/本体换基 | `SpatialDynamicsAdapter` 实现 P = diag(1,1,−1)，极向量乘 P，轴向量乘 det(P)P，WXYZ 四元数按该反射重排。两器根节点接收 C++ 质心及本体姿态。 |
| 七关节 | 原生几何直接输出每个关节的位置和旋转，按 J1 至 J7 驱动组内 Prefab 的同名节点。柔性影响包含在原生下游框架。 |
| 臂段网格 | 原 CAD 六段保留原网格和组件，以入段长度识别源尺寸，逐段登记到原生下游关节或工具端，仅沿臂段长轴校准段长。关节、相机和原生状态保持各自坐标基准。 |
| 可视端口 | 用原模型舱体 X 端面作为示意安装面，将其平移对齐原生端口。物理端口和舱体端面各有节点。目标的原抓捕标记绑定为来访器原生抓捕点。 |
| 可视工具 | 复用 J7 下的「夹爪」网格，以夹爪包围盒沿 J7 至夹爪方向的边缘作接触参考。原生端执行器六自由度驱动夹爪与接触参考。此处包围盒是组内模型下的展示标定。 |
| 目标冗余装配 | 隐藏目标参考实例的机械臂关节分支及归类为 `SelfArm` 的碰撞/网格。资产及 GUID 保留。 |
| 后续换模 | 在 `SpatialSceneBuilder.CreateCraft` 指定组内 Prefab，并核对舱体端面、七关节顺序、J7 夹爪、目标抓捕标记、太阳翼和相机安装点。真实装配数据到位时更换端面和夹爪的几何标定。 |

## 原工程改动与回滚

- 原 7DOF 交付目录零改动。`Native7Dof/original.patch` 仅对构建暂存副本改三处。`main.cpp` 改为使用共用原生 `DemoSession.hpp`，保留默认 station、原 CLI 选项和 CSV 边界。`DockingDynamics.hpp` / `.cpp` 增加从已有 `GeometryAt` 读取七关节框架的只读方法。Kane 方程、RK4、控制器和接触/分离模型沿用原交付。
- 新增 `Native7Dof` 的构建、ABI、会话和两种 Windows 回归，Unity `SpatialDynamicsAdapter.cs` 及插件导入描述。修改 Unity 生成场景、装配绑定、总览标签、运行状态入口、SPR/1 DTO 和编辑器自检。修改 Qt UI、状态发送、默认配置与发布构建脚本。新增证据及本轮操作和验收说明。
- 回滚发布时，通过先前归档的整目录版本恢复可执行文件、Unity 子目录和默认配置。用户的 `data` 和自定义 `config/data_root.txt` 可继续沿用。源码层撤销本轮可追踪改动与新增项即可恢复原状态入口。

## 发布验收实测

构建 `20261127_011953_663`。Qt、Unity Win64、26 项算法自检、原生 CLI 的两组自检均通过。原生 DLL 的 4,001 帧 arm 输出与归档 CSV 模式逐帧一致。`q` 最大差为 6.83×10⁻¹²。原生锁紧工况的端口发布位置和速度差为 0，两质心最大速度差为 9.065 mm/s。独立 Windows Player 两次跑满 80 s，末态一致。Player 内实测夹爪参考与原生位姿最大位置偏差 8.377 μm，七关节最大 4.285 μm。夹爪及关节四元数最小同符号分量距离均小于 8.0×10⁻⁷。锁紧时实际端面最大间距 0.239 μm。几何误差为这套组内显示模型的 Transform 对比结果。

正式发布中保留可直接运行的 Qt exe、完整 Unity 子目录及本机运行库。已对 192 个文件逐一比较候选包与发布目录的 SHA-256。当前机旧发布版本为 `20261124_115912_584`，归档于 `归档/旧发布/dist-20261124_115912_584-before-7DOF/`。正式入口用旧用户 JSON 的隔离副本复验。验收档案含候选包数值回归、正式包截图和只读参考目录核查。

[验收矩阵及复验步骤](ACCEPTANCE_MATRIX.md) · [日志、报文和截图](../../归档/验收证据/20261127_7DOF集成/验收证据.md)
[本次交付逐文件清单](7DOF_文件清单.md)。
