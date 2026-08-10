import os
import sys
import cv2
import time
import signal
import logging

sys.path.insert(0, os.path.dirname(__file__))
import mqtt_publisher
import qr_reader

logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO"),
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
log = logging.getLogger("smartline.vision")

CAM_URL = os.environ.get("CAM_STREAM_URL", "http://192.168.1.50:81/stream")
SHOW_PREVIEW = os.environ.get("SHOW_PREVIEW", "false").lower() == "true"

stop = False


def _handle_signal(signum, frame):
    global stop
    log.warning(f"Sinyal alındı ({signum}), durduruluyor...")
    stop = True


signal.signal(signal.SIGINT, _handle_signal)
signal.signal(signal.SIGTERM, _handle_signal)


def main():
    global stop
    log.info("Sistem başlatılıyor...")
    mqtt_publisher.start_mqtt()
    log.info(f"Kamera URL: {CAM_URL}")

    cap = None
    try:
        while not stop:
            cap = cv2.VideoCapture(CAM_URL)

            if not cap.isOpened():
                log.warning("Kamera yayınına bağlanılamadı. 5 saniye içinde tekrar denenecek...")
                time.sleep(5)
                continue

            log.info("Kamera bağlantısı başarılı, yayın okunuyor...")

            while not stop:
                ret, frame = cap.read()
                if not ret:
                    log.warning("Yayın koptu, yeniden bağlanılıyor...")
                    break

                processed_frame = qr_reader.process_frame(frame, mqtt_publisher.publish_barcode)

                if SHOW_PREVIEW:
                    cv2.imshow("SmartLine Vision - Canli Yayin", processed_frame)
                    if cv2.waitKey(1) & 0xFF == ord('q'):
                        log.info("Kullanıcı çıkışı (q tuşuna basıldı).")
                        stop = True
                        break

            cap.release()
            cap = None

    finally:
        log.info("Temizlik yapılıyor...")
        if cap is not None and cap.isOpened():
            cap.release()
        if SHOW_PREVIEW:
            cv2.destroyAllWindows()
        mqtt_publisher.stop_mqtt()


if __name__ == "__main__":
    main()
