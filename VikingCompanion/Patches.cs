using HarmonyLib;
using UnityEngine;

namespace VikingCompanion
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ZNetScene_Awake_Patch
    {
        private static void Postfix(ZNetScene __instance)
        {
            CompanionPrefab.Register(__instance);
            BjornChest.Register(__instance);
        }
    }

    // В задачах (сторожить, рубить, собирать, отдавать) движением управляет Companion.
    // Ванильный ИИ остаётся только для поиска врагов и боя.
    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    internal static class MonsterAI_UpdateAI_Patch
    {
        private static bool Prefix(MonsterAI __instance, float dt, ref bool __result)
        {
            Companion c = Companion.Local;
            if (c == null || c.Ai != __instance || !c.ControlsMovement)
            {
                return true;
            }
            if (!__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner() || __instance.m_targetCreature != null)
            {
                return true;
            }
            // То, что делает BaseAI.UpdateAI, плюс поиск врагов
            __instance.UpdateRegeneration(dt);
            __instance.m_timeSinceHurt += dt;
            __instance.UpdateTarget(c.Humanoid, dt, out _, out _);
            if (__instance.m_targetCreature != null)
            {
                return true;
            }
            c.UpdateTask(dt);
            __result = true;
            return false;
        }
    }

    // Не гоняется за зверьём и не убегает далеко от игрока
    [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
    internal static class MonsterAI_UpdateTarget_Patch
    {
        private const float Leash = 35f;

        private static void Postfix(MonsterAI __instance, ref bool canHearTarget, ref bool canSeeTarget)
        {
            Companion c = Companion.Local;
            if (c == null || c.Ai != __instance)
            {
                return;
            }
            __instance.m_targetStatic = null;
            Character target = __instance.m_targetCreature;
            Player player = Player.m_localPlayer;
            if (target == null || player == null)
            {
                return;
            }
            bool wanted = c.IsHunting || c.IsThreat(target);
            bool leashed = c.Guarding
                ? Utils.DistanceXZ(target.transform.position, c.GuardCenter) > c.GuardRadius + 25f
                : Vector3.Distance(target.transform.position, player.transform.position) > Leash && c.CurrentMode != Companion.Mode.Stay;
            // В опасном месте («там тролль») сам драку не начинает — только если тварь уже идёт на них
            if (wanted && !c.IsHunting && Places.DangerAt(target.transform.position) != null)
            {
                BaseAI ai = target.GetBaseAI();
                Character theirs = ai != null ? ai.GetTargetCreature() : null;
                wanted = theirs != null && (theirs == c.Humanoid || theirs.IsPlayer());
            }
            if (!wanted || leashed)
            {
                __instance.m_targetCreature = null;
                canHearTarget = false;
                canSeeTarget = false;
            }
        }
    }

    // В бою топор со щитом (не кирка), а если велено — лук, пока враг не подошёл вплотную
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon))]
    internal static class Humanoid_EquipBestWeapon_Patch
    {
        private static bool Prefix(Humanoid __instance, Character targetCreature)
        {
            // Мобы зовут это часто — сравниваем ссылку, без поиска компонента
            Companion c = Companion.Local;
            if (c != null && c.Humanoid == __instance)
            {
                c.EquipCombat(targetCreature);
                return false;
            }
            return true;
        }
    }

    // Лук у ИИ не натягивается (это делает только игрок) — у Бьёрна тетива всегда натянута до конца
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetAttackDrawPercentage))]
    internal static class Humanoid_GetAttackDrawPercentage_Patch
    {
        private static void Postfix(Humanoid __instance, ref float __result)
        {
            Companion c = Companion.Local;
            if (c != null && c.Humanoid == __instance)
            {
                ItemDrop.ItemData weapon = __instance.GetCurrentWeapon();
                if (weapon != null && weapon.m_shared.m_attack.m_bowDraw)
                {
                    __result = 1f;
                }
            }
        }
    }

    // Стрела летит по дуге (тяжесть стрелы) — целимся с поправкой на падение и на бег зверя
    [HarmonyPatch(typeof(Attack), nameof(Attack.GetProjectileSpawnPoint))]
    internal static class Attack_GetProjectileSpawnPoint_Patch
    {
        private static readonly System.Collections.Generic.Dictionary<GameObject, float> s_gravity = new System.Collections.Generic.Dictionary<GameObject, float>();

        private static void Postfix(Attack __instance, ref Vector3 spawnPoint, ref Vector3 aimDir)
        {
            Companion c = Companion.Local;
            if (c == null || __instance.m_character != c.Humanoid || __instance.m_weapon == null || !__instance.m_bowDraw)
            {
                return;
            }
            Character target = c.AimTarget;
            if (target == null || target.IsDead())
            {
                return;
            }
            ItemDrop.ItemData ammo = __instance.m_ammoItem ?? __instance.m_lastUsedAmmo;
            float speed = __instance.m_projectileVel + (ammo != null ? ammo.m_shared.m_attack.m_projectileVel : 0f);
            GameObject projectile = ammo != null && ammo.m_shared.m_attack.m_attackProjectile != null
                ? ammo.m_shared.m_attack.m_attackProjectile : __instance.m_attackProjectile;
            aimDir = Ballistics.Aim(spawnPoint, target.GetCenterPoint(), target.GetVelocity(), speed, Gravity(projectile));
        }

        private static float Gravity(GameObject projectile)
        {
            if (projectile == null)
            {
                return 5f;
            }
            if (!s_gravity.TryGetValue(projectile, out float g))
            {
                Projectile p = projectile.GetComponent<Projectile>();
                s_gravity[projectile] = g = p != null ? p.m_gravity : 5f;
            }
            return g;
        }
    }

    internal static class Ballistics
    {
        // Угол вылета, чтобы попасть в точку с упреждением на скорость цели (две итерации хватает)
        public static Vector3 Aim(Vector3 from, Vector3 target, Vector3 targetVelocity, float speed, float gravity)
        {
            Vector3 point = target;
            float angle = 0f;
            Vector3 flat = Vector3.forward;
            for (int i = 0; i < 3; i++)
            {
                Vector3 d = point - from;
                flat = new Vector3(d.x, 0f, d.z);
                float x = flat.magnitude;
                if (x < 0.5f || speed < 1f)
                {
                    return d.normalized;
                }
                flat /= x;
                float v2 = speed * speed;
                float disc = v2 * v2 - gravity * (gravity * x * x + 2f * d.y * v2);
                angle = disc < 0f ? Mathf.PI / 4f : Mathf.Atan((v2 - Mathf.Sqrt(disc)) / (gravity * x));
                float time = x / (speed * Mathf.Cos(angle));
                Vector3 velocity = targetVelocity;
                velocity.y = 0f;
                point = target + velocity * time;
            }
            return (flat * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)).normalized;
        }
    }

    // ---------- События для памяти ----------

    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class Character_OnDeath_Patch
    {
        private static void Prefix(Character __instance)
        {
            if (__instance.m_boss && !__instance.IsPlayer() && __instance.m_nview != null && __instance.m_nview.IsOwner())
            {
                CompanionManager.OnBossKilled(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class Player_OnDeath_Patch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                CompanionManager.OnPlayerDied(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    internal static class Player_PlacePiece_Patch
    {
        private static void Postfix(Player __instance, Piece piece)
        {
            if (__instance == Player.m_localPlayer && piece != null)
            {
                CompanionManager.OnBuilt(Localization.instance.Localize(piece.m_name));
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
    internal static class InventoryGui_DoCrafting_Patch
    {
        private static void Prefix(InventoryGui __instance, Player player, out (Recipe recipe, int count, int quality) __state)
        {
            Recipe recipe = __instance.m_craftRecipe;
            ItemDrop.ItemData upgrade = __instance.m_craftUpgradeItem;
            string name = recipe != null && recipe.m_item != null ? recipe.m_item.m_itemData.m_shared.m_name : "";
            __state = (recipe, name.Length > 0 ? player.GetInventory().CountItems(name) : 0, upgrade != null ? upgrade.m_quality : -1);
        }

        private static void Postfix(InventoryGui __instance, Player player, (Recipe recipe, int count, int quality) __state)
        {
            Recipe recipe = __state.recipe;
            if (recipe == null || recipe.m_item == null || player != Player.m_localPlayer)
            {
                return;
            }
            string token = recipe.m_item.m_itemData.m_shared.m_name;
            string name = Localization.instance.Localize(token);
            ItemDrop.ItemData upgrade = __instance.m_craftUpgradeItem;
            if (__state.quality >= 0 && upgrade != null && upgrade.m_quality > __state.quality)
            {
                CompanionManager.OnCrafted($"{name} (улучшил до {upgrade.m_quality})");
            }
            else if (__state.quality < 0 && player.GetInventory().CountItems(token) > __state.count)
            {
                CompanionManager.OnCrafted(name);
            }
        }
    }

    // Вкладка «Бьёрн» в настройках игры (и в главном меню, и в игре по Esc)
    [HarmonyPatch(typeof(Settings), "Awake")]
    internal static class Settings_Awake_Patch
    {
        private static void Prefix(Settings __instance)
        {
            try
            {
                SettingsTab.Inject(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Вкладка настроек не добавилась: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
    internal static class Character_GetHoverText_Patch
    {
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance.TryGetComponent(out Companion c))
            {
                __result = c.GetHoverText();
            }
        }
    }

    // Всё, что игрок пишет в чат, слышит компаньон
    [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
    internal static class Chat_SendText_Patch
    {
        private static void Postfix(Talker.Type type, string text)
        {
            if (type == Talker.Type.Normal || type == Talker.Type.Shout || type == Talker.Type.Whisper)
            {
                CompanionManager.OnPlayerChat(text);
            }
        }
    }

    // R по умолчанию — «Убрать оружие»; пока голос на R, это действие молчит
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
    internal static class ZInput_GetButtonDown_Patch
    {
        private static bool Prefix(string name, ref bool __result)
        {
            if (name == "Hide" && Plugin.VoiceEnabled.Value && Plugin.SuppressHide.Value && Plugin.VoiceKey.Value == KeyCode.R)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
