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

    /* A default rather than a constant, so plasma/check.sh can aim this same file at a scratch
       daemon on its unique name. Nothing in the widget ever sets it. */
    property string service: "dev.meshsync.Daemon"
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
    property bool showNotificationContent: false
    property bool dialling: false
    property bool ringing: false
    property int treeRevision: 0
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
        bus.showNotificationContent = unwrap(map.ShowNotificationContent, false) === true;
        bus.dialling = unwrap(map.IsDialling, false) === true;
        bus.ringing = unwrap(map.IsRinging, false) === true;
        bus.treeRevision = Number(unwrap(map.TreeRevision, 0));
        bus.peerCount = Number(unwrap(map.PeerCount, 0));
        bus.connectedCount = Number(unwrap(map.ConnectedCount, 0));
        bus.pendingCount = Number(unwrap(map.PendingCount, 0));
        bus.notificationCount = Number(unwrap(map.NotificationCount, 0));
    }

    readonly property ListModel devices: ListModel { }
    readonly property ListModel pending: ListModel { }
    readonly property ListModel notifications: ListModel { }

    /* The last thing a call said, for the footer to show. */
    property string lastMessage: ""

    /* ...and it goes away on its own. "Sent to 2 devices" is worth reading once; a widget still
       saying it twenty minutes later is presenting a fact about the past as the state of the
       mesh, which is the one thing this widget exists not to do. */
    onLastMessageChanged: if (bus.lastMessage.length > 0) fade.restart()

    readonly property Timer __fade: Timer {
        id: fade
        interval: 8000
        onTriggered: bus.lastMessage = ""
    }

    /*
     * A clock, for "last seen three minutes ago".
     *
     * The daemon sends a unix timestamp rather than a phrase, because the phrase has to be in the
     * reader's language. A binding that reads Date.now() re-evaluates only when its own
     * dependencies move, so with nothing ticking the phrase is written once and then frozen -
     * "3 minutes ago", an hour later. A minute is the finest granularity the phrase has, and it
     * only runs while somebody is looking at it.
     */
    property double now: Math.floor(Date.now() / 1000)

    readonly property Timer __clock: Timer {
        interval: 60000
        repeat: true
        running: bus.watching
        onTriggered: bus.now = Math.floor(Date.now() / 1000)
    }

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

        /*
         * The first full read, and the point at which the lists are worth fetching.
         *
         * Deferred by a turn. Filling the models from inside this handler puts rows into the
         * popup during its first layout pass, and Plasma's own ScrollView answers that with two
         * binding-loop warnings on the scrollbar's visibility as it settles. Letting the layout
         * finish first costs nothing anybody can perceive and keeps the log clean.
         */
        onRefreshed: {
            bus.readProperties();
            Qt.callLater(bus.refreshObjects);
            Qt.callLater(bus.refreshNotifications);
        }

        /*
         * A PropertiesChanged landing, carrying exactly the keys that moved.
         *
         * This is a real QML signal on DBus.Properties and it is what makes a targeted refresh
         * possible. onPropertyMapChanged fires for the same events without saying what changed,
         * so everything below would have to be guessed - which is how the widget came to derive
         * a revision from four counts and poll for the rest.
         */
        onPropertiesChanged: (name, changed) => {
            bus.readProperties();

            /* The device tree. The daemon bumps TreeRevision for anything a list has to be
               redrawn for - a device arriving or leaving, a rename, a link changing, a new
               address - and deliberately not for LastSeen, which moves on every dial round and
               would turn this into a poll. */
            if (changed.TreeRevision !== undefined)
                bus.refreshObjects();

            /* Notifications. The count moves when one arrives or is dismissed. The setting moves
               when grouping changes from by-app to by-conversation, which changes every row
               without changing the count at all. */
            if (changed.NotificationCount !== undefined ||
                changed.ShowNotificationContent !== undefined)
                bus.refreshNotifications();
        }
    }

    /*
     * What used to make the lists update, and why nothing does now.
     *
     * A SignalWatcher on ObjectManager is not available here: this Plasma exports
     * SignalWatcher.onReceivedSignal as a *slot* rather than a signal, so QML cannot attach a
     * handler and the applet refuses to load outright if you try. That much is still true.
     *
     * The conclusion drawn from it was too broad. DBus.Properties.propertiesChanged IS a real
     * signal, and it carries the changed keys - so the refresh above is driven by what actually
     * moved. What stood here instead was a revision derived from four counts,
     * peers*1000 + connected*100 + pending*10, plus a ten second timer as a backstop. It missed
     * every change that keeps the set the same: a rename, a link going from Bluetooth to Wi-Fi,
     * a new address. The daemon now publishes TreeRevision for exactly those, so both are gone.
     *
     * Nothing here polls any more.
     */

    /* True while a person can see the list: the popup is open, or the widget is on a desktop
       where it always is. Set from main.qml. */
    property bool watching: false

    // ──────────────────────────────── calling

    /*
     * Three rules, each of them reproduced on the wire rather than reasoned about.
     *
     * NO `signature` ON THE MESSAGE. Setting it makes this binding send an empty body, so every
     * argument is dropped in silence and the daemon answers "Unexpected end of data" - which
     * reads as a daemon fault and is not one. Two otherwise identical Join calls captured under
     * dbus-monitor differ only in whether the string is there at all. The types come from the
     * DBus.* wrappers instead, which is what the binding actually reads.
     *
     * `new` ON EVERY WRAPPER. DBus.string(x) raises "TypeError: Function can only be called with
     * |new|", and that throw aborts the *calling* function before asyncCall is ever reached - so
     * nothing is sent, nothing is logged, and the control simply does nothing when clicked.
     * bus.text() is the only place a string is wrapped, for the same reason BusNames.cs is the
     * only place a path is spelled.
     *
     * THE REJECT CALLBACK IS HANDED THE PENDING REPLY, not a message. String(it) is
     * "Plasma::DBusPendingReply(0x55...)", which is what the footer used to show a person. The
     * text is on reply.error.
     */
    function call(path: string, member: string, args: var, onOk: var, target: string): void {
        DBus.SessionBus.asyncCall({
            service: bus.service,
            path: path,
            iface: target,
            member: member,
            arguments: args
        },
        /* The resolve callback is handed the DBusPendingReply, not the value inside it. Reading
           `reply` directly gives you the wrapper - which iterates as an object with no error and
           quietly produces nothing, so it is worth being explicit about. */
        reply => { if (onOk) onOk(reply.value); },
        reply => {
            const failure = reply ? reply.error : null;
            bus.lastMessage = failure && failure.message
                ? failure.message
                : i18n("Mesh Sync did not answer.");
            console.warn("meshsync:", member, "failed:", failure ? failure.name : reply);
        });
    }

    function daemonCall(member: string, args: var, onOk: var): void {
        call(bus.root, member, args, onOk, bus.iface);
    }

    /// The one place a string becomes a D-Bus argument. `new` is not optional - see call().
    function text(value: string): var { return new DBus.string(String(value)); }

    /*
     * A (bs) answer as plain values.
     *
     * The bool arrives bare and the string arrives wrapped as { value: "..." }, so reading
     * reply[1] straight renders "[object Object]" wherever the answer is shown. It is the same
     * trap as the property map, one layer further in, and unwrap() already knows how to open it.
     */
    function outcome(reply: var): var {
        return {
            ok: bus.unwrap(reply ? reply[0] : false, false) === true,
            message: String(bus.unwrap(reply ? reply[1] : "", ""))
        };
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
        reply => console.warn("meshsync: GetManagedObjects failed:",
                              reply && reply.error ? reply.error.name : reply));
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

    /*
     * Replaces a model's contents in place rather than clear-and-refill, so a row the pointer is
     * over does not vanish and reappear under it every time anything changes.
     *
     * A row that has not changed is left alone entirely. ListModel.set marks the item changed
     * whatever it was handed, and every delegate binding then re-evaluates - which for a list
     * that refetches on any tree change is most of the work for none of the result.
     */
    function fill(model: ListModel, rows: var): void {
        for (let i = 0; i < rows.length; i++) {
            const row = Object.assign({ path: rows[i].path }, rows[i].values);

            if (i >= model.count) {
                model.append(row);
                continue;
            }

            if (!bus.same(model.get(i), row))
                model.set(i, row);
        }

        while (model.count > rows.length)
            model.remove(model.count - 1);
    }

    /// Whether a model row already says what a fresh one says. Shallow on purpose: every value
    /// in these rows is a string, a number or a bool by the time plain() is done with it.
    function same(current: var, fresh: var): bool {
        for (const key in fresh) {
            if (current[key] !== fresh[key])
                return false;
        }
        return true;
    }

    // ──────────────────────────────── notifications

    function refreshNotifications(): void {
        if (!bus.available)
            return;

        daemonCall("Notifications", [], reply => {
            const rows = [];

            for (const entry of (reply || [])) {
                rows.push({
                    key: String(entry[0]),
                    appName: String(entry[1]),
                    from: String(entry[2]),
                    at: Number(entry[3]),
                    canReply: Boolean(entry[4]),
                    replyLabel: String(entry[5]) || "Reply",
                    title: String(entry[6] || ""),
                    text: String(entry[7] || "")
                });
            }

            bus.regroup(rows);
        });
    }

    /*
     * Groups the flat list the way a phone's shade does: by conversation where the daemon is
     * telling us who it is from, and by application otherwise.
     *
     * The distinction matters. With ShowNotificationContent off there is no sender, so three
     * WhatsApp notifications are indistinguishable and grouping them under one "WhatsApp" head
     * with a count is the only honest rendering. With it on, "Aditya" and "Mum" are separate
     * conversations and each gets its own reply box - which is the thing that makes replying from
     * a panel feel like replying on the phone rather than answering a queue.
     *
     * Newest first, and the group carries the newest message's key, because that is the one a
     * reply should thread onto.
     *
     * WHY A MERGED GROUP CANNOT BE REPLIED TO. With the setting off, every WhatsApp conversation
     * lands under one "WhatsApp" head - and one reply box on that head has to thread onto ONE
     * key, whichever happened to win. A reply typed there went to an arbitrary conversation, and
     * the box could not say which because the whole point of the setting is that it does not
     * know. So reply is offered when the group IS a conversation, or when it holds exactly one
     * notification and there is therefore nothing to confuse it with. Android keeps one
     * notification per conversation and updates it in place - see MirroredNotifications, which
     * stores by key - so with the setting on that is nearly always the case anyway.
     */
    function regroup(rows: var): void {
        rows.sort((a, b) => b.at - a.at);

        const order = [];
        const byKey = {};

        for (const row of rows) {
            const groupKey = row.title.length > 0 ? row.appName + "\u0000" + row.title : row.appName;

            if (byKey[groupKey] === undefined) {
                byKey[groupKey] = {
                    key: row.key,
                    appName: row.appName,
                    from: row.from,
                    heading: row.title.length > 0 ? row.title : row.appName,
                    subheading: row.title.length > 0 ? i18n("%1 on %2", row.appName, row.from)
                                                     : i18n("on %1", row.from),
                    preview: row.text,
                    at: row.at,
                    canReply: row.canReply,
                    replyLabel: row.replyLabel,
                    // Whether this group is one conversation rather than one application's worth
                    // of them. Decides, below, whether a reply has somewhere unambiguous to go.
                    conversation: row.title.length > 0,
                    count: 1,
                    keys: row.key
                };
                order.push(groupKey);
                continue;
            }

            const group = byKey[groupKey];
            group.count += 1;
            group.keys += "\n" + row.key;

            // A group can reply if any message in it can, and the newest one already won the
            // heading, so nothing else is taken from the older ones.
            if (row.canReply && !group.canReply) {
                group.canReply = true;
                group.replyLabel = row.replyLabel;
                group.key = row.key;
            }
        }

        for (const groupKey of order) {
            const group = byKey[groupKey];
            group.canReply = group.canReply && (group.conversation || group.count === 1);
        }

        bus.fill(bus.notifications, order.map(k => ({ path: k, values: byKey[k] })));
    }

    /// Dismisses every notification in a group, which is what tapping one away on a phone does.
    function dismissGroup(keys: string): void {
        for (const key of String(keys).split("\n")) {
            if (key.length > 0) bus.dismiss(key);
        }
    }

    // ──────────────────────────────── actions

    function sendText(text: string): void {
        if (!text)
            return;

        daemonCall("SendText", [bus.text(text)], reply => {
            const sent = Number(bus.unwrap(reply, 0));
            bus.lastMessage = sent > 0
                ? i18np("Sent to %1 device", "Sent to %1 devices", sent)
                : i18n("Nothing is reachable, so nothing was sent");
        });
    }

    /* Asks the daemon to send what is on the clipboard now. The widget never reads the clipboard
       itself: on Wayland a plasmoid cannot, and the daemon holds its own connection for exactly
       that reason. */
    function sendClipboard(): void {
        daemonCall("SendClipboard", [], result => {
            bus.lastMessage = bus.outcome(result).message;
        });
    }

    function dial(): void { daemonCall("Dial", []); }

    function stopRinging(): void { daemonCall("StopRinging", []); }

    function show(page: string): void {
        daemonCall("Show", [bus.text(page)]);
    }

    /*
     * One of the daemon's own settings, written over the bus.
     *
     * These belong to Mesh Sync rather than to the widget, so they go to the app rather than into
     * Plasma's config - two copies of one answer is how a checkbox ends up disagreeing with what
     * it controls. They travel over org.freedesktop.DBus.Properties because the interface already
     * declares them writable, and a second way to set one value would be the same mistake again.
     *
     * This only reaches the daemon because the daemon DECLARES the Properties interface in its
     * introspection. Qt introspects an object before it marshals a call to it, and against a peer
     * that does not declare Get and Set it sends them with an empty body - see
     * MeshBusObject.PropertiesXml, which exists for this.
     */
    function setProperty(name: string, value: var): void {
        call(bus.root, "Set", [bus.text(bus.iface), bus.text(name), new DBus.variant(value)],
             null, bus.propertiesIface);
    }

    function setTrayIconVisible(visible: bool): void {
        bus.setProperty("TrayIconVisible", visible);
    }

    function setNotificationContent(show: bool): void {
        bus.setProperty("ShowNotificationContent", show);
    }

    function setTransport(mode: string): void {
        bus.setProperty("Transport", bus.text(mode));
    }

    function ring(path: string, on: bool): void {
        call(path, "Ring", [on], null, bus.deviceIface);
    }

    function forget(path: string): void {
        call(path, "Forget", [], null, bus.deviceIface);
    }

    /*
     * A dropped URL as a path the daemon can open.
     *
     * QML's url type has no toLocalFile, so the conversion is written by hand - and therefore
     * written once. Anything that is not a local file is refused rather than handed to SendFile
     * as a string beginning "http", which the daemon would then fail to open with a message about
     * a file that was never there.
     */
    function localPath(url: var): string {
        const text = String(url);
        return text.startsWith("file://") ? decodeURIComponent(text.substring(7)) : "";
    }

    /// The one reachable device, or "" when there is none or more than one. What lets a file
    /// dropped on the panel icon go somewhere without asking.
    function onlyReachableDevice(): string {
        let found = "";

        for (let i = 0; i < bus.devices.count; i++) {
            const row = bus.devices.get(i);
            if (row.IsConnected !== true)
                continue;
            if (found.length > 0)
                return "";
            found = row.path;
        }

        return found;
    }

    function sendFile(path: string, file: string): void {
        call(path, "SendFile", [bus.text(file)], reply => {
            bus.lastMessage = bus.outcome(reply).message;
        }, bus.deviceIface);
    }

    function confirm(path: string): void {
        call(path, "Confirm", [], reply => { bus.lastMessage = bus.outcome(reply).message; },
             bus.pairingIface);
    }

    function reject(path: string): void {
        call(path, "Reject", [], reply => { bus.lastMessage = bus.outcome(reply).message; },
             bus.pairingIface);
    }

    function dismiss(key: string): void {
        daemonCall("DismissNotification", [bus.text(key)]);
    }

    function dismissAll(): void {
        daemonCall("DismissAllNotifications", []);
    }

    /* Replies go out through the app that posted the notification, on the phone. Nothing here
       knows anything about WhatsApp - see SyncContent.NotificationReply. */
    function reply(key: string, text: string): void {
        if (!text)
            return;

        daemonCall("ReplyToNotification", [bus.text(key), bus.text(text)], result => {
            const answer = bus.outcome(result);
            bus.replied(key, answer.ok, answer.message);
        });
    }
}
