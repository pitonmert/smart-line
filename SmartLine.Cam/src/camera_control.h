#pragma once

#include "esp_camera.h"
#include "esp_http_server.h"
#include "http_body.h"

#include <stdbool.h>
#include <stdint.h>

// Owns the camera mutex, the JPEG still endpoint, and the raw RGB565 still endpoint.
// The stream holds the mutex only while it owns the driver framebuffer. The JPEG
// copy is sent after the framebuffer is returned.

void camera_control_init(const camera_config_t* config, bool psram, uint16_t sensor_pid);
bool camera_lock(uint32_t timeout_ms);
void camera_unlock();
void camera_register_http(httpd_handle_t server);
void camera_note_transport(const char* path, const BodySendStats* stats, const char* transport_error, int esp_status);
void camera_note_stream_frame(uint32_t capture_ms, uint32_t jpeg_ms, uint32_t jpeg_bytes, uint32_t send_ms, int send_ok);
