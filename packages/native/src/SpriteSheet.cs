using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace HajimiPet
{
    /// <summary>
    /// 从 Codex 规格的 sprite atlas 取帧。
    ///
    /// 契约（与 codex pet.json 一致）：
    ///   整图 1536×1872，8 列 × 9 行，每格 192×208，透明背景。
    ///   行 0 idle / 1 running-right / 2 running-left / 3 waving / 4 jumping
    ///      5 failed / 6 waiting / 7 running / 8 review
    ///
    /// 每行用到第几帧是固定的（见 FrameCount），最后一帧时长更长。
    /// 找不到 atlas 时上层会退回原来的分层渲染，所以老素材仍然能用。
    /// </summary>
    internal sealed class SpriteSheet
    {
        public const int CellW = 192;
        public const int CellH = 208;
        public const int Cols = 8;
        public const int Rows = 9;
        public const int SheetW = CellW * Cols;   // 1536
        public const int SheetH = CellH * Rows;   // 1872

        public enum Row
        {
            Idle = 0,
            RunningRight = 1,
            RunningLeft = 2,
            Waving = 3,
            Jumping = 4,
            Failed = 5,
            Waiting = 6,
            Running = 7,
            Review = 8,
        }

        // 每行实有多少帧（取自 hatch-pet 契约）
        private static readonly int[] FrameCount = { 6, 8, 8, 4, 5, 8, 6, 6, 6 };

        // 每行首帧之后的帧间隔（毫秒）；最后一帧用 LongerMs
        private static readonly int[] FrameMs = { 110, 120, 120, 140, 140, 140, 150, 120, 150 };
        private static readonly int[] FirstMs = { 280, 120, 120, 140, 140, 140, 150, 120, 150 };
        private static readonly int[] LastMs = { 320, 220, 220, 280, 280, 240, 260, 220, 280 };

        private readonly Bitmap _sheet;
        private readonly Rectangle[] _clips = new Rectangle[Rows * Cols];

        public int Width { get { return _sheet.Width; } }
        public int Height { get { return _sheet.Height; } }

        private SpriteSheet(Bitmap sheet)
        {
            _sheet = sheet;
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    _clips[r * Cols + c] = new Rectangle(c * CellW, r * CellH, CellW, CellH);
                }
            }
        }

        public static int FramesIn(Row row) { return FrameCount[(int)row]; }

        /// <summary>某一帧停留多久（毫秒）。</summary>
        public static int DurationFor(Row row, int frame)
        {
            int n = FrameCount[(int)row];
            if (n <= 1) return FirstMs[(int)row];
            if (frame <= 0) return FirstMs[(int)row];
            if (frame >= n - 1) return LastMs[(int)row];
            return FrameMs[(int)row];
        }

        /// <summary>按已播放毫秒数算当前帧号。</summary>
        public static int FrameAt(Row row, long elapsedMs)
        {
            int n = FrameCount[(int)row];
            if (n <= 0) return 0;
            int total = 0;
            for (int i = 0; i < n; i++) total += DurationFor(row, i);
            if (total <= 0) return 0;
            long t = elapsedMs % total;
            int acc = 0;
            for (int i = 0; i < n; i++)
            {
                acc += DurationFor(row, i);
                if (t < acc) return i;
            }
            return n - 1;
        }

        /// <summary>从安装目录读 atlas；没有就返回 null。</summary>
        public static SpriteSheet LoadFromPetDir(string petDir)
        {
            if (string.IsNullOrEmpty(petDir)) return null;
            string manifestPath = Path.Combine(petDir, "pet.json");
            if (!File.Exists(manifestPath)) return null;

            string sheetName = "spritesheet.webp";
            try
            {
                string json = File.ReadAllText(manifestPath);
                int i = json.IndexOf("\"spritesheetPath\"", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int q1 = json.IndexOf('"', json.IndexOf(':', i) + 1);
                    int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
                    if (q1 > 0 && q2 > q1) sheetName = json.Substring(q1 + 1, q2 - q1 - 1);
                }
            }
            catch
            {
                /* 用默认文件名 */
            }

            string sheetPath = Path.Combine(petDir, sheetName);

            // GDI+（.NET Framework）解不了 WebP，所以优先用同名 PNG；
            // 没有 PNG 才回落到 manifest 指的文件（老版本 .NET 会静默失败）。
            string pngSibling = Path.ChangeExtension(sheetPath, ".png");
            if (File.Exists(pngSibling))
            {
                sheetPath = pngSibling;
            }
            else if (!File.Exists(sheetPath))
            {
                return null;
            }

            try
            {
                using (var raw = new Bitmap(sheetPath))
                {
                    // 必须真的把像素拷进来：new Bitmap(w,h,format) 是空图
                    var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(raw, new Rectangle(0, 0, raw.Width, raw.Height));
                    }

                    if (copy.Width != SheetW || copy.Height != SheetH)
                    {
                        // 尺寸不符就不认，避免画出错位的图
                        copy.Dispose();
                        return null;
                    }
                    return new SpriteSheet(copy);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把某一帧画进目标矩形（等比缩放，脚底对齐）。</summary>
        public void DrawFrame(Graphics g, Row row, int frame, Rectangle dest)
        {
            int r = Math.Max(0, Math.Min(Rows - 1, (int)row));
            int c = Math.Max(0, Math.Min(FramesIn(row) - 1, frame));
            Rectangle src = _clips[r * Cols + c];

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(_sheet, dest, src, GraphicsUnit.Pixel);
        }
    }
}