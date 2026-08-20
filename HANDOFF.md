# Handoff notes

Written at the end of a long session so the next one can pick up without rediscovering anything.
Read this alongside [AGENTS.md](AGENTS.md), which describes the architecture and the rules.

---

## The most recent session, in short

The whole plan in `.lavish/mesh-sync-plan.html` was executed as far as phase 5.
205 tests, up from 138; both apps build with no warnings.

**Committed the tree first.** Everything below the identity work had been sitting uncommitted -
about 3,600 insertions including the entire `Identity` tree - while the two most recent commits
were documentation describing code git did not have. It went in as one safety commit, then was
rewritten into a six-commit series before anything was pushed.

**Forward secrecy on both tiers.** The agreement was static-static, so a recovered private key
would have opened every session that pair had ever had. Each connection now mints an ephemeral
keypair and mixes two ECDH secrets through HKDF. The key belongs to the connection rather than the
peer, which is what `PeerSession` is.

**The identity key is wrapped before it reaches the disk**, by DPAPI on Windows and a
Keystore-held AES key on Android.

**Pairing takes two steps.** Showing the code says somebody was invited; comparing the fingerprint
on both screens says it is the right somebody.

**Open source.** GPL-3.0, renamed to `dev.meshsync.app`, CI, and a threat model that says what is
*not* covered.

**Three features.** File transfer, find my device, notification mirroring.

### What has not been near a phone

Everything since the identity work, except the Windows DPAPI migration - that was verified end to
end on a real key file: 138 bytes plaintext became 362 wrapped, same fingerprint, paired device
intact, and the next run read it back without rewriting.

The rest is tested over loopback and against its own protocols, which is real coverage and is not
the same as a radio. **The first thing to do with a phone attached is a clean pair**, because the
application id changed and Android will treat this as a different app.

There is no accessibility grant to restore any more - that service is gone. What needs checking on
a device instead is that the foreground service comes up on its own after a reboot, and that the
Quick Settings tile appears and sends.

---

## Where things stand

The whole plan in `.lavish/ble-standby-build-order.html` is implemented.
The solution builds with **0 warnings** and passes **205 tests**, up from 57 when this began.

The shape of the project changed completely.
It was a phone that dialled one hardcoded laptop, both sharing a single key baked into the source.
It is now a named mesh of equal devices that authenticate each other by keypair, hold a link per
peer, and treat Bluetooth as the standing link with Wi-Fi raised on demand.

### What changed, in order of how much it matters

**Every install no longer shares one key.**
`DeriveKey("MasterPassword123", "Salt")` is gone from both sides.
Each device has a persisted P-256 keypair, and each *connection* agrees its own AES-256 key - see
the session above, which moved this on again from the per-pair key described here.
A listener refuses a peer it has not paired with, rather than accepting anything that reaches it.

**Bluetooth is the standing link.**
Held continuously whenever a peer is in range.
Wi-Fi is raised on screen-on and dropped on screen-off, so the socket is down all night instead of
heartbeating through it.

**There is a real foreground service.**
There was none before, only an ongoing notification with no `startForeground` behind it.
`connectedDevice` type, which is what makes holding a Bluetooth link through Doze plausible.

**Roles are negotiated on both tiers.**
Every device listens and dials. Wi-Fi collisions are settled by fingerprint; Bluetooth roles by
capability first and fingerprint second.
Windows gained a GATT client and Android a GATT server, so neither platform is stuck on one half.

**A session per peer.**
Listening moved out of `TcpTransportConnection` into `TcpAcceptor`, so a second peer joins instead
of evicting the first, and `MeshLinks` fans out on send.

**The mesh has a name.**
"Surya's Mesh" rather than "connected to MSI-SURYANSHU".
It rides in the pairing code and in both hellos.

**A DHCP lease change no longer breaks pairing.**
Devices announce their address to each other over whichever link is up.

### Verified on hardware this session

| Thing | Evidence |
|---|---|
| Identity generates and survives a restart | Same fingerprint across two runs, `device.key` on disk |
| Pairing, and a Wi-Fi link both ways | `Peer identified as "MSI-SURYANSHU"` / `"S21 FE"` |
| Bluetooth identity exchange, both directions | Both sides log the other's fingerprint |
| Android GATT server advertising | `[BlePeripheral] Advertising started` |
| Bluetooth role rule choosing correctly | Laptop `3B7F…` took peripheral, phone `4CF6…` central, unprompted |
| Wi-Fi glare resolution | `Two links to 3B7F…; keeping the existing one` |
| Both tiers held at once | Notification read `Wi-Fi and Bluetooth` |
| Foreground service running | `Foreground service running; the links are held by it now` |
| Mesh name adopted by an already-paired device | `[Peers] Mesh name set to "Surya's Mesh"` |
| Address handover | `Announced this computer at 192.168.0.104` |

### Not verified

Everything in the table above was confirmed on hardware **before** the security and features work.
Nothing since has been near a phone, so the whole list below is open, and the first four items in
particular are things only a radio can answer.

- **A pair under the new handshake.** Forward secrecy changed both wire formats and both ends must
  update together. Loopback proves the agreement; it does not prove two radios agreeing.
- **The fingerprint comparison, end to end.** Confirming on one device and watching the other
  connect on its next retry.
- **The Android Keystore wrap.** It compiles. The Windows half was verified on a real key file;
  this half has not been run.
- **A file crossing between two real devices**, and landing in Downloads through MediaStore.
- **Ringing a phone that is face-down on silent**, which is the case the alarm stream exists for.
- **Notification mirroring**, including whether dismissal really round-trips.
- **Three devices at once.** The code holds a link per peer and fans out, but that path has only
  been exercised by tests over loopback.
- **A phone acting as peripheral carrying real traffic.** The GATT server advertises and accepts
  connections, confirmed on device, but with one phone the role rule correctly makes it the
  central, so nothing has crossed that link.
- **Overnight Doze survival** of the foreground service.
- **Screen-off actually dropping Wi-Fi** and screen-on raising it, watched end to end.
- **The wake frame** carrying a real image from laptop to phone.

---

## Hard-won findings

These cost real time to isolate.
None of them are guessable from the documentation.

### From the security and features work

**A session key that belongs to the peer quietly breaks revocation.**
The old key lived in a cache the registry could clear, so forgetting a device stopped it syncing
at once.
A session holds its own copy, so without an explicit check a forgotten device keeps working until
its link happens to drop.
`PeerSession.IsUsable` asks the registry on every payload for exactly that reason.

**A hardcoded wire version in a test makes it pass for the wrong reason - again.**
This is the second time. Bumping to 3 broke the same reassembly test, because a version mismatch
drops a connection in precisely the way the test is trying to provoke.
The constant is `internal` now and the test reads it, so the copy cannot go stale a third time.

**Refusing to load a key file is not the same as being allowed to replace it.**
The first attempt returned null for both, so a wrapped key that could not be unwrapped - a
Keystore briefly unavailable, say - was overwritten by a fresh identity.
A test caught it. There are two outcomes, and only one of them may touch the file.

**An empty file never completes if completion is driven by chunks.**
There is no chunk coming, because there is nothing to put in one.
The transfer finishes at the offer instead. Another one a test caught rather than a person.

**`SizeBytes` was an `int`.**
Fine for a clipboard item, which cannot reach two gigabytes. A video can, and it would have been
reported as a negative size.

**WinForms makes `Timer` ambiguous too**, not only `Brush` and `MessageBox`.
`Android.Media.Stream` collides with `System.IO.Stream` on the other side.
Both are qualified where they appear.

**`System.Security.Cryptography.ProtectedData` is in the SDK now.**
Referencing the package explicitly produces `NU1510` rather than working quietly, which costs the
zero-warning bar.

**The accessibility service made the phone refuse to take payments.**
UPI and banking apps in India - BHIM among them - detect any enabled accessibility service and
block until it is turned off, because that is the route screen-reading fraud takes.
So the app was not merely asking for a frightening permission, it was actively breaking something
the owner needs far more than clipboard sync.
It has been removed, and sending from the phone is user-initiated now.

**It was also hosting three things that had nothing to do with it.**
The screenshot observer, the network watcher and the screen watcher all lived in the accessibility
service, and none of them needed accessibility.
Declining that one permission silently cost screenshot sync, reconnect-on-network-change and the
screen-following Wi-Fi logic as well.
They belong to the foreground service now.

**And it was quietly the boot-persistence mechanism.**
Android rebinds an enabled accessibility service on boot, and that service started everything
else, so nothing ever needed a `BOOT_COMPLETED` receiver.
Removing it would have shown up as a phone that stopped syncing after a restart until the app was
opened by hand - and would have looked like a transport bug rather than a missing receiver.

**Play Store is no longer closed, which was not the reason for any of this.**
The accessibility service was the single blocker, and it is gone.
Worth revisiting whether that changes the distribution decision, which was made because of it.

**iOS can never be a peer**, for two separate reasons.
Background pasteboard reads have returned nil since iOS 9, and a backgrounded app's advertised
service UUIDs move to Apple's overflow area, which Windows and Android cannot see.
`BleRoleRules` already handles the second one: iOS declares `Central` only, exactly like an
Android phone without advertising hardware.

**GNOME Wayland has no background clipboard access.**
KWin and the wlroots compositors implement `wlr-data-control` and its successor
`ext-data-control-v1`; Mutter implements neither, and GNOME is the default on Ubuntu, Fedora and
Debian. A companion GNOME Shell extension is the way out.

### Identity and pairing

**Pairing carries one key in one direction, so the scanned side needs a pairing window.**
The QR shows one device's public key and the other scans it.
That lets the scanner authenticate us and gives us nothing.
So showing the pairing code *is* the signal that a stranger was invited.

**Sort the fingerprints before mixing them into the key derivation.**
Unsorted, the two ends derive different keys from the same shared secret and every payload fails
to decrypt, with nothing on the wire to say why.

**A device must refuse its own public key**, or it agrees a secret with itself and echoes its own
clipboard back for ever.

**Which key decrypts a payload is also the answer to who sent it.**
AES-GCM authenticates, so a payload that opens under a peer's key could only have come from that
peer.
That is what lets Bluetooth identify a sender without carrying identity in every frame.

**A rejected dialler briefly believes it succeeded.**
Refusal happens when the listener reads the hello, which is after the socket is open.
Tests must assert on the durable outcome, not on what the dial returned.

**A name that reaches a device only at pairing time never reaches one already paired.**
The mesh name went in the QR code first, and every device that had paired before that shipped sat
there calling it "your mesh" for ever.
It is in both hellos now, adopted only when the receiving device has no name of its own.

### Bluetooth

**A GATT attribute value is capped at 512 bytes, whatever the MTU says.**
Windows reports `MaxNotificationSize` as MTU minus the ATT header, which is 514 on a 517 MTU.
Bisected on device: a 512-byte chunk arrives, a 513-byte chunk never does, with no error.

**Windows keeps one outstanding notification per characteristic.**
Chunks sent back to back overwrite each other; a 128-chunk message arrived as its last chunk alone.
Fixed with a four-byte receipt per chunk in our own protocol.

**Indications look like the right answer and are not.**
Acknowledged at the ATT layer, which is exactly the flow control needed, but on this stack the
confirmations never arrived and Windows tore the link down with `GATT status 19`.

**Notifying a characteristic reaches every subscriber.**
Invisible with one phone. With two it hands each of them the other's traffic and lets either
answer the receipt the sender is waiting on.
Use the per-client overload and track which subscriber is which device.

**Length alone stops discriminating frames as soon as one of them is variable.**
An identity exchange is about 120 bytes, squarely in the data range.
It borrows the one value a data chunk's message id can never be: zero.
The counter used to wrap straight through zero after 255 messages, so that had to be fixed before
the marker meant anything.

**Android silently throttles BLE scanning.**
More than about five start/stop cycles in thirty seconds and the scan returns nothing, with no
error and no callback.
Holding the link rather than rebuilding it per use keeps the rate down.

**Advertising is a hardware capability on Android; scanning is not.**
`BluetoothAdapter.BluetoothLeAdvertiser` is null on devices without peripheral support, which is
why role negotiation is capability-first.

**Declaring `BLUETOOTH_ADVERTISE` is not requesting it.**
It is a runtime grant on Android 12+, and the failure is quiet: the advertiser throws a
`SecurityException` naming the permission, which gets logged and swallowed, so the phone simply
never becomes findable and nothing says why.
Scan and connect had been granted long ago; advertising had never been needed until the phone
could take the peripheral role.

**Android caches a device's service and handle table across connections.**
`BluetoothGatt.refresh()` clears it, but it is not in the SDK and needs reflection.

**Killing the desktop process orphans its GATT registration.**
The phone keeps discovering the orphan: it connects, subscribes, both ends report success, and
nothing crosses.
Quitting gracefully recovers; a crash or a Task Manager kill needs the adapter toggled.
This costs the standing link now, not just the fallback.

**"Ready" is not proof of a usable link.**
Android reports the subscription write as successful even against a dead service, so the link must
answer a ping before it is reported connected.

**No OS-level bonding is used or needed.**
Both characteristics are `GattProtectionLevel.Plain` and peers are found by service UUID, so
"forget this device" in Bluetooth settings changes nothing.

### Networking

**`TcpClient.ConnectAsync` has no default timeout.** With no route it waited over two minutes.
Bounded to five seconds.

**A connect timeout raises `OperationCanceledException`.**
Catching that as "the caller cancelled us" meant a phone with Wi-Fi off never reached the fallback.

**Mobile data counts as "a network".**
Check for a Wi-Fi or Ethernet **transport** specifically; cellular can never route to a LAN address.

**TCP is a byte stream with no message boundaries.**
The original framing read the length prefix with a single `ReadAsync` and trusted it, which
desynchronises the stream permanently the first time a read returns short.

**A dual-stack listener reports IPv4 peers as `::ffff:192.168.0.103`.**
That parses as an address, reads perfectly well in a log, and can never be dialled back.
Seen as a connect timeout against a device that was plainly right there.
Unwrapped both where addresses are recorded and where they are dialled, so a stored one self-heals.

**A heartbeat is not free, but an idle socket is.**
The interval was 10s, chosen for fast drop detection before anything weighed the cost.
For comparison, the push service every app on the phone shares heartbeats about every 15 minutes,
and most of that is holding a NAT mapping open across the internet, which does not apply on one
subnet.
Now 30s with a 90s timeout.

**A hardcoded protocol version in a test makes it pass for the wrong reason.**
Bumping the wire version broke one transport test and should have broken two: the other asserted
that a bad frame drops the connection, which a version mismatch also does.

### Clipboard

**`EchoSuppressor.IsEcho` must not consume its entry.**
Both platforms raise several notifications per copy, so consuming on the first check let the
second look like a genuine user copy.

**Images cannot be matched by content hash.**
Windows decodes a received JPEG and re-encodes it on capture, so the bytes never match.

**MediaStore publishes a row when the file is created, not when it is written.**
Wait for `IS_PENDING` to clear and the byte count to match. Deduplicate on the row id.

**Holding two links at once introduces double delivery.**
A device with both tiers up to the same peer receives every copy twice unless Bluetooth is skipped
for peers Wi-Fi already reached.

### Android platform

**`ACTION_SCREEN_ON` and `ACTION_SCREEN_OFF` cannot be declared in the manifest.**
Android only delivers them to receivers registered at runtime.
Such a receiver must *not* carry `[BroadcastReceiver]` either: the attribute writes a `<receiver>`
element, which then demands a public default constructor and fails the build with `XA4213`.

**A receiver registered only for protected system broadcasts needs no exported flag.**
`ActivityFlags` has no `ReceiverNotExported` member in .NET for Android, and reaching for it is a
compile error rather than the hint that the flag was unnecessary.

**A foreground service must call `startForeground` within seconds of being started**, so the
notification is posted before anything else that can fail.

**Starting a foreground service from the background is refused on Android 12+.**
Receiving `BOOT_COMPLETED` is one of the exemptions, which is what makes the boot receiver the
right place to do it. It is also started from the activity, and a refusal is logged rather than
thrown - the app still works when the user next opens it.

**Stopping a foreground service is what removes its notification.**
Cancelling the notification directly does nothing while the service is up.

**`SetupComplete` and "is paired" used to mean the same thing and no longer do.**
Pairing lives in the peer registry now, so a device could finish setup and hold no peers, which the
dashboard reported as "not paired" with no way out of it.

### UI

**The MAUI template's purple was still in `colors.xml`.**
`#512BD4` had never been changed, so opening the drawer tinted the status bar against a warm
off-white and near-black palette.
`colorPrimary` is the band behind the drawer, and it is set to the page background so the bar
disappears into the page.

**WPF: reassigning colour keys does not repaint anything.**
Swap the whole merged dictionary instead.

**MAUI's template `Styles.xaml` sets an implicit `BoxView.BackgroundColor`**, separate from
`Color`, so transparent spacers rendered as beige bars.

**`HorizontalAlignment` inside a `FrameworkElement` resolves to the property, not the enum.**
Qualify it as `System.Windows.HorizontalAlignment.Center`.

**WinForms is referenced for the tray icon**, so `Brush` and `MessageBox` are ambiguous in WPF
code and need qualifying.

---

## Testing gotchas

**Global mutable state breaks test isolation before it breaks anything else.**
The pairing window was static, so one test class opening it made another class's "a stranger is
refused" fail intermittently under xUnit's parallel execution.
It belongs to `PeerSecurity` now, which is better design and fixed the tests as a side effect.

**Several devices on one machine cannot share a listening port**, so `MeshLinks` accepts a
`host:port` address. In the field every device is on 45001.

**An incremental build does not re-report warnings.**
Use `-t:Rebuild` when you need to be sure of the zero-warning bar.

**`adb install -r` preserves the identity and the paired devices. `pm clear` wipes both.**

```powershell
adb shell run-as dev.meshsync.app ls /data/data/dev.meshsync.app/files
```

**Kill the daemon before building**, or the build fails with `MSB3021` on a locked `CoreLib.dll`.

**This Samsung blocks adb Wi-Fi control.** Wi-Fi has to be toggled by hand for fallback testing.

**Do not blind-tap the phone.** Screenshot, locate the target, then tap.

**`dumpsys notification` includes history**, so grepping for the package matches notifications that
are already gone. `--noredact` and reading `android.title` for the live record works.

---

## Open decisions

**The app name.**
"Mesh Sync" throughout, including the brand assets, and it has grown more apt: it really is a mesh
now rather than a phone and a host.
Renaming would touch namespaces, the `meshsync://` scheme, tray text and registry keys.

**`TcpDiscoveryService` is unused and should probably be deleted.**
Address handover over an existing link does the job better: no multicast, and it works on networks
with client isolation.

**Introduction is designed but not surfaced.**
`PeerRegistry.PeersToIntroduceTo` exists so a new device can learn the set from one scan instead of
one scan per pair. It needs a confirmation step in the UI before it should be wired up.

**Connection state is per app, not per peer.**
Both apps know whether *anything* is reachable rather than which peers are, so a device list can
only mark one device connected. That is the next thing to grow when a third device arrives.

**Renaming the mesh does not propagate.**
It travels on joining only. A last-changed timestamp in the hello would fix it.

---

## Commands

```powershell
# Windows daemon
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project src/WinDaemon/WinDaemon.csproj

# Android: build and install, preserving app data
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/dev.meshsync.app-Signed.apk

# Tests
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

**Logs are the fastest way to see what is happening.**

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
adb logcat -s MeshSync                                               # Android
```

Identity and pairing live beside the log: `%LOCALAPPDATA%\MeshSync\device.key` and `peers.json` on
Windows, the app-private files directory on Android.
Deleting either forces a re-pair.

---

## Layout

```
src/CoreLib/
  Identity/           DeviceIdentity, PeerRegistry, PeerSecurity, PairingWindow
  Transport/          TcpAcceptor, TcpTransportConnection, MeshLinks, SyncContent,
                      BleFragmenter, BleProtocol, BleRole
src/WinDaemon/        WPF window with sidebar and device list, GATT server and client,
                      clipboard worker, tray
src/AndroidClient/    MAUI app with a navigation drawer: Home, Activity, Devices, Settings,
                      About, plus the setup wizard. Foreground service hosting the screenshot,
                      network and screen watchers; boot receiver; notification listener; ringer;
                      GATT client and server; Quick Settings tile, PROCESS_TEXT and share targets
src/assets/           brand handoff: SVG, PNG, style sheet
tests/CoreLib.Tests/  205 tests: crypto, key agreement, identity, key storage, the registry,
                      and mesh links over real loopback sockets
```

`TrustManager` and `WindowsBleDiscovery` were deleted rather than left alongside their
replacements.
The first minted a fresh keypair on every construction and nothing on the wire consulted it; the
second scanned for manufacturer data nothing published and logged through `Console.WriteLine`.
A second and broken version of something is worse than not having it.
