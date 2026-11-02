// Exercise the actual diagnostic firmware with fake sensors and GPIO outputs.
#include "sensor_fakes.h"
#include "../src/sensor-test/main.cpp"

#include <cassert>
#include <iostream>
#include <string>

void checkOutputs(int expectedServo = 0) {
    assert(outputs[COLOR_LED_PIN] == LOW);
    assert(outputs[FAN_PIN] == (aht.temperature >= SICAKLIK_SINIRI ? HIGH : LOW));
    assert(outputs[BUZZER_PIN] == (flameValue < ALEV_SINIRI ? HIGH : LOW));
    assert(!myServo.angles.empty());
    assert(myServo.angles.back() == expectedServo);
}

void checkDisabled() {
    assert(!colorSensorReady);
    checkOutputs();
    assert(Serial.output.str().find("COLOR: unavailable") != std::string::npos);
    const int previousReads = Wire1.transmissions;
    loop();
    assert(Wire1.transmissions == previousReads);
    checkOutputs();
}

int main(int argc, char** argv) {
    assert(argc == 2);
    const std::string scenario = argv[1];
    if (scenario == "missing") tcs.beginSuccess = false;
    if (scenario == "bus-failure") Wire1.beginSuccess = false;
    setup();
    assert(Wire1.timeout == 50);
    assert(myServo.angles.back() == 0);

    if (scenario == "missing" || scenario == "bus-failure") {
        aht.temperature = 35;
        flameValue = 100;
        loop();
        assert(aht.readings == 1);
        assert(Wire1.transmissions == 0);
        assert(Wire1.requests == 0);
        assert(tcs.starts == (scenario == "missing" ? 1 : 0));
        checkDisabled();
    } else if (scenario == "valid") {
        ColorReading reading{};
        assert(readColorSensor(reading));
        assert(reading.clear == 0x1234 && reading.red == 0x5678);
        assert(reading.green == 0x9ABC && reading.blue == 0xDEF0);
        assert(Wire1.lastAddress == 0x29 && Wire1.lastRegister == 0xB4);
        assert(Wire1.illuminatedDuringRead);
        loop();
        assert(Serial.output.str().find("COLOR (local only): R:22136") != std::string::npos);
        checkOutputs();
    } else if (scenario == "zero") {
        Wire1.bytes.fill(0);
        loop();
        assert(colorSensorReady);
        assert(Serial.output.str().find("R:0 G:0 B:0 Clear:0") != std::string::npos);
        checkOutputs();
    } else if (scenario == "independent-outputs") {
        // Color drives only the local servo test; fan/alarm still follow their sensors.
        Wire1.bytes = {100, 0, 0xFF, 0x7F, 0, 0, 0, 0};
        loop();
        checkOutputs(90);
        Wire1.bytes = {100, 0, 0, 0, 0, 0, 0xFF, 0x7F};
        aht.temperature = 35;
        flameValue = 100;
        loop();
        checkOutputs();
        aht.temperature = 25;
        flameValue = 4095;
        loop();
        checkOutputs();
    } else if (scenario == "red-servo" || scenario == "fire-during-capture") {
        Wire1.bytes = {100, 0, 200, 0, 0, 0, 0, 0};
        fireDuringColorCapture = scenario == "fire-during-capture";
        loop();
        checkOutputs(fireDuringColorCapture ? 0 : 90);
        if (!fireDuringColorCapture) {
            const auto previousWrites = myServo.angles.size();
            loop();
            for (auto index = previousWrites; index < myServo.angles.size(); index++) {
                assert(myServo.angles[index] == 90);
            }
            flameValue = 100;
            loop();
            checkOutputs();
            flameValue = 4095;
            loop();
            checkOutputs(90);
        }
    } else if (scenario == "red-threshold") {
        Wire1.bytes = {20, 0, 200, 0, 0, 0, 0, 0};
        loop();
        checkOutputs();
        Wire1.bytes = {100, 0, 115, 0, 100, 0, 0, 0};
        loop();
        checkOutputs();
        Wire1.bytes = {100, 0, 115, 0, 0, 0, 100, 0};
        loop();
        checkOutputs();
    } else {
        // A failed reading must return the previously activated servo to neutral.
        Wire1.bytes = {100, 0, 200, 0, 0, 0, 0, 0};
        loop();
        checkOutputs(90);
        Serial.output.str("");
        if (scenario == "disconnected") Wire1.probeResult = 2;
        else if (scenario == "timeout") Wire1.probeResult = 5;
        else if (scenario == "register-failure") Wire1.registerResult = 5;
        else if (scenario == "truncated") Wire1.received = 7;
        else if (scenario == "empty") Wire1.received = 0;
        else if (scenario == "read-failure") Wire1.failReadAt = 3;
        else assert(false);
        aht.temperature = 35;
        flameValue = 4095;
        loop();
        assert(aht.readings == 2);
        assert(Serial.output.str().find("COLOR (local only):") == std::string::npos);
        checkDisabled();
    }
    std::cout << "sensor diagnostics checks passed: " << scenario << '\n';
}
