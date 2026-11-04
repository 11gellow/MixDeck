# MixDeck v0.1

源码状态说明：`v0.1` 标签固定在 `f09fbbe`。当前开发分支在此基础上合入待实机验收的自启动优化候选；下方便携版说明对应原 v0.1，不能将后续源码构建冒充该发布包。启动对比步骤见 `docs/STARTUP_VALIDATION.md`。

Windows 音量管理小工具，使用 C#、.NET 10 和 WPF 开发。当前版本为首个可用成品，后续按需求和问题维护。

支持系统与应用音量、静音、输出设备切换、按设备记忆并自动恢复音量、托盘运行和登录自启动。界面包含“音量管理”“高级选项”“系统日志”，支持浅色、深色和跟随系统主题。已记忆设置默认收起，收起时自动保存和恢复继续生效。关闭窗口时可选择退出或最小化到托盘，并记住选择；可在高级选项修改。

## 使用便携版

1. 将 `MixDeck-v0.1-win-x64.zip` **完整解压**到固定目录，无需安装 .NET。
2. 从资源管理器运行目录内的 `AudioManager.Desktop.exe`（工程内部保留原命名）。
3. 登录自启动默认关闭，需要时在高级选项开启。开启后请保持目录固定；移动目录后重新关闭、开启自启动。

配置保存在 `%LOCALAPPDATA%\MixDeck`，不在便携目录。更新时先退出程序，将新版解压到原目录，保留 AppData 中的设置；更换版本前可自行备份该配置目录。分发包不包含开发者的配置、音频现场日志或测试素材。

当前主要在 Windows 11 x64 上验证。Windows 10、不同 DPI、更多硬件和长期驻留尚未全面验收；既有滑条 lifecycle 自动化用例存在间歇失败，未声明修复。详情见源码的 `docs/releases/v0.1.md`（便携包内为 `RELEASE-NOTES.md`）。独立分支的自启动优化候选方案未包含在本版。

## 开发与打包

需要 Windows 和 .NET 10 SDK（`global.json` 指定 10.0.204，允许补丁更新）。

```powershell
dotnet run --project src/AudioManager.Desktop
dotnet build AudioManager.sln -c Release --warnaserror
powershell -NoProfile -File tools/Package-Release.ps1
```

打包脚本要求干净的 Git 工作区，输出到被忽略的 `artifacts/releases/v0.1`，包含 Windows x64 自包含 ZIP、SHA256 校验及来源清单；不覆盖已有输出。程序保留多文件发布，不裁剪 WPF，也不改变 Windows 启动项。

开发进度见源码的 `docs/STATUS.md`，原开发里程碑编号与成品 v0.1 分开记录。
