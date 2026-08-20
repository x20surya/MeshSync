using System;
using Android.Content;
using CoreLib.Diagnostics;

namespace AndroidClient.Platforms.Android
{
    /// <summary>
    /// Opens a file that arrived from the mesh, in whatever app the phone thinks owns it.
    ///
    /// <para><b>Why the app cannot just open a path.</b> Everything received on Android 10 and
    /// above is written through MediaStore, which hands back a content URI and never a path. That
    /// URI is the only handle to the file that will ever exist, which is why it is recorded on the
    /// activity row at the moment of writing rather than looked up later.</para>
    ///
    /// <para>The grant matters as much as the URI: a content URI belongs to the process that
    /// created it, so handing one to another application without
    /// <see cref="ActivityFlags.GrantReadUriPermission"/> gives the receiver something it is not
    /// allowed to read.</para>
    /// </summary>
    public static class ReceivedFiles
    {
        /// <summary>True when something on this phone was willing to open it.</summary>
        public static bool Open(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return false;

            try
            {
                var context = global::Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(location);
                if (uri == null) return false;

                string mime = context.ContentResolver?.GetType(uri) ?? "*/*";

                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mime);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

                // A chooser rather than the default handler: "application/octet-stream" is what
                // an unrecognised file is sent as, and letting the user pick beats a flat refusal.
                var chooser = Intent.CreateChooser(intent, "Open with");
                chooser?.AddFlags(ActivityFlags.NewTask);

                context.StartActivity(chooser ?? intent);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Files", "Nothing on this phone would open that file", ex);
                return false;
            }
        }
    }
}
