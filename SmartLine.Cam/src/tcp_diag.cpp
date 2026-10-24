#include "tcp_diag.h"

#if SMARTLINE_TCP_DIAG

#include <Arduino.h>
#include <errno.h>

#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "soc/soc_memory_types.h"

#include <lwip/sockets.h>

// BSS stays in internal DRAM. CONFIG_SPIRAM_ALLOW_BSS_SEG_EXTERNAL_MEMORY
// is not set in this Arduino-ESP32 package, and esp_ptr_internal checks it
// again before the socket is opened.
static uint8_t g_payload[SMARTLINE_TCP_DIAG_BYTES];

static uint32_t clock_ms(void* /*ctx*/) {
  return static_cast<uint32_t>(esp_timer_get_time() / 1000);
}

static int write_once(void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out) {
  const int fd = *static_cast<int*>(ctx);
  errno = 0;
  const int wrote = send(fd, chunk->data, chunk->length, 0);
  if (wrote < 0) {
    *error_code = errno;
    *timed_out = (errno == EAGAIN || errno == EWOULDBLOCK || errno == ETIMEDOUT) ? 1 : 0;
    return -1;
  }
  if (wrote == 0) {
    *error_code = EPIPE;
    return -1;
  }
  return wrote;
}

static void serve_client(int fd) {
  // Connection-to-completion includes option setup and ends when the writes
  // return, before shutdown. A slow setsockopt is part of the connection.
  const uint32_t connected_at = clock_ms(nullptr);
  const int enabled = 1;
  setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));
  // Same 15 second ceiling as the HTTP server's send_wait_timeout. It records
  // a stalled write. It is not raised to make a slow transfer look finished.
  const timeval timeout{15, 0};
  setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
  BodySendStats stats = {};
  tcp_diag_send(
    g_payload,
    SMARTLINE_TCP_DIAG_BYTES,
    SMARTLINE_HTTP_CHUNK_BYTES,
    write_once,
    &fd,
    clock_ms,
    nullptr,
    &stats
  );
  const uint32_t total_ms = clock_ms(nullptr) - connected_at;
  const char* error_name = transport_error_name(stats);
  Serial.printf(
    "[TCP] 9001 yazilan %lu/%lu yazim %lu max %lu ms yazim_ms %lu toplam_ms %lu errno %d %s\n",
    static_cast<unsigned long>(stats.bytes_written),
    static_cast<unsigned long>(stats.bytes_intended),
    static_cast<unsigned long>(stats.chunk_count),
    static_cast<unsigned long>(stats.max_chunk_ms),
    static_cast<unsigned long>(stats.body_send_ms),
    static_cast<unsigned long>(total_ms),
    stats.error_code,
    error_name[0] == '\0' ? "yok" : error_name
  );

  shutdown(fd, SHUT_WR);
  close(fd);
}

static void tcp_diag_task(void* /*arg*/) {
  const int listen_fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (listen_fd < 0) {
    Serial.printf("[TCP] soket acilamadi errno %d\n", errno);
    vTaskDelete(nullptr);
    return;
  }

  const int reuse = 1;
  setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

  sockaddr_in address = {};
  address.sin_family = AF_INET;
  address.sin_port = htons(SMARTLINE_TCP_DIAG_PORT);
  address.sin_addr.s_addr = htonl(INADDR_ANY);
  if (bind(listen_fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0 || listen(listen_fd, 1) != 0) {
    Serial.printf("[TCP] 9001 dinlenemedi errno %d\n", errno);
    close(listen_fd);
    vTaskDelete(nullptr);
    return;
  }

  Serial.printf(
    "[TCP] 9001 hazir. %d bayt, parca %d, dahili RAM, kamera yok.\n",
    SMARTLINE_TCP_DIAG_BYTES,
    SMARTLINE_HTTP_CHUNK_BYTES
  );

  while (true) {
    const int fd = accept(listen_fd, nullptr, nullptr);
    if (fd < 0) {
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }
    serve_client(fd);
  }
}

void tcp_diag_start() {
  tcp_diag_fill(g_payload, sizeof(g_payload));
  if (!esp_ptr_internal(g_payload) || !esp_ptr_internal(g_payload + sizeof(g_payload) - 1)) {
    Serial.println("[TCP] tampon dahili RAM'de degil. Sunucu acilmadi.");
    return;
  }

  // Same priority band and core as the HTTP server task, so the comparison
  // is not a difference in which CPU runs the writes.
  const BaseType_t created = xTaskCreatePinnedToCore(
    tcp_diag_task,
    "tcp_diag",
    6144,
    nullptr,
    tskIDLE_PRIORITY + 5,
    nullptr,
    1
  );
  if (created != pdPASS) {
    Serial.println("[TCP] gorev acilamadi.");
  }
}

#else

void tcp_diag_start() {}

#endif
