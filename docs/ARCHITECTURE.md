# 项目架构

> 基线日期：2026-10-20；实现事实更新：2026-10-22。来源：用户提供的《Windows Audio Manager 项目开发规划》（规划 V1.0）。基线职责与约束保持不变；四项目、音频核心、实时状态、WPF/托盘、配置与恢复规则均已实现，真实重启登录验收待用户反馈。

## 整体设计与模块

采用模块化单体与分层架构，以 WPF + XAML / MVVM 分离界面和业务状态，以 Windows Core Audio API 管理设备与会话。优先使用 NAudio；其封装未覆盖或不满足需求时，再以 C# COM Interop 补充。完整技术栈与产品边界见 [PROJECT.md](PROJECT.md)。

| 规划模块 | 职责 |
| --- | --- |
| `AudioManager.Desktop` | 图形界面、交互、界面状态、系统托盘和应用生命周期 |
| `AudioManager.Core` | 音量规则、配置模型、恢复策略、业务事件和抽象接口；不依赖 WPF 或具体 Windows 音频实现 |
| `AudioManager.Windows` | Windows 音频 API、设备操作、会话枚举、事件监听和系统相关功能 |
| `AudioManager.Tests` | 规则匹配、配置读写、恢复策略单元测试；具备环境时执行系统集成测试 |

已落地的引用为 Desktop → Core/Windows、Windows → Core、Tests → Core/Windows/Desktop；Core 不引用 Desktop、Windows、WPF 或 NAudio。App 通过 DI 组合音频、配置、窗口和生命周期；主 ViewModel 使用 `IAudioStateMonitor` 读取、`IAudioController` 写入，配置规则通过 Core 服务执行，不直接调用底层音频 API。阶段 4 未新增 NuGet 包或项目引用。

## 当前目录与规划目录

当前根目录为 `MixDeck/`，已创建解决方案、SDK 配置、README、`.gitignore`、管理/验收文档，以及规划中的四个项目。工程名沿用 `AudioManager`，根目录未重命名；当前已有 Git 仓库及提交，main 跟踪 origin/main，本轮未初始化或提交。`global.json` 指定 SDK `10.0.204`，采用 `latestPatch` 且禁用预览版；构建入口为 `dotnet build AudioManager.sln --configuration Debug --nologo`。

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

区分运行时实例标识、稳定应用标识、进程和输出设备；不能以 PID 持久化匹配。2026-10-21 用户确认按完整程序路径匹配、同程序多会话统一目标：`AudioIdentityMatcher` 用路径大小写不敏感比较，系统声音用专门身份，再按原端点 ID 限定设备。不持久化 PID/运行时会话键，也不猜测跨路径、跨设备或同名程序匹配。相同设备同程序的多会话值冲突时拒绝保存并要求统一；路径不可读的会话跳过并报告。

阶段 1 的运行时控制使用 `AudioSessionKey(DeviceId, InstanceId)` 精确定位；不按 PID 合并会话。返回会话标识、PID、进程名称/路径、状态和音量；可选元数据失败通过 Issues/日志反馈，仍保留可控制会话。该运行时键不作为持久化身份；阶段 4 的匹配规划不变。

### 配置恢复

启动恢复按原规划执行：

1. 加载已保存配置与音量规则。
2. 初始化 Windows 音频设备管理模块。
3. 注册设备与会话监听。
4. 枚举当前会话。
5. 匹配已有配置与当前应用。
6. 依据每个设备记住的当前方案设置目标音量，无全局自动恢复开关。
7. 保留尚未匹配的规则。
8. 新会话出现后继续匹配与恢复。

设备切换、连接断开或控制接口失效后，重新枚举并重建监听；会话退出释放资源。2026-10-22 用户明确替换旧持续纠偏策略：`DeviceProfiles` 按原端点 ID 保存当前方案，Windows 多媒体默认输出变化时更新当前方案并恢复该设备目标；其他设备的目标保持独立。首次遇到无方案设备时以可读实际值建立默认方案，不随意选取冲突的多会话值。新会话/监听恢复/显式刷新应用该设备目标；普通音量变化事件不持续改回旧值。

用户前端操作和系统入口产生的 Self/External 音量/静音事件，经控制器新鲜回读确认后更新对应设备当前方案规则并原子保存；同路径其他会话统一最新目标。仅更新事件涉及目标，不用整份实时快照覆盖方案。生命周期恢复、失败回读和已确认实际值的自身回调不作为新用户调节；旧 Revision 忽略。事件来源不能识别所有第三方意图或听感异常，不声称可自动区分应用自身重置与系统入口调节。手动原始快照保持不变；显式恢复快照同时将该设备对应规则设为快照目标，避免下次恢复反向覆盖。

### 状态同步与事件处理

变化来源包括本软件操作、Windows 音量混合器操作、其他应用或系统操作、配置恢复以及设备和会话变化。Desktop 控制链为“IAudioController → AudioWriteGuard → WindowsAudioController → MTA 队列 → NAudioBackend → 系统音频接口”，写入回读，不返回 COM 对象。滑块中间值合并，回读只在无后续待写值时更新界面。读取链为“IAudioStateMonitor.StartAsync → ReadChangesAsync（UI 上下文唯一消费者）→ 按 Revision 应用快照”，并将事件转交 Core 配置服务，不轮询。所有可控制项目保留 1 秒 Self 回传过滤，但不因方案管理禁用；配置服务通过 RestoreReadback 通知发布恢复后的实际值，更新 UI 并重置旧回调过滤基线。系统限制仍由 AudioWriteGuard 禁用并说明原因。External 在界面标记；默认设备变化始终跟随系统；用户下拉选择通过独立的 IDefaultAudioOutputController 切换 Windows 普通播放输出，再从监听器刷新、确认真实默认设备和应用对应方案。刷新重新枚举/重试监听并清除暂时的写入限制；隐藏托盘不停止读取，显式退出取消读取后释放容器。

当前控制器的按需操作在专用 MTA 线程执行；每次重新获取并释放 NAudio 对象。尚未执行的请求可取消，已开始的系统调用不强行中断；关闭时排空已接受操作，释放 COM 初始化并结束线程。错误保留操作、分类、原始异常与 HRESULT，整体失败记录 Error、单项不可读记录 Warning。

S2-01 的监听器使用独立 MTA 队列，按设备保留 `MMDevice`/manager。订阅 `OnSessionCreated` 后访问 `Sessions.Count` 启用本机通知，首次枚举补齐已有会话；之后直接从通知携带的数据增量添加，不靠定时扫描或重枚举。NAudio 回调的借用接口只在回调有效期内复制字段，进程名称/路径在队列中补全；不把 COM 对象传给队列或 Core。枚举与通知重叠时按运行时键去重；过期及不可读项跳过并记录日志，注册身份检查隔离停止/重启后的迟到通知。

创建通知通过 `ReadCreatedSessionsAsync` 单消费者队列输出，上层代码不在本机回调或音频线程执行。取消读取不停止监听；停止前已排入公开队列的事件仍可读取。重复 Start 不重复注册，Stop 清除该设备集合并释放资源；监听器或 DI 容器释放时关闭所有注册和通知队列。已知集合只含创建时快照，暂不更新音量或清理退出会话；消费者应及时读取通知，并在不需要监听时 Stop/Dispose，避免长期积压。

完整实时能力使用独立的 `IAudioStateMonitor`（DI 单例，按需 Start）。它先注册设备事件再读取设备/默认角色，对全部 active Render 端点各持有一个监听源；首次会话枚举后增量处理创建、音量、静音、活动、退出和主音量事件。Core 的 `AudioStateStore` 只在所属队列修改，按设备/实例键去重，实际内容变化才递增 Revision；快照使用只读副本，历史快照不会被后续事件修改。

会话回调保留原 EventContext，写入使用进程内只读随机 GUID；主音量通过 NAudio NotificationGuid 携带同一标记，精确分类 Self/External，不按数值猜来源。监听器不写音量，不产生回写循环。补充的会话 COM 注册/解绑/读取位于 MTA；创建回调的借用接口先 AddRef，再将拥有引用的私有任务投递至同一 MTA 注册会话事件，投递被拒绝或关闭时释放该引用，Core/公开通知始终只接触托管数据。

设备连接、移除、状态及默认角色通知触发设备清单重新读取，仅新增/失效端点需要重建。会话过期或断开时解绑释放；失效端点先清会话和音量，再采用 0.25/1/3 秒的有限退避。初始化成功后须稳定 10 秒才重置预算，短时间反复失效或其他设备事件不能绕过预算；耗尽后保留失败状态，显式 Refresh 或明确设备事件可再次尝试。Stop 取消恢复/稳定计时器，运行周期和注册身份过滤迟到回调。普通运行没有定时扫描，退避与稳定检查都是故障触发的一次性计时任务。

`ReadChangesAsync` 提供单消费者队列，包含类型、来源、上下文、更新后快照及必要错误信息；取消读取不停止后台，停止前已入队项仍能读取，Dispose 关闭队列。消费者代码在本机回调/工作队列之外执行。非致命信息读取错误通过日志及 ReadFailure 输出；可选元数据失败不触发整个端点重建。消费者需及时消费并在不用时 Stop/Dispose，长时积压与资源预算仍待 S6-08。

PropertyChanged 可能随滑块写入高频产生，当前不触发全设备扫描；设备重命名等元信息可用 Refresh 更新。Refresh 重新读取设备清单并重试缺失监听，不重建或重新抓取健康端点全部会话音量。旧 `IAudioSessionMonitor` 保持创建通知兼容能力；Desktop 已启动完整监听并提供托盘，阶段 4 在同一事件消费者后接入恢复。历史“Desktop 尚未启动监听”“保存恢复尚未实现”等描述已依据代码和验证修正，未改变原规划。

### 桌面窗口与托盘生命周期

`App` 是组合与生命周期入口，`ShutdownMode=OnExplicitShutdown`。`DesktopLifetime` 只管理托盘可用性、显示/隐藏和退出状态，不依赖音频实现；`SystemTrayIcon` 只持有通知图标、菜单和回调，菜单或双击事件通过 WPF Dispatcher 投递至窗口线程。`MainWindow` 仅上报退出请求和显示托盘可用性提示。

托盘可用时，窗口 Closing 先取消关闭再隐藏；最小化也调用同一隐藏分支。托盘双击或“显示主窗口”执行 Show、必要时恢复 Normal 并激活，不新建窗口或监听。菜单“退出”及页脚“退出程序”先设置退出状态再调用 WPF Shutdown，避免关闭被再次隐藏。系统 SessionEnding 先标记退出，避免阻止注销；托盘初始化失败时记录错误并允许关闭窗口退出，不留下无法恢复的隐藏进程。

OnExit 解除窗口/系统事件，先隐藏并释放 NotifyIcon、菜单和克隆图标，再释放 DI 容器及监听。隐藏期间容器保持存活，不重复注册监听。生命周期和菜单派发/释放由自动化测试覆盖，关闭隐藏、用户协助恢复及显式退出有本机证据；实际最小化、系统注销、Explorer 重启及长期驻留仍待实机检查，详见 [DESKTOP_VALIDATION.md](DESKTOP_VALIDATION.md)。

### 状态分离与持久化

| 状态 | 含义与边界 |
| --- | --- |
| 实时音量状态 | Windows 当前可枚举设备、会话返回的状态；随事件更新 |
| 已保存音量状态 | 方案建立时的原始快照保留基准；每设备方案目标按用户实际调节自动保存 |
| 目标音量状态 | 各输出设备当前方案规则保存的音量和静音；生命周期恢复时应用 |

已在 Core 的 `Configuration/`、`Services/JsonConfigurationStore`、`Services/AudioConfigurationService` 及接口中实现。`System.Text.Json` 格式版本 2，包含设置、应用/设备、多个方案、每设备方案映射及当前默认输出方案 ID；方案有建立时的原始快照和独立规则目标，已有手动快照保持兼容。配置位于用户确认的 `%LOCALAPPDATA%\MixDeck\settings.json`，`.bak` 是上一份有效配置。缺字段/损坏可读取备份但不自动覆盖原件；未知版本阻止普通保存且不降级。显式导入/恢复默认保留原件；同目录临时文件 Flush(true)/原子替换，配合锁文件及内容指纹防止旧实例覆盖。详细异常和大小边界见 [CONFIGURATION_VALIDATION.md](CONFIGURATION_VALIDATION.md)。

`AudioConfigurationService` 不另行消费监听队列；`MainViewModel` 唯一消费者在 UI 上下文将变化转交服务，信号量串行化规则与配置操作，不轮询。启动/选择方案/编辑规则后匹配当前状态；新会话恢复相关目标，用户音量事件保存相关目标，设备重建或默认切换后应用对应设备规则。每项应用前回读实际值，相等不写；Self 延迟快照也按当前值判断，避免回传循环。失败对象停止自动重试，刷新或新的设备/会话实例可重新判断；暂缺应用规则保留。

`Services/AudioWriteGuard` 包装底层控制器并回读，基于失败或目标偏差超过 0.005 标记暂不可修改；UI 显示实际状态及原因、禁用对应滑块和静音，刷新可重试。它不把硬件支持位当成权限，也不宣称预知所有系统混合器灰色项。原始控制器继续由 DI 持有和释放。

Desktop 新增 `ConfigurationViewModel`、主窗口内的 `AdvancedOptionsView` 与文件/确认对话框适配；规则和存储业务留 Core。Windows 的 `WindowsLoginStartup` 实现 `ILoginStartup`，只控制当前用户 MixDeck Run 项，带引号的 exe 路径与 --tray，默认关闭；导入不能隐式开启。App 在显示/Loaded 初始化后按 --tray 隐藏，托盘失败则保持可见。设置写入失败时回滚启动开关；真实系统重启证据仍待反馈。当前 UI 已取消全局自动恢复开关，保留独立的登录启动与不可用设备偏好。

开发验收环境补充（2026-10-21）：由 MSIX 开发工具启动的进程曾使用私有 AppData/HKCU 视图，同环境回读无法证明系统可见。已保留原件并迁移到真实用户目录，通过普通 Explorer 启动、保存设置，使用独立 StdRegProv/Win32_StartupCommand/CIM_DataFile 核验。该处理没有改变配置路径约定、Run 实现或模块依赖；后续不能用工具私有副本覆盖实际用户配置，详见配置验收记录。

### 设备方案与自动保存调整（2026-10-22）

用户已明确授权本次业务语义调整；四项目依赖、事件驱动和系统调用边界不变。配置版本 2 新增 `DeviceProfileSelection(DeviceId, ProfileId)`，保留 `ActiveProfileId` 表示当前默认输出对应方案。首次自动建立方案只捕获对应设备；选择方案只改该设备绑定。版本 1 在严格校验后按各设备原有目标映射方案，优先原当前方案，移除旧开关，保留原始多设备快照/规则；首次写入的原子替换保留旧主文件为备份。高级选项只显示所管理设备的方案与规则，编辑旧多设备方案时保留其他设备规则。

`AudioConfigurationService` 仍由 MainViewModel 的唯一事件消费者转发状态，使用原有 semaphore 串行保护持久化与恢复；不新增扫描或后台计时器。相同实际值回调不重复写文件；每次不同用户目标完成持久化后才更新内存配置。失败保留系统已生效值与原保存目标并提示，避免回滚用户操作。S4-13 已取消长期草稿与整份规则提交，规则按方案/设备/规则 ID 仅合并修改字段，偏好同样逐字段持久化；UI 回传只更新显示，避免再次保存。输入文本仅在当前输入框暂存，回车或失焦提交、Esc 取消，离开设备时提交后结束编辑。

历史验收中的持续纠偏与全局开关仅代表 2026-10-21 行为；本次按用户明确指令替换，证据留在历史记录。当前执行与证据见 [CONFIGURATION_VALIDATION.md](CONFIGURATION_VALIDATION.md)。

### 隐式保存与同级页面（S4-13，2026-10-22）

用户确认移除手动新建和保存入口，已有方案及导入/导出/快照恢复继续兼容。MainWindow 使用两个同级 TabItem，AdvancedOptionsView 复用单例 ConfigurationViewModel；切页不创建窗口、监听或新服务。设置及规则请求由 UI 队列依次执行，Core 在既有 gate 内读取最新配置并合并；快照和 JSON v2 不变。登录启动注册表写入失败或配置保存失败时回退，并显示实际注册表值。

Core 提供 BeginVolumeEditAsync/EndVolumeEditAsync 短期编辑令牌，按端点及稳定应用身份延迟该目标的音量事件保存，不长期持有锁。Desktop 继续合并并实时写系统音量；鼠标松开、捕获/焦点丢失、键盘松键及单次滚轮结束后，等待 LatestValueWriter 排空，再回读并保存。无回调也能保存；晚到回调按实际值去重，同路径会话在最终提交后统一。无可用配置不能阻止普通音量控制。

切页、隐藏、刷新、用户切换设备及正常退出调用收尾流程；设备或会话失效时释放范围、保留目标并提示。事件和其他进程仍可并发修改系统，因此保留逐字段合并、实际回读、写入限制和文件冲突保护，不能以隐式保存推断不存在并发。系统注销/断电不能保证异步收尾完成，真实注销和重启仍单独验收。

### 程序选择与系统默认输出同步（S4-12，2026-10-22）

用户明确纠正此前“下拉只选择管理对象”的解释：下拉选择即切换 Windows 输出。Core 新增 `IDefaultAudioOutputController` 抽象；Windows 注册独立的 `WindowsDefaultAudioOutputController`，不修改既有音量控制接口和监听消费者。Desktop 通过此接口请求切换，在确认前保留上一次实际设备，暂时禁用设备选择及音量控件；成功后 Refresh 监听状态、核对多媒体默认端点并转交原配置服务，使方案随实际默认设备切换。列表重建/系统事件不能反向触发设置。不可用设备被拒绝，失败回读实际状态并提示，不把 UI 选择伪造为系统结果。

切换普通播放的 Console/Multimedia 角色，通信角色保持独立；Windows 层先验证可用 Render 端点、记录原角色，写入后逐角色回读。部分失败时尽力回退自己的改动，回退前检查角色仍为自己刚写入的值，不覆盖第三方并发选择；回退失败明确报告。所有 COM 创建、使用和释放都在自有 MTA 队列，未开始请求可取消，已开始请求按既有音频线程约定执行完并核对。

系统边界：现有 NAudio 查询接口没有默认端点写入封装；为满足用户明确的系统切换需求，在 Windows/Interop 单独隔离 PolicyConfig COM 写入。它是未文档化的 Windows 接口，不能标为微软公开稳定 API；其使用与原规划“公开音频管理接口”的一般原则存在具体差异，本次如实保留此边界，未替换技术栈、修改系统文件或注册表实现路由。ABI 已参照 [EarTrumpet 定义](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/MMDeviceAPI/IPolicyConfig.cs)及 [audioswitch 原始声明](https://github.com/tartakynov/audioswitch/blob/master/IPolicyConfig.h)核对。本机 Windows 11 已实测，其他 Windows 构建兼容性仍需验证，失败须显式反馈。

默认端点改变由 Windows 路由；应用显式绑定其他端点时，不额外改写该应用的独立路由。完整命令、真实切换/还原证据见 [CONFIGURATION_VALIDATION.md](CONFIGURATION_VALIDATION.md)。

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
| 规则与用户手动操作冲突 | 2026-10-22 用户确认调节即保存；启动/设备切换/新会话应用目标；恢复回调不回写，系统失败显示实际状态并停止重试 |
| COM 线程或回调异常 | 请求及实时监听已按 MTA 生命周期管理，创建借用引用受控转移，事件投递与关闭有自动化验证；Windows 10/真实拔插及长期兼容验证仍待阶段 6 |
| 会话尚未创建 | 保留规则，等待会话创建事件 |
| 用户配置丢失 | 配置备份、原子写入及异常处理 |
| 后台资源占用过高 | 事件驱动，限制轮询和重复操作 |

异常诊断需记录音量修改、恢复操作和 API 返回结果；提供重新获取会话、刷新状态及手动重应用目标音量的能力。先复现并验证“重新设置相同值”“改变后再恢复”等行为，再确定有效的自动恢复策略；不得承诺修复应用内部音量、驱动或其他处理环节造成的所有播放异常。

## 架构维护约束

- 本文件依据用户规划建立稳定基线；未定细节保持“待确认”，不把方案写成已实现事实。开发任务与验收见 [TASKS.md](TASKS.md)，当前状态见 [STATUS.md](STATUS.md)。
- 普通开发遵循此架构；重大调整须先说明原因、影响及建议，获得用户同意后再实施，不以更新文档代替确认。
- 发现源码或历史设计与基线不一致时，记录双方事实、差异和待决事项，不擅自以当前实现覆盖原始规划。
