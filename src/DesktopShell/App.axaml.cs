using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CoreLib.Diagnostics;
using DesktopCore;
using DesktopCore.Ipc;
using DesktopCore.Tray;

namespace DesktopShell;

public partial class App : Application
{
    private Daemon? _daemon;
    private MeshBus? _bus;
    private TrayItem? _tray;
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

            // Only where our own StatusNotifierItem cannot go. On Linux TrayItem owns the icon;
            // building both would put two identical icons in the panel.
            if (!OperatingSystem.IsLinux()) BuildTray();

            // Logged, because a quit that leaves no line is a quit nobody can explain afterwards.
            // This one fires when the session manager asks, which is not the same as the tray's
            // Quit and used to be indistinguishable from it in the log.
            desktop.ShutdownRequested += (_, _) =>
            {
                Log.Write("Shell", "The session asked Mesh Sync to stop.");
                Shutdown();
            };
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

            // After the daemon, not before: the first thing a client does is read properties,
            // and answering them from a device that has not started yet would report a mesh with
            // no listener as though that were its resting state.
            _bus = await MeshBus.TryStartAsync(_daemon, ShowPage, QuitFromBus).ConfigureAwait(false);

            // The tray item is ours rather than Avalonia's, so it can carry a themed icon, a
            // tooltip, and a NeedsAttention state when a device is asking to join. On macOS
            // there is no StatusNotifier host and this returns null, which is why the Avalonia
            // one is still built there.
            _tray = await TrayItem.TryStartAsync(_daemon, ShowPage, QuitFromBus).ConfigureAwait(false);

            if (_tray == null && OperatingSystem.IsLinux())
                Log.Write("Shell", "No status area took the tray icon; the window is the only way in.");

            // The widget ships with the app but Plasma reads it from a fixed place, and an
            // AppImage cannot put anything there. Once, quietly, and never on a desktop that
            // has no use for it.
            DesktopCore.Platform.WidgetInstaller.EnsureInstalled(_daemon.DataDirectory);
        }
        catch (Exception ex)
        {
            Log.Write("Shell", "The daemon could not start", ex);
        }
    }

    /// <summary>
    /// Raises the window on a named page, for anything holding the bus.
    ///
    /// <para>The widget's "Open Mesh Sync" comes through here, and so will a second launch of the
    /// app once it hands over rather than racing for the port. Names rather than indices, because
    /// an index is exactly the kind of thing that goes wrong quietly - the Home page's pairing
    /// button spent its life opening Notifications for precisely that reason.</para>
    /// </summary>
    private void ShowPage(string page)
    {
        int view = page.Trim().ToLowerInvariant() switch
        {
            "activity" => 1,
            "notifications" => 2,
            "files" => 3,
            "devices" => 4,
            "settings" => 5,
            "about" => 6,
            _ => 0,
        };

        Dispatcher.UIThread.Post(() =>
        {
            ShowWindow();
            _window?.SelectView(view);
        });
    }

    private void QuitFromBus() => Dispatcher.UIThread.Post(() =>
    {
        Log.Write("Shell", "Quit was asked for over the bus.");
        Shutdown();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    });

    /// <summary>
    /// The macOS tray icon, an NSStatusItem by way of Avalonia.
    ///
    /// <para>Linux does not come through here any more: <c>DesktopCore.Tray.TrayItem</c> owns the
    /// StatusNotifierItem there, because Avalonia's cannot express an icon name, a tooltip or an
    /// attention state. macOS has no StatusNotifierWatcher, so this stays for the Mac head.</para>
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
        if (_daemon != null) Log.Write("Shell", "Mesh Sync is stopping.");

        try { _stopping?.Cancel(); } catch { }

        // Before the daemon: the bus is subscribed to the daemon's events, and a disposed daemon
        // raising one into a disposed connection is a fault on a thread nobody is watching.
        _bus?.Dispose();
        _bus = null;

        _tray?.Dispose();
        _tray = null;

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
