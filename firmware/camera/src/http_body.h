#pragma once

// Chunked HTTP body sender with no ESP-IDF dependency.
//
// Arduino-ESP32 2.0.17 (ESP-IDF v4.4.7) implements httpd_resp_send() as one
// httpd_send_all() loop. Each send() asks LwIP to copy the whole remaining
// buffer (NETCONN_COPY) into a 5760-byte TCP send buffer (TCP_SND_BUF_DEFAULT)
// and a 1436-byte MSS. The socket stays blocked until that call finishes or
// send_wait_timeout expires. With CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL set to
// 4096, a JPEG larger than 4 KB from malloc() is in PSRAM, and the raw VGA
// frame is allocated in PSRAM on purpose. Copying that memory inside the TCPIP
// task shares the ESP32 flash/PSRAM cache with Wi-Fi.
//
// The production path therefore copies at most SMARTLINE_HTTP_CHUNK_BYTES into
// an internal scratch buffer and sends that chunk before reading more PSRAM.
// 2048 is the default because two such chunks (4096) still fit in the 5760-byte
// send buffer, while 4096 would fill it and make every following chunk wait.
// 1024 also fits, but it doubles the number of socket calls for a 614400-byte
// raw frame. The scratch pointer may be null for the diagnostic endpoints,
// which must show the socket the original internal or PSRAM bytes.

#include <errno.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#ifndef SMARTLINE_HTTP_CHUNK_BYTES
#define SMARTLINE_HTTP_CHUNK_BYTES 2048
#endif

static_assert(
  SMARTLINE_HTTP_CHUNK_BYTES == 1024 || SMARTLINE_HTTP_CHUNK_BYTES == 2048 || SMARTLINE_HTTP_CHUNK_BYTES == 4096,
  "HTTP body chunk must be 1024, 2048, or 4096");

struct BodyChunk {
  const uint8_t* data;
  size_t length;
};

struct BodySendStats {
  uint32_t bytes_intended;
  uint32_t bytes_written;
  uint32_t chunk_count;
  uint32_t max_chunk_ms;
  uint32_t body_send_ms;
  uint32_t started_at_ms;
  int error_code;
  int timed_out;
  int complete;
};

// Return the number of bytes accepted, or a negative value on failure.
// A data write must make progress. The terminating chunk has data == nullptr
// and length == 0; a non-negative return completes the HTTP chunked body.
typedef int (*BodyWriteFn)(void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out);
typedef uint32_t (*BodyClockFn)(void* ctx);

inline int throughput_bytes_ok(int requested) {
  return requested == 1024 || requested == 8192 || requested == 32768 || requested == 65536;
}

inline const char* transport_error_name(const BodySendStats& stats) {
  if (stats.complete) {
    return "";
  }
  if (stats.timed_out) {
    return "send_timeout";
  }
  if (stats.error_code == EPIPE || stats.error_code == ECONNRESET || stats.error_code == ENOTCONN) {
    return "client_disconnected";
  }
  return "send_failed";
}

inline int body_send(
  const uint8_t* data,
  size_t length,
  size_t chunk_size,
  uint8_t* stage,
  int finish,
  BodyWriteFn write_fn,
  void* write_ctx,
  BodyClockFn now_ms,
  void* clock_ctx,
  BodySendStats* stats
) {
  if (stats == nullptr) {
    return -1;
  }
  memset(stats, 0, sizeof(*stats));
  stats->bytes_intended = static_cast<uint32_t>(length);
  if (chunk_size == 0 || write_fn == nullptr || now_ms == nullptr || (length > 0 && data == nullptr)) {
    stats->error_code = EINVAL;
    return -1;
  }

  const uint32_t started = now_ms(clock_ctx);
  stats->started_at_ms = started;
  size_t offset = 0;
  while (offset < length) {
    size_t amount = length - offset;
    if (amount > chunk_size) {
      amount = chunk_size;
    }
    const uint8_t* source = data + offset;
    if (stage != nullptr) {
      memcpy(stage, source, amount);
      source = stage;
    }
    size_t sent = 0;
    while (sent < amount) {
      BodyChunk chunk{source + sent, amount - sent};
      int error_code = 0;
      int timed_out = 0;
      const uint32_t chunk_started = now_ms(clock_ctx);
      const int wrote = write_fn(write_ctx, &chunk, &error_code, &timed_out);
      const uint32_t elapsed = now_ms(clock_ctx) - chunk_started;
      if (elapsed > stats->max_chunk_ms) {
        stats->max_chunk_ms = elapsed;
      }
      if (wrote <= 0 || static_cast<size_t>(wrote) > chunk.length) {
        stats->error_code = error_code;
        stats->timed_out = timed_out ? 1 : 0;
        stats->body_send_ms = now_ms(clock_ctx) - started;
        return -1;
      }
      stats->bytes_written += static_cast<uint32_t>(wrote);
      stats->chunk_count += 1;
      sent += static_cast<size_t>(wrote);
    }
    offset += amount;
  }

  if (finish) {
    BodyChunk end{nullptr, 0};
    int error_code = 0;
    int timed_out = 0;
    const uint32_t end_started = now_ms(clock_ctx);
    const int wrote = write_fn(write_ctx, &end, &error_code, &timed_out);
    const uint32_t end_elapsed = now_ms(clock_ctx) - end_started;
    if (end_elapsed > stats->max_chunk_ms) {
      stats->max_chunk_ms = end_elapsed;
    }
    if (wrote < 0) {
      stats->error_code = error_code;
      stats->timed_out = timed_out ? 1 : 0;
      stats->body_send_ms = now_ms(clock_ctx) - started;
      return -1;
    }
  }
  stats->body_send_ms = now_ms(clock_ctx) - started;
  stats->complete = stats->bytes_written == stats->bytes_intended ? 1 : 0;
  return stats->complete ? 0 : -1;
}

// Free the owned image whether the socket accepted the last byte or not.
// capture_error is intentionally not an argument: a transport failure must
// not replace the camera result.
inline int send_owned_body(
  uint8_t** data,
  size_t length,
  size_t chunk_size,
  uint8_t* stage,
  int finish,
  BodyWriteFn write_fn,
  void* write_ctx,
  BodyClockFn now_ms,
  void* clock_ctx,
  BodySendStats* stats,
  const char** transport_error
) {
  const uint8_t* bytes = data == nullptr ? nullptr : *data;
  const int rc = body_send(bytes, length, chunk_size, stage, finish, write_fn, write_ctx, now_ms, clock_ctx, stats);
  if (transport_error != nullptr && stats != nullptr) {
    *transport_error = transport_error_name(*stats);
  }
  if (data != nullptr && *data != nullptr) {
    free(*data);
    *data = nullptr;
  }
  return rc;
}
