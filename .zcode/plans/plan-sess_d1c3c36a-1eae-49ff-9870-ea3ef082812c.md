# 功能诊断面板 + ASUS/Lenovo 充电控制接入 实现方案

## 目标
1. 新增侧边栏「诊断」导航页（第 6 项），集中展示所有功能模块的可用性状态
2. 重构 `OemChargeController`：废弃不工作的 WMI 实现，改走 IOCTL（ASUS `\\.\ATKACPI` + Lenovo `\\.\EnergyDrv`）
3. 充电页接线：检测能力后灰显不支持控件、提示文案区分成功/失败/不支持

## 技术决策（基于调研结论）
- **不复制 GPL 源码**，参考协议常数自行重写（协议事实不受版权保护）
- **ASUS**：IOCTL `0x0022240C` -> `\\.\ATKACPI`，DEVID `0x00120057`，支持 40-100% 任意阈值
- **Lenovo**：IOCTL `0x831020F8` -> `\\.\EnergyDrv`，仅 3 模式（保养/正常/快充），不支持任意阈值
- **Dell/HP**：第一版标记「开发中」，诊断面板显示但不实现控制逻辑

---

## 改动文件清单（5 个文件）

### 1. `src/Traynexus/NativeMethods.cs` — 新增 IOCTL P/Invoke 区块
在文件末尾 `SetWindowPos` 之后追加新区块 `// ==== 设备 IOCTL（OEM 充电控制）====`：
- `CreateFileW`（kernel32, CharSet=Unicode, SetLastError=true）返回 `SafeFileHandle`
- `DeviceIoControl`（kernel32, SetLastError=true）签名：`(SafeFileHandle, uint ioControlCode, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint bytesReturned, IntPtr overlapped)`
- 访问常量：`GENERIC_READ=0x80000000`, `GENERIC_WRITE=0x40000000`, `FILE_SHARE_READ=1`, `FILE_SHARE_WRITE=2`, `OPEN_EXISTING=3`
- `INVALID_HANDLE_VALUE = new IntPtr(-1)`
- 复用现有 `CloseHandle(IntPtr)`（line 69）作为 SafeFileHandle 释放的兜底（若用 SafeFileHandle 则自动管理）

### 2. `src/Traynexus/OemChargeController.cs` — 重写（保留外壳，替换内核）
**保留**：
- `OemVendor` 枚举（Unknown/Lenovo/Dell/HP/Asus）
- `ChargeCapability` 类结构 + 扩展字段
- `GetCapability()` 的制造商检测逻辑（`Win32_ComputerSystem.Manufacturer` 子串匹配）

**扩展 `ChargeCapability` 类**：
```csharp
public class ChargeCapability
{
    public OemVendor Oem;
    public bool Supported;              // 设备句柄能否打开 + IOCTL 探测调用不抛异常
    public string DevicePath = "";      // \\.\ATKACPI 或 \\.\EnergyDrv
    public string DriverName = "";      // 驱动/软件名（诊断面板展示用）
    public string Hint = "";            // 人类可读说明
    public int MinThreshold = 50;       // 支持的最小阈值
    public int MaxThreshold = 100;      // 支持的最大阈值
    public int[] SupportedThresholds = null;  // Lenovo 固定档位 {60,80,100}；null 表示连续
    public ChargeModeType ModeType = ChargeModeType.Continuous;  // Continuous/ThreeMode
}
public enum ChargeModeType { Continuous, ThreeMode }
```

**废弃**：`ProbeOemWmi`、`WmiClassExists`、`SetLenovo/SetDell/SetHP/SetAsus` 的 WMI 方法体

**新增 IOCTL 实现**：
- `ProbeAsus()`：`CreateFileW(@"\\.\ATKACPI")` 打开句柄 -> 发一次 DSTS 查询 BatteryLimit(DEVID 0x00120057) -> 不抛异常即支持。设置 `DevicePath`、`DriverName="ASUS System Control Interface"`、`MinThreshold=40`、`MaxThreshold=100`
- `ProbeLenovo()`：`CreateFileW(@"\\.\EnergyDrv")` 打开句柄 -> 发 IOCTL 0x831020F8 inBuffer=0xFF 查询 -> 不抛异常即支持。设置 `DevicePath`、`DriverName="Lenovo Energy Management Driver"`、`SupportedThresholds={60,80,100}`、`ModeType=ThreeMode`
- `ProbeDell()/ProbeHp()`：第一版直接返回 `Supported=false, Hint="Dell/HP 支持开发中"`
- `SetAsusLimit(int percent)`：构造 8 字节 args `[0..3]=DEVID(0x00120057) [4..7]=percent`，调用 `DeviceSet(DEVS=0x53564544, args)`
- `SetLenovoMode(int mode)`：mode 0=保养{0x08,0x03} / 1=正常{0x05,0x08} / 2=快充{0x05,0x07}，逐个发 IOCTL 0x831020F8
- `GetStatus()`：新增，回读当前阈值/模式（DSTS 查询 ASUS / IOCTL 查询 Lenovo），供诊断面板和充电页验证

**`SetChargeLimit(int percent)` 重写**：
- 调 `GetCapability()` 缓存（不每次重探，加静态字段 `_cachedCap` + 5 分钟 TTL）
- ASUS：直接传 percent（40-100）
- Lenovo：把 percent 映射到 3 档（>=100->正常, >=80->正常, <80->保养）-- 注意 Lenovo 的「正常」对应满充，「保养」对应 60% 上限
- Dell/HP：返回 false
- 返回 `bool` 成功与否（不再被 try-catch 吞）

### 3. `src/Traynexus/MainForm.cs` — 新增诊断页 + 充电页接线修复

#### 3a. 侧边栏扩展（line 171-172）
```csharp
string[] labels = { "概览", "内存释放", "充电管理", "亮度", "设置", "诊断" };
NavIcon[] icons = { NavIcon.Grid, NavIcon.Memory, NavIcon.Battery, NavIcon.Sun, NavIcon.Cog, NavIcon.Activity };
```

#### 3b. 页面字段（line 46）
```csharp
private Panel _pageOverview, _pageRelease, _pageCharge, _pageBright, _pageSettings, _pageDiagnostic;
```

#### 3c. NavigateTo 扩展（line 265 switch）
```csharp
case 5: page = GetDiagnosticPage(); break;
```

#### 3d. 新增 `GetDiagnosticPage()` 方法（放在 GetSettingsPage 之后）
布局结构（参考 GetChargePage 的 FlowLayoutPanel 模式）：
- FlowLayoutPanel：Dock=Fill, TopDown, WrapContents=false, AutoScroll=true, Padding=(28,40,28,40)
- 大标题「功能诊断」15pt Bold + 副标题「检测各功能模块依赖项的安装状态」9pt CInk2
- 顶部「立即重新检测」FlatBorderedButton（右上角，触发全量重检）
- 按功能分组，每组一个 RoundedCard 卡片：

**卡片 1：内存管理**（预期全绿，展示已就绪）
- 行1：内存采集（状态：✅ 就绪）— `MemoryInfo.Take()` 可用
- 行2：内存释放（状态：✅ 就绪）— StandbyList + WorkingSet
- 行3：阈值自动释放（状态：✅ 就绪）

**卡片 2：电池信息**
- 行1：电池基础数据（电量/充电状态）— 检测 `Win32_Battery` WMI 是否返回 IsPresent
- 行2：电池深度数据（设计容量/循环次数）— 检测 `BatteryStaticData`/`BatteryCycleCount` 是否返回非 0 值。未上报时显示「硬件未上报」+ 说明「ACPI 表未实现，装驱动无效」

**卡片 3：OEM 充电控制**（核心）
- 行1：当前机型 — 显示 `Win32_ComputerSystem.Manufacturer` + Model
- 行2：ASUS 充电控制 — 检测 `\\.\ATKACPI` 句柄能否打开。支持时显示「✅ 就绪（ASUS System Control Interface）」+ 支持阈值范围；不支持显示「⚠️ 需安装 ASUS 官方驱动」+ SoftLinkButton「打开下载页」跳 asus.com
- 行3：Lenovo 充电控制 — 检测 `\\.\EnergyDrv`。支持显示「✅ 就绪」+ 「保养/正常/快充 三档模式」；不支持显示「⚠️ 需安装 Lenovo Vantage」+ 跳 lenovo.com
- 行4：Dell 充电控制 — 显示「⏳ 开发中」
- 行5：HP 充电控制 — 显示「⏳ 开发中」

**卡片 4：亮度控制**
- 行1：内置屏亮度（WMI `WmiMonitorBrightnessMethods`）— 检测 WMI 类是否存在且可调用。第一版只检测不实现控制
- 行2：外接屏 DDC/CI — 显示「⏳ 开发中」

每个诊断行用新私有方法 `AddDiagnosticRow(Panel card, int y, string name, string desc, DiagStatus status, Action onClick)`：
- `DiagStatus` 枚举：`Ready/Warning/Unsupported/Pending`
- 状态用 `TagLabel` 显示（Ready=CGreen「就绪」/Warning=COrange「需安装」/Unsupported=CInk3「不支持」/Pending=CInk3「开发中」）
- 若 `onClick != null` 右侧加 `SoftLinkButton`（如「打开下载页」）
- 行高 36，分隔线 `Color.FromArgb(240,240,243)`

#### 3e. 充电页接线修复
- `GetChargePage()` 开头调一次 `OemChargeController.GetCapability()`，缓存到新字段 `_chargeCap`
- 若 `!_chargeCap.Supported`：禁用 `_bcTrack`（设 Enabled=false，OnPaint 画灰）、禁用 3 个 `_modeCards`，`_bcHint` 显示「当前机型不支持充电阈值控制（{Oem}）。{Hint}」
- 若 Lenovo（ThreeMode 模式）：隐藏滑块行，只显示 3 个模式卡；或保留滑块但 ValueChanged 时映射到 3 档
- `_bcTrack.ValueChanged`（line 672）和 `SelectChargeMode`（line 905）：去掉 `try { } catch { }`，改用 `bool ok = OemChargeController.SetChargeLimit(...)`，`UpdateChargeHint` 根据 ok 显示「已设置」/「设置失败」
- `UpdateChargeHint()` 扩展：区分支持/不支持/成功/失败 4 种文案

### 4. `build.rsp` — 无需改动
所有新代码都在现有 .cs 文件内，无新文件。`System.Management.dll` 已引用。

### 5. `.gitignore` — 追加 bin/ 排除规则
当前 bin/ 残留 WebView2 DLL，加入 `bin/*.dll` 和 `bin/Traynexus.exe.WebView2/` 排除，避免污染仓库

---

## 实现步骤顺序

1. **NativeMethods.cs** — 加 IOCTL P/Invoke（基础设施，无依赖）
2. **OemChargeController.cs** — 重写 IOCTL 实现（依赖步骤1）
3. **MainForm.cs 诊断页** — `GetDiagnosticPage()` + `AddDiagnosticRow()` + `DiagStatus` 枚举（依赖步骤2的 GetCapability）
4. **MainForm.cs 侧边栏+导航** — 扩展 labels/icons 数组 + case 5 + 字段（步骤3的前置）
5. **MainForm.cs 充电页接线** — 灰显逻辑 + 提示文案 + 去掉空 catch（依赖步骤2）
6. **编译验证** — `csc.exe @build.rsp`，确保 0 error 0 warning
7. **.gitignore 清理**

## 风险与回退
- **Lenovo IOCTL 协议未经真机验证**：模式码 `{0x08,0x03}` 等来自 LLT 开源代码，若不工作，SetChargeLimit 返回 false，UI 显示「设置失败」，不会崩溃。诊断面板的探测仍能正确判断「驱动是否安装」
- **ASUS 6080 机型检测**：第一版不实现 6080 机型名单（需要完整机型数据库），所有 ASUS 机器按连续阈值处理。若用户机器是 6080 机型，设置非 60/80/100 的值时驱动会自动归一化，不影响功能
- **GPL 合规**：全部代码自行编写，仅参考协议常数（DEVID/IOCTL 码/设备路径），不复制任何源码片段

## 不在本次范围
- Dell/HP 充电控制实现（标记开发中）
- 亮度控制实现（诊断面板只检测 WMI 类，不实现控制）
- 电池深度数据接入 UI（诊断面板只检测可用性，不替换充电页 mock）
- 任务调度、主题/语言切换
- ThinkPad `LEN_BATTERY_METHODS` WMI 支持（LLT 无参考，需独立逆向）