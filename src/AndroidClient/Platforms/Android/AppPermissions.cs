using Android.Content;
using Android.OS;
using Android.Provider;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// The one place that knows what this app asks Android for, and when.
    ///
    /// <para><b>Why it exists.</b> The requests used to be scattered: three Bluetooth grants and
    /// photo access fired from <c>MainActivity.OnCreate</c>, before the user had seen a single
    /// screen, and photo access again from the setup wizard - where the second call did nothing,
    /// because Android does not re-show a dialog that has already been answered. That is the
    /// shape of the bug this class exists to make impossible: a button that requests something
    /// already refused looks broken, and the only honest thing left to offer is app settings.</para>
    ///
    /// <para>So every request here answers two questions rather than one: is it granted, and if
    /// not, will Android still ask? <see cref="Outcome"/> carries both.</para>
    /// </summary>
    public static class AppPermissions
    {
        /// <summary>What happened, and what the UI may honestly offer next.</summary>
        public enum Outcome
        {
            /// <summary>Granted, now or already.</summary>
            Granted,

            /// <summary>Refused, but Android will show the dialog again if asked.</summary>
            Refused,

            /// <summary>Refused for good. Only app settings can change it now.</summary>
            Blocked,
        }

        // ──────────────────────────────────── Bluetooth

        /// <summary>
        /// The three grants Android 12 turned into runtime permissions.
        ///
        /// <para>Declared together deliberately. Scanning and connecting are what let this phone
        /// find another; advertising is what lets it <em>be</em> found, and it is the one that had
        /// never been asked for until the phone could take the peripheral role. A device that can
        /// only scan is a device that can only ever be the central, and
        /// <c>BleLinkArbiter</c> needs to know which of those is true.</para>
        ///
        /// <para>A subclass rather than MAUI's own <c>Permissions.Bluetooth</c>, so the three are
        /// named here and cannot quietly change underneath this app.</para>
        /// </summary>
        public sealed class MeshBluetooth : Permissions.BasePlatformPermission
        {
            public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
                OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? new (string, bool)[]
                    {
                        (global::Android.Manifest.Permission.BluetoothScan, true),
                        (global::Android.Manifest.Permission.BluetoothConnect, true),
                        (global::Android.Manifest.Permission.BluetoothAdvertise, true),
                    }
                    : new (string, bool)[]
                    {
                        (global::Android.Manifest.Permission.AccessFineLocation, true),
                    };
        }

        public static Task<bool> HasBluetoothAsync() => HasAsync<MeshBluetooth>();

        public static Task<Outcome> RequestBluetoothAsync() => RequestAsync<MeshBluetooth>();

        // ──────────────────────────────────── photos, for screenshot sync

        public static Task<bool> HasPhotosAsync() =>
            OperatingSystem.IsAndroidVersionAtLeast(33)
                ? HasAsync<Permissions.Photos>()
                : HasAsync<Permissions.StorageRead>();

        public static Task<Outcome> RequestPhotosAsync() =>
            OperatingSystem.IsAndroidVersionAtLeast(33)
                ? RequestAsync<Permissions.Photos>()
                : RequestAsync<Permissions.StorageRead>();

        // ──────────────────────────────────── posting notifications

        /// <summary>
        /// Android 13 and above will not show any notification without this, including the
        /// foreground service's own - which is the notification that says syncing is running and
        /// carries the Send action.
        /// </summary>
        public static Task<bool> HasPostNotificationsAsync() =>
            OperatingSystem.IsAndroidVersionAtLeast(33)
                ? HasAsync<Permissions.PostNotifications>()
                : Task.FromResult(true);

        public static Task<Outcome> RequestPostNotificationsAsync() =>
            OperatingSystem.IsAndroidVersionAtLeast(33)
                ? RequestAsync<Permissions.PostNotifications>()
                : Task.FromResult(Outcome.Granted);

        // ──────────────────────────────────── battery

        /// <summary>
        /// Whether Android is still free to doze this app.
        ///
        /// <para>The foreground service is what holds the links, and it is the right mechanism.
        /// It is not sufficient on its own: several manufacturers stop a foreground service
        /// anyway once the phone has been idle, and the result reads as "sync is unreliable"
        /// rather than as a setting somebody could change.</para>
        /// </summary>
        public static bool IsBatteryOptimised()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var power = (PowerManager?)context.GetSystemService(Context.PowerService);

                return power != null && !power.IsIgnoringBatteryOptimizations(context.PackageName!);
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", "Could not read the battery optimisation state", ex);

                // Never nag on the strength of a failed check.
                return false;
            }
        }

        /// <summary>
        /// Asks Android to stop optimising this app, from a tap and only once.
        ///
        /// <para>The direct dialog needs <c>REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c>. Google Play
        /// restricts that permission to a short list of app types; Mesh Sync is installed from a
        /// GitHub release rather than Play, so the dialog is available - and the settings list is
        /// the fallback if a device refuses it anyway.</para>
        /// </summary>
        public static void RequestBatteryExemption()
        {
            var context = global::Android.App.Application.Context;

            try
            {
                var intent = new Intent(
                    Settings.ActionRequestIgnoreBatteryOptimizations,
                    global::Android.Net.Uri.Parse("package:" + context.PackageName));

                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", "The battery exemption dialog was refused; opening the list", ex);

                try
                {
                    var list = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                    list.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(list);
                }
                catch (Exception inner)
                {
                    Log.Write("Permissions", "Could not open battery optimisation settings", inner);
                }
            }
        }

        /// <summary>
        /// The list every app appears in, for changing an answer already given.
        ///
        /// A separate destination from <see cref="RequestBatteryExemption"/>, because Android
        /// offers no dialog for putting an app back under optimisation - only this screen.
        /// </summary>
        public static void OpenBatterySettings()
        {
            try
            {
                var intent = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                intent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", "Could not open battery optimisation settings", ex);
                OpenAppSettings();
            }
        }

        // ──────────────────────────────────── app settings

        /// <summary>The last resort, for anything Android will no longer prompt for.</summary>
        public static void OpenAppSettings()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var intent = new Intent(
                    Settings.ActionApplicationDetailsSettings,
                    global::Android.Net.Uri.Parse("package:" + context.PackageName));

                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", "Could not open app settings", ex);
            }
        }

        // ──────────────────────────────────── the shared shape

        private static async Task<bool> HasAsync<T>() where T : Permissions.BasePermission, new()
        {
            try
            {
                return await Permissions.CheckStatusAsync<T>() == PermissionStatus.Granted;
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", $"Checking {typeof(T).Name} failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Asks, and reports whether asking again would achieve anything.
        ///
        /// <para><c>ShouldShowRationale</c> is read <b>after</b> the request rather than before.
        /// Before the first request it is false for a permission that has never been seen, which
        /// is indistinguishable from false for one refused twice - so reading it first would
        /// report every fresh permission as permanently blocked.</para>
        /// </summary>
        private static async Task<Outcome> RequestAsync<T>() where T : Permissions.BasePermission, new()
        {
            try
            {
                var status = await Permissions.CheckStatusAsync<T>();

                if (status != PermissionStatus.Granted)
                    status = await Permissions.RequestAsync<T>();

                if (status == PermissionStatus.Granted)
                {
                    Log.Write("Permissions", $"{typeof(T).Name} granted.");
                    return Outcome.Granted;
                }

                bool willAskAgain = Permissions.ShouldShowRationale<T>();

                Log.Write("Permissions",
                    $"{typeof(T).Name} refused{(willAskAgain ? "" : " for good")}.");

                return willAskAgain ? Outcome.Refused : Outcome.Blocked;
            }
            catch (Exception ex)
            {
                Log.Write("Permissions", $"Requesting {typeof(T).Name} failed", ex);
                return Outcome.Refused;
            }
        }
    }
}
