# PixJoin

[![Release](https://github.com/Enlpot/PixJoin/actions/workflows/release.yml/badge.svg)](https://github.com/Enlpot/PixJoin/actions/workflows/release.yml)
[![Version](https://img.shields.io/github/v/release/Enlpot/PixJoin)](https://github.com/Enlpot/PixJoin/releases)
[![License](https://img.shields.io/github/license/Enlpot/PixJoin)](https://github.com/Enlpot/PixJoin/blob/master/LICENSE)
![.NET](https://img.shields.io/badge/.NET-8-blue)
![Platform](https://img.shields.io/badge/Windows-10%2F11-blue)

Windows 截图贴图工具：截图 → 钉在桌面（贴图）→ 贴图相互靠近自动吸附组合成一张，一键导出或复制为单张图片。

## 功能特性

- **截图贴图**：全局快捷键（默认 `Ctrl+Shift+A`）截图，选区后立即成为桌面贴图
- **吸附组合（Join）**：贴图相互靠近自动吸附、对齐并组合成一张；按住保护键（默认 `Ctrl`）拖出即可拆分，防止误拆
- **像素级放大镜**：截图时提供放大视图与像素网格，精确取点
- **边缘对齐吸附**：拖动 / 缩放贴图时，边缘自动与其它贴图对齐（上 / 下 / 左 / 右），缩放时以吸附边为锚点
- **贴图交互**：双击关闭、滚轮缩放、`ESC` 关闭（首次使用弹窗确认，可持久化）
- **文字识别（OCR）**：贴图后自动识别图中文字（Windows 系统引擎，离线零依赖）——光标悬停文字显示竖条（IBeam），左键拖动按词选择（跨行连续），选中后浮动「复制」按钮；`Shift+C` 复制整张贴图全部文字；右键可关单张贴图的「文本可选择」
- **贴图管理**：隐藏其他贴图、贴图鼠标穿透、锁定（禁止拖动 / 缩放 / 关闭）、`Alt` 按住强制移动（跳过文字选择）
- **自由拆分**：组合内贴图可独立移出，重新吸附又可组合
- **导出**：支持透明背景、自动裁剪空白边缘，导出或复制为单张图片（保持原图清晰度）
- **完全可配置**：快捷键、吸附阈值、拆分保护键、透明度、导出选项等均可在设置窗口调整
- **开机自启**（可选）

## 下载与安装

到 [Releases](https://github.com/Enlpot/PixJoin/releases) 下载最新版，两种版本二选一：

| 版本 | 体积 | 说明 |
| --- | --- | --- |
| `PixJoin-*-win-x64.zip`（自包含） | ~60MB | 内置 .NET 运行时，**解压双击即用**，无需安装任何东西，推荐 |
| `PixJoin-*-win-x64-fx.zip`（框架依赖） | ~120KB | 体积小，需已安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

> 每次推 `v*` 标签（如 `v0.2.0`）时，GitHub Actions 会自动构建并同时发布以上两个版本，附 SHA256 校验文件。

## 使用

解压后双击 `PixJoin.exe` 启动（驻留托盘）。

| 操作 | 按键 |
| --- | --- |
| 开始截图 | `Ctrl+Shift+A`（可在设置中修改，也可双击托盘图标） |
| 拆分组合中的贴图 | 按住 `Ctrl` 拖出（保护键，可在设置中改） |
| 关闭贴图 | 双击贴图 |
| 缩放贴图 | 滚轮（靠近贴图边缘时以吸附边为锚点） |
| 调整透明度 | `Alt` + 滚轮 |
| 关闭悬停贴图 | `ESC` |
| 复制整张贴图文字 | `Shift+C`（鼠标悬停在该贴图上） |
| 强制移动贴图 | 按住 `Alt` 拖动（跳过文字选择） |
| 导出 / 复制为单张图片 | 右键贴图 |
| 设置 | 右键托盘 → 设置… |

## 技术栈

- C# / .NET 8 / WPF，**无第三方依赖**；文字识别使用 Windows 系统内置引擎（Windows.Media.Ocr，需 Win10 1809+ 及对应语言包）
- 分层架构：
  - `PixJoin.Core` — 纯逻辑层（吸附 / 组合 / 导出 / 设置），可单元测试
  - `PixJoin.App` — WPF 界面层（截图遮罩、贴图窗口、托盘、设置）
  - `PixJoin.Core.Tests` — 单元测试（控制台，无需测试框架）

## 构建与运行

```bash
# 构建
dotnet build PixJoin.sln -c Release

# 运行
src/PixJoin.App/bin/Release/net8.0-windows/PixJoin.exe

# 无界面自检（抓屏 → 裁切 → 组合 → 导出）
src/PixJoin.App/bin/Release/net8.0-windows/PixJoin.exe --selftest

# 单元测试
dotnet run --project tests/PixJoin.Core.Tests
```

## 自动化发布

推送符合 `v*` 的标签即可触发 GitHub Actions 自动发布（构建 → 单元测试 → 打包两个版本 → 生成 Release）：

```bash
git tag v0.2.0
git push origin v0.2.0
```

## 项目结构

```
src/PixJoin.Core/       纯逻辑层：吸附引擎、组合管理、导出、设置
src/PixJoin.App/        WPF 界面层：截图遮罩、贴图窗口、托盘、设置窗口
tests/PixJoin.Core.Tests  单元测试（控制台）
.github/workflows/      GitHub Actions 自动发布流水线
```

## 许可

[MIT](https://github.com/Enlpot/PixJoin/blob/master/LICENSE)
