using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HajimiPet
{
    /// <summary>
    /// Native layered-window desktop pet (UpdateLayeredWindow + 32bpp ARGB).
    /// No Electron / Chromium involved.
    ///
    /// Layers come from one source render, split along measured gaps
    /// (see tools/assets/build_layers.py):
    ///   pet-torso.png      body, painted LAST so it covers the ankle cut
    ///   pet-leg-left.png   left foot, pivots about its ankle (100, 406)
    ///   pet-leg-right.png  right foot, pivots about its ankle (272, 406)
    ///   pet-shadow.png     separate soft ground shadow
    ///
    /// Behaviour:
    ///   tasks running + cursor over the pet -> alternating foot lifts with ankle
    ///   roll and a bob/lean in the torso, i.e. it actually runs in place.
    ///   no tasks running                    -> cursor-following look + idle breathing.
    /// </summary>
    internal sealed class PetForm : Form
    {
        // ---------------- Win32 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        // Source-art metrics, measured with tools/measure_feet.py
        private const float SRC_H = 460f;
        private const float ANKLE_L_X = 100f;
        private const float ANKLE_R_X = 272f;
        private const float ANKLE_Y = 406f;

        private const int BadgeGap = 2;
        private const int BadgeH = 17;

        // ---------------- Layers ----------------
        private readonly Bitmap _torso;
        private readonly Bitmap _legLeft;
        private readonly Bitmap _legRight;
        private readonly Bitmap _shadow;

        // ---------------- Timers ----------------
        private readonly Timer _anim;
        private readonly Timer _poll;

        // ---------------- Animation ----------------
        private float _phase;
        private float _tiltX;
        private float _tiltY;
        private int _runFrame;

        // ---------------- Interaction ----------------
        private bool _hover;
        private bool _dragging;
        private bool _movedWhileDown;
        private Point _dragStart;
        private Point _formStart;

        private int _bubbleLeft;
        private string _bubble = string.Empty;
        private int _badgePulse;
        private int _lastNotified;

        private bool _listOpen;
        private int _activeCount;
        private readonly List<SessionItem> _active = new List<SessionItem>();

        private Rectangle _badgeRect;
        private Rectangle _lastBubbleRect;
        private Rectangle _lastListRect;
        private Rectangle _lastShadowRect;
        private Rectangle _lastCatRect;
        private Rectangle[] _rowRects = new Rectangle[0];
        private Rectangle[] _btnReply = new Rectangle[0];
        private Rectangle[] _btnStop = new Rectangle[0];
        private Rectangle[] _btnClose = new Rectangle[0];

        private enum IconKind { Reply, Stop, Close }

        // 会话行右侧的图标按钮尺寸
        private const int BtnSize = 15;

        public event Action OpenDshRequested;
        public event Action<SessionItem> SessionRequested;
        /// <summary>会话行右侧「回复」按钮。</summary>
        public event Action<SessionItem> ReplyRequested;
        /// <summary>会话行右侧「停止」按钮。</summary>
        public event Action<SessionItem> StopRequested;
        /// <summary>进行中对话数发生变化时触发（托盘据此刷新统计）。</summary>
        public event Action<int> ActiveCountChanged;

        public int PetSize { get; private set; }
        public bool DemoRun { get; set; }
        /// <summary>测试用：强制覆盖"进行中对话数"，用于验证空闲态外观。</summary>
        public int? DebugActiveOverride { get; set; }

        /// <summary>
        /// 自检用：把当前渲染结果导出为 PNG。
        /// 分层窗口内容无法被 PrintWindow / 抓屏可靠捕获，所以让程序自己吐图，
        /// 这样验证的是真实渲染输出，不依赖系统合成状态。
        /// </summary>
        public void DumpFrame(string path, bool listOpen, int activeOverride)
        {
            DebugActiveOverride = activeOverride;
            Poll();
            _listOpen = listOpen;
            // Poll 之后才知道有多少行，必须重新按行数布局
            ApplyLayout();
            Render();

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(canvas, w, h);
                canvas.Save(path, ImageFormat.Png);
            }
            Console.Out.Write($"[dump] {System.IO.Path.GetFileName(path)} {w}x{h} rows={_active.Count} listOpen={_listOpen} | ");

            // 每个帧都写自己的几何，供自检按真实矩形判断
            string json = "{" +
                "\"window\":[" + w + "," + h + "]," +
                "\"badge\":[" + _badgeRect.X + "," + _badgeRect.Y + "," +
                    _badgeRect.Width + "," + _badgeRect.Height + "]," +
                "\"shadow\":[" + _lastShadowRect.X + "," + _lastShadowRect.Y + "," +
                    _lastShadowRect.Width + "," + _lastShadowRect.Height + "]," +
                "\"cat\":[" + _lastCatRect.X + "," + _lastCatRect.Y + "," +
                    _lastCatRect.Width + "," + _lastCatRect.Height + "]," +
                "\"list\":[" + _lastListRect.X + "," + _lastListRect.Y + "," +
                    _lastListRect.Width + "," + _lastListRect.Height + "]," +
                "\"rows\":[" + _rowRects.Length + "]" +
                "}";
            System.IO.File.WriteAllText(
                System.IO.Path.ChangeExtension(path, ".json"), json);
        }

        /// <summary>自检用：导出一帧带侧边气泡的画面，并写出各区域的真实矩形。</summary>
        public void DumpFrameWithBubble(string path, int activeOverride, string text)
        {
            DebugActiveOverride = activeOverride;
            Poll();
            _bubble = text;
            _bubbleLeft = 200;
            ApplyLayout();
            Render();

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(canvas, w, h);
                canvas.Save(path, ImageFormat.Png);
            }

            // 把绘制时记录下来的真实矩形写出来，避免用颜色猜位置
            string json = "{" +
                "\"window\":[" + w + "," + h + "]," +
                "\"badge\":[" + _badgeRect.X + "," + _badgeRect.Y + "," +
                    _badgeRect.Width + "," + _badgeRect.Height + "]," +
                "\"bubble\":[" + _lastBubbleRect.X + "," + _lastBubbleRect.Y + "," +
                    _lastBubbleRect.Width + "," + _lastBubbleRect.Height + "]," +
                "\"list\":[" + _lastListRect.X + "," + _lastListRect.Y + "," +
                    _lastListRect.Width + "," + _lastListRect.Height + "]," +
                "\"shadow\":[" + _lastShadowRect.X + "," + _lastShadowRect.Y + "," +
                    _lastShadowRect.Width + "," + _lastShadowRect.Height + "]," +
                "\"cat\":[" + _lastCatRect.X + "," + _lastCatRect.Y + "," +
                    _lastCatRect.Width + "," + _lastCatRect.Height + "]," +
                "\"rows\":[" + _rowRects.Length + "]" +
                "}";
            System.IO.File.WriteAllText(
                System.IO.Path.ChangeExtension(path, ".json"), json);
        }

        public PetForm()
        {
            _torso = LoadBitmap("pet-torso.png");
            _legLeft = LoadBitmap("pet-leg-left.png");
            _legRight = LoadBitmap("pet-leg-right.png");
            _shadow = LoadBitmap("pet-shadow.png");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "HajimiPet";

            PetSize = 104;
            ApplyLayout();

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 6;

            // 1.5 秒轮询：新任务出现后最迟 1.5 秒就能同步到角标与列表
            _poll = new Timer { Interval = 1500 };
            _poll.Tick += (s, e) => Poll();
            _poll.Start();
            Poll();

            _anim = new Timer { Interval = 33 };
            _anim.Tick += (s, e) => Tick();
            _anim.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ApplySize(int size)
        {
            PetSize = Math.Max(72, Math.Min(180, size));
            ApplyLayout();
            Render();
        }

        private void ApplyLayout()
        {
            int catH = PetSize;
            int catW = (int)(PetSize * 0.817f);
            int shadowH = Math.Max(6, (int)(catH * 0.06f));

            int listH = CurrentListHeight();

            // 气泡出现在猫的右侧，所以窗口要预留右侧空间；气泡不显示时收回。
            int bubbleSide = BubbleVisible() ? BubbleWidth + BubbleGap : 0;

            int baseW = catW + 14;
            int w = baseW;
            if (bubbleSide > 0) w = baseW + bubbleSide;
            if (listH > 0) w = Math.Max(w, ListWidth + 10);
            // 顶部区域：有角标时才占位，没有任务时头顶留干净
            int badgeH = _activeCount > 0 ? BadgeH + BadgeGap : 0;
            int bodyH = badgeH + catH + shadowH;
            // 列表展开时，在主体上方额外加上列表高度
            int h = bodyH + listH;
            ClientSize = new Size(w, h);
        }

        // 侧边气泡：挂在猫的右侧，不与上方会话列表重叠
        private const int BubbleWidth = 132;
        private const int BubbleGap = 6;
        private const int ListWidth = 252;
        private const int ListRowH = 20;
        private const int ListPadY = 6;
        private const int ListMaxRows = 8;

        private bool BubbleVisible()
        {
            return _bubbleLeft > 0 && !string.IsNullOrEmpty(_bubble);
        }

        private int CurrentListHeight()
        {
            if (!_listOpen) return 0;
            int count = Math.Min(_active.Count, ListMaxRows);
            if (count <= 0) return 0;
            return count * ListRowH + ListPadY;
        }

        private static Bitmap LoadBitmap(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string candidate in asm.GetManifestResourceNames())
            {
                if (!candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream s = asm.GetManifestResourceStream(candidate))
                using (Bitmap raw = new Bitmap(s))
                {
                    // The pixels must actually be copied in: new Bitmap(w, h, format) is blank.
                    Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height));
                    }
                    return copy;
                }
            }
            throw new InvalidOperationException("missing embedded asset: " + name);
        }

        // ---------------- Animation ----------------

        private void Tick()
        {
            bool sprinting = (DemoRun || (_activeCount > 0 && _hover && !_listOpen));

            _phase += sprinting ? 0.34f : 0.075f;
            if (_phase > 6.2831853f) _phase -= 6.2831853f;
            if (sprinting) _runFrame++;

            bool bubbleWas = BubbleVisible();
            if (_bubbleLeft > 0) _bubbleLeft--;
            if (_badgePulse > 0) _badgePulse--;

            // 气泡出现/消失会改变需要的窗口宽度，宽度变化时重新布局并贴回原位
            if (bubbleWas != BubbleVisible()) Reflow();

            Render();
        }

        /// <summary>窗口尺寸需要变化时调用：保持右边缘和底边不动，避免桌宠跳位。</summary>
        private void Reflow()
        {
            int right = Left + Width;
            int bottom = Top + Height;
            ApplyLayout();
            Left = right - Width;
            Top = bottom - Height;
        }

        private void Render()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(canvas, w, h);
                ApplyLayered(canvas, w, h);
            }
        }

        /// <summary>把整个桌宠场景画进 canvas。实时渲染与自检导图共用这一份。</summary>
        private void DrawScene(Bitmap canvas, int w, int h)
        {
            bool sprinting = (DemoRun || (_activeCount > 0 && _hover && !_listOpen));

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }

            {
                int catH = PetSize;
                int catW = (int)(PetSize * 0.817f);
                // 有侧边气泡时猫靠右（气泡在左侧向左展开，不会出屏）；否则居中。
                bool bubbleOn = BubbleVisible();
                int catX = bubbleOn ? (w - catW - 2) : (w - catW) / 2;
                // 猫始终固定在窗口底部（脚底+阴影不因列表展开而移动），
                // 列表向上升，这样展开时猫不会掉出窗口，也不会被任务栏遮住。
                int shadowH = Math.Max(6, (int)(catH * 0.06f));
                int catY = h - shadowH - catH;
                float scale = PetSize / SRC_H;

                // 脚底线：素材里脚底在 439/460
                int footY = catY + (int)(catH * (439f / 460f));

                // ---- Run cycle: 6 frames, mirrors tools/verify_layers.py ----
                float bodyBob, lean, dash, liftL, liftR, ankL, ankR;
                if (sprinting)
                {
                    // liftL/liftR 单位为素材像素；ankL/ankR 为摆腿角度（度）。
                    switch (_runFrame % 6)
                    {
                        case 0: liftL = 20f; liftR = 0f; ankL = 26f; ankR = -6f; bodyBob = 4.0f; lean = -3.0f; dash = 1.4f; break;
                        case 1: liftL = 14f; liftR = 3f; ankL = 16f; ankR = -2f; bodyBob = 2.0f; lean = -1.5f; dash = 0.8f; break;
                        case 2: liftL = 4f; liftR = 8f; ankL = 4f; ankR = 6f; bodyBob = 0.0f; lean = 0.0f; dash = 0.0f; break;
                        case 3: liftL = 0f; liftR = 20f; ankL = -6f; ankR = 26f; bodyBob = 4.0f; lean = 3.0f; dash = -1.4f; break;
                        case 4: liftL = 3f; liftR = 14f; ankL = -2f; ankR = 16f; bodyBob = 2.0f; lean = 1.5f; dash = -0.8f; break;
                        default: liftL = 8f; liftR = 4f; ankL = 6f; ankR = 4f; bodyBob = 0.0f; lean = 0.0f; dash = 0.0f; break;
                    }
                }
                else
                {
                    bodyBob = (float)Math.Sin(_phase) * 0.9f;
                    lean = 0f;
                    dash = 0f;
                    liftL = 0f;
                    liftR = 0f;
                    ankL = 0f;
                    ankR = 0f;
                }

                float sway = (float)Math.Sin(_phase * (sprinting ? 1.4f : 0.55f)) *
                             (sprinting ? 2.2f : 1.6f);

                // 1. 地面阴影：必须跟着猫走，所以用猫的中心而不是窗口中心
                int sw = (int)(_shadow.Width * scale * (1f + 0.06f * Math.Abs(dash)));
                int sh = Math.Max(6, (int)(_shadow.Height * scale));
                int shadowX = catX + (catW - sw) / 2;
                _lastShadowRect = new Rectangle(shadowX, footY - 1, sw, sh);
                _lastCatRect = new Rectangle(catX, catY, catW, catH);
                DrawPlain(canvas, _shadow,
                    new Rectangle(shadowX, footY - 1, sw, sh));

                // 2. feet first, so the torso can cover the ankle cut.
                // lift/dash are given in SOURCE pixels and scaled here, so the
                // motion reads the same at every pet size.
                float sLift = scale;
                DrawLeg(canvas, _legLeft, catX, catY, catW, catH, scale,
                    ANKLE_L_X, -dash * 1.6f * sLift, -liftL * sLift, ankL);
                DrawLeg(canvas, _legRight, catX, catY, catW, catH, scale,
                    ANKLE_R_X, dash * 1.6f * sLift, -liftR * sLift, ankR);

                // 3. torso last
                DrawRotated(canvas, _torso,
                    new Rectangle(catX + (int)(dash * 1.2f * scale), catY - (int)(bodyBob * scale), catW, catH),
                    sway + _tiltX + lean, w / 2f, h * 0.96f);

                // 4. overlays
                // 角标紧贴猫头顶
                int badgeBottom = catY - BadgeGap;
                int badgeTop = badgeBottom - BadgeH;
                // 列表在角标上方，占据窗口新加出来的那块高度
                int listH = CurrentListHeight();
                int listTop = badgeTop - listH;

                DrawBadge(canvas, w, catY, catX, catW);
                if (BubbleVisible()) DrawBubble(canvas, w, catY, catX, catW);
                if (_listOpen) DrawList(canvas, w, listTop);
            }
        }

        /// <summary>Translate the leg, then rotate it about its own ankle.</summary>
        private static void DrawLeg(Bitmap canvas, Bitmap leg, int catX, int catY,
            int catW, int catH, float scale, float ankleSrcX, float dx, float dy, float angle)
        {
            float pivotX = catX + ankleSrcX * scale;
            float pivotY = catY + ANKLE_Y * scale;

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                g.TranslateTransform(pivotX + dx, pivotY + dy);
                if (Math.Abs(angle) > 0.05f) g.RotateTransform(angle);
                g.TranslateTransform(-pivotX, -pivotY);

                g.DrawImage(leg, new Rectangle(catX, catY, catW, catH));
                g.ResetTransform();
            }
        }

        private static void DrawPlain(Bitmap canvas, Bitmap bmp, Rectangle dest)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(bmp, dest);
            }
        }

        private static void DrawRotated(Bitmap canvas, Bitmap bmp, Rectangle dest,
            float angle, float pivotX, float pivotY)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (Math.Abs(angle) > 0.03f)
                {
                    g.TranslateTransform(pivotX, pivotY);
                    g.RotateTransform(angle);
                    g.TranslateTransform(-pivotX, -pivotY);
                }
                g.DrawImage(bmp, dest);
                if (Math.Abs(angle) > 0.03f) g.ResetTransform();
            }
        }

        // ---------------- Badge ----------------

        private void DrawBadge(Bitmap canvas, int w, int catY, int catX, int catW)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool running = _activeCount > 0;

                // 角标紧贴猫头顶，并且横向对齐猫的中心（不是窗口中心）
                int badgeTop = Math.Max(0, catY - BadgeGap - BadgeH);
                int catCenterX = catX + catW / 2;

                if (!running)
                {
                    // 没有任务在跑：什么都不画。
                    // 之前这里留了个灰胶囊和小圆点，用户明确不要 —— 头上保持干净。
                    _badgeRect = Rectangle.Empty;
                    return;
                }

                string text = _activeCount.ToString();
                using (Font font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(text, font);
                    int bw = Math.Max(26, (int)ts.Width + 16 + 6);
                    int bh = BadgeH;
                    int bx = catCenterX - bw / 2;

                    // 新任务出现时角标放大一点，作为即时反馈
                    int pulse = _badgePulse > 0 ? Math.Min(3, _badgePulse / 2) : 0;
                    _badgeRect = new Rectangle(bx - pulse, badgeTop - pulse,
                        bw + pulse * 2, bh + pulse * 2);

                    using (GraphicsPath path = RoundRect(_badgeRect, _badgeRect.Height / 2))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 59, 165, 95)))
                    using (Pen pen = new Pen(Color.FromArgb(255, 59, 165, 95), 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(pen, path);
                    }

                    // 脉动小圆点
                    using (SolidBrush d = new SolidBrush(Color.FromArgb(255, 234, 255, 242)))
                    {
                        g.FillEllipse(d, _badgeRect.X + 7, _badgeRect.Y + (_badgeRect.Height - 6) / 2, 6, 6);
                    }

                    using (SolidBrush fg = new SolidBrush(Color.White))
                    {
                        g.DrawString(text, font, fg, _badgeRect.X + 17,
                            _badgeRect.Y + (_badgeRect.Height - ts.Height) / 2f + 0.5f);
                    }
                }
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 侧边气泡：挂在猫的左侧（猫默认贴屏幕右边，向左展开才不会出屏），
        /// 竖直对齐猫头，用一个小尾巴指向右方的猫。
        /// 位置在猫的侧面，永远不会和上方的会话列表重叠。
        /// </summary>
        private void DrawBubble(Bitmap canvas, int w, int catY, int catX, int catW)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Font font = new Font("Microsoft YaHei UI", 8f))
                {
                    int maxTextW = BubbleWidth - 18;
                    string text = WrapText(g, _bubble, font, maxTextW);
                    SizeF ts = g.MeasureString(text, font);

                    int bw = Math.Min(BubbleWidth, (int)ts.Width + 18);
                    int bh = (int)ts.Height + 10;
                    // 气泡右边缘贴在猫左边，向左展开
                    int bx = Math.Max(1, catX - BubbleGap - bw);
                    int by = catY + Math.Max(2, (int)(PetSize * 0.10f));
                    _lastBubbleRect = new Rectangle(bx, by, bw, bh);

                    // 尾巴：指向右侧的猫
                    int tipY = by + Math.Min(bh / 2, (int)(PetSize * 0.28f));
                    Point[] tail =
                    {
                        new Point(bx + bw - 2, tipY - 4),
                        new Point(bx + bw + 5, tipY),
                        new Point(bx + bw - 2, tipY + 4),
                    };

                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(250, 255, 253, 248)))
                    using (Pen pen = new Pen(Color.FromArgb(210, 240, 176, 110), 1f))
                    using (GraphicsPath path = RoundRect(new Rectangle(bx, by, bw, bh), 9))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(pen, path);
                        // 尾巴盖在边框上，视觉上连成一体
                        g.FillPolygon(bg, tail);
                        g.DrawLines(pen, new[]
                        {
                            tail[0], tail[1], tail[2],
                        });
                    }

                    using (SolidBrush ink = new SolidBrush(Color.FromArgb(63, 52, 40)))
                    {
                        g.DrawString(text, font, ink, bx + 9, by + 5);
                    }
                }
            }
        }

        /// <summary>按字符换行（中文没有空格，按宽度断行）。避免标点被挤到行首。</summary>
        private static string WrapText(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            const string noLineStart = "，。、；：！？）」』…”’%";
            var sb = new System.Text.StringBuilder();
            int lineW = 0;
            foreach (char ch in text)
            {
                float cw = g.MeasureString(ch.ToString(), font).Width;
                bool overflow = lineW + cw > maxWidth && lineW > 0;
                if (overflow && noLineStart.IndexOf(ch) >= 0)
                {
                    // 标点不另起一行，挤在本行末尾
                    sb.Append(ch);
                    lineW += (int)cw;
                    continue;
                }
                if (overflow)
                {
                    sb.Append('\n');
                    lineW = 0;
                }
                sb.Append(ch);
                lineW += (int)cw;
            }
            return sb.ToString();
        }

        /// <summary>会话行右侧的小图标按钮：↩ 回复 / ■ 停止。</summary>
        private static void DrawIconButton(Graphics g, Rectangle r, IconKind kind, bool enabled)
        {
            SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int alpha = enabled ? 255 : 110;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(enabled ? 246 : 236, 241, 233, 220)))
            using (Pen border = new Pen(Color.FromArgb(enabled ? 190 : 110, 222, 205, 182), 1f))
            {
                g.FillEllipse(bg, r);
                g.DrawEllipse(border, r);
            }

            switch (kind)
            {
                case IconKind.Stop:
                    {
                        int s = Math.Max(5, r.Width - 9);
                        int x = r.X + (r.Width - s) / 2;
                        int y = r.Y + (r.Height - s) / 2;
                        using (SolidBrush fg = new SolidBrush(Color.FromArgb(alpha, 205, 90, 74)))
                        {
                            g.FillRectangle(fg, new Rectangle(x, y, s, s));
                        }
                        break;
                    }
                case IconKind.Close:
                    {
                        using (Pen fg = new Pen(Color.FromArgb(alpha, 150, 132, 112), 1.4f))
                        {
                            int pad = 5;
                            g.DrawLine(fg, r.X + pad, r.Y + pad, r.Right - pad, r.Bottom - pad);
                            g.DrawLine(fg, r.Right - pad, r.Y + pad, r.X + pad, r.Bottom - pad);
                        }
                        break;
                    }
                default:
                    {
                        // 折返箭头 = 回复
                        using (Pen fg = new Pen(Color.FromArgb(alpha, 90, 130, 100), 1.4f))
                        {
                            int left = r.X + 4;
                            int right = r.Right - 4;
                            int midY = r.Y + r.Height / 2 + 1;
                            int topY = r.Y + 5;
                            g.DrawLine(fg, right, midY, left, midY);
                            g.DrawLine(fg, left, midY, left + 3, midY - 3);
                            g.DrawLine(fg, left, midY, left + 3, midY + 3);
                            g.DrawLine(fg, right, midY, right, topY);
                        }
                        break;
                    }
            }

            g.SmoothingMode = prev;
        }

        // ---------------- Session list ----------------

        private void DrawList(Bitmap canvas, int w, int listTop)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int count = Math.Min(_active.Count, ListMaxRows);
                if (count == 0)
                {
                    _rowRects = new Rectangle[0];
                    return;
                }

                _rowRects = new Rectangle[count];
                _btnReply = new Rectangle[count];
                _btnStop = new Rectangle[count];
                _btnClose = new Rectangle[count];

                int lw = Math.Min(w - 8, ListWidth);
                int lh = count * ListRowH + ListPadY;
                int lx = (w - lw) / 2;
                int ly = Math.Max(0, listTop);

                // 让列表底边正好落在 listTop + listH = 角标顶边，不留空隙
                lh = Math.Max(ListRowH + ListPadY, Math.Min(lh, listTop + lh - ly));
                _lastListRect = new Rectangle(lx, ly, lw, lh);

                using (GraphicsPath path = RoundRect(new Rectangle(lx, ly, lw, lh), 9))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(252, 255, 253, 248)))
                using (Pen pen = new Pen(Color.FromArgb(215, 240, 176, 110), 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(pen, path);
                }

                using (Font font = new Font("Microsoft YaHei UI", 8.5f))
                {
                    for (int i = 0; i < count; i++)
                    {
                        SessionItem item = _active[i];
                        int ry = ly + 6 + i * ListRowH;
                        _rowRects[i] = new Rectangle(lx + 3, ry, lw - 6, ListRowH);
                        _btnReply[i] = default(Rectangle);
                        _btnStop[i] = default(Rectangle);
                        _btnClose[i] = default(Rectangle);

                        // 状态点：跑着=脉动绿点，跑完未看=琥珀点，看过=空心灰点
                        int dotX = lx + 9;
                        int dotY = ry + 7;
                        if (item.Active)
                        {
                            // 外圈光晕表示在跑
                            using (SolidBrush halo = new SolidBrush(Color.FromArgb(70, 59, 165, 95)))
                            {
                                g.FillEllipse(halo, dotX - 2, dotY - 2, 10, 10);
                            }
                            using (SolidBrush dot = new SolidBrush(Color.FromArgb(59, 165, 95)))
                            {
                                g.FillEllipse(dot, dotX, dotY, 6, 6);
                            }
                        }
                        else if (item.Unread)
                        {
                            using (SolidBrush dot = new SolidBrush(Color.FromArgb(230, 156, 62)))
                            {
                                g.FillEllipse(dot, dotX, dotY, 6, 6);
                            }
                        }
                        else
                        {
                            using (Pen ring = new Pen(Color.FromArgb(190, 180, 168), 1.2f))
                            {
                                g.DrawEllipse(ring, dotX, dotY, 5, 5);
                            }
                        }

                        // 右侧三个图标按钮：回复 ↩ / 停止 ■ / 叉掉 ×
                        int btnY = ry + 2;
                        int closeX = lx + lw - 8 - BtnSize;
                        int stopX = closeX - 4 - BtnSize;
                        int replyX = stopX - 4 - BtnSize;
                        _btnClose[i] = new Rectangle(closeX, btnY, BtnSize, BtnSize);
                        _btnStop[i] = new Rectangle(stopX, btnY, BtnSize, BtnSize);
                        _btnReply[i] = new Rectangle(replyX, btnY, BtnSize, BtnSize);

                        // 停止只在真的在跑时才有意义，否则画成禁用态
                        DrawIconButton(g, _btnReply[i], IconKind.Reply, item.Active || item.Unread);
                        DrawIconButton(g, _btnStop[i], IconKind.Stop, item.Active);
                        DrawIconButton(g, _btnClose[i], IconKind.Close, true);

                        // 标题可用宽度：右边让出三个按钮
                        string ago = item.Ago;
                        SizeF agoSize = g.MeasureString(ago, font);
                        int titleRight = replyX - 6;
                        int titleMax = Math.Max(40, titleRight - (lx + 21));
                        string title = item.Title;
                        while (title.Length > 1 && g.MeasureString(title, font).Width > titleMax)
                        {
                            title = title.Substring(0, title.Length - 1);
                        }
                        if (title.Length < item.Title.Length) title = title.TrimEnd() + "…";

                        // 已完成且看过的行弱化，避免和进行中的抢注意力
                        int ink = item.Active || item.Unread ? 63 : 150;
                        using (SolidBrush inkBrush = new SolidBrush(Color.FromArgb(ink, 52, 40)))
                        {
                            g.DrawString(title, font, inkBrush, lx + 21, ry + 2);
                        }

                        using (Font small = new Font("Microsoft YaHei UI", 7.5f))
                        using (SolidBrush agoBrush = new SolidBrush(Color.FromArgb(178, 158, 138)))
                        {
                            float tw = g.MeasureString(title, font).Width;
                            int ax = lx + 21 + (int)tw + 6;
                            if (ax + (int)agoSize.Width < titleRight)
                            {
                                g.DrawString(ago, small, agoBrush, ax, ry + 3);
                            }
                        }
                    }
                }
            }
        }

        // ---------------- Present to the layered window ----------------

        private void ApplyLayered(Bitmap canvas, int w, int h)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = canvas.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                POINT dst = new POINT { X = Left, Y = Top };
                SIZE size = new SIZE { cx = w, cy = h };
                POINT src = new POINT { X = 0, Y = 0 };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---------------- Interaction ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover = true;

            if (_dragging)
            {
                Point cur = Cursor.Position;
                int dx = cur.X - _dragStart.X;
                int dy = cur.Y - _dragStart.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _movedWhileDown = true;
                Left = _formStart.X + dx;
                Top = _formStart.Y + dy;
                return;
            }

            float nx = (e.X / (float)Math.Max(1, ClientSize.Width) - 0.5f) * 2f;
            float ny = (e.Y / (float)Math.Max(1, ClientSize.Height) - 0.5f) * 2f;
            _tiltX = Clamp(nx * 5f, -6f, 6f);
            _tiltY = Clamp(ny * 4f, -5f, 5f);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _tiltX = 0f;
            _tiltY = 0f;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (_listOpen)
            {
                // 先判按钮，再判整行：按钮在行内，必须优先命中
                for (int i = 0; i < _btnReply.Length; i++)
                {
                    SessionItem item = (i < _active.Count) ? _active[i] : null;
                    if (item == null) continue;

                    if (_btnClose[i].Contains(e.Location))
                    {
                        // 叉掉：从列表移除，角标随之下降
                        SessionScanner.Dismiss(item.Id);
                        CloseList();
                        Poll();
                        ApplyLayout();
                        Say("已移除");
                        return;
                    }
                    if (_btnStop[i].Contains(e.Location))
                    {
                        if (!item.Active)
                        {
                            Say("该任务已经结束");
                            return;
                        }
                        if (StopRequested != null) StopRequested(item);
                        return;
                    }
                    if (_btnReply[i].Contains(e.Location))
                    {
                        // 点开这一行就算看过了
                        SessionScanner.MarkSeen(item.Id);
                        if (ReplyRequested != null) ReplyRequested(item);
                        return;
                    }
                }

                for (int i = 0; i < _rowRects.Length; i++)
                {
                    if (_rowRects[i].Contains(e.Location))
                    {
                        SessionItem picked = (i < _active.Count) ? _active[i] : null;
                        // 点开即视为已查看，跑完的角标计数随之下降
                        if (picked != null) SessionScanner.MarkSeen(picked.Id);
                        CloseList();
                        if (SessionRequested != null && picked != null)
                        {
                            SessionRequested(picked);
                        }
                        return;
                    }
                }
            }

            // 角标在空闲时不画，命中判断要跳过空矩形
            if (_badgeRect != Rectangle.Empty && _badgeRect.Contains(e.Location))
            {
                ToggleList();
                return;
            }

            _dragging = true;
            _movedWhileDown = false;
            _dragStart = Cursor.Position;
            _formStart = new Point(Left, Top);
            Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_dragging) return;

            _dragging = false;
            Capture = false;
            if (_movedWhileDown) { ClampToScreen(); return; }

            Say(_activeCount > 0 ? "跑着呢。" : "在呢。");
            if (OpenDshRequested != null) OpenDshRequested();
        }

        public void ToggleList()
        {
            if (_listOpen) { CloseList(); return; }

            // 先刷新数据，确认真的有任务在跑，再按行数撑开窗口
            Poll();
            if (_activeCount == 0)
            {
                Say("没有任务在跑。");
                return;
            }

            int bottom = Top + Height;   // 保持窗口底边不动，向上长高
            _listOpen = true;
            ApplyLayout();
            Top = bottom - Height;
            ClampToScreen();
        }

        public void RefreshSessions()
        {
            Poll();
        }

        /// <summary>收起列表并恢复窗口尺寸（底边保持不动）。</summary>
        public void CloseList()
        {
            if (!_listOpen) return;
            int bottom = Top + Height;
            _listOpen = false;
            _rowRects = new Rectangle[0];
            ApplyLayout();
            Top = bottom - Height;
            ClampToScreen();
        }

        private void ClampToScreen()
        {
            Screen screen = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
            Rectangle work = screen.WorkingArea;
            Left = Math.Max(work.Left - Width + 34, Math.Min(Left, work.Right - 34));
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - 34));
        }

        public void Say(string text)
        {
            _bubble = text;
            _bubbleLeft = 110;
        }

        private void Poll()
        {
            List<SessionItem> items = SessionScanner.ReadActive();

            if (DebugActiveOverride.HasValue)
            {
                int want = DebugActiveOverride.Value;
                items.Clear();
                for (int i = 0; i < want; i++)
                {
                    // 交替造出三种状态，方便验证角标/列表的显示差异
                    bool active = (i % 3) == 0;
                    bool unread = !active && (i % 3) == 1;
                    items.Add(new SessionItem
                    {
                        Id = "debug-" + i,
                        Title = active ? ("进行中 " + (i + 1))
                             : unread ? ("刚跑完 " + (i + 1))
                             : ("已看过 " + (i + 1)),
                        Active = active,
                        Unread = unread,
                        Turn = 1,
                        Updated = DateTime.Now.AddMinutes(-i * 3),
                        Ago = i == 0 ? "刚刚" : (i * 3) + " 分前",
                    });
                }
            }

            // 只有内容真的变了才更新，避免无意义的重绘与列表抖动
            bool changed = items.Count != _activeCount;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (!string.Equals(items[i].Id, _active[i].Id, StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            _active.Clear();
            _active.AddRange(items);
            int prevCount = _activeCount;
            _activeCount = items.Count;

            // 角标占位与否会改变窗口高度，所以"有没有东西可显示"也算变化
            bool layoutChanged = changed || (prevCount > 0) != (_activeCount > 0);

            if (changed)
            {
                // 新任务出现时让角标跳一下，用户能立刻看到
                if (_activeCount > _lastNotified)
                {
                    _badgePulse = 8;
                }
                _lastNotified = _activeCount;

                Action<int> handler = ActiveCountChanged;
                if (handler != null) handler(_activeCount);
            }

            // 列表展开时行数会变；空闲/有角标切换也会变高度
            if (_listOpen || layoutChanged)
            {
                int bottom = Top + Height;
                ApplyLayout();
                Top = bottom - Height;
            }
        }

        public int ActiveCount { get { return _activeCount; } }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _anim.Stop();
            _poll.Stop();
            base.OnFormClosing(e);
        }
    }
}