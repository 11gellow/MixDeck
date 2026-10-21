# 实时事件与状态模块验收（阶段 2）

> 日期：2026-10-21。范围：S2-01～S2-07；本轮在已完成的创建通知基础上完成 S2-02～S2-07。依据用户“完成当前模块之前不要停止”的要求持续开发，未推进后续 UI/配置功能。

## 规划与边界

开发前及实现过程中重新阅读了 AGENTS.md、STATUS/TASKS、PROJECT、ARCHITECTURE，并检查实际代码和 Git 状态。现有目录不是 Git 仓库，未擅自初始化。保留 .NET 10、WPF/MVVM、Core/Windows/Desktop/Tests 分层、NAudio、xUnit、Logging；没有新增依赖包或改变业务目标。

Core 的实时模型只包含当前可观察状态，不接触保存配置或规则目标。Windows 负责事件、COM、设备与会话资源；Desktop 尚未接实时接口，S3-09、托盘、人工界面核对和阶段 4～6 继续未完成。

NAudio 3.2.0 会话 setter 使用空 context，事件 handler 也不传回 context，无法准确标记自身写入。因此按既有“封装不足时补充 COM Interop”原则新增窄投影，只补会话身份/volume/events；设备通知和端点主音量继续使用 NAudio，未访问其私有成员。

## 实现入口

| 位置 | 职责 |
| --- | --- |
| Core `IAudioStateMonitor` | Start/Stop、状态快照、清单/失效监听 Refresh、单消费者通知 |
| Core `AudioRuntimeState` / `AudioDeviceRuntimeState` | 设备、默认角色、主音量、进程/会话、状态、音量、静音、失败信息及版本 |
| Core `AudioStateStore` / `AudioStateChanged` | 精确运行时键增量更新、不可变快照、来源与错误通知；不执行音量写入 |
| Windows `WindowsAudioStateMonitor` | 所属 MTA 调度、设备/端点生命周期、迟到回调隔离、有界恢复和公开通知 |
| Windows `NAudioDeviceSource` / `NAudioEndpointSource` | 系统设备及会话/主音量事件，首次枚举与增量订阅 |
| Windows `SessionComInterop` / `SessionVolumeWriter` | 独立持有会话 COM 引用与回调上下文，释放失效接口，携带进程标记写入 |

## 实际验证

环境：Windows 11 家庭版中文版 `10.0.26100` x64，SDK `10.0.204`、运行时 `10.0.8`，NAudio.Wasapi `3.2.0`。最终真实测试默认输出为“耳机 (3- Realtek(R) Audio)”。

```powershell
dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror

$previousIntegrationSetting = $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS
try {
    $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = '1'
    dotnet test tests/AudioManager.Tests/AudioManager.Tests.csproj --configuration Debug --no-build --nologo --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=realtime-state-final.trx'
} finally {
    $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = $previousIntegrationSetting
}
```

结果：四项目 Debug 构建 **0 警告、0 错误**；**101/101 测试通过，0 失败、0 跳过**，包括 92 项单元测试与 9 项真实集成。原始 TRX 位于 `tests/AudioManager.Tests/TestResults/realtime-state-final.trx`（生成产物，由 .gitignore 排除）。本轮新增 35 项测试：10 Store、22 Monitor、3 真实集成；既有 66 项一起重跑通过。

最终文档/边界审计：Python 严格 UTF-8 解码、围栏和内部链接检查覆盖 9 份 Markdown、40 个内部链接，全部有效；62 个任务编号唯一，25 项完成、37 项未完成，阶段 2 七项已完成而后续未验收项保持未勾选。TRX XML 逐项结果/计数、项目引用与目标框架、Core 无 NAudio/WPF 依赖均检查通过；CIM 检查无残留无声辅助进程。

| 任务与要求 | 直接证据 | 结果/范围 |
| --- | --- | --- |
| S2-01 创建通知及去重 | 既有创建通知集成 + `SessionCreationStateAndExitPreserveOtherInstancesAndDevices` | 保留原创建接口，新实时链按设备/实例独立维护，无 PID 聚合 |
| S2-02 会话状态、音量、静音和来源 | `SessionEventsPreserveExactWriteContextAndExpiredSessionsAreReleased`、`RealtimeStateTracksNativeEventsAndExactOriginsWithoutPolling`、`VolumeChangesTrackExactContextAndDuplicatesDoNotChangeRevision` | 真实控制器写入 0.37 携自身 GUID，独立调用方写 0.23 携外部 GUID，静音与主音量同样准确区分；相同状态不重复更新或回写 |
| S2-03 设备及默认角色 | `RegisterReadAndReleaseDeviceNotificationsWithoutChangingSystemDevices` + 默认角色/null、Added/Removed、StateChanged 测试 | 真实客户端 10 次注册/独立读取 22 端点及默认值/释放通过；模拟通知更新默认/设备并重建，健康端点不重复订阅；未真实切默认或物理拔插 |
| S2-04 实时模型与分离 | `AudioStateStoreTests` 10/10 | 包含设备/应用/进程/多会话状态，历史快照不可变；不存在将实时值写入保存/目标状态的路径 |
| S2-05 通知与来源 | 真实独立服务集成、取消/更换消费者、非致命错误反馈测试 | 无 UI 即维护与通知；包含类型/来源/context/快照和必要 Issue；回调读错可见且不触发整端点重建 |
| S2-06 退出/失效/重建 | 真实子进程退出移除；瞬态失败恢复、断开/连接、预算耗尽、显式重试及旧注册测试 | 释放过期会话，清除失效端点状态，0.25/1/3 秒有界退避；短时反复失效及其他设备事件不能绕过预算 |
| S2-07 线程/并发/关闭 | 初始化、同 MTA、消费侧 Stop/Dispose、并发关闭、源释放异常、COM 初始化失败测试 | 已接受任务排空，运行周期/注册身份过滤迟到通知，资源释放失败记录并继续清理，单消费者与容器生命周期正确 |

新增的上层真实集成不通过会话枚举轮询寻找变化：创建子进程后从事件流拿到会话键，随后依次观察原生音量/静音、暂停→恢复带来的 Inactive→Active、主音量及退出通知中的新状态，Revision 从 4 增至 17。所有应用写入仅针对无声测试实例；主音量短暂修改后恢复 `0.16024216` / 未静音并回读。测试辅助进程和流在 finally 清理。

## 恢复与消费语义

- 正常运行由系统回调更新；没有持续定时扫描。设备变更后重新读清单，健康端点保持订阅。
- 失效先清除本机接口与旧状态；最多进行 0.25/1/3 秒三次延迟恢复。成功后连续稳定 10 秒才重置预算，避免“初始化成功后立刻失效”循环；耗尽保留失败反馈，明确设备事件或显式 Refresh 可重试。
- 所有恢复与稳定计时器都是故障触发的一次性任务，停止时取消，旧任务通过运行周期及恢复对象身份检查被忽略。
- `ReadChangesAsync` 单消费者及时读取，取消读取不停止服务；Stop 前已入队的事件仍能读取，Dispose 完成队列。版本代表实际状态变化，非致命错误可在版本不变时单独通知。
- `RefreshAsync` 只刷新设备清单和重试缺失监听，不重建健康端点全部会话。为避免音量 property 通知触发高频全量枚举，PropertyChanged 不触发扫描，设备重命名等元信息需主动 Refresh。

## 未执行和不适用项

- **未执行**：真实切换默认设备、物理拔插、Windows 10、系统重启、长时间运行/通知积压、性能预算、安装与打包。设备处理代码已通过模拟系统事件验证，但不据此认定 S6-05/S6-06 的实机兼容测试完成。
- **未执行**：WPF 自动同步接入与人工拖动/静音/DPI验收，仍属 S3-09 和 S3-02～S3-06；本次未操作 UI，UI 布局检查对新增后台功能不适用。
- **不适用**：本次保存/恢复规则、优先级、配置损坏、导入导出验证；相应实现仍待阶段 4，不能写“通过”。
- API 标量与实际听感不同；本次不宣称修复驱动或应用内部播放异常。

## 接口依据

- [Microsoft：OnSimpleVolumeChanged](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionevents-onsimplevolumechanged) 规定回传写入使用的 EventContext。
- [Microsoft：RegisterSessionNotification](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-registersessionnotification) 规定 MTA 初始化与 GetCount 启用通知；[创建回调引用](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionnotification-onsessioncreated) 说明跨回调持有时需要引用所有权。
- 已核对当前 NuGet 提交 `e3960d4cbd7870c53d20e65c4ad419d050b73063` 中的 [SimpleAudioVolume](https://github.com/naudio/NAudio/blob/e3960d4cbd7870c53d20e65c4ad419d050b73063/src/NAudio.Wasapi/CoreAudioApi/SimpleAudioVolume.cs)、[IAudioSessionEventsHandler](https://github.com/naudio/NAudio/blob/e3960d4cbd7870c53d20e65c4ad419d050b73063/src/NAudio.Wasapi/CoreAudioApi/Interfaces/IAudioSessionEventsHandler.cs) 和 [AudioEndpointVolume](https://github.com/naudio/NAudio/blob/e3960d4cbd7870c53d20e65c4ad419d050b73063/src/NAudio.Wasapi/CoreAudioApi/AudioEndpointVolume.cs) 的上下文能力差异。
