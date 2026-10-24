"""MJPEG ingestion with reconnect. The opener is injected so tests do not need a camera."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from urllib.parse import urlsplit

from frame_codec import frame_size


@dataclass
class CameraStatus:
    online: bool
    url: str
    last_error: str | None
    frames_read: int
    consecutive_failures: int = 0
    frame_width: int | None = None
    frame_height: int | None = None
    last_frame_at: datetime | None = None
    stream_host: str = ""


def safe_stream_host(url: str) -> str:
    """Return host and port only. Userinfo and the path are not diagnostic data."""

    parts = urlsplit(url)
    if not parts.hostname:
        return ""
    if parts.port:
        return f"{parts.hostname}:{parts.port}"
    return parts.hostname


class CameraStream:
    def __init__(self, url: str, opener=None) -> None:
        self.url = url
        self._opener = opener or _opencv_open
        self._capture = None
        self.online = False
        self.last_error: str | None = None
        self.frames_read = 0
        self.consecutive_failures = 0
        self.frame_width: int | None = None
        self.frame_height: int | None = None
        self.last_frame_at: datetime | None = None

    def read(self):
        if self._capture is None:
            try:
                self._capture = self._opener(self.url)
            except Exception as exc:  # noqa: BLE001 - camera failures are operational, not programming errors
                self._fail(str(exc))
                return None
            if self._capture is None or not getattr(self._capture, "isOpened", lambda: True)():
                self._fail("camera stream did not open")
                self._capture = None
                return None

        ok, frame = self._capture.read()
        if not ok or frame is None:
            self._fail("camera frame was not available")
            self.close()
            return None
        width, height = frame_size(frame)
        self.online = True
        self.last_error = None
        self.frames_read += 1
        self.consecutive_failures = 0
        self.frame_width = width
        self.frame_height = height
        self.last_frame_at = datetime.now(timezone.utc)
        return frame

    def _fail(self, message: str) -> None:
        self.online = False
        self.last_error = message
        self.consecutive_failures += 1

    def status(self) -> CameraStatus:
        return CameraStatus(
            self.online,
            self.url,
            self.last_error,
            self.frames_read,
            self.consecutive_failures,
            self.frame_width,
            self.frame_height,
            self.last_frame_at,
            safe_stream_host(self.url),
        )

    def close(self) -> None:
        capture = self._capture
        self._capture = None
        if capture is not None and hasattr(capture, "release"):
            capture.release()


def _opencv_open(url: str):
    import cv2

    return cv2.VideoCapture(url)
