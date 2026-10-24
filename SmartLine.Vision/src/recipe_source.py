"""Active product recipe for Vision.

The API database is the source of truth. Vision reads it with the device key,
which stays in this process and is never returned by the control API. A recipe
file remains the offline fallback. An inspection request may also carry the
snapshot the API already validated.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from urllib import error, request


class ActiveRecipeSource:
    def __init__(self, api_base: str, device_key: str, file_recipe: dict | None = None, transport=None, ttl_seconds: float = 2.0) -> None:
        self.api_base = api_base.rstrip("/")
        self.device_key = device_key
        self.file_recipe = file_recipe
        self.ttl_seconds = ttl_seconds
        self._transport = transport or _get_json
        self._cached: dict | None = None
        self._cached_at: datetime | None = None
        self.last_error: str | None = None
        self.last_source = "missing"

    def resolve(self, requested: dict | None = None) -> tuple[dict | None, dict]:
        if requested is not None:
            recipe = _normalize_request(requested)
            if recipe is None:
                return None, {"source": "request", "error": "recipe_invalid"}
            self.last_source = "request"
            self.last_error = None
            return recipe, _meta(recipe, "request")

        fetched = self._fetch()
        if fetched is not None:
            self.last_source = "api"
            return fetched, _meta(fetched, "api")
        if self.file_recipe is not None:
            self.last_source = "file"
            return self.file_recipe, _meta(self.file_recipe, "file")
        self.last_source = "missing"
        return None, {"source": "missing", "id": None, "name": None, "version": None}

    def _fetch(self) -> dict | None:
        if not self.api_base or not self.device_key:
            return self._cached
        now = datetime.now(timezone.utc)
        if self._cached is not None and self._cached_at is not None:
            age = (now - self._cached_at).total_seconds()
            if age < self.ttl_seconds:
                return self._cached
        url = f"{self.api_base}/api/recipes/active/snapshot"
        try:
            payload = self._transport(
                url,
                {"X-Device-Key": self.device_key, "Accept": "application/json"},
            )
        except Exception as exc:  # noqa: BLE001 - keep the last good snapshot
            self.last_error = str(exc)
            return None
        recipe = _normalize_api(payload)
        if recipe is None:
            self.last_error = "recipe_snapshot_invalid"
            return None
        self._cached = recipe
        self._cached_at = now
        self.last_error = None
        return recipe


def _normalize_request(payload: dict) -> dict | None:
    if not isinstance(payload, dict):
        return None
    definition = payload.get("definition") if isinstance(payload.get("definition"), dict) else payload
    if not isinstance(definition, dict) or not isinstance(definition.get("productClasses"), list):
        return None
    recipe = dict(definition)
    recipe["id"] = payload.get("id") or payload.get("recipeId") or definition.get("id")
    recipe["version"] = payload.get("version") if payload.get("version") is not None else payload.get("recipeVersion")
    recipe["name"] = payload.get("name") or definition.get("name")
    return recipe


def _normalize_api(payload: dict) -> dict | None:
    if not isinstance(payload, dict):
        return None
    data = payload.get("data") if isinstance(payload.get("data"), dict) else payload
    if not isinstance(data, dict):
        return None
    definition = data.get("definition")
    if not isinstance(definition, dict) or not isinstance(definition.get("productClasses"), list):
        return None
    recipe = dict(definition)
    recipe["id"] = data.get("id")
    recipe["name"] = data.get("name") or definition.get("name")
    recipe["version"] = data.get("version")
    return recipe


def _meta(recipe: dict, source: str) -> dict:
    return {
        "source": source,
        "id": None if recipe.get("id") is None else str(recipe.get("id")),
        "name": recipe.get("name"),
        "version": recipe.get("version"),
    }


def _get_json(url: str, headers: dict) -> dict:
    req = request.Request(url, headers=headers, method="GET")
    try:
        with request.urlopen(req, timeout=3) as response:
            return json.loads(response.read().decode("utf-8"))
    except error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Recipe snapshot failed with HTTP {exc.code}: {detail}") from exc
