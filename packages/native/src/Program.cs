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
        private static ReplyBox _replyBox;

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

            // 回复框自检：真实创建窗口、检查焦点与提交回调，然后退出。
            if (Array.IndexOf(args, "--reply-selftest") >= 0)
            {
                RunReplyBoxSelftest();
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
        /// 「回复」：弹出输入框，把用户真正写的话发进该会话。
        ///
        /// 桥接可用时：用户在输入框按 Enter，消息直接进入该会话（走 DSH 自己的
        /// createUserMessage -> agent.followup 路径）。
        /// 桥接不可用时：输入框照常弹，但提交后改成「复制 + 唤出 DSH 定位」，
        /// 由用户粘贴并回车，不谎称已经发送。
        /// </summary>
        private static void OnReplyRequested(SessionItem item)
        {
            if (_replyBox != null && !_replyBox.IsDisposed)
            {
                _replyBox.Close();
                _replyBox = null;
                return;
            }

            string reason;
            bool bridged = PetBridge.Available(out reason);

            // 浮在桌宠旁边
            var anchor = new Point(_pet.Left, _pet.Top);
            var box = new ReplyBox(item.Title, anchor, string.Empty);

            box.Submitted += text =>
            {
                if (bridged)
                {
                    PetBridge.BridgeResult r = PetBridge.Prompt(item.Id, text);
                    if (r.Ok)
                    {
                        _pet.Say("已发送到该对话");
                        _pet.RefreshSessions();
                        return;
                    }
                    _pet.Say("发送失败：" + Short(r.Error));
                    return;
                }

                // 没有桥接：把文本交给剪贴板，让用户在 DSH 里粘贴
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    _pet.Say("复制失败");
                    return;
                }
                OpenDsh();
                _pet.Say("已复制，去 DSH 粘贴");
            };

            box.FormClosed += (s, e) => { _replyBox = null; };
            box.Show();
            _replyBox = box;

            if (!bridged) _pet.Say("桥接未就绪，将复制文本");
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

        /// <summary>
        /// 回复框自检：真的建一个 ReplyBox，填入文本，触发提交回调，
        /// 确认窗口能创建、能拿焦点、能把用户文本原样交出去。
        /// </summary>
        private static void RunReplyBoxSelftest()
        {
            string captured = null;
            var box = new ReplyBox("自检会话标题", new Point(200, 200), "自检文本");
            box.Submitted += text => { captured = text; };

            box.Show();
            Application.DoEvents();
            Console.Out.Write("box-ok ");

            // 焦点应落在输入框上（ReplyBox 在 Shown 里 Focus）
            Application.DoEvents();
            bool focused = box.ActiveControl != null;
            Console.Out.Write(focused ? "focus-ok " : "focus-fail ");

            // 触发提交：找到 TextBox 并模拟 Enter
            var tb = FindTextBox(box);
            if (tb != null)
            {
                tb.Text = "自检发送内容";
                var ev = new KeyEventArgs(Keys.Enter);
                var mi = typeof(ReplyBox).GetMethod("OnInputKeyDown",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                mi.Invoke(box, new object[] { tb, ev });
                Application.DoEvents();
            }

            Console.Out.Write(captured == "自检发送内容"
                ? "submit-ok "
                : "submit-fail(" + (captured ?? "null") + ") ");
            if (!box.IsDisposed) box.Close();
        }

        private static TextBox FindTextBox(Control root)
        {
            if (root is TextBox t) return t;
            foreach (Control c in root.Controls)
            {
                TextBox found = FindTextBox(c);
                if (found != null) return found;
            }
            return null;
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
