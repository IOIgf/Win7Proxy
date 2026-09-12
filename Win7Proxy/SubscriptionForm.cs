using System;
using System.Windows.Forms;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>
    /// 订阅对话框：输入 URL 与名称，可选格式与"跳过证书校验"。
    /// 传入已有 Subscription 时为编辑模式，保留其 Id，从而支持「重新导入替换旧节点」。
    /// </summary>
    public class SubscriptionForm : Form
    {
        private readonly TextBox _url;
        private readonly TextBox _name;
        private readonly ComboBox _format;
        private readonly CheckBox _insecure;

        /// <summary>被编辑/新增的订阅对象。编辑模式下由构造函数传入，Fetch() 会就地更新它。</summary>
        public Subscription Subscription { get; private set; }

        public SubscriptionForm(Subscription existing = null, bool editMode = false)
        {
            HiDpi.ApplyTo(this);   // 必须在设置 Size / 创建控件之前

            Text = editMode ? "编辑订阅" : "导入订阅";
            Width = 560; Height = 268;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new System.Drawing.Font("Segoe UI", 9F);

            Controls.Add(new Label { Left = 12, Top = 14, Width = 260, Text = "订阅地址 (URL)：", AutoSize = true });
            _url = new TextBox { Left = 12, Top = 36, Width = 520 };
            Controls.Add(_url);

            Controls.Add(new Label { Left = 12, Top = 70, Width = 260, Text = "备注名称：", AutoSize = true });
            _name = new TextBox { Left = 12, Top = 92, Width = 250 };
            Controls.Add(_name);

            Controls.Add(new Label { Left = 282, Top = 70, Width = 120, Text = "格式：", AutoSize = true });
            _format = new ComboBox
            {
                Left = 282, Top = 92, Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _format.Items.AddRange(new[] { "自动识别", "v2rayN", "Clash", "SIP008" });
            _format.SelectedIndex = 0;
            Controls.Add(_format);

            _insecure = new CheckBox
            {
                Left = 12, Top = 130, Width = 520,
                Text = "跳过 TLS 证书校验（仅当订阅站使用自签名证书时勾选）"
            };
            Controls.Add(_insecure);

            var okText = editMode ? "保存并更新" : "拉取";
            var fetch = new Button { Text = okText, Left = 292, Top = 178, Width = 120, Height = 30, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 420, Top = 178, Width = 110, Height = 30, DialogResult = DialogResult.Cancel };
            Controls.Add(fetch); Controls.Add(cancel);
            AcceptButton = fetch; CancelButton = cancel;

            if (existing != null)
            {
                Subscription = existing;
                _url.Text = existing.Url;
                _name.Text = existing.Name;
                _insecure.Checked = existing.AllowInsecureTls;
                _format.SelectedIndex = (int)existing.Format;
            }

            HiDpi.ScaleForDpi(this);   // 必须在所有控件创建完之后
        }

        /// <summary>拉取并解析订阅；成功返回节点数，失败抛 ProxyCoreException。</summary>
        public int Fetch()
        {
            var url = _url.Text.Trim();
            var name = string.IsNullOrWhiteSpace(_name.Text) ? "订阅" : _name.Text.Trim();

            if (Subscription == null) Subscription = new Subscription();
            Subscription.Name = name;
            Subscription.Url = url;
            Subscription.AllowInsecureTls = _insecure.Checked;
            Subscription.Format = (SubscriptionFormat)_format.SelectedIndex;

            ProxyCore.SubscriptionFetcher.Fetch(Subscription);
            return Subscription.Nodes.Count;
        }
    }
}
