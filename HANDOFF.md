# Handoff notes

Written at the end of a long session so the next one can pick up without rediscovering anything.
Read this alongside [AGENTS.md](AGENTS.md), which describes the architecture and the rules.

---

## The most recent session, in short

**v0.2.3: eighteen connection defects in the Linux and Mac head, and the shared arbitration that
replaced them.**
286 tests, up from 252; every project builds with no warnings.

The Bluetooth tier ran both halves and never asked which one should exist, so two devices in range
each dialled the other, both links stayed up, and the clipboard crossed twice.
`BleRoleRules` was named in five comments in `DesktopCore` and called by none of them.
Windows had gated its scan loop on it all along and Android repaired the collision afterwards; all
three now go through one `BleLinkArbiter` in `CoreLib`.

The worst of them was not a divergence from Windows at all: awaiting a `Console.In` read stopped
the entire Bluetooth tier dead while logging nothing.
See the finding below, which is the one to read first if any of this ever seems to come back.

Six of the eighteen were mechanisms sitting in `src/WinDaemon` with no platform dependency in them
that the Linux head had reimplemented differently or not at all, so `LinkState`, `TransportSettings`
and `BleLinkArbiter` moved into `CoreLib` and the Windows copies were deleted.
Verified against a phone over Bluetooth with no network between the two, and by 34 new tests
covering rules the old suite could not reach.

An earlier session is summarised below, and the findings from every session are in **Hard-won
findings**.

### The session before that

The whole plan in `.lavish/mesh-sync-plan.html` was executed as far as phase 5.
205 tests at the time; both apps build with no warnings.

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

### It has now been near a phone

Everything above was taken to hardware on 2026-08-20 against the S21 FE, and the tables further
down record what held and what did not.
Four defects came out of it that no test could have found, all fixed: the Quick Settings tile never
actually read the clipboard, nothing worked over Wi-Fi while the phone was the hotspot, ringing
could not vibrate, and notification mirroring had no logging to diagnose itself with.

The clean pair the previous note asked for was done, and the Android Keystore wrap came through it
intact.
The accessibility grant is gone for good and nothing needs restoring in its place.

The reboot was done too: the phone came back, the boot receiver started the service unaided, and
the Keystore unwrapped the identity on the first run after a cold boot.
What is still open is Doze survival overnight, which only time can answer.

---

## Where things stand

The whole plan in `.lavish/ble-standby-build-order.html` is implemented.
The solution builds with **0 warnings** and passes **286 tests**, up from 57 when this began.

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

### Verified on hardware, before the security work

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

### Verified on hardware after the security and features work

Run on 2026-08-20 against the S21 FE (`AC83-492B-684F-4263`) and this laptop
(`3B7F-5889-CC1C-09B4`), with the phone acting as the hotspot.

| Thing | Evidence |
|---|---|
| The forward-secret handshake between two radios | Both ends re-identify on every reconnect, over Bluetooth and TCP |
| The Android Keystore wrap | `device.key` begins `MSK1`; the fingerprint is unchanged across restarts, force-stops and reinstalls |
| Clipboard, desktop to phone | `[Sync] Received text payload`, and the text pasteable on the phone |
| Clipboard, phone to desktop, share sheet | `[Share] Sent 22 characters from the share sheet`, matched by `Get-Clipboard` |
| Clipboard, phone to desktop, selection menu | `[ProcessText] Sent 23 characters`, matched by `Get-Clipboard` |
| Clipboard, phone to desktop, Quick Settings tile | `[Clipboard] Sending 16 characters from the clipboard`, after the focus fix below |
| An image crossing over Wi-Fi | 11853 bytes sent, 11853 received, cached as `clip_*.jpg` |
| A file crossing, and landing in Downloads through MediaStore | Identical SHA-256 on both sides, 12000 bytes |
| Ringing the phone from the daemon | `[Ring] MSI-SURYANSHU asked this phone to ring`, and `[Ring] Stopped ringing.` |
| Notification mirroring, phone to desktop | `[Notify] Mirroring a notification…` then `[Notify] Mirrored a notification from S21 FE` |
| Notification dismissal, both directions | Dismissing on the desktop cleared it on the phone, and the phone's removal cleared it on the desktop |
| The phone as hotspot raising the Wi-Fi tier | `[Sync] Acting as a hotspot on swlan0`, then `[Transport] Peer identified` |
| Address handover both ways | Each side's `peers.json` now holds the other's address, and the daemon dialled the phone |
| Recovery after being force-stopped | The process came back unaided and re-established the link |
| Browsing a phone's shared folders from the desktop | The listing came back with real sizes and dates |
| Fetching a file the desktop asked for | `[Files] Received "screenshot.png", 18 KB` and saved to Downloads |
| A browse against a peer that does not speak it | Times out, says so, and leaves the link unharmed |
| Delivering an 81.8 MB APK over the mesh | Sent in under two seconds over the phone's own hotspot |
| Boot persistence, on a real reboot | `[Service] The phone restarted; bringing the links back.`, unprompted |
| The Keystore unwrap after a cold boot | `[Sync] Identity AC83-492B-684F-4263, 1 paired device(s)` on the first run after restart |
| A hotspot subnet change surviving | The subnet moved from `10.178.251.x` to `10.137.49.x` across the reboot and both sides re-announced and reconnected |

### Found on hardware and fixed

These are the four defects the radios found, none of which any test could have.

**The Quick Settings tile never read the clipboard.**
It read in `OnCreate`, and Android only answers a clipboard read to an app that already holds
window focus, which an activity does not during `OnCreate`.
The read does not throw: it returns an empty clip, indistinguishable from the clipboard genuinely
being empty, and the empty path logged nothing at all.
The tile appeared to work and silently sent nothing.
Reading now happens in `OnWindowFocusChanged`, and the read is separated from the send so that
waiting for a link cannot cost the focus the read depends on.

**Nothing worked over Wi-Fi while the phone was the hotspot.**
`HasUsableNetwork` asked for the active network's transport, and on a phone sharing its connection
that is cellular, because that is how the phone reaches the internet.
Tethering is not surfaced as a Wi-Fi transport at all.
So the check answered "no usable network" in the one topology where the peer was a single hop away,
and images and files fell back to Bluetooth or were dropped as not worth encrypting.
It now falls through to the interface list and recognises an access-point interface.

**Ringing could not vibrate.**
`android.permission.VIBRATE` was missing from the manifest.
The sound still played, so this was invisible until a real ring was tried, and Android throws a
`SecurityException` for the vibrate call rather than ignoring it.
The buzz is the half that finds a phone face-down in a sofa.

**Notification mirroring had no logging on any path.**
A listener that Android had not yet bound looked exactly like mirroring being broken.
There is now one line when a notification is mirrored, one when it goes away, and one when a
notification is dropped because nothing is connected.

### Added after the hardware run

**Notification mirroring is on by default and muted per app.**
Deny-by-default plus an empty allow list meant three opt-ins before anything appeared, which is
indistinguishable from the feature being broken.
The listener grant is the real gate; the mute list is what banking and authenticator apps are for.
Old settings are dropped on read rather than translated - an allow list of three apps is not a mute
list of every other one.

**Mirrored notifications reach the Windows notification centre.**
An AppUserModelID registered under `HKCU` is what makes toasts possible without an installer, and a
hashed tag is what makes them removable when the phone dismisses one.
Verified by watching `LastNotificationAddedTime` advance under the app's own id on every send.
Banners will not appear while Windows Do Not Disturb is on, which is a setting rather than a bug and
cost some time to work out.

**The phone displays notifications from the mesh** instead of only sourcing them, and a swipe there
tells the device it came from - through a delete intent, because the listener ignores this app's own
notifications and would never see it.

**The phone can send a file and open one that arrives.**
`SendFileAsync` had existed with nothing calling it.
Receiving had the matching hole: a file arrived and could not be reached from the app that received
it, because a file written through MediaStore has no path the app is allowed to know and the content
URI has to be kept at the moment of writing.

**Browsing a paired device's shared folders, both ways.**
`SharedFolders` is the whole security story: the wire carries a folder id and a relative path, never
a path, and the relative half is rejected, joined, resolved and then checked to still be inside the
folder it came from.
Downloads is shared out of the box on both sides.

### Still not verified

- **The phone's own Files page against the desktop.** The desktop-to-phone direction is verified;
  the reverse runs the same code from the other end but has not been tapped through.
- **The phone displaying a mirrored notification**, which needs a second phone or Windows-to-phone
  mirroring to have a sender at all.
- **Three devices at once.**
  The code holds a link per peer and fans out, but that path has only been exercised over loopback.
- **A phone acting as peripheral carrying real traffic.**
  With one phone the role rule correctly makes it the central, so nothing has crossed that link.
- **Overnight Doze survival** of the foreground service.
- **Ringing a phone that is face-down on silent**, which is the case the alarm stream exists for.
- **The wake frame** carrying a real image: the image that crossed went over a Wi-Fi link that was
  already up.
- **The Linux and Mac head against a phone.** Two Linux devices on one machine pair, agree a
  session key and exchange payloads both ways, which is the whole transport proven from Linux -
  but no phone has been on the other end of it yet.
- **The keyring on anything but KDE.** The wrap, the unwrap and the in-place upgrade of an
  existing plaintext key are all exercised against KWallet. gnome-keyring serves the same
  interface and has not been tried.
- **The Linux clipboard on X11.** The Wayland path is exercised: two processes round-trip text
  through the compositor and a copy crosses the mesh. `CommandLineClipboard` and the
  `wl-paste --watch` framing are the X11 fallback and have compiled without ever running, because
  no helper is installed on the development machine.
- **The Mac head running.** It cross-publishes from Linux to a real Mach-O arm64 binary with the
  Cocoa backend in it, and nothing has launched it.

### The Bluetooth link churns, and it is not ours

A standing Bluetooth link is dropped by Windows at almost exactly 30 seconds and immediately
re-established by the phone, which reports it as `status 19`, `GATT_CONN_TERMINATE_PEER_USER`.
It reconnects in about a second and nothing is lost, but the "standing" link is reconnecting
roughly a hundred and twenty times an hour.

The phone is not the cause: its heartbeat has a 24 second timeout that never fires, so traffic is
crossing right up to the moment the radio goes.
`GattSession.MaintainConnection` on each subscribed client was the obvious fix, is what the central
half already does, and does not stop it - the flag is honoured for a link Windows dialled, not for
one it accepted.
Left in place because it is correct practice and harmless.
Whatever the real cause, it is below this code, and the reconnect path covers it.

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

### The Linux and Mac port

**`MeshLinks` used one port for two different things, and it reached itself.**
The constructor took a single `port` and used it both to bind the listener and as the port to
dial when a stored address carried none.
Those are the same number for every device in the field, because they all listen on 45001, so
nothing ever noticed.
Run two devices on one machine - which is the only way to exercise a third device without a third
piece of hardware - and the one listening on 45002 dialled its peer's bare address on 45002, which
is itself, and logged `Refusing a connection from this device's own identity` in a loop.
There is now a separate `peerPort` that defaults to `port`, so every existing caller is unchanged
and only the desktop head passes it.

**Every install shares one BLE service UUID, so a scan finds other people's meshes.**
Two meshes in one room is not a hypothetical: a laptop here held a Bluetooth link to a phone in
somebody else's mesh for as long as both were in range. The session was refused correctly - the
phone is not paired and never became so - but the link was reported as up the moment the GATT
characteristics resolved, the timeout only covered a peer that had gone quiet rather than one that
had never spoken, and a device that refused outright was retried four seconds later, forever.

A link now has twelve seconds to produce a session or it is dropped, and a device that refuses is
ignored for five minutes. Eleven connect attempts in ninety seconds became one. The asymmetry that
made it obvious is worth remembering: the Android end refused and stayed refused, so only the
desktop looked wrong.

**BlueZ keeps a device object for every LE address it has ever seen.**
A phone rotates its LE address for privacy, so most of those objects are ghosts that still carry
the service UUID they advertised at the time, and dialling one connects to an address that stopped
existing minutes ago. RSSI is the discriminator: BlueZ publishes it only while a device is being
seen in the current discovery session and drops it when the device goes away.

**`Console.In.ReadLineAsync` is not asynchronous, and it killed the whole Bluetooth tier.**
This is the worst one in the file, because nothing failed and nothing was logged.
`Console.In` is a *synchronized* `TextReader`: its async methods run the blocking read inline while holding the reader's monitor, so the await never yields and the thread it was called on stops servicing anything else.
In `LinuxDaemon` that thread was one the D-Bus connection needed, so the moment the shell asked for input the scanner wedged mid-handshake - BlueZ had been asked to watch for property changes and the reply was never read.
The symptom was a daemon that started cleanly, printed its banner, took commands, and simply never found a single device over Bluetooth, forever.
It is a race, which is why it looked intermittent: whether it wedges depends on whether the shell reaches its first read before `AddMatch` completes, and running with `--no-shell` or `--quiet` changes the timing enough to hide it.
`Shell.ReadLineOnItsOwnThread` now does the blocking read on a dedicated long-running thread and races it against the stopping token.
**The rule: never `await` a `Console.In` read in a process that has anything else going on.**

**Both halves of the Bluetooth tier ran at once with nothing deciding between them.**
Every install advertises the same service UUID and every device also scans for it, so two devices in range each dialled the other, both links came up carrying the same peer, and the clipboard crossed twice - the echo suppressor is on the sending side, so the receiver has no defence.
Windows had prevented this all along by gating its scan loop on `BleRoleRules`, and Android repaired it afterwards in `ResolveBleCollision`; the Linux head did neither, and the five comments in `DesktopCore` claiming the roles were "settled per link by BleRoleRules" described behaviour the code did not have.
The decision now lives in `CoreLib.Transport.BleLinkArbiter`, which all three call: `ShouldDialAnyPeer` before scanning, and `KeepFor` to settle a collision when two devices dial inside the same moment.
Advertising is never gated - only scanning - because a peer that cannot advertise depends on this device staying findable.

**Report a capability honestly or the arbitration is worse than useless.**
This box says it can advertise and then BlueZ refuses the exported GATT tree, so `LinuxBlePeripheral` stands aside.
Telling the arbiter `BleCapability.Both` anyway makes it answer "you advertise" - and the device then neither advertises nor scans, which is a deadlock rather than a degraded state.
`Daemon` sets the capability from whether the peripheral actually started, not from what the adapter claimed.

**The scan cadence was 4 seconds and ungated; it is 30 with a 12-second window now.**
That is the Windows interval, and discovery is stopped in a `finally` between rounds rather than started once and left running for the life of the process.
An active scan alongside a live link contends with it for the same antenna, which is most of why an established link felt rough rather than merely duplicated.
The old code also set `_discovering = true` inside the `catch`, so one transient refusal - an adapter powered off at launch - convinced the loop it was already scanning and it never tried again.

**A refusal has to be remembered against the identity, not only the address.**
The five-minute cooldown was keyed on the BlueZ object path, which encodes the LE address, and a phone rotating its address arrives under a path nothing has refused.
It cannot stop the connection, because nothing knows who a device is until its hello arrives, but refusing on the hello costs one second instead of the full twelve-second identity grace.

**D-Bus aligns every dict entry and every struct to eight bytes, and nothing here does it for you.**
This cost most of the keyring work. A hand-rolled `a{ss}` with two pairs is malformed, because the
second entry needs padding the writer has no API to emit - and dbus-daemon answers a malformed
message by closing the connection with nothing said. Reading has the same trap in reverse:
`Secret.Service.GetSecrets` returns `a{o(oayays)}`, whose struct values are 8-aligned, and reading
the fields one by one lands on the padding and produces an empty object path.
Both are avoided rather than solved: every dictionary written here holds exactly one entry, with a
second property set afterwards through the Properties interface, and the secret is read with
`Secret.Item.GetSecret`, which returns the struct alone at the start of the body where it is
aligned already.

**A D-Bus message writer is a struct, and handing it to an `Action<T>` throws the body away.**
Every BlueZ call that carried arguments closed the connection with "connection closed by peer" and
nothing else - because the header declared a signature while the body was written into a copy of
the writer that was then discarded, so the message promised bytes it did not have. Calls with no
arguments worked perfectly, which is what made it look like a marshalling problem in the arguments
themselves. The fix is a `delegate void MessageArgs(ref MessageWriter)`; the lesson is that a
silent disconnect from dbus-daemon means malformed, and the first thing to check is whether the
body was written at all.

**BlueZ rejects the exported GATT tree.** It calls `GetManagedObjects` on the application root and
closes the connection on the reply. `a{oa{sa{sv}}}` needs every dict entry aligned to eight bytes
and the writer does not appear to align successive entries. Until that is right the peripheral half
stands aside and the central half carries the tier.

**Bluetooth is where Linux and macOS have to part.** BlueZ is D-Bus, so plain `net10.0` reaches it.
CoreBluetooth is reachable only from `net10.0-macos` or `net10.0-maccatalyst`, which need macOS and
Xcode to build - so the moment the Mac head gets Bluetooth it stops being cross-buildable from
Linux. Keep `DesktopCore` and `DesktopShell` free of both, and split the Mac head out when its
radio is built.

**Wayland client object ids must be allocated densely, and freed ids are reissued at once.**
libwayland keeps client objects in a dense array and `wl_map_insert_at` refuses any id that would
leave a gap, so ids cannot be picked freely - jumping from 6 to 10 is rejected as
`invalid arguments for ext_data_control_manager_v1#4.create_data_source`, which reads like a
malformed request and is not.
The follow-on is worse: destroying an object to reclaim its id races the compositor reissuing it,
so a destroy sent from a background thread tore down the *newly issued* offer that had just taken
that id, and the read came back empty every second time.
Sources now count upwards and are never destroyed. One id per clipboard write is cheap; the race
is not.

**Nothing in the compositor's replies is optional to read.**
`wl_display.error` was being dropped on the floor, so a rejected request looked exactly like a
request that had worked and then quietly done nothing. Logging it turned a day's guessing into one
line naming the object and the reason.

**Avalonia has no native Wayland backend, which decides where the clipboard watcher lives.**
The shell runs through XWayland - its toplevel reports as `Avalonia.X11.X11Window` - and an
XWayland client cannot speak `ext_data_control_manager_v1`.
That protocol is the only way to read the clipboard in the background on Wayland; a plain client
can read it only while focused.
So Avalonia's clipboard API is right for sending and for applying what arrives, and the watcher
has to hold its own native Wayland connection.
KDE Plasma 6.6 advertises `ext_data_control_manager_v1` but not the older wlroots
`zwlr_data_control_manager_v1`, so target the standardised one.

**Avalonia 12 is not Avalonia 11, and most of what is written about it is 11.**
`AvaloniaLocator.Current` is gone.
`TextBox.Watermark` is now `PlaceholderText`.
The clipboard moved to an `IDataTransfer` model with the text helpers in
`Avalonia.Input.Platform.ClipboardExtensions`, and it is `TryGetTextAsync`, not `GetTextAsync`.
`Bitmap.Save(string, int?)` is obsolete in favour of the `BitmapEncoderOptions` overload.
A window loaded from XAML needs a public parameterless constructor, so the running device is
handed over by a method rather than through the constructor.

**A screenshot of the shell was showing an app that had never started.**
The capture path returned before the line that starts the daemon, so the window rendered a device
with no listener and no links, and the only clue was the absence of `Listening on 45001` in the
log.
Worth remembering that a screenshot is evidence of what was drawn, not of what was running.

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

**The central announces itself before it knows who it is talking to.**
The Bluetooth handshake has the central connect, subscribe and send its hello - public key, device
name and mesh name - and only then does either end authorise. So two meshes in range learn each
other's device and mesh names, even though nothing is let in and nothing readable crosses. Closing
it means the central waits for the peripheral's hello, checks whether that key is paired, and only
answers if it is. Strictly better, and it changes the handshake on all three platforms, so it is a
protocol decision rather than a fix.

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
tests/CoreLib.Tests/  286 tests: crypto, key agreement, identity, key storage, the registry,
                      and mesh links over real loopback sockets
```

`TrustManager` and `WindowsBleDiscovery` were deleted rather than left alongside their
replacements.
The first minted a fresh keypair on every construction and nothing on the wire consulted it; the
second scanned for manufacturer data nothing published and logged through `Console.WriteLine`.
A second and broken version of something is worse than not having it.
