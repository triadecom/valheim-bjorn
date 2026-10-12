"""Мост между модом VikingCompanion и Claude.

Мод шлёт события и сводку обстановки, мост спрашивает Claude и возвращает
реплику и план действий компаньона. Реплики озвучиваются (Silero или Piper), голос игрока
распознаётся (Parakeet) — всё локально, кроме самой LLM. Память своя для каждого мира.
Слушает только 127.0.0.1. Завершается сам, когда умирает процесс игры.
"""

import argparse
import hashlib
import json
import logging
import os
import re
import sys
import threading
import time
from concurrent.futures import Future
from dataclasses import asdict, dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Literal

BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))  # мост запускается с -I, свой каталог добавляем сами

import anthropic  # noqa: E402
from pydantic import BaseModel, ValidationError  # noqa: E402

import voice  # noqa: E402
from memory import Memory  # noqa: E402

STT_MODEL = "mlx-community/parakeet-tdt-0.6b-v3"
ENV_FILE = BASE_DIR / ".env"
PROMPTS_DIR = BASE_DIR / "prompts"

DATA_DIR = BASE_DIR / "data"
VOICES_DIR = DATA_DIR / "voices"
MEMORY_DIR = DATA_DIR / "memory"
LEGACY_MEMORY_FILE = DATA_DIR / "memory.json"
LOG_FILE = DATA_DIR / "bridge.log"

# «Мышление» из настроек: модель и усилие. Быстрый Haiku отвечает голосом почти сразу,
# вдумчивый дольше рассуждает, Sonnet умнее, но заметно медленнее и дороже.
THINKING = {
    "fast": ("claude-haiku-5-5", "low"),
    "thoughtful": ("claude-haiku-5-5", "medium"),
    "smart": ("claude-sonnet-5-5", "low"),
}
MEMORY_MODEL = "claude-haiku-5-5"

ACTIONS = ["follow", "stay", "come", "attack", "gather_wood", "mine", "pick", "hunt", "loot",
           "give_items", "store", "fetch", "combat_style",
           "mark_place", "forget_place", "guard", "repair", "smelt", "collect"]
ActionName = Literal["follow", "stay", "come", "attack", "gather_wood", "mine", "pick", "hunt", "loot",
                     "give_items", "store", "fetch", "combat_style",
                     "mark_place", "forget_place", "guard", "repair", "smelt", "collect"]

OUTPUT_SCHEMA = {
    "type": "object",
    "properties": {
        "say": {
            "type": "string",
            "description": "Что ты говоришь вслух. Пустая строка — промолчать.",
        },
        "plan": {
            "type": "array",
            "description": "Что ты делаешь после этих слов, по порядку. Пустой список — продолжать своё дело.",
            "items": {
                "type": "object",
                "properties": {
                    "do": {"type": "string", "enum": ACTIONS},
                    "what": {"type": "string", "description": "Что именно; пустая строка — не важно."},
                    "count": {"type": "integer", "description": "Сколько; 0 — на твоё усмотрение."},
                    "kind": {"type": "string", "enum": ["home", "portal", "resource", "danger", "landmark"],
                             "description": "Только для mark_place: что это за место."},
                    "note": {"type": "string", "description": "Только для mark_place: что игрок сказал об этом месте."},
                },
                "required": ["do", "what", "count"],
                "additionalProperties": False,
            },
        },
        "remember": {
            "type": "string",
            "description": "Что запомнить надолго, одной фразой. Пустая строка — нечего.",
        },
    },
    "required": ["say", "plan", "remember"],
    "additionalProperties": False,
}

log = logging.getLogger("bridge")


class Step(BaseModel):
    do: ActionName
    what: str = ""
    count: int = 0
    kind: str = ""
    note: str = ""


class Reply(BaseModel):
    say: str
    plan: list[Step] = []
    remember: str = ""

    def payload(self) -> dict:
        # action — первый шаг, для старых версий мода
        return {"say": self.say, "plan": [s.model_dump() for s in self.plan], "action": self.plan[0].do if self.plan else "none"}


@dataclass
class Settings:
    """То, что игрок настраивает на вкладке «Бьёрн» в настройках игры."""
    name: str = "Бьёрн"
    role: str = "viking"            # viking, skald, mentor, jester, friend, custom
    role_custom: str = ""           # своё описание характера
    length: str = "short"           # short, normal, long
    humor: int = 50                 # 0..100
    rudeness: int = 50              # 0..100
    chattiness: str = "normal"      # quiet, normal, chatty
    thinking: str = "fast"          # fast, thoughtful, smart
    memory: bool = True             # учиться и помнить
    tts: bool = True
    voice: str = "aidar"
    tone: str = "low"
    tempo: str = "medium"
    volume: float = 1.0
    fillers: bool = True
    extra: dict = field(default_factory=dict)

    def model(self) -> tuple[str, str]:
        return THINKING.get(self.thinking, THINKING["fast"])


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


def _near_list(items: list | None, unit: str = "") -> str:
    parts = []
    for it in (items or [])[:8]:
        where = f", ближ. {it.get('nearest', 0):.0f} м {it.get('where', '')}".rstrip() if it.get("nearest") is not None else ""
        parts.append(f"{it.get('name')} ×{it.get('count', 0)}{unit}{where}")
    return "; ".join(parts)


def render_state(state: dict, name: str) -> str:
    player = state.get("player_name") or "игрок"
    lines = [
        f"День {state.get('day', '?')}, {state.get('time_of_day', '?')}, погода: {state.get('weather') or 'обычная'}.",
        f"Земли: {state.get('biome') or 'неизвестно'}{'; ' + state['location'] if state.get('location') else ''}."
        + (f" Дом игрока (кровать): {state['home']}." if state.get("home") else ""),
        (
            f"{player}: здоровье {state.get('player_hp', 0):.0f}/{state.get('player_hp_max', 0):.0f}, "
            f"груз {state.get('player_weight', 0):.0f}/{state.get('player_weight_max', 0):.0f}, "
            f"в руках: {state.get('player_weapon') or 'ничего'}, сытных блюд съедено: {state.get('player_foods', 0)} из 3."
        ),
    ]
    if state.get("player_items"):
        lines.append(f"В мешке у {player}: {state['player_items']}.")
    plan = f", дальше по плану: {state['companion_plan']}" if state.get("companion_plan") else ""
    arrows = state.get("companion_arrows", 0)
    weapons = f"бьётся: {state.get('companion_style') or 'топором и щитом'}"
    if state.get("companion_bow"):
        weapons += f"; лук {state['companion_bow']}, стрел {arrows}"
    lines.append(
        f"{name}: здоровье {state.get('companion_hp', 0):.0f}/{state.get('companion_hp_max', 0):.0f}, "
        f"сейчас: {state.get('companion_mode') or 'идёт следом'}{plan}; {weapons}; "
        f"несёт: {state.get('companion_carry') or 'ничего'}."
    )
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
        f"камней и руды {rocks}{' (' + state['rocks_where'] + ')' if rocks and state.get('rocks_where') else ''}; "
        f"вещей на земле рядом {state.get('items_near', 0)}."
    )
    plants = _near_list(state.get("plants"))
    lines.append(f"Растения и находки рядом: {plants}." if plants else "Ягод, грибов и трав рядом не видно.")
    animals = _near_list(state.get("animals"))
    lines.append(f"Дичь рядом: {animals}." if animals else "Дичи рядом не видно.")
    chests = state.get("chests") or []
    if chests:
        for ch in chests[:2]:
            contents = ch.get("contents") or "пусто"
            lines.append(f"Сундук Бьёрна в {ch.get('distance', 0):.0f} м {ch.get('where', '')}: {contents} "
                         f"(занято ячеек {ch.get('used', 0)} из {ch.get('total', 0)}).")
    else:
        lines.append("Сундука Бьёрна поблизости нет.")
    furnaces = state.get("furnaces") or []
    for f in furnaces[:6]:
        fuel = f" топливо ({f.get('fuel_item')}) {f.get('fuel', 0)}/{f.get('fuel_max', 0)}," if f.get("fuel_max") else ""
        ready = f" готово к выдаче {f.get('ready')}," if f.get("ready") else ""
        lines.append(f"Печь «{f.get('name')}» в {f.get('distance', 0):.0f} м {f.get('where', '')}:{fuel}{ready} "
                     f"в очереди {f.get('queued', 0)}/{f.get('queue_max', 0)} (принимает: {f.get('inputs') or '?'}).")
    if state.get("damaged_near"):
        lines.append(f"Повреждённых построек рядом: {state['damaged_near']}.")
    places = state.get("places") or []
    if places:
        parts = []
        for p in places[:12]:
            where = "вы здесь" if p.get("here") else f"{p.get('distance', 0):.0f} м {p.get('where', '')}"
            text = f"«{p.get('name')}» ({p.get('kind')}, {where})"
            if p.get("note"):
                text += f" — {p['note']}"
            if p.get("about") and (p.get("here") or p.get("kind") == "база"):
                text += f" [{p['about']}]"
            parts.append(text)
        lines.append("Места, что ты знаешь: " + "; ".join(parts) + ".")
    else:
        lines.append("Мест ты пока не запоминал.")
    pins = state.get("pins") or []
    if pins:
        lines.append("Метки игрока на карте: " + ", ".join(
            f"«{p.get('name')}» ({p.get('nearest', 0):.0f} м {p.get('where', '')})" for p in pins[:8]) + ".")
    if state.get("guarding"):
        lines.append(f"Ты сейчас сторожишь «{state['guarding']}».")
    return "\n".join(lines)


def read_prompt(name: str) -> str:
    text = (PROMPTS_DIR / name).read_text(encoding="utf-8")
    if text.startswith("---"):
        text = text.split("---", 2)[2]   # заголовок с названием роли — для людей
    return text.strip()


ROLES = ["viking", "skald", "mentor", "jester", "friend", "custom"]


def role_title(role: str) -> str:
    text = (PROMPTS_DIR / f"roles/{role}.md").read_text(encoding="utf-8")
    m = re.search(r"^title:\s*(.+)$", text, re.M)
    return m.group(1).strip() if m else role


def role_signature(cfg: "Settings") -> str:
    """Что меняет голос персонажа: роль, своя роль и имя."""
    custom = " ".join(cfg.role_custom.split())
    return f"{cfg.role}|{cfg.name}|{hashlib.sha1(custom.encode()).hexdigest()[:8]}"

LENGTH = {
    "short": "Говори коротко: одна-две фразы, обычно до 20 слов. Это слова в пути и в бою, а не сага.",
    "normal": "Говори коротко, но не односложно: две-три фразы, до 40 слов.",
    "long": "Можешь говорить развёрнуто: до пяти фраз, если есть что сказать. На простые вопросы и в бою — коротко.",
}

CHATTINESS = {
    "quiet": "Ты молчалив. На события откликайся, только если это действительно важно: опасность, рана, итог дела. Иначе say — пустая строка.",
    "normal": "",
    "chatty": "Ты разговорчив: охотно откликаешься на события, вспоминаешь былое, сам расспрашиваешь игрока о планах.",
}


def scale(value: int, low: str, mid: str, high: str, top: str) -> str:
    if value < 25:
        return low
    if value < 60:
        return mid
    if value < 85:
        return high
    return top


def build_style(cfg: Settings) -> str:
    lines = ["# Манера (настроил игрок)", LENGTH.get(cfg.length, LENGTH["short"])]
    lines.append(scale(cfg.humor, "Шутишь редко, держишься серьёзно.", "Шутишь иногда, к месту.",
                       "Шутишь часто.", "Шутишь постоянно, балагуришь при любом случае."))
    lines.append(scale(cfg.rudeness, "Говоришь мягко и по-доброму, без грубостей.", "Прям и порой резковат, но не груб.",
                       "Грубоват и дерзок, любишь поддеть игрока.",
                       "Груб и язвителен на словах (но без мата), хотя в деле верен до конца."))
    if CHATTINESS.get(cfg.chattiness):
        lines.append(CHATTINESS[cfg.chattiness])
    return "\n".join(lines)


def build_system(cfg: Settings) -> str:
    role = cfg.role if cfg.role in ROLES else "viking"
    parts = [read_prompt(f"roles/{role}.md")]
    custom = " ".join(cfg.role_custom.split())[:1500]
    if custom:
        parts.append("# Каким тебя хочет видеть игрок\n" + custom
                     + "\nЭто описание важнее общих слов о характере выше, если они расходятся.")
    parts += [build_style(cfg), read_prompt("rules.md"), read_prompt("lore.md")]
    return "\n\n".join(parts).replace("{name}", cfg.name)


def plan_text(plan: list[Step]) -> str:
    out = []
    for step in plan:
        item = step.do
        if step.what:
            item += f" «{step.what}»"
        if step.count:
            item += f" ×{step.count}"
        if step.kind:
            item += f" [{step.kind}]"
        if step.note:
            item += f" ({step.note})"
        out.append(item)
    return ", ".join(out)


class Brain:
    def __init__(self, cfg: Settings, persist: bool = True, memory_dir: Path | None = None):
        self.cfg = cfg
        self.client = anthropic.Anthropic(api_key=load_api_key(), timeout=30.0, max_retries=1)
        self.lock = threading.Lock()
        # Своя папка памяти (тесты) — без переноса старой общей памяти
        self.memory = Memory(memory_dir, None) if memory_dir else Memory(MEMORY_DIR, LEGACY_MEMORY_FILE)
        self.memory.persist = persist
        self.memory.enabled = cfg.memory
        self.system = build_system(cfg)
        self.speaker: voice.Speaker | None = None
        self.voice_loader = None   # функция, которая пересобирает голос по настройкам

    @property
    def name(self) -> str:
        return self.cfg.name

    @property
    def fillers(self) -> bool:
        return self.cfg.fillers and self.cfg.tts

    def configure(self, changes: dict) -> dict:
        """Новые настройки из игры. Голос пересобирается в фоне, характер — сразу."""
        with self.lock:
            old = self.cfg
            data = asdict(old)
            for key, value in changes.items():
                if key in data and key != "extra" and value is not None:
                    try:
                        data[key] = type(data[key])(value)
                    except (TypeError, ValueError):
                        log.warning("настройка %s=%r не подошла", key, value)
            self.cfg = Settings(**data)
            self.system = build_system(self.cfg)
            self.memory.enabled = self.cfg.memory
            cfg = self.cfg
        if self.speaker:
            self.speaker.volume = cfg.volume
        voice_changed = (old.voice, old.tone, old.tempo) != (cfg.voice, cfg.tone, cfg.tempo) \
            or (cfg.tts and self.speaker is None)
        if voice_changed and self.voice_loader:
            threading.Thread(target=self.voice_loader, args=(cfg,), daemon=True).start()
        if cfg != old:
            model, effort = cfg.model()
            log.info("настройки: роль %s, длина %s, юмор %d, резкость %d, болтливость %s, %s/%s, память %s, голос %s%s",
                     cfg.role, cfg.length, cfg.humor, cfg.rudeness, cfg.chattiness, model, effort,
                     "вкл" if cfg.memory else "выкл", cfg.voice if cfg.tts else "выкл",
                     f", своя роль {len(cfg.role_custom)} зн." if cfg.role_custom else "")
        return {"ok": True}

    def _select_memory(self, state: dict) -> None:
        if state.get("world") or state.get("player_name"):
            self.memory.select(state.get("world", ""), state.get("player_name", ""))

    def _incoming(self, kind: str, text: str, player: str) -> tuple[str, str]:
        if kind == "chat":
            return f"{player} говорит тебе: «{text}»", f"{player}: {text}"
        if kind == "voice":
            return (f"{player} говорит тебе вслух: «{text}» (распознано по голосу, отдельные слова могут быть искажены)",
                    f"{player}: {text}")
        return f"Событие: {text}", f"[{text}]"

    def _system_blocks(self) -> list[dict]:
        # Роль, манера, правила и знания не меняются от реплики к реплике — их кэширует API.
        # Память меняется только после размышлений — отдельный блок со своей точкой кэша.
        with self.lock:
            blocks = [{"type": "text", "text": self.system, "cache_control": {"type": "ephemeral"}}]
        remembered = self.memory.prompt_block()
        if remembered:
            blocks.append({"type": "text", "text": remembered, "cache_control": {"type": "ephemeral"}})
        return blocks

    def respond(self, kind: str, text: str, state: dict, speak: bool = True, commit: bool = True) -> Reply:
        """Спрашивает Claude потоком; законченные предложения сразу уходят в речь."""
        self._select_memory(state)
        with self.lock:
            cfg = self.cfg
        self.memory.mark_role(role_signature(cfg), role_title(cfg.role if cfg.role in ROLES else "viking"))
        player = state.get("player_name") or "Игрок"
        incoming, _ = self._incoming(kind, text, player)
        if LOG_STATE:
            log.info("state for %s: %s", kind, json.dumps(state, ensure_ascii=False))
        user = (
            f"<history>\n{self.memory.recent()}\n</history>\n\n"
            f"<state>\n{render_state(state, self.name)}\n</state>\n\n"
            f"<now>\n{incoming}\n</now>"
        )
        model, effort = self.cfg.model()
        speaker = self.speaker if speak and self.cfg.tts else None
        feeder = voice.SentenceFeeder(speaker.say) if speaker else None
        started = time.monotonic()
        first_audio = None
        raw = ""
        with self.client.messages.stream(
            model=model,
            max_tokens=2048,
            system=self._system_blocks(),
            output_config={"effort": effort, "format": {"type": "json_schema", "schema": OUTPUT_SCHEMA}},
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
            reply = Reply(say="")
        else:
            try:
                reply = Reply.model_validate_json(raw)
            except ValidationError:
                log.warning("bad output stop=%s body=%r request_id=%s", response.stop_reason, raw[:300], response._request_id)
                reply = Reply(say="")
        reply.say = " ".join(reply.say.split())
        if feeder:
            said, _ = voice.say_prefix(raw)
            feeder.feed(said, True)   # договорить хвост без точки в конце
        usage = response.usage
        log.info(
            "%s %r -> say=%r plan=[%s]%s | %s %.1fs (речь с %.1fs) in=%d cache r/w=%d/%d out=%d%s",
            kind, text, reply.say, plan_text(reply.plan), f" remember={reply.remember!r}" if reply.remember else "",
            model, elapsed, first_audio or 0, usage.input_tokens, usage.cache_read_input_tokens or 0,
            usage.cache_creation_input_tokens or 0, usage.output_tokens, "" if commit else " [упреждающий]",
        )
        if commit:
            self.commit(kind, text, state, reply)
        return reply

    def commit(self, kind: str, text: str, state: dict, reply: Reply) -> None:
        self._select_memory(state)
        _, history_line = self._incoming(kind, text, state.get("player_name") or "Игрок")
        lines = [history_line]
        if reply.say:
            lines.append(f"{self.name}: {reply.say}")
        if reply.plan:
            lines.append(f"[{self.name}: план — {plan_text(reply.plan)}]")
        self.memory.add_lines(lines)
        if reply.remember:
            self.memory.remember(reply.remember)
        self.memory.maybe_reflect(self.client, MEMORY_MODEL, self.name)

    def note(self, text: str, state: dict) -> None:
        self._select_memory(state)
        self.memory.note(text, state.get("day"))
        self.memory.maybe_reflect(self.client, MEMORY_MODEL, self.name)

    def speak(self, text: str) -> None:
        if self.speaker and self.cfg.tts:
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
                return {"transcript": "", "say": "", "plan": [], "error": "not_recording"}
            self._recording = False
            audio = self.recorder.stop()
        if self._worker:
            self._worker.join(timeout=2)
        if len(audio) == 0 or float(abs(audio).max()) < 1e-4:
            # Сплошные нули — macOS не дала доступ к микрофону
            return {"transcript": "", "say": "", "plan": [], "error": "no_mic"}
        # После последнего промежуточного распознавания была только тишина — оно и есть итог
        tail = audio[self._partial_samples:]
        if self.partial and len(tail) < voice.SAMPLE_RATE * 1.5 and (len(tail) == 0 or float(abs(tail).max()) < 0.02):
            text = self.partial
        else:
            text = self.stt.transcribe(audio)
        stt_ms = (time.monotonic() - t0) * 1000
        if not text:
            return {"transcript": "", "say": "", "plan": [], "error": "silence"}

        spec_hit = False
        reply = None
        # Пока говорил, обстановка изменилась (нарубил, начался бой) — заготовка устарела
        stale = any(self._spec_state.get(k) != state.get(k) for k in ("companion_carry", "companion_mode", "companion_plan")) \
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
        return {"transcript": text, **reply.payload(), "error": "",
                "stt_ms": stt_ms, "total_ms": total_ms, "speculative": spec_hit}


ERROR_REPLY = {"say": "", "plan": [], "action": "none"}


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

        def _memory_info(self) -> dict:
            mem = brain.memory
            with mem.lock:
                return {"world": mem.world, "player": mem.player, "facts": [f["text"] for f in mem.facts],
                        "summary": mem.summary, "history": len(mem.history), "enabled": mem.enabled}

        def do_GET(self):
            if self.path == "/health":
                model, effort = brain.cfg.model()
                self._send(200, {"ok": True, "model": model, "effort": effort, "name": brain.name,
                                 "voice": session.ready, "tts": brain.speaker is not None})
            elif self.path == "/v1/voice/partial":
                self._send(200, session.partial_info())
            elif self.path == "/v1/memory":
                self._send(200, self._memory_info())
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            try:
                req = self._body()
                if self.path == "/v1/event":
                    if req.get("kind") == "chat" and brain.speaker:
                        brain.speaker.stop()  # написал в чат — перебил
                    reply = brain.respond(str(req.get("kind", "event")), str(req.get("text", "")), req.get("state") or {})
                    self._send(200, {**reply.payload(), "error": ""})
                elif self.path == "/v1/note":
                    brain.note(str(req.get("text", "")), req.get("state") or {})
                    self._send(200, {"ok": True})
                elif self.path == "/v1/config":
                    self._send(200, brain.configure(req))
                elif self.path == "/v1/memory":
                    state = req.get("state") or {}
                    brain._select_memory(state)
                    self._send(200, self._memory_info())
                elif self.path == "/v1/memory/forget":
                    brain._select_memory(req.get("state") or {})
                    brain.memory.forget()
                    self._send(200, {"ok": True})
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
                self._send(200, {**ERROR_REPLY, "error": "connection"})
            except anthropic.RateLimitError:
                log.warning("rate limited")
                self._send(200, {**ERROR_REPLY, "error": "rate_limit"})
            except anthropic.APIStatusError as e:
                log.error("API error %s: %s", e.status_code, e.message)
                self._send(200, {**ERROR_REPLY, "error": f"api_{e.status_code}"})
            except Exception:
                log.exception("request failed")
                self._send(200, {**ERROR_REPLY, "error": "internal"})

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


def make_engine(cfg: Settings, args):
    if cfg.voice in ("ruslan", "denis", "dmitri"):
        return voice.PiperEngine(VOICES_DIR / f"ru_RU-{cfg.voice}-medium.onnx", args.pitch)
    return voice.SileroEngine(VOICES_DIR / "silero_v5_ru.pt", cfg.voice, cfg.tone, cfg.tempo)


def load_voice(brain: Brain, session: VoiceSession, args) -> None:
    lock = threading.Lock()

    def reload(cfg: Settings) -> None:
        # Новый голос из настроек: собираем движок в фоне, старый говорит, пока новый не готов
        with lock:
            try:
                engine = make_engine(cfg, args)
                if brain.speaker:
                    brain.speaker.set_engine(engine)
                else:
                    brain.speaker = voice.Speaker(engine, volume=cfg.volume, mute=args.mute)
                log.info("голос сменён: %s (%s, %s)", cfg.voice, cfg.tone, cfg.tempo)
            except Exception:
                log.exception("голос %s не загрузился", cfg.voice)

    loaded = brain.cfg
    if not args.no_tts:
        try:
            brain.speaker = voice.Speaker(make_engine(loaded, args), volume=loaded.volume, mute=args.mute)
        except Exception:
            log.exception("TTS не загрузился — компаньон будет только писать")
    # Пока голос грузился, игра могла прислать другие настройки голоса
    brain.voice_loader = reload
    now = brain.cfg
    if (now.voice, now.tone, now.tempo) != (loaded.voice, loaded.tone, loaded.tempo) or (now.tts and brain.speaker is None):
        reload(now)
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
    parser.add_argument("--config", help="JSON с настройками характера (роль, длина ответов, мышление…)")
    parser.add_argument("--memory-dir", help="другая папка для памяти (тесты)")
    parser.add_argument("--state", help="JSON со сводкой обстановки для --selftest")
    args = parser.parse_args()

    DATA_DIR.mkdir(exist_ok=True)
    handlers = [RotatingFileHandler(LOG_FILE, maxBytes=1_000_000, backupCount=2, encoding="utf-8")]
    if args.selftest:
        handlers.append(logging.StreamHandler(sys.stdout))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    for noisy in ("httpx", "httpx2", "anthropic"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    cfg = Settings(name=args.name, voice=args.voice, tone=args.silero_pitch, tempo=args.silero_tempo,
                   volume=args.volume, tts=not args.no_tts, fillers=not args.no_fillers)
    if args.config:
        # Настройки характера при запуске; дальше мод присылает их через /v1/config
        try:
            changes = json.loads(Path(args.config).read_text(encoding="utf-8"))
            cfg = Settings(**{**asdict(cfg), **{k: v for k, v in changes.items() if k in asdict(cfg)}})
        except (OSError, ValueError, TypeError):
            log.exception("настройки из %s не прочитались", args.config)
    brain = Brain(cfg, persist=not args.selftest, memory_dir=Path(args.memory_dir) if args.memory_dir else None)
    session = VoiceSession(brain)

    if args.selftest:
        state = {"player_name": "Varg", "day": 3, "time_of_day": "вечер", "biome": "Луга",
                 "player_hp": 60, "player_hp_max": 100, "companion_hp": 250, "companion_hp_max": 250,
                 "trees_near": 12, "rocks_near": 3}
        if args.state:
            state.update(json.loads(Path(args.state).read_text(encoding="utf-8")))
        reply = brain.respond("chat", args.selftest, state, speak=False)
        print(json.dumps(reply.model_dump(), ensure_ascii=False))
        return

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    # Модели голоса грузятся в фоне: текстовый чат работает сразу
    threading.Thread(target=load_voice, args=(brain, session, args), daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(brain, session))
    log.info("bridge listening on 127.0.0.1:%d, model %s, name %s", args.port, cfg.model()[0], cfg.name)
    server.serve_forever()


if __name__ == "__main__":
    main()
