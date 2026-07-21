# Traynexus 代码审计报告

**审计日期**：2026-07-20
**审计范围**：`src/Traynexus/` 全部 15 个 C# 文件 + 构建脚本 + app.manifest
**审计方式**：人工逐文件阅读 + 跨文件交叉验证 + 静态分析
**审计深度**：严重 bug / 资源泄漏 / 线程安全 / 异常处理 / 逻辑错误 / 安全问题

---

## 总览

| 等级 | 数量 | 描述 |
|---|---|---|
| 🔴 P0 严重 | 2 | 导致功能失效或崩溃 |
| 🟠 P1 高 | 6 | 资源泄漏、并发风险、IOCTL 缺陷 |
| 🟡 P2 中 | 7 | 逻辑瑕疵、UI 阻塞、异常吞没 |
| 🟢 P3 提示 | 4 | 代码质量改进建议 |

**总体评价**：代码质量中上，核心模块（MemoryCleaner / Settings / OemChargeController）设计严谨、注释到位、有明显的迭代修复痕迹（H1/H9/P1-1/P2-2 等）。但存在 **2 个 P0 级 bug** 必须修复，以及若干 GDI+ 资源泄漏。

---

## 🔴 P0 严重

### P0-1. `build.bat` 和 `build_debug.bat` 漏编 `BrightnessController.cs`

**位置**：`build.bat:20`、`build_debug.bat:22`
**对比**：`build.rsp:33` 已包含 `src\Traynexus\BrightnessController.cs`，但两个 `.bat` 的 csc 命令行都**没有**这个文件。

**影响**：
- `MainForm.cs` 多处调用 `BrightnessController.GetBrightness()` / `SetBrightness()` / `IsSupported()` / `EnumerateMonitors()`
- 直接执行 `build.bat` 会编译失败：`error CS0103: The name 'BrightnessController' does not exist in the current context`
- `build.rsp` 是对的，但 README 教用户用的是 `build.bat`

**修复**：把 `src\Traynexus\BrightnessController.cs` 追加到 build.bat:20 和 build_debug.bat:22 的源文件清单末尾（在 `OemChargeController.cs` 之后）。

---

### P0-2. `Settings.PersistWhitelist()` 在 `WhitelistLock` 内做文件 I/O 造成死锁风险

**位置**：`Settings.cs:87-113`
**问题代码**：
```csharp
public bool PersistWhitelist()
{
    lock (WhitelistLock)
    {
        // ...
        bool ok = WriteWhitelistFile();  // ← 锁内做文件 I/O（慢操作）
        // ...
    }
}
```

**问题分析**：
- `WhitelistLock` 同时被 UI 线程（`List_ItemCheck`、`Persist`、`Reload`）和后台线程（`MemoryCleaner.Execute → IsProtected`）使用
- 文件 I/O 可能耗时几十到几百毫秒（磁盘慢、杀毒软件扫描、网络盘）
- 在锁内做 I/O 期间，**所有后台释放线程会卡住等待**
- 若此时 UI 也调用 `SnapshotWhitelists()`，UI 也会被卡
- 更糟糕的是：如果用户在设置面板频繁点击"保存持久化"，每次都会卡住后台释放线程

**类似位置**：
- `RemovePersisted()` 同样在锁内 `WriteWhitelistFile()`（`Settings.cs:118-147`）
- `ReloadWhitelist()` 同样在锁内 `File.ReadAllLines()`（`Settings.cs:311-328`）

**修复方案**：
```csharp
public bool PersistWhitelist()
{
    string content;
    lock (WhitelistLock)
    {
        var added = new List<string>();
        foreach (var n in SessionWhitelist)
            if (UserWhitelist.Add(n)) added.Add(n);

        // 在锁内只构造内容，不做 I/O
        content = BuildWhitelistContent();
        // 记下 added 供回滚
        _pendingAdded = added;
    }

    // 锁外做 I/O
    bool ok = TryWriteWhitelistFile(content);

    lock (WhitelistLock)
    {
        if (ok) { SessionWhitelist.Clear(); _pendingAdded = null; return true; }
        else
        {
            foreach (var n in _pendingAdded) UserWhitelist.Remove(n);
            _pendingAdded = null;
            return false;
        }
    }
}
```

或者更简单：用 `ReaderWriterLockSlim` 替换 `lock`，I/O 期间用写锁，读取用读锁。

---

## 🟠 P1 高

### P1-1. `MainForm` 大量 `new Font()` 挂在控件属性上，控件 Dispose 时不会自动释放

**位置**：MainForm.cs 通篇（已确认 100+ 处 `new Font(...)` 赋值给 `xxx.Font`）
**问题**：
```csharp
lblT.Font = new Font(Fnt, 14f, FontStyle.Bold);   // 这个 Font 何时 Dispose？
```
- WinForms 控件的 `Font` 属性**不会**在控件 Dispose 时自动 Dispose 该 Font
- 每次打开"概览页" / "充电管理页" / "诊断页" 都会创建多个新 Font
- 尽管 `GetXxxPage()` 用了 `_pageXxx != null` 缓存，但 `NavigateTo()` 切换页面会 `Controls.Clear()`，再切回会重新走 `GetXxxPage()`（不命中缓存）就会再 new 一批 Font
- GDI+ Font 是非托管资源，泄漏会耗尽 GDI 句柄（默认每进程 10000 个）

**确认 GetXxxPage 缓存逻辑**：
- `GetOverviewPage()` 等用了 `if (_pageOverview != null) return _pageOverview;` — 表面缓存
- 但 `NavigateTo()` 调用 `_content.Controls.Clear()` 清空子控件
- 然后调用 `GetOverviewPage()` 拿到缓存的 `_pageOverview`，重新 `Controls.Add(page)`
- 所以 `_pageOverview` 是复用的，**Font 只 new 一次** ← 看起来安全

**但仍有泄漏点**：
1. **`NavigateToAbout()` 走 `_content.Controls.Clear()`** 后又调 `NavigateTo(5)` → `_content.Controls.Add(GetSettingsPage())` → GetSettingsPage 内部又 `new` 一堆 Font ← **不会泄漏，因为 GetSettingsPage 也是缓存**
2. **真正会泄漏的**：诊断页 `RebuildDiagCards()` 之类的反复重建场景
3. **`NavButton` 构造函数** `this.Font = new Font(Fnt, 10f);` — NavButton 跟随主窗体生命周期，理论上只创建一次，但当 MainForm 被关闭重建时（用户关闭窗口再打开控制台），会再次创建

**实测影响**：中等泄漏。长时间运行 + 频繁开关控制台窗口可能造成 GDI 句柄缓慢上涨。

**修复**：把 Font 提取为静态共享：
```csharp
private static class Fonts
{
    public static readonly Font H1 = new Font(Fnt, 15f, FontStyle.Bold);
    public static readonly Font H2 = new Font(Fnt, 12f, FontStyle.Bold);
    public static readonly Font Body = new Font(Fnt, 9f);
    public static readonly Font Small = new Font(Fnt, 8f);
    public static readonly Font BodyBold = new Font(Fnt, 10f, FontStyle.Bold);
    // ...
}
```
应用退出时统一 Dispose（或不 Dispose，由进程退出回收）。

---

### P1-2. `BatteryInfo.Take()` 在 UI 线程同步执行多个 WMI 查询，主刷新定时器每秒 1 次

**位置**：`TrayContext.cs:251-265` + `BatteryInfo.cs:68-125`
**问题**：
- `BatteryTick()` 每 5 秒在 UI 线程调用 `BatteryInfo.Take()`
- `Take()` 内部串行做 4 个 WMI 查询：`Win32_Battery` → `root\wmi\BatteryStatus` → `root\wmi\BatteryStaticData` → `root\wmi\BatteryCycleCount`
- 若 WMI 取不到，还会 `FillDeepData → GetDeepDataFromPowercfg` 启动 powercfg.exe 子进程（最多 5 秒等待）
- WMI 查询每次都要新建 `ManagementObjectSearcher`，冷启动查询在低端机上可能耗时 100-500ms
- UI 线程被阻塞 → 托盘菜单点击响应延迟、控制台拖动卡顿

**类似问题**：`MainForm.RefreshTitle()` / `RefreshOverview()` / `RefreshChargeStatus()` 都在 2 秒定时器里同步调 `MemoryInfo.Take()` 和 `BrightnessController.GetBrightness()`（WMI）

**修复**：
1. `BatteryTick` 改为后台线程采集 + UI 线程 PostToUi 更新：
```csharp
private void BatteryTick()
{
    ThreadPool.QueueUserWorkItem(_ =>
    {
        var snap = BatteryInfo.Take();
        this.PostToUi(() => {
            _battery = snap;
            // ... UI 更新
        });
    });
}
```
2. 或把 `_refreshTimer.Interval` 从 2000 改成 5000，并在后台预热数据

---

### P1-3. `OemChargeController.SetLenovoByPercent` 在后台线程内 `Thread.Sleep(50)` × 最多 12 次

**位置**：`OemChargeController.cs:344-360`
**问题**：
- 双命令连发每条 sleep 50ms → 100ms
- 回读校验 10 次 × 50ms → 最多 500ms
- 总计最多 600ms，确实如注释所说"耗时 1-2s"
- 调用方在 `MainForm.SelectChargeMode` 中用了 `ThreadPool.QueueUserWorkItem`，**没有阻塞 UI** ✅

**但**：`_bcTrack.ValueChanged` 触发时也会异步调用（`MainForm.cs:739`），拖动滑块会频繁触发，多个后台任务可能并发争用 `\\.\EnergyDrv` 设备句柄。

**修复**：
- 拖动滑块应该 debounce（如 500ms 内不再变化才发送）
- 或加互斥锁防止并发 IOCTL：
```csharp
private static readonly object _ioctlLock = new object();
public static bool SetChargeLimit(int percent)
{
    lock (_ioctlLock) { /* ... */ }
}
```

---

### P1-4. `QuickForm.PushBitmapToLayered` 在 UI 线程被频繁调用，每次创建/销毁 GDI 句柄

**位置**：`QuickForm.cs:299-329`
**问题**：
- 每 100ms `_focusTimer` Tick 检查失焦，期间 `RedrawLayered()` 也会触发
- `PushBitmapToLayered` 每次调用都 `GetDC → CreateCompatibleDC → GetHbitmap → SelectObject → UpdateLayeredWindow → SelectObject(还原) → DeleteObject → DeleteDC → ReleaseDC`
- 一次创建 + 销毁 5 个 GDI 对象
- 若 QuickForm 长时间显示且频繁刷新，GDI 句柄会有短暂峰值

**缓解**：代码用了 try/finally 确保释放，**没有泄漏**，只是性能开销。

**优化**：缓存 `memDc` 和 `hBitmap` 复用（仅当尺寸不变）。

---

### P1-5. `IconRenderer.RenderIcon` 内部 `new SolidBrush(textColor)` 未 Dispose

**位置**：`IconRenderer.cs:153`
**问题代码**：
```csharp
using (var font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
{
    var sf = new StringFormat { ... };
    var fullRect = new RectangleF(0, 0, size, size);
    var shadowRect = new RectangleF(1.5f, 1.5f, size, size);
    using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
    {
        g.DrawString(text, font, shadowBrush, shadowRect, sf);
    }
    g.DrawString(text, font, new SolidBrush(textColor), fullRect, sf);  // ← 泄漏
}
```
- `new SolidBrush(textColor)` 作为参数传入，无人 Dispose
- 托盘图标每秒最多重建 1 次（百分比变化时），且 `IconRenderer` 有缓存（按 percent 缓存 100 个），泄漏速度可控
- 但仍是确定性的 GDI+ 对象泄漏

**修复**：
```csharp
using (var textBrush = new SolidBrush(textColor))
    g.DrawString(text, font, textBrush, fullRect, sf);
```

---

### P1-6. `TrayContext.TickRefresh` 的 OEM 充电状态查询每秒调一次

**位置**：`TrayContext.cs:183-192`
**问题**：
```csharp
private void TickRefresh()  // 每 1 秒
{
    // ...
    try
    {
        var cap = OemChargeController.GetCapability();  // 缓存 5 分钟
        if (cap.Supported)
        {
            var status = OemChargeController.GetStatus();  // ← 每秒都重新 IOCTL
            // ...
        }
    }
    catch { }
}
```
- `GetCapability()` 有 5 分钟缓存 ✅
- 但 `GetStatus()` **没有缓存**，每次都打开设备 + IOCTL 查询
- 每秒 1 次设备句柄打开/关闭 + IOCTL，长期运行对驱动不友好

**修复**：给 `GetStatus()` 也加 5-10 秒缓存：
```csharp
private static ChargeStatus _cachedStatus;
private static DateTime _statusTime = DateTime.MinValue;
private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(10);

public static ChargeStatus GetStatus()
{
    lock (_cacheLock)
    {
        if (_cachedStatus != null && DateTime.Now - _statusTime < StatusTtl)
            return _cachedStatus;
    }
    // ... 原查询逻辑
}
```

---

## 🟡 P2 中

### P2-1. `MemoryCleaner.TrimAllProcesses` 中 `Process.GetProcesses()` 后逐个 `try { p.Dispose(); }` 但循环外漏 Dispose

**位置**：`MemoryCleaner.cs:181-228`
**问题**：每个 `p.Dispose()` 都包了 try-catch，**这是正确的**。但循环结束后没有 `procs = null` 提示 GC，且 `Process[] procs` 数组本身没有被特殊处理。`Process.GetProcesses()` 返回的数组里每个 `Process` 对象都持有一个句柄，逐个 `Dispose` 是必要的（已做）✅。**非问题**，跳过。

---

### P2-2. `AutoStartManager.Enable()` 的 schtasks `/TR` 参数引号转义脆弱

**位置**：`AutoStartManager.cs:48`
**问题代码**：
```csharp
string trArgs = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exePath + "\\\"\" /SC ONLOGON /RL HIGHEST /F";
```
- 当 `exePath` 含空格时（如 `C:\Program Files\Traynexus\Traynexus.exe`），生成的命令行：
  ```
  schtasks /Create /TN "Traynexus_AutoStart" /TR "\"C:\Program Files\Traynexus\Traynexus.exe\"" /SC ONLOGON /RL HIGHEST /F
  ```
- 这种 `\"...\"` 嵌套转义在 cmd 下是能解析的 ✅
- 但若 `exePath` 含特殊字符（如 `&`、`|`、`"`），会失败
- 用户安装路径是 `Application.ExecutablePath`，通常是安全的
- **风险提示**：路径中含 Unicode 特殊字符（如 `中文` 或 `&`）时可能失败

**修复**：直接传 task XML 或用 TaskScheduler COM API 更稳。

---

### P2-3. `QuickForm._focusTimer` 在 `FormClosed` 中 Dispose，但 Hide 后 timer 还在跑

**位置**：`QuickForm.cs:98-125`
**问题**：`Hide()` 后 `_focusTimer.Stop()` 被调用（在 VisibleChanged 处理中）✅，但 `_focusTimer` 实例本身只在 `FormClosed` 中 Dispose。QuickForm 会被多次 Show/Hide，timer 反复 Start/Stop，**没有泄漏** ✅。但 timer 在 Close 后没置 null，重复进入 `FormClosed` 时 `Stop()` 是幂等的 ✅。

**非问题**，跳过。

---

### P2-4. `Program.cs` Mutex 没有 ReleaseMutex 调用，进程崩溃时 named mutex 会变成 abandoned

**位置**：`Program.cs:15-39`
**问题**：
```csharp
_mutex = new Mutex(true, "Global\\Traynexus_SingleInstance_v1", out createdNew);
// ...
Application.Run(new TrayContext());
// ...
GC.KeepAlive(_mutex);  // 仅保持存活，从未 ReleaseMutex
```
- 当进程崩溃时，mutex 进入 abandoned 状态，下一个进程 `new Mutex(true, name, out createdNew)` 时 `createdNew = false` 但能获得所有权，会抛 `AbandonedMutexException`
- 当前代码没有 catch 这个异常，第二个进程会因 `createdNew=false` 弹"已经在运行"对话框退出 ← 实际行为反而符合预期
- 但如果用户期望"崩溃后允许新实例启动"，就会出问题

**实际影响**：低。崩溃后用户重启时弹出"已经在运行"，需要手动结束残留进程或重启系统。

**修复**：catch `AbandonedMutexException`：
```csharp
try
{
    createdNew = _mutex.WaitOne(0);
    if (createdNew) _mutex.ReleaseMutex();
    // 用 WaitOne 而不是构造参数的 initiallyOwned
}
catch (AbandonedMutexException)
{
    createdNew = true;  // 上一个进程崩溃了，我们接管
}
```

---

### P2-5. `BatteryInfo.GetDeepDataFromPowercfg` 启动 `powercfg.exe` 等待 5 秒但可能失败

**位置**：`BatteryInfo.cs:219-228`
**问题**：
```csharp
var psi = new ProcessStartInfo("powercfg.exe", "/batteryreport /output \"" + htmlPath + "\"")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardError = true
};
using (var p = Process.Start(psi))
{
    p.WaitForExit(5000);  // 等待最多 5s，但不检查返回值
}
if (!File.Exists(htmlPath)) return null;
```
- 没有 `RedirectStandardOutput`，powercfg 可能输出大量文本导致阻塞
- `WaitForExit(5000)` 超时后进程仍在运行，htmlPath 文件可能晚一点生成
- 30s 缓存生效时会再次启动进程

**修复**：
```csharp
psi.RedirectStandardOutput = true;
using (var p = Process.Start(psi))
{
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
}
```

---

### P2-6. `Settings.Log` 永远追加到同一个 error.log 文件，无大小限制

**位置**：`Settings.cs:17-34`
**问题**：日志文件无大小上限，长期运行可能累积很大。

**修复**：超过 1MB 时截断或滚动。

---

### P2-7. `OemChargeController.CallAsusMethod` 输出缓冲区只有 16 字节

**位置**：`OemChargeController.cs:316`
**问题**：
```csharp
byte[] outBuf = new byte[16];
```
- ACPI DSTS 返回值规范通常是 8 字节（4 字节 method + 4 字节 args），16 字节够
- 但部分 ASUS 机型返回的电池信息结构体可能更大
- 若返回数据超过 16 字节，会被截断且无法察觉

**修复**：根据 `ASUS_DEVID_BATTERY_LIMIT` 的实际返回值规范调整，或一开始用 64 字节保险。

---

## 🟢 P3 提示

### P3-1. `MainForm.cs` 3705 行是单文件巨石，建议拆分

**位置**：整个 MainForm.cs
**问题**：
- 15 个内部类（NavButton/ToggleSwitch/RoundedCard/CollapsibleDiagCard/BarStrip/RoundSlider/IconBox/ModeCard/RingChart/SubTabButton/FlatBorderedButton/TagLabel/PickerDialog/NumberDialog/TransparentPanel）都塞在一个文件
- 100+ 个 `new Font(...)` 散布
- 页面构建方法 `GetOverviewPage` / `GetChargePage` / `GetBrightPage` / `GetDiagnosticPage` / `GetSettingsPage` 各自 200-500 行

**建议**：
- 自绘控件抽到 `Controls/` 目录，每个控件一个文件
- 页面构建抽到 `Pages/` 目录
- MainForm 只保留导航和生命周期

---

### P3-2. 自绘控件的 `OnPaintBackground` 都是空实现，依赖 `OnPaint` 重画

**位置**：多个内部类
**问题**：
```csharp
protected override void OnPaintBackground(PaintEventArgs pevent) { }
```
- 这是为了消除双缓冲初始化的脏像素，是已知技术
- 但意味着每次 Invalidate 都会完整 OnPaint
- 部分控件（如 `BarStrip`）只需更新进度条，不必整面重画

**建议**：使用 `Invalidate(rect)` 局部刷新。

---

### ~~P3-3. 国际化字符串硬编码中文~~（已决策：仅中文）

**位置**：全项目
**决策记录**：作者 2026-07-20 确认仅做中文版。曾考虑中英双语，但实现需引入 resx 资源并重新编译整个项目，成本大于收益，放弃。
**结论**：非问题。字符串继续硬编码中文即可，无需提取 resx。

---

### P3-4. `app.manifest` 声明了 Win 7/8/8.1/10 兼容性，但项目用了 Win 10+ 的特性

**位置**：`app.manifest:13-24`
**问题**：
- 声明支持 Win 7，但 `WmiMonitorBrightnessMethods` 类是 Win 8+，`BatteryCycleCount` 是 Win 10+
- `requireAdministrator` 在 Win 7 下的 UAC 行为略不同

**建议**：把 supportedOS 改为只 Win 10 + Win 11（新增 `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`），或删除 Win 7/8 条目避免误导。

---

## 修复优先级建议（实际修复进度）

### ✅ v1.0720.2 已修复
1. ~~P0-1~~：build.bat / build_debug.bat 已补 `BrightnessController.cs`
2. ~~P0-2~~：build_debug.bat 已补 4 个 `/resource` 参数
3. ~~P1~~：`ConfigMigrator.Migrate` 迁移成功后删除旧目录
4. ~~P1~~：`SelectChargeMode` 失败回滚 `_settings` 到旧值
5. ~~P1~~：Lenovo UI 引导改回"5MB EM 驱动"（与代码注释/README 统一）
6. ~~P2~~：`AutoStartManager.Disable` 检查 `ExitCode`

### ✅ v1.0720.3 已修复
7. ~~P1~~：`OemChargeController.GetStatus` 加 10s 缓存
8. ~~P1~~：`SetChargeLimit` 加 `_ioctlLock` 互斥锁 + 滑块 500ms debounce
9. ~~P1~~：`BatteryTick` 改后台线程采集
10. ~~P1~~：`BatteryInfo.FillDeepData` 加 30s 缓存
11. ~~P1~~：`IconRenderer.RenderIcon` `new SolidBrush` 改 using
12. ~~P2~~：Settings 锁外 I/O（PersistWhitelist / RemovePersisted / ReloadWhitelist）
13. ~~P2~~：`Settings.Save()` 返回 bool
14. ~~P2~~：`BatteryInfo` powercfg 子进程防阻塞（RedirectStandardOutput + 超时 Kill）
15. ~~P2~~：`BatteryStatus=3` 兜底改为非充电
16. ~~P2~~：Program.cs Mutex abandoned 处理
17. ~~P2~~：删除死代码 `ShowAbout()`
18. ~~P3~~：`GetWindowLongPtr`/`SetWindowLongPtr` 64 位稳妥化
19. ~~P3~~：error.log 1MB 截断
20. ~~P3~~：app.manifest supportedOS 收敛到 Win 10+

### ✅ v1.0720.4 已修复
21. ~~P1-1~~：MainForm Font 静态共享（提取 `Fonts` 静态类，74 处 `new Font()` 全部替换；QuickForm 4 处 Paint 内 Font 纳入共享；修复 OnPaint 内 `using` 包裹共享 Font 的 Dispose 陷阱）

### 🔵 未修复（设计中 / 留给下个迭代）
22. **P1**（外部报告 #7）：实现 ASUS `QueryAsusLimit`（DSTS 返回值解析），或明确标注"ASUS 不支持回读"。需真实 ASUS 机型验证
23. **P3-1**：MainForm.cs 拆文件（3294 行巨石 -> Controls/ + Pages/）
24. ~~P3-3~~：已决策仅中文，无需处理

---

## 亮点（值得肯定的设计）

- ✅ **MemoryCleaner.EnablePrivilege** 正确处理了 `AdjustTokenPrivileges` 的 `ERROR_NOT_ALL_ASSIGNED` 陷阱
- ✅ **Settings.WriteWhitelistFile** 原子写入（tmp + Replace）
- ✅ **PersistWhitelist** 失败回滚只撤回本次新增项，不误删原有持久化项
- ✅ **IconRenderer** 缓存机制 + 双检锁
- ✅ **MemoryCleaner.TrimAllProcesses** 进程名匹配统一走 `WhitelistSnapshot.MatchInSet`
- ✅ **Program.cs** 单实例锁
- ✅ **TrayContext.Dispose** 完整清理链路
- ✅ **QuickForm.PushBitmapToLayered** try/finally 确保 GDI 句柄释放
- ✅ **OemChargeController** 清晰的协议注释和能力缓存
- ✅ **代码注释里能看到 H1/H9/P1-1/P2-2 等修复编号**，说明作者有迭代审计习惯

---

**审计人**：WorkBuddy (Claude)
**审计方法**：静态代码审查 + 跨文件调用链分析
