FROM python:3.12-slim-bookworm AS base
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates gosu \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 smartline
RUN pip install --no-cache-dir uv==0.13.6
WORKDIR /app
ENV UV_PYTHON_DOWNLOADS=never PATH="/app/.venv/bin:$PATH" PYTHONUNBUFFERED=1
COPY pyproject.toml uv.lock ./
COPY src/ ./src/
COPY deploy/entrypoint.sh ./
RUN chmod 0755 /app/entrypoint.sh
ENTRYPOINT ["/app/entrypoint.sh"]

FROM base AS web
RUN uv sync --frozen --no-dev --no-editable \
    && uv cache clean \
    && rm -rf /app/src
CMD ["smartline", "web", "--host", "0.0.0.0", "--port", "8443"]

FROM base AS vision
ARG INFERENCE_BACKEND=all
RUN apt-get update && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 libzbar0 \
    && rm -rf /var/lib/apt/lists/* \
    && if [ "$INFERENCE_BACKEND" != onnx ]; then \
         uv sync --frozen --no-dev --no-editable --extra vision --extra openvino; \
       else uv sync --frozen --no-dev --no-editable --extra vision; fi \
    && uv cache clean \
    && rm -rf /app/src
CMD ["smartline", "vision"]

FROM base AS development
RUN uv sync --frozen --extra vision
CMD ["smartline", "web", "--host", "0.0.0.0", "--port", "8000"]
