# MixDeck

一个 Windows 音量管理小工具，使用 C#、.NET 10 和 WPF 开发。

目前可以调节系统和各应用的音量、切换静音、切换 Windows 默认输出设备并自动选择对应音量方案，并实时同步音量变化。关闭窗口后会留在托盘运行，可从托盘菜单退出。

主窗口提供同级“音量管理”和“高级选项”。设备首次使用自动建立方案，已有方案可选择、导入导出和恢复快照；规则与偏好修改后隐式保存，无需保存按钮。数值回车或失焦提交，滑块实时生效、松手后保存。系统拒绝修改时显示实际值和无法修改提示。

项目还在开发中，暂无安装包，主要在 Windows 11 上测试。每个输出设备记住各自方案；前端或系统调节都更新当前设备目标，滑块可直接操作。配置默认保存在 `%LOCALAPPDATA%\MixDeck`。启动、切换设备和新会话出现时自动恢复；登录启动默认关闭；真实重启登录验收正在进行。

## 运行

需要 Windows 和 .NET 10 SDK（当前使用 10.0.204）。在项目根目录执行：

```powershell
dotnet run --project src/AudioManager.Desktop
```

工程内部暂时沿用 `AudioManager` 命名。开发进度见 [docs/STATUS.md](docs/STATUS.md)。

登录自启动验收请从资源管理器直接运行构建生成的 `AudioManager.Desktop.exe`；开发工具的隔离设置可能与正常桌面不同，排查记录见 [配置验收](docs/CONFIGURATION_VALIDATION.md)。
