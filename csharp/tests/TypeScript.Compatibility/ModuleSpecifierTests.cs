using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ModuleSpecifierTests
{
    internal static int NodeModuleSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Node module specifier assertion {checks + 1}");
            checks++;
        }
        var source = Parser.ParseSourceFile(new("/project/main.ts"u8), new SourceText(""u8));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/node_modules/pkg/package.json"u8] = Wtf8.Encode("{\"exports\":{\"./public\":\"./lib/item.js\"}}")
        });
        var naming = new ModuleSpecifierPackages(fs, new(), "/project"u8, "/project"u8);
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/item.ts"u8, source, 0) == "pkg/public"u8);
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/private.ts"u8, source, 0) == ""u8);
        Check(naming.FromNodeModules("/project/node_modules/@types/scope__pkg/index.d.ts"u8, source, 0) == "@scope/pkg"u8);
        Check(naming.FromNodeModules("/project/node_modules/other/lib/item.ts"u8, source, 0, isRedirect: true) == ""u8);
        Check(naming.FromNodeModules("/project/node_modules/other/index.d.ts"u8, source, 0, isRedirect: true) == "other"u8);
        Check(naming.FromNodeModules("/project/node_modules/other/index.d.ts"u8, source, 0, globalTypingsCache: "/project/cache"u8) == ""u8);
        Check(
            naming.FromNodeModules(
                "/project/node_modules/pkg/lib/item.ts"u8,
                source,
                0,
                isRedirect: true,
                globalTypingsCache: "/project/cache"u8) == "pkg/public"u8);
        Utf8String deep = Utf8String.Concat(Utf8String.Copy("/project/node_modules/other/"u8), Utf8String.Concat(Enumerable.Repeat(Utf8String.Copy("sub/"u8), 20_000)), "item.ts"u8);
        Check(naming.FromNodeModules(deep, source, 0) == deep["/project/node_modules/".Length..^3]);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            naming.FromNodeModules(deep, source, 0, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled node module naming query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/item.ts"u8, source, 0) == "pkg/public"u8);
        return checks;
    }

    internal static int PackageSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Package specifier assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("outDir"u8, "\"lib\""u8);
        options.SetRaw("declarationDir"u8, "\"types\""u8);
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/package.json"u8] = Wtf8.Encode(
                "{\"imports\":{\"#item\":{\"types\":\"./types/item.d.ts\",\"default\":\"./lib/item.js\"},\"#/*\":\"./lib/*.js\"}}")
        });
        var naming = new ModuleSpecifierPackages(fs, options, "/project"u8, "/project/src"u8, [".vue"u8]);
        Check(naming.OutputFile("/project/src/item.ts"u8, false) == "/project/lib/item.js"u8);
        Check(naming.OutputFile("/project/src/item.ts"u8, true) == "/project/types/item.d.ts"u8);
        Check(naming.OutputFile("/project/src/item.vue"u8, true) == "/project/types/item.d.vue.ts"u8);
        Check(naming.OutputFile("/project/src/item.mts"u8, true) == "/project/types/item.d.mts"u8);
        Check(naming.Conditions(0).SequenceEqual([Utf8String.Copy("import"u8), Utf8String.Copy("types"u8)]));
        Check(naming.FromImports("/project/src/item.ts"u8, "/project/src"u8, 0, false) == "#item"u8);
        Check(naming.FromImports("/project/lib/other.ts"u8, "/project/src"u8, 0, false) == "#/other"u8);
        Check(naming.FromImports("/project/lib/other.ts"u8, "/project/src"u8, 0, true) == "#/other"u8);
        using var ordered = JsonDocument.Parse(
            "{\"types@>99\":\"./lib/item.js\",\"types@>=7.1.0-dev\":\"./types/item.d.ts\",\"default\":\"./lib/item.js\"}");
        Check(
            naming.FromMap(
                "/project/src/item.ts"u8,
                "/project"u8,
                "pkg"u8,
                ordered.RootElement,
                ["types"u8],
                PackageSpecifierMatch.Exact,
                true,
                false) == "pkg"u8);
        using var fallback = JsonDocument.Parse("[null,{\"unknown\":\"./none.js\"},\"./lib/item.js\"]");
        Check(
            naming.FromMap(
                "/project/lib/item.ts"u8,
                "/project"u8,
                "pkg"u8,
                fallback.RootElement,
                [],
                PackageSpecifierMatch.Exact,
                false,
                false) == "pkg"u8);
        using var exports = JsonDocument.Parse("{\"./first/*\":\"./lib/*.js\",\"./second/*\":\"./lib/*.js\"}");
        Check(
            naming.FromExports(
                "/project/lib/item.ts"u8,
                "/project"u8,
                "@scope/pkg"u8,
                exports.RootElement,
                ["types"u8]) == "@scope/pkg/first/item"u8);
        using var deep = JsonDocument.Parse((Utf8String.Concat(new Utf8String('[', 20_000), "\"./lib/item.js\""u8) + new Utf8String(']', 20_000)).Memory,
            new JsonDocumentOptions { MaxDepth = int.MaxValue });
        Check(
            naming.FromMap(
                "/project/lib/item.ts"u8,
                "/project"u8,
                "deep"u8,
                deep.RootElement,
                [],
                PackageSpecifierMatch.Exact,
                false,
                false) == "deep"u8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (Action query in new Action[]
        {
            () => naming.FromMap("x"u8, "/project"u8, "x"u8, deep.RootElement, [], PackageSpecifierMatch.Exact, false, false, cancellation.Token),
            () => naming.FromExports("x"u8, "/project"u8, "x"u8, exports.RootElement, [], cancellation.Token),
            () => naming.FromImports("x"u8, "/project"u8, 0, false, cancellation.Token)
        })
        {
            try
            {
                query();
                throw new InvalidOperationException("Canceled package naming query completed");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(naming.FromImports("/project/src/item.ts"u8, "/project/src"u8, 0, false) == "#item"u8);
        var nodeOptions = new CompilerOptions();
        nodeOptions.SetRaw("moduleResolution"u8, "\"node16\""u8);
        var nodeNaming = new ModuleSpecifierPackages(fs, nodeOptions, "/project"u8, "/project/src"u8);
        Check(nodeNaming.Conditions(0).SequenceEqual([Utf8String.Copy("require"u8), Utf8String.Copy("types"u8), Utf8String.Copy("node"u8)]));
        Check(nodeNaming.FromImports("/project/lib/other.ts"u8, "/project/src"u8, 0, false) == ""u8);
        nodeOptions.SetRaw("resolvePackageJsonImports"u8, "false"u8);
        Check(nodeNaming.FromImports("/project/lib/item.ts"u8, "/project/src"u8, 0, false) == ""u8);
        return checks;
    }

    internal static int Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Module specifier path assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/project/folder.ts"u8] = [] });
        ModuleSpecifierEnding[] minimal = [ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index, ModuleSpecifierEnding.JavaScript];
        ModuleSpecifierEnding[] typed = [ModuleSpecifierEnding.TypeScript, ModuleSpecifierEnding.JavaScript];
        Check(ModuleSpecifierPaths.ProcessEnding("./folder/index.ts"u8, minimal, options, fs, "/project"u8) == "./folder/index"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./other/index.ts"u8, minimal, options, fs, "/project"u8) == "./other"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.mts"u8, typed, options) == "./file.d.mts"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.cts"u8, typed, options) == "./file.d.cts"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.mts"u8, minimal, options) == "./file.mjs"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.css.ts"u8, minimal, options) == "./file.css"u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.mjs"u8, minimal, options) == "./file.mjs"u8);
        options.SetRaw("jsx"u8, "\"preserve\""u8);
        Check(ModuleSpecifierPaths.ProcessEnding("./file.tsx"u8, [ModuleSpecifierEnding.JavaScript], options) == "./file.jsx"u8);
        var source = Parser.ParseSourceFile(new("/project/main.ts"u8), new SourceText("import './x'; import './y.js';"u8));
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, 0)[0] == ModuleSpecifierEnding.Minimal);
        options.SetRaw("allowImportingTsExtensions"u8, "true"u8);
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, 0)[0] == ModuleSpecifierEnding.JavaScript);
        options.SetRaw("moduleResolution"u8, "\"nodenext\""u8);
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, ReferenceResolutionMode.Import)
            .SequenceEqual([ModuleSpecifierEnding.TypeScript, ModuleSpecifierEnding.JavaScript]));
        Check(ModuleSpecifierPaths.FromRootDirectories(["/project/src"u8, "/project/generated"u8], "/project/generated/sub/file.ts"u8,
            "/project/src/sub"u8, minimal, options, fs, "/project"u8) == "./file"u8);
        using var paths = JsonDocument.Parse("{\"@app/*\":[\"./src/*.ts\"],\"second/*\":[\"./src/*\"]}");
        Check(
            ModuleSpecifierPaths.FromPaths("src/item.ts"u8, CompilerOptions.ParsePaths(paths.RootElement)!, minimal, "/project"u8, options, fs, "/project"u8) == "@app/item"u8);
        Check(ModuleSpecifierPaths.RealNonJavaScriptFileName("foo.module.d.css.ts"u8) == "foo.module.css"u8);
        Check(ModuleSpecifierPaths.RealNonJavaScriptFileName("foo.d.ts"u8) == ""u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        foreach (Action query in new Action[]
        {
            () => ModuleSpecifierPaths.AllowedEndings(options, source, 0, cancellation: stop.Token),
            () => ModuleSpecifierPaths.ProcessEnding("a.ts"u8, minimal, options, cancellation: stop.Token),
            () => ModuleSpecifierPaths.FromRootDirectories([], "a.ts"u8, "/project"u8, minimal, options, fs, "/project"u8, stop.Token),
            () => ModuleSpecifierPaths.FromPaths("a.ts"u8, CompilerOptions.ParsePaths(paths.RootElement)!, minimal, "/project"u8, options, fs, "/project"u8, stop.Token)
        })
        {
            try
            {
                query();
                throw new InvalidOperationException("Canceled naming query completed");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(ModuleSpecifierPaths.ProcessEnding("./other/index.ts"u8, minimal, options, fs, "/project"u8) == "./other"u8);
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Write(json.RootElement, writer);
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void Write(JsonElement input, Utf8JsonWriter writer)
    {
        if (JsonStrings.GetString(input.GetProperty("operation"u8)) == "program"u8)
        {
            Parser.RunParse(ModuleSpecifierProgramTests.WriteAsync(input, writer));
            return;
        }
        Utf8String Text(Utf8String key, Utf8String fallback = default) => input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;
        int Number(Utf8String key) => input.TryGetProperty(key, out var value) ? value.GetInt32() : 0;
        Utf8String[] Strings(Utf8String key) => input.TryGetProperty(key, out var value)
            ? value.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
        var options = new CompilerOptions();
        if (input.TryGetProperty("options"u8, out var supplied))
            foreach (var item in supplied.EnumerateObject())
                options.Set(JsonStrings.GetName(item), item.Value);
        var files = input.GetProperty("files"u8).EnumerateObject().ToDictionary(
            p => JsonStrings.GetName(p),
            p => JsonStrings.GetString(p.Value).Span.ToArray());
        var fs = new MemoryFileSystem(files, input.GetProperty("sensitive"u8).GetBoolean());
        var source = Parser.ParseSourceFile(new(Text("fileName"u8)), new SourceText(Text("source"u8)));
        if (Text("operation"u8) == "print"u8)
        {
            bool neverAsciiEscape = input.TryGetProperty("neverAsciiEscape"u8, out var never) && never.GetBoolean();
            bool withoutSource = input.TryGetProperty("withoutSource"u8, out var without) && without.GetBoolean();
            writer.WriteStartArray();
            foreach (var node in source.DescendantsAndSelf())
            {
                SyntaxNode? printedNode = Text("printMode"u8) switch
                {
                    var matchedText when matchedText == "source"u8 => node is SourceFileNode ? node : null,
                    var matchedText2 when matchedText2 == "types"u8 => (node as TypeAliasDeclarationNode)?.Type,
                    _ => node is ComputedPropertyNameNode ? node : null
                };
                if (printedNode is null)
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue((int)printedNode.Kind);
                writer.WriteNumberValue(printedNode.Pos);
                writer.WriteNumberValue(printedNode.End);
                writer.WriteStringValue(
                    TypeScript.Compiler.Checking.Checker.PrintDiagnosticNode(
                        printedNode,
                        neverAsciiEscape,
                        sourceFile: withoutSource ? null : source).Span);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            return;
        }
        var endings = input.TryGetProperty("endings"u8, out var suppliedEndings)
            ? suppliedEndings.EnumerateArray().Select(v => (ModuleSpecifierEnding)v.GetInt32()).ToArray() : [];
        Utf8String target = Text("target"u8), directory = Text("directory"u8);
        if (Text("operation"u8) is var matchedText4 && (matchedText4 == "all-paths"u8 || matchedText4 == "local"u8 || matchedText4 == "select"u8 || matchedText4 == "generate"u8))
        {
            ModuleSpecifierGenerationTests.Write(input, writer, fs, source, options);
            return;
        }
        if (Text("operation"u8) == "node-modules"u8)
        {
            var naming = new ModuleSpecifierPackages(fs, options, directory, directory);
            writer.WriteStringValue(naming.FromNodeModules(target, source,
                (ReferenceResolutionMode)Number("defaultMode"u8), (ReferenceResolutionMode)Number("mode"u8), Text("preference"u8),
                input.TryGetProperty("packageNameOnly"u8, out var nameOnly) && nameOnly.GetBoolean(),
                input.TryGetProperty("redirect"u8, out var redirect) && redirect.GetBoolean(), Text("globalTypingsCache"u8)));
            return;
        }
        if (Text("operation"u8) is var matchedText5 && (matchedText5 == "package-map"u8 || matchedText5 == "package-exports"u8 || matchedText5 == "package-imports"u8 || matchedText5 == "package-conditions"u8 || matchedText5 == "output-paths"u8))
        {
            var naming = new ModuleSpecifierPackages(
                fs,
                options,
                directory,
                Text("commonDirectory"u8, directory),
                Strings("mapperExtensions"u8));
            JsonElement map = input.TryGetProperty("packageMap"u8, out var packageMap) ? packageMap : default;
            bool prefer = input.TryGetProperty("preferTypeScript"u8, out var preferred) && preferred.GetBoolean();
            Utf8String? named = Text("operation"u8) switch
            {
                var matchedText6 when matchedText6 == "package-map"u8 => naming.FromMap(target, Text("packageDirectory"u8), Text("packageName"u8), map, Strings("conditions"u8),
                    (PackageSpecifierMatch)Number("matchMode"u8),
                    input.TryGetProperty("imports"u8, out var imports) && imports.GetBoolean(),
                    prefer),
                var matchedText7 when matchedText7 == "package-exports"u8 => naming.FromExports(target, Text("packageDirectory"u8), Text("packageName"u8), map, Strings("conditions"u8)),
                var matchedText8 when matchedText8 == "package-imports"u8 => naming.FromImports(target, Text("sourceDirectory"u8), (ReferenceResolutionMode)Number("mode"u8), prefer),
                _ => null
            };
            if (named is not null)
                writer.WriteStringValue(named.Value.Span);
            else
            {
                writer.WriteStartArray();
                foreach (Utf8String item in Text("operation"u8) == "package-conditions"u8 ? naming.Conditions((ReferenceResolutionMode)Number("mode"u8))
                    : new[] { naming.OutputFile(target, false), naming.OutputFile(target, true) })
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
            }
            return;
        }
        if (Text("operation"u8) == "endings"u8)
        {
            writer.WriteStartArray();
            foreach (var ending in ModuleSpecifierPaths.AllowedEndings(options, source, (ReferenceResolutionMode)Number("defaultMode"u8),
                (ReferenceResolutionMode)Number("mode"u8), Text("preference"u8), Text("oldSpecifier"u8)))
                writer.WriteNumberValue((int)ending);
            writer.WriteEndArray();
            return;
        }
        Utf8String result;
        switch (Text("operation"u8))
        {
            case var entrypoint when entrypoint == "entrypoint-ending"u8:
                result = new ResolvedEntrypoint(default, default, target, (EntrypointEnding)Number("entrypointEnding"u8))
                    .Format(options, source, (ReferenceResolutionMode)Number("defaultMode"u8), Text("preference"u8), default, endings);
                break;
            case var matchedText9 when matchedText9 == "process"u8:
                result = ModuleSpecifierPaths.ProcessEnding(target, endings, options, fs, directory);
                break;
            case var matchedText10 when matchedText10 == "roots"u8:
                result = ModuleSpecifierPaths.FromRootDirectories(
                    Strings("roots"u8),
                    target,
                    Text("sourceDirectory"u8),
                    endings,
                    options,
                    fs,
                    directory);
                break;
            case var matchedText11 when matchedText11 == "paths"u8:
                using (var pathsStream = new MemoryStream())
                {
                    using (var pathsWriter = new Utf8JsonWriter(pathsStream))
                    {
                        pathsWriter.WriteStartObject();
                        foreach (var pair in input.GetProperty("paths"u8).EnumerateArray())
                        {
                            pathsWriter.WritePropertyName(JsonStrings.GetString(pair[0]));
                            pair[1].WriteTo(pathsWriter);
                        }
                        pathsWriter.WriteEndObject();
                    }
                    using var paths = JsonDocument.Parse(pathsStream.ToArray());
                    result = ModuleSpecifierPaths.FromPaths(
                        target,
                        CompilerOptions.ParsePaths(paths.RootElement)!,
                        endings,
                        Text("baseDirectory"u8),
                        options,
                        fs,
                        directory);
                }
                break;
            case var matchedText3 when matchedText3 == "non-js"u8:
                result = ModuleSpecifierPaths.RealNonJavaScriptFileName(target);
                break;
            default:
                throw new ArgumentException("Unknown module naming operation");
        }
        writer.WriteStringValue(result);
    }
}
