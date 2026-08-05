# 🐳 DeepSeek 余额监视器

一个可爱又低调的**桌面悬浮小窗**，实时显示你的 DeepSeek API 账户余额。
放在屏幕角落当小挂件，不占任务栏，随时瞄一眼就知道还有多少钱。

## ✨ 功能

- 🪟 **悬浮小窗**：无边框、透明圆角、马卡龙渐变配色，始终置顶
- 🐳 **贴边收纳**：点 `–` 按钮或拖到屏幕左右侧边，小窗会缩成半露出的鲸鱼精灵球；鼠标靠近自动探出
- �🖱 **随处拖动**：按住小窗任意位置即可拖动
- 🔑 **自定义 API Key**：右键菜单随时修改
- ⏱ **自动刷新**：默认每分钟更新一次，也可选 30 秒 / 1 / 5 / 15 分钟
- 💡 **低余额提醒**：余额低于阈值时，数字会变成珊瑚色提示你
- 📊 同时显示：可用余额、充值余额、赠送余额、最近更新时间

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
| 拖到屏幕左、右侧边松手 | 自动收成鲸鱼精灵球 |
| 点 `–` 按钮 / 右键选「收进屏幕侧边」 | 直接收进最近的左、右侧边 |
| 鼠标靠近边缘的鲸鱼精灵球 | 小窗自动滑出查看 |
| 鼠标离开探出的小窗 | 0.7 秒后自动收回侧边 |
| 右键点击 | 弹出菜单（刷新 / 设置 Key / 收进侧边 / 刷新间隔 / 退出） |
| 点头部的 ↻ | 立即刷新余额 |
| 首次打开 | 自动引导你设置 API Key |

## 📁 项目结构

```
deepseek余额监视器/
├── main.py          # 程序入口
├── widget.py        # 悬浮窗界面（透明圆角 + 渐变 + 右键菜单）
├── api.py           # DeepSeek 余额接口调用
├── config.py        # 本地配置读写
├── config.json      # 运行后自动生成（存你的 API Key，别提交到 git）
└── requirements.txt # 依赖列表
```

## ⚠️ 注意

- `config.json` 会以**明文**保存你的 API Key，请勿把该文件分享或提交到公开仓库（已在 `.gitignore` 中忽略）。
- 余额数据来自 DeepSeek 官方接口 `GET /user/balance`。
