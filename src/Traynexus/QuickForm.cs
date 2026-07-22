using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 左键速览面板：Win32 Layered Window + per-pixel alpha。
    /// 圆角 & 阴影通过 GDI+ AntiAlias 完全平滑（等同开始菜单圆角观感）。
    /// 因为 UpdateLayeredWindow 不合成子控件，所有内容（进度条、按钮、文字）都在
    /// RenderSurface() 里直接绘制到一张 32-bpp ARGB 位图上。
    /// </summary>
    public class QuickForm : Form
    {
        private const int CornerRadius = 14;

        private readonly Settings _settings;
        private readonly TrayContext _context;

        // ------ 缓存的数据/状态 ------
        private int _memPercent;
        private string _memDetail = "";
        private int _battPercent;
        private string _battDetail = "";
        private bool _battPresent;
        private bool _isReleasing;

        // "一键释放" 按钮的命中矩形（客户区坐标），OnPaint 完成后填充
        private Rectangle _btnRect;
        private bool _btnHover;
        private bool _btnPressed;
        private string _btnText = "一键释放内存";

        // 失焦轮询
        private System.Windows.Forms.Timer _focusTimer;
        private DateTime _showTime;

        // ============================================================
        //  Win32 Interop
        // ============================================================
        private const int WS_EX_LAYERED   = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST   = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern bool DeleteObject(IntPtr hObj);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // ============================================================
        public QuickForm(Settings settings, TrayContext context)
        {
            _settings = settings;
            _context = context;

            this.Text = "Traynexus";
            this.Size = new Size(300, 250);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.MinimumSize = this.Size;
            this.MaximumSize = this.Size;

            // 失焦检测：轮询前台窗口，但显示后给 400ms 宽限期获得焦点
            _focusTimer = new System.Windows.Forms.Timer();
            _focusTimer.Interval = 100;
            _focusTimer.Tick += (s, e) =>
            {
                if (!this.Visible) return;
                if ((DateTime.Now - _showTime).TotalMilliseconds < 400) return;
                if (GetForegroundWindow() != this.Handle)
                    this.Hide();
            };

            this.VisibleChanged += (s, e) =>
            {
                if (this.Visible)
                {
                    _showTime = DateTime.Now;
                    _focusTimer.Start();
                    RedrawLayered();
                }
                else
                {
                    _focusTimer.Stop();
                    _btnHover = false;
                    _btnPressed = false;
                }
            };

            this.FormClosed += (s, e) => { _focusTimer.Stop(); _focusTimer.Dispose(); };

            // 鼠标处理（按钮命中）
            this.MouseMove += OnMouseMoveLayered;
            this.MouseDown += OnMouseDownLayered;
            this.MouseUp += OnMouseUpLayered;
            this.MouseLeave += OnMouseLeaveLayered;
        }

        // 加上 WS_EX_LAYERED
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        // ============================================================
        //  数据接口（TrayContext 会调用）
        // ============================================================
        public void RefreshData()
        {
            try
            {
                var mem = MemoryInfo.Take();
                _memPercent = Math.Min(100, mem.UsedPercent);
                _memDetail = MemorySnapshot.FormatBytes(mem.UsedBytes) + " / " + MemorySnapshot.FormatBytes(mem.TotalBytes);

                // 电池改读 TrayContext 后台线程采集的缓存快照，避免 UI 线程同步触发 BatteryInfo.Take()
                // （首次可能拉起 powercfg.exe，5s 超时会卡死速览面板）
                var batt = _context != null ? _context.CurrentBattery : null;
                _battPresent = batt != null && batt.IsPresent;
                if (_battPresent)
                {
                    _battPercent = Math.Min(100, batt.Percent);
                    _battDetail = batt.IsCharging ? "充电中" : "使用中";
                }
                else
                {
                    _battPercent = 0;
                    _battDetail = "无电池";
                }
            }
            catch (Exception ex) { Settings.Log("QuickForm.RefreshData 失败: " + ex.Message); }
            if (this.Visible) RedrawLayered();
        }

        public void ShowNearCursor()
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var work = screen.WorkingArea;
            int x = Cursor.Position.X - Width + 20;
            int y = Cursor.Position.Y - Height - 8;
            if (x + Width > work.Right) x = work.Right - Width - 8;
            if (y + Height > work.Bottom) y = work.Bottom - Height - 8;
            if (x < work.Left) x = work.Left + 8;
            if (y < work.Top) y = work.Top + 8;
            this.Location = new Point(x, y);
        }

        // ============================================================
        //  鼠标（按钮 hit-test）
        // ============================================================
        private void OnMouseMoveLayered(object sender, MouseEventArgs e)
        {
            bool over = _btnRect.Contains(e.Location);
            if (over != _btnHover)
            {
                _btnHover = over;
                this.Cursor = over ? Cursors.Hand : Cursors.Default;
                RedrawLayered();
            }
        }

        private void OnMouseDownLayered(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _btnRect.Contains(e.Location))
            {
                _btnPressed = true;
                RedrawLayered();
            }
        }

        private void OnMouseUpLayered(object sender, MouseEventArgs e)
        {
            bool wasPressed = _btnPressed;
            _btnPressed = false;
            if (e.Button == MouseButtons.Left && wasPressed && _btnRect.Contains(e.Location))
            {
                QuickRelease();
            }
            RedrawLayered();
        }

        private void OnMouseLeaveLayered(object sender, EventArgs e)
        {
            if (_btnHover || _btnPressed)
            {
                _btnHover = false;
                _btnPressed = false;
                this.Cursor = Cursors.Default;
                RedrawLayered();
            }
        }

        // ============================================================
        //  释放按钮动作
        // ============================================================
        private void QuickRelease()
        {
            if (_isReleasing) return;
            _isReleasing = true;
            _btnText = "释放中...";
            RedrawLayered();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string result = null;
                try
                {
                    var r = MemoryCleaner.Execute(_settings);
                    long delta = (long)r.BeforeUsedBytes - (long)r.AfterUsedBytes;
                    result = "已释放 " + (delta > 0 ? MemorySnapshot.FormatBytes((ulong)delta) : "0 B") +
                             " · " + r.TrimmedCount + " 个进程";
                }
                catch (Exception ex) { result = "释放失败: " + ex.Message; }

                try
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        _isReleasing = false;
                        _btnText = "一键释放内存";
                        RefreshData();
                        _context.ShowBalloon("释放完成", result);
                        var t = new System.Windows.Forms.Timer { Interval = 800 };
                        t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!IsDisposed) Hide(); };
                        t.Start();
                    }));
                }
                catch { }
            });
        }

        // ============================================================
        //  渲染 + Layered Window 上屏
        // ============================================================
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RedrawLayered();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) RedrawLayered();
        }

        private void RedrawLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            using (var bmp = RenderSurface(Width, Height))
            {
                PushBitmapToLayered(bmp);
            }
        }

        /// <summary>
        /// 把 32-bpp ARGB 位图作为整个窗口内容送到 GPU 合成。
        /// </summary>
        private void PushBitmapToLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                var size = new SIZE(bmp.Width, bmp.Height);
                var pointSrc = new POINT(0, 0);
                var topPos = new POINT(this.Left, this.Top);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                UpdateLayeredWindow(this.Handle, screenDc, ref topPos, ref size, memDc, ref pointSrc, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ============================================================
        //  Compose the whole panel onto an ARGB bitmap
        // ============================================================
        private Bitmap RenderSurface(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Layered surface 与 desktop 直接合成，用 ClearType 会有彩色边——用 AntiAliasGridFit 更安全
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;
                g.Clear(Color.Transparent);

                var cardRect = new Rectangle(0, 0, w, h);

                // —— 圆角卡片背景（纯白 + 边缘 AA）——
                using (var path = RoundedRect(cardRect, CornerRadius))
                {
                    using (var br = new SolidBrush(Color.White))
                        g.FillPath(br, path);
                    // 细描一层浅灰边，让边缘更"实"
                    using (var pen = new Pen(Color.FromArgb(220, 220, 224), 1f))
                        g.DrawPath(pen, path);
                }

                DrawContent(g, w, h);
            }
            return bmp;
        }

        private void DrawContent(Graphics g, int w, int h)
        {
            int pad = 18;
            int barW = w - pad * 2;
            int y = pad;

            // 复用全局共享 Font（Fonts 静态类），避免每次 Paint 都 new 4 个 Font
            var fontTitle = Fonts.S10B;
            var fontPct = Fonts.S11B;
            var fontDetail = Fonts.S8;
            var fontBtn = Fonts.S10B;

            Color memColor = Color.FromArgb(52, 199, 89);
            Color battColor = Color.FromArgb(10, 122, 255);
            Color inkColor = Color.FromArgb(24, 24, 27);
            Color grayColor = Color.FromArgb(128, 128, 128);

            try
            {
                // === 内存区 ===
                using (var br = new SolidBrush(inkColor))
                    g.DrawString("内存", fontTitle, br, pad, y);

                string memPctText = _memPercent + "%";
                var memPctSize = g.MeasureString(memPctText, fontPct);
                using (var br = new SolidBrush(memColor))
                    g.DrawString(memPctText, fontPct, br, pad + barW - memPctSize.Width, y);

                y += 28;
                using (var br = new SolidBrush(grayColor))
                    g.DrawString(_memDetail, fontDetail, br, pad, y);

                y += 30;
                DrawRoundedBar(g, pad, y, barW, 8, _memPercent, memColor);

                y += 28;

                // === 电池区 ===
                using (var br = new SolidBrush(inkColor))
                    g.DrawString("电池", fontTitle, br, pad, y);

                string battPctText = _battPresent ? (_battPercent + "%") : "--";
                var battPctSize = g.MeasureString(battPctText, fontPct);
                using (var br = new SolidBrush(battColor))
                    g.DrawString(battPctText, fontPct, br, pad + barW - battPctSize.Width, y);

                y += 28;
                using (var br = new SolidBrush(grayColor))
                    g.DrawString(_battDetail, fontDetail, br, pad, y);

                y += 30;
                DrawRoundedBar(g, pad, y, barW, 8, _battPercent, battColor);

                y += 28;

                // === 一键释放（圆角按钮） ===
                _btnRect = new Rectangle(pad, y, barW, 38);
                DrawReleaseButton(g, _btnRect, fontBtn);
            }
            finally
            {
                // 共享 Font 不 Dispose，由进程退出回收
            }
        }

        private static void DrawRoundedBar(Graphics g, int x, int y, int w, int h, int value, Color barColor)
        {
            var trackRect = new Rectangle(x, y, w, h);
            using (var path = RoundedRect(trackRect, h / 2))
            using (var br = new SolidBrush(Color.FromArgb(234, 234, 238)))
                g.FillPath(br, path);

            if (value > 0)
            {
                int fillW = (int)(w * (value / 100.0));
                if (fillW < h) fillW = h;
                var fillRect = new Rectangle(x, y, fillW, h);
                using (var path = RoundedRect(fillRect, h / 2))
                using (var br = new SolidBrush(barColor))
                    g.FillPath(br, path);
            }
        }

        private void DrawReleaseButton(Graphics g, Rectangle rect, Font font)
        {
            Color baseColor = Color.FromArgb(10, 122, 255);
            Color bg;
            if (_isReleasing)
                bg = Color.FromArgb(150, baseColor.R, baseColor.G, baseColor.B);
            else if (_btnPressed)
                bg = Color.FromArgb(6, 90, 200);
            else if (_btnHover)
                bg = Color.FromArgb(38, 140, 255);
            else
                bg = baseColor;

            using (var path = RoundedRect(rect, 10))
            using (var br = new SolidBrush(bg))
                g.FillPath(br, path);

            using (var br = new SolidBrush(Color.White))
            using (var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
                g.DrawString(_btnText, font, br, rect, sf);
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
