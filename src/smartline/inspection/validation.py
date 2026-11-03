"""Recipe and calibration validation ported from the existing contracts."""

import copy
import math
import re


CODE = re.compile(r"[A-Z][A-Z0-9_]{0,40}")


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def recipe_defaults(value):
    result = {
        "name": "",
        "description": "",
        "productClasses": [],
        "allowedColors": [],
        "sizeClasses": [],
        "defectClasses": [],
        "sortingRules": [],
        "destinationBins": [],
        "acceptance": {
            "minimumConfidence": 0.6,
            "rejectUnknownClass": True,
            "rejectUnknownDefects": True,
            "defectsOverrideLowConfidence": True,
            "requireKnownColor": False,
            "defaultDestinationBin": "MANUAL_REVIEW",
        },
        "model": {"task": "detect", "family": "YOLO26", "name": "yolo26n", "inputSize": 640},
        "cameraProfile": {},
        "roi": {"x": 0, "y": 0, "width": 1, "height": 1},
        "physicalLimits": {},
        "inspection": {
            "triggerModes": [],
            "selectionStrategy": "latest_frame_on_trigger",
            "continuousInference": False,
        },
    }
    for key, item in value.items():
        if isinstance(result.get(key), dict) and isinstance(item, dict):
            result[key].update(copy.deepcopy(item))
        else:
            result[key] = copy.deepcopy(item)
    for key, defaults in {
        "productClasses": {"code": "", "displayName": ""},
        "allowedColors": {
            "code": "",
            "displayName": "",
            "hueMin": 0,
            "hueMax": 0,
            "saturationMin": 0,
            "valueMin": 0,
        },
        "sizeClasses": {"code": "", "displayName": "", "minWidthMm": 0, "maxWidthMm": 0},
        "defectClasses": {"code": "", "displayName": "", "rejects": True},
        "destinationBins": {"code": "", "displayName": "", "role": "HOLD"},
        "sortingRules": {"id": "", "priority": 0, "when": {}, "destinationBin": ""},
    }.items():
        if isinstance(result[key], list):
            result[key] = [
                dict(copy.deepcopy(defaults), **item) if isinstance(item, dict) else item
                for item in result[key]
            ]
    return result


def validate_recipe(recipe):
    errors = []
    collections = [
        "productClasses",
        "allowedColors",
        "sizeClasses",
        "defectClasses",
        "destinationBins",
        "sortingRules",
    ]
    sections = ["acceptance", "physicalLimits", "model", "roi", "cameraProfile", "inspection"]
    if (
        not isinstance(recipe, dict)
        or any(not isinstance(recipe.get(k), list) for k in collections)
        or any(not isinstance(recipe.get(k), dict) for k in sections)
        or not isinstance(recipe["inspection"].get("triggerModes"), list)
    ):
        return ["Recipe sections and collections must not be null."]
    if any(not isinstance(item, dict) for k in collections for item in recipe[k]) or any(
        not isinstance(r.get("when"), dict) for r in recipe["sortingRules"]
    ):
        return ["Recipe collections must not contain null entries or rule conditions."]
    name = recipe.get("name")
    if not isinstance(name, str) or not name.strip() or len(name.strip()) > 80:
        errors.append("Recipe name is required and must be at most 80 characters.")
    if not isinstance(recipe.get("description"), str):
        errors.append("Recipe description must not be null.")
    model = recipe["model"]
    if model.get("artifactFileName") is not None and not (model.get("version") or "").strip():
        errors.append("A model artifact requires its version.")
    if (
        len(recipe.get("description") or "") > 1000
        or len(model.get("name") or "") > 80
        or len(model.get("version") or "") > 80
    ):
        errors.append("Recipe description or model metadata is too long.")
    codes = {}
    for key, label in zip(
        collections[:5], ["product class", "color", "size class", "defect class", "destination bin"]
    ):
        seen = set()
        for item in recipe[key]:
            code = item.get("code")
            if not isinstance(code, str) or not CODE.fullmatch(code):
                errors.append(
                    f"{label} code '{code}' must be uppercase letters, digits, or underscores."
                )
            elif code in seen:
                errors.append(f"Duplicate {label} code '{code}'.")
            if isinstance(code, str):
                seen.add(code)
        codes[key] = seen
    if not recipe["productClasses"]:
        errors.append("A recipe needs at least one product class.")
    if not recipe["destinationBins"]:
        errors.append("A recipe needs at least one destination bin.")
    for color in recipe["allowedColors"]:
        if any(not finite(color.get(k)) or not 0 <= color[k] <= 360 for k in ["hueMin", "hueMax"]):
            errors.append(f"Color {color.get('code')} hue must stay between 0 and 360.")
        if any(
            not finite(color.get(k)) or not 0 <= color[k] <= 1
            for k in ["saturationMin", "valueMin"]
        ):
            errors.append(
                f"Color {color.get('code')} saturation and value minimums must stay between 0 and 1."
            )
    sizes = sorted(
        recipe["sizeClasses"], key=lambda s: s["minWidthMm"] if finite(s.get("minWidthMm")) else 0
    )
    for i, size in enumerate(sizes):
        a, b = size.get("minWidthMm"), size.get("maxWidthMm")
        if not finite(a) or not finite(b) or a < 0 or b <= a:
            errors.append(f"Size class {size.get('code')} needs a positive width range.")
        if (
            i
            and finite(a)
            and finite(sizes[i - 1].get("maxWidthMm"))
            and a < sizes[i - 1]["maxWidthMm"]
        ):
            errors.append(f"Size class {size.get('code')} overlaps {sizes[i - 1].get('code')}.")
    for dest in recipe["destinationBins"]:
        if dest.get("role") not in ["PASS", "REJECT", "HOLD"]:
            errors.append(
                f"Destination {dest.get('code')} has an unknown role '{dest.get('role')}'."
            )
    acceptance = recipe["acceptance"]
    if acceptance.get("defaultDestinationBin") not in codes["destinationBins"]:
        errors.append("The default destination bin is not defined.")
    if not any(b.get("role") == "HOLD" for b in recipe["destinationBins"]):
        errors.append("A recipe needs a HOLD destination for products requiring manual review.")
    if (
        not finite(acceptance.get("minimumConfidence"))
        or not 0 <= acceptance["minimumConfidence"] <= 1
    ):
        errors.append("Minimum confidence must be between 0 and 1.")
    minimum = recipe["physicalLimits"].get("minConfidenceToSort")
    if minimum is not None and (not finite(minimum) or not 0 <= minimum <= 1):
        errors.append("Physical minimum confidence must be between 0 and 1.")
    priorities, ids = set(), set()
    fields = {
        "quality": ({"GOOD", "DEFECTIVE", "MANUAL_REVIEW"}, False),
        "productClass": (codes["productClasses"], True),
        "colorClass": (codes["allowedColors"], True),
        "sizeClass": (codes["sizeClasses"], True),
        "defectClass": (codes["defectClasses"], False),
    }
    for rule in recipe["sortingRules"]:
        if not isinstance(rule.get("id"), str) or not rule["id"].strip() or rule["id"] in ids:
            errors.append("Every sorting rule needs a unique id.")
        ids.add(rule.get("id"))
        if rule.get("priority") in priorities:
            errors.append(f"Sorting rule priority {rule.get('priority')} is duplicated.")
        priorities.add(rule.get("priority"))
        if rule.get("destinationBin") not in codes["destinationBins"]:
            errors.append(f"Sorting rule {rule.get('id')} points at an unknown bin.")
        for field, (defined, unknown) in fields.items():
            value = rule["when"].get(field)
            if value is None or isinstance(value, str) and (not value.strip() or value == "ANY"):
                continue
            if not isinstance(value, str) or not CODE.fullmatch(value):
                errors.append(f"Sorting rule {field} '{value}' is not a valid code.")
            elif value not in defined and not (unknown and value == "UNKNOWN"):
                errors.append(f"Sorting rule {field} '{value}' is not defined in the recipe.")
    if model.get("task") != "detect":
        errors.append("Only detection models are supported in v0.2.0.")
    if not finite(model.get("inputSize")) or not 32 <= model["inputSize"] <= 4096:
        errors.append("Model input size is outside 32..4096.")
    if not (model.get("name") or "").strip():
        errors.append("Model name is required even when weights are not loaded.")
    roi = recipe["roi"]
    if (
        any(not finite(roi.get(k)) for k in ("x", "y", "width", "height"))
        or roi.get("x", -1) < 0
        or roi.get("y", -1) < 0
        or roi.get("width", 0) <= 0
        or roi.get("height", 0) <= 0
        or roi["x"] + roi["width"] > 1
        or roi["y"] + roi["height"] > 1
    ):
        errors.append("ROI must be a normalized rectangle inside the frame.")
    for k in ["sourceWidth", "sourceHeight"]:
        v = recipe["cameraProfile"].get(k)
        if v is not None and (not finite(v) or v <= 0):
            errors.append("Camera source dimensions must be positive when they are set.")
    for k in ["maxWidthMm", "maxHeightMm"]:
        v = recipe["physicalLimits"].get(k)
        if v is not None and (not finite(v) or v <= 0):
            errors.append("Physical limits must be positive when they are set.")
    for mode in recipe["inspection"]["triggerModes"]:
        if mode not in ["manual", "api", "simulator", "hardware"]:
            errors.append(f"Trigger mode '{mode}' is not supported.")
    if recipe["inspection"].get("continuousInference"):
        errors.append(
            "Continuous inference is not part of the event-triggered inspection pipeline."
        )
    for product in recipe["productClasses"]:
        expected = product.get("expectedColor")
        if expected is not None and (not isinstance(expected, str) or not CODE.fullmatch(expected)):
            errors.append(f"Expected color for {product.get('code')} is not a valid code.")
        elif expected is not None and expected not in codes["allowedColors"]:
            errors.append(f"Expected color for {product.get('code')} is not defined in the recipe.")
    return errors


def calibration_defaults(value):
    result = {
        "gateMappings": [],
        "tofBaseline": {"source": "NOT_INTEGRATED"},
        "orientationDegrees": 0,
        "flipHorizontal": False,
        "flipVertical": False,
        "hardwareActuationEnabled": False,
        "hardwareActuationConfirmed": False,
    }
    result.update(copy.deepcopy(value))
    return result


def validate_calibration(c):
    errors = []
    if not isinstance(c, dict):
        return ["Calibration is required."]
    if not isinstance(c.get("gateMappings"), list) or not isinstance(c.get("tofBaseline"), dict):
        return ["Gate mappings and time-of-flight baseline are required."]
    if len(c.get("notes") or "") > 1000:
        errors.append("Notes exceed 1000 characters.")
    if c.get("pixelsPerMillimeter") is not None and any(
        not finite(c.get(k)) or not 0 < c[k] <= 10000 for k in ["sourceWidth", "sourceHeight"]
    ):
        errors.append("Scale calibration requires source image dimensions.")
    if c.get("orientationDegrees", 0) not in (0, 90, 180, 270):
        errors.append("Camera orientation must be 0, 90, 180, or 270 degrees.")
    for k, low, high, label in [
        ("pixelsPerMillimeter", 0, 100, "Pixels per millimetre"),
        ("beltSpeedMmPerSecond", 0, 5000, "Belt speed"),
        ("inspectionToSorterDistanceMm", 0, 20000, "Inspection-to-sorter distance"),
    ]:
        v = c.get(k)
        if v is not None and (
            not finite(v) or v < low or k != "inspectionToSorterDistanceMm" and v == 0 or v > high
        ):
            errors.append(f"{label} is outside its supported range.")
    for key in ["cameraRoi", "inspectionZone"]:
        rect = c.get(key)
        if rect is not None and (
            not isinstance(rect, dict)
            or any(not finite(rect.get(k)) for k in ["x", "y", "width", "height"])
            or rect["x"] < 0
            or rect["y"] < 0
            or rect["width"] <= 0
            or rect["height"] <= 0
            or any(rect[k] > 10000 for k in ["x", "y", "width", "height"])
        ):
            errors.append(f"{key} must sit inside a positive pixel rectangle within 10000 pixels.")
    points = c.get("perspectiveReference")
    if points is not None:
        if not isinstance(points, list) or len(points) not in [0, 4]:
            errors.append("Perspective reference must contain exactly four points when it is set.")
        elif any(
            not isinstance(p, dict) or not finite(p.get("x")) or not finite(p.get("y"))
            for p in points
        ):
            errors.append("Perspective points must be finite.")
    color = c.get("colorReference")
    if color is not None and (
        not isinstance(color, dict)
        or any(
            not finite(color.get(k)) or not 0 <= color[k] <= 255 for k in ["red", "green", "blue"]
        )
    ):
        errors.append("Color reference channels must stay between 0 and 255.")
    bins = set()
    for gate in c["gateMappings"]:
        if not isinstance(gate, dict):
            errors.append("Gate mapping cannot be null.")
            continue
        code = gate.get("binCode")
        if not isinstance(code, str) or not code.strip() or len(code) > 41 or code in bins:
            errors.append("Gate mappings need unique destination bins.")
        bins.add(code)
        if not finite(gate.get("servoDegrees", 0)) or not 0 <= gate.get("servoDegrees", 0) <= 180:
            errors.append(f"Servo angle for {code} must stay between 0 and 180 degrees.")
        if gate.get("enabled") and not c.get("hardwareActuationEnabled"):
            errors.append(f"Gate {code} cannot be enabled while hardware actuation is off.")
    if c.get("hardwareActuationEnabled") or any(
        isinstance(g, dict) and g.get("enabled") for g in c["gateMappings"]
    ):
        errors.append("Hardware actuation is not integrated.")
    if c.get("hardwareActuationEnabled") and not c.get("hardwareActuationConfirmed"):
        errors.append("Hardware actuation requires an explicit confirmation flag.")
    tof = c["tofBaseline"]
    source = tof.get("source", "NOT_INTEGRATED")
    if source == "NOT_INTEGRATED" and any(
        tof.get(k) is not None
        for k in ["productPresent", "distanceMm", "triggerTimestamp", "sensorHealthy"]
    ):
        errors.append("VL53L1X is not integrated. The baseline cannot contain sensor readings.")
    elif (
        source == "SIMULATION"
        and tof.get("distanceMm") is not None
        and (not finite(tof["distanceMm"]) or tof["distanceMm"] < 0)
    ):
        errors.append("A simulated distance cannot be negative.")
    elif source not in ["NOT_INTEGRATED", "SIMULATION"]:
        errors.append(
            "Time-of-flight source must be NOT_INTEGRATED or SIMULATION. Hardware readings are deferred."
        )
    return errors
