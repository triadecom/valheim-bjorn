using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace VikingCompanion
{
    // Место, которое игрок показал компаньону: «это наша база», «тут медь», «там тролль».
    public class Place
    {
        public string name = "";
        public string kind = Places.Landmark;
        public float x, y, z;
        public float radius = 15f;
        public string biome = "";
        public string about = "";   // что было вокруг, когда отметили
        public string note = "";    // что сказал о нём игрок: «не дразни там троллей»
        public int day;

        public Vector3 Position => new Vector3(x, y, z);

        public bool Contains(Vector3 pos) => Utils.DistanceXZ(pos, Position) <= radius;
    }

    // Места мира: свои для каждого мира и персонажа, лежат рядом с конфигом мода.
    // Модель видит их в сводке (расстояние и сторона света), компаньон по ним сторожит базу,
    // чинит постройки и обходит опасные места. Ещё здесь помнится, какое место он сторожит.
    internal static class Places
    {
        public const string Home = "home", Portal = "portal", Resource = "resource", Danger = "danger", Landmark = "landmark";
        public static readonly string[] Kinds = { Home, Portal, Resource, Danger, Landmark };

        private const int MaxPlaces = 60;

        private static string s_key;
        private static List<Place> s_list = new List<Place>();
        private static string s_guard = "";

        private class Saved
        {
            public List<Place> places;
            public string guard;
        }

        public static List<Place> All
        {
            get
            {
                EnsureLoaded();
                return s_list;
            }
        }

        // Какое место он сторожит (переживает выход из игры)
        public static string Guard
        {
            get
            {
                EnsureLoaded();
                return s_guard;
            }
            set
            {
                EnsureLoaded();
                if (s_guard != (value ?? ""))
                {
                    s_guard = value ?? "";
                    Save();
                }
            }
        }

        public static float DefaultRadius(string kind)
        {
            switch (kind)
            {
                case Home: return 30f;
                case Danger: return 35f;
                case Resource: return 20f;
                case Portal: return 8f;
                default: return 15f;
            }
        }

        public static string KindName(string kind)
        {
            switch (kind)
            {
                case Home: return "база";
                case Portal: return "портал";
                case Resource: return "ресурсы";
                case Danger: return "опасно";
                default: return "место";
            }
        }

        // ---------- Файл ----------

        private static string Slug(string text)
        {
            string s = Regex.Replace((text ?? "").Trim(), @"[^\w-]+", "_").Trim('_');
            return s.Length > 0 ? (s.Length > 60 ? s.Substring(0, 60) : s) : "default";
        }

        private static string CurrentKey()
        {
            string world = ZNet.World != null ? ZNet.World.m_name : "";
            string player = Game.instance != null && Game.instance.GetPlayerProfile() != null ? Game.instance.GetPlayerProfile().GetName() : "";
            return world.Length > 0 ? $"{Slug(world)}__{Slug(player)}" : null;
        }

        private static string FilePath(string key) => Path.Combine(Paths.ConfigPath, "VikingCompanion", "places", key + ".json");

        private static void EnsureLoaded()
        {
            string key = CurrentKey();
            if (key == null || key == s_key)
            {
                return;
            }
            s_key = key;
            s_list = new List<Place>();
            s_guard = "";
            string path = FilePath(key);
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                Dictionary<string, object> root = Json.Obj(Json.Parse(File.ReadAllText(path)));
                s_guard = Json.Str(root, "guard");
                foreach (object node in Json.List(root, "places"))
                {
                    Dictionary<string, object> o = Json.Obj(node);
                    if (o == null || Json.Str(o, "name").Length == 0)
                    {
                        continue;
                    }
                    s_list.Add(new Place
                    {
                        name = Json.Str(o, "name"),
                        kind = Json.Str(o, "kind", Landmark),
                        x = (float)Json.Num(o, "x"),
                        y = (float)Json.Num(o, "y"),
                        z = (float)Json.Num(o, "z"),
                        radius = (float)Json.Num(o, "radius", 15),
                        biome = Json.Str(o, "biome"),
                        about = Json.Str(o, "about"),
                        note = Json.Str(o, "note"),
                        day = (int)Json.Num(o, "day"),
                    });
                }
                Plugin.Log.LogInfo($"Места: {s_list.Count} ({key})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Места не прочитались ({path}): {e.Message}");
            }
        }

        private static void Save()
        {
            if (s_key == null)
            {
                return;
            }
            try
            {
                string path = FilePath(s_key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Serialize(new Saved { places = s_list, guard = s_guard }));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Места не сохранились: {e.Message}");
            }
        }

        // «Забыть всё» на вкладке настроек
        public static void Clear()
        {
            EnsureLoaded();
            foreach (Place p in s_list)
            {
                RemovePin(p);
            }
            s_list.Clear();
            s_guard = "";
            Save();
        }

        // ---------- Изменения ----------

        public static Place Mark(string name, string kind, Vector3 pos, string note, string about)
        {
            EnsureLoaded();
            kind = Kinds.Contains(kind) ? kind : GuessKind(name + " " + note);
            Place old = Find(name, exact: true);
            if (old != null)
            {
                RemovePin(old);
                s_list.Remove(old);
            }
            var place = new Place
            {
                name = Capitalize(name.Trim()),
                kind = kind,
                x = pos.x, y = pos.y, z = pos.z,
                radius = DefaultRadius(kind),
                biome = WorldScan.BiomeName(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pos) : Heightmap.Biome.None),
                about = about ?? "",
                note = (note ?? "").Trim(),
                day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0,
            };
            s_list.Insert(0, place);
            if (s_list.Count > MaxPlaces)
            {
                s_list.RemoveRange(MaxPlaces, s_list.Count - MaxPlaces);
            }
            AddPin(place);
            Save();
            Plugin.Log.LogInfo($"Место: «{place.name}» ({place.kind}) {pos:F0}, {place.about}");
            return place;
        }

        public static Place Forget(string name)
        {
            Place place = Find(name);
            if (place != null)
            {
                RemovePin(place);
                s_list.Remove(place);
                if (string.Equals(s_guard, place.name, StringComparison.OrdinalIgnoreCase))
                {
                    s_guard = "";
                }
                Save();
            }
            return place;
        }

        private static string Capitalize(string s) => s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s;

        public static string GuessKind(string text)
        {
            string t = Catalog.Normalize(text);
            if (Regex.IsMatch(t, "баз|дом|лагер|крепост|убежищ|хижин|home|base"))
            {
                return Home;
            }
            if (Regex.IsMatch(t, "портал|portal"))
            {
                return Portal;
            }
            if (Regex.IsMatch(t, "опас|тролл|логов|гнезд|драугр|склеп|не ход|осторож|danger"))
            {
                return Danger;
            }
            if (Regex.IsMatch(t, "мед|олов|желез|серебр|руд|обсидиан|жил|залеж|ягод|малин|черник|грибы|лес|ore"))
            {
                return Resource;
            }
            return Landmark;
        }

        // ---------- Поиск ----------

        // «База», «на базе», «домой» → место-база; иначе по названию
        public static Place Find(string name, bool exact = false)
        {
            EnsureLoaded();
            string n = Catalog.Normalize(name);
            if (n.Length == 0)
            {
                return null;
            }
            Place place = s_list.FirstOrDefault(p => Catalog.Normalize(p.name) == n);
            if (place != null || exact)
            {
                return place;
            }
            place = s_list.FirstOrDefault(p => Catalog.NameMatches(name, p.name) || Catalog.NameMatches(p.name, name));
            if (place == null && GuessKind(name) == Home)
            {
                place = HomePlace(null);
            }
            return place;
        }

        // Ближайшая база (к точке, если она задана)
        public static Place HomePlace(Vector3? near)
        {
            EnsureLoaded();
            IEnumerable<Place> homes = s_list.Where(p => p.kind == Home);
            return near.HasValue ? homes.OrderBy(p => Utils.DistanceXZ(p.Position, near.Value)).FirstOrDefault() : homes.FirstOrDefault();
        }

        // Опасное место, в которое попадает точка
        public static Place DangerAt(Vector3 pos)
        {
            EnsureLoaded();
            for (int i = 0; i < s_list.Count; i++)
            {
                if (s_list[i].kind == Danger && s_list[i].Contains(pos))
                {
                    return s_list[i];
                }
            }
            return null;
        }

        public static Place At(Vector3 pos)
        {
            EnsureLoaded();
            return s_list.Where(p => p.kind != Danger && p.Contains(pos)).OrderBy(p => p.radius).FirstOrDefault();
        }

        // ---------- Метки на карте ----------

        private static Minimap.PinType PinFor(string kind)
        {
            switch (kind)
            {
                case Home: return Minimap.PinType.Icon1;
                case Portal: return Minimap.PinType.Icon4;
                case Resource: return Minimap.PinType.Icon2;
                case Danger: return Minimap.PinType.Icon3;
                default: return Minimap.PinType.Icon0;
            }
        }

        private static void AddPin(Place place)
        {
            if (Minimap.instance == null || !Plugin.MapPins.Value)
            {
                return;
            }
            Minimap.PinData existing = Minimap.instance.GetClosestPin(place.Position, 5f, false);
            if (existing != null && existing.m_name == place.name)
            {
                return;
            }
            Minimap.instance.AddPin(place.Position, PinFor(place.kind), place.name, true, false);
        }

        private static void RemovePin(Place place)
        {
            if (Minimap.instance == null)
            {
                return;
            }
            Minimap.PinData pin = Minimap.instance.GetClosestPin(place.Position, 5f, false);
            if (pin != null && pin.m_name == place.name)
            {
                Minimap.instance.RemovePin(pin);
            }
        }

        // Метки, которые игрок сам подписал на карте: «медь», «склеп», «тролль»
        public static IEnumerable<Minimap.PinData> PlayerPins()
        {
            if (Minimap.instance == null)
            {
                return Enumerable.Empty<Minimap.PinData>();
            }
            EnsureLoaded();
            return Minimap.instance.m_pins.Where(p => p.m_save && !string.IsNullOrWhiteSpace(p.m_name)
                && !s_list.Any(pl => pl.name == p.m_name && Utils.DistanceXZ(pl.Position, p.m_pos) < 5f));
        }
    }
}
