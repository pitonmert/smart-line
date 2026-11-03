"""Environment-based configuration shared by web and management commands."""

import os
from pathlib import Path
from urllib.parse import unquote, urlsplit

BASE_DIR = Path(__file__).resolve().parents[3]
DEBUG = os.getenv("SMARTLINE_ENV", "development").lower() != "production"
MOCK = os.getenv("SMARTLINE_MOCK", "false").lower() == "true"
SECRET_KEY = os.getenv("DJANGO_SECRET_KEY", "smartline-local-no-http-identity")
ALLOWED_HOSTS = os.getenv(
    "ALLOWED_HOSTS", "localhost,127.0.0.1,[::1],testserver,smartline.local,web"
).split(",")
INSTALLED_APPS = ["smartline.inspection", "smartline.devices", "smartline.web"]
MIDDLEWARE = ["smartline.web.http.ResponseMiddleware"]
ROOT_URLCONF = "smartline.web.urls"
ASGI_APPLICATION = "smartline.config.asgi.application"
TEMPLATES = [
    {
        "BACKEND": "django.template.backends.django.DjangoTemplates",
        "APP_DIRS": True,
        "OPTIONS": {"context_processors": ["django.template.context_processors.request"]},
    }
]
DEFAULT_AUTO_FIELD = "django.db.models.BigAutoField"
USE_TZ = True
TIME_ZONE = os.getenv("SMARTLINE_TIME_ZONE", "Europe/Istanbul")
LANGUAGE_CODE = "en"
APPEND_SLASH = False
STATIC_URL = "/static/"
STATIC_ROOT = BASE_DIR / "staticfiles"
STATICFILES_DIRS = [BASE_DIR / "src/smartline/web/static"]

_db = urlsplit(
    os.getenv("DATABASE_URL", "postgresql://smartline:smartline@127.0.0.1:5432/smartline_python")
)
DATABASES = {
    "default": {
        "ENGINE": "django.db.backends.postgresql",
        "NAME": unquote(_db.path.lstrip("/")),
        "USER": unquote(_db.username or ""),
        "PASSWORD": unquote(_db.password or ""),
        "HOST": _db.hostname,
        "PORT": _db.port or 5432,
        "CONN_MAX_AGE": 0,
    }
}
for _key, _variable in {
    "NAME": "DB_NAME",
    "USER": "DB_USER",
    "PASSWORD": "DB_PASSWORD",
    "HOST": "DB_HOST",
    "PORT": "DB_PORT",
}.items():
    if _variable in os.environ:
        DATABASES["default"][_key] = os.environ[_variable]
DATASET_ROOT = Path(os.getenv("DATASET_ROOT", str(BASE_DIR / "data/python/datasets")))
VISION_URL = os.getenv("VISION_URL", "http://127.0.0.1:8090").rstrip("/")
VISION_CAPTURE_TIMEOUT = float(os.getenv("VISION_CAPTURE_TIMEOUT_SECONDS", "35"))
SIMULATION_ENABLED = DEBUG and os.getenv("SIMULATION_ENABLED", "true").lower() == "true"
HARDWARE_ACTUATION_ENABLED = os.getenv("HARDWARE_ACTUATION_ENABLED", "false").lower() == "true"
HIGH_TEMP_THRESHOLD = float(os.getenv("HIGH_TEMP_THRESHOLD", "35"))
LOW_TEMP_THRESHOLD = float(os.getenv("LOW_TEMP_THRESHOLD", "10"))
FAN_HYSTERESIS = float(os.getenv("FAN_HYSTERESIS_CELSIUS", "2"))
ALARM_TEMP_THRESHOLD = float(os.getenv("ALARM_TEMP_THRESHOLD", "40"))
LOGGING = {
    "version": 1,
    "disable_existing_loggers": False,
    "handlers": {"console": {"class": "logging.StreamHandler"}},
    "root": {"handlers": ["console"], "level": os.getenv("LOG_LEVEL", "INFO")},
}
