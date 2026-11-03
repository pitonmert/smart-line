#pragma once

#include "esp_http_server.h"
#include "http_body.h"

#include <stddef.h>
#include <stdint.h>

// stage != 0 copies each chunk into a per-send internal-RAM buffer before the
// socket write. Concurrent server tasks never share a staging buffer.
// stage == 0 passes the caller's buffer to the socket. The debug endpoints
// use stage == 0 so an internal payload and a PSRAM payload are not both
// reduced to the same scratch buffer.
// esp_status receives the last esp_err_t from httpd_resp_send_chunk.
// finish != 0 writes the zero-length chunk that ends a chunked response.
// The MJPEG stream must pass 0 because the socket stays open for later frames.
int transport_send_body(
  httpd_req_t* request,
  const uint8_t* data,
  size_t length,
  int stage,
  int finish,
  BodySendStats* stats,
  int* esp_status
);

void transport_register_http(httpd_handle_t server);
