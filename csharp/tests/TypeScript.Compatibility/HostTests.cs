using TypeScript.Compiler.Text;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compatibility;

internal static class HostTests
{
    public static void RunLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(document.RootElement, writer);
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void Process(JsonElement input, Utf8JsonWriter writer)
    {
        Utf8String Text(Utf8String name, Utf8String fallback = default) => input.TryGetProperty(name, out var value) ? JsonStrings.GetString(value)! : fallback;
        bool Flag(Utf8String name) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        Utf8String mode = Text("mode"u8), path = Text("path"u8), other = Text("other"u8);
        bool sensitive = Flag("sensitive"u8);
        if (mode == "glob"u8)
        {
            writer.WriteBooleanValue(
                ConfigParser.GlobMatches(CompilerPath.Resolve(Text("directory"u8), path), other, sensitive, Flag("exclude"u8)));
            return;
        }
        var files = new Dictionary<Utf8String, byte[]>();
        if (input.TryGetProperty("files"u8, out var filesInput))
            foreach (var item in filesInput.EnumerateObject())
                files[JsonStrings.GetName(item)] = (JsonStrings.GetString(item.Value)!).Span.ToArray();
        var symlinks = new Dictionary<Utf8String, Utf8String>();
        if (input.TryGetProperty("symlinks"u8, out var linksInput))
            foreach (var item in linksInput.EnumerateObject())
                symlinks[JsonStrings.GetName(item)] = TypeScript.Compiler.Configuration.JsonStrings.GetString(item.Value)!;
        var fs = new MemoryFileSystem(files, sensitive, Text("directory"u8, "/"u8), symlinks);
        if (mode == "readDirectory"u8)
        {
            Utf8String[]? Strings(Utf8String key) =>
                input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(v => JsonStrings.GetString(v)!).ToArray()
                    : null;
            Utf8String[] result = FileMatcher.ReadDirectory(
                fs,
                path,
                Text("directory"u8, "/"u8),
                Strings("extensions"u8),
                Strings("excludes"u8),
                Strings("includes"u8),
                input.TryGetProperty("depth"u8, out var depth) ? depth.GetInt32() : -1);
            writer.WriteStartArray();
            foreach (Utf8String file in result)
                writer.WriteStringValue(file);
            writer.WriteEndArray();
            return;
        }
        writer.WriteStartObject();
        if (mode == "path"u8)
        {
            writer.WriteString("normalize"u8, CompilerPath.Normalize(path));
            writer.WriteNumber("root"u8, CompilerPath.RootLength(path));
            writer.WriteBoolean("absolute"u8, CompilerPath.IsAbsolute(path));
            writer.WriteBoolean("url"u8, CompilerPath.IsUrl(path));
            writer.WriteString("directory"u8, CompilerPath.DirectoryName(path));
            writer.WriteString("base"u8, CompilerPath.BaseName(path));
            writer.WriteString("extension"u8, CompilerPath.Extension((Utf8String)path).Span);
            writer.WriteBoolean("declaration"u8, CompilerPath.IsDeclarationFile(path));
            writer.WriteString("combine"u8, CompilerPath.Combine(path, other));
            writer.WriteString("resolve"u8, CompilerPath.Resolve(path, other));
            writer.WriteBoolean("contains"u8, CompilerPath.Contains(path, other, sensitive));
            writer.WriteString("relative"u8, CompilerPath.Relative(path, other, sensitive));
        }
        else
        {
            CompilerOptions options;
            Utf8String[] names;
            Diagnostic[] diagnostics;
            if (mode == "cli"u8)
            {
                var result = new CommandLineParser(
                    fs,
                    Text(
                        "directory"u8,
                        "/"u8)).Parse(input.GetProperty("arguments"u8).EnumerateArray().Select(a => JsonStrings.GetString(a)!).ToArray(), Flag("build"u8));
                options = result.Options;
                names = result.FileNames;
                diagnostics = result.Diagnostics;
            }
            else
            {
                var existing = new CompilerOptions();
                if (input.TryGetProperty("existing"u8, out var existingInput))
                    foreach (var property in existingInput.EnumerateObject())
                        existing.Set(JsonStrings.GetName(property), property.Value);
                var result = new ConfigParser(fs, Text("directory"u8, "/"u8)).Parse(path, existing);
                if (mode == "configUnits"u8)
                {
                    void Units(Utf8String value)
                    {
                        writer.WriteStartArray();
                        // This probe reports JavaScript code units to its independent Node oracle.
                        for (int offset = 0; offset < value.Length;)
                        {
                            int point = Wtf8.Decode(value.Span[offset..], out int width);
                            offset += width;
                            if (point <= 0xFFFF)
                                writer.WriteNumberValue(point);
                            else
                            {
                                writer.WriteNumberValue(0xD800 + ((point - 0x10000) >> 10));
                                writer.WriteNumberValue(0xDC00 + ((point - 0x10000) & 0x3FF));
                            }
                        }
                        writer.WriteEndArray();
                    }
                    writer.WritePropertyName("files"u8);
                    writer.WriteStartArray();
                    foreach (Utf8String file in result.FileNames)
                        Units(file);
                    writer.WriteEndArray();
                    writer.WritePropertyName("outDir"u8);
                    Units(result.Options.String("outDir"u8) ?? ""u8);
                    writer.WritePropertyName("typeRoots"u8);
                    writer.WriteStartArray();
                    foreach (Utf8String item in result.Options.Strings("typeRoots"u8) ?? [])
                        Units(item);
                    writer.WriteEndArray();
                    writer.WritePropertyName("paths"u8);
                    writer.WriteStartArray();
                    if (result.Options.Get("paths"u8) is { ValueKind: JsonValueKind.Object } paths)
                        foreach (var property in paths.EnumerateObject())
                        {
                            writer.WriteStartArray();
                            Units(JsonStrings.GetName(property));
                            writer.WriteStartArray();
                            foreach (var item in property.Value.EnumerateArray())
                                Units(JsonStrings.GetString(item));
                            writer.WriteEndArray();
                            writer.WriteEndArray();
                        }
                    writer.WriteEndArray();
                    writer.WritePropertyName("diagnostics"u8);
                    writer.WriteStartArray();
                    foreach (var diagnostic in result.Diagnostics)
                        writer.WriteNumberValue((int)diagnostic.Code);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    return;
                }
                options = result.Options;
                names = result.FileNames;
                diagnostics = result.Diagnostics;
                writer.WritePropertyName("references"u8);
                writer.WriteStartArray();
                foreach (var reference in result.References)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(reference.Path);
                    writer.WriteBooleanValue(reference.Circular);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("compileOnSave"u8, result.CompileOnSave);
            }
            writer.WritePropertyName("options"u8);
            writer.WriteStartObject();
            foreach (var pair in options.Values)
            {
                if (mode != "cli"u8
                    && (pair.Value.ValueKind == JsonValueKind.Null
                        || pair.Value.ValueKind == JsonValueKind.String && JsonStrings.GetString(pair.Value) == ""u8))
                    continue;
                writer.WritePropertyName(pair.Key);
                var definition = OptionDefinitions.Find(pair.Key) ?? OptionDefinitions.Find(
                    pair.Key,
                    OptionGroup.Build) ?? OptionDefinitions.Find(pair.Key, OptionGroup.Watch);
                if (definition is not null && definition.Values.Length != 0 && pair.Value.ValueKind == JsonValueKind.String)
                    writer.WriteStringValue(Canonical(definition, JsonStrings.GetString(pair.Value)!));
                else if (definition is not null && definition.Values.Length != 0 && pair.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var element in pair.Value.EnumerateArray())
                        writer.WriteStringValue(Canonical(definition, JsonStrings.GetString(element)!));
                    writer.WriteEndArray();
                }
                else
                    pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WritePropertyName("files"u8);
            writer.WriteStartArray();
            foreach (Utf8String name in names)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WritePropertyName("diagnostics"u8);
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics)
                writer.WriteNumberValue((int)diagnostic.Code);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static Utf8String Canonical(OptionDefinition definition, Utf8String name)
    {
        int index = Array.FindIndex(definition.Values, v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? name : definition.Values[Array.IndexOf(definition.ValueIdentities, definition.ValueIdentities[index])];
    }

    public static void Run(Utf8String repository)
    {
        int assertions = 0;
        void Check(bool valid, Utf8String message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message.ToString());
        }
        var changingOptions = new CompilerOptions();
        Check(changingOptions.EmitTargetYear == 2025, "Unset target uses the default year"u8);
        Check(changingOptions.EmitModuleKind == 7, "Unset module follows the default target year"u8);
        changingOptions.SetRaw("target"u8, "\"es2015\""u8);
        Check(changingOptions.EmitTargetYear == 2015 && changingOptions.String("target"u8) == "es2015"u8,
            "Setting a target invalidates its previously computed default"u8);
        Check(changingOptions.EmitModuleKind == 5, "Changing the target updates the inferred module kind"u8);
        changingOptions.SetRaw("module"u8, "\"commonjs\""u8);
        Check(changingOptions.EmitModuleKind == 1, "An explicit module kind overrides the target default"u8);
        changingOptions.SetRaw("target"u8, "1"u8);
        Check(changingOptions.EmitTargetYear == 2009 && changingOptions.String("target"u8) is null,
            "Replacing a string option with a number updates both accessors"u8);
        Check(changingOptions.EmitModuleKind == 1, "Target changes preserve an explicit module kind"u8);
        var overrideOptions = new CompilerOptions();
        overrideOptions.SetRaw("target"u8, "\"esnext\""u8);
        overrideOptions.SetRaw("module"u8, "\"node20\""u8);
        overrideOptions.SetRaw("strict"u8, "false"u8);
        changingOptions.Merge(overrideOptions);
        Check(changingOptions.EmitTargetYear == int.MaxValue && changingOptions.String("target"u8) == "esnext"u8
            && !changingOptions.StrictOption("noImplicitAny"u8), "Merging options updates strings and target years"u8);
        Check(changingOptions.EmitModuleKind == 102, "Merging options invalidates a cached module kind"u8);
        changingOptions.SetRaw("module"u8, "99"u8);
        Check(changingOptions.EmitModuleKind == 99, "Numeric module kinds replace string module kinds"u8);
        changingOptions.SetRaw("module"u8, "null"u8);
        Check(changingOptions.EmitModuleKind == 99, "Null module values use the target-derived default"u8);
        changingOptions.SetRaw("target"u8, "null"u8);
        Check(changingOptions.EmitTargetYear == 2025 && changingOptions.String("target"u8) is null,
            "Null option values clear string access"u8);
        int optionReadFailures = 0;
        Parallel.For(0, 128, _ =>
        {
            if (changingOptions.EmitTargetYear != 2025 || changingOptions.EmitModuleKind != 7
                || changingOptions.StrictOption("noImplicitAny"u8))
                Interlocked.Increment(ref optionReadFailures);
        });
        Check(optionReadFailures == 0, "Parsed options permit concurrent reads and target initialization"u8);
        var typedOptions = new CompilerOptions();
        typedOptions.SetRaw("strict"u8, "false"u8);
        typedOptions.SetRaw("noImplicitAny"u8, "true"u8);
        typedOptions.SetRaw("allowUnreachableCode"u8, "false"u8);
        typedOptions.SetRaw("verbatimModuleSyntax"u8, "true"u8);
        typedOptions.SetRaw("noEmit"u8, "true"u8);
        Check(typedOptions.StrictNoImplicitAny && typedOptions.AllowUnreachableCode == false
            && typedOptions.VerbatimModuleSyntax == true && typedOptions.NoEmit == true,
            "Typed checker options preserve explicit true and false values"u8);
        var typedOverride = new CompilerOptions();
        typedOverride.SetRaw("noImplicitAny"u8, "null"u8);
        typedOverride.SetRaw("verbatimModuleSyntax"u8, "false"u8);
        typedOverride.SetRaw("noEmit"u8, "null"u8);
        typedOptions.Merge(typedOverride);
        Check(!typedOptions.StrictNoImplicitAny && typedOptions.AllowUnreachableCode == false
            && typedOptions.VerbatimModuleSyntax == false && typedOptions.NoEmit is null,
            "Merging typed options restores strict fallback and nullable values"u8);
        typedOptions.SetRaw("allowUnreachableCode"u8, "null"u8);
        Check(typedOptions.AllowUnreachableCode is null, "Null clears the typed reachability option"u8);
        typedOptions.SetRaw("jsx"u8, "\"react-jsx\""u8);
        typedOptions.SetRaw("moduleResolution"u8, "\"nodenext\""u8);
        typedOptions.SetRaw("moduleDetection"u8, "\"force\""u8);
        Check(typedOptions.Jsx == JsxEmit.ReactJSX && typedOptions.ModuleResolution == ModuleResolutionKind.NodeNext
            && typedOptions.ModuleDetection == ModuleDetectionKind.Force,
            "Typed enum options use the same wire values as the Go compiler"u8);
        typedOptions.SetRaw("lib"u8, "[\"es5\",\"dom\",1]"u8);
        typedOptions.SetRaw("paths"u8, "{\"@/*\":[\"src/*\"]}"u8);
        Check(typedOptions.Lib is [_, var matchedText] && typedOptions.Lib[0] == "es5"u8 && matchedText == "dom"u8 && typedOptions.Paths is [{ Key: var matchedText2, Value: var matchedText3 }] && matchedText2 == "@/*"u8 && matchedText3.Length == 1 && matchedText3[0] == "src/*"u8,
            "Typed list and path options retain their contents and order"u8);
        typedOptions.SetRaw("paths"u8, "null"u8);
        Check(typedOptions.Paths is null, "Null clears a typed path map"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/base/config.json"u8] = Encoding.UTF8.GetBytes(
                "{\"compilerOptions\":{\"outDir\":\"${configDir}/dist\",\"typeRoots\":[\"types\"],\"lib\":[\"ES6\",\"invalid\",1],\"paths\":{\"@/*\":[\"${configDir}/src/*\"]}},\"include\":[\"${configDir}/src\"]}"),
            ["/app/tsconfig.json"u8] = Encoding.UTF8.GetBytes(
                "{\"extends\":\"../base/config.json\",\"compilerOptions\":{\"allowJs\":true},\"references\":[{\"path\":\"../lib\",\"circular\":true}]}"),
            ["/app/src/a.ts"u8] = [],
            ["/app/src/a.d.ts"u8] = [],
            ["/app/src/a.js"u8] = [],
            ["/app/src/b.js"u8] = [],
            ["/app/src/.hidden.ts"u8] = [],
            ["/app/src/.hidden/a.ts"u8] = [],
            ["/app/src/node_modules/pkg/a.ts"u8] = [],
            ["/app/src/b.min.js"u8] = [],
            ["/app/src/data.json"u8] = [],
        };
        var fs = new MemoryFileSystem(files);
        var parsed = new ConfigParser(fs, "/app"u8).Parse("tsconfig.json"u8);
        Check(
            parsed.FileNames.SequenceEqual([Utf8String.Copy("/app/src/a.ts"u8), Utf8String.Copy("/app/src/b.js"u8)]),
            "Inherited template glob with extension priority, hidden/minified/package exclusion"u8);
        Check(parsed.Options.String("outDir"u8) == "/app/dist"u8, "Template resolves at final config"u8);
        Check(
            parsed.Options.Get("typeRoots"u8)?.EnumerateArray().Single().GetString() == "/base/types",
            "List paths resolve at defining config"u8);
        Check(
            parsed.Options.Get("paths"u8)?.GetProperty("@/*")[0].GetString() == "/app/src/*"
                && parsed.Options.String("pathsBasePath"u8) == "/base"u8,
            "Path mapping ownership and template"u8);
        Check(parsed.References is [{ Path: var matchedText4, Circular: true }] && matchedText4 == "/lib"u8, "Project references"u8);
        Check(
            parsed.Diagnostics.Select(d => d.Code).SequenceEqual(
                [DiagnosticCode.ArgumentFor0OptionMustBeColon1, DiagnosticCode.CompilerOption0RequiresAValueOfType1])
                && parsed.Diagnostics.All(d => d.Length > 0 && d.FileName == "/base/config.json"u8),
            "List validation with source spans"u8);
        var cli = new CommandLineParser(
            fs,
            "/app"u8).Parse(
                ["--composite"u8, "true"u8, "--plugins"u8, "false"u8, "--checkers"u8, "-1"u8, "--lib"u8, "es6,invalid"u8, "--typeRoots"u8, "types"u8, "x.ts"u8]);
        Check(cli.FileNames.SequenceEqual([Utf8String.Copy("x.ts"u8)]), "Invalid options consume their arguments"u8);
        Check(
            cli.Diagnostics.Select(d => d.Code).SequenceEqual(
                [
                        DiagnosticCode.Option0CanOnlyBeSpecifiedInTsconfigJsonFileOrSetToFalseOrNullOnCommandLine,
                        DiagnosticCode.Option0CanOnlyBeSpecifiedInTsconfigJsonFileOrSetToNullOnCommandLine,
                        DiagnosticCode.Option0RequiresValueToBeGreaterThan1,
                        DiagnosticCode.ArgumentFor0OptionMustBeColon1
                    ]),
            "Config-only, numeric bound and list diagnostics"u8);
        Check(cli.Options.Get("typeRoots"u8)?.EnumerateArray().Single().GetString() == "/app/types", "Command line list file paths"u8);
        Check(
            !ConfigParser.GlobMatches("/a/??.ts"u8, "/a/😀.ts"u8, true) && ConfigParser.GlobMatches("/a/?.ts"u8, "/a/😀.ts"u8, true),
            "Glob Unicode scalar wildcard semantics"u8);
        Check(ConfigParser.GlobMatches("/a/**/*.min.js"u8, "/a/b.min.js"u8, true), "Explicit minified glob"u8);
        Check(ConfigParser.GlobMatches("/a/**"u8, "/a/.hidden/x"u8, true, true), "Exclude recursion includes hidden paths"u8);
        Check(!ConfigParser.GlobMatches("/a/**"u8, "/a/b.ts"u8, true), "Include trailing recursion invalid"u8);
        fs.WriteFile("/app/empty.json"u8, Encoding.UTF8.GetBytes("// empty"));
        Check(new ConfigParser(fs, "/app"u8).Parse("empty.json"u8).Diagnostics.Length == 0, "Comment-only config"u8);
        fs.WriteFile("/app/recover.json"u8, Encoding.UTF8.GetBytes("{compilerOptions:{'strict':true}, files:['a.ts']}"));
        var recovered = new ConfigParser(fs, "/app"u8).Parse("recover.json"u8);
        Check(
            recovered.Options.Boolean("strict"u8) == true && recovered.Diagnostics.All(d => d.Length > 0),
            "JSON recovery retains options and spans"u8);
        fs.CreateDirectory("/empty/nested"u8);
        fs.WriteFile("/empty/file.ts"u8, [1]);
        fs.Remove("/empty/file.ts"u8);
        Check(
            fs.DirectoryExists("/empty"u8) && fs.GetAccessibleEntries("/empty"u8).Directories.SequenceEqual([Utf8String.Copy("nested"u8)]),
            "Empty directories survive file deletion"u8);
        fs.CreateSymbolicLink("/link"u8, "/app"u8);
        Check(
            fs.DirectoryExists("/link/src"u8) && fs.RealPath("/link/src/a.ts"u8) == "/app/src/a.ts"u8,
            "Intermediate directory symlink resolution"u8);
        fs.CreateSymbolicLink("/app/src/loop"u8, "/app/src"u8);
        Check(fs.ReadFile("/app/src/loop/loop/a.ts"u8) is not null, "Repeated directory links can consume the remaining path"u8);
        Check(
            FileMatcher.ReadDirectory(
                fs,
                "/link/src"u8,
                "/"u8,
                [".ts"u8],
                includes: ["**/*"u8]).SequenceEqual([Utf8String.Copy("/link/src/a.d.ts"u8), Utf8String.Copy("/link/src/a.ts"u8)]),
            "Symlink traversal terminates at visited real directory"u8);
        fs.WriteFile("/link/new.ts"u8, [4]);
        Check(fs.ReadFile("/app/new.ts"u8)!.SequenceEqual(new byte[] { 4 }), "Symlink writes target file"u8);
        fs.Remove("/link"u8);
        Check(!fs.DirectoryExists("/link"u8) && fs.FileExists("/app/new.ts"u8), "Removing symlink preserves target"u8);
        fs.CreateSymbolicLink("/cycle-a"u8, "/cycle-b"u8);
        fs.CreateSymbolicLink("/cycle-b"u8, "/cycle-a"u8);
        Check(!fs.FileExists("/cycle-a/file.ts"u8) && fs.ReadFile("/cycle-a/file.ts"u8) is null, "Symlink cycles are reported as inaccessible"u8);
        fs.CreateSymbolicLink("/app/recur"u8, "/app"u8);
        fs.WriteFile("/app/alias-cycle.json"u8, Encoding.UTF8.GetBytes("{\"extends\":\"./recur/alias-cycle.json\"}"));
        Check(
            new ConfigParser(
                fs,
                "/app"u8).Parse("alias-cycle.json"u8).Diagnostics.Any(
                    d => d.Code == DiagnosticCode.CircularityDetectedWhileResolvingConfigurationColon0),
            "Config inheritance detects cycles through directory aliases"u8);
        fs.WriteFile(
            "/app/template.json"u8,
            Encoding.UTF8.GetBytes("{\"compilerOptions\":{\"outDir\":\"${configDir}dist\",\"rootDir\":\"${configDir}\"}}"));
        var template = new ConfigParser(fs, "/app"u8).Parse("template.json"u8);
        Check(
            template.Options.String("outDir"u8) == "/app/dist"u8 && template.Options.String("rootDir"u8) == "/app"u8,
            "Config directory template supplies its directory separator"u8);
        fs.WriteFile(
            "/app/surrogate.json"u8,
            Encoding.UTF8.GetBytes(
                "{\"files\":[\"raw-\\uD800.ts\",\"raw-\\uDFFF.ts\",\"face-\\uD83D\\uDE00.ts\"],\"compilerOptions\":{\"outDir\":\"dir-\\uD800\",\"typeRoots\":[\"types-\\uDFFF\"],\"paths\":{\"module-\\uD800/*\":[\"${configDir}/src-\\uDFFF/*\"]}}}"));
        var surrogate = new ConfigParser(fs, "/app"u8).Parse("surrogate.json"u8);
        Check(
            surrogate.Diagnostics.Length == 0
                && surrogate.FileNames.SequenceEqual([Utf8String.Copy(Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x72, 0x61, 0x77, 0x2D, 0xED, 0xA0, 0x80, 0x2E, 0x74, 0x73])), Utf8String.Copy(Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x72, 0x61, 0x77, 0x2D, 0xED, 0xBF, 0xBF, 0x2E, 0x74, 0x73])), Utf8String.Copy("/app/face-😀.ts"u8)]),
            "Config paths preserve paired and unpaired UTF-16 code units"u8);
        Check(
            surrogate.Options.String("outDir"u8) == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x64, 0x69, 0x72, 0x2D, 0xED, 0xA0, 0x80])
                && surrogate.Options.Strings("typeRoots"u8)!.Single() == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x74, 0x79, 0x70, 0x65, 0x73, 0x2D, 0xED, 0xBF, 0xBF]),
            "Config scalar and list option strings are lossless"u8);
        JsonProperty mapping = surrogate.Options.Get("paths"u8)!.Value.EnumerateObject().Single();
        Check(
            JsonStrings.GetName(mapping) == Utf8String.Copy([0x6D, 0x6F, 0x64, 0x75, 0x6C, 0x65, 0x2D, 0xED, 0xA0, 0x80, 0x2F, 0x2A]) && JsonStrings.GetString(mapping.Value[0]) == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x73, 0x72, 0x63, 0x2D, 0xED, 0xBF, 0xBF, 0x2F, 0x2A]),
            "Path mapping keys and values preserve UTF-16 code units"u8);
        var surrogateCli = new CommandLineParser(fs, "/app"u8).Parse(["--outDir"u8, Utf8String.Copy([0x63, 0x6C, 0x69, 0x2D, 0xED, 0xA0, 0x80]), "--typeRoots"u8, Utf8String.Copy([0x74, 0x79, 0x70, 0x65, 0x73, 0x2D, 0xED, 0xBF, 0xBF])]);
        Check(
            surrogateCli.Options.String("outDir"u8) == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x63, 0x6C, 0x69, 0x2D, 0xED, 0xA0, 0x80])
                && surrogateCli.Options.Strings("typeRoots"u8)!.Single() == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x74, 0x79, 0x70, 0x65, 0x73, 0x2D, 0xED, 0xBF, 0xBF]),
            "Command line path options preserve UTF-16 code units"u8);
        Utf8String response = Utf8String.Copy([0x2D, 0x2D, 0x6F, 0x75, 0x74, 0x44, 0x69, 0x72, 0x20, 0x22, 0x72, 0x65, 0x73, 0x70, 0x6F, 0x6E, 0x73, 0x65, 0x2D, 0xED, 0xA0, 0x80, 0x22]);
        var responseBytes = new List<byte> { 0xff, 0xfe };
        for (int at = 0; at < response.Length;)
        {
            int point = Wtf8.Decode(response.Span[at..], out int width);
            at += width;
            responseBytes.Add((byte)point);
            responseBytes.Add((byte)(point >> 8));
        }
        fs.WriteFile("/app/surrogate.rsp"u8, responseBytes.ToArray());
        Check(
            new CommandLineParser(fs, "/app"u8).Parse(["@surrogate.rsp"u8]).Options.String("outDir"u8) == Utf8String.Copy([0x2F, 0x61, 0x70, 0x70, 0x2F, 0x72, 0x65, 0x73, 0x70, 0x6F, 0x6E, 0x73, 0x65, 0x2D, 0xED, 0xA0, 0x80]),
            "UTF-16 response file preserves path code units"u8);
        fs.WriteFile(
            "/app/unknown-surrogate.json"u8,
            Encoding.UTF8.GetBytes("{\"files\":[\"input.ts\"],\"compilerOptions\":{\"bad\\uD800\":true}}"));
        var unknownSurrogate = new ConfigParser(fs, "/app"u8).Parse("unknown-surrogate.json"u8);
        Check(
            unknownSurrogate.Diagnostics is [{ Code: DiagnosticCode.UnknownCompilerOption0, Length: > 0 }]
                && unknownSurrogate.Diagnostics[0].Arguments[0] == Utf8String.Copy([0x62, 0x61, 0x64, 0xED, 0xA0, 0x80]),
            "Unknown option with unpaired-surrogate key is diagnosed losslessly"u8);
        ParsedConfig NumericConfig(Utf8String value, bool option, Utf8String? optionName = null)
        {
            Utf8String property = option ? Utf8String.Concat(Utf8String.Concat("\"compilerOptions\":{\""u8, optionName ?? Utf8String.Copy("maxNodeModuleJsDepth"u8), "\":"u8), value, "}"u8) : Utf8String.Concat("\"custom\":"u8, value);
            fs.WriteFile("/app/numeric.json"u8, (Utf8String.Copy("{"u8) + property + ",\"files\":[\"main.ts\"]}"u8).Span.ToArray());
            return new ConfigParser(fs, "/app"u8).Parse("numeric.json"u8);
        }
        foreach (Utf8String value in new Utf8String[] { "1e309"u8, "-1e309"u8, new Utf8String('9', 400), Utf8String.Copy("-0x"u8) + new Utf8String('f', 300) })
            Check(
                NumericConfig(value, false).Diagnostics.Length == 0,
                Utf8String.Copy("Unknown metadata retains numeric values beyond finite double range: "u8) + value[..Math.Min(value.Length, 16)]);
        foreach (var (text, value) in new[]
        {
            ("2e0", 2d),
            ("0x10", 16d),
            ("0o20", 16d),
            ("0b10000", 16d),
            ("1.5", 1.5),
            ("-1", -1d),
            ("9223372036854775807", (double)long.MaxValue),
            ("1e309", double.PositiveInfinity),
            ("-1e309", double.NegativeInfinity),
            ("9223372036854775808", 9223372036854775808d)
        })
        {
            var numeric = NumericConfig(Utf8String.FromString(text), true);
            Check(
                numeric.Diagnostics.Length == 0 && numeric.Options.Number("maxNodeModuleJsDepth"u8) == value,
                Utf8String.Copy("TypeScript numeric option preserves Number semantics: "u8) + Utf8String.FromString(text));
        }
        foreach (Utf8String value in new Utf8String[] { "1e309"u8, "-1e309"u8, "9223372036854775808"u8 })
        {
            var numeric = NumericConfig(value, true, "checkers"u8);
            Check(
                numeric.Diagnostics is [{ Code: DiagnosticCode.CompilerOption0RequiresAValueOfType1, Length: > 0 }]
                    && numeric.Options.Get("checkers"u8)?.ValueKind == JsonValueKind.Null,
                Utf8String.Copy("Native worker-count option rejects out-of-range values without overflowing: "u8) + value);
        }
        fs.WriteFile(
            "/app/watch.json"u8,
            Encoding.UTF8.GetBytes(
                "{\"watchOptions\":{\"watchFile\":\"useFsEvents\",\"excludeDirectories\":[\"${configDir}/cache\"]},\"include\":[\"./src/**/*.ts\"],\"exclude\":[\"**/.*/\"]}"));
        var watch = new ConfigParser(fs, "/app"u8).Parse("watch.json"u8);
        Check(
            watch.WatchOptions.String("watchFile"u8) == "usefsevents"u8
                && watch.WatchOptions.Get("excludeDirectories"u8)?[0].GetString() == "/app/cache",
            "Watch configuration and template substitution"u8);
        Check(
            watch.WildcardDirectories.Count == 1 && watch.WildcardDirectories["/app/src"u8],
            "Wildcard watch directories normalize dot prefixes"u8);
        var insensitive = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/Project/Source/Value.ts"u8] = [5] }, false);
        Check(
            insensitive.FileExists("/project/source/value.ts"u8)
                && insensitive.RealPath("/PROJECT/SOURCE/VALUE.TS"u8) == "/Project/Source/Value.ts"u8,
            "Case-insensitive host identity and actual casing"u8);
        Check(
            insensitive.GetAccessibleEntries("/project/source"u8).Files.SequenceEqual([Utf8String.Copy("Value.ts"u8)]),
            "Case-insensitive host enumeration preserves names"u8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new ConfigParser(fs, "/app"u8).Parse("tsconfig.json"u8, cancellation: cancellation.Token);
            throw new InvalidDataException("Config ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        var libraries = new LibraryFileSystem(new PhysicalFileSystem());
        Check(
            libraries.GetAccessibleEntries(libraries.LibraryDirectory).Files.Length >= 108,
            "Library inventory in current resource layout"u8);
        Check(
            libraries.ReadFile(
                CompilerPath.Combine(
                    libraries.LibraryDirectory,
                    "lib.d.ts"u8))!.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(repository.ToString(), "tsc/internal/bundled/libs/lib.d.ts"))),
            "Library bytes in current resource layout"u8);
        if (LibraryFileSystem.Embedded)
        {
            Check(
                libraries.DirectoryExists(libraries.LibraryDirectory + "/"u8)
                    && libraries.FileExists(libraries.LibraryDirectory + "/./lib.d.ts"u8),
                "Bundled directory normalization"u8);
            Check(
                !libraries.FileExists(libraries.LibraryDirectory + "/lib.d.ts/"u8)
                    && libraries.ReadFile(libraries.LibraryDirectory + "/lib.d.ts/"u8) is null,
                "Bundled file cannot be read as directory"u8);
            foreach (Action write in new Action[]
            {
                () => libraries.AppendFile(libraries.LibraryDirectory + "/lib.d.ts"u8, []),
                () => libraries.Remove(libraries.LibraryDirectory),
                () => libraries.SetTimes(libraries.LibraryDirectory + "/lib.d.ts"u8, DateTime.UtcNow, DateTime.UtcNow)
            })
                try
                {
                    write();
                    throw new InvalidDataException("Mutated bundled resource");
                }
                catch (UnauthorizedAccessException)
                {
                    assertions++;
                }
        }
        Utf8String scratchRoot = Utf8String.FromString(Path.GetFullPath(Path.Combine(repository.ToString(), "built/csharp")));
        Utf8String scratch = Utf8String.FromString(Path.Combine(scratchRoot.ToString(), "host-filesystem-" + Guid.NewGuid().ToString("N")));
        var physical = new PhysicalFileSystem();
        try
        {
            Utf8String file = Utf8String.FromString(Path.Combine(scratch.ToString(), "nested", "file.ts"));
            physical.WriteFile(file, [1, 2]);
            physical.AppendFile(file, [3]);
            Check(
                physical.ReadFile(file)!.SequenceEqual(new byte[] { 1, 2, 3 }) && physical.Stat(file)?.Length == 3,
                "Physical write, append, read and stat"u8);
            DateTime time = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            physical.SetTimes(file, time, time);
            Check(physical.Stat(file)?.LastWriteTimeUtc == time, "Physical file timestamps"u8);
            Check(physical.GetAccessibleEntries(scratch).Directories.SequenceEqual([Utf8String.Copy("nested"u8)]), "Physical directory enumeration"u8);
            Check(physical.RealPath(file).EndsWith("/nested/file.ts"u8, StringComparison.Ordinal), "Physical canonical path separators"u8);
            Utf8String invalid = Utf8String.FromString(Path.Combine(scratch.ToString(), "bad\0.json"));
            Check(
                !physical.FileExists(invalid)
                    && physical.ReadFile(invalid) is null
                    && physical.Stat(invalid) is null
                    && physical.RealPath(invalid) == invalid,
                "NUL physical paths are inaccessible rather than crashing read-only host operations"u8);
            Check(
                new ConfigParser(physical, scratch).Parse("bad\0.json"u8).Diagnostics is [{ Code: DiagnosticCode.CannotReadFile0 }],
                "Invalid physical config path produces a read diagnostic"u8);
            if (OperatingSystem.IsWindows())
            {
                Utf8String surrogateFile = Utf8String.FromString(Path.Combine(scratch.ToString(), "surrogate-\ud800.ts"));
                physical.WriteFile(surrogateFile, [7]);
                Check(
                    physical.ReadFile(surrogateFile)!.SequenceEqual(new byte[] { 7 })
                        && physical.GetAccessibleEntries(scratch).Files.Contains(Utf8String.Concat("surrogate-"u8, new byte[] { 0xED, 0xA0, 0x80 }, ".ts"u8)),
                    "Physical Windows filenames round-trip unpaired UTF-16 code units"u8);
            }
        }
        finally
        {
            if (!Path.GetFullPath(scratch.ToString()).StartsWith((scratchRoot + Path.DirectorySeparatorChar).ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fixture path");
            physical.Remove(scratch);
        }
        fs.WriteFile("/app/malformed.json"u8, Encoding.UTF8.GetBytes("{ this is not json"));
        var malformed = new ConfigParser(fs, "/app"u8).Parse("malformed.json"u8);
        Check(
            malformed.Diagnostics.Length > 0
                && malformed.Diagnostics.All(d => d.FileName == "/app/malformed.json"u8 && d.Start >= 0 && d.Start <= 18),
            "Malformed JSON reports located recovery errors"u8);
        Console.WriteLine($"Hosts: {assertions} assertions; embedded={LibraryFileSystem.Embedded}");
    }
}
