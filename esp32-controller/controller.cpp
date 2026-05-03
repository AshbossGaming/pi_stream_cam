/*
 * ESP32 PTZ Camera Controller
 * Hardware: ESP32 + W5500 Ethernet + Analog joystick + Zoom dial + OLED
 * 
 * Connections:
 * - Joystick X: A0 (GP36)
 * - Joystick Y: A1 (GP39)  
 * - Zoom dial: A2 (GP34)
 * - Cam select buttons: GPIO14, GPIO27
 * - Preset buttons: GPIO32, GPIO33, GPIO34, GPIO35
 * - Center button: GPIO21
 * - Status LED: GPIO2
 * - OLED: SDA=GPIO22, SCL=GPIO23
 * - Ethernet: SPI (MOSI=23, MISO=19, SCLK=18, CS=5, RST=4)
 */

#include <Arduino.h>
#include <Ethernet.h>
#include <ArduinoHttpClient.h>
#include <WiFi.h>  // Use WiFi.h for ESP32
#include <ervo.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>

// ============== CONFIGURATION ==============
// Camera IPs (adjust for your network)
IPAddress cam1IP(192, 168, 100, 203);
IPAddress cam2IP(192, 168, 100, 204);
uint16_t camPort = 5000;

// Ethernet MAC (unique per device)
byte mac[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0xFE, 0xED };

// Joystick settings
const int JOYSTICK_X = A0;
const int JOYSTICK_Y = A1;
const int ZOOM_DIAL = A2;
const int JOYSTICK_CENTER = 2048;  // Center point (12-bit ADC = 4096)
const int JOYSTICK_DEADZONE = 200;

// Pin definitions
const int SEL_CAM_1 = 14;
const int SEL_CAM_2 = 27;
const int BTN_PRESET_1 = 32;
const int BTN_PRESET_2 = 33;
const int BTN_PRESET_3 = 34;
const int BTN_PRESET_4 = 35;
const int BTN_CENTER = 21;
const int LED_STATUS = 2;

// OLED settings
#define OLED_SCREEN_WIDTH 128
#define OLED_SCREEN_HEIGHT 64
#define OLED_SDA 22
#define OLED_SCL 23
#define OLED_RESET -1

// ============== GLOBAL STATE ==============
EthernetClient ethClient;
HttpClient httpClient(ethClient, cam1IP, camPort);

uint8_t currentCam = 0;
uint8_t currentPan = 90;
uint8_t currentTilt = 45;
uint8_t currentZoom = 1;

// Presets [4 positions][pan, tilt, zoom]
uint8_t presets[4][3] = {
    { 90, 45, 1 },   // Preset 1: center
    { 45, 30, 1 },  // Preset 2: left
    { 135, 30, 1 }, // Preset 3: right
    { 90, 60, 2 }  // Preset 4: up close
};

bool serverConnected = false;
unsigned long lastMoveTime = 0;
const unsigned long MOVE_INTERVAL = 50;  // ms between PTZ updates

Adafruit_SSD1306 display(OLED_SCREEN_WIDTH, OLED_SCREEN_HEIGHT, &Wire, OLED_RESET);

// ============== SETUP ==============
void setup() {
    Serial.begin(115200);
    Serial.println("\n=== PTZ Controller Starting ===");
    
    // Configure pins
    pinMode(SEL_CAM_1, INPUT_PULLUP);
    pinMode(SEL_CAM_2, INPUT_PULLUP);
    pinMode(BTN_PRESET_1, INPUT_PULLUP);
    pinMode(BTN_PRESET_2, INPUT_PULLUP);
    pinMode(BTN_PRESET_3, INPUT_PULLUP);
    pinMode(BTN_PRESET_4, INPUT_PULLUP);
    pinMode(BTN_CENTER, INPUT_PULLUP);
    pinMode(LED_STATUS, OUTPUT);
    
    // Initialize Ethernet
    Serial.println("Initializing Ethernet...");
    if (Ethernet.begin(mac) == 0) {
        Serial.println("DHCP failed, trying link...");
        Ethernet.begin(mac, Ethernet.localIP());
    }
    
    Serial.print("IP: ");
    Serial.println(Ethernet.localIP());
    
    // Initialize OLED
    Serial.println("Initializing OLED...");
    Wire.begin(OLED_SDA, OLED_SCL);
    if (!display.begin(SSD1306_SWITCHCAPVCC, 0x3C)) {
        Serial.println("OLED init failed");
    } else {
        display.clearDisplay();
        display.setTextSize(1);
        display.setTextColor(SSD1306_WHITE);
        display.setCursor(0, 0);
        display.println("PTZ Controller");
        display.println(Ethernet.localIP());
        display.display();
    }
    
    delay(1000);
    digitalWrite(LED_STATUS, HIGH);  // LED on = ready
    Serial.println("=== Ready ===");
}

// ============== MAIN LOOP ==============
void loop() {
    handleJoystick();
    handleButtons();
    updateStatusLED();
    
    // Get status every 2 seconds
    static unsigned long lastStatus = 0;
    if (millis() - lastStatus > 2000) {
        getPTZStatus();
        lastStatus = millis();
    }
    
    delay(10);
}

// ============== JOYSTICK CONTROL ==============
void handleJoystick() {
    int joyX = analogRead(JOYSTICK_X) - JOYSTICK_CENTER;
    int joyY = analogRead(JOYSTICK_Y) - JOYSTICK_CENTER;
    int zoomADC = analogRead(ZOOM_DIAL);
    uint8_t zoom = map(zoomADC, 0, 4096, 1, 8);
    zoom = constrain(zoom, 1, 8);
    
    // Check for movement
    if (abs(joyX) > JOYSTICK_DEADZONE || abs(joyY) > JOYSTICK_DEADZONE || zoom != currentZoom) {
        if (millis() - lastMoveTime > MOVE_INTERVAL) {
            int8_t dPan = 0, dTilt = 0;
            
            if (abs(joyX) > JOYSTICK_DEADZONE) {
                dPan = joyX > 0 ? 2 : -2;
            }
            if (abs(joyY) > JOYSTICK_DEADZONE) {
                dTilt = joyY > 0 ? 2 : -2;
            }
            
            if (dPan != 0 || dTilt != 0) {
                currentPan = constrain(currentPan + dPan, 0, 180);
                currentTilt = constrain(currentTilt + dTilt, 0, 90);
                sendPTZ(currentPan, currentTilt);
            }
            
            if (zoom != currentZoom) {
                currentZoom = zoom;
                sendZoom(currentZoom);
            }
            
            lastMoveTime = millis();
        }
    }
}

// ============== BUTTONS ==============
void handleButtons() {
    static bool lastBtn[8] = { false };
    bool btn[8] = {
        digitalRead(SEL_CAM_1) == LOW,
        digitalRead(SEL_CAM_2) == LOW,
        digitalRead(BTN_PRESET_1) == LOW,
        digitalRead(BTN_PRESET_2) == LOW,
        digitalRead(BTN_PRESET_3) == LOW,
        digitalRead(BTN_PRESET_4) == LOW,
        digitalRead(BTN_CENTER) == LOW,
        false
    };
    
    // Camera selection
    if (btn[0] && !lastBtn[0]) selectCam(0);
    if (btn[1] && !lastBtn[1]) selectCam(1);
    
    // Presets
    if (btn[2] && !lastBtn[2]) recallPreset(0);
    if (btn[3] && !lastBtn[3]) recallPreset(1);
    if (btn[4] && !lastBtn[4]) recallPreset(2);
    if (btn[5] && !lastBtn[5]) recallPreset(3);
    
    // Center
    if (btn[6] && !lastBtn[6]) center();
    
    // Save preset (hold + preset button)
    if (btn[2] && btn[6] && !lastBtn[2]) savePreset(0);
    if (btn[3] && btn[6] && !lastBtn[3]) savePreset(1);
    if (btn[4] && btn[6] && !lastBtn[4]) savePreset(2);
    if (btn[5] && btn[6] && !lastBtn[5]) savePreset(3);
    
    // Debounce
    for (int i = 0; i < 7; i++) lastBtn[i] = btn[i];
}

// ============== CAMERA SELECTION ==============
void selectCam(uint8_t index) {
    currentCam = index;
    if (index == 0) {
        httpClient.changeHost(cam1IP, camPort);
        Serial.print("Cam 1: ");
        Serial.println(cam1IP);
    } else {
        httpClient.changeHost(cam2IP, camPort);
        Serial.print("Cam 2: ");
        Serial.println(cam2IP);
    }
    getPTZStatus();
    updateDisplay();
}

// ============== PTZ API ==============
void sendPTZ(uint8_t pan, uint8_t tilt) {
    char path[32];
    sprintf(path, "/api/ptz/pan/%d", pan);
    httpClient.get(path);
    int status = httpClient.responseStatusCode();
    httpClient.stop();
    
    sprintf(path, "/api/ptz/tilt/%d", tilt);
    httpClient.get(path);
    status = httpClient.responseStatusCode();
    httpClient.stop();
    
    Serial.print("PTZ: ");
    Serial.print(pan);
    Serial.print(", ");
    Serial.println(tilt);
}

void sendZoom(uint8_t level) {
    char path[32];
    sprintf(path, "/api/ptz/zoom/%d", level);
    httpClient.get(path);
    httpClient.responseStatusCode();
    httpClient.stop();
    
    Serial.print("Zoom: ");
    Serial.println(level);
}

void getPTZStatus() {
    httpClient.get("/api/ptz/status");
    int status = httpClient.responseStatusCode();
    
    if (status == 200) {
        serverConnected = true;
        while (httpClient.available()) {
            String line = httpClient.readStringUntil('\n');
            if (line.indexOf("pan") > 0) {
                int p = line.substring(line.indexOf("pan") + 5).toInt();
                int t = line.substring(line.indexOf("tilt") + 6).toInt();
                int z = line.indexOf("zoom") > 0 ? line.substring(line.indexOf("zoom") + 6).toInt() : 1;
                currentPan = p;
                currentTilt = t;
                currentZoom = z > 0 ? z : 1;
            }
        }
    } else {
        serverConnected = false;
    }
    httpClient.stop();
    
    updateDisplay();

    // Try alternate camera on failure
    if (!serverConnected && currentCam == 0) {
        httpClient.changeHost(cam2IP, camPort);
        getPTZStatus();
        if (serverConnected) {
            currentCam = 1;
            Serial.print("Auto-switch to Cam 2: ");
            Serial.println(cam2IP);
        } else {
            httpClient.changeHost(cam1IP, camPort);
        }
    }
}

void center() {
    currentPan = 90;
    currentTilt = 45;
    sendPTZ(90, 45);
}

void recallPreset(uint8_t index) {
    currentPan = presets[index][0];
    currentTilt = presets[index][1];
    currentZoom = presets[index][2];
    sendPTZ(currentPan, currentTilt);
    delay(50);
    sendZoom(currentZoom);
    Serial.print("Preset ");
    Serial.println(index + 1);
}

void savePreset(uint8_t index) {
    presets[index][0] = currentPan;
    presets[index][1] = currentTilt;
    presets[index][2] = currentZoom;
    Serial.print("Saved Preset ");
    Serial.println(index + 1);
}

// ============== DISPLAY ==============
void updateDisplay() {
    display.clearDisplay();
    display.setCursor(0, 0);
    display.print("Cam: ");
    display.println(currentCam + 1);
    display.print("Pan: ");
    display.print(currentPan);
    display.print("  Tilt: ");
    display.println(currentTilt);
    display.print("Zoom: ");
    display.print(currentZoom);
    display.println("x");
    display.print(serverConnected ? "ONLINE" : "OFFLINE");
    display.display();
}

// ============== STATUS LED ==============
void updateStatusLED() {
    static unsigned long lastBlink = 0;
    static bool ledState = false;
    
    if (serverConnected) {
        digitalWrite(LED_STATUS, HIGH);
    } else {
        if (millis() - lastBlink > 500) {
            ledState = !ledState;
            digitalWrite(LED_STATUS, ledState);
            lastBlink = millis();
        }
    }
}