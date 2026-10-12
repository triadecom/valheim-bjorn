using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VikingCompanion
{
    // «Сундук Бьёрна»: армированный сундук, на который сверх обычного уходит 10 глаз грейдварфов.
    // Бьёрн сам кладёт в него добычу и берёт из него вещи. Строится молотом, раздел мебели.
    internal static class BjornChest
    {
        public const string PrefabName = "piece_chest_bjorn";
        public static readonly int PrefabHash = PrefabName.GetStableHashCode();

        private const string BasePrefab = "piece_chest";
        private const string ExtraItem = "GreydwarfEye";
        private const int ExtraAmount = 10;

        private static GameObject s_holder;
        private static GameObject s_prefab;

        private static bool Russian => Localization.instance == null || Localization.instance.GetSelectedLanguage() == "Russian";
        public static string Title => Russian ? "Сундук Бьёрна" : "Björn's chest";

        public static bool Is(Container container)
        {
            ZNetView nview = container != null ? container.m_nview : null;
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            return zdo != null && zdo.GetPrefab() == PrefabHash;
        }

        public static void Register(ZNetScene scene)
        {
            if (s_prefab == null)
            {
                s_prefab = Build(scene);
            }
            if (s_prefab != null && !scene.m_namedPrefabs.ContainsKey(PrefabHash))
            {
                scene.m_prefabs.Add(s_prefab);
                scene.m_namedPrefabs.Add(PrefabHash, s_prefab);
            }
            AddToHammer();
        }

        private static GameObject Build(ZNetScene scene)
        {
            GameObject original = scene.GetPrefab(BasePrefab);
            GameObject eye = scene.GetPrefab(ExtraItem);
            if (original == null || eye == null)
            {
                Plugin.Log.LogError($"Сундук Бьёрна не собран: нет {BasePrefab} или {ExtraItem}");
                return null;
            }
            // Неактивный родитель: у копии не вызываются Awake, пока мы её перестраиваем
            s_holder = new GameObject("VikingCompanionChestPrefab");
            s_holder.SetActive(false);
            Object.DontDestroyOnLoad(s_holder);

            GameObject go = Object.Instantiate(original, s_holder.transform, false);
            go.name = PrefabName;
            Piece piece = go.GetComponent<Piece>();
            piece.m_name = Title;
            piece.m_description = Russian
                ? "Окованный сундук с глазами грейдварфов на замке. Бьёрн сам складывает сюда добычу и берёт отсюда вещи."
                : "An iron-bound chest with greydwarf eyes on the lock. Björn stores his loot here and takes things from it.";
            piece.m_resources = piece.m_resources
                .Select(r => new Piece.Requirement { m_resItem = r.m_resItem, m_amount = r.m_amount, m_amountPerLevel = r.m_amountPerLevel, m_recover = r.m_recover })
                .Append(new Piece.Requirement { m_resItem = eye.GetComponent<ItemDrop>(), m_amount = ExtraAmount, m_recover = true })
                .ToArray();
            Container container = go.GetComponent<Container>();
            container.m_name = Title;

            // Чуть зеленоватый отлив металла, чтобы отличать от обычного армированного сундука
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && mats[i].HasProperty("_Color"))
                    {
                        mats[i] = new Material(mats[i]) { color = mats[i].color * new Color(0.78f, 0.95f, 0.82f, 1f) };
                    }
                }
                r.sharedMaterials = mats;
            }
            Plugin.Log.LogInfo($"Сундук Бьёрна: {string.Join(", ", piece.m_resources.Select(r => $"{r.m_resItem.name}×{r.m_amount}"))}");
            return go;
        }

        // В меню молота — сразу за армированным сундуком
        public static void AddToHammer()
        {
            if (s_prefab == null || ObjectDB.instance == null)
            {
                return;
            }
            GameObject hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            PieceTable table = hammer != null ? hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
            if (table == null || table.m_pieces.Contains(s_prefab))
            {
                return;
            }
            int index = table.m_pieces.FindIndex(p => p != null && p.name == BasePrefab);
            table.m_pieces.Insert(index >= 0 ? index + 1 : table.m_pieces.Count, s_prefab);
        }

        public static string Contents(Container chest, int max = 10)
        {
            var groups = chest.GetInventory().GetAllItems()
                .GroupBy(i => Localization.instance.Localize(i.m_shared.m_name))
                .Select(g => (name: g.Key, count: g.Sum(i => i.m_stack)))
                .OrderByDescending(g => g.count)
                .ToList();
            string text = string.Join(", ", groups.Take(max).Select(g => $"{g.name} ×{g.count}"));
            if (groups.Count > max)
            {
                text += $" и ещё {groups.Count - max} видов";
            }
            return text;
        }
    }

    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    internal static class ObjectDB_Awake_Patch
    {
        private static void Postfix() => BjornChest.AddToHammer();
    }

    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class Container_GetHoverText_Patch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            if (BjornChest.Is(__instance))
            {
                __result += $"\n<color=#aaaaaa>{Plugin.CompanionName.Value} кладёт сюда добычу и берёт отсюда вещи</color>";
            }
        }
    }
}
