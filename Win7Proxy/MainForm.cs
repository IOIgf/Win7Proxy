using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
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
        private readonly AppState _state;
        private CoreProcess _core;
        private readonly DataGridView _grid;
        private readonly TextBox _log;
        private readonly Label _status;
        private readonly Label _nodeLabel;
        private readonly ComboBox _mode;
        private readonly NotifyIcon _tray;
        private int _sortCol = -1;
        private bool _sortAsc = true;
        private bool _realExit = false;
        private Button _testBtn;
        private readonly HashSet<int> _testingRows = new HashSet<int>();
        private readonly object _testingLock = new object();
        private System.Windows.Forms.Timer _spinTimer;
        private int _spinFrame;

        public MainForm()
        {
            _state = AppState.Load();

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            Icon appIcon = null;
            try { if (File.Exists(iconPath)) appIcon = new Icon(iconPath); }
            catch { appIcon = null; }

            Text = "Win7Proxy";
            Icon = appIcon ?? SystemIcons.Application;
            Width = 820; Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(244, 246, 249);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(10, 8, 10, 8),
                BackColor = Color.FromArgb(244, 246, 249)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52F));
            Controls.Add(layout);

            var title = new Label
            {
                Text = "Win7Proxy  ·  Windows 7 代理客户端",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 125, 218),
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 0, 6)
            };
            layout.Controls.Add(title, 0, 0);

            var toolPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
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
                    Margin = new Padding(2, 0, 2, 0),
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
                toolPanel.Controls.Add(b);
                return b;
            }
            Btn("导入订阅", (s, e) => ImportSubscription());
            Btn("添加节点", (s, e) => AddNode());
            _testBtn = Btn("测试延迟", (s, e) => TestLatency());
            Btn("全选", (s, e) => _grid.SelectAll());
            Btn("删除选中", (s, e) => DeleteSelected());
            Btn("启动", (s, e) => StartProxy(), Color.FromArgb(46, 160, 67));
            Btn("停止", (s, e) => StopProxy(), Color.FromArgb(208, 64, 64));
            Btn("更新内核", (s, e) => UpdateCore());

            var modeLabel = new Label
            {
                Text = "模式:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Margin = new Padding(14, 7, 2, 0)
            };
            toolPanel.Controls.Add(modeLabel);
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F) };
            _mode.Items.AddRange(new[] { "全局", "规则", "直连" });
            _mode.SelectedIndex = (int)_state.Mode;
            _mode.SelectedIndexChanged += (s, e) =>
            {
                _state.Mode = (ProxyMode)_mode.SelectedIndex;
                _state.Save();
            };
            toolPanel.Controls.Add(_mode);
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
            layout.Controls.Add(toolPanel, 0, 1);

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
            _grid.Columns["remarks"].FillWeight = 38;
            _grid.Columns["type"].FillWeight = 12;
            _grid.Columns["addr"].FillWeight = 32;
            _grid.Columns["latency"].FillWeight = 10;
            _grid.Columns["source"].FillWeight = 18;
            foreach (DataGridViewColumn c in _grid.Columns)
                c.SortMode = DataGridViewColumnSortMode.Programmatic;
            _grid.SelectionChanged += (s, e) =>
            {
                if (_grid.CurrentRow != null) { _state.SelectedIndex = _grid.CurrentRow.Index; _state.Save(); }
                UpdateNodeLabel();
            };
            _grid.ColumnHeaderMouseClick += (s, e) => SortByColumn(e.ColumnIndex);
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _state.Nodes.Count)
                {
                    _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
                    StartProxy();
                }
            };
            layout.Controls.Add(_grid, 0, 3);

            _nodeLabel = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 125, 218),
                Text = "当前节点：未选择",
                Padding = new Padding(2, 4, 2, 2)
            };
            layout.Controls.Add(_nodeLabel, 0, 2);

            _status = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(80, 84, 92),
                Text = "状态：已停止",
                Padding = new Padding(2, 2, 2, 6)
            };
            layout.Controls.Add(_status, 0, 4);

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 251, 252),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9)
            };
            layout.Controls.Add(_log, 0, 5);

            _tray = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
                Text = "Win7Proxy",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("显示主窗口", null, (s, e) => ShowForm());
            _tray.ContextMenuStrip.Items.Add("启动", null, (s, e) => StartProxy());
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
                try { _grid.Rows[_state.SelectedIndex].Selected = true; } catch { }
            UpdateNodeLabel();
        }

        private Node SelectedNode()
        {
            if (_grid.CurrentRow == null) return null;
            var i = _grid.CurrentRow.Index;
            return (i >= 0 && i < _state.Nodes.Count) ? _state.Nodes[i] : null;
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
            // 记录当前视图状态（按节点引用保存，排序/刷新后仍能对应到正确的行）
            int first = _grid.FirstDisplayedScrollingRowIndex;
            var selNodes = SelectedNodes();
            var curNode = (_grid.CurrentRow != null && _grid.CurrentRow.Index < _state.Nodes.Count)
                ? _state.Nodes[_grid.CurrentRow.Index] : null;

            _grid.Rows.Clear();
            foreach (var n in _state.Nodes)
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
                int idx = _state.Nodes.IndexOf(n);
                if (idx >= 0) { _grid.Rows[idx].Selected = true; if (!focusIdx.HasValue) focusIdx = idx; }
            }
            if (curNode != null)
            {
                int idx = _state.Nodes.IndexOf(curNode);
                if (idx >= 0) { _grid.CurrentCell = _grid.Rows[idx].Cells[0]; focusIdx = idx; }
            }
            // 恢复滚动位置：优先让焦点行可见，否则回到原先的顶部行
            int scrollTo = focusIdx.HasValue ? focusIdx.Value : (first >= 0 ? first : 0);
            if (_grid.Rows.Count > 0)
                _grid.FirstDisplayedScrollingRowIndex = Math.Min(scrollTo, _grid.Rows.Count - 1);
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
            if (_log.IsDisposed) return;
            if (_log.InvokeRequired) { _log.BeginInvoke((Action<string>)AppendLog, line); return; }
            _log.AppendText(line + Environment.NewLine);
            if (_log.Lines.Length > 2000)
                _log.Text = string.Join(Environment.NewLine, _log.Lines, _log.Lines.Length - 1000, 1000);
        }

        private void SetStatus(string text)
        {
            if (_status.InvokeRequired) { _status.BeginInvoke((Action<string>)SetStatus, text); return; }
            _status.Text = text;
        }

        private void ImportSubscription()
        {
            using (var dlg = new SubscriptionForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var count = dlg.Fetch();
                    _state.Subscriptions.Add(dlg.Subscription);
                    _state.Nodes.AddRange(dlg.Subscription.Nodes);
                    _state.Save();
                    RefreshGrid();
                    MessageBox.Show("成功导入 " + count + " 个节点。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (ProxyCoreException ex)
                {
                    MessageBox.Show("订阅拉取失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void AddNode()
        {
            using (var dlg = new InputDialog("添加节点", "粘贴单条链接 (vmess:// / vless:// / trojan:// / ss://)："))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var node = V2rayNParser.ParseLink(dlg.Value.Trim());
                    if (node == null) { MessageBox.Show("无法解析该链接。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    node.SourceName = "手动";
                    _state.Nodes.Add(node);
                    _state.Save();
                    RefreshGrid();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("解析失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private List<Node> SelectedNodes()
        {
            var list = new List<Node>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                var i = row.Index;
                if (i >= 0 && i < _state.Nodes.Count) list.Add(_state.Nodes[i]);
            }
            return list;
        }

        private void TestLatency()
        {
            List<Node> targets = _grid.SelectedRows.Count > 0
                ? SelectedNodes()
                : new List<Node>(_state.Nodes);
            if (targets.Count == 0) return;

            var indices = new List<int>();
            foreach (var n in targets)
            {
                int i = _state.Nodes.IndexOf(n);
                if (i >= 0) indices.Add(i);
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
                Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 16 }, n =>
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
                    int idx = _state.Nodes.IndexOf(n);
                    if (!_grid.IsDisposed && _grid.IsHandleCreated && idx >= 0 && idx < _grid.Rows.Count)
                    {
                        _grid.Invoke((Action)(() =>
                        {
                            _grid.Rows[idx].Cells["latency"].Value = ms < 0 ? "超时" : ms + " ms";
                        }));
                    }
                    lock (_testingLock) _testingRows.Remove(idx);
                });

                if (!_grid.IsDisposed)
                {
                    _grid.Invoke((Action)(() =>
                    {
                        if (_spinTimer != null) _spinTimer.Stop();
                        lock (_testingLock) _testingRows.Clear();
                        if (_testBtn != null && !_testBtn.IsDisposed) _testBtn.Enabled = true;
                        SetStatus(_core != null ? "状态：运行中" : "状态：已停止");
                    }));
                }
                AppendLog("延迟测试完成。");
            });
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

        private void StartProxy()
        {
            var node = SelectedNode();
            if (node == null) { MessageBox.Show("请先在列表中选择一个节点。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                var coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreConstants.CoreDir);
                Directory.CreateDirectory(coreDir);
                var cfgPath = Path.Combine(coreDir, CoreConstants.ConfigFile);
                File.WriteAllText(cfgPath, XrayConfigBuilder.Build(node, _state.Mode).ToJson());

                var xrayPath = Path.Combine(coreDir, CoreConstants.XrayExe);
                if (!File.Exists(xrayPath))
                {
                    MessageBox.Show("未找到 core/xray.exe。请运行 fetch-core.bat 下载 xray-win7 内核后重试。",
                        "缺少内核", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (_core != null) { try { _core.Stop(); _core.Dispose(); } catch { } _core = null; }
                _core = new CoreProcess();
                _core.LogReceived += s => AppendLog(s);
                _core.Exited += () => SetStatus("状态：已停止（内核退出）");
                _core.Start(xrayPath, cfgPath, coreDir);

                bool up = false;
                for (int i = 0; i < 20; i++)
                {
                    if (CoreProcess.IsPortListening(CoreConstants.SocksPort, 300)) { up = true; break; }
                    Thread.Sleep(200);
                }
                SystemProxy.SetProxy(_state.Mode, null);
                SetStatus("状态：运行中 — " + node.Remarks);
                if (!up) MessageBox.Show("已发送启动命令，但本机端口尚未监听，请查看下方日志。",
                    "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (ProxyCoreException ex)
            {
                MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopProxy()
        {
            if (_core != null)
            {
                try { _core.Stop(); _core.Dispose(); } catch { }
                _core = null;
            }
            SystemProxy.Clear();
            SetStatus("状态：已停止");
        }

        private void UpdateCore()
        {
            try
            {
                var bat = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fetch-core.bat");
                Process.Start(new ProcessStartInfo(bat)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动 fetch-core.bat，请手动以管理员或普通用户运行它。\n" + ex.Message,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
