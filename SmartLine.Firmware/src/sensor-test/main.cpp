#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_AHTX0.h>
#include <Adafruit_TCS34725.h>
#include <ESP32Servo.h>
#include "Config.h"

Adafruit_AHTX0 aht;
Adafruit_TCS34725 tcs(TCS34725_INTEGRATIONTIME_50MS, TCS34725_GAIN_4X);
Servo myServo;

bool fireActive = false;
bool colorSensorReady = false;

struct ColorReading {
    uint16_t red;
    uint16_t green;
    uint16_t blue;
    uint16_t clear;
};

bool disableColorSensor() {
    digitalWrite(COLOR_LED_PIN, LOW);
    colorSensorReady = false;
    return false;
}

bool readColorSensor(ColorReading& reading) {
    if (!colorSensorReady) {
        return false;
    }

    Wire1.beginTransmission(TCS34725_ADDRESS);
    if (Wire1.endTransmission() != 0) {
        return disableColorSensor();
    }

    digitalWrite(COLOR_LED_PIN, HIGH);
    delay(100);

    // Read all four channels with checked I2C transfers; getRawData exposes no error status.
    constexpr uint8_t AUTO_INCREMENT = 0x20;
    Wire1.beginTransmission(TCS34725_ADDRESS);
    Wire1.write(TCS34725_COMMAND_BIT | AUTO_INCREMENT | TCS34725_CDATAL);
    if (Wire1.endTransmission(false) != 0) {
        return disableColorSensor();
    }

    constexpr uint8_t CHANNEL_BYTES = 8;
    const uint8_t received = Wire1.requestFrom(uint8_t(TCS34725_ADDRESS), CHANNEL_BYTES);
    digitalWrite(COLOR_LED_PIN, LOW);
    if (received != CHANNEL_BYTES) {
        return disableColorSensor();
    }

    uint8_t bytes[CHANNEL_BYTES];
    for (uint8_t index = 0; index < CHANNEL_BYTES; index++) {
        const int value = Wire1.read();
        if (value < 0) {
            return disableColorSensor();
        }
        bytes[index] = static_cast<uint8_t>(value);
    }

    reading.clear = uint16_t(bytes[0]) | (uint16_t(bytes[1]) << 8);
    reading.red = uint16_t(bytes[2]) | (uint16_t(bytes[3]) << 8);
    reading.green = uint16_t(bytes[4]) | (uint16_t(bytes[5]) << 8);
    reading.blue = uint16_t(bytes[6]) | (uint16_t(bytes[7]) << 8);
    return true;
}

void setup() {
    Serial.begin(115200);
    delay(1000);
    Serial.println("Starting local sensor diagnostics...");

    pinMode(COLOR_LED_PIN, OUTPUT);
    pinMode(BUZZER_PIN, OUTPUT);
    pinMode(FAN_PIN, OUTPUT);
    pinMode(FLAME_PIN, INPUT);
    digitalWrite(COLOR_LED_PIN, LOW);
    digitalWrite(BUZZER_PIN, LOW);
    digitalWrite(FAN_PIN, LOW);

    ESP32PWM::allocateTimer(0);
    myServo.setPeriodHertz(50);
    myServo.attach(SERVO_PIN, 500, 2400);
    myServo.write(0);

    Wire.begin(21, 22);
    const bool colorBusReady = Wire1.begin(32, 33);
    Wire1.setTimeOut(50);
    delay(500);

    if (!aht.begin(&Wire)) {
        Serial.println("[SENSOR] AHT10 not found.");
    } else {
        Serial.println("[SENSOR] AHT10 ready.");
    }

    colorSensorReady = colorBusReady && tcs.begin(TCS34725_ADDRESS, &Wire1);
    Serial.println(colorSensorReady
        ? "[SENSOR] TCS34725 ready for local monitoring and servo testing."
        : "[SENSOR] TCS34725 unavailable; color diagnostics disabled.");

    Serial.println("[SYSTEM] Sensor diagnostics ready; red triggers the local servo test.");
    delay(2000);
}

void loop() {
    sensors_event_t humidity, temperature;
    aht.getEvent(&humidity, &temperature);
    int flameReading = analogRead(FLAME_PIN);

    const bool fanActive = temperature.temperature >= SICAKLIK_SINIRI;
    digitalWrite(FAN_PIN, fanActive ? HIGH : LOW);
    fireActive = flameReading < ALEV_SINIRI;
    digitalWrite(BUZZER_PIN, fireActive ? HIGH : LOW);
    if (fireActive) {
        myServo.write(0);
    }

    ColorReading color;
    const bool hasColor = readColorSensor(color);

    // Recheck fire after the color measurement before allowing the local servo test.
    flameReading = analogRead(FLAME_PIN);
    fireActive = flameReading < ALEV_SINIRI;
    digitalWrite(BUZZER_PIN, fireActive ? HIGH : LOW);
    const bool redDetected = hasColor
        && color.red > color.green + 15
        && color.red > color.blue + 15
        && color.clear > 20;
    const int servoAngle = !fireActive && redDetected ? 90 : 0;
    myServo.write(servoAngle);

    Serial.println("\n==================================================");
    Serial.print("TEMPERATURE: "); Serial.print(temperature.temperature, 1);
    Serial.print(" C | HUMIDITY: "); Serial.print(humidity.relative_humidity, 1);
    Serial.println("%");
    Serial.print("FIRE: "); Serial.print(fireActive ? "ACTIVE" : "CLEAR");
    Serial.print(" (sensor="); Serial.print(flameReading); Serial.println(")");
    if (hasColor) {
        Serial.print("COLOR (local only): R:"); Serial.print(color.red);
        Serial.print(" G:"); Serial.print(color.green);
        Serial.print(" B:"); Serial.print(color.blue);
        Serial.print(" Clear:"); Serial.println(color.clear);
    } else {
        Serial.println("COLOR: unavailable (diagnostics disabled until restart)");
    }
    Serial.print("FAN: "); Serial.print(fanActive ? "ON" : "OFF");
    Serial.print(" | ALARM: "); Serial.print(fireActive ? "ACTIVE" : "SILENT");
    Serial.print(" | SERVO: "); Serial.print(servoAngle);
    Serial.println(" degrees (local test)");
    Serial.println("==================================================");
    delay(2500);
}
