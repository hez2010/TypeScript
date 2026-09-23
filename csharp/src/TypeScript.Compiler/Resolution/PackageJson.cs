using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

public enum PackageMapKind
{
    Conditions,
    Subpaths,
    Imports,
    Invalid
}

/// <summary>Owned JSON values retain declaration order, which determines conditional export precedence.</summary>
public sealed class PackageJson
{
    private readonly JsonElement root;
    public bool Parseable { get; }
    public string Directory { get; }
    public string? Name => String("name");
    public string? Version => String("version");
    public string? Type => String("type");

    internal PackageJson WithDirectory(string directory) => directory == Directory ? this : new(directory, root, Parseable);

    private PackageJson(string directory, JsonElement root, bool parseable)
    {
        Directory = directory;
        this.root = root;
        Parseable = parseable;
    }

    public static PackageJson Parse(string directory, ReadOnlyMemory<byte> contents)
    {
        try
        {
            using var document = JsonDocument.Parse(contents, new() { MaxDepth = int.MaxValue });
            return new(directory, document.RootElement.Clone(), true);
        }
        catch (JsonException)
        {
            return new(directory, default, false);
        }
    }

    public JsonElement Get(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : default;

    public string? String(string name) => Get(name) is { ValueKind: JsonValueKind.String } value ? JsonStrings.GetString(value) : null;

    public IEnumerable<(string Name, string Version, string Field)> Dependencies()
    {
        foreach (string field in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            JsonElement value = Get(field);
            if (value.ValueKind != JsonValueKind.Object)
                continue;
            var entries = Properties(value).ToArray();
            // Expected<map[string]string> rejects the entire field if any value has the wrong type.
            if (entries.Any(p => p.Value.ValueKind != JsonValueKind.String))
                continue;
            foreach (var entry in entries)
                yield return (entry.Name, JsonStrings.GetString(entry.Value), field);
        }
    }

    public bool HasDependency(string name) => Dependencies().Any(d => d.Name == name);

    public IReadOnlySet<string> RuntimeDependencies() => Dependencies().Where(d => d.Field != "devDependencies")
        .Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    public JsonElement VersionPaths(SemanticVersion compilerVersion)
    {
        JsonElement versions = Get("typesVersions");
        if (versions.ValueKind != JsonValueKind.Object)
            return default;
        foreach (var entry in Properties(versions))
            if (VersionRange.Parse(entry.Name)?.Test(compilerVersion) == true)
                return entry.Value.ValueKind == JsonValueKind.Object ? entry.Value : default;
        return default;
    }

    public static PackageMapKind MapKind(JsonElement value)
    {
        bool dot = false, hash = false, other = false;
        if (value.ValueKind != JsonValueKind.Object)
            return PackageMapKind.Invalid;
        foreach (var entry in Properties(value))
        {
            if (entry.Name.Length == 0)
                continue;
            dot |= entry.Name[0] == '.';
            hash |= entry.Name[0] == '#';
            other |= entry.Name[0] is not ('.' or '#');
        }
        return other && (dot || hash) ? PackageMapKind.Invalid
            : dot ? PackageMapKind.Subpaths : hash ? PackageMapKind.Imports : PackageMapKind.Conditions;
    }

    public static IEnumerable<JsonProperty> Properties(JsonElement value)
    {
        // Go's ordered map replaces a duplicate value without moving its first insertion position.
        var entries = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
            entries[entry.Name] = entry;
        return entries.Values;
    }
}

public sealed record PackageJsonEntry(string Directory, bool DirectoryExists, PackageJson? Contents);

/// <summary>Cache entries belong to one filesystem generation. Invalidating serializes with readers.</summary>
public sealed class PackageJsonCache(IFileSystem fileSystem, string currentDirectory)
{
    private readonly object gate = new();
    private readonly Dictionary<string, PackageJsonEntry> entries = new(
        fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public PackageJsonEntry Get(string directory)
    {
        directory = CompilerPath.Resolve(currentDirectory, directory);
        if (directory.Length > CompilerPath.RootLength(directory))
            directory = directory.TrimEnd('/');
        lock (gate)
        {
            if (entries.TryGetValue(directory, out var existing))
                return existing with { Directory = directory, Contents = existing.Contents?.WithDirectory(directory) };
            bool exists = fileSystem.DirectoryExists(directory);
            byte[]? bytes = exists ? fileSystem.ReadFile(CompilerPath.Combine(directory, "package.json")) : null;
            var entry = new PackageJsonEntry(
                directory,
                exists,
                bytes is null ? null : PackageJson.Parse(directory, SourceEncoding.DecodeBytes(bytes)));
            entries.Add(directory, entry);
            return entry;
        }
    }

    public void Invalidate()
    {
        lock (gate)
            entries.Clear();
    }

    public PackageJson? Scope(string directory)
    {
        foreach (string path in Ancestors(CompilerPath.Resolve(currentDirectory, directory)))
        {
            if (Get(path).Contents is { } package)
                return package;
        }
        return null;
    }

    internal static IEnumerable<string> Ancestors(string directory)
    {
        while (true)
        {
            yield return directory;
            string parent = CompilerPath.DirectoryName(directory);
            if (parent == directory || directory.Length == 0)
                yield break;
            directory = parent;
        }
    }
}
