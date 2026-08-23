/*
 * One mirrored phone notification, and a box to answer it from.
 *
 * What is deliberately not here: the title and the body. The daemon does not put them on the
 * session bus, because everything on that bus is readable by every program running as this user
 * and a mirrored notification is the most private thing Mesh Sync carries. So the panel says
 * which app and which device, and reading it means opening the window - where it already was.
 *
 * The reply box appears only when the phone said the notification carried a reply action.
 * Offering one that did nothing would be a message the user believes they sent.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents

Item {
    id: root

    required property MeshBus bus
    required property string notificationKey
    required property string appName
    required property string from
    required property bool canReply
    required property string replyLabel

    property string status: ""
    property bool sending: false

    implicitHeight: layout.implicitHeight + Kirigami.Units.smallSpacing * 2

    Rectangle {
        anchors.fill: parent
        radius: Kirigami.Units.cornerRadius
        color: Kirigami.Theme.textColor
        opacity: hover.hovered ? 0.06 : 0
        Behavior on opacity { NumberAnimation { duration: Kirigami.Units.shortDuration } }
    }

    HoverHandler { id: hover }

    ColumnLayout {
        id: layout
        anchors.fill: parent
        anchors.margins: Kirigami.Units.smallSpacing
        spacing: Kirigami.Units.smallSpacing

        RowLayout {
            Layout.fillWidth: true
            spacing: Kirigami.Units.smallSpacing

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 0

                PlasmaComponents.Label {
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    text: root.appName
                }

                PlasmaComponents.Label {
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    font: Kirigami.Theme.smallFont
                    opacity: 0.7
                    text: i18n("on %1", root.from)
                }
            }

            PlasmaComponents.ToolButton {
                icon.name: "dialog-close"
                display: PlasmaComponents.AbstractButton.IconOnly
                text: i18n("Dismiss")
                onClicked: root.bus.dismiss(root.notificationKey)

                PlasmaComponents.ToolTip.text: text
                PlasmaComponents.ToolTip.visible: hovered
            }
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: Kirigami.Units.smallSpacing
            visible: root.canReply

            PlasmaComponents.TextField {
                id: draft
                Layout.fillWidth: true
                enabled: !root.sending
                placeholderText: i18n("Reply to %1…", root.appName)
                onAccepted: root.send()
            }

            PlasmaComponents.Button {
                icon.name: "document-send-symbolic"
                text: root.replyLabel
                enabled: !root.sending && draft.text.trim().length > 0
                onClicked: root.send()
            }
        }

        PlasmaComponents.Label {
            Layout.fillWidth: true
            visible: root.status.length > 0
            wrapMode: Text.Wrap
            font: Kirigami.Theme.smallFont
            opacity: 0.75
            text: root.status
        }
    }

    function send(): void {
        const text = draft.text.trim();
        if (text.length === 0)
            return;

        root.sending = true;
        root.status = i18n("Sending…");
        root.bus.reply(root.notificationKey, text);
    }

    /* The daemon answers asynchronously and the answer names the notification, so the right row
       picks it up. Clearing the box only on success is deliberate: a reply that did not go must
       still be there to try again, not retyped from memory. */
    readonly property Connections __replies: Connections {
        target: root.bus

        function onReplied(key: string, ok: bool, message: string): void {
            if (key !== root.notificationKey)
                return;

            root.sending = false;
            root.status = message;
            if (ok)
                draft.text = "";
        }
    }
}
