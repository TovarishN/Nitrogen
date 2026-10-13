using System.Globalization;
using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Dependency-free JSON decoding for registered analyzer configuration inputs.</summary>
internal sealed class ConfigurationJson
{
    readonly string _text;
    int _position;
    ConfigurationJson(string text) => _text = text;

    internal static Dictionary<string, object?> Read(string text)
    {
        var reader = new ConfigurationJson(text);
        var value = reader.Value(0) as Dictionary<string, object?> ?? throw new FormatException("expected a JSON object");
        reader.White();
        if (reader._position != text.Length) throw new FormatException("unexpected text after JSON object");
        return value;
    }

    object? Value(int depth)
    {
        if (depth > 64) throw new FormatException("JSON nesting exceeds 64");
        White();
        if (_position >= _text.Length) throw new FormatException("incomplete JSON value");
        char c = _text[_position];
        if (c == '"') return String();
        if (c == '{')
        {
            _position++;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (Take('}')) return result;
            do
            {
                White();
                string key = String();
                Require(':');
                result[key] = Value(depth + 1);
                if (Take('}')) return result;
            } while (Take(','));
            throw new FormatException("expected ',' or '}'");
        }
        if (c == '[')
        {
            _position++;
            var result = new List<object?>();
            if (Take(']')) return result;
            do
            {
                result.Add(Value(depth + 1));
                if (Take(']')) return result;
            } while (Take(','));
            throw new FormatException("expected ',' or ']' ");
        }
        foreach (string literal in new[] { "true", "false", "null" })
            if (_text.Substring(_position).StartsWith(literal, StringComparison.Ordinal))
            {
                _position += literal.Length;
                return literal == "null" ? null : (object)(literal == "true");
            }
        int start = _position;
        if (c == '-') _position++;
        if (_position < _text.Length && _text[_position] == '0') _position++;
        else Digits();
        if (_position < _text.Length && _text[_position] == '.') { _position++; Digits(); }
        if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
        {
            _position++;
            if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-')) _position++;
            Digits();
        }
        return new Number(_text.Substring(start, _position - start));
    }

    sealed record Number(string Text);

    void Digits()
    {
        int start = _position;
        while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9') _position++;
        if (_position == start) throw new FormatException("expected JSON value or number digit");
    }

    string String()
    {
        if (_position >= _text.Length || _text[_position++] != '"') throw new FormatException("expected JSON string");
        var result = new StringBuilder();
        while (_position < _text.Length)
        {
            char c = _text[_position++];
            if (c == '"') return result.ToString();
            if (c < ' ') throw new FormatException("unescaped control character in JSON string");
            if (c != '\\') { result.Append(c); continue; }
            if (_position >= _text.Length) break;
            c = _text[_position++];
            switch (c)
            {
                case '"': case '\\': case '/': result.Append(c); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case 'u':
                    if (_position + 4 > _text.Length || !ushort.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                        throw new FormatException("invalid JSON unicode escape");
                    _position += 4;
                    result.Append((char)code);
                    break;
                default: throw new FormatException("invalid JSON escape");
            }
        }
        throw new FormatException("unterminated JSON string");
    }

    void White()
    {
        while (_position < _text.Length && (_text[_position] == ' ' || _text[_position] == '\r' || _text[_position] == '\n' || _text[_position] == '\t')) _position++;
    }
    bool Take(char c)
    {
        White();
        if (_position >= _text.Length || _text[_position] != c) return false;
        _position++;
        return true;
    }
    void Require(char c)
    {
        if (!Take(c)) throw new FormatException("expected '" + c + "'");
    }
}
