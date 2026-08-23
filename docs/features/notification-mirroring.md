---
type: feature
status: shipped
platforms: [windows, android, linux]
tier: either
code:
  - src/CoreLib/Transport/NotificationProtocol.cs
  - src/AndroidClient/Platforms/Android/NotificationMirrorService.cs
  - src/WinDaemon/MirroredNotifications.cs
  - src/WinDaemon/WindowsToasts.cs
  - src/DesktopCore/MirroredNotifications.cs
  - src/DesktopCore/Platform/DesktopNotifier.cs
updated: 2026-08-23
---

# Notification mirroring

Mirror the apps you choose from your phone, dismiss them from either end, and **reply to them
without picking the phone up**.
A few hundred bytes each, so Bluetooth carries them, which means notifications keep mirroring -
and keep being answerable - with no network at all.

## Replying

> **In flight** as of 2026-08-23: protocol, desktop, bus and both UIs are built and the protocol
> is under test; the Android half has not yet met a real WhatsApp notification.

Reading a message on the laptop and then reaching for the phone to answer it is most of the reason
a mirror gets switched off again. So the desktop can answer.

**Nothing here talks to WhatsApp.** Android attaches a `RemoteInput` to the reply action of a
messaging notification, and firing that action with the text filled in is byte for byte what
happens when a person types into the notification shade. The message goes out through WhatsApp, or
Signal, or Messages, from the account already signed in on the phone. No credential is held and no
app is automated from the outside - the line this project drew when it
[[android-client|banned the accessibility service]] is not crossed.

`FindReplyAction` matches on `AllowFreeFormInput`: some apps attach a `RemoteInput` restricted to
canned choices, and typing into that one does not send what was typed.

**The notification says whether it can be answered**, and the desktop believes it rather than
guessing. A reply box on a notification whose app offered no reply action is a message the user
believes they sent.

**The frame grew by appending.** A flags byte and a reply label go after the five original fields,
and `TryParse` reads them only if they are there - so a device on the older build reads the five it
knows and stops. Both directions keep working across a mixed mesh, which matters because the phone
and the desktop are updated on different days by different means.

## Where it lives

| Role | Head | File |
|---|---|---|
| Source | Android | the notification listener service |
| Display | Windows | `src/WinDaemon/MirroredNotifications.cs`, `WindowsToasts.cs` |
| Display | Linux and macOS | `src/DesktopCore/MirroredNotifications.cs`, `Platform/DesktopNotifier.cs` |
| Wire | shared | `src/CoreLib/Transport/NotificationProtocol.cs` |

Three content types: `Notification` (0x07), `NotificationDismiss` (0x08) and
`NotificationReply` (0x0C).

## Which heads have it

**Only Android sources notifications.**
No desktop head reads the system notification centre, so mirroring is one-directional today: phone
to desktop, and phone to phone once there is a second phone.

**Displaying works on Windows and Linux.**
Mirrored notifications land in the platform's own notification centre rather than in a window the
app owns.
On Windows that needs an AppUserModelID registered under `HKCU`, which is what makes toasts
possible with no installer, and a hashed tag, which is what makes them removable when the phone
dismisses one.

**The phone displays them too**, not only sources them.
A swipe there tells the device it came from, through a delete intent, because the listener ignores
this app's own notifications and would never otherwise see it.

## Replying, in flight

> **In flight.** `NotificationReply` and the `CanReply` / `ReplyLabel` fields on
> `NotificationProtocol` are uncommitted as of 2026-08-23, along with `ReplyToNotification` on
> [[dbus-ipc]].

The one thing mirroring could not do.
Reading a message on the laptop and then picking up the phone to answer it is most of the reason a
mirror gets switched off again.

**It is not a message the app sends. It is the notification's own reply action being pulled.**
Android attaches a `RemoteInput` to the reply action of a messaging notification, and firing that
action with the text filled in is exactly what happens when you reply from the shade.
So the message goes out through WhatsApp, or Signal, or Messages, by the account already signed in
on the phone.

Nothing here has or needs any credential, and no app is automated from the outside - **which is the
line this project drew when it banned the accessibility service**.
See [[clipboard-sync]].

Two short strings, capped at `MaxReplyLabelBytes` (64) and `MaxReplyBytes` (2048), so Bluetooth
carries it.
Answering a message with no network is the case that makes the feature worth having rather than a
convenience.

## The rule that governs this feature

**Mirrored notifications are never written down.**
Not to the [[activity-log]], not to a cache, and not into a log line carrying their contents.
They are the most private thing this app touches, and `AGENTS.md` states this as a prohibition
rather than a preference.

## Why it is on by default and muted per app

Deny-by-default plus an empty allow list meant three opt-ins before anything appeared, which is
indistinguishable from the feature being broken.
The listener grant is the real gate.
The mute list is what banking and authenticator apps are for.
Old settings are dropped on read rather than translated, because an allow list of three apps is
not a mute list of every other one.

## What is still open

- **A phone displaying a mirrored notification is unverified**, because it needs a second phone or
  Windows-to-phone mirroring to have a sender at all.
- Banners will not appear while Windows Do Not Disturb is on.
  That is a setting rather than a bug, and it cost real time to work out.

## See also

[[content-types]] · [[bluetooth-tier]] · [[activity-log]] · [[android-client]]
