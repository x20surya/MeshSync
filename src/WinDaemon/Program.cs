using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinDaemon
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("Starting Password Manager & Clipboard Sync Daemon...");
            Console.WriteLine("Listening for clipboard changes. Press Ctrl+C to exit.");

            // Create and run the hidden message window
            using var listener = new ClipboardListenerWindow();
            Application.Run();
        }
    }

    class ClipboardListenerWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        public ClipboardListenerWindow()
        {
            // Create a message-only window
            CreateHandle(new CreateParams
            {
                Caption = "ClipboardListener",
                // HWND_MESSAGE
                Parent = new IntPtr(-3)
            });

            // Register this window to receive WM_CLIPBOARDUPDATE messages
            if (!AddClipboardFormatListener(Handle))
            {
                Console.WriteLine($"Failed to register clipboard listener. Error: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                Console.WriteLine("Successfully registered clipboard format listener.");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                OnClipboardUpdate();
            }

            base.WndProc(ref m);
        }

        private void OnClipboardUpdate()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard updated!");
            
            try
            {
                // It's often necessary to give the clipboard a moment to be released by the source app
                System.Threading.Thread.Sleep(50);
                
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    string preview = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                    Console.WriteLine($"    Text: {preview}");
                }
                else if (Clipboard.ContainsImage())
                {
                    Console.WriteLine("    Image copied.");
                }
                else
                {
                    Console.WriteLine("    Other data format copied.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Error accessing clipboard: {ex.Message}");
            }
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
