#!/bin/bash
# Undeploy script for Raspberry Pi
# This will stop the service and remove the installed files

echo "Undeploying Pi Stream Cam..."

# Stop and disable service
echo "Stopping and disabling service..."
sudo systemctl stop pi-stream-cam 2>/dev/null
sudo systemctl disable pi-stream-cam 2>/dev/null

# Remove systemd service file
echo "Removing service file..."
sudo rm -f /etc/systemd/system/pi-stream-cam.service
sudo systemctl daemon-reload

# Remove application files
echo "Removing application files..."
sudo rm -rf /opt/pi-stream-cam
sudo rm -rf /var/log/pi-stream-cam
sudo rm -rf /var/lib/pi-stream-cam

# Optionally remove the user
# Note: We don't remove the user by default as it might own other files, 
# but you can do it manually with: sudo userdel picam

echo ""
echo "Undeploy complete!"
