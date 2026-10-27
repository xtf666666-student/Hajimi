using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace HajimiPet
{
    internal static class Program
    {
        private static Mutex _single;
        private static NotifyIcon _tray;
        private static PetForm _pet;
        private static ContextMenuStrip _menu;

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            _single = new Mutex(true, "HajimiPet.SingleInstance", out created);
            if (!created)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int size = 104;
            bool demoRun = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--size" && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed)) size = parsed;
                }
                else if (args[i] == "--demo-run")
                {
                    // 仅用于截图与自检：强制进入跑步状态
                    demoRun = true;
                }
            }

            _pet = new PetForm();
            _pet.ApplySize(size);
            _pet.DemoRun = demoRun;
            _pet.OpenDshRequested += OpenDsh;
            _pet.SessionRequested += OnSessionRequested;

            BuildTray();
            _pet.Show();

            Application.ApplicationExit += (s, e) =>
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            };
            Application.Run();
        }

        private static void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Visible = true,
                Text = "哈基米桌宠",
                Icon = LoadIcon(),
            };
            RefreshTray();
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) _pet.Visible = !_pet.Visible;
            };
        }

        private static void RefreshTray()
        {
            if (_menu != null) _menu.Dispose();
            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem("进行中：" + _pet.ActiveCount + " 个对话") { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("打开 DeepSeek Harness", null, (s, e) => OpenDsh());
            _menu.Items.Add("刷新", null, (s, e) => _pet.RefreshSessions());
            _menu.Items.Add("大小：小 (72)", null, (s, e) => Resize(72));
            _menu.Items.Add("大小：中 (104)", null, (s, e) => Resize(104));
            _menu.Items.Add("大小：大 (140)", null, (s, e) => Resize(140));
            _menu.Items.Add("大小：特大 (180)", null, (s, e) => Resize(180));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("显示 / 隐藏", null, (s, e) => _pet.Visible = !_pet.Visible);
            _menu.Items.Add("退出", null, (s, e) => Application.Exit());
            _tray.ContextMenuStrip = _menu;
        }

        private static void Resize(int size)
        {
            _pet.ApplySize(size);
            RefreshTray();
        }

        private static Icon LoadIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("pet.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var s = asm.GetManifestResourceStream(name))
                        {
                            return new Icon(s);
                        }
                    }
                }
            }
            catch
            {
                // 没有图标也能运行
            }
            return SystemIcons.Application;
        }

        private static void OpenDsh()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "dsh://open",
                    UseShellExecute = true,
                });
            }
            catch
            {
                _pet.Say("找不到 DSH 入口");
            }
        }

        private static void OnSessionRequested(SessionItem item)
        {
            // DSH 没有逐会话深链接：唤出主窗口 + 把标题放进剪贴板
            try
            {
                Clipboard.SetText(item.Title);
                _pet.Say("标题已复制，回 DSH 搜索");
            }
            catch
            {
                _pet.Say("复制失败");
            }
            OpenDsh();
        }
    }
}
