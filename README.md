# TrayNexus

> Windows 系统托盘一站式资源管家：内存释放 · 电池保养 · 亮度管理 · 功能诊断。

由 MemTrayCN v1.1 演进而来，融合品牌重塑（双环闪电 Logo）与全新 UI（纯 WinForms 自绘、线条彩色图标、圆角卡片布局）。

---

## 当前状态

**已交付**：托盘常驻 · 内存释放 · 电池保养（OEM 充电控制）· 亮度控制 · 功能诊断面板 · 主控制台（概览 / 内存释放 / 充电管理 / 亮度 / 诊断 / 设置 / 关于）· 品牌视觉体系

**技术路线**：纯 WinForms + GDI+ 自绘，零外部 DLL 依赖，单 exe 即可运行。

| 模块 | 状态 | 备注 |
|---|---|---|
| 托盘图标（双环渲染） | ✅ | `IconRenderer` 动态绘制内存百分比 |
| 内存清理引擎 | ✅ | `MemoryCleaner`（StandbyList + WorkingSet + 阈值自动释放） |
| 电池信息采集 | ✅ | `BatteryInfo`（WMI + powercfg HTML 兜底，含设计容量/循环次数/健康度/温度） |
| OEM 充电控制 | ✅ | Lenovo `\\.\EnergyDrv` IOCTL（保养/正常/快充三档）+ ASUS `\\.\ATKACPI` IOCTL（40-100% 任意阈值） |
| 亮度控制 | ✅ | `BrightnessController` WMI `WmiMonitorBrightnessMethods` 读写内置屏亮度 |
| 主控制台 UI | ✅ | 单 exe WinForms + GDI+ 自绘 |
| 功能诊断面板 | ✅ | 4 张可折叠卡片，检测各功能依赖状态 + 安装引导 |
| 图标系统（自绘 lucide） | ✅ | `DrawIcon`：Grid/Memory/Battery/Sun/Cog/Activity/Flash |
| 计划任务 | ⏳ | 设置页 UI + 持久化已就位，定时执行逻辑未接入 |

---

## 技术栈

- **宿主**：C# .NET Framework 4.x（系统自带，无需安装运行时）
- **UI 层**：纯 WinForms + GDI+ 自绘（`RoundedCard` / `IconBox` / `RingChart` / `BarStrip` / `NavButton` / `ModeCard` / `RoundSlider` / `FlatBorderedButton` / `CollapsibleDiagCard`）
- **图标**：`NavIcon` 枚举 + `DrawIcon()` 静态方法，运行时按 accent 色描边渲染 lucide 风格图标
- **窗口**：`FixedSingle` + `WS_MAXIMIZEBOX` 位删除（`OnHandleCreated` 里 `SetWindowLong`），无最大化按钮
- **权限**：`requireAdministrator`（StandbyList 清理需要 `SeProfileSingleProcessPrivilege`）
- **依赖**：**零外部 DLL**，单 exe 即可分发（约 298 KB）

---

## 目录结构

```
Traynexus/
├── src/
│   └── Traynexus/                 # C# 源码（全部参与编译）
│       ├── Program.cs             # 入口
│       ├── TrayContext.cs         # 托盘生命周期（图标 · 菜单 · 定时器 · 电池采集）
│       ├── IconRenderer.cs        # 双环托盘图标 GDI+ 渲染 + LogoLoader
│       ├── MainForm.cs            # 主控制台（含所有自绘控件 + 诊断页）
│       ├── QuickForm.cs           # 左键速览悬浮窗（Layered Window）
│       ├── ReleasePanel.cs        # 独立"内存释放"面板
│       ├── MemoryCleaner.cs       # 内存清理引擎（StandbyList + WorkingSet）
│       ├── MemoryInfo.cs          # 内存快照采集
│       ├── BatteryInfo.cs         # 电池状态采集（WMI + powercfg HTML 兜底）
│       ├── BrightnessController.cs # 亮度控制（WMI WmiMonitorBrightnessMethods）
│       ├── OemChargeController.cs # OEM 充电阈值控制（Lenovo IOCTL + ASUS IOCTL）
│       ├── NativeMethods.cs       # P/Invoke 声明（CreateFileW/DeviceIoControl 等）
│       ├── Settings.cs            # settings.ini + whitelist.txt 读写
│       ├── ConfigMigrator.cs      # MemTrayCN -> Traynexus 配置迁移
│       ├── AutoStartManager.cs    # 开机自启
│       └── app.manifest           # requireAdministrator + DPI-aware
├── resources/
│   └── tray_default.ico           # 多尺寸品牌 ico（16/32/48/64/128/256）
├── logo_128/256/512.png           # 内嵌资源（供 LogoLoader）
├── github_icon.png / _white.png   # 关于页 GitHub 行图标
├── deliverables/                  # 规划文档（PRD / 架构）
├── docs/                          # 工程文档
├── bin/                           # 构建产物（单 exe ≈ 298 KB）
├── _attic/                        # 归档：不参与编译的历史文件（见其内 README）
├── build.bat                      # 发布构建
├── build_debug.bat                # 调试构建
├── build.rsp                      # csc.exe 响应文件（Git Bash 编译用）
├── README.md
└── CHANGELOG.md
```

---

## 功能详解

### 内存释放
- **释放方式**：StandbyList 清理 + WorkingSet 削减 + 组合释放
- **阈值自动释放**：内存达设定百分比自动触发，60 秒冷却
- **白名单**：持久化 + 会话临时保护，内置系统关键进程硬保护
- **进程列表**：复选切换保护、过滤、排序、干跑预览

### 电池保养
- **OEM 充电控制**：
  - Lenovo：`\\.\EnergyDrv` + IOCTL `0x831020F8`，三档模式（保养 60% / 正常 100% / 快充）
    - 只需安装 Lenovo Energy Management 驱动（5MB），不需要 Vantage（557MB）
  - ASUS：`\\.\ATKACPI` + IOCTL `0x0022240C`，40-100% 任意阈值
  - Dell/HP：开发中
- **电池健康**：设计容量 / 满充容量 / 循环次数 / 健康度百分比（powercfg HTML 报告兜底）
- **健康报告**：弹窗显示中文电池详细信息（系统信息 + 电池信息 + 容量与健康）
- **充电状态**：用 `ChargeRate` 精确判断（保养模式下 `ChargeRate=0`，显示「保养中·暂停充电」）

### 亮度控制
- **内置屏**：WMI `WmiMonitorBrightnessMethods` 读写亮度
- **显示器枚举**：动态检测显示器数量和亮度
- **滑块实时调节**：拖动即调 `SetBrightness()`
- **自动亮度开关**：持久化到 Settings

### 功能诊断
- **4 张可折叠卡片**：内存管理 / 电池信息 / OEM 充电控制 / 亮度控制
- **检测内容**：各功能依赖的驱动/WMI/服务是否就绪
- **安装引导**：未就绪时弹窗显示安装步骤 + 跳转厂商驱动下载页
- **重新检测**：清缓存 + 重建所有卡片

### 设置
- **通用**：开机自启（schtasks）
- **内存**：释放方式 / 阈值触发 / 白名单编辑 / 配置文件夹
- **计划**：夜间保养 / 周末满充 / 会议免扰（持久化已就位，定时执行待接入）

---

## 视觉体系

### 主题色
| 名称 | 值 | 用途 |
|---|---|---|
| `CBlue`   | `#0A7AFF` | 概览 · 亮度卡 · 选中态 |
| `CGreen`  | `#34C759` | 内存卡（图标 + 进度条） |
| `battAmber` | `#FFB020` | 电池卡 · "主屏幕"标注 |
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
- **`RoundedCard`**：双层同心 `FillPath` 描边（外 border · 内 body），无 AA halo/双线/四角深斑
- **`CollapsibleDiagCard`**：可折叠诊断卡片（点标题行展开/收起，▶/▼ 箭头 + 汇总状态标签）
- **`ModeCard`**：充电模式卡（3 选 1，含大百分比 + 选中勾 + 灰显态）
- **`RingChart`**：环形百分比图
- **`RoundSlider`**：全自绘滑块（替代 Win32 TrackBar，防止穿透卡片边框）
- **`TagLabel`**：彩色药丸状态标签
- **`TransparentPanel`**：支持子 Label 透明背景的 Panel

---

## 构建

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
Windows csc 参数含 `\` 会被 MSYS 转义，用响应文件：
```bash
"C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @build.rsp
```

**产物**：`bin/Traynexus.exe`（单文件约 298 KB，无外部依赖）

---

## 运行

1. **首次运行**：右键以管理员身份启动（StandbyList 清理需管理员权限）
2. **托盘常驻**：左键呼出速览面板，右键呼出菜单（控制台 / 退出）
3. **控制台**：托盘右键 -> 控制台，或双击托盘图标
4. **充电控制**：需安装对应厂商的电源管理驱动（Lenovo 装 Energy Management 5MB 即可）

---

## 更新日志

见 [`CHANGELOG.md`](CHANGELOG.md)

---

## 设计文档

- [产品需求文档 PRD](deliverables/traynexus-merge-prd.md)
- [架构设计](deliverables/traynexus-merge-architecture.md)
- [实现缺口清单](docs/implementation-gap.md)

---

© 2026 Aiyow · 由 MemTrayCN 演进而来
