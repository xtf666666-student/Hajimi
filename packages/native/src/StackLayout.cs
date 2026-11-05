using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HajimiPet
{
    /// <summary>
    /// 会话卡片的堆叠布局 + 弹簧动画。
    ///
    /// 用户要求的是 iPhone 通知堆叠那种观感：每张卡片独立、圆角、有厚度，
    /// 而不是一个装在一起的大框。收起时彼此叠成一小摞，展开时按弹簧曲线散开。
    ///
    /// 这里只管布局与插值，不负责绘制（绘制在 PetForm 里，共用同一套坐标）。
    /// </summary>
    internal sealed class StackLayout
    {
        // 两行卡片：标题 + 内容（仿用户给的通知样式）
        // 宽度从 248 加到 360：卡里要放"标题 · 内容"两段文字，
        // 248 时内容被挤得不到 120px，几乎全靠省略号。
        // 再放到 360 后内容区约 200px，能显示十来个汉字。
        public const int CardW = 360;
        public const int CardH = 58;
        /// <summary>收起时每张只露出这么多，形成"一摞"。
        /// 16px 刚好露出下沿圆角，iPhone 通知堆叠就是这个观感。</summary>
        public const int PeekOffset = 16;
        /// <summary>展开时相邻卡片之间的缝隙。
        /// 7px 太窄（卡片高 58，边框+投影占掉一部分，看着像紧挨着），
        /// 12px 能让每张卡片的圆角和投影完整可见，层次清楚。</summary>
        public const int Gap = 12;
        public const int MaxCards = 6;

        private readonly List<float> _y = new List<float>();
        private readonly List<float> _target = new List<float>();
        private int _rows;

        /// <summary>展开进度：0 = 收起（叠成一摞），1 = 完全展开。</summary>
        private float _spread;

        public float Spread { get { return _spread; } }

        /// <summary>展开进度的目标值（0 = 堆叠，1 = 完全展开）。</summary>
        public float SpreadTarget { get { return _spreadTarget; } }
        public int Rows { get { return _rows; } }

        /// <summary>某张卡片当前的顶部 y（已含弹簧插值）。</summary>
        public float YOf(int index)
        {
            return index >= 0 && index < _y.Count ? _y[index] : 0f;
        }

        /// <summary>完全展开时，整摞总共需要多高。</summary>
        public static int ExpandedHeight(int rows)
        {
            if (rows <= 0) return 0;
            return rows * CardH + (rows - 1) * Gap;
        }

        /// <summary>收起时占用的高度（只有最上面那张完整可见）。</summary>
        public static int CollapsedHeight(int rows)
        {
            if (rows <= 0) return 0;
            return CardH + (rows - 1) * PeekOffset;
        }

        public int CurrentHeight()
        {
            if (_rows <= 0) return 0;
            int expanded = ExpandedHeight(_rows);
            int collapsed = CollapsedHeight(_rows);
            return (int)(collapsed + (expanded - collapsed) * _spread);
        }

        /// <summary>
        /// 展开进度到达目标值之后的高度。
        ///
        /// 窗口尺寸按这个值算，展开/收起过程中就不必每帧改窗口大小 ——
        /// 只改一次，卡片在固定的窗口里做插值。这是消除掉帧的关键。
        /// </summary>
        public int TargetHeight()
        {
            if (_rows <= 0) return 0;
            int expanded = ExpandedHeight(_rows);
            int collapsed = CollapsedHeight(_rows);
            return (int)(collapsed + (expanded - collapsed) * _spreadTarget);
        }

        /// <summary>行数变化时重建目标位置，并沿用已有的 y 继续插值。</summary>
        public void SetRows(int rows)
        {
            if (rows > MaxCards) rows = MaxCards;
            if (rows == _rows && _y.Count == rows) return;

            var oldY = new List<float>(_y);
            _rows = rows;

            while (_y.Count > rows) _y.RemoveAt(_y.Count - 1);
            while (_vel.Count > rows) _vel.RemoveAt(_vel.Count - 1);

            for (int i = _y.Count; i < rows; i++)
            {
                // 新增的卡片从"上一张的位置"滑入，而不是从 0 冒出来
                _y.Add(oldY.Count > 0 ? oldY[Math.Min(i, oldY.Count - 1)] : 0f);
                _vel.Add(0f);
            }

            _target.Clear();
            for (int i = 0; i < rows; i++) _target.Add(0f);
            RecomputeTargets();

            // 首次出现（之前没有行）时直接摆到位，避免整摞从 0 挤成一团
            if (rows > 0 && oldY.Count == 0)
            {
                for (int i = 0; i < _y.Count; i++)
                {
                    if (i < _target.Count) _y[i] = _target[i];
                }
            }
        }

        public void SetSpread(bool open)
        {
            _spreadTarget = open ? 1f : 0f;
        }

        /// <summary>无动画地把展开进度设为某个值（用于"从叠好的状态开始展开"）。</summary>
        public void SnapSpread(float value)
        {
            _spread = Math.Max(0f, Math.Min(1f, value));
            _spreadVel = 0f;
            _spreadTarget = _spread;
            RecomputeTargets();

            // 关键：位置也要立刻对齐到目标，否则每张卡都停在 y=0，
            // 叠在一起看起来只剩一张。
            for (int i = 0; i < _y.Count; i++)
            {
                if (i < _target.Count) _y[i] = _target[i];
                if (i < _vel.Count) _vel[i] = 0f;
            }
        }

        private float _spreadTarget;

        /// <summary>
        /// 每帧推进。欠阻尼弹簧：快到目标时减速并轻微回弹。
        ///
        /// 参数用 110 / 10，是实测调出来的：
        ///   原来 26/12 —— 1.38 秒才到位，且峰值 0.994 = 完全没有回弹（又慢又硬）
        ///   现在 110/10 —— 0.19 秒到位，峰值 1.166 = 带 17% 回弹，弹两下就停
        /// 保留了"从下往上弹出来"的过程，但不再让人等。
        /// </summary>
        public bool Step(float dt, float stiffness = 200f, float damping = 13f)
        {
            bool moving = false;

            // 展开进度也用弹簧
            float dv = -stiffness * (_spread - _spreadTarget) - damping * _spreadVel;
            _spreadVel += dv * dt;
            _spread += _spreadVel * dt;
            if (_spread < 0f) { _spread = 0f; _spreadVel = 0f; }
            if (_spread > 1f) { _spread = 1f; _spreadVel = 0f; }
            if (Math.Abs(_spread - _spreadTarget) > 0.001f || Math.Abs(_spreadVel) > 0.001f)
            {
                moving = true;
                RecomputeTargets();
            }

            if (_rows <= 0) return moving;

            for (int i = 0; i < _y.Count; i++)
            {
                float target = _target.Count > i ? _target[i] : 0f;
                float acc = -stiffness * (_y[i] - target) - damping * _vel[i];
                _vel[i] += acc * dt;
                _y[i] += _vel[i] * dt;
                if (Math.Abs(_y[i] - target) > 0.05f || Math.Abs(_vel[i]) > 0.05f) moving = true;
                else { _y[i] = target; _vel[i] = 0f; }
            }
            return moving;
        }

        private readonly List<float> _vel = new List<float>();
        private float _spreadVel;

        private void RecomputeTargets()
        {
            while (_vel.Count < _rows) _vel.Add(0f);

            // 收起时每张只露出 PeekOffset；展开时按 Gap 正常排列
            float collapsed = 0f;
            for (int i = 0; i < _rows; i++)
            {
                float expandedY = i * (CardH + Gap);
                float collapsedY = i * PeekOffset;
                float y = collapsedY + (expandedY - collapsedY) * _spread;
                if (_target.Count > i) _target[i] = y;
                else _target.Add(y);
            }
        }
    }
}