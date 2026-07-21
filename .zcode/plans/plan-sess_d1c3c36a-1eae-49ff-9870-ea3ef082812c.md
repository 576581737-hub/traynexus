# 外接屏 DDC/CI 亮度控制 + 亮度页交互优化 实现方案

## 目标
1. 实现外接屏 DDC/CI 亮度控制（dxva2.dll 物理显示器 API）
2. 优化亮度页交互：区分内置/外接屏，每个显示器独立滑块控制
3. 诊断页外接屏 DDC/CI 从「开发中」改为实际探测

## 改动文件（3 个）

### 1. `NativeMethods.cs` - 新增 DDC/CI P/Invoke 区块
在文件末尾加新区块 `// ==== DDC/CI 物理显示器亮度（dxva2.dll + user32.dll）====`：
- `EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData)` -- user32.dll，枚举显示器
- `GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray)` -- dxva2.dll，获取物理监视器句柄
- `DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray)` -- dxva2.dll，释放句柄
- `GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness)` -- dxva2.dll，读亮度
- `SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness)` -- dxva2.dll，写亮度
- `GetMonitorCapabilities(IntPtr hMonitor, out uint pdwMonitorCapabilities, out uint pdwSupportedColorTemperatures)` -- dxva2.dll，检测 DDC/CI 能力
- `PHYSICAL_MONITOR` 结构体（hPhysicalMonitor + szPhysicalMonitorDescription）
- `MonitorEnumProc` 委托

### 2. `BrightnessController.cs` - 扩展 DDC/CI 支持
**MonitorInfo 扩展字段**：
```
public IntPtr PhysicalHandle;    // DDC/CI 物理监视器句柄（外接屏用）
public bool DdcSupported;        // 是否支持 DDC/CI 亮度控制
```

**EnumerateMonitors 重写**：
1. 先用 WMI 枚举内置屏（现有逻辑保留）
2. 再用 `EnumDisplayMonitors` + `GetPhysicalMonitorsFromHMONITOR` 枚举所有物理显示器
3. 对每个物理显示器调 `GetMonitorCapabilities` 检测 DDC/CI 能力
4. 内置屏（WMI 枚举到的）不重复加入 DDC 列表
5. 外接屏标记 `IsInternal=false`，存 `PhysicalHandle`

**新增 SetBrightness 重载**：
```
public static bool SetBrightness(MonitorInfo monitor, int percent)
```
- `monitor.IsInternal` -> 走现有 WMI `WmiSetBrightness`
- `!monitor.IsInternal` && `DdcSupported` -> 走 `SetMonitorBrightness(PhysicalHandle, value)`（需把 0-100 映射到 min-max 范围）

**新增 IsDdcSupported()**：
- 尝试枚举物理显示器 + 检测能力，有任意一个支持就返回 true

### 3. `MainForm.cs` - 亮度页交互优化

**MakeMonitorCard 签名扩展**：
```
private RoundedCard MakeMonitorCard(MonitorInfo monitor)
```
- 内置屏卡片：名称前加图标/标识「内置」，AccentColor=CBlue
- 外接屏卡片：名称前加标识「外接」，AccentColor=CPurple
- 滑块 ValueChanged 改为调 `SetBrightness(monitor, value)`（区分内置/外接）
- 不支持亮度的显示器（DdcSupported=false）：滑块禁用 + 显示「不支持亮度调节」

**GetBrightPage 调用改为传 MonitorInfo**：
- `MakeMonitorCard(m)` 替代 `MakeMonitorCard(m.Name, m.Brightness)`
- 查找显示器按钮重新枚举时也传 MonitorInfo

**诊断页外接屏 DDC/CI 改为实际探测**：
```
bool ddcOk = BrightnessController.IsDdcSupported();
y4 = AddDiagnosticRow(card4, y4, "外接屏 DDC/CI", "dxva2 物理显示器 API",
    ddcOk ? DiagStatus.Ready : DiagStatus.Unsupported,
    ddcOk ? "就绪" : "无外接屏", null, isLast: true);
```
Summary 改为 `内置Ok + ddcOk ? "2/2就绪" : 内置Ok ? "1/2就绪" : "0/2就绪"`

## 交互设计

亮度页布局（展开后）：
```
亮度
统一管理多显示器亮度，支持自动亮度。

自动亮度
  [根据系统环境光自动调节]          [开关]

🔍 查找显示器

已检测到 2 个显示器 · 支持亮度调节

┌─ 内置显示器 ───────────── 75% ─┐
│ 亮度  ━━━━━●━━━━━━━━━━━━━━     │
└────────────────────────────────┘

┌─ 外接显示器（DDC/CI）────── 60% ─┐
│ 亮度  ━━━━●━━━━━━━━━━━━━━━━     │
└────────────────────────────────┘
```

- 内置屏用蓝色滑块（CBlue）
- 外接屏用紫色滑块（CPurple）区分
- 不支持 DDC/CI 的外接屏：滑块灰显 + 卡片标题显示「不支持亮度调节」
- 拖动滑块实时调亮度（内置走 WMI，外接走 DDC/CI）

## 实现步骤
1. NativeMethods.cs 加 DDC/CI P/Invoke
2. BrightnessController.cs 扩展 MonitorInfo + EnumerateMonitors + SetBrightness 重载 + IsDdcSupported
3. MainForm.cs MakeMonitorCard 改签名 + GetBrightPage 调整 + 诊断页 DDC 探测
4. 编译验证
