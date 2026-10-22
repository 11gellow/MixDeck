# 阶段 4 配置保存与自动恢复

> 更新：2026-10-22。S4-11 设备方案/自动保存及 S4-12 系统输出切换已实现，最新 179 项回归通过；S4-09 实际重启登录仍待明确结果。2026-10-21 的开关/持续纠偏记录仅作历史，不代表当前行为。

## 2026-10-22：下拉同步 Windows 输出设备（S4-12）

用户明确指出：系统使用 A 时，在程序输出设备下拉选择 B，系统也应改用 B。此前下拉只展示 B 会话是实现解释偏差，本次已据用户指令修正；下方 S4-11 中“下拉仅选择管理对象”的说明保留为历史事实，不再描述当前行为。

实现与边界：

- 新 Core 抽象 `IDefaultAudioOutputController`，Windows 的独立实现由 DI 持有，在专用 MTA 创建、使用和释放 PolicyConfig COM。设置前验证可用 Render 端点并记录角色，设置后独立回读 Console/Multimedia；通信角色不改。没有修改系统文件或绕过 Windows 音频系统。
- Console/Multimedia 任一失败或回读不一致，尽力恢复自己已改动的角色；回退前检查仍是自己写入的目标，避免覆盖第三方并发选择。原无默认设备或回退失败如实报告，不承诺事务原子性。
- UI 发起切换后暂时禁用设备下拉和音量控件，保留已确认的实际设备；成功后刷新实时监听，核对多媒体默认 ID，再将实际状态转交原配置服务恢复对应设备方案。失败同样重新读取实际设备并提示。不可用设备拒绝设置，列表重建/系统变化不反向设置，外部默认变化始终同步，不再固定停留于此前手动选择。
- 现有 NAudio 没有默认端点写入封装，设置使用未文档化的 Windows PolicyConfig；这与原“公开音频管理接口”的一般原则有具体差异，未将其当作微软公开稳定 API，适配已单独隔离。来源与兼容边界见 ARCHITECTURE；Windows 10/其他构建仍需实机验证。应用独立指定输出时不强制改写应用路由。

先复现再修改：新增 `SelectingOutputChangesWindowsDefaultInsteadOfOnlyDisplayingItsSessions` 在旧 setter 上稳定失败，预期写 device-b、实际写入列表为空；TRX `default-output-red.trx`。修正后失败/无效写入、待完成期间控件、原生双角色/回读/回退/第三方并发和配置恢复场景均通过。

最终命令：

```powershell
dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror
# 仅在构建成功后执行
$env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = '1'
dotnet test tests/AudioManager.Tests/AudioManager.Tests.csproj --configuration Debug --no-build --nologo --logger 'trx;LogFileName=default-output-final.trx' --results-directory tests/AudioManager.Tests/TestResults
```

最终标准路径构建 0 警告、0 错误；**179/179 通过**（168 单元/组件 + 11 真实音频集成），0 失败、0 跳过。报告 `tests/AudioManager.Tests/TestResults/default-output-final.trx`。新增 15 项覆盖：原生默认输出组件 9、Desktop 切换 4、配置联动 1、真实默认切换 1。既有不跟随手动选择及只查看不可用设备的测试按用户新要求调整。

真实集成 `SwitchingSystemDefaultOutputChangesActualRolesAndNotifiesMonitorThenRestoresOriginals`：从原耳机切至可用扬声器，由独立 WindowsAudioController 查询两普通播放角色为目标，通信角色不变；收到真实监听通知，完整状态含新的多媒体默认 ID。finally 按原 Console/Multimedia 各自 ID 分别恢复并回读，不写系统音量/静音。

第一次通知测试曾等待“独立 Multimedia 回调”而超时，但原生设置和所有角色回读此前已成功，finally 也已恢复。进一步核对真实通知为 Console，其完整快照已经包含 Multimedia 新默认；监听器会去重，后续同状态通知不一定单独发布。测试已改为验证默认设备变化通知中的实际完整状态，不要求每角色各发布一条，未修改监听去重实现。`default-output-live.trx` 重跑通过。

GUI 实测（原路径 exe，通过普通资源管理器启动）：

1. 用户从托盘正常退出占用旧 DLL 的实例后，完成最终构建与回归；新版正常启动。记录原默认为“耳机 (3- Realtek(R) Audio)”，三角色均指向 ID `{0.0.0.00000000}.{49cbda8a-2887-4fd7-90cb-81a68b5e53fc}`，主音量 15%/未静音。这是本轮原值，不用上一轮的 17% 覆盖用户新调节。
2. 在程序下拉选“扬声器 (3- Realtek(R) Audio)”并确认。独立 PowerShell 进程通过 NAudio/MMDeviceEnumerator 查询：Console/Multimedia 均为扬声器 ID `{0.0.0.00000000}.{3ac5ed3b-e136-4277-8423-89a3afd13e43}`，Communications 仍为原耳机；程序扬声器显示“默认”、主音量 100%/静音，列表同步。这里是真实系统输出切换，不是旧版只查看列表。
3. 再通过程序下拉选回原耳机。独立查询三角色均为原耳机，主音量 0.15/未静音；UI 默认标记、15% 与成功日志一致。实际测试的输出已恢复，新版保持运行供用户继续使用；没有更改登录启动设置或重启电脑。

独立回读使用本机 .NET 已安装的 NAudio 程序集，创建新的 MMDeviceEnumerator，分别 GetDefaultAudioEndpoint(Render, Console/Multimedia/Communications)，读取 ID、友好名、主音量和静音后 Dispose。该进程不使用 MainViewModel 状态、不调用默认输出或音量写入，避免用界面自身声明当成系统证据。

验证缺口：本机 Windows 11 基本输出双向切换及方案联动已有证据；Windows 10、更多驱动/设备、物理拔插、失败硬件和电脑重启仍按各自任务保留。模拟失败/回退验证不冒充真实系统拒绝或真实拔插。

## 2026-10-22：设备方案与调节自动保存（S4-11）

用户明确要求每个输出设备对应自己的保存方案，更换输出自动选择；取消“启用当前方案自动恢复”，前端和系统入口的音量调节自动保存且主界面可调。这是对前一天已确认的“程序持续纠偏、受管控件禁用”策略的明确替换，已据此更新 PROJECT 和 ARCHITECTURE；四项目分层、依赖和系统接口不变。本轮没有开始阶段 5。

当前行为：

- 用端点 ID 记住设备当前方案；Windows 多媒体默认输出变化时自动选择并恢复该设备目标。主窗口下拉仍只选择管理对象，配置页显示该对象对应方案，不修改系统默认输出。
- 无方案设备首次可读时创建默认方案；后续新建快照只保存管理设备。路径不可读项跳过；初始多会话冲突不任意选值，后续调节任一可读会话可保存并统一目标。原始快照作为回退基准保留。
- 启动、默认设备切换、设备监听恢复、新会话及显式刷新应用对应目标，无全局启用开关。普通音量事件不会把用户新值强行拉回。
- 用户前端 Self 或系统入口 External 音量/静音事件经新鲜回读确认后，仅保存相关目标；同设备同程序路径的会话使用该新目标。不整份回写实时快照，原始快照不变。用户细调的保存比较独立于恢复容差，小于 0.5% 的实际改变也会保存；每次不同目标在原有 gate 内原子持久化；相同实际值回传不重复保存，无新增轮询/定时器。
- 已执行恢复的实际值作为回调基线，延迟/重复 Self 回传不学习旧快照；失败回读不替换目标，保留系统实际值并显示限制。RestoreReadback 将恢复实际值通知 UI，防止一秒 Self 过滤挡住合法快照恢复。用户显式恢复快照时也更新该设备恢复目标。
- 自动保存失败显示错误并保留系统已生效音量、旧内存目标和现有文件，不回写音量抵消用户调节；配置加载受阻不自动建立/覆盖方案，显式导入或重置后恢复记忆。
- 规则表的未提交草稿和未保存登录设置不因自动保存被清空；旧草稿保存时检查规则是否已变化，拒绝覆盖期间的新音量。旧多设备方案仅显示、编辑当前设备规则，其他目标合并保留，应用规则也仅作用当前设备，不覆盖其他设备尚未消费的用户调节。
- 格式版本 2 增加设备方案映射，移除旧全局开关。严格校验版本 1 后迁移：优先绑定原当前方案中对应设备的目标，否则绑定包含该设备的已有方案，保留所有快照/规则；首次写入将原主文件留为 `.bak`，后续 `.bak` 按正常上一有效版本滚动。未知版本不降级/覆盖。登录启动偏好独立，默认仍关闭，导入沿用本机登录设置。

验证命令（最终标准输出路径）：

```powershell
dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror
# 仅在构建成功后执行测试
$env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS = '1'
dotnet test tests/AudioManager.Tests/AudioManager.Tests.csproj --configuration Debug --no-build --nologo --logger 'trx;LogFileName=device-profile-final.trx' --results-directory tests/AudioManager.Tests/TestResults
```

结果：四项目构建成功，0 警告、0 错误；**164/164 通过**（154 单元/组件 + 10 真实音频集成），0 失败、0 跳过。报告 `tests/AudioManager.Tests/TestResults/device-profile-final.trx`。旧需求测试已改为新语义，并增加设备映射/独立目标、无开关首次记忆、Self/External 自动保存、多会话同步、失败保护、旧版迁移、规则草稿冲突、每设备编辑、精细音量调节保存和重置解除受阻覆盖。真实 WPF STA 资源/XAML 加载与布局通过。

真实音频集成只写测试创建的无声会话：独立新进程从 JSON 恢复 0.37/true；新 PID 会话恢复相同目标；独立系统事件上下文写 0.23 后两测试会话均为 0.23，JSON 规则为 0.23，原快照仍 0.37；再次写 0.19 后两会话及规则均为 0.19。finally 恢复原值并回读、清理测试子进程和临时文件。不将该测试称为 Windows 音量混合器人工操作或电脑重启。

实机 GUI：

1. 正常退出旧实例后在原 Desktop Debug 路径完成最终构建，通过普通资源管理器双击 exe 启动；避免开发工具的私有 AppData/HKCU 视图。程序实时同步、所有正常滑块可用，配置页无全局自动恢复复选框。
2. 默认耳机主音量原为 17%，通过主界面改到 16%，日志显示自动保存对应耳机默认方案；随后恢复 17%，日志/规则均为 17%。没有点击保存设置或规则。后续程序正常退出并重新启动仍显示 17%。
3. 主窗口选择管理另一可用扬声器：显示 100%/静音；其配置页当前方案仍为“阶段4验收快照”，原始快照保留 12 个目标，当前设备仅列 7 条规则。默认耳机使用另一个独立默认方案。这里只验证管理对象及配置上下文切换，不等同系统默认设备切换。
4. 最终新版已从原 exe 路径启动留给用户继续使用，未修改登录启动偏好，未重启电脑。

构建过程中的环境问题：旧程序占用默认输出 DLL，首次标准构建失败；先在独立产物路径验证，再正常退出旧实例。最初将独立产物放在 Tests 下的 TestResults，使后续标准构建误收集嵌套生成的 `.cs` 并报重复特性；已核对路径并移至根目录被忽略的 `artifacts/`，最终标准构建和 164 项回归成功。该次构建失败后误跑的旧 150 项结果不作为当前证据，最终 TRX 已由成功构建后的 164 项结果覆盖。没有为此改变项目编译规则或关闭检查。

验证边界：双设备默认切换/恢复通过模拟事件和真实 API 组件验证；本轮没有实际修改 Windows 默认设备或物理拔插，也未执行 Windows 10、长期驻留、系统重启。Self/External 仅能说明来源，无法证明第三方操作意图；运行时可回读的外部调节作为新目标，不宣称自动识别所有应用自身异常重置或听感异常。

当前重启验收步骤：在主窗口选相应设备、正常调节到目标并确认自动保存；如需登录启动，单独勾选“登录后启动到托盘”并保存设置。正常重启登录后检查托盘、实时同步和恢复值，再验证稍后出现会话。收到实际结果前 S4-09 保持待验证；不要求再寻找已移除的自动恢复开关。

## 2026-10-21 历史实现与验证

以下开关、持续纠偏及受管禁用记录是旧需求下的实测历史，已被上面的 S4-11 用户指令替代。涉及自启动隔离根因及真实用户环境的记录仍对后续验收有效。

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

## 用户重启验收步骤（旧版历史操作）

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

## 用户使用反馈与后续边界（2026-10-22）

- 用户反馈“已启动，目前核心功能已可以正常运行”，并询问小瑕疵和规划想法差异是否留到最后。该反馈支持程序启动及核心可用，不补写其未明确提供的登录自动启动方式、恢复数值或稍后会话的重启验证结果；S4-09 继续待验证。
- 当日只读系统快照：最近系统启动为 13:05:45，查询时未列出 Desktop 进程或 MixDeck 启动清单项；真实用户配置存在，13309 字节，最后修改 00:03:35。快照仅反映查询时状态，不推定用户此前未成功启动，也不推定新的故障原因；没有重启、启动程序或改动用户设置。
- 已请用户提供最在意问题的实际行为、期望行为及使用影响。尚无具体缺陷、复现结果或新需求决策；先核对规划，再安排单项处理。正确性和已确认需求偏差优先，纯视觉细节可集中完善。
- 本轮仅补充记录；构建和音频业务测试不适用，未重复执行。此前 150 项通过属于历史代码回归证据，不作为本轮新增验证。


## S4-13：隐式保存与同级高级选项（2026-10-22）

### 已确认行为和实现

- 用户明确取消手动保存、手动新建方案和长期规则草稿，主窗口采用“音量管理”“高级选项”两个同级页签。保留已有/导入方案、导入导出、快照恢复及删除；选中方案立即保存绑定并应用。新设备自动建立默认方案和原始快照，后续目标变化不改快照，JSON 仍为 v2。
- 规则按方案/设备/规则 ID 在 Core gate 内合并实际修改的字段，偏好同样按字段更新；界面配置通知只同步显示。输入回车提交并移动焦点到页面标题，失焦/切页/隐藏提交有效值，Esc 取消；非法值恢复最新保存结果并提示。登录启动仍使用注册表写入及持久化失败回退。
- 滑块拖动实时写系统，编辑范围只延迟对应设备/稳定应用目标的音量保存，其他目标继续保存。松手、捕获/焦点丢失、键盘松键、滚轮单次操作后等待末次写入排空，强制回读并持久化，晚到回调去重；同路径会话在提交最终目标后统一。范围跨等待不持有 Core 锁。
- 刷新、用户切换设备、切页/隐藏及正常退出执行收尾；失效时释放范围并保留旧目标，文件冲突不覆盖其他进程的配置。无可用配置也不能阻止普通音量调节。系统注销/断电不承诺完成异步收尾，仍需 S4-09/阶段 6 实际验证。

### 自动验证与实机证据

最终命令（标准路径）：

```powershell
dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror
$env:AUDIO_MANAGER_RUN_INTEGRATION_TESTS='1'
dotnet test tests/AudioManager.Tests/AudioManager.Tests.csproj --configuration Debug --no-build --nologo --logger "trx;LogFileName=implicit-save-final-standard.trx" --results-directory tests/AudioManager.Tests/TestResults
```

- 构建 0 警告、0 错误。最终 **189/189 通过**（178 单元/组件 + 11 真实音频集成），0 失败/跳过；TRX：`tests/AudioManager.Tests/TestResults/implicit-save-final-standard.trx`（忽略的本机产物）。
- 新增 10 项隐式保存回归，并更新原草稿/命令测试。覆盖单方案外部事件交错、字段合并、原始快照和其他设备不变、拖动实时写入但中间目标不保存、其他目标照常保存、无回调强制回读、重复收尾、设备/会话消失、拒绝写入、方案替换、文件冲突、主页面偏好自动保存及写入队列排空。
- WPF STA 组件测试加载真实主窗口和高级选项 XAML，运行 DispatcherSynchronizationContext，验证两个同级页签、共享 VM、六列规则表和无保存按钮；通过真实绑定和路由事件验证回车提交、Esc 取消及失焦保存。初次扩展测试缺少 UI 同步上下文及布局泵，造成测试失败/一次测试主机中止，已修正测试环境后全部通过，未将中止记为通过。
- 真实 Core Audio 及原生通知：既有独立进程恢复/迟启动/系统事件用例中，编辑范围内 0.31→0.47 实时生效且磁盘逐次保持原样；结束后磁盘保存 0.47，同路径另一测试会话统一，原始快照仍为 0.37。仅使用无声测试会话，finally 恢复并回读，临时配置与进程已清理。
- 首轮完整实机测试时旧版程序仍后台运行，出现原生事件上下文、多会话音量和通知等待失败；第二次尝试退出未成功，仍有失败，不属于隔离验收。通过实际窗口“退出程序”正常退出并核对进程消失后，隔离回归通过；最终标准路径 189 项再次全通过。实机测试前必须正常退出其他 MixDeck 实例，避免它按同路径规则同步测试会话。
- 实际界面已显示同级页签及高级选项中的规则输入/开关，原保存与新建按钮不存在；规则输入 15→14 并切页后主页面实际显示 14%，随后恢复 15%。最终通过普通 Explorer 双击标准路径 exe 启动，保留真实用户环境原设备方案（原快照时间 2026-10-22 18:48:17），主音量 15%/未静音。开发工具直接启动使用独立私有视图，仅作界面验证，没有用其配置覆盖真实用户文件，也没有更改登录启动设置。
- 鼠标长按过程的每一帧没有通过桌面自动化逐帧采样；中间值不保存和收尾依据组件与真实音频事件测试。真实重启、注销、Windows 10、物理拔插和长期驻留未执行，继续保留对应验证缺口。
