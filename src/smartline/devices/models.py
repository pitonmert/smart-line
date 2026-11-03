from django.db import models
from django.utils import timezone


class ConveyorSetting(models.Model):
    id = models.IntegerField(primary_key=True, default=1)
    speed = models.IntegerField(default=50)
    is_running = models.BooleanField(default=False)
    is_emergency_stopped = models.BooleanField(default=False)
    is_fire_detected = models.BooleanField(default=False)
    emergency_reset_generation = models.IntegerField(default=0)
    fire_device_epoch = models.CharField(max_length=32, default="")
    last_fire_generation = models.BigIntegerField(default=0)
    is_fire_generation_fault = models.BooleanField(default=False)
    temperature = models.DecimalField(max_digits=12, decimal_places=4, default=22)
    humidity = models.DecimalField(max_digits=12, decimal_places=4, default=45)
    fan_active = models.BooleanField(default=False)
    last_updated = models.DateTimeField(default=timezone.now)


class Alert(models.Model):
    alert_type = models.CharField(max_length=50, db_index=True)
    is_active = models.BooleanField(default=True, db_index=True)
    triggered_at = models.DateTimeField(default=timezone.now, db_index=True)
    resolved_at = models.DateTimeField(null=True)
    updated_at = models.DateTimeField(auto_now=True)


class SystemLog(models.Model):
    event_type = models.CharField(max_length=50, db_index=True)
    message = models.TextField(null=True)
    severity = models.CharField(max_length=20, default="Info", db_index=True)
    created_at = models.DateTimeField(default=timezone.now, db_index=True)
