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
                    || Vector3.Distance(keepNear, pos) > leash || skip.Contains(zdo.m_uid))
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
                Plants = new List<Group>(), Chests = new List<Container>(),
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
                    || Vector3.Distance(keepNear, pos) > leash || skip.Contains(zdo.m_uid) || zdo.GetBool(ZDOVars.s_picked))
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
                if (dist >= bestDist || Vector3.Distance(keepNear, pos) > leash || skip.Contains(c.GetZDOID()))
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
        public static Container FindChest(Vector3 from, Vector3 keepNear, float leash)
        {
            int hash = BjornChest.PrefabHash;
            Container best = null;
            float bestDist = float.MaxValue;
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
                if (dist < bestDist && Vector3.Distance(keepNear, pos) <= leash && kv.Value.TryGetComponent(out Container chest))
                {
                    bestDist = dist;
                    best = chest;
                }
            }
            return best;
        }

        // Куда смотреть относительно взгляда игрока: «впереди», «справа сзади»…
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
