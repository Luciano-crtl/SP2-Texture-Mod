using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SP2TextureMod
{
    /// <summary>
    /// A small JSON reader (the game's runtime has no JSON library that can read arbitrary documents).
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, strings string, true/false bool,
    /// null stays null. Used for the JSON part of glTF files.
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text, out string error)
        {
            error = null;
            try
            {
                int pos = 0;
                SkipSpace(text, ref pos);
                object value = ReadValue(text, ref pos, 0);
                SkipSpace(text, ref pos);
                if (pos != text.Length)
                {
                    error = "unexpected text after the JSON value at character " + pos;
                    return null;
                }

                return value;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ---------------------------------------------------------------- reading helpers

        public static Dictionary<string, object> Object(object value, string key)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner))
            {
                return inner as Dictionary<string, object>;
            }

            return null;
        }

        public static List<object> Array(object value, string key)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner))
            {
                return inner as List<object>;
            }

            return null;
        }

        public static int Int(object value, string key, int fallback)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner) && inner is double)
            {
                return (int)(double)inner;
            }

            return fallback;
        }

        public static double Number(object value, string key, double fallback)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner) && inner is double)
            {
                return (double)inner;
            }

            return fallback;
        }

        public static string String(object value, string key)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner))
            {
                return inner as string;
            }

            return null;
        }

        public static bool Bool(object value, string key, bool fallback)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object inner;
            if (d != null && d.TryGetValue(key, out inner) && inner is bool)
            {
                return (bool)inner;
            }

            return fallback;
        }

        public static bool Has(object value, string key)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            return d != null && d.ContainsKey(key);
        }

        // ---------------------------------------------------------------- parser

        private static object ReadValue(string t, ref int p, int depth)
        {
            if (depth > 200)
            {
                throw new FormatException("the JSON is nested too deeply");
            }

            if (p >= t.Length)
            {
                throw new FormatException("the JSON ends too early");
            }

            char c = t[p];
            if (c == '{')
            {
                p++;
                Dictionary<string, object> result = new Dictionary<string, object>();
                SkipSpace(t, ref p);
                if (p < t.Length && t[p] == '}')
                {
                    p++;
                    return result;
                }

                while (true)
                {
                    SkipSpace(t, ref p);
                    if (p >= t.Length || t[p] != '"')
                    {
                        throw new FormatException("a key was expected at character " + p);
                    }

                    string key = ReadString(t, ref p);
                    SkipSpace(t, ref p);
                    if (p >= t.Length || t[p] != ':')
                    {
                        throw new FormatException("':' was expected at character " + p);
                    }

                    p++;
                    SkipSpace(t, ref p);
                    result[key] = ReadValue(t, ref p, depth + 1);
                    SkipSpace(t, ref p);
                    if (p < t.Length && t[p] == ',')
                    {
                        p++;
                        continue;
                    }

                    if (p < t.Length && t[p] == '}')
                    {
                        p++;
                        return result;
                    }

                    throw new FormatException("',' or '}' was expected at character " + p);
                }
            }

            if (c == '[')
            {
                p++;
                List<object> list = new List<object>();
                SkipSpace(t, ref p);
                if (p < t.Length && t[p] == ']')
                {
                    p++;
                    return list;
                }

                while (true)
                {
                    SkipSpace(t, ref p);
                    list.Add(ReadValue(t, ref p, depth + 1));
                    SkipSpace(t, ref p);
                    if (p < t.Length && t[p] == ',')
                    {
                        p++;
                        continue;
                    }

                    if (p < t.Length && t[p] == ']')
                    {
                        p++;
                        return list;
                    }

                    throw new FormatException("',' or ']' was expected at character " + p);
                }
            }

            if (c == '"')
            {
                return ReadString(t, ref p);
            }

            if (string.CompareOrdinal(t, p, "true", 0, 4) == 0)
            {
                p += 4;
                return true;
            }

            if (string.CompareOrdinal(t, p, "false", 0, 5) == 0)
            {
                p += 5;
                return false;
            }

            if (string.CompareOrdinal(t, p, "null", 0, 4) == 0)
            {
                p += 4;
                return null;
            }

            int start = p;
            while (p < t.Length && (char.IsDigit(t[p]) || t[p] == '-' || t[p] == '+' || t[p] == '.' || t[p] == 'e' || t[p] == 'E'))
            {
                p++;
            }

            double number;
            if (p == start || !double.TryParse(t.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw new FormatException("a value was expected at character " + start);
            }

            return number;
        }

        private static string ReadString(string t, ref int p)
        {
            p++; // the opening quote
            StringBuilder sb = null;
            int start = p;
            while (p < t.Length)
            {
                char c = t[p];
                if (c == '"')
                {
                    string piece = t.Substring(start, p - start);
                    p++;
                    return sb == null ? piece : sb.Append(piece).ToString();
                }

                if (c == '\\')
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder();
                    }

                    sb.Append(t, start, p - start);
                    p++;
                    if (p >= t.Length)
                    {
                        break;
                    }

                    char e = t[p];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (p + 4 >= t.Length)
                            {
                                throw new FormatException("a \\u escape is cut short");
                            }

                            sb.Append((char)int.Parse(t.Substring(p + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            p += 4;
                            break;
                        default: sb.Append(e); break;
                    }

                    p++;
                    start = p;
                    continue;
                }

                p++;
            }

            throw new FormatException("a string is not closed");
        }

        private static void SkipSpace(string t, ref int p)
        {
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t' || t[p] == '\n' || t[p] == '\r'))
            {
                p++;
            }
        }
    }
}
