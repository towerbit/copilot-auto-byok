using System;
using System.Threading;
using System.Windows.Forms;

namespace copilot_auto_tray
{
    static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            #region 检查是否已有实例在运行
            bool createdNew;
            // 创建一个跨会话的全局命名的互斥体
            _mutex = new Mutex(true, "Global\\CopilotAutoTray_SingleInstance", out createdNew);
            if (!createdNew)
            {
                CopilotTrayApp.OpenBrowser();
                Application.Exit();
                return;
            }
            #endregion

            Application.Run(new CopilotTrayApp());
        }
    }
}