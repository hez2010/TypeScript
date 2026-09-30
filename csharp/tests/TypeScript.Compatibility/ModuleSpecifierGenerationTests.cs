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
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>());
        var host = new FixtureHost(input.RootElement, fs);
        var generator = new ModuleSpecifierGenerator(host, new());
        var source = Parser.ParseSourceFile(new("/project/main.ts"u8), new SourceText(""u8));
        var result = generator.ForFile(source, "/store/pkg/index.d.ts"u8);
        Check(result.Kind == ModuleSpecifierKind.NodeModules && result.Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)]));
        var paths = generator.AllPaths(source.FileName, "/store/pkg/index.d.ts"u8);
        Check(paths.Count == 2 && paths[0].FileName == "/project/node_modules/pkg/index.d.ts"u8);
        Check(generator.AllPaths("/store/pkg/own.ts"u8, "/store/pkg/index.d.ts"u8).Count == 1);
        try
        {
            ((IList<Utf8String>)result.Specifiers)[0] = "changed"u8;
            throw new InvalidOperationException("Mutable naming result");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        foreach (Action query in new Action[] {
            () => generator.EachPath(source.FileName, "/store/pkg/index.d.ts"u8, cancellation: stop.Token),
            () => generator.AllPaths(source.FileName, "/store/pkg/index.d.ts"u8, stop.Token),
            () => generator.ForFile(source, "/store/pkg/index.d.ts"u8, cancellation: stop.Token),
            () => generator.Select(paths, source, cancellation: stop.Token),
            () => generator.Local("/store/pkg/index.d.ts"u8, source, new(), 0, cancellation: stop.Token) })
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
        Check(generator.ForFile(source, "/store/pkg/index.d.ts"u8).Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)]));
        try
        {
            generator.ForFile(
                source,
                "/store/pkg/index.d.ts"u8,
                new(Excluded: _ => throw new InvalidOperationException("callback")),
                forAutoImport: true);
            throw new InvalidOperationException("Missing callback failure");
        }
        catch (InvalidOperationException error) when (error.Message == "callback")
        {
            checks++;
        }
        Check(generator.ForFile(source, "/store/pkg/index.d.ts"u8).Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)]));
        return checks;
    }

    internal static void Write(JsonElement input, Utf8JsonWriter writer, IFileSystem fs, SourceFileNode source, CompilerOptions options)
    {
        var host = new FixtureHost(input, fs);
        if (options.Get("paths"u8) is not null)
            options.SetString("pathsBasePath"u8, host.CurrentDirectory);
        if (options.Strings("rootDirs"u8) is { } roots)
            options.SetArray("rootDirs"u8, roots.Select(p => OptionValues.String(CompilerPath.Resolve(host.CurrentDirectory, p))));
        foreach (Utf8String key in new Utf8String[] { "outDir"u8, "declarationDir"u8, "rootDir"u8 })
            if (options.String(key) is { } directory)
                options.SetString(key, CompilerPath.Resolve(host.CurrentDirectory, directory));
        var generator = new ModuleSpecifierGenerator(host, options);
        Utf8String prefix = host.Text("excludedPrefix"u8);
        var preferences = new ModuleSpecifierPreferences(host.Text("relative"u8), host.Text("preference"u8),
            prefix.Length == 0 ? null : s => s.StartsWith(prefix, StringComparison.Ordinal));
        var mode = (ReferenceResolutionMode)host.Number("mode"u8);
        Utf8String operation = host.Text("operation"u8);
        if (operation == "all-paths"u8)
        {
            writer.WriteStartArray();
            foreach (var path in generator.AllPaths(source.FileName, host.Text("target"u8)))
            {
                writer.WriteStartObject();
                writer.WriteString("FileName"u8, path.FileName);
                writer.WriteBoolean("IsInNodeModules"u8, path.IsInNodeModules);
                writer.WriteBoolean("IsRedirect"u8, path.IsRedirect);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            return;
        }
        if (operation == "local"u8)
        {
            writer.WriteStringValue(generator.Local(host.Text("target"u8), source, preferences, mode, host.Boolean("pathsOnly"u8)));
            return;
        }
        var result = operation == "select"u8
            ? generator.Select(input.GetProperty("modulePaths"u8).EnumerateArray().Select(p => new ModuleSpecifierPath(
                JsonStrings.GetString(p.GetProperty("FileName"u8))!,
                p.GetProperty("IsInNodeModules"u8).GetBoolean(),
                p.GetProperty("IsRedirect"u8).GetBoolean())).ToArray(),
                source, preferences, mode, host.Boolean("forAutoImport"u8))
            : generator.ForFile(source, host.Text("target"u8), preferences, mode, host.Boolean("forAutoImport"u8));
        writer.WriteStartObject();
        writer.WriteStartArray("Specifiers"u8);
        foreach (Utf8String specifier in result.Specifiers)
            writer.WriteStringValue(specifier);
        writer.WriteEndArray();
        writer.WriteNumber("Kind"u8, (int)result.Kind);
        writer.WriteEndObject();
    }

    private sealed class FixtureHost(JsonElement input, IFileSystem fs) : IModuleSpecifierHost
    {
        public IFileSystem FileSystem => fs;
        public Utf8String CurrentDirectory => Text("directory"u8);
        public Utf8String ConfigFileName => CompilerPath.Combine(CurrentDirectory, "tsconfig.json"u8);
        public Utf8String CommonSourceDirectory => Text("commonDirectory"u8, CurrentDirectory);
        public Utf8String GlobalTypingsCache => Text("globalTypingsCache"u8);
        public IReadOnlyList<Utf8String> ContentMapperExtensions => Strings("mapperExtensions"u8);

        public Utf8String OriginalSourceFileName(SourceFileNode source) => Text("originalSource"u8, source.FileName);

        public Utf8String ProjectOutput(Utf8String sourceFileName) => Text("referenceOutput"u8);

        public IReadOnlyList<Utf8String> RedirectTargets(Utf8String target) => Strings("redirects"u8);

        public IReadOnlyList<Utf8String> SymlinkDirectories(Utf8String realDirectory)
        {
            if (input.TryGetProperty("symlinks"u8, out var links))
                foreach (var entry in links.EnumerateObject())
                    if (CompilerPath.Resolve(CurrentDirectory, JsonStrings.GetName(entry)).TrimEnd((byte)'/').Equals(realDirectory.TrimEnd((byte)'/'),
                        fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        return entry.Value.EnumerateArray().Select(JsonStrings.GetString).Distinct(Utf8StringComparer.Ordinal).ToArray();
            return [];
        }

        public ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import)
        {
            Utf8String name = import is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)import).Text;
            return input.TryGetProperty("importTargets"u8, out var targets) && targets.TryGetProperty(name, out var target)
                ? new(JsonStrings.GetString(target)) : null;
        }

        public ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import)
        {
            Utf8String? name = import is StringLiteralNode literal ? literal.Text : (import as NoSubstitutionTemplateLiteralNode)?.Text;
            return name is not null && input.TryGetProperty("importModes"u8, out var modes) && modes.TryGetProperty(name.Value.Span, out var mode)
                ? (ReferenceResolutionMode)mode.GetInt32() : (ReferenceResolutionMode)Number("defaultMode"u8);
        }

        internal Utf8String Text(Utf8String key, Utf8String fallback = default) =>
            input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;

        internal int Number(Utf8String key) => input.TryGetProperty(key, out var value) ? value.GetInt32() : 0;

        internal bool Boolean(Utf8String key) => input.TryGetProperty(key, out var value) && value.GetBoolean();

        private Utf8String[] Strings(Utf8String key) =>
            input.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
    }
}
