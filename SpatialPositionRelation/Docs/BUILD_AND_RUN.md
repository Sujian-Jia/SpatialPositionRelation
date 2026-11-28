# 构建与运行

本轮验证版为 `20261127_220036_022`。双击 [最终 Windows 程序](../../归档/验收证据/第四轮_方案A位姿/release/SpatialPositionRelation.exe)，分发时保留完整 `release` 文件夹。带此前用户配置启动时，默认腕部和肘部机位会在加载时迁移。[实机终态](../../归档/验收证据/第四轮_方案A位姿/最终版-80秒完成.jpg) · [构建记录](../../归档/验收证据/第四轮_方案A位姿/build-final.log)


原 `dist` 入口版本为 `20261127_163137_130`。启动 [SpatialPositionRelation.exe](D:/Project/261121SpatialPositionRelation/dist/SpatialPositionRelation.exe)。分发时保留整个 `dist`，其中包含 Qt 运行库、Unity Player 和原生 DLL。回滚包位于 [旧发布](D:/Project/261121SpatialPositionRelation/归档/旧发布/dist-20261127_144403_227-before-cone)。用户数据仍在原数据目录。

在项目根目录运行 `powershell -ExecutionPolicy Bypass -File Spatial_Qt/build.ps1`。输出到独立目录时加 `-DistributionDirectory D:\Project\261121SpatialPositionRelation\dist-candidate`。构建依赖 Qt 6.12.0 MinGW x64、Unity 2022.3.62f3、CMake、Git、Python 3，以及工作区内的原生计算交付和已导入的组内模型。脚本顺序为原生 Release 及基线、Unity 26 项自检、Unity Win64 构建、Qt Release 和部署。

在「位置/机构」点「播放」，观察机械臂与目标捕获座。预测进度和首个遮挡区间在「未来预警」列。点「暂停」可冻结工况和预测，点「复位」可清空旧预测。勾选「显示射线」可看透明圆锥面或椭圆锥面。点击 Unity 总览内的「可拖动视角」，再用鼠标左键环绕和滚轮缩放。

点主窗口右上角「运行状态」→「查看中文运行记录」查阅事件。需要原始 TCP 报文时可从同一窗口打开「诊断文件」。

## 第四轮候选

启动 [第四轮候选程序](../../归档/验收证据/第四轮_测试关节与任务书/candidate/SpatialPositionRelation.exe)。分发保留整个 `candidate` 文件夹。运行位置/机构页时，待播放或结束状态可勾选「测试关节角」。播放和暂停时七轴由遥测回显。「显示太阳矢量方向」切换既有日盘、箭头和标签。

在 `D:\Project\261121SpatialPositionRelation` 运行以下命令可另建候选目录，并运行真实 Windows Player 回归。`dist/Unity/SpatialUnity.exe` 若为第三轮正式版，用同样的脚本加末尾参数 `--no-presentation` 做旧版对照。脚本严格核对动力学和几何量，记录冻结帧的六路 PNG 摘要和遮挡部件差异。`--strict-pictures` 将任何 PNG 字节差异计为失败，`--strict-blockers` 将部件名单差异计为失败。候选版和无预览对照均实测到少量像素变化及部件名单差异，详见验收记录。

```powershell
powershell -ExecutionPolicy Bypass -File Spatial_Qt/build.ps1 -DistributionDirectory "$PWD/dist-candidate"
py -3 -X utf8 SpatialPositionRelation/Native7Dof/player-regression.py dist-candidate/Unity/SpatialUnity.exe dist-candidate/config/default_config.json '归档/验收证据/20261127_算法核对与锥面/original-arm-replay.csv' Spatial_Qt/SpatialPositionRelation/build/native7dof/player-regression
```

分发和复验见 [验收矩阵](ACCEPTANCE_MATRIX.md) · [界面回归](第四轮界面交互与回归.md) · [模型标定与替换](7DOF_装配标定.md) · [TCP 字段](TCP_PROTOCOL.md)。
