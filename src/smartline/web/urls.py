from django.http import HttpResponse, HttpResponseRedirect
from django.urls import path, re_path

from . import api_inspection as inspection, api_system as system, pages
from .events import events
from .http import not_found, server_error

handler404 = not_found
handler500 = server_error

urlpatterns = [
    path("health", lambda request: HttpResponse("Healthy", content_type="text/plain")),
    path("api/events", events),
    path("api/camera/preview", inspection.camera),
    path("api/inspections/vision", inspection.inspections, {"vision": True}),
    path("api/inspections", inspection.inspections),
    path("api/inspections/<uuid:identifier>", inspection.inspections),
    path("api/tracking", inspection.tracking),
    path("api/tracking/queue", inspection.tracking, {"queue": True}),
    path("api/tracking/<uuid:identifier>/release", inspection.tracking),
    path("api/simulation/<str:action>", inspection.simulation),
    path("api/dataset/sessions", inspection.dataset),
    path("api/dataset/sessions/active", inspection.dataset, {"action": "active"}),
    path("api/dataset/sessions/<uuid:identifier>", inspection.dataset),
    path("api/dataset/sessions/<uuid:identifier>/stop", inspection.dataset, {"action": "stop"}),
    path(
        "api/dataset/sessions/<uuid:identifier>/captures/camera",
        inspection.dataset,
        {"action": "capture"},
    ),
    path("api/dataset/sessions/<uuid:identifier>/samples/<uuid:sample_id>", inspection.dataset),
    path("api/dataset/sessions/<uuid:identifier>/images/<str:file_name>", inspection.dataset),
    path("api/conveyor/<str:action>", system.conveyor),
    path("api/sensors/<str:action>", system.sensors),
    path("api/products", system.products),
    path("api/products/defective", system.products, {"filter_kind": "defective"}),
    path("api/products/<int:identifier>", system.products),
    path("api/products/<str:filter_kind>/<str:value>", system.products),
    path("api/alerts", system.alerts),
    path("api/alerts/active", system.alerts, {"action": "active"}),
    path("api/alerts/<int:identifier>", system.alerts),
    path("api/alerts/<int:identifier>/resolve", system.alerts, {"action": "resolve"}),
    path("api/alerts/type/<str:value>", system.alerts, {"action": "type"}),
    path("api/logs", system.logs),
    path("api/logs/<str:action>/<str:value>", system.logs),
    path("api/statistics/<str:mode>", system.statistics),
]
for base, kind in [("recipes", "recipe"), ("calibration-profiles", "calibration")]:
    urlpatterns.extend(
        [
            path(f"api/{base}", inspection.definitions, {"kind": kind}),
            path(f"api/{base}/active", inspection.definitions, {"kind": kind, "action": "active"}),
            path(f"api/{base}/<uuid:identifier>", inspection.definitions, {"kind": kind}),
            path(
                f"api/{base}/<uuid:identifier>/activate",
                inspection.definitions,
                {"kind": kind, "action": "activate"},
            ),
        ]
    )
urlpatterns.append(
    path(
        "api/recipes/active/snapshot",
        inspection.definitions,
        {"kind": "recipe", "action": "snapshot"},
    )
)
for name in ["dashboard", "diagnostics", "control-room"]:
    urlpatterns.append(path("api/" + name, system.summary, {"kind": name}))
for route in [
    "",
    "history",
    "management",
    *[
        "management/" + n
        for n in [
            "control",
            "recipes",
            "calibration",
            "sensors",
            "diagnostics",
            "inspection",
            "sorting",
            "dataset",
            "simulation",
        ]
    ],
]:
    urlpatterns.append(path(route, pages.dashboard))
for old, new in {
    "inspection": "management/inspection",
    "analytics": "history",
    "sensors": "management/sensors",
    "sorting": "management/sorting",
    "recipes": "management/recipes",
    "calibration": "management/calibration",
    "dataset": "management/dataset",
    "diagnostics": "management/diagnostics",
}.items():
    urlpatterns.append(path(old, lambda request, new=new: HttpResponseRedirect("/" + new)))
urlpatterns.extend(
    [
        path("favicon.svg", pages.static, {"asset": "favicon.svg"}),
        re_path(r"^static/(?P<asset>.+)$", pages.static),
        re_path(r"^.*$", not_found),
    ]
)
