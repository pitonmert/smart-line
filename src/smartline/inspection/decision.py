"""Deterministic recipe decisions, independent of Django and image inference."""

import math
from decimal import Decimal, ROUND_HALF_UP


def normalize(value):
    return (value or "").strip().upper()


def match_size(classes, width):
    ordered = sorted(classes, key=lambda item: item["minWidthMm"])
    for index, size in enumerate(ordered):
        if width >= size["minWidthMm"] and (
            width <= size["maxWidthMm"] if index == len(ordered) - 1 else width < size["maxWidthMm"]
        ):
            return size["code"]
    return None


def yield_percent(passed, rejected):
    if passed < 0 or rejected < 0:
        raise ValueError("Counts must not be negative.")
    if passed + rejected == 0:
        return None
    return float(
        Decimal(str(100.0 * passed / (passed + rejected))).quantize(
            Decimal("0.1"), rounding=ROUND_HALF_UP
        )
    )


def allows_destination(quality, role):
    return (
        role == "HOLD"
        or role == "REJECT"
        and quality != "MANUAL_REVIEW"
        or role == "PASS"
        and quality == "GOOD"
    )


def decide(recipe, observation):
    confidence = observation.get("confidence", 0)
    if (
        not isinstance(confidence, (int, float))
        or not math.isfinite(confidence)
        or not 0 <= confidence <= 1
    ):
        raise ValueError("Confidence must be between 0 and 1.")
    for key in ("estimatedWidthMm", "estimatedHeightMm"):
        v = observation.get(key)
        if v is not None and (not isinstance(v, (int, float)) or not math.isfinite(v) or v < 0):
            raise ValueError("Physical dimensions must be finite and non-negative.")
    reasons = []
    product = next(
        (
            p
            for p in recipe["productClasses"]
            if p["code"] == normalize(observation.get("productClass"))
        ),
        None,
    )
    product_code = product["code"] if product else "UNKNOWN"
    if product:
        reasons.append("CLASS:" + product_code)
    color_code = normalize(observation.get("colorClass"))
    color_known = any(c["code"] == color_code for c in recipe["allowedColors"])
    if not color_known:
        if color_code:
            reasons.append("COLOR_NOT_IN_RECIPE")
        color_code = "UNKNOWN"
    width = observation.get("estimatedWidthMm")
    if width is not None:
        size = match_size(recipe["sizeClasses"], width) or "UNKNOWN"
        reasons.append("SIZE_FROM_VISUAL_WIDTH")
    else:
        size = normalize(observation.get("sizeClass"))
        if not any(s["code"] == size for s in recipe["sizeClasses"]):
            size = "UNKNOWN"
    defined_defects = {d["code"]: d for d in recipe["defectClasses"]}
    raw_defects = list(
        dict.fromkeys(normalize(d) for d in observation.get("defectClasses") or [] if normalize(d))
    )
    defects = {d for d in raw_defects if d in defined_defects}
    if defects:
        reasons.append("DEFECTS_PRESENT")
    if (
        product
        and color_known
        and normalize(product.get("expectedColor"))
        and normalize(product["expectedColor"]) != color_code
    ):
        reasons.append("COLOR_MISMATCH")
        if "WRONG_COLOR" in defined_defects:
            defects.add("WRONG_COLOR")
    if width is not None and size == "UNKNOWN":
        reasons.append("SIZE_OUT_OF_RANGE")
        if "WRONG_SIZE" in defined_defects:
            defects.add("WRONG_SIZE")
    unknown_defects = [d for d in raw_defects if d not in defined_defects]
    reasons.extend("UNKNOWN_DEFECT:" + d for d in unknown_defects)
    acceptance = recipe["acceptance"]
    reject_unknown = not product and acceptance.get("rejectUnknownClass", True)
    if reject_unknown:
        reasons.append("UNKNOWN_CLASS")
    limits = recipe.get("physicalLimits") or {}
    exceeded = False
    for key, maximum, reason in [
        ("estimatedWidthMm", "maxWidthMm", "PHYSICAL_WIDTH_EXCEEDED"),
        ("estimatedHeightMm", "maxHeightMm", "PHYSICAL_HEIGHT_EXCEEDED"),
    ]:
        if (
            observation.get(key) is not None
            and limits.get(maximum) is not None
            and observation[key] > limits[maximum]
        ):
            exceeded = True
            reasons.append(reason)
    low = confidence < max(
        acceptance.get("minimumConfidence", 0.6), limits.get("minConfidenceToSort") or 0
    )
    rejecting = (
        any(defined_defects[d].get("rejects", True) for d in defects)
        or bool(unknown_defects)
        and acceptance.get("rejectUnknownDefects", True)
        or reject_unknown
        or exceeded
    )
    if rejecting and (acceptance.get("defectsOverrideLowConfidence", True) or not low):
        quality = "DEFECTIVE"
    elif low:
        quality = "MANUAL_REVIEW"
        reasons.append("LOW_CONFIDENCE")
    elif not product:
        quality = "MANUAL_REVIEW"
    elif acceptance.get("requireKnownColor", False) and not color_known:
        quality = "MANUAL_REVIEW"
        reasons.append("COLOR_UNKNOWN")
    else:
        quality = "GOOD"
    if rejecting and low and not acceptance.get("defectsOverrideLowConfidence", True):
        quality = "MANUAL_REVIEW"
        reasons.append("LOW_CONFIDENCE")
    ordered_defects = [d["code"] for d in recipe["defectClasses"] if d["code"] in defects]
    bins = {b["code"]: b for b in recipe["destinationBins"]}

    def matches(rule):
        when = rule["when"]
        values = {
            "quality": quality,
            "productClass": product_code,
            "colorClass": color_code,
            "sizeClass": size,
        }
        return (
            all(not when.get(k) or when[k] == "ANY" or when[k] == v for k, v in values.items())
            and (
                not when.get("defectClass")
                or when["defectClass"] == "ANY"
                and bool(defects)
                or when.get("defectClass") in defects
            )
            and allows_destination(quality, bins[rule["destinationBin"]]["role"])
        )

    rule = next(
        (
            r
            for r in sorted(recipe["sortingRules"], key=lambda r: (r["priority"], r["id"]))
            if matches(r)
        ),
        None,
    )
    destination = bins[rule["destinationBin"] if rule else acceptance["defaultDestinationBin"]]
    if not allows_destination(quality, destination["role"]):
        holds = sorted((b for b in bins.values() if b["role"] == "HOLD"), key=lambda b: b["code"])
        if not holds:
            raise ValueError("No HOLD destination is defined for manual review.")
        destination = holds[0]
        reasons.append("DESTINATION_GUARD")
    reasons.append("RULE:" + rule["id"] if rule else "DEFAULT_DESTINATION")
    return {
        "productClass": product_code,
        "colorClass": color_code,
        "sizeClass": size,
        "defectClasses": ordered_defects,
        "qualityStatus": quality,
        "decision": {"PASS": "PASS", "REJECT": "REJECT", "HOLD": "MANUAL_REVIEW"}.get(
            destination["role"], "MANUAL_REVIEW"
        ),
        "destinationBin": destination["code"],
        "ruleId": rule["id"] if rule else None,
        "reasons": reasons,
        "simulation": bool(observation.get("simulation"))
        or (observation.get("triggerSource") or "").lower() == "simulator",
    }
