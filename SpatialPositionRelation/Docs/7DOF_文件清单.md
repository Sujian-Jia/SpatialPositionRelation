# 7DOF 交付文件清单

2026-11-27。清单对应当前工作区交付，兼含此前未提交的界面与遮挡整改。发布产物和验收存档由工作区保留。

## 修改及新增的工程文件

```text
M	.gitignore
M	README.md
M	SpatialPositionRelation/Assets/Editor/SpatialAlgorithmSelfTest.cs
M	SpatialPositionRelation/Assets/Editor/SpatialCameraGestureSelfTest.cs
M	SpatialPositionRelation/Assets/Editor/SpatialPlayerSmokeTest.cs
M	SpatialPositionRelation/Assets/Editor/SpatialSceneBuilder.cs
M	SpatialPositionRelation/Assets/Scenes/SpatialDemo.unity
M	SpatialPositionRelation/Assets/Scripts/SpatialCraftAssembly.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialEmbeddedViews.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialModelBindings.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialOccluderIdentity.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialOverviewLabels.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialProtocol.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialRuntime.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialSensorFrustum.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialSensorGeometry.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialSensorView.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialSimulation.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialTcpBridge.cs
M	SpatialPositionRelation/Assets/Scripts/SpatialTcpTransport.cs
M	Spatial_Qt/SpatialPositionRelation/config/default_config.json
M	Spatial_Qt/SpatialPositionRelation/config/发布使用说明.md
M	Spatial_Qt/SpatialPositionRelation/main.cpp
M	Spatial_Qt/SpatialPositionRelation/mainwindow.cpp
M	Spatial_Qt/SpatialPositionRelation/mainwindow.h
M	Spatial_Qt/SpatialPositionRelation/mainwindowcapture.cpp
M	Spatial_Qt/SpatialPositionRelation/mainwindowplayer.cpp
M	Spatial_Qt/SpatialPositionRelation/mainwindowstate.cpp
M	Spatial_Qt/SpatialPositionRelation/mainwindowui.cpp
M	Spatial_Qt/SpatialPositionRelation/runtimedata.cpp
M	Spatial_Qt/SpatialPositionRelation/telemetrydisplay.cpp
M	Spatial_Qt/build.ps1
A	SpatialPositionRelation/Assets/Plugins.meta
A	SpatialPositionRelation/Assets/Plugins/x86_64.meta
A	SpatialPositionRelation/Assets/Plugins/x86_64/SpatialNative7Dof.dll.meta
A	SpatialPositionRelation/Assets/Scripts/SpatialDynamicsAdapter.cs
A	SpatialPositionRelation/Assets/Scripts/SpatialDynamicsAdapter.cs.meta
A	SpatialPositionRelation/Docs/7DOF_构建与接口.md
A	SpatialPositionRelation/Docs/7DOF_集成门禁.md
A	SpatialPositionRelation/Docs/ACCEPTANCE_MATRIX.md
A	SpatialPositionRelation/Native7Dof/CMakeLists.txt
A	SpatialPositionRelation/Native7Dof/DemoSession.hpp
A	SpatialPositionRelation/Native7Dof/Spr7Api.cpp
A	SpatialPositionRelation/Native7Dof/Spr7Api.h
A	SpatialPositionRelation/Native7Dof/build-native.ps1
A	SpatialPositionRelation/Native7Dof/original.patch
A	SpatialPositionRelation/Native7Dof/player-regression.py
A	SpatialPositionRelation/Native7Dof/regression.py
A	SpatialPositionRelation/Docs/7DOF_文件清单.md
```

`Native7Dof/original.patch` 仅改构建暂存的 `main.cpp`、`DockingDynamics.hpp`、`DockingDynamics.cpp`。前者复用会话编排，后两者导出原几何数据。原 7DOF 项目为零改动。Unity 插件 DLL 为生成物。组内 Prefab 与 FBX 仍使用项目已导入的资产。

[验收矩阵](ACCEPTANCE_MATRIX.md) · [可执行文件和构建方式](7DOF_构建与接口.md)
