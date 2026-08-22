using CoreLib.Diagnostics;
using DesktopCore;
using CoreLib.Identity;
using CoreLib.Transport;

namespace LinuxDaemon;

/// <summary>
/// The interactive console.
///
/// <para>Everything printed here is user interface rather than diagnostics, which is why it goes
/// to <c>Console</c> directly. Anything worth keeping goes through <see cref="Log"/> and lands
/// in the log file as well.</para>
///
/// <para><c>send</c> exists so the transport can be exercised on a machine with no clipboard
/// helper installed at all. That matters more than it sounds: it is what lets this daemon prove
/// CoreLib against a real phone before anything else on the desktop side is built.</para>
/// </summary>
public static class Shell
{
    public static void PrintBanner(Daemon daemon)
    {
        var security = daemon.Security;

        Console.WriteLine();
        Console.WriteLine("  Mesh Sync - Linux daemon");
        Console.WriteLine($"  Device     {daemon.DeviceName}");
        Console.WriteLine($"  Identity   {security.Identity.ShortFingerprint}");
        Console.WriteLine($"  Mesh       {security.Peers.MeshNameOrDefault}");
        Console.WriteLine($"  Listening  {NetworkUtil.GetLocalLanAddress() ?? "no LAN address"}:{daemon.Port}");
        Console.WriteLine($"  Clipboard  {ClipboardLine(daemon)}");
        Console.WriteLine($"  Data       {daemon.DataDirectory}");
        Console.WriteLine();

        PrintPeers(daemon);

        if (security.Peers.IsEmpty)
        {
            Console.WriteLine("  No paired devices yet. Type `pair` to show a code for your phone to scan.");
            Console.WriteLine();
        }

        Console.WriteLine("  Type `help` for commands.");
        Console.WriteLine();
    }

    private static string ClipboardLine(Daemon daemon)
    {
        var bridge = daemon.ClipboardBridge;

        if (!bridge.IsAvailable)
        {
            return "off - no helper installed (sudo apt install wl-clipboard). `send` still works.";
        }

        return bridge.SupportsWatching
            ? $"{bridge.Name}, watched"
            : $"{bridge.Name}, polled";
    }

    public static async Task RunAsync(Daemon daemon, CancellationTokenSource stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Console.Write("> ");

            string? line;
            try { line = await Console.In.ReadLineAsync(stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (line == null) return;             // stdin closed
            line = line.Trim();
            if (line.Length == 0) continue;

            // A terminal echoes what was typed; a pipe does not, so the prompt and the first
            // line of output end up on the same line and anything reading the transcript back
            // sees "> " glued to the front of it. Echoing here keeps a scripted run readable
            // and is what made this testable at all.
            if (Console.IsInputRedirected) Console.WriteLine(line);

            string command = line.Split(' ', 2)[0].ToLowerInvariant();
            string rest = line.Length > command.Length ? line[(command.Length + 1)..].Trim() : "";

            try
            {
                if (await DispatchAsync(daemon, command, rest, stopping).ConfigureAwait(false)) return;
            }
            catch (Exception ex)
            {
                // A bad command must never take the daemon down with it.
                Log.Write("Shell", $"`{command}` failed", ex);
                Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Returns true when the shell should exit.</summary>
    private static async Task<bool> DispatchAsync(Daemon daemon, string command, string rest,
                                                  CancellationTokenSource stopping)
    {
        switch (command)
        {
            case "help" or "?":
                PrintHelp();
                return false;

            case "status":
                PrintBanner(daemon);
                return false;

            case "peers":
                PrintPeers(daemon);
                return false;

            case "pair":
                ShowPairingCode(daemon);
                return false;

            case "join":
                if (rest.Length == 0) Console.WriteLine("  Usage: join <meshsync:// code from the other device>");
                else Report(daemon.Join(rest));
                return false;

            case "confirm":
                Report(daemon.Confirm(rest));
                return false;

            case "reject":
                Report(daemon.Reject(rest));
                return false;

            case "forget":
                Forget(daemon, rest);
                return false;

            case "bt":
                await ShowBluetoothAsync().ConfigureAwait(false);
                return false;

            case "clip":
                {
                    string? text = await daemon.ClipboardBridge.GetTextAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    Console.WriteLine(text == null ? "  (the clipboard is empty, or unreachable)" : $"  {text}");
                }
                return false;

            case "clipset":
                {
                    bool ok = await daemon.ClipboardBridge.SetTextAsync(rest, CancellationToken.None)
                        .ConfigureAwait(false);
                    Console.WriteLine(ok ? "  On the clipboard." : "  Could not reach the clipboard.");
                }
                return false;

            case "send":
                await SendAsync(daemon, rest).ConfigureAwait(false);
                return false;

            case "ring":
                await RingAsync(daemon, rest, on: true).ConfigureAwait(false);
                return false;

            case "unring":
                await RingAsync(daemon, rest, on: false).ConfigureAwait(false);
                return false;

            case "name":
                SetMeshName(daemon, rest);
                return false;

            case "uri":
                Console.WriteLine($"  {daemon.PairingUri}");
                return false;

            case "quit" or "exit":
                stopping.Cancel();
                return true;

            default:
                Console.WriteLine($"  Unknown command `{command}`. Type `help`.");
                return false;
        }
    }

    private static async Task ShowBluetoothAsync()
    {
        using var bluez = await DesktopCore.Bluetooth.BlueZ.TryConnectAsync().ConfigureAwait(false);

        if (bluez == null) { Console.WriteLine("  No BlueZ on this machine."); return; }

        var (present, canAdvertise, path, detail) =
            await DesktopCore.Bluetooth.BlueZCapability.ProbeAsync(bluez).ConfigureAwait(false);

        Console.WriteLine($"  adapter       {path ?? "none"}");
        Console.WriteLine($"  usable        {present}");
        Console.WriteLine($"  can advertise {canAdvertise}");
        Console.WriteLine($"  {detail}");

        var objects = await bluez.GetObjectsAsync().ConfigureAwait(false);
        int devices = objects.Count(o => o.Has(DesktopCore.Bluetooth.BlueZ.DeviceInterface));
        Console.WriteLine($"  known devices {devices}, {objects.Count} BlueZ objects in total");

        // Narrowing down which message bodies the bus accepts.
        try
        {
            var powered = await bluez.GetPropertyAsync(path!, DesktopCore.Bluetooth.BlueZ.AdapterInterface, "Powered")
                .ConfigureAwait(false);
            Console.WriteLine($"  two-string body (Properties.Get): OK, Powered={powered.GetBool()}");
        }
        catch (Exception ex) { Console.WriteLine($"  two-string body (Properties.Get): FAILED {ex.GetType().Name}"); }

        try
        {
            await bluez.CallAsync(path!, DesktopCore.Bluetooth.BlueZ.AdapterInterface, "SetDiscoveryFilter", "a{sv}",
                (ref Tmds.DBus.Protocol.MessageWriter w) => { var d = w.WriteArrayStart(Tmds.DBus.Protocol.DBusType.DictEntry); w.WriteArrayEnd(d); })
                .ConfigureAwait(false);
            Console.WriteLine("  empty a{sv} body: OK");
        }
        catch (Exception ex) { Console.WriteLine($"  empty a{{sv}} body: FAILED {ex.GetType().Name}"); }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
              status              this device, its mesh and its peers
              peers               paired devices and which are connected
              pair                open the pairing window and show a code to scan
              uri                 print the pairing URI without the QR code
              join <code>         join a mesh from another device's meshsync:// code
              confirm <prefix>    accept a device that is waiting, by fingerprint prefix
              reject  <prefix>    turn one away
              forget  <prefix>    unpair a device, which costs a re-pair to undo
              bt                  what this machine's Bluetooth radio can do
              clip                print what is on this machine's clipboard
              clipset <text>      put text on this machine's clipboard
              send <text>         send text to every connected device
              ring   <prefix>     make a device sound an alarm
              unring <prefix>     stop it
              name <mesh name>    name this mesh, if it has none
              quit                close the links and exit

            Flags: --quiet (no log on the console), --no-shell (for a service manager)
            """);
    }

    private static void PrintPeers(Daemon daemon)
    {
        var peers = daemon.Security.Peers.Peers;

        if (peers.Count == 0)
        {
            Console.WriteLine("  No paired devices.");
            return;
        }

        Console.WriteLine($"  Paired devices ({peers.Count})");

        foreach (var peer in peers)
        {
            bool connected = daemon.Mesh.IsConnectedTo(peer.Fingerprint);
            string name = daemon.Mesh.NameOf(peer.Fingerprint) ?? peer.Name ?? "unnamed";
            string seen = Ago(peer.LastSeenUtc);

            Console.WriteLine($"    {(connected ? "*" : " ")} {DeviceIdentity.Shorten(peer.Fingerprint)}  " +
                              $"{name,-20}  {peer.LastAddress ?? "no address",-15}  {seen}");
        }

        Console.WriteLine(daemon.Mesh.ConnectedCount > 0
            ? $"  * connected ({daemon.Mesh.ConnectedCount} of {peers.Count})"
            : "  none connected");
        Console.WriteLine();
    }

    private static void ShowPairingCode(Daemon daemon)
    {
        daemon.Security.Pairing.Open();

        string uri = daemon.PairingUri;

        Console.WriteLine();
        string? qr = TerminalQr.Render(uri);
        if (qr != null) Console.Write(qr);
        else Console.WriteLine("  (terminal too narrow for the QR code)");

        Console.WriteLine();
        Console.WriteLine($"  {uri}");
        Console.WriteLine();
        Console.WriteLine($"  Scan it from the phone. Open for {daemon.Security.Pairing.Remaining.TotalMinutes:F0} more minutes.");
        Console.WriteLine("  This device will refuse it once and ask you to compare fingerprints - that is by design.");
        Console.WriteLine();
    }

    private static void Forget(Daemon daemon, string prefix)
    {
        var peer = daemon.FindPeer(prefix);
        if (peer == null)
        {
            Console.WriteLine("  No single paired device matches that. Try `peers`.");
            return;
        }

        Console.WriteLine(daemon.Security.Peers.Forget(peer.Fingerprint)
            ? $"  Forgot {DeviceIdentity.Shorten(peer.Fingerprint)}. Pairing again means scanning a code again."
            : "  It was not paired.");
    }

    private static async Task SendAsync(Daemon daemon, string text)
    {
        if (text.Length == 0)
        {
            Console.WriteLine("  Nothing to send. Usage: send <text>");
            return;
        }

        int sent = await daemon.SendTextAsync(text).ConfigureAwait(false);

        Console.WriteLine(sent > 0
            ? $"  Sent to {sent} device(s)."
            : "  Nothing connected, so nothing was sent - and nothing is queued anywhere.");
    }

    private static async Task RingAsync(Daemon daemon, string prefix, bool on)
    {
        var peer = daemon.FindPeer(prefix);
        if (peer == null)
        {
            Console.WriteLine("  No single paired device matches that. Try `peers`.");
            return;
        }

        bool ok = await daemon.RingAsync(peer.Fingerprint, on).ConfigureAwait(false);

        Console.WriteLine(ok
            ? $"  Asked {DeviceIdentity.Shorten(peer.Fingerprint)} to {(on ? "ring" : "stop")}."
            : "  That device is not connected.");
    }

    private static void SetMeshName(Daemon daemon, string name)
    {
        if (name.Length == 0)
        {
            Console.WriteLine($"  This mesh is called \"{daemon.Security.Peers.MeshNameOrDefault}\".");
            return;
        }

        daemon.Security.Peers.MeshName = name;
        Console.WriteLine($"  This mesh is now called \"{daemon.Security.Peers.MeshName}\".");
        Console.WriteLine("  Renaming travels to devices that join later, not to ones already paired.");
    }

    private static void Report((bool Ok, string Message) result) =>
        Console.WriteLine($"  {result.Message}");

    private static string Ago(DateTimeOffset when)
    {
        if (when == default) return "never seen";

        var span = DateTimeOffset.UtcNow - when;

        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{span.TotalMinutes:F0}m ago";
        if (span < TimeSpan.FromDays(1)) return $"{span.TotalHours:F0}h ago";
        return $"{span.TotalDays:F0}d ago";
    }
}
