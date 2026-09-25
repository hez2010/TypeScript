using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ModuleSpecifierGenerationTests
{
    internal static int Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Module generation assertion {checks + 1}");
            checks++;
        }
        using var input = JsonDocument.Parse("""
            {"directory":"/project","symlinks":{"/store/pkg":["/project/node_modules/pkg"]}}
            """);
        var fs = new MemoryFileSystem(new Dictionary<string, byte[]>());
        var host = new FixtureHost(input.RootElement, fs);
        var generator = new ModuleSpecifierGenerator(host, new());
        var source = Parser.ParseSourceFile(new("/project/main.ts"), new SourceText(""));
        var result = generator.ForFile(source, "/store/pkg/index.d.ts");
        Check(result.Kind == ModuleSpecifierKind.NodeModules && result.Specifiers.SequenceEqual(["pkg"]));
        var paths = generator.AllPaths(source.FileName, "/store/pkg/index.d.ts");
        Check(paths.Count == 2 && paths[0].FileName == "/project/node_modules/pkg/index.d.ts");
        Check(generator.AllPaths("/store/pkg/own.ts", "/store/pkg/index.d.ts").Count == 1);
        try
        {
            ((IList<string>)result.Specifiers)[0] = "changed";
            throw new InvalidOperationException("Mutable naming result");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        foreach (Action query in new Action[] {
            () => generator.EachPath(source.FileName, "/store/pkg/index.d.ts", cancellation: stop.Token),
            () => generator.AllPaths(source.FileName, "/store/pkg/index.d.ts", stop.Token),
            () => generator.ForFile(source, "/store/pkg/index.d.ts", cancellation: stop.Token),
            () => generator.Select(paths, source, cancellation: stop.Token),
            () => generator.Local("/store/pkg/index.d.ts", source, new(), 0, cancellation: stop.Token) })
        {
            try
            {
                query();
                throw new InvalidOperationException("Canceled generation completed");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(generator.ForFile(source, "/store/pkg/index.d.ts").Specifiers.SequenceEqual(["pkg"]));
        try
        {
            generator.ForFile(
                source,
                "/store/pkg/index.d.ts",
                new(Excluded: _ => throw new InvalidOperationException("callback")),
                forAutoImport: true);
            throw new InvalidOperationException("Missing callback failure");
        }
        catch (InvalidOperationException error) when (error.Message == "callback")
        {
            checks++;
        }
        Check(generator.ForFile(source, "/store/pkg/index.d.ts").Specifiers.SequenceEqual(["pkg"]));
        return checks;
    }

    internal static void Write(JsonElement input, Utf8JsonWriter writer, IFileSystem fs, SourceFileNode source, CompilerOptions options)
    {
        var host = new FixtureHost(input, fs);
        if (options.Get("paths") is not null)
            options.SetString("pathsBasePath", host.CurrentDirectory);
        if (options.Strings("rootDirs") is { } roots)
            options.SetArray("rootDirs", roots.Select(p => OptionValues.String(CompilerPath.Resolve(host.CurrentDirectory, p))));
        foreach (string key in new[] { "outDir", "declarationDir", "rootDir" })
            if (options.String(key) is { } directory)
                options.SetString(key, CompilerPath.Resolve(host.CurrentDirectory, directory));
        var generator = new ModuleSpecifierGenerator(host, options);
        string prefix = host.Text("excludedPrefix");
        var preferences = new ModuleSpecifierPreferences(host.Text("relative"), host.Text("preference"),
            prefix.Length == 0 ? null : s => s.StartsWith(prefix, StringComparison.Ordinal));
        var mode = (ReferenceResolutionMode)host.Number("mode");
        string operation = host.Text("operation");
        if (operation == "all-paths")
        {
            writer.WriteStartArray();
            foreach (var path in generator.AllPaths(source.FileName, host.Text("target")))
            {
                writer.WriteStartObject();
                writer.WriteString("FileName", path.FileName);
                writer.WriteBoolean("IsInNodeModules", path.IsInNodeModules);
                writer.WriteBoolean("IsRedirect", path.IsRedirect);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            return;
        }
        if (operation == "local")
        {
            writer.WriteStringValue(generator.Local(host.Text("target"), source, preferences, mode, host.Boolean("pathsOnly")));
            return;
        }
        var result = operation == "select"
            ? generator.Select(input.GetProperty("modulePaths").EnumerateArray().Select(p => new ModuleSpecifierPath(
                p.GetProperty("FileName").GetString()!,
                p.GetProperty("IsInNodeModules").GetBoolean(),
                p.GetProperty("IsRedirect").GetBoolean())).ToArray(),
                source, preferences, mode, host.Boolean("forAutoImport"))
            : generator.ForFile(source, host.Text("target"), preferences, mode, host.Boolean("forAutoImport"));
        writer.WriteStartObject();
        writer.WriteStartArray("Specifiers");
        foreach (string specifier in result.Specifiers)
            writer.WriteStringValue(specifier);
        writer.WriteEndArray();
        writer.WriteNumber("Kind", (int)result.Kind);
        writer.WriteEndObject();
    }

    private sealed class FixtureHost(JsonElement input, IFileSystem fs) : IModuleSpecifierHost
    {
        public IFileSystem FileSystem => fs;
        public string CurrentDirectory => Text("directory");
        public string ConfigFileName => CompilerPath.Combine(CurrentDirectory, "tsconfig.json");
        public string CommonSourceDirectory => Text("commonDirectory", CurrentDirectory);
        public string GlobalTypingsCache => Text("globalTypingsCache");
        public IReadOnlyList<string> ContentMapperExtensions => Strings("mapperExtensions");

        public string OriginalSourceFileName(SourceFileNode source) => Text("originalSource", source.FileName);

        public string ProjectOutput(string sourceFileName) => Text("referenceOutput");

        public IReadOnlyList<string> RedirectTargets(string target) => Strings("redirects");

        public IReadOnlyList<string> SymlinkDirectories(string realDirectory)
        {
            if (input.TryGetProperty("symlinks", out var links))
                foreach (var entry in links.EnumerateObject())
                    if (CompilerPath.Resolve(CurrentDirectory, entry.Name).TrimEnd('/').Equals(realDirectory.TrimEnd('/'),
                        fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        return entry.Value.EnumerateArray().Select(JsonStrings.GetString).Distinct(StringComparer.Ordinal).ToArray();
            return [];
        }

        public ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import)
        {
            string name = import is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)import).Text;
            return input.TryGetProperty("importTargets", out var targets) && targets.TryGetProperty(name, out var target)
                ? new(JsonStrings.GetString(target)) : null;
        }

        public ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import)
        {
            string? name = import is StringLiteralNode literal ? literal.Text : (import as NoSubstitutionTemplateLiteralNode)?.Text;
            return name is not null && input.TryGetProperty("importModes", out var modes) && modes.TryGetProperty(name, out var mode)
                ? (ReferenceResolutionMode)mode.GetInt32() : (ReferenceResolutionMode)Number("defaultMode");
        }

        internal string Text(string key, string fallback = "") =>
            input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;

        internal int Number(string key) => input.TryGetProperty(key, out var value) ? value.GetInt32() : 0;

        internal bool Boolean(string key) => input.TryGetProperty(key, out var value) && value.GetBoolean();

        private string[] Strings(string key) =>
            input.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
    }
}
