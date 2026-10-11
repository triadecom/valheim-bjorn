using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Поведение компаньона поверх MonsterAI: бой и следование делает ванильный ИИ,
    // а рубка, добыча, сбор и передача вещей — этот компонент (из постфикса UpdateAI).
    public class Companion : MonoBehaviour, Interactable
    {
        public enum Mode { Follow, Stay, Work, Loot, Give }
        public enum WorkKind { Wood, Stone }

        public static Companion Local { get; private set; }

        public Humanoid Humanoid { get; private set; }
        public MonsterAI Ai { get; private set; }
        public Mode CurrentMode { get; private set; } = Mode.Follow;
        public WorkKind CurrentWork { get; private set; }

        // Лес ищем по всей загруженной округе, но не дальше Leash от игрока — иначе зона выгрузится
        private const float SearchRadius = 70f;
        public const float Leash = 75f;
        private const float LootRadius = 12f;
        private const int WorkGoal = 50;
        private const string ModeKey = "vc_mode";

        private ZNetView m_nview;
        private float m_taskTimeLeft;
        private int m_startCount;
        private Vector3 m_workOrigin;
        private MonoBehaviour m_target;
        private ZDOID m_targetId;
        private Collider[] m_targetColliders = new Collider[0];
        private float m_targetSince;
        private float m_bestDist;
        private float m_stuckTimer;
        private float m_nextSwing;
        private int m_swings;
        private float m_searchTimer;
        private ItemDrop m_loot;
        private float m_lootSince;
        private float m_lootSearchTimer;
        private int m_looted;
        private float m_huntUntil;
        private Vector3 m_stayPoint;
        private bool m_restoring;
        private float m_nextEquip;
        private readonly HashSet<ZDOID> m_skipTargets = new HashSet<ZDOID>();
        private readonly HashSet<int> m_skipLoot = new HashSet<int>();

        public string Name => Humanoid != null ? Humanoid.m_name : Plugin.CompanionName.Value;
        public bool IsHunting => Time.time < m_huntUntil;

        private static void Debug(string message)
        {
            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo("[debug] " + message);
            }
        }

        private void Awake()
        {
            Humanoid = GetComponent<Humanoid>();
            Ai = GetComponent<MonsterAI>();
            m_nview = GetComponent<ZNetView>();
            Humanoid.m_onDeath += OnDeath;
        }

        private void Start()
        {
            if (m_nview == null || !m_nview.IsValid() || !m_nview.IsOwner())
            {
                return;
            }
            Local = this;
            ApplyLook();
            // Режим хранится в ZDO: если зона выгружалась, компаньон продолжает сторожить
            m_restoring = true;
            SetMode(m_nview.GetZDO().GetInt(ModeKey) == (int)Mode.Stay ? Mode.Stay : Mode.Follow);
            m_restoring = false;
            CompanionManager.OnCompanionLoaded(this);
        }

        private void OnDestroy()
        {
            if (Local == this)
            {
                Local = null;
                if (!Humanoid.IsDead())
                {
                    CompanionManager.OnCompanionUnloaded(this);
                }
            }
        }

        private void ApplyLook()
        {
            VisEquipment vis = GetComponent<VisEquipment>();
            if (vis == null)
            {
                return;
            }
            vis.SetModel(0);
            vis.SetHairItem(Plugin.Hair.Value.GetStableHashCode());
            vis.SetBeardItem(Plugin.Beard.Value.GetStableHashCode());
            vis.SetHairColor(ParseColor(Plugin.HairColor.Value));
        }

        private static Vector3 ParseColor(string value)
        {
            string[] parts = value.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                return new Vector3(r, g, b);
            }
            return new Vector3(0.55f, 0.32f, 0.16f);
        }

        // ---------- Режимы ----------

        public void SetMode(Mode mode, WorkKind work = WorkKind.Wood)
        {
            Player player = Player.m_localPlayer;
            CurrentMode = mode;
            m_target = null;
            m_loot = null;
            m_looted = 0;
            m_lootSearchTimer = 0f;
            m_stuckTimer = 0f;
            m_skipTargets.Clear();
            m_skipLoot.Clear();
            if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner())
            {
                m_nview.GetZDO().Set(ModeKey, (int)mode);
            }

            switch (mode)
            {
                case Mode.Follow:
                    Ai.ResetPatrolPoint();
                    Ai.SetFollowTarget(player != null ? player.gameObject : null);
                    EquipCombat();
                    break;
                case Mode.Stay:
                    Ai.SetFollowTarget(null);
                    // Точка охраны в ZDO переживает выгрузку зоны
                    m_stayPoint = Ai.GetPatrolPoint(out Vector3 saved) && m_restoring ? saved : transform.position;
                    Ai.SetPatrolPoint(m_stayPoint);
                    EquipCombat();
                    break;
                case Mode.Work:
                    Ai.SetFollowTarget(null);
                    Ai.ResetPatrolPoint();
                    CurrentWork = work;
                    m_taskTimeLeft = 240f;
                    m_workOrigin = transform.position;
                    m_startCount = CountGathered();
                    EquipTool();
                    break;
                case Mode.Loot:
                    Ai.SetFollowTarget(null);
                    Ai.ResetPatrolPoint();
                    m_taskTimeLeft = 60f;
                    m_workOrigin = transform.position;
                    break;
                case Mode.Give:
                    Ai.SetFollowTarget(null);
                    Ai.ResetPatrolPoint();
                    m_taskTimeLeft = 40f;
                    if (CountGathered() == 0)
                    {
                        // Отдавать нечего — незачем идти
                        CurrentMode = Mode.Follow;
                        Ai.SetFollowTarget(player != null ? player.gameObject : null);
                        CompanionManager.OnTaskFinished(this, "give_empty", "");
                    }
                    break;
            }
        }

        public void Hunt(float seconds)
        {
            m_huntUntil = Time.time + seconds;
            Character prey = FindNearestTarget(30f, includeAnimals: true);
            if (prey != null)
            {
                Ai.m_targetCreature = prey;
                Ai.m_lastKnownTargetPos = prey.transform.position;
                Ai.SetAlerted(true);
            }
        }

        public string DescribeMode()
        {
            switch (CurrentMode)
            {
                case Mode.Stay: return "сторожит место";
                case Mode.Work: return CurrentWork == WorkKind.Wood ? "рубит лес" : "добывает камень";
                case Mode.Loot: return "собирает вещи с земли";
                case Mode.Give: return "несёт добычу побратиму";
                default: return InCombat() ? "бьётся с врагом" : "идёт следом";
            }
        }

        public string DescribeCarry()
        {
            var parts = new List<string>();
            foreach (var group in GatheredItems().GroupBy(i => i.m_shared.m_name))
            {
                parts.Add($"{Localization.instance.Localize(group.Key)} ×{group.Sum(i => i.m_stack)}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "";
        }

        public bool InCombat()
        {
            return Ai.m_targetCreature != null && !Ai.m_targetCreature.IsDead();
        }

        // ---------- Кадр ИИ (вызывается после MonsterAI.UpdateAI) ----------

        // Всё, кроме следования, ведём сами: ванильный ИИ тогда только ищет врагов
        public bool ControlsMovement => CurrentMode != Mode.Follow;

        public void UpdateTask(float dt)
        {
            Player player = Player.m_localPlayer;
            if (Humanoid.IsDead() || player == null)
            {
                return;
            }
            switch (CurrentMode)
            {
                case Mode.Stay:
                    UpdateStay(dt, player);
                    break;
                case Mode.Work:
                    UpdateWork(dt);
                    break;
                case Mode.Loot:
                    UpdateLoot(dt);
                    break;
                case Mode.Give:
                    UpdateGive(dt, player);
                    break;
            }
        }

        private void UpdateStay(float dt, Player player)
        {
            if (Time.time >= m_nextEquip)
            {
                m_nextEquip = Time.time + 1f;
                EquipCombat();
            }
            float dist = Utils.DistanceXZ(m_stayPoint, transform.position);
            if (dist > 1.5f)
            {
                Ai.MoveTo(dt, m_stayPoint, 0.5f, dist > 6f);
                return;
            }
            Ai.StopMoving();
            if (Vector3.Distance(player.transform.position, transform.position) < 12f)
            {
                Ai.LookAt(player.GetHeadPoint());
            }
        }

        // ---------- Рубка и добыча ----------

        private void UpdateWork(float dt)
        {
            m_taskTimeLeft -= dt;
            int gathered = CountGathered() - m_startCount;
            if (m_taskTimeLeft <= 0f || gathered >= WorkGoal)
            {
                Finish(CurrentWork == WorkKind.Wood ? "work_wood_done" : "work_stone_done");
                return;
            }

            // Сначала подбираем то, что нападало рядом
            if (StepLoot(dt, 8f) == LootState.Busy)
            {
                return;
            }

            if (!IsValidTarget(m_target))
            {
                m_searchTimer -= dt;
                if (m_searchTimer > 0f)
                {
                    Ai.StopMoving();
                    return;
                }
                m_searchTimer = 1f;
                SetTarget(FindWorkTarget());
                m_targetSince = Time.time;
                m_bestDist = float.MaxValue;
                m_stuckTimer = 0f;
                m_swings = 0;
                if (m_target != null)
                {
                    Debug($"цель: {m_target.GetType().Name} {m_target.name} в {Vector3.Distance(m_target.transform.position, transform.position):0.0} м");
                }
                if (m_target == null)
                {
                    // Целей больше нет — сначала собрать то, что нападало
                    if (FindLoot(10f) != null)
                    {
                        m_lootSearchTimer = 0f;
                        return;
                    }
                    Finish(CurrentWork == WorkKind.Wood ? "work_wood_none" : "work_stone_none");
                }
                return;
            }

            if (Time.time >= m_nextEquip)
            {
                m_nextEquip = Time.time + 1f;
                EquipTool();
            }
            // Реальная точка поверхности по лучу от груди к центру цели
            Vector3 point = SurfacePoint(m_target, out float dist);
            if (dist < m_bestDist - 0.25f)
            {
                m_bestDist = dist;
                m_stuckTimer = 0f;
            }
            else
            {
                m_stuckTimer += dt;
            }

            if (dist > Reach)
            {
                Ai.MoveTo(dt, point, 0.6f, dist > 6f);
                if (m_stuckTimer > 6f)
                {
                    Blacklist(m_target);
                }
                return;
            }

            Ai.StopMoving();
            m_stuckTimer = 0f;   // стоит и бьёт — это работа, а не застревание
            Ai.LookAt(point);
            if (Time.time >= m_nextSwing && !Humanoid.InAttack() && Ai.IsLookingAt(point, 20f))
            {
                if (Humanoid.StartAttack(null, false))
                {
                    m_nextSwing = Time.time + 1.1f;
                    m_swings++;
                    Debug($"удар #{m_swings} по {m_target.name}: до поверхности {dist:0.00} м, прочность {TargetHealth(m_target)}");
                }
            }
            // Если долго не выходит (не тот инструмент, мешает склон) — берёмся за другое.
            // Большие рудные жилы бьются долго, им даём больше времени.
            if (Time.time - m_targetSince > (m_target is MineRock5 ? 120f : 45f))
            {
                Blacklist(m_target);
            }
        }

        private const float Reach = 1.4f;

        private void SetTarget(MonoBehaviour target)
        {
            m_target = target;
            ZNetView nview = target != null ? target.GetComponent<ZNetView>() : null;
            m_targetId = nview != null && nview.GetZDO() != null ? nview.GetZDO().m_uid : ZDOID.None;
            // Коллайдеры цели берём один раз, а не каждый кадр
            m_targetColliders = target != null ? target.GetComponentsInChildren<Collider>() : new Collider[0];
        }

        private Vector3 SurfacePoint(MonoBehaviour target, out float distance)
        {
            Vector3 origin = transform.position + Vector3.up * 1f;
            Vector3 center = TargetCenter(target);
            // Целимся на высоту груди: так и подходим, и бьём
            center.y = Mathf.Clamp(center.y, transform.position.y + 0.3f, transform.position.y + 1.6f);
            Vector3 toCenter = center - origin;
            var ray = new Ray(origin, toCenter.normalized);
            float best = float.MaxValue;
            Vector3 bestPoint = center;
            foreach (Collider c in m_targetColliders)
            {
                if (c != null && c.enabled && !c.isTrigger && c.Raycast(ray, out RaycastHit hit, toCenter.magnitude + 2f) && hit.distance < best)
                {
                    best = hit.distance;
                    bestPoint = hit.point;
                }
            }
            if (best == float.MaxValue)
            {
                // Луч не попал (мы внутри габарита или цель за склоном) — идём к центру
                distance = Utils.DistanceXZ(center, transform.position);
                return center;
            }
            distance = best;
            return bestPoint;
        }

        private Vector3 TargetCenter(MonoBehaviour target)
        {
            if (target is TreeBase)
            {
                return target.transform.position + Vector3.up * 1f;   // ствол у основания
            }
            bool any = false;
            Bounds bounds = default;
            foreach (Collider c in m_targetColliders)
            {
                if (c == null || !c.enabled || c.isTrigger)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = c.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(c.bounds);
                }
            }
            return any ? bounds.center : target.transform.position;
        }

        private static string TargetHealth(MonoBehaviour target)
        {
            ZNetView nview = target.GetComponent<ZNetView>();
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            return zdo != null ? zdo.GetFloat(ZDOVars.s_health, -1f).ToString("0") : "?";
        }

        public int ToolTier(bool wood)
        {
            ItemDrop.ItemData tool = FindTool(wood ? Skills.SkillType.Axes : Skills.SkillType.Pickaxes);
            return tool != null ? tool.m_shared.m_toolTier : -1;
        }

        private MonoBehaviour FindWorkTarget()
        {
            bool wood = CurrentWork == WorkKind.Wood;
            int tier = ToolTier(wood);
            Player player = Player.m_localPlayer;
            if (tier < 0 || player == null)
            {
                return null;
            }
            ZNetView nview = WorldScan.FindNearest(transform.position, SearchRadius, wood, tier,
                player.transform.position, Leash, m_skipTargets);
            if (nview == null)
            {
                return null;
            }
            if (wood)
            {
                return nview.TryGetComponent(out TreeLog log) ? log : (MonoBehaviour)nview.GetComponent<TreeBase>();
            }
            if (nview.TryGetComponent(out MineRock5 rock5))
            {
                return rock5;
            }
            return nview.TryGetComponent(out MineRock rock) ? rock : (MonoBehaviour)nview.GetComponent<Destructible>();
        }

        private static bool IsValidTarget(MonoBehaviour target)
        {
            return target != null && target.gameObject.activeInHierarchy;
        }

        private void Blacklist(MonoBehaviour target)
        {
            Debug($"бросаю цель {(target != null ? target.name : "-")} (ударов {m_swings}, застрял {m_stuckTimer:0.0} с)");
            if (!m_targetId.IsNone())
            {
                m_skipTargets.Add(m_targetId);
            }
            SetTarget(null);
        }

        // ---------- Сбор вещей ----------

        private enum LootState { Busy, Waiting, Nothing }

        private void UpdateLoot(float dt)
        {
            m_taskTimeLeft -= dt;
            LootState state = StepLoot(dt, LootRadius);
            if (state == LootState.Nothing || m_taskTimeLeft <= 0f)
            {
                Finish("loot_done");
                return;
            }
            if (state == LootState.Waiting)
            {
                Ai.StopMoving();
            }
        }

        private LootState StepLoot(float dt, float radius)
        {
            if (m_loot == null || !m_loot.CanPickup())
            {
                m_loot = null;
                m_lootSearchTimer -= dt;
                if (m_lootSearchTimer > 0f)
                {
                    return LootState.Waiting;
                }
                m_lootSearchTimer = 1.5f;
                m_loot = FindLoot(radius);
                m_lootSince = Time.time;
                if (m_loot == null)
                {
                    return LootState.Nothing;
                }
            }

            Vector3 pos = m_loot.transform.position;
            float dist = Vector3.Distance(pos, transform.position);
            if (dist > 1.5f)
            {
                Ai.MoveTo(dt, pos, 0.6f, dist > 6f);
                if (Time.time - m_lootSince > 12f)
                {
                    m_skipLoot.Add(m_loot.GetInstanceID());
                    m_loot = null;
                }
                return LootState.Busy;
            }

            Ai.StopMoving();
            if (Humanoid.Pickup(m_loot.gameObject, false, true))
            {
                m_looted++;
                Debug($"подобрал {m_loot.m_itemData.m_shared.m_name} ×{m_loot.m_itemData.m_stack}");
            }
            else
            {
                m_skipLoot.Add(m_loot.GetInstanceID());
            }
            m_loot = null;
            m_lootSearchTimer = 0f;
            return LootState.Busy;
        }

        private ItemDrop FindLoot(float radius)
        {
            ItemDrop best = null;
            float bestDist = radius;
            Vector3 center = CurrentMode == Mode.Loot ? m_workOrigin : transform.position;
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (drop == null)
                {
                    continue;
                }
                // Сначала дешёвые проверки расстояния, луч к земле — только для близких вещей
                Vector3 pos = drop.transform.position;
                float dist = Vector3.Distance(pos, center);
                if (dist >= bestDist || Mathf.Abs(pos.y - transform.position.y) > 4f)
                {
                    continue;
                }
                if (m_skipLoot.Contains(drop.GetInstanceID()) || !drop.CanPickup() || drop.GetComponent<Fish>() != null)
                {
                    continue;
                }
                if (ZoneSystem.instance.GetGroundHeight(pos) < ZoneSystem.instance.m_waterLevel - 0.3f)
                {
                    continue;   // в воду за вещами не лезет
                }
                bestDist = dist;
                best = drop;
            }
            return best;
        }

        // ---------- Передача добычи ----------

        private void UpdateGive(float dt, Player player)
        {
            m_taskTimeLeft -= dt;
            float dist = Vector3.Distance(player.transform.position, transform.position);
            if (dist > 2.5f && m_taskTimeLeft > 0f)
            {
                Ai.MoveTo(dt, player.transform.position, 2f, dist > 8f);
                return;
            }
            Ai.StopMoving();
            Ai.LookAt(player.GetHeadPoint());
            string summary = GiveAll(player);
            CompanionManager.OnTaskFinished(this, string.IsNullOrEmpty(summary) ? "give_empty" : "gave", summary);
            SetMode(Mode.Follow);
        }

        private string GiveAll(Player player)
        {
            Inventory mine = Humanoid.GetInventory();
            Inventory theirs = player.GetInventory();
            var given = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in GatheredItems().ToList())
            {
                int stack = item.m_stack;
                mine.RemoveItem(item);
                bool fits = theirs.GetTotalWeight() + item.GetWeight() <= player.GetMaxCarryWeight() && theirs.CanAddItem(item);
                if (!fits || !theirs.AddItem(item))
                {
                    ItemDrop.DropItem(item, stack, player.transform.position + player.transform.forward * 0.8f + Vector3.up, Quaternion.identity);
                }
                string name = Localization.instance.Localize(item.m_shared.m_name);
                given[name] = (given.TryGetValue(name, out int n) ? n : 0) + stack;
            }
            if (given.Count == 0)
            {
                return "";
            }
            string text = string.Join(", ", given.Select(kv => $"{kv.Key} ×{kv.Value}"));
            player.Message(MessageHud.MessageType.TopLeft, $"{Name} передал: {text}");
            return text;
        }

        // ---------- Снаряжение ----------

        private IEnumerable<ItemDrop.ItemData> GatheredItems()
        {
            return Humanoid.GetInventory().GetAllItems().Where(i => !IsKit(i));
        }

        private bool IsKit(ItemDrop.ItemData item)
        {
            return Humanoid.IsItemEquiped(item)
                || (CompanionPrefab.IsKitItem(item) && (item.IsWeapon() || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield));
        }

        // Вызывается каждый кадр работы — без LINQ, чтобы не мусорить в память
        private int CountGathered()
        {
            int sum = 0;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (!IsKit(item))
                {
                    sum += item.m_stack;
                }
            }
            return sum;
        }

        private ItemDrop.ItemData FindTool(Skills.SkillType skill)
        {
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (item.IsWeapon() && item.m_shared.m_skillType == skill)
                {
                    return item;
                }
            }
            return null;
        }

        private ItemDrop.ItemData FindShield()
        {
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    return item;
                }
            }
            return null;
        }

        public void EquipCombat()
        {
            ItemDrop.ItemData axe = FindTool(Skills.SkillType.Axes);
            if (axe != null && Humanoid.m_rightItem != axe)
            {
                Humanoid.EquipItem(axe, false);
            }
            ItemDrop.ItemData shield = FindShield();
            if (shield != null && Humanoid.m_leftItem == null && (Humanoid.m_rightItem == null || !Humanoid.m_rightItem.IsTwoHanded()))
            {
                Humanoid.EquipItem(shield, false);
            }
        }

        private void EquipTool()
        {
            if (CurrentWork == WorkKind.Wood)
            {
                EquipCombat();
                return;
            }
            ItemDrop.ItemData pickaxe = FindTool(Skills.SkillType.Pickaxes);
            if (pickaxe != null && Humanoid.m_rightItem != pickaxe)
            {
                Humanoid.EquipItem(pickaxe, false);
            }
        }

        // ---------- Цели боя ----------

        public Character FindNearestTarget(float range, bool includeAnimals)
        {
            Character best = null;
            float bestDist = range;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == Humanoid || c.IsDead() || !BaseAI.IsEnemy(Humanoid, c))
                {
                    continue;
                }
                if (!includeAnimals && !IsThreat(c))
                {
                    continue;
                }
                float dist = Vector3.Distance(c.transform.position, transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }
            return best;
        }

        // Звери не враги, пока сами не нападают; монстры — всегда
        public bool IsThreat(Character c)
        {
            if (c.GetFaction() != Character.Faction.AnimalsVeg)
            {
                return true;
            }
            BaseAI ai = c.GetBaseAI();
            Character theirTarget = ai != null ? ai.GetTargetCreature() : null;
            return theirTarget != null && (theirTarget == Humanoid || theirTarget.IsPlayer());
        }

        // ---------- Речь ----------

        public void Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || Chat.instance == null)
            {
                return;
            }
            float ttl = Mathf.Clamp(4f + text.Length * 0.07f, 5f, 14f);
            Chat.instance.SetNpcText(gameObject, Vector3.up * 2.2f, 30f, ttl, Name, text, false);
            Chat.instance.AddString(Name, text, Talker.Type.Normal);
            Chat.instance.m_hideTimer = 0f;
            Plugin.Log.LogInfo($"{Name}: {text}");
        }

        // ---------- Взаимодействие (клавиша E) ----------

        public string GetHoverText()
        {
            string order = CurrentMode == Mode.Stay ? "За мной" : "Жди здесь";
            return Localization.instance.Localize(
                $"{Name}\n[<color=yellow><b>$KEY_Use</b></color>] {order}\n<color=#aaaaaa>Говори с ним в чате</color>");
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold)
            {
                return false;
            }
            SetMode(CurrentMode == Mode.Stay ? Mode.Follow : Mode.Stay);
            CompanionManager.OnOrderByHand(this);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        // ---------- Смерть ----------

        private void OnDeath()
        {
            // Добыча остаётся на месте гибели, снаряжение — нет
            foreach (ItemDrop.ItemData item in GatheredItems().ToList())
            {
                ItemDrop.DropItem(item, item.m_stack, transform.position + Vector3.up, Quaternion.identity);
            }
            Humanoid.GetInventory().RemoveAll();
            CompanionManager.OnCompanionDied(this);
        }

        private void Finish(string reason)
        {
            int gathered = CountGathered() - m_startCount;
            string details = reason.StartsWith("loot") ? m_looted.ToString() : gathered.ToString();
            SetMode(Mode.Follow);
            CompanionManager.OnTaskFinished(this, reason, details);
        }
    }
}
