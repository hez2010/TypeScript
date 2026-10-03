using System.Collections.Concurrent;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects.TypeAcquisition;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal sealed class FixtureNpm : INpmExecutor
{
    private readonly MemoryFileSystem fs;
    private readonly Utf8String registry;
    private readonly Dictionary<Utf8String, Utf8String> packages;
    internal readonly ConcurrentQueue<(Utf8String Directory, Utf8String[] Arguments)> Calls = new();

    internal FixtureNpm(MemoryFileSystem fs, JsonElement specification)
    {
        this.fs = fs; registry = JsonStrings.GetString(specification.GetProperty("registry"));
        packages = specification.TryGetProperty("packages", out var values) && values.ValueKind == JsonValueKind.Object
            ? values.EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value)) : [];
    }

    public ValueTask<NpmResult> InstallAsync(Utf8String directory, IReadOnlyList<Utf8String> arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Calls.Enqueue((directory, arguments.ToArray()));
        if (arguments.Count < 3 || arguments[0] != "install"u8 || arguments[1] != "--ignore-scripts"u8)
            return new(new NpmResult("Unexpected npm arguments"u8, 1));
        if (arguments.Count == 3 && arguments[2] == "types-registry@latest"u8)
        {
            fs.WriteFile(CompilerPath.Combine(directory, "node_modules/types-registry/index.json"u8), registry.Span);
            return new(new NpmResult(default, 0));
        }
        foreach (var argument in arguments.Skip(2).TakeWhile(argument => !argument.StartsWith("--"u8)))
        {
            if (!argument.StartsWith("@types/"u8)) return new(new NpmResult("Unexpected package argument"u8, 1));
            int version = argument.LastIndexOf((byte)'@');
            var name = argument["@types/"u8.Length..(version > 0 ? version : argument.Length)];
            if (!packages.TryGetValue(name, out var contents)) return new(new NpmResult("No package contents in fixture"u8, 1));
            fs.WriteFile(CompilerPath.Combine(directory, "node_modules/@types"u8, name, "index.d.ts"u8), contents.Span);
        }
        return new(new NpmResult(default, 0));
    }
}
