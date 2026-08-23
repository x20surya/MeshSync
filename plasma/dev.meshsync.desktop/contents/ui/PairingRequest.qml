/*
 * A device asking to join, with its fingerprint next to the two answers.
 *
 * This is the one thing in the widget that is waiting on a person, and it is why the widget pops
 * itself open. Pairing is two-sided and silently half-finishes: the phone trusts the laptop, the
 * laptop never completes "Allow", and the phone then dials several times a second forever while
 * the log repeats "not a paired device, and pairing is not open". Putting the knock on the panel,
 * with the fingerprint beside it, removes that failure rather than reporting it.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents

Rectangle {
    id: root

    required property MeshBus bus
    required property string objectPath
    required property string deviceName
    required property string fingerprint

    implicitHeight: layout.implicitHeight + Kirigami.Units.largeSpacing
    radius: Kirigami.Units.cornerRadius
    color: "transparent"
    border.width: 1
    border.color: Kirigami.Theme.neutralTextColor

    ColumnLayout {
        id: layout
        anchors.fill: parent
        anchors.margins: Kirigami.Units.smallSpacing
        spacing: Kirigami.Units.smallSpacing

        RowLayout {
            spacing: Kirigami.Units.smallSpacing

            Kirigami.Icon {
                source: "dialog-warning-symbolic"
                Layout.preferredWidth: Kirigami.Units.iconSizes.small
                Layout.preferredHeight: Kirigami.Units.iconSizes.small
                color: Kirigami.Theme.neutralTextColor
            }

            PlasmaComponents.Label {
                Layout.fillWidth: true
                elide: Text.ElideRight
                font.weight: Font.DemiBold
                color: Kirigami.Theme.neutralTextColor
                text: i18n("%1 wants to join", root.deviceName)
            }
        }

        PlasmaComponents.Label {
            Layout.fillWidth: true
            wrapMode: Text.WrapAnywhere
            font: Kirigami.Theme.smallFont
            opacity: 0.85
            text: root.fingerprint
        }

        PlasmaComponents.Label {
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            font: Kirigami.Theme.smallFont
            opacity: 0.7
            text: i18n("Check this matches the code on the other device before allowing it.")
        }

        RowLayout {
            spacing: Kirigami.Units.smallSpacing

            PlasmaComponents.Button {
                Layout.fillWidth: true
                icon.name: "dialog-ok-apply-symbolic"
                text: i18n("Allow")
                onClicked: root.bus.confirm(root.objectPath)
            }

            PlasmaComponents.Button {
                Layout.fillWidth: true
                icon.name: "dialog-cancel-symbolic"
                text: i18n("Refuse")
                onClicked: root.bus.reject(root.objectPath)
            }
        }
    }
}
