import json
from datetime import datetime, timezone as datetime_timezone
from importlib.resources import files

from django.utils import timezone

from smartline.web.errors import ApiError, missing
from smartline.web.serialization import fields
from .models import CalibrationProfile, ProductRecipe
from .transactions import inspection_transaction
from .validation import calibration_defaults, recipe_defaults, validate_calibration, validate_recipe


def contract(path):
    return json.loads(files("smartline.inspection").joinpath("contracts", path).read_text())


def seed():
    from smartline.devices.models import ConveyorSetting

    with inspection_transaction():
        ConveyorSetting.objects.get_or_create(
            pk=1, defaults={"last_updated": datetime(2026, 8, 9, tzinfo=datetime_timezone.utc)}
        )
        if not ProductRecipe.objects.filter(name="DemoBox").exists():
            definition = contract("recipes/demobox.json")
            ProductRecipe.objects.create(
                name="DemoBox",
                description=definition["description"],
                definition=definition,
                is_active=not ProductRecipe.objects.filter(is_active=True).exists(),
            )
        if not CalibrationProfile.objects.filter(name="Uncalibrated baseline").exists():
            definition = contract("calibration/uncalibrated.json")
            CalibrationProfile.objects.create(
                name="Uncalibrated baseline",
                description=definition.get("notes") or "No physical calibration has been measured.",
                definition=definition,
            )


def data(item):
    result = fields(item, "id name description is_active version definition updated_at")
    if isinstance(item, CalibrationProfile):
        result["simulationDataPresent"] = (
            item.definition.get("tofBaseline", {}).get("source") == "SIMULATION"
        )
    return result


def required(model, identifier=None):
    item = (
        model.objects.filter(pk=identifier).first()
        if identifier
        else model.objects.filter(is_active=True).first()
    )
    if item is None:
        missing("Calibration profile" if model is CalibrationProfile else "Product recipe")
    return item


def calibration_for(definition):
    identifier = definition.get("calibrationProfileId")
    if identifier:
        return required(CalibrationProfile, identifier)
    return CalibrationProfile.objects.filter(is_active=True).first()


def save(payload, identifier=None, calibration=False):
    model = CalibrationProfile if calibration else ProductRecipe
    code = "CALIBRATION_INVALID" if calibration else "RECIPE_INVALID"
    if not isinstance(payload.get("definition"), dict):
        raise ApiError("Definition is required.", code)
    definition = (
        calibration_defaults(payload["definition"])
        if calibration
        else recipe_defaults(payload["definition"])
    )
    name = (payload.get("name") or "").strip()
    if calibration:
        name = name or "Calibration"
    else:
        definition["name"] = name or definition["name"]
        if payload.get("description") is not None:
            definition["description"] = payload["description"]
        name = definition["name"]
    errors = validate_calibration(definition) if calibration else validate_recipe(definition)
    if len(name) > 80 or len(payload.get("description") or "") > 1000:
        errors.append("Name or description is too long.")
    if errors:
        raise ApiError(" ".join(errors), code)
    with inspection_transaction():
        query = model.objects.filter(name=name)
        if identifier:
            query = query.exclude(pk=identifier)
        if query.exists():
            raise ApiError("A definition with this name already exists.", code, 409)
        if (
            not calibration
            and definition.get("calibrationProfileId")
            and not CalibrationProfile.objects.filter(
                pk=definition["calibrationProfileId"]
            ).exists()
        ):
            raise ApiError("Referenced calibration profile does not exist.", code)
        item = required(model, identifier) if identifier else model()
        if identifier and item.is_active and not calibration:
            from .vision_gateway import validate_model_recipe

            validate_model_recipe(definition)
        item.name = name
        item.description = (
            payload.get("description")
            if payload.get("description") is not None
            else (
                item.description
                if identifier and calibration
                else definition.get("notes", "")
                if calibration
                else definition["description"]
            )
        )
        item.definition = definition
        if identifier:
            item.version += 1
        item.updated_at = timezone.now()
        item.save()
    return data(item)


def activate(identifier, calibration=False):
    model = CalibrationProfile if calibration else ProductRecipe
    with inspection_transaction():
        item = required(model, identifier)
        if not calibration:
            from .vision_gateway import validate_model_recipe

            validate_model_recipe(item.definition)
        model.objects.filter(is_active=True).update(is_active=False)
        item.is_active = True
        item.updated_at = timezone.now()
        item.save()
    return data(item)
