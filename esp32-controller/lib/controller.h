#ifndef CONTROLLER_H
#define CONTROLLER_H

#include <Arduino.h>
#include <Ethernet.h>
#include <ArduinoHttpClient.h>
#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>

// Configuration
#define JOYSTICK_X A0
#define JOYSTICK_Y A1
#define ZOOM_DIAL A2
#define SEL_CAM_1 14
#define SEL_CAM_2 27
#define BTN_PRESET_1 32
#define BTN_PRESET_2 33
#define BTN_PRESET_3 34
#define BTN_PRESET_4 35
#define BTN_CENTER 21
#define LED_STATUS 2

// OLED Pins
#define OLED_SDA 22
#define OLED_SCL 23

// Pan/Tilt limits
#define PAN_MIN 0
#define PAN_MAX 180
#define TILT_MIN 0
#define TILT_MAX 90

// Number of cameras
#define MAX_CAMS 2

// Preset struct
struct Preset {
    uint8_t pan;
    uint8_t tilt;
    uint8_t zoom;
};

// Camera config
struct CamConfig {
    IPAddress ip;
    String name;
    uint8_t active;
};

class PTZController {
private:
    EthernetClient ethClient;
    HttpClient httpClient;
    
    CamConfig cameras[MAX_CAMS];
    uint8_t currentCam;
    
    Preset presets[4];
    uint8_t currentPreset;
    
    int16_t lastJoyX, lastJoyY;
    uint8_t currentPan, currentTilt, currentZoom;
    uint8_t targetPan, targetTilt;
    
    unsigned long lastMoveTime;
    unsigned long moveInterval;
    
    bool serverConnected;
    
    Adafruit_SSD1306* display;
    
public:
    PTZController();
    
    void begin();
    void update();
    
    // Camera management
    void setCamCount(uint8_t count);
    void setCamIP(uint8_t index, IPAddress ip, const char* name);
    uint8_t getCurrentCam() { return currentCam; }
    void selectCam(uint8_t index);
    void nextCam();
    
    // Presets
    void savePreset(uint8_t index);
    void recallPreset(uint8_t index);
    
    // PTZ control
    void setPan(uint8_t angle);
    void setTilt(uint8_t angle);
    void setZoom(uint8_t level);
    void moveDelta(int8_t dPan, int8_t dTilt);
    void center();
    
    // HTTP API
    void apiCall(const char* path, const char* method = "GET", const char* body = nullptr);
    void getStatus();
    bool isConnected() { return serverConnected; }
    
    // Display
    void updateDisplay();
};

#endif