"""Private HTTP adapter to the image process; no image decoding in the web process."""

import json
import socket
import threading
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from django.conf import settings

from smartline.web.errors import ApiError
from smartline.web.serialization import Encoder
from .models import InspectionRecord, ProductRecipe, ProductTrack
from .recipes import calibration_for, required
from .tracking import actuation
from .workflow import submit, track_data

inspection_gate = threading.Lock()


def call(path, payload=None, timeout=None, binary=False):
    if not settings.VISION_URL:
        raise ApiError("Vision is not configured.", "VISION_UNAVAILABLE", 503)
    request = Request(
        settings.VISION_URL + path,
        data=json.dumps(payload, cls=Encoder).encode() if payload is not None else None,
        headers={"Content-Type": "application/json"} if payload is not None else {},
    )
    try:
        with urlopen(request, timeout=timeout or settings.VISION_CAPTURE_TIMEOUT) as response:
            body = response.read(1_000_001)
            if len(body) > 1_000_000:
                raise ApiError("Vision result is too large.", "VISION_INVALID", 503)
            return (body, response.headers) if binary else json.loads(body)
    except HTTPError as error:
        raise ApiError(
            "Vision inspection failed. Check model, recipe and camera status.",
            "VISION_UNAVAILABLE",
            409 if error.code == 409 else 503,
        ) from error
    except (TimeoutError, socket.timeout) as error:
        raise ApiError(
            "Vision inspection exceeded its time budget.", "VISION_TIMEOUT", 504
        ) from error
    except URLError as error:
        if isinstance(error.reason, (TimeoutError, socket.timeout)):
            raise ApiError(
                "Vision inspection exceeded its time budget.", "VISION_TIMEOUT", 504
            ) from error
        raise ApiError("Vision cannot be reached.", "VISION_UNAVAILABLE", 503) from error
    except (json.JSONDecodeError, UnicodeError) as error:
        raise ApiError("Vision returned an invalid result.", "VISION_INVALID", 503) from error


def validate_model_recipe(definition):
    if not definition.get("model", {}).get("artifactFileName"):
        return
    try:
        call("/validate-recipe", {"recipe": {"definition": definition}})
    except ApiError as error:
        if error.status == 409:
            raise ApiError(
                "Recipe does not match the configured detection artifact. Restart Vision with the matching artifact.",
                "RECIPE_INVALID",
                409,
            ) from error
        raise


def frame(role):
    try:
        body, headers = call("/frames/" + role, binary=True)
    except ApiError as error:
        raise ApiError(str(error), "DATASET_INVALID", error.status) from error
    if (
        not body
        or len(body) > 900000
        or not body.startswith(b"\xff\xd8\xff")
        or not body.endswith(b"\xff\xd9")
    ):
        raise ApiError("Vision must return a JPEG frame.", "VISION_INVALID", 503)

    def header(*names, default=None):
        return next((headers[n] for n in names if headers.get(n) is not None), default)

    def dimension(*names):
        value = header(*names)
        if value is None:
            return None
        number = int(value)
        if number <= 0:
            raise ApiError("Vision frame dimensions are invalid.", "VISION_INVALID", 503)
        return number

    return {
        "jpeg": body,
        "capturedAt": header("X-Captured-At"),
        "width": dimension("X-Frame-Width", "X-Image-Width"),
        "height": dimension("X-Frame-Height", "X-Image-Height"),
        "role": header("X-Frame-Role", default=role),
        "origin": header(
            "X-Frame-Origin", default="still_capture" if role != "preview" else "live_stream"
        ),
        "captureSource": header(
            "X-Capture-Source", default="esp32_capture" if role != "preview" else "mjpeg_preview"
        ),
        "previewFallback": header("X-Preview-Fallback", default="false").lower() == "true",
    }


def inspect(request_id):
    try:
        identifier = uuid.UUID(str(request_id))
        if identifier.int == 0:
            raise ValueError()
    except (ValueError, TypeError, AttributeError) as error:
        raise ApiError("RequestId is required.") from error
    with inspection_gate:
        previous = InspectionRecord.objects.filter(pk=identifier).first()
        if previous:
            return {
                "inspection": previous.result,
                "track": track_data(ProductTrack.objects.get(pk=previous.product_id)),
                "actuation": actuation(settings.HARDWARE_ACTUATION_ENABLED),
            }, False
        recipe = required(ProductRecipe)
        profile = calibration_for(recipe.definition)
        if profile and profile.definition.get("tofBaseline", {}).get("source") == "SIMULATION":
            raise ApiError(
                "Real inspection cannot use simulated sensor calibration.",
                "CALIBRATION_INVALID",
                409,
            )
        expected = (
            recipe.version,
            profile.id if profile else None,
            profile.version if profile else None,
            profile.definition if profile else None,
        )
        response = call(
            "/inspect",
            {
                "source": "api",
                "recipe": {
                    "id": recipe.id,
                    "version": recipe.version,
                    "definition": recipe.definition,
                },
                "calibration": profile.definition if profile else None,
            },
        )
        try:
            if (
                str(response["recipeId"]) != str(recipe.id)
                or response["recipeVersion"] != recipe.version
            ):
                raise ApiError(
                    "Vision returned a different recipe snapshot.", "RECIPE_INVALID", 409
                )
            if not response["inference"]["available"]:
                raise ApiError("Inference did not run.", "VISION_UNAVAILABLE", 503)
            observation = response["observation"]
            if not isinstance(observation, dict):
                raise TypeError()
            observation.update(
                recipeId=str(recipe.id),
                recipeVersion=recipe.version,
                triggerSource="api",
                simulation=False,
                capturedAt=response["frame"]["capturedAt"],
            )
        except (KeyError, TypeError) as error:
            raise ApiError(
                "Vision returned an incomplete result.", "VISION_INVALID", 503
            ) from error
        return submit(observation, model_source=True, request_id=identifier, expected=expected)
