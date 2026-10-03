using System.Collections.Concurrent;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Projects.TypeAcquisition;

public readonly record struct NpmResult(Utf8String Output, int ExitCode);
public interface INpmExecutor
{
    ValueTask<NpmResult> InstallAsync(Utf8String directory, IReadOnlyList<Utf8String> arguments, CancellationToken cancellation);
}
public sealed record TypingsInstallRequest(TypeAcquisitionOptions Acquisition, CompilerOptions CompilerOptions,
    IReadOnlyList<Utf8String> FileNames, Utf8String ProjectRoot, IReadOnlyList<Utf8String> UnresolvedImports, IFileSystem FileSystem);
public sealed record TypingsInstallResult(Utf8String[] Files, Utf8String[] FilesToWatch);

/// <summary>Owns the global typings cache and a bounded set of npm installs shared by editor projects.</summary>
public sealed class TypingsInstaller : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly IFileSystem fs;
    private readonly INpmExecutor npm;
    private readonly Utf8String location;
    private readonly Action<Utf8String>? log;
    private readonly SemaphoreSlim slots;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Utf8String, CachedTyping> cache = new(Utf8StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Utf8String, bool> missing = new(Utf8StringComparer.Ordinal);
    private IReadOnlyDictionary<Utf8String, IReadOnlyDictionary<Utf8String, Utf8String>> registry = new Dictionary<Utf8String, IReadOnlyDictionary<Utf8String, Utf8String>>();
    private readonly HashSet<Task> active = [];
    private Task? initialization;
    private bool disposed;

    public TypingsInstaller(IFileSystem fileSystem, Utf8String location, INpmExecutor npm, int concurrency = 5, Action<Utf8String>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        fs = fileSystem; this.location = location; this.npm = npm; this.log = log;
        slots = new(concurrency, concurrency);
    }

    public async ValueTask<bool> IsKnownPackageAsync(Utf8String name, CancellationToken cancellation = default)
    {
        if (TypingsDiscovery.ValidatePackageName(name).Result != PackageNameValidationResult.Ok) return false;
        await InitializeAsync(cancellation).ConfigureAwait(false);
        return registry.ContainsKey(name);
    }

    public ValueTask<TypingsInstallResult> InstallAsync(TypingsInstallRequest request, CancellationToken cancellation = default)
    {
        var completion = new TaskCompletionSource<TypingsInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active.Add(completion.Task); }
        _ = CompleteAsync();
        return new(completion.Task);

        async Task CompleteAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            try { completion.TrySetResult(await InstallCoreAsync(request, linked.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { lock (sync) active.Remove(completion.Task); }
        }
    }

    private async Task<TypingsInstallResult> InstallCoreAsync(TypingsInstallRequest request, CancellationToken cancellation)
    {
        await InitializeAsync(cancellation).ConfigureAwait(false);
        var discovered = TypingsDiscovery.Discover(request.FileSystem, request.Acquisition, request.CompilerOptions,
            request.FileNames, request.ProjectRoot, request.UnresolvedImports, cache, registry, cancellation);
        var filtered = new List<Utf8String>();
        foreach (var name in discovered.NewNames)
        {
            var key = Mangle(name);
            if (missing.ContainsKey(key)) continue;
            if (TypingsDiscovery.ValidatePackageName(name).Result != PackageNameValidationResult.Ok) { missing[key] = true; continue; }
            if (!registry.TryGetValue(key, out var versions) || cache.TryGetValue(key, out var typing) && TypingsDiscovery.IsCurrent(typing, versions)) continue;
            filtered.Add(key);
        }
        var files = discovered.CachedFiles.ToList();
        if (filtered.Count != 0)
        {
            var packages = filtered.Select(name => "@types/"u8 + name + "@latest"u8).ToArray();
            try
            {
                await InstallPackagesAsync(packages, slots, async (batch, token) =>
                {
                    Utf8String[] arguments = ["install"u8, "--ignore-scripts"u8, .. batch, "--save-dev"u8,
                        "--user-agent=\"typesInstaller/"u8 + BuildInfo.CompilerVersion + "\""u8];
                    var result = await npm.InstallAsync(location, arguments, token).ConfigureAwait(false);
                    if (result.ExitCode != 0) throw new IOException($"npm install failed ({result.ExitCode}): {result.Output}");
                }, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch
            {
                foreach (var name in filtered) missing[name] = true;
                throw;
            }
            var resolver = CreateResolver();
            foreach (var name in filtered)
            {
                Utf8String fileName = await ResolveAsync(resolver, name, cancellation).ConfigureAwait(false);
                if (fileName.IsEmpty) { missing[name] = true; continue; }
                var tags = registry[name];
                var versionText = tags.GetValueOrDefault(TypingsDiscovery.VersionTag, tags.GetValueOrDefault("latest"u8));
                var version = SemanticVersion.Parse(versionText) ?? throw new InvalidDataException($"Invalid typings version: {versionText}");
                cache[name] = new(fileName, version); files.Add(fileName);
            }
        }
        return new(files.Order(Utf8StringComparer.Ordinal).ToArray(), discovered.FilesToWatch.Order(Utf8StringComparer.Ordinal).ToArray());
    }

    internal static Utf8String Mangle(Utf8String name) => name.StartsWith("@"u8) && name.IndexOf((byte)'/') is > 0 and var slash
        ? name[1..slash] + "__"u8 + name[(slash + 1)..] : name;

    public static async Task InstallPackagesAsync(IReadOnlyList<Utf8String> packages, SemaphoreSlim slots,
        Func<IReadOnlyList<Utf8String>, CancellationToken, ValueTask> install, CancellationToken cancellation = default)
    {
        var pending = new List<Task>();
        int start = 0, size = 100;
        Exception? schedulingError = null;
        try
        {
            for (int index = 0; index < packages.Count; index++)
            {
                size += packages[index].Length + 1;
                if (size < 8000) continue;
                await QueueAsync(start, index).ConfigureAwait(false);
                start = index; size = 100 + packages[index].Length + 1;
            }
            if (start < packages.Count) await QueueAsync(start, packages.Count).ConfigureAwait(false);
        }
        catch (Exception error) { schedulingError = error; }
        await Task.WhenAll(pending).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (schedulingError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(schedulingError).Throw();

        async ValueTask QueueAsync(int first, int end)
        {
            await slots.WaitAsync(cancellation).ConfigureAwait(false);
            pending.Add(RunAsync(Enumerable.Range(first, end - first).Select(index => packages[index]).ToArray()));
        }
        async Task RunAsync(Utf8String[] batch)
        {
            try { await install(batch, cancellation).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellation)
    {
        TaskCompletionSource? completion = null;
        Task task;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialization is null) initialization = (completion = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            task = initialization;
        }
        if (completion is not null) _ = CompleteInitializationAsync(completion);
        await task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private async Task CompleteInitializationAsync(TaskCompletionSource completion)
    {
        try
        {
            await ReadCacheAsync(lifetime.Token).ConfigureAwait(false);
            var manifest = CompilerPath.Combine(location, "package.json"u8);
            if (!fs.FileExists(manifest))
                try { fs.WriteFile(manifest, "{ \"private\": true }"u8); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log?.Invoke(Utf8String.FromString(error.Message)); }
            try
            {
                var result = await npm.InstallAsync(location, ["install"u8, "--ignore-scripts"u8, "types-registry@latest"u8], lifetime.Token).ConfigureAwait(false);
                if (result.ExitCode != 0) log?.Invoke(result.Output);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { log?.Invoke(Utf8String.FromString(error.Message)); }
            var entries = new Dictionary<Utf8String, IReadOnlyDictionary<Utf8String, Utf8String>>(Utf8StringComparer.Ordinal);
            if (ReadJson(CompilerPath.Combine(location, "node_modules/types-registry/index.json"u8)) is { } registryFile
                && registryFile.TryGetProperty("entries", out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var entry in values.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var versions = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
                    foreach (var version in entry.Value.EnumerateObject())
                        if (version.Value.ValueKind == JsonValueKind.String) versions[JsonStrings.GetName(version)] = JsonStrings.GetString(version.Value);
                    entries[JsonStrings.GetName(entry)] = versions;
                }
            registry = entries;
            completion.TrySetResult();
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task ReadCacheAsync(CancellationToken cancellation)
    {
        var manifest = ReadJson(CompilerPath.Combine(location, "package.json"u8));
        var packageLock = ReadJson(CompilerPath.Combine(location, "package-lock.json"u8));
        if (manifest is not { } config || packageLock is not { } locked
            || !config.TryGetProperty("devDependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object) return;
        var resolver = CreateResolver();
        foreach (var dependency in dependencies.EnumerateObject())
        {
            cancellation.ThrowIfCancellationRequested();
            var name = JsonStrings.GetName(dependency);
            JsonElement entry = default;
            bool found = locked.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object
                && packages.TryGetProperty("node_modules/"u8 + name, out entry);
            if (!found) found = locked.TryGetProperty("dependencies", out var legacy) && legacy.ValueKind == JsonValueKind.Object
                && legacy.TryGetProperty(name, out entry);
            if (!found || entry.ValueKind != JsonValueKind.Object) continue;
            name = CompilerPath.BaseName(name);
            var fileName = await ResolveAsync(resolver, name, cancellation).ConfigureAwait(false);
            if (fileName.IsEmpty) { missing[name] = true; continue; }
            if (entry.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && SemanticVersion.Parse(JsonStrings.GetString(version)) is { } parsed) cache[name] = new(fileName, parsed);
        }
    }

    private JsonElement? ReadJson(Utf8String path)
    {
        if (fs.ReadFile(path) is not { } bytes) return null;
        try { using var json = JsonDocument.Parse(bytes); return json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.Clone() : null; }
        catch (JsonException) { return null; }
    }
    private ModuleResolver CreateResolver()
    {
        var options = new CompilerOptions(); options.SetRaw("moduleResolution"u8, "\"nodenext\""u8);
        return new(fs, options, location);
    }
    private async ValueTask<Utf8String> ResolveAsync(ModuleResolver resolver, Utf8String name, CancellationToken cancellation) =>
        (await resolver.ResolveAsync(name, CompilerPath.Combine(location, "index.d.ts"u8), cancellation: cancellation).ConfigureAwait(false)).FileName;

    public async ValueTask DisposeAsync()
    {
        Task[] requests;
        lock (sync)
        {
            if (disposed) return;
            disposed = true; requests = initialization is null ? active.ToArray() : [.. active, initialization];
        }
        lifetime.Cancel();
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        catch (Exception) { /* Each request retains its own failure; disposal joins all owned work. */ }
        lifetime.Dispose(); slots.Dispose();
    }
}
