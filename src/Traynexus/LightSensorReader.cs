using System;
using System.Diagnostics;
using System.Management;
using System.Text;

namespace Traynexus
{
    /// <summary>
    /// 环境光传感器读取器。
    ///
    /// 两段式设计：
    /// 1. IsAvailable()：用 WMI 查 PnP 传感器设备（Class=Sensor, Status=OK），不依赖 WinRT。
    ///    这能检测到 HID 传感器集合等非标准 ALS 设备（如 ITE8350），避免 WinRT GetDefault() 返回 null 的误报。
    /// 2. GetLux()：仍走 WinRT LightSensor 读取 lux 值。部分传感器硬件存在但驱动未正确暴露给 WinRT，
    ///    此时返回 null，调用方据此提示"传感器数据不可用"而非"未检测到传感器"。
    /// </summary>
    public static class LightSensorReader
    {
        // PowerShell 脚本：激活 WinRT LightSensor，读取当前照度，输出 JSON
        private const string PsScript =
            "[Windows.Devices.Sensors.LightSensor,Windows.Devices.Sensors,ContentType=WindowsRuntime] | Out-Null; " +
            "$s = [Windows.Devices.Sensors.LightSensor]::GetDefault(); " +
            "if ($s -eq $null) { '{\"hasSensor\":false}'; exit } " +
            "$r = $s.GetCurrentReading(); " +
            "if ($r -eq $null) { '{\"hasSensor\":false}'; exit } " +
            "('{\"hasSensor\":true,\"lux\":' + ($r.IlluminanceInLux).ToString() + '}')";

        /// <summary>
        /// 缓存：传感器硬件是否存在（探测一次即可，硬件不会热插拔）。
        /// null=未探测，true=有硬件，false=无硬件。
        /// </summary>
        private static bool? _sensorAvailable;
        private static readonly object _probeLock = new object();

        /// <summary>
        /// 探测系统是否有传感器硬件。用 WMI 查 PnP 设备（Class=Sensor, Status=OK），不依赖 WinRT。
        /// 能检测到 HID 传感器集合等非标准 ALS 设备。
        /// </summary>
        public static bool IsAvailable()
        {
            if (_sensorAvailable.HasValue) return _sensorAvailable.Value;
            lock (_probeLock)
            {
                if (_sensorAvailable.HasValue) return _sensorAvailable.Value;
                _sensorAvailable = ProbeSensorViaWmi();
                return _sensorAvailable.Value;
            }
        }

        /// <summary>清除探测缓存（诊断页"重新检测"时调用）。</summary>
        public static void InvalidateCache()
        {
            lock (_probeLock) { _sensorAvailable = null; }
        }

        /// <summary>
        /// 读取当前环境光照度（lux）。走 WinRT LightSensor API。
        /// 传感器硬件存在但驱动未暴露给 WinRT 时返回 null（调用方应提示"数据不可用"而非"未检测到"）。
        /// </summary>
        public static float? GetLux()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + PsScript + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        Settings.Log("LightSensorReader.GetLux 超时");
                        return null;
                    }
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    if (string.IsNullOrEmpty(output)) return null;
                    return ParseLux(output);
                }
            }
            catch (Exception ex)
            {
                Settings.Log("LightSensorReader.GetLux 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 用 WMI 查 PnP 传感器设备是否存在（Class=Sensor 且 Status=OK）。
        /// 比 WinRT GetDefault() 更宽松，能检测到 HID 传感器集合等非标准设备。
        /// </summary>
        private static bool ProbeSensorViaWmi()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT * FROM Win32_PnPEntity WHERE ClassGuid='{5175D334-C371-4806-B3BA-71FD53C9258D}' AND Status='OK'"))
                {
                    foreach (var mo in searcher.Get())
                        return true;   // 只要有一个 OK 状态的传感器设备就算可用
                }
            }
            catch (Exception ex)
            {
                Settings.Log("LightSensorReader.ProbeSensorViaWmi 失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>从 JSON 输出解析 lux 值。</summary>
        private static float? ParseLux(string json)
        {
            // {"hasSensor":true,"lux":42.5}
            int idx = json.IndexOf("\"lux\":");
            if (idx < 0) return null;
            string rest = json.Substring(idx + 6);
            var sb = new StringBuilder();
            foreach (char c in rest)
            {
                if (char.IsDigit(c) || c == '.' || c == '-') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            float lux;
            if (float.TryParse(sb.ToString(), out lux)) return lux;
            return null;
        }

        /// <summary>
        /// 将 lux 值映射到亮度百分比（0-100）。
        /// 经验曲线：暗环境低亮度，亮环境高亮度，避免突变。
        /// </summary>
        public static int LuxToBrightness(float lux)
        {
            if (lux < 0) lux = 0;
            if (lux < 10) return 20;
            if (lux < 50) return 40;
            if (lux < 200) return 60;
            if (lux < 1000) return 80;
            return 100;
        }
    }
}
