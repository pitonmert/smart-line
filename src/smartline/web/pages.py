import mimetypes
from pathlib import Path

from django.conf import settings
from django.http import HttpResponse
from django.shortcuts import render

from .http import not_found


def dashboard(request):
    return render(request, "smartline/dashboard.html", {"mock": settings.MOCK})


def static(request, asset):
    root = Path(__file__).parent / "static"
    path = (root / asset).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        return not_found(request)
    response = HttpResponse(
        path.read_bytes(),
        content_type=mimetypes.guess_type(path.name)[0] or "application/octet-stream",
    )
    response["Cache-Control"] = "public, max-age=3600"
    return response
