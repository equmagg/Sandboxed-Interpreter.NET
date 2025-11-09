using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Interpretor
{
    public class Parser
    {
        private readonly Lexer _lexer;
        private readonly Stack<Dictionary<string, Ast.AstNode>> _constScopes = new();
        private HashSet<string> _declaredEnums = new();
        private int _anonCounter = 0;
        private string _currentNamespace = "";
        public readonly List<(int pos, ParseException exception)> Errors = new();
        public Parser(string text)
        {
            _lexer = new Lexer(text);
            _lexer.NextToken();
        }
        #region Helpers
        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ParseException MakeParseError(string message)
        {
            return ParseException.Create(message, skipFrames: 1, needFileInfo: true);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Report(ParseException exception) => Errors.Add((_lexer.Position, exception));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Consume(Ast.TokenType type)
        {
            if (_lexer.CurrentTokenType != type)
                throw new ParseException($"Expected {type}, but got {_lexer.CurrentTokenType}");
            _lexer.NextToken();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryConsume(Ast.TokenType type)
        {
            if (_lexer.CurrentTokenType == type) { _lexer.NextToken(); return true; }
            return false;
        }
        private void Synchronize(params Ast.TokenType[] sync)
        {
            int guard = Math.Max(256, _lexer.GetCodeLength() * 4);
            while (_lexer.CurrentTokenType != Ast.TokenType.EndOfInput &&
                   !sync.Contains(_lexer.CurrentTokenType) && --guard > 0)
            {
                _lexer.NextToken();
            }
        }
        private Ast.MissingNode Missing(string expected, string? note = null)
        {
            string got = note == null ? "" : $", but got {note}";
            var ex = MakeParseError($"Expected {expected}{got}");
            Report(ex);
            return new Ast.MissingNode(ex, _lexer.Position);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsTypeKeyword(string tokenText) =>
            tokenText == "var" || Enum.TryParse<Ast.ValueType>(tokenText, ignoreCase: true, out _) || _declaredEnums.Contains(tokenText);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool PeekPointerMark()
            => _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "*";
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsOp(string op) => _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == op;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryConsumeOp(string op)
        {
            if (IsOp(op)) { _lexer.NextToken(); return true; }
            return false;
        }

        private Ast.ValueType ReadMaybePointer(Ast.ValueType baseType)
        {
            if (PeekPointerMark()) { _lexer.NextToken(); return Ast.ValueType.IntPtr; }
            return baseType;
        }
        private Ast.ValueType ReadMaybeNullable(Ast.ValueType vt)
        {
            if (_lexer.CurrentTokenType == Ast.TokenType.Question)
            {
                _lexer.NextToken();//skip ?
                return Ast.ValueType.Nullable;
            }
            return vt;
        }

        private bool IsPostfixUnary(Ast.OperatorToken token, Ast.OperatorInfo info)
            => info.Associativity == Ast.Associativity.Right && (token == Ast.OperatorToken.Increment || token == Ast.OperatorToken.Decrement);

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }
                if (++i == s.Length) break;
                switch (s[i])
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (i + 4 < s.Length && ushort.TryParse(s.Substring(i + 1, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        {
                            sb.Append((char)cp); i += 4; break;
                        }
                        sb.Append('u'); break;
                    case 'U': // \UFFFFFFFF
                        if (i + 8 < s.Length && uint.TryParse(s.Substring(i + 1, 8),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u32))
                        { sb.Append(char.ConvertFromUtf32((int)u32)); i += 8; }
                        else sb.Append('U');
                        break;
                    case 'x':
                        {
                            int start = i + 1;
                            int max = Math.Min(s.Length - start, 4);
                            int count = 0, val = 0;

                            while (count < max)
                            {
                                int c = s[start + count];
                                int hv = -1;
                                if (c >= '0' && c <= '9') hv = c - '0';
                                else if (c >= 'a' && c <= 'f') hv = c - 'a' + 10;
                                else if (c >= 'A' && c <= 'F') hv = c - 'A' + 10;
                                if (hv < 0) break;
                                val = (val << 4) + hv;
                                count++;
                            }
                            if (count == 0) sb.Append('x');
                            else { sb.Append((char)val); i += count; }
                            break;
                        }
                    default: sb.Append(s[i]); break;
                }
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
        private static bool ContainsPatternBinding(Ast.AstNode? node)
        {
            if (node == null) return false;
            var stack = new Stack<Ast.AstNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur is Ast.DeclarationPatternNode) return true;
                foreach (var child in EnumerateChildren(cur))
                    stack.Push(child);
            }
            return false;
        }
        public static IEnumerable<Ast.AstNode> EnumerateChildren(Ast.AstNode n)
        {
            switch (n)
            {
                case Ast.StatementListNode sl: foreach (var s in sl.Statements) yield return s; break;
                case Ast.BlockNode bl: foreach (var s in bl.Statements) yield return s; break;
                case Ast.UsingNode u:
                    yield return u.Declaration;
                    if (u.Body is not null) yield return u.Body;
                    break;
                case Ast.BinOpNode b: yield return b.Left; yield return b.Right; break;
                case Ast.UnaryOpNode u: yield return u.Operand; break;
                case Ast.IfNode ifn: yield return ifn.Condition; yield return ifn.ThenBody; if (ifn.ElseBody != null) yield return ifn.ElseBody; break;
                case Ast.WhileNode wn: yield return wn.Condition; yield return wn.Body; break;
                case Ast.DoWhileNode dwn: yield return dwn.Body; yield return dwn.Condition; break;
                case Ast.ForNode fn: if (fn.Init is not null) yield return fn.Init; if (fn.Condition is not null) yield return fn.Condition; if (fn.Step is not null) yield return fn.Step; yield return fn.Body; break;
                case Ast.ForeachNode fe: yield return fe.Body; yield return fe.CollectionExpr; break;
                case Ast.SwitchNode sw:
                    yield return sw.Discriminant;
                    foreach (var (_, _, body) in sw.Cases) foreach (var s in body) yield return s;
                    break;
                case Ast.SwitchExprNode se:
                    yield return se.Discriminant;
                    foreach (var arm in se.Arms) { yield return arm.pat; yield return arm.expr; }
                    break;
                case Ast.WhenPatternNode wp: yield return wp.Inner; yield return wp.Guard; break;
                case Ast.IsPatternNode ip: yield return ip.Expr; yield return ip.Pattern; break;
                case Ast.BinaryPatternNode bp: yield return bp.Left; yield return bp.Right; break;
                default: yield break;
            }
        }
        #region Generics
        void ConsumeGenericCloserOrThrow(string context)
        {
            if (IsOp(">")) { _lexer.NextToken(); return; }
            if (IsOp(">>"))
            {
                int end = _lexer.Position;
                int start = end - _lexer.CurrentTokenText.Length;
                _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">");
                return;
            }
            if (IsOp(">>>"))
            {
                int end = _lexer.Position;
                int start = end - _lexer.CurrentTokenText.Length;
                _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">>");
                return;
            }
            throw new ParseException($"Expected > while parsing generics ({context}), got '{_lexer.CurrentTokenText}'.");
        }
        string[] ParseTypeParameterListIfAny()
        {
            if (!IsOp("<")) return Array.Empty<string>();
            _lexer.NextToken(); // '<'
            var list = new List<string>();
            if (IsOp(">")) { _lexer.NextToken(); return list.ToArray(); }

            while (true)
            {
                if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                    throw new ParseException($"Expected Identifier in parameter list, got '{_lexer.CurrentTokenText}'.");

                list.Add(_lexer.CurrentTokenText);
                _lexer.NextToken();

                if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); continue; }
                ConsumeGenericCloserOrThrow("parameter type list");
                break;
            }
            return list.ToArray();
        }
        Dictionary<string, string[]>? ParseGenericConstraintsIfAny()
        {
            if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "where"))
                return null;
            int maxIters = Math.Max(256, _lexer.GetCodeLength() * 4);
            int iters = 0;
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "where")
            {
                _lexer.NextToken(); // skip where

                if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                    throw new ApplicationException("Expecter parameter name after 'where'.");

                string tp = _lexer.CurrentTokenText;
                _lexer.NextToken(); // skip name

                if (_lexer.CurrentTokenType != Ast.TokenType.Colon)
                    throw new ApplicationException("Expected ':' after parameter name in 'where'.");

                _lexer.NextToken(); // skip :

                var items = new List<string>();
                var sb = new StringBuilder();
                int depth = 0;
                int pdepth = 0;

                void Flush()
                {
                    var s = sb.ToString().Trim();
                    if (s.Length > 0) items.Add(s);
                    sb.Clear();
                }

                while (true)
                {
                    if (++iters > maxIters)
                        throw new ApplicationException("Constraints argument parsing exceeded sane iteration limit.");
                    if (sb.Length > (_lexer.GetCodeLength() * 2))
                        throw new ApplicationException("Constraints argument text is too large (possible runaway parse).");
                    if (depth == 0 && pdepth == 0)
                    {
                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); Flush(); continue; }
                        if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "where") { Flush(); break; }
                        if (_lexer.CurrentTokenType is Ast.TokenType.BraceOpen or Ast.TokenType.Semicolon) { Flush(); break; }
                        if (IsOp("=>")) { Flush(); break; }
                    }

                    if (IsOp("<")) { depth++; sb.Append('<'); _lexer.NextToken(); continue; }
                    if (IsOp(">") || IsOp(">>") || IsOp(">>>"))
                    {
                        if (depth > 0)
                        {
                            depth--;
                            sb.Append('>');

                            int end = _lexer.Position, start = end - _lexer.CurrentTokenText.Length;
                            if (IsOp(">>>"))
                                _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">>");
                            else if (IsOp(">>"))
                                _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">");

                            continue;
                        }
                        else
                        {
                            Flush();
                            break;
                        }

                    }
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen) { pdepth++; sb.Append('('); _lexer.NextToken(); continue; }
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) { pdepth = Math.Max(0, pdepth - 1); sb.Append(')'); _lexer.NextToken(); continue; }
                    sb.Append(_lexer.CurrentTokenText);
                    if (_lexer.CurrentTokenType is Ast.TokenType.Identifier or Ast.TokenType.Keyword) sb.Append(' ');
                    _lexer.NextToken();
                }

                map[tp] = items;
            }

            return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        }
        string[] ParseTypeArgumentListIfAny()
        {
            if (!IsOp("<")) return Array.Empty<string>();

            _lexer.NextToken(); // skip <
            var args = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            int maxIters = Math.Max(256, _lexer.GetCodeLength() * 4);
            int iters = 0;
            int lastPos = -1;
            string lastTok = "";
            while (true)
            {
                if (++iters > maxIters)
                    throw new ApplicationException("Generic argument parsing exceeded sane iteration limit.");
                if (sb.Length > (_lexer.GetCodeLength() * 2))
                    throw new ApplicationException("Generic argument text is too large (possible runaway parse).");
                if (_lexer.Position == lastPos && _lexer.CurrentTokenText == lastTok)
                    throw new ApplicationException("Stuck while parsing generic arguments (no lexer progress).");
                lastPos = _lexer.Position; lastTok = _lexer.CurrentTokenText;
                if (depth == 0 && _lexer.CurrentTokenType is not Ast.TokenType.Identifier and not Ast.TokenType.Keyword
                    and not Ast.TokenType.Comma and not Ast.TokenType.Operator)
                {
                    throw new ApplicationException($"Unexpected token '{_lexer.CurrentTokenText}' in type argument list.");
                }
                if (IsOp("<")) { depth++; sb.Append('<'); _lexer.NextToken(); continue; }

                if (IsOp(">") || IsOp(">>") || IsOp(">>>"))
                {
                    if (depth == 0)
                    {
                        var part = sb.ToString().Trim();
                        if (part.Length == 0) throw new ApplicationException("Argument cannot be empty.");
                        args.Add(part);
                        int end = _lexer.Position, start = end - _lexer.CurrentTokenText.Length;
                        if (IsOp(">>>"))
                            _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">>");
                        else if (IsOp(">>"))
                            _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">");
                        else
                            _lexer.NextToken();

                        break;
                    }
                    else
                    {
                        depth--;
                        if (depth < 0) throw new ApplicationException("Negative generic depth.");
                        sb.Append('>');

                        int end = _lexer.Position, start = end - _lexer.CurrentTokenText.Length;
                        if (IsOp(">>>"))
                            _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">>");
                        else if (IsOp(">>"))
                            _lexer.ResetCurrent(start + 1, Ast.TokenType.Operator, ">");

                        continue;
                    }
                }

                if (_lexer.CurrentTokenType == Ast.TokenType.Comma && depth == 0)
                {
                    var part = sb.ToString().Trim();
                    if (part.Length == 0)
                        throw new ApplicationException("Type argument cannot be empty.");
                    args.Add(part);
                    sb.Clear();
                    _lexer.NextToken();
                    continue;
                }

                sb.Append(_lexer.CurrentTokenText);
                if (_lexer.CurrentTokenType is Ast.TokenType.Identifier or Ast.TokenType.Keyword) sb.Append(' ');
                _lexer.NextToken();
            }

            return args.ToArray();
        }
        bool SkipTypeArgsIfAny() => ParseTypeArgumentListIfAny().Length > 0;
        #endregion
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
                statements.Add(SoftParseStatement());
            var hoisted = statements
              .Where(s => s is Ast.FunctionDeclarationNode or Ast.EnumDeclarationNode or Ast.StructDeclarationNode
              or Ast.InterfaceDeclarationNode or Ast.ClassDeclarationNode)
              .Concat(statements.Where(s => s is not Ast.FunctionDeclarationNode && s is not Ast.EnumDeclarationNode && s is not Ast.StructDeclarationNode
              && s is not Ast.InterfaceDeclarationNode && s is not Ast.ClassDeclarationNode)).ToList();
            return new Ast.StatementListNode(hoisted);
        }
        private Ast.AstNode ParseBlock(bool forceScope = false)
        {
            Consume(Ast.TokenType.BraceOpen); //{
            _constScopes.Push(new Dictionary<string, Ast.AstNode>());
            var statements = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose && _lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
                statements.Add(SoftParseStatement());
            _constScopes.Pop();
            Consume(Ast.TokenType.BraceClose); //}
            var hoisted = statements
              .Where(s => s is Ast.FunctionDeclarationNode)
              .Concat(statements.Where(s => s is not Ast.FunctionDeclarationNode))
              .ToList();
            return new Ast.BlockNode(hoisted, forceScope);
        }
        #endregion
        #region Statemet parsers
        private Ast.AstNode SoftParseStatement()
        {
            int start = _lexer.Position;
            try
            {
                return ParseStatement();
            }
            catch (ParseException ex)
            {
                var miss = new Ast.MissingNode(ex, start);
                Synchronize(Ast.TokenType.Semicolon, Ast.TokenType.BraceClose);
                if (TryConsume(Ast.TokenType.Semicolon)) { }
                return miss;
            }

        }
        private Ast.AstNode ParseStatement()
        {
            if (_lexer.CurrentTokenType == Ast.TokenType.Semicolon)
            {
                _lexer.NextToken(); return new Ast.EmptyNode();
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && LooksLikeTupleDeconstructionDeclaration())
                return ParseTupleDeconstructionDeclaration();
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "var")
            {
                int savePos = _lexer.Position;
                var saveType = _lexer.CurrentTokenType;
                var saveText = _lexer.CurrentTokenText;

                _lexer.NextToken(); //skip var
                bool isVarTuple = LooksLikeVarTupleDeconstructionDeclaration();
                _lexer.ResetCurrent(savePos, saveType, saveText);

                if (isVarTuple)
                    return ParseVarTupleDeconstructionDeclaration();
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
                    int savePos = _lexer.Position;
                    var saveType = _lexer.CurrentTokenType;
                    var saveText = _lexer.CurrentTokenText;
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && TryConsumeTupleReturnSignature())
                    {
                        if (PeekPointerMark())
                            _lexer.NextToken();
                        if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                            ReadArraySuffixes();
                        int savePos2 = _lexer.Position;
                        var saveType2 = _lexer.CurrentTokenType;
                        var saveText2 = _lexer.CurrentTokenText;
                        bool looksLikeFunc = _lexer.CurrentTokenType == Ast.TokenType.Identifier;
                        _lexer.NextToken();//skip name
                        looksLikeFunc = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen
                            || (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<");
                        if (looksLikeFunc)
                        {
                            _lexer.ResetCurrent(savePos2, saveType2, saveText2);
                            return ParseFunctionDeclaration(isVoid: false, returnTypeText: "tuple", fnMods: mods);
                        }
                        else { _lexer.ResetCurrent(savePos, saveType, saveText); return ParseDeclaration(mods); }
                    }
                    if (isVoid || IsTypeKeyword(_lexer.CurrentTokenText) || _lexer.CurrentTokenType == Ast.TokenType.Identifier)
                    {
                        _lexer.NextToken();//skip type
                        if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                            SkipTypeArgsIfAny();
                        bool looksLikeFunc = _lexer.CurrentTokenType == Ast.TokenType.Identifier;
                        _lexer.NextToken();//skip name
                        looksLikeFunc = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen
                            || (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<");
                        _lexer.ResetCurrent(savePos, saveType, saveText);
                        if (looksLikeFunc) { _lexer.NextToken(); return ParseFunctionDeclaration(isVoid, retType, fnMods: mods); }
                        else if (LooksLikeStructureDeclaration())
                        {
                            string structTypeName = _lexer.CurrentTokenText;
                            _lexer.NextToken();//skip type
                            var leftTypeArgs = ParseTypeArgumentListIfAny();
                            string name = _lexer.CurrentTokenText;
                            Consume(Ast.TokenType.Identifier);
                            Consume(Ast.TokenType.Operator); // expect =
                            Ast.AstNode expr;
                            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "new")
                            {
                                int p = _lexer.Position; var t = _lexer.CurrentTokenType; var s = _lexer.CurrentTokenText;
                                _lexer.NextToken();
                                bool target = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen;
                                _lexer.ResetCurrent(p, t, s);

                                expr = target ? ParseTargetTypedNew(structTypeName, leftTypeArgs) : ParseExpression();
                            }
                            else expr = ParseExpression();
                            Consume(Ast.TokenType.Semicolon);
                            bool isDict = string.Equals(structTypeName, "Dictionary", StringComparison.Ordinal);
                            if (isDict && expr is Ast.CollectionExpressionNode coll)
                            {
                                if (coll.Items.Length == 0)
                                {
                                    expr = new Ast.NewDictionaryNode(
                                        new Ast.GenericUse(leftTypeArgs),
                                        Array.Empty<Ast.AstNode>(),
                                        Array.Empty<(Ast.AstNode Key, Ast.AstNode Value)>()
                                    );
                                }
                                else if (coll.IsDictionaryExpr)
                                {
                                    if (leftTypeArgs == null || leftTypeArgs.Length != 2)
                                        throw new ParseException("Dictionary requires exactly 2 generic type arguments on the left side.");
                                    if ((coll.Items.Length & 1) != 0)
                                        throw new ParseException("Dictionary collection expression must contain key:value pairs.");

                                    var pairs = new List<(Ast.AstNode Key, Ast.AstNode Value)>(coll.Items.Length / 2);
                                    for (int i = 0; i < coll.Items.Length; i += 2)
                                        pairs.Add((coll.Items[i], coll.Items[i + 1]));
                                    expr = new Ast.NewDictionaryNode(
                                    new Ast.GenericUse(leftTypeArgs),
                                    Array.Empty<Ast.AstNode>(),
                                    pairs.ToArray()
                            );
                                }
                            }
                            bool isPublic = mods is not null && mods.Contains("public");
                            if (mods != null)
                                name = string.IsNullOrEmpty(_currentNamespace) ? name : $"{_currentNamespace}.{name}";
                            var declType = isDict ? Ast.ValueType.Dictionary : Ast.ValueType.Struct;
                            return new Ast.VariableDeclarationNode(declType, name, expr, isArray: false, isConst: false, isPublic: false, innerType: null)
                            {
                                CustomTypeName = structTypeName,
                                Generic = leftTypeArgs.Length == 0 ? null : new Ast.GenericUse(leftTypeArgs)
                            };
                        }
                        else return ParseDeclaration(mods);
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
                    case "global": _lexer.NextToken(); return ParseUsing();
                    case "using": return ParseUsing();
                    case "const": return ParseConstDeclaration();
                    case "enum": return ParseEnumDeclaration();
                    case "class": return ParseClassDeclaration();
                    case "struct": return ParseStructDeclaration();
                    case "interface": return ParseInterfaceDeclaration();
                    case "namespace": return ParseNamespaceDeclaration();
                }
            }
            //attribute?
            if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
            {
                var attrs = ParseAttributeList();
                //expect function delaration
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && TryConsumeTupleReturnSignature())
                {
                    if (PeekPointerMark())
                        _lexer.NextToken();

                    if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                        ReadArraySuffixes();
                    return (Ast.FunctionDeclarationNode)ParseFunctionDeclaration(isVoid: false, returnTypeText: "tuple", attrs: attrs);
                }
                if (_lexer.CurrentTokenText == "void" || IsTypeKeyword(_lexer.CurrentTokenText))
                {
                    bool isVoid = _lexer.CurrentTokenText == "void";
                    string? retType = isVoid ? null : _lexer.CurrentTokenText;
                    _lexer.NextToken();//skip type
                    if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                        SkipTypeArgsIfAny();

                    if (PeekPointerMark())
                        _lexer.NextToken();

                    if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                        ReadArraySuffixes();
                    return (Ast.FunctionDeclarationNode)ParseFunctionDeclaration(isVoid, retType, attrs: attrs);
                }

                throw new ParseException("Attributes are only allowed before function declaration");
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
            {
                if (LooksLikeStructArrayDeclaration())
                {
                    _lexer.NextToken(); //skip type name
                    var arrInfo = ReadArraySuffixes();
                    string name = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);

                    Ast.AstNode init = new Ast.LiteralNode(null);
                    if (_lexer.CurrentTokenText == "=")
                    {
                        _lexer.NextToken();//skip =
                        init = ParseExpression();
                    }
                    Consume(Ast.TokenType.Semicolon);

                    return new Ast.VariableDeclarationNode(
                        Ast.ValueType.Struct,
                        name,
                        init,
                        isArray: true,
                        arrayLength: arrInfo.dims);
                }
                if (LooksLikeStructureDeclaration())
                {
                    string structTypeName = _lexer.CurrentTokenText;
                    _lexer.NextToken();//skip type
                    var leftTypeArgs = ParseTypeArgumentListIfAny();
                    string name = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    Consume(Ast.TokenType.Operator);//expect =
                    Ast.AstNode expr;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "new")
                    {
                        int p = _lexer.Position; var t = _lexer.CurrentTokenType; var s = _lexer.CurrentTokenText;
                        _lexer.NextToken();
                        bool target = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen;
                        _lexer.ResetCurrent(p, t, s);

                        expr = target ? ParseTargetTypedNew(structTypeName, leftTypeArgs) : ParseExpression();
                    }
                    else expr = ParseExpression();
                    Consume(Ast.TokenType.Semicolon);
                    bool isDict = string.Equals(structTypeName, "Dictionary", StringComparison.Ordinal);
                    if (isDict && expr is Ast.CollectionExpressionNode coll)
                    {
                        if (coll.Items.Length == 0)
                        {
                            expr = new Ast.NewDictionaryNode(
                                new Ast.GenericUse(leftTypeArgs),
                                Array.Empty<Ast.AstNode>(),
                                Array.Empty<(Ast.AstNode Key, Ast.AstNode Value)>()
                            );
                        }
                        else if (coll.IsDictionaryExpr)
                        {
                            if (leftTypeArgs == null || leftTypeArgs.Length != 2)
                                throw new ParseException("Dictionary requires exactly 2 generic type arguments on the left side.");
                            if ((coll.Items.Length & 1) != 0)
                                throw new ParseException("Dictionary collection expression must contain key:value pairs.");

                            var pairs = new List<(Ast.AstNode Key, Ast.AstNode Value)>(coll.Items.Length / 2);
                            for (int i = 0; i < coll.Items.Length; i += 2)
                                pairs.Add((coll.Items[i], coll.Items[i + 1]));
                            expr = new Ast.NewDictionaryNode(
                            new Ast.GenericUse(leftTypeArgs),
                            Array.Empty<Ast.AstNode>(),
                            pairs.ToArray()
                    );
                        }
                    }
                    var declType = isDict ? Ast.ValueType.Dictionary : Ast.ValueType.Struct;
                    return new Ast.VariableDeclarationNode(declType, name, expr, isArray: false, isConst: false, isPublic: false, innerType: null)
                    {
                        CustomTypeName = structTypeName,
                        Generic = leftTypeArgs.Length == 0 ? null : new Ast.GenericUse(leftTypeArgs)
                    };
                }
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && TryConsumeTupleReturnSignature())
            {
                return ParseFunctionDeclaration(isVoid: false, returnTypeText: "tuple");
            }
            //expect declaration
            if (IsTypeKeyword(_lexer.CurrentTokenText) && !int.TryParse(_lexer.CurrentTokenText, out _))
            {
                //look ahead: function?
                string saveType = _lexer.CurrentTokenText;
                int pos = _lexer.Position;
                Ast.TokenType saveToken = _lexer.CurrentTokenType;
                _lexer.NextToken();

                if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                    SkipTypeArgsIfAny();

                if (PeekPointerMark())
                    _lexer.NextToken();

                if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                    ReadArraySuffixes();

                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                {
                    var ident = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen
                        || (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<"))
                    {
                        return ParseFunctionDeclaration(false, saveType, ident);
                    }
                }
                _lexer.ResetCurrent(pos, saveToken, saveType);
                return ParseDeclaration();
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                return ParseBlock();
            var exprStatement = ParseExpression();
            Consume(Ast.TokenType.Semicolon);
            return exprStatement;
        }
        #region Struct helpers
        private bool LooksLikeVarTupleDeconstructionDeclaration()
        {
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenOpen)
                return false;

            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            _lexer.NextToken(); //skip (
            bool sawAny = false;
            while (true)
            {
                if (_lexer.CurrentTokenType != Ast.TokenType.Identifier) { sawAny = false; break; }
                sawAny = true;
                _lexer.NextToken(); //skip name

                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                {
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                    continue;
                }
                break;
            }
            if (!sawAny || _lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }

            _lexer.NextToken(); //skip )
            bool ok = _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=";

            _lexer.ResetCurrent(savePos, saveType, saveText);
            return ok;
        }
        private bool LooksLikeTupleDeconstructionDeclaration()
        {
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenOpen) return false;
            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            _lexer.NextToken(); //skip (
            bool sawAny = false;
            while (true)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier && _lexer.CurrentTokenText == "_")
                {
                    sawAny = true;
                    _lexer.NextToken(); // skip _
                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                        continue;
                    }
                    break;
                }
                if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText) && _lexer.CurrentTokenText != "var"))
                { sawAny = false; break; }
                _lexer.NextToken(); //skip type
                if (PeekPointerMark()) _lexer.NextToken();
                if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen) ReadArraySuffixes();
                if (_lexer.CurrentTokenType == Ast.TokenType.Question) _lexer.NextToken();//skip ?
                if (_lexer.CurrentTokenType != Ast.TokenType.Identifier) { sawAny = false; break; }
                _lexer.NextToken(); //skip name
                sawAny = true;

                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                {
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                    continue;
                }
                break;
            }
            if (!sawAny || _lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
            _lexer.NextToken(); //skip )
            bool ok = _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=";
            _lexer.ResetCurrent(savePos, saveType, saveText);
            return ok;
        }
        private bool TryConsumeTupleReturnSignature()
        {
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenOpen) return false;

            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;
            _lexer.NextToken(); //skip (
            while (true)
            {
                if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText) && _lexer.CurrentTokenText != "var"))
                {
                    _lexer.ResetCurrent(savePos, saveType, saveText);
                    return false;
                }
                _lexer.NextToken(); //skip type
                if (PeekPointerMark()) _lexer.NextToken(); //skip *
                if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen) ReadArraySuffixes();
                if (_lexer.CurrentTokenType == Ast.TokenType.Question) _lexer.NextToken(); //skip ?
                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                    _lexer.NextToken();
                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                {
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                    continue;
                }
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;

                _lexer.ResetCurrent(savePos, saveType, saveText);
                return false;
            }
            _lexer.NextToken(); //skip )

            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }

            return true;
        }
        private bool LooksLikeConstructor(string structName)
        {
            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            while (IsStructModifier())
                _lexer.NextToken();

            if (!(_lexer.CurrentTokenType == Ast.TokenType.Identifier && _lexer.CurrentTokenText == structName))
            {
                _lexer.ResetCurrent(savePos, saveType, saveText);
                return false;
            }
            _lexer.NextToken();//skip struct name

            bool looks = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen;
            _lexer.ResetCurrent(savePos, saveType, saveText);
            return looks;
        }
        private bool LooksLikeStructureDeclaration(List<string>? mods = null)
        {
            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                return false;

            _lexer.NextToken();//skip type
            bool hadGenerics = SkipTypeArgsIfAny();
            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
            {
                _lexer.ResetCurrent(savePos, saveType, saveText);
                return false;
            }

            string varName = _lexer.CurrentTokenText;
            _lexer.NextToken();//skip name

            if (_lexer.CurrentTokenText != "=")
            {
                _lexer.ResetCurrent(savePos, saveType, saveText);
                return false;
            }
            _lexer.NextToken();//skip =
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "new")
            {
                _lexer.NextToken();//skip new
                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                {
                    if (_lexer.CurrentTokenText != saveText)
                    { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
                    _lexer.NextToken(); //skip struct name
                    SkipTypeArgsIfAny();
                    bool ok = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen
                           || _lexer.CurrentTokenType == Ast.TokenType.BraceOpen;
                    _lexer.ResetCurrent(savePos, saveType, saveText);
                    return ok;
                }
                bool looksTargetTyped = _lexer.CurrentTokenType == Ast.TokenType.ParenOpen;
                _lexer.ResetCurrent(savePos, saveType, saveText);
                return looksTargetTyped;
            }
            bool looksCollectionExpr = hadGenerics && _lexer.CurrentTokenType == Ast.TokenType.BracketsOpen;
            _lexer.ResetCurrent(savePos, saveType, saveText);
            return looksCollectionExpr;
        }
        private bool LooksLikeStructArrayDeclaration()
        {
            int savePos = _lexer.Position;
            var saveType = _lexer.CurrentTokenType;
            var saveText = _lexer.CurrentTokenText;

            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                return false;
            if (saveText == "Struct")
            {
                _lexer.NextToken(); //skip type name
                SkipTypeArgsIfAny();
                bool looksOk = _lexer.CurrentTokenType == Ast.TokenType.BracketsOpen;
                _lexer.ResetCurrent(savePos, saveType, saveText); return looksOk;
            }
            _lexer.NextToken(); //skip type name
            SkipTypeArgsIfAny();
            if (_lexer.CurrentTokenType != Ast.TokenType.BracketsOpen)
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }

            do
            {
                _lexer.NextToken(); //[

                //fixed size
                do
                {
                    if (_lexer.CurrentTokenType == Ast.TokenType.Number)
                    {
                        _lexer.NextToken();
                    }

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        continue;
                    }
                    break;
                } while (true);

                if (_lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
                _lexer.NextToken(); //]

            } while (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen);


            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
            _lexer.NextToken();//variable name
            if ((_lexer.CurrentTokenText != "="))
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
            _lexer.NextToken();//skip =
            if ((_lexer.CurrentTokenType != Ast.TokenType.BraceOpen))
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
            _lexer.NextToken();//skip {
            if ((_lexer.CurrentTokenText != "new"))
            { _lexer.ResetCurrent(savePos, saveType, saveText); return false; }
            _lexer.NextToken();//skip new
            bool ok = (_lexer.CurrentTokenText == saveText);
            _lexer.ResetCurrent(savePos, saveType, saveText);
            return ok;
        }
        private Ast.AstNode ParseTargetTypedNew(string structName, string[]? leftTypeArgs = null)
        {
            if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "new"))
                throw new ParseException("Expected 'new'");

            _lexer.NextToken(); //skip new
            Consume(Ast.TokenType.ParenOpen);

            var args = new List<Ast.AstNode>();
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                do
                {
                    args.Add(ParseExpression());
                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); continue; }
                    break;
                } while (true);
            }
            Consume(Ast.TokenType.ParenClose);
            if (string.Equals(structName, "Dictionary", StringComparison.Ordinal))
            {
                if (leftTypeArgs == null || leftTypeArgs.Length != 2)
                    throw new ParseException("Dictionary requires exactly 2 generic type arguments on the left side.");

                (Ast.AstNode Key, Ast.AstNode Value)[]? init = null;
                if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                    init = ParseDictionaryInitializer();

                return new Ast.NewDictionaryNode(
                    new Ast.GenericUse(leftTypeArgs),
                    args.ToArray(),
                    init
                );
            }
            (string Name, Ast.AstNode Expr)[]? inits = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                inits = ParseObjectInitializer();
            return new Ast.NewStructNode(structName, args.ToArray()) { Initializers = inits };
        }
        #endregion
        private Ast.AstNode ParseDeclaration(List<string>? mods = null)
        {
            bool IsPublic = mods is null ? false : mods.Contains("public");
            var typeStr = _lexer.CurrentTokenText;
            bool isVar = typeStr == "var";
            _lexer.NextToken();
            var baseType = isVar ? Ast.ValueType.Object : Enum.Parse<Ast.ValueType>(typeStr, true);
            var declarations = new List<Ast.AstNode>();
            var innerType = baseType;
            while (true)
            {

                (bool isArr, int?[] dims) arrayInfo = ReadArraySuffixes();
                if (!isVar)
                {
                    baseType = ReadMaybePointer(baseType);
                    baseType = ReadMaybeNullable(baseType);
                }
                var name = _lexer.CurrentTokenText;
                if (mods != null)
                {
                    name = string.IsNullOrEmpty(_currentNamespace) ? name : $"{_currentNamespace}.{name}";
                }

                Consume(Ast.TokenType.Identifier);
                Ast.AstNode expr = new Ast.LiteralNode(null);

                if (_lexer.CurrentTokenText == "=")
                {
                    _lexer.NextToken();
                    expr = ParseExpression();
                }
                else if (isVar)
                    throw new ParseException("Variable declared with 'var' must have an initializer");
                bool inferArrayForVar = false;
                Ast.ValueType inferredType = baseType;
                if (isVar)
                {
                    switch (expr)
                    {
                        case Ast.NewArrayNode na:
                            inferredType = na.ElementType;
                            inferArrayForVar = true;
                            break;
                        case Ast.ArrayLiteralNode al:
                            inferredType = al.ElementType;
                            inferArrayForVar = true;
                            break;
                    }
                }
                Ast.AstNode node = ((!isVar && arrayInfo.isArr) || (isVar && inferArrayForVar))
                    ? new Ast.VariableDeclarationNode(isVar ? inferredType : baseType, name, expr, isArray: true, arrayInfo.dims, isPublic: IsPublic, innerType: null)
                    : new Ast.VariableDeclarationNode(baseType, name, expr, isPublic: IsPublic, innerType: innerType != baseType ? innerType : null);
                declarations.Add(node);

                if (_lexer.CurrentTokenType != Ast.TokenType.Comma)
                    break;

                _lexer.NextToken();
            }

            Consume(Ast.TokenType.Semicolon);

            return declarations.Count == 1 ? declarations[0] : new Ast.StatementListNode(declarations);

        }
        private Ast.AstNode ParseConstDeclaration()
        {
            _lexer.NextToken(); //skip const
            if (!IsTypeKeyword(_lexer.CurrentTokenText))
                throw new ParseException("Type expected after const");

            var typeTok = _lexer.CurrentTokenText;
            _lexer.NextToken(); //skip type

            var name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);

            if (_lexer.CurrentTokenText != "=")
                throw new ParseException("Const declaration must have an initializer");

            _lexer.NextToken(); //skip =
            Ast.AstNode init = ParseExpression();
            Consume(Ast.TokenType.Semicolon);

            if (init is Ast.LiteralNode lit)
                _constScopes.Peek()[name] = lit;
            else
                return new Ast.VariableDeclarationNode(Enum.Parse<Ast.ValueType>(typeTok, true),
                    name, init, isArray: false, isConst: true, isPublic: false, innerType: null);

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
        private Ast.AstNode ParseNamespaceDeclaration()
        {
            _lexer.NextToken();

            var sb = new StringBuilder();
            sb.Append(_lexer.CurrentTokenText);
            Consume(Ast.TokenType.Identifier);
            while (_lexer.CurrentTokenText == ".")
            {
                _lexer.NextToken();
                sb.Append('.').Append(_lexer.CurrentTokenText);
                Consume(Ast.TokenType.Identifier);
            }
            string fullName = sb.ToString();

            Consume(Ast.TokenType.BraceOpen);

            string saved = _currentNamespace;
            _currentNamespace = string.IsNullOrEmpty(saved)
                                  ? fullName
                                  : $"{saved}.{fullName}";

            var members = new List<Ast.AstNode>();
            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
                members.Add(ParseStatement());
            Consume(Ast.TokenType.BraceClose);

            _currentNamespace = saved;

            return new Ast.NamespaceDeclarationNode(fullName, members);
        }

        #endregion
        #region Expression parser 
        private Ast.AstNode ParseExpression(int parentPrecedence = 0)
        {
            Ast.AstNode left = ParseUnary();
            while (true)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Keyword)
                {
                    if (_lexer.CurrentTokenText == "as")
                    {
                        _lexer.NextToken();//skip as
                        if (!IsTypeKeyword(_lexer.CurrentTokenText))
                            throw new ParseException("Type expected after 'as'");
                        string typeStr = _lexer.CurrentTokenText;
                        _lexer.NextToken();
                        var baseType = Enum.Parse<Ast.ValueType>(typeStr, true);
                        baseType = ReadMaybePointer(baseType);
                        baseType = ReadMaybeNullable(baseType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) baseType = Ast.ValueType.Array;
                        left = new Ast.AsNode(baseType, left);
                        continue;
                    }
                    if (_lexer.CurrentTokenText == "is")
                    {
                        _lexer.NextToken();//skip is
                        var pattern = ParsePattern();
                        left = new Ast.IsPatternNode(left, pattern);
                        continue;
                    }
                    if (_lexer.CurrentTokenText == "switch")
                    {
                        _lexer.NextToken();//skip switch
                        Consume(Ast.TokenType.BraceOpen);
                        var arms = new List<(Ast.PatternNode, Ast.AstNode)>();
                        while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
                        {
                            var pattern = ParsePattern();
                            Consume(Ast.TokenType.Operator); //expect =>
                            arms.Add((pattern, ParseExpression()));
                            if (_lexer.CurrentTokenType == Ast.TokenType.Comma) _lexer.NextToken();
                        }
                        Consume(Ast.TokenType.BraceClose);
                        left = new Ast.SwitchExprNode(left, arms);
                        continue;
                    }
                }
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
                if (opToken == Ast.OperatorToken.AddressOf)
                    opToken = Ast.OperatorToken.BitAnd;
                if (!Ast.TryGetOperatorInfo(opToken, out var opInfo)) break;
                if (opInfo!.Precedence < parentPrecedence) break;
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
                int savePos = _lexer.Position;
                var saveType = _lexer.CurrentTokenType;
                var saveText = _lexer.CurrentTokenText;

                bool looksLikeLambda = false;
                bool looksLikeCast = false;

                _lexer.NextToken(); //skip (
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                {
                    _lexer.NextToken();
                    looksLikeLambda = _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>";
                }
                else
                {
                    bool ok = true;
                    do
                    {
                        if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                        {
                            _lexer.NextToken(); //skip type
                            if (PeekPointerMark()) _lexer.NextToken();
                            //var arrInfo = ReadArraySuffixes();
                            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier) { ok = false; break; }
                            _lexer.NextToken(); //skip name
                        }
                        else if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                        {
                            _lexer.NextToken();
                        }
                        else
                        {
                            ok = false; break;
                        }

                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                        {
                            _lexer.NextToken();
                            continue;
                        }
                        break;
                    } while (true);

                    if (ok && _lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                    {
                        _lexer.NextToken(); // skip )
                        looksLikeLambda = _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>";
                    }
                    else if (!ok)
                    {
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose)
                        {
                            _lexer.NextToken();
                            looksLikeCast = _lexer.CurrentTokenType != Ast.TokenType.Operator || _lexer.CurrentTokenText != "=>";
                        }
                    }

                }
                _lexer.ResetCurrent(savePos, saveType, saveText);

                if (looksLikeLambda)
                    return ParseLambda();

                if (looksLikeCast)
                {
                    _lexer.NextToken(); //skip (
                    var vt = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                    _lexer.NextToken(); //skip type
                    vt = ReadMaybePointer(vt);
                    vt = ReadMaybeNullable(vt);
                    Consume(Ast.TokenType.ParenClose);
                    return new Ast.CastNode(vt, ParseUnary());
                }

                _lexer.NextToken(); //skip (
                var items = new List<(string? name, Ast.AstNode expr)>();
                bool hasComma = false;
                bool hasNamed = false;

                if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                {
                    while (true)
                    {
                        string? fieldName = null;
                        if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                        {
                            int savePos2 = _lexer.Position;
                            var saveType2 = _lexer.CurrentTokenType;
                            var saveText2 = _lexer.CurrentTokenText;

                            string candidate = _lexer.CurrentTokenText;
                            _lexer.NextToken();

                            if (_lexer.CurrentTokenType == Ast.TokenType.Colon)
                            {
                                hasNamed = true;
                                _lexer.NextToken(); // skip :
                                fieldName = candidate;
                                var valueExpr = ParseExpression();
                                items.Add((fieldName, valueExpr));
                            }
                            else
                            {
                                _lexer.ResetCurrent(savePos2, saveType2, saveText2);
                                items.Add((null, ParseExpression()));
                            }
                        }
                        else
                        {
                            items.Add((null, ParseExpression()));
                        }

                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                        {
                            hasComma = true;
                            _lexer.NextToken();
                            if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                            continue;
                        }
                        break;
                    }
                }
                Consume(Ast.TokenType.ParenClose);
                if (hasComma || items.Count > 1 || hasNamed)
                    return new Ast.TupleLiteralNode(items.ToArray());
                return items.Count == 0 ? new Ast.TupleLiteralNode(Array.Empty<(string?, Ast.AstNode)>()) : items[0].expr;
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
                        if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                        {
                            string structName = _lexer.CurrentTokenText;
                            _lexer.NextToken();
                            var typeArgs = ParseTypeArgumentListIfAny();
                            if (string.Equals(structName, "Dictionary", StringComparison.Ordinal))
                            {
                                if (typeArgs.Length != 2)
                                    throw new ParseException("Dictionary requires exactly 2 generic type arguments: Dictionary<TKey, TValue>");
                                var optionalArgs = new List<Ast.AstNode>();
                                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                                {
                                    _lexer.NextToken(); //skip (
                                    if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                                    {
                                        while (true)
                                        {
                                            optionalArgs.Add(ParseExpression());
                                            if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                            {
                                                _lexer.NextToken();
                                                if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                                                continue;
                                            }
                                            break;
                                        }
                                    }
                                    Consume(Ast.TokenType.ParenClose);
                                }
                                (Ast.AstNode Key, Ast.AstNode Value)[]? init = null;
                                if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                                    init = ParseDictionaryInitializer();
                                return new Ast.NewDictionaryNode(new Ast.GenericUse(typeArgs), optionalArgs.ToArray(), init);
                            }
                            if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                            {
                                var arrSizes = new List<Ast.AstNode>();
                                int arrRank = 0;
                                while (true)
                                {
                                    arrRank++;
                                    Consume(Ast.TokenType.BracketsOpen);
                                    if (_lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                                        arrSizes.Add(ParseExpression());
                                    Consume(Ast.TokenType.BracketsClose);
                                    if (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen) continue;
                                    break;
                                }

                                if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                                {
                                    var lit = (Ast.ArrayLiteralNode)ParsePrimary();
                                    lit.ElementType = Ast.ValueType.Struct;
                                    return lit;
                                }

                                return new Ast.NewArrayNode(arrRank > 1 ? Ast.ValueType.Array : Ast.ValueType.Struct, len: arrSizes.ToArray());
                            }
                            var args = new List<Ast.AstNode>();
                            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                            {
                                _lexer.NextToken();//skip (
                                if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                                {
                                    while (true)
                                    {
                                        args.Add(ParseExpression());
                                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                        {
                                            _lexer.NextToken();
                                            if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                                            continue;
                                        }
                                        break;
                                    }
                                }
                                Consume(Ast.TokenType.ParenClose);
                            }
                            (string Name, Ast.AstNode Expr)[]? inits = null;
                            if (_lexer.CurrentTokenType == Ast.TokenType.BraceOpen)
                                inits = ParseObjectInitializer();
                            return new Ast.NewStructNode(structName, args.ToArray())
                            {
                                Generic = typeArgs.Length == 0 ? null : new Ast.GenericUse(typeArgs),
                                Initializers = inits
                            };
                        }
                        if (!IsTypeKeyword(_lexer.CurrentTokenText))
                            throw new ParseException("Expected type after 'new'");

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
                    if (_lexer.CurrentTokenText == "stackalloc")
                    {
                        _lexer.NextToken(); // skip stackalloc

                        if (!IsTypeKeyword(_lexer.CurrentTokenText) || _lexer.CurrentTokenText == "var")
                            throw new ParseException("Element type expected after 'stackalloc'");

                        var elemType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); // skip type

                        elemType = ReadMaybePointer(elemType);
                        elemType = ReadMaybeNullable(elemType);

                        Consume(Ast.TokenType.BracketsOpen);
                        var lenExpr = ParseExpression();
                        Consume(Ast.TokenType.BracketsClose);

                        return new Ast.StackallocNode(elemType, lenExpr);
                    }
                    if (_lexer.CurrentTokenText == "nameof")
                    {
                        _lexer.NextToken();
                        Consume(Ast.TokenType.ParenOpen);
                        string last = "";
                        if (_lexer.CurrentTokenType is Ast.TokenType.Identifier or Ast.TokenType.Keyword)
                        {
                            last = _lexer.CurrentTokenText;
                            _lexer.NextToken();
                            while (_lexer.CurrentTokenType == Ast.TokenType.Dot)
                            {
                                _lexer.NextToken(); // skip .
                                if (_lexer.CurrentTokenType is Ast.TokenType.Identifier or Ast.TokenType.Keyword)
                                {
                                    last = _lexer.CurrentTokenText;
                                    _lexer.NextToken();
                                }
                                else throw new ParseException("Identifier expected after '.' in nameof(...)");
                            }
                        }
                        Consume(Ast.TokenType.ParenClose);
                        return new Ast.LiteralNode(last);
                    }
                    if (_lexer.CurrentTokenText == "default")
                    {
                        _lexer.NextToken();
                        if (_lexer.CurrentTokenType != Ast.TokenType.ParenOpen)
                            return new Ast.LiteralNode(null);
                        Consume(Ast.TokenType.ParenOpen);
                        if (!IsTypeKeyword(_lexer.CurrentTokenText))
                            throw new ParseException("Type expected in default(<type>)");
                        var vt = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken();
                        vt = ReadMaybePointer(vt);
                        vt = ReadMaybeNullable(vt);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) vt = Ast.ValueType.Array;
                        Consume(Ast.TokenType.ParenClose);
                        return new Ast.LiteralNode(Ast.ExecutionContext.DefaultValue(vt));
                    }
                    break;
                case Ast.TokenType.Number:
                    {
                        string num = _lexer.CurrentTokenText; _lexer.NextToken();
                        object value = ParseNumericLiteral(num);
                        return new Ast.LiteralNode(value);
                    }
                case Ast.TokenType.Char:
                    {
                        char ch = _lexer.CurrentTokenText[0];
                        _lexer.NextToken();
                        return new Ast.LiteralNode(ch);
                    }
                case Ast.TokenType.String:
                    {
                        var strValue = Unescape(_lexer.CurrentTokenText);
                        _lexer.NextToken();
                        return new Ast.LiteralNode(strValue);
                    }
                case Ast.TokenType.InterpolatedString:
                    {
                        var strValue = _lexer.CurrentTokenText;
                        _lexer.NextToken();
                        return ParseInterpolatedString(strValue);
                    }
                case Ast.TokenType.BracketsOpen:
                    {
                        _lexer.NextToken();//skip [
                        var items = new List<Ast.AstNode>();
                        bool dict = false;
                        if (_lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                        {
                            while (true)
                            {
                                var first = ParseExpression();

                                if (_lexer.CurrentTokenType == Ast.TokenType.Colon)
                                {
                                    dict = true;
                                    _lexer.NextToken(); // skip :
                                    var value = ParseExpression();
                                    items.Add(first);
                                    items.Add(value);
                                }
                                else
                                {
                                    if (dict)
                                        throw new ParseException("Expected ':' in dictionary element");
                                    items.Add(first);
                                }
                                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                {
                                    _lexer.NextToken();
                                    if (_lexer.CurrentTokenType == Ast.TokenType.BracketsClose) break;
                                    continue;
                                }
                                break;
                            }
                        }
                        Consume(Ast.TokenType.BracketsClose);
                        return new Ast.CollectionExpressionNode(items.ToArray(), dict);
                    }
                case Ast.TokenType.Identifier:
                    {
                        var name = _lexer.CurrentTokenText;
                        _lexer.NextToken();
                        if (IsOp("=>"))
                        {
                            _lexer.NextToken();
                            var body = ParseExpression();
                            string lambdaName = (++_anonCounter).ToString();
                            Ast.ValueType? retType = null;
                            if (body is not Ast.BlockNode)
                                retType = Ast.ValueType.Object;

                            var decl = new Ast.FunctionDeclarationNode(
                                ret: retType,
                                name: lambdaName,
                                @params: new string[] { name },
                                defVals: Array.Empty<Ast.AstNode>(),
                                types: new Ast.ValueType[] { Ast.ValueType.Object },
                                body: body);

                            return new Ast.LambdaNode(decl);
                        }
                        if (name == "true") return new Ast.LiteralNode(true);
                        if (name == "false") return new Ast.LiteralNode(false);
                        if (name == "null") return new Ast.LiteralNode(null);
                        Ast.AstNode? ln = null;
                        if (_constScopes.Any(s => s.TryGetValue(name, out ln)))
                            return ln!;
                        var parts = new List<string> { name };
                        bool lastNullConditional = false;
                        while (_lexer.CurrentTokenType == Ast.TokenType.Dot)
                        {
                            lastNullConditional = _lexer.CurrentTokenText == "?.";
                            _lexer.NextToken();
                            if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                                break;

                            parts.Add(_lexer.CurrentTokenText);
                            _lexer.NextToken();
                        }

                        Ast.AstNode node = new Ast.VariableReferenceNode(name);
                        Ast.GenericUse? callGeneric = null;
                        if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                        {
                            int savePos = _lexer.Position;
                            var saveType = _lexer.CurrentTokenType;
                            var saveText = _lexer.CurrentTokenText;
                            try
                            {
                                var peeked = ParseTypeArgumentListIfAny();
                                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                                {
                                    callGeneric = new Ast.GenericUse(peeked);
                                }
                                else
                                {
                                    _lexer.ResetCurrent(savePos, saveType, saveText);
                                }
                            }
                            catch
                            {
                                _lexer.ResetCurrent(savePos, saveType, saveText);
                            }
                        }
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                        {
                            _lexer.NextToken(); //skip (
                            string fullname = string.Join(".", parts);
                            var args = new List<Ast.AstNode>();
                            var argNames = new List<string?>();
                            bool sawNamed = false;
                            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                            {
                                while (true)
                                {
                                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "out")
                                    {
                                        _lexer.NextToken(); //skip out
                                        bool isVar = _lexer.CurrentTokenText == "var";
                                        if (isVar)
                                        {
                                            throw new ParseException("out parameter has to be explicitly declared.");
                                        }

                                        var type = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                                        _lexer.NextToken(); //skip type
                                        (bool isArr, _) = ReadArraySuffixes();
                                        if (isArr) type = Ast.ValueType.Array;
                                        type = ReadMaybePointer(type);
                                        string varName = _lexer.CurrentTokenText;
                                        Consume(Ast.TokenType.Identifier);
                                        args.Add(new Ast.OutNode(varName, type));
                                        argNames.Add(null);
                                    }
                                    else if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "ref")
                                    {
                                        _lexer.NextToken(); // skip ref
                                        if (sawNamed)
                                            throw new ApplicationException("'ref' argument cannot appear after named argument.");
                                        var targetExpr = ParseExpression();
                                        args.Add(new Ast.UnaryOpNode(Ast.OperatorToken.AddressOf, targetExpr));
                                        argNames.Add(null);
                                    }
                                    else if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                                    {
                                        int savePos = _lexer.Position;
                                        var saveType = _lexer.CurrentTokenType;
                                        var saveText = _lexer.CurrentTokenText;
                                        string candidate = _lexer.CurrentTokenText;
                                        _lexer.NextToken();

                                        if (_lexer.CurrentTokenType == Ast.TokenType.Colon)
                                        {
                                            sawNamed = true;
                                            _lexer.NextToken(); // skip :
                                            var valueExpr = ParseExpression();
                                            argNames.Add(candidate);
                                            args.Add(valueExpr);
                                        }
                                        else
                                        {
                                            _lexer.ResetCurrent(savePos, saveType, saveText);

                                            if (sawNamed)
                                                throw new ParseException("Positional argument cannot appear after named argument.");

                                            argNames.Add(null);
                                            args.Add(ParseExpression());
                                        }

                                    }
                                    else
                                    {
                                        if (sawNamed)
                                            throw new ParseException("Positional argument cannot appear after named argument.");

                                        argNames.Add(null);
                                        args.Add(ParseExpression());
                                    }
                                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                                    { _lexer.NextToken(); continue; }
                                    break;
                                }
                            }
                            Consume(Ast.TokenType.ParenClose);
                            return new Ast.CallNode(fullname, args.ToArray(), argNames.ToArray())
                            {
                                Generic = callGeneric,
                                IsNullConditional = lastNullConditional
                            };
                        }

                        while (_lexer.CurrentTokenType == Ast.TokenType.BracketsOpen)
                        {
                            bool nullIndex = _lexer.CurrentTokenText == "?[";
                            _lexer.NextToken(); //skip [
                            bool fromEnd = false;
                            if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "^")
                            {
                                fromEnd = true;
                                _lexer.NextToken();
                            }
                            bool hasStart = false;
                            Ast.AstNode? startExpr = null;
                            if (_lexer.CurrentTokenType != Ast.TokenType.Range && _lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                            {
                                hasStart = true;
                                startExpr = ParseExpression();
                            }
                            if (_lexer.CurrentTokenType == Ast.TokenType.Range)
                            {
                                _lexer.NextToken(); //skip ..

                                Ast.AstNode? endExpr = null;
                                if (_lexer.CurrentTokenType != Ast.TokenType.BracketsClose)
                                    endExpr = ParseExpression();

                                Consume(Ast.TokenType.BracketsClose);

                                var args = new List<Ast.AstNode> { node };
                                if (hasStart) args.Add(startExpr!);
                                if (endExpr != null) args.Add(endExpr);

                                node = new Ast.CallNode("InRange", args.ToArray()) { IsNullConditional = nullIndex };
                                continue;
                            }


                            Ast.AstNode indexExpr = hasStart ? startExpr! : ParseExpression();
                            Consume(Ast.TokenType.BracketsClose); //skip ]
                            node = new Ast.ArrayIndexNode(node, indexExpr, fromEnd, nullIndex);
                            continue;
                        }
                        if (_lexer.CurrentTokenType == Ast.TokenType.Dot)
                        {
                            var memberParts = new List<string>();
                            bool anyNullConditional = false;
                            do
                            {
                                anyNullConditional = _lexer.CurrentTokenText == "?.";
                                _lexer.NextToken();

                                if (_lexer.CurrentTokenType != Ast.TokenType.Identifier)
                                    throw new ParseException("Identifier expected after '.'");

                                memberParts.Add(_lexer.CurrentTokenText);
                                _lexer.NextToken(); //skip name
                            }
                            while (_lexer.CurrentTokenType == Ast.TokenType.Dot);

                            return new Ast.UnresolvedReferenceNode(memberParts, anyNullConditional, root: node);
                        }
                        if (parts.Count > 1) //arr.Length
                        {
                            return new Ast.UnresolvedReferenceNode(parts, lastNullConditional);
                        }
                        return node;
                    }
                case Ast.TokenType.ParenOpen:
                    {
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
                    }
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
            var parseExeption = MakeParseError($"Unexpected token in expression: {_lexer.CurrentTokenType}: {_lexer.CurrentTokenText}");
            var miss = new Ast.MissingNode(parseExeption, _lexer.Position);
            Report(parseExeption);
            if (_lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
                _lexer.NextToken();
            return miss;
        }
        private Ast.AstNode ParseInterpolatedString(string content)
        {
            //cut string
            var parts = new List<Ast.AstNode>();
            var sb = new StringBuilder();
            int i = 0;
            while (i < content.Length)
            {
                //brace escape?
                if (content[i] == '{' && i + 1 < content.Length && content[i + 1] == '{')
                {
                    sb.Append('{'); i += 2; continue;
                }
                if (content[i] == '}' && i + 1 < content.Length && content[i + 1] == '}')
                {
                    sb.Append('}'); i += 2; continue;
                }
                if (content[i] == '{')//expression start
                {
                    if (sb.Length > 0)
                    {
                        parts.Add(new Ast.LiteralNode(Unescape(sb.ToString())));
                        sb.Clear();
                    }
                    int j = i + 1;
                    int depth = 0;
                    bool inStr = false, inChar = false, inVerbatim = false;
                    while (j < content.Length)
                    {
                        char ch = content[j];

                        if (inStr)
                        {
                            if (ch == '\\') { j += 2; continue; }
                            if (ch == '"') { inStr = false; j++; continue; }
                            j++; continue;
                        }
                        if (inChar)
                        {
                            if (ch == '\\') { j += 2; continue; }
                            if (ch == '\'') { inChar = false; j++; continue; }
                            j++; continue;
                        }
                        if (inVerbatim)
                        {
                            if (ch == '"' && j + 1 < content.Length && content[j + 1] == '"') { j += 2; continue; }
                            if (ch == '"') { inVerbatim = false; j++; continue; }
                            j++; continue;
                        }
                        if (ch == '$' && j + 1 < content.Length)
                        {

                        }
                        if (ch == '@' && j + 1 < content.Length && content[j + 1] == '"') { inVerbatim = true; j += 2; continue; }
                        if (ch == '"') { inStr = true; j++; continue; }
                        if (ch == '\'') { inChar = true; j++; continue; }

                        if (ch == '{') { depth++; j++; continue; }
                        if (ch == '}')
                        {
                            if (depth == 0) break;
                            depth--; j++; continue;
                        }

                        j++;

                    }
                    if (j >= content.Length)
                        throw new ParseException("Unclosed { in interpolated string");

                    string exprSrc = content.Substring(i + 1, j - (i + 1)).Trim();
                    int comma = -1, colon = -1, d = 0;
                    bool sIn = false, cIn = false, vIn = false;

                    for (int p = 0; p < exprSrc.Length; p++)
                    {
                        if (sIn) { if (exprSrc[p] == '\\') { p++; continue; } if (exprSrc[p] == '"') sIn = false; continue; }
                        if (cIn) { if (exprSrc[p] == '\\') { p++; continue; } if (exprSrc[p] == '\'') cIn = false; continue; }
                        if (vIn)
                        {
                            if (exprSrc[p] == '"' && p + 1 < exprSrc.Length && exprSrc[p + 1] == '"') { p++; continue; }
                            if (exprSrc[p] == '"') { vIn = false; }
                            continue;
                        }
                        if (exprSrc[p] == '"')
                        {
                            sIn = true; continue;
                        }
                        if (exprSrc[p] == '\'') { cIn = true; continue; }

                        if (exprSrc[p] == '{') { d++; continue; }
                        if (exprSrc[p] == '}') { d--; continue; }

                        if (d == 0)
                        {
                            if (exprSrc[p] == ',' && comma < 0) { comma = p; continue; }
                            if (exprSrc[p] == ':' && colon < 0) { colon = p; }
                        }
                    }
                    string? exprText, alignText = null, fmtText = null;
                    if (comma >= 0 && colon >= 0 && colon > comma)
                    {
                        exprText = exprSrc.Substring(0, comma).Trim();
                        alignText = exprSrc.Substring(comma + 1, colon - (comma + 1)).Trim();
                        fmtText = exprSrc.Substring(colon + 1).Trim();
                    }
                    else if (comma >= 0)
                    {
                        exprText = exprSrc.Substring(0, comma).Trim();
                        alignText = exprSrc.Substring(comma + 1).Trim();
                    }
                    else if (colon >= 0)
                    {
                        exprText = exprSrc.Substring(0, colon).Trim();
                        fmtText = exprSrc.Substring(colon + 1).Trim();
                    }
                    else
                    {
                        exprText = exprSrc;
                    }
                    Ast.AstNode valueNode = new Parser(exprText).ParseExpression();
                    if (!string.IsNullOrEmpty(fmtText))
                    {
                        valueNode = new Ast.CallNode("ToString", new Ast.AstNode[] { valueNode, new Ast.LiteralNode(fmtText) });
                    }
                    else
                    {
                        valueNode = new Ast.CallNode("ToString", new Ast.AstNode[] { valueNode, new Ast.LiteralNode(null) });
                    }
                    if (!string.IsNullOrEmpty(alignText))
                    {
                        var alignNode = new Parser(alignText).ParseExpression();
                        valueNode = new Ast.CallNode("Align", new Ast.AstNode[] { valueNode, alignNode });
                    }

                    parts.Add(valueNode);

                    i = j + 1; // skip closing }
                    continue;
                }
                if (content[i] == '}')
                    throw new ParseException("Single '}' in interpolated string is not allowed.");
                sb.Append(content[i]);
                i++;
            }
            if (sb.Length > 0) parts.Add(new Ast.LiteralNode(Unescape(sb.ToString())));

            //glue together with +
            if (parts.Count == 0) return new Ast.LiteralNode(string.Empty);
            Ast.AstNode node = parts[0];
            for (int k = 1; k < parts.Count; k++)
                node = new Ast.BinOpNode(node, Ast.OperatorToken.Plus, parts[k]);

            return node;
        }
        private static object ParseNumericLiteral(string raw)
        {
            ReadOnlySpan<char> s = raw.AsSpan();
            string? suffix = null;
            if (s.Length >= 1)
            {
                char last = s[^1];
                if (last is 'f' or 'F' or 'd' or 'D' or 'm' or 'M')
                {
                    suffix = last.ToString();
                    s = s[..^1];
                }
                else if (s.Length >= 2)
                {
                    char a = s[^2], b = s[^1];
                    if ((a is 'u' or 'U' or 'l' or 'L') && (b is 'u' or 'U' or 'l' or 'L') && char.ToLowerInvariant(a) != char.ToLowerInvariant(b))
                    { suffix = "ul"; s = s[..^2]; }
                    else if (b is 'u' or 'U' or 'l' or 'L')
                    { suffix = char.ToLowerInvariant(b).ToString(); s = s[..^1]; }
                }
                else if (last is 'u' or 'U' or 'l' or 'L')
                {
                    suffix = char.ToLowerInvariant(last).ToString();
                    s = s[..^1];
                }
            }
            if (s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            {
                var digits = s[2..];
                EnsureValidDigitsWithUnderscores(digits, IsHexDigit, "hex", raw);

                string cleaned = digits.ToString().Replace("_", "");
                if (!ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ul))
                    throw new ParseException($"Invalid hex literal '{raw}'");

                return CoerceInteger(ul, suffix);
            }
            if (s.Length >= 2 && s[0] == '0' && (s[1] == 'b' || s[1] == 'B'))
            {
                ReadOnlySpan<char> digits = s[2..];
                EnsureValidDigitsWithUnderscores(digits, (char c) => c == '0' || c == '1', "binary", raw);
                string cleaned = digits.ToString().Replace("_", "");
                ulong ul = 0;
                foreach (char c in cleaned)
                {
                    ulong v = (ulong)(c - '0');
                    ul = (ul << 1) | v;
                }

                return CoerceInteger(ul, suffix);
            }
            int eIdx = IndexOfAny(s, 'e', 'E');
            ReadOnlySpan<char> mantissa = eIdx < 0 ? s : s[..eIdx];
            ReadOnlySpan<char> expPart = eIdx < 0 ? default : s[(eIdx + 1)..];
            bool expNegative = false;
            if (!expPart.IsEmpty)
            {
                if (expPart[0] == '+' || expPart[0] == '-')
                {
                    expNegative = expPart[0] == '-';
                    expPart = expPart[1..];
                }
                EnsureValidDigitsWithUnderscores(expPart, char.IsDigit, "exponent", raw);
            }
            int dotIdx = IndexOf(mantissa, '.');
            ReadOnlySpan<char> intPart = dotIdx < 0 ? mantissa : mantissa[..dotIdx];
            ReadOnlySpan<char> fracPart = dotIdx < 0 ? default : mantissa[(dotIdx + 1)..];

            bool hasDot = dotIdx >= 0;
            bool hasExp = eIdx >= 0;
            if (!intPart.IsEmpty) EnsureValidDigitsWithUnderscores(intPart, char.IsDigit, "integer", raw);
            if (!fracPart.IsEmpty) EnsureValidDigitsWithUnderscores(fracPart, char.IsDigit, "fraction", raw);

            if ((intPart.IsEmpty && fracPart.IsEmpty) || (hasExp && expPart.IsEmpty))
                throw new ParseException($"Malformed number literal '{raw}'");
            string intClean = intPart.IsEmpty ? "0" : intPart.ToString().Replace("_", "");
            string fracClean = fracPart.IsEmpty ? "" : fracPart.ToString().Replace("_", "");
            string expClean = expPart.IsEmpty ? "" : (expNegative ? "-" : "") + expPart.ToString().Replace("_", "");

            bool forceFloat = suffix is "f" or "F";
            bool forceDouble = suffix is "d" or "D" || (suffix is null && (hasDot || hasExp));
            bool forceDecimal = suffix is "m" or "M";
            if (forceDecimal)
            {
                if (hasExp)
                    throw new ParseException($"Decimal literal cannot have exponent: '{raw}'");
                string dec = fracClean.Length == 0 ? intClean : (intClean + "." + fracClean);
                if (!decimal.TryParse(dec, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var m))
                    throw new ParseException($"Invalid decimal literal '{raw}'");
                return m;
            }
            if (forceFloat)
            {
                string sfloat = BuildFloatString(intClean, fracClean, expClean);
                if (!float.TryParse(sfloat, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    throw new ParseException($"Invalid float literal '{raw}'");
                return f;
            }

            if (forceDouble || hasDot || hasExp)
            {
                string sdouble = BuildFloatString(intClean, fracClean, expClean);
                if (!double.TryParse(sdouble, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new ParseException($"Invalid double literal '{raw}'");
                return d;
            }
            if (!ulong.TryParse(intClean, NumberStyles.None, CultureInfo.InvariantCulture, out var ui))
                throw new ParseException($"Invalid integer literal '{raw}'");
            return CoerceInteger(ui, suffix);

            static int IndexOf(ReadOnlySpan<char> span, char c)
            { for (int i = 0; i < span.Length; i++) if (span[i] == c) return i; return -1; }
            static int IndexOfAny(ReadOnlySpan<char> span, char a, char b)
            { for (int i = 0; i < span.Length; i++) { var ch = span[i]; if (ch == a || ch == b) return i; } return -1; }
            static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            static void EnsureValidDigitsWithUnderscores(ReadOnlySpan<char> part, Func<char, bool> isDigit, string where, string rawFull)
            {
                if (part.IsEmpty) return;
                if (part[0] == '_' || part[^1] == '_')
                    throw new ParseException($"Underscore cannot be at the start/end of {where} part in '{rawFull}'");
                bool prevDigit = false, prevUnderscore = false;
                for (int i = 0; i < part.Length; i++)
                {
                    char c = part[i];
                    if (c == '_')
                    {
                        if (!prevDigit) throw new ParseException($"Underscore must be between digits in '{rawFull}'");
                        prevDigit = false; prevUnderscore = true;
                        continue;
                    }
                    if (!isDigit(c)) throw new ParseException($"Invalid digit '{c}' in {where} part of '{rawFull}'");
                    if (prevUnderscore == true)
                        prevUnderscore = false;
                    prevDigit = true;
                }
            }

            static string BuildFloatString(string intClean, string fracClean, string expClean)
            {
                var sb = new System.Text.StringBuilder(intClean);
                if (fracClean.Length > 0) { sb.Append('.'); sb.Append(fracClean); }
                if (expClean.Length > 0) { sb.Append('e'); sb.Append(expClean); }
                return sb.ToString();
            }
            static object CoerceInteger(ulong ul, string? sfx)
            {
                if (sfx is not null)
                {
                    switch (sfx.ToLowerInvariant())
                    {
                        case "u":
                            if (ul > uint.MaxValue) throw new OverflowException("Value does not fit into uint");
                            return (uint)ul;
                        case "l":
                            checked { return (long)ul; }
                        case "ul":
                            return ul;
                    }
                }
                if (ul <= int.MaxValue) return (int)ul;
                if (ul <= uint.MaxValue) return (uint)ul;
                if (ul <= long.MaxValue) return (long)ul;
                return ul;
            }
        }
        #endregion
        #region Function parser
        private Ast.AstNode ParseFunctionDeclaration(bool isVoid = false, string? returnTypeText = null, string? name = null, IList<Ast.AttributeNode>? attrs = null, List<string>? fnMods = null)
        {
            Ast.ValueType? retType = isVoid ? null : Enum.Parse<Ast.ValueType>(returnTypeText!, ignoreCase: true);
            if (!isVoid && _lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                SkipTypeArgsIfAny();
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
            name = string.IsNullOrEmpty(_currentNamespace) ? name : $"{_currentNamespace}.{name}";
            var fnTypeParams = ParseTypeParameterListIfAny();
            Consume(Ast.TokenType.ParenOpen);
            var paramNames = new List<string>();
            var paramTypes = new List<Ast.ValueType>();
            var defaultVals = new List<Ast.AstNode?>();
            int paramsIndex = -1;


            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                do
                {
                    bool isParams = false;
                    bool isRef = false;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword)
                    {
                        if (_lexer.CurrentTokenText == "params")
                        {
                            isParams = true;
                            _lexer.NextToken(); //skip params
                        }
                        if (_lexer.CurrentTokenText == "ref")
                        {
                            isRef = true;
                            _lexer.NextToken(); // skip ref
                            if (isParams) throw new ParseException("'params' cannot be combined with 'ref'.");
                        }
                        if (_lexer.CurrentTokenText == "out")
                        {
                            isRef = true;
                            _lexer.NextToken(); // skip out
                            if (isParams) throw new ParseException("'params' cannot be combined with 'out'.");
                        }
                    }
                    var pType = Ast.ValueType.Object;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                    {
                        pType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); //skip time
                        pType = ReadMaybePointer(pType);//ptr*
                        pType = ReadMaybeNullable(pType);//ptr?
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray)
                            pType = Ast.ValueType.Array;

                    }
                    else if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && TryConsumeTupleReturnSignature())
                    {
                        pType = Ast.ValueType.Tuple;
                    }
                    else if (_lexer.CurrentTokenType == Ast.TokenType.Identifier
                        && string.Equals(_lexer.CurrentTokenText, "Dictionary", StringComparison.Ordinal))
                    {
                        pType = Ast.ValueType.Dictionary;
                        _lexer.NextToken(); // skip Dictionary
                        if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                            SkipTypeArgsIfAny();
                        pType = ReadMaybePointer(pType);
                        pType = ReadMaybeNullable(pType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) pType = Ast.ValueType.Array;
                    }
                    if (isRef) pType = Ast.ValueType.Reference;
                    var pname = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    if (isParams)
                    {
                        if (paramsIndex != -1)
                            throw new ParseException("Only one params parameter is allowed.");
                        if (pType != Ast.ValueType.Array)
                            throw new ParseException("The 'params' parameter must be an array type.");
                        paramsIndex = paramNames.Count;
                    }
                    paramTypes.Add(pType);
                    paramNames.Add(pname);
                    Ast.AstNode? def = null;
                    if (_lexer.CurrentTokenText == "=")
                    {
                        _lexer.NextToken();
                        def = ParseExpression();
                    }
                    defaultVals.Add(def);
                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                    }
                    else break;
                }
                while (true);
            }
            Consume(Ast.TokenType.ParenClose);
            Dictionary<string, string[]>? fnWhere = ParseGenericConstraintsIfAny();
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
            return new Ast.FunctionDeclarationNode(retType!, name, paramNames.ToArray(), paramTypes.ToArray(), defaultVals.ToArray(), body, fnMods, attrs)
            {
                Generics = (fnTypeParams.Length == 0 && fnWhere is null) ? null : new Ast.GenericInfo(fnTypeParams, fnWhere),
                ParamsIndex = paramsIndex
            };
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
                                if (_lexer.CurrentTokenType is Ast.TokenType.String or Ast.TokenType.Number or Ast.TokenType.Char
                                    || (_lexer.CurrentTokenType is Ast.TokenType.Keyword && (_lexer.CurrentTokenText is "true" or "false")))
                                {
                                    args.Add(_lexer.CurrentTokenText);
                                    _lexer.NextToken();
                                }
                                else throw new ParseException("Only constant attribute args are supported");

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
            var defaultValues = new List<Ast.AstNode?>();

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
                        pType = ReadMaybeNullable(pType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) pType = Ast.ValueType.Array;
                    }

                    string pname = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    paramNames.Add(pname);
                    paramTypes.Add(pType);
                    Ast.AstNode? def = null;
                    if (_lexer.CurrentTokenText == "=")
                    {
                        _lexer.NextToken();
                        def = ParseExpression();
                    }
                    defaultValues.Add(def);
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
                defVals: defaultValues.ToArray(),
                types: paramTypes.ToArray(),
                body: body);

            return new Ast.LambdaNode(decl);
        }

        #endregion
        #region Structure objects parser
        private Ast.AstNode ParseConstructor(string structName)
        {
            var mods = new List<string>();
            while (IsStructModifier())
            {
                mods.Add(_lexer.CurrentTokenText);
                _lexer.NextToken();
            }

            string ident = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            if (!string.Equals(ident, structName, StringComparison.Ordinal))
                throw new ParseException($"Constructor expected '{structName}'.");

            //params
            Consume(Ast.TokenType.ParenOpen);
            var paramNames = new List<string>();
            var paramTypes = new List<Ast.ValueType>();
            var defaultVals = new List<Ast.AstNode?>();
            int paramsIndex = -1;
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                do
                {
                    bool isParams = false;
                    bool isRef = false;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword)
                    {
                        if (_lexer.CurrentTokenText == "params")
                        {
                            isParams = true;
                            _lexer.NextToken(); //skip params
                        }
                        if (_lexer.CurrentTokenText == "ref")
                        {
                            isRef = true;
                            _lexer.NextToken(); // skip ref
                            if (isParams) throw new ParseException("'params' cannot be combined with 'ref'.");
                        }
                        if (_lexer.CurrentTokenText == "out")
                        {
                            throw new ParseException("'out' modifier cannot be inside a constructor.");
                        }
                    }
                    var pType = Ast.ValueType.Object;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
                    {
                        pType = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                        _lexer.NextToken(); //skip type
                        pType = ReadMaybePointer(pType);
                        pType = ReadMaybeNullable(pType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) pType = Ast.ValueType.Array;
                    }
                    else if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen && TryConsumeTupleReturnSignature())
                    {
                        pType = Ast.ValueType.Tuple;
                    }
                    else if (_lexer.CurrentTokenType == Ast.TokenType.Identifier
                        && string.Equals(_lexer.CurrentTokenText, "Dictionary", StringComparison.Ordinal))
                    {
                        pType = Ast.ValueType.Dictionary;
                        _lexer.NextToken(); // skip Dictionary
                        if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "<")
                            SkipTypeArgsIfAny();
                        pType = ReadMaybePointer(pType);
                        pType = ReadMaybeNullable(pType);
                        var arrInfo = ReadArraySuffixes();
                        if (arrInfo.isArray) pType = Ast.ValueType.Array;
                    }
                    if (isRef)
                        pType = Ast.ValueType.Reference;
                    var pname = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    if (isParams)
                    {
                        if (paramsIndex != -1)
                            throw new ParseException("Only one params parameter is allowed.");
                        if (pType != Ast.ValueType.Array)
                            throw new ParseException("The 'params' parameter must be an array type.");
                        paramsIndex = paramNames.Count;
                    }
                    paramNames.Add(pname);
                    paramTypes.Add(pType);

                    Ast.AstNode? def = null;
                    if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=")
                    {
                        _lexer.NextToken();
                        def = ParseExpression();
                    }
                    defaultVals.Add(def);

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); continue; }
                    break;
                }
                while (true);
            }
            Consume(Ast.TokenType.ParenClose);

            //this or base?
            Ast.ConstructorDeclarationNode.Initializer? init = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.Colon)
            {
                _lexer.NextToken(); // ':'
                if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword || _lexer.CurrentTokenType == Ast.TokenType.Identifier))
                    throw new ApplicationException("Expecteed this or base after ':'");
                string kind = _lexer.CurrentTokenText; //this or base
                _lexer.NextToken();
                Consume(Ast.TokenType.ParenOpen);
                var args = new List<Ast.AstNode>();
                if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
                {
                    do
                    {
                        args.Add(ParseExpression());
                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma) { _lexer.NextToken(); continue; }
                        break;
                    } while (true);
                }
                Consume(Ast.TokenType.ParenClose);
                init = new Ast.ConstructorDeclarationNode.Initializer(kind, args.ToArray());
            }


            Ast.AstNode body;
            if (_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "=>")
            {
                _lexer.NextToken(); //skip =>
                var expr = ParseExpression();
                Consume(Ast.TokenType.Semicolon);
                body = expr;
            }
            else
            {
                body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            }

            return new Ast.ConstructorDeclarationNode(structName, paramNames.ToArray(), paramTypes.ToArray(), defaultVals.ToArray(), body, mods, init, paramsIndex);
        }
        private (string Name, Ast.AstNode Expr)[] ParseObjectInitializer()
        {
            Consume(Ast.TokenType.BraceOpen);
            var items = new List<(string, Ast.AstNode)>();
            if (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
            {
                while (true)
                {
                    string field = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    if (!(_lexer.CurrentTokenType == Ast.TokenType.Operator && _lexer.CurrentTokenText == "="))
                        throw new ParseException("'=' expected in object initializer");
                    _lexer.NextToken(); //skip =
                    var expr = ParseExpression();
                    items.Add((field, expr));
                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        if (_lexer.CurrentTokenType == Ast.TokenType.BraceClose) break;
                        continue;
                    }
                    break;
                }
            }
            Consume(Ast.TokenType.BraceClose);
            return items.ToArray();
        }
        private (Ast.AstNode Key, Ast.AstNode Value)[] ParseDictionaryInitializer()
        {
            var items = new List<(Ast.AstNode Key, Ast.AstNode Value)>();
            Consume(Ast.TokenType.BraceOpen);
            if (_lexer.CurrentTokenType != Ast.TokenType.BraceClose)
            {
                while (true)
                {
                    Consume(Ast.TokenType.BraceOpen);

                    var key = ParseExpression();
                    Consume(Ast.TokenType.Comma);
                    var val = ParseExpression();

                    Consume(Ast.TokenType.BraceClose);
                    items.Add((key, val));

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken(); // skip ,
                        if (_lexer.CurrentTokenType == Ast.TokenType.BraceClose) break;
                        continue;
                    }
                    break;
                }
            }
            Consume(Ast.TokenType.BraceClose);
            return items.ToArray();
        }
        private Ast.AstNode ParseClassDeclaration(IList<string>? mods = null) => ParseStructDeclaration(mods);
        private Ast.AstNode ParseInterfaceDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); // skip interface
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            var typeParams = ParseTypeParameterListIfAny();
            Dictionary<string, string[]>? whereMap = ParseGenericConstraintsIfAny();
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

            return new Ast.InterfaceDeclarationNode(name, mods ?? Array.Empty<string>(), members)
            {
                Generics = (typeParams.Length == 0 && whereMap is null) ? null : new Ast.GenericInfo(typeParams, whereMap)
            };
        }

        private Ast.AstNode ParseStructDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); //skip struct
            string name = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            var typeParams = ParseTypeParameterListIfAny();
            Dictionary<string, string[]>? whereMap = ParseGenericConstraintsIfAny();
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
                if (LooksLikeConstructor(name))
                    members.Add(ParseConstructor(name));
                else
                    members.Add(ParseStatement());
            }
            Consume(Ast.TokenType.BraceClose);
            return new Ast.StructDeclarationNode(name, mods ?? Array.Empty<string>(), members)
            {
                Generics = (typeParams.Length == 0 && whereMap is null) ? null : new Ast.GenericInfo(typeParams, whereMap)
            };
        }
        private Ast.AstNode ParseEnumDeclaration(IList<string>? mods = null)
        {
            _lexer.NextToken(); //skip enum
            string simpleName = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            string name = string.IsNullOrEmpty(_currentNamespace) ? simpleName : $"{_currentNamespace}.{simpleName}";
            var underlying = Ast.ValueType.Int;
            if (_lexer.CurrentTokenText == ":")
            {
                _lexer.NextToken(); //skip :
                if (_lexer.CurrentTokenType != Ast.TokenType.Keyword)
                    throw new ParseException("Underlying type expected after ':'");
                string typeTok = _lexer.CurrentTokenText;
                _lexer.NextToken();//skip type
                var vt = Enum.Parse<Ast.ValueType>(typeTok, true);
                if (!(vt is Ast.ValueType.Byte or Ast.ValueType.Sbyte or Ast.ValueType.UShort or Ast.ValueType.Short
                    or Ast.ValueType.Int or Ast.ValueType.Uint or Ast.ValueType.Long or Ast.ValueType.Ulong))
                    throw new ParseException($"{vt} underlying type is not supported for enums.");
                underlying = vt;
            }
            Consume(Ast.TokenType.BraceOpen);
            var members = new List<Ast.EnumDeclarationNode.Member>();



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
            _declaredEnums.Add(name);
            return new Ast.EnumDeclarationNode(name, members.ToArray(), mods?.ToArray(), underlying);

        }
        private Ast.AstNode ParseTupleDeconstructionDeclaration()
        {
            Consume(Ast.TokenType.ParenOpen);

            var decls = new List<Ast.AstNode>();
            var names = new List<string>();
            while (true)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier && _lexer.CurrentTokenText == "_")
                {
                    names.Add("_");
                    _lexer.NextToken();
                }
                else
                {
                    if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText)))
                        throw new ParseException("Type expected in tuple deconstruction");



                    string typeTok = _lexer.CurrentTokenText;
                    if (typeTok == "var")
                        throw new ParseException("Explicit types are required in tuple deconstruction");

                    _lexer.NextToken(); //skip type

                    var vt = Enum.Parse<Ast.ValueType>(typeTok, true);
                    vt = ReadMaybePointer(vt);
                    vt = ReadMaybeNullable(vt);
                    var arr = ReadArraySuffixes();
                    if (arr.isArray) vt = Ast.ValueType.Array;

                    string name = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);

                    Ast.AstNode init = new Ast.LiteralNode(null);
                    if (name != "_")
                    {
                        Ast.AstNode decl = arr.isArray
                        ? new Ast.VariableDeclarationNode(vt, name, init, isArray: true, arrayLength: arr.dims, isPublic: false, innerType: null)
                        : new Ast.VariableDeclarationNode(vt, name, init, isPublic: false, innerType: null);

                        decls.Add(decl);
                    }
                    names.Add(name);
                }
                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                {
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                    continue;
                }
                break;
            }
            Consume(Ast.TokenType.ParenClose);
            Consume(Ast.TokenType.Operator); //expect =
            var rhs = ParseExpression();
            Consume(Ast.TokenType.Semicolon);
            var leftItems = names.Select(n => ((string?)null, (Ast.AstNode)new Ast.VariableReferenceNode(n))).ToArray();

            var assign = new Ast.BinOpNode(new Ast.TupleLiteralNode(leftItems), Ast.OperatorToken.Equals, rhs);

            decls.Add(assign);
            return decls.Count == 1 ? decls[0] : new Ast.StatementListNode(decls);
        }
        private Ast.AstNode ParseVarTupleDeconstructionDeclaration()
        {
            Consume(Ast.TokenType.Keyword); //expect var
            Consume(Ast.TokenType.ParenOpen);

            var names = new List<string>();
            while (true)
            {
                string name = _lexer.CurrentTokenText;
                Consume(Ast.TokenType.Identifier);
                names.Add(name);

                if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                {
                    _lexer.NextToken();
                    if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                    continue;
                }
                break;
            }

            Consume(Ast.TokenType.ParenClose);
            Consume(Ast.TokenType.Operator); //expect =
            var rhs = ParseExpression();
            Consume(Ast.TokenType.Semicolon);

            var leftItems = names.Select(n => ((string?)null, (Ast.AstNode)new Ast.VariableReferenceNode(n))).ToArray();

            return new Ast.BinOpNode(new Ast.TupleLiteralNode(leftItems), Ast.OperatorToken.Equals, rhs);
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
            bool force = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen && ContainsPatternBinding(condition);
            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock(force) : ParseStatement();
            return new Ast.WhileNode(condition, body);
        }
        private Ast.AstNode ParseFor()
        {
            _lexer.NextToken(); //skip for
            Consume(Ast.TokenType.ParenOpen);

            Ast.AstNode? init = null;

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

            Ast.AstNode? condition = null;
            if (_lexer.CurrentTokenType != Ast.TokenType.Semicolon)
            {
                condition = ParseExpression();
            }
            Consume(Ast.TokenType.Semicolon);
            bool force = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen && ContainsPatternBinding(condition);
            Ast.AstNode? step = null;
            if (_lexer.CurrentTokenType != Ast.TokenType.ParenClose)
            {
                step = ParseExpression();
            }
            Consume(Ast.TokenType.ParenClose);

            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock(force) : ParseStatement();
            return new Ast.ForNode(init!, condition!, step!, body);
        }
        private Ast.AstNode ParseForeach()
        {
            _lexer.NextToken(); //skip foreach
            Consume(Ast.TokenType.ParenOpen);
            string[] names;
            Ast.ValueType[]? types;
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "var")
            {
                _lexer.NextToken(); //skip var
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                {
                    _lexer.NextToken(); //skip (
                    var n = new List<string>();
                    while (true)
                    {
                        string name = _lexer.CurrentTokenText;
                        Consume(Ast.TokenType.Identifier);
                        n.Add(name);

                        if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                        {
                            _lexer.NextToken();
                            if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                            continue;
                        }
                        break;
                    }
                    Consume(Ast.TokenType.ParenClose);
                    names = n.ToArray();
                    types = null;
                }
                else
                {
                    string varName = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    names = new[] { varName };
                    types = null;
                }
            }
            else if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
            {
                _lexer.NextToken(); //skip (
                var n = new List<string>();
                var t = new List<Ast.ValueType>();
                while (true)
                {
                    if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText)))
                        throw new ParseException("Type expected in foreach tuple pattern");
                    bool isVar = _lexer.CurrentTokenText == "var";
                    var vt = isVar ? Ast.ValueType.Object : Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                    _lexer.NextToken(); //skip type
                    vt = ReadMaybePointer(vt);
                    vt = ReadMaybeNullable(vt);
                    var arr = ReadArraySuffixes();
                    if (arr.isArray) vt = Ast.ValueType.Array;
                    string name = _lexer.CurrentTokenText;
                    Consume(Ast.TokenType.Identifier);
                    t.Add(vt);
                    n.Add(name);

                    if (_lexer.CurrentTokenType == Ast.TokenType.Comma)
                    {
                        _lexer.NextToken();
                        if (_lexer.CurrentTokenType == Ast.TokenType.ParenClose) break;
                        continue;
                    }
                    break;
                }
                Consume(Ast.TokenType.ParenClose);
                names = n.ToArray();
                types = t.ToArray();
            }
            else
            {
                if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText)))
                    throw new ParseException("Type expected in foreach");
                bool isVar = _lexer.CurrentTokenText == "var";
                string typeText = _lexer.CurrentTokenText;
                _lexer.NextToken(); //skip type
                var vt = isVar ? Ast.ValueType.Object : Enum.Parse<Ast.ValueType>(typeText, ignoreCase: true);
                vt = ReadMaybePointer(vt);
                vt = ReadMaybeNullable(vt);
                var arr = ReadArraySuffixes();
                if (arr.isArray) vt = Ast.ValueType.Array;

                string varName = _lexer.CurrentTokenText;
                Consume(Ast.TokenType.Identifier);

                names = new[] { varName };
                types = new[] { vt };
            }
            if (_lexer.CurrentTokenText != "in")
                throw new ParseException($"Expected 'in', but got {_lexer.CurrentTokenText}");
            _lexer.NextToken(); // skip in
            var collectionExpr = ParseExpression();
            Consume(Ast.TokenType.ParenClose);
            var body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();

            return new Ast.ForeachNode(names, types, collectionExpr, body);
        }

        #endregion
        private Ast.AstNode ParseGoto()
        {
            _lexer.NextToken(); //skip goto
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "case")
            {
                _lexer.NextToken(); // skip case
                var expr = ParseExpression();
                Consume(Ast.TokenType.Semicolon);
                return new Ast.GotoCaseNode(expr, isDefault: false);
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "default")
            {
                _lexer.NextToken(); // skip default
                Consume(Ast.TokenType.Semicolon);
                return new Ast.GotoCaseNode(null, isDefault: true);
            }
            string label = _lexer.CurrentTokenText;
            Consume(Ast.TokenType.Identifier);
            Consume(Ast.TokenType.Semicolon);
            return new Ast.GotoNode(label);
        }
        private Ast.AstNode ParseIf()
        {
            _lexer.NextToken(); //skip if
            if (!TryConsume(Ast.TokenType.ParenOpen))
            {
                _ = Missing("'(' after if");
            }
            Ast.AstNode cond;
            try { cond = ParseExpression(); }
            catch (ParseException e)
            {
                cond = new Ast.MissingNode(e, _lexer.Position);
                Synchronize(Ast.TokenType.ParenClose, Ast.TokenType.BraceOpen, Ast.TokenType.Semicolon);
            }
            if (!TryConsume(Ast.TokenType.ParenClose))
            {
                _ = Missing("')' after condition");
                if (_lexer.CurrentTokenType != Ast.TokenType.BraceOpen)
                    Synchronize(Ast.TokenType.BraceOpen, Ast.TokenType.Semicolon);
            }
            bool force = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen && ContainsPatternBinding(cond);
            Ast.AstNode body = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();
            Ast.AstNode? elseBody = null;
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "else")
            {
                _lexer.NextToken(); //skip else
                elseBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock(force) : ParseStatement();
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

            var cases = new List<(Ast.PatternNode? pattern, Ast.AstNode? value, List<Ast.AstNode> body)>();
            List<Ast.AstNode>? currentBody = null;
            Ast.PatternNode? currentPattern = null;
            Ast.AstNode? currentValue = null; //if null default

            while (_lexer.CurrentTokenType != Ast.TokenType.BraceClose && _lexer.CurrentTokenType != Ast.TokenType.EndOfInput)
            {
                if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && (_lexer.CurrentTokenText == "case" || _lexer.CurrentTokenText == "default"))
                {
                    if (currentBody != null)
                        cases.Add((currentPattern, currentValue, currentBody));

                    currentBody = new List<Ast.AstNode>();
                    currentPattern = null;
                    currentValue = null;

                    if (_lexer.CurrentTokenText == "case")
                    {
                        _lexer.NextToken(); //skip case
                        int savePos = _lexer.Position;
                        var saveType = _lexer.CurrentTokenType;
                        var saveText = _lexer.CurrentTokenText;
                        try
                        {
                            currentPattern = ParsePattern();
                        }
                        catch
                        {
                            _lexer.ResetCurrent(savePos, saveType, saveText);
                            currentValue = ParseExpression();
                        }
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
                cases.Add((currentPattern, currentValue, currentBody));

            Consume(Ast.TokenType.BraceClose);

            return new Ast.SwitchNode(expr, cases);
        }
        private Ast.AstNode ParseTryCatch()
        {
            _lexer.NextToken();
            var tryBody = _lexer.CurrentTokenType == Ast.TokenType.BraceOpen ? ParseBlock() : ParseStatement();

            if (!(_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "catch"))
                throw new ParseException("expected 'catch' after 'try' block");
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
            if (_lexer.CurrentTokenType == Ast.TokenType.Semicolon)
            {
                _lexer.NextToken(); // consume ';'
                return new Ast.ThrowNode(null);
            }
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
        #region Pattern parser
        private Ast.PatternNode ParsePattern()
        {
            var pat = ParsePatternOr();

            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword &&
                _lexer.CurrentTokenText == "when")
            {
                _lexer.NextToken(); //skip when
                Ast.AstNode guard;
                if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
                {
                    _lexer.NextToken();
                    guard = ParseExpression();
                    Consume(Ast.TokenType.ParenClose);
                }
                else
                {
                    guard = ParseExpression();
                }
                pat = new Ast.WhenPatternNode(pat, guard);
            }
            return pat;
        }
        private Ast.PatternNode ParsePatternOr()
        {
            var left = ParsePatternAnd();
            while (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "or")
            {
                _lexer.NextToken();
                var right = ParsePatternAnd();
                left = new Ast.BinaryPatternNode(left, right, andOp: false);
            }
            return left;
        }

        private Ast.PatternNode ParsePatternAnd()
        {
            var left = ParsePatternNot();
            while (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "and")
            {
                _lexer.NextToken();
                var right = ParsePatternNot();
                left = new Ast.BinaryPatternNode(left, right, andOp: true);
            }
            return left;
        }

        private Ast.PatternNode ParsePatternNot()
        {
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && _lexer.CurrentTokenText == "not")
            {
                _lexer.NextToken();
                return new Ast.NotPatternNode(ParsePatternNot());
            }
            return ParsePrimaryPattern();
        }

        private Ast.PatternNode ParsePrimaryPattern()
        {
            //switch default pattern
            if (_lexer.CurrentTokenType == Ast.TokenType.Identifier && _lexer.CurrentTokenText == "_")
            {
                _lexer.NextToken();
                return new Ast.AnyPatternNode();
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.ParenOpen)
            {
                _lexer.NextToken();
                var inner = ParsePattern();
                Consume(Ast.TokenType.ParenClose);
                return inner;
            }
            //null pattern
            if ((_lexer.CurrentTokenType is Ast.TokenType.Keyword or Ast.TokenType.Identifier)
                 && _lexer.CurrentTokenText == "null")
            {
                _lexer.NextToken();
                return new Ast.NullPatternNode();
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.Operator)
            {
                var op = _lexer.ParseOperator();
                if (op is Ast.OperatorToken.Greater or Ast.OperatorToken.GreaterOrEqual or Ast.OperatorToken.Less or Ast.OperatorToken.LessOrEqual)
                {
                    _lexer.NextToken();
                    var constant = ParsePrimary();
                    return new Ast.RelationalPatternNode(op, constant);
                }
            }
            if (_lexer.CurrentTokenType == Ast.TokenType.Keyword && IsTypeKeyword(_lexer.CurrentTokenText))
            {
                var vt = Enum.Parse<Ast.ValueType>(_lexer.CurrentTokenText, true);
                _lexer.NextToken();
                if (_lexer.CurrentTokenType == Ast.TokenType.Identifier)
                {
                    string name = _lexer.CurrentTokenText;
                    _lexer.NextToken();
                    return new Ast.DeclarationPatternNode(vt, name);
                }
                return new Ast.TypePatternNode(vt);
            }
            if ((_lexer.CurrentTokenType is Ast.TokenType.Number or Ast.TokenType.String or Ast.TokenType.Char)
                || ((_lexer.CurrentTokenType is Ast.TokenType.Keyword or Ast.TokenType.Identifier)
                && (_lexer.CurrentTokenText is "true" or "false")))
            {
                var lit = ParsePrimary();
                return new Ast.ConstantPatternNode(lit);
            }
            throw new ParseException("Unsupported pattern");
        }

        #endregion
    }
}
