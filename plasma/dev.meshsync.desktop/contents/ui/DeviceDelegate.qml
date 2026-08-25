/*
 * One paired device: whether it is reachable, over what, and the two things worth doing to it
 * from a panel - make it ring, and send it a file.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents

Item {
    id: root

    required property MeshBus bus
    required property string objectPath
    required property string deviceName
    required property string fingerprint
    required property bool connected
    required property string activeLink
    required property string lastAddress
    required property double lastSeen

    /* Whether this device has asked that one to ring - answered by the daemon, not guessed here.
       It used to be a delegate-local bool, which survives a row being reused for a different
       device and is lost when the popup is rebuilt, so the button could offer to stop a ring it
       never started and forget one it did. */
    required property bool ringing

    implicitHeight: layout.implicitHeight + Kirigami.Units.smallSpacing * 2

    /* The Mesh Sync accent, and the only place in the widget that is not Plasma's own colour.
       A brand is allowed one dot. */
    readonly property color accent: Kirigami.Theme.colorSet === Kirigami.Theme.Complementary
        ? "#4FA894" : (root.darkTheme ? "#4FA894" : "#2F7A6B")

    readonly property bool darkTheme: Kirigami.Theme.backgroundColor.hslLightness < 0.5

    Rectangle {
        anchors.fill: parent
        radius: Kirigami.Units.cornerRadius
        color: Kirigami.Theme.textColor
        opacity: hover.hovered ? 0.06 : 0
        Behavior on opacity { NumberAnimation { duration: Kirigami.Units.shortDuration } }
    }

    HoverHandler { id: hover }

    RowLayout {
        id: layout
        anchors.fill: parent
        anchors.margins: Kirigami.Units.smallSpacing
        spacing: Kirigami.Units.smallSpacing

        Rectangle {
            Layout.alignment: Qt.AlignVCenter
            implicitWidth: Kirigami.Units.gridUnit * 0.55
            implicitHeight: implicitWidth
            radius: width / 2
            color: root.connected ? root.accent : "transparent"
            border.width: root.connected ? 0 : 1
            border.color: Kirigami.Theme.disabledTextColor
        }

        ColumnLayout {
            Layout.fillWidth: true
            spacing: 0

            PlasmaComponents.Label {
                Layout.fillWidth: true
                elide: Text.ElideRight
                text: root.deviceName
            }

            PlasmaComponents.Label {
                Layout.fillWidth: true
                elide: Text.ElideRight
                font: Kirigami.Theme.smallFont
                opacity: 0.7
                text: {
                    if (root.connected)
                        return root.activeLink === "ble" ? i18n("Bluetooth") : i18n("Wi-Fi · %1", root.lastAddress);
                    if (root.lastSeen > 0)
                        return i18n("last seen %1", root.relativeAge(root.lastSeen));
                    return root.fingerprint;
                }
            }
        }

        PlasmaComponents.ToolButton {
            /* A bell, not a loudspeaker. The volume icons say "this device is making a noise",
               which is the opposite of what this button is for. */
            icon.name: root.ringing ? "audio-volume-muted-symbolic" : "notifications-symbolic"
            display: PlasmaComponents.AbstractButton.IconOnly
            text: root.ringing ? i18n("Stop ringing %1", root.deviceName) : i18n("Ring %1", root.deviceName)
            enabled: root.connected
            /* Nothing is flipped here. The daemon answers whether it was asked, and the row
               redraws when that answer changes - so a request that did not arrive leaves the
               button saying "ring", which is the truth. */
            onClicked: root.bus.ring(root.objectPath, !root.ringing)

            PlasmaComponents.ToolTip.text: text
            PlasmaComponents.ToolTip.visible: hovered
        }
    }

    /* Compact enough for a panel row. The daemon sends a unix timestamp rather than a phrase,
       because the phrase has to be in the reader's language and the daemon does not know it.

       Reads bus.now rather than Date.now() so the binding has something that changes to depend
       on. With Date.now() it is evaluated once and never again, so a row said "3 minutes ago"
       an hour later. */
    function relativeAge(at: double): string {
        const seconds = Math.max(0, Math.floor(root.bus.now - at));

        if (seconds < 60) return i18n("just now");
        if (seconds < 3600) return i18np("%1 minute ago", "%1 minutes ago", Math.floor(seconds / 60));
        if (seconds < 86400) return i18np("%1 hour ago", "%1 hours ago", Math.floor(seconds / 3600));
        return i18np("%1 day ago", "%1 days ago", Math.floor(seconds / 86400));
    }

    /* A file dropped on a device row goes to that device. The compact representation opens the
       widget on a drag for exactly this. */
    DropArea {
        anchors.fill: parent
        onDropped: drop => {
            if (!drop.hasUrls || drop.urls.length === 0)
                return;

            for (const url of drop.urls) {
                const file = root.bus.localPath(url);
                if (file.length > 0)
                    root.bus.sendFile(root.objectPath, file);
            }
        }
    }
}
