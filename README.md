# MixDeck

一个 Windows 音量管理小工具，使用 C#、.NET 10 和 WPF 开发。

目前可以调节系统和各应用的音量、切换静音、选择要管理的输出设备，并实时同步音量变化。关闭窗口后会留在托盘运行，可从托盘菜单退出。

还可以从“方案与自动恢复”保存音量快照、管理多个方案、手动恢复、编辑自动恢复目标，以及配置登录后启动。启用自动恢复后以当前方案为准；系统拒绝修改时显示实际值和无法修改提示。

项目还在开发中，暂无安装包，主要在 Windows 11 上测试。配置默认保存在 `%LOCALAPPDATA%\MixDeck`，自动恢复和登录启动默认关闭；真实重启登录验收正在进行。

## 运行

需要 Windows 和 .NET 10 SDK（当前使用 10.0.204）。在项目根目录执行：

```powershell
dotnet run --project src/AudioManager.Desktop
```

工程内部暂时沿用 `AudioManager` 命名。开发进度见 [docs/STATUS.md](docs/STATUS.md)。

登录自启动验收请从资源管理器直接运行构建生成的 `AudioManager.Desktop.exe`；开发工具的隔离设置可能与正常桌面不同，排查记录见 [配置验收](docs/CONFIGURATION_VALIDATION.md)。
