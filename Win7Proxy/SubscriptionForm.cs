using System;
using System.Windows.Forms;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>导入订阅对话框：输入 URL 与名称，拉取并解析。</summary>
    public class SubscriptionForm : Form
    {
        private readonly TextBox _url;
        private readonly TextBox _name;
        public Subscription Subscription { get; private set; }

        public SubscriptionForm()
        {
            Text = "导入订阅";
            Width = 540; Height = 220;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            Controls.Add(new Label { Left = 12, Top = 14, Width = 200, Text = "订阅地址 (URL)： " });
            _url = new TextBox { Left = 12, Top = 38, Width = 500 };
            Controls.Add(_url);

            Controls.Add(new Label { Left = 12, Top = 74, Width = 200, Text = "备注名称：" });
            _name = new TextBox { Left = 12, Top = 98, Width = 500 };
            Controls.Add(_name);

            var fetch = new Button { Text = "拉取", Left = 300, Top = 140, Width = 100, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 412, Top = 140, Width = 100, DialogResult = DialogResult.Cancel };
            Controls.Add(fetch); Controls.Add(cancel);
            AcceptButton = fetch; CancelButton = cancel;
        }

        /// <summary>拉取并解析订阅；成功返回节点数，失败抛 ProxyCoreException。</summary>
        public int Fetch()
        {
            var sub = new Subscription
            {
                Name = string.IsNullOrWhiteSpace(_name.Text) ? "订阅" : _name.Text.Trim(),
                Url = _url.Text.Trim()
            };
            ProxyCore.SubscriptionFetcher.Fetch(sub);
            Subscription = sub;
            return sub.Nodes.Count;
        }
    }
}
