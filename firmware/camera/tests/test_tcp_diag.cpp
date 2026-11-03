// Host checks for the raw TCP diagnostic sender. This file is not firmware.
#include "tcp_diag.h"

#include <cstdio>
#include <cstring>
#include <vector>

namespace {

int g_failures = 0;

void check(bool condition, const char* text, int line) {
  if (!condition) {
    std::fprintf(stderr, "FAIL %s:%d %s\n", "test_tcp_diag.cpp", line, text);
    g_failures += 1;
  }
}

#define CHECK(condition) check(static_cast<bool>(condition), #condition, __LINE__)

struct Clock {
  uint32_t now;
};

uint32_t advance(void* ctx) {
  Clock* clock = static_cast<Clock*>(ctx);
  const uint32_t value = clock->now;
  clock->now += 2;
  return value;
}

struct Sink {
  std::vector<uint8_t> received;
  std::vector<size_t> lengths;
  int limit;
  int fail_at;
  int fail_code;
  int fail_timeout;
  int writes;

  Sink() : limit(0), fail_at(-1), fail_code(0), fail_timeout(0), writes(0) {}
};

int write_sink(void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out) {
  Sink* sink = static_cast<Sink*>(ctx);
  sink->writes += 1;
  if (chunk->data == nullptr || chunk->length == 0) {
    *error_code = EIO;
    return -1;
  }
  if (sink->fail_at >= 0 && static_cast<int>(sink->received.size()) >= sink->fail_at) {
    *error_code = sink->fail_code;
    *timed_out = sink->fail_timeout;
    return -1;
  }
  size_t amount = chunk->length;
  if (sink->limit > 0 && amount > static_cast<size_t>(sink->limit)) {
    amount = static_cast<size_t>(sink->limit);
  }
  if (sink->fail_at >= 0) {
    const size_t room = static_cast<size_t>(sink->fail_at) - sink->received.size();
    if (amount > room) {
      amount = room;
    }
  }
  if (amount == 0) {
    *error_code = EPIPE;
    return -1;
  }
  sink->received.insert(sink->received.end(), chunk->data, chunk->data + amount);
  sink->lengths.push_back(amount);
  return static_cast<int>(amount);
}

void test_production_default_is_off() {
  CHECK(SMARTLINE_TCP_DIAG == 0);
  CHECK(SMARTLINE_TCP_DIAG_PORT == 9001);
  CHECK(SMARTLINE_TCP_DIAG_BYTES == 32768);
  CHECK(SMARTLINE_HTTP_CHUNK_BYTES == 2048);
}

void test_full_payload_uses_http_chunk_size() {
  std::vector<uint8_t> data(SMARTLINE_TCP_DIAG_BYTES);
  tcp_diag_fill(data.data(), data.size());
  CHECK(data[0] == 0);
  CHECK(data[255] == 255);
  CHECK(data[256] == 0);
  CHECK(data.back() == static_cast<uint8_t>((SMARTLINE_TCP_DIAG_BYTES - 1) & 0xFF));

  Sink sink;
  Clock clock{10};
  BodySendStats stats = {};
  const int rc = tcp_diag_send(
    data.data(),
    data.size(),
    SMARTLINE_HTTP_CHUNK_BYTES,
    write_sink,
    &sink,
    advance,
    &clock,
    &stats
  );
  CHECK(rc == 0);
  CHECK(stats.complete == 1);
  CHECK(stats.bytes_intended == 32768);
  CHECK(stats.bytes_written == 32768);
  CHECK(stats.chunk_count == 16);
  CHECK(sink.lengths.size() == 16);
  CHECK(sink.lengths.front() == 2048);
  CHECK(sink.lengths.back() == 2048);
  CHECK(sink.received == data);
  CHECK(stats.max_chunk_ms > 0);
  CHECK(stats.body_send_ms > 0);
  CHECK(transport_error_name(stats)[0] == '\0');
}

void test_partial_writes_continue() {
  // Each 2048-byte slice is written on its own. A 500-byte accept therefore
  // finishes the slice as 500+500+500+500+48, not as one stream of 500s.
  // Sixteen slices are 64 full writes and 16 tails.
  std::vector<uint8_t> data(32768);
  tcp_diag_fill(data.data(), data.size());
  Sink sink;
  sink.limit = 500;
  Clock clock{0};
  BodySendStats stats = {};
  const int rc = tcp_diag_send(data.data(), data.size(), 2048, write_sink, &sink, advance, &clock, &stats);
  CHECK(rc == 0);
  CHECK(stats.complete == 1);
  CHECK(stats.bytes_written == 32768);
  CHECK(stats.chunk_count == 80);
  CHECK(sink.lengths.size() == 80);
  size_t full = 0;
  size_t tails = 0;
  for (size_t length : sink.lengths) {
    if (length == 500) {
      full += 1;
    } else if (length == 48) {
      tails += 1;
    }
  }
  CHECK(full == 64);
  CHECK(tails == 16);
  CHECK(sink.lengths.back() == 48);
  CHECK(sink.received == data);
}

void test_disconnect_does_not_claim_success() {
  std::vector<uint8_t> data(32768);
  tcp_diag_fill(data.data(), data.size());
  Sink sink;
  sink.fail_at = 4096;
  sink.fail_code = EPIPE;
  Clock clock{0};
  BodySendStats stats = {};
  const int rc = tcp_diag_send(data.data(), data.size(), 2048, write_sink, &sink, advance, &clock, &stats);
  CHECK(rc != 0);
  CHECK(stats.complete == 0);
  CHECK(stats.bytes_written == 4096);
  CHECK(stats.bytes_intended == 32768);
  CHECK(std::strcmp(transport_error_name(stats), "client_disconnected") == 0);
  CHECK(sink.received.size() == 4096);
}

void test_timeout_is_labeled() {
  std::vector<uint8_t> data(2048, 1);
  Sink sink;
  sink.fail_at = 0;
  sink.fail_code = EAGAIN;
  sink.fail_timeout = 1;
  Clock clock{0};
  BodySendStats stats = {};
  const int rc = tcp_diag_send(data.data(), data.size(), 2048, write_sink, &sink, advance, &clock, &stats);
  CHECK(rc != 0);
  CHECK(stats.timed_out == 1);
  CHECK(stats.bytes_written == 0);
  CHECK(std::strcmp(transport_error_name(stats), "send_timeout") == 0);
}

void test_zero_chunk_is_rejected() {
  uint8_t byte = 1;
  Sink sink;
  Clock clock{0};
  BodySendStats stats = {};
  const int rc = tcp_diag_send(&byte, 1, 0, write_sink, &sink, advance, &clock, &stats);
  CHECK(rc != 0);
  CHECK(stats.error_code == EINVAL);
  CHECK(sink.writes == 0);
}

}  // namespace

int main() {
  test_production_default_is_off();
  test_full_payload_uses_http_chunk_size();
  test_partial_writes_continue();
  test_disconnect_does_not_claim_success();
  test_timeout_is_labeled();
  test_zero_chunk_is_rejected();
  if (g_failures != 0) {
    std::fprintf(stderr, "%d tcp diagnostic check(s) failed\n", g_failures);
    return 1;
  }
  std::printf("tcp diagnostic checks passed\n");
  return 0;
}
