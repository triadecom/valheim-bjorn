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
        private static Player s_player;

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
            // Следом — держится рядом; на работе может уйти к лесу, но не дальше поводка
            float dist = Vector3.Distance(c.transform.position, player.transform.position);
            bool working = c.CurrentMode == Companion.Mode.Work || c.CurrentMode == Companion.Mode.Loot;
            if (!player.IsTeleporting() && c.CurrentMode != Companion.Mode.Stay && dist > (working ? Companion.Leash + 10f : 50f))
            {
                if (working)
                {
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
            s_nextIdle = Time.time + Random.Range(420f, 720f);
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
                Apply(now, reply.action);
            });
        }

        // Компаньон сторожит далеко: слышит издалека и может прибежать
        private static void CallBackFromAfar(Player player, string text)
        {
            Bridge.GameState state = BuildState(player, null);
            state.companion_mode = "далеко, сторожит место, куда его поставили";
            Bridge.Send("chat", text, state, reply =>
            {
                bool comes = reply.action == "follow" || reply.action == "come"
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
                if (s_parked && (reply.action == "follow" || reply.action == "come"))
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
            Apply(c, reply.action);
        }

        public static void OnOrderByHand(Companion c)
        {
            Comment(c, c.CurrentMode == Companion.Mode.Stay
                ? "Побратим жестом велел тебе ждать здесь."
                : "Побратим жестом позвал тебя за собой.", force: true);
        }

        public static void OnTaskFinished(Companion c, string reason, string details)
        {
            string text;
            switch (reason)
            {
                case "work_wood_done": text = $"Ты закончил рубить лес, собрал {details} шт. Скажи об этом и иди к побратиму."; break;
                case "work_wood_none": text = $"Деревьев рядом больше нет (собрано {details} шт.). Ты возвращаешься к побратиму."; break;
                case "work_stone_done": text = $"Ты закончил добывать камень, собрал {details} шт."; break;
                case "work_stone_none": text = $"Подходящих камней рядом нет (собрано {details} шт.). Ты возвращаешься."; break;
                case "loot_done": text = $"Ты подобрал с земли вещей: {details}."; break;
                case "gave": text = $"Ты отдал побратиму добычу: {details}."; break;
                case "give_empty": text = "Побратим просил добычу, но у тебя ничего нет."; break;
                default: text = reason; break;
            }
            Comment(c, text, force: true);
        }

        private static void Apply(Companion c, string action)
        {
            switch (action)
            {
                case "follow":
                case "come":
                    c.SetMode(Companion.Mode.Follow);
                    break;
                case "stay":
                    c.SetMode(Companion.Mode.Stay);
                    break;
                case "attack":
                    c.SetMode(Companion.Mode.Follow);
                    c.Hunt(60f);
                    break;
                case "gather_wood":
                    c.SetMode(Companion.Mode.Work, Companion.WorkKind.Wood);
                    break;
                case "mine":
                    c.SetMode(Companion.Mode.Work, Companion.WorkKind.Stone);
                    break;
                case "loot":
                    c.SetMode(Companion.Mode.Loot);
                    break;
                case "give_items":
                    c.SetMode(Companion.Mode.Give);
                    break;
            }
            if (!string.IsNullOrEmpty(action) && action != "none")
            {
                Plugin.Log.LogInfo($"Действие: {action}");
            }
        }

        // Без моста компаньон понимает простые команды
        private static readonly (Regex pattern, string action, string say)[] s_fallback =
        {
            (new Regex(@"отда|дай мне|give", RegexOptions.IgnoreCase), "give_items", "Держи, побратим."),
            (new Regex(@"руби|дров|дерев|лес|wood|chop", RegexOptions.IgnoreCase), "gather_wood", "Берусь за топор."),
            (new Regex(@"камн|камен|руд|кирк|mine|stone", RegexOptions.IgnoreCase), "mine", "Пойду долбить камень."),
            (new Regex(@"подбер|собер|лут|loot|pick", RegexOptions.IgnoreCase), "loot", "Соберу, что валяется."),
            (new Regex(@"стой|жди|охраня|stay|wait", RegexOptions.IgnoreCase), "stay", "Буду ждать здесь."),
            (new Regex(@"атак|бей|убей|охот|attack|kill|hunt", RegexOptions.IgnoreCase), "attack", "За Одина!"),
            (new Regex(@"за мной|ко мне|сюда|иди|пошли|follow|come", RegexOptions.IgnoreCase), "follow", "Иду, побратим."),
        };

        private static void Fallback(Companion c, string text)
        {
            foreach (var (pattern, action, say) in s_fallback)
            {
                if (pattern.IsMatch(text))
                {
                    c.Say(say);
                    Apply(c, action);
                    return;
                }
            }
            c.Say("Хм.");
        }

        // ---------- События ----------

        private static void Comment(Companion c, string text, bool force = false)
        {
            if (Voice.Busy || (!force && (!Plugin.ProactiveComments.Value || Bridge.Busy || Time.time - s_lastComment < Plugin.CommentCooldown.Value)))
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            s_lastComment = Time.time;
            s_nextIdle = Time.time + Random.Range(420f, 720f);
            Bridge.Send("event", text, BuildState(player, c), reply =>
            {
                Companion now = Companion.Local;
                if (now != null && string.IsNullOrEmpty(reply.error))
                {
                    now.Say(reply.say);
                    Apply(now, reply.action);
                }
            });
        }

        private static void UpdateEvents(Player player, Companion c)
        {
            if (c.InCombat())
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
            }

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

            if (Time.time > s_nextIdle)
            {
                Comment(c, "Давно тихо, вы просто в пути. Можешь коротко что-нибудь сказать или промолчать.");
            }
        }

        // ---------- Обстановка для LLM ----------

        private static Bridge.GameState BuildState(Player player, Companion c)
        {
            var s = new Bridge.GameState
            {
                player_name = Game.instance.GetPlayerProfile().GetName(),
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
            return s;
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
