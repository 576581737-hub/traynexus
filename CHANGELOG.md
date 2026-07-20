# TrayNexus Changelog

本项目采用语义化版本编号，日期使用项目时钟（`2026-07-*` 迭代序列）。

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
