namespace S_Interpretor
{
    using System;
    using System.Globalization;
    using System.Numerics;
    using System.Reactive;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;

    /*
    internal class Program
    {
        static void Main(string[] args)
        {
            string code = File.ReadAllText("code.cs");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
            var ast = new Ast(cts.Token);
            ast.Interpret(code, consoleOutput: true, printTree: false);
            }
        }
    }
    */

    #region Parser
    public class Parser
    {
        #region Lexer
        public class Lexer
        {
            private readonly string _text;
            private int _position;
            public Lexer(string text) => _text = text;
            public int GetCodeLength() => _text.Length;
            public char GetAtPosition(int pos) => _text[pos];
            public Ast.TokenType CurrentTokenType { get; private set; }
            public string CurrentTokenText { get; private set; }

            private static readonly Dictionary<string, Ast.OperatorToken> OperatorMap = new()
            {
            { "+", Ast.OperatorToken.Plus },
            { "-", Ast.OperatorToken.Minus },
            { "*", Ast.OperatorToken.Multiply },
            { "/", Ast.OperatorToken.Divide },
            { "!", Ast.OperatorToken.Not },
            { "++", Ast.OperatorToken.Increment },
            { "--", Ast.OperatorToken.Decrement },
            { "==", Ast.OperatorToken.Equal },
            { "!=", Ast.OperatorToken.NotEqual },
            { ">", Ast.OperatorToken.Greater },
            { "<", Ast.OperatorToken.Less },
            { "<=", Ast.OperatorToken.LessOrEqual },
            { ">=", Ast.OperatorToken.GreaterOrEqual },
            { "+=", Ast.OperatorToken.PlusEqual },
            { "-=", Ast.OperatorToken.MinusEqual },
            { "*=", Ast.OperatorToken.MultiplyEqual },
            { "/=", Ast.OperatorToken.DivideEqual },
            { "%", Ast.OperatorToken.Module },
            { ">>", Ast.OperatorToken.RightShift },
            { "<<", Ast.OperatorToken.LeftShift },
            { "=", Ast.OperatorToken.Equals },
            { "&", Ast.OperatorToken.AddressOf },
            { "&&", Ast.OperatorToken.And },
            { "||", Ast.OperatorToken.Or },
            { "=>", Ast.OperatorToken.Lambda },
            };
            private static readonly Dictionary<string, Ast.TokenType> TokenTypeMap = new()
            {
            { ";", Ast.TokenType.Semicolon },
            { ":", Ast.TokenType.Colon },
            { "(", Ast.TokenType.ParenOpen },
            { ")", Ast.TokenType.ParenClose },
            { "{", Ast.TokenType.BraceOpen },
            { "}", Ast.TokenType.BraceClose },
            { "[", Ast.TokenType.BracketsOpen },
            { "]", Ast.TokenType.BracketsClose },
            { "=", Ast.TokenType.Equals },
            { ",", Ast.TokenType.Comma },
            { "?", Ast.TokenType.Question },
            { ".", Ast.TokenType.Dot },
            };

            private static readonly HashSet<string> Keywords = new() { "int", "string", "char", "byte", "float", "long", "ulong", "uint", "bool", "double", "IntPtr", "intPtr", "object",
            "if", "goto", "for", "while", "do", "void","return", "break","continue","try","catch", "switch", "case", "finally" , "var", "default", "throw", "else", "new", "foreach", "in",
            "using", "struct", "class", "checked", "unchecked", "out", "const", "enum", "ref", "private", "public", "protected", "this", "static", "abstract", "interface", "delegate", "as",
            "internal", "unsafe", "sealed", "virtual", "override", "partial", "readonly", "params", "lock", "implicit", "explicit", "fixed", "extern", "operator", "namespace", "event", "base"};

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

                bool dotStartsNumber = current == '.' && _position > 0 && char.IsDigit(_text[_position - 1])
                    && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1]);
                //Number literal (int/double)
                if (char.IsDigit(current) || dotStartsNumber)
                {
                    int start = _position;
                    while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
                        _position++;
                    CurrentTokenText = _text.Substring(start, _position - start);
                    CurrentTokenType = Ast.TokenType.Number;
                    return;
                }
                
                //Identifier or keyword
                if (char.IsLetter(current))
                {
                    int start = _position;
                    while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
                        _position++;
                    CurrentTokenText = _text.Substring(start, _position - start);
                    CurrentTokenType = Keywords.Contains(CurrentTokenText) ? Ast.TokenType.Keyword : Ast.TokenType.Identifier;
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

                    CurrentTokenText = _text.Substring(start, _position - start);
                    _position++; //skip "
                    CurrentTokenType = Ast.TokenType.InterpolatedString;
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

            public Ast.OperatorToken ParseOperator()
            {
                if (OperatorMap.TryGetValue(CurrentTokenText, out var token)) return token;
                throw new Exception($"Unknown operator: {CurrentTokenText}");
            }
        }
        #endregion
        private readonly Lexer _lexer;
        private readonly Stack<Dictionary<string, Ast.AstNode>> _constScopes = new();
        private HashSet<string> _declaredEnums = new();
        private int _anonCounter = 0;
        public Parser(string text)
        {
            _lexer = new Lexer(text);
            _lexer.NextToken();
        }

        private void Consume(Ast.TokenType type)
        {if (_lexer.CurrentTokenType != type)
                throw new Exception($"Expected {type}, but got {_lexer.CurrentTokenType}");
            _lexer.NextToken();
        }

        #region Helpers
        private bool IsTypeKeyword(string tokenText) =>
            tokenText == "var" || Enum.TryParse<Ast.ValueType>(tokenText, ignoreCase: true, out _) || _declaredEnums.Contains(tokenText);
        private bool PeekPointerMark()
            => _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "*";
        private Ast.ValueType ReadMaybePointer(Ast.ValueType baseType)
        {
            if (PeekPointerMark()) { _lexer.NextToken(); return Ast.ValueType.IntPtr; }
            return baseType;
        }
        private bool IsPostfixUnary(Ast.OperatorToken token, Ast.OperatorInfo info)
            => info.Associativity == Ast.Associativity.Right && (token == Ast.OperatorToken.Increment || token == Ast.OperatorToken.Decrement);

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }

                if (++i == s.Length) break;//alone /
                char append;
                switch (s[i])
                {
                    case '\\': append = '\\'; break;
                    case '"': append = '"'; break;
                    case 'n': append = '\n'; break;
                    default: append = s[i]; break;
                }
                sb.Append(append);
            }
            return sb.ToString();
        }
        
        private (bool isArray, int?[] dims) ReadArraySuffixes()
        {
            if (_lexer.CurrentTokenType != Ast.TokenType.BracketsOpen)
                return (false, Array.Empty<int?>());

            var list = new List<int?>();
            do
            {
                _lexer.NextToken(); //[

                //fixed size
                do
                {
                    if (_lexer.CurrentTokenType == Ast.TokenType.Number)
                    {
                        list.Add(int.Parse(_lexer.CurrentTokenText));
                        _lexer.NextToken();
                    }
                    else
                        list.Add(null);//jagged

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        continue;
                    }
                    break;
                } while (true);

                Consume(Ast.TokenType.BracketsClose); //]

            } while (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen);

            return (true, list.ToArray());
        }
        private bool IsStructModifier()
        {
            if (_lexer.CurrentTokenType != Ast.TokenType.Keyword) return false;
            switch (_lexer.CurrentTokenText)
            {
                case "partial":
                case "readonly":
                case "unsafe":
                case "public":
                case "private":
                case "protected":
                case "internal": return true;
                default: return false;
            }
        }
        private bool IsClassModifier() => IsStructModifier() 
            || _lexer.CurrentTokenText is "static" or "sealed" or "abstract";

        #endregion
        #region Root/Block parsers
        public Ast.StatementListNode ParseProgram()
        {
            _constScopes.Push(new Dictionary<string, Ast.AstNode>());
            var statements = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
                statements.Add(ParseStatement());
            var hoisted = statements
              .Where(s => s is Ast.FunctionDeclarationNode or Ast.EnumDeclarationNode or Ast.StructDeclarationNode 
              or Ast.InterfaceDeclarationNode or Ast.ClassDeclarationNode)
              .Concat(statements.Where(s => s is not Ast.FunctionDeclarationNode && s is not Ast.EnumDeclarationNode && s is not Ast.StructDeclarationNode
              && s is not Ast.InterfaceDeclarationNode && s is not Ast.ClassDeclarationNode)).ToList();
            return new Ast.StatementListNode(hoisted);
        }
        private Ast.AstNode ParseBlock()
        {
            Consume(Ast.TokenType.BraceOpen); //{
            _constScopes.Push(new Dictionary<string, Ast.AstNode>());
            var statements = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose && _lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
                statements.Add(ParseStatement());
            _constScopes.Pop();
            Consume(Ast.TokenType.BraceClose); //}
            var hoisted = statements
              .Where(s => s is Ast.FunctionDeclarationNode)
              .Concat(statements.Where(s => s is not Ast.FunctionDeclarationNode))
              .ToList();
            return new Ast.BlockNode(hoisted);
        }
        #endregion
        #region Statemet parsers
        private Ast.AstNode ParseStatement()
        {
            if (_lexer.CurrentTokenType == Ast.TokenType.Semicolon)
            {
                _lexer.NextToken(); return new Ast.EmptyNode();
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword)
            {
                if (IsClassModifier())
                {
                    var mods = new List<string>();
                    while (IsClassModifier())
                    {
                        mods.Add(_lexer.CurrentTokenText); _lexer.NextToken();
                    }
                    if (_lexer.CurrentTokenText == "enum") return ParseEnumDeclaration(mods);
                    if (_lexer.CurrentTokenText == "struct") return ParseStructDeclaration(mods);
                    if (_lexer.CurrentTokenText == "interface") return ParseInterfaceDeclaration(mods);
                    if (_lexer.CurrentTokenText == "class") return ParseClassDeclaration(mods);
                    bool isVoid = _lexer.CurrentTokenText == "void";
                    string? retType = isVoid ? null : _lexer.CurrentTokenText;
                    if (isVoid || IsTypeKeyword(_lexer.CurrentTokenText) || _lexer.CurrentTokenType == Ast.TokenType.Identifier)
                    {
                        int savePos = _lexer.Position;
                        var saveType = _lexer.CurrentTokenType;
                        var saveText = _lexer.CurrentTokenText;
                        _lexer.NextToken();//skip type
                        bool looksLikeFunc = _lexer.CurrentTokenType == Ast.TokenType.Identifier;
                        _lexer.NextToken();//skip name
                        if (_lexer.CurrentTokenType != Ast.TokenType.ParenOpen) looksLikeFunc = false;
                        _lexer.ResetCurrent(savePos, saveType, saveText);
                        if (looksLikeFunc) { _lexer.NextToken(); return ParseFunctionDeclaration(isVoid, retType); }
                        else return ParseDeclaration();
                    }
                }
                switch (_lexer.CurrentTokenText)
                {
                    case "if": return ParseIf();
                    case "goto": return ParseGoto();
                    case "while": return ParseWhile();
                    case "for": return ParseFor();
                    case "do": return ParseDoWhile();
                    case "foreach": return ParseForeach();
                    case "return": return ParseReturn();
                    case "break": _lexer.NextToken(); Consume(Ast.TokenType.Semicolon); return new Ast.BreakNode();
                    case "continue": _lexer.NextToken(); Consume(Ast.TokenType.Semicolon); return new Ast.ContinueNode();
                    case "try": return ParseTryCatch();
                    case "switch": return ParseSwitch();
                    case "throw": return ParseThrow();
                    case "void": _lexer.NextToken(); return ParseFunctionDeclaration(isVoid: true);
                    case "using": return ParseUsing();
                    case "const": return ParseConstDeclaration();
                    case "enum": return ParseEnumDeclaration();
                    case "struct": return ParseStructDeclaration();
                    case "interface": return ParseInterfaceDeclaration();
                    case "class": return ParseClassDeclaration();
                }
            }
            //attribute?
            if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
            {
                var attrs = ParseAttributeList();

                //expect function delaration
                if (_lexer.CurrentTokenText == "void" || IsTypeKeyword(_lexer.CurrentTokenText))
                {
                    bool isVoid = _lexer.CurrentTokenText == "void";
                    string? retType = isVoid ? null : _lexer.CurrentTokenText;
                    _lexer.NextToken();//skip type

                    var func = (Ast.FunctionDeclarationNode) ParseFunctionDeclaration(isVoid, retType, attrs: attrs);

                    return func;
                }

                throw new ApplicationException("Attributes are only allowed before function declaration");
            }
            //expect declaration
            if (IsTypeKeyword(_lexer.CurrentTokenText) && !int.TryParse(_lexer.CurrentTokenText, out _))
            {
                //look ahead: function?
                string saveType = _lexer.CurrentTokenText;
                int pos = _lexer.Position;
                Ast.TokenType saveToken = _lexer.CurrentTokenType;
                _lexer.NextToken();//skip (

                if (PeekPointerMark()) 
                    _lexer.NextToken();

                if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                    ReadArraySuffixes();

                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                {
                    var ident = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                    {
                        return ParseFunctionDeclaration(false, saveType, ident);
                    }
                }
                _lexer.ResetCurrent(pos, saveToken, saveType);
                return ParseDeclaration();
            }
            var exprStatement = ParseExpression();
            Consume(Ast.TokenType.Semicolon);
            return exprStatement;
        }
        private Ast.AstNode ParseDeclaration()
        {
            var typeStr = _lexer.CurrentTokenText;
            bool isVar = typeStr == "var";
            _lexer.NextToken();
            var baseType = isVar ? Ast.ValueType.Object : Enum.Parse<Ast.ValueType>(typeStr, true);
            //(bool isArr, int?) arrayInfo = ReadMaybeArraySuffix();
            (bool isArr, int?[] dims) arrayInfo = ReadArraySuffixes();
            if (!isVar) baseType = ReadMaybePointer(baseType);
            var name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            Ast.AstNode expr = new Ast.LiteralNode(null);

            if (_lexer.CurrentTokenText == "=")
            {
                _lexer.NextToken();
                expr = ParseExpression();
            }
            else if (isVar)
                throw new ApplicationException("Variable declared with 'var' must have an initializer");

            Consume(Ast.TokenType.Semicolon);
            if (!isVar && arrayInfo.isArr)//Array
            {
                return new Ast.VariableDeclarationNode(baseType, name, expr, isArray: true, arrayInfo.Item2);
            }
            return new Ast.VariableDeclarationNode(baseType, name, expr);
        }
        private Ast.AstNode ParseConstDeclaration()
        {
            _lexer.NextToken(); //skip const
            if (!IsTypeKeyword(_lexer.CurrentTokenText))
                throw new ApplicationException("Type expected after const");

            var typeTok = _lexer.CurrentTokenText;
            _lexer.NextToken(); //skip type

            var name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            if (_lexer.CurrentTokenText != "=")
                throw new ApplicationException("Const declaration must have an initializer");

            _lexer.NextToken(); //skip =
            Ast.AstNode init = ParseExpression();
            Consume(Ast.TokenType.Semicolon);

            if (init is Ast.LiteralNode lit)
                _constScopes.Peek()[name] = lit;
            else
                return new Ast.VariableDeclarationNode( Enum.Parse<Ast.ValueType>(typeTok, true), 
                    name, init, isArray: false, isConst: true);

            return new Ast.EmptyNode();

        }

        private Ast.AstNode ParseUsing()
        {
            _lexer.NextToken(); // skip using
            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
            {
                //using (var x = ...)
                _lexer.NextToken(); //skip (
                var declaration = ParseDeclaration(); //mb ParseExpression?
                Consume(Ast.TokenType.ParenClose);
                var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
                return new Ast.UsingNode(declaration, body);
            }
            else
            {
                //using MyNamespace;
                var expr = ParseExpression();
                Consume(Ast.TokenType.Semicolon);
                return new Ast.UsingNode(expr, null);
            }
        }

        #endregion
        #region Expression parser 
        private Ast.AstNode ParseExpression(int parentPrecedence = 0)
        {
            Ast.AstNode left = ParseUnary();
            while (true)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Question)
                {
                    const int ternaryPrecedence = 0;
                    if (ternaryPrecedence < parentPrecedence) break;

                    _lexer.NextToken();//skip ?
                    var ifTrue = ParseExpression();
                    Consume(Ast.TokenType.Colon);
                    var ifFalse = ParseExpression(ternaryPrecedence);

                    left = new Ast.ConditionalNode(left, ifTrue, ifFalse);
                    continue;
                }
                if (_lexer.CurrentTokenType != Ast.TokenType.Operator) break;
                var opToken = _lexer.ParseOperator();
                if (!Ast.TryGetOperatorInfo(opToken, out var opInfo)) break;
                if (opInfo.Precedence < parentPrecedence) break;
                if (IsPostfixUnary(opToken, opInfo))
                {
                    _lexer.NextToken();
                    left = new Ast.UnaryOpNode(opToken, left, true);
                    continue;
                }

                _lexer.NextToken();
                var right = ParseExpression(opInfo.Associativity == Ast.Associativity.Left ? opInfo.Precedence + 1 : opInfo.Precedence);

                left = new Ast.BinOpNode(left, opToken, right);
            }

            return left;
        }
        private Ast.AstNode ParseUnary()
        {
            if (_lexer.CurrentTokenType == Ast.TokenType.Operator)
            {
                var op = _lexer.ParseOperator();
                if (Ast.GetOperatorInfo(op).Associativity == Ast.Associativity.Right ||
                    op is Ast.OperatorToken.Minus or Ast.OperatorToken.Multiply)
                {
                    _lexer.NextToken();
                    return new Ast.UnaryOpNode(op, ParseUnary());
                }
            }

            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
            {
                int bakPos = _lexer.Position;
                var bakType = _lexer.CurrentTokenType;
                var bakText = _lexer.CurrentTokenText;

                bool looksLikeLambda = false;
                bool looksLikeCast = false;

                _lexer.NextToken(); //skip (
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                {
                    _lexer.NextToken();
                    looksLikeLambda = _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>";
                }
                else if (_lexer.CurrentTokenType == Ast.TokenType.Keyword &&
                         IsTypeKeyword(_lexer.CurrentTokenText)) // (int
                {
                    _lexer.NextToken();//skip type
                    if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                    {
                        looksLikeLambda = true; // (int x
                    }
                    else if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                    {
                        _lexer.NextToken(); //skip (
                        if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>")
                            looksLikeLambda = true; // (int)=>
                        else
                            looksLikeCast = true; // (int)expr
                    }
                }
                _lexer.ResetCurrent(bakPos, bakType, bakText);

                if (looksLikeLambda)
                    return ParseLambda();

                if (looksLikeCast)
                {
                    _lexer.NextToken(); //skip (
                    var vt = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                    _lexer.NextToken(); //skip type
                    vt = ReadMaybePointer(vt);
                    Consume(Ast.TokenType.ParenClose);
                    return new Ast.CastNode(vt, ParseUnary());
                }

                _lexer.NextToken(); //skip (
                var inner = ParseExpression();
                Consume(Ast.TokenType.ParenClose);
                return inner;
            }

            return ParsePrimary();
        }

        private Ast.AstNode ParsePrimary()
        {
            switch (_lexer.CurrentTokenType)
            {
                case Ast.TokenType.Keyword:
                    if (_lexer.CurrentTokenText == "new")
                    {
                        _lexer.NextToken(); //skip new

                        if (!IsTypeKeyword(_lexer.CurrentTokenText))
                            throw new ApplicationException("Expected type after 'new'");

                        var elemType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); // skip type
                        var sizes = new List<Ast.AstNode>();
                        int rank = 0;
                        while (true)
                        {
                            rank++;
                            Consume(Ast.TokenType.BracketsOpen);
                            if (_lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                                sizes.Add(ParseExpression());//can be empty
                            Consume(Ast.TokenType.BracketsClose);
                            if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen) continue;
                            break;
                        }

                        if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                        {
                            var lit = (Ast.ArrayLiteralNode)ParsePrimary();
                            lit.ElementType = elemType;
                            return lit;
                        }
                        return new Ast.NewArrayNode(rank > 1 ? Ast.ValueType.Array : elemType, sizes.ToArray());
                    }
                    break;
                case Ast.TokenType.Number:
                    string num = _lexer.CurrentTokenText; _lexer.NextToken();
                    if (num.Contains('.')) return new Ast.LiteralNode(double.Parse(num));
                    if (ulong.TryParse(num, out var ulongVal))
                    {
                        if (ulongVal <= int.MaxValue)
                            return new Ast.LiteralNode((int)ulongVal);
                        else if (ulongVal <= uint.MaxValue)
                            return new Ast.LiteralNode((uint)ulongVal);
                        else if (ulongVal <= long.MaxValue)
                            return new Ast.LiteralNode((long)ulongVal);
                        else
                            return new Ast.LiteralNode(ulongVal);
                    }
                    if (long.TryParse(num, out var longVal))
                    {
                        if (longVal >= int.MinValue)
                            return new Ast.LiteralNode((int)longVal);
                        else if (longVal >= long.MinValue)
                            return new Ast.LiteralNode(longVal);
                    }
                    return new Ast.LiteralNode(int.Parse(num));
                case Ast.TokenType.Char:
                    char ch = _lexer.CurrentTokenText[0];
                    _lexer.NextToken();
                    return new Ast.LiteralNode(ch);
                case Ast.TokenType.String:
                    var strValue = Unescape(_lexer.CurrentTokenText);
                    _lexer.NextToken();
                    return new Ast.LiteralNode(strValue);
                case Ast.TokenType.InterpolatedString:
                    var strValue2 = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    return ParseInterpolatedString(strValue2);
                case Ast.TokenType.Identifier:
                    var name = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    if (name == "true") return new Ast.LiteralNode(true);
                    if (name == "false") return new Ast.LiteralNode(false);
                    if (name == "null") return new Ast.LiteralNode(null);
                    Ast.AstNode? ln = null;
                    if (_constScopes.Any(s => s.TryGetValue(name, out ln)))
                        return ln;
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                    {
                        _lexer.NextToken(); //skip (
                        var args = new List<Ast.AstNode>();
                        if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                        {
                            do
                            {
                                args.Add(ParseExpression());
                                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                {
                                    _lexer.NextToken();
                                    continue;
                                }
                                break;
                            }
                            while (true);
                        }
                        Consume(Ast.TokenType.ParenClose);
                        return new Ast.CallNode(name, args.ToArray());
                    }
                    var node = new Ast.VariableReferenceNode(name);
                    if (_lexer.CurrentTokenType == Ast.TokenType.Dot)
                    {
                        _lexer.NextToken(); //skip .
                        string method = _lexer.CurrentTokenText;
                        Consume(Ast.TokenType.Identifier);

                        var args = new List<Ast.AstNode>();
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                        {
                            _lexer.NextToken(); //skip (
                            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                            {
                                do
                                {
                                    args.Add(ParseExpression());
                                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                    { _lexer.NextToken(); continue; }
                                    break;
                                }
                                while (true);
                            }
                            Consume(Ast.TokenType.ParenClose); //skip )
                        }
                        args.Insert(0, node);
                        return new Ast.CallNode(method, args.ToArray());
                    }
                    while (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                    {
                        _lexer.NextToken(); //skip [
                        var indexExpr = ParseExpression();
                        Consume(Ast.TokenType.BracketsClose); //skip ]
                        return new Ast.ArrayIndexNode(node, indexExpr);
                    }
                    return node;
                case Ast.TokenType.ParenOpen:
                    Console.WriteLine("parsing");
                    int savePos = _lexer.Position;
                    var saveType = _lexer.CurrentTokenType;
                    var saveText = _lexer.CurrentTokenText;

                    int depth = 0;
                    bool sawArrow = false;
                    do
                    {
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                            depth++;
                        else if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                            depth--;

                        if (depth == 0 &&
                            _lexer.CurrentTokenType == Ast.TokenType.Operator &&
                            _lexer.CurrentTokenText == "=>")
                        {
                            sawArrow = true;
                            break;
                        }
                        _lexer.NextToken();
                    }
                    while (_lexer.CurrentTokenType != Ast.TokenType.EndOfInput);

                    _lexer.ResetCurrent(savePos, saveType, saveText);

                    if (sawArrow)
                        return ParseLambda();

                    _lexer.NextToken();
                    var expr = ParseExpression();
                    Consume(Ast.TokenType.ParenClose);
                    return expr;
                case Ast.TokenType.BraceOpen:
                {
                        _lexer.NextToken();
                        var elems = new List<Ast.AstNode>();
                        if (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
                        {
                            do
                            {
                                elems.Add(ParseExpression());
                                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                { _lexer.NextToken(); continue; }
                                break;
                            } while (true);
                        }
                        Consume(Ast.TokenType.BraceClose);

                        return new Ast.ArrayLiteralNode(Ast.ValueType.Object, elems.ToArray());
                }

            }



            throw new ApplicationException($"Unexpected token in expression: {_lexer.CurrentTokenType}: {_lexer.CurrentTokenText}");
        }
        private Ast.AstNode ParseInterpolatedString(string content)
        {
            //cut string
            var parts = new List<Ast.AstNode>();
            var sb = new StringBuilder();
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{')//expression start
                {
                    if (sb.Length > 0)
                    {
                        parts.Add(new Ast.LiteralNode(Unescape(sb.ToString())));
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
            if (sb.Length > 0) parts.Add(new Ast.LiteralNode(Unescape(sb.ToString())));

            //glue together with +
            if (parts.Count == 0) return new Ast.LiteralNode(string.Empty);
            Ast.AstNode node = parts[0];
            for (int k = 1; k < parts.Count; k++)
                node = new Ast.BinOpNode(node, Ast.OperatorToken.Plus, parts[k]);

            return node;
        }
        #endregion
        #region Function parser
        private Ast.AstNode ParseFunctionDeclaration(bool isVoid = false, string? returnTypeText = null, string? name = null, IList<Ast.AttributeNode>? attrs = null)
        {
            Ast.ValueType? retType = isVoid ? null : Enum.Parse<Ast.ValueType>(returnTypeText!, ignoreCase: true);
            if (!isVoid && _lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
            {
                ReadArraySuffixes();
                retType = Ast.ValueType.Array;
            }
            if (name == null)
            {
                name = _lexer.CurrentTokenText;
                Consume(Ast.TokenType.Identifier);
            }

            Consume(Ast.TokenType.ParenOpen);
            var paramNames = new List<string>();
            var paramTypes = new List<Ast.ValueType>();
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                do
                {
                    var pType = Ast.ValueType.Object;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                    {
                        pType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); //skip time
                        pType = ReadMaybePointer(pType);//ptr*?
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray)
                            pType = Ast.ValueType.Array;

                    }

                    var pname = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    paramTypes.Add(pType);
                    paramNames.Add(pname);
                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                    }
                    else break;
                }
                while (true);
            }
            Consume(Ast.TokenType.ParenClose);

            Ast.AstNode body;
            if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>")
            {
                _lexer.NextToken(); // skip =>
                var expr = ParseExpression();
                Consume(Ast.TokenType.Semicolon);
                body = new Ast.ReturnNode(expr);
            }
            else
            {
                body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }
            return new Ast.FunctionDeclarationNode(retType!, name, paramNames.ToArray(), paramTypes.ToArray(), body, attrs);
        }
        private Ast.AstNode ParseReturn()
        {
            _lexer.NextToken(); //skip return
            Ast.AstNode? expr = null;
            if (_lexer.CurrentTokenType != Ast.TokenType.Semicolon)
                expr = ParseExpression();
            Consume(Ast.TokenType.Semicolon);
            return new Ast.ReturnNode(expr);
        }
        private List<Ast.AttributeNode> ParseAttributeList()
        {
            var list = new List<Ast.AttributeNode>();

            while (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
            {
                Consume(Ast.TokenType.BracketsOpen);

                do//inside []
                {
                    var name = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);//skip attribute name

                    var args = new List<string>();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                    {
                        Consume(Ast.TokenType.ParenOpen);
                        if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                        {
                            do
                            {
                                if (_lexer.CurrentTokenType != Ast.TokenType.String)
                                    throw new NotImplementedException("Attribute params should be strings");

                                args.Add(_lexer.CurrentTokenText);
                                _lexer.NextToken();//skip string

                                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                { _lexer.NextToken(); continue; }
                                break;
                            }
                            while (true);
                        }
                        Consume(Ast.TokenType.ParenClose);
                    }

                    list.Add(new Ast.AttributeNode(name, args.ToArray()));

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        continue;
                    }
                    break;
                }
                while (true);

                Consume(Ast.TokenType.BracketsClose);
                //repeat if more attributes
            }
            return list;
        }
        private Ast.AstNode ParseLambda()
        {
            Consume(Ast.TokenType.ParenOpen);

            var paramNames = new List<string>();
            var paramTypes = new List<Ast.ValueType>();

            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                while (true)
                {
                    Ast.ValueType pType = Ast.ValueType.Object;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                    {
                        pType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); //skip type

                        pType = ReadMaybePointer(pType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) pType = Ast.ValueType.Array;
                    }

                    string pname = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    paramNames.Add(pname);
                    paramTypes.Add(pType);

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); continue; }
                    break;
                }
                
            }
            Consume(Ast.TokenType.ParenClose);

            Consume(Ast.TokenType.Operator); //skip =>

            Ast.AstNode body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseExpression();

            string name = (++_anonCounter).ToString();
            Ast.ValueType? retType = null;
            if (body is not Ast.BlockNode)
                retType = Ast.ValueType.Object;

            var decl = new Ast.FunctionDeclarationNode(
                ret: retType,
                name: name,
                @params: paramNames.ToArray(),
                types: paramTypes.ToArray(),
                body: body);

            return new Ast.LambdaNode(decl);
        }

        #endregion
        #region Structure objects parser
        private Ast.AstNode ParseClassDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); // skip class
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            if (_lexer.CurrentTokenText == ":") //inheritance
            {
                _lexer.NextToken();
                while (_lexer.CurrentTokenType == Ast.TokenType.Identifier
                    || _lexer.CurrentTokenType == Ast.TokenType.Keyword)
                    _lexer.NextToken(); //skip names
            }

            Consume(Ast.TokenType.BraceOpen);
            var members = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
                members.Add(ParseStatement());
            Consume(Ast.TokenType.BraceClose);


            return new Ast.ClassDeclarationNode(name, mods ?? Array.Empty<string>(), members);
        }

        private Ast.AstNode ParseInterfaceDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); // skip interface
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            if (_lexer.CurrentTokenText == ":") //inheritance
            {
                _lexer.NextToken();
                while (_lexer.CurrentTokenType == Ast.TokenType.Identifier
                    || _lexer.CurrentTokenType == Ast.TokenType.Keyword)
                    _lexer.NextToken();
            }

            Consume(Ast.TokenType.BraceOpen);
            var members = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
            {
                members.Add(ParseStatement());
            }
            Consume(Ast.TokenType.BraceClose);

            return new Ast.InterfaceDeclarationNode(name, mods ?? Array.Empty<string>(), members);
        }

        private Ast.AstNode ParseStructDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); //skip struct
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            if (_lexer.CurrentTokenText == ":")
            {
                _lexer.NextToken(); //skip :
                while (_lexer.CurrentTokenType == Ast.TokenType.Identifier || _lexer.CurrentTokenType == Ast.TokenType.Keyword)
                    _lexer.NextToken(); //till {
            }
            Consume(Ast.TokenType.BraceOpen);
            var members = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
            {
                members.Add(ParseStatement());
            }
            Consume(Ast.TokenType.BraceClose);
            return new Ast.StructDeclarationNode(name, mods ?? Array.Empty<string>(), members);
        }
        private Ast.AstNode ParseEnumDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); //skip enum
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            Consume(Ast.TokenType.BraceOpen);
            var members = new List<Ast.EnumDeclarationNode.Member>();

            int? lastSemicolonValue = null;
            
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
            {
                string memName = _lexer.CurrentTokenText;
                Consume(Ast.TokenType.Identifier);
                Ast.AstNode? explicitExpr = null;
                if (_lexer.CurrentTokenText == "=")
                {
                    _lexer.NextToken();//skip =
                    explicitExpr = ParseExpression();
                }
                members.Add(new Ast.EnumDeclarationNode.Member(memName, explicitExpr));
                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                { _lexer.NextToken(); continue; }

                break;
            }
            Consume(Ast.TokenType.BraceClose);

            return new Ast.EnumDeclarationNode(name, members.ToArray(), mods?.ToArray());

        }
        #endregion
        #region Flow Control parser
        #region Loop parser
        private Ast.AstNode ParseDoWhile()
        {
            _lexer.NextToken(); //skip do
            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            Consume(Ast.TokenType.Keyword); //expect while
            Consume(Ast.TokenType.ParenOpen);
            var condition = ParseExpression();
            Consume(Ast.TokenType.ParenClose);
            Consume(Ast.TokenType.Semicolon);
            return new Ast.DoWhileNode(body, condition);
        }
        private Ast.AstNode ParseWhile()
        {
            _lexer.NextToken(); //skip while
            Consume(Ast.TokenType.ParenOpen);
            var condition = ParseExpression();
            Consume(Ast.TokenType.ParenClose);
            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            return new Ast.WhileNode(condition, body);
        }
        private Ast.AstNode ParseFor()
        {
            _lexer.NextToken(); //skip for
            Consume(Ast.TokenType.ParenOpen);

            Ast.AstNode init = null;

            if (IsTypeKeyword(_lexer.CurrentTokenText) && !int.TryParse(_lexer.CurrentTokenText, out _))
            {
                init = ParseDeclaration();
            }
            else if (_lexer.CurrentTokenType != Ast.TokenType.Semicolon)
            {
                init = ParseExpression();
                Consume(Ast.TokenType.Semicolon);
            }
            else
            {
                Consume(Ast.TokenType.Semicolon);
            }

            Ast.AstNode condition = null;
            if (_lexer.CurrentTokenType != Ast.TokenType.Semicolon)
            {
                condition = ParseExpression();
            }
            Consume(Ast.TokenType.Semicolon);

            Ast.AstNode step = null;
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                step = ParseExpression();
            }
            Consume(Ast.TokenType.ParenClose);

            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            return new Ast.ForNode(init, condition, step, body);
        }
        private Ast.AstNode ParseForeach()
        {
            _lexer.NextToken(); //skip foreach
            Consume(Ast.TokenType.ParenOpen);
            bool isVar = _lexer.CurrentTokenText == "var";
            if (!IsTypeKeyword(_lexer.CurrentTokenText))
                throw new ApplicationException("Type expected in foreach");
            string typeText = _lexer.CurrentTokenText;
            _lexer.NextToken(); //skip type
            var varType = isVar ? Ast.ValueType.Object : Enum.Parse<Ast.ValueType>(typeText, true);

            string varName = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            if (_lexer.CurrentTokenText != "in")
                throw new ApplicationException($"Expected 'in', but got {_lexer.CurrentTokenText}");
            _lexer.NextToken(); //skip in

            var collectionExpr = ParseExpression();
            Consume(Ast.TokenType.ParenClose);

            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();

            return new Ast.ForeachNode(varType, varName, collectionExpr, body);

        }

        #endregion
        private Ast.AstNode ParseGoto()
        {
            _lexer.NextToken();
            string label = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            Consume(Ast.TokenType.Semicolon);
            return new Ast.GotoNode(label);
        }
        private Ast.AstNode ParseIf()
        {
            _lexer.NextToken(); //skip if
            Consume(Ast.TokenType.ParenOpen);
            var cond = ParseExpression();
            Consume(Ast.TokenType.ParenClose);

            Ast.AstNode body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            Ast.AstNode? elseBody = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "else")
            {
                _lexer.NextToken(); //skip else
                elseBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }

            return new Ast.IfNode(cond, body, elseBody);
        }
        private Ast.AstNode ParseSwitch()
        {
            _lexer.NextToken(); //skip switch
            Consume(Ast.TokenType.ParenOpen);
            var expr = ParseExpression();
            Consume(Ast.TokenType.ParenClose);

            Consume(Ast.TokenType.BraceOpen);

            var cases = new List<(Ast.AstNode? value, List<Ast.AstNode> body)>();
            List<Ast.AstNode>? currentBody = null;
            Ast.AstNode? currentValue = null; //if null default

            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose && _lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && (_lexer.CurrentTokenText == "case" || _lexer.CurrentTokenText == "default"))
                {
                    if (currentBody != null)
                        cases.Add((currentValue, currentBody));

                    currentBody = new List<Ast.AstNode>();
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
                    Consume(Ast.TokenType.Colon);
                    continue;
                }

                currentBody ??= new List<Ast.AstNode>();//if no case
                currentBody.Add(ParseStatement());
            }

            if (currentBody != null)
                cases.Add((currentValue, currentBody));

            Consume(Ast.TokenType.BraceClose);

            return new Ast.SwitchNode(expr, cases);
        }
        private Ast.AstNode ParseTryCatch()
        {
            _lexer.NextToken();
            var tryBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();

            if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "catch"))
                throw new Exception("expected 'catch' after 'try' block");
            _lexer.NextToken(); //skip catch

            string? exVar = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
            {
                _lexer.NextToken(); //(
                if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                {
                    string first = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);

                    if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                    {
                        exVar = _lexer.CurrentTokenText;
                        Consume(Ast.TokenType.Identifier);
                    }
                    else
                    {
                        exVar = first;
                    }
                }

                Consume(Ast.TokenType.ParenClose); //)
            }

            var catchBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            Ast.AstNode? finallyBody = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "finally")
            {
                _lexer.NextToken();
                finallyBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }

            return new Ast.TryCatchNode(tryBody, catchBody, finallyBody, exVar);
        }
        private Ast.AstNode ParseThrow()
        {
            _lexer.NextToken(); //skip throw

            //skip new if needed
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword &&
                _lexer.CurrentTokenText == "new")
                _lexer.NextToken();

            if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
            {
                int savePos = _lexer.Position;
                var saveType = _lexer.CurrentTokenType;
                var saveText = _lexer.CurrentTokenText;

                _lexer.NextToken();
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                {
                    _lexer.NextToken(); //skip (
                    var msgExpr = ParseExpression();
                    Consume(Ast.TokenType.ParenClose);

                    Consume(Ast.TokenType.Semicolon);
                    return new Ast.ThrowNode(msgExpr);
                }
                _lexer.ResetCurrent(savePos, saveType, saveText);
            }

            var expr = ParseExpression();
            Consume(Ast.TokenType.Semicolon);
            return new Ast.ThrowNode(expr);
        }


        #endregion


    }
    #endregion
    public class Ast //ToDo reference type arrays?
    {
        #region Context
        public class ExecutionContext : IDisposable
        {
            private const int HeaderSize = 6;
            public const int MaxCallDepth = 512;
            private const int ObjectTableCapacity = 64;
            private readonly Stack<Dictionary<string, Variable>> _scopes = new();
            private readonly Stack<int> _memoryOffsets = new();
            private readonly ObjectTable _handles = new(capacity: ObjectTableCapacity);
            private readonly byte[] _memory;
            private readonly HashSet<int> _pinned = new();
            private int _heapend = 0;
            private int _allocPointer = 0;
            private int _callDepth = 0;
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
                public AttributeNode[] Attributes;
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
            public void Check()
            {
                if (CancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Execution was cancelled");
                if (_scopes.Count > 1024) throw new OutOfMemoryException($"Too many scopes alive, danger of stack overflow");
                int vars = 0;
                foreach (var scope in _scopes) vars += scope.Count;
                if (vars > 2048) throw new OutOfMemoryException($"Too many declarations, danger of host memory ddos");
            }
            #region Memory manager
            public string PrintMemory(int bytesPerRow = 16, int dataPreview = 64, bool printStack = false)
            {
                var sb = new System.Text.StringBuilder();

                // ----- Stack -----
                sb.AppendLine("=== STACK ===");
                if (printStack)
                {
                    for (int i = 0; i < StackSize; i++)
                    {
                        if (i % bytesPerRow == 0)
                            sb.Append($"{i:X4}: ");

                        sb.Append($"{_memory[i]:X2} ");

                        if (i % bytesPerRow == bytesPerRow - 1)
                            sb.AppendLine();
                    }
                    if (StackSize % bytesPerRow != 0)
                        sb.AppendLine();
                } else sb.AppendLine("...emmited...");
                // ----- Heap -----
                sb.AppendLine("=== HEAP ===");
                int pos = 0;
                try
                {
                    while (pos < _heapend)
                    {
                        int headerPos = pos + StackSize;
                        int len = System.BitConverter.ToInt32(_memory, headerPos);
                        bool used = _memory[headerPos + 4] != 0;
                        ValueType vt = (ValueType)_memory[headerPos + 5];
                        int payload = len - HeaderSize; // HeaderSize существует в ExecutionContext

                        sb.Append('[')
                          .Append($"@{headerPos} len={payload}({payload + HeaderSize}) used={(used)} type={vt} data=");

                        int preview = System.Math.Min(payload, dataPreview);
                        for (int i = 0; i < preview; i++)
                            sb.Append($"{_memory[headerPos + HeaderSize + i]:X2} ");
                        if (payload > preview) sb.Append("...");
                        sb.Append(']')
                          .AppendLine();

                        pos += len; // следующий блок
                    }
                }catch(Exception e) { Console.WriteLine(e); }
                return sb.ToString();
            }
            public void Dispose()
            {
                _handles.Dispose();
                _scopes.Clear();
            }
            #region Stack manager
            private static readonly Dictionary<Ast.ValueType, Func<byte[], int, object>> typeReaders = new()
            {
                { Ast.ValueType.Int, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.IntPtr, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Array, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.String, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Object, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Enum, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Bool, (heap, offset) => heap[offset] != 0 },
                { Ast.ValueType.Float, (heap, offset) => BitConverter.ToSingle(heap, offset) },
                { Ast.ValueType.Double, (heap, offset) => BitConverter.ToDouble(heap, offset) },
                { Ast.ValueType.Char, (heap, offset) => BitConverter.ToChar(heap, offset) },
                { Ast.ValueType.Long, (heap, offset) => BitConverter.ToInt64(heap, offset) },
                { Ast.ValueType.Ulong, (heap, offset) => BitConverter.ToUInt64(heap, offset) },
                { Ast.ValueType.Uint, (heap, offset) => BitConverter.ToUInt32(heap, offset) },
                { ValueType.Short,  (m,o)=>BitConverter.ToInt16 (m,o) },
                { ValueType.UShort, (m,o)=>BitConverter.ToUInt16(m,o) },
                { ValueType.Byte,   (m,o)=>m[o] },
                { ValueType.Sbyte,  (m,o)=>(sbyte)m[o] },
                { ValueType.Decimal,(m,o)=>BitConverter.ToUInt32(m,o)},
            };

            private static readonly Dictionary<Ast.ValueType, Action<byte[], int, object>> typeWriters = new()
            {
                { Ast.ValueType.Int, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.IntPtr, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.String, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Array, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Object, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Enum, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Bool, (heap, offset, value) => heap[offset] = (bool)value ? (byte)1 : (byte)0 },
                { Ast.ValueType.Float, (heap, offset, value) => BitConverter.GetBytes((float)value).CopyTo(heap, offset) },
                { Ast.ValueType.Double, (heap, offset, value) => BitConverter.GetBytes((double)value).CopyTo(heap, offset) },
                { Ast.ValueType.Char, (heap, offset, value) => BitConverter.GetBytes((char)value).CopyTo(heap, offset) },
                { Ast.ValueType.Long, (heap, offset, value) => BitConverter.GetBytes((long)value).CopyTo(heap, offset) },
                { Ast.ValueType.Ulong, (heap, offset, value) => BitConverter.GetBytes((ulong)value).CopyTo(heap, offset) },
                { Ast.ValueType.Uint, (heap, offset, value) => BitConverter.GetBytes((uint)value).CopyTo(heap, offset) },
                { Ast.ValueType.Short, (heap, offset, value) => BitConverter.GetBytes((short)value).CopyTo(heap, offset) },
                { Ast.ValueType.UShort, (heap, offset, value) => BitConverter.GetBytes((ushort)value).CopyTo(heap, offset) },
            };
            public object ReadFromStack(int addr, ValueType vt)
            {
                ValidateAddress(addr, GetTypeSize(vt));
                return typeReaders[vt](_memory, addr);
            }
            public object ReadFromMemorySlice(byte[] memory, ValueType vt) => typeReaders[vt](memory, 0);
            public void EnterScope()
            {
                _memoryOffsets.Push(_allocPointer);
                _scopes.Push(new Dictionary<string, Variable>());
            }

            public void ExitScope()
            {
                foreach (var kvp in _scopes.Peek())
                {
                    if (kvp.Value.Type == ValueType.Array)
                    {
                        int basePtr = BitConverter.ToInt32(_memory, kvp.Value.Address);
                        if (basePtr >= StackSize)
                        {
                            var elemType = GetHeapObjectType(basePtr);
                            if (IsReferenceType(elemType))
                            {
                                int len = GetArrayLength(basePtr);
                                for (int i = 0; i < len; i++)
                                {
                                    int p = BitConverter.ToInt32(_memory, basePtr + i * sizeof(int));
                                    if (p <= 0) continue;

                                    if (elemType == ValueType.Object) ReleaseObject(p);
                                    //else if (elemType == ValueType.String) Free(p);
                                    //else if (elemType == ValueType.Array)  Free(p);
                                }
                            }
                            //Free(basePtr);
                        }
                    }
                    
                    if (kvp.Value.Type != ValueType.Object) continue;
                    int ptr = BitConverter.ToInt32(_memory, kvp.Value.Address);
                    if (ptr >= StackSize)
                    {
                        int id = BitConverter.ToInt32(_memory, ptr);
                        ReleaseObject(id);
                    }
                }

                _scopes.Pop();
                _allocPointer = _memoryOffsets.Pop();
                CollectGarbage();
            }

            

            public void EnterFunction()
            {
                if (++_callDepth > MaxCallDepth)
                    throw new StackOverflowException($"Recursion limit {MaxCallDepth} reached");
            }
            public void ExitFunction() => _callDepth--;

            public bool HasVariable(string name) => _scopes.Any(scope => scope.ContainsKey(name));
            public Variable Stackalloc(ValueType type)
            {
                int size = GetTypeSize(type);
                if (_allocPointer + size > StackSize) throw new StackOverflowException();
                int address = _allocPointer;
                _allocPointer += size;

                return new Variable(type, address, size);
            }
            public int GetTypeSize(Ast.ValueType type)
            {
                switch (type)
                {
                    case ValueType.Int: return sizeof(int);
                    case ValueType.Bool: return sizeof(int);
                    case ValueType.String: return sizeof(int);
                    case ValueType.Array: return sizeof(int);
                    case ValueType.Object: return sizeof(int);
                    case ValueType.IntPtr: return sizeof(int);
                    case ValueType.Enum: return sizeof(int);
                    case ValueType.Uint: return sizeof(uint);
                    case ValueType.Long: return sizeof(long);
                    case ValueType.Ulong: return sizeof(ulong);
                    case ValueType.Double: return sizeof(double);
                    case ValueType.Float: return sizeof(float);
                    case ValueType.Decimal: return sizeof(decimal);
                    case ValueType.Char: return sizeof(decimal);
                    case ValueType.Short: return sizeof(short);
                    case ValueType.UShort: return sizeof(ushort);
                    case ValueType.Byte: return sizeof(byte);
                    case ValueType.Sbyte: return sizeof(sbyte);
                    default: throw new ApplicationException($"Unsupported type {type}");
                }
            }
            public object ReadVariable(string name)
            {
                var variable = Get(name);
                if (variable.Address < 0) return null;
                return typeReaders[variable.Type](_memory, variable.Address);
            }
            public void WriteVariable(string name, object value)
            {
                var variable = Get(name);
                ValidateAddress(variable.Address, GetTypeSize(variable.Type));
                typeWriters[variable.Type](_memory, variable.Address, value);

            }
            internal void ValidateAddress(int addr, int size = 1)
            {
                if (addr < StackSize)
                {
                    if (addr < 0 || addr + size > StackSize)
                        throw new ArgumentOutOfRangeException(nameof(addr), $"Stack address {addr} (+{size}) out of range 0..{StackSize}");
                }
                else
                {
                    if (addr < StackSize || addr + size > StackSize + _heapend)
                        throw new ArgumentOutOfRangeException(nameof(addr), $"Heap address {addr} (+{size}) out of range {_memory.Length}");
                }
            }

            #endregion
            #region Heap manager
            public int Pin(object? val)
            {
                if (val is int addr && addr >= StackSize)
                    return AddPin(addr);

                if (val is ValueTuple<int, bool> obj)
                    return AddPin(obj.Item1);

                if (val is ValueTuple<int, ValueType> arr)
                    return AddPin(arr.Item1);

                return -1; 
            }

            private int AddPin(int addr)
            {
                _pinned.Add(addr);

                var elemType = GetHeapObjectType(addr);
                if (IsReferenceType(elemType))
                {
                    int len = GetArrayLength(addr);
                    for (int i = 0; i < len; i++)
                    {
                        int child = BitConverter.ToInt32(_memory, addr + i * sizeof(int));
                        if (child >= StackSize) _pinned.Add(child);
                    }
                }
                return addr;
            }

            public void Unpin(int key) => _pinned.Remove(key);
            private void CollectGarbage()
            {
                //Mark
                HashSet<int> reachable = new(_pinned);
                foreach (var scope in _scopes)
                    foreach (var kv in scope)
                    {
                        Variable v = kv.Value;
                        if (v.Type == ValueType.Array)
                        {
                            int bp = BitConverter.ToInt32(_memory, v.Address);
                            var et = GetHeapObjectType(bp);
                            if (IsReferenceType(et))
                            {
                                int len = GetArrayLength(bp);
                                for (int i = 0; i < len; i++)
                                {
                                    int p = BitConverter.ToInt32(_memory, bp + i * sizeof(int));
                                    if (p >= StackSize) reachable.Add(p);
                                }
                            }
                            if (bp >= StackSize && bp < StackSize + _heapend)
                                reachable.Add(bp);
                        }
                        //Reference exist
                        else if (Ast.IsReferenceType(v.Type) || v.Type == Ast.ValueType.IntPtr)
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
            public int GetArrayLength(int dataPtr)
            {
                int bytes = GetHeapObjectLength(dataPtr);
                var vt = GetHeapObjectType(dataPtr);
                return bytes / GetTypeSize(vt);
            }
            private void WriteHeader(int pos, int len, ValueType vt, bool used)
            {
                checked
                {
                    BitConverter.GetBytes(len).CopyTo(_memory, pos);
                    _memory[pos + 4] = used ? (byte)1 : (byte)0;
                    _memory[pos + 5] = (byte)vt;
                }
            }
            public ValueType GetHeapObjectType(int addr) => (ValueType)_memory[addr - HeaderSize + 5];
            public int GetHeapObjectLength(int addr) => (BitConverter.ToInt32(_memory, addr - HeaderSize)) - HeaderSize;
            private int GetBlockPayloadSize(int addr)
            {
                int len = BitConverter.ToInt32(_memory, addr - HeaderSize);
                return len - HeaderSize;
            }
            public Span<byte> GetSpan(int addr)
            {
                if (addr < StackSize) throw new ArgumentOutOfRangeException($"Pointer to Stack in GetSpan");
                int len = GetBlockPayloadSize(addr);
                ValidateAddress(addr, len);
                return _memory.AsSpan(addr, len);
            }
            public void Free(int addr)
            {

                int blockStart = addr - HeaderSize;
                if ((blockStart + HeaderSize) != addr)
                    throw new ArgumentException("Pointer must reference the start of an allocated block during free");
                if (blockStart < StackSize || blockStart >= _heapend + StackSize)
                    throw new ArgumentOutOfRangeException(nameof(addr));
                if (_memory[blockStart + 4] == 0)
                    throw new InvalidOperationException($"double-free or invalid pointer {addr}");
                _memory[blockStart + 4] = 0;//free flag
            }
            public void WriteBytes(int addr, ReadOnlySpan<byte> src)
            {
                ValidateAddress(addr, src.Length);
                if (src.Length > GetBlockPayloadSize(addr)) throw new ArgumentException("WriteBytes: too big for declared header");

                src.CopyTo(_memory.AsSpan(addr, src.Length));
            }
            public int Malloc(int size, ValueType valueType)
            {
                int need = size + HeaderSize;
                int addr = FindFreeBlock(need, valueType);
                if (addr >= 0) return addr;
                //CollectGarbage();
                DefragmentFree();
                addr = FindFreeBlock(need, valueType);
                if (addr >= 0) return addr;
                checked
                {
                    if (_heapend + need > _memory.Length - StackSize)
                        throw new OutOfMemoryException();
                    WriteHeader(_heapend + StackSize, need, valueType, used: true);
                    addr = _heapend + HeaderSize;
                    _heapend += need;
                    return addr + StackSize;
                }

            }

            private void DefragmentFree()
            {
                int pos = 0;
                int lastUsedEnd = 0;

                while (pos < _heapend)
                {
                    int len = BitConverter.ToInt32(_memory, pos + StackSize);
                    bool used = _memory[pos + StackSize + 4] != 0;

                    if (!used)
                    {
                        int runStart = pos;
                        int runLen = len;

                        int next = pos + len;
                        while (next < _heapend)
                        {
                            int nHdrIdx = next + StackSize;
                            int nLen = BitConverter.ToInt32(_memory, nHdrIdx);
                            bool nUsed = _memory[nHdrIdx + 4] != 0;
                            if (nUsed) break;

                            runLen += nLen;
                            next += nLen;
                        }

                        WriteHeader(runStart + StackSize, runLen, ValueType.IntPtr, used: false);

                        pos = next;
                    }
                    else
                    {
                        lastUsedEnd = pos + len;
                        pos += len;
                    }
                }

                if (pos == _heapend)
                    return;

                _heapend = lastUsedEnd;
            }
            private int FindFreeBlock(int need, ValueType? requested = null)
            {
                int pos = 0;
                while (pos < _heapend)
                {
                    int len = BitConverter.ToInt32(_memory, pos + StackSize);

                    if (_memory[pos + 4 + StackSize] == 0 && len >= need)
                    {
                        ///_memory[pos + 4 + StackSize] = 1;
                        ValueType vt = requested is null ? GetHeapObjectType(pos + HeaderSize + StackSize) : (ValueType)requested;
                        if (len - need >= HeaderSize+1)
                        {
                            WriteHeader(pos + StackSize, need, vt, true);
                            WriteHeader(pos + need + StackSize, len - need, ValueType.IntPtr, false);
                        }
                        else
                        {
                            WriteHeader(pos + StackSize, len, vt, true);
                            int tail = len - need;
                            if (tail > 0)
                            {
                                int tailStart = pos + StackSize + need;
                                _memory.AsSpan(tailStart, tail).Fill(Ast.IsReferenceType(vt) ? (byte)0xFF : (byte)0x00);
                            }

                        }


                        return pos + HeaderSize + StackSize; 
                    }
                    pos += len;
                }
                return -1;
            }
            public void StoreStringVariable(string name, string value)
            {
                var varInfo = Get(name);
                if (varInfo.Type != ValueType.String)
                    throw new ApplicationException($"{name} is not a string");

                int oldPtr = (int)ReadVariable(name);
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                if (oldPtr >= 0 && oldPtr >= StackSize)
                {
                    int capacity = GetHeapObjectLength(oldPtr);
                    if (bytes.Length <= capacity)
                    {
                        WriteBytes(oldPtr, bytes);
                        //BitConverter.GetBytes(bytes.Length + HeaderSize).CopyTo(_memory, oldPtr - HeaderSize);
                        if (bytes.Length < capacity)
                            _memory.AsSpan(oldPtr + bytes.Length, capacity - bytes.Length).Clear();
                        return;
                    }
                    //not enough space, reallocate
                    Free(oldPtr);
                }
                else throw new ArgumentOutOfRangeException($"Reallocating string with pointer in Stack {oldPtr}");
                int newPtr = Malloc(bytes.Length, ValueType.String);
                WriteBytes(newPtr, bytes);
                WriteVariable(name, newPtr);
            }
            public int PackReference(object? value, ValueType et)
            {
                if (et == ValueType.String && value is string s)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(s);
                    int ptr = Malloc(bytes.Length, ValueType.String);
                    WriteBytes(ptr, bytes);
                    return ptr;
                }
                if (et == ValueType.Array && value is int p) return p;
                if (et == ValueType.Object) return AddObject(value!);
                if (et == ValueType.IntPtr && value is IntPtr ip) return ip.ToInt32();
                if (et == ValueType.Array && value is ValueTuple<int, ValueType> info)
                {
                    var (len, elemType) = info;
                    int bytes = IsReferenceType(elemType) ? sizeof(int) * len : GetTypeSize(elemType) * len;
                    int basePtr = Malloc(bytes, elemType);
                    int payload = GetHeapObjectLength(basePtr);
                    RawMemory.AsSpan(basePtr, payload).Fill(IsReferenceType(elemType) ? (byte)0xFF : (byte)0x00);
                    return basePtr;
                }
                throw new ApplicationException("Unsupported element type while packing reference");
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public string ReadHeapString(int ptr) => ReadHeapString(GetSpan(ptr));
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public string ReadHeapString(ReadOnlySpan<byte> raw)
            {
                if (raw.Length == 0) return string.Empty;
                int len = raw.Length;
                int limit = len - HeaderSize - 1;
                while (len > limit)
                {
                    byte b = raw[len - 1];
                    if (b != 0x00 && b != 0xFF) break;
                    len--;
                }
                return Encoding.UTF8.GetString(raw.Slice(0, len));
            }
            #region Array helpers
            public void ArrayResize(string name, int newLength)
            {
                if (Get(name).Type != ValueType.Array)
                    throw new ApplicationException($"{name} is not an array");
                int ptr = (int)ReadVariable(name);
                int newPtr = ArrayResize(ptr, newLength);
                WriteVariable(name, newPtr);
            }
            public int ArrayResize(int oldPtr, int newLength)
            {
                
                if (oldPtr < StackSize || oldPtr >= _memory.Length - sizeof(int))
                    throw new ArgumentOutOfRangeException(nameof(oldPtr), $"nullptr");
                var elemType = GetHeapObjectType(oldPtr);
                int elemSize = GetTypeSize(elemType);
                int oldBytes = GetHeapObjectLength(oldPtr);
                int oldLength = oldBytes / elemSize;
                int requiredBytes = checked(newLength * elemSize);

                if (newLength == oldLength)
                    return oldPtr;

                int newPtr = Malloc(requiredBytes, elemType);
                _memory.AsSpan(oldPtr, oldBytes).CopyTo(_memory.AsSpan(newPtr, oldBytes));
                _memory.AsSpan(newPtr + oldBytes, requiredBytes - oldBytes).Clear();
                Free(oldPtr);
                return newPtr;
            }
            public void ArrayAdd(string name, object element)
            {
                if (Get(name).Type != ValueType.Array)
                    throw new ApplicationException($"{name} is not an array");
                int ptr = (int)ReadVariable(name);
                int newPtr = ArrayAdd(ptr, element);
                WriteVariable(name, newPtr);
            }

            public int ArrayAdd(int ptr, object element)
            {
                var type = InferType(element);
                if (ptr < StackSize || ptr >= _memory.Length - sizeof(int))
                    throw new ArgumentOutOfRangeException("nullptr");

                var elemType = GetHeapObjectType(ptr);
                if (elemType != type)
                    throw new ArgumentException("Type missmatch while adding to array");

                int elemSize = GetTypeSize(elemType);
                int oldBytes = GetHeapObjectLength(ptr);
                int oldLength = oldBytes / elemSize;
                int needBytes = checked((oldLength + 1) * elemSize);

                int newPtr = Malloc(needBytes, elemType);
                _memory.AsSpan(ptr, oldBytes)
                       .CopyTo(_memory.AsSpan(newPtr, oldBytes));
                _memory.AsSpan(newPtr + oldBytes, needBytes - oldBytes).Clear();



                ReadOnlySpan<byte> src = GetSourceBytes(element);
                WriteBytes(newPtr + oldBytes, src);
                Free(ptr);
                return newPtr;
            }
            public void ArrayAddAt(string varName, int index, object element)
            {
                if (Get(varName).Type != ValueType.Array)
                    throw new ApplicationException($"{varName} is not an array");

                int ptr = (int)ReadVariable(varName);
                int newPtr = ArrayAddAt(ptr, index, element);
                WriteVariable(varName, newPtr);
            }
            public int ArrayAddAt(int ptr, int index, object element)
            {
                var elemType = GetHeapObjectType(ptr);
                int elemSize = GetTypeSize(elemType);
                int bytes = GetHeapObjectLength(ptr);
                int length = bytes / elemSize;

                if (index < 0 || index > length)
                    throw new ArgumentOutOfRangeException(nameof(index));
                var incomingType = InferType(element);
                if (incomingType != elemType)
                    throw new ArgumentException($"Type mismatch while inserting into array, adding {incomingType} to {elemType}");

                int needBytes = checked((length + 1) * elemSize);
                int newPtr = Malloc(needBytes, elemType);

                _memory.AsSpan(ptr, index * elemSize).CopyTo(_memory.AsSpan(newPtr, index * elemSize));
                ReadOnlySpan<byte> src = GetSourceBytes(element);
                WriteBytes(newPtr + index * elemSize, src);
                int tailBytes = (length - index) * elemSize;
                _memory.AsSpan(ptr + index * elemSize, tailBytes)
                       .CopyTo(_memory.AsSpan(newPtr + (index + 1) * elemSize, tailBytes));

                Free(ptr);
                return newPtr;

            }

            public int ArrayIndexOf(string name, object element)
            {
                if (Get(name).Type != ValueType.Array)
                    throw new ApplicationException($"{name} is not an array");

                int ptr = (int)ReadVariable(name);
                return ArrayIndexOf(ptr, element);
            }

            public int ArrayIndexOf(int ptr, object element)
            {
                var elemType = GetHeapObjectType(ptr);
                int elemSize = GetTypeSize(elemType);
                int bytes = GetHeapObjectLength(ptr);
                int length = bytes / elemSize;

                if (!MatchVariableType(element, elemType) && elemType != ValueType.Object)
                    element = Cast(element, elemType);

                for (int i = 0; i < length; i++)
                {
                    int addr = ptr + i * elemSize;
                    object current = IsReferenceType(elemType) ? DerefReference(addr, elemType) : ReadFromStack(addr, elemType);

                    if ((current == null && element == null) || current?.Equals(element) == true)
                        return i;
                }
                return -1;
            }
            public void ArrayRemoveAt(string name, int index)
            {
                if (Get(name).Type != ValueType.Array)
                    throw new ApplicationException($"{name} is not an array");

                int ptr = (int)ReadVariable(name);
                int newPtr = ArrayRemoveAt(ptr, index);
                WriteVariable(name, newPtr);
            }

            public int ArrayRemoveAt(int ptr, int index)
            {
                var elemType = GetHeapObjectType(ptr);
                int elemSize = GetTypeSize(elemType);
                int bytes = GetHeapObjectLength(ptr);
                int length = bytes / elemSize;

                if (index < 0 || index >= length)
                    throw new ArgumentOutOfRangeException(nameof(index));

                int newBytes = (length - 1) * elemSize;
                int newPtr = Malloc(newBytes, elemType);

                _memory.AsSpan(ptr, index * elemSize)
                       .CopyTo(_memory.AsSpan(newPtr, index * elemSize));

                int tail = (length - index - 1) * elemSize;
                _memory.AsSpan(ptr + (index + 1) * elemSize, tail)
                       .CopyTo(_memory.AsSpan(newPtr + index * elemSize, tail));

                Free(ptr);
                return newPtr;
            }
            public int ArrayConcat(int firstPtr, int secondPtr)
            {
                if (firstPtr < StackSize || secondPtr < StackSize)
                    throw new ArgumentOutOfRangeException("nullptr");

                var elemType1 = GetHeapObjectType(firstPtr);
                var elemType2 = GetHeapObjectType(secondPtr);
                if (elemType1 != elemType2)
                    throw new ArgumentException("Arrays have different element types");

                int elemSize = GetTypeSize(elemType1);

                int bytes1 = GetHeapObjectLength(firstPtr);
                int bytes2 = GetHeapObjectLength(secondPtr);

                int newBytes = checked(bytes1 + bytes2);
                int newPtr = Malloc(newBytes, elemType1);

                _memory.AsSpan(firstPtr, bytes1).CopyTo(_memory.AsSpan(newPtr, bytes1));
                _memory.AsSpan(secondPtr, bytes2).CopyTo(_memory.AsSpan(newPtr + bytes1, bytes2));

                return newPtr;

            }
            public int ArraySort(int ptr, bool asc = true)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                object?[] buf = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;

                    buf[i] = IsReferenceType(elemType)
                        ? DerefReference(addr, elemType)
                        : ReadFromStack(addr, elemType);
                }

                Array.Sort(buf, (a, b) =>
                {
                    int cmp = Comparer<object>.Default.Compare(a, b);
                    return asc ? cmp : -cmp;
                });
                int newPtr;
                if (IsReferenceType(elemType))
                {
                    newPtr = Malloc(sizeof(int) * len, elemType);
                    for (int i = 0; i < len; i++)
                    {
                        int packed = buf[i] switch
                        {
                            null => -1,
                            string s => PackReference(s, ValueType.String),
                            int p => p,
                            _ => PackReference(buf[i]!, elemType)
                        };
                        BitConverter.GetBytes(packed)
                                    .CopyTo(RawMemory, newPtr + i * sizeof(int));
                    }
                }
                else
                {
                    newPtr = Malloc(eSize * len, elemType);
                    for (int i = 0; i < len; i++)
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(elemType, buf[i]);
                        src.CopyTo(RawMemory.AsSpan(newPtr + i * eSize, eSize));
                    }
                }

                Free(ptr);

                return newPtr;

            }
            public int ArraySortBy(int ptr, object keySelector, bool asc = true)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);

                object?[] data = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;
                    data[i] = IsReferenceType(elemType)
                                ? DerefReference(addr, elemType)
                                : ReadFromStack(addr, elemType);
                }

                int lambdaId = Convert.ToInt32(keySelector);

                Array.Sort(data, (a, b) =>
                {
                    var ka = InvokeById(lambdaId, a);
                    var kb = InvokeById(lambdaId, b);

                    int cmp = Comparer<object>.Default.Compare(ka ?? 0, kb ?? 0);
                    return asc ? cmp : -cmp;
                });

                int newPtr;
                if (IsReferenceType(elemType))
                {
                    newPtr = Malloc(sizeof(int) * len, elemType);
                    for (int i = 0; i < len; i++)
                    {
                        int packed = data[i] switch
                        {
                            null => -1,
                            string s => PackReference(s, ValueType.String),
                            int p => p,
                            _ => PackReference(data[i]!, elemType)
                        };
                        BitConverter.GetBytes(packed)
                                    .CopyTo(RawMemory, newPtr + i * sizeof(int));
                    }
                }
                else
                {
                    newPtr = Malloc(eSize * len, elemType);
                    for (int i = 0; i < len; i++)
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(elemType, data[i]);
                        src.CopyTo(RawMemory.AsSpan(newPtr + i * eSize, eSize));
                    }
                }

                Free(ptr);
                return newPtr;
            }
            public int ArraySelect(int ptr, object selector)
            {
                var srcElemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(srcElemType);
                object?[] src = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;
                    src[i] = IsReferenceType(srcElemType)
                                ? DerefReference(addr, srcElemType)
                                : ReadFromStack(addr, srcElemType);
                }

                int lambdaId = Convert.ToInt32(selector);
                object?[] dst = new object?[len];
                for (int i = 0; i < len; i++)
                    dst[i] = InvokeById(lambdaId, src[i]);

                ValueType dstElemType = ValueType.Object;
                object? sample = dst.FirstOrDefault(v => v is not null);
                if (sample is not null)
                {
                    dstElemType = InferType(sample);
                    if (dst.Any(v => v is not null && InferType(v!) != dstElemType))
                        dstElemType = ValueType.Object;
                }
                int newPtr;
                if (IsReferenceType(dstElemType))
                {
                    newPtr = Malloc(sizeof(int) * len, dstElemType);
                    for (int i = 0; i < len; i++)
                    {
                        int packed = dst[i] switch
                        {
                            null => -1,
                            string s => PackReference(s, ValueType.String),
                            int p when dstElemType == ValueType.Array
                                               || dstElemType == ValueType.IntPtr => p,
                            _ => PackReference(dst[i]!, dstElemType)
                        };
                        BitConverter.GetBytes(packed).CopyTo(RawMemory, newPtr + i * sizeof(int));
                    }
                }
                else
                {
                    int dstSize = GetTypeSize(dstElemType);
                    newPtr = Malloc(dstSize * len, dstElemType);

                    for (int i = 0; i < len; i++)
                    {
                        object val = dst[i] ?? 0;
                        ReadOnlySpan<byte> srcBytes = dstElemType switch
                        {
                            ValueType.Int => BitConverter.GetBytes(Convert.ToInt32(val)),
                            ValueType.Uint => BitConverter.GetBytes(Convert.ToUInt32(val)),
                            ValueType.Long => BitConverter.GetBytes(Convert.ToInt64(val)),
                            ValueType.Ulong => BitConverter.GetBytes(Convert.ToUInt64(val)),
                            ValueType.Short => BitConverter.GetBytes(Convert.ToInt16(val)),
                            ValueType.UShort => BitConverter.GetBytes(Convert.ToUInt16(val)),
                            ValueType.Byte => new[] { Convert.ToByte(val) },
                            ValueType.Sbyte => new[] { unchecked((byte)Convert.ToSByte(val)) },
                            ValueType.Float => BitConverter.GetBytes(Convert.ToSingle(val)),
                            ValueType.Double => BitConverter.GetBytes(Convert.ToDouble(val)),
                            ValueType.Char => BitConverter.GetBytes(Convert.ToChar(val)),
                            ValueType.Bool => BitConverter.GetBytes(Convert.ToBoolean(val)),
                            _ => throw new ApplicationException($"ArraySelect: unsupported result type {dstElemType}")
                        };
                        srcBytes.CopyTo(RawMemory.AsSpan(newPtr + i * dstSize, dstSize));
                    }
                }

                return newPtr;

            }
            public int ArrayWhere(int ptr, object predicate)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);

                object?[] buf = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;
                    buf[i] = IsReferenceType(elemType)
                                ? DerefReference(addr, elemType)
                                : ReadFromStack(addr, elemType);
                }

                int lambdaId = Convert.ToInt32(predicate);
                var passed = new List<object?>(len);
                foreach (var v in buf)
                    if (Convert.ToBoolean(InvokeById(lambdaId, v)))
                        passed.Add(v);

                int outLen = passed.Count;
                int newPtr;
                if (IsReferenceType(elemType))
                {
                    newPtr = Malloc(sizeof(int) * outLen, elemType);
                    for (int i = 0; i < outLen; i++)
                    {
                        int packed = passed[i] switch
                        {
                            null => -1,
                            string s => PackReference(s, ValueType.String),
                            int p => p,
                            _ => PackReference(passed[i]!, elemType)
                        };
                        BitConverter.GetBytes(packed)
                                    .CopyTo(RawMemory, newPtr + i * sizeof(int));
                    }
                }
                else
                {
                    newPtr = Malloc(eSize * outLen, elemType);
                    for (int i = 0; i < outLen; i++)
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(elemType, passed[i]);
                        src.CopyTo(RawMemory.AsSpan(newPtr + i * eSize, eSize));
                    }
                }
                return newPtr;
            }
            public int ArrayRange(int start, int end)
            {
                int len = Math.Abs(end - start);
                int ptr = Malloc(sizeof(int) * len, ValueType.Int);

                for (int i = 0, v = start, step = end >= start ? 1 : -1; i < len; i++, v += step)
                {
                    BitConverter.GetBytes(v) .CopyTo(RawMemory, ptr + i * sizeof(int));
                }
                return ptr;
            }

            public bool ArrayAny(int ptr, object predicate)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                int lambdaId = Convert.ToInt32(predicate);

                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;
                    object? val = IsReferenceType(elemType)
                                    ? DerefReference(addr, elemType)
                                    : ReadFromStack(addr, elemType);
                    if (Convert.ToBoolean(InvokeById(lambdaId, val)))
                        return true;
                }
                return false;
            }

            public bool ArrayAll(int ptr, object predicate)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                int lambdaId = Convert.ToInt32(predicate);

                for (int i = 0; i < len; i++)
                {
                    int addr = ptr + i * eSize;
                    object? val = IsReferenceType(elemType)
                                    ? DerefReference(addr, elemType)
                                    : ReadFromStack(addr, elemType);
                    if (!Convert.ToBoolean(InvokeById(lambdaId, val)))
                        return false;
                }
                return true;
            }
            public object? ArrayExtremum(int ptr, object keySel, bool min)
            {
                var et = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr), sz = GetTypeSize(et);
                int lid = Convert.ToInt32(keySel);
                object? bestElem = null, bestKey = null;
                bool hasAny = false;

                for (int i = 0; i < len; i++)
                {
                    object? v = IsReferenceType(et)
                                ? DerefReference(ptr + i * sz, et)
                                : ReadFromStack(ptr + i * sz, et);
                    object? key = InvokeById(lid, v);
                    if (!hasAny || Comparer<object>.Default.Compare(key!, bestKey!) * (min ? 1 : -1) < 0)
                    { bestElem = v; bestKey = key; hasAny = true; }
                }
                if (!hasAny) throw new InvalidOperationException("Empty sequence");
                return bestKey;
            }
            public object? ArrayFind(int ptr, object pred, bool fromEnd, bool orDefault, bool single)
            {
                var et = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr), sz = GetTypeSize(et);
                int lid = Convert.ToInt32(pred);
                object? found = null; int hits = 0;

                IEnumerable<int> idx = Enumerable.Range(0, len);
                if (fromEnd) idx = idx.Reverse();
                foreach (int i in idx)
                {
                    object? v = IsReferenceType(et)
                                ? DerefReference(ptr + i * sz, et)
                                : ReadFromStack(ptr + i * sz, et);
                    if (!Convert.ToBoolean(InvokeById(lid, v))) continue;

                    if (!single) return v; // First/Last
                    if (++hits == 1) found = v; // Single
                    else throw new InvalidOperationException("Sequence contains more than one matching element");
                }
                if (single) hits = (hits == 1 ? 1 : 0);
                if (hits == 1) return found;
                return orDefault ? DefaultValue(et) : throw new InvalidOperationException("No element satisfies the condition");
            }
            public int ArrayCount(int ptr, object predicate)
            {
                var et = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int sz = GetTypeSize(et);
                int lid = Convert.ToInt32(predicate);
                int cnt = 0;
                for (int i = 0; i < len; i++)
                {
                    int a = ptr + i * sz;
                    object? v = IsReferenceType(et) ? DerefReference(a, et) : ReadFromStack(a, et);
                    if (Convert.ToBoolean(InvokeById(lid, v))) cnt++;
                }
                return cnt;
            }
            public int ArraySum(int ptr, object selector)
            {
                var et = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr), sz = GetTypeSize(et);
                int lid = Convert.ToInt32(selector);
                int acc = 0;
                for (int i = 0; i < len; i++)
                {
                    object? v = IsReferenceType(et) ? DerefReference(ptr + i * sz, et) : ReadFromStack(ptr + i * sz, et);
                    acc += Convert.ToInt32(InvokeById(lid, v));
                }
                return acc;
            }

            public ReadOnlySpan<byte> GetSourceBytes(ValueType vt, object? data)
            {
                switch (vt)
                {
                    case ValueType.Int: return BitConverter.GetBytes((int)data!);
                    case ValueType.Uint: return BitConverter.GetBytes((uint)data!);
                    case ValueType.Long: return BitConverter.GetBytes((long)data!);
                    case ValueType.Ulong: return BitConverter.GetBytes((ulong)data!);
                    case ValueType.Short: return BitConverter.GetBytes((short)data!);
                    case ValueType.UShort: return BitConverter.GetBytes((ushort)data!);
                    case ValueType.Byte: return new[] { (byte)data! };
                    case ValueType.Sbyte: return new[] { unchecked((byte)(sbyte)data!) };
                    case ValueType.Float: return BitConverter.GetBytes((float)data!);
                    case ValueType.Double: return BitConverter.GetBytes((double)data!);
                    case ValueType.Decimal: return BitConverter.GetBytes((ulong)data!);
                    case ValueType.Char: return BitConverter.GetBytes((char)data!);
                    case ValueType.Bool: return BitConverter.GetBytes((bool)data!);
                    default:
                        throw new ApplicationException($"GetSourceBytes: Unsupported element type {vt}");
                }
            }
            private ReadOnlySpan<byte> GetSourceBytes(object? data)
            {
                if (data is int i) return BitConverter.GetBytes(i);
                else if (data is uint ui) return BitConverter.GetBytes(ui);
                else if (data is long l) return BitConverter.GetBytes(l);
                else if (data is ulong ul) return BitConverter.GetBytes(ul);
                else if (data is short s) return BitConverter.GetBytes(s);
                else if (data is ushort us) return BitConverter.GetBytes(us);
                else if (data is byte b) return new byte[] { b };
                else if (data is sbyte sb) return new byte[] { unchecked((byte)sb) };
                else if (data is char c) return BitConverter.GetBytes(c);
                else if (data is float f) return BitConverter.GetBytes(f);
                else if (data is double d) return BitConverter.GetBytes(d);
                else if (data is string str) return BitConverter.GetBytes(PackReference(str, ValueType.String));
                else if (data is decimal m)
                {
                    int[] bits = decimal.GetBits(m);
                    Span<byte> tmp = stackalloc byte[16];
                    for (int j = 0; j < 4; j++)
                        BitConverter.GetBytes(bits[j]).CopyTo(tmp.Slice(j * 4));
                    return tmp.ToArray();
                }
                else if (data is null) return BitConverter.GetBytes(-1);
                else
                    throw new ApplicationException($"GetSourceBytes: Unsupported element type {data.GetType()}");
            }
            private object? DefaultValue(ValueType vt) => vt switch
            {
                ValueType.Bool => false,
                ValueType.Char => '\0',
                ValueType.Byte => (byte)0,
                ValueType.Sbyte => (sbyte)0,
                ValueType.Short => (short)0,
                ValueType.UShort => (ushort)0,
                ValueType.Int => 0,
                ValueType.Uint => 0U,
                ValueType.Long => 0L,
                ValueType.Ulong => 0UL,
                ValueType.Float => 0f,
                ValueType.Double => 0d,
                _ => null
            };
            private object? DerefReference(int addr, ValueType vt)
            {
                int handle = BitConverter.ToInt32(_memory, addr);

                return vt switch
                {
                    ValueType.String => handle <= 0 ? null : ReadHeapString(handle),
                    ValueType.Object => GetObject(handle),
                    ValueType.Array => handle <= 0 ? null : handle,
                    _ => handle <= 0 ? null : handle
                };
            }
            


            #endregion
            internal int AddObject(object o) => _handles.Add(o);
            internal object? GetObject(int id) => _handles.Get(id);
            internal void ReleaseObject(int id) => _handles.Release(id);
            #endregion

            #endregion

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
                        //_scopes.Peek()[name] = new Variable(type, -1, sizeof(int));
                        _scopes.Peek()[name] = Stackalloc(ValueType.String);
                        WriteVariable(name, -1);
                    }
                    else throw new ApplicationException("Declaring string with non-string value");
                }
                else if (type == ValueType.Array)
                {
                    if (value is ValueTuple<int, ValueType> info)
                    {
                        if (!IsReferenceType(info.Item2))
                        {
                            int length = GetTypeSize(info.Item2) * info.Item1;
                            var address = Malloc(length, info.Item2);
                            var pointer = Stackalloc(ValueType.Array);
                            _scopes.Peek()[name] = pointer;
                            WriteVariable(name, address);
                        }
                        else if (IsReferenceType(info.Item2))
                        {
                            int bytes = sizeof(int) * info.Item1;
                            int basePtr = Malloc(bytes, info.Item2);
                            _memory.AsSpan(basePtr, bytes).Fill(0xFF); //-1 = null
                            var varSlot = Stackalloc(ValueType.Array);
                            _scopes.Peek()[name] = varSlot;
                            WriteVariable(name, basePtr);
                        }
                    }
                    else if (value is int ptr)
                    {
                        var slot = Stackalloc(ValueType.Array);
                        _scopes.Peek()[name] = slot;
                        WriteVariable(name, ptr);
                    }
                    else if (value is null)
                    {
                        _scopes.Peek()[name] = new Variable(type, -1, sizeof(int));
                    }
                    else throw new ApplicationException("Declaring array pointer with non-array data");
                }
                else
                {

                    if (type == ValueType.Object && value is not null) type = InferType(value);
                    if (type == ValueType.Object && value is ValueTuple<int, bool> obj)
                    {
                        var bytes = BitConverter.GetBytes(obj.Item1);
                        var address = Malloc(GetTypeSize(ValueType.Object), ValueType.Object);
                        var pointer = Stackalloc(ValueType.Object);
                        _scopes.Peek()[name] = pointer;
                        WriteVariable(name, address);
                        WriteBytes(address, bytes);
                        return;
                    }
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
            public Ast.ValueType InferType(object v)
            {
                switch (v)
                {
                    case int: return ValueType.Int;
                    case double: return ValueType.Double;
                    case float: return ValueType.Float;
                    case decimal: return ValueType.Decimal;
                    case uint: return ValueType.Uint;
                    case long: return ValueType.Long;
                    case ulong: return ValueType.Ulong;
                    case ushort: return ValueType.UShort;
                    case short: return ValueType.Short;
                    case byte: return ValueType.Byte;
                    case sbyte: return ValueType.Sbyte;
                    case char: return ValueType.Char;
                    case string: return ValueType.String;
                    case bool: return ValueType.Bool;
                    default: return ValueType.Object;
                }
            }
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
            internal static bool IsObject(object? o) => o is not null && o is not string &&
                   o.GetType().IsValueType == false && o is not Array;
            public object InvokeById(object id, params object[] args)
            {
                string name = id.ToString();

                if (!Functions.TryGetValue(name, out var overloads))
                    throw new ApplicationException($"Anonymous function {name} not found.");

                var fn = overloads.FirstOrDefault(f => f.ParamNames.Length == args.Length);
                EnterFunction();
                EnterScope();
                Check();
                try
                {
                    for (int i = 0; i < fn.ParamNames.Length; i++)
                        Declare(fn.ParamNames[i], fn.ParamTypes[i], args[i]);

                    return fn.Body.Evaluate(this);
                }
                finally
                {
                    ExitScope();
                    ExitFunction();
                }
            }

            #endregion

        }
        #endregion
        #region Object Handle
        internal sealed class ObjectTable : IDisposable
        {
            private readonly GCHandle[] _slots;
            private readonly Stack<int> _free;

            public ObjectTable(int capacity)
            {
                _slots = new GCHandle[capacity];
                _free = new Stack<int>(capacity);
                for (int i = capacity - 1; i >= 0; --i)
                    _free.Push(i);
            }

            public int Add(object obj)
            {
                if (_free.Count == 0)
                    throw new OutOfMemoryException("Object table is full");

                int slot = _free.Pop();
                _slots[slot] = GCHandle.Alloc(obj, GCHandleType.Normal);
                return slot;
            }

            public object? Get(int id) =>
                (uint)id < _slots.Length && _slots[id].IsAllocated
                   ? _slots[id].Target
                   : null;

            public void Release(int id)
            {
                if ((uint)id >= _slots.Length || !_slots[id].IsAllocated)
                    return;

                _slots[id].Free();
                _free.Push(id);
            }

            public void Dispose()
            {
                foreach (ref var h in _slots.AsSpan())
                    if (h.IsAllocated) h.Free();
            }
        }

        #endregion
        public ExecutionContext Context { get; }
        public AstNode? RootNode { get; private set; }
        public string Output = "";
        private Random _random = new();
        public Ast()
        {
            this.Context = new Ast.ExecutionContext(CancellationToken.None);
        }
        public Ast(CancellationToken token)
        {
            this.Context = new Ast.ExecutionContext(token);
        }
        public Ast(CancellationToken token, int totalMemory, int stackSize = 1024)
        {
            this.Context = new Ast.ExecutionContext(token, totalMemory, stackSize);
        }
        public string Interpret(string code, bool consoleOutput = false, bool printTree = false)
        {
            try
            {
                ImportStandartLibrary(consoleOutput);
                var parser = new Parser(code);
                var ast = parser.ParseProgram();
                RootNode = ast;
                if (printTree) RootNode.Print();
                RootNode.Evaluate(this.Context); 
            }
            catch (OperationCanceledException)
            {
                if (consoleOutput) Console.WriteLine("Program timed out");
                Output = "Program timed out";
            }
            catch (Exception e)
            {
                if (consoleOutput) Console.WriteLine(e.ToString());
                Output += e.Message;
            }
            finally
            {
                //Console.WriteLine(Context.PrintMemory());
                Context.Dispose();
            }
            return Output;
        }
        public enum ValueType
        {
            Int, String, Bool, Double, Ulong, Uint, Long, Byte, Object, Sbyte, Short, Char, UShort, Float, Decimal, IntPtr, Array, Enum
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
            Or,
            Lambda,
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
            Dot,
        }

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
        private void ImportStandartLibrary(bool consoleOutput = false)
        {
            if (consoleOutput)
            {
                this.Context.RegisterNative("print", (Action<string>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<int>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<double>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<ulong>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<long>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<bool>)Console.WriteLine);
                this.Context.RegisterNative("print", (Action<char>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<string>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<int>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<double>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<ulong>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<long>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<bool>)Console.WriteLine);
                this.Context.RegisterNative("WriteLine", (Action<char>)Console.WriteLine);
                this.Context.RegisterNative("Write", (Action<string>)Console.Write);
                this.Context.RegisterNative("Write", (Action<double>)Console.Write);
                this.Context.RegisterNative("Write", (Action<int>)Console.Write);
                this.Context.RegisterNative("Write", (Action<ulong>)Console.Write);
                this.Context.RegisterNative("Write", (Action<long>)Console.Write);
                this.Context.RegisterNative("Write", (Action<bool>)Console.Write);
                this.Context.RegisterNative("Write", (Action<char>)Console.Write);
            }
            else
            {
                this.Context.RegisterNative("print", (string str) => { if(Output.Length < 4000) Output += (str + '\n'); });
                this.Context.RegisterNative("print", (int str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("print", (ulong str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("print", (double str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("print", (bool str) => { if (Output.Length < 4000) Output += str.ToString() + '\n'; });
                this.Context.RegisterNative("print", (char str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("WriteLine", (string str) => { if (Output.Length < 4000) Output += (str + '\n'); });
                this.Context.RegisterNative("WriteLine", (int str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("WriteLine", (ulong str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("WriteLine", (double str) => { if (Output.Length < 4000) Output += str + '\n'; });
                this.Context.RegisterNative("WriteLine", (bool str) => { if (Output.Length < 4000) Output += str.ToString() + '\n'; });
                this.Context.RegisterNative("WriteLine", (char str) => { if (Output.Length < 4000) Output += str.ToString() + '\n'; });
                this.Context.RegisterNative("Write", (string str) => { if (Output.Length < 4000) Output += (str); });
                this.Context.RegisterNative("Write", (int str) => { if (Output.Length < 4000) Output += str; });
                this.Context.RegisterNative("Write", (ulong str) => { if (Output.Length < 4000) Output += str; });
                this.Context.RegisterNative("Write", (double str) => { if (Output.Length < 4000) Output += str; });
                this.Context.RegisterNative("Write", (bool str) => { if (Output.Length < 4000) Output += str; });
                this.Context.RegisterNative("Write", (char str) => { if (Output.Length < 4000) Output += str; });
            }
            this.Context.RegisterNative("typeof", (object o) => { return o.GetType().ToString(); });
            this.Context.RegisterNative("sizeof", (object o) => { return Context.GetTypeSize(Context.InferType(o)); });
            this.Context.RegisterNative("Length", (Func<int, int>)Context.GetArrayLength);
            this.Context.RegisterNative("Length", (string str) => str.Length);
            this.Context.RegisterNative("Count", (Func<int, int>)Context.GetArrayLength);
            this.Context.RegisterNative("Resize", (Action<string, int>)Context.ArrayResize);
            this.Context.RegisterNative("Resize", (Func<int, int, int>)Context.ArrayResize);
            this.Context.RegisterNative("Add", (Action<string, object>)Context.ArrayAdd);
            this.Context.RegisterNative("Add", (Func<int, object, int>)Context.ArrayAdd);
            this.Context.RegisterNative("AddAt", (Action<string, int, object>)Context.ArrayAddAt);
            this.Context.RegisterNative("AddAt", (Func<int, int, object, int>)Context.ArrayAddAt);
            this.Context.RegisterNative("IndexOf", (Func<string, object, int>)Context.ArrayIndexOf);
            this.Context.RegisterNative("IndexOf", (Func<int, object, int>)Context.ArrayIndexOf);
            this.Context.RegisterNative("RemoveAt", (Action<string, int>)Context.ArrayRemoveAt);
            this.Context.RegisterNative("RemoveAt", (Func<int, int, int>)Context.ArrayRemoveAt);
            this.Context.RegisterNative("Remove", (string varName, object value) 
                => { int i = Context.ArrayIndexOf(varName, value); if (i < 0) return; Context.ArrayRemoveAt(varName, i); });
            this.Context.RegisterNative("Remove", (int ptr, object value)
                => { int i = Context.ArrayIndexOf(ptr, value); if (i < 0) return ptr; return Context.ArrayRemoveAt(ptr, i); });
            this.Context.RegisterNative("Sort", (int ptr) => Context.ArraySort(ptr, asc: true));
            this.Context.RegisterNative("SortDescending", (int ptr) => Context.ArraySort(ptr, asc: false));
            this.Context.RegisterNative("SortBy", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: true));
            this.Context.RegisterNative("SortByDescending", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: false));
            this.Context.RegisterNative("Select", (int ptr, object pred) => Context.ArraySelect(ptr, pred));
            this.Context.RegisterNative("Where", (int ptr, object pred) => Context.ArrayWhere(ptr, pred));
            this.Context.RegisterNative("Any", (int ptr, object pred) => Context.ArrayAny(ptr, pred));
            this.Context.RegisterNative("All", (int ptr, object pred) => Context.ArrayAll(ptr, pred));
            this.Context.RegisterNative("MinBy", (int p, object sel) => Context.ArrayExtremum(p, sel, true));
            this.Context.RegisterNative("MaxBy", (int p, object sel) => Context.ArrayExtremum(p, sel, false));
            this.Context.RegisterNative("Count", (int p, object pr) => Context.ArrayCount(p, pr));
            this.Context.RegisterNative("Sum", (int p, object sel) => Context.ArraySum(p, sel));
            this.Context.RegisterNative("First", (int p, object pr) => Context.ArrayFind(p, pr, false, false, false));
            this.Context.RegisterNative("FirstOrDefault", (int p, object pr) => Context.ArrayFind(p, pr,false, true, false));
            this.Context.RegisterNative("Last", (int p, object pr) => Context.ArrayFind(p, pr, true, false, false));
            this.Context.RegisterNative("LastOrDefault", (int p, object pr) => Context.ArrayFind(p, pr, true, true, false));
            this.Context.RegisterNative("Single", (int p, object pr) => Context.ArrayFind(p, pr, false, false, true));
            this.Context.RegisterNative("SingleOrDefault", (int p, object pr) => Context.ArrayFind(p, pr, false,true, true));
            this.Context.RegisterNative("Concat", (Func<int, int, int>)Context.ArrayConcat);
            this.Context.RegisterNative("Range", (Func<int, int, int>)Context.ArrayRange);
            this.Context.RegisterNative("Range", (int start) => Context.ArrayRange(0,start));
            this.Context.RegisterNative("IsNullOrEmpty", (string str) => string.IsNullOrEmpty(str));
            this.Context.RegisterNative("IsNullOrWhiteSpace", (string str) => string.IsNullOrWhiteSpace(str));
            this.Context.RegisterNative("Substring", (string str, int start)=> Context.PackReference(str.Substring(start), ValueType.String));
            this.Context.RegisterNative("Substring", (string str, int start, int len)=> Context.PackReference(str.Substring(start, len), ValueType.String));
            this.Context.RegisterNative("Pow", (Func<double, double, double>)Math.Pow);
            this.Context.RegisterNative("IsDigit", (char c) => { return char.IsDigit(c); });
            this.Context.RegisterNative("IsLetter", (char c) => { return char.IsLetter(c); });
            this.Context.RegisterNative("Numeric", (string str) => { return ulong.TryParse(str, out _); });
            this.Context.RegisterNative("IsNumber", (string str) => { return double.TryParse(str, out _); });
            this.Context.RegisterNative("Join", (Func<string, string, string>)Join);
            this.Context.RegisterNative("Join", (int varaiable, char separator) => { return Join(separator.ToString(), varaiable); });
            this.Context.RegisterNative("Join", (int varaiable, string separator) => { return Join(separator, varaiable); });
            this.Context.RegisterNative("Join", (int varaiable) => { return Join(", ", varaiable); });
            this.Context.RegisterNative("Random", (Func<int, int, int>)_random.Next);
            this.Context.RegisterNative("Next", (Func<int, int, int>)_random.Next);
            this.Context.RegisterNative("UtcNow", () => { return DateTime.UtcNow.ToString(); });
            this.Context.RegisterNative("TimeNow", () => { return DateTime.Now.ToString(); });  
            this.Context.RegisterNative("InvokeByAttribute", (string attr, string[] attrArgs, object[] callArgs)
                => InvokeByAttribute(this.Context, attr, attrArgs, callArgs));
            this.Context.RegisterNative("Split", (Func<string, string, int>)Split);
            this.Context.RegisterNative("Split", (Func<string, char, int>)((s, c) => Split(s, c.ToString())));
            this.Context.RegisterNative("Contains", (string str, string value) => { return str.Contains(value); });
            this.Context.RegisterNative("Contains", (string str, ulong value) => { return str.Contains(value.ToString()); });
            this.Context.RegisterNative("Contains", (string str, int value) => { return str.Contains(value.ToString()); });
            this.Context.RegisterNative("Contains", (int ptr, object value) => { return this.Context.ArrayIndexOf(ptr, value) != -1; });
            this.Context.RegisterNative("ToString", (int val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (double val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (ulong val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (float val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (decimal val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (uint val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (long val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (byte val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (char val) => { return val.ToString(); });
            this.Context.RegisterNative("ToString", (string val) => { return val; });
            this.Context.RegisterNative("IntParse", (string val) => { return int.Parse(val); });
            this.Context.RegisterNative("UIntParse", (string val) => { return uint.Parse(val); });
            this.Context.RegisterNative("LongParse", (string val) => { return long.Parse(val); });
            this.Context.RegisterNative("ULongParse", (string val) => { return ulong.Parse(val); });
            this.Context.RegisterNative("DoubleParse", (string val) => { return double.Parse(val); });
            this.Context.RegisterNative("FloatParse", (string val) => { return float.Parse(val); });
            this.Context.RegisterNative("DecimalParse", (string val) => { return decimal.Parse(val); });
            this.Context.RegisterNative("ByteParse", (string val) => { return byte.Parse(val); });
            this.Context.RegisterNative("Replace", (string str, string old, string newStr)=> { return str.Replace(old, newStr); });
            this.Context.RegisterNative("Replace", (string str, char old, char newStr)=> { return str.Replace(old, newStr); });
            this.Context.RegisterNative("Replace", (string str, char old, string newStr)=> { return str.Replace(old.ToString(), newStr); });
            this.Context.RegisterNative("Invoke", (Func<object, object>)(id => Context.InvokeById(id)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object>)((id, a1) => Context.InvokeById(id, a1)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object>)((id, a1, a2) => Context.InvokeById(id, a1, a2)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object, object>)((id, a1, a2, a3) => Context.InvokeById(id, a1, a2, a3)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object, object, object>)((id, a1, a2, a3, a4) => Context.InvokeById(id, a1, a2, a3, a4)));

        }
        #region Helpers
        
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
            ValueType.Array => value is ValueTuple<int, ValueType> 
            || value is ValueTuple<object?[], ValueType> 
            || value is int,
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
        private string Join(string separator, string arrayName)
        {
            var varInfo = Context.Get(arrayName);
            if (varInfo.Type != ValueType.Array)
                throw new ApplicationException($"{arrayName} is not an array");
            int ptr = (int)Context.ReadVariable(arrayName);
            return Join(separator, ptr);
        }
        private string Join(string separator, int ptr)
        {
            if (ptr < Context.StackSize || ptr >= Context.RawMemory.Length - sizeof(int))
                throw new ArgumentOutOfRangeException(nameof(ptr), $"nullptr {ptr}");
            var elemType = Context.GetHeapObjectType(ptr);
            int elemSize = Context.GetTypeSize(elemType);
            int bytes = Context.GetHeapObjectLength(ptr);
            var sb = new StringBuilder();
            for (int offset = 0; offset < bytes; offset += elemSize)
            {
                if (offset > 0)
                    sb.Append(separator);
                object? slice = null;
                if (elemType == Ast.ValueType.String)
                {
                    int strPtr = BitConverter.ToInt32(Context.RawMemory, ptr + offset);
                    if (strPtr > 0)
                    {
                        slice = Context.ReadHeapString(strPtr); 
                    }
                }
                else slice = Context.ReadFromMemorySlice(Context.RawMemory[(ptr + offset)..(ptr + offset + elemSize)],elemType);
                sb.Append(slice?.ToString() ?? string.Empty);
            }
            return sb.ToString();

        }
        private int Split(string str, string sep)
        {
            if (str is null) throw new NullReferenceException("null reference in Split");
            var parts = str.Split(sep);
            int count = parts.Length;

            int arrPtr = this.Context.Malloc(sizeof(int) * count, Ast.ValueType.String);
            var span = this.Context.RawMemory.AsSpan(arrPtr, count * sizeof(int));

            for (int i = 0; i < count; i++)
            {
                string part = parts[i];
                byte[] bytes = Encoding.UTF8.GetBytes(part);
                int strPtr = this.Context.Malloc(bytes.Length, Ast.ValueType.String);
                this.Context.WriteBytes(strPtr, bytes);

                BitConverter.GetBytes(strPtr).CopyTo(span.Slice(i * sizeof(int)));
            }

            return arrPtr;
        }
        private bool HasAttribute( Ast.ExecutionContext.Function fn, string name, string[] args)
            => fn.Attributes.Any(a => a.Name == name && a.Args.Length == args.Length && a.Args.SequenceEqual(args, StringComparer.Ordinal));
        
        public object? InvokeByAttribute(Ast.ExecutionContext ctx, string attrName, string[] attrArgs, object[] callArgs)
        {
            var candidates = ctx.Functions
                     .SelectMany(kv => kv.Value)
                     .Where(f => HasAttribute(f, attrName, attrArgs))
                     .ToList();

            Ast.ExecutionContext.Function? candidate = null;
            foreach (var fn in candidates)
            {
                if (fn.ParamNames.Length != callArgs.Length) continue;

                bool ok = true;
                for (int i = 0; i < callArgs.Length && ok; i++)
                {
                    var need = fn.ParamTypes[i];
                    var arg = callArgs[i];

                    if (need == Ast.ValueType.Object) continue;
                    if (Ast.MatchVariableType(arg, need)) continue;

                    try { ctx.Cast(arg!, need); }
                    catch { ok = false; }
                }

                if (ok) { candidate = fn; break; }
            }

            if (candidate is null)
                throw new Exception($"No overload of [{attrName}] matches the provided arguments.");

            ctx.Check();
            ctx.EnterFunction();
            ctx.EnterScope();
            try
            {
                for (int i = 0; i < callArgs.Length; i++)
                {
                    object? arg = callArgs[i];
                    var needType = (candidate.Value).ParamTypes[i];

                    if (!Ast.MatchVariableType(arg, needType) && needType != Ast.ValueType.Object)
                        arg = ctx.Cast(arg!, needType);

                    ctx.Declare((candidate).Value.ParamNames[i], needType, arg);
                }
                var res = (candidate.Value).Body.Evaluate(ctx);
                return res is Ast.ReturnSignal r ? r.Value : res;
            }
            finally
            {
                ctx.ExitScope();
                ctx.ExitFunction();
            }
        }
        
        #endregion
        public struct Variable
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
                            return Ast.IsReferenceType(vInfo.Type) ? context.ReadVariable(vr.Name) : vInfo.Address;
                        }
                        throw new Exception($"& operator requires a variable, got {Operand.GetType()}");
                    case OperatorToken.Multiply: //*ptr dereference
                        if (Operand is VariableReferenceNode operand)
                        {
                            var vInfo = context.Get(operand.Name);
                            if (vInfo.Type != ValueType.IntPtr)
                                throw new Exception("Cannot dereference non‑pointer variable");

                            int addr = (int)context.ReadVariable(operand.Name);
                            if (addr >= context.StackSize)//heap object
                            {
                                var type = context.GetHeapObjectType(addr);
                                if (type == ValueType.String) return context.ReadHeapString(addr);

                                throw new NotImplementedException($"Dereferencing unknown type in heap: {type}");
                            }
                            context.ValidateAddress(addr, context.GetTypeSize(vInfo.Type));
                            var varInfo = context.GetVariableByAddress(addr);
                            if (varInfo is null) throw new InvalidOperationException("Stack pointer to dead memory");
                            return context.ReadFromStack(addr, (ValueType)(varInfo?.Type));
                        }
                        int p = Convert.ToInt32(Operand.Evaluate(context));
                        context.ValidateAddress(p, sizeof(int));
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
                Console.WriteLine($"{indent}└── {(Op == OperatorToken.Multiply ? "Dereference" : Op)}");
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
                if (Op == OperatorToken.Equals && Left is VariableReferenceNode lv)
                {
                    var lvInfo = context.Get(lv.Name);
                    if (lvInfo.Type == ValueType.String)
                    {
                        //if variable copy pointer
                        if (Right is VariableReferenceNode rv && context.Get(rv.Name).Type == ValueType.String)
                        {
                            var dInfo = context.Get(lv.Name);
                            var sInfo = context.Get(rv.Name);
                            if (dInfo.Type != ValueType.String || sInfo.Type != ValueType.String)
                                throw new ApplicationException("Both vars must be strings for ptr copy");

                            int oldDestPtr = (int)context.ReadVariable(lv.Name);
                            if (oldDestPtr >= context.StackSize) context.Free(oldDestPtr);

                            int newPtr = (int)context.ReadVariable(rv.Name);
                            context.WriteVariable(lv.Name, newPtr);
                            return Right.Evaluate(context);
                        }

                        //if value reallocate?
                        string strVal = Right.Evaluate(context)?.ToString() ?? string.Empty;
                        context.StoreStringVariable(lv.Name, strVal);
                        return strVal;
                    }
                }
                else if(Op == OperatorToken.Equals && Left is ArrayIndexNode ai)
                {
                    var rhs = Right.Evaluate(context); 
                    ai.Write(context, rhs);
                    return rhs;
                }
                if (Op == OperatorToken.PlusEqual && Left is VariableReferenceNode lvs &&
                    context.Get(lvs.Name).Type == ValueType.String)
                {
                    string cur = Left.Evaluate(context)?.ToString() ?? string.Empty;
                    string add = Right.Evaluate(context)?.ToString() ?? string.Empty;
                    string combined = cur + add;
                    context.StoreStringVariable(lvs.Name, combined);
                    return combined;
                }
                
                var l = Left.Evaluate(context);
                var r = Right.Evaluate(context);
                if ((Op is OperatorToken.Equal or OperatorToken.NotEqual))
                {
                    if(l is string || r is string)
                    {
                        bool eq = string.Equals(l?.ToString(), r?.ToString(), StringComparison.Ordinal);
                        return Op == OperatorToken.Equal ? eq : !eq;
                    }
                    else if(l is null || r is null)
                    {
                        bool eq = ReferenceEquals(l, r) || (l?.Equals(r) ?? false);
                        return Op == OperatorToken.Equal ? eq : !eq;
                    }
                }

                
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
                else if (l is null) throw new ApplicationException($"Unsupported operator {Op} for null");
                else throw new ApplicationException($"Unsupported operator {Op} for Type {l.GetType()}");
                if (Left is VariableReferenceNode vr && IsAssignmentOp(Op))
                {
                    context.WriteVariable(vr.Name, result);
                }
                else if (Left is ArrayIndexNode ai && IsAssignmentOp(Op))
                {
                    ai.Write(context, result);
                }
                else if (IsAssignmentOp(Op))
                {
                    throw new InvalidOperationException("Assignment operator points to nothing");
                }
                    return result;
            }
            private static bool IsAssignmentOp(OperatorToken op) => op is OperatorToken.Equals or OperatorToken.PlusEqual or
                OperatorToken.MinusEqual or OperatorToken.MultiplyEqual or OperatorToken.DivideEqual;
            public static bool EvaluateBool(bool l, bool r, OperatorToken op) => op switch
            {
                OperatorToken.Equals => r,
                OperatorToken.Equal => l == r,
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
        #region Variable nodes
        public class VariableDeclarationNode : AstNode
        {
            public string Name { get; }
            public ValueType Type { get; }
            public AstNode Expression { get; }
            public bool IsArray { get; }
            public int?[]? ArrayLength { get; }
            public bool IsConst { get; }
            public VariableDeclarationNode(ValueType type, string name, AstNode expression, 
                bool isArray = false, int?[]? arrayLength = null, bool isConst = false)
            {
                Type = type;
                Name = name;
                Expression = expression;
                IsArray = isArray;
                ArrayLength = arrayLength;
                IsConst = isConst;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var value = Expression.Evaluate(context);
                if (IsArray)
                {
                    if (value is ValueTuple<object?[], ValueType> arr)
                    {
                        object?[] vals = arr.Item1;
                        ValueType elemT = arr.Item2 == ValueType.Object ? Type : arr.Item2;
                        if (elemT != Type)
                            throw new ApplicationException("Type missmatch during declaration");
                        int elemSize = context.GetTypeSize(elemT);
                        int basePtr;
                        if (Ast.IsReferenceType(elemT))
                        {
                            basePtr = context.Malloc(sizeof(int) * vals.Length, elemT);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                int packed = context.PackReference(vals[i]!, elemT);
                                BitConverter.GetBytes(packed).CopyTo(context.RawMemory, basePtr + i * sizeof(int));
                            }
                        }
                        else
                        {
                            int bytes = elemSize * vals.Length;
                            basePtr = context.Malloc(bytes, elemT);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                ReadOnlySpan<byte> src = context.GetSourceBytes(elemT, vals[i]);
                                src.CopyTo(context.RawMemory.AsSpan(basePtr + i * elemSize, elemSize));
                            }
                        }

                        context.Declare(Name, ValueType.Array, (vals.Length, elemT));
                        context.WriteVariable(Name, basePtr);
                        return null;
                    }
                    else if (value is ValueTuple<int, ValueType> info)
                    {
                        if (info.Item2 != Type && info.Item2 != ValueType.Array) throw new ApplicationException($"Type missmatch during array assignment");
                        context.Declare(Name, ValueType.Array, info);
                        var (len, elemType) = info;
                        int bytes = IsReferenceType(elemType) ? sizeof(int) * len : context.GetTypeSize(elemType) * len;
                        int basePtr = context.Malloc(bytes, elemType);
                        int payload = context.GetHeapObjectLength(basePtr);
                        context.RawMemory.AsSpan(basePtr, payload).Fill(IsReferenceType(elemType) ? (byte)0xFF : (byte)0x00);
                        context.WriteVariable(Name, basePtr);
                    }
                    else if (value is int ptr)
                    {
                        context.Declare(Name, ValueType.Array, ptr);
                        context.WriteVariable(Name, ptr);
                    }
                    else if (value is string strPtr && int.TryParse(strPtr, out int sptr))
                    {
                        context.Declare(Name, ValueType.Array, sptr);
                        context.WriteVariable(Name, sptr);
                    }
                    else if (ArrayLength is not null)
                    {
                        int len = ArrayLength.All(d => d.HasValue) ? ArrayLength.Aggregate(1, (p, d) => p * d!.Value) : 0;
                        if (len * context.GetTypeSize(Type) >= context.RawMemory.Length - context.StackSize) throw new OutOfMemoryException();
                        context.Declare(Name, ValueType.Array, (len, Type));
                    }
                    else context.Declare(Name, ValueType.Array, null);
                }
                else context.Declare(Name, Type, value);
                return null;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── declare {Type.ToString().ToLower()}{(IsArray ? "[]" : "")} {Name}");
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
                        if (v.Type == ValueType.String) 
                        {
                            if (ptr < 0) return null;
                            var span = context.GetSpan(ptr);
                            int len = span.IndexOf((byte)0);
                            if (len < 0) len = span.Length;
                            return context.ReadHeapString(span.Slice(0, len)); 
                        }
                        if (v.Type == ValueType.Array) return ptr;
                        if (v.Type == ValueType.Object) return BitConverter.ToInt32(context.GetSpan(ptr));
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
        public class NewArrayNode : AstNode
        {
            public ValueType ElementType { get; }
            public AstNode[] LengthExprs { get; }

            public NewArrayNode(ValueType elem, AstNode[] len)
            {
                ElementType = elem;
                LengthExprs = len;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var len = LengthExprs[0].Evaluate(context);

                if (len is int i)
                {
                    if (i * context.GetTypeSize(ElementType) >= context.RawMemory.Length - context.StackSize) throw new OutOfMemoryException();
                    return (i, ElementType);
                }
                else return null;
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── new {ElementType.ToString().ToLower()}[ ]");
                foreach(var len in LengthExprs)
                    len.Print(indent + (last ? "    " : "│   "), true);
            }
        }
        public class ArrayIndexNode : AstNode
        {
            public AstNode ArrayExpr { get; }
            public AstNode IndexExpr { get; }

            public ArrayIndexNode(AstNode arrayExpr, AstNode indexExpr)
            {
                ArrayExpr = arrayExpr;
                IndexExpr = indexExpr;
            }
            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                object arr = ArrayExpr.Evaluate(context);
                if (arr is string s)
                {
                    int idx = Convert.ToInt32(IndexExpr.Evaluate(context));
                    if (idx < 0 || idx >= s.Length)
                        throw new IndexOutOfRangeException($"index {idx} length {s.Length}");
                    return s[idx]; // char
                }

                int basePtr = Convert.ToInt32(arr);
                int i = Convert.ToInt32(IndexExpr.Evaluate(context));

                int addr = ElementAddress(context, basePtr, i, out var vt);
                if (IsReferenceType(vt))
                {
                    int handle = BitConverter.ToInt32(context.RawMemory, addr);
                    
                    return vt switch
                    {
                        ValueType.String => handle < 0 ? handle : context.ReadHeapString(handle),
                        ValueType.Array => handle,
                        ValueType.Object => handle,
                        _ => handle 
                    };
                }
                else return context.ReadFromStack(addr, vt);
            }
            public void Write(ExecutionContext context, object value)
            {
                int basePtr = Convert.ToInt32(ArrayExpr.Evaluate(context));
                int idx = Convert.ToInt32(IndexExpr.Evaluate(context));
                int addr = ElementAddress(context, basePtr, idx, out var vt);
                if (value is ValueTuple<int, ValueType>)
                {
                    int ptr = context.PackReference(value, ValueType.Array);
                    BitConverter.GetBytes(ptr).CopyTo(context.RawMemory, addr);
                    return;
                }
                if (!MatchVariableType(value, vt))
                    value = context.Cast(value, vt);

                if (context.InferType(value) != context.GetHeapObjectType(basePtr)) 
                    throw new ApplicationException($"Type missmatch during indexing: {context.GetHeapObjectType(basePtr)} -> {context.InferType(value)}");
                
                if (Ast.IsReferenceType(vt)) 
                { 
                    int handle = context.PackReference(value, vt); 
                    BitConverter.GetBytes(handle).CopyTo(context.RawMemory, addr); 
                    return; 
                }
                ReadOnlySpan<byte> src;
                if (value is int i) src = BitConverter.GetBytes(i);
                else if (value is char c) src = BitConverter.GetBytes(c);
                else if (value is uint ui) src = BitConverter.GetBytes(ui);
                else if (value is ulong ul) src = BitConverter.GetBytes(ul);
                else if (value is long l) src = BitConverter.GetBytes(l);
                else if (value is float f) src = BitConverter.GetBytes(f);
                else if (value is double d) src = BitConverter.GetBytes(d);
                else if (value is short s) src = BitConverter.GetBytes(s);
                else if (value is ushort us) src = BitConverter.GetBytes(us);
                else if (value is byte b) src = new[] { b };
                else if (value is sbyte sb) src = new[] { unchecked((byte)sb) };
                else if (value is decimal m)
                {
                    int[] bits = decimal.GetBits(m);
                    Span<byte> bytes = stackalloc byte[16];

                    for (int j = 0; j < 4; j++)
                    {
                        BitConverter.GetBytes(bits[j]).CopyTo(bytes.Slice(j * 4));
                    }
                    src = new ReadOnlySpan<byte>(bytes.ToArray());
                }
                else throw new ApplicationException($"Unknown type while writing to array index: {value.GetType()}");
                context.ValidateAddress(addr, src.Length);
                src.CopyTo(context.RawMemory.AsSpan(addr, src.Length));
            }
            int ElementAddress(ExecutionContext ctx, int basePtr, int index, out ValueType elemType)
            {
                elemType = ctx.GetHeapObjectType(basePtr);
                int elemSize = ctx.GetTypeSize(elemType);
                int length = ctx.GetArrayLength(basePtr);

                if (index < 0 || index >= length)
                    throw new IndexOutOfRangeException($"index {index} length {length}");

                return basePtr + index * elemSize;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── index");
                string ci = indent + (isLast ? "    " : "│   ");
                ArrayExpr.Print(ci, false);
                IndexExpr.Print(ci, true);
            }
        }
        public class ArrayLiteralNode : AstNode
        {
            public ValueType ElementType { get; set; }
            public AstNode[] Items { get; }

            public ArrayLiteralNode(ValueType elemType, AstNode[] items)
            {
                ElementType = elemType;
                Items = items;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();

                object?[] values = Items.Select(i => i.Evaluate(context)).ToArray();

                //ValueTuple<object?[], Ast.ValueType>
                return (values, ElementType);
            }

            public override void Print(string ind = "", bool last = true)
            {
                Console.WriteLine($"{ind}└── literal-array ({ElementType.ToString().ToLower()})");
                string child = ind + (last ? "    " : "│   ");
                foreach (var (n, i) in Items.Select((n, idx) => (idx, n)))
                    i.Print(child, n == Items.Length - 1);
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

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var val = Expr.Evaluate(context);
                return context.Cast(val, Target);
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
        public class WhileNode : AstNode
        {
            public Ast.AstNode Condition { get; }
            public Ast.AstNode Body { get; }

            public WhileNode(Ast.AstNode condition, Ast.AstNode body)
            {
                Condition = condition;
                Body = body;
            }

            public override object Evaluate(ExecutionContext context)
            {
                while (true)
                {
                    context.Check();
                    context.EnterScope();
                    try
                    {;
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
                    finally { context.ExitScope(); }
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
        public class DoWhileNode : Ast.AstNode
        {
            public Ast.AstNode Body { get; }
            public Ast.AstNode Condition { get; }

            public DoWhileNode(Ast.AstNode body, Ast.AstNode condition)
            {
                Body = body;
                Condition = condition;
            }

            public override object Evaluate(ExecutionContext context)
            {
                do
                {
                    context.Check();
                    context.EnterScope();
                    try
                    {
                        var res = Body.Evaluate(context);
                        if (res is BreakSignal) break;
                        if (res is ContinueSignal) { }
                        if (res is ReturnSignal) return res;
                    }
                    finally { context.ExitScope(); }
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
        public class ForNode : Ast.AstNode
        {
            public Ast.AstNode Init { get; }
            public Ast.AstNode Condition { get; }
            public Ast.AstNode Step { get; }
            public Ast.AstNode Body { get; }

            public ForNode(Ast.AstNode init, Ast.AstNode condition, Ast.AstNode step, Ast.AstNode body)
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
                        
                            context.Check();
                            var cond = Condition?.Evaluate(context);
                            if (cond is bool b && !b) break;
                        context.EnterScope();
                        try
                        {
                            var res = Body.Evaluate(context);
                            if (res is BreakSignal) break;
                            if (res is ContinueSignal) { Step?.Evaluate(context); continue; }
                            if (res is ReturnSignal) return res;
                        }
                        finally 
                        { 
                            context.ExitScope(); 
                        }
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
        public class ForeachNode : AstNode
        {
            public ValueType VariableType { get; }
            public string VariableName { get; }
            public AstNode CollectionExpr { get; }
            public AstNode Body { get; }

            public ForeachNode(ValueType variableType, string variableName, AstNode collectionExpr, AstNode body)
            {
                VariableType = variableType;
                VariableName = variableName;
                CollectionExpr = collectionExpr;
                Body = body;
            }

            public override object? Evaluate(ExecutionContext context)
            {
                var collection = CollectionExpr.Evaluate(context);
                IEnumerable<object?> items;
                if (collection is List<int> li) items = (IEnumerable<object?>)li.Cast<object?>();
                else if (collection is List<string> ls) items = (IEnumerable<object?>)ls.Cast<object?>();
                else if (collection is (object?[] arr, _)) items = arr;
                else if (collection is ValueTuple<object?[], ValueType> t) items = t.Item1;
                else if (collection is int ptr) items = Enumerable.Range(0, context.GetArrayLength(ptr)).Select(i =>
                {
                    int addr = ptr + i * sizeof(int);
                    var et = context.GetHeapObjectType(ptr);
                    return Ast.IsReferenceType(et)
                           ? BitConverter.ToInt32(context.RawMemory, addr)
                           : context.ReadFromStack(addr, et);
                });
                else throw new ApplicationException("Unsupported source in foreach");
                foreach (var item in items)
                {
                    context.Check();
                    context.EnterScope();
                    try
                    {
                        context.Declare(VariableName, VariableType, item);

                        var res = Body.Evaluate(context);
                        if (res is BreakSignal) break;
                        if (res is ContinueSignal) continue;
                        if (res is ReturnSignal) return res;
                    }
                    finally { context.ExitScope(); }
                }

                return null;

            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── foreach {VariableType} {VariableName}");
                Body.Print(indent + (isLast ? "    " : "│   "), true);
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
                context.Check();
                var cond = Condition.Evaluate(context);
                if (cond is bool b && b)
                {
                    var res = ThenBody.Evaluate(context);
                    if (res is ReturnSignal or BreakSignal or ContinueSignal)
                        return res;
                }
                else if (ElseBody is not null)
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

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var v = Condition.Evaluate(context);
                return (v is bool b && b) ? IfTrue.Evaluate(context)
                                          : IfFalse.Evaluate(context);
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

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                try
                {
                    var res = TryBlock.Evaluate(context);
                    return res;
                }
                catch (Exception ex)
                {
                    context.EnterScope();
                    try
                    {
                        if (ExVar != null)
                            context.Declare(ExVar, ValueType.String, ex.Message);
                        var res = CatchBlock.Evaluate(context);
                        return res;
                    }
                    finally { context.ExitScope(); }
                }
                finally
                {
                    if (FinallyBlock is not LiteralNode) FinallyBlock.Evaluate(context);
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

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var discVal = Discriminant.Evaluate(context);
                bool execute = false;
                foreach (var (valExpr, body) in Cases)
                {
                    if (!execute)
                    {
                        if (valExpr == null) //default
                            execute = true;
                        else
                            execute = Equals(discVal, valExpr.Evaluate(context));
                    }

                    if (execute)
                    {
                        foreach (var stmt in body)
                        {
                            var r = stmt.Evaluate(context);
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
                    if (Cases[i].body is not null) foreach (var body in Cases[i].body) body.Print((childIndent) + (i == Cases.Count - 1 ? "    " : "│   "), false);
                }
            }
        }
        public class ThrowNode : AstNode
        {
            public AstNode Expr;
            public ThrowNode(AstNode expr) => Expr = expr;

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var msg = Expr.Evaluate(context)?.ToString();
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
                context.Check();
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
            public int PinKey;
            public ReturnSignal(object? value, int pinKey = -1)
            {
                Value = value;
                PinKey = pinKey;
            }
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
            public override object Evaluate(ExecutionContext context)
            {
                var val = Expression?.Evaluate(context);
                
                int pinKey = context.Pin(val);
                return new ReturnSignal(val, pinKey);
            }

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
                for (int i = 0; i < Statements.Count && i < 1024; i++)
                {
                    if (Statements[i] is LabelNode label)
                        labels[label.Name] = i;
                }

                context.Labels = labels;

                //main cycle
                int ip = 0;
                while (ip < Statements.Count)
                {
                    context.Check();

                    var node = Statements[ip];

                    if (node is GotoNode gotoNode)
                    {
                        if (!labels.TryGetValue(gotoNode.TargetLabel, out var newIp))
                            throw new Exception($"Label {gotoNode.TargetLabel} not found");
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
                context.Check();
                context.EnterScope();
                try
                {
                    object result = null;
                    foreach (var statement in Statements)
                    {
                        context.Check();
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
        public class UsingNode : AstNode
        {
            public AstNode Declaration { get; }
            public AstNode? Body { get; }

            public UsingNode(AstNode declaration, AstNode? body)
            {
                Declaration = declaration;
                Body = body;
            }

            public override object? Evaluate(ExecutionContext context)
            {
                //
                return null;
            }

            public override void Print(string indent = "", bool l = true) =>
                Console.WriteLine($"{indent}└── using");
        }

        #endregion

        #region Function nodes
        public sealed record AttributeNode(string Name, string[] Args);
        public class FunctionDeclarationNode : Ast.AstNode
        {
            public ValueType? ReturnType; //null is void
            public string Name;
            public string[] Params;
            public ValueType[] ParamTypes;
            public Ast.AstNode Body;
            public IReadOnlyList<AttributeNode> Attributes { get; }
            public FunctionDeclarationNode(ValueType? ret, string name, string[] @params, ValueType[] types, Ast.AstNode body, IList<AttributeNode>? attrs = null)
            { ReturnType = ret; Name = name; Params = @params; ParamTypes = types; Body = body; Attributes = attrs?.ToArray() ?? Array.Empty<AttributeNode>(); }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                context.AddFunction(Name, new ExecutionContext.Function
                {
                    ReturnType = ReturnType ?? ValueType.Object,
                    ParamNames = Params,
                    ParamTypes = ParamTypes,
                    Body = Body,
                    Attributes = this.Attributes.ToArray(),
                });
                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                foreach (var a in Attributes)
                    Console.WriteLine($"{indent}├── [{a.Name}({string.Join(",", a.Args)})]");

                Console.WriteLine($"{indent}└── function {Name}({string.Join(", ", Params)})");
                Body.Print(indent + (isLast ? "    " : "│   "), true);
            }

        }
        public class CallNode : AstNode
        {
            public string Name;
            public AstNode[] Args;
            public CallNode(string name, AstNode[] args) { Name = name; Args = args; }
            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.Check();
                if (ctx.NativeFunctions.TryGetValue(Name, out var overloads))
                {
                    var argVals = Args.Select(a => a.Evaluate(ctx)).ToArray();
                    string? inFuncException = null;
                    foreach (var del in overloads)
                    {
                        var pars = del.Method.GetParameters();
                        if (pars.Length != argVals.Length) continue;
                        ctx.EnterFunction();
                        try
                        {
                            object?[] conv = new object?[argVals.Length];
                            bool ok = true;
                            for (int i = 0; i < pars.Length; i++)
                            {
                                var need = pars[i].ParameterType;
                                var have = argVals[i];
                                if (have is int hid && need != typeof(int))
                                {
                                    var real = ctx.GetObject(hid);
                                    if (real is not null && need.IsInstanceOfType(real))
                                    {
                                        conv[i] = real;
                                        continue;
                                    }
                                }
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

                            object? ret = del.DynamicInvoke(conv);
                            if (ExecutionContext.IsObject(ret))
                            {
                                int id = ctx.AddObject(ret);
                                return (id, isObject: true);
                            }
                            return ret;
                        }
                        catch(Exception e) { inFuncException = e.ToString(); }
                        finally { ctx.ExitFunction();  }
                    }
                    throw new ApplicationException($"No native overload '{Name}' matches given arguments ({string.Join(", ", argVals.Select(x => x.GetType().ToString()))})" +
                        $"{(inFuncException is not null?$"\n{inFuncException}": "")}");
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
                        else if (Ast.MatchVariableType(have, need)) score += 3;
                        else if (need == ValueType.Object) score += 1;
                        else if (have is IConvertible) score += 0;
                        else return -1;
                    }
                    return score;
                }
                var best = candidates.Select(f => (fn: f, score: BestScore(argVals2, f))).Where(t => t.score >= 0)
                    .OrderByDescending(t => t.score).FirstOrDefault();
                var fn = best.fn;
                ctx.EnterFunction();
                ctx.EnterScope();
                try
                {
                    for (int i = 0; i < fn.ParamNames.Length; i++)
                    {
                        var val = Args[i].Evaluate(ctx);
                        if (!MatchVariableType(val, fn.ParamTypes[i]) && fn.ParamTypes[i] != ValueType.Object)
                        {
                            try { val = ctx.Cast(val, fn.ParamTypes[i]); }
                            catch { throw new ApplicationException($"Cannot cast {val?.GetType().Name} to {fn.ParamTypes[i]}"); }
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
                    ctx.ExitScope();
                    if (ret is ReturnSignal rsignal && rsignal.PinKey != -1)
                    {
                        ctx.Unpin(rsignal.PinKey);
                    }
                    return result;
                }
                finally { ctx.ExitFunction(); }
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── call {Name}()");
                string childIndent = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Args.Length; i++)
                {
                    Args[i].Print(childIndent, i == Args.Length - 1);
                }
            }

        }
        public class LambdaNode : AstNode
        {
            public FunctionDeclarationNode Decl { get; }
            public LambdaNode(FunctionDeclarationNode decl) => Decl = decl;

            public override object Evaluate(ExecutionContext ctx)
            {
                Decl.Evaluate(ctx);
                return int.Parse(Decl.Name);
            }

            public override void Print(string ind = "", bool last = true)
            {
                Console.WriteLine($"{ind}└── lambda -> {Decl.Name}");
                Decl.Body.Print(ind + (last ? "    " : "│   "), true);
            }
        }

        #endregion
        #region Structure objects nodes
        public class EnumDeclarationNode : AstNode
        {
            public readonly struct Member
            {
                public Member(string name, AstNode? explicitValue) { Name = name; ExplicitValue = explicitValue; }
                public string Name { get; }
                public AstNode? ExplicitValue { get; }
            }
            public string Name { get; }
            public Member[] Members { get; }
            public string[] Modifiers { get; }
            public EnumDeclarationNode(string name, Member[] members, string[]? modifiers = null)
            {
                Name = name; 
                Members = members;
                Modifiers = modifiers ?? Array.Empty<string>();
            }
            public override object? Evaluate(ExecutionContext ctx)
            {
                int nextAuto = 0;
                foreach (var m in Members)
                {
                    int value = m.ExplicitValue is null ? nextAuto : Convert.ToInt32(m.ExplicitValue.Evaluate(ctx));
                    nextAuto = checked(value + 1);
                    ctx.Declare(m.Name, ValueType.Int, value);
                }

                return Members.ToDictionary(x => x.Name, x =>ctx.ReadVariable(x.Name));

            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── enum {Name}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                foreach (var (m, i) in Members.Select((m, i) => (m, i)))
                    Console.WriteLine($"{childIndent}└── {m}");
            }
        }
        public class StructDeclarationNode : AstNode
        {
            public string Name { get; }
            public string[] Modifiers { get; }
            public AstNode[] Members { get; }
            public StructDeclarationNode(string name, IEnumerable<string> modifiers, IEnumerable<AstNode> members)
            {
                Name = name;
                Modifiers = modifiers.ToArray();
                Members = members.ToArray();
            }
            public override object? Evaluate(ExecutionContext ctx)
            {
                return null;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── struct {string.Join(' ', Modifiers)} {Name}");
                string child = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Members.Length; i++)
                    Members[i].Print(child, i == Members.Length - 1);
            }

        }
        public class ClassDeclarationNode : AstNode
        {
            public string Name { get; }
            public string[] Modifiers { get; }
            public AstNode[] Members { get; }

            public ClassDeclarationNode(string name, IEnumerable<string> mods, IEnumerable<AstNode> members)
            { Name = name; Modifiers = mods.ToArray(); Members = members.ToArray(); }

            public override object? Evaluate(ExecutionContext ctx)
            {
                return null;
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── class {string.Join(' ', Modifiers)} {Name}");
                string ch = indent + (last ? "    " : "│   ");
                for (int i = 0; i < Members.Length; i++)
                    Members[i].Print(ch, i == Members.Length - 1);
            }
        }

        public class InterfaceDeclarationNode : AstNode
        {
            public string Name { get; }
            public string[] Modifiers { get; }
            public AstNode[] Members { get; }

            public InterfaceDeclarationNode(string name, IEnumerable<string> mods, IEnumerable<AstNode> members)
            { Name = name; Modifiers = mods.ToArray(); Members = members.ToArray(); }

            public override object? Evaluate(ExecutionContext ctx) 
            {
                return null;
            }
            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── interface {string.Join(' ', Modifiers)} {Name}");
                string ch = indent + (last ? "    " : "│   ");
                for (int i = 0; i < Members.Length; i++)
                    Members[i].Print(ch, i == Members.Length - 1);
            }
        }

        #endregion
    }
}
