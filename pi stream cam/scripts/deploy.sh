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

echo "Setting up Pi Stream Cam service..."
echo "Release ID: $RELEASE_ID"

echo "Installing GStreamer and libcamera dependencies..."
sudo apt-get update -qq
sudo apt-get install -y -qq \
    gstreamer1.0-tools \
    gstreamer1.0-libcamera \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-plugins-base \
    libcamera-dev \
    libcamera-v4l2

echo "Creating directory structure..."
sudo mkdir -p "$RELEASE_DIR"
sudo mkdir -p "$RECORDINGS_DIR"
sudo mkdir -p /var/log/pi-stream-cam

echo "Copying release to $RELEASE_DIR..."
tar -cf - . | sudo tar -xf - -C "$RELEASE_DIR"
sudo chmod +x "$RELEASE_DIR/pi-stream-cam"

# Copy VERSION file if present
if [ -f VERSION ]; then
    sudo cp VERSION "$RELEASE_DIR/"
fi

echo "Installing systemd service..."
sudo cp "$RELEASE_DIR/$SERVICE_FILE" "$SERVICE_PATH"
sudo systemctl daemon-reload
sudo systemctl enable pi-stream-cam

# Stop current version before switching
if systemctl is-active --quiet pi-stream-cam; then
    sudo systemctl stop pi-stream-cam
fi

echo "Activating new release..."
sudo ln -sfnT "$RELEASE_DIR" "$APP_ROOT/current"
sudo systemctl start pi-stream-cam

echo "Pruning old releases (keeping last 3)..."
find "$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d | sort -r | tail -n +4 | xargs -r sudo rm -rf

echo "Checking service status..."
sleep 2
sudo systemctl status pi-stream-cam --no-pager

echo ""
echo "=== Deployment complete ==="
echo "Service: pi-stream-cam"
echo "Release: $RELEASE_DIR"
echo "Recordings: $RECORDINGS_DIR"
echo "Logs: sudo journalctl -u pi-stream-cam -f"
echo ""
echo "Make sure to:"
echo "  1. Enable camera interface: sudo raspi-config (Interface Options > Camera)"
echo "  2. Enable I2C: sudo raspi-config (Interface Options > I2C)"
echo "  3. Reboot after enabling interfaces"
