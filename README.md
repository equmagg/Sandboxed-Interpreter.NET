# Sandboxed Interpreter.NET
![build](https://img.shields.io/badge/build-passing-brightgreen) ![dotnet](https://img.shields.io/badge/.NET-8.0-blue) ![aot](https://img.shields.io/badge/nativeAOT-compatible-brightgreen)

C#-like isolated interpeter designed to be embeded in .NET applications for remote code execution.
Should be safe to use with untrusted code.
---

## Features

- Compatible with most of C# syntax apart from classes, structures, and a standart library.
- All of memory is isolated internally within a byte array.
- All of the code can be timed out, avoiding infinite loops and CPU pressure.
- Recursion depth, scopes, variable declarations and more are strictly limited to secure the host.
- None of the internal methods can access native objects, or host resourses, unless explicily declared by the host.

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
Here is an example of creating an interface for Discord.NET to use interpretor for command execution.
```cs
using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var args = message.Content.Split(' ');
    var ast = new Ast(cts.Token, totalMemory: 1024*21, stackSize: 1024); //in bytes
    code = $"InvokeByAttribute(\"Command\", GetAttributeParams(), GetParams());\nreturn;\n" + code;
    ast.Context.RegisterNative("GetAttributeParams", () => { return new string[] { args[0].Replace(prefix, "") }; });
    ast.Context.RegisterNative("GetParams", () => { return args.Skip(1).Select(x => (object)x).ToArray(); });
    ast.Context.RegisterNative("SendEmbed", (string str) => { SendEmbed(message, (str)).GetAwaiter().GetResult(); } );
    ast.Context.RegisterNative("GetComponent", () => { return new ComponentBuilder(); } );
    
    string output = ast.Interpret(code, consoleOutput: false, printTree: false);
    SendEmbed(message, output).GetAwaiter().GetResult();
}
```
---