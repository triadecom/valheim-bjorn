using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Что можно собрать и на кого охотиться, и как понять слова «малина», «грибы», «оленя», «стрелы».
    // Модель пишет what по-русски (как в сводке) — сопоставляем по основам слов и по категориям.
    internal static class Catalog
    {
        public enum PickKind { Berry, Mushroom, Herb, Ground, Crop, Other }

        public class PickInfo
        {
            public string Prefab;
            public string ItemPrefab;
            public string ItemToken;     // $item_raspberry — так предмет зовётся в инвентаре
            public string Display;       // «Малина»
            public PickKind Kind;
            public bool Wild;            // дикий лён и ячмень — не чужие посевы
            public bool Never;           // злит двергров и т. п. — не трогаем
        }

        public class PreyInfo
        {
            public string Prefab;
            public string Display;
        }

        private static readonly HashSet<string> s_berries = new HashSet<string> { "Raspberry", "Blueberries", "Cloudberry", "Lingonberry", "Vineberry" };
        private static readonly HashSet<string> s_herbs = new HashSet<string> { "Thistle", "Dandelion", "Fiddleheadfern" };
        private static readonly HashSet<string> s_ground = new HashSet<string> { "Flint", "Stone", "Wood", "Frostwood", "StoneRock" };
        private static readonly HashSet<string> s_crops = new HashSet<string>
        {
            "Carrot", "Turnip", "Onion", "Barley", "Flax", "Kale", "Poteitr",
            "CarrotSeeds", "TurnipSeeds", "OnionSeeds", "KaleSeeds", "OatSeeds",
        };
        private static readonly HashSet<string> s_neverPick = new HashSet<string> { "LuredWisp", "LuredFaderEmber" };
        public static readonly HashSet<string> PreyPrefabs = new HashSet<string>
        {
            "Deer", "Hare", "Boar", "Boar_piggy", "Chicken", "Hen", "Neck", "Lox", "Lox_Calf",
        };

        // Слова-категории → что под ними понимать
        private static readonly string[] s_allWords = { "все", "всё", "всякое", "что-нибудь", "любые", "любое", "any", "all" };

        private static Dictionary<int, PickInfo> s_picks;
        private static Dictionary<int, PreyInfo> s_prey;
        private static ZNetScene s_builtFor;

        private static bool EnsureBuilt()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || Localization.instance == null)
            {
                return false;
            }
            if (s_picks != null && s_builtFor == scene)
            {
                return true;
            }
            s_builtFor = scene;
            s_picks = new Dictionary<int, PickInfo>();
            s_prey = new Dictionary<int, PreyInfo>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                if (prefab.TryGetComponent(out Pickable pick) && pick.m_itemPrefab != null && prefab.GetComponent<Piece>() == null)
                {
                    ItemDrop drop = pick.m_itemPrefab.GetComponent<ItemDrop>();
                    if (drop == null)
                    {
                        continue;
                    }
                    string item = pick.m_itemPrefab.name;
                    var info = new PickInfo
                    {
                        Prefab = prefab.name,
                        ItemPrefab = item,
                        ItemToken = drop.m_itemData.m_shared.m_name,
                        Display = Localization.instance.Localize(pick.GetHoverName()),
                        Wild = prefab.name.EndsWith("_Wild"),
                        Never = pick.m_aggravateRange > 0f || s_neverPick.Contains(prefab.name),
                    };
                    info.Kind = s_berries.Contains(item) ? PickKind.Berry
                        : item.StartsWith("Mushroom") ? PickKind.Mushroom
                        : s_herbs.Contains(item) ? PickKind.Herb
                        : s_ground.Contains(item) && pick.m_respawnTimeMinutes >= 0f && !prefab.name.Contains("Offspring") ? PickKind.Ground
                        : s_crops.Contains(item) ? PickKind.Crop
                        : PickKind.Other;
                    s_picks[prefab.name.GetStableHashCode()] = info;
                }
                else if (PreyPrefabs.Contains(prefab.name) && prefab.TryGetComponent(out Character ch))
                {
                    s_prey[prefab.name.GetStableHashCode()] = new PreyInfo { Prefab = prefab.name, Display = Localization.instance.Localize(ch.m_name) };
                }
            }
            return true;
        }

        public static bool TryPick(int prefabHash, out PickInfo info)
        {
            info = null;
            return EnsureBuilt() && s_picks.TryGetValue(prefabHash, out info);
        }

        public static bool TryPrey(int prefabHash, out PreyInfo info)
        {
            info = null;
            return EnsureBuilt() && s_prey.TryGetValue(prefabHash, out info);
        }

        // Видно в сводке для модели: еда, травы и то, что валяется под ногами (не чужие грядки)
        public static bool Visible(PickInfo info)
        {
            return !info.Never && (info.Kind == PickKind.Berry || info.Kind == PickKind.Mushroom || info.Kind == PickKind.Herb
                || info.Kind == PickKind.Ground || (info.Kind == PickKind.Crop && info.Wild));
        }

        // ---------- Понимание слов ----------

        public static string Normalize(string text)
        {
            text = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
            var chars = text.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : ' ').ToArray();
            return string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static readonly HashSet<string> s_stopWords = new HashSet<string>
        {
            "мне", "нам", "все", "всё", "всю", "весь", "немного", "штук", "штуки", "пару", "из", "на", "для", "это", "эти", "той", "тот",
        };

        // Значимые слова запроса: «спелую малину» → «спел», «мали»
        private static List<string> Stems(string what)
        {
            var stems = new List<string>();
            foreach (string word in Normalize(what).Split(' '))
            {
                if (word.Length < 3 || s_stopWords.Contains(word))
                {
                    continue;
                }
                stems.Add(word.Substring(0, Math.Max(3, word.Length - 2)));
            }
            return stems;
        }

        // Все значимые слова запроса находятся среди слов названия
        public static bool NameMatches(string what, string name)
        {
            string w = Normalize(what);
            string n = Normalize(name);
            if (w.Length == 0 || n.Length == 0)
            {
                return false;
            }
            if (n == w || n.Contains(w))
            {
                return true;
            }
            List<string> stems = Stems(what);
            if (stems.Count == 0)
            {
                return false;
            }
            string[] words = n.Split(' ');
            return stems.All(stem => words.Any(word => word.StartsWith(stem)));
        }

        private static bool Has(string what, params string[] stems)
        {
            string w = Normalize(what);
            return stems.Any(s => w.Contains(s));
        }

        public static bool IsAll(string what)
        {
            string w = Normalize(what);
            return w.Length == 0 || s_allWords.Contains(w);
        }

        public static Func<PickInfo, bool> PickFilter(string what)
        {
            if (IsAll(what) || Has(what, "трав", "растен", "herb", "forage"))
            {
                return p => !p.Never && (p.Kind == PickKind.Berry || p.Kind == PickKind.Mushroom || p.Kind == PickKind.Herb);
            }
            bool berries = Has(what, "ягод", "berr");
            bool mushrooms = Has(what, "гриб", "mushroom");
            bool food = Has(what, "еда", "еду", "съедоб", "food");
            if (berries || mushrooms || food)
            {
                return p => !p.Never && ((berries || food) && p.Kind == PickKind.Berry || (mushrooms || food) && p.Kind == PickKind.Mushroom
                    || NameMatches(what, p.Display));
            }
            if (Has(what, "ветк", "хворост", "branch"))
            {
                return p => p.Prefab.StartsWith("Pickable_Branch");
            }
            return p => !p.Never && (NameMatches(what, p.Display) || NameMatches(what, Localization.instance.Localize(p.ItemToken))
                || p.ItemPrefab.Equals(what, StringComparison.OrdinalIgnoreCase));
        }

        public static Func<PreyInfo, bool> PreyFilter(string what)
        {
            if (IsAll(what) || Has(what, "дичь", "дичи", "звер", "животн", "animal", "game"))
            {
                return p => true;
            }
            return p => NameMatches(what, p.Display) || p.Prefab.Equals(what, StringComparison.OrdinalIgnoreCase);
        }

        // Для отдать / сложить / взять: «малину», «ягоды», «стрелы», «дерево», «руду», пусто — всё
        public static Func<ItemDrop.ItemData, bool> ItemFilter(string what)
        {
            if (IsAll(what) || Has(what, "добыч", "лут", "loot"))
            {
                return i => true;
            }
            string prefabFor = Normalize(what);
            HashSet<string> names = null;
            if (Has(what, "ягод", "berr"))
            {
                names = new HashSet<string>(s_berries);
            }
            else if (Has(what, "гриб", "mushroom"))
            {
                names = new HashSet<string>(PrefabsOfKind(PickKind.Mushroom));
            }
            else if (Has(what, "трав", "растен", "herb"))
            {
                names = new HashSet<string>(s_berries.Concat(s_herbs).Concat(PrefabsOfKind(PickKind.Mushroom)));
            }
            else if (Has(what, "дров", "дерев", "wood") && !Has(what, "тонк", "хорош", "древн", "мертв", "железн"))
            {
                names = new HashSet<string> { "Wood" };
            }
            else if (Has(what, "камн", "камен", "булыжн", "stone") && !Has(what, "точил"))
            {
                names = new HashSet<string> { "Stone" };
            }
            bool food = Has(what, "еда", "еду", "еды", "съедоб", "food");
            bool ore = Has(what, "руд", "ore");
            bool ammo = Has(what, "стрел", "arrow");
            return item =>
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
                if (names != null)
                {
                    return names.Contains(prefab);
                }
                if (food && item.m_shared.m_food > 0f)
                {
                    return true;
                }
                if (ammo && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo && item.m_shared.m_ammoType == "$ammo_arrows"
                    && Normalize(what).Split(' ').Length <= 1)
                {
                    return true;
                }
                if (ore && prefab.EndsWith("Ore") && Normalize(what).Split(' ').Length <= 1)
                {
                    return true;
                }
                return NameMatches(what, Localization.instance.Localize(item.m_shared.m_name))
                    || prefab.Equals(prefabFor, StringComparison.OrdinalIgnoreCase);
            };
        }

        private static IEnumerable<string> PrefabsOfKind(PickKind kind)
        {
            EnsureBuilt();
            return s_picks != null ? s_picks.Values.Where(p => p.Kind == kind).Select(p => p.ItemPrefab).Distinct() : Enumerable.Empty<string>();
        }

        // Камни по руде: «медь» → медные жилы, «олово» → оловянные, пусто или «камень» — что попадётся
        public static Func<string, bool> RockFilter(string what)
        {
            if (IsAll(what) || Has(what, "камн", "камен", "stone"))
            {
                return name => true;
            }
            var map = new (string word, string prefab)[]
            {
                ("мед", "copper"), ("медь", "copper"), ("олов", "tin"), ("серебр", "silver"), ("обсидиан", "obsidian"),
                ("желез", "iron"), ("copper", "copper"), ("tin", "tin"), ("silver", "silver"),
            };
            foreach (var (word, prefab) in map)
            {
                if (Has(what, word))
                {
                    return name => name.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            return name => true;
        }
    }
}
