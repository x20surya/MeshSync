using System.Diagnostics;
using System.Text;
using CoreLib.Diagnostics;

namespace DesktopCore.Clipboard;

/// <summary>Running a short-lived helper and getting its output back, without hanging on it.</summary>
internal static class Proc
{
    /// <summary>Whether a command exists on PATH.</summary>
    public static bool Exists(string command)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("/usr/bin/env", $"which {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (probe == null) return false;
            probe.WaitForExit(3000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a command and returns stdout, or null if it failed.
    ///
    /// <paramref name="stdin"/> is written and the stream closed, which is what tells
    /// <c>wl-copy</c> and <c>xclip</c> that the content is complete.
    /// </summary>
    public static async Task<string?> RunAsync(string file, IEnumerable<string> args, string? stdin,
                                               TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var a in args) info.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(info);
            if (process == null) return null;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            string output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            // wl-paste exits non-zero on an empty clipboard, which is a normal state and not
            // worth logging as a failure.
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Write("Clipboard", $"{file} did not finish within {timeout.TotalSeconds:F0}s.");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard", $"Running {file} failed", ex);
            return null;
        }
    }
}
