using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 开机自启管理：通过 schtasks 创建/查询/删除任务计划，
    /// 以管理员权限（/RL HIGHEST）静默启动，不弹 UAC。
    /// 从 TrayContext 提取，便于后续控制台调用。
    /// </summary>
    public static class AutoStartManager
    {
        private const string TaskName = "Traynexus_AutoStart";
        private const string OldTaskName = "MemTrayCN_AutoStart";

        /// <summary>
        /// 当前任务计划是否已存在（即自启已启用）。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Settings.Log("AutoStartManager.IsEnabled 失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 启用开机自启：创建任务计划（ONLOGON, /RL HIGHEST）。
        /// </summary>
        public static bool Enable()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                // 防御性校验：Windows 路径不会含双引号，若含则拒绝创建任务，避免破坏 schtasks 命令行转义。
                // 正常路径经 /TR "\"<path>\"" 转义后，schtasks 实际收到 /TR "C:\...\Traynexus.exe"。
                if (exePath == null || exePath.IndexOf('"') >= 0) return false;
                string trArgs = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exePath + "\\\"\" /SC ONLOGON /RL HIGHEST /F";
                var psi = new ProcessStartInfo("schtasks.exe", trArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Settings.Log("AutoStartManager.Enable 失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 关闭开机自启：删除任务计划。
        /// 检查 ExitCode，删除失败（任务不存在/权限不足）返回 false。
        /// </summary>
        public static bool Disable()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe",
                    "/Delete /TN \"" + TaskName + "\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Settings.Log("AutoStartManager.Disable 失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 清理旧版本（MemTrayCN）遗留的任务计划。首次运行时调用。
        /// 失败静默，不影响主流程。
        /// </summary>
        public static void CleanupOldTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe",
                    "/Query /TN \"" + OldTaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return; }
                    if (p.ExitCode != 0) return; // 旧任务不存在，无需清理
                }

                var del = new ProcessStartInfo("schtasks.exe",
                    "/Delete /TN \"" + OldTaskName + "\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(del)) { if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } } }
            }
            catch (Exception ex) { Settings.Log("AutoStartManager.CleanupOldTask 失败（不影响启动）: " + ex.Message); }
        }
    }
}
