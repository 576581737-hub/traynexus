# TrayNexus

> Windows 系统托盘一站式资源管家：**内存释放 · 电池保养 · 亮度管理 · 功能诊断**。

纯 WinForms + GDI+ 自绘，**零外部 DLL 依赖**，单 exe 即可运行（约 300 KB）。由 MemTrayCN 演进而来，融合品牌重塑（双环闪电 Logo）与全新 UI（线条彩色图标、圆角卡片布局）。

[English abstract](#english-abstract)

---

## 徽章

![License](https://img.shields.io/github/license/576581737-hub/traynexus)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-Framework%204.x-512BD4)
![Language](https://img.shields.io/github/languages/top/576581737-hub/traynexus)
![Build](https://github.com/576581737-hub/traynexus/actions/workflows/build.yml/badge.svg)
![Version](https://img.shields.io/badge/version-v1.0721.0-blue)
![Size](https://img.shields.io/badge/size-~300%20KB-green)

---

## ✨ 功能特性

| 模块 | 状态 | 说明 |
|---|---|---|
| 托盘图标（双环渲染） | ✅ | `IconRenderer` 动态绘制内存占用百分比 |
| 内存清理引擎 | ✅ | `MemoryCleaner`（StandbyList + WorkingSet + 阈值自动释放） |
| 电池信息采集 | ✅ | `BatteryInfo`（WMI + powercfg HTML 兜底：设计容量 / 循环次数 / 健康度 / 温度） |
| OEM 充电控制 | 🟡 | Lenovo `\\.\EnergyDrv` IOCTL 三档模式；ASUS `\\.\ATKACPI` IOCTL（读取待完善）；Dell/HP 规划中 |
| 亮度控制 | ✅ | `BrightnessController` WMI `WmiMonitorBrightnessMethods` 读写内置屏亮度 |
| 主控制台 UI | ✅ | 单 exe WinForms + GDI+ 自绘（概览 / 内存释放 / 充电管理 / 亮度 / 诊断 / 设置 / 关于） |
| 功能诊断面板 | ✅ | 4 张可折叠卡片，检测各功能依赖状态 + 安装引导 |
| 图标系统（自绘 lucide） | ✅ | `DrawIcon`：Grid / Memory / Battery / Sun / Cog / Activity / Flash |
| 计划任务 | ✅ | 夜间保养 + 周末满充已接入（`_scheduleTimer` 定时触发 `CheckSchedule`） |

图例：✅ 已完成 · 🟡 部分完成 · ⏳ 规划中

---

## 📋 系统要求

- **操作系统**：Windows 10 / 11（项目使用 Win 10+ 的 WMI 类，不支持 Win 7/8/8.1）
- **运行时**：.NET Framework 4.x（Windows 自带，无需另行安装）
- **权限**：首次运行建议**右键「以管理员身份运行」**——内存 StandbyList 清理需要 `SeProfileSingleProcessPrivilege`
- **依赖**：无。不需要 Visual Studio、不需要 dotnet SDK、不需要 WebView2

---

## 🚀 快速开始

### 方式一：下载安装（普通用户）

前往 **[Releases](https://github.com/576581737-hub/traynexus/releases)** 下载：

- **安装版** `Traynexus-Setup-*.exe`：标准安装（开始菜单 / 桌面快捷方式 / 卸载程序）
- **便携版** `Traynexus-Portable-*.zip`：解压即用，无需安装

### 方式二：自己构建（开发者，最简单）

```bat
:: 发布构建（生成 bin\Traynexus.exe）
build.bat

:: 调试构建（含 /debug，便于排查）
build_debug.bat
```

构建脚本调用系统自带的 `csc.exe`，**不依赖任何安装过的开发工具**。构建日志同时写入 `build_log.txt`。

### 方式三：从 CI 制品获取

每次 push 都会通过 GitHub Actions 自动构建，可在 **Actions → Build → Artifacts** 下载 `Traynexus.exe`。

### 运行

1. 右键以管理员身份启动 `Traynexus.exe`
2. 托盘常驻：左键呼出速览面板，右键呼出菜单（控制台 / 退出）
3. 控制台：托盘右键 → 控制台，或双击托盘图标
4. 充电控制：需安装对应厂商的电源管理驱动（见下方「OEM 充电支持矩阵」）

---

## 🔋 OEM 充电支持矩阵

| 品牌 | 控制方式 | 状态 | 备注 |
|---|---|---|---|
| **Lenovo** | `\\.\EnergyDrv` + IOCTL `0x831020F8` | ✅ | 三档模式：保养 60% / 正常 100% / 快充。**只需安装 Lenovo Energy Management 驱动（约 5 MB），不需要 Vantage（约 557 MB）** |
| **ASUS** | `\\.\ATKACPI` + IOCTL `0x0022240C` | 🟡 | 40–100% 任意阈值写入已支持；充电上限读取（`QueryAsusLimit`）待真实机型验证 |
| **Dell / HP** | — | ⏳ | 规划中 |

> 充电状态判断使用 `ChargeRate` 精确判定：保养模式下 `ChargeRate=0`，界面显示「保养中 · 暂停充电」。

---

## 🛠 技术栈

- **宿主**：C# .NET Framework 4.x（系统自带，无需安装运行时）
- **UI 层**：纯 WinForms + GDI+ 自绘（`RoundedCard` / `IconBox` / `RingChart` / `BarStrip` / `NavButton` / `ModeCard` / `RoundSlider` / `FlatBorderedButton` / `CollapsibleDiagCard`）
- **图标**：`NavIcon` 枚举 + `DrawIcon()` 静态方法，运行时按 accent 色描边渲染 lucide 风格图标
- **窗口**：`FixedSingle` + 删除 `WS_MAXIMIZEBOX` 位（`OnHandleCreated` 里 `SetWindowLong`），无最大化按钮
- **权限**：`requireAdministrator`（StandbyList 清理需要提权）
- **依赖**：**零外部 DLL**，单 exe 即可分发（约 300 KB）

---

## 📁 目录结构

```
Traynexus/
├── src/
│   └── Traynexus/             # C# 源码（全部参与编译）
│       ├── Program.cs             # 入口
│       ├── TrayContext.cs         # 托盘生命周期（图标 · 菜单 · 定时器 · 电池采集 · 计划任务）
│       ├── IconRenderer.cs        # 双环托盘图标 GDI+ 渲染 + LogoLoader
│       ├── MainForm.cs            # 主控制台（含所有自绘控件 + 诊断页）
│       ├── QuickForm.cs           # 左键速览悬浮窗（Layered Window）
│       ├── ReleasePanel.cs        # 独立「内存释放」面板
│       ├── MemoryCleaner.cs       # 内存清理引擎（StandbyList + WorkingSet）
│       ├── MemoryInfo.cs          # 内存快照采集
│       ├── BatteryInfo.cs         # 电池状态采集（WMI + powercfg HTML 兜底）
│       ├── BrightnessController.cs# 亮度控制（WMI WmiMonitorBrightnessMethods）
│       ├── OemChargeController.cs # OEM 充电阈值控制（Lenovo / ASUS IOCTL）
│       ├── NativeMethods.cs       # P/Invoke 声明（CreateFileW / DeviceIoControl 等）
│       ├── Settings.cs            # settings.ini + whitelist.txt 读写
│       ├── ConfigMigrator.cs      # MemTrayCN → Traynexus 配置迁移
│       ├── Fonts.cs               # 静态共享 Font 实例（消除 GDI+ 句柄泄漏）
│       ├── AutoStartManager.cs    # 开机自启（schtasks）
│       └── app.manifest           # requireAdministrator + DPI-aware
├── resources/
│   └── tray_default.ico         # 多尺寸品牌 ico（16/32/48/64/128/256）
├── logo_128.png / logo_256.png  # 内嵌资源（供 LogoLoader）
├── github_icon.png / github_icon_white.png # 关于页 GitHub 行图标
├── docs/                        # 工程文档（预留目录）
├── build.bat                    # 发布构建
├── build_debug.bat              # 调试构建
├── build.rsp                    # csc.exe 响应文件（Git Bash / CI 编译用）
├── installer/
│   └── Traynexus.iss            # Inno Setup 安装脚本（图标统一为 tray_default.ico）
├── .github/workflows/build.yml  # GitHub Actions 自动构建
├── LICENSE                      # MIT
├── README.md
└── CHANGELOG.md
```

> `bin/`（构建产物）、`lib/`、`_attic/`、`deliverables/`、`.zcode/` 等目录已被 `.gitignore` 排除，不纳入仓库。

---

## 🔍 功能详解

### 内存释放
- **释放方式**：StandbyList 清理 + WorkingSet 削减 + 组合释放
- **阈值自动释放**：内存达设定百分比自动触发，60 秒冷却
- **白名单**：持久化 + 会话临时保护，内置系统关键进程硬保护
- **进程列表**：复选切换保护、过滤、排序、干跑预览

### 电池保养
- **OEM 充电控制**：见上方「OEM 充电支持矩阵」
- **电池健康**：设计容量 / 满充容量 / 循环次数 / 健康度百分比（powercfg HTML 报告兜底）
- **健康报告**：弹窗显示中文电池详细信息（系统信息 + 电池信息 + 容量与健康）
- **充电状态**：用 `ChargeRate` 精确判断（保养模式下 `ChargeRate=0`，显示「保养中 · 暂停充电」）

### 亮度控制
- **内置屏**：WMI `WmiMonitorBrightnessMethods` 读写亮度
- **显示器枚举**：动态检测显示器数量和亮度
- **滑块实时调节**：拖动即调 `SetBrightness()`
- **自动亮度开关**：持久化到 Settings

### 功能诊断
- **4 张可折叠卡片**：内存管理 / 电池信息 / OEM 充电控制 / 亮度控制
- **检测内容**：各功能依赖的驱动 / WMI / 服务是否就绪
- **安装引导**：未就绪时弹窗显示安装步骤 + 跳转厂商驱动下载页
- **重新检测**：清缓存 + 重建所有卡片

### 设置
- **通用**：开机自启（schtasks）
- **内存**：释放方式 / 阈值触发 / 白名单编辑 / 配置文件夹
- **计划**：夜间保养 / 周末满充（定时执行已接入，会议免扰规划中）

### 计划任务
- **夜间保养**：到设定时段自动切到保养模式（60%），离开时段恢复
- **周末满充**：周末自动切到正常模式（100%），工作日恢复保养
- 由 `TrayContext._scheduleTimer` 每分钟轮询 `CheckSchedule()` 驱动

---

## 🎨 视觉体系

### 主题色
| 名称 | 值 | 用途 |
|---|---|---|
| `CBlue`   | `#0A7AFF` | 概览 · 亮度卡 · 选中态 |
| `CGreen`  | `#34C759` | 内存卡（图标 + 进度条） |
| `battAmber` | `#FFB020` | 电池卡 · 「主屏幕」标注 |
| `COrange` | `#FF9F0A` | 品牌辅助色 |
| `CPurple` | `#AF52DE` | 电池健康卡 |
| `CInk`    | 深灰 | 主要文字 |
| `CInk2`   | 中灰 | 次要文字 |
| `CPanel`  | 白 | 卡片底 |
| `CPanel2` | `#FAFAFC` | 侧边栏底 |

### 图标（lucide 移植）
`NavIcon` 枚举含：`Grid` · `Memory` · `Battery` · `Sun` · `Cog` · `Activity` · `Flash`
均用 `GraphicsPath` 手写 lucide 24×24 viewBox 路径，运行时按 accent 描边。

### 卡片控件
- **`RoundedCard`**：双层同心 `FillPath` 描边（外 border · 内 body），无 AA halo / 双线 / 四角深斑
- **`CollapsibleDiagCard`**：可折叠诊断卡片（点标题行展开 / 收起，▶/▼ 箭头 + 汇总状态标签）
- **`ModeCard`**：充电模式卡（3 选 1，含大百分比 + 选中勾 + 灰显态）
- **`RingChart`**：环形百分比图
- **`RoundSlider`**：全自绘滑块（替代 Win32 TrackBar，防止穿透卡片边框）
- **`TagLabel`**：彩色药丸状态标签
- **`TransparentPanel`**：支持子 Label 透明背景的 Panel

---

## 🏗 构建（开发者）

依赖（开发机）：
- .NET Framework 4.x（系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）
- **无其他依赖**

```bat
:: 发布构建
build.bat

:: 调试构建
build_debug.bat
```

### Git Bash 下手动构建
Windows 下 csc 参数含 `\` 会被 MSYS 转义，用响应文件：
```bash
"C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @build.rsp
```

**产物**：`bin/Traynexus.exe`（单文件约 300 KB，无外部依赖）

> 注意：`.bat` 文件必须保持 CRLF 换行符（已通过 `.gitattributes` 强制），否则 `cmd.exe` 会解析失败。

---

## 🤝 贡献

欢迎 Issue 与 PR！

- 提 Bug 请附：**Windows 版本 + .NET Framework 版本 + 复现步骤 + `error.log`（若有）**
- 提功能建议请在 Issue 中描述使用场景
- 代码风格：保持现有纯 WinForms + GDI+ 自绘路线，不引入外部运行时依赖
- ASUS 充电上限读取、Dell/HP 支持为当前开放任务，欢迎认领

---

## 📜 许可证

[MIT](LICENSE) © 2026 Aiyow · 由 MemTrayCN 演进而来

---

## English Abstract

**TrayNexus** is a Windows system-tray toolkit that bundles four utilities into a single ~300 KB executable with **zero external dependencies**:

- **Memory cleanup** — StandbyList + WorkingSet trimming with threshold auto-release.
- **Battery care** — OEM charge-threshold control (Lenovo `\\.\EnergyDrv` IOCTL is fully supported; ASUS partial; Dell/HP planned).
- **Brightness control** — built-in display brightness via WMI `WmiMonitorBrightnessMethods`.
- **Diagnostics** — collapsible cards that probe each feature's driver/WMI/service dependencies.
- **Scheduled tasks** — nightly battery care and weekend full-charge, driven by an in-app timer.

Built with plain WinForms + GDI+ (no WebView2, no NuGet runtime packages). Requires .NET Framework 4.x (ships with Windows) and administrator rights for memory trimming. Build with the bundled `build.bat` (uses the system `csc.exe`); no Visual Studio or .NET SDK needed. Licensed under MIT.
