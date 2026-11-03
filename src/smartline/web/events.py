"""Process-local notifications for the supported single web worker."""

import asyncio
import json
import threading
from contextlib import asynccontextmanager

from django.http import StreamingHttpResponse

from .serialization import Encoder


class EventBus:
    def __init__(self):
        self._subscribers = set()
        self._lock = threading.Lock()

    def publish(self, name, value):
        message = (
            f"event: {name}\ndata: {json.dumps(value, cls=Encoder, allow_nan=False)}\n\n".encode()
        )
        with self._lock:
            subscribers = tuple(self._subscribers)
        for loop, queue in subscribers:
            if not loop.is_closed():
                loop.call_soon_threadsafe(queue.put_nowait, message)

    @asynccontextmanager
    async def subscribe(self):
        subscription = (asyncio.get_running_loop(), asyncio.Queue())
        with self._lock:
            self._subscribers.add(subscription)
        try:
            yield subscription[1]
        finally:
            with self._lock:
                self._subscribers.discard(subscription)


bus = EventBus()


async def events(request):
    async def stream():
        async with bus.subscribe() as queue:
            yield b"retry: 2000\nevent: ready\ndata: {}\n\n"
            from django.conf import settings

            if settings.MOCK:
                from .mock import fixture

                await asyncio.sleep(1)
                example = fixture()
                for index, product in enumerate(example["dashboard"]["recentProducts"][:3]):
                    payload = dict(product, id=100 + index, barcode=f"MOCK-RECENT-{index + 1}")
                    yield f"event: ReceiveProductUpdate\ndata: {json.dumps(payload)}\n\n".encode()
            while True:
                try:
                    yield await asyncio.wait_for(queue.get(), timeout=15)
                except TimeoutError:
                    yield b"event: heartbeat\ndata: {}\n\n"

    response = StreamingHttpResponse(stream(), content_type="text/event-stream")
    response["Cache-Control"] = "no-cache, no-transform"
    return response
