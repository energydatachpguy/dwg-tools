// Minimal JSON writer/reader, so the plugin builds the same for
// .NET Framework 4.x (AutoCAD 2020-2024) and .NET 8 (AutoCAD 2025+), where
// System.Web.Script.Serialization does not exist.
//
// Writes: null, bool, string, char, enums (as numbers), all numeric types,
// DateTime (ISO 8601), IDictionary, IEnumerable, other objects by public
// properties. Reads: objects -> Dictionary<string, object>, arrays ->
// List<object>, numbers -> int / long / double, true/false/null, strings.
// C# 5 only (stock csc.exe).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DwgFlat
{
    public static class Json
    {
        public static string Serialize(object o)
        {
            StringBuilder sb = new StringBuilder(256);
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth)
        {
            if (depth > 256) throw new InvalidOperationException("JSON nesting too deep");
            if (o == null || o is DBNull) { sb.Append("null"); return; }
            if (o is string) { Str(sb, (string)o); return; }
            if (o is char) { Str(sb, o.ToString()); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is Enum) { sb.Append(Convert.ToInt64(o, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float)
            {
                double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (o is int || o is long || o is short || o is byte || o is uint || o is ulong || o is ushort || o is sbyte || o is decimal)
            { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is DateTime) { Str(sb, ((DateTime)o).ToString("s", CultureInfo.InvariantCulture)); return; }
            IDictionary dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Str(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value, depth + 1);
                }
                sb.Append('}'); return;
            }
            IEnumerable list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('['); bool first = true;
                foreach (object x in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, x, depth + 1);
                }
                sb.Append(']'); return;
            }
            // Any other object: its public readable properties.
            sb.Append('{'); bool f = true;
            foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(o, null); } catch { continue; }
                if (!f) sb.Append(',');
                f = false;
                Str(sb, p.Name); sb.Append(':'); Write(sb, v, depth + 1);
            }
            sb.Append('}');
        }

        static void Str(StringBuilder sb, string s)
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // --- reader ---
        public static object Parse(string text)
        {
            int i = 0;
            object v = Value(text, ref i, 0);
            Ws(text, ref i);
            if (i < text.Length) throw new FormatException("Unexpected text after JSON at " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
            if (d == null) throw new FormatException("JSON is not an object");
            return d;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == (char)0xFEFF)) i++;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 256) throw new FormatException("JSON nesting too deep");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a name at " + i);
                    string k = ReadStr(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                    i++;
                    d[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                List<object> l = new List<object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return ReadStr(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character '" + c + "' at " + i);
            string num = s.Substring(start, i - start);
            if (num.IndexOfAny(new char[] { '.', 'e', 'E' }) < 0)
            {
                long n;
                if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                {
                    if (n >= int.MinValue && n <= int.MaxValue) return (int)n;
                    return n;
                }
            }
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) == 0) { i += w.Length; return true; }
            return false;
        }

        static string ReadStr(string s, ref int i)
        {
            i++;   // opening quote
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4; break;
                    default: throw new FormatException("Bad escape \\" + e);
                }
            }
            throw new FormatException("Unterminated string in JSON");
        }
    }
}
