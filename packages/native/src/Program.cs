using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
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
            int? activeOverride = null;
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
                else if (args[i] == "--active" && i + 1 < args.Length)
                {
                    // 仅用于自检：强制覆盖进行中的对话数
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed)) activeOverride = parsed;
                }
            }

            _pet = new PetForm();
            _pet.ApplySize(size);
            _pet.DemoRun = demoRun;
            _pet.DebugActiveOverride = activeOverride;
            _pet.OpenDshRequested += OpenDsh;
            _pet.SessionRequested += OnSessionRequested;
            _pet.ReplyRequested += OnReplyRequested;
            _pet.StopRequested += OnStopRequested;
            // 进行中对话数变化时，托盘统计跟着刷新
            _pet.ActiveCountChanged += count => RefreshTray();

            // 自检导图：把真实渲染输出写成 PNG 后直接退出，不显示窗口。
            // 分层窗口内容无法被系统截图可靠捕获，这是唯一能验证真实绘制的方式。
            string dumpDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dump" && i + 1 < args.Length) dumpDir = args[i + 1];
            }

            // 桥接自检：走一遍真实的 PetBridge 调用后退出，用于验证端到端连通。
            if (Array.IndexOf(args, "--bridge-selftest") >= 0)
            {
                string why;
                if (PetBridge.Available(out why)) Console.Out.Write("token-ok ");
                else Console.Out.Write("token-fail(" + why + ") ");

                PetBridge.BridgeResult ping = PetBridge.Cancel("__ping__");
                if (ping.Status == 401) Console.Out.Write("ping-401 ");
                else Console.Out.Write("ping-ok ");

                PetBridge.BridgeResult r = PetBridge.Cancel("session-does-not-exist-0000");
                if (r.Ok) Console.Out.Write("cancel-ok ");
                else if (r.Status == 409) Console.Out.Write("cancel-409 ");
                else Console.Out.Write("cancel-" + r.Status + "(" + r.Error + ") ");
                return;
            }
            if (dumpDir != null)
            {
                System.IO.Directory.CreateDirectory(dumpDir);
                _pet.DumpFrame(System.IO.Path.Combine(dumpDir, "idle.png"), false, 0);
                _pet.DumpFrame(System.IO.Path.Combine(dumpDir, "busy3.png"), false, 3);
                _pet.DumpFrame(System.IO.Path.Combine(dumpDir, "busy3-list.png"), true, 3);
                _pet.DumpFrame(System.IO.Path.Combine(dumpDir, "busy1-list.png"), true, 1);
                _pet.DumpFrameWithBubble(
                    System.IO.Path.Combine(dumpDir, "bubble.png"),
                    2, "本哈基米正在盯着那个任务跑，别催。");
                Console.Out.Write("dump done -> " + dumpDir);
                return;
            }

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
            // 托盘尚未创建时（例如自检导图模式）直接跳过，避免空引用
            if (_tray == null || _pet == null) return;

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

        /// <summary>
        /// 「回复」：优先走 pet-bridge 直接把消息发进该会话。
        /// 桥接不可用时退回「唤出 DSH + Ctrl+K 定位 + 粘贴」，由用户按回车。
        /// </summary>
        private static void OnReplyRequested(SessionItem item)
        {
            string reason;
            if (PetBridge.Available(out reason))
            {
                // 桥接只负责把消息送进去；这里发一句最常用的追问，
                // 用户随后可以在 DSH 里继续输入。
                PetBridge.BridgeResult r = PetBridge.Prompt(item.Id, "继续");
                if (r.Ok)
                {
                    _pet.Say("已发送到该对话");
                    OpenDsh();
                    return;
                }
                _pet.Say("桥接失败：" + Short(r.Error));
            }
            else
            {
                _pet.Say("桥接未就绪，改用定位");
            }

            try
            {
                Clipboard.SetText(item.Title);
            }
            catch
            {
                return;
            }
            OpenDsh();
            JumpToSessionInDsh(item.Title);
        }

        /// <summary>
        /// 「停止」：优先走 pet-bridge 真正取消这一轮。
        /// 桥接不可用时只做定位，不会谎称已停止。
        /// </summary>
        private static void OnStopRequested(SessionItem item)
        {
            string reason;
            if (!PetBridge.Available(out reason))
            {
                _pet.Say("桥接未就绪，需手动停止");
                try { Clipboard.SetText(item.Title); } catch { return; }
                OpenDsh();
                JumpToSessionInDsh(item.Title);
                return;
            }

            PetBridge.BridgeResult r = PetBridge.Cancel(item.Id);
            if (r.Ok)
            {
                // 桥接返回 ok 表示真的调到了 agent.cancel()，或该会话本就没在跑
                bool idle = r.Body != null && r.Body.IndexOf("\"alreadyIdle\":true", StringComparison.Ordinal) >= 0;
                _pet.Say(idle ? "该任务已结束" : "已停止该任务");
                _pet.RefreshSessions();
            }
            else if (r.Status == 409)
            {
                _pet.Say("停止失败：会话不在运行");
                _pet.RefreshSessions();
            }
            else
            {
                _pet.Say("停止失败：" + Short(r.Error));
            }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知原因";
            return s.Length > 22 ? s.Substring(0, 22) + "…" : s;
        }

        /// <summary>用 DSH 的 Ctrl+K 搜索把目标会话选中。</summary>
        private static void JumpToSessionInDsh(string title)
        {
            try
            {
                // 等 DSH 窗口真正激活后再发按键，否则按键会打到别处
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    if (IsDshForeground()) break;
                }
                if (!IsDshForeground()) return;

                Thread.Sleep(120);
                SendKeys.SendWait("^k");
                Thread.Sleep(260);
                SendKeys.SendWait("^v");
                Thread.Sleep(420);
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(120);
                // 清掉搜索框里的残留，让输入焦点回到对话
                SendKeys.SendWait("{ESC}");
            }
            catch
            {
                /* 按键失败不影响已复制的标题 */
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        private static bool IsDshForeground()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            string t = sb.ToString();
            return t.IndexOf("DeepSeek", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
