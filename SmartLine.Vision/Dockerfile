FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        gosu \
        libgl1 \
        libglib2.0-0 \
        libzbar0 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 smartline

WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY --chown=smartline:smartline src/ ./src/
COPY docker-entrypoint.sh /usr/local/bin/smartline-vision-entrypoint
RUN chmod 0755 /usr/local/bin/smartline-vision-entrypoint
ENTRYPOINT ["/usr/local/bin/smartline-vision-entrypoint"]
CMD ["python", "-u", "src/main.py"]
