# 阶段 1 音频控制核心验收

> 日期：2026-10-20。用户本次明确要求完成设备、主音量和会话控制核心并测试；范围为 S1-01～S1-10。本文记录实际验证，不代表阶段 2～6 或跨系统兼容验收。

## 环境与命令

- Windows 11 家庭版中文版，版本 `10.0.26100`，x64。
- .NET SDK `10.0.204`、运行时 `10.0.8`；SDK 由根目录 `global.json` 选择。
- `dotnet restore AudioManager.sln --nologo`：四项目依赖还原成功。
- `dotnet build AudioManager.sln --configuration Debug --no-restore --nologo --warnaserror`：四项目构建成功，0 警告、0 错误。
- 启用 `AUDIO_MANAGER_RUN_INTEGRATION_TESTS=1` 后执行：

```powershell
dotnet test tests/AudioManager.Tests/AudioManager.Tests.csproj --configuration Debug --nologo --logger 'console;verbosity=normal' --logger 'trx;LogFileName=core-final.trx'
```

结果：36 项通过、0 失败、0 跳过，包含 31 项单元测试和 5 项真实设备集成测试；进程退出码 0。原始结果位于 `tests/AudioManager.Tests/TestResults/core-final.trx`（生成文件，不纳入版本管理）。测试使用无声流创建自己的应用会话；主音量与测试会话的音量、静音均在 `finally` 恢复，并回读核对。

## 逐项验收

| 任务 | 实现与证据 | 结果 |
| --- | --- | --- |
| S1-01 | `AudioManager.sln` 与 SDK 配置可读取；实际构建入口如上 | 完成 |
| S1-02 | Core、Windows、Desktop、Tests 均在解决方案内；Desktop 为 WPF，Tests 为 xUnit；四项目构建成功 | 完成 |
| S1-03 | `.csproj` 引用：Windows → Core，Desktop → Core/Windows，Tests → Core/Windows；Core 无包、项目或 UI 框架引用。DI 测试验证单例、解析及释放；Desktop 启动/退出时组合和释放服务 | 完成 |
| S1-04 | 实际还原 NAudio.Wasapi/NAudio.Core 3.2.0、Microsoft.Extensions 的 DI/Logging 包 10.0.13、xUnit 2.9.3、VSTest 适配器 3.1.4、Test SDK 17.14.1；System.Text.Json 由 .NET 10 提供 | 完成 |
| S1-05 | 真实枚举 22 个输出端点，含 2 个 Active，以及 Disabled、NotPresent、Unplugged 状态，名称和 ID 非空。无设备返回空集合；部分读取错误使用 Issues，整体 API 失败抛出异常；空结果与失败路径另有模拟测试 | 完成 |
| S1-06 | Console、Multimedia 默认端点为 Razer Barracuda X 2.4；Communications 为 3- Realtek(R) Audio。与独立直接查询 Windows Core Audio 的结果逐项相同；无默认设备返回 null 有单元测试 | 完成 |
| S1-07 | 主音量 1.00 → 0.95，返回值与再次读取一致；静音设置与回读成功；最后恢复 1.00、静音 False 并核对。单元测试拒绝越界、NaN、正负无穷值，合法端点值通过 | 完成 |
| S1-08 | 实际读取名称、PID、路径、会话标识、实例 ID、状态、音量和静音；同一 testhost 进程的两个 GUID 会话保留不同实例；权限不足、路径不可读和进程退出的元数据降级有模拟测试 | 完成 |
| S1-09 | 两个同进程会话和一个另一进程会话之间，按设备 ID + 实例 ID 精确控制。第一个会话设置 0、0.37、1 及静音；另一进程会话设置 0.23；每步核对其他测试会话未被改变，并验证恢复原值 | 完成 |
| S1-10 | 真实无效设备/实例返回明确错误；真实子进程退出后会话被排除，旧键返回 SessionUnavailable。20 次创建/读取/释放无异常；单元测试覆盖初始化失败、HRESULT/日志、取消、错误后继续使用、MTA 同线程执行/清理、关闭时完成已接受操作及重复释放 | 完成 |

真实测试位于 [LiveAudioTests.cs](../tests/AudioManager.Tests/Integration/LiveAudioTests.cs)。模拟输入不会被当作拔插设备或音频服务故障的实际复现。

## 实现边界与剩余验证

- Core 只定义抽象与实时快照；没有实现规则、配置保存、目标状态或自动恢复，也不会把当前音量自动保存为目标值。
- Windows 每次操作重新获取设备/会话；NAudio 对象在专用 MTA 线程内通过 `using` 释放，控制器关闭时结束线程与 COM 初始化。当前没有长期监听或高频扫描；事件订阅、自发事件识别和监听重建留待阶段 2。
- Desktop 只有模板空窗和 DI 生命周期入口，尚无音量 UI、MVVM 或托盘；这些仍属于阶段 3。API 示例见 [README.md](../README.md)。
- 日志经 `Microsoft.Extensions.Logging` 输出，整体失败记录 Error，单项不可读记录 Warning；Desktop 使用 Debug 提供器，持久诊断日志留待后续任务。
- Windows 10 未提供测试环境；本次未执行 Windows 设置页人工视觉对照（以系统 Core Audio 实际配置为对照）、物理设备拔插、重启恢复、长期资源占用、安装打包验证，不能将本机通过推定为全平台通过。
- 20 次释放检查仅支持本次基础生命周期验收，不证明长期无泄漏；API 标量回读不等于最终听感验证。

实现采用 NAudio 的 [Core Audio API](https://naudio.github.io/NAudio/api/NAudio.CoreAudioApi.MMDeviceEnumerator.html) 和 [NAudio.Wasapi 3.2.0](https://www.nuget.org/packages/NAudio.Wasapi/3.2.0)；音频 API 无自写 COM 接口，COM 初始化使用 Windows `ole32`。
