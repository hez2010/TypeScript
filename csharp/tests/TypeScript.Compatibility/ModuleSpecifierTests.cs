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
        var source = Parser.ParseSourceFile(new("/project/main.ts"), new SourceText(""));
        var fs = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/node_modules/pkg/package.json"] = Wtf8.Encode("{\"exports\":{\"./public\":\"./lib/item.js\"}}")
        });
        var naming = new ModuleSpecifierPackages(fs, new(), "/project", "/project");
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/item.ts", source, 0) == "pkg/public");
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/private.ts", source, 0) == "");
        Check(naming.FromNodeModules("/project/node_modules/@types/scope__pkg/index.d.ts", source, 0) == "@scope/pkg");
        Check(naming.FromNodeModules("/project/node_modules/other/lib/item.ts", source, 0, isRedirect: true) == "");
        Check(naming.FromNodeModules("/project/node_modules/other/index.d.ts", source, 0, isRedirect: true) == "other");
        Check(naming.FromNodeModules("/project/node_modules/other/index.d.ts", source, 0, globalTypingsCache: "/project/cache") == "");
        Check(
            naming.FromNodeModules(
                "/project/node_modules/pkg/lib/item.ts",
                source,
                0,
                isRedirect: true,
                globalTypingsCache: "/project/cache") == "pkg/public");
        string deep = "/project/node_modules/other/" + string.Concat(Enumerable.Repeat("sub/", 20_000)) + "item.ts";
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
        Check(naming.FromNodeModules("/project/node_modules/pkg/lib/item.ts", source, 0) == "pkg/public");
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
        options.SetRaw("outDir", "\"lib\"");
        options.SetRaw("declarationDir", "\"types\"");
        var fs = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/package.json"] = Wtf8.Encode(
                "{\"imports\":{\"#item\":{\"types\":\"./types/item.d.ts\",\"default\":\"./lib/item.js\"},\"#/*\":\"./lib/*.js\"}}")
        });
        var naming = new ModuleSpecifierPackages(fs, options, "/project", "/project/src", [".vue"]);
        Check(naming.OutputFile("/project/src/item.ts", false) == "/project/lib/item.js");
        Check(naming.OutputFile("/project/src/item.ts", true) == "/project/types/item.d.ts");
        Check(naming.OutputFile("/project/src/item.vue", true) == "/project/types/item.d.vue.ts");
        Check(naming.OutputFile("/project/src/item.mts", true) == "/project/types/item.d.mts");
        Check(naming.Conditions(0).SequenceEqual(["import", "types"]));
        Check(naming.FromImports("/project/src/item.ts", "/project/src", 0, false) == "#item");
        Check(naming.FromImports("/project/lib/other.ts", "/project/src", 0, false) == "#/other");
        Check(naming.FromImports("/project/lib/other.ts", "/project/src", 0, true) == "#/other");
        using var ordered = JsonDocument.Parse(
            "{\"types@>99\":\"./lib/item.js\",\"types@>=7.1.0-dev\":\"./types/item.d.ts\",\"default\":\"./lib/item.js\"}");
        Check(
            naming.FromMap(
                "/project/src/item.ts",
                "/project",
                "pkg",
                ordered.RootElement,
                ["types"],
                PackageSpecifierMatch.Exact,
                true,
                false) == "pkg");
        using var fallback = JsonDocument.Parse("[null,{\"unknown\":\"./none.js\"},\"./lib/item.js\"]");
        Check(
            naming.FromMap(
                "/project/lib/item.ts",
                "/project",
                "pkg",
                fallback.RootElement,
                [],
                PackageSpecifierMatch.Exact,
                false,
                false) == "pkg");
        using var exports = JsonDocument.Parse("{\"./first/*\":\"./lib/*.js\",\"./second/*\":\"./lib/*.js\"}");
        Check(
            naming.FromExports(
                "/project/lib/item.ts",
                "/project",
                "@scope/pkg",
                exports.RootElement,
                ["types"]) == "@scope/pkg/first/item");
        using var deep = JsonDocument.Parse(new string('[', 20_000) + "\"./lib/item.js\"" + new string(']', 20_000),
            new JsonDocumentOptions { MaxDepth = int.MaxValue });
        Check(
            naming.FromMap(
                "/project/lib/item.ts",
                "/project",
                "deep",
                deep.RootElement,
                [],
                PackageSpecifierMatch.Exact,
                false,
                false) == "deep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (Action query in new Action[]
        {
            () => naming.FromMap("x", "/project", "x", deep.RootElement, [], PackageSpecifierMatch.Exact, false, false, cancellation.Token),
            () => naming.FromExports("x", "/project", "x", exports.RootElement, [], cancellation.Token),
            () => naming.FromImports("x", "/project", 0, false, cancellation.Token)
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
        Check(naming.FromImports("/project/src/item.ts", "/project/src", 0, false) == "#item");
        var nodeOptions = new CompilerOptions();
        nodeOptions.SetRaw("moduleResolution", "\"node16\"");
        var nodeNaming = new ModuleSpecifierPackages(fs, nodeOptions, "/project", "/project/src");
        Check(nodeNaming.Conditions(0).SequenceEqual(["require", "types", "node"]));
        Check(nodeNaming.FromImports("/project/lib/other.ts", "/project/src", 0, false) == "");
        nodeOptions.SetRaw("resolvePackageJsonImports", "false");
        Check(nodeNaming.FromImports("/project/lib/item.ts", "/project/src", 0, false) == "");
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
        var fs = new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/folder.ts"] = [] });
        ModuleSpecifierEnding[] minimal = [ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index, ModuleSpecifierEnding.JavaScript];
        ModuleSpecifierEnding[] typed = [ModuleSpecifierEnding.TypeScript, ModuleSpecifierEnding.JavaScript];
        Check(ModuleSpecifierPaths.ProcessEnding("./folder/index.ts", minimal, options, fs, "/project") == "./folder/index");
        Check(ModuleSpecifierPaths.ProcessEnding("./other/index.ts", minimal, options, fs, "/project") == "./other");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.mts", typed, options) == "./file.d.mts");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.cts", typed, options) == "./file.d.cts");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.mts", minimal, options) == "./file.mjs");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.d.css.ts", minimal, options) == "./file.css");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.mjs", minimal, options) == "./file.mjs");
        options.SetRaw("jsx", "\"preserve\"");
        Check(ModuleSpecifierPaths.ProcessEnding("./file.tsx", [ModuleSpecifierEnding.JavaScript], options) == "./file.jsx");
        var source = Parser.ParseSourceFile(new("/project/main.ts"), new SourceText("import './x'; import './y.js';"));
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, 0)[0] == ModuleSpecifierEnding.Minimal);
        options.SetRaw("allowImportingTsExtensions", "true");
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, 0)[0] == ModuleSpecifierEnding.JavaScript);
        options.SetRaw("moduleResolution", "\"nodenext\"");
        Check(ModuleSpecifierPaths.AllowedEndings(options, source, ReferenceResolutionMode.Import)
            .SequenceEqual([ModuleSpecifierEnding.TypeScript, ModuleSpecifierEnding.JavaScript]));
        Check(ModuleSpecifierPaths.FromRootDirectories(["/project/src", "/project/generated"], "/project/generated/sub/file.ts",
            "/project/src/sub", minimal, options, fs, "/project") == "./file");
        using var paths = JsonDocument.Parse("{\"@app/*\":[\"./src/*.ts\"],\"second/*\":[\"./src/*\"]}");
        Check(
            ModuleSpecifierPaths.FromPaths("src/item.ts", paths.RootElement, minimal, "/project", options, fs, "/project") == "@app/item");
        Check(ModuleSpecifierPaths.RealNonJavaScriptFileName("foo.module.d.css.ts") == "foo.module.css");
        Check(ModuleSpecifierPaths.RealNonJavaScriptFileName("foo.d.ts") == "");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        foreach (Action query in new Action[]
        {
            () => ModuleSpecifierPaths.AllowedEndings(options, source, 0, cancellation: stop.Token),
            () => ModuleSpecifierPaths.ProcessEnding("a.ts", minimal, options, cancellation: stop.Token),
            () => ModuleSpecifierPaths.FromRootDirectories([], "a.ts", "/project", minimal, options, fs, "/project", stop.Token),
            () => ModuleSpecifierPaths.FromPaths("a.ts", paths.RootElement, minimal, "/project", options, fs, "/project", stop.Token)
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
        Check(ModuleSpecifierPaths.ProcessEnding("./other/index.ts", minimal, options, fs, "/project") == "./other");
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
        if (input.GetProperty("operation").GetString() == "program")
        {
            Parser.RunParse(ModuleSpecifierProgramTests.WriteAsync(input, writer));
            return;
        }
        string Text(string key, string fallback = "") => input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;
        int Number(string key) => input.TryGetProperty(key, out var value) ? value.GetInt32() : 0;
        string[] Strings(string key) => input.TryGetProperty(key, out var value)
            ? value.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
        var options = new CompilerOptions();
        if (input.TryGetProperty("options", out var supplied))
            foreach (var item in supplied.EnumerateObject())
                options.Set(item.Name, item.Value);
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(
            p => p.Name,
            p => Wtf8.Encode(JsonStrings.GetString(p.Value)));
        var fs = new MemoryFileSystem(files, input.GetProperty("sensitive").GetBoolean());
        var source = Parser.ParseSourceFile(new(Text("fileName")), new SourceText(Text("source")));
        if (Text("operation") == "print")
        {
            bool neverAsciiEscape = input.TryGetProperty("neverAsciiEscape", out var never) && never.GetBoolean();
            bool withoutSource = input.TryGetProperty("withoutSource", out var without) && without.GetBoolean();
            writer.WriteStartArray();
            foreach (var node in source.DescendantsAndSelf())
            {
                SyntaxNode? printedNode = Text("printMode") switch
                {
                    "source" => node is SourceFileNode ? node : null,
                    "types" => (node as TypeAliasDeclarationNode)?.Type,
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
                        sourceFile: withoutSource ? null : source));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            return;
        }
        var endings = input.TryGetProperty("endings", out var suppliedEndings)
            ? suppliedEndings.EnumerateArray().Select(v => (ModuleSpecifierEnding)v.GetInt32()).ToArray() : [];
        string target = Text("target"), directory = Text("directory");
        if (Text("operation") is "all-paths" or "local" or "select" or "generate")
        {
            ModuleSpecifierGenerationTests.Write(input, writer, fs, source, options);
            return;
        }
        if (Text("operation") == "node-modules")
        {
            var naming = new ModuleSpecifierPackages(fs, options, directory, directory);
            writer.WriteStringValue(naming.FromNodeModules(target, source,
                (ReferenceResolutionMode)Number("defaultMode"), (ReferenceResolutionMode)Number("mode"), Text("preference"),
                input.TryGetProperty("packageNameOnly", out var nameOnly) && nameOnly.GetBoolean(),
                input.TryGetProperty("redirect", out var redirect) && redirect.GetBoolean(), Text("globalTypingsCache")));
            return;
        }
        if (Text("operation") is "package-map" or "package-exports" or "package-imports" or "package-conditions" or "output-paths")
        {
            var naming = new ModuleSpecifierPackages(
                fs,
                options,
                directory,
                Text("commonDirectory", directory),
                Strings("mapperExtensions"));
            JsonElement map = input.TryGetProperty("packageMap", out var packageMap) ? packageMap : default;
            bool prefer = input.TryGetProperty("preferTypeScript", out var preferred) && preferred.GetBoolean();
            string? named = Text("operation") switch
            {
                "package-map" => naming.FromMap(target, Text("packageDirectory"), Text("packageName"), map, Strings("conditions"),
                    (PackageSpecifierMatch)Number("matchMode"),
                    input.TryGetProperty("imports", out var imports) && imports.GetBoolean(),
                    prefer),
                "package-exports" => naming.FromExports(target, Text("packageDirectory"), Text("packageName"), map, Strings("conditions")),
                "package-imports" => naming.FromImports(target, Text("sourceDirectory"), (ReferenceResolutionMode)Number("mode"), prefer),
                _ => null
            };
            if (named is not null)
                writer.WriteStringValue(named);
            else
            {
                writer.WriteStartArray();
                foreach (string item in Text("operation") == "package-conditions" ? naming.Conditions((ReferenceResolutionMode)Number("mode"))
                    : new[] { naming.OutputFile(target, false), naming.OutputFile(target, true) })
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
            }
            return;
        }
        if (Text("operation") == "endings")
        {
            writer.WriteStartArray();
            foreach (var ending in ModuleSpecifierPaths.AllowedEndings(options, source, (ReferenceResolutionMode)Number("defaultMode"),
                (ReferenceResolutionMode)Number("mode"), Text("preference"), Text("oldSpecifier")))
                writer.WriteNumberValue((int)ending);
            writer.WriteEndArray();
            return;
        }
        string result;
        switch (Text("operation"))
        {
            case "process":
                result = ModuleSpecifierPaths.ProcessEnding(target, endings, options, fs, directory);
                break;
            case "roots":
                result = ModuleSpecifierPaths.FromRootDirectories(
                    Strings("roots"),
                    target,
                    Text("sourceDirectory"),
                    endings,
                    options,
                    fs,
                    directory);
                break;
            case "paths":
                using (var pathsStream = new MemoryStream())
                {
                    using (var pathsWriter = new Utf8JsonWriter(pathsStream))
                    {
                        pathsWriter.WriteStartObject();
                        foreach (var pair in input.GetProperty("paths").EnumerateArray())
                        {
                            pathsWriter.WritePropertyName(JsonStrings.GetString(pair[0]));
                            pair[1].WriteTo(pathsWriter);
                        }
                        pathsWriter.WriteEndObject();
                    }
                    using var paths = JsonDocument.Parse(pathsStream.ToArray());
                    result = ModuleSpecifierPaths.FromPaths(
                        target,
                        paths.RootElement,
                        endings,
                        Text("baseDirectory"),
                        options,
                        fs,
                        directory);
                }
                break;
            case "non-js":
                result = ModuleSpecifierPaths.RealNonJavaScriptFileName(target);
                break;
            default:
                throw new ArgumentException("Unknown module naming operation");
        }
        writer.WriteStringValue(result);
    }
}
