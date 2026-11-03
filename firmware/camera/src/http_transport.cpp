#include "http_transport.h"

#include "camera_control.h"

#include <Arduino.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "esp_heap_caps.h"
#include "esp_timer.h"
#include <lwip/sockets.h>

struct SendContext {
  httpd_req_t* request;
  int esp_status;
};

static uint32_t clock_ms(void* /*ctx*/) {
  return static_cast<uint32_t>(esp_timer_get_time() / 1000);
}

static int write_chunk(void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out) {
  SendContext* send = static_cast<SendContext*>(ctx);
  errno = 0;
  const char* bytes = chunk->data == nullptr ? nullptr : reinterpret_cast<const char*>(chunk->data);
  const esp_err_t result = httpd_resp_send_chunk(send->request, bytes, chunk->length);
  send->esp_status = static_cast<int>(result);
  if (result != ESP_OK) {
    *error_code = errno;
    *timed_out = (errno == EAGAIN || errno == EWOULDBLOCK || errno == ETIMEDOUT) ? 1 : 0;
    return -1;
  }
  return static_cast<int>(chunk->length);
}

static void disable_nagle(httpd_req_t* request) {
  const int fd = httpd_req_to_sockfd(request);
  if (fd < 0) {
    return;
  }
  const int enabled = 1;
  // Chunked writes must not wait for a delayed ACK. Failure here does not
  // hide a later short write; the chunk loop still records that result.
  setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));
}

int transport_send_body(
  httpd_req_t* request,
  const uint8_t* data,
  size_t length,
  int stage,
  int finish,
  BodySendStats* stats,
  int* esp_status
) {
  SendContext ctx{request, ESP_OK};
  // Port 81 streams while port 80 serves stills. Those independent HTTPD
  // tasks can enter here at the same time, so a file-static staging buffer
  // corrupts whichever response loses the race. Keep this allocation scoped
  // to one synchronous send; MALLOC_CAP_INTERNAL keeps PSRAM out of the
  // Wi-Fi/TCP copy path without consuming another 2 KB from either task stack.
  uint8_t* stage_buffer = nullptr;
  if (stage) {
    stage_buffer = static_cast<uint8_t*>(
      heap_caps_malloc(SMARTLINE_HTTP_CHUNK_BYTES, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT)
    );
    if (stage_buffer == nullptr) {
      if (stats != nullptr) {
        memset(stats, 0, sizeof(*stats));
        stats->bytes_intended = static_cast<uint32_t>(length);
        stats->error_code = ENOMEM;
      }
      if (esp_status != nullptr) {
        *esp_status = ESP_ERR_NO_MEM;
      }
      return -1;
    }
  }
  disable_nagle(request);
  const int rc = body_send(
    data,
    length,
    SMARTLINE_HTTP_CHUNK_BYTES,
    stage_buffer,
    finish,
    write_chunk,
    &ctx,
    clock_ms,
    nullptr,
    stats
  );
  free(stage_buffer);
  if (esp_status != nullptr) {
    *esp_status = ctx.esp_status;
  }
  return rc;
}

static void set_number_header(httpd_req_t* request, const char* name, int value, char* storage, size_t storage_length) {
  snprintf(storage, storage_length, "%d", value);
  httpd_resp_set_hdr(request, name, storage);
}

static int query_int(httpd_req_t* request, const char* key, int fallback) {
  char query[80];
  char value[16];
  if (httpd_req_get_url_query_str(request, query, sizeof(query)) != ESP_OK) {
    return fallback;
  }
  if (httpd_query_key_value(query, key, value, sizeof(value)) != ESP_OK) {
    return fallback;
  }
  return atoi(value);
}

static esp_err_t send_throughput(httpd_req_t* request, const char* memory_name, uint32_t caps) {
  const int bytes = query_int(request, "bytes", 32768);
  const int stage = query_int(request, "stage", 0) == 1 ? 1 : 0;
  if (!throughput_bytes_ok(bytes)) {
    return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "throughput_bytes");
  }
  if ((caps & MALLOC_CAP_SPIRAM) != 0 && !psramFound()) {
    httpd_resp_set_status(request, "503 Service Unavailable");
    httpd_resp_set_type(request, "application/json");
    return httpd_resp_send(request, "{\"error\":\"throughput_psram_unavailable\"}", HTTPD_RESP_USE_STRLEN);
  }

  uint8_t* payload = static_cast<uint8_t*>(heap_caps_malloc(static_cast<size_t>(bytes), caps));
  if (payload == nullptr) {
    const char* error = "throughput_alloc_failed";
    BodySendStats stats = {};
    stats.bytes_intended = static_cast<uint32_t>(bytes);
    stats.error_code = ENOMEM;
    camera_note_transport("debug", &stats, error, ESP_ERR_NO_MEM);
    httpd_resp_set_status(request, "503 Service Unavailable");
    httpd_resp_set_type(request, "application/json");
    return httpd_resp_send(request, "{\"error\":\"throughput_alloc_failed\"}", HTTPD_RESP_USE_STRLEN);
  }
  for (int index = 0; index < bytes; index++) {
    payload[index] = static_cast<uint8_t>(index & 0xFF);
  }

  char payload_header[16];
  char chunk_header[16];
  char stage_header[8];
  set_number_header(request, "X-Debug-Payload-Bytes", bytes, payload_header, sizeof(payload_header));
  set_number_header(request, "X-Debug-Chunk-Bytes", SMARTLINE_HTTP_CHUNK_BYTES, chunk_header, sizeof(chunk_header));
  set_number_header(request, "X-Debug-Staged", stage, stage_header, sizeof(stage_header));
  httpd_resp_set_type(request, "application/octet-stream");
  httpd_resp_set_hdr(request, "Cache-Control", "no-store");
  httpd_resp_set_hdr(request, "X-Debug-Memory", memory_name);

  BodySendStats stats = {};
  int esp_status = ESP_OK;
  const int rc = transport_send_body(request, payload, static_cast<size_t>(bytes), stage, 1, &stats, &esp_status);
  free(payload);
  const char* transport_error = transport_error_name(stats);
  camera_note_transport("debug", &stats, transport_error, esp_status);
  Serial.printf(
    "[HTTP] throughput %s %d bayt stage %d yazilan %lu/%lu %lu ms hata %s errno %d esp 0x%x\n",
    memory_name,
    bytes,
    stage,
    static_cast<unsigned long>(stats.bytes_written),
    static_cast<unsigned long>(stats.bytes_intended),
    static_cast<unsigned long>(stats.body_send_ms),
    transport_error[0] == '\0' ? "yok" : transport_error,
    stats.error_code,
    static_cast<unsigned>(esp_status)
  );
  return rc == 0 ? ESP_OK : ESP_FAIL;
}

static esp_err_t throughput_query_handler(httpd_req_t* request) {
  char query[80];
  char memory_name[16];
  bool use_psram = false;
  if (httpd_req_get_url_query_str(request, query, sizeof(query)) == ESP_OK &&
      httpd_query_key_value(query, "mem", memory_name, sizeof(memory_name)) == ESP_OK) {
    use_psram = strcmp(memory_name, "psram") == 0;
    if (!use_psram && strcmp(memory_name, "internal") != 0) {
      return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "throughput_memory");
    }
  }
  if (use_psram) {
    return send_throughput(request, "psram", MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  }
  return send_throughput(request, "internal", MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
}

static esp_err_t throughput_internal_handler(httpd_req_t* request) {
  return send_throughput(request, "internal", MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
}

static esp_err_t throughput_psram_handler(httpd_req_t* request) {
  return send_throughput(request, "psram", MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
}

void transport_register_http(httpd_handle_t server) {
  const httpd_uri_t query = {
    .uri = "/debug/throughput",
    .method = HTTP_GET,
    .handler = throughput_query_handler,
    .user_ctx = nullptr,
  };
  const httpd_uri_t internal = {
    .uri = "/debug/throughput/internal",
    .method = HTTP_GET,
    .handler = throughput_internal_handler,
    .user_ctx = nullptr,
  };
  const httpd_uri_t psram = {
    .uri = "/debug/throughput/psram",
    .method = HTTP_GET,
    .handler = throughput_psram_handler,
    .user_ctx = nullptr,
  };
  if (httpd_register_uri_handler(server, &query) != ESP_OK ||
      httpd_register_uri_handler(server, &internal) != ESP_OK ||
      httpd_register_uri_handler(server, &psram) != ESP_OK) {
    Serial.println("[HTTP] /debug/throughput register hatasi.");
  } else {
    Serial.println("[HTTP] /debug/throughput hazir.");
  }
}
