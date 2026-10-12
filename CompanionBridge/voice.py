"""Голос: микрофон, распознавание (Parakeet на MLX) и речь (Piper)."""

import difflib
import logging
import queue
import re
import threading
import time
import wave
from pathlib import Path

import numpy as np

log = logging.getLogger("bridge.voice")

SAMPLE_RATE = 16000


# ---------- Распознавание ----------

class STT:
    """Parakeet TDT v3: ~0.15 с на фразу целиком, русский язык."""

    def __init__(self, model_id: str, device: str = "gpu"):
        import mlx.core as mx
        from parakeet_mlx import from_pretrained
        from parakeet_mlx.audio import get_logmel

        self._mx = mx
        # Видеокарту занимает игра; на CPU медленнее без нагрузки, но не делит GPU с Valheim
        self._device = mx.cpu if device == "cpu" else mx.gpu
        self._get_logmel = get_logmel
        self._lock = threading.Lock()
        started = time.monotonic()
        with mx.stream(self._device):
            self.model = from_pretrained(model_id)
        self.transcribe(np.zeros(SAMPLE_RATE // 2, dtype=np.float32))  # прогрев
        log.info("STT %s (%s) готов за %.1fs", model_id, device, time.monotonic() - started)

    def transcribe(self, audio: np.ndarray) -> str:
        if len(audio) < SAMPLE_RATE // 4:
            return ""
        with self._lock, self._mx.stream(self._device):
            mel = self._get_logmel(self._mx.array(audio), self.model.preprocessor_config)
            return self.model.generate(mel)[0].text.strip()


# ---------- Микрофон ----------

class Recorder:
    """Пишет микрофон в буфер float32 16 кГц. В тестах вместо микрофона — wav-файлы."""

    def __init__(self, fake_dir: Path | None = None):
        self.fake_dir = fake_dir
        self._chunks: list[np.ndarray] = []
        self._lock = threading.Lock()
        self._stream = None
        self._fake_thread = None
        self._recording = False
        self.level = 0.0

    def start(self, test_audio: str = "") -> None:
        with self._lock:
            self._chunks = []
        self.level = 0.0
        self._recording = True
        if self.fake_dir is not None and test_audio:
            # Тестовая фраза вместо микрофона; живые нажатия идут в настоящий микрофон
            path = self.fake_dir / test_audio
            self._fake_thread = threading.Thread(target=self._feed_file, args=(path,), daemon=True)
            self._fake_thread.start()
            return
        import sounddevice as sd
        try:
            self._stream = sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32",
                                          blocksize=SAMPLE_RATE // 20, callback=self._on_audio)
            self._rate = SAMPLE_RATE
        except Exception:
            # Устройство не умеет 16 кГц — пишем в родной частоте и пересэмплируем
            rate = int(sd.query_devices(kind="input")["default_samplerate"])
            self._stream = sd.InputStream(samplerate=rate, channels=1, dtype="float32",
                                          blocksize=rate // 20, callback=self._on_audio)
            self._rate = rate
        self._stream.start()

    def _on_audio(self, data, frames, time_info, status) -> None:
        block = data[:, 0].copy()
        if self._rate != SAMPLE_RATE:
            block = resample(block, self._rate, SAMPLE_RATE)
        self._append(block)

    def _append(self, block: np.ndarray) -> None:
        self.level = float(np.sqrt(np.mean(block * block))) if len(block) else 0.0
        with self._lock:
            self._chunks.append(block)

    def _feed_file(self, path: Path) -> None:
        audio = load_wav(path)
        step = SAMPLE_RATE // 20
        for i in range(0, len(audio), step):
            if not self._recording:
                return
            self._append(audio[i:i + step])
            time.sleep(step / SAMPLE_RATE)
        # дальше — тишина, как в живом микрофоне, пока не отпустят кнопку
        while self._recording:
            self._append(np.zeros(step, dtype=np.float32) + 1e-3 * np.random.randn(step).astype(np.float32))
            time.sleep(step / SAMPLE_RATE)

    def snapshot(self) -> np.ndarray:
        with self._lock:
            return np.concatenate(self._chunks) if self._chunks else np.zeros(0, dtype=np.float32)

    def stop(self) -> np.ndarray:
        self._recording = False
        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None
        if self._fake_thread is not None:
            self._fake_thread.join(timeout=1)
            self._fake_thread = None
        return self.snapshot()


def resample(audio: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst or len(audio) == 0:
        return audio
    ratio = src / dst
    if ratio > 1:
        # простой фильтр от наложения перед прореживанием
        k = int(round(ratio))
        audio = np.convolve(audio, np.ones(k) / k, mode="same")
    positions = np.arange(0, len(audio), ratio)
    return np.interp(positions, np.arange(len(audio)), audio).astype(np.float32)


def load_wav(path: Path) -> np.ndarray:
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
        if w.getnchannels() > 1:
            data = data.reshape(-1, w.getnchannels()).mean(axis=1)
    return resample(data, rate, SAMPLE_RATE)


# ---------- Речь ----------

class PiperEngine:
    """Piper: быстрый, но голос заметно «синтетический» и путает ударения."""

    def __init__(self, voice_path: Path, pitch: float):
        from piper import PiperVoice
        from piper.config import SynthesisConfig

        self.voice = PiperVoice.load(voice_path)
        # Голос ниже за счёт пониженной частоты воспроизведения, а темп возвращаем length_scale
        self.pitch = pitch
        self.config = SynthesisConfig(length_scale=pitch, noise_scale=0.6, noise_w_scale=0.7)

    def synth(self, text: str) -> tuple[np.ndarray, int]:
        chunks = [c.audio_int16_array for c in self.voice.synthesize(text, self.config)]
        audio = np.concatenate(chunks) if chunks else np.zeros(0, dtype=np.int16)
        return audio, int(self.voice.config.sample_rate * self.pitch)


class SileroEngine:
    """Silero v5: живее Piper, сам ставит ударения и «ё», ~30 мс на фразу на CPU."""

    RATE = 48000

    def __init__(self, model_path: Path, speaker: str, pitch: str, tempo: str):
        import torch

        torch.set_num_threads(4)
        self.torch = torch
        self.model = torch.package.PackageImporter(str(model_path)).load_pickle("tts_models", "model")
        self.model.to(torch.device("cpu"))
        self.speaker = speaker
        self.prosody = (pitch, tempo)

    def synth(self, text: str) -> tuple[np.ndarray, int]:
        pitch, tempo = self.prosody
        safe = text.replace("&", " и ").replace("<", " ").replace(">", " ")
        ssml = f'<speak><prosody pitch="{pitch}" rate="{tempo}">{safe}</prosody></speak>'
        with self.torch.inference_mode():
            audio = self.model.apply_tts(ssml_text=ssml, speaker=self.speaker, sample_rate=self.RATE,
                                         put_accent=True, put_yo=True).numpy()
        return (np.clip(audio, -1, 1) * 32767).astype(np.int16), self.RATE


# ---------- Текст для синтеза ----------
# Silero молча выбрасывает цифры и латиницу, а фраза без русских букв роняет его с «Text is empty».
# Поэтому перед синтезом числа пишем словами, а имена вроде Xan — кириллицей.

_ONES = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять",
         "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать",
         "семнадцать", "восемнадцать", "девятнадцать"]
_TENS = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"]
_HUNDREDS = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"]


def _below_thousand(n: int, female: bool = False) -> list[str]:
    words = []
    if n >= 100:
        words.append(_HUNDREDS[n // 100])
        n %= 100
    if n >= 20:
        words.append(_TENS[n // 10])
        n %= 10
    if n or not words:
        word = _ONES[n]
        if female and n in (1, 2):
            word = "одна" if n == 1 else "две"
        if n or not words:
            words.append(word)
    return [w for w in words if w and not (w == "ноль" and len(words) > 1)]


def number_words(n: int) -> str:
    if n >= 1_000_000:
        return " ".join(_ONES[int(d)] for d in str(n))
    thousands, rest = divmod(n, 1000)
    words = []
    if thousands:
        words += _below_thousand(thousands, female=True) if thousands > 1 else []
        last = thousands % 100
        words.append("тысяч" if 11 <= last <= 14 or thousands % 10 in (0, 5, 6, 7, 8, 9)
                     else "тысяча" if thousands % 10 == 1 else "тысячи")
    if rest or not thousands:
        words += _below_thousand(rest)
    return " ".join(words)


_LATIN = [("sch", "ш"), ("sh", "ш"), ("ch", "ч"), ("th", "т"), ("ph", "ф"), ("kh", "х"), ("zh", "ж"),
          ("ee", "и"), ("oo", "у"), ("ya", "я"), ("yu", "ю"), ("yo", "ё"), ("ck", "к"), ("qu", "кв"),
          ("a", "а"), ("b", "б"), ("c", "к"), ("d", "д"), ("e", "е"), ("f", "ф"), ("g", "г"), ("h", "х"),
          ("i", "и"), ("j", "дж"), ("k", "к"), ("l", "л"), ("m", "м"), ("n", "н"), ("o", "о"), ("p", "п"),
          ("q", "к"), ("r", "р"), ("s", "с"), ("t", "т"), ("u", "у"), ("v", "в"), ("w", "в"), ("x", "кс"),
          ("y", "и"), ("z", "з")]


def _cyrillic(word: str) -> str:
    low = word.lower()
    out, i = [], 0
    while i < len(low):
        for lat, cyr in _LATIN:
            if low.startswith(lat, i):
                out.append(cyr)
                i += len(lat)
                break
        else:
            out.append(low[i])
            i += 1
    text = "".join(out)
    return text[:1].upper() + text[1:] if word[:1].isupper() else text


def speakable(text: str) -> str:
    """Текст, который синтезатор произнесёт целиком; пустая строка — произносить нечего."""
    text = text.replace("×", " ").replace("%", " процентов")
    text = re.sub(r"\d+", lambda m: number_words(int(m.group())), text)
    text = re.sub(r"[A-Za-z]+", lambda m: _cyrillic(m.group()), text)
    text = " ".join(text.split())
    return text if re.search(r"[А-Яа-яЁё]", text) else ""


class Speaker:
    """Очередь фраз → синтез → воспроизведение. Прерывается мгновенно."""

    def __init__(self, engine, volume: float = 1.0, mute: bool = False):
        started = time.monotonic()
        self.engine = engine
        self.volume = volume
        self.mute = mute
        self._queue: queue.Queue = queue.Queue()
        self._generation = 0
        self._speaking = False
        self.synth("Тест.")
        # Заготовленные междометия: звучат мгновенно, пока LLM думает
        self._fillers = [self.synth(t) for t in ("Хм.", "Так...", "Ну-ка...", "Хм-м.")]
        self._last_filler = -1
        threading.Thread(target=self._worker, daemon=True).start()
        log.info("TTS %s готов за %.1fs", type(engine).__name__, time.monotonic() - started)

    def set_engine(self, engine) -> None:
        """Сменить голос на лету: междометия пересинтезируются новым голосом."""
        fillers = [engine.synth(t) for t in ("Хм.", "Так...", "Ну-ка...", "Хм-м.")]
        self.engine = engine
        self._fillers = fillers

    @property
    def speaking(self) -> bool:
        return self._speaking or not self._queue.empty()

    def synth(self, text: str) -> tuple[np.ndarray, int]:
        return self.engine.synth(text)

    def say(self, text: str) -> None:
        text = speakable(text)
        if text:
            self._queue.put((self._generation, text))

    def filler(self) -> None:
        import random
        choices = [i for i in range(len(self._fillers)) if i != self._last_filler]
        self._last_filler = random.choice(choices)
        self._queue.put((self._generation, self._fillers[self._last_filler]))

    def stop(self) -> None:
        """Перебили — замолкает сразу."""
        self._generation += 1
        while not self._queue.empty():
            try:
                self._queue.get_nowait()
            except queue.Empty:
                break
        if self._speaking and not self.mute:
            import sounddevice as sd
            sd.stop()

    def _worker(self) -> None:
        import sounddevice as sd
        while True:
            generation, item = self._queue.get()
            if generation != self._generation:
                continue
            text = item if isinstance(item, str) else "(междометие)"
            try:
                audio, rate = self.synth(item) if isinstance(item, str) else item
            except Exception:
                # Одна неудачная фраза не должна оборвать голос до конца игры
                log.exception("синтез не удался: %r", text)
                continue
            if generation != self._generation or len(audio) == 0:
                continue
            self._speaking = True
            try:
                if self.mute:
                    log.info("(без звука) %s", text)
                else:
                    samples = np.clip(audio.astype(np.float32) * self.volume / 32768.0, -1, 1)
                    sd.play(samples, rate)
                    sd.wait()
            except Exception:
                log.exception("playback failed")
            finally:
                self._speaking = False


# ---------- Потоковый разбор ответа LLM ----------

_SAY_START = re.compile(r'"say"\s*:\s*"')
_ESCAPES = {"n": "\n", "t": " ", '"': '"', "\\": "\\", "/": "/", "b": "", "f": "", "r": ""}


def say_prefix(raw: str) -> tuple[str, bool]:
    """Сколько поля say уже пришло в недописанном JSON и закрыта ли строка."""
    m = _SAY_START.search(raw)
    if not m:
        return "", False
    out = []
    i = m.end()
    while i < len(raw):
        ch = raw[i]
        if ch == "\\":
            if i + 1 >= len(raw):
                break
            nxt = raw[i + 1]
            if nxt == "u":
                if i + 6 > len(raw):
                    break
                out.append(chr(int(raw[i + 2:i + 6], 16)))
                i += 6
                continue
            out.append(_ESCAPES.get(nxt, nxt))
            i += 2
            continue
        if ch == '"':
            return "".join(out), True
        out.append(ch)
        i += 1
    return "".join(out), False


_SENTENCE = re.compile(r".+?[.!?…]+(?=\s)", re.S)


class SentenceFeeder:
    """Отдаёт в речь каждое законченное предложение, как только оно дописано."""

    def __init__(self, speak):
        self.speak = speak
        self.sent = 0

    @staticmethod
    def split(text: str) -> list[str]:
        parts: list[str] = []
        SentenceFeeder(parts.append).feed(text, True)
        return parts

    def feed(self, text: str, closed: bool) -> None:
        rest = text[self.sent:]
        while True:
            m = _SENTENCE.match(rest)
            if not m:
                break
            self.speak(m.group(0))
            self.sent += m.end()
            rest = text[self.sent:]
        if closed and rest.strip():
            self.speak(rest)
            self.sent = len(text)


def speech_paused(audio: np.ndarray, tail_seconds: float = 0.25) -> bool:
    """Хвост записи заметно тише самой речи — человек замолчал."""
    tail = int(SAMPLE_RATE * tail_seconds)
    if len(audio) < tail * 3:
        return False
    block = SAMPLE_RATE // 20
    body = audio[:-tail]
    usable = len(body) // block * block
    if usable == 0:
        return False
    loudness = np.sqrt(np.mean(body[:usable].reshape(-1, block) ** 2, axis=1))
    peak = float(np.percentile(loudness, 90))
    tail_rms = float(np.sqrt(np.mean(audio[-tail:] ** 2)))
    return peak > 0.01 and tail_rms < peak * 0.15


def similar(a: str, b: str) -> bool:
    """«медь» и «меть» — одна и та же фраза; разница в паре букв ответа не меняет."""
    a, b = normalize(a), normalize(b)
    if not a or not b:
        return False
    return a == b or difflib.SequenceMatcher(None, a, b).ratio() >= 0.92


def normalize(text: str) -> str:
    text = text.lower().replace("ё", "е")
    return " ".join(re.sub(r"[^\w\s]", " ", text).split())
