# Traynexus 代码审查报告 - 2026-07-26

审查范围：v1.0725.2（HEAD 84c42f9），17 个 .cs 源文件 + build.rsp / build.bat / build_debug.bat + installer/Traynexus.iss + app.manifest

审查方式：只读分析，未修改任何代码文件。

## 摘要

共发现 **15** 个问题：P0 × 0，P1 × 4，P2 × 6，P3 × 5。

- **P0（致命）**：未发现。全量扫描 C# 6/7 语法（`?.`、`$""`、`out var`、`nameof`、expression-bodied members、tuple 解构、`using static`）均无命中，代码可被 C# 5 编译器（csc.exe v4.0.30319）正常编译。
- **P1（严重）**：版本号三方不一致；DDC/CI 物理监视器句柄大量泄漏且退出时从不释放；WMI 亮度设置返回值丢弃（已知问题仍存在）；schtasks 子进程超时后孤儿泄漏。
- **P2（一般）**：ASUS 机型 GetStatus 每秒发 IOCTL；DDC 亮度映射整数除法误差；WMI ManagementObject 普遍未 Dispose；WebClient 无超时；构建脚本引用未使用的 SMA.dll；Lenovo 快充模式为死代码。
- **P3（改进）**：manifest 版本号陈旧；多处 Font 未 Dispose；IconRenderer 废弃参数；日志截断字节/字符混用；TickRefresh 1s 频率偏高。

---

## P0 - 致命（编译不过 / 必崩 / 数据丢失）

无。

全量正则扫描以下 C# 6/7 语法均无命中（仅 `src/ui/traynexus-ui.html` 中的 JavaScript `?.` 不影响编译）：

| 语法 | 命中 | 说明 |
|------|------|------|
| `?.` 空条件运算符 | 0 | C# 6 |
| `$"..."` 字符串插值 | 0 | C# 6 |
| `out var` | 0 | C# 7 |
| `nameof(` | 0 | C# 6 |
| `using static` | 0 | C# 6 |
| expression-bodied 成员 `=>` | 0 | C# 6（lambda `=>` 属 C# 3+，不算） |
| tuple 解构 `var (a,b) =` | 0 | C# 7 |

代码可被 `csc.exe` (.NET Framework 4.x / C# 5) 正常编译。

---

## P1 - 严重（特定场景崩溃 / 功能失效 / 性能严重劣化）

### 1. 版本号三方不一致——更新检查与显示版本错误

- **位置**：`src/Traynexus/UpdateChecker.cs:23`、`src/Traynexus/MainForm.cs:242`、`src/Traynexus/MainForm.cs:2313`、`installer/Traynexus.iss:9-16`
- **类型**：配置/发布缺陷
- **影响**：
  - `UpdateChecker.CurrentVersion = "1.0725.1"`，但安装包与 CHANGELOG 均为 `1.0725.2`。
  - 主窗体侧边栏底部 `lblVer.Text = "Version: v1.0725.1"`（MainForm.cs:242）与关于页 `lblVer.Text = "v1.0725.1"`（MainForm.cs:2313）向用户显示错误版本。
  - 更新检查 `IsNewerVersion(latest, CurrentVersion)` 以 1.0725.1 为基准：若 GitHub 已发布 1.0725.2，会被误报为"有新版本"（用户已是 1.0725.2 却被提示升级到 1.0725.2）。
- **代码**：
  ```csharp
  // UpdateChecker.cs:23
  public const string CurrentVersion = "1.0725.1";

  // MainForm.cs:242
  lblVer.Text = "Version: v1.0725.1";

  // MainForm.cs:2313
  lblVer.Text = "v1.0725.1";

  // installer/Traynexus.iss:9-10
  AppVersion=1.0725.2
  AppVerName=TrayNexus 1.0725.2
  ```
- **修复建议**：将 UpdateChecker.CurrentVersion 与两处 lblVer 统一改为 `"1.0725.2"`。建议后续把版本号集中到一个常量（如 `UpdateChecker.CurrentVersion`），所有 UI 直接引用该常量，避免再次漂移：
  ```csharp
  // MainForm.cs:242
  lblVer.Text = "Version: v" + UpdateChecker.CurrentVersion;
  // MainForm.cs:2313
  lblVer.Text = "v" + UpdateChecker.CurrentVersion;
  ```

### 2. DDC/CI 物理监视器句柄泄漏 + Cleanup 从未被调用

- **位置**：`src/Traynexus/BrightnessController.cs:224-302`（`EnumerateDdcMonitors`）、`src/Traynexus/BrightnessController.cs:305-318`（`Cleanup`）、`src/Traynexus/TrayContext.cs:459-479`（`Dispose`）
- **类型**：资源泄漏（Win32 Handle）
- **影响**：
  1. **Cleanup 从未被调用**：`TrayContext.Dispose` 释放了 Timer / Tray / IconRenderer 缓存，但**漏调** `BrightnessController.Cleanup()`。DDC 物理监视器句柄（`GetPhysicalMonitorsFromHMONITOR` 返回）在整个进程生命周期内全部泄漏，直到 OS 回收。
  2. **EnumerateDdcMonitors 三处句柄泄漏**：
     - 第 247 行：跳过内置屏时 `if (skipCount > 0) { skipCount--; continue; }` —— `pm.hPhysicalMonitor` 已分配，直接 `continue`，**句柄永不销毁**。
     - 第 286-289 行：外接屏不支持 DDC 时（`else` 分支），仅设 `info.Brightness = -1`，**句柄既不加入 `_physicalHandles` 也不销毁**。
     - 第 284 行：DDC 支持的句柄加入 `_physicalHandles`，但每次调用 `EnumerateMonitors` 都重新 `GetPhysicalMonitorsFromHMONITOR` 拿**新句柄**追加进列表，旧句柄变陈旧副本，`Cleanup` 时对陈旧句柄调 `DestroyPhysicalMonitors` 行为未定义。
  3. **用户可触发**：MainForm 亮度页"🔍 查找显示器"按钮（MainForm.cs:1310）与初始加载（MainForm.cs:1328）每次点击都调用 `EnumerateMonitors`，每点一次泄漏一组句柄。
- **代码**（跳过内置屏的泄漏路径）：
  ```csharp
  // BrightnessController.cs:242-247
  foreach (var pm in physicals)
  {
      if (pm.hPhysicalMonitor == IntPtr.Zero) continue;
      // 跳过内置屏（WMI 已枚举的），避免重复
      if (skipCount > 0) { skipCount--; continue; }   // ← pm.hPhysicalMonitor 泄漏
  ```
  ```csharp
  // BrightnessController.cs:286-289
  else
  {
      info.Brightness = -1;   // ← pm.hPhysicalMonitor 泄漏（未加入 _physicalHandles，未 Destroy）
  }
  ```
  ```csharp
  // TrayContext.cs:459-479 Dispose 未调 Cleanup
  protected override void Dispose(bool disposing)
  {
      if (disposing)
      {
          ...
          IconRenderer.DisposeCache();   // ← 缺少 BrightnessController.Cleanup();
      }
      base.Dispose(disposing);
  }
  ```
- **修复建议**：
  1. `TrayContext.Dispose` 中补一行 `BrightnessController.Cleanup();`。
  2. `EnumerateDdcMonitors` 中对**所有** `pm.hPhysicalMonitor != IntPtr.Zero` 的句柄，要么加入 `_physicalHandles`（统一由 Cleanup 释放），要么在本地 `try/finally` 立即 `DestroyPhysicalMonitors`。非 DDC 与跳过路径必须显式释放：
     ```csharp
     // 跳过内置屏：立即释放该句柄
     if (skipCount > 0) {
         skipCount--;
         var tmp = new NativeMethods.PHYSICAL_MONITOR[1];
         tmp[0] = pm;
         try { NativeMethods.DestroyPhysicalMonitors(1, tmp); } catch { }
         continue;
     }
     // 非 DDC 外接屏同理
     ```
  3. 若希望 `_physicalHandles` 跨调用复用，应在 `EnumerateMonitors` 开头先 `Cleanup` 旧句柄，或改为"首次枚举后缓存，后续直接复用"策略，避免每次枚举都申请新句柄。

### 3. BrightnessController.SetBrightness 丢弃 WMI 返回值（已知问题 #1，仍存在）

- **位置**：`src/Traynexus/BrightnessController.cs:89-111`
- **类型**：逻辑缺陷
- **影响**：`mo.InvokeMethod("WmiSetBrightness", ...)` 返回 `ManagementBaseObject`，其 `ReturnValue` 属性 0 = 成功，非 0 = 失败。当前代码只要不抛异常就 `return true`，WMI 实际写入失败（如固件拒绝、亮度超范围）也会上报"成功"，导致 UI 显示新亮度但实际未改变，用户误以为生效。
- **代码**：
  ```csharp
  // BrightnessController.cs:98-103
  foreach (ManagementObject mo in searcher.Get())
  {
      mo.InvokeMethod("WmiSetBrightness", new object[] { 0, (byte)percent });
      InvalidateBrightnessCache();
      return true;   // ← 不检查返回值
  }
  ```
- **修复建议**：
  ```csharp
  foreach (ManagementObject mo in searcher.Get())
  {
      using (mo)
      {
          var ret = mo.InvokeMethod("WmiSetBrightness", new object[] { 0, (byte)percent })
                      as ManagementBaseObject;
          uint code = ret == null ? 0xffffffff : Convert.ToUInt32(ret["ReturnValue"]);
          if (code == 0)
          {
              InvalidateBrightnessCache();
              return true;
          }
          Settings.Log("WmiSetBrightness ReturnValue=" + code);
          return false;
      }
  }
  ```

### 4. AutoStartManager 子进程超时后孤儿泄漏 + 异常路径

- **位置**：`src/Traynexus/AutoStartManager.cs:33-34`、`60-61`、`85-86`、`110-111`、`120`
- **类型**：资源泄漏（进程）+ 运行时异常
- **影响**：`p.WaitForExit(3000)` / `WaitForExit(5000)` 超时后返回 `false`，但代码紧接着访问 `p.ExitCode`。**进程未退出时 `p.ExitCode` 抛 `InvalidOperationException`**（"Process has not exited"）。虽然被外层 `catch` 捕获返回 `false`，但：
  1. `schtasks.exe` 子进程仍在运行（`Process.Dispose()` 不会 kill 子进程），成为孤儿进程。
  2. `using` 块结束时 `Process.Dispose` 仅释放托管资源，不杀进程。
  3. 若 schtasks 因 UAC/权限挂起，多次调用会累积孤儿进程。
- **代码**：
  ```csharp
  // AutoStartManager.cs:31-35
  using (var p = Process.Start(psi))
  {
      p.WaitForExit(3000);
      return p.ExitCode == 0;   // ← 超时后 p 未退出，ExitCode 抛异常
  }
  ```
- **修复建议**：检查 `WaitForExit` 返回值，超时则 kill：
  ```csharp
  using (var p = Process.Start(psi))
  {
      if (!p.WaitForExit(3000))
      {
          try { p.Kill(); } catch { }
          return false;
      }
      return p.ExitCode == 0;
  }
  ```
  全部 5 处 `WaitForExit` + `ExitCode` 都需同样修复。

---

## P2 - 一般（边界 case 异常 / 健壮性问题）

### 1. ASUS 机型 GetStatus 每秒发 IOCTL（null 结果不缓存）

- **位置**：`src/Traynexus/OemChargeController.cs:241-291`（`GetStatus`）、`385-401`（`QueryAsusLimit`）、`src/Traynexus/TrayContext.cs:252-262`（`TickRefresh` 每 1s 调用）、`src/Traynexus/MainForm.cs:998-1075`（`RefreshChargeStatus` 每 2s 调用）
- **类型**：性能问题
- **影响**：`QueryAsusLimit` 第 395 行 `return null;`（第一版未实现精确阈值读取）。`GetStatus` 第 282 行 `if (result != null)` 才缓存——null 结果**不缓存**。导致 ASUS 机型上：
  - `TrayContext.TickRefresh`（1s）每秒调用 `GetStatus` → 缓存 miss → `GetCapability`（5min 缓存命中）→ 进入 `_ioctlLock` → `OpenDevice(ATKACPI)` → `DeviceIoControl` → 返回 null → 不缓存。
  - `MainForm.RefreshChargeStatus`（2s）同样每 2s 一次。
  - 合计约 **1.5 次/秒 IOCTL** 持续打 ATKACPI 驱动，UI 线程上每次几十 ms 抖动，长期运行可能影响驱动稳定性。
- **代码**：
  ```csharp
  // OemChargeController.cs:282-289
  if (result != null)   // ← null 不缓存，导致每秒重查
  {
      lock (_cacheLock)
      {
          _cachedStatus = result;
          _statusTime = DateTime.Now;
      }
  }
  ```
- **修复建议**：对 ASUS 暂时返回一个占位 `ChargeStatus`（如 `LimitPercent = -1` 表示未知）并走缓存，或显式缓存"null/失败"结果：
  ```csharp
  // 失败/未实现也缓存，避免每秒重试
  lock (_cacheLock)
  {
      _cachedStatus = result;   // 允许 null
      _statusTime = DateTime.Now;
  }
  ```
  调用方已对 null 做了判空（TrayContext.cs:259 `if (status != null)`），缓存 null 安全。

### 2. DDC 亮度映射整数除法误差

- **位置**：`src/Traynexus/BrightnessController.cs:276`
- **类型**：逻辑缺陷（数值计算）
- **影响**：`(int)(cur * 100 / range) - (int)(min * 100 / range)` 分别对两个除法做整数截断，与正确的 `(int)((cur - min) * 100 / range)` 在某些 min/cur/range 组合下相差 1。例如 min=2, max=5, range=3, cur=3：
  - 当前公式：`(3*100/3) - (2*100/3) = 100 - 66 = 34`
  - 正确公式：`(3-2)*100/3 = 33`
  亮度显示偏差 1%，滑块与实际亮度轻微错位。
- **代码**：
  ```csharp
  // BrightnessController.cs:274-278
  uint range = max - min;
  if (range > 0)
      info.Brightness = (int)(cur * 100 / range) - (int)(min * 100 / range);
  ```
- **修复建议**：
  ```csharp
  uint range = max - min;
  if (range > 0)
      info.Brightness = (int)((cur - min) * 100 / range);
  else
      info.Brightness = (int)cur;
  ```

### 3. ManagementObject 普遍未 Dispose（WMI COM 对象缓慢泄漏）

- **位置**：多处
  - `BrightnessController.cs:67`、`98`、`155`、`193`（`foreach (var mo in searcher.Get())`）
  - `BatteryInfo.cs:82`、`113`、`158`、`173`、`209`
  - `OemChargeController.cs:495`、`507`（`ReadManufacturer` / `ReadModel`）
- **类型**：资源泄漏（COM 对象）
- **影响**：`ManagementObject` / `ManagementBaseObject` 实现 `IDisposable`，底层是 COM 对象。`foreach (var mo in searcher.Get())` 拿到的 `mo` 不 Dispose，靠 GC 最终回收。短期影响小，但 TrayContext.TickRefresh 每秒触发 BrightnessController.GetBrightness（5s 缓存后约每 5s 一次）+ BatteryTick（5s 一次），长期运行 COM 对象累积，增加 GC 压力与内存占用。
- **代码**（示例）：
  ```csharp
  // BrightnessController.cs:67-71
  foreach (var mo in searcher.Get())
  {
      value = Convert.ToInt32(mo["CurrentBrightness"]);
      break;   // ← mo 未 Dispose
  }
  ```
- **修复建议**：所有 `foreach (var mo in searcher.Get())` 改为显式 Dispose：
  ```csharp
  foreach (var mo in searcher.Get())
  {
      try { value = Convert.ToInt32(mo["CurrentBrightness"]); }
      finally { try { mo.Dispose(); } catch { } }
      break;
  }
  ```
  或用 `using` 包裹单个 `mo`（注意 `break` 前必须 Dispose）。

### 4. UpdateChecker.Check 无实际超时控制

- **位置**：`src/Traynexus/UpdateChecker.cs:35-52`
- **类型**：健壮性问题（注释与实现不符）
- **影响**：注释第 35 行写"超时 8 秒"，但 `WebClient.DownloadString` 没有超时属性，底层 `HttpWebRequest` 默认超时 100s。GitHub API 慢或网络不通时，后台线程会挂起最长 100s。虽不阻塞 UI（在 ThreadPool 调用），但与注释承诺不符，且 24h 节流逻辑会延迟。
- **代码**：
  ```csharp
  // UpdateChecker.cs:35
  /// 同步检查最新版本（应在后台线程调用）。
  /// 超时 8 秒。网络异常/解析失败返回 HasUpdate=false。
  ...
  // UpdateChecker.cs:52
  json = wc.DownloadString(RepoApiUrl);   // ← 无超时，默认 100s
  ```
- **修复建议**：改用 `HttpWebRequest` + `Timeout`，或用 `Task.Run` + `Task.WaitAsync`（C# 5 不支持，需自己实现）：
  ```csharp
  var req = (HttpWebRequest)WebRequest.Create(RepoApiUrl);
  req.Timeout = 8000;
  req.ReadWriteTimeout = 8000;
  req.UserAgent = "Traynexus/" + CurrentVersion;
  req.Accept = "application/vnd.github+json";
  using (var resp = req.GetResponse())
  using (var sr = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8))
  {
      json = sr.ReadToEnd();
  }
  ```

### 5. build.bat / build_debug.bat 引用未使用的 System.Management.Automation.dll

- **位置**：`build.bat:34`、`build_debug.bat:22`
- **类型**：构建配置缺陷
- **影响**：两个 .bat 都引用 `C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll`，但：
  1. 全量 grep `powershell|System.Management.Automation|Runspace|Pipeline|PSObject` 在 .cs 源码中**零命中**——代码完全不用 SMA。
  2. `build.rsp`（另一个构建入口）**不引用** SMA.dll，与两个 .bat 不一致。
  3. 若构建机器未装 Windows Management Framework（SMA.dll 不在 GAC），`build.bat` 会因找不到引用直接编译失败。
  4. 之前可能用 PowerShell 做过电池报告/自动亮度，现已全部移除，SMA 引用是历史残留。
- **代码**：
  ```bat
  REM build.bat:34（节选）
  /reference:Microsoft.CSharp.dll /reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll /out:bin\Traynexus.exe
  ```
- **修复建议**：从 `build.bat` 与 `build_debug.bat` 中删除 `/reference:...System.Management.Automation.dll` 一段，与 `build.rsp` 保持一致。

### 6. OemChargeController.SetChargeLimit Lenovo 快充模式为死代码

- **位置**：`src/Traynexus/OemChargeController.cs:171-173`
- **类型**：死代码 / 逻辑不一致
- **影响**：第 171 行 `uint[] cmds = percent >= 80 ? LENOVO_MODE_NORMAL : LENOVO_MODE_CONSERVATION;` 只在 NORMAL（>=80）和 CONSERVATION（<80）之间二选一，**RAPID 模式永不触发**。第 172-173 行 `cmds == LENOVO_MODE_RAPID ? 1` 永远为 false，`expectedMode` 永远是 0 或 2。UI 上没有"快充"入口，Lenovo 用户实际只能用保养/正常两档。这是设计决策还是遗漏不明确，但代码里的 RAPID 分支是死代码。
- **代码**：
  ```csharp
  // OemChargeController.cs:171-173
  uint[] cmds = percent >= 80 ? LENOVO_MODE_NORMAL : LENOVO_MODE_CONSERVATION;
  int expectedMode = (cmds == LENOVO_MODE_CONSERVATION) ? 0 :
                     (cmds == LENOVO_MODE_RAPID) ? 1 : 2;   // ← LENOVO_MODE_RAPID 永不命中
  ```
- **修复建议**：若不打算支持快充，删除 `LENOVO_MODE_RAPID` 常量与三目里的中间分支，简化为 `expectedMode = (cmds == LENOVO_MODE_CONSERVATION) ? 0 : 2;`。若要支持，需在 UI 增加快充入口并在 `cmds` 选择里加入 RAPID 分支。

---

## P3 - 改进（规范 / 可读性 / 微优化）

### 1. app.manifest 版本号陈旧

- **位置**：`src/Traynexus/app.manifest:3`
- **类型**：版本不一致
- **影响**：`<assemblyIdentity version="1.7.17.3" name="Traynexus"/>` 与产品版本 1.0725.2 完全不相关。manifest 版本号对运行无影响，但属版本元数据漂移。
- **修复建议**：改为 `version="1.0725.2.0"`（4 段）与产品版本对齐。

### 2. TrayContext.BuildMenu 创建的 Font 未 Dispose

- **位置**：`src/Traynexus/TrayContext.cs:173`
- **类型**：GDI+ 资源泄漏（轻微）
- **影响**：`consoleItem.Font = new Font(consoleItem.Font, FontStyle.Bold);` 每次启动创建一个 Font，`ContextMenuStrip.Dispose()` 不释放其 Items 的 Font。项目已有 `Fonts` 静态类专门解决此类泄漏，此处遗漏。
- **修复建议**：改为 `consoleItem.Font = Fonts.S9B;`。

### 3. ReleasePanel._loading.Font 未 Dispose

- **位置**：`src/Traynexus/ReleasePanel.cs:177`
- **类型**：GDI+ 资源泄漏（轻微）
- **影响**：`_loading.Font = new Font(this.Font.FontFamily, 11f);` 每次打开释放面板创建一个 11pt Font，窗体关闭时不 Dispose。`Fonts` 静态类没有 11pt 档位，可加一个或直接用 `Fonts.S11`。
- **修复建议**：在 `Fonts` 中加 `public static readonly Font S11 = new Font(Fnt, 11f);`（已存在，直接用），此处改 `_loading.Font = Fonts.S11;`。

### 4. IconRenderer.Build 接收但不再使用 battPercent / isCharging

- **位置**：`src/Traynexus/IconRenderer.cs:41`
- **类型**：死参数
- **影响**：v1.0725.2 移除自动亮度后，图标不再绘制电池信息，但 `Build(int memPercent, int battPercent, bool isCharging)` 签名保留，注释说"保留参数兼容但不再用于绘图"。所有调用方（TrayContext.cs:281、MainForm.cs:100、ReleasePanel.cs:600）仍传入 `_battery.Percent` / `0` / `false` 等无效值。
- **修复建议**：若没有外部调用方依赖此签名，可简化为 `Build(int memPercent)`，同步更新 3 处调用点。若担心兼容，保留现状但删除注释里的"保留参数兼容"误导。

### 5. Settings.Log 日志截断字节与字符混用

- **位置**：`src/Traynexus/Settings.cs:41-42`
- **类型**：边界 case 健壮性
- **影响**：`int keepChars = (int)Math.Min(full.Length, KeepLogBytes);` 中 `KeepLogBytes = 512*1024`（字节），`full.Length` 是 UTF-16 char 数。对纯 ASCII 日志 1 char ≈ 1 byte，近似正确；但中文/特殊字符 UTF-8 编码后字节数大于字符数，截断点会偏后（保留的实际字节数略超 512KB）。影响很小，日志功能不受损。
- **修复建议**：若要精确，改为按字节截断：先 `byte[] all = File.ReadAllBytes(LogPath)`，再按字节偏移切分。当前实现可接受，标注注释说明是"按字符近似"即可。

---

## 已知问题复核

| # | 历史问题 | 当前状态 | 证据 |
|---|---------|---------|------|
| 1 | `BrightnessController.SetBrightness` 第 100 行 `mo.InvokeMethod("WmiSetBrightness", ...)` 返回值被丢弃，WMI 写失败也报成功 | **仍存在**（已升级为 P1-3） | `BrightnessController.cs:100` 代码未变，返回值仍未检查 |
| 2 | `BrightnessController.GetBrightness` 有 5 秒缓存 | **仍存在**（已实现，正常工作） | `BrightnessController.cs:36-39`、`54-84` 缓存逻辑完整，`SetBrightness` 成功后调 `InvalidateBrightnessCache` |
| 3 | `ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE ClassGuid='...' AND Status='OK'")` 在本机 PowerShell 报"无效查询" | **已随自动亮度功能移除** | 全量 grep `Win32_PnPEntity`/`ClassGuid`/`ProbeSensorViaWmi` 在 .cs 源码中零命中。`AdaptiveBrightnessController.cs` 与 `LightSensorReader.cs` 已从 build.rsp 与源目录删除 |
| 4 | v1.0725.2 移除自动亮度功能后是否有残留引用 / 悬空字段 / UI 控件 / Settings 序列化字段 | **.cs 源码无残留** | 全量 grep `Adaptive|LightSensor|autoBright|ALS` 在 17 个 .cs 中零命中。Settings.cs 无自动亮度相关字段。仅 `src/ui/traynexus-ui.html`（HTML mockup，不参与编译）残留 `autoBrightSw` 元素，不影响运行 |
| 5 | WinForms Timer + ThreadPool 后台任务混合，跨线程访问 UI 控件 | **未发现违规** | 后台线程统一用 `this.PostToUi(...)`（TrayContext，经 `UiMarshal._anchor.BeginInvoke`）或 `this.BeginInvoke(new Action(...))`（MainForm/ReleasePanel/QuickForm）回 UI 线程。`_batterySampling` / `_autoReleasing` 均为 `volatile`，UI 线程 check+set 原子，后台线程只 reset，无竞态。`IsDisposed` 在所有 `BeginInvoke` 回调前均有判空 |

---

## 附：审查覆盖矩阵

| 文件 | 已读 | 主要关注点 |
|------|------|-----------|
| Program.cs | ✓ | 单实例 Mutex、AbandonedMutex 处理正确 |
| TrayContext.cs | ✓ | Timer 生命周期、PostToUi 模式、Dispose 漏调 Cleanup |
| MainForm.cs | ✓ | 版本号、刷新定时器、自定义控件 Dispose |
| QuickForm.cs | ✓ | Layered Window GDI 句柄释放正确 |
| Settings.cs | ✓ | 白名单锁、原子写入 |
| MemoryInfo.cs | ✓ | MEMORYSTATUSEX 调用 |
| MemoryCleaner.cs | ✓ | 进程枚举 Dispose、特权提升 |
| ReleasePanel.cs | ✓ | Font 泄漏、ListView 线程安全 |
| BatteryInfo.cs | ✓ | WMI ManagementObject 未 Dispose、powercfg 缓存 |
| OemChargeController.cs | ✓ | ASUS null 不缓存、Lenovo 死代码、锁顺序无死锁 |
| BrightnessController.cs | ✓ | **句柄泄漏 + Cleanup 未调用 + 返回值丢弃** |
| IconRenderer.cs | ✓ | Icon 缓存 + GetHicon/DestroyIcon 正确 |
| Fonts.cs | ✓ | 静态共享 Font 设计正确 |
| NativeMethods.cs | ✓ | P/Invoke 签名、GetWindowLongPtr 64 位兼容 |
| AutoStartManager.cs | ✓ | **进程超时孤儿泄漏** |
| ConfigMigrator.cs | ✓ | 迁移逻辑、目录删除 try/catch |
| UpdateChecker.cs | ✓ | **版本号陈旧 + 无超时** |
| build.rsp / build.bat / build_debug.bat | ✓ | **SMA.dll 残留引用** |
| installer/Traynexus.iss | ✓ | 版本号正确（1.0725.2） |
| app.manifest | ✓ | 版本号陈旧 |
