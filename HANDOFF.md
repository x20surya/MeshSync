# Handoff notes

Written at the end of a long session so the next one can pick up without rediscovering anything.
Read this alongside [AGENTS.md](AGENTS.md), which describes the architecture and the rules.

---

## Where things stand

Everything below is committed on `master`, builds with **0 warnings and 0 errors**, and passes **57 tests**.
`master` was **8 commits ahead of `origin/master`** when this was written, so a push is probably wanted.

Two transports work end to end and were verified on real hardware, not just in tests.
Wi-Fi carries text and images.
Bluetooth carries text with no network of any kind, which is the point of it.

### Verified on device

| Thing | Result |
|---|---|
| Wi-Fi text and image sync, both directions | works |
| Screenshot beaming, phone to laptop | works, byte-exact |
| BLE text sync with Wi-Fi off, both directions | works, byte-exact over 14 chunks |
| Failover, Wi-Fi drops to Bluetooth carrying traffic | 1.3s |
| Cold start to Bluetooth, Wi-Fi already off | 3.2s |
| BLE throughput, laptop to phone | about 6.7 KB/s, 64 KB in 9.3s |
| BLE throughput, phone to laptop | much faster, writes pipeline properly |

### Not verified

- The Wi-Fi-preferred path after the `HasUsableNetwork` change.
  adb cannot toggle Wi-Fi on this Samsung, so this was only exercised with Wi-Fi off.
- The Android **Activity** and **About** pages, visually.
  They share the layout pattern of the pages that were checked.
- Wizard steps 2 and 3, visually.

---

## Hard-won findings

These cost real time to isolate.
None of them are guessable from the documentation.

### Bluetooth

**A GATT attribute value is capped at 512 bytes, whatever the MTU says.**
Windows reports `MaxNotificationSize` as MTU minus the 3-byte ATT header, which is 514 on a 517 MTU.
That is two bytes optimistic and anything over 512 is dropped with no error on either side.
Bisected on device: a 512-byte chunk arrives, a 513-byte chunk never does.

**Windows keeps one outstanding notification per characteristic.**
Sending chunks back to back overwrites each one before it is transmitted.
A 128-chunk message arrived as its last chunk alone.
Fixed with a four-byte receipt written back per chunk, in our own protocol.

**Indications look like the right answer and are not.**
They are acknowledged at the ATT layer, which is exactly the flow control needed, but on this stack the confirmations never arrived and Windows tore the link down with `GATT status 19`.

**Android silently throttles BLE scanning.**
More than about five start/stop cycles in thirty seconds and the scan simply returns nothing, with no error and no callback.
This is why Bluetooth appeared to connect only by luck or only after the service was restarted.

**`BLUETOOTH_SCAN` needs `neverForLocation`.**
Without it Android 12+ ties scanning to location: it asks for a location permission the app has no use for, and returns no results whenever location services happen to be off.

**Android caches a device's service and handle table across connections.**
If the peripheral republishes its service, the phone keeps writing to handles that no longer exist.
`BluetoothGatt.refresh()` clears it, but it is not in the SDK and needs reflection.

**Killing the desktop process orphans its GATT registration.**
The phone then keeps discovering the orphan: it connects, subscribes, both ends report success, and nothing crosses in either direction.
Quitting gracefully recovers perfectly; a hard kill does not, and no amount of client-side retrying gets past it.
Bluetooth is now released on `ProcessExit` and on Windows signing out, which covers every path user code can intercept.
**A crash or a Task Manager kill still orphans it.** Recovery needs the Bluetooth adapter to be toggled.

**"Ready" is not proof of a usable link.**
Android reports the subscription descriptor write as successful even when it lands on a dead service.
The phone now requires a ping to be answered before reporting the transport connected.

### Networking

**`TcpClient.ConnectAsync` has no default timeout.**
With no route it waited over two minutes before failing, which is two minutes before the Bluetooth fallback was even attempted.
Bounded to five seconds.

**A connect timeout raises `OperationCanceledException`.**
Catching that as "the caller cancelled us" meant a phone with Wi-Fi already off never reached the fallback at all.
The catch now guards on the loop's own token.

**Mobile data counts as "a network".**
Asking whether any network exists answers yes on cellular, which can never route to a private LAN address.
Check for a Wi-Fi or Ethernet **transport** specifically.

**TCP is a byte stream with no message boundaries.**
The original framing read the length prefix with a single `ReadAsync` and trusted it, which desynchronises the stream permanently the first time a read returns short.
This was the original "connection keeps dropping" bug.

### Clipboard

**`EchoSuppressor.IsEcho` must not consume its entry.**
Both platforms raise several clipboard notifications per copy, so consuming on the first check let the second look like a genuine user copy and bounce content back, ping-ponging between the devices.

**Images cannot be matched by content hash.**
Windows decodes a received JPEG to a bitmap and re-encodes it on capture, so the bytes coming back never match what was stored.
Observed as a 16 KB screenshot arriving and a 49 KB re-encode going straight back out.
A short kind-scoped guard covers that window.

**MediaStore publishes a row when the file is created, not when it is written.**
An immediate read returns partial bytes; a capture was seen arriving as 62 bytes.
Wait for `IS_PENDING` to clear and for the byte count to match the reported size.
Deduplicate on the **row id**, not the URI text, which is not spelled the same way for every notification.

### UI

**WPF: reassigning colour keys does not repaint anything.**
A brush already handed to a rendered element does not re-resolve when the colour behind it changes.
The setting saved and logged correctly, which made it look like the click was being missed.
Swap the whole merged dictionary instead: `Palette.Light.xaml` and `Palette.Dark.xaml` hold finished brushes.

**MAUI's template `Styles.xaml` sets an implicit `BoxView.BackgroundColor`.**
It is separate from `Color`, so `Color="Transparent"` spacers rendered as beige bars.

**`HorizontalAlignment` inside a `FrameworkElement` resolves to the property, not the enum.**
Qualify it as `System.Windows.HorizontalAlignment.Center`.

---

## Testing gotchas

**`adb install -r` usually preserves the accessibility grant. `pm clear` never does.**
Do not use `pm clear`; reinstall over the top.
An install occasionally drops it anyway, so check before trusting a result:

```powershell
adb shell settings get secure enabled_accessibility_services
```

If it is off, the user has to re-enable it by hand in Settings, and readings taken before then are meaningless.

**Kill the daemon before building**, or the build fails with `MSB3021` on a locked `CoreLib.dll`.
It relaunches on its own because run-on-startup is enabled.

```powershell
Get-Process WinDaemon -ErrorAction SilentlyContinue | Stop-Process -Force
```

**This Samsung blocks adb Wi-Fi control.**
Neither `svc wifi disable` nor `cmd wifi set-wifi-enabled disabled` takes effect.
Wi-Fi has to be toggled by hand for fallback testing.

**Do not blind-tap the phone.**
Screenshot, locate the target, then tap.
A missed tap once surfaced the user's browser and captured a verification code, which had to be deleted.

**`dumpsys notification` includes history.**
Grepping for the package matches records of notifications that are already gone, so it reports present when it is not.
Confirm visually.

---

## Open decisions

**Pairing crypto is not implemented, and the repository is public.**
Both sides derive from a literal `DeriveKey("MasterPassword123", "Salt")`, so every install shares one key and the listener accepts any LAN connection.
The ECDsa keypair is generated, shown in the QR, scanned and stored, then never consulted.
This is the largest outstanding item.

**No Android foreground service.**
The socket is held only by the accessibility service, so Doze will eventually kill it.
The heartbeat and backoff make recovery fast, but this is the structural fix.

**The app name is undecided.**
Currently "Mesh Sync" throughout, including the brand assets.
BLE rules out the `Clip-` names since there is no wire; `Splice`, `Seam`, `Tandem` and `Nearsync` were the surviving candidates.
Renaming touches namespaces, the `meshsync://` deep link scheme, tray text and registry keys.

**Discovery is implemented but not wired to pairing.**
`TcpDiscoveryService` works on both sides and nothing consumes it, so a DHCP lease change still breaks pairing until the QR is rescanned.

**Images over Bluetooth are refused by design.**
Measured throughput says a compressed screenshot would take roughly 25 seconds, not the minutes originally assumed.
Feasible if wanted, with harder downscaling and a progress indicator.

---

## Commands

```powershell
# Windows daemon
dotnet run --project src/WinDaemon/WinDaemon.csproj

# Android: build and install, preserving app data
dotnet build src/AndroidClient/AndroidClient.csproj -t:SignAndroidPackage -f net10.0-android
adb install -r src/AndroidClient/bin/Debug/net10.0-android/com.companyname.androidclient-Signed.apk

# Tests
dotnet test tests/CoreLib.Tests/CoreLib.Tests.csproj
```

**Logs are the fastest way to see what is happening.**
`Console.WriteLine` writes nowhere from a WinExe, so everything goes through `CoreLib.Diagnostics.Log`.

```powershell
Get-Content "$env:LOCALAPPDATA\MeshSync\daemon.log" -Wait -Tail 20   # Windows
adb logcat -s MeshSync                                               # Android
```

---

## Layout

```
src/CoreLib/          transport, crypto, echo suppression, activity log, diagnostics
  Transport/          TcpTransportConnection, BleFragmenter, BleProtocol, discovery
src/WinDaemon/        WPF window with sidebar, GATT server, clipboard worker, tray
src/AndroidClient/    MAUI wizard and dashboard, GATT client, accessibility service,
                      PROCESS_TEXT and share targets
src/assets/           brand handoff: SVG, PNG, style sheet
tests/CoreLib.Tests/  57 tests, real loopback sockets for the transport
```

The BLE stacks were nearly deleted early on as dead code.
They were kept because Bluetooth turned out to be a planned tier, and they are now the working Bluetooth transport.
