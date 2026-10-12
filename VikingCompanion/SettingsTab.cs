using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Valheim.SettingsGui;
using Object = UnityEngine.Object;

namespace VikingCompanion
{
    // Вкладка «Бьёрн» в окне настроек игры: роль и характер, длина ответов, болтливость, «мышление»,
    // голос, стиль боя, своё описание роли и память. Элементы — копии родных из других вкладок,
    // поэтому выглядят как часть игры. Сохраняется по «ОК», как остальные настройки.
    internal class SettingsTab : MonoBehaviour, ISettingsTab
    {
        // Общих с другими вкладками настроек нет
        public event Action<string, int> SharedSettingChanged
        {
            add { }
            remove { }
        }

        private static readonly (string key, string label, string hint)[] s_roles =
        {
            ("viking", "Оригинальный Бьёрн", "Суровый викинг из Валхейма. Знает только этот мир."),
            ("skald", "Скальд", "Воин-поэт: кеннинги, торжественная речь, иногда строфа."),
            ("mentor", "Старый наставник", "Седой ветеран: спокойные дельные советы, как выжить и окрепнуть."),
            ("jester", "Весёлый дружинник", "Балагур и задира: байки, подначки, шутки в любой беде."),
            ("friend", "Друг по сети", "Будто живой друг играет с тобой по сети: знает игру, говорит как геймер."),
            ("custom", "Только своя роль", "Характер целиком из твоего описания ниже."),
        };
        private static readonly (string key, string label, string hint)[] s_lengths =
        {
            ("short", "Коротко", "Одна-две фразы — быстрее всего голосом."),
            ("normal", "Обычно", "Две-три фразы."),
            ("long", "Развёрнуто", "До пяти фраз, если есть что сказать."),
        };
        private static readonly (string key, string label, string hint)[] s_chattiness =
        {
            ("quiet", "Молчун", "Сам говорит только о важном: раны, итог дела, опасность."),
            ("normal", "Обычно", "Откликается на ночь, новые земли, бой; иногда говорит в пути."),
            ("chatty", "Болтун", "Говорит чаще, вспоминает былое, сам расспрашивает."),
        };
        private static readonly (string key, string label, string hint)[] s_thinking =
        {
            ("fast", "Быстро · Haiku", "Отвечает быстрее всего (~1–2 с)."),
            ("thoughtful", "Вдумчиво · Haiku", "Дольше размышляет: точнее понимает сложные приказы, отвечает на пару секунд позже."),
            ("smart", "Умно · Sonnet", "Модель умнее: лучше советы и разговор, но дороже и ответ голосом заметно позже."),
        };
        private static readonly (string key, string label, string hint)[] s_voices =
        {
            ("aidar", "Айдар", "Мужской голос Silero."),
            ("eugene", "Евгений", "Мужской голос Silero."),
            ("baya", "Бая", "Женский голос Silero."),
            ("kseniya", "Ксения", "Женский голос Silero."),
            ("xenia", "Ксения (2)", "Женский голос Silero."),
            ("ruslan", "Руслан · Piper", "Запасной голос Piper: быстрее, но «синтетичнее»."),
        };
        private static readonly (string key, string label, string hint)[] s_tones =
        {
            ("x-low", "Очень низкий", ""), ("low", "Низкий", ""), ("medium", "Средний", ""), ("high", "Высокий", ""),
        };
        private static readonly (string key, string label, string hint)[] s_tempos =
        {
            ("x-slow", "Очень медленно", ""), ("slow", "Медленно", ""), ("medium", "Обычно", ""), ("fast", "Быстро", ""),
        };
        private static readonly (string key, string label, string hint)[] s_combat =
        {
            ("melee", "Топор и щит", "Бьётся вблизи."),
            ("bow", "Лук", "Стреляет издалека, вблизи берётся за топор."),
        };

        private class Stepper
        {
            public (string key, string label, string hint)[] Values;
            public int Index;
            public TMP_Text Text;
            public string Key => Values[Index].key;
        }

        private Transform m_root;
        private TabHandler m_tabs;
        private Stepper m_role, m_length, m_chat, m_think, m_voice, m_tone, m_tempo, m_combatStyle;
        private Slider m_humor, m_rudeness, m_volume;
        private Toggle m_tts, m_memory;
        private TMP_InputField m_name, m_custom;
        private TMP_Text m_hint, m_memoryText, m_forgetLabel;
        private float m_forgetArmedUntil;
        private TMP_FontAsset m_font;

        // ---------- Встраивание в окно настроек ----------

        public static void Inject(Settings settings)
        {
            Transform panel = settings.transform.Find("Panel");
            Transform buttons = panel != null ? panel.Find("TabButtons") : null;
            Transform content = panel != null ? panel.Find("TabContent") : null;
            TabHandler tabs = buttons != null ? buttons.GetComponent<TabHandler>() : null;
            if (tabs == null || content == null || buttons.childCount == 0 || buttons.Find("Bjorn") != null)
            {
                return;
            }
            Transform source = buttons.Find("Accessibility") ?? buttons.GetChild(buttons.childCount - 1);
            GameObject button = Instantiate(source.gameObject, buttons);
            button.name = "Bjorn";
            foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.name.StartsWith("Label"))
                {
                    text.text = Plugin.CompanionName.Value;
                }
            }
            Button b = button.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();

            var page = new GameObject("Bjorn", typeof(RectTransform));
            var rect = (RectTransform)page.transform;
            rect.SetParent(content, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            page.SetActive(false);
            SettingsTab tab = page.AddComponent<SettingsTab>();
            tab.m_root = settings.transform;
            tab.m_tabs = tabs;
            tabs.m_tabs.Add(new TabHandler.Tab { m_button = b, m_page = rect, m_default = false, m_onClick = new UnityEvent() });
        }

        // ---------- ISettingsTab ----------

        public void Initialize()
        {
            try
            {
                Build();
                Load();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Вкладка «{Plugin.CompanionName.Value}» не построилась: {e}");
            }
        }

        public void OnTabOpen(Button backButton, Button okButton)
        {
            RefreshMemory();
        }

        public void OnOkAsync(OkActionCompletedHandler okActionCompletedCallback)
        {
            try
            {
                Save();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Настройки {Plugin.CompanionName.Value} не сохранились: {e}");
            }
            okActionCompletedCallback?.Invoke();
        }

        public void OnBack()
        {
        }

        private bool m_typing;
        private (bool tab, bool keyboard, bool gamepad) m_tabInput;

        private void Update()
        {
            // Пока печатаешь в поле, Tab и Q/E не листают вкладки
            bool typing = (m_name != null && m_name.isFocused) || (m_custom != null && m_custom.isFocused);
            if (m_tabs != null && typing != m_typing)
            {
                if (typing)
                {
                    m_tabInput = (m_tabs.m_tabKeyInput, m_tabs.m_keybaordInput, m_tabs.m_gamepadInput);
                    m_tabs.m_tabKeyInput = m_tabs.m_keybaordInput = m_tabs.m_gamepadInput = false;
                }
                else
                {
                    (m_tabs.m_tabKeyInput, m_tabs.m_keybaordInput, m_tabs.m_gamepadInput) = m_tabInput;
                }
                m_typing = typing;
            }
            if (m_forgetLabel != null && m_forgetArmedUntil > 0f && Time.unscaledTime > m_forgetArmedUntil)
            {
                m_forgetArmedUntil = 0f;
                m_forgetLabel.text = "Забыть всё";
            }
        }

        // ---------- Значения ----------

        private void Load()
        {
            Set(m_role, Plugin.Role.Value);
            Set(m_length, Plugin.ReplyLength.Value);
            Set(m_chat, Plugin.Chattiness.Value);
            Set(m_think, Plugin.Thinking.Value);
            Set(m_voice, Plugin.TtsVoice.Value);
            Set(m_tone, Plugin.TtsTone.Value);
            Set(m_tempo, Plugin.TtsTempo.Value);
            Set(m_combatStyle, Plugin.CombatStyle.Value);
            m_humor.SetValueWithoutNotify(Plugin.Humor.Value);
            m_rudeness.SetValueWithoutNotify(Plugin.Rudeness.Value);
            m_volume.SetValueWithoutNotify(Mathf.Clamp01(Plugin.TtsVolume.Value));
            m_tts.SetIsOnWithoutNotify(Plugin.TtsEnabled.Value);
            m_memory.SetIsOnWithoutNotify(Plugin.Learn.Value);
            m_name.SetTextWithoutNotify(Plugin.CompanionName.Value);
            m_custom.SetTextWithoutNotify(Plugin.RoleCustom.Value);
            UpdateSliderTexts();
            ShowHint(m_role);
        }

        private void Save()
        {
            Plugin.Role.Value = m_role.Key;
            Plugin.ReplyLength.Value = m_length.Key;
            Plugin.Chattiness.Value = m_chat.Key;
            Plugin.Thinking.Value = m_think.Key;
            Plugin.TtsVoice.Value = m_voice.Key;
            Plugin.TtsTone.Value = m_tone.Key;
            Plugin.TtsTempo.Value = m_tempo.Key;
            Plugin.CombatStyle.Value = m_combatStyle.Key;
            Plugin.Humor.Value = Mathf.RoundToInt(m_humor.value);
            Plugin.Rudeness.Value = Mathf.RoundToInt(m_rudeness.value);
            Plugin.TtsVolume.Value = Mathf.Round(m_volume.value * 100f) / 100f;
            Plugin.TtsEnabled.Value = m_tts.isOn;
            Plugin.Learn.Value = m_memory.isOn;
            Plugin.RoleCustom.Value = (m_custom.text ?? "").Trim();
            string name = (m_name.text ?? "").Trim();
            if (name.Length > 0 && name != Plugin.CompanionName.Value)
            {
                Plugin.CompanionName.Value = name;
                if (Companion.Local != null)
                {
                    Companion.Local.Humanoid.m_name = name;
                }
            }
            Plugin.Log.LogInfo($"Настройки {Plugin.CompanionName.Value}: роль {m_role.Key}, ответы {m_length.Key}, болтливость {m_chat.Key}, " +
                $"мышление {m_think.Key}, юмор {Plugin.Humor.Value}, резкость {Plugin.Rudeness.Value}, голос {m_voice.Key}, бой {m_combatStyle.Key}");
        }

        private static void Set(Stepper stepper, string key)
        {
            int index = Array.FindIndex(stepper.Values, v => v.key == key);
            stepper.Index = Mathf.Max(0, index);
            stepper.Text.text = stepper.Values[stepper.Index].label;
        }

        private void Step(Stepper stepper, int delta)
        {
            int n = stepper.Values.Length;
            stepper.Index = ((stepper.Index + delta) % n + n) % n;
            stepper.Text.text = stepper.Values[stepper.Index].label;
            ShowHint(stepper);
        }

        private void ShowHint(Stepper stepper)
        {
            string hint = stepper.Values[stepper.Index].hint;
            if (m_hint != null && !string.IsNullOrEmpty(hint))
            {
                m_hint.text = hint;
            }
        }

        private void UpdateSliderTexts()
        {
            SetValueText(m_humor, $"{Mathf.RoundToInt(m_humor.value)}");
            SetValueText(m_rudeness, $"{Mathf.RoundToInt(m_rudeness.value)}");
            SetValueText(m_volume, $"{Mathf.RoundToInt(m_volume.value * 100f)}%");
        }

        private static void SetValueText(Slider slider, string text)
        {
            Transform value = slider.transform.Find("Value");
            if (value != null)
            {
                value.GetComponent<TMP_Text>().text = text;
            }
        }

        // ---------- Память ----------

        private void RefreshMemory()
        {
            if (m_memoryText == null)
            {
                return;
            }
            if (Player.m_localPlayer == null)
            {
                m_memoryText.text = "Память своя для каждого мира — загляни сюда в игре.";
                return;
            }
            m_memoryText.text = "Память: …";
            Bridge.QueryMemory(CompanionManager.CurrentState(), info =>
            {
                if (m_memoryText != null)
                {
                    m_memoryText.text = info.ok ? $"В этом мире помнит фактов: {info.facts}" : "Мост не отвечает — память недоступна.";
                }
            });
        }

        private void OnForget()
        {
            if (Player.m_localPlayer == null)
            {
                m_memoryText.text = "Стереть память можно в игре: она своя для каждого мира.";
                return;
            }
            if (m_forgetArmedUntil <= 0f)
            {
                m_forgetArmedUntil = Time.unscaledTime + 3f;
                m_forgetLabel.text = "Точно? Ещё раз";
                return;
            }
            m_forgetArmedUntil = 0f;
            m_forgetLabel.text = "Забыть всё";
            Bridge.ForgetMemory(CompanionManager.CurrentState(), ok =>
            {
                if (m_memoryText != null)
                {
                    m_memoryText.text = ok ? $"{Plugin.CompanionName.Value} всё забыл об этом мире." : "Не вышло: мост не отвечает.";
                }
            });
        }

        // ---------- Построение ----------

        private Transform Template(string path)
        {
            Transform t = m_root.Find(path);
            if (t == null)
            {
                throw new InvalidOperationException("нет образца " + path);
            }
            return t;
        }

        private void Build()
        {
            Transform stepperT = Template("Panel/TabContent/Gameplay/List/Language");
            Transform sliderT = Template("Panel/TabContent/Audio/List/MasterVolume");
            Transform toggleT = Template("Panel/TabContent/Audio/List/ContinuosMusic");
            Transform buttonT = Template("Panel/TabContent/Gameplay/List/Tutorial/Reset");
            m_font = stepperT.GetComponentInChildren<TMP_Text>(true).font;

            const float c1 = 210f, c2 = 655f, w = 230f;
            float Row(int i) => 46f + i * 44f;

            Header("Характер", c1 - 190f, 18f);
            m_role = AddStepper(stepperT, "Роль", s_roles, c1, Row(0), w);
            m_length = AddStepper(stepperT, "Длина ответов", s_lengths, c1, Row(1), w);
            m_chat = AddStepper(stepperT, "Болтливость", s_chattiness, c1, Row(2), w);
            m_think = AddStepper(stepperT, "Мышление", s_thinking, c1, Row(3), w);
            m_humor = AddSlider(sliderT, "Юмор", 0f, 100f, true, c1, Row(4), w - 40f);
            m_rudeness = AddSlider(sliderT, "Резкость", 0f, 100f, true, c1, Row(5), w - 40f);

            Header("Голос и бой", c2 - 190f, 18f);
            m_voice = AddStepper(stepperT, "Голос", s_voices, c2, Row(0), w);
            m_tone = AddStepper(stepperT, "Тон", s_tones, c2, Row(1), w);
            m_tempo = AddStepper(stepperT, "Темп речи", s_tempos, c2, Row(2), w);
            m_volume = AddSlider(sliderT, "Громкость", 0f, 1f, false, c2, Row(3), w - 40f);
            m_tts = AddToggle(toggleT, "Говорить вслух", c2, Row(4));
            m_combatStyle = AddStepper(stepperT, "В бою", s_combat, c2, Row(5), w);

            m_name = AddInput("Имя", c1, Row(6) + 4f, w, 32f, false, "Бьёрн");
            m_memory = AddToggle(toggleT, "Учиться и помнить", c2, Row(6) + 4f);

            Label("Своя роль: опиши, каким он должен быть", 30f, Row(7) + 6f, 600f, TextAlignmentOptions.Left, 14f);
            m_custom = AddInput(null, 30f, Row(7) + 24f, 860f, 78f, true,
                "Например: «Ты мой старший брат, зови меня малой» или «Говори как пират, ненавидишь троллей»");

            m_hint = Label("", 30f, Row(9) + 40f, 520f, TextAlignmentOptions.Left, 13f);
            m_hint.color = new Color(0.8f, 0.8f, 0.8f, 1f);
            m_memoryText = Label("", 560f, Row(9) + 40f, 200f, TextAlignmentOptions.Right, 13f);
            m_memoryText.color = new Color(0.8f, 0.8f, 0.8f, 1f);
            AddButton(buttonT, "Забыть всё", 775f, Row(9) + 40f, 120f, 34f, OnForget, out m_forgetLabel);
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private TMP_Text Label(string text, float x, float y, float width, TextAlignmentOptions align, float size)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(transform, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.font = m_font;
            tmp.fontSize = size;
            tmp.color = new Color(1f, 0.64f, 0.1f, 1f);
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.text = text;
            tmp.raycastTarget = false;
            Place((RectTransform)go.transform, x, y, width, 22f);
            return tmp;
        }

        private void Header(string text, float x, float y)
        {
            TMP_Text t = Label(text, x, y, 400f, TextAlignmentOptions.Left, 17f);
            t.fontStyle = FontStyles.Bold;
        }

        private Stepper AddStepper(Transform template, string label, (string, string, string)[] values, float x, float y, float width)
        {
            GameObject go = Instantiate(template.gameObject, transform);
            go.name = "Stepper_" + label;
            Place((RectTransform)go.transform, x, y, width, 32f);
            // Подпись слева от переключателя, по правому краю
            var labelT = (RectTransform)go.transform.Find("Label");
            labelT.pivot = new Vector2(1f, 0.5f);
            labelT.anchoredPosition = new Vector2(-14f, 0f);
            labelT.sizeDelta = new Vector2(190f, 0f);
            TMP_Text labelText = labelT.GetComponent<TMP_Text>();
            labelText.text = label;
            labelText.alignment = TextAlignmentOptions.MidlineRight;
            Transform extra = go.transform.Find("CommunityTranslation");
            if (extra != null)
            {
                Destroy(extra.gameObject);
            }
            Transform stepper = go.transform.Find("Stepper");
            var value = (RectTransform)stepper.Find("Value");
            value.offsetMin = new Vector2(36f, 0f);
            value.offsetMax = new Vector2(-36f, 0f);
            TMP_Text text = value.GetComponentInChildren<TMP_Text>();
            text.fontSize = 14f;
            text.enableAutoSizing = false;
            var s = new Stepper { Values = values, Text = text };
            foreach (string side in new[] { "Left", "Right" })
            {
                var btnRect = (RectTransform)stepper.Find(side);
                btnRect.sizeDelta = new Vector2(32f, 32f);
                btnRect.GetComponentInChildren<TMP_Text>().fontSize = 20f;
                Button btn = btnRect.GetComponent<Button>();
                // Слушатели образца ведут к языку игры — заменяем своими
                btn.onClick = new Button.ButtonClickedEvent();
                int delta = side == "Left" ? -1 : 1;
                btn.onClick.AddListener(() => Step(s, delta));
            }
            StripTooltips(go);
            return s;
        }

        private Slider AddSlider(Transform template, string label, float min, float max, bool whole, float x, float y, float width)
        {
            GameObject go = Instantiate(template.gameObject, transform);
            go.name = "Slider_" + label;
            Place((RectTransform)go.transform, x, y, width, 20f);
            go.transform.Find("Label").GetComponent<TMP_Text>().text = label;
            Slider slider = go.GetComponent<Slider>();
            slider.onValueChanged = new Slider.SliderEvent();
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;
            slider.onValueChanged.AddListener(_ => UpdateSliderTexts());
            StripTooltips(go);
            return slider;
        }

        private Toggle AddToggle(Transform template, string label, float x, float y)
        {
            GameObject go = Instantiate(template.gameObject, transform);
            go.name = "Toggle_" + label;
            Place((RectTransform)go.transform, x, y, 20f, 20f);
            go.transform.Find("Label").GetComponent<TMP_Text>().text = label;
            Toggle toggle = go.GetComponent<Toggle>();
            toggle.onValueChanged = new Toggle.ToggleEvent();
            StripTooltips(go);
            return toggle;
        }

        private void AddButton(Transform template, string label, float x, float y, float width, float height, Action onClick, out TMP_Text text)
        {
            GameObject go = Instantiate(template.gameObject, transform);
            go.name = "Button_" + label;
            Place((RectTransform)go.transform, x, y, width, height);
            text = go.GetComponentInChildren<TMP_Text>();
            text.text = label;
            text.fontSize = 14f;
            Button button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => onClick());
            StripTooltips(go);
        }

        private TMP_InputField AddInput(string label, float x, float y, float width, float height, bool multiline, string placeholder)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            Place((RectTransform)go.transform, x, y, width, height);
            if (multiline)
            {
                ((RectTransform)go.transform).pivot = new Vector2(0f, 1f);
            }
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.47f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            var areaRect = (RectTransform)area.transform;
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.offsetMin = new Vector2(8f, 4f);
            areaRect.offsetMax = new Vector2(-8f, -4f);

            TextMeshProUGUI MakeText(string name, Color color)
            {
                var t = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
                t.transform.SetParent(area.transform, false);
                var r = (RectTransform)t.transform;
                r.anchorMin = Vector2.zero;
                r.anchorMax = Vector2.one;
                r.offsetMin = r.offsetMax = Vector2.zero;
                var tmp = t.GetComponent<TextMeshProUGUI>();
                tmp.font = m_font;
                tmp.fontSize = 14f;
                tmp.color = color;
                tmp.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
                tmp.textWrappingMode = multiline ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                tmp.richText = false;
                return tmp;
            }

            TextMeshProUGUI text = MakeText("Text", Color.white);
            TextMeshProUGUI hint = MakeText("Placeholder", new Color(1f, 1f, 1f, 0.35f));
            hint.text = placeholder;
            hint.fontStyle = FontStyles.Italic;

            // Поле собираем на неактивной странице: TMP_InputField включится, когда все ссылки уже на месте
            TMP_InputField input = go.AddComponent<TMP_InputField>();
            input.textViewport = areaRect;
            input.textComponent = text;
            input.placeholder = hint;
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = multiline ? 1500 : 24;
            input.richText = false;
            input.caretColor = new Color(1f, 0.64f, 0.1f, 1f);
            input.customCaretColor = true;
            input.selectionColor = new Color(1f, 0.64f, 0.1f, 0.35f);
            input.fontAsset = m_font;
            input.pointSize = 14f;

            if (label != null)
            {
                TMP_Text l = Label(label, x - 204f, y, 190f, TextAlignmentOptions.MidlineRight, 14f);
                l.transform.SetAsLastSibling();
            }
            return input;
        }

        private static void StripTooltips(GameObject go)
        {
            foreach (SettingsTooltip tip in go.GetComponentsInChildren<SettingsTooltip>(true))
            {
                Destroy(tip);
            }
        }
    }
}
