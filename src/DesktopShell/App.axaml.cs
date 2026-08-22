using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CoreLib.Diagnostics;
using DesktopCore;

namespace DesktopShell;

public partial class App : Application
{
    private Daemon? _daemon;
    private MainWindow? _window;
    private CancellationTokenSource? _stopping;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window must not stop syncing - the whole point of the app is that it
            // keeps holding links while you are doing something else. Quit is deliberate, from
            // the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var paths = new Paths();
            InstallLogSink(paths);

            _stopping = new CancellationTokenSource();
            _daemon = new Daemon(paths);

            _window = new MainWindow();
            _window.Attach(_daemon);
            desktop.MainWindow = _window;
            _window.Show();

            _ = StartDaemonAsync();

            // Renders the window to a file and exits. Used to check the layout without
            // photographing the desktop, which would capture whatever else is on screen.
            if (desktop.Args?.Contains("--selftest") == true)
            {
                _ = SelfTestAsync(desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            string? shot = Argument(desktop.Args, "--screenshot");
            if (shot != null)
            {
                _ = CaptureAsync(shot, int.TryParse(Argument(desktop.Args, "--view"), out int v) ? v : 0, desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            BuildTray();

            desktop.ShutdownRequested += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task SelfTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await Task.Delay(2500);
            Console.WriteLine();
            Console.WriteLine("Sidebar and pages");
            Console.Write(_window!.SelfTest());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  self-test threw: {ex}");
        }
        finally
        {
            Shutdown();
            desktop.Shutdown();
        }
    }

    private static string? Argument(string[]? args, string flag)
    {
        if (args == null) return null;

        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Draws the window into a bitmap and writes it out. A layout pass has to have happened
    /// first, so this waits for the window to settle rather than rendering immediately.
    /// </summary>
    private async Task CaptureAsync(string path, int view, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // Long enough for the first dial to finish, which takes seconds and is the
            // difference between a screenshot of a connected mesh and one of a connecting one.
            _window!.SelectView(view);
            await Task.Delay(9000);
            _window.SelectView(view);
            await Task.Delay(1200);

            var size = new Avalonia.PixelSize((int)_window.Bounds.Width, (int)_window.Bounds.Height);
            if (size.Width <= 0 || size.Height <= 0) size = new Avalonia.PixelSize(880, 600);

            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
            bitmap.Render(_window);
            bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

            Log.Write("Shell", $"Wrote {path}.");
        }
        catch (Exception ex)
        {
            Log.Write("Shell", "Could not capture the window", ex);
        }
        finally
        {
            Shutdown();
            desktop.Shutdown();
        }
    }

    private async Task StartDaemonAsync()
    {
        try
        {
            await _daemon!.StartAsync(_stopping!.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("Shell", "The daemon could not start", ex);
        }
    }

    /// <summary>
    /// The tray icon, which on Linux is a StatusNotifierItem over D-Bus and on macOS is an
    /// NSStatusItem. Avalonia hides the difference; what it cannot hide is that a desktop with
    /// no status area shows nothing at all, which is why quitting is also possible from the
    /// window itself.
    /// </summary>
    private void BuildTray()
    {
        try
        {
            var show = new NativeMenuItem("Show Mesh Sync");
            show.Click += (_, _) => ShowWindow();

            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) =>
            {
                Shutdown();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
            };

            var icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://meshsync/Assets/meshsync.png"))),
                ToolTipText = "Mesh Sync",
                IsVisible = true,
                Menu = new NativeMenu { Items = { show, quit } },
            };

            icon.Clicked += (_, _) => ShowWindow();

            TrayIcon.SetIcons(this, new TrayIcons { icon });
        }
        catch (Exception ex)
        {
            // A missing status area is not a reason to fail to start.
            Log.Write("Shell", "Could not create the tray icon", ex);
        }
    }

    private void ShowWindow()
    {
        if (_window == null) return;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Shutdown()
    {
        try { _stopping?.Cancel(); } catch { }
        _daemon?.Dispose();
        _daemon = null;
    }

    private static void InstallLogSink(Paths paths)
    {
        object gate = new();
        string path = paths.LogFile;

        Log.Sink = line =>
        {
            lock (gate)
            {
                try { File.AppendAllText(path, line + Environment.NewLine); }
                catch { /* A log that cannot be written must not take the app down. */ }
            }
        };

        Log.Write("Shell", $"Mesh Sync starting. Data in {paths.DataDirectory}.");
    }
}
