/*
 * One conversation from the phone, and a box to answer it in.
 *
 * Grouped the way a phone's own shade groups: by conversation where the daemon is telling us who
 * a message is from, and by application otherwise. Three messages from Aditya are one row with a
 * count and one reply box, not three rows - which is the difference between a shade and a queue.
 *
 * What the daemon sends depends on one setting. With ShowNotificationContent off there is no
 * sender and no text at all, because everything on the session bus is readable by every program
 * running as this user and a mirrored notification is the most private thing Mesh Sync carries.
 * The row then says which app and which device, and still offers the reply box - the phone told
 * us the action exists without telling us what it is about.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts

import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents

Item {
    id: root

    required property MeshBus bus

    /*
     * Which conversation this row IS, as opposed to which message it is currently threading on.
     *
     * MeshBus.fill() rewrites rows in place rather than clearing and refilling - so the pointer
     * does not lose a row from under it - which means a delegate at index 0 can become a
     * different conversation while a half-typed reply is still sitting in it. The list re-sorts
     * newest-first on every arrival, so that is not a corner case. Without this, a message meant
     * for one person is sent to whoever the sort moved into that slot.
     */
    required property string identity

    onIdentityChanged: root.forget()

    required property string notificationKey
    required property string groupKeys
    required property string appName
    required property string heading
    required property string subheading
    required property string preview
    required property int count
    required property bool canReply
    required property string replyLabel

    property string status: ""
    property bool sending: false

    /*
     * The key this box is waiting on, taken when Send was pressed.
     *
     * A group carries its newest message's key, so a message arriving while a reply is in flight
     * moves notificationKey - and an answer matched against the current key would then never
     * match at all, leaving the field disabled on "Sending…" until the widget was rebuilt.
     */
    property string pendingKey: ""

    /// Everything about this row that belonged to whoever was in it before.
    function forget(): void {
        draft.text = "";
        root.status = "";
        root.sending = false;
        root.pendingKey = "";
    }

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
        spacing: Kirigami.Units.smallSpacing / 2

        RowLayout {
            Layout.fillWidth: true
            spacing: Kirigami.Units.smallSpacing

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 0

                RowLayout {
                    Layout.fillWidth: true
                    spacing: Kirigami.Units.smallSpacing

                    /* fillWidth rather than a width computed from the parent's: a child of a
                       layout that reads parent.width is feeding the layout's own output back into
                       its input, which is the shape a binding loop takes. */
                    PlasmaComponents.Label {
                        Layout.fillWidth: true
                        elide: Text.ElideRight
                        font.weight: Font.DemiBold
                        text: root.heading
                    }

                    /* The count a phone shows on a collapsed conversation, right beside the name
                       rather than stranded at the far edge - at arm's length the two have to read
                       as one thing. Only when there is more than one, because "1" on every row is
                       noise. */
                    Rectangle {
                        visible: root.count > 1
                        Layout.alignment: Qt.AlignVCenter
                        implicitWidth: Math.max(badge.implicitWidth + Kirigami.Units.smallSpacing * 1.5,
                                                badge.implicitHeight + 4)
                        implicitHeight: badge.implicitHeight + 2
                        radius: height / 2
                        color: Kirigami.Theme.highlightColor

                        PlasmaComponents.Label {
                            id: badge
                            anchors.centerIn: parent
                            font: Kirigami.Theme.smallFont
                            color: Kirigami.Theme.highlightedTextColor
                            text: root.count
                        }
                    }

                }

                PlasmaComponents.Label {
                    Layout.fillWidth: true
                    visible: root.preview.length > 0
                    elide: Text.ElideRight
                    maximumLineCount: 2
                    wrapMode: Text.Wrap
                    text: root.preview
                }

                PlasmaComponents.Label {
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    font: Kirigami.Theme.smallFont
                    opacity: 0.7
                    text: root.subheading
                }
            }

            PlasmaComponents.ToolButton {
                icon.name: "dialog-close"
                display: PlasmaComponents.AbstractButton.IconOnly
                text: root.count > 1 ? i18np("Dismiss %1 message", "Dismiss all %1 messages", root.count)
                                     : i18n("Dismiss")
                onClicked: root.bus.dismissGroup(root.groupKeys)

                PlasmaComponents.ToolTip.text: text
                PlasmaComponents.ToolTip.visible: hovered
            }
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: Kirigami.Units.smallSpacing / 2
            spacing: Kirigami.Units.smallSpacing
            visible: root.canReply

            PlasmaComponents.TextField {
                id: draft
                Layout.fillWidth: true
                enabled: !root.sending
                placeholderText: i18n("Reply to %1…", root.heading)
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

        // Threaded onto the newest message in the group, which is the one whose reply action the
        // phone still has open - and held, so the answer can be matched even if a newer message
        // has moved the group on since.
        root.pendingKey = root.notificationKey;
        root.bus.reply(root.pendingKey, text);
    }

    /* The daemon answers asynchronously and names the notification, so the right row picks it up.
       The box is cleared only on success: a reply that did not go must still be there to try
       again, not retyped from memory. */
    readonly property Connections __replies: Connections {
        target: root.bus

        function onReplied(key: string, ok: bool, message: string): void {
            if (key !== root.pendingKey || root.pendingKey.length === 0)
                return;

            root.pendingKey = "";
            root.sending = false;
            root.status = message;
            if (ok)
                draft.text = "";
        }
    }
}
