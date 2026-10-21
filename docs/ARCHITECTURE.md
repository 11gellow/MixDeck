# 项目架构

> 基线日期：2026-10-20；实现事实更新：2026-10-21。来源：用户提供的《Windows Audio Manager 项目开发规划》（规划 V1.0）。基线职责与约束保持不变；四项目、音频控制核心、实时状态模块、事件同步的 WPF 主界面及托盘已落地，规则与配置仍为规划。

## 整体设计与模块

采用模块化单体与分层架构，以 WPF + XAML / MVVM 分离界面和业务状态，以 Windows Core Audio API 管理设备与会话。优先使用 NAudio；其封装未覆盖或不满足需求时，再以 C# COM Interop 补充。完整技术栈与产品边界见 [PROJECT.md](PROJECT.md)。

| 规划模块 | 职责 |
| --- | --- |
| `AudioManager.Desktop` | 图形界面、交互、界面状态、系统托盘和应用生命周期 |
| `AudioManager.Core` | 音量规则、配置模型、恢复策略、业务事件和抽象接口；不依赖 WPF 或具体 Windows 音频实现 |
| `AudioManager.Windows` | Windows 音频 API、设备操作、会话枚举、事件监听和系统相关功能 |
| `AudioManager.Tests` | 规则匹配、配置读写、恢复策略单元测试；具备环境时执行系统集成测试 |

已落地的引用为 Desktop → Core/Windows、Windows → Core、Tests → Core/Windows/Desktop（2026-10-21 为 ViewModel 测试新增 Desktop 引用并启用 WPF）；Core 不引用 Desktop、Windows、WPF 或 NAudio。Desktop 的 App 启动入口通过 `AddWindowsAudio` 及 ViewModel/窗口注册组合模块，由容器创建主窗口，退出时释放容器；ViewModel 只依赖 Core 的 `IAudioStateMonitor`（读取实时状态与通知）和 `IAudioController`（写入），不直接调用底层音频 API。当前没有业务规则，后续仍通过 Core 抽象访问能力。

## 当前目录与规划目录

当前根目录为 `MixDeck/`，已创建解决方案、SDK 配置、README、`.gitignore`、管理/验收文档，以及规划中的四个项目。工程名沿用 `AudioManager`，根目录未重命名；没有初始化 Git。`global.json` 指定 SDK `10.0.204`，采用 `latestPatch` 且禁用预览版；构建入口为 `dotnet build AudioManager.sln --configuration Debug --nologo`。

Core 为 `net10.0`，现有 `Models/` 中的音频快照、实例键、音量边界与异常类型，`Interfaces/IAudioController.cs`、`Interfaces/IAudioSessionMonitor.cs` 以及 `Events/AudioSessionCreated.cs`。其余项目为 `net10.0-windows`；Desktop 使用 WPF，Tests 使用 xUnit。Windows 中的 `WindowsAudioController` 负责验证、错误/日志与调度，`NAudioBackend` 负责设备、主音量、会话查询和控制；`AudioSessions/SessionSnapshotReader` 负责元数据降级，`Interop/AudioThread` 负责串行执行和 COM 初始化生命周期。`WindowsAudioSessionMonitor` 与 `AudioSessions/NAudioSessionSource` 负责 S2-01 的长期创建监听，由 `AddWindowsAudio` 注册为独立单例；Desktop 不使用该接口，而是使用阶段 2 的 `IAudioStateMonitor`。

当前使用 NAudio.Wasapi 3.2.0（传递依赖 NAudio.Core），只引入阶段 1 所需的 NAudio 子包；DI/Logging 的 Microsoft.Extensions 包为 10.0.13。没有在 Core 引入平台依赖。依赖版本和测试证据见 [CORE_VALIDATION.md](CORE_VALIDATION.md)。

S3-08 在 Desktop 启用 `UseWindowsForms`，仅使用 .NET Windows Desktop 自带的 `NotifyIcon`、`ContextMenuStrip` 和图标资源；未增加 NuGet 包，主界面继续使用 WPF/MVVM。移除自动生成的 Forms/Drawing 全局 using，避免与 WPF 类型冲突；Core 仍为可移植 `net10.0`，不引入托盘或 UI 依赖。

阶段 2 新增 Core 的 `Interfaces/IAudioStateMonitor`、`Models/AudioRuntimeState`、`Events/AudioStateChanged` 及 `Services/AudioStateStore`；Windows 的 `WindowsAudioStateMonitor` 负责生命周期/队列和恢复，`Notifications/NAudioDeviceSource` 负责设备事件，`Notifications/NAudioEndpointSource` 负责端点与会话事件。`Interop/SessionComInterop` / `SessionVolumeWriter` 仅补齐 NAudio 会话封装缺失的 EventContext；设备与主音量继续使用 NAudio。没有新增包或改变模块依赖，完整验收见 [REALTIME_STATE_VALIDATION.md](REALTIME_STATE_VALIDATION.md)。

下列保留原规划目录结构，并非全部目录已经存在；各模块的后续子目录按实际功能创建，`DECISIONS.md` 尚未创建。实际还包括根目录 `global.json` 及 `docs/` 中各阶段验收记录；Desktop 已建立 `Views/`（MainWindow、转换器）、`ViewModels/`（主窗口、设备、会话、操作记录及命令/合并写入辅助类）、`Resources/Styles.xaml`，另有规划外的 `Services/AppIconProvider`（按程序路径读取并缓存应用图标，非音频 API）、`Services/DesktopLifetime` 和 `Services/SystemTrayIcon`；`Controls/` 尚未需要。生成的 `bin/`、`obj/`、TestResults 不纳入版本管理。

```text
AudioManager/                  # 原规划名称，当前实际目录为 MixDeck/
├── AudioManager.sln
├── README.md
├── .gitignore
├── docs/
│   ├── PROJECT.md
│   ├── ARCHITECTURE.md
│   ├── DECISIONS.md
│   ├── TASKS.md
│   └── STATUS.md
├── src/
│   ├── AudioManager.Desktop/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   └── Resources/
│   ├── AudioManager.Core/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   ├── Services/
│   │   ├── Rules/
│   │   └── Events/
│   └── AudioManager.Windows/
│       ├── AudioSessions/
│       ├── Devices/
│       ├── Notifications/
│       └── Interop/
└── tests/
    └── AudioManager.Tests/
```

## 主要 Windows 接口

| 接口 | 规划用途 |
| --- | --- |
| `IMMDeviceEnumerator` | 枚举和管理音频设备 |
| `IAudioEndpointVolume` | 读取、设置设备主音量 |
| `IAudioSessionManager2` | 管理音频会话 |
| `IAudioSessionEnumerator` | 枚举音频会话 |
| `IAudioSessionControl2` | 读取会话身份信息 |
| `ISimpleAudioVolume` | 读取、修改会话音量和静音状态 |
| `IAudioSessionNotification` | 接收新会话通知 |
| `IAudioSessionEvents` | 接收会话状态、音量等变化通知 |
| `IMMNotificationClient` | 接收设备及默认设备变化通知 |

## 关键状态与数据流

### 应用身份与多会话

区分会话运行时实例标识、应用稳定标识、进程信息和输出设备；不能仅以 PID 作为持久化配置唯一键。初步以程序路径、会话标识、设备标识等信息建立匹配机制，处理同一应用的多个会话、应用退出、会话重建及设备变化。具体稳定键组成、多会话聚合或逐会话应用规则、跨设备匹配策略待确认。

阶段 1 的运行时控制使用 `AudioSessionKey(DeviceId, InstanceId)` 精确定位；不按 PID 合并会话。返回会话标识、PID、进程名称/路径、状态和音量；可选元数据失败通过 Issues/日志反馈，仍保留可控制会话。该运行时键不作为持久化身份；阶段 4 的匹配规划不变。

### 配置恢复

启动恢复按原规划执行：

1. 加载已保存配置与音量规则。
2. 初始化 Windows 音频设备管理模块。
3. 注册设备与会话监听。
4. 枚举当前会话。
5. 匹配已有配置与当前应用。
6. 依据启用的恢复策略设置目标音量。
7. 保留尚未匹配的规则。
8. 新会话出现后继续匹配与恢复。

设备切换、连接断开或控制接口失效后，重新枚举并重建相应监听；会话退出后释放相关资源。规则与手动操作的优先级、启停行为和冲突处理细节尚待确认。

### 状态同步与事件处理

变化来源包括本软件操作、Windows 音量混合器操作、其他应用或系统操作、配置恢复以及设备和会话变化。控制链为“Core 的 IAudioController → WindowsAudioController → MTA 队列 → NAudioBackend → 系统音频接口”，返回托管快照，写入后回读；不返回 COM 对象。Desktop 的 ViewModel 只用该接口写入；滑块中间值合并，回读只在无后续待写值时更新界面。读取链为“IAudioStateMonitor.StartAsync → ReadChangesAsync（单消费者，在 UI 线程上下文中逐条处理）→ 按 Revision 应用完整快照”，不轮询；正在写入的值不被快照覆盖，本软件写入的延迟回传（Origin=Self）在写入后 1 秒内若与最后写入值不同则忽略，Origin=External 的快照始终应用并在界面标记。默认设备变化时界面自动跟随，除非用户已手动选择其他设备。“刷新”运行中调用 `RefreshAsync` 重试失效监听，未运行时重新 `StartAsync`。隐藏到托盘不停止读取；显式退出时 ViewModel 先取消读取，容器随后释放监听。规则待阶段 4。

当前控制器的按需操作在专用 MTA 线程执行；每次重新获取并释放 NAudio 对象。尚未执行的请求可取消，已开始的系统调用不强行中断；关闭时排空已接受操作，释放 COM 初始化并结束线程。错误保留操作、分类、原始异常与 HRESULT，整体失败记录 Error、单项不可读记录 Warning。

S2-01 的监听器使用独立 MTA 队列，按设备保留 `MMDevice`/manager。订阅 `OnSessionCreated` 后访问 `Sessions.Count` 启用本机通知，首次枚举补齐已有会话；之后直接从通知携带的数据增量添加，不靠定时扫描或重枚举。NAudio 回调的借用接口只在回调有效期内复制字段，进程名称/路径在队列中补全；不把 COM 对象传给队列或 Core。枚举与通知重叠时按运行时键去重；过期及不可读项跳过并记录日志，注册身份检查隔离停止/重启后的迟到通知。

创建通知通过 `ReadCreatedSessionsAsync` 单消费者队列输出，上层代码不在本机回调或音频线程执行。取消读取不停止监听；停止前已排入公开队列的事件仍可读取。重复 Start 不重复注册，Stop 清除该设备集合并释放资源；监听器或 DI 容器释放时关闭所有注册和通知队列。已知集合只含创建时快照，暂不更新音量或清理退出会话；消费者应及时读取通知，并在不需要监听时 Stop/Dispose，避免长期积压。

完整实时能力使用独立的 `IAudioStateMonitor`（DI 单例，按需 Start）。它先注册设备事件再读取设备/默认角色，对全部 active Render 端点各持有一个监听源；首次会话枚举后增量处理创建、音量、静音、活动、退出和主音量事件。Core 的 `AudioStateStore` 只在所属队列修改，按设备/实例键去重，实际内容变化才递增 Revision；快照使用只读副本，历史快照不会被后续事件修改。

会话回调保留原 EventContext，写入使用进程内只读随机 GUID；主音量通过 NAudio NotificationGuid 携带同一标记，精确分类 Self/External，不按数值猜来源。监听器不写音量，不产生回写循环。补充的会话 COM 注册/解绑/读取位于 MTA；创建回调的借用接口先 AddRef，再将拥有引用的私有任务投递至同一 MTA 注册会话事件，投递被拒绝或关闭时释放该引用，Core/公开通知始终只接触托管数据。

设备连接、移除、状态及默认角色通知触发设备清单重新读取，仅新增/失效端点需要重建。会话过期或断开时解绑释放；失效端点先清会话和音量，再采用 0.25/1/3 秒的有限退避。初始化成功后须稳定 10 秒才重置预算，短时间反复失效或其他设备事件不能绕过预算；耗尽后保留失败状态，显式 Refresh 或明确设备事件可再次尝试。Stop 取消恢复/稳定计时器，运行周期和注册身份过滤迟到回调。普通运行没有定时扫描，退避与稳定检查都是故障触发的一次性计时任务。

`ReadChangesAsync` 提供单消费者队列，包含类型、来源、上下文、更新后快照及必要错误信息；取消读取不停止后台，停止前已入队项仍能读取，Dispose 关闭队列。消费者代码在本机回调/工作队列之外执行。非致命信息读取错误通过日志及 ReadFailure 输出；可选元数据失败不触发整个端点重建。消费者需及时消费并在不用时 Stop/Dispose，长时积压与资源预算仍待 S6-08。

PropertyChanged 可能随滑块写入高频产生，当前不触发全设备扫描；设备重命名等元信息可用 Refresh 更新。Refresh 重新读取设备清单并重试缺失监听，不重建或重新抓取健康端点全部会话音量。旧 `IAudioSessionMonitor` 保持创建通知兼容能力；Desktop 已在 S3-09 启动完整 `IAudioStateMonitor` 并在 UI 上下文消费，S3-08 已提供后台托盘；保存恢复仍待阶段 4。此前“Desktop 尚未启动监听”的描述已按代码及实机证据纠正，未改变原规划。

### 桌面窗口与托盘生命周期

`App` 是组合与生命周期入口，`ShutdownMode=OnExplicitShutdown`。`DesktopLifetime` 只管理托盘可用性、显示/隐藏和退出状态，不依赖音频实现；`SystemTrayIcon` 只持有通知图标、菜单和回调，菜单或双击事件通过 WPF Dispatcher 投递至窗口线程。`MainWindow` 仅上报退出请求和显示托盘可用性提示。

托盘可用时，窗口 Closing 先取消关闭再隐藏；最小化也调用同一隐藏分支。托盘双击或“显示主窗口”执行 Show、必要时恢复 Normal 并激活，不新建窗口或监听。菜单“退出”及页脚“退出程序”先设置退出状态再调用 WPF Shutdown，避免关闭被再次隐藏。系统 SessionEnding 先标记退出，避免阻止注销；托盘初始化失败时记录错误并允许关闭窗口退出，不留下无法恢复的隐藏进程。

OnExit 解除窗口/系统事件，先隐藏并释放 NotifyIcon、菜单和克隆图标，再释放 DI 容器及监听。隐藏期间容器保持存活，不重复注册监听。生命周期和菜单派发/释放由自动化测试覆盖，关闭隐藏、用户协助恢复及显式退出有本机证据；实际最小化、系统注销、Explorer 重启及长期驻留仍待实机检查，详见 [DESKTOP_VALIDATION.md](DESKTOP_VALIDATION.md)。

### 状态分离与持久化

| 状态 | 含义与边界 |
| --- | --- |
| 实时音量状态 | Windows 当前可枚举设备、会话返回的状态；随事件更新 |
| 已保存音量状态 | 用户主动保存的配置与快照；异常实时值不得自动覆盖 |
| 目标音量状态 | 当前启用规则期望达到的音量和静音状态 |

第一版使用 `System.Text.Json` 读写本地 JSON，包含软件设置、已知应用、设备信息、音量快照、恢复规则和配置版本号；支持方案管理、导入导出、默认配置及用户偏好。按需区分输出设备，处理损坏、缺失字段和版本变化。采用安全写入、原子写入、配置备份及异常处理，降低异常退出造成配置丢失的风险。文件路径、版本迁移规则及具体备份 / 写入方案待确认。

## 架构约束

1. UI 不直接操作底层音频 API。
2. Core 不依赖具体 Windows 音频封装库。
3. Windows 系统调用通过业务接口向上提供能力。
4. 音量规则与设备控制逻辑分离。
5. 实时音频状态与持久化配置分开管理。
6. 事件监听不阻塞 UI 线程。
7. 正确释放音频资源、COM 对象和事件订阅。
8. 通过依赖注入组合模块，避免全局共享可变状态。

## 技术风险与待验证事项

| 风险 | 原规划处理方向 |
| --- | --- |
| 显示值与实际听感不一致 | 结合日志、API 状态与可复现实验定位；API 音量不代表最终声压，不能仅比较数值判断所有异常 |
| 应用重启导致会话标识变化 | 使用应用级匹配，避免仅依赖 PID 或运行时标识 |
| 设备切换使控制接口失效 | 重新枚举设备并重建监听 |
| 规则与用户手动操作冲突 | 明确规则优先级和启停机制，具体策略待确认 |
| COM 线程或回调异常 | 请求及实时监听已按 MTA 生命周期管理，创建借用引用受控转移，事件投递与关闭有自动化验证；Windows 10/真实拔插及长期兼容验证仍待阶段 6 |
| 会话尚未创建 | 保留规则，等待会话创建事件 |
| 用户配置丢失 | 配置备份、原子写入及异常处理 |
| 后台资源占用过高 | 事件驱动，限制轮询和重复操作 |

异常诊断需记录音量修改、恢复操作和 API 返回结果；提供重新获取会话、刷新状态及手动重应用目标音量的能力。先复现并验证“重新设置相同值”“改变后再恢复”等行为，再确定有效的自动恢复策略；不得承诺修复应用内部音量、驱动或其他处理环节造成的所有播放异常。

## 架构维护约束

- 本文件依据用户规划建立稳定基线；未定细节保持“待确认”，不把方案写成已实现事实。开发任务与验收见 [TASKS.md](TASKS.md)，当前状态见 [STATUS.md](STATUS.md)。
- 普通开发遵循此架构；重大调整须先说明原因、影响及建议，获得用户同意后再实施，不以更新文档代替确认。
- 发现源码或历史设计与基线不一致时，记录双方事实、差异和待决事项，不擅自以当前实现覆盖原始规划。
