using System;

namespace Traynexus
{
    /// <summary>
    /// 一次内存快照。
    /// </summary>
    public class MemorySnapshot
    {
        public ulong TotalBytes;
        public ulong AvailBytes;
        public ulong UsedBytes;
        public int UsedPercent;        // 0-100
        public ulong CommitTotalBytes;
        public ulong CommitLimitBytes;
        public ulong SystemCacheBytes;
        public ulong PageFileTotalBytes;
        public ulong PageFileAvailBytes;

        public string FormatShort()
        {
            return string.Format("{0} / {1}  ({2}%)",
                FormatBytes(UsedBytes), FormatBytes(TotalBytes), UsedPercent);
        }

        public string FormatDetails()
        {
            return string.Format(
                "已用: {0}\r\n" +
                "可用: {1}\r\n" +
                "总量: {2}\r\n" +
                "系统缓存 (StandbyList 大致): {3}\r\n" +
                "提交: {4} / {5}\r\n" +
                "页面文件: {6} 可用 / {7} 总",
                FormatBytes(UsedBytes),
                FormatBytes(AvailBytes),
                FormatBytes(TotalBytes),
                FormatBytes(SystemCacheBytes),
                FormatBytes(CommitTotalBytes),
                FormatBytes(CommitLimitBytes),
                FormatBytes(PageFileAvailBytes),
                FormatBytes(PageFileTotalBytes));
        }

        public static string FormatBytes(ulong b)
        {
            const double K = 1024.0;
            double v = b;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= K && i < units.Length - 1) { v /= K; i++; }
            if (i <= 1) return string.Format("{0:F0} {1}", v, units[i]);
            return string.Format("{0:F1} {1}", v, units[i]);
        }
    }

    public static class MemoryInfo
    {
        public static MemorySnapshot Take()
        {
            var s = new MemorySnapshot();
            var m = new NativeMethods.MEMORYSTATUSEX();
            if (NativeMethods.GlobalMemoryStatusEx(m))
            {
                s.TotalBytes = m.ullTotalPhys;
                s.AvailBytes = m.ullAvailPhys;
                s.UsedBytes = m.ullTotalPhys - m.ullAvailPhys;
                s.UsedPercent = (int)m.dwMemoryLoad;
                s.PageFileTotalBytes = m.ullTotalPageFile;
                s.PageFileAvailBytes = m.ullAvailPageFile;
            }
            NativeMethods.PERFORMANCE_INFORMATION pi;
            uint cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(
                typeof(NativeMethods.PERFORMANCE_INFORMATION));
            if (NativeMethods.GetPerformanceInfo(out pi, cb))
            {
                ulong pageSize = (ulong)pi.PageSize.ToUInt64();
                s.CommitTotalBytes = (ulong)pi.CommitTotal.ToUInt64() * pageSize;
                s.CommitLimitBytes = (ulong)pi.CommitLimit.ToUInt64() * pageSize;
                s.SystemCacheBytes = (ulong)pi.SystemCache.ToUInt64() * pageSize;
            }
            return s;
        }
    }
}
