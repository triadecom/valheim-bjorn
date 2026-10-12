using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Поведение компаньона поверх MonsterAI: бой и следование делает ванильный ИИ,
    // а рубка, добыча, сбор трав, охота с луком, сундук и передача вещей — этот компонент (из UpdateAI).
    // Приказы приходят планом из нескольких шагов; шаги выполняются по очереди, итог — одним отчётом.
    public partial class Companion : MonoBehaviour, Interactable
    {
        public enum Mode { Follow, Stay, Work, Loot, Give, Pick, Hunt, Store, Fetch, Guard, Repair, Smelt, Collect }
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
        private const float ShootRange = 34f;

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
        private Vector3 m_lootCenter;
        private bool m_lootAroundCenter;
        private int m_looted;
        private float m_huntUntil;
        private Vector3 m_stayPoint;
        private bool m_restoring;
        private float m_nextEquip;
        private readonly HashSet<ZDOID> m_skipTargets = new HashSet<ZDOID>();
        private readonly HashSet<int> m_skipLoot = new HashSet<int>();

        // План: очередь шагов и итоги выполненных
        private readonly Queue<PlanStep> m_plan = new Queue<PlanStep>();
        private readonly List<string> m_results = new List<string>();
        private PlanStep m_step;
        private Vector3 m_planOrigin;
        private bool m_planHadTask;

        // Параметры текущего дела
        private string m_what = "";
        private int m_goal;
        private Dictionary<string, int> m_snapshot = new Dictionary<string, int>();
        private System.Func<Catalog.PickInfo, bool> m_pickFilter;
        private HashSet<string> m_pickNames = new HashSet<string>();
        private System.Func<ItemDrop.ItemData, bool> m_itemFilter;
        private System.Func<string, bool> m_rockFilter;
        private Pickable m_pickable;
        private float m_pickedAt;

        // Охота
        private Character m_prey;
        private Vector3 m_preyLastPos;
        private int m_kills;
        private int m_shotsAtPrey;
        private float m_nextShot;
        private float m_drawStart = -1f;
        private float m_preySince;
        private float m_lootUntil;
        private readonly Dictionary<string, int> m_killed = new Dictionary<string, int>();
        private int m_escaped;
        private int m_huntRetries;

        // Сундук
        private Container m_chest;
        private float m_chestPhaseAt = -1f;
        private bool m_chestOpenedByMe;

        private int m_lastArrowDay = -1;
        // «Принеси 20 из сундука» — отдать ровно столько, а не всё такое из мешка
        private int m_fetchedTotal;
        private string m_fetchedWhat = "";
        private int m_giveLimit;
        // Дело не начать (нечего отдавать, не понял, что взять) — итог без хождения
        private string m_immediateResult;

        public string Name => Humanoid != null ? Humanoid.m_name : Plugin.CompanionName.Value;
        public bool IsHunting => Time.time < m_huntUntil;
        public bool PrefersBow => Plugin.CombatStyle.Value == "bow";
        // По кому целиться из лука: на охоте — дичь, в бою — враг ванильного ИИ
        public Character AimTarget => CurrentMode == Mode.Hunt && m_prey != null && Ai.m_targetCreature == null ? m_prey : Ai.m_targetCreature;

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
            // Появился на базе, которую сторожил, — сторожит дальше
            if (CompanionManager.TakePendingGuard(out string guard))
            {
                BeginGuard(guard);
            }
            // Снаряжение выдаёт Humanoid.Start — стрелы досыпаем следом
            Invoke(nameof(TopUpKit), 0.5f);
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

        // Стрелы из снаряжения: досыпать до нормы при появлении и с каждым новым днём («настрогал за ночь»)
        private void TopUpKit()
        {
            Inventory inv = Humanoid.GetInventory();
            foreach (KeyValuePair<string, int> kit in CompanionPrefab.KitCounts)
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kit.Key) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null)
                {
                    continue;
                }
                int have = inv.CountItems(drop.m_itemData.m_shared.m_name);
                if (have < kit.Value)
                {
                    inv.AddItem(prefab, kit.Value - have);
                }
            }
            m_lastArrowDay = EnvMan.instance != null ? EnvMan.instance.GetDay() : -1;
        }

        // ---------- План ----------

        public void RunPlan(List<PlanStep> plan)
        {
            if (plan == null || plan.Count == 0)
            {
                return;
            }
            // «Стреляй из лука» посреди работы — не повод бросать работу
            if (plan.All(s => s.Do == "combat_style"))
            {
                foreach (PlanStep step in plan)
                {
                    Begin(step);
                }
                return;
            }
            m_plan.Clear();
            m_results.Clear();
            m_planHadTask = false;
            m_fetchedTotal = 0;
            Player player = Player.m_localPlayer;
            m_planOrigin = player != null ? player.transform.position : transform.position;
            foreach (PlanStep step in plan)
            {
                m_plan.Enqueue(step);
            }
            Plugin.Log.LogInfo($"План: {string.Join(", ", plan)}");
            StartNext();
        }

        public void CancelPlan()
        {
            m_plan.Clear();
            m_results.Clear();
            m_step = null;
        }

        // Занят делом: идёт задание или в очереди есть шаги
        public bool OnTask => m_plan.Count > 0 || (CurrentMode != Mode.Follow && CurrentMode != Mode.Stay && CurrentMode != Mode.Guard);

        public string DescribePlan()
        {
            return m_plan.Count > 0 ? PlanStep.Describe(m_plan) : "";
        }

        private void StartNext()
        {
            while (m_plan.Count > 0)
            {
                PlanStep step = m_plan.Dequeue();
                if (Begin(step))
                {
                    return;
                }
            }
            // План кончился: вернуться к игроку (если не велено сторожить) и рассказать, что сделано
            m_step = null;
            if (CurrentMode != Mode.Stay && CurrentMode != Mode.Guard)
            {
                SetMode(Mode.Follow);
            }
            // Только мгновенные шаги (запомнить место) — отчёт не нужен, он уже всё сказал
            if (!m_planHadTask)
            {
                m_results.Clear();
            }
            if (m_results.Count > 0)
            {
                var results = new List<string>(m_results);
                m_results.Clear();
                CompanionManager.OnPlanFinished(this, results);
            }
        }

        // true — началось дело, которое длится; false — шаг выполнен сразу
        private bool Begin(PlanStep step)
        {
            m_step = step;
            m_what = step.What ?? "";
            switch (step.Do)
            {
                case "follow":
                case "come":
                    SetMode(Mode.Follow);
                    return false;
                case "stay":
                    // «Наруби дров, а потом жди тут» — «тут» это где игрок отдал приказ
                    if (m_planHadTask)
                    {
                        m_stayPoint = m_planOrigin;
                        SetMode(Mode.Stay, keepPoint: true);
                    }
                    else
                    {
                        SetMode(Mode.Stay);
                    }
                    return false;
                case "attack":
                    SetMode(Mode.Follow);
                    Hunt(60f);
                    return false;
                case "combat_style":
                    Plugin.CombatStyle.Value = Catalog.Normalize(step.What).Contains("bow") || Catalog.Normalize(step.What).Contains("лук") ? "bow" : "melee";
                    m_nextEquip = 0f;
                    if (CurrentMode == Mode.Follow || CurrentMode == Mode.Stay)
                    {
                        EquipCombat();
                    }
                    return false;
                case "gather_wood":
                    m_planHadTask = true;
                    SetMode(Mode.Work, WorkKind.Wood, step.Count);
                    return Started(Mode.Work);
                case "mine":
                    m_planHadTask = true;
                    SetMode(Mode.Work, WorkKind.Stone, step.Count);
                    return Started(Mode.Work);
                case "pick":
                    m_planHadTask = true;
                    SetMode(Mode.Pick, goal: step.Count);
                    return Started(Mode.Pick);
                case "hunt":
                    m_planHadTask = true;
                    SetMode(Mode.Hunt, goal: step.Count);
                    return Started(Mode.Hunt);
                case "loot":
                    m_planHadTask = true;
                    SetMode(Mode.Loot);
                    return Started(Mode.Loot);
                case "give_items":
                    m_planHadTask = true;
                    m_giveLimit = step.Count > 0 ? step.Count
                        : m_fetchedTotal > 0 && Catalog.Normalize(step.What) == Catalog.Normalize(m_fetchedWhat) ? m_fetchedTotal : 0;
                    m_fetchedTotal = 0;
                    SetMode(Mode.Give);
                    return Started(Mode.Give);
                case "store":
                    m_planHadTask = true;
                    SetMode(Mode.Store);
                    return Started(Mode.Store);
                case "fetch":
                    m_planHadTask = true;
                    SetMode(Mode.Fetch, goal: step.Count);
                    return Started(Mode.Fetch);
                case "mark_place":
                    m_results.Add(MarkPlace(step));
                    return false;
                case "forget_place":
                    Place gone = Places.Forget(step.What);
                    m_results.Add(gone != null ? $"забыл место «{gone.name}»" : $"места «{step.What}» и не было");
                    return false;
                case "guard":
                    BeginGuard(step.What);
                    return false;
                case "repair":
                    m_planHadTask = true;
                    SetMode(Mode.Repair);
                    return Started(Mode.Repair);
                case "smelt":
                    m_planHadTask = true;
                    SetMode(Mode.Smelt, goal: step.Count);
                    return Started(Mode.Smelt);
                case "collect":
                    m_planHadTask = true;
                    SetMode(Mode.Collect);
                    return Started(Mode.Collect);
                default:
                    Plugin.Log.LogWarning($"Неизвестный шаг плана: {step.Do}");
                    return false;
            }
        }

        private bool Started(Mode mode)
        {
            if (m_immediateResult != null)
            {
                m_results.Add(m_immediateResult);
                m_immediateResult = null;
                SetMode(Mode.Follow);
                return false;
            }
            return CurrentMode == mode;
        }

        // Дело кончилось: записать итог и взяться за следующий шаг
        private void Finish(string result)
        {
            Debug($"итог шага {m_step}: {result}");
            if (!string.IsNullOrEmpty(result))
            {
                m_results.Add(result);
            }
            EndHuntDraw();
            if (m_chestOpenedByMe && m_chest != null)
            {
                m_chest.SetInUse(false);
            }
            m_chestOpenedByMe = false;
            CurrentMode = Mode.Follow;
            Ai.SetFollowTarget(Player.m_localPlayer != null ? Player.m_localPlayer.gameObject : null);
            StartNext();
        }

        // ---------- Режимы ----------

        public void SetMode(Mode mode, WorkKind work = WorkKind.Wood, int goal = 0, bool keepPoint = false)
        {
            Player player = Player.m_localPlayer;
            EndHuntDraw();
            if (mode != Mode.Guard)
            {
                m_guardPlace = null;
                m_guardFar = false;
                // Ушёл с охраны — база больше не ждёт его (при загрузке зоны режим ещё восстанавливается)
                if (!m_restoring && Places.Guard.Length > 0)
                {
                    Places.Guard = "";
                }
            }
            m_repairTarget = null;
            m_lootFilter = null;
            CurrentMode = mode;
            m_target = null;
            m_loot = null;
            m_looted = 0;
            m_lootSearchTimer = 0f;
            m_lootAroundCenter = false;
            m_stuckTimer = 0f;
            m_bestDist = float.MaxValue;
            m_pickable = null;
            m_prey = null;
            m_chest = null;
            m_chestPhaseAt = -1f;
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
                    if (!keepPoint)
                    {
                        m_stayPoint = Ai.GetPatrolPoint(out Vector3 saved) && m_restoring ? saved : transform.position;
                    }
                    Ai.SetPatrolPoint(m_stayPoint);
                    EquipCombat();
                    break;
                case Mode.Work:
                    BeginTask(240f);
                    CurrentWork = work;
                    m_goal = goal > 0 ? goal : WorkGoal;
                    m_rockFilter = work == WorkKind.Stone ? Catalog.RockFilter(m_what) : null;
                    m_startCount = CountGathered();
                    EquipTool();
                    break;
                case Mode.Pick:
                    BeginTask(180f);
                    m_goal = goal > 0 ? goal : 60;
                    m_pickFilter = Catalog.PickFilter(m_what);
                    m_pickNames.Clear();
                    break;
                case Mode.Hunt:
                    BeginTask(240f);
                    m_goal = goal > 0 ? goal : 2;
                    m_kills = 0;
                    m_escaped = 0;
                    m_huntRetries = 0;
                    m_killed.Clear();
                    m_shotsAtPrey = 0;
                    m_lootUntil = 0f;
                    break;
                case Mode.Loot:
                    BeginTask(60f);
                    m_lootCenter = m_workOrigin;
                    m_lootAroundCenter = true;
                    break;
                case Mode.Give:
                    BeginTask(40f);
                    m_itemFilter = Catalog.ItemFilter(m_what);
                    if (!GatheredItems(m_itemFilter, m_what).Any())
                    {
                        // Отдавать нечего — незачем идти
                        m_immediateResult = Catalog.IsAll(m_what) ? "отдавать было нечего: мешок пуст" : $"отдавать было нечего: «{m_what}» у тебя нет";
                    }
                    break;
                case Mode.Store:
                    BeginTask(60f);
                    m_itemFilter = Catalog.ItemFilter(m_what);
                    if (!GatheredItems(m_itemFilter, m_what).Any())
                    {
                        m_immediateResult = Catalog.IsAll(m_what) ? "складывать в сундук было нечего" : $"складывать в сундук было нечего: «{m_what}» у тебя нет";
                    }
                    break;
                case Mode.Fetch:
                    BeginTask(60f);
                    m_goal = goal;
                    m_itemFilter = Catalog.ItemFilter(m_what);
                    if (Catalog.IsAll(m_what))
                    {
                        m_immediateResult = "не понял, что взять из сундука";
                    }
                    break;
                case Mode.Guard:
                    Ai.SetFollowTarget(null);
                    Ai.ResetPatrolPoint();
                    EquipCombat();
                    break;
                case Mode.Repair:
                    BeginTask(300f);
                    StartRepair();
                    break;
                case Mode.Smelt:
                    BeginTask(180f);
                    m_goal = goal;
                    StartSmelt();
                    break;
                case Mode.Collect:
                    BeginTask(90f);
                    StartCollect();
                    break;
            }
        }

        private void BeginTask(float seconds)
        {
            Ai.SetFollowTarget(null);
            Ai.ResetPatrolPoint();
            m_taskTimeLeft = seconds;
            m_workOrigin = transform.position;
            m_snapshot = Snapshot();
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
                case Mode.Give: return "несёт добычу игроку";
                case Mode.Pick: return "собирает " + (m_what.Length > 0 ? m_what : "травы и ягоды");
                case Mode.Hunt: return "охотится" + (m_what.Length > 0 ? " на " + m_what : "") + (m_kills > 0 ? $" (добыто {m_kills})" : "");
                case Mode.Store: return "несёт добычу в сундук";
                case Mode.Fetch: return "идёт к сундуку за " + m_what;
                case Mode.Guard: return DescribeGuard();
                case Mode.Repair: return "чинит постройки" + (m_repaired > 0 ? $" (починил {m_repaired})" : "");
                case Mode.Smelt: return "загружает печи";
                case Mode.Collect: return "забирает готовое из печей";
                default: return InCombat() ? "бьётся с врагом" : "идёт следом";
            }
        }

        public string DescribeCarry()
        {
            var parts = new List<string>();
            foreach (var group in GatheredItems(null, "").GroupBy(i => i.m_shared.m_name))
            {
                parts.Add($"{Localization.instance.Localize(group.Key)} ×{group.Sum(i => i.m_stack)}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "";
        }

        public bool InCombat()
        {
            return Ai.m_targetCreature != null && !Ai.m_targetCreature.IsDead();
        }

        // ---------- Кадр ИИ (вызывается вместо MonsterAI.UpdateAI, когда врага нет) ----------

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
                case Mode.Pick:
                    UpdatePick(dt);
                    break;
                case Mode.Hunt:
                    UpdateHunt(dt);
                    break;
                case Mode.Store:
                case Mode.Fetch:
                    UpdateChest(dt, player);
                    break;
                case Mode.Guard:
                    UpdateGuard(dt, player);
                    break;
                case Mode.Repair:
                    UpdateRepair(dt, player);
                    break;
                case Mode.Smelt:
                    UpdateSmelt(dt, player);
                    break;
                case Mode.Collect:
                    UpdateCollect(dt, player);
                    break;
            }
        }

        // Раз в несколько секунд при любом режиме (из CompanionManager)
        public void SlowUpdate()
        {
            if (EnvMan.instance != null && m_lastArrowDay >= 0 && EnvMan.instance.GetDay() != m_lastArrowDay)
            {
                TopUpKit();
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
            if (m_taskTimeLeft <= 0f || gathered >= m_goal)
            {
                FinishGathering(CurrentWork == WorkKind.Wood ? "нарубил" : "добыл", "");
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
                    string none = CurrentWork == WorkKind.Wood ? "деревьев в округе больше нет" : "подходящих камней в округе больше нет";
                    FinishGathering(CurrentWork == WorkKind.Wood ? "нарубил" : "добыл", none);
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
                player.transform.position, Leash, m_skipTargets, m_rockFilter);
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

        // ---------- Сбор растений ----------

        private void UpdatePick(float dt)
        {
            m_taskTimeLeft -= dt;
            int picked = CountNames(m_pickNames);
            if (m_taskTimeLeft <= 0f || picked >= m_goal)
            {
                FinishGathering("собрал", "");
                return;
            }
            // Сорванное падает под ноги — подобрать
            if (StepLoot(dt, 5f) == LootState.Busy)
            {
                return;
            }
            if (Time.time - m_pickedAt < 0.5f)
            {
                Ai.StopMoving();
                return;
            }
            if (m_pickable == null || !m_pickable.CanBePicked())
            {
                m_searchTimer -= dt;
                if (m_searchTimer > 0f)
                {
                    Ai.StopMoving();
                    return;
                }
                m_searchTimer = 1f;
                Player player = Player.m_localPlayer;
                m_pickable = WorldScan.FindNearestPickable(transform.position, 50f, m_pickFilter, player.transform.position, Leash, m_skipTargets);
                m_targetSince = Time.time;
                m_bestDist = float.MaxValue;
                m_stuckTimer = 0f;
                if (m_pickable == null)
                {
                    if (FindLoot(8f) != null)
                    {
                        m_lootSearchTimer = 0f;
                        return;
                    }
                    FinishGathering("собрал", picked == 0 ? (m_what.Length > 0 ? $"{m_what} поблизости не нашлось" : "трав и ягод поблизости не нашлось") : "");
                    return;
                }
                if (Catalog.TryPick(m_pickable.m_nview.GetZDO().GetPrefab(), out Catalog.PickInfo info))
                {
                    m_pickNames.Add(info.ItemToken);
                }
                Debug($"рву {m_pickable.name} в {Vector3.Distance(m_pickable.transform.position, transform.position):0.0} м");
                return;
            }

            Vector3 pos = m_pickable.transform.position;
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
            // Куст широкий: рвать можно и с пары шагов, а если упёрся в ветки — с того места, где стоит
            if (dist > 1.8f && !(dist < 3f && m_stuckTimer > 1.5f))
            {
                Ai.MoveTo(dt, pos, 1f, dist > 8f);
                if (m_stuckTimer > 8f || Time.time - m_targetSince > 30f)
                {
                    SkipPickable();
                }
                return;
            }
            Ai.StopMoving();
            Ai.LookAt(pos + Vector3.up * 0.5f);
            Humanoid.m_zanim.SetTrigger("interact");
            m_pickable.Interact(Humanoid, false, false);
            m_pickedAt = Time.time;
            m_lootSearchTimer = 0f;
            SkipPickable();
        }

        private void SkipPickable()
        {
            ZDO zdo = m_pickable != null && m_pickable.m_nview != null ? m_pickable.m_nview.GetZDO() : null;
            if (zdo != null)
            {
                m_skipTargets.Add(zdo.m_uid);
            }
            m_pickable = null;
        }

        private int CountNames(HashSet<string> names)
        {
            int sum = 0;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (names.Contains(item.m_shared.m_name))
                {
                    sum += item.m_stack;
                }
            }
            int before = 0;
            foreach (string name in names)
            {
                before += m_snapshot.TryGetValue(name, out int n) ? n : 0;
            }
            return sum - before;
        }

        // ---------- Охота с луком ----------

        private void UpdateHunt(float dt)
        {
            m_taskTimeLeft -= dt;
            // Только что добыли зверя — подобрать мясо и шкуру
            if (Time.time < m_lootUntil)
            {
                EndHuntDraw();
                if (StepLoot(dt, 7f) == LootState.Busy)
                {
                    // Пока находит, что подобрать, — не торопится дальше
                    m_lootUntil = Mathf.Max(m_lootUntil, Time.time + 3f);
                    return;
                }
                if (Utils.DistanceXZ(m_lootCenter, transform.position) > 3f)
                {
                    Ai.MoveTo(dt, m_lootCenter, 1.5f, true);
                    return;
                }
                Ai.StopMoving();
                return;
            }
            m_lootAroundCenter = false;
            if (m_kills >= m_goal || m_taskTimeLeft <= 0f)
            {
                FinishHunt(m_kills >= m_goal ? "" : "время вышло");
                return;
            }

            if (m_prey != null && !m_prey.IsDead())
            {
                m_preyLastPos = m_prey.transform.position;
            }
            if (m_prey == null || m_prey.IsDead())
            {
                if (m_prey != null || m_shotsAtPrey > 0)
                {
                    // Был зверь — и пал
                    if (m_shotsAtPrey > 0 || m_prey != null)
                    {
                        m_kills++;
                        string name = m_prey != null ? Localization.instance.Localize(m_prey.m_name) : "зверь";
                        m_killed[name] = (m_killed.TryGetValue(name, out int k) ? k : 0) + 1;
                        Plugin.Log.LogInfo($"Охота: добыт {name} (выстрелов {m_shotsAtPrey})");
                        m_lootCenter = m_preyLastPos;
                        m_lootAroundCenter = true;
                        m_lootUntil = Time.time + 12f;
                        m_lootSearchTimer = 1.5f;   // туша падает не сразу
                    }
                    m_prey = null;
                    m_shotsAtPrey = 0;
                    return;
                }
                m_searchTimer -= dt;
                if (m_searchTimer > 0f)
                {
                    Ai.StopMoving();
                    return;
                }
                m_searchTimer = 1f;
                Player player = Player.m_localPlayer;
                // Ищет там же, где игрок видит зверей в сводке: в поводке от игрока, ближайшего к себе
                m_prey = WorldScan.FindNearestPrey(transform.position, Leash * 2f, Catalog.PreyFilter(m_what), player.transform.position, Leash, m_skipTargets);
                m_preySince = Time.time;
                m_bestDist = float.MaxValue;
                m_shotsAtPrey = 0;
                if (m_prey == null && m_skipTargets.Count > 0 && m_huntRetries < 2)
                {
                    // Упущенные звери могли успокоиться — ещё попытка
                    m_huntRetries++;
                    m_skipTargets.Clear();
                    m_searchTimer = 3f;
                    return;
                }
                if (m_prey == null)
                {
                    string none = m_escaped > 0 ? "звери убегали и не подпускали на выстрел"
                        : m_what.Length > 0 ? $"{m_what} поблизости не видно" : "дичи поблизости не видно";
                    FinishHunt(m_kills == 0 ? none : "больше дичи поблизости нет");
                    return;
                }
                Debug($"дичь: {m_prey.m_name} в {Vector3.Distance(m_prey.transform.position, transform.position):0} м");
                return;
            }

            ItemDrop.ItemData bow = FindBow();
            if (bow == null || ArrowCount() == 0)
            {
                // Лука или стрел нет — по-старому, бегом и топором
                if (Ai.m_targetCreature != m_prey)
                {
                    m_huntUntil = Time.time + 30f;
                    Ai.m_targetCreature = m_prey;
                    Ai.m_lastKnownTargetPos = m_prey.transform.position;
                    Ai.SetAlerted(true);
                    m_shotsAtPrey = Mathf.Max(m_shotsAtPrey, 1);
                }
                return;
            }

            Vector3 target = m_prey.GetCenterPoint();
            float dist = Vector3.Distance(target, transform.position);
            bool sees = dist <= ShootRange && LineOfSight(target);
            if (!sees)
            {
                EndHuntDraw();
                // Подкрадываемся шагом, издалека бежим; если зверь уходит — не гоняемся вечно
                Ai.MoveTo(dt, m_prey.transform.position, ShootRange * 0.6f, dist > 45f);
                if (dist < m_bestDist - 0.5f)
                {
                    m_bestDist = dist;
                    m_stuckTimer = 0f;
                }
                else
                {
                    m_stuckTimer += dt;
                }
                if (m_stuckTimer > 12f || Time.time - m_preySince > 60f || Vector3.Distance(m_prey.transform.position, Player.m_localPlayer.transform.position) > Leash)
                {
                    Debug($"упустил {m_prey.m_name}");
                    m_escaped++;
                    m_skipTargets.Add(m_prey.GetZDOID());
                    m_prey = null;
                    m_shotsAtPrey = 0;
                }
                return;
            }

            Ai.StopMoving();
            m_stuckTimer = 0f;
            if (Humanoid.m_leftItem != bow)
            {
                Humanoid.EquipItem(bow, false);
            }
            Ai.LookAt(target);
            if (Humanoid.InAttack() || Time.time < m_nextShot)
            {
                return;
            }
            // Натягиваем тетиву, как игрок: анимация прицеливания и сила выстрела растут со временем
            string drawState = bow.m_shared.m_attack.m_drawAnimationState;
            if (m_drawStart < 0f)
            {
                m_drawStart = Time.time;
            }
            float drawn = Time.time - m_drawStart;
            Humanoid.m_attackDrawTime = Mathf.Max(0.01f, drawn * 2.5f);   // ~1 с до полной силы
            if (!string.IsNullOrEmpty(drawState))
            {
                Humanoid.m_zanim.SetBool(drawState, true);
                Humanoid.m_zanim.SetFloat("drawpercent", Humanoid.GetAttackDrawPercentage());
            }
            if (drawn >= 1.0f && Ai.IsLookingAt(target, 8f))
            {
                if (Humanoid.StartAttack(null, false))
                {
                    m_shotsAtPrey++;
                    Debug($"выстрел #{m_shotsAtPrey} по {m_prey.m_name} с {dist:0} м");
                }
                EndHuntDraw();
                m_nextShot = Time.time + 0.6f;
                if (m_shotsAtPrey > 15)
                {
                    m_skipTargets.Add(m_prey.GetZDOID());
                    m_prey = null;
                    m_shotsAtPrey = 0;
                }
            }
        }

        private void EndHuntDraw()
        {
            if (m_drawStart < 0f || Humanoid == null)
            {
                return;
            }
            m_drawStart = -1f;
            Humanoid.m_attackDrawTime = 0f;
            ItemDrop.ItemData bow = FindBow();
            string drawState = bow != null ? bow.m_shared.m_attack.m_drawAnimationState : "";
            if (!string.IsNullOrEmpty(drawState))
            {
                Humanoid.m_zanim.SetBool(drawState, false);
            }
        }

        private static int s_losMask;

        private bool LineOfSight(Vector3 target)
        {
            if (s_losMask == 0)
            {
                s_losMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            }
            Vector3 eye = Humanoid.m_eye != null ? Humanoid.m_eye.position : transform.position + Vector3.up * 1.6f;
            return !Physics.Linecast(eye, target, s_losMask, QueryTriggerInteraction.Ignore);
        }

        private void FinishHunt(string note)
        {
            EndHuntDraw();
            string killed = m_killed.Count > 0 ? string.Join(", ", m_killed.Select(kv => $"{kv.Key} ×{kv.Value}")) : "";
            string gained = Gained();
            string result = killed.Length > 0 ? $"охота: добыл {killed}" : "охота: никого не добыл";
            if (gained.Length > 0)
            {
                result += $", подобрал: {gained}";
            }
            if (note.Length > 0)
            {
                result += $" ({note})";
            }
            if (FindBow() != null && ArrowCount() == 0)
            {
                result += "; стрелы кончились";
            }
            Finish(result);
        }

        // ---------- Сундук Бьёрна ----------

        private void UpdateChest(float dt, Player player)
        {
            m_taskTimeLeft -= dt;
            if (m_chest == null)
            {
                // Сундуков несколько — за вещами к тому, где они есть, а складывать — туда, где есть место
                System.Func<Container, bool> prefer = CurrentMode == Mode.Fetch
                    ? (System.Func<Container, bool>)(ch => ch.GetInventory().GetAllItems().Any(m_itemFilter))
                    : ch => ch.GetInventory().GetEmptySlots() > 0;
                m_chest = WorldScan.FindChest(transform.position, player.transform.position, 90f, prefer);
                m_bestDist = float.MaxValue;
                m_stuckTimer = 0f;
                if (m_chest == null)
                {
                    Finish(CurrentMode == Mode.Store
                        ? "сундука Бьёрна рядом нет — сложить не во что"
                        : "сундука Бьёрна рядом нет — взять неоткуда");
                    return;
                }
            }
            Vector3 pos = m_chest.transform.position;
            float dist = Utils.DistanceXZ(pos, transform.position);
            if (m_chestPhaseAt < 0f)
            {
                if (dist < m_bestDist - 0.25f)
                {
                    m_bestDist = dist;
                    m_stuckTimer = 0f;
                }
                else
                {
                    m_stuckTimer += dt;
                }
                if (dist > 2f && !(dist < 3.5f && m_stuckTimer > 2f))
                {
                    Ai.MoveTo(dt, pos, 1.2f, dist > 8f);
                    if (m_stuckTimer > 10f || m_taskTimeLeft <= 0f)
                    {
                        Finish("не смог дойти до сундука");
                    }
                    return;
                }
                // Дошёл: открыть крышку
                Ai.StopMoving();
                m_chestPhaseAt = Time.time;
                m_chestOpenedByMe = !m_chest.IsInUse();
                if (m_chestOpenedByMe)
                {
                    m_chest.SetInUse(true);
                }
                Humanoid.m_zanim.SetTrigger("interact");
            }
            Ai.StopMoving();
            Ai.LookAt(pos + Vector3.up * 0.5f);
            if (Time.time - m_chestPhaseAt < 0.8f)
            {
                return;
            }
            string result = CurrentMode == Mode.Store ? StoreItems() : FetchItems();
            Finish(result);
        }

        private string StoreItems()
        {
            Inventory mine = Humanoid.GetInventory();
            Inventory chest = m_chest.GetInventory();
            var stored = new Dictionary<string, int>();
            var left = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in GatheredItems(m_itemFilter, m_what).ToList())
            {
                string name = Localization.instance.Localize(item.m_shared.m_name);
                int stack = item.m_stack;
                mine.RemoveItem(item);
                bool all = chest.AddItem(item);
                int moved = all ? stack : stack - item.m_stack;
                if (!all)
                {
                    mine.AddItem(item);   // не влезло — остаётся при себе
                    left[name] = (left.TryGetValue(name, out int l) ? l : 0) + item.m_stack;
                }
                if (moved > 0)
                {
                    stored[name] = (stored.TryGetValue(name, out int n) ? n : 0) + moved;
                }
            }
            string text = stored.Count > 0 ? "сложил в сундук: " + Join(stored) : "в сундук ничего не влезло";
            if (left.Count > 0)
            {
                text += "; не влезло (сундук полон): " + Join(left);
            }
            return text;
        }

        private string FetchItems()
        {
            Inventory mine = Humanoid.GetInventory();
            Inventory chest = m_chest.GetInventory();
            int want = m_goal > 0 ? m_goal : int.MaxValue;
            var taken = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in chest.GetAllItems().Where(m_itemFilter).ToList())
            {
                if (want <= 0)
                {
                    break;
                }
                string name = Localization.instance.Localize(item.m_shared.m_name);
                int take = Mathf.Min(want, item.m_stack);
                ItemDrop.ItemData moving;
                if (take >= item.m_stack)
                {
                    chest.RemoveItem(item);
                    moving = item;
                }
                else
                {
                    moving = item.Clone();
                    moving.m_stack = take;
                    chest.RemoveItem(item, take);
                }
                if (!mine.AddItem(moving))
                {
                    take -= moving.m_stack;
                    chest.AddItem(moving);   // не унести — обратно
                }
                if (take > 0)
                {
                    taken[name] = (taken.TryGetValue(name, out int n) ? n : 0) + take;
                    want -= take;
                }
            }
            if (taken.Count == 0)
            {
                return $"в сундуке не нашлось «{m_what}»";
            }
            m_fetchedTotal = taken.Values.Sum();
            m_fetchedWhat = m_what;
            string text = "взял из сундука: " + Join(taken);
            if (m_goal > 0 && want > 0 && want != int.MaxValue)
            {
                text += $" (просили {m_goal}, больше не было)";
            }
            return text;
        }

        private static string Join(Dictionary<string, int> counts)
        {
            return string.Join(", ", counts.Select(kv => $"{kv.Key} ×{kv.Value}"));
        }

        // ---------- Сбор вещей с земли ----------

        private enum LootState { Busy, Waiting, Nothing }

        private void UpdateLoot(float dt)
        {
            m_taskTimeLeft -= dt;
            LootState state = StepLoot(dt, LootRadius);
            if (state == LootState.Nothing || m_taskTimeLeft <= 0f)
            {
                string gained = Gained();
                Finish(gained.Length > 0 ? "подобрал с земли: " + gained : "подбирать было нечего");
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
            Vector3 center = m_lootAroundCenter ? m_lootCenter : transform.position;
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
                if (m_skipLoot.Contains(drop.GetInstanceID()) || !drop.CanPickup() || drop.GetComponent<Fish>() != null
                    || (m_lootFilter != null && !m_lootFilter(drop)))
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
            Finish(summary.Length > 0 ? "отдал игроку: " + summary : "отдавать было нечего");
        }

        private string GiveAll(Player player)
        {
            Inventory mine = Humanoid.GetInventory();
            Inventory theirs = player.GetInventory();
            var given = new Dictionary<string, int>();
            int left = m_giveLimit > 0 ? m_giveLimit : int.MaxValue;
            foreach (ItemDrop.ItemData whole in GatheredItems(m_itemFilter, m_what).ToList())
            {
                if (left <= 0)
                {
                    break;
                }
                ItemDrop.ItemData item = whole;
                if (whole.m_stack > left)
                {
                    // Часть пачки остаётся у него
                    item = whole.Clone();
                    item.m_stack = left;
                    mine.RemoveItem(whole, left);
                }
                else
                {
                    mine.RemoveItem(whole);
                }
                int stack = item.m_stack;
                left -= stack;
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
            string text = Join(given);
            player.Message(MessageHud.MessageType.TopLeft, $"{Name} передал: {text}");
            return text;
        }

        // ---------- Снаряжение и добыча в мешке ----------

        // Добыча — всё, кроме снаряжения. Свои стрелы он отдаёт, только если просят именно стрелы, и оставляет себе колчан.
        private IEnumerable<ItemDrop.ItemData> GatheredItems(System.Func<ItemDrop.ItemData, bool> filter, string what)
        {
            bool arrowsAsked = !Catalog.IsAll(what) && filter != null;
            int keepArrows = CompanionPrefab.KitArrowCount;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems().ToList())
            {
                if (IsKit(item) || (filter != null && !filter(item)))
                {
                    continue;
                }
                if (IsArrow(item))
                {
                    if (!arrowsAsked || keepArrows >= item.m_stack)
                    {
                        keepArrows -= item.m_stack;
                        continue;
                    }
                    if (keepArrows > 0)
                    {
                        // Часть пачки остаётся в колчане
                        int give = item.m_stack - keepArrows;
                        keepArrows = 0;
                        ItemDrop.ItemData part = item.Clone();
                        part.m_stack = give;
                        Humanoid.GetInventory().RemoveItem(item, give);
                        Humanoid.GetInventory().AddItem(part);
                        yield return part;
                        continue;
                    }
                }
                yield return item;
            }
        }

        private bool IsKit(ItemDrop.ItemData item)
        {
            return Humanoid.IsItemEquiped(item) || item.m_shared.m_name == "$item_hammer"
                || (CompanionPrefab.IsKitItem(item) && (item.IsWeapon() || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield
                    || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow));
        }

        private static bool IsArrow(ItemDrop.ItemData item)
        {
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo && item.m_shared.m_ammoType == "$ammo_arrows";
        }

        // Вызывается каждый кадр работы — без LINQ, чтобы не мусорить в память
        private int CountGathered()
        {
            int sum = 0;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (!IsKit(item) && !IsArrow(item))
                {
                    sum += item.m_stack;
                }
            }
            return sum;
        }

        private Dictionary<string, int> Snapshot()
        {
            var counts = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                counts[item.m_shared.m_name] = (counts.TryGetValue(item.m_shared.m_name, out int n) ? n : 0) + item.m_stack;
            }
            return counts;
        }

        // Что прибавилось в мешке с начала дела: «Древесина ×42, Смола ×1»
        private string Gained()
        {
            var now = Snapshot();
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in now)
            {
                int before = m_snapshot.TryGetValue(kv.Key, out int n) ? n : 0;
                if (kv.Value > before)
                {
                    parts.Add($"{Localization.instance.Localize(kv.Key)} ×{kv.Value - before}");
                }
            }
            return string.Join(", ", parts);
        }

        private void FinishGathering(string verb, string note)
        {
            string gained = Gained();
            string result = gained.Length > 0 ? $"{verb}: {gained}" : $"{verb}: ничего";
            if (note.Length > 0)
            {
                result += $" ({note})";
            }
            Finish(result);
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

        public ItemDrop.ItemData FindBow()
        {
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow && item.m_shared.m_ammoType == "$ammo_arrows")
                {
                    PrepareBow(item);
                    return item;
                }
            }
            return null;
        }

        public int ArrowCount()
        {
            int sum = 0;
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (IsArrow(item))
                {
                    sum += item.m_stack;
                }
            }
            return sum;
        }

        // Ванильный ИИ стреляет, только подойдя на m_aiAttackRange (у луков игрока это 2 м).
        // Лучу Бьёрна даём свою копию общих данных с дальностью лучника — луки игрока не меняются.
        private static readonly HashSet<ItemDrop.ItemData.SharedData> s_tunedShared = new HashSet<ItemDrop.ItemData.SharedData>();

        private static void PrepareBow(ItemDrop.ItemData bow)
        {
            if (s_tunedShared.Contains(bow.m_shared))
            {
                return;
            }
            var copy = (ItemDrop.ItemData.SharedData)typeof(object)
                .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(bow.m_shared, null);
            copy.m_aiAttackRange = 24f;
            copy.m_aiAttackRangeMin = 0f;
            copy.m_aiAttackInterval = 1.6f;
            copy.m_aiAttackMaxAngle = 6f;
            bow.m_shared = copy;
            s_tunedShared.Add(copy);
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

        // В бою: топор со щитом; если велено биться луком — лук, пока враг не подошёл вплотную
        public void EquipCombat(Character target = null)
        {
            if (PrefersBow && (target == null || Vector3.Distance(target.transform.position, transform.position) > 7f))
            {
                ItemDrop.ItemData bow = FindBow();
                if (bow != null && ArrowCount() > 0)
                {
                    if (Humanoid.m_leftItem != bow)
                    {
                        Humanoid.EquipItem(bow, false);
                    }
                    return;
                }
            }
            ItemDrop.ItemData axe = FindTool(Skills.SkillType.Axes);
            if (axe != null && Humanoid.m_rightItem != axe)
            {
                Humanoid.EquipItem(axe, false);
            }
            ItemDrop.ItemData shield = FindShield();
            if (shield != null && Humanoid.m_leftItem != shield && (Humanoid.m_rightItem == null || !Humanoid.m_rightItem.IsTwoHanded()))
            {
                Humanoid.EquipItem(shield, false);
            }
        }

        private void EquipTool()
        {
            if (CurrentWork == WorkKind.Wood)
            {
                ItemDrop.ItemData axe = FindTool(Skills.SkillType.Axes);
                if (axe != null && Humanoid.m_rightItem != axe)
                {
                    Humanoid.EquipItem(axe, false);
                }
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
                if (c == Humanoid || c.IsDead() || c.IsTamed() || !BaseAI.IsEnemy(Humanoid, c))
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

        // Звери не враги, пока сами не нападают; монстры — всегда.
        // Олени, кабаны и прочая дичь числятся в игре «лесными монстрами» — узнаём их по виду.
        public bool IsThreat(Character c)
        {
            bool prey = c.GetFaction() == Character.Faction.AnimalsVeg || WorldScan.IsPrey(c, out _);
            if (!prey)
            {
                return !c.IsTamed();
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
            string doing = CurrentMode != Mode.Follow && CurrentMode != Mode.Stay ? $"\n<color=#aaaaaa>{DescribeMode()}</color>" : "";
            return Localization.instance.Localize(
                $"{Name}{doing}\n[<color=yellow><b>$KEY_Use</b></color>] {order}\n<color=#aaaaaa>Говори с ним в чате или голосом ({Plugin.VoiceKey.Value})</color>");
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold)
            {
                return false;
            }
            CancelPlan();
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
            EndHuntDraw();
            CancelPlan();
            // Добыча остаётся на месте гибели, снаряжение — нет
            foreach (ItemDrop.ItemData item in GatheredItems(null, "").ToList())
            {
                ItemDrop.DropItem(item, item.m_stack, transform.position + Vector3.up, Quaternion.identity);
            }
            Humanoid.GetInventory().RemoveAll();
            CompanionManager.OnCompanionDied(this);
        }
    }
}
