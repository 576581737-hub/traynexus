# TrayNexus Changelog

本项目采用语义化版本编号，日期使用项目时钟（`2026-07-*` 迭代序列）。

---

## v1.0721.0 - 2026-07-21（首个公开 Release：安装版 + 便携版 + 仓库清理）

### 发布
- 首个公开 GitHub Release（tag `v1.0721.0`），资产：
  - 安装版 `Traynexus-Setup-1.0721.0.exe`（Inno Setup 6 构建）
  - 便携版 `Traynexus-Portable-1.0721.0.zip`（解压即用）
- 安装包 / 卸载程序 / 桌面及开始菜单快捷方式图标统一为 `resources/tray_default.ico`
  （`SetupIconFile` + `UninstallDisplayIcon={app}\Traynexus.exe` + 快捷方式 `IconFilename` 一致）

### 仓库清理
- 修正 `.gitignore` 排除规则：原行内注释（`pattern  # comment`）导致模式失效，改为独立注释行
- 移出误跟踪的内部目录：`.zcode/` `deliverables/` `_attic/`（`git rm --cached`，本地保留）
- 移出无关文件：`src/ui/`（WebView 原型残留，无宿主加载）、`docs/` 内部审计/规划文档、`logo_512.png`（未嵌入构建）
- 新增 `.gitattributes`（强制 `*.bat`/`*.cmd` 用 CRLF，根治历史 build.bat 闪退）
- 新增 GitHub Actions 自动构建（`.github/workflows/build.yml`，`csc @build.rsp`）

### 文档
- README 重写：版本徽章对齐 v1.0721.0、目录结构修正（移除已删文件、补 Fonts.cs/installer）、功能特性补充计划任务（✅ 已接入）、新增 Releases 下载入口、英文摘要同步

### 已知待办（本轮重新编译时处理）
- 应用内版本标签（`MainForm.cs` `lblVer` 仍为 `v1.0717.3`）与发布号 `v1.0721.0` 待统一，提权重新构建后对齐

---

## v1.0720.4 - 2026-07-20（Font 静态共享 + GitHub 开源链接）

### P1 资源泄漏修复（审计 P1-1）
- **新增 `Fonts` 静态类**（`src\Traynexus\Fonts.cs`）：集中持有 15 档共享 Font 实例（8f~22f，Regular/Bold）
- **MainForm.cs 74 处 `new Font(...)` 全部替换为 `Fonts.XX` 引用**：消除 GDI+ 句柄缓慢上涨
  - 原本 `new Font()` 挂在控件属性上，WinForms 控件 Dispose 不会自动释放 Font
  - 改为静态共享后整进程只 new 一次，进程退出由 OS 回收
- **QuickForm.cs 4 处 Paint 内 Font 纳入共享**：`fontTitle/fontPct/fontDetail/fontBtn` 改引用 Fonts，去掉 finally 里的 Dispose
- **修复 OnPaint 内 `using (var x = Fonts.XX)` 陷阱**：4 处原 `using` 包裹的 Font 改为直接传参，避免对共享实例误调 Dispose 导致后续 ObjectDisposedException

### 工程优化
- **GitHub 链接改为真实开源地址**：关于页「问题反馈」「GitHub」两处链接从占位 `github.com/traynexus/traynexus` 改为 `github.com/576581737-hub/traynexus`
- **构建脚本同步**：`build.bat` / `build_debug.bat` / `build.rsp` 均追加 `src\Traynexus\Fonts.cs`

### 产物
- `bin/Traynexus.exe`（300544 字节，0 error 0 warning）

### 未修复（留下个迭代）
- **P1（外部报告 #7）**：ASUS `QueryAsusLimit` 真正实现，需真实 ASUS 硬件验证 DSTS 返回值位解析
- **P3-1**：MainForm.cs 拆文件（当前 3294 行 -> Controls/ + Pages/），纯工程优化不影响运行时

---

## v1.0720.3 - 2026-07-20（代码审计修复第二批：性能优化 + 资源泄漏 + 健壮性）

### P1 性能优化
- **OemChargeController.GetStatus**：新增 10s 缓存，避免 TickRefresh 每秒打开/关闭设备句柄
- **OemChargeController.SetChargeLimit**：新增 `_ioctlLock` 互斥锁，防止滑块拖动/并发操作同时 IOCTL
- **SetChargeLimit 成功后清空 GetStatus 缓存**，让下次读取立即拿到新值
- **MainForm 充电页滑块**：新增 500ms debounce，拖动停下后才真正发 IOCTL（之前每帧都发）
- **TrayContext.BatteryTick**：改为后台线程采集 WMI（可能耗时 100-500ms），UI 线程只更新结果，避免卡 UI
- **BatteryInfo.FillDeepData**：新增 30s 缓存（容量/循环/制造商/序列号变化缓慢），之前 BatteryTick 5s 一次每次都查 4 个 WMI 类

### P1 资源泄漏
- **IconRenderer.RenderIcon:153**：`new SolidBrush(textColor)` 直接传参无人 Dispose → 改 `using` 包裹

### P2 健壮性
- **Settings 锁外 I/O**：`PersistWhitelist` / `RemovePersisted` / `ReloadWhitelist` 文件 I/O 挪出 `WhitelistLock`，避免阻塞后台释放线程；保留「失败回滚只撤回本次新增项」语义
- **Settings.Save()**：返回值从 `void` 改为 `bool`，与 `WriteWhitelistFile` 设计一致；写盘失败时记日志
- **BatteryInfo.GetDeepDataFromPowercfg**：powercfg 子进程同时 `RedirectStandardOutput` + `BeginOutputReadLine` 异步消费，超时 5s `Kill()`，防止缓冲区满阻塞
- **BatteryInfo Win32_Battery status=3**：兜底逻辑改为仅 status=2/6/7/8/9 视为充电中（status=3 是「已充满」不算），避免 root\wmi 不可用时已充满电池被误显为充电中
- **Program.cs Mutex**：改用 `initiallyOwned=false` + `WaitOne(0)` 抢锁，捕获 `AbandonedMutexException`，上个进程崩溃后新实例可正常接管（之前会误报"已在运行"）
- **删除死代码 `ShowAbout()`**：CHANGELOG v1.0717.3 标记保留备用，但实际从未被调用，清理减少维护负担
- **AutoStartManager.Disable**：检查 `ExitCode == 0` 才返回 true，schtasks 失败不再误报成功

### P3 兼容性 / 维护性
- **NativeMethods.GetWindowLong/SetWindowLong**：声明改为 Ptr 版本（`GetWindowLongPtrW` / `SetWindowLongPtrW`），通过 IntPtr.Size 自动选择 32/64 位实现，64 位系统上更稳妥
- **Settings.Log**：error.log 超过 1MB 自动截断为只保留最近 512KB，避免无限增长
- **app.manifest**：移除 Win 7/8/8.1 `supportedOS` 声明（项目用了 Win 10+ WMI 类），避免老系统用户误装

### 产物
- `bin/Traynexus.exe`（约 297 KB，0 error 0 warning 待用户本地验证）

---

## v1.0720.2 - 2026-07-20（代码审计修复：构建脚本 + 配置迁移 + 充电模式回滚 + 安装引导统一）

### P0 构建脚本修复
- **build.bat**：补上漏编的 `src\Traynexus\BrightnessController.cs`（之前直接编译会报 CS0103）
- **build_debug.bat**：补上 `BrightnessController.cs` + 4 个 `/resource:` 参数（logo_256/logo_128/github_icon/github_icon_white）
  - 之前调试版运行起来窗口图标、品牌图、GitHub 图标全部加载失败

### P1 功能修复
- **ConfigMigrator.Migrate**：迁移成功后自动删除旧目录 `%APPDATA%\MemTrayCN`
  - 之前只复制不删除，导致 `ConflictDetected()` 每次启动都返回 true，每次启动都弹冲突气泡
  - 旧目录删除失败不影响迁移结果，仅日志记录
- **SelectChargeMode 失败回滚**：IOCTL 设置失败时，`_settings.ChargeMode`/`ChargeLimit` 回滚到旧值并重新 `Save()`
  - 之前先持久化后执行，IOCTL 失败时重启后 UI 显示的模式与实际硬件状态不一致
  - 不支持的机型进入 SelectChargeMode 时也不再持久化新设置
- **Lenovo 安装引导文案统一**：UI 引导从"必须装 557MB Vantage"改回"只需 5MB Energy Management 驱动"
  - 与 `OemChargeController.cs` 注释 + `README.md` 描述一致
  - 之前三方话术矛盾：代码注释/README 说不要 Vantage，UI 引导说必须装 Vantage

### P2 健壮性修复
- **AutoStartManager.Disable**：检查 `ExitCode`，schtasks 删除失败时返回 false
  - 之前不检查退出码，失败也返回 true，用户以为关掉了实际没关

### 产物
- `bin/Traynexus.exe`（同 v1.0720.1 体积，约 297 KB，0 error 0 warning）

---

## v1.0720.1 - 2026-07-20（Lenovo 充电控制 IOCTL 验证通过 + 诊断页修复 + 全功能验证）

### Lenovo 充电控制最终方案
- **只需 5MB 的 Energy Management 驱动**（AcpiVpc.sys），不需要 557MB 的 Vantage
- `\\.\EnergyDrv` 设备 + IOCTL `0x831020F8`，双命令 `{0x08, 0x03}` 设保养模式
- bridge_test.exe 验证：回读 `state=0x000E0022 保养=True`，`ChargeRate=0` 充电暂停
- 之前失败的根因：未安装 EM 驱动，设备虽能打开但 IOCTL 不被正确执行
- ProbeLenovo 改为检测 `\\.\EnergyDrv` 设备能否打开，不再依赖 Vantage/VantageService

### 充电状态精确判断
- `BatterySnapshot.ChargeRate` 新增字段，从 `root\wmi\BatteryStatus` 采集
- 用 `ChargeRate > 0` 判断是否真正充电（之前用 `BatteryStatus=2` 是错的）
- 保养模式下 `BatteryStatus=2` 但 `ChargeRate=0`，Windows 显示「充电中」是系统限制
- 充电状态行显示「保养中·暂停充电」（蓝色）或「充电中」（绿色）

### 诊断页修复
- TagLabel 去掉 `SupportsTransparentBackColor`，改用卡片底色实色背景
- 修复 `_summaryTag.BackColor = Color.Transparent` 导致的崩溃
- 状态标签恢复右侧垂直居中（不另起一行）
- 第三行「充电控制功能」副标题可换行（`wrapDesc` 参数），右侧留 120px 避免遮挡
- 标题和副标题宽度统一右侧留 120px

### 托盘提示改进
- 去掉「Traynexus - 」前缀
- 加入亮度显示
- 保养模式时显示「保养中」标识

### 安装引导改进
- ShowDriverInstallGuide 按厂商显示具体软件名和安装步骤
- 亮度不可用时弹窗说明原因和解决方法
- OpenDriverDownload 跳转厂商驱动下载页（非官网首页）

### 全功能验证
- 标题栏：内存/电量/亮度真实数据 ✅
- 概览页：4 张卡片（内存/电池/亮度/健康）真实数据 + 2s 刷新 ✅
- 充电页：模式卡/滑块/健康卡/状态行/健康报告/电池校准 ✅
- 亮度页：显示器枚举/滑块实时调/自动开关持久化 ✅
- 设置页：开机自启/内存设置/计划 toggle 持久化/关于 ✅
- 诊断页：4 张折叠卡片 + OEM 检测 + 安装引导 ✅
- 托盘：提示/图标/左键速览/右键菜单/退出无残留 ✅

### 已知限制
- 保养模式不主动放电（电量 95% > 60% 时只暂停充电，等自然消耗到 60% 后维持）
- Windows 系统电池图标显示「充电中」是系统限制（只看 BatteryStatus 不看 ChargeRate）
- 计划任务 toggle 已持久化，定时执行逻辑未接入

### 产物
- `bin/Traynexus.exe` 298 KB（0 error 0 warning）

---

## v1.0718.1 - 2026-07-18（功能接线：电池深度数据 + 亮度控制 + 诊断面板 + 设置持久化）

### 新增文件
- **BrightnessController.cs**：WMI WmiMonitorBrightnessMethods 实现内置屏亮度读写
- **诊断页**：侧边栏第 5 项「诊断」，4 张可折叠卡片（内存/电池/OEM充电/亮度）

### 电池深度数据（BatteryInfo.cs 重写）
- BatterySnapshot.HealthPercent 新增属性
- powercfg XML 兜底：WMI 取不到时调 powercfg /batteryreport /xml 解析，30s 缓存
- 消除概览页/充电页硬编码 mock

### 亮度控制（新建 BrightnessController + 亮度页接线）
- 概览页亮度卡 + 标题栏显示真实亮度
- 亮度页 EnumerateMonitors 动态生成显示器卡片，滑块接 SetBrightness
- 自动亮度开关接 Settings.AutoBrightness 持久化

### 充电页健康卡接线
- ring/lblGood/lblStat/lblStat2 提升为类字段
- 新增 RefreshHealthCard 动态设置环图颜色
- 健康报告按钮生成 powercfg 报告，电池校准显示步骤说明

### MainForm 复用 TrayContext 电池数据
- TrayContext.CurrentBattery internal getter
- 消除 3 处并发 WMI 轮询

### Settings 持久化扩展
- 新增 AutoBrightness/Theme/Language/NightCareEnabled/WeekendFullCharge/MeetingDndMode

### 设置页接线
- 主题/语言 ComboBox -> Settings 持久化
- 计划三 toggle -> Settings 持久化

### OEM 充电控制 IOCTL 重构
- ASUS: \.ATKACPI + IOCTL，40-100% 任意阈值
- Lenovo: \.EnergyDrv + IOCTL，3 档模式
- 不支持时灰显滑块/模式卡

### 退出修复
- ExitApp 改 Application.ExitThread，避免残留进程

### 编译
- 产物 bin/Traynexus.exe 289 KB（0 error 0 warning）

---

## v1.0717.3 — 2026-07-17（UI 视觉打磨收官）

### 图标系统
- **新增 `NavIcon.Activity`**：lucide 心电图折线，用于"电池健康"卡片（替代原来的 Heart）
- **新增 `NavIcon.Cog`**：8 齿波浪曲线齿轮（`AddClosedCurve` tension=0.35），用于侧边栏"设置"（替代原来的 Gear）
- **重写 `DrawIcon`**：
  - Memory：改为 lucide `cpu`（外框 + 内框 + 8 引脚）
  - Battery：改为 lucide `battery`（矩形 + 端子）
  - Sun：8 光线（4 正交 + 4 对角，坐标 4.22/19.78 等 lucide 精确值）
  - Heart：4 段贝塞尔精准复刻 lucide 心形
  - Pen 粗细自适应（≈ `size/15`），`PixelOffsetMode.HighQuality` 抗锯齿保险
- **新增 `PolarPt` 辅助方法**：极坐标转屏幕像素（用于齿轮等圆周分布图标）

### 概览页布局
- 4 张卡片改为**固定行高** `Absolute(186)`，卡内 bar 上下留白对称 18px
- 卡片 `WrapCard.Margin = 0`（原默认 3px 导致卡片比按钮窄 6px）
- 底部按钮 `Margin = (0/8, 0, 8/0, 0)` 与卡片列间距 16px 完全对齐
- 页面 `Padding = (28, 40, 28, 40)` 上下对称，`middleWrap.Resize` 让 grid 在剩余竖直空间垂直居中
- **配色调整**：
  - 内存卡 `CGreen`
  - 电池卡 `#FFB020` 琥珀（更暖，突出电池）
  - 亮度卡 `CBlue`（原 `COrange` → 选中态蓝，视觉一致性更强）
  - 电池健康卡 `CPurple`
- 亮度卡片新增右下角 **"主屏幕"** 标签（`#FFB020` 琥珀，10pt Bold，Resize 事件重定位防裁切）
- 概览卡 `IconBox` 背景改为 `Transparent`，图标线条直接坐在白底卡片上（无淡色圆角底）
- `IconBox.OnPaint`：填父背景色时先切 `SmoothingMode.None + PixelOffsetMode.None`，避免 AA 模式下矩形边缘半像素混色形成横竖细线

### 窗口
- **移除最大化按钮**：`OnHandleCreated` 中通过 `SetWindowLong(GWL_STYLE, & ~WS_MAXIMIZEBOX)` + `SetWindowPos(SWP_FRAMECHANGED)` 强删按钮，比单纯 `MaximizeBox=false` 彻底
- `NativeMethods` 新增：`GetWindowLong` / `SetWindowLong` / `SetWindowPos` 及相关常量

### 托盘
- **移除右键菜单"关于"项**（`TrayContext.CreateContextMenu`），保留「控制台 / 退出」
- `ShowAbout()` 方法保留但不再挂载（便于未来复用）

### 品牌资源
- **重新合成 `resources/tray_default.ico`**：用 `logo_1024.png / logo_512.png` 逐尺寸 LANCZOS 重采样，生成 256/128/64/48/32/16 六尺寸多分辨率 ico（79 KB）
- EXE 文件图标、窗口图标、任务栏图标全部升级为品牌双环闪电 logo

---

## v1.0717.2 — 2026-07-17（充电管理页 & 首帧渲染）

### 充电管理页
- 标题行与提示文字合并为 `capRow`（高 24），hint 右对齐；删除独立 `_bcHint`
- Slider row 收缩 56→46；`RoundSlider.Height` 20→16；子元素 Y 相应下移
- `flow.Padding` 最终为 `(28, 40, 28, 40)`；`health.Margin.Bottom = 0`
- `ModeCard` 内容布局：Icon Y=12, 名称 Y=14, 大百分比 Y=44, 描述 Y=H-26
- 引入 `RoundSlider` 全自绘控件（替代 Win32 TrackBar），防止穿透卡片边框

### 首帧渲染修复
- `NavigateTo` 内 `BeginInvoke` 前加 `IsHandleCreated` 守卫（构造期首次 `NavigateTo(0)` 会因句柄未建抛 `InvalidOperationException`）
- `MainForm` ctor 挂 `Shown` 事件：显示后对 `_content.Controls[0]` 执行 `Invalidate(true) + Update()`
- `NavigateTo` 里 `Controls.Add(page)` 后同样 invalidate，应对页面缓存的双缓冲位图残影
- 解决 RingChart 冷启动时中心竖线脏像素问题

### 设置页
- 删除"自定义任务调度"行，保留"周末满充准备"、"会议免扰模式"

---

## v1.0717.1 — 2026-07-17（卡片描边定案）

### 卡片描边最终方案
- 放弃 `DrawPath` 描边（AA halo 造成双线 + 四角深斑）
- 改为**双层同心 `FillPath`**：
  - 外层 `FillPath(borderColor, RoundedRect(0,0,W,H,R))`
  - 内层 `FillPath(cardColor, RoundedRect(1,1,W-2,H-2,R-1))`
- 无 `DrawPath` → 无 AA 溢出，选中态 border=2px
- 应用于 `RoundedCard` 和 `ModeCard`

### 概览页
- Header 高度 62 → 76
- Title `Size(300,40)` Y=2；Subtitle Y=48
- 底部按钮行 `acts.Height` 52 → 44

### 主窗口
- `MaximizeBox = false`
- `FormBorderStyle = FormBorderStyle.FixedSingle`

---

## v1.0715.x — 2026-07-15（品牌 Logo & 图标资源）

- 新增 TrayNexus 品牌 Logo（三色环 + 闪电）多尺寸 PNG：`logo_128/256/512/1024/2048.png`
- 品牌预览稿：`preview_on_black/blue/gradient/white.png`
- Logo SVG 源文件：`logo.svg`
- GitHub 图标 PNG（黑/白双版）用于关于页

---

## v1.0714.x — 2026-07-14（初始迁移）

- 从 MemTrayCN v1.1 fork，重命名为 TrayNexus
- 初始搭建 WebView2 + HTML UI 骨架（后续放弃，改为纯 WinForms）
- 保留内存清理引擎、电池信息采集等核心后端
- 建立目录结构：`src/` / `resources/` / `deliverables/` / `archive/` / `docs/`

---

## 技术决策记录

### 为何放弃 WebView2 改用纯 WinForms
- **依赖简化**：WebView2 Runtime 虽然是 Evergreen，但仍是 200MB+ 系统组件；纯 WinForms 单 exe ~270KB
- **启动速度**：WinForms 冷启动 <100ms，WebView2 需初始化 Edge 内核 500-1000ms
- **权限一致性**：`requireAdministrator` 下 WebView2 IPC 有额外壁垒
- **调试成本**：C# ↔ JS 桥接层的 bug 定位困难，纯 C# 栈单一
- **代价**：手写自绘控件量增加（当前 `MainForm.cs` ~2500 行）

### 图标为何用 GDI+ 手写而非嵌 PNG
- 运行时可按 accent 动态染色（选中态、hover 变色）
- 高 DPI 无缩放失真
- exe 体积不膨胀（30 图标 × 3 色 × 4KB ≈ 400KB 的开销全省掉）
- 代价：每个图标需精确 port lucide path（~15 分钟/图标）

### 卡片描边为何用双层 FillPath
- `DrawPath` 描边在 `SmoothingMode.AntiAlias` 下 AA 半像素会外溢，与父背景混色形成"halo"晕边
- 四角是圆弧转直边的过渡点，AA 采样密度不均产生"深斑"
- 双层 FillPath 完全避免 DrawPath，边缘由填充色的天然分界保证锐利

---

© 2026 Aiyow
