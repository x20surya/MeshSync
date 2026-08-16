using Android.Content;
using Android.Net;
using Android.OS;
using CoreLib.Diagnostics;
using System;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Tells <see cref="SyncManager"/> the moment a usable network appears.
    ///
    /// Without this the client only ever discovers that Wi-Fi came back when its own
    /// backoff timer next fires, so walking back into range could leave the phone
    /// unsynced for up to a minute despite the network being ready.
    /// </summary>
    public sealed class NetworkWatcher : ConnectivityManager.NetworkCallback, IDisposable
    {
        private readonly ConnectivityManager? _connectivityManager;
        private bool _registered;

        public NetworkWatcher(Context context)
        {
            _connectivityManager = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        }

        public void Start()
        {
            if (_registered || _connectivityManager == null) return;
            if (Build.VERSION.SdkInt < BuildVersionCodes.N) return;

            try
            {
                _connectivityManager.RegisterDefaultNetworkCallback(this);
                _registered = true;
                Log.Write("Network", "Watching for connectivity changes.");
            }
            catch (Exception ex)
            {
                Log.Write("Network", "Could not register network callback", ex);
            }
        }

        public void Stop()
        {
            if (!_registered || _connectivityManager == null) return;

            try { _connectivityManager.UnregisterNetworkCallback(this); }
            catch (Exception ex) { Log.Write("Network", "Unregistering network callback failed", ex); }
            finally { _registered = false; }
        }

        public override void OnAvailable(Network network)
        {
            base.OnAvailable(network);
            SyncManager.NotifyNetworkAvailable();
        }

        public override void OnLost(Network network)
        {
            base.OnLost(network);
            Log.Write("Network", "Default network lost.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }
    }
}
