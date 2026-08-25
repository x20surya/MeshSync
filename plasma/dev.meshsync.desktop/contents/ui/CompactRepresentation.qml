/*
 * The icon in the panel or the system tray.
 *
 * Also a drop target: dragging a file onto the icon opens the widget so a device can be picked,
 * which is how KDE Connect's does it and is the only sensible answer when the drop has no device
 * attached to it yet.
 */

pragma ComponentBehavior: Bound

import QtQuick

import org.kde.kirigami as Kirigami
import org.kde.plasma.plasmoid

DropArea {
    id: root

    required property PlasmoidItem plasmoidItem

    onEntered: drag => {
        if (!drag.hasUrls)
            drag.accepted = false;
    }

    /*
     * One reachable device takes the file directly - that is the whole gesture, and having to
     * open a popup to pick the only device there is would be the wrong answer to it.
     *
     * With several, or with none, there is nothing to guess, so the popup opens and a device row
     * takes the drop instead. Opening on drop rather than on hover also stops a file dragged
     * across the panel on its way somewhere else from throwing the widget open.
     */
    onDropped: drop => {
        if (!drop.hasUrls || drop.urls.length === 0)
            return;

        const only = root.plasmoidItem.bus.onlyReachableDevice();

        if (only.length === 0) {
            root.plasmoidItem.expanded = true;
            return;
        }

        for (const url of drop.urls) {
            const file = root.plasmoidItem.bus.localPath(url);
            if (file.length > 0)
                root.plasmoidItem.bus.sendFile(only, file);
        }
    }

    MouseArea {
        anchors.fill: parent
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton

        onClicked: mouse => {
            /* Middle-click sends the clipboard. It is the one action worth a single gesture:
               everything else needs a device chosen or a fingerprint compared. The daemon reads
               the clipboard, not the widget - on Wayland a plasmoid cannot. */
            if (mouse.button === Qt.MiddleButton) {
                root.plasmoidItem.bus.sendClipboard();
                return;
            }

            root.plasmoidItem.expanded = !root.plasmoidItem.expanded;
        }
    }

    Kirigami.Icon {
        anchors.fill: parent
        source: Plasmoid.icon
        active: parent.containsDrag
    }
}
