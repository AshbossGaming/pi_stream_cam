#!/bin/bash
# Setup static IP for Pi Camera Stream

echo "Configuring static IP: 192.168.100.203"

# Backup current config
sudo cp /etc/dhcpcd.conf /etc/dhcpcd.conf.bak

# Check if already configured
if grep -q "192.168.100.203" /etc/dhcpcd.conf; then
    echo "Static IP already configured"
    exit 0
fi

# Append static IP config
sudo tee -a /etc/dhcpcd.conf > /dev/null << 'EOF'

# Static IP for PTZ Camera
interface eth0
static ip_address=192.168.100.203/24
static routers=192.168.100.1
static domain_name_servers=192.168.100.1
EOF

echo "Static IP configured. Rebooting..."

# Reboot to apply
sudo reboot
