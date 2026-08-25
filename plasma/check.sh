#!/usr/bin/env bash
#
# The Plasma widget's only executable check.
#
# Starts a scratch daemon, loads the real MeshBus.qml under plasmawindowed, calls every function
# on it once, and reads dbus-monitor to answer the one question that matters: did the arguments
# reach the wire.
#
# WHY THE WIRE AND NOT THE REPLY. Every defect this was written for produces a call that is
# dispatched, answered and logged as an ordinary failure:
#
#   - a `signature` set on the message makes the QML binding send an EMPTY BODY, so the daemon
#     answers "Unexpected end of data" and the widget shows a dead button;
#   - DBus.string(x) without `new` raises a TypeError inside the calling function, so asyncCall
#     is never reached and nothing is sent at all;
#   - a daemon that does not declare org.freedesktop.DBus.Properties in its introspection makes
#     Qt drop the arguments to Get and Set, because Qt introspects before it marshals.
#
# None of the three is visible to a test that only asks whether an exception was raised, and none
# of them can be caught by meshsyncctl - gdbus encodes arguments correctly, so the shell tool
# passes against a surface no Qt client can use. Counting bytes on the wire catches all three.
#
# Usage:  plasma/check.sh [--keep]
#           --keep   leave the scratch data directory and the capture behind for inspection
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
WIDGET="$HERE/dev.meshsync.desktop"
PROBE_SRC="$HERE/check"

KEEP=0
[ "${1:-}" = "--keep" ] && KEEP=1

red()   { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }
dim()   { printf '\033[2m%s\033[0m\n' "$*"; }

need() {
    command -v "$1" >/dev/null 2>&1 || {
        red "plasma/check.sh needs $1. $2"
        exit 2
    }
}

need plasmawindowed "It ships with plasma-workspace."
need dbus-monitor   "It ships with dbus (dbus-bin on Debian)."
need dotnet         "The check runs a scratch daemon so nothing real is touched."

[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ] || { red "No session bus. This check needs a desktop session."; exit 2; }

WORK="$(mktemp -d /tmp/meshsync-check.XXXXXX)"
DATA="$WORK/data"
CAPTURE="$WORK/wire.log"
DAEMON_LOG="$WORK/daemon.log"
PROBE_LOG="$WORK/probe.log"
mkdir -p "$DATA"

DAEMON_PID=""
MONITOR_PID=""

cleanup() {
    [ -n "${PROBE_PID:-}" ] && kill "$PROBE_PID" 2>/dev/null
    [ -n "$MONITOR_PID" ] && kill "$MONITOR_PID" 2>/dev/null
    [ -n "$DAEMON_PID" ]  && kill "$DAEMON_PID"  2>/dev/null
    wait 2>/dev/null
    if [ "$KEEP" = "1" ]; then
        dim "kept: $WORK"
    else
        rm -rf "$WORK"
    fi
}
trap cleanup EXIT INT TERM

# ─────────────────────────────────────────────────────────────── the scratch daemon
#
# Wi-Fi only and a port nothing else uses, so the check never touches the radio and never
# collides with a Mesh Sync the person running it is actually using. It will not win the
# well-known name if one is already running - that is expected, and its unique name is what the
# probe is pointed at.

printf 'WiFi\n' > "$DATA/transport"

dim "starting a scratch daemon in $DATA"
dotnet run --project "$REPO/src/LinuxDaemon/LinuxDaemon.csproj" -- \
    --data "$DATA" --port 45099 --no-shell > "$DAEMON_LOG" 2>&1 &
DAEMON_PID=$!

SERVICE=""
for _ in $(seq 1 120); do
    kill -0 "$DAEMON_PID" 2>/dev/null || { red "The scratch daemon exited."; sed -n '1,40p' "$DAEMON_LOG"; exit 2; }

    # Either it took the well-known name, or it logged the unique name it is serving on instead.
    if grep -q "Publishing on the session bus" "$DATA/daemon.log" 2>/dev/null; then
        SERVICE="dev.meshsync.Daemon"; break
    fi
    SERVICE="$(sed -n 's/.*serving on \(:[0-9.]*\) only.*/\1/p' "$DATA/daemon.log" 2>/dev/null | tail -1)"
    [ -n "$SERVICE" ] && break
    sleep 0.5
done

[ -n "$SERVICE" ] || { red "The scratch daemon never reached the session bus."; tail -20 "$DATA/daemon.log" 2>/dev/null; exit 2; }
dim "scratch daemon is $SERVICE"

# ─────────────────────────────────────────────────────────────── the probe package
#
# MeshBus.qml is COPIED, not duplicated: the check drives the file that ships, so a fix here and
# a regression there cannot drift apart.

PKGROOT="$WORK/xdg/plasma/plasmoids/dev.meshsync.check"
mkdir -p "$PKGROOT/contents/ui"
cp "$PROBE_SRC/metadata.json" "$PKGROOT/metadata.json"
cp "$WIDGET/contents/ui/MeshBus.qml" "$PKGROOT/contents/ui/MeshBus.qml"
sed "s|@SERVICE@|$SERVICE|" "$PROBE_SRC/contents/ui/main.qml" > "$PKGROOT/contents/ui/main.qml"

# ─────────────────────────────────────────────────────────────── capture and run

dbus-monitor --session "destination='$SERVICE'" > "$CAPTURE" 2>/dev/null &
MONITOR_PID=$!
sleep 1

XDG_DATA_DIRS="$WORK/xdg:${XDG_DATA_DIRS:-/usr/local/share:/usr/share}" \
    timeout 60 plasmawindowed dev.meshsync.check > "$PROBE_LOG" 2>&1 &
PROBE_PID=$!

# The sweep first, then something changes underneath the widget. Renaming the mesh is the only
# change a scratch daemon with no peers can be made to publish, and it is enough to prove the
# widget is taking PropertiesChanged rather than polling: the widget used to derive everything
# from four counts, so a change that moved no count reached it only via a ten second timer.
wait_for() {
    for _ in $(seq 1 120); do
        grep -q "$1" "$PROBE_LOG" 2>/dev/null && return 0
        kill -0 "$PROBE_PID" 2>/dev/null || return 1
        sleep 0.5
    done
    return 1
}

if wait_for "CHECK|swept"; then
    gdbus call --session -d "$SERVICE" -o /dev/meshsync/Daemon \
        -m org.freedesktop.DBus.Properties.Set \
        dev.meshsync.Daemon1 MeshName "<'meshsync-check-live'>" >/dev/null 2>&1
fi

wait_for "CHECK|done|" || true
kill "$PROBE_PID" 2>/dev/null
wait "$PROBE_PID" 2>/dev/null

sleep 1
kill "$MONITOR_PID" 2>/dev/null; MONITOR_PID=""

grep -q "CHECK|done|" "$PROBE_LOG" || {
    red "The probe did not finish. It usually means the applet refused to load."
    grep -E "\.qml:|CHECK\|" "$PROBE_LOG" | head -20
    exit 2
}

# ─────────────────────────────────────────────────────────────── read the wire
#
# dbus-monitor indents a top-level argument by exactly three spaces; anything nested is deeper.
# So the body size of a call is the number of lines matching /^   [^ ]/ before the next record.

body_counts() {
    awk '
        function flush() { if (current != "") { print current "\t" count; current = "" } }
        # Any line at column zero closes the record before it. Calls arrive back to back, so
        # flushing only on a reply would keep the last of each run and lose the rest.
        /^method call / {
            flush()
            member = ""
            if (match($0, /member=[^ ]+/)) member = substr($0, RSTART + 7, RLENGTH - 7)
            current = member; count = 0
            next
        }
        /^   [^ ]/ { if (current != "") count++ ; next }
        /^[^ ]/ { flush(); next }
        END { flush() }
    ' "$CAPTURE"
}

COUNTS="$WORK/counts.txt"
body_counts > "$COUNTS"

# control name : bus member : how many arguments the body must carry
EXPECT="
reconnect|Dial|0
stop-ringing|StopRinging|0
send-clipboard|SendClipboard|0
dismiss-all|DismissAllNotifications|0
notifications|Notifications|0
object-tree|GetManagedObjects|0
open-mesh-sync|Show|1
send-text|SendText|1
dismiss|DismissNotification|1
reply|ReplyToNotification|2
ring|Ring|1
send-file|SendFile|1
forget|Forget|0
confirm|Confirm|0
reject|Reject|0
set-transport|Set|3
set-tray-icon|Set|3
set-content|Set|3
"

printf '\n%-18s %-26s %s\n' "CONTROL" "BUS MEMBER" "RESULT"
printf '%s\n' "──────────────────────────────────────────────────────────────────────"

FAILED=0
PASSED=0

for row in $EXPECT; do
    name="${row%%|*}"; rest="${row#*|}"
    member="${rest%%|*}"; want="${rest##*|}"

    if grep -q "CHECK|threw|$name|" "$PROBE_LOG"; then
        why="$(sed -n "s/.*CHECK|threw|$name|//p" "$PROBE_LOG" | head -1)"
        printf '%-18s %-26s ' "$name" "$member"; red "THREW  $why"
        FAILED=$((FAILED + 1)); continue
    fi

    # Every body recorded for this member; Set appears three times, so all of them must be right.
    got="$(awk -F'\t' -v m="$member" '$1 == m { print $2 }' "$COUNTS" | sort -u | tr '\n' ' ')"

    if [ -z "$got" ]; then
        printf '%-18s %-26s ' "$name" "$member"; red "NOT SENT  nothing reached the bus"
        FAILED=$((FAILED + 1)); continue
    fi

    ok=1
    for n in $got; do [ "$n" = "$want" ] || ok=0; done

    if [ "$ok" = "1" ]; then
        printf '%-18s %-26s ' "$name" "$member"; green "ok  $want arg(s)"
        PASSED=$((PASSED + 1))
    else
        printf '%-18s %-26s ' "$name" "$member"
        red "EMPTY BODY  wanted $want arg(s), wire carried: ${got% }"
        FAILED=$((FAILED + 1))
    fi
done

# ─────────────────────────────────────────────────────────────── does it notice
#
# Two halves. The widget has to re-read when the daemon says a property moved, and the daemon has
# to publish the one property that says the device tree moved - because no count does. The first
# is exercised above; the second is a contract check, since a daemon with no peers has no tree to
# move. See the probe for why they cannot be the same test.

printf '\n'
if grep -q "CHECK|live|mesh-name" "$PROBE_LOG"; then
    printf '%-18s %-26s ' "live-update" "PropertiesChanged"; green "ok  the rename arrived"
    PASSED=$((PASSED + 1))
else
    printf '%-18s %-26s ' "live-update" "PropertiesChanged"
    red "MISSED  the widget never saw the mesh renamed under it"
    FAILED=$((FAILED + 1))
fi

REVISION="$(gdbus call --session -d "$SERVICE" -o /dev/meshsync/Daemon \
    -m org.freedesktop.DBus.Properties.Get dev.meshsync.Daemon1 TreeRevision 2>&1 || true)"

if printf '%s' "$REVISION" | grep -q "uint32"; then
    printf '%-18s %-26s ' "tree-revision" "Daemon1.TreeRevision"; green "ok  published as uint32"
    PASSED=$((PASSED + 1))
else
    printf '%-18s %-26s ' "tree-revision" "Daemon1.TreeRevision"
    red "MISSING  the device list has nothing to refresh on"
    FAILED=$((FAILED + 1))
fi

printf '\n'

# A call may be answered with an error and still be correct - Ring on a fingerprint no device has
# is answered NoSuchDevice by design. Only errors that mean the body was wrong are failures.
MALFORMED="$(grep -c "Unexpected end of data" "$CAPTURE" 2>/dev/null || true)"
MALFORMED="${MALFORMED:-0}"

printf '%s\n' "──────────────────────────────────────────────────────────────────────"
if [ "$MALFORMED" != "0" ]; then
    red "$MALFORMED call(s) were answered \"Unexpected end of data\" - a body was short."
fi

if [ "$FAILED" = "0" ]; then
    green "$PASSED/$((PASSED + FAILED)) controls reached the daemon with their arguments."
    exit 0
fi

red "$FAILED of $((PASSED + FAILED)) controls did not reach the daemon."
dim "re-run with --keep to inspect the capture and the probe log"
exit 1
