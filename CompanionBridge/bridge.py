"""Мост между модом VikingCompanion и Claude.

Мод шлёт события и сводку обстановки, мост спрашивает Claude Haiku и возвращает
реплику и действие компаньона. Реплики озвучиваются (Piper), голос игрока
распознаётся (Parakeet) — всё локально, кроме самой LLM.
Слушает только 127.0.0.1. Завершается сам, когда умирает процесс игры.
"""

import argparse
import json
import logging
import os
import sys
import threading
import time
from concurrent.futures import Future
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Literal

BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))  # мост запускается с -I, свой каталог добавляем сами

import anthropic  # noqa: E402
from pydantic import BaseModel, ValidationError  # noqa: E402

import voice  # noqa: E402

MODEL = "claude-haiku-5-5"
STT_MODEL = "mlx-community/parakeet-tdt-0.6b-v3"
ENV_FILE = BASE_DIR / ".env"

DATA_DIR = BASE_DIR / "data"
VOICES_DIR = DATA_DIR / "voices"
MEMORY_FILE = DATA_DIR / "memory.json"
LOG_FILE = DATA_DIR / "bridge.log"

HISTORY_IN_PROMPT = 30
HISTORY_KEPT = 120

ACTIONS = ["none", "follow", "stay", "come", "attack", "gather_wood", "mine", "loot", "give_items"]

OUTPUT_SCHEMA = {
    "type": "object",
    "properties": {
        "say": {
            "type": "string",
            "description": "Что ты говоришь вслух. Пустая строка — промолчать.",
        },
        "action": {
            "type": "string",
            "enum": ACTIONS,
            "description": "Что ты делаешь после этих слов.",
        },
    },
    "required": ["say", "action"],
    "additionalProperties": False,
}

log = logging.getLogger("bridge")


class Reply(BaseModel):
    say: str
    action: Literal["none", "follow", "stay", "come", "attack", "gather_wood", "mine", "loot", "give_items"]


def read_env_file(path: Path, wanted: set[str]) -> dict[str, str]:
    """Строки KEY=VALUE из файла; берём только нужные имена, остальное не читаем в память."""
    found: dict[str, str] = {}
    if not path.is_file():
        return found
    with path.open(encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if line.startswith("export "):
                line = line[len("export "):].lstrip()
            name, sep, value = line.partition("=")
            name = name.strip()
            if sep and name in wanted:
                value = value.strip().strip('"').strip("'")
                if value:
                    found[name] = value
    return found


def load_api_key() -> str:
    """Ключ Anthropic: переменная окружения ANTHROPIC_API_KEY, иначе CompanionBridge/.env.

    В .env можно положить сам ключ (ANTHROPIC_API_KEY=...) или только указатель на свой файл секретов:
    ANTHROPIC_API_KEY_FILE=путь и ANTHROPIC_API_KEY_NAME=имя переменной в нём — тогда ключ нигде не дублируется.
    """
    names = {"ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY_FILE", "ANTHROPIC_API_KEY_NAME"}
    settings = read_env_file(ENV_FILE, names)
    for name in names:
        if os.environ.get(name):
            settings[name] = os.environ[name]
    if settings.get("ANTHROPIC_API_KEY"):
        return settings["ANTHROPIC_API_KEY"]
    key_file = settings.get("ANTHROPIC_API_KEY_FILE")
    if key_file:
        key_name = settings.get("ANTHROPIC_API_KEY_NAME", "ANTHROPIC_API_KEY")
        value = read_env_file(Path(key_file).expanduser(), {key_name}).get(key_name)
        if value:
            return value
        raise RuntimeError(f"{key_name} не найден в {key_file}")
    raise RuntimeError(f"Нет ключа Anthropic: задай ANTHROPIC_API_KEY или создай {ENV_FILE} (см. .env.example)")


# Отладка: что игра присылает о мире (VCOMP_LOG_STATE=1 при запуске игры)
LOG_STATE = os.environ.get("VCOMP_LOG_STATE") == "1"


def render_state(state: dict, name: str) -> str:
    player = state.get("player_name") or "побратим"
    lines = [
        f"День {state.get('day', '?')}, {state.get('time_of_day', '?')}, погода: {state.get('weather') or 'обычная'}.",
        f"Земли: {state.get('biome') or 'неизвестно'}{'; ' + state['location'] if state.get('location') else ''}.",
        (
            f"{player}: здоровье {state.get('player_hp', 0):.0f}/{state.get('player_hp_max', 0):.0f}, "
            f"груз {state.get('player_weight', 0):.0f}/{state.get('player_weight_max', 0):.0f}, "
            f"в руках: {state.get('player_weapon') or 'ничего'}."
        ),
        (
            f"{name}: здоровье {state.get('companion_hp', 0):.0f}/{state.get('companion_hp_max', 0):.0f}, "
            f"сейчас: {state.get('companion_mode') or 'идёт следом'}, "
            f"несёт: {state.get('companion_carry') or 'ничего'}."
        ),
    ]
    enemies = state.get("enemies") or []
    if enemies:
        parts = [f"{e.get('name')} ({e.get('distance', 0):.0f} м)" for e in enemies[:6]]
        lines.append("Враги рядом: " + ", ".join(parts) + ".")
    else:
        lines.append("Врагов поблизости нет.")
    # Счёт — то, до чего компаньон может дойти и что осилит своим топором и киркой
    trees, rocks = state.get("trees_near", 0), state.get("rocks_near", 0)
    lines.append(
        f"В округе (до 70 м): деревьев {trees}{' (' + state['trees_where'] + ')' if trees and state.get('trees_where') else ''}, "
        f"камней {rocks}{' (' + state['rocks_where'] + ')' if rocks and state.get('rocks_where') else ''}; "
        f"вещей на земле рядом {state.get('items_near', 0)}."
    )
    return "\n".join(lines)


class Brain:
    def __init__(self, name: str, persist: bool = True):
        self.name = name
        self.persist = persist
        self.system = (BASE_DIR / "persona.md").read_text(encoding="utf-8").replace("{name}", name)
        self.client = anthropic.Anthropic(api_key=load_api_key(), timeout=25.0, max_retries=1)
        self.lock = threading.Lock()
        self.history: list[str] = self._load_history() if persist else []
        self.speaker: voice.Speaker | None = None
        self.fillers = True

    def _load_history(self) -> list[str]:
        try:
            data = json.loads(MEMORY_FILE.read_text(encoding="utf-8"))
            return [str(x) for x in data.get("history", [])][-HISTORY_KEPT:]
        except (OSError, ValueError):
            return []

    def _save_history(self) -> None:
        tmp = MEMORY_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps({"history": self.history}, ensure_ascii=False, indent=1), encoding="utf-8")
        tmp.replace(MEMORY_FILE)

    def _incoming(self, kind: str, text: str, player: str) -> tuple[str, str]:
        if kind == "chat":
            return f"{player} говорит тебе: «{text}»", f"{player}: {text}"
        if kind == "voice":
            return (f"{player} говорит тебе вслух: «{text}» (распознано по голосу, отдельные слова могут быть искажены)",
                    f"{player}: {text}")
        return f"Событие: {text}", f"[{text}]"

    def respond(self, kind: str, text: str, state: dict, speak: bool = True, commit: bool = True) -> Reply:
        """Спрашивает Haiku потоком; законченные предложения сразу уходят в речь."""
        player = state.get("player_name") or "Побратим"
        incoming, _ = self._incoming(kind, text, player)
        if LOG_STATE:
            log.info("state for %s: %s", kind, json.dumps(state, ensure_ascii=False))
        with self.lock:
            history = "\n".join(self.history[-HISTORY_IN_PROMPT:]) or "(пока ничего)"
        user = (
            f"<history>\n{history}\n</history>\n\n"
            f"<state>\n{render_state(state, self.name)}\n</state>\n\n"
            f"<now>\n{incoming}\n</now>"
        )

        speaker = self.speaker if speak else None
        feeder = voice.SentenceFeeder(speaker.say) if speaker else None
        started = time.monotonic()
        first_audio = None
        raw = ""
        with self.client.messages.stream(
            model=MODEL,
            max_tokens=1024,
            system=self.system,
            output_config={"effort": "low", "format": {"type": "json_schema", "schema": OUTPUT_SCHEMA}},
            messages=[{"role": "user", "content": user}],
        ) as stream:
            for delta in stream.text_stream:
                raw += delta
                if feeder:
                    said, closed = voice.say_prefix(raw)
                    before = feeder.sent
                    feeder.feed(said, closed)
                    if first_audio is None and feeder.sent > before:
                        first_audio = time.monotonic() - started
            response = stream.get_final_message()
        elapsed = time.monotonic() - started

        if response.stop_reason == "refusal":
            log.warning("refusal (%s) request_id=%s", getattr(response.stop_details, "category", None), response._request_id)
            reply = Reply(say="", action="none")
        else:
            try:
                reply = Reply.model_validate_json(raw)
            except ValidationError:
                log.warning("bad output stop=%s body=%r request_id=%s", response.stop_reason, raw[:200], response._request_id)
                reply = Reply(say="", action="none")
        reply.say = " ".join(reply.say.split())
        if feeder:
            said, _ = voice.say_prefix(raw)
            feeder.feed(said, True)   # договорить хвост без точки в конце
        log.info(
            "%s %r -> say=%r action=%s | %.1fs (речь с %.1fs) in=%d out=%d%s",
            kind, text, reply.say, reply.action, elapsed, first_audio or 0,
            response.usage.input_tokens, response.usage.output_tokens, "" if commit else " [упреждающий]",
        )
        if commit:
            self.commit(kind, text, state, reply)
        return reply

    def commit(self, kind: str, text: str, state: dict, reply: Reply) -> None:
        _, history_line = self._incoming(kind, text, state.get("player_name") or "Побратим")
        with self.lock:
            self.history.append(history_line)
            if reply.say:
                self.history.append(f"{self.name}: {reply.say}")
            if reply.action != "none":
                self.history.append(f"[{self.name}: {reply.action}]")
            self.history = self.history[-HISTORY_KEPT:]
            if self.persist:
                self._save_history()

    def speak(self, text: str) -> None:
        if self.speaker:
            for sentence in voice.SentenceFeeder.split(text):
                self.speaker.say(sentence)


class VoiceSession:
    """Нажатие R: запись, распознавание по ходу речи и упреждающий ответ на паузах."""

    MAX_SPECULATIONS = 6

    def __init__(self, brain: Brain):
        self.brain = brain
        self.stt: voice.STT | None = None
        self.recorder: voice.Recorder | None = None
        self.ready = False
        self.error = ""
        self._lock = threading.Lock()
        self._recording = False
        self._worker: threading.Thread | None = None
        self.partial = ""
        self._state: dict = {}
        self._spec_text = ""
        self._spec: Future | None = None
        self._spec_state: dict = {}
        self._spec_count = 0
        self._partial_samples = 0
        self._spec_at = 0.0
        self._stt_times: list[float] = []
        self._started_at = 0.0
        self._first_text_at = 0.0

    def load(self, stt_model: str, fake_dir: Path | None, device: str = "gpu") -> None:
        try:
            self.stt = voice.STT(stt_model, device)
            self.recorder = voice.Recorder(fake_dir)
            self.ready = True
        except Exception as e:
            self.error = f"stt: {e}"
            log.exception("STT не загрузился")

    def start(self, state: dict, test_audio: str = "") -> dict:
        if not self.ready:
            return {"ok": False, "error": "loading" if not self.error else "stt_failed"}
        if self.brain.speaker:
            self.brain.speaker.stop()  # перебили — замолкает
        with self._lock:
            if self._recording:
                self.recorder.stop()
            self._recording = True
            self.partial = ""
            self._state = state
            self._spec_text = ""
            self._spec = None
            self._spec_count = 0
            self._partial_samples = 0
            self._stt_times = []
            self._started_at = time.monotonic()
            self._first_text_at = 0.0
        try:
            self.recorder.start(test_audio)
        except Exception as e:
            self._recording = False
            log.exception("микрофон не открылся")
            return {"ok": False, "error": f"mic_error: {e}"}
        self._worker = threading.Thread(target=self._listen, daemon=True)
        self._worker.start()
        return {"ok": True, "error": ""}

    def update_state(self, state: dict) -> None:
        # Игра присылает свежую обстановку во время записи: упреждающий ответ видит, что компаньон уже нарубил
        if state and self._recording:
            self._state = state

    def _listen(self) -> None:
        last = ""
        while self._recording:
            audio = self.recorder.snapshot()
            if len(audio) < voice.SAMPLE_RATE // 3:
                time.sleep(0.05)
                continue
            # После последнего распознавания была только тишина — распознавать нечего, бережём процессор
            fresh = audio[self._partial_samples:]
            if self.partial and (len(fresh) < voice.SAMPLE_RATE // 5 or float(abs(fresh).max()) < 0.02):
                if len(fresh) >= voice.SAMPLE_RATE // 5:
                    # Замолчал после фразы — самое время подготовить ответ
                    self._maybe_speculate(self.partial, paused=True)
                time.sleep(0.05)
                continue
            t0 = time.monotonic()
            text = self.stt.transcribe(audio)
            self._stt_times.append(time.monotonic() - t0)
            if not self._recording:
                break
            if text and not self.partial:
                self._first_text_at = time.monotonic() - self._started_at
            self.partial = text
            self._partial_samples = len(audio)
            # Человек замолчал (тишина в хвосте) или текст перестал меняться —
            # заранее готовим ответ, не дожидаясь отпускания кнопки
            self._maybe_speculate(text, voice.speech_paused(audio) or text == last)
            last = text
            time.sleep(0.03)

    def _maybe_speculate(self, text: str, paused: bool) -> None:
        if (paused and len(text.split()) >= 2 and not voice.similar(text, self._spec_text)
                and self._spec_count < self.MAX_SPECULATIONS and time.monotonic() - self._spec_at > 0.5):
            self._speculate(text)

    def _speculate(self, text: str) -> None:
        self._spec_at = time.monotonic()
        self._spec_text = text
        self._spec_count += 1
        future: Future = Future()
        self._spec = future
        state = self._state
        self._spec_state = state

        def run():
            try:
                future.set_result(self.brain.respond("voice", text, state, speak=False, commit=False))
            except Exception as e:
                future.set_exception(e)

        threading.Thread(target=run, daemon=True).start()

    def partial_info(self) -> dict:
        level = self.recorder.level if self.recorder else 0.0
        return {"text": self.partial, "level": level, "recording": self._recording}

    def cancel(self) -> dict:
        with self._lock:
            if self._recording:
                self._recording = False
                self.recorder.stop()
        return {"ok": True}

    def stop(self, state: dict) -> dict:
        t0 = time.monotonic()
        with self._lock:
            if not self._recording:
                return {"transcript": "", "say": "", "action": "none", "error": "not_recording"}
            self._recording = False
            audio = self.recorder.stop()
        if self._worker:
            self._worker.join(timeout=2)
        if len(audio) == 0 or float(abs(audio).max()) < 1e-4:
            # Сплошные нули — macOS не дала доступ к микрофону
            return {"transcript": "", "say": "", "action": "none", "error": "no_mic"}
        # После последнего промежуточного распознавания была только тишина — оно и есть итог
        tail = audio[self._partial_samples:]
        if self.partial and len(tail) < voice.SAMPLE_RATE * 1.5 and (len(tail) == 0 or float(abs(tail).max()) < 0.02):
            text = self.partial
        else:
            text = self.stt.transcribe(audio)
        stt_ms = (time.monotonic() - t0) * 1000
        if not text:
            return {"transcript": "", "say": "", "action": "none", "error": "silence"}

        spec_hit = False
        reply = None
        # Пока говорил, обстановка изменилась (нарубил, начался бой) — заготовка устарела
        stale = any(self._spec_state.get(k) != state.get(k) for k in ("companion_carry", "companion_mode")) \
            or bool(self._spec_state.get("enemies")) != bool(state.get("enemies"))
        if self._spec is not None and voice.similar(self._spec_text, text) and not stale:
            try:
                reply = self._spec.result(timeout=20)
                spec_hit = True
                self.brain.commit("voice", text, state, reply)
                self.brain.speak(reply.say)
            except Exception:
                log.exception("упреждающий ответ не удался")
                reply = None
        if reply is None:
            # Ответ ещё не готов — коротко хмыкнуть, чтобы было слышно, что услышал
            if self.brain.speaker and self.brain.fillers:
                self.brain.speaker.filler()
            reply = self.brain.respond("voice", text, state, speak=True, commit=True)
        total_ms = (time.monotonic() - t0) * 1000
        times = self._stt_times or [0.0]
        log.info("голос: %r | распознано за %.0f мс, ответ через %.0f мс%s | по ходу: %d раз, среднее %.0f мс, "
                 "макс %.0f мс, первый текст через %.1f с",
                 text, stt_ms, total_ms, " (угадан заранее)" if spec_hit else "",
                 len(self._stt_times), sum(times) / len(times) * 1000, max(times) * 1000, self._first_text_at)
        return {"transcript": text, "say": reply.say, "action": reply.action, "error": "",
                "stt_ms": stt_ms, "total_ms": total_ms, "speculative": spec_hit}


def make_handler(brain: Brain, session: VoiceSession):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, code: int, payload: dict) -> None:
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _body(self) -> dict:
            length = int(self.headers.get("Content-Length", "0"))
            return json.loads(self.rfile.read(length).decode("utf-8")) if length else {}

        def do_GET(self):
            if self.path == "/health":
                self._send(200, {"ok": True, "model": MODEL, "name": brain.name,
                                 "voice": session.ready, "tts": brain.speaker is not None})
            elif self.path == "/v1/voice/partial":
                self._send(200, session.partial_info())
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            try:
                req = self._body()
                if self.path == "/v1/event":
                    if req.get("kind") == "chat" and brain.speaker:
                        brain.speaker.stop()  # написал в чат — перебил
                    reply = brain.respond(str(req.get("kind", "event")), str(req.get("text", "")), req.get("state") or {})
                    self._send(200, {"say": reply.say, "action": reply.action, "error": ""})
                elif self.path == "/v1/voice/start":
                    self._send(200, session.start(req.get("state") or {}, str(req.get("test_audio", ""))))
                elif self.path == "/v1/voice/stop":
                    self._send(200, session.stop(req.get("state") or {}))
                elif self.path == "/v1/voice/partial":
                    session.update_state(req.get("state") or {})
                    self._send(200, session.partial_info())
                elif self.path == "/v1/voice/cancel":
                    self._send(200, session.cancel())
                elif self.path == "/v1/speech/stop":
                    if brain.speaker:
                        brain.speaker.stop()
                    self._send(200, {"ok": True})
                else:
                    self._send(404, {"error": "not found"})
            except anthropic.APIConnectionError:
                log.warning("no connection to Anthropic API")
                self._send(200, {"say": "", "action": "none", "error": "connection"})
            except anthropic.RateLimitError:
                log.warning("rate limited")
                self._send(200, {"say": "", "action": "none", "error": "rate_limit"})
            except anthropic.APIStatusError as e:
                log.error("API error %s: %s", e.status_code, e.message)
                self._send(200, {"say": "", "action": "none", "error": f"api_{e.status_code}"})
            except Exception:
                log.exception("request failed")
                self._send(200, {"say": "", "action": "none", "error": "internal"})

        def log_message(self, fmt, *args):
            pass

    return Handler


def watch_parent(pid: int) -> None:
    while True:
        time.sleep(3)
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            log.info("game process %d is gone, exiting", pid)
            os._exit(0)
        except PermissionError:
            pass


def load_voice(brain: Brain, session: VoiceSession, args) -> None:
    if not args.no_tts:
        try:
            if args.voice in ("ruslan", "denis", "dmitri"):
                engine = voice.PiperEngine(VOICES_DIR / f"ru_RU-{args.voice}-medium.onnx", args.pitch)
            else:
                engine = voice.SileroEngine(VOICES_DIR / "silero_v5_ru.pt", args.voice, args.silero_pitch, args.silero_tempo)
            brain.speaker = voice.Speaker(engine, volume=args.volume, mute=args.mute)
        except Exception:
            log.exception("TTS не загрузился — компаньон будет только писать")
    session.load(args.stt_model, Path(args.fake_mic) if args.fake_mic else None, args.stt_device)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--name", default="Бьёрн")
    parser.add_argument("--parent-pid", type=int, default=0)
    parser.add_argument("--voice", default="aidar",
                        help="Silero: aidar, eugene (мужские), baya, kseniya, xenia; Piper: ruslan, denis, dmitri")
    parser.add_argument("--pitch", type=float, default=0.9, help="Piper: <1 — ниже и грубее")
    parser.add_argument("--silero-pitch", default="low", help="Silero: x-low, low, medium, high")
    parser.add_argument("--silero-tempo", default="medium", help="Silero: x-slow, slow, medium, fast")
    parser.add_argument("--volume", type=float, default=1.0)
    parser.add_argument("--no-tts", action="store_true", help="без озвучки")
    parser.add_argument("--no-fillers", action="store_true", help="не хмыкать, пока думает")
    parser.add_argument("--mute", action="store_true", help="озвучка синтезируется, но не играет (тесты)")
    parser.add_argument("--stt-model", default=STT_MODEL)
    # На видеокарте быстрее без игры, но под Valheim первый вызов после паузы идёт 1–1,7 с,
    # а сама игра дёргается на сотни миллисекунд. Процессор: ровные ~0,4 с и без рывков
    parser.add_argument("--stt-device", default="cpu", choices=["gpu", "cpu"])
    parser.add_argument("--fake-mic", help="каталог с wav вместо микрофона (тесты)")
    parser.add_argument("--selftest", help="один запрос из командной строки без сервера")
    args = parser.parse_args()

    DATA_DIR.mkdir(exist_ok=True)
    handlers = [RotatingFileHandler(LOG_FILE, maxBytes=1_000_000, backupCount=2, encoding="utf-8")]
    if args.selftest:
        handlers.append(logging.StreamHandler(sys.stdout))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    for noisy in ("httpx", "httpx2", "anthropic"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    brain = Brain(args.name, persist=not args.selftest)
    brain.fillers = not args.no_fillers
    session = VoiceSession(brain)

    if args.selftest:
        state = {"player_name": "Varg", "day": 3, "time_of_day": "вечер", "biome": "Луга",
                 "player_hp": 60, "player_hp_max": 100, "companion_hp": 250, "companion_hp_max": 250,
                 "trees_near": 12, "rocks_near": 3}
        reply = brain.respond("chat", args.selftest, state, speak=False)
        print(json.dumps(reply.model_dump(), ensure_ascii=False))
        return

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    # Модели голоса грузятся в фоне: текстовый чат работает сразу
    threading.Thread(target=load_voice, args=(brain, session, args), daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(brain, session))
    log.info("bridge listening on 127.0.0.1:%d, model %s, name %s", args.port, MODEL, args.name)
    server.serve_forever()


if __name__ == "__main__":
    main()
