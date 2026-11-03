#pragma once

// Development-only raw TCP payload sender. No ESP-IDF types.
//
// The normal firmware build leaves SMARTLINE_TCP_DIAG at 0, so port 9001 is
// not opened. A development build defines SMARTLINE_TCP_DIAG=1 and sends
// exactly SMARTLINE_TCP_DIAG_BYTES from internal RAM with the same 2048-byte
// write size the HTTP body uses. There is no HTTP framing and no second copy.
// That is a comparison, not a measured speedup.

#include "http_body.h"

#ifndef SMARTLINE_TCP_DIAG
#define SMARTLINE_TCP_DIAG 0
#endif

#ifndef SMARTLINE_TCP_DIAG_PORT
#define SMARTLINE_TCP_DIAG_PORT 9001
#endif

#ifndef SMARTLINE_TCP_DIAG_BYTES
#define SMARTLINE_TCP_DIAG_BYTES 32768
#endif

static_assert(SMARTLINE_TCP_DIAG == 0 || SMARTLINE_TCP_DIAG == 1, "TCP diagnostic flag is 0 or 1");
static_assert(SMARTLINE_TCP_DIAG_PORT == 9001, "TCP diagnostic port is 9001");
static_assert(SMARTLINE_TCP_DIAG_BYTES == 32768, "TCP diagnostic payload is 32768 bytes");

inline void tcp_diag_fill(uint8_t* data, size_t length) {
  for (size_t index = 0; index < length; index++) {
    data[index] = static_cast<uint8_t>(index & 0xFF);
  }
}

// finish is 0: this is a raw socket, not an HTTP chunked body.
// A short positive write is progress. The caller keeps the buffer alive
// until this function returns.
inline int tcp_diag_send(
  const uint8_t* data,
  size_t length,
  size_t chunk_size,
  BodyWriteFn write_fn,
  void* write_ctx,
  BodyClockFn now_ms,
  void* clock_ctx,
  BodySendStats* stats
) {
  return body_send(data, length, chunk_size, nullptr, 0, write_fn, write_ctx, now_ms, clock_ctx, stats);
}

void tcp_diag_start();
