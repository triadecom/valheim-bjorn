using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VikingCompanion
{
    // Печи: «загрузи плавильню», «закинь дров в углежог», «забери слитки».
    // Руду, уголь и дрова берёт из мешка, а чего не хватает — из Сундука Бьёрна.
    // Работает с любой постройкой-переработчиком: плавильня, доменная печь, углежог, мельница, прялка, очиститель эйтра.
    public partial class Companion
    {
        private const float FurnaceRadius = 40f;

        // Ценное дерево в углежог — только если его назвали прямо
        private static readonly HashSet<string> s_preciousWood = new HashSet<string>
        {
            "FineWood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood",
        };

        private List<Smelter> m_furnaces = new List<Smelter>();
        private System.Func<Smelter, string, bool> m_furnaceInput;
        private bool m_fuelOnly;
        private int m_furnaceIndex;
        private bool m_smeltAtChest;
        private float m_nextFeed;
        private int m_fedTotal;
        private readonly Dictionary<string, Dictionary<string, int>> m_fed = new Dictionary<string, Dictionary<string, int>>();
        private readonly List<string> m_smeltNotes = new List<string>();
        private System.Func<ItemDrop, bool> m_lootFilter;
        private readonly HashSet<Container> m_chestsVisited = new HashSet<Container>();

        private static string FurnaceName(Smelter s) => Localization.instance.Localize(s.m_name);

        private static List<Smelter> FurnacesNear(Vector3 at, string what)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(at, FurnaceRadius, pieces);
            List<Smelter> all = pieces.Select(p => p != null ? p.GetComponent<Smelter>() : null)
                .Where(s => s != null && s.m_nview != null && s.m_nview.IsValid()).Distinct().ToList();
            // «в углежог», «в плавильню» — только такие печи
            string w = Catalog.Normalize(what);
            string kind = Regex.IsMatch(w, "углеж|угольн|kiln") ? "charcoal"
                : Regex.IsMatch(w, "доменн|blast") ? "blast"
                : Regex.IsMatch(w, "плавил|smelter") ? "smelter"
                : Regex.IsMatch(w, "мельниц|windmill") ? "windmill"
                : Regex.IsMatch(w, "прялк|spinning") ? "spinning"
                : Regex.IsMatch(w, "очистит|эйтр|refinery") ? "refinery" : null;
            if (kind != null)
            {
                List<Smelter> some = all.Where(s => s.name.ToLowerInvariant().Contains(kind)).ToList();
                if (some.Count > 0)
                {
                    return some;
                }
            }
            return all;
        }

        private static IEnumerable<string> Inputs(Smelter s) =>
            s.m_conversion.Where(c => c.m_from != null).Select(c => c.m_from.gameObject.name);

        private static string FuelOf(Smelter s) => s.m_fuelItem != null ? s.m_fuelItem.gameObject.name : null;

        private int FuelSpace(Smelter s) => s.m_fuelItem != null ? Mathf.Max(0, Mathf.FloorToInt(s.m_maxFuel - s.GetFuel())) : 0;

        private static int OreSpace(Smelter s) => Mathf.Max(0, s.m_maxOre - s.GetQueueSize());

        // Сколько этого предмета у него в мешке (не считая снаряжения)
        private int InBag(string prefab)
        {
            int n = 0;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == prefab && !IsKit(item))
                {
                    n += item.m_stack;
                }
            }
            return n;
        }

        private ItemDrop.ItemData BagItem(string prefab)
        {
            return Humanoid.GetInventory().GetAllItems()
                .FirstOrDefault(i => i.m_dropPrefab != null && i.m_dropPrefab.name == prefab && !IsKit(i));
        }

        private void StartSmelt()
        {
            Player player = Player.m_localPlayer;
            Vector3 at = player != null ? player.transform.position : transform.position;
            m_furnaces = FurnacesNear(at, m_what);
            m_furnaceIndex = 0;
            m_fedTotal = 0;
            m_fed.Clear();
            m_smeltNotes.Clear();
            m_nextFeed = 0f;
            if (m_furnaces.Count == 0)
            {
                m_immediateResult = "печей рядом нет (ищешь в 40 м от игрока)";
                return;
            }
            string w = Catalog.Normalize(m_what);
            bool namesInput = Regex.IsMatch(w, "руд|медь|мед|олов|желез|серебр|лом|дров|дерев|древесин|ячмен|лён|лен|сок|ткан|ore|wood");
            m_fuelOnly = Regex.IsMatch(w, "уголь|угл|топлив|coal") && !namesInput && !Regex.IsMatch(w, "углеж");
            System.Func<ItemDrop.ItemData, bool> filter = namesInput ? Catalog.ItemFilter(m_what) : null;
            m_furnaceInput = (s, prefab) =>
            {
                if (m_fuelOnly)
                {
                    return false;
                }
                if (filter != null)
                {
                    GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
                    ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                    return drop != null && (filter(drop.m_itemData) || (Regex.IsMatch(w, "дров|дерев|древесин|wood") && prefab == "Wood"));
                }
                return !s_preciousWood.Contains(prefab);
            };

            // Чего не хватает в мешке — взять из сундука, где это есть
            Dictionary<string, int> need = Needs();
            m_chestsVisited.Clear();
            Container chest = HelpfulChest(at);
            bool chestHelps = chest != null;
            m_smeltAtChest = chestHelps;
            m_chest = chest;
            if (!chestHelps && need.All(kv => InBag(kv.Key) == 0))
            {
                m_immediateResult = need.Count == 0
                    ? "печи и так полны"
                    : "загружать нечего: ни в мешке, ни в сундуке нет " + string.Join(", ", need.Keys.Select(DisplayOf).Distinct());
            }
        }

        private bool ChestHelps(Container chest)
        {
            return Needs().Any(kv => kv.Value > InBag(kv.Key) && chest.GetInventory().CountItems(PrefabToken(kv.Key)) > 0);
        }

        private Container HelpfulChest(Vector3 playerPos)
        {
            Container chest = WorldScan.FindChest(transform.position, playerPos, 90f, ChestHelps, m_chestsVisited);
            return chest != null && ChestHelps(chest) ? chest : null;
        }

        // Что и сколько влезет во все печи: имя префаба → штук
        private Dictionary<string, int> Needs()
        {
            var need = new Dictionary<string, int>();
            int budget = m_goal > 0 ? m_goal : int.MaxValue;
            foreach (Smelter s in m_furnaces)
            {
                string fuel = FuelOf(s);
                if (fuel != null && FuelSpace(s) > 0 && (m_fuelOnly || Inputs(s).Any(i => m_furnaceInput(s, i))))
                {
                    need[fuel] = (need.TryGetValue(fuel, out int f) ? f : 0) + FuelSpace(s);
                }
                int space = Mathf.Min(OreSpace(s), budget);
                foreach (string input in Inputs(s).Where(i => m_furnaceInput(s, i)))
                {
                    need[input] = (need.TryGetValue(input, out int n) ? n : 0) + space;
                }
            }
            return need;
        }

        private static string PrefabToken(string prefab)
        {
            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            return go != null ? go.GetComponent<ItemDrop>().m_itemData.m_shared.m_name : prefab;
        }

        private static string DisplayOf(string prefab) => Localization.instance.Localize(PrefabToken(prefab)).ToLowerInvariant();

        private void UpdateSmelt(float dt, Player player)
        {
            m_taskTimeLeft -= dt;
            if (m_taskTimeLeft <= 0f)
            {
                FinishSmelt("время вышло");
                return;
            }
            if (m_smeltAtChest)
            {
                if (m_chest == null)
                {
                    m_smeltAtChest = false;
                    return;
                }
                if (!WalkTo(dt, m_chest.transform.position, 2f))
                {
                    if (m_stuckTimer > 10f)
                    {
                        m_smeltNotes.Add("не смог дойти до сундука");
                        m_smeltAtChest = false;
                    }
                    return;
                }
                Humanoid.m_zanim.SetTrigger("interact");
                TakeForFurnaces(m_chest);
                m_chestsVisited.Add(m_chest);
                // Чего-то ещё не хватает, а оно есть в другом сундуке — туда
                m_chest = HelpfulChest(player.transform.position);
                m_smeltAtChest = m_chest != null;
                m_bestDist = float.MaxValue;
                m_stuckTimer = 0f;
                return;
            }
            if (m_furnaceIndex >= m_furnaces.Count)
            {
                FinishSmelt("");
                return;
            }
            Smelter s = m_furnaces[m_furnaceIndex];
            if (s == null || !s.m_nview.IsValid())
            {
                m_furnaceIndex++;
                return;
            }
            Vector3 door = s.m_addOreSwitch != null ? s.m_addOreSwitch.transform.position : s.transform.position;
            if (!WalkTo(dt, door, 2f))
            {
                if (m_stuckTimer > 10f)
                {
                    m_smeltNotes.Add($"не дошёл до {FurnaceName(s).ToLowerInvariant()}");
                    NextFurnace();
                }
                return;
            }
            Ai.LookAt(door);
            if (Time.time < m_nextFeed)
            {
                return;
            }
            m_nextFeed = Time.time + 0.2f;
            if (!FeedOne(s))
            {
                if (s.m_requiresRoof && !s.m_haveRoof)
                {
                    m_smeltNotes.Add($"{FurnaceName(s).ToLowerInvariant()} без крыши — не работает");
                }
                NextFurnace();
            }
        }

        private void NextFurnace()
        {
            m_furnaceIndex++;
            m_bestDist = float.MaxValue;
            m_stuckTimer = 0f;
        }

        // Одна штука в печь: сперва топливо, потом сырьё. false — больше нечего или некуда
        private bool FeedOne(Smelter s)
        {
            string fuel = FuelOf(s);
            bool wantsFuel = fuel != null && FuelSpace(s) > 0 && (m_fuelOnly || Inputs(s).Any(i => m_furnaceInput(s, i)));
            if (wantsFuel && InBag(fuel) > 0 && s.OnAddFuel(null, Humanoid, null))
            {
                Count(s, fuel);
                return true;
            }
            if (OreSpace(s) == 0 || (m_goal > 0 && m_fedTotal >= m_goal))
            {
                return false;
            }
            foreach (string input in Inputs(s).Where(i => m_furnaceInput(s, i)))
            {
                ItemDrop.ItemData item = BagItem(input);
                if (item != null && s.OnAddOre(null, Humanoid, item))
                {
                    m_fedTotal++;
                    Count(s, input);
                    return true;
                }
            }
            return false;
        }

        private void Count(Smelter s, string prefab)
        {
            string furnace = FurnaceName(s);
            if (!m_fed.TryGetValue(furnace, out Dictionary<string, int> items))
            {
                m_fed[furnace] = items = new Dictionary<string, int>();
            }
            string name = DisplayOf(prefab);
            items[name] = (items.TryGetValue(name, out int n) ? n : 0) + 1;
            if (items.Values.Sum() % 4 == 1)
            {
                Humanoid.m_zanim.SetTrigger("interact");
            }
        }

        // Из сундука — ровно столько, сколько влезет в печи (и сколько просили)
        private void TakeForFurnaces(Container chest)
        {
            Inventory from = chest.GetInventory();
            Inventory mine = Humanoid.GetInventory();
            var taken = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in Needs())
            {
                int want = kv.Value - InBag(kv.Key);
                string token = PrefabToken(kv.Key);
                foreach (ItemDrop.ItemData item in from.GetAllItems().Where(i => i.m_shared.m_name == token).ToList())
                {
                    if (want <= 0)
                    {
                        break;
                    }
                    int take = Mathf.Min(want, item.m_stack);
                    ItemDrop.ItemData moving = item.Clone();
                    moving.m_stack = take;
                    if (!mine.AddItem(moving))
                    {
                        break;   // мешок полон
                    }
                    from.RemoveItem(item, take);
                    want -= take;
                    string name = DisplayOf(kv.Key);
                    taken[name] = (taken.TryGetValue(name, out int n) ? n : 0) + take;
                }
            }
            if (taken.Count > 0)
            {
                m_smeltNotes.Insert(0, "взял из сундука: " + Join(taken));
            }
        }

        private void FinishSmelt(string note)
        {
            var parts = m_fed.Select(kv => $"{kv.Key} — {Join(kv.Value)}").ToList();
            string result = parts.Count > 0 ? "загрузил печи: " + string.Join("; ", parts) : "в печи ничего не загрузил";
            if (parts.Count == 0)
            {
                Dictionary<string, int> need = Needs();
                if (need.Count == 0)
                {
                    result += " — они и так полны";
                }
                else if (need.All(kv => InBag(kv.Key) == 0))
                {
                    result += " — нечего: нет " + string.Join(", ", need.Keys.Select(DisplayOf).Distinct());
                }
            }
            else
            {
                // Чего не хватило: печь просит топлива, а угля нет
                var lacking = m_furnaces.Where(s => s != null && FuelOf(s) != null && FuelSpace(s) > 0 && s.GetQueueSize() > 0 && InBag(FuelOf(s)) == 0)
                    .Select(s => DisplayOf(FuelOf(s))).Distinct().ToList();
                if (lacking.Count > 0)
                {
                    m_smeltNotes.Add("не хватило: " + string.Join(", ", lacking));
                }
            }
            if (m_smeltNotes.Count > 0)
            {
                result += " (" + string.Join("; ", m_smeltNotes) + ")";
            }
            if (note.Length > 0)
            {
                result += $" ({note})";
            }
            Finish(result);
        }

        // ---------- Забрать готовое ----------

        private void StartCollect()
        {
            Player player = Player.m_localPlayer;
            Vector3 at = player != null ? player.transform.position : transform.position;
            m_furnaces = FurnacesNear(at, m_what);
            m_furnaceIndex = 0;
            if (m_furnaces.Count == 0)
            {
                m_immediateResult = "печей рядом нет (ищешь в 40 м от игрока)";
                return;
            }
            var outputs = new HashSet<string>(m_furnaces.SelectMany(s => s.m_conversion).Where(c => c.m_to != null).Select(c => c.m_to.gameObject.name));
            System.Func<ItemDrop.ItemData, bool> filter = Catalog.IsAll(m_what) || Regex.IsMatch(Catalog.Normalize(m_what), "готов|печ|плавил|углеж|слитк|всё|все")
                ? null : Catalog.ItemFilter(m_what);
            m_lootFilter = d => d.m_itemData.m_dropPrefab != null && outputs.Contains(d.m_itemData.m_dropPrefab.name)
                && (filter == null || filter(d.m_itemData) || Regex.IsMatch(Catalog.Normalize(m_what), "слитк|bar"));
        }

        private void UpdateCollect(float dt, Player player)
        {
            m_taskTimeLeft -= dt;
            if (m_taskTimeLeft <= 0f || m_furnaceIndex >= m_furnaces.Count)
            {
                m_lootFilter = null;
                string gained = Gained();
                Finish(gained.Length > 0 ? "забрал из печей: " + gained : "в печах пока нечего забирать");
                return;
            }
            Smelter s = m_furnaces[m_furnaceIndex];
            if (s == null || !s.m_nview.IsValid())
            {
                NextFurnace();
                return;
            }
            Vector3 output = s.m_outputPoint != null ? s.m_outputPoint.position : s.transform.position;
            if (m_lootAroundCenter && Utils.DistanceXZ(m_lootCenter, output) < 0.1f)
            {
                // Подбирает то, что выпало у этой печи
                if (StepLoot(dt, 5f) == LootState.Nothing)
                {
                    m_lootAroundCenter = false;
                    NextFurnace();
                }
                return;
            }
            if (!WalkTo(dt, output, 2.5f))
            {
                if (m_stuckTimer > 10f)
                {
                    NextFurnace();
                }
                return;
            }
            // Готовое, что копится внутри (мельница, прялка), — выгрести наружу
            if (s.GetProcessedQueueSize() > 0)
            {
                Humanoid.m_zanim.SetTrigger("interact");
                s.OnEmpty(null, Humanoid, null);
            }
            m_lootCenter = output;
            m_lootAroundCenter = true;
            m_lootSearchTimer = 0.6f;   // выгребенное падает не сразу
        }

        // Дойти до точки. true — на месте (или упёрся совсем рядом)
        private bool WalkTo(float dt, Vector3 pos, float reach)
        {
            float dist = Utils.DistanceXZ(pos, transform.position);
            if (dist < m_bestDist - 0.25f)
            {
                m_bestDist = dist;
                m_stuckTimer = 0f;
            }
            else
            {
                m_stuckTimer += dt;
            }
            if (dist <= reach || (dist < reach + 1.5f && m_stuckTimer > 1.5f))
            {
                Ai.StopMoving();
                return true;
            }
            Ai.MoveTo(dt, pos, reach * 0.6f, dist > 8f);
            return false;
        }
    }
}
