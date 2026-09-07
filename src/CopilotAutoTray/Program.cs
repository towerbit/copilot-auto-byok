using System;
using System.Threading;
using System.Windows.Forms;

namespace copilot_auto_tray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 检查是否已有实例在运行
            bool createdNew;
            var mutex = new Mutex(true, "CopilotAutoTray_SingleInstance", out createdNew);
            if (!createdNew)
            {
                var result = MessageBox.Show("CopilotAutoTray 已有一个实例在运行，是否需要打开窗口？", "提示",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (result == DialogResult.OK)
                {
                    CopilotTrayApp.OpenBrowser();
                }
                Application.Exit();
                return;
            }

            Application.Run(new CopilotTrayApp());
        }
    }
}