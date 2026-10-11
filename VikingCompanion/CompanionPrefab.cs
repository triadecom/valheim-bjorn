using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VikingCompanion
{
    // Префаб компаньона: копия префаба игрока, где Player заменён на Humanoid + MonsterAI.
    // Модель, анимации и снаряжение игрока работают как есть, а управляет телом ИИ мобов.
    internal static class CompanionPrefab
    {
        public const string PrefabName = "VikingCompanion";

        private static GameObject s_holder;

        public static GameObject Prefab { get; private set; }

        public static readonly HashSet<string> KitNames = new HashSet<string>();
        // Внутренние имена предметов снаряжения ($item_axe_bronze): проверка без выделения памяти
        private static readonly HashSet<string> s_kitItemNames = new HashSet<string>();

        public static bool IsKitItem(ItemDrop.ItemData item) => s_kitItemNames.Contains(item.m_shared.m_name);

        public static void Register(ZNetScene scene)
        {
            if (Prefab == null)
            {
                try
                {
                    Prefab = Build(scene);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Не удалось собрать префаб компаньона: {e}");
                    return;
                }
            }
            if (Prefab == null)
            {
                return;
            }
            int hash = PrefabName.GetStableHashCode();
            if (!scene.m_namedPrefabs.ContainsKey(hash))
            {
                scene.m_prefabs.Add(Prefab);
                scene.m_namedPrefabs.Add(hash, Prefab);
            }
        }

        private static GameObject Build(ZNetScene scene)
        {
            GameObject playerPrefab = scene.GetPrefab("Player");
            if (playerPrefab == null)
            {
                Plugin.Log.LogError("Префаб Player не найден");
                return null;
            }

            // Неактивный родитель: у копии не вызываются Awake, пока мы её перестраиваем
            s_holder = new GameObject("VikingCompanionPrefabs");
            s_holder.SetActive(false);
            Object.DontDestroyOnLoad(s_holder);

            GameObject go = Object.Instantiate(playerPrefab, s_holder.transform, false);
            go.name = PrefabName;
            Plugin.Log.LogInfo("Компоненты Player: " + string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name)));

            Player player = go.GetComponent<Player>();
            foreach (Type type in new[] { typeof(PlayerController), typeof(Talker), typeof(Skills) })
            {
                foreach (Component c in go.GetComponents(type))
                {
                    Object.DestroyImmediate(c);
                }
            }

            Humanoid humanoid = go.AddComponent<Humanoid>();
            CopySerializedFields(player, humanoid, typeof(Character));
            CopySerializedFields(player, humanoid, typeof(Humanoid));
            Object.DestroyImmediate(player);

            humanoid.m_name = Plugin.CompanionName.Value;
            humanoid.m_group = PrefabName;
            humanoid.m_faction = Character.Faction.Players;
            humanoid.m_health = Plugin.MaxHealth.Value;
            humanoid.m_regenAllHPTime = 180f;
            humanoid.m_defaultItems = ResolveKit(scene);
            humanoid.m_randomWeapon = Array.Empty<GameObject>();
            humanoid.m_randomArmor = Array.Empty<GameObject>();
            humanoid.m_randomShield = Array.Empty<GameObject>();
            humanoid.m_randomSets = Array.Empty<Humanoid.ItemSet>();
            humanoid.m_randomItems = Array.Empty<Humanoid.RandomItem>();

            MonsterAI ai = go.AddComponent<MonsterAI>();
            ai.m_viewRange = 30f;
            ai.m_viewAngle = 120f;
            ai.m_hearRange = 20f;
            ai.m_alertRange = 25f;
            ai.m_enableHuntPlayer = false;
            ai.m_attackPlayerObjects = false;
            ai.m_fleeIfHurtWhenTargetCantBeReached = false;
            ai.m_fleeIfNotAlerted = false;
            ai.m_fleeIfLowHealth = 0f;
            ai.m_afraidOfFire = false;
            ai.m_avoidFire = false;
            ai.m_avoidWater = false;
            ai.m_jumpInterval = 0f;
            ai.m_randomMoveRange = 4f;
            ai.m_randomMoveInterval = 6f;
            ai.m_moveMinAngle = 10f;
            ai.m_smoothMovement = true;
            ai.m_pathAgentType = Pathfinding.AgentType.Humanoid;
            ai.m_idleSoundInterval = 9999f;
            ai.m_minAttackInterval = 0.5f;
            ai.m_maxChaseDistance = 0f;

            ZNetView nview = go.GetComponent<ZNetView>();
            nview.m_persistent = false;

            go.AddComponent<Companion>();
            return go;
        }

        private static GameObject[] ResolveKit(ZNetScene scene)
        {
            var items = new List<GameObject>();
            KitNames.Clear();
            s_kitItemNames.Clear();
            foreach (string raw in Plugin.Kit.Value.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                GameObject prefab = scene.GetPrefab(name);
                if (prefab == null || prefab.GetComponent<ItemDrop>() == null)
                {
                    Plugin.Log.LogWarning($"Предмет снаряжения не найден: {name}");
                    continue;
                }
                items.Add(prefab);
                KitNames.Add(name);
                s_kitItemNames.Add(prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name);
            }
            return items.ToArray();
        }

        // Копируем то, что Unity сериализует: public и [SerializeField], кроме readonly
        private static void CopySerializedFields(object from, object to, Type declaring)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in declaring.GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral)
                {
                    continue;
                }
                bool serialized = field.IsPublic
                    ? !field.IsDefined(typeof(NonSerializedAttribute), false)
                    : field.IsDefined(typeof(SerializeField), false);
                if (serialized)
                {
                    field.SetValue(to, field.GetValue(from));
                }
            }
        }
    }
}
