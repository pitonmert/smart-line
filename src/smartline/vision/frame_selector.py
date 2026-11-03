"""Keep the newest live preview and any still offered for one role.

Preview always comes from the 240x176 MJPEG frame. Inspection and dataset
select a still that was offered for that role. They do not silently reuse the
preview. ``use_preview_fallback`` is the only path that does, and only when the
caller has explicitly allowed it.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from threading import Lock

from smartline.vision.frame_codec import frame_size


ROLE_PREVIEW = "preview"
ROLE_INSPECTION = "inspection"
ROLE_DATASET = "dataset"
ROLES = (ROLE_PREVIEW, ROLE_INSPECTION, ROLE_DATASET)
ORIGIN_LIVE = "live_stream"
ORIGIN_STILL = "still_capture"


@dataclass
class SelectedFrame:
    frame: object
    captured_at: datetime
    sequence: int
    role: str
    origin: str
    width: int | None
    height: int | None
    preview_fallback: bool = False
    capture_source: str = "mjpeg_preview"


class FrameSelector:
    def __init__(self) -> None:
        self._lock = Lock()
        self._latest: SelectedFrame | None = None
        self._stills: dict[str, SelectedFrame] = {}
        self._served: dict[str, SelectedFrame] = {}
        self._sequence = 0
        self.observed_count = 0

    def observe(
        self,
        frame,
        captured_at: datetime | None = None,
        width: int | None = None,
        height: int | None = None,
    ) -> None:
        inferred_width, inferred_height = frame_size(frame)
        with self._lock:
            self._sequence += 1
            self.observed_count += 1
            self._latest = SelectedFrame(
                frame=frame,
                captured_at=captured_at or datetime.now(timezone.utc),
                sequence=self._sequence,
                role=ROLE_PREVIEW,
                origin=ORIGIN_LIVE,
                width=width if width is not None else inferred_width,
                height=height if height is not None else inferred_height,
            )

    def offer_still(
        self,
        role: str,
        frame,
        captured_at: datetime | None = None,
        capture_source: str = "esp32_capture",
    ) -> None:
        """Reserve a higher-quality still for one role. The live preview is unchanged."""

        if role not in (ROLE_INSPECTION, ROLE_DATASET):
            raise ValueError("A still frame can only be offered for inspection or dataset capture.")
        width, height = frame_size(frame)
        with self._lock:
            self._sequence += 1
            self._stills[role] = SelectedFrame(
                frame=frame,
                captured_at=captured_at or datetime.now(timezone.utc),
                sequence=self._sequence,
                role=role,
                origin=ORIGIN_STILL,
                width=width,
                height=height,
                capture_source=capture_source or "esp32_capture",
            )

    def select(self, role: str) -> SelectedFrame | None:
        if role not in ROLES:
            raise ValueError(f"Unknown frame role '{role}'.")
        with self._lock:
            if role == ROLE_PREVIEW:
                chosen = self._latest
                fallback = False
                source = "mjpeg_preview"
            else:
                chosen = self._stills.get(role)
                fallback = False
                source = None if chosen is None else chosen.capture_source
            if chosen is None:
                return None
            served = self._copy(chosen, role, chosen.origin, fallback, source or "esp32_capture")
            self._served[role] = served
            return served

    def use_preview_fallback(self, role: str) -> SelectedFrame | None:
        """Serve the latest preview under another role and say that this happened."""

        if role not in (ROLE_INSPECTION, ROLE_DATASET):
            raise ValueError("Preview fallback is only defined for inspection and dataset capture.")
        with self._lock:
            if self._latest is None:
                return None
            served = self._copy(self._latest, role, ORIGIN_LIVE, True, "mjpeg_preview")
            self._served[role] = served
            return served

    def _copy(
        self, chosen: SelectedFrame, role: str, origin: str, fallback: bool, source: str
    ) -> SelectedFrame:
        return SelectedFrame(
            frame=chosen.frame,
            captured_at=chosen.captured_at,
            sequence=chosen.sequence,
            role=role,
            origin=origin,
            width=chosen.width,
            height=chosen.height,
            preview_fallback=fallback,
            capture_source=source,
        )

    def select_for_inspection(self) -> SelectedFrame | None:
        return self.select(ROLE_INSPECTION)

    def latest(self) -> SelectedFrame | None:
        with self._lock:
            return self._latest

    def role_status(self) -> dict:
        with self._lock:
            report = {}
            for role in ROLES:
                served = self._served.get(role)
                report[role] = (
                    None
                    if served is None
                    else {
                        "sequence": served.sequence,
                        "capturedAt": served.captured_at.isoformat(),
                        "origin": served.origin,
                        "width": served.width,
                        "height": served.height,
                        "captureSource": served.capture_source,
                        "previewFallback": served.preview_fallback,
                    }
                )
            return report

    @property
    def has_frame(self) -> bool:
        with self._lock:
            return self._latest is not None or bool(self._stills)
