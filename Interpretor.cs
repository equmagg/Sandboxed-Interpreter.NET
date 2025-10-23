namespace Interpretor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;

    public partial class Ast
    {
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
        const int MaxOutputLen = 4000;
        public string Output = String.Empty;
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
        public string Interpret(string code, bool consoleOutput = false, bool printTree = false, bool printMemory = false)
        {
            try
            {
                ImportStandartLibrary(consoleOutput);
                var parser = new Parser(code);
                Ast.AstNode? ast = null;
                try
                {
                    ast = parser.ParseProgram();
                }
                catch (ParseException pe)
                {
                    parser.Report(pe);
                }
                var errors = parser.Errors.Select(x => $"at {x.pos}: {x.exception.ToUserString(parserFramesLimit: 3)}");
                string errorLog = string.Join("\n\n", errors);
                if (errorLog.Length > MaxOutputLen) errorLog = errorLog.Substring(0, MaxOutputLen);
                if (consoleOutput) Console.WriteLine(errorLog);
                Output += errorLog;
                RootNode = ast;
                if (printTree) RootNode?.Print();
                RootNode?.Evaluate(this.Context);
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
                if (printMemory) Console.WriteLine(Context.PrintMemory());
                Context.Dispose();
            }
            return Output;
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
        
        public static bool TryGetOperatorInfo(OperatorToken token, out OperatorInfo? info) =>
        _operatorTable.TryGetValue(token, out info);

        public static OperatorInfo GetOperatorInfo(OperatorToken token) =>
            _operatorTable.TryGetValue(token, out var info) ? info
            : throw new ArgumentNullException($"No operator info defined for {token}");
        private void ImportStandartLibrary(bool consoleOutput = false)
        {
            var printNames = new string[] { "print", "Console.WriteLine" };
            for (int i = 0; i < printNames.Length; i++)
            {
                Context.RegisterNative(printNames[i], (string str) => { if (Output.Length + str?.Length < MaxOutputLen) Output += (str + '\n'); if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (int str) => { if (Output.Length < MaxOutputLen) Output += str + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (ulong str) => { if (Output.Length < MaxOutputLen) Output += str + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (double str) => { if (Output.Length < MaxOutputLen) Output += str + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (bool str) => { if (Output.Length < MaxOutputLen) Output += str.ToString() + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (char str) => { if (Output.Length < MaxOutputLen) Output += str + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (DateTime str) => { if (Output.Length < MaxOutputLen) Output += str.ToString() + '\n'; if (consoleOutput) Console.WriteLine(str); });
                Context.RegisterNative(printNames[i], (TimeSpan str) => { if (Output.Length < MaxOutputLen) Output += str.ToString() + '\n'; if (consoleOutput) Console.WriteLine(str); });
            }

            this.Context.RegisterNative("Console.Write", (string str) => { if (Output.Length + str?.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (int str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (ulong str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (double str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (bool str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (char str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (DateTime str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });
            this.Context.RegisterNative("Console.Write", (TimeSpan str) => { if (Output.Length < MaxOutputLen) Output += str; if (consoleOutput) Console.Write(str); });

            this.Context.RegisterNative("typeof", (object o) => {
                return o is null ? "Null"
                : (o is int i && i > Context.StackSize && i < Context.StackSize + Context.MemoryUsed ? GetTypeByPtr(i)?.ToString() ?? "null" : o.GetType().Name);
            });
            this.Context.RegisterNative("sizeof", (object o) => { return Context.GetTypeSize(ExecutionContext.InferType(o)); });
            this.Context.RegisterNative("Json.Serialize", (int ptr) => { return Context.SerializeJson(ptr); });
            this.Context.RegisterNative("Json.Deserialize", (int ptr) => { return Context.DeserializeJson(ptr); });
            this.Context.RegisterNative("Xaml.Serialize", (int ptr) => { return -1; });
            this.Context.RegisterNative("Xaml.Deserialize", (int ptr) => { return -1; });
            this.Context.RegisterNative("Length", (Func<int, int>)Context.GetArrayLength);
            this.Context.RegisterNative("Length", (string str) => str.Length);
            this.Context.RegisterNative("Count", (Func<int, int>)(p =>
            {
                if (Context.GetHeapObjectType(p) == (Context.IsArray(p) ? Ast.ValueType.Array : Context.GetHeapObjectType(p)))
                    return Context.GetDictionaryCount(p);
                return Context.GetArrayLength(p);
            }));
            this.Context.RegisterNative("ContainsKey", (Func<int, object, bool>)Context.DictionaryContainsKey);
            this.Context.RegisterNative("ContainsValue", (Func<int, object, bool>)Context.DictionaryContainsValue);
            this.Context.RegisterNative("Resize", (Func<int, int, int>)Context.ArrayResize);
            this.Context.RegisterNative("Array.Resize", (Func<int, int, int>)Context.ArrayResize);
            this.Context.RegisterNative("Add", (Func<int, object, int>)Context.ArrayAdd);
            this.Context.RegisterNative("AddAt", (Func<int, int, object, int>)Context.ArrayAddAt);
            this.Context.RegisterNative("RemoveAt", (Func<int, int, int>)Context.ArrayRemoveAt);
            this.Context.RegisterNative("Remove", (int ptr, object value) =>
            {
                var heapType = Context.IsArray(ptr) ? Ast.ValueType.Array : Context.GetHeapObjectType(ptr);
                if (heapType == Ast.ValueType.Dictionary)
                    return Context.DictionaryRemove(ptr, value);
                int i = Context.ArrayIndexOf(ptr, value); if (i < 0) return ptr; return Context.ArrayRemoveAt(ptr, i);
            });
            #region Linq
            this.Context.RegisterNative("Sort", (int ptr) => Context.ArraySort(ptr, asc: true));
            this.Context.RegisterNative("SortDescending", (int ptr) => Context.ArraySort(ptr, asc: false));
            this.Context.RegisterNative("SortBy", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: true));
            this.Context.RegisterNative("OrderBy", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: true));
            this.Context.RegisterNative("SortByDescending", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: false));
            this.Context.RegisterNative("OrderByDescending", (int ptr, object key) => Context.ArraySortBy(ptr, key, asc: false));
            this.Context.RegisterNative("Select", (int ptr, object pred) => Context.ArraySelect(ptr, pred));
            this.Context.RegisterNative("Where", (int ptr, object pred) => Context.ArrayWhere(ptr, pred));
            this.Context.RegisterNative("Any", (int ptr, object pred) => Context.ArrayAny(ptr, pred));
            this.Context.RegisterNative("All", (int ptr, object pred) => Context.ArrayAll(ptr, pred));
            this.Context.RegisterNative("MinBy", (int p, object sel) => Context.ArrayExtremum(p, sel, true));
            this.Context.RegisterNative("MaxBy", (int p, object sel) => Context.ArrayExtremum(p, sel, false));
            this.Context.RegisterNative("Count", (int p, object pr) => Context.ArrayCount(p, pr));
            this.Context.RegisterNative("Sum", (int p, object sel) => Context.ArraySum(p, sel));
            this.Context.RegisterNative("First", (int p, object pr) => Context.ArrayFind(p, pr, false, false, false));
            this.Context.RegisterNative("FirstOrDefault", (int p, object pr) => Context.ArrayFind(p, pr, false, true, false));
            this.Context.RegisterNative("Last", (int p, object pr) => Context.ArrayFind(p, pr, true, false, false));
            this.Context.RegisterNative("LastOrDefault", (int p, object pr) => Context.ArrayFind(p, pr, true, true, false));
            this.Context.RegisterNative("Single", (int p, object pr) => Context.ArrayFind(p, pr, false, false, true));
            this.Context.RegisterNative("SingleOrDefault", (int p, object pr) => Context.ArrayFind(p, pr, false, true, true));
            this.Context.RegisterNative("Concat", (Func<int, int, int>)Context.ArrayConcat);
            this.Context.RegisterNative("Reverse", (Func<int, int>)Context.ArrayReverse);
            this.Context.RegisterNative("Array.Reverse", (Func<int, int>)Context.ArrayReverse);
            this.Context.RegisterNative("Distinct", (Func<int, int>)Context.ArrayDistinct);
            this.Context.RegisterNative("Average", (Func<int, double>)Context.ArrayAverage);
            this.Context.RegisterNative("IndexOf", (Func<int, object, int>)Context.ArrayIndexOf);
            this.Context.RegisterNative("FindIndex", (int p, object pr) => Context.ArrayFindIndex(p, pr));
            #endregion
            this.Context.RegisterNative("Range", (Func<int, int, int>)Context.ArrayRange);
            this.Context.RegisterNative("Range", (int start) => Context.ArrayRange(0, start));
            this.Context.RegisterNative("InRange", (int ptr, int start, int end) => Context.ArraySlice(ptr, start, end));
            this.Context.RegisterNative("InRange", (int ptr, int end) => Context.ArraySlice(ptr, 0, end));
            this.Context.RegisterNative("InRange", (string s, int start, int end) => s.Substring(start, end - start));
            this.Context.RegisterNative("InRange", (string s, int end) => s.Substring(0, end));
            #region char
            this.Context.RegisterNative("IsDigit", (char c) => { return char.IsDigit(c); });
            this.Context.RegisterNative("Char.IsDigit", (char c) => { return char.IsDigit(c); });
            this.Context.RegisterNative("IsLetter", (char c) => { return char.IsLetter(c); });
            this.Context.RegisterNative("Char.IsLetter", (char c) => { return char.IsLetter(c); });
            this.Context.RegisterNative("IsWhiteSpace", (char c) => { return char.IsWhiteSpace(c); });
            this.Context.RegisterNative("Char.IsWhiteSpace", (char c) => { return char.IsWhiteSpace(c); });
            this.Context.RegisterNative("IsLetterOrDigit", (char c) => { return char.IsLetterOrDigit(c); });
            this.Context.RegisterNative("Char.IsLetterOrDigit", (char c) => { return char.IsLetterOrDigit(c); });
            this.Context.RegisterNative("Char.ToUpper", (char c) => { return char.ToUpperInvariant(c); });
            this.Context.RegisterNative("ToUpper", (char c) => { return char.ToUpperInvariant(c); });
            this.Context.RegisterNative("Char.ToLower", (char c) => { return char.ToLowerInvariant(c); });
            this.Context.RegisterNative("ToLower", (char c) => { return char.ToLowerInvariant(c); });
            #endregion
            this.Context.RegisterNative("Join", (Func<string, string, string>)Join);
            this.Context.RegisterNative("Join", (int varaiable, char separator) => { return Join(separator.ToString(), varaiable); });
            this.Context.RegisterNative("String.Join", (char separator, int varaiable) => { return Join(separator.ToString(), varaiable); });
            this.Context.RegisterNative("Join", (int varaiable, string separator) => { return Join(separator, varaiable); });
            this.Context.RegisterNative("String.Join", (string separator, int varaiable) => { return Join(separator, varaiable); });
            this.Context.RegisterNative("Join", (int varaiable) => { return Join(", ", varaiable); });
            #region Random
            this.Context.RegisterNative("Random.Next", (Func<int, int, int>)Random.Shared.Next);
            this.Context.RegisterNative("Random.Shared.Next", (Func<int, int, int>)Random.Shared.Next);
            this.Context.RegisterNative("Random.Shared.Next", (Func<int, int>)Random.Shared.Next);
            this.Context.RegisterNative("Random.Shared.NextInt64", (Func<long>)Random.Shared.NextInt64);
            this.Context.RegisterNative("Random.Shared.NextInt64", (Func<long, long>)Random.Shared.NextInt64);
            this.Context.RegisterNative("Random.Shared.NextDouble", (Func<double>)Random.Shared.NextDouble);
            this.Context.RegisterNative("Random.Shared.NextSingle", (Func<float>)Random.Shared.NextSingle);
            #endregion
            #region DateTime
            this.Context.RegisterNative("DateTime.UtcNow", () => { return DateTime.UtcNow; });
            this.Context.RegisterNative("DateTime.UtcNow.Year", () => { return DateTime.UtcNow.Year; });
            this.Context.RegisterNative("DateTime.UtcNow.Month", () => { return DateTime.UtcNow.Month; });
            this.Context.RegisterNative("DateTime.UtcNow.Day", () => { return DateTime.UtcNow.Day; });
            this.Context.RegisterNative("DateTime.UtcNow.Hour", () => { return DateTime.UtcNow.Hour; });
            this.Context.RegisterNative("DateTime.UtcNow.Minute", () => { return DateTime.UtcNow.Minute; });
            this.Context.RegisterNative("DateTime.UtcNow.Second", () => { return DateTime.UtcNow.Second; });
            this.Context.RegisterNative("DateTime.UtcNow.Microsecond", () => (int)((DateTime.UtcNow.Ticks % TimeSpan.TicksPerMillisecond) / 10));
            this.Context.RegisterNative("DateTime.UtcNow.Millisecond", () => { return DateTime.UtcNow.Millisecond; });
            this.Context.RegisterNative("DateTime.UtcNow.Nanosecond", () => (int)((DateTime.UtcNow.Ticks % 10) * 100));
            this.Context.RegisterNative("DateTime.UtcNow.Ticks", () => { return DateTime.UtcNow.Ticks; });
            this.Context.RegisterNative("DateTime.Now", () => { return DateTime.Now; });
            this.Context.RegisterNative("DateTime.Now.Year", () => { return DateTime.Now.Year; });
            this.Context.RegisterNative("DateTime.Now.Month", () => { return DateTime.Now.Month; });
            this.Context.RegisterNative("DateTime.Now.Day", () => { return DateTime.Now.Day; });
            this.Context.RegisterNative("DateTime.Now.Hour", () => { return DateTime.Now.Hour; });
            this.Context.RegisterNative("DateTime.Now.Minute", () => { return DateTime.Now.Minute; });
            this.Context.RegisterNative("DateTime.Now.Second", () => { return DateTime.Now.Second; });
            this.Context.RegisterNative("DateTime.Now.Microsecond", () => (int)((DateTime.Now.Ticks % TimeSpan.TicksPerMillisecond) / 10));
            this.Context.RegisterNative("DateTime.Now.Millisecond", () => { return DateTime.Now.Millisecond; });
            this.Context.RegisterNative("DateTime.Now.Nanosecond", () => (int)((DateTime.Now.Ticks % 10) * 100));
            this.Context.RegisterNative("DateTime.Now.Ticks", () => { return DateTime.Now.Ticks; });
            this.Context.RegisterNative("DateTime.Today", () => { return DateTime.Today; });
            this.Context.RegisterNative("DateTime.Parse", (string s) => DateTime.Parse(s, CultureInfo.InvariantCulture));
            this.Context.RegisterNative("TimeSpan.Parse", (string s) => TimeSpan.Parse(s, CultureInfo.InvariantCulture));
            this.Context.RegisterNative("Ticks", (DateTime d) => d.Ticks);
            this.Context.RegisterNative("Ticks", (TimeSpan t) => t.Ticks);
            this.Context.RegisterNative("TimeSpan.FromSeconds", (double x) => TimeSpan.FromSeconds(x));
            this.Context.RegisterNative("TimeSpan.FromMinutes", (double x) => TimeSpan.FromMinutes(x));
            this.Context.RegisterNative("TimeSpan.FromHours", (double x) => TimeSpan.FromHours(x));
            this.Context.RegisterNative("TimeSpan.FromDays", (double x) => TimeSpan.FromDays(x));
            this.Context.RegisterNative("TimeSpan.FromMilliseconds", (double x) => TimeSpan.FromMilliseconds(x));
            this.Context.RegisterNative("TimeSpan.FromMicroseconds", (double x) => TimeSpan.FromMicroseconds(x));
            this.Context.RegisterNative("TimeSpan.FromTicks", (long x) => TimeSpan.FromTicks(x));
            #endregion
            #region Math
            this.Context.RegisterNative("Math.Abs", (double x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (decimal x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (int x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (long x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (short x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (sbyte x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Abs", (float x) => Math.Abs(x));
            this.Context.RegisterNative("Math.Sqrt", (double x) => Math.Sqrt(x));
            this.Context.RegisterNative("Math.Cbrt", (double x) => Math.Cbrt(x));
            this.Context.RegisterNative("Math.Floor", (double x) => Math.Floor(x));
            this.Context.RegisterNative("Math.Floor", (float x) => Math.Floor(x));
            this.Context.RegisterNative("Math.Ceiling", (double x) => Math.Ceiling(x));
            this.Context.RegisterNative("Math.Ceiling", (float x) => Math.Ceiling(x));
            this.Context.RegisterNative("Math.Round", (double x) => Math.Round(x));
            this.Context.RegisterNative("Math.Round", (float x) => Math.Round(x));
            this.Context.RegisterNative("Math.Round", (Func<double, int, double>)Math.Round);
            this.Context.RegisterNative("Math.Truncate", (double x) => Math.Truncate(x));
            this.Context.RegisterNative("Math.Sign", (double x) => Math.Sign(x));
            this.Context.RegisterNative("Math.Sign", (float x) => Math.Sign(x));
            this.Context.RegisterNative("Math.Sign", (int x) => Math.Sign(x));
            this.Context.RegisterNative("Math.Sign", (long x) => Math.Sign(x));
            this.Context.RegisterNative("Math.Min", (double a, double b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (decimal a, decimal b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (float a, float b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (int a, int b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (uint a, uint b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (long a, long b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (ulong a, ulong b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (short a, short b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (ushort a, ushort b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Min", (sbyte a, sbyte b) => Math.Min(a, b));
            this.Context.RegisterNative("Math.Max", (double a, double b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (decimal a, decimal b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (float a, float b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (int a, int b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (uint a, uint b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (long a, long b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (ulong a, ulong b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (short a, short b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (ushort a, ushort b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Max", (sbyte a, sbyte b) => Math.Max(a, b));
            this.Context.RegisterNative("Math.Cos", (double a) => Math.Cos(a));
            this.Context.RegisterNative("Math.Acos", (double a) => Math.Acos(a));
            this.Context.RegisterNative("Math.Cosh", (double a) => Math.Cosh(a));
            this.Context.RegisterNative("Math.Sin", (double a) => Math.Sin(a));
            this.Context.RegisterNative("Math.Asin", (double a) => Math.Asin(a));
            this.Context.RegisterNative("Math.Sinh", (double a) => Math.Sinh(a));
            this.Context.RegisterNative("Math.Tan", (double a) => Math.Tan(a));
            this.Context.RegisterNative("Math.Tanh", (double a) => Math.Tanh(a));
            this.Context.RegisterNative("Math.Log", (double a) => Math.Log(a));
            this.Context.RegisterNative("Math.Log2", (double a) => Math.Log2(a));
            this.Context.RegisterNative("Math.Log10", (double a) => Math.Log10(a));
            this.Context.RegisterNative("Math.Exp", (double a) => Math.Exp(a));
            this.Context.RegisterNative("Math.Pow", (Func<double, double, double>)Math.Pow);
            this.Context.RegisterNative("Math.Lerp", (double v1, double v2, double a) => (v1 * (1.0 - a)) + (v2 * a));
            this.Context.RegisterNative("Math.Lerp", (float v1, float v2, float a) => (v1 * (1.0 - a)) + (v2 * a));
            this.Context.RegisterNative("Math.Lerp", (decimal v1, decimal v2, decimal a) => (v1 * (1.0m - a)) + (v2 * a));
            this.Context.RegisterNative("Double.Lerp", (double a, double v1, double v2) => (v1 * (1.0 - a)) + (v2 * a));
            this.Context.RegisterNative("Single.Lerp", (float v1, float v2, float a) => (v1 * (1.0 - a)) + (v2 * a));
            this.Context.RegisterNative("Math.Clamp", (double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (decimal v, decimal lo, decimal hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (float v, float lo, float hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (uint v, uint lo, uint hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (long v, long lo, long hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (ulong v, ulong lo, ulong hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (short v, short lo, short hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (ushort v, ushort lo, ushort hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (byte v, byte lo, byte hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp", (sbyte v, sbyte lo, sbyte hi) => Math.Min(Math.Max(v, lo), hi));
            this.Context.RegisterNative("Math.Clamp01", (double v) => Math.Min(Math.Max(v, 0), 1));
            this.Context.RegisterNative("Math.Clamp01", (float v) => Math.Min(Math.Max(v, 0), 1));
            this.Context.RegisterNative("Math.Clamp01", (decimal v) => Math.Min(Math.Max(v, 0), 1));
            this.Context.Declare("Math.PI", Ast.ValueType.Double, Math.PI);
            this.Context.Declare("Math.E", Ast.ValueType.Double, Math.E);
            this.Context.Declare("Math.Tau", Ast.ValueType.Double, Math.Tau);
            this.Context.Declare("Double.Epsilon", Ast.ValueType.Double, Double.Epsilon);
            this.Context.Declare("Single.Epsilon", Ast.ValueType.Float, Single.Epsilon);
            this.Context.Declare("Double.MinValue", Ast.ValueType.Double, Double.MinValue);
            this.Context.Declare("Double.MaxValue", Ast.ValueType.Double, Double.MaxValue);
            this.Context.Declare("Single.MinValue", Ast.ValueType.Float, Single.MinValue);
            this.Context.Declare("Single.MaxValue", Ast.ValueType.Float, Single.MaxValue);
            this.Context.Declare("Byte.MinValue", Ast.ValueType.Byte, Byte.MinValue);
            this.Context.Declare("Byte.MaxValue", Ast.ValueType.Byte, Byte.MaxValue);
            this.Context.Declare("SByte.MinValue", Ast.ValueType.Sbyte, SByte.MinValue);
            this.Context.Declare("SByte.MaxValue", Ast.ValueType.Sbyte, SByte.MaxValue);
            this.Context.Declare("Int16.MinValue", Ast.ValueType.Short, Int16.MinValue);
            this.Context.Declare("Int16.MaxValue", Ast.ValueType.Short, Int16.MaxValue);
            this.Context.Declare("UInt16.MinValue", Ast.ValueType.UShort, UInt16.MinValue);
            this.Context.Declare("UInt16.MaxValue", Ast.ValueType.UShort, UInt16.MaxValue);
            this.Context.Declare("Int32.MinValue", Ast.ValueType.Int, Int32.MinValue);
            this.Context.Declare("Int32.MaxValue", Ast.ValueType.Int, Int32.MaxValue);
            this.Context.Declare("UInt32.MinValue", Ast.ValueType.Uint, UInt32.MinValue);
            this.Context.Declare("UInt32.MaxValue", Ast.ValueType.Uint, UInt32.MaxValue);
            this.Context.Declare("Int64.MinValue", Ast.ValueType.Long, Int64.MinValue);
            this.Context.Declare("Int64.MaxValue", Ast.ValueType.Long, Int64.MaxValue);
            this.Context.Declare("UInt64.MinValue", Ast.ValueType.Ulong, UInt64.MinValue);
            this.Context.Declare("UInt64.MaxValue", Ast.ValueType.Ulong, UInt64.MaxValue);
            this.Context.Declare("Char.MinValue", Ast.ValueType.Char, Char.MinValue);
            this.Context.Declare("Char.MaxValue", Ast.ValueType.Char, Char.MaxValue);
            #endregion
            #region string
            this.Context.RegisterNative("String.Empty", () => "");
            this.Context.RegisterNative("Environment.NewLine", () => "\n");
            this.Context.RegisterNative("IsNullOrEmpty", (string str) => string.IsNullOrEmpty(str));
            this.Context.RegisterNative("String.IsNullOrEmpty", (string str) => string.IsNullOrEmpty(str));
            this.Context.RegisterNative("IsNullOrWhiteSpace", (string str) => string.IsNullOrWhiteSpace(str));
            this.Context.RegisterNative("String.IsNullOrWhiteSpace", (string str) => string.IsNullOrWhiteSpace(str));
            this.Context.RegisterNative("Substring", (string str, int start) => Context.PackReference(str.Substring(start), ValueType.String));
            this.Context.RegisterNative("Substring", (string str, int start, int len) => Context.PackReference(str.Substring(start, len), ValueType.String));
            this.Context.RegisterNative("Trim", (string s) => s.Trim());
            this.Context.RegisterNative("TrimStart", (string s) => s.TrimStart());
            this.Context.RegisterNative("TrimEnd", (string s) => s.TrimEnd());
            this.Context.RegisterNative("ToUpper", (string s) => s.ToUpperInvariant());
            this.Context.RegisterNative("ToLower", (string s) => s.ToLowerInvariant());
            this.Context.RegisterNative("StartsWith", (string s, string p) => s.StartsWith(p, StringComparison.Ordinal));
            this.Context.RegisterNative("EndsWith", (string s, string p) => s.EndsWith(p, StringComparison.Ordinal));
            this.Context.RegisterNative("PadLeft", (string s, int w) => s.PadLeft(w));
            this.Context.RegisterNative("PadRight", (string s, int w) => s.PadRight(w));
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
            this.Context.RegisterNative("ToString", (DateTime val) => { return val.ToString(CultureInfo.InvariantCulture); });
            this.Context.RegisterNative("ToString", (TimeSpan val) => { return val.ToString(); });
            this.Context.RegisterNative("ToCharArray", (Func<string, int>)ToCharArray);
            this.Context.RegisterNative("IndexOf", (string str, string val) => str.IndexOf(val));
            this.Context.RegisterNative("IndexOf", (string str, char val) => str.IndexOf(val));
            this.Context.RegisterNative("LastIndexOf", (string str, string val) => str.LastIndexOf(val));
            this.Context.RegisterNative("LastIndexOf", (string str, char val) => str.LastIndexOf(val));
            this.Context.RegisterNative("EqualsIgnoreCase", (string str, string str2) => str.Equals(str2, StringComparison.OrdinalIgnoreCase));
            this.Context.RegisterNative("Numeric", (string str) => { return ulong.TryParse(str, out _); });
            this.Context.RegisterNative("IsNumber", (string str) => { return double.TryParse(str, out _); });
            this.Context.RegisterNative("Replace", (string str, string old, string newStr) => { return str.Replace(old, newStr); });
            this.Context.RegisterNative("Replace", (string str, char old, char newStr) => { return str.Replace(old, newStr); });
            this.Context.RegisterNative("Replace", (string str, char old, string newStr) => { return str.Replace(old.ToString(), newStr); });
            this.Context.RegisterNative("Remove", (string str, char old) => { return str.Replace(old.ToString(), ""); });
            this.Context.RegisterNative("Remove", (string str, string old) => { return str.Replace(old, ""); });
            this.Context.RegisterNative("Align", (string s, int width) => width >= 0 ? s.PadLeft(width) : s.PadRight(-width));
            this.Context.RegisterNative("ToString", (object? value, string? format) =>
            {
                if (value is null) return "";
                if (string.IsNullOrEmpty(format)) return value switch
                {
                    DateTime dt => dt.ToString(CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
                    _ => value.ToString() ?? ""
                };
                bool IsAllDigits(ReadOnlySpan<char> s)
                {
                    for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
                    return true;
                }
                if ((format[0] == 'x' || format[0] == 'X') && (format.Length == 1 || IsAllDigits(format.AsSpan(1))))
                {
                    bool upper = format[0] == 'X';
                    string width = format.Length > 1 ? format.Substring(1) : "";
                    string fmt = (upper ? "X" : "x") + width;

                    return value switch
                    {
                        byte or sbyte or short or ushort or int or uint or long or ulong
                            => ((IFormattable)value).ToString(fmt, CultureInfo.InvariantCulture) ?? "",
                        Enum e
                            => Convert.ToUInt64(e).ToString(fmt, CultureInfo.InvariantCulture),
                        _ => throw new ApplicationException("Hex format is valid only for integral types and enums")
                    };
                }
                if (value is IFormattable formattable) return formattable.ToString(format, CultureInfo.InvariantCulture) ?? "";
                return value.ToString() ?? "";
            });
            #endregion
            this.Context.RegisterNative("InvokeByAttribute", (string attr, string[] attrArgs, object[] callArgs)
                => InvokeByAttribute(this.Context, attr, attrArgs, callArgs));

            this.Context.RegisterNative("IntParse", (string val) => { return int.Parse(val); });
            this.Context.RegisterNative("Int.Parse", (string val) => { return int.Parse(val); });
            this.Context.RegisterNative("IntParse", (char val) => { return (int)(val - '0'); });
            this.Context.RegisterNative("Int.Parse", (char val) => { return (int)(val - '0'); });
            this.Context.RegisterNative("UIntParse", (string val) => { return uint.Parse(val); });
            this.Context.RegisterNative("Uint.Parse", (string val) => { return uint.Parse(val); });
            this.Context.RegisterNative("LongParse", (string val) => { return long.Parse(val); });
            this.Context.RegisterNative("Long.Parse", (string val) => { return long.Parse(val); });
            this.Context.RegisterNative("ULongParse", (string val) => { return ulong.Parse(val); });
            this.Context.RegisterNative("ULong.Parse", (string val) => { return ulong.Parse(val); });
            this.Context.RegisterNative("Ulong.Parse", (string val) => { return ulong.Parse(val); });
            this.Context.RegisterNative("DoubleParse", (string val) => { return double.Parse(val); });
            this.Context.RegisterNative("Double.Parse", (string val) => { return double.Parse(val); });
            this.Context.RegisterNative("FloatParse", (string val) => { return float.Parse(val); });
            this.Context.RegisterNative("Float.Parse", (string val) => { return float.Parse(val); });
            this.Context.RegisterNative("DecimalParse", (string val) => { return decimal.Parse(val); });
            this.Context.RegisterNative("Decimal.Parse", (string val) => { return decimal.Parse(val); });
            this.Context.RegisterNative("ByteParse", (string val) => { return byte.Parse(val); });
            this.Context.RegisterNative("Byte.Parse", (string val) => { return byte.Parse(val); });
            this.Context.RegisterNative("ByteParse", (char val) => { return (byte)(val - '0'); });
            this.Context.RegisterNative("Byte.Parse", (char val) => { return (byte)(val - '0'); });
            this.Context.RegisterNative("Invoke", (Func<object, object>)(id => Context.InvokeById(id)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object>)((id, a1) => Context.InvokeById(id, a1)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object>)((id, a1, a2) => Context.InvokeById(id, a1, a2)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object, object>)((id, a1, a2, a3) => Context.InvokeById(id, a1, a2, a3)));
            this.Context.RegisterNative("Invoke", (Func<object, object, object, object, object, object>)((id, a1, a2, a3, a4) => Context.InvokeById(id, a1, a2, a3, a4)));
            this.Context.RegisterNative("Free", (Action<int>)Context.Free);
            this.Context.RegisterNative("Guid.NewGuid", () => Guid.NewGuid().ToString("D"));
            this.Context.RegisterNative("PrintMemoryUsage", () => {
                if (Output.Length < MaxOutputLen) Output +=
                    ($"{Context.MemoryUsed / 1024}Kb {Context.MemoryUsed % 1024}B used out of {(Context.RawMemory.Length - Context.StackSize) / 1024}Kb total");
            });
            this.Context.RegisterNative("PrintMemoryDump", () => {
                var mem = Context.PrintMemory(); if (Output.Length + Math.Min(mem.Length, MaxOutputLen - 1) < MaxOutputLen)
                    Output += mem.Length > MaxOutputLen - 1 ? mem.Substring(0, MaxOutputLen - 1) : mem;
            });
            this.Context.RegisterNative("GetMemoryUsage", () => {
                return $"{Context.MemoryUsed / 1024}Kb {Context.MemoryUsed % 1024}B used " +
                $"out of {(Context.RawMemory.Length - Context.StackSize) / 1024}Kb total";
            });
            this.Context.RegisterNative("GetMemoryDump", () => { var mem = Context.PrintMemory(); return mem; });
            this.Context.RegisterNative("GetMemoryDump", (int len) => { var mem = Context.PrintMemory(); return mem.Length > len ? mem.Substring(0, len) : mem; });
            this.Context.RegisterNative("GC.Collect", () => { Context.CollectGarbage(); });
            this.Context.RegisterNative("ValidAddress", (int addr) => { return addr >= Context.StackSize && addr < Context.StackSize + Context.MemoryUsed; });

        }
        #region Helpers
        public static string FormatExceptionForUser(Exception ex, int frameLimit = 3, int innerLimit = 1)
        {
            for (int i = 0; i < innerLimit; i++)
            {
                if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                    ex = tie.InnerException;
                else if (ex.InnerException != null)
                    ex = ex.InnerException;
                else
                    break;
            }

            var sb = new System.Text.StringBuilder(256);
            sb.Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);

            var st = ex.StackTrace;
            if (string.IsNullOrEmpty(st))
                return sb.Append("  at <no stack trace>").ToString();

            var lines = st.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int shown = 0;
            for (int i = 0; i < lines.Length && shown < frameLimit; i++)
            {
                var line = lines[i].TrimStart();
                if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;

                int inIdx = line.IndexOf(" in ", StringComparison.Ordinal);
                if (inIdx >= 0) line = line.Substring(0, inIdx);

                sb.Append("  ").AppendLine(line);
                shown++;
            }
            if (lines.Length > shown) sb.AppendLine("  ...");
            return sb.ToString().TrimEnd();
        }
        private Ast.ValueType? GetTypeByPtr(int ptr)
        {
            if (ptr <= 0 || ptr >= this.Context.RawMemory.Length - sizeof(int)) return null;
            if (ptr < Context.StackSize)
            {
                var v = Context.GetVariableByAddress(ptr);
                return !v.HasValue ? null : v.Value.Type;
            }
            return this.Context.IsArray(ptr) ? ValueType.Array : this.Context.GetHeapObjectType(ptr);
        }
        private static bool MatchVariableType(object? value, ValueType type) => value is null ? false : type switch
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
            ValueType.Reference => value is int,
            ValueType.Nullable => value is null || !IsReferenceType(ExecutionContext.InferType(value)),
            ValueType.Struct => value is int,
            ValueType.Class => value is int,
            ValueType.Tuple => value is int,
            ValueType.Dictionary => value is int,
            ValueType.DateTime => value is DateTime,
            ValueType.TimeSpan => value is TimeSpan,
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
            ValueType.Reference => false,
            ValueType.DateTime => false,
            ValueType.TimeSpan => false,
            _ => true
        };
        static bool IsNumeric(Ast.ValueType t) => t is Ast.ValueType.Byte or Ast.ValueType.Sbyte
            or Ast.ValueType.Short or Ast.ValueType.UShort
            or Ast.ValueType.Int or Ast.ValueType.Uint
            or Ast.ValueType.Long or Ast.ValueType.Ulong
            or Ast.ValueType.Float or Ast.ValueType.Double or Ast.ValueType.Decimal;
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
            if (elemType == Ast.ValueType.Tuple)
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var (et, val, _) in Context.ReadTuple(ptr))
                    parts.Add(val?.ToString() ?? string.Empty);
                return string.Join(separator, parts);
            }
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
                else slice = Context.ReadFromMemorySlice(Context.RawMemory[(ptr + offset)..(ptr + offset + elemSize)], elemType);
                sb.Append(slice?.ToString() ?? string.Empty);
            }
            return sb.ToString();

        }
        private int Split(string str, string sep)
        {
            if (str is null) throw new NullReferenceException("null reference in Split");
            var parts = str.Split(sep);
            int count = parts.Length;

            int arrPtr = this.Context.Malloc(sizeof(int) * count, Ast.ValueType.String, isArray: true);
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
        private int ToCharArray(string s)
        {
            if (s is null) throw new NullReferenceException("null reference in ToCharArray");
            int len = s.Length;
            int arrPtr = this.Context.Malloc(len * sizeof(char), Ast.ValueType.Char, isArray: true);
            var span = this.Context.RawMemory.AsSpan(arrPtr, len * sizeof(char));
            for (int i = 0; i < len; i++)
                BitConverter.GetBytes(s[i]).CopyTo(span.Slice(i * sizeof(char)));
            return arrPtr;
        }
        private bool HasAttribute(Ast.ExecutionContext.Function fn, string name, string[] args)
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
                if (callArgs.Length > fn.ParamNames.Length) continue;
                int required = Enumerable.Range(0, fn.ParamNames.Length)
                    .Count(i => fn.DefaultValues[i] is null);
                if (callArgs.Length < required) continue;
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
                    Ast.ValueType needType = (candidate.Value).ParamTypes[i];

                    if (!Ast.MatchVariableType(arg, needType) && needType != Ast.ValueType.Object)
                        arg = ctx.Cast(arg!, needType);

                    ctx.Declare((candidate).Value.ParamNames[i], needType, arg);
                }
                for (int i = callArgs.Length; i < (candidate.Value).ParamNames.Length; i++)
                {
                    object arg = (candidate.Value).DefaultValues[i]!.Evaluate(ctx);
                    Ast.ValueType needType = (candidate.Value).ParamTypes[i];
                    if (!Ast.MatchVariableType(arg, needType) && needType != Ast.ValueType.Object)
                        arg = ctx.Cast(arg, needType);
                    ctx.Declare((candidate.Value).ParamNames[i], needType, arg);
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
                            if (vInfo.Type == ValueType.Reference)
                                return context.ReadVariable(vr.Name);
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
                            if (!varInfo.HasValue) throw new InvalidOperationException("Stack pointer to dead memory");
                            return context.ReadFromStack(addr, (ValueType)(varInfo.Value.Type));
                        }
                        int p = Convert.ToInt32(Operand.Evaluate(context));
                        context.ValidateAddress(p, sizeof(int));
                        return BitConverter.ToInt32(context.RawMemory, p);
                    case OperatorToken.BitComplement: return ~(int)Operand.Evaluate(context);
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
                    if (vr.TryGetCache(ctx, out int addr, out var vt)
                        && !Ast.IsReferenceType(vt) && vt != ValueType.Char && vt != ValueType.Reference)
                    {
                        var cur = ctx.ReadFromStack(addr, vt);
                        object newVal = Add(cur, add);
                        ctx.WriteVariableById(addr, vt, newVal);
                        return IsPostfix ? cur : newVal;
                    }
                    var v = ctx.Get(vr.Name);
                    if (v.Type == ValueType.Reference)
                    {
                        int address = Convert.ToInt32(ctx.ReadVariable(v));
                        var cur = ctx.ReadByAddress(address);
                        var newVal = Add(cur!, add);
                        ctx.AssignByAddress(address, newVal);
                        return IsPostfix ? cur! : newVal;
                    }
                    var curByName = ctx.ReadVariable(v);
                    object newValByName = Add(curByName, add);
                    ctx.WriteVariable(v, newValByName);
                    return IsPostfix ? curByName : newValByName;
                }
                if (Operand is ArrayIndexNode ai)
                {
                    var cur = ai.Evaluate(ctx);
                    var newVal = Add(cur, add);
                    ai.Write(ctx, newVal);
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
                if (Op == OperatorToken.NullDefault)
                {
                    var leftVal = Left.Evaluate(context);
                    return leftVal ?? Right.Evaluate(context);
                }
                if (Op == OperatorToken.NullDefaultEqual)
                {
                    object current = Left.Evaluate(context);
                    if (current != null) return current;

                    object newVal = Right.Evaluate(context);

                    if (Left is VariableReferenceNode v)
                    {
                        var info = context.Get(v.Name);
                        if (info.Type == ValueType.String)
                            context.StoreStringVariable(v.Name, newVal?.ToString() ?? string.Empty);
                        else context.WriteVariable(v.Name, newVal);
                    }
                    else if (Left is ArrayIndexNode ai) ai.Write(context, newVal);
                    else throw new InvalidOperationException("Left side of ??= must be assignable");

                    return newVal!;
                }
                if (Op == OperatorToken.Equals && Left is TupleLiteralNode tl)
                {
                    var names = new List<string>(tl.Items.Length);
                    foreach (var (name, expr) in tl.Items)
                    {
                        if (name != null)
                            throw new ApplicationException("Assigment tuples should not be named '='");

                        if (expr is VariableReferenceNode varRef)
                        {
                            names.Add(varRef.Name);
                        }
                        else
                        {
                            if (expr is UnresolvedReferenceNode ur && ur.Parts.Count == 1)
                                names.Add(ur.Parts[0]);
                            else
                                throw new InvalidOperationException("Invalid token in tuple assignment");
                        }
                    }

                    var rhs = Right.Evaluate(context);
                    context.DeconstructAssign(names.ToArray(), rhs);
                    return rhs;
                }
                if (Op == OperatorToken.Equals && Left is VariableReferenceNode lv)
                {
                    if (!context.HasVariable(lv.Name) && context.HasVariable("this") && context.Get("this").Type == Ast.ValueType.Struct)
                    {
                        int instPtr = Convert.ToInt32(context.ReadVariable("this"));
                        var rhs = Right.Evaluate(context);
                        context.WriteStructField(instPtr, lv.Name, rhs);
                        return rhs;
                    }
                    var lvInfo = context.Get(lv.Name);
                    if (lvInfo.Type == ValueType.Reference)
                    {
                        int addr = Convert.ToInt32(context.ReadVariable(lv.Name));
                        object rhs = Right.Evaluate(context);
                        context.AssignByAddress(addr, rhs);
                        return rhs;
                    }
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
                    if (lvInfo.Type == ValueType.Char)
                    {
                        var rhs = Right.Evaluate(context);
                        if (!MatchVariableType(rhs, lvInfo.Type)) throw new ApplicationException($"Type missmatch during char assignment: {rhs.GetType()}");
                        context.WriteVariable(lv.Name, Convert.ToChar(rhs));
                        return Convert.ToChar(rhs);
                    }
                    if (!Ast.IsReferenceType(lvInfo.Type))
                    {
                        object rhs = Right.Evaluate(context);
                        if (!MatchVariableType(rhs, lvInfo.Type) && lvInfo.Type != ValueType.Object)
                            rhs = context.Cast(rhs, lvInfo.Type);
                        if (lv.TryGetCache(context, out int addr, out var vt) && vt == lvInfo.Type)
                        {
                            if (!MatchVariableType(rhs, vt) && vt != ValueType.Object)
                                rhs = context.Cast(rhs, vt);
                            context.WriteVariableById(addr, vt, rhs);
                            return rhs;
                        }

                        context.WriteVariable(lv.Name, rhs);
                        return rhs;
                    }
                    if (lvInfo.Type == ValueType.Nullable)
                    {
                        int oldPtr = (int)context.ReadVariable(lv.Name);
                        if (oldPtr >= context.StackSize && context.IsUsed(oldPtr)) context.Free(oldPtr);
                        var rhs = Right.Evaluate(context);
                        if (rhs is null)
                        {
                            context.WriteVariable(lv.Name, -1);
                            return null!;
                        }
                        if (oldPtr < 0) context.WriteVariable(lv.Name, context.PackReference(rhs, ValueType.Nullable));
                        else
                        {
                            var baseType = (ValueType)context.RawMemory[oldPtr];
                            if (!Ast.MatchVariableType(rhs, baseType)) rhs = context.Cast(rhs, baseType);
                            ReadOnlySpan<byte> src = context.GetSourceBytes(baseType, rhs);
                            src.CopyTo(context.RawMemory.AsSpan(oldPtr + 1, src.Length));
                        }
                        return rhs;
                    }
                    if (lvInfo.Type == ValueType.Array)
                    {
                        int oldPtr = (int)context.ReadVariable(lv.Name);
                        object rhs = Right.Evaluate(context);
                        if (oldPtr >= context.StackSize && context.IsUsed(oldPtr))
                            context.Free(oldPtr);
                        int newPtr;
                        switch (rhs)
                        {
                            case ValueTuple<int, ValueType> info:
                                newPtr = context.PackReference(info, ValueType.Array);
                                break;

                            case ValueTuple<object?[], ValueType> arr:
                                var (items, et) = arr;
                                int elemSize = context.GetTypeSize(et);

                                if (Ast.IsReferenceType(et))
                                {
                                    int p = context.Malloc(sizeof(int) * items.Length, et, isArray: true);
                                    for (int i = 0; i < items.Length; i++)
                                    {
                                        int h = context.PackReference(items[i], et);
                                        BitConverter.GetBytes(h).CopyTo(context.RawMemory, p + i * sizeof(int));
                                    }
                                    newPtr = p;
                                }
                                else
                                {
                                    int p = context.Malloc(elemSize * items.Length, et, isArray: true);
                                    for (int i = 0; i < items.Length; i++)
                                        context.GetSourceBytes(et, items[i]).CopyTo(context.RawMemory.AsSpan(p + i * elemSize, elemSize));
                                    newPtr = p;
                                }

                                break;

                            case int ptr:
                                newPtr = ptr;
                                break;
                            case string ptr:
                                newPtr = int.Parse(ptr);
                                break;
                            default:
                                throw new ApplicationException("Unsupported value on right side of array assignment: " + rhs.GetType());

                        }
                        context.WriteVariable(lv.Name, newPtr);
                        return rhs;
                    }
                }
                else if (Op == OperatorToken.Equals && Left is ArrayIndexNode ai)
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
                    if (l is string || r is string)
                    {
                        bool eq = string.Equals(l?.ToString(), r?.ToString(), StringComparison.Ordinal);
                        return Op == OperatorToken.Equal ? eq : !eq;
                    }
                    else if (l is char || r is char)
                    {
                        bool eq = string.Equals(l?.ToString(), r?.ToString(), StringComparison.Ordinal);
                        return Op == OperatorToken.Equal ? eq : !eq;
                    }
                    else if (l is null || r is null)
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
                else if (l is int li && r is int ri)
                {
                    var fast = EvaluateInt32(li, ri, Op);//purely optimazational fast path
                    result = fast;
                }
                else if (l is bool bl && r is bool br) result = EvaluateBool(bl, br, Op);
                else if (l is double d) result = EvaluateBinary(d, Convert.ToDouble(r), Op);
                else if (r is double dr) result = EvaluateBinary(Convert.ToDouble(l), dr, Op);
                else if (l is float f) result = EvaluateBinary(f, Convert.ToSingle(r), Op);
                else if (r is float fr) result = EvaluateBinary(Convert.ToSingle(l), fr, Op);
                else if (l is decimal de) result = EvaluateBinary(de, Convert.ToDecimal(r), Op);
                else if (r is decimal der) result = EvaluateBinary(Convert.ToDecimal(l), der, Op);
                else if (l is int i) result = EvaluateBinary(i, Convert.ToInt32(r), Op);
                else if (l is long ll) result = EvaluateBinary(ll, Convert.ToInt64(r), Op);
                else if (l is ulong ul) result = EvaluateBinary(ul, Convert.ToUInt64(r), Op);
                else if (l is uint ui) result = EvaluateBinary(ui, Convert.ToUInt32(r), Op);
                else if (l is short s) result = EvaluateBinary(s, Convert.ToInt16(r), Op);
                else if (l is ushort us) result = EvaluateBinary(us, Convert.ToUInt16(r), Op);
                else if (l is byte b) result = EvaluateBinary(b, Convert.ToByte(r), Op);
                else if (l is sbyte sb) result = EvaluateBinary(sb, Convert.ToSByte(r), Op);
                else if (l is DateTime dt && r is TimeSpan ts) result = Op switch
                {
                    OperatorToken.Plus or OperatorToken.PlusEqual => dt + ts,
                    OperatorToken.Minus or OperatorToken.MinusEqual => dt - ts,
                    _ => throw new ApplicationException($"Unsupported op {Op} for DateTime and TimeSpan")
                };
                else if (l is DateTime dl) result = EvaluateBinary(dl, Convert.ToDateTime(r), Op);
                else if (l is TimeSpan tls && r is TimeSpan trs) result = EvaluateTimeSpan(tls, trs, Op);
                else if (l is null) throw new ApplicationException($"Unsupported operator {Op} for null");
                else throw new ApplicationException($"Unsupported operator {Op} for Type {l.GetType()}");
                if (Left is VariableReferenceNode vr && IsAssignmentOp(Op))
                {
                    if (!context.HasVariable(vr.Name) && context.HasVariable("this") && context.Get("this").Type == Ast.ValueType.Struct)
                    {
                        int instPtr = Convert.ToInt32(context.ReadVariable("this"));
                        context.WriteStructField(instPtr, vr.Name, result);
                        return result;
                    }
                    if (vr.TryGetCache(context, out int addr, out var vt)
                        && !Ast.IsReferenceType(vt) && vt != ValueType.Char && vt != ValueType.Reference)
                    {
                        if (!MatchVariableType(result, vt) && vt != ValueType.Object)
                            result = context.Cast(result, vt);
                        context.WriteVariableById(addr, vt, result);
                        return result;
                    }
                    var vInfo = context.Get(vr.Name);
                    if (vInfo.Type == ValueType.Reference)
                    {
                        int address = Convert.ToInt32(context.ReadVariable(vr.Name));
                        object res = result;
                        context.AssignByAddress(address, res);
                        return res;
                    }
                    context.WriteVariable(vInfo, result);
                }
                else if (Left is ArrayIndexNode ai && IsAssignmentOp(Op))
                {
                    ai.Write(context, result);
                }
                else if (Left is UnaryOpNode un && un.Op == OperatorToken.Multiply && Op == OperatorToken.Equals)
                {
                    int addr = un.Operand is VariableReferenceNode pvr
                        ? Convert.ToInt32(context.ReadVariable(pvr.Name))
                        : Convert.ToInt32(un.Operand.Evaluate(context));
                    var target = context.GetVariableByAddress(addr)
                        ?? throw new InvalidOperationException("Pointer points to invalid stack memory");
                    object rhs = Right.Evaluate(context);
                    if (!MatchVariableType(rhs, target.Type)) rhs = context.Cast(rhs, (Ast.ValueType)target.Type);
                    context.WriteVariableById(addr, target.Type, rhs);
                    return rhs;
                }
                else if (IsAssignmentOp(Op) && Left is UnresolvedReferenceNode ur && ur.Parts.Count == 2)
                {
                    string varName = ur.Parts[0];
                    string field = ur.Parts[1];
                    var info = context.Get(varName);
                    if (info.Type != ValueType.Struct) throw new ApplicationException($"{varName} is not a struct");
                    int instPtr = Convert.ToInt32(context.ReadVariable(varName));
                    context.WriteStructField(instPtr, field, result);
                    return result;
                }
                else if (IsAssignmentOp(Op))
                {
                    throw new InvalidOperationException("Assignment operator points to nothing");
                }
                return result;
            }

            private static bool IsAssignmentOp(OperatorToken op) => op is OperatorToken.Equals or OperatorToken.PlusEqual or
                OperatorToken.MinusEqual or OperatorToken.MultiplyEqual or OperatorToken.DivideEqual or OperatorToken.ModuleEqual or
                OperatorToken.NullDefaultEqual or OperatorToken.BitAndEqual or OperatorToken.BitOrEqual or OperatorToken.BitXorEqual or
                OperatorToken.RightShiftEqual or OperatorToken.UnsignedRightShiftEqual or OperatorToken.LeftShiftEqual;
            private static object EvaluateTimeSpan(TimeSpan l, TimeSpan r, OperatorToken op) => op switch
            {
                OperatorToken.Plus or OperatorToken.PlusEqual => l + r,
                OperatorToken.Minus or OperatorToken.MinusEqual => l - r,

                OperatorToken.Greater => l > r,
                OperatorToken.GreaterOrEqual => l >= r,
                OperatorToken.Less => l < r,
                OperatorToken.LessOrEqual => l <= r,
                OperatorToken.Equal => l == r,
                OperatorToken.NotEqual => l != r,

                _ => throw new ApplicationException($"Unsupported op {op} for TimeSpan")
            };
            public static bool EvaluateBool(bool l, bool r, OperatorToken op) => op switch
            {
                OperatorToken.Equals => r,
                OperatorToken.Equal => l == r,
                OperatorToken.NotEqual => l != r,
                OperatorToken.And => l && r,
                OperatorToken.Or => l || r,
                OperatorToken.BitXor => l ^ r,
                _ => throw new ApplicationException($"Unsupported operation {op} for bool type")
            };
            public static object EvaluateBinary<T>(T l, T r, OperatorToken op) where T : struct, IConvertible
            {
                var lCode = l.GetTypeCode();
                var rCode = r.GetTypeCode();
                if (lCode == TypeCode.Decimal || rCode == TypeCode.Decimal)
                {
                    return EvaluateDecimal(Convert.ToDecimal(l), Convert.ToDecimal(r), op);
                }
                if (lCode is TypeCode.Double or TypeCode.Single || rCode is TypeCode.Double or TypeCode.Single)
                {
                    return EvaluateDouble(Convert.ToDouble(l), Convert.ToDouble(r), op);
                }
                if (lCode == TypeCode.DateTime || rCode == TypeCode.DateTime)
                {
                    var ld = Convert.ToDateTime(l); var rd = Convert.ToDateTime(r);
                    return op switch
                    {
                        OperatorToken.Minus or OperatorToken.MinusEqual => ld - rd,
                        OperatorToken.Equal => ld == rd,
                        OperatorToken.NotEqual => ld != rd,
                        OperatorToken.GreaterOrEqual => ld >= rd,
                        OperatorToken.LessOrEqual => ld <= rd,
                        OperatorToken.Less => ld < rd,
                        OperatorToken.Greater => ld > rd,
                        _ => throw new ApplicationException($"Unsupported op {op} for DateTime")
                    };
                }
                bool unsigned = false;
                switch (lCode)
                {
                    case TypeCode.Byte:
                    case TypeCode.DateTime:
                    case TypeCode.UInt16:
                    case TypeCode.UInt32:
                    case TypeCode.UInt64: unsigned = true; break;
                    default:
                        switch (rCode)
                        {
                            case TypeCode.Byte:
                            case TypeCode.UInt16:
                            case TypeCode.UInt32:
                            case TypeCode.UInt64: unsigned = true; break;
                        }
                        break;
                }
                if (unsigned)
                {
                    ulong a = Convert.ToUInt64(l);
                    ulong b = Convert.ToUInt64(r);
                    var res = EvaluateUInt64(a, b, op);
                    if (res is bool) return res;
                    switch (lCode)
                    {
                        case TypeCode.Byte: return res is byte rb ? rb : unchecked((byte)Convert.ToUInt64(res));
                        case TypeCode.UInt16: return res is ushort ru16 ? ru16 : unchecked((ushort)Convert.ToUInt64(res));
                        case TypeCode.UInt32: return res is uint ru32 ? ru32 : unchecked((uint)Convert.ToUInt64(res));
                        default: return res;
                    }
                }
                else
                {
                    long a = Convert.ToInt64(l);
                    long b = Convert.ToInt64(r);
                    var res = EvaluateInt64(a, b, op);
                    if (res is bool) return res;
                    switch (lCode)
                    {
                        case TypeCode.SByte: return res is sbyte rsb ? rsb : unchecked((sbyte)Convert.ToInt64(res));
                        case TypeCode.Int16: return res is short r16 ? r16 : unchecked((short)Convert.ToInt64(res));
                        case TypeCode.Int32: return res is int r32 ? r32 : unchecked((int)Convert.ToInt64(res));
                        default: return res;
                    }
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static object EvaluateInt32(int l, int r, OperatorToken op)
            {
                unchecked
                {
                    switch (op)
                    {
                        case OperatorToken.PlusEqual:
                        case OperatorToken.Plus: return l + r;
                        case OperatorToken.MinusEqual:
                        case OperatorToken.Minus: return l - r;
                        case OperatorToken.MultiplyEqual:
                        case OperatorToken.Multiply: return l * r;
                        case OperatorToken.DivideEqual:
                        case OperatorToken.Divide: return l / r;
                        case OperatorToken.ModuleEqual:
                        case OperatorToken.Module: return l % r;

                        case OperatorToken.RightShiftEqual: return l >> r;
                        case OperatorToken.RightShift: return l >> r;
                        case OperatorToken.LeftShiftEqual: return l << r;
                        case OperatorToken.LeftShift: return l << r;
                        case OperatorToken.UnsignedRightShiftEqual:
                        case OperatorToken.UnsignedRightShift: return l >>> r;
                        case OperatorToken.BitAndEqual: return l & r;
                        case OperatorToken.BitAnd: return l & r;
                        case OperatorToken.BitOrEqual: return l | r;
                        case OperatorToken.BitOr: return l | r;
                        case OperatorToken.BitXorEqual: return l ^ r;
                        case OperatorToken.BitXor: return l ^ r;

                        case OperatorToken.Equal: return l == r;
                        case OperatorToken.Equals: return r;
                        case OperatorToken.NotEqual: return l != r;
                        case OperatorToken.Greater: return l > r;
                        case OperatorToken.GreaterOrEqual: return l >= r;
                        case OperatorToken.Less: return l < r;
                        case OperatorToken.LessOrEqual: return l <= r;

                        case OperatorToken.Pow: return Math.Pow(l, r);
                    }
                }
                return EvaluateBinary((long)l, (long)r, op);
            }
            private static object EvaluateInt64(long l, long r, OperatorToken op) => op switch
            {
                OperatorToken.Plus or OperatorToken.PlusEqual => l + r,
                OperatorToken.Minus or OperatorToken.MinusEqual => l - r,
                OperatorToken.Multiply or OperatorToken.MultiplyEqual => l * r,
                OperatorToken.Divide or OperatorToken.DivideEqual => l / r,
                OperatorToken.Module or OperatorToken.ModuleEqual => l % r,
                OperatorToken.Greater => l > r,
                OperatorToken.Less => l < r,
                OperatorToken.GreaterOrEqual => l >= r,
                OperatorToken.LessOrEqual => l <= r,
                OperatorToken.Equal => l == r,
                OperatorToken.NotEqual => l != r,
                OperatorToken.Equals => r,
                OperatorToken.RightShift or OperatorToken.RightShiftEqual => (int)l >> (int)r,
                OperatorToken.LeftShift or OperatorToken.LeftShiftEqual => (int)l << (int)r,
                OperatorToken.UnsignedRightShift or OperatorToken.UnsignedRightShiftEqual => (int)l >>> (int)r,
                OperatorToken.BitXor or OperatorToken.BitXorEqual => l ^ r,
                OperatorToken.BitOr or OperatorToken.BitOrEqual => l | r,
                OperatorToken.BitAnd or OperatorToken.BitAndEqual => l & r,
                OperatorToken.Pow => Convert.ToInt64(Math.Pow(l, r)),
                _ => throw new ApplicationException($"Unsupported op {op} for Int64")
            };

            private static object EvaluateUInt64(ulong l, ulong r, OperatorToken op) => op switch
            {
                OperatorToken.Plus or OperatorToken.PlusEqual => l + r,
                OperatorToken.Minus or OperatorToken.MinusEqual => l - r,
                OperatorToken.Multiply or OperatorToken.MultiplyEqual => l * r,
                OperatorToken.Divide or OperatorToken.DivideEqual => l / r,
                OperatorToken.Module => l % r,
                OperatorToken.ModuleEqual => l % r,
                OperatorToken.Greater => l > r,
                OperatorToken.Less => l < r,
                OperatorToken.GreaterOrEqual => l >= r,
                OperatorToken.LessOrEqual => l <= r,
                OperatorToken.Equal => l == r,
                OperatorToken.NotEqual => l != r,
                OperatorToken.Equals => r,
                OperatorToken.RightShift => (uint)l >> (int)r,
                OperatorToken.RightShiftEqual => (int)l >> (int)r,
                OperatorToken.LeftShift => (uint)l << (int)r,
                OperatorToken.LeftShiftEqual => (int)l << (int)r,
                OperatorToken.UnsignedRightShift => (uint)l >>> (int)r,
                OperatorToken.UnsignedRightShiftEqual => (int)l >>> (int)r,
                OperatorToken.BitXorEqual => l ^ r,
                OperatorToken.BitXor => l ^ r,
                OperatorToken.BitOr => l | r,
                OperatorToken.BitOrEqual => l | r,
                OperatorToken.BitAnd => l & r,
                OperatorToken.BitAndEqual => l & r,
                OperatorToken.Pow => Convert.ToUInt64(Math.Pow(l, r)),
                _ => throw new ApplicationException($"Unsupported op {op} for UInt64")
            };

            private static object EvaluateDouble(double l, double r, OperatorToken op) => op switch
            {
                OperatorToken.Plus or OperatorToken.PlusEqual => l + r,
                OperatorToken.Minus or OperatorToken.MinusEqual => l - r,
                OperatorToken.Multiply or OperatorToken.MultiplyEqual => l * r,
                OperatorToken.Divide or OperatorToken.DivideEqual => l / r,
                OperatorToken.Module => l % r,
                OperatorToken.ModuleEqual => l % r,
                OperatorToken.Greater => l > r,
                OperatorToken.Less => l < r,
                OperatorToken.GreaterOrEqual => l >= r,
                OperatorToken.LessOrEqual => l <= r,
                OperatorToken.Equal => Math.Abs(l - r) < double.Epsilon,
                OperatorToken.NotEqual => Math.Abs(l - r) > double.Epsilon,
                OperatorToken.Equals => r,
                OperatorToken.Pow => Math.Pow(l, r),
                _ => throw new ApplicationException($"Unsupported op {op} for Double")
            };

            private static object EvaluateDecimal(decimal l, decimal r, OperatorToken op) => op switch
            {
                OperatorToken.Plus or OperatorToken.PlusEqual => l + r,
                OperatorToken.Minus or OperatorToken.MinusEqual => l - r,
                OperatorToken.Multiply or OperatorToken.MultiplyEqual => l * r,
                OperatorToken.Divide or OperatorToken.DivideEqual => l / r,
                OperatorToken.Module => l % r,
                OperatorToken.ModuleEqual => l % r,
                OperatorToken.Greater => l > r,
                OperatorToken.Less => l < r,
                OperatorToken.GreaterOrEqual => l >= r,
                OperatorToken.LessOrEqual => l <= r,
                OperatorToken.Equal => l == r,
                OperatorToken.NotEqual => l != r,
                OperatorToken.Equals => r,
                _ => throw new ApplicationException($"Unsupported op {op} for Decimal")
            };

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── {Op}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                Left.Print(childIndent, false);
                Right.Print(childIndent, true);
            }
        }
        public class AsNode : AstNode
        {
            public ValueType Target;
            public AstNode Expr;
            public AsNode(ValueType target, AstNode expr)
            { Target = target; Expr = expr; }
            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.Check();
                var v = Expr.Evaluate(ctx);
                if (!IsReferenceType(Target) && Target != ValueType.Nullable && Target != ValueType.Object)
                    throw new ApplicationException("'as' operator can only be applied to reference types");
                if (v is null) return null!;
                int ptr = -1;
                ValueType type;
                if (v is string)
                {
                    type = ValueType.String;
                }
                else if (v is int ip && ip >= ctx.StackSize && ip < ctx.StackSize + ctx.MemoryUsed)
                {
                    ptr = ip;
                    type = ctx.IsArray(ip) ? ValueType.Array : ctx.GetHeapObjectType(ip);
                }
                else
                {
                    type = ExecutionContext.InferType(v);
                }
                switch (Target)
                {
                    case ValueType.String:
                        if (type != ValueType.String) return null!;
                        return v is string s ? s : ctx.ReadHeapString(ptr);
                    case ValueType.Array:
                        return type == ValueType.Array && ptr >= 0 ? ptr : null!;

                    case ValueType.Struct:
                        return type == ValueType.Struct && ptr >= 0 ? ptr : null!;

                    case ValueType.Tuple:
                        return type == ValueType.Tuple && ptr >= 0 ? ptr : null!;
                    case ValueType.Nullable:
                        if (type == ValueType.Nullable && ptr >= 0) return ptr;
                        if (!IsReferenceType(type))
                            return ctx.PackReference(v!, ValueType.Nullable);
                        return null!;
                    case ValueType.Object:
                        if (v is string s2) return ctx.PackReference(s2, ValueType.Object);
                        if (ptr >= 0 && type == ValueType.String)
                            return ctx.PackReference(ctx.ReadHeapString(ptr), ValueType.Object);
                        if (ExecutionContext.IsObject(v))
                            return ctx.PackReference(v!, ValueType.Object);
                        return null!;
                    default:
                        return null!;
                }
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── as({Target.ToString().ToLower()})");
                string child = indent + (isLast ? "    " : "│   ");
                Expr.Print(child, true);
            }
        }
        #endregion
        #region Variable nodes
        public class VariableDeclarationNode : AstNode
        {
            public string Name { get; }
            public ValueType Type { get; }
            public AstNode Expression { get; }
            public bool IsArray { get; }
            public int?[]? ArrayLength { get; }
            public bool IsConst { get; }
            public bool IsPublic { get; }
            public ValueType InnerType { get; }
            public string? CustomTypeName { get; init; }
            public GenericUse? Generic { get; init; }
            public VariableDeclarationNode(ValueType type, string name, AstNode expression, bool isArray = false,
                int?[]? arrayLength = null, bool isConst = false, bool isPublic = false, ValueType? innerType = null)
            {
                Type = type;
                Name = name.Length < 200 ? name : "_";
                Expression = expression;
                IsArray = isArray;
                ArrayLength = arrayLength;
                IsConst = isConst;
                IsPublic = isPublic;
                InnerType = innerType ?? (type is ValueType.Nullable or ValueType.IntPtr ? ValueType.Object : type);
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var value = Expression.Evaluate(context);
                if (Type == ValueType.Object && value is int pointer && pointer >= context.StackSize && pointer < context.StackSize + context.MemoryUsed)
                {
                    var type = context.GetHeapObjectType(pointer);
                    switch (type)
                    {
                        case ValueType.Struct:
                        case ValueType.Tuple:
                        case ValueType.Dictionary:
                            context.Declare(Name, type, value);
                            return null!;
                    }
                }
                if (Type == ValueType.Nullable && InnerType != Type && value is not null) value = context.Cast(value, InnerType);
                if (IsArray)
                {
                    if (value is ValueTuple<object?[], ValueType, int[]> arr3)//with pins
                    {
                        var vals = arr3.Item1;
                        var elemT = arr3.Item2 == ValueType.Object ? Type : arr3.Item2;
                        var pins = arr3.Item3;

                        if (elemT != Type) throw new ApplicationException("Type missmatch during declaration");

                        int elemSize = context.GetTypeSize(elemT);
                        int basePtr;

                        if (Ast.IsReferenceType(elemT))
                        {
                            basePtr = context.Malloc(sizeof(int) * vals.Length, elemT, isArray: true);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                int h = context.PackReference(vals[i]!, elemT);
                                BitConverter.GetBytes(h).CopyTo(context.RawMemory, basePtr + i * sizeof(int));
                            }
                        }
                        else
                        {
                            basePtr = context.Malloc(elemSize * vals.Length, elemT, isArray: true);
                            for (int i = 0; i < vals.Length; i++)
                                context.GetSourceBytes(elemT, vals[i]).CopyTo(context.RawMemory.AsSpan(basePtr + i * elemSize, elemSize));
                        }

                        foreach (var k in pins) if (k != -1) context.Unpin(k);

                        context.Declare(Name, ValueType.Array, basePtr);
                        return null!;
                    }
                    else if (value is ValueTuple<object?[], ValueType> arr)
                    {
                        object?[] vals = arr.Item1;
                        ValueType elemT = arr.Item2 == ValueType.Object ? Type : arr.Item2;
                        if (elemT != Type)
                            throw new ApplicationException("Type missmatch during declaration");
                        int elemSize = context.GetTypeSize(elemT);
                        int basePtr;
                        if (Ast.IsReferenceType(elemT))
                        {
                            basePtr = context.Malloc(sizeof(int) * vals.Length, elemT, isArray: true);

                            for (int i = 0; i < vals.Length; i++)
                            {
                                int packed = context.PackReference(vals[i]!, elemT);
                                BitConverter.GetBytes(packed).CopyTo(context.RawMemory, basePtr + i * sizeof(int));
                            }
                        }
                        else
                        {
                            int bytes = elemSize * vals.Length;
                            basePtr = context.Malloc(bytes, elemT, isArray: true);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                ReadOnlySpan<byte> src = context.GetSourceBytes(elemT, vals[i]);
                                src.CopyTo(context.RawMemory.AsSpan(basePtr + i * elemSize, elemSize));
                            }
                        }

                        context.Declare(Name, ValueType.Array, basePtr);
                        return null!;
                    }
                    else if (value is ValueTuple<int, ValueType> info)
                    {
                        if (info.Item2 != Type && info.Item2 != ValueType.Array) throw new ApplicationException($"Type missmatch during array assignment");
                        context.Declare(Name, ValueType.Array, info);
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
                    else if (ArrayLength is not null && value is not null)
                    {
                        int len = ArrayLength.All(d => d.HasValue) ? ArrayLength.Aggregate(1, (p, d) => p * d!.Value) : 0;
                        if (len * context.GetTypeSize(Type) >= context.RawMemory.Length - context.StackSize) throw new OutOfMemoryException();
                        context.Declare(Name, ValueType.Array, (len, Type));
                    }
                    else context.Declare(Name, ValueType.Array, null);
                }
                else context.Declare(Name, Type, value);
                return null!;
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
            private int _cachedAddr = -1;
            private Ast.ValueType _cachedType;
            private int _cachedVer = -1;
            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                if (TryEnsureCached(context) && !Ast.IsReferenceType(_cachedType) && _cachedType != ValueType.Reference)
                    return context.ReadFromStack(_cachedAddr, _cachedType);
                string resolved = Name;
                if (!context.HasVariable(resolved))
                {
                    string ns = context.CurrentNameSpace ?? string.Empty;

                    while (ns.Length > 0)
                    {
                        string candidate = $"{ns}.{Name}";
                        if (context.HasVariable(candidate))
                        {
                            resolved = candidate;
                            goto leave;
                        }
                        int lastDot = ns.LastIndexOf('.');
                        ns = lastDot < 0 ? string.Empty : ns.Substring(0, lastDot);
                    }
                    foreach (var u in context.Usings)
                    {
                        string candidate = $"{u}.{Name}";
                        if (context.HasVariable(candidate)) { resolved = candidate; break; }
                    }
                }
            leave:
                if (!context.HasVariable(resolved) && context.HasVariable("this") && context.Get("this").Type == Ast.ValueType.Struct)
                {
                    int instPtr = Convert.ToInt32(context.ReadVariable("this"));
                    try
                    {
                        int off = context.GetStructFieldOffset(instPtr, Name, out var vt) + 1;
                        return Ast.IsReferenceType(vt)
                            ? context.DerefReference(instPtr + off, vt)!
                            : context.ReadFromStack(instPtr + off, vt);
                    }
                    catch
                    { }
                }
                var v = context.Get(resolved);
                if (v.Type == ValueType.Reference)
                {
                    int addr = Convert.ToInt32(context.ReadVariable(resolved));
                    if (addr <= 0) return null!;
                    return context.ReadByAddress(addr)!;
                }
                if (IsReferenceType(v.Type))
                {
                    int ptr = Convert.ToInt32(context.ReadVariable(resolved));
                    if (ptr == -1) return null!;
                    if (v.Type == ValueType.String)
                    {
                        var span = context.GetSpan(ptr);
                        int len = span.IndexOf((byte)0);
                        if (len < 0) len = span.Length;
                        return context.ReadHeapString(span.Slice(0, len));
                    }
                    if (v.Type == ValueType.Nullable)
                    {
                        var baseType = (ValueType)context.RawMemory[ptr];
                        int size = context.GetTypeSize(baseType);
                        return context.ReadFromMemorySlice(context.RawMemory[(ptr + 1)..(ptr + 1 + size)], baseType);
                    }
                    switch (v.Type)
                    {
                        case ValueType.Array:
                        case ValueType.Tuple:
                        case ValueType.Struct:
                        case ValueType.Class:
                        case ValueType.Dictionary:
                            return ptr;
                    }
                    if (v.Type == ValueType.Object) return BitConverter.ToInt32(context.GetSpan(ptr));
                    throw new ApplicationException($"Reading object: {v.Type}");
                }
                return context.ReadVariable(resolved);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool TryEnsureCached(ExecutionContext ctx)
            {
                if (_cachedVer == ctx.ScopeVersion && _cachedAddr >= 0) return true;

                string resolved = Name;
                var v = ctx.Get(resolved);
                _cachedAddr = v.Address;
                _cachedType = v.Type;
                _cachedVer = ctx.ScopeVersion;
                return true;
            }
            internal bool TryGetCache(ExecutionContext ctx, out int address, out ValueType type)
            {
                TryEnsureCached(ctx);
                address = _cachedAddr;
                type = _cachedType;
                return address >= 0;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── var {Name}");
            }
        }
        public class NewArrayNode : AstNode
        {
            public ValueType ElementType { get; }
            public AstNode[] LengthExprs { get; }
            public int Rank { get; }
            public NewArrayNode(ValueType elem, AstNode[] len, int rank = 1)
            {
                ElementType = elem;
                LengthExprs = len;
                Rank = Math.Max(1, rank);
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                if (Rank == 1)
                {
                    var len = LengthExprs[0].Evaluate(context);

                    if (len is int i)
                    {
                        int size = checked(i * context.GetTypeSize(ElementType));
                        if (size >= context.RawMemory.Length - context.StackSize) throw new OutOfMemoryException();
                        return (i, ElementType);
                    }
                    else return null!;
                }
                if (LengthExprs.Length == 0)
                    throw new ApplicationException("At least the first dimension length must be specified for jagged arrays.");
                int[] dims = new int[LengthExprs.Length];
                for (int i = 0; i < dims.Length; i++)
                {
                    int v = Convert.ToInt32(LengthExprs[i].Evaluate(context));
                    if (v < 0) throw new ApplicationException("Array length must be non-negative");
                    dims[i] = v;
                }
                int Allocate(ValueType leaf, ReadOnlySpan<int> known, int ranksLeft)
                {
                    if (ranksLeft == 1)
                    {
                        if (known.Length < 1) throw new ApplicationException("Internal: missing leaf length");
                        int n = known[0];
                        int elemSize = context.GetTypeSize(leaf);
                        int bytes = checked(n * elemSize);
                        int p = context.Malloc(bytes, leaf, isArray: true);
                        if (Ast.IsReferenceType(leaf))
                            context.RawMemory.AsSpan(p, bytes).Fill(0xFF);
                        else
                            context.RawMemory.AsSpan(p, bytes).Clear();
                        return p;
                    }
                    if (known.Length < 1) throw new ApplicationException("Internal: missing dimension length");
                    int len = known[0];
                    int top = context.Malloc(len * sizeof(int), ValueType.Array, isArray: true);
                    context.RawMemory.AsSpan(top, len * sizeof(int)).Fill(0xFF);
                    if (known.Length >= 2)
                    {
                        for (int i = 0; i < len; i++)
                        {
                            int child = Allocate(leaf, known.Slice(1), ranksLeft - 1);
                            BitConverter.GetBytes(child).CopyTo(context.RawMemory, top + i * sizeof(int));
                        }
                    }
                    return top;
                }
                if (LengthExprs.Length == Rank)
                    return Allocate(ElementType, dims, Rank);
                return Allocate(ElementType, dims.AsSpan(0, 1), Rank);
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── new {ElementType.ToString().ToLower()}[{(Rank > 1 ? $"rank:{Rank}" : " ")}]");
                foreach (var len in LengthExprs)
                    len.Print(indent + (last ? "    " : "│   "), true);
            }
        }
        public class ArrayIndexNode : AstNode
        {
            public AstNode ArrayExpr { get; }
            public AstNode IndexExpr { get; }
            public bool FromEnd { get; }
            public bool IsNullConditional { get; }
            public ArrayIndexNode(AstNode arrayExpr, AstNode indexExpr, bool fromEnd = false, bool isNullConditional = false)
            {
                ArrayExpr = arrayExpr;
                IndexExpr = indexExpr;
                FromEnd = fromEnd;
                IsNullConditional = isNullConditional;
            }
            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                object arr = ArrayExpr.Evaluate(context);
                if (arr is null)
                {
                    if (IsNullConditional) return null!;
                    throw new ApplicationException("Null reference in array index");
                }
                if (arr is string s)
                {
                    int idx = Convert.ToInt32(IndexExpr.Evaluate(context));
                    if (FromEnd) idx = s.Length - idx;
                    if (idx < 0 || idx >= s.Length)
                        throw new IndexOutOfRangeException($"Index out of range: index {idx} length {s.Length}");
                    return s[idx];
                }

                int basePtr = Convert.ToInt32(arr);
                var heapType = context.IsArray(basePtr) ? ValueType.Array : context.GetHeapObjectType(basePtr);
                if (heapType == ValueType.Dictionary)
                {
                    var keyObj = IndexExpr.Evaluate(context);
                    if (FromEnd) throw new ApplicationException("Index from end is not applicable to Dictionary");
                    return context.DictionaryGet(basePtr, keyObj)!;
                }
                int i = Convert.ToInt32(IndexExpr.Evaluate(context));
                if (FromEnd) i = context.GetArrayLength(basePtr) - i;
                int addr = ElementAddress(context, basePtr, i, out var vt);
                if (IsReferenceType(vt))
                {
                    int handle = BitConverter.ToInt32(context.RawMemory, addr);

                    return vt switch
                    {
                        ValueType.String => handle < 0 ? handle : context.ReadHeapString(handle),
                        ValueType.Object => handle,
                        _ => handle
                    };
                }
                else return context.ReadFromStack(addr, vt);
            }
            public void Write(ExecutionContext context, object value)
            {
                object arr = ArrayExpr.Evaluate(context);

                if (arr is null)
                {
                    if (IsNullConditional) return;
                    throw new ApplicationException("Null reference on indexed assignment");
                }

                int basePtr = Convert.ToInt32(arr);
                var heapType = context.IsArray(basePtr) ? ValueType.Array : context.GetHeapObjectType(basePtr);
                if (heapType == ValueType.Dictionary)
                {
                    var keyObj = IndexExpr.Evaluate(context);
                    int newPtr = context.DictionarySet(basePtr, keyObj, value);
                    if (ArrayExpr is VariableReferenceNode vr && newPtr != basePtr)
                        context.WriteVariable(vr.Name, newPtr);
                    return;
                }
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
                var incoming = ExecutionContext.InferType(value);
                if (incoming == ValueType.Int && value is int p && p >= context.StackSize && p < context.StackSize + context.MemoryUsed)
                {
                    var heapT = context.GetHeapObjectType(p);
                    if (heapT == vt || (vt == ValueType.Object && heapT != ValueType.String))
                        incoming = heapT;
                }
                if (incoming != context.GetHeapObjectType(basePtr))
                    throw new ApplicationException($"Type missmatch during indexing: {context.GetHeapObjectType(basePtr)} -> {ExecutionContext.InferType(value)}");

                if (Ast.IsReferenceType(vt))
                {
                    int handle = context.PackReference(value, vt);
                    BitConverter.GetBytes(handle).CopyTo(context.RawMemory, addr);
                    return;
                }

                ReadOnlySpan<byte> src = context.GetSourceBytes(value);
                context.ValidateAddress(addr, src.Length);
                src.CopyTo(context.RawMemory.AsSpan(addr, src.Length));
            }
            int ElementAddress(ExecutionContext ctx, int basePtr, int index, out ValueType elemType)
            {
                elemType = ctx.GetHeapObjectType(basePtr);
                int elemSize = ctx.GetTypeSize(elemType);
                int length = ctx.GetArrayLength(basePtr);

                if (index < 0 || index >= length)
                    throw new IndexOutOfRangeException($"Index out of range: index {index} length {length}");

                return basePtr + index * elemSize;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── index{(IsNullConditional ? "?" : "")}");
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

                var values = new object?[Items.Length];
                if (Ast.IsReferenceType(ElementType))
                {
                    var pins = new int[Items.Length];
                    for (int i = 0; i < Items.Length; i++)
                    {
                        var v = Items[i].Evaluate(context);
                        if (v is int addr && addr >= context.StackSize)
                            pins[i] = context.Pin(addr);
                        else
                            pins[i] = -1;

                        values[i] = v;
                    }

                    return (values, ElementType, pins);
                }
                else
                {
                    for (int i = 0; i < Items.Length; i++)
                    {
                        values[i] = Items[i].Evaluate(context);
                    }

                    return (values, ElementType);
                }

            }

            public override void Print(string ind = "", bool last = true)
            {
                Console.WriteLine($"{ind}└── literal-array ({ElementType.ToString().ToLower()})");
                string child = ind + (last ? "    " : "│   ");
                foreach (var (n, i) in Items.Select((n, idx) => (idx, n)))
                    i.Print(child, n == Items.Length - 1);
            }
        }
        public sealed class CollectionExpressionNode : Ast.AstNode
        {
            public Ast.AstNode[] Items { get; }
            public CollectionExpressionNode(Ast.AstNode[] items) =>
                Items = items ?? Array.Empty<Ast.AstNode>();

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                if (Items.Length == 0)
                    return (Array.Empty<object?>(), ValueType.Object);

                var values = new object?[Items.Length];
                ValueType elemT = ValueType.Object;
                bool hasCandidate = false;
                for (int i = 0; i < Items.Length; i++)
                {
                    object? v = Items[i].Evaluate(context);
                    values[i] = v;

                    ValueType vt;
                    if (v is null) { vt = ValueType.Object; }
                    else if (v is string) { vt = ValueType.String; }
                    else if (v is int p && p >= context.StackSize)
                    {
                        // Heap handle: derive the heap object type (Array/Tuple/Struct/etc.)
                        vt = context.GetHeapObjectType(p);
                    }
                    else
                    {
                        vt = ExecutionContext.InferType(v);
                    }

                    if (!hasCandidate && vt != ValueType.Object)
                    {
                        elemT = vt; hasCandidate = true;
                    }
                    else if (vt != ValueType.Object)
                    {
                        if (elemT != vt)
                        {
                            if (IsNumeric(elemT) && IsNumeric(vt))
                            {
                                elemT = (vt == ValueType.Double || elemT == ValueType.Double
                                         || vt == ValueType.Float || elemT == ValueType.Float
                                         || vt == ValueType.Decimal || elemT == ValueType.Decimal)
                                            ? ValueType.Double
                                            : (vt == ValueType.Long || elemT == ValueType.Long
                                               || vt == ValueType.Ulong || elemT == ValueType.Ulong)
                                                ? ValueType.Long
                                                : ValueType.Int;
                            }
                            else
                            {
                                elemT = ValueType.Object;
                            }
                        }
                    }

                }
                return (values, elemT);
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── collection [] ({Items.Length} items)");
            }
        }
        public sealed class TupleLiteralNode : AstNode
        {
            public readonly (string? name, AstNode expr)[] Items;
            public TupleLiteralNode((string? name, AstNode expr)[] items) => Items = items;

            public override object Evaluate(ExecutionContext context)
            {
                var slots = new List<(ValueType t, object? v, int namePtr)>(Items.Length);

                foreach (var (name, ex) in Items)
                {
                    context.Check();
                    object? val = ex.Evaluate(context);
                    ValueType et;
                    if (val is null) et = ValueType.Object;
                    else if (val is string) et = ValueType.String;
                    else if (val is int p && p >= context.StackSize) et = context.GetHeapObjectType(p);
                    else et = ExecutionContext.InferType(val);

                    int namePtr = name is null ? -1 : context.PackReference(name, ValueType.String);

                    slots.Add((et, val, namePtr));
                }

                return context.AllocateTuple(slots);
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── tuple");
                string ci = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Items.Length; i++)
                    Items[i].expr.Print(ci, i == Items.Length - 1);

            }
        }
        public class LiteralNode : AstNode
        {
            public object? Value { get; }

            public LiteralNode(object? value) => Value = value;

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                return Value!;
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

        public sealed class TupleTargetNode : AstNode
        {
            public readonly string[] Names;
            public TupleTargetNode(IEnumerable<string> names) => Names = names.ToArray();
            public override object Evaluate(ExecutionContext ctx) =>
                throw new InvalidOperationException("TupleTargetNode must not be evaluated directly");
            public override void Print(string ind = "", bool last = true)
                => Console.WriteLine($"{ind}└── tuple-target ({string.Join(", ", Names)})");
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
                    var cond = Condition.Evaluate(context);
                    if (!(cond is bool bb && bb)) break;
                    object? res;
                    if (Body is BlockNode)
                    {
                        res = Body.Evaluate(context);
                    }
                    else
                    {
                        context.EnterScope();
                        try
                        {
                            res = Body.Evaluate(context);
                        }
                        finally { context.ExitScope(); }
                    }
                    switch (res)
                    {
                        case BreakSignal: break;
                        case ContinueSignal: continue;
                        case GotoCaseSignal:
                        case ReturnSignal: return res;
                    }
                }

                return null!;
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
                    object? res;
                    if (Body is BlockNode)
                    {
                        res = Body.Evaluate(context);
                    }
                    else
                    {
                        context.EnterScope();
                        try
                        {
                            res = Body.Evaluate(context);
                        }
                        finally { context.ExitScope(); }
                    }
                    if (res is BreakSignal) break;
                    if (res is ContinueSignal) { }
                    if (res is ReturnSignal or GotoCaseSignal) return res;
                }
                while (Condition.Evaluate(context) is bool b && b);

                return null!;
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
                        object? res;
                        if (Body is BlockNode)
                        {
                            res = Body.Evaluate(context);
                        }
                        else
                        {
                            context.EnterScope();
                            try
                            {
                                res = Body.Evaluate(context);
                            }
                            finally
                            {
                                context.ExitScope();
                            }
                        }
                        if (res is BreakSignal) break;
                        if (res is ContinueSignal) { Step?.Evaluate(context); continue; }
                        if (res is ReturnSignal or GotoCaseSignal) return res;
                        Step?.Evaluate(context);

                    }
                }
                finally { context.ExitScope(); }

                return null!;
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
            public string[] Names { get; }
            public ValueType[]? DeclaredTypes { get; }
            public AstNode CollectionExpr { get; }
            public AstNode Body { get; }

            public ForeachNode(string[] names, ValueType[]? types, AstNode collectionExpr, AstNode body)
            {
                Names = names;
                DeclaredTypes = types;
                CollectionExpr = collectionExpr;
                Body = body;
            }

            public override object Evaluate(ExecutionContext context)
            {
                var collection = CollectionExpr.Evaluate(context);
                bool tupleGenerated = false;
                IEnumerable<object?> items;
                switch (collection)
                {
                    case List<int> li:
                        items = li.Cast<object?>();
                        break;
                    case List<string> ls:
                        items = ls.Cast<object?>();
                        break;
                    case (object?[] arr, _):
                        items = arr;
                        break;
                    case ValueTuple<object?[], Ast.ValueType, int[]> t3:
                        items = t3.Item2 == Ast.ValueType.String
                            ? t3.Item1.Select(v => v is int h && h > 0 ? context.ReadHeapString(h) : v)
                            : t3.Item1;
                        break;
                    case ValueTuple<object?[], Ast.ValueType> t2:
                        items = t2.Item1;
                        break;
                    case int ptr when ptr >= context.StackSize && ptr < context.StackSize + context.MemoryUsed:
                        {
                            var heapType = context.IsArray(ptr) ? Ast.ValueType.Array : context.GetHeapObjectType(ptr);
                            if (heapType == Ast.ValueType.Dictionary)
                            {
                                var (keyT, valT) = context.GetDictionaryElementTypes(ptr);
                                int ks = Ast.IsReferenceType(keyT) ? sizeof(int) : context.GetTypeSize(keyT);
                                int vs = Ast.IsReferenceType(valT) ? sizeof(int) : context.GetTypeSize(valT);
                                int count = context.GetDictionaryCount(ptr);
                                int keyNamePtr = context.PackReference("Key", Ast.ValueType.String);
                                int valNamePtr = context.PackReference("Value", Ast.ValueType.String);
                                var list = new List<object?>(count);
                                int basePos = ptr + 2;
                                int step = ks + vs;
                                for (int i = 0; i < count; i++)
                                {
                                    int pos = basePos + i * step;
                                    object? key = Ast.IsReferenceType(keyT)
                                        ? BitConverter.ToInt32(context.RawMemory, pos)
                                        : context.ReadFromStack(pos, keyT);
                                    int vaddr = pos + ks;
                                    object? val = Ast.IsReferenceType(valT)
                                        ? BitConverter.ToInt32(context.RawMemory, vaddr)
                                        : context.ReadFromStack(vaddr, valT);

                                    int tuplePtr = context.AllocateTuple(new List<(Ast.ValueType t, object? v, int namePtr)>
                                    { (keyT, key, keyNamePtr), (valT, val, valNamePtr) });

                                    list.Add(tuplePtr);
                                }
                                items = list;
                                tupleGenerated = true;
                            }
                            else
                            {
                                int len = context.GetArrayLength(ptr);
                                if (heapType == Ast.ValueType.String)
                                {
                                    items = Enumerable.Range(0, len).Select(i =>
                                    {
                                        int h = BitConverter.ToInt32(context.RawMemory, ptr + i * sizeof(int));
                                        return h > 0 ? context.ReadHeapString(h) : null;
                                    });
                                }
                                else if (Ast.IsReferenceType(heapType))
                                {
                                    items = Enumerable.Range(0, len).Select(i =>
                                        (object?)BitConverter.ToInt32(context.RawMemory, ptr + i * sizeof(int)));
                                }
                                else
                                {
                                    int elemSize = context.GetTypeSize(heapType);
                                    items = Enumerable.Range(0, len).Select(i =>
                                        context.ReadFromStack(ptr + i * elemSize, heapType));
                                }
                            }
                        }
                        break;
                    case string s:
                        items = s.ToCharArray().Cast<object?>();
                        break;
                    default: throw new NotSupportedException("Unsupported source in foreach");
                }
                foreach (var item in items)
                {
                    context.Check();
                    context.EnterScope();
                    try
                    {
                        if (Names.Length == 1)
                        {
                            var vt = DeclaredTypes is null ? ValueType.Object : DeclaredTypes[0];
                            if (vt == ValueType.Object && tupleGenerated) vt = ValueType.Tuple;
                            context.Declare(Names[0], vt, item);
                        }
                        else
                        {
                            if (DeclaredTypes is not null)
                            {
                                for (int i = 0; i < Names.Length; i++)
                                {
                                    var name = Names[i];
                                    if (name == "_") continue;
                                    var tdecl = DeclaredTypes[i];
                                    var def = ExecutionContext.DefaultValue(tdecl);
                                    context.Declare(name, tdecl, def);
                                }
                            }
                            context.DeconstructAssign(Names, item);
                        }
                        var res = Body.Evaluate(context);
                        if (res is BreakSignal) break;
                        if (res is ContinueSignal) continue;
                        if (res is ReturnSignal or GotoCaseSignal) return res;

                    }
                    finally { context.ExitScope(); }
                }

                return null!;

            }

            public override void Print(string indent = "", bool isLast = true)
            {
                string hdr;
                if (Names.Length == 1)
                {
                    var t = DeclaredTypes is null ? "var" : DeclaredTypes[0].ToString().ToLowerInvariant();
                    hdr = $"foreach {t} {Names[0]}";
                }
                else
                {
                    if (DeclaredTypes is null)
                        hdr = $"foreach var ({string.Join(", ", Names)})";
                    else
                    {
                        var parts = Names.Select((n, i) => $"{DeclaredTypes[i].ToString().ToLowerInvariant()} {n}");
                        hdr = $"foreach ({string.Join(", ", parts)})";
                    }
                }
                Console.WriteLine($"{indent}└── {hdr}");
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
                    if (res is ReturnSignal or BreakSignal or ContinueSignal or GotoCaseSignal)
                        return res;
                }
                else if (ElseBody is not null)
                {
                    var res = ElseBody.Evaluate(context);
                    if (res is ReturnSignal or BreakSignal or ContinueSignal or GotoCaseSignal)
                        return res;
                }
                return null!;
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
                    if (ex is OperationCanceledException or OutOfMemoryException or StackOverflowException)
                        throw;
                    context.EnterScope();
                    try
                    {
                        if (ExVar != null)
                            context.Declare(ExVar, ValueType.String, ex.Message);
                        try
                        {
                            var res = CatchBlock.Evaluate(context);
                            return res;
                        }
                        catch (RethrowException)
                        {
                            throw ex;
                        }
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
            public List<(PatternNode? pattern, AstNode? value, List<AstNode> body)> Cases;

            public SwitchNode(AstNode disc, List<(PatternNode? pattern, AstNode? value, List<AstNode> body)> cases)
            {
                Discriminant = disc;
                Cases = cases;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var disc = Discriminant.Evaluate(context);

                var constValues = new object?[Cases.Count];
                int defaultIndex = -1;
                for (int j = 0; j < Cases.Count; j++)
                {
                    var (pat, val, _) = Cases[j];
                    if (pat is null && val is null)
                    {
                        defaultIndex = j;
                        constValues[j] = null;
                    }
                    else if (val is not null)
                    {
                        constValues[j] = val.Evaluate(context);
                    }
                }

                int FindMatch(object? v)
                {
                    for (int k = 0; k < Cases.Count; k++)
                    {
                        var (pat, val, _) = Cases[k];
                        if (pat is not null)
                        {
                            bool matched;
                            context.EnterScope();
                            try { matched = pat.Match(v, context); }
                            finally { context.ExitScope(); }
                            if (matched) return k;
                        }
                        else if (val is not null)
                        {
                            if (Equals(constValues[k], v)) return k;
                        }
                    }
                    return -1;
                }

                int i = FindMatch(disc);
                if (i < 0) i = defaultIndex;
                if (i < 0) return null!;

                NEXT_CASE:
                while (i < Cases.Count)
                {
                    var (pat, _, body) = Cases[i];
                    context.EnterScope();
                    try
                    {
                        if (pat is not null)
                        {
                            _ = pat.Match(disc, context);
                        }

                        for (int j = 0; j < body.Count; j++)
                        {
                            var res = body[j].Evaluate(context);
                            switch (res)
                            {
                                case BreakSignal: return null!;
                                case ContinueSignal:
                                case ReturnSignal: return res;
                                case GotoCaseSignal g:
                                    {
                                        int target = g.IsDefault ? defaultIndex : FindMatch(g.Value);
                                        if (target < 0)
                                            throw new ApplicationException("goto case: target not found");
                                        i = target;
                                        goto NEXT_CASE;
                                    }
                            }
                        }
                    }
                    finally
                    {
                        context.ExitScope();
                    }

                    i++;
                }

                return null!;
            }


            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── switch");
                Discriminant.Print(indent + "    ");
                string childIndent = indent + (isLast ? "    " : "│   ");

                for (int i = 0; i < Cases.Count; i++)
                {
                    var (pat, val, body) = Cases[i];
                    if (pat is not null)
                        pat.Print(childIndent, false);
                    else if (val is not null)
                        val.Print(childIndent, false);
                    else
                        Console.WriteLine($"{childIndent}└── default");

                    var bi = childIndent + (i == Cases.Count - 1 ? "    " : "│   ");
                    foreach (var stmt in body)
                        stmt.Print(bi, false);
                }
            }
        }
        public class ThrowNode : AstNode
        {
            public AstNode? Expr;
            public ThrowNode(AstNode? expr) => Expr = expr;

            public override object Evaluate(ExecutionContext context)
            {
                if (Expr is null) //rethrow
                {
                    throw new RethrowException();
                }
                context.Check();
                var msg = Expr.Evaluate(context)?.ToString();
                throw new Exception(msg);
            }
            public override void Print(string indent = "", bool isLast = true)
            { Console.WriteLine($"{indent}└── throw"); Expr?.Print(indent + (isLast ? "    " : "│   ")); }
        }
        public class LabelNode : AstNode
        {
            public string Name { get; }

            public LabelNode(string name) => Name = name;

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                return null!;
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
        public sealed class GotoCaseNode : AstNode
        {
            public AstNode? Expr;
            public bool IsDefault;
            public GotoCaseNode(AstNode? expr, bool isDefault)
            { Expr = expr; IsDefault = isDefault; }

            public override object Evaluate(ExecutionContext context)
            {
                var target = IsDefault ? null : Expr!.Evaluate(context);
                return new GotoCaseSignal(target, IsDefault);
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine(IsDefault ? $"{indent}└── goto default" : $"{indent}└── goto case ...");
            }
        }
        #region Signals
        public sealed class RethrowException : Exception
        {
            public RethrowException() : base("rethrow outside of catch") { }
        }
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
        public sealed class GotoCaseSignal
        {
            public readonly object? Value;
            public readonly bool IsDefault;
            public GotoCaseSignal(object? value, bool isDefault)
            {
                Value = value; IsDefault = isDefault;
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

                return null!;
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
            private readonly bool _requiresScope;
            public BlockNode(List<AstNode> statements, bool forceScope = false)
            {
                Statements = statements;
                _requiresScope = forceScope || AnalyzeRequiresScope(Statements);
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                if (!_requiresScope)
                {
                    foreach (var statement in Statements)
                    {
                        var res = statement.Evaluate(context);
                        if (res is ReturnSignal or BreakSignal or ContinueSignal or GotoCaseSignal)
                            return res;
                    }
                    return null!;
                }
                context.EnterScope();
                try
                {
                    object? result = null;
                    foreach (var statement in Statements)
                    {
                        //context.Check();
                        var res = statement.Evaluate(context);
                        if (res is ReturnSignal or BreakSignal or ContinueSignal or GotoCaseSignal) return res;
                    }
                    return result!;
                }
                finally
                {
                    context.ExitScope();
                }

            }
            private static bool AnalyzeRequiresScope(IEnumerable<AstNode> stmts)
            {
                foreach (var s in stmts)
                {
                    if (IsDeclarationLike(s)) return true;
                    if (ContainsPatternBinding(s)) return true;
                    if (NeedsBlockForUsing(s)) return true;
                    if (!IsScopeFreeStatement(s)) return true;
                }
                return false;
            }
            private static bool IsDeclarationLike(AstNode n)
            {
                if (n is VariableDeclarationNode) return true;

                if (n is StatementListNode sl)
                    return sl.Statements.Any(IsDeclarationLike);

                return false;
            }
            private static bool NeedsBlockForUsing(AstNode n)
            {
                if (n is UsingNode u && u.Body is not null)
                    return IsDeclarationLike(u.Declaration);
                return false;
            }
            private static bool ContainsPatternBinding(AstNode n)
            {
                var stack = new Stack<AstNode>();
                stack.Push(n);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (cur is DeclarationPatternNode) return true;
                    if (cur is IsPatternNode) { }
                    foreach (var child in Parser.EnumerateChildren(cur))
                        stack.Push(child);
                }
                return false;
            }
            private static bool IsScopeFreeStatement(AstNode n)
            {
                if (n is EmptyNode) return true;
                if (n is BlockNode) return true;
                if (n is BreakNode or ContinueNode or GotoNode or GotoCaseNode or ReturnNode)
                    return true;
                if (n is BinOpNode b)
                    return IsSafeAssignment(b);
                if (n is UnaryOpNode u)
                    return IsSafeUnary(u);
                return false;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsSafeAssignment(BinOpNode b)
            {
                if (!IsSafeLValue(b.Left)) return false;
                if (!IsAssignmentLikeOperator(b.Op)) return false;
                return !ContainsPatternBinding(b.Right);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsSafeUnary(UnaryOpNode u)
            {
                if (!IsIncDec(u.Op)) return false;
                return IsSafeLValue(u.Operand) && !ContainsPatternBinding(u.Operand);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsSafeLValue(AstNode n) => n is VariableReferenceNode || n is ArrayIndexNode || n is UnresolvedReferenceNode;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsIncDec(OperatorToken op)
                => op is OperatorToken.Increment or OperatorToken.Decrement;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsAssignmentLikeOperator(OperatorToken op) =>
                op is OperatorToken.Equals
              or OperatorToken.PlusEqual
              or OperatorToken.MinusEqual
              or OperatorToken.MultiplyEqual
              or OperatorToken.DivideEqual
              or OperatorToken.ModuleEqual
              or OperatorToken.LeftShiftEqual
              or OperatorToken.RightShiftEqual
              or OperatorToken.UnsignedRightShiftEqual
              or OperatorToken.BitAndEqual
              or OperatorToken.BitOrEqual
              or OperatorToken.BitXorEqual
              or OperatorToken.NullDefaultEqual;

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
            public override object Evaluate(ExecutionContext ctx) => null!;
            public override void Print(string i = "", bool l = true)
                => Console.WriteLine($"{i}└── empty");
        }
        public sealed class MissingNode : Ast.AstNode
        {
            public readonly ParseException Exception;
            public readonly int Position;
            public MissingNode(ParseException exception, int position)
            {
                Exception = exception;
                Position = position;
            }

            public override object Evaluate(Ast.ExecutionContext _)
            {
                return null!;
            }

            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── <exception {Exception}> @{Position}");
            }

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

            public override object Evaluate(ExecutionContext context)
            {
                if (Body is not null)
                {
                    var disp = Declaration.Evaluate(context);
                    context.EnterScope();
                    try
                    {
                        Declaration.Evaluate(context);
                        Body.Evaluate(context);
                        void TryDispose(string varName)
                        {
                            if (!context.HasVariable(varName)) return;
                            var info = context.Get(varName);
                            if (info.Type != Ast.ValueType.Object) return;

                            var ptrObj = Convert.ToInt32(context.ReadVariable(varName));
                            if (ptrObj < context.StackSize || ptrObj >= context.StackSize + context.MemoryUsed) return;

                            int handleId = BitConverter.ToInt32(context.RawMemory, ptrObj);
                            if (handleId == -1) return;// null

                            var obj = context.GetObject(handleId);
                            if (obj is IDisposable d)
                            {
                                d.Dispose();
                            }
                        }
                        switch (Declaration)
                        {
                            case VariableDeclarationNode vd:
                                TryDispose(vd.Name);
                                break;

                            case StatementListNode sl:
                                foreach (var node in sl.Statements)
                                    if (node is VariableDeclarationNode vd)
                                        TryDispose(vd.Name);
                                break;
                            default: break;
                        }
                        return null!;
                    }
                    finally { context.ExitScope(); }
                }
                string ns;
                switch (Declaration)
                {
                    case VariableReferenceNode vr: ns = vr.Name; break;
                    case UnresolvedReferenceNode ur: ns = string.Join(".", ur.Parts); break;
                    default: throw new ApplicationException("Invalid using directive");
                }
                if (ns.Length < 200 && context.Usings.Count < 200) context.Usings.Add(ns);
                else throw new ApplicationException("using directive is too large");
                return null!;
            }

            public override void Print(string indent = "", bool l = true) =>
                Console.WriteLine($"{indent}└── using");
        }
        public class NamespaceDeclarationNode : Ast.AstNode
        {
            public string FullName { get; }
            public AstNode[] Members { get; }

            public NamespaceDeclarationNode(string fullName, IEnumerable<AstNode> members)
            {
                FullName = fullName;
                Members = members.ToArray();
            }

            public override object Evaluate(ExecutionContext ctx)
            {
                string buff = ctx.CurrentNameSpace;
                ctx.CurrentNameSpace = string.IsNullOrEmpty(buff) ? FullName : $"{buff}.{FullName}";
                foreach (var m in Members) m.Evaluate(ctx);
                ctx.CurrentNameSpace = buff;
                return null!;
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── namespace {FullName}");
                var childIndent = indent + (last ? "    " : "│   ");
                for (int i = 0; i < Members.Length; i++)
                    Members[i].Print(childIndent, i == Members.Length - 1);
            }
        }
        public class UnresolvedReferenceNode : AstNode
        {
            public AstNode? Target { get; }
            public List<string> Parts { get; }
            public bool IsNullConditional { get; }
            public UnresolvedReferenceNode(List<string> parts, bool isNullConditional = false, AstNode? root = null)
            { Target = root; Parts = parts; IsNullConditional = isNullConditional; }
            public override object Evaluate(ExecutionContext context)
            {
                if (Target is not null)
                {
                    context.Check();
                    object? current = Target.Evaluate(context);
                    for (int i = 0; i < Parts.Count; i++)
                    {
                        if (current is null)
                        {
                            if (IsNullConditional) return null!;
                            throw new ApplicationException("Null reference");
                        }
                        string member = Parts[i];
                        if (current is string s) current = context.PackReference(s, ValueType.String);
                        if (current is int ptr && ptr >= context.StackSize && ptr < context.StackSize + context.MemoryUsed)
                        {
                            var ht = context.GetHeapObjectType(ptr);

                            if (ht == ValueType.Struct)
                            {
                                int off = context.GetStructFieldOffset(ptr, member, out var vt) + 1;
                                current = IsReferenceType(vt)
                                         ? context.DerefReference(ptr + off, vt)!
                                         : context.ReadFromStack(ptr + off, vt);
                                continue;
                            }
                            if (ht == ValueType.Tuple)
                            {
                                var items = context.ReadTuple(ptr);
                                if (member.Length >= 5 && member.StartsWith("Item", StringComparison.Ordinal)
                                    && int.TryParse(member.AsSpan(4), out int idx) && idx >= 1 && idx <= items.Count)
                                {
                                    current = items[idx - 1].value!;
                                    continue;
                                }
                                bool found = false;
                                for (int k = 0; k < items.Count; k++)
                                {
                                    if (items[k].namePtr >= context.StackSize)
                                    {
                                        string name = context.ReadHeapString(items[k].namePtr);
                                        if (string.Equals(name, member, StringComparison.Ordinal))
                                        {
                                            current = items[k].value!;
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                                if (!found) throw new ApplicationException($"Tuple has no member '{member}'");
                                continue;
                            }
                            if (context.NativeFunctions.ContainsKey(member))
                            {
                                var call = new CallNode(member, new AstNode[] { new LiteralNode(ptr) });
                                current = call.Evaluate(context);
                                continue;
                            }

                            throw new ApplicationException($"No native '{member}' for expression");
                        }
                        else
                        {
                            if (context.NativeFunctions.ContainsKey(member))
                            {
                                var call = new CallNode(member, new AstNode[] { new LiteralNode(current) });
                                current = call.Evaluate(context);
                                continue;
                            }
                            throw new ApplicationException($"Member '{member}' is not available on value");
                        }
                    }
                    return current!;
                }
                if (Parts.Count >= 2)
                {
                    string varName = Parts[0];
                    for (int i = 1; i < Parts.Count - 1; i++)
                    {
                        varName += $".{Parts[i]}";
                    }
                    if (context.HasVariable(varName))
                    {
                        string member = Parts[Parts.Count - 1];
                        var info = context.Get(varName);
                        if (IsNullConditional && IsReferenceType(info.Type) && Convert.ToInt32(context.ReadVariable(varName)) <= 0)
                        {
                            return null!;
                        }
                        if (context.NativeFunctions.ContainsKey(member))
                        {
                            var call = new CallNode(member, new AstNode[] { new VariableReferenceNode(varName) });
                            return call.Evaluate(context);
                        }
                        object target = context.ReadVariable(varName);
                        if (info.Type == ValueType.Struct)
                        {
                            int ptr = Convert.ToInt32(target);
                            int off = context.GetStructFieldOffset(ptr, member, out var vt) + 1;
                            return IsReferenceType(vt) ? context.DerefReference(ptr + off, vt)! : context.ReadFromStack(ptr + off, vt);
                        }
                        if (info.Type == ValueType.Tuple)
                        {
                            var items = context.ReadTuple(Convert.ToInt32(target));
                            if (member.Length >= 5 && member.StartsWith("Item")
                                && int.TryParse(member.AsSpan(4), out int idx) && idx >= 1 && idx <= items.Count)
                            {
                                return items[idx - 1].value!;
                            }
                            for (int i = 0; i < items.Count; i++)
                            {
                                if (items[i].namePtr >= context.StackSize)
                                {
                                    string name = context.ReadHeapString(items[i].namePtr);
                                    if (string.Equals(name, member))
                                        return items[i].value!;
                                }
                            }
                            throw new ApplicationException($"Tuple '{varName}' has no member '{member}'");
                        }
                        throw new ApplicationException($"No native function '{member}' for variable '{varName}' found");

                    }
                }

                string fullName = string.Join('.', Parts);
                if (context.HasVariable(fullName))
                    return context.ReadVariable(fullName);
                if (context.NativeFunctions.ContainsKey(fullName))
                    return new CallNode(fullName, Array.Empty<AstNode>()).Evaluate(context);
                foreach (var u in context.Usings)
                {
                    string candidate = $"{u}.{fullName}";
                    if (context.HasVariable(candidate))
                        return context.ReadVariable(candidate);
                    if (context.NativeFunctions.ContainsKey(candidate))
                        return new CallNode(candidate, Array.Empty<AstNode>()).Evaluate(context);
                }
                throw new ApplicationException($"Unknown reference '{string.Join('.', Parts)}'");
            }
            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── unresolved {string.Join('.', Parts)}");
            }


        }
        #endregion

        #region Function nodes
        public sealed class GenericInfo
        {
            public readonly string[] TypeParameters;
            public Dictionary<string, string[]>? Constraints;
            public GenericInfo(string[] typeParameters, Dictionary<string, string[]>? constraints)
            {
                TypeParameters = typeParameters;
                Constraints = constraints;
            }
        }
        public sealed class GenericUse
        {
            public readonly string[] TypeArguments;
            public GenericUse(string[] typeArguments)
            {
                TypeArguments = typeArguments;
            }
        }
        public sealed class AttributeNode
        {
            public readonly string Name;
            public readonly string[] Args;
            public AttributeNode(string name, string[] args)
            {
                Name = name;
                Args = args;
            }
        }
        public class FunctionDeclarationNode : Ast.AstNode
        {
            public ValueType? ReturnType; //null is void
            public string Name;
            public string[] Params;
            public ValueType[] ParamTypes;
            public AstNode?[] DefaultValues;
            public Ast.AstNode Body;
            public int ParamsIndex = -1;
            public GenericInfo? Generics { get; init; }
            public string[] Modifiers { get; }
            public IReadOnlyList<AttributeNode> Attributes { get; }
            public FunctionDeclarationNode(ValueType? ret, string name, string[] @params, ValueType[] types, AstNode?[] defVals, Ast.AstNode body,
                IList<string>? mods = null, IList<AttributeNode>? attrs = null)
            {
                ReturnType = ret; Name = name; Params = @params; ParamTypes = types; Body = body; DefaultValues = defVals;
                Modifiers = mods?.ToArray() ?? Array.Empty<string>();
                Attributes = attrs?.ToArray() ?? Array.Empty<AttributeNode>();
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                context.AddFunction(Name, new ExecutionContext.Function
                {
                    ReturnType = ReturnType ?? ValueType.Object,
                    ParamNames = Params,
                    ParamTypes = ParamTypes,
                    Body = Body,
                    DefaultValues = this.DefaultValues,
                    Attributes = this.Attributes.ToArray(),
                    IsPublic = Modifiers.Contains("public"),
                    Generics = this.Generics,
                    ParamsIndex = this.ParamsIndex,
                });
                return null!;
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
            public string?[]? ArgNames { get; }
            public GenericUse? Generic { get; init; }
            public bool IsNullConditional { get; init; }
            public CallNode(string name, AstNode[] args, string?[]? argNames = null) { Name = name; Args = args; ArgNames = argNames; }
            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.Check();
                int dot = Name.IndexOf('.');
                if (dot > 0)
                {
                    string objName = Name.Substring(0, dot);
                    string mName = Name.Substring(dot + 1);

                    if (ctx.HasVariable(objName))
                    {
                        if (IsNullConditional && (new VariableReferenceNode(objName).Evaluate(ctx)) is null)
                        {
                            return null!;
                        }
                        var newArgs = new AstNode[Args.Length + 1];
                        newArgs[0] = new VariableReferenceNode(objName);
                        Array.Copy(Args, 0, newArgs, 1, Args.Length);

                        return new CallNode(mName, newArgs).Evaluate(ctx);
                    }
                }
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
                            if (ExecutionContext.IsObject(ret) && ret is not null)
                            {
                                int id = ctx.AddObject(ret);
                                return (id, isObject: true);
                            }
                            return ret!;
                        }
                        catch (Exception ex)
                        {
                            inFuncException = FormatExceptionForUser(ex, frameLimit: 2, innerLimit: 1);
                            if (ex is OperationCanceledException or OutOfMemoryException or StackOverflowException) throw;
                        }
                        finally { ctx.ExitFunction(); }
                    }
                    throw new ApplicationException((inFuncException is null ? $"No native overload '{Name}' matches given arguments " +
                        $"({string.Join(", ", argVals.Select(x => x?.GetType()?.ToString() ?? "null"))})" : $"Exception in {Name}\n{inFuncException}"));
                }

                if (!TryResolve(Name, ctx, out string qName, out var overloads2))
                    throw new ApplicationException($"Function '{Name}' not defined");
                Name = qName;

                var callArgs = Args.Select(a => a.Evaluate(ctx)).ToArray();
                var callNames = ArgNames ?? new string?[Args.Length];
                bool TryMap(ExecutionContext.Function f, out Dictionary<int, object?> fixedMap, out List<object?> varargs, out string? error)
                {
                    fixedMap = new Dictionary<int, object?>();
                    varargs = new List<object?>();
                    error = null;

                    int prefixCount = f.ParamsIndex >= 0 ? f.ParamsIndex : f.ParamNames.Length;
                    var usedFixed = new bool[prefixCount];

                    bool sawNamed = false;
                    int posCursor = 0;

                    for (int i = 0; i < callArgs.Length; i++)
                    {
                        var val = callArgs[i];
                        var nm = i < callNames.Length ? callNames[i] : null;

                        if (!string.IsNullOrEmpty(nm))
                        {
                            sawNamed = true;
                            if (f.ParamsIndex >= 0 && string.Equals(nm, f.ParamNames[f.ParamsIndex], StringComparison.Ordinal))
                            { error = "Passing 'params' by name is not supported."; return false; }

                            int pi = Array.FindIndex(f.ParamNames, 0, prefixCount, p => string.Equals(p, nm, StringComparison.Ordinal));
                            if (pi < 0) { error = $"No parameter named '{nm}'."; return false; }
                            if (fixedMap.ContainsKey(pi)) { error = $"Parameter '{nm}' specified multiple times."; return false; }

                            fixedMap[pi] = val;
                            usedFixed[pi] = true;
                        }
                        else
                        {
                            if (sawNamed)
                            {
                                if (f.ParamsIndex >= 0)
                                {
                                    varargs.Add(val);
                                    continue;
                                }
                                else
                                {
                                    error = "Positional argument cannot appear after named argument.";
                                    return false;
                                }
                            }

                            while (posCursor < prefixCount && usedFixed[posCursor]) posCursor++;
                            if (posCursor < prefixCount)
                            {
                                fixedMap[posCursor] = val;
                                usedFixed[posCursor] = true;
                                posCursor++;
                            }
                            else if (f.ParamsIndex >= 0)
                            {
                                varargs.Add(val);
                            }
                            else
                            {
                                error = "Too many arguments for this overload.";
                                return false;
                            }
                        }
                    }

                    for (int i = 0; i < prefixCount; i++)
                    {
                        bool required = f.DefaultValues[i] is null;
                        if (required && !fixedMap.ContainsKey(i))
                        {
                            error = $"Required parameter '{f.ParamNames[i]}' was not supplied.";
                            return false;
                        }
                    }
                    return true;
                }

                int BestScore(ExecutionContext.Function f, Dictionary<int, object?> fixedMap)
                {
                    int prefixCount = f.ParamsIndex >= 0 ? f.ParamsIndex : f.ParamTypes.Length;
                    int score = 0;

                    for (int i = 0; i < prefixCount; i++)
                    {
                        if (!fixedMap.TryGetValue(i, out var have)) continue;
                        var need = f.ParamTypes[i];

                        if (have is null) score += need == ValueType.Object ? 2 : 0;
                        else if (Ast.MatchVariableType(have, need)) score += 3;
                        else if (need == ValueType.Object) score += 1;
                        else if (have is IConvertible) score += 0;
                        else return -1;
                    }
                    if (Enumerable.Range(0, prefixCount).All(i => fixedMap.ContainsKey(i))) score += 1;

                    return score;
                }

                var candidates = new List<(ExecutionContext.Function fn, Dictionary<int, object?> fixedMap, List<object?> varargs, int score)>();

                foreach (var f in overloads2!)
                {
                    if (!TryMap(f, out var fm, out var va, out var err)) continue;
                    int sc = BestScore(f, fm);
                    if (sc >= 0) candidates.Add((f, fm, va, sc));
                }
                if (candidates.Count == 0)
                    throw new ApplicationException($"No overload '{Name}' matches given arguments.");
                var best = candidates.OrderByDescending(t => t.score).First();
                var fn = best.fn;
                string fnNs = Name.Contains('.') ? Name.Substring(0, Name.LastIndexOf('.')) : "";
                if (fnNs != ctx.CurrentNameSpace && !fn.IsPublic) throw new ApplicationException($"The function '{Name}' is inaccessible due to its protection level");
                string savedNs = ctx.CurrentNameSpace;
                ctx.CurrentNameSpace = fnNs;
                ctx.EnterFunction();
                ctx.EnterScope();
                try
                {
                    var effectiveParamTypes = (Ast.ValueType[])fn.ParamTypes.Clone();
                    if (fn.Generics is { } gi)
                    {
                        if (Generic is { } use && use.TypeArguments.Length > 0)
                        {
                            var argTypes = new List<Ast.ValueType>(use.TypeArguments.Length);
                            foreach (var raw in use.TypeArguments)
                            {
                                ctx.Check();
                                var name = raw.Trim();
                                if (!Enum.TryParse<Ast.ValueType>(name, ignoreCase: true, out var vt))
                                    throw new ApplicationException($"Unknown type name '{name}' in generic arguments.");
                                argTypes.Add(vt);
                            }
                            int expected = gi.TypeParameters?.Length ?? 0;
                            if (argTypes.Count != expected)
                                throw new ApplicationException($"Generic argument count mismatch: given {argTypes.Count}, expected {expected}.");
                            if (argTypes.Count == 1)
                            {
                                for (int i = 0; i < effectiveParamTypes.Length; i++)
                                    if (effectiveParamTypes[i] == Ast.ValueType.Object)
                                        effectiveParamTypes[i] = argTypes[0];
                            }
                            else
                            {
                                int idx = 0;
                                for (int i = 0; i < effectiveParamTypes.Length && idx < argTypes.Count; i++)
                                    if (effectiveParamTypes[i] == Ast.ValueType.Object)
                                        effectiveParamTypes[i] = argTypes[idx++];
                            }

                            CheckGenericConstraints(ctx, fn, effectiveParamTypes);
                        }
                        else
                        {
                            int want = gi.TypeParameters?.Length ?? 0;
                            int taken = 0;
                            for (int i = 0; i < effectiveParamTypes.Length && i < best.fixedMap.Count; i++)
                            {
                                ctx.Check();
                                if (effectiveParamTypes[i] == Ast.ValueType.Object)
                                {
                                    var inferred = ExecutionContext.InferType(callArgs[i]!);
                                    if (inferred != Ast.ValueType.Object)
                                    {
                                        effectiveParamTypes[i] = inferred;
                                        if (want > 0 && ++taken >= want) break;
                                    }
                                }
                            }
                            CheckGenericConstraints(ctx, fn, effectiveParamTypes);
                        }
                    }
                    for (int i = 0; i < fn.ParamNames.Length; i++)
                    {
                        if (fn.ParamsIndex >= 0 && i == fn.ParamsIndex)
                        {
                            if (best.varargs.Count == 1 && best.varargs[0] is int p && p >= ctx.StackSize
                                && p < ctx.StackSize + ctx.MemoryUsed && ctx.IsArray(p))
                            {
                                ctx.Declare(fn.ParamNames[i], Ast.ValueType.Array, p);
                            }
                            else
                            {
                                object?[] tail = best.varargs.ToArray();

                                Ast.ValueType PickElemType(object?[] items)
                                {
                                    Ast.ValueType et = Ast.ValueType.Object;
                                    foreach (var it in items)
                                    {
                                        if (it is null) continue;
                                        var t = ExecutionContext.InferType(it);
                                        if (et == Ast.ValueType.Object) et = t;
                                        else if (et != t)
                                        {
                                            if (IsNumeric(et) && IsNumeric(t)) { et = Ast.ValueType.Double; continue; }
                                            et = Ast.ValueType.Object; break;
                                        }
                                    }
                                    return et;
                                }

                                var elemType = PickElemType(tail);
                                ctx.Declare(fn.ParamNames[i], Ast.ValueType.Array, (tail, elemType));
                            }
                            break;
                        }
                        object? val;
                        if (best.fixedMap.TryGetValue(i, out var supplied))
                        {
                            val = supplied;
                        }
                        else
                        {
                            var defExpr = fn.DefaultValues[i];
                            if (defExpr is null)
                                throw new ApplicationException($"Parameter '{fn.ParamNames[i]}' is required");
                            val = defExpr.Evaluate(ctx);
                        }

                        var needType = effectiveParamTypes[i];
                        if (!MatchVariableType(val!, needType) && needType != Ast.ValueType.Object)
                        {
                            try { val = ctx.Cast(val!, needType); }
                            catch { throw new ApplicationException($"Cannot cast {val?.GetType().Name} to {needType}"); }
                        }
                        ctx.Declare(fn.ParamNames[i], needType, val);
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
                    return result!;
                }
                finally { ctx.ExitFunction(); ctx.CurrentNameSpace = savedNs; }
            }
            private static bool TryResolve(string simpleName, ExecutionContext ctx, out string qualified, out List<ExecutionContext.Function>? overloads)
            {
                if (ctx.Functions.TryGetValue(simpleName, out overloads))
                {
                    qualified = simpleName;
                    return true;
                }
                string ns = ctx.CurrentNameSpace;
                while (!string.IsNullOrEmpty(ns))
                {
                    string candidate = $"{ns}.{simpleName}";
                    if (ctx.Functions.TryGetValue(candidate, out overloads))
                    {
                        qualified = candidate;
                        return true;
                    }

                    int lastDot = ns.LastIndexOf('.');
                    ns = lastDot < 0 ? string.Empty : ns.Substring(0, lastDot);
                }
                foreach (var u in ctx.Usings)
                {
                    string candidate = $"{u}.{simpleName}";
                    if (ctx.Functions.TryGetValue(candidate, out overloads))
                    {
                        qualified = candidate;
                        return true;
                    }
                }
                qualified = string.Empty;
                overloads = null;
                return false;

            }

            void CheckGenericConstraints(ExecutionContext ctx, ExecutionContext.Function fn, Ast.ValueType[] effectiveParamTypes)
            {
                if (fn.Generics?.Constraints is null) return;

                var gi = fn.Generics;
                var map = new Dictionary<string, Ast.ValueType>(StringComparer.Ordinal);

                int idx = 0;
                for (int i = 0; i < fn.ParamTypes.Length && idx < gi.TypeParameters.Length; i++)
                    if (fn.ParamTypes[i] == Ast.ValueType.Object)
                        map[gi.TypeParameters[idx++]] = effectiveParamTypes[i];

                foreach (var kv in gi.Constraints)
                {
                    var tpName = kv.Key;
                    if (!map.TryGetValue(tpName, out var bound))
                        continue;

                    foreach (var raw in kv.Value)
                    {
                        ctx.Check();
                        var s = raw.Trim().ToLowerInvariant();
                        bool ok =
                            s == "numeric" ? IsNumeric(bound) :
                            s == "struct" ? ((!Ast.IsReferenceType(bound) && bound != Ast.ValueType.Nullable) || bound == Ast.ValueType.Struct) :
                            s == "class" ? (Ast.IsReferenceType(bound) && bound != Ast.ValueType.Nullable) :
                            s == "unmanaged" ? (!Ast.IsReferenceType(bound) && bound != Ast.ValueType.Nullable) :
                            s == "notnull" ? (bound != Ast.ValueType.Nullable) :
                            Enum.TryParse<Ast.ValueType>(raw, true, out var vt) ? vt == bound : false;

                        if (!ok)
                            throw new ApplicationException($"Generic constraint failed: '{tpName} : {raw}' does not match actual type '{bound}'.");
                    }
                }
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

            public override object Evaluate(ExecutionContext context)
            {
                Decl.Evaluate(context);
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
            public ValueType UnderlyingType { get; }
            public EnumDeclarationNode(string name, Member[] members, string[]? modifiers = null, ValueType underlying = ValueType.Int)
            {
                Name = name;
                Members = members;
                Modifiers = modifiers ?? Array.Empty<string>();
                UnderlyingType = underlying;
            }
            public override object Evaluate(ExecutionContext context)
            {
                long nextAuto = 0;
                foreach (var m in Members)
                {
                    object value = m.ExplicitValue is null ? nextAuto : Convert.ToInt32(m.ExplicitValue.Evaluate(context));
                    object casted = context.Cast(value, UnderlyingType);
                    long nextBase = Convert.ToInt64(casted);
                    nextAuto = checked(nextBase + 1);
                    context.Declare($"{Name}.{m.Name}", UnderlyingType, casted);
                }

                return Members.ToDictionary(x => x.Name, x => context.ReadVariable($"{Name}.{x.Name}"));
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── enum {Name}");
                string childIndent = indent + (isLast ? "    " : "│   ");
                foreach (var (m, i) in Members.Select((m, i) => (m, i)))
                    Console.WriteLine($"{childIndent}└── {m}");
            }
        }
        public class NewDictionaryNode : Ast.AstNode
        {
            public GenericUse Generic { get; }
            public Ast.AstNode[] CtorArgs { get; }
            public (Ast.AstNode Key, Ast.AstNode Value)[]? Initializer { get; }

            public NewDictionaryNode(GenericUse generic, Ast.AstNode[] ctorArgs, (Ast.AstNode Key, Ast.AstNode Value)[]? initializer)
            {
                Generic = generic;
                CtorArgs = ctorArgs ?? Array.Empty<Ast.AstNode>();
                Initializer = initializer;
            }
            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                if (Generic is null || Generic.TypeArguments.Length != 2)
                    throw new ArgumentException("Dictionary<TK,TV> requires two type arguments");
                ValueType keyT = Enum.Parse<ValueType>(Generic.TypeArguments[0], true);
                ValueType valT = Enum.Parse<ValueType>(Generic.TypeArguments[1], true);
                var pairs = new List<(object? key, object? value)>();
                if (Initializer is not null)
                {
                    foreach (var (kExpr, vExpr) in Initializer)
                    {
                        context.Check();
                        object? k = kExpr.Evaluate(context);
                        object? v = vExpr.Evaluate(context);

                        if (!Ast.MatchVariableType(k, keyT) && keyT != ValueType.Object)
                            k = context.Cast(k, keyT);
                        if (!Ast.MatchVariableType(v, valT) && valT != ValueType.Object)
                            v = context.Cast(v, valT);

                        pairs.Add((k, v));
                    }
                }
                return context.AllocateDictionary(keyT, valT, pairs);
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── new Dictionary<...>");
                var child = indent + (isLast ? "    " : "│   ");
                if (CtorArgs.Length > 0)
                {
                    Console.WriteLine($"{child}└── ctor-args ({CtorArgs.Length})");
                }
                if (Initializer is { Length: > 0 })
                {
                    Console.WriteLine($"{child}└── init-pairs ({Initializer.Length})");
                }
            }
        }
        public class ConstructorDeclarationNode : Ast.AstNode
        {
            public readonly string StructName;
            public readonly string[] ParamNames;
            public readonly Ast.ValueType[] ParamTypes;
            public readonly Ast.AstNode?[] DefaultValues;
            public readonly Ast.AstNode Body;
            public readonly string[] Modifiers;
            public readonly Initializer? CtorInitializer;
            public int ParamsIndex = -1;
            public readonly struct Initializer
            {
                public string Kind { get; }
                public Ast.AstNode[] Args { get; }
                public Initializer(string kind, Ast.AstNode[] args) { Kind = kind; Args = args; }
            }

            public ConstructorDeclarationNode(string structName, string[] paramNames, Ast.ValueType[] paramTypes, Ast.AstNode?[] defaultValues,
                Ast.AstNode body, IEnumerable<string>? modifiers = null, Initializer? initializer = null, int paramsIndex = -1)
            {
                StructName = structName;
                ParamNames = paramNames;
                ParamTypes = paramTypes;
                DefaultValues = defaultValues;
                Body = body;
                Modifiers = (modifiers ?? Array.Empty<string>()).ToArray();
                CtorInitializer = initializer;
                ParamsIndex = paramsIndex;
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();
                var fn = new ExecutionContext.Function
                {
                    ReturnType = ValueType.Object,
                    ParamNames = ParamNames,
                    ParamTypes = ParamTypes,
                    DefaultValues = DefaultValues,
                    Body = Body,
                    Attributes = Array.Empty<AttributeNode>(),
                    IsPublic = Modifiers.Contains("public"),
                    ParamsIndex = this.ParamsIndex,
                };
                string key = $"__ctor__:{StructName}";
                context.AddFunction(key, fn);
                return null!;
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── ctor {StructName}({string.Join(", ", ParamNames)})");
                Body.Print(indent + (last ? "    " : "│   "), true);
            }
        }
        public class StructDeclarationNode : AstNode
        {
            public string Name { get; }
            public string[] Modifiers { get; }
            public AstNode[] Members { get; }
            public GenericInfo? Generics { get; init; }
            public StructDeclarationNode(string name, IEnumerable<string> modifiers, IEnumerable<AstNode> members)
            {
                Name = name;
                Modifiers = modifiers.ToArray();
                Members = members.ToArray();
            }
            public override object Evaluate(ExecutionContext ctx)
            {
                var fields = Members.OfType<VariableDeclarationNode>().ToList();
                if (fields.Count == 0) return null!;
                var buf = new List<byte>();
                foreach (var f in fields)
                {
                    buf.Add((byte)f.Type);
                    byte[] nameBytes = Encoding.UTF8.GetBytes(f.Name);
                    if (nameBytes.Length > 255) throw new ApplicationException("Field name is too long");
                    buf.Add((byte)nameBytes.Length);
                    buf.AddRange(nameBytes);
                    bool hasInitExpr = f.Expression is not Ast.LiteralNode lit || lit.Value != null;
                    if (hasInitExpr)
                    {
                        object val = f.Expression.Evaluate(ctx);
                        if (!Ast.MatchVariableType(val, f.Type) && f.Type != Ast.ValueType.Object)
                            val = ctx.Cast(val, f.Type);

                        buf.Add(1); // hasInit = true
                        if (Ast.IsReferenceType(f.Type))
                        {
                            int fieldPtr = val is int pi ? pi : ctx.PackReference(val, f.Type);
                            buf.AddRange(BitConverter.GetBytes(fieldPtr));
                        }
                        else
                        {
                            ReadOnlySpan<byte> src = ctx.GetSourceBytes(f.Type, val);
                            buf.AddRange(src.ToArray());
                        }
                    }
                    else
                    {
                        buf.Add(0); // hasInit = false
                    }
                }
                int ptr = ctx.Malloc(buf.Count, ValueType.Byte);
                ctx.WriteBytes(ptr, buf.ToArray());
                ctx.Declare(Name, ValueType.IntPtr, ptr);

                foreach (var ctor in Members.OfType<ConstructorDeclarationNode>())
                    ctor.Evaluate(ctx);
                foreach (var func in Members.OfType<FunctionDeclarationNode>())
                {
                    func.ParamTypes = func.ParamTypes.Prepend(ValueType.Struct).ToArray();
                    func.Params = func.Params.Prepend("this").ToArray();
                    func.DefaultValues = func.DefaultValues.Prepend(null).ToArray();

                    func.Evaluate(ctx);
                }
                return ptr;
            }
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"{indent}└── struct {string.Join(' ', Modifiers)} {Name}");
                string child = indent + (isLast ? "    " : "│   ");
                for (int i = 0; i < Members.Length; i++)
                    Members[i].Print(child, i == Members.Length - 1);
            }

        }
        public class NewStructNode : Ast.AstNode
        {
            private readonly string _structName;
            public Ast.AstNode[] Args { get; }
            public GenericUse? Generic { get; init; }
            public (string Name, Ast.AstNode Expr)[]? Initializers { get; init; }
            public NewStructNode(string name, params Ast.AstNode[]? args)
            {
                _structName = name;
                Args = args ?? Array.Empty<Ast.AstNode>();
            }

            public override object Evaluate(ExecutionContext context)
            {
                context.Check();

                if (!context.HasVariable(_structName))
                    throw new ApplicationException($"{_structName} signature not found");

                int ptr = Convert.ToInt32(context.ReadVariable(_structName));

                int sigLen = context.GetHeapObjectLength(ptr);
                int pos = 0;
                var fieldTypes = new List<ValueType>();
                var initBytes = new List<byte[]?>();
                while (pos < sigLen)
                {
                    var vt = (ValueType)context.RawMemory[ptr + pos++];
                    int nameLen = context.RawMemory[ptr + pos++];
                    pos += nameLen;//skip name
                    byte hasInit = context.RawMemory[ptr + pos++];
                    byte[]? init = null;
                    if (hasInit == 1)
                    {
                        int sz = context.GetTypeSize(vt);
                        init = new byte[sz];
                        Buffer.BlockCopy(context.RawMemory, ptr + pos, init, 0, sz);
                        pos += sz;
                    }
                    fieldTypes.Add(vt);
                    initBytes.Add(init);
                }

                int instPtr = context.Malloc(4 + fieldTypes.Sum(vt => 1 + context.GetTypeSize(vt)), ValueType.Struct);
                BitConverter.GetBytes(ptr).CopyTo(context.RawMemory, instPtr);
                int offset = 4;
                for (int i = 0; i < fieldTypes.Count; i++)
                {
                    var vt = fieldTypes[i];
                    context.RawMemory[instPtr + offset++] = (byte)vt;
                    var span = context.RawMemory.AsSpan(instPtr + offset, context.GetTypeSize(vt));
                    var init = initBytes[i];
                    if (init is not null)
                    {
                        init.CopyTo(span);
                    }
                    else
                    {
                        span.Fill(Ast.IsReferenceType(vt) ? (byte)0xFF : (byte)0x00);
                    }
                    offset += span.Length;
                }
                object[] argVals = Args.Select(a => a.Evaluate(context)).ToArray();
                string key = $"__ctor__:{_structName}";

                if (context.Functions.TryGetValue(key, out var ctors) && ctors.Count > 0)
                {
                    ExecutionContext.Function? best = null;
                    int bestScore = int.MinValue;
                    foreach (var c in ctors)
                    {
                        if (argVals.Length > c.ParamTypes.Length) continue;
                        bool defaultsOk = true;
                        for (int i = argVals.Length; i < c.ParamTypes.Length; i++)
                            if (c.DefaultValues[i] is null) { defaultsOk = false; break; }
                        if (!defaultsOk) continue;
                        int score = 0;
                        bool ok = true;
                        for (int i = 0; i < argVals.Length; i++)
                        {
                            var pt = c.ParamTypes[i];
                            var av = argVals[i];
                            var at = ExecutionContext.InferType(av);
                            if (at.Equals(pt)) score += 2;
                            else
                            {
                                try { _ = context.Cast(av, pt); score += 1; }
                                catch { ok = false; break; }
                            }
                        }
                        if (!ok) continue;
                        int tieBreak = -c.ParamTypes.Length;
                        int finalScore = (score << 8) | (tieBreak & 0xFF);
                        if (finalScore > bestScore) { bestScore = finalScore; best = c; }
                    }
                    if (best.HasValue)
                    {
                        var ctor = best.Value;
                        context.EnterFunction();
                        context.EnterScope();
                        int pin = context.Pin(instPtr);
                        try
                        {
                            context.Declare("this", ValueType.Struct, instPtr);

                            for (int i = 0; i < ctor.ParamNames.Length; i++)
                            {
                                object val;
                                if (i < argVals.Length) val = argVals[i];
                                else val = ctor.DefaultValues[i]!.Evaluate(context);

                                if (!MatchVariableType(val, ctor.ParamTypes[i]))
                                    val = context.Cast(val, ctor.ParamTypes[i]);

                                context.Declare(ctor.ParamNames[i], ctor.ParamTypes[i], val);
                            }


                            _ = ctor.Body.Evaluate(context);
                        }
                        finally
                        {
                            context.ExitScope();
                            context.ExitFunction();
                            context.Unpin(pin);
                        }
                    }
                }
                if (Initializers is { Length: > 0 })
                {
                    foreach (var (name, ex) in Initializers)
                    {
                        var val = ex.Evaluate(context);
                        context.WriteStructField(instPtr, name, val);
                    }
                }
                return instPtr;
            }

            public override void Print(string indent = "", bool last = true)
            {
                Console.WriteLine($"{indent}└── new {_structName}({(Args.Length == 0 ? "" : "...")})");
                string childIndent = indent + (last ? "    " : "│   ");
                for (int i = 0; i < Args.Length; i++)
                    Args[i].Print(childIndent, i == Args.Length - 1);
            }
        }
        public class ClassDeclarationNode : AstNode
        {
            public string Name { get; }
            public string[] Modifiers { get; }
            public AstNode[] Members { get; }
            public GenericInfo? Generics { get; init; }
            public ClassDeclarationNode(string name, IEnumerable<string> mods, IEnumerable<AstNode> members)
            { Name = name; Modifiers = mods.ToArray(); Members = members.ToArray(); }

            public override object Evaluate(ExecutionContext ctx)
            {
                return null!;
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
            public GenericInfo? Generics { get; init; }
            public InterfaceDeclarationNode(string name, IEnumerable<string> mods, IEnumerable<AstNode> members)
            { Name = name; Modifiers = mods.ToArray(); Members = members.ToArray(); }

            public override object Evaluate(ExecutionContext ctx)
            {
                return null!;
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

        #region Pattern matching
        public abstract class PatternNode : AstNode
        {
            public abstract bool Match(object? value, ExecutionContext ctx);
            public override object Evaluate(ExecutionContext context)
                => throw new InvalidOperationException("Pattern‑node cannot be evaluated standalone");
        }
        public sealed class ConstantPatternNode : PatternNode
        {
            public readonly AstNode Constant;
            public ConstantPatternNode(AstNode constant) => Constant = constant;
            public override bool Match(object? v, ExecutionContext context)
                => Equals(v, Constant.Evaluate(context));
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"ConstantPatternNode");
            }
        }
        public sealed class AnyPatternNode : PatternNode
        {
            public override bool Match(object? _, ExecutionContext __) => true;
            public override void Print(string indent = "", bool last = true)
                => Console.WriteLine("AnyPatternNode");
        }
        public sealed class TypePatternNode : PatternNode
        {
            public readonly ValueType Target;
            public TypePatternNode(ValueType t) => Target = t;
            public override bool Match(object? value, ExecutionContext _)
                => value != null && MatchVariableType(value, Target);
            public override void Print(string indent = "", bool last = true)
                => Console.WriteLine($"TypePatternNode {Target}");
        }
        public sealed class DeclarationPatternNode : PatternNode
        {
            public readonly ValueType Target;
            public readonly string Name;
            public DeclarationPatternNode(ValueType t, string name)
            { Target = t; Name = name; }
            public override bool Match(object? value, ExecutionContext ctx)
            {
                if (value is null) return Target == ValueType.Object;
                if (!MatchVariableType(value, Target))
                {
                    try { value = ctx.Cast(value, Target); }
                    catch { return false; }
                }
                if (Name != "_")
                {
                    if (ctx.HasVariable(Name)) ctx.WriteVariable(Name, value!);
                    else ctx.Declare(Name, Target, value!);
                }
                return true;
            }
            public override void Print(string ind = "", bool last = true)
                => Console.WriteLine($"{ind}└── decl-pattern {Target} {Name}");
        }
        public sealed class RelationalPatternNode : PatternNode
        {
            public readonly OperatorToken Op;
            public readonly AstNode Constant;
            public RelationalPatternNode(OperatorToken op, AstNode k) { Op = op; Constant = k; }
            public override bool Match(object? value, ExecutionContext context)
            {
                if (value is null) return false;
                var constant = Constant.Evaluate(context);
                if (constant is null) return false;
                if (value is not IComparable left || constant is not IComparable right) return false;
                int cmp;
                try { cmp = left.CompareTo(right); }
                catch { return false; }
                switch (Op)
                {
                    case OperatorToken.Greater: return cmp > 0;
                    case OperatorToken.GreaterOrEqual: return cmp >= 0;
                    case OperatorToken.Less: return cmp < 0;
                    case OperatorToken.LessOrEqual: return cmp <= 0;
                    default: return false;
                }
            }
            public override void Print(string ind = "", bool last = true)
                => Console.WriteLine($"RelationalPatternNode {Op}");
        }
        public sealed class NullPatternNode : PatternNode
        {
            public override bool Match(object? value, ExecutionContext _) => value is null;
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"NullPatternNode");
            }
        }

        public sealed class NotPatternNode : PatternNode
        {
            public readonly PatternNode Inner;
            public NotPatternNode(PatternNode inner) => Inner = inner;
            public override bool Match(object? value, ExecutionContext context) => !Inner.Match(value, context);
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"NotPatternNode");
            }
        }

        public sealed class BinaryPatternNode : PatternNode
        {
            public readonly PatternNode Left, Right;
            public readonly bool IsAnd;
            public BinaryPatternNode(PatternNode l, PatternNode r, bool andOp)
            { Left = l; Right = r; IsAnd = andOp; }
            public override bool Match(object? value, ExecutionContext context)
                => IsAnd ? (Left.Match(value, context) && Right.Match(value, context))
                         : (Left.Match(value, context) || Right.Match(value, context));
            public override void Print(string indent = "", bool isLast = true)
            {
                Console.WriteLine($"BinaryPatternNode");
            }
        }
        public sealed class IsPatternNode : AstNode
        {
            public readonly AstNode Expr;
            public readonly PatternNode Pattern;
            public IsPatternNode(AstNode expr, PatternNode pattern)
            { Expr = expr; Pattern = pattern; }

            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.Check();
                var val = Expr.Evaluate(ctx);
                return Pattern.Match(val, ctx);
            }

            public override void Print(string ind = "", bool last = true)
            {
                Console.WriteLine($"{ind}└── is");
                Expr.Print(ind + (last ? "    " : "│   "), false);
                Pattern.Print(ind + (last ? "    " : "│   "), true);
            }
        }
        public sealed class WhenPatternNode : PatternNode
        {
            public readonly PatternNode Inner;
            public readonly AstNode Guard;

            public WhenPatternNode(PatternNode inner, AstNode guard)
            { Inner = inner; Guard = guard; }

            public override bool Match(object? value, ExecutionContext ctx)
                => Inner.Match(value, ctx) && Convert.ToBoolean(Guard.Evaluate(ctx));

            public override void Print(string indent = "", bool last = true)
                => Console.WriteLine("WhenPatternNode");
        }
        public class SwitchExprNode : AstNode
        {
            public readonly AstNode Discriminant;
            public readonly (PatternNode pat, AstNode expr)[] Arms;
            public SwitchExprNode(AstNode disc, IEnumerable<(PatternNode pat, AstNode expr)> arms)
            { Discriminant = disc; Arms = arms.ToArray(); }
            public override object Evaluate(ExecutionContext ctx)
            {
                ctx.Check();
                var value = Discriminant.Evaluate(ctx);
                foreach (var (pat, expr) in Arms)
                    if (pat.Match(value, ctx))
                        return expr.Evaluate(ctx);
                throw new ApplicationException("switch expression: no matching case");
            }

            public override void Print(string ind = "", bool last = true)
            {
                Console.WriteLine($"{ind}└── switch‑expr");
                string ch = ind + (last ? "    " : "│   ");
                Discriminant.Print(ch, false);
                foreach (var arm in Arms)
                {
                    arm.pat.Print(ch + "│   ", false);
                    arm.expr.Print(ch + "│   ", true);
                }
            }

        }

        #endregion
    }
    public class ParseException : ApplicationException
    {
        public string? CapturedStackText { get; }
        public ParseException() { }
        public ParseException(string message) : base(message) { }
        private ParseException(string message, string? stackText) : base(message)
        => CapturedStackText = stackText;
        public static ParseException Create(string message, int skipFrames = 1, bool needFileInfo = true)
        => new ParseException(message, new StackTrace(skipFrames, needFileInfo).ToString());

        public string ToUserString(int parserFramesLimit = 6,
            bool includeFirstExternalFrame = false,
            string visibleNamespacePrefix = "Interpretor",
            bool appendHidden = false)
        {
            var sb = new StringBuilder();
            sb.Append(GetType().FullName).Append(": ").Append(Message);

            var stackText = CapturedStackText ?? new StackTrace(this, false).ToString();
            if (string.IsNullOrWhiteSpace(stackText))
                return sb.ToString();
            var lines = stackText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var frames = Array.FindAll(lines, static l => l.StartsWith("   at ", StringComparison.Ordinal));
            if (frames.Length == 0)
                return sb.ToString();

            int shown = 0;
            int i = 0;
            string? lastKey = null;
            int repeat = 0;
            for (; i < frames.Length && shown < parserFramesLimit; i++)
            {
                var key = ExtractKey(frames[i]);
                if (key.Length == 0) continue;
                if (IsHiddenKey(key)) continue;

                if (!IsVisible(key, visibleNamespacePrefix))
                    break;

                if (key == lastKey) { repeat++; continue; }
                FlushRepeat();
                lastKey = key;
                repeat = 0;

                AppendFrame(sb, key);
                shown++;
            }
            FlushRepeat();
            if (includeFirstExternalFrame && i < frames.Length)
            {
                var extKey = ExtractKey(frames[i]);
                if (extKey.Length != 0 && !IsHiddenKey(extKey))
                {
                    AppendFrame(sb, extKey);
                    shown++;
                    i++;
                }
            }
            int hidden = frames.Length - i;
            if (hidden > 0)
            {
                sb.AppendLine();
                sb.Append("   ... ");
                if (appendHidden)
                    sb.Append(hidden).Append(hidden == 1 ? " more frame hidden" : " more frames hidden");
            }

            return sb.ToString();
            void FlushRepeat()
            {
                if (repeat <= 0) return;
                sb.AppendLine();
                sb.Append("   at ").Append(lastKey);
                repeat = 0;
            }

            static void AppendFrame(StringBuilder sb2, string key)
            {
                sb2.AppendLine();
                sb2.Append("   at ").Append(key);
            }

            static string ExtractKey(string line)
            {
                int start = line.IndexOf("   at ", StringComparison.Ordinal);
                if (start >= 0) start += 6; else start = 0;

                int inIdx = line.IndexOf(" in ", start, StringComparison.Ordinal);
                var core = inIdx >= 0 ? line.AsSpan(start, inIdx - start) : line.AsSpan(start);

                int plus = core.IndexOf(" + ");
                if (plus >= 0) core = core[..plus];

                int paren = core.IndexOf('(');
                if (paren >= 0) core = core[..paren];

                return core.Trim().ToString();
            }

            static bool IsVisible(string key, string nsPrefix)
            {
                if (string.IsNullOrEmpty(nsPrefix)) return true;
                return key.StartsWith(nsPrefix + ".", StringComparison.Ordinal);
            }

            static bool IsHiddenKey(string key)
            {
                int lastDot = key.LastIndexOf('.');
                var method = lastDot >= 0 ? key[(lastDot + 1)..] : key;

                return method is "MakeParseError" or "Missing" or "Report" or "Synchronize"
                               or "SoftParseStatement" or "ParseStatementSafe";
            }
        }

    }
}
