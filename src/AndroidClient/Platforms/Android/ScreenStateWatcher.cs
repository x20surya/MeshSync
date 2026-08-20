using System;
using Android.Content;
using Android.OS;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Tells <see cref="SyncManager"/> when the screen goes on and off, which is what decides
    /// whether the Wi-Fi link is held open.
    ///
    /// This is the half of Bluetooth standby that actually saves anything. Bluetooth holds
    /// presence continuously for microamps between connection events; Wi-Fi is raised while the
    /// user is looking at the phone and dropped when they are not, so the socket is down all
    /// night instead of heartbeating through it. Raising it on screen-on rather than on demand
    /// also means the connect cost lands while the phone is being unlocked, so nothing ever
    /// waits for it.
    ///
    /// <para><c>ACTION_SCREEN_ON</c> and <c>ACTION_SCREEN_OFF</c> cannot be declared in the
    /// manifest - Android only delivers them to receivers registered at runtime - so this is
    /// owned by the foreground service, which is the longest-lived component there is.</para>
    ///
    /// <para>Deliberately carries no <c>[BroadcastReceiver]</c> attribute. That attribute exists
    /// to write a <c>&lt;receiver&gt;</c> element into the manifest, which would require a public
    /// default constructor so the system could instantiate it - and there is nothing for the
    /// manifest to declare, because the two actions this cares about are only ever delivered to
    /// receivers registered at runtime.</para>
    /// </summary>
    public sealed class ScreenStateWatcher : BroadcastReceiver, IDisposable
    {
        private readonly Context _context;
        private bool _registered;

        public ScreenStateWatcher(Context context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Start()
        {
            if (_registered) return;

            try
            {
                var filter = new IntentFilter();
                filter.AddAction(Intent.ActionScreenOn);
                filter.AddAction(Intent.ActionScreenOff);
                filter.AddAction(Intent.ActionUserPresent);

                // No exported flag, deliberately. Apps targeting Android 14 and above must
                // declare one - except when every action registered is a protected system
                // broadcast, which all three of these are. Nothing but the system can send
                // them, so there is no surface for another app to reach.
                _context.RegisterReceiver(this, filter);
                _registered = true;

                // Seed the current state: the receiver only reports changes, so starting up
                // with the screen already off would otherwise look like the screen being on
                // and hold Wi-Fi open until the next lock.
                PublishCurrentState();

                Log.Write("Screen", "Watching for screen on and off.");
            }
            catch (Exception ex)
            {
                // Not fatal. Without this, Wi-Fi is simply held whenever Bluetooth is not,
                // which is the behaviour that predates standby rather than a broken one.
                Log.Write("Screen", "Could not register the screen state receiver", ex);
            }
        }

        public void Stop()
        {
            if (!_registered) return;

            try { _context.UnregisterReceiver(this); }
            catch (Exception ex) { Log.Write("Screen", "Unregistering the screen state receiver failed", ex); }
            finally { _registered = false; }
        }

        private void PublishCurrentState()
        {
            try
            {
                var power = (PowerManager?)_context.GetSystemService(Context.PowerService);
                if (power == null) return;

                if (power.IsInteractive) SyncManager.NotifyScreenOn();
                else SyncManager.NotifyScreenOff();
            }
            catch (Exception ex)
            {
                Log.Write("Screen", "Could not read the current screen state", ex);
            }
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case Intent.ActionScreenOn:
                case Intent.ActionUserPresent:
                    SyncManager.NotifyScreenOn();
                    break;

                case Intent.ActionScreenOff:
                    SyncManager.NotifyScreenOff();
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }
    }
}
