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
        var endings = input.TryGetProperty("endings", out var suppliedEndings)
            ? suppliedEndings.EnumerateArray().Select(v => (ModuleSpecifierEnding)v.GetInt32()).ToArray() : [];
        string target = Text("target"), directory = Text("directory");
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
