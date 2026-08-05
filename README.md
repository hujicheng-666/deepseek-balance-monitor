# 🐳 DeepSeek

一个轻量的 **DeepSeek 桌面悬浮窗**，用于查看 API 余额和官方服务事件。
它始终置顶、不占任务栏，可收纳到屏幕左右侧边。

## ✨ 功能

- 无边框、置顶、石墨玻璃质感的紧凑悬浮窗
- 自动刷新余额，支持 30 秒 / 1 / 5 / 15 分钟间隔
- 拖动或点击收纳按钮后，可缩为侧边鲸鱼球；悬停自动展开
- 余额页与服务事件页在同一卡片内切换，不改变窗口尺寸
- 服务事件页从 DeepSeek 官方 Statuspage 读取近期事件，并以可滚动时间线展示
- 右键菜单可设置 API Key、刷新间隔、低余额提醒和退出
- 关闭时提供粒子消散动效

## 📦 安装

```powershell
pip install PySide6 requests
```

## 🚀 运行

```powershell
python main.py
```

首次运行会提示输入 API Key。也可以将 `config.example.json` 复制为
`config.json` 后再填写；请勿提交 `config.json`，其中可能包含你的密钥。

> 提示：想让悬浮窗开机自启，可以把这个命令放进「启动」文件夹：
> `Win+R` 输入 `shell:startup`，在里面放一个 `start.bat`，内容写上
> `python D:\vscode\deepseek余额监视器\main.py` 即可。

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
├── main.py          # 程序入口
├── widget.py        # 悬浮窗、侧边收纳与服务事件时间线
├── api.py           # 余额与官方服务状态接口调用
├── config.py        # 本地配置读写
├── config.json      # 运行后自动生成（存你的 API Key，别提交到 git）
└── requirements.txt # 依赖列表
```

## ⚠️ 注意

- `config.json` 会以**明文**保存你的 API Key，请勿把该文件分享或提交到公开仓库（已在 `.gitignore` 中忽略）。
- 余额数据来自 DeepSeek 官方接口 `GET /user/balance`。
- 服务事件数据来自 [DeepSeek Service Status](https://status.deepseek.com/)。
