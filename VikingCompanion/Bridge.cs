using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VikingCompanion
{
    // Клиент локального моста (CompanionBridge/bridge.py). Запросы идут в фоне,
    // ответы выполняются в главном потоке через Pump().
    internal static class Bridge
    {
        [Serializable]
        public class EnemyInfo
        {
            public string name;
            public float distance;
        }

        [Serializable]
        public class GameState
        {
            public string player_name;
            public float player_hp;
            public float player_hp_max;
            public float player_weight;
            public float player_weight_max;
            public string player_weapon;
            public float companion_hp;
            public float companion_hp_max;
            public string companion_mode;
            public string companion_carry;
            public string biome;
            public int day;
            public string time_of_day;
            public string weather;
            public string location;
            public int trees_near;
            public string trees_where;
            public int rocks_near;
            public string rocks_where;
            public int items_near;
            public EnemyInfo[] enemies;
        }

        [Serializable]
        private class EventRequest
        {
            public string kind;
            public string text;
            public GameState state;
        }

        [Serializable]
        public class Reply
        {
            public string say;
            public string action;
            public string error;
        }

        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly ConcurrentQueue<Action> s_mainThread = new ConcurrentQueue<Action>();
        private static int s_inFlight;
        private static bool s_startAttempted;
        private static bool s_warnedOffline;
        private static DateTime s_startedAt = DateTime.MinValue;

        public static bool Busy => s_inFlight > 0;

        private static string Url => Plugin.BridgeUrl.Value.TrimEnd('/');

        [Serializable]
        private class VoiceRequest
        {
            public GameState state;
            public string test_audio;
        }

        [Serializable]
        public class VoiceStartReply
        {
            public bool ok;
            public string error;
        }

        [Serializable]
        public class VoicePartial
        {
            public string text;
            public float level;
            public bool recording;
        }

        [Serializable]
        public class VoiceReply
        {
            public string transcript;
            public string say;
            public string action;
            public string error;
            public float total_ms;
            public bool speculative;
        }

        public static void Send(string kind, string text, GameState state, Action<Reply> onDone)
        {
            string json = Json.Serialize(new EventRequest { kind = kind, text = text, state = state });
            Interlocked.Increment(ref s_inFlight);
            Request("/v1/event", json, (body, error) =>
            {
                Interlocked.Decrement(ref s_inFlight);
                Reply reply = Parse<Reply>(body, ref error) ?? new Reply { say = "", action = "none", error = error ?? "empty" };
                if (!string.IsNullOrEmpty(reply.error) && !s_warnedOffline)
                {
                    s_warnedOffline = true;
                    Plugin.Log.LogWarning($"Мост не ответил ({reply.error}) — компаньон понимает только простые команды");
                }
                else if (string.IsNullOrEmpty(reply.error))
                {
                    s_warnedOffline = false;
                }
                onDone(reply);
            });
        }

        // ---------- Голос ----------

        public static void VoiceStart(GameState state, string testAudio, Action<VoiceStartReply> onDone)
        {
            string json = Json.Serialize(new VoiceRequest { state = state, test_audio = testAudio ?? "" });
            Request("/v1/voice/start", json, (body, error) =>
                onDone(Parse<VoiceStartReply>(body, ref error) ?? new VoiceStartReply { ok = false, error = error ?? "empty" }));
        }

        public static void VoiceStop(GameState state, Action<VoiceReply> onDone)
        {
            string json = Json.Serialize(new VoiceRequest { state = state, test_audio = "" });
            Request("/v1/voice/stop", json, (body, error) =>
                onDone(Parse<VoiceReply>(body, ref error) ?? new VoiceReply { say = "", action = "none", error = error ?? "empty" }));
        }

        public static void VoiceCancel()
        {
            Request("/v1/voice/cancel", "{}", (body, error) => { });
        }

        // state != null — заодно отдать мосту свежую обстановку для упреждающего ответа
        public static void VoicePartialPoll(GameState state, Action<VoicePartial> onDone)
        {
            string json = state != null ? Json.Serialize(new VoiceRequest { state = state, test_audio = "" }) : null;
            Request("/v1/voice/partial", json, (body, error) =>
            {
                VoicePartial partial = Parse<VoicePartial>(body, ref error);
                if (partial != null)
                {
                    onDone(partial);
                }
            });
        }

        // ---------- Транспорт ----------

        private static T Parse<T>(string body, ref string error) where T : class
        {
            if (body == null)
            {
                return null;
            }
            try
            {
                return JsonUtility.FromJson<T>(body);
            }
            catch (Exception)
            {
                error = "bad_json";
                return null;
            }
        }

        // json == null — GET. Ответ (тело или ошибка) приходит в главном потоке.
        private static void Request(string path, string json, Action<string, string> onDone)
        {
            Task.Run(async () =>
            {
                string body = null;
                string error = null;
                // Сразу после запуска мост ещё поднимается — даём ему несколько секунд
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (json == null)
                        {
                            body = await s_http.GetStringAsync(Url + path).ConfigureAwait(false);
                        }
                        else
                        {
                            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                            using (HttpResponseMessage response = await s_http.PostAsync(Url + path, content).ConfigureAwait(false))
                            {
                                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }
                        }
                        break;
                    }
                    catch (Exception e)
                    {
                        bool starting = (DateTime.UtcNow - s_startedAt).TotalSeconds < 20;
                        if (!starting || attempt >= 6 || json == null)
                        {
                            error = "offline: " + e.GetType().Name;
                            break;
                        }
                        await Task.Delay(1500).ConfigureAwait(false);
                    }
                }
                s_mainThread.Enqueue(() => onDone(body, error));
            });
        }

        public static void Pump()
        {
            while (s_mainThread.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                }
            }
        }

        public static void EnsureStarted()
        {
            if (s_startAttempted || !Plugin.BridgeAutoStart.Value)
            {
                return;
            }
            s_startAttempted = true;
            s_startedAt = DateTime.UtcNow;
            Task.Run(async () =>
            {
                bool running = false;
                try
                {
                    using (HttpResponseMessage r = await s_http.GetAsync(Url + "/health").ConfigureAwait(false))
                    {
                        running = r.IsSuccessStatusCode;
                    }
                }
                catch (Exception)
                {
                }
                s_mainThread.Enqueue(() =>
                {
                    if (running)
                    {
                        Plugin.Log.LogInfo("Мост уже запущен");
                    }
                    else
                    {
                        StartProcess();
                    }
                });
            });
        }

        private static string VoiceArgs()
        {
            var args = new System.Collections.Generic.List<string>
            {
                "--voice", Plugin.TtsVoice.Value,
                "--pitch", Plugin.TtsPitch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--silero-pitch", Plugin.TtsTone.Value,
                "--silero-tempo", Plugin.TtsTempo.Value,
                "--stt-device", Plugin.SttDevice.Value,
                "--volume", Plugin.TtsVolume.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (!Plugin.TtsEnabled.Value)
            {
                args.Add("--no-tts");
            }
            if (!Plugin.TtsFillers.Value)
            {
                args.Add("--no-fillers");
            }
            // Для тестов: фейковый микрофон, без звука
            string extra = Environment.GetEnvironmentVariable("VCOMP_BRIDGE_ARGS");
            if (!string.IsNullOrEmpty(extra))
            {
                args.Add(extra);
            }
            return string.Join(" ", args);
        }

        private static void StartProcess()
        {
            string python = Plugin.BridgePython.Value;
            string script = Plugin.BridgeScript.Value;
            if (!File.Exists(python) || !File.Exists(script))
            {
                Plugin.Log.LogError($"Мост не найден: {python} / {script}");
                return;
            }
            int port = new Uri(Url).Port;
            int pid = Process.GetCurrentProcess().Id;
            string name = Plugin.CompanionName.Value.Replace("\"", "");
            var psi = new ProcessStartInfo("/usr/bin/arch")
            {
                // Игра идёт под Rosetta, а venv моста собран под arm64
                Arguments = $"-arm64 \"{python}\" -I \"{script}\" --port {port} --parent-pid {pid} --name \"{name}\" {VoiceArgs()}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            // Не тащим в Python внедрение BepInEx
            foreach (string key in psi.Environment.Keys.ToList())
            {
                if (key.StartsWith("DYLD_") || key.StartsWith("DOORSTOP_") || key == "LD_PRELOAD" || key == "LD_LIBRARY_PATH")
                {
                    psi.Environment.Remove(key);
                }
            }
            try
            {
                Process.Start(psi);
                Plugin.Log.LogInfo("Мост запущен");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Не удалось запустить мост: {e.Message}");
            }
        }
    }
}
