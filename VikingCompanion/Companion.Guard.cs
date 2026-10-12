using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VikingCompanion
{
    // Места и охрана: «запомни, тут база», «там тролль, не суйся», «иди на базу и сторожи».
    public partial class Companion
    {
        private const string GuardKey = "vc_guard";

        private Place m_guardPlace;
        private Vector3 m_guardCenter;
        private float m_guardRadius = 10f;
        private bool m_guardFar;
        private float m_guardSince;
        private Vector3 m_wander;
        private float m_nextWander;
        private List<Vector3> m_guardSpots = new List<Vector3>();

        // Для патча выбора врагов: на охране держится своего места, а не игрока
        public bool Guarding => CurrentMode == Mode.Guard && !m_guardFar;
        public Vector3 GuardCenter => m_guardCenter;
        public float GuardRadius => m_guardRadius;

        // ---------- Запомнить место ----------

        private string MarkPlace(PlanStep step)
        {
            Player player = Player.m_localPlayer;
            string kind = Places.Kinds.Contains(step.Kind) ? step.Kind : Places.GuessKind(step.What + " " + step.Note);
            string name = step.What.Length > 0 ? step.What : kind == Places.Home ? "База" : "Место";
            Vector3 pos = player != null ? player.transform.position : transform.position;
            // «Там тролль»: опасное место — где эта тварь, если её видно, иначе куда смотрит игрок
            if (kind == Places.Danger && player != null)
            {
                Character beast = FindNamedCreature(step.What + " " + step.Note, player.transform.position, 120f);
                if (beast != null)
                {
                    pos = beast.transform.position;
                }
                else if (LookPoint(out Vector3 look))
                {
                    pos = look;
                }
            }
            string about = WorldScan.Surroundings(pos, Humanoid);
            Place place = Places.Mark(name, kind, pos, step.Note, about);
            string where = player != null && Utils.DistanceXZ(player.transform.position, pos) > 10f
                ? $" в {Utils.DistanceXZ(player.transform.position, pos):0} м {WorldScan.Compass(player.transform.position, pos)}" : "";
            return $"запомнил место «{place.name}» ({Places.KindName(place.kind)}){where}" + (about.Length > 0 ? $": {about}" : "");
        }

        private Character FindNamedCreature(string text, Vector3 near, float range)
        {
            Character best = null;
            float bestDist = range;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c.IsPlayer() || c == Humanoid || c.IsTamed())
                {
                    continue;
                }
                float dist = Vector3.Distance(near, c.transform.position);
                if (dist < bestDist && Catalog.NameMatches(Localization.instance.Localize(c.m_name), text))
                {
                    bestDist = dist;
                    best = c;
                }
            }
            return best;
        }

        // Куда смотрит игрок (камера), если это дальше пары шагов
        private static bool LookPoint(out Vector3 point)
        {
            point = Vector3.zero;
            if (GameCamera.instance == null)
            {
                return false;
            }
            Transform cam = GameCamera.instance.transform;
            int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            if (Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 150f, mask) && hit.distance > 8f)
            {
                point = hit.point;
                return true;
            }
            return false;
        }

        // ---------- Сторожить ----------

        private void BeginGuard(string what)
        {
            Player player = Player.m_localPlayer;
            Vector3 playerPos = player != null ? player.transform.position : transform.position;
            Place place = what.Length > 0 ? Places.Find(what) : Places.At(playerPos);
            if (place == null && what.Length > 0)
            {
                // Не знает такого места — сторожит, где стоит, и говорит об этом
                m_planHadTask = true;
                m_results.Add(Places.GuessKind(what) == Places.Home
                    ? "где база, ещё не знаешь — пусть игрок покажет её: «запомни, тут база»; пока сторожишь, где стоишь"
                    : $"места «{what}» не знаешь; пока сторожишь, где стоишь");
                SetMode(Mode.Stay);
                return;
            }
            SetMode(Mode.Guard);
            m_guardPlace = place;
            m_guardCenter = place != null ? place.Position : m_planHadTask ? m_planOrigin : playerPos;
            m_guardRadius = place != null ? Mathf.Clamp(place.radius, 8f, 30f) : 10f;
            m_guardSince = Time.time;
            m_guardFar = Utils.DistanceXZ(playerPos, m_guardCenter) > 70f;
            m_nextWander = Time.time + 2f;
            m_nextRepairScan = Time.time + 3f;
            m_guardSpots = GuardSpots(m_guardCenter, m_guardRadius);
            Places.Guard = place != null ? place.name : "";
            if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner())
            {
                m_nview.GetZDO().Set(GuardKey, Places.Guard);
            }
            Debug($"сторожит {(place != null ? place.name : "место")} r={m_guardRadius:0}, далеко={m_guardFar}");
        }

        private string DescribeGuard()
        {
            string name = m_guardPlace != null ? $"«{m_guardPlace.name}»" : "место";
            if (m_guardFar)
            {
                return "уходит сторожить " + name;
            }
            return m_repairTarget != null ? $"чинит постройки ({name})" : "сторожит " + name;
        }

        // Куда ходить на базе: к очагу, верстаку, сундукам, кровати
        private static List<Vector3> GuardSpots(Vector3 center, float radius)
        {
            var spots = new List<Vector3> { center };
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(center, radius, pieces);
            foreach (Piece piece in pieces)
            {
                if (piece != null && (piece.GetComponent<Fireplace>() != null || piece.GetComponent<CraftingStation>() != null
                    || piece.GetComponent<Container>() != null || piece.GetComponent<Bed>() != null))
                {
                    spots.Add(piece.transform.position);
                }
            }
            return spots.Take(12).ToList();
        }

        private void UpdateGuard(float dt, Player player)
        {
            if (m_guardFar)
            {
                // База далеко: уходит туда сам, а появится там, когда игрок придёт
                Ai.MoveTo(dt, m_guardCenter, 2f, true);
                if (Utils.DistanceXZ(transform.position, m_guardCenter) < m_guardRadius)
                {
                    m_guardFar = false;
                }
                else if (Vector3.Distance(player.transform.position, transform.position) > 40f || Time.time - m_guardSince > 15f)
                {
                    CompanionManager.ParkAtGuard(this);
                }
                return;
            }
            if (Time.time >= m_nextEquip && m_repairTarget == null)
            {
                m_nextEquip = Time.time + 1f;
                EquipCombat();
            }
            float fromCenter = Utils.DistanceXZ(m_guardCenter, transform.position);
            if (fromCenter > m_guardRadius + 2f)
            {
                Ai.MoveTo(dt, m_guardCenter, 2f, fromCenter > 10f);
                return;
            }
            // Сломали стену — чинит
            if (StepGuardRepair(dt))
            {
                return;
            }
            if (Time.time >= m_nextWander)
            {
                Vector3 spot = m_guardSpots[Random.Range(0, m_guardSpots.Count)];
                Vector2 off = Random.insideUnitCircle * 3f;
                m_wander = spot + new Vector3(off.x, 0f, off.y);
                m_nextWander = Time.time + Random.Range(10f, 22f);
                m_bestDist = float.MaxValue;
                m_stuckTimer = 0f;
            }
            float dist = Utils.DistanceXZ(m_wander, transform.position);
            if (dist > 1.5f && m_stuckTimer < 6f)
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
                Ai.MoveTo(dt, m_wander, 1f, false);
                return;
            }
            Ai.StopMoving();
            if (Vector3.Distance(player.transform.position, transform.position) < 12f)
            {
                Ai.LookAt(player.GetHeadPoint());
            }
        }
    }
}
