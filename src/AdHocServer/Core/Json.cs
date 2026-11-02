using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dpt.AdHoc.Core
{
    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>
    /// Minimal, dependency free JSON support. .NET 4.0 has no System.Text.Json and we
    /// refuse to drag NuGet packages into an XP capable build.
    /// </summary>
    public static class Json
    {
        public static string Quote(string value)
        {
            if (value == null) return "null";
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- parse

        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("empty input");
            int pos = 0;
            object value = ParseValue(text, ref pos, 0);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw new JsonException("trailing data at " + pos);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
            if (d == null) throw new JsonException("root is not an object");
            return d;
        }

        private static object ParseValue(string s, ref int pos, int depth)
        {
            if (depth > 64) throw new JsonException("nesting too deep");
            SkipWs(s, ref pos);
            if (pos >= s.Length) throw new JsonException("unexpected end");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObj(s, ref pos, depth);
                case '[': return ParseArr(s, ref pos, depth);
                case '"': return ParseStr(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNum(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseObj(string s, ref int pos, int depth)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            pos++; // {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return d; }
            while (true)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new JsonException("expected key at " + pos);
                string key = ParseStr(s, ref pos);
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new JsonException("expected ':' at " + pos);
                pos++;
                d[key] = ParseValue(s, ref pos, depth + 1);
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new JsonException("unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return d; }
                throw new JsonException("expected ',' or '}' at " + pos);
            }
        }

        private static List<object> ParseArr(string s, ref int pos, int depth)
        {
            List<object> list = new List<object>();
            pos++; // [
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos, depth + 1));
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new JsonException("unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new JsonException("expected ',' or ']' at " + pos);
            }
        }

        private static string ParseStr(string s, ref int pos)
        {
            pos++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
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
                        if (pos + 4 > s.Length) throw new JsonException("bad \\u escape");
                        int code;
                        if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new JsonException("bad \\u escape");
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default: throw new JsonException("bad escape '\\" + e + "'");
                }
            }
            throw new JsonException("unterminated string");
        }

        private static object ParseNum(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && ((s[pos] >= '0' && s[pos] <= '9') || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-')) pos++;
            string raw = s.Substring(start, pos - start);
            if (raw.Length == 0) throw new JsonException("unexpected character at " + start);
            long l;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new JsonException("bad number '" + raw + "'");
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new JsonException("expected " + literal + " at " + pos);
            pos += literal.Length;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { pos++; continue; }
                break;
            }
        }

        // ------------------------------------------------------------- helpers

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v = Get(d, key);
            return v == null ? def : (v as string) ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v = Get(d, key);
            if (v == null) return def;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;
            int r;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v = Get(d, key);
            if (v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return def;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        public static string Stringify(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            string str = value as string;
            if (str != null) { sb.Append(Quote(str)); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(Number((double)value)); return; }
            if (value is DateTime) { sb.Append(Quote(((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))); return; }

            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(kv.Key)).Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            IEnumerable seq = value as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, o);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Streaming JSON writer used by the API. Cheap, allocation friendly, no reflection.</summary>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _sb;
        private bool _needComma;
        private int _depth;

        public JsonBuilder() : this(768) { }

        public JsonBuilder(int capacity) { _sb = new StringBuilder(capacity); }

        public JsonBuilder BeginObject() { Sep(); _sb.Append('{'); _depth++; _needComma = false; return this; }
        public JsonBuilder EndObject() { _sb.Append('}'); _depth--; _needComma = true; return this; }
        public JsonBuilder BeginArray() { Sep(); _sb.Append('['); _depth++; _needComma = false; return this; }
        public JsonBuilder EndArray() { _sb.Append(']'); _depth--; _needComma = true; return this; }

        public JsonBuilder Name(string name)
        {
            Sep();
            _sb.Append(Json.Quote(name)).Append(':');
            _needComma = false;
            return this;
        }

        public JsonBuilder Str(string value) { Sep(); _sb.Append(value == null ? "null" : Json.Quote(value)); _needComma = true; return this; }
        public JsonBuilder Num(long value) { Sep(); _sb.Append(Json.Number(value)); _needComma = true; return this; }
        public JsonBuilder Num(double value) { Sep(); _sb.Append(Json.Number(value)); _needComma = true; return this; }
        public JsonBuilder Bool(bool value) { Sep(); _sb.Append(value ? "true" : "false"); _needComma = true; return this; }
        public JsonBuilder Null() { Sep(); _sb.Append("null"); _needComma = true; return this; }
        public JsonBuilder Raw(string json) { Sep(); _sb.Append(json); _needComma = true; return this; }

        public JsonBuilder Prop(string name, string value) { return Name(name).Str(value); }
        public JsonBuilder Prop(string name, long value) { return Name(name).Num(value); }
        public JsonBuilder Prop(string name, int value) { return Name(name).Num((long)value); }
        public JsonBuilder Prop(string name, double value) { return Name(name).Num(value); }
        public JsonBuilder Prop(string name, bool value) { return Name(name).Bool(value); }

        private void Sep()
        {
            if (_needComma) _sb.Append(',');
            _needComma = false;
        }

        public int Depth { get { return _depth; } }

        public override string ToString() { return _sb.ToString(); }
    }
}