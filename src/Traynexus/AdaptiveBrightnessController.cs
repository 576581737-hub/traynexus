using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace Traynexus
{
    /// <summary>
    /// 自动亮度"单一控制源"管理。
    ///
    /// 根因：Traynexus 写内置屏亮度走 WMI WmiSetBrightness，与 Windows 自适应亮度(powercfg)
    /// 以及 AMD Vari-Bright 驱动级自适应亮度，作用在**同一亮度通道**上。三者同时开启会互相
    /// 覆盖（亮度跳变/抖动），而且 Vari-Bright 对暗内容产生负反馈、退出后不恢复，正是
    /// "微信截图后屏幕变暗不恢复"这类 bug 的成因。
    ///
    /// 原则：开启 Traynexus 自动亮度时**接管**亮度控制——关闭系统/OEM 自适应亮度，让只存在
    /// 一个控制源。关闭自动亮度或退出程序时恢复原状（受设置开关控制）。
    /// </summary>
    public static class AdaptiveBrightnessController
    {
        // Windows 电源设置：视频子组下的"自适应亮度"（ADAPTBRIGHT）
        // 注意：aded5e82-b909-4619-9949-f5d71dac0bcb 在本机是"显示器亮度等级"(VIDEONORMALLEVEL, 0-100%)，
        // 绝不能去禁用它（否则会把亮度设成 0 = 黑屏）。真正可开关的自适应亮度是下面这个 GUID。
        private const string SUB_VIDEO = "7516b95f-f776-4464-8c53-06167f40cc99";
        private const string VIDEOADAPT = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";
        // 显示设备类（用于探测 AMD Vari-Bright 注册表）
        private const string DISPLAY_CLASS = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static bool _takenOver;
        private static int? _savedWinAc;
        private static int? _savedWinDc;
        private static bool _variDisabled;
        private static string _savedVariBrightKey;
        private static string _savedVariBrightValueName;
        private static int? _savedVariBrightValue;

        /// <summary>当前是否已接管亮度控制（UI/诊断显示用）。</summary>
        public static bool IsTakenOver { get { return _takenOver; } }

        /// <summary>
        /// 接管亮度控制：关闭 Windows 自适应亮度，检测 AMD 并尝试关闭 Vari-Bright。
        /// 返回给用户的提示语（null 表示无需提示）。
        /// </summary>
        public static string TakeOver()
        {
            if (_takenOver) return null;
            var parts = new List<string>();

            string winMsg;
            bool win = DisableWindowsAdaptiveBrightness(out winMsg);
            if (win) parts.Add("已关闭 Windows 自适应亮度");
            else if (!string.IsNullOrEmpty(winMsg)) parts.Add(winMsg);

            if (IsAmdPresent())
            {
                if (DisableAmdVariBright()) parts.Add("已关闭 AMD Vari-Bright");
                else parts.Add("请保持 AMD Vari-Bright 关闭，避免与自动亮度冲突");
            }

            // 接管状态：只要真正禁用了其中一项即视为已接管
            _takenOver = win || _variDisabled;
            return parts.Count == 0 ? null : string.Join("；", parts);
        }

        /// <summary>释放接管：恢复系统原始亮度设置。</summary>
        public static void Release()
        {
            if (!_takenOver) return;
            try { RestoreWindowsAdaptiveBrightness(); }
            catch (Exception ex) { Settings.Log("AdaptiveBrightnessController.Release(Win) 失败: " + ex.Message); }
            try { RestoreAmdVariBright(); }
            catch (Exception ex) { Settings.Log("AdaptiveBrightnessController.Release(Vari) 失败: " + ex.Message); }
            _takenOver = false;
        }

        // ---------------------------------------------------------------
        // Windows 自适应亮度（powercfg）
        // ---------------------------------------------------------------
        private static bool DisableWindowsAdaptiveBrightness(out string msg)
        {
            msg = null;
            try
            {
                int? ac = QueryWindowsAdaptive(true);
                int? dc = QueryWindowsAdaptive(false);
                _savedWinAc = ac;
                _savedWinDc = dc;

                bool need = (ac ?? 0) != 0 || (dc ?? 0) != 0;
                if (!need) return true;   // 本来就是关的

                bool okAc = SetWindowsAdaptive(true, 0);
                bool okDc = SetWindowsAdaptive(false, 0);
                if (okAc && okDc) return true;

                msg = "需以管理员身份运行才能接管系统亮度";
                return false;
            }
            catch (Exception ex)
            {
                Settings.Log("DisableWindowsAdaptiveBrightness 失败: " + ex.Message);
                msg = "接管系统亮度失败（可能需要管理员权限）";
                return false;
            }
        }

        private static void RestoreWindowsAdaptiveBrightness()
        {
            if (_savedWinAc.HasValue) SetWindowsAdaptive(true, _savedWinAc.Value);
            if (_savedWinDc.HasValue) SetWindowsAdaptive(false, _savedWinDc.Value);
            _savedWinAc = null;
            _savedWinDc = null;
        }

        private static int? QueryWindowsAdaptive(bool ac)
        {
            string arg = "SCHEME_CURRENT " + SUB_VIDEO + " " + VIDEOADAPT;
            var psi = new ProcessStartInfo("powercfg.exe", "/query " + arg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string marker = ac ? "Current AC Power Setting Index" : "Current DC Power Setting Index";
                int idx = outp.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                int c = outp.IndexOf("0x", idx, StringComparison.OrdinalIgnoreCase);
                if (c < 0) return null;
                string hex = outp.Substring(c + 2, 8);
                int v;
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v)) return v;
            }
            return null;
        }

        private static bool SetWindowsAdaptive(bool ac, int value)
        {
            string verb = ac ? "setacvalueindex" : "setdcvalueindex";
            var psi = new ProcessStartInfo("powercfg.exe",
                string.Format("/{0} SCHEME_CURRENT {1} {2} {3}", verb, SUB_VIDEO, VIDEOADAPT, value))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
        }

        // ---------------------------------------------------------------
        // AMD Vari-Bright（注册表，仅切换已存在的值，绝不创建新值）
        // ---------------------------------------------------------------
        /// <summary>系统是否存在 AMD 显示适配器。</summary>
        public static bool IsAmdPresent()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT Name FROM Win32_VideoController WHERE Status='OK'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string name = (mo["Name"] as string) ?? "";
                        if (name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch (Exception ex) { Settings.Log("AdaptiveBrightnessController.IsAmdPresent 失败: " + ex.Message); }
            return false;
        }

        private static bool DisableAmdVariBright()
        {
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(
                    "SYSTEM\\CurrentControlSet\\Control\\Class\\" + DISPLAY_CLASS, false))
                {
                    if (cls == null) return false;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("0")) continue;   // 0000 / 0001 ...
                        using (var k = cls.OpenSubKey(sub, true))
                        {
                            if (k == null) continue;
                            object val = k.GetValue("VariBright");
                            if (val is int && (int)val != 0)
                            {
                                _savedVariBrightKey = "SYSTEM\\CurrentControlSet\\Control\\Class\\" + DISPLAY_CLASS + "\\" + sub;
                                _savedVariBrightValueName = "VariBright";
                                _savedVariBrightValue = (int)val;
                                k.SetValue("VariBright", 0, RegistryValueKind.DWord);
                                _variDisabled = true;
                                Settings.Log("AdaptiveBrightnessController 已关闭 Vari-Bright (" + sub + ")");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Settings.Log("DisableAmdVariBright 失败: " + ex.Message); }
            return false;
        }

        private static void RestoreAmdVariBright()
        {
            if (_savedVariBrightKey == null || _savedVariBrightValueName == null) return;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(_savedVariBrightKey, true))
                    if (k != null) k.SetValue(_savedVariBrightValueName, _savedVariBrightValue, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Settings.Log("RestoreAmdVariBright 失败: " + ex.Message); }
            finally
            {
                _savedVariBrightKey = null;
                _savedVariBrightValueName = null;
                _savedVariBrightValue = null;
                _variDisabled = false;
            }
        }
    }
}
