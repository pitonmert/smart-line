from django.db.models import F, Q
from django.http import HttpResponse
from django.utils import timezone

from smartline.inspection import datasets, recipes, vision_gateway, workflow
from smartline.inspection.models import (
    CalibrationProfile,
    DatasetSession,
    InspectionRecord,
    ProductRecipe,
    ProductTrack,
)
from smartline.inspection.tracking import actuation
from smartline.web.errors import missing
from .http import api, body, boolean, success
from .serialization import page


@api(("GET", "POST", "PUT"))
def definitions(request, kind, identifier=None, action=None):
    calibration = kind == "calibration"
    model = CalibrationProfile if calibration else ProductRecipe
    if request.method == "GET":
        if action in ["active", "snapshot"]:
            return recipes.data(recipes.required(model))
        if identifier:
            return recipes.data(recipes.required(model, identifier))
        return [recipes.data(r) for r in model.objects.order_by("name")]
    if action == "activate" and request.method == "POST":
        return recipes.activate(identifier, calibration)
    if (
        action
        or request.method == "PUT"
        and not identifier
        or request.method == "POST"
        and identifier
    ):
        missing()
    result = recipes.save(body(request), identifier, calibration)
    return success(
        result,
        status=200 if identifier else 201,
        location=None
        if identifier
        else f"/api/{'calibration-profiles' if calibration else 'recipes'}/{result['id']}",
    )


@api(("GET", "POST"))
def inspections(request, identifier=None, vision=False):
    if request.method == "POST":
        payload = body(request)
        result, created = (
            vision_gateway.inspect(payload.get("requestId")) if vision else workflow.submit(payload)
        )
        return success(
            result,
            status=201 if created else 200,
            location=f"/api/inspections/{result['inspection']['inspectionId']}"
            if created
            else None,
        )
    query = InspectionRecord.objects.all()
    if identifier:
        result = query.filter(pk=identifier).first()
        if result is None:
            missing("Inspection")
        return result.result
    params = request.GET
    for key, field in [("decision", "decision"), ("productClass", "product_class")]:
        if params.get(key, "").strip():
            query = query.filter(**{field: params[key].strip().upper()})
    if params.get("recipeId"):
        query = query.filter(recipe_id=params["recipeId"])
    if params.get("simulation") is not None:
        query = query.filter(is_simulation=boolean(params["simulation"]))
    for key, field in [("from", "processed_at__gte"), ("to", "processed_at__lte")]:
        if params.get(key):
            query = query.filter(**{field: workflow.timestamp(params[key])})
    if params.get("q", "").strip():
        term = params["q"].strip()
        ids = ProductTrack.objects.filter(public_code__contains=term).values("id")
        query = query.filter(
            Q(product_class__contains=term)
            | Q(destination_bin__contains=term)
            | Q(decision__contains=term)
            | Q(product_id__in=ids)
        )
    return page(query.order_by("-processed_at", "-id"), params, lambda r: r.result, 20)


@api()
def camera(request):
    image = vision_gateway.frame("preview")
    return HttpResponse(image["jpeg"], content_type="image/jpeg")


@api(("GET", "POST"))
def tracking(request, identifier=None, queue=False):
    if request.method == "POST":
        return workflow.release(identifier)
    query = ProductTrack.objects.all()
    if queue:
        query = query.filter(stage="QUEUED_FOR_SORT")
        query = query.order_by(F("sort_due_at").asc(nulls_last=True), "queued_at")
    else:
        if boolean(request.GET.get("activeOnly")):
            query = query.exclude(stage__in=["SORTED", "FAILED"])
        query = query.order_by(F("sort_due_at").asc(nulls_last=True), "detected_at")[:100]
    return [workflow.track_data(t) for t in query]


@api(("GET", "POST"))
def simulation(request, action):
    if action == "status" and request.method == "GET":
        return workflow.simulation_status()
    if request.method != "POST":
        missing()
    if action == "clear":
        workflow.simulation_latch.clear()
        return "cleared"
    workflow.ensure_simulation()
    payload = body(request)
    if action == "arrivals":
        workflow.simulation_latch.set()
        payload["triggerSource"] = "simulator"
        result, _ = workflow.submit(payload, simulation=True)
        return success(
            result,
            "SIMULATION arrival recorded.",
            201,
            f"/api/inspections/{result['inspection']['inspectionId']}",
        )
    if action == "tick":
        now = workflow.timestamp(payload.get("utcNow")) or timezone.now()
        from smartline.inspection.transactions import inspection_transaction

        with inspection_transaction():
            due = list(
                ProductTrack.objects.filter(
                    is_simulation=True, stage="QUEUED_FOR_SORT", sort_due_at__lte=now
                ).values_list("id", flat=True)
            )
            for identifier in due:
                workflow.release(identifier, now)
        return {"sortedCount": len(due), "actuation": actuation()}
    missing()


@api(("GET", "POST", "DELETE"))
def dataset(request, identifier=None, action=None, sample_id=None, file_name=None):
    if request.method == "GET":
        if file_name:
            image, content_type = datasets.read_image(identifier, file_name)
            return HttpResponse(image, content_type=content_type)
        if action == "active":
            session = DatasetSession.objects.filter(status="ACTIVE").first()
            if session is None:
                missing("Active dataset session")
            return datasets.response(session)
        if identifier:
            return datasets.response(datasets.session_required(identifier))
        return [
            datasets.response(s)
            for s in DatasetSession.objects.order_by("-status", "-started_at")[:50]
        ]
    if request.method == "DELETE" and sample_id:
        return datasets.discard(identifier, sample_id)
    if request.method == "POST":
        if action == "stop":
            return datasets.stop(identifier)
        if action == "capture":
            payload = body(request)
            image = vision_gateway.frame("dataset")
            return datasets.capture(identifier, payload, image)
        if identifier is None and action is None:
            result = datasets.start(body(request))
            return success(
                result,
                "Dataset session started.",
                201,
                f"/api/dataset/sessions/{result['sessionId']}",
            )
    missing()
