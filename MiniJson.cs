using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZipSaber
{
    /// <summary>
    /// Tiny JSON reader: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string/bool/null. Enough for the BeatMods API without a dependency.
    /// </summary>
    internal static class MiniJson
    {
        internal static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            var v = Value(json, ref i);
            return v;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (c == 't' && Match(s, ref i, "true"))  return true;
            if (c == 'f' && Match(s, ref i, "false")) return false;
            if (c == 'n' && Match(s, ref i, "null"))  return null;
            return Num(s, ref i);
        }

        private static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"Bad token at {i}");
            i += word.Length;
            return true;
        }

        private static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string key = Str(s, ref i);
                Ws(s, ref i);
                if (s[i] != ':') throw new FormatException($"Expected ':' at {i}");
                i++;
                d[key] = Value(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException($"Expected ',' or '}}' at {i}");
            }
        }

        private static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException($"Expected ',' or ']' at {i}");
            }
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException($"Expected string at {i}");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
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
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string");
        }

        private static double Num(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException($"Bad value at {i}");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ── Convenience getters ─────────────────────────────────────────────────
        internal static string GetString(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as string : null;

        internal static bool GetBool(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) && v is bool b && b;

        internal static List<object> GetList(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as List<object> : null;

        internal static Dictionary<string, object> GetObj(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
    }
}
