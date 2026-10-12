"""Проверка доступа к микрофону.

При первом обращении macOS показывает запрос доступа от имени приложения,
которое нас запустило (лаунчер «Valheim с модами»). Ждём, пока придёт
настоящий звук (разрешили), или до таймаута (запретили / не ответили).
Код выхода: 0 — доступ есть, 2 — нет доступа, 1 — ошибка устройства, 3 — CoreAudio завис.
"""

import os
import sys
import threading
import time

import numpy as np
import sounddevice as sd


def main() -> int:
    timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    # CoreAudio иногда виснет при остановке потока записи — лаунчер не должен ждать вечно
    watchdog = threading.Timer(timeout + 8.0, lambda: (print("hung", flush=True), os._exit(3)))
    watchdog.daemon = True
    watchdog.start()

    peak = [0.0]

    def on_audio(data, frames, time_info, status):
        peak[0] = max(peak[0], float(np.abs(data[:, 0]).max()))

    deadline = time.monotonic() + timeout
    try:
        # Поток открываем один раз: частые открытия-закрытия и ловят ту самую взаимоблокировку
        stream = sd.InputStream(samplerate=16000, channels=1, dtype="float32", callback=on_audio)
        stream.start()
        try:
            # Без доступа macOS отдаёт ровные нули; после «Разрешить» — живой шум комнаты
            while peak[0] <= 1e-5 and time.monotonic() < deadline:
                time.sleep(0.2)
        finally:
            stream.abort()
            stream.close()
    except Exception as e:
        print(f"error {e}", flush=True)
        os._exit(1)
    if peak[0] > 1e-5:
        print(f"ok {peak[0]:.5f}", flush=True)
        os._exit(0)
    print("denied", flush=True)
    os._exit(2)


if __name__ == "__main__":
    main()
