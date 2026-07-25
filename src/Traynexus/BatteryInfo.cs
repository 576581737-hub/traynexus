using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Traynexus
{
    /// <summary>
    /// 电池快照：电量百分比、充电状态、是否存在电池。
    /// 通过 WMI Win32_Battery 采集基础信息；
    /// 设计容量/满充容量/循环次数优先用 WMI（BatteryStaticData/BatteryCycleCount），
    /// 取不到时兜底解析 powercfg /batteryreport 生成的 XML。
    /// </summary>
    public class BatterySnapshot
    {
        public int Percent;          // 0-100，无电池时 0
        public bool IsCharging;      // 正在充电（基于 ChargeRate > 0 判断，而非 BatteryStatus）
        public bool IsPresent;       // 是否检测到电池（台式机为 false）
        public int ChargeRate;       // 充电速率(mW)，0=未充电

        public int DesignCapacityWh;   // 设计容量(Wh)，取不到为 0
        public int FullChargeCapacityWh; // 当前满充容量(Wh)，取不到为 0
        public int CycleCount;        // 循环次数，取不到为 0
        public int Temperature;       // 温度(℃)，取不到为 0

        // 电池详细信息（供健康报告弹窗显示）
        public string BatteryName = "";        // 电池型号
        public string Manufacturer = "";       // 制造商
        public string SerialNumber = "";       // 序列号
        public string Chemistry = "";          // 化学类型
        // 系统信息（供健康报告弹窗显示）
        public string ComputerName = "";
        public string SystemProduct = "";
        public string Bios = "";
        public string OsBuild = "";
        public string ReportTime = "";

        /// <summary>电池健康度百分比 = FullChargeCapacityWh / DesignCapacityWh * 100。取不到为 0。</summary>
        public int HealthPercent
        {
            get
            {
                if (DesignCapacityWh <= 0 || FullChargeCapacityWh <= 0) return 0;
                return Math.Max(0, Math.Min(100, FullChargeCapacityWh * 100 / DesignCapacityWh));
            }
        }
    }

    /// <summary>
    /// 电池信息采集器：WMI 查询 Win32_Battery。
    /// 查询失败（无电池/WMI 不可用）返回 IsPresent=false 的快照。
    /// </summary>
    public static class BatteryInfo
    {
        // powercfg 解析结果缓存（powercfg 调用较慢，30s 内复用）
        private static BatteryDeepData _cachedDeep;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        // FillDeepData 整体结果缓存（WMI + powercfg 全部查询，30s 内复用）
        // 深度数据（容量/循环次数/制造商/序列号等）变化缓慢，无需每次重查
        private static BatteryDeepData _cachedDeepAll;
        private static DateTime _deepAllTime = DateTime.MinValue;
        private static readonly TimeSpan DeepAllTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 采集当前电池快照。线程安全（每次新建 ManagementObjectSearcher）。
        /// </summary>
        public static BatterySnapshot Take()
        {
            var snap = new BatterySnapshot();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        try
                        {
                            snap.IsPresent = true;
                            try
                            {
                                snap.Percent = Convert.ToInt32(mo["EstimatedChargeRemaining"]);
                            }
                            catch { snap.Percent = 0; }
                            // BatteryStatus: 1=放电, 2=交流电, 3=已充电, 4=低, 5=严重低, 其他
                            // 注意：status=2 只表示「接着电源」，不代表真正在充电
                            // 保养模式下 status=2 但 ChargeRate=0（充电已暂停）
                            // status=3 是"已充满"，不算充电中
                            try
                            {
                                int status = Convert.ToInt32(mo["BatteryStatus"]);
                                // 只把 2 (AC power) 当作潜在充电中；3 (Charged) 已满，不算
                                // 后面 root\wmi ChargeRate 查询成功时会用真实充电速率覆盖
                                snap.IsCharging = (status == 2 || status == 6 || status == 7 || status == 8 || status == 9);
                            }
                            catch { snap.IsCharging = false; }
                            break; // 只取第一个电池
                        }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }

                // 用 root\wmi BatteryStatus 的 ChargeRate 精确判断是否真正充电
                // ChargeRate > 0 = 正在充电；= 0 = 充电暂停（保养模式）或已充满
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "root\\wmi", "SELECT ChargeRate FROM BatteryStatus"))
                    {
                        foreach (var mo in searcher.Get())
                        {
                            try
                            {
                                snap.ChargeRate = Convert.ToInt32(mo["ChargeRate"]);
                                // ChargeRate=0 时覆盖 IsCharging 为 false
                                if (snap.ChargeRate == 0) snap.IsCharging = false;
                                break;
                            }
                            finally { try { mo.Dispose(); } catch { } }
                        }
                    }
                }
                catch { /* BatteryStatus WMI 不可用时保持 Win32_Battery 的判断 */ }
            }
            catch (Exception ex)
            {
                Settings.Log("BatteryInfo.Take 失败: " + ex.Message);
                snap.IsPresent = false;
            }

            // 尝试读取深度数据（设计容量/满充容量/循环次数）
            FillDeepData(snap);

            return snap;
        }

        /// <summary>填充深度数据：先试 WMI，取不到兜底 powercfg XML 解析。结果缓存 30s。</summary>
        private static void FillDeepData(BatterySnapshot snap)
        {
            // 命中缓存：直接复用整份深度数据（容量/循环/制造商/序列号等变化缓慢）
            lock (_cacheLock)
            {
                if (_cachedDeepAll != null && DateTime.Now - _deepAllTime < DeepAllTtl)
                {
                    CopyDeepToSnapshot(_cachedDeepAll, snap);
                    return;
                }
            }

            // 未命中：完整执行 WMI + powercfg 查询，结果存到临时 deep 对象
            var deep = new BatteryDeepData();

            // WMI 优先
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT DesignedCapacity, FullChargedCapacity FROM BatteryStaticData"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        try
                        {
                            try { deep.DesignCapacityWh = Convert.ToInt32(mo["DesignedCapacity"]); } catch { }
                            try { deep.FullChargeCapacityWh = Convert.ToInt32(mo["FullChargedCapacity"]); } catch { }
                            break;
                        }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch { /* 多数机器无 BatteryStaticData，静默 */ }

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT CycleCount FROM BatteryCycleCount"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        try
                        {
                            try { deep.CycleCount = Convert.ToInt32(mo["CycleCount"]); } catch { }
                            break;
                        }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch { /* 静默 */ }

            // WMI 取不到设计/满充容量 -> 兜底 powercfg
            if (deep.DesignCapacityWh <= 0 || deep.FullChargeCapacityWh <= 0 || deep.CycleCount <= 0)
            {
                var fromPowercfg = GetDeepDataFromPowercfg();
                if (fromPowercfg != null)
                {
                    if (deep.DesignCapacityWh <= 0) deep.DesignCapacityWh = fromPowercfg.DesignCapacityWh;
                    if (deep.FullChargeCapacityWh <= 0) deep.FullChargeCapacityWh = fromPowercfg.FullChargeCapacityWh;
                    if (deep.CycleCount <= 0) deep.CycleCount = fromPowercfg.CycleCount;
                    if (string.IsNullOrEmpty(deep.BatteryName)) deep.BatteryName = fromPowercfg.BatteryName;
                    if (string.IsNullOrEmpty(deep.Manufacturer)) deep.Manufacturer = fromPowercfg.Manufacturer;
                    if (string.IsNullOrEmpty(deep.SerialNumber)) deep.SerialNumber = fromPowercfg.SerialNumber;
                    if (string.IsNullOrEmpty(deep.Chemistry)) deep.Chemistry = fromPowercfg.Chemistry;
                    deep.ComputerName = fromPowercfg.ComputerName;
                    deep.SystemProduct = fromPowercfg.SystemProduct;
                    deep.Bios = fromPowercfg.Bios;
                    deep.OsBuild = fromPowercfg.OsBuild;
                    deep.ReportTime = fromPowercfg.ReportTime;
                }
            }

            // 采集电池详细信息（名称/制造商/序列号/化学类型）
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT DeviceID, ManufactureName, SerialNumber, Chemistry FROM BatteryStaticData"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        try
                        {
                            try { if (string.IsNullOrEmpty(deep.BatteryName)) deep.BatteryName = Convert.ToString(mo["DeviceID"]); } catch { }
                            try { if (string.IsNullOrEmpty(deep.Manufacturer)) deep.Manufacturer = Convert.ToString(mo["ManufactureName"]); } catch { }
                            try { if (string.IsNullOrEmpty(deep.SerialNumber)) deep.SerialNumber = Convert.ToString(mo["SerialNumber"]); } catch { }
                            try { if (string.IsNullOrEmpty(deep.Chemistry)) deep.Chemistry = Convert.ToString(mo["Chemistry"]); } catch { }
                            break;
                        }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch { /* 静默 */ }

            // 缓存 + 应用
            lock (_cacheLock)
            {
                _cachedDeepAll = deep;
                _deepAllTime = DateTime.Now;
            }
            CopyDeepToSnapshot(deep, snap);
        }

        /// <summary>把 BatteryDeepData 的深度字段复制到 BatterySnapshot</summary>
        private static void CopyDeepToSnapshot(BatteryDeepData deep, BatterySnapshot snap)
        {
            snap.DesignCapacityWh = deep.DesignCapacityWh;
            snap.FullChargeCapacityWh = deep.FullChargeCapacityWh;
            snap.CycleCount = deep.CycleCount;
            snap.BatteryName = deep.BatteryName ?? "";
            snap.Manufacturer = deep.Manufacturer ?? "";
            snap.SerialNumber = deep.SerialNumber ?? "";
            snap.Chemistry = deep.Chemistry ?? "";
            snap.ComputerName = deep.ComputerName ?? "";
            snap.SystemProduct = deep.SystemProduct ?? "";
            snap.Bios = deep.Bios ?? "";
            snap.OsBuild = deep.OsBuild ?? "";
            snap.ReportTime = deep.ReportTime ?? "";
        }

        /// <summary>从 powercfg /batteryreport（HTML 格式）正则提取深度数据（带 30s 缓存）</summary>
        private static BatteryDeepData GetDeepDataFromPowercfg()
        {
            lock (_cacheLock)
            {
                if (_cachedDeep != null && DateTime.Now - _cacheTime < CacheTtl)
                    return _cachedDeep;
            }

            var result = new BatteryDeepData();
            try
            {
                string htmlPath = Path.Combine(Path.GetTempPath(), "traynexus_battery_report.html");
                try { if (File.Exists(htmlPath)) File.Delete(htmlPath); } catch { }

                var psi = new ProcessStartInfo("powercfg.exe", "/batteryreport /output \"" + htmlPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    // 异步消费 stdout/stderr，防止缓冲区满导致子进程阻塞
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                }

                if (!File.Exists(htmlPath)) return null;

                string html = File.ReadAllText(htmlPath, Encoding.UTF8);
                try { File.Delete(htmlPath); } catch { }

                // HTML 结构：<span class="label">FIELD</span></td><td>VALUE</td>
                // 容量带逗号和 mWh 后缀，需清理
                result.BatteryName = ExtractHtmlField(html, "NAME");
                result.Manufacturer = ExtractHtmlField(html, "MANUFACTURER");
                result.SerialNumber = ExtractHtmlField(html, "SERIAL NUMBER");
                result.Chemistry = ExtractHtmlField(html, "CHEMISTRY");
                result.DesignCapacityWh = ParseMwhToWh(ExtractHtmlField(html, "DESIGN CAPACITY"));
                result.FullChargeCapacityWh = ParseMwhToWh(ExtractHtmlField(html, "FULL CHARGE CAPACITY"));
                int cycle;
                if (int.TryParse(ExtractHtmlField(html, "CYCLE COUNT"), out cycle)) result.CycleCount = cycle;

                // 系统信息
                result.ComputerName = ExtractHtmlField(html, "COMPUTER NAME");
                result.SystemProduct = ExtractHtmlField(html, "SYSTEM PRODUCT NAME");
                result.Bios = ExtractHtmlField(html, "BIOS");
                result.OsBuild = ExtractHtmlField(html, "OS BUILD");
                result.ReportTime = ExtractHtmlField(html, "REPORT TIME");
            }
            catch (Exception ex)
            {
                Settings.Log("GetDeepDataFromPowercfg 失败: " + ex.Message);
                return null;
            }

            lock (_cacheLock)
            {
                _cachedDeep = result;
                _cacheTime = DateTime.Now;
            }
            return result;
        }

        /// <summary>从 HTML 提取 label 对应的值。匹配 &lt;span class="label">FIELD</span>&lt;/td>&lt;td>VALUE&lt;/td></summary>
        private static string ExtractHtmlField(string html, string fieldName)
        {
            // 匹配 <span class="label">FIELD</span></td><td>VALUE
            // VALUE 可能后面跟 </td> 或换行+mWh
            string pattern = @"<span\s+class=""label"">\s*" + Regex.Escape(fieldName) + @"\s*</span></td><td>([^<]*)";
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            string val = m.Groups[1].Value.Trim();
            // 清理容量字段里的逗号和单位
            return val;
        }

        /// <summary>解析 "75,000 mWh" 格式为 Wh（去掉逗号和单位，/1000）</summary>
        private static int ParseMwhToWh(string mwhText)
        {
            if (string.IsNullOrEmpty(mwhText)) return 0;
            // 去掉逗号、空格、mWh 后缀
            string num = mwhText.Replace(",", "").Replace("mWh", "").Trim();
            int mwh;
            if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out mwh))
                return mwh / 1000;
            return 0;
        }

        /// <summary>powercfg 解析的深度数据（内部缓存用）</summary>
        private class BatteryDeepData
        {
            public int DesignCapacityWh;
            public int FullChargeCapacityWh;
            public int CycleCount;
            public string BatteryName = "";
            public string Manufacturer = "";
            public string SerialNumber = "";
            public string Chemistry = "";
            // 系统信息（供健康报告弹窗）
            public string ComputerName = "";
            public string SystemProduct = "";
            public string Bios = "";
            public string OsBuild = "";
            public string ReportTime = "";
        }
    }
}
