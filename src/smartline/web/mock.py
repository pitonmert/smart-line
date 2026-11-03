import copy
import json
from functools import lru_cache
from pathlib import Path

from django.http import HttpResponse

from .http import failure, success


@lru_cache
def fixture():
    return json.loads((Path(__file__).parent / "static/mock-data.json").read_text())


def respond(request):
    data = fixture()
    if request.method != "GET":
        return failure(
            "Mock preview is read-only. No operation was performed.", "MOCK_READ_ONLY", 409
        )
    path = request.path
    if path == "/api/camera/preview" or "/images/mock-" in path:
        return HttpResponse(data["camera"], content_type="image/svg+xml")
    room = {
        "activeRecipe": data["recipe"]["name"],
        "currentProduct": data["queue"][0],
        "lastInspection": data["inspections"][0],
        "totalCount": 2480,
        "acceptedCount": 2360,
        "rejectedCount": 90,
        "manualReviewCount": 30,
        "yieldPercent": 96.3,
        "binCounts": [
            {"destinationBin": "BIN_A", "count": 2360},
            {"destinationBin": "REJECT_BIN", "count": 90},
            {"destinationBin": "MANUAL_REVIEW", "count": 30},
        ],
        "recentEvents": [
            {
                "createdAt": data["inspections"][0]["processedAt"],
                "eventType": "MOCK_PREVIEW",
                "message": "Example production data loaded.",
                "severity": "INFO",
            }
        ],
        "simulationActive": False,
    }
    reads = {
        "/api/dashboard": data["dashboard"],
        "/api/diagnostics": data["diagnostics"],
        "/api/control-room": room,
        "/api/recipes": [data["recipe"]],
        "/api/recipes/active": data["recipe"],
        "/api/recipes/active/snapshot": data["recipe"],
        "/api/calibration-profiles": [data["calibration"]],
        "/api/tracking/queue": data["queue"],
        "/api/tracking": data["queue"],
        "/api/simulation/status": data["simulation"],
        "/api/dataset/sessions": [data["dataset"]],
        "/api/dataset/sessions/active": data["dataset"],
        "/api/conveyor/status": data["dashboard"]["conveyorStatus"],
        "/api/sensors/status": data["dashboard"]["conveyorStatus"],
        "/api/statistics/hourly": [
            dict(
                data["statistics"],
                id=i + 1,
                hour=i + 8,
                totalProducts=180 + i * 30,
                defectiveProducts=5 + i,
            )
            for i in range(8)
        ],
        "/api/statistics/daily": [data["statistics"]],
    }
    if path == "/api/inspections":
        items = copy.deepcopy(data["inspections"])
        for key in ["decision", "productClass", "simulation"]:
            if request.GET.get(key):
                items = [r for r in items if str(r[key]).lower() == request.GET[key].lower()]
        if request.GET.get("q"):
            items = [r for r in items if request.GET["q"].lower() in json.dumps(r).lower()]
        page = max(1, int(request.GET.get("pageNumber", 1)))
        size = max(1, min(100, int(request.GET.get("pageSize", 20))))
        start = (page - 1) * size
        return success(
            {
                "items": items[start : start + size],
                "pageNumber": page,
                "pageSize": size,
                "hasNextPage": start + size < len(items),
                "hasPreviousPage": page > 1,
            }
        )
    return (
        success(reads[path])
        if path in reads
        else failure("No mock data exists for this route.", "RESOURCE_NOT_FOUND", 404)
    )
