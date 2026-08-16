using System;
using System.Diagnostics;

namespace CoreLib.Diagnostics
{
    /// <summary>
    /// Minimal pluggable log sink.
    /// WinDaemon is a WinExe with no console attached, so <c>Console.WriteLine</c> diagnostics
    /// were being written into the void. Host apps install a real sink at startup
    /// (file, logcat, Debug output) via <see cref="Sink"/>.
    /// </summary>
    public static class Log
    {
        /// <summary>Installed by the host app. Receives "[HH:mm:ss.fff] [Tag] message".</summary>
        public static Action<string>? Sink;

        public static void Write(string tag, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}";
            try
            {
                Sink?.Invoke(line);
            }
            catch
            {
                // A broken sink must never take the caller down.
            }

            Debug.WriteLine(line);
        }

        public static void Write(string tag, string message, Exception ex)
            => Write(tag, $"{message}: {ex.GetType().Name}: {ex.Message}");
    }
}
