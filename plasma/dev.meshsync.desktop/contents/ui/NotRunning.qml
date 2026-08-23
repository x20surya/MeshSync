/*
 * Mesh Sync is not on the bus.
 *
 * A state the widget draws rather than an error it hides, which is the same rule the clipboard
 * follows for a compositor without ext-data-control. Starting it is offered rather than done
 * silently: a widget that launches a background app on its own is not something a panel should
 * do without being asked.
 */

pragma ComponentBehavior: Bound

import QtQuick

import org.kde.kirigami as Kirigami
import org.kde.plasma.extras as PlasmaExtras

Item {
    id: root

    required property MeshBus bus

    PlasmaExtras.PlaceholderMessage {
        anchors.centerIn: parent
        width: parent.width - Kirigami.Units.gridUnit * 2

        iconName: "meshsync"
        text: i18n("Mesh Sync is not running")
        explanation: i18n("Start it and this widget fills in by itself.")

        helpfulAction: Kirigami.Action {
            icon.name: "system-run-symbolic"
            text: i18n("Start Mesh Sync")

            /* D-Bus activation. Calling any method on a name nobody owns makes the bus start
               the service named in dev.meshsync.Daemon.service, which the package installs -
               so no path to a binary is written down here and it works the same from a .deb, a
               tarball or an AppImage. Show is the right method to use for it: if the daemon
               turns out to be running already, raising its window is a reasonable thing to have
               done. */
            onTriggered: root.bus.show("home")
        }
    }
}
