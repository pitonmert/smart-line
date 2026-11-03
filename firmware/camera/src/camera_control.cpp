#include "camera_control.h"

#include "http_transport.h"
#include "img_converters.h"

#include <Arduino.h>
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// frame2jpg uses the jpge scale. 1 is the smallest file and 100 is the sharpest.
// The live stream stays at 60. A still JPEG uses 85 so a manual snapshot stays
// sharp and well under the API's 900 KB image limit. Vision should prefer
// GET /capture/raw and compress on the host instead of waiting for this encoder.
static const int kStillJpegQuality = 85;

// esp32-camera f0bb429 (Arduino-ESP32 2.0.17) allocates fb_size once in
// cam_config. On the classic ESP32 it forces psram_mode false. A non-JPEG
// frame whose length is not exactly fb_size is dropped (FB-SIZE), and a
// longer frame stops the DMA copy (FB-OVF). GC2145 set_framesize only writes
// the sensor window. Allocating VGA and then calling set_framesize(HQVGA)
// makes every preview frame 84480 bytes against a 614400-byte expectation, so
// the stream would stall. The reverse overflows. Each still therefore
// deinitializes and initializes at the target size. That is the safe path.

// The driver already refuses a short RGB565 buffer, and deinit allocates a
// new one, so extra discards are not a size flush. One still discard lets
// exposure settle after the GC2145 register reload. The preview buffer is
// new, so no VGA bytes remain in it; the live stream settles the next frames.
// Both counts are compile-time constants in the range 0..2.
#ifndef SMARTLINE_STILL_STALE_FRAMES
#define SMARTLINE_STILL_STALE_FRAMES 1
#endif
#ifndef SMARTLINE_PREVIEW_STALE_FRAMES
#define SMARTLINE_PREVIEW_STALE_FRAMES 0
#endif

static_assert(SMARTLINE_STILL_STALE_FRAMES >= 0 && SMARTLINE_STILL_STALE_FRAMES <= 2, "still stale frames must be 0, 1, or 2");
static_assert(SMARTLINE_PREVIEW_STALE_FRAMES >= 0 && SMARTLINE_PREVIEW_STALE_FRAMES <= 2, "preview stale frames must be 0, 1, or 2");

// VGA RGB565 is 640 * 480 * 2 = 614400 bytes. That fits in PSRAM and does not
// fit safely beside Wi-Fi in internal DRAM. Without PSRAM the still path stays
// unavailable instead of claiming VGA.
#ifndef SMARTLINE_STILL_FRAMESIZE
#define SMARTLINE_STILL_FRAMESIZE FRAMESIZE_VGA
#endif

struct FrameSpec {
  framesize_t size;
  int width;
  int height;
  const char* name;
};

struct CaptureTimings {
  uint32_t mutexWaitMs;
  uint32_t stillDeinitMs;
  uint32_t stillInitMs;
  uint32_t stale1Ms;
  uint32_t stale2Ms;
  uint32_t frameAcquireMs;
  uint32_t jpegEncodeMs;
  uint32_t frameCopyMs;
  uint32_t previewDeinitMs;
  uint32_t previewInitMs;
  uint32_t previewStaleMs;
  uint32_t previewRestoreMs;
  uint32_t httpPrepMs;
  uint32_t totalMs;
};

struct StillResult {
  const char* error;
  uint8_t* data;
  size_t length;
  int width;
  int height;
  CaptureTimings timings;
};

static const FrameSpec kFrameSpecs[] = {
  {FRAMESIZE_HQVGA, 240, 176, "HQVGA"},
  {FRAMESIZE_QVGA, 320, 240, "QVGA"},
  {FRAMESIZE_VGA, 640, 480, "VGA"},
  {FRAMESIZE_SVGA, 800, 600, "SVGA"},
  {FRAMESIZE_XGA, 1024, 768, "XGA"},
  {FRAMESIZE_SXGA, 1280, 1024, "SXGA"},
  {FRAMESIZE_UXGA, 1600, 1200, "UXGA"},
};

static SemaphoreHandle_t g_mutex = nullptr;
static camera_config_t g_config = {};
static bool g_psram = false;
static uint16_t g_pid = 0;
static framesize_t g_preview_size = FRAMESIZE_HQVGA;
static framesize_t g_still_size = SMARTLINE_STILL_FRAMESIZE;
static int g_preview_width = 240;
static int g_preview_height = 176;
static int g_still_width = 0;
static int g_still_height = 0;
static bool g_still_available = false;
static char g_still_name[16] = "";
static char g_last_error[48] = "";
static char g_capture_path[8] = "";
static uint32_t g_last_still_ms = 0;
static int g_last_still_width = 0;
static int g_last_still_height = 0;
static bool g_camera_ready = false;
static CaptureTimings g_timings = {};

struct TransportRecord {
  char error[32];
  char path[8];
  int error_code;
  int esp_status;
  int timed_out;
  uint32_t bytes_intended;
  uint32_t bytes_written;
  uint32_t send_ms;
  uint32_t started_at_ms;
  uint32_t chunk_count;
  uint32_t max_chunk_ms;
  uint32_t stream_capture_ms;
  uint32_t stream_jpeg_ms;
  uint32_t stream_jpeg_bytes;
  uint32_t stream_send_ms;
  uint32_t stream_frames;
  uint32_t stream_failures;
};

static TransportRecord g_transport = {};

static const FrameSpec* find_spec(framesize_t size) {
  for (const FrameSpec& spec : kFrameSpecs) {
    if (spec.size == size) {
      return &spec;
    }
  }
  return nullptr;
}

static uint32_t ms_since(int64_t started) {
  const int64_t delta = esp_timer_get_time() - started;
  if (delta <= 0) {
    return 0;
  }
  return static_cast<uint32_t>((delta + 500) / 1000);
}

static void set_error(const char* code) {
  strncpy(g_last_error, code, sizeof(g_last_error) - 1);
  g_last_error[sizeof(g_last_error) - 1] = '\0';
}

static void clear_error() {
  g_last_error[0] = '\0';
}

static void publish_timings(const CaptureTimings& timings, const char* path) {
  g_timings = timings;
  g_timings.previewRestoreMs = timings.previewDeinitMs + timings.previewInitMs + timings.previewStaleMs;
  strncpy(g_capture_path, path, sizeof(g_capture_path) - 1);
  g_capture_path[sizeof(g_capture_path) - 1] = '\0';
}

static uint8_t* alloc_owned(size_t length) {
  if (length >= 64 * 1024) {
    uint8_t* psram = static_cast<uint8_t*>(heap_caps_malloc(length, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    if (psram != nullptr) {
      return psram;
    }
  }
  return static_cast<uint8_t*>(malloc(length));
}

static bool init_at(framesize_t size, const char** error_code, uint32_t* deinit_ms, uint32_t* init_ms) {
  const int64_t deinit_started = esp_timer_get_time();
  esp_camera_deinit();
  *deinit_ms += ms_since(deinit_started);
  g_config.frame_size = size;
  g_config.pixel_format = PIXFORMAT_RGB565;
  g_config.fb_count = 1;
  g_config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  g_config.fb_location = g_psram ? CAMERA_FB_IN_PSRAM : CAMERA_FB_IN_DRAM;
  g_camera_ready = false;

  const int64_t init_started = esp_timer_get_time();
  const esp_err_t err = esp_camera_init(&g_config);
  if (err != ESP_OK) {
    *init_ms += ms_since(init_started);
    Serial.printf("[CAM] Yeniden baslatma hatasi: 0x%x\n", err);
    *error_code = "camera_init_failed";
    return false;
  }

  sensor_t* sensor = esp_camera_sensor_get();
  if (sensor != nullptr) {
    g_pid = sensor->id.PID;
    sensor->set_vflip(sensor, 0);
    sensor->set_hmirror(sensor, 0);
    sensor->set_brightness(sensor, 0);
    sensor->set_contrast(sensor, 0);
    sensor->set_saturation(sensor, 0);
  }
  *init_ms += ms_since(init_started);

  g_camera_ready = true;
  return true;
}

static bool restore_preview(uint32_t* deinit_ms, uint32_t* init_ms) {
  const char* error = nullptr;
  if (init_at(g_preview_size, &error, deinit_ms, init_ms)) {
    return true;
  }
  if (init_at(g_preview_size, &error, deinit_ms, init_ms)) {
    return true;
  }
  set_error("preview_restore_failed");
  Serial.println("[CAM] Onizleme cozunurlugu geri yuklenemedi.");
  return false;
}

static bool discard_stale(int count, uint32_t* first_ms, uint32_t* second_ms) {
  *first_ms = 0;
  *second_ms = 0;
  for (int index = 0; index < count; index++) {
    const int64_t started = esp_timer_get_time();
    camera_fb_t* frame = esp_camera_fb_get();
    const uint32_t elapsed = ms_since(started);
    if (index == 0) {
      *first_ms = elapsed;
    } else {
      *second_ms += elapsed;
    }
    if (frame == nullptr) {
      return false;
    }
    esp_camera_fb_return(frame);
  }
  return true;
}

static esp_err_t send_json(httpd_req_t* request, const char* status, const char* body) {
  httpd_resp_set_status(request, status);
  httpd_resp_set_type(request, "application/json");
  httpd_resp_set_hdr(request, "Cache-Control", "no-store");
  return httpd_resp_send(request, body, HTTPD_RESP_USE_STRLEN);
}

static void write_status(char* buffer, size_t length) {
  const char* sensor_name = g_pid == GC2145_PID ? "GC2145" : "unknown";
  char quoted_error[64];
  char quoted_transport[64];
  if (g_last_error[0] == '\0') {
    snprintf(quoted_error, sizeof(quoted_error), "null");
  } else {
    snprintf(quoted_error, sizeof(quoted_error), "\"%s\"", g_last_error);
  }
  if (g_transport.error[0] == '\0') {
    snprintf(quoted_transport, sizeof(quoted_transport), "null");
  } else {
    snprintf(quoted_transport, sizeof(quoted_transport), "\"%s\"", g_transport.error);
  }

  const int written = snprintf(
    buffer,
    length,
    "{\"sensorPid\":\"0x%04X\",\"sensorName\":\"%s\",\"psramDetected\":%s,"
    "\"previewWidth\":%d,\"previewHeight\":%d,\"previewFrameSize\":\"HQVGA\","
    "\"configuredStillWidth\":%d,\"configuredStillHeight\":%d,"
    "\"configuredStillFrameSize\":\"%s\",\"stillAvailable\":%s,"
    "\"lastStillAtMs\":%lu,\"lastStillWidth\":%d,\"lastStillHeight\":%d,"
    "\"lastCaptureError\":%s,\"cameraReady\":%s,\"lastCapturePath\":\"%s\","
    "\"lastMutexWaitMs\":%lu,\"lastStillDeinitMs\":%lu,\"lastStillInitMs\":%lu,"
    "\"lastStaleFrame1Ms\":%lu,\"lastStaleFrame2Ms\":%lu,\"lastFrameAcquireMs\":%lu,"
    "\"lastJpegEncodeMs\":%lu,\"lastFrameCopyMs\":%lu,"
    "\"lastPreviewDeinitMs\":%lu,\"lastPreviewInitMs\":%lu,\"lastPreviewStaleMs\":%lu,"
    "\"lastPreviewRestoreMs\":%lu,\"lastHttpPrepMs\":%lu,\"lastCaptureTotalMs\":%lu,"
    "\"stillStaleFrames\":%d,\"previewStaleFrames\":%d,"
    "\"lastTransportError\":%s,\"lastTransportErrno\":%d,\"lastTransportEspErr\":%d,"
    "\"lastTransportTimeout\":%s,\"lastBodyStartedAtMs\":%lu,\"lastBodyBytes\":%lu,\"lastBodyWritten\":%lu,"
    "\"lastBodySendMs\":%lu,\"lastBodyChunkCount\":%lu,\"lastBodyMaxChunkMs\":%lu,"
    "\"lastBodyChunkBytes\":%d,\"lastTransportPath\":\"%s\","
    "\"lastStreamCaptureMs\":%lu,\"lastStreamJpegMs\":%lu,\"lastStreamJpegBytes\":%lu,"
    "\"lastStreamSendMs\":%lu,\"lastStreamFrames\":%lu,\"lastStreamSendFailures\":%lu}",
    g_pid,
    sensor_name,
    g_psram ? "true" : "false",
    g_preview_width,
    g_preview_height,
    g_still_available ? g_still_width : 0,
    g_still_available ? g_still_height : 0,
    g_still_available ? g_still_name : "",
    g_still_available ? "true" : "false",
    static_cast<unsigned long>(g_last_still_ms),
    g_last_still_width,
    g_last_still_height,
    quoted_error,
    g_camera_ready ? "true" : "false",
    g_capture_path,
    static_cast<unsigned long>(g_timings.mutexWaitMs),
    static_cast<unsigned long>(g_timings.stillDeinitMs),
    static_cast<unsigned long>(g_timings.stillInitMs),
    static_cast<unsigned long>(g_timings.stale1Ms),
    static_cast<unsigned long>(g_timings.stale2Ms),
    static_cast<unsigned long>(g_timings.frameAcquireMs),
    static_cast<unsigned long>(g_timings.jpegEncodeMs),
    static_cast<unsigned long>(g_timings.frameCopyMs),
    static_cast<unsigned long>(g_timings.previewDeinitMs),
    static_cast<unsigned long>(g_timings.previewInitMs),
    static_cast<unsigned long>(g_timings.previewStaleMs),
    static_cast<unsigned long>(g_timings.previewRestoreMs),
    static_cast<unsigned long>(g_timings.httpPrepMs),
    static_cast<unsigned long>(g_timings.totalMs),
    SMARTLINE_STILL_STALE_FRAMES,
    SMARTLINE_PREVIEW_STALE_FRAMES,
    quoted_transport,
    g_transport.error_code,
    g_transport.esp_status,
    g_transport.timed_out ? "true" : "false",
    static_cast<unsigned long>(g_transport.started_at_ms),
    static_cast<unsigned long>(g_transport.bytes_intended),
    static_cast<unsigned long>(g_transport.bytes_written),
    static_cast<unsigned long>(g_transport.send_ms),
    static_cast<unsigned long>(g_transport.chunk_count),
    static_cast<unsigned long>(g_transport.max_chunk_ms),
    SMARTLINE_HTTP_CHUNK_BYTES,
    g_transport.path,
    static_cast<unsigned long>(g_transport.stream_capture_ms),
    static_cast<unsigned long>(g_transport.stream_jpeg_ms),
    static_cast<unsigned long>(g_transport.stream_jpeg_bytes),
    static_cast<unsigned long>(g_transport.stream_send_ms),
    static_cast<unsigned long>(g_transport.stream_frames),
    static_cast<unsigned long>(g_transport.stream_failures)
  );
  if (written < 0 || static_cast<size_t>(written) >= length) {
    snprintf(buffer, length, "{\"error\":\"status_truncated\"}");
  }
}

struct TimingHeaderStorage {
  char mutex_ms[16];
  char still_deinit_ms[16];
  char still_init_ms[16];
  char stale1_ms[16];
  char stale2_ms[16];
  char acquire_ms[16];
  char jpeg_ms[16];
  char copy_ms[16];
  char preview_deinit_ms[16];
  char preview_init_ms[16];
  char preview_stale_ms[16];
  char preview_restore_ms[16];
  char http_prep_ms[16];
  char total_ms[16];
};

static void set_u32_header(httpd_req_t* request, const char* name, uint32_t value, char* storage, size_t storage_length) {
  snprintf(storage, storage_length, "%lu", static_cast<unsigned long>(value));
  httpd_resp_set_hdr(request, name, storage);
}

// httpd_resp_set_hdr keeps the value pointer. storage must live until the body send returns.
static void set_timing_headers(httpd_req_t* request, const CaptureTimings& timings, TimingHeaderStorage* storage) {
  set_u32_header(request, "X-Timing-Mutex-Ms", timings.mutexWaitMs, storage->mutex_ms, sizeof(storage->mutex_ms));
  set_u32_header(request, "X-Timing-Still-Deinit-Ms", timings.stillDeinitMs, storage->still_deinit_ms, sizeof(storage->still_deinit_ms));
  set_u32_header(request, "X-Timing-Still-Init-Ms", timings.stillInitMs, storage->still_init_ms, sizeof(storage->still_init_ms));
  set_u32_header(request, "X-Timing-Stale1-Ms", timings.stale1Ms, storage->stale1_ms, sizeof(storage->stale1_ms));
  set_u32_header(request, "X-Timing-Stale2-Ms", timings.stale2Ms, storage->stale2_ms, sizeof(storage->stale2_ms));
  set_u32_header(request, "X-Timing-Acquire-Ms", timings.frameAcquireMs, storage->acquire_ms, sizeof(storage->acquire_ms));
  set_u32_header(request, "X-Timing-Jpeg-Ms", timings.jpegEncodeMs, storage->jpeg_ms, sizeof(storage->jpeg_ms));
  set_u32_header(request, "X-Timing-Copy-Ms", timings.frameCopyMs, storage->copy_ms, sizeof(storage->copy_ms));
  set_u32_header(request, "X-Timing-Preview-Deinit-Ms", timings.previewDeinitMs, storage->preview_deinit_ms, sizeof(storage->preview_deinit_ms));
  set_u32_header(request, "X-Timing-Preview-Init-Ms", timings.previewInitMs, storage->preview_init_ms, sizeof(storage->preview_init_ms));
  set_u32_header(request, "X-Timing-Preview-Stale-Ms", timings.previewStaleMs, storage->preview_stale_ms, sizeof(storage->preview_stale_ms));
  set_u32_header(request, "X-Timing-Preview-Restore-Ms", timings.previewRestoreMs, storage->preview_restore_ms, sizeof(storage->preview_restore_ms));
  set_u32_header(request, "X-Timing-Http-Prep-Ms", timings.httpPrepMs, storage->http_prep_ms, sizeof(storage->http_prep_ms));
  set_u32_header(request, "X-Timing-Total-Ms", timings.totalMs, storage->total_ms, sizeof(storage->total_ms));
}

static StillResult prepare_locked(bool raw) {
  StillResult result = {};
  if (!g_still_available) {
    set_error("still_requires_psram");
    result.error = "still_requires_psram";
    return result;
  }

  const char* init_error = nullptr;
  if (!init_at(g_still_size, &init_error, &result.timings.stillDeinitMs, &result.timings.stillInitMs)) {
    set_error("still_init_failed");
    restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
    result.error = "still_init_failed";
    return result;
  }

  if (!discard_stale(SMARTLINE_STILL_STALE_FRAMES, &result.timings.stale1Ms, &result.timings.stale2Ms)) {
    set_error("still_frame_missing");
    restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
    result.error = "still_frame_missing";
    return result;
  }

  const int64_t acquire_started = esp_timer_get_time();
  camera_fb_t* frame = esp_camera_fb_get();
  result.timings.frameAcquireMs = ms_since(acquire_started);
  if (frame == nullptr) {
    set_error("still_frame_missing");
    restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
    result.error = "still_frame_missing";
    return result;
  }

  const int width = static_cast<int>(frame->width);
  const int height = static_cast<int>(frame->height);
  result.width = width;
  result.height = height;
  if (width != g_still_width || height != g_still_height) {
    esp_camera_fb_return(frame);
    set_error("still_size_mismatch");
    restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
    Serial.printf("[STILL] Boyut uyusmadi: %dx%d\n", width, height);
    result.error = "still_size_mismatch";
    return result;
  }

  const size_t raw_length = static_cast<size_t>(width) * static_cast<size_t>(height) * 2;
  if (raw && (frame->format != PIXFORMAT_RGB565 || frame->len != raw_length)) {
    const char* code = frame->format == PIXFORMAT_RGB565 ? "still_size_mismatch" : "still_format_unsupported";
    esp_camera_fb_return(frame);
    set_error(code);
    restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
    result.error = code;
    return result;
  }

  uint8_t* owned = nullptr;
  size_t owned_length = 0;
  if (raw) {
    const int64_t copy_started = esp_timer_get_time();
    owned = alloc_owned(raw_length);
    if (owned != nullptr) {
      memcpy(owned, frame->buf, raw_length);
      owned_length = raw_length;
    }
    result.timings.frameCopyMs = ms_since(copy_started);
    esp_camera_fb_return(frame);
    if (owned == nullptr) {
      set_error("still_copy_failed");
      restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
      result.error = "still_copy_failed";
      return result;
    }
  } else {
    uint8_t* jpeg = nullptr;
    size_t jpeg_length = 0;
    const bool native_jpeg = frame->format == PIXFORMAT_JPEG;
    bool converted = false;
    const int64_t encode_started = esp_timer_get_time();
    if (native_jpeg) {
      jpeg = frame->buf;
      jpeg_length = frame->len;
    } else {
      converted = frame2jpg(frame, kStillJpegQuality, &jpeg, &jpeg_length);
    }
    result.timings.jpegEncodeMs = ms_since(encode_started);
    if ((!native_jpeg && !converted) || jpeg == nullptr || jpeg_length == 0) {
      if (converted && jpeg != nullptr) {
        free(jpeg);
      }
      esp_camera_fb_return(frame);
      set_error("still_jpeg_failed");
      restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
      result.error = "still_jpeg_failed";
      return result;
    }

    const int64_t copy_started = esp_timer_get_time();
    owned = alloc_owned(jpeg_length);
    if (owned != nullptr) {
      memcpy(owned, jpeg, jpeg_length);
      owned_length = jpeg_length;
    }
    result.timings.frameCopyMs = ms_since(copy_started);
    if (converted) {
      free(jpeg);
    }
    esp_camera_fb_return(frame);
    if (owned == nullptr) {
      set_error("still_jpeg_failed");
      restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs);
      result.error = "still_jpeg_failed";
      return result;
    }
  }

  // The owned copy survives deinit. The driver buffer must not be sent.
  if (!restore_preview(&result.timings.previewDeinitMs, &result.timings.previewInitMs)) {
    free(owned);
    result.error = "preview_restore_failed";
    return result;
  }
  if (SMARTLINE_PREVIEW_STALE_FRAMES > 0) {
    uint32_t ignored = 0;
    if (!discard_stale(SMARTLINE_PREVIEW_STALE_FRAMES, &result.timings.previewStaleMs, &ignored)) {
      result.timings.previewStaleMs += ignored;
      Serial.println("[STILL] Onizleme eski kare alinamadi. HQVGA yine de geri yuklendi.");
    } else {
      result.timings.previewStaleMs += ignored;
    }
  }

  g_last_still_ms = millis();
  g_last_still_width = width;
  g_last_still_height = height;
  clear_error();
  result.data = owned;
  result.length = owned_length;
  return result;
}

static void remember_timings(const CaptureTimings& timings, const char* path) {
  if (camera_lock(1000)) {
    publish_timings(timings, path);
    camera_unlock();
  }
}

static void log_capture(const char* path, const StillResult& result, const BodySendStats* transport) {
  Serial.printf(
    "[STILL] %s %s %dx%d %u bayt. toplam %lu ms, mutex %lu, deinit %lu, init %lu, stale %lu/%lu, kare %lu, jpeg %lu, kopya %lu, geri %lu, govde %lu/%lu %lu ms errno %d.\n",
    path,
    result.error == nullptr ? "ok" : result.error,
    result.width,
    result.height,
    static_cast<unsigned>(result.length),
    static_cast<unsigned long>(result.timings.totalMs),
    static_cast<unsigned long>(result.timings.mutexWaitMs),
    static_cast<unsigned long>(result.timings.stillDeinitMs),
    static_cast<unsigned long>(result.timings.stillInitMs),
    static_cast<unsigned long>(result.timings.stale1Ms),
    static_cast<unsigned long>(result.timings.stale2Ms),
    static_cast<unsigned long>(result.timings.frameAcquireMs),
    static_cast<unsigned long>(result.timings.jpegEncodeMs),
    static_cast<unsigned long>(result.timings.frameCopyMs),
    static_cast<unsigned long>(result.timings.previewRestoreMs),
    transport == nullptr ? 0UL : static_cast<unsigned long>(transport->bytes_written),
    transport == nullptr ? 0UL : static_cast<unsigned long>(transport->bytes_intended),
    transport == nullptr ? 0UL : static_cast<unsigned long>(transport->body_send_ms),
    transport == nullptr ? 0 : transport->error_code
  );
}

static void queue_frame_headers(
  httpd_req_t* request,
  bool raw,
  const StillResult& result,
  char* width_header,
  char* height_header,
  char* pid_header,
  char* bytes_header,
  char* chunk_header
) {
  snprintf(width_header, 16, "%d", result.width);
  snprintf(height_header, 16, "%d", result.height);
  snprintf(pid_header, 16, "0x%04X", g_pid);
  snprintf(bytes_header, 16, "%u", static_cast<unsigned>(result.length));
  snprintf(chunk_header, 16, "%d", SMARTLINE_HTTP_CHUNK_BYTES);
  httpd_resp_set_type(request, raw ? "application/octet-stream" : "image/jpeg");
  httpd_resp_set_hdr(request, "Cache-Control", "no-store");
  httpd_resp_set_hdr(request, "X-Frame-Width", width_header);
  httpd_resp_set_hdr(request, "X-Frame-Height", height_header);
  httpd_resp_set_hdr(request, "X-Frame-Bytes", bytes_header);
  httpd_resp_set_hdr(request, "X-Body-Chunk-Bytes", chunk_header);
  httpd_resp_set_hdr(request, "X-Sensor-Pid", pid_header);
  httpd_resp_set_hdr(request, "X-Frame-Size", g_still_name);
  httpd_resp_set_hdr(request, "X-Pixel-Format", raw ? "RGB565" : "JPEG");
  if (raw) {
    // Byte 0 is RRRRRGGG and byte 1 is GGGBBBBB. This matches esp32-camera
    // f0bb429 to_jpg.cpp convert_line_format for PIXFORMAT_RGB565.
    httpd_resp_set_hdr(request, "X-Rgb565-Order", "BE");
  }
}

static void queue_error_headers(httpd_req_t* request, const StillResult& result, char* body, size_t body_length) {
  if (strcmp(result.error, "still_requires_psram") == 0) {
    snprintf(
      body,
      body_length,
      "{\"error\":\"still_requires_psram\",\"previewWidth\":%d,\"previewHeight\":%d}",
      g_preview_width,
      g_preview_height
    );
  } else if (strcmp(result.error, "still_size_mismatch") == 0) {
    snprintf(
      body,
      body_length,
      "{\"error\":\"still_size_mismatch\",\"width\":%d,\"height\":%d,"
      "\"configuredStillWidth\":%d,\"configuredStillHeight\":%d}",
      result.width,
      result.height,
      g_still_width,
      g_still_height
    );
  } else {
    snprintf(body, body_length, "{\"error\":\"%s\"}", result.error);
  }
  httpd_resp_set_status(request, "503 Service Unavailable");
  httpd_resp_set_type(request, "application/json");
  httpd_resp_set_hdr(request, "Cache-Control", "no-store");
}

static esp_err_t handle_still(httpd_req_t* request, bool raw) {
  const int64_t started = esp_timer_get_time();
  const char* path = raw ? "raw" : "jpeg";
  const int64_t lock_started = esp_timer_get_time();
  if (!camera_lock(8000)) {
    CaptureTimings timings = {};
    TimingHeaderStorage storage = {};
    const int64_t prep_started = esp_timer_get_time();
    char body[] = "{\"error\":\"camera_busy\"}";
    httpd_resp_set_status(request, "503 Service Unavailable");
    httpd_resp_set_type(request, "application/json");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    timings.mutexWaitMs = ms_since(lock_started);
    timings.httpPrepMs = ms_since(prep_started);
    timings.totalMs = ms_since(started);
    set_timing_headers(request, timings, &storage);
    return httpd_resp_send(request, body, HTTPD_RESP_USE_STRLEN);
  }

  const uint32_t mutex_wait = ms_since(lock_started);
  StillResult result = prepare_locked(raw);
  result.timings.mutexWaitMs = mutex_wait;
  camera_unlock();

  // Header setup is the HTTP preparation. The body send below is not included.
  const int64_t prep_started = esp_timer_get_time();
  TimingHeaderStorage storage = {};
  char width_header[16];
  char height_header[16];
  char pid_header[16];
  char bytes_header[16];
  char chunk_header[16];
  char error_body[192];
  if (result.error != nullptr) {
    queue_error_headers(request, result, error_body, sizeof(error_body));
  } else {
    queue_frame_headers(request, raw, result, width_header, height_header, pid_header, bytes_header, chunk_header);
  }
  result.timings.httpPrepMs = ms_since(prep_started);
  result.timings.previewRestoreMs = result.timings.previewDeinitMs + result.timings.previewInitMs + result.timings.previewStaleMs;
  result.timings.totalMs = ms_since(started);
  set_timing_headers(request, result.timings, &storage);
  remember_timings(result.timings, path);

  if (result.error != nullptr) {
    log_capture(path, result, nullptr);
    return httpd_resp_send(request, error_body, HTTPD_RESP_USE_STRLEN);
  }

  // Headers above describe preparation only. Body stats are recorded after
  // the final chunk, and the owned buffer stays alive until that return.
  BodySendStats transport = {};
  int esp_status = ESP_OK;
  const int sent = transport_send_body(request, result.data, result.length, 1, 1, &transport, &esp_status);
  const char* transport_error = transport_error_name(transport);
  free(result.data);
  result.data = nullptr;
  camera_note_transport(path, &transport, transport_error, esp_status);
  log_capture(path, result, &transport);
  return sent == 0 ? ESP_OK : ESP_FAIL;
}

void camera_control_init(const camera_config_t* config, bool psram, uint16_t sensor_pid) {
  if (g_mutex == nullptr) {
    g_mutex = xSemaphoreCreateMutex();
  }
  g_config = *config;
  g_psram = psram;
  g_pid = sensor_pid;
  g_camera_ready = true;

  const FrameSpec* preview = find_spec(FRAMESIZE_HQVGA);
  if (preview != nullptr) {
    g_preview_width = preview->width;
    g_preview_height = preview->height;
  }

  const FrameSpec* still = find_spec(g_still_size);
  g_still_available = psram && still != nullptr;
  if (g_still_available) {
    g_still_width = still->width;
    g_still_height = still->height;
    strncpy(g_still_name, still->name, sizeof(g_still_name) - 1);
  } else {
    g_still_width = 0;
    g_still_height = 0;
    g_still_name[0] = '\0';
    set_error("still_requires_psram");
  }

  Serial.printf(
    "[CAM] Onizleme %dx%d. Still %s. PSRAM %s. PID 0x%04X. Stale still %d, onizleme %d.\n",
    g_preview_width,
    g_preview_height,
    g_still_available ? g_still_name : "yok",
    g_psram ? "var" : "yok",
    g_pid,
    SMARTLINE_STILL_STALE_FRAMES,
    SMARTLINE_PREVIEW_STALE_FRAMES
  );
}

bool camera_lock(uint32_t timeout_ms) {
  if (g_mutex == nullptr) {
    return false;
  }
  return xSemaphoreTake(g_mutex, pdMS_TO_TICKS(timeout_ms)) == pdTRUE;
}

void camera_unlock() {
  if (g_mutex != nullptr) {
    xSemaphoreGive(g_mutex);
  }
}

void camera_note_transport(const char* path, const BodySendStats* stats, const char* transport_error, int esp_status) {
  if (stats == nullptr || !camera_lock(1000)) {
    return;
  }
  g_transport.bytes_intended = stats->bytes_intended;
  g_transport.bytes_written = stats->bytes_written;
  g_transport.send_ms = stats->body_send_ms;
  g_transport.started_at_ms = stats->started_at_ms;
  g_transport.chunk_count = stats->chunk_count;
  g_transport.max_chunk_ms = stats->max_chunk_ms;
  g_transport.error_code = stats->error_code;
  g_transport.esp_status = esp_status;
  g_transport.timed_out = stats->timed_out;
  strncpy(g_transport.error, transport_error == nullptr ? "" : transport_error, sizeof(g_transport.error) - 1);
  g_transport.error[sizeof(g_transport.error) - 1] = '\0';
  strncpy(g_transport.path, path == nullptr ? "" : path, sizeof(g_transport.path) - 1);
  g_transport.path[sizeof(g_transport.path) - 1] = '\0';
  camera_unlock();
}

void camera_note_stream_frame(uint32_t capture_ms, uint32_t jpeg_ms, uint32_t jpeg_bytes, uint32_t send_ms, int send_ok) {
  if (!camera_lock(1000)) {
    return;
  }
  g_transport.stream_capture_ms = capture_ms;
  g_transport.stream_jpeg_ms = jpeg_ms;
  g_transport.stream_jpeg_bytes = jpeg_bytes;
  g_transport.stream_send_ms = send_ms;
  g_transport.stream_frames += 1;
  if (!send_ok) {
    g_transport.stream_failures += 1;
  }
  camera_unlock();
}

static esp_err_t status_handler(httpd_req_t* request) {
  char body[3072];
  if (!camera_lock(1000)) {
    return send_json(request, "503 Service Unavailable", "{\"error\":\"camera_busy\"}");
  }
  write_status(body, sizeof(body));
  camera_unlock();
  return send_json(request, "200 OK", body);
}

static esp_err_t capture_handler(httpd_req_t* request) {
  return handle_still(request, false);
}

static esp_err_t capture_raw_handler(httpd_req_t* request) {
  return handle_still(request, true);
}

void camera_register_http(httpd_handle_t server) {
  const httpd_uri_t capture = {
    .uri = "/capture",
    .method = HTTP_GET,
    .handler = capture_handler,
    .user_ctx = nullptr,
  };
  const httpd_uri_t capture_raw = {
    .uri = "/capture/raw",
    .method = HTTP_GET,
    .handler = capture_raw_handler,
    .user_ctx = nullptr,
  };
  const httpd_uri_t status = {
    .uri = "/status",
    .method = HTTP_GET,
    .handler = status_handler,
    .user_ctx = nullptr,
  };

  if (httpd_register_uri_handler(server, &capture) != ESP_OK) {
    Serial.println("[HTTP] /capture register hatasi.");
  }
  if (httpd_register_uri_handler(server, &capture_raw) != ESP_OK) {
    Serial.println("[HTTP] /capture/raw register hatasi.");
  }
  if (httpd_register_uri_handler(server, &status) != ESP_OK) {
    Serial.println("[HTTP] /status register hatasi.");
  } else {
    Serial.println("[HTTP] /capture, /capture/raw ve /status hazir.");
  }
#if defined(SMARTLINE_HTTP_DIAG) && SMARTLINE_HTTP_DIAG
  transport_register_http(server);
#endif
}
