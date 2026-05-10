#!/bin/bash
# Dump all available camera devices and their V4L2 controls
# Useful for discovering camera options to pass to the service

echo "=== V4L2 Devices ==="
v4l2-ctl --list-devices 2>/dev/null || echo "v4l2-ctl not available"

echo ""
echo "=== Camera Options ==="
for dev in /dev/video*; do
  echo "--- $dev ---"
  v4l2-ctl -d "$dev" --list-ctrls 2>/dev/null || true
  echo ""
done

echo ""
echo "=== libcamera Cameras ==="
if command -v libcamera-still &> /dev/null; then
  libcamera-still --list-cameras 2>/dev/null || true
elif command -v rpicam-still &> /dev/null; then
  rpicam-still --list-cameras 2>/dev/null || true
else
  echo "Neither libcamera-still nor rpicam-still available"
fi

echo ""
echo "=== I2C Devices ==="
sudo i2cdetect -y 1 2>/dev/null || echo "i2cdetect not available"
