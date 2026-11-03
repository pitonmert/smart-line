import uuid

from django.db import models
from django.db.models import Q
from django.utils import timezone


class Product(models.Model):
    barcode = models.CharField(max_length=50, unique=True)
    color = models.CharField(max_length=20, null=True, blank=True, db_index=True)
    status = models.CharField(max_length=20, default="OK", db_index=True)
    passed_at = models.DateTimeField(default=timezone.now, db_index=True)
    updated_at = models.DateTimeField(auto_now=True)


class Statistic(models.Model):
    date = models.DateField()
    hour = models.IntegerField(null=True)
    total_products = models.IntegerField(default=0)
    defective_products = models.IntegerField(default=0)
    red_products = models.IntegerField(default=0)
    blue_products = models.IntegerField(default=0)
    green_products = models.IntegerField(default=0)
    yellow_products = models.IntegerField(default=0)

    class Meta:
        constraints = [
            models.UniqueConstraint(
                fields=["date", "hour"], name="statistics_date_hour", nulls_distinct=False
            )
        ]


class ProductRecipe(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    name = models.CharField(max_length=80, unique=True)
    description = models.CharField(max_length=1000, default="")
    is_active = models.BooleanField(default=False)
    version = models.IntegerField(default=1)
    definition = models.JSONField()
    created_at = models.DateTimeField(default=timezone.now)
    updated_at = models.DateTimeField(default=timezone.now)

    class Meta:
        constraints = [
            models.UniqueConstraint(
                fields=["is_active"], condition=Q(is_active=True), name="one_active_recipe"
            )
        ]


class CalibrationProfile(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    name = models.CharField(max_length=80, unique=True)
    description = models.CharField(max_length=1000, default="")
    is_active = models.BooleanField(default=False)
    version = models.IntegerField(default=1)
    definition = models.JSONField()
    created_at = models.DateTimeField(default=timezone.now)
    updated_at = models.DateTimeField(default=timezone.now)

    class Meta:
        constraints = [
            models.UniqueConstraint(
                fields=["is_active"], condition=Q(is_active=True), name="one_active_calibration"
            )
        ]


class InspectionRecord(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    product_id = models.UUIDField(db_index=True)
    recipe_id = models.UUIDField()
    captured_at = models.DateTimeField()
    processed_at = models.DateTimeField(db_index=True)
    product_class = models.CharField(max_length=64)
    color_class = models.CharField(max_length=64)
    size_class = models.CharField(max_length=64)
    defect_classes = models.JSONField(default=list)
    quality_status = models.CharField(max_length=32)
    confidence = models.FloatField()
    decision = models.CharField(max_length=32)
    destination_bin = models.CharField(max_length=64)
    model_name = models.CharField(max_length=80, null=True)
    model_version = models.CharField(max_length=80, null=True)
    processing_latency_ms = models.IntegerField(null=True)
    image_reference = models.CharField(max_length=300, null=True)
    is_simulation = models.BooleanField(default=False, db_index=True)
    result = models.JSONField()

    class Meta:
        indexes = [models.Index(fields=["recipe_id", "decision"])]


class ProductTrack(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    public_code = models.CharField(max_length=32, unique=True)
    recipe_id = models.UUIDField(null=True)
    inspection_id = models.UUIDField(null=True)
    stage = models.CharField(max_length=32, default="DETECTED")
    is_simulation = models.BooleanField(default=False, db_index=True)
    destination_bin = models.CharField(max_length=64, null=True)
    decision = models.CharField(max_length=32, null=True)
    failure_reason = models.CharField(max_length=300, null=True)
    detected_at = models.DateTimeField(default=timezone.now)
    captured_at = models.DateTimeField(null=True)
    inspecting_at = models.DateTimeField(null=True)
    decided_at = models.DateTimeField(null=True)
    queued_at = models.DateTimeField(null=True)
    sorted_at = models.DateTimeField(null=True)
    failed_at = models.DateTimeField(null=True)
    manual_review_at = models.DateTimeField(null=True)
    sort_due_at = models.DateTimeField(null=True)
    updated_at = models.DateTimeField(default=timezone.now)

    class Meta:
        indexes = [models.Index(fields=["stage", "sort_due_at"])]


class DatasetSession(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    recipe_id = models.UUIDField(null=True)
    recipe_name = models.CharField(max_length=80)
    status = models.CharField(max_length=20, default="ACTIVE", db_index=True)
    product_class = models.CharField(max_length=64)
    quality_label = models.CharField(max_length=64)
    color_label = models.CharField(max_length=64)
    size_label = models.CharField(max_length=64)
    defect_labels = models.JSONField(default=list)
    notes = models.CharField(max_length=1000, null=True)
    capture_mode = models.CharField(max_length=20)
    interval_ms = models.IntegerField(null=True)
    image_count = models.IntegerField(default=0)
    storage_path = models.CharField(max_length=300)
    started_at = models.DateTimeField(default=timezone.now, db_index=True)
    stopped_at = models.DateTimeField(null=True)
    is_simulation = models.BooleanField(default=False)

    class Meta:
        constraints = [
            models.UniqueConstraint(
                fields=["status"], condition=Q(status="ACTIVE"), name="one_active_dataset"
            )
        ]
