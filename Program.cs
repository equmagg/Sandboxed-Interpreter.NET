using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using static S_Interpretor.AstTree;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
using System;
using System.Text.RegularExpressions;

namespace S_Interpretor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string code = File.ReadAllText("code.txt");
            var ast = new AstTree();
            ast.ImportStandartLibrary(true);
            //ast.Context.RegisterNative("sqrt", (Func<double, double>)Math.Sqrt);
            ast.Interpret(code, printTree: true);
        }
    }
    #region Lexer
    public class Lexer
    {
        private readonly string _text;
        private int _position;

        public Lexer(string text) => _text = text;

        public AstTree.TokenType CurrentTokenType { get; private set; }
        public string CurrentTokenText { get; private set; }

        private static readonly Dictionary<string, AstTree.OperatorToken> OperatorMap = new()
        {
            { "+", AstTree.OperatorToken.Plus },
            { "-", AstTree.OperatorToken.Minus },
            { "*", AstTree.OperatorToken.Multiply },
            { "/", AstTree.OperatorToken.Divide },
            { "!", AstTree.OperatorToken.Not },
            { "++", AstTree.OperatorToken.Increment },
            { "--", AstTree.OperatorToken.Decrement },
            { "==", AstTree.OperatorToken.Equal },
            { "!=", AstTree.OperatorToken.NotEqual },
            { ">", AstTree.OperatorToken.Greater },
            { "<", AstTree.OperatorToken.Less },
            { "<=", AstTree.OperatorToken.LessOrEqual },
            { ">=", AstTree.OperatorToken.GreaterOrEqual },
            { "+=", AstTree.OperatorToken.PlusEqual },
            { "-=", AstTree.OperatorToken.MinusEqual },
            { "*=", AstTree.OperatorToken.MultiplyEqual },
            { "/=", AstTree.OperatorToken.DivideEqual },
            { "%", AstTree.OperatorToken.Module },
            { ">>", AstTree.OperatorToken.RightShift },
            { "<<", AstTree.OperatorToken.LeftShift },
            { "=", AstTree.OperatorToken.Equals },
            { "&", AstTree.OperatorToken.AddressOf },
            { "&&", AstTree.OperatorToken.And },
            { "||", AstTree.OperatorToken.Or },
        };
        private static readonly Dictionary<string, AstTree.TokenType> TokenTypeMap = new()
        {
            { ";", AstTree.TokenType.Semicolon },
            { ":", AstTree.TokenType.Colon },
            { "(", AstTree.TokenType.ParenOpen },
            { ")", AstTree.TokenType.ParenClose },
            { "{", AstTree.TokenType.BraceOpen },
            { "}", AstTree.TokenType.BraceClose },
            { "[", AstTree.TokenType.BracketsOpen },
            { "]", AstTree.TokenType.BracketsClose },
            { "=", AstTree.TokenType.Equals },
            { ",", AstTree.TokenType.Comma },
            { "?", AstTree.TokenType.Question },
        };

        private static readonly HashSet<string> Keywords = new() { "int", "string", "char", "byte", "float", "long", "ulong", "uint", "bool", "double", "IntPtr", "intPtr",
            "if", "goto", "for", "while", "do", "void","return", "break","continue","try","catch", "switch", "case", "finally" , "var", "default", "throw", "else" };

        public void ResetCurrent(int pos, AstTree.TokenType token, string text)
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
                CurrentTokenType = AstTree.TokenType.EndOfInput;
                return;
            }

            char current = _text[_position];


            //Number literal (int/double)
            if (char.IsDigit(current) || (current == '.' && _position > 0 && char.IsDigit(_text[_position - 1])))
            {
                int start = _position;
                while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
                    _position++;
                CurrentTokenText = _text[start.._position];
                CurrentTokenType = TokenType.Number; 
                return;
            }

            //Identifier or keyword
            if (char.IsLetter(current))
            {
                int start = _position;
                while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
                    _position++;
                CurrentTokenText = _text[start.._position];
                CurrentTokenType = Keywords.Contains(CurrentTokenText) ? TokenType.Keyword : TokenType.Identifier; 
                return;
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
                    
                CurrentTokenText = _text[start.._position];
                _position++; //skip "
                CurrentTokenType = AstTree.TokenType.InterpolatedString;
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
                CurrentTokenType = AstTree.TokenType.String;
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
                    value = esc switch
                    {
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '\"',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => esc
                    };
                }
                else value = c;

                if (_position >= _text.Length || _text[_position] != '\'')
                    throw new Exception("Unterminated char literal");
                _position++;//skip '

                CurrentTokenText = value.ToString();
                CurrentTokenType = TokenType.Char;
                return;
            }

            //Operators
            foreach (var op in OperatorMap.Keys.OrderByDescending(k => k.Length))
            {
                if (_text.AsSpan(_position).StartsWith(op))
                {
                    CurrentTokenText = op; 
                    _position += op.Length; 
                    CurrentTokenType = TokenType.Operator; 
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
                if (_text[_position] == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')// if single line comment
                {
                    _position += 2; //skip "//"
                    while (_position < _text.Length &&
                           _text[_position] != '\n' &&
                           _text[_position] != '\r')
                        _position++;
                    continue;
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

        public AstTree.OperatorToken ParseOperator()
        {
            if (OperatorMap.TryGetValue(CurrentTokenText, out var token)) return token;
            throw new Exception($"Unknown operator: {CurrentTokenText}");
        }
    }
    #endregion
    #region Parser
    public class Parser
    {
        private readonly Lexer _lexer;

        public Parser(string text)
        {
            _lexer = new Lexer(text);
            _lexer.NextToken();
        }

        private void Consume(AstTree.TokenType type)
        {
            if (_lexer.CurrentTokenType != type && type == AstTree.TokenType.Semicolon && _lexer.CurrentTokenType == AstTree.TokenType.Identifier)
                throw new Exception($"Expected Semicolon, did you forget ; ?");
            else if(_lexer.CurrentTokenType != type)
                throw new Exception($"Expected {type}, but got {_lexer.CurrentTokenType}");
            _lexer.NextToken();
        }

        #region Helpers
        private bool IsTypeKeyword(string tokenText) =>
            tokenText == "var" || Enum.TryParse<AstTree.ValueType>(tokenText, ignoreCase: true, out _);
        private bool PeekPointerMark()
            => _lexer.CurrentTokenType == TokenType.Operator && _lexer.CurrentTokenText == "*";
        private AstTree.ValueType ReadMaybePointer(AstTree.ValueType baseType)
        {
            if (PeekPointerMark()) { _lexer.NextToken(); return AstTree.ValueType.IntPtr; }
            return baseType;
        }
        private bool IsPostfixUnary(OperatorToken token, OperatorInfo info)
        {
            return info.Associativity == Associativity.Right && (token == OperatorToken.Increment || token == OperatorToken.Decrement);
        }
        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }

                if (++i == s.Length) break;//alone /
                sb.Append(s[i] switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    _ => s[i]
                });
            }
            return sb.ToString();
        }
        #endregion

        #region Root/Block parsers
        public AstTree.StatementListNode ParseProgram()
        {
            var statements = new List<AstTree.AstNode>();
            while (_lexer.CurrentTokenType != AstTree.TokenType.EndOfInput)
            {
                statements.Add(ParseStatement());
            }
            return new AstTree.StatementListNode(statements);
        }
        private AstNode ParseBlock()
        {
            Consume(TokenType.BraceOpen); //{
            var statements = new List<AstTree.AstNode>();
            while (_lexer.CurrentTokenType != TokenType.BraceClose && _lexer.CurrentTokenType != TokenType.EndOfInput)
            {
                statements.Add(ParseStatement());
            }
            Consume(TokenType.BraceClose); //}
            return new AstTree.BlockNode(statements);
        }
        #endregion
        #region Statemet parsers
        private AstTree.AstNode ParseStatement()
        {
            if (_lexer.CurrentTokenType == TokenType.Semicolon)
            {
                _lexer.NextToken(); return new EmptyNode();
            }
            if (_lexer.CurrentTokenType == AstTree.TokenType.Keyword)
            {
                switch (_lexer.CurrentTokenText)
                {
                    case "if": return ParseIf();
                    case "goto": return ParseGoto();
                    case "while": return ParseWhile();
                    case "for": return ParseFor();
                    case "do": return ParseDoWhile();
                    case "return": return ParseReturn();
                    case "break": _lexer.NextToken(); Consume(TokenType.Semicolon); return new BreakNode();
                    case "continue": _lexer.NextToken(); Consume(TokenType.Semicolon); return new ContinueNode();
                    case "try": return ParseTryCatch();
                    case "switch": return ParseSwitch();
                    case "throw": _lexer.NextToken(); var expr = ParseExpression(); Consume(TokenType.Semicolon); return new ThrowNode(expr);
                    case "void": return ParseFunctionDeclaration(isVoid: true);
                }
            }
            //expect declaration
            if (IsTypeKeyword(_lexer.CurrentTokenText) && !int.TryParse(_lexer.CurrentTokenText, out _))
            {
                //look ahead: function?
                string saveType = _lexer.CurrentTokenText; 
                int pos = _lexer.Position;
                TokenType saveToken = _lexer.CurrentTokenType; 
                _lexer.NextToken();//skip (
                bool pointerDecl = PeekPointerMark();
                if (pointerDecl) _lexer.NextToken(); 

                if (_lexer.CurrentTokenType == TokenType.Identifier)
                {
                    var ident = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == TokenType.ParenOpen)
                    {
                        return ParseFunctionDeclaration(false, saveType, ident);
                    }
                }
                _lexer.ResetCurrent(pos, saveToken, saveType);
                return ParseDeclaration();
            }
            var exprStatement = ParseExpression();
            Consume(AstTree.TokenType.Semicolon);
            return exprStatement;
        }
        private AstTree.AstNode ParseDeclaration()
        {
            var typeStr = _lexer.CurrentTokenText;
            bool isVar = typeStr == "var";
            _lexer.NextToken();
            var baseType = isVar ? AstTree.ValueType.Object //will figure out during evaluation 
                : Enum.Parse<AstTree.ValueType>(typeStr, true);
            if (!isVar) baseType = ReadMaybePointer(baseType);
            var name = _lexer.CurrentTokenText;
            Consume(AstTree.TokenType.Identifier);
            AstTree.AstNode expr = new AstTree.LiteralNode(null); ;

            if (_lexer.CurrentTokenText == "=")
            {
                _lexer.NextToken();
                expr = ParseExpression();
            }
            else if (isVar)
                throw new ApplicationException("Variable declared with 'var' must have an initializer");

            Consume(AstTree.TokenType.Semicolon);

            return new AstTree.VariableDeclarationNode(baseType, name, expr);
        }
        #endregion
        #region Expression parser 
        private AstTree.AstNode ParseExpression(int parentPrecedence = 0)
        {
            AstTree.AstNode left = ParseUnary();
            while (true)
            {
                if (_lexer.CurrentTokenType == TokenType.Question)
                {
                    const int ternaryPrecedence = 0;
                    if (ternaryPrecedence < parentPrecedence) break;

                    _lexer.NextToken();//skip ?
                    var ifTrue = ParseExpression();
                    Consume(TokenType.Colon);
                    var ifFalse = ParseExpression(ternaryPrecedence);

                    left = new AstTree.ConditionalNode(left, ifTrue, ifFalse);
                    continue;
                }
                if (_lexer.CurrentTokenType != TokenType.Operator) break;
                var opToken = _lexer.ParseOperator();
                if (!AstTree.TryGetOperatorInfo(opToken, out var opInfo)) break;
                if (opInfo.Precedence < parentPrecedence) break;
                if (IsPostfixUnary(opToken, opInfo))
                {
                    _lexer.NextToken();
                    left = new AstTree.UnaryOpNode(opToken, left, true);
                    continue;
                }
                
                _lexer.NextToken();
                var right = ParseExpression(opInfo.Associativity == AstTree.Associativity.Left ? opInfo.Precedence + 1 : opInfo.Precedence);

                left = new AstTree.BinOpNode(left, opToken, right);
            }

            return left;
        }
        private AstTree.AstNode ParseUnary()
        {
            if (_lexer.CurrentTokenType == AstTree.TokenType.Operator)
            {
                var opToken = _lexer.ParseOperator();
                if (AstTree.GetOperatorInfo(opToken).Associativity is AstTree.Associativity.Right || opToken is OperatorToken.Minus or OperatorToken.Multiply)
                {
                    _lexer.NextToken();
                    var operand = ParseUnary();
                    return new AstTree.UnaryOpNode(opToken, operand);
                }
            }
            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            _lexer.NextToken();
            bool isCast = _lexer.CurrentTokenType == TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText);
            _lexer.ResetCurrent(savePos, saveType, saveText);
            if (_lexer.CurrentTokenType == TokenType.ParenOpen && isCast)
            {
                _lexer.NextToken(); //skip (
                var typeTok = _lexer.CurrentTokenText;
                _lexer.NextToken(); //skip type
                var vt = Enum.Parse<AstTree.ValueType>(typeTok, true);
                vt = ReadMaybePointer(vt);
                Consume(TokenType.ParenClose);

                var operand = ParseUnary(); //precedence
                return new CastNode(vt, operand);
            }

            return ParsePrimary();
        }
        private AstTree.AstNode ParsePrimary()
        {
            switch (_lexer.CurrentTokenType)
            {
                case TokenType.Number:
                    string num = _lexer.CurrentTokenText; _lexer.NextToken();
                    return num.Contains('.') ? new LiteralNode(double.Parse(num)) : new LiteralNode(int.Parse(num));
                case TokenType.Char:
                    char ch = _lexer.CurrentTokenText[0];
                    _lexer.NextToken();
                    return new LiteralNode(ch);
                case TokenType.String:
                    var strValue = Unescape(_lexer.CurrentTokenText);
                    _lexer.NextToken();
                    return new AstTree.LiteralNode(strValue);
                case TokenType.InterpolatedString:
                    var strValue2 = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    return ParseInterpolatedString(strValue2);
                case TokenType.Identifier:
                    var name = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    if (name == "true") return new LiteralNode(true);
                    if (name == "false") return new LiteralNode(false);
                    if (_lexer.CurrentTokenType == TokenType.ParenOpen)
                    {
                        _lexer.NextToken(); //skip (
                        var args = new List<AstNode>();
                        if (_lexer.CurrentTokenType != TokenType.ParenClose)
                        {
                            do
                            {
                                args.Add(ParseExpression());
                                if (_lexer.CurrentTokenType == TokenType.Comma)
                                {
                                    _lexer.NextToken();
                                    continue;
                                }
                                break;
                            }
                            while (true);
                        }
                        Consume(TokenType.ParenClose);
                        return new CallNode(name, args.ToArray());
                    }
                    return new AstTree.VariableReferenceNode(name);
                case TokenType.ParenOpen:
                    _lexer.NextToken();
                    var expr = ParseExpression();
                    Consume(AstTree.TokenType.ParenClose);
                    return expr;
            }
            throw new ApplicationException($"Unexpected token in expression: {_lexer.CurrentTokenType}: {_lexer.CurrentTokenText}");
        }
        private AstTree.AstNode ParseInterpolatedString(string content)
        {
            //cut string
            var parts = new List<AstNode>();
            var sb = new StringBuilder();
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{')//expression start
                {
                    if (sb.Length > 0)
                    {
                        parts.Add(new LiteralNode(Unescape(sb.ToString())));
                        sb.Clear();
                    }

                    //paired } no nesting
                    int j = content.IndexOf('}', i + 1);
                    if (j < 0) throw new ApplicationException("Unclosed { in interpolated string");

                    string exprSrc = content.Substring(i + 1, j - i - 1);

                    var exprParser = new Parser(exprSrc);
                    parts.Add(exprParser.ParseExpression());

                    i = j; //skip }
                    continue;
                }
                sb.Append(content[i]);
            }
            if (sb.Length > 0) parts.Add(new LiteralNode(Unescape(sb.ToString())));

            //glue together with +
            if (parts.Count == 0) return new LiteralNode(string.Empty);
            AstNode node = parts[0];
            for (int k = 1; k < parts.Count; k++)
                node = new BinOpNode(node, OperatorToken.Plus, parts[k]);

            return node;
        }
        #endregion
        #region Function parser
        private AstNode ParseFunctionDeclaration(bool isVoid = false, string? returnTypeText = null, string? name = null)
        {
            AstTree.ValueType? retType = isVoid ? null :
                Enum.Parse<AstTree.ValueType>(returnTypeText!, ignoreCase: true);

            if (name == null)
            {
                name = _lexer.CurrentTokenText;
                Consume(TokenType.Identifier);
            }

            Consume(TokenType.ParenOpen);
            var paramNames = new List<string>();
            var paramTypes = new List<AstTree.ValueType>();
            if (_lexer.CurrentTokenType != TokenType.ParenClose)
            {
                do
                {
                    var pType = AstTree.ValueType.Object;
                    if (_lexer.CurrentTokenType == TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                    {
                        pType = Enum.Parse<AstTree.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); //skip time
                        pType = ReadMaybePointer(pType);//ptr*?
                    }
                    
                    var pname = _lexer.CurrentTokenText;
                    Consume(TokenType.Identifier);
                    paramTypes.Add(pType);
                    paramNames.Add(pname);
                    if (_lexer.CurrentTokenType == TokenType.Comma)
                    {
                        _lexer.NextToken();
                    }
                    else break;
                }
                while (true);
            }
            Consume(TokenType.ParenClose);

            var body = _lexer.CurrentTokenType == TokenType.BraceOpen
                       ? ParseBlock()
                       : ParseStatement();

            return new FunctionDeclarationNode(retType!, name, paramNames.ToArray(), paramTypes.ToArray(), body);
        }
        private AstNode ParseReturn()
        {
            _lexer.NextToken(); //skip return
            AstNode? expr = null;
            if (_lexer.CurrentTokenType != TokenType.Semicolon)
                expr = ParseExpression();
            Consume(TokenType.Semicolon);
            return new ReturnNode(expr);
        }
        #endregion
        #region Flow Control parser
        #region Loop parser
        private AstTree.AstNode ParseDoWhile()
        {
            _lexer.NextToken(); //skip do
            var body = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            Consume(TokenType.Keyword); //expect while
            Consume(TokenType.ParenOpen);
            var condition = ParseExpression();
            Consume(TokenType.ParenClose);
            Consume(TokenType.Semicolon);
            return new AstTree.DoWhileNode(body, condition);
        }
        private AstTree.AstNode ParseWhile()
        {
            _lexer.NextToken(); //skip while
            Consume(TokenType.ParenOpen);
            var condition = ParseExpression();
            Consume(TokenType.ParenClose);
            var body = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            return new AstTree.WhileNode(condition, body);
        }
        private AstTree.AstNode ParseFor()
        {
            _lexer.NextToken(); //skip for
            Consume(TokenType.ParenOpen);

            AstTree.AstNode init = null;

            if (IsTypeKeyword(_lexer.CurrentTokenText) && !int.TryParse(_lexer.CurrentTokenText, out _))
            {
                init = ParseDeclaration();
            }
            else if (_lexer.CurrentTokenType != TokenType.Semicolon)
            {
                init = ParseExpression();
                Consume(TokenType.Semicolon);
            }
            else
            {
                Consume(TokenType.Semicolon);
            }

            AstTree.AstNode condition = null;
            if (_lexer.CurrentTokenType != TokenType.Semicolon)
            {
                condition = ParseExpression();
            }
            Consume(TokenType.Semicolon);

            AstTree.AstNode step = null;
            if (_lexer.CurrentTokenType != TokenType.ParenClose)
            {
                step = ParseExpression();
            }
            Consume(TokenType.ParenClose);

            var body = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            return new AstTree.ForNode(init, condition, step, body);
        }
        #endregion
        private AstTree.AstNode ParseGoto()
        {
            _lexer.NextToken();
            string label = _lexer.CurrentTokenText;
            Consume(AstTree.TokenType.Identifier);
            Consume(AstTree.TokenType.Semicolon);
            return new AstTree.GotoNode(label);
        }
        private AstTree.AstNode ParseIf()
        {
            _lexer.NextToken(); //skip if
            Consume(AstTree.TokenType.ParenOpen);
            var cond = ParseExpression();
            Consume(AstTree.TokenType.ParenClose);

            AstTree.AstNode body = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            AstNode? elseBody = null;
            if (_lexer.CurrentTokenType == TokenType.Keyword && _lexer.CurrentTokenText == "else")
            {
                _lexer.NextToken(); //skip else
                elseBody = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }

            return new AstTree.IfNode(cond, body, elseBody);
        }
        private AstNode ParseSwitch()
        {
            _lexer.NextToken(); //skip switch
            Consume(TokenType.ParenOpen);
            var expr = ParseExpression();
            Consume(TokenType.ParenClose);

            Consume(TokenType.BraceOpen);

            var cases = new List<(AstNode? value, List<AstNode> body)>();
            List<AstNode>? currentBody = null;
            AstNode? currentValue = null; //if null default

            while (_lexer.CurrentTokenType != TokenType.BraceClose && _lexer.CurrentTokenType != TokenType.EndOfInput)
            {
                if (_lexer.CurrentTokenType == TokenType.Keyword && (_lexer.CurrentTokenText == "case" || _lexer.CurrentTokenText == "default"))
                {
                    if (currentBody != null)
                        cases.Add((currentValue, currentBody));

                    currentBody = new List<AstNode>();
                    currentValue = null;

                    if (_lexer.CurrentTokenText == "case")
                    {
                        _lexer.NextToken(); //skip case
                        currentValue = ParseExpression();//const
                    }
                    else
                    {
                        _lexer.NextToken(); //skip default
                    }
                    Consume(TokenType.Colon);
                    continue;
                }

                currentBody ??= new List<AstNode>();//if no case
                currentBody.Add(ParseStatement());
            }

            if (currentBody != null)
                cases.Add((currentValue, currentBody));

            Consume(TokenType.BraceClose);

            return new SwitchNode(expr, cases);
        }
        private AstNode ParseTryCatch()
        {
            _lexer.NextToken();
            var tryBody = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();

            if (!(_lexer.CurrentTokenType == TokenType.Keyword && _lexer.CurrentTokenText == "catch"))
                throw new Exception("expected 'catch' after 'try' block");
            _lexer.NextToken(); //skip catch

            string? exVar = null;
            if (_lexer.CurrentTokenType == TokenType.ParenOpen)
            {
                _lexer.NextToken(); //(
                exVar = _lexer.CurrentTokenText;
                Consume(TokenType.Identifier);
                Consume(TokenType.ParenClose); //)
            }

            var catchBody = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            AstNode? finallyBody = null;
            if (_lexer.CurrentTokenType == TokenType.Keyword && _lexer.CurrentTokenText == "finally")
            {
                _lexer.NextToken();
                finallyBody = _lexer.CurrentTokenType == TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }

            return new TryCatchNode(tryBody, catchBody, finallyBody, exVar);
        }
        

        #endregion


    }
    #endregion
    public class AstTree
    {
        public ExecutionContext Context { get; }
        public AstNode? RootNode { get; private set; }
        public AstTree()
        {
            this.Context = new AstTree.ExecutionContext(CancellationToken.None);
        }
        public AstTree(CancellationToken token)
        {
            this.Context = new AstTree.ExecutionContext(token);
        }
        public void ImportStandartLibrary(bool ConsoleOutput = false)
        {
            if (ConsoleOutput)
            {
                this.Context.RegisterNative("print", (Action<string>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<int>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<double>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<string>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<int>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<double>)Console.WriteLine);
                this.Context.RegisterNative("Write", (Action<string>)Console.Write);
                this.Context.RegisterNative("Write", (Action<double>)Console.Write);
                this.Context.RegisterNative("Write", (Action<int>)Console.Write);
            }
            this.Context.RegisterNative("typeof", (object o)=>{ return o.GetType().ToString(); } );
        }
        public void Interpret(string code, bool printTree = false) //TODO arrays, native objects, attributes, function finding
        {
            var parser = new Parser(code);
            var ast = parser.ParseProgram();
            RootNode = ast;
            if(printTree) RootNode.Print();
            RootNode.Evaluate(this.Context);

        }
        public enum ValueType
        {
            Int, String, Bool, Double, Ulong, Uint, Long, Byte, Object, Sbyte, Short, Char, UShort, Float, Decimal, IntPtr
        }
        public enum Associativity { Left, Right }
        public enum OperatorToken
        {
            Plus,
            Minus,
            Not,
            Increment,
            Decrement,
            Multiply,
            Divide,
            Greater,
            Less,
            Equal,
            Equals,
            NotEqual,
            PlusEqual,
            MinusEqual,
            MultiplyEqual,
            DivideEqual,
            Module,
            LessOrEqual,
            GreaterOrEqual,
            RightShift,
            LeftShift,
            Pow,
            AddressOf,
            And,
            Or
        }
        public enum TokenType
        {
            Identifier,
            Keyword,
            Number,
            String,
            InterpolatedString,
            Char,
            Operator,
            ParenOpen,//(
            ParenClose,//)
            Semicolon,//;
            Equals,//=
            EndOfInput,
            Colon,//:
            BraceOpen, //{
            BraceClose,//}
            BracketsOpen,//[
            BracketsClose,//]
            Comma,//,
            Question,//?
        }
        private static bool MatchVariableType(object value, ValueType type) => type switch
        {
            ValueType.Int => value is int,
            ValueType.String => value is string,
            ValueType.Bool => value is bool,
            ValueType.Double => value is double,
            ValueType.Char => value is char,
            ValueType.Short => value is short,
            ValueType.Long => value is long,
            ValueType.Ulong => value is ulong,
            ValueType.Uint => value is uint,
            ValueType.Byte => value is byte,
            ValueType.Sbyte => value is sbyte,
            ValueType.UShort => value is ushort,
            ValueType.Float => value is float,
            ValueType.Decimal => value is decimal,
            ValueType.Object => value is object,
            ValueType.IntPtr => value is int,
            _ => false
        };
        public static bool IsReferenceType(ValueType type) => type switch
        {
            ValueType.Double => false,
            ValueType.Float => false,
            ValueType.Decimal => false,
            ValueType.Int => false,
            ValueType.Uint => false,
            ValueType.Long => false,
            ValueType.Ulong => false,
            ValueType.Short => false,
            ValueType.UShort => false,
            ValueType.Byte => false,
            ValueType.Sbyte => false,
            ValueType.Char => false,
            ValueType.Bool => false,
            ValueType.IntPtr => false,
            _ => true
        };
        public class OperatorInfo
        {
            public int Precedence { get; }
            public Associativity Associativity { get; }

            public OperatorInfo(int precedence, Associativity associativity)
            {
                Precedence = precedence;
                Associativity = associativity;
            }
        }
        private static readonly Dictionary<OperatorToken, OperatorInfo> _operatorTable = new()
        {
            { OperatorToken.Not, new OperatorInfo(3, Associativity.Right) },
            { OperatorToken.Increment, new OperatorInfo(3, Associativity.Right) },
            { OperatorToken.Decrement, new OperatorInfo(3, Associativity.Right) },
            { OperatorToken.AddressOf, new OperatorInfo(3, Associativity.Right) },

            { OperatorToken.Multiply, new OperatorInfo(5, Associativity.Left) },
            { OperatorToken.Divide, new OperatorInfo(5, Associativity.Left) },
            { OperatorToken.MultiplyEqual, new OperatorInfo(3, Associativity.Left) },
            { OperatorToken.DivideEqual, new OperatorInfo(3, Associativity.Left) },
            { OperatorToken.Module, new OperatorInfo(5, Associativity.Left) },
            { OperatorToken.Pow, new OperatorInfo(6, Associativity.Right) },

            { OperatorToken.Plus, new OperatorInfo(4, Associativity.Left) },
            { OperatorToken.Minus, new OperatorInfo(4, Associativity.Left) },
            { OperatorToken.PlusEqual, new OperatorInfo(3, Associativity.Left) },
            { OperatorToken.MinusEqual, new OperatorInfo(3, Associativity.Left) },

            { OperatorToken.Greater, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.Less, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.Equal, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.NotEqual, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.LessOrEqual, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.GreaterOrEqual, new OperatorInfo(2, Associativity.Left) },
            { OperatorToken.Equals, new OperatorInfo(1, Associativity.Left) },
            { OperatorToken.And, new OperatorInfo(1, Associativity.Left) },
            { OperatorToken.Or, new OperatorInfo(1, Associativity.Left) },
        };
        public static bool TryGetOperatorInfo(OperatorToken token, out OperatorInfo info) =>
        _operatorTable.TryGetValue(token, out info);

        public static OperatorInfo GetOperatorInfo(OperatorToken token) =>
            _operatorTable.TryGetValue(token, out var info) ? info
            : throw new ArgumentNullException($"No operator info defined for {token}");
        #region Context
        public class ExecutionContext
        {
            private const int HeaderSize = 6;
            private readonly Stack<Dictionary<string, Variable>> _scopes = new();
            private readonly Stack<int> _memoryOffsets = new();
            private readonly byte[] _memory;
            private int _heapend = 0;
            private int _allocPointer = 0;
            public readonly Dictionary<string, List<Function>> Functions = new();
            public readonly Dictionary<string, List<Delegate>> NativeFunctions = new();
            public readonly int StackSize;
            public Dictionary<string, int> Labels { get; set; } = new();
            public CancellationToken CancellationToken { get; }
            public byte[] RawMemory => _memory;
            public int UsedMemory => _allocPointer;
            public struct Function
            {
                public ValueType ReturnType;
                public string[] ParamNames; 
                public ValueType[] ParamTypes;
                public AstNode Body;
            }
            public ExecutionContext(CancellationToken token, int size = 1024 * 4, int stackSize = 1024 * 1)
            {
                CancellationToken = token;
                StackSize = stackSize;
                _memory = new byte[size];
                EnterScope();
            }
            public void RegisterNative(string name, Delegate fn)
            {
                if (!NativeFunctions.TryGetValue(name, out var list))
                    NativeFunctions[name] = list = new List<Delegate>();
                list.Add(fn ?? throw new ArgumentNullException(nameof(fn)));
            }
            public void AddFunction(string name, Function fn)
            {
                if (!Functions.TryGetValue(name, out var list))
                    Functions[name] = list = new List<Function>();
                list.Add(fn);
            }

            #region Memory manager

            #region Stack manager
            private static readonly Dictionary<AstTree.ValueType, Func<byte[], int, object>> typeReaders = new()
            {
                { AstTree.ValueType.Int, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { AstTree.ValueType.IntPtr, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { AstTree.ValueType.String, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { AstTree.ValueType.Bool, (heap, offset) => heap[offset] != 0 },
                { AstTree.ValueType.Float, (heap, offset) => BitConverter.ToSingle(heap, offset) },
                { AstTree.ValueType.Double, (heap, offset) => BitConverter.ToDouble(heap, offset) },
                { AstTree.ValueType.Char, (heap, offset) => BitConverter.ToChar(heap, offset) },
                { AstTree.ValueType.Long, (heap, offset) => BitConverter.ToInt64(heap, offset) },
                { AstTree.ValueType.Ulong, (heap, offset) => BitConverter.ToUInt64(heap, offset) },
                { AstTree.ValueType.Uint, (heap, offset) => BitConverter.ToUInt16(heap, offset) },
            };

            private static readonly Dictionary<AstTree.ValueType, Action<byte[], int, object>> typeWriters = new()
            {
                { AstTree.ValueType.Int, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { AstTree.ValueType.IntPtr, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { AstTree.ValueType.String, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Bool, (heap, offset, value) => heap[offset] = (bool)value ? (byte)1 : (byte)0 },
                { AstTree.ValueType.Float, (heap, offset, value) => BitConverter.GetBytes((float)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Double, (heap, offset, value) => BitConverter.GetBytes((double)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Char, (heap, offset, value) => BitConverter.GetBytes((char)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Long, (heap, offset, value) => BitConverter.GetBytes((long)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Ulong, (heap, offset, value) => BitConverter.GetBytes((ulong)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Uint, (heap, offset, value) => BitConverter.GetBytes((uint)value).CopyTo(heap, offset) },
                { AstTree.ValueType.Short, (heap, offset, value) => BitConverter.GetBytes((short)value).CopyTo(heap, offset) },
                { AstTree.ValueType.UShort, (heap, offset, value) => BitConverter.GetBytes((ushort)value).CopyTo(heap, offset) },
            };
            public object ReadFromStack(int addr, AstTree.ValueType valueType) => typeReaders[valueType](_memory, addr);
            public void EnterScope()
            {
                _memoryOffsets.Push(_allocPointer);
                _scopes.Push(new Dictionary<string, Variable>());
            }

            public void ExitScope()
            {
                _scopes.Pop();
                _allocPointer = _memoryOffsets.Pop();
                CollectGarbage();
            }
            public bool HasVariable(string name) =>_scopes.Any(scope => scope.ContainsKey(name));
            public Variable Stackalloc(ValueType type)
            {
                int size = GetTypeSize(type);
                if (_allocPointer + size > _memory.Length)
                    throw new OutOfMemoryException();
                int address = _allocPointer;
                _allocPointer += size;

                return new Variable(type, address, size);

            }

            private int GetTypeSize(AstTree.ValueType type) => type switch
            {
                ValueType.Int => sizeof(int),
                ValueType.Bool => sizeof(bool),
                ValueType.Uint => sizeof(uint),
                ValueType.Long => sizeof(long),
                ValueType.Ulong => sizeof(ulong),
                ValueType.Char => sizeof(char),
                ValueType.Byte => sizeof(byte),
                ValueType.Sbyte => sizeof(sbyte),
                ValueType.Short => sizeof(short),
                ValueType.UShort => sizeof(ushort),
                ValueType.Decimal => sizeof(decimal),
                ValueType.Double => sizeof(double),
                ValueType.Float => sizeof(float),
                ValueType.IntPtr => sizeof(int),
                AstTree.ValueType.String => sizeof(int),
                AstTree.ValueType.Object => sizeof(int),
                _ => throw new NotSupportedException($"Unsupported type {type}")
            };

            public object ReadVariable(string name)
            {
                var variable = Get(name);
                return typeReaders[variable.Type](_memory, variable.Address);
            }
            public void WriteVariable(string name, object value)
            {
                var variable = Get(name);
                typeWriters[variable.Type](_memory, variable.Address, value);

            }
            #endregion
            #region Heap manager
            private void CollectGarbage()
            {
                //Mark
                HashSet<int> reachable = new();
                foreach (var scope in _scopes)
                    foreach (var kv in scope)
                    {
                        Variable v = kv.Value;

                        //Reference exist
                        if (AstTree.IsReferenceType(v.Type) || v.Type == AstTree.ValueType.IntPtr)
                        {
                            int ptr = BitConverter.ToInt32(_memory, v.Address);

                            if (ptr >= StackSize && ptr < StackSize + _heapend)
                                reachable.Add(ptr);
                        }
                    }
                //Sweep
                int pos = 0;
                while (pos < _heapend)
                {
                    int headerPos = pos + StackSize;
                    int len = BitConverter.ToInt32(_memory, headerPos);
                    bool used = _memory[headerPos + 4] != 0;

                    int dataAddr = pos + HeaderSize + StackSize;

                    if (used && !reachable.Contains(dataAddr))
                        _memory[headerPos + 4] = 0;//free

                    pos += len;//next block
                }
            }
            private void WriteHeader(int pos, int len, ValueType vt, bool used)
            {
                BitConverter.GetBytes(len).CopyTo(_memory, pos);
                _memory[pos + 4] = used ? (byte)1 : (byte)0;
                _memory[pos + 5] = (byte)vt;
            }
            public ValueType GetHeapObjectType(int addr) => (ValueType)_memory[addr - HeaderSize + 5];
            public int GetHeapObjectLength(int addr) => (BitConverter.ToInt32(_memory, addr - HeaderSize))-HeaderSize;
            private int GetBlockPayloadSize(int addr)
            {
                int len = BitConverter.ToInt32(_memory, addr - HeaderSize);
                return len - HeaderSize;
            }
            public Span<byte> GetSpan(int addr) => _memory.AsSpan(addr, GetBlockPayloadSize(addr));
            public void Free(int addr)
            {

                int blockStart = addr - HeaderSize;
                if (blockStart < StackSize || blockStart >= _heapend + StackSize)
                    throw new ArgumentOutOfRangeException(nameof(addr));
                if (_memory[blockStart + 4] == 0)
                    throw new InvalidOperationException($"double-free or invalid pointer {addr}");
                _memory[blockStart + 4] = 0;//free flag
            }
            private void EnsureAlive(int addr, int need)
            {
                int p = addr - HeaderSize;
                if (p < 0 || p >= _heapend)
                    throw new ArgumentOutOfRangeException(nameof(addr));

                if (_memory[p + 4] == 0)
                    throw new InvalidOperationException("use-after-free");

            }

            public void WriteBytes(int addr, ReadOnlySpan<byte> src)
            {
                int len = GetBlockPayloadSize(addr);
                if (src.Length > len) throw new ArgumentException("too big");

                src.CopyTo(_memory.AsSpan(addr, src.Length));
            }

            public int Malloc(int size, ValueType valueType)
            {
                int pos = 0;
                while (pos < _heapend)
                {
                    int len = BitConverter.ToInt32(_memory, pos + StackSize);
                    bool used = _memory[pos + 4 + StackSize] != 0;

                    if (!used && len >= size + HeaderSize)
                    {
                        _memory[pos + 4 + StackSize] = 1;
                        return pos + HeaderSize + StackSize;
                    }
                    pos += len;
                }

                // bump-pointer – конец кучи
                int need = size + HeaderSize;
                if (_heapend + need > _memory.Length - StackSize)
                    throw new OutOfMemoryException();

                WriteHeader(_heapend + StackSize, need, valueType, used: true);
                int addr = _heapend + HeaderSize;
                _heapend += need;
                return addr + StackSize;
            }
            #endregion

            #endregion
            public void Check()
            {
                if (CancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Execution was cancelled");
                if (_scopes.Count>100) throw new OutOfMemoryException($"Too many scopes alive, danger of stack overflow");
            }
            #region Variable pointer dictionary manager
            public void Declare(string name, ValueType type, object? value)
            {

                if (_scopes.Peek().ContainsKey(name))
                    throw new Exception($"Variable '{name}' already declared");
                if (type == ValueType.String)
                {
                    if (value is string s)
                    {
                        var bytes = Encoding.UTF8.GetBytes(s);
                        if (!MatchVariableType(value, type))
                            throw new Exception($"Type mismatch in declaration of {value.GetType()} '{name}' expected {type} during asignment");
                        var address = Malloc(bytes.Length, ValueType.String);
                        var pointer = Stackalloc(ValueType.String);
                        _scopes.Peek()[name] = pointer;
                        WriteVariable(name, address);
                        WriteBytes(address, bytes);
                    }
                    else if (value is null)
                    {
                        _scopes.Peek()[name] = new Variable(type, -1, sizeof(int));
                    }
                    else throw new ApplicationException("Declaring string with non-string value");
                }
                else
                {

                    if (type == ValueType.Object && value is not null) type = InferType(value);
                    if (type == ValueType.Object && value is not null) throw new ApplicationException($"Allocating variable with undefined type {value.GetType()}");
                    var variable = Stackalloc(type);
                    _scopes.Peek()[name] = variable;
                    if (value is not null)
                    {
                        if (value is not int && type == ValueType.Int) value = Convert.ToInt32(value);
                        if (value is not double && type == ValueType.Double) value = Convert.ToDouble(value);
                        if (value is not float && type == ValueType.Float) value = Convert.ToSingle(value);
                        if (value is not ulong && type == ValueType.Ulong) value = Convert.ToUInt64(value);
                        if (value is not long && type == ValueType.Long) value = Convert.ToInt64(value);
                        if (value is not short && type == ValueType.Short) value = Convert.ToInt16(value);
                        if (value is not decimal && type == ValueType.Decimal) value = Convert.ToDecimal(value);
                        if (value is not byte && type == ValueType.Byte) value = Convert.ToByte(value);
                        if (value is not sbyte && type == ValueType.Sbyte) value = Convert.ToSByte(value);
                        if (value is not ushort && type == ValueType.UShort) value = Convert.ToUInt16(value);
                        if (value is not uint && type == ValueType.Uint) value = Convert.ToUInt32(value);


                        if (!MatchVariableType(value, type))
                            throw new ApplicationException($"Type mismatch in declaration of {value.GetType()} '{name}' expected {type} during asignment");
                        WriteVariable(name, value);
                    }
                }

            }
            private AstTree.ValueType InferType(object v) => v switch
            {
                int => ValueType.Int,
                double => ValueType.Double,
                float => ValueType.Float,
                bool => ValueType.Bool,
                char => ValueType.Char,
                long => ValueType.Long,
                ulong => ValueType.Ulong,
                uint => ValueType.Uint,
                short => ValueType.Short,
                ushort => ValueType.UShort,
                byte => ValueType.Byte,
                sbyte => ValueType.Sbyte,
                decimal => ValueType.Decimal,
                string => ValueType.String,
                _ => ValueType.Object,
                //_ => throw new NotSupportedException($"Cannot infer ValueType for {v.GetType()}")
            };
            public object Cast(object value, ValueType dest)
            {
                static decimal ToDec(object v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture);

                checked
                {
                    return dest switch
                    {
                        ValueType.Int => (int)ToDec(value),
                        ValueType.Long => (long)ToDec(value),
                        ValueType.Short => (short)ToDec(value),
                        ValueType.Sbyte => (sbyte)ToDec(value),

                        ValueType.Uint => (uint)ToDec(value),
                        ValueType.Ulong => (ulong)ToDec(value),
                        ValueType.UShort => (ushort)ToDec(value),
                        ValueType.Byte => (byte)ToDec(value),

                        ValueType.IntPtr => (IntPtr)(int)ToDec(value),

                        ValueType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                        ValueType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                        ValueType.Decimal => ToDec(value),

                        ValueType.Char => Convert.ToChar(value, CultureInfo.InvariantCulture),
                        ValueType.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                        ValueType.String => value.ToString()!,

                        _ => value
                    };
                }
            }
            public Variable? GetVariableByAddress(int addr)
            {
                foreach (var scope in _scopes)
                    foreach (var v in scope.Values)
                        if (v.Address == addr)
                            return v;
                return null;//dead memory
            }
            public Variable Get(string name)
            {
                foreach (var scope in _scopes)
                {
                    if (scope.TryGetValue(name, out var reference))
                        return reference;
                }
                throw new Exception($"Variable '{name}' not found");
            }
            #endregion

        }
        #endregion
        public class Variable
        {
            public ValueType Type { get; }
            public int Address { get; set; }
            public int Size { get; set; }

            public Variable(ValueType type, int address, int size)
            {
                Type = type;
                Address = address;
                Size = size;
            }
        }
        public abstract class AstNode
        {
            public abstract object Evaluate(ExecutionContext context);
            public abstract void Print(string indent = "", bool isLast = true);
        }
        #region Operator Nodes
        public class UnaryOpNode : AstNode
        {
            public OperatorToken Op { get; }
            public AstNode Operand { get; }
            public bool IsPostfix;
            public UnaryOpNode(OperatorToken op, AstNode operand, bool postfix = false)
            { Op = op; Operand = operand; IsPostfix = postfix; }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();

                switch (Op)
                {
                    case OperatorToken.Not:
                        return !(bool)Operand.Evaluate(context);
                    case OperatorToken.Minus:
                        return Negate(Operand.Evaluate(context));
                    case OperatorToken.AddressOf:
                        if (Operand is VariableReferenceNode vr)
                        {
                            var vInfo = context.Get(vr.Name);
                            return AstTree.IsReferenceType(vInfo.Type) ? context.ReadVariable(vr.Name) : vInfo.Address;
                        }
                        throw new Exception($"& operator requires a variable, got {Operand.GetType()}");
                    case OperatorToken.Multiply: //*ptr dereference
                        if (Operand is VariableReferenceNode operand)
                        {
                            var vInfo = context.Get(operand.Name);
                            if (vInfo.Type != ValueType.IntPtr)
                                throw new Exception("Cannot dereference non‑pointer variable");
                            
                            int addr = (int)context.ReadVariable(operand.Name);
                            
                            if(addr >= context.StackSize)//heap object
                            {
                                var type = context.GetHeapObjectType(addr);
                                int len = context.GetHeapObjectLength(addr);
                                if(type == ValueType.String) return Encoding.UTF8.GetString(context.RawMemory, addr, len);

                                throw new NotImplementedException($"Dereferencing unknown type in heap: {type}");
                            }
                            var varInfo = context.GetVariableByAddress(addr);
                            if (varInfo is null) throw new InvalidOperationException("Stack pointer to dead memory");
                            return context.ReadFromStack(addr, varInfo.Type);
                        }
                        int p = Convert.ToInt32(Operand.Evaluate(context));
                        return BitConverter.ToInt32(context.RawMemory, p);

                    case OperatorToken.Increment:
                        return IntIncrement(context, 1);
                    case OperatorToken.Decrement:
                        return IntIncrement(context, -1);
                }
                throw new ApplicationException($"Unimplement unary operator in unary node {Op}");

            }
            private object IntIncrement(ExecutionContext ctx, int add)
            {
                if (Operand is VariableReferenceNode vr)
                {
                    var cur = ctx.ReadVariable(vr.Name);
                    object newVal = Add(cur, add);
                    ctx.WriteVariable(vr.Name, newVal); 
                    return IsPostfix ? cur : newVal;
                }
                var val = Operand.Evaluate(ctx); 
                return Add(val, add);
            }
            private static object Add(object value, int delta) => value switch
            {
                int i => i + delta,
                long l => l + delta,
                float f => f + delta,
                double d => d + delta,
                decimal m => m + delta,
                ulong ul => ul + (uint)delta,
                uint ui => ui + delta,
                short s => s + delta,
                ushort s => s + delta,
                byte b => b + delta,
                sbyte sb => sb + delta,
                _ => throw new ApplicationException($"Increment operator not supported for type {value.GetType()}")
            };
            private object Negate(object value) => value switch
            {
                int i => -i,
                double d => -d,
                decimal de => -de,
                float f => -f,
                long l => -l,
                short s => -s,
                sbyte sb => -sb,
                _ => throw new Exception($"Increment operator not supported for type {value.GetType()}")
            };

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── {(Op == OperatorToken.Multiply ? "Dereference": Op)}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Operand.Print(childIndent, true);
            }
        }
        public class BinOpNode : AstNode
        {
            public AstNode Left { get; }
            public OperatorToken Op { get; }
            public AstNode Right { get; }

            public BinOpNode(AstNode left, OperatorToken op, AstNode right)
            {
                Left = left;
                Op = op;
                Right = right;
            }
            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var l = Left.Evaluate(context);
                var r = Right.Evaluate(context);
                object result;
                if ((Op == OperatorToken.Plus || Op == OperatorToken.PlusEqual) && (l is string || r is string))
                {
                    result = (l?.ToString() ?? string.Empty) + (r?.ToString() ?? string.Empty);
                }
                else if (l is bool bl && r is bool br) result = EvaluateBool(bl, br, Op);
                else if (l is int i) result = EvaluateBinary(i, Convert.ToInt32(r), Op);
                else if (l is double d) result = EvaluateBinary(d, Convert.ToDouble(r), Op);
                else if (l is float f) result = EvaluateBinary(f, Convert.ToSingle(r), Op);
                else if (l is decimal de) result = EvaluateBinary(de, Convert.ToDecimal(r), Op);
                else if (l is long ll) result = EvaluateBinary(ll, Convert.ToInt64(r), Op);
                else if (l is ulong ul) result = EvaluateBinary(ul, Convert.ToUInt64(r), Op);
                else if (l is uint ui) result = EvaluateBinary(ui, Convert.ToUInt32(r), Op);
                else if (l is short s) result = EvaluateBinary(s, Convert.ToInt16(r), Op);
                else if (l is ushort us) result = EvaluateBinary(us, Convert.ToUInt16(r), Op);
                else if (l is byte b) result = EvaluateBinary(b, Convert.ToByte(r), Op);
                else if (l is sbyte sb) result = EvaluateBinary(sb, Convert.ToSByte(r), Op);
                else throw new ApplicationException($"Unsupported operator {Op} for Type {l.GetType()}");
                if (Left is VariableReferenceNode variable && (Op == OperatorToken.PlusEqual ||
                    Op == OperatorToken.MinusEqual || Op == OperatorToken.MultiplyEqual ||
                    Op == OperatorToken.DivideEqual || Op == OperatorToken.Equals))
                {
                    var value = context.Get(variable.Name);
                    context.WriteVariable(variable.Name, result);
                }
                return result;
            }
            public static bool EvaluateBool(bool l, bool r, OperatorToken op) => op switch
            {
                OperatorToken.Equals => l == r,
                OperatorToken.NotEqual => l != r,
                OperatorToken.And => l && r,
                OperatorToken.Or => l || r,
                _ => throw new ApplicationException($"Unsupported operation {op} for bool type")
            };
            public static object EvaluateBinary<T>(T l, T r, OperatorToken op) where T : INumber<T>
            {
                return op switch
                {
                    OperatorToken.Plus => l + r,
                    OperatorToken.PlusEqual => l + r,
                    OperatorToken.Minus => l - r,
                    OperatorToken.MinusEqual => l - r,
                    OperatorToken.Multiply => l * r,
                    OperatorToken.MultiplyEqual => l * r,
                    OperatorToken.Divide => l / r,
                    OperatorToken.DivideEqual => l / r,
                    OperatorToken.Greater => l > r,
                    OperatorToken.Less => l < r,
                    OperatorToken.GreaterOrEqual => l >= r,
                    OperatorToken.LessOrEqual => l <= r,
                    OperatorToken.Equal => l == r,
                    OperatorToken.NotEqual => l != r,
                    OperatorToken.Module => l % r,
                    OperatorToken.Equals => r,
                    _ => throw new ApplicationException($"Unsupported operation {op} for type {typeof(T)}")
                };
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── {Op}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Left.Print(childIndent, false);
                Right.Print(childIndent, true);
            }
        }
        #endregion
        #region Values nodes
        #region Variabe nodes
        public class VariableDeclarationNode : AstNode
        {
            public string Name { get; }
            public ValueType Type { get; }
            public AstNode Expression { get; }

            public VariableDeclarationNode(ValueType type, string name, AstNode expression)
            {
                Type = type;
                Name = name;
                Expression = expression;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var value = Expression.Evaluate(context);
                context.Declare(Name, Type, value);
                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── declare {Type.ToString().ToLower()} {Name}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Expression.Print(childIndent, true);
            }

        }
        public class VariableReferenceNode : AstNode
        {
            public string Name { get; }

            public VariableReferenceNode(string name) => Name = name;

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var v = context.Get(Name);
                if (IsReferenceType(v.Type))
                {
                    var pointer = context.ReadVariable(Name);
                    if (pointer is int ptr)
                    {
                        if (v.Type == ValueType.String) return Encoding.UTF8.GetString(context.GetSpan(ptr));
                        else throw new ApplicationException($"Reading object: {v.Type}");
                    }
                    else throw new ApplicationException($"non-int pointer: {pointer.GetType()}");
                }
                else return context.ReadVariable(Name);
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── var {Name}");
            }
        }
        public class AssignmentNode : AstNode
        {
            public string Name { get; }
            public AstNode Expression { get; }

            public AssignmentNode(string name, AstNode expression)
            {
                Name = name;
                Expression = expression;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var variable = context.Get(Name);
                var value = Expression.Evaluate(context);

                if (!TypeMatches(value, variable.Type))
                    throw new Exception($"Cannot assign value of incompatible type to '{Name}'");

                context.WriteVariable(Name, value);
                return null;
            }

            private bool TypeMatches(object value, ValueType type) =>
                MatchVariableType(value, type);
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── assign {Name}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Expression.Print(childIndent, true);
            }
        }
        #endregion
        public class LiteralNode : AstNode
        {
            public object? Value { get; }

            public LiteralNode(object? value) => Value = value;

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                return Value;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                string prefix = Value is string ? "\"" : "";
                string value = prefix + (Value is not null ? Value : "null") + prefix;
                Console.WriteLine($"{indent}└── value {value}");
            }

        }
        public class CastNode : AstNode
        {
            public ValueType Target;
            public AstNode Expr;
            public CastNode(ValueType t, AstNode e) { Target = t; Expr = e; }

            public override object Evaluate(ExecutionContext ctx)
            {
                var val = Expr.Evaluate(ctx);
                return ctx.Cast(val, Target);
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── cast({Target.ToString().ToLower()})");
                string child = indent + (isLast ? "    " : "│   ");
                Expr.Print(child, true);
            }
        }

        #endregion
        
        #region Flow control nodes
        #region Loop nodes
        public class WhileNode : AstTree.AstNode
        {
            public AstTree.AstNode Condition { get; }
            public AstTree.AstNode Body { get; }

            public WhileNode(AstTree.AstNode condition, AstTree.AstNode body)
            {
                Condition = condition;
                Body = body;
            }

            public override object Evaluate(ExecutionContext context)
            {
                while (true)
                {
                    var cond = Condition.Evaluate(context);
                    if (!(cond is bool bb && bb)) break;

                    var res = Body.Evaluate(context);
                    switch (res)
                    {
                        case BreakSignal: break;
                        case ContinueSignal: continue;
                        case ReturnSignal: return res;
                    }

                }

                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── while");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Condition.Print(childIndent, false);
                Body.Print(childIndent, true);
            }
        }
        public class DoWhileNode : AstTree.AstNode
        {
            public AstTree.AstNode Body { get; }
            public AstTree.AstNode Condition { get; }

            public DoWhileNode(AstTree.AstNode body, AstTree.AstNode condition)
            {
                Body = body;
                Condition = condition;
            }

            public override object Evaluate(ExecutionContext context)
            {
                do
                {
                    var res = Body.Evaluate(context);
                    if (res is BreakSignal) break;
                    if (res is ContinueSignal) { }
                    if (res is ReturnSignal) return res;

                }
                while (Condition.Evaluate(context) is bool b && b);

                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── do-while");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Body.Print(childIndent, false);
                Condition.Print(childIndent, true);
            }
        }
        public class ForNode : AstTree.AstNode
        {
            public AstTree.AstNode Init { get; }
            public AstTree.AstNode Condition { get; }
            public AstTree.AstNode Step { get; }
            public AstTree.AstNode Body { get; }

            public ForNode(AstTree.AstNode init, AstTree.AstNode condition, AstTree.AstNode step, AstTree.AstNode body)
            {
                Init = init;
                Condition = condition;
                Step = step;
                Body = body;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.EnterScope();
                try
                {
                    Init?.Evaluate(context);
                    while (true)
                    {
                        var cond = Condition?.Evaluate(context);
                        if (cond is bool b && !b) break;

                        var res = Body.Evaluate(context);
                        if (res is BreakSignal) break;
                        if (res is ContinueSignal) { Step?.Evaluate(context); continue; }
                        if (res is ReturnSignal) return res;

                        Step?.Evaluate(context);
                    }
                }
                finally { context.ExitScope(); }

                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── for");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Init?.Print(childIndent, false);
                Condition?.Print(childIndent, false);
                Step?.Print(childIndent, false);
                Body.Print(childIndent, true);
            }
        }
        #endregion
        public class IfNode : AstNode
        {
            public AstNode Condition { get; }
            public AstNode ThenBody { get; }
            public AstNode? ElseBody { get; }

            public IfNode(AstNode condition, AstNode body, AstNode? elseBody = null)
            {
                Condition = condition;
                ThenBody = body;
                ElseBody = elseBody;
            }

            public override object Evaluate(ExecutionContext context)
            {
                var cond = Condition.Evaluate(context);
                if (cond is bool b && b)
                {
                    var res = ThenBody.Evaluate(context);
                    if (res is ReturnSignal or BreakSignal or ContinueSignal)
                        return res;
                }
                else if(ElseBody is not null)
                {
                    var res = ElseBody.Evaluate(context);
                    if (res is ReturnSignal or BreakSignal or ContinueSignal)
                        return res;
                }
                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── if");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Condition.Print(childIndent, false);
                ThenBody.Print(childIndent, true);
            }

        }
        public class ConditionalNode : AstNode
        {
            public AstNode Condition, IfTrue, IfFalse;
            public ConditionalNode(AstNode cond, AstNode t, AstNode f)
            { Condition = cond; IfTrue = t; IfFalse = f; }

            public override object Evaluate(ExecutionContext ctx)
            {
                var v = Condition.Evaluate(ctx);
                return (v is bool b && b) ? IfTrue.Evaluate(ctx)
                                          : IfFalse.Evaluate(ctx);
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── ?:");
                string ci = indent + (isLast ? "    " : "│   ");
                Condition.Print(ci, false);
                IfTrue.Print(ci, false);
                IfFalse.Print(ci, true);
            }
        }

        public class TryCatchNode : AstNode
        {
            public AstNode TryBlock, CatchBlock, FinallyBlock;
            public string? ExVar;
            public TryCatchNode(AstNode @try, AstNode @catch, AstNode? @finally, string? exVar)
            {
                TryBlock = @try;
                CatchBlock = @catch;
                FinallyBlock = @finally ?? new LiteralNode(null);
                ExVar = exVar;
            }

            public override object Evaluate(ExecutionContext ctx)
            {
                try
                {
                    var res = TryBlock.Evaluate(ctx);
                    return res;
                }
                catch (Exception ex)
                {
                    ctx.EnterScope();
                    try
                    {
                        if (ExVar != null)
                            ctx.Declare(ExVar, ValueType.String, ex.Message);
                        var res = CatchBlock.Evaluate(ctx);
                        return res;
                    }
                    finally { ctx.ExitScope(); }
                }
                finally
                {
                    if (FinallyBlock is not LiteralNode) FinallyBlock.Evaluate(ctx);
                }

            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── try-catch");
                string ci = indent + (isLast ? "    " : "│   ");
                TryBlock.Print(ci, false);
                CatchBlock.Print(ci, true); 
                if (FinallyBlock is not LiteralNode)
                    FinallyBlock.Print(ci, true);

            }
        }
        public class SwitchNode : AstNode
        {
            public AstNode Discriminant;
            public List<(AstNode? value, List<AstNode> body)> Cases;

            public SwitchNode(AstNode disc, List<(AstNode? value, List<AstNode> body)> cases)
            {
                Discriminant = disc;
                Cases = cases;
            }

            public override object Evaluate(ExecutionContext ctx)
            {
                var discVal = Discriminant.Evaluate(ctx);

                bool execute = false;
                foreach (var (valExpr, body) in Cases)
                {
                    if (!execute)
                    {
                        if (valExpr == null) //default
                            execute = true;
                        else
                            execute = Equals(discVal, valExpr.Evaluate(ctx));
                    }

                    if (execute)
                    {
                        foreach (var stmt in body)
                        {
                            var r = stmt.Evaluate(ctx);
                            if (r is BreakSignal) return null; // break from switch
                            if (r is ContinueSignal or ReturnSignal) return r;
                        }
                    }
                }
                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            { 
                Console.WriteLine($"{indent}└── switch");
                Discriminant.Print(indent + "    ");
                string childIndent = indent + (isLast ? "    " : "│   ");

                for (int i = 0; i < Cases.Count; i++)
                {
                    if (Cases[i].value is not null) Cases[i].value.Print(childIndent, isLast);
                    else Console.WriteLine($"{childIndent}└── default");
                    if (Cases[i].body is not null) foreach (var body in Cases[i].body) body.Print((childIndent) + (i==Cases.Count-1 ? "    " : "│   "), false);
                }
            }
        }

        public class ThrowNode : AstNode
        {
            public AstNode Expr;
            public ThrowNode(AstNode expr) => Expr = expr;

            public override object Evaluate(ExecutionContext ctx)
            {
                var msg = Expr.Evaluate(ctx)?.ToString();
                throw new Exception(msg);
            }
            public override void Print(string indent = "", bool isLast = true)
            { Console.WriteLine($"{indent}└── throw"); Expr.Print(indent + (isLast ? "    " : "│   ")); }
        }

        public class LabelNode : AstNode
        {
            public string Name { get; }

            public LabelNode(string name) => Name = name;

            public override object Evaluate(ExecutionContext context)
            {
                //для Goto
                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── label {Name}");
            }

        }
        public class GotoNode : AstNode
        {
            public string TargetLabel { get; }

            public GotoNode(string label) => TargetLabel = label;

            public override object Evaluate(ExecutionContext context)
            {
                throw new InvalidOperationException("GotoNode must be handled by StatementListNode");
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── goto {TargetLabel}");
            }

        }
        #region Signals
        public class ReturnSignal
        {
            public object? Value;
            public ReturnSignal(object? value) => Value = value;
        }
        public class BreakSignal { }
        public class ContinueSignal { }
        #endregion
        #region Signal nodes
        public class BreakNode : AstNode
        {
            public override object Evaluate(ExecutionContext _) => new BreakSignal();
            public override void Print(string i = "", bool l = true) =>
                Console.WriteLine($"{i}└── break");
        }
        public class ContinueNode : AstNode
        {
            public override object Evaluate(ExecutionContext _) => new ContinueSignal();
            public override void Print(string i = "", bool l = true) =>
                Console.WriteLine($"{i}└── continue");
        }
        public class ReturnNode : AstNode
        {
            public AstNode? Expression;
            public ReturnNode(AstNode? expr) => Expression = expr;
            public override object Evaluate(ExecutionContext ctx) =>
                new ReturnSignal(Expression?.Evaluate(ctx));
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── return");
                string childIndent = indent + (isLast ? "    " : "│   ");
                if (Expression is not null) Expression.Print(childIndent, isLast);
            }
        }
        #endregion
        #endregion
        #region Structure nodes
        public class StatementListNode : AstNode
        {
            public List<AstNode> Statements { get; }

            public StatementListNode(List<AstNode> statements)
            {
                Statements = statements;
            }

            public override object Evaluate(ExecutionContext context)
            {
                //lables map
                var labels = new Dictionary<string, int>();
                for (int i = 0; i < Statements.Count; i++)
                {
                    if (Statements[i] is LabelNode label)
                        labels[label.Name] = i;
                }

                context.Labels = labels;

                //main cycle
                int ip = 0; // instruction pointer
                while (ip < Statements.Count)
                {
                    context.Check();

                    var node = Statements[ip];

                    if (node is GotoNode gotoNode)
                    {
                        if (!labels.TryGetValue(gotoNode.TargetLabel, out var newIp))
                            throw new Exception($"Label '{gotoNode.TargetLabel}' not found");
                        ip = newIp;
                    }
                    else
                    {
                        node.Evaluate(context);
                        ip++;
                    }
                }

                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── Root");
                string childIndent = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Statements.Count; i++)
                {
                    Statements[i].Print(childIndent, i == Statements.Count - 1);
                }
            }

        }
        public class BlockNode : AstNode
        {
            public List<AstNode> Statements { get; }

            public BlockNode(List<AstNode> statements)
            {
                Statements = statements;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.EnterScope();

                try
                {
                    object result = null;
                    foreach (var statement in Statements)
                    {
                        var res = statement.Evaluate(context);
                        if (res is ReturnSignal or BreakSignal or ContinueSignal) return res;
                    }
                    return result;
                }
                finally
                {
                    context.ExitScope();
                }

            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── block");
                string childIndent = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Statements.Count; i++)
                {
                    Statements[i].Print(childIndent, i == Statements.Count - 1);
                }
            }

        }
        public sealed class EmptyNode : AstNode
        {
            public override object? Evaluate(ExecutionContext ctx) => null;
            public override void Print(string i = "", bool l = true)
                => Console.WriteLine($"{i}└── empty");
        }

        #endregion

        #region Function nodes
        public class FunctionDeclarationNode : AstNode
        {
            public ValueType? ReturnType; //null is void
            public string Name;
            public string[] Params; 
            public ValueType[] ParamTypes;
            public AstNode Body;
            public FunctionDeclarationNode(ValueType? ret, string name, string[] @params, ValueType[] types, AstNode body)
            { ReturnType = ret; Name = name; Params = @params; ParamTypes = types; Body = body; }

            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.AddFunction(Name, new ExecutionContext.Function
                {
                    ReturnType = ReturnType ?? ValueType.Object,
                    ParamNames = Params,
                    ParamTypes = ParamTypes,
                Body = Body
                });
                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── function {Name}({string.Join(", ", Params)})");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Body.Print(childIndent, false);
            }

        }
        public class CallNode : AstNode
        {
            public string Name;
            public AstNode[] Args;
            public CallNode(string name, AstNode[] args) { Name = name; Args = args; }
            public override object Evaluate(ExecutionContext ctx)
            {
                if (ctx.NativeFunctions.TryGetValue(Name, out var overloads))
                {
                    var argVals = Args.Select(a => a.Evaluate(ctx)).ToArray();
                    foreach (var del in overloads)
                    {
                        var pars = del.Method.GetParameters();
                        if (pars.Length != argVals.Length) continue;

                        try
                        {
                            object?[] conv = new object?[argVals.Length];
                            bool ok = true;
                            for (int i = 0; i < pars.Length; i++)
                            {
                                var need = pars[i].ParameterType;
                                var have = argVals[i];

                                if (have == null && need.IsClass) { conv[i] = null; continue; }

                                if (need.IsInstanceOfType(have))
                                { conv[i] = have; continue; }

                                if (have is IConvertible)
                                {
                                    conv[i] = Convert.ChangeType(have, need);
                                    continue;
                                }
                                ok = false; break;
                            }
                            if (!ok) continue;

                            return del.DynamicInvoke(conv);
                        }
                        catch { }
                    }
                    throw new Exception($"No native overload '{Name}' matches given arguments ({argVals.Length})");
                }

                if (!ctx.Functions.TryGetValue(Name, out var overloads2))
                    throw new ApplicationException($"Function '{Name}' not defined");
                var argVals2 = Args.Select(a => a.Evaluate(ctx)).ToArray();
                var candidates = overloads2.Where(f => f.ParamNames.Length == argVals2.Length).ToList();
                int BestScore(ReadOnlySpan<object?> args, ExecutionContext.Function f)
                {
                    int score = 0;
                    for (int i = 0; i < args.Length; i++)
                    {
                        var need = f.ParamTypes[i];
                        var have = args[i];
                        if (have is null) score += need == ValueType.Object ? 2 : 0;
                        else if (AstTree.MatchVariableType(have, need)) score += 3;
                        else if (need == ValueType.Object) score += 1;
                        else if (have is IConvertible) score += 0;
                        else return -1;
                    }
                    return score;
                }
                var best = candidates.Select(f => (fn: f, score: BestScore(argVals2, f))).Where(t => t.score >= 0)
                    .OrderByDescending(t => t.score).FirstOrDefault();
                var fn = best.fn;
                ctx.EnterScope();
                try
                {
                    for (int i = 0; i < fn.ParamNames.Length; i++)
                    {
                        var val = Args[i].Evaluate(ctx);
                        if (!MatchVariableType(val, fn.ParamTypes[i]) && fn.ParamTypes[i] != ValueType.Object)
                        {
                            try { val = ctx.Cast(val, fn.ParamTypes[i]); }
                            catch { throw new ApplicationException( $"Cannot cast {val?.GetType().Name} to {fn.ParamTypes[i]}"); }
                        }
                        ctx.Declare(fn.ParamNames[i], fn.ParamTypes[i], val);
                    }

                    var ret = fn.Body.Evaluate(ctx);
                    var result = ret is ReturnSignal rs ? rs.Value : null;
                    if (fn.ReturnType is { } declared && declared != ValueType.Object && result is not null)
                    {
                        if (!MatchVariableType(result, declared))
                            result = ctx.Cast(result, declared);
                    }

                    return result;
                }
                finally { ctx.ExitScope(); }
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── call {Name}()");
                string childIndent = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i<Args.Length; i++)
                {
                    Args[i].Print(childIndent, i == Args.Length - 1);
                }
            }

        }
        #endregion

    }
}
