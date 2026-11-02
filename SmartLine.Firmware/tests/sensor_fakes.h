#pragma once

#include <array>
#include <cstdint>
#include <sstream>
#include <vector>

constexpr int LOW = 0, HIGH = 1, INPUT = 0, OUTPUT = 1;
constexpr int COLOR_LED_PIN = 26, BUZZER_PIN = 25, FAN_PIN = 27;
constexpr int FLAME_PIN = 34, SERVO_PIN = 13, ALEV_SINIRI = 500;
constexpr float SICAKLIK_SINIRI = 32;
constexpr uint8_t TCS34725_ADDRESS = 0x29, TCS34725_COMMAND_BIT = 0x80;
constexpr uint8_t TCS34725_CDATAL = 0x14;
constexpr int TCS34725_INTEGRATIONTIME_50MS = 0xEB, TCS34725_GAIN_4X = 1;

inline std::array<int, 40> outputs{};
inline int flameValue = 4095;
inline bool fireDuringColorCapture = false;
inline void pinMode(int, int) {}
inline void digitalWrite(int pin, int value) { outputs.at(pin) = value; }
inline int analogRead(int) { return flameValue; }
inline void delay(unsigned long duration) {
    if (duration == 100 && fireDuringColorCapture) flameValue = 100;
}

struct SerialFake {
    std::ostringstream output;
    void begin(int) {}
    template <typename T> void print(T value) { output << value; }
    template <typename T> void print(T value, int) { print(value); }
    template <typename T> void println(T value) { output << value << '\n'; }
};
inline SerialFake Serial;

struct TwoWire {
    bool beginSuccess = true;
    uint16_t timeout = 0;
    uint8_t probeResult = 0, registerResult = 0, received = 8;
    uint8_t lastAddress = 0, lastRegister = 0;
    int transmissions = 0, requests = 0, readIndex = 0, failReadAt = -1;
    bool illuminatedDuringRead = false;
    std::array<uint8_t, 8> bytes{0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A, 0xF0, 0xDE};

    bool begin(int, int) { return beginSuccess; }
    void setTimeOut(uint16_t value) { timeout = value; }
    void beginTransmission(uint8_t address) { lastAddress = address; transmissions++; }
    void write(uint8_t value) { lastRegister = value; }
    uint8_t endTransmission(bool stop = true) { return stop ? probeResult : registerResult; }
    uint8_t requestFrom(uint8_t, uint8_t) {
        requests++;
        readIndex = 0;
        illuminatedDuringRead = outputs[COLOR_LED_PIN] == HIGH;
        return received;
    }
    int read() {
        if (readIndex == failReadAt) return -1;
        return bytes.at(readIndex++);
    }
};
inline TwoWire Wire, Wire1;

struct sensors_event_t { float temperature = 0, relative_humidity = 0; };
struct Adafruit_AHTX0 {
    float temperature = 25, humidity = 45;
    int readings = 0;
    bool begin(TwoWire*) { return true; }
    void getEvent(sensors_event_t* h, sensors_event_t* t) {
        readings++;
        h->relative_humidity = humidity;
        t->temperature = temperature;
    }
};

struct Adafruit_TCS34725 {
    bool beginSuccess = true;
    int starts = 0;
    Adafruit_TCS34725(int, int) {}
    bool begin(uint8_t, TwoWire*) { starts++; return beginSuccess; }
};

struct ESP32PWM { static void allocateTimer(int) {} };
struct Servo {
    std::vector<int> angles;
    void setPeriodHertz(int) {}
    void attach(int, int, int) {}
    void write(int angle) { angles.push_back(angle); }
};
