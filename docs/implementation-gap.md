# 实现缺口清单（Implementation Gap）

> 对照 `deliverables/` 规划文档与当前代码实际状态。
> 最后核对日期：2026-07-14

本文档是「下一步该干什么」的权威清单。读完它就能知道项目卡在哪、从哪接起。

---

## 一、一句话结论

**桥接管道已铺好（C# 侧 `AddHostObjectToScript` + JS 侧 `TraynexusBridge` 封装都到位），但 UI 里没有一处真正调用它。** 所有按钮、滑块、表格交互仍停留在原型阶段的纯前端 mock（写死数据 + `setTimeout` 假动画）。此外电池/亮度/调度的后端方法尚未编写。

用架构文档的任务编号说：**T07-T08 完成，T09-T13 未做。**

---

## 二、三层对照表

### 2.1 桥接方法：C# 已实现 ↔ JS 已封装 ↔ UI 已调用？

| 模块 | BridgeApi.cs 方法 | bridge.js 封装 | UI 实际调用 | 缺口 |
|------|-------------------|----------------|-------------|------|
| 内存 | `GetMemorySnapshot()` | `getMemorySnapshot()` | ❌ 无 | UI 写死 5.8/16GB、36%，未拉真实数据 |
| 内存 | `ExecuteRelease()` | `executeRelease()` | ❌ 无 | `quickRelease()` / `rpExec()` 用 `setTimeout` 假装成功 |
| 内存 | `PreviewTargets()` | `previewTargets()` | ❌ 无 | `rpRender()` 渲染写死的 `PROCS` 数组（15个假进程） |
| 设置 | `GetSettings()` | `getSettings()` | ❌ 无 | 页面加载时不读后端配置 |
| 设置 | `UpdateSettings(mode,thrEn,thrPct)` | `updateSettings(...)` | ❌ 无 | `selMode()` / 阈值对话框只改 DOM，不持久化 |
| 白名单 | `GetWhitelistContent()` | `getWhitelistContent()` | ❌ 无 | 「编辑白名单」按钮无动作 |
| 白名单 | `SaveWhitelist(names)` | `saveWhitelist(names)` | ❌ 无 | `rpPersist()` 空操作 |
| 自启 | `GetAutoStartState()` | `getAutoStartState()` | ❌ 无 | 通用设置的自启开关不读状态 |
| 自启 | `SetAutoStart(enable)` | `setAutoStart(enable)` | ❌ 无 | 拨动开关不生效 |
| 文件 | `OpenConfigFolder()` | `openConfigFolder()` | ❌ 无 | 「打开配置文件夹」`<a>` 无 onclick |
| 文件 | `OpenWhitelistInNotepad()` | `openWhitelistInNotepad()` | ❌ 无 | 「编辑白名单文件」`<a>` 无 onclick |
| 迁移 | `CheckMigration()` | `checkMigration()` | ❌ 无 | 启动时不检测旧 MemTrayCN 配置 |
| URL | `OpenUrl(url)` | `openUrl(url)` | ❌ 无 | GitHub/反馈链接用 `window.open`（在 WebView2 内可能受限） |
| 电池 | `GetBatteryInfo()` | `getBatteryInfo()` | ❌ 无 | 且该方法本身是占位桩 `{"percent":0}` |

**结论：12 个已封装的桥接方法，UI 调用数为 0。**

### 2.2 后端方法：尚不存在

| 模块 | 需要的能力 | C# 现状 | 对应 PRD |
|------|-----------|---------|----------|
| 电池采集 | `Win32_Battery` WMI 查询（电量/充电/容量/循环/温度） | ❌ 无 `BatteryInfo.cs`，`GetBatteryInfo` 写死 | R21 |
| 充电阈值 | OEM WMI（Lenovo/Dell/HP/ASUS）设置充电上限 | ❌ 无 | R19/R20 |
| 亮度枚举 | `EnumDisplayMonitors` + DDC/CI | ❌ 无 | R28 |
| 亮度调节 | WMI `WmiMonitorBrightnessMethods` 或 DDC/CI | ❌ 无 | R28 |
| 任务调度 | 规则引擎（时间/事件触发） | ❌ 无，UI 表格是写死 HTML | R24-R26 |
| 电池校准 | 满充→放电→再满充流程 | ❌ 无 | R30 |
| UI 推送 | 后端定时器 → `PostWebMessageAsJson` 推送内存/电池到前端 | ❌ 无推送逻辑 | T06/T12 |

### 2.3 架构文档计划 vs 代码现实的结构偏差

| 计划（架构文档 T09-T11） | 实际代码 | 偏差 |
|--------------------------|---------|------|
| `src/ui/css/style.css` 独立样式 | 全部内联在 `traynexus-ui.html` | 不利于维护，但不影响功能 |
| `src/ui/js/app.js` 应用逻辑 | 内联在 HTML `<script>` | 同上 |
| `src/ui/js/release-panel.js` 释放面板 | 内联 | 同上 |
| `src/ui/js/ultracode.js` 引擎 | 内联（已用 Canvas 重构） | 同上 |
| 前端调用 `BridgeApi.ExecuteRelease` | mock | **核心缺口** |

---

## 三、按优先级排序的接线 TODO

### 🔴 P0-1：内存释放主链路接线（让核心功能真正可用）

这是最小可用闭环，完成后「内存释放」整条线就活了。

**UI 侧改动（`src/ui/traynexus-ui.html` 内联 JS）：**

1. **页面初始化拉真实内存**
   - 在 `initUltracode(...)` 调用前，`await TraynexusBridge.getMemorySnapshot()`
   - 用返回的 `usedPercent` / `usedDisplay` / `totalDisplay` 替换写死的 36% / 5.8/16.0 GB
   - 用 `usedPercent` 作为 `initUltracode` 的第三个参数（扫描位置）

2. **速览面板「一键释放内存」**
   - `quickRelease(btn)` 改造：旋转圈阶段调用 `TraynexusBridge.executeRelease()`
   - 用返回的 `freedDisplay` / `afterDisplay` 更新对号后的文案
   - 失败时（返回含 `error`）显示红色提示而非绿对号

3. **释放面板进程列表**
   - 删除写死的 `PROCS` 数组
   - `rpRender()` 改为 `await TraynexusBridge.previewTargets()` 后渲染真实进程
   - 字段映射：`name`/`pid`/`workingSetDisplay`/`protected`/`protectReason`
   - 「将被释放」状态由 `protected===false` 判定

4. **释放面板「立即释放」**
   - `rpExec()` 改为调用 `executeRelease()`，用返回的 `trimmedCount`/`freedDisplay` 填充结果弹窗

5. **充电模式选择持久化**
   - `selMode(m)` 末尾追加 `TraynexusBridge.updateSettings(m, ...)`
   - 页面加载时调 `getSettings()` 回填当前模式高亮

### 🔴 P0-2：设置项接线

6. **阈值对话框** - 「确定」时调 `updateSettings(mode, true, value)`
7. **释放方式三开关** - `toggleMode` 后把选择写入设置（需扩展 Settings 支持 releaseMethod 字段）
8. **自启开关** - `GetAutoStartState` 回填 + `SetAutoStart` 写入
9. **白名单** - 「编辑白名单文件」`openWhitelistInNotepad()`、「打开配置文件夹」`openConfigFolder()`
10. **持久化** - `rpPersist()` 调 `saveWhitelist()` 把勾选的「内存保持」进程写入

### 🟡 P1-1：电池采集（后端 + 接线）

11. **新增 `BatteryInfo.cs`** - WMI `SELECT * FROM Win32_Battery`，返回电量/充电状态/（若可读）容量
12. **改 `BridgeApi.GetBatteryInfo()`** - 调用 `BatteryInfo.Take()` 而非返回占位值
13. **UI 接线** - 速览面板电池区块、概览电池卡片、托盘内环弧长都改读真实数据
14. **后端推送** - `TrayContext._batteryTimer`(5s) → `WebViewPanel.PostMessage` 推电池 JSON，前端 `Bridge.onMessage` 更新

### 🟡 P1-2：充电阈值（依赖 OEM，需降级）

15. **OEM 检测 + WMI 设置** - Lenovo/Dell/HP/ASUS 分支
16. **降级 UI** - 不支持时充电模式三选项灰显 + Tooltip（见 PRD 四-5）

### 🟡 P1-3：任务调度

17. **后端规则引擎** - 规则持久化（settings.ini 扩展）+ 定时器/事件触发
18. **UI 接线** - 规则表格从写死 HTML 改为后端拉取；增删改调桥接

### ⚪ P2：亮度 / 校准 / 安装包

19. **亮度** - DDC/CI 或 WMI，硬件兼容性差，首版可不做（PRD 已标 P2）
20. **电池校准** - 流程长，P2
21. **安装包** - Inno Setup（T13）

---

## 四、接线时的技术注意点

1. **hostObject 返回值是同步代理**：`bridge.js` 已用 `Promise.resolve(raw).then(...)` 做了容错，但 `BridgeApi` 返回的 JSON 字符串在不同 WebView2 版本可能表现为 `ByReference` 对象，需 `.toString()`。封装层已处理，UI 侧直接 `await` 即可。

2. **C# 侧是同步阻塞**：`ExecuteRelease` 会阻塞 WebView2 UI 线程（工作集削减遍历进程）。释放期间前端动画会卡顿。**建议**：要么接受短暂卡顿（释放通常 <1s），要么把 `ExecuteRelease` 内部搬到线程池 + 通过 `PostWebMessageAsJson` 回推结果（改动较大，P0 阶段可先用同步）。

3. **`MemorySnapshot` 类型**：`BridgeApi.GetMemorySnapshot` 返回的字段名见 `BridgeApi.cs:43-57`（`usedPercent`/`usedDisplay`/`totalDisplay`/`availDisplay` 等），前端按此取值。

4. **`PreviewTargets` 返回数组**：注意 `workingSet` 是字节数（long），`workingSetDisplay` 是格式化字符串（如 "1.2 GB"）。前端表格可直接用 `workingSetDisplay`。

5. **前端目前全内联在 HTML**：接线时直接改 `src/ui/traynexus-ui.html` 的 `<script>` 段即可，无需拆分文件（拆分是优化，非必需）。改完记得 `build.bat` 会把 `src/ui` 复制到 `bin/ui`。

6. **测试方法**：构建后运行 `bin/Traynexus.exe`，在 WebView2 里按 F12 打开 DevTools，Console 里手动执行 `await TraynexusBridge.getMemorySnapshot()` 验证桥接是否通，再看 UI 是否调用了它。

---

## 五、验证清单（接线完成后逐项勾）

- [ ] 速览面板内存数字 = 真实内存（非写死 36%）
- [ ] 点「一键释放内存」后内存数字真的下降
- [ ] 释放面板进程列表 = 真实进程（非 15 个假数据）
- [ ] 切换充电模式后重启应用，模式保持
- [ ] 自启开关拨动后，任务计划程序里出现/消失 `Traynexus_AutoStart`
- [ ] 「打开配置文件夹」能打开 `%APPDATA%\Traynexus`
- [ ] DevTools Console 无 `TraynexusBridge` 报错
