#!/usr/bin/env bash
# Готовит мост: окружение Python (arm64), зависимости и модели голоса.
# Запуск: ./setup.sh   (другой Python: PYTHON=/path/to/python3.12 ./setup.sh)
set -euo pipefail
cd "$(dirname "$0")"

PYTHON="${PYTHON:-python3.12}"
if [ ! -x .venv/bin/python ]; then
    echo "→ окружение .venv ($PYTHON, arm64)"
    arch -arm64 "$PYTHON" -m venv .venv
fi
echo "→ зависимости"
arch -arm64 .venv/bin/python -m pip install --upgrade pip --quiet
arch -arm64 .venv/bin/python -m pip install -r requirements.txt

fetch() {
    local url="$1" dest="$2"
    [ -s "$dest" ] && return 0
    echo "→ $(basename "$dest")"
    curl -fL --progress-bar -o "$dest.part" "$url"
    mv "$dest.part" "$dest"
}

mkdir -p data/voices
# Silero v5 (основной голос). Лицензия моделей Silero — CC BY-NC 4.0: только некоммерческое использование.
fetch https://models.silero.ai/models/tts/ru/v5_ru.pt data/voices/silero_v5_ru.pt
# Piper ruslan (запасной голос)
for ext in onnx onnx.json; do
    fetch "https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/ruslan/medium/ru_RU-ruslan-medium.$ext" \
        "data/voices/ru_RU-ruslan-medium.$ext"
done

if [ ! -f .env ] && [ -z "${ANTHROPIC_API_KEY:-}" ]; then
    echo
    echo "Осталось указать ключ Anthropic: cp .env.example .env && chmod 600 .env, затем впиши ключ."
fi
echo
echo "Готово. Модель распознавания речи (Parakeet, ~2,5 ГБ) скачается сама при первом запуске моста."
