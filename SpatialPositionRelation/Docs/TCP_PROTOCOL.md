# SPR/1 装配与预测增量

TCP 仍使用 UTF-8 JSONL。`state` 的 `demoDocking`、`demoRunId` 和 `dynamicsSpeed` 分别控制播放、复位与倍速。原生会话唯一提供动力学状态。

| 遥测字段 | 含义 |
| --- | --- |
| `dynamics.appliedGripper` / `dynamics.appliedGrapple` | 组内夹爪 `basePlate` 和目标 `轴顶点` 在 Unity 世界系的位置，单位 m。 |
| `dynamics.basePivot`, `baseFoot`, `baseSeat`, `grappleFoot`, `grappleSeat` | 实际装配基准点。`linkStarts`、`linkEnds` 按七段索引给出依据 FBX 顶点拟合并父接到实际网格上的近端、远端接合基准。 |
| `sensors[].trajectoryForecastAvailable`, `trajectoryForecastComplete` | 当前之后已有结果、预测已算到 80 s。 |
| `trajectoryForecastThroughSec`, `trajectoryForecastWindowSec`, `trajectoryForecastSampleSec` | 预测已算到的工况时刻、当前时刻至 80 s 的预测窗、0.02 s 采样间隔。 |
| `futureOcclusion`, `futureOcclusionStartSec`, `futureOcclusionEndSec` | 当前及未来已算区间的首个连续遮挡段。部分和全部遮挡均计入。预测未完成时，开放区间的末端随计算进度更新。 |
| `trajectoryForecastBlockedSec`, `trajectoryForecastOutOfFovSec`, `trajectoryForecastPeakBlockedRatio` | 已算部分中，从当前时刻计的累计遮挡时间、累计出场时间及峰值遮挡点占入场点比例。 |

Qt 的实时遥测约每 0.2 s 刷新。预测 C++ 步长 0.001 s，隔离场景遮挡按每 0.02 s 原生样本计算。连接和报文实例如附 [验收记录](D:/Project/261121SpatialPositionRelation/归档/验收证据/20261127_装配与预测/最终版-原始SPR1报文选段.jsonl)。

## 第四轮显示命令

Qt 可在 SPR/1 握手后发送 `presentation`。Unity 按 `demoRunId` 门控并通过现有遥测持续输出动力学及传感器数据。该显示命令适用于总览日盘、箭头和预览网格。同一轮中播放和暂停锁定预览。未开始或原生已完成且未继续推进时，可带七项角度。空数组退出预览。`state`、`telemetry` 与 C++ ABI 的动力学语义沿用原定义。

```json
{"protocol":"SPR/1","type":"presentation","demoRunId":1,"showSunVector":true,"previewJointDegrees":[35,-15,20,50,45,-80,100]}
```

这是实际回归发出的命令。用户设定不勾选测试时，Qt 发送时省略 `previewJointDegrees`。省略与空数组均清空预览。光照计算继续使用原场景太阳向量，`showRays` 留在 `state` 消息。真实出入站包见 [Player TCP](../../归档/验收证据/第四轮_测试关节与任务书/windows-player-candidate/tcp.jsonl)。

方案 A 的机械臂三路通过既有 `targetId` 传 `GRAPPLE-FIXTURE`，`sampleRadiusM` 表示示意采样的半跨距，默认 0.015 m。靶标解析、遮挡和预测引用同一捕获座基准。见 [实机字段值](../../归档/验收证据/第四轮_方案A位姿/qt-final/qt-state-migrated.json)。
