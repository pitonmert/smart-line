#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Wire.h>
#include <Adafruit_AHTX0.h>
#include <Adafruit_TCS34725.h>
#include <ESP32Servo.h>
#include <Preferences.h>
#include <esp_system.h>
#include <time.h>
#include "Config.h"
#include "MqttTlsConfig.h"

// These topics must match the API MQTT options and Mosquitto ACL.
const char* TOPIC_ENV = "smartline/sensors/environment";
const char* TOPIC_FIRE = "smartline/sensors/fire";
const char* TOPIC_CMD_SPEED = "smartline/commands/speed";
const char* TOPIC_CMD_CONVEYOR = "smartline/commands/stop";
const char* TOPIC_CMD_FAN = "smartline/commands/fan";

Adafruit_AHTX0 aht;
Adafruit_TCS34725 tcs = Adafruit_TCS34725(
    TCS34725_INTEGRATIONTIME_50MS,
    TCS34725_GAIN_4X
);
Servo conveyorServo;

WiFiClientSecure tlsClient;
PubSubClient mqttClient(tlsClient);
Preferences safetyPreferences;

constexpr uint32_t SAFETY_RECORD_MAGIC = 0x534C5346; // "SLSF"
constexpr uint16_t SAFETY_RECORD_VERSION = 1;
constexpr size_t DEVICE_EPOCH_HEX_LENGTH = 32;
constexpr const char* SAFETY_RECORD_KEY = "safetyState";

struct SafetyRecord {
    uint32_t magic;
    uint16_t version;
    uint16_t size;
    uint32_t fireGeneration;
    uint32_t resetGeneration;
    uint8_t emergencyLatched;
    uint8_t reserved[3];
    uint32_t checksum;
    char deviceEpoch[DEVICE_EPOCH_HEX_LENGTH + 1];
    uint8_t trailingReserved[3];
};

static_assert(sizeof(SafetyRecord) == 60, "Unexpected safety record layout");

// The controller starts stopped and stays stopped until a retained start state arrives.
bool commandRunning = false;
bool emergencyLatched = true;
uint32_t lastAppliedResetGeneration = 0;
uint32_t localFireGeneration = 0;
char deviceEpoch[DEVICE_EPOCH_HEX_LENGTH + 1] = {};
bool safetyPersistenceReady = false;
bool safetyPersistenceFault = false;
bool localFireActive = false;
bool lastPublishedFire = false;
bool hasPublishedFire = false;
bool fanApiOverride = false;
bool fanApiState = false;
bool conveyorOutputReady = false;
int targetSpeed = 50;
int requestedServoAngle = 0;
int latestFlameReading = 4095;

unsigned long lastPublishTime = 0;
const unsigned long PUBLISH_INTERVAL_MS = 2500;

void publishFireStateIfNeeded();
void applyConveyorOutput();

uint32_t calculateSafetyRecordChecksum(SafetyRecord record) {
    record.checksum = 0;
    const auto* bytes = reinterpret_cast<const uint8_t*>(&record);
    uint32_t checksum = 2166136261u;
    for (size_t index = 0; index < sizeof(record); index++) {
        checksum ^= bytes[index];
        checksum *= 16777619u;
    }
    return checksum;
}

bool isValidDeviceEpoch(const char* epoch) {
    if (
        epoch == nullptr
        || strnlen(epoch, DEVICE_EPOCH_HEX_LENGTH + 1) != DEVICE_EPOCH_HEX_LENGTH
    ) {
        return false;
    }

    bool containsNonZeroCharacter = false;
    for (size_t index = 0; index < DEVICE_EPOCH_HEX_LENGTH; index++) {
        const char value = epoch[index];
        if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) {
            return false;
        }
        containsNonZeroCharacter = containsNonZeroCharacter || value != '0';
    }
    return containsNonZeroCharacter;
}

bool isValidSafetyRecord(const SafetyRecord& record) {
    return record.magic == SAFETY_RECORD_MAGIC
        && record.version == SAFETY_RECORD_VERSION
        && record.size == sizeof(SafetyRecord)
        && record.emergencyLatched <= 1
        && isValidDeviceEpoch(record.deviceEpoch)
        && record.checksum == calculateSafetyRecordChecksum(record);
}

void generateDeviceEpoch(char (&epoch)[DEVICE_EPOCH_HEX_LENGTH + 1]) {
    static constexpr char HEX_DIGITS[] = "0123456789abcdef";
    uint8_t randomBytes[DEVICE_EPOCH_HEX_LENGTH / 2];
    bool allZero;
    do {
        esp_fill_random(randomBytes, sizeof(randomBytes));
        allZero = true;
        for (const uint8_t value : randomBytes) {
            allZero = allZero && value == 0;
        }
    } while (allZero);

    for (size_t index = 0; index < sizeof(randomBytes); index++) {
        epoch[index * 2] = HEX_DIGITS[randomBytes[index] >> 4];
        epoch[index * 2 + 1] = HEX_DIGITS[randomBytes[index] & 0x0F];
    }
    epoch[DEVICE_EPOCH_HEX_LENGTH] = '\0';
}

SafetyRecord createSafetyRecord(
    const char* epoch,
    uint32_t fireGeneration,
    uint32_t resetGeneration,
    bool latched
) {
    SafetyRecord record{};
    record.magic = SAFETY_RECORD_MAGIC;
    record.version = SAFETY_RECORD_VERSION;
    record.size = sizeof(SafetyRecord);
    record.fireGeneration = fireGeneration;
    record.resetGeneration = resetGeneration;
    record.emergencyLatched = latched ? 1 : 0;
    strlcpy(record.deviceEpoch, epoch, sizeof(record.deviceEpoch));
    record.checksum = calculateSafetyRecordChecksum(record);
    return record;
}

void enterSafetyPersistenceFault(const char* message) {
    safetyPersistenceFault = true;
    emergencyLatched = true;
    commandRunning = false;
    applyConveyorOutput();
    Serial.print("[SAFETY] NVS persistence fault; start disabled: ");
    Serial.println(message);
}

bool persistSafetyRecord(
    const char* epoch,
    uint32_t fireGeneration,
    uint32_t resetGeneration,
    bool latched
) {
    if (!safetyPersistenceReady) {
        enterSafetyPersistenceFault("NVS namespace is unavailable");
        return false;
    }

    const SafetyRecord record = createSafetyRecord(
        epoch,
        fireGeneration,
        resetGeneration,
        latched
    );
    const size_t bytesWritten = safetyPreferences.putBytes(
        SAFETY_RECORD_KEY,
        &record,
        sizeof(record)
    );
    if (bytesWritten != sizeof(record)) {
        enterSafetyPersistenceFault("could not atomically persist safety record");
        return false;
    }
    return true;
}

void setEmergencyLatched(bool latched) {
    if (emergencyLatched == latched) {
        return;
    }
    if (!latched && safetyPersistenceFault) {
        emergencyLatched = true;
        return;
    }

    if (latched) {
        // A safety transition affects RAM and the physical output before flash I/O.
        emergencyLatched = true;
        commandRunning = false;
        applyConveyorOutput();
    }

    if (!persistSafetyRecord(
        deviceEpoch,
        localFireGeneration,
        lastAppliedResetGeneration,
        latched
    )) {
        return;
    }

    emergencyLatched = latched;
}

void recordLocalFireEvent() {
    // Make the physical state safe before any potentially blocking NVS operation.
    localFireActive = true;
    emergencyLatched = true;
    commandRunning = false;
    applyConveyorOutput();

    if (localFireGeneration == UINT32_MAX) {
        enterSafetyPersistenceFault("local fire generation exhausted");
        return;
    }

    const uint32_t nextGeneration = localFireGeneration + 1;
    // Latch and generation are committed together as one checksummed NVS blob. A torn or
    // corrupt record is rejected on boot and leaves the firmware fail-safe latched.
    if (!persistSafetyRecord(
        deviceEpoch,
        nextGeneration,
        lastAppliedResetGeneration,
        true
    )) {
        return;
    }

    localFireGeneration = nextGeneration;
}

bool commandAcknowledgesLocalFire(
    const char* acknowledgedFireEpoch,
    uint32_t acknowledgedFireGeneration
) {
    return acknowledgedFireEpoch != nullptr
        && strcmp(acknowledgedFireEpoch, deviceEpoch) == 0
        && acknowledgedFireGeneration >= localFireGeneration;
}

void consumeResetGeneration(
    uint32_t generation,
    const char* acknowledgedFireEpoch,
    uint32_t acknowledgedFireGeneration
) {
    if (generation <= lastAppliedResetGeneration) {
        return;
    }
    if (safetyPersistenceFault) {
        commandRunning = false;
        emergencyLatched = true;
        return;
    }

    commandRunning = false;
    const bool acknowledgementMatches = commandAcknowledgesLocalFire(
        acknowledgedFireEpoch,
        acknowledgedFireGeneration
    );
    const bool canClearLatch = acknowledgementMatches && !localFireActive;

    if (!persistSafetyRecord(
        deviceEpoch,
        localFireGeneration,
        generation,
        canClearLatch ? false : emergencyLatched
    )) {
        return;
    }

    lastAppliedResetGeneration = generation;
    if (!acknowledgementMatches) {
        Serial.println("[SAFETY] Admin reset rejected because the local fire epoch/generation is unacknowledged.");
        return;
    }
    if (localFireActive) {
        Serial.println("[SAFETY] Admin reset rejected because local fire is active.");
        return;
    }

    emergencyLatched = false;
    Serial.println("[SAFETY] New Admin reset generation applied; conveyor remains stopped.");
}

void applyConveyorOutput() {
    const bool mustBeSafe =
        localFireActive
        || emergencyLatched
        || safetyPersistenceFault
        || !commandRunning;
    if (conveyorOutputReady) {
        conveyorServo.write(mustBeSafe ? 0 : requestedServoAngle);
    }
}

void updateLocalFireSafety() {
    latestFlameReading = analogRead(FLAME_PIN);
    const bool fireNow = latestFlameReading < ALEV_SINIRI;

    if (fireNow && !localFireActive) {
        // This routine makes RAM/output safe first, then atomically persists latch + generation.
        recordLocalFireEvent();
    } else {
        localFireActive = fireNow;
    }

    if (localFireActive) {
        setEmergencyLatched(true);
        commandRunning = false;
    }

    digitalWrite(BUZZER_PIN, localFireActive ? HIGH : LOW);
    applyConveyorOutput();
}

void handleConveyorCommand(JsonDocument& document) {
    if (
        !document["isRunning"].is<bool>()
        || !document["emergency"].is<bool>()
    ) {
        Serial.println("[MQTT] Invalid conveyor state payload ignored.");
        return;
    }

    const bool requestedRunning = document["isRunning"].as<bool>();
    const bool requestedEmergency = document["emergency"].as<bool>();

    if (requestedEmergency) {
        // Emergency stop never depends on auxiliary acknowledgement fields being well formed.
        setEmergencyLatched(true);
        commandRunning = false;
        Serial.println("[SAFETY] Remote emergency stop latched.");
        applyConveyorOutput();
        return;
    }

    // Even a malformed normal-stop payload is safe to honor. Missing acknowledgement data must
    // never clear a latch or authorize a start.
    if (!requestedRunning) {
        commandRunning = false;
        applyConveyorOutput();
    }

    if (
        !document["resetGeneration"].is<uint32_t>()
        || !document["acknowledgedFireEpoch"].is<const char*>()
        || !document["acknowledgedFireGeneration"].is<uint32_t>()
    ) {
        commandRunning = false;
        applyConveyorOutput();
        Serial.println("[MQTT] Conveyor acknowledgement fields are invalid; start/reset rejected.");
        return;
    }

    const uint32_t requestedResetGeneration = document["resetGeneration"].as<uint32_t>();
    const char* acknowledgedFireEpoch = document["acknowledgedFireEpoch"].as<const char*>();
    const uint32_t acknowledgedFireGeneration =
        document["acknowledgedFireGeneration"].as<uint32_t>();

    consumeResetGeneration(
        requestedResetGeneration,
        acknowledgedFireEpoch,
        acknowledgedFireGeneration
    );

    if (!requestedRunning) {
        commandRunning = false;
        Serial.println("[CMD] Normal conveyor stop applied.");
    } else if (
        localFireActive
        || emergencyLatched
        || safetyPersistenceFault
        || !commandAcknowledgesLocalFire(
            acknowledgedFireEpoch,
            acknowledgedFireGeneration
        )
    ) {
        commandRunning = false;
        Serial.println("[SAFETY] Start rejected because a safety interlock or stale acknowledgement is active.");
    } else {
        commandRunning = true;
        Serial.println("[CMD] Conveyor start applied.");
    }

    applyConveyorOutput();
}

void mqttCallback(char* topic, byte* payload, unsigned int length) {
    String topicValue(topic);
    String payloadValue;
    payloadValue.reserve(length);
    for (unsigned int index = 0; index < length; index++) {
        payloadValue += static_cast<char>(payload[index]);
    }

    JsonDocument document;
    const DeserializationError error = deserializeJson(document, payloadValue);
    if (error) {
        Serial.println("[MQTT] Invalid JSON command ignored.");
        return;
    }

    if (topicValue == TOPIC_CMD_CONVEYOR) {
        handleConveyorCommand(document);
    } else if (topicValue == TOPIC_CMD_SPEED) {
        if (!document["speed"].is<int>()) {
            Serial.println("[MQTT] Invalid speed payload ignored.");
            return;
        }

        targetSpeed = constrain(document["speed"].as<int>(), 0, 100);
        Serial.print("[CMD] Target speed updated: ");
        Serial.println(targetSpeed);
        // A speed update never changes running or emergency state.
    } else if (topicValue == TOPIC_CMD_FAN) {
        if (!document["active"].is<bool>()) {
            Serial.println("[MQTT] Invalid fan payload ignored.");
            return;
        }

        fanApiState = document["active"].as<bool>();
        fanApiOverride = true;
        digitalWrite(FAN_PIN, fanApiState ? HIGH : LOW);
        Serial.print("[CMD] Fan override: ");
        Serial.println(fanApiState ? "ON" : "OFF");
    }
}

void connectMqtt() {
    commandRunning = false;
    applyConveyorOutput();

    while (!mqttClient.connected()) {
        updateLocalFireSafety();
        Serial.print("[MQTT] Connecting with TLS...");

        if (
            mqttClient.connect(
                MQTT_CLIENT_ID,
                MQTT_USERNAME,
                MQTT_PASSWORD,
                nullptr,
                0,
                false,
                nullptr
            )
        ) {
            Serial.println(" connected.");
            hasPublishedFire = false;
            mqttClient.subscribe(TOPIC_CMD_CONVEYOR, 1);
            mqttClient.subscribe(TOPIC_CMD_SPEED, 1);
            mqttClient.subscribe(TOPIC_CMD_FAN, 1);
            publishFireStateIfNeeded();
            Serial.println("[MQTT] Command topics subscribed at QoS 1.");
        } else {
            Serial.print(" failed, state=");
            Serial.print(mqttClient.state());
            Serial.println("; retrying in 3 seconds.");
            delay(3000);
        }
    }
}

void connectWifi() {
    WiFi.mode(WIFI_STA);
    WiFi.disconnect();
    delay(100);
    WiFi.begin(WIFI_SSID, WIFI_PASS);
    Serial.print("[WiFi] Connecting");

    int attempts = 0;
    while (WiFi.status() != WL_CONNECTED) {
        updateLocalFireSafety();
        delay(500);
        Serial.print(".");
        attempts++;

        if (attempts > 40) {
            Serial.println("\n[WiFi] Connection failed; restarting safely.");
            ESP.restart();
        }
    }

    Serial.println(" connected.");
    Serial.print("[WiFi] IP: ");
    Serial.println(WiFi.localIP());
}

void synchronizeClock() {
    configTime(0, 0, NTP_SERVER);
    Serial.print("[TLS] Synchronizing clock");

    const time_t minimumValidTime = 1704067200; // 2024-01-01 UTC
    time_t currentTime = time(nullptr);
    int attempts = 0;
    while (currentTime < minimumValidTime && attempts < 40) {
        updateLocalFireSafety();
        delay(500);
        Serial.print(".");
        currentTime = time(nullptr);
        attempts++;
    }

    if (currentTime < minimumValidTime) {
        Serial.println(" failed; restarting because TLS cannot be validated safely.");
        ESP.restart();
    }

    Serial.println(" synchronized.");
}

void setup() {
    Serial.begin(115200);
    delay(1000);

    pinMode(COLOR_LED_PIN, OUTPUT);
    pinMode(BUZZER_PIN, OUTPUT);
    pinMode(FAN_PIN, OUTPUT);
    pinMode(FLAME_PIN, INPUT);

    digitalWrite(COLOR_LED_PIN, LOW);
    digitalWrite(BUZZER_PIN, LOW);
    digitalWrite(FAN_PIN, LOW);

    safetyPersistenceReady = safetyPreferences.begin("smartline", false);
    if (!safetyPersistenceReady) {
        enterSafetyPersistenceFault("NVS namespace could not be opened");
    } else if (!safetyPreferences.isKey(SAFETY_RECORD_KEY)) {
        // A new board or erased NVS receives a fresh identity and deliberately starts latched.
        char provisionedEpoch[DEVICE_EPOCH_HEX_LENGTH + 1];
        generateDeviceEpoch(provisionedEpoch);
        if (persistSafetyRecord(provisionedEpoch, 0, 0, true)) {
            strlcpy(deviceEpoch, provisionedEpoch, sizeof(deviceEpoch));
            localFireGeneration = 0;
            lastAppliedResetGeneration = 0;
            emergencyLatched = true;
            Serial.println("[SAFETY] New safety identity provisioned in latched/stopped state.");
        }
    } else {
        const size_t storedSize = safetyPreferences.getBytesLength(SAFETY_RECORD_KEY);
        SafetyRecord storedRecord{};
        const size_t bytesRead = storedSize == sizeof(storedRecord)
            ? safetyPreferences.getBytes(SAFETY_RECORD_KEY, &storedRecord, sizeof(storedRecord))
            : 0;
        if (bytesRead != sizeof(storedRecord) || !isValidSafetyRecord(storedRecord)) {
            enterSafetyPersistenceFault("safety record is incomplete or corrupt");
        } else {
            strlcpy(deviceEpoch, storedRecord.deviceEpoch, sizeof(deviceEpoch));
            localFireGeneration = storedRecord.fireGeneration;
            lastAppliedResetGeneration = storedRecord.resetGeneration;
            emergencyLatched = storedRecord.emergencyLatched == 1;
        }
    }

    ESP32PWM::allocateTimer(0);
    conveyorServo.setPeriodHertz(50);
    conveyorServo.attach(SERVO_PIN, 500, 2400);
    conveyorOutputReady = true;
    conveyorServo.write(0);

    Wire.begin(21, 22);
    Wire1.begin(32, 33);
    delay(500);

    if (!aht.begin(&Wire)) {
        Serial.println("[SENSOR] AHT10 not found.");
    } else {
        Serial.println("[SENSOR] AHT10 ready.");
    }

    if (!tcs.begin(TCS34725_ADDRESS, &Wire1)) {
        Serial.println("[SENSOR] TCS34725 not found.");
    } else {
        Serial.println("[SENSOR] TCS34725 ready.");
    }

    updateLocalFireSafety();
    connectWifi();
    synchronizeClock();

    tlsClient.setCACert(MQTT_CA_CERT);
    tlsClient.setCertificate(MQTT_CLIENT_CERT);
    tlsClient.setPrivateKey(MQTT_CLIENT_PRIVATE_KEY);
    mqttClient.setServer(MQTT_BROKER, MQTT_PORT);
    mqttClient.setCallback(mqttCallback);
    mqttClient.setKeepAlive(30);
    mqttClient.setSocketTimeout(10);
    mqttClient.setBufferSize(512);
    connectMqtt();

    Serial.println("[SYSTEM] SmartLine firmware ready in safe stopped state.");
}

void publishFireStateIfNeeded() {
    if (hasPublishedFire && localFireActive == lastPublishedFire) {
        return;
    }

    char firePayload[160];
    snprintf(
        firePayload,
        sizeof(firePayload),
        "{\"isDetected\":%s,\"deviceEpoch\":\"%s\",\"fireGeneration\":%lu}",
        localFireActive ? "true" : "false",
        deviceEpoch,
        static_cast<unsigned long>(localFireGeneration)
    );
    if (mqttClient.publish(TOPIC_FIRE, firePayload, true)) {
        lastPublishedFire = localFireActive;
        hasPublishedFire = true;
        Serial.print("[MQTT] Fire state published: ");
        Serial.println(firePayload);
    } else {
        hasPublishedFire = false;
        Serial.println("[MQTT] Fire state publish failed; retrying while connected.");
    }
}

void publishEnvironmentData(float temperature, float humidity) {
    char environmentPayload[96];
    snprintf(
        environmentPayload,
        sizeof(environmentPayload),
        "{\"temperature\":%.1f,\"humidity\":%.1f}",
        temperature,
        humidity
    );
    mqttClient.publish(TOPIC_ENV, environmentPayload, false);
}

void loop() {
    updateLocalFireSafety();

    if (WiFi.status() != WL_CONNECTED) {
        commandRunning = false;
        applyConveyorOutput();
        WiFi.reconnect();
        delay(500);
        return;
    }

    if (!mqttClient.connected()) {
        connectMqtt();
    }
    mqttClient.loop();
    publishFireStateIfNeeded();

    const unsigned long now = millis();
    if (now - lastPublishTime < PUBLISH_INTERVAL_MS) {
        return;
    }
    lastPublishTime = now;

    sensors_event_t humidity;
    sensors_event_t temperature;
    aht.getEvent(&humidity, &temperature);

    digitalWrite(COLOR_LED_PIN, HIGH);
    delay(100);
    uint16_t red;
    uint16_t green;
    uint16_t blue;
    uint16_t clear;
    tcs.getRawData(&red, &green, &blue, &clear);
    digitalWrite(COLOR_LED_PIN, LOW);

    bool fanActive;
    if (fanApiOverride) {
        fanActive = fanApiState;
        fanApiOverride = false;
    } else {
        fanActive = temperature.temperature >= SICAKLIK_SINIRI;
        digitalWrite(FAN_PIN, fanActive ? HIGH : LOW);
    }

    const bool redDetected = red > 500 && red > green * 2 && red > blue * 2;
    requestedServoAngle = redDetected ? 90 : 0;
    updateLocalFireSafety();

    Serial.println("\n==================================================");
    Serial.print("TEMPERATURE: ");
    Serial.print(temperature.temperature, 1);
    Serial.print(" C | HUMIDITY: ");
    Serial.print(humidity.relative_humidity, 1);
    Serial.println("%");
    Serial.print("FIRE: ");
    Serial.print(localFireActive ? "ACTIVE" : "CLEAR");
    Serial.print(" (sensor=");
    Serial.print(latestFlameReading);
    Serial.println(")");
    Serial.print("CONVEYOR: running=");
    Serial.print(commandRunning ? "true" : "false");
    Serial.print(" emergencyLatched=");
    Serial.println(emergencyLatched ? "true" : "false");
    Serial.print("SAFETY GENERATIONS: fire=");
    Serial.print(localFireGeneration);
    Serial.print(" reset=");
    Serial.print(lastAppliedResetGeneration);
    Serial.print(" persistenceFault=");
    Serial.println(safetyPersistenceFault ? "true" : "false");
    Serial.print("SERVO: ");
    Serial.print(
        localFireActive || emergencyLatched || safetyPersistenceFault || !commandRunning
            ? 0
            : requestedServoAngle
    );
    Serial.println(" degrees");
    Serial.print("FAN: ");
    Serial.print(fanActive ? "ON" : "OFF");
    Serial.print(" | TARGET SPEED: ");
    Serial.println(targetSpeed);
    Serial.println("==================================================");

    publishEnvironmentData(temperature.temperature, humidity.relative_humidity);
}
