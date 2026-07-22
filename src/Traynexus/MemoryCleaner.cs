using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Traynexus
{
    public class ReleaseResult
    {
        public ReleaseMode Mode;
        public int TrimmedCount;
        public int SkippedByBlacklist;
        public int SkippedByWhitelist;
        public int FailedByAccess;
        public int FailedByProcessExit;   // OpenProcess 返回 ERROR_INVALID_PARAMETER (进程已退出)
        public bool StandbyPurged;
        public bool WorkingSetsEmptied;
        public string StandbyError = "";
        public ulong BeforeUsedBytes;
        public ulong AfterUsedBytes;

        public string FormatSummary()
        {
            long delta = (long)AfterUsedBytes - (long)BeforeUsedBytes;
            string sign = delta >= 0 ? "+" : "-";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("模式: " + Mode);
            sb.AppendLine("释放前已用: " + MemorySnapshot.FormatBytes(BeforeUsedBytes));
            sb.AppendLine("释放后已用: " + MemorySnapshot.FormatBytes(AfterUsedBytes));
            sb.AppendLine("变化: " + sign + MemorySnapshot.FormatBytes((ulong)Math.Abs(delta)));
            if ((Mode & ReleaseMode.StandbyOnly) == ReleaseMode.StandbyOnly)
            {
                sb.AppendLine("StandbyList: " + (StandbyPurged ? "已清理" :
                    ("失败 " + (string.IsNullOrEmpty(StandbyError) ? "" : "(" + StandbyError + ")"))));
                sb.AppendLine("EmptyWorkingSets: " + (WorkingSetsEmptied ? "已执行" : "未执行"));
            }
            if ((Mode & ReleaseMode.WorkingSetOnly) == ReleaseMode.WorkingSetOnly)
            {
                sb.AppendLine("削减进程工作集: " + TrimmedCount + " 个成功, "
                    + FailedByAccess + " 个权限不足, "
                    + FailedByProcessExit + " 个已退出, "
                    + SkippedByBlacklist + " 个系统进程受保护, "
                    + SkippedByWhitelist + " 个用户白名单跳过");
            }
            return sb.ToString();
        }
    }

    public static class MemoryCleaner
    {
        // 硬编码保护清单 -- 系统关键进程永远不动
        // 注意用小写方便忽略大小写比较
        // 唯一来源：ReleasePanel 等其他使用方应直接引用此集合，不再各自维护副本。
        public static readonly HashSet<string> HardBlacklist = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "System Idle Process", "Registry",
            "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "lsm", "svchost", "dwm", "explorer",
            "fontdrvhost", "MemCompression", "Memory Compression",
            "audiodg", "conhost", "ctfmon", "spoolsv",
            "LsaIso", "WUDFHost", "dllhost", "taskhostw",
            "SearchIndexer", "SearchHost", "SearchApp",
            "StartMenuExperienceHost", "ShellExperienceHost",
            "RuntimeBroker", "sihost", "ApplicationFrameHost",
            "Traynexus",
            // Windows Defender / AV: 谨慎起见也保护
            "MsMpEng", "SecurityHealthService", "SecurityHealthSystray",
            "NisSrv",
        };

        private static bool IsProtected(string procName, Settings s, WhitelistSnapshot snap)
        {
            if (string.IsNullOrEmpty(procName)) return true;
            // 传进来的 procName 可能不带 .exe（Process.ProcessName 已经去掉了后缀）
            string bare = procName;
            if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 4);
            if (HardBlacklist.Contains(bare)) return true;
            // 用户白名单 + 会话白名单（线程安全快照）
            if (snap.Contains(procName)) return true;
            return false;
        }

        public static bool EnablePrivilege(string privilegeName)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out token))
                    return false;

                NativeMethods.LUID luid;
                if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out luid))
                    return false;

                var tp = new NativeMethods.TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = NativeMethods.SE_PRIVILEGE_ENABLED;

                if (!NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0,
                    IntPtr.Zero, IntPtr.Zero))
                    return false;

                // AdjustTokenPrivileges 的著名陷阱：返回 true 也可能只是部分成功，
                // 必须用 GetLastError==ERROR_NOT_ALL_ASSIGNED(1300) 判断。
                // 注意：成功时不保证 LastError 被清零，所以只能判 1300，不能判 ==0。
                int err = Marshal.GetLastWin32Error();
                return err != 1300;
            }
            finally
            {
                if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            }
        }

        /// <summary>
        /// 执行 StandbyList 清理。需要 SeProfileSingleProcessPrivilege。
        /// </summary>
        public static bool PurgeStandbyList(out string error)
        {
            error = "";
            if (!EnablePrivilege(NativeMethods.SeProfileSingleProcessPrivilege))
            {
                error = "无法启用 SeProfileSingleProcessPrivilege（需要管理员权限）";
                return false;
            }
            int cmd = NativeMethods.MemoryPurgeStandbyList;
            int nt = NativeMethods.NtSetSystemInformation(
                NativeMethods.SystemMemoryListInformation,
                ref cmd, sizeof(int));
            if (nt != 0)
            {
                error = string.Format("NtSetSystemInformation 返回 0x{0:X8}", nt);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 触发 SystemMemoryList 的 MemoryEmptyWorkingSets：
        /// 由系统内核统一清空所有可清理进程的工作集，比手动一个个 SetProcessWorkingSetSize
        /// 更"干净"，也天然避开了 System/Registry 这类不允许 OpenProcess 的进程。
        /// </summary>
        public static bool EmptyAllWorkingSets(out string error)
        {
            error = "";
            if (!EnablePrivilege(NativeMethods.SeProfileSingleProcessPrivilege) ||
                !EnablePrivilege(NativeMethods.SeIncreaseQuotaPrivilege))
            {
                error = "特权不足";
                return false;
            }
            int cmd = NativeMethods.MemoryEmptyWorkingSets;
            int nt = NativeMethods.NtSetSystemInformation(
                NativeMethods.SystemMemoryListInformation,
                ref cmd, sizeof(int));
            if (nt != 0)
            {
                error = string.Format("NtSetSystemInformation 返回 0x{0:X8}", nt);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 联想同款：逐个进程 SetProcessWorkingSetSize(-1, -1)。
        /// 我们在此基础上加了黑名单/白名单。
        /// </summary>
        private static void TrimAllProcesses(Settings s, WhitelistSnapshot snap, ReleaseResult r)
        {
            IntPtr minus1 = new IntPtr(-1);
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }

            foreach (var p in procs)
            {
                string name = "";
                try { name = p.ProcessName; } catch { }

                if (IsProtected(name, s, snap))
                {
                    if (HardBlacklist.Contains(name))
                        r.SkippedByBlacklist++;
                    else
                        r.SkippedByWhitelist++;
                    try { p.Dispose(); } catch { }
                    continue;
                }

                IntPtr h = IntPtr.Zero;
                try
                {
                    h = NativeMethods.OpenProcess(
                        NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                        false, (uint)p.Id);
                }
                catch (Exception ex) { Settings.Log("MemoryCleaner.OpenProcess 失败: " + ex.Message); r.FailedByAccess++; try { p.Dispose(); } catch { } continue; }

                if (h == IntPtr.Zero)
                {
                    // 区分失败原因：ERROR_ACCESS_DENIED(5) = 权限不足（如 PPL 进程），
                    // ERROR_INVALID_PARAMETER(87) = 进程已退出。
                    int le = Marshal.GetLastWin32Error();
                    if (le == 87) r.FailedByProcessExit++;
                    else r.FailedByAccess++;
                }
                else
                {
                    try
                    {
                        if (NativeMethods.SetProcessWorkingSetSize(h, minus1, minus1))
                            r.TrimmedCount++;
                        else
                            r.FailedByAccess++;
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(h);
                    }
                }
                try { p.Dispose(); } catch { }
            }
        }

        public class PreviewItem
        {
            public string Name;
            public int Pid;
            public long WorkingSet;
            public bool Protected;      // true = 会被跳过（黑名单/白名单）
            public string ProtectReason;
        }

        /// <summary>
        /// 生成干跑预览列表（不执行任何释放操作）。
        /// includeProtected=true 时也包含会被保护跳过的进程，方便用户核对为什么某进程没释放。
        /// </summary>
        public static List<PreviewItem> PreviewTargets(Settings s, bool includeProtected)
        {
            var list = new List<PreviewItem>();
            // 拿一份不可变快照，避免遍历期间被 UI 线程修改
            var snap = s.SnapshotWhitelists();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return list; }

            foreach (var p in procs)
            {
                string name = "";
                int pid = 0;
                try { name = p.ProcessName; pid = p.Id; } catch { }
                if (string.IsNullOrEmpty(name)) { try { p.Dispose(); } catch { } continue; }

                bool prot = IsProtected(name, s, snap);
                string reason = "";
                if (prot)
                {
                    string bare = name;
                    if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        bare = bare.Substring(0, bare.Length - 4);
                    if (HardBlacklist.Contains(bare)) reason = "系统进程";
                    else if (snap.IsInSession(name)) reason = "会话临时保护";
                    else reason = "用户白名单";
                }

                if (prot && !includeProtected)
                {
                    try { p.Dispose(); } catch { }
                    continue;
                }

                long ws = 0;
                try { ws = p.WorkingSet64; } catch { }

                list.Add(new PreviewItem {
                    Name = name, Pid = pid, WorkingSet = ws,
                    Protected = prot, ProtectReason = reason
                });
                try { p.Dispose(); } catch { }
            }
            return list;
        }

        public static ReleaseResult Execute(Settings s)
        {
            var r = new ReleaseResult();
            r.Mode = s.Mode;
            var before = MemoryInfo.Take();
            r.BeforeUsedBytes = before.UsedBytes;

            bool doStandby = (s.Mode == ReleaseMode.StandbyOnly || s.Mode == ReleaseMode.Both);
            bool doWorking = (s.Mode == ReleaseMode.WorkingSetOnly || s.Mode == ReleaseMode.Both);

            if (doStandby)
            {
                string err;
                r.StandbyPurged = PurgeStandbyList(out err);
                r.StandbyError = err;
                // 顺便调一次 EmptyWorkingSets（对系统更安全，让内核自己决定）
                string wsErr;
                r.WorkingSetsEmptied = EmptyAllWorkingSets(out wsErr);
                if (!r.WorkingSetsEmptied && !string.IsNullOrEmpty(wsErr))
                    r.StandbyError = string.IsNullOrEmpty(r.StandbyError) ? wsErr : (r.StandbyError + "; " + wsErr);
            }

            if (doWorking)
            {
                // 取一份白名单快照，避免遍历进程期间被 UI 线程修改 HashSet
                var snap = s.SnapshotWhitelists();
                TrimAllProcesses(s, snap, r);
            }

            // 让内核有时间反映
            System.Threading.Thread.Sleep(500);
            var after = MemoryInfo.Take();
            r.AfterUsedBytes = after.UsedBytes;
            return r;
        }
    }
}
