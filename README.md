# MixDeck

一个 Windows 音量管理小工具，使用 C#、.NET 10 和 WPF 开发。

目前可以调节系统和各应用的音量、切换静音、选择要管理的输出设备，并实时同步音量变化。关闭窗口后会留在托盘运行，可从托盘菜单退出。

项目还在开发中，配置保存和自动恢复尚未实现，也暂时没有安装包。目前主要在 Windows 11 上测试。

## 运行

需要 Windows 和 .NET 10 SDK（当前使用 10.0.204）。在项目根目录执行：

```powershell
dotnet run --project src/AudioManager.Desktop
```

工程内部暂时沿用 `AudioManager` 命名。开发进度见 [docs/STATUS.md](docs/STATUS.md)。
