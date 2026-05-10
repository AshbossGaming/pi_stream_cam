# Pi Stream Cam

A Raspberry Pi-based PTZ (Pan-Tilt-Zoom) surveillance camera system with web-based control, an ESP32 physical controller, and an Android companion app.

## Features

- **Live MJPEG streaming** — multipart/x-mixed-replace stream viewable in any browser
- **PTZ control** — pan (0–180°) and tilt (0–180°) via MG90S servos driven by a PCA9685 PWM controller over I2C
- **4 PTZ presets** — save and recall camera positions
- **Camera tuning** — zoom (1–8x digital), focus, autofocus modes, exposure compensation, white balance, sharpness, brightness, contrast, saturation, JPEG quality, video flip
 - **Video recording** — H.264 hardware-encoded via rpicam-vid
- **High-resolution snapshots** — up to 1920×1080 JPEG via rpicam-jpeg
- **Secure access** — cookie-based authentication for the web UI, API key for the mobile app
- **State persistence** — camera and PTZ settings saved to JSON and restored on restart
- **Dual-camera support** — Cam 1 (192.168.100.203) and Cam 2 (192.168.100.204)
- **ESP32 physical controller** — hardware panel with joystick, zoom dial, OLED display, and preset buttons
- **Android app** — simple WebView wrapper around the mobile web UI
- **Systemd service** — auto-start with resource limits (384 MB RAM, CPU weight 20)

## Quick Start

### Prerequisites

- .NET 8 SDK
- Raspberry Pi with Arducam 16MP IMX519, PCA9685, and MG90S servos wired up
- I2C and camera interfaces enabled (`sudo raspi-config`)

### Build & Run (Development)

```
dotnet restore
dotnet run --project "pi stream cam/pi stream cam.csproj"
```

The control API and web UI are served on port 5000 (configurable via `CONTROL_PORT` and `STREAM_PORT` environment variables).

### Publish for Raspberry Pi

```
dotnet publish "pi stream cam.csproj" -c Release -r linux-arm64 -f net8.0 --self-contained true -o ./publish
```

Copy the `publish/` directory to your Pi and run:

```
cd publish
sudo bash scripts/deploy.sh
```

The deploy script:
1. Installs `libcamera-apps` (provides rpicam-vid and rpicam-jpeg for camera capture)
2. Creates the directory structure (`/opt/pi-stream-cam/releases/`, `/var/lib/pi-stream-cam/recordings/`, `/var/log/pi-stream-cam`)
3. Copies the release to a timestamped directory (e.g. `/opt/pi-stream-cam/releases/20250101120000/`)
4. Installs the systemd service unit and enables it
5. Stops any running instance, activates the new release via symlink (`/opt/pi-stream-cam/current`), and starts the service
6. Prunes old releases, keeping the last 3

### Access

Open `http://<pi-ip>:5000/` in a browser. Default password: `admin`.

## Project Structure

```
pi stream cam/
├── pi stream cam.csproj     — .NET 8 web application
├── Program.cs               — Startup: Kestrel, auth, routing, services
├── Controllers/
│   ├── PtzController.cs     — PTZ control endpoints
│   └── StreamController.cs  — MJPEG stream, snapshot, recording endpoints
├── Services/
│   ├── CameraService.cs     — rpicam-vid capture loop, autofocus, and recording
│   ├── ServoService.cs      — PCA9685 servo control and presets
│   └── VersionInfo.cs       — Assembly version
├── Models/
│   └── PtzModels.cs         — PtzPreset, PtzState DTOs
├── Middleware/
│   └── ApiKeyAuthMiddleware.cs
├── wwwroot/
│   └── dock.html            — Web UI (responsive, dark-themed)
├── scripts/                 — Build, deploy, and service scripts
├── debian/                  — Debian packaging files
├── esp32-controller/        — ESP32 firmware (PlatformIO)
├── PiCamAndroid/            — Android WebView wrapper
└── build.gradle.kts         — Gradle orchestrator
```

## Service Management

```bash
sudo systemctl status pi-stream-cam
sudo systemctl stop/start/restart pi-stream-cam
sudo journalctl -u pi-stream-cam -f
```

## Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `CONTROL_PORT`      | 5000    | Port for control API and web UI |
| `STREAM_PORT`       | 5001    | Port for MJPEG stream |
| `ASPNETCORE_ENVIRONMENT` | Production | ASP.NET environment |

The login password is configured in `appsettings.json` (`Password` field, default: `admin`). The mobile API key is hardcoded as `pi-stream-cam-mobile-v1`.

## Hardware Wiring

| Component | Connection |
|-----------|-----------|
| PCA9685 VCC | Raspberry Pi 3.3V |
| PCA9685 GND | Raspberry Pi GND |
| PCA9685 SDA | Raspberry Pi pin 3 (GPIO 2) |
| PCA9685 SCL | Raspberry Pi pin 5 (GPIO 3) |
| Pan servo | PCA9685 channel 0 |
| Tilt servo | PCA9685 channel 1 |

## License

See [LICENSE](LICENSE) (if available).
