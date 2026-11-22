using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DarkSwordsman.AgentBridge
{
    // exec (code=<C#> | file=<.cs>): compile a method body with Mono's CodeDom (mcs) against every loaded
    // assembly and run it on the main thread. For the questions the fixed commands can't answer.
    internal static partial class Commands
    {
        private const string SnippetClass = "AgentBridgeSnippet";

        // Shared with the snippet; `Random` and `Object` would otherwise be ambiguous.
        private static readonly string[] SnippetUsings =
        {
            "System", "System.Collections", "System.Collections.Generic", "System.IO", "System.Linq", "System.Reflection", "System.Text",
            "UnityEditor", "UnityEditor.SceneManagement", "UnityEngine", "UnityEngine.SceneManagement",
            "Object = UnityEngine.Object", "Random = UnityEngine.Random", "Debug = UnityEngine.Debug",
        };

        private static readonly Regex SnippetFrame = new Regex(@"\s*\[0x[0-9a-f]+\] in .*?(?:[\\/]snippet:(\d+)|[\\/]exec[\\/]\w+\.cs:\d+|<\w+>:0)$");

        private static readonly Regex UsingLine = new Regex(@"^\s*using\s+(?!var\b|static\b)[\w.]+(\s*=\s*[\w.<>, ]+)?\s*;\s*$");

        // Mono can't unload assemblies, so every distinct snippet stays loaded until the next domain reload.
        // Identical snippets reuse theirs.
        private static readonly Dictionary<string, Assembly> SnippetCache = new Dictionary<string, Assembly>();

        // exec code=<statements or one expression> | file=<path to .cs, relative to the project>
        private static string Exec(Args a)
        {
            string code = a.Get("code"), file = a.Get("file");
            if (code == null && file == null) throw new ArgumentException("give code=<C#> or file=<path to a .cs snippet>");
            if (file != null)
            {
                if (!File.Exists(file)) throw new ArgumentException($"no file {Path.GetFullPath(file)}");
                code = File.ReadAllText(file);
                // ub passes code that has newlines in a side file next to the request.
                if (Path.GetFullPath(file).StartsWith(Path.GetFullPath(AgentBridge.Root), StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            }

            // One line not ending in ';' or '}' is an expression: return it, or run it as a statement if it's void.
            Assembly asm;
            List<string> errors;
            var expression = false;
            var trimmed = code.Trim();
            if (!trimmed.Contains("\n") && !trimmed.EndsWith(";") && !trimmed.EndsWith("}"))
            {
                asm = Compile($"return {trimmed};", out errors);
                expression = asm != null;
                // A void call can't be returned. Otherwise keep the expression's errors; they're the clearer ones.
                if (asm == null) asm = Compile($"{trimmed};", out _);
            }
            else asm = Compile(code, out errors);
            if (asm == null) throw new ArgumentException("compile failed\n" + string.Join("\n", errors));

            Snippet.Out.Clear();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Agent Bridge exec");
            var group = Undo.GetCurrentGroup();
            object result;
            try
            {
                result = asm.GetType(SnippetClass).GetMethod("Run").Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                var e = tie.InnerException ?? tie;
                // Keep the frames worth reading: not the bridge's helpers, and snippet lines as "snippet line N".
                var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim())
                    .TakeWhile(l => !l.Contains("System.Reflection"))
                    .Where(l => !l.Contains("DarkSwordsman.AgentBridge") && !l.StartsWith("at AgentBridgeSnippet.Find") && !l.StartsWith("at AgentBridgeSnippet.Print"))
                    .Select(l => SnippetFrame.Replace(l, m => m.Groups[1].Success ? $" (snippet line {m.Groups[1].Value})" : ""))
                    .Take(8);
                return $"ERROR: {e.GetType().Name}: {e.Message}\n{string.Join("\n", frames)}\n" + (Snippet.Out.Length > 0 ? "printed before the error:\n" + Snippet.Out : "");
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }

            var sb = new StringBuilder(Snippet.Out.ToString());
            if (result != null || expression) sb.AppendLine(Snippet.Format(result));
            return sb.Length > 0 ? sb.ToString() : "ok (no output; return a value or Print(...) to see one)\n";
        }

        // Compiles `body` as the body of `object Run()`. Leading `using X;` lines move above the class, and
        // `#line 1` keeps error line numbers matching the snippet.
        private static Assembly Compile(string body, out List<string> errors)
        {
            errors = new List<string>();
            var lines = body.Replace("\r\n", "\n").Split('\n');
            var usings = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith("//")) continue;
                if (!UsingLine.IsMatch(l)) break;
                usings.Add(l);
                lines[i] = "";
            }

            var src = new StringBuilder();
            foreach (var u in SnippetUsings) src.AppendLine($"using {u};");
            foreach (var u in usings) src.AppendLine(u);
            src.AppendLine($"public static class {SnippetClass} {{");
            src.AppendLine("    static void Print(object o) => DarkSwordsman.AgentBridge.Snippet.Print(o);");
            src.AppendLine("    static GameObject Find(string scenePath) => DarkSwordsman.AgentBridge.Snippet.Find(scenePath);");
            src.AppendLine("    public static object Run() {");
            src.AppendLine("#line 1 \"snippet\"");
            foreach (var l in lines) src.AppendLine(l);
            src.AppendLine("#line default");
            src.AppendLine("        return null;");
            src.AppendLine("    }");
            src.AppendLine("}");
            var source = src.ToString();
            if (SnippetCache.TryGetValue(source, out var cached)) return cached;

            var dir = Path.Combine(AgentBridge.Root, "exec");
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                var roslyn = RoslynPaths();
                if (roslyn != null) CompileRoslyn(roslyn.Value.dotnet, roslyn.Value.csc, source, dll, errors);
                else CompileCodeDom(source, dll, errors);
                if (errors.Count > 0) return null;
                if (!File.Exists(dll)) { errors.Add("the compiler reported success but wrote no assembly"); return null; }
                var pdb = Path.ChangeExtension(dll, ".pdb");
                // With symbols, runtime errors name the snippet line.
                var asm = File.Exists(pdb) ? Assembly.Load(File.ReadAllBytes(dll), File.ReadAllBytes(pdb)) : Assembly.Load(File.ReadAllBytes(dll));
                if (SnippetCache.Count >= 64) SnippetCache.Clear();
                SnippetCache[source] = asm;
                return asm;
            }
            finally
            {
                try { File.Delete(dll); File.Delete(Path.ChangeExtension(dll, ".pdb")); } catch (IOException) { }
            }
        }

        // Unity 2021.2+ ships the Roslyn compiler it builds scripts with, plus a .NET runtime to run it.
        // It takes current C#, which Mono's mcs (the CodeDom fallback) doesn't.
        private static (string dotnet, string csc)? RoslynPaths()
        {
            var data = EditorApplication.applicationContentsPath;
            var csc = Path.Combine(data, "DotNetSdkRoslyn", "csc.dll");
            var dotnet = Path.Combine(data, "NetCoreRuntime", Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
            return File.Exists(csc) && File.Exists(dotnet) ? (dotnet, csc) : ((string, string)?)null;
        }

        private static readonly Regex CscError = new Regex(@"\((\d+),\d+\): error (CS\d+): (.*)$");

        private static void CompileRoslyn(string dotnet, string csc, string source, string dll, List<string> errors)
        {
            var src = Path.ChangeExtension(dll, ".cs");
            File.WriteAllText(src, source);
            var rsp = References(forCodeDom: false);
            // /shared reuses Unity's warm compiler server when there is one; a cold compile takes a few seconds.
            var psi = new System.Diagnostics.ProcessStartInfo(dotnet,
                $"exec \"{csc}\" /shared /nologo /noconfig /nostdlib+ /target:library /langversion:latest /optimize- /debug:portable /nowarn:162,1701,1702 " +
                $"/out:\"{dll}\" @\"{rsp}\" \"{src}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var stderr = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (InvalidOperationException) { } errors.Add("csc took over 60 s"); return; }
                    foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    {
                        var m = CscError.Match(line);
                        if (m.Success) errors.Add($"line {m.Groups[1].Value}: {m.Groups[2].Value} {m.Groups[3].Value}");
                        else if (line.Contains("error")) errors.Add(line);
                    }
                    if (p.ExitCode != 0 && errors.Count == 0) errors.Add($"csc exited with {p.ExitCode}: {output.Trim()} {stderr.Result.Trim()}");
                }
            }
            finally
            {
                try { File.Delete(src); } catch (IOException) { }
            }
        }

        private static void CompileCodeDom(string source, string dll, List<string> errors)
        {
            using (var provider = new CSharpCodeProvider())
            {
                var p = new CompilerParameters { GenerateInMemory = false, OutputAssembly = dll, CompilerOptions = $"@\"{References(forCodeDom: true)}\" /nowarn:162" };
                var results = provider.CompileAssemblyFromSource(p, source);
                foreach (CompilerError e in results.Errors)
                {
                    if (e.IsWarning) continue;
                    // mcs prints a BOM on stdout that Mono's CodeDom reports as an error with no number or text.
                    if (string.IsNullOrEmpty(e.ErrorNumber) && (e.ErrorText ?? "").Trim('\uFEFF', ' ', '\t', '\r', '\n') == "") continue;
                    errors.Add(e.Line > 0 ? $"line {e.Line}: {e.ErrorNumber} {e.ErrorText}" : $"{e.ErrorNumber} {e.ErrorText}");
                }
            }
        }

        // A response file of every loaded assembly with a file, one per name (the highest version): a project's
        // worth of /r: flags passes Windows' 32 KB command line limit. mcs adds mscorlib itself, and with
        // netstandard alongside, its facades define the same types twice; Roslyn (/nostdlib) needs them all.
        private static string References(bool forCodeDom)
        {
            var key = forCodeDom ? "codedom" : "roslyn";
            if (RefsFiles.TryGetValue(key, out var cached) && File.Exists(cached)) return cached;
            var skip = forCodeDom
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mscorlib", "System.Runtime", "System.Private.CoreLib", "System.Collections" }
                : new HashSet<string>();
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(x => !x.IsDynamic)
                .Select(x => { try { return x.Location; } catch (NotSupportedException) { return null; } })
                .Where(l => !string.IsNullOrEmpty(l) && File.Exists(l))
                .Select(l => { try { return (path: l, name: AssemblyName.GetAssemblyName(l)); } catch (Exception) { return (path: l, name: null); } })
                .Where(x => x.name == null || !skip.Contains(x.name.Name))
                .GroupBy(x => x.name?.Name ?? x.path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.name?.Version).First().path);
            var rsp = Path.Combine(AgentBridge.Root, "exec", key + ".rsp");
            File.WriteAllLines(rsp, refs.Select(r => $"/r:\"{r}\""));
            return RefsFiles[key] = rsp;
        }

        private static readonly Dictionary<string, string> RefsFiles = new Dictionary<string, string>();

        // A new domain means new assemblies (and paths) to reference.
        [InitializeOnLoadMethod]
        private static void ResetSnippets()
        {
            RefsFiles.Clear();
            SnippetCache.Clear();
        }
    }

    // Helpers that snippets call. Public, because a snippet is its own assembly.
    public static class Snippet
    {
        internal static readonly StringBuilder Out = new StringBuilder();

        public static void Print(object o) => Out.AppendLine(Format(o));

        // A scene path as the other commands take it (inactive objects included).
        public static GameObject Find(string scenePath) => Commands.FindForSnippet(scenePath);

        // Objects print the way `props` prints references; collections print one item per line.
        public static string Format(object o)
        {
            switch (o)
            {
                case null: return "null";
                case string s: return s;
                case Object u: return u == null ? "null (destroyed)" : Commands.DescribeForSnippet(u);
                case IDictionary d: return string.Join("\n", d.Keys.Cast<object>().Select(k => $"{Format(k)}: {Format(d[k])}"));
                case IEnumerable e: return string.Join("\n", e.Cast<object>().Select(Format));
                case float f: return f.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                case double v: return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                default: return o.ToString();
            }
        }
    }

    internal static partial class Commands
    {
        internal static GameObject FindForSnippet(string path) => ResolveScenePath(path);
        internal static string DescribeForSnippet(Object o) => Describe(o);
    }
}
