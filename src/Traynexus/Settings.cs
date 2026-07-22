using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Traynexus
{
    public enum ReleaseMode
    {
        StandbyOnly = 1,   // 只清 StandbyList（最安全）
        WorkingSetOnly = 2,// 只削工作集（联想同款）
        Both = 3           // 组合
    }

    public class Settings
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Traynexus", "error.log");

        // error.log 超过 1MB 时截断为只保留最近 512KB，避免无限增长
        private const long MaxLogBytes = 1L * 1024 * 1024;        // 1 MB
        private const long KeepLogBytes = 512L * 1024;             // 保留最近 512 KB

        /// <summary>
        /// 追加一行日志到 %APPDATA%\Traynexus\error.log。失败时静默。
        /// 仅用于关键 I/O 和异常路径，不用于常规流程。
        /// 文件超过 1MB 时自动截断为只保留最近 512KB。
        /// </summary>
        public static void Log(string msg)
        {
            try
            {
                // 大小检查与截断（截断失败不影响追加）
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                    {
                        string full = File.ReadAllText(LogPath, Encoding.UTF8);
                        int keepChars = (int)Math.Min(full.Length, KeepLogBytes);
                        string tail = full.Substring(full.Length - keepChars);
                        // 从下一行开头截，避免半行
                        int nl = tail.IndexOf('\n');
                        if (nl >= 0 && nl < tail.Length - 1) tail = tail.Substring(nl + 1);
                        File.WriteAllText(LogPath,
                            "---- 日志已截断（仅保留最近 " + KeepLogBytes / 1024 + " KB）----\r\n" + tail,
                            new UTF8Encoding(false));
                    }
                }
                catch { /* 截断失败不影响追加 */ }

                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public ReleaseMode Mode = ReleaseMode.Both;
        public bool ThresholdEnabled = false;
        public int ThresholdPercent = 80;

        // 充电管理：0=满充(100), 1=均衡(80), 2=保养(60)
        public int ChargeMode = 1;
        // 自定义充电上限百分比（50-100），由 ChargeMode 推导或用户滑块设定
        public int ChargeLimit = 80;

        // 亮度管理
        public bool AutoBrightness = false;

        // 计划任务
        public bool NightCareEnabled = false;    // 夜间自动保养 22:00-07:00 降至 60%
        public bool WeekendFullCharge = false;   // 周末满充准备

        // 更新检查
        public bool UpdateCheckEnabled = true;           // 启动时自动检查更新
        public string LastUpdateCheck = "";              // 上次检查时间（ISO 8601 字符串，避免 DateTime 序列化问题）

        public string ConfigDir;
        public string WhitelistPath;
        public string SettingsPath;

        // H1 修复：所有白名单读写必须持有此锁。
        // UI 线程（List_ItemCheck / Persist / Reload）与后台线程
        // （MemoryCleaner.Execute -> IsProtected）会并发访问这两个集合。
        public readonly object WhitelistLock = new object();

        public HashSet<string> UserWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 会话临时白名单：勾选后立即生效，但不写文件；程序退出即消失。
        /// 若要持久化需显式调用 PersistWhitelist()。
        /// </summary>
        public HashSet<string> SessionWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 合并检查：进程是否在任一白名单里。线程安全。
        /// </summary>
        public bool IsInWhitelist(string procName)
        {
            lock (WhitelistLock)
            {
                return WhitelistSnapshot.MatchInSet(UserWhitelist, procName) ||
                       WhitelistSnapshot.MatchInSet(SessionWhitelist, procName);
            }
        }

        /// <summary>
        /// 把 SessionWhitelist 合并到 UserWhitelist 并写文件。
        /// H1/H9 修复：先写文件成功再清会话，写失败回滚，返回是否真的成功。
        /// 二次审计 P1-1 修复：只回滚本次真正新增的项，避免误删原有持久化项。
        /// 三次审计 P2 修复：文件 I/O 挪出锁外，避免阻塞后台释放线程。
        /// </summary>
        public bool PersistWhitelist()
        {
            List<string> added;
            string contentToWrite;

            // 第一步：在锁内改内存状态 + 构造要写入的内容
            lock (WhitelistLock)
            {
                added = new List<string>();
                foreach (var n in SessionWhitelist)
                {
                    if (UserWhitelist.Add(n)) added.Add(n);
                }
                contentToWrite = BuildWhitelistContent();
            }

            // 第二步：锁外做文件 I/O（可能耗时几十到几百毫秒）
            bool ok = TryWriteWhitelistFile(contentToWrite);

            // 第三步：锁内根据 I/O 结果决定提交还是回滚
            lock (WhitelistLock)
            {
                if (ok)
                {
                    SessionWhitelist.Clear();
                    return true;
                }
                else
                {
                    // 写失败：只撤回本次真正新增的项，保留原有持久化项不变。
                    // SessionWhitelist 不清空，用户可重试保存。
                    foreach (var n in added) UserWhitelist.Remove(n);
                    return false;
                }
            }
        }

        /// <summary>
        /// 从持久化白名单里移除若干条目并立即写文件。返回是否成功。
        /// 文件 I/O 挪出锁外。
        /// </summary>
        public bool RemovePersisted(IEnumerable<string> namesToRemove)
        {
            if (namesToRemove == null) return true;

            List<string> removed;
            string contentToWrite;

            lock (WhitelistLock)
            {
                removed = new List<string>();
                foreach (var n in namesToRemove)
                {
                    if (UserWhitelist.Remove(n)) removed.Add(n);
                    string bare = n;
                    if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        bare = bare.Substring(0, bare.Length - 4);
                        if (UserWhitelist.Remove(bare)) removed.Add(bare);
                    }
                    else
                    {
                        if (UserWhitelist.Remove(n + ".exe")) removed.Add(n + ".exe");
                    }
                }
                contentToWrite = BuildWhitelistContent();
            }

            bool ok = TryWriteWhitelistFile(contentToWrite);

            if (!ok)
            {
                lock (WhitelistLock)
                {
                    // 回滚
                    foreach (var r in removed) UserWhitelist.Add(r);
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// 构造 whitelist.txt 的完整内容（仅读 UserWhitelist，调用方必须持有 WhitelistLock）。
        /// </summary>
        private string BuildWhitelistContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 每行一个进程名（含或不含 .exe），保护它们不被 WorkingSet 削减。");
            sb.AppendLine("# 井号 # 开头的是注释。系统关键进程已经内置保护，无需列出。");
            sb.AppendLine("# ---");
            var sorted = new List<string>(UserWhitelist);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in sorted) sb.AppendLine(n);
            return sb.ToString();
        }

        /// <summary>
        /// 把内容原子写入 whitelist.txt（先写 .tmp 再 File.Replace）。
        /// 纯 I/O 操作，不访问共享状态，无需持锁。
        /// </summary>
        private bool TryWriteWhitelistFile(string content)
        {
            string tmp = WhitelistPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(WhitelistPath))
                    File.Replace(tmp, WhitelistPath, null);
                else
                    File.Move(tmp, WhitelistPath);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            s.ConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Traynexus");
            Directory.CreateDirectory(s.ConfigDir);
            s.SettingsPath = Path.Combine(s.ConfigDir, "settings.ini");
            s.WhitelistPath = Path.Combine(s.ConfigDir, "whitelist.txt");

            // 读设置
            if (File.Exists(s.SettingsPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(s.SettingsPath))
                    {
                        var l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        int eq = l.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = l.Substring(0, eq).Trim();
                        string v = l.Substring(eq + 1).Trim();
                        if (k.Equals("Mode", StringComparison.OrdinalIgnoreCase))
                        {
                            int iv; if (int.TryParse(v, out iv))
                            {
                                // P2-3 修复：校验枚举合法性
                                if (iv >= 1 && iv <= 3)
                                    s.Mode = (ReleaseMode)iv;
                            }
                        }
                        else if (k.Equals("ThresholdEnabled", StringComparison.OrdinalIgnoreCase))
                            s.ThresholdEnabled = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k.Equals("ThresholdPercent", StringComparison.OrdinalIgnoreCase))
                        {
                            int iv; if (int.TryParse(v, out iv))
                            {
                                // P2-3 修复：校验范围 1-99
                                if (iv >= 1 && iv <= 99) s.ThresholdPercent = iv;
                            }
                        }
                        else if (k.Equals("ChargeMode", StringComparison.OrdinalIgnoreCase))
                        {
                            int iv; if (int.TryParse(v, out iv))
                            {
                                if (iv >= 0 && iv <= 2) s.ChargeMode = iv;
                            }
                        }
                        else if (k.Equals("ChargeLimit", StringComparison.OrdinalIgnoreCase))
                        {
                            int iv; if (int.TryParse(v, out iv))
                            {
                                if (iv >= 50 && iv <= 100) s.ChargeLimit = iv;
                            }
                        }
                        else if (k.Equals("AutoBrightness", StringComparison.OrdinalIgnoreCase))
                            s.AutoBrightness = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k.Equals("NightCareEnabled", StringComparison.OrdinalIgnoreCase))
                            s.NightCareEnabled = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k.Equals("WeekendFullCharge", StringComparison.OrdinalIgnoreCase))
                            s.WeekendFullCharge = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k.Equals("UpdateCheckEnabled", StringComparison.OrdinalIgnoreCase))
                            s.UpdateCheckEnabled = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k.Equals("LastUpdateCheck", StringComparison.OrdinalIgnoreCase))
                            s.LastUpdateCheck = v;
                    }
                }
                catch (Exception ex) { Log("Settings.Load 解析失败: " + ex.Message); }
            }

            // 读白名单
            if (!File.Exists(s.WhitelistPath))
            {
                try
                {
                    File.WriteAllText(s.WhitelistPath,
                        "# 每行一个进程名（含 .exe），保护它们不被 WorkingSet 削减。\r\n" +
                        "# 井号 # 开头的是注释。系统关键进程已经内置保护，无需列出。\r\n" +
                        "# 例：\r\n" +
                        "# MyBigIDE.exe\r\n" +
                        "# WeChat.exe\r\n",
                        new UTF8Encoding(false));
                }
                catch (Exception ex) { Log("Settings.Load 创建默认白名单失败: " + ex.Message); }
            }
            else
            {
                try
                {
                    foreach (var line in File.ReadAllLines(s.WhitelistPath))
                    {
                        var l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        s.UserWhitelist.Add(l);
                    }
                }
                catch (Exception ex) { Log("Settings.Load 读取白名单失败: " + ex.Message); }
            }
            return s;
        }

        /// <summary>
        /// 保存 settings.ini（原子写入）。返回 true 表示写盘成功，false 表示失败。
        /// 调用方可据此提示用户或回滚内存状态。
        /// </summary>
        public bool Save()
        {
            // P2-2 修复：原子写入（与 WriteWhitelistFile 一致）
            var sb = new StringBuilder();
            sb.AppendLine("# Traynexus settings");
            sb.AppendLine("Mode=" + (int)Mode);
            sb.AppendLine("ThresholdEnabled=" + (ThresholdEnabled ? "1" : "0"));
            sb.AppendLine("ThresholdPercent=" + ThresholdPercent);
            sb.AppendLine("ChargeMode=" + ChargeMode);
            sb.AppendLine("ChargeLimit=" + ChargeLimit);
            sb.AppendLine("AutoBrightness=" + (AutoBrightness ? "1" : "0"));
            sb.AppendLine("NightCareEnabled=" + (NightCareEnabled ? "1" : "0"));
            sb.AppendLine("WeekendFullCharge=" + (WeekendFullCharge ? "1" : "0"));
            sb.AppendLine("UpdateCheckEnabled=" + (UpdateCheckEnabled ? "1" : "0"));
            sb.AppendLine("LastUpdateCheck=" + (LastUpdateCheck ?? ""));

            string tmp = SettingsPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(SettingsPath))
                    File.Replace(tmp, SettingsPath, null);
                else
                    File.Move(tmp, SettingsPath);
                return true;
            }
            catch (Exception ex)
            {
                Log("Settings.Save 失败: " + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        public void ReloadWhitelist()
        {
            // 锁外读文件（慢操作）
            List<string> lines = null;
            if (File.Exists(WhitelistPath))
            {
                try
                {
                    lines = new List<string>();
                    foreach (var line in File.ReadAllLines(WhitelistPath))
                    {
                        var l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        lines.Add(l);
                    }
                }
                catch (Exception ex) { Log("ReloadWhitelist 失败: " + ex.Message); return; }
            }

            // 锁内只更新内存状态
            lock (WhitelistLock)
            {
                UserWhitelist.Clear();
                if (lines != null)
                {
                    foreach (var l in lines) UserWhitelist.Add(l);
                }
            }
        }

        /// <summary>
        /// 在锁内返回 UserWhitelist / SessionWhitelist 的快照副本，供后台线程安全读取。
        /// </summary>
        public WhitelistSnapshot SnapshotWhitelists()
        {
            lock (WhitelistLock)
            {
                return new WhitelistSnapshot
                {
                    User = new HashSet<string>(UserWhitelist, StringComparer.OrdinalIgnoreCase),
                    Session = new HashSet<string>(SessionWhitelist, StringComparer.OrdinalIgnoreCase),
                };
            }
        }
    }

    /// <summary>
    /// 白名单快照：让后台线程拿到一份不可变副本，避免持锁遍历进程。
    /// </summary>
    public class WhitelistSnapshot
    {
        public HashSet<string> User;
        public HashSet<string> Session;

        /// <summary>
        /// 统一的白名单匹配逻辑：检查进程名（含/不含 .exe）是否在指定集合中。
        /// 所有白名单判断都应通过此方法，避免匹配规则修改时多处不同步。
        /// </summary>
        public static bool MatchInSet(HashSet<string> set, string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            string bare = procName;
            if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 4);
            return set.Contains(procName) || set.Contains(procName + ".exe") ||
                   set.Contains(bare) || set.Contains(bare + ".exe");
        }

        public bool Contains(string procName)
        {
            return MatchInSet(User, procName) || MatchInSet(Session, procName);
        }

        /// <summary>
        /// 进程是否在会话白名单中（不排除同时在 User 中的情况）。
        /// </summary>
        public bool IsInSession(string procName)
        {
            return MatchInSet(Session, procName);
        }

        /// <summary>
        /// 进程是否在持久化（User）白名单中。
        /// </summary>
        public bool IsInUser(string procName)
        {
            return MatchInSet(User, procName);
        }
    }
}
