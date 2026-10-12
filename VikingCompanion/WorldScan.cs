using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Деревья и камни ищем по всем загруженным объектам мира (ZNetScene), а не физикой:
    // так компаньон видит лес и за 70 м, а проход по объектам стоит доли миллисекунды.
    internal static class WorldScan
    {
        public enum Kind : byte { None, Tree, Log, Rock }

        private struct PrefabInfo
        {
            public Kind Kind;
            public int Tier;
            public string Name;
        }

        // Скопление одного вида растений или зверей: сколько и где ближайший
        public class Group
        {
            public string Name;
            public int Count;
            public float Nearest = float.MaxValue;
            public Vector3 NearestPos;

            public void Add(float dist, Vector3 pos)
            {
                Count++;
                if (dist < Nearest)
                {
                    Nearest = dist;
                    NearestPos = pos;
                }
            }
        }

        public struct Survey
        {
            public int Trees;
            public int Rocks;
            public float NearestTree;
            public float NearestRock;
            public Vector3 NearestTreePos;
            public Vector3 NearestRockPos;
            public List<Group> Plants;
            public List<Container> Chests;
            public Dictionary<string, int> Ores;
        }

        private static Dictionary<int, PrefabInfo> s_prefabs;
        private static ZNetScene s_builtFor;

        private static bool EnsureBuilt()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
            {
                return false;
            }
            if (s_prefabs != null && s_builtFor == scene)
            {
                return true;
            }
            s_builtFor = scene;
            s_prefabs = new Dictionary<int, PrefabInfo>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                PrefabInfo info = Classify(prefab);
                if (info.Kind != Kind.None)
                {
                    s_prefabs[prefab.name.GetStableHashCode()] = info;
                }
            }
            return true;
        }

        private static PrefabInfo Classify(GameObject prefab)
        {
            if (prefab.TryGetComponent(out TreeLog log))
            {
                return new PrefabInfo { Kind = Kind.Log, Tier = log.m_minToolTier, Name = prefab.name };
            }
            // Пни (Destructible) не берём: горизонтальный удар ИИ проходит над ними
            if (prefab.TryGetComponent(out TreeBase tree))
            {
                return new PrefabInfo { Kind = Kind.Tree, Tier = tree.m_minToolTier, Name = prefab.name };
            }
            if (prefab.TryGetComponent(out MineRock5 rock5))
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = rock5.m_minToolTier, Name = prefab.name };
            }
            if (prefab.TryGetComponent(out MineRock rock))
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = rock.m_minToolTier, Name = prefab.name };
            }
            if (prefab.TryGetComponent(out Destructible stone) && stone.m_destructibleType == DestructibleType.Default
                && stone.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                && prefab.name.IndexOf("rock", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = stone.m_minToolTier, Name = prefab.name };
            }
            return default;
        }

        private static bool Matches(Kind kind, bool wood)
        {
            return wood ? kind == Kind.Tree || kind == Kind.Log : kind == Kind.Rock;
        }

        // Ближайшая цель для работы. Поваленные стволы — в первую очередь, чтобы дорубить.
        // keepNear — точка, от которой нельзя уходить дальше leash (игрок: иначе зона выгрузится).
        public static ZNetView FindNearest(Vector3 from, float radius, bool wood, int tier,
            Vector3 keepNear, float leash, HashSet<ZDOID> skip, System.Func<string, bool> nameFilter = null)
        {
            if (!EnsureBuilt())
            {
                return null;
            }
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float bestScore = float.MaxValue;
            ZNetView best = null;
            foreach (KeyValuePair<ZDO, ZNetView> kv in ZNetScene.instance.m_instances)
            {
                ZDO zdo = kv.Key;
                if (!s_prefabs.TryGetValue(zdo.GetPrefab(), out PrefabInfo info) || !Matches(info.Kind, wood) || info.Tier > tier
                    || (nameFilter != null && !nameFilter(info.Name)))
                {
                    continue;
                }
                Vector3 pos = zdo.GetPosition();
                float dist = Vector3.Distance(from, pos);
                if (dist > radius || pos.y < water - 1f || Mathf.Abs(pos.y - from.y) > 20f
                    || Vector3.Distance(keepNear, pos) > leash || skip.Contains(zdo.m_uid) || Places.DangerAt(pos) != null)
                {
                    continue;
                }
                float score = info.Kind == Kind.Log ? dist - 6f : dist;
                if (score < bestScore && kv.Value != null && kv.Value.gameObject.activeInHierarchy)
                {
                    bestScore = score;
                    best = kv.Value;
                }
            }
            return best;
        }

        // Сводка для LLM: сколько леса и камня вокруг и где ближайшие
        public static Survey Look(Vector3 center, float radius, int axeTier, int pickaxeTier)
        {
            var s = new Survey
            {
                NearestTree = float.MaxValue, NearestRock = float.MaxValue,
                Plants = new List<Group>(), Chests = new List<Container>(), Ores = new Dictionary<string, int>(),
            };
            if (!EnsureBuilt())
            {
                return s;
            }
            int chestHash = BjornChest.PrefabHash;
            var plants = new Dictionary<string, Group>();
            foreach (KeyValuePair<ZDO, ZNetView> kv in ZNetScene.instance.m_instances)
            {
                ZDO zdo = kv.Key;
                int prefab = zdo.GetPrefab();
                if (!s_prefabs.TryGetValue(prefab, out PrefabInfo info))
                {
                    if (prefab == chestHash)
                    {
                        if (kv.Value != null && Vector3.Distance(center, zdo.GetPosition()) <= radius && kv.Value.TryGetComponent(out Container chest))
                        {
                            s.Chests.Add(chest);
                        }
                    }
                    else if (Catalog.TryPick(prefab, out Catalog.PickInfo pick) && Catalog.Visible(pick))
                    {
                        Vector3 at = zdo.GetPosition();
                        float d = Vector3.Distance(center, at);
                        // Ягоды на кустах: сорванный куст стоит пустым до отрастания
                        if (d <= radius && !zdo.GetBool(ZDOVars.s_picked))
                        {
                            if (!plants.TryGetValue(pick.Display, out Group group))
                            {
                                plants[pick.Display] = group = new Group { Name = pick.Display };
                            }
                            group.Add(d, at);
                        }
                    }
                    continue;
                }
                Vector3 pos = zdo.GetPosition();
                float dist = Vector3.Distance(center, pos);
                if (dist > radius)
                {
                    continue;
                }
                if (info.Kind == Kind.Rock)
                {
                    string ore = OreName(info.Name);
                    if (ore != null)
                    {
                        s.Ores[ore] = (s.Ores.TryGetValue(ore, out int n) ? n : 0) + 1;
                    }
                    if (info.Tier > pickaxeTier)
                    {
                        continue;
                    }
                    s.Rocks++;
                    if (dist < s.NearestRock)
                    {
                        s.NearestRock = dist;
                        s.NearestRockPos = pos;
                    }
                }
                else
                {
                    if (info.Tier > axeTier)
                    {
                        continue;
                    }
                    s.Trees++;
                    if (dist < s.NearestTree)
                    {
                        s.NearestTree = dist;
                        s.NearestTreePos = pos;
                    }
                }
            }
            s.Plants = plants.Values.OrderBy(g => g.Nearest).ToList();
            s.Chests.Sort((a, b) => Vector3.Distance(center, a.transform.position).CompareTo(Vector3.Distance(center, b.transform.position)));
            return s;
        }

        // Ближайшее растение по фильтру, которое можно сорвать
        public static Pickable FindNearestPickable(Vector3 from, float radius, System.Func<Catalog.PickInfo, bool> filter,
            Vector3 keepNear, float leash, HashSet<ZDOID> skip)
        {
            if (!EnsureBuilt())
            {
                return null;
            }
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float best = float.MaxValue;
            Pickable result = null;
            foreach (KeyValuePair<ZDO, ZNetView> kv in ZNetScene.instance.m_instances)
            {
                ZDO zdo = kv.Key;
                if (!Catalog.TryPick(zdo.GetPrefab(), out Catalog.PickInfo info) || !filter(info))
                {
                    continue;
                }
                Vector3 pos = zdo.GetPosition();
                float dist = Vector3.Distance(from, pos);
                if (dist > radius || dist >= best || pos.y < water - 0.5f || Mathf.Abs(pos.y - from.y) > 15f
                    || Vector3.Distance(keepNear, pos) > leash || skip.Contains(zdo.m_uid) || zdo.GetBool(ZDOVars.s_picked)
                    || Places.DangerAt(pos) != null)
                {
                    continue;
                }
                if (kv.Value != null && kv.Value.TryGetComponent(out Pickable pickable) && pickable.CanBePicked())
                {
                    best = dist;
                    result = pickable;
                }
            }
            return result;
        }

        // Дичь: живые, не ручные звери из списка охоты
        public static Character FindNearestPrey(Vector3 from, float radius, System.Func<Catalog.PreyInfo, bool> filter,
            Vector3 keepNear, float leash, HashSet<ZDOID> skip)
        {
            Character best = null;
            float bestDist = radius;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (!IsPrey(c, out Catalog.PreyInfo info) || !filter(info))
                {
                    continue;
                }
                Vector3 pos = c.transform.position;
                float dist = Vector3.Distance(from, pos);
                if (dist >= bestDist || Vector3.Distance(keepNear, pos) > leash || skip.Contains(c.GetZDOID()) || Places.DangerAt(pos) != null)
                {
                    continue;
                }
                bestDist = dist;
                best = c;
            }
            return best;
        }

        public static bool IsPrey(Character c, out Catalog.PreyInfo info)
        {
            info = null;
            if (c == null || c.IsDead() || c.IsTamed() || c.IsPlayer())
            {
                return false;
            }
            ZNetView nview = c.m_nview;
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            return zdo != null && Catalog.TryPrey(zdo.GetPrefab(), out info);
        }

        public static List<Group> LookAnimals(Vector3 center, float radius)
        {
            var groups = new Dictionary<string, Group>();
            foreach (Character c in Character.GetAllCharacters())
            {
                if (!IsPrey(c, out Catalog.PreyInfo info))
                {
                    continue;
                }
                float dist = Vector3.Distance(center, c.transform.position);
                if (dist > radius)
                {
                    continue;
                }
                if (!groups.TryGetValue(info.Display, out Group group))
                {
                    groups[info.Display] = group = new Group { Name = info.Display };
                }
                group.Add(dist, c.transform.position);
            }
            return groups.Values.OrderBy(g => g.Nearest).ToList();
        }

        // Сундуки Бьёрна в загруженной округе, ближайший первым
        // Ближайший Сундук Бьёрна; если сундуков несколько — ближайший из тех, что подходят (prefer), иначе просто ближайший
        public static Container FindChest(Vector3 from, Vector3 keepNear, float leash, System.Func<Container, bool> prefer = null,
            ICollection<Container> skip = null)
        {
            int hash = BjornChest.PrefabHash;
            Container best = null, bestPreferred = null;
            float bestDist = float.MaxValue, bestPreferredDist = float.MaxValue;
            if (ZNetScene.instance == null)
            {
                return null;
            }
            foreach (KeyValuePair<ZDO, ZNetView> kv in ZNetScene.instance.m_instances)
            {
                if (kv.Key.GetPrefab() != hash || kv.Value == null)
                {
                    continue;
                }
                Vector3 pos = kv.Key.GetPosition();
                float dist = Vector3.Distance(from, pos);
                if (Vector3.Distance(keepNear, pos) > leash || !kv.Value.TryGetComponent(out Container chest) || (skip != null && skip.Contains(chest)))
                {
                    continue;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = chest;
                }
                if (prefer != null && dist < bestPreferredDist && prefer(chest))
                {
                    bestPreferredDist = dist;
                    bestPreferred = chest;
                }
            }
            return bestPreferred ?? best;
        }

        // Куда смотреть относительно взгляда игрока: «впереди», «справа сзади»…
        private static string OreName(string prefab)
        {
            string p = prefab.ToLowerInvariant();
            if (p.Contains("copper")) return "медь";
            if (p.Contains("tin")) return "олово";
            if (p.Contains("silver")) return "серебро";
            if (p.Contains("obsidian")) return "обсидиан";
            if (p.Contains("iron") || p.Contains("mudpile")) return "железный лом";
            return null;
        }

        // Сторона света: в Валхейме север — +Z, восток — +X
        public static string Compass(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            if (new Vector2(d.x, d.z).magnitude < 1f)
            {
                return "здесь";
            }
            float angle = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (angle < 0f)
            {
                angle += 360f;
            }
            string[] names = { "на север", "на северо-восток", "на восток", "на юго-восток", "на юг", "на юго-запад", "на запад", "на северо-запад" };
            return names[Mathf.RoundToInt(angle / 45f) % 8];
        }

        public static string BiomeName(Heightmap.Biome biome)
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
                case Heightmap.Biome.None: return "";
                default: return biome.ToString();
            }
        }

        // Что вокруг точки — одной строкой для памяти о месте: постройки, ресурсы, твари
        public static string Surroundings(Vector3 at, Character self)
        {
            var parts = new List<string>();
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(at, 25f, pieces);
            var named = new Dictionary<string, int>();
            int built = 0;
            foreach (Piece piece in pieces)
            {
                if (piece == null || !piece.IsPlacedByPlayer())
                {
                    continue;
                }
                built++;
                string label = null;
                if (piece.GetComponent<Bed>() != null) label = "кровать";
                else if (piece.TryGetComponent(out TeleportWorld portal)) label = portal.GetText().Length > 0 ? $"портал «{portal.GetText()}»" : "портал";
                else if (piece.GetComponent<CraftingStation>() != null || piece.GetComponent<Smelter>() != null
                    || piece.GetComponent<Fermenter>() != null || piece.GetComponent<Beehive>() != null || piece.GetComponent<CookingStation>() != null)
                    label = Localization.instance.Localize(piece.m_name).ToLowerInvariant();
                else if (piece.GetComponent<Container>() != null) label = "сундук";
                else if (piece.GetComponent<Fireplace>() != null) label = "очаг";
                if (label != null)
                {
                    named[label] = (named.TryGetValue(label, out int n) ? n : 0) + 1;
                }
            }
            if (built > 0)
            {
                string list = string.Join(", ", named.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key));
                parts.Add($"построек {built}" + (list.Length > 0 ? $": {list}" : ""));
            }
            Survey look = Look(at, 50f, 99, 99);
            var nature = new List<string>();
            foreach (var ore in look.Ores.OrderByDescending(kv => kv.Value))
            {
                nature.Add($"{ore.Key} ×{ore.Value}");
            }
            if (look.Trees > 0)
            {
                nature.Add($"деревьев {look.Trees}");
            }
            nature.AddRange(look.Plants.Take(3).Select(g => $"{g.Name.ToLowerInvariant()} ×{g.Count}"));
            if (nature.Count > 0)
            {
                parts.Add("рядом " + string.Join(", ", nature));
            }
            var beasts = Character.GetAllCharacters()
                .Where(c => c != null && !c.IsDead() && !c.IsPlayer() && c != self && !c.IsTamed()
                    && c.GetFaction() != Character.Faction.Players && Vector3.Distance(c.transform.position, at) < 60f)
                .GroupBy(c => Localization.instance.Localize(c.m_name))
                .Select(g => g.Count() > 1 ? $"{g.Key.ToLowerInvariant()} ×{g.Count()}" : g.Key.ToLowerInvariant())
                .Take(5).ToList();
            if (beasts.Count > 0)
            {
                parts.Add("твари: " + string.Join(", ", beasts));
            }
            string text = string.Join("; ", parts);
            return text.Length > 240 ? text.Substring(0, 240) : text;
        }

        public static string Direction(Transform viewer, Vector3 to)
        {
            Vector3 d = to - viewer.position;
            d.y = 0f;
            Vector3 forward = viewer.forward;
            forward.y = 0f;
            float angle = Vector3.SignedAngle(forward, d, Vector3.up);
            if (angle < 0f)
            {
                angle += 360f;
            }
            string[] names = { "впереди", "впереди справа", "справа", "сзади справа", "сзади", "сзади слева", "слева", "впереди слева" };
            return names[Mathf.RoundToInt(angle / 45f) % 8];
        }
    }
}
