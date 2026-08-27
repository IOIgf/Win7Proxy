using System.Windows.Forms;

namespace Win7Proxy
{
    /// <summary>简单文本输入对话框（用于添加单条节点链接）。</summary>
    public class InputDialog : Form
    {
        private readonly TextBox _box;
        public string Value => _box.Text;

        public InputDialog(string title, string prompt)
        {
            Text = title;
            Width = 520; Height = 160;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var lbl = new Label { Left = 12, Top = 14, Width = 480, Text = prompt };
            _box = new TextBox { Left = 12, Top = 40, Width = 480 };
            var ok = new Button { Text = "确定", Left = 320, Top = 86, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 410, Top = 86, DialogResult = DialogResult.Cancel };

            Controls.Add(lbl); Controls.Add(_box); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}
