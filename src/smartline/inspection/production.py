"""Legacy barcode products and statistics remain distinct from inspections."""

import uuid

from django.conf import settings
from django.db import connection, transaction
from django.utils import timezone
from zoneinfo import ZoneInfo

from smartline.devices.service import alert, audit
from smartline.web.errors import ApiError
from smartline.web.events import bus
from smartline.web.serialization import fields
from .models import Product, Statistic

COLORS = ["RED", "BLUE", "GREEN", "YELLOW"]
STATUSES = ["OK", "DEFECTIVE", "NO_BARCODE", "WRONG_COLOR"]


def enum_value(raw, options, code):
    value = (raw or "").strip().upper()
    if value not in options:
        raise ApiError("Geçersiz değer", code)
    return value


def product_data(item):
    return fields(item, "id barcode color status passed_at")


def statistic_data(item):
    return fields(
        item,
        "id date hour total_products defective_products red_products blue_products green_products yellow_products",
        defectiveRate=round(100 * item.defective_products / item.total_products, 2)
        if item.total_products
        else 0,
    )


def _create(payload):
    barcode = payload.get("barcode")
    if not isinstance(barcode, str) or not barcode.strip() or len(barcode) > 50:
        raise ApiError("Barkod boş olamaz ve en fazla 50 karakter olabilir")
    color = payload.get("color")
    color = enum_value(color, COLORS, "PRODUCT_COLOR_INVALID") if color and color.strip() else None
    state = enum_value(payload.get("status", "OK"), STATUSES, "PRODUCT_STATUS_INVALID")
    if Product.objects.filter(barcode=barcode.strip()).exists():
        raise ApiError("Bu barkod daha önce okutulmuş", "PRODUCT_BARCODE_DUPLICATE", 409)
    return Product.objects.create(barcode=barcode.strip(), color=color, status=state)


def create_product(payload):
    with transaction.atomic():
        item = _create(payload)
        data = product_data(item)
        transaction.on_commit(lambda: bus.publish("ReceiveProductUpdate", data))
    return data


def barcode(payload):
    raw = payload.get("barcode") or ""
    color = payload.get("color")
    if (
        not isinstance(raw, str)
        or len(raw) > 50
        or color is not None
        and (not isinstance(color, str) or len(color) > 20)
    ):
        raise ApiError("Geçersiz barkod veya renk verisi")
    code = raw.strip()
    state = "OK" if code else "NO_BARCODE"
    with transaction.atomic():
        item = _create(
            {"barcode": code or "UNKNOWN_" + uuid.uuid4().hex, "color": color, "status": state}
        )
        if state == "NO_BARCODE":
            alert("NO_BARCODE_DETECTED")
            audit("NO_BARCODE_DETECTED", "Barkod okunamadı!", "Warning")
        else:
            audit("PRODUCT_PASSED", f"Ürün geçti: {code}, Renk: {color or ''}")
        increment(item.color, state)
        data = product_data(item)
        transaction.on_commit(lambda: bus.publish("ReceiveProductUpdate", data))
    return (
        "Barkod okunamadı — ürün kaydedildi (NO_BARCODE)" if not raw else f"Ürün kaydedildi: {raw}"
    )


def increment(color, state):
    now = timezone.now().astimezone(ZoneInfo(settings.TIME_ZONE))
    table = connection.ops.quote_name(Statistic._meta.db_table)
    with connection.cursor() as cursor:
        for hour in [None, now.hour]:
            cursor.execute(
                f"""INSERT INTO {table}
                (date,hour,total_products,defective_products,red_products,blue_products,green_products,yellow_products)
                VALUES (%s,%s,1,%s,%s,%s,%s,%s)
                ON CONFLICT (date,hour) DO UPDATE SET
                total_products={table}.total_products+1,
                defective_products={table}.defective_products+EXCLUDED.defective_products,
                red_products={table}.red_products+EXCLUDED.red_products,
                blue_products={table}.blue_products+EXCLUDED.blue_products,
                green_products={table}.green_products+EXCLUDED.green_products,
                yellow_products={table}.yellow_products+EXCLUDED.yellow_products""",
                [now.date(), hour, int(state != "OK"), *[int(color == c) for c in COLORS]],
            )
