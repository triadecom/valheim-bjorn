using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace VikingCompanion
{
    // JsonUtility из сборки мода молча теряет вложенные объекты ({"state":{}}),
    // поэтому запросы к мосту собираем сами: публичные поля, строки, числа, массивы, вложенные классы.
    internal static class Json
    {
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
