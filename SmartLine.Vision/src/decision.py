"""Recipe decision engine.

This module matches SmartLine.API/Domain/Inspection/DecisionEngine.cs.
Sorting rules live in the Product Recipe. A detector does not choose a bin.
"""

from __future__ import annotations

import math

UNKNOWN = "UNKNOWN"
GOOD = "GOOD"
DEFECTIVE = "DEFECTIVE"
MANUAL_REVIEW = "MANUAL_REVIEW"
PASS = "PASS"
REJECT = "REJECT"
HOLD = "HOLD"


def decide(recipe: dict, observation: dict) -> dict:
    confidence = float(observation.get("confidence", 0))
    if confidence != confidence or confidence < 0 or confidence > 1:
        raise ValueError("Confidence must be between 0 and 1.")
    # Negatif, sonsuz veya sayı olmayan ölçümlerle ürün kararı üretmeyelim.
    for name in ("estimatedWidthMm", "estimatedHeightMm"):
        value = observation.get(name)
        if value is not None and (not math.isfinite(float(value)) or float(value) < 0):
            raise ValueError("Physical dimensions must be finite and non-negative.")

    reasons: list[str] = []
    product = _product(recipe, observation.get("productClass"), reasons)
    color = _color(recipe, observation.get("colorClass"), reasons)
    size = _size(recipe, observation, reasons)
    defects = _defects(recipe, observation.get("defectClasses") or [], reasons)

    if (
        product["known"]
        and color["known"]
        and product["expected"]
        and product["expected"] != color["code"]
    ):
        reasons.append("COLOR_MISMATCH")
        _add_known(recipe, defects, "WRONG_COLOR")

    if observation.get("estimatedWidthMm") is not None and size["code"] == UNKNOWN:
        reasons.append("SIZE_OUT_OF_RANGE")
        _add_known(recipe, defects, "WRONG_SIZE")

    unknown = []
    seen = set()
    known_codes = {item["code"] for item in recipe.get("defectClasses", [])}
    for raw in observation.get("defectClasses") or []:
        code = _norm(raw)
        if code and code not in known_codes and code not in seen:
            seen.add(code)
            unknown.append(code)
            reasons.append(f"UNKNOWN_DEFECT:{code}")

    by_code = {item["code"]: item for item in recipe.get("defectClasses", [])}
    rejecting_defect = any(by_code[code].get("rejects", True) for code in defects)
    acceptance = recipe.get("acceptance") or {}
    reject_unknown_defect = bool(unknown) and acceptance.get(
        "rejectUnknownDefects", True
    )
    reject_unknown_class = (not product["known"]) and acceptance.get(
        "rejectUnknownClass", True
    )
    if reject_unknown_class:
        reasons.append("UNKNOWN_CLASS")

    # Boyut sınıfına uyması yeterli değildir; fiziksel üst sınırlar da kontrol edilir.
    limits = recipe.get("physicalLimits") or {}
    width = observation.get("estimatedWidthMm")
    height = observation.get("estimatedHeightMm")
    max_width = limits.get("maxWidthMm")
    max_height = limits.get("maxHeightMm")
    width_exceeded = (
        width is not None and max_width is not None and float(width) > float(max_width)
    )
    height_exceeded = (
        height is not None
        and max_height is not None
        and float(height) > float(max_height)
    )
    if width_exceeded:
        reasons.append("PHYSICAL_WIDTH_EXCEEDED")
    if height_exceeded:
        reasons.append("PHYSICAL_HEIGHT_EXCEEDED")

    # API ile aynı şekilde, iki güven eşiğinden daha yüksek olanını kullanırız.
    minimum = max(
        float(acceptance.get("minimumConfidence", 0.6)),
        float(limits.get("minConfidenceToSort") or 0),
    )
    low_confidence = confidence < minimum
    rejecting = (
        rejecting_defect
        or reject_unknown_defect
        or reject_unknown_class
        or width_exceeded
        or height_exceeded
    )
    override = acceptance.get("defectsOverrideLowConfidence", True)
    if rejecting and (override or not low_confidence):
        quality = DEFECTIVE
    elif low_confidence:
        quality = MANUAL_REVIEW
        reasons.append("LOW_CONFIDENCE")
    elif not product["known"]:
        quality = MANUAL_REVIEW
    elif acceptance.get("requireKnownColor", False) and not color["known"]:
        quality = MANUAL_REVIEW
        reasons.append("COLOR_UNKNOWN")
    else:
        quality = GOOD

    if rejecting and low_confidence and not override:
        quality = MANUAL_REVIEW
        reasons.append("LOW_CONFIDENCE")

    ordered = [
        item["code"]
        for item in recipe.get("defectClasses", [])
        if item["code"] in defects
    ]
    # Kuralın koşulları tutsa bile hedef bölme ürünün değerlendirmesine uygun olmalı.
    rule = next(
        (
            item
            for item in sorted(
                recipe.get("sortingRules", []),
                key=lambda rule: (rule["priority"], rule["id"]),
            )
            if _matches(
                item.get("when") or {},
                quality,
                product["code"],
                color["code"],
                size["code"],
                ordered,
            )
            and any(
                destination["code"] == item["destinationBin"]
                and _allows_destination(quality, destination.get("role", HOLD))
                for destination in recipe.get("destinationBins", [])
            )
        ),
        None,
    )
    bin_code = (
        rule["destinationBin"]
        if rule
        else acceptance.get("defaultDestinationBin", MANUAL_REVIEW)
    )
    destination = next(
        item for item in recipe.get("destinationBins", []) if item["code"] == bin_code
    )
    if not _allows_destination(quality, destination.get("role", HOLD)):
        # Uygun olmayan varsayılan hedef yerine ürünü insan kontrolü için bekletiriz.
        # API ile aynı bölmeyi seçmek için kod sırasını kullanırız.
        destination = next(
            (
                item
                for item in sorted(
                    recipe.get("destinationBins", []), key=lambda item: item["code"]
                )
                if item.get("role", HOLD) == HOLD
            ),
            None,
        )
        if destination is None:
            raise ValueError("No HOLD destination is defined for manual review.")
        reasons.append("DESTINATION_GUARD")
    if rule is None:
        reasons.append("DEFAULT_DESTINATION")
    else:
        reasons.append(f"RULE:{rule['id']}")

    trigger = str(observation.get("triggerSource") or "")
    simulation = bool(observation.get("simulation")) or trigger.lower() == "simulator"
    return {
        "productClass": product["code"],
        "colorClass": color["code"],
        "sizeClass": size["code"],
        "defectClasses": ordered,
        "qualityStatus": quality,
        "decision": _decision_for_role(destination.get("role", HOLD)),
        "destinationBin": destination["code"],
        "ruleId": None if rule is None else rule["id"],
        "reasons": reasons,
        "simulation": simulation,
    }


def match_size_class(size_classes: list[dict], width_mm: float) -> str | None:
    ordered = sorted(size_classes, key=lambda item: item["minWidthMm"])
    for index, size in enumerate(ordered):
        last = index == len(ordered) - 1
        upper = (
            width_mm <= size["maxWidthMm"] if last else width_mm < size["maxWidthMm"]
        )
        if width_mm >= size["minWidthMm"] and upper:
            return size["code"]
    return None


def _allows_destination(quality: str, role: str) -> bool:
    # Sağlam ürün bölmesine yalnızca GOOD; kontrol bekleyen ürünler ise yalnızca HOLD'a gider.
    return (
        role == HOLD
        or (role == REJECT and quality != MANUAL_REVIEW)
        or (role == PASS and quality == GOOD)
    )


def _decision_for_role(role: str) -> str:
    if role == PASS:
        return PASS
    if role == REJECT:
        return REJECT
    return MANUAL_REVIEW


def _product(recipe: dict, raw, reasons: list[str]) -> dict:
    code = _norm(raw)
    match = next(
        (item for item in recipe.get("productClasses", []) if item["code"] == code),
        None,
    )
    if match is None:
        return {"code": UNKNOWN, "known": False, "expected": None}
    reasons.append(f"CLASS:{match['code']}")
    expected = _norm(match.get("expectedColor"))
    return {"code": match["code"], "known": True, "expected": expected or None}


def _color(recipe: dict, raw, reasons: list[str]) -> dict:
    code = _norm(raw)
    if any(item["code"] == code for item in recipe.get("allowedColors", [])):
        return {"code": code, "known": True}
    if code:
        reasons.append("COLOR_NOT_IN_RECIPE")
    return {"code": UNKNOWN, "known": False}


def _size(recipe: dict, observation: dict, reasons: list[str]) -> dict:
    if observation.get("estimatedWidthMm") is not None:
        measured = match_size_class(
            recipe.get("sizeClasses", []), float(observation["estimatedWidthMm"])
        )
        reasons.append("SIZE_FROM_VISUAL_WIDTH")
        if measured is None:
            return {"code": UNKNOWN, "known": False}
        return {"code": measured, "known": True}
    claimed = _norm(observation.get("sizeClass"))
    if any(item["code"] == claimed for item in recipe.get("sizeClasses", [])):
        return {"code": claimed, "known": True}
    return {"code": UNKNOWN, "known": False}


def _defects(recipe: dict, raw, reasons: list[str]) -> set[str]:
    known = {item["code"] for item in recipe.get("defectClasses", [])}
    found = {_norm(item) for item in raw}
    found = {item for item in found if item in known}
    if found:
        reasons.append("DEFECTS_PRESENT")
    return found


def _add_known(recipe: dict, defects: set[str], code: str) -> None:
    if any(item["code"] == code for item in recipe.get("defectClasses", [])):
        defects.add(code)


def _matches(
    when: dict, quality: str, product: str, color: str, size: str, defects: list[str]
) -> bool:
    if not _field(when.get("quality"), quality):
        return False
    if not _field(when.get("productClass"), product):
        return False
    if not _field(when.get("colorClass"), color):
        return False
    if not _field(when.get("sizeClass"), size):
        return False
    defect = when.get("defectClass")
    if defect is None or str(defect).strip() == "":
        return True
    if defect == "ANY":
        return len(defects) > 0
    return defect in defects


def _field(expected, actual: str) -> bool:
    if expected is None or str(expected).strip() == "" or expected == "ANY":
        return True
    return str(expected) == actual


def _norm(value) -> str:
    if value is None:
        return ""
    return str(value).strip().upper()
