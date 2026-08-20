using System;
using System.Security.Cryptography;
using System.Text;
using CoreLib.Diagnostics;
using CoreLib.Transport;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace WinDaemon
{
    /// <summary>
    /// Puts a mirrored notification where Windows keeps notifications.
    ///
    /// <para><b>Why the in-app list was not enough.</b> Mirroring already worked: a notification
    /// crossed from the phone and appeared on the daemon's Notifications page. That page is only
    /// useful to somebody already looking at it, which is nobody - the window lives in the tray.
    /// A notification that has to be gone looking for is not a notification.</para>
    ///
    /// <para><b>Registering without an installer.</b> Windows will not raise a toast for a process
    /// it cannot name, and the usual way to give it one is a Start Menu shortcut carrying an
    /// AppUserModelID. This app has no installer - it puts itself in the <c>Run</c> key and that
    /// is all - so it registers the id directly under <c>HKCU\Software\Classes\AppUserModelId</c>
    /// instead, which Windows has accepted since 1709 and which leaves nothing behind but one
    /// registry key.</para>
    ///
    /// <para><b>Removal is the reason for the tag.</b> Dismissing a notification on the phone has
    /// to clear it here too, and Action Center will only give up an entry addressed by tag and
    /// group. The peer's key is opaque and far too long for a tag, so it is hashed to something
    /// short and stable.</para>
    /// </summary>
    public static class WindowsToasts
    {
        private const string Aumid = "dev.meshsync.daemon";
        private const string Group = "mesh";

        private static bool _registered;
        private static ToastNotifier? _notifier;

        /// <summary>True once Windows has accepted the identity and a notifier exists.</summary>
        private static bool Ready()
        {
            if (_notifier != null) return true;

            try
            {
                if (!_registered)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\AppUserModelId\{Aumid}");

                    key?.SetValue("DisplayName", "Mesh Sync", RegistryValueKind.String);

                    string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "meshsync.ico");
                    if (System.IO.File.Exists(icon)) key?.SetValue("IconUri", icon, RegistryValueKind.String);

                    _registered = true;
                }

                _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
                return _notifier != null;
            }
            catch (Exception ex)
            {
                // A daemon that cannot toast is still a daemon: the in-app list keeps working.
                Log.Write("Toast", "Windows would not give this app a notifier", ex);
                return false;
            }
        }

        /// <summary>Raises a toast for a notification that arrived from the mesh.</summary>
        public static void Show(MirroredNotification notification, string fromDevice)
        {
            if (!Ready()) return;

            try
            {
                string source = string.IsNullOrEmpty(notification.AppName)
                    ? fromDevice
                    : $"{notification.AppName} on {fromDevice}";

                string title = string.IsNullOrEmpty(notification.Title) ? source : notification.Title;

                var xml = new XmlDocumentBuilder()
                    .Text(title)
                    .Text(notification.Text)
                    .Attribution(source)
                    .Build();

                var toast = new ToastNotification(xml)
                {
                    Tag = TagFor(notification.Key),
                    Group = Group
                };

                _notifier!.Show(toast);
            }
            catch (Exception ex)
            {
                Log.Write("Toast", "Could not raise a notification", ex);
            }
        }

        /// <summary>Clears the toast for one notification, because it went away on its device.</summary>
        public static void Remove(string key)
        {
            try
            {
                ToastNotificationManager.History.Remove(TagFor(key), Group, Aumid);
            }
            catch (Exception ex)
            {
                Log.Write("Toast", "Could not clear a notification", ex);
            }
        }

        /// <summary>Clears every toast this app has raised.</summary>
        public static void Clear()
        {
            try { ToastNotificationManager.History.Clear(Aumid); }
            catch (Exception ex) { Log.Write("Toast", "Could not clear the notifications", ex); }
        }

        /// <summary>
        /// A tag Action Center will accept, from a key it would not.
        ///
        /// Tags are capped well below the length of a peer's key, and the key may hold characters
        /// the field does not want. A truncated SHA-256 is short, stable across runs, and legal.
        /// </summary>
        private static string TagFor(string key)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash, 0, 8);
        }

        /// <summary>
        /// Builds the toast payload.
        ///
        /// Assembled as XML because that is the only shape <see cref="ToastNotification"/> takes,
        /// and text is escaped rather than concatenated: a notification is written by whatever
        /// app posted it on the phone, so an ampersand in a message subject is not hypothetical.
        /// </summary>
        private sealed class XmlDocumentBuilder
        {
            private readonly StringBuilder _lines = new();
            private string _attribution = "";

            public XmlDocumentBuilder Text(string value)
            {
                if (!string.IsNullOrEmpty(value)) _lines.Append($"<text>{Escape(value)}</text>");
                return this;
            }

            public XmlDocumentBuilder Attribution(string value)
            {
                _attribution = value;
                return this;
            }

            public Windows.Data.Xml.Dom.XmlDocument Build()
            {
                string attribution = string.IsNullOrEmpty(_attribution)
                    ? ""
                    : $"<text placement=\"attribution\">{Escape(_attribution)}</text>";

                var document = new Windows.Data.Xml.Dom.XmlDocument();
                document.LoadXml(
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    _lines + attribution +
                    "</binding></visual></toast>");

                return document;
            }

            private static string Escape(string value) =>
                value.Replace("&", "&amp;")
                     .Replace("<", "&lt;")
                     .Replace(">", "&gt;")
                     .Replace("\"", "&quot;");
        }
    }
}
