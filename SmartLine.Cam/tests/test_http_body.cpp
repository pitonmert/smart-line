// Host-side checks for the chunked body sender. This file is not firmware.
#include "http_body.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <atomic>
#include <thread>
#include <vector>

namespace {

int g_failures = 0;

void check(bool condition, const char* text, int line) {
  if (!condition) {
    std::fprintf(stderr, "FAIL %s:%d %s\n", "test_http_body.cpp", line, text);
    g_failures += 1;
  }
}

#define CHECK(condition) check(static_cast<bool>(condition), #condition, __LINE__)

struct Clock {
  uint32_t now;
  uint32_t step;
};

uint32_t advance(void* ctx) {
  Clock* clock = static_cast<Clock*>(ctx);
  const uint32_t value = clock->now;
  clock->now += clock->step;
  return value;
}

struct Sink {
  std::vector<uint8_t> received;
  std::vector<size_t> lengths;
  const uint8_t* stage;
  int limit;
  int fail_at;
  int fail_code;
  int fail_timeout;
  int oversize;
  int zero_data;
  bool saw_terminator;
  int writes;

  Sink()
    : stage(nullptr),
      limit(0),
      fail_at(-1),
      fail_code(0),
      fail_timeout(0),
      oversize(0),
      zero_data(0),
      saw_terminator(false),
      writes(0) {}
};

int write_sink(void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out) {
  Sink* sink = static_cast<Sink*>(ctx);
  sink->writes += 1;
  if (chunk->data == nullptr && chunk->length == 0) {
    sink->saw_terminator = true;
    return 0;
  }
  if (sink->oversize) {
    return static_cast<int>(chunk->length) + 1;
  }
  if (sink->zero_data) {
    *error_code = EIO;
    return 0;
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
  if (sink->stage != nullptr) {
    CHECK(chunk->data == sink->stage || (chunk->data > sink->stage && chunk->data < sink->stage + SMARTLINE_HTTP_CHUNK_BYTES));
  }
  sink->received.insert(sink->received.end(), chunk->data, chunk->data + amount);
  sink->lengths.push_back(amount);
  return static_cast<int>(amount);
}

BodySendStats send_of(const std::vector<uint8_t>& data, size_t chunk_size, uint8_t* stage, int finish, Sink* sink, Clock* clock) {
  BodySendStats stats = {};
  body_send(data.data(), data.size(), chunk_size, stage, finish, write_sink, sink, advance, clock, &stats);
  return stats;
}

void test_exact_chunks() {
  std::vector<uint8_t> data(5000);
  for (size_t index = 0; index < data.size(); index++) {
    data[index] = static_cast<uint8_t>(index & 0xFF);
  }
  Sink sink = {};
  Clock clock{1000, 3};
  const BodySendStats stats = send_of(data, 2048, nullptr, 1, &sink, &clock);
  CHECK(stats.complete == 1);
  CHECK(stats.bytes_intended == 5000);
  CHECK(stats.bytes_written == 5000);
  CHECK(stats.chunk_count == 3);
  CHECK(stats.started_at_ms == 1000);
  CHECK(stats.body_send_ms > 0);
  CHECK(stats.max_chunk_ms > 0);
  CHECK(sink.saw_terminator);
  CHECK(sink.lengths.size() == 3);
  CHECK(sink.lengths[0] == 2048);
  CHECK(sink.lengths[1] == 2048);
  CHECK(sink.lengths[2] == 904);
  CHECK(sink.received == data);
}

void test_partial_writes_are_counted() {
  std::vector<uint8_t> data(25, 7);
  Sink sink = {};
  sink.limit = 10;
  Clock clock{0, 1};
  const BodySendStats stats = send_of(data, 2048, nullptr, 1, &sink, &clock);
  CHECK(stats.complete == 1);
  CHECK(stats.bytes_written == 25);
  CHECK(stats.chunk_count == 3);
  CHECK(sink.lengths[0] == 10);
  CHECK(sink.lengths[1] == 10);
  CHECK(sink.lengths[2] == 5);
  CHECK(sink.saw_terminator);
}

void test_disconnect_stops_before_the_terminator() {
  std::vector<uint8_t> data(100, 9);
  Sink sink = {};
  sink.limit = 10;
  sink.fail_at = 30;
  sink.fail_code = EPIPE;
  Clock clock{50, 4};
  uint8_t* owned = static_cast<uint8_t*>(std::malloc(data.size()));
  CHECK(owned != nullptr);
  std::memcpy(owned, data.data(), data.size());
  const char* capture_error = "still_ok";
  const char* transport_error = "unset";
  BodySendStats stats = {};
  const int rc = send_owned_body(
    &owned,
    data.size(),
    2048,
    nullptr,
    1,
    write_sink,
    &sink,
    advance,
    &clock,
    &stats,
    &transport_error
  );
  CHECK(rc != 0);
  CHECK(owned == nullptr);
  CHECK(std::strcmp(capture_error, "still_ok") == 0);
  CHECK(std::strcmp(transport_error, "client_disconnected") == 0);
  CHECK(stats.complete == 0);
  CHECK(stats.bytes_written == 30);
  CHECK(stats.bytes_intended == 100);
  CHECK(!sink.saw_terminator);
  CHECK(stats.timed_out == 0);
  CHECK(stats.error_code == EPIPE);
}

void test_timeout_wins_over_the_socket_name() {
  Sink sink = {};
  sink.fail_at = 0;
  sink.fail_code = EAGAIN;
  sink.fail_timeout = 1;
  Clock clock{1, 2};
  std::vector<uint8_t> data(8, 1);
  const BodySendStats stats = send_of(data, 2048, nullptr, 1, &sink, &clock);
  CHECK(stats.complete == 0);
  CHECK(stats.timed_out == 1);
  CHECK(std::strcmp(transport_error_name(stats), "send_timeout") == 0);
  CHECK(!sink.saw_terminator);
}

void test_reset_and_not_connected_are_disconnects() {
  BodySendStats reset = {};
  reset.error_code = ECONNRESET;
  CHECK(std::strcmp(transport_error_name(reset), "client_disconnected") == 0);
  BodySendStats closed = {};
  closed.error_code = ENOTCONN;
  CHECK(std::strcmp(transport_error_name(closed), "client_disconnected") == 0);
  BodySendStats other = {};
  other.error_code = EIO;
  CHECK(std::strcmp(transport_error_name(other), "send_failed") == 0);
  BodySendStats done = {};
  done.complete = 1;
  done.error_code = EPIPE;
  CHECK(transport_error_name(done)[0] == '\0');
}

void test_rejected_writes_do_not_claim_success() {
  std::vector<uint8_t> data(16, 3);
  Clock clock{0, 1};
  Sink over = {};
  over.oversize = 1;
  const BodySendStats over_stats = send_of(data, 8, nullptr, 1, &over, &clock);
  CHECK(over_stats.complete == 0);
  CHECK(over_stats.bytes_written == 0);
  CHECK(!over.saw_terminator);

  Sink zero = {};
  zero.zero_data = 1;
  const BodySendStats zero_stats = send_of(data, 8, nullptr, 1, &zero, &clock);
  CHECK(zero_stats.complete == 0);
  CHECK(std::strcmp(transport_error_name(zero_stats), "send_failed") == 0);
  CHECK(!zero.saw_terminator);

  BodySendStats bad = {};
  Sink unused = {};
  const int rc = body_send(data.data(), data.size(), 0, nullptr, 1, write_sink, &unused, advance, &clock, &bad);
  CHECK(rc != 0);
  CHECK(bad.error_code == EINVAL);
  CHECK(unused.writes == 0);
  CHECK(body_send(nullptr, 4, 8, nullptr, 1, write_sink, &unused, advance, &clock, &bad) != 0);
  CHECK(body_send(data.data(), data.size(), 8, nullptr, 1, nullptr, &unused, advance, &clock, &bad) != 0);
  CHECK(body_send(data.data(), data.size(), 8, nullptr, 1, write_sink, &unused, advance, &clock, nullptr) != 0);
}

void test_stage_copies_before_the_write() {
  std::vector<uint8_t> data{1, 2, 3, 4, 5};
  uint8_t stage[8];
  std::memset(stage, 0xAB, sizeof(stage));
  Sink sink = {};
  sink.stage = stage;
  Clock clock{10, 1};
  const BodySendStats stats = send_of(data, 2048, stage, 1, &sink, &clock);
  CHECK(stats.complete == 1);
  CHECK(sink.received == data);
  CHECK(std::memcmp(stage, data.data(), data.size()) == 0);
}

struct OverlapGate {
  std::atomic<int> arrivals{0};
};

struct OverlapSink {
  OverlapGate* gate;
  std::vector<uint8_t> received;
  bool waited;
};

int write_overlapping_sink(void* ctx, const BodyChunk* chunk, int* /*error_code*/, int* /*timed_out*/) {
  OverlapSink* sink = static_cast<OverlapSink*>(ctx);
  if (chunk->data == nullptr && chunk->length == 0) {
    return 0;
  }
  // Both sends copy their first chunk before either sink reads it. A shared
  // staging buffer would therefore make at least one response contain the
  // other response's bytes.
  if (!sink->waited) {
    sink->waited = true;
    sink->gate->arrivals.fetch_add(1, std::memory_order_release);
    while (sink->gate->arrivals.load(std::memory_order_acquire) != 2) {
      std::this_thread::yield();
    }
  }
  sink->received.insert(sink->received.end(), chunk->data, chunk->data + chunk->length);
  return static_cast<int>(chunk->length);
}

void test_overlapping_staged_stream_and_still_keep_their_bytes() {
  std::vector<uint8_t> stream(5000);
  std::vector<uint8_t> still(5000);
  for (size_t index = 0; index < stream.size(); index++) {
    stream[index] = static_cast<uint8_t>(index & 0xFF);
    still[index] = static_cast<uint8_t>(0xFF - (index & 0xFF));
  }
  uint8_t stream_stage[SMARTLINE_HTTP_CHUNK_BYTES];
  uint8_t still_stage[SMARTLINE_HTTP_CHUNK_BYTES];
  OverlapGate gate = {};
  OverlapSink stream_sink{&gate, {}, false};
  OverlapSink still_sink{&gate, {}, false};
  BodySendStats stream_stats = {};
  BodySendStats still_stats = {};
  Clock stream_clock{0, 1};
  Clock still_clock{0, 1};

  std::thread stream_send([&]() {
    body_send(
      stream.data(), stream.size(), SMARTLINE_HTTP_CHUNK_BYTES, stream_stage, 1,
      write_overlapping_sink, &stream_sink, advance, &stream_clock, &stream_stats
    );
  });
  std::thread still_send([&]() {
    body_send(
      still.data(), still.size(), SMARTLINE_HTTP_CHUNK_BYTES, still_stage, 1,
      write_overlapping_sink, &still_sink, advance, &still_clock, &still_stats
    );
  });
  stream_send.join();
  still_send.join();

  CHECK(stream_stats.complete == 1);
  CHECK(still_stats.complete == 1);
  CHECK(stream_sink.received == stream);
  CHECK(still_sink.received == still);
}

void test_open_stream_does_not_end_the_response() {
  std::vector<uint8_t> data(3000, 4);
  Sink sink = {};
  Clock clock{0, 1};
  const BodySendStats stats = send_of(data, 2048, nullptr, 0, &sink, &clock);
  CHECK(stats.complete == 1);
  CHECK(stats.chunk_count == 2);
  CHECK(!sink.saw_terminator);
  CHECK(sink.received == data);
}

void test_terminator_failure_is_not_success() {
  std::vector<uint8_t> data(4, 8);
  Sink sink = {};
  Clock clock{0, 1};
  auto finish_fail = [](void* ctx, const BodyChunk* chunk, int* error_code, int* timed_out) -> int {
    if (chunk->length == 0) {
      static_cast<Sink*>(ctx)->saw_terminator = true;
      *error_code = ECONNRESET;
      *timed_out = 0;
      return -1;
    }
    return write_sink(ctx, chunk, error_code, timed_out);
  };
  BodySendStats stats = {};
  const int rc = body_send(data.data(), data.size(), 2048, nullptr, 1, finish_fail, &sink, advance, &clock, &stats);
  CHECK(rc != 0);
  CHECK(stats.bytes_written == 4);
  CHECK(stats.complete == 0);
  CHECK(sink.saw_terminator);
  CHECK(std::strcmp(transport_error_name(stats), "client_disconnected") == 0);
}

void test_success_frees_the_owned_buffer() {
  uint8_t* owned = static_cast<uint8_t*>(std::malloc(4));
  owned[0] = 1;
  owned[1] = 2;
  owned[2] = 3;
  owned[3] = 4;
  Sink sink = {};
  Clock clock{8, 1};
  const char* transport_error = "unset";
  BodySendStats stats = {};
  const int rc = send_owned_body(&owned, 4, 1024, nullptr, 1, write_sink, &sink, advance, &clock, &stats, &transport_error);
  CHECK(rc == 0);
  CHECK(owned == nullptr);
  CHECK(stats.complete == 1);
  CHECK(transport_error[0] == '\0');
}

void test_allowed_throughput_sizes() {
  CHECK(throughput_bytes_ok(1024));
  CHECK(throughput_bytes_ok(8192));
  CHECK(throughput_bytes_ok(32768));
  CHECK(throughput_bytes_ok(65536));
  CHECK(!throughput_bytes_ok(0));
  CHECK(!throughput_bytes_ok(2048));
  CHECK(!throughput_bytes_ok(1023));
  CHECK(!throughput_bytes_ok(65537));
  CHECK(!throughput_bytes_ok(-1));
}

}  // namespace

int main() {
  test_exact_chunks();
  test_partial_writes_are_counted();
  test_disconnect_stops_before_the_terminator();
  test_timeout_wins_over_the_socket_name();
  test_reset_and_not_connected_are_disconnects();
  test_rejected_writes_do_not_claim_success();
  test_stage_copies_before_the_write();
  test_overlapping_staged_stream_and_still_keep_their_bytes();
  test_open_stream_does_not_end_the_response();
  test_terminator_failure_is_not_success();
  test_success_frees_the_owned_buffer();
  test_allowed_throughput_sizes();
  if (g_failures != 0) {
    std::fprintf(stderr, "%d http body check(s) failed\n", g_failures);
    return 1;
  }
  std::printf("http body checks passed\n");
  return 0;
}
