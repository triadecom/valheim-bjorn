"""Долгая память компаньона: своя для каждого мира и персонажа.

Что хранится (data/memory/<мир>__<персонаж>.json):
- history — последние реплики и события, дословно (их видит каждый запрос);
- facts — то, что компаньон усвоил надолго: о побратиме, о мире, уроки, договорённости;
- summary — короткая летопись того, что было раньше истории;
- journal — заметные события игры (смерти, победы, ремесло), сырьё для размышлений.

Компаньон учится сам: когда накопилось достаточно нового, мост в фоне просит модель
обновить факты и летопись. Ещё модель может записать факт сразу (поле remember в ответе).
"""

import json
import logging
import re
import threading
import time
from pathlib import Path

log = logging.getLogger("bridge.memory")

HISTORY_IN_PROMPT = 30
HISTORY_KEPT = 120
JOURNAL_KEPT = 200
MAX_FACTS = 40
FACT_CHARS = 160
SUMMARY_CHARS = 1200
# Сколько новых строк истории или событий копится до очередного размышления
REFLECT_EVERY = 16

REFLECT_SCHEMA = {
    "type": "object",
    "properties": {
        "summary": {
            "type": "string",
            "description": "Летопись: самое важное, что было с вами, от начала до сегодня. До 900 знаков.",
        },
        "facts": {
            "type": "array",
            "description": "Полный обновлённый список того, что стоит помнить долго.",
            "items": {
                "type": "object",
                "properties": {
                    "text": {"type": "string"},
                    "kind": {"type": "string", "enum": ["player", "world", "lesson", "bond"]},
                },
                "required": ["text", "kind"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["summary", "facts"],
    "additionalProperties": False,
}

REFLECT_PROMPT = """Ты — память {name}, спутника игрока в мире Валхейма. Твоя задача — решить, что {name} запомнит надолго.

Тебе дают то, что {name} уже помнит, и то, что случилось с тех пор. Верни обновлённые летопись и список фактов.

Что стоит помнить (kind):
- player — о побратиме: имя и как он любит, чтобы его звали; что любит и чего не терпит; как обычно играет; о чём просил не делать или делать всегда.
- world — о мире: где дом и кровать, порталы, залежи руды, опасные места, какие боссы уже повержены.
- lesson — уроки самого {name}: на чём ошибся, за что его поправили, что сработало.
- bond — ваши отношения: клятвы, общие победы и потери, шутки, которые стали общими.

Правила:
- Просьбы побратима о твоём поведении («не болтай в бою», «зови меня ярлом», «не трогай мои грядки») сохраняй обязательно и дословно по смыслу — это самое важное.
- Каждый факт — одна короткая фраза в третьем лице о побратиме или от лица {name} («Побратима зовут Варг, любит, когда его зовут ярлом», «Я слишком много болтаю — побратим просил молчать в бою»).
- Объединяй повторы, убирай устаревшее (если новое противоречит старому — оставь новое). Не больше {max_facts} фактов, самые важные — первыми.
- Не храни мелочи: погоду, время суток, разовые приказы и их итоги («наруби дров», «сложил малину в сундук»), что сейчас лежит в сундуках — этому место разве что в летописи. Факт — то, что пригодится и через неделю.
- Летопись — связный короткий рассказ: что вы прошли, кого победили, где живёте, что сейчас главное. Не дублируй в ней факты дословно.
- Не выдумывай: только то, что есть в данных."""


def slug(text: str) -> str:
    text = re.sub(r"[^\w-]+", "_", (text or "").strip(), flags=re.UNICODE).strip("_")
    return text[:60] or "default"


class Memory:
    def __init__(self, base_dir: Path, legacy_file: Path | None = None):
        self.base_dir = base_dir
        self.legacy_file = legacy_file
        self.enabled = True
        self.persist = True
        self.lock = threading.RLock()
        self.key = ""
        self.world = ""
        self.player = ""
        self.history: list[str] = []
        self.facts: list[dict] = []
        self.summary = ""
        self.journal: list[str] = []
        self.pending = 0
        self.revision = 0
        self._reflecting = False
        self._last_reflect = 0.0

    # ---------- Файлы ----------

    @property
    def path(self) -> Path:
        return self.base_dir / f"{self.key}.json"

    def select(self, world: str, player: str) -> None:
        """Переключиться на память нужного мира и персонажа (если они сменились)."""
        key = f"{slug(world)}__{slug(player)}"
        with self.lock:
            if key == self.key:
                return
            self.key, self.world, self.player = key, world or "", player or ""
            self.history, self.facts, self.summary, self.journal, self.pending = [], [], "", [], 0
            if not self.persist:
                return
            try:
                data = json.loads(self.path.read_text(encoding="utf-8"))
                self.history = [str(x) for x in data.get("history", [])][-HISTORY_KEPT:]
                self.facts = [f for f in data.get("facts", []) if isinstance(f, dict) and f.get("text")][:MAX_FACTS]
                self.summary = str(data.get("summary", ""))
                self.journal = [str(x) for x in data.get("journal", [])][-JOURNAL_KEPT:]
                self.pending = int(data.get("pending", 0))
            except FileNotFoundError:
                self._import_legacy()
            except (OSError, ValueError):
                log.exception("память %s не читается — начинаю с чистого листа", self.path.name)
            self.revision += 1
            log.info("память: %s (фактов %d, строк истории %d)", self.key, len(self.facts), len(self.history))

    def _import_legacy(self) -> None:
        # Раньше вся память лежала одним файлом без привязки к миру — отдаём её первому миру
        if not self.legacy_file or not self.legacy_file.is_file():
            return
        try:
            data = json.loads(self.legacy_file.read_text(encoding="utf-8"))
            self.history = [str(x) for x in data.get("history", [])][-HISTORY_KEPT:]
            self.pending = len(self.history)
            self.legacy_file.replace(self.legacy_file.with_suffix(".legacy.json"))
            log.info("старая память (%d строк) перенесена в %s", len(self.history), self.key)
            self.save()
        except (OSError, ValueError):
            log.exception("старая память не перенеслась")

    def save(self) -> None:
        if not self.persist or not self.key:
            return
        with self.lock:
            data = {
                "world": self.world, "player": self.player, "updated": time.strftime("%Y-%m-%d %H:%M:%S"),
                "facts": self.facts, "summary": self.summary, "history": self.history,
                "journal": self.journal, "pending": self.pending,
            }
            self.base_dir.mkdir(parents=True, exist_ok=True)
            tmp = self.path.with_suffix(".tmp")
            tmp.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
            tmp.replace(self.path)

    def forget(self) -> None:
        with self.lock:
            self.history, self.facts, self.summary, self.journal, self.pending = [], [], "", [], 0
            self.revision += 1
            if self.persist and self.key and self.path.exists():
                self.path.unlink()
        log.info("память %s стёрта", self.key)

    # ---------- Запись ----------

    def add_lines(self, lines: list[str]) -> None:
        with self.lock:
            self.history.extend(lines)
            self.history = self.history[-HISTORY_KEPT:]
            self.pending += len(lines)
            self.save()

    def note(self, text: str, day: int | None = None) -> None:
        """Событие игры без ответа модели: идёт в историю и в журнал для размышлений."""
        text = " ".join(text.split())
        if not text:
            return
        with self.lock:
            self.journal.append(f"день {day}: {text}" if day else text)
            self.journal = self.journal[-JOURNAL_KEPT:]
            self.history.append(f"[{text}]")
            self.history = self.history[-HISTORY_KEPT:]
            self.pending += 2   # события важнее болтовни — размышлять о них раньше
            self.save()

    def remember(self, text: str, kind: str = "player") -> None:
        text = " ".join(text.split())[:FACT_CHARS]
        if not text or not self.enabled:
            return
        with self.lock:
            low = text.lower()
            if any(f["text"].lower() == low for f in self.facts):
                return
            self.facts.insert(0, {"text": text, "kind": kind, "added": time.strftime("%Y-%m-%d")})
            self.facts = self.facts[:MAX_FACTS]
            self.revision += 1
            self.save()
        log.info("запомнил: %s", text)

    # ---------- Для промпта ----------

    def recent(self) -> str:
        with self.lock:
            return "\n".join(self.history[-HISTORY_IN_PROMPT:]) or "(пока ничего)"

    def prompt_block(self) -> str:
        """Что компаньон помнит — отдельный блок системного промпта (меняется только после размышлений)."""
        with self.lock:
            if not self.enabled or (not self.facts and not self.summary):
                return ""
            parts = ["# Что ты помнишь"]
            if self.summary:
                parts.append("Летопись: " + self.summary)
            if self.facts:
                parts.append("\n".join(f"- {f['text']}" for f in self.facts))
            parts.append("Опирайся на это, когда к месту: обращайся к побратиму так, как он любит, вспоминай общее прошлое, "
                         "не повторяй ошибок из уроков. Не пересказывай память без повода.")
            return "\n".join(parts)

    # ---------- Размышления ----------

    def maybe_reflect(self, client, model: str, name: str) -> None:
        with self.lock:
            due = self.enabled and self.persist and self.pending >= REFLECT_EVERY and not self._reflecting
            # Не чаще раза в минуту, даже если события сыплются
            if not due or time.monotonic() - self._last_reflect < 60:
                return
            self._reflecting = True
            self._last_reflect = time.monotonic()
        threading.Thread(target=self._reflect, args=(client, model, name), daemon=True).start()

    def reflect_now(self, client, model: str, name: str) -> None:
        with self.lock:
            if self._reflecting:
                return
            self._reflecting = True
        self._reflect(client, model, name)

    def _reflect(self, client, model: str, name: str) -> None:
        started = time.monotonic()
        try:
            with self.lock:
                key = self.key
                consumed = self.pending
                facts_before = [f["text"] for f in self.facts]
                facts = "\n".join(f"- [{f.get('kind', 'player')}] {f['text']}" for f in self.facts) or "(пусто)"
                summary = self.summary or "(пусто)"
                fresh_history = self.history[-max(consumed, 1):][-80:]
                fresh_journal = self.journal[-40:]
            user = (
                f"<что_помнит>\nЛетопись: {summary}\nФакты:\n{facts}\n</что_помнит>\n\n"
                f"<журнал_событий>\n" + ("\n".join(fresh_journal) or "(пусто)") + "\n</журнал_событий>\n\n"
                f"<новые_разговоры>\n" + "\n".join(fresh_history) + "\n</новые_разговоры>"
            )
            response = client.messages.create(
                model=model,
                max_tokens=4000,
                system=REFLECT_PROMPT.format(name=name, max_facts=MAX_FACTS),
                output_config={"effort": "low", "format": {"type": "json_schema", "schema": REFLECT_SCHEMA}},
                messages=[{"role": "user", "content": user}],
            )
            if response.stop_reason == "refusal":
                log.warning("размышление: отказ модели")
                return
            text = next((b.text for b in response.content if b.type == "text"), "")
            data = json.loads(text)
            new_facts = []
            for f in data.get("facts", [])[:MAX_FACTS]:
                t = " ".join(str(f.get("text", "")).split())[:FACT_CHARS]
                if t:
                    new_facts.append({"text": t, "kind": f.get("kind", "player")})
            with self.lock:
                if self.key != key:
                    return   # пока думал, игрок ушёл в другой мир
                # Что успели запомнить, пока модель думала, — не терять
                fresh = [f for f in self.facts if f["text"] not in facts_before]
                known = {f["text"].lower() for f in new_facts}
                self.facts = ([f for f in fresh if f["text"].lower() not in known] + new_facts)[:MAX_FACTS]
                self.summary = str(data.get("summary", ""))[:SUMMARY_CHARS]
                self.pending = max(0, self.pending - consumed)
                self.revision += 1
                self.save()
            log.info("размышление: фактов %d, летопись %d знаков, %.1fs, in=%d out=%d",
                     len(new_facts), len(self.summary), time.monotonic() - started,
                     response.usage.input_tokens, response.usage.output_tokens)
        except Exception:
            log.exception("размышление не удалось")
        finally:
            with self.lock:
                self._reflecting = False
