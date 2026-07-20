using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 主控制台：按 traynexus-ui.html 的视觉规范还原。
    /// 顶部标题栏 + 左侧图标导航 + 右侧内容区（概览 / 内存释放 / 充电管理 / 亮度 / 设置）。
    /// 除"内存释放"页复用 ReleasePanel 外，其余页面均按 mockup 布局重建。
    /// </summary>
    public class MainForm : Form
    {
        // ============ 颜色（对应 HTML :root） ============
        private static readonly Color CBg      = Color.FromArgb(243, 243, 246); // --bg
        private static readonly Color CPanel   = Color.White;                    // --panel
        private static readonly Color CPanel2  = Color.FromArgb(250, 250, 252);  // --panel2
        private static readonly Color CLine    = Color.FromArgb(227, 227, 232);  // --line
        private static readonly Color CInk     = Color.FromArgb(29, 29, 31);     // --ink
        private static readonly Color CInk2    = Color.FromArgb(110, 110, 115);  // --ink2
        private static readonly Color CInk3    = Color.FromArgb(154, 154, 160);  // --ink3
        private static readonly Color CBlue    = Color.FromArgb(10, 122, 255);   // --blue
        private static readonly Color CGreen   = Color.FromArgb(52, 199, 89);    // --green
        private static readonly Color COrange  = Color.FromArgb(255, 159, 10);   // --orange
        private static readonly Color CPurple  = Color.FromArgb(175, 82, 222);   // --purple

        private static readonly Color CIcoMem   = Color.FromArgb(234, 250, 240);
        private static readonly Color CIcoBatt  = Color.FromArgb(234, 243, 255);
        private static readonly Color CIcoBri   = Color.FromArgb(255, 244, 224);
        private static readonly Color CIcoHealth= Color.FromArgb(243, 238, 255);

        private const string Fnt = "Microsoft YaHei UI";

        private readonly Settings _settings;
        private readonly TrayContext _context;

        // 导航
        private Panel _sidebar;
        private Panel _content;
        private NavButton[] _navButtons;
        private int _currentPage = -1;

        // 页面
        private Panel _pageOverview, _pageRelease, _pageCharge, _pageBright, _pageSettings, _pageDiagnostic;
        private ReleasePanel _releasePanel;

        // 诊断页控件引用（供「重新检测」刷新）
        private FlowLayoutPanel _diagFlow;
        private ChargeCapability _diagCap;

        // 概览页数据控件
        private Label _ovMemBig, _ovMemSub, _ovBattBig, _ovBattSub;
        private BarStrip _ovMemBar, _ovBattBar;
        private Label _ovBriBig, _ovBriSub, _ovHealBig, _ovHealSub;
        private BarStrip _ovBriBar, _ovHealBar;

        // 充电页
        private ModeCard[] _modeCards;
        private RoundSlider _bcTrack;
        private Label _bcVal, _bcHint;
        private Label _chargeStatusVal, _adapterStatusVal;
        private ChargeCapability _chargeCap;   // 充电能力检测结果（GetChargePage 首次构建时填充）
        private bool _chargeOpOk = true;       // 上一次 SetChargeLimit 是否成功（供 UpdateChargeHint 展示）
        // 充电页健康卡控件（供 RefreshChargeStatus 刷新）
        private RingChart _healthRing;
        private Label _healthLabel, _healthStat, _healthStat2;

        // 亮度页
        private ToggleSwitch _autoBrightSw;

        // 设置页子页签（供 NavigateToAbout 使用）
        private SubTabButton[] _setTabBtns;
        private Panel[] _setTabPanels;
        private Control _setTitle, _setSubtitle, _setSubTabRow;
        private FlowLayoutPanel _setFlow;
        private Panel _setSubContainer;

        // 定时刷新
        private readonly System.Windows.Forms.Timer _refreshTimer;

        public MainForm(Settings settings, TrayContext context)
        {
            _settings = settings;
            _context = context;

            this.Text = "TrayNexus";
            this.Size = new Size(1000, 640);
            this.MinimumSize = new Size(880, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ShowInTaskbar = true;
            this.MaximizeBox = false;   // 隐藏最大化按钮
            this.FormBorderStyle = FormBorderStyle.FixedSingle;   // 固定尺寸，防拖拽
            this.Icon = LogoLoader.GetWindowIcon() ?? IconRenderer.Build(50, 0, false);
            this.BackColor = CPanel;
            this.Font = new Font(Fnt, 9f);

            BuildBody();

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 2000;
            _refreshTimer.Tick += (s, e) => { RefreshTitle(); RefreshOverview(); RefreshChargeStatus(); };
            _refreshTimer.Start();

            this.FormClosed += (s, e) => _refreshTimer.Stop();

            // 首次显示后再刷一遍当前页面 —— 避免首次显示时子控件双缓冲区未初始化留下脏像素
            this.Shown += (s, e) =>
            {
                if (_content != null && _content.Controls.Count > 0)
                {
                    var p = _content.Controls[0];
                    p.Invalidate(true);
                    p.Update();
                }
            };

            NavigateTo(0);
            RefreshTitle();
        }

        /// <summary>
        /// 彻底移除窗口的最大化按钮。仅 MaximizeBox=false 在部分 Win 版本仍会渲染灰色最大化按钮，
        /// 因此在 handle 创建后再用 SetWindowLong 强删 WS_MAXIMIZEBOX 样式位，然后 SetWindowPos
        /// 触发非客户区重绘。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int style = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_STYLE);
                style &= ~NativeMethods.WS_MAXIMIZEBOX;
                NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_STYLE, style);
                NativeMethods.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
            }
            catch { }
        }

        // ============================================================
        // 窗口标题：TrayNexus  |  内存 x/x GB (x%)  ·  电量 x%  ·  亮度 x%
        // （由 Windows 原生标题栏渲染，无需自绘）
        // ============================================================
        private void RefreshTitle()
        {
            try
            {
                var mem = MemoryInfo.Take();
                var batt = _context != null ? _context.CurrentBattery : BatteryInfo.Take();
                string battText = batt.IsPresent ? (batt.Percent + "%") : "--";
                // 亮度单独 try-catch，避免 WMI 异常影响整个标题
                string brightText = "--";
                try
                {
                    int bright = BrightnessController.GetBrightness();
                    if (bright >= 0) brightText = bright + "%";
                }
                catch { }
                this.Text = string.Format(
                    "TrayNexus  |  内存 {0} / {1} ({2}%)  ·  电量 {3}  ·  亮度 {4}",
                    MemorySnapshot.FormatBytes(mem.UsedBytes),
                    MemorySnapshot.FormatBytes(mem.TotalBytes),
                    mem.UsedPercent,
                    battText,
                    brightText);
            }
            catch { }
        }

        // ============================================================
        // 主体：左侧导航 + 右侧内容
        //   注意 Dock 顺序：Controls.Add 越晚的控件越先 Dock（更外层）。
        //   期望是「先 sidebar 占左，content 填剩余」，所以先 Add sidebar 再 Add content。
        // ============================================================
        private void BuildBody()
        {
            _sidebar = new Panel();
            _sidebar.Dock = DockStyle.Left;
            _sidebar.Width = 220;
            _sidebar.BackColor = CPanel2;
            _sidebar.Padding = new Padding(14, 16, 14, 14);
            _sidebar.Paint += (s, e) =>
            {
                using (var pen = new Pen(CLine))
                    e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
            };

            string[] labels = { "概览", "内存释放", "充电管理", "亮度", "诊断", "设置" };
            NavIcon[] icons = { NavIcon.Grid, NavIcon.Memory, NavIcon.Battery, NavIcon.Sun, NavIcon.Activity, NavIcon.Cog };
            _navButtons = new NavButton[labels.Length];
            // 从下往上添加，让第一个在最上面（Dock.Top 反向堆叠）
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var btn = new NavButton(icons[i], labels[i]);
                btn.Dock = DockStyle.Top;
                btn.Height = 42;
                btn.Click += (s, e) =>
                {
                    // 已经在这一页时仍点击 → 若是"设置"页且顶部被隐藏（用户之前从"关于 TrayNexus"入口进来），
                    // 主动恢复顶部并回到"通用"子页签。其他页无副作用。
                    if (_currentPage == idx && idx == 5)
                    {
                        SetSettingsHeaderVisible(true);
                        if (_setTabBtns != null && _setTabBtns.Length > 0)
                        {
                            for (int k = 0; k < _setTabBtns.Length; k++)
                            {
                                _setTabBtns[k].IsActive = (k == 0);
                                if (_setTabPanels[k] != null) _setTabPanels[k].Visible = (k == 0);
                            }
                        }
                        return;
                    }
                    NavigateTo(idx);
                };
                _sidebar.Controls.Add(btn);
                _navButtons[i] = btn;
            }

            // 底部：关于 TrayNexus 按钮 + 版本号（居中）
            var footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 74;
            footer.BackColor = CPanel2;
            footer.Padding = new Padding(0);

            var lblVer = new Label();
            lblVer.Text = "Version: v1.0717.3";
            lblVer.Dock = DockStyle.Top;
            lblVer.Height = 22;
            lblVer.AutoSize = false;
            lblVer.AutoEllipsis = true;
            lblVer.TextAlign = ContentAlignment.MiddleCenter;
            lblVer.ForeColor = CInk3;
            lblVer.Font = new Font(Fnt, 8f);
            footer.Controls.Add(lblVer);

            var btnAboutRow = new Panel();
            btnAboutRow.Dock = DockStyle.Fill;
            btnAboutRow.BackColor = CPanel2;

            var btnAbout = new FlatBorderedButton();
            btnAbout.Text = "关于 TrayNexus";
            btnAbout.Font = new Font(Fnt, 9.5f, FontStyle.Bold);
            btnAbout.Size = new Size(160, 34);
            btnAbout.Cursor = Cursors.Hand;
            btnAbout.Click += (s, e) => NavigateToAbout();
            btnAboutRow.Resize += (s, e) =>
            {
                // 水平居中：按钮宽度使用父容器宽度 - 24 的左右各 12 边距
                int w = Math.Max(120, btnAboutRow.Width - 24);
                btnAbout.Size = new Size(w, 34);
                btnAbout.Location = new Point((btnAboutRow.Width - w) / 2, (btnAboutRow.Height - 34) / 2);
            };
            btnAboutRow.Controls.Add(btnAbout);
            footer.Controls.Add(btnAboutRow);

            _sidebar.Controls.Add(footer);

            _content = new Panel();
            _content.Dock = DockStyle.Fill;
            _content.BackColor = CPanel;
            _content.Padding = new Padding(0);

            // Dock 顺序：Controls 集合中越晚 Add 的越先 Dock（抢占空间）。
            // 我们要 sidebar 抢占左侧全高，content 填剩余 —— 所以 sidebar 后 Add。
            this.Controls.Add(_content);
            this.Controls.Add(_sidebar);
        }

        private void NavigateTo(int index)
        {
            if (_currentPage == index) return;
            _currentPage = index;
            for (int i = 0; i < _navButtons.Length; i++)
                _navButtons[i].IsActive = (i == index);

            _content.Controls.Clear();
            _content.Padding = new Padding(0);
            Panel page = null;
            switch (index)
            {
                case 0: page = GetOverviewPage(); break;
                case 1: page = GetReleasePage(); break;
                case 2: page = GetChargePage(); break;
                case 3: page = GetBrightPage(); break;
                case 4: page = GetDiagnosticPage(); break;
                case 5:
                    page = GetSettingsPage();
                    // 侧边栏 -> 设置：默认显示顶部大标题/副标题/子页签，并激活"通用"
                    SetSettingsHeaderVisible(true);
                    if (_setTabBtns != null && _setTabBtns.Length > 0)
                    {
                        for (int k = 0; k < _setTabBtns.Length; k++)
                        {
                            _setTabBtns[k].IsActive = (k == 0);
                            if (_setTabPanels[k] != null) _setTabPanels[k].Visible = (k == 0);
                        }
                    }
                    break;
            }
            if (page != null)
            {
                _content.Controls.Add(page);
                // 强制刷新整棵子树 —— 避免页面缓存复用时的旧位图/双缓冲脏数据造成瞬时黑线
                page.Invalidate(true);
                page.Update();
                // 布局完成后再刷一次（IsHandleCreated 判空避免构造期崩溃）
                var refreshPage = page;
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (!refreshPage.IsDisposed) { refreshPage.Invalidate(true); refreshPage.Update(); }
                    }));
                }
            }

            RefreshOverview();
        }

        // ============================================================
        // ① 概览页  (全部 Dock 布局，宽度自适应)
        // ============================================================
        private Panel GetOverviewPage()
        {
            if (_pageOverview != null) return _pageOverview;

            _pageOverview = NewPage();
            _pageOverview.Padding = new Padding(28, 30, 28, 40);   // 顶部 30px（上移 10px）

            // 底部：快捷操作按钮行 (先 Add → 后 Dock → 靠下)
            var acts = new TableLayoutPanel();
            acts.Dock = DockStyle.Bottom;
            acts.Height = 44;
            acts.ColumnCount = 2;
            acts.RowCount = 1;
            acts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            acts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            acts.Padding = new Padding(0, 6, 0, 0);
            acts.BackColor = CPanel;

            var btnRel = new FlatBorderedButton();
            btnRel.Text = "内存释放";
            btnRel.Font = new Font(Fnt, 9.5f);
            btnRel.Dock = DockStyle.Fill;
            btnRel.Margin = new Padding(0, 0, 8, 0);   // 与卡片列间距 16px (8+8) 对齐
            btnRel.Click += (s, e) => NavigateTo(1);

            var btnChg = new FlatBorderedButton();
            btnChg.Text = "充电管理";
            btnChg.Font = new Font(Fnt, 9.5f);
            btnChg.Dock = DockStyle.Fill;
            btnChg.Margin = new Padding(8, 0, 0, 0);   // 与卡片列间距 16px (8+8) 对齐
            btnChg.Click += (s, e) => NavigateTo(2);

            acts.Controls.Add(btnRel, 0, 0);
            acts.Controls.Add(btnChg, 1, 0);

            // 标题区 —— 主标题字号 15pt Bold 中文实际渲染高度 ~36-38px，Height 需 ≥ 40 才不被裁
            var header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 76;
            header.BackColor = CPanel;

            var lblH = new Label();
            lblH.Text = "概览";
            lblH.Font = new Font(Fnt, 15f, FontStyle.Bold);
            lblH.ForeColor = CInk;
            lblH.AutoSize = false;
            lblH.Size = new Size(300, 40);
            lblH.TextAlign = ContentAlignment.MiddleLeft;
            lblH.Location = new Point(0, 2);
            header.Controls.Add(lblH);

            var lblD = new Label();
            lblD.Text = "系统资源一体化状态总览，点击左侧导航进入对应模块。";
            lblD.Font = new Font(Fnt, 9f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(700, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(0, 48);
            header.Controls.Add(lblD);

            // 2x2 卡片网格：列 50/50 与底部两个按钮 50/50 对齐；行高固定，避免撑满导致下方大片空白
            var grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            grid.RowCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));   // 卡实高 = 186-16(WrapCard 上下 8+8) = 170
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));   // 卡内 bar 底 = 152，卡内下方留白 170-152 = 18px（与 ico 顶部 Y=18 对称）
            grid.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
            grid.BackColor = CPanel;
            grid.Padding = new Padding(0, 0, 0, 0);
            grid.Height = 186 * 2;   // 372

            // 新版线条图标：icoBg 全部传 Transparent（IconBox 在 A=0 时跳过圆角底）
            var battAmber = Color.FromArgb(255, 176, 32);
            var cardMem   = MakeOverviewCard("内存",     Color.Transparent, CGreen,     NavIcon.Memory,   out _ovMemBig,  out _ovMemSub,  out _ovMemBar);
            var cardBatt  = MakeOverviewCard("电池",     Color.Transparent, battAmber,  NavIcon.Battery,  out _ovBattBig, out _ovBattSub, out _ovBattBar);
            var cardBri   = MakeOverviewCard("亮度",     Color.Transparent, CBlue,      NavIcon.Sun,      out _ovBriBig,  out _ovBriSub,  out _ovBriBar);
            var cardHeal  = MakeOverviewCard("电池健康", Color.Transparent, CPurple,    NavIcon.Activity, out _ovHealBig, out _ovHealSub, out _ovHealBar);
            _ovBriBar.BarColor = CBlue;
            _ovHealBar.BarColor = CPurple;
            // 亮度/健康数据由 RefreshOverview 动态填充，不再硬编码 mock

            // 亮度卡片右侧标注"主屏幕"：与 b2 同 Y 行，右对齐
            // 用 Resize 事件手动重定位，避免 Anchor.Right 依赖 cardBri 初始 Width 导致的漂移/裁切
            var lblScreen = new Label();
            lblScreen.Text = "主屏幕";
            lblScreen.Font = new Font(Fnt, 10f, FontStyle.Bold);          // 加粗字号 +1（9→10pt Bold）
            lblScreen.ForeColor = Color.FromArgb(255, 176, 32);           // #FFB020 琥珀
            lblScreen.AutoSize = false;
            lblScreen.TextAlign = ContentAlignment.MiddleRight;
            lblScreen.Size = new Size(120, 26);                           // 加高到 26，Bold 10pt 中文下缘不被裁
            lblScreen.BackColor = Color.Transparent;
            cardBri.Controls.Add(lblScreen);
            Action reflowScreen = () =>
            {
                if (cardBri.Width > 0)
                    lblScreen.Location = new Point(cardBri.Width - lblScreen.Width - 20, 104);
            };
            cardBri.SizeChanged += (s, e) => reflowScreen();
            cardBri.HandleCreated += (s, e) => reflowScreen();
            reflowScreen();

            grid.Controls.Add(WrapCard(cardMem,  new Padding(0, 0, 8, 8)), 0, 0);
            grid.Controls.Add(WrapCard(cardBatt, new Padding(8, 0, 0, 8)), 1, 0);
            grid.Controls.Add(WrapCard(cardBri,  new Padding(0, 8, 8, 0)), 0, 1);
            grid.Controls.Add(WrapCard(cardHeal, new Padding(8, 8, 0, 0)), 1, 1);

            // 中间容器：Dock.Fill，让 grid 在其剩余竖直空间里居中
            // 由于 header 已 Dock.Top、acts 已 Dock.Bottom，middleWrap 就是中间那段可用空间；
            // grid 高度 = 432，middleWrap 剩多少高度就把 (h-432)/2 作为上下等距留白 → 整个界面上下居中
            var middleWrap = new Panel();
            middleWrap.Dock = DockStyle.Fill;
            middleWrap.BackColor = CPanel;
            middleWrap.Resize += (s, e) =>
            {
                grid.Left = 0;
                grid.Width = middleWrap.Width;
                grid.Top = Math.Max(0, (middleWrap.Height - grid.Height) / 2);
            };
            middleWrap.Controls.Add(grid);

            // 按 Dock 反向顺序 Add：先 Fill 的 middleWrap（会被压到最里）
            _pageOverview.Controls.Add(middleWrap);
            _pageOverview.Controls.Add(acts);
            _pageOverview.Controls.Add(header);

            return _pageOverview;
        }

        private Panel WrapCard(Control card, Padding margin)
        {
            var wrap = new Panel();
            wrap.Dock = DockStyle.Fill;
            wrap.Padding = margin;
            wrap.Margin = new Padding(0);   // 关键：TableLayoutPanel 里 Panel 默认 Margin=3，
                                            // 会导致卡片比按钮窄 6px。清零后卡片外边界与按钮完全对齐
            wrap.BackColor = CPanel;
            card.Dock = DockStyle.Fill;
            wrap.Controls.Add(card);
            return wrap;
        }

        private RoundedCard MakeOverviewCard(string title, Color icoBg, Color accent, NavIcon icon,
            out Label big, out Label sub, out BarStrip bar)
        {
            var card = new RoundedCard();

            // 直接把 icon + 标题作为 card 的绝对定位子控件——不再套 head 子面板，
            // 否则 head 的方角会覆盖 card 的圆角外区域（用白色盖住父背景的浅灰），
            // 视觉上就"没有圆角"。
            var ico = new IconBox(icon, accent, icoBg);
            ico.Size = new Size(30, 30);
            ico.Location = new Point(20, 18);
            card.Controls.Add(ico);

            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblT.ForeColor = CInk;
            lblT.AutoSize = true;
            lblT.Location = new Point(58, 24);
            card.Controls.Add(lblT);

            big = new Label();
            big.Text = "--";
            big.Font = new Font(Fnt, 22f, FontStyle.Bold);
            big.ForeColor = CInk;
            big.AutoSize = true;
            big.Location = new Point(20, 62);
            card.Controls.Add(big);

            sub = new Label();
            sub.Text = "";
            sub.Font = new Font(Fnt, 9f);
            sub.ForeColor = CInk2;
            sub.AutoSize = true;
            sub.Location = new Point(20, 116);
            card.Controls.Add(sub);

            bar = new BarStrip();
            bar.Location = new Point(20, 146);
            bar.Size = new Size(300, 6);
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bar.BarColor = accent;
            card.Controls.Add(bar);

            var barRef = bar;
            card.Resize += (s, e) => { barRef.Width = card.Width - 40; };

            return card;
        }

        private void RefreshOverview()
        {
            if (_ovMemBig == null || _currentPage != 0) return;
            try
            {
                var mem = MemoryInfo.Take();
                _ovMemBig.Text = mem.UsedPercent + "%";
                _ovMemSub.Text = "已用 " + MemorySnapshot.FormatBytes(mem.UsedBytes) + " / " + MemorySnapshot.FormatBytes(mem.TotalBytes);
                _ovMemBar.Value = Math.Min(100, mem.UsedPercent);

                // 复用 TrayContext 的电池快照，避免并发 WMI 轮询
                var batt = _context != null ? _context.CurrentBattery : BatteryInfo.Take();
                if (batt.IsPresent)
                {
                    _ovBattBig.Text = batt.Percent + "%";
                    string mode = new string[] { "满充", "均衡", "保养" }[Math.Max(0, Math.Min(2, _settings.ChargeMode))];
                    _ovBattSub.Text = mode + "模式 · 充电上限 " + _settings.ChargeLimit + "%";
                    _ovBattBar.Value = Math.Min(100, batt.Percent);
                }
                else
                {
                    _ovBattBig.Text = "--";
                    _ovBattSub.Text = "未检测到电池";
                    _ovBattBar.Value = 0;
                }

                // 亮度卡
                int bright = BrightnessController.GetBrightness();
                if (bright >= 0)
                {
                    _ovBriBig.Text = bright + "%";
                    _ovBriSub.Text = _settings.AutoBrightness ? "自动亮度：开" : "自动亮度：关";
                    _ovBriBar.Value = Math.Min(100, bright);
                }
                else
                {
                    _ovBriBig.Text = "--";
                    _ovBriSub.Text = "不支持亮度调节";
                    _ovBriBar.Value = 0;
                }

                // 电池健康卡
                if (batt.IsPresent && batt.HealthPercent > 0)
                {
                    _ovHealBig.Text = batt.HealthPercent + "%";
                    _ovHealSub.Text = "设计 " + batt.DesignCapacityWh + " Wh · 当前 " + batt.FullChargeCapacityWh + " Wh";
                    _ovHealBar.Value = Math.Min(100, batt.HealthPercent);
                }
                else if (batt.IsPresent)
                {
                    _ovHealBig.Text = "--";
                    _ovHealSub.Text = "深度数据不可用";
                    _ovHealBar.Value = 0;
                }
                else
                {
                    _ovHealBig.Text = "--";
                    _ovHealSub.Text = "未检测到电池";
                    _ovHealBar.Value = 0;
                }
            }
            catch { }
        }

        // ============================================================
        // ② 内存释放页（保持原状 — 复用 ReleasePanel）
        // ============================================================
        private Panel GetReleasePage()
        {
            if (_pageRelease != null) return _pageRelease;
            _pageRelease = NewPage();
            _releasePanel = new ReleasePanel(_settings);
            _releasePanel.TopLevel = false;
            _releasePanel.FormBorderStyle = FormBorderStyle.None;
            _releasePanel.Dock = DockStyle.Fill;
            _releasePanel.ShowInTaskbar = false;
            _releasePanel.ControlBox = false;
            _pageRelease.Controls.Add(_releasePanel);
            _releasePanel.Show();
            return _pageRelease;
        }

        // ============================================================
        // ③ 充电管理页  (FlowLayoutPanel 垂直堆叠，宽度自适应)
        // ============================================================
        private Panel GetChargePage()
        {
            if (_pageCharge != null) return _pageCharge;

            _pageCharge = NewPage();
            _chargeCap = OemChargeController.GetCapability();

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            // 上下 padding 平分（40/40）—— 内容总高 ~508px，可视区 ~610px，
            // 上下各留 40px 让整个页面视觉居中；顺带把"充电模式"标题上方拉开呼吸空间。
            flow.Padding = new Padding(28, 30, 28, 40);
            flow.BackColor = CPanel;
            _pageCharge.Controls.Add(flow);

            // === 标题 ===
            var lblT = new Label();
            lblT.Text = "充电模式";
            lblT.Font = new Font(Fnt, 14f, FontStyle.Bold);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Height = 34;
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Margin = new Padding(0, 0, 0, 6);
            flow.Controls.Add(lblT);

            // === 3 个模式卡片行 ===
            var modes = new Panel();
            modes.Height = 108;
            modes.Margin = new Padding(0, 0, 0, 16);
            modes.BackColor = CPanel;

            string[] mn = { "满电", "均衡", "保养" };
            string[] mp = { "100%", "80%", "60%" };
            string[] md = { "随时满电，适合外出", "日常推荐，兼顾续航", "长期插电最护电池" };
            Color[] mc = { CGreen, CBlue, COrange };
            NavIcon[] mi = { NavIcon.Battery, NavIcon.Battery, NavIcon.Battery };
            Color[] mib = { CIcoMem, CIcoBatt, CIcoBri };
            _modeCards = new ModeCard[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var mc0 = new ModeCard();
                mc0.Setup(mn[i], mp[i], md[i], mi[i], mc[i], mib[i]);
                mc0.Click += (s, e) => SelectChargeMode(idx);
                _modeCards[i] = mc0;
                modes.Controls.Add(mc0);
            }
            modes.Resize += (s, e) => LayoutModes(modes);
            flow.Controls.Add(modes);

            // === "自定义充电上限" 标签 + 右侧状态提示 (箭头指向的位置) ===
            var capRow = new Panel();
            capRow.Height = 24;
            capRow.Margin = new Padding(0, 0, 0, 6);
            capRow.BackColor = CPanel;

            var lblCap = new Label();
            lblCap.Text = "自定义充电上限";
            lblCap.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblCap.ForeColor = CInk;
            lblCap.AutoSize = false;
            lblCap.Size = new Size(200, 24);
            lblCap.TextAlign = ContentAlignment.MiddleLeft;
            lblCap.Location = new Point(0, 0);
            capRow.Controls.Add(lblCap);

            _bcHint = new Label();
            _bcHint.Font = new Font(Fnt, 9f);
            _bcHint.ForeColor = CInk2;
            _bcHint.AutoSize = false;
            _bcHint.Size = new Size(500, 24);
            _bcHint.TextAlign = ContentAlignment.MiddleRight;
            _bcHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            capRow.Controls.Add(_bcHint);
            capRow.Resize += (s, e) =>
            {
                _bcHint.Location = new Point(capRow.Width - 500, 0);
            };
            flow.Controls.Add(capRow);

            // === 滑块行 (RoundedCard) ===
            var sliderRow = new RoundedCard();
            sliderRow.Height = 46;
            sliderRow.Margin = new Padding(0, 0, 0, 10);

            var lblLimit = new Label();
            lblLimit.Text = "上限";
            lblLimit.Font = new Font(Fnt, 9f);
            lblLimit.ForeColor = CInk2;
            lblLimit.Location = new Point(16, 15);
            lblLimit.AutoSize = true;
            sliderRow.Controls.Add(lblLimit);

            _bcTrack = new RoundSlider();
            _bcTrack.Minimum = 50; _bcTrack.Maximum = 100;
            _bcTrack.Value = Math.Max(50, Math.Min(100, _settings.ChargeLimit));
            _bcTrack.AccentColor = CBlue;
            _bcTrack.Location = new Point(60, 15);
            _bcTrack.Size = new Size(200, 16);
            _bcTrack.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            sliderRow.Controls.Add(_bcTrack);

            _bcVal = new Label();
            _bcVal.Text = _bcTrack.Value + "%";
            _bcVal.Font = new Font(Fnt, 10f, FontStyle.Bold);
            _bcVal.ForeColor = CBlue;
            _bcVal.TextAlign = ContentAlignment.MiddleRight;
            _bcVal.AutoSize = false;
            _bcVal.Size = new Size(60, 32);
            _bcVal.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sliderRow.Controls.Add(_bcVal);

            sliderRow.Resize += (s, e) =>
            {
                _bcTrack.Width = sliderRow.Width - 60 - 74;
                _bcVal.Location = new Point(sliderRow.Width - 74, 12);
            };

            // 滑块 debounce：拖动时频繁触发 ValueChanged，等用户停手 500ms 才真正发 IOCTL
            System.Windows.Forms.Timer bcDebounce = null;
            _bcTrack.ValueChanged += (s, e) =>
            {
                _bcVal.Text = _bcTrack.Value + "%";
                _settings.ChargeLimit = _bcTrack.Value;
                _settings.ChargeMode = _bcTrack.Value >= 100 ? 0 : (_bcTrack.Value >= 80 ? 1 : 2);
                _settings.Save();

                // debounce：重启计时器，500ms 内不再变化才执行
                if (bcDebounce == null)
                {
                    bcDebounce = new System.Windows.Forms.Timer { Interval = 500 };
                    bcDebounce.Tick += (s2, e2) =>
                    {
                        bcDebounce.Stop();
                        int sliderVal = _bcTrack.Value;
                        // 异步设置充电阈值（IOCTL + 回读校验耗时 1-2s，不阻塞 UI）
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            bool ok = false;
                            if (_chargeCap != null && _chargeCap.Supported)
                                ok = OemChargeController.SetChargeLimit(sliderVal);
                            this.BeginInvoke(new Action(() =>
                            {
                                _chargeOpOk = ok;
                                UpdateModeSelection();
                                UpdateChargeHint();
                            }));
                        });
                    };
                }
                bcDebounce.Stop();
                bcDebounce.Start();
            };
            flow.Controls.Add(sliderRow);

            // === "充电与电源" 标签 + 两行只读状态 ===
            var lblHm = new Label();
            lblHm.Text = "充电与电源";
            lblHm.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblHm.ForeColor = CInk;
            lblHm.AutoSize = false;
            lblHm.Height = 24;
            lblHm.TextAlign = ContentAlignment.MiddleLeft;
            lblHm.Margin = new Padding(0, 0, 0, 6);
            flow.Controls.Add(lblHm);

            flow.Controls.Add(MakeChargeStatusRow("充电状态", out _chargeStatusVal));
            flow.Controls.Add(MakeChargeStatusRow("电源适配器", out _adapterStatusVal));

            RefreshChargeStatus();

            // === "电池健康" 大标题 ===
            var lblBH = new Label();
            lblBH.Text = "电池健康";
            lblBH.Font = new Font(Fnt, 12f, FontStyle.Bold);
            lblBH.ForeColor = CInk;
            lblBH.AutoSize = false;
            lblBH.Height = 30;
            lblBH.TextAlign = ContentAlignment.MiddleLeft;
            // Margin.Top=12 与 "长期满电告警" 行的 Margin.Bottom=4 合计 16px，
            // 与 上方模式卡片 → "自定义充电上限" 标签 之间的 16px 间距一致
            lblBH.Margin = new Padding(0, 12, 0, 6);
            flow.Controls.Add(lblBH);

            // === 电池健康卡片 ===
            var health = new RoundedCard();
            health.Height = 130;
            health.Margin = new Padding(0, 0, 0, 0);   // 底部间距由 flow 的 padding.bottom 提供

            var ring = new RingChart();
            _healthRing = ring;
            ring.Percent = 0;
            ring.RingColor = CGreen;
            ring.Location = new Point(16, 14);
            ring.Size = new Size(66, 66);
            health.Controls.Add(ring);

            var lblGood = new Label();
            _healthLabel = lblGood;
            lblGood.Text = "--";
            lblGood.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblGood.ForeColor = CInk;
            lblGood.AutoSize = true;
            lblGood.Location = new Point(94, 18);
            health.Controls.Add(lblGood);

            var lblStat = new Label();
            _healthStat = lblStat;
            lblStat.Text = "--";
            lblStat.Font = new Font(Fnt, 8.5f);
            lblStat.ForeColor = CInk2;
            lblStat.AutoSize = true;
            lblStat.Location = new Point(94, 42);
            health.Controls.Add(lblStat);

            var lblStat2 = new Label();
            _healthStat2 = lblStat2;
            lblStat2.Text = "--";
            lblStat2.Font = new Font(Fnt, 8.5f);
            lblStat2.ForeColor = CInk2;
            lblStat2.AutoSize = true;
            lblStat2.Location = new Point(94, 62);
            health.Controls.Add(lblStat2);

            var btnReport = new FlatBorderedButton();
            btnReport.Text = "健康报告";
            btnReport.Font = new Font(Fnt, 9f);
            btnReport.Size = new Size(180, 30);
            btnReport.Location = new Point(16, 90);
            btnReport.Click += (s, e) =>
            {
                try
                {
                    var batt = _context != null ? _context.CurrentBattery : BatteryInfo.Take();
                    var sb = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(batt.ComputerName))
                    {
                        sb.AppendLine("—— 系统信息 ——");
                        sb.AppendLine("  计算机名：" + (string.IsNullOrEmpty(batt.ComputerName) ? "未知" : batt.ComputerName));
                        sb.AppendLine("  产品型号：" + (string.IsNullOrEmpty(batt.SystemProduct) ? "未知" : batt.SystemProduct));
                        sb.AppendLine("  BIOS版本：" + (string.IsNullOrEmpty(batt.Bios) ? "未知" : batt.Bios));
                        sb.AppendLine("  系统版本：" + (string.IsNullOrEmpty(batt.OsBuild) ? "未知" : batt.OsBuild));
                        sb.AppendLine("  报告时间：" + (string.IsNullOrEmpty(batt.ReportTime) ? "未知" : batt.ReportTime));
                        sb.AppendLine();
                    }
                    sb.AppendLine("—— 电池信息 ——");
                    sb.AppendLine("  电池型号：" + (string.IsNullOrEmpty(batt.BatteryName) ? "未知" : batt.BatteryName));
                    sb.AppendLine("  制造商：" + (string.IsNullOrEmpty(batt.Manufacturer) ? "未知" : batt.Manufacturer));
                    sb.AppendLine("  序列号：" + (string.IsNullOrEmpty(batt.SerialNumber) ? "未知" : batt.SerialNumber));
                    sb.AppendLine("  化学类型：" + (string.IsNullOrEmpty(batt.Chemistry) ? "未知" : batt.Chemistry));
                    sb.AppendLine();
                    sb.AppendLine("—— 容量与健康 ——");
                    if (batt.DesignCapacityWh > 0)
                    {
                        sb.AppendLine("  设计容量：" + batt.DesignCapacityWh + " Wh");
                        sb.AppendLine("  当前满充：" + batt.FullChargeCapacityWh + " Wh");
                        int loss = batt.DesignCapacityWh - batt.FullChargeCapacityWh;
                        sb.AppendLine("  容量损耗：" + loss + " Wh（" + (batt.DesignCapacityWh > 0 ? loss * 100 / batt.DesignCapacityWh : 0) + "%）");
                        sb.AppendLine("  健康度：" + batt.HealthPercent + "%");
                    }
                    else
                    {
                        sb.AppendLine("  设计容量：不可用");
                        sb.AppendLine("  当前满充：不可用");
                        sb.AppendLine("  健康度：不可用");
                    }
                    sb.AppendLine("  循环次数：" + (batt.CycleCount > 0 ? batt.CycleCount.ToString() : "不可用"));
                    sb.AppendLine("  当前电量：" + (batt.IsPresent ? batt.Percent + "%" : "无电池"));
                    MessageBox.Show(this, sb.ToString(), "电池健康报告",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "电池健康报告"); }
            };
            health.Controls.Add(btnReport);

            var btnCalib = new FlatBorderedButton();
            btnCalib.Text = "电池校准";
            btnCalib.Font = new Font(Fnt, 9f);
            btnCalib.Size = new Size(180, 30);
            btnCalib.Location = new Point(206, 90);
            btnCalib.Click += (s, e) => MessageBox.Show(this,
                "电池校准步骤：\r\n\r\n" +
                "1. 将电池充满至 100%（接电源适配器）\r\n" +
                "2. 拔掉电源，正常使用直到电量低于 5%\r\n" +
                "3. 关机状态下接电源充满至 100%\r\n\r\n" +
                "此操作每 3-6 个月执行一次即可，\r\n" +
                "帮助系统重新校准电量计精度。",
                "电池校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
            health.Controls.Add(btnCalib);

            // 两个按钮左右对称、平分卡片宽度：左边距 = 右边距 = 中间 gap = 16
            const int btnPad = 16;
            health.Resize += (s, e) =>
            {
                int w = Math.Max(120, (health.Width - btnPad * 3) / 2);
                btnReport.Location = new Point(btnPad, 90);
                btnReport.Size = new Size(w, 30);
                btnCalib.Location = new Point(btnPad + w + btnPad, 90);
                btnCalib.Size = new Size(w, 30);
            };

            flow.Controls.Add(health);

            // === 统一宽度联动：让所有子行宽度 = flow 内容宽度 ===
            flow.Layout += (s, e) => SyncFlowChildrenWidth(flow);
            flow.SizeChanged += (s, e) => SyncFlowChildrenWidth(flow);
            SyncFlowChildrenWidth(flow);
            UpdateChargeHint();  // 初始状态填充 _bcHint 文本

            UpdateModeSelection();

            // 不支持充电阈值控制时：禁用滑块和模式卡，避免用户误操作后无反馈
            if (_chargeCap != null && !_chargeCap.Supported)
            {
                if (_bcTrack != null) _bcTrack.Enabled = false;
                if (_modeCards != null)
                {
                    foreach (var mcard in _modeCards)
                    {
                        mcard.Enabled = false;
                        mcard.IsDimmed = true;   // ModeCard 需支持 IsDimmed 画灰态
                    }
                }
            }

            return _pageCharge;
        }

        /// <summary>让 FlowLayoutPanel 里所有直接子控件的 Width 等于内容宽度。</summary>
        private static void SyncFlowChildrenWidth(FlowLayoutPanel flow)
        {
            int w = flow.ClientSize.Width - flow.Padding.Horizontal;
            if (w <= 0) return;
            foreach (Control c in flow.Controls)
                c.Width = Math.Max(50, w - c.Margin.Horizontal);
        }

        /// <summary>
        /// 充电页里的一行状态显示：左侧灰色小标签，右侧带色小圆点 + 状态值。
        /// 用 out 把值 Label 传出去，供 RefreshChargeStatus 更新文字与颜色。
        /// </summary>
        private Panel MakeChargeStatusRow(string title, out Label valueLabel)
        {
            var row = new Panel();
            row.Height = 30;
            row.Margin = new Padding(0, 0, 0, 4);
            row.BackColor = CPanel;

            var lbl = new Label();
            lbl.Text = title;
            lbl.Font = new Font(Fnt, 9f);
            lbl.ForeColor = CInk2;
            lbl.AutoSize = false;
            lbl.Location = new Point(0, 0);
            lbl.Size = new Size(220, 30);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            row.Controls.Add(lbl);

            var val = new Label();
            val.Text = "--";
            val.Font = new Font(Fnt, 9f, FontStyle.Bold);
            val.ForeColor = CInk;
            val.AutoSize = false;
            val.Size = new Size(160, 30);
            val.TextAlign = ContentAlignment.MiddleRight;
            val.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(val);

            row.Resize += (s, e) =>
            {
                val.Location = new Point(row.Width - val.Width, 0);
            };
            valueLabel = val;
            return row;
        }

        /// <summary>刷新充电页状态 + 健康卡（每 2s 由 refreshTimer 触发）</summary>
        private void RefreshChargeStatus()
        {
            if (_chargeStatusVal == null || _adapterStatusVal == null) return;
            try
            {
                // 复用 TrayContext 电池快照，避免并发 WMI 轮询
                var batt = _context != null ? _context.CurrentBattery : BatteryInfo.Take();
                if (batt.IsPresent)
                {
                    // 先检查 OEM 充电模式（保养模式可能暂停充电）
                    string chargeText = null;
                    Color chargeColor = CInk2;
                    if (_chargeCap != null && _chargeCap.Supported)
                    {
                        try
                        {
                            var status = OemChargeController.GetStatus();
                            if (status != null)
                            {
                                if (status.LimitPercent <= 80 && batt.Percent >= status.LimitPercent)
                                {
                                    chargeText = "保养中·暂停充电";
                                    chargeColor = CBlue;
                                }
                                else if (status.LimitPercent <= 80)
                                {
                                    chargeText = "保养中·充电至" + status.LimitPercent + "%";
                                    chargeColor = CBlue;
                                }
                            }
                        }
                        catch { }
                    }
                    // 没有保养模式信息，按电池实际状态显示
                    if (chargeText == null)
                    {
                        if (batt.IsCharging)
                        {
                            chargeText = "充电中";
                            chargeColor = CGreen;
                        }
                        else
                        {
                            chargeText = "停止充电";
                            chargeColor = CInk2;
                        }
                    }
                    _chargeStatusVal.Text = chargeText;
                    _chargeStatusVal.ForeColor = chargeColor;
                }
                else
                {
                    _chargeStatusVal.Text = "无电池";
                    _chargeStatusVal.ForeColor = CInk2;
                }

                var line = SystemInformation.PowerStatus.PowerLineStatus;
                if (line == PowerLineStatus.Online)
                {
                    _adapterStatusVal.Text = "适配器已接入";
                    _adapterStatusVal.ForeColor = CGreen;
                }
                else if (line == PowerLineStatus.Offline)
                {
                    _adapterStatusVal.Text = "未找到适配器";
                    _adapterStatusVal.ForeColor = COrange;
                }
                else
                {
                    _adapterStatusVal.Text = "未知";
                    _adapterStatusVal.ForeColor = CInk2;
                }

                // 刷新健康卡
                RefreshHealthCard(batt);
            }
            catch { }
        }

        /// <summary>刷新电池健康卡（环图 + 标题 + 两行描述）</summary>
        private void RefreshHealthCard(BatterySnapshot batt)
        {
            if (_healthRing == null) return;
            if (!batt.IsPresent)
            {
                _healthRing.Percent = 0;
                _healthLabel.Text = "未检测到电池";
                _healthStat.Text = "";
                _healthStat2.Text = "";
                return;
            }
            int hp = batt.HealthPercent;
            if (hp > 0)
            {
                _healthRing.Percent = hp;
                _healthRing.RingColor = hp >= 80 ? CGreen : (hp >= 60 ? COrange : Color.FromArgb(239, 68, 68));
                _healthLabel.Text = hp >= 80 ? "电池状态良好" : (hp >= 60 ? "电池健康度一般" : "建议更换电池");
                _healthStat.Text = "设计 " + batt.DesignCapacityWh + " Wh · 当前满充 " + batt.FullChargeCapacityWh + " Wh";
                string cycleText = batt.CycleCount > 0 ? ("循环次数 " + batt.CycleCount) : "循环次数不可用";
                _healthStat2.Text = cycleText;
            }
            else
            {
                _healthRing.Percent = 0;
                _healthLabel.Text = "深度数据不可用";
                _healthStat.Text = "当前机型未上报设计容量/循环次数";
                _healthStat2.Text = "基础电量数据不受影响";
            }
            _healthRing.Invalidate();
        }

        private void LayoutModes(Panel container)
        {
            int gap = 12;
            int w = (container.Width - gap * 2) / 3;
            for (int i = 0; i < _modeCards.Length; i++)
            {
                _modeCards[i].Location = new Point(i * (w + gap), 0);
                _modeCards[i].Size = new Size(w, 108);
            }
        }

        private void UpdateChargeHint()
        {
            // 不支持：显示机型原因
            if (_chargeCap == null || !_chargeCap.Supported)
            {
                string oem = _chargeCap != null ? _chargeCap.Oem.ToString() : "当前机型";
                string hint = _chargeCap != null && !string.IsNullOrEmpty(_chargeCap.Hint) ? _chargeCap.Hint : "";
                _bcHint.Text = oem + " 不支持充电阈值控制。" + hint;
                return;
            }
            // 支持但上次设置失败
            if (!_chargeOpOk)
            {
                _bcHint.Text = "上次设置未生效。驱动可能不支持此阈值，或需安装厂商电源管理软件（见「诊断」页）。";
                return;
            }
            // 成功
            string mode = _bcTrack.Value >= 100 ? "满电" : (_bcTrack.Value >= 80 ? "均衡" : "保养");
            if (_chargeCap.ModeType == ChargeModeType.ThreeMode)
            {
                // Lenovo 三档：实际只有保养(60)/正常(100)，滑块值已映射
                string m = _bcTrack.Value >= 80 ? "正常" : "保养";
                _bcHint.Text = "当前：" + m + "模式（" + _chargeCap.Oem + " 三档控制）。";
            }
            else
            {
                _bcHint.Text = "当前：充电至 " + _bcTrack.Value + "% 停止（" + mode + "模式）。";
            }
        }

        private void SelectChargeMode(int mode)
        {
            int[] caps = { 100, 80, 60 };
            string[] modeNames = { "满电", "均衡", "保养" };

            // 保存旧值，供 IOCTL 失败时回滚
            int oldMode = _settings.ChargeMode;
            int oldLimit = _settings.ChargeLimit;

            _settings.ChargeMode = mode;
            _settings.ChargeLimit = caps[mode];
            _settings.Save();

            if (_chargeCap == null || !_chargeCap.Supported)
            {
                // 不支持的机型：不持久化新设置，直接回滚
                _settings.ChargeMode = oldMode;
                _settings.ChargeLimit = oldLimit;
                _settings.Save();

                _chargeOpOk = false;
                if (_bcTrack != null) _bcTrack.Value = oldLimit;
                UpdateModeSelection();
                UpdateChargeHint();
                MessageBox.Show(this,
                    "当前机型不支持充电阈值控制，无法切换" + modeNames[mode] + "模式。\r\n\r\n" +
                    "请到「诊断」页查看充电控制功能状态和安装引导。",
                    "充电模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 后台执行设置（IOCTL + 回读校验可能耗时 1-2s）
            int targetMode = mode;
            int targetLimit = caps[mode];
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = OemChargeController.SetChargeLimit(targetLimit);
                this.BeginInvoke(new Action(() =>
                {
                    _chargeOpOk = ok;
                    if (ok)
                    {
                        // 成功：保持已持久化的新设置，仅同步 UI
                        if (_bcTrack != null) _bcTrack.Value = targetLimit;
                        UpdateModeSelection();
                        UpdateChargeHint();
                        MessageBox.Show(this,
                            modeNames[targetMode] + "模式已生效。\r\n\r\n" +
                            (targetMode == 2 ? "充电上限已设为 60%，电池将暂停充电。" :
                             targetMode == 1 ? "充电上限已设为 80%。" :
                             "充电上限已设为 100%，电池将充满。"),
                            "充电模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        // 失败：回滚 _settings 到旧值，避免重启后状态不一致
                        _settings.ChargeMode = oldMode;
                        _settings.ChargeLimit = oldLimit;
                        _settings.Save();

                        if (_bcTrack != null) _bcTrack.Value = oldLimit;
                        UpdateModeSelection();
                        UpdateChargeHint();
                        MessageBox.Show(this,
                            modeNames[targetMode] + "模式设置未生效。\r\n\r\n" +
                            "驱动接受了命令但未实际切换，可能原因：\r\n" +
                            "1. 厂商电源管理软件未安装或未运行\r\n" +
                            "2. BIOS 中充电阈值控制被禁用\r\n" +
                            "3. 电池温度过高/过低，驱动拒绝切换\r\n\r\n" +
                            "设置已回滚到之前的模式。\r\n" +
                            "请到「诊断」页查看驱动状态和安装引导。",
                            "充电模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        private void UpdateModeSelection()
        {
            if (_modeCards == null) return;
            int mode = _settings.ChargeMode;
            for (int i = 0; i < _modeCards.Length; i++)
                _modeCards[i].IsSelected = (i == mode);
        }

        // ============================================================
        // ④ 亮度页  (FlowLayoutPanel 垂直堆叠，宽度自适应)
        // ============================================================
        private Panel GetBrightPage()
        {
            if (_pageBright != null) return _pageBright;

            _pageBright = NewPage();

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            // Top=16：15pt 粗体大标题需要额外的顶部呼吸空间，避免上边缘被裁
            flow.Padding = new Padding(28, 16, 28, 22);
            flow.BackColor = CPanel;
            _pageBright.Controls.Add(flow);

            // 大标题
            var lblH = new Label();
            lblH.Text = "亮度";
            lblH.Font = new Font(Fnt, 15f, FontStyle.Bold);
            lblH.ForeColor = CInk;
            lblH.AutoSize = false;
            lblH.Height = 36;
            lblH.TextAlign = ContentAlignment.MiddleLeft;
            lblH.Margin = new Padding(0, 0, 0, 4);
            flow.Controls.Add(lblH);

            // 副标题
            var lblD = new Label();
            lblD.Text = "统一管理多显示器亮度，支持自动亮度。";
            lblD.Font = new Font(Fnt, 9f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Height = 22;
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Margin = new Padding(0, 0, 0, 12);
            flow.Controls.Add(lblD);

            // "自动亮度" 小节标题
            var lblAuto = new Label();
            lblAuto.Text = "自动亮度";
            lblAuto.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblAuto.ForeColor = CInk;
            lblAuto.AutoSize = false;
            lblAuto.Height = 24;
            lblAuto.TextAlign = ContentAlignment.MiddleLeft;
            lblAuto.Margin = new Padding(0, 0, 0, 4);
            flow.Controls.Add(lblAuto);

            // 自动亮度开关行
            var autoRow = new Panel();
            autoRow.Height = 32;
            autoRow.Margin = new Padding(0, 0, 0, 12);
            autoRow.BackColor = CPanel;
            var lblAutoDesc = new Label();
            lblAutoDesc.Text = "根据系统环境光自动调节";
            lblAutoDesc.Font = new Font(Fnt, 9f);
            lblAutoDesc.ForeColor = CInk;
            lblAutoDesc.AutoSize = false;
            lblAutoDesc.Size = new Size(240, 32);
            lblAutoDesc.Location = new Point(0, 0);
            lblAutoDesc.TextAlign = ContentAlignment.MiddleLeft;
            autoRow.Controls.Add(lblAutoDesc);

            _autoBrightSw = new ToggleSwitch();
            _autoBrightSw.Checked = _settings.AutoBrightness;
            _autoBrightSw.Size = new Size(46, 26);
            _autoBrightSw.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _autoBrightSw.CheckedChanged += (s, e) =>
            {
                _settings.AutoBrightness = _autoBrightSw.Checked;
                _settings.Save();
            };
            autoRow.Controls.Add(_autoBrightSw);
            autoRow.Resize += (s, e) =>
            {
                _autoBrightSw.Location = new Point(autoRow.Width - 46, 3);
            };
            flow.Controls.Add(autoRow);

            // 查找显示器按钮 -> 重新枚举并刷新卡片
            var btnFind = new FlatBorderedButton();
            btnFind.Text = "🔍 查找显示器";
            btnFind.Font = new Font(Fnt, 9.5f);
            btnFind.Size = new Size(140, 32);
            btnFind.Margin = new Padding(0, 0, 0, 8);
            // 标签声明提前供 btnFind lambda 引用
            var lblStatus = new Label();
            lblStatus.Font = new Font(Fnt, 9f);
            lblStatus.ForeColor = CGreen;
            lblStatus.AutoSize = false;
            lblStatus.Height = 22;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Margin = new Padding(0, 0, 0, 14);
            btnFind.Click += (s, e) =>
            {
                // 移除旧显示器卡片（lblStatus 之后的所有控件）
                for (int i = flow.Controls.Count - 1; i >= 0; i--)
                {
                    if (flow.Controls[i] == lblStatus) break;
                    flow.Controls.RemoveAt(i);
                }
                var monitors = BrightnessController.EnumerateMonitors();
                if (monitors.Count > 0)
                {
                    lblStatus.Text = "已检测到 " + monitors.Count + " 个显示器 · 支持亮度调节";
                    lblStatus.ForeColor = CGreen;
                    foreach (var m in monitors)
                        flow.Controls.Add(MakeMonitorCard(m.Name, m.Brightness));
                }
                else
                {
                    lblStatus.Text = "未检测到支持亮度调节的显示器";
                    lblStatus.ForeColor = COrange;
                }
                SyncBrightWidth(flow, btnFind);
            };
            flow.Controls.Add(btnFind);

            // 初始枚举显示器
            var initMonitors = BrightnessController.EnumerateMonitors();
            if (initMonitors.Count > 0)
            {
                lblStatus.Text = "已检测到 " + initMonitors.Count + " 个显示器 · 支持亮度调节";
                lblStatus.ForeColor = CGreen;
            }
            else
            {
                lblStatus.Text = "未检测到支持亮度调节的显示器";
                lblStatus.ForeColor = COrange;
            }
            flow.Controls.Add(lblStatus);

            // 显示器卡片
            foreach (var m in initMonitors)
                flow.Controls.Add(MakeMonitorCard(m.Name, m.Brightness));

            // 宽度联动 (查找显示器按钮除外——保持 140 固定宽度)
            flow.Layout += (s, e) => SyncBrightWidth(flow, btnFind);
            flow.SizeChanged += (s, e) => SyncBrightWidth(flow, btnFind);
            SyncBrightWidth(flow, btnFind);

            return _pageBright;
        }

        /// <summary>亮度页：除按钮外，全部铺满内容宽度</summary>
        private static void SyncBrightWidth(FlowLayoutPanel flow, Control keepFixed)
        {
            int w = flow.ClientSize.Width - flow.Padding.Horizontal;
            if (w <= 0) return;
            foreach (Control c in flow.Controls)
            {
                if (ReferenceEquals(c, keepFixed)) continue;
                c.Width = Math.Max(50, w - c.Margin.Horizontal);
            }
        }

        /// <summary>显示器亮度卡片：标题左 + 数值右，下方 亮度 + 滑块</summary>
        private RoundedCard MakeMonitorCard(string name, int val)
        {
            var card = new RoundedCard();
            card.Height = 84;
            card.Margin = new Padding(0, 0, 0, 12);

            var lblN = new Label();
            lblN.Text = name;
            lblN.Font = new Font(Fnt, 10f, FontStyle.Bold);
            lblN.ForeColor = CInk;
            lblN.AutoSize = true;
            lblN.Location = new Point(16, 12);
            card.Controls.Add(lblN);

            var lblV = new Label();
            lblV.Text = val + "%";
            lblV.Font = new Font(Fnt, 9f);
            lblV.ForeColor = CInk2;
            lblV.TextAlign = ContentAlignment.MiddleRight;
            lblV.AutoSize = false;
            lblV.Size = new Size(60, 20);
            lblV.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            card.Controls.Add(lblV);

            var lblB = new Label();
            lblB.Text = "亮度";
            lblB.Font = new Font(Fnt, 9f);
            lblB.ForeColor = CInk2;
            lblB.Location = new Point(16, 44);
            lblB.AutoSize = true;
            card.Controls.Add(lblB);

            // TrackBar 是 Win32 原生控件，thumb 会超出 Size 边界。
            // Location=(60,38)、height=34 → 中线 y≈55、thumb 底 y≈68；卡片 96 高、下边框在 y≈88，留 20px 余量。
            var tb = new RoundSlider();
            tb.Minimum = 0; tb.Maximum = 100; tb.Value = Math.Max(0, val);
            tb.AccentColor = CBlue;
            tb.Location = new Point(60, 50);
            tb.Size = new Size(200, 16);
            tb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tb.ValueChanged += (s, e) =>
            {
                lblV.Text = tb.Value + "%";
                // 拖动滑块时实时设置亮度（仅内置屏支持）
                try { BrightnessController.SetBrightness(tb.Value); } catch { }
            };
            card.Controls.Add(tb);

            card.Resize += (s, e) =>
            {
                lblV.Location = new Point(card.Width - 76, 12);
                tb.Width = card.Width - 60 - 16;
            };

            return card;
        }

        // ============================================================
        // ⑤ 设置页  (FlowLayoutPanel + 子页签)
        // ============================================================
        private Panel GetSettingsPage()
        {
            if (_pageSettings != null) return _pageSettings;

            _pageSettings = NewPage();

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = false;  // 主窗定高，禁用滚动条
            flow.Padding = new Padding(28, 16, 28, 8);
            flow.BackColor = CPanel;
            _pageSettings.Controls.Add(flow);
            _setFlow = flow;

            // 大标题
            var lblH = new Label();
            lblH.Text = "设置";
            lblH.Font = new Font(Fnt, 15f, FontStyle.Bold);
            lblH.ForeColor = CInk;
            lblH.AutoSize = false;
            lblH.Height = 36;
            lblH.TextAlign = ContentAlignment.MiddleLeft;
            lblH.Margin = new Padding(0, 0, 0, 4);
            flow.Controls.Add(lblH);
            _setTitle = lblH;

            // 副标题
            var lblD = new Label();
            lblD.Text = "基础模块参数配置中心。";
            lblD.Font = new Font(Fnt, 9f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Height = 22;
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Margin = new Padding(0, 0, 0, 12);
            flow.Controls.Add(lblD);
            _setSubtitle = lblD;

            // 子页签行（去掉"关于" - 已改由侧边栏底部"关于 TrayNexus"按钮入口）
            var subTab = new Panel();
            subTab.Height = 40;
            subTab.Margin = new Padding(0, 0, 0, 12);
            subTab.BackColor = CPanel;
            _setSubTabRow = subTab;
            string[] tabNames = { "通用", "内存", "计划", "关于" };
            var tabBtns = new SubTabButton[tabNames.Length];
            var tabPanels = new Panel[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                int idx = i;
                var b = new SubTabButton();
                b.Text = tabNames[i];
                b.Font = new Font(Fnt, 9.5f, FontStyle.Bold);
                b.Size = new Size(78, 32);
                b.Location = new Point(i * 86, 0);
                if (tabNames[i] == "关于") b.Visible = false;
                b.Click += (s, e) =>
                {
                    // 从可见子页签切换 → 顶部（设置/副标题/子页签行）恢复显示
                    SetSettingsHeaderVisible(true);
                    for (int k = 0; k < tabBtns.Length; k++)
                    {
                        tabBtns[k].IsActive = (k == idx);
                        if (tabPanels[k] != null) tabPanels[k].Visible = (k == idx);
                    }
                };
                subTab.Controls.Add(b);
                tabBtns[i] = b;
            }
            flow.Controls.Add(subTab);

            // 子页面容器（4 个 Panel 叠层，用 Visible 切换）
            // 高度动态计算：flow 客户区高 − padding − 大标题/副标题/子页签行占用，让内容不出滚动条
            var subContainer = new Panel();
            subContainer.Height = 420;
            subContainer.BackColor = CPanel;
            flow.Controls.Add(subContainer);
            _setSubContainer = subContainer;

            // flow Resize 时同步刷新 subContainer 高度
            flow.SizeChanged += (s, e) => AdjustSubContainerHeight();

            tabPanels[0] = BuildSettingsGeneral(subContainer);
            tabPanels[1] = BuildSettingsMemory(subContainer);
            tabPanels[2] = BuildSettingsSchedule(subContainer);
            tabPanels[3] = BuildSettingsAbout(subContainer);

            // 保存引用供 NavigateToAbout 使用
            _setTabBtns = tabBtns;
            _setTabPanels = tabPanels;

            // 默认选中"通用"
            tabBtns[0].IsActive = true;
            tabPanels[0].Visible = true;
            for (int i = 1; i < tabPanels.Length; i++) tabPanels[i].Visible = false;

            // 宽度联动：子页签行按钮不需要拉宽（保持左对齐），只让其他元素铺满
            flow.Layout += (s, e) => SyncSettingsWidth(flow, subTab);
            flow.SizeChanged += (s, e) => SyncSettingsWidth(flow, subTab);
            SyncSettingsWidth(flow, subTab);

            return _pageSettings;
        }

        /// <summary>设置页宽度联动：所有直接子控件（含子页签行本身）都按内容宽拉伸；
        /// 子页签行内部的按钮保持固定尺寸不受影响。</summary>
        private static void SyncSettingsWidth(FlowLayoutPanel flow, Panel subTab)
        {
            int w = flow.ClientSize.Width - flow.Padding.Horizontal;
            if (w <= 0) return;
            foreach (Control c in flow.Controls)
                c.Width = Math.Max(50, w - c.Margin.Horizontal);
        }

        // ============================================================
        // ⑥ 诊断页  (功能可用性诊断，按模块分组卡片)
        // ============================================================
        private Panel GetDiagnosticPage()
        {
            if (_pageDiagnostic != null) return _pageDiagnostic;

            _pageDiagnostic = NewPage();

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.Padding = new Padding(28, 40, 28, 50);   // 右侧 28px：SyncDiagWidth 已预留滚动条宽度
            flow.BackColor = CPanel;
            _pageDiagnostic.Controls.Add(flow);
            _diagFlow = flow;

            // 标题行：大标题左 + 「重新检测」按钮右
            var header = new Panel();
            header.Height = 40;
            header.Margin = new Padding(0, 0, 0, 6);
            header.BackColor = CPanel;
            var lblH = new Label();
            lblH.Text = "功能诊断";
            lblH.Font = new Font(Fnt, 15f, FontStyle.Bold);
            lblH.ForeColor = CInk;
            lblH.AutoSize = false;
            lblH.Size = new Size(300, 36);
            lblH.TextAlign = ContentAlignment.MiddleLeft;
            lblH.Location = new Point(0, 0);
            header.Controls.Add(lblH);

            var btnRecheck = new FlatBorderedButton();
            btnRecheck.Text = "重新检测";
            btnRecheck.Font = new Font(Fnt, 9f);
            btnRecheck.Size = new Size(96, 30);
            btnRecheck.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRecheck.Click += (s, e) =>
            {
                OemChargeController.InvalidateCache();
                RebuildDiagnosticContent();
                MessageBox.Show(this, "已重新检测所有功能模块。", "功能诊断",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            header.Controls.Add(btnRecheck);
            header.Resize += (s, e) =>
            {
                btnRecheck.Location = new Point(header.Width - 96, 3);
            };
            flow.Controls.Add(header);

            // 副标题
            var lblD = new Label();
            lblD.Text = "检测各功能模块依赖项的安装状态。未就绪项会给出原因和引导。";
            lblD.Font = new Font(Fnt, 9f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Height = 22;
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Margin = new Padding(0, 0, 0, 14);
            flow.Controls.Add(lblD);

            RebuildDiagnosticContent();

            // 宽度联动
            flow.Layout += (s, e) => SyncDiagWidth(flow);
            flow.SizeChanged += (s, e) => SyncDiagWidth(flow);
            SyncDiagWidth(flow);

            return _pageDiagnostic;
        }

        /// <summary>重建诊断页所有卡片内容（首次加载和「重新检测」时调用）</summary>
        private void RebuildDiagnosticContent()
        {
            var flow = _diagFlow;
            if (flow == null) return;

            // 清除除标题/副标题外的旧卡片
            for (int i = flow.Controls.Count - 1; i >= 2; i--)
                flow.Controls.RemoveAt(i);

            // 采集诊断数据
            _diagCap = OemChargeController.GetCapability();
            bool memOk = TestMemoryModule();
            bool battBasic = TestBatteryBasic();
            bool battDeep = TestBatteryDeep();
            bool brightWmi = TestBrightnessWmi();

            // 卡片 1：内存管理（3 项，全就绪或全异常）
            var card1 = BuildDiagCard("内存管理", NavIcon.Memory, CGreen);
            int y1 = 0;
            y1 = AddDiagnosticRow(card1, y1, "内存采集", "GlobalMemoryStatusEx + GetPerformanceInfo",
                memOk ? DiagStatus.Ready : DiagStatus.Warning, memOk ? "就绪" : "异常", null);
            y1 = AddDiagnosticRow(card1, y1, "内存释放", "StandbyList 清理 + WorkingSet 削减",
                memOk ? DiagStatus.Ready : DiagStatus.Warning, memOk ? "就绪" : "异常", null);
            y1 = AddDiagnosticRow(card1, y1, "阈值自动释放", "内存达阈值自动触发，需管理员权限",
                memOk ? DiagStatus.Ready : DiagStatus.Warning, memOk ? "就绪" : "异常", null, isLast: true);
            card1.SetSummary(memOk ? "3项就绪" : "需关注", memOk ? CGreen : COrange);
            flow.Controls.Add(card1);

            // 卡片 2：电池信息（基础 + 深度）
            var card2 = BuildDiagCard("电池信息", NavIcon.Battery, CBlue);
            int y2 = 0;
            y2 = AddDiagnosticRow(card2, y2, "电池基础数据", "电量百分比 / 充电状态 (WMI Win32_Battery)",
                battBasic ? DiagStatus.Ready : DiagStatus.Unsupported,
                battBasic ? "就绪" : "未检测到电池", null);
            y2 = AddDiagnosticRow(card2, y2, "电池深度数据", "设计容量 / 循环次数 (BatteryStaticData)",
                battDeep ? DiagStatus.Ready : DiagStatus.Unsupported,
                battDeep ? "就绪" : "硬件未上报",
                battDeep ? null : (Action)(() => MessageBox.Show(this,
                    "电池深度数据（设计容量、循环次数、温度）由 OEM 在 ACPI 表中实现。\r\n" +
                    "若此项显示「硬件未上报」，说明当前机型固件未实现该接口，\r\n" +
                    "安装任何驱动或软件都无法解决。\r\n\r\n" +
                    "基础电量数据不受影响，仍可正常使用。",
                    "电池深度数据说明", MessageBoxButtons.OK, MessageBoxIcon.Information)),
                isLast: true);
            int battReady = (battBasic ? 1 : 0) + (battDeep ? 1 : 0);
            card2.SetSummary(battReady + "/2就绪", battReady == 2 ? CGreen : (battReady == 1 ? COrange : CInk3));
            flow.Controls.Add(card2);

            // 卡片 3：OEM 充电控制（精简为 3 项：当前机型 / 机型品牌 / 充电控制功能）
            var card3 = BuildDiagCard("OEM 充电控制", NavIcon.Flash, COrange);
            int y3 = 0;
            // 行1：当前机型（厂商 · 型号）
            y3 = AddDiagnosticRow(card3, y3, "当前机型",
                (_diagCap.Manufacturer ?? "未知") + " · " + (_diagCap.Model ?? ""),
                DiagStatus.Info, _diagCap.Oem.ToString(), null);
            // 行2：机型品牌（自动检测后的厂商名）
            string brandName;
            switch (_diagCap.Oem)
            {
                case OemVendor.Asus:   brandName = "ASUS（华硕）"; break;
                case OemVendor.Lenovo: brandName = "Lenovo（联想）"; break;
                case OemVendor.Dell:   brandName = "Dell（戴尔）"; break;
                case OemVendor.HP:     brandName = "HP（惠普）"; break;
                default:               brandName = "未识别"; break;
            }
            y3 = AddDiagnosticRow(card3, y3, "机型品牌", brandName,
                _diagCap.Oem == OemVendor.Unknown ? DiagStatus.Unsupported : DiagStatus.Info,
                _diagCap.Oem == OemVendor.Unknown ? "未识别" : "已识别", null);
            // 行3：充电控制功能（自动检测支持状态，后面显示具体结果）
            string funcDesc;
            DiagStatus funcStatus;
            string funcTag;
            Action funcAction = null;
            if (_diagCap.Supported)
            {
                funcDesc = _diagCap.DriverName + " · 阈值范围 " + _diagCap.MinThreshold + "-" + _diagCap.MaxThreshold + "%";
                funcStatus = DiagStatus.Ready;
                funcTag = _diagCap.ModeType == ChargeModeType.ThreeMode ? "就绪(三档)" : "就绪(任意)";
            }
            else if (_diagCap.Oem == OemVendor.Dell || _diagCap.Oem == OemVendor.HP)
            {
                funcDesc = _diagCap.Hint;
                funcStatus = DiagStatus.Pending;
                funcTag = "开发中";
                OemVendor guideOemDellHp = _diagCap.Oem;
                funcAction = () => ShowDriverInstallGuide(guideOemDellHp);
            }
            else if (_diagCap.Oem == OemVendor.Unknown)
            {
                funcDesc = "未识别厂商，无法检测充电控制能力";
                funcStatus = DiagStatus.Unsupported;
                funcTag = "不支持";
                funcAction = () => ShowDriverInstallGuide(OemVendor.Unknown);
            }
            else
            {
                // ASUS/Lenovo 但驱动未装 -> 弹窗引导安装
                funcDesc = _diagCap.Hint + "（点击「查看」查看安装引导）";
                funcStatus = DiagStatus.Warning;
                funcTag = "需安装";
                OemVendor guideOem = _diagCap.Oem;
                funcAction = () => ShowDriverInstallGuide(guideOem);
            }
            y3 = AddDiagnosticRow(card3, y3, "充电控制功能", funcDesc, funcStatus, funcTag, funcAction, isLast: true, wrapDesc: true);
            // 充电汇总
            string chargeSummary;
            Color chargeColor;
            if (_diagCap.Supported)
            {
                chargeSummary = "已支持";
                chargeColor = CGreen;
            }
            else if (_diagCap.Oem == OemVendor.Dell || _diagCap.Oem == OemVendor.HP)
            {
                chargeSummary = "开发中";
                chargeColor = CInk3;
            }
            else if (_diagCap.Oem == OemVendor.Unknown)
            {
                chargeSummary = "不支持";
                chargeColor = CInk3;
            }
            else
            {
                chargeSummary = "需安装驱动";
                chargeColor = COrange;
            }
            card3.SetSummary(chargeSummary, chargeColor);
            flow.Controls.Add(card3);

            // 卡片 4：亮度控制
            var card4 = BuildDiagCard("亮度控制", NavIcon.Sun, CBlue);
            int y4 = 0;
            y4 = AddDiagnosticRow(card4, y4, "内置屏亮度", "WMI WmiMonitorBrightnessMethods",
                brightWmi ? DiagStatus.Ready : DiagStatus.Warning,
                brightWmi ? "就绪" : "不可用",
                brightWmi ? null : (Action)(() => MessageBox.Show(this,
                    "内置屏亮度调节需要显卡驱动支持 WMI WmiMonitorBrightnessMethods 接口。\r\n\r\n" +
                    "不可用的常见原因：\r\n" +
                    "1. 台式机无内置屏\r\n" +
                    "2. 显卡驱动过旧或未安装\r\n" +
                    "3. 外接显示器不支持 WMI 亮度控制\r\n\r\n" +
                    "解决方法：\r\n" +
                    "更新显卡驱动到最新版本后重试。",
                    "内置屏亮度说明", MessageBoxButtons.OK, MessageBoxIcon.Information)));
            y4 = AddDiagnosticRow(card4, y4, "外接屏 DDC/CI", "物理显示器硬件支持 DDC/CI 协议",
                DiagStatus.Pending, "开发中", null, isLast: true);
            card4.SetSummary(brightWmi ? "1/2就绪" : "待接入", brightWmi ? CBlue : CInk3);
            flow.Controls.Add(card4);

            // 全部卡片添加完后，显式刷新滚动范围
            UpdateDiagScrollRange();
        }

        /// <summary>构建一张可折叠诊断卡片。返回 CollapsibleDiagCard，行通过 AddDiagnosticRow 加到 .Body。</summary>
        private CollapsibleDiagCard BuildDiagCard(string title, NavIcon icon, Color accent)
        {
            var card = new CollapsibleDiagCard(title, icon, accent);
            // 展开/收起时通知 FlowLayoutPanel 重新布局（让后续卡片上移/下移 + 刷新滚动范围）
            card.ExpandedChanged += (s, e) =>
            {
                if (_diagFlow == null) return;
                _diagFlow.PerformLayout();
                // 显式计算并设置 AutoScrollMinSize，确保滚动范围包含所有卡片总高
                UpdateDiagScrollRange();
            };
            return card;
        }

        /// <summary>
        /// 计算诊断页所有子控件的总高度，显式设置 FlowLayoutPanel 的 AutoScrollMinSize。
        /// FlowLayoutPanel 的 AutoScroll 有时无法自动感知子控件高度变化（尤其我们手动改
        /// CollapsibleDiagCard.Height 的场景），需要显式赋值 AutoScrollMinSize 触发滚动条。
        /// </summary>
        private void UpdateDiagScrollRange()
        {
            if (_diagFlow == null) return;
            int totalH = _diagFlow.Padding.Vertical;   // top + bottom padding
            foreach (Control c in _diagFlow.Controls)
            {
                totalH += c.Height + c.Margin.Vertical;
            }
            int w = Math.Max(1, _diagFlow.ClientSize.Width);
            _diagFlow.AutoScrollMinSize = new Size(w, totalH);
        }

        /// <summary>诊断行状态枚举</summary>
        private enum DiagStatus { Ready, Warning, Unsupported, Pending, Info }

        /// <summary>
        /// 在诊断卡片的 Body 区追加一行。返回下一个 y 坐标。
        /// 行高 44（标题 20 + 描述 18 + 上下padding），行间距 4。
        /// </summary>
        private int AddDiagnosticRow(CollapsibleDiagCard card, int y, string name, string desc,
            DiagStatus status, string statusText, Action onClick, bool isLast = false, bool wrapDesc = false)
        {
            const int rowGap = 6;   // 行间距
            Panel body = card.Body;

            // 行高计算：wrapDesc=true 时副标题可换行（动态高度），否则固定 24
            int descH = 24;
            if (wrapDesc)
            {
                try
                {
                    using (var g = body.CreateGraphics())
                    {
                        var font = new Font(Fnt, 8f);
                        // 副标题宽度右侧留 120 给状态标签，避免文字被标签遮挡
                        int availW = Math.Max(200, body.Width - 32 - 120);
                        var sz = g.MeasureString(desc, font, availW);
                        descH = Math.Max(24, (int)Math.Ceiling(sz.Height) + 2);
                    }
                }
                catch { }
            }
            int rowH = 6 + 22 + 4 + descH + 6;   // top + title + gap + desc + bottom（状态标签在右侧垂直居中）

            var row = new TransparentPanel();
            row.Location = new Point(16, y);
            row.Size = new Size(Math.Max(200, body.Width - 32), rowH);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                if (!isLast)
                {
                    using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                        e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
                }
            };

            // 标题（第一行，右侧留 120 给状态标签）
            var lblN = new Label();
            lblN.Text = name;
            lblN.Font = new Font(Fnt, 9.5f);
            lblN.ForeColor = CInk;
            lblN.AutoSize = false;
            lblN.Size = new Size(Math.Max(200, row.Width - 120), 22);
            lblN.TextAlign = ContentAlignment.MiddleLeft;
            lblN.Location = new Point(0, 6);
            lblN.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblN.BackColor = Color.Transparent;
            row.Controls.Add(lblN);

            // 副标题（第二行，右侧留 120 给状态标签）
            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(Math.Max(200, row.Width - 120), descH);
            lblD.TextAlign = ContentAlignment.TopLeft;
            lblD.Location = new Point(0, 32);
            lblD.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblD.BackColor = Color.Transparent;
            row.Controls.Add(lblD);

            // 状态标签 + 按钮（右侧垂直居中，不另起一行）
            int statusY = (rowH - 22) / 2;   // 垂直居中
            Color tagColor;
            switch (status)
            {
                case DiagStatus.Ready:        tagColor = CGreen; break;
                case DiagStatus.Warning:      tagColor = COrange; break;
                case DiagStatus.Unsupported:  tagColor = CInk3; break;
                case DiagStatus.Pending:      tagColor = CInk3; break;
                case DiagStatus.Info:         tagColor = CBlue; break;
                default:                      tagColor = CInk3; break;
            }
            var tag = new TagLabel();
            tag.Text = statusText;
            tag.Accent = tagColor;
            tag.Font = new Font(Fnt, 8f, FontStyle.Bold);
            tag.Size = new Size(80, 22);
            tag.Location = new Point(row.Width - 80, statusY);
            tag.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tag.BackColor = Color.FromArgb(245, 246, 249);
            row.Controls.Add(tag);

            // 可选操作按钮（在状态标签左侧）
            if (onClick != null)
            {
                var link = new SoftLinkButton();
                link.Text = "查看";
                link.Font = new Font(Fnt, 9f, FontStyle.Bold);
                link.Size = new Size(60, 24);
                link.Location = new Point(row.Width - 80 - 60 - 8, statusY - 1);
                link.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                link.Click += (s, e) => { try { onClick(); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
                row.Controls.Add(link);
                tag.BringToFront();
            }

            body.Controls.Add(row);
            card.RefreshHeight();
            return y + rowH + rowGap;   // 加行间距
        }

        /// <summary>诊断页宽度联动：卡片铺满内容宽度，始终预留滚动条宽度避免卡片因滚动条出现/消失而跳动。
        /// 用 flow.Width（常量）而非 ClientSize.Width（滚动条出现时会变），确保卡片宽度恒定。</summary>
        private static void SyncDiagWidth(FlowLayoutPanel flow)
        {
            // flow.Width 是控件总宽（常量），减去左 padding 和滚动条宽度，得到卡片恒定宽度
            int scrollbarW = SystemInformation.VerticalScrollBarWidth;
            int w = flow.Width - flow.Padding.Left - scrollbarW;
            if (w <= 0) return;
            foreach (Control c in flow.Controls)
            {
                c.Width = Math.Max(200, w - c.Margin.Horizontal);
                // 折叠卡片：同步 Body 内每行的宽度 + 行内 Label 宽度
                var card = c as CollapsibleDiagCard;
                if (card != null && card.Body != null)
                {
                    int bodyW = card.Body.ClientSize.Width;
                    if (bodyW <= 0) bodyW = c.ClientSize.Width;
                    foreach (Control row in card.Body.Controls)
                    {
                        int rowW = Math.Max(50, bodyW - 32);
                        row.Width = rowW;
                        // 同步行内 Label 宽度（右侧留 120 给状态标签）
                        int lblW = Math.Max(200, rowW - 120);
                        if (row.Controls.Count >= 2)
                        {
                            row.Controls[0].Width = lblW;
                            row.Controls[1].Width = lblW;
                        }
                    }
                }
            }
        }

        // ============================================================
        // 诊断探测辅助方法
        // ============================================================
        private static bool TestMemoryModule()
        {
            try { var s = MemoryInfo.Take(); return s.TotalBytes > 0; }
            catch { return false; }
        }

        private static bool TestBatteryBasic()
        {
            try { var b = BatteryInfo.Take(); return b.IsPresent; }
            catch { return false; }
        }

        private static bool TestBatteryDeep()
        {
            try
            {
                var b = BatteryInfo.Take();
                return b.IsPresent && (b.DesignCapacityWh > 0 || b.CycleCount > 0);
            }
            catch { return false; }
        }

        private static bool TestBrightnessWmi()
        {
            try
            {
                return BrightnessController.IsSupported();
            }
            catch { return false; }
        }

        /// <summary>打开厂商驱动下载页（直接跳到支持页，非官网首页）</summary>
        private static void OpenDriverDownload(OemVendor oem)
        {
            string url;
            switch (oem)
            {
                case OemVendor.Asus:
                    url = "https://www.asus.com.cn/support/Download-Center/";
                    break;
                case OemVendor.Lenovo:
                    url = "https://newsupport.lenovo.com.cn/driveDownloadsIndex.html";
                    break;
                case OemVendor.Dell:
                    url = "https://www.dell.com/support/home/zh-cn/drivers";
                    break;
                case OemVendor.HP:
                    url = "https://support.hp.com/cn-zh/drivers";
                    break;
                default:
                    url = "https://www.google.com/search?q=电源管理驱动下载";
                    break;
            }
            OpenUrl(url);
        }

        /// <summary>显示驱动安装引导弹窗（具体到软件名和安装步骤）</summary>
        private void ShowDriverInstallGuide(OemVendor oem)
        {
            string title = "驱动安装引导";
            string body;
            switch (oem)
            {
                case OemVendor.Lenovo:
                    body = "Lenovo 充电阈值控制只需安装 Energy Management 驱动（5MB）：\r\n\r\n" +
                           "说明：EnergyDrv 设备由 AcpiVpc.sys 驱动创建，\r\n" +
                           "通过 IOCTL 0x831020F8 直接控制充电模式，\r\n" +
                           "无需安装 557MB 的 Lenovo Vantage。\r\n\r\n" +
                           "安装步骤：\r\n" +
                           "① 打开 Lenovo 驱动下载页（点「确定」跳转）\r\n" +
                           "② 搜索您的机型，下载「Energy Management 驱动」\r\n" +
                           "   （英文：Energy Management Driver / AcpiVpc）\r\n" +
                           "③ 安装后重启 TrayNexus\r\n\r\n" +
                           "诊断页应显示「就绪」，充电控制生效。\r\n\r\n" +
                           "点击「确定」打开 Lenovo 驱动下载页。";
                    break;
                case OemVendor.Asus:
                    body = "ASUS 充电阈值控制需要以下组件：\r\n\r\n" +
                           "1. MyASUS（应用商店搜索安装）\r\n" +
                           "2. ASUS System Control Interface 驱动\r\n\r\n" +
                           "安装步骤：\r\n" +
                           "① 打开 MyASUS\r\n" +
                           "② 进入「自定义」->「电源与性能」\r\n" +
                           "③ 设置「电池健康充电」阈值\r\n\r\n" +
                           "安装后重启 TrayNexus，诊断页应显示「就绪」。\r\n\r\n" +
                           "点击「确定」打开 ASUS 驱动下载页。";
                    break;
                case OemVendor.Dell:
                    body = "Dell 充电阈值控制需要以下组件：\r\n\r\n" +
                           "1. Dell Command | Power Manager\r\n" +
                           "2. Dell Command | Configure CLI (cctk.exe)\r\n\r\n" +
                           "TrayNexus 暂未集成 Dell 充电控制，\r\n" +
                           "请使用 Dell 官方软件管理充电阈值。\r\n\r\n" +
                           "点击「确定」打开 Dell 驱动下载页。";
                    break;
                case OemVendor.HP:
                    body = "HP 充电阈值控制需要以下组件：\r\n\r\n" +
                           "1. HP Power Manager\r\n" +
                           "2. HP Support Assistant\r\n\r\n" +
                           "TrayNexus 暂未集成 HP 充电控制，\r\n" +
                           "请使用 HP 官方软件管理充电阈值。\r\n\r\n" +
                           "点击「确定」打开 HP 驱动下载页。";
                    break;
                default:
                    body = "当前机型未识别厂商，无法提供充电阈值控制。\r\n\r\n" +
                           "如需此功能，请确认您的笔记本品牌，\r\n" +
                           "并安装对应厂商的电源管理软件。";
                    title = "充电控制不支持";
                    break;
            }
            DialogResult dr = MessageBox.Show(this, body, title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (dr == DialogResult.OK && oem != OemVendor.Unknown)
                OpenDriverDownload(oem);
        }

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex) { Settings.Log("OpenUrl 失败: " + ex.Message); }
        }

        /// <summary>
        /// 侧边栏底部"关于 TrayNexus"按钮：跳到设置页并激活"关于"子页签。
        /// 从这个入口进入时，隐藏设置页顶部"设置/副标题/子页签行"三元素，让关于内容独占。
        /// </summary>
        private void NavigateToAbout()
        {
            NavigateTo(5);   // 设置页现在是 index 5（诊断是 index 4）
            if (_setTabBtns == null || _setTabPanels == null) return;
            int aboutIdx = _setTabBtns.Length - 1; // 关于 是最后一个
            for (int k = 0; k < _setTabBtns.Length; k++)
            {
                _setTabBtns[k].IsActive = (k == aboutIdx);
                if (_setTabPanels[k] != null) _setTabPanels[k].Visible = (k == aboutIdx);
            }
            // 隐藏顶部三元素，让"关于"内容占满
            SetSettingsHeaderVisible(false);
        }

        /// <summary>切换设置页顶部（大标题/副标题/子页签行）的可见性；
        /// 隐藏时（关于独占）把 flow 底部内边距对齐顶部内边距，
        /// 并让 subContainer 撑满 flow 内容区高度，中间内容自然铺开。</summary>
        private void SetSettingsHeaderVisible(bool visible)
        {
            if (_setTitle != null) _setTitle.Visible = visible;
            if (_setSubtitle != null) _setSubtitle.Visible = visible;
            if (_setSubTabRow != null) _setSubTabRow.Visible = visible;
            if (_setFlow != null)
            {
                var pad = _setFlow.Padding;
                pad.Bottom = visible ? 8 : 16;   // 关于独占（visible=false）时上下都是 16；带头时下边距缩到 8 不出滚动条
                _setFlow.Padding = pad;
            }
            AdjustSubContainerHeight();
        }

        /// <summary>根据顶部三元素(设置/副标题/子页签)是否可见，动态计算 subContainer 高度铺满 flow 剩余高度。</summary>
        private void AdjustSubContainerHeight()
        {
            if (_setSubContainer == null || _setFlow == null) return;
            int consumed = 0;
            foreach (Control c in _setFlow.Controls)
            {
                if (c == _setSubContainer) continue;
                if (!c.Visible) continue;
                consumed += c.Height + c.Margin.Vertical;
            }
            int avail = _setFlow.ClientSize.Height - _setFlow.Padding.Vertical - consumed - _setSubContainer.Margin.Vertical;
            if (avail > 0) _setSubContainer.Height = avail;
        }

        private Panel BuildSettingsGeneral(Panel host)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = CPanel;
            host.Controls.Add(p);

            int y = 0;
            y = AddSettingsToggleRow(p, y, "开机自启（当前用户）", "通过任务计划程序以最高权限静默启动",
                AutoStartManager.IsEnabled(), on =>
                {
                    if (on) AutoStartManager.Enable(); else AutoStartManager.Disable();
                });

            return p;
        }

        private Panel BuildSettingsMemory(Panel host)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = CPanel;
            host.Controls.Add(p);

            int y = 0;
            y = AddSettingsLinkRow(p, y, "释放方式", "选择内存清理策略", "更改", () =>
            {
                string[] items = { "清理待机页 (StandbyList)", "削减工作集 (WorkingSet)", "组合释放 (推荐)" };
                int idx = (int)_settings.Mode - 1;
                using (var dlg = new PickerDialog("释放方式", items, idx))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _settings.Mode = (ReleaseMode)(dlg.SelectedIndex + 1);
                        _settings.Save();
                    }
                }
            });
            y = AddSettingsLinkRow(p, y, "阈值触发", "内存达到 " + _settings.ThresholdPercent + "% 自动释放", "设置", () =>
            {
                using (var dlg = new NumberDialog("阈值触发", "内存使用率达到多少 % 时自动释放：", _settings.ThresholdPercent, 1, 99))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _settings.ThresholdEnabled = true;
                        _settings.ThresholdPercent = dlg.Value;
                        _settings.Save();
                    }
                }
            });
            y = AddSettingsLinkRow(p, y, "编辑白名单文件", "whitelist.txt", "打开", () =>
            {
                try { System.Diagnostics.Process.Start("notepad.exe", _settings.WhitelistPath); }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            y = AddSettingsLinkRow(p, y, "打开配置文件夹", "%APPDATA%\\Traynexus", "打开", () =>
            {
                try { System.Diagnostics.Process.Start(_settings.ConfigDir); }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            return p;
        }

        private Panel BuildSettingsSchedule(Panel host)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = CPanel;
            host.Controls.Add(p);

            int y = 0;
            y = AddSettingsToggleRow(p, y, "夜间自动保养", "22:00–07:00 充电上限自动降至 60%",
                _settings.NightCareEnabled, on =>
                {
                    _settings.NightCareEnabled = on;
                    _settings.Save();
                });
            y = AddSettingsToggleRow(p, y, "周末满充准备", "周六日凌晨自动充电至 100%",
                _settings.WeekendFullCharge, on =>
                {
                    _settings.WeekendFullCharge = on;
                    _settings.Save();
                });
            y = AddSettingsToggleRow(p, y, "会议免扰模式", "检测到全屏会议软件时暂停自动释放",
                _settings.MeetingDndMode, on =>
                {
                    _settings.MeetingDndMode = on;
                    _settings.Save();
                });
            return p;
        }

        private Panel BuildSettingsAbout(Panel host)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = CPanel;
            host.Controls.Add(p);

            int y = 8;

            // 品牌区 —— 高度加到 56，主/副标题之间预留 6px 视觉间距，避免 11pt Bold 中文压到副标题上
            var brand = new Panel();
            brand.Location = new Point(0, y);
            brand.Size = new Size(host.Width, 56);
            brand.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            brand.BackColor = CPanel;

            // 品牌区图标：50×50，Y=2 —— 顶部与主标题顶(Y=2)对齐，底部(Y=52)与副标题底(Y=52)对齐
            var appIcon = MakeIconControl(LogoLoader.GetBrandBitmap(), NavIcon.Flash, COrange, CIcoBri);
            appIcon.Size = new Size(50, 50);
            appIcon.Location = new Point(0, 2);
            brand.Controls.Add(appIcon);

            // 主标题：Y=2，Height=24（顶缘 2 与 logo 顶对齐）
            var lblApp = new Label();
            lblApp.Text = "TrayNexus";
            lblApp.Font = new Font(Fnt, 11f, FontStyle.Bold);
            lblApp.ForeColor = CInk;
            lblApp.AutoSize = false;
            lblApp.Size = new Size(200, 24);
            lblApp.TextAlign = ContentAlignment.MiddleLeft;
            lblApp.Location = new Point(60, 2);
            brand.Controls.Add(lblApp);

            // 副标题：Y=32，Height=20（底缘 52 与 logo 底对齐；主标题底 26 → 6px 段落内空隙）
            var lblSlog = new Label();
            lblSlog.Text = "系统资源一体化管家";
            lblSlog.Font = new Font(Fnt, 8f);
            lblSlog.ForeColor = CInk2;
            lblSlog.AutoSize = false;
            lblSlog.Size = new Size(240, 20);
            lblSlog.TextAlign = ContentAlignment.MiddleLeft;
            lblSlog.Location = new Point(60, 32);
            brand.Controls.Add(lblSlog);

            var lblVer = new Label();
            lblVer.Text = "v1.0717.3";
            lblVer.Font = new Font(Fnt, 8f);
            lblVer.ForeColor = CInk2;
            lblVer.AutoSize = false;
            lblVer.TextAlign = ContentAlignment.MiddleRight;
            lblVer.Size = new Size(110, 20);
            lblVer.Location = new Point(brand.Width - 110, 18);
            lblVer.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            brand.Controls.Add(lblVer);

            p.Controls.Add(brand);
            // 品牌区 -> 内容区：+56（brand 高）+ 24（段落层级空档），让品牌头与下方信息组有明显段落感
            y += 80;

            // —— 信息组 1：许可 / 版权 / 联系方式 —— 组内小间隙 4px，组末段落间隙由 desc 前的 +8 提供
            y = AddInfoRow(p, y, "许可声明", "个人免费授权 · 开源应用"); y += 4;
            y = AddInfoRow(p, y, "版权所有", "Aiyow"); y += 4;
            y = AddInfoRow(p, y, "联系方式", "E-mail：zzaiyow@agent.qq.com   WeChat：zzAiyow");
            y += 12; // 段落层级：信息组 → 简介

            // 简介
            var lblDesc = new Label();
            lblDesc.Text = "常驻托盘轻量级资源管家，整合内存释放 / 电池健康管理 / 多显示器亮度控制。";
            lblDesc.Font = new Font(Fnt, 8.5f);
            lblDesc.ForeColor = CInk2;
            lblDesc.AutoSize = false;
            lblDesc.Size = new Size(host.Width, 20);
            lblDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblDesc.Location = new Point(0, y);
            p.Controls.Add(lblDesc);
            y += 30;

            // 标签
            var tags = new Panel();
            tags.Location = new Point(0, y);
            tags.Size = new Size(host.Width, 28);
            tags.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            AddTag(tags,   0, "内存释放",  COrange);
            AddTag(tags,  92, "电池保养",  CGreen);
            AddTag(tags, 184, "亮度管理",  CBlue);
            p.Controls.Add(tags);
            y += 44;
            y += 12; // 段落层级：简介/标签 → 反馈 & 仓库 组

            // —— 信息组 2：问题反馈 / GitHub —— 组内小间隙 4px
            y = AddInfoRowWithLink(p, y, "问题反馈", "GitHub Issues", "提交",
                () => { try { System.Diagnostics.Process.Start("https://github.com/traynexus/traynexus/issues"); } catch { } });
            y += 4;
            // GitHub 行：内嵌 github_icon.png（黑版）。加载失败时兜底到通用 Flash 图标（几乎不会走到）。
            var ghIcon = MakeIconControl(LogoLoader.GetGithubBitmap(false), NavIcon.Flash, CInk, Color.Transparent);
            y = AddInfoRowWithIconAndLink(p, y, ghIcon, "GitHub", "github.com/traynexus/traynexus", "打开",
                () => { try { System.Diagnostics.Process.Start("https://github.com/traynexus/traynexus"); } catch { } });

            // 版权：贴 subContainer 底部（Anchor=Bottom）—— 关于独占时 subContainer 会撑高，
            // 中间的信息组自然向下铺开填充空白；版权与末行之间由 topMargin(=12) 制造段落层级。
            // 版权 Y = host.Height - 34（比字号本身多 10px 顶部呼吸空间），Height=34。
            var lblCr = new Label();
            lblCr.Text = "© 2026 Aiyow · Made with ⚡ on Windows.";
            lblCr.Font = new Font(Fnt, 8f);
            lblCr.ForeColor = CInk2;
            lblCr.TextAlign = ContentAlignment.MiddleCenter;
            lblCr.AutoSize = false;
            lblCr.Size = new Size(host.Width, 34);
            lblCr.Location = new Point(0, host.Height - 34);
            lblCr.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(lblCr);

            return p;
        }

        private void AddTag(Panel parent, int x, string text, Color accent)
        {
            var t = new TagLabel();
            t.Text = text;
            t.Accent = accent;
            t.Font = new Font(Fnt, 8f, FontStyle.Bold);
            t.Size = new Size(84, 22);
            t.Location = new Point(x, 2);
            parent.Controls.Add(t);
        }

        // ============================================================
        // 通用行辅助
        // ============================================================
        private int AddSettingsToggleRow(Panel parent, int y, string title, string desc, bool init, Action<bool> onChange)
        {
            var row = new Panel();
            row.Location = new Point(0, y);
            row.Size = new Size(parent.Width, 52);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
            };

            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 9.5f);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Size = new Size(320, 22);
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Location = new Point(0, 4);
            row.Controls.Add(lblT);

            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(320, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(0, 28);
            row.Controls.Add(lblD);

            var sw = new ToggleSwitch();
            sw.Checked = init;
            sw.Size = new Size(46, 26);
            sw.Location = new Point(row.Width - 46, 12);
            sw.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sw.CheckedChanged += (s, e) => { try { onChange(sw.Checked); } catch { } };
            row.Controls.Add(sw);

            parent.Controls.Add(row);
            return y + 56;
        }

        private int AddSettingsLinkRow(Panel parent, int y, string title, string desc, string linkText, Action onClick)
        {
            var row = new Panel();
            row.Location = new Point(0, y);
            row.Size = new Size(parent.Width, 52);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
            };

            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 9.5f);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Size = new Size(320, 22);
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Location = new Point(0, 4);
            row.Controls.Add(lblT);

            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(320, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(0, 28);
            row.Controls.Add(lblD);

            var link = new SoftLinkButton();
            link.Text = linkText;
            link.Font = new Font(Fnt, 9f, FontStyle.Bold);
            link.Size = new Size(72, 28);
            link.Location = new Point(row.Width - 72, 12);
            link.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            link.Click += (s, e) => { try { onClick(); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            row.Controls.Add(link);

            parent.Controls.Add(row);
            return y + 56;
        }

        private int AddSettingsComboRow(Panel parent, int y, string title, string desc, string[] options, int init, Action<int> onChange)
        {
            var row = new Panel();
            row.Location = new Point(0, y);
            row.Size = new Size(parent.Width, 52);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
            };

            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 9.5f);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Size = new Size(320, 22);
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Location = new Point(0, 4);
            row.Controls.Add(lblT);

            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(320, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(0, 28);
            row.Controls.Add(lblD);

            var cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Items.AddRange(options);
            cb.SelectedIndex = Math.Max(0, Math.Min(options.Length - 1, init));
            cb.Font = new Font(Fnt, 9.5f);
            cb.Size = new Size(140, 26);
            cb.Location = new Point(row.Width - 140, 13);
            cb.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cb.SelectedIndexChanged += (s, e) => { try { onChange(cb.SelectedIndex); } catch { } };
            row.Controls.Add(cb);

            parent.Controls.Add(row);
            return y + 56;
        }

        private int AddInfoRow(Panel parent, int y, string title, string desc)
        {
            var row = new Panel();
            row.Location = new Point(0, y);
            row.Size = new Size(parent.Width, 56);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
            };
            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 9f);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Size = new Size(300, 22);
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Location = new Point(0, 2);
            row.Controls.Add(lblT);
            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(500, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(0, 30);
            row.Controls.Add(lblD);
            parent.Controls.Add(row);
            return y + 60;
        }

        private int AddInfoRowWithLink(Panel parent, int y, string title, string desc, string linkText, Action onClick)
        {
            int newY = AddInfoRow(parent, y, title, desc);
            var link = new SoftLinkButton();
            link.Text = linkText;
            link.Font = new Font(Fnt, 9f, FontStyle.Bold);
            link.Size = new Size(60, 24);
            // 行高 56，link 高 24，垂直居中：(56-24)/2 = 16
            link.Location = new Point(parent.Width - 60, y + 16);
            link.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            link.Click += (s, e) => { try { onClick(); } catch { } };
            parent.Controls.Add(link);
            link.BringToFront();
            return newY;
        }

        /// <summary>行左边有图标 + 右边链接的通用信息行（用于 GitHub 等有 logo 的行）</summary>
        /// <summary>行左边有图标 + 右边链接的通用信息行（图标控件由调用方提供：IconBox / PictureBox 都可）</summary>
        private int AddInfoRowWithIconAndLink(Panel parent, int y, Control iconControl, string title, string desc, string linkText, Action onClick)
        {
            var row = new Panel();
            row.Location = new Point(0, y);
            row.Size = new Size(parent.Width, 56);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(240, 240, 243)))
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
            };

            // 左边图标：34×34，垂直居中于整行（行高 56 → y=(56-34)/2 = 11）
            iconControl.Size = new Size(34, 34);
            iconControl.Location = new Point(0, 11);
            row.Controls.Add(iconControl);

            int textX = 42;

            var lblT = new Label();
            lblT.Text = title;
            lblT.Font = new Font(Fnt, 9f);
            lblT.ForeColor = CInk;
            lblT.AutoSize = false;
            lblT.Size = new Size(240, 22);
            lblT.TextAlign = ContentAlignment.MiddleLeft;
            lblT.Location = new Point(textX, 2);
            row.Controls.Add(lblT);

            var lblD = new Label();
            lblD.Text = desc;
            lblD.Font = new Font(Fnt, 8f);
            lblD.ForeColor = CInk2;
            lblD.AutoSize = false;
            lblD.Size = new Size(400, 22);
            lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.Location = new Point(textX, 30);
            row.Controls.Add(lblD);

            var link = new SoftLinkButton();
            link.Text = linkText;
            link.Font = new Font(Fnt, 9f, FontStyle.Bold);
            link.Size = new Size(60, 24);
            link.Location = new Point(row.Width - 60, 16);
            link.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            link.Click += (s, e) => { try { onClick(); } catch { } };
            row.Controls.Add(link);

            parent.Controls.Add(row);
            return y + 60;
        }

        private Panel MakeToggleRow(int x, int y, int w, string title, bool init)
        {
            var row = new Panel();
            row.Location = new Point(x, y);
            row.Size = new Size(w, 30);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var lbl = new Label();
            lbl.Text = title;
            lbl.Font = new Font(Fnt, 9f);
            lbl.ForeColor = CInk;
            lbl.AutoSize = true;
            lbl.Location = new Point(0, 6);
            row.Controls.Add(lbl);
            var sw = new ToggleSwitch();
            sw.Checked = init;
            sw.Size = new Size(46, 26);
            sw.Location = new Point(w - 46, 2);
            sw.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(sw);
            return row;
        }

        private Panel NewPage()
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = CPanel;
            return p;
        }

        /// <summary>
        /// 统一 logo/图标构造：优先用内嵌 PNG 加 PictureBox(Zoom)，加载失败兜底到 IconBox 的自绘 NavIcon。
        /// 关于顶部品牌 logo 与 GitHub 行图标共用此逻辑。
        /// </summary>
        private static Control MakeIconControl(Bitmap preferredBitmap, NavIcon fallbackIcon, Color fallbackStroke, Color fallbackBg)
        {
            if (preferredBitmap != null)
            {
                var pic = new PictureBox();
                pic.Image = preferredBitmap;
                pic.SizeMode = PictureBoxSizeMode.Zoom;
                pic.BackColor = Color.Transparent;
                return pic;
            }
            return new IconBox(fallbackIcon, fallbackStroke, fallbackBg);
        }

        // ============================================================
        //   自定义控件
        // ============================================================
        private enum NavIcon { Grid, Memory, Battery, Sun, Gear, Heart, Flash, Activity, Cog }

        /// <summary>侧边栏导航按钮：图标 + 文字，选中态左侧蓝条 + 淡蓝底</summary>
        private class NavButton : Control
        {
            private bool _active;
            private readonly NavIcon _icon;
            public NavButton(NavIcon icon, string text)
            {
                _icon = icon;
                this.Text = text;
                this.DoubleBuffered = true;
                // 关键：Selectable=false 避免拿到焦点后画虚线焦点框
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Font = new Font(Fnt, 10f);
                this.Cursor = Cursors.Hand;
                this.BackColor = CPanel2;
                this.Margin = new Padding(0);
            }
            public bool IsActive
            {
                get { return _active; }
                set { _active = value; Invalidate(); }
            }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // 先用父背景色清底 —— 双缓冲区起始像素未定义，AA 边缘会与黑混色出杂边
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel2);
                using (var brBg = new SolidBrush(parentBg)) g.FillRectangle(brBg, this.ClientRectangle);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Color bg = CPanel2;
                Color fg = CInk2;
                if (_active) { bg = Color.FromArgb(235, 244, 255); fg = CBlue; }
                else if (this.ClientRectangle.Contains(this.PointToClient(Cursor.Position))) { bg = Color.FromArgb(0xF0, 0xF0, 0xF3); fg = CInk; }
                using (var br = new SolidBrush(bg))
                {
                    var rr = new Rectangle(2, 2, Width - 4, Height - 4);
                    using (var path = RoundedRect(rr, 8))
                        g.FillPath(br, path);
                }
                if (_active)
                {
                    using (var br = new SolidBrush(CBlue))
                        g.FillRectangle(br, 0, 6, 3, Height - 12);
                }
                DrawIcon(g, _icon, new Rectangle(14, (Height - 18) / 2, 18, 18), fg);
                TextRenderer.DrawText(g, this.Text, this.Font,
                    new Rectangle(42, 0, Width - 42, Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>iOS 风格开关：40x24（我们用 46x26 稍大以适应 Windows）</summary>
        private class ToggleSwitch : Control
        {
            private bool _checked;
            public event EventHandler CheckedChanged;
            public ToggleSwitch()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Size = new Size(46, 26);
                this.Cursor = Cursors.Hand;
            }
            public bool Checked
            {
                get { return _checked; }
                set { if (_checked != value) { _checked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
            }
            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Checked = !Checked; }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // 清父背景 —— 消除 AA 圆角边缘的黑/紫色杂边
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var brBg = new SolidBrush(parentBg)) g.FillRectangle(brBg, this.ClientRectangle);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var rr = new Rectangle(0, 0, Width - 1, Height - 1);
                Color track = _checked ? CGreen : Color.FromArgb(233, 233, 234);
                using (var br = new SolidBrush(track))
                using (var path = RoundedRect(rr, Height / 2))
                    g.FillPath(br, path);
                int knobD = Height - 4;
                int knobX = _checked ? Width - knobD - 3 : 3;
                using (var br = new SolidBrush(Color.White))
                    g.FillEllipse(br, knobX, 2, knobD, knobD);
                using (var pen = new Pen(Color.FromArgb(30, 0, 0, 0)))
                    g.DrawEllipse(pen, knobX, 2, knobD, knobD);
            }
        }

        /// <summary>
        /// 圆角卡片容器（Win11 快速设置面板风格）：
        /// - 卡片底色：浅灰 (249,250,252)
        /// - 边框：深灰 (200,200,208)，Inset 1.5px（笔画整体落在卡片内部）
        /// - 无 Region 剪裁——纯 GDI+ 绘制，避免 Region-vs-Pen 像素错位
        /// - Padding 保护边框：Dock 子控件不会覆盖边框像素
        /// </summary>
        private class RoundedCard : Panel
        {
            public int CornerRadius = 12;
            // 深灰接近黑色，与浅灰底 (245,246,249) 形成明显对比
            public Color BorderColor = Color.FromArgb(150, 150, 158);
            public RoundedCard()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                // 卡片底色：淡浅灰
                this.BackColor = Color.FromArgb(245, 246, 249);
                this.Padding = new Padding(2, 2, 2, 2);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // 父背景（先清空整个客户区，避免旧像素残留）
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg))
                    g.FillRectangle(br, this.ClientRectangle);
                // ====================================================
                //  双层同心填充 —— 业界标准做法
                //  1. 外层填充边框色，覆盖 (0..W, 0..H) 整个圆角区
                //  2. 内层填充卡片底色，内缩 1px（形成 1px 视觉边框）
                //  优点：完全没有 DrawPath 描边 → 没有 AA 双线、没有四角黑斑
                // ====================================================
                using (var pathOuter = RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius))
                using (var brOuter = new SolidBrush(BorderColor))
                    g.FillPath(brOuter, pathOuter);
                using (var pathInner = RoundedRect(new Rectangle(1, 1, Width - 2, Height - 2), CornerRadius - 1))
                using (var brInner = new SolidBrush(this.BackColor))
                    g.FillPath(brInner, pathInner);
                // 补弧线端点：双层填充在圆角弧线起止处有 1px GDI+ 渲染间隙，
                // 在弧线端点各补 1px BorderColor 像素覆盖间隙（不在角落画方块）
                using (var brCorner = new SolidBrush(BorderColor))
                {
                    int r = CornerRadius;
                    // 左上弧线端点：(0, r) 和 (r, 0)
                    g.FillRectangle(brCorner, 0, r, 1, 1);
                    g.FillRectangle(brCorner, r, 0, 1, 1);
                    // 右上弧线端点：(W-1, r) 和 (W-1-r, 0)
                    g.FillRectangle(brCorner, Width - 1, r, 1, 1);
                    g.FillRectangle(brCorner, Width - 1 - r, 0, 1, 1);
                    // 左下弧线端点：(0, H-1-r) 和 (r, H-1)
                    g.FillRectangle(brCorner, 0, Height - 1 - r, 1, 1);
                    g.FillRectangle(brCorner, r, Height - 1, 1, 1);
                    // 右下弧线端点：(W-1, H-1-r) 和 (W-1-r, H-1)
                    g.FillRectangle(brCorner, Width - 1, Height - 1 - r, 1, 1);
                    g.FillRectangle(brCorner, Width - 1 - r, Height - 1, 1, 1);
                }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                // 边框已经在 OnPaintBackground 用同心填充完成--这里不再 DrawPath 避免 AA 双线
            }
        }

        /// <summary>
        /// 支持透明背景色的 Panel。标准 Panel 不支持 BackColor=Transparent，
        /// 需通过 SetStyle 启用 SupportsTransparentBackColor 样式位。
        /// 诊断页的行容器用它，让子 Label 真正透明。
        /// </summary>
        private class TransparentPanel : Panel
        {
            public TransparentPanel()
            {
                this.SetStyle(ControlStyles.SupportsTransparentBackColor, true);
                this.BackColor = Color.FromArgb(245, 246, 249);   // 卡片底色，与 RoundedCard 一致
            }
        }

        /// <summary>
        /// 可折叠诊断卡片：继承 RoundedCard 的圆角双层填充画法，额外支持点击标题行
        /// 展开/收起内容区。收起时卡片高度收缩到标题行（46px），展开时恢复全部内容行。
        /// 标题行右侧显示 ▶/▼ 箭头 + 汇总状态标签。
        /// </summary>
        private class CollapsibleDiagCard : RoundedCard
        {
            private readonly Panel _header;
            private readonly Panel _body;
            private readonly Label _arrow;
            private readonly TagLabel _summaryTag;
            private bool _expanded;

            /// <summary>展开/收起时触发，让外层 FlowLayoutPanel 重新布局</summary>
            public event EventHandler ExpandedChanged;

            public CollapsibleDiagCard(string title, NavIcon icon, Color accent)
            {
                this.Margin = new Padding(0, 0, 0, 14);
                // 保留 RoundedCard 基类的 Padding=(2,2,2,2)：保护 1px 边框不被子控件覆盖，
                // 让 4 角圆角和描边完整可见（与概览页卡片一致）
                // 加深边框：从 (150,150,158) 加到 (120,120,130) 更明显
                this.BorderColor = Color.FromArgb(120, 120, 130);
                _expanded = false;   // 默认收起

                // === 标题行（可点击）===
                // 不用 Dock=Top——它会让 _header 覆盖圆角区域。
                // 改为绝对定位，X=CornerRadius 起始，避开左圆角；Width=W-2*CornerRadius 避开右圆角。
                // Gap 区域（0..12 和 W-12..W）由 RoundedCard 的背景色填充，与 _header 同色，视觉不可见。
                _header = new TransparentPanel();   // TransparentPanel 支持子 Label 的 BackColor=Transparent
                _header.Location = new Point(CornerRadius, 2);
                _header.Size = new Size(Math.Max(1, this.Width - CornerRadius * 2), 46);
                _header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                _header.Cursor = Cursors.Hand;
                _header.Click += (s, e) => Toggle();

                var ico = new IconBox(icon, accent, Color.Transparent);
                ico.Size = new Size(22, 22);
                ico.Location = new Point(16, 12);
                _header.Controls.Add(ico);

                var lbl = new Label();
                lbl.Text = title;
                lbl.Font = new Font(Fnt, 11f, FontStyle.Bold);
                lbl.ForeColor = CInk;
                lbl.AutoSize = false;
                lbl.Size = new Size(300, 46);
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                lbl.Location = new Point(46, 0);
                lbl.BackColor = Color.Transparent;
                lbl.Cursor = Cursors.Hand;
                lbl.Click += (s, e) => Toggle();
                _header.Controls.Add(lbl);

                // 汇总状态标签（右侧，左移避免被滚动条/圆角遮挡）
                _summaryTag = new TagLabel();
                _summaryTag.Text = "";
                _summaryTag.Accent = CInk3;
                _summaryTag.Font = new Font(Fnt, 8f, FontStyle.Bold);
                _summaryTag.Size = new Size(90, 22);
                _summaryTag.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                _summaryTag.Location = new Point(_header.Width - 90 - 40, 12);
                _summaryTag.BackColor = Color.FromArgb(245, 246, 249);   // 卡片底色（TagLabel 不支持透明）
                _header.Controls.Add(_summaryTag);

                // 箭头（标签背景色右边界 与 卡片右侧描边 正中间）
                // 标签背景右边界(卡片坐标)=12+(W-24)-41=W-53，卡片右侧描边=W-1，间距52px，居中=W-27，转_header坐标=W-25
                _arrow = new Label();
                _arrow.Text = "▶";
                _arrow.Font = new Font(Fnt, 11f);
                _arrow.ForeColor = CInk2;
                _arrow.AutoSize = false;
                _arrow.Size = new Size(20, 46);
                _arrow.TextAlign = ContentAlignment.MiddleCenter;
                _arrow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                _arrow.Location = new Point(_header.Width - 25, 0);
                _arrow.BackColor = Color.Transparent;
                _arrow.Cursor = Cursors.Hand;
                _arrow.Click += (s, e) => Toggle();
                _header.Controls.Add(_arrow);

                _header.Resize += (s, e) =>
                {
                    _summaryTag.Location = new Point(_header.Width - 90 - 40, 12);
                    _arrow.Location = new Point(_header.Width - 25, 0);
                };
                this.Controls.Add(_header);

                // === 内容区（收起时 Visible=false）===
                _body = new TransparentPanel();   // TransparentPanel 支持子 Label 的 BackColor=Transparent
                _body.Location = new Point(CornerRadius, 48);   // X=CornerRadius 避开左圆角
                _body.Size = new Size(Math.Max(1, this.Width - CornerRadius * 2), 0);   // 避开右圆角
                _body.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                _body.Visible = false;   // 默认收起
                this.Controls.Add(_body);

                UpdateHeight();
            }

            /// <summary>内容区容器，外层用 AddDiagnosticRow 往里加行</summary>
            public Panel Body { get { return _body; } }

            /// <summary>设置汇总状态标签（如「3项就绪」「需关注」）</summary>
            public void SetSummary(string text, Color accent)
            {
                _summaryTag.Text = text;
                _summaryTag.Accent = accent;
                _summaryTag.Invalidate();
            }

            public bool IsExpanded { get { return _expanded; } }

            public void Toggle()
            {
                _expanded = !_expanded;
                _arrow.Text = _expanded ? "▼" : "▶";
                _body.Visible = _expanded;
                UpdateHeight();
                if (ExpandedChanged != null) ExpandedChanged(this, EventArgs.Empty);
            }

            /// <summary>根据展开状态和内容区高度计算卡片总高度</summary>
            private void UpdateHeight()
            {
                // 计算 _body 内容总高度（所有行的最低部）
                int bodyH = 0;
                foreach (Control c in _body.Controls)
                {
                    int bottom = c.Location.Y + c.Height;
                    if (bottom > bodyH) bodyH = bottom;
                }
                _body.Height = bodyH;

                // Padding=(2,2,2,2) -> 上下各 2px 边框保护
                if (_expanded)
                {
                    // 展开：Padding.Top(2) + header(46) + body + 6 呼吸 + Padding.Bottom(2)
                    this.Height = 2 + 46 + bodyH + 6 + 2;
                }
                else
                {
                    // 收起：Padding.Top(2) + header(46) + Padding.Bottom(2)
                    this.Height = 2 + 46 + 2;
                }
                this.Invalidate();   // 触发 OnPaint 重画
            }

            /// <summary>内容行变化后调用，让卡片高度跟随内容更新</summary>
            public void RefreshHeight()
            {
                UpdateHeight();
            }
        }

        /// <summary>数值条（圆角）</summary>
        private class BarStrip : Control
        {
            private int _value;
            public Color BarColor = CGreen;
            public int Value { get { return _value; } set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
            public BarStrip()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                this.Height = 6;
            }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);
                var rTrack = new Rectangle(0, 0, Width, Height);
                using (var path = RoundedRect(rTrack, Height / 2))
                using (var br = new SolidBrush(Color.FromArgb(233, 233, 236)))
                    g.FillPath(br, path);
                if (_value > 0)
                {
                    int w = Math.Max(Height, (int)(Width * _value / 100.0));
                    var rF = new Rectangle(0, 0, w, Height);
                    using (var path = RoundedRect(rF, Height / 2))
                    using (var br = new SolidBrush(BarColor))
                        g.FillPath(br, path);
                }
            }
        }

        /// <summary>
        /// 自绘水平滑条（Win11 快速设置面板同款）：圆形拇指 + 圆角轨道。
        /// 完全用 GDI+，不依赖 Win32 TrackBar 原生 HWND，因此不会突破卡片圆角边界。
        /// </summary>
        private class RoundSlider : Control
        {
            private int _min = 0, _max = 100, _value = 0;
            private bool _dragging;
            public Color AccentColor = Color.FromArgb(10, 122, 255);
            public event EventHandler ValueChanged;
            public int Minimum { get { return _min; } set { _min = value; ClampValue(); Invalidate(); } }
            public int Maximum { get { return _max; } set { _max = value; ClampValue(); Invalidate(); } }
            public int Value
            {
                get { return _value; }
                set
                {
                    int v = Math.Max(_min, Math.Min(_max, value));
                    if (v != _value)
                    {
                        _value = v;
                        Invalidate();
                        if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                    }
                }
            }
            private void ClampValue() { _value = Math.Max(_min, Math.Min(_max, _value)); }
            public RoundSlider()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Height = 20;
                this.Cursor = Cursors.Hand;
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left) { _dragging = true; UpdateFromMouse(e.X); }
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragging) UpdateFromMouse(e.X);
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragging = false;
            }
            private void UpdateFromMouse(int x)
            {
                int thumbR = Height / 2;
                int trackX = thumbR;
                int trackW = Math.Max(1, Width - thumbR * 2);
                double t = (x - trackX) / (double)trackW;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                Value = _min + (int)Math.Round(t * (_max - _min));
            }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);

                int thumbR = Height / 2;       // 拇指半径
                int trackH = 4;                 // 轨道高度
                int trackY = (Height - trackH) / 2;
                int trackL = thumbR;
                int trackW = Math.Max(1, Width - thumbR * 2);

                // —— 灰轨道 ——
                var trackRect = new Rectangle(trackL, trackY, trackW, trackH);
                using (var path = RoundedRect(trackRect, trackH / 2))
                using (var br = new SolidBrush(Color.FromArgb(210, 210, 215)))
                    g.FillPath(br, path);

                // —— 进度色块 ——
                int range = Math.Max(1, _max - _min);
                int fillW = (int)Math.Round((double)(_value - _min) / range * trackW);
                if (fillW > 0)
                {
                    var fillRect = new Rectangle(trackL, trackY, fillW, trackH);
                    using (var path = RoundedRect(fillRect, trackH / 2))
                    using (var br = new SolidBrush(AccentColor))
                        g.FillPath(br, path);
                }

                // —— 圆形拇指（白底 + accent 描边 + 中心 accent 圆点）——
                int cx = trackL + fillW;
                int cy = Height / 2;
                int r = thumbR - 1;
                // 外圈白底
                using (var br = new SolidBrush(Color.White))
                    g.FillEllipse(br, cx - r, cy - r, r * 2, r * 2);
                // 描边
                using (var pen = new Pen(Color.FromArgb(180, 180, 190), 1.2f))
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                // 中心 accent 点
                int cr = r - 4;
                if (cr > 0)
                {
                    using (var br = new SolidBrush(AccentColor))
                        g.FillEllipse(br, cx - cr, cy - cr, cr * 2, cr * 2);
                }
            }
        }

        /// <summary>圆角图标框：背景色块 + 图标笔画色</summary>
        private class IconBox : Control
        {
            private readonly NavIcon _icon;
            private readonly Color _stroke;
            private readonly Color _bg;
            public IconBox(NavIcon icon, Color stroke, Color bg)
            {
                _icon = icon; _stroke = stroke; _bg = bg;
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // ============================================================
                // 关键：先用非抗锯齿 + None 模式填父背景色，避免 FillRectangle 在
                // AntiAlias + HighQuality 组合下对矩形边缘做半像素混色，
                // 造成图标周边出现极细的横竖灰线。
                // ============================================================
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg))
                    g.FillRectangle(br, this.ClientRectangle);

                // 切换到高质量模式绘制图标本身
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                if (_bg.A != 0)
                {
                    var rr = new Rectangle(0, 0, Width - 1, Height - 1);
                    using (var path = RoundedRect(rr, 7))
                    using (var br = new SolidBrush(_bg))
                        g.FillPath(br, path);
                    int pad = Math.Max(4, Width / 5);
                    DrawIcon(g, _icon, new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2), _stroke);
                }
                else
                {
                    // 无底：图标铺满整个控件（-2 内缩为线条留边距，避免圆角剪切）
                    var iconR = new Rectangle(1, 1, Width - 2, Height - 2);
                    DrawIcon(g, _icon, iconR, _stroke);
                }
            }
        }

        /// <summary>充电模式卡片</summary>
        private class ModeCard : Panel
        {
            private bool _selected;
            private string _mn, _mp, _md;
            private Color _accent;
            private Color _icoBg;
            private NavIcon _icon;
            public ModeCard()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                this.Cursor = Cursors.Hand;
            }
            public void Setup(string name, string percent, string desc, NavIcon icon, Color accent, Color icoBg)
            {
                _mn = name; _mp = percent; _md = desc; _icon = icon; _accent = accent; _icoBg = icoBg;
                Invalidate();
            }
            public bool IsSelected { get { return _selected; } set { _selected = value; Invalidate(); } }
            /// <summary>灰显态：不支持充电控制时置 true，OnPaint 用浅灰降饱和</summary>
            public bool IsDimmed { get { return _dimmed; } set { _dimmed = value; Invalidate(); } }
            private bool _dimmed;
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);

                Color bg = _selected ? Color.FromArgb(232, 242, 255) : Color.FromArgb(245, 246, 249);
                Color border = _selected ? CBlue : Color.FromArgb(150, 150, 158);
                int borderPx = _selected ? 2 : 1;
                // 灰显态：不支持充电控制时降饱和，让用户视觉上感知「不可用」
                if (_dimmed && !_selected)
                {
                    bg = Color.FromArgb(240, 240, 243);
                    border = Color.FromArgb(220, 220, 226);
                }
                // 双层同心填充：外层边框色，内层卡片色（内缩 borderPx）
                using (var pathOuter = RoundedRect(new Rectangle(0, 0, Width, Height), 10))
                using (var brOuter = new SolidBrush(border))
                    g.FillPath(brOuter, pathOuter);
                using (var pathInner = RoundedRect(new Rectangle(borderPx, borderPx, Width - borderPx * 2, Height - borderPx * 2), 10 - borderPx))
                using (var brInner = new SolidBrush(bg))
                    g.FillPath(brInner, pathInner);

                // === 内容排布（顶 12、底 12）===
                //   Icon: y=12..38, 名称: y=14..38 (与图标视觉居中)
                //   大数字: y=42..76
                //   描述贴底: y = Height - 12 - 16 = Height - 28
                var icoR = new Rectangle(12, 12, 26, 26);
                Color accentColor = _dimmed ? CInk3 : _accent;
                Color nameColor   = _dimmed ? CInk3 : CInk;
                Color descColor   = _dimmed ? CInk3 : CInk2;
                using (var path = RoundedRect(icoR, 7))
                using (var brIco = new SolidBrush(_dimmed ? Color.FromArgb(235, 235, 238) : _icoBg))
                    g.FillPath(brIco, path);
                DrawIcon(g, _icon, new Rectangle(icoR.X + 5, icoR.Y + 5, icoR.Width - 10, icoR.Height - 10), accentColor);
                using (var fName = new Font(Fnt, 10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _mn, fName, new Point(46, 14), nameColor, TextFormatFlags.NoPadding);

                using (var fBig = new Font(Fnt, 14f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _mp, fBig, new Point(12, 44), accentColor, TextFormatFlags.NoPadding);

                using (var fDesc = new Font(Fnt, 8f))
                    TextRenderer.DrawText(g, _md, fDesc, new Point(12, Height - 26), descColor, TextFormatFlags.NoPadding);

                // 选中勾
                if (_selected)
                {
                    var tick = new Rectangle(Width - 26, 8, 18, 18);
                    using (var br = new SolidBrush(CBlue))
                        g.FillEllipse(br, tick);
                    using (var pen = new Pen(Color.White, 1.8f))
                    {
                        g.DrawLines(pen, new PointF[] {
                            new PointF(tick.X + 4.5f, tick.Y + 9),
                            new PointF(tick.X + 8, tick.Y + 12.5f),
                            new PointF(tick.X + 13.5f, tick.Y + 6)
                        });
                    }
                }
            }
        }

        /// <summary>圆环图（电池健康）</summary>
        private class RingChart : Control
        {
            public int Percent = 80;
            public Color RingColor = CGreen;
            public RingChart()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);
                int sz = Math.Min(Width, Height) - 8;
                var r = new Rectangle((Width - sz) / 2, (Height - sz) / 2, sz, sz);
                using (var pen = new Pen(Color.FromArgb(238, 238, 238), 4f))
                    g.DrawEllipse(pen, r);
                using (var pen = new Pen(RingColor, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, r, -90, 360f * Percent / 100f);
                using (var fPct = new Font(Fnt, 10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Percent + "%", fPct, this.ClientRectangle, CInk,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>设置页的子页签按钮</summary>
        private class SubTabButton : Control
        {
            private bool _active;
            public SubTabButton()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Cursor = Cursors.Hand;
            }
            public bool IsActive { get { return _active; } set { _active = value; Invalidate(); } }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);
                var rr = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                Color bg = _active ? CBlue : CPanel;
                Color border = _active ? CBlue : Color.FromArgb(215, 215, 222);
                Color fg = _active ? Color.White : CInk2;
                using (var path = RoundedRectF(rr, 8))
                using (var br = new SolidBrush(bg))
                    g.FillPath(br, path);
                using (var path = RoundedRectF(rr, 8))
                using (var pen = new Pen(border, 1.2f) { Alignment = PenAlignment.Center })
                    g.DrawPath(pen, path);
                TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>浅蓝背景的行内链接按钮</summary>
        private class SoftLinkButton : Control
        {
            private bool _hover;
            public SoftLinkButton()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);
                var rr = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                int alpha = _hover ? 40 : 26;
                using (var path = RoundedRectF(rr, 6))
                using (var br = new SolidBrush(Color.FromArgb(alpha, 10, 122, 255)))
                    g.FillPath(br, path);
                TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, CBlue,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>普通白底细边圆角按钮</summary>
        private class FlatBorderedButton : Control
        {
            private bool _hover;
            public FlatBorderedButton()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.SetStyle(ControlStyles.Selectable, false);
                this.TabStop = false;
                this.Cursor = Cursors.Hand;
                this.ForeColor = CInk;
                this.BackColor = CPanel;
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var parentBg = (this.Parent != null ? this.Parent.BackColor : CPanel);
                using (var br = new SolidBrush(parentBg)) g.FillRectangle(br, this.ClientRectangle);
                var rr = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                using (var path = RoundedRectF(rr, 8))
                using (var br = new SolidBrush(_hover ? Color.FromArgb(245, 247, 250) : CPanel))
                    g.FillPath(br, path);
                using (var path = RoundedRectF(rr, 8))
                using (var pen = new Pen(Color.FromArgb(215, 215, 222), 1.2f) { Alignment = PenAlignment.Center })
                    g.DrawPath(pen, path);
                TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>关于页彩色标签</summary>
        private class TagLabel : Control
        {
            public Color Accent = CBlue;
            public TagLabel()
            {
                this.DoubleBuffered = true;
                // 不设 SupportsTransparentBackColor -- 避免透明路径导致下层文字透出
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.BackColor = Color.FromArgb(245, 246, 249);   // 卡片底色
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                // 先填背景（卡片底色，不透明）
                using (var brBg = new SolidBrush(this.BackColor))
                    g.FillRectangle(brBg, this.ClientRectangle);
                // 画圆角药丸
                var rr = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundedRect(rr, Height / 2))
                using (var br = new SolidBrush(Color.FromArgb(45, Accent.R, Accent.G, Accent.B)))
                    g.FillPath(br, path);
                TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // 简易选择/数值对话框
        private class PickerDialog : Form
        {
            public int SelectedIndex;
            public PickerDialog(string title, string[] items, int init)
            {
                this.Text = title;
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MinimizeBox = false; this.MaximizeBox = false;
                this.StartPosition = FormStartPosition.CenterParent;
                this.Size = new Size(360, 200);
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(20, 22), Size = new Size(320, 26) };
                cb.Items.AddRange(items);
                cb.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, init));
                this.Controls.Add(cb);
                var ok = new Button { Text = "确定", Location = new Point(180, 120), Size = new Size(70, 28) };
                var cancel = new Button { Text = "取消", Location = new Point(260, 120), Size = new Size(70, 28) };
                ok.Click += (s, e) => { SelectedIndex = cb.SelectedIndex; DialogResult = DialogResult.OK; Close(); };
                cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                this.Controls.Add(ok); this.Controls.Add(cancel);
            }
        }
        private class NumberDialog : Form
        {
            public int Value;
            public NumberDialog(string title, string prompt, int init, int min, int max)
            {
                this.Text = title;
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MinimizeBox = false; this.MaximizeBox = false;
                this.StartPosition = FormStartPosition.CenterParent;
                this.Size = new Size(360, 200);
                var lbl = new Label { Text = prompt, Location = new Point(20, 20), AutoSize = true };
                this.Controls.Add(lbl);
                var nud = new NumericUpDown { Minimum = min, Maximum = max, Value = init, Location = new Point(20, 60), Size = new Size(100, 26) };
                this.Controls.Add(nud);
                var ok = new Button { Text = "确定", Location = new Point(180, 120), Size = new Size(70, 28) };
                var cancel = new Button { Text = "取消", Location = new Point(260, 120), Size = new Size(70, 28) };
                ok.Click += (s, e) => { Value = (int)nud.Value; DialogResult = DialogResult.OK; Close(); };
                cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                this.Controls.Add(ok); this.Controls.Add(cancel);
            }
        }

        // ============================================================
        //   工具方法
        // ============================================================
        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>浮点版圆角矩形 —— 用于 1px 边框对齐（配合 rect + 0.5 偏移，避免线被拆到两行像素）。</summary>
        private static GraphicsPath RoundedRectF(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>极坐标 → 屏幕像素（用于齿轮等圆周分布的图标）。</summary>
        private static PointF PolarPt(float cx, float cy, float r, float degrees, float ux, float uy)
        {
            double rad = degrees * Math.PI / 180.0;
            return new PointF(
                cx + (float)Math.Cos(rad) * r * ux,
                cy + (float)Math.Sin(rad) * r * uy);
        }

        private static void DrawIcon(Graphics g, NavIcon icon, Rectangle r, Color stroke)
        {
            // 保存 & 强化抗锯齿参数（图标线条精细，需要 HighQuality 像素偏移防止半像素模糊）
            var prevSmooth = g.SmoothingMode;
            var prevOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 线宽随图标尺寸自适应：24px 时 1.6f，更小则线性缩放但不低于 1.2f
            float px = Math.Max(1.2f, Math.Min(r.Width, r.Height) / 15f);
            using (var pen = new Pen(stroke, px) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round, MiterLimit = 1f })
            {
                // 归一化坐标助手：把 lucide 24×24 viewBox 映射到目标矩形
                float ux = r.Width / 24f, uy = r.Height / 24f;
                Func<float, float, PointF> P = (x, y) => new PointF(r.X + x * ux, r.Y + y * uy);

                switch (icon)
                {
                    case NavIcon.Grid:
                        {
                            int gap = 2;
                            int w = (r.Width - gap) / 2;
                            int h = (r.Height - gap) / 2;
                            g.DrawRectangle(pen, r.X, r.Y, w, h);
                            g.DrawRectangle(pen, r.X + w + gap, r.Y, w, h);
                            g.DrawRectangle(pen, r.X, r.Y + h + gap, w, h);
                            g.DrawRectangle(pen, r.X + w + gap, r.Y + h + gap, w, h);
                            break;
                        }
                    case NavIcon.Memory:
                        {
                            // lucide "cpu"：外框 rect(4,4,16,16) rx=2 + 内框 rect(9,9,6,6) + 8 根引脚
                            var outer = new RectangleF(r.X + 4 * ux, r.Y + 4 * uy, 16 * ux, 16 * uy);
                            var inner = new RectangleF(r.X + 9 * ux, r.Y + 9 * uy, 6 * ux, 6 * uy);
                            using (var pOuter = RoundedRectF(outer, 2f * Math.Min(ux, uy)))
                                g.DrawPath(pen, pOuter);
                            g.DrawRectangle(pen, inner.X, inner.Y, inner.Width, inner.Height);
                            // 顶部 2 引脚
                            g.DrawLine(pen, P(9, 1), P(9, 4));
                            g.DrawLine(pen, P(15, 1), P(15, 4));
                            // 底部 2 引脚
                            g.DrawLine(pen, P(9, 20), P(9, 23));
                            g.DrawLine(pen, P(15, 20), P(15, 23));
                            // 左侧 2 引脚
                            g.DrawLine(pen, P(1, 9), P(4, 9));
                            g.DrawLine(pen, P(1, 14), P(4, 14));
                            // 右侧 2 引脚
                            g.DrawLine(pen, P(20, 9), P(23, 9));
                            g.DrawLine(pen, P(20, 14), P(23, 14));
                            break;
                        }
                    case NavIcon.Battery:
                        {
                            // lucide "battery"：主体 rect(1,6,18,12) rx=2 + 右端子 line(23,11)-(23,13)
                            // 但由于我们要留边距，稍微内缩：主体用 (2,7,18,10)，端子在 21
                            var body = new RectangleF(r.X + 2 * ux, r.Y + 7 * uy, 18 * ux, 10 * uy);
                            using (var pBody = RoundedRectF(body, 2f * Math.Min(ux, uy)))
                                g.DrawPath(pen, pBody);
                            g.DrawLine(pen, P(22, 11), P(22, 13));
                            break;
                        }
                    case NavIcon.Sun:
                        {
                            // lucide "sun"：中心圆 r=4 + 8 根光线（正交 4 + 对角 4）
                            var solar = new RectangleF(P(8, 8).X, P(8, 8).Y, 8 * ux, 8 * uy);
                            g.DrawEllipse(pen, solar.X, solar.Y, solar.Width, solar.Height);
                            // 正交 4 根
                            g.DrawLine(pen, P(12, 1),  P(12, 3));
                            g.DrawLine(pen, P(12, 21), P(12, 23));
                            g.DrawLine(pen, P(1, 12),  P(3, 12));
                            g.DrawLine(pen, P(21, 12), P(23, 12));
                            // 对角 4 根（lucide 精确值 4.22 / 19.78 / 5.64 / 18.36）
                            g.DrawLine(pen, P(4.22f, 4.22f),   P(5.64f, 5.64f));
                            g.DrawLine(pen, P(18.36f, 18.36f), P(19.78f, 19.78f));
                            g.DrawLine(pen, P(4.22f, 19.78f),  P(5.64f, 18.36f));
                            g.DrawLine(pen, P(18.36f, 5.64f),  P(19.78f, 4.22f));
                            break;
                        }
                    case NavIcon.Gear:
                        {
                            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                            int rad = Math.Min(r.Width, r.Height) / 4;
                            g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                            for (int i = 0; i < 8; i++)
                            {
                                double a = i * Math.PI / 4;
                                int x1 = cx + (int)(Math.Cos(a) * (rad + 1));
                                int y1 = cy + (int)(Math.Sin(a) * (rad + 1));
                                int x2 = cx + (int)(Math.Cos(a) * (rad + 4));
                                int y2 = cy + (int)(Math.Sin(a) * (rad + 4));
                                g.DrawLine(pen, x1, y1, x2, y2);
                            }
                            break;
                        }
                    case NavIcon.Heart:
                        {
                            // lucide "heart"：底尖 (12,21.23) 收拢，上方两个对称圆弧在 (12, 5.67) 处形成凹陷。
                            // 用 4 段三次贝塞尔近似两条对称弧线。
                            using (var path = new GraphicsPath())
                            {
                                var bottom = P(12, 21.23f);
                                var dip    = P(12, 5.67f);
                                var lTop   = P(6.94f, 3.61f);
                                var rTop   = P(17.06f, 3.61f);

                                // 左半：底 → 左弧顶 → 中心凹陷
                                path.AddBezier(bottom,
                                    P(2f, 16f),
                                    P(1.5f, 8.5f),
                                    lTop);
                                path.AddBezier(lTop,
                                    P(9.5f, 1.5f),
                                    P(12f, 3.5f),
                                    dip);
                                // 右半：中心凹陷 → 右弧顶 → 底
                                path.AddBezier(dip,
                                    P(12f, 3.5f),
                                    P(14.5f, 1.5f),
                                    rTop);
                                path.AddBezier(rTop,
                                    P(22.5f, 8.5f),
                                    P(22f, 16f),
                                    bottom);
                                g.DrawPath(pen, path);
                            }
                            break;
                        }
                    case NavIcon.Flash:
                        {
                            var pts = new Point[] {
                                new Point(r.X + (int)(r.Width * 0.55f), r.Y),
                                new Point(r.X + (int)(r.Width * 0.25f), r.Y + (int)(r.Height * 0.55f)),
                                new Point(r.X + (int)(r.Width * 0.5f),  r.Y + (int)(r.Height * 0.55f)),
                                new Point(r.X + (int)(r.Width * 0.35f), r.Bottom),
                                new Point(r.X + (int)(r.Width * 0.75f), r.Y + (int)(r.Height * 0.45f)),
                                new Point(r.X + (int)(r.Width * 0.5f),  r.Y + (int)(r.Height * 0.45f)),
                                new Point(r.X + (int)(r.Width * 0.55f), r.Y),
                            };
                            using (var fill = new SolidBrush(Color.FromArgb(255, 214, 10)))
                                g.FillPolygon(fill, pts);
                            g.DrawPolygon(pen, pts);
                            break;
                        }
                    case NavIcon.Activity:
                        {
                            // lucide "activity"：心电图折线 M22 12 h-4 l-3 9 L9 3 l-3 9 H2
                            // 左右各留 2px 边距，落到 24 视图坐标：(2,12)-(6,12)-(9,3)-(15,21)-(18,12)-(22,12)
                            var pts = new PointF[] {
                                P(2f, 12f),
                                P(6f, 12f),
                                P(9f, 3f),
                                P(15f, 21f),
                                P(18f, 12f),
                                P(22f, 12f),
                            };
                            g.DrawLines(pen, pts);
                            break;
                        }
                    case NavIcon.Cog:
                        {
                            // lucide "cog"：中心小圆 r=3 + 8 个齿状凸起 + 外圈"齿包"环
                            // 精度控制在 24 viewBox，画法：
                            //   1) 中心圆 r=3（8..8..14..14 的圆）
                            //   2) 外圈 12 个齿：用 12 条短径向线（4..3 长）分布在 r=7..r=10
                            //      为了贴近你截图那种"齿包"感，齿是外扩的凸包而不是简单短线；
                            //      这里用 12 段小圆弧 + 8 段直线切齿的近似实现。
                            //   最终采用 lucide 官方 24 齿路径的关键控制点近似：
                            //   在 r=8 的圆基础上，每 30° 向外凸出 2px 形成 12 齿。
                            var cx = P(12, 12).X;
                            var cy = P(12, 12).Y;
                            // 中心圆（小）
                            g.DrawEllipse(pen,
                                cx - 3f * ux, cy - 3f * uy,
                                6f * ux, 6f * uy);
                            // 12 齿外圈：交替 r1 / r2 采样 24 个点连成波浪路径
                            using (var toothPath = new GraphicsPath())
                            {
                                const int teeth = 8;
                                const int seg = teeth * 2; // 8 齿：16 个采样点（凸/凹交替）
                                float rOut = 10.5f;   // 齿顶
                                float rIn  = 7.8f;    // 齿谷
                                float toothHalfDeg = 12f; // 齿顶弧宽度的一半（角度）
                                var pts2 = new PointF[seg * 2];
                                for (int i = 0; i < teeth; i++)
                                {
                                    // 每齿由 4 个点组成：谷入 → 齿肩上 → 齿肩下 → 谷出
                                    float centerAng = i * (360f / teeth) - 90f;
                                    float aTopA = centerAng - toothHalfDeg;
                                    float aTopB = centerAng + toothHalfDeg;
                                    float aValA = centerAng - (360f / teeth / 2f) + 4f;
                                    float aValB = centerAng + (360f / teeth / 2f) - 4f;
                                    pts2[i * 4 + 0] = PolarPt(cx, cy, rIn,  aValA, ux, uy);
                                    pts2[i * 4 + 1] = PolarPt(cx, cy, rOut, aTopA, ux, uy);
                                    pts2[i * 4 + 2] = PolarPt(cx, cy, rOut, aTopB, ux, uy);
                                    pts2[i * 4 + 3] = PolarPt(cx, cy, rIn,  aValB, ux, uy);
                                }
                                toothPath.AddClosedCurve(pts2, 0.35f);
                                g.DrawPath(pen, toothPath);
                            }
                            break;
                        }
                }
            }

            // 恢复调用方的 Graphics 状态
            g.SmoothingMode = prevSmooth;
            g.PixelOffsetMode = prevOffset;
        }
    }
}
