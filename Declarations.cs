using System;
using System;

namespace Interpretor
{
    public partial class Ast
    {
        public enum ValueType : byte
        {
            Int, Double, Ulong, Uint, Long, Byte, Object, Sbyte, Short, Char, UShort, Float, Decimal, IntPtr, String, Bool, DateTime, TimeSpan,
            Array, Enum, Nullable, Struct, Class, Tuple, Dictionary, Reference
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
            ModuleEqual,
            LessOrEqual,
            GreaterOrEqual,
            RightShift,
            RightShiftEqual,
            UnsignedRightShift,
            UnsignedRightShiftEqual,
            LeftShift,
            LeftShiftEqual,
            Pow,
            AddressOf,
            And,
            Or,
            Lambda,
            BitXor,
            BitXorEqual,
            BitComplement,
            BitAnd,
            BitAndEqual,
            BitOr,
            BitOrEqual,
            NullDefault,
            NullDefaultEqual
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
            EndOfInput,
            Colon,//:
            BraceOpen, //{
            BraceClose,//}
            BracketsOpen,//[
            BracketsClose,//]
            Comma,//,
            Question,//?
            Dot,
            Range,
        }
        private static readonly Dictionary<OperatorToken, OperatorInfo> _operatorTable = new()
        {
            { OperatorToken.Increment, new OperatorInfo(15, Associativity.Right) },
            { OperatorToken.Decrement, new OperatorInfo(15, Associativity.Right) },
            { OperatorToken.Not, new OperatorInfo(14, Associativity.Right) },
            { OperatorToken.AddressOf, new OperatorInfo(14, Associativity.Right) },
            { OperatorToken.BitComplement, new OperatorInfo(14, Associativity.Right) },

            { OperatorToken.Pow, new OperatorInfo(13, Associativity.Right) },
            { OperatorToken.Multiply, new OperatorInfo(12, Associativity.Left) },
            { OperatorToken.Divide, new OperatorInfo(12, Associativity.Left) },
            { OperatorToken.Module, new OperatorInfo(12, Associativity.Left) },

            { OperatorToken.Plus, new OperatorInfo(11, Associativity.Left) },
            { OperatorToken.Minus, new OperatorInfo(11, Associativity.Left) },
            { OperatorToken.RightShift, new OperatorInfo(10, Associativity.Left) },
            { OperatorToken.LeftShift, new OperatorInfo(10, Associativity.Left) },
            { OperatorToken.UnsignedRightShift, new OperatorInfo(10, Associativity.Left) },
            { OperatorToken.Greater, new OperatorInfo(9, Associativity.Left) },
            { OperatorToken.Less, new OperatorInfo(9, Associativity.Left) },
            { OperatorToken.LessOrEqual, new OperatorInfo(9, Associativity.Left) },
            { OperatorToken.GreaterOrEqual, new OperatorInfo(9, Associativity.Left) },
            { OperatorToken.Equal, new OperatorInfo(8, Associativity.Left) }, // ==
            { OperatorToken.NotEqual, new OperatorInfo(8, Associativity.Left) },
            { OperatorToken.BitXor, new OperatorInfo(6, Associativity.Left) },
            { OperatorToken.BitAnd, new OperatorInfo(7, Associativity.Left) },
            { OperatorToken.BitOr, new OperatorInfo(5, Associativity.Left) },
            { OperatorToken.And, new OperatorInfo(4, Associativity.Left) },
            { OperatorToken.Or, new OperatorInfo(3, Associativity.Left) },
            { OperatorToken.NullDefault, new OperatorInfo(2, Associativity.Right) },

            { OperatorToken.Equals, new OperatorInfo(0, Associativity.Right) }, // =
            { OperatorToken.PlusEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.MinusEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.MultiplyEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.DivideEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.ModuleEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.LeftShiftEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.RightShiftEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.UnsignedRightShiftEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.BitAndEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.BitOrEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.BitXorEqual, new OperatorInfo(0, Associativity.Right) },
            { OperatorToken.NullDefaultEqual, new OperatorInfo(0, Associativity.Right) }, // ??=
            //{ OperatorToken.Lambda, new OperatorInfo(0, Associativity.Right) },
        };
    }
    public partial class Lexer
    {
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
            { "%=", Ast.OperatorToken.ModuleEqual },
            { ">>", Ast.OperatorToken.RightShift },
            { ">>=", Ast.OperatorToken.RightShiftEqual },
            { "<<", Ast.OperatorToken.LeftShift },
            { "<<=", Ast.OperatorToken.LeftShiftEqual },
            { "=", Ast.OperatorToken.Equals },
            { "&", Ast.OperatorToken.AddressOf },
            { "&&", Ast.OperatorToken.And },
            { "||", Ast.OperatorToken.Or },
            { "=>", Ast.OperatorToken.Lambda },
            { "^", Ast.OperatorToken.BitXor },
            { "^=", Ast.OperatorToken.BitXorEqual },
            { "**", Ast.OperatorToken.Pow },
            { "|", Ast.OperatorToken.BitOr },
            { "|=", Ast.OperatorToken.BitOrEqual },
            { "&=", Ast.OperatorToken.BitAndEqual },
            { "~", Ast.OperatorToken.BitComplement },
            { ">>>", Ast.OperatorToken.UnsignedRightShift },
            { ">>>=", Ast.OperatorToken.UnsignedRightShiftEqual },
            { "??", Ast.OperatorToken.NullDefault },
            { "??=", Ast.OperatorToken.NullDefaultEqual },
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
            { "?[", Ast.TokenType.BracketsOpen },
            { "]", Ast.TokenType.BracketsClose },
            { ",", Ast.TokenType.Comma },
            { "?", Ast.TokenType.Question },
            { ".", Ast.TokenType.Dot },
            { "?.", Ast.TokenType.Dot },
            { "..", Ast.TokenType.Range },
            };

        private static readonly HashSet<string> Keywords = new() { "int", "string", "char", "byte", "float", "long", "ulong", "uint", "bool", "double", "IntPtr", "intPtr", "object",
            "if", "goto", "for", "while", "do", "void","return", "break","continue","try","catch", "switch", "case", "finally" , "var", "default", "throw", "else", "new", "foreach", "in",
            "using", "struct", "class", "checked", "unchecked", "out", "const", "enum", "ref", "private", "public", "protected", "static", "abstract", "interface", "delegate", "as", "base",
            "internal", "unsafe", "sealed", "virtual", "override", "partial", "readonly", "params", "lock", "implicit", "explicit", "fixed", "extern", "operator", "namespace", "event", "is",
            "and", "or", "not", "async", "await", "volatile", "yield", "record", "where", "nameof", "when", "unmanaged", "notnull", "global", "stackalloc" };
    }
}
