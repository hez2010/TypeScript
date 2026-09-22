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
        string Text(string name, string fallback = "") => input.TryGetProperty(name, out var value) ? value.GetString()! : fallback;
        bool Flag(string name) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        string mode = Text("mode"), path = Text("path"), other = Text("other");
        bool sensitive = Flag("sensitive");
        if (mode == "glob")
        {
            writer.WriteBooleanValue(
                ConfigParser.GlobMatches(CompilerPath.Resolve(Text("directory"), path), other, sensitive, Flag("exclude")));
            return;
        }
        var files = new Dictionary<string, byte[]>();
        if (input.TryGetProperty("files", out var filesInput))
            foreach (var item in filesInput.EnumerateObject())
                files[item.Name] = Encoding.UTF8.GetBytes(item.Value.GetString()!);
        var symlinks = new Dictionary<string, string>();
        if (input.TryGetProperty("symlinks", out var linksInput))
            foreach (var item in linksInput.EnumerateObject())
                symlinks[item.Name] = item.Value.GetString()!;
        var fs = new MemoryFileSystem(files, sensitive, Text("directory", "/"), symlinks);
        if (mode == "readDirectory")
        {
            string[]? Strings(string key) =>
                input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
                    : null;
            string[] result = FileMatcher.ReadDirectory(
                fs,
                path,
                Text("directory", "/"),
                Strings("extensions"),
                Strings("excludes"),
                Strings("includes"),
                input.TryGetProperty("depth", out var depth) ? depth.GetInt32() : -1);
            writer.WriteStartArray();
            foreach (string file in result)
                writer.WriteStringValue(file);
            writer.WriteEndArray();
            return;
        }
        writer.WriteStartObject();
        if (mode == "path")
        {
            writer.WriteString("normalize", CompilerPath.Normalize(path));
            writer.WriteNumber("root", CompilerPath.RootLength(path));
            writer.WriteBoolean("absolute", CompilerPath.IsAbsolute(path));
            writer.WriteBoolean("url", CompilerPath.IsUrl(path));
            writer.WriteString("directory", CompilerPath.DirectoryName(path));
            writer.WriteString("base", CompilerPath.BaseName(path));
            writer.WriteString("extension", CompilerPath.Extension(path));
            writer.WriteBoolean("declaration", CompilerPath.IsDeclarationFile(path));
            writer.WriteString("combine", CompilerPath.Combine(path, other));
            writer.WriteString("resolve", CompilerPath.Resolve(path, other));
            writer.WriteBoolean("contains", CompilerPath.Contains(path, other, sensitive));
            writer.WriteString("relative", CompilerPath.Relative(path, other, sensitive));
        }
        else
        {
            CompilerOptions options;
            string[] names;
            Diagnostic[] diagnostics;
            if (mode == "cli")
            {
                var result = new CommandLineParser(
                    fs,
                    Text(
                        "directory",
                        "/")).Parse(input.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray(), Flag("build"));
                options = result.Options;
                names = result.FileNames;
                diagnostics = result.Diagnostics;
            }
            else
            {
                var existing = new CompilerOptions();
                if (input.TryGetProperty("existing", out var existingInput))
                    foreach (var property in existingInput.EnumerateObject())
                        existing.Set(property.Name, property.Value);
                var result = new ConfigParser(fs, Text("directory", "/")).Parse(path, existing);
                if (mode == "configUnits")
                {
                    void Units(string value)
                    {
                        writer.WriteStartArray();
                        foreach (char unit in value)
                            writer.WriteNumberValue(unit);
                        writer.WriteEndArray();
                    }
                    writer.WritePropertyName("files");
                    writer.WriteStartArray();
                    foreach (string file in result.FileNames)
                        Units(file);
                    writer.WriteEndArray();
                    writer.WritePropertyName("outDir");
                    Units(result.Options.String("outDir") ?? "");
                    writer.WritePropertyName("typeRoots");
                    writer.WriteStartArray();
                    foreach (string item in result.Options.Strings("typeRoots") ?? [])
                        Units(item);
                    writer.WriteEndArray();
                    writer.WritePropertyName("paths");
                    writer.WriteStartArray();
                    if (result.Options.Get("paths") is { ValueKind: JsonValueKind.Object } paths)
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
                    writer.WritePropertyName("diagnostics");
                    writer.WriteStartArray();
                    foreach (var diagnostic in result.Diagnostics)
                        writer.WriteNumberValue(diagnostic.Code);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    return;
                }
                options = result.Options;
                names = result.FileNames;
                diagnostics = result.Diagnostics;
                writer.WritePropertyName("references");
                writer.WriteStartArray();
                foreach (var reference in result.References)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(reference.Path);
                    writer.WriteBooleanValue(reference.Circular);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("compileOnSave", result.CompileOnSave);
            }
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            foreach (var pair in options.Values)
            {
                if (mode != "cli"
                    && (pair.Value.ValueKind == JsonValueKind.Null
                        || pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString() == ""))
                    continue;
                writer.WritePropertyName(pair.Key);
                var definition = OptionDefinitions.Find(pair.Key) ?? OptionDefinitions.Find(
                    pair.Key,
                    OptionGroup.Build) ?? OptionDefinitions.Find(pair.Key, OptionGroup.Watch);
                if (definition is not null && definition.Values.Length != 0 && pair.Value.ValueKind == JsonValueKind.String)
                    writer.WriteStringValue(Canonical(definition, pair.Value.GetString()!));
                else if (definition is not null && definition.Values.Length != 0 && pair.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var element in pair.Value.EnumerateArray())
                        writer.WriteStringValue(Canonical(definition, element.GetString()!));
                    writer.WriteEndArray();
                }
                else
                    pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (string name in names)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WritePropertyName("diagnostics");
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics)
                writer.WriteNumberValue(diagnostic.Code);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static string Canonical(OptionDefinition definition, string name)
    {
        int index = Array.FindIndex(definition.Values, v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? name : definition.Values[Array.IndexOf(definition.ValueIdentities, definition.ValueIdentities[index])];
    }

    public static void Run(string repository)
    {
        int assertions = 0;
        void Check(bool valid, string message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message);
        }
        var files = new Dictionary<string, byte[]>
        {
            ["/base/config.json"] = Encoding.UTF8.GetBytes(
                "{\"compilerOptions\":{\"outDir\":\"${configDir}/dist\",\"typeRoots\":[\"types\"],\"lib\":[\"ES6\",\"invalid\",1],\"paths\":{\"@/*\":[\"${configDir}/src/*\"]}},\"include\":[\"${configDir}/src\"]}"),
            ["/app/tsconfig.json"] = Encoding.UTF8.GetBytes(
                "{\"extends\":\"../base/config.json\",\"compilerOptions\":{\"allowJs\":true},\"references\":[{\"path\":\"../lib\",\"circular\":true}]}"),
            ["/app/src/a.ts"] = [],
            ["/app/src/a.d.ts"] = [],
            ["/app/src/a.js"] = [],
            ["/app/src/b.js"] = [],
            ["/app/src/.hidden.ts"] = [],
            ["/app/src/.hidden/a.ts"] = [],
            ["/app/src/node_modules/pkg/a.ts"] = [],
            ["/app/src/b.min.js"] = [],
            ["/app/src/data.json"] = [],
        };
        var fs = new MemoryFileSystem(files);
        var parsed = new ConfigParser(fs, "/app").Parse("tsconfig.json");
        Check(
            parsed.FileNames.SequenceEqual(["/app/src/a.ts", "/app/src/b.js"]),
            "Inherited template glob with extension priority, hidden/minified/package exclusion");
        Check(parsed.Options.String("outDir") == "/app/dist", "Template resolves at final config");
        Check(
            parsed.Options.Get("typeRoots")?.EnumerateArray().Single().GetString() == "/base/types",
            "List paths resolve at defining config");
        Check(
            parsed.Options.Get("paths")?.GetProperty("@/*")[0].GetString() == "/app/src/*"
                && parsed.Options.String("pathsBasePath") == "/base",
            "Path mapping ownership and template");
        Check(parsed.References is [{ Path: "/lib", Circular: true }], "Project references");
        Check(
            parsed.Diagnostics.Select(d => d.Code).SequenceEqual([6046, 5024])
                && parsed.Diagnostics.All(d => d.Length > 0 && d.FileName == "/base/config.json"),
            "List validation with source spans");
        var cli = new CommandLineParser(
            fs,
            "/app").Parse(
                ["--composite", "true", "--plugins", "false", "--checkers", "-1", "--lib", "es6,invalid", "--typeRoots", "types", "x.ts"]);
        Check(cli.FileNames.SequenceEqual(["x.ts"]), "Invalid options consume their arguments");
        Check(
            cli.Diagnostics.Select(d => d.Code).SequenceEqual([6230, 6064, 5002, 6046]),
            "Config-only, numeric bound and list diagnostics");
        Check(cli.Options.Get("typeRoots")?.EnumerateArray().Single().GetString() == "/app/types", "Command line list file paths");
        Check(
            ConfigParser.GlobMatches("/a/??.ts", "/a/😀.ts", true) && !ConfigParser.GlobMatches("/a/?.ts", "/a/😀.ts", true),
            "Glob UTF-16 wildcard semantics");
        Check(ConfigParser.GlobMatches("/a/**/*.min.js", "/a/b.min.js", true), "Explicit minified glob");
        Check(ConfigParser.GlobMatches("/a/**", "/a/.hidden/x", true, true), "Exclude recursion includes hidden paths");
        Check(!ConfigParser.GlobMatches("/a/**", "/a/b.ts", true), "Include trailing recursion invalid");
        fs.WriteFile("/app/empty.json", Encoding.UTF8.GetBytes("// empty"));
        Check(new ConfigParser(fs, "/app").Parse("empty.json").Diagnostics.Length == 0, "Comment-only config");
        fs.WriteFile("/app/recover.json", Encoding.UTF8.GetBytes("{compilerOptions:{'strict':true}, files:['a.ts']}"));
        var recovered = new ConfigParser(fs, "/app").Parse("recover.json");
        Check(
            recovered.Options.Boolean("strict") == true && recovered.Diagnostics.All(d => d.Length > 0),
            "JSON recovery retains options and spans");
        fs.CreateDirectory("/empty/nested");
        fs.WriteFile("/empty/file.ts", [1]);
        fs.Remove("/empty/file.ts");
        Check(
            fs.DirectoryExists("/empty") && fs.GetAccessibleEntries("/empty").Directories.SequenceEqual(["nested"]),
            "Empty directories survive file deletion");
        fs.CreateSymbolicLink("/link", "/app");
        Check(
            fs.DirectoryExists("/link/src") && fs.RealPath("/link/src/a.ts") == "/app/src/a.ts",
            "Intermediate directory symlink resolution");
        fs.CreateSymbolicLink("/app/src/loop", "/app/src");
        Check(fs.ReadFile("/app/src/loop/loop/a.ts") is not null, "Repeated directory links can consume the remaining path");
        Check(
            FileMatcher.ReadDirectory(
                fs,
                "/link/src",
                "/",
                [".ts"],
                includes: ["**/*"]).SequenceEqual(["/link/src/a.d.ts", "/link/src/a.ts"]),
            "Symlink traversal terminates at visited real directory");
        fs.WriteFile("/link/new.ts", [4]);
        Check(fs.ReadFile("/app/new.ts")!.SequenceEqual(new byte[] { 4 }), "Symlink writes target file");
        fs.Remove("/link");
        Check(!fs.DirectoryExists("/link") && fs.FileExists("/app/new.ts"), "Removing symlink preserves target");
        fs.CreateSymbolicLink("/cycle-a", "/cycle-b");
        fs.CreateSymbolicLink("/cycle-b", "/cycle-a");
        Check(!fs.FileExists("/cycle-a/file.ts") && fs.ReadFile("/cycle-a/file.ts") is null, "Symlink cycles are reported as inaccessible");
        fs.CreateSymbolicLink("/app/recur", "/app");
        fs.WriteFile("/app/alias-cycle.json", Encoding.UTF8.GetBytes("{\"extends\":\"./recur/alias-cycle.json\"}"));
        Check(
            new ConfigParser(fs, "/app").Parse("alias-cycle.json").Diagnostics.Any(d => d.Code == 18000),
            "Config inheritance detects cycles through directory aliases");
        fs.WriteFile(
            "/app/template.json",
            Encoding.UTF8.GetBytes("{\"compilerOptions\":{\"outDir\":\"${configDir}dist\",\"rootDir\":\"${configDir}\"}}"));
        var template = new ConfigParser(fs, "/app").Parse("template.json");
        Check(
            template.Options.String("outDir") == "/app/dist" && template.Options.String("rootDir") == "/app",
            "Config directory template supplies its directory separator");
        fs.WriteFile(
            "/app/surrogate.json",
            Encoding.UTF8.GetBytes(
                "{\"files\":[\"raw-\\uD800.ts\",\"raw-\\uDFFF.ts\",\"face-\\uD83D\\uDE00.ts\"],\"compilerOptions\":{\"outDir\":\"dir-\\uD800\",\"typeRoots\":[\"types-\\uDFFF\"],\"paths\":{\"module-\\uD800/*\":[\"${configDir}/src-\\uDFFF/*\"]}}}"));
        var surrogate = new ConfigParser(fs, "/app").Parse("surrogate.json");
        Check(
            surrogate.Diagnostics.Length == 0
                && surrogate.FileNames.SequenceEqual(["/app/raw-\ud800.ts", "/app/raw-\udfff.ts", "/app/face-😀.ts"]),
            "Config paths preserve paired and unpaired UTF-16 code units");
        Check(
            surrogate.Options.String("outDir") == "/app/dir-\ud800"
                && surrogate.Options.Strings("typeRoots")!.Single() == "/app/types-\udfff",
            "Config scalar and list option strings are lossless");
        JsonProperty mapping = surrogate.Options.Get("paths")!.Value.EnumerateObject().Single();
        Check(
            JsonStrings.GetName(mapping) == "module-\ud800/*" && JsonStrings.GetString(mapping.Value[0]) == "/app/src-\udfff/*",
            "Path mapping keys and values preserve UTF-16 code units");
        var surrogateCli = new CommandLineParser(fs, "/app").Parse(["--outDir", "cli-\ud800", "--typeRoots", "types-\udfff"]);
        Check(
            surrogateCli.Options.String("outDir") == "/app/cli-\ud800"
                && surrogateCli.Options.Strings("typeRoots")!.Single() == "/app/types-\udfff",
            "Command line path options preserve UTF-16 code units");
        string response = "--outDir \"response-\ud800\"";
        byte[] responseBytes = new byte[2 + response.Length * 2];
        responseBytes[0] = 0xff;
        responseBytes[1] = 0xfe;
        for (int i = 0; i < response.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(responseBytes.AsSpan(2 + i * 2, 2), response[i]);
        fs.WriteFile("/app/surrogate.rsp", responseBytes);
        Check(
            new CommandLineParser(fs, "/app").Parse(["@surrogate.rsp"]).Options.String("outDir") == "/app/response-\ud800",
            "UTF-16 response file preserves path code units");
        fs.WriteFile(
            "/app/unknown-surrogate.json",
            Encoding.UTF8.GetBytes("{\"files\":[\"input.ts\"],\"compilerOptions\":{\"bad\\uD800\":true}}"));
        var unknownSurrogate = new ConfigParser(fs, "/app").Parse("unknown-surrogate.json");
        Check(
            unknownSurrogate.Diagnostics is [{ Code: 5023, Length: > 0 }] && unknownSurrogate.Diagnostics[0].Arguments[0] == "bad\ud800",
            "Unknown option with unpaired-surrogate key is diagnosed losslessly");
        ParsedConfig NumericConfig(string value, bool option, string optionName = "maxNodeModuleJsDepth")
        {
            string property = option ? "\"compilerOptions\":{\"" + optionName + "\":" + value + "}" : "\"custom\":" + value;
            fs.WriteFile("/app/numeric.json", Encoding.UTF8.GetBytes("{" + property + ",\"files\":[\"main.ts\"]}"));
            return new ConfigParser(fs, "/app").Parse("numeric.json");
        }
        foreach (string value in new[] { "1e309", "-1e309", new string('9', 400), "-0x" + new string('f', 300) })
            Check(
                NumericConfig(value, false).Diagnostics.Length == 0,
                "Unknown metadata retains numeric values beyond finite double range: " + value[..Math.Min(value.Length, 16)]);
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
            var numeric = NumericConfig(text, true);
            Check(
                numeric.Diagnostics.Length == 0 && numeric.Options.Number("maxNodeModuleJsDepth") == value,
                "TypeScript numeric option preserves Number semantics: " + text);
        }
        foreach (string value in new[] { "1e309", "-1e309", "9223372036854775808" })
        {
            var numeric = NumericConfig(value, true, "checkers");
            Check(
                numeric.Diagnostics is [{ Code: 5024, Length: > 0 }] && numeric.Options.Get("checkers")?.ValueKind == JsonValueKind.Null,
                "Native worker-count option rejects out-of-range values without overflowing: " + value);
        }
        fs.WriteFile(
            "/app/watch.json",
            Encoding.UTF8.GetBytes(
                "{\"watchOptions\":{\"watchFile\":\"useFsEvents\",\"excludeDirectories\":[\"${configDir}/cache\"]},\"include\":[\"./src/**/*.ts\"],\"exclude\":[\"**/.*/\"]}"));
        var watch = new ConfigParser(fs, "/app").Parse("watch.json");
        Check(
            watch.WatchOptions.String("watchFile") == "usefsevents"
                && watch.WatchOptions.Get("excludeDirectories")?[0].GetString() == "/app/cache",
            "Watch configuration and template substitution");
        Check(
            watch.WildcardDirectories.Count == 1 && watch.WildcardDirectories["/app/src"],
            "Wildcard watch directories normalize dot prefixes");
        var insensitive = new MemoryFileSystem(new Dictionary<string, byte[]> { ["/Project/Source/Value.ts"] = [5] }, false);
        Check(
            insensitive.FileExists("/project/source/value.ts")
                && insensitive.RealPath("/PROJECT/SOURCE/VALUE.TS") == "/Project/Source/Value.ts",
            "Case-insensitive host identity and actual casing");
        Check(
            insensitive.GetAccessibleEntries("/project/source").Files.SequenceEqual(["Value.ts"]),
            "Case-insensitive host enumeration preserves names");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new ConfigParser(fs, "/app").Parse("tsconfig.json", cancellation: cancellation.Token);
            throw new InvalidDataException("Config ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        var libraries = new LibraryFileSystem(new PhysicalFileSystem());
        Check(
            libraries.GetAccessibleEntries(libraries.LibraryDirectory).Files.Length >= 108,
            "Library inventory in current resource layout");
        Check(
            libraries.ReadFile(
                CompilerPath.Combine(
                    libraries.LibraryDirectory,
                    "lib.d.ts"))!.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(repository, "tsc/internal/bundled/libs/lib.d.ts"))),
            "Library bytes in current resource layout");
        if (LibraryFileSystem.Embedded)
        {
            Check(
                libraries.DirectoryExists(libraries.LibraryDirectory + "/")
                    && libraries.FileExists(libraries.LibraryDirectory + "/./lib.d.ts"),
                "Bundled directory normalization");
            Check(
                !libraries.FileExists(libraries.LibraryDirectory + "/lib.d.ts/")
                    && libraries.ReadFile(libraries.LibraryDirectory + "/lib.d.ts/") is null,
                "Bundled file cannot be read as directory");
            foreach (Action write in new Action[]
            {
                () => libraries.AppendFile(libraries.LibraryDirectory + "/lib.d.ts", []),
                () => libraries.Remove(libraries.LibraryDirectory),
                () => libraries.SetTimes(libraries.LibraryDirectory + "/lib.d.ts", DateTime.UtcNow, DateTime.UtcNow)
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
        string scratchRoot = Path.GetFullPath(Path.Combine(repository, "built/csharp"));
        string scratch = Path.Combine(scratchRoot, "host-filesystem-" + Guid.NewGuid().ToString("N"));
        var physical = new PhysicalFileSystem();
        try
        {
            string file = Path.Combine(scratch, "nested", "file.ts");
            physical.WriteFile(file, [1, 2]);
            physical.AppendFile(file, [3]);
            Check(
                physical.ReadFile(file)!.SequenceEqual(new byte[] { 1, 2, 3 }) && physical.Stat(file)?.Length == 3,
                "Physical write, append, read and stat");
            DateTime time = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            physical.SetTimes(file, time, time);
            Check(physical.Stat(file)?.LastWriteTimeUtc == time, "Physical file timestamps");
            Check(physical.GetAccessibleEntries(scratch).Directories.SequenceEqual(["nested"]), "Physical directory enumeration");
            Check(physical.RealPath(file).EndsWith("/nested/file.ts", StringComparison.Ordinal), "Physical canonical path separators");
            string invalid = Path.Combine(scratch, "bad\0.json");
            Check(
                !physical.FileExists(invalid)
                    && physical.ReadFile(invalid) is null
                    && physical.Stat(invalid) is null
                    && physical.RealPath(invalid) == invalid,
                "NUL physical paths are inaccessible rather than crashing read-only host operations");
            Check(
                new ConfigParser(physical, scratch).Parse("bad\0.json").Diagnostics is [{ Code: 5083 }],
                "Invalid physical config path produces a read diagnostic");
            if (OperatingSystem.IsWindows())
            {
                string surrogateFile = Path.Combine(scratch, "surrogate-\ud800.ts");
                physical.WriteFile(surrogateFile, [7]);
                Check(
                    physical.ReadFile(surrogateFile)!.SequenceEqual(new byte[] { 7 })
                        && physical.GetAccessibleEntries(scratch).Files.Contains("surrogate-\ud800.ts"),
                    "Physical Windows filenames round-trip unpaired UTF-16 code units");
            }
        }
        finally
        {
            if (!Path.GetFullPath(scratch).StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fixture path");
            physical.Remove(scratch);
        }
        fs.WriteFile("/app/malformed.json", Encoding.UTF8.GetBytes("{ this is not json"));
        var malformed = new ConfigParser(fs, "/app").Parse("malformed.json");
        Check(
            malformed.Diagnostics.Length > 0
                && malformed.Diagnostics.All(d => d.FileName == "/app/malformed.json" && d.Start >= 0 && d.Start <= 18),
            "Malformed JSON reports located recovery errors");
        Console.WriteLine($"Hosts: {assertions} assertions; embedded={LibraryFileSystem.Embedded}");
    }
}
