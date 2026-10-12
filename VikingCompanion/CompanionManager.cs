using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VikingCompanion
{
    // Появление компаньона, следование через порталы, события и разговоры.
    internal static class CompanionManager
    {
        private static float s_respawnTimer;
        private static ZDOID s_zdo = ZDOID.None;
        private static bool s_greeted;
        private static bool s_returning;
        private static bool s_parked;

        public static bool IsParked => s_parked;
        private static Heightmap.Biome s_lastBiome = Heightmap.Biome.None;
        private static int s_lastDayPhase = -1;
        private static float s_lastComment = -999f;
        private static float s_nextIdle;
        private static float s_lastCombat = -999f;
        private static float s_lastCompanionHurt = -999f;
        private static float s_lastPlayerHurt = -999f;
        private static float s_nextSlow;
        private static int s_lastDay = -1;
        private static Player s_player;
        private static readonly HashSet<Heightmap.Biome> s_seenBiomes = new HashSet<Heightmap.Biome>();

        // Болтливость: quiet — только важное, chatty — чаще и с разговорами в пути
        private static float CommentCooldown => Plugin.Chattiness.Value == "chatty" ? 25f : 60f;
        private static bool Proactive => Plugin.Chattiness.Value != "quiet";
        private static float NextIdleDelay => Plugin.Chattiness.Value == "chatty" ? Random.Range(180f, 360f) : Random.Range(420f, 720f);

        public static void Update(float dt)
        {
            Player player = Player.m_localPlayer;
            if (player != s_player)
            {
                ResetSession(player);
            }
            if (player == null || ZNetScene.instance == null || CompanionPrefab.Prefab == null)
            {
                return;
            }
            if (player.IsDead() || player.InIntro())
            {
                return;
            }
            Bridge.EnsureStarted();

            Companion c = Companion.Local;
            if (c == null)
            {
                if (s_respawnTimer > 0f)
                {
                    s_respawnTimer -= dt;
                    return;
                }
                // Сторожит в выгруженной зоне — вернётся сам, когда игрок подойдёт
                if (s_parked && ZDOMan.instance.GetZDO(s_zdo) != null)
                {
                    return;
                }
                s_parked = false;
                if (!player.IsTeleporting())
                {
                    Spawn(player);
                }
                return;
            }

            if (c.CurrentMode == Companion.Mode.Follow && c.Ai.GetFollowTarget() != player.gameObject)
            {
                c.Ai.SetFollowTarget(player.gameObject);
            }
            if (Time.time >= s_nextSlow)
            {
                s_nextSlow = Time.time + 3f;
                c.SlowUpdate();
            }
            // Следом — держится рядом; на работе может уйти к лесу, но не дальше поводка
            float dist = Vector3.Distance(c.transform.position, player.transform.position);
            bool working = c.CurrentMode != Companion.Mode.Follow && c.CurrentMode != Companion.Mode.Stay;
            if (!player.IsTeleporting() && c.CurrentMode != Companion.Mode.Stay && dist > (working ? Companion.Leash + 10f : 50f))
            {
                if (working)
                {
                    c.CancelPlan();
                    c.SetMode(Companion.Mode.Follow);
                }
                TeleportNear(c, player);
            }
            UpdateEvents(player, c);
        }

        private static void ResetSession(Player player)
        {
            s_player = player;
            s_respawnTimer = 0f;
            s_zdo = ZDOID.None;
            s_greeted = false;
            s_returning = false;
            s_parked = false;
            s_lastBiome = Heightmap.Biome.None;
            s_lastDayPhase = -1;
            s_lastDay = -1;
            s_nextIdle = Time.time + NextIdleDelay;
            s_seenBiomes.Clear();
            s_build.Clear();
        }

        // ---------- Появление ----------

        private static Vector3 PlaceNear(Player player)
        {
            Vector3 pos = player.transform.position - player.transform.forward * 2f + player.transform.right * 1.5f;
            if (ZoneSystem.instance.FindFloor(pos + Vector3.up * 2f, out float height) && Mathf.Abs(height - player.transform.position.y) < 3f)
            {
                pos.y = height + 0.1f;
            }
            else
            {
                pos = player.transform.position + player.transform.right * 1f;
            }
            return pos;
        }

        private static void Spawn(Player player, bool announce = true)
        {
            // Старый ZDO (компаньон остался в выгруженной зоне) убираем, чтобы не было двойника
            if (!s_zdo.IsNone())
            {
                ZDO old = ZDOMan.instance.GetZDO(s_zdo);
                if (old != null)
                {
                    ZDOMan.instance.DestroyZDO(old);
                }
            }
            GameObject go = Object.Instantiate(CompanionPrefab.Prefab, PlaceNear(player), player.transform.rotation);
            ZNetView nview = go.GetComponent<ZNetView>();
            s_zdo = nview.GetZDO() != null ? nview.GetZDO().m_uid : ZDOID.None;
            Plugin.Log.LogInfo($"Компаньон появился рядом с {player.GetPlayerName()}");

            Companion c = go.GetComponent<Companion>();
            if (!announce)
            {
                return;
            }
            if (s_returning)
            {
                s_returning = false;
                Comment(c, "Ты вернулся к побратиму после гибели в бою.", force: true);
            }
            else if (!s_greeted)
            {
                s_greeted = true;
                Comment(c, "Ты только что нагнал побратима — поприветствуй его.", force: true);
            }
        }

        // Character.TeleportTo у не-игроков пустой, переносим тело сами
        private static void TeleportNear(Companion c, Player player)
        {
            Vector3 pos = PlaceNear(player);
            c.Ai.StopMoving();
            c.transform.position = pos;
            Rigidbody body = c.Humanoid.m_body;
            body.position = pos;
            body.linearVelocity = Vector3.zero;
            Plugin.Log.LogInfo("Компаньон перенесён к игроку");
        }

        public static void OnCompanionLoaded(Companion c)
        {
            s_parked = false;
            ZDO zdo = c.GetComponent<ZNetView>().GetZDO();
            if (zdo != null)
            {
                s_zdo = zdo.m_uid;
            }
        }

        public static void OnCompanionUnloaded(Companion c)
        {
            s_parked = c.CurrentMode == Companion.Mode.Stay;
        }

        public static void OnCompanionDied(Companion c)
        {
            s_respawnTimer = Plugin.RespawnSeconds.Value;
            s_returning = true;
            Character killer = c.Humanoid.m_lastHit != null ? c.Humanoid.m_lastHit.GetAttacker() : null;
            Note(killer != null ? $"{c.Name} пал в бою, его сразил {Localization.instance.Localize(killer.m_name)}" : $"{c.Name} пал");
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                player.Message(MessageHud.MessageType.Center, $"{c.Name} пал в бою. Он вернётся.");
            }
        }

        // ---------- Разговор ----------

        public static void OnPlayerChat(string text)
        {
            text = text.Trim();
            Companion c = Companion.Local;
            Player player = Player.m_localPlayer;
            if (text.Length == 0 || player == null)
            {
                return;
            }
            if (c == null)
            {
                if (s_parked)
                {
                    CallBackFromAfar(player, text);
                }
                return;
            }
            Bridge.Send("chat", text, BuildState(player, c), reply =>
            {
                Companion now = Companion.Local;
                if (now == null)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(reply.error))
                {
                    Fallback(now, text);
                    return;
                }
                now.Say(reply.say);
                Apply(now, reply.plan);
            });
        }

        private static bool Comes(List<PlanStep> plan)
        {
            return plan.Count > 0 && (plan[0].Do == "follow" || plan[0].Do == "come");
        }

        // Компаньон сторожит далеко: слышит издалека и может прибежать
        private static void CallBackFromAfar(Player player, string text)
        {
            Bridge.GameState state = BuildState(player, null);
            state.companion_mode = "далеко, сторожит место, куда его поставили";
            Bridge.Send("chat", text, state, reply =>
            {
                bool comes = Comes(reply.plan)
                    || (!string.IsNullOrEmpty(reply.error) && Regex.IsMatch(text, @"за мной|ко мне|сюда|иди|follow|come", RegexOptions.IgnoreCase));
                if (comes && Player.m_localPlayer != null && Companion.Local == null)
                {
                    s_parked = false;
                    Spawn(Player.m_localPlayer, announce: false);
                    Companion.Local?.Say(reply.say);
                }
                else if (!string.IsNullOrEmpty(reply.say) && Chat.instance != null)
                {
                    Chat.instance.AddString(Plugin.CompanionName.Value, $"(издалека) {reply.say}", Talker.Type.Shout);
                }
            });
        }

        public static Bridge.GameState CurrentState()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return new Bridge.GameState();
            }
            Bridge.GameState state = BuildState(player, Companion.Local);
            if (Companion.Local == null && s_parked)
            {
                state.companion_mode = "далеко, сторожит место, куда его поставили";
            }
            return state;
        }

        // Ответ на голосовую фразу: мост уже озвучил реплику, здесь — текст и действие
        public static void OnVoiceReply(Bridge.VoiceReply reply)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            if (Chat.instance != null && !string.IsNullOrEmpty(reply.transcript))
            {
                Chat.instance.AddString(player.GetPlayerName(), reply.transcript, Talker.Type.Normal);
                Chat.instance.m_hideTimer = 0f;
            }
            Companion c = Companion.Local;
            if (c == null)
            {
                if (s_parked && Comes(reply.plan))
                {
                    s_parked = false;
                    Spawn(player, announce: false);
                    Companion.Local?.Say(reply.say);
                }
                else if (!string.IsNullOrEmpty(reply.say) && Chat.instance != null)
                {
                    Chat.instance.AddString(Plugin.CompanionName.Value, $"(издалека) {reply.say}", Talker.Type.Shout);
                }
                return;
            }
            c.Say(reply.say);
            Apply(c, reply.plan);
        }

        public static void OnOrderByHand(Companion c)
        {
            Comment(c, c.CurrentMode == Companion.Mode.Stay
                ? "Побратим жестом велел тебе ждать здесь."
                : "Побратим жестом позвал тебя за собой.", force: true);
        }

        // План выполнен: один отчёт модели обо всём сразу — она и расскажет побратиму
        public static void OnPlanFinished(Companion c, List<string> results)
        {
            string text = results.Count == 1
                ? $"Ты закончил дело: {results[0]}."
                : "Ты закончил всё, что поручили: " + string.Join("; ", results.Select((r, i) => $"{i + 1}) {r}")) + ".";
            Comment(c, text + " Коротко скажи побратиму об итоге.", force: true);
        }

        private static void Apply(Companion c, List<PlanStep> plan, bool fromEvent = false)
        {
            if (plan == null || plan.Count == 0)
            {
                return;
            }
            // Реакция на событие (бой, ночь) не отменяет поручение побратима: от врагов он и так отобьётся
            if (fromEvent && c.OnTask && plan.Any(s => s.Do != "combat_style"))
            {
                Plugin.Log.LogInfo($"Действие по событию пропущено, занят делом: {string.Join(", ", plan)}");
                plan = plan.Where(s => s.Do == "combat_style").ToList();
                if (plan.Count == 0)
                {
                    return;
                }
            }
            Plugin.Log.LogInfo($"Действие: {string.Join(", ", plan)}");
            c.RunPlan(plan);
        }

        // Без моста компаньон понимает простые команды
        private static readonly (Regex pattern, string action, string say)[] s_fallback =
        {
            (new Regex(@"в сундук|сложи|убери", RegexOptions.IgnoreCase), "store", "Отнесу в сундук."),
            (new Regex(@"из сундука|возьми из|принеси из", RegexOptions.IgnoreCase), "fetch", "Схожу к сундуку."),
            (new Regex(@"отда|дай мне|give", RegexOptions.IgnoreCase), "give_items", "Держи, побратим."),
            (new Regex(@"охот|олен|кабан|дичь|hunt", RegexOptions.IgnoreCase), "hunt", "Пойду поохочусь."),
            (new Regex(@"ягод|малин|черник|гриб|трав|чертопол|одуванч|собери|нарви|pick|berr", RegexOptions.IgnoreCase), "pick", "Соберу, что растёт."),
            (new Regex(@"руби|дров|дерев|лес|wood|chop", RegexOptions.IgnoreCase), "gather_wood", "Берусь за топор."),
            (new Regex(@"камн|камен|руд|кирк|mine|stone", RegexOptions.IgnoreCase), "mine", "Пойду долбить камень."),
            (new Regex(@"подбер|лут|loot", RegexOptions.IgnoreCase), "loot", "Соберу, что валяется."),
            (new Regex(@"стой|жди|охраня|stay|wait", RegexOptions.IgnoreCase), "stay", "Буду ждать здесь."),
            (new Regex(@"атак|бей|убей|attack|kill", RegexOptions.IgnoreCase), "attack", "За Одина!"),
            (new Regex(@"за мной|ко мне|сюда|иди|пошли|follow|come", RegexOptions.IgnoreCase), "follow", "Иду, побратим."),
        };

        private static void Fallback(Companion c, string text)
        {
            foreach (var (pattern, action, say) in s_fallback)
            {
                if (pattern.IsMatch(text))
                {
                    c.Say(say);
                    string what = action == "pick" || action == "hunt" || action == "fetch" ? FallbackWhat(text) : "";
                    if (action == "fetch" && what.Length == 0)
                    {
                        c.Say("Что взять-то, брат? Скажи толком.");
                        return;
                    }
                    var plan = new List<PlanStep> { new PlanStep(action, what) };
                    if (action == "fetch")
                    {
                        plan.Add(new PlanStep("give_items", what));
                    }
                    Apply(c, plan);
                    return;
                }
            }
            c.Say("Хм.");
        }

        // «принеси из сундука стрелы» → «стрелы»: последнее значимое слово
        private static string FallbackWhat(string text)
        {
            string[] skip = { "собери", "нарви", "принеси", "возьми", "из", "сундука", "мне", "бьёрн", "бьерн", "поохоться", "охоться", "на", "иди", "сходи" };
            string[] words = Catalog.Normalize(text).Split(' ');
            for (int i = words.Length - 1; i >= 0; i--)
            {
                if (words[i].Length >= 3 && !skip.Contains(words[i]))
                {
                    return words[i];
                }
            }
            return "";
        }

        // ---------- События ----------

        private static void Comment(Companion c, string text, bool force = false)
        {
            if (Voice.Busy || (!force && (!Proactive || Bridge.Busy || Time.time - s_lastComment < CommentCooldown)))
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            s_lastComment = Time.time;
            s_nextIdle = Time.time + NextIdleDelay;
            Bridge.Send("event", text, BuildState(player, c), reply =>
            {
                Companion now = Companion.Local;
                if (now != null && string.IsNullOrEmpty(reply.error))
                {
                    now.Say(reply.say);
                    Apply(now, reply.plan, fromEvent: true);
                }
            });
        }

        private static void UpdateEvents(Player player, Companion c)
        {
            // Охота на дичь — не бой, о ней он и так отчитается
            if (c.InCombat() && !(c.CurrentMode == Companion.Mode.Hunt && WorldScan.IsPrey(c.Ai.m_targetCreature, out _)))
            {
                if (Time.time - s_lastCombat > 40f)
                {
                    Comment(c, $"Бой! На вас идёт {Localization.instance.Localize(c.Ai.m_targetCreature.m_name)}.");
                }
                s_lastCombat = Time.time;
            }

            if (c.Humanoid.GetHealthPercentage() < 0.3f && Time.time - s_lastCompanionHurt > 90f)
            {
                s_lastCompanionHurt = Time.time;
                Comment(c, "Ты тяжело ранен.", force: true);
            }
            if (player.GetHealthPercentage() < 0.25f && Time.time - s_lastPlayerHurt > 90f)
            {
                s_lastPlayerHurt = Time.time;
                Comment(c, "Побратим тяжело ранен!", force: true);
            }

            Heightmap.Biome biome = player.GetCurrentBiome();
            if (biome != Heightmap.Biome.None && biome != s_lastBiome)
            {
                bool first = s_lastBiome == Heightmap.Biome.None;
                s_lastBiome = biome;
                if (!first)
                {
                    Comment(c, $"Вы вошли в новые земли: {BiomeName(biome)}.");
                }
                if (s_seenBiomes.Add(biome) && !first)
                {
                    Note($"вошли в земли: {BiomeName(biome)}");
                }
            }

            int day = EnvMan.instance.GetDay();
            if (day != s_lastDay)
            {
                if (s_lastDay >= 0)
                {
                    Note($"начался день {day}");
                }
                s_lastDay = day;
            }
            FlushBuildNotes();

            int phase = EnvMan.IsNight() ? 1 : 0;
            if (phase != s_lastDayPhase)
            {
                bool first = s_lastDayPhase < 0;
                s_lastDayPhase = phase;
                if (!first)
                {
                    Comment(c, phase == 1 ? "Наступила ночь." : "Рассвело.");
                }
            }

            if (Time.time > s_nextIdle && Proactive)
            {
                Comment(c, Plugin.Chattiness.Value == "chatty"
                    ? "Давно тихо, вы просто в пути. Скажи что-нибудь: вспомни былое, спроси побратима о планах или пошути."
                    : "Давно тихо, вы просто в пути. Можешь коротко что-нибудь сказать или промолчать.");
            }
        }

        // ---------- Заметки для памяти (без ответа модели) ----------

        private static readonly Dictionary<string, int> s_build = new Dictionary<string, int>();
        private static float s_buildSince = -1f;

        public static void Note(string text)
        {
            Player player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            Plugin.Log.LogInfo($"Заметка: {text}");
            Bridge.Note(text, new Bridge.GameState
            {
                player_name = Game.instance.GetPlayerProfile().GetName(),
                world = ZNet.World != null ? ZNet.World.m_name : "",
                day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0,
            });
        }

        // Постройки и ремесло копим и пишем одной строкой раз в полторы минуты
        public static void OnBuilt(string what)
        {
            AddBuild("построил " + what);
        }

        public static void OnCrafted(string what)
        {
            AddBuild("смастерил " + what);
        }

        private static void AddBuild(string key)
        {
            if (s_build.Count == 0)
            {
                s_buildSince = Time.time;
            }
            s_build[key] = (s_build.TryGetValue(key, out int n) ? n : 0) + 1;
        }

        private static void FlushBuildNotes()
        {
            if (s_build.Count == 0 || Time.time - s_buildSince < 90f)
            {
                return;
            }
            string text = "побратим " + string.Join(", ", s_build.Select(kv => kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key));
            s_build.Clear();
            Note(text);
        }

        public static void OnBossKilled(Character boss)
        {
            string name = Localization.instance.Localize(boss.m_name);
            Note($"повержен {name}!");
            Companion c = Companion.Local;
            Player player = Player.m_localPlayer;
            if (c != null && player != null && Vector3.Distance(boss.transform.position, player.transform.position) < 80f)
            {
                Comment(c, $"Пал {name}, один из древних врагов Одина! Вы победили.", force: true);
            }
        }

        public static void OnPlayerDied(Player player)
        {
            Character killer = player.m_lastHit != null ? player.m_lastHit.GetAttacker() : null;
            Note(killer != null ? $"побратим погиб, его убил {Localization.instance.Localize(killer.m_name)}" : "побратим погиб");
        }

        // ---------- Обстановка для LLM ----------

        private static Bridge.GameState BuildState(Player player, Companion c)
        {
            var s = new Bridge.GameState
            {
                player_name = Game.instance.GetPlayerProfile().GetName(),
                world = ZNet.World != null ? ZNet.World.m_name : "",
                player_foods = player.GetFoods().Count,
                player_items = PlayerItems(player),
                home = Home(player),
                player_hp = player.GetHealth(),
                player_hp_max = player.GetMaxHealth(),
                player_weight = player.GetInventory().GetTotalWeight(),
                player_weight_max = player.GetMaxCarryWeight(),
                biome = BiomeName(player.GetCurrentBiome()),
                day = EnvMan.instance.GetDay(),
                time_of_day = TimeOfDay(EnvMan.instance.GetDayFraction()),
                weather = EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "",
                location = player.InInterior() ? "в подземелье" : "",
            };
            ItemDrop.ItemData weapon = player.GetCurrentWeapon();
            if (weapon != null && (player.m_unarmedWeapon == null || weapon != player.m_unarmedWeapon.m_itemData))
            {
                s.player_weapon = Localization.instance.Localize(weapon.m_shared.m_name);
            }
            if (c != null)
            {
                s.companion_hp = c.Humanoid.GetHealth();
                s.companion_hp_max = c.Humanoid.GetMaxHealth();
                s.companion_mode = c.DescribeMode();
                s.companion_carry = c.DescribeCarry();
                s.companion_plan = c.DescribePlan();
                s.companion_style = c.PrefersBow ? "из лука, вблизи топором" : "топором и щитом";
                ItemDrop.ItemData bow = c.FindBow();
                s.companion_bow = bow != null ? Localization.instance.Localize(bow.m_shared.m_name) : "";
                s.companion_arrows = c.ArrowCount();
            }

            var enemies = new List<Bridge.EnemyInfo>();
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch.IsDead() || ch.IsPlayer() || (c != null && ch == c.Humanoid) || !BaseAI.IsEnemy(player, ch))
                {
                    continue;
                }
                float dist = Vector3.Distance(ch.transform.position, player.transform.position);
                if (dist < 40f && (c == null || c.IsThreat(ch)))
                {
                    enemies.Add(new Bridge.EnemyInfo { name = Localization.instance.Localize(ch.m_name), distance = dist });
                }
            }
            s.enemies = enemies.OrderBy(e => e.distance).Take(6).ToArray();
            // Что компаньон может срубить и разбить своими инструментами в пределах поводка
            int axe = c != null ? c.ToolTier(true) : 99;
            int pickaxe = c != null ? c.ToolTier(false) : 99;
            WorldScan.Survey look = WorldScan.Look(player.transform.position, Companion.Leash - 5f, axe, pickaxe);
            s.trees_near = look.Trees;
            s.rocks_near = look.Rocks;
            if (look.Trees > 0)
            {
                s.trees_where = $"ближайшее в {look.NearestTree:0} м {WorldScan.Direction(player.transform, look.NearestTreePos)}";
            }
            if (look.Rocks > 0)
            {
                s.rocks_where = $"ближайший в {look.NearestRock:0} м {WorldScan.Direction(player.transform, look.NearestRockPos)}";
            }
            Vector3 at = player.transform.position;
            foreach (ItemDrop d in ItemDrop.s_instances)
            {
                if (d != null && Vector3.Distance(d.transform.position, at) < 15f)
                {
                    s.items_near++;
                }
            }
            s.plants = look.Plants.Take(8).Select(g => Near(player, g)).ToArray();
            s.animals = WorldScan.LookAnimals(at, Companion.Leash - 5f).Take(6).Select(g => Near(player, g)).ToArray();
            s.chests = look.Chests.Take(2).Select(chest =>
            {
                Inventory inv = chest.GetInventory();
                return new Bridge.ChestInfo
                {
                    distance = Vector3.Distance(at, chest.transform.position),
                    where = WorldScan.Direction(player.transform, chest.transform.position),
                    contents = BjornChest.Contents(chest),
                    used = inv.GetAllItems().Count,
                    total = inv.GetWidth() * inv.GetHeight(),
                };
            }).ToArray();
            return s;
        }

        private static Bridge.NearInfo Near(Player player, WorldScan.Group g)
        {
            return new Bridge.NearInfo
            {
                name = g.Name,
                count = g.Count,
                nearest = g.Nearest,
                where = WorldScan.Direction(player.transform, g.NearestPos),
            };
        }

        // Что у побратима в мешке (кроме надетого) — чтобы понимал, чего не хватает
        private static string PlayerItems(Player player)
        {
            var groups = player.GetInventory().GetAllItems()
                .Where(i => !i.m_equipped)
                .GroupBy(i => Localization.instance.Localize(i.m_shared.m_name))
                .Select(g => (name: g.Key, count: g.Sum(i => i.m_stack)))
                .OrderByDescending(g => g.count)
                .Take(14);
            return string.Join(", ", groups.Select(g => $"{g.name} ×{g.count}"));
        }

        private static string Home(Player player)
        {
            PlayerProfile profile = Game.instance.GetPlayerProfile();
            if (!profile.HaveCustomSpawnPoint())
            {
                return "";
            }
            Vector3 home = profile.GetCustomSpawnPoint();
            float dist = Utils.DistanceXZ(home, player.transform.position);
            return dist < 15f ? "здесь, рядом" : $"в {dist:0} м {WorldScan.Direction(player.transform, home)}";
        }

        private static string BiomeName(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return "Луга";
                case Heightmap.Biome.BlackForest: return "Чёрный лес";
                case Heightmap.Biome.Swamp: return "Болота";
                case Heightmap.Biome.Mountain: return "Горы";
                case Heightmap.Biome.Plains: return "Равнины";
                case Heightmap.Biome.Mistlands: return "Туманные земли";
                case Heightmap.Biome.AshLands: return "Пепельные земли";
                case Heightmap.Biome.DeepNorth: return "Глубокий север";
                case Heightmap.Biome.Ocean: return "Океан";
                default: return biome.ToString();
            }
        }

        private static string TimeOfDay(float fraction)
        {
            if (fraction < 0.22f || fraction >= 0.8f) return "ночь";
            if (fraction < 0.35f) return "утро";
            if (fraction < 0.65f) return "день";
            return "вечер";
        }
    }
}
