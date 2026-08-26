---
type: head
status: shipped
platforms: [android]
tier: either
code:
  - src/AndroidClient/SyncManager.cs
  - src/AndroidClient/Platforms/Android/SyncForegroundService.cs
  - src/AndroidClient/Platforms/Android/AppPermissions.cs
  - src/AndroidClient/ScanPage.xaml.cs
  - src/AndroidClient/SetupPage.xaml.cs
updated: 2026-08-26
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

**`NotificationMirrorService`** is the only sensitive permission left.
**Every app mirrors once Android's listener grant is given**, and muting is per application -
which is the opposite of what this note said until 2026-08-26, and had been for a while.
The deny-by-default model was dropped at settings schema 2: the grant was in place, the service
was bound, and nothing appeared, because a second and third opt-in were still waiting in a
settings screen.
A mirror that shows nothing until configured is indistinguishable from a broken one.
Nothing is ever stored either way.
Its reply path **fills the `RemoteInput` the app itself attached** - it is not automating an app
from the outside, which is the line this project drew when it banned the accessibility service.

**`AppPermissions`** is the one place that knows what this app asks Android for and when.
Every request answers two questions rather than one: is it granted, and *will Android still ask*.
That second one is not a nicety - Android silently ignores a request for something already
refused twice, so a button that asks again does nothing at all and reads as broken.
See [[#Permissions are asked where they are explained]].

**`ScanPage`** is the pairing scanner, and it is the app's own.
The reader binds CameraX when its handler is created, so it is built in code **after** the camera
grant is in hand rather than declared in the XAML: a handler created while the permission is
refused binds to nothing, and granting afterwards leaves a black rectangle with no error anywhere
to say why.

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

## Permissions are asked where they are explained

One per step of the setup wizard, immediately after the screen that says why it is wanted, and
never before.

| Step | Asks for |
|---|---|
| Pair | `CAMERA`, on tapping *Scan the code* |
| Keep it connected | the three `BLUETOOTH_*` grants, then the battery exemption |
| See your phone on your computer | `POST_NOTIFICATIONS` and the notification listener |
| Sending from this phone | `READ_MEDIA_IMAGES`, for screenshots |

**Three of these used to fire from `MainActivity.OnCreate`**, before the user had seen a single
screen: two system dialogs stacked on the splash, and a refusal quietly cost radio pairing and
screenshot sync with nothing anywhere to say so.
Photo access was then asked for a second time by the wizard, where Android ignored it because it
had already been answered - so that step appeared to do nothing.

**The listener grant is a settings screen, not a dialog.**
Nothing else here can be asked for the same way, so the wizard explains it, opens the screen for
this app specifically on Android 11+, and lets its own 700ms poll notice the grant and advance.
That poll is why no step ever has to ask "did that work?".

**The wizard runs once and stores `SetupComplete`**, so an existing install never sees a new step.
Anything added later reaches those users through the dashboard's warning card instead - which is
where notification mirroring and the battery exemption are offered, one at a time and each
dismissible for good.
Re-running a wizard on somebody already set up is worse than the gap it closes.

## Platform gotchas

**Declaring a permission is not requesting it.**
The Bluetooth permissions are runtime grants on Android 12+ and being refused one fails silently.
`android.permission.VIBRATE` was simply missing, and Android throws for the vibrate call rather
than ignoring it.

**`QUERY_ALL_PACKAGES` is what turns a package name into a name.**
Android 11 hid every other installed app behind package visibility, so
`PackageManager.getApplicationInfo` throws for anything but a system package.
The mute list read `in.swiggy.android`, and worse, **every notification mirrored to the desktop
was labelled with the package too**.

**The splash screen has no theme variant.**
`MauiSplashScreen` takes one colour and one image, so a phone in dark mode got a full-screen
off-white flash before a near-black app.
It is fixed with Android resource qualifiers - `values-night` for the colour and
`drawable-night` for the lockup - and the night drawable has to mirror MAUI's own structure
exactly, including the `-v31` wrapper that sizes the icon.
Pointing straight at the bitmap instead skips that wrapper, and Android 12+ then draws the lockup
full size into the system splash icon slot and masks it, clipping the wordmark.

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
