"""Operator workflow coverage against the real Django template application."""

import os
import copy
import json
import socket
import subprocess
import time
from pathlib import Path
from urllib.request import urlopen

import pytest
from playwright.sync_api import sync_playwright


@pytest.fixture(scope="module")
def web_server():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, SMARTLINE_MOCK="true")
    log = Path("/tmp/smartline-python-browser.log").open("w+")
    process = subprocess.Popen(
        [str(Path(".venv/bin/smartline").resolve()), "web", "--mock", "--port", str(port)],
        env=env,
        stdout=log,
        stderr=log,
    )
    url = f"http://127.0.0.1:{port}"
    try:
        for _ in range(100):
            try:
                with urlopen(url + "/health", timeout=0.5) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(0.1)
            if process.poll() is not None:
                log.seek(0)
                pytest.fail(log.read())
        else:
            pytest.fail("Web process did not start")
        yield url
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        log.close()


@pytest.fixture(scope="module")
def browser():
    with sync_playwright() as p:
        browser = p.chromium.launch()
        yield browser
        browser.close()


@pytest.fixture
def page(browser, web_server):
    context = browser.new_context(base_url=web_server, viewport={"width": 1920, "height": 1080})
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    yield page
    assert not errors, errors
    context.close()


def loaded(page, path="/"):
    page.goto(path)
    page.locator("#page h1").wait_for()
    page.wait_for_function("document.querySelector('#connection').textContent === 'Live'")


def test_production_is_read_only(page):
    writes = []
    page.on(
        "request",
        lambda r: (
            writes.append(r.url) if "/api/" in r.url and r.method not in ["GET", "HEAD"] else None
        ),
    )
    loaded(page)
    assert page.get_by_role("heading", name="Production", exact=True).count() == 1
    assert page.locator(".monitor-grid [data-camera]").is_visible()
    assert page.locator(".production-counters").inner_text().find("2,480") >= 0
    assert page.get_by_role("button", name="Inspect camera with Vision").count() == 0
    assert not writes


@pytest.mark.parametrize(
    "route,title",
    [
        ("/history", "History"),
        ("/management", "Management"),
        ("/management/control", "Conveyor controls"),
        ("/management/recipes", "Product Recipes"),
        ("/management/calibration", "Calibration"),
        ("/management/sensors", "Sensors"),
        ("/management/diagnostics", "System Diagnostics"),
        ("/management/inspection", "Manual inspection"),
        ("/management/sorting", "Logical sorting"),
        ("/management/dataset", "Dataset collection"),
        ("/management/simulation", "Simulation tools"),
    ],
)
def test_routes(page, route, title):
    loaded(page, route)
    assert page.get_by_role("heading", name=title, exact=True).count() == 1


def test_language_theme_persistence_and_form_retention(page):
    loaded(page, "/management/calibration")
    page.locator('[name="name"]').fill("User calibration")
    page.get_by_role("button", name="Türkçe").click()
    assert page.locator("html").get_attribute("lang") == "tr"
    assert page.locator('[name="name"]').input_value() == "User calibration"
    assert page.get_by_role("link", name="Üretim", exact=True).count() == 1
    page.locator("#theme-toggle").click()
    assert page.locator("html").get_attribute("data-theme") == "light"
    assert page.locator('[name="name"]').input_value() == "User calibration"
    page.reload()
    page.locator("#page h1").wait_for()
    assert page.locator("html").get_attribute("lang") == "tr"
    assert page.locator("html").get_attribute("data-theme") == "light"


@pytest.mark.parametrize("mode", ["invalid", "blocked"])
def test_invalid_or_blocked_storage(page, mode):
    if mode == "invalid":
        page.add_init_script(
            "localStorage.setItem('smartline.language','xx');localStorage.setItem('smartline.theme','xx')"
        )
    else:
        page.add_init_script(
            "Storage.prototype.getItem=()=>{throw Error('blocked')};Storage.prototype.setItem=()=>{throw Error('blocked')}"
        )
    loaded(page)
    assert page.locator("html").get_attribute("lang") == "en"
    assert page.locator("html").get_attribute("data-theme") == "dark"
    page.get_by_role("button", name="Türkçe").click()
    assert page.locator("html").get_attribute("lang") == "tr"


def test_history_filters_details_and_pagination(page):
    loaded(page, "/history")
    page.locator('select[name="decision"]').select_option("REJECT")
    page.get_by_role("button", name="Search", exact=True).click()
    page.wait_for_function("document.querySelectorAll('tbody tr').length===3")
    assert all("REJECT" in text for text in page.locator("tbody tr").all_inner_texts())
    page.get_by_role("button", name="View details").first.click()
    assert page.get_by_text("Physical estimate", exact=True).is_visible()
    page.get_by_role("button", name="Production summary", exact=True).click()
    page.locator("canvas").wait_for()
    page.get_by_role("button", name="Line", exact=True).click()


def test_legacy_routes_and_browser_history(page):
    loaded(page, "/inspection")
    assert page.url.endswith("/management/inspection")
    page.get_by_role("link", name="History", exact=True).click()
    page.wait_for_url("**/history")
    page.go_back()
    page.wait_for_url("**/management/inspection")


@pytest.mark.parametrize("width,height", [(1920, 1080), (1366, 768), (390, 844)])
def test_responsive_layout(page, width, height):
    page.set_viewport_size({"width": width, "height": height})
    for route in ["/", "/history", "/management", "/management/dataset"]:
        loaded(page, route)
        assert page.evaluate("document.documentElement.scrollWidth<=innerWidth"), route
    if width == 1920:
        loaded(page)
        assert page.evaluate("document.documentElement.scrollHeight<=innerHeight")


def test_mock_write_error_is_localized(page):
    loaded(page, "/management/inspection")
    page.get_by_role("button", name="Inspect camera with Vision", exact=True).click()
    page.locator("[data-error]").filter(has_text="Mock preview is read-only").wait_for()


def test_inactive_tools_do_not_poll(page):
    calls = []
    page.on("request", lambda r: calls.append(r.url))
    loaded(page)
    page.wait_for_timeout(1200)
    assert not any(
        "/api/dataset/" in url or "/api/simulation/status" in url or "/api/tracking/queue" in url
        for url in calls
    )


def test_mock_has_no_external_requests(page):
    external = []
    page.on(
        "request",
        lambda r: (
            external.append(r.url)
            if r.url.startswith("http") and not r.url.startswith("http://127.0.0.1:")
            else None
        ),
    )
    loaded(page)
    assert not external


def install_live_reads(page, change=None):
    fixture = json.loads(Path("src/smartline/web/static/mock-data.json").read_text())
    fixture["diagnostics"].update(
        api="healthy",
        database="healthy",
        mqtt="connected",
        vision="ONLINE",
        camera="ONLINE",
        esp32="online",
        environment="Development",
        simulation={
            "enabled": True,
            "active": False,
            "latched": False,
            "inFlightSimulatedProducts": 0,
        },
    )
    fixture["dashboard"]["conveyorStatus"].update(
        isRunning=False, isEmergencyStopped=False, isFireDetected=False
    )
    reads = {
        "/api/dashboard": fixture["dashboard"],
        "/api/diagnostics": fixture["diagnostics"],
        "/api/simulation/status": fixture["diagnostics"]["simulation"],
    }
    if change:
        change(fixture, reads)

    def handle(route):
        from urllib.parse import urlsplit

        path = urlsplit(route.request.url).path
        if path in reads:
            route.fulfill(json={"isSuccess": True, "data": reads[path], "message": ""})
        else:
            route.continue_()

    page.route("**/api/**", handle)
    return fixture, reads


def test_live_controls_errors_and_disconnect_recovery(page):
    install_live_reads(page)
    page.route(
        "**/api/conveyor/start",
        lambda r: r.fulfill(
            status=503, json={"isSuccess": False, "code": "MQTT_UNAVAILABLE", "message": "offline"}
        ),
    )
    loaded(page, "/management/control")
    page.get_by_role("button", name="Start", exact=True).click()
    page.locator("[data-error]").filter(has_text="device connection").wait_for()
    assert page.locator('[name="speed"]').input_value() == "55"
    page.context.set_offline(True)
    page.wait_for_function("document.querySelector('[data-action=start]').disabled")
    page.context.set_offline(False)
    page.wait_for_function("!document.querySelector('[data-action=start]').disabled")


def test_failed_calibration_preserves_inputs(page):
    install_live_reads(page)
    page.route(
        "**/api/calibration-profiles",
        lambda r: (
            r.fulfill(
                status=400,
                json={"isSuccess": False, "code": "CALIBRATION_INVALID", "message": "invalid"},
            )
            if r.request.method == "POST"
            else r.continue_()
        ),
    )
    loaded(page, "/management/calibration")
    page.locator('[name="name"]').fill("Keep this")
    page.get_by_role("button", name="Save profile", exact=True).click()
    page.locator("[data-error]").filter(has_text="calibration is invalid").wait_for()
    assert page.locator('[name="name"]').input_value() == "Keep this"


def test_dataset_interval_stops_on_navigation_and_remote_stop(page):
    fixture, reads = install_live_reads(page)
    session = copy.deepcopy(fixture["dataset"])
    session.update(captureMode="interval", intervalMs=200)
    sessions = [session]
    captures = []
    page.route(
        "**/api/dataset/sessions", lambda r: r.fulfill(json={"isSuccess": True, "data": sessions})
    )

    def capture(route):
        captures.append(route.request.post_data_json)
        session["imageCount"] += 1
        route.fulfill(
            json={
                "isSuccess": True,
                "data": {"session": session, "sample": session["recentSamples"][0]},
            }
        )

    page.route("**/api/dataset/sessions/*/captures/camera", capture)
    loaded(page, "/management/dataset")
    page.get_by_role("button", name="Start interval", exact=True).click()
    page.wait_for_function(
        "document.querySelector('[data-testid=dataset-count]').textContent.includes('4')"
    )
    page.get_by_role("link", name="Production", exact=True).click()
    count = len(captures)
    page.wait_for_timeout(800)
    assert len(captures) == count
    page.get_by_role("link", name="Management", exact=True).click()
    page.get_by_role("link", name="Dataset collection", exact=False).click()
    page.get_by_role("button", name="Start interval", exact=True).click()
    page.wait_for_timeout(400)
    sessions.clear()
    page.wait_for_function("document.querySelector('[data-action=capture]').disabled", timeout=8000)
    count = len(captures)
    page.wait_for_timeout(700)
    assert len(captures) == count


def test_fire_banner_is_global_and_controls_remain_locked(page):
    def fire(fixture, reads):
        fixture["dashboard"]["conveyorStatus"].update(isFireDetected=True, isEmergencyStopped=True)
        fixture["dashboard"]["activeAlerts"] = [
            {"id": 7, "alertType": "FIRE", "isActive": True, "triggeredAt": "2026-10-06T00:00:00Z"}
        ]

    install_live_reads(page, fire)
    loaded(page)
    assert page.get_by_text("FIRE ALARM", exact=True).is_visible()
    assert not page.get_by_role("button", name="Resolve alarm", exact=True).is_visible()
    page.get_by_role("link", name="History", exact=True).click()
    assert page.get_by_text("FIRE ALARM", exact=True).is_visible()
    page.get_by_role("link", name="Management", exact=True).click()
    page.get_by_role("link", name="Conveyor controls", exact=False).click()
    assert page.get_by_role("button", name="Start", exact=True).is_disabled()
    assert page.get_by_role("button", name="Reset emergency stop", exact=True).is_disabled()


@pytest.mark.parametrize(
    "language,theme", [("en", "dark"), ("en", "light"), ("tr", "dark"), ("tr", "light")]
)
def test_all_language_and_theme_combinations(page, language, theme):
    page.add_init_script(
        f"localStorage.setItem('smartline.language','{language}');localStorage.setItem('smartline.theme','{theme}')"
    )
    page.goto("/management/calibration")
    page.locator('[name="name"]').wait_for()
    assert page.locator("html").get_attribute("lang") == language
    assert page.locator("html").get_attribute("data-theme") == theme
    assert page.locator('[name="name"]').get_attribute("aria-label") == (
        "Name" if language == "en" else "Ad"
    )
    page.reload()
    page.locator('[name="name"]').wait_for()
    assert page.locator("html").get_attribute("data-theme") == theme


def test_errors_switch_language_without_losing_unknown_server_text(page):
    loaded(page, "/management/inspection")
    page.get_by_role("button", name="Inspect camera with Vision", exact=True).click()
    page.locator("[data-error]").filter(has_text="Mock preview is read-only").wait_for()
    page.get_by_role("button", name="Türkçe").click()
    assert "Mock preview" not in page.locator("[data-error]").inner_text()
    assert page.locator("[data-error]").inner_text()
    page.route(
        "**/api/inspections/vision",
        lambda r: r.fulfill(
            status=400, json={"isSuccess": False, "code": "CUSTOM_FAILURE", "message": "History"}
        ),
    )
    page.locator('[data-action="inspect"]').click()
    page.locator("[data-error]").filter(has_text="History").wait_for()
    page.get_by_role("button", name="English").click()
    assert page.locator("[data-error]").inner_text() == "History"


def test_recipe_names_are_user_content_and_barcode_counters_remain_available(page):
    fixture = json.loads(Path("src/smartline/web/static/mock-data.json").read_text())
    recipes = [fixture["recipe"]]
    recipes[0]["name"] = "History"
    fixture["diagnostics"]["activeRecipe"] = "History"
    page.route(
        "**/api/diagnostics",
        lambda r: r.fulfill(json={"isSuccess": True, "data": fixture["diagnostics"]}),
    )
    page.route("**/api/recipes", lambda r: r.fulfill(json={"isSuccess": True, "data": recipes}))
    loaded(page, "/management/recipes")
    page.get_by_role("button", name="Türkçe").click()
    assert page.get_by_role("heading", name="History", exact=True).is_visible()
    page.goto("/management/diagnostics")
    page.locator(".detail-list").wait_for()
    assert page.locator(".detail-list dd").filter(has_text="History").count() >= 1
    page.get_by_role("button", name="English").click()
    page.get_by_role("link", name="History", exact=True).click()
    page.get_by_role("button", name="Production summary", exact=True).click()
    page.get_by_role("button", name="Recent products", exact=True).click()
    assert page.get_by_role("columnheader", name="Barcode", exact=True).is_visible()
    page.get_by_role("button", name="Hourly production", exact=True).click()
    assert page.get_by_role("columnheader", name="Accepted", exact=True).is_visible()


def test_camera_roi_is_separate_from_stored_inspection_boxes(page):
    loaded(page)
    page.locator("[data-roi]").wait_for(state="visible")
    roi = page.locator("[data-roi]").bounding_box()
    image = page.locator("[data-camera]").bounding_box()
    assert roi["width"] > 0 and roi["height"] > 0
    assert roi["x"] >= image["x"] and roi["y"] >= image["y"]
    assert roi["x"] + roi["width"] <= image["x"] + image["width"] + 1
    assert page.locator("[data-detection]").count() == 0


def test_fullscreen_and_empty_data(page):
    def empty(fixture, reads):
        reads["/api/control-room"] = {
            "lastInspection": None,
            "totalCount": 0,
            "acceptedCount": 0,
            "rejectedCount": 0,
            "manualReviewCount": 0,
            "yieldPercent": None,
            "activeRecipe": fixture["recipe"]["name"],
        }

    install_live_reads(page, empty)
    loaded(page)
    assert page.get_by_text("No inspection yet", exact=True).is_visible()
    assert page.locator(".production-counters dd").all_inner_texts() == ["0", "0", "0", "0", "—"]
    page.locator("#fullscreen").click()
    page.wait_for_function("document.fullscreenElement !== null")
    page.locator("#fullscreen").click()
    page.wait_for_function("document.fullscreenElement === null")
