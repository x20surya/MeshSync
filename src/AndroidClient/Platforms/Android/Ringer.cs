using System;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Makes this phone findable by making a noise, whatever it has been silenced to.
    ///
    /// <para><b>The alarm stream, not the notification one.</b> The moment you want to find a
    /// phone is very often the moment it is face-down on silent, and the notification stream
    /// respects that. The alarm stream is the one Android keeps audible through Do Not Disturb
    /// and the silent switch, which is precisely why it exists and precisely what this is.</para>
    ///
    /// <para>The volume is turned up and put back. Leaving someone's alarm at maximum because
    /// they once looked for their phone would be a small betrayal, and they would find out at
    /// seven the next morning.</para>
    ///
    /// <para>It stops itself after a minute. A ring triggered by a mis-tap must not run until the
    /// battery does.</para>
    /// </summary>
    public static class Ringer
    {
        public const int NotificationId = 1002;
        private const string ChannelId = "MeshSyncRingChannel";

        private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(1);

        private static readonly object Gate = new();

        private static Ringtone? _ringtone;
        private static Vibrator? _vibrator;
        private static Timer? _stopTimer;
        private static int? _volumeBefore;

        public static bool IsRinging { get { lock (Gate) return _ringtone != null; } }

        public static void Start(string fromDevice)
        {
            var context = global::Android.App.Application.Context;

            lock (Gate)
            {
                if (_ringtone != null) return;

                try
                {
                    RaiseAlarmVolume(context);
                    StartTone(context);
                    StartVibrating(context);

                    _stopTimer = new Timer(_ => Stop(), null, MaxDuration, Timeout.InfiniteTimeSpan);
                }
                catch (Exception ex)
                {
                    Log.Write("Ring", "Could not start ringing", ex);
                    StopLocked();
                    return;
                }
            }

            Log.Write("Ring", $"{fromDevice} asked this phone to ring.");
            ShowNotification(context, fromDevice);
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_ringtone == null) return;
                StopLocked();
            }

            Log.Write("Ring", "Stopped ringing.");

            try
            {
                var manager = (NotificationManager?)global::Android.App.Application.Context
                    .GetSystemService(Context.NotificationService);
                manager?.Cancel(NotificationId);
            }
            catch (Exception ex) { Log.Write("Ring", "Could not clear the ring notification", ex); }
        }

        /// <summary>Tears everything down and puts the volume back. Caller holds the gate.</summary>
        private static void StopLocked()
        {
            try { _ringtone?.Stop(); } catch { }
            _ringtone = null;

            try { _vibrator?.Cancel(); } catch { }
            _vibrator = null;

            try { _stopTimer?.Dispose(); } catch { }
            _stopTimer = null;

            if (_volumeBefore.HasValue)
            {
                try
                {
                    var audio = (AudioManager?)global::Android.App.Application.Context
                        .GetSystemService(Context.AudioService);
                    audio?.SetStreamVolume(global::Android.Media.Stream.Alarm, _volumeBefore.Value, 0);
                }
                catch (Exception ex) { Log.Write("Ring", "Could not restore the alarm volume", ex); }

                _volumeBefore = null;
            }
        }

        private static void RaiseAlarmVolume(Context context)
        {
            var audio = (AudioManager?)context.GetSystemService(Context.AudioService);
            if (audio == null) return;

            _volumeBefore = audio.GetStreamVolume(global::Android.Media.Stream.Alarm);
            audio.SetStreamVolume(global::Android.Media.Stream.Alarm, audio.GetStreamMaxVolume(global::Android.Media.Stream.Alarm), 0);
        }

        private static void StartTone(Context context)
        {
            // Alarm first, then ringtone, then notification: the later ones are quieter and more
            // easily silenced, but a phone that beeps is better than one that does not.
            var uri = RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                   ?? RingtoneManager.GetDefaultUri(RingtoneType.Ringtone)
                   ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);

            if (uri == null)
            {
                Log.Write("Ring", "This phone has no default alarm sound.");
                return;
            }

            var ringtone = RingtoneManager.GetRingtone(context, uri);
            if (ringtone == null) return;

            ringtone.AudioAttributes = new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Alarm)!
                .SetContentType(AudioContentType.Sonification)!
                .Build();

            if (OperatingSystem.IsAndroidVersionAtLeast(28)) ringtone.Looping = true;

            ringtone.Play();
            _ringtone = ringtone;
        }

        private static void StartVibrating(Context context)
        {
            try
            {
                Vibrator? vibrator;

                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    var manager = (VibratorManager?)context.GetSystemService(Context.VibratorManagerService);
                    vibrator = manager?.DefaultVibrator;
                }
                else
                {
#pragma warning disable CA1422 // VibratorManager does not exist below API 31.
                    vibrator = (Vibrator?)context.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422
                }

                if (vibrator?.HasVibrator != true) return;

                // Off, on, off, on ... repeating from index 0, so it keeps going until cancelled.
                long[] pattern = { 0, 600, 400 };
                vibrator.Vibrate(VibrationEffect.CreateWaveform(pattern, repeat: 0)!);

                _vibrator = vibrator;
            }
            catch (Exception ex)
            {
                // A phone that will not vibrate is still ringing, so this is not worth failing over.
                Log.Write("Ring", "Could not vibrate", ex);
            }
        }

        /// <summary>
        /// A full-screen notification with a Stop, so the phone can be silenced without
        /// unlocking it and hunting for the app - which is awkward while it is shrieking.
        /// </summary>
        private static void ShowNotification(Context context, string fromDevice)
        {
            try
            {
                var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
                if (manager == null) return;

                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    var channel = new NotificationChannel(ChannelId, "Find my device", NotificationImportance.High)
                    {
                        Description = "Sounds an alarm when another of your devices asks where this one is"
                    };
                    channel.SetSound(null, null);   // The ringtone is played directly, on the alarm stream.
                    channel.EnableVibration(false); // Same: handled here so it can outlive the notification.
                    manager.CreateNotificationChannel(channel);
                }

                var stopIntent = new Intent(context, typeof(StopRingReceiver));
                var pendingStop = PendingIntent.GetBroadcast(context, 0, stopIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                var builder = new Notification.Builder(context, ChannelId);
                builder.SetContentTitle("Mesh Sync is ringing");
                builder.SetContentText($"{fromDevice} is looking for this device");
                builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm);
                builder.SetOngoing(true);
                builder.SetCategory(Notification.CategoryAlarm);
                builder.SetVisibility(NotificationVisibility.Public);
                builder.AddAction(new Notification.Action.Builder(
                    global::Android.Graphics.Drawables.Icon.CreateWithResource(
                        context, global::Android.Resource.Drawable.IcMenuCloseClearCancel),
                    "Stop", pendingStop).Build()!);
                builder.SetFullScreenIntent(pendingStop, true);

                manager.Notify(NotificationId, builder.Build());
            }
            catch (Exception ex)
            {
                Log.Write("Ring", "Could not show the ring notification", ex);
            }
        }
    }

    /// <summary>Stops the ring from the notification's action.</summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class StopRingReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => Ringer.Stop();
    }
}
