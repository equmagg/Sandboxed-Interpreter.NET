using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Interpretor
{
    public partial class Ast
    {
        public class ExecutionContext : IDisposable
        {
            private const int HeaderSize = 4;
            public const int MaxCallDepth = 512;
            private const int ObjectTableCapacity = 64;
            private const byte UsedMask = 0x80; // 1000_0000
            private const byte ArrayMask = 0x40; // 0100_0000
            private const byte TypeMask = 0x3F; // 0011_1111
            private readonly Stack<Dictionary<string, Variable>> _scopes = new();
            private readonly Stack<int> _memoryOffsets = new();
            private readonly ObjectTable _handles = new(capacity: ObjectTableCapacity);
            private readonly byte[] _memory;
            private readonly HashSet<int> _pinned = new();
            private int _heapend = 0;
            private int _allocPointer = 0;
            private int _callDepth = 0;
            private ulong _operationsCount = 0;
            private int _totalVars = 0;
            public readonly Dictionary<string, List<Function>> Functions = new();
            public readonly Dictionary<string, List<Delegate>> NativeFunctions = new();
            public readonly HashSet<string> Usings = new();
            public readonly int StackSize;
            public int ScopeVersion { get; private set; } = 0;
            public Dictionary<string, int> Labels { get; set; } = new();
            public CancellationToken CancellationToken { get; }
            public byte[] RawMemory => _memory;
            public int MemoryUsed => _heapend;
            private string _currentNameSpace = "";
            public string CurrentNameSpace { get { return _currentNameSpace; } set { if (value.Length < 200) _currentNameSpace = value; } }
            public struct Function
            {
                public ValueType ReturnType;
                public string[] ParamNames;
                public ValueType[] ParamTypes;
                public AstNode?[] DefaultValues;
                public AstNode Body;
                public AttributeNode[] Attributes;
                public bool IsPublic;
                public Ast.GenericInfo? Generics;
                public int ParamsIndex;
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
            public bool TryMap(string simple, out string full)
            {
                if (HasVariable(simple) || Functions.ContainsKey(simple) || NativeFunctions.ContainsKey(simple))
                { full = simple; return true; }
                foreach (var u in Usings)
                {
                    string candidate = $"{u}.{simple}";
                    if (HasVariable(candidate) || Functions.ContainsKey(candidate) || NativeFunctions.ContainsKey(candidate))
                    { full = candidate; return true; }
                }
                full = null!; return false;
            }
            public void Check()
            {
                if (CancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Execution was cancelled");
                _operationsCount++;
                if ((_operationsCount & 0x3FF) == 0) //%1024
                {
                    if (_scopes.Count > 1024) throw new OutOfMemoryException($"Too many scopes alive, danger of stack overflow");
                    if (_totalVars > 2048) throw new OutOfMemoryException($"Too many declarations, danger of host memory ddos");
                    if (_operationsCount > 100_000_000) throw new OutOfMemoryException($"Too many operations");
                }
            }
            #region Memory manager
            public string PrintMemory(int bytesPerRow = 16, int dataPreview = 64, bool printStack = false)
            {
                var sb = new System.Text.StringBuilder();
                //Console.WriteLine($"Memory: {_memory.Length / (1024*1024)}Mb {_memory.Length/1024}Kb {_memory.Length%1024}B");
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
                }
                else sb.AppendLine("...emmited...");
                sb.AppendLine("=== HEAP ===");
                int pos = 0;
                try
                {
                    while (pos < _heapend)
                    {
                        int headerPos = pos + StackSize;
                        int len = GetHeapObjectLength(headerPos + HeaderSize) + HeaderSize;
                        bool used = IsUsed(headerPos + HeaderSize);
                        bool isArray = IsArray(headerPos + HeaderSize);
                        ValueType vt = GetHeapObjectType(headerPos + HeaderSize);
                        int payload = len - HeaderSize;

                        sb.Append('[')
                          .Append($"@{headerPos}({headerPos + HeaderSize}) len={payload}({payload + HeaderSize}) used={(used)} type={vt}{(isArray ? "[]" : "")} data=");

                        int preview = System.Math.Min(payload, dataPreview);
                        for (int i = 0; i < preview; i++)
                            sb.Append($"{_memory[headerPos + HeaderSize + i]:X2} ");
                        if (payload > preview) sb.Append("...");
                        sb.Append(']')
                          .AppendLine();

                        pos += len;
                    }
                }
                catch (Exception e) { Console.WriteLine(e); }
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
                { Ast.ValueType.Reference, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Array, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Tuple, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.String, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Object, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Enum, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Struct, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Class, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Nullable, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Dictionary, (heap, offset) => BitConverter.ToInt32(heap, offset) },
                { Ast.ValueType.Bool, (heap, offset) => heap[offset] != 0 },
                { Ast.ValueType.Float, (heap, offset) => BitConverter.ToSingle(heap, offset) },
                { Ast.ValueType.Double, (heap, offset) => BitConverter.ToDouble(heap, offset) },
                { Ast.ValueType.Char, (heap, offset) => BitConverter.ToChar(heap, offset) },
                { Ast.ValueType.Long, (heap, offset) => BitConverter.ToInt64(heap, offset) },
                { Ast.ValueType.Ulong, (heap, offset) => BitConverter.ToUInt64(heap, offset) },
                { Ast.ValueType.Uint, (heap, offset) => BitConverter.ToUInt32(heap, offset) },
                { Ast.ValueType.Short,  (heap, offset)=>BitConverter.ToInt16 (heap, offset) },
                { Ast.ValueType.UShort, (heap, offset)=>BitConverter.ToUInt16(heap, offset) },
                { Ast.ValueType.Byte,   (heap, offset)=>heap[offset] },
                { Ast.ValueType.Sbyte,  (heap, offset)=>unchecked((sbyte)heap[offset]) },
                { Ast.ValueType.Decimal,(heap, offset)=>BitConverter.ToUInt32(heap, offset)},
                { Ast.ValueType.DateTime, (heap, offset) => new DateTime(BitConverter.ToInt64(heap, offset), DateTimeKind.Unspecified) },
                { Ast.ValueType.TimeSpan, (heap, offset) => new TimeSpan(BitConverter.ToInt64(heap, offset)) },
            };

            private static readonly Dictionary<Ast.ValueType, Action<byte[], int, object>> typeWriters = new()
            {
                { Ast.ValueType.Int, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.IntPtr, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Reference, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.String, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Array, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Tuple, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Object, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Enum, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Struct, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Class, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Dictionary, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.Bool, (heap, offset, value) => heap[offset] = (bool)value ? (byte)1 : (byte)0 },
                { Ast.ValueType.Float, (heap, offset, value) => BitConverter.GetBytes((float)value).CopyTo(heap, offset) },
                { Ast.ValueType.Double, (heap, offset, value) => BitConverter.GetBytes((double)value).CopyTo(heap, offset) },
                { Ast.ValueType.Char, (heap, offset, value) => BitConverter.GetBytes((char)value).CopyTo(heap, offset) },
                { Ast.ValueType.Long, (heap, offset, value) => BitConverter.GetBytes((long)value).CopyTo(heap, offset) },
                { Ast.ValueType.Ulong, (heap, offset, value) => BitConverter.GetBytes((ulong)value).CopyTo(heap, offset) },
                { Ast.ValueType.Uint, (heap, offset, value) => BitConverter.GetBytes((uint)value).CopyTo(heap, offset) },
                { Ast.ValueType.Short, (heap, offset, value) => BitConverter.GetBytes((short)value).CopyTo(heap, offset) },
                { Ast.ValueType.UShort, (heap, offset, value) => BitConverter.GetBytes((ushort)value).CopyTo(heap, offset) },
                { Ast.ValueType.Byte, (heap, offset, value) => heap[offset] = (byte)value },
                { Ast.ValueType.Sbyte, (heap, offset, value) => heap[offset] = unchecked((byte)(sbyte)value) },
                { Ast.ValueType.Decimal, (heap, offset, value) => BitConverter.GetBytes((int)value).CopyTo(heap, offset) },
                { Ast.ValueType.DateTime, (heap, offset, value) => BitConverter.GetBytes(((DateTime)value).Ticks).CopyTo(heap, offset) },
                { Ast.ValueType.TimeSpan, (heap, offset, value) => BitConverter.GetBytes(((TimeSpan)value).Ticks).CopyTo(heap, offset) },
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
                ScopeVersion++;
            }

            public void ExitScope()
            {
                var top = _scopes.Peek();
                _totalVars -= top.Count;
                foreach (var kvp in top)
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
                    ScopeVersion++;
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool HasVariable(string name)
            {
                foreach (var scope in _scopes)
                    if (scope.ContainsKey(name)) return true;
                return false;
            }
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
                    case ValueType.Bool: return sizeof(bool);
                    case ValueType.String: return sizeof(int);
                    case ValueType.Array: return sizeof(int);
                    case ValueType.Tuple: return sizeof(int);
                    case ValueType.Object: return sizeof(int);
                    case ValueType.IntPtr: return sizeof(int);
                    case ValueType.Reference: return sizeof(int);
                    case ValueType.Enum: return sizeof(int);
                    case ValueType.Nullable: return sizeof(int);
                    case ValueType.Dictionary: return sizeof(int);
                    case ValueType.Struct: return sizeof(int);
                    case ValueType.Class: return sizeof(int);
                    case ValueType.Uint: return sizeof(uint);
                    case ValueType.Long: return sizeof(long);
                    case ValueType.Ulong: return sizeof(ulong);
                    case ValueType.Double: return sizeof(double);
                    case ValueType.Float: return sizeof(float);
                    case ValueType.Decimal: return sizeof(decimal);
                    case ValueType.Char: return sizeof(char);
                    case ValueType.Short: return sizeof(short);
                    case ValueType.UShort: return sizeof(ushort);
                    case ValueType.Byte: return sizeof(byte);
                    case ValueType.Sbyte: return sizeof(sbyte);
                    case ValueType.DateTime: return sizeof(long);
                    case ValueType.TimeSpan: return sizeof(long);
                    default: throw new ApplicationException($"Unsupported type {type}");
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object ReadVariable(string name)
            {
                var variable = Get(name);
                if (variable.Address < 0) return null!;
                return typeReaders[variable.Type](_memory, variable.Address);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object ReadVariable(Variable variable)
            {
                if (variable.Address < 0) return null!;
                return typeReaders[variable.Type](_memory, variable.Address);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteVariable(string name, object value)
            {
                var variable = Get(name);
                ValidateAddress(variable.Address, GetTypeSize(variable.Type));
                typeWriters[variable.Type](_memory, variable.Address, value);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteVariable(Variable variable, object value)
            {
                ValidateAddress(variable.Address, GetTypeSize(variable.Type));
                typeWriters[variable.Type](_memory, variable.Address, value);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteVariableById(int ptr, Ast.ValueType vt, object value)
            {
                ValidateAddress(ptr, GetTypeSize(vt));
                typeWriters[vt](_memory, ptr, value);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                return addr;
            }
            public void Unpin(int key) => _pinned.Remove(key);
            public void CollectGarbage()
            {
                //Mark
                HashSet<int> reachable = new();
                foreach (var p in _pinned) Mark(p, reachable);
                foreach (var scope in _scopes)
                    foreach (var kv in scope)
                    {
                        Variable v = kv.Value;
                        if (Ast.IsReferenceType(v.Type) || v.Type == Ast.ValueType.IntPtr || v.Type == Ast.ValueType.Array)
                        {
                            int root = BitConverter.ToInt32(_memory, v.Address);
                            if (root >= StackSize && root < StackSize + _heapend)
                                Mark(root, reachable);
                        }
                    }
                //Sweep
                int pos = 0;
                while (pos < _heapend)
                {
                    int headerPos = pos + StackSize;
                    int len = GetHeapObjectLength(headerPos + HeaderSize) + HeaderSize;
                    bool used = IsUsed(headerPos + HeaderSize);

                    int dataAddr = pos + HeaderSize + StackSize;

                    if (used && !reachable.Contains(dataAddr))
                        Free(headerPos + HeaderSize);//free

                    pos += len;//next block
                }
            }
            private void Mark(int ptr, HashSet<int> reachable)
            {
                if (ptr < StackSize || ptr >= StackSize + _heapend) return;
                if (!reachable.Add(ptr)) return;

                var vt = GetHeapObjectType(ptr);
                if (vt == ValueType.Byte) return;

                int bytes = GetHeapObjectLength(ptr);
                int blockEnd = ptr + bytes;
                int heapEnd = StackSize + _heapend;
                if (bytes < 0 || blockEnd < ptr || blockEnd > heapEnd) return;
                bool isArr = IsArray(ptr);
                if (vt == ValueType.String && !isArr) return;

                if (vt == ValueType.Nullable)
                {
                    if (bytes >= 1)
                    {
                        var baseT = (ValueType)_memory[ptr];
                        if (IsReferenceType(baseT) && bytes >= 1 + sizeof(int))
                        {
                            int child = BitConverter.ToInt32(_memory, ptr + 1);
                            if (child >= StackSize && child < heapEnd) Mark(child, reachable);
                        }
                    }
                    return;
                }
                if (isArr)
                {
                    if (!IsReferenceType(vt)) return;
                    int len = bytes / sizeof(int);
                    if (len < 0 || len > ((heapEnd - ptr) / sizeof(int))) return;
                    int last = ptr + len * sizeof(int);
                    if (last > heapEnd) len = Math.Max(0, (heapEnd - ptr) / sizeof(int));
                    for (int i = 0; i < len; i++)
                    {
                        int at = ptr + i * sizeof(int);
                        if (at + sizeof(int) > heapEnd) break;
                        int child = BitConverter.ToInt32(_memory, at);
                        if (child >= StackSize && child < heapEnd) Mark(child, reachable);
                    }
                    return;
                }
                if (bytes >= sizeof(int))
                {
                    int sigPtr = BitConverter.ToInt32(_memory, ptr);

                    if (vt == ValueType.Tuple)
                    {
                        int pos = ptr;
                        int end = ptr + bytes;
                        while (pos < end)
                        {
                            var et = (ValueType)_memory[pos++];

                            if (IsReferenceType(et))
                            {
                                if (pos + sizeof(int) > end) break;
                                int child = BitConverter.ToInt32(_memory, pos);
                                if (child >= StackSize && child < StackSize + _heapend) Mark(child, reachable);
                                pos += sizeof(int);
                            }
                            else
                            {
                                pos += GetTypeSize(et);
                            }

                            if (pos + sizeof(int) > end) break;
                            int namePtr = BitConverter.ToInt32(_memory, pos);
                            if (namePtr >= StackSize && namePtr < StackSize + _heapend) Mark(namePtr, reachable);
                            pos += sizeof(int);
                        }
                        return;
                    }

                    if (vt == ValueType.Struct || (sigPtr >= StackSize && sigPtr < heapEnd && GetHeapObjectType(sigPtr) == ValueType.Byte))
                    {
                        if (!(sigPtr >= StackSize && sigPtr < heapEnd)) return;
                        reachable.Add(sigPtr);
                        int sigLen = GetHeapObjectLength(sigPtr);
                        if (sigLen < 0 || sigPtr + sigLen > heapEnd) return;
                        int s = 0;
                        int val = ptr + sizeof(int);
                        while (s < sigLen)
                        {
                            if (s + 1 > sigLen) break;
                            ValueType declared = (ValueType)_memory[sigPtr + s++];
                            if (s >= sigLen) break;
                            int nameLen = _memory[sigPtr + s++];
                            if (nameLen < 0 || s + nameLen > sigLen) break;
                            s += nameLen;
                            if (s >= sigLen) break;
                            bool hasInit = _memory[sigPtr + s++] != 0;
                            if (hasInit)
                            {
                                s += GetTypeSize(declared);
                                if (s > sigLen) break;
                            }
                            if (val >= heapEnd) break;
                            ValueType instFieldType = (ValueType)_memory[val++];
                            int payloadSize = IsReferenceType(instFieldType) ? sizeof(int) : GetTypeSize(instFieldType);

                            if (IsReferenceType(instFieldType))
                            {
                                if (val + sizeof(int) > heapEnd) break;
                                int child = BitConverter.ToInt32(_memory, val);
                                if (child >= StackSize) Mark(child, reachable);
                            }

                            val += payloadSize;
                            if (val > blockEnd) break;
                        }
                        return;
                    }
                }
                if (vt == ValueType.Dictionary)
                {
                    if (bytes < 2) return;
                    var kt = (ValueType)_memory[ptr];
                    var vt2 = (ValueType)_memory[ptr + 1];
                    int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                    int vs = IsReferenceType(vt2) ? sizeof(int) : GetTypeSize(vt2);

                    int pos = ptr + 2;
                    int end = ptr + bytes;
                    while (pos + ks + vs <= end)
                    {
                        if (IsReferenceType(kt))
                        {
                            int kptr = BitConverter.ToInt32(_memory, pos);
                            if (kptr >= StackSize && kptr < heapEnd) Mark(kptr, reachable);
                        }
                        if (IsReferenceType(vt2))
                        {
                            int vptr = BitConverter.ToInt32(_memory, pos + ks);
                            if (vptr >= StackSize && vptr < heapEnd) Mark(vptr, reachable);
                        }
                        pos += ks + vs;
                    }
                    return;
                }
            }
            public int GetArrayLength(int dataPtr)
            {
                ValidateAddress(dataPtr);
                int bytes = GetHeapObjectLength(dataPtr);
                var vt = GetHeapObjectType(dataPtr);
                int es = GetTypeSize(vt);
                if (es <= 0) throw new ApplicationException($"Invalid element size for {vt}");
                return bytes / GetTypeSize(vt);
            }
            private void WriteHeader(int pos, int len, ValueType vt, bool used, bool isArray = false)
            {
                checked
                {
                    if (len > 0xFFFFFF)
                        throw new ArgumentOutOfRangeException(nameof(len), "UInt24 range 0‑0xFFFFFF.");
                }
                unchecked
                {
                    _memory[pos] = (byte)(len & 0xFF);
                    _memory[pos + 1] = (byte)((len >> 8) & 0xFF);
                    _memory[pos + 2] = (byte)((len >> 16) & 0xFF);
                    if ((byte)vt > 0x3F)   // 0x3F = 63
                        throw new ArgumentOutOfRangeException(nameof(vt), "Must fit into 6 bits (0‑63)");
                }
                _memory[pos + 3] = (byte)((byte)vt | (isArray ? ArrayMask : 0) | (used ? UsedMask : 0));

            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ValueType GetHeapObjectType(int addr) => (ValueType)(_memory[addr - HeaderSize + 3] & TypeMask);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetHeapObjectLength(int addr)
                => (_memory[addr - HeaderSize] | (_memory[addr - HeaderSize + 1] << 8) | (_memory[addr - HeaderSize + 2] << 16)) - HeaderSize;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Span<byte> GetSpan(int addr)
            {
                if (addr < StackSize) throw new ArgumentOutOfRangeException($"Pointer to Stack in GetSpan");
                int len = GetHeapObjectLength(addr);
                ValidateAddress(addr, len);
                return _memory.AsSpan(addr, len);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsUsed(int addr) => (_memory[addr - HeaderSize + 3] & UsedMask) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsArray(int addr) => (_memory[addr - HeaderSize + 3] & ArrayMask) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Free(int addr)
            {

                int blockStart = addr - HeaderSize;
                if (blockStart < StackSize || blockStart >= _heapend + StackSize)
                    throw new ArgumentOutOfRangeException(nameof(addr));
                if (!IsUsed(addr))
                    throw new InvalidOperationException($"double-free or invalid pointer {addr}");
                _memory[blockStart + 3] = (byte)(_memory[blockStart + 3] & ~UsedMask);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteBytes(int addr, ReadOnlySpan<byte> src)
            {
                ValidateAddress(addr, src.Length);
                if (src.Length > GetHeapObjectLength(addr)) throw new ArgumentException("WriteBytes: too big for declared header");

                src.CopyTo(_memory.AsSpan(addr, src.Length));
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteBytesAt(int baseAddr, int offset, ReadOnlySpan<byte> src)
            {
                int cap = GetHeapObjectLength(baseAddr);
                if (offset < 0 || src.Length < 0 || offset + src.Length > cap)
                    throw new ArgumentException("WriteBytesAt: write exceeds object bounds");

                ValidateAddress(baseAddr + offset, src.Length);
                src.CopyTo(_memory.AsSpan(baseAddr + offset, src.Length));
            }
            public int Malloc(int size, ValueType valueType, bool isArray = false)
            {
                if (size < 0) throw new ArgumentException(nameof(size), "Allocation length must be positive");
                int need = size + HeaderSize;
                int addr = FindFreeBlock(need, valueType, isArray: isArray);
                if (addr >= 0) return addr;
                DefragmentFree();
                addr = FindFreeBlock(need, valueType, isArray: isArray);
                if (addr >= 0) return addr;
                checked
                {
                    if (_heapend + need > _memory.Length - StackSize)
                        throw new OutOfMemoryException();
                    WriteHeader(_heapend + StackSize, need, valueType, used: true, isArray: isArray);
                    addr = _heapend + HeaderSize;
                    _heapend += need;
                    return addr + StackSize;
                }

            }
            /// <summary>Reallocates memory. If new length is greater then the previous, copies bytes to the new address and frees the old one.</summary>
            /// <exception cref="ArgumentOutOfRangeException"></exception>
            public int Realloc(int oldPtr, int newLength)
            {
                if (oldPtr < StackSize || oldPtr >= StackSize + _heapend)
                    throw new ArgumentOutOfRangeException(nameof(oldPtr), "nullptr");
                var elemType = GetHeapObjectType(oldPtr);
                bool isArr = IsArray(oldPtr);
                int oldBytes = GetHeapObjectLength(oldPtr);
                if (newLength == oldBytes) return oldPtr;
                int newPtr = Malloc(newLength, elemType, isArray: isArr);
                if (newLength > oldBytes)
                {
                    if (oldBytes > 0)
                        _memory.AsSpan(oldPtr, oldBytes).CopyTo(_memory.AsSpan(newPtr, oldBytes));
                    var tail = _memory.AsSpan(newPtr + oldBytes, newLength - oldBytes);
                    if (isArr && IsReferenceType(elemType))
                        tail.Fill(0xFF);
                    else
                        tail.Clear();
                }
                RelocateReferences(oldPtr, newPtr);
                if (newLength > oldBytes) Free(oldPtr);
                return newPtr;
                void RelocateReferences(int oldPtr, int newPtr)
                {
                    if (oldPtr == newPtr) return;
                    if (oldPtr < StackSize || oldPtr > StackSize + MemoryUsed || newPtr < StackSize || newPtr > StackSize + MemoryUsed)
                        throw new ArgumentOutOfRangeException(nameof(oldPtr), "Expect heap data pointers");
                    if (_pinned.Remove(oldPtr)) _pinned.Add(newPtr);
                    foreach (var scope in _scopes)
                    {
                        foreach (var kv in scope)
                        {
                            var v = kv.Value;
                            if (Ast.IsReferenceType(v.Type) || v.Type == Ast.ValueType.IntPtr || v.Type == Ast.ValueType.Array)
                            {
                                int cur = BitConverter.ToInt32(_memory, v.Address);
                                if (cur == oldPtr)
                                    BitConverter.GetBytes(newPtr).CopyTo(_memory, v.Address);
                            }
                        }
                    }
                    int pos = 0;
                    while (pos < _heapend)
                    {
                        int headerPos = pos + StackSize;
                        int len = GetHeapObjectLength(headerPos + HeaderSize) + HeaderSize;
                        bool used = IsUsed(headerPos + HeaderSize);
                        if (used)
                        {
                            int ptr = headerPos + HeaderSize;
                            int bytes = GetHeapObjectLength(ptr);
                            var vt = GetHeapObjectType(ptr);
                            bool isArr = IsArray(ptr);
                            if (vt == Ast.ValueType.Nullable)
                            {
                                if (bytes >= 1)
                                {
                                    var baseT = (Ast.ValueType)_memory[ptr];
                                    if (Ast.IsReferenceType(baseT) && bytes >= 1 + sizeof(int))
                                    {
                                        int h = BitConverter.ToInt32(_memory, ptr + 1);
                                        if (h == oldPtr)
                                            BitConverter.GetBytes(newPtr).CopyTo(_memory, ptr + 1);
                                    }
                                }
                            }
                            else if (isArr)
                            {
                                if (Ast.IsReferenceType(vt))
                                {
                                    int count = bytes / sizeof(int);
                                    for (int i = 0; i < count; i++)
                                    {
                                        int at = ptr + i * sizeof(int);
                                        int h = BitConverter.ToInt32(_memory, at);
                                        if (h == oldPtr)
                                            BitConverter.GetBytes(newPtr).CopyTo(_memory, at);
                                    }
                                }
                            }
                            else
                            {
                                if (vt == Ast.ValueType.Tuple)
                                {
                                    int p = ptr, end = ptr + bytes;
                                    while (p < end)
                                    {
                                        var et = (Ast.ValueType)_memory[p++];
                                        if (Ast.IsReferenceType(et))
                                        {
                                            int h = BitConverter.ToInt32(_memory, p);
                                            if (h == oldPtr)
                                                BitConverter.GetBytes(newPtr).CopyTo(_memory, p);
                                            p += sizeof(int);
                                        }
                                        else
                                        {
                                            p += GetTypeSize(et);
                                        }
                                        // namePtr (string handle)
                                        int namePtr = BitConverter.ToInt32(_memory, p);
                                        if (namePtr == oldPtr)
                                            BitConverter.GetBytes(newPtr).CopyTo(_memory, p);
                                        p += sizeof(int);
                                    }
                                }
                                else
                                {
                                    if (bytes >= sizeof(int))
                                    {
                                        int sigPtr = BitConverter.ToInt32(_memory, ptr);
                                        bool isStructLike = vt == Ast.ValueType.Struct
                                            || (sigPtr >= StackSize && sigPtr < StackSize + _heapend
                                            && GetHeapObjectType(sigPtr) == Ast.ValueType.Byte);
                                        if (isStructLike && sigPtr >= StackSize && sigPtr < StackSize + _heapend)
                                        {
                                            int sigLen = GetHeapObjectLength(sigPtr);
                                            int s = 0;
                                            int val = ptr + sizeof(int);
                                            while (s < sigLen)
                                            {
                                                if (s + 1 > sigLen) break;
                                                var declared = (Ast.ValueType)_memory[sigPtr + s++];
                                                if (s >= sigLen) break;
                                                int nameLen = _memory[sigPtr + s++];
                                                s += nameLen;
                                                if (s >= sigLen) break;
                                                bool hasInit = _memory[sigPtr + s++] != 0;
                                                if (hasInit)
                                                {
                                                    s += GetTypeSize(declared);
                                                    if (s > sigLen) break;
                                                }
                                                if (val > ptr + bytes) break;
                                                var instFieldType = (Ast.ValueType)_memory[val++];
                                                int payloadSize = Ast.IsReferenceType(instFieldType) ? sizeof(int) : GetTypeSize(instFieldType);

                                                if (Ast.IsReferenceType(instFieldType))
                                                {
                                                    int h = BitConverter.ToInt32(_memory, val);
                                                    if (h == oldPtr)
                                                        BitConverter.GetBytes(newPtr).CopyTo(_memory, val);
                                                }
                                                val += payloadSize;
                                                if (val > ptr + bytes) break;
                                            }
                                        }
                                    }
                                    if (vt == Ast.ValueType.Dictionary && bytes >= 2)
                                    {
                                        var kt = (Ast.ValueType)_memory[ptr];
                                        var vt2 = (Ast.ValueType)_memory[ptr + 1];
                                        int ks = Ast.IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                                        int vs = Ast.IsReferenceType(vt2) ? sizeof(int) : GetTypeSize(vt2);

                                        int p = ptr + 2, end = ptr + bytes;
                                        while (p + ks + vs <= end)
                                        {
                                            if (Ast.IsReferenceType(kt))
                                            {
                                                int kptr = BitConverter.ToInt32(_memory, p);
                                                if (kptr == oldPtr)
                                                    BitConverter.GetBytes(newPtr).CopyTo(_memory, p);
                                            }
                                            if (Ast.IsReferenceType(vt2))
                                            {
                                                int vptr = BitConverter.ToInt32(_memory, p + ks);
                                                if (vptr == oldPtr)
                                                    BitConverter.GetBytes(newPtr).CopyTo(_memory, p + ks);
                                            }
                                            p += ks + vs;
                                        }
                                    }
                                }
                            }
                        }
                        pos += len;
                    }
                }
            }
            private void DefragmentFree()
            {
                int pos = 0;
                int lastUsedEnd = 0;

                while (pos < _heapend)
                {
                    int len = GetHeapObjectLength(pos + StackSize + HeaderSize) + HeaderSize;
                    bool used = IsUsed(pos + StackSize + HeaderSize);
                    if (!used)
                    {
                        int runStart = pos;
                        int runLen = len;

                        int next = pos + len;
                        while (next < _heapend)
                        {
                            int nHdrIdx = next + StackSize;
                            int nLen = GetHeapObjectLength(nHdrIdx + HeaderSize) + HeaderSize;
                            bool nUsed = IsUsed(nHdrIdx + HeaderSize);
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
            private int FindFreeBlock(int need, ValueType? requested = null, bool isArray = false)
            {

                if (_heapend + need > _memory.Length - StackSize)
                    throw new OutOfMemoryException();
                int pos = 0;
                while (pos < _heapend)
                {
                    int len = GetHeapObjectLength(pos + StackSize + HeaderSize) + HeaderSize;

                    if (!IsUsed(pos + HeaderSize + StackSize) && len >= need)
                    {
                        int spare = len - need;
                        ValueType vt = requested is null ? GetHeapObjectType(pos + HeaderSize + StackSize) : (ValueType)requested;
                        if (spare == 0)
                        {
                            WriteHeader(pos + StackSize, len, vt, used: true, isArray: isArray);
                            return pos + HeaderSize + StackSize;
                        }

                        if (spare >= HeaderSize)
                        {
                            WriteHeader(pos + StackSize, need, vt, used: true, isArray: isArray);
                            WriteHeader(pos + need + StackSize, len - need, ValueType.IntPtr, used: false, isArray: false);
                            return pos + HeaderSize + StackSize;
                        }


                        //return pos + HeaderSize + StackSize; 
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
                if (oldPtr >= StackSize)
                {
                    int capacity = GetHeapObjectLength(oldPtr);
                    if (bytes.Length <= capacity)
                    {
                        WriteBytes(oldPtr, bytes);
                        if (bytes.Length < capacity)
                            _memory.AsSpan(oldPtr + bytes.Length, capacity - bytes.Length).Clear();
                        return;
                    }
                    //not enough space, reallocate
                    Free(oldPtr);
                }
                else if (oldPtr != -1) throw new ArgumentOutOfRangeException($"Reallocating string with pointer in Stack {oldPtr}");
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
                if (et == ValueType.IntPtr && value is int ii) return ii;
                if (et == ValueType.Struct && value is int sp) return sp;
                if (et == ValueType.Class && value is int cp) return cp;
                if (et == ValueType.Tuple && value is int tp) return tp;
                if (et == ValueType.Dictionary && value is int dp) return dp;
                if (et == ValueType.Nullable)
                {
                    if (value is null) return -1;
                    var baseType = InferType(value);
                    if (IsReferenceType(baseType)) throw new ApplicationException("Nullable type can only be applicable to value types");
                    int bytes = 1 + GetTypeSize(baseType);
                    int ptr = Malloc(bytes, ValueType.Nullable);
                    _memory[ptr] = (byte)baseType;
                    var span = RawMemory.AsSpan(ptr + 1, bytes - 1);
                    ReadOnlySpan<byte> src = GetSourceBytes(baseType, value);
                    src.CopyTo(span);
                    return ptr;
                }
                if (et == ValueType.Array && value is ValueTuple<int, ValueType> info)
                {
                    var (len, elemType) = info;
                    int bytes = IsReferenceType(elemType) ? sizeof(int) * len : GetTypeSize(elemType) * len;
                    int basePtr = Malloc(bytes, elemType, isArray: true);
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
                if (len <= 0) return string.Empty;
                return Encoding.UTF8.GetString(raw.Slice(0, len));
            }
            #region Tuple
            public int AllocateTuple(List<(Ast.ValueType t, object? v, int namePtr)> list)
            {
                int total = 0;
                foreach (var (t, v, _) in list)
                    total += 1 + (Ast.IsReferenceType(t) ? sizeof(int) : GetTypeSize(t)) + sizeof(int);

                int ptr = Malloc(total, Ast.ValueType.Tuple, isArray: false);
                int w = ptr;
                foreach (var (t, v, namePtr) in list)
                {
                    _memory[w++] = (byte)t;

                    if (Ast.IsReferenceType(t))
                    {
                        int child;
                        if (v is null) child = -1;
                        else if (t == Ast.ValueType.String && v is string s) child = PackReference(s, Ast.ValueType.String);
                        else if (v is int p && p >= StackSize) child = p;
                        else if (t == Ast.ValueType.Object) child = PackReference(v, Ast.ValueType.Object);
                        else child = Convert.ToInt32(v);

                        BitConverter.GetBytes(child).CopyTo(_memory, w);
                        w += sizeof(int);
                    }
                    else
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(t, v);
                        src.CopyTo(_memory.AsSpan(w, src.Length));
                        w += src.Length;
                    }
                    BitConverter.GetBytes(namePtr).CopyTo(_memory, w);
                    w += sizeof(int);
                }
                return ptr;
            }
            public List<(Ast.ValueType type, object? value, int namePtr)> ReadTuple(int ptr)
            {
                if (GetHeapObjectType(ptr) != Ast.ValueType.Tuple)
                    throw new ApplicationException("Value is not a tuple");
                int end = ptr + GetHeapObjectLength(ptr);
                int r = ptr;
                List<(Ast.ValueType type, object? value, int namePtr)> ret = new();
                while (r < end)
                {
                    var t = (Ast.ValueType)_memory[r++];
                    object? val;

                    if (Ast.IsReferenceType(t))
                    {
                        int h = BitConverter.ToInt32(_memory, r); r += sizeof(int);
                        if (h <= 0) val = null;
                        else if (t == Ast.ValueType.String) val = ReadHeapString(h);
                        else if (t == Ast.ValueType.Object) val = GetObject(h);
                        else if (t == Ast.ValueType.Nullable)
                        {
                            var baseT = (Ast.ValueType)_memory[h];
                            int addr = h + 1;
                            val = Ast.IsReferenceType(baseT) ? DerefReference(addr, baseT) : ReadFromStack(addr, baseT);
                        }
                        else
                        {
                            val = h;
                        }
                    }
                    else
                    {
                        int sz = GetTypeSize(t);
                        ValidateAddress(r, sz);
                        val = ReadFromStack(r, t);
                        r += sz;
                    }

                    int namePtr = BitConverter.ToInt32(_memory, r); r += sizeof(int);
                    ret.Add((t, val, namePtr));
                }
                return ret;
            }
            public void DeconstructAssign(string[] names, object? rhs)
            {
                if (rhs is not int ptr || ptr < StackSize || GetHeapObjectType(ptr) != Ast.ValueType.Tuple)
                    throw new ApplicationException("Right side is not a tuple");

                var elems = ReadTuple(ptr);
                if (elems.Count != names.Length)
                    throw new ApplicationException($"Tuple arity mismatch: {elems.Count} vs {names.Length}");
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i];
                    if (name == "_") continue; //discard

                    var (t, v, _) = elems[i];

                    if (HasVariable(name))
                    {
                        var varInfo = Get(name);
                        if (varInfo.Type == Ast.ValueType.String)
                        {
                            int oldPtr = Convert.ToInt32(ReadVariable(name));

                            if (v is null)
                            {
                                if (oldPtr >= StackSize) Free(oldPtr);
                                WriteVariable(name, -1);
                            }
                            else if (v is int sptr && sptr >= StackSize)
                            {
                                if (oldPtr >= StackSize) Free(oldPtr);
                                WriteVariable(name, sptr);
                            }
                            else
                            {
                                StoreStringVariable(name, v.ToString()!);
                            }
                            continue;
                        }
                        if (Ast.IsReferenceType(varInfo.Type))
                        {
                            int oldPtr = Convert.ToInt32(ReadVariable(name));
                            if (oldPtr >= StackSize) Free(oldPtr);

                            int packed = (v is int p && p >= StackSize) ? p : PackReference(v!, varInfo.Type);

                            WriteVariable(name, packed);
                            continue;
                        }
                        if (!MatchVariableType(v, varInfo.Type) && varInfo.Type != Ast.ValueType.Object)
                            v = Cast(v!, varInfo.Type);

                        WriteVariable(name, v!);
                    }
                    else
                    {
                        var declType = t;
                        if (declType == Ast.ValueType.Tuple) declType = Ast.ValueType.Object;
                        Declare(name, declType, v!);
                    }
                }
            }
            #endregion
            #region Dictionary
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public (ValueType keyType, ValueType valueType) GetDictionaryElementTypes(int dictPtr)
            {
                ValidateAddress(dictPtr, 2);
                var kt = (ValueType)RawMemory[dictPtr];
                var vt = (ValueType)RawMemory[dictPtr + 1];
                return (kt, vt);
            }
            public int GetDictionaryCount(int dictPtr)
            {
                var (kt, vt) = GetDictionaryElementTypes(dictPtr);
                int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                int vs = IsReferenceType(vt) ? sizeof(int) : GetTypeSize(vt);
                int bytes = GetHeapObjectLength(dictPtr);
                if (bytes < 2) return 0;
                int payload = bytes - 2;
                if (payload < 0) return 0;
                int step = ks + vs;
                return step == 0 ? 0 : (payload / step);
            }
            public int AllocateDictionary(ValueType keyType, ValueType valueType, IEnumerable<(object? key, object? value)> pairs)
            {
                List<(object? key, object? value)> list = (pairs ?? Array.Empty<(object? key, object? value)>()).ToList();

                int ks = IsReferenceType(keyType) ? sizeof(int) : GetTypeSize(keyType);
                int vs = IsReferenceType(valueType) ? sizeof(int) : GetTypeSize(valueType);
                int total = 2 + checked(list.Count * (ks + vs));

                int ptr = Malloc(total, ValueType.Dictionary, isArray: false);
                RawMemory[ptr] = (byte)keyType;
                RawMemory[ptr + 1] = (byte)valueType;

                int pos = ptr + 2;
                foreach (var (k, v) in list)
                {
                    object? key = k;
                    object? val = v;

                    if (!Ast.MatchVariableType(key, keyType) && keyType != ValueType.Object)
                        key = key is null ? null : Cast(key, keyType);
                    if (!Ast.MatchVariableType(val, valueType) && valueType != ValueType.Object)
                        val = val is null ? null : Cast(val, valueType);
                    if (IsReferenceType(keyType))
                    {
                        int handle = PackReference(key!, keyType);
                        BitConverter.GetBytes(handle).CopyTo(RawMemory, pos);
                    }
                    else
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(keyType, key!);
                        src.CopyTo(RawMemory.AsSpan(pos, src.Length));
                    }
                    pos += ks;
                    if (IsReferenceType(valueType))
                    {
                        int handle = PackReference(val!, valueType);
                        BitConverter.GetBytes(handle).CopyTo(RawMemory, pos);
                    }
                    else
                    {
                        ReadOnlySpan<byte> src = GetSourceBytes(valueType, val!);
                        src.CopyTo(RawMemory.AsSpan(pos, src.Length));
                    }
                    pos += vs;
                }
                return ptr;
            }
            private static bool KeysEqual(ValueType kt, object? keyRequested, ReadOnlySpan<byte> storedKeyBytes, ExecutionContext ctx)
            {
                if (!IsReferenceType(kt))
                {
                    if (keyRequested is null) return false;
                    ReadOnlySpan<byte> rq = ctx.GetSourceBytes(kt, keyRequested);
                    return rq.SequenceEqual(storedKeyBytes);
                }

                if (kt == ValueType.String)
                {
                    int kptr = BitConverter.ToInt32(storedKeyBytes);
                    if (kptr <= 0) return keyRequested is null;
                    string stored = ctx.ReadHeapString(kptr);
                    return string.Equals(stored, keyRequested?.ToString(), StringComparison.Ordinal);
                }

                int sptr = BitConverter.ToInt32(storedKeyBytes);
                int rptr = (keyRequested is int ip) ? ip : -1;
                return sptr > 0 && rptr > 0 && sptr == rptr;
            }
            private int DictionaryFindValueAddress(int dictPtr, object? key, out ValueType valueType)
            {
                var (kt, vt) = GetDictionaryElementTypes(dictPtr);
                valueType = vt;

                int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                int vs = IsReferenceType(vt) ? sizeof(int) : GetTypeSize(vt);

                int bytes = GetHeapObjectLength(dictPtr);
                int pos = dictPtr + 2;
                int end = dictPtr + bytes;

                int step = ks + vs;
                while (pos + step <= end)
                {
                    if (KeysEqual(kt, key, RawMemory.AsSpan(pos, ks), this))
                        return pos + ks;
                    pos += step;
                }
                return -1;
            }
            public bool DictionaryContainsValue(int dictPtr, object? value)
            {
                var (kt, vt) = GetDictionaryElementTypes(dictPtr);
                if (!Ast.MatchVariableType(value, vt) && vt != ValueType.Object)
                    value = value is null ? null : Cast(value!, vt);
                int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                int vs = IsReferenceType(vt) ? sizeof(int) : GetTypeSize(vt);

                int bytes = GetHeapObjectLength(dictPtr);
                int pos = dictPtr + 2;
                int end = dictPtr + bytes;
                int step = ks + vs;
                while (pos + step <= end)
                {
                    int vaddr = pos + ks;

                    if (IsReferenceType(vt))
                    {
                        object? ev = DerefReference(vaddr, vt);
                        object? rv = value;

                        if (ev is null)
                        {
                            if (rv is null) return true;
                        }
                        else if (rv is null)
                        {
                            //not equal
                        }
                        else if (Equals(ev, rv))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        if (value is not null)
                        {
                            ReadOnlySpan<byte> stored = RawMemory.AsSpan(vaddr, vs);
                            ReadOnlySpan<byte> rq = GetSourceBytes(vt, value!);
                            if (rq.SequenceEqual(stored)) return true;
                        }
                    }
                    pos += step;
                }
                return false;
            }
            public bool DictionaryContainsKey(int dictPtr, object? key) => DictionaryFindValueAddress(dictPtr, key, out _) >= 0;
            public object? DictionaryGet(int dictPtr, object? key)
            {
                int addr = DictionaryFindValueAddress(dictPtr, key, out var vt);
                if (addr < 0) throw new KeyNotFoundException("Key not found");
                if (IsReferenceType(vt))
                {
                    int handle = BitConverter.ToInt32(RawMemory, addr);
                    return vt switch
                    {
                        ValueType.String => handle <= 0 ? null : ReadHeapString(handle),
                        ValueType.Object => handle,
                        ValueType.Array => handle <= 0 ? null : handle,
                        ValueType.Struct => handle <= 0 ? null : handle,
                        ValueType.Tuple => handle <= 0 ? null : handle,
                        ValueType.Nullable => handle <= 0 ? null : handle,
                        _ => handle <= 0 ? null : handle
                    };
                }
                else return ReadFromStack(addr, vt);
            }
            public int DictionarySet(int dictPtr, object? key, object? value)
            {
                int existingAddr = DictionaryFindValueAddress(dictPtr, key, out var vt);
                if (existingAddr >= 0)
                {
                    DictionarySetExisting(dictPtr, key, value);
                    return dictPtr;
                }
                var (kt, vt2) = GetDictionaryElementTypes(dictPtr);
                vt = vt2;
                if (!Ast.MatchVariableType(key, kt) && kt != ValueType.Object)
                    key = key is null ? null : Cast(key!, kt);
                if (!Ast.MatchVariableType(value, vt) && vt != ValueType.Object)
                    value = value is null ? null : Cast(value!, vt);
                int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                int vs = IsReferenceType(vt) ? sizeof(int) : GetTypeSize(vt);
                int oldBytes = GetHeapObjectLength(dictPtr);
                int needBytes = checked(oldBytes + ks + vs);

                int newPtr = Realloc(dictPtr, needBytes);

                int pos = newPtr + oldBytes;
                if (IsReferenceType(kt))
                {
                    int kh = PackReference(key!, kt);
                    BitConverter.GetBytes(kh).CopyTo(RawMemory, pos);
                }
                else
                {
                    ReadOnlySpan<byte> ksrc = GetSourceBytes(kt, key!);
                    ksrc.CopyTo(RawMemory.AsSpan(pos, ksrc.Length));
                }
                pos += ks;
                if (IsReferenceType(vt))
                {
                    int vh = PackReference(value!, vt);
                    BitConverter.GetBytes(vh).CopyTo(RawMemory, pos);
                }
                else
                {
                    ReadOnlySpan<byte> vsrc = GetSourceBytes(vt, value!);
                    vsrc.CopyTo(RawMemory.AsSpan(pos, vsrc.Length));
                }

                return newPtr;
            }
            public void DictionarySetExisting(int dictPtr, object? key, object? value)
            {
                int addr = DictionaryFindValueAddress(dictPtr, key, out var vt);
                if (addr < 0) throw new KeyNotFoundException("Key not found");

                if (!Ast.MatchVariableType(value, vt) && vt != ValueType.Object)
                    value = value is null ? null : Cast(value, vt);
                if (IsReferenceType(vt))
                {
                    int old = BitConverter.ToInt32(RawMemory, addr);
                    if (old >= StackSize && old < StackSize + MemoryUsed) Free(old);
                    int packed = PackReference(value!, vt);
                    BitConverter.GetBytes(packed).CopyTo(RawMemory, addr);
                }
                else
                {
                    ReadOnlySpan<byte> src = GetSourceBytes(vt, value);
                    ValidateAddress(addr, src.Length);
                    src.CopyTo(RawMemory.AsSpan(addr, src.Length));
                }
            }
            #endregion
            #region Array helpers
            public int ArrayResize(int oldPtr, int newLength)
            {
                if (newLength < 0) throw new ArgumentException(nameof(newLength), "Length must be positive");
                if (oldPtr < StackSize || oldPtr >= StackSize + MemoryUsed)
                    throw new ArgumentOutOfRangeException(nameof(oldPtr), "nullptr");
                var elemType = GetHeapObjectType(oldPtr);
                int elemSize = GetTypeSize(elemType);
                int oldBytes = GetHeapObjectLength(oldPtr);
                if (elemSize <= 0 || oldBytes % elemSize != 0)
                    throw new ApplicationException("Corrupted array header");
                int oldLength = oldBytes / elemSize;

                if (newLength == oldLength)
                    return oldPtr;

                int newBytes = checked(newLength * elemSize);
                if (newBytes == oldBytes) return oldPtr;
                else if (newBytes > oldBytes)
                {
                    return Realloc(oldPtr, newBytes);
                }
                int newPtr = Realloc(oldPtr, newBytes);
                if (newBytes > 0)
                    _memory.AsSpan(oldPtr, newBytes).CopyTo(_memory.AsSpan(newPtr, newBytes));
                if (elemType == ValueType.Object)
                {
                    for (int i = newLength; i < oldLength; i++)
                    {
                        int handle = BitConverter.ToInt32(_memory, oldPtr + i * sizeof(int));
                        if (handle > 0) ReleaseObject(handle);
                    }
                }
                if (newPtr != oldPtr) Free(oldPtr);
                return newPtr;
            }

            public int ArrayAdd(int ptr, object element)
            {
                var type = InferType(element);
                if (ptr < StackSize || ptr >= _memory.Length - sizeof(int))
                    throw new ArgumentOutOfRangeException("nullptr");

                var elemType = GetHeapObjectType(ptr);
                if (type != elemType)
                {
                    if (IsReferenceType(elemType))
                    {
                        if (element is int p)
                        {
                            if (p > 0 && elemType != ValueType.Object && p >= StackSize && p < StackSize + MemoryUsed)
                            {
                                var heapType = GetHeapObjectType(p);
                                if (heapType != elemType)
                                    throw new ArgumentException($"Type mismatch while adding pointer to {heapType} to {elemType}");
                            }
                            else if (elemType == ValueType.Object)
                            {
                                element = PackReference(element!, ValueType.Object);
                                type = ValueType.Int;
                            }
                            else if (elemType == ValueType.String && element is string)
                            {
                                //ok
                            }
                            else throw new ArgumentException($"Type mismatch while adding {type} to {elemType}");
                        }
                    }
                    else
                    {
                        element = Cast(element, elemType);
                        type = elemType;
                    }
                }

                int elemSize = GetTypeSize(elemType);
                int oldBytes = GetHeapObjectLength(ptr);
                int oldLength = oldBytes / elemSize;
                int needBytes = checked((oldLength + 1) * elemSize);
                ReadOnlySpan<byte> src = GetSourceBytes(element);
                int newPtr = Realloc(ptr, needBytes);

                WriteBytesAt(newPtr, oldBytes, src);
                return newPtr;
            }
            public int ArrayAddAt(int ptr, int index, object element)
            {
                var elemType = GetHeapObjectType(ptr);
                int elemSize = GetTypeSize(elemType);
                int bytes = GetHeapObjectLength(ptr);
                int length = bytes / elemSize;

                if (index < 0 || index > length)
                    throw new ArgumentOutOfRangeException(nameof(index), $"Specified argument '{index}' was out of the range of valid values.");
                var type = InferType(element);
                if (type != elemType)
                {
                    if (IsReferenceType(elemType))
                    {
                        if (element is int p)
                        {
                            if (p > 0 && elemType != ValueType.Object && p >= StackSize && p < StackSize + MemoryUsed)
                            {
                                var heapType = GetHeapObjectType(p);
                                if (heapType != elemType)
                                    throw new ArgumentException($"Type mismatch while adding pointer to {heapType} to {elemType}");
                            }
                            else if (elemType == ValueType.Object)
                            {
                                element = PackReference(element!, ValueType.Object);
                                type = ValueType.Int;
                            }
                            else if (elemType == ValueType.String && element is string)
                            {
                                //ok
                            }
                            else throw new ArgumentException($"Type mismatch while adding {type} to {elemType}");
                        }
                    }
                    else
                    {
                        element = Cast(element, elemType);
                        type = elemType;
                    }
                }

                int needBytes = checked((length + 1) * elemSize);
                int newPtr = Realloc(ptr, needBytes);
                int tailBytes = (length - index) * elemSize;
                if (tailBytes > 0)
                    _memory.AsSpan(newPtr + index * elemSize, tailBytes)
                           .CopyTo(_memory.AsSpan(newPtr + (index + 1) * elemSize, tailBytes));
                ReadOnlySpan<byte> src = GetSourceBytes(element);
                WriteBytesAt(newPtr, index * elemSize, src);

                return newPtr;
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
                    Check();
                    int addr = ptr + i * elemSize;
                    object current = IsReferenceType(elemType) ? DerefReference(addr, elemType)! : ReadFromStack(addr, elemType);

                    if ((current == null && element == null) || current?.Equals(element) == true)
                        return i;
                }
                return -1;
            }
            public int ArrayFindIndex(int ptr, object predicate)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                int lambdaId = Convert.ToInt32(predicate);

                for (int i = 0; i < len; i++)
                {
                    Check();
                    int addr = ptr + i * eSize;
                    object? val = IsReferenceType(elemType) ? DerefReference(addr, elemType) : ReadFromStack(addr, elemType);
                    if (Convert.ToBoolean(InvokeById(lambdaId, val!)))
                        return i;
                }
                return -1;
            }
            public int DictionaryRemove(int dictPtr, object? key)
            {
                int vaddr = DictionaryFindValueAddress(dictPtr, key, out _);
                if (vaddr < 0) return dictPtr;
                var (kt, vt) = GetDictionaryElementTypes(dictPtr);
                int ks = IsReferenceType(kt) ? sizeof(int) : GetTypeSize(kt);
                int vs = IsReferenceType(vt) ? sizeof(int) : GetTypeSize(vt);
                int bytes = GetHeapObjectLength(dictPtr);
                int keyPos = vaddr - ks;
                int toRemove = ks + vs;
                int newSize = bytes - toRemove;
                int newPtr = Realloc(dictPtr, newSize);
                int headLen = keyPos - dictPtr;
                if (headLen > 0)
                    _memory.AsSpan(dictPtr, headLen)
                           .CopyTo(_memory.AsSpan(newPtr, headLen));
                int tailSrc = keyPos + toRemove;
                int tailLen = (dictPtr + bytes) - tailSrc;
                if (tailLen > 0)
                    _memory.AsSpan(tailSrc, tailLen)
                           .CopyTo(_memory.AsSpan(newPtr + headLen, tailLen));
                if (newPtr != dictPtr) Free(dictPtr);
                return newPtr;
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
                int newPtr = Realloc(ptr, newBytes);
                int headBytes = index * elemSize;
                if (headBytes > 0)
                    _memory.AsSpan(ptr, headBytes)
                           .CopyTo(_memory.AsSpan(newPtr, headBytes));

                int tailBytes = (length - index - 1) * elemSize;
                if (tailBytes > 0)
                    _memory.AsSpan(ptr + (index + 1) * elemSize, tailBytes)
                           .CopyTo(_memory.AsSpan(newPtr + index * elemSize, tailBytes));

                if (newPtr != ptr) Free(ptr);
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
                int newPtr = Malloc(newBytes, elemType1, isArray: true);

                _memory.AsSpan(firstPtr, bytes1).CopyTo(_memory.AsSpan(newPtr, bytes1));
                _memory.AsSpan(secondPtr, bytes2).CopyTo(_memory.AsSpan(newPtr + bytes1, bytes2));

                return newPtr;

            }
            public int ArrayReverse(int ptr)
            {
                var et = this.GetHeapObjectType(ptr);
                int len = this.GetArrayLength(ptr), sz = this.GetTypeSize(et);
                int np = this.Malloc(len * sz, et, isArray: true);
                for (int i = 0; i < len; i++)
                    this.RawMemory.AsSpan(ptr + i * sz, sz).CopyTo(this.RawMemory.AsSpan(np + (len - 1 - i) * sz, sz));
                this.Free(ptr);
                return np;
            }
            public int ArrayDistinct(int ptr)
            {
                var et = this.GetHeapObjectType(ptr);
                int len = this.GetArrayLength(ptr), sz = this.GetTypeSize(et);
                var seen = new HashSet<object?>();
                var buf = new List<object?>();
                for (int i = 0; i < len; i++)
                {
                    object? v = Ast.IsReferenceType(et)
                        ? this.DerefReference(ptr + i * sz, et)
                        : this.ReadFromStack(ptr + i * sz, et);
                    if (seen.Add(v)) buf.Add(v);
                }
                return this.PackReference((buf.ToArray(), et), Ast.ValueType.Array);
            }
            public double ArrayAverage(int ptr)
            {
                var t = this.GetHeapObjectType(ptr);
                if (t is not (Ast.ValueType.Int or Ast.ValueType.Long or Ast.ValueType.Float or Ast.ValueType.Double or Ast.ValueType.Decimal))
                    throw new ApplicationException("Average: numeric arrays only");
                int n = this.GetArrayLength(ptr);
                if (n == 0) throw new InvalidOperationException("Empty sequence");
                decimal sum = 0;
                for (int i = 0; i < n; i++) sum += Convert.ToDecimal(this.ReadFromStack(ptr + i * this.GetTypeSize(t), t), CultureInfo.InvariantCulture);
                return (double)(sum / n);
            }
            public int ArraySort(int ptr, bool asc = true)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                object?[] buf = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    Check();
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
                    newPtr = Malloc(sizeof(int) * len, elemType, isArray: true);
                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    newPtr = Malloc(eSize * len, elemType, isArray: true);
                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    var ka = a == null ? null : InvokeById(lambdaId, a);
                    var kb = b == null ? null : InvokeById(lambdaId, b);

                    int cmp = Comparer<object>.Default.Compare(ka ?? 0, kb ?? 0);
                    return asc ? cmp : -cmp;
                });

                int newPtr;
                if (IsReferenceType(elemType))
                {
                    newPtr = Malloc(sizeof(int) * len, elemType, isArray: true);
                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    newPtr = Malloc(eSize * len, elemType, isArray: true);
                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    dst[i] = InvokeById(lambdaId, src[i]!);

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
                    newPtr = Malloc(sizeof(int) * len, dstElemType, isArray: true);
                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    newPtr = Malloc(dstSize * len, dstElemType, isArray: true);

                    for (int i = 0; i < len; i++)
                    {
                        Check();
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
                    if (Convert.ToBoolean(v == null ? null : InvokeById(lambdaId, v)))
                        passed.Add(v);

                int outLen = passed.Count;
                int newPtr;
                if (IsReferenceType(elemType))
                {
                    newPtr = Malloc(sizeof(int) * outLen, elemType, isArray: true);
                    for (int i = 0; i < outLen; i++)
                    {
                        Check();
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
                    newPtr = Malloc(eSize * outLen, elemType, isArray: true);
                    for (int i = 0; i < outLen; i++)
                    {
                        Check();
                        ReadOnlySpan<byte> src = GetSourceBytes(elemType, passed[i]);
                        src.CopyTo(RawMemory.AsSpan(newPtr + i * eSize, eSize));
                    }
                }
                return newPtr;
            }
            public int ArrayRange(int start, int end)
            {
                int len = Math.Abs(end - start);
                int ptr = Malloc(sizeof(int) * len, ValueType.Int, isArray: true);

                for (int i = 0, v = start, step = end >= start ? 1 : -1; i < len; i++, v += step)
                {
                    Check();
                    BitConverter.GetBytes(v).CopyTo(RawMemory, ptr + i * sizeof(int));
                }
                return ptr;
            }
            public int ArraySlice(int ptr, int? from = null, int? to = null)
            {
                var elemType = GetHeapObjectType(ptr);
                int elemSize = GetTypeSize(elemType);
                int length = GetArrayLength(ptr);

                int start = from ?? 0;
                int end = to ?? length;
                if (start < 0 || start > end || end > length)
                    throw new ArgumentOutOfRangeException($"slice [{start}..{end}] of length {length}");

                int sliceLen = end - start;
                if (sliceLen == 0)
                    return Malloc(0, elemType, isArray: true);

                int newPtr = Malloc(sliceLen * elemSize, elemType, isArray: true);
                _memory.AsSpan(ptr + start * elemSize, sliceLen * elemSize)
                       .CopyTo(_memory.AsSpan(newPtr, sliceLen * elemSize));

                return newPtr;
            }

            public bool ArrayAny(int ptr, object predicate)
            {
                var elemType = GetHeapObjectType(ptr);
                int len = GetArrayLength(ptr);
                int eSize = GetTypeSize(elemType);
                int lambdaId = Convert.ToInt32(predicate);

                for (int i = 0; i < len; i++)
                {
                    Check();
                    int addr = ptr + i * eSize;
                    object? val = IsReferenceType(elemType)
                                    ? DerefReference(addr, elemType)
                                    : ReadFromStack(addr, elemType);
                    if (Convert.ToBoolean(InvokeById(lambdaId, val!)))
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
                    Check();
                    int addr = ptr + i * eSize;
                    object? val = IsReferenceType(elemType)
                                    ? DerefReference(addr, elemType)
                                    : ReadFromStack(addr, elemType);
                    if (!Convert.ToBoolean(InvokeById(lambdaId, val!)))
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
                    Check();
                    object? v = IsReferenceType(et)
                                ? DerefReference(ptr + i * sz, et)
                                : ReadFromStack(ptr + i * sz, et);
                    object? key = v == null ? null : InvokeById(lid, v);
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
                    Check();
                    object? v = IsReferenceType(et)
                                ? DerefReference(ptr + i * sz, et)
                                : ReadFromStack(ptr + i * sz, et);
                    if (!Convert.ToBoolean(v == null ? null : InvokeById(lid, v))) continue;

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
                    Check();
                    int a = ptr + i * sz;
                    object? v = IsReferenceType(et) ? DerefReference(a, et) : ReadFromStack(a, et);
                    if (Convert.ToBoolean(v == null ? null : InvokeById(lid, v))) cnt++;
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
                    Check();
                    object? v = IsReferenceType(et) ? DerefReference(ptr + i * sz, et) : ReadFromStack(ptr + i * sz, et);
                    acc += Convert.ToInt32(v == null ? null : InvokeById(lid, v));
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
                    case ValueType.DateTime: return BitConverter.GetBytes(((DateTime)data!).Ticks);
                    case ValueType.TimeSpan: return BitConverter.GetBytes(((TimeSpan)data!).Ticks);
                    default:
                        throw new ApplicationException($"GetSourceBytes: Unsupported element type {vt}");
                }
            }
            public ReadOnlySpan<byte> GetSourceBytes(object? data)
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
                else if (data is DateTime dt) return BitConverter.GetBytes(dt.Ticks);
                else if (data is TimeSpan ts) return BitConverter.GetBytes(ts.Ticks);
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
            public static object? DefaultValue(ValueType vt) => vt switch
            {
                Ast.ValueType.Bool => false,
                Ast.ValueType.Char => '\0',
                Ast.ValueType.Byte => (byte)0,
                Ast.ValueType.Sbyte => (sbyte)0,
                Ast.ValueType.Short => (short)0,
                Ast.ValueType.UShort => (ushort)0,
                Ast.ValueType.Int => 0,
                Ast.ValueType.Uint => 0U,
                Ast.ValueType.Long => 0L,
                Ast.ValueType.Ulong => 0UL,
                Ast.ValueType.Float => 0f,
                Ast.ValueType.Double => 0d,
                Ast.ValueType.Decimal => 0m,
                Ast.ValueType.IntPtr => 0,
                Ast.ValueType.DateTime => new DateTime(0),
                Ast.ValueType.TimeSpan => new TimeSpan(0),
                _ => null
            };
            public object? DerefReference(int addr, ValueType vt)
            {
                int handle = BitConverter.ToInt32(_memory, addr);
                switch (vt)
                {
                    case Ast.ValueType.String: return handle <= 0 ? null : ReadHeapString(handle);
                    case Ast.ValueType.Object: return GetObject(handle);
                    case Ast.ValueType.Array: return handle <= 0 ? null : handle;
                    case Ast.ValueType.Struct: return handle <= 0 ? null : handle;
                    case Ast.ValueType.Nullable:
                        if (handle <= 0) return null; var baseT = (ValueType)RawMemory[handle];
                        return ReadFromMemorySlice(RawMemory[(handle + 1)..(handle + 1 + GetTypeSize(baseT))], baseT);
                    default: return handle <= 0 ? null : handle;
                }
            }
            public object? ReadByAddress(int addr)
            {
                var target = GetVariableByAddress(addr);
                if (target is null) throw new InvalidOperationException($"Reference points to invalid stack memory {addr}");
                var vt = target.Value.Type;
                return IsReferenceType(vt) ? DerefReference(addr, vt) : ReadFromStack(addr, vt);
            }
            public void AssignByAddress(int addr, object? value)
            {
                var target = GetVariableByAddress(addr);
                if (target is null) throw new InvalidOperationException($"Reference points to invalid stack memory {addr}");
                var vt = target.Value.Type;

                if (!Ast.MatchVariableType(value, vt) && vt != ValueType.Object)
                    value = Cast(value!, vt);

                if (IsReferenceType(vt))
                {
                    int oldPtr = BitConverter.ToInt32(_memory, addr);
                    if (oldPtr >= StackSize) Free(oldPtr);
                    int packed = PackReference(value!, vt);
                    BitConverter.GetBytes(packed).CopyTo(_memory, addr);
                }
                else
                {
                    ReadOnlySpan<byte> src = GetSourceBytes(vt, value!);
                    src.CopyTo(_memory.AsSpan(addr, src.Length));
                }
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
                if (name.Length > 200) throw new ApplicationException("Variable name is too large");
                if (_scopes.Peek().ContainsKey(name))
                    throw new ApplicationException($"Variable '{name}' already declared");
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
                        _totalVars++;
                        WriteVariable(name, address);
                        WriteBytes(address, bytes);
                    }
                    else if (value is null)
                    {
                        //_scopes.Peek()[name] = new Variable(type, -1, sizeof(int));
                        _scopes.Peek()[name] = Stackalloc(ValueType.String);
                        _totalVars++;
                        WriteVariable(name, -1);
                    }
                    else throw new ApplicationException("Declaring string with non-string value");
                }
                else if (type == ValueType.Nullable)
                {
                    var variable = Stackalloc(ValueType.Nullable);
                    _scopes.Peek()[name] = variable;
                    _totalVars++;
                    int pointer = value is null ? -1 : PackReference(value, ValueType.Nullable);
                    WriteVariable(name, pointer);
                    return;
                }
                else if (type == ValueType.Array)
                {
                    if (value is ValueTuple<int, ValueType> info)
                    {
                        if (!IsReferenceType(info.Item2))
                        {
                            int length = GetTypeSize(info.Item2) * info.Item1;
                            var address = Malloc(length, info.Item2, isArray: true);
                            var pointer = Stackalloc(ValueType.Array);
                            _scopes.Peek()[name] = pointer;
                            _totalVars++;
                            WriteVariable(name, address);
                        }
                        else if (IsReferenceType(info.Item2))
                        {
                            int bytes = sizeof(int) * info.Item1;
                            int basePtr = Malloc(bytes, info.Item2, isArray: true);
                            _memory.AsSpan(basePtr, bytes).Fill(0xFF);
                            var varSlot = Stackalloc(ValueType.Array);
                            _scopes.Peek()[name] = varSlot;
                            _totalVars++;
                            WriteVariable(name, basePtr);
                        }
                    }
                    else if (value is ValueTuple<object?[], ValueType> arr)
                    {
                        object?[] vals = arr.Item1;
                        ValueType elemT = arr.Item2;
                        int basePtr;
                        if (IsReferenceType(elemT))
                        {
                            basePtr = Malloc(sizeof(int) * vals.Length, elemT, isArray: true);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                int packed = PackReference(vals[i]!, elemT);
                                BitConverter.GetBytes(packed).CopyTo(_memory, basePtr + i * sizeof(int));
                            }
                        }
                        else
                        {
                            int elemSize = GetTypeSize(elemT);
                            basePtr = Malloc(elemSize * vals.Length, elemT, isArray: true);
                            for (int i = 0; i < vals.Length; i++)
                            {
                                ReadOnlySpan<byte> src = GetSourceBytes(elemT, vals[i]!);
                                src.CopyTo(_memory.AsSpan(basePtr + i * elemSize, elemSize));
                            }
                        }
                        var slot = Stackalloc(ValueType.Array);
                        _scopes.Peek()[name] = slot;
                        _totalVars++;
                        WriteVariable(name, basePtr);
                    }
                    else if (value is int ptr)
                    {
                        var slot = Stackalloc(ValueType.Array);
                        _scopes.Peek()[name] = slot;
                        _totalVars++;
                        WriteVariable(name, ptr);
                    }
                    else if (value is null)
                    {
                        _scopes.Peek()[name] = Stackalloc(ValueType.Array);
                        _totalVars++;
                        WriteVariable(name, -1);
                    }
                    else throw new ApplicationException("Declaring array pointer with non-array data");
                }
                else
                {

                    if (type == ValueType.Object && value is not null) type = InferType(value);
                    if (type == ValueType.String)
                    {
                        Declare(name, ValueType.String, value);
                        return;
                    }

                    if (type == ValueType.Object && value is ValueTuple<int, bool> obj)
                    {
                        var bytes = BitConverter.GetBytes(obj.Item1);
                        var address = Malloc(GetTypeSize(ValueType.Object), ValueType.Object);
                        var pointer = Stackalloc(ValueType.Object);
                        _scopes.Peek()[name] = pointer;
                        _totalVars++;
                        WriteVariable(name, address);
                        WriteBytes(address, bytes);
                        return;
                    }
                    var variable = Stackalloc(type);
                    _scopes.Peek()[name] = variable;
                    _totalVars++;
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
            public static Ast.ValueType InferType(object v)
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
                    case TimeSpan: return ValueType.TimeSpan;
                    case DateTime: return ValueType.DateTime;
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
                        ValueType.Nullable => value is null ? null! : PackReference(value, ValueType.Nullable),

                        ValueType.DateTime => value is string ds ? DateTime.Parse(ds, CultureInfo.InvariantCulture) : (value is long li ? new DateTime(li) : value),
                        ValueType.TimeSpan => value is string ts ? TimeSpan.Parse(ts, CultureInfo.InvariantCulture) : (value is long lt ? new TimeSpan(lt) : value),

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
                if (id is Ast.ReturnSignal idSig)
                    id = idSig.Value ?? throw new ApplicationException("Cannot invoke null function id");

                string name = id?.ToString()!;

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
                    var raw = fn.Body.Evaluate(this);
                    if (raw is Ast.ReturnSignal rs)
                    {
                        var val = rs.Value;
                        if (rs.PinKey != -1) Unpin(rs.PinKey);
                        return val!;
                    }
                    return raw!;
                }
                finally
                {
                    ExitScope();
                    ExitFunction();
                }
            }

            #endregion
            #region Struct helpers
            public int GetStructFieldOffset(int instPtr, string field, out ValueType vt)
            {
                int sigPtr = BitConverter.ToInt32(_memory, instPtr);
                int sigLen = GetHeapObjectLength(sigPtr);

                int sigPos = 0;
                int dataOffs = 4;

                while (sigPos < sigLen)
                {
                    vt = (ValueType)_memory[sigPtr + sigPos++];
                    int nameLen = _memory[sigPtr + sigPos++];
                    string name = Encoding.UTF8.GetString(_memory, sigPtr + sigPos, nameLen);
                    sigPos += nameLen;
                    byte hasInit = _memory[sigPtr + sigPos++];
                    if (hasInit == 1)
                    {
                        int sz = GetTypeSize(vt);
                        sigPos += sz;
                    }
                    if (name == field) return dataOffs;

                    dataOffs += 1 + GetTypeSize(vt);
                }
                throw new ApplicationException($"Field '{field}' not found");
            }
            public void WriteStructField(int instPtr, string field, object value)
            {
                int off = GetStructFieldOffset(instPtr, field, out var vt) + 1;
                if (!Ast.MatchVariableType(value, vt) && vt != ValueType.Object)
                    value = Cast(value, vt);

                if (IsReferenceType(vt))
                {
                    int oldPtr = BitConverter.ToInt32(_memory, instPtr + off);
                    if (oldPtr >= StackSize) Free(oldPtr);
                    int packed = PackReference(value, vt);
                    BitConverter.GetBytes(packed).CopyTo(_memory, instPtr + off);
                }
                else
                {
                    ReadOnlySpan<byte> src = GetSourceBytes(vt, value);
                    src.CopyTo(_memory.AsSpan(instPtr + off, src.Length));
                }
            }
            #region Seialization
            static ReadOnlySpan<byte> TRUEu8 => new byte[] { (byte)'t', (byte)'r', (byte)'u', (byte)'e' };
            static ReadOnlySpan<byte> FALSEu8 => new byte[] { (byte)'f', (byte)'a', (byte)'l', (byte)'s', (byte)'e' };
            static ReadOnlySpan<byte> NULLu8 => new byte[] { (byte)'n', (byte)'u', (byte)'l', (byte)'l' };
            static ReadOnlySpan<byte> TUPLEu8 => new byte[] { (byte)'$', (byte)'t', (byte)'u', (byte)'p', (byte)'l', (byte)'e' };
            static ReadOnlySpan<byte> NAMESu8 => new byte[] { (byte)'$', (byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)'s' };
            public int SerializeJson(int anyPtr, int depthLimit = 8)
            {
                const int MaxOutBytes = 256 * 1024;
                const int HardMaxDepth = 32;
                const int MaxStringBytes = 64 * 1024;
                const int MaxArrayElements = 16_384;
                const int MaxStructFields = 1_024;
                byte[] mem = RawMemory;
                const byte QUOTE = (byte)'"';
                const byte BSL = (byte)'\\';
                const byte LBR = (byte)'{';
                const byte RBR = (byte)'}';
                const byte LSB = (byte)'[';
                const byte RSB = (byte)']';
                const byte COL = (byte)':';
                const byte COM = (byte)',';
                byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1024);
                int w = 0;
                void Ensure(int add)
                {
                    if (add < 0) throw new ApplicationException("Serialization internal error");
                    long need = (long)w + add;
                    if (need > MaxOutBytes) throw new ApplicationException("Serialization Limit Exceeded");
                    if (need <= buf.Length) return;
                    int target = (int)need;
                    int next = buf.Length << 1;
                    if (next < target) next = target;
                    if (next > MaxOutBytes) next = MaxOutBytes;
                    if (next < target) throw new ApplicationException("Serialization Limit Exceeded");
                    byte[] n = System.Buffers.ArrayPool<byte>.Shared.Rent(System.Math.Max(buf.Length << 1, w + add));
                    System.Buffer.BlockCopy(buf, 0, n, 0, w);
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf); buf = n;
                }
                void PutEscByte(byte b)
                {
                    if (b == QUOTE || b == BSL) { Ensure(2); buf[w++] = BSL; buf[w++] = b; return; }
                    if (b >= 0x20) { Ensure(1); buf[w++] = b; return; }

                    Ensure(6);
                    buf[w++] = (byte)'\\'; buf[w++] = (byte)'u';
                    buf[w++] = (byte)'0'; buf[w++] = (byte)'0';
                    const string HEX = "0123456789ABCDEF";
                    buf[w++] = (byte)HEX[(b >> 4) & 0xF];
                    buf[w++] = (byte)HEX[b & 0xF];
                }
                void PutQuotedUtf8(ReadOnlySpan<byte> raw)
                {
                    Ensure(1); buf[w++] = QUOTE;
                    for (int i = 0; i < raw.Length; i++) PutEscByte(raw[i]);
                    Ensure(1); buf[w++] = QUOTE;
                }

                static bool IsRef(Ast.ValueType vt) => Ast.IsReferenceType(vt);
                void PutBool(bool v) { var s = v ? TRUEu8 : FALSEu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; }
                void PutInt32(int v) { Ensure(11); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c); w += c; }
                void PutUInt32(uint v) { Ensure(11); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c); w += c; }
                void PutInt64(long v) { Ensure(20); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c); w += c; }
                void PutUInt64(ulong v) { Ensure(20); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c); w += c; }
                void PutFloat(float v) { Ensure(24); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c, new System.Buffers.StandardFormat('G')); w += c; }
                void PutDouble(double v) { Ensure(24); System.Buffers.Text.Utf8Formatter.TryFormat(v, buf.AsSpan(w), out int c, new System.Buffers.StandardFormat('G')); w += c; }
                void PutChar(ushort ch) { PutQuotedUtf8(System.Text.Encoding.UTF8.GetBytes(((char)ch).ToString())); }
                int NormalizeInstancePtr(int p)
                {
                    if (p < StackSize)
                    {
                        int h = BitConverter.ToInt32(mem, p);
                        return (h >= StackSize) ? h : -1;
                    }
                    return p;
                }
                ReadOnlySpan<byte> HeapSpan(int ptr)
                {
                    int len = GetHeapObjectLength(ptr);
                    return mem.AsSpan(ptr, len);
                }
                void PutStringFromHeap(int ptr)
                {
                    if (ptr < StackSize) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                    var raw = HeapSpan(ptr);
                    PutQuotedUtf8(raw);
                }
                void PutNullableFromHeap(int ptr)
                {
                    if (ptr < StackSize) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                    var baseT = (Ast.ValueType)mem[ptr];
                    int valAddr = ptr + 1;
                    PutValue(baseT, valAddr, isHeapPayload: true, depth: 0);
                }
                void PutTupleFromHeap(int ptr, int depth)
                {
                    if (depth <= 0 || ptr < StackSize) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                    var items = ReadTuple(ptr);
                    bool anyNames = false;
                    int count = 0;
                    foreach (var it in items) { if (it.namePtr >= StackSize) anyNames = true; count++; }
                    void PutManagedString(string s)
                    {
                        Ensure(1); buf[w++] = QUOTE;
                        var raw = System.Text.Encoding.UTF8.GetBytes(s);
                        for (int i = 0; i < raw.Length; i++) PutEscByte(raw[i]);
                        Ensure(1); buf[w++] = QUOTE;
                    }
                    void PutTupleValue(Ast.ValueType et, object? v, int d)
                    {
                        if (v is null) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }

                        if (Ast.IsReferenceType(et))
                        {
                            if (et == Ast.ValueType.String) { PutManagedString((string)v); return; }
                            if (et == Ast.ValueType.Struct) { PutStructInstance(Convert.ToInt32(v), d - 1); return; }
                            if (et == Ast.ValueType.Array) { PutArrayFromHeap(Convert.ToInt32(v), d - 1); return; }
                            if (et == Ast.ValueType.Nullable)
                            {
                                var ov = v;
                                if (ov is string sv) { PutManagedString(sv); return; }
                                if (ov is int hp)
                                {
                                    var ht = GetHeapObjectType(hp);
                                    if (ht == Ast.ValueType.Struct) { PutStructInstance(hp, d - 1); return; }
                                    if (ht == Ast.ValueType.Array) { PutArrayFromHeap(hp, d - 1); return; }
                                }
                            }
                            var sp = NULLu8; Ensure(sp.Length); sp.CopyTo(buf.AsSpan(w)); w += sp.Length; return;
                        }

                        // value types
                        switch (et)
                        {
                            case Ast.ValueType.Bool: PutBool(Convert.ToBoolean(v)); break;
                            case Ast.ValueType.Int: PutInt32(Convert.ToInt32(v)); break;
                            case Ast.ValueType.Uint: PutUInt32(Convert.ToUInt32(v)); break;
                            case Ast.ValueType.Long: PutInt64(Convert.ToInt64(v)); break;
                            case Ast.ValueType.Ulong: PutUInt64(Convert.ToUInt64(v)); break;
                            case Ast.ValueType.Double: PutDouble(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                            case Ast.ValueType.Float: PutFloat(Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                            case Ast.ValueType.Char: PutChar(Convert.ToUInt16(v)); break;
                            default:
                                PutManagedString(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                                break;
                        }
                    }
                    if (!anyNames)
                    {
                        Ensure(1); buf[w++] = LSB;
                        int idx = 0;
                        foreach (var (et, val, _) in ReadTuple(ptr))
                        {
                            if (idx++ != 0) { Ensure(1); buf[w++] = COM; }
                            PutTupleValue(et, val, depth);
                        }
                        Ensure(1); buf[w++] = RSB;
                    }
                    else
                    {
                        Ensure(1); buf[w++] = LBR;
                        // "$tuple"
                        Ensure(1); buf[w++] = QUOTE; var k1 = TUPLEu8; Ensure(k1.Length); k1.CopyTo(buf.AsSpan(w)); w += k1.Length; Ensure(1); buf[w++] = QUOTE; Ensure(1); buf[w++] = COL;
                        Ensure(1); buf[w++] = LSB;
                        int idx = 0;
                        var again = ReadTuple(ptr);
                        foreach (var (et, val, _) in again)
                        {
                            if (idx++ != 0) { Ensure(1); buf[w++] = COM; }
                            PutTupleValue(et, val, depth);
                        }
                        Ensure(1); buf[w++] = RSB;
                        // "$names"
                        Ensure(1); buf[w++] = COM;
                        Ensure(1); buf[w++] = QUOTE; var k2 = NAMESu8; Ensure(k2.Length); k2.CopyTo(buf.AsSpan(w)); w += k2.Length; Ensure(1); buf[w++] = QUOTE; Ensure(1); buf[w++] = COL;
                        Ensure(1); buf[w++] = LSB;
                        idx = 0;
                        foreach (var (_, __, namePtr) in ReadTuple(ptr))
                        {
                            if (idx++ != 0) { Ensure(1); buf[w++] = COM; }
                            string name = namePtr >= StackSize ? ReadHeapString(namePtr) : $"Item{idx}";
                            PutManagedString(name);
                        }
                        Ensure(1); buf[w++] = RSB;

                        Ensure(1); buf[w++] = RBR;
                    }
                }
                void PutArrayFromHeap(int ptr, int depth)
                {
                    bool GetArrayMeta(int ptr, out int len)
                    {
                        len = 0;
                        try { len = GetArrayLength(ptr); }
                        catch { return false; }
                        if (len < 0 || len > MaxArrayElements) return false;
                        return true;
                    }
                    if (depth <= 0) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                    if (ptr < StackSize || !GetArrayMeta(ptr, out int len)) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                    var et = GetHeapObjectType(ptr);
                    Ensure(1); buf[w++] = LSB;
                    for (int i = 0; i < len; i++)
                    {
                        Check();
                        if (i != 0) { Ensure(1); buf[w++] = COM; }
                        if (!Ast.IsReferenceType(et))
                        {
                            int es = GetTypeSize(et);
                            long off = (long)i * es;
                            if (off > int.MaxValue) throw new ApplicationException("Serialization Limit Exceeded");
                            PutValue(et, ptr + (int)off, isHeapPayload: true, depth: depth);
                        }
                        else
                        {
                            long off = (long)i * sizeof(int);
                            if (off > int.MaxValue) throw new ApplicationException("Serialization Limit Exceeded");
                            int h = BitConverter.ToInt32(mem, (ptr + (int)off));
                            if (h <= 0) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; continue; }
                            if (et == Ast.ValueType.String) PutStringFromHeap(h);
                            else if (et == Ast.ValueType.Struct) PutStructInstance(h, depth - 1);
                            else if (et == Ast.ValueType.Array) PutArrayFromHeap(h, depth - 1);
                            else if (et == Ast.ValueType.Nullable) PutNullableFromHeap(h);
                            else { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; }
                        }
                    }
                    Ensure(1); buf[w++] = RSB;
                }
                void PutValue(Ast.ValueType vt, int addr, bool isHeapPayload, int depth)
                {
                    if (Ast.IsReferenceType(vt))
                    {
                        int h = BitConverter.ToInt32(mem, addr);
                        if (h <= 0) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; return; }
                        if (vt == Ast.ValueType.String) { PutStringFromHeap(h); return; }
                        if (vt == Ast.ValueType.Struct) { PutStructInstance(h, depth - 1); return; }
                        if (vt == Ast.ValueType.Array) { PutArrayFromHeap(h, depth - 1); return; }
                        if (vt == Ast.ValueType.Nullable) { PutNullableFromHeap(h); return; }
                        var sp = NULLu8; Ensure(sp.Length); sp.CopyTo(buf.AsSpan(w)); w += sp.Length;
                        return;
                    }
                    switch (vt)
                    {
                        case Ast.ValueType.Bool: PutBool(mem[addr] != 0); break;
                        case Ast.ValueType.Byte: PutUInt32(mem[addr]); break;
                        case Ast.ValueType.Sbyte: PutInt32(unchecked((sbyte)mem[addr])); break;
                        case Ast.ValueType.Short: PutInt32(BitConverter.ToInt16(mem, addr)); break;
                        case Ast.ValueType.UShort: PutUInt32(BitConverter.ToUInt16(mem, addr)); break;
                        case Ast.ValueType.Int: PutInt32(BitConverter.ToInt32(mem, addr)); break;
                        case Ast.ValueType.Uint: PutUInt32(BitConverter.ToUInt32(mem, addr)); break;
                        case Ast.ValueType.Long: PutInt64(BitConverter.ToInt64(mem, addr)); break;
                        case Ast.ValueType.Ulong: PutUInt64(BitConverter.ToUInt64(mem, addr)); break;
                        case Ast.ValueType.DateTime: PutInt64(BitConverter.ToInt64(mem, addr)); break;
                        case Ast.ValueType.TimeSpan: PutInt64(BitConverter.ToInt64(mem, addr)); break;
                        case Ast.ValueType.Float: PutFloat(BitConverter.ToSingle(mem, addr)); break;
                        case Ast.ValueType.Double: PutDouble(BitConverter.ToDouble(mem, addr)); break;
                        case Ast.ValueType.Char: PutChar(BitConverter.ToUInt16(mem, addr)); break;
                        case Ast.ValueType.Enum: PutInt32(BitConverter.ToInt32(mem, addr)); break;
                        default: var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; break;
                    }
                }
                void PutStructInstance(int instPtr, int depth)
                {
                    if (depth <= 0) { var sp = NULLu8; Ensure(sp.Length); sp.CopyTo(buf.AsSpan(w)); w += sp.Length; return; }
                    if (instPtr < StackSize) { var sp = NULLu8; Ensure(sp.Length); sp.CopyTo(buf.AsSpan(w)); w += sp.Length; return; }

                    int sigPtr = BitConverter.ToInt32(mem, instPtr);
                    if (sigPtr < StackSize) { var sp = NULLu8; Ensure(sp.Length); sp.CopyTo(buf.AsSpan(w)); w += sp.Length; return; }
                    var sig = HeapSpan(sigPtr);
                    int sigLen = sig.Length;
                    int s = 0;

                    int val = instPtr + sizeof(int);
                    Ensure(1); buf[w++] = LBR;
                    bool first = true;
                    int fields = 0;
                    while (s < sigLen)
                    {
                        Check();
                        if (fields++ >= MaxStructFields) throw new ApplicationException("Too many struct fields");
                        Ast.ValueType fieldType = (Ast.ValueType)sig[s++];
                        if (s >= sigLen) break;
                        int nameLen = sig[s++];
                        if (nameLen < 0 || nameLen > MaxStringBytes) throw new ApplicationException("Field name too large");
                        if (s + nameLen + 1 > sigLen) break;
                        ReadOnlySpan<byte> nameBytes = sig.Slice(s, nameLen);
                        s += nameLen;
                        bool hasInit = sig[s++] != 0;
                        if (hasInit) s += GetTypeSize(fieldType);

                        Ast.ValueType instFieldType = (Ast.ValueType)mem[val++];
                        int payloadSize = IsRef(instFieldType) ? sizeof(int) : GetTypeSize(instFieldType);

                        if (!first) { Ensure(1); buf[w++] = COM; }
                        first = false;
                        Ensure(1); buf[w++] = QUOTE; for (int i = 0; i < nameBytes.Length; i++) PutEscByte(nameBytes[i]); Ensure(1); buf[w++] = QUOTE; Ensure(1); buf[w++] = COL;
                        PutValue(instFieldType, val, isHeapPayload: true, depth: depth);

                        val += payloadSize;
                    }
                    Ensure(1); buf[w++] = RBR;
                }
                depthLimit = depthLimit < 0 ? 0 : (depthLimit > HardMaxDepth ? HardMaxDepth : depthLimit);
                int inst = NormalizeInstancePtr(anyPtr);
                if (inst < 0) { var s = NULLu8; Ensure(s.Length); s.CopyTo(buf.AsSpan(w)); w += s.Length; }
                else
                {
                    var t = GetHeapObjectType(inst);
                    if (t == Ast.ValueType.String) { PutStringFromHeap(inst); }
                    else if (t == Ast.ValueType.Nullable) { PutNullableFromHeap(inst); }
                    else if (t == Ast.ValueType.Tuple) { PutTupleFromHeap(inst, depthLimit); }
                    else
                    {
                        int sigPtr = BitConverter.ToInt32(mem, inst);
                        bool looksLikeStruct = sigPtr >= StackSize && GetHeapObjectType(sigPtr) == Ast.ValueType.Byte;
                        if (looksLikeStruct) PutStructInstance(inst, depthLimit);
                        else PutArrayFromHeap(inst, depthLimit);
                    }
                }
                try
                {
                    int ptr = Malloc(w, Ast.ValueType.String);
                    WriteBytes(ptr, buf.AsSpan(0, w));
                    return ptr;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                }
            }
            public int DeserializeJson(int ptr)
            {
                string text = ReadHeapString(ptr);
                if (string.IsNullOrWhiteSpace(text)) return -1;
                int i = 0;
                char Peek() => i < text.Length ? text[i] : '\0';
                char Next() => i < text.Length ? text[i++] : '\0';
                void SkipWs() { while (i < text.Length && char.IsWhiteSpace(text[i])) i++; }
                bool Consume(char c) { SkipWs(); if (Peek() == c) { i++; return true; } return false; }
                void Expect(char c) { SkipWs(); if (Next() != c) throw new ApplicationException($"Expected '{c}' at {i}"); }
                string ParseString()
                {
                    SkipWs();
                    if (Next() != '\"') throw new ApplicationException($"Expected string at {i}");
                    var sb = new StringBuilder();
                    while (true)
                    {
                        Check();
                        if (i >= text.Length) throw new ApplicationException("Unterminated string");
                        char c = Next();
                        if (c == '\"') break;
                        if (c != '\\') { sb.Append(c); continue; }
                        if (i >= text.Length) throw new ApplicationException("Bad escape");
                        c = Next();
                        switch (c)
                        {
                            case '\"': sb.Append('\"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > text.Length) throw new ApplicationException("Bad \\u escape");
                                ushort code = Convert.ToUInt16(text.Substring(i, 4), 16);
                                sb.Append((char)code);
                                i += 4;
                                break;
                            default: throw new ApplicationException($"Bad escape '\\{c}'");
                        }
                    }
                    return sb.ToString();
                }
                object? ParseNumber()
                {
                    SkipWs();
                    int start = i;
                    if (Peek() == '-') i++;
                    bool hasDot = false, hasExp = false;
                    while (i < text.Length)
                    {
                        Check();
                        char c = text[i];
                        if (char.IsDigit(c)) { i++; continue; }
                        if (c == '.' && !hasDot) { hasDot = true; i++; continue; }
                        if ((c == 'e' || c == 'E') && !hasExp)
                        {
                            hasExp = true; i++;
                            if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                            continue;
                        }
                        break;
                    }
                    string num = text.Substring(start, i - start);
                    if (!hasDot && !hasExp)
                    {
                        if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                        {
                            if (l >= int.MinValue && l <= int.MaxValue) return (int)l;
                            return l;
                        }
                    }
                    double d = double.Parse(num, CultureInfo.InvariantCulture);
                    return d;
                }
                List<object?> ParseArray()
                {
                    Expect('[');
                    var list = new List<object?>();
                    SkipWs();
                    if (Consume(']')) return list;
                    while (true)
                    {
                        Check();
                        list.Add(ParseValue());
                        SkipWs();
                        if (Consume(']')) break;
                        Expect(',');
                    }
                    return list;
                }
                object? ParseValue()
                {
                    SkipWs();
                    char c = Peek();
                    if (c == '\"') return ParseString();
                    if (c == '{') return ParseObject();
                    if (c == '[') return ParseArray();
                    if (c == 't') { if (text.Substring(i, System.Math.Min(4, text.Length - i)) != "true") throw new ApplicationException("Expected true"); i += 4; return true; }
                    if (c == 'f') { if (text.Substring(i, System.Math.Min(5, text.Length - i)) != "false") throw new ApplicationException("Expected false"); i += 5; return false; }
                    if (c == 'n') { if (text.Substring(i, System.Math.Min(4, text.Length - i)) != "null") throw new ApplicationException("Expected null"); i += 4; return null; }
                    return ParseNumber();
                }
                List<(string Key, object? Val)> ParseObject()
                {
                    Expect('{');
                    var obj = new System.Collections.Generic.List<(string, object?)>();
                    SkipWs();
                    if (Consume('}')) return obj;
                    while (true)
                    {
                        Check();
                        string key = ParseString();
                        Expect(':');
                        object? val = ParseValue();
                        obj.Add((key, val));
                        SkipWs();
                        if (Consume('}')) break;
                        Expect(',');
                    }
                    return obj;
                }
                int MakeNullable(ValueType baseType, object? value)
                {
                    if (value is null) return -1;
                    int bytes = 1 + GetTypeSize(baseType);
                    int p = Malloc(bytes, ValueType.Nullable);
                    RawMemory[p] = (byte)baseType;
                    ReadOnlySpan<byte> src = GetSourceBytes(baseType, Cast(value, baseType));
                    src.CopyTo(RawMemory.AsSpan(p + 1, bytes - 1));
                    return p;
                }

                ValueType InferScalarType(object v)
                {
                    return v switch
                    {
                        int => ValueType.Int,
                        long => ValueType.Long,
                        double => ValueType.Double,
                        bool => ValueType.Bool,
                        string => ValueType.String,
                        _ => ValueType.Object
                    };
                }
                int BuildObject(List<(string Key, object? Val)> fields)
                {
                    int tupleIdx = fields.FindIndex(p => p.Key == "$tuple");
                    if (tupleIdx >= 0)
                    {
                        var rawVals = fields[tupleIdx].Val as List<object?> ?? new List<object?>();
                        var rawNames = (fields.Find(p => p.Key == "$names").Val as List<object?>);
                        var items = new List<(Ast.ValueType Type, object? Value, int NamePtr)>(rawVals.Count);
                        int i = 0;
                        foreach (var v in rawVals)
                        {
                            Ast.ValueType et;
                            object? payload;
                            switch (v)
                            {
                                case null:
                                    et = Ast.ValueType.Object; payload = null; break;
                                case string s:
                                    et = Ast.ValueType.String; payload = s; break;
                                case bool b:
                                    et = Ast.ValueType.Bool; payload = b; break;
                                case int iv:
                                    et = Ast.ValueType.Int; payload = iv; break;
                                case long lv:
                                    et = Ast.ValueType.Long; payload = lv; break;
                                case double dv:
                                    et = Ast.ValueType.Double; payload = dv; break;

                                case List<object?> arr:
                                    et = Ast.ValueType.Array; payload = BuildArray(arr, out _); break;

                                case List<(string Key, object? Val)> o2:
                                    et = Ast.ValueType.Struct; payload = BuildObject(o2); break;

                                default:
                                    et = Ast.ValueType.String; payload = v.ToString(); break;
                            }

                            int namePtr = -1;
                            if (rawNames != null && i < rawNames.Count && rawNames[i] is string ns)
                                namePtr = PackReference(ns, Ast.ValueType.String);

                            items.Add((et, payload, namePtr));
                            i++;
                        }
                        return AllocateTuple(items);
                    }
                    var names = fields;
                    var declTypes = new List<ValueType>(names.Count);
                    var instTypes = new List<ValueType>(names.Count);
                    var payloads = new List<object?>(names.Count);

                    foreach (var (k, v) in names)
                    {
                        if (v is null)
                        {
                            declTypes.Add(ValueType.Object);
                            instTypes.Add(ValueType.Object);
                            payloads.Add(-1);
                            continue;
                        }

                        switch (v)
                        {
                            case string s:
                                declTypes.Add(ValueType.String);
                                instTypes.Add(ValueType.String);
                                payloads.Add(PackReference(s, ValueType.String));
                                break;

                            case bool b:
                                declTypes.Add(ValueType.Bool);
                                instTypes.Add(ValueType.Bool);
                                payloads.Add(b);
                                break;

                            case int or long or double:
                                {
                                    var t = InferScalarType(v);
                                    declTypes.Add(t);
                                    instTypes.Add(t);
                                    payloads.Add(v);
                                    break;
                                }

                            case List<object?> arr:
                                {
                                    int ap = BuildArray(arr, out var elemHeaderType);
                                    declTypes.Add(ValueType.Array);
                                    instTypes.Add(ValueType.Array);
                                    payloads.Add(ap);
                                    break;
                                }

                            case List<(string Key, object? Val)> obj:
                                {
                                    int sp = BuildObject(obj);
                                    declTypes.Add(ValueType.Struct);
                                    instTypes.Add(ValueType.Struct);
                                    payloads.Add(sp);
                                    break;
                                }

                            default:
                                throw new ApplicationException($"Struct: unsupported value type {v.GetType()}");
                        }
                    }

                    var sigBuf = new List<byte>();
                    for (int idx = 0; idx < names.Count; idx++)
                    {
                        Check();
                        ValueType vt = declTypes[idx];
                        sigBuf.Add((byte)vt);
                        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(names[idx].Key);
                        if (nameBytes.Length > 255) throw new ApplicationException("Field name too long");
                        sigBuf.Add((byte)nameBytes.Length);
                        sigBuf.AddRange(nameBytes);
                        sigBuf.Add(0);
                    }
                    int sigPtr = Malloc(sigBuf.Count, ValueType.Byte);
                    WriteBytes(sigPtr, sigBuf.ToArray());

                    int total = 4;
                    for (int idx = 0; idx < declTypes.Count; idx++)
                        total += 1 + GetTypeSize(declTypes[idx]);
                    int instPtr = Malloc(total, ValueType.Struct);
                    BitConverter.GetBytes(sigPtr).CopyTo(RawMemory, instPtr);
                    int pos = instPtr + 4;
                    for (int idx = 0; idx < declTypes.Count; idx++)
                    {
                        Check();
                        RawMemory[pos++] = (byte)instTypes[idx];
                        var declared = declTypes[idx];
                        if (IsReferenceType(declared))
                        {
                            int refVal = payloads[idx] is int pval ? pval : PackReference(payloads[idx], declared);
                            BitConverter.GetBytes(refVal).CopyTo(RawMemory, pos);
                        }
                        else
                        {
                            ReadOnlySpan<byte> src = GetSourceBytes(declared, payloads[idx]!);
                            src.CopyTo(RawMemory.AsSpan(pos, src.Length));
                        }
                        pos += GetTypeSize(declared);
                    }
                    return instPtr;
                }
                int BuildArray(List<object?> items, out ValueType elemHeaderType)
                {
                    bool anyNull = false, anyStr = false, anyBool = false, anyArr = false, anyObj = false;
                    bool anyDouble = false, anyLong = false, anyInt = false;
                    foreach (var v in items)
                    {
                        Check();
                        if (v is null) { anyNull = true; continue; }
                        switch (v)
                        {
                            case string: anyStr = true; break;
                            case bool: anyBool = true; break;
                            case double: anyDouble = true; break;
                            case long: anyLong = true; break;
                            case int iv:
                                if (iv >= StackSize && GetHeapObjectType(iv) == ValueType.Struct)
                                    anyObj = true;
                                else
                                    anyInt = true;
                                break;
                            case List<object?>: anyArr = true; break;
                            case List<(string Key, object? Val)>: anyObj = true; break;
                            default: throw new ApplicationException($"Array: unsupported element {v.GetType()}");
                        }
                    }

                    int cats = (anyStr ? 1 : 0) + (anyBool ? 1 : 0) + ((anyInt || anyLong || anyDouble) ? 1 : 0) + (anyArr ? 1 : 0) + (anyObj ? 1 : 0);
                    if (cats > 1) throw new ApplicationException("Array: heterogeneous arrays are not supported");

                    if (anyStr) elemHeaderType = ValueType.String;
                    else if (anyBool && !(anyInt || anyLong || anyDouble) && !anyArr && !anyObj) elemHeaderType = anyNull ? ValueType.Nullable : ValueType.Bool;
                    else if (anyArr) elemHeaderType = ValueType.Array;
                    else if (anyObj) elemHeaderType = ValueType.Struct;
                    else if (anyInt || anyLong || anyDouble)
                    {
                        elemHeaderType = anyDouble ? ValueType.Double : (anyLong ? ValueType.Long : ValueType.Int);
                        if (anyNull) elemHeaderType = ValueType.Nullable;
                    }
                    else
                    {
                        elemHeaderType = ValueType.Object;
                    }

                    int n = items.Count;

                    if (elemHeaderType == ValueType.Nullable)
                    {
                        int baseType = (anyStr || anyArr || anyObj) ? (int)ValueType.Object
                                    : (anyDouble ? (int)ValueType.Double
                                    : (anyLong ? (int)ValueType.Long
                                    : (anyBool ? (int)ValueType.Bool : (int)ValueType.Int)));
                        if ((ValueType)baseType == ValueType.Object || (ValueType)baseType == ValueType.String)
                            throw new ApplicationException("Array: nullable supported only for value types");

                        int basePtr = Malloc(sizeof(int) * n, ValueType.Nullable, isArray: true);
                        for (int idx = 0; idx < n; idx++)
                        {
                            object? v = items[idx];
                            int np = v is null ? -1 : MakeNullable((ValueType)baseType, v);
                            BitConverter.GetBytes(np).CopyTo(RawMemory, basePtr + idx * sizeof(int));
                        }
                        return basePtr;
                    }

                    if (IsReferenceType(elemHeaderType))
                    {
                        int basePtr = Malloc(sizeof(int) * n, elemHeaderType, isArray: true);
                        for (int idx = 0; idx < n; idx++)
                        {
                            Check();
                            int cell = basePtr + idx * sizeof(int);
                            object? v = items[idx];
                            int p = -1;
                            if (v is null) p = -1;
                            else if (elemHeaderType == ValueType.String) p = PackReference((string)v, ValueType.String);
                            else if (elemHeaderType == ValueType.Array) p = BuildArray((System.Collections.Generic.List<object?>)v, out _);
                            else if (elemHeaderType == ValueType.Struct)
                            {
                                if (v is int sp)
                                    p = sp;
                                else
                                    p = BuildObject((List<(string Key, object? Val)>)v);
                            }
                            else p = -1;
                            BitConverter.GetBytes(p).CopyTo(RawMemory, cell);
                        }
                        return basePtr;
                    }
                    else
                    {
                        int elemSize = GetTypeSize(elemHeaderType);
                        int basePtr = Malloc(n * elemSize, elemHeaderType, isArray: true);
                        for (int idx = 0; idx < n; idx++)
                        {
                            Check();
                            object? v = items[idx];
                            if (v is null) throw new ApplicationException("Array: null in non nullable value array");
                            object vv = elemHeaderType switch
                            {
                                ValueType.Double => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
                                ValueType.Long => v is int ii ? (long)ii : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture),
                                _ => v
                            };
                            ReadOnlySpan<byte> src = GetSourceBytes(elemHeaderType, vv);
                            src.CopyTo(RawMemory.AsSpan(basePtr + idx * elemSize, elemSize));
                        }
                        return basePtr;
                    }
                }
                SkipWs();
                object? root = ParseValue();
                SkipWs();
                if (i != text.Length) throw new ApplicationException("Trailing characters");

                return root switch
                {
                    null => -1,
                    string s => PackReference(s, ValueType.String),
                    bool b => MakeNullable(ValueType.Bool, b),
                    int v => MakeNullable(ValueType.Int, v),
                    long v => MakeNullable(ValueType.Long, v),
                    double v => MakeNullable(ValueType.Double, v),
                    List<object?> arr => BuildArray(arr, out _),
                    List<(string Key, object? Val)> obj => BuildObject(obj),
                    _ => throw new ApplicationException($"Root unsupported: {root.GetType()}")
                };
            }
            #endregion
            #endregion
        }
    }
}
