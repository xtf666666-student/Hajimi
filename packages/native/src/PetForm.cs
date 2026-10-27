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

        private bool _listOpen;
        private int _activeCount;
        private readonly List<SessionItem> _active = new List<SessionItem>();

        private Rectangle _badgeRect;
        private Rectangle[] _rowRects = new Rectangle[0];

        public event Action OpenDshRequested;
        public event Action<SessionItem> SessionRequested;

        public int PetSize { get; private set; }
        public bool DemoRun { get; set; }

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

            _poll = new Timer { Interval = 4000 };
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
            // 底部要留出抬脚 + 地面阴影的余量，否则跑动时脚会被窗口裁掉
            int footRoom = (int)(PetSize * 0.10f) + 6;
            ClientSize = new Size(catW + 14, BadgeH + BadgeGap + catH + footRoom);
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

            Render();
        }

        private void Render()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            bool sprinting = (DemoRun || (_activeCount > 0 && _hover && !_listOpen));

            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                }

                int catH = PetSize;
                int catW = (int)(PetSize * 0.817f);
                int catX = (w - catW) / 2;
                int catY = BadgeH + BadgeGap;
                float scale = PetSize / SRC_H;

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

                // 1. ground shadow (stretches slightly while running)
                int sw = (int)(_shadow.Width * scale * (1f + 0.06f * Math.Abs(dash)));
                int sh = Math.Max(4, (int)(_shadow.Height * scale));
                DrawPlain(canvas, _shadow, new Rectangle((w - sw) / 2, h - sh - 1, sw, sh));

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
                DrawBadge(canvas, w);
                if (_bubbleLeft > 0) DrawBubble(canvas, w, catY);
                if (_listOpen) DrawList(canvas, w, catY);

                ApplyLayered(canvas, w, h);
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

        private void DrawBadge(Bitmap canvas, int w)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool running = _activeCount > 0;
                string text = _activeCount.ToString();

                using (Font font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(text, font);
                    int dot = running ? 6 : 0;
                    int bw = Math.Max(26, (int)ts.Width + 16 + dot);
                    int bh = BadgeH;
                    int bx = (w - bw) / 2;
                    _badgeRect = new Rectangle(bx, 0, bw, bh);

                    Color fill = running
                        ? Color.FromArgb(255, 59, 165, 95)
                        : Color.FromArgb(248, 255, 240, 220);
                    Color line = running
                        ? Color.FromArgb(255, 59, 165, 95)
                        : Color.FromArgb(220, 240, 176, 110);

                    using (GraphicsPath path = RoundRect(_badgeRect, bh / 2))
                    using (SolidBrush bg = new SolidBrush(fill))
                    using (Pen pen = new Pen(line, 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(pen, path);
                    }

                    int textX = bx + 8;
                    if (running)
                    {
                        using (SolidBrush d = new SolidBrush(Color.FromArgb(255, 234, 255, 242)))
                        {
                            g.FillEllipse(d, bx + 7, (bh - 6) / 2, 6, 6);
                        }
                        textX = bx + 17;
                    }

                    using (SolidBrush fg = new SolidBrush(
                        running ? Color.White : Color.FromArgb(169, 99, 31)))
                    {
                        g.DrawString(text, font, fg, textX, (bh - ts.Height) / 2f + 0.5f);
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

        private void DrawList(Bitmap canvas, int w, int catY)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                const int rowH = 19;
                int count = Math.Min(_active.Count, 8);
                if (count == 0)
                {
                    _rowRects = new Rectangle[0];
                    return;
                }

                int lw = Math.Min(w + 70, 210);
                int lh = count * rowH + 9;
                int lx = (w - lw) / 2;
                int ly = Math.Max(0, catY - lh - 3);
                _rowRects = new Rectangle[count];

                using (GraphicsPath path = RoundRect(new Rectangle(lx, ly, lw, lh), 8))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(250, 255, 253, 248)))
                using (Pen pen = new Pen(Color.FromArgb(215, 240, 176, 110), 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(pen, path);
                }

                using (Font font = new Font("Microsoft YaHei UI", 8f))
                {
                    for (int i = 0; i < count; i++)
                    {
                        SessionItem item = _active[i];
                        int ry = ly + 4 + i * rowH;
                        _rowRects[i] = new Rectangle(lx + 3, ry, lw - 6, rowH);

                        using (SolidBrush dot = new SolidBrush(Color.FromArgb(59, 165, 95)))
                        {
                            g.FillEllipse(dot, lx + 8, ry + 6, 6, 6);
                        }
                        string title = item.Title.Length > 14
                            ? item.Title.Substring(0, 14) + "…"
                            : item.Title;
                        using (SolidBrush ink = new SolidBrush(Color.FromArgb(63, 52, 40)))
                        {
                            g.DrawString(title, font, ink, lx + 19, ry + 1);
                        }
                        using (SolidBrush ago = new SolidBrush(Color.FromArgb(176, 154, 132)))
                        {
                            SizeF s = g.MeasureString(item.Ago, font);
                            g.DrawString(item.Ago, font, ago, lx + lw - (int)s.Width - 7, ry + 1);
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
                        _listOpen = false;
                        if (SessionRequested != null && i < _active.Count)
                        {
                            SessionRequested(_active[i]);
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
            _listOpen = !_listOpen;
            if (_listOpen)
            {
                Poll();
                if (_activeCount == 0) Say("没有任务在跑。");
            }
        }

        public void RefreshSessions()
        {
            Poll();
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
            _active.Clear();
            _active.AddRange(items);
            _activeCount = items.Count;
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