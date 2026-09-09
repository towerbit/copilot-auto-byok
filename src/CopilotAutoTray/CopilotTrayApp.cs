using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace copilot_auto_tray
{
    public class CopilotTrayApp : ApplicationContext
    {
        private readonly string _appTitle = GetAppTitle();
        private NotifyIcon _notifyIcon;

        public CopilotTrayApp()
        {
            InitializeComponent();
            StopCmd();
            StartCmd();
            OpenBrowser();
        }

        private string CmdFilePath
        {
            get 
            {
                var ret = Properties.Settings.Default.cmd;
                if(!Path.IsPathRooted(ret))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    return Path.Combine(baseDir, ret);
                }
                return ret;
            }
        } 

        private static string GetAppTitle()
        {
            object[] attrs = Assembly.GetExecutingAssembly()
                                     .GetCustomAttributes(typeof(AssemblyTitleAttribute), false);
            if (attrs.Length > 0)
            {
                var titleAttr = (AssemblyTitleAttribute)attrs[0];
                return titleAttr.Title;
            }
            return "Copilot Auto Tray";
        }

        private void InitializeComponent()
        {
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("打开窗口", null, (s, e) => OpenBrowser());
            contextMenu.Items.Add("重启服务", null, (s, e) => RestartCmd());
            contextMenu.Items.Add("关于", null, (s, e) => ShowAbout());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("退出", null, (s, e) => ExitApp());

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                BalloonTipTitle = _appTitle,
                Visible = true,
                ContextMenuStrip = contextMenu
            };

            _notifyIcon.DoubleClick += (s, e) => OpenBrowser();
        }

        private void ShowAbout()
        {
            var sbVersion = new System.Text.StringBuilder();

            // 获取 CopilotTray 版本号
            var trayVersion = Assembly.GetExecutingAssembly().GetName().Version;
            sbVersion.AppendLine($"copilot-auto-tray : {trayVersion}");

            // 获取 cmd 程序的路径
            
            if (!string.IsNullOrEmpty(CmdFilePath) &&
                File.Exists(CmdFilePath))
            {
                var filename = Path.GetFileNameWithoutExtension(CmdFilePath);
                sbVersion.Append($"{filename}");
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(CmdFilePath);
                var fileVersion = fileVersionInfo.FileVersion;
                if (!string.IsNullOrEmpty(fileVersion))
                {
                    sbVersion.AppendLine($" : {fileVersion}");
                }
                else
                {
                    sbVersion.AppendLine(" : 未获取到版本号");
                }
            }
            else
            {
                sbVersion.AppendLine("执行程序: 未配置或文件不存在");
            }

            sbVersion.AppendLine();
            sbVersion.AppendLine("访问 https://github.com/towerbit/copilot-auto-byok 了解更多信息");

            _notifyIcon.ShowBalloonTip(6000, "",
                 sbVersion.ToString().Trim(), ToolTipIcon.Info);
        }

        private Icon LoadIcon()
        {
            try
            {
                var resourceName = "copilot_auto_tray.copilot-auto-tray.ico";
                var stream = Assembly.GetExecutingAssembly()
                                     .GetManifestResourceStream(resourceName);
                if (stream != null)
                    return new Icon(stream);
            }
            catch { }

            return CreateDefaultIcon();
        }

        private Icon CreateDefaultIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(0, 120, 212));
                using (var brush = new SolidBrush(Color.White))
                using (var font = new Font("Segoe UI", 14, FontStyle.Bold))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("C", font, brush, new RectangleF(0, 0, 32, 32), sf);
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        private void StartCmd()
        {
            
            if (string.IsNullOrEmpty(CmdFilePath))
            {
                _notifyIcon.ShowBalloonTip(3000, "",
                    "未配置执行程序，请先在设置中配置 cmd", ToolTipIcon.Warning);
                return;
            }

            if (!File.Exists(CmdFilePath))
            {
                _notifyIcon.ShowBalloonTip(3000, "",
                    $"执行程序不存在: {CmdFilePath}", ToolTipIcon.Error);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = CmdFilePath,
                    WorkingDirectory = Path.GetDirectoryName(CmdFilePath),
                    Arguments = $"--urls=\"{Properties.Settings.Default.urls}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                _notifyIcon.ShowBalloonTip(3000, "",
                    "Copilot Auto BYOK 服务已启动", ToolTipIcon.Info);
            }
            catch (Win32Exception ex)
            {
                _notifyIcon.ShowBalloonTip(3000, "",
                    "服务程序启动出错: " + ex.Message, ToolTipIcon.Error);
            }
        }

        private void StopCmd()
        {
            try
            {
                // 尝试通过进程名称查找并终止
                var processName = Path.GetFileNameWithoutExtension(CmdFilePath);
                foreach(var process in Process.GetProcessesByName(processName))
                {
                    if (process.MainModule.FileName == CmdFilePath)
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                        Debug.Print($"DEBUG: {process.ProcessName} 进程 {process.Id} 已被终止");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Print("WARN: " + ex.Message);
            }
        }

        /// <summary>
        /// 查找本地安装的 Microsoft Edge 浏览器路径
        /// </summary>
        /// <returns>Edge 可执行文件路径，如果未找到则返回 null</returns>
        private static string FindEdgeBrowser()
        {
            // Edge 常见的安装路径
            string[] edgePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"Microsoft\Edge\Application\msedge.exe"),
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };

            foreach (var path in edgePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // 尝试从注册表查找
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    var value = key?.GetValue("") as string;
                    if (!string.IsNullOrEmpty(value) && File.Exists(value))
                        return value;
                }
            }
            catch { }

            // 尝试从注册表查找 (HKCU)
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    var value = key?.GetValue("") as string;
                    if (!string.IsNullOrEmpty(value) && File.Exists(value))
                        return value;
                }
            }
            catch { }

            return null;
        }

        private const string APP_ID = "copilot-auto-byok";
        /// <summary>
        /// 单独的 Edge 用户数据目录，用于存储 PWA 的配置和状态，
        /// 主要用于查找和关闭窗口，避免影响其他的 Edge 浏览器实例
        /// </summary>
        private readonly static string PROFILE_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            $"{Application.ProductName}\\Profile");

        internal static void OpenBrowser()
        {
            var appUrl = Properties.Settings.Default.urls;
            // 优先尝试使用 Edge PWA 方式打开
            var edgePath = FindEdgeBrowser();
            if (!string.IsNullOrEmpty(edgePath))
            {
                try
                {
                    // Edge PWA 模式参数                  
                    var args = $"--app={appUrl} --app-id={APP_ID} --user-data-dir=\"{PROFILE_PATH}\"";
                    var psi = new ProcessStartInfo
                    {
                        FileName = edgePath,
                        Arguments = args,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                    return;
                }
                catch { }
            }

            // 回退到默认浏览器
            try
            {
                Process.Start(appUrl);
            }
            catch { }
        }

        /// <summary>
        /// 关闭 Edge PWA 窗口
        /// </summary>
        private void CloseBrowser()
        {
            try
            {
                var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='msedge.exe'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var cmd = obj["CommandLine"] as string ?? string.Empty;
                    Debug.Print(cmd);
                    if (cmd.Contains($"--app-id={APP_ID}") &&
                        cmd.Contains($"--user-data-dir=\"{PROFILE_PATH}\""))
                    {
                        if (int.TryParse(obj["ProcessId"]?.ToString(), out int pid))
                        {
                            try
                            {
                                var p = Process.GetProcessById(pid);
                                if (!p.HasExited)
                                    p.Kill();
                            }
                            catch
                            {
                                /* 忽略已退出或无权限 */
                                Debug.Print($"WARN : CloseBrowser 无法终止进程 {pid}");
                            }
                        }
                    }
                }
            }
            catch
            {
                /* WMI 不可用时就没办法了，忽略 */
                Debug.Print("WARN : CloseBrowser WMI 查询失败");
            }
        }

        private void RestartCmd()
        {
            StopCmd();
            StartCmd();
            _notifyIcon.ShowBalloonTip(2000, "",
                "服务程序已重启", ToolTipIcon.Info);
        }

        private void ExitApp()
        {
            StopCmd();
            CloseBrowser();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _notifyIcon != null) _notifyIcon.Dispose();
            base.Dispose(disposing);
        }
    }
}
