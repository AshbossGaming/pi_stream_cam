#!/bin/bash
# Publish script - run on development machine
# Publishes the app for Raspberry Pi (linux-arm64)

echo "Publishing for Raspberry Pi (linux-arm64)..."

# Self-contained: includes .NET runtime (no runtime install needed on Pi)
# .NET 8 runtime is supported on Pi OS
SELF_CONTAINED=true

dotnet publish "pi stream cam.csproj" -c Release -r linux-arm64 -f net8.0 --self-contained $SELF_CONTAINED -o ./publish

if [ "$SELF_CONTAINED" = false ]; then
    echo ""
    echo "Published with framework-dependent mode."
    echo "Make sure .NET 8 runtime is installed on the Pi"
else
    echo ""
    echo "Published as self-contained (includes .NET runtime)"
fi

echo ""
echo "Note: Requires libcamera-apps on the Pi (installed by deploy.sh)"
echo ""
echo "Done! Copy the 'publish' folder to your Raspberry Pi."
echo "Then on the Pi, run: sudo bash scripts/deploy.sh"
