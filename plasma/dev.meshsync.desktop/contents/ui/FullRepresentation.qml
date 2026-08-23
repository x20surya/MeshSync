/*
 * What opens when the icon is clicked, and what is drawn if the widget is dropped straight onto
 * the desktop.
 *
 * Order is deliberate: pairing requests first because they are the only thing here that is
 * waiting on a person, then devices, then notifications, then the actions. Nothing below the
 * fold is urgent.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.plasma.plasmoid

PlasmaExtras.Representation {
    id: root

    required property MeshBus bus

    Layout.minimumWidth: Kirigami.Units.gridUnit * 20
    Layout.minimumHeight: Kirigami.Units.gridUnit * 16
    Layout.preferredWidth: Kirigami.Units.gridUnit * 24
    Layout.preferredHeight: Kirigami.Units.gridUnit * 26

    collapseMarginsHint: true

    /* Only refresh the tree while somebody is looking at it. */
    Component.onCompleted: root.bus.watching = true
    Component.onDestruction: root.bus.watching = false

    header: PlasmaExtras.PlasmoidHeading {
        contentItem: RowLayout {
            spacing: Kirigami.Units.smallSpacing

            Kirigami.Icon {
                source: "meshsync"
                Layout.preferredWidth: Kirigami.Units.iconSizes.small
                Layout.preferredHeight: Kirigami.Units.iconSizes.small
                visible: root.bus.available
            }

            ColumnLayout {
                spacing: 0
                Layout.fillWidth: true

                PlasmaExtras.Heading {
                    level: 5
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    text: root.bus.available && root.bus.meshName.length > 0
                        ? root.bus.meshName : i18n("Mesh Sync")
                }

                PlasmaComponents.Label {
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    font: Kirigami.Theme.smallFont
                    opacity: 0.7
                    visible: root.bus.available
                    text: root.bus.bluetoothStatus.length > 0
                        ? i18n("%1 · Bluetooth %2", root.bus.deviceName, root.bus.bluetoothStatus)
                        : root.bus.deviceName
                }
            }

            PlasmaComponents.ToolButton {
                icon.name: "view-refresh-symbolic"
                display: PlasmaComponents.AbstractButton.IconOnly
                text: i18n("Reconnect now")
                visible: root.bus.available
                onClicked: root.bus.dial()

                PlasmaComponents.ToolTip.text: text
                PlasmaComponents.ToolTip.visible: hovered
            }

            PlasmaComponents.ToolButton {
                icon.name: "configure"
                display: PlasmaComponents.AbstractButton.IconOnly
                text: i18n("Open Mesh Sync…")
                visible: root.bus.available
                onClicked: root.bus.show("home")

                PlasmaComponents.ToolTip.text: text
                PlasmaComponents.ToolTip.visible: hovered
            }
        }
    }

    contentItem: Item {
        NotRunning {
            anchors.fill: parent
            visible: !root.bus.available
            bus: root.bus
        }

        PlasmaComponents.ScrollView {
            anchors.fill: parent
            visible: root.bus.available
            contentWidth: availableWidth

            ColumnLayout {
                width: parent.width
                spacing: Kirigami.Units.smallSpacing

                // ─────────── a device is asking to join

                Repeater {
                    model: root.bus.pending

                    delegate: PairingRequest {
                        required property var model
                        Layout.fillWidth: true
                        bus: root.bus
                        objectPath: model.path
                        deviceName: model.Name || i18n("A device")
                        fingerprint: model.ShortFingerprint || ""
                    }
                }

                // ─────────── devices

                Kirigami.Heading {
                    Layout.fillWidth: true
                    Layout.topMargin: Kirigami.Units.smallSpacing
                    level: 6
                    opacity: 0.7
                    text: i18n("Devices")
                    visible: root.bus.devices.count > 0 && Plasmoid.configuration.showDevices
                }

                Repeater {
                    model: Plasmoid.configuration.showDevices ? root.bus.devices : null

                    delegate: DeviceDelegate {
                        required property var model
                        Layout.fillWidth: true
                        bus: root.bus
                        objectPath: model.path
                        deviceName: model.Name || model.ShortFingerprint || i18n("Unnamed device")
                        fingerprint: model.ShortFingerprint || ""
                        connected: model.IsConnected === true
                        activeLink: model.ActiveLink || "none"
                        lastAddress: model.LastAddress || ""
                        lastSeen: model.LastSeen || 0
                    }
                }

                PlasmaExtras.PlaceholderMessage {
                    Layout.fillWidth: true
                    Layout.topMargin: Kirigami.Units.gridUnit
                    visible: root.bus.devices.count === 0 && root.bus.pending.count === 0
                    iconName: "list-add-symbolic"
                    text: i18n("No devices yet")
                    explanation: i18n("Open Mesh Sync and show a pairing code, then scan it on your phone.")

                    helpfulAction: Kirigami.Action {
                        icon.name: "meshsync"
                        text: i18n("Pair a device")
                        onTriggered: root.bus.show("devices")
                    }
                }

                // ─────────── notifications from the phone

                Kirigami.Heading {
                    Layout.fillWidth: true
                    Layout.topMargin: Kirigami.Units.smallSpacing
                    level: 6
                    opacity: 0.7
                    text: i18n("From your phone")
                    visible: root.bus.notifications.count > 0 && Plasmoid.configuration.showNotifications
                }

                Repeater {
                    model: Plasmoid.configuration.showNotifications ? root.bus.notifications : null

                    delegate: NotificationDelegate {
                        required property var model
                        Layout.fillWidth: true
                        bus: root.bus
                        notificationKey: model.key
                        appName: model.appName
                        from: model.from
                        canReply: model.canReply === true
                        replyLabel: model.replyLabel
                    }
                }

                Item { Layout.fillHeight: true }
            }
        }
    }

    footer: PlasmaExtras.PlasmoidHeading {
        position: PlasmaExtras.PlasmoidHeading.Position.Footer
        visible: root.bus.available

        contentItem: ColumnLayout {
            spacing: Kirigami.Units.smallSpacing

            PlasmaComponents.Label {
                Layout.fillWidth: true
                visible: text.length > 0
                elide: Text.ElideRight
                font: Kirigami.Theme.smallFont
                opacity: 0.75
                text: root.bus.lastMessage
            }

            RowLayout {
                spacing: Kirigami.Units.smallSpacing

                PlasmaComponents.Button {
                    Layout.fillWidth: true
                    icon.name: "edit-paste-symbolic"
                    text: i18n("Send clipboard")
                    onClicked: root.bus.sendClipboard()
                }

                /* Only while this computer is the one being looked for. It sits in the footer
                   rather than in a page, because the alarm can start while the widget is showing
                   anything and the way to stop it must not be somewhere else. */
                PlasmaComponents.Button {
                    icon.name: "audio-volume-muted-symbolic"
                    text: i18n("Stop ringing")
                    visible: root.bus.ringing
                    onClicked: root.bus.stopRinging()
                }
            }
        }
    }
}
