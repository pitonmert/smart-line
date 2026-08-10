from pyzbar.pyzbar import decode
import os
import cv2
import logging

log = logging.getLogger("smartline.qr_reader")

# Performans ve Spam Ayarları
FRAME_SKIP = int(os.environ.get("FRAME_SKIP", "3")) # Her 3 karede bir analiz yap (Mac için 1 de yapabilirsin ama gerek yok)
DEBOUNCE_FRAMES = int(os.environ.get("DEBOUNCE_FRAMES", "30")) # Aynı barkodu peş peşe 30 kare boyunca tekrar API'ye atma

frame_counter = 0
# Barkod başına debounce sayacı: {barkod_değeri: kalan_kare}
_debounce: dict[str, int] = {}

def process_frame(frame, on_barcode_detected_callback):
    """
    Kareyi analiz eder, barkod bulursa callback fonksiyonunu (MQTT) tetikler.
    Ayrıca ekranda görmek için çerçevenin üzerine yeşil kutu çizer.
    """
    global frame_counter, _debounce

    frame_counter += 1

    # Her kareyi analiz etme (İşlemci tasarrufu)
    if frame_counter % FRAME_SKIP != 0:
        return frame

    # Debounce sayaçlarını düşür
    _debounce = {k: v - 1 for k, v in _debounce.items() if v > 1}

    # PyZbar ile kareyi tara
    results = decode(frame)

    if not results:
        return frame

    for barcode in results:
        value = barcode.data.decode("utf-8", errors="replace")

        # Ekranda barkodun etrafına yeşil kutu çiz (Görsel geri bildirim)
        (x, y, w, h) = barcode.rect
        cv2.rectangle(frame, (x, y), (x + w, y + h), (0, 255, 0), 2)
        cv2.putText(frame, value, (x, y - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 255, 0), 2)

        # Spam Kontrolü: Bu barkod debounce süresindeyse geç
        if _debounce.get(value, 0) > 0:
            continue

        # Yeni barkod (veya süresi dolmuş barkod)
        log.info(f"Barkod okundu: {value}")
        on_barcode_detected_callback(value)
        _debounce[value] = DEBOUNCE_FRAMES

    return frame
