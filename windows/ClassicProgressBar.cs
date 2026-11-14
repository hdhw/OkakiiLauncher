using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BubbaBloxLauncher
{
    /// <summary>
    /// The classic launcher bar: a light sunken track with a green block. Marquee (no known
    /// progress) sweeps the block across; SetValue(0..100) fills from the left instead.
    /// </summary>
    public sealed class ClassicProgressBar : Control
    {
        private static readonly Color Track = Color.FromArgb(230, 230, 230);
        private static readonly Color Border = Color.FromArgb(188, 188, 188);
        private static readonly Color GreenTop = Color.FromArgb(18, 138, 18);
        private static readonly Color GreenBottom = Color.FromArgb(6, 112, 6);

        private readonly Timer timer = new Timer { Interval = 16 };
        private float marqueePos;      // block's left edge as a fraction of the track
        private int value = -1;        // -1 = marquee

        public ClassicProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            timer.Tick += (s, e) =>
            {
                marqueePos += 0.012f;
                if (marqueePos > 1f) marqueePos = -0.28f;
                Invalidate();
            };
            timer.Start();
        }

        public void SetValue(int percent)
        {
            value = Math.Max(0, Math.Min(100, percent));
            timer.Stop();
            Invalidate();
        }

        public void SetMarquee()
        {
            value = -1;
            if (!timer.Enabled) timer.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Track)) g.FillRectangle(b, r);

            var inner = Rectangle.Inflate(r, -1, -1);
            Rectangle block;
            if (value >= 0)
            {
                block = new Rectangle(inner.X, inner.Y, (int)(inner.Width * value / 100f), inner.Height + 1);
            }
            else
            {
                int w = (int)(inner.Width * 0.28f);
                int x = inner.X + (int)(inner.Width * marqueePos);
                block = Rectangle.Intersect(new Rectangle(x, inner.Y, w, inner.Height + 1), new Rectangle(inner.X, inner.Y, inner.Width, inner.Height + 1));
            }
            if (block.Width > 0)
                using (var gb = new LinearGradientBrush(new Rectangle(0, inner.Y, 1, inner.Height + 1), GreenTop, GreenBottom, LinearGradientMode.Vertical))
                    g.FillRectangle(gb, block);

            using (var p = new Pen(Border)) g.DrawRectangle(p, r);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
