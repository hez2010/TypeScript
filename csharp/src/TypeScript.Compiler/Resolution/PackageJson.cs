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
    public Utf8String Directory { get; }
    public Utf8String? Name => String(Utf8Literals.Name);
    public Utf8String? Version => String(Utf8Literals.Version);
    public Utf8String? Type => String(Utf8Literals.Type);

    internal PackageJson WithDirectory(Utf8String directory) => directory == Directory ? this : new(directory, root, Parseable);

    private PackageJson(Utf8String directory, JsonElement root, bool parseable)
    {
        Directory = directory;
        this.root = root;
        Parseable = parseable;
    }

    public static PackageJson Parse(Utf8String directory, ReadOnlyMemory<byte> contents)
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

    public JsonElement Get(Utf8String name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : default;

    public Utf8String? String(Utf8String name) => Get(name) is { ValueKind: JsonValueKind.String } value ? JsonStrings.GetString(value) : (Utf8String?)null;

    public IEnumerable<(Utf8String Name, Utf8String Version, Utf8String Field)> Dependencies()
    {
        foreach (Utf8String field in new Utf8String[] { Utf8Literals.Dependencies, Utf8Literals.DevDependencies, Utf8Literals.PeerDependencies, Utf8Literals.OptionalDependencies })
        {
            JsonElement value = Get(field);
            if (value.ValueKind != JsonValueKind.Object)
                continue;
            var entries = Properties(value).ToArray();
            // Expected<map[string]string> rejects the entire field if any value has the wrong type.
            if (entries.Any(p => p.Value.ValueKind != JsonValueKind.String))
                continue;
            foreach (var entry in entries)
                yield return (JsonStrings.GetName(entry), JsonStrings.GetString(entry.Value), field);
        }
    }

    public bool HasDependency(Utf8String name) => Dependencies().Any(d => d.Name == name);

    public IReadOnlySet<Utf8String> RuntimeDependencies() => Dependencies().Where(d => d.Field != Utf8Literals.DevDependencies)
        .Select(d => d.Name).ToHashSet(Utf8StringComparer.Ordinal);

    public JsonElement VersionPaths(SemanticVersion compilerVersion)
    {
        JsonElement versions = Get(Utf8Literals.TypesVersions);
        if (versions.ValueKind != JsonValueKind.Object)
            return default;
        foreach (var entry in Properties(versions))
            if (VersionRange.Parse(JsonStrings.GetName(entry))?.Test(compilerVersion) == true)
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
        var entries = new Dictionary<Utf8String, JsonProperty>(Utf8StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
            entries[JsonStrings.GetName(entry)] = entry;
        return entries.Values;
    }
}

public sealed record PackageJsonEntry(Utf8String Directory, bool DirectoryExists, PackageJson? Contents);

/// <summary>Cache entries belong to one filesystem generation. Invalidating serializes with readers.</summary>
public sealed class PackageJsonCache(IFileSystem fileSystem, Utf8String currentDirectory)
{
    private readonly object gate = new();
    private readonly Dictionary<Utf8String, PackageJsonEntry> entries = new(
        fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);

    public PackageJsonEntry Get(Utf8String directory)
    {
        directory = CompilerPath.Resolve(currentDirectory, directory);
        if (directory.Length > CompilerPath.RootLength(directory))
            directory = directory.TrimEnd((byte)'/');
        lock (gate)
        {
            if (entries.TryGetValue(directory, out var existing))
                return existing with { Directory = directory, Contents = existing.Contents?.WithDirectory(directory) };
            bool exists = fileSystem.DirectoryExists(directory);
            byte[]? bytes = exists ? fileSystem.ReadFile(CompilerPath.Combine(directory, Utf8Literals.PackageJson)) : null;
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

    public PackageJson? Scope(Utf8String directory)
    {
        foreach (Utf8String path in Ancestors(CompilerPath.Resolve(currentDirectory, directory)))
        {
            if (Get(path).Contents is { } package)
                return package;
        }
        return null;
    }

    internal static IEnumerable<Utf8String> Ancestors(Utf8String directory)
    {
        while (true)
        {
            yield return directory;
            Utf8String parent = CompilerPath.DirectoryName(directory);
            if (parent == directory || directory.Length == 0)
                yield break;
            directory = parent;
        }
    }
}
