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
- **Cam 1 interface**: `http://192.168.100.203:5000/`
- **Cam 2 interface**: `http://192.168.100.204:5000/`
- **Mobile view**: `/mobile` (on either cam)
- **Dock view**: `/dock` (on either cam)

Default password: `admin` (change in `appsettings.json`)

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
- `GET /api/stream/mjpeg` - MJPEG stream
- `GET /api/stream/snapshot` - Single JPEG snapshot

## Configuration

Edit `appsettings.json` before publishing:

```json
{
  "Password": "admin",
  "Logging": { ... }
}
```

## Static IP

The cameras are configured with static IPs `192.168.100.203` (Cam 1) and `192.168.100.204` (Cam 2). To set this up on a Pi:

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

## Troubleshooting

- **Camera not found**: Ensure camera is enabled in `raspi-config`
- **Servos not moving**: Check I2C is enabled and PCA9685 is wired correctly
- **Can't access from browser**: Check firewall, ensure service is running
- **Check logs**: `sudo journalctl -u pi-stream-cam -f`
