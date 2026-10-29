using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HajimiPet
{
    /// <summary>
    /// 回复输入框。
    ///
    /// 桌宠本体是分层窗口（UpdateLayeredWindow），子控件不会被绘制，
    /// 所以输入框必须是独立的窗口，浮在桌宠旁边。
    ///
    /// 视觉：圆角白卡、柔和描边、无系统标题栏，整体像一张小纸条。
    /// 交互：Enter 发送，Shift+Enter 换行，Esc 关闭，点外面自动关闭。
    /// </summary>
    internal sealed class ReplyBox : Form
    {
        private const int Radius = 12;
        private const int PadX = 14;
        private const int PadY = 11;

        private readonly TextBox _input;
        private readonly Label _who;
        private readonly Label _hint;
        private readonly Label _send;
        private bool _sent;

        /// <summary>用户按 Enter（或点发送）时触发，参数是输入文本。</summary>
        public event Action<string> Submitted;

        public ReplyBox(string sessionTitle, Point anchor)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(253, 252, 249);
            DoubleBuffered = true;
            KeyPreview = true;
            Padding = new Padding(PadX, PadY, PadX, PadY);

            int width = 360;
            int height = 138;

            _who = new Label
            {
                Text = Ellipsis(sessionTitle, 34),
                AutoSize = false,
                Height = 17,
                Dock = DockStyle.Top,
                ForeColor = Color.FromArgb(152, 134, 114),
                Font = new Font("Microsoft YaHei UI", 8f),
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 4),
            };

            _input = new TextBox
            {
                Multiline = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 10f),
                ForeColor = Color.FromArgb(52, 42, 32),
                BackColor = Color.FromArgb(253, 252, 249),
                Dock = DockStyle.Fill,
                Text = string.Empty,
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Color.Transparent };

            _hint = new Label
            {
                Text = "Enter 发送 · Esc 取消",
                AutoSize = true,
                ForeColor = Color.FromArgb(176, 158, 138),
                Font = new Font("Microsoft YaHei UI", 7.5f),
                BackColor = Color.Transparent,
                Location = new Point(0, 6),
            };

            _send = new Label
            {
                Text = "发送",
                AutoSize = false,
                Size = new Size(46, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                BackColor = Color.FromArgb(59, 165, 95),
                Cursor = Cursors.Hand,
            };
            StyleSendButton(_send);
            _send.Click += (s, e) => Submit();
            _send.MouseEnter += (s, e) => _send.BackColor = Color.FromArgb(48, 148, 82);
            _send.MouseLeave += (s, e) => _send.BackColor = Color.FromArgb(59, 165, 95);

            bottom.Controls.Add(_hint);
            bottom.Controls.Add(_send);

            Controls.Add(_input);
            Controls.Add(bottom);
            Controls.Add(_who);

            ClientSize = new Size(width, height);
            MinimumSize = new Size(280, height);

            PlaceBeside(anchor, width, height);

            _input.KeyDown += OnKeyDown;
            Deactivate += (s, e) => { if (!_sent) Close(); };
            Shown += (s, e) => { _input.Focus(); };
            Resize += (s, e) => LayoutBottom(bottom);
        }

        private static void StyleSendButton(Label b)
        {
            // 圆角药丸
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
                using (GraphicsPath p = RoundRect(r, r.Height / 2))
                using (SolidBrush bg = new SolidBrush(b.BackColor))
                {
                    g.FillPath(bg, p);
                }
                TextRenderer.DrawText(g, b.Text, b.Font, r, b.ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void LayoutBottom(Panel bottom)
        {
            _send.Location = new Point(bottom.Width - _send.Width, 2);
        }

        /// <summary>浮在桌宠侧边：优先左侧，不行就右侧，始终不出屏。</summary>
        private void PlaceBeside(Point anchor, int width, int height)
        {
            Rectangle work = Screen.FromPoint(anchor).WorkingArea;

            int x = anchor.X - width - 12;
            if (x < work.Left + 6) x = anchor.X + 12;
            if (x + width > work.Right - 6) x = work.Right - width - 6;

            int y = anchor.Y + height / 4;
            if (y + height > work.Bottom - 6) y = work.Bottom - height - 6;
            if (y < work.Top + 6) y = work.Top + 6;

            Location = new Point(x, y);
        }

        private static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (!_sent) Close();
                return;
            }
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                Submit();
            }
        }

        private void Submit()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0)
            {
                if (!_sent) Close();
                return;
            }
            _sent = true;
            Action<string> handler = Submitted;
            Close();
            if (handler != null) handler(text);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var all = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = RoundRect(all, Radius))
            {
                // 投影
                using (GraphicsPath sp = RoundRect(new Rectangle(1, 3, Width - 3, Height - 3), Radius))
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(26, 120, 100, 80)))
                {
                    g.FillPath(sh, sp);
                }
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(253, 252, 249)))
                using (Pen border = new Pen(Color.FromArgb(206, 226, 208, 184), 1f))
                {
                    g.FillPath(bg, p);
                    g.DrawPath(border, p);
                }
            }

            // 顶部一条淡色分隔线
            using (Pen line = new Pen(Color.FromArgb(40, 226, 208, 184), 1f))
            {
                g.DrawLine(line, PadX, PadY + _who.Height + 2,
                    Width - PadX, PadY + _who.Height + 2);
            }
        }
    }
}