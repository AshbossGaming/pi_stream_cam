#!/bin/bash
# Dump all available V4L2 camera devices and their controls
# Useful for discovering USB webcam device paths and capabilities

echo "=== V4L2 Devices ==="
v4l2-ctl --list-devices 2>/dev/null || echo "v4l2-ctl not available"

echo ""
echo "=== Camera Controls ==="
for dev in /dev/video*; do
  echo "--- $dev ---"
  v4l2-ctl -d "$dev" --list-ctrls 2>/dev/null || true
  echo ""
done

echo ""
echo "=== Supported Formats ==="
for dev in /dev/video*; do
  echo "--- $dev ---"
  v4l2-ctl -d "$dev" --list-formats-ext 2>/dev/null || true
  echo ""
done
