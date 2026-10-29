#!/usr/bin/env bash
# ScreenWorm installer for KDE Plasma 6 on Wayland (Arch/CachyOS, Debian/Ubuntu family).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
BINDIR="$PREFIX/bin"
APPDIR="$PREFIX/share/applications"
ICONDIR="$PREFIX/share/icons/hicolor/256x256/apps"
CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"
AUTOSTART="$CONFIG_HOME/autostart"
CONFIGDIR="$CONFIG_HOME/screenworm"
STATUSDIR="$STATE_HOME/screenworm"
TIMEOUT=240
ACTION=install
AUTOSTART_ON=1
IDLE_ON=1
PURGE=0
BINARY=""

usage() {
    cat <<EOF
Usage: linux/install.sh [--install|--uninstall] [options]
  --prefix DIR      install under DIR (default: \$HOME/.local)
  --timeout SEC     idle seconds before the screensaver starts (default: 240)
  --binary PATH     use an already-built screenworm binary
  --no-autostart    do not create the KDE autostart entry
  --no-idle         do not build/install the Wayland idle helper
  --purge           with --uninstall, also remove config and logs
  -h, --help        show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install) ACTION=install ;;
        --uninstall) ACTION=uninstall ;;
        --prefix) PREFIX="$2"; BINDIR="$PREFIX/bin"; APPDIR="$PREFIX/share/applications"; ICONDIR="$PREFIX/share/icons/hicolor/256x256/apps"; shift ;;
        --timeout) TIMEOUT="$2"; shift ;;
        --binary) BINARY="$2"; shift ;;
        --no-autostart) AUTOSTART_ON=0 ;;
        --no-idle) IDLE_ON=0 ;;
        --purge) PURGE=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
    esac
    shift
done

say() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

remove_if_present() { if [[ -e "$1" ]]; then rm -f "$1"; say "removed $1"; fi; }
refresh_desktop() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPDIR" >/dev/null 2>&1 || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
}

if [[ "$ACTION" == uninstall ]]; then
    pkill -TERM -x screenworm-idle 2>/dev/null || true
    pkill -TERM -x screenworm 2>/dev/null || true
    remove_if_present "$AUTOSTART/screenworm-idle.desktop"
    remove_if_present "$APPDIR/screenworm.desktop"
    remove_if_present "$ICONDIR/screenworm.png"
    remove_if_present "$BINDIR/screenworm"
    remove_if_present "$BINDIR/screenworm-idle"
    if [[ "$PURGE" == 1 ]]; then
        rm -rf "$CONFIGDIR" "$STATUSDIR"
        say "removed config and logs"
    fi
    refresh_desktop
    say "ScreenWorm removed. The KDE lock screen timeout was not changed."
    exit 0
fi

[[ "$TIMEOUT" =~ ^[0-9]+$ ]] || die "--timeout must be a number"
(( TIMEOUT >= 1 )) || die "--timeout must be at least 1"
[[ "$(uname -s)" == Linux ]] || die "this installer is for Linux"
if [[ -z "$BINARY" ]]; then
    command -v dotnet >/dev/null 2>&1 || die "dotnet SDK not found. Arch: sudo pacman -S dotnet-sdk"
    DOTNET_VERSION="$(dotnet --version 2>/dev/null || true)"
    [[ "$DOTNET_VERSION" =~ ^(1[0-9]|[2-9][0-9])\. ]] || die ".NET 10 SDK or newer is required (found: ${DOTNET_VERSION:-none})"
fi
if [[ "$IDLE_ON" == 1 ]]; then
    command -v cc >/dev/null 2>&1 || die "cc not found. Arch: sudo pacman -S base-devel"
    command -v pkg-config >/dev/null 2>&1 || die "pkg-config not found. Arch: sudo pacman -S pkgconf"
    command -v wayland-scanner >/dev/null 2>&1 || die "wayland-scanner not found. Arch: sudo pacman -S wayland"
    pkg-config --exists wayland-client || die "wayland-client headers not found. Arch: sudo pacman -S wayland"
fi

LDCONFIG="$(ldconfig -p 2>/dev/null || true)"
[[ "$LDCONFIG" == *"libglfw.so.3"* ]] || die "GLFW 3 not found. Arch: sudo pacman -S glfw"
[[ "$LDCONFIG" == *"libGL.so.1"* ]] || die "libGL not found. Arch: sudo pacman -S mesa"
[[ "$LDCONFIG" == *"libfreetype.so.6"* ]] || die "FreeType not found. Arch: sudo pacman -S freetype2"

BUILD="$ROOT/linux/build"
PUB="$BUILD/publish"
IDLEBUILD="$BUILD/idle"
rm -rf "$PUB" "$IDLEBUILD"
mkdir -p "$BUILD" "$PUB" "$IDLEBUILD" "$BINDIR" "$APPDIR" "$ICONDIR"

if [[ -n "$BINARY" ]]; then
    [[ -f "$BINARY" ]] || die "binary not found: $BINARY"
    cp "$BINARY" "$PUB/screenworm"
    chmod +x "$PUB/screenworm"
else
    say "Building ScreenWorm (self-contained single file)..."
    if ! dotnet publish "$ROOT/linux/ScreenWorm.csproj" -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true -o "$PUB" >/dev/null; then
        warn "self-contained publish failed; trying framework-dependent publish"
        dotnet publish "$ROOT/linux/ScreenWorm.csproj" -c Release -r linux-x64 --self-contained false \
            -p:PublishSingleFile=true -o "$PUB" >/dev/null || die "dotnet publish failed"
    fi
    [[ -x "$PUB/screenworm" ]] || die "publish did not produce screenworm"
fi

if [[ "$IDLE_ON" == 1 ]]; then
    say "Building the Wayland idle helper..."
    SYSTEMD_CFLAGS=""; SYSTEMD_LIBS=""; SYSTEMD_DEF=""
    if pkg-config --exists libsystemd; then
        SYSTEMD_CFLAGS="$(pkg-config --cflags libsystemd)"
        SYSTEMD_LIBS="$(pkg-config --libs libsystemd)"
        SYSTEMD_DEF="-DHAVE_SYSTEMD"
    else
        warn "libsystemd not found; ScreenWorm will keep running behind KDE's lock screen"
    fi
    wayland-scanner client-header "$ROOT/linux/idle/ext-idle-notify-v1.xml" "$IDLEBUILD/ext-idle-notify-v1-client-protocol.h"
    wayland-scanner private-code "$ROOT/linux/idle/ext-idle-notify-v1.xml" "$IDLEBUILD/ext-idle-notify-v1-protocol.c"
    cc -O2 -Wall -Wextra $SYSTEMD_DEF -I"$IDLEBUILD" $SYSTEMD_CFLAGS -o "$IDLEBUILD/screenworm-idle" \
        "$ROOT/linux/idle/idle.c" "$IDLEBUILD/ext-idle-notify-v1-protocol.c" \
        $(pkg-config --cflags --libs wayland-client) $SYSTEMD_LIBS
    if [[ -n "$SYSTEMD_LIBS" ]]; then say "KDE lock detection: enabled"; fi
fi

install -Dm755 "$PUB/screenworm" "$BINDIR/screenworm"
say "installed $BINDIR/screenworm"
if [[ "$IDLE_ON" == 1 ]]; then
    install -Dm755 "$IDLEBUILD/screenworm-idle" "$BINDIR/screenworm-idle"
    say "installed $BINDIR/screenworm-idle"
fi
install -Dm644 "$ROOT/linux/screenworm.png" "$ICONDIR/screenworm.png"

cat > "$APPDIR/screenworm.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ScreenWorm
Comment=Glowing worms fed by live system statistics
Exec="$BINDIR/screenworm" --run
TryExec=$BINDIR/screenworm
Icon=screenworm
Terminal=false
Categories=Graphics;
Keywords=screensaver;opengl;system;monitor;worms;
Actions=Window;

[Desktop Action Window]
Name=Open in a window
Exec="$BINDIR/screenworm" --window
EOF
say "installed $APPDIR/screenworm.desktop"

if [[ "$AUTOSTART_ON" == 1 && "$IDLE_ON" == 1 ]]; then
    mkdir -p "$AUTOSTART"
    cat > "$AUTOSTART/screenworm-idle.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ScreenWorm idle
Comment=Start ScreenWorm after $TIMEOUT seconds of inactivity
Exec="$BINDIR/screenworm-idle" --timeout $TIMEOUT -- "$BINDIR/screenworm" --run
OnlyShowIn=KDE;
X-GNOME-Autostart-enabled=true
X-KDE-autostart-after=panel
Terminal=false
NoDisplay=true
EOF
    say "installed $AUTOSTART/screenworm-idle.desktop"
    say "The idle helper starts with the next KDE session."
else
    if [[ -f "$AUTOSTART/screenworm-idle.desktop" ]]; then
        rm -f "$AUTOSTART/screenworm-idle.desktop"
        say "removed $AUTOSTART/screenworm-idle.desktop"
    fi
    if [[ "$IDLE_ON" == 1 ]]; then
        warn "autostart not installed; launch '$BINDIR/screenworm-idle --timeout $TIMEOUT -- $BINDIR/screenworm --run' yourself if needed"
    else
        warn "idle helper and autostart skipped; launch '$BINDIR/screenworm --run' manually"
    fi
fi

mkdir -p "$CONFIGDIR"
if [[ ! -f "$CONFIGDIR/config.txt" ]]; then
    cat > "$CONFIGDIR/config.txt" <<EOF
quality=auto
hud=1
crt=0
particles=262144
EOF
    say "wrote default config $CONFIGDIR/config.txt"
fi

if command -v kreadconfig6 >/dev/null 2>&1; then
    LOCK="$(kreadconfig6 --file kscreenlockerrc --group Daemon --key Timeout 2>/dev/null || true)"
    if [[ -z "$LOCK" ]]; then LOCK=300; fi
    if [[ "$LOCK" =~ ^[0-9]+$ ]] && (( LOCK > 0 && LOCK <= TIMEOUT )); then
        warn "KDE also locks the screen after ${LOCK}s. Set System Settings > Security & Privacy > Screen Locking > 'Lock screen automatically' to more than ${TIMEOUT}s so the worms are visible first."
    fi
fi

refresh_desktop
say ""
say "ScreenWorm installed."
say "  Test window:   $BINDIR/screenworm --window"
say "  Screenshot:    $BINDIR/screenworm --screenshot ~/screenworm.png --demo --seconds 10"
say "  Full screen:   $BINDIR/screenworm --run"
say ""
say "KDE Plasma 6 on Wayland has no external screensaver API. ScreenWorm is an"
say "idle overlay: it appears after $TIMEOUT seconds of input inactivity and exits on"
say "the first key, click or pointer movement. It also stops when KDE locks the"
say "screen. Keep KDE's lock timeout longer than $TIMEOUT seconds to see the worms first."
