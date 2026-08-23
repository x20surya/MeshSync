/*
 * The one place in the widget that knows the bus.
 *
 * Everything else binds to `props` or calls a function here; no other file mentions a service
 * name, an object path or a signature. That is the same rule the daemon side follows in
 * DesktopCore/Ipc/BusNames.cs, and for the same reason - a path spelled out in two places is a
 * path that will be spelled differently in two places.
 *
 * No compiled plugin of our own: org.kde.plasma.workspace.dbus ships with plasma-workspace and
 * gives QML a real D-Bus binding. KDE Connect's plasmoid uses a C++ plugin instead, which is
 * what this deliberately avoids.
 */

pragma ComponentBehavior: Bound

import QtQml
import QtQuick

import org.kde.plasma.workspace.dbus as DBus

QtObject {
    id: bus

    readonly property string service: "dev.meshsync.Daemon"
    readonly property string root: "/dev/meshsync/Daemon"
    readonly property string iface: "dev.meshsync.Daemon1"
    readonly property string deviceIface: "dev.meshsync.Device1"
    readonly property string pairingIface: "dev.meshsync.Pairing1"
    readonly property string objectManager: "org.freedesktop.DBus.ObjectManager"
    readonly property string propertiesIface: "org.freedesktop.DBus.Properties"

    /* Whether Mesh Sync is running at all. A missing daemon is a state the widget draws, not an
       error it hides - see NotRunning.qml. */
    readonly property bool available: watcher.registered

    /*
     * The daemon's properties, read out explicitly rather than bound to through the map.
     *
     * Two reasons, both learned the hard way. The map is a QQmlPropertyMap and starts empty, so
     * a binding to `map.MeshName` written before the first GetAll returns resolves to undefined
     * and is never re-evaluated when the key appears - the widget draws its fallbacks for ever
     * and looks like it cannot see the daemon. And the values are not what they appear: a string
     * arrives wrapped as `{ value: "..." }` while a bool arrives bare, so reading one without
     * unwrapping yields "[object Object]".
     *
     * Both disappear if the map is read once per change into ordinary typed properties, which is
     * also nicer for every file that consumes them.
     */
    property string meshName: ""
    property string deviceName: ""
    property string fingerprint: ""
    property string activeLink: "none"
    property string bluetoothStatus: ""
    property string transport: "both"
    property string pairingUri: ""
    property bool connected: false
    property bool trayIconVisible: true
    property bool dialling: false
    property bool ringing: false
    property int peerCount: 0
    property int connectedCount: 0
    property int pendingCount: 0
    property int notificationCount: 0

    /* A typed wrapper reads as { value: x }; a bool reads as itself. One helper, used for all. */
    function unwrap(raw: var, fallback: var): var {
        if (raw === undefined || raw === null)
            return fallback;
        if (typeof raw === "object" && raw.value !== undefined)
            return raw.value;
        return raw;
    }

    function readProperties(): void {
        const map = properties.properties;
        if (!map)
            return;

        bus.meshName = String(unwrap(map.MeshName, ""));
        bus.deviceName = String(unwrap(map.DeviceName, ""));
        bus.fingerprint = String(unwrap(map.Fingerprint, ""));
        bus.activeLink = String(unwrap(map.ActiveLink, "none"));
        bus.bluetoothStatus = String(unwrap(map.BluetoothStatus, ""));
        bus.transport = String(unwrap(map.Transport, "both"));
        bus.pairingUri = String(unwrap(map.PairingUri, ""));
        bus.connected = unwrap(map.IsConnected, false) === true;
        bus.trayIconVisible = unwrap(map.TrayIconVisible, true) === true;
        bus.dialling = unwrap(map.IsDialling, false) === true;
        bus.ringing = unwrap(map.IsRinging, false) === true;
        bus.peerCount = Number(unwrap(map.PeerCount, 0));
        bus.connectedCount = Number(unwrap(map.ConnectedCount, 0));
        bus.pendingCount = Number(unwrap(map.PendingCount, 0));
        bus.notificationCount = Number(unwrap(map.NotificationCount, 0));
    }

    readonly property ListModel devices: ListModel { }
    readonly property ListModel pending: ListModel { }
    readonly property ListModel notifications: ListModel { }

    /* The last thing a call said, for the footer to show. Cleared on the next successful one. */
    property string lastMessage: ""

    signal replied(string key, bool ok, string message)

    // ──────────────────────────────── wiring

    readonly property DBus.DBusServiceWatcher __watcher: DBus.DBusServiceWatcher {
        id: watcher
        busType: DBus.BusType.Session
        watchedService: bus.service
        onRegisteredChanged: {
            if (registered) {
                bus.refreshObjects();
                bus.refreshNotifications();
            } else {
                bus.devices.clear();
                bus.pending.clear();
                bus.notifications.clear();
            }
        }
    }

    readonly property DBus.Properties __properties: DBus.Properties {
        id: properties
        busType: DBus.BusType.Session
        service: bus.service
        path: bus.root
        iface: bus.iface

        /* refreshed is the full read; propertyMapChanged is a PropertiesChanged landing. Both
           have to be taken, or the widget is correct at startup and then frozen. */
        onRefreshed: bus.readProperties()
        onPropertyMapChanged: bus.readProperties()
    }

    /*
     * What makes the lists update.
     *
     * The obvious route - a SignalWatcher on ObjectManager - is not available here: this Plasma
     * exports SignalWatcher.onReceivedSignal as a *slot* rather than a signal, so QML cannot
     * attach a handler to it and the applet refuses to load outright if you try.
     *
     * The counts do the work instead. The daemon publishes PeerCount, ConnectedCount,
     * PendingCount and NotificationCount as ordinary properties, and Properties above delivers
     * PropertiesChanged for them. Every arrival, departure, connect and disconnect moves one of
     * those numbers, so this is event-driven rather than polled, and it uses only the part of
     * the binding that is verified to work.
     *
     * The timer is the backstop for the one thing a count cannot see: a device whose address
     * changed while the set stayed the same. It runs only while the widget is open.
     */
    readonly property int treeRevision: bus.available
        ? bus.peerCount * 1000 + bus.connectedCount * 100 + bus.pendingCount * 10 + (bus.dialling ? 1 : 0)
        : -1

    onTreeRevisionChanged: bus.refreshObjects()

    onNotificationCountChanged: bus.refreshNotifications()

    /* Set by the full representation while it is on screen. A widget nobody is looking at has no
       business waking up. */
    property bool watching: false

    readonly property Timer __backstop: Timer {
        interval: 10000
        repeat: true
        running: bus.available && bus.watching
        onTriggered: bus.refreshObjects()
    }

    // ──────────────────────────────── calling

    function call(path: string, member: string, signature: string, args: var,
                  onOk: var, target: string): void {
        DBus.SessionBus.asyncCall({
            service: bus.service,
            path: path,
            iface: target,
            member: member,
            signature: signature,
            arguments: args
        },
        /* The resolve callback is handed the DBusPendingReply, not the value inside it. Reading
           `reply` directly gives you the wrapper - which iterates as an object with no error and
           quietly produces nothing, so it is worth being explicit about. */
        reply => { if (onOk) onOk(reply.value); },
        error => {
            bus.lastMessage = String(error);
            console.warn("meshsync:", member, "failed:", error);
        });
    }

    function daemonCall(member: string, signature: string, args: var, onOk: var): void {
        call(bus.root, member, signature, args, onOk, bus.iface);
    }

    // ──────────────────────────────── the object tree

    /* One shape for a device and a pairing request, because the delegate wants the same fields
       from both and a pairing request is a device that has not been let in yet. */
    function refreshObjects(): void {
        if (!bus.available)
            return;

        DBus.SessionBus.asyncCall({
            service: bus.service, path: bus.root,
            iface: bus.objectManager, member: "GetManagedObjects"
        },
        reply => bus.applyObjects(reply.value),
        error => console.warn("meshsync: GetManagedObjects failed:", error));
    }

    function applyObjects(reply: var): void {
        const seenDevices = [];
        const seenPending = [];

        for (const path in reply) {
            const interfaces = reply[path];
            if (!interfaces)
                continue;

            if (interfaces[bus.deviceIface])
                seenDevices.push({ path: path, values: bus.plain(interfaces[bus.deviceIface]) });
            else if (interfaces[bus.pairingIface])
                seenPending.push({ path: path, values: bus.plain(interfaces[bus.pairingIface]) });
        }

        /* Connected first, then alphabetical. A list that reorders itself as devices come and go
           is a list you cannot click reliably, so within each group the order is stable. */
        seenDevices.sort((a, b) => {
            if (a.values.IsConnected !== b.values.IsConnected)
                return a.values.IsConnected ? -1 : 1;
            return String(a.values.Name).localeCompare(String(b.values.Name));
        });

        bus.fill(bus.devices, seenDevices);
        bus.fill(bus.pending, seenPending);
    }

    /*
     * An a{sv} as plain JavaScript values.
     *
     * The same wrapping that catches you on the property map catches you here: every variant in
     * the dictionary arrives as `{ value: x }`. Put one of those into a ListModel and QML makes
     * it a nested model, so `model.Name` is an object and a string property bound to it comes out
     * empty - a device list of blank rows that look like a daemon bug and are not.
     */
    function plain(dictionary: var): var {
        const out = {};
        for (const key in dictionary)
            out[key] = bus.unwrap(dictionary[key], "");
        return out;
    }

    /* Replaces a model's contents in place rather than clear-and-refill, so a row the pointer is
       over does not vanish and reappear under it every time anything changes. */
    function fill(model: ListModel, rows: var): void {
        for (let i = 0; i < rows.length; i++) {
            const row = Object.assign({ path: rows[i].path }, rows[i].values);

            if (i < model.count)
                model.set(i, row);
            else
                model.append(row);
        }

        while (model.count > rows.length)
            model.remove(model.count - 1);
    }

    // ──────────────────────────────── notifications

    function refreshNotifications(): void {
        if (!bus.available)
            return;

        daemonCall("Notifications", "", [], reply => {
            const rows = [];

            for (const entry of (reply || [])) {
                rows.push({
                    values: {
                        key: String(entry[0]),
                        appName: String(entry[1]),
                        from: String(entry[2]),
                        at: Number(entry[3]),
                        canReply: Boolean(entry[4]),
                        replyLabel: String(entry[5]) || "Reply"
                    },
                    path: String(entry[0])
                });
            }

            bus.fill(bus.notifications, rows);
        });
    }

    // ──────────────────────────────── actions

    function sendText(text: string): void {
        if (!text)
            return;

        daemonCall("SendText", "s", [DBus.string(text)], reply => {
            const sent = Number(reply);
            bus.lastMessage = sent > 0
                ? i18np("Sent to %1 device", "Sent to %1 devices", sent)
                : i18n("Nothing is reachable, so nothing was sent");
        });
    }

    /* Asks the daemon to send what is on the clipboard now. The widget never reads the clipboard
       itself: on Wayland a plasmoid cannot, and the daemon holds its own connection for exactly
       that reason. */
    function sendClipboard(): void {
        daemonCall("SendClipboard", "", [], result => {
            bus.lastMessage = String(result[1]);
        });
    }

    function dial(): void { daemonCall("Dial", "", []); }

    function stopRinging(): void { daemonCall("StopRinging", "", []); }

    function show(page: string): void {
        daemonCall("Show", "s", [DBus.string(page)]);
    }

    /* Whether Mesh Sync draws its own tray icon. The setting belongs to the app, not to the
       widget, so it is written over the bus rather than kept here - two copies of one answer is
       how a checkbox ends up disagreeing with what it controls. */
    function setTrayIconVisible(visible: bool): void {
        DBus.SessionBus.asyncCall({
            service: bus.service, path: bus.root, iface: bus.propertiesIface,
            member: "Set", signature: "ssv",
            arguments: [DBus.string(bus.iface), DBus.string("TrayIconVisible"),
                        DBus.variant(visible)]
        }, () => {}, error => console.warn("meshsync: could not change the tray icon:", error));
    }

    function setTransport(mode: string): void {
        DBus.SessionBus.asyncCall({
            service: bus.service, path: bus.root, iface: bus.propertiesIface,
            member: "Set", signature: "ssv",
            arguments: [DBus.string(bus.iface), DBus.string("Transport"),
                        DBus.variant(DBus.string(mode))]
        }, () => {}, error => console.warn("meshsync: could not set the transport:", error));
    }

    function ring(path: string, on: bool): void {
        call(path, "Ring", "b", [on], null, bus.deviceIface);
    }

    function forget(path: string): void {
        call(path, "Forget", "", [], null, bus.deviceIface);
    }

    function sendFile(path: string, file: string): void {
        call(path, "SendFile", "s", [DBus.string(file)], reply => {
            bus.lastMessage = String(reply[1]);
        }, bus.deviceIface);
    }

    function confirm(path: string): void {
        call(path, "Confirm", "", [], reply => { bus.lastMessage = String(reply[1]); }, bus.pairingIface);
    }

    function reject(path: string): void {
        call(path, "Reject", "", [], reply => { bus.lastMessage = String(reply[1]); }, bus.pairingIface);
    }

    function dismiss(key: string): void {
        daemonCall("DismissNotification", "s", [DBus.string(key)]);
    }

    function dismissAll(): void {
        daemonCall("DismissAllNotifications", "", []);
    }

    /* Replies go out through the app that posted the notification, on the phone. Nothing here
       knows anything about WhatsApp - see SyncContent.NotificationReply. */
    function reply(key: string, text: string): void {
        if (!text)
            return;

        daemonCall("ReplyToNotification", "ss", [DBus.string(key), DBus.string(text)], result => {
            bus.replied(key, Boolean(result[0]), String(result[1]));
        });
    }
}
