using CoreLib.Diagnostics;
using DesktopCore;
using DesktopCore.Ipc;
using DesktopCore.Tray;
using CoreLib.Transport;

namespace LinuxDaemon;

/// <summary>
/// Starts the device and hands over to the shell.
///
/// <para>Diagnostics go through <see cref="Log"/> exactly as they do in the other two apps. The
/// difference is only where the sink points: the Windows daemon has no console to write to, so
/// its sink is a file; this one has a console and writes to both. Direct <c>Console</c> calls in
/// this project are the shell's own output - prompts, tables, command results - which is a user
/// interface rather than a diagnostic.</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        string? dataDirectory = Value(args, "--data");
        string? deviceName = Value(args, "--name");
        int port = TcpTransportConnection.DefaultPort;

        string? portText = Value(args, "--port");
        if (portText != null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            Console.Error.WriteLine($"--port needs a number between 1 and 65535, not \"{portText}\".");
            return 2;
        }

        var paths = new Paths(dataDirectory);
        InstallLogSink(paths, toConsole: !args.Contains("--quiet"));

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            // Handled here rather than letting it kill the process, so links close and the
            // registry is flushed on the way out.
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Stopping.");
            stopping.Cancel();
        };

        using var daemon = new Daemon(paths, port, deviceName);

        try
        {
            await daemon.StartAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("Daemon", "Could not start", ex);
            Console.Error.WriteLine($"Could not start: {ex.Message}");
            return 1;
        }

        // The same bus surface the windowed head publishes. A machine with a panel but no
        // desktop session still wants a widget and a tray icon, and this is the head that runs
        // there. Losing the name to a device already running is expected, not an error.
        using var bus = await MeshBus.TryStartAsync(daemon, quit: stopping.Cancel).ConfigureAwait(false);

        // And a tray icon, on a machine that has a panel but no desktop session to run a window
        // in. This head has never had one; there was nowhere for it to come from until the item
        // stopped being the toolkit's.
        using var tray = await TrayItem.TryStartAsync(daemon, quit: stopping.Cancel).ConfigureAwait(false);

        DesktopCore.Platform.WidgetInstaller.EnsureInstalled(paths.DataDirectory);

        Shell.PrintBanner(daemon);

        if (args.Contains("--no-shell"))
        {
            // Under a service manager there is nobody to take commands from, so the process just
            // holds the links open. An explicit flag rather than sniffing whether stdin is a
            // terminal: piping commands in is exactly how this gets tested, and that looks
            // identical to having no terminal at all.
            Log.Write("Daemon", "Running without the shell.");
            try { await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return 0;
        }

        await Shell.RunAsync(daemon, stopping).ConfigureAwait(false);
        return 0;
    }

    private static void PrintUsage() => Console.WriteLine("""
        meshsyncd - Mesh Sync on Linux, without a window.

          --data <dir>    where the identity, peers and log live
                          (default $XDG_DATA_HOME/MeshSync, or ~/.local/share/MeshSync)
          --port <n>      the port to listen on (default 45001)
          --name <name>   the name announced to peers (default this machine's hostname)
          --quiet         keep the log out of the console; it still goes to the log file
          --no-shell      do not take commands, just hold the links open

        --data and --port together are what let two devices run on one machine, which is how
        the mesh is exercised without a second piece of hardware.
        """);

    /// <summary>Reads <c>--flag value</c>, or null when the flag is absent.</summary>
    private static string? Value(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Points <see cref="Log"/> at the console and a file beside the identity, so the log sits
    /// where the Windows daemon's does relative to its own data.
    /// </summary>
    private static void InstallLogSink(Paths paths, bool toConsole)
    {
        object gate = new();
        string path = paths.LogFile;

        Log.Sink = line =>
        {
            lock (gate)
            {
                if (toConsole) Console.WriteLine(line);

                try { File.AppendAllText(path, line + Environment.NewLine); }
                catch { /* A log that cannot be written must not take the daemon down. */ }
            }
        };

        Log.Write("Daemon", $"Mesh Sync starting. Data in {paths.DataDirectory}.");
    }
}
