using System;
using System.Drawing;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 托盘应用主上下文：管理 NotifyIcon、定时刷新、面板显示。
    /// 纯 WinForms，无 WebView 依赖。
    /// </summary>
    public class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _batteryTimer;
        private readonly Settings _settings;

        private Icon _currentIcon;
        private int _lastPercent = -1;
        private int _lastBatteryPercent = -1;
        private bool _lastBatteryCharging;

        // 面板（单例，延迟创建）
        private QuickForm _quickForm;
        private MainForm _mainForm;

        // 电池
        private BatterySnapshot _battery = new BatterySnapshot { IsPresent = false };

        /// <summary>获取最新电池快照（供 MainForm 复用，避免 MainForm 各自调 WMI 造成并发轮询）</summary>
        internal BatterySnapshot CurrentBattery { get { return _battery; } }

        // 自动释放
        private volatile bool _autoReleasing;
        private DateTime _lastAutoReleaseTime = DateTime.MinValue;
        private static readonly TimeSpan AutoReleaseCooldown = TimeSpan.FromSeconds(60);

        // 计划任务定时器（夜间保养 + 周末满充）
        private System.Windows.Forms.Timer _scheduleTimer;
        private bool _nightCareActive;       // 夜间保养当前是否激活
        private bool _weekendChargeActive;   // 周末满充当前是否激活

        // 自动亮度定时器（10s 采样环境光，无传感器时不启动）
        private System.Windows.Forms.Timer _autoBrightTimer;
        private int _lastAutoBrightness = -1;  // 上次设置的亮度，避免频繁调 WMI
        private volatile bool _autoBrightSampling;

        // 迁移通知
        private string _pendingMigrationNotice;
        private string _pendingConflictNotice;

        public TrayContext()
        {
            UiMarshal.Init();

            try { AutoStartManager.CleanupOldTask(); }
            catch (Exception ex) { Settings.Log("TrayContext.CleanupOldTask 失败: " + ex.Message); }

            try
            {
                if (ConfigMigrator.NeedsMigration())
                {
                    var result = ConfigMigrator.Migrate();
                    _pendingMigrationNotice = result.Success ? result.Message : ("配置迁移失败: " + result.Message);
                }
                else if (ConfigMigrator.ConflictDetected())
                {
                    _pendingConflictNotice = "检测到旧目录 MemTrayCN 与新目录 Traynexus 同时存在，已使用新目录配置。";
                }
            }
            catch (Exception ex) { Settings.Log("ConfigMigrator 异常: " + ex.Message); }

            _settings = Settings.Load();

            // 托盘图标
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Visible = true;
            _tray.Text = "Traynexus";
            _tray.ContextMenuStrip = BuildMenu();
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ToggleQuickPanel();
            };
            _tray.BalloonTipClicked += (s, e) =>
            {
                // 点击更新气泡 -> 打开 Release 下载页
                if (!string.IsNullOrEmpty(_pendingUpdateUrl))
                {
                    try { System.Diagnostics.Process.Start(_pendingUpdateUrl); } catch { }
                    _pendingUpdateUrl = null;
                }
            };

            ShowPendingNotices();

            // 内存刷新定时器（1秒）
            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += (s, e) => TickRefresh();
            _timer.Start();

            // 电池采集定时器（5秒）
            _batteryTimer = new Timer();
            _batteryTimer.Interval = 5000;
            _batteryTimer.Tick += (s, e) => BatteryTick();
            _batteryTimer.Start();

            TickRefresh();
            BatteryTick();

            // 计划任务定时器（每分钟检查一次夜间保养/周末满充）
            _scheduleTimer = new System.Windows.Forms.Timer();
            _scheduleTimer.Interval = 60000;
            _scheduleTimer.Tick += (s, e) => CheckSchedule();
            _scheduleTimer.Start();
            CheckSchedule();   // 启动时立即检查一次

            // 启动后延时检查更新（后台线程，24h 节流）
            StartUpdateCheck();

            // 自动亮度定时器（10s 采样环境光；无传感器时不启动，UI 会灰色不可用）
            _autoBrightTimer = new System.Windows.Forms.Timer();
            _autoBrightTimer.Interval = 10000;
            _autoBrightTimer.Tick += (s, e) => AutoBrightnessTick();
            _autoBrightTimer.Start();
        }

        // ============================================================
        // 自动亮度（环境光传感器 -> 亮度调节）
        // ============================================================
        private void AutoBrightnessTick()
        {
            // 用户关闭了自动亮度则跳过
            if (!_settings.AutoBrightness) return;
            // 防重入：上次采样还没完（PowerShell 子进程可能 1-2s）
            if (_autoBrightSampling) return;
            _autoBrightSampling = true;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 传感器不可用则不动作（UI 已灰色不可用）
                    if (!LightSensorReader.IsAvailable()) return;

                    float? lux = LightSensorReader.GetLux();
                    if (!lux.HasValue) return;   // 读取失败，静默跳过

                    int target = LightSensorReader.LuxToBrightness(lux.Value);
                    // 变化超过 5% 才实际调节，避免频繁 WMI SetBrightness
                    if (Math.Abs(target - _lastAutoBrightness) < 5) return;

                    bool ok = BrightnessController.SetBrightness(target);
                    if (ok) _lastAutoBrightness = target;
                }
                catch (Exception ex) { Settings.Log("AutoBrightnessTick 失败: " + ex.Message); }
                finally { _autoBrightSampling = false; }
            });
        }

        /// <summary>供 MainForm 开关切换时立即触发一次自动亮度调节。</summary>
        internal void TriggerAutoBrightness()
        {
            AutoBrightnessTick();
        }

        // ============================================================
        // 更新检查（启动后延时检查 + 24h 节流）
        // ============================================================
        private void StartUpdateCheck()
        {
            if (!_settings.UpdateCheckEnabled) return;

            // 24h 节流：上次检查未满 24h 则跳过
            DateTime lastCheck;
            if (DateTime.TryParse(_settings.LastUpdateCheck, out lastCheck))
            {
                if (DateTime.Now - lastCheck < TimeSpan.FromHours(24)) return;
            }

            // 后台线程延时 10s 再检查，避免启动时网络请求拖慢 UI 初始化
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(10000);
                var result = UpdateChecker.Check();

                // 无论结果如何，记录检查时间
                _settings.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                try { _settings.Save(); } catch { }

                if (result.HasUpdate)
                {
                    this.PostToUi(() =>
                    {
                        try
                        {
                            _tray.BalloonTipTitle = "发现新版本 v" + result.LatestVersion;
                            _tray.BalloonTipText = "点击查看下载页面，当前版本 v" + UpdateChecker.CurrentVersion;
                            _tray.BalloonTipIcon = ToolTipIcon.Info;
                            _tray.ShowBalloonTip(8000);
                            _pendingUpdateUrl = result.ReleaseUrl;
                        }
                        catch (Exception ex) { Settings.Log("更新通知失败: " + ex.Message); }
                    });
                }
            });
        }

        // 更新气泡点击后跳转的 URL（由 BalloonTipClicked 使用）
        private string _pendingUpdateUrl;

        // ============================================================
        // 右键菜单（原生 ContextMenuStrip）
        // ============================================================
        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var consoleItem = new ToolStripMenuItem("控制台", null, (s, e) => ShowConsole());
            consoleItem.Font = new Font(consoleItem.Font, FontStyle.Bold);
            menu.Items.Add(consoleItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出", null, (s, e) => ExitApp());
            menu.Items.Add(exitItem);

            return menu;
        }

        // ============================================================
        // 速览面板（左键切换显示/隐藏）
        // ============================================================
        internal void ToggleQuickPanel()
        {
            if (_quickForm != null && !_quickForm.IsDisposed && _quickForm.Visible)
            {
                _quickForm.Hide();
                return;
            }
            if (_quickForm == null || _quickForm.IsDisposed)
            {
                _quickForm = new QuickForm(_settings, this);
                _quickForm.FormClosed += (s, e) => { _quickForm = null; };
            }
            _quickForm.RefreshData();
            _quickForm.ShowNearCursor();
            _quickForm.Show();
            _quickForm.Activate();  // 主动抢焦点，让 GetForegroundWindow 能匹配
        }

        // ============================================================
        // 主控制台
        // ============================================================
        internal void ShowConsole()
        {
            if (_mainForm != null && !_mainForm.IsDisposed)
            {
                if (_mainForm.WindowState == FormWindowState.Minimized)
                    _mainForm.WindowState = FormWindowState.Normal;
                _mainForm.BringToFront();
                _mainForm.Activate();
                return;
            }
            _mainForm = new MainForm(_settings, this);
            _mainForm.FormClosed += (s, e) => { _mainForm = null; };
            _mainForm.Show();
        }

        // ============================================================
        // 气泡通知
        // ============================================================
        internal void ShowBalloon(string title, string text)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(5000);
            }
            catch (Exception ex) { Settings.Log("TrayContext.ShowBalloon 失败: " + ex.Message); }
        }

        // ============================================================
        // 定时刷新
        // ============================================================
        private void TickRefresh()
        {
            var s = MemoryInfo.Take();
            string battText;
            if (!_battery.IsPresent)
            {
                battText = "--";
            }
            else
            {
                // 检查保养模式状态
                string chargeMode = "";
                try
                {
                    var cap = OemChargeController.GetCapability();
                    if (cap.Supported)
                    {
                        var status = OemChargeController.GetStatus();
                        if (status != null && status.LimitPercent <= 80)
                            chargeMode = " 保养中";
                    }
                }
                catch (Exception ex) { Settings.Log("TrayContext.TickRefresh 充电状态 失败: " + ex.Message); }
                battText = _battery.Percent + "%" + (_battery.IsCharging ? " 充电中" : "") + chargeMode;
            }
            // 亮度
            string brightText = "";
            try
            {
                int bright = BrightnessController.GetBrightness();
                if (bright >= 0) brightText = " · 亮度 " + bright + "%";
            }
            catch (Exception ex) { Settings.Log("TrayContext.TickRefresh 亮度 失败: " + ex.Message); }
            _tray.Text = s.FormatShort() + " · 电量 " + battText + brightText;

            if (s.UsedPercent != _lastPercent)
            {
                _lastPercent = s.UsedPercent;
                try
                {
                    _currentIcon = IconRenderer.Build(s.UsedPercent, _battery.Percent, _battery.IsCharging);
                    _tray.Icon = _currentIcon;
                }
                catch (Exception ex) { Settings.Log("TrayContext.TickRefresh 图标 失败: " + ex.Message); }
            }

            // 速览面板可见时刷新
            if (_quickForm != null && !_quickForm.IsDisposed && _quickForm.Visible)
            {
                _quickForm.RefreshData();
            }

            // 阈值自动释放
            if (_settings.ThresholdEnabled && s.UsedPercent >= _settings.ThresholdPercent
                && !_autoReleasing
                && DateTime.Now - _lastAutoReleaseTime >= AutoReleaseCooldown)
            {
                _autoReleasing = true;
                _lastAutoReleaseTime = DateTime.Now;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var r = MemoryCleaner.Execute(_settings);
                        long delta = (long)r.BeforeUsedBytes - (long)r.AfterUsedBytes;
                        string freed = delta > 0 ? MemorySnapshot.FormatBytes((ulong)delta) : "0 B";
                        this.PostToUi(() =>
                        {
                            _tray.BalloonTipTitle = "Traynexus 自动释放";
                            _tray.BalloonTipText = string.Format("达到阈值 {0}%，已释放 {1}\r\n模式: {2}",
                                _settings.ThresholdPercent, freed, r.Mode);
                            _tray.BalloonTipIcon = ToolTipIcon.Info;
                            _tray.ShowBalloonTip(5000);
                        });
                    }
                    catch (Exception ex) { Settings.Log("自动释放失败: " + ex.Message); }
                    finally { _autoReleasing = false; }
                });
            }
        }

        private volatile bool _batterySampling;
        private void BatteryTick()
        {
            // 后台线程采集（WMI 查询可能耗时 100-500ms），UI 线程只更新结果
            if (_batterySampling) return;   // 上次还没采完，跳过
            _batterySampling = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                BatterySnapshot snap = null;
                try { snap = BatteryInfo.Take(); }
                catch (Exception ex) { Settings.Log("BatteryTick 失败: " + ex.Message); }
                finally { _batterySampling = false; }
                if (snap == null) return;

                this.PostToUi(() =>
                {
                    _battery = snap;
                    // 电池变化时更新图标
                    if (_battery.Percent != _lastBatteryPercent || _battery.IsCharging != _lastBatteryCharging)
                    {
                        _lastBatteryPercent = _battery.Percent;
                        _lastBatteryCharging = _battery.IsCharging;
                        _lastPercent = -1; // 强制下次 TickRefresh 重建图标
                    }
                });
            });
        }

        // ============================================================
        // 计划任务：夜间保养 + 周末满充
        // ============================================================
        private void CheckSchedule()
        {
            try
            {
                var now = DateTime.Now;
                int hour = now.Hour;
                DayOfWeek dow = now.DayOfWeek;
                bool isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;

                // 周末满充：周六/周日 0:00-8:00 设满电模式（优先级高于夜间保养）
                if (_settings.WeekendFullCharge && isWeekend && hour >= 0 && hour < 8)
                {
                    if (!_weekendChargeActive)
                    {
                        _weekendChargeActive = true;
                        _nightCareActive = false;   // 周末满充优先
                        SetChargeModeForSchedule(100);   // 满电模式
                        ShowBalloon("周末满充准备", "已自动切换为满电模式，充电至 100%。");
                    }
                    return;
                }
                else if (_weekendChargeActive)
                {
                    // 周末满充时段结束，恢复用户设定
                    _weekendChargeActive = false;
                    RestoreUserChargeMode();
                }

                // 夜间保养：22:00-07:00 设保养模式（60%）
                if (_settings.NightCareEnabled && (hour >= 22 || hour < 7))
                {
                    if (!_nightCareActive)
                    {
                        _nightCareActive = true;
                        SetChargeModeForSchedule(60);   // 保养模式
                        ShowBalloon("夜间自动保养", "已自动切换为保养模式，充电上限 60%。");
                    }
                }
                else if (_nightCareActive)
                {
                    // 夜间保养时段结束，恢复用户设定
                    _nightCareActive = false;
                    RestoreUserChargeMode();
                }
            }
            catch (Exception ex) { Settings.Log("CheckSchedule 失败: " + ex.Message); }
        }

        /// <summary>计划任务专用：设置充电模式（不影响用户的 ChargeMode 设置，只改实际充电状态）</summary>
        private void SetChargeModeForSchedule(int limit)
        {
            try
            {
                var cap = OemChargeController.GetCapability();
                if (cap.Supported)
                    OemChargeController.SetChargeLimit(limit);
            }
            catch (Exception ex) { Settings.Log("SetChargeModeForSchedule 失败: " + ex.Message); }
        }

        /// <summary>恢复用户设定的充电模式</summary>
        private void RestoreUserChargeMode()
        {
            try
            {
                var cap = OemChargeController.GetCapability();
                if (cap.Supported)
                    OemChargeController.SetChargeLimit(_settings.ChargeLimit);
            }
            catch (Exception ex) { Settings.Log("RestoreUserChargeMode 失败: " + ex.Message); }
        }

        // ============================================================
        // 其他
        // ============================================================
        private void ShowPendingNotices()
        {
            try
            {
                if (!string.IsNullOrEmpty(_pendingMigrationNotice))
                {
                    _tray.BalloonTipTitle = "Traynexus 配置迁移";
                    _tray.BalloonTipText = _pendingMigrationNotice;
                    _tray.BalloonTipIcon = ToolTipIcon.Info;
                    _tray.ShowBalloonTip(5000);
                    _pendingMigrationNotice = null;
                }
                else if (!string.IsNullOrEmpty(_pendingConflictNotice))
                {
                    _tray.BalloonTipTitle = "Traynexus 配置提示";
                    _tray.BalloonTipText = _pendingConflictNotice;
                    _tray.BalloonTipIcon = ToolTipIcon.Warning;
                    _tray.ShowBalloonTip(5000);
                    _pendingConflictNotice = null;
                }
            }
            catch (Exception ex) { Settings.Log("TrayContext.ShowPendingNotices 失败: " + ex.Message); }
        }

        private void ExitApp()
        {
            _tray.Visible = false;   // 立即隐藏托盘图标，给用户即时反馈
            // 不用 Environment.Exit(0)——它在 UI 线程调用会死锁（等所有线程结束但 UI 线程自己被阻塞）
            Application.ExitThread();   // 终止当前线程的消息循环，允许 Dispose 正常执行
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_quickForm != null && !_quickForm.IsDisposed) { try { _quickForm.Close(); } catch { } }
                    if (_mainForm != null && !_mainForm.IsDisposed) { try { _mainForm.Close(); } catch { } }
                    _timer.Stop(); _timer.Dispose();
                    _batteryTimer.Stop(); _batteryTimer.Dispose();
                    if (_scheduleTimer != null) { _scheduleTimer.Stop(); _scheduleTimer.Dispose(); }
                    if (_autoBrightTimer != null) { _autoBrightTimer.Stop(); _autoBrightTimer.Dispose(); }
                    _tray.Visible = false;
                    if (_tray.ContextMenuStrip != null) { try { _tray.ContextMenuStrip.Dispose(); } catch { } }
                    _tray.Dispose();
                    UiMarshal.Cleanup();
                    IconRenderer.DisposeCache();
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// UI 线程 marshal 锚点。
    /// </summary>
    internal static class UiMarshal
    {
        private static Control _anchor;
        public static void Init()
        {
            if (_anchor == null)
            {
                _anchor = new Control();
                var h = _anchor.Handle;
            }
        }
        public static void PostToUi(this object _, Action a)
        {
            if (_anchor == null) { Settings.Log("PostToUi: anchor 未初始化"); return; }
            try { _anchor.BeginInvoke(a); }
            catch (Exception ex) { Settings.Log("PostToUi 失败: " + ex.Message); }
        }
        public static void Cleanup()
        {
            if (_anchor != null) { try { _anchor.Dispose(); } catch { } _anchor = null; }
        }
    }
}
