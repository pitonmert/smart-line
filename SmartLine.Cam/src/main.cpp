#include "esp_camera.h"
#include "img_converters.h"
#include <WiFi.h>
#include "camera_pins.h"
#include "camera_control.h"
#include "tcp_diag.h"
#include <stdlib.h>

// =====================================================
// Wi-Fi configuration from PlatformIO environment
// =====================================================

#ifndef WIFI_SSID
  #error "WIFI_SSID tanimli degil."
#endif

#ifndef WIFI_PASS
  #error "WIFI_PASS tanimli degil."
#endif

#ifndef WIFI_STATIC_IP
  #error "WIFI_STATIC_IP tanimli degil."
#endif

#ifndef WIFI_GATEWAY
  #error "WIFI_GATEWAY tanimli degil."
#endif

#ifndef WIFI_SUBNET
  #error "WIFI_SUBNET tanimli degil."
#endif

const char* ssid = WIFI_SSID;
const char* password = WIFI_PASS;

#define WIFI_MAX_RETRIES 40

void startCameraServer();


// =====================================================
// SETUP
// =====================================================

void setup() {

  Serial.begin(115200);
  Serial.setDebugOutput(true);

  delay(500);

  Serial.println();
  Serial.println("======================================");
  Serial.println("SmartLine Camera baslatiliyor...");
  Serial.println("======================================");


  // ---------------------------------------------------
  // Camera config
  // ---------------------------------------------------

  camera_config_t config = {};

  config.ledc_channel = LEDC_CHANNEL_0;
  config.ledc_timer = LEDC_TIMER_0;

  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;

  config.pin_xclk = XCLK_GPIO_NUM;
  config.pin_pclk = PCLK_GPIO_NUM;
  config.pin_vsync = VSYNC_GPIO_NUM;
  config.pin_href = HREF_GPIO_NUM;

  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;

  config.pin_pwdn = PWDN_GPIO_NUM;
  config.pin_reset = RESET_GPIO_NUM;

  config.xclk_freq_hz = 20000000;


  // ===================================================
  // GC2145 / RHYX M21-45
  // ===================================================
  //
  // Bu sensor hardware JPEG desteklemiyor.
  //
  // Kamera:
  // RGB565
  //
  // ESP32:
  // RGB565 -> JPEG
  //
  // Browser / Vision:
  // MJPEG
  //
  // ===================================================

  config.pixel_format = PIXFORMAT_RGB565;


  // ---------------------------------------------------
  // FAST PREVIEW MODE
  // ---------------------------------------------------
  //
  // 240x176
  //
  // 320x240'a gore ESP32'nin JPEG donusum yuku
  // ciddi sekilde azalir.
  //
  // AI ve veri kumesi /capture/raw veya /capture uzerinden ayri bir kare alir.
  // Bu onizleme cozunurlugu kalici olarak yukseltilmez.
  // ---------------------------------------------------

  config.frame_size = FRAMESIZE_HQVGA;

  config.jpeg_quality = 12;

  config.fb_count = 1;

  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;


  // ---------------------------------------------------
  // PSRAM
  // ---------------------------------------------------

  if (psramFound()) {

    Serial.println("[CAM] PSRAM bulundu.");

    config.fb_location = CAMERA_FB_IN_PSRAM;

  } else {

    Serial.println("[CAM] UYARI: PSRAM bulunamadi.");

    config.fb_location = CAMERA_FB_IN_DRAM;
  }


  // ---------------------------------------------------
  // Camera initialization
  // ---------------------------------------------------

  Serial.println("[CAM] Kamera initialize ediliyor...");

  esp_err_t err = esp_camera_init(&config);

  if (err != ESP_OK) {

    Serial.printf(
      "[CAM] Kamera baslatma hatasi: 0x%x\n",
      err
    );

    Serial.println(
      "[CAM] 5 saniye sonra yeniden baslatiliyor..."
    );

    delay(5000);

    ESP.restart();
  }


  Serial.println(
    "[CAM] Kamera basariyla baslatildi."
  );


  // ---------------------------------------------------
  // Detect sensor
  // ---------------------------------------------------

  sensor_t* sensor = esp_camera_sensor_get();

  if (sensor != nullptr) {

    Serial.printf(
      "[CAM] Sensor PID: 0x%04X\n",
      sensor->id.PID
    );

    Serial.printf(
      "[CAM] Sensor VER: 0x%02X\n",
      sensor->id.VER
    );


    if (sensor->id.PID == GC2145_PID) {

      Serial.println(
        "[CAM] GC2145 sensor algilandi."
      );
    }


    // -------------------------------------------------
    // Basic image tuning
    // -------------------------------------------------

    sensor->set_vflip(sensor, 0);

    sensor->set_hmirror(sensor, 0);

    sensor->set_brightness(sensor, 0);

    sensor->set_contrast(sensor, 0);

    sensor->set_saturation(sensor, 0);
  }


  // ===================================================
  // Wi-Fi static IP
  // ===================================================

  IPAddress localIP;
  IPAddress gateway;
  IPAddress subnet;


  if (!localIP.fromString(WIFI_STATIC_IP)) {

    Serial.println(
      "[WiFi] WIFI_STATIC_IP gecersiz."
    );

    delay(5000);

    ESP.restart();
  }


  if (!gateway.fromString(WIFI_GATEWAY)) {

    Serial.println(
      "[WiFi] WIFI_GATEWAY gecersiz."
    );

    delay(5000);

    ESP.restart();
  }


  if (!subnet.fromString(WIFI_SUBNET)) {

    Serial.println(
      "[WiFi] WIFI_SUBNET gecersiz."
    );

    delay(5000);

    ESP.restart();
  }


  if (!WiFi.config(
        localIP,
        gateway,
        subnet
      )) {

    Serial.println(
      "[WiFi] Statik IP yapilandirmasi basarisiz."
    );
  }


  WiFi.mode(WIFI_STA);


  // Streaming icin sleep kapali
  WiFi.setSleep(false);


  Serial.print(
    "[WiFi] Baglaniliyor: "
  );

  Serial.println(ssid);


  WiFi.begin(
    ssid,
    password
  );


  int retries = 0;


  while (
    WiFi.status() != WL_CONNECTED
  ) {

    delay(500);

    Serial.print(".");


    retries++;


    if (
      retries >= WIFI_MAX_RETRIES
    ) {

      Serial.println();

      Serial.println(
        "[WiFi] Baglanti basarisiz."
      );

      Serial.println(
        "[WiFi] Yeniden baslatiliyor..."
      );

      delay(2000);

      ESP.restart();
    }
  }


  Serial.println();

  Serial.println(
    "[WiFi] Baglandi."
  );


  Serial.print(
    "[WiFi] IP: "
  );

  Serial.println(
    WiFi.localIP()
  );


  Serial.print(
    "[WiFi] RSSI: "
  );

  Serial.print(
    WiFi.RSSI()
  );

  Serial.println(
    " dBm"
  );


  // ===================================================
  // HTTP camera server
  // ===================================================

  camera_control_init(
    &config,
    psramFound(),
    sensor == nullptr ? 0 : sensor->id.PID
  );

  startCameraServer();

  // Port 9001 exists only when the development flag is compiled in.
  tcp_diag_start();


  Serial.println();

  Serial.println(
    "======================================"
  );

  Serial.println(
    "SMARTLINE CAMERA HAZIR"
  );

  Serial.print(
    "Stream: http://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":81/stream"
  );

  Serial.print(
    "Capture: http://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":81/capture"
  );

  Serial.print(
    "Raw: http://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":81/capture/raw"
  );

  Serial.print(
    "Status: http://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":81/status"
  );

  Serial.print(
    "Throughput: http://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":81/debug/throughput/internal?bytes=32768"
  );

#if SMARTLINE_TCP_DIAG
  Serial.print(
    "TCP: tcp://"
  );

  Serial.print(
    WiFi.localIP()
  );

  Serial.println(
    ":9001"
  );
#endif

  Serial.println(
    "======================================"
  );
}


// =====================================================
// HTTP MJPEG STREAM
// =====================================================

#include "esp_http_server.h"
#include "esp_timer.h"
#include "http_transport.h"

#include <errno.h>
#include <string.h>


#define BOUNDARY \
  "123456789000000000000987654321"

#define BOUNDARY_HEAD \
  "--" BOUNDARY "\r\n"

#define BOUNDARY_SEP \
  "\r\n--" BOUNDARY "\r\n"


esp_err_t stream_handler(httpd_req_t* req) {
  esp_err_t res = httpd_resp_set_type(req, "multipart/x-mixed-replace;boundary=" BOUNDARY);
  if (res != ESP_OK) {
    return res;
  }
  res = httpd_resp_send_chunk(req, BOUNDARY_HEAD, sizeof(BOUNDARY_HEAD) - 1);
  if (res != ESP_OK) {
    return res;
  }

  Serial.println("[STREAM] Client baglandi.");
  int captureFailCount = 0;
  int conversionFailCount = 0;
  uint32_t windowStarted = 0;
  bool windowOpen = false;
  uint32_t windowFrames = 0;
  uint32_t windowCaptureMs = 0;
  uint32_t windowJpegMs = 0;
  uint32_t windowBytes = 0;
  uint32_t windowSendMs = 0;
  uint32_t windowFail = 0;

  while (true) {
    if (!camera_lock(200)) {
      delay(10);
      continue;
    }

    const int64_t captureStarted = esp_timer_get_time();
    camera_fb_t* fb = esp_camera_fb_get();
    const uint32_t captureMs = static_cast<uint32_t>((esp_timer_get_time() - captureStarted) / 1000);
    if (fb == nullptr) {
      camera_unlock();
      captureFailCount++;
      Serial.printf("[STREAM] Frame alinamadi (%d/10)\n", captureFailCount);
      if (captureFailCount >= 10) {
        Serial.println("[STREAM] Cok fazla capture hatasi.");
        res = ESP_FAIL;
        break;
      }
      delay(20);
      continue;
    }
    captureFailCount = 0;

    uint8_t* jpgBuffer = nullptr;
    size_t jpgLength = 0;
    bool jpegAllocated = false;
    uint32_t jpegMs = 0;
    if (fb->format == PIXFORMAT_JPEG) {
      jpgBuffer = fb->buf;
      jpgLength = fb->len;
    } else {
      const int64_t jpegStarted = esp_timer_get_time();
      const bool converted = frame2jpg(fb, 60, &jpgBuffer, &jpgLength);
      jpegMs = static_cast<uint32_t>((esp_timer_get_time() - jpegStarted) / 1000);
      if (!converted || jpgBuffer == nullptr || jpgLength == 0) {
        conversionFailCount++;
        Serial.printf("[STREAM] RGB565 -> JPEG donusumu basarisiz (%d)\n", conversionFailCount);
        if (jpgBuffer != nullptr) {
          free(jpgBuffer);
        }
        esp_camera_fb_return(fb);
        camera_unlock();
        if (conversionFailCount >= 10) {
          Serial.println("[STREAM] Cok fazla JPEG conversion hatasi.");
          res = ESP_FAIL;
          break;
        }
        delay(1);
        continue;
      }
      jpegAllocated = true;
      conversionFailCount = 0;
    }

    uint8_t* owned = jpgBuffer;
    bool freeOwned = jpegAllocated;
    if (!jpegAllocated) {
      owned = static_cast<uint8_t*>(malloc(jpgLength));
      if (owned != nullptr) {
        memcpy(owned, jpgBuffer, jpgLength);
        freeOwned = true;
      }
    }
    esp_camera_fb_return(fb);
    camera_unlock();
    if (owned == nullptr) {
      conversionFailCount++;
      if (conversionFailCount >= 10) {
        res = ESP_FAIL;
        break;
      }
      delay(1);
      continue;
    }

    char headerBuffer[96];
    const size_t headerLength = snprintf(
      headerBuffer,
      sizeof(headerBuffer),
      "Content-Type: image/jpeg\r\nContent-Length: %u\r\n\r\n",
      static_cast<unsigned int>(jpgLength)
    );
    res = httpd_resp_send_chunk(req, headerBuffer, headerLength);
    BodySendStats transport = {};
    int espStatus = ESP_OK;
    if (res == ESP_OK) {
      // finish=0 keeps the multipart response open for the next frame.
      if (transport_send_body(req, owned, jpgLength, 1, 0, &transport, &espStatus) != 0) {
        res = ESP_FAIL;
      }
    }
    if (res == ESP_OK) {
      res = httpd_resp_send_chunk(req, BOUNDARY_SEP, sizeof(BOUNDARY_SEP) - 1);
    }
    if (freeOwned) {
      free(owned);
    }

    const char* transportError = res == ESP_OK ? "" : (transport.complete ? "send_failed" : transport_error_name(transport));
    if (res != ESP_OK && transport.bytes_intended == 0) {
      transport.bytes_intended = static_cast<uint32_t>(jpgLength);
      transport.error_code = EIO;
      transportError = "send_failed";
    }
    camera_note_transport("stream", &transport, transportError, espStatus);
    camera_note_stream_frame(captureMs, jpegMs, static_cast<uint32_t>(jpgLength), transport.body_send_ms, res == ESP_OK);

    windowFrames += 1;
    windowCaptureMs += captureMs;
    windowJpegMs += jpegMs;
    windowBytes += static_cast<uint32_t>(jpgLength);
    windowSendMs += transport.body_send_ms;
    if (res != ESP_OK) {
      windowFail += 1;
    }
    const uint32_t now = millis();
    if (!windowOpen) {
      windowStarted = now;
      windowOpen = true;
    }
    if (static_cast<uint32_t>(now - windowStarted) >= 2000) {
      Serial.printf(
        "[STREAM] %lu ms pencere: kare=%lu alim_ms=%lu jpeg_ms=%lu bayt=%lu gonder_ms=%lu hata=%lu\n",
        static_cast<unsigned long>(now - windowStarted),
        static_cast<unsigned long>(windowFrames),
        static_cast<unsigned long>(windowCaptureMs),
        static_cast<unsigned long>(windowJpegMs),
        static_cast<unsigned long>(windowBytes),
        static_cast<unsigned long>(windowSendMs),
        static_cast<unsigned long>(windowFail)
      );
      windowStarted = now;
      windowFrames = 0;
      windowCaptureMs = 0;
      windowJpegMs = 0;
      windowBytes = 0;
      windowSendMs = 0;
      windowFail = 0;
    }

    if (res != ESP_OK) {
      Serial.printf("[STREAM] Client ayrildi / HTTP hata: 0x%x\n", res);
      break;
    }
    delay(1);
  }

  Serial.println("[STREAM] Stream sonlandi.");
  return res;
}


// =====================================================
// HTTP SERVER
// =====================================================

void startCameraServer() {


  httpd_config_t serverConfig =
    HTTPD_DEFAULT_CONFIG();


  serverConfig.server_port = 81;


  /*
   * Software JPEG conversion biraz daha fazla
   * stack kullanabilir.
   */

  // Still capture reinitializes the sensor on this task.
  // Timing headers on /capture and /capture/raw need more than the default 8.
  // Handlers: /stream, /capture, /capture/raw, /status, and three throughput routes.
  // core 1 keeps this task off CPU0, where the Wi-Fi and TCPIP tasks are pinned.
  // send_wait_timeout stays 15 seconds. Raising it would only hide a stalled
  // socket write. recv_wait_timeout stays at the 5 second default. max_open_sockets
  // stays at 7. Wi-Fi modem sleep is already disabled in setup().
  serverConfig.stack_size = 20480;
  serverConfig.core_id = 1;
  serverConfig.max_uri_handlers = 12;
  serverConfig.max_resp_headers = 32;
  serverConfig.send_wait_timeout = 15;


  httpd_uri_t streamUri = {

    .uri = "/stream",

    .method = HTTP_GET,

    .handler = stream_handler,

    .user_ctx = nullptr
  };


  httpd_handle_t streamServer =
    nullptr;


  esp_err_t result =
    httpd_start(
      &streamServer,
      &serverConfig
    );


  if (
    result != ESP_OK
  ) {

    Serial.printf(
      "[HTTP] Server baslatilamadi: 0x%x\n",
      result
    );

    return;
  }


  result =
    httpd_register_uri_handler(
      streamServer,
      &streamUri
    );


  if (
    result != ESP_OK
  ) {

    Serial.printf(
      "[HTTP] /stream register hatasi: 0x%x\n",
      result
    );

    return;
  }


  // Yayın uzun süre açık kalır; fotoğraf ve durum istekleri ayrı sunucuda işlenir.
  httpd_config_t controlConfig = serverConfig;
  controlConfig.server_port = 80;
  controlConfig.ctrl_port = serverConfig.ctrl_port + 1;
  httpd_handle_t controlServer = nullptr;
  if (httpd_start(&controlServer, &controlConfig) != ESP_OK) {
    Serial.println("[HTTP] Control server baslatilamadi.");
    return;
  }
  camera_register_http(controlServer);

  Serial.println(
    "[HTTP] MJPEG server baslatildi."
  );
}


// =====================================================
// MAIN LOOP
// =====================================================

void loop() {


  delay(5000);


  // ---------------------------------------------------
  // Wi-Fi reconnect
  // ---------------------------------------------------

  if (
    WiFi.status() != WL_CONNECTED
  ) {

    Serial.println(
      "[WiFi] Baglanti koptu. Yeniden baglaniliyor..."
    );


    WiFi.reconnect();
  }
}