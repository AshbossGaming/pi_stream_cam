# Pi Stream Cam

A Raspberry Pi-based PTZ camera system with web control, featuring the Arducam 16MP IMX519 camera and MG90S servos controlled via PCA9685.

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
- **Control interface**: `http://<pi-ip>:5000/`
- **Stream endpoint**: `http://<pi-ip>:5001/api/stream/mjpeg`
- **Dock view**: `/dock` (full control UI, requires login)
- **Mobile view**: `/mobile` (redirects to dock)

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
- `GET /api/stream/mjpeg` - MJPEG stream (hardware-accelerated via rpicam-vid)

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
  "CONTROL_PORT": 5000,
  "STREAM_PORT": 5001
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

### 1. Identify your Stream URL
The MJPEG stream is served on the stream port (default `5001`):
```
http://<pi-ip>:5001/api/stream/mjpeg
```

*Note: Replace with your Raspberry Pi's actual IP.*

### 2. Add as a Browser Source (Recommended)
1. Open OBS Studio.
2. In the **Sources** dock, click **+** and select **Browser**.
3. Name it (e.g., "Pi Cam 1").
4. In **URL**, paste your stream URL.
5. Set **Width** and **Height** (e.g., `1280` x `720`).
6. Click **OK**.

### 3. Alternative: Media Source
1. Click **+** in **Sources** and select **Media Source**.
2. Uncheck **Local File**.
3. In **Input**, paste your stream URL.
4. Set **Input Format** to `mjpeg`.
5. Click **OK**.

## System Power Control

The dock UI includes **Shutdown** and **Reboot** buttons. These require re-entering the admin password for safety.

On the Pi, the service runs as a `DynamicUser` without sudo privileges, so a setuid helper binary (`/usr/local/bin/pi-cam-power`) is used to execute `systemctl poweroff` / `systemctl reboot` as root. The deploy script compiles and installs this helper automatically.

## Deploy Architecture

- **publish.sh** builds a self-contained linux-arm64 .NET app
- **deploy.sh** installs dependencies, copies the release to `/opt/pi-stream-cam/releases/<timestamp>/`, compiles the setuid power helper, installs/restarts the systemd service
- The last 3 releases are kept; older ones are pruned
- The systemd unit uses `DynamicUser`, cgroups (384M max), and `Nice=10` for minimal interference

## Troubleshooting

- **Camera not found**: Ensure camera is enabled in `raspi-config`
- **Servos not moving**: Check I2C is enabled and PCA9685 is wired correctly
- **Can't access from browser**: Check firewall, ensure service is running
- **Shutdown/reboot not working**: Check `/usr/local/bin/pi-cam-power` exists and has setuid (`ls -l /usr/local/bin/pi-cam-power` should show `-rwsr-xr-x root root`)
- **Check logs**: `sudo journalctl -u pi-stream-cam -f`
