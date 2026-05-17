#!/bin/bash
set -euo pipefail

# ============================================================
# Pi Stream Cam Installer
# ============================================================
# Usage:
#   curl -sL https://raw.githubusercontent.com/AshbossGaming/pi_stream_cam/main/install.sh | sudo bash
#   curl -sL https://raw.githubusercontent.com/AshbossGaming/pi_stream_cam/main/install.sh | sudo bash -s -- --version v1.0.0
#   curl -sL ... | sudo bash -s -- --uninstall
#   curl -sL ... | sudo bash -s -- --help
# ============================================================

REPO="AshbossGaming/pi_stream_cam"
APP_ROOT="/opt/pi-stream-cam"
SERVICE_NAME="pi-stream-cam"
MEDIAMTX_SERVICE="mediamtx"
INSTALL_DIR="/opt/pi-stream-cam/current"
STATE_DIR="/var/lib/pi-stream-cam"
LOG_DIR="/var/log/pi-stream-cam"
RECORDINGS_DIR="$STATE_DIR/recordings"
VERSION_FILE="$INSTALL_DIR/VERSION"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info()  { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn()  { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_step()  { echo ""; echo "==> $1"; }

usage() {
    cat <<EOF
Pi Stream Cam Installer — https://github.com/$REPO

Usage:
  curl -sL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo bash
  sudo ./install.sh [options]

Options:
  --version TAG   Install a specific version (default: latest)
  --uninstall     Remove Pi Stream Cam completely
  --help          Show this help

Examples:
  Install latest:    curl -sL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo bash
  Install v1.0.0:   curl -sL ... | sudo bash -s -- --version v1.0.0
  Uninstall:        curl -sL ... | sudo bash -s -- --uninstall
EOF
    exit 0
}

uninstall() {
    log_step "Uninstalling Pi Stream Cam"

    log_info "Stopping services..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    log_info "Removing service file..."
    rm -f "/etc/systemd/system/$SERVICE_NAME.service"
    systemctl daemon-reload

    log_info "Removing application files..."
    rm -rf "$APP_ROOT"

    log_info "Removing zmqsend and power helper..."
    rm -f /usr/local/bin/zmqsend
    rm -f /usr/local/bin/pi-cam-power
    rm -f /etc/pi-stream-cam-ffmpeg-path

    log_info "Note: mediamtx, ffmpeg, and system packages were NOT removed."
    log_info "To remove those: sudo apt-get remove libcamera-apps ffmpeg mediamtx"
    log_info "State files in $STATE_DIR were kept."
    log_info "To remove state: sudo rm -rf $STATE_DIR"

    echo ""
    log_info "Uninstall complete."
    exit 0
}

# --- Parse arguments ---
VERSION_TAG=""
DO_UNINSTALL=false

for arg in "$@"; do
    case "$arg" in
        --help) usage ;;
        --uninstall) DO_UNINSTALL=true ;;
        --version) shift; VERSION_TAG="${1:-}" ;;
    esac
done

if [ "$DO_UNINSTALL" = true ]; then
    uninstall
fi

# --- Preflight checks ---
if [ "$(id -u)" -ne 0 ]; then
    log_error "This script must be run as root (use sudo)."
    exit 1
fi

if [ ! -f /etc/os-release ]; then
    log_error "This script only supports Linux (Raspberry Pi OS)."
    exit 1
fi

ARCH=$(uname -m)
if [ "$ARCH" != "aarch64" ] && [ "$ARCH" != "armv7l" ]; then
    log_error "Unsupported architecture: $ARCH (expected aarch64 or armv7l)"
    exit 1
fi

# --- Detect latest version ---
log_step "Detecting Pi Stream Cam version"

if [ -z "$VERSION_TAG" ]; then
    log_info "Fetching latest release from GitHub..."
    VERSION_TAG=$(curl -sL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | head -1 | cut -d'"' -f4)

    if [ -z "$VERSION_TAG" ]; then
        log_error "Could not detect latest version from GitHub."
        log_error "Check: https://github.com/$REPO/releases"
        exit 1
    fi
fi

VERSION="${VERSION_TAG#v}"
ARTIFACT="pi-stream-cam-$VERSION-linux-arm64.tar.gz"
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$VERSION_TAG/$ARTIFACT"
CHECKSUM_URL="$DOWNLOAD_URL.sha256"

log_info "Version: $VERSION_TAG ($VERSION)"
log_info "Download: $DOWNLOAD_URL"

# --- Download release ---
log_step "Downloading release"

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

cd "$TMP_DIR"

log_info "Downloading $ARTIFACT..."
curl -sL -o "$ARTIFACT" "$DOWNLOAD_URL" || {
    log_error "Download failed. Check version tag or network."
    exit 1
}

log_info "Downloading checksum..."
curl -sL -o "$ARTIFACT.sha256" "$CHECKSUM_URL" || log_warn "No checksum file found, skipping verification."

if [ -f "$ARTIFACT.sha256" ]; then
    log_info "Verifying checksum..."
    sha256sum -c "$ARTIFACT.sha256" || {
        log_error "Checksum verification failed!"
        exit 1
    }
fi

# --- Extract ---
log_step "Extracting release"

EXTRACT_DIR="pi-stream-cam-$VERSION"
tar -xzf "$ARTIFACT"

if [ ! -d "$EXTRACT_DIR" ]; then
    log_error "Extraction failed: directory '$EXTRACT_DIR' not found"
    exit 1
fi

# --- Install system dependencies ---
log_step "Installing system dependencies"

apt-get update -qq
apt-get install -y -qq \
    libcamera-apps \
    build-essential \
    gcc \
    make \
    pkg-config \
    libzmq3-dev \
    libzmq5 \
    wget \
    curl \
    tar

# --- Check / install ffmpeg with ZMQ ---
log_step "Checking ffmpeg ZMQ support"

FFMPEG_PATH=""
FFMPEG_STATIC="/usr/local/bin/ffmpeg-static"

if command -v ffmpeg &>/dev/null && ffmpeg -filters 2>/dev/null | grep -q " zmq "; then
    log_info "System ffmpeg has ZMQ support"
    FFMPEG_PATH="$(command -v ffmpeg)"
elif [ -x "$FFMPEG_STATIC" ] && "$FFMPEG_STATIC" -filters 2>/dev/null | grep -q " zmq "; then
    log_info "Static ffmpeg has ZMQ support"
    FFMPEG_PATH="$FFMPEG_STATIC"
else
    log_info "Downloading static ffmpeg with ZMQ support..."
    apt-get install -y -qq xz-utils

    BtBN_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64.tar.xz"
    wget -q -O /tmp/ffmpeg-static.tar.xz "$BtBN_URL"
    tar -xf /tmp/ffmpeg-static.tar.xz -C /tmp/

    FFMPEG_EXTRACTED=$(find /tmp/ffmpeg-master-latest-linuxarm64 -name ffmpeg -type f 2>/dev/null | head -1)
    if [ -n "$FFMPEG_EXTRACTED" ]; then
        cp "$FFMPEG_EXTRACTED" "$FFMPEG_STATIC"
        chown root:root "$FFMPEG_STATIC"
        chmod 755 "$FFMPEG_STATIC"
        FFMPEG_PATH="$FFMPEG_STATIC"
        log_info "Static ffmpeg installed to $FFMPEG_STATIC"
    else
        log_warn "Could not find ffmpeg binary in downloaded tarball"
    fi

    rm -rf /tmp/ffmpeg-master-latest-linuxarm64 /tmp/ffmpeg-static.tar.xz
fi

# Write ffmpeg path
if [ -n "$FFMPEG_PATH" ]; then
    echo "$FFMPEG_PATH" > /etc/pi-stream-cam-ffmpeg-path
    log_info "ffmpeg path written to /etc/pi-stream-cam-ffmpeg-path"
fi

# --- Compile zmqsend helper ---
log_step "Compiling zmqsend helper"

if [ -f "$EXTRACT_DIR/scripts/zmqsend.c" ]; then
    gcc -Os -s -o /tmp/zmqsend "$EXTRACT_DIR/scripts/zmqsend.c" -lzmq
    cp /tmp/zmqsend /usr/local/bin/zmqsend
    chown root:root /usr/local/bin/zmqsend
    chmod 755 /usr/local/bin/zmqsend
    rm -f /tmp/zmqsend
    log_info "zmqsend installed at /usr/local/bin/zmqsend"
else
    log_warn "zmqsend.c not found in release"
fi

# --- Install MediaMTX ---
log_step "Installing MediaMTX RTSP server"

if command -v mediamtx &>/dev/null; then
    log_info "MediaMTX already installed"
else
    log_info "Downloading MediaMTX..."
    MEDIA_URL="https://github.com/bluenviron/mediamtx/releases/latest/download/mediamtx_linux_arm64.tar.gz"
    wget -q -O /tmp/mediamtx.tar.gz "$MEDIA_URL"
    tar -xzf /tmp/mediamtx.tar.gz -C /tmp/
    mv /tmp/mediamtx /usr/local/bin/mediamtx
    chmod +x /usr/local/bin/mediamtx
    rm -f /tmp/mediamtx.tar.gz
    log_info "MediaMTX installed to /usr/local/bin/mediamtx"
fi

# --- Configure MediaMTX ---
log_info "Configuring MediaMTX..."
cp "$EXTRACT_DIR/mediamtx.yml" /etc/mediamtx.yml
cp "$EXTRACT_DIR/mediamtx.service" /etc/systemd/system/mediamtx.service
systemctl daemon-reload
systemctl enable mediamtx
systemctl restart mediamtx
sleep 2
if systemctl is-active --quiet mediamtx; then
    log_info "MediaMTX is running"
else
    log_error "MediaMTX failed to start"
    journalctl -u mediamtx -n 20 --no-pager
    exit 1
fi

# --- Remove previous installation ---
log_step "Removing previous installation"

if [ -d "$APP_ROOT" ]; then
    log_info "Removing old installation at $APP_ROOT..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    rm -rf "$APP_ROOT/releases" "$APP_ROOT/current" 2>/dev/null || true
fi

# --- Create directories ---
log_step "Creating directories"

mkdir -p "$APP_ROOT/releases"
mkdir -p "$STATE_DIR"
mkdir -p "$RECORDINGS_DIR"
mkdir -p "$LOG_DIR"

RELEASE_ID="$(date +%Y%m%d%H%M%S)"
RELEASE_DIR="$APP_ROOT/releases/$RELEASE_ID"
mkdir -p "$RELEASE_DIR"

# --- Install release ---
log_step "Installing release $RELEASE_ID"

cp -r "$EXTRACT_DIR"/* "$RELEASE_DIR/"
chmod +x "$RELEASE_DIR/pi-stream-cam"

# --- Compile power helper ---
log_step "Compiling system power helper"

if [ -f "$RELEASE_DIR/scripts/pi-cam-power.c" ]; then
    gcc -Os -s -o /tmp/pi-cam-power "$RELEASE_DIR/scripts/pi-cam-power.c"
    cp /tmp/pi-cam-power /usr/local/bin/pi-cam-power
    chown root:root /usr/local/bin/pi-cam-power
    chmod u+s /usr/local/bin/pi-cam-power
    rm -f /tmp/pi-cam-power
    log_info "Setuid power helper installed"
else
    log_warn "pi-cam-power.c not found"
fi

# --- Install service ---
log_step "Installing systemd service"

cp "$RELEASE_DIR/pi-stream-cam.service" /etc/systemd/system/pi-stream-cam.service
systemctl daemon-reload
systemctl enable pi-stream-cam

log_info "Activating release..."
ln -sfnT "$RELEASE_DIR" "$APP_ROOT/current"

# --- Start service ---
log_step "Starting Pi Stream Cam"

systemctl start "$SERVICE_NAME"
sleep 3

if systemctl is-active --quiet "$SERVICE_NAME"; then
    log_info "Pi Stream Cam is running"
else
    log_error "Pi Stream Cam failed to start"
    journalctl -u "$SERVICE_NAME" -n 30 --no-pager
    exit 1
fi

# --- Prune old releases (keep last 3) ---
log_step "Pruning old releases"

find "$APP_ROOT/releases" \
    -mindepth 1 \
    -maxdepth 1 \
    -type d | sort -r | tail -n +4 | xargs -r rm -rf

# --- Done ---
HOSTNAME=$(hostname -I | awk '{print $1}')

echo ""
echo "======================================="
echo -e "${GREEN}Installation Complete!${NC}"
echo "======================================="
echo ""
echo "  Web Interface: http://$HOSTNAME:5000/"
echo "  Dock UI:       http://$HOSTNAME:5000/dock"
echo "  RTSP Stream:   rtsp://$HOSTNAME:8554/cam"
echo ""
echo "  Default password: admin"
echo "  Change it via:    sudo systemctl edit pi-stream-cam"
echo "  Add:              Environment=Password=your-password"
echo ""
echo "  View logs:  sudo journalctl -u pi-stream-cam -f"
echo "  Restart:    sudo systemctl restart pi-stream-cam"
echo "  Uninstall:  curl -sL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo bash -s -- --uninstall"
echo ""

# Cleanup happens via trap
