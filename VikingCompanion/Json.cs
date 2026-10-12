using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace VikingCompanion
{
    // JsonUtility из сборки мода молча теряет вложенные объекты ({"state":{}}),
    // поэтому запросы к мосту собираем сами: публичные поля, строки, числа, массивы, вложенные классы.
    // Ответы моста разбираем тоже сами: в Dictionary, List, string, double, bool и null.
    internal static class Json
    {
        // ---------- Разбор ----------

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            int i = 0;
            try
            {
                return ReadValue(text, ref i);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, object> Obj(object node) => node as Dictionary<string, object>;

        public static string Str(Dictionary<string, object> obj, string key, string fallback = "")
        {
            return obj != null && obj.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }

        public static double Num(Dictionary<string, object> obj, string key, double fallback = 0)
        {
            return obj != null && obj.TryGetValue(key, out object v) && v is double d ? d : fallback;
        }

        public static bool Bool(Dictionary<string, object> obj, string key, bool fallback = false)
        {
            return obj != null && obj.TryGetValue(key, out object v) && v is bool b ? b : fallback;
        }

        public static List<object> List(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out object v) && v is List<object> l ? l : new List<object>();
        }

        private static void SkipSpace(string t, ref int i)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i]))
            {
                i++;
            }
        }

        private static object ReadValue(string t, ref int i)
        {
            SkipSpace(t, ref i);
            char c = t[i];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>();
                i++;
                SkipSpace(t, ref i);
                if (t[i] == '}')
                {
                    i++;
                    return obj;
                }
                while (true)
                {
                    SkipSpace(t, ref i);
                    string key = ReadString(t, ref i);
                    SkipSpace(t, ref i);
                    i++;   // ':'
                    obj[key] = ReadValue(t, ref i);
                    SkipSpace(t, ref i);
                    if (t[i++] == '}')
                    {
                        return obj;
                    }
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                SkipSpace(t, ref i);
                if (t[i] == ']')
                {
                    i++;
                    return list;
                }
                while (true)
                {
                    list.Add(ReadValue(t, ref i));
                    SkipSpace(t, ref i);
                    if (t[i++] == ']')
                    {
                        return list;
                    }
                }
            }
            if (c == '"')
            {
                return ReadString(t, ref i);
            }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "true", 0, 4) == 0)
            {
                i += 4;
                return true;
            }
            if (t.Length - i >= 5 && string.CompareOrdinal(t, i, "false", 0, 5) == 0)
            {
                i += 5;
                return false;
            }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "null", 0, 4) == 0)
            {
                i += 4;
                return null;
            }
            int start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0)
            {
                i++;
            }
            return double.Parse(t.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string ReadString(string t, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // открывающая кавычка
            while (true)
            {
                char c = t[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = t[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
        }

        // ---------- Запись ----------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(512);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("0.###", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("0.###", CultureInfo.InvariantCulture));
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem)
                        {
                            sb.Append(',');
                        }
                        firstItem = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
            }

            sb.Append('{');
            bool first = true;
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                WriteString(sb, field.Name);
                sb.Append(':');
                Write(sb, field.GetValue(value));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
