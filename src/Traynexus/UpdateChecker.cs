using System;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Traynexus
{
    /// <summary>
    /// 更新检查器：通过 GitHub Releases API 检查新版本。
    /// 使用 System.Net.WebClient（System.dll 自带，无需额外引用）。
    /// 匿名请求限流 60次/小时，本程序启动后检查一次 + 每 24h 节流，远低于限制。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>GitHub Releases API 端点（latest release）</summary>
        private const string RepoApiUrl = "https://api.github.com/repos/576581737-hub/traynexus/releases/latest";

        /// <summary>仓库主页（检查更新按钮跳转用）</summary>
        private const string RepoUrl = "https://github.com/576581737-hub/traynexus/releases";

        /// <summary>当前应用版本号（与 MainForm lblVer / .iss AppVersion 保持一致）</summary>
        public const string CurrentVersion = "1.0722.1";

        /// <summary>检查结果</summary>
        public class UpdateResult
        {
            public bool HasUpdate;
            public string LatestVersion;   // 不含 v 前缀，如 "1.0722.2"
            public string ReleaseUrl;      // Release 页面 URL
        }

        /// <summary>
        /// 同步检查最新版本（应在后台线程调用）。
        /// 超时 8 秒。网络异常/解析失败返回 HasUpdate=false。
        /// </summary>
        public static UpdateResult Check()
        {
            var result = new UpdateResult { HasUpdate = false, LatestVersion = CurrentVersion, ReleaseUrl = RepoUrl };
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    // GitHub API 要求 User-Agent，否则返回 403
                    wc.Headers[HttpRequestHeader.UserAgent] = "Traynexus/" + CurrentVersion;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";

                    string json;
                    try
                    {
                        json = wc.DownloadString(RepoApiUrl);
                    }
                    catch (WebException wex)
                    {
                        // 404 表示还没有 release
                        Settings.Log("UpdateChecker.Check 网络失败: " + wex.Message);
                        return result;
                    }

                    // 手写 JSON 解析（不引入 Newtonsoft.Json）
                    string tag = ExtractJsonString(json, "tag_name");
                    string htmlUrl = ExtractJsonString(json, "html_url");

                    if (string.IsNullOrEmpty(tag))
                    {
                        Settings.Log("UpdateChecker.Check: 未找到 tag_name");
                        return result;
                    }

                    // tag 格式 "v1.0722.1"，去掉前缀 v
                    string latest = tag.TrimStart('v', 'V');
                    result.LatestVersion = latest;
                    if (!string.IsNullOrEmpty(htmlUrl)) result.ReleaseUrl = htmlUrl;

                    // 版本比较：逐段数字比对
                    result.HasUpdate = IsNewerVersion(latest, CurrentVersion);
                }
            }
            catch (Exception ex)
            {
                Settings.Log("UpdateChecker.Check 异常: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// 从 JSON 字符串中提取指定字段的字符串值。
        /// 匹配 "field":"value" 模式，处理转义引号。
        /// </summary>
        private static string ExtractJsonString(string json, string field)
        {
            // 匹配 "tag_name":"v1.0722.1" -- 字段名后可能有空格
            string pattern = "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"";
            var m = Regex.Match(json, pattern);
            if (m.Success && m.Groups.Count > 1)
                return m.Groups[1].Value;
            return null;
        }

        /// <summary>
        /// 判断 latest 是否比 current 新。逐段数字比对。
        /// 如 "1.0722.2" vs "1.0722.1" -> true；"1.0722.1" vs "1.0722.1" -> false。
        /// 非数字段视为 0。
        /// </summary>
        private static bool IsNewerVersion(string latest, string current)
        {
            if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(current)) return false;
            var la = latest.Split('.');
            var cu = current.Split('.');
            int len = Math.Max(la.Length, cu.Length);
            for (int i = 0; i < len; i++)
            {
                int l = i < la.Length ? ParseSegment(la[i]) : 0;
                int c = i < cu.Length ? ParseSegment(cu[i]) : 0;
                if (l > c) return true;
                if (l < c) return false;
            }
            return false;   // 完全相等
        }

        private static int ParseSegment(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : 0;
        }
    }
}
