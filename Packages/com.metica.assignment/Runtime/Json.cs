using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Metica.Assignment {
    /// <summary>
    /// A JSON number kept as its source text, so that decimal values are not rounded through double.
    /// </summary>
    internal sealed class JsonNumber {
        public string Raw { get; }

        public JsonNumber(string raw) {
            Raw = raw;
        }

        public bool TryGetDecimal(out decimal value) {
            return decimal.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>long for integer literals that fit, double otherwise.</summary>
        public object ToLongOrDouble() {
            bool isInteger = Raw.IndexOf('.') < 0 && Raw.IndexOf('e') < 0 && Raw.IndexOf('E') < 0;
            if (isInteger && long.TryParse(Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) {
                return l;
            }
            return double.Parse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Minimal strict JSON reader and writer, so the runtime assembly has no dependencies.
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;, numbers to JsonNumber.
    /// Duplicate object keys are rejected.
    /// </summary>
    internal static class Json {
        private const int MaxDepth = 64;

        public static bool TryParse(string text, out object value, out string error) {
            value = null;
            error = null;
            if (text == null) {
                error = "null input";
                return false;
            }
            try {
                var parser = new Parser(text);
                value = parser.ParseDocument();
                return true;
            } catch (FormatException e) {
                error = e.Message;
                return false;
            }
        }

        public static string Write(object value) {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value) {
            switch (value) {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    if (b) {
                        sb.Append("true");
                    } else {
                        sb.Append("false");
                    }
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) {
                        throw new ArgumentException("JSON cannot hold NaN or Infinity");
                    }
                    string text = d.ToString("R", CultureInfo.InvariantCulture);
                    sb.Append(text);
                    // Keep a decimal point on whole numbers so a double reads back as a double, not a long.
                    if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0) {
                        sb.Append(".0");
                    }
                    break;
                case JsonNumber n:
                    sb.Append(n.Raw);
                    break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool firstPair = true;
                    foreach (KeyValuePair<string, object> pair in dict) {
                        if (!firstPair) {
                            sb.Append(',');
                        }
                        firstPair = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        WriteValue(sb, pair.Value);
                    }
                    sb.Append('}');
                    break;
                case IList list:
                    sb.Append('[');
                    for (int index = 0; index < list.Count; index++) {
                        if (index > 0) {
                            sb.Append(',');
                        }
                        WriteValue(sb, list[index]);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException("Unsupported JSON value type: " + value.GetType());
            }
        }

        private static void WriteString(StringBuilder sb, string s) {
            sb.Append('"');
            foreach (char c in s) {
                switch (c) {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20) {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        } else {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser {
            private readonly string _s;
            private int _i;
            private int _depth;

            public Parser(string s) {
                _s = s;
            }

            public object ParseDocument() {
                object value = ParseValue();
                SkipWhitespace();
                if (_i != _s.Length) {
                    throw Fail("unexpected trailing characters");
                }
                return value;
            }

            private object ParseValue() {
                SkipWhitespace();
                if (_i >= _s.Length) {
                    throw Fail("unexpected end of input");
                }
                char c = _s[_i];
                switch (c) {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                        ExpectLiteral("true");
                        return true;
                    case 'f':
                        ExpectLiteral("false");
                        return false;
                    case 'n':
                        ExpectLiteral("null");
                        return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) {
                            return ParseNumber();
                        }
                        throw Fail("unexpected character '" + c + "'");
                }
            }

            private Dictionary<string, object> ParseObject() {
                EnterNested();
                _i++; // '{'
                var result = new Dictionary<string, object>();
                SkipWhitespace();
                if (Peek() == '}') {
                    _i++;
                    _depth--;
                    return result;
                }
                while (true) {
                    SkipWhitespace();
                    if (Peek() != '"') {
                        throw Fail("expected a string key");
                    }
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    object value = ParseValue();
                    if (result.ContainsKey(key)) {
                        throw Fail("duplicate key \"" + key + "\"");
                    }
                    result.Add(key, value);
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') {
                        _i++;
                        continue;
                    }
                    if (c == '}') {
                        _i++;
                        _depth--;
                        return result;
                    }
                    throw Fail("expected ',' or '}'");
                }
            }

            private List<object> ParseArray() {
                EnterNested();
                _i++; // '['
                var result = new List<object>();
                SkipWhitespace();
                if (Peek() == ']') {
                    _i++;
                    _depth--;
                    return result;
                }
                while (true) {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') {
                        _i++;
                        continue;
                    }
                    if (c == ']') {
                        _i++;
                        _depth--;
                        return result;
                    }
                    throw Fail("expected ',' or ']'");
                }
            }

            private string ParseString() {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true) {
                    if (_i >= _s.Length) {
                        throw Fail("unterminated string");
                    }
                    char c = _s[_i++];
                    if (c == '"') {
                        return sb.ToString();
                    }
                    if (c < 0x20) {
                        throw Fail("control character in string");
                    }
                    if (c != '\\') {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length) {
                        throw Fail("unterminated escape");
                    }
                    char e = _s[_i++];
                    switch (e) {
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        case '/':
                            sb.Append('/');
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            if (_i + 4 > _s.Length) {
                                throw Fail("truncated \\u escape");
                            }
                            string hex = _s.Substring(_i, 4);
                            if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)) {
                                throw Fail("invalid \\u escape");
                            }
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw Fail("invalid escape '\\" + e + "'");
                    }
                }
            }

            // -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?
            private JsonNumber ParseNumber() {
                int start = _i;
                if (Peek() == '-') {
                    _i++;
                }
                if (Peek() == '0') {
                    _i++;
                } else if (IsDigit(Peek())) {
                    ReadDigits();
                } else {
                    throw Fail("invalid number");
                }
                if (Peek() == '.') {
                    _i++;
                    if (!IsDigit(Peek())) {
                        throw Fail("invalid number");
                    }
                    ReadDigits();
                }
                if (Peek() == 'e' || Peek() == 'E') {
                    _i++;
                    if (Peek() == '+' || Peek() == '-') {
                        _i++;
                    }
                    if (!IsDigit(Peek())) {
                        throw Fail("invalid number");
                    }
                    ReadDigits();
                }
                return new JsonNumber(_s.Substring(start, _i - start));
            }

            private void ReadDigits() {
                while (IsDigit(Peek())) {
                    _i++;
                }
            }

            private static bool IsDigit(char c) {
                return c >= '0' && c <= '9';
            }

            private void ExpectLiteral(string literal) {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0) {
                    throw Fail("invalid literal");
                }
                _i += literal.Length;
            }

            private void Expect(char c) {
                if (Peek() != c) {
                    throw Fail("expected '" + c + "'");
                }
                _i++;
            }

            private char Peek() {
                if (_i < _s.Length) {
                    return _s[_i];
                }
                return '\0';
            }

            private void SkipWhitespace() {
                while (_i < _s.Length) {
                    char c = _s[_i];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') {
                        return;
                    }
                    _i++;
                }
            }

            private void EnterNested() {
                _depth++;
                if (_depth > MaxDepth) {
                    throw Fail("nesting too deep");
                }
            }

            private FormatException Fail(string message) {
                return new FormatException(message + " at position " + _i);
            }
        }
    }
}
