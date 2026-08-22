using CoreLib.Diagnostics;
using DesktopCore.Clipboard;

namespace DesktopCore.Platform;

/// <summary>
/// Makes this computer findable: a repeating two-tone alarm until it is stopped.
///
/// <para><b>Why the tone is generated rather than shipped.</b> A bundled sound file is one more
/// thing in the package and one more thing to license, and the requirement here is only that the
/// noise is loud and obviously artificial. A WAV is a header and some samples, so it is written
/// once into the temp directory at startup and played from there.</para>
///
/// <para>Playback goes through whichever of PipeWire, PulseAudio or ALSA the machine has, because
/// which one that is varies by distribution and none of them is safe to assume.</para>
/// </summary>
public sealed class Ringer : IDisposable
{
    private readonly string? _wavPath;
    private readonly string? _player;
    private CancellationTokenSource? _ringing;
    private bool _disposed;

    public bool IsRinging => _ringing is { IsCancellationRequested: false };

    /// <summary>Raised when the alarm starts or stops, so a UI can show a way to stop it.</summary>
    public event Action<bool>? StateChanged;

    public Ringer()
    {
        _player = FindPlayer();

        if (_player == null)
        {
            Log.Write("Ring", "No audio player found; ringing will be silent.");
            return;
        }

        try
        {
            _wavPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshsync-ring.wav");
            File.WriteAllBytes(_wavPath, BuildAlarmWav());
        }
        catch (Exception ex)
        {
            Log.Write("Ring", "Could not write the alarm sound", ex);
            _wavPath = null;
        }
    }

    private static string? FindPlayer()
    {
        // macOS first, since afplay is always there and the Linux ones never are.
        if (OperatingSystem.IsMacOS()) return Proc.Exists("afplay") ? "afplay" : null;

        foreach (string candidate in new[] { "pw-play", "paplay", "aplay", "ffplay" })
        {
            if (Proc.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void Start(string askedBy)
    {
        if (_disposed || IsRinging) return;

        Log.Write("Ring", $"{askedBy} asked this computer to ring.");

        _ringing = new CancellationTokenSource();
        var token = _ringing.Token;

        StateChanged?.Invoke(true);

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_player == null || _wavPath == null)
                {
                    // Nothing to play, but the alarm state is still real - the window shows a
                    // banner and the tray says so. Sleeping keeps that state honest.
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                await PlayOnceAsync(token).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    public void Stop()
    {
        if (!IsRinging) return;

        Log.Write("Ring", "Stopped ringing.");

        try { _ringing?.Cancel(); } catch { }
        _ringing?.Dispose();
        _ringing = null;

        StateChanged?.Invoke(false);
    }

    private async Task PlayOnceAsync(CancellationToken token)
    {
        string[] args = _player switch
        {
            "ffplay" => ["-nodisp", "-autoexit", "-loglevel", "quiet", _wavPath!],
            "aplay" => ["-q", _wavPath!],
            _ => [_wavPath!],
        };

        await Proc.RunAsync(_player!, args, null, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
    }

    /// <summary>
    /// A two-second 16-bit mono WAV: a pair of tones alternating twice a second, which carries
    /// through a sofa far better than a single steady one.
    /// </summary>
    private static byte[] BuildAlarmWav()
    {
        const int rate = 44100;
        const int seconds = 2;
        int samples = rate * seconds;

        var pcm = new byte[samples * 2];

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            double frequency = ((int)(t * 2) % 2 == 0) ? 880.0 : 1174.7;   // A5 and D6

            // Faded at both ends of each half-second so the switch does not click.
            double phase = (t * 2) % 1.0;
            double envelope = Math.Min(1.0, Math.Min(phase, 1.0 - phase) * 12.0);

            short value = (short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * short.MaxValue * 0.55);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                 // PCM header size
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(rate);
        w.Write(rate * 2);           // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();

        return stream.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        StateChanged = null;
    }
}
