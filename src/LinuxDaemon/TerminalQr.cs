using System.Text;
using QRCoder;

namespace LinuxDaemon;

/// <summary>
/// Draws a pairing QR code into the terminal.
///
/// <para><b>Why half blocks.</b> The pairing payload carries a whole P-256 public key, so the
/// code lands around 50 modules square. Two characters per module - the usual way to keep a
/// terminal QR square, since cells are twice as tall as they are wide - would be over a hundred
/// columns and wrap on a default terminal, and a wrapped QR does not scan. U+2580 UPPER HALF
/// BLOCK carries two module rows in one cell, so the code is one character per module wide and
/// half as many lines tall, which fits.</para>
///
/// <para>The foreground paints the upper module and the background the lower one, so every cell
/// is the same character and only the colours change.</para>
/// </summary>
public static class TerminalQr
{
    private const string Esc = "\u001b";
    private const string Reset = Esc + "[0m";
    private const string UpperHalf = "▀";

    // 231 and 16 are white and black in the 256-colour cube. Used rather than the basic 30-37
    // range because a terminal theme is free to redefine those, and a QR code rendered in
    // someone's accent colours does not scan.
    private static string Foreground(bool dark) => dark ? Esc + "[38;5;16m" : Esc + "[38;5;231m";

    private static string Background(bool dark) => dark ? Esc + "[48;5;16m" : Esc + "[48;5;231m";

    /// <summary>
    /// Renders <paramref name="payload"/> as a scannable block of text, or null if it will not
    /// fit the terminal - in which case the caller should fall back to printing the URI, because
    /// a QR that has wrapped is worse than no QR at all.
    /// </summary>
    public static string? Render(string payload, int? terminalWidth = null)
    {
        QRCodeData data;
        try
        {
            using var generator = new QRCodeGenerator();
            // Q corrects a quarter of the code, which is what makes it survive being scanned off
            // a screen at an angle. L would be smaller and is not worth the retries.
            data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        }
        catch
        {
            return null;
        }

        using (data)
        {
            var matrix = data.ModuleMatrix;
            int size = matrix.Count;
            if (size == 0) return null;

            int width = terminalWidth ?? SafeWindowWidth();
            if (size > width) return null;

            var sb = new StringBuilder(size * size / 2 + size * 24);

            // Two module rows per line. An odd final row pairs with light, which just extends the
            // quiet zone by one module and is what a scanner wants anyway.
            for (int row = 0; row < size; row += 2)
            {
                var top = matrix[row];
                var bottom = row + 1 < size ? matrix[row + 1] : null;

                bool currentFg = false, currentBg = false, started = false;

                for (int col = 0; col < size; col++)
                {
                    bool topDark = top[col];
                    bool bottomDark = bottom?[col] ?? false;

                    // Escape sequences are emitted only when a colour actually changes, which
                    // keeps a 50-module code to a few hundred bytes a line instead of a few
                    // thousand.
                    if (!started || topDark != currentFg)
                    {
                        sb.Append(Foreground(topDark));
                        currentFg = topDark;
                    }

                    if (!started || bottomDark != currentBg)
                    {
                        sb.Append(Background(bottomDark));
                        currentBg = bottomDark;
                    }

                    started = true;
                    sb.Append(UpperHalf);
                }

                sb.Append(Reset).Append('\n');
            }

            return sb.ToString();
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            // Zero when output is redirected, which is not a width worth comparing against.
            int width = Console.WindowWidth;
            return width > 0 ? width : 80;
        }
        catch
        {
            return 80;
        }
    }
}
