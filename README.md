# Pi Stream Cam

A Raspberry Pi-based PTZ camera system with H.264 RTSP streaming and web control, featuring the Arducam 16MP IMX519 camera and MG90S servos controlled via PCA9685.

## Hardware Requirements

- Raspberry Pi (3B+ or newer recommended)
- Arducam 16MP IMX519 camera
- PCA9685 PWM driver board
- 2x MG90S servos (pan/tilt)
- Power supply for servos (5V)

### Wiring

```
PCA9685 -> Raspberry Pi:
VCC -> 3.3V (pin 1)
GND -> GND (pin 6)
SDA -> SDA (pin 3)
SCL -> SCL (pin 5)

Servos -> PCA9685:
Pan servo -> Channel 0
Tilt servo -> Channel 1
```

## Software Requirements

- Raspberry Pi OS (64-bit recommended)
- .NET 8 SDK (for building)
- libcamera (comes with Pi OS)
- ffmpeg (installed by deploy.sh)

## Quick Start

### 1. Build on Development Machine

```bash
cd "pi stream cam"
bash scripts/publish.sh
```

This creates a `publish` folder with the self-contained app for linux-arm64.

### 2. Deploy to Raspberry Pi

Copy the `publish` folder to your Pi, then run:

```bash
cd publish
sudo bash scripts/deploy.sh
```

### 3. Enable Pi Interfaces

On the Pi, run `sudo raspi-config` and enable:
- Interface Options > Camera (enable)
- Interface Options > I2C (enable)

Then reboot: `sudo reboot`

### 4. Access the Camera

After reboot, navigate to:
- **Web interface**: `http://<pi-ip>:5000/`
- **Dock UI**: `/dock` (PTZ/camera controls, requires login)
- **RTSP stream**: `rtsp://<pi-ip>:8554/cam` (add as Media Source in OBS)

Default password: `admin` (change via `Password` config or env var)

## API Endpoints

### PTZ Control
- `GET /api/ptz/status` - Get current position and camera settings
- `POST /api/ptz/pan/{angle}` - Set pan angle (0-180)
- `POST /api/ptz/tilt/{angle}` - Set tilt angle (0-180)
- `POST /api/ptz/move` - Move relative (body: `{"deltaPan": 10, "deltaTilt": -5}`)
- `POST /api/ptz/center` - Center PTZ

### Camera Settings
- `POST /api/ptz/zoom/{level}` - Set zoom (1-8)
- `POST /api/ptz/focus/{value}` - Set manual focus (0-100)
- `POST /api/ptz/autofocus/{mode}` - Set autofocus mode (continuous/single/manual)
- `POST /api/ptz/focus-range/{range}` - Set focus range (macro/normal)
- `POST /api/ptz/exposure/{value}` - Exposure compensation (-8 to 8)
- `POST /api/ptz/whitebalance/{value}` - White balance preset (0-8)
- `POST /api/ptz/sharpness/{value}` - Sharpness (0.0-16.0)
- `POST /api/ptz/brightness/{value}` - Brightness (-1, 0, 1)
- `POST /api/ptz/contrast/{value}` - Contrast (0, 1, 2)
- `POST /api/ptz/saturation/{value}` - Saturation (0, 1, 2)

### Stream
- `GET /api/stream/info` - RTSP stream URL and info

### System
- `POST /api/system/shutdown` - Shutdown the Pi (body: `{"password": "..."}`)
- `POST /api/system/reboot` - Reboot the Pi (body: `{"password": "..."}`)

### Bulk Settings
- `POST /api/option` - Set multiple options at once (body: `{"zoom": 2, "brightness": 0, ...}`)

## Configuration

Set via environment variables or `appsettings.json`:

```json
{
  "Password": "admin",
  "PORT": 5000
}
```

Default password: `admin`. For production, set via environment:
```
Environment=Password=your-secure-password
```

## Static IP

To configure a static IP on the Pi:

```bash
sudo bash scripts/setup-static-ip.sh
```

This configures the static IP on eth0 and reboots the Pi.

## Service Management

```bash
sudo systemctl status pi-stream-cam    # Check status
sudo systemctl stop pi-stream-cam       # Stop service
sudo systemctl start pi-stream-cam      # Start service
sudo journalctl -u pi-stream-cam -f    # View logs
```

## Undeploy

To remove the application and service from your Pi:

```bash
sudo systemctl stop pi-stream-cam
sudo systemctl disable pi-stream-cam
sudo rm -f /etc/systemd/system/pi-stream-cam.service
sudo rm -rf /opt/pi-stream-cam
sudo systemctl daemon-reload
```

## Adding Stream to OBS

The Pi serves H.264 video as MPEG-TS over UDP on port `5004`:

```
udp://<pi-ip>:5004
```

In OBS, use `udp://@:5004` as the Media Source URL.

### Add as Media Source
1. Open OBS Studio.
2. In the **Sources** dock, click **+** and select **Media Source**.
3. Name it (e.g., "Pi Cam").
4. Uncheck **Local File**.
5. In **Input**, paste `udp://@:5004`.
6. Set **Network Caching** to `100ms` (lower latency).
7. Set **Close file when inactive** to `No`.
8. Click **OK**.

### Streaming to YouTube from OBS
Once the source is in OBS:
1. Go to **Settings > Stream**.
2. Select **YouTube - RTMPS**.
3. Paste your YouTube stream key.
4. Click **Start Streaming**.

The hardware H.264 encoder on the Pi delivers the stream at 2 Mbps, 720p30, with keyframes every 30 frames.

## System Power Control

The dock UI includes **Shutdown** and **Reboot** buttons. These require re-entering the admin password for safety.

On the Pi, the service runs as a `DynamicUser` without sudo privileges, so a setuid helper binary (`/usr/local/bin/pi-cam-power`) is used to execute `systemctl poweroff` / `systemctl reboot` as root. The deploy script compiles and installs this helper automatically.

## Pipeline Architecture

```
rpicam-vid --codec h264 ... --output -
    |
    | (pipe stdout → stdin)
    v
ffmpeg -i pipe: -c copy -f rtsp -listen 1 rtsp://0.0.0.0:8554/cam
    |
    | (RTSP over TCP)
    v
OBS Media Source → YouTube RTMPS
```

The .NET app spawns and manages both processes, restarting the pipeline on camera setting changes (zoom, AF mode, flip).

## Deploy Architecture

- **publish.sh** builds a self-contained linux-arm64 .NET app
- **deploy.sh** installs dependencies (libcamera-apps, ffmpeg), copies the release to `/opt/pi-stream-cam/releases/<timestamp>/`, compiles the setuid power helper, installs/restarts the systemd service
- The last 3 releases are kept; older ones are pruned
- The systemd unit uses `DynamicUser`, cgroups (384M max), and `Nice=10` for minimal interference

## Troubleshooting

- **Camera not found**: Ensure camera is enabled in `raspi-config`
- **Servos not moving**: Check I2C is enabled and PCA9685 is wired correctly
- **Stream not connecting**: Check `ffmpeg` is installed (`which ffmpeg`), verify port 5004 is open, check service logs
- **Shutdown/reboot not working**: Check `/usr/local/bin/pi-cam-power` exists and has setuid (`ls -l /usr/local/bin/pi-cam-power` should show `-rwsr-xr-x root root`)
- **Check logs**: `sudo journalctl -u pi-stream-cam -f`
