#include "esp_camera.h"
#include <WiFi.h>
#include "camera_pins.h"

// Ağ bilgileri build_flags üzerinden enjekte edilir (platformio.ini → sysenv)
#ifndef WIFI_SSID
  #error "WIFI_SSID tanımlı değil. Ortam değişkenini ayarla ve derle."
#endif
#ifndef WIFI_PASS
  #error "WIFI_PASS tanımlı değil. Ortam değişkenini ayarla ve derle."
#endif
#ifndef WIFI_STATIC_IP
  #error "WIFI_STATIC_IP tanımlı değil. Ortam değişkenini ayarla ve derle."
#endif
#ifndef WIFI_GATEWAY
  #error "WIFI_GATEWAY tanımlı değil. Ortam değişkenini ayarla ve derle."
#endif
#ifndef WIFI_SUBNET
  #error "WIFI_SUBNET tanımlı değil. Ortam değişkenini ayarla ve derle."
#endif

const char* ssid     = WIFI_SSID;
const char* password = WIFI_PASS;

#define WIFI_MAX_RETRIES 40  // 40 × 500ms = 20 sn timeout

void startCameraServer(); // Stream sunucusu fonksiyon prototipi

void setup() {
  Serial.begin(115200);
  Serial.setDebugOutput(true);
  Serial.println();

  camera_config_t config;
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
  config.pixel_format = PIXFORMAT_JPEG;

  // PSRAM kontrolü ve çözünürlük ayarı
  if(psramFound()){
    config.frame_size = FRAMESIZE_VGA; // 640x480 - Vision için ideal
    config.jpeg_quality = 10;
    config.fb_count = 2;
  } else {
    config.frame_size = FRAMESIZE_QVGA; // PSRAM yok → 320x240 (SVGA RAM'e sığmaz)
    config.jpeg_quality = 15;
    config.fb_count = 1;
  }

  // Kamera başlatma
  esp_err_t err = esp_camera_init(&config);
  if (err != ESP_OK) {
    Serial.printf("Kamera baslatma hatasi: 0x%x — yeniden baslatiliyor\n", err);
    delay(3000);
    ESP.restart();
  }

  // Statik IP yapılandırması (DHCP yerine sabit IP → Vision URL'si hiç değişmez)
  IPAddress local_IP, gateway, subnet;
  local_IP.fromString(WIFI_STATIC_IP);
  gateway.fromString(WIFI_GATEWAY);
  subnet.fromString(WIFI_SUBNET);
  WiFi.config(local_IP, gateway, subnet);

  // WiFi bağlantısı (20 sn timeout → restart)
  WiFi.begin(ssid, password);
  int retries = 0;
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
    if (++retries >= WIFI_MAX_RETRIES) {
      Serial.println("\nWiFi baglantisi basarisiz — yeniden baslatiliyor");
      ESP.restart();
    }
  }
  Serial.println("\nWiFi Baglandi!");

  startCameraServer();

  Serial.print("Kamera yayini hazır! Sunu kullan: 'http://");
  Serial.print(WiFi.localIP());
  Serial.println(":81/stream'");
}

// Stream sunucusu mantığı (MJPEG aktarımı)
#include "esp_http_server.h"

#define BOUNDARY      "123456789000000000000987654321"
#define BOUNDARY_HEAD "--" BOUNDARY "\r\n"
#define BOUNDARY_SEP  "\r\n--" BOUNDARY "\r\n"

esp_err_t stream_handler(httpd_req_t *req){
  camera_fb_t * fb = NULL;
  esp_err_t res = ESP_OK;
  size_t _jpg_buf_len = 0;
  uint8_t * _jpg_buf = NULL;
  char part_buf[64];

  res = httpd_resp_set_type(req, "multipart/x-mixed-replace;boundary=" BOUNDARY);
  if(res != ESP_OK) return res;

  // İlk frame öncesi boundary başlığı
  res = httpd_resp_send_chunk(req, BOUNDARY_HEAD, sizeof(BOUNDARY_HEAD) - 1);
  if(res != ESP_OK) return res;

  int fail_count = 0;
  while(true){
    fb = esp_camera_fb_get();
    if (!fb) {
      if (++fail_count >= 10) {
        Serial.println("Kamera karesi 10 kez alinamadi, stream sonlandiriliyor");
        res = ESP_FAIL;
        break;
      }
      Serial.println("Kamera karesi alinamadi, yeniden deneniyor...");
      delay(100);
      continue;
    }
    fail_count = 0;
    _jpg_buf_len = fb->len;
    _jpg_buf = fb->buf;
    if(res == ESP_OK){
      size_t hlen = snprintf(part_buf, sizeof(part_buf),
        "Content-Type: image/jpeg\r\nContent-Length: %u\r\n\r\n", _jpg_buf_len);
      res = httpd_resp_send_chunk(req, part_buf, hlen);
    }
    if(res == ESP_OK){
      res = httpd_resp_send_chunk(req, (const char *)_jpg_buf, _jpg_buf_len);
    }
    if(res == ESP_OK){
      res = httpd_resp_send_chunk(req, BOUNDARY_SEP, sizeof(BOUNDARY_SEP) - 1);
    }
    if(fb){
      esp_camera_fb_return(fb);
      fb = NULL;
      _jpg_buf = NULL;
    } else if(res == ESP_OK){
      break;
    }
    if(res != ESP_OK) break;
  }
  return res;
}

void startCameraServer(){
  httpd_config_t config = HTTPD_DEFAULT_CONFIG();
  config.server_port = 81;

  httpd_uri_t stream_uri = {
    .uri       = "/stream",
    .method    = HTTP_GET,
    .handler   = stream_handler,
    .user_ctx  = NULL
  };

  httpd_handle_t stream_httpd = NULL;
  if (httpd_start(&stream_httpd, &config) == ESP_OK) {
    httpd_register_uri_handler(stream_httpd, &stream_uri);
  }
}

void loop() {
  delay(10000); // Ana işler stream handler içinde dönüyor
}
