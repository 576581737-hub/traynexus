using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 暴露给前端 WebView2 的桥接 API。
    /// 通过 AddHostObjectToScript("traynexus", bridge) 注入到 JS，
    /// JS 端用 window.chrome.webview.hostObjects.traynexus 调用。
    /// 所有方法返回 JSON 字符串（手动拼接，避免引入 Newtonsoft.Json）。
    /// 不使用 string interpolation（$""），保持 C# 5 兼容。
    /// </summary>
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]
    public class BridgeApi
    {
        private readonly Settings _settings;
        private readonly TrayContext _context;

        public BridgeApi(Settings settings, TrayContext context)
        {
            _settings = settings;
            _context = context;
        }

        // ============================================================
        // 内存 & 释放
        // ============================================================

        /// <summary>
        /// 返回当前内存快照 JSON。
        /// </summary>
        public string GetMemorySnapshot()
        {
            try
            {
                var s = MemoryInfo.Take();
                // 手动拼接 JSON，避免依赖 Newtonsoft
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"totalBytes\":").Append(s.TotalBytes).Append(",");
                sb.Append("\"availBytes\":").Append(s.AvailBytes).Append(",");
                sb.Append("\"usedBytes\":").Append(s.UsedBytes).Append(",");
                sb.Append("\"usedPercent\":").Append(s.UsedPercent).Append(",");
                sb.Append("\"commitTotalBytes\":").Append(s.CommitTotalBytes).Append(",");
                sb.Append("\"commitLimitBytes\":").Append(s.CommitLimitBytes).Append(",");
                sb.Append("\"systemCacheBytes\":").Append(s.SystemCacheBytes).Append(",");
                sb.Append("\"pageFileTotalBytes\":").Append(s.PageFileTotalBytes).Append(",");
                sb.Append("\"pageFileAvailBytes\":").Append(s.PageFileAvailBytes).Append(",");
                sb.Append("\"usedDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes(s.UsedBytes))).Append("\",");
                sb.Append("\"totalDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes(s.TotalBytes))).Append("\",");
                sb.Append("\"availDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes(s.AvailBytes))).Append("\"");
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 返回电池信息 JSON。通过 WMI Win32_Battery 采集真实电量/充电状态。
        /// 设计容量/循环次数等深度数据多数机器拿不到，为 0。
        /// </summary>
        public string GetBatteryInfo()
        {
            try
            {
                var b = BatteryInfo.Take();
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"percent\":").Append(b.Percent).Append(",");
                sb.Append("\"isCharging\":").Append(b.IsCharging ? "true" : "false").Append(",");
                sb.Append("\"isPresent\":").Append(b.IsPresent ? "true" : "false").Append(",");
                sb.Append("\"designCapacityWh\":").Append(b.DesignCapacityWh).Append(",");
                sb.Append("\"fullChargeCapacityWh\":").Append(b.FullChargeCapacityWh).Append(",");
                sb.Append("\"cycleCount\":").Append(b.CycleCount).Append(",");
                sb.Append("\"temperature\":").Append(b.Temperature);
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\",\"isPresent\":false}";
            }
        }

        /// <summary>
        /// 检测当前设备是否支持充电阈值控制。返回 JSON。
        /// </summary>
        public string GetChargeCapability()
        {
            try
            {
                var cap = OemChargeController.GetCapability();
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"supported\":").Append(cap.Supported ? "true" : "false").Append(",");
                sb.Append("\"oem\":").Append((int)cap.Oem);
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"supported\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 设置充电上限百分比。返回 JSON。
        /// </summary>
        public string SetChargeLimit(int percent)
        {
            try
            {
                bool ok = OemChargeController.SetChargeLimit(percent);
                return "{\"success\":" + (ok ? "true" : "false") + "}";
            }
            catch (Exception ex)
            {
                return "{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 执行内存释放。同步阻塞调用（JS 端是异步的）。
        /// 返回结果 JSON。
        /// </summary>
        public string ExecuteRelease()
        {
            try
            {
                var r = MemoryCleaner.Execute(_settings);
                long delta = (long)r.BeforeUsedBytes - (long)r.AfterUsedBytes;
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"mode\":").Append((int)r.Mode).Append(",");
                sb.Append("\"trimmedCount\":").Append(r.TrimmedCount).Append(",");
                sb.Append("\"skippedByBlacklist\":").Append(r.SkippedByBlacklist).Append(",");
                sb.Append("\"skippedByWhitelist\":").Append(r.SkippedByWhitelist).Append(",");
                sb.Append("\"failedByAccess\":").Append(r.FailedByAccess).Append(",");
                sb.Append("\"failedByProcessExit\":").Append(r.FailedByProcessExit).Append(",");
                sb.Append("\"standbyPurged\":").Append(r.StandbyPurged ? "true" : "false").Append(",");
                sb.Append("\"workingSetsEmptied\":").Append(r.WorkingSetsEmptied ? "true" : "false").Append(",");
                sb.Append("\"standbyError\":\"").Append(EscapeJson(r.StandbyError)).Append("\",");
                sb.Append("\"beforeUsedBytes\":").Append(r.BeforeUsedBytes).Append(",");
                sb.Append("\"afterUsedBytes\":").Append(r.AfterUsedBytes).Append(",");
                sb.Append("\"freedBytes\":").Append((ulong)(delta > 0 ? delta : 0)).Append(",");
                sb.Append("\"freedDisplay\":\"").Append(EscapeJson(delta > 0 ? MemorySnapshot.FormatBytes((ulong)delta) : "0 B")).Append("\",");
                sb.Append("\"beforeDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes(r.BeforeUsedBytes))).Append("\",");
                sb.Append("\"afterDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes(r.AfterUsedBytes))).Append("\"");
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 干跑预览：列出所有进程及是否会被保护。
        /// 返回 JSON 数组字符串。
        /// </summary>
        public string PreviewTargets()
        {
            try
            {
                var list = MemoryCleaner.PreviewTargets(_settings, true);
                StringBuilder sb = new StringBuilder();
                sb.Append("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var p = list[i];
                    sb.Append("{");
                    sb.Append("\"name\":\"").Append(EscapeJson(p.Name)).Append("\",");
                    sb.Append("\"pid\":").Append(p.Pid).Append(",");
                    sb.Append("\"workingSet\":").Append(p.WorkingSet).Append(",");
                    sb.Append("\"workingSetDisplay\":\"").Append(EscapeJson(MemorySnapshot.FormatBytes((ulong)(p.WorkingSet < 0 ? 0 : p.WorkingSet)))).Append("\",");
                    sb.Append("\"protected\":").Append(p.Protected ? "true" : "false").Append(",");
                    sb.Append("\"protectReason\":\"").Append(EscapeJson(p.ProtectReason)).Append("\"");
                    sb.Append("}");
                }
                sb.Append("]");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        // ============================================================
        // 设置
        // ============================================================

        public string GetSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"mode\":").Append((int)_settings.Mode).Append(",");
                sb.Append("\"thresholdEnabled\":").Append(_settings.ThresholdEnabled ? "true" : "false").Append(",");
                sb.Append("\"thresholdPercent\":").Append(_settings.ThresholdPercent).Append(",");
                sb.Append("\"chargeMode\":").Append(_settings.ChargeMode).Append(",");
                sb.Append("\"chargeLimit\":").Append(_settings.ChargeLimit);
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 更新设置并持久化。
        /// </summary>
        public string UpdateSettings(int mode, bool thresholdEnabled, int thresholdPercent)
        {
            try
            {
                if (mode < 1 || mode > 3) return "{\"success\":false,\"error\":\"invalid mode\"}";
                if (thresholdPercent < 1 || thresholdPercent > 99) return "{\"success\":false,\"error\":\"invalid thresholdPercent\"}";
                _settings.Mode = (ReleaseMode)mode;
                _settings.ThresholdEnabled = thresholdEnabled;
                _settings.ThresholdPercent = thresholdPercent;
                _settings.Save();
                return "{\"success\":true}";
            }
            catch (Exception ex)
            {
                return "{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 更新充电设置并持久化。chargeMode: 0满充/1均衡/2保养；chargeLimit: 50-100。
        /// </summary>
        public string UpdateChargeSettings(int chargeMode, int chargeLimit)
        {
            try
            {
                if (chargeMode < 0 || chargeMode > 2) return "{\"success\":false,\"error\":\"invalid chargeMode\"}";
                if (chargeLimit < 50 || chargeLimit > 100) return "{\"success\":false,\"error\":\"invalid chargeLimit\"}";
                _settings.ChargeMode = chargeMode;
                _settings.ChargeLimit = chargeLimit;
                _settings.Save();
                // 尝试通过 OEM WMI 设置充电阈值（失败不报错，前端按 capability 显示）
                OemChargeController.SetChargeLimit(chargeLimit);
                return "{\"success\":true}";
            }
            catch (Exception ex)
            {
                return "{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        // ============================================================
        // 白名单
        // ============================================================

        /// <summary>
        /// 返回白名单文件原始内容（含注释行）。
        /// </summary>
        public string GetWhitelistContent()
        {
            try
            {
                if (!File.Exists(_settings.WhitelistPath)) return "";
                return File.ReadAllText(_settings.WhitelistPath, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "# 读取失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 把字符串（每行一个进程名）写入白名单文件。返回是否成功。
        /// </summary>
        public bool SaveWhitelist(string names)
        {
            try
            {
                lock (_settings.WhitelistLock)
                {
                    _settings.UserWhitelist.Clear();
                    if (!string.IsNullOrEmpty(names))
                    {
                        string[] lines = names.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string raw in lines)
                        {
                            string l = raw.Trim();
                            if (l.Length == 0 || l.StartsWith("#")) continue;
                            _settings.UserWhitelist.Add(l);
                        }
                    }
                    // 通过反射调用私有 WriteWhitelistFile 不方便，直接重写文件
                    var sb = new StringBuilder();
                    sb.AppendLine("# 每行一个进程名（含或不含 .exe），保护它们不被 WorkingSet 削减。");
                    sb.AppendLine("# 井号 # 开头的是注释。系统关键进程已经内置保护，无需列出。");
                    sb.AppendLine("# ---");
                    var sorted = new System.Collections.Generic.List<string>(_settings.UserWhitelist);
                    sorted.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (var n in sorted) sb.AppendLine(n);

                    string tmp = _settings.WhitelistPath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(_settings.WhitelistPath))
                        File.Replace(tmp, _settings.WhitelistPath, null);
                    else
                        File.Move(tmp, _settings.WhitelistPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Settings.Log("SaveWhitelist 失败: " + ex.Message);
                return false;
            }
        }

        // ============================================================
        // 文件夹/文件打开
        // ============================================================

        public void OpenConfigFolder()
        {
            try
            {
                if (!string.IsNullOrEmpty(_settings.ConfigDir) && Directory.Exists(_settings.ConfigDir))
                {
                    Process.Start(_settings.ConfigDir);
                }
            }
            catch (Exception ex) { Settings.Log("OpenConfigFolder: " + ex.Message); }
        }

        public void OpenWhitelistInNotepad()
        {
            try
            {
                if (!File.Exists(_settings.WhitelistPath))
                {
                    // 触发 Settings.Load 时的初始化写文件逻辑（若已被初始化过则跳过）
                    File.WriteAllText(_settings.WhitelistPath,
                        "# 每行一个进程名（含或不含 .exe），保护它们不被 WorkingSet 削减。\r\n" +
                        "# 井号 # 开头的是注释。系统关键进程已经内置保护，无需列出。\r\n",
                        new UTF8Encoding(false));
                }
                Process.Start("notepad.exe", _settings.WhitelistPath);
            }
            catch (Exception ex) { Settings.Log("OpenWhitelistInNotepad: " + ex.Message); }
        }

        // ============================================================
        // 自启动
        // ============================================================

        public bool GetAutoStartState()
        {
            try { return AutoStartManager.IsEnabled(); }
            catch { return false; }
        }

        public bool SetAutoStart(bool enable)
        {
            try
            {
                if (enable) return AutoStartManager.Enable();
                return AutoStartManager.Disable();
            }
            catch (Exception ex)
            {
                Settings.Log("SetAutoStart: " + ex.Message);
                return false;
            }
        }

        // ============================================================
        // 其他
        // ============================================================

        public void OpenUrl(string url)
        {
            try
            {
                // 简单校验，避免恶意协议
                if (string.IsNullOrEmpty(url)) return;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                Process.Start(url);
            }
            catch (Exception ex) { Settings.Log("OpenUrl: " + ex.Message); }
        }

        // ============================================================
        // 窗口控制（前端标题栏按钮调用）
        // ============================================================

        public void MinimizeWindow()
        {
            try { _context.MinimizeConsole(); }
            catch (Exception ex) { Settings.Log("MinimizeWindow: " + ex.Message); }
        }

        public void MaximizeWindow()
        {
            try { _context.MaximizeConsole(); }
            catch (Exception ex) { Settings.Log("MaximizeWindow: " + ex.Message); }
        }

        public void CloseWindow()
        {
            try { _context.CloseConsole(); }
            catch (Exception ex) { Settings.Log("CloseWindow: " + ex.Message); }
        }

        // ============================================================
        // 托盘菜单动作（右键 HTML 菜单调用）
        // ============================================================

        public void OpenConsole(string navTarget)
        {
            try { _context.OpenConsoleFromMenu(navTarget); }
            catch (Exception ex) { Settings.Log("OpenConsole: " + ex.Message); }
        }

        public void ExitApp()
        {
            try { _context.ExitFromMenu(); }
            catch (Exception ex) { Settings.Log("ExitApp: " + ex.Message); }
        }

        /// <summary>
        /// 返回配置迁移检查结果 JSON。
        /// </summary>
        public string CheckMigration()
        {
            try
            {
                bool needs = ConfigMigrator.NeedsMigration();
                bool conflict = ConfigMigrator.ConflictDetected();
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"needsMigration\":").Append(needs ? "true" : "false").Append(",");
                sb.Append("\"conflictDetected\":").Append(conflict ? "true" : "false").Append(",");
                sb.Append("\"oldConfigDir\":\"").Append(EscapeJson(ConfigMigrator.OldConfigDir)).Append("\",");
                sb.Append("\"newConfigDir\":\"").Append(EscapeJson(ConfigMigrator.NewConfigDir)).Append("\"");
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        /// <summary>
        /// 简易 JSON 字符串转义：处理 \ " \n \r \t 等。
        /// </summary>
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
