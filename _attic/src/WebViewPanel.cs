using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace Traynexus
{
    /// <summary>
    /// 嵌入 WebView2 的无边框 WinForms 面板。
    /// 用于承载前端 HTML UI（src/ui/traynexus-ui.html）。
    /// 通过 SetVirtualHostNameToFolderMapping 把本地 ui 目录映射为
    /// https://traynexus.ui/ 虚拟主机，避免 file:// 协议限制。
    ///
    /// 两种模式：
    /// - Quick：速览面板（小窗 ~320px 宽），失焦自动隐藏
    /// - Console：主控制台（大窗 ~720px 宽），失焦不隐藏
    /// </summary>
    public class WebViewPanel : Form
    {
        private readonly BridgeApi _bridge;
        private readonly string _mode;  // "quick" 或 "console"
        private WebView2 _webView;
        private bool _isReady;
        private bool _navInjected;
        private const int CornerRadius = 12;

        /// <summary>
        /// WebView2 是否已完成初始化并可接收消息。
        /// </summary>
        public bool IsReady
        {
            get { return _isReady && _webView != null && _webView.CoreWebView2 != null; }
        }

        /// <summary>面板模式：quick 或 console</summary>
        public string Mode { get { return _mode; } }

        /// <summary>是否在失焦时自动隐藏（quick 和 menu 模式）</summary>
        public bool AutoHideOnDeactivate { get { return _mode == "quick" || _mode == "menu"; } }

        public WebViewPanel(BridgeApi bridge, string mode)
        {
            _bridge = bridge;
            _mode = mode ?? "console";

            // Form 设置：无边框、暗色背景、置顶
            FormBorderStyle = FormBorderStyle.None;
            BackColor = ColorTranslator.FromHtml("#1C1C1E");
            TopMost = (_mode == "quick");  // 速览置顶，控制台不强制置顶
            ShowInTaskbar = (_mode == "console");  // 控制台显示在任务栏
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = true;
            MinimizeBox = true;

            // 严格按 HTML 定义的尺寸：
            // - 速览面板 .quick 宽 310px，加窗口边距为 320
            // - 主控制台 .win 是 980x620（32 titlebar + 588 body，见 HTML CSS）
            // - 右键菜单 .ctxmenu 宽 220px，高约 210px
            if (_mode == "quick")
                Size = new Size(320, 480);
            else if (_mode == "menu")
                Size = new Size(240, 265);
            else
            {
                Size = new Size(980, 620);
                MaximumSize = Screen.PrimaryScreen.WorkingArea.Size;
            }

            ShowIcon = false;
            ControlBox = false;
            Text = "Traynexus";

            // 关闭时清空 ready 状态（避免向已销毁控件推消息）
            FormClosed += (s, e) => { _isReady = false; };

            // 失焦自动隐藏（仅 quick 模式）
            Deactivate += (s, e) =>
            {
                if (AutoHideOnDeactivate)
                {
                    // 延迟一点避免点击面板内部元素时误触发
                    var t = new Timer { Interval = 150 };
                    t.Tick += (ts, te) =>
                    {
                        t.Stop(); t.Dispose();
                        if (!IsDisposed && !ContainsFocus)
                            Hide();
                    };
                    t.Start();
                }
            };

            // 应用圆角
            ApplyRoundRegion();
            SizeChanged += (s, e) => ApplyRoundRegion();

            InitWebView();
        }

        // ============================================================
        // 无边框窗口拖拽缩放（仅 console 模式）
        // ============================================================
        private const int WM_NCHITTEST = 0x0084;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                          HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17, HTCAPTION = 2;
        private const int BORDER = 6;   // 边缘拖拽感应宽度
        private const int TITLEBAR = 32; // HTML 标题栏高度，用于拖拽移动整窗

        protected override void WndProc(ref Message m)
        {
            if (_mode == "console" && m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                int x = (short)(lp & 0xFFFF) - Left;
                int y = (short)((lp >> 16) & 0xFFFF) - Top;

                bool left = x < BORDER;
                bool right = x >= Width - BORDER;
                bool top = y < BORDER;
                bool bottom = y >= Height - BORDER;

                if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

                // 标题栏区域可拖动移动窗口（避开右上角三个按钮 138px 宽）
                if (y < TITLEBAR && x < Width - 138)
                { m.Result = (IntPtr)HTCAPTION; return; }

                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// 应用圆角区域。Size 变化时需重算。仅速览面板需要圆角。
        /// </summary>
        private void ApplyRoundRegion()
        {
            if (_mode != "quick") return;  // 控制台保持直角，避免拖拽缩放闪烁
            try
            {
                IntPtr hrgn = NativeMethods.CreateRoundRectRgn(
                    0, 0, Width + 1, Height + 1, CornerRadius, CornerRadius);
                Region = Region.FromHrgn(hrgn);
            }
            catch { /* 圆角失败不影响功能 */ }
        }

        /// <summary>
        /// 异步初始化 WebView2：创建控件、设置虚拟主机映射、注入 host object、导航。
        /// </summary>
        private async void InitWebView()
        {
            try
            {
                _webView = new WebView2();
                _webView.Dock = DockStyle.Fill;
                _webView.BackColor = ColorTranslator.FromHtml("#1C1C1E");
                Controls.Add(_webView);

                // 等待 CoreWebView2 初始化
                await _webView.EnsureCoreWebView2Async(null);

                if (_webView.CoreWebView2 == null)
                {
                    Settings.Log("WebViewPanel: CoreWebView2 为 null");
                    return;
                }

                string uiDir = ResolveUiDir();
                if (!Directory.Exists(uiDir))
                {
                    Settings.Log("WebViewPanel: UI 目录不存在: " + uiDir);
                }

                // 把本地 ui 目录映射到 https://traynexus.ui/
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "traynexus.ui",
                    uiDir,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                // 注入桥接对象，JS 端用 hostObjects.traynexus 访问
                _webView.CoreWebView2.AddHostObjectToScript("traynexus", _bridge);

                // 导航到主页面
                _webView.CoreWebView2.Navigate("https://traynexus.ui/traynexus-ui.html");

                // 页面加载完成后注入应用模式
                _webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess && !_navInjected)
                    {
                        _navInjected = true;
                        try
                        {
                            _webView.CoreWebView2.ExecuteScriptAsync(
                                "setAppMode('" + _mode + "');");
                        }
                        catch (Exception ex)
                        {
                            Settings.Log("ExecuteScriptAsync setAppMode: " + ex.Message);
                        }
                    }
                };

                _isReady = true;
            }
            catch (Exception ex)
            {
                Settings.Log("InitWebView 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 解析 UI 目录路径：
        /// 1) exe 同级的 ui/
        /// 2) exe 父目录的 src/ui/
        /// 3) 开发期：项目根的 src/ui/
        /// </summary>
        private static string ResolveUiDir()
        {
            string exeDir = Application.StartupPath;
            string exeParent = Path.GetDirectoryName(exeDir);
            if (string.IsNullOrEmpty(exeParent)) exeParent = exeDir;

            string[] candidates = new string[]
            {
                Path.Combine(exeDir, "ui"),
                Path.Combine(exeParent, "src", "ui"),
                Path.Combine(exeParent, "ui"),
                // 开发期 exe 可能在 bin/ 下，src 在 ../
                Path.GetFullPath(Path.Combine(exeDir, "..", "src", "ui")),
            };

            foreach (var c in candidates)
            {
                try
                {
                    if (Directory.Exists(c)) return c;
                }
                catch { }
            }
            // 默认返回相对路径，由 WebView2 决定是否报错
            return Path.Combine(exeDir, "ui");
        }

        /// <summary>
        /// 切换前端面板（如 "console" / "settings"）。
        /// </summary>
        public void NavigateToPanel(string panel)
        {
            if (!IsReady) return;
            try
            {
                string json = "{\"type\":\"navigate\",\"panel\":\"" + (panel ?? "") + "\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { Settings.Log("NavigateToPanel: " + ex.Message); }
        }

        /// <summary>
        /// 向前端推送任意 JSON 消息。
        /// </summary>
        public void PostMessage(string json)
        {
            if (!IsReady || string.IsNullOrEmpty(json)) return;
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { Settings.Log("WebViewPanel.PostMessage: " + ex.Message); }
        }

        /// <summary>
        /// 把面板定位到托盘图标附近（速览：图标左侧上方；控制台：屏幕右下角）。
        /// 强制保证面板完全在屏幕工作区内。
        /// </summary>
        public void ShowNearTray()
        {
            try
            {
                // 使用鼠标所在屏幕（一般就是托盘所在屏幕）
                var screen = Screen.FromPoint(Cursor.Position);
                var work = screen.WorkingArea;
                int x, y;

                if (_mode == "quick" || _mode == "menu")
                {
                    // 速览/菜单：以鼠标点击位置为参考，面板出现在图标"左上方"
                    var pt = Cursor.Position;
                    x = pt.X - Width + 20;   // 面板右边略过鼠标
                    y = pt.Y - Height - 8;    // 面板底部在鼠标上方
                }
                else
                {
                    // 控制台：屏幕右下角
                    x = work.Right - Width - 12;
                    y = work.Bottom - Height - 12;
                }

                // 保护：完全约束在屏幕工作区
                if (x + Width > work.Right) x = work.Right - Width - 8;
                if (y + Height > work.Bottom) y = work.Bottom - Height - 8;
                if (x < work.Left) x = work.Left + 8;
                if (y < work.Top) y = work.Top + 8;
                Location = new Point(x, y);
            }
            catch
            {
                // 失败时使用默认位置
            }

            if (!Visible)
            {
                Show();
            }
            else
            {
                BringToFront();
                Activate();
            }
        }
    }
}
