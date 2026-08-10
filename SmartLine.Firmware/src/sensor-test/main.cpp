#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_AHTX0.h>
#include <Adafruit_TCS34725.h>
#include <ESP32Servo.h>
#include "Config.h"

// --- Sensör Nesneleri ---
Adafruit_AHTX0 aht;
Adafruit_TCS34725 tcs = Adafruit_TCS34725(TCS34725_INTEGRATIONTIME_50MS, TCS34725_GAIN_4X);
Servo myServo;

bool yanginDurumu = false;

void setup() {
    Serial.begin(115200);
    delay(1000);
    Serial.println("\n\n\n\n\n");
    Serial.println("Sistem Baslatiliyor...");

    // Pin Modları
    pinMode(COLOR_LED_PIN, OUTPUT);
    pinMode(BUZZER_PIN, OUTPUT);
    pinMode(FAN_PIN, OUTPUT);
    pinMode(FLAME_PIN, INPUT);

    // Başlangıçta eyleyicileri kapalı tut
    digitalWrite(COLOR_LED_PIN, LOW);
    digitalWrite(BUZZER_PIN, LOW);
    digitalWrite(FAN_PIN, LOW);

    // Servo Motor Kurulumu
    ESP32PWM::allocateTimer(0);
    myServo.setPeriodHertz(50);
    myServo.attach(SERVO_PIN, 500, 2400);
    myServo.write(0);

    // I2C Hatlarının Kurulumu
    Wire.begin(21, 22);   // 1. Hat: AHT10
    Wire1.begin(32, 33);  // 2. Hat: TCS34725
    delay(500);

    // AHT10 Başlatma
    if (!aht.begin(&Wire)) {
        Serial.println("HATA: AHT10 Sicaklik Sensoru bulunamadi!");
    } else {
        Serial.println("AHT10 Basariyla Bulundu.");
    }

    // TCS34725 Başlatma
    if (!tcs.begin(TCS34725_ADDRESS, &Wire1)) {
        Serial.println("UYARI: TCS34725 Renk Sensoru bagli degil veya bulunamadi!");
    } else {
        Serial.println("TCS34725 Basariyla Bulundu.");
    }

    Serial.println("--- TUM SISTEM HAZIR ---");
    delay(2000);
}

void loop() {
    // 1. ÖLÇÜMLER
    sensors_event_t humidity, temp;
    aht.getEvent(&humidity, &temp);

    int alevDegeri = analogRead(FLAME_PIN);

    // Renk Okuma (Aydınlatmayı anlık aç/kapat)
    digitalWrite(COLOR_LED_PIN, HIGH);
    delay(100);
    uint16_t r, g, b, c;
    tcs.getRawData(&r, &g, &b, &c);
    digitalWrite(COLOR_LED_PIN, LOW);

    // 2. MANTIK VE EYLEYİCİ KONTROL

    // Fan Kontrolü
    bool fanAcik = false;
    if (temp.temperature >= SICAKLIK_SINIRI) {
        digitalWrite(FAN_PIN, HIGH);
        fanAcik = true;
    } else {
        digitalWrite(FAN_PIN, LOW);
    }

    // Güvenlik ve Alarm Mantığı
    int servoAcisi = 0;

    if (alevDegeri < ALEV_SINIRI) {
        // YANGIN VAR
        digitalWrite(BUZZER_PIN, HIGH);
        myServo.write(0);
        yanginDurumu = true;
        servoAcisi = 0;
    } else {
        // YANGIN YOK
        digitalWrite(BUZZER_PIN, LOW);
        yanginDurumu = false;

        // Renk Kontrolü (Kırmızı Kart)
        if (r > g + 15 && r > b + 15 && c > 20) {
            myServo.write(90);
            servoAcisi = 90;
        } else {
            myServo.write(0);
            servoAcisi = 0;
        }
    }

    // 3. KONSOL ÇIKTISI
    Serial.println("\n==================================================");
    Serial.print("SICAKLIK: "); Serial.print(temp.temperature, 1);
    Serial.print(" C   |   NEM: %"); Serial.println(humidity.relative_humidity, 1);
    Serial.print("ALEV    : ");
    Serial.print(yanginDurumu ? "TEHLIKE! " : "GUVENDE  ");
    Serial.print("(Sensor: "); Serial.print(alevDegeri); Serial.println(")");
    Serial.print("RENK    : R:"); Serial.print(r);
    Serial.print("  G:"); Serial.print(g);
    Serial.print("  B:"); Serial.print(b);
    Serial.print("  Parlaklik:"); Serial.println(c);
    Serial.print("SISTEM  : Fan -> ");
    Serial.print(fanAcik ? "ACIK" : "KAPALI");
    Serial.print("  |  Alarm -> ");
    Serial.print(yanginDurumu ? "AKTIF" : "SESSIZ");
    Serial.print("  |  Kapi(Servo) -> ");
    Serial.print(servoAcisi); Serial.println(" derece");
    Serial.println("==================================================");

    delay(2500);
}
