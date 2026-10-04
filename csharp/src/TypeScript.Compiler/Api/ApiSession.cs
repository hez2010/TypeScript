using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Api;

public sealed record ApiSessionOptions
{
    public bool UseBinaryResponses { get; init; }
}

/// <summary>Owns independent API snapshots and pins their resources for concurrent requests.</summary>
public sealed partial class ApiSession : IRpcHandler, IAsyncDisposable
{
    private static long nextId;
    private readonly object sync = new();
    private readonly ProjectSnapshotHost host;
    private readonly IFileSystem fileSystem;
    private readonly ApiSessionOptions options;
    private readonly Dictionary<ulong, ApiSnapshotData> snapshots = [];
    private readonly Dictionary<Utf8String, ReadOnlyMemory<byte>[]> batchPages = [];
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? disposal;
    private bool disposed;
    private int activeRequests;
    private long nextPage;
    public Utf8String Id { get; } = (Utf8String)"api-session-"u8 + Interlocked.Increment(ref nextId);
    public Utf8String CurrentDirectory => host.Options.CurrentDirectory;
    public bool CaseSensitive => fileSystem.CaseSensitive;

    public ApiSession(IFileSystem fileSystem, ProjectHostOptions? hostOptions = null, ApiSessionOptions? options = null)
    {
        this.fileSystem = fileSystem; host = new(fileSystem, hostOptions); this.options = options ?? new();
    }

    public ApiSession(ProjectSession projectSession, ApiSessionOptions? options = null)
    {
        this.projectSession = projectSession; host = projectSession.RetainHost();
        fileSystem = projectSession.FileSystem; this.options = options ?? new();
    }

    async ValueTask<RpcResponse> IRpcHandler.HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
    {
        if (method == "echo"u8)
        {
            using var invocation = Enter(cancellation);
            invocation.Cancellation.ThrowIfCancellationRequested();
            return new(parameters.IsEmpty && !options.UseBinaryResponses ? "null"u8.ToArray() : parameters.ToArray(), options.UseBinaryResponses);
        }
        if (method == "ping"u8) return await HandleRequestAsync(method, cancellation: cancellation).ConfigureAwait(false);
        JsonDocument? document;
        try { document = parameters.IsEmpty ? null : JsonDocument.Parse(parameters, new() { MaxDepth = int.MaxValue }); }
        catch (JsonException error) { throw new ApiException(error.Message, true, error); }
        using (document) return await HandleRequestAsync(method, document?.RootElement ?? default, cancellation).ConfigureAwait(false);
    }

    public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, JsonElement parameters = default, CancellationToken cancellation = default)
    {
        using var invocation = Enter(cancellation);
        cancellation = invocation.Cancellation;
        cancellation.ThrowIfCancellationRequested();
        if (method == "ping"u8) return RpcResponse.String("pong"u8);
        if (method == "echo"u8)
        {
            var response = RpcResponse.Json(writer => ApiJson.Write(writer, parameters));
            return response with { IsBinary = options.UseBinaryResponses };
        }
        if (method == "initialize"u8) return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteBoolean("useCaseSensitiveFileNames"u8, CaseSensitive);
            ApiJson.String(writer, "currentDirectory"u8, CurrentDirectory); writer.WriteEndObject();
        });
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new ApiException("Expected request parameters", true);
        if (ProfileRequest(method, parameters) is { } profileResponse) return profileResponse;
        if (method == "batchRequests"u8) return await BatchAsync(parameters, cancellation).ConfigureAwait(false);
        if (method == "printNode"u8) return PrintNode(parameters, cancellation);
        if (method == "formatNodeForInsertion"u8) return await FormatNodeForInsertionAsync(parameters, cancellation).ConfigureAwait(false);
        if (method == "release"u8)
        {
            ulong id = ApiJson.UInt64(parameters, "snapshot"u8);
            if (id == 0) throw new ApiException("empty handle");
            await ReleaseAsync(id).ConfigureAwait(false); return RpcResponse.Boolean(true);
        }
        if (method == "createSnapshot"u8) return await CreateSnapshotAsync(parameters, null, cancellation).ConfigureAwait(false);
        if (method == "updateSnapshot"u8)
        {
            await using var parent = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
            return await CreateSnapshotAsync(ApiJson.Get(parameters, "changes"u8), parent.Data, cancellation).ConfigureAwait(false);
        }
        if (method == "createSourceFile"u8 || method == "createSourceFileFromFile"u8)
        {
            var name = host.FileName(ApiJson.String(parameters, "fileName"u8));
            Utf8String? supplied = method == "createSourceFile"u8 ? ApiJson.String(parameters, "sourceText"u8)
                : fileSystem is ICompilerSourceProvider provider ? provider.ReadSource(name)?.Text.Text
                : fileSystem.ReadFile(name) is { } bytes ? new Utf8String(SourceEncoding.DecodeBytes(bytes)) : null;
            var text = supplied ?? throw new ApiException($"could not read file \"{name}\"");
            var kind = (ScriptKind)ApiJson.Int32(ApiJson.Get(parameters, "options"u8), "scriptKind"u8);
            if (kind == ScriptKind.Unknown) { kind = DocumentSnapshot.InferKind(name); if (kind == ScriptKind.Unknown) kind = ScriptKind.TS; }
            if (kind is not (ScriptKind.JS or ScriptKind.JSX or ScriptKind.TS or ScriptKind.TSX or ScriptKind.JSON))
                throw new ApiException($"invalid scriptKind {(int)kind}");
            var parseOptions = new ParseOptions(name, kind);
            var source = await Parser.ParseSourceFileAsync(parseOptions, new(text), cancellation).ConfigureAwait(false);
            return await EncodeAsync(source, new() { Path = host.Path(name), ParseOptions = parseOptions }, cancellation).ConfigureAwait(false);
        }
        return await LanguageServiceRequestAsync(method, parameters, cancellation).ConfigureAwait(false)
            ?? await CheckerRequestAsync(method, parameters, cancellation).ConfigureAwait(false)
            ?? await DiagnosticRequestAsync(method, parameters, cancellation).ConfigureAwait(false)
            ?? await EmissionRequestAsync(method, parameters, cancellation).ConfigureAwait(false)
            ?? ConfigurationRequest(method, parameters, cancellation)
            ?? await ResolutionRequestAsync(method, parameters, cancellation).ConfigureAwait(false)
            ?? await SnapshotRequestAsync(method, parameters, cancellation).ConfigureAwait(false);
    }

    public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters = default, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        return default;
    }
    private Invocation Enter(CancellationToken cancellation)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); activeRequests++;
            return new(this, CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token));
        }
    }
    private sealed class Invocation(ApiSession session, CancellationTokenSource source) : IDisposable
    {
        internal CancellationToken Cancellation => source.Token;
        public void Dispose()
        {
            source.Dispose();
            lock (session.sync) if (--session.activeRequests == 0 && session.disposed) session.drained.TrySetResult();
        }
    }
    private sealed class SnapshotPin(ApiSnapshotData data, ProjectWorkspaceSnapshot.Lease lease) : IAsyncDisposable
    {
        internal ApiSnapshotData Data { get; } = data;
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
    private SnapshotPin Pin(ulong id)
    {
        lock (sync)
            return snapshots.TryGetValue(id, out var data) ? new(data, data.Snapshot.Acquire())
                : throw new ApiException($"snapshot {id} not found");
    }
    private async ValueTask RegisterAsync(ApiSnapshotData data)
    {
        bool release = false;
        lock (sync)
        {
            if (disposed) release = true;
            else if (snapshots.TryGetValue(data.Snapshot.Id, out var previous)) { previous.References++; release = true; }
            else snapshots.Add(data.Snapshot.Id, data);
        }
        if (release) await data.Ownership.DisposeAsync().ConfigureAwait(false);
        if (disposed) throw new ObjectDisposedException(nameof(ApiSession));
    }
    private async ValueTask ReleaseAsync(ulong id)
    {
        ApiSnapshotData? release = null;
        lock (sync)
        {
            if (!snapshots.TryGetValue(id, out var data)) throw new ApiException($"snapshot {id} not found");
            if (--data.References == 0) { snapshots.Remove(id); release = data; }
        }
        if (release is not null) await release.Ownership.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            if (activeRequests == 0) drained.TrySetResult();
            // Start outside the caller's stack so callbacks cannot run while the map lock is held.
            disposal = Task.Run(DisposeCoreAsync);
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await drained.Task.ConfigureAwait(false);
        profiler.Dispose();
        ApiSnapshotData[] released;
        lock (sync) { released = snapshots.Values.ToArray(); snapshots.Clear(); batchPages.Clear(); }
        List<Exception>? failures = null;
        foreach (var snapshot in released)
            try { await snapshot.Ownership.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        try { await ReleaseLanguageServerReferencesAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try
        {
            if (projectSession is null) await host.DisposeAsync().ConfigureAwait(false);
            else await host.ReleaseAsync().ConfigureAwait(false);
        }
        finally { shutdown.Dispose(); languageServerUpdate.Dispose(); }
        if (failures is not null) throw new AggregateException(failures);
    }

    private async ValueTask<RpcResponse> EncodeAsync(Ast.SyntaxNode? root, AstEncodingOptions encoding, CancellationToken cancellation)
    {
        if (root is null) return options.UseBinaryResponses ? new(ReadOnlyMemory<byte>.Empty, true) : RpcResponse.Null;
        var encoded = await AstEncoder.EncodeAsync(root, options: encoding, cancellation: cancellation).ConfigureAwait(false);
        return options.UseBinaryResponses ? new(encoded.Bytes, true) : RpcResponse.Json(writer =>
        { writer.WriteStartObject(); writer.WriteBase64String("data"u8, encoded.Bytes); writer.WriteEndObject(); });
    }

    private async ValueTask<RpcResponse> SnapshotRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (method == "getCurrentLanguageServerSnapshot"u8)
            return await GetLanguageServerSnapshotAsync(parameters, cancellation).ConfigureAwait(false);
        if (method != "getDefaultProjectForFile"u8 && method != "getSourceFile"u8 && method != "getSourceFileNames"u8)
            throw new ApiException($"unknown API method \"{method}\"", true);
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        if (method == "getDefaultProjectForFile"u8)
        {
            var project = data.Snapshot.GetDefaultProject(Document(ApiJson.Get(parameters, "file"u8)));
            return project is null ? RpcResponse.Null : RpcResponse.Json(writer => WriteProject(writer, project));
        }
        var program = data.Program(ApiJson.String(parameters, "project"u8));
        if (method == "getSourceFileNames"u8) return RpcResponse.Json(writer => ApiJson.Strings(writer, program.SourceFiles.Select(file => file.Syntax.FileName)));
        var file = program.GetFile(Document(ApiJson.Get(parameters, "file"u8), program));
        return await EncodeAsync(file?.Syntax, new() { Path = file is null ? default : host.Path(file.Syntax.FileName),
            ParseOptions = file?.ParseOptions ?? default, Mapping = file?.Mapping,
            SupplementalSourceFiles = file?.SupplementalSourceFiles ?? [] }, cancellation).ConfigureAwait(false);
    }
}
