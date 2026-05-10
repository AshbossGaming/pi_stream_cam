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

echo "Setting up Pi Stream Cam service..."

echo "Installing GStreamer and libcamera dependencies..."
sudo apt-get update -qq
sudo apt-get install -y -qq \
    gstreamer1.0-tools \
    gstreamer1.0-libcamera \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad \
    libcamera-dev \
    libcamera-v4l2

if ! id -u picam &>/dev/null; then
    sudo useradd -r -s /bin/false picam
fi

sudo usermod -aG gpio,i2c,video picam

sudo mkdir -p "$RELEASE_DIR"
sudo mkdir -p /var/log/pi-stream-cam
sudo mkdir -p /var/lib/pi-stream-cam
sudo chown -R picam:picam /var/lib/pi-stream-cam
sudo chown -R picam:picam /var/log/pi-stream-cam

echo "Copying release to $RELEASE_DIR..."
tar -cf - . | sudo tar -xf - -C "$RELEASE_DIR"
sudo chown -R picam:picam "$RELEASE_DIR"
sudo chmod +x "$RELEASE_DIR/pi-stream-cam"

sudo cp "$RELEASE_DIR/$SERVICE_FILE" "$SERVICE_PATH"
sudo systemctl daemon-reload
sudo systemctl enable pi-stream-cam

if systemctl is-active --quiet pi-stream-cam; then
    sudo systemctl stop pi-stream-cam
fi

sudo ln -sfnT "$RELEASE_DIR" "$APP_ROOT/current"
sudo systemctl start pi-stream-cam

echo "Pruning old releases..."
find "$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d | sort -r | tail -n +4 | xargs -r sudo rm -rf

echo ""
echo "Done! Service status:"
sudo systemctl status pi-stream-cam --no-pager
echo ""
echo "Make sure to:"
echo "  1. Enable camera interface: sudo raspi-config (Interface Options > Camera)"
echo "  2. Enable I2C: sudo raspi-config (Interface Options > I2C)"
echo "  3. Reboot after enabling interfaces"
echo ""
echo "Camera pipeline now uses: gst-launch-1.0 + libcamerasrc (was rpicam-vid)"
