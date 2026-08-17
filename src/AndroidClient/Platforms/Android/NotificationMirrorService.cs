using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Service.Notification;
using CoreLib.Diagnostics;
using CoreLib.Transport;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Mirrors this phone's notifications to the rest of the mesh.
    ///
    /// <para><b>Its own permission, and a normal one.</b> Unlike the accessibility service this
    /// app also uses, <c>BIND_NOTIFICATION_LISTENER_SERVICE</c> is a permission Android intends
    /// applications to ask for. The user grants it in Settings and can revoke it there.</para>
    ///
    /// <para><b>Off until asked, and then per application.</b> Nothing is mirrored by default and
    /// nothing is mirrored wholesale. Mirroring everything is unusable within a day - the second
    /// screen fills with the same noise the first one has - and this is the most private data the
    /// app will ever carry, so the allowlist is the feature rather than a setting on it.</para>
    ///
    /// <para><b>Never stored.</b> A mirrored notification goes out over the link and is not
    /// written to the activity log or anywhere else. Clipboard traffic is ephemeral by project
    /// rule; this is more so.</para>
    /// </summary>
    [Service(
        Label = "Mesh Sync notifications",
        Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
        Exported = true)]
    [IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
    public class NotificationMirrorService : NotificationListenerService
    {
        private static NotificationMirrorService? _instance;

        /// <summary>True while Android has actually bound the service, not merely granted it.</summary>
        public static bool IsRunning => _instance != null;

        public override void OnListenerConnected()
        {
            base.OnListenerConnected();
            _instance = this;
            Log.Write("Notify", "Notification mirroring is connected.");
        }

        public override void OnListenerDisconnected()
        {
            base.OnListenerDisconnected();
            _instance = null;
            Log.Write("Notify", "Notification mirroring disconnected.");
        }

        public override void OnNotificationPosted(StatusBarNotification? sbn)
        {
            base.OnNotificationPosted(sbn);

            try
            {
                if (sbn == null) return;
                if (!NotificationMirrorSettings.IsEnabled) return;

                string package = sbn.PackageName ?? "";

                // Our own notifications would loop straight back out, and the ongoing foreground
                // one would be mirrored for ever.
                if (package == PackageName) return;

                // Ongoing ones are the persistent kind - a music player, a download, a
                // navigation route. They are not events, and mirroring them means a second
                // screen with a stuck row on it.
                if (sbn.IsOngoing) return;

                if (!NotificationMirrorSettings.IsAllowed(package)) return;

                var extras = sbn.Notification?.Extras;
                string title = extras?.GetString(Notification.ExtraTitle) ?? "";
                string text = extras?.GetString(Notification.ExtraText)
                              ?? extras?.GetString(Notification.ExtraBigText)
                              ?? "";

                // Nothing to read means nothing worth sending. A notification with neither is
                // usually a placeholder an app updates a moment later.
                if (title.Length == 0 && text.Length == 0) return;

                var mirrored = new MirroredNotification(
                    sbn.Key ?? "",
                    package,
                    NameOf(package),
                    title,
                    text,
                    DateTimeOffset.FromUnixTimeMilliseconds(sbn.PostTime));

                if (mirrored.Key.Length == 0) return;

                _ = SyncManager.SendNotificationAsync(mirrored);
            }
            catch (Exception ex)
            {
                // Never let this take the listener down: Android will stop rebinding a service
                // that keeps throwing, and the user would have to re-grant it by hand.
                Log.Write("Notify", "Mirroring a notification failed", ex);
            }
        }

        public override void OnNotificationRemoved(StatusBarNotification? sbn)
        {
            base.OnNotificationRemoved(sbn);

            try
            {
                if (sbn == null || !NotificationMirrorSettings.IsEnabled) return;
                if (sbn.PackageName == PackageName) return;
                if (!NotificationMirrorSettings.IsAllowed(sbn.PackageName ?? "")) return;

                string key = sbn.Key ?? "";
                if (key.Length > 0) _ = SyncManager.SendNotificationDismissAsync(key);
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Mirroring a dismissal failed", ex);
            }
        }

        /// <summary>
        /// Clears a notification here, because the other device cleared it there.
        ///
        /// This is what makes mirroring feel finished rather than like a second inbox to empty.
        /// </summary>
        public static void DismissByKey(string key)
        {
            try { _instance?.CancelNotification(key); }
            catch (Exception ex) { Log.Write("Notify", "Could not dismiss a notification", ex); }
        }

        /// <summary>Every app that has posted a notification recently, for the allowlist screen.</summary>
        public static IReadOnlyList<(string Package, string Name)> RecentApps()
        {
            try
            {
                var active = _instance?.GetActiveNotifications();
                if (active == null) return Array.Empty<(string, string)>();

                return active
                    .Select(n => n.PackageName ?? "")
                    .Where(p => p.Length > 0 && p != _instance!.PackageName)
                    .Distinct(StringComparer.Ordinal)
                    .Select(p => (p, NameOf(p)))
                    .OrderBy(pair => pair.Item2, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not list the apps that notify", ex);
                return Array.Empty<(string, string)>();
            }
        }

        /// <summary>A package name is not a name. This is what the user actually recognises.</summary>
        private static string NameOf(string package)
        {
            try
            {
                var manager = global::Android.App.Application.Context.PackageManager;
                if (manager == null) return package;

                var info = manager.GetApplicationInfo(package, 0);
                return manager.GetApplicationLabel(info) ?? package;
            }
            catch
            {
                return package;
            }
        }

        /// <summary>True once the user has granted the listener in Settings.</summary>
        public static bool IsGranted()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                string? enabled = global::Android.Provider.Settings.Secure.GetString(
                    context.ContentResolver, "enabled_notification_listeners");

                return enabled?.Contains(context.PackageName ?? "", StringComparison.Ordinal) == true;
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not read the notification listener grant", ex);
                return false;
            }
        }

        /// <summary>Opens the Settings screen where the grant lives. Only the user can give it.</summary>
        public static void RequestGrant()
        {
            try
            {
                var intent = new Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
                intent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                Log.Write("Notify", "Could not open the notification access settings", ex);
            }
        }
    }

    /// <summary>
    /// Which applications may be mirrored, and whether mirroring is on at all.
    ///
    /// Default deny, deliberately. Granting the listener says "you may see my notifications";
    /// it does not say "send all of them to my laptop", and conflating the two would be taking
    /// something the user did not offer.
    /// </summary>
    public static class NotificationMirrorSettings
    {
        private const string PrefsName = "NotificationMirror";
        private const string KeyEnabled = "Enabled";
        private const string KeyAllowed = "AllowedPackages";

        private static ISharedPreferences? Prefs()
        {
            try
            {
                return global::Android.App.Application.Context
                    .GetSharedPreferences(PrefsName, FileCreationMode.Private);
            }
            catch { return null; }
        }

        public static bool IsEnabled
        {
            get => Prefs()?.GetBoolean(KeyEnabled, false) ?? false;
            set
            {
                Prefs()?.Edit()?.PutBoolean(KeyEnabled, value)?.Apply();
                Log.Write("Notify", value ? "Notification mirroring switched on." : "Notification mirroring switched off.");
            }
        }

        public static IReadOnlyList<string> Allowed()
        {
            string raw = Prefs()?.GetString(KeyAllowed, "") ?? "";
            return raw.Length == 0
                ? Array.Empty<string>()
                : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool IsAllowed(string package) =>
            package.Length > 0 && Allowed().Contains(package, StringComparer.Ordinal);

        public static void SetAllowed(string package, bool allowed)
        {
            var current = Allowed().ToList();

            if (allowed && !current.Contains(package, StringComparer.Ordinal)) current.Add(package);
            else if (!allowed) current.RemoveAll(p => string.Equals(p, package, StringComparison.Ordinal));
            else return;

            Prefs()?.Edit()?.PutString(KeyAllowed, string.Join('\n', current))?.Apply();
        }
    }
}
