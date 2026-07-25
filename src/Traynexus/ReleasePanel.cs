using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Traynexus
{
    /// <summary>
    /// 释放面板 -- 合并了原来的"干跑预览"和"保护进程名单"两个窗口。
    ///
    /// 布局:
    ///   顶部工具栏: 过滤 / 只看未保护 / 从文件重载 / 保存持久化 / 刷新
    ///   中间列表  : 复选列 + 进程名 + PID + 工作集 + 状态
    ///   底部第1行 : 完整统计信息
    ///   底部第2行 : [ 关闭 ]  [ 立即释放 N ]
    ///
    /// 交互:
    ///   - 单击行任意位置切换勾选（首列复选框同步）
    ///   - 单击列头排序
    ///   - 无键盘快捷键，避免与其他软件冲突
    /// </summary>
    public class ReleasePanel : Form
    {
        private readonly Settings _settings;
        private readonly System.Windows.Forms.Timer _titleTimer;

        // 顶部
        private readonly TextBox _filter;
        private readonly CheckBox _onlyUnprotected;
        private readonly Button _btnReload;
        private readonly Button _btnPersist;
        private readonly Button _btnManagePersisted;
        private readonly Button _btnRefresh;

        // 中间
        private readonly ListView _list;
        private readonly Label _loading;

        // 底部
        private readonly Label _stats;
        private readonly Button _btnClose;
        private readonly Button _btnExec;

        // 状态
        private List<ProcRow> _allRows = new List<ProcRow>();
        private int _sortColumn = 3;               // 默认按 WS 降序
        private SortOrder _sortOrder = SortOrder.Descending;
        private Icon _ownedIcon;
        private volatile bool _loaded;
        private bool _suppressItemCheck;

        public ReleasePanel(Settings settings)
        {
            _settings = settings;

            this.Text = "Traynexus - 释放面板";
            this.Size = new Size(880, 620);
            this.MinimumSize = new Size(680, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ShowInTaskbar = true;

            // ==== 顶部工具栏 ====
            var top = new TableLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 44;
            top.ColumnCount = 7;
            top.Padding = new Padding(8, 8, 8, 6);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var lblSearch = new Label();
            lblSearch.Text = "过滤:";
            lblSearch.AutoSize = true;
            lblSearch.Anchor = AnchorStyles.Left;
            lblSearch.Margin = new Padding(0, 6, 6, 0);
            top.Controls.Add(lblSearch, 0, 0);

            _filter = new TextBox();
            _filter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _filter.Margin = new Padding(0, 3, 8, 3);
            _filter.TextChanged += (s, e) => ApplyFilterAndBind();
            top.Controls.Add(_filter, 1, 0);

            _onlyUnprotected = new CheckBox();
            _onlyUnprotected.Text = "只看未保护";
            _onlyUnprotected.AutoSize = true;
            _onlyUnprotected.Anchor = AnchorStyles.Left;
            _onlyUnprotected.Margin = new Padding(0, 6, 8, 0);
            _onlyUnprotected.CheckedChanged += (s, e) => ApplyFilterAndBind();
            top.Controls.Add(_onlyUnprotected, 2, 0);

            _btnReload = new Button();
            _btnReload.Text = "从文件重载";
            _btnReload.AutoSize = true;
            _btnReload.Margin = new Padding(0, 0, 4, 0);
            _btnReload.Click += (s, e) =>
            {
                // P1-1 修复③：加锁保护 SessionWhitelist.Clear
                lock (_settings.WhitelistLock)
                {
                    _settings.SessionWhitelist.Clear();
                }
                _settings.ReloadWhitelist();
                RefreshProtectionFlags();
                ApplyFilterAndBind();
            };
            top.Controls.Add(_btnReload, 3, 0);

            _btnPersist = new Button();
            _btnPersist.Text = "💾 保存持久化";
            _btnPersist.AutoSize = true;
            _btnPersist.Margin = new Padding(0, 0, 4, 0);
            _btnPersist.Click += (s, e) =>
            {
                bool ok = _settings.PersistWhitelist();
                ApplyFilterAndBind();
                MessageBox.Show(this,
                    ok ? "保存成功" : "保存失败（文件可能被占用）",
                    "保护名单",
                    MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            top.Controls.Add(_btnPersist, 4, 0);

            _btnManagePersisted = new Button();
            _btnManagePersisted.Text = "🗂 管理已持久化";
            _btnManagePersisted.AutoSize = true;
            _btnManagePersisted.Margin = new Padding(0, 0, 4, 0);
            _btnManagePersisted.Click += (s, e) => ShowPersistedManager();
            top.Controls.Add(_btnManagePersisted, 5, 0);

            _btnRefresh = new Button();
            _btnRefresh.Text = "🔄 刷新";
            _btnRefresh.AutoSize = true;
            _btnRefresh.Margin = new Padding(0);
            _btnRefresh.Click += (s, e) => LoadProcessesAsync();
            top.Controls.Add(_btnRefresh, 6, 0);

            // ==== 中间列表 ====
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.MultiSelect = false;   // 无键盘快捷键，多选价值不大
            _list.HideSelection = false;
            _list.Columns.Add("", 32);              // 复选列表头留空
            _list.Columns.Add("进程名", 240);
            _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
            _list.Columns.Add("工作集", 110, HorizontalAlignment.Right);
            _list.Columns.Add("状态", 280);
            _list.ColumnClick += (s, e) =>
            {
                if (e.Column == _sortColumn)
                    _sortOrder = _sortOrder == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
                else
                {
                    _sortColumn = e.Column;
                    _sortOrder = (e.Column == 1 || e.Column == 4) ? SortOrder.Ascending : SortOrder.Descending;
                }
                ApplyFilterAndBind();
            };
            _list.ItemCheck += List_ItemCheck;
            _list.MouseClick += List_MouseClick;

            _loading = new Label();
            _loading.Text = "读取进程中...";
            _loading.Font = Fonts.S11;
            _loading.ForeColor = Color.DimGray;
            _loading.BackColor = SystemColors.Window;
            _loading.TextAlign = ContentAlignment.MiddleCenter;
            _loading.Dock = DockStyle.Fill;

            // ==== 底部 ====
            var bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 76;
            bottom.ColumnCount = 1;
            bottom.RowCount = 2;
            bottom.Padding = new Padding(8, 6, 8, 8);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 第一行：信息
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 第二行：按钮

            _stats = new Label();
            _stats.Dock = DockStyle.Fill;
            _stats.AutoSize = false;
            _stats.TextAlign = ContentAlignment.MiddleLeft;
            _stats.Height = 26;
            _stats.Margin = new Padding(0, 0, 0, 4);
            bottom.Controls.Add(_stats, 0, 0);

            var btnPanel = new FlowLayoutPanel();
            btnPanel.Dock = DockStyle.Fill;
            btnPanel.FlowDirection = FlowDirection.RightToLeft;
            btnPanel.WrapContents = false;
            btnPanel.Margin = new Padding(0);

            _btnExec = new Button();
            _btnExec.Text = "立即释放";
            _btnExec.Size = new Size(140, 32);
            _btnExec.Margin = new Padding(6, 0, 0, 0);
            _btnExec.Click += (s, e) => ExecuteInPlace();
            btnPanel.Controls.Add(_btnExec);

            _btnClose = new Button();
            _btnClose.Text = "关闭";
            _btnClose.Size = new Size(96, 32);
            _btnClose.Margin = new Padding(0);
            _btnClose.Click += (s, e) => this.Close();
            btnPanel.Controls.Add(_btnClose);

            bottom.Controls.Add(btnPanel, 0, 1);

            // ==== Dock 顺序：Fill 的两个（loading & list）先添加，才不会被 top/bottom 遮挡 ====
            this.Controls.Add(_loading);
            this.Controls.Add(_list);
            this.Controls.Add(bottom);
            this.Controls.Add(top);
            _loading.BringToFront();

            // 标题实时刷新
            UpdateTitle();
            _titleTimer = new System.Windows.Forms.Timer();
            _titleTimer.Interval = 2000;
            _titleTimer.Tick += (s, e) => UpdateTitle();
            _titleTimer.Start();

            this.FormClosed += (s, e) =>
            {
                _titleTimer.Stop();
                _titleTimer.Dispose();
                // _ownedIcon 引用 IconRenderer 缓存的共享实例，不可在此 Dispose
                _ownedIcon = null;
            };

            LoadProcessesAsync();
        }

        // ============================================================
        // 加载
        // ============================================================
        private class ProcRow
        {
            public string Name;
            public int Pid;
            public long WorkingSet;
            public bool IsHardProtected;    // 系统硬保护
            public bool IsPersisted;        // 已持久化白名单
            public bool IsSession;          // 会话勾选
        }

        // 硬编码保护清单：直接引用 MemoryCleaner.HardBlacklist，不再维护副本。
        private static HashSet<string> HardBlacklist { get { return MemoryCleaner.HardBlacklist; } }

        private void LoadProcessesAsync()
        {
            _loaded = false;
            _loading.Text = "读取进程中...";
            _loading.Visible = true;
            _loading.BringToFront();
            _btnRefresh.Enabled = false;

            // P1-1 修复①：后台线程取白名单快照，避免与 UI 线程并发读写 HashSet
            var snap = _settings.SnapshotWhitelists();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var rows = new List<ProcRow>();
                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        string name = "";
                        int pid = 0;
                        long ws = 0;
                        try { name = p.ProcessName; pid = p.Id; } catch { }
                        try { ws = p.WorkingSet64; } catch { }
                        if (!string.IsNullOrEmpty(name))
                        {
                            var r = new ProcRow();
                            r.Name = name;
                            r.Pid = pid;
                            r.WorkingSet = ws;
                            r.IsHardProtected = HardBlacklist.Contains(name);
                            r.IsPersisted = snap.IsInUser(name);
                            r.IsSession = snap.IsInSession(name);
                            rows.Add(r);
                        }
                        try { p.Dispose(); } catch { }
                    }
                }
                catch (Exception ex) { Settings.Log("ReleasePanel.LoadProcessesAsync 枚举失败: " + ex.Message); }

                try
                {
                    if (this.IsDisposed) return;
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.IsDisposed) return;
                        _allRows = rows;
                        _loaded = true;
                        _loading.Visible = false;
                        _btnRefresh.Enabled = true;
                        ApplyFilterAndBind();
                    }));
                }
                catch (Exception ex) { Settings.Log("ReleasePanel.LoadProcessesAsync 回调失败: " + ex.Message); }
            });
        }

        // ============================================================
        // 过滤 + 绑定
        // ============================================================

        /// <summary>
        /// 用最新白名单快照刷新 _allRows 中每个 ProcRow 的保护状态字段。
        /// 在"从文件重载"或"管理已持久化"修改了白名单后调用，
        /// 再调 ApplyFilterAndBind() 重建 ListView，使面板显示与实际一致。
        /// </summary>
        private void RefreshProtectionFlags()
        {
            var snap = _settings.SnapshotWhitelists();
            foreach (var r in _allRows)
            {
                r.IsPersisted = snap.IsInUser(r.Name);
                r.IsSession = snap.IsInSession(r.Name);
            }
        }

        private void ApplyFilterAndBind()
        {
            if (!_loaded) return;

            string q = _filter.Text.Trim();
            bool onlyUnprot = _onlyUnprotected.Checked;

            var list = new List<ProcRow>();
            foreach (var r in _allRows)
            {
                if (onlyUnprot && (r.IsHardProtected || r.IsPersisted)) continue;
                if (q.Length > 0)
                {
                    if (r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        !r.Pid.ToString().Contains(q))
                        continue;
                }
                list.Add(r);
            }

            list.Sort((a, b) =>
            {
                int c;
                switch (_sortColumn)
                {
                    case 0:  // 状态 (checkbox) 排序：勾选在前
                        c = GetProtectRank(a).CompareTo(GetProtectRank(b));
                        break;
                    case 1: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 2: c = a.Pid.CompareTo(b.Pid); break;
                    case 3: c = a.WorkingSet.CompareTo(b.WorkingSet); break;
                    case 4: c = GetProtectRank(a).CompareTo(GetProtectRank(b)); break;
                    default: c = 0; break;
                }
                return _sortOrder == SortOrder.Descending ? -c : c;
            });

            _suppressItemCheck = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            long totalWs = 0;
            int willRelease = 0;
            int sessionCount = 0;
            int persistedCount = 0;
            foreach (var r in list)
            {
                var lvi = new ListViewItem("");   // 复选列
                lvi.SubItems.Add(r.Name);
                lvi.SubItems.Add(r.Pid.ToString());
                lvi.SubItems.Add(MemorySnapshot.FormatBytes((ulong)r.WorkingSet));
                lvi.SubItems.Add(GetStatusText(r));
                lvi.Tag = r;

                bool prot = r.IsHardProtected || r.IsPersisted || r.IsSession;
                lvi.Checked = prot;
                if (r.IsHardProtected) lvi.ForeColor = Color.Gray;
                else if (r.IsPersisted) lvi.ForeColor = Color.FromArgb(30, 90, 200);
                else lvi.ForeColor = Color.Black;

                _list.Items.Add(lvi);

                if (!prot) { willRelease++; totalWs += r.WorkingSet; }
                if (r.IsSession) sessionCount++;
                if (r.IsPersisted) persistedCount++;
            }
            _list.EndUpdate();
            _suppressItemCheck = false;

            int totalItems = list.Count;
            int totalAll = _allRows.Count;
            _stats.Text = string.Format(
                "共 {0} 项 (过滤后 {1}) | 将释放 {2} 个 (合计 WS {3}) | 会话勾选 {4} | 已持久化 {5}",
                totalAll, totalItems, willRelease,
                MemorySnapshot.FormatBytes((ulong)totalWs),
                sessionCount, persistedCount);
            _btnExec.Text = string.Format("立即释放 {0}", willRelease);
            _btnPersist.Enabled = sessionCount > 0;
        }

        private static int GetProtectRank(ProcRow r)
        {
            // 保护越严排前面：Hard=0, Persisted=1, Session=2, None=3
            if (r.IsHardProtected) return 0;
            if (r.IsPersisted) return 1;
            if (r.IsSession) return 2;
            return 3;
        }

        private static string GetStatusText(ProcRow r)
        {
            if (r.IsHardProtected) return "🔒 系统硬保护 (不可改)";
            if (r.IsPersisted) return "⭐ 已持久化 (在文件里)";
            if (r.IsSession) return "会话勾选 (未持久化)";
            return "将被释放";
        }

        // ============================================================
        // 交互
        // ============================================================
        private void List_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressItemCheck) return;
            var lvi = _list.Items[e.Index];
            var r = lvi.Tag as ProcRow;
            if (r == null) return;

            // 系统硬保护 / 已持久化：不允许在此界面切换
            if (r.IsHardProtected)
            {
                e.NewValue = CheckState.Checked;   // 强制保持勾选
                return;
            }
            if (r.IsPersisted)
            {
                e.NewValue = CheckState.Checked;
                return;
            }

            bool willBeChecked = (e.NewValue == CheckState.Checked);
            // P1-1 修复②：加锁保护 SessionWhitelist 读写
            lock (_settings.WhitelistLock)
            {
                if (willBeChecked)
                {
                    _settings.SessionWhitelist.Add(r.Name);
                    r.IsSession = true;
                }
                else
                {
                    _settings.SessionWhitelist.Remove(r.Name);
                    _settings.SessionWhitelist.Remove(r.Name + ".exe");
                    r.IsSession = false;
                }
            }

            // 更新该行文字 + 底部统计（延到 UI 空闲）
            this.BeginInvoke(new Action(() =>
            {
                if (this.IsDisposed || _list.IsDisposed) return;
                try
                {
                    lvi.SubItems[4].Text = GetStatusText(r);
                    lvi.ForeColor = Color.Black;
                    RecalcStats();
                }
                catch (InvalidOperationException) { /* 控件已释放 */ }
            }));
        }

        private void List_MouseClick(object sender, MouseEventArgs e)
        {
            // 单击行任意位置（不含复选框自身，那里 ListView 自己处理）切换勾选
            var hit = _list.HitTest(e.Location);
            if (hit.Item == null) return;
            // 只在点击复选框以外的区域时手动切换
            var chkArea = new Rectangle(hit.Item.Bounds.Left, hit.Item.Bounds.Top, 32, hit.Item.Bounds.Height);
            if (chkArea.Contains(e.Location)) return;
            // 硬保护 / 持久化不允许切换
            var r = hit.Item.Tag as ProcRow;
            if (r == null || r.IsHardProtected || r.IsPersisted) return;
            hit.Item.Checked = !hit.Item.Checked;
        }

        private void RecalcStats()
        {
            long totalWs = 0;
            int willRelease = 0;
            int sessionCount = 0;
            int persistedCount = 0;
            foreach (ListViewItem lvi in _list.Items)
            {
                var r = lvi.Tag as ProcRow;
                if (r == null) continue;
                bool prot = r.IsHardProtected || r.IsPersisted || r.IsSession;
                if (!prot) { willRelease++; totalWs += r.WorkingSet; }
                if (r.IsSession) sessionCount++;
                if (r.IsPersisted) persistedCount++;
            }
            _stats.Text = string.Format(
                "共 {0} 项 (过滤后 {1}) | 将释放 {2} 个 (合计 WS {3}) | 会话勾选 {4} | 已持久化 {5}",
                _allRows.Count, _list.Items.Count, willRelease,
                MemorySnapshot.FormatBytes((ulong)totalWs),
                sessionCount, persistedCount);
            _btnExec.Text = string.Format("立即释放 {0}", willRelease);
            _btnPersist.Enabled = sessionCount > 0;
        }

        // ============================================================
        // 管理已持久化：弹出子对话框
        // ============================================================
        private void ShowPersistedManager()
        {
            using (var dlg = new PersistedManagerDialog(_settings))
            {
                dlg.ShowDialog(this);
                if (dlg.Changed)
                {
                    // 已持久化列表可能变化 -> 刷新面板视图
                    RefreshProtectionFlags();
                    ApplyFilterAndBind();
                }
            }
        }

        // ============================================================
        // 面板内直接执行释放（不关闭窗口）
        // ============================================================
        private void ExecuteInPlace()
        {
            _btnExec.Enabled = false;
            string origText = _btnExec.Text;
            _btnExec.Text = "释放中...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ReleaseResult r = null;
                Exception caught = null;
                try
                {
                    r = MemoryCleaner.Execute(_settings);
                }
                catch (Exception ex) { caught = ex; }

                try
                {
                    if (this.IsDisposed) return;
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.IsDisposed) return;
                        if (r != null)
                        {
                            MessageBox.Show(this, r.FormatSummary(), "释放完成",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show(this, caught != null ? caught.ToString() : "未知错误", "释放失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        _btnExec.Enabled = true;
                        _btnExec.Text = origText;
                        // 刷新：进程列表和 WS 都会变
                        LoadProcessesAsync();
                        UpdateTitle();
                    }));
                }
                catch (Exception ex) { Settings.Log("ReleasePanel.Execute 回调失败: " + ex.Message); }
            });
        }

        // ============================================================
        // 标题与图标
        // ============================================================
        private void UpdateTitle()
        {
            var snap = MemoryInfo.Take();
            this.Text = "Traynexus - 释放面板  |  " + snap.FormatShort();
            try
            {
                // IconRenderer 缓存 Icon，返回的是共享实例，不可 Dispose
                // 电池数据 T12 才接入，暂时用 0%、未充电
                _ownedIcon = IconRenderer.Build(snap.UsedPercent, 0, false);
                this.Icon = _ownedIcon;
            }
            catch { }
        }
    }

    // ================================================================
    // 「管理已持久化」子对话框
    // ================================================================
    internal class PersistedManagerDialog : Form
    {
        private readonly Settings _settings;
        private readonly CheckedListBox _clb;
        private readonly Label _hint;
        private readonly Button _btnRemove;
        private readonly Button _btnClose;

        public bool Changed { get; private set; }

        public PersistedManagerDialog(Settings settings)
        {
            _settings = settings;

            this.Text = "管理已持久化白名单";
            this.Size = new Size(420, 460);
            this.MinimumSize = new Size(360, 320);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // 顶部说明
            var lblTop = new Label();
            lblTop.Text = "勾选要取消保护的进程，然后点【移除选中项】。系统硬保护无法在此操作。";
            lblTop.Dock = DockStyle.Top;
            lblTop.Height = 40;
            lblTop.Padding = new Padding(10, 10, 10, 6);
            lblTop.ForeColor = Color.DimGray;

            // 列表
            _clb = new CheckedListBox();
            _clb.Dock = DockStyle.Fill;
            _clb.CheckOnClick = true;
            _clb.IntegralHeight = false;
            _clb.ItemHeight = 22;
            _clb.ItemCheck += (s, e) =>
            {
                this.BeginInvoke(new Action(UpdateHint));
            };

            // 底部
            var bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 76;
            bottom.ColumnCount = 1;
            bottom.RowCount = 2;
            bottom.Padding = new Padding(8, 6, 8, 8);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _hint = new Label();
            _hint.Dock = DockStyle.Fill;
            _hint.AutoSize = false;
            _hint.Height = 26;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.Margin = new Padding(0, 0, 0, 4);
            bottom.Controls.Add(_hint, 0, 0);

            var btnBar = new FlowLayoutPanel();
            btnBar.Dock = DockStyle.Fill;
            btnBar.FlowDirection = FlowDirection.RightToLeft;
            btnBar.WrapContents = false;
            btnBar.Margin = new Padding(0);

            _btnClose = new Button();
            _btnClose.Text = "关闭";
            _btnClose.Size = new Size(96, 32);
            _btnClose.Margin = new Padding(6, 0, 0, 0);
            _btnClose.Click += (s, e) => this.Close();
            btnBar.Controls.Add(_btnClose);

            _btnRemove = new Button();
            _btnRemove.Text = "移除选中项";
            _btnRemove.Size = new Size(140, 32);
            _btnRemove.Margin = new Padding(0);
            _btnRemove.Click += (s, e) => RemoveChecked();
            btnBar.Controls.Add(_btnRemove);

            bottom.Controls.Add(btnBar, 0, 1);

            // 顺序: 先 Fill 再 Top/Bottom
            this.Controls.Add(_clb);
            this.Controls.Add(bottom);
            this.Controls.Add(lblTop);

            LoadItems();
            UpdateHint();
        }

        private void LoadItems()
        {
            _clb.BeginUpdate();
            _clb.Items.Clear();
            // P1-1 修复④：加锁保护 UserWhitelist 枚举
            List<string> sorted;
            lock (_settings.WhitelistLock)
            {
                sorted = new List<string>(_settings.UserWhitelist);
            }
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in sorted) _clb.Items.Add(n, false);
            _clb.EndUpdate();
        }

        private void UpdateHint()
        {
            int total = _clb.Items.Count;
            int chk = _clb.CheckedItems.Count;
            _hint.Text = string.Format("已持久化 {0} 个 | 已勾选 {1} 个", total, chk);
            _btnRemove.Enabled = chk > 0;
        }

        private void RemoveChecked()
        {
            var toRemove = new List<string>();
            foreach (object it in _clb.CheckedItems) toRemove.Add(it.ToString());
            if (toRemove.Count == 0) return;

            var res = MessageBox.Show(this,
                string.Format("确认从持久化白名单移除 {0} 项吗？\r\n(会立即写入 whitelist.txt)",
                    toRemove.Count),
                "确认移除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (res != DialogResult.OK) return;

            bool ok = _settings.RemovePersisted(toRemove);
            Changed = true;
            LoadItems();
            UpdateHint();
            MessageBox.Show(this,
                ok ? "已移除并保存" : "移除失败（文件可能被占用），内存已改但未写盘",
                "管理已持久化",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
