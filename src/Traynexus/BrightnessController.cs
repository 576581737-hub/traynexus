using System;
using System.Collections.Generic;
using System.Management;

namespace Traynexus
{
    /// <summary>
    /// 显示器亮度信息。
    /// </summary>
    public class MonitorInfo
    {
        public string Name;          // 显示器名称（如"内置显示器"）
        public int Brightness;       // 当前亮度 0-100，-1 表示不可读
        public bool IsInternal;      // 是否内置屏
    }

    /// <summary>
    /// 亮度控制器：通过 WMI WmiMonitorBrightnessMethods 读写内置屏亮度。
    /// 外接屏 DDC/CI 暂不支持（需 DeviceIoControl + 物理显示器句柄）。
    /// 线程安全：WMI 查询每次新建 ManagementObjectSearcher。
    /// </summary>
    public static class BrightnessController
    {
        /// <summary>
        /// 获取内置屏当前亮度。返回 -1 表示不支持或读取失败。
        /// </summary>
        public static int GetBrightness()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        return Convert.ToInt32(mo["CurrentBrightness"]);
                    }
                }
            }
            catch (Exception ex)
            {
                Settings.Log("BrightnessController.GetBrightness 失败: " + ex.Message);
            }
            return -1;
        }

        /// <summary>
        /// 设置内置屏亮度。percent 范围 0-100。返回是否成功。
        /// 需要管理员权限（app.manifest 已要求 requireAdministrator）。
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
                        // WmiSetBrightness(uint32 Timeout, uint8 Brightness)
                        // Timeout=0 表示立即生效
                        mo.InvokeMethod("WmiSetBrightness", new object[] { 0, (byte)percent });
                        return true;
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
        /// 检测系统是否支持亮度控制（WMI WmiMonitorBrightnessMethods 是否存在）。
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementObject mo in searcher.Get()) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 枚举所有显示器（当前只支持内置屏）。外接屏 DDC/CI 返回 IsInternal=false。
        /// </summary>
        public static List<MonitorInfo> EnumerateMonitors()
        {
            var list = new List<MonitorInfo>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    int idx = 1;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var info = new MonitorInfo();
                        info.IsInternal = true;
                        info.Name = "显示器 " + idx + (idx == 1 ? "（主屏）" : "");
                        try { info.Brightness = Convert.ToInt32(mo["CurrentBrightness"]); }
                        catch { info.Brightness = -1; }
                        list.Add(info);
                        idx++;
                    }
                }
            }
            catch (Exception ex)
            {
                Settings.Log("BrightnessController.EnumerateMonitors 失败: " + ex.Message);
            }
            return list;
        }
    }
}
