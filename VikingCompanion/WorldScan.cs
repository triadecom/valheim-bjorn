using System.Collections.Generic;
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
        }

        public struct Survey
        {
            public int Trees;
            public int Rocks;
            public float NearestTree;
            public float NearestRock;
            public Vector3 NearestTreePos;
            public Vector3 NearestRockPos;
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
                return new PrefabInfo { Kind = Kind.Log, Tier = log.m_minToolTier };
            }
            // Пни (Destructible) не берём: горизонтальный удар ИИ проходит над ними
            if (prefab.TryGetComponent(out TreeBase tree))
            {
                return new PrefabInfo { Kind = Kind.Tree, Tier = tree.m_minToolTier };
            }
            if (prefab.TryGetComponent(out MineRock5 rock5))
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = rock5.m_minToolTier };
            }
            if (prefab.TryGetComponent(out MineRock rock))
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = rock.m_minToolTier };
            }
            if (prefab.TryGetComponent(out Destructible stone) && stone.m_destructibleType == DestructibleType.Default
                && stone.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                && prefab.name.IndexOf("rock", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new PrefabInfo { Kind = Kind.Rock, Tier = stone.m_minToolTier };
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
            Vector3 keepNear, float leash, HashSet<ZDOID> skip)
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
                if (!s_prefabs.TryGetValue(zdo.GetPrefab(), out PrefabInfo info) || !Matches(info.Kind, wood) || info.Tier > tier)
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
            var s = new Survey { NearestTree = float.MaxValue, NearestRock = float.MaxValue };
            if (!EnsureBuilt())
            {
                return s;
            }
            foreach (ZDO zdo in ZNetScene.instance.m_instances.Keys)
            {
                if (!s_prefabs.TryGetValue(zdo.GetPrefab(), out PrefabInfo info))
                {
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
            return s;
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
