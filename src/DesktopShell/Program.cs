using Avalonia;

namespace DesktopShell;

/// <summary>
/// The Linux and Mac head.
///
/// <para>Avalonia rather than MAUI because this is the only shell that can be developed on the
/// machine it runs on and still produce a Mac binary: <c>dotnet publish -r osx-arm64</c> yields a
/// self-contained app including the Cocoa backend. Mac Catalyst cannot be built off a Mac at all,
/// and has no Linux target in any case.</para>
///
/// <para>On Linux this runs through XWayland, because Avalonia 12 has no native Wayland backend.
/// That is fine for the window and for foreground clipboard access, and it is exactly why the
/// background clipboard watcher is a separate component rather than something the toolkit
/// does - see <c>DesktopCore.Clipboard</c>.</para>
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // One device per session. A second launch raises the first one's window and stops,
        // rather than starting a device that cannot bind port 45001 and then sits there looking
        // as though it is working. The diagnostic modes are exempt: they run against their own
        // data directory and are meant to be startable alongside a real instance.
        bool diagnostic = args.Contains("--selftest") || args.Contains("--screenshot");

        if (!diagnostic && DesktopCore.Ipc.MeshBus.TryHandOverAsync().GetAwaiter().GetResult()) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
