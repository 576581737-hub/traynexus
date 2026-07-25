using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Management;

namespace Traynexus
{
    /// <summary>
    /// 显示器亮度信息。
    /// </summary>
    public class MonitorInfo
    {
        public string Name;          // 显示器名称
        public int Brightness;       // 当前亮度 0-100，-1 表示不可读
        public bool IsInternal;      // 是否内置屏
        public bool DdcSupported;    // 是否支持 DDC/CI 亮度控制
        public IntPtr PhysicalHandle; // DDC/CI 物理监视器句柄（外接屏用）
        public uint DdcMin;          // DDC/CI 亮度最小值
        public uint DdcMax;          // DDC/CI 亮度最大值
    }

    /// <summary>
    /// 亮度控制器：
    /// - 内置屏：WMI WmiMonitorBrightnessMethods
    /// - 外接屏：dxva2.dll 物理显示器 API（DDC/CI）
    /// 线程安全：WMI/Win32 查询每次新建。
    /// </summary>
    public static class BrightnessController
    {
        // DDC/CI 物理监视器句柄缓存（EnumerateMonitors 时获取，程序退出前不释放）
        private static readonly List<IntPtr> _physicalHandles = new List<IntPtr>();

        // 内置屏亮度缓存：GetBrightness 是无缓存的 WMI 查询（100-500ms），
        // 被 TrayContext.TickRefresh（1s）和 MainForm（2s）高频调用，全部在 UI 线程造成周期性卡顿。
        // 加 5 秒缓存，把 WMI 查询频率从 ~3 次/2s 降到 ~1 次/5s。
        private static readonly object _brightLock = new object();
        private static int _cachedBrightness = int.MinValue;  // int.MinValue 表示尚未采集
        private static DateTime _brightnessCacheTime = DateTime.MinValue;
        private static readonly TimeSpan BrightnessCacheTtl = TimeSpan.FromSeconds(5);

        /// <summary>清除内置屏亮度缓存，让下次 GetBrightness 立即重查 WMI。SetBrightness 成功后调用。</summary>
        public static void InvalidateBrightnessCache()
        {
            lock (_brightLock)
            {
                _cachedBrightness = int.MinValue;
                _brightnessCacheTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 获取内置屏当前亮度。返回 -1 表示不支持或读取失败。结果缓存 5 秒。
        /// </summary>
        public static int GetBrightness()
        {
            lock (_brightLock)
            {
                if (_cachedBrightness != int.MinValue && DateTime.Now - _brightnessCacheTime < BrightnessCacheTtl)
                    return _cachedBrightness;
            }
            int value = -1;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        try { value = Convert.ToInt32(mo["CurrentBrightness"]); }
                        finally { try { mo.Dispose(); } catch { } }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Settings.Log("BrightnessController.GetBrightness 失败: " + ex.Message);
            }
            lock (_brightLock)
            {
                _cachedBrightness = value;
                _brightnessCacheTime = DateTime.Now;
            }
            return value;
        }

        /// <summary>
        /// 设置内置屏亮度。percent 范围 0-100。返回是否成功。
        /// </summary>
        public static bool SetBrightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
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
                }
            }
            catch (Exception ex)
            {
                Settings.Log("BrightnessController.SetBrightness 失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 设置指定显示器亮度（区分内置/外接）。
        /// </summary>
        public static bool SetBrightness(MonitorInfo monitor, int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            if (monitor.IsInternal)
            {
                return SetBrightness(percent);
            }

            // 外接屏走 DDC/CI
            if (!monitor.DdcSupported || monitor.PhysicalHandle == IntPtr.Zero)
                return false;

            try
            {
                // 把 0-100 映射到 DDC 的 min-max 范围
                uint range = monitor.DdcMax - monitor.DdcMin;
                if (range == 0) range = 100;
                uint value = monitor.DdcMin + (uint)(percent * range / 100);
                return NativeMethods.SetMonitorBrightness(monitor.PhysicalHandle, value);
            }
            catch (Exception ex)
            {
                Settings.Log("SetBrightness(DDC) 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 检测系统是否支持内置屏亮度控制。
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try { return true; }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch (Exception ex) { Settings.Log("BrightnessController.IsSupported 失败: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 检测是否有外接屏支持 DDC/CI 亮度控制。
        /// </summary>
        public static bool IsDdcSupported()
        {
            try
            {
                var monitors = EnumerateMonitors();
                foreach (var m in monitors)
                {
                    if (!m.IsInternal && m.DdcSupported) return true;
                }
            }
            catch (Exception ex) { Settings.Log("BrightnessController.IsDdcSupported 失败: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 枚举所有显示器（内置屏 WMI + 外接屏 DDC/CI）。
        /// </summary>
        public static List<MonitorInfo> EnumerateMonitors()
        {
            // 先释放上次枚举的旧 DDC 句柄，避免每次枚举累积泄漏
            Cleanup();

            var list = new List<MonitorInfo>();
            int internalCount = 0;

            // 1. WMI 枚举内置屏
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            var info = new MonitorInfo();
                            info.IsInternal = true;
                            internalCount++;
                            info.Name = "内置显示器";
                            try { info.Brightness = Convert.ToInt32(mo["CurrentBrightness"]); }
                            catch { info.Brightness = -1; }
                            list.Add(info);
                        }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                Settings.Log("EnumerateMonitors WMI 失败: " + ex.Message);
            }

            // 2. DDC/CI 枚举外接屏
            try
            {
                EnumerateDdcMonitors(list, internalCount);
            }
            catch (Exception ex)
            {
                Settings.Log("EnumerateMonitors DDC 失败: " + ex.Message);
            }

            return list;
        }

        /// <summary>通过 EnumDisplayMonitors + GetPhysicalMonitorsFromHMONITOR 枚举 DDC/CI 显示器</summary>
        private static void EnumerateDdcMonitors(List<MonitorInfo> list, int internalCount)
        {
            int externalIdx = 0;
            int skipCount = internalCount;   // 跳过内置屏（WMI 已枚举的），避免重复
            NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
            {
                try
                {
                    // 获取物理监视器数量
                    uint count;
                    if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0)
                        return true;

                    // 获取物理监视器句柄
                    var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
                    if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                        return true;

                    foreach (var pm in physicals)
                    {
                        if (pm.hPhysicalMonitor == IntPtr.Zero) continue;

                        // 跳过内置屏（WMI 已枚举的），避免重复——立即释放该句柄避免泄漏
                        if (skipCount > 0)
                        {
                            skipCount--;
                            var tmp = new NativeMethods.PHYSICAL_MONITOR[1];
                            tmp[0] = pm;
                            try { NativeMethods.DestroyPhysicalMonitors(1, tmp); } catch { }
                            continue;
                        }

                        // 检测 DDC/CI 能力
                        uint caps, colorTemps;
                        bool ddcCap = NativeMethods.GetMonitorCapabilities(pm.hPhysicalMonitor, out caps, out colorTemps)
                            && (caps & NativeMethods.MC_CAPS_BRIGHTNESS) != 0;

                        var info = new MonitorInfo();
                        info.IsInternal = false;
                        info.DdcSupported = ddcCap;
                        info.PhysicalHandle = pm.hPhysicalMonitor;
                        externalIdx++;

                        // 显示器描述
                        string desc = pm.szPhysicalMonitorDescription;
                        if (string.IsNullOrEmpty(desc)) desc = "";
                        info.Name = "外接显示器" + (externalIdx > 1 ? " " + externalIdx : "") + (ddcCap ? "" : "（不支持亮度调节）");

                        if (ddcCap)
                        {
                            // 读当前亮度
                            uint min, cur, max;
                            if (NativeMethods.GetMonitorBrightness(pm.hPhysicalMonitor, out min, out cur, out max))
                            {
                                info.DdcMin = min;
                                info.DdcMax = max;
                                // 映射到 0-100（先减 min 再乘除，避免分别截断误差）
                                uint range = max - min;
                                if (range > 0)
                                    info.Brightness = (int)((cur - min) * 100 / range);
                                else
                                    info.Brightness = (int)cur;
                            }
                            else
                            {
                                info.Brightness = -1;
                            }
                            _physicalHandles.Add(pm.hPhysicalMonitor);
                        }
                        else
                        {
                            info.Brightness = -1;
                            // 非 DDC 外接屏：立即释放句柄避免泄漏
                            var tmp = new NativeMethods.PHYSICAL_MONITOR[1];
                            tmp[0] = pm;
                            try { NativeMethods.DestroyPhysicalMonitors(1, tmp); } catch { }
                        }

                        list.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    Settings.Log("EnumerateDdcMonitors 回调失败: " + ex.Message);
                }
                return true;   // 继续枚举
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }

        /// <summary>释放所有缓存的 DDC/CI 物理监视器句柄（程序退出时调用）</summary>
        public static void Cleanup()
        {
            foreach (var h in _physicalHandles)
            {
                try
                {
                    var arr = new NativeMethods.PHYSICAL_MONITOR[1];
                    arr[0].hPhysicalMonitor = h;
                    NativeMethods.DestroyPhysicalMonitors(1, arr);
                }
                catch { }
            }
            _physicalHandles.Clear();
        }
    }
}
