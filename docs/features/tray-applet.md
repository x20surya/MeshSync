---
type: feature
status: shipped
platforms: [linux]
tier: n/a
code:
  - src/DesktopCore/Tray/TrayItem.cs
  - src/DesktopCore/Tray/TrayMenu.cs
  - src/DesktopCore/Platform/TraySettings.cs
updated: 2026-08-23
---

# Tray applet

> Verified against plasmashell: registered with the watcher, `GetLayout` read back correctly, menu
> events act, and the icon hides and returns. Turning it off from the widget's settings works as
> of 2026-08-25 - before that the settings page could not be opened and the checkbox wrote to the
> bus through a call that carried no arguments, so the answer to two icons was unreachable twice
> over. See [[plasma-widget]].

Mesh Sync's own StatusNotifierItem, replacing the one Avalonia produced. Works on any desktop with
a status area, which is the half of this the [[plasma-widget]] cannot reach.

## Why it replaced Avalonia's rather than configuring it

Read live from the running app, Avalonia's tray item had:

| | Was | Is |
|---|---|---|
| `IconName` | empty, with a 128px bitmap in its place | `meshsync-tray-*-symbolic`, recoloured by the theme |
| `Status` | `Active`, always | Passive, Active, **NeedsAttention** on a pairing request |
| `ToolTip` | an empty struct, despite the text being set | the mesh name and what is reachable |
| `SecondaryActivate` | ignored | middle-click sends the clipboard |
| Menu | Show, Quit | devices, pairing requests, ring, reconnect, stop ringing |
| Headless daemon | no icon at all | the same icon |

None of those are settings Avalonia does not expose. They are things the interface has and the
toolkit does not carry.

## What it cost

`com.canonical.dbusmenu`. A StatusNotifierItem does not carry its menu, it carries the object path
of one, so owning the item means owning the menu server too. `GetLayout` returns
`(u(ia{sv}av))` - a revision and a node, where a node is an id, a dictionary of properties, and an
array of variants each holding another node. It is the deepest shape in the repo, and it worked
first time only because every dictionary goes through `BusWrite` - see [[dbus-interface]].

Flat, deliberately: no submenus in this version, and the layout is rebuilt on `AboutToShow` so it
is about the mesh as it is rather than as it was at startup.

## Two icons, and the answer to it

Put the [[plasma-widget]] in the system tray and this sits beside it, identical - the thing that
makes KDE Connect ship its plasmoid and its indicator as separate installs. Here they are one
product, so the widget's settings offer to turn this one off, over `TrayIconVisible`.

Turning it off cannot un-register it: a tray watches one thing, the bus name, and there is no
"unregister". So hiding drops the connection and showing opens a fresh one. The name watcher
belongs to the connection, which is why it is re-taken each time round - without that, hiding
worked and bringing it back never did.

## See also

[[plasma-widget]] · [[dbus-ipc]] · [[desktop-shell]] · [[linux-daemon]] · [[find-my-device]]
