using System;
using System.Drawing;
using System.Windows.Forms;

namespace HajimiPet
{
    /// <summary>
    /// 回复输入框。
    ///
    /// 桌宠本体是分层窗口（UpdateLayeredWindow），子控件不会被绘制，
    /// 所以输入框必须是独立的普通窗口，浮在桌宠旁边。
    ///
    /// 行为：Enter 发送，Esc 取消，点别处失焦即取消。
    /// </summary>
    internal sealed class ReplyBox : Form
    {
        private readonly TextBox _input;
        private readonly Label _hint;
        private readonly string _sessionTitle;

        /// <summary>用户按 Enter 提交时触发，参数为输入文本。</summary>
        public event Action<string> Submitted;

        public ReplyBox(string sessionTitle, Point anchor, string initialText)
        {
            _sessionTitle = sessionTitle ?? string.Empty;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(253, 251, 246);
            Padding = new Padding(1);
            DoubleBuffered = true;
            KeyPreview = true;

            int width = 340;
            int height = 92;

            _hint = new Label
            {
                Text = Ellipsis(_sessionTitle, 30),
                AutoSize = false,
                Height = 18,
                Dock = DockStyle.Top,
                ForeColor = Color.FromArgb(140, 122, 102),
                Font = new Font("Microsoft YaHei UI", 8f),
                Padding = new Padding(10, 4, 8, 0),
                BackColor = Color.Transparent,
            };

            _input = new TextBox
            {
                Multiline = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = Color.FromArgb(60, 50, 40),
                BackColor = Color.FromArgb(253, 251, 246),
                Height = 52,
                Dock = DockStyle.Fill,
                Text = initialText ?? string.Empty,
            };

            var hintBar = new Label
            {
                Text = "Enter 发送 · Esc 取消",
                AutoSize = false,
                Height = 16,
                Dock = DockStyle.Bottom,
                ForeColor = Color.FromArgb(170, 152, 132),
                Font = new Font("Microsoft YaHei UI", 7.5f),
                Padding = new Padding(10, 0, 8, 3),
                BackColor = Color.Transparent,
            };

            Controls.Add(_input);
            Controls.Add(hintBar);
            Controls.Add(_hint);

            ClientSize = new Size(width, height);
            MinimumSize = new Size(240, height);

            // 浮在桌宠左侧，并保证不出屏
            Rectangle work = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - width - 10;
            if (x < work.Left + 4) x = anchor.X + 10;
            if (x + width > work.Right - 4) x = work.Right - width - 4;
            int y = anchor.Y;
            if (y + height > work.Bottom - 4) y = work.Bottom - height - 4;
            if (y < work.Top + 4) y = work.Top + 4;
            Location = new Point(x, y);

            _input.KeyDown += OnInputKeyDown;
            Deactivate += (s, e) => Cancel();
            Shown += (s, e) =>
            {
                _input.Focus();
                _input.SelectionStart = _input.TextLength;
            };
        }

        private static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Cancel();
                return;
            }

            // Enter 直接发送；Shift+Enter 换行
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                string text = _input.Text.Trim();
                if (text.Length == 0)
                {
                    Cancel();
                    return;
                }
                Action<string> handler = Submitted;
                Close();
                if (handler != null) handler(text);
            }
        }

        private void Cancel()
        {
            if (!IsDisposed) Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(224, 205, 178), 1f))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
