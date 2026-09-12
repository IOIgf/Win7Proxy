using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>
    /// 订阅管理：查看已导入的订阅、更新/编辑/删除，以及手动新增订阅。
    /// 删除订阅会连带删除它带来的节点（按 SubscriptionId 精确匹配）。
    /// </summary>
    public class SubscriptionManagerForm : Form
    {
        private sealed class Item
        {
            public Subscription Sub;
            public override string ToString()
            {
                var when = Sub.Updated == DateTime.MinValue ? "从未更新" : Sub.Updated.ToString("MM-dd HH:mm");
                return string.Format("{0}  [{1} 个节点 · {2}]  {3}", Sub.Name, Sub.Nodes.Count, when, Sub.Url);
            }
        }

        private readonly AppState _state;
        private readonly Action _onChanged;
        private readonly ListBox _list;

        public SubscriptionManagerForm(AppState state, Action onChanged)
        {
            HiDpi.ApplyTo(this);   // 必须在设置 Size / 创建控件之前

            _state = state;
            _onChanged = onChanged;

            Text = "订阅管理";
            Width = 720; Height = 400;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(244, 246, 249);

            _list = new ListBox
            {
                Left = 12, Top = 12, Width = 680, Height = 290,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F)
            };
            Controls.Add(_list);

            int y = 314;
            AddButton("新增", 12, y, 80, (s, e) => AddNew());
            AddButton("更新", 100, y, 80, (s, e) => UpdateSelected());
            AddButton("编辑", 188, y, 80, (s, e) => EditSelected());
            AddButton("删除", 276, y, 80, (s, e) => DeleteSelected());
            AddButton("关闭", 612, y, 80, (s, e) => Close());

            Reload();

            HiDpi.ScaleForDpi(this);   // 必须在所有控件创建完之后
        }

        private Button AddButton(string text, int left, int top, int width, EventHandler handler)
        {
            var b = new Button
            {
                Text = text, Left = left, Top = top, Width = width, Height = 30,
                FlatStyle = FlatStyle.Flat, BackColor = Color.White
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += handler;
            Controls.Add(b);
            return b;
        }

        private void Reload()
        {
            _list.Items.Clear();
            foreach (var s in _state.Subscriptions) _list.Items.Add(new Item { Sub = s });
            if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
        }

        private Subscription Selected()
        {
            var it = _list.SelectedItem as Item;
            return it?.Sub;
        }

        private void AddNew()
        {
            using (var dlg = new SubscriptionForm(null, false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var count = dlg.Fetch();
                    _state.Subscriptions.Add(dlg.Subscription);
                    ApplyNodes(dlg.Subscription);
                    MessageBox.Show("已导入 " + count + " 个节点。", "完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (ProxyCore.ProxyCoreException ex)
                {
                    MessageBox.Show("订阅拉取失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            Reload();
            _onChanged?.Invoke();
        }

        private void UpdateSelected()
        {
            var sub = Selected();
            if (sub == null) { MessageBox.Show("请先选择一个订阅。", "提示"); return; }
            try
            {
                var count = ProxyCore.SubscriptionFetcher.Fetch(sub).Nodes.Count;
                ApplyNodes(sub);
                MessageBox.Show("已更新，共 " + count + " 个节点。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (ProxyCore.ProxyCoreException ex)
            {
                MessageBox.Show("更新失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Reload();
            _onChanged?.Invoke();
        }

        private void EditSelected()
        {
            var sub = Selected();
            if (sub == null) { MessageBox.Show("请先选择一个订阅。", "提示"); return; }
            using (var dlg = new SubscriptionForm(sub, true))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var count = dlg.Fetch();
                    ApplyNodes(sub);
                    MessageBox.Show("已保存并更新，共 " + count + " 个节点。", "完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (ProxyCore.ProxyCoreException ex)
                {
                    MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            Reload();
            _onChanged?.Invoke();
        }

        private void DeleteSelected()
        {
            var sub = Selected();
            if (sub == null) { MessageBox.Show("请先选择一个订阅。", "提示"); return; }
            if (MessageBox.Show("删除订阅「" + sub.Name + "」以及它带来的 " + sub.Nodes.Count + " 个节点？",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            RemoveNodesOf(sub.Id);
            _state.Subscriptions.Remove(sub);
            _state.Save();
            Reload();
            _onChanged?.Invoke();
        }

        /// <summary>
        /// 用订阅拉取到的新节点替换旧节点，并尽量把旧节点已测得的延迟继承过来，
        /// 避免每次更新订阅后延迟列全部变成 "-"。
        /// </summary>
        public static void ApplyNodesTo(AppState state, Subscription sub)
        {
            var oldLatency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in state.Nodes)
                if (n.SubscriptionId == sub.Id && n.LatencyMs >= 0)
                    oldLatency[ProxyCore.NetUtil.Fingerprint(n)] = n.LatencyMs;

            RemoveNodesFrom(state, sub.Id);

            foreach (var n in sub.Nodes)
            {
                int ms;
                if (n.LatencyMs < 0 && oldLatency.TryGetValue(ProxyCore.NetUtil.Fingerprint(n), out ms))
                    n.LatencyMs = ms;
                state.Nodes.Add(n);
            }
        }

        public static void RemoveNodesFrom(AppState state, string subscriptionId)
        {
            if (string.IsNullOrEmpty(subscriptionId)) return;
            state.Nodes.RemoveAll(n => n.SubscriptionId == subscriptionId);
        }

        private void ApplyNodes(Subscription sub)
        {
            ApplyNodesTo(_state, sub);
            if (_state.DedupeOnImport) Dedupe(_state);
            _state.Save();
        }

        private void RemoveNodesOf(string id)
        {
            RemoveNodesFrom(_state, id);
        }

        /// <summary>按指纹去掉重复节点，保留先出现的那个。</summary>
        public static int Dedupe(AppState state)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var removed = 0;
            for (int i = state.Nodes.Count - 1; i >= 0; i--)
            {
                var fp = ProxyCore.NetUtil.Fingerprint(state.Nodes[i]);
                if (!seen.Add(fp))
                {
                    state.Nodes.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }
    }
}
