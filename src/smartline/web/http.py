import json
import logging
import re
from functools import wraps
from pathlib import Path

from asgiref.sync import iscoroutinefunction
from django.core.exceptions import ValidationError
from django.db import DatabaseError, IntegrityError
from django.http import HttpResponse, JsonResponse
from django.utils.decorators import sync_and_async_middleware

from .errors import ApiError
from .serialization import Encoder

log = logging.getLogger(__name__)
_contracts = json.loads(Path(__file__).with_name("http_contracts.json").read_text())
_route_methods = []
for _contract in _contracts:
    _pattern = _contract["path"]
    _pattern = re.sub(
        r"\{\w+:guid\}",
        r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        _pattern,
    )
    _pattern = re.sub(r"\{\w+:int\}", r"-?[0-9]+", _pattern)
    _pattern = re.sub(r"\{\w+\}", r"[^/]+", _pattern)
    _route_methods.append((re.compile(_pattern), set(_contract["methods"])))


def success(data, message="İşlem başarılı", status=200, location=None):
    response = JsonResponse(
        {"isSuccess": True, "message": message, "data": data},
        encoder=Encoder,
        status=status,
        json_dumps_params={"allow_nan": False},
    )
    if location:
        response["Location"] = location
    return response


def failure(message, code, status):
    return JsonResponse(
        {"isSuccess": False, "message": message, "data": None, "code": code}, status=status
    )


def api(methods=("GET",)):
    def decorate(view):
        @wraps(view)
        def dispatch(request, *args, **kwargs):
            allowed = set().union(
                *(allowed for pattern, allowed in _route_methods if pattern.fullmatch(request.path))
            )
            if not allowed:
                return not_found(request)
            if request.method not in allowed:
                return failure("İstek işlenemedi", "REQUEST_INVALID", 405)
            try:
                from django.conf import settings

                if settings.MOCK:
                    from .mock import respond

                    return respond(request)
                result = view(request, *args, **kwargs)
                return result if isinstance(result, HttpResponse) else success(result)
            except ApiError as error:
                return failure(str(error), error.code, error.status)
            except (ValueError, TypeError, KeyError, ValidationError, UnicodeError) as error:
                return failure(
                    str(error) or "İstek gövdesi veya parametreleri geçersiz",
                    "REQUEST_INVALID",
                    400,
                )
            except IntegrityError as error:
                cause = error.__cause__
                name = getattr(getattr(cause, "diag", None), "constraint_name", "") or ""
                code = "PRODUCT_BARCODE_DUPLICATE" if "barcode" in name else "DATABASE_CONFLICT"
                return failure("Veritabanı kısıtlaması ihlal edildi", code, 409)
            except DatabaseError:
                log.exception("Database operation failed")
                return failure("Veritabanı işlemi tamamlanamadı", "DATABASE_CONFLICT", 409)
            except Exception:
                log.exception("Request failed")
                return failure("Beklenmeyen bir hata oluştu", "UNEXPECTED_ERROR", 500)

        return dispatch

    return decorate


def body(request):
    if request.content_type != "application/json":
        raise ApiError("Content-Type must be application/json.")
    value = json.loads(
        request.body,
        parse_constant=lambda v: (_ for _ in ()).throw(ValueError("JSON numbers must be finite")),
    )
    if not isinstance(value, dict):
        raise ApiError("Request body must be an object.")
    # Match the previous case-insensitive request binding without changing wire output.
    names = {
        n.lower(): n
        for n in [
            "requestId",
            "recipeId",
            "productId",
            "recipeVersion",
            "capturedAt",
            "productClass",
            "colorClass",
            "sizeClass",
            "defectClasses",
            "confidence",
            "visualWidth",
            "visualHeight",
            "estimatedWidthMm",
            "estimatedHeightMm",
            "simulation",
            "boundingBoxes",
            "sourceWidth",
            "sourceHeight",
            "modelName",
            "modelVersion",
            "processingLatencyMs",
            "cameraSource",
            "imageReference",
            "triggerSource",
            "frameRole",
            "captureSource",
            "previewFallback",
            "isDetected",
            "deviceEpoch",
            "fireGeneration",
            "temperature",
            "humidity",
            "speed",
            "barcode",
            "color",
            "status",
            "name",
            "description",
            "definition",
            "qualityLabel",
            "colorLabel",
            "sizeLabel",
            "defectLabels",
            "notes",
            "captureMode",
            "intervalMs",
            "distanceMm",
            "beltSpeedMmPerSecond",
            "utcNow",
        ]
    }
    value = {names.get(k.lower(), k): v for k, v in value.items()}
    strings = "requestId recipeId productId capturedAt productClass colorClass sizeClass modelName modelVersion cameraSource imageReference triggerSource frameRole captureSource deviceEpoch barcode color status name description qualityLabel colorLabel sizeLabel notes captureMode utcNow".split()
    integers = "speed recipeVersion processingLatencyMs sourceWidth sourceHeight intervalMs fireGeneration".split()
    numbers = "confidence visualWidth visualHeight estimatedWidthMm estimatedHeightMm temperature humidity distanceMm beltSpeedMmPerSecond".split()
    booleans = "simulation previewFallback isDetected".split()
    for keys, valid in [
        (strings, lambda v: isinstance(v, str)),
        (integers, lambda v: isinstance(v, int) and not isinstance(v, bool)),
        (numbers, lambda v: isinstance(v, (int, float)) and not isinstance(v, bool)),
        (booleans, lambda v: isinstance(v, bool)),
    ]:
        if any(value.get(k) is not None and not valid(value[k]) for k in keys):
            raise ApiError("Request body contains an invalid field type.")
    for key in ["defectClasses", "defectLabels"]:
        if value.get(key) is not None and (
            not isinstance(value[key], list)
            or any(v is not None and not isinstance(v, str) for v in value[key])
        ):
            raise ApiError("Request body contains an invalid label collection.")
    return value


def boolean(value):
    if value is None:
        return None
    if str(value).lower() not in ["true", "false"]:
        raise ApiError("Boolean query parameter is invalid.")
    return str(value).lower() == "true"


def not_found(request, exception=None):
    return failure("Kaynak bulunamadı", "RESOURCE_NOT_FOUND", 404)


def server_error(request):
    return failure("Beklenmeyen bir hata oluştu", "UNEXPECTED_ERROR", 500)


@sync_and_async_middleware
def ResponseMiddleware(get_response):
    def mock_response(request):
        from django.conf import settings

        if settings.MOCK and request.path.startswith("/api/") and request.path != "/api/events":
            from .mock import respond

            return respond(request)
        return None

    def headers(response):
        response["X-Frame-Options"] = "DENY"
        response["X-Content-Type-Options"] = "nosniff"
        response["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains"
        response["Referrer-Policy"] = "strict-origin-when-cross-origin"
        return response

    if iscoroutinefunction(get_response):

        async def middleware(request):
            response = mock_response(request)
            return headers(response if response is not None else await get_response(request))
    else:

        def middleware(request):
            response = mock_response(request)
            return headers(response if response is not None else get_response(request))

    return middleware
