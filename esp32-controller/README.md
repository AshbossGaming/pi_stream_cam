# ESP32 PTZ Camera Controller

## Hardware Required
- ESP32 (WROOM or WROOM-2)
- W5500 Ethernet module (SPI)
- Analog joystick (2-axis)
- Potentiometer 10K (zoom dial)
- SSD1306 OLED 128x64 (I2C)
- Push buttons (6x)
- LED (status)
- Resistors 10K (pull-down for buttons)

## Wiring

### Ethernet (W5500)
| W5500 | ESP32 |
|-------|-------|
| MISO  | GPIO19 |
| MOSI  | GPIO23 |
| SCK   | GPIO18 |
| CS    | GPIO5  |
| RST   | GPIO4  |
| INT   | Not connected |

### AnalogJoystick
| Joystick | ESP32 |
|----------|-------|
| X axis   | GPIO36 (A0) |
| Y axis   | GPIO39 (A1) |
| VCC     | 3.3V |
| GND     | GND |

### Zoom Dial
| Potentiometer | ESP32 |
|--------------|-------|
| Wiper        | GPIO34 (A2) |
| End 1        | GND |
| End 2        | 3.3V |

### Buttons (all with 10K pull-down to GND)
| Button | ESP32 |
|-------|-------|
| Cam 1 | GPIO14 |
| Cam 2 | GPIO27 |
| Preset 1 | GPIO32 |
| Preset 2 | GPIO33 |
| Preset 3 | GPIO34 |
| Preset 4 | GPIO35 |
| Center | GPIO21 |

### OLED (I2C)
| OLED | ESP32 |
|------|------|
| SDA  | GPIO22 |
| SCL  | GPIO23 |
| VCC  | 3.3V |
| GND  | GND |

### Status LED
| LED | ESP32 |
|-----|-------|
| + (anode) | GPIO2 |
| - (cathode) | GND (via 220Ω) |

## Build (PlatformIO)

```bash
cd esp32-controller
pio run
```

Or edit platformio.ini to select your board, then:

```bash
pio run -e lolin32
pio upload -e lolin32
```

## How to Use

### Controls
- **Joystick**: Move camera pans left/right, tilts up/down
- **Zoom dial**: Turn to zoom in/out (1x-8x)
- **Cam 1 button**: Select camera 1 (192.168.100.203)
- **Cam 2 button**: Select camera 2 (192.168.100.204)
- **Preset 1-4**: Recall saved positions
- **Center + Preset**: Save current position to preset

### Display
Shows: Current camera, Pan angle, Tilt angle, Zoom level, Connection status

## Config

Edit these lines in controller.cpp for your network:

```cpp
//_camera IPs
IPAddress cam1IP(192, 168, 100, 203);
IPAddress cam2IP(192, 168, 100, 204);
uint16_t camPort = 5000;

// Your MAC address (unique per device)
byte mac[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0xFE, 0xED };
```

## Troubleshooting

1. No Ethernet: Check CS pin (GPIO5) and wiring
2. No OLED: Check I2C pins (22, 23) and address (0x3C)
3. Camera offline: LED blinks - auto-switches to backup camera
4. Joystick drift: Adjust JOYSTICK_DEADZONE