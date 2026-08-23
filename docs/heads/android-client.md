---
type: head
status: shipped
platforms: [android]
tier: either
code:
  - src/AndroidClient/SyncManager.cs
  - src/AndroidClient/Platforms/Android/SyncForegroundService.cs
updated: 2026-08-24
---

# Android client

.NET MAUI app with a navigation drawer - Home, Activity, Devices, Files, Settings, About - plus
the setup wizard.
Application id `dev.meshsync.app`, Android 8 or newer.

One of the two finished platforms, alongside [[windows-daemon]].

## The foreground service is the app

`Platforms/Android/SyncForegroundService.cs`, type `connectedDevice`.
It holds the links, which is what makes surviving Doze plausible, and it hosts three watchers that
have nothing to do with each other:

- `ScreenshotObserver.cs` - a `ContentObserver` on MediaStore, so screenshots send with no tap
- `NetworkWatcher.cs` - reconnect on network change
- `ScreenStateWatcher.cs` - the screen-following Wi-Fi logic

`BootReceiver.cs` starts it after a restart.

**All four of those used to live in an accessibility service**, and none of them needed
accessibility.
Declining that one permission silently cost screenshot sync, reconnect-on-network-change and the
screen logic as well - and, because Android rebinds an enabled accessibility service on boot, it
was also quietly the boot-persistence mechanism.
Removing it without the `BOOT_COMPLETED` receiver would have looked like a transport bug.

## Sending the clipboard, three ways

Android only lets an app read the clipboard while it is in front, so sending is user-initiated.

| Route | File |
|---|---|
| Quick Settings tile | `SendClipboardTileService.cs`, `SendClipboardActivity.cs` |
| `PROCESS_TEXT` selection menu | `ProcessTextActivity.cs` |
| Share sheet | `ShareTargetActivity.cs` |

**The tile read in `OnCreate` and never worked.**
Android only answers a clipboard read to an app that already holds window focus, which an activity
does not during `OnCreate`.
The read does not throw - it returns an empty clip, indistinguishable from the clipboard genuinely
being empty - so the tile appeared to work and silently sent nothing.
Reading happens in `OnWindowFocusChanged` now, and the read is separated from the send so that
waiting for a link cannot cost the focus the read depends on.

See [[clipboard-sync]] for why the accessibility route is closed for good.

## The pieces worth knowing about

**`ClipboardCapture`** obeys one rule: since Android 10 the clipboard may only be read by an app
that holds focus. Everything about the three send routes follows from that.

**`MirroredNotificationDisplay`** is why the phone displays mirrored notifications as well as
sourcing them. **These cannot loop**: a notification this class posts is posted by this app, and
`NotificationMirrorService` ignores its own app's notifications.
That is also why a swipe needs a delete intent to be noticed at all.

**`ReceivedFiles`** exists because everything received on Android 10 and later goes through
MediaStore, which hands back a content URI and no path the app is allowed to know.
The URI has to be kept at the moment of writing or the file is on the device and unreachable from
the app that put it there.

**`Ringer`** uses the **alarm stream**, not the notification one.
The moment you want to find a phone is the moment it is on silent.

**`AndroidKeyProtector`** wraps rather than generates in the Keystore.
Generating the identity key inside the Keystore would mean doing ECDH through `KeyAgreement`,
which forks the key agreement between platforms. See [[key-at-rest]].

**`NotificationMirrorService`** is the only sensitive permission left, and it is off until asked,
then allowed per application. Nothing is mirrored by default and nothing is ever stored.
Its reply path **fills the `RemoteInput` the app itself attached** - it is not automating an app
from the outside, which is the line this project drew when it banned the accessibility service.

## Building and installing

```powershell
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk
```

**Use `install -r`.**
`adb shell pm clear` wipes the identity and the paired devices, which costs a re-pair.

MAUI device deployment is broken enough on .NET 10 that this is done by hand, with
FastDeployment disabled in the `.csproj`.
Debug builds deployed by CLI need `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>`.

## Platform gotchas

**Declaring a permission is not requesting it.**
The Bluetooth permissions are runtime grants on Android 12+ and being refused one fails silently.
`android.permission.VIBRATE` was simply missing, and Android throws for the vibrate call rather
than ignoring it.

**`ACTION_SCREEN_ON` and `ACTION_SCREEN_OFF` cannot be declared in the manifest.**
Android only delivers them to receivers registered at runtime, and such a receiver must not carry
`[BroadcastReceiver]` either - the attribute writes a `<receiver>` element, which then demands a
public default constructor and fails the build with `XA4213`.

**A foreground service must call `startForeground` within seconds**, so the notification is posted
before anything else that can fail.
Starting one from the background is refused on Android 12+, and receiving `BOOT_COMPLETED` is one
of the exemptions.
**Stopping the service is what removes its notification**; cancelling it directly does nothing.

**Android caches a device's service and handle table across connections.**
`BluetoothGatt.refresh()` clears it and is not in the SDK, so it needs reflection.

**`Android.Media.Stream` collides with `System.IO.Stream`.**

## See also

[[clipboard-sync]] · [[notification-mirroring]] · [[bluetooth-tier]] · [[ble-role-negotiation]]
