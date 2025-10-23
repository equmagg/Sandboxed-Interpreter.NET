using System;
using System.Text;

namespace Interpretor
{
    public partial class Lexer
    {
        private readonly string _text;
        private int _position;
        public Lexer(string text) => _text = text;
        public int GetCodeLength() => _text.Length;
        public char GetAtPosition(int pos) => _text[pos];
        public Ast.TokenType CurrentTokenType { get; private set; }
        public string CurrentTokenText { get; private set; } = string.Empty;

        
        public void ResetCurrent(int pos, Ast.TokenType token, string text)
        {
            _position = pos;
            CurrentTokenType = token;
            CurrentTokenText = text;
        }
        public int Position => _position;
        public void NextToken()
        {
            SkipWhitespace();

            if (_position >= _text.Length)
            {
                CurrentTokenType = Ast.TokenType.EndOfInput;
                return;
            }

            char current = _text[_position];

            //Number literal
            if (char.IsDigit(current))
            {
                static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                int start = _position;
                if (current == '0' && _position + 1 < _text.Length && (_text[_position + 1] == 'x' || _text[_position + 1] == 'X'))
                {
                    _position += 2;
                    int digitsStart = _position;
                    while (_position < _text.Length && (IsHexDigit(_text[_position]) || _text[_position] == '_'))
                        _position++;
                    int sufStart = _position;
                    if (_position < _text.Length)
                    {
                        char c1 = _text[_position];
                        char c2 = _position + 1 < _text.Length ? _text[_position + 1] : '\0';
                        if ((c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                            && (c2 == 'u' || c2 == 'U' || c2 == 'l' || c2 == 'L')
                            && char.ToLowerInvariant(c1) != char.ToLowerInvariant(c2))
                        {
                            _position += 2;
                        }
                        else if (c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                        {
                            _position += 1;
                        }
                    }
                    CurrentTokenText = _text.Substring(start, _position - start);
                    CurrentTokenType = Ast.TokenType.Number;
                    return;
                }
                if (current == '0' && _position + 1 < _text.Length && (_text[_position + 1] == 'b' || _text[_position + 1] == 'B'))
                {
                    _position += 2;
                    while (_position < _text.Length)
                    {
                        char c = _text[_position];
                        if (c == '0' || c == '1' || c == '_') { _position++; continue; }
                        break;
                    }
                    if (_position < _text.Length)
                    {
                        char c1 = _text[_position];
                        char c2 = _position + 1 < _text.Length ? _text[_position + 1] : '\0';
                        if ((c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                            && (c2 == 'u' || c2 == 'U' || c2 == 'l' || c2 == 'L')
                            && char.ToLowerInvariant(c1) != char.ToLowerInvariant(c2))
                        {
                            _position += 2;
                        }
                        else if (c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                        {
                            _position += 1;
                        }
                    }
                    CurrentTokenText = _text.Substring(start, _position - start);
                    CurrentTokenType = Ast.TokenType.Number;
                    return;
                }
                while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_')) _position++;
                if (_position < _text.Length && _text[_position] == '.' && !(_position + 1 < _text.Length && _text[_position + 1] == '.'))
                {
                    _position++; //skip .
                    while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_')) _position++;
                }
                if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
                {
                    int save = _position;
                    _position++;
                    if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
                        _position++;
                    bool any = false;
                    while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_'))
                    {
                        _position++;
                        any = true;
                    }
                    if (!any) _position = save;
                }
                if (_position < _text.Length)
                {
                    char c1 = _text[_position];
                    char c2 = _position + 1 < _text.Length ? _text[_position + 1] : '\0';
                    if (c1 is 'f' or 'F' or 'd' or 'D' or 'm' or 'M')
                    {
                        _position++;
                    }
                    else if ((c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                        && (c2 == 'u' || c2 == 'U' || c2 == 'l' || c2 == 'L')
                        && char.ToLowerInvariant(c1) != char.ToLowerInvariant(c2))
                    {
                        _position += 2;
                    }
                    else if (c1 == 'u' || c1 == 'U' || c1 == 'l' || c1 == 'L')
                    {
                        _position += 1;
                    }
                }
                CurrentTokenText = _text.Substring(start, _position - start);
                CurrentTokenType = Ast.TokenType.Number;
                return;
            }

            //Identifier or keyword
            if (char.IsLetter(current) || current == '_')
            {
                int start = _position;
                while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
                    _position++;
                CurrentTokenText = _text.Substring(start, _position - start);
                CurrentTokenType = Keywords.Contains(CurrentTokenText) ? Ast.TokenType.Keyword : Ast.TokenType.Identifier;
                return;
            }
            //Combined string
            if (_position + 2 < _text.Length)
            {
                if ((current == '$' && _text[_position + 1] == '@' && _text[_position + 2] == '"')
                    || (current == '@' && _text[_position + 1] == '$' && _text[_position + 2] == '"'))
                {
                    _position += 3; // skip $@"
                    var sb = new StringBuilder();
                    while (_position < _text.Length)
                    {
                        char c = _text[_position++];

                        if (c == '"')
                        {
                            if (_position < _text.Length && _text[_position] == '"')
                            {
                                sb.Append('"');
                                _position++;
                                continue;
                            }
                            break;
                        }
                        if (c == '\\') sb.Append("\\\\");
                        else sb.Append(c);
                    }
                    CurrentTokenText = sb.ToString();
                    CurrentTokenType = Ast.TokenType.InterpolatedString;
                    return;
                }
            }
            //Interpolated string
            if (current == '$' && _position + 1 < _text.Length && _text[_position + 1] == '"')
            {
                _position += 2; //skip $"
                int start = _position;
                var sb = new StringBuilder();
                while (_position < _text.Length && _text[_position] != '"')
                {
                    char c = _text[_position++];
                    if (c == '\\') //escape?
                    {
                        if (_position >= _text.Length) break;
                        sb.Append('\\');
                        sb.Append(_text[_position++]);
                        continue;
                    }
                    if (c == '"') break; //skip "
                    sb.Append(c);
                }

                CurrentTokenText = _text.Substring(start, _position - start);
                _position++; //skip "
                CurrentTokenType = Ast.TokenType.InterpolatedString;
                return;
            }
            //Verbatim string
            if (current == '@' && _position + 1 < _text.Length && _text[_position + 1] == '"')
            {
                _position += 2; // skip @"
                var sb = new StringBuilder();
                while (_position < _text.Length)
                {
                    char c = _text[_position++];

                    if (c == '"')
                    {
                        if (_position < _text.Length && _text[_position] == '"')
                        {
                            sb.Append('"');
                            _position++; // skip "
                            continue;
                        }
                        break;
                    }
                    if (c == '\\') sb.Append("\\\\");
                    else sb.Append(c);
                }

                CurrentTokenText = sb.ToString();
                CurrentTokenType = Ast.TokenType.String;
                return;
            }
            //String literal
            if (current == '"')
            {
                _position++; //skip "
                var sb = new StringBuilder();
                while (_position < _text.Length)
                {
                    char c = _text[_position++];
                    if (c == '\\') //escape?
                    {
                        if (_position >= _text.Length) break;
                        sb.Append('\\');
                        sb.Append(_text[_position++]);
                        continue;
                    }
                    if (c == '"') break; //skip "
                    sb.Append(c);
                }
                CurrentTokenText = sb.ToString();
                CurrentTokenType = Ast.TokenType.String;
                return;
            }
            if (current == '\'')
            {
                _position++; //skip '
                if (_position >= _text.Length) throw new Exception("Unterminated char literal");

                char value;
                char c = _text[_position++];
                if (c == '\\') //escape?
                {
                    if (_position >= _text.Length) throw new Exception("Bad escape in char literal");
                    char esc = _text[_position++];
                    switch (esc)
                    {
                        case '\\': value = '\\'; break;
                        case '\'': value = '\''; break;
                        case '"': value = '\"'; break;
                        case 'n': value = '\n'; break;
                        case 'r': value = '\r'; break;
                        case 't': value = '\t'; break;
                        default: value = esc; break;
                    }
                }
                else value = c;

                if (_position >= _text.Length || _text[_position] != '\'')
                    throw new Exception("Unterminated char literal");
                _position++;//skip '

                CurrentTokenText = value.ToString();
                CurrentTokenType = Ast.TokenType.Char;
                return;
            }

            //Operators
            foreach (var op in OperatorMap.Keys.OrderByDescending(k => k.Length))
            {
                if (_text.AsSpan(_position).StartsWith(op))
                {
                    CurrentTokenText = op;
                    _position += op.Length;
                    CurrentTokenType = Ast.TokenType.Operator;
                    return;
                }
            }
            //Structural TokenType
            foreach (var kvp in TokenTypeMap.OrderByDescending(kvp => kvp.Key.Length))
            {
                if (_text.AsSpan(_position).StartsWith(kvp.Key))
                {
                    CurrentTokenText = kvp.Key;
                    _position += kvp.Key.Length;
                    CurrentTokenType = kvp.Value;
                    return;
                }
            }

            throw new Exception($"Unexpected character '{current}'");
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position])) { _position++; continue; }
                if (_text[_position] == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/') //if single line comment
                {
                    _position += 2; //skip "//"
                    while (_position < _text.Length &&
                           _text[_position] != '\n' &&
                           _text[_position] != '\r')
                        _position++;
                    continue;
                }
                if (_text[_position] == '#') //if preprocessor command
                {
                    _position++;
                    while (_position < _text.Length && char.IsLetter(_text[_position]))
                        _position++;
                    _position++;
                }
                if (_text[_position] == '/' && _position + 1 < _text.Length && _text[_position + 1] == '*') //if block comment
                {
                    _position += 2; //skip /*
                    while (_position + 1 < _text.Length &&
                          !(_text[_position] == '*' && _text[_position + 1] == '/'))
                        _position++;
                    if (_position + 1 >= _text.Length)
                        throw new ApplicationException("Unterminated block comment");
                    _position += 2; //skip */
                    continue;
                }

                break;

            }
        }

        public Ast.OperatorToken ParseOperator()
        {
            if (OperatorMap.TryGetValue(CurrentTokenText, out var token)) return token;
            throw new Exception($"Unknown operator: {CurrentTokenText}");
        }
    }
}
