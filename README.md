# Sandboxed C# Interpreter
![build](https://img.shields.io/badge/build-passing-brightgreen) ![dotnet](https://img.shields.io/badge/.NET-6.0+-blue) ![aot](https://img.shields.io/badge/nativeAOT-compatible-brightgreen) ![unity](https://img.shields.io/badge/Unity-compatible-brightgreen)

C#-like isolated interpeter designed to be embeded in .NET applications for remote code execution or as a DSL.
Should be safe to use with untrusted code.
---

## Features

- Compatible with most of C# syntax apart from OOP semantics, events, extentions, some struct syntax, and a standart library.
- All of memory is isolated internally within a byte array with concrete limits.
- All of the code can be timed out, avoiding infinite loops and CPU pressure.
- Recursion depth, scopes, variable declarations and more are strictly limited to secure the host.
- None of the internal methods can access native objects or host resourses, unless explicily declared by the host.

---

## Initialize

```cs
string code = File.ReadAllText("code.cs");
using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
{
	var ast = new Ast(cts.Token);
	string output = ast.Interpret(code, consoleOutput: true, printTree: false, printMemory: false);
}
```

### Create native interface
You can access your native application exclusively through delegates.
Here is an example of creating an interface to use an interpretor for remote command execution (using attributes).
```cs
using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var args = message.Content.Split(' ');
    var ast = new Ast(cts.Token, totalMemory: 1024*31, stackSize: 1024); //in bytes
    code = $"InvokeByAttribute(\"Command\", GetAttributeParams(), GetParams());\nreturn;\n{code}";
    ast.Context.RegisterNative("GetAttributeParams", () => { return new string[] { args[0].Replace(prefix, "") }; });
    ast.Context.RegisterNative("GetParams", () => { return args.Skip(1).Select(x => (object)x).ToArray(); });
    
    string output = ast.Interpret(code, consoleOutput: false, printTree: false);
    SendEmbed(message, output).GetAwaiter().GetResult();
}
```
---