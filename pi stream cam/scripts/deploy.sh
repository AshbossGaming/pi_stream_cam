#!/bin/bash
set -euo pipefail

# Deploy script for Raspberry Pi.
# Run from the extracted publish directory.

APP_ROOT="/opt/pi-stream-cam"
RELEASES_DIR="$APP_ROOT/releases"
RELEASE_ID="$(date +%Y%m%d%H%M%S)"
RELEASE_DIR="$RELEASES_DIR/$RELEASE_ID"
SERVICE_FILE="pi-stream-cam.service"
SERVICE_PATH="/etc/systemd/system/pi-stream-cam.service"
RECORDINGS_DIR="/var/lib/pi-stream-cam/recordings"

echo "======================================="
echo "Pi Stream Cam Deployment"
echo "======================================="
echo "Release ID: $RELEASE_ID"

echo ""
echo "Installing dependencies..."

sudo apt-get update -qq

sudo apt-get install -y -qq \
    ffmpeg \
    wget \
    curl \
    tar

echo ""
echo "Installing MediaMTX RTSP server..."

if command -v mediamtx >/dev/null 2>&1; then

    echo "MediaMTX already installed"

else

    echo "Downloading MediaMTX..."

    MEDIA_URL="https://github.com/bluenviron/mediamtx/releases/latest/download/mediamtx_linux_arm64.tar.gz"

    wget -q -O /tmp/mediamtx.tar.gz "$MEDIA_URL"

    tar -xzf /tmp/mediamtx.tar.gz -C /tmp/

    sudo mv /tmp/mediamtx /usr/local/bin/mediamtx
    sudo chmod +x /usr/local/bin/mediamtx

    rm -f /tmp/mediamtx.tar.gz

    echo "MediaMTX installed to /usr/local/bin/mediamtx"
fi

echo ""
echo "Installing MediaMTX service..."

sudo mkdir -p /etc/mediamtx

sudo cp scripts/mediamtx.yml /etc/mediamtx.yml
sudo cp scripts/mediamtx.service /etc/systemd/system/mediamtx.service

sudo systemctl daemon-reload

sudo systemctl enable mediamtx
sudo systemctl restart mediamtx

sleep 2

echo ""
echo "Checking MediaMTX status..."

sudo systemctl is-active --quiet mediamtx || {
    echo "ERROR: MediaMTX failed to start"
    sudo journalctl -u mediamtx -n 50 --no-pager
    exit 1
}

echo "MediaMTX running"

echo ""
echo "Creating directory structure..."

sudo mkdir -p "$RELEASE_DIR"
sudo mkdir -p "$RECORDINGS_DIR"
sudo mkdir -p /var/log/pi-stream-cam

echo ""
echo "Copying release to:"
echo "$RELEASE_DIR"

tar -cf - . | sudo tar -xf - -C "$RELEASE_DIR"

sudo chmod +x "$RELEASE_DIR/pi-stream-cam"

echo ""
echo "Compiling system power helper..."

if [ -f "$RELEASE_DIR/scripts/pi-cam-power.c" ]; then

    gcc -Os -s -o /tmp/pi-cam-power "$RELEASE_DIR/scripts/pi-cam-power.c"

    sudo cp /tmp/pi-cam-power /usr/local/bin/pi-cam-power

    sudo chown root:root /usr/local/bin/pi-cam-power
    sudo chmod u+s /usr/local/bin/pi-cam-power

    rm -f /tmp/pi-cam-power

    echo "Installed setuid power helper"

else
    echo "WARNING: pi-cam-power.c not found"
fi

# Copy VERSION file if present
if [ -f VERSION ]; then
    sudo cp VERSION "$RELEASE_DIR/"
fi

echo ""
echo "Installing pi-stream-cam systemd service..."

sudo cp "$RELEASE_DIR/$SERVICE_FILE" "$SERVICE_PATH"

sudo systemctl daemon-reload
sudo systemctl enable pi-stream-cam

echo ""
echo "Stopping existing service..."

if systemctl is-active --quiet pi-stream-cam; then
    sudo systemctl stop pi-stream-cam
fi

echo ""
echo "Activating release..."

sudo ln -sfnT "$RELEASE_DIR" "$APP_ROOT/current"

echo ""
echo "Starting pi-stream-cam..."

sudo systemctl start pi-stream-cam

sleep 3

echo ""
echo "Checking service status..."

sudo systemctl is-active --quiet pi-stream-cam || {
    echo "ERROR: pi-stream-cam failed to start"
    sudo journalctl -u pi-stream-cam -n 100 --no-pager
    exit 1
}

echo "pi-stream-cam running"

echo ""
echo "Pruning old releases..."

find "$RELEASES_DIR" \
    -mindepth 1 \
    -maxdepth 1 \
    -type d | sort -r | tail -n +4 | xargs -r sudo rm -rf

echo ""
echo "======================================="
echo "Deployment Complete"
echo "======================================="
echo ""
echo "Service:"
echo "  pi-stream-cam"
echo ""
echo "Release:"
echo "  $RELEASE_DIR"
echo ""
echo "RTSP Stream:"
echo "  rtsp://$(hostname -I | awk '{print $1}'):8554/cam"
echo ""
echo "Recordings:"
echo "  $RECORDINGS_DIR"
echo ""
echo "Live logs:"
echo "  sudo journalctl -u pi-stream-cam -f"
echo ""
echo "MediaMTX logs:"
echo "  sudo journalctl -u mediamtx -f"
echo ""

echo "Verify RTSP:"
echo "  ffplay rtsp://$(hostname -I | awk '{print $1}'):8554/cam"
echo ""
echo "OBS URL:"
echo "  rtsp://$(hostname -I | awk '{print $1}'):8554/cam"
echo ""