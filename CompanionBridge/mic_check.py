"""Проверка доступа к микрофону.

При первом обращении macOS показывает запрос доступа от имени приложения,
которое нас запустило (лаунчер «Valheim с модами»). Ждём, пока придёт
настоящий звук (разрешили), или до таймаута (запретили / не ответили).
Код выхода: 0 — доступ есть, 2 — нет доступа, 1 — ошибка устройства.
"""

import sys
import time

import numpy as np
import sounddevice as sd


def heard(seconds: float) -> float:
    chunks: list[np.ndarray] = []

    def on_audio(data, frames, time_info, status):
        chunks.append(data[:, 0].copy())

    with sd.InputStream(samplerate=16000, channels=1, dtype="float32", callback=on_audio):
        time.sleep(seconds)
    audio = np.concatenate(chunks) if chunks else np.zeros(1, dtype=np.float32)
    return float(np.abs(audio).max())


def main() -> int:
    timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    deadline = time.monotonic() + timeout
    try:
        while True:
            # Без доступа macOS отдаёт ровные нули; после «Разрешить» — живой шум комнаты
            peak = heard(0.5)
            if peak > 1e-5:
                print(f"ok {peak:.5f}")
                return 0
            if time.monotonic() > deadline:
                print("denied")
                return 2
    except Exception as e:
        print(f"error {e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
