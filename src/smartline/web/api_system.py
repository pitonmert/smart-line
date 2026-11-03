from datetime import date, timedelta

from smartline.devices import service
from smartline.devices.models import Alert, SystemLog
from smartline.inspection.models import Product, Statistic
from smartline.inspection import production
from . import monitoring
from .errors import ApiError, missing
from .http import api, body, success
from .serialization import page


@api(("GET", "POST"))
def conveyor(request, action):
    if action == "status" and request.method == "GET":
        return service.status()
    if request.method == "POST" and action != "status":
        return service.control(action, body(request) if request.body else {})
    missing()


@api(("GET", "POST"))
def sensors(request, action):
    if action == "status" and request.method == "GET":
        return service.status()
    if request.method == "POST":
        payload = body(request)
        if action == "environment":
            return service.environment(payload)
        if action == "fire":
            service.fire(payload)
            return (
                "YANGIN ALARMI! Acil durdurma latch'i etkin."
                if payload["isDetected"]
                else "Yangın sensörü temiz; latch yönetici resetini bekliyor"
            )
        if action == "barcode":
            return production.barcode(payload)
    missing()


@api(("GET", "POST"))
def products(request, identifier=None, filter_kind=None, value=None):
    if request.method == "POST" and not identifier and not filter_kind:
        result = production.create_product(body(request))
        return success(result, "Ürün başarıyla oluşturuldu", 201, f"/api/products/{result['id']}")
    query = Product.objects.all()
    if identifier or filter_kind == "barcode":
        item = (
            query.filter(pk=identifier).first()
            if identifier
            else query.filter(barcode=value).first()
        )
        if item is None:
            missing("Product")
        return production.product_data(item)
    if filter_kind == "defective":
        query = query.exclude(status="OK")
    elif filter_kind == "status":
        query = query.filter(
            status=production.enum_value(value, production.STATUSES, "PRODUCT_STATUS_INVALID")
        )
    elif filter_kind == "color":
        query = query.filter(
            color=production.enum_value(value, production.COLORS, "PRODUCT_COLOR_INVALID")
        )
    return page(query.order_by("-passed_at", "-id"), request.GET, production.product_data)


@api(("GET", "POST"))
def alerts(request, identifier=None, action=None, value=None):
    if request.method == "POST" and identifier and action == "resolve":
        return service.resolve_alert(identifier)
    query = Alert.objects.all()
    if identifier:
        item = query.filter(pk=identifier).first()
        if item is None:
            missing("Alert")
        return service.alert_data(item)
    if action == "active":
        query = query.filter(is_active=True)
    elif action == "type":
        kind = production.enum_value(
            value,
            ["FIRE", "EMERGENCY_STOP", "HIGH_TEMPERATURE", "NO_BARCODE_DETECTED"],
            "REQUEST_INVALID",
        )
        query = query.filter(alert_type=kind)
    return page(query.order_by("-triggered_at", "-id"), request.GET, service.alert_data, 20)


@api()
def logs(request, action=None, value=None):
    query = SystemLog.objects.all()
    if action == "severity":
        levels = {v.lower(): v for v in ["Info", "Warning", "Error", "Critical"]}
        if value.lower() not in levels:
            raise ApiError("Geçersiz log seviyesi")
        query = query.filter(severity=levels[value.lower()])
    elif action == "event":
        from .vocabulary import EVENTS

        query = query.filter(event_type=production.enum_value(value, EVENTS, "REQUEST_INVALID"))
    return page(query.order_by("-created_at", "-id"), request.GET, service.log_data, 20)


@api()
def statistics(request, mode):
    today = monitoring.today()
    if mode == "hourly":
        day = date.fromisoformat(request.GET["date"]) if request.GET.get("date") else today
        query = Statistic.objects.filter(date=day, hour__isnull=False).order_by("hour", "id")[:24]
    else:
        start = (
            date.fromisoformat(request.GET["startDate"])
            if request.GET.get("startDate")
            else today - timedelta(days=7)
        )
        end = date.fromisoformat(request.GET["endDate"]) if request.GET.get("endDate") else today
        if start > end or (end - start).days > 365:
            raise ApiError("İstatistik tarih aralığı en fazla 366 gün olabilir")
        query = Statistic.objects.filter(date__gte=start, date__lte=end, hour=None).order_by(
            "date", "id"
        )
    return [production.statistic_data(s) for s in query]


@api()
def summary(request, kind):
    return {
        "dashboard": monitoring.dashboard,
        "diagnostics": monitoring.diagnostics,
        "control-room": monitoring.control_room,
    }[kind]()
