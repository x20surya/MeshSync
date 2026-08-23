/*
 * The widget's settings.
 *
 * Two of these are the widget's own and are stored by Plasma. The third is not: it belongs to
 * Mesh Sync itself and is written over the bus, because the thing it controls - whether the app
 * draws its own tray icon - is the app's, and a copy of it kept here would be a second answer to
 * the same question.
 */

pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls as QQC2
import QtQuick.Layouts

import org.kde.kirigami as Kirigami

Kirigami.FormLayout {
    id: root

    property alias cfg_showNotifications: showNotifications.checked
    property alias cfg_showDevices: showDevices.checked

    readonly property MeshBus bus: MeshBus { }

    QQC2.CheckBox {
        id: showDevices
        Kirigami.FormData.label: i18n("Show:")
        text: i18n("Your devices")
    }

    QQC2.CheckBox {
        id: showNotifications
        text: i18n("Notifications from your phone")
    }

    QQC2.CheckBox {
        id: showContent
        text: i18n("Who each message is from, and a preview")
        enabled: root.bus.available && showNotifications.checked
        checked: root.bus.showNotificationContent
        onToggled: root.bus.setNotificationContent(checked)
    }

    QQC2.Label {
        Layout.maximumWidth: Kirigami.Units.gridUnit * 22
        wrapMode: Text.Wrap
        font: Kirigami.Theme.smallFont
        opacity: 0.7
        text: i18n("Off, the widget groups by app and can still reply, but shows nothing a message says. On, it groups by conversation like your phone does - and the text is then on the session bus, where any program running as you can read it.")
    }

    Item { Kirigami.FormData.isSection: true }

    QQC2.CheckBox {
        id: appTrayIcon
        Kirigami.FormData.label: i18n("Mesh Sync:")
        text: i18n("Also show its own tray icon")
        enabled: root.bus.available
        checked: root.bus.trayIconVisible
        onToggled: root.bus.setTrayIconVisible(checked)
    }

    QQC2.Label {
        Layout.maximumWidth: Kirigami.Units.gridUnit * 22
        wrapMode: Text.Wrap
        font: Kirigami.Theme.smallFont
        opacity: 0.7
        text: root.bus.available
            ? i18n("Turn this off if you have put this widget in the system tray, so the same mark is not there twice.")
            : i18n("Mesh Sync is not running, so this cannot be changed right now.")
    }
}
