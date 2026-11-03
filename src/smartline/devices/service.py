"""Device state transitions and retained command ordering."""

import re
import threading
from contextlib import contextmanager

from django.conf import settings
from django.db import transaction
from django.utils import timezone

from smartline.inspection.validation import finite
from smartline.web.errors import ApiError, missing
from smartline.web.events import bus
from smartline.web.serialization import fields
from .models import Alert, ConveyorSetting, SystemLog
from .mqtt import transport

command_lock = threading.RLock()
pending_lock = threading.Lock()
pending_emergencies = 0


def status(setting=None):
    setting = setting or ConveyorSetting.objects.filter(pk=1).first()
    if setting is None:
        missing("ConveyorSetting")
    return fields(
        setting,
        "speed is_running temperature humidity fan_active is_emergency_stopped is_fire_detected fire_device_epoch last_fire_generation is_fire_generation_fault last_updated",
    )


def alert_data(alert):
    return fields(alert, "id alert_type is_active triggered_at resolved_at")


def log_data(item):
    return fields(item, "id event_type message severity created_at")


def audit(event, message, severity="Info"):
    item = SystemLog.objects.create(event_type=event, message=message, severity=severity)
    data = log_data(item)
    transaction.on_commit(lambda: bus.publish("ReceiveLogUpdate", data))
    return item


def alert(kind):
    item = Alert.objects.create(alert_type=kind)
    data = alert_data(item)
    transaction.on_commit(lambda: bus.publish("ReceiveAlertUpdate", data))
    return item


def resolve_alert(identifier):
    with transaction.atomic():
        item = Alert.objects.select_for_update().filter(pk=identifier).first()
        if item is None:
            missing("Alert")
        if item.is_active:
            item.is_active = False
            item.resolved_at = timezone.now()
            item.save()
            audit("ALERT_RESOLVED", f"Alarm #{identifier} çözüldü")
            data = alert_data(item)
            transaction.on_commit(lambda: bus.publish("ReceiveAlertUpdate", data))
    return alert_data(item)


def _blocked(s):
    if s.is_fire_generation_fault:
        raise ApiError("Yangın sensörü sayacı geri sardı", "FIRE_GENERATION_MISMATCH", 409)
    if s.is_fire_detected:
        raise ApiError("Yangın sensörü aktif", "FIRE_SENSOR_ACTIVE", 409)
    if s.is_emergency_stopped:
        raise ApiError("Acil durdurma latch'i etkin", "CONVEYOR_EMERGENCY_LATCHED", 409)


def _state(s, emergency=None):
    transport.state(
        s.is_running if emergency is None else False,
        s.is_emergency_stopped if emergency is None else emergency,
        s.emergency_reset_generation,
        s.fire_device_epoch,
        s.last_fire_generation,
    )


def _best_effort(callback):
    try:
        callback()
    except ApiError as error:
        return error
    return None


def _serialized(s):
    if pending_emergencies:
        return None
    error = _best_effort(lambda: _state(s))
    if pending_emergencies:
        return _best_effort(lambda: _state(s, True)) or error
    return error


def publish_desired_state():
    with command_lock:
        s = ConveyorSetting.objects.get(pk=1)
        failure = _serialized(s)
        transport.publish("smartline/commands/speed", {"speed": s.speed})
        transport.publish("smartline/commands/fan", {"active": s.fan_active})
        if failure:
            raise failure


def control(action, payload=None):
    payload = payload or {}
    if action == "emergency-stop":
        return emergency()
    with command_lock:
        with transaction.atomic():
            s = ConveyorSetting.objects.select_for_update().get(pk=1)
            if action == "emergency-reset":
                if s.is_fire_generation_fault:
                    raise ApiError(
                        "Yeni bir güvenlik epoch'u gerekli", "FIRE_GENERATION_MISMATCH", 409
                    )
                if s.is_fire_detected:
                    raise ApiError(
                        "Yangın sensörü aktifken reset yapılamaz", "FIRE_SENSOR_ACTIVE", 409
                    )
                if s.emergency_reset_generation == 2147483647:
                    raise OverflowError("Emergency reset generation overflow")
                s.is_emergency_stopped = False
                s.is_running = False
                s.emergency_reset_generation += 1
                audit("EMERGENCY_RESET", "Acil durdurma latch'i sıfırlandı", "Warning")
            else:
                _blocked(s)
                if action == "speed":
                    speed = payload.get("speed")
                    if (
                        not isinstance(speed, int)
                        or isinstance(speed, bool)
                        or not 0 <= speed <= 100
                    ):
                        raise ApiError("Hız 0-100 arasında olmalıdır")
                    if s.is_running:
                        raise ApiError(
                            "Konveyör çalışırken hız değiştirilemez",
                            "CONVEYOR_MUST_BE_STOPPED",
                            409,
                        )
                    s.speed = speed
                    audit("SPEED_CHANGED", f"Konveyör hızı {speed}'e güncellendi")
                elif action in ["start", "stop"]:
                    running = action == "start"
                    if s.is_running != running:
                        audit(
                            "SYSTEM_STARTED" if running else "SYSTEM_STOPPED",
                            "Konveyör başlatıldı"
                            if running
                            else "Konveyör normal olarak durduruldu",
                        )
                    s.is_running = running
                else:
                    missing()
            s.last_updated = timezone.now()
            s.save()
        failure = (
            _best_effort(lambda: transport.publish("smartline/commands/speed", {"speed": s.speed}))
            if action == "speed"
            else _serialized(s)
        )
    result = status(s)
    bus.publish("ReceiveConveyorUpdate", result)
    if failure:
        raise failure
    return result


@contextmanager
def _pending():
    global pending_emergencies
    with pending_lock:
        pending_emergencies += 1
    try:
        yield
    finally:
        with pending_lock:
            pending_emergencies -= 1


def emergency(reading=None):
    with _pending():
        failure = _best_effort(lambda: transport.state(False, True, 0, "", 0))
        with command_lock:
            try:
                with transaction.atomic():
                    s = ConveyorSetting.objects.select_for_update().get(pk=1)
                    first = not s.is_emergency_stopped
                    rollback = False
                    if reading:
                        epoch, generation = reading["deviceEpoch"], reading["fireGeneration"]
                        changed = epoch != s.fire_device_epoch
                        rollback = not changed and generation < s.last_fire_generation
                        advanced = not changed and generation > s.last_fire_generation
                        first = changed or advanced or rollback and not s.is_fire_generation_fault
                        if changed:
                            s.fire_device_epoch = epoch
                            s.last_fire_generation = generation
                            s.is_fire_generation_fault = False
                        elif rollback:
                            s.is_fire_generation_fault = True
                        elif advanced:
                            s.last_fire_generation = generation
                    s.is_running = False
                    s.is_emergency_stopped = True
                    s.is_fire_detected = s.is_fire_detected or reading is not None
                    s.last_updated = timezone.now()
                    if first:
                        alert("FIRE" if reading else "EMERGENCY_STOP")
                        audit(
                            "FIRE_DETECTED" if reading else "EMERGENCY_STOP",
                            "Yangın sensörü sayacı geri sardı"
                            if rollback
                            else "Yangın algılandı; acil durdurma latch'i etkinleştirildi"
                            if reading
                            else "Acil durdurma latch'i operatör tarafından etkinleştirildi",
                            "Critical",
                        )
                    s.save()
            except Exception:
                _best_effort(lambda: transport.state(False, True, 0, "", 0))
                raise
            final = _best_effort(lambda: _state(s, True))
        result = status(s)
        bus.publish("ReceiveConveyorUpdate", result)
        if final or failure:
            raise final or failure
        return result


def fire(reading):
    if (
        not isinstance(reading.get("isDetected"), bool)
        or not isinstance(reading.get("deviceEpoch"), str)
        or not re.fullmatch(r"[0-9a-f]{32}", reading["deviceEpoch"])
    ):
        raise ApiError("Device epoch tam olarak 32 küçük harf hexadecimal karakterden oluşmalıdır")
    generation = reading.get("fireGeneration")
    if (
        not isinstance(generation, int)
        or isinstance(generation, bool)
        or not 0 <= generation <= 4294967295
    ):
        raise ApiError("Geçersiz fire generation değeri")
    if reading["isDetected"]:
        return emergency(reading)
    with command_lock:
        with transaction.atomic():
            s = ConveyorSetting.objects.select_for_update().get(pk=1)
            changed = reading["deviceEpoch"] != s.fire_device_epoch
            missed = not changed and generation > s.last_fire_generation
            rollback = not changed and generation < s.last_fire_generation
            transition = changed or missed or rollback and not s.is_fire_generation_fault
            if changed:
                s.fire_device_epoch = reading["deviceEpoch"]
                s.last_fire_generation = generation
                s.is_fire_generation_fault = False
            elif rollback:
                s.is_fire_generation_fault = True
            elif missed:
                s.last_fire_generation = generation
            immediate = None
            if changed or missed or rollback:
                immediate = _best_effort(lambda: _state(s, True))
                s.is_running = False
                s.is_emergency_stopped = True
            s.is_fire_detected = False
            s.last_updated = timezone.now()
            if transition:
                alert("FIRE")
                audit(
                    "FIRE_DETECTED",
                    "Yangın sensörü güvenlik durumu değişti; latch korundu",
                    "Critical",
                )
            s.save()
        final = _serialized(s)
    result = status(s)
    bus.publish("ReceiveConveyorUpdate", result)
    if final or immediate:
        raise final or immediate
    return result


def environment(reading):
    temp, humidity = reading.get("temperature", 0), reading.get("humidity", 0)
    if (
        not finite(temp)
        or not finite(humidity)
        or not -40 <= temp <= 80
        or not 0 <= humidity <= 100
    ):
        raise ApiError("Geçersiz ortam verisi")
    with transaction.atomic():
        s = ConveyorSetting.objects.select_for_update().get(pk=1)
        old = s.fan_active
        s.temperature, s.humidity = temp, humidity
        if temp > settings.HIGH_TEMP_THRESHOLD + settings.FAN_HYSTERESIS:
            s.fan_active = True
        elif temp < settings.HIGH_TEMP_THRESHOLD - settings.FAN_HYSTERESIS:
            s.fan_active = False
        s.last_updated = timezone.now()
        s.save()
        if temp > settings.ALARM_TEMP_THRESHOLD:
            alert("HIGH_TEMPERATURE")
            audit("HIGH_TEMPERATURE_DETECTED", f"Yüksek sıcaklık algılandı: {temp}°C", "Warning")
    failure = (
        _best_effort(lambda: transport.publish("smartline/commands/fan", {"active": s.fan_active}))
        if old != s.fan_active
        else None
    )
    bus.publish(
        "ReceiveEnvironmentUpdate",
        {"temperature": temp, "humidity": humidity, "fanActive": s.fan_active},
    )
    if failure:
        raise failure
    return f"Ortam verisi kaydedildi: {temp}°C, %{humidity}"
