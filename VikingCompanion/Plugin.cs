using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace VikingCompanion
{
    [BepInPlugin(Guid, ModName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "varg.valheim.vikingcompanion";
        public const string ModName = "VikingCompanion";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> CompanionName;
        internal static ConfigEntry<float> MaxHealth;
        internal static ConfigEntry<string> Kit;
        internal static ConfigEntry<string> Hair;
        internal static ConfigEntry<string> Beard;
        internal static ConfigEntry<string> HairColor;
        internal static ConfigEntry<float> RespawnSeconds;
        internal static ConfigEntry<string> BridgeUrl;
        internal static ConfigEntry<bool> BridgeAutoStart;
        internal static ConfigEntry<string> BridgePython;
        internal static ConfigEntry<string> BridgeScript;
        internal static ConfigEntry<string> Chattiness;
        internal static ConfigEntry<string> Role;
        internal static ConfigEntry<string> RoleCustom;
        internal static ConfigEntry<string> ReplyLength;
        internal static ConfigEntry<int> Humor;
        internal static ConfigEntry<int> Rudeness;
        internal static ConfigEntry<string> Thinking;
        internal static ConfigEntry<bool> Learn;
        internal static ConfigEntry<string> CombatStyle;
        internal static ConfigEntry<bool> DebugLog;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<bool> SuppressHide;
        internal static ConfigEntry<bool> TtsEnabled;
        internal static ConfigEntry<string> TtsVoice;
        internal static ConfigEntry<float> TtsPitch;
        internal static ConfigEntry<string> TtsTone;
        internal static ConfigEntry<string> TtsTempo;
        internal static ConfigEntry<string> SttDevice;
        internal static ConfigEntry<float> TtsVolume;
        internal static ConfigEntry<bool> TtsFillers;

        // Каталог моста, который знал проект при сборке (CompanionBridge рядом с исходниками мода)
        private static string BuiltBridgeDir()
        {
            foreach (object attr in typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                var meta = (System.Reflection.AssemblyMetadataAttribute)attr;
                if (meta.Key == "BridgeDir" && !string.IsNullOrEmpty(meta.Value))
                {
                    return meta.Value;
                }
            }
            return Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "CompanionBridge");
        }

        private const string OldDefaultKit = "AxeBronze,ShieldWood,PickaxeAntler,ArmorLeatherChest,ArmorLeatherLegs,HelmetLeather,CapeDeerHide";
        private const string DefaultKit = "AxeBronze,ShieldWood,PickaxeAntler,BowFineWood,ArrowFlint:60,ArmorLeatherChest,ArmorLeatherLegs,HelmetLeather,CapeDeerHide";

        // Не чаще раза в секунду: настройки меняются пачкой
        private static float s_configDirtyAt = -1f;

        private void Awake()
        {
            Log = Logger;
            string bridgeDir = BuiltBridgeDir();

            CompanionName = Config.Bind("Companion", "Name", "Бьёрн", "Имя компаньона.");
            MaxHealth = Config.Bind("Companion", "MaxHealth", 250f, "Здоровье компаньона.");
            Kit = Config.Bind("Companion", "Kit", DefaultKit,
                "Снаряжение (имена префабов через запятую). Топор — оружие и инструмент для рубки, кирка — для камня, лук — для охоты. " +
                "«Имя:число» — расходное в таком количестве (стрелы досыпаются при появлении и каждый новый день).");
            // Старый набор без лука — обновить на новый
            if (Kit.Value == OldDefaultKit)
            {
                Kit.Value = DefaultKit;
            }
            CombatStyle = Config.Bind("Companion", "CombatStyle", "melee", new ConfigDescription(
                "Как биться: melee — топор и щит, bow — из лука издалека (вблизи всё равно топором). Меняется и приказом «стреляй из лука».",
                new AcceptableValueList<string>("melee", "bow")));
            Hair = Config.Bind("Look", "Hair", "Hair3", "Причёска (префаб Hair*).");
            Beard = Config.Bind("Look", "Beard", "Beard5", "Борода (префаб Beard*).");
            HairColor = Config.Bind("Look", "HairColor", "0.55,0.32,0.16", "Цвет волос RGB 0..1.");
            RespawnSeconds = Config.Bind("Companion", "RespawnSeconds", 90f, "Через сколько секунд павший компаньон возвращается.");

            BridgeUrl = Config.Bind("Bridge", "Url", "http://127.0.0.1:8765", "Адрес локального моста к LLM.");
            BridgeAutoStart = Config.Bind("Bridge", "AutoStart", true, "Запускать мост автоматически вместе с игрой.");
            BridgePython = Config.Bind("Bridge", "Python", Path.Combine(bridgeDir, ".venv/bin/python"), "Python из виртуального окружения моста.");
            BridgeScript = Config.Bind("Bridge", "Script", Path.Combine(bridgeDir, "bridge.py"), "Скрипт моста.");

            Chattiness = Config.Bind("Personality", "Chattiness", "normal", new ConfigDescription(
                "Болтливость: quiet — говорит сам только о важном (раны, итог дела); normal — комментирует ночь, новые земли, бой; chatty — чаще и охотнее.",
                new AcceptableValueList<string>("quiet", "normal", "chatty")));
            Role = Config.Bind("Personality", "Role", "viking", new ConfigDescription(
                "Роль: viking — оригинальный Бьёрн; skald — скальд; mentor — старый наставник; jester — весёлый дружинник; friend — друг по сети, будто играет с тобой; custom — только своё описание.",
                new AcceptableValueList<string>("viking", "skald", "mentor", "jester", "friend", "custom")));
            RoleCustom = Config.Bind("Personality", "RoleCustom", "", "Своё описание характера — дополняет роль (или заменяет при Role = custom).");
            ReplyLength = Config.Bind("Personality", "ReplyLength", "short", new ConfigDescription(
                "Длина ответов: short — одна-две фразы; normal — две-три; long — развёрнуто.", new AcceptableValueList<string>("short", "normal", "long")));
            Humor = Config.Bind("Personality", "Humor", 50, new ConfigDescription("Юмор, 0–100.", new AcceptableValueRange<int>(0, 100)));
            Rudeness = Config.Bind("Personality", "Rudeness", 50, new ConfigDescription("Резкость и грубоватость, 0–100.", new AcceptableValueRange<int>(0, 100)));
            Thinking = Config.Bind("Brain", "Thinking", "fast", new ConfigDescription(
                "Как думает: fast — Haiku, отвечает быстрее всего; thoughtful — Haiku с размышлением, точнее, но дольше; smart — Sonnet, умнее и дороже, ответ голосом заметно позже.",
                new AcceptableValueList<string>("fast", "thoughtful", "smart")));
            Learn = Config.Bind("Brain", "Memory", true, "Учиться и помнить: факты о тебе и мире, летопись странствий — отдельно для каждого мира.");

            VoiceEnabled = Config.Bind("Voice", "Enabled", true, "Разговор голосом: держи клавишу, говори, отпусти.");
            VoiceKey = Config.Bind("Voice", "Key", KeyCode.R, "Клавиша «рации».");
            SuppressHide = Config.Bind("Voice", "SuppressHide", true,
                "Глушить игровое действие «Убрать оружие» (по умолчанию тоже R). Его можно переназначить в настройках управления.");
            TtsEnabled = Config.Bind("Voice", "Speech", true, "Компаньон отвечает голосом (Piper, локально).");
            TtsVoice = Config.Bind("Voice", "SpeechVoice", "aidar",
                "Голос. Silero (живее): aidar, eugene — мужские; baya, kseniya, xenia — женские. Piper (запасной): ruslan, denis, dmitri.");
            TtsTone = Config.Bind("Voice", "SpeechTone", "low", "Тон голоса Silero: x-low, low, medium, high.");
            TtsTempo = Config.Bind("Voice", "SpeechTempo", "medium", "Темп речи Silero: x-slow, slow, medium, fast.");
            TtsPitch = Config.Bind("Voice", "SpeechPitch", 0.9f, "Тон голоса Piper: меньше 1 — ниже и грубее.");
            SttDevice = Config.Bind("Voice", "RecognitionDevice", "cpu",
                "Где распознавать речь: cpu — ровно ~0,4 с на фразу и без рывков игры; gpu — быстрее без игры, но под Valheim первая фраза ~1,5 с и игра подтормаживает.");
            TtsVolume = Config.Bind("Voice", "SpeechVolume", 1.0f, "Громкость голоса.");
            TtsFillers = Config.Bind("Voice", "Fillers", true, "Хмыкать, пока думает, если ответ не готов заранее.");

            DebugLog = Config.Bind("Debug", "DebugLog", false, "Подробный лог задач компаньона (цели, удары, подбор).");

            Config.SettingChanged += (sender, args) => s_configDirtyAt = Time.realtimeSinceStartup;
            new Harmony(Guid).PatchAll();
            Log.LogInfo($"{ModName} {Version} загружен");
        }

        private void Update()
        {
            if (s_configDirtyAt >= 0f && Time.realtimeSinceStartup - s_configDirtyAt > 0.5f)
            {
                s_configDirtyAt = -1f;
                Bridge.PushConfig();
            }
            Bridge.Pump();
            CompanionManager.Update(Time.deltaTime);
            Voice.Update();
        }
    }
}
