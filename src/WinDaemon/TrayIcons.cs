using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinDaemon
{
    /// <summary>
    /// Draws the tray glyph at runtime from the brand mark's own geometry: two rings of
    /// equal radius overlapped so they share one lens.
    ///
    /// Rendering rather than shipping an .ico keeps it crisp at every DPI and lets the
    /// colour carry connection state, which a static icon could not.
    /// </summary>
    public static class TrayIcons
    {
        // Native coordinate space from the design handoff. Ring centres sit on y = 120,
        // 68 apart, radius 44, stroke 11, tight viewBox of 167 x 99.
        private const float ViewBoxX = 36.5f;
        private const float ViewBoxY = 70.5f;
        private const float ViewBoxW = 167f;
        private const float ViewBoxH = 99f;
        private const float Radius = 44f;
        private const float StrokeWidth = 11f;
        private const float LeftCentreX = 86f;
        private const float RightCentreX = 154f;
        private const float CentreY = 120f;

        private static readonly Color Connected = Color.FromArgb(0x2F, 0x7A, 0x6B);
        private static readonly Color Waiting = Color.FromArgb(0xB0, 0x72, 0x2F);

        public static Icon Create(bool connected) => Render(connected ? Connected : Waiting);

        private static Icon Render(Color color)
        {
            const int size = 32;
            // The mark is much wider than tall, so width is the constraint. Inset slightly
            // so the stroke is not clipped by the icon bounds.
            float scale = (size - 2f) / ViewBoxW;
            float offsetX = (size - ViewBoxW * scale) / 2f;
            float offsetY = (size - ViewBoxH * scale) / 2f;

            RectangleF Ring(float centreX)
            {
                float left = (centreX - Radius - ViewBoxX) * scale + offsetX;
                float top = (CentreY - Radius - ViewBoxY) * scale + offsetY;
                float diameter = Radius * 2f * scale;
                return new RectangleF(left, top, diameter, diameter);
            }

            using var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var leftRing = Ring(LeftCentreX);
                var rightRing = Ring(RightCentreX);

                using var pen = new Pen(color, StrokeWidth * scale);
                g.DrawEllipse(pen, leftRing);
                g.DrawEllipse(pen, rightRing);

                // The lens is exactly where the two discs overlap, so intersecting the
                // regions reproduces it without hand-computing the arc endpoints.
                using var leftDisc = new GraphicsPath();
                leftDisc.AddEllipse(leftRing);
                using var rightDisc = new GraphicsPath();
                rightDisc.AddEllipse(rightRing);

                using var lens = new Region(leftDisc);
                lens.Intersect(rightDisc);

                using var brush = new SolidBrush(color);
                g.FillRegion(brush, lens);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                // Clone so the icon survives destroying the temporary GDI handle.
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool DestroyIcon(IntPtr handle);
        }
    }
}
