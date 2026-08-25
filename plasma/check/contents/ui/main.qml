/*
 * The widget's only executable check.
 *
 * It loads the real MeshBus.qml - check.sh copies it in beside this file rather than duplicating
 * it - points it at a scratch daemon, and calls every function on it once. Nothing here decides
 * whether a call worked: check.sh reads dbus-monitor and asks the only question that matters,
 * which is whether the arguments were on the wire.
 *
 * That is deliberate. Two of the defects this exists to catch produce a call that is dispatched,
 * answered and logged as an ordinary failure - a `signature` on the message makes the binding send
 * an empty body, and DBus.string() without `new` throws before asyncCall is reached. Neither is
 * visible from inside QML unless you go looking, and neither would fail a test that only asked
 * whether an exception was raised.
 *
 * Every call is aimed at something that cannot matter: a scratch daemon in a temporary data
 * directory, and a fingerprint no device has.
 */

pragma ComponentBehavior: Bound

import QtQuick

import org.kde.plasma.plasmoid

PlasmoidItem {
    id: root

    /* check.sh substitutes the scratch daemon's bus name here. It is usually a unique name like
       :1.84, because the well-known name belongs to whichever Mesh Sync is already running. */
    readonly property string target: "@SERVICE@"

    /* A fingerprint no device has, so Ring, SendFile and Forget are answered NoSuchDevice before
       the daemon does anything with them - and the argument is still on the wire, which is what
       is being checked. */
    readonly property string nowhere:
        "/dev/meshsync/Daemon/devices/0000000000000000000000000000000000000000000000000000000000000000"
    readonly property string noKnock:
        "/dev/meshsync/Daemon/pending/0000000000000000000000000000000000000000000000000000000000000000"

    readonly property MeshBus bus: MeshBus {
        service: root.target
        /* The clock and anything else gated on somebody looking. Nobody is, but the check is. */
        watching: true
    }

    /*
     * The second half of the check: does the widget notice a change it did not cause.
     *
     * check.sh renames the mesh over the bus once the sweep is done. If PropertiesChanged is not
     * being taken - which is the state the widget was in for every device property, because it
     * derived a revision from four counts instead - meshName never moves and this never fires.
     */
    readonly property string sentinel: "meshsync-check-live"

    property bool sawRename: false

    readonly property Connections __live: Connections {
        target: root.bus

        function onMeshNameChanged(): void {
            if (root.bus.meshName !== root.sentinel || root.sawRename)
                return;

            root.sawRename = true;
            console.warn("CHECK|live|mesh-name");
            root.finishIfLive();
        }

        /*
         * Informational only.
         *
         * TreeRevision moves when a device or a pairing request arrives, leaves or changes, and a
         * scratch daemon has none of either - the pairing URI carries no port, so two daemons on
         * one machine cannot be made to pair without dialling whatever is on 45001, which is
         * whichever Mesh Sync the person running this is actually using. That the property exists
         * and is read as a number is asserted from check.sh instead; that the widget refetches
         * when it moves is one binding above.
         */
        function onTreeRevisionChanged(): void {
            console.warn("CHECK|revision|" + root.bus.treeRevision);
        }
    }

    function finishIfLive(): void {
        if (root.sawRename) {
            deadline.stop();
            console.warn("CHECK|done|" + root.calls.length);
        }
    }

    /* Named so a failure names the control a person would have clicked, not the member it sends. */
    readonly property var calls: [
        { name: "reconnect",        run: () => root.bus.dial() },
        { name: "stop-ringing",     run: () => root.bus.stopRinging() },
        { name: "send-clipboard",   run: () => root.bus.sendClipboard() },
        { name: "dismiss-all",      run: () => root.bus.dismissAll() },
        { name: "notifications",    run: () => root.bus.refreshNotifications() },
        { name: "object-tree",      run: () => root.bus.refreshObjects() },
        { name: "open-mesh-sync",   run: () => root.bus.show("home") },
        { name: "send-text",        run: () => root.bus.sendText("meshsync-check") },
        { name: "dismiss",          run: () => root.bus.dismiss("meshsync-check-key") },
        { name: "reply",            run: () => root.bus.reply("meshsync-check-key", "meshsync-check-text") },
        { name: "ring",             run: () => root.bus.ring(root.nowhere, false) },
        { name: "send-file",        run: () => root.bus.sendFile(root.nowhere, "/nonexistent/meshsync-check") },
        { name: "forget",           run: () => root.bus.forget(root.nowhere) },
        { name: "confirm",          run: () => root.bus.confirm(root.noKnock) },
        { name: "reject",           run: () => root.bus.reject(root.noKnock) },
        { name: "set-transport",    run: () => root.bus.setTransport("wifi") },
        { name: "set-tray-icon",    run: () => root.bus.setTrayIconVisible(true) },
        { name: "set-content",      run: () => root.bus.setNotificationContent(false) },
    ]

    /*
     * One at a time, with a gap.
     *
     * Fired together they interleave on the wire and a failure cannot be attributed to a control.
     * The gap is what lets check.sh read the capture as a sequence.
     */
    property int next: 0

    readonly property Timer __step: Timer {
        interval: 260
        repeat: true
        running: true
        onTriggered: {
            if (root.next >= root.calls.length) {
                running = false;
                settle.start();
                return;
            }


            const call = root.calls[root.next++];
            console.warn("CHECK|fired|" + call.name);

            /* A throw here is a finding, not a crash: DBus.string() without `new` raises a
               TypeError inside the caller, which is exactly how eight controls came to do
               nothing while looking perfectly well wired. */
            try {
                call.run();
            } catch (error) {
                console.warn("CHECK|threw|" + call.name + "|" + error);
            }
        }
    }

    /* Long enough for the last reply to land, then check.sh is told to start changing things
       underneath the widget. */
    readonly property Timer __settle: Timer {
        id: settle
        interval: 1200
        onTriggered: { console.warn("CHECK|swept"); deadline.start(); }
    }

    /* A widget that never notices has to fail rather than hang. */
    readonly property Timer __deadline: Timer {
        id: deadline
        interval: 12000
        onTriggered: {
            if (!root.sawRename) console.warn("CHECK|missed|mesh-name");
            console.warn("CHECK|done|" + root.calls.length);
        }
    }

    Component.onCompleted: console.warn("CHECK|target|" + root.target)
}
