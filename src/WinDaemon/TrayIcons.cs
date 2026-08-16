using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinDaemon
{
    /// <summary>
    /// Draws the tray glyph at runtime - two linked nodes, matching the in-app mark.
    /// Rendering it rather than shipping an .ico keeps it crisp at every DPI and lets the
    /// colour reflect connection state, which a static SystemIcons.Shield could not.
    /// </summary>
    public static class TrayIcons
    {
        private static readonly Color Connected = Color.FromArgb(0x2F, 0x7A, 0x6B);
        private static readonly Color Waiting = Color.FromArgb(0xB0, 0x72, 0x2F);

        public static Icon Create(bool connected) => Render(connected ? Connected : Waiting, connected);

        private static Icon Render(Color color, bool filled)
        {
            const int size = 32;
            using var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var pen = new Pen(color, 3f);
                using var brush = new SolidBrush(color);

                // Left node - always solid.
                g.FillEllipse(brush, 3, 11, 10, 10);

                // Link.
                g.DrawLine(pen, 13, 16, 19, 16);

                // Right node - solid once a peer is connected, outlined while waiting.
                if (filled) g.FillEllipse(brush, 19, 11, 10, 10);
                else g.DrawEllipse(pen, 20, 12, 8, 8);
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
