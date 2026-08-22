using System.Diagnostics;
using System.Text;
using CoreLib.Diagnostics;

namespace DesktopCore.Clipboard;

/// <summary>
/// Notices that the local clipboard changed, and says what it changed to.
///
/// <para><b>Two mechanisms, deliberately.</b> <c>wl-paste --watch</c> is told by the compositor
/// when the selection changes, which is the right shape: no polling, and no work at all while
/// nothing is being copied. Everything else is polled, because neither <c>xclip</c> nor
/// <c>xsel</c> has a watch mode and a poll is the only thing left.</para>
///
/// <para>The watch is framed with a NUL after each change. <c>wl-paste --watch cat</c> alone
/// writes one clipboard entry straight after another with nothing between them, so two copies
/// in quick succession arrive as one payload with no way to tell where the first ended.</para>
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly IClipboardBridge _clipboard;
    private readonly TimeSpan _pollInterval;
    private Process? _watch;
    private bool _disposed;

    /// <summary>Raised with the new clipboard text. Never raised with an empty string.</summary>
    public event Func<string, Task>? TextChanged;

    public ClipboardWatcher(IClipboardBridge clipboard, TimeSpan? pollInterval = null)
    {
        _clipboard = clipboard;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_clipboard.IsAvailable) return Task.CompletedTask;

        // The Wayland bridge is already listening - the compositor tells it when the selection
        // changes - so there is nothing here to drive. Subscribing is the whole job.
        if (_clipboard is WaylandClipboard wayland)
        {
            wayland.SelectionChanged += text => _ = RaiseAsync(text);
            return Task.CompletedTask;
        }

        return _clipboard.SupportsWatching
            ? WatchAsync(cancellationToken)
            : PollAsync(cancellationToken);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        // Restarted rather than abandoned if it dies: the compositor going away and coming
        // back is a normal thing on a desktop and should not silently end clipboard sync.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var info = new ProcessStartInfo("wl-paste")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                info.ArgumentList.Add("--watch");
                info.ArgumentList.Add("sh");
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("cat; printf '\\0'");

                _watch = Process.Start(info);
                if (_watch == null)
                {
                    Log.Write("Clipboard", "Could not start wl-paste --watch; falling back to polling.");
                    await PollAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                Log.Write("Clipboard", "Watching the clipboard through wl-paste.");
                await ReadFramesAsync(_watch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "The clipboard watch failed", ex);
            }

            if (cancellationToken.IsCancellationRequested) return;

            Log.Write("Clipboard", "The clipboard watch ended; restarting it in 2s.");
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads NUL-delimited clipboard entries off the watch process.</summary>
    private async Task ReadFramesAsync(Process watch, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var chunk = new char[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await watch.StandardOutput.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return; // The watch exited.

            for (int i = 0; i < read; i++)
            {
                if (chunk[i] != '\0') { buffer.Append(chunk[i]); continue; }

                string text = buffer.ToString();
                buffer.Clear();
                if (text.Length > 0) await RaiseAsync(text).ConfigureAwait(false);
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        Log.Write("Clipboard", $"Polling the clipboard every {_pollInterval.TotalSeconds:F0}s through {_clipboard.Name}.");

        string? last = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? text = await _clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);

                // Compared against the last value rather than sent every tick. The echo
                // suppressor would catch a repeat anyway, but there is no reason to hand it
                // the same string once a second.
                if (text != null && text != last)
                {
                    last = text;
                    await RaiseAsync(text).ConfigureAwait(false);
                }
                else if (text == null)
                {
                    last = null;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Reading the clipboard failed", ex);
            }

            try { await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RaiseAsync(string text)
    {
        var handler = TextChanged;
        if (handler == null) return;

        try { await handler(text).ConfigureAwait(false); }
        catch (Exception ex) { Log.Write("Clipboard", "A clipboard handler threw", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TextChanged = null;

        try
        {
            if (_watch is { HasExited: false }) _watch.Kill(entireProcessTree: true);
            _watch?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", "Stopping the clipboard watch failed", ex);
        }
    }
}
