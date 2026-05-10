#!/bin/bash
# Measure camera-streamer performance metrics
# Usage: ./rpi_measure.sh [duration_seconds]

DURATION=${1:-10}
INTERVAL=2

echo "=== Pi Stream Cam Performance Measurement ==="
echo "Duration: ${DURATION}s, Interval: ${INTERVAL}s"
echo ""

echo "--- CPU / Memory ---"
for i in $(seq 1 $((DURATION / INTERVAL))); do
  ps aux | grep -E "(pi-stream-cam|gst-launch)" | grep -v grep
  echo ""
  sleep $INTERVAL
done

echo ""
echo "--- Temperature ---"
for i in $(seq 1 $((DURATION / INTERVAL))); do
  echo "CPU temp: $(vcgencmd measure_temp | grep -oP '\d+\.\d+')°C"
  echo "Throttled: $(vcgencmd get_throttled)"
  sleep $INTERVAL
done

echo ""
echo "--- Memory ---"
free -h

echo ""
echo "--- Camera Status ---"
curl -s http://localhost:5000/api/status 2>/dev/null | python3 -m json.tool 2>/dev/null || echo "Service not reachable"

echo ""
echo "--- I2C Detect ---"
sudo i2cdetect -y 1 2>/dev/null || echo "i2cdetect not available"
