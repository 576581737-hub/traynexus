using System;
using System.Diagnostics;
using System.Management;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

namespace Traynexus
{
    /// <summary>
    /// 环境光传感器读取器。
    ///
    /// 两段式设计：
    /// 1. IsAvailable()：用 WMI 查 PnP 传感器设备（Class=Sensor, Status=OK），不依赖 WinRT。
    ///    这能检测到 HID 传感器集合等非标准 ALS 设备（如 ITE8350），避免 WinRT GetDefault() 返回 null 的误报。
    /// 2. GetLux()：走 WinRT LightSensor 读取 lux 值。部分传感器硬件存在但驱动未正确暴露给 WinRT，
    ///    此时返回 null，调用方据此提示"传感器数据不可用"而非"未检测到传感器"。
    ///
    /// v1.0725.1 优化：GetLux 复用**常驻 PowerShell Runspace**（仅首次加载 WinRT 类型），
    /// 不再每次读取都新开 powershell.exe 进程（原实现每 10~15s 一次，浪费且慢）。
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

        // 常驻 Runspace（懒加载，进程级复用）。读取失败/超时则重置重建。
        private static Runspace _rs;
        private static readonly object _rsLock = new object();

        private static Runspace GetRunspace()
        {
            lock (_rsLock)
            {
                if (_rs == null)
                {
                    _rs = RunspaceFactory.CreateRunspace();
                    _rs.Open();
                }
                return _rs;
            }
        }

        private static void ResetRunspace()
        {
            lock (_rsLock)
            {
                if (_rs != null)
                {
                    try { _rs.Dispose(); } catch { }
                    _rs = null;
                }
            }
        }

        /// <summary>
        /// 缓存：传感器硬件是否存在（探测一次即可，硬件不会热插拔）。
        /// null=未探测，true=有硬件，false=无硬件。
        /// </summary>
        private static bool? _sensorAvailable;
        private static readonly object _probeLock = new object();

        /// <summary>探测系统是否有传感器硬件。用 WMI 查 PnP 设备（Class=Sensor, Status=OK），不依赖 WinRT。</summary>
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
        /// 读取当前环境光照度（lux）。走 WinRT LightSensor API（复用常驻 Runspace）。
        /// 传感器硬件存在但驱动未暴露给 WinRT 时返回 null（调用方应提示"数据不可用"而非"未检测到"）。
        /// </summary>
        public static float? GetLux()
        {
            try
            {
                Runspace rs = GetRunspace();
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = rs;
                    ps.AddScript(PsScript);
                    // 超时保护：WinRT 读取极快，若异常挂起则重置 Runspace 避免永久卡死
                    var t = System.Threading.Tasks.Task.Factory.StartNew(() => ps.Invoke());
                    if (!t.Wait(5000))
                    {
                        Settings.Log("LightSensorReader.GetLux 超时，重置 Runspace");
                        ResetRunspace();
                        return null;
                    }
                    var results = t.Result;
                    if (ps.HadErrors || results == null || results.Count == 0) return null;
                    object first = results.Count > 0 ? results[0] : null;
                    string output = (first == null ? "" : first.ToString()).Trim();
                    if (string.IsNullOrEmpty(output)) return null;
                    return ParseLux(output);
                }
            }
            catch (Exception ex)
            {
                Settings.Log("LightSensorReader.GetLux 失败: " + ex.Message);
                ResetRunspace();
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
        /// 将 lux 值映射到亮度百分比（0-100）。连续分段线性曲线（锚点插值），
        /// 比旧版 5 档硬阶梯更平滑，避免 lux 在档位边界反复横跳造成亮度抖动。
        /// </summary>
        public static int LuxToBrightness(float lux)
        {
            if (lux < 0) lux = 0;
            // (lux, 亮度%) 锚点：暗环境低亮度，亮环境高亮度
            var anchors = new[] {
                new Tuple<float,float>(0f, 20f),
                new Tuple<float,float>(10f, 28f),
                new Tuple<float,float>(50f, 42f),
                new Tuple<float,float>(200f, 60f),
                new Tuple<float,float>(1000f, 82f),
                new Tuple<float,float>(5000f, 100f),
            };
            if (lux <= anchors[0].Item1) return (int)anchors[0].Item2;
            if (lux >= anchors[anchors.Length - 1].Item1) return 100;
            for (int i = 1; i < anchors.Length; i++)
            {
                if (lux <= anchors[i].Item1)
                {
                    float x0 = anchors[i - 1].Item1, y0 = anchors[i - 1].Item2;
                    float x1 = anchors[i].Item1, y1 = anchors[i].Item2;
                    float tt = (lux - x0) / (x1 - x0);
                    return (int)Math.Round(y0 + (y1 - y0) * tt);
                }
            }
            return 100;
        }
    }
}
