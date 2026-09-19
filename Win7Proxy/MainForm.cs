using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProxyCore;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace Win7Proxy
{
    public class MainForm : Form
    {
        private static readonly string AppVersion = GetAppVersion();

        private static string GetAppVersion()
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version == null ? "" : version.Major + "." + version.Minor + "." + version.Build;
        }

        private readonly AppState _state;
        private CoreProcess _core;
        private readonly object _coreLock = new object();
        private readonly DataGridView _grid;
        private readonly TextBox _log;
        private readonly Label _status;
        private readonly Label _nodeLabel;
        private readonly ComboBox _mode;
        private readonly FlowLayoutPanel _toolPanel;
        private ComboBox _coreBox;
        private readonly NotifyIcon _tray;
        private int _sortCol = -1;
        private bool _sortAsc = true;
        private bool _realExit = false;
        private Button _testBtn;
        private readonly HashSet<int> _testingRows = new HashSet<int>();
        private readonly object _testingLock = new object();
        private System.Windows.Forms.Timer _spinTimer;
        private int _spinFrame;
        private ComboBox _subFilter;
        private string _filterSubscriptionId;   // null=全部, ""=手动, 其它=订阅 Id
        private bool _suppressFilterEvent;
        private List<Node> _view = new List<Node>();

        private sealed class GroupItem
        {
            public string Id;
            public string Text;
            public override string ToString() { return Text; }
        }

        private ToolStripMenuItem _miAutoStart;
        private ToolStripMenuItem _miAutoConnect;
        private ToolStripMenuItem _miDedupe;
        private string _coreVersion = "";
        private Node _currentNode;
        private bool _busy;

        public MainForm()
        {
            HiDpi.ApplyTo(this);   // 必须在设置 Size / 创建控件之前

            _state = AppState.Load();

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            Icon appIcon = null;
            try { if (File.Exists(iconPath)) appIcon = new Icon(iconPath); }
            catch { appIcon = null; }

            Text = "Win7Proxy " + AppVersion + "  ·  Windows 7 代理客户端";
            Icon = appIcon ?? SystemIcons.Application;
            Width = 860; Height = 600;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(244, 246, 249);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10, 8, 10, 8),
                BackColor = Color.FromArgb(244, 246, 249)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52F));

            _toolPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 4)
            };
            Button Btn(string text, EventHandler h, Color? accent = null)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9F),
                    Margin = new Padding(2, 2, 2, 2),
                    Padding = new Padding(12, 5, 12, 5)
                };
                b.FlatAppearance.BorderSize = 0;
                if (accent.HasValue)
                {
                    b.BackColor = accent.Value;
                    b.ForeColor = Color.White;
                }
                else
                {
                    b.BackColor = Color.White;
                    b.ForeColor = Color.FromArgb(40, 44, 52);
                }
                b.Click += h;
                _toolPanel.Controls.Add(b);
                return b;
            }
            Btn("导入订阅", (s, e) => ImportSubscription());
            Btn("添加节点", (s, e) => AddNode());
            Btn("更新订阅", (s, e) => UpdateAllSubscriptions());
            _testBtn = Btn("测试延迟", (s, e) => TestLatency());
            Btn("全选", (s, e) => _grid.SelectAll());
            Btn("删除选中", (s, e) => DeleteSelected());
            Btn("启动", (s, e) => StartProxy(false), Color.FromArgb(46, 160, 67));
            Btn("停止", (s, e) => StopProxy(), Color.FromArgb(208, 64, 64));
            Btn("更新内核", (s, e) => UpdateCore());

            var modeLabel = new Label
            {
                Text = "模式:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Margin = new Padding(14, 9, 2, 0)
            };
            _toolPanel.Controls.Add(modeLabel);
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F) };
            _mode.Items.AddRange(new[] { "全局", "规则", "直连" });
            _mode.SelectedIndex = (int)_state.Mode;
            _mode.SelectedIndexChanged += (s, e) =>
            {
                _state.Mode = (ProxyMode)_mode.SelectedIndex;
                _state.Save();
                // 旧版本：改了模式只存盘，正在跑的内核不会重建配置，
                // 结果"切到规则模式"其实还在全局。现在运行中切换会立刻重启内核。
                if (_core != null && _currentNode != null)
                {
                    AppendLog("模式已切换为「" + _mode.Text + "」，正在重启内核...");
                    StartProxy(true);
                }
                else
                {
                    UpdateStatus();
                }
            };
            _toolPanel.Controls.Add(_mode);

            // 内核选择：不同内核支持的传输方式不同（例如 h2 只有 V2Ray / sing-box 还能用）
            var coreLabel = new Label
            {
                Text = "内核:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Margin = new Padding(14, 9, 2, 0)
            };
            _toolPanel.Controls.Add(coreLabel);
            _coreBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F) };
            foreach (var spec in CoreRegistry.All) _coreBox.Items.Add(spec.Name);
            _coreBox.SelectedIndex = (int)_state.Core;
            _coreBox.SelectedIndexChanged += (s, e) => OnCoreKindChanged();
            _toolPanel.Controls.Add(_coreBox);

            var filterLabel = new Label
            {
                Text = "订阅:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Margin = new Padding(14, 9, 2, 0)
            };
            _toolPanel.Controls.Add(filterLabel);
            _subFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F) };
            _subFilter.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressFilterEvent) return;
                var item = _subFilter.SelectedItem as GroupItem;
                _filterSubscriptionId = item == null ? null : item.Id;
                RefreshGrid();
            };
            _toolPanel.Controls.Add(_subFilter);
            _spinTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _spinTimer.Tick += (s, e) =>
            {
                if (_grid.IsDisposed) return;
                _spinFrame = (_spinFrame + 1) % 4;
                string glyph = new[] { "◐", "◓", "◑", "◒" }[_spinFrame];
                lock (_testingLock)
                {
                    foreach (int idx in _testingRows)
                        if (idx >= 0 && idx < _grid.Rows.Count)
                            _grid.Rows[idx].Cells["latency"].Value = glyph;
                }
            };
            layout.Controls.Add(_toolPanel, 0, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true, AllowUserToAddRows = false, ReadOnly = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                GridColor = Color.FromArgb(230, 234, 238),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _grid.RowTemplate.Height = 28;
            _grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(40, 44, 52),
                SelectionBackColor = Color.FromArgb(213, 232, 252),
                SelectionForeColor = Color.FromArgb(20, 24, 32),
                Padding = new Padding(4, 2, 4, 2)
            };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(45, 125, 218),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Padding = new Padding(4, 4, 4, 4)
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(245, 248, 252)
            };
            _grid.Columns.Add("remarks", "备注");
            _grid.Columns.Add("type", "类型");
            _grid.Columns.Add("addr", "地址");
            _grid.Columns.Add("latency", "延迟");
            _grid.Columns.Add("source", "来源");
            _grid.Columns["remarks"].FillWeight = 36;
            _grid.Columns["type"].FillWeight = 11;
            _grid.Columns["addr"].FillWeight = 30;
            _grid.Columns["latency"].FillWeight = 10;
            _grid.Columns["source"].FillWeight = 13;
            foreach (DataGridViewColumn c in _grid.Columns)
                c.SortMode = DataGridViewColumnSortMode.Programmatic;
            _grid.SelectionChanged += (s, e) =>
            {
                if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _view.Count)
                {
                    _state.SelectedIndex = _state.Nodes.IndexOf(_view[_grid.CurrentRow.Index]);
                    _state.Save();
                }
                UpdateNodeLabel();
            };
            _grid.ColumnHeaderMouseClick += (s, e) => SortByColumn(e.ColumnIndex);
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _view.Count)
                {
                    _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
                    StartProxy(false);
                }
            };
            layout.Controls.Add(_grid, 0, 2);

            _nodeLabel = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 125, 218),
                Text = "当前节点：未选择",
                Padding = new Padding(2, 4, 2, 2)
            };
            layout.Controls.Add(_nodeLabel, 0, 1);

            _status = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Text = "状态：已停止",
                Padding = new Padding(2, 2, 2, 6)
            };
            layout.Controls.Add(_status, 0, 3);

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 251, 252),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9)
            };
            layout.Controls.Add(_log, 0, 4);
            _state.PersistenceError += message => AppendLog(message);

            Controls.Add(layout);
            var menu = BuildMenu();
            Controls.Add(menu);
            MainMenuStrip = menu;

            _tray = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
                Text = "Win7Proxy",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("显示主窗口", null, (s, e) => ShowForm());
            _tray.ContextMenuStrip.Items.Add("启动", null, (s, e) => StartProxy(false));
            _tray.ContextMenuStrip.Items.Add("停止", null, (s, e) => StopProxy());
            _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            _tray.ContextMenuStrip.Items.Add("退出", null, (s, e) => RealExit());
            _tray.DoubleClick += (s, e) => ShowForm();

            FormClosing += (s, e) =>
            {
                if (!_realExit)
                {
                    e.Cancel = true;
                    Hide();
                    _state.Save();
                    return;
                }
                StopProxy();
                _state.Save();
                _tray.Visible = false;
            };

            RefreshGrid();
            if (_state.SelectedIndex >= 0 && _state.SelectedIndex < _state.Nodes.Count)
            {
                int row = _view.IndexOf(_state.Nodes[_state.SelectedIndex]);
                if (row >= 0) try { _grid.Rows[row].Selected = true; } catch { }
            }
            UpdateNodeLabel();

            HiDpi.ScaleForDpi(this);   // 必须在所有控件创建完之后

            // 下面这些要等窗体句柄创建完才能安全 Invoke，所以放到 Load 里做
            Load += (s, e) => OnFirstLoad();
        }

        private void OnFirstLoad()
        {
            if (!string.IsNullOrEmpty(_state.LoadWarning))
                AppendLog(_state.LoadWarning);

            // 上次异常退出可能留下一个没人管的 xray，先按 PID 文件回收掉
            var killed = CoreProcess.KillOrphan(PidFilePath());
            if (killed > 0) AppendLog("已清理上次残留的内核进程。");
            if (SystemProxy.Restore())
                AppendLog("已恢复上次异常退出前的系统代理设置。");

            ProbeCoreVersionAsync();

            if (_state.AutoConnect && _state.Mode != ProxyMode.Direct &&
                _state.SelectedIndex >= 0 && _state.SelectedIndex < _state.Nodes.Count)
            {
                AppendLog("自动连接已开启，正在用上次节点启动...");
                StartProxy(true);
            }
            else
            {
                UpdateStatus();
            }
        }

        // ---------- 菜单 ----------

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip { BackColor = Color.FromArgb(244, 246, 249) };

            var mSub = new ToolStripMenuItem("订阅(&S)");
            mSub.DropDownItems.Add("导入订阅(&I)...", null, (s, e) => ImportSubscription());
            mSub.DropDownItems.Add("从剪贴板导入(&C)", null, (s, e) => ImportFromClipboard());
            mSub.DropDownItems.Add(new ToolStripSeparator());
            mSub.DropDownItems.Add("更新全部订阅(&U)", null, (s, e) => UpdateAllSubscriptions());
            mSub.DropDownItems.Add("订阅管理(&M)...", null, (s, e) => OpenSubscriptionManager());

            var mNode = new ToolStripMenuItem("节点(&N)");
            mNode.DropDownItems.Add("添加节点(&A)...", null, (s, e) => AddNode());
            mNode.DropDownItems.Add("复制原始链接(&L)", null, (s, e) => CopyRawLinks());
            mNode.DropDownItems.Add(new ToolStripSeparator());
            mNode.DropDownItems.Add("去除重复节点(&D)", null, (s, e) => DedupeNodes());
            mNode.DropDownItems.Add("清空延迟结果(&R)", null, (s, e) => ClearLatency());

            var mOpt = new ToolStripMenuItem("设置(&O)");
            _miAutoStart = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = _state.AutoStart };
            _miAutoStart.CheckedChanged += (s, e) =>
            {
                _state.AutoStart = _miAutoStart.Checked;
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Win7Proxy.exe");
                SystemProxy.SetAutoStart(_miAutoStart.Checked, exe);
                _state.Save();
                AppendLog("开机自启：" + (_miAutoStart.Checked ? "已开启" : "已关闭"));
            };
            _miAutoConnect = new ToolStripMenuItem("启动时自动连接上次节点") { CheckOnClick = true, Checked = _state.AutoConnect };
            _miAutoConnect.CheckedChanged += (s, e) =>
            {
                _state.AutoConnect = _miAutoConnect.Checked;
                _state.Save();
            };
            _miDedupe = new ToolStripMenuItem("导入订阅时自动去重") { CheckOnClick = true, Checked = _state.DedupeOnImport };
            _miDedupe.CheckedChanged += (s, e) =>
            {
                _state.DedupeOnImport = _miDedupe.Checked;
                _state.Save();
            };
            mOpt.DropDownItems.Add(_miAutoStart);
            mOpt.DropDownItems.Add(_miAutoConnect);
            mOpt.DropDownItems.Add(_miDedupe);

            var mHelp = new ToolStripMenuItem("帮助(&H)");
            mHelp.DropDownItems.Add("打开程序目录", null, (s, e) => OpenFolder(AppDomain.CurrentDomain.BaseDirectory));
            mHelp.DropDownItems.Add("打开配置目录", null, (s, e) => OpenFolder(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win7Proxy")));
            mHelp.DropDownItems.Add(new ToolStripSeparator());
            mHelp.DropDownItems.Add("清空日志", null, (s, e) => { _log.Clear(); });
            mHelp.DropDownItems.Add("关于", null, (s, e) => ShowAbout());

            menu.Items.Add(mSub);
            menu.Items.Add(mNode);
            menu.Items.Add(mOpt);
            menu.Items.Add(mHelp);
            return menu;
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "Win7Proxy " + AppVersion + "\n\n" +
                "Windows 7 上的代理客户端，支持 Xray / V2Ray / sing-box 三种内核。\n" +
                "当前内核：" + CoreRegistry.Of(_state.Core).Name +
                (string.IsNullOrEmpty(_coreVersion) ? "（未检测到）" : "  " + _coreVersion) +
                "\n\n流量接管：系统代理指向本机内核端口，分流由 geoip/geosite 规则完成。",
                "关于 Win7Proxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", path));
            }
            catch { }
        }

        // ---------- 基础 UI ----------

        private Node SelectedNode()
        {
            if (_grid.CurrentRow == null) return null;
            var i = _grid.CurrentRow.Index;
            return (i >= 0 && i < _view.Count) ? _view[i] : null;
        }

        private void UpdateNodeLabel()
        {
            if (_nodeLabel == null || _nodeLabel.IsDisposed) return;
            if (_nodeLabel.InvokeRequired) { _nodeLabel.BeginInvoke((Action)UpdateNodeLabel); return; }
            var n = SelectedNode();
            _nodeLabel.Text = n != null ? "当前节点：" + n.Remarks : "当前节点：未选择";
        }

        private void RefreshGrid()
        {
            if (InvokeRequired) { BeginInvoke((Action)RefreshGrid); return; }
            RebuildGroupFilter();

            // 记录当前视图状态（按节点引用保存，排序/刷新后仍能对应到正确的行）
            int first = _grid.FirstDisplayedScrollingRowIndex;
            var selNodes = SelectedNodes();
            var curNode = (_grid.CurrentRow != null && _grid.CurrentRow.Index < _view.Count)
                ? _view[_grid.CurrentRow.Index] : null;

            _view = BuildView();

            _grid.Rows.Clear();
            foreach (var n in _view)
            {
                _grid.Rows.Add(
                    n.Remarks,
                    n.Type.ToString(),
                    n.Address + ":" + n.Port,
                    n.LatencyMs < 0 ? "-" : n.LatencyMs + " ms",
                    n.SourceName);
            }

            // 恢复选中与当前行（按节点引用匹配新位置）
            int? focusIdx = null;
            foreach (var n in selNodes)
            {
                int idx = _view.IndexOf(n);
                if (idx >= 0) { _grid.Rows[idx].Selected = true; if (!focusIdx.HasValue) focusIdx = idx; }
            }
            if (curNode != null)
            {
                int idx = _view.IndexOf(curNode);
                if (idx >= 0) { _grid.CurrentCell = _grid.Rows[idx].Cells[0]; focusIdx = idx; }
            }
            // 恢复滚动位置：优先让焦点行可见，否则回到原先的顶部行
            int scrollTo = focusIdx.HasValue ? focusIdx.Value : (first >= 0 ? first : 0);
            if (_grid.Rows.Count > 0)
                _grid.FirstDisplayedScrollingRowIndex = Math.Min(scrollTo, _grid.Rows.Count - 1);
        }

        private List<Node> BuildView()
        {
            var list = new List<Node>();
            foreach (var n in _state.Nodes)
                if (MatchesFilter(n)) list.Add(n);
            return list;
        }

        private bool MatchesFilter(Node n)
        {
            if (_filterSubscriptionId == null) return true;
            if (_filterSubscriptionId == "") return string.IsNullOrEmpty(n.SubscriptionId);
            return n.SubscriptionId == _filterSubscriptionId;
        }

        // 重建「订阅」下拉：全部 + 每个订阅 + 手动；订阅被删/改名后自动跟上，
        // 当前分组若已不存在则回落到「全部」。
        private void RebuildGroupFilter()
        {
            if (_subFilter == null || _subFilter.IsDisposed) return;
            _suppressFilterEvent = true;
            try
            {
                var wanted = _filterSubscriptionId;
                _subFilter.Items.Clear();
                _subFilter.Items.Add(new GroupItem { Id = null, Text = "全部" });
                foreach (var s in _state.Subscriptions)
                    _subFilter.Items.Add(new GroupItem { Id = s.Id, Text = string.IsNullOrEmpty(s.Name) ? "(未命名)" : s.Name });
                foreach (var n in _state.Nodes)
                    if (string.IsNullOrEmpty(n.SubscriptionId))
                    {
                        _subFilter.Items.Add(new GroupItem { Id = "", Text = "手动" });
                        break;
                    }

                int restore = 0;
                for (int i = 0; i < _subFilter.Items.Count; i++)
                    if (((GroupItem)_subFilter.Items[i]).Id == wanted) { restore = i; break; }
                _filterSubscriptionId = ((GroupItem)_subFilter.Items[restore]).Id;
                _subFilter.SelectedIndex = restore;
            }
            finally { _suppressFilterEvent = false; }
        }

        private void SortByColumn(int col)
        {
            if (col < 0 || col >= _grid.Columns.Count) return;
            if (_sortCol == col) _sortAsc = !_sortAsc;
            else { _sortCol = col; _sortAsc = true; }

            Comparison<Node> cmp = (a, b) =>
            {
                int r;
                switch (col)
                {
                    case 0: r = string.Compare(a.Remarks, b.Remarks, StringComparison.OrdinalIgnoreCase); break;
                    case 1: r = string.Compare(a.Type.ToString(), b.Type.ToString(), StringComparison.OrdinalIgnoreCase); break;
                    case 2: r = string.Compare(a.Address, b.Address, StringComparison.OrdinalIgnoreCase);
                            if (r == 0) r = a.Port.CompareTo(b.Port); break;
                    case 3:
                        // 延迟：数值升序；未测(-1)排到最后
                        int ka = a.LatencyMs < 0 ? int.MaxValue : a.LatencyMs;
                        int kb = b.LatencyMs < 0 ? int.MaxValue : b.LatencyMs;
                        r = ka.CompareTo(kb);
                        break;
                    case 4: r = string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase); break;
                    default: r = 0; break;
                }
                return _sortAsc ? r : -r;
            };
            _state.Nodes.Sort(cmp);
            _state.Save();
            RefreshGrid();
        }

        private void AppendLog(string line)
        {
            if (_log == null || _log.IsDisposed) return;
            if (_log.InvokeRequired) { _log.BeginInvoke((Action<string>)AppendLog, line); return; }
            _log.AppendText(line + Environment.NewLine);
            if (_log.Lines.Length > 2000)
                _log.Text = string.Join(Environment.NewLine, _log.Lines, _log.Lines.Length - 1000, 1000);
        }

        private void SetStatus(string text)
        {
            if (_status == null || _status.IsDisposed) return;
            if (_status.InvokeRequired) { _status.BeginInvoke((Action<string>)SetStatus, text); return; }
            _status.Text = text;
        }

        private void UpdateStatus()
        {
            var modeText = _state.Mode == ProxyMode.Global ? "全局" : (_state.Mode == ProxyMode.Rule ? "规则" : "直连");
            if (_core != null && _currentNode != null)
                SetStatus("状态：运行中（" + modeText + "） — " + _currentNode.Remarks);
            else
                SetStatus("状态：已停止（" + modeText + "）" +
                          (string.IsNullOrEmpty(_coreVersion) ? "" : "  ·  内核 " + _coreVersion));
        }

        private bool TryBeginBusy(string status)
        {
            if (_busy) return false;
            _busy = true;
            UseWaitCursor = true;
            if (_toolPanel != null) _toolPanel.Enabled = false;
            if (MainMenuStrip != null) MainMenuStrip.Enabled = false;
            SetStatus(status);
            return true;
        }

        private void EndBusy()
        {
            _busy = false;
            UseWaitCursor = false;
            if (_toolPanel != null && !_toolPanel.IsDisposed) _toolPanel.Enabled = true;
            if (MainMenuStrip != null && !MainMenuStrip.IsDisposed) MainMenuStrip.Enabled = true;
            UpdateStatus();
        }

        // ---------- 订阅与节点 ----------

        private async void ImportSubscription()
        {
            Subscription sub;
            using (var dlg = new SubscriptionForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                sub = dlg.CreateSubscription();
            }

            if (!TryBeginBusy("状态：正在拉取订阅…")) return;
            try
            {
                await Task.Run(() => SubscriptionFetcher.Fetch(sub));
                var count = sub.Nodes.Count;

                // 同一个 URL 重复导入时替换旧订阅，而不是把节点无脑追加。
                var existing = FindByUrl(sub.Url);
                if (existing != null)
                {
                    sub.Id = existing.Id;
                    SubscriptionManagerForm.RemoveNodesFrom(_state, existing.Id);
                    _state.Subscriptions.Remove(existing);
                }

                _state.Subscriptions.Add(sub);
                SubscriptionManagerForm.ApplyNodesTo(_state, sub);
                if (_state.DedupeOnImport) SubscriptionManagerForm.Dedupe(_state);
                _state.Save();
                RefreshGrid();
                MessageBox.Show("成功导入 " + count + " 个节点" +
                                (existing != null ? "（已替换同 URL 的旧订阅）" : "") + "。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                AppendLog("导入订阅「" + sub.Name + "」，" + count + " 个节点。");
            }
            catch (ProxyCoreException ex)
            {
                MessageBox.Show("订阅拉取失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("订阅处理失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndBusy(); }
        }

        private Subscription FindByUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            foreach (var s in _state.Subscriptions)
                if (string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        private async void UpdateAllSubscriptions()
        {
            if (_state.Subscriptions.Count == 0)
            {
                MessageBox.Show("还没有导入任何订阅。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!TryBeginBusy("状态：正在更新订阅…")) return;
            AppendLog("开始更新 " + _state.Subscriptions.Count + " 个订阅...");
            var ok = 0;
            var fail = new StringBuilder();
            var total = 0;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var sub in new List<Subscription>(_state.Subscriptions))
                    {
                        try
                        {
                            SubscriptionFetcher.Fetch(sub);
                            ok++;
                            total += sub.Nodes.Count;
                        }
                        catch (Exception ex)
                        {
                            fail.AppendLine("  " + sub.Name + "：" + ex.Message);
                        }
                    }
                });

                // 统一重建节点列表：订阅带来的节点全部替换，手动添加的节点保留。
                RebuildNodesFromSubscriptions();
                if (_state.DedupeOnImport) SubscriptionManagerForm.Dedupe(_state);
                _state.Save();
                RefreshGrid();
                AppendLog("订阅更新完成：成功 " + ok + " 个，共 " + total + " 个节点。");
                if (fail.Length > 0) AppendLog("失败的订阅：\n" + fail);
                MessageBox.Show("更新完成：成功 " + ok + " 个订阅，共 " + total + " 个节点。" +
                                (fail.Length > 0 ? "\n\n部分订阅失败，详见日志。" : ""),
                    "更新订阅", MessageBoxButtons.OK,
                    fail.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("更新订阅失败：" + ex.Message);
                MessageBox.Show("更新订阅失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndBusy(); }
        }

        /// <summary>按当前订阅列表重建节点：订阅节点全部替换，手动添加的节点保留。</summary>
        private void RebuildNodesFromSubscriptions()
        {
            var manual = new List<Node>();
            foreach (var n in _state.Nodes)
                if (string.IsNullOrEmpty(n.SubscriptionId)) manual.Add(n);

            _state.Nodes.Clear();
            _state.Nodes.AddRange(manual);
            foreach (var sub in _state.Subscriptions)
                SubscriptionManagerForm.ApplyNodesTo(_state, sub);
        }

        private void OpenSubscriptionManager()
        {
            using (var dlg = new SubscriptionManagerForm(_state, () => RefreshGrid()))
                dlg.ShowDialog(this);
        }

        private void ImportFromClipboard()
        {
            string text = "";
            try { text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("剪贴板是空的，或无法读取。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ImportRawText(text);
        }

        private void ImportRawText(string raw)
        {
            raw = raw.Trim();
            var added = 0;

            // 单条链接
            var single = V2rayNParser.ParseLink(raw);
            if (single != null)
            {
                single.SourceName = "手动";
                _state.Nodes.Add(single);
                added = 1;
            }
            else
            {
                var parser = SubscriptionParserFactory.Detect(raw);
                if (parser == null)
                {
                    MessageBox.Show("剪贴板内容既不是可识别的单条链接，也不是 v2rayN / Clash / SIP008 订阅。",
                        "无法识别", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var sub = new Subscription { Name = "剪贴板导入", Url = "" };
                var nodes = parser.Parse(raw);
                foreach (var n in nodes) { n.SourceName = sub.Name; n.SubscriptionId = ""; }
                foreach (var n in nodes) _state.Nodes.Add(n);
                added = nodes.Count;
            }

            if (_state.DedupeOnImport)
            {
                var removed = SubscriptionManagerForm.Dedupe(_state);
                added -= removed;
            }
            _state.Save();
            RefreshGrid();
            AppendLog("从剪贴板导入 " + added + " 个节点。");
            if (added > 0)
                MessageBox.Show("已导入 " + added + " 个节点。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AddNode()
        {
            using (var dlg = new InputDialog("添加节点", "粘贴单条链接 (vmess:// / vless:// / trojan:// / ss:// / hysteria2://)："))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var node = V2rayNParser.ParseLink(dlg.Value.Trim());
                    if (node == null) { MessageBox.Show("无法解析该链接。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    node.SourceName = "手动";
                    node.SubscriptionId = "";
                    _state.Nodes.Add(node);
                    if (_state.DedupeOnImport) SubscriptionManagerForm.Dedupe(_state);
                    _state.Save();
                    RefreshGrid();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("解析失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CopyRawLinks()
        {
            var nodes = SelectedNodes();
            if (nodes.Count == 0) { MessageBox.Show("请先选中要复制的节点。", "提示"); return; }

            var sb = new StringBuilder();
            var missing = 0;
            foreach (var n in nodes)
            {
                if (string.IsNullOrEmpty(n.RawLink)) { missing++; continue; }
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(n.RawLink);
            }
            if (sb.Length == 0)
            {
                MessageBox.Show("选中的节点没有保存原始链接（订阅更新后重新解析的节点才会有）。",
                    "无法复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
            AppendLog("已复制 " + (nodes.Count - missing) + " 条链接到剪贴板。");
        }

        private void DedupeNodes()
        {
            var removed = SubscriptionManagerForm.Dedupe(_state);
            _state.Save();
            RefreshGrid();
            MessageBox.Show(removed > 0 ? "已去除 " + removed + " 个重复节点。" : "没有发现重复节点。",
                "去重", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ClearLatency()
        {
            foreach (var n in _state.Nodes) n.LatencyMs = -1;
            _state.Save();
            RefreshGrid();
        }

        private List<Node> SelectedNodes()
        {
            var list = new List<Node>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                var i = row.Index;
                if (i >= 0 && i < _view.Count) list.Add(_view[i]);
            }
            return list;
        }

        private void TestLatency()
        {
            List<Node> targets = _grid.SelectedRows.Count > 0
                ? SelectedNodes()
                : new List<Node>(_view);
            if (targets.Count == 0) return;

            var indices = new List<int>();
            var tcpTargets = new List<Node>();
            var quicTargets = new List<Node>();
            foreach (var n in targets)
            {
                int i = _view.IndexOf(n);
                if (i >= 0) indices.Add(i);
                // Hysteria2 跑在 QUIC/UDP 上，不在节点端口监听 TCP，用 TCP 测只会得到假的“超时”
                if (n.Type == NodeType.Hysteria2) quicTargets.Add(n);
                else tcpTargets.Add(n);
            }
            lock (_testingLock) { _testingRows.Clear(); _testingRows.UnionWith(indices); }
            if (_testBtn != null && !_testBtn.IsDisposed) _testBtn.Enabled = false;
            _spinFrame = 0;
            string glyph0 = new[] { "◐", "◓", "◑", "◒" }[0];
            foreach (int i in indices)
                if (i >= 0 && i < _grid.Rows.Count)
                    _grid.Rows[i].Cells["latency"].Value = glyph0;
            if (_spinTimer != null) _spinTimer.Start();
            SetStatus("状态：延迟测试中…");
            AppendLog("开始测试延迟（" + targets.Count + " 个节点）...");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Parallel.ForEach(tcpTargets, new ParallelOptions { MaxDegreeOfParallelism = 16 }, n =>
                {
                    int ms = -1;
                    try
                    {
                        using (var c = new TcpClient())
                        {
                            var sw = Stopwatch.StartNew();
                            var ar = c.BeginConnect(n.Address, n.Port, null, null);
                            if (ar.AsyncWaitHandle.WaitOne(2000))
                            {
                                c.EndConnect(ar);
                                sw.Stop();
                                ms = (int)sw.ElapsedMilliseconds;
                            }
                        }
                    }
                    catch { }
                    n.LatencyMs = ms;
                    SetLatencyCell(_view.IndexOf(n), ms < 0 ? "超时" : ms + " ms");
                    lock (_testingLock) _testingRows.Remove(_view.IndexOf(n));
                });

                // QUIC 类节点：顺序做真实延迟测试（每个都要临时拉起一个内核，不能并发）
                foreach (var n in quicTargets)
                {
                    int idx = _view.IndexOf(n);
                    AppendLog("真实延迟测试：" + n.Remarks + "（Hysteria2/QUIC）...");
                    var coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir);
                    int ms = RealLatencyTester.Test(_state.Core, n, coreDir, 8000, s => AppendLog("  " + s));
                    n.LatencyMs = ms;
                    SetLatencyCell(idx, ms < 0 ? "失败" : ms + " ms");
                    lock (_testingLock) _testingRows.Remove(idx);
                }

                if (!_grid.IsDisposed)
                {
                    _grid.Invoke((Action)(() =>
                    {
                        if (_spinTimer != null) _spinTimer.Stop();
                        lock (_testingLock) _testingRows.Clear();
                        if (_testBtn != null && !_testBtn.IsDisposed) _testBtn.Enabled = true;
                        UpdateStatus();
                    }));
                }
                _state.Save();
                AppendLog("延迟测试完成。");
            });
        }

        private void SetLatencyCell(int idx, string text)
        {
            if (idx < 0 || _grid.IsDisposed || !_grid.IsHandleCreated) return;
            try
            {
                _grid.Invoke((Action)(() =>
                {
                    if (idx >= 0 && idx < _grid.Rows.Count) _grid.Rows[idx].Cells["latency"].Value = text;
                }));
            }
            catch { }
        }

        private void DeleteSelected()
        {
            var nodes = SelectedNodes();
            if (nodes.Count == 0)
            {
                MessageBox.Show("请先选中要删除的节点（按住 Ctrl / Shift 可多选，或用「全选」）。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确认删除选中的 " + nodes.Count + " 个节点？", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var n in nodes) _state.Nodes.Remove(n);
            _state.Save();
            RefreshGrid();
            AppendLog("已删除 " + nodes.Count + " 个节点。");
        }

        // ---------- 内核控制 ----------

        private string PidFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir, "xray.pid");
        }

        private void StartProxy(bool quiet)
        {
            if (_busy)
            {
                if (!quiet) MessageBox.Show("正在执行后台操作，请稍候。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var node = SelectedNode();
            if (node == null)
            {
                if (!quiet) MessageBox.Show("请先在列表中选择一个节点。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 直连模式：停止本程序接管并恢复原系统代理设置，不启动内核。
            // 旧版本这里照样起内核、随后又把系统代理清掉，结果是内核空转。
            if (_state.Mode == ProxyMode.Direct)
            {
                StopCoreOnly();
                SystemProxy.SetProxy(ProxyMode.Direct);
                _currentNode = null;
                UpdateStatus();
                AppendLog("已切换到直连模式，未启动内核，并已恢复原系统代理设置。");
                if (!quiet) MessageBox.Show("直连模式：本程序已停止接管系统代理，不会启动内核。", "直连模式",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var spec = CoreRegistry.Of(_state.Core);
            if (!spec.Win7Usable && CoreRegistry.IsWindows7OrEarlier())
            {
                var message = spec.Name + " 没有可在 Windows 7 上运行的官方构建，请使用 Xray 内核。";
                AppendLog("无法启动：" + message);
                if (!quiet) MessageBox.Show(message, "系统不兼容", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir);
            var coreExe = Path.Combine(coreDir, spec.ExeName);
            if (!File.Exists(coreExe))
            {
                if (quiet) { AppendLog("未找到 core/" + spec.ExeName + "，跳过自动启动。"); return; }
                var ask = MessageBox.Show(
                    "未找到 core\\" + spec.ExeName + "。\n\n现在下载并安装 " + spec.Name + " 内核吗？",
                    "缺少内核", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) DownloadCoreAsync(spec.Kind);
                return;
            }

            // 内核能力检查：h2 在 Xray 上、REALITY 在 V2Ray 上都属于"写了必然启动失败"，
            // 在这里拦下来并给出可操作的建议，好过让用户对着"启动失败"四个字发呆
            var unsupported = spec.ReasonUnsupported(node);
            if (unsupported != null)
            {
                AppendLog("无法启动：" + unsupported);
                if (!quiet)
                    MessageBox.Show(unsupported + "\n\n请在工具栏的「内核」下拉框里换一个内核，或者换一个节点。",
                        "当前内核不支持该节点", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(coreDir);
                var cfgPath = Path.Combine(coreDir, CoreConstants.ConfigFile);
                File.WriteAllText(cfgPath, CoreConfigFactory.BuildJson(spec.Kind, node, _state.Mode, coreDir));

                // 已知的内核不兼容（比如 h2 传输在新版 xray 里已被移除）提前说清楚，
                // 否则用户只会看到"内核启动失败"四个字，无从下手
                foreach (var w in NodeCompat.Warnings(node, spec))
                    AppendLog("兼容性提示：" + w);

                // sing-box 没有 geo 数据时规则会退化，说清楚
                if (spec.NeedsGeoDb && !CoreConfigFactory.HasSingboxGeo(coreDir))
                    AppendLog("提示：缺少 sing-box 的 .srs 规则集，国内外分流会退化；请更新内核以补齐规则文件。");

                StopCoreOnly();

                // 两个入站端口任一被占用都不能继续。只发警告会把其他程序的监听端口
                // 误判成当前内核启动成功，随后错误接管系统代理。
                if (CoreProcess.IsPortListening(CoreConstants.SocksPort, 300) ||
                    CoreProcess.IsPortListening(CoreConstants.HttpPort, 300))
                    throw new ProxyCoreException("本地端口 " + CoreConstants.SocksPort + " 或 " +
                        CoreConstants.HttpPort + " 已被其他程序占用，请先退出冲突的代理软件。");

                var core = new CoreProcess();
                core.LogReceived += s => AppendLog(s);
                core.Exited += () => OnCoreExited(core);
                lock (_coreLock) _core = core;
                core.Start(coreExe, cfgPath, coreDir, PidFilePath());

                bool up = false;
                for (int i = 0; i < 25; i++)
                {
                    if (!core.IsRunning) break;
                    if (CoreProcess.IsPortListening(CoreConstants.SocksPort, 150) &&
                        CoreProcess.IsPortListening(CoreConstants.HttpPort, 150))
                    {
                        up = true;
                        break;
                    }
                    Thread.Sleep(200);
                }

                if (!up)
                {
                    AppendLog("内核启动失败：本地入站端口未在 5 秒内全部就绪，请查看日志。");
                    StopCoreOnly();
                    SystemProxy.Restore();
                    _currentNode = null;
                    UpdateStatus();
                    if (!quiet)
                        MessageBox.Show("内核没有成功启动，系统代理未被接管。请查看下方日志。",
                            "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 与退出回调串行化：若进程在健康检查后立刻退出，回调会在本段结束后
                // 恢复系统代理；若它已经退出，则这里不会再接管代理。
                lock (_coreLock)
                {
                    if (!ReferenceEquals(core, _core) || !core.IsRunning)
                        throw new ProxyCoreException("内核在启动完成前已经退出，请查看内核日志。");
                    SystemProxy.SetProxy(_state.Mode);
                    _currentNode = node;
                }
                UpdateStatus();
                AppendLog("内核已启动：" + node.Remarks + "（" + _mode.Text + "模式）");
            }
            catch (ProxyCoreException ex)
            {
                if (!quiet) MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AppendLog("启动失败：" + ex.Message);
                StopCoreOnly();
                SystemProxy.Restore();
                _currentNode = null;
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AppendLog("启动失败：" + ex.Message);
                StopCoreOnly();
                SystemProxy.Restore();
                _currentNode = null;
            }
        }

        private void OnCoreExited(CoreProcess core)
        {
            lock (_coreLock)
            {
                // 主动停止时 _core 已被换掉/置空，这时不该当成"内核崩了"
                if (!ReferenceEquals(core, _core)) return;
                _core = null;
                _currentNode = null;
            }

            // 内核自己挂了（配置错误、端口冲突等）时，必须把系统代理撤掉，
            // 否则用户会陷入"代理开着但没人干活"的断网状态。
            SystemProxy.Restore();
            UpdateStatus();
            AppendLog("内核进程已退出，已恢复接管前的系统代理设置。");
        }

        private void StopCoreOnly()
        {
            CoreProcess core;
            lock (_coreLock)
            {
                core = _core;
                _core = null;
            }
            if (core == null) return;
            try { core.Stop(); core.Dispose(); } catch { }
        }

        private void StopProxy()
        {
            StopCoreOnly();
            SystemProxy.Restore();
            _currentNode = null;
            UpdateStatus();
            AppendLog("已停止，已恢复接管前的系统代理设置。");
        }

        private void ProbeCoreVersionAsync()
        {
            var spec = CoreRegistry.Of(_state.Core);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir, spec.ExeName);
                var v = CoreProcess.GetCoreVersion(path);
                _coreVersion = v ?? "";
                BeginInvoke((Action)(() =>
                {
                    UpdateStatus();
                    AppendLog(string.IsNullOrEmpty(v)
                        ? "未检测到 " + spec.Name + " 内核（core\\" + spec.ExeName + "）。"
                        : "检测到内核：" + spec.Name + " " + v);
                }));
            });
        }

        /// <summary>切换内核：更新状态与版本显示，运行中则换内核重启。</summary>
        private void OnCoreKindChanged()
        {
            _state.Core = (CoreKind)_coreBox.SelectedIndex;
            _state.Save();

            var spec = CoreRegistry.Of(_state.Core);
            AppendLog("已切换内核：" + spec.Name + "（core\\" + spec.ExeName + "）");
            if (!spec.Win7Usable)
                AppendLog("提示：" + spec.Name + " 没有 Windows 7 可用的构建，仅适用于 Windows 10/11。");

            ProbeCoreVersionAsync();

            if (_core != null && _currentNode != null)
            {
                AppendLog("正在用 " + spec.Name + " 重启...");
                StartProxy(true);
            }
            else
            {
                UpdateStatus();
            }
        }

        /// <summary>后台下载并安装指定内核，日志实时打到界面上。</summary>
        private async void DownloadCoreAsync(CoreKind kind, bool restartAfter = false)
        {
            var spec = CoreRegistry.Of(kind);
            if (!spec.Win7Usable && CoreRegistry.IsWindows7OrEarlier())
            {
                MessageBox.Show(spec.Name + " 没有可在 Windows 7 上运行的官方构建，请使用 Xray 内核。",
                    "系统不兼容", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!TryBeginBusy("状态：正在下载内核…")) return;
            var coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir);
            var reconnect = restartAfter && _core != null && _currentNode != null;
            if (reconnect) StopProxy();
            AppendLog("开始下载 " + spec.Name + " 内核，请稍候（日志会实时显示）...");

            var ok = false;
            try
            {
                ok = await Task.Run(() => CoreDownloader.Install(kind, coreDir, AppendLog));
                if (ok)
                {
                    AppendLog(spec.Name + " 内核安装完成。");
                    ProbeCoreVersionAsync();
                }
                else
                {
                    AppendLog(spec.Name + " 内核安装失败。可手动从 GitHub Releases 下载后把 "
                              + spec.ExeName + " 放进 core\\ 目录。");
                }
            }
            catch (Exception ex)
            {
                AppendLog("下载内核时出错：" + ex.Message);
            }
            finally { EndBusy(); }

            if (ok && reconnect)
            {
                AppendLog("内核更新完成，正在重新连接...");
                StartProxy(true);
            }
        }

        private void UpdateCore()
        {
            var spec = CoreRegistry.Of(_state.Core);
            var r = MessageBox.Show("将下载并覆盖当前的 " + spec.Name + " 内核（core\\" + spec.ExeName + "）。\n\n继续吗？",
                "更新内核", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            DownloadCoreAsync(spec.Kind, _core != null && _currentNode != null);
        }

        private void ShowForm()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
        }

        private void RealExit()
        {
            _realExit = true;
            Close();
        }
    }
}
