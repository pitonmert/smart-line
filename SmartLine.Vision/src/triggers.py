"""Inspection triggers.

manual, api, and simulator are available now.
A hardware trigger is the future VL53L1X product-arrival event and is rejected
unless the caller explicitly marks the request as simulation.
"""

from __future__ import annotations

from dataclasses import dataclass


ALLOWED = {"manual", "api", "simulator", "hardware"}


@dataclass
class TriggerRequest:
    source: str
    simulation: bool = False
    product_present: bool | None = None
    distance_mm: float | None = None
    trigger_timestamp: str | None = None
    sensor_healthy: bool | None = None
    product_class: str | None = None
    color_class: str | None = None
    size_class: str | None = None
    defect_classes: list[str] | None = None
    confidence: float | None = None

    def validate(self) -> None:
        source = self.source.strip().lower()
        if source not in ALLOWED:
            raise ValueError(f"Unsupported trigger source '{self.source}'.")
        self.source = source
        sensor_fields = [self.product_present, self.distance_mm, self.sensor_healthy]
        if source == "hardware" and not self.simulation:
            raise ValueError(
                "VL53L1X hardware trigger is not integrated. Use manual, api, or simulator."
            )
        if not self.simulation and any(field is not None for field in sensor_fields):
            raise ValueError("Sensor fields are only accepted on an explicit SIMULATION trigger.")
        if self.confidence is not None and not 0 <= self.confidence <= 1:
            raise ValueError("Trigger confidence must be between 0 and 1.")
