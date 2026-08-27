using System;
using System.Threading;
using System.Windows.Forms;

namespace Win7Proxy
{
    internal static class Program
    {
        private const string MutexName = "Win7ProxySingleInstance_9f3a1c";

        [STAThread]
        private static void Main()
        {
            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Win7Proxy 已在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
