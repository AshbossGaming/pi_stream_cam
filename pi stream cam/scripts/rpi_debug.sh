#!/bin/bash
# Raspberry Pi debug helper - toggle kernel driver debug logging
# Usage: ./rpi_debug.sh on|off

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <on|off>"
  exit 1
fi

debug="0"
[[ "$1" != "on" ]] || debug=0xFFFFFF

set -x

for module in /sys/module/bcm2835_*; do
  echo $debug | sudo tee $module/parameters/debug
done
