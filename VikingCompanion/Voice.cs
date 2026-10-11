using UnityEngine;

namespace VikingCompanion
{
    // Разговор голосом: держишь клавишу (R) — мост пишет микрофон и распознаёт по ходу,
    // отпускаешь — Бьёрн отвечает (часто ответ готов заранее, пока ты договаривал).
    public static class Voice
    {
        private enum State { Idle, Starting, Recording, Waiting }

        private const float MinHold = 0.3f;
        private const float PollInterval = 0.2f;
        private const float StateInterval = 1f;

        private static State s_state = State.Idle;
        private static float s_pressedAt;
        private static float s_nextPoll;
        private static float s_nextState;
        private static bool s_releasedEarly;
        private static string s_partial = "";
        private static float s_level;
        private static string s_testAudio = "";
        private static bool s_testHeld;

        public static bool Busy => s_state != State.Idle;

        public static void Update()
        {
            if (!Plugin.VoiceEnabled.Value)
            {
                return;
            }
            UpdateInput();
            UpdatePanel();
        }

        private static void UpdateInput()
        {
            Player player = Player.m_localPlayer;
            KeyCode key = Plugin.VoiceKey.Value;
            bool held = s_testHeld || ZInput.GetKey(key, false);

            switch (s_state)
            {
                case State.Idle:
                    if (player != null && (s_testHeld || ZInput.GetKeyDown(key, false)) && player.TakeInput())
                    {
                        Begin(player);
                    }
                    break;
                case State.Starting:
                    if (!held)
                    {
                        s_releasedEarly = true;   // отпустил раньше, чем мост открыл микрофон
                    }
                    break;
                case State.Recording:
                    if (!held)
                    {
                        End();
                    }
                    else if (Time.time >= s_nextPoll)
                    {
                        s_nextPoll = Time.time + PollInterval;
                        Bridge.GameState state = null;
                        if (Time.time >= s_nextState)
                        {
                            s_nextState = Time.time + StateInterval;
                            state = CompanionManager.CurrentState();
                        }
                        Bridge.VoicePartialPoll(state, p =>
                        {
                            if (s_state == State.Recording)
                            {
                                s_partial = p.text ?? "";
                                s_level = p.level;
                            }
                        });
                    }
                    break;
            }
        }

        // Для автотестов: «нажать» и «отпустить» кнопку, вместо микрофона — wav моста
        public static void TestPress(string clip)
        {
            s_testAudio = clip;
            s_testHeld = true;
        }

        public static void TestRelease()
        {
            s_testHeld = false;
        }

        private static void Begin(Player player)
        {
            if (Companion.Local == null && !CompanionManager.IsParked)
            {
                ShowStatus($"{Plugin.CompanionName.Value} не рядом");
                return;
            }
            s_state = State.Starting;
            s_pressedAt = Time.time;
            s_releasedEarly = false;
            s_partial = "";
            s_level = 0f;
            string clip = s_testAudio;
            s_testAudio = "";
            Bridge.VoiceStart(CompanionManager.CurrentState(), clip, reply =>
            {
                if (!reply.ok)
                {
                    s_state = State.Idle;
                    ShowStatus(DescribeError(reply.error));
                    return;
                }
                s_state = State.Recording;
                s_nextPoll = Time.time;
                s_nextState = Time.time + StateInterval;
                if (s_releasedEarly)
                {
                    End();
                }
            });
        }

        private static void End()
        {
            if (Time.time - s_pressedAt < MinHold)
            {
                // Случайное короткое нажатие — не разговор
                Bridge.VoiceCancel();
                s_state = State.Idle;
                return;
            }
            s_state = State.Waiting;
            Bridge.VoiceStop(CompanionManager.CurrentState(), reply =>
            {
                s_state = State.Idle;
                if (!string.IsNullOrEmpty(reply.error))
                {
                    ShowStatus(DescribeError(reply.error));
                    return;
                }
                Plugin.Log.LogInfo($"Голос: «{reply.transcript}» → ответ через {reply.total_ms:0} мс{(reply.speculative ? " (готовился заранее)" : "")}");
                CompanionManager.OnVoiceReply(reply);
            });
        }

        private static string DescribeError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return "";
            }
            if (error.StartsWith("no_mic"))
            {
                return "Нет доступа к микрофону. Разреши его в Системных настройках → Конфиденциальность → Микрофон";
            }
            if (error.StartsWith("mic_error"))
            {
                return "Микрофон не открылся — проверь, что он подключён и выбран в настройках звука macOS";
            }
            if (error == "silence")
            {
                return "Не расслышал";
            }
            if (error == "loading")
            {
                return "Голос ещё загружается, попробуй через пару секунд";
            }
            if (error.StartsWith("offline"))
            {
                return "Мост не запущен — голос недоступен";
            }
            return "Голос: ошибка (" + error + ")";
        }

        private static void ShowStatus(string text)
        {
            if (!string.IsNullOrEmpty(text) && MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
            }
        }

        // ---------- Окно на экране: родной пузырь реплики NPC, закреплённый внизу ----------

        private static GameObject s_panel;
        private static Animator s_panelAnimator;
        private static TMPro.TMP_Text s_panelTopic;
        private static TMPro.TMP_Text s_panelText;
        private static bool s_panelShown;
        private static float s_nextPanelText;

        private static void UpdatePanel()
        {
            bool show = s_state != State.Idle && Player.m_localPlayer != null
                && Hud.instance != null && !Hud.instance.m_userHidden;
            if (s_panel == null)
            {
                if (!show || Chat.instance == null)
                {
                    return;
                }
                s_panel = Object.Instantiate(Chat.instance.m_npcTextBaseLarge, Chat.instance.transform);
                s_panel.name = "VikingCompanionVoice";
                s_panel.SetActive(true);
                s_panelAnimator = s_panel.GetComponent<Animator>();
                s_panelTopic = s_panel.transform.Find("Topic").GetComponent<TMPro.TMP_Text>();
                s_panelText = s_panel.transform.Find("Text").GetComponent<TMPro.TMP_Text>();
            }
            if (show != s_panelShown)
            {
                s_panelShown = show;
                s_panelAnimator.SetBool("visible", show);
                s_nextPanelText = 0f;
            }
            if (!show)
            {
                return;
            }
            s_panel.transform.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.14f, 0f);
            // Текст пересобираем 10 раз в секунду, а не каждый кадр: TMP перестраивает меш на каждое присвоение
            if (Time.unscaledTime < s_nextPanelText)
            {
                return;
            }
            s_nextPanelText = Time.unscaledTime + 0.1f;

            string name = Plugin.CompanionName.Value;
            s_panelTopic.text = "";
            if (s_state == State.Waiting)
            {
                SetPanelText($"<color=#ffc64d>{name} думает…</color>\n{(s_partial.Length > 0 ? s_partial : "…")}");
                return;
            }
            // Точка «на записи» горит ярче, когда звук громче
            float glow = Mathf.Clamp01(0.35f + s_level * 15f);
            string dot = $"<color=#{ColorUtility.ToHtmlStringRGB(Color.Lerp(new Color(0.35f, 0.1f, 0.08f), new Color(1f, 0.25f, 0.15f), glow))}>●</color>";
            string body = s_partial.Length > 0 ? s_partial : $"<color=#bbbbbb>говори… отпусти {Plugin.VoiceKey.Value}, когда закончишь</color>";
            SetPanelText($"{dot} <color=#ffc64d>{name} слушает</color>\n{body}");
        }

        private static void SetPanelText(string text)
        {
            if (s_panelText.text != text)
            {
                s_panelText.text = text;
            }
        }
    }
}
