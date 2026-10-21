# 阶段 4 配置保存与自动恢复

> 日期：2026-10-21。阶段 4 功能实现及可自动运行验证已完成；真实重启登录等待用户反馈。S4-09 保持待验证。

## 本轮确认与边界

- 用户要求持续完成当前模块，当前模块为阶段 4；阶段 5 的异常实验和阶段 6 发布工作不在本轮范围。
- 用户确认：当前方案以程序为准；系统拒绝修改或回读未生效时，显示实际状态并呈现不可修改效果。启用后外部偏离会恢复；停用不再自动写入。受管项目在方案规则中修改，实时滑块不隐式改写快照。
- 用户确认：按完整程序路径匹配，同程序多个会话统一目标。按原设备端点 ID 匹配，不跨设备、不同程序路径或 PID 猜测。系统声音使用专门身份；路径不可读项跳过并报告。相同设备同程序的多个会话数值不一致时，保存失败并提示先统一，不任意挑选。
- 用户确认：配置位于 `%LOCALAPPDATA%\MixDeck\settings.json`，同目录备份；登录启动默认关闭，由设置控制当前用户 Run 项，参数 `--tray`。导入不能隐式开启自动写入或登录启动。
- 实时快照、用户主动保存的快照、可编辑规则目标分离；规则编辑不更改原始快照。方案默认不启用自动恢复，必须明确选择当前方案并保存启用设置。

## 模型和存储

Core 的 Configuration 记录版本、软件设置、已知应用/设备、多个方案、快照及独立规则目标；不持久化 PID 或运行时会话键。`System.Text.Json` 由 .NET 提供，没有新增包或模块依赖。

版本 1 是首个格式，无旧格式可迁移。未知版本不降级到备份、不覆盖；缺字段、损坏及非法目标有明确错误。有效备份可用于读取恢复，但读取本身不改写主文件。普通保存使用同目录临时文件、Flush(true) 及原子替换；保留上一份有效主文件。修复损坏主文件时保留原件副本并保留有效备份。显式导入/恢复默认也保留原件；不同进程写入用锁文件及内容指纹检测防止旧实例覆盖。

配置/导入上限 4 MiB；未提交临时文件不会加载。导出到新文件，不覆盖当前配置或备份，也不静默覆盖已有导出文件。

## 权限与限制的可观察边界

微软文档说明 [SetMasterVolume](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-isimpleaudiovolume-setmastervolume) 返回写入结果并使用事件上下文区分来源；[QueryHardwareSupport](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-queryhardwaresupport) 仅说明硬件支持，缺少硬件功能时系统可提供软件控制。因此不能将硬件支持位当成“混合器是否灰色”的权限标志。

本轮采用实际调用结果和回读确认：失败或偏差超过 0.005 标量时显示实际值、不可修改原因并禁止重复写入；用户点击刷新可显式重试，设备重建/新实例重新判断。该机制不能预知所有系统混合器灰色项，也不检测实际听感异常；未出现的真实系统限制需后续实机补验。

登录启动采用微软文档的[当前用户 Run 项](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)，不在启动过程中反复写注册表。真实重启、登录和 Windows 10 兼容检查不能由单元测试替代。

## 已执行检查

- 配置存储 10 项测试通过：往返/偏好/备份、损坏主文件恢复、未知版本不降级、缺字段阻止覆盖、显式重置保留原件、锁定写入失败、旧实例冲突、取消、导入导出、非法音量/重复目标/未知引用/PID 身份、孤立临时文件和超大配置。
- 首次测试发现超大配置的 `InvalidDataException` 未纳入受控加载分支，已修正并重跑通过；加入流式指纹，超大损坏文件可在显式重置前保留原件。
- 截至该检查点：严格 Debug 构建 0 警告、0 错误；原 21 项 Desktop ViewModel 与新增 10 项存储测试共 31/31 通过。
- 恢复时检查到实际 Git 为 main 分支、已有提交；未执行初始化或提交。旧状态文档“master 无提交”是历史记录，与当前事实不同。

## 待补齐

- 真实系统重启登录：用户已明确选择本轮手动重启并反馈；已打开最新程序供其检查目标、开启自动恢复/登录启动并保存。检测到用户输入后停止界面操作，等待反馈。
- 物理设备变化、系统限制导致的混合器灰色项、Windows 10、高 DPI/最小尺寸全场景及长期后台，继续保留对应阶段 6 缺口。

## 最终自动化检查点

```powershell
dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror
$previousSetting = $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS
try {
    $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = '1'
    dotnet test tests/AudioManager.Tests --configuration Debug --no-build --nologo --logger 'trx;LogFileName=configuration-final.trx' --logger 'console;verbosity=minimal'
} finally {
    $env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = $previousSetting
}
```

- 四项目构建成功，0 警告、0 错误；**150/150 通过**，0 失败、0 跳过（140 单元/组件 + 10 真实集成），退出码均为 0。TRX：`tests/AudioManager.Tests/TestResults/configuration-final.trx`。
- 本轮新增 34 项：配置存储 14、恢复/身份/规则 12、配置 ViewModel/WPF/受限控制/隔离注册表 7、真实配置联调 1；原 116 项均保留并通过。
- 恢复覆盖：快照与规则独立、应用重启/PID/实例变化、多会话统一、同名不同路径不误匹配、不可读身份、缺设备/迟会话、外部纠偏、Self 延迟回传、不重复写、停用、规则禁用、多方案切换/删除、导入关闭自动恢复、默认配置不改系统音量、失败/无效写入禁用及刷新重试、未知方案报错。
- ViewModel 覆盖命令链路、非法规则拒绝、登录设置失败、保存失败回滚、受管/系统受限控件禁用和释放。WPF STA 中加载真实资源/XAML并布局；实机另行观察，组件测试不冒充点击验收。
- 配置窗口首次测试发现独立测试进程未初始化 WPF pack URI，已修正测试宿主初始化后重跑；不是产品运行失败。

## 真实音频联调

`LiveConfigurationTests` 在既有串行真实音频测试类中运行，只写入自己创建的无声子进程会话：

1. 保存单独测试会话目标 0.37/true，持久化后销毁原配置服务。
2. 将系统该会话改成 0.8/false，由另一个新进程读取 JSON、注册监听、匹配并恢复；父进程独立回读为 0.37/true。
3. 再启动新 PID/新实例的无声子进程，收到真实创建事件后按相同路径恢复到 0.37/true。
4. 使用独立事件上下文写入 0.23，真实 External 通知后恢复为 0.37；另一个测试会话保持目标。
5. 关闭自动恢复后外部写入 0.19，处理通知后仍为 0.19；实时过程不改配置快照。
6. finally 恢复所有测试会话的原始值并回读，停止子进程并删除临时目录。不修改用户其他应用音量或系统主音量。

测试探针入口 `--configuration-restore-probe` 仅在 Tests 工程中，过滤到指定测试 PID，拒绝含主音量目标的配置；不是 Desktop 产品参数。它证明独立进程加载链路，不证明操作系统重启。

## 真实窗口与后台启动

- 正常启动最新 Desktop 后实时同步可用，打开“方案与自动恢复”；初始无方案，自动恢复/登录启动默认未选。
- 真实输入“阶段4验收快照”并保存，窗口显示当前方案、12 个快照目标及 12 条规则；磁盘 JSON 回查版本 1、1 个方案、两个开关 false。保存本身没有写入系统音量。
- 在规则表输入 abc 并点击保存，界面明确提示目标必须在 0～100；磁盘对应规则仍为原值 1.0，未错误保存。
- 观察后改善规则表的设备友好名称、文字省略/悬停、行高与分隔线；外层可滚动，避免小窗口底部入口不可达；最终构建和全量测试在这些改动后通过。跨 DPI/最小尺寸仍不推定为实机通过。
- 关闭配置窗口、点击主窗口“退出程序”；首次自动化输入因 geometry unavailable 失败，刷新绑定后重试正常退出，之后构建前确认无 Desktop 进程。该 UI 工具问题未被记录为产品退出失败。
- 单独启动 `--tray` 验证进程 3 秒后仍运行、主窗口句柄 0、Responding=true；测试清理该进程。随后正常启动最新构建供用户重启验收，检测到用户操作后停止输入。最后启动的程序是交付用户继续验收的实例，不应当作为测试残留强制结束。

## 用户重启验收步骤

1. 在“方案与自动恢复”检查当前方案与各目标，按需保存实际希望恢复的音量。
2. 勾选自动恢复、登录后启动到托盘，点击保存设置。确认没有错误提示。
3. 正常重启 Windows 并登录，观察程序是否出现在托盘；双击恢复，检查当前方案、实时同步及 API 音量是否为目标。
4. 对原先未启动的应用，启动并产生音频会话，检查是否应用方案目标；系统拒绝时应显示实际值和无法修改提示。
5. 向本会话反馈实际成功项、失败项和提示。仅用户表示计划重启不等于完成；收到实际结果后更新 S4-09。

## 文档与依赖核对

- 本轮重新读取 STATUS、TASKS、PROJECT、ARCHITECTURE 及相关代码，按用户确认更新实现事实；未改变稳定技术栈或整体分层，AGENTS.md 未修改。
- 11 份 Markdown 严格 UTF-8、代码围栏和 45 处本地链接检查通过；64 个任务编号唯一，43 完成、21 未完成（含 S4-09）。TRX XML 核对 150 通过、0 失败。
- Git 差异空白检查通过（CRLF 视为合法行尾）；Core 无项目/第三方包引用，四项目依赖保持原分层。源码和文档未提交，未打包发布。

## 首次真实重启反馈与排查

- 用户反馈：已重启，系统托盘没有自启动程序，因此尚未测试音量恢复。此为未通过的自启动尝试，不能继续表述为“等待首次重启反馈”。
- 只读证据：Windows LastBootUpTime 为 2026-10-21 23:27:56；没有 AudioManager.Desktop 进程；exe 路径存在。配置最后修改时间为 23:06:02，自动恢复和登录启动仍 false；HKCU Run、Run32 及 StartupApproved 对应检查没有 MixDeck 启用记录。
- 当前可确认的是“本机没有有效启用记录”，不能据此认定 Run 命令错误、Windows 拦截或启动崩溃。已向用户核对勾选及保存操作；暂不改音量目标，也不把未执行的恢复记为失败。
- 排查顺序：设置未提交 → 未保存开关在其他操作中重置 → 注册后被外部移除。后两项需复现/证据，不提前写成根因。
- 用户补充：不确定是否曾开启/保存这两个开关；无法重建先前操作。可以确认当时没有有效启用记录，不能将某一未复现的界面路径归为本次根因。
- 后续由真实窗口操作：打开现有方案，勾选自动恢复、登录后启动到托盘并点击保存，界面显示“设置已保存”。23:35:03 独立回查 JSON 两值 true，HKCU Run 已写入带引号的 exe 绝对路径与 --tray，目标 exe 存在，StartupApproved 无对应禁用记录；原 12 个快照/规则目标未修改。
- 实际注册路径在部分目录名上使用小写，按 Windows 路径大小写不敏感规则验证等价。首次诊断脚本使用大小写敏感比较失败，纠正比较方式后检查通过；不是产品路径错误。
- 复验命令：`dotnet test tests/AudioManager.Tests --configuration Debug --no-build --nologo --filter 'FullyQualifiedName~CommandsSaveSelectEditRestoreImportExportAndResetThroughBusinessService|FullyQualifiedName~WindowsLoginRegistration|FullyQualifiedName~FailedPersistenceRollsBackStartup'`，3/3 通过。本轮只进行了设置启用、只读诊断和文档更新，没有修改业务代码，因此没有为相同代码重复完整 150 项回归。
- 当前已实际启用自动恢复及登录启动，程序保留运行供复验。真实再次重启和音量恢复仍待用户执行/反馈，S4-09 不勾选。

## 第二次真实重启失败：隔离视图与真实用户环境

用户第二次反馈重启后无托盘，音量仍被重置；系统 LastBootUpTime 为 23:38:30，无 Desktop 进程，但工具视图内两个开关 true、Run 项仍在。这推翻了“同环境回读成功就足以证明有效注册”的判断。

按可复现的系统边界逐项核对：

1. .NET 10 Desktop 运行时存在于系统标准安装位置，无 DOTNET_ROOT 覆盖；G 为本地 NTFS 固定磁盘。原命令从 System32 目录启动后进程存活、主窗口隐藏，未复现工作目录/运行时失败。该诊断临时进程已清理。
2. 本地 Registry/`reg.exe` 读取 MixDeck 成功；独立 `StdRegProv.GetStringValue` 返回 1、`EnumValues` 不含 MixDeck，`Win32_StartupCommand` 也不含该项，而同一真实用户的 OneDrive 等可读。
3. 在临时 `Software\MixDeck.Tests\RegistryVisibility-*` 键复现：本地写入/读回成功，WMI 返回 2（不存在）；临时键已清理。这是可见性差异的确定反馈信号，不依赖再次重启。
4. 真实系统 `CIM_DataFile` 查不到用户路径的配置，但发现原配置位于 `C:\Users\25353\AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\MixDeck\settings.json`（13307 字节）。私有 Helium User.dat 时间也与此前设置写入吻合。当前 shell 的 GetCurrentPackageFullName 返回 15700，并不能单独说明子进程的所有注册表/文件视图相同；最终依据独立系统提供者的对照。

这些现象与微软文档的 [MSIX 文件及 HKCU 写入虚拟化](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)一致：私有视图不提供 Windows 正常登录启动所需的全局用户可见性。未发现程序崩溃证据，也没有关闭、修改或绕过安全软件设置。

处理与复验：

- 不改业务代码、不改当前用户 Run 方案。原配置保留；先复制到被 Git 忽略的 `tests/AudioManager.Tests/TestResults/standalone-data/MixDeck/settings.json`，SHA-256 与原件一致，再通过普通 Explorer 将目录复制到真实用户 Local 下；不是覆盖已有用户配置。
- 通过 Explorer 双击 Debug exe，由普通桌面进程加载真实配置，原方案 12 个目标均存在，自动恢复启用。此时登录启动实际值为关闭，界面明确显示与配置不同，进一步验证两种视图之前不一致。
- 在普通进程勾选登录启动并保存，界面成功。随后系统 `StdRegProv.GetStringValue` 返回 0 和正确 exe/--tray 命令；`Win32_StartupCommand` 首次实际列出 MixDeck；`CIM_DataFile` 查到真实用户配置，23:55:45、13307 字节。两个开关保持开启，正常实例 PID 28368 留给用户继续验收。
- Explorer 复制使用原生工具栏复制/粘贴；路径导航先安排好。键入路径会替换工具的文本剪贴板，因此不能在复制文件后再用 type_text 导航，否则文件剪贴板被替换。迁移原件和真实配置均保留。
- 本轮无源码变更，不为环境修复重复编写业务测试；150 项历史回归依然只证明相应代码行为。新增真实系统提供者对照检查通过，但真实登录启动/恢复尚不能勾选。

后续开发验收须注意：从普通资源管理器直接启动 exe；不要把开发工具中读取的私有配置覆盖到真实配置。登录注册以独立系统查询为准：

```powershell
Get-CimInstance Win32_StartupCommand -Filter "Name='MixDeck'"
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Invoke-CimMethod -Namespace root/default -ClassName StdRegProv -MethodName GetStringValue -Arguments @{
    hDefKey = [uint32]2147483651
    sSubKeyName = $sid + '\Software\Microsoft\Windows\CurrentVersion\Run'
    sValueName = 'MixDeck'
}
```

实际重启后若再次失败，沿真实进程/启动记录继续排查；不得再次仅依据私有视图内的 true 判定已修复。
