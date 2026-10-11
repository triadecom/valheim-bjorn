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
            bool leashed = Vector3.Distance(target.transform.position, player.transform.position) > Leash
                && c.CurrentMode != Companion.Mode.Stay;
            if (!wanted || leashed)
            {
                __instance.m_targetCreature = null;
                canHearTarget = false;
                canSeeTarget = false;
            }
        }
    }

    // В бою всегда топор со щитом, а не кирка
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon))]
    internal static class Humanoid_EquipBestWeapon_Patch
    {
        private static bool Prefix(Humanoid __instance)
        {
            // Мобы зовут это часто — сравниваем ссылку, без поиска компонента
            Companion c = Companion.Local;
            if (c != null && c.Humanoid == __instance)
            {
                c.EquipCombat();
                return false;
            }
            return true;
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
