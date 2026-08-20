using System;
using Android.App;
using Android.Content;
using Android.Service.QuickSettings;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// A Quick Settings tile that sends the clipboard in one tap.
    ///
    /// <para><b>What it replaces.</b> The accessibility service, which captured every copy
    /// automatically and made the phone refuse to run UPI and banking apps. This is the nearest
    /// honest substitute: pull down the shade, tap once. Not automatic, but two swipes and a tap
    /// beats opening an app, and it sits in the same place whatever you were doing.</para>
    ///
    /// <para>The tile cannot read the clipboard itself - a service has no focus, and Android
    /// gives a focusless reader nothing - so it launches <see cref="SendClipboardActivity"/>,
    /// which is translucent and finishes immediately.</para>
    /// </summary>
    [Service(
        Label = "Send clipboard",
        Icon = "@android:drawable/ic_menu_share",
        Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
        Exported = true)]
    // The literal rather than TileService.ActionQsTile, because an attribute argument has to be
    // a compile-time constant and the inherited member is not usable as one here.
    [IntentFilter(new[] { "android.service.quicksettings.action.QS_TILE" })]
    public class SendClipboardTileService : TileService
    {
        public override void OnStartListening()
        {
            base.OnStartListening();

            try
            {
                var tile = QsTile;
                if (tile == null) return;

                // Says what will happen if it is tapped, rather than only what it is. A tile
                // that is going to do nothing should look like one.
                bool ready = SyncManager.IsPaired && !SyncManager.IsPaused;

                tile.State = ready ? TileState.Inactive : TileState.Unavailable;
                tile.Label = "Send clipboard";
                tile.ContentDescription = ready
                    ? "Send what is on the clipboard to your other devices"
                    : "Pair a device in Mesh Sync first";

                tile.UpdateTile();
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Could not update the Quick Settings tile", ex);
            }
        }

        public override void OnClick()
        {
            base.OnClick();

            try
            {
                var intent = new Intent(this, typeof(SendClipboardActivity));
                intent.AddFlags(ActivityFlags.NewTask);

                if (OperatingSystem.IsAndroidVersionAtLeast(34))
                {
                    // The Intent overload was removed in Android 14; a PendingIntent is the only
                    // way to launch from a tile there.
                    var pending = PendingIntent.GetActivity(this, 0, intent,
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                    if (pending == null)
                    {
                        Log.Write("Clipboard", "Android would not give a PendingIntent for the tile.");
                        return;
                    }

                    StartActivityAndCollapse(pending);
                }
                else
                {
#pragma warning disable CA1422 // The Intent overload is the only one below API 34.
                    StartActivityAndCollapse(intent);
#pragma warning restore CA1422
                }
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard", "Could not open the clipboard sender from the tile", ex);
            }
        }
    }
}
