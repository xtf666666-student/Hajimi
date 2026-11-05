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
        /// <summary>
        /// 上次布局时气泡是否可见。
        ///
        /// 必须用这个字段做比较，不能用"本帧开始/结束是否可见"——
        /// Say() 在 Tick 之外调用，等 Tick 开始时气泡已经可见，
        /// 前后对比永远相等，Reflow() 不触发，窗口宽度不长，
        /// 气泡就挤在原宽度里压到桌宠身上。
        /// </summary>
        private bool _bubbleLaidOut;
        private int _badgePulse;

        /// <summary>
        /// 当前鼠标悬停在哪个按钮上（-1 = 无）。
        /// 用"卡片序号 + 按钮种类"标识，因为按钮矩形每帧都会重算。
        /// 悬停时按钮底色加深、图标变深，给用户即时反馈 ——
        /// 否则点下去没有视觉变化，会以为程序卡住。
        /// </summary>
        private int _hoverRow = -1;
        private IconKind _hoverKind = IconKind.Reply;
        private bool _hoverAnyBtn;

        /// <summary>
        /// 鼠标当前悬停在**哪张卡片**上（-1 = 没有）。
        /// 对应 CSS 的 :hover —— × 只在鼠标进入该卡片时才显示。
        /// </summary>
        private int _hoverCardRow = -1;

        /// <summary>
        /// 每张卡片的 × 淡入进度 [0,1]，用于 transition: opacity 0.2s 的等效过渡。
        /// 按行号索引，和 _active 的卡片一一对应。
        /// </summary>
        private float[] _closeFade = new float[0];

        /// <summary>
        /// 上一帧实际使用的卡片动作按钮直径。
        /// 指示器（脚底小圆点/收起箭头）读它来保持同样大小；
        /// 列表还没画过时用 BtnSize 兜底。
        /// </summary>
        private int _actionBtnD;
        private int _lastNotified;

        private bool _listOpen;
        private int _activeCount;
        private readonly List<SessionItem> _active = new List<SessionItem>();

        private Rectangle _badgeRect;
        private Rectangle _lastBubbleRect;
        private Rectangle _lastListRect;
        /// <summary>整摞卡片的外接矩形，用于「点堆叠区域任意位置展开」。</summary>
        private Rectangle _lastStackRect;
        private Rectangle _lastShadowRect;
        private Rectangle _lastCatRect;
        private Rectangle[] _rowRects = new Rectangle[0];
        private Rectangle[] _btnReply = new Rectangle[0];
        private Rectangle[] _btnStop = new Rectangle[0];
        private Rectangle[] _btnClose = new Rectangle[0];

        private enum IconKind { Reply, Stop, Close }

        // 会话行右侧的图标按钮尺寸
        // 卡片右侧动作按钮的**基准直径**。
        // 实际直径由"左侧两行文字的实测高度"决定（见 DrawStackCard），
        // 这里是夹取上下限时的常量；指示器（小圆点/收起箭头）也用同一套上下限，
        // 保证两处视觉大小一致。
        private const int BtnSize = 26;
        private const int BtnMinD = 18;
        private const int BtnMaxD = 40;

        /// <summary>
        /// 卡片动作按钮的直径 —— 与左侧两行内容同高（用户要求）。
        /// 指示器也用这个值，保证"脚底那颗圆点/箭头"和卡片按钮一样大。
        /// </summary>
        private int ActionBtnDiameter { get { return BtnSize; } }

        // 卡片堆叠布局（iPhone 通知那种）
        private readonly StackLayout _stack = new StackLayout();

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
        /// <summary>测试用：强制"鼠标在桌宠上"，用于导出悬停态（数字圆点）。</summary>
        public bool ForceHover { get; set; }

        // ---------------- 接近感应（带迟滞） ----------------
        // 用户要求：鼠标"接近"就把细线换成数字圆点，不用等到真的压在桌宠身上；
        // 而且一旦变成圆点，不要稍微动一下就退回细线，必须离开足够远才收回去。
        //
        // 这就是标准的迟滞（hysteresis）：进入阈值 < 退出阈值，
        // 两个阈值之间保持原状态，鼠标在边界抖动时不会来回闪。

        /// <summary>鼠标距猫身多少像素以内 -> 变成数字圆点（按桌宠尺寸等比）。</summary>
        private int NearEnterPx { get { return Math.Max(18, (int)(PetSize * 0.30f)); } }
        /// <summary>鼠标离开到多少像素以外 -> 才退回细线（明显大于进入阈值）。</summary>
        private int NearExitPx { get { return Math.Max(40, (int)(PetSize * 0.65f)); } }

        /// <summary>当前是否处于"接近"状态（决定画圆点还是细线）。</summary>
        private bool _near;

        // ---- 细线 <-> 圆点 的形态弹簧 ----
        // 0 = 细线，1 = 圆点。
        // 参数刻意和会话列表的展开弹簧保持一致（200/13）：用户觉得列表那个速度正好，
        // 两处用同一组参数，观感才统一。实测约 145ms 到位，仍带一次回弹。
        private float _morph;
        private float _morphVel;
        private const float MorphStiffness = 200f;
        private const float MorphDamping = 13f;

        /// <summary>测试用：强制覆盖"进行中对话数"，用于验证空闲态外观。</summary>
        public int? DebugActiveOverride { get; set; }

        

        /// <summary>导图用：把任务框偏移立刻摆到目标位置（不在过渡中途截图）。</summary>
        private void SnapListOffset()
        {
            _snapOffsetOnly = true;

            _listOffX = 0f;
            _listOffXVel = 0f;
            _listOffYVel = 0f;
            _listOffY = -ListBottomGap;

            // 导图时把 × 的淡入状态设成满（模拟"鼠标悬停在卡片上"），
            // 否则导出的图里 × 全是隐藏的，看不出布局对不对。
            int count = Math.Min(_active.Count, StackLayout.MaxCards);
            _closeFade = new float[count];
            for (int i = 0; i < count; i++) _closeFade[i] = 1f;
        }

        /// <summary>回到实时模式，恢复屏幕适配。</summary>
        private void ResumeListOffset()
        {
            _snapOffsetOnly = false;
        }

        /// <summary>导图用：把当前渲染结果导出为 PNG。</summary>
        /// 分层窗口内容无法被 PrintWindow / 抓屏可靠捕获，所以让程序自己吐图，
        /// 这样验证的是真实渲染输出，不依赖系统合成状态。
        /// </summary>
        public void DumpFrame(string path, bool listOpen, int activeOverride)
        {
            // -1 表示"用真实数据，不覆盖"，用于核对角标计数
            DebugActiveOverride = activeOverride < 0 ? (int?)null : activeOverride;
            // 这个导出方法要的是"鼠标不在身上"的普通态，必须清掉悬停覆盖，
            // 否则前一次 DumpFrameHover 的 ForceHover 会残留。
            ForceHover = false;
            // 导图要的是"细线"终态，形态弹簧直接归零，否则会停在过渡中途
            _morph = 0f;
            _morphVel = 0f;
            // 显现弹簧也要复位：上一次 DumpFrameReveal 留下的非零值
            // 会让 CurrentListHeight 误以为列表还占着高度，窗口宽度跟着错。
            _reveal = 0f;
            _revealVel = 0f;
            _listSpaceAllocated = false;
            Poll();
            _listOpen = listOpen;
            // 列表打开时显现必须置满：_reveal 控制整层透明度，
            // 留在 0 会让导出的卡片完全透明（看起来像列表没画出来）。
            _reveal = listOpen ? 1f : 0f;
            _revealVel = 0f;
            _listSpaceAllocated = listOpen;
            if (_listOpen)
            {
                // 导图要求稳定的画面，不能停在弹簧中途
                int rows = Math.Min(_active.Count, StackLayout.MaxCards);
                _stack.SetRows(rows);
                _stack.SnapSpread(1f);
            }
            // Poll 之后才知道有多少行，必须重新按行数布局
            ApplyLayout();
            // 任务框偏移直接摆到目标位，避免导出时停在过渡中途
            SnapListOffset();
            ApplyLayout();
            SnapListOffset();
            Render();

            SaveFrame(path);
        }

        /// <summary>
        /// 自检：模拟"点开列表"的动画过程，统计帧数、窗口改尺寸次数、耗时。
        ///
        /// 用户两次反馈"展开很慢 / 明显卡顿掉帧"，光看静态截图没法证明修好没有，
        /// 所以把这段过程量化出来。
        /// </summary>
        public string RunAnimProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = false;
            Poll();

            _listOpen = true;
            _listExpanded = false;
            _stack.SetRows(Math.Min(rows, StackLayout.MaxCards));
            _stack.SnapSpread(0f);
            ApplyLayout();
            int startH = Height;

            // 展开
            _listExpanded = true;
            _stack.SetSpread(true);
            Reflow();
            int afterOpenH = Height;

            int sizeChanges = (afterOpenH != startH) ? 1 : 0;
            int frames = 0;
            double ms = 0;
            double settleMs = -1;
            const double dtMs = 16.0;   // 60fps

            for (int i = 0; i < 240; i++)
            {
                float dt = (float)(dtMs / 1000.0);
                bool moving = _stack.Step(dt);
                frames++;
                ms += dtMs;
                if (settleMs < 0 && _stack.Spread >= 0.95f) settleMs = ms;
                if (!moving)
                {
                    break;
                }
            }

            int endH = Height;
            return "rows=" + rows +
                   " frames=" + frames +
                   " 帧时长=" + dtMs + "ms" +
                   " 展开耗时=" + ms.ToString("0") + "ms" +
                   " 到95%=" + (settleMs < 0 ? "n/a" : settleMs.ToString("0") + "ms") +
                   " 窗口改尺寸次数=" + sizeChanges +
                   " 高度 " + startH + "->" + afterOpenH + "->" + endH;
        }

        /// <summary>导出"悬停态"：脚底指示器显示数字圆点（附件二）。</summary>
        public void DumpFrameHover(string path, int activeOverride)
        {
            DebugActiveOverride = activeOverride < 0 ? (int?)null : activeOverride;
            ForceHover = true;
            // 导图要的是"圆点"终态，形态弹簧直接置 1
            _morph = 1f;
            _morphVel = 0f;
            // 列表相关状态必须清掉：上一次导图留下的 _reveal/_listOpen
            // 会让窗口按"列表展开"算宽度，导出的图宽度就不对了。
            _listOpen = false;
            _listExpanded = false;
            _reveal = 0f;
            _revealVel = 0f;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            Poll();
            ApplyLayout();
            SnapListOffset();
            Render();
            SaveFrame(path);
        }

        /// <summary>
        /// 自检：模拟"弹出 -> 收回"全过程，逐帧记录窗口高度。
        ///
        /// 要证明两件事：
        ///   1. 收回不是瞬时清空，而是有中间帧（否则用户会觉得"消失得太快"）
        ///   2. 收回过程中窗口尺寸**不能每帧变化**（那会掉帧卡顿）
        /// </summary>
        public string RunCloseProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _reveal = 0f;
            _revealVel = 0f;
            Poll();

            // ---- 弹出 ----
            _listOpen = true;
            _listExpanded = false;
            _stack.SetRows(Math.Min(rows, StackLayout.MaxCards));
            _stack.SnapSpread(0f);
            _stack.SetSpread(false);
            _reveal = 0f;
            _revealVel = 0f;
            _listSpaceAllocated = true;
            Reflow();

            const float dt = 0.016f;
            int openFrames = 0;
            int openHeightChanges = 0;
            int lastH = Height;
            for (int i = 0; i < 200; i++)
            {
                StepReveal(dt);
                bool needSpace = _listOpen || _reveal > 0.002f;
                if (needSpace != _listSpaceAllocated)
                {
                    _listSpaceAllocated = needSpace;
                    Reflow();
                }
                if (Height != lastH) { openHeightChanges++; lastH = Height; }
                openFrames++;
                if (Math.Abs(_reveal - 1f) < 0.002f) break;
            }

            // ---- 收回 ----
            CloseList();
            var changes = new System.Text.StringBuilder();
            int closeFrames = 0;
            int closeHeightChanges = 0;
            var heights = new System.Text.StringBuilder();
            for (int i = 0; i < 200; i++)
            {
                StepReveal(dt);
                bool needSpace = _listOpen || _reveal > 0.002f;
                if (needSpace != _listSpaceAllocated)
                {
                    _listSpaceAllocated = needSpace;
                    Reflow();
                }
                if (Height != lastH)
                {
                    closeHeightChanges++;
                    changes.Append("   帧").Append(closeFrames)
                           .Append(" 高 ").Append(lastH).Append("->").Append(Height)
                           .Append(" needSpace=").Append(needSpace ? "1" : "0").Append('\n');
                    lastH = Height;
                }
                if (closeFrames < 6 || Math.Abs(_reveal) < 0.002f)
                {
                    heights.Append("   帧").Append(closeFrames)
                           .Append(" reveal=").Append(_reveal.ToString("0.000"))
                           .Append(" 窗高=").Append(Height).Append('\n');
                }
                closeFrames++;
                if (Math.Abs(_reveal) < 0.002f) break;
            }

            return "弹出 帧数=" + openFrames + " 窗口改尺寸=" + openHeightChanges +
                   "\n收回 帧数=" + closeFrames + " 窗口改尺寸=" + closeHeightChanges +
                   "\n收回前几帧:\n" + heights +
                   "高度变化明细:\n" + changes +
                   (closeFrames > 3 ? "PASS" : "FAIL") + " 收回有中间帧" +
                   "\n" + (closeHeightChanges <= 1 ? "PASS" : "FAIL") + " 收回时窗口只改一次尺寸";
        }

        /// <summary>
        /// 自检：把"弹出 / 展开 / 收回"三个动作的到位时间一起量出来，
        /// 确认它们用的是同一速度（用户要求三者统一）。
        /// </summary>
        public string RunMotionProbe(int rows)
        {
            var sb = new System.Text.StringBuilder();
            const double dtMs = 16.0;
            float dt = (float)(dtMs / 1000.0);

            // 1) 弹出：reveal 0 -> 1
            _reveal = 0f;
            _revealVel = 0f;
            double ms = 0, t1 = -1;
            for (int i = 0; i < 400; i++)
            {
                float acc = -RevealStiffness * (_reveal - 1f) - RevealDamping * _revealVel;
                _revealVel += acc * dt;
                _reveal += _revealVel * dt;
                ms += dtMs;
                if (t1 < 0 && _reveal >= 0.95f) t1 = ms;
                if (Math.Abs(_reveal - 1f) < 0.002f && Math.Abs(_revealVel) < 0.02f) break;
            }
            sb.Append("弹出列表   到95%=").Append(t1 < 0 ? "n/a" : t1.ToString("0") + "ms").Append('\n');

            // 2) 展开卡片：spread 0 -> 1
            _stack.SetRows(Math.Min(rows, StackLayout.MaxCards));
            _stack.SnapSpread(0f);
            _stack.SetSpread(true);
            ms = 0;
            double t2 = -1;
            for (int i = 0; i < 400; i++)
            {
                _stack.Step(dt);
                ms += dtMs;
                if (t2 < 0 && _stack.Spread >= 0.95f) t2 = ms;
                if (_stack.Spread >= 0.999f) break;
            }
            sb.Append("展开卡片   到95%=").Append(t2 < 0 ? "n/a" : t2.ToString("0") + "ms").Append('\n');

            // 3) 收回：reveal 1 -> 0
            _reveal = 1f;
            _revealVel = 0f;
            ms = 0;
            double t3 = -1;
            for (int i = 0; i < 400; i++)
            {
                float acc = -RevealStiffness * (_reveal - 0f) - RevealDamping * _revealVel;
                _revealVel += acc * dt;
                _reveal += _revealVel * dt;
                ms += dtMs;
                if (t3 < 0 && _reveal <= 0.05f) t3 = ms;
                if (Math.Abs(_reveal) < 0.002f && Math.Abs(_revealVel) < 0.02f) break;
            }
            sb.Append("收回列表   到95%=").Append(t3 < 0 ? "n/a" : t3.ToString("0") + "ms").Append('\n');

            // 4) 形态：细线 <-> 圆点
            _morph = 0f;
            _morphVel = 0f;
            ms = 0;
            double t4 = -1;
            for (int i = 0; i < 400; i++)
            {
                float acc = -MorphStiffness * (_morph - 1f) - MorphDamping * _morphVel;
                _morphVel += acc * dt;
                _morph += _morphVel * dt;
                ms += dtMs;
                if (t4 < 0 && _morph >= 0.95f) t4 = ms;
                if (Math.Abs(_morph - 1f) < 0.002f && Math.Abs(_morphVel) < 0.02f) break;
            }
            sb.Append("形态过渡   到95%=").Append(t4 < 0 ? "n/a" : t4.ToString("0") + "ms");

            _reveal = 0f;
            _revealVel = 0f;
            _morph = 0f;
            _morphVel = 0f;
            return sb.ToString();
        }

        /// <summary>
        /// 自检：验证"说话时窗口会变宽"这条路径。
        ///
        /// 之前的 bug 是 Tick 里用"本帧前后是否可见"比较，而 Say() 在 Tick 之外
        /// 调用，比较永远相等 -> 从不 Reflow -> 气泡挤在旧宽度里压住桌宠。
        /// 这里直接复现这段逻辑，确认宽度真的长大了。
        /// </summary>
        public string RunBubbleProbe()
        {
            DebugActiveOverride = 0;
            _bubble = string.Empty;
            _bubbleLeft = 0;
            _bubbleLaidOut = false;
            ApplyLayout();
            int before = Width;

            Say("在呢。");

            // 复刻 Tick 里的判定
            bool bubbleNow = BubbleVisible();
            bool wouldReflow = (bubbleNow != _bubbleLaidOut);
            if (wouldReflow)
            {
                _bubbleLaidOut = bubbleNow;
                Reflow();
            }
            int after = Width;

            // 在气泡可见的状态下渲染一帧，拿到真实的气泡/猫矩形
            using (Bitmap probe = new Bitmap(after, Height, PixelFormat.Format32bppArgb))
            {
                DrawScene(probe, after, Height);
            }
            int gap = _lastCatRect.X - (_lastBubbleRect.X + _lastBubbleRect.Width);
            bool inWindow = _lastBubbleRect.X >= 0 &&
                            _lastBubbleRect.X + _lastBubbleRect.Width <= after;

            // 再让它消失，确认会缩回去
            _bubbleLeft = 0;
            bubbleNow = BubbleVisible();
            if (bubbleNow != _bubbleLaidOut)
            {
                _bubbleLaidOut = bubbleNow;
                Reflow();
            }
            int restored = Width;

            bool grew = after > before;
            bool shrank = Math.Abs(restored - before) <= 2;

            return "说话前宽=" + before + "  说话后宽=" + after +
                   "  收回后宽=" + restored +
                   "\n气泡矩形=" + _lastBubbleRect.X + "," + _lastBubbleRect.Y +
                   "," + _lastBubbleRect.Width + "," + _lastBubbleRect.Height +
                   "  猫左缘=" + _lastCatRect.X +
                   "\n气泡右缘到猫左缘=" + gap + "px" +
                   "\n" + (grew ? "PASS" : "FAIL") + " 说话时窗口变宽" +
                   "\n" + (shrank ? "PASS" : "FAIL") + " 气泡消失后窗口缩回" +
                   "\n" + (gap >= 2 ? "PASS" : "FAIL") + " 气泡不压桌宠" +
                   "\n" + (inWindow ? "PASS" : "FAIL") + " 气泡完整在窗口内";
        }

        /// <summary>导出指定显现进度下的列表，用于查看淡入中间态。</summary>
        public void DumpFrameReveal(string path, int activeOverride, float reveal, bool expanded)
        {
            DebugActiveOverride = activeOverride < 0 ? (int?)null : activeOverride;
            ForceHover = false;
            _morph = 0f;
            _morphVel = 0f;
            Poll();
            _listOpen = true;
            _listExpanded = expanded;
            int rows = Math.Min(_active.Count, StackLayout.MaxCards);
            _stack.SetRows(rows);
            _stack.SnapSpread(expanded ? 1f : 0f);
            _reveal = reveal;
            _revealVel = 0f;
            _listSpaceAllocated = true;
            ApplyLayout();
            SnapListOffset();
            Render();
            SaveFrame(path);
        }

        /// <summary>导出指定形态进度下的画面，用于查看细线→圆点的中间形状。</summary>
        public void DumpFrameMorph(string path, int activeOverride, float morph)
        {
            DebugActiveOverride = activeOverride < 0 ? (int?)null : activeOverride;
            ForceHover = true;          // 判定走"要圆点"，具体形状由 morph 决定
            _morph = morph;
            _morphVel = 0f;
            // 同 DumpFrameHover：清掉列表状态，避免宽度被带偏
            _listOpen = false;
            _listExpanded = false;
            _reveal = 0f;
            _revealVel = 0f;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            Poll();
            ApplyLayout();
            SnapListOffset();
            Render();
            SaveFrame(path);
        }

        /// <summary>导出"叠好的状态"（iPhone 通知那种摞在一起）。</summary>
        public void DumpFrameStacked(string path, int activeOverride)
        {
            DebugActiveOverride = activeOverride < 0 ? (int?)null : activeOverride;
            ForceHover = true;          // 指示器画成圆点/箭头，与真实弹出后一致
            _morph = 1f;
            _morphVel = 0f;
            Poll();
            _listOpen = true;
            _listExpanded = false;
            int rows = Math.Min(_active.Count, StackLayout.MaxCards);
            _stack.SetRows(rows);
            _stack.SnapSpread(0f);
            // 显现必须置满，否则整层透明，导出的图上看不到卡片
            _reveal = 1f;
            _revealVel = 0f;
            _listSpaceAllocated = true;
            ApplyLayout();
            SnapListOffset();
            Render();
            SaveFrame(path);
        }

        private void SaveFrame(string path)
        {
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
            SnapListOffset();
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

            _anim = new Timer { Interval = 16 };   // 60fps：弹簧只有 0.2 秒，30fps 只有 6 帧，必然显得一顿一顿
            _anim.Tick += (s, e) => Tick();
            _anim.Start();

            // 实时运行：恢复屏幕适配（导图钩子会临时关掉它）
            ResumeListOffset();
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
            Reflow();
            Render();
        }

        /// <summary>
        /// 猫在窗口内的左边界 —— **永远居中**。
        ///
        /// 让猫固定在窗口正中央是这套布局的地基：
        ///   - 窗口位置 = 鼠标拖到哪，猫就在哪（不会在窗口里跳）
        ///   - 任务框用自己的偏移量在窗口内移动，不需要窗口挪窝
        /// 之前让猫靠向窗口边缘（_hugT），拖动时猫在窗口里的位置会跳，
        /// 任务框追不上，看起来就是"鼠标动任务框也跟着乱动"。
        /// </summary>
        private int CatLeftInWindow(int w)
        {
            int catW = (int)(PetSize * 0.817f);
            int left = (w - catW) / 2;
            if (left < 0) left = 0;
            return left;
        }

        /// <summary>桌宠离屏幕边缘至少留这么多像素，拖不过去。</summary>
        private const int EdgeMargin = 2;

        /// <summary>
        /// 任务框**开始让位**的触发距离（相对屏幕边缘）。
        ///
        /// 这是"提前量"：不等卡片真的贴到屏幕边才动，而是离边还有这么远
        /// 就开始向反方向平移 / 向上翻转。
        ///
        /// 取 28px —— 约卡片高度的 1/2、卡宽的 1/9：
        ///   - 太小（8px）会显得"贴边才反应"
        ///   - 太大（56px）窗口要放得下更远的横移，会撑到 608px 宽，
        ///     透明区挡鼠标，得不偿失
        /// 28px 比原来提前了约 3 倍，视觉上明显是"还没碰边就让开了"。
        /// </summary>
        private const int ListEdgeMargin = 28;

        /// <summary>
        /// 任务框让位后，仍需与屏幕边缘保持的最小间距。
        /// 和触发距离分开，这样"提前开始动"和"动到哪儿停下"是两回事。
        /// </summary>
        private const int ListMinGap = 8;

        // ---- 任务框相对桌宠的摆放（贴边时自动四向适配） ----
        // _listOffX: 卡片摞中心相对"猫头顶中心"的横向偏移（屏幕像素）
        // _listOffY: 卡片摞底边相对"猫头顶"的纵向偏移；正常为负（在头顶上方）
        private float _listOffX;
        private float _listOffY;
        private float _listOffXVel;
        private float _listOffYVel;
        /// <summary>导图时置 true：跳过屏幕适配，任务框固定在默认位置。</summary>
        private bool _snapOffsetOnly;
        private float _listTarX;    // 目标：通常是 0（与猫中心对齐）
        private float _listTarY;    // 目标：通常是 -(stackH + gap)，放在头顶上方

        /// <summary>
        /// 窗口顶部/底部为任务框预留的高度（**常驻固定值**）。
        ///
        /// 不论列表开没开、有几行，这个值都不变，所以窗口尺寸永远固定，
        /// 猫的位置也就永远不需要因为布局变化而调整。
        /// 按 3 行作为最小常驻量，超过就按实际行数放宽。
        /// </summary>
        private int ListAreaFor(int rows)
        {
            // **恒定值**：不论列表开没开、有几行，始终按最大容量预留。
            //
            // 这是"除拖动外任何操作都不改变桌宠位置"的地基：
            // 只要 listArea 不变，窗口尺寸和猫在窗口内的 y 就都不变，
            // 任何布局切换都不会引起窗口位移。
            // 代价是空闲时窗口也占着这块透明区域。
            int r = 3;   // 常驻预留 3 行的高度，够用且不至于把窗口撑到 960px
            return StackLayout.ExpandedHeight(r) + ListTopPad + ListBottomGap + 4;
        }

        /// <summary>最近一次绘制时猫在**屏幕**上的矩形（仅用于诊断/自检）。</summary>
        private Rectangle _lastCatScreenRect;

        /// <summary>
        /// 猫在屏幕上的锚点（左上角）。**这是唯一的位置基准**。
        ///
        /// 只有拖动会改它。任何布局变化（列表弹出/展开/收回、角标出现、
        /// 气泡出现、窗口改尺寸）都从它反推窗口的 Left/Top，
        /// 保证猫在屏幕上纹丝不动。
        ///
        /// 之前用"上次绘制位置反推 + 增量修正"，连续操作会累积误差，
        /// 实测点两次圆点就偏移了 203px。
        /// </summary>
        private Point _catAnchor;
        private bool _catAnchorValid;

        /// <summary>
        /// 取得（必要时初始化）猫的屏幕锚点。
        /// 首次调用时用当前窗口反推出猫现在在哪，之后一直沿用，
        /// 只有拖动才会写新值。
        /// </summary>
        private Point EnsureCatAnchor(int winW)
        {
            if (!_catAnchorValid)
            {
                int catH = PetSize;
                int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;
                int listArea = ListAreaFor(_active.Count);
                _catAnchor = new Point(
                    Left + CatLeftInWindow(winW),
                    Top + CatYInWindow(listArea, catH, indicatorArea));
                _catAnchorValid = true;
            }
            return _catAnchor;
        }

        /// <summary>把锚点更新到当前窗口对应的猫位置（拖动时调用）。</summary>
        private void SetCatAnchorFromWindow()
        {
            int catH = PetSize;
            int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;
            int listArea = ListAreaFor(_active.Count);
            _catAnchor = new Point(
                Left + CatLeftInWindow(Width),
                Top + CatYInWindow(listArea, catH, indicatorArea));
            _catAnchorValid = true;
        }

        private int CatYInWindow(int listArea, int catH, int indicatorArea)
        {
            // 猫头顶在窗口内的 y = 上方为任务框预留的高度。
            // 窗口总高由 ApplyLayout 保证 >= listArea + catH + indicatorArea
            // + 下方预留，所以这个返回值一定让猫完整落在窗口内。
            return listArea;
        }

        /// <summary>
        /// 窗口下方为任务框预留的高度（翻转到猫脚下方时用）。
        /// ApplyLayout 和 UpdateListTarget 必须用同一个值。
        /// </summary>
        private int BelowAreaFor(int listArea, int catH, int indicatorArea)
        {
            int rows = Math.Min(_active.Count, StackLayout.MaxCards);
            if (!_listOpen && _reveal <= 0.002f) return listArea;
            if (rows <= 0) return listArea;

            // 注意：调用方算总高时已经把 catH 加进去了，这里**不能再加 catH**。
            // 只需要"指示器槽位 + 呼吸 + 整摞高度"。
            int need = indicatorArea + ListBottomGap
                     + StackLayout.ExpandedHeight(rows) + ListTopPad;
            return Math.Max(listArea, need);
        }

        private void ApplyLayout()
        {
            int catH = PetSize;
            int catW = (int)(PetSize * 0.817f);
            int shadowH = 0;  // 用户要求去掉地面灰色块

            int listH = CurrentListHeight();

            // 任务框以猫头顶中心为基准，贴边时会左右让位 / 上下翻转。
            // 窗口必须**一次留足**所有可能的位置，任何方向都不能裁掉卡片。
            //
            // 宽度：卡片居中时占 CardW；最大横移 ±(CardW/2 + ListEdgeMargin)。
            //       所以窗口至少要 CardW + 2*ListEdgeMargin 才放得下最极端位置。
            // 高度：上下各留一份展开高度（翻转时用），都按**最大**高度算。
            int rows = Math.Min(_active.Count, StackLayout.MaxCards);
            int listMaxH = listH > 0 ? StackLayout.ExpandedHeight(rows) : 0;

            // 卡片可能横移的最大距离（相对窗口中心）
            // 让位时卡片中心最远能偏到 ±(CardW/2 + ListEdgeMargin)，见 UpdateListTarget
            int shiftMax = StackLayout.CardW / 2 + ListEdgeMargin;
            // 窗口半宽必须 >= shiftMax + CardW/2，即整窗 >= CardW + 2*shiftMax
            int needW = StackLayout.CardW + shiftMax * 2;

            int baseW = catW + 14;
            int w = baseW;
            // 气泡完全占在猫左侧的额外空间里，不与身体重叠
            if (BubbleVisible()) w += BubbleWidth + BubbleGap;
            // 宽度只在列表可能出现时才放开，空闲时保持紧凑
            if (listH > 0) w = Math.Max(w, needW);

            // 纵向：上方列表区 + 猫身 + 脚底指示器 + 下方列表区（翻转时用）。
            // 上下都按**最大**高度预留，这样列表从堆叠展开成完整列表时
            // 窗口尺寸不用再变，也不会裁掉卡片。
            int listArea = ListAreaFor(_active.Count);
            int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;

            // 下方预留要能容纳"翻转到猫脚下方"的整摞，见 BelowAreaFor
            int belowArea = BelowAreaFor(listArea, catH, indicatorArea);

            int h = listArea + catH + shadowH + indicatorArea + belowArea;

            // 关键：窗口尺寸变化时，保持**猫的屏幕位置**完全不变。
            //
            // 横向：猫在窗口内居中，窗口变宽 -> Left 左移一半宽度差。
            // 纵向：猫在窗口内位于 listArea 处，窗口变高（listArea 变大）
            //       会把猫往下推 -> 必须把 Top 同步上移相同的量。
            // 之前只处理了横向，纵向没管，所以点圆点弹出列表时
            // 猫会"突然换了一大截位置"（用户反馈的 bug）。
            int oldW = Width;
            int oldH = Height;

            if (oldW > 0 && catW > 0)
            {
                // 用**锚点**（猫的屏幕左上角）反推窗口位置。
                //
                // 注意：即使窗口尺寸没变，也可能因为 listArea 变化
                // （列表开/关切换）导致猫在窗口内的 y 改变。
                // 所以这里不能因为"尺寸相同"就跳过 ——
                // 必须每次都按新 listArea 重新对齐，否则猫会在屏幕上跳 203px。
                Point anchor = EnsureCatAnchor(oldW);

                int newLeft = anchor.X - CatLeftInWindow(w);
                int newTop = anchor.Y - CatYInWindow(listArea, catH, indicatorArea);

                if (newLeft != Left || newTop != Top || oldW != w || oldH != h)
                {
                    // 先按**新尺寸**画好，再一次性提交「位置 + 尺寸 + 内容」。
                    //
                    // 不能只调 SetBounds：它只改窗口矩形，位图还是旧的，
                    // 系统会立刻把旧内容贴到新坐标 —— 用户看到"整只桌宠
                    // 向左闪一下的残影"就是这个（128 宽的旧图贴到 512 窗口）。
                    int w0 = w, h0 = h;
                    UseLayeredResize(newLeft, newTop, w0, h0);
                }
            }
            else
            {
                ClientSize = new Size(w, h);
            }

            // 尺寸变了，窗口内猫的相对位置也变了 —— 但屏幕上猫没动，
            // 所以锚点保持不变，不需要重新记录。

            // 不做任何"猫别出屏"的钳制：
            // 用户要求"除了拖拽，任何操作都不许改变桌宠位置"。
            // 钳制会在窗口变大时把猫推回屏幕内 —— 那就是变相移动。
            // 只有拖动（OnMouseMove）才允许写 Left/Top。
        }

        // ---- 分层窗口的尺寸/位置切换 ----
        //
        // 不用 SetBounds：它只改窗口的位置和尺寸，**位图内容还是旧的**。
        // 系统会立刻把旧内容按新坐标画出来 —— 用户看到"桌宠整体向左闪一下"
        // 的残影就是这个（128 宽的旧图被贴到 512 宽的窗口左上角）。
        //
        // 正确做法是把"新位置 + 新尺寸 + 新内容"用一次 UpdateLayeredWindow
        // 提交，中间不存在"旧内容配新窗口"的状态。
        private Point _layeredPos;
        private Size _layeredSize;
        private bool _layeredDirty;
        private bool _layeredInit;

        private void UseLayeredResize(int left, int top, int w, int h)
        {
            // 关键：整个"改尺寸 + 换内容"的过程必须让系统看不到中间帧。
            //
            // SetBounds 会让窗口矩形立刻变成新的大小/位置，而位图还是旧的
            // （128 宽的旧图），系统就会在这短暂窗口里把旧内容画出来 ——
            // 用户看到"整只桌宠向左闪一下的残影"。
            //
            // 做法：
            //   1) 先按新尺寸把位图画好（此时窗口还是旧尺寸，画在离屏位图里）
            //   2) 把窗口矩形改到位
            //   3) 立刻提交新位图
            // 全程用 WM_SETREDRAW 抑制中间绘制，系统一次都没机会画旧内容。
            int w0 = w, h0 = h;
            RenderInto(w0, h0);                 // 1) 离屏画好

            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                SetBounds(left, top, w0, h0);   // 2) 改窗口矩形
                ApplyLayeredAt(_canvas, left, top, w0, h0);   // 3) 提交新内容
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Invalidate();
            }

            _lastRenderedW = w0;
            _lastRenderedH = h0;
        }

        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>把场景画进 _canvas（不动窗口，不提交）。</summary>
        private void RenderInto(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            if (_canvas == null || _canvas.Width != w || _canvas.Height != h)
            {
                Bitmap old = _canvas;
                _canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                if (old != null) old.Dispose();
            }
            DrawScene(_canvas, w, h);
        }

        private void Render()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            RenderInto(w, h);
            ApplyLayeredAt(_canvas, Left, Top, w, h);
            _lastRenderedW = w;
            _lastRenderedH = h;
        }

        // 脚底下方指示器占位：三种形态都用同一个槽位高度，切换时不跳
        // 脚底下方指示器占位：三种形态都用同一个槽位高度，切换时不跳。
        // 要放得下 IndicatorDiameter() 的实际值（约 26px）+ 呼吸，
        // 否则圆点会被窗口底边裁掉。
        private const int IndicatorSlotH = 32;

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
            // 收回动画进行中也要保留高度，否则卡片会被窗口边缘裁掉
            if (!_listOpen && _reveal <= 0.002f) return 0;
            int count = Math.Min(_active.Count, StackLayout.MaxCards);
            if (count <= 0) return 0;
            if (_stack.Rows != count) _stack.SetRows(count);

            // 纯堆叠（且没在过渡）时用紧凑高度，避免窗口上方留一大块
            // "透明但仍然吃鼠标"的区域，点到会误拖桌宠。
            // 展开中 / 收回中 / 已展开都用展开高度：
            // 这样过渡期间窗口尺寸固定，不必每帧改尺寸，卡片也不会被裁或压到猫。
            bool wantExpanded = _listHeightHold || _stack.SpreadTarget > 0.5f;
            return wantExpanded
                ? StackLayout.ExpandedHeight(count)
                : StackLayout.CollapsedHeight(count);
        }

        /// <summary>绘制位置用：按**当前**展开度算，让卡片是从下往上长出来的。</summary>
        private int AnimatedListHeight()
        {
            // 淡出过程中（_reveal 仍在衰减）也要返回真实高度。
            // 之前写成 if (!_listOpen) return 0，于是关闭的瞬间这个值变 0，
            // listTop = catY + offY - 0 - pad，整摞下移了约一整个摞高，
            // 看起来就是"一直下降到盖住桌宠才消失"（用户反馈的现象）。
            if (!_listOpen && _reveal <= 0.002f) return 0;
            int count = Math.Min(_active.Count, StackLayout.MaxCards);
            if (count <= 0) return 0;
            if (_stack.Rows != count) _stack.SetRows(count);
            return _stack.CurrentHeight();
        }

        // 卡片 × 半浮在左上角外，顶部需要留的余量；底部与角标留的呼吸
        private const int ListTopPad = 7;
        private const int ListBottomGap = 4;

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

        /// <summary>
        /// 自检：量化"细线 -> 圆点"的形态弹簧。
        /// 用户要求"动作慢一点、显得富有弹性"，这里给出到位时间与回弹峰值。
        /// </summary>
        public string RunMorphProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("刚度=").Append(MorphStiffness)
              .Append(" 阻尼=").Append(MorphDamping).Append('\n');

            // 0 -> 1
            _morph = 0f;
            _morphVel = 0f;
            double ms = 0, to95 = -1;
            float peak = 0f;
            const double dtMs = 16.0;
            for (int i = 0; i < 400; i++)
            {
                float dt = (float)(dtMs / 1000.0);
                float acc = -MorphStiffness * (_morph - 1f) - MorphDamping * _morphVel;
                _morphVel += acc * dt;
                _morph += _morphVel * dt;
                ms += dtMs;
                if (_morph > peak) peak = _morph;
                if (to95 < 0 && _morph >= 0.95f) to95 = ms;
                if (Math.Abs(_morph - 1f) < 0.002f && Math.Abs(_morphVel) < 0.02f) break;
            }
            sb.Append("细线->圆点 到95%=").Append(to95 < 0 ? "n/a" : to95.ToString("0") + "ms")
              .Append("  峰值=").Append(peak.ToString("0.000"))
              .Append("  过冲=").Append(((peak - 1f) * 100).ToString("0.0")).Append("%\n");

            // 1 -> 0（回去也要有过程）
            _morph = 1f;
            _morphVel = 0f;
            ms = 0;
            to95 = -1;
            float low = 1f;
            for (int i = 0; i < 400; i++)
            {
                float dt = (float)(dtMs / 1000.0);
                float acc = -MorphStiffness * (_morph - 0f) - MorphDamping * _morphVel;
                _morphVel += acc * dt;
                _morph += _morphVel * dt;
                ms += dtMs;
                if (_morph < low) low = _morph;
                if (to95 < 0 && _morph <= 0.05f) to95 = ms;
                if (Math.Abs(_morph) < 0.002f && Math.Abs(_morphVel) < 0.02f) break;
            }
            sb.Append("圆点->细线 到95%=").Append(to95 < 0 ? "n/a" : to95.ToString("0") + "ms")
              .Append("  最低=").Append(low.ToString("0.000"))
              .Append("  回弹=").Append(((0f - low) * 100).ToString("0.0")).Append("%");

            _morph = 0f;
            _morphVel = 0f;
            return sb.ToString();
        }

        /// <summary>
        /// 自检：把一串"鼠标到猫身的距离"喂进去，输出状态变化序列。
        /// 用于证明迟滞生效（进入/退出阈值不同、边界抖动不闪烁）。
        /// </summary>
        public string RunProximityProbe()
        {
            int enter = NearEnterPx;
            int exit = NearExitPx;
            // 按实际阈值构造：远 -> 刚好进不去 -> 进入 -> 迟滞区间几档 -> 刚好退不出 -> 退出
            int[] distances = {
                enter + 120,          // 很远
                enter + 5,            // 略超进入阈值，不该变
                enter - 2,            // 进入圆点
                (enter + exit) / 2,   // 迟滞区间中部，不该变
                exit - 3,             // 还差一点到退出阈值，不该变
                exit + 3,             // 越过退出阈值，退回细线
                enter - 2,            // 再次进入
            };

            var sb = new System.Text.StringBuilder();
            sb.Append("桌宠尺寸=").Append(PetSize)
              .Append("px  进入阈值=").Append(enter)
              .Append("px  退出阈值=").Append(exit).Append("px\n");

            _near = false;
            sb.Append("起始: 细线\n");

            int changes = 0;
            for (int i = 0; i < distances.Length; i++)
            {
                int d = distances[i];
                bool was = _near;
                if (!_near && d <= enter) _near = true;
                else if (_near && d >= exit) _near = false;

                sb.Append("  ").Append(d.ToString().PadLeft(4)).Append("px  ")
                  .Append(_near ? "圆点" : "细线");
                if (_near != was)
                {
                    changes++;
                    sb.Append("   <-- 切换");
                }
                sb.Append('\n');
            }

            // 期望恰好 3 次切换：进入 / 退出 / 再进入
            sb.Append("切换次数=").Append(changes)
              .Append(changes == 3 ? "（正确：迟滞区间内没有来回闪）" : "（异常）");
            return sb.ToString();
        }

        /// <summary>
        /// 推进会话列表的显现弹簧。
        /// 与列表展开、形态过渡共用同一组参数，三处动作速度一致。
        /// 允许轻微过冲（>1），卡片会稍稍多滑出一点再落定。
        /// </summary>
        private void StepReveal(float dt)
        {
            float target = _listOpen ? 1f : 0f;

            float acc = -RevealStiffness * (_reveal - target) - RevealDamping * _revealVel;
            _revealVel += acc * dt;
            _reveal += _revealVel * dt;

            // 收回时把负值夹掉：欠阻尼弹簧会冲过 0 再弹回来，
            // 那会让"需要列表高度"的判断反复翻转 —— 实测窗口 269→164→269→164
            // 闪了三次。收回方向不需要回弹，直接停在 0。
            if (_reveal < 0f)
            {
                _reveal = 0f;
                _revealVel = 0f;
            }

            if (Math.Abs(_reveal - target) < 0.002f && Math.Abs(_revealVel) < 0.02f)
            {
                _reveal = target;
                _revealVel = 0f;
            }
        }

        /// <summary>
        /// 每帧读一次全局鼠标位置，维护"接近 / 远离"状态。
        ///
        /// 不能用窗口的 MouseEnter/MouseLeave：那要求鼠标真的进到窗口里，
        /// 而窗口只有猫那么大，用户会觉得"贴到脸上了才反应"。
        /// 这里直接读屏幕坐标算距离，并且进入/退出用不同阈值做迟滞。
        /// </summary>
        private void UpdateProximity()
        {
            if (_lastCatRect.Width <= 0)
            {
                return;   // 还没有首帧，先不判
            }

            // 猫在屏幕上的位置 = 窗口位置 + 猫在窗口内的位置
            Rectangle catScreen = _lastCatRect;
            catScreen.Offset(Left, Top);

            Point p = Cursor.Position;
            double dist = DistanceToRect(p, catScreen);

            if (!_near && dist <= NearEnterPx)
            {
                _near = true;
            }
            else if (_near && dist >= NearExitPx)
            {
                _near = false;
            }
        }

        /// <summary>点到矩形的最短距离；点在矩形内返回 0。</summary>
        private static double DistanceToRect(Point p, Rectangle r)
        {
            int dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
            int dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
            return Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        /// <summary>
        /// 推进"细线 / 圆点"的形态弹簧。
        /// 不做上下限夹取：允许越过 1 一点点，那点过冲就是视觉上的弹性来源；
        /// 绘制时再把宽度等几何值夹回合法范围。
        /// </summary>
        private void StepMorph(float dt)
        {
            bool wantDot = !_listOpen && (_hover || _near || ForceHover);
            float target = wantDot ? 1f : 0f;

            float acc = -MorphStiffness * (_morph - target) - MorphDamping * _morphVel;
            _morphVel += acc * dt;
            _morph += _morphVel * dt;

            // 收敛到极接近时定格，避免永远在抖
            if (Math.Abs(_morph - target) < 0.002f && Math.Abs(_morphVel) < 0.02f)
            {
                _morph = target;
                _morphVel = 0f;
            }
        }

        // ---------------- Animation ----------------

        private DateTime _lastTick = DateTime.UtcNow;

        private void Tick()
        {
            bool sprinting = (DemoRun || (_activeCount > 0 && _hover && !_listOpen));

            // 用真实时间步长：定时器并不保证 33ms，若实际更慢还按 0.033 推进，
            // 弹簧会显得迟钝。封顶 100ms，避免卡顿后一帧跳太远。
            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (dt <= 0f || dt > 0.1f) dt = 0.033f;

            _phase += sprinting ? 0.34f : 0.075f;
            if (_phase > 6.2831853f) _phase -= 6.2831853f;
            if (sprinting) _runFrame++;

            UpdateProximity();
            StepMorph(dt);
            StepReveal(dt);
            StepListOffset(dt);
            StepCloseFade(dt);

            // 列表需要占窗口高度时，只在"需要 / 不需要"翻转的那一帧改尺寸，
            // 动画过程中窗口保持不动，避免每帧重建窗口表面导致掉帧。
            bool needSpace = _listOpen || _reveal > 0.002f;
            if (needSpace != _listSpaceAllocated)
            {
                _listSpaceAllocated = needSpace;
                Reflow();
            }

            if (_bubbleLeft > 0) _bubbleLeft--;
            if (_badgePulse > 0) _badgePulse--;

            // 卡片堆叠的弹簧动画。
            // 注意：不再每帧 Reflow —— 窗口高度按目标值算，动画开始时已定好，
            // 卡片在这个固定窗口里插值即可。每帧改 ClientSize 会重建窗口表面，
            // 正是之前"明显卡顿掉帧"和"穿模"的来源。
            _stack.Step(dt);

            // 展开/收回的弹簧停下来后解除高度保持，窗口缩回紧凑尺寸。
            // 这样每次过渡只在"开始"和"结束"各改一次尺寸，中途不变。
            if (_listHeightHold && Math.Abs(_stack.Spread - _stack.SpreadTarget) < 0.02f)
            {
                _listHeightHold = false;
                Reflow();
            }

            // 气泡出现/消失会改变需要的窗口宽度。
            // 用 _bubbleLaidOut 比较，而不是"本帧前后是否可见"：
            // Say() 在 Tick 之外调用，前后对比永远相等，宽度就永远不长。
            bool bubbleNow = BubbleVisible();
            if (bubbleNow != _bubbleLaidOut)
            {
                _bubbleLaidOut = bubbleNow;
                Reflow();
            }

            Render();
        }

        /// <summary>
        /// 窗口尺寸变化时重新布局。
        ///
        /// 关键是保持**猫的屏幕坐标**不变：猫贴窗口右侧，所以窗口变宽时
        /// Left 要同步左移相同像素，猫在屏幕上才不会"往右挪"。
        /// </summary>
        private void Reflow()
        {
            // ApplyLayout 内部已经负责"保持猫屏幕位置不变"地调整窗口。
            // 这里不要再自己算一遍 Left/Top，否则两套公式打架，
            // 猫会被推来推去（用户看到的"点圆点后桌宠突然换位置"）。
            ApplyLayout();

            // 拖动期间不要重新定位窗口。
            // 拖动逻辑正按鼠标位置算 Left/Top，这里再改一次就是第二个写者，
            // 两方一帧内互相覆盖 -> 系统把两次移动都画出来 -> 晃影。
            if (_dragging)
            {
                _bubbleLaidOut = BubbleVisible();
                return;
            }

            // 只做"猫别出屏"的兜底，不动猫的相对位置
            ClampCatToScreen();

            // 布局完成，记录这次是按"气泡可见/不可见"哪种状态算的宽度
            _bubbleLaidOut = BubbleVisible();
        }

        private int _lastRenderedW;
        private int _lastRenderedH;

        private Bitmap _canvas;   // 复用画布，避免每帧分配新 Bitmap

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
                int catX = CatLeftInWindow(w);

                // 猫落在"上方列表区"之下，与 ApplyLayout 的 h 公式对应：
                //   h = listArea*2 + catH + indicatorArea
                // 所以 catY = listArea 时，猫 + 指示器正好填满下方，居中对称。
                int listArea = ListAreaFor(_active.Count);
                int catY = CatYInWindow(listArea, catH,
                                        _active.Count > 0 ? IndicatorSlotH : 0);
                // CatYInWindow 与 ApplyLayout 用的是同一个公式，
                // 保证"布局算出的猫位置"和"实际画出的猫位置"永远一致。
                // 之前这里直接写 catY = listArea，列表关闭时 listArea=0，
                // 猫被画到窗口顶部，屏幕上就"跳了 203px"（用户反馈的 bug）。
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

                // 1. 地面阴影：用户明确要求去掉这块灰色，只保留脚底下那条小横线。
                //    不再绘制 _shadow，也不再为它预留高度。

                _lastShadowRect = new Rectangle(catX + catW / 2, footY, 0, 0);
                _lastCatRect = new Rectangle(catX, catY, catW, catH);
                // 同时记下屏幕坐标：布局变化时要靠它把猫钉在原位
                _lastCatScreenRect = new Rectangle(catX + Left, catY + Top, catW, catH);

                // 2. feet first, so the torso can cover the ankle cut.
                // lift/dash are given in SOURCE pixels and scaled here, so the
                // motion reads the same at every pet size.
                float sLift = scale;
                DrawLeg(canvas, _legLeft, catX, catY, catW, catH, scale,
                    ANKLE_L_X, -dash * 1.6f * sLift, -liftL * sLift, ankL);
                DrawLeg(canvas, _legRight, catX, catY, catW, catH, scale,
                    ANKLE_R_X, dash * 1.6f * sLift, -liftR * sLift, ankR);

                // 3. torso last
                // 旋转支点必须锚在**猫自己的脚底中心**，不能用窗口坐标。
                // 之前写成 (w/2, h*0.96)：窗口很矮时恰好接近脚底，
                // 但窗口为了装任务框变高到 570 后，支点跑到 y=547，
                // 离猫身很远，几度的倾斜就把整只猫甩出画面 ——
                // 用户看到的"鼠标一动模型就毫无章法"就是这个。
                float pivotX = catX + catW / 2f;
                float pivotY = catY + catH * 0.96f;
                DrawRotated(canvas, _torso,
                    new Rectangle(catX + (int)(dash * 1.2f * scale), catY - (int)(bodyBob * scale), catW, catH),
                    sway + _tiltX + lean, pivotX, pivotY);

                // 4. overlays
                // 会话列表以**猫头顶中心**为基准摆放：
                //   横向 = 猫中心 + _listOffX（贴边时左右让位）
                //   纵向 = 猫头顶 + _listOffY（正常在头顶上方，贴顶时翻到下方）
                int stackH = AnimatedListHeight();
                int listTop = 0;
                int listCx = catX + catW / 2;   // 卡片摞中心，默认与猫中心对齐

                // 判断"要不要摆到目标位置"必须用**列表是否在显示**，
                // 不能用 stackH > 0：刚点开的那一帧 _stack.Spread 还是 0，
                // AnimatedListHeight() 返回 0，于是这一帧卡片被画在
                // 默认值（窗口左上角附近），下一帧才跳回头顶 ——
                // 这就是用户看到的"先出现在很左侧再飞过来"。
                if (_listOpen || _reveal > 0.002f)
                {
                    // _listOffY 表示"摞底边相对猫头顶"（负值 = 在头顶上方）。
                    // 底边固定在猫头顶附近不动，顶部由当前摞高反推 ——
                    // 展开时摞变高、整摞向上生长，收回时向下收拢，
                    // 底边始终贴着猫头顶，这才是自然的动效。
                    int stackBottomY = catY + (int)Math.Round(_listOffY);
                    listTop = stackBottomY - stackH - ListTopPad;
                    listCx = catX + catW / 2 + (int)Math.Round(_listOffX);
                }
                // reveal 0 -> 1 时位移从 +RevealSlidePx 收敛到 0
                int slide = (int)Math.Round((1f - _reveal) * RevealSlidePx);
                listTop += slide;

                // 任务指示器紧贴**真实脚底**。
                // 注意要用 footY(439/460)，不能用 catY + catH(460)：
                // 素材底部还有 21/460 的空白，用整图底边会把线推到 14px 之外，
                // 用户看到的"离得太远"就是这个。
                DrawIndicator(canvas, w, catX, catW, footY);

                if (BubbleVisible()) DrawBubble(canvas, w, catY, catX, catW);
                // 收回动画期间 _listOpen 已经是 false，但还要继续画到 reveal 归零为止。
                //
                // 先把列表画到一块**紧贴卡片大小**的独立图层里，再按绝对坐标
                // 合成到主画布。这样列表可以在窗口范围内任意位置摆放，
                // 不会被窗口边缘裁掉，也不用为它预留占满整窗的缓冲。
                if (_listOpen || _reveal > 0.002f)
                {
                    DrawListLayer(canvas, listTop, listCx);
                }
            }
        }

        /// <summary>
        /// 脚底下方的任务指示器，三态：
        ///   细线    —— 有任务在跑、鼠标不在身上（默认）
        ///   数字圆点 —— 有任务在跑、鼠标在身上（悬停时）
        ///   收起箭头 —— 列表已打开（点过圆点之后）
        /// </summary>
        private void DrawIndicator(Bitmap canvas, int w, int catX, int catW, int footBottom)
        {
            // 判"有没有卡片要展示"，不是"有没有在跑的"。
            // 已完成的卡片会一直保留（用户要求不自动消失），
            // 所以只要列表里还有卡片，脚底指示器就得在，否则用户点不进列表。
            if (_active.Count <= 0)
            {
                _badgeRect = Rectangle.Empty;
                _indicatorKind = IndicatorKind.None;
                return;
            }

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = catX + catW / 2;
                // 紧贴脚底：只留 2~3px 缝隙，不再在槽位里居中（那会推远）
                int bandTop = footBottom + 3;

                if (_listOpen)
                {
                    // ---- 收起箭头：一个白色小圆，里面一个向上的折角 ----
                    // 直径与卡片右侧动作按钮一致（用户要求对齐）
                    _indicatorKind = IndicatorKind.Chevron;
                    int d = IndicatorDiameter();
                    _badgeRect = new Rectangle(cx - d / 2, bandTop + 2, d, d);

                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(252, 251, 247)))
                    using (Pen border = new Pen(Color.FromArgb(216, 202, 182), 1f))
                    {
                        g.FillEllipse(bg, _badgeRect);
                        g.DrawEllipse(border, _badgeRect);
                    }
                    using (Pen chev = new Pen(Color.FromArgb(120, 104, 86), 1.6f))
                    {
                        chev.StartCap = LineCap.Round;
                        chev.EndCap = LineCap.Round;
                        int mx = _badgeRect.X + _badgeRect.Width / 2;
                        int my = _badgeRect.Y + _badgeRect.Height / 2 + 1;
                        int arm = Math.Max(3, d / 4);
                        g.DrawLine(chev, mx - arm, my + 2, mx, my - 2);
                        g.DrawLine(chev, mx, my - 2, mx + arm, my + 2);
                    }
                    return;
                }

                // ---- 细线 <-> 数字圆点：用弹簧插值做变形过渡 ----
                // 不是瞬间切换形状，而是让"短横杠"慢慢长成"圆"，并带一点回弹。
                // 两者的圆角半径都等于高度的一半，所以只要把宽高一起插值，
                // 形状会自然从药丸过渡成圆，不需要额外画两套。
                int lineW = (int)Math.Round(catW * 0.34f);
                if (lineW < 26) lineW = 26;
                if (lineW > 44) lineW = 44;
                const int lineH = 5;
                // 圆点直径与卡片动作按钮一致（用户要求对齐）
                int dotD = IndicatorDiameter();

                float baseT = Clamp01(_morph);
                // 允许 >1 的那部分用来体现回弹：形状会先稍稍胀大再落回
                float grow = _morph < 0f ? 0f : _morph;

                int shapeW = (int)Math.Round(lineW + (dotD - lineW) * grow);
                int shapeH = (int)Math.Round(lineH + (dotD - lineH) * grow);
                _badgeRect = new Rectangle(cx - shapeW / 2, bandTop, shapeW, shapeH);

                _indicatorKind = baseT > 0.5f ? IndicatorKind.Dot : IndicatorKind.Line;

                // 颜色也跟着过渡，避免变形到一半颜色突变
                Color lineCol = Color.FromArgb(226, 200, 162);
                Color dotCol = Color.FromArgb(232, 206, 168);
                Color fill = Blend(lineCol, dotCol, baseT);

                using (GraphicsPath path = RoundRect(_badgeRect, shapeH / 2))
                using (SolidBrush bg = new SolidBrush(fill))
                {
                    g.FillPath(bg, path);
                }

                // 数字只在形状大到能装下文字时才浮现。
                // 直接用 baseT 会在 25% 就显示文字：那时形状只有 9px 高，
                // 而字高约 13px，文字会溢出到形状外面，看起来像一团污点。
                // 所以把文字进度往后压：55% 才开始出现，100% 才完全不透明。
                float textT = Clamp01((baseT - 0.55f) / 0.45f);
                if (textT > 0.02f)
                {
                    // 显示"卡片总数"（运行中 + 已完成未关闭），
                    // 因为点它展开的就是这些卡片，数字要对得上。
                    string text = _active.Count.ToString();
                    int textAlpha = (int)Math.Round(255 * textT);
                    if (textAlpha > 255) textAlpha = 255;

                    using (Font font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                    using (SolidBrush fg = new SolidBrush(Color.FromArgb(textAlpha, 92, 68, 44)))
                    {
                        SizeF ts = g.MeasureString(text, font);
                        g.DrawString(text, font, fg,
                            _badgeRect.X + (_badgeRect.Width - ts.Width) / 2f,
                            _badgeRect.Y + (_badgeRect.Height - ts.Height) / 2f + 0.5f);
                    }
                }
            }
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        private static Color Blend(Color a, Color b, float t)
        {
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int b2 = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, b2);
        }

        // ---------------- Helpers ----------------

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
                    // 气泡整个放在猫左侧的预留带里，右边缘与猫留出间距，绝不压到身体
                    int bx = Math.Max(2, catX - BubbleGap - bw);
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

        private enum IndicatorKind { None, Line, Dot, Chevron }
        private IndicatorKind _indicatorKind = IndicatorKind.None;

        /// <summary>
        /// 脚底指示器（小圆点 / 收起箭头）的直径。
        /// 与卡片右侧的回复/停止按钮保持一致，两者视觉上才是一套。
        /// </summary>
        private int IndicatorDiameter()
        {
            int d = _actionBtnD > 0 ? _actionBtnD : BtnSize;
            if (d < BtnMinD) d = BtnMinD;
            if (d > BtnMaxD) d = BtnMaxD;
            return d;
        }

        // ---------------- 列表图层（用于整层淡入淡出） ----------------

        private Bitmap _listLayer;

        /// <summary>
        /// 画会话列表并整体做透明度合成。
        /// 列表画在与窗口同尺寸的图层上（坐标直接用窗口坐标），
        /// 这样淡入淡出可以一次性作用于圆角、文字、投影。
        /// </summary>
        private void DrawListLayer(Bitmap canvas, int listTop, int listCx)
        {
            int w = canvas.Width;
            int h = canvas.Height;
            EnsureListLayer(w, h);

            using (Graphics lg = Graphics.FromImage(_listLayer))
            {
                lg.Clear(Color.Transparent);
                lg.SmoothingMode = SmoothingMode.AntiAlias;
                lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            DrawList(_listLayer, w, listTop, listCx);
            CompositeLayer(canvas, _listLayer, _reveal);
        }

        private void EnsureListLayer(int w, int h)
        {
            if (_listLayer == null || _listLayer.Width != w || _listLayer.Height != h)
            {
                Bitmap old = _listLayer;
                _listLayer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                if (old != null) old.Dispose();
            }
        }

        /// <summary>
        /// 把列表图层按 alpha 合成到主画布。
        /// GDI+ 画形状没有全局透明度，只能用 ColorMatrix 的 Matrix33 缩放 alpha，
        /// 这样圆角、文字、投影能一次性整体淡掉。
        /// </summary>
        private static void CompositeLayer(Bitmap canvas, Bitmap layer, float alpha)
        {
            float a = alpha < 0f ? 0f : (alpha > 1f ? 1f : alpha);
            if (a <= 0.004f) return;

            using (Graphics g = Graphics.FromImage(canvas))
            using (ImageAttributes ia = new ImageAttributes())
            {
                var cm = new ColorMatrix();
                cm.Matrix33 = a;
                ia.SetColorMatrix(cm);
                g.DrawImage(layer,
                    new Rectangle(0, 0, layer.Width, layer.Height),
                    0, 0, layer.Width, layer.Height,
                    GraphicsUnit.Pixel, ia);
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
        /// <summary>
        /// 卡片上的圆形小按钮（回复 / 停止 / 关闭）。
        ///
        /// 视觉参照：淡灰圆底 + **无描边** + 小巧的深灰线条图标。
        /// 之前底色偏暖、描边偏重（棕色 1px 完整圆），图标又按
        /// "直径 - 9" 算，几乎撑满整个圆 —— 看起来又笨又重。
        /// 现在图标固定占直径的 44%，四周留足空隙。
        /// </summary>
        /// <summary>
        /// 绘制卡片的 × 关闭按钮 —— **裸图标，没有实心圆底**。
        ///
        /// 对应 CSS 里的那条规则：去掉外围实心圆，只保留干净的 x。
        /// 透明度由 fade 控制（0=完全隐藏，1=显示），用来做悬停淡入淡出；
        /// 命中判定仍走 _btnClose 的矩形，即使透明也能点到，
        /// 但矩形只在淡入到一定程度后才参与命中（见 UpdateHoverButton）。
        /// </summary>
        private static void DrawCloseBare(Graphics g, Rectangle r, float fade, bool hover)
        {
            if (fade <= 0.02f) return;

            SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int a = (int)Math.Round(255 * Clamp01(fade));

            // 极细的圆圈边框（不是实心底），悬停时稍明显
            using (Pen ring = new Pen(Color.FromArgb(
                (int)(a * (hover ? 0.85f : 0.55f)), 186, 168, 146), 1f))
            {
                g.DrawEllipse(ring, r);
            }

            // 干净的 x
            using (Pen fg = new Pen(Color.FromArgb(
                (int)(a * (hover ? 1f : 0.8f)), 120, 102, 82), 1.4f))
            {
                fg.StartCap = LineCap.Round;
                fg.EndCap = LineCap.Round;
                int pad = Math.Max(3, r.Width / 4);
                g.DrawLine(fg, r.X + pad, r.Y + pad, r.Right - pad, r.Bottom - pad);
                g.DrawLine(fg, r.Right - pad, r.Y + pad, r.X + pad, r.Bottom - pad);
            }

            g.SmoothingMode = prev;
        }

        private static void DrawIconButton(Graphics g, Rectangle r, IconKind kind, bool enabled,
            bool hover)
        {
            SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int alpha = enabled ? 255 : 110;

            // 圆底：悬停时明显加深，给出"可以点"的即时反馈。
            // 用桌宠自己的奶油/暖棕系（不照搬参考图的灰色），
            // 但保留参考图的"浅底 + 极淡描边"轻重关系。
            Color bgNormal = Color.FromArgb(enabled ? 250 : 236, 243, 236, 224);
            Color bgHover = Color.FromArgb(enabled ? 255 : 236, 232, 220, 200);
            using (SolidBrush bg = new SolidBrush(hover && enabled ? bgHover : bgNormal))
            {
                g.FillEllipse(bg, r);
            }
            using (Pen border = new Pen(Color.FromArgb(
                hover && enabled ? 150 : (enabled ? 70 : 40), 214, 194, 168), 1f))
            {
                g.DrawEllipse(border, r);
            }

            // 图标尺寸：直径的 44%，居中
            int icon = Math.Max(8, (int)Math.Round(r.Width * 0.44f));
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;

            // 图标用深暖棕；悬停时再深一档
            Color ink = Color.FromArgb(alpha,
                hover && enabled ? 96 : 118,
                hover && enabled ? 74 : 96,
                hover && enabled ? 52 : 72);

            switch (kind)
            {
                case IconKind.Stop:
                    {
                        // 停止保留暖红：比纯灰更贴合桌宠配色，也更像"停止"。
                        // 但尺寸收到和线条图标一致，不再是撑满圆的大方块。
                        using (SolidBrush fg = new SolidBrush(
                            Color.FromArgb(alpha, 196, 106, 88)))
                        {
                            g.FillRectangle(fg, new Rectangle(cx - icon / 2, cy - icon / 2, icon, icon));
                        }
                        break;
                    }
                case IconKind.Close:
                    {
                        using (Pen fg = new Pen(ink, 1.3f))
                        {
                            int h = icon / 2;
                            g.DrawLine(fg, cx - h, cy - h, cx + h, cy + h);
                            g.DrawLine(fg, cx + h, cy - h, cx - h, cy + h);
                        }
                        break;
                    }
                default:
                    {
                        // 折返箭头 = 回复：一横 + 左箭头 + 右端上折
                        using (Pen fg = new Pen(ink, 1.3f))
                        {
                            int w2 = icon / 2;
                            int y = cy + 1;
                            g.DrawLine(fg, cx + w2, y, cx - w2, y);
                            g.DrawLine(fg, cx - w2, y, cx - w2 + 4, y - 4);
                            g.DrawLine(fg, cx - w2, y, cx - w2 + 4, y + 4);
                            g.DrawLine(fg, cx + w2, y, cx + w2, y - icon / 2);
                        }
                        break;
                    }
            }

            g.SmoothingMode = prev;
        }

        // ---------------- Session list ----------------

        private void DrawList(Bitmap canvas, int w, int listTop, int centerX)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int count = Math.Min(_active.Count, StackLayout.MaxCards);
                if (count == 0)
                {
                    _rowRects = new Rectangle[0];
                    _btnClose = new Rectangle[0];
                    _btnReply = new Rectangle[0];
                    _btnStop = new Rectangle[0];
                    return;
                }

                _rowRects = new Rectangle[count];
                _btnClose = new Rectangle[count];
                _btnReply = new Rectangle[count];
                _btnStop = new Rectangle[count];

                int cardW = StackLayout.CardW;
                int cardH = StackLayout.CardH;

                // 卡片摞以 centerX 为中心横向对称摆放。
                // **不要**再把它夹回窗口内：
                // 之前这里做了 lx = clamp(lx, 2, w-cardW-2)，等于把横向让位
                // 限制在窗口宽度里，而窗口只比猫宽一点，任务框根本挪不动，
                // 结果贴边时直接把卡片画到猫身上（用户截图的"压住桌宠"）。
                //
                // 卡片能不能完整显示由 UpdateListTarget 保证（它按屏幕工作区
                // 算偏移），窗口这里只负责画。所以窗口为列表留足了宽度，
                // 就不需要再夹。
                int lx = centerX - cardW / 2;

                // listTop 是整块列表的顶边；卡片从这里往下让 7px，
                // 给半浮在卡片外的 × 留位置
                int baseY = Math.Max(0, listTop) + ListTopPad;

                if (_stack.Rows != count) _stack.SetRows(count);
                // 上报卡片摞本身（不含 × 余量与底部呼吸），自检据此判真实间距
                _lastListRect = new Rectangle(lx, baseY, cardW, _stack.CurrentHeight());

                // 整摞外接矩形：从第一张顶边到最后一张底边，覆盖所有露出的边缘
                int lastIdx = count - 1;
                int stackBottom = baseY + (int)Math.Round(_stack.YOf(lastIdx)) + cardH;
                _lastStackRect = new Rectangle(lx, baseY, cardW, stackBottom - baseY);

                // 标题用粗一点的 9.5，状态行用 8；两行层次分明
                using (Font font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (Font timeFont = new Font("Microsoft YaHei UI", 8f))
                using (Font metaFont = new Font("Microsoft YaHei UI", 7.5f))
                {
                    // 从最底下那张往上画，这样上面的卡片自然压住下面的
                    float spread = _stack.Spread;
                    for (int i = count - 1; i >= 0; i--)
                    {
                        SessionItem item = _active[i];
                        int cy = baseY + (int)Math.Round(_stack.YOf(i));

                        // 堆叠时，越靠后的卡片横向收窄一点，形成"一摞纸"的层次；
                        // 展开到一半以上就恢复原宽度。
                        int inset = 0;
                        if (spread < 0.55f && i > 0)
                        {
                            float k = (1f - spread / 0.55f);
                            inset = (int)Math.Round(i * 5f * k);
                        }

                        var card = new Rectangle(lx + inset, cy, cardW - inset * 2, cardH);
                        _rowRects[i] = card;

                        // 收起时，被压住的卡片做淡处理
                        bool covered = (i > 0) && (spread < 0.55f);
                        DrawStackCard(g, card, item, covered, font, timeFont, metaFont, i);
                    }
                }
            }
        }

        /// <summary>
        /// 画一张卡片。仿用户给的通知样式：两行（标题 + 状态），圆角、柔和投影。
        ///
        /// 配色按桌宠自己的奶油色调来，不用附件里的纯白：
        ///   底  RGB(253,251,246) 左右的奶油白
        ///   边  RGB(226,208,184) 淡棕
        ///   文  深褐 52,42,32
        /// </summary>
        private void DrawStackCard(Graphics g, Rectangle card, SessionItem item,
            bool covered, Font font, Font timeFont, Font metaFont, int index)
        {
            // 卡片底必须完全**不透明**。
            // 之前被压住的卡用 alpha 208，下层卡片会透上来 —— 这就是用户
            // 看到的"穿模"。堆叠层次靠投影和边框体现，不靠透明度。
            const int alpha = 255;

            // 投影：越靠下的卡片越淡，形成一摞的层次
            using (GraphicsPath sp = RoundRect(
                new Rectangle(card.X, card.Y + 2, card.Width, card.Height), 13))
            using (SolidBrush shadow = new SolidBrush(
                Color.FromArgb(covered ? 16 : 30, 122, 102, 82)))
            {
                g.FillPath(shadow, sp);
            }

            using (GraphicsPath path = RoundRect(card, 13))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(alpha, 253, 251, 246)))
            using (Pen border = new Pen(Color.FromArgb(covered ? 150 : 210, 228, 211, 188), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            // ---- 布局：标题完整 + 内容最多两行 ----
            //
            // 用户要求：
            //   - 标题**完整展示**，绝不允许省略号
            //   - 标题后跟一个 " · " 分隔符，粗细/颜色介于标题与正文之间
            //   - 内容可以从第一行续到第二行
            //   - 去掉"正在响应"状态行
            string body = item.Body ?? string.Empty;

            var titleFont = font;     // 9.5 Bold
            var bodyFont = timeFont;  // 8 Regular

            float titleH = g.MeasureString(item.Title, titleFont).Height;
            float bodyH = g.MeasureString("测", bodyFont).Height;
            const float lineGap = 2f;

            // 两行文字的总高：第一行（标题行） + 第二行（内容续行）
            float contentH = titleH + lineGap + bodyH;

            // 圆形按钮直径 = 内容高度，再按卡片高度收一下
            int btnD = (int)Math.Round(contentH);
            if (btnD < BtnMinD) btnD = BtnMinD;
            if (btnD > BtnMaxD) btnD = BtnMaxD;
            int capD = (int)(card.Height * 0.62f);
            if (btnD > capD) btnD = capD;
            _actionBtnD = btnD;   // 供指示器对齐用

            // ---- 左上角 × ----
            // 必须完整落在**卡片矩形内**才点得到：窗口的鼠标命中基于
            // ClientRectangle，超出窗口（或超出卡片可视区）的点会被系统丢弃。
            // 之前放在 card.X-2, card.Y-6（卡片外面），所以点了没反应。
            // 现在贴卡片左上角内侧，视觉上仍压着圆角。
            const int closeD = 15;
            _btnClose[index] = new Rectangle(card.X + 2, card.Y + 2, closeD, closeD);
            float closeFade = (index < _closeFade.Length) ? _closeFade[index] : 0f;
            DrawCloseBare(g, _btnClose[index], closeFade,
                _hoverCardRow == index && !_hoverAnyBtn);

            // ---- 右侧动作按钮 ----
            // 运行中：回复 + 停止；已完成：只保留回复。
            int btnY = card.Y + (card.Height - btnD) / 2;
            int stopX = card.Right - 12 - btnD;
            int replyX = stopX - 8 - btnD;
            if (item.Active)
            {
                _btnStop[index] = new Rectangle(stopX, btnY, btnD, btnD);
                _btnReply[index] = new Rectangle(replyX, btnY, btnD, btnD);
                DrawIconButton(g, _btnStop[index], IconKind.Stop, true,
                    IsHovered(index, IconKind.Stop));
            }
            else
            {
                _btnStop[index] = Rectangle.Empty;
                _btnReply[index] = new Rectangle(stopX, btnY, btnD, btnD);
            }
            DrawIconButton(g, _btnReply[index], IconKind.Reply, true,
                IsHovered(index, IconKind.Reply));

            // ---- 文字区域 ----
            float contentTop = card.Y + (card.Height - contentH) / 2f;
            if (contentTop < card.Y + 3f) contentTop = card.Y + 3f;

            // × 现在压在卡片正左上角外侧（card.X-2 .. card.X+13），
            // 不再侵入内容区，所以左边距可以贴边。
            // 只有已完成的卡需要多留 18px 给绿勾（勾在 card.X+6）。
            // × 占 card.X+2 .. card.X+17，文字必须让开它（不悬停时 × 是透明的，
            // 但空间照留，避免悬停时文字跳动）。
            // 已完成还要再让开 14px 的绿勾。
            int textLeft = card.X + (item.Active ? 20 : 32);
            int textRight = _btnReply[index].X - 8;

            // 绿色圆底 + 白色对勾（仅已完成），放在 × 右侧
            if (!item.Active)
            {
                var mark = new Rectangle(card.X + 19,
                    (int)Math.Round(contentTop + titleH / 2f) - 7, 14, 14);
                using (SolidBrush bg2 = new SolidBrush(Color.FromArgb(alpha, 76, 176, 116)))
                {
                    g.FillEllipse(bg2, mark);
                }
                using (Pen tick = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.4f))
                {
                    tick.StartCap = LineCap.Round;
                    tick.EndCap = LineCap.Round;
                    int cx = mark.X + mark.Width / 2;
                    int cy = mark.Y + mark.Height / 2;
                    g.DrawLine(tick, cx - 4, cy, cx - 1, cy + 3);
                    g.DrawLine(tick, cx - 1, cy + 3, cx + 4, cy - 3);
                }
            }

            const string Sep = " · ";
            // 与绘制时同一个字号（9pt Bold），否则宽度算不准，
            // 内容起始位置会和分隔符错开。
            float sepW;
            using (Font sepFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            {
                sepW = g.MeasureString(Sep, sepFont).Width;
            }

            // **标题完整展示**：不截断。若标题本身就超过整行宽度
            // （极少见），宁可让内容从第二行开始也不切标题。
            float titleW = g.MeasureString(item.Title, titleFont).Width;

            using (SolidBrush ink = new SolidBrush(Color.FromArgb(alpha, 52, 40, 30)))
            {
                g.DrawString(item.Title, titleFont, ink, textLeft, contentTop);
            }

            // 分隔符：紧跟标题绘制，" " 前后各留半个字宽。
            // 颜色按用户指定 #6A6B6D（(106,107,109)），字号与标题同档并加粗，
            // 明显度介于标题与正文之间。
            bool sepFits = !string.IsNullOrEmpty(body);
            if (sepFits)
            {
                using (SolidBrush sepBr = new SolidBrush(Color.FromArgb(alpha, 106, 107, 109)))
                using (Font sepFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                {
                    g.DrawString(Sep, sepFont, sepBr,
                        textLeft + titleW, contentTop + (titleH - bodyH) / 2f);
                }
            }

            // ---- 内容：从标题右侧开始，排不下就续到第二行 ----
            float firstLineX = textLeft + titleW + (sepFits ? sepW : 0f);
            float firstLineW = textRight - firstLineX;
            float secondLineW = textRight - textLeft;

            if (!string.IsNullOrEmpty(body))
            {
                using (SolidBrush bodyBr = new SolidBrush(Color.FromArgb(alpha, 118, 100, 82)))
                {
                    if (firstLineW > 40)
                    {
                        // 第一段：尽量填满标题右侧
                        string head = TakeFitting(g, body, bodyFont, (int)firstLineW);
                        g.DrawString(head, bodyFont, bodyBr,
                            firstLineX, contentTop + (titleH - bodyH) / 2f + 1f);

                        string rest = body.Substring(head.Length).TrimStart();
                        if (!string.IsNullOrEmpty(rest) && secondLineW > 40)
                        {
                            string second = Ellipsize(g, rest, bodyFont, (int)secondLineW);
                            g.DrawString(second, bodyFont, bodyBr,
                                textLeft, contentTop + titleH + lineGap);
                        }
                    }
                    else
                    {
                        // 标题几乎占满整行：内容整体从第二行开始
                        string second = Ellipsize(g, body, bodyFont, (int)secondLineW);
                        g.DrawString(second, bodyFont, bodyBr,
                            textLeft, contentTop + titleH + lineGap);
                    }
                }
            }
        }

        /// <summary>
        /// 从字符串开头取出**能放进 maxWidth 的最长前缀**，不加省略号。
        /// 用于"第一行填满、剩余部分换行继续"的排版。
        /// </summary>
        private static string TakeFitting(Graphics g, string s, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (g.MeasureString(s, font).Width <= maxWidth) return s;

            int n = s.Length;
            while (n > 0 && g.MeasureString(s.Substring(0, n), font).Width > maxWidth)
            {
                n--;
            }
            return s.Substring(0, n);
        }
        /// <summary>
        /// 推进每张卡片 × 按钮的淡入淡出。
        ///
        /// 等效 CSS 的 `transition: opacity 0.2s ease`：
        ///   - 鼠标在本卡片上 -> 目标 1
        ///   - 否则            -> 目标 0
        /// 用固定速率的线性插值（0.2 秒走完全程），比弹簧更贴近 CSS 的 ease。
        /// </summary>
        private void StepCloseFade(float dt)
        {
            int count = Math.Min(_active.Count, StackLayout.MaxCards);
            if (_closeFade.Length != count)
            {
                // 行数变化时重建数组，已有的进度保留
                var next = new float[count];
                for (int i = 0; i < count && i < _closeFade.Length; i++)
                {
                    next[i] = _closeFade[i];
                }
                _closeFade = next;
            }

            float step = dt / 0.2f;   // 0.2 秒走完 0 -> 1
            for (int i = 0; i < count; i++)
            {
                float target = (_hoverCardRow == i) ? 1f : 0f;
                if (Math.Abs(_closeFade[i] - target) <= step)
                {
                    _closeFade[i] = target;
                }
                else
                {
                    _closeFade[i] += (_closeFade[i] < target) ? step : -step;
                }
            }
        }

        /// <summary>
        /// 当前绘制的按钮是否处于悬停状态。
        /// 由 UpdateHoverButton 每帧（鼠标移动时）算好，绘制时直接读。
        /// </summary>
        private bool IsHovered(int row, IconKind kind)
        {
            return _hoverAnyBtn && _hoverRow == row && _hoverKind == kind;
        }

        /// <summary>
        /// 根据鼠标位置更新"悬停的按钮"。
        /// 按钮矩形每帧都会重算（卡片位置/直径都可能变），
        /// 所以这里直接用这一帧的矩形做命中，和绘制用的是同一份数据。
        /// </summary>
        private void UpdateHoverButton(Point p)
        {
            _hoverAnyBtn = false;
            _hoverRow = -1;
            _hoverCardRow = -1;

            if (!_listOpen && _reveal <= 0.002f) return;
            if (_rowRects == null || _rowRects.Length == 0) return;
            if (_btnReply == null || _btnReply.Length == 0) return;

            for (int i = 0; i < _rowRects.Length; i++)
            {
                if (i >= _btnReply.Length) break;

                // 先判"鼠标是否在本卡片上"—— × 的显示由它决定（等效 :hover）。
                // 用卡片矩形判定，比逐个按钮判定更接近 CSS 的行为。
                if (_rowRects[i].Contains(p))
                {
                    _hoverCardRow = i;
                }

                // × 现在压在卡片正左上角外侧，命中区要算进去
                if (_btnClose.Length > i && _btnClose[i].Contains(p))
                {
                    _hoverAnyBtn = true; _hoverRow = i; _hoverKind = IconKind.Close;
                    _hoverCardRow = i;      // 点 × 时也保持它可见
                    return;
                }
                if (_btnStop.Length > i && _btnStop[i] != Rectangle.Empty &&
                    _btnStop[i].Contains(p))
                {
                    _hoverAnyBtn = true; _hoverRow = i; _hoverKind = IconKind.Stop;
                    return;
                }
                if (_btnReply[i].Contains(p))
                {
                    _hoverAnyBtn = true; _hoverRow = i; _hoverKind = IconKind.Reply;
                    return;
                }
            }
        }

        /// <summary>按像素宽度截断并加省略号。</summary>
        private static string Ellipsize(Graphics g, string s, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (g.MeasureString(s, font).Width <= maxWidth) return s;

            string t = s;
            while (t.Length > 1 && g.MeasureString(t + "…", font).Width > maxWidth)
            {
                t = t.Substring(0, t.Length - 1);
            }
            return t.TrimEnd() + "…";
        }

        // ---------------- Present to the layered window ----------------

        /// <summary>把画布按指定屏幕位置提交到分层窗口。</summary>
        private void ApplyLayeredAt(Bitmap canvas, int left, int top, int w, int h)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = canvas.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                POINT dst = new POINT { X = left, Y = top };
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

                // 先把目标位置算出来、钳制好，**只赋一次** Left/Top。
                //
                // 之前的写法是先写越界值再调 Clamp 拉回来，等于每帧给窗口
                // 两次 SetWindowPos：先飞出屏幕、再弹回边界。虽然最终坐标相同，
                // 系统已经把这两次移动都画出来了 —— 这就是"来回晃的虚影"。
                int wantLeft = _formStart.X + dx;
                int wantTop = _formStart.Y + dy;
                int okLeft, okTop;
                ClampWindowPos(wantLeft, wantTop, out okLeft, out okTop);

                // 位置没变就不要碰窗口。
                // 鼠标在边界外抖动时 wantLeft 一直相同，若每帧都 SetBounds，
                // 系统会反复处理窗口移动消息，合成层就会闪。
                if (okLeft != Left || okTop != Top)
                {
                    SetBounds(okLeft, okTop, Width, Height);
                }

                // 拖动是唯一允许改猫位置的操作：把锚点同步到当前位置，
                // 之后的布局变化都以它为准。
                SetCatAnchorFromWindow();

                // 拖动中不改窗口生长方向：切方向会改变猫在窗口内的锚点
                // (CatLeftInWindow)，位置限制的边界也跟着变，就会抖。
                // 松手时再统一判定并重排一次。

                // 撞边时**不要**重置锚点。
                //
                // 之前每次撞边都把 _formStart/_dragStart 重置到边界和当前鼠标位置，
                // 结果鼠标在边界外每抖一下都会产生一次新的位移 —— 实测 60 次抖动
                // 里窗口被挪了 50 次，就是你看到的晃动。
                //
                // 保留原始锚点则天然有"死区"：越界的距离会累积在 dx/dy 里，
                // 鼠标必须真正回到边界内侧、把攒下的偏移量退完，窗口才开始动。
                // 这就是物理上"顶住墙"的感觉，也不会抖。
                return;
            }

            // 悬停到卡片按钮时给它加深反馈（否则点下去没有视觉变化，
            // 用户会以为程序卡住）。
            // 用这一帧的按钮矩形做命中，和绘制共用同一份数据。
            UpdateHoverButton(e.Location);

            // 倾斜幅度按**猫身范围**归一化，不能用窗口尺寸。
            // 窗口为了装任务框会变得很大（512x570），若用窗口宽高归一化，
            // 鼠标在猫身上移动时 nx/ny 几乎不变，倾斜效果形同消失；
            // 而在空白区域移动却会给出满幅倾斜，看起来就是"乱动"。
            int catW = (int)(PetSize * 0.817f);
            int catH = PetSize;
            int catX0 = CatLeftInWindow(ClientSize.Width);
            int catY0 = CatYInWindow(ListAreaFor(_active.Count), catH,
                                     _active.Count > 0 ? IndicatorSlotH : 0);

            float nx = (e.X - (catX0 + catW / 2f)) / Math.Max(1f, catW / 2f);
            float ny = (e.Y - (catY0 + catH / 2f)) / Math.Max(1f, catH / 2f);
            _tiltX = Clamp(nx * 5f, -6f, 6f);
            _tiltY = Clamp(ny * 4f, -5f, 5f);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _tiltX = 0f;
            _tiltY = 0f;
            // 离开窗口时清掉按钮悬停，避免按钮停在"加深"状态
            _hoverAnyBtn = false;
            _hoverRow = -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (_listOpen)
            {
                // 从最上层（数组尾部是屏幕最下面，最上面是 index 0）往下判，
                // 但绘制是从下往上叠的，所以命中也必须从上往下。
                for (int i = 0; i < _btnReply.Length; i++)
                {
                    SessionItem item = (i < _active.Count) ? _active[i] : null;
                    if (item == null) continue;

                    if (_btnClose[i].Contains(e.Location))
                    {
                        // × 主动移除
                        SessionScanner.Dismiss(item.Id, item.Turn);
                        Poll();
                        // 判"列表里还有没有卡片"，不能用 _activeCount：
                        // 那是运行中的数量，删掉一个已完成的卡片后它可能已经是 0，
                        // 会把整个列表一起关掉（看起来像"点了没反应"）。
                        if (_active.Count == 0) CloseList();
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
                        SessionScanner.MarkSeen(item.Id, item.Turn);
                        if (ReplyRequested != null) ReplyRequested(item);
                        return;
                    }
                }

                // 堆叠状态下，整摞区域任意位置点击都展开。
                // 必须放在卡片命中之前：堆叠时下面那张只露出边缘，
                // 不在任何 _rowRects 里，否则点边缘会没反应（用户就是这样踩到的）。
                if (!_listExpanded && _lastStackRect.Contains(e.Location))
                {
                    _listExpanded = true;
                    _listHeightHold = true;   // 先长到展开高度，动画期间不再改尺寸
                    _stack.SetSpread(true);
                    Reflow();
                    return;
                }

                for (int i = 0; i < _rowRects.Length; i++)
                {
                    if (_rowRects[i].Contains(e.Location))
                    {
                        SessionItem picked = (i < _active.Count) ? _active[i] : null;
                        if (picked == null) continue;

                        // 点开即视为已查看
                        SessionScanner.MarkSeen(picked.Id, picked.Turn);

                        // 用户要求：点已跑完的任务就自动移除；在跑的保留
                        if (!picked.Active)
                        {
                            SessionScanner.Dismiss(picked.Id, picked.Turn);
                            Poll();
                            if (_activeCount == 0) CloseList();
                        }

                        if (SessionRequested != null) SessionRequested(picked);
                        Say("已打开：" + Short2(picked.Title));
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

            // 拖动结束：把锚点记录成鼠标拖到的位置。
            // 之后所有布局变化都以这个锚点为基准，保证猫不再移动。
            if (_movedWhileDown)
            {
                SetCatAnchorFromWindow();
                ApplyLayout();
                ClampCatToScreen();
                SetCatAnchorFromWindow();   // 钳制后可能微调，再记一次
                return;
            }

            Say(_activeCount > 0 ? "跑着呢。" : "在呢。");
            if (OpenDshRequested != null) OpenDshRequested();
        }

        /// <summary>
        /// 角标的点击循环：收起 -> 堆叠 -> 展开 -> 收起。
        ///
        /// 用户要求初始是 iPhone 通知那种"摞在一起"的样子，
        /// 再点一次才真正摊开，而不是一点就全展开。
        /// </summary>
        /// <summary>
        /// 指示器（脚底小横线 / 数字圆点 / 收起箭头）的点击行为。
        ///
        /// 用户要求的分段：
        ///   没打开        -> 打开（初始叠成一摞）
        ///   已展开        -> 先收回成堆叠状态（不是直接消失）
        ///   已堆叠        -> 直接关闭
        ///
        /// 想"展开"就点那摞卡片本身（见 OnMouseDown），不走这里。
        /// </summary>
        public void ToggleList()
        {
            if (!_listOpen)
            {
                Poll();
                // 判"列表里还有没有卡片"，不能用 _activeCount：
                // 那是运行中的数量，已完成但未关闭的卡片仍然要能展开看。
                if (_active.Count == 0)
                {
                    Say("现在没有任务");
                    return;
                }

                _listOpen = true;
                int count = Math.Min(_active.Count, StackLayout.MaxCards);
                _stack.SetRows(count);
                // 初始就是叠好的状态（不展开）
                _stack.SnapSpread(0f);
                _stack.SetSpread(false);
                // 显现弹簧从 0 弹到 1：卡片从下方滑上来
                _reveal = 0f;
                _revealVel = 0f;
                // 立刻给列表分配窗口高度（只改一次尺寸），不留给下一帧
                _listSpaceAllocated = true;
                _listHeightHold = false;   // 初始是堆叠态，用紧凑高度

                // 顺序很关键：**先**把偏移量摆到目标位置，**再** Reflow。
                //
                // Reflow 内部会在尺寸变化时立刻同步 Render（为了消除
                // "窗口变宽但内容还是旧的"那一帧）。如果这时 _listOffX
                // 还是上一轮的残留值，那一帧就会把卡片画在很左边 ——
                // 这就是用户反复看到的"点开后先从左侧闪现"。
                // 之前是先 Reflow 再设偏移，正好踩中这个顺序陷阱。
                UpdateListTarget();
                _listOffX = _listTarX;
                _listOffY = _listTarY;
                _listOffXVel = 0f;
                _listOffYVel = 0f;

                Reflow();
                return;
            }

            if (_listExpanded)
            {
                // 展开 -> 先收回成堆叠，让用户再点一次才关闭。
                // 保持窗口高度直到弹簧停稳，卡片不会因为窗口立刻变矮而压到猫。
                _listExpanded = false;
                _listHeightHold = true;
                _stack.SetSpread(false);
                return;
            }

            // 已是堆叠状态：再点才真正关闭
            CloseList();
        }

        /// <summary>
        /// 自检：模拟"拖到边界后继续往外拖"，检查窗口位置是否稳定。
        ///
        /// 之前的 bug：拖到边界后继续拖，窗口会像虚影一样来回晃。
        /// 原因是让位动画(StepHug -> Reflow -> ClampToScreen)和拖动的位置限制
        /// 同时在改 Left，互相较劲。这里逐帧跑两套逻辑，记录位置抖动。
        /// </summary>
        public string RunJitterProbe()
        {
            DebugActiveOverride = 3;
            ForceHover = true;
            _morph = 1f;
            _reveal = 1f;
            _revealVel = 0f;
            _listOpen = true;
            _listExpanded = true;
            _listSpaceAllocated = true;
            _listHeightHold = false;
            Poll();
            ApplyLayout();

            Screen screen = Screen.FromPoint(new Point(0, 0));
            Rectangle work = screen.WorkingArea;
            var sb = new System.Text.StringBuilder();
            sb.Append("窗口 ").Append(Width).Append('x').Append(Height)
              .Append("  工作区 ").Append(work.Left).Append("..").Append(work.Right)
              .Append('\n');

            // 撞边后**猫**应停在距屏幕边缘 EdgeMargin 处
            int anchorNow = CatLeftInWindow(Width);
            int boundCatX = work.Left + EdgeMargin;
            int boundLeft = boundCatX - anchorNow;
            sb.Append("撞边后猫应停在 x=").Append(boundCatX)
              .Append(" (窗口 Left=").Append(boundLeft).Append(")\n");

            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
            SetBounds(Left, Top, Width, Height);
            Application.DoEvents();

            _dragging = true;
            _formStart = new Point(Left, Top);
            _dragStart = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);

            // 诊断：确认钳制本身工作正常
            {
                int dl, dt;
                ClampWindowPos(-9999, Top, out dl, out dt);
                sb.Append("[诊断] 向左推 -9999 被钳制到 ").Append(dl)
                  .Append(" (期望 ").Append(boundLeft).Append(")\n");
            }

            var seen = new System.Text.StringBuilder();
            int lastLeft = Left;
            int flips = 0;
            int maxJump = 0;
            bool touchedBound = false;
            int stepsAfterBound = 0;
            int step0Offset = 0;

            for (int step = 0; step < 400; step++)
            {
                // 每步都以"撞边后的正确起点"作为基准，避免累积误差
                if (step == 0)
                {
                    _formStart = new Point(boundLeft, Top);
                }
                int dx = -step * 5;
                int wantLeft = _formStart.X + dx;
                int wantTop = _formStart.Y;
                int okLeft, okTop;
                ClampWindowPos(wantLeft, wantTop, out okLeft, out okTop);

                // 复刻真实拖动路径：只赋一次位置
                SetBounds(okLeft, okTop, Width, Height);

                // 撞边时不重置锚点（和真实逻辑一致）

                // 每帧照常推进让位动画（拖动中应被冻结）

                // 用**猫**的屏幕位置判断是否已经撞到边界
                int catXNow = Left + CatLeftInWindow(Width);
                if (!touchedBound && catXNow <= boundCatX + 1)
                {
                    touchedBound = true;
                    lastLeft = Left;
                    continue;
                }

                if (touchedBound)
                {
                    stepsAfterBound++;
                    int jump = Math.Abs(Left - lastLeft);
                    if (jump > 0)
                    {
                        flips++;
                        if (jump > maxJump) maxJump = jump;
                        if (flips <= 6)
                        {
                            seen.Append("  第").Append(step).Append("步 Left ")
                                .Append(lastLeft).Append("->").Append(Left)
                                .Append(" 猫x=").Append(catXNow)
                                .Append(" (跳").Append(jump).Append("px)\n");
                        }
                    }
                }
                lastLeft = Left;
            }

            _dragging = false;
            _formStart = new Point(Left, Top);

            // ---- 第二段：鼠标在边界外小幅抖动 ----
            // 真实抖动最常发生在这里：鼠标已经出了边界，人还在微微动。
            // 复刻"只在该轴重置锚点 + 位置没变就不 SetBounds"的逻辑，
            // 统计窗口位置是否被反复改动。
            _dragging = true;
            _formStart = new Point(Left, Top);
            _dragStart = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
            int jitterChanges = 0;
            int jitterMax = 0;
            int anchorLeft = Left;
            var leftSeq = new List<int>();
            var sysb = new System.Text.StringBuilder();

            for (int i = 0; i < 60; i++)
            {
                // 鼠标在边界外左右抖 ±1..3px
                int curX = _dragStart.X - step0Offset - (i % 7) * 3;
                int curY = _dragStart.Y + ((i % 5) - 2);
                int dx = curX - _dragStart.X;
                int dy = curY - _dragStart.Y;
                int wantLeft = _formStart.X + dx;
                int wantTop = _formStart.Y + dy;
                int okLeft, okTop;
                ClampWindowPos(wantLeft, wantTop, out okLeft, out okTop);

                if (i < 6)
                {
                    sysb.Append("  抖").Append(i)
                         .Append(": formStart=").Append(_formStart.X)
                         .Append(" want=").Append(wantLeft)
                         .Append(" ok=").Append(okLeft)
                         .Append(" 当前Left=").Append(Left).Append('\n');
                }

                if (okLeft != Left || okTop != Top)
                {
                    jitterChanges++;
                    int j = Math.Abs(okLeft - Left) + Math.Abs(okTop - Top);
                    if (j > jitterMax) jitterMax = j;
                    SetBounds(okLeft, okTop, Width, Height);
                }

                // 撞边时不重置锚点，保留死区
                leftSeq.Add(Left);
            }

            // 真实判据：看 Left 序列里有多少次发生了变化
            int actualChanges = 0;
            for (int i = 1; i < leftSeq.Count; i++)
            {
                if (leftSeq[i] != leftSeq[i - 1]) actualChanges++;
            }
            string seqHead = leftSeq.Count > 8
                ? string.Join(",", leftSeq.GetRange(0, 8))
                : string.Join(",", leftSeq);

            _dragging = false;

            bool stable = touchedBound && flips == 0;
            bool jitterStable = actualChanges == 0;
            return sb + "撞边后又拖了 " + stepsAfterBound + " 步" +
                   "  位置变化次数=" + flips +
                   "  最大跳动=" + maxJump + "px  最终 Left=" + Left + "\n" +
                   seen +
                   sysb.ToString() +
                   "边界外抖 60 次: Left 序列开头=" + seqHead +
                   " 实际变化=" + actualChanges + "次\n" +
                   (!touchedBound ? "（未能撞到边界，测试无效）\n" : "") +
                   (stable ? "PASS" : "FAIL") + " 撞边后继续外拖，位置保持不动\n" +
                   (jitterStable ? "PASS" : "FAIL") + " 边界外抖动时窗口不晃";
        }

        /// <summary>
        /// 自检：模拟拖动，把窗口往四个方向推出去，确认被硬限制在工作区内。
        /// 用户要求的是"挪不动"，不是"能飘出去再弹回来"。
        /// </summary>
        public string RunDragProbe()
        {
            DebugActiveOverride = 0;   // 收起到最小状态，窗口最紧凑
            _listOpen = false;
            _listExpanded = false;
            _reveal = 0f;
            _revealVel = 0f;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            Poll();
            ApplyLayout();

            Screen screen = Screen.FromPoint(new Point(0, 0));
            Rectangle work = screen.WorkingArea;
            var sb = new System.Text.StringBuilder();
            sb.Append("工作区 ").Append(work.Left).Append(',').Append(work.Top)
              .Append(" .. ").Append(work.Right).Append(',').Append(work.Bottom)
              .Append("  窗口 ").Append(Width).Append('x').Append(Height)
              .Append("  EdgeMargin=").Append(EdgeMargin).Append('\n');

            bool ok = true;

            // 四个方向各推 4000px（远超屏幕尺寸），确保一定撞到边界
            var moves = new (string name, int dl, int dt)[]
            {
                ("往左推", -4000, 0),
                ("往右推", 4000, 0),
                ("往上推", 0, -4000),
                ("往下推", 0, 4000),
            };

            foreach (var m in moves)
            {
                SetBounds(work.Left + (work.Width - Width) / 2,
                          work.Top + (work.Height - Height) / 2,
                          Width, Height);
                Application.DoEvents();

                int wantL = Left + m.dl;
                int wantT = Top + m.dt;
                int cl, ct;
                ClampWindowPos(wantL, wantT, out cl, out ct);
                SetBounds(cl, ct, Width, Height);
                Application.DoEvents();



                int catX = Left + CatLeftInWindow(Width);
                int catWNow = (int)(PetSize * 0.817f);
                int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;
                int catYInWin = ListAreaFor(_active.Count);
                int catY = Top + catYInWin;

                // 判据按**猫身**：窗口允许溢出屏幕（任务框让位靠 UpdateListTarget），
                // 只有猫必须完整可见。
                bool inside = catX >= work.Left + EdgeMargin - 2 &&
                              catX + catWNow <= work.Right - EdgeMargin + 2 &&
                              catY >= work.Top + EdgeMargin - 2 &&
                              catY + PetSize <= work.Bottom - EdgeMargin + 2;
                if (!inside) ok = false;

                sb.Append(m.name).Append(" -> 停在 (").Append(Left).Append(',')
                  .Append(Top).Append(") 猫x=").Append(catX)
                  .Append("  猫在界内=").Append(inside ? "是" : "否").Append('\n');
            }

            return sb.ToString() +
                   (ok ? "PASS" : "FAIL") + " 四个方向都推不出屏幕";
        }

        /// <summary>
        /// 自检：把桌宠顶到屏幕左右边缘，验证两件事：
        ///   1. 猫身被限制在可见区域内，且保留 EdgeMargin
        ///   2. 列表展开后完整落在工作区内（会自动让到有空间的一侧）
        /// </summary>
        

        /// <summary>
        /// 自检：把猫分别推到屏幕左/中/右/上/下（真实钳制位置），
        /// 逐帧推进任务框适配后，直接算它的屏幕矩形是否完整可见、
        /// 以及中间区域是否严格居中。
        ///
        /// 不做 Application.DoEvents / DrawScene —— 在动画循环里调它们会卡死。
        /// </summary>
        public string RunListFitProbe()
        {
            DebugActiveOverride = 3;
            ForceHover = true;
            _morph = 1f;
            _reveal = 1f;
            _revealVel = 0f;
            _listOpen = true;
            _listExpanded = true;
            _listSpaceAllocated = true;
            _listHeightHold = false;
            _snapOffsetOnly = false;
            _dragging = false;
            Poll();
            ApplyLayout();

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            var sb = new System.Text.StringBuilder();
            sb.Append("工作区 ").Append(work.Left).Append(',').Append(work.Top)
              .Append(" .. ").Append(work.Right).Append(',').Append(work.Bottom)
              .Append("  ListEdgeMargin=").Append(ListEdgeMargin)
              .Append("  窗口 ").Append(Width).Append('x').Append(Height).Append('\n');

            bool ok = true;
            int centerLeft = work.Left + (work.Width - Width) / 2;
            int centerTop = work.Top + (work.Height - Height) / 2;

            var cases = new (string name, int wantLeft, int wantTop)[]
            {
                ("贴左", work.Left - 5000, centerTop),
                ("居中", centerLeft, centerTop),
                ("贴右", work.Right + 5000, centerTop),
                ("贴上", centerLeft, work.Top - 5000),
                ("贴下", centerLeft, work.Bottom + 5000),
                ("贴顶翻下", centerLeft, work.Top + 2),
            };

            foreach (var c in cases)
            {
                int cl, ct;
                ClampWindowPos(c.wantLeft, c.wantTop, out cl, out ct);
                Left = cl;
                Top = ct;

                for (int i = 0; i < 400; i++) StepListOffset(0.016f);

                int catX = Left + CatLeftInWindow(Width);
                int catW = (int)(PetSize * 0.817f);
                int catCx = catX + catW / 2;

                // 和 DrawScene 用完全一致的基准：catY 就是窗口内的 ListAreaFor 处
                int catYInWin = ListAreaFor(_active.Count);
                int catTopAbs = Top + catYInWin;

                int stackH = AnimatedListHeight();
                int totalH = stackH + ListTopPad;
                int listTop = catTopAbs + (int)Math.Round(_listOffY) - stackH - ListTopPad;
                int listCx = catCx + (int)Math.Round(_listOffX);
                int listLx = listCx - StackLayout.CardW / 2;

                bool inside = listLx >= work.Left + ListEdgeMargin - 2 &&
                              listLx + StackLayout.CardW <= work.Right - ListEdgeMargin + 2 &&
                              listTop >= work.Top + ListEdgeMargin - 2 &&
                              listTop + totalH <= work.Bottom - ListEdgeMargin + 2;
                bool centered = Math.Abs(listCx - catCx) <= 2;

                sb.Append(c.name).Append(": 猫中心=").Append(catCx)
                  .Append(" 列表中心=").Append(listCx)
                  .Append(" 偏移=").Append(listCx - catCx)
                  .Append("px 列表x=").Append(listLx).Append('~')
                  .Append(listLx + StackLayout.CardW)
                  .Append(" y=").Append(listTop).Append('~').Append(listTop + totalH)
                  .Append(" (屏 ").Append(work.Top).Append('~').Append(work.Bottom).Append(')')
                  .Append(" 界内=").Append(inside ? "是" : "否")
                  .Append(" 居中=").Append(centered ? "是" : "否").Append('\n');

                if (!inside) ok = false;
                if (c.name == "居中" && !centered) ok = false;
            }

            return sb.ToString() + (ok ? "PASS" : "FAIL")
                + " 居中严格对齐、贴边自动让位且不出屏";
        }

        /// <summary>
        /// 自检：除了拖动，任何操作都不许改变桌宠的屏幕位置。
        ///
        /// 逐个触发"点圆点弹出列表 / 收回 / 展开 / 停止按钮刷新"，
        /// 每次都比对猫的屏幕矩形。偏移超过 1px 就算失败。
        /// </summary>
        public string RunAnchorProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _reveal = 0f;
            _revealVel = 0f;
            _listOpen = false;
            _listExpanded = false;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            _dragging = false;
            _catAnchorValid = false;   // 清掉可能的脏锚点，从当前窗口重新初始化
            Poll();
            ApplyLayout();

            // 摆到屏幕中间，留出足够空间
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            SetBounds(work.Left + (work.Width - Width) / 2,
                      work.Top + (work.Height - Height) / 2, Width, Height);
            SetCatAnchorFromWindow();   // 锚点跟着这次摆位更新

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // 取基准：渲染一帧让 _lastCatScreenRect 生效
            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            Rectangle baseCat = _lastCatScreenRect;
            sb.Append("基准 猫屏幕位置=").Append(baseCat.X).Append(',')
              .Append(baseCat.Y).Append("  窗口=").Append(Width)
              .Append('x').Append(Height).Append('\n');

            // 依次做各种操作，每次都检查猫有没有动
            string[] steps = { "点圆点弹出列表", "点堆叠区展开", "点箭头收回", "再弹出", "再次收回" };

            for (int k = 0; k < steps.Length; k++)
            {
                switch (k)
                {
                    case 0: ToggleList(); break;                       // 弹出
                    case 1: _listExpanded = true; _stack.SetSpread(true); Reflow(); break;
                    case 2: ToggleList(); break;                       // 堆叠 -> 关闭
                    case 3: ToggleList(); break;                       // 再弹出
                    case 4: CloseList(); Poll(); break;                // 收回 + 刷新
                }

                using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                    PixelFormat.Format32bppArgb))
                {
                    DrawScene(b, Width, Height);
                }
                Rectangle now = _lastCatScreenRect;
                int dx = now.X - baseCat.X;
                int dy = now.Y - baseCat.Y;
                bool same = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
                if (!same) ok = false;

                sb.Append(steps[k]).Append(" -> 猫位置=").Append(now.X).Append(',')
                  .Append(now.Y).Append("  位移=(").Append(dx).Append(',')
                  .Append(dy).Append(")  窗口=").Append(Width).Append('x')
                  .Append(Height).Append("  锚点=").Append(_catAnchor.X).Append(',')
                  .Append(_catAnchor.Y).Append(" Top=").Append(Top)
                  .Append(same ? "  未移动" : "  动了！").Append('\n');
            }

            _listOpen = false;
            _reveal = 0f;
            _revealVel = 0f;
            return sb.ToString() + (ok ? "PASS" : "FAIL")
                + " 非拖动操作不改变桌宠位置";
        }

        /// <summary>
        /// 自检：模拟"点小圆点"后**第一帧**的渲染，直接报告画布尺寸与列表位置。
        /// 用户反馈的第一个 bug 是"卡片先出现在很左侧"，说明首帧用了错误的宽度。
        /// </summary>
                /// <summary>
        /// 自检：逐步复刻真实点击序列，**每一步之后都渲染一次**，
        /// 记录当时列表中心与猫中心的偏差。
        /// 用来抓"点开后短暂出现在很左侧"的时序 bug。
        /// </summary>
        /// <summary>
        /// 自检：收起的整段淡出过程中，卡片底边必须一直贴着猫头顶，
        /// 不能一路下沉到盖住猫才消失。
        /// </summary>
        /// <summary>
        /// 自检：鼠标悬停到卡片按钮时，按钮必须变深（有可见反馈）。
        /// 否则用户点下去看不到变化，会以为程序卡了。
        /// </summary>
        public string RunHoverProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = false;
            _morph = 1f;
            _reveal = 1f;
            _revealVel = 0f;
            _listOpen = true;
            _listExpanded = true;
            _listSpaceAllocated = true;
            _listHeightHold = false;
            _dragging = false;
            _catAnchorValid = false;
            Poll();
            ApplyLayout();

            _stack.SetRows(Math.Min(rows, StackLayout.MaxCards));
            _stack.SnapSpread(1f);

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // 先把列表画一遍，拿到按钮矩形
            RenderListOnly(out _lastListRect);

            if (_btnReply.Length == 0 || _btnStop.Length == 0)
            {
                return "找不到按钮矩形";
            }

            // 采样点：按钮圆心
            Func<Rectangle, Point> center = r =>
                new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

            // ---- 未悬停时的底色 ----
            _hoverAnyBtn = false;
            _hoverRow = -1;

            // ---- 悬停到回复按钮 ----
            _hoverAnyBtn = true;
            _hoverRow = 0;
            _hoverKind = IconKind.Reply;

            // 分别渲染并比较按钮圆心的像素亮度
            int[] normal = SampleButtons(null);
            int[] hoverReply = SampleButtons(IconKind.Reply);
            int[] hoverStop = SampleButtons(IconKind.Stop);

            sb.Append("回复按钮 圆心亮度: 常态=").Append(normal[0])
              .Append(" 悬停=").Append(hoverReply[0]).Append('\n');
            sb.Append("停止按钮 圆心亮度: 常态=").Append(normal[1])
              .Append(" 悬停=").Append(hoverStop[1]).Append('\n');

            // 悬停时底色变深 -> 亮度下降
            if (hoverReply[0] >= normal[0]) ok = false;
            if (hoverStop[1] >= normal[1]) ok = false;

            _hoverAnyBtn = false;
            _hoverRow = -1;
            _listOpen = false;
            _reveal = 0f;
            _revealVel = 0f;

            return sb.ToString() + (ok ? "PASS" : "FAIL")
                + " 悬停按钮时底色变深";
        }

        /// <summary>只渲染列表并返回按钮圆心亮度（Index 0=回复, 1=停止）。</summary>
        private int[] SampleButtons(IconKind? hoverKind)
        {
            if (hoverKind.HasValue)
            {
                _hoverAnyBtn = true;
                _hoverRow = 0;
                _hoverKind = hoverKind.Value;
            }
            else
            {
                _hoverAnyBtn = false;
                _hoverRow = -1;
            }

            int w = Math.Max(1, Width);
            int h = Math.Max(1, Height);
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(b, w, h);
                var result = new int[2];
                Rectangle[] rects = { _btnReply[0], _btnStop[0] };
                // 采样点：按钮圆心的**左上方**，贴近边缘但仍在圆内，
                // 这样避开了中间的图标（图标占直径的 44%），量到的是圆底本身。
                for (int k = 0; k < 2; k++)
                {
                    Rectangle r = rects[k];
                    Point c = new Point(r.X + (int)(r.Width * 0.22f),
                                        r.Y + (int)(r.Height * 0.22f));
                    if (c.X < 0 || c.Y < 0 || c.X >= w || c.Y >= h)
                    {
                        result[k] = -1;
                        continue;
                    }
                    Color px = b.GetPixel(c.X, c.Y);
                    result[k] = (px.R + px.G + px.B) / 3;
                }
                return result;
            }
        }

        /// <summary>占位：渲染列表以填充按钮矩形。</summary>
        private void RenderListOnly(out Rectangle unused)
        {
            unused = Rectangle.Empty;
            int w = Math.Max(1, Width);
            int h = Math.Max(1, Height);
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                DrawScene(b, w, h);
            }
        }

        public string RunFadeProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _listOpen = true;
            _listExpanded = false;
            _listSpaceAllocated = true;
            _listHeightHold = false;
            _dragging = false;
            _catAnchorValid = false;
            Poll();
            ApplyLayout();

            _stack.SetRows(Math.Min(rows, StackLayout.MaxCards));
            _stack.SnapSpread(0f);
            _reveal = 1f;
            _revealVel = 0f;

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            int baseBottom = _lastListRect.Y + _lastListRect.Height;
            sb.Append("基准 底边y=").Append(baseBottom).Append('\n');

            CloseList();

            for (int i = 0; i < 40; i++)
            {
                StepReveal(0.016f);
                if (_reveal <= 0.002f) break;

                using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                    PixelFormat.Format32bppArgb))
                {
                    DrawScene(b, Width, Height);
                }
                int bottom = _lastListRect.Y + _lastListRect.Height;
                int drift = bottom - baseBottom;
                if (i < 5)
                {
                    sb.Append("  帧").Append(i).Append(" reveal=")
                      .Append(_reveal.ToString("0.00"))
                      .Append(" 底边y=").Append(bottom)
                      .Append(" 相对基准=").Append(drift).Append("px\n");
                }
                if (drift > 14) ok = false;
            }

            _listOpen = false;
            _reveal = 0f;
            _revealVel = 0f;
            return sb.ToString() + (ok ? "PASS" : "FAIL") + " 淡出过程中卡片不下沉";
        }

        public string RunFirstFrameProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _reveal = 0f;
            _revealVel = 0f;
            _listOpen = false;
            _listExpanded = false;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            _dragging = false;
            _catAnchorValid = false;
            Poll();
            ApplyLayout();

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // 记录一帧的偏差
            Func<string, bool> snap = (label) =>
            {
                int w = ClientSize.Width;
                int h = ClientSize.Height;
                if (w <= 0 || h <= 0) return true;

                using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    DrawScene(b, w, h);
                }
                int catCx = CatLeftInWindow(w) + (int)(PetSize * 0.817f) / 2;
                int listCx = _lastListRect.X + _lastListRect.Width / 2;
                int drift = listCx - catCx;

                sb.Append("  ").Append(label).Append(": 画布=").Append(w)
                  .Append("x").Append(h)
                  .Append(" 猫中心=").Append(catCx)
                  .Append(" 列表中心=").Append(listCx)
                  .Append(" 偏差=").Append(drift).Append("px\n");
                // 列表还没显示时（_lastListRect 为空）不算偏差
                if (_lastListRect.Width > 0 && Math.Abs(drift) > 6) return false;
                return true;
            };

            ok &= snap("点开前");

            // 完全复刻 ToggleList 的内部步骤，每步都看一帧
            _listOpen = true;                       ok &= snap("置 _listOpen=true");
            int count = Math.Min(_active.Count, StackLayout.MaxCards);
            _stack.SetRows(count);                  ok &= snap("SetRows");
            _stack.SnapSpread(0f);                  ok &= snap("SnapSpread(0)");
            _stack.SetSpread(false);                ok &= snap("SetSpread(false)");
            _reveal = 0f; _revealVel = 0f;          ok &= snap("reveal=0");
            _listSpaceAllocated = true;             ok &= snap("spaceAllocated");
            _listHeightHold = false;                ok &= snap("heightHold=false");
            UpdateListTarget();                     ok &= snap("UpdateListTarget");
            _listOffX = _listTarX;
            _listOffY = _listTarY;
            _listOffXVel = 0f;
            _listOffYVel = 0f;                      ok &= snap("偏移归位");
            Reflow();                               ok &= snap("Reflow");

            _listOpen = false;
            _reveal = 0f;
            _revealVel = 0f;
            _listExpanded = false;
            return sb.ToString() + (ok ? "PASS" : "FAIL")
                + " 点开全过程每一帧列表都对准猫头顶";
        }


        /// <summary>
        /// 自检：点开 / 收回列表时，第一帧的卡片位置必须已经是正确位置，
        /// 不能先出现在别处再飞过来。
        ///
        /// 用户反馈的两个闪现：
        ///   - 点圆点后卡片先出现在很左侧，再飞回头顶
        ///   - 收起时卡片先跳到很上方，再落回堆叠位置
        /// </summary>
        public string RunListJumpProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _reveal = 0f;
            _revealVel = 0f;
            _listOpen = false;
            _listExpanded = false;
            _listSpaceAllocated = false;
            _listHeightHold = false;
            _dragging = false;
            _catAnchorValid = false;
            Poll();
            ApplyLayout();

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            SetBounds(work.Left + (work.Width - Width) / 2,
                      work.Top + (work.Height - Height) / 2, Width, Height);
            SetCatAnchorFromWindow();

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // ---- 1) 点开列表，检查第一帧 ----
            ToggleList();

            // 期望值必须在**点开之后**算：窗口已从 128 变成 512，
            // 猫在窗口内的位置随之改变。
            int catXInWin = CatLeftInWindow(Width);
            int expectedCx = catXInWin + (int)(PetSize * 0.817f) / 2;

            // 立刻（不推进任何动画）渲染一帧
            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            int firstLx = _lastListRect.X;
            int firstCx = firstLx + _lastListRect.Width / 2;
            int driftX = firstCx - expectedCx;

            sb.Append("点开首帧: 列表中心=").Append(firstCx)
              .Append(" 期望=").Append(expectedCx)
              .Append(" 横向偏差=").Append(driftX).Append("px\n");
            if (Math.Abs(driftX) > 6) ok = false;

            // 收敛后应稳定在同一位置
            for (int i = 0; i < 300; i++) { StepReveal(0.016f); StepListOffset(0.016f); }
            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            int settledCx = _lastListRect.X + _lastListRect.Width / 2;
            int settledTop = _lastListRect.Y;
            sb.Append("收敛后:   列表中心=").Append(settledCx)
              .Append(" 顶部y=").Append(settledTop).Append("\n");
            if (Math.Abs(settledCx - expectedCx) > 6) ok = false;

            // ---- 2) 展开，再检查收回时首帧纵向位置 ----
            _listExpanded = true;
            _listHeightHold = true;
            _stack.SetSpread(true);
            for (int i = 0; i < 300; i++) { StepListOffset(0.016f); _stack.Step(0.016f); }

            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            int expandedTop = _lastListRect.Y;
            int expandedBottom = _lastListRect.Y + _lastListRect.Height;

            // 收回到堆叠（对应点箭头时的第一段动作）
            _listExpanded = false;
            _stack.SetSpread(false);
            StepListOffset(0.016f);

            using (Bitmap b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb))
            {
                DrawScene(b, Width, Height);
            }
            int collapsingBottom = _lastListRect.Y + _lastListRect.Height;
            int dy = collapsingBottom - expandedBottom;

            sb.Append("展开时 底部y=").Append(expandedBottom)
              .Append("  收回首帧 底部y=").Append(collapsingBottom)
              .Append("  底边位移=").Append(dy).Append("px\n");
            // 底边应当保持在猫头顶附近（位移极小），不能跳
            if (Math.Abs(dy) > 12) ok = false;

            _listOpen = false;
            _reveal = 0f;
            _revealVel = 0f;
            return sb.ToString() + (ok ? "PASS" : "FAIL")
                + " 列表首帧即在正确位置，无闪现";
        }

        public string RunClickProbe(int rows)
        {
            DebugActiveOverride = rows;
            ForceHover = true;
            _morph = 1f;
            _reveal = 0f;
            _revealVel = 0f;
            _listOpen = false;
            _listExpanded = false;
            Poll();

            var sb = new System.Text.StringBuilder();

            // 第一次点击：弹出（叠好的状态）
            ToggleList();
            sb.Append("第1次点击 -> 打开=").Append(_listOpen ? "是" : "否")
              .Append(" 摊开=").Append(_listExpanded ? "是" : "否").Append('\n');
            bool ok1 = _listOpen && !_listExpanded;

            // 第二次点击：堆叠状态下必须直接收起
            ToggleList();
            sb.Append("第2次点击 -> 打开=").Append(_listOpen ? "是" : "否")
              .Append(" 摊开=").Append(_listExpanded ? "是" : "否").Append('\n');
            bool ok2 = !_listOpen;

            // 重新打开 -> 手动摊开 -> 点箭头：应回到堆叠而不是关闭
            ToggleList();
            bool opened = _listOpen;
            _listExpanded = true;
            _stack.SetSpread(true);
            sb.Append("打开并摊开 -> 打开=").Append(_listOpen ? "是" : "否")
              .Append(" 摊开=").Append(_listExpanded ? "是" : "否").Append('\n');
            bool ok3 = opened && _listExpanded;

            ToggleList();
            sb.Append("摊开时点箭头 -> 打开=").Append(_listOpen ? "是" : "否")
              .Append(" 摊开=").Append(_listExpanded ? "是" : "否").Append('\n');
            bool ok4 = _listOpen && !_listExpanded;   // 还在，但回到堆叠

            ToggleList();
            sb.Append("堆叠时再点箭头 -> 打开=").Append(_listOpen ? "是" : "否").Append('\n');
            bool ok5 = !_listOpen;                    // 这次才关闭

            _reveal = 0f;
            _revealVel = 0f;
            return sb +
                   (ok1 ? "PASS" : "FAIL") + " 第一次点击弹出且为堆叠态" +
                   "\n" + (ok2 ? "PASS" : "FAIL") + " 堆叠状态下点击直接关闭" +
                   "\n" + (ok3 ? "PASS" : "FAIL") + " 重新打开后可摊开" +
                   "\n" + (ok4 ? "PASS" : "FAIL") + " 摊开时点箭头先回到堆叠（不关闭）" +
                   "\n" + (ok5 ? "PASS" : "FAIL") + " 堆叠时再点一次才关闭";
        }

        /// <summary>列表是否已完全摊开（false 表示只是叠成一摞）。</summary>
        private bool _listExpanded;

        // ---- 会话列表"出现 / 消失"的显现弹簧 ----
        // 0 = 完全隐藏，1 = 完全显现。
        // 点圆点弹出、点箭头收回，都用这一条，参数与列表展开、形态过渡一致（200/13），
        // 三处动作速度统一。之前这两个动作是瞬时的，用户觉得太快。
        private float _reveal;
        private float _revealVel;
        private const float RevealStiffness = 110f;
        private const float RevealDamping = 10f;
        /// <summary>显现过程中卡片从下方滑入的距离。</summary>
        private const int RevealSlidePx = 10;
        /// <summary>窗口是否已经为列表预留了高度（用于避免每帧改尺寸）。</summary>
        private bool _listSpaceAllocated;
        /// <summary>
        /// 过渡期间把窗口按住"展开高度"。
        /// 展开/收回一开始置 true，弹簧停下来后清零，
        /// 这样每次过渡只改一次窗口尺寸（开始长高 / 结束缩回），中途不变。
        /// </summary>
        private bool _listHeightHold;

        public void RefreshSessions()
        {
            Poll();
        }

        /// <summary>
        /// 收起列表。
        ///
        /// 不再瞬时清空：把 _listOpen 置 false，显现弹簧会把它从 1 收到 0，
        /// 窗口高度在这一过程中保持不变，等 reveal 归零后 Tick 里再缩窗口。
        /// 这样收回和弹出速度一致（同一组 200/13 弹簧），又不会每帧改窗口尺寸。
        /// </summary>
        public void CloseList()
        {
            if (!_listOpen) return;
            _listOpen = false;
            _listExpanded = false;   // 下次打开又是叠好的状态
            // 立刻失效命中区域，收的过程中不该再点到卡片
            _rowRects = new Rectangle[0];
            _btnClose = new Rectangle[0];
            _btnReply = new Rectangle[0];
            _btnStop = new Rectangle[0];
            _lastStackRect = Rectangle.Empty;
            // 卡片的叠放状态等完全隐藏后再复位，避免收回时突然塌成一摞
        }

        private static string Short2(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length > 12 ? s.Substring(0, 12) + "…" : s;
        }

        /// <summary>
        /// 拖动/布局时的限制：按**猫身**计算，而不是整个窗口。
        ///
        /// 用户明确要求"桌宠和任务框各对各的，互不影响"：
        /// 窗口为了装下任务框会变宽，变宽后不该把猫一起推回屏幕里，
        /// 也不该让任务框的宽度影响猫能贴到多靠边。
        /// 所以唯一需要留边的是猫身本身。
        ///
        /// left/top 是**窗口**左上角的目标值；这里换算成猫的屏幕位置再判断。
        /// </summary>
        private void ClampWindowPos(int wantLeft, int wantTop, out int left, out int top)
        {
            left = wantLeft;
            top = wantTop;
            if (Width <= 0 || Height <= 0) return;

            // 用**主屏**判断边界：用当前窗口位置挑屏幕时，窗口一旦被推到
            // 屏幕外（Left 变成 -1233 这种），FromPoint 会落到虚拟屏上，
            // 算出来的工作区完全不同，钳制就失效了 —— 实测左推能一路到 -1914。
            // 桌宠只在主屏上活动，固定用主屏最稳，也符合用户预期。
            Rectangle work = Screen.PrimaryScreen.WorkingArea;

            int catW = (int)(PetSize * 0.817f);
            int catH = PetSize;
            int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;
            int catYInWin = ListAreaFor(_active.Count);

            // 猫在窗口内的位置随 _hugT 变化（贴边时猫靠向窗口外侧）。
            // 这里必须按**当前实际**的锚点算，否则猫会被算出屏幕外。
            int anchor = CatLeftInWindow(Width);

            // 猫的左边界与右边界
            int minLeft = work.Left + EdgeMargin - anchor;
            int maxLeft = work.Right - EdgeMargin - catW - anchor;
            if (minLeft > maxLeft)
            {
                int mid = (minLeft + maxLeft) / 2;
                minLeft = maxLeft = mid;
            }
            left = Math.Max(minLeft, Math.Min(wantLeft, maxLeft));

            int minTop = work.Top + EdgeMargin - catYInWin;
            int maxTop = work.Bottom - EdgeMargin - catH - catYInWin;
            if (minTop > maxTop)
            {
                int mid = (minTop + maxTop) / 2;
                minTop = maxTop = mid;
            }
            top = Math.Max(minTop, Math.Min(wantTop, maxTop));
        }

        /// <summary>按猫身把窗口拉回可见区（窗口溢出屏幕是允许的）。</summary>
        private void ClampCatToScreen()
        {
            int l, t;
            ClampWindowPos(Left, Top, out l, out t);
            if (l != Left || t != Top)
            {
                SetBounds(l, t, Width, Height);
            }
        }

        /// <summary>兼容旧调用点：行为已改为按猫身限制。</summary>
        private void ClampWindowToScreen()
        {
            ClampCatToScreen();
        }

        private void ClampToScreen()
        {
            if (!IsHandleCreated) return;

            int catW = (int)(PetSize * 0.817f);
            int catH = PetSize;
            int indicatorArea = _active.Count > 0 ? IndicatorSlotH : 0;
            int catYInWin = ListAreaFor(_active.Count);

            int catX = Left + CatLeftInWindow(Width);
            Screen screen = Screen.FromPoint(
                new Point(catX + catW / 2, Top + catYInWin + catH / 2));
            Rectangle work = screen.WorkingArea;

            // 按**猫身**限制，而不是按窗口：
            // 窗口在列表/气泡展开时会变宽，那时窗口本身允许伸出屏幕，
            // 但猫必须完整留在可见区域内，且离边缘至少 EdgeMargin。
            // 之前是按窗口留 34px，结果猫能被拖到几乎完全出屏，
            // 列表展开后整块被屏幕边缘切掉（用户截图里的样子）。
            int anchor = CatLeftInWindow(Width);

            int minLeft = work.Left + EdgeMargin - anchor;
            int maxLeft = work.Right - EdgeMargin - catW - anchor;
            if (minLeft > maxLeft)
            {
                int mid = (minLeft + maxLeft) / 2;
                minLeft = maxLeft = mid;
            }
            Left = Math.Max(minLeft, Math.Min(Left, maxLeft));

            int minTop = work.Top + EdgeMargin - catYInWin;
            int maxTop = work.Bottom - EdgeMargin - catH - catYInWin;
            if (minTop > maxTop)
            {
                int mid = (minTop + maxTop) / 2;
                minTop = maxTop = mid;
            }
            Top = Math.Max(minTop, Math.Min(Top, maxTop));
        }


        /// <summary>
        /// 计算任务框相对猫头的目标偏移，并做屏幕四向适配。
        ///
        /// 默认：卡片摞中心对准猫头顶中心，摞底边贴在头顶上方 ListBottomGap 处。
        /// 贴边时依次尝试：上方 -> 下方（若上方放不下）；横向则左右平移让位。
        ///
        /// 目标值只算一次，实际位置由 StepListOffset 的弹簧追过去 ——
        /// 这样拖动时任务框会平滑跟着挪，而不是瞬间跳。
        /// </summary>
        private void UpdateListTarget()
        {
            // 只有列表真的在显示（打开中或淡出中）才有目标位置。
            if (!_listOpen && _reveal <= 0.002f)
            {
                _listTarX = 0f;
                _listTarY = -(ListBottomGap + 4);
                return;
            }

            int rowsNow = Math.Min(_active.Count, StackLayout.MaxCards);
            if (rowsNow <= 0)
            {
                _listTarX = 0f;
                _listTarY = -(ListBottomGap + 4);
                return;
            }

            int catW = (int)(PetSize * 0.817f);
            int catH = PetSize;
            int catYInWin = ListAreaFor(_active.Count);

            // 猫在屏幕上的位置
            int catX = Left + CatLeftInWindow(Width);
            int catTop = Top + catYInWin;
            int catCx = catX + catW / 2;

            Screen screen = Screen.FromPoint(new Point(catCx, catTop + catH / 2));
            Rectangle work = screen.WorkingArea;

            // ---- 纵向：默认在头顶上方；上方放不下就翻到下方 ----
            // offY 表示"卡片摞**底边**"相对猫头顶的偏移。
            //
            // 关键：**一律按展开后的高度算**，不用当前动画高度。
            // 之前用 AnimatedListHeight()（随 _stack.Spread 变化），
            // 目标位置在展开/收回动画中一直是变的，弹簧不停追 ——
            // 于是"刚打开时列表先出现在很左侧、再飞回头顶"，
            // "收回时先跳到很上方、再落回原位"（用户反馈的两个闪现）。
            int fullH = StackLayout.ExpandedHeight(rowsNow) + ListTopPad;

            float offAbove = -ListBottomGap;
            int topIfAbove = catTop + (int)offAbove - fullH;
            bool fitsAbove = topIfAbove >= work.Top + ListEdgeMargin;

            // 翻到下方时，卡片顶部必须让开脚底的**任务指示器**（收起箭头）。
            // 原来只留了 ListBottomGap(4px)，卡片会直接盖住那颗箭头，
            // 用户点不到收起按钮（截图里的问题）。
            // 这里加上指示器槽位 + 一点呼吸，刚好让箭头完整露出。
            int indicatorReserve = (_active.Count > 0 ? IndicatorSlotH : 0) + ListBottomGap;
            float offBelow = catH + indicatorReserve + fullH;
            int topIfBelow = catTop + (int)offBelow - fullH;
            bool fitsBelow = topIfBelow + fullH <= work.Bottom - ListEdgeMargin;
            float offY;
            if (fitsAbove)
            {
                offY = offAbove;
            }
            else if (fitsBelow)
            {
                offY = offBelow;
            }
            else
            {
                // 上下都放不下（窗口自己已经占据超过半屏）。这时不做翻转，
                // 直接把整摞夹进工作区 —— 翻转后照样超出，不如保证完整可见。
                int clampedTop = Math.Max(work.Top + ListEdgeMargin,
                                          Math.Min(topIfAbove, work.Bottom - ListEdgeMargin - fullH));
                offY = clampedTop + fullH - catTop;
            }

            // ---- 横向：以猫中心为基准，提前让位 ----
            //
            // 卡片中心的可活动区间：
            //   左边界 = 屏幕左边 + 触发距离 + 半个卡宽
            //   右边界 = 屏幕右边 - 触发距离 - 半个卡宽
            // 触发距离用 ListEdgeMargin（56px），意味着当猫中心进入
            // "离屏幕边 56px + 半卡宽" 的范围时，卡片就开始往反方向挪，
            // 而不是等到真的贴边才动。
            int halfW = StackLayout.CardW / 2 + ListTopPad;
            int minCx = work.Left + ListEdgeMargin + halfW;
            int maxCx = work.Right - ListEdgeMargin - halfW;

            int wantCx = catCx;
            if (minCx > maxCx)
            {
                wantCx = (minCx + maxCx) / 2;
            }
            else
            {
                wantCx = Math.Max(minCx, Math.Min(catCx, maxCx));
            }
            float offX = wantCx - catCx;

            // 再加一道**窗口内**约束：列表是画在窗口自己的图层上的，
            // 超出窗口的部分会被硬裁掉（用户看到的"被截断一部分"）。
            // 所以偏移量不能超过窗口能容纳的范围。
            //   卡片左缘 >= 0  →  offX >= -(catCxInWin) + CardW/2
            //   卡片右缘 <= W  →  offX <= W - catCxInWin - CardW/2
            int catCxInWin = CatLeftInWindow(Width) + catW / 2;
            int halfCard = StackLayout.CardW / 2;
            int minOffX = -(catCxInWin - halfCard);
            int maxOffX = Width - catCxInWin - halfCard;
            if (minOffX > maxOffX)
            {
                int mid = (minOffX + maxOffX) / 2;
                minOffX = maxOffX = mid;
            }
            offX = Math.Max(minOffX, Math.Min(offX, maxOffX));

            // 纵向也要夹在窗口内：卡片顶边 >= 0，底边 <= H。
            // offY 表示"摞底边"相对猫头顶的偏移，换算成窗口内坐标再限制。
            int catTopInWin = ListAreaFor(_active.Count);
            int bottomInWin = catTopInWin + (int)Math.Round(offY);
            int topInWin = bottomInWin - fullH;
            int minTop2 = ListTopPad;
            int maxTop2 = Math.Max(minTop2, Height - fullH);
            int winTop = Math.Max(minTop2, Math.Min(topInWin, maxTop2));
            offY = winTop + fullH - catTopInWin;

            _listTarX = offX;
            _listTarY = offY;
        }

        /// <summary>
        /// 推进任务框位置的弹簧。
        /// 用户要求"适配过程有动作，速度跟随拖动" —— 这里用和形态过渡同一组参数，
        /// 拖动时目标每帧都在变，弹簧跟着追，看起来就是平滑跟随。
        /// </summary>
        private void StepListOffset(float dt)
        {
            // 导图期间不追目标：那时窗口可能还没定位，屏幕适配算出的目标不可信，
            // 会把已经摆好的偏移又推走。
            if (_snapOffsetOnly)
            {
                return;
            }

            UpdateListTarget();

            // 拖动中：窗口正跟着鼠标快速移动，弹簧追不上会"猫和任务框分离"。
            // 所以拖动时把弹簧调得很紧（几乎贴合），只留一点点滞后感 ——
            // 用户要的是"贴边时能看到任务框挪过去"，不是"拖动全程都在飘"。
            float stiffness = _dragging ? 600f : ListOffStiffness;
            float damping = _dragging ? 40f : ListOffDamping;
            if (Math.Abs(_listOffX - _listTarX) < 0.5f && Math.Abs(_listOffXVel) < 1f)
            {
                _listOffX = _listTarX;
                _listOffXVel = 0f;
            }
            else
            {
                float ax = -stiffness * (_listOffX - _listTarX) - damping * _listOffXVel;
                _listOffXVel += ax * dt;
                _listOffX += _listOffXVel * dt;
            }

            if (Math.Abs(_listOffY - _listTarY) < 0.5f && Math.Abs(_listOffYVel) < 1f)
            {
                _listOffY = _listTarY;
                _listOffYVel = 0f;
            }
            else
            {
                float ay = -stiffness * (_listOffY - _listTarY) - damping * _listOffYVel;
                _listOffYVel += ay * dt;
                _listOffY += _listOffYVel * dt;
            }
        }

        // 任务框跟随弹簧：比堆叠/形态更软一些，跟随拖动时才有"黏着感"
        private const float ListOffStiffness = 90f;
        private const float ListOffDamping = 14f;


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

            // _activeCount 是**角标数字**，语义是"正在运行几个任务"，
            // 所以只数 Active 的。列表里可能还挂着已完成的卡片
            // （用户要求完成后不自动消失），但它们不该计入运行数。
            int running = 0;
            foreach (SessionItem it in items) { if (it.Active) running++; }
            _activeCount = running;

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
                if (_listOpen) _stack.SetRows(Math.Min(_active.Count, StackLayout.MaxCards));
                Reflow();
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