using System;
using System.Collections.Generic;
using System.Text;

namespace KspContinuum
{
    public static class StartupCacheManifestReader
    {
        public static string[] ReadBundleObjects(string json)
        {
            var reader = new Reader(json);
            Value root = reader.Parse();
            Value array;
            if (root.Fields == null || !root.Fields.TryGetValue("bundles", out array) || array.Items == null || array.Items.Count != 3)
                throw new FormatException("Manifest requires exactly three bundle objects.");
            var result = new string[3];
            for (int i = 0; i < result.Length; i++)
            {
                Value item = array.Items[i];
                if (item.Fields == null) throw new FormatException("Bundle entry must be an object.");
                result[i] = json.Substring(item.Start, item.End - item.Start);
            }
            return result;
        }
        sealed class Value
        {
            public int Start, End;
            public Dictionary<string, Value> Fields;
            public List<Value> Items;
        }
        sealed class Reader
        {
            readonly string json;
            int position;
            public Reader(string text)
            {
                if (text == null || text.Length > 65536) throw new FormatException("Manifest size is invalid.");
                json = text;
            }
            public Value Parse()
            {
                Value result = Read(0); Space();
                if (position != json.Length) throw new FormatException("Trailing manifest content.");
                return result;
            }
            Value Read(int depth)
            {
                if (depth > 16) throw new FormatException("Manifest nesting limit exceeded.");
                Space(); var value = new Value { Start = position };
                char first = Peek();
                if (first == '{')
                {
                    position++; value.Fields = new Dictionary<string, Value>(StringComparer.Ordinal); Space();
                    if (!Take('}'))
                        while (true)
                        {
                            Space(); string key = String(); Space(); Need(':');
                            if (value.Fields.Count >= 128 || value.Fields.ContainsKey(key)) throw new FormatException("Duplicate field or member limit.");
                            value.Fields.Add(key, Read(depth + 1)); Space();
                            if (Take('}')) break; Need(',');
                        }
                }
                else if (first == '[')
                {
                    position++; value.Items = new List<Value>(); Space();
                    if (!Take(']'))
                        while (true)
                        {
                            if (value.Items.Count >= 128) throw new FormatException("Manifest array limit exceeded.");
                            value.Items.Add(Read(depth + 1)); Space(); if (Take(']')) break; Need(',');
                        }
                }
                else if (first == '"') String();
                else if (first == 't') Literal("true");
                else if (first == 'f') Literal("false");
                else if (first == 'n') Literal("null");
                else Number();
                value.End = position; return value;
            }
            string String()
            {
                Need('"'); var text = new StringBuilder();
                while (true)
                {
                    char next = Peek(); position++;
                    if (next == '"') return text.ToString();
                    if (next < 32) throw new FormatException("Control character in JSON string.");
                    if (next == '\\')
                    {
                        next = Peek(); position++;
                        switch (next)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': next = '\b'; break;
                            case 'f': next = '\f'; break;
                            case 'n': next = '\n'; break;
                            case 'r': next = '\r'; break;
                            case 't': next = '\t'; break;
                            case 'u':
                                int code = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    char digit = Peek(); position++;
                                    int hex = digit >= '0' && digit <= '9' ? digit - '0' : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10 : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10 : -1;
                                    if (hex < 0) throw new FormatException("Invalid JSON unicode escape.");
                                    code = code * 16 + hex;
                                }
                                next = (char)code; break;
                            default: throw new FormatException("Invalid JSON escape.");
                        }
                    }
                    text.Append(next);
                    if (text.Length > 8192) throw new FormatException("Manifest string limit exceeded.");
                }
            }
            void Number()
            {
                Take('-');
                if (!Take('0'))
                {
                    char digit = Peek();
                    if (digit < '1' || digit > '9') throw new FormatException("Invalid JSON value.");
                    position++; Digits(false);
                }
                if (Take('.')) Digits(true);
                if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); Digits(true); }
            }
            void Digits(bool required)
            {
                int start = position;
                while (position < json.Length && json[position] >= '0' && json[position] <= '9') position++;
                if (required && position == start) throw new FormatException("Missing JSON number digits.");
            }
            void Literal(string value)
            {
                foreach (char expected in value) Need(expected);
            }
            char Peek()
            {
                if (position >= json.Length) throw new FormatException("Truncated JSON manifest.");
                return json[position];
            }
            bool Take(char value)
            {
                if (position >= json.Length || json[position] != value) return false;
                position++; return true;
            }
            void Need(char value) { if (!Take(value)) throw new FormatException("Unexpected JSON token."); }
            void Space() { while (position < json.Length && (json[position] == ' ' || json[position] == '\r' || json[position] == '\n' || json[position] == '\t')) position++; }
        }
    }
}
