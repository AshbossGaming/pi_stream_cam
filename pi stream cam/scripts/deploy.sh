#!/bin/bash
# Deploy script for Raspberry Pi
# Run this on the Pi after copying the published files

echo "Setting up Pi Stream Cam service..."

# Create service user if not exists
if ! id -u picam &>/dev/null; then
    sudo useradd -r -s /bin/false picam
fi

# Add user to required groups for GPIO/I2C access
sudo usermod -aG gpio,i2c,video picam

# Create directories
sudo mkdir -p /opt/pi-stream-cam
sudo mkdir -p /var/log/pi-stream-cam
sudo mkdir -p /var/lib/pi-stream-cam
sudo chown -R picam:picam /var/lib/pi-stream-cam

# Copy files (run from the directory containing published files)
sudo cp -r * /opt/pi-stream-cam/

# Set permissions
sudo chown -R picam:picam /opt/pi-stream-cam
sudo chown -R picam:picam /var/log/pi-stream-cam
sudo chmod +x /opt/pi-stream-cam/pi-stream-cam

# Install systemd service
sudo cp pi-stream-cam.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable pi-stream-cam
sudo systemctl start pi-stream-cam

echo ""
echo "Done! Service status:"
sudo systemctl status pi-stream-cam --no-pager
echo ""
echo "Make sure to:"
echo "  1. Enable camera interface: sudo raspi-config (Interface Options > Camera)"
echo "  2. Enable I2C: sudo raspi-config (Interface Options > I2C)"
echo "  3. Reboot after enabling interfaces"
