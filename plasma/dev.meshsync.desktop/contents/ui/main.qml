/*
 * Mesh Sync on the panel.
 *
 * Reads dev.meshsync.Daemon over the session bus and owns no truth of its own: which link wins,
 * whether to dial, whether a pairing may be confirmed are all decided in CoreLib and rendered
 * here. The widget wears Plasma's theme rather than Mesh Sync's, because a widget painted in an
 * app's own colours looks broken on somebody else's panel.
 */

pragma ComponentBehavior: Bound

import QtQuick

import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.plasmoid

PlasmoidItem {
    id: root

    readonly property bool inPanel: [
        PlasmaCore.Types.TopEdge,
        PlasmaCore.Types.RightEdge,
        PlasmaCore.Types.BottomEdge,
        PlasmaCore.Types.LeftEdge,
    ].includes(Plasmoid.location)

    readonly property MeshBus bus: MeshBus {
        /* Whether anybody can see the device list. Not component lifetime: Plasma keeps a full
           representation alive after the first expand, so a widget opened once at login used to
           go on waking up every ten seconds until logout. */
        watching: root.expanded || !root.inPanel
    }

    readonly property bool connected: bus.available && bus.connected
    readonly property int waiting: bus.available ? bus.pendingCount : 0
    readonly property int mirrored: bus.available ? bus.notificationCount : 0

    /* Symbolic in a panel, full colour on the desktop, and the attention variant while a device
       is asking to join - the panel is where that has to be noticeable. */
    Plasmoid.icon: !root.bus.available ? "meshsync-tray-offline-symbolic"
        : root.waiting > 0 ? "meshsync-tray-attention-symbolic"
        : root.inPanel ? (root.connected ? "meshsync-tray-active-symbolic" : "meshsync-tray-symbolic")
        : "meshsync"

    /* Passive folds it into the tray's overflow, which is right for a device with nothing paired.
       NeedsAttention pops it back out, which is the whole point of a pairing request. */
    Plasmoid.status: root.waiting > 0 ? PlasmaCore.Types.NeedsAttentionStatus
        : root.connected || root.mirrored > 0 ? PlasmaCore.Types.ActiveStatus
        : PlasmaCore.Types.PassiveStatus

    toolTipMainText: root.bus.available && root.bus.meshName.length > 0 ? root.bus.meshName : i18n("Mesh Sync")

    toolTipSubText: {
        if (!root.bus.available)
            return i18n("Not running");
        if (root.waiting > 0)
            return i18np("%1 device is asking to join", "%1 devices are asking to join", root.waiting);
        if (!root.connected)
            return i18n("Nothing reachable");

        const count = root.bus.connectedCount;
        return root.bus.activeLink === "ble"
            ? i18np("%1 device over Bluetooth", "%1 devices over Bluetooth", count)
            : i18np("%1 device over Wi-Fi", "%1 devices over Wi-Fi", count);
    }

    fullRepresentation: FullRepresentation { bus: root.bus }

    compactRepresentation: CompactRepresentation { plasmoidItem: root }

    /* A pairing request is the one thing worth opening the widget for on its own. Nothing else
       here ever pops itself open: a clipboard arriving must not steal the screen. */
    onWaitingChanged: {
        if (root.waiting > 0 && !root.expanded)
            root.expanded = true;
    }

    /*
     * Added to the menu, never substituted into it.
     *
     * setInternalAction("configure", ...) replaces the action that EVERY route to a widget's own
     * settings goes through - CompactApplet.qml for a panel or tray applet, ConfigOverlay.qml for
     * a widget on the desktop, BasicPlasmoidHeading.qml for the popup's own header button.
     * Overriding it left config/configGeneral.qml with no way in at all, so the two checkboxes and
     * the two settings that belong to the app could not be reached from anywhere.
     *
     * KDE Connect's plasmoid does override it, and ships no contents/config directory - which is
     * the condition under which that is the right call, and is not the case here.
     */
    Plasmoid.contextualActions: [openAction, clipboardAction, reconnectAction]

    PlasmaCore.Action {
        id: openAction
        text: i18n("Open Mesh Sync…")
        icon.name: "meshsync"
        enabled: root.bus.available
        onTriggered: root.bus.show("home")
    }

    /* The two things worth doing without opening anything. Both are already one gesture inside
       the popup; on the menu they are one gesture from a panel that is showing something else. */
    PlasmaCore.Action {
        id: clipboardAction
        text: i18n("Send clipboard")
        icon.name: "edit-paste-symbolic"
        enabled: root.bus.available
        onTriggered: root.bus.sendClipboard()
    }

    PlasmaCore.Action {
        id: reconnectAction
        text: i18n("Reconnect now")
        icon.name: "view-refresh-symbolic"
        enabled: root.bus.available
        onTriggered: root.bus.dial()
    }

    Component.onCompleted: {
        root.bus.refreshObjects();
        root.bus.refreshNotifications();
    }
}
