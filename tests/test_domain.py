from datetime import datetime, timedelta, timezone
from types import SimpleNamespace

import pytest

from smartline.inspection.decision import decide, match_size, yield_percent
from smartline.inspection.recipes import contract
from smartline.inspection.tracking import (
    TIMESTAMPS,
    actuation,
    advance,
    delay_milliseconds,
    sort_due_at,
)
from smartline.inspection.validation import (
    calibration_defaults,
    validate_calibration,
    validate_recipe,
)


@pytest.mark.parametrize(
    "case", contract("fixtures/decision-cases.json")["cases"], ids=lambda c: c["name"]
)
def test_shared_decision_cases(recipe, case):
    if case.get("physicalLimits") is not None:
        recipe["physicalLimits"] = case["physicalLimits"]
    result = decide(recipe, case["observation"])
    for key, value in case["expected"].items():
        assert result[key] == value
    assert set(case.get("expectedReasons", [])) <= set(result["reasons"])


def test_default_contracts_and_json_roundtrip(recipe):
    assert not validate_recipe(recipe)
    assert not validate_calibration(contract("calibration/uncalibrated.json"))
    import json

    result = decide(
        json.loads(json.dumps(recipe)),
        {
            "productClass": "BOX_GREEN",
            "colorClass": "GREEN",
            "sizeClass": "SMALL",
            "confidence": 0.9,
        },
    )
    assert json.loads(json.dumps(result))["decision"] == "PASS"


@pytest.mark.parametrize("value", [-0.1, 1.4, float("nan"), float("inf"), float("-inf")])
def test_invalid_confidence(recipe, value):
    with pytest.raises(ValueError):
        decide(recipe, {"confidence": value})


@pytest.mark.parametrize("key", ["estimatedWidthMm", "estimatedHeightMm"])
@pytest.mark.parametrize("value", [-1, float("nan"), float("inf"), float("-inf")])
def test_invalid_measurements(recipe, observation, key, value):
    observation[key] = value
    with pytest.raises(ValueError):
        decide(recipe, observation)


@pytest.mark.parametrize(
    "section",
    [
        "productClasses",
        "allowedColors",
        "sizeClasses",
        "defectClasses",
        "destinationBins",
        "sortingRules",
        "acceptance",
        "physicalLimits",
        "model",
        "cameraProfile",
        "roi",
        "inspection",
    ],
)
def test_null_recipe_sections(recipe, section):
    recipe[section] = None
    assert validate_recipe(recipe)


@pytest.mark.parametrize(
    "section",
    [
        "productClasses",
        "allowedColors",
        "sizeClasses",
        "defectClasses",
        "destinationBins",
        "sortingRules",
    ],
)
def test_null_recipe_items(recipe, section):
    recipe[section].append(None)
    assert validate_recipe(recipe)


@pytest.mark.parametrize(
    "section,key",
    [
        ("physicalLimits", "maxWidthMm"),
        ("physicalLimits", "maxHeightMm"),
        ("physicalLimits", "minConfidenceToSort"),
        ("roi", "x"),
        ("acceptance", "minimumConfidence"),
    ],
)
@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_non_finite_recipe_values(recipe, section, key, value):
    recipe[section][key] = value
    assert validate_recipe(recipe)


@pytest.mark.parametrize(
    "field,value",
    [
        ("quality", "EXCELLENT"),
        ("productClass", "MISSING_BOX"),
        ("colorClass", "PURPLE"),
        ("sizeClass", "EXTRA_LARGE"),
        ("defectClass", "SCRATCHED"),
    ],
)
def test_undefined_rule_references(recipe, field, value):
    recipe["sortingRules"][1]["when"][field] = value
    assert any("not defined" in e for e in validate_recipe(recipe))


@pytest.mark.parametrize(
    "section",
    ["productClasses", "allowedColors", "sizeClasses", "defectClasses", "destinationBins"],
)
def test_code_newline_rejected(recipe, section):
    recipe[section][0]["code"] += "\n"
    assert validate_recipe(recipe)


@pytest.mark.parametrize("value", [None, "", "ANY"])
def test_wildcard_conditions(recipe, value):
    recipe["sortingRules"][1]["when"] = {
        k: value for k in ["quality", "productClass", "colorClass", "sizeClass", "defectClass"]
    }
    assert not validate_recipe(recipe)


@pytest.mark.parametrize("scenario", ["low", "width", "height", "unknown", "defect"])
def test_unsafe_catch_all_cannot_pass(recipe, observation, scenario):
    recipe["sortingRules"].insert(
        0, {"id": "catch-all", "priority": 0, "when": {}, "destinationBin": "BIN_A"}
    )
    if scenario == "low":
        observation["confidence"] = 0.1
    if scenario == "width":
        observation["estimatedWidthMm"] = 251
    if scenario == "height":
        observation["estimatedHeightMm"] = 251
    if scenario == "unknown":
        observation["productClass"] = "MYSTERY"
    if scenario == "defect":
        observation["defectClasses"] = ["DAMAGED"]
    result = decide(recipe, observation)
    assert result["decision"] == ("MANUAL_REVIEW" if scenario == "low" else "REJECT")
    assert "RULE:catch-all" not in result["reasons"]


@pytest.mark.parametrize("low,default", [(False, "BIN_A"), (True, "BIN_A"), (True, "REJECT_BIN")])
def test_unsafe_default_uses_hold(recipe, observation, low, default):
    recipe["sortingRules"] = []
    recipe["acceptance"]["defaultDestinationBin"] = default
    observation.update(confidence=0.1 if low else 0.9, defectClasses=[] if low else ["DAMAGED"])
    result = decide(recipe, observation)
    assert result["decision"] == "MANUAL_REVIEW"
    assert "DESTINATION_GUARD" in result["reasons"]


def test_missing_safe_destination_and_unknown_references(recipe, observation):
    recipe["productClasses"][0]["expectedColor"] = "PURPLE"
    assert validate_recipe(recipe)
    recipe["sortingRules"] = []
    recipe["acceptance"]["defaultDestinationBin"] = "BIN_A"
    recipe["destinationBins"] = [b for b in recipe["destinationBins"] if b["role"] != "HOLD"]
    observation["confidence"] = 0.1
    with pytest.raises(ValueError):
        decide(recipe, observation)


def test_confidence_floor_and_size_boundaries(recipe, observation):
    recipe["physicalLimits"]["minConfidenceToSort"] = 0.2
    observation["confidence"] = 0.3
    assert decide(recipe, observation)["decision"] == "MANUAL_REVIEW"
    ordered = sorted(recipe["sizeClasses"], key=lambda s: s["minWidthMm"])
    for i, size in enumerate(ordered):
        assert match_size(ordered, size["minWidthMm"]) == size["code"]
        assert match_size(ordered, size["maxWidthMm"]) == (
            ordered[i + 1]["code"]
            if i < len(ordered) - 1 and ordered[i + 1]["minWidthMm"] == size["maxWidthMm"]
            else size["code"]
            if i == len(ordered) - 1
            else None
        )


@pytest.mark.parametrize(
    "key", ["pixelsPerMillimeter", "beltSpeedMmPerSecond", "inspectionToSorterDistanceMm"]
)
@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_non_finite_calibration(key, value):
    c = calibration_defaults({key: value})
    assert validate_calibration(c)


@pytest.mark.parametrize(
    "patch",
    [
        {"gateMappings": None},
        {"gateMappings": [None]},
        {"tofBaseline": None},
        {"hardwareActuationEnabled": True, "hardwareActuationConfirmed": True},
        {"orientationDegrees": 45},
        {"perspectiveReference": [{"x": 0, "y": 0}]},
        {"colorReference": {"red": 256, "green": 0, "blue": 0}},
        {"tofBaseline": {"source": "NOT_INTEGRATED", "distanceMm": 42}},
        {"tofBaseline": {"source": "HARDWARE"}},
        {"tofBaseline": {"source": "SIMULATION", "distanceMm": -1}},
    ],
)
def test_calibration_rejects_unsupported_data(patch):
    assert validate_calibration(calibration_defaults(patch))


@pytest.mark.parametrize("distance", [-1, 20001, float("nan"), float("inf"), float("-inf")])
def test_invalid_sort_distance(distance):
    with pytest.raises(ValueError):
        delay_milliseconds(distance, 200)


@pytest.mark.parametrize("speed", [-1, 0, 5001, float("nan"), float("inf"), float("-inf")])
def test_unknown_sort_speed(speed):
    assert delay_milliseconds(1000, speed) is None


@pytest.mark.parametrize("delay", [-1, float("nan"), float("inf"), float("-inf")])
def test_invalid_due_time(delay):
    with pytest.raises(ValueError):
        sort_due_at(datetime.now(timezone.utc), delay)


@pytest.mark.parametrize(
    "passed,rejected,expected",
    [
        (0, 0, None),
        (0, 1, 0),
        (1, 0, 100),
        (1, 1, 50),
        (2147483647, 2147483647, 50),
        (2147483647, 0, 100),
        (2, 1, 66.7),
        (1, 15, 6.3),
    ],
)
def test_production_yield(passed, rejected, expected):
    assert yield_percent(passed, rejected) == expected


@pytest.mark.parametrize("counts", [(-1, 0), (0, -1)])
def test_negative_counts(counts):
    with pytest.raises(ValueError):
        yield_percent(*counts)


@pytest.mark.parametrize(
    "source",
    [
        "DETECTED",
        "CAPTURED",
        "INSPECTING",
        "DECIDED",
        "QUEUED_FOR_SORT",
        "MANUAL_REVIEW",
        "SORTED",
        "FAILED",
    ],
)
@pytest.mark.parametrize(
    "target",
    [
        "DETECTED",
        "CAPTURED",
        "INSPECTING",
        "DECIDED",
        "QUEUED_FOR_SORT",
        "MANUAL_REVIEW",
        "SORTED",
        "FAILED",
    ],
)
def test_tracking_transition_matrix(source, target):
    now = datetime(2026, 10, 6, 9, tzinfo=timezone.utc)
    track = SimpleNamespace(
        stage=source,
        failure_reason=None,
        updated_at=now,
        **{k: now if k == "detected_at" else None for k in TIMESTAMPS.values()},
    )
    expected = {
        "DETECTED": {"CAPTURED", "FAILED"},
        "CAPTURED": {"INSPECTING", "FAILED"},
        "INSPECTING": {"DECIDED", "MANUAL_REVIEW", "FAILED"},
        "DECIDED": {"QUEUED_FOR_SORT", "MANUAL_REVIEW", "FAILED"},
        "QUEUED_FOR_SORT": {"SORTED", "FAILED"},
        "MANUAL_REVIEW": {"DECIDED", "FAILED"},
        "SORTED": set(),
        "FAILED": set(),
    }
    if target in expected[source]:
        advance(track, target, now)
        assert track.stage == target
    else:
        with pytest.raises(ValueError):
            advance(track, target, now)
        assert track.stage == source


def test_tracking_time_and_manual_review():
    now = datetime(2026, 10, 6, 9, tzinfo=timezone.utc)
    track = SimpleNamespace(
        stage="DETECTED",
        failure_reason=None,
        updated_at=now,
        **{k: now if k == "detected_at" else None for k in TIMESTAMPS.values()},
    )
    with pytest.raises(ValueError):
        advance(track, "CAPTURED", now - timedelta(seconds=1))
    assert track.stage == "DETECTED"
    for i, stage in enumerate(["CAPTURED", "INSPECTING", "DECIDED", "MANUAL_REVIEW", "DECIDED"]):
        advance(track, stage, now + timedelta(seconds=i), "review")
    assert track.failure_reason is None
    assert track.decided_at == now + timedelta(seconds=4)
    with pytest.raises(ValueError):
        advance(track, "QUEUED_FOR_SORT", now + timedelta(seconds=3))
    assert actuation(False)["hardwareCommandIssued"] is False
    assert actuation(True)["actuation"] == "not_wired"
    assert delay_milliseconds(20000, 5000) == 4000
