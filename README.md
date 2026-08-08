# 🐳 DeepSeek

一个轻量的 **DeepSeek 桌面悬浮窗**（C# / WPF），用于查看 API 余额和官方服务事件。
它始终置顶、不占任务栏，可收纳到屏幕左右侧边。

## ✨ 功能

- 无边框、置顶、石墨玻璃质感的紧凑悬浮窗
- 自动刷新余额，支持 30 秒 / 1 / 5 / 15 分钟间隔
- 拖动或点击收纳按钮后，可缩为侧边鲸鱼球；悬停自动展开
- 余额页与服务事件页在同一卡片内切换，不改变窗口尺寸
- 服务事件页读取 DeepSeek 官方状态页近期事件，以可滚动时间线展示
- 右键菜单可设置 API Key、刷新间隔、低余额提醒和退出
- 关闭时提供粒子消散动效

## 🚀 运行（开发模式）

需要 **.NET 8 SDK**：

```powershell
dotnet run --project .\DeepSeekMonitor\DeepSeekMonitor.csproj
```

或先构建再运行：

```powershell
dotnet build .\DeepSeekMonitor\DeepSeekMonitor.csproj -c Release
.\DeepSeekMonitor\bin\Release\net8.0-windows\DeepSeekMonitor.exe
```

首次运行会提示输入 API Key。也可以将 `config.example.json` 复制为
`config.json` 后再填写；请勿提交 `config.json`，其中可能包含你的密钥。

> 提示：想让悬浮窗开机自启，可以把这个命令放进「启动」文件夹：
> `Win+R` 输入 `shell:startup`，在里面放一个 `start.bat`，内容写上
> `start "" "D:\vscode\deepseek余额监视器\DeepSeekMonitor\bin\Release\net8.0-windows\DeepSeekMonitor.exe"` 即可。

## 🔑 获取 API Key

1. 登录 [platform.deepseek.com](https://platform.deepseek.com)
2. 左侧菜单 → **API Keys** → **创建 API Key**
3. 复制 `sk-` 开头的密钥，粘贴进悬浮窗的输入框

## 🎨 使用说明

| 操作 | 效果 |
| --- | --- |
| 按住左键拖动 | 移动小窗位置 |
| 拖到屏幕左、右侧边松手 | 自动收成侧边鲸鱼球 |
| 点 `–` 按钮 / 右键选「收进屏幕侧边」 | 直接收进最近的左、右侧边 |
| 鼠标靠近边缘的鲸鱼精灵球 | 小窗自动滑出查看 |
| 鼠标离开探出的小窗 | 0.7 秒后自动收回侧边 |
| 点击头部的 `◉` | 切换服务事件时间线；再次点击返回余额页 |
| 在事件时间线上滚动 | 中心事件清晰、上下事件淡出，停止后自动对齐 |
| 右键点击 | 弹出菜单（刷新 / 设置 Key / 收进侧边 / 刷新间隔 / 退出） |
| 点头部的 ↻ | 立即刷新余额 |
| 首次打开 | 自动引导你设置 API Key |

## 📁 项目结构

```
deepseek-balance-monitor/
├── DeepSeekMonitor/        # C# / WPF 工程（唯一版本）
│   ├── MainWindow.xaml(.cs)   # 悬浮窗、贴边收纳与服务事件时间线
│   ├── InputDialog.xaml(.cs)  # API Key 输入框
│   ├── Services/
│   │   ├── DeepSeekApi.cs      # 余额与官方服务状态接口调用
│   │   └── AppConfig.cs        # 本地配置读写（config.json）
│   ├── Models/                 # BalanceInfo / ServiceEvent
│   ├── TimelineWheelOverlay.cs # 时间线滚轮渲染
│   └── ParticleDismiss.cs      # 粒子消散动效
├── build.ps1             # 一键打包（publish + Inno Setup）
├── installer.iss         # Inno Setup 安装脚本
├── config.example.json   # 配置模板
└── config.json           # 运行后生成（存你的 API Key，别提交到 git）
```

## ⚠️ 注意

- `config.json` 会以**明文**保存你的 API Key，请勿把该文件分享或提交到公开仓库（已在 `.gitignore` 中忽略）。
- 余额数据来自 DeepSeek 官方接口 `GET /user/balance`。
- 服务事件数据来自 [DeepSeek Service Status](https://status.deepseek.com/)。

## 📦 打包发布（.NET publish + Inno Setup）

一键打包（自包含单文件，无需目标机器安装 .NET 运行时，再生成安装程序）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

> `build.ps1` 会自动找 Inno Setup（`D:\Inno Setup 7\ISCC.exe` 或标准路径，也可用环境变量 `ISCC` 指定）。

产物：
- `dist\DeepSeek\DeepSeekMonitor.exe` — 免安装版（绿色，自包含单文件）
- `release\DeepSeek-Setup-2.0.0.exe` — 安装程序

## 🍎🐧 跨平台版（Avalonia，macOS / Linux）

`DeepSeekMonitor.Avalonia` 是用 **Avalonia** 移植的跨平台版本，功能与 WPF 版基本一致
（悬浮窗、余额刷新、服务时间线、托盘、贴边收纳），业务逻辑（API/配置）复用同一套实现。
WPF 版（Windows）与 Avalonia 版并存，互不影响。

仅生成系统安装包，不再保留便携版、压缩包或 AppImage：

- Windows：在 Windows 上执行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，生成 `release\DeepSeek-Setup-2.0.0.exe`。
- macOS：在 macOS 上执行 `./packaging/make-macos-dmg.sh`，生成 `release/DeepSeek-2.0.0-macos-<arch>.dmg`。
- Linux：在 Linux 上执行 `./packaging/make-linux-packages.sh`，生成 `release/DeepSeek_2.0.0_linux_<arch>.deb` 和 `.rpm`。

也可在对应系统上执行 `powershell -ExecutionPolicy Bypass -File .\build-av.ps1`，它会选择该系统的安装包脚本。

> 说明：
> - macOS DMG 未签名，正式分发前请设置 `MACOS_SIGN_IDENTITY`，并再执行 `notarize`。
> - 托盘图标在 Linux 需要支持 StatusNotifier/AppIndicator 的桌面环境；纯 X11 无托盘也可正常使用。
> - 粒子消散动效在 Avalonia 版中简化为直接关闭。
