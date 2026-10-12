using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Ремонт построек молотком: «почини всё на базе». На охране базы чинит сам, что сломали.
    // Как у игрока: нужен молот, а для построек, которые требуют верстак, — верстак рядом.
    public partial class Companion
    {
        private const float RepairBelow = 0.999f;

        private WearNTear m_repairTarget;
        private Vector3 m_repairCenter;
        private float m_repairRadius;
        private float m_repairSince;
        private float m_repairSwingAt = -1f;
        private float m_nextRepairScan;
        private int m_repaired;
        private int m_noStation;
        private readonly HashSet<int> m_skipRepair = new HashSet<int>();

        private void StartRepair()
        {
            Player player = Player.m_localPlayer;
            Vector3 playerPos = player != null ? player.transform.position : transform.position;
            m_repaired = 0;
            m_noStation = 0;
            m_skipRepair.Clear();
            m_repairSwingAt = -1f;
            Place place = m_what.Length > 0 ? Places.Find(m_what) : Places.At(playerPos);
            if (place == null && m_what.Length > 0 && Places.GuessKind(m_what) == Places.Home)
            {
                m_immediateResult = "где база, ещё не знаешь — пусть игрок покажет её: «запомни, тут база»";
                return;
            }
            m_repairCenter = place != null ? place.Position : playerPos;
            m_repairRadius = place != null ? Mathf.Max(place.radius, 25f) : 30f;
            float far = Utils.DistanceXZ(playerPos, m_repairCenter);
            if (far > 80f)
            {
                m_immediateResult = $"до «{place.name}» далеко ({far:0} м) — чинить там можно, когда будете рядом";
                return;
            }
            if (Hammer() == null)
            {
                m_immediateResult = "нет молота — чинить нечем";
            }
        }

        // Молот всегда при нём: если потерялся — сколотит новый
        private ItemDrop.ItemData Hammer()
        {
            foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_name == "$item_hammer")
                {
                    return item;
                }
            }
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            if (prefab == null || !Humanoid.GetInventory().AddItem(prefab, 1))
            {
                return null;
            }
            return Humanoid.GetInventory().GetAllItems().FirstOrDefault(i => i.m_shared.m_name == "$item_hammer");
        }

        private bool NeedsRepair(WearNTear wnt)
        {
            if (wnt == null || !wnt.m_nview.IsValid() || wnt.GetHealthPercentage() >= RepairBelow || m_skipRepair.Contains(wnt.GetInstanceID()))
            {
                return false;
            }
            Piece piece = wnt.GetComponent<Piece>();
            return piece != null && piece.IsPlacedByPlayer();
        }

        private bool StationOk(WearNTear wnt)
        {
            Piece piece = wnt.GetComponent<Piece>();
            return piece.m_craftingStation == null || CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, wnt.transform.position) != null;
        }

        private WearNTear FindDamaged(Vector3 center, float radius)
        {
            WearNTear best = null;
            float bestDist = float.MaxValue;
            foreach (WearNTear wnt in WearNTear.s_allInstances)
            {
                if (wnt == null || Utils.DistanceXZ(wnt.transform.position, center) > radius || !NeedsRepair(wnt))
                {
                    continue;
                }
                if (!StationOk(wnt))
                {
                    m_noStation++;
                    m_skipRepair.Add(wnt.GetInstanceID());
                    continue;
                }
                float dist = Vector3.Distance(wnt.transform.position, transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = wnt;
                }
            }
            return best;
        }

        private void UpdateRepair(float dt, Player player)
        {
            m_taskTimeLeft -= dt;
            if (m_taskTimeLeft <= 0f)
            {
                FinishRepair("время вышло");
                return;
            }
            if (m_repairTarget == null || !NeedsRepair(m_repairTarget))
            {
                m_repairTarget = FindDamaged(m_repairCenter, m_repairRadius);
                ResetRepairApproach();
                if (m_repairTarget == null)
                {
                    FinishRepair("");
                    return;
                }
            }
            StepRepair(dt);
        }

        // На охране: раз в несколько секунд смотрит, не сломали ли чего
        private bool StepGuardRepair(float dt)
        {
            if (m_repairTarget != null && NeedsRepair(m_repairTarget))
            {
                StepRepair(dt);
                return true;
            }
            if (m_repairTarget != null)
            {
                m_repairTarget = null;
                EquipCombat();
            }
            if (Time.time < m_nextRepairScan || InCombat())
            {
                return false;
            }
            m_nextRepairScan = Time.time + 8f;
            m_repairTarget = FindDamaged(m_guardCenter, m_guardRadius);
            ResetRepairApproach();
            if (m_repairTarget != null && Hammer() == null)
            {
                m_repairTarget = null;
            }
            return m_repairTarget != null;
        }

        private void ResetRepairApproach()
        {
            m_repairSince = Time.time;
            m_repairSwingAt = -1f;
            m_bestDist = float.MaxValue;
            m_stuckTimer = 0f;
        }

        private void StepRepair(float dt)
        {
            WearNTear wnt = m_repairTarget;
            Vector3 pos = wnt.transform.position;
            Vector3 aim = pos;
            Collider col = wnt.GetComponentInChildren<Collider>();
            if (col != null)
            {
                aim = col.ClosestPoint(Humanoid.GetCenterPoint());
            }
            float flat = Utils.DistanceXZ(aim, transform.position);
            float height = Mathf.Abs(aim.y - transform.position.y);
            // Молотом достаёт метров на пять, как игрок; к высокой стене подходит вплотную
            bool reach = flat < 2.5f && height < 5f || Vector3.Distance(aim, Humanoid.GetCenterPoint()) < 3.5f;
            if (!reach)
            {
                if (flat < m_bestDist - 0.25f)
                {
                    m_bestDist = flat;
                    m_stuckTimer = 0f;
                }
                else
                {
                    m_stuckTimer += dt;
                }
                Ai.MoveTo(dt, aim, 1f, flat > 8f);
                if (m_stuckTimer > 6f || Time.time - m_repairSince > 25f)
                {
                    Debug($"не дотянуться до {wnt.name}");
                    m_skipRepair.Add(wnt.GetInstanceID());
                    m_repairTarget = null;
                }
                return;
            }
            Ai.StopMoving();
            Ai.LookAt(aim);
            ItemDrop.ItemData hammer = Hammer();
            if (hammer == null)
            {
                m_repairTarget = null;
                return;
            }
            if (Humanoid.m_rightItem != hammer)
            {
                Humanoid.EquipItem(hammer, false);
            }
            if (m_repairSwingAt < 0f)
            {
                // Замах молотом, а починка — когда он опустится
                Humanoid.m_zanim.SetTrigger(hammer.m_shared.m_attack.m_attackAnimation);
                m_repairSwingAt = Time.time;
                return;
            }
            if (Time.time - m_repairSwingAt < 0.45f)
            {
                return;
            }
            m_repairSwingAt = -1f;
            if (wnt.Repair())
            {
                m_repaired++;
                Piece piece = wnt.GetComponent<Piece>();
                piece.m_placeEffect.Create(pos, wnt.transform.rotation, null, 1f);
                Debug($"починил {wnt.name}");
            }
            m_repairTarget = null;
            m_repairSince = Time.time;
        }

        private void FinishRepair(string note)
        {
            string result = m_repaired > 0 ? $"починил построек: {m_repaired}" : "чинить было нечего — всё цело";
            if (m_noStation > 0)
            {
                result += $"; ещё {m_noStation} не починить без верстака рядом";
            }
            if (note.Length > 0)
            {
                result += $" ({note})";
            }
            Finish(result);
        }
    }
}
