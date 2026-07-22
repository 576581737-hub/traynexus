<div align="center">

# TrayNexus

**Windows 系统托盘资源管家** —— 单文件 exe，零依赖，常驻后台帮你管好内存、电池、屏幕亮度与硬件诊断。

[![License](https://img.shields.io/github/license/576581737-hub/traynexus)](LICENSE)
[![Latest Release](https://img.shields.io/github/v/release/576581737-hub/traynexus)](https://github.com/576581737-hub/traynexus/releases)
[![Build](https://github.com/576581737-hub/traynexus/actions/workflows/build.yml/badge.svg)](https://github.com/576581737-hub/traynexus/actions)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](https://github.com/576581737-hub/traynexus)
[![.NET](https://img.shields.io/badge/.NET-Framework%204.x-512BD4)](https://dotnet.microsoft.com)
[![Size](https://img.shields.io/badge/size-%3C%20350%20KB-green)](https://github.com/576581737-hub/traynexus/releases)

*内存释放 · 电池保养 · 亮度管理 · 功能诊断*

</div>

---

![TrayNexus Preview](docs/preview.svg)

## 这是什么

TrayNexus 把一个 Windows 用户最常需要的几件「系统维护小事」收进一个常驻托盘的小工具里：

- **内存释放** —— 清理 Standby 备用列表、回收工作集，支持阈值自动触发，让老机器/小内存笔记本更跟手。
- **电池保养** —— 对接联想、华硕等厂商的充电阈值接口，把电池长期保持在 60% 保养档，显著延缓老化。
- **亮度管理** —— 直接读写内置屏幕亮度，支持环境光自动调节。
- **功能诊断** —— 一键检测各项功能依赖是否就绪，缺驱动就给出安装引导。

它**不依赖任何外部运行时**：不需要 .NET 6/7/8 SDK，不需要 WebView2，不需要 Visual Studio。系统自带的 .NET Framework 4.x 就够了，编译产物是**一个约 300 KB 的 exe**。

> 由 MemTrayCN 演进而来，本版完成品牌重塑（双环闪电 Logo）与全新 UI（线条彩色图标 + 圆角卡片布局）。

## 功能亮点

| 能力 | 说明 |
|------|------|
| 托盘速览 | 双环图标实时绘制内存占用百分比；左键呼出悬浮速览面板 |
| 内存清理引擎 | StandbyList + WorkingSet 组合释放，可设阈值自动触发（60 秒冷却），含进程白名单保护 |
| 电池信息采集 | 设计容量 / 满充容量 / 循环次数 / 健康度，WMI 不可用时用 `powercfg` 报告兜底 |
| OEM 充电控制 | 联想 `/Management` 三档模式；华硕任意阈值写入；Dell/HP 规划中 |
| 亮度控制 | WMI 读写内置屏亮度，环境光自动调节（无传感器时优雅降级） |
| 功能诊断面板 | 4 张可折叠卡片，检测依赖状态并给出安装引导 |
| 计划任务 | 夜间自动切保养档、周末自动满充，由内置定时器驱动 |
| 开机自启 | 通过系统任务计划程序注册，无残留注册表项 |

## 快速开始

### 下载安装（推荐普通用户）

前往 **[Releases](https://github.com/576581737-hub/traynexus/releases)** 下载：

- **安装版** `Traynexus-Setup-*.exe` —— 标准安装，含开始菜单 / 桌面快捷方式 / 卸载程序
- **便携版** `Traynexus-Portable-*.zip` —— 解压即用，无需安装

> 首次运行建议**右键「以管理员身份运行」**：内存 StandbyList 清理需要提权，否则清理能力会受限。

### 自己构建（开发者）

项目用系统自带的 `csc.exe` 直接编译，**不依赖任何装好的开发工具**：

```bat
build.bat          :: 发布构建，生成 bin\Traynexus.exe
build_debug.bat    :: 调试构建，含 /debug 符号
```

构建日志写入 `build_log.txt`。每次 push 也会由 GitHub Actions 自动构建，可在 **Actions → Artifacts** 取到最新 exe。

### 基本用法

1. 以管理员身份启动 `Traynexus.exe`
2. 托盘常驻：**左键**呼出速览面板，**右键**打开菜单（控制台 / 退出）
3. 控制台：托盘右键 → 控制台，或双击托盘图标
4. 充电控制需先安装对应厂商的电源管理驱动（见下）

## OEM 充电支持矩阵

| 品牌 | 控制方式 | 状态 | 备注 |
|------|----------|------|------|
| **联想** | `\\.\EnergyDrv` + IOCTL `0x831020F8` | ✅ 完整 | 三档：保养 60% / 正常 100% / 快充。**只需安装约 5 MB 的 Lenovo Energy Management 驱动，无需 500+ MB 的 Vantage** |
| **华硕** | `\\.\ATKACPI` + IOCTL `0x0022240C` | 🟡 部分 | 40–100% 任意阈值写入已支持；充电上限读取待真机验证 |
| **Dell / HP** | — | ⏳ 规划中 | 欢迎认领 |

充电状态用 `ChargeRate` 精确判定：保养模式下 `ChargeRate=0`，界面显示「保养中 · 暂停充电」。

## 技术架构

- **宿主**：C# / .NET Framework 4.x（系统自带，无需安装运行时）
- **UI**：纯 WinForms + GDI+ 自绘，全部控件手写（圆角卡片、环形图、滑块、折叠诊断卡……），无第三方 UI 库
- **图标**：lucide 风格路径在运行时按主题色描边渲染，托盘图标用双环动态绘制内存百分比
- **权限**：`requireAdministrator`（StandbyList 清理需提权）
- **依赖**：零外部 DLL，单 exe 分发（约 300 KB）

### 目录结构

```
Traynexus/
├── src/Traynexus/          # C# 源码（全部参与编译）
│   ├── Program.cs          # 入口
│   ├── TrayContext.cs      # 托盘生命周期：图标 · 菜单 · 定时器 · 采集 · 计划任务
│   ├── IconRenderer.cs     # 双环托盘图标 GDI+ 渲染
│   ├── MainForm.cs         # 主控制台（含所有自绘控件与诊断页）
│   ├── QuickForm.cs        # 左键速览悬浮窗
│   ├── MemoryCleaner.cs    # 内存清理引擎
│   ├── BatteryInfo.cs      # 电池采集（WMI + powercfg 兜底）
│   ├── BrightnessController.cs  # 亮度控制
│   ├── OemChargeController.cs   # OEM 充电阈值（联想 / 华硕 IOCTL）
│   ├── LightSensorReader.cs     # 环境光传感器读取（WinRT）
│   ├── UpdateChecker.cs         # 更新检查（GitHub Releases）
│   ├── Settings.cs / Fonts.cs / AutoStartManager.cs / NativeMethods.cs / app.manifest
├── resources/tray_default.ico   # 多尺寸品牌图标（安装包/卸载/快捷方式统一使用）
├── installer/Traynexus.iss      # Inno Setup 安装脚本
├── build.bat / build_debug.bat / build.rsp   # 构建脚本
├── .github/workflows/build.yml  # CI 自动构建
├── LICENSE · README.md · CHANGELOG.md
```

> `bin/`、`lib/` 等构建产物与内部目录已被 `.gitignore` 排除，不纳入仓库。

## 贡献

欢迎 Issue 与 PR。

- 提 Bug 请附：**Windows 版本 + .NET Framework 版本 + 复现步骤 + `error.log`（若有）**
- 代码风格：保持纯 WinForms + GDI+ 自绘路线，不引入外部运行时依赖
- 当前开放任务：**华硕充电上限读取**、**Dell/HP 充电支持**

## 许可证

[MIT](LICENSE) © 2026 Aiyow · 由 MemTrayCN 演进而来

---

## English

**TrayNexus** is a single-file (~300 KB), zero-dependency Windows system-tray toolkit that quietly handles four everyday maintenance jobs:

- **Memory cleanup** — StandbyList + WorkingSet trimming with threshold auto-release.
- **Battery care** — OEM charge-threshold control (Lenovo fully supported, ASUS partial, Dell/HP planned) to keep the battery at a healthy 60%.
- **Brightness** — built-in display brightness via WMI, with ambient-light auto adjustment.
- **Diagnostics** — collapsible cards that probe each feature's driver/WMI/service dependencies and guide installation.

It uses only the .NET Framework 4.x that ships with Windows — no SDK, no WebView2, no Visual Studio required. Build with the bundled `build.bat` (system `csc.exe`). Released under the MIT license. See [Releases](https://github.com/576581737-hub/traynexus/releases) for install/portable packages.
