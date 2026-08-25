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
import QtQuick.Controls as QQC2
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.plasma.plasmoid

PlasmaExtras.Representation {
    id: root

    required property MeshBus bus

    /*
     * As tall as it needs to be, between a floor and a ceiling.
     *
     * It used to be a flat 26 grid units whatever it held, so one device and no notifications
     * drew a single row and then two thirds of a popup of nothing. A panel widget is expected to
     * size to its content. The floor gives the empty state room to sit in rather than squeezing
     * it; the ceiling makes a busy shade scroll instead of covering the screen.
     */
    Layout.minimumWidth: Kirigami.Units.gridUnit * 20
    Layout.preferredWidth: Kirigami.Units.gridUnit * 24
    Layout.minimumHeight: Kirigami.Units.gridUnit * 12
    /*
     * The header and the footer are part of this height and are not part of the scroll area, so
     * they have to be added back or the popup is exactly that much too short - the scroll view
     * then decides it is overflowing, shows a bar, narrows the content, rewraps the previews,
     * gets a different content height, and Plasma's ScrollBar reports the circle as a binding
     * loop on `visible`. The extra grid unit on top is headroom, so the answer to "is this
     * overflowing" is decisive rather than exactly on its own boundary.
     */
    readonly property real chrome: (root.header ? root.header.height : 0)
                                 + (root.footer ? root.footer.height : 0)
                                 + Kirigami.Units.gridUnit

    readonly property real ceiling: Kirigami.Units.gridUnit * 30

    Layout.preferredHeight: Math.min(root.ceiling,
                                     Math.max(Kirigami.Units.gridUnit * 12,
                                              stack.implicitHeight + root.chrome))

    collapseMarginsHint: true

    /* One read on the way in, so a popup opened after a long idle is right before the first
       change arrives. Everything after that is event-driven - see MeshBus. */
    Component.onCompleted: root.bus.refreshObjects()

    header: PlasmaExtras.PlasmoidHeading {
        contentItem: RowLayout {
            spacing: Kirigami.Units.smallSpacing

            /* Medium, and centred against the two-line block beside it. At iconSizes.small the
               mark sat level with the first line and looked like it had slipped. */
            Kirigami.Icon {
                source: "meshsync"
                Layout.alignment: Qt.AlignVCenter
                Layout.preferredWidth: Kirigami.Units.iconSizes.medium
                Layout.preferredHeight: Kirigami.Units.iconSizes.medium
                Layout.rightMargin: Kirigami.Units.smallSpacing / 2
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
                icon.name: "list-add-symbolic"
                display: PlasmaComponents.AbstractButton.IconOnly
                text: i18n("Pair a device…")
                visible: root.bus.available
                onClicked: root.bus.show("devices")

                PlasmaComponents.ToolTip.text: text
                PlasmaComponents.ToolTip.visible: hovered
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

            /* Not "configure". That icon means settings everywhere else in Plasma, and Plasma's
               own header puts the real settings button beside it - two sliders side by side, one
               of which opened an application. */
            PlasmaComponents.ToolButton {
                icon.name: "window-new-symbolic"
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
            id: scroller
            anchors.fill: parent
            visible: root.bus.available

            contentWidth: availableWidth

            /*
             * This popup never scrolls sideways.
             *
             * Every row here elides or wraps, so a horizontal bar can only ever appear because a
             * Text reported the width it would LIKE - implicitWidth, the whole unwrapped string -
             * and inflated the column that contains it. One long notification preview was enough
             * to put a scrollbar under the whole widget.
             *
             * It also holds bottomPadding at zero, which is one of the two things the vertical
             * bar's own visibility was chasing round in a circle.
             */
            QQC2.ScrollBar.horizontal.policy: QQC2.ScrollBar.AlwaysOff

            ColumnLayout {
                id: stack
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
                        ringing: model.IsRinging === true
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
                        identity: model.path
                        notificationKey: model.key
                        groupKeys: model.keys
                        appName: model.appName
                        heading: model.heading
                        subheading: model.subheading
                        preview: model.preview
                        count: model.count
                        canReply: model.canReply === true
                        replyLabel: model.replyLabel
                    }
                }

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
