using System;
using System.IO;
using System.Media;
using System.Threading;
using CoreLib.Diagnostics;

namespace WinDaemon
{
    /// <summary>
    /// Makes this machine findable by making a noise.
    ///
    /// <para>The tone is generated rather than shipped as an asset, because a two-second alarm is
    /// a hundred lines of arithmetic and a WAV file is a binary blob in a repository people are
    /// meant to read. It also means the sound is the same on every machine regardless of what
    /// the system sounds have been set to - including muted, which is exactly when someone is
    /// looking for a laptop.</para>
    ///
    /// <para>It stops itself after a minute. A ring triggered by a mis-tap must not run until the
    /// battery does.</para>
    /// </summary>
    public static class Ringer
    {
        /// <summary>Long enough to find something down the back of a sofa, short enough to forgive.</summary>
        private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(1);

        private static readonly object Gate = new();

        private static SoundPlayer? _player;
        // Fully qualified: WinForms is referenced for the tray icon, so a bare Timer is ambiguous
        // between it and System.Threading - the same trap Brush and MessageBox set in this project.
        private static System.Threading.Timer? _stopTimer;

        public static bool IsRinging { get { lock (Gate) return _player != null; } }

        /// <summary>Raised when the ring starts or stops, so the window can offer a way to stop it.</summary>
        public static event Action? Changed;

        public static void Start(string fromDevice)
        {
            lock (Gate)
            {
                if (_player != null) return;

                try
                {
                    _player = new SoundPlayer(BuildAlarm());
                    _player.PlayLooping();

                    _stopTimer = new System.Threading.Timer(_ => Stop(), null, MaxDuration, Timeout.InfiniteTimeSpan);
                }
                catch (Exception ex)
                {
                    Log.Write("Ring", "Could not start ringing", ex);
                    _player = null;
                    return;
                }
            }

            Log.Write("Ring", $"{fromDevice} asked this computer to ring.");
            Raise();
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_player == null) return;

                try { _player.Stop(); } catch { }
                try { _player.Dispose(); } catch { }
                _player = null;

                try { _stopTimer?.Dispose(); } catch { }
                _stopTimer = null;
            }

            Log.Write("Ring", "Stopped ringing.");
            Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Write("Ring", "Changed handler threw", ex); }
        }

        /// <summary>
        /// A two-second alarm as a WAV in memory: two alternating tones, which carry further
        /// through a cushion than one steady note and read as an alarm rather than a notification.
        /// </summary>
        private static MemoryStream BuildAlarm()
        {
            const int sampleRate = 44100;
            const double seconds = 2.0;
            const short amplitude = 12000;

            int sampleCount = (int)(sampleRate * seconds);
            var stream = new MemoryStream(44 + sampleCount * 2);
            var writer = new BinaryWriter(stream);

            int dataBytes = sampleCount * 2;

            // Canonical 16-bit mono PCM header.
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);                       // PCM header size
            writer.Write((short)1);                 // PCM, uncompressed
            writer.Write((short)1);                 // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);           // bytes per second
            writer.Write((short)2);                 // block align
            writer.Write((short)16);                // bits per sample
            writer.Write("data"u8);
            writer.Write(dataBytes);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;

                // Alternating every quarter second.
                double frequency = ((int)(t * 4) % 2 == 0) ? 880.0 : 660.0;

                // Faded at both ends of each beep, because a square-edged tone clicks.
                double intoBeep = (t * 4) % 1.0;
                double envelope = Math.Min(1.0, Math.Min(intoBeep, 1.0 - intoBeep) * 12.0);

                writer.Write((short)(Math.Sin(2 * Math.PI * frequency * t) * amplitude * envelope));
            }

            writer.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}
