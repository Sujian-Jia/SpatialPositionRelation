# 空间位置关系计算模块

在轨服务场景的演示与验证程序。Qt 做界面，内嵌一个 Windows 版 Unity Player 显示三维画面，机械臂和两器的运动由一份原生 C++ 七自由度动力学模型算出来。

一段 80 秒的工况从目标接近开始，经过机械臂抓捕、搬运、端口接触、锁紧，到分离结束。界面上同时给出六路敏感器的视场、遮挡比例，以及从当前时刻算到 80 秒的未来遮挡时段。

## 代码结构

```
Spatial_Qt/                  Qt 主程序
SpatialPositionRelation/     Unity 工程
  Native7Dof/                原生动力学、ABI 与回归脚本
  Docs/                      说明与验收记录
  Assets/                    场景、脚本、模型
```

三者的关系：

```
Qt 主程序 ──启动、嵌入窗口──> Unity Player
    ▲                              │
    └──── SPR/1 JSONL，TCP 环回 ───┘
                                   │ P/Invoke
                                   ▼
                        SpatialNative7Dof.dll
```

Qt 启动时先占一个本机空闲端口，把端口号和一次性令牌通过命令行传给 Unity，再用 `-parentHWND` 把窗口嵌进右侧视口并随窗口尺寸同步。关掉 Qt，它拉起的 Unity 子进程一起退出。

## 工况与操作

1. 打开 `SpatialPositionRelation.exe`，等右上角出现「Unity 就绪 · TCP 已连接」。
2. 总览左上角可以勾选多路相机，画面在右侧纵向排列；双击子视图铺满，再双击还原。选「固定视角」保持预设构图，选「可拖动视角」后用左键环绕、滚轮缩放。
3. 「位置/机构」点「播放」跑完整工况。目标先主动接近，己方机械臂在 6.7 秒左右抓捕，52.3 秒端口接触，58.002 秒锁紧，64.0 秒分离。「暂停」「继续」「复位」和倍率（0.25–16）都在同一页。
4. 敏感器表格里，「未来预警 (s)」是预报出的第一个连续遮挡区间，「当前遮挡」「可见点/总点数」「面积入场/可见」「太阳入射」「直射受光」「遮挡来源」「建议」随遥测刷新。
5. 「显示射线」画出每一路的透明视场锥面：水平和垂直 FOV 相等时是圆锥，不等时是椭圆锥。
6. 「显示太阳矢量方向」控制日盘、箭头和标签，光照方向与地影不受它影响。
7. 「测试关节角」只在未播放或播放结束后可勾选。滑块从收到的七轴读数起步，0.1° 一格；播放和暂停时锁定，只做只读回显。
8. 勾「手动检查转轴」后可以选择己方、来访器或两器，拖帆板滑块；取消勾选恢复自动对日。
9. 右上角「运行状态」→「查看中文运行记录」看事件；需要原始报文时在同一个窗口打开「诊断文件」。
10. 「敏感器」页能保存参数、遥测和相机单帧，也能逐帧录制（PNG 加对应遥测）。

界面上的细节见 `Spatial_Qt/SpatialPositionRelation/config/发布使用说明.md`。

## 未来遮挡预报

预报不和播放串行。播放时从当前原生会话克隆一份快照（`Spr7_Clone`，连时钟、约束和接触历史一起复制），在后台按 1 ms 定步长推进、每 20 ms 取样，一直算到 80 秒。判据和实时遮挡同一套：同一批采样点、同一个阈值，离屏碰撞体按各分支的固定相对位置各建一棵碰撞树。

暂停时副本冻结在原生的样本边界上，读数停止；复位、改敏感器配置、改轨道参数、手动拖帆板，都会让旧结果作废并重算。

这套预报只覆盖两器和机构的动态遮挡。Qt 的太阳轨道时钟和动力学时钟是分开的。

## 构建

需要 Qt 6.12.0 MinGW 64-bit、MinGW-w64 GCC 13.1、Unity 2022.3.62f3、CMake 3.16 以上，回归脚本还要 Python 3。

一次构建全部：

```powershell
powershell -ExecutionPolicy Bypass -File Spatial_Qt/build.ps1
```

脚本依次跑原生 Release 自检与基线比对、Unity 的 26 项算法自检、Win64 Player 构建、Qt Release 编译和 `windeployqt` 部署，最后把 Player、配置和 `BUILD_VERSION.txt` 放进 `dist/`。想输出到别的目录就加 `-DistributionDirectory`：

```powershell
powershell -ExecutionPolicy Bypass -File Spatial_Qt/build.ps1 -DistributionDirectory "$PWD\dist-candidate"
```

`build.ps1` 顶部有四个绝对路径，换成自己机器上的：

```powershell
$unityExe  = 'D:\Software\Unity\2022.3.62f3\Editor\Unity.exe'
$qtBin     = 'D:\Software\QT\6.12.0\mingw_64\bin'
$mingwBin  = 'D:\Software\QT\Tools\mingw1310_64\bin'
$cmakeExe  = 'D:\Software\QT\Tools\CMake_64\bin\cmake.exe'
```

原生插件也可以单独编：

```powershell
powershell -ExecutionPolicy Bypass -File SpatialPositionRelation\Native7Dof\build-native.ps1 -Verify
```

它把原交付目录里的源码复制到 `Spatial_Qt/SpatialPositionRelation/build/native7dof/source/`，在副本里打 `original.patch`（原目录不动），用 MinGW 编出 `spr7_cli.exe` 和 DLL，`-Verify` 会跑两组 CLI 自检、`arm`/`station` 两个工况的 CSV，再和 `validation/composite` 基线比对，最后把 DLL 放进 `Assets/Plugins/x86_64/`。

Unity 和 Qt 分开编的话，Unity 用 Hub 打开 `SpatialPositionRelation` 子目录，或者：

```powershell
& "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath .\SpatialPositionRelation `
  -buildTarget Win64 -executeMethod SpatialSceneBuilder.BuildWindowsPlayer `
  -logFile .\unity-build.log
```

Qt 端：

```powershell
cmake -G "MinGW Makefiles" -S .\Spatial_Qt\SpatialPositionRelation `
      -B .\Spatial_Qt\SpatialPositionRelation\build\Release `
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="D:\Software\QT\6.12.0\mingw_64"
cmake --build .\Spatial_Qt\SpatialPositionRelation\build\Release --parallel 4
```

编译完记得用 `windeployqt` 补齐 Qt 运行库，并把 Player 放到 `Unity/` 子目录，`dist/` 就是这么拼出来的。

## 配置

`Spatial_Qt/SpatialPositionRelation/config/default_config.json`，构建时复制到程序目录的 `config/`。

| 字段 | 含义 |
| --- | --- |
| `dynamicsSpeed` | 动力学播放倍率，0.25–16 |
| `showSunVector` | 是否显示太阳矢量方向，默认关，保存参数时会记住 |
| `viewSensorId` | 默认聚焦的敏感器 ID，空串表示不指定 |
| `overviewMode` | 总览模式，`fixed` 是固定视角 |
| `displaySensorIds` | 默认同时显示的相机 |
| `orbit` | 轨道根数、时间倍率、闰秒和历元 |
| `sensors[]` | 六路敏感器的类型、安装位、局部位姿、视场角和告警阈值 |

机械臂三路相机的 `targetId` 是 `GRAPPLE-FIXTURE`，`sampleRadiusM` 是示意采样的半跨距，默认 0.015 m；靶标解析、遮挡和预报引用的是同一个捕获座基准。

带旧用户配置启动时，按机位 ID 合并读取；只有位置、旋转、靶标和采样尺寸都还是旧默认值，才会迁移到新的捕获座配置。

## 协议与原生接口

Qt 和 Unity 之间是 UTF-8 JSONL，`state` 里用 `demoDocking`、`demoRunId`、`dynamicsSpeed` 控制播放、复位和倍速。第四轮加了一条 `presentation`，专管总览的太阳和七轴预览：

```json
{"protocol":"SPR/1","type":"presentation","demoRunId":1,"showSunVector":true,"previewJointDegrees":[35,-15,20,50,45,-80,100]}
```

预报相关的字段有 `trajectoryForecastAvailable`、`trajectoryForecastComplete`、`trajectoryForecastThroughSec`、`futureOcclusionStartSec`/`EndSec`，还有累计的 `trajectoryForecastBlockedSec`、`OutOfFovSec`、`PeakBlockedRatio`。装配侧另有 `dynamics.appliedGripper`、`appliedGrapple` 和 `basePivot` 等基准点。完整字段表在 `Docs/TCP_PROTOCOL.md`。

原生 DLL 的接口在 `Native7Dof/Spr7Api.h`，ABI v1，七个导出：`Spr7_Abi`、`Spr7_Create`、`Spr7_Clone`、`Spr7_ReadCalibration`、`Spr7_Destroy`、`Spr7_Advance`、`Spr7_Read`。`Spr7Frame` 是 168 个连续 double，四元数按 WXYZ，单位 SI。`Spr7_Advance` 每步 1 ms，以 20 步为采样边界；`Spr7_Read` 只能在采样边界调。

## 数据

默认写在程序目录的 `data/` 下：`config/user_config.json` 是参数，`logs/` 是诊断日志，`output/sensors/` 放遥测和相机图片。

数据根目录的优先级是 `--data-root "<路径>"` → `config/data_root.txt` → `data/`。启动时取消选择数据目录会进只读模式。

## 仓库约定

模型按 LFS 存，克隆后如果模型不对，先跑 `git lfs install && git lfs pull`。构建产物（`dist/`、`Library/`、`Build/`、`Logs/`）、原生的 `SpatialNative7Dof.dll`、外部交付目录 `7DOF_masscenter_real_constraint/` 和本地的 `归档/` 都不入库。

`.gitignore` 用的是根目录白名单：`/*` 忽略一切，只放行 `SpatialPositionRelation/`、`Spatial_Qt/`、`.gitignore`、`.gitattributes`、`README.md`。以后根目录加新文件夹要自己补一条 `!/目录名/`。

行尾设了 `core.autocrlf=false`，保证 Windows 上检出的字节和提交时一致。

## 已知限制

- 未来遮挡只算两器和机构的动态遮挡，太阳轨道用独立时钟。
- 外部交会对接联调按确认的范围延期了。飞行实装尺寸和光照绝对值需要甲方的图纸、接口坐标和标定资料，目前验收的是原生工况、算法和参考装配。
- 固定帧严格比对里还留着 3 组 PNG 字节差异（不超过 3 个像素、单通道色差 1/255）和 1 次遮挡部件名单差异，已记为稳定性问题，记录在验收矩阵里。
- 换实装模型时，要重新核对舱体端面、七关节顺序、J7 夹爪、抓捕标记、帆板和相机安装点。

## 文档

`SpatialPositionRelation/Docs/` 下面这些是主要参考：

- `PROGRESS.md` 逐轮的开发与验收进度
- `DECISIONS.md` 装配口径、预报克隆、锥面和文案的处理依据
- `BUILD_AND_RUN.md` 构建、运行和真实 Player 回归命令
- `TCP_PROTOCOL.md` SPR/1 的增量字段与 `presentation`
- `ACCEPTANCE_MATRIX.md` 各轮验收矩阵
- `7DOF_构建与接口.md` 方案 A 的构建、坐标与模型标定边界
- `7DOF_算法一致性核对.md` 七项算法逐帧对照、穿模核查
- `7DOF_装配标定.md` 七段连杆、基座与捕获座的标定
- `敏感器位姿核对.md` 六路位姿、靶标与两种装配口径

验收证据（截图、日志、原始报文）在作者本地的 `归档/验收证据/` 里，没随仓库发布，文档里的那类链接打不开是正常的。

## 许可

未附开源许可证，默认保留所有权利。要引用、复制或分发，请先联系作者。
