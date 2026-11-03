import json
import re
from datetime import date, datetime, timezone
from decimal import Decimal
from uuid import UUID


def camel(name):
    return re.sub(r"_([a-z])", lambda m: m[1].upper(), name)


class Encoder(json.JSONEncoder):
    def default(self, value):
        if isinstance(value, datetime):
            return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
        if isinstance(value, date):
            return value.isoformat()
        if isinstance(value, UUID):
            return str(value)
        if isinstance(value, Decimal):
            return float(value)
        return super().default(value)


def json_data(value):
    return json.loads(json.dumps(value, cls=Encoder, allow_nan=False))


def fields(model, names, **extra):
    result = {camel(n): getattr(model, n) for n in names.split()}
    result.update(extra)
    return json_data(result)


def page(query, params, serializer, default_size=10):
    number = max(1, min(100000, int(params.get("pageNumber", 1))))
    size = max(1, min(100, int(params.get("pageSize", default_size))))
    items = list(query[(number - 1) * size : number * size + 1])
    return {
        "items": [serializer(item) for item in items[:size]],
        "hasNextPage": len(items) > size,
        "hasPreviousPage": number > 1,
        "pageNumber": number,
        "pageSize": size,
    }
