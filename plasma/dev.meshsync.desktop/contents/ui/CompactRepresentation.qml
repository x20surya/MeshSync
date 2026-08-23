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
        if (drag.hasUrls)
            root.plasmoidItem.expanded = true;
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
