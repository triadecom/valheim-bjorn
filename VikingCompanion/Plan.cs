using System.Collections.Generic;
using System.Linq;

namespace VikingCompanion
{
    // Шаг плана из ответа моста: {do, what, count, kind?, note?}. План выполняется по порядку.
    public class PlanStep
    {
        public string Do = "";
        public string What = "";
        public int Count;
        public string Kind = "";   // mark_place: база, портал, ресурсы, опасность
        public string Note = "";   // mark_place: что сказал игрок о месте

        public PlanStep()
        {
        }

        public PlanStep(string action, string what = "", int count = 0)
        {
            Do = action;
            What = what ?? "";
            Count = count;
        }

        // Мгновенные шаги не ждут окончания дела: сразу идёт следующий
        public bool Instant => Do == "follow" || Do == "come" || Do == "attack" || Do == "combat_style" || Do == "none"
            || Do == "mark_place" || Do == "forget_place";

        public override string ToString()
        {
            string text = Do;
            if (What.Length > 0)
            {
                text += $" «{What}»";
            }
            if (Count > 0)
            {
                text += $" ×{Count}";
            }
            return text;
        }

        public static List<PlanStep> FromReply(Dictionary<string, object> reply)
        {
            var plan = new List<PlanStep>();
            foreach (object node in Json.List(reply, "plan"))
            {
                Dictionary<string, object> step = Json.Obj(node);
                string action = Json.Str(step, "do");
                if (action.Length > 0 && action != "none")
                {
                    plan.Add(new PlanStep(action, Json.Str(step, "what").Trim(), (int)Json.Num(step, "count"))
                    {
                        Kind = Json.Str(step, "kind").Trim(),
                        Note = Json.Str(step, "note").Trim(),
                    });
                }
            }
            // Старый мост отвечал одним действием
            if (plan.Count == 0)
            {
                string action = Json.Str(reply, "action", "none");
                if (action != "none" && action.Length > 0)
                {
                    plan.Add(new PlanStep(action));
                }
            }
            return plan;
        }

        public static string Describe(IEnumerable<PlanStep> plan)
        {
            return string.Join(", ", plan.Select(s => s.Russian()));
        }

        // Для сводки модели и для подсказки над головой
        public string Russian()
        {
            string what = What.Length > 0 ? " " + What : "";
            string count = Count > 0 ? $" ×{Count}" : "";
            switch (Do)
            {
                case "follow":
                case "come": return "идти за игроком";
                case "stay": return "сторожить место";
                case "attack": return "напасть на врага";
                case "gather_wood": return "рубить лес" + count;
                case "mine": return "добывать камень" + what + count;
                case "pick": return "собирать" + (what.Length > 0 ? what : " травы и ягоды") + count;
                case "hunt": return "охотиться" + (what.Length > 0 ? " на" + what : "") + count;
                case "loot": return "подбирать вещи";
                case "give_items": return "отдать игроку" + (what.Length > 0 ? what : " добычу");
                case "store": return "сложить в сундук" + what;
                case "fetch": return "взять из сундука" + what + count;
                case "combat_style": return What == "bow" ? "биться луком" : "биться топором";
                case "mark_place": return "запомнить место" + what;
                case "forget_place": return "забыть место" + what;
                case "guard": return "сторожить" + (what.Length > 0 ? what : " место");
                case "repair": return "чинить постройки" + (what.Length > 0 ? " —" + what : "");
                case "smelt": return "загрузить печи" + what + count;
                case "collect": return "забрать готовое из печей" + what;
                default: return Do;
            }
        }
    }
}
