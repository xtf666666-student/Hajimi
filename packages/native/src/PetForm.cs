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
        private Rectangle[] _rowRects = new Rectangle[0];

        public event Action OpenDshRequested;
        public event Action<SessionItem> SessionRequested;
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
            _listOpen = listOpen;
            DebugActiveOverride = activeOverride;
            Poll();
            ApplyLayout();
            Render();

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(canvas, w, h);
                canvas.Save(path, ImageFormat.Png);
            }
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

            int baseW = catW + 14;
            int w = listH > 0 ? Math.Max(baseW, ListWidth + 10) : baseW;
            // 底部区域：角标 + 猫 + 阴影（固定的"主体高度"）
            int bodyH = BadgeH + BadgeGap + catH + shadowH;
            // 列表展开时，在主体上方额外加上列表高度
            int h = bodyH + listH;
            ClientSize = new Size(w, h);
        }

        private const int ListWidth = 214;
        private const int ListRowH = 20;
        private const int ListPadY = 6;
        private const int ListMaxRows = 8;

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

            if (_bubbleLeft > 0) _bubbleLeft--;
            if (_badgePulse > 0) _badgePulse--;

            Render();
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
                int catX = (w - catW) / 2;
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

                // 1. 地面阴影：顶端对齐脚底线，向下铺开，紧贴脚底不留缝
                int sw = (int)(_shadow.Width * scale * (1f + 0.06f * Math.Abs(dash)));
                int sh = Math.Max(6, (int)(_shadow.Height * scale));
                DrawPlain(canvas, _shadow,
                    new Rectangle((w - sw) / 2, footY - 1, sw, sh));

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

                DrawBadge(canvas, w, catY);
                if (_bubbleLeft > 0) DrawBubble(canvas, w, catY);
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

        private void DrawBadge(Bitmap canvas, int w, int catY)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool running = _activeCount > 0;

                // 角标紧贴猫头顶：底边落在 catY - BadgeGap，不再固定在最顶部
                int badgeBottom = Math.Max(BadgeH, catY - BadgeGap);
                int badgeTop = badgeBottom - BadgeH;

                if (!running)
                {
                    // 没有任务在跑：只留一个灰色小胶囊，不显示 0
                    const int idleW = 22;
                    int bh = BadgeH;
                    int bx = (w - idleW) / 2;
                    _badgeRect = new Rectangle(bx, badgeTop, idleW, bh);

                    using (GraphicsPath path = RoundRect(_badgeRect, bh / 2))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(232, 228, 220, 206)))
                    using (Pen pen = new Pen(Color.FromArgb(200, 205, 192, 176), 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(pen, path);
                    }
                    using (SolidBrush dot = new SolidBrush(Color.FromArgb(190, 170, 150, 128)))
                    {
                        g.FillEllipse(dot, bx + idleW / 2 - 3, badgeTop + (bh - 6) / 2, 6, 6);
                    }
                    return;
                }

                string text = _activeCount.ToString();
                using (Font font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(text, font);
                    int bw = Math.Max(26, (int)ts.Width + 16 + 6);
                    int bh = BadgeH;
                    int bx = (w - bw) / 2;

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

        private void DrawBubble(Bitmap canvas, int w, int catY)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Font font = new Font("Microsoft YaHei UI", 8f))
                {
                    SizeF ts = g.MeasureString(_bubble, font);
                    int bw = (int)ts.Width + 14;
                    int bh = (int)ts.Height + 7;
                    int bx = (w - bw) / 2;
                    int by = BadgeH + BadgeGap + 1;
                    using (GraphicsPath path = RoundRect(new Rectangle(bx, by, bw, bh), 8))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(248, 255, 253, 248)))
                    using (Pen pen = new Pen(Color.FromArgb(200, 240, 176, 110), 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(pen, path);
                    }
                    using (SolidBrush ink = new SolidBrush(Color.FromArgb(63, 52, 40)))
                    {
                        g.DrawString(_bubble, font, ink, bx + 7, by + 3);
                    }
                }
            }
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

                int lw = Math.Min(w - 8, ListWidth);
                int lh = count * ListRowH + ListPadY;
                int lx = (w - lw) / 2;
                int ly = Math.Max(0, listTop);
                _rowRects = new Rectangle[count];

                // 让列表底边正好落在 listTop + listH = 角标顶边，不留空隙
                lh = Math.Max(ListRowH + ListPadY, Math.Min(lh, listTop + lh - ly));

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

                        using (SolidBrush dot = new SolidBrush(Color.FromArgb(59, 165, 95)))
                        {
                            g.FillEllipse(dot, lx + 9, ry + 7, 6, 6);
                        }

                        string ago = item.Ago;
                        SizeF agoSize = g.MeasureString(ago, font);
                        int titleRight = lx + lw - (int)agoSize.Width - 10;
                        int titleMax = Math.Max(40, titleRight - (lx + 21));
                        string title = item.Title;
                        while (title.Length > 1 && g.MeasureString(title, font).Width > titleMax)
                        {
                            title = title.Substring(0, title.Length - 1);
                        }
                        if (title.Length < item.Title.Length) title = title.TrimEnd() + "…";

                        using (SolidBrush ink = new SolidBrush(Color.FromArgb(63, 52, 40)))
                        {
                            g.DrawString(title, font, ink, lx + 21, ry + 2);
                        }
                        using (SolidBrush agoBrush = new SolidBrush(Color.FromArgb(176, 154, 132)))
                        {
                            g.DrawString(ago, font, agoBrush, lx + lw - (int)agoSize.Width - 8, ry + 2);
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
                for (int i = 0; i < _rowRects.Length; i++)
                {
                    if (_rowRects[i].Contains(e.Location))
                    {
                        SessionItem picked = (i < _active.Count) ? _active[i] : null;
                        CloseList();
                        if (SessionRequested != null && picked != null)
                        {
                            SessionRequested(picked);
                        }
                        return;
                    }
                }
            }

            if (_badgeRect.Contains(e.Location))
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
                    items.Add(new SessionItem
                    {
                        Id = "debug-" + i,
                        Title = "调试会话 " + (i + 1),
                        Active = true,
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
            _activeCount = items.Count;

            if (changed)
            {
                // 新任务出现时让角标跳一下，用户能立刻看到
                if (_activeCount > _lastNotified)
                {
                    _badgePulse = 8;
                }
                _lastNotified = _activeCount;

                // 列表正展开时，行数变了就要同步调整窗口高度
                if (_listOpen)
                {
                    int bottom = Top + Height;
                    ApplyLayout();
                    Top = bottom - Height;
                }

                Action<int> handler = ActiveCountChanged;
                if (handler != null) handler(_activeCount);
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