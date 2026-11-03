import asyncio
import logging
import os

os.environ.setdefault("DJANGO_SETTINGS_MODULE", "smartline.config.settings")

from django.core.asgi import get_asgi_application

django_application = get_asgi_application()
log = logging.getLogger(__name__)


def initialize():
    from django.conf import settings
    from django.core.management import call_command
    from django.db import close_old_connections
    from smartline.inspection.recipes import seed
    from smartline.inspection.datasets import recover
    from smartline.web.errors import ApiError

    if settings.MOCK:
        return
    try:
        call_command("migrate", interactive=False, verbosity=0)
        seed()
        try:
            recover()
        except ApiError:
            log.exception(
                "Dataset recovery is unavailable; other application functions remain enabled"
            )
        if os.getenv("MQTT_ENABLED", "true").lower() == "true":
            from smartline.devices.mqtt import transport

            transport.start()
    finally:
        close_old_connections()


async def application(scope, receive, send):
    if scope["type"] != "lifespan":
        return await django_application(scope, receive, send)
    while True:
        message = await receive()
        if message["type"] == "lifespan.startup":
            try:
                await asyncio.to_thread(initialize)
            except Exception as error:
                log.exception("Application initialization failed")
                await send({"type": "lifespan.startup.failed", "message": str(error)})
                return
            await send({"type": "lifespan.startup.complete"})
        elif message["type"] == "lifespan.shutdown":
            from smartline.devices.mqtt import transport

            await asyncio.to_thread(transport.stop)
            await send({"type": "lifespan.shutdown.complete"})
            return
